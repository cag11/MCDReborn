"""
Weapon mastery, in the game: a star with the level on every weapon's tile, star pictures on the
Mastery lines under a weapon's name, and a screen (J) listing every weapon with its level, progress
and bonus.

The levels themselves are property lines on the weapons (MCDR_MasteryMelee01..25,
MCDR_MasteryRanged01..25, MCDR_MasteryXp0..9 - see MCDSaveEdit/Logic/Mastery.cs); the item plugin
earns them and applies the bonuses. Where those ids start depends on what else is registered, so the
screen finds out at run time: the first EArmorPropertyID value whose name is MCDR_MasteryMelee01.

    /Game/MCDReborn/UI/Mastery/T_Star0..5          plain, bronze, silver, gold, platinum, full platinum
    /Game/MCDReborn/UI/UMG_MCDRebornMasteryBadge   the star on a tile
    /Game/MCDReborn/UI/UMG_MCDRebornMastery        the screen and the passes
    /Game/MCDReborn/Actors/BP_MCDRebornMastery, Lobby/Mastery, Ingame/Mastery

    UE4Editor-Cmd.exe <project>.uproject -run=Py <this file> -unattended -nopause -nosplash

Delete the widgets, actor and levels first to rebuild them.
"""

import os
import sys

HERE = os.environ.get('MCDREBORN_LOADER', r'C:/Users/Gaming/RiderProjects/MCDSaveEditReborn/Tools/loader')
sys.path.insert(0, HERE)

import unreal_engine as ue
import mcd_ui
from mcd_ui import (say, keep, on_disk, pin, link, set_default, event, put, place, label, fill,
                    stub_font, INK, GOLD, DIM, SHADOW, TITLE_FONT, TITLE_FACE, BODY_FONT, BODY_FACE)
import build_gems
from build_gems import (Graph, typed_variable, image, CLEAR, DEBOUNCE, SHOWN, COLLAPSED, LOOKS,
                        for_range, looping, unless_holding, told)

from unreal_engine.classes import (
    Actor,
    Button,
    CanvasPanel,
    CanvasPanelSlot,
    DungeonsImage,
    GameplayStatics,
    Image,
    InventoryItem,
    InventoryItemSlot,
    ItemStashComponent,
    BlueprintMapLibrary,
    KismetMathLibrary,
    KismetNodeHelperLibrary,
    KismetStringLibrary,
    KismetSystemLibrary,
    KismetTextLibrary,
    PanelWidget,
    PlayerCharacter,
    PlayerController,
    TextBlock,
    Texture2D,
    UserWidget,
    WalletComponent,
    Widget,
    WidgetBlueprintFactory,
    WidgetBlueprintLibrary,
    WorldFactory,
    K2Node_Self,
)
from unreal_engine.structs import LinearColor, Vector2D, WidgetTransform

WIDGET = '/Game/MCDReborn/UI/UMG_MCDRebornMastery'
BADGE = '/Game/MCDReborn/UI/UMG_MCDRebornMasteryBadge'
ACTOR = '/Game/MCDReborn/Actors/BP_MCDRebornMastery'
LEVELS = ['/Game/MCDReborn/Lobby/Mastery', '/Game/MCDReborn/Ingame/Mastery']
ICONS = '/Game/MCDReborn/UI/Mastery'
PICTURES = os.path.join(HERE, 'mastery')

KEY = 'J'
LEVELS_MAX = 25
STEPS = 10
FIRST_NAME = 'MCDR_MasteryMelee01'       # the first mastery id's name; the rest follow it
TIER_WORDS = ['(Full Platinum)', '(Platinum)', '(Gold)', '(Silver)', '(Bronze)']   # tiers 5..1, as the lines say them
DAMAGE_PER_LEVEL, SPEED_PER_TIER, ROLL_PER_TIER = 2, 4, 5
SECONDS_BASE, SECONDS_STEP = 60, 30      # seconds of use from level n to n+1: BASE + STEP * n (as Mastery.cs)
CURRENCY = 'MCDR_Mastery_'              # + a family's key: the character's seconds of use of that kind of weapon

#EItemTag
MELEE_TAG, RANGED_TAG = 10, 11

ROWS = 14                                # weapons a page of the screen shows
ROW_H = 58.0
PANEL_W, PANEL_H = 1500.0, 1000.0
BACK = LinearColor(R=0.0060, G=0.0070, B=0.0110, A=0.965)
ROW_BACK = LinearColor(R=0.0319, G=0.0319, B=0.0319, A=1.0)
BAR_BACK = LinearColor(R=0.10, G=0.10, B=0.12, A=1.0)
BAR_FILL = LinearColor(R=0.9131, G=0.6724, B=0.1590, A=1.0)


COUNTS = {}
PLUGIN_LIST = r'C:/Program Files (x86)/Steam/steamapps/common/MinecraftDungeons/Dungeons/Binaries/Win64/MCDRebornItems.txt'


def pretty(key):
    """A family's id as words: DoubleAxe -> Double Axe."""
    out = ''
    for i, c in enumerate(key):
        if i and c.isupper() and not key[i - 1].isupper():
            out += ' '
        out += c
    return out


def read_families():
    """
    (family keys, their names, this app's item ids, the family each is in), from the plugin's list -
    the same families the plugin shares mastery by.
    """
    keys, names, kinds, custom_ids, custom_families = [], [], [], [], []
    try:
        lines = open(PLUGIN_LIST, encoding='utf-8').read().splitlines()
    except Exception as problem:
        say('no plugin list (%s): families by id only' % problem)
        lines = []
    for line in lines:
        if not line.startswith('@masteryfamily\t'):
            continue
        _, kind, family, ids = line.split('\t', 3)
        keys.append(family)
        names.append(pretty(family))
        kinds.append(kind)
        for one in ids.split(','):
            if one.startswith('MCDR_'):
                custom_ids.append(one)
                custom_families.append(family)
    say('families: %d, %d of them with items of this app' % (len(keys), len(set(custom_families))))
    return keys, names, kinds, custom_ids, custom_families


def import_pictures():
    found = []
    for k in range(6):
        name = 'T_Star%d' % k
        path = '%s/%s' % (ICONS, name)
        try:
            there = ue.load_object(ue.find_class('Texture2D'), '%s.%s' % (path, name))
        except Exception:
            there = None
        if there is None:
            made = ue.import_asset(os.path.join(PICTURES, name + '.png'), ICONS)
            there = made[0] if isinstance(made, list) else made
            for field, value in (('Filter', 0), ('MipGenSettings', 13), ('CompressionSettings', 7), ('NeverStream', True)):
                try:
                    setattr(there, field, value)
                except Exception as problem:
                    say('  %s.%s not set: %s' % (name, field, problem))
            keep(there)
        found.append(there)
    return found


def stars_variable(widget, stars):
    typed_variable(widget, 'Stars', '(%s)' % ','.join(s.get_path_name() for s in stars),
                   PinCategory='object', PinSubCategoryObject=Texture2D, ContainerType=1)


def tier_of(g, level):
    """0 plain .. 5 full platinum, from a level."""
    return g.call(KismetMathLibrary.Min, A=g.math('Divide_IntInt', level, 5), B=5)


def read_levels(g, item, base):
    """
    Lvl, Step, Ranged from an item's lines: Lvl the mastery level (0 for none), Step the tenths
    towards the next, Ranged whether the level line is a bow's.
    """
    g.setter('Lvl', 0)
    g.setter('Step', 0)
    g.setter('Ranged', 'false')
    loop = g.for_each(g.props(item()))
    g.then(loop, 'Exec', 'Completed')
    after = list(g.loose)
    g.loose = [(loop, 'LoopBody')]
    entry = g.breaks('ArmorPropertyData', (loop, 'Array Element'))
    line_id = g.call(KismetMathLibrary.Conv_ByteToInt, InByte=(entry, 'ID'))
    offset = g.math('Subtract_IntInt', line_id, base())
    between = lambda low, high: g.both(g.math('GreaterEqual_IntInt', offset, low), g.math('LessEqual_IntInt', offset, high))
    g.either(between(0, LEVELS_MAX - 1), lambda: g.setter('Lvl', g.math('Add_IntInt', offset, 1)), lambda: None)
    g.either(between(LEVELS_MAX, 2 * LEVELS_MAX - 1),
             lambda: (g.setter('Lvl', g.math('Subtract_IntInt', offset, LEVELS_MAX - 1)), g.setter('Ranged', 'true')), lambda: None)
    g.either(between(2 * LEVELS_MAX, 2 * LEVELS_MAX + STEPS - 1),
             lambda: g.setter('Step', g.math('Subtract_IntInt', offset, 2 * LEVELS_MAX)), lambda: None)
    g.loose = after


def level_variables(widget):
    for name, kind, default in (('Lvl', 'int', '0'), ('Step', 'int', '0'), ('Ranged', 'bool', 'false'), ('Base', 'int', '-1')):
        ue.blueprint_add_member_variable(widget, name, kind, False, default)


def family_currency(g, k):
    """The name of family k's currency: MCDR_Mastery_<key>."""
    return g.call(KismetStringLibrary.Conv_StringToName, InString=g.call(
        KismetStringLibrary.Concat_StrStr, A=CURRENCY, B=g.element(g.get('FamilyKeys'), k)))


def level_of(g, seconds):
    """Lvl and Step from seconds of use, as the plugin reckons them."""
    g.setter('Rem', seconds)
    g.setter('Lvl', 0)

    def one(n):
        need = g.math('Add_IntInt', SECONDS_BASE, g.math('Multiply_IntInt', n, SECONDS_STEP))
        g.either(g.both(g.math('EqualEqual_IntInt', g.get('Lvl'), n), g.math('GreaterEqual_IntInt', g.get('Rem'), need)),
                 lambda: (g.setter('Rem', g.math('Subtract_IntInt', g.get('Rem'), need)),
                          g.setter('Lvl', g.math('Add_IntInt', g.get('Lvl'), 1))), lambda: None)

    looping(g, for_range(g, 0, LEVELS_MAX - 1), one)
    need_now = g.math('Add_IntInt', SECONDS_BASE, g.math('Multiply_IntInt', g.get('Lvl'), SECONDS_STEP))
    g.setter('Step', g.call(KismetMathLibrary.Min, A=g.math('Divide_IntInt', g.math('Multiply_IntInt', g.get('Rem'), STEPS), need_now),
                            B=STEPS - 1))


def family_key(g, item, custom_count):
    """Key = the family of an item: its id up to the first underscore, this app's items by their source."""
    ids = g.breaks('InventoryItemData', g.get('Item', InventoryItem, of=item()))
    sid = g.breaks('SerializableItemId', (ids, 'ItemId'))
    g.setter('Key', g.call(KismetStringLibrary.Conv_NameToString, InName=(sid, 'SerializedId')))
    if custom_count:
        looping(g, for_range(g, 0, custom_count - 1), lambda k: g.either(
            g.call(KismetStringLibrary.EqualEqual_StrStr, A=g.get('Key'), B=g.element(g.get('CustomIds'), k)),
            lambda: g.setter('Key', g.element(g.get('CustomFamilies'), k)), lambda: None))
    cut = g.call(KismetStringLibrary.Split, SourceString=g.get('Key'), InStr='_')
    g.setter('Key', g.call(KismetMathLibrary.SelectString, A=(cut[0], 'LeftS'), B=g.get('Key'), bPickA=cut))


#--- the tile badge ------------------------------------------------------------------------------------

def build_badge(stars, tile_class):
    there = on_disk(BADGE, 'WidgetBlueprint')
    if there is not None:
        say('badge already there: ' + BADGE)
        return there
    factory = WidgetBlueprintFactory()
    factory.ParentClass = UserWidget
    widget = factory.factory_create_new(BADGE)
    widget.modify()
    wt = widget.WidgetTree
    wt.RootWidget.Visibility = 3                    # HitTestInvisible: the tile keeps its clicks
    body_face = stub_font(BODY_FONT)
    #Top left of the tile: the sockets are top right.
    build_gems.anchored(wt.RootWidget, image(wt, 'Star', stars[0], 26.0, variable=True), 0.0, 0.0, 4.0, 4.0)
    build_gems.anchored(wt.RootWidget, label(wt, 'Level', '1', INK, body_face, 15, variable=True, typeface=BODY_FACE, outline=2),
                        0.0, 0.0, 30.0, 6.0)

    typed_variable(widget, 'Tile', PinCategory='object', PinSubCategoryObject=tile_class)
    stars_variable(widget, stars)
    level_variables(widget)
    ue.blueprint_mark_as_structurally_modified(widget)
    ue.compile_blueprint(widget)

    g = Graph(widget)
    tick = event(widget, UserWidget, 'Tick', 0, 0)
    g.loose = [(tick, 'then')]
    g.setter('Lvl', 0)
    slot = lambda: g.get('InventoryItemSlot', tile_class, of=g.get('Tile'))
    item = lambda: g.get('Item', InventoryItemSlot, of=slot())
    ready = g.both(g.math('GreaterEqual_IntInt', g.get('Base'), 0), g.valid(g.get('Tile')))
    g.either(ready, lambda: g.either(g.valid(slot()), lambda: g.either(g.valid(item()),
             lambda: read_levels(g, item, lambda: g.get('Base')), lambda: None), lambda: None), lambda: None)

    def show():
        g.run(Image.SetBrushFromTexture, self=g.get('Star'), Texture=g.element(g.get('Stars'), tier_of(g, g.get('Lvl'))),
              bMatchSize='false')
        g.run(TextBlock.SetText, self=g.get('Level'), InText=g.call(KismetTextLibrary.Conv_IntToText, Value=g.get('Lvl')))
        g.shows('Star', LOOKS)
        g.shows('Level', LOOKS)

    g.either(g.math('Greater_IntInt', g.get('Lvl'), 0), show,
             lambda: (g.shows('Star', COLLAPSED), g.shows('Level', COLLAPSED)))
    ue.compile_blueprint(widget)
    keep(widget)
    say('badge built: ' + BADGE)
    return widget


#--- the screen ----------------------------------------------------------------------------------------

def build_tree(widget, stars):
    widget.modify()
    wt = widget.WidgetTree
    title_face = stub_font(TITLE_FONT)
    body_face = stub_font(BODY_FONT)
    wt.RootWidget.Visibility = 4

    left, top = (1920.0 - PANEL_W) / 2.0, (1080.0 - PANEL_H) / 2.0
    panel = CanvasPanel('Panel', wt)
    panel.bIsVariable = True
    panel.Visibility = 1
    place(wt.RootWidget, panel, left, top, PANEL_W, PANEL_H)
    put(panel, fill(wt, 'Backing', BACK), 0.0, 0.0, PANEL_W, PANEL_H)
    put(panel, label(wt, 'Title', 'WEAPON MASTERY', GOLD, title_face, 32, typeface=TITLE_FACE, shadow=SHADOW),
        40.0, 22.0, 700.0, 48.0)
    close = mcd_ui.button(wt, 'Close', 'CLOSE', body_face, 18, typeface=BODY_FACE, centred=True)
    close.ToolTipText = 'Close (J or Esc).'
    put(panel, close, PANEL_W - 190.0, 26.0, 150.0, 42.0)
    for text, x in (('Level', 40.0), ('Weapon type', 150.0), ('Progress', 700.0), ('Bonus', 1060.0)):
        put(panel, label(wt, 'Head' + text, text, DIM, body_face, 16, typeface=BODY_FACE), x, 92.0, 300.0, 24.0)

    #The rows, in order: row r is child r of Rows, and each row's parts are its children in the
    #same order in every row - 0 star, 1 level, 2 name, 3 bar back, 4 bar, 5 progress, 6 bonus.
    rows = CanvasPanel('Rows', wt)
    rows.bIsVariable = True
    rows.Visibility = 4
    put(panel, rows, 30.0, 122.0, PANEL_W - 60.0, ROWS * ROW_H)
    for r in range(ROWS):
        row = CanvasPanel('Row%02d' % r, wt)
        row.Visibility = 1
        put(rows, row, 0.0, r * ROW_H, PANEL_W - 60.0, ROW_H - 6.0)
        put(row, image(wt, 'RStar%02d' % r, stars[0], 40.0), 10.0, 6.0, 40.0, 40.0)
        put(row, label(wt, 'RLevel%02d' % r, '0', INK, body_face, 22, typeface=BODY_FACE), 60.0, 11.0, 60.0, 30.0)
        put(row, label(wt, 'RName%02d' % r, 'Weapon', INK, body_face, 20, typeface=BODY_FACE), 120.0, 12.0, 540.0, 30.0)
        put(row, fill(wt, 'RBarBack%02d' % r, BAR_BACK), 670.0, 18.0, 280.0, 16.0)
        bar = fill(wt, 'RBar%02d' % r, BAR_FILL)
        bar.RenderTransformPivot = Vector2D(X=0.0, Y=0.5)
        put(row, bar, 670.0, 18.0, 280.0, 16.0)
        put(row, label(wt, 'RStep%02d' % r, '0%', DIM, body_face, 18, typeface=BODY_FACE), 960.0, 13.0, 80.0, 28.0)
        put(row, label(wt, 'RBonus%02d' % r, '', INK, body_face, 17, typeface=BODY_FACE), 1030.0, 13.0, 420.0, 28.0)

    put(panel, label(wt, 'Empty', 'No weapons yet.', DIM, body_face, 20, variable=True, typeface=BODY_FACE),
        40.0, 140.0, 800.0, 30.0)
    foot = PANEL_H - 70.0
    for name, caption, x in (('Prev', '<', 40.0), ('Next', '>', 170.0)):
        button = mcd_ui.button(wt, name, caption, body_face, 20, typeface=BODY_FACE, centred=True)
        put(panel, button, x, foot, 110.0, 44.0)
    put(panel, label(wt, 'PageText', 'Page 1 of 1', DIM, body_face, 18, variable=True, typeface=BODY_FACE), 300.0, foot + 10.0, 300.0, 28.0)
    put(panel, label(wt, 'Hint', 'Mastery belongs to you, not the weapon: +2% damage a level, and every five levels a star.',
                     DIM, body_face, 15, typeface=BODY_FACE), 560.0, foot + 12.0, PANEL_W - 600.0, 26.0)


def variables(widget, stars):
    for name, kind, default in (('Busy', 'int', '0'), ('Open', 'bool', 'false'), ('Fresh', 'int', '0'), ('Scan', 'int', '0'),
                                ('Scan2', 'int', '0'), ('Page', 'int', '0'), ('W', 'int', '0'), ('Line', 'string', ''),
                                ('Tier', 'int', '0'), ('Has', 'bool', 'false'), ('Looked', 'bool', 'false'),
                                ('Equipped', 'bool', 'false'), ('Key', 'string', ''), ('Seen', 'string', ''),
                                ('Label', 'string', ''), ('Rem', 'int', '0'), ('Scan3', 'int', '1000'),
                                ('EquippedKeys', 'string', ''), ('Secs', 'int', '0'), ('Made', 'bool', 'false')):
        ue.blueprint_add_member_variable(widget, name, kind, False, default)
    level_variables(widget)
    typed_variable(widget, 'Tiers', '(%s)' % ','.join('"%s"' % w for w in TIER_WORDS), PinCategory='string', ContainerType=1)
    typed_variable(widget, 'WItem', PinCategory='object', PinSubCategoryObject=InventoryItem)
    keys, names, kinds, custom_ids, custom_families = read_families()
    listed = lambda values: '(%s)' % ','.join('"%s"' % v for v in values)
    typed_variable(widget, 'Kinds', listed(kinds), PinCategory='string', ContainerType=1)
    typed_variable(widget, 'Balances', PinCategory='int', ContainerType=1)
    typed_variable(widget, 'FamilyKeys', listed(keys), PinCategory='string', ContainerType=1)
    typed_variable(widget, 'FamilyNames', listed(names), PinCategory='string', ContainerType=1)
    typed_variable(widget, 'CustomIds', listed(custom_ids), PinCategory='string', ContainerType=1)
    typed_variable(widget, 'CustomFamilies', listed(custom_families), PinCategory='string', ContainerType=1)
    COUNTS['families'] = (len(keys), len(custom_ids))
    COUNTS['keys'] = keys
    stars_variable(widget, stars)
    ue.blueprint_mark_as_structurally_modified(widget)
    ue.compile_blueprint(widget)


def build_graph(widget, tile_class, bullet_class, badge_class):
    g = Graph(widget)
    family_count, custom_count = COUNTS['families']
    tick = event(widget, UserWidget, 'Tick', 0, 0)
    g.loose = [(tick, 'then')]
    player = lambda: g.call(UserWidget.GetOwningPlayer)

    def key(name):
        pressed = g.call(PlayerController.WasInputKeyJustPressed, self=player(), Key=None)
        set_default(pressed[0], 'Key', name)
        return pressed

    g.setter('Busy', g.call(KismetMathLibrary.Max, A=g.math('Subtract_IntInt', g.get('Busy'), 1), B=0))

    #--- once: where the mastery ids start (whatever else is registered before them) -----------------
    g.section()

    def look():
        g.setter('Looked', 'true')

        def one(v):
            name = g.call(KismetNodeHelperLibrary.GetEnumeratorName, EnumeratorValue=g.call(KismetMathLibrary.Conv_IntToByte, InInt=v))
            pin(name[0], 'Enum').default_object = ue.find_enum('EArmorPropertyID')
            hit = g.call(KismetStringLibrary.Contains, SearchIn=g.call(KismetStringLibrary.Conv_NameToString, InName=name),
                         Substring=FIRST_NAME)
            g.either(g.both(hit, g.math('Less_IntInt', g.get('Base'), 0)), lambda: g.setter('Base', v), lambda: None)

        looping(g, for_range(g, 41, 250), one)

    g.either(g.get('Looked'), lambda: None, look)
    known = lambda: g.math('GreaterEqual_IntInt', g.get('Base'), 0)

    #--- every few frames: a badge into every item tile that has none -------------------------------
    g.section()
    g.setter('Scan', g.math('Add_IntInt', g.get('Scan'), 1))

    def attach_pass():
        g.setter('Scan', 0)
        found = g.run(WidgetBlueprintLibrary.GetAllWidgetsOfClass, TopLevelOnly='false')
        pin(found, 'WidgetClass').default_object = tile_class
        loop = g.for_each((found, 'FoundWidgets'))
        g.then(loop, 'Exec', 'Completed')
        after = list(g.loose)
        g.loose = [(loop, 'LoopBody')]
        tile = g.cast(tile_class, (loop, 'Array Element'))
        padder = g.get('ItemInfoPadder', tile_class, of=tile)
        unless_holding(g, padder, badge_class)
        made = g.run(WidgetBlueprintLibrary.Create, OwningPlayer=player())
        pin(made, 'WidgetType').default_object = badge_class
        badge = g.cast(badge_class, (made, 'ReturnValue'))
        g.setter('Tile', tile, owner=badge_class, of=badge)
        g.setter('Base', g.get('Base'), owner=badge_class, of=badge)
        placed = (g.run(CanvasPanel.AddChildToCanvas, self=padder, Content=badge), 'ReturnValue')
        g.run(CanvasPanelSlot.SetAnchors, self=placed,
              InAnchors='(Minimum=(X=0.000000,Y=0.000000),Maximum=(X=1.000000,Y=1.000000))')
        g.run(CanvasPanelSlot.SetOffsets, self=placed, InOffset='(Left=0.000000,Top=0.000000,Right=0.000000,Bottom=0.000000)')
        g.run(CanvasPanelSlot.SetZOrder, self=placed, InZOrder=51)
        g.loose = after

    g.either(g.both(known(), g.math('GreaterEqual_IntInt', g.get('Scan'), 10)), attach_pass, lambda: None)

    #--- every few frames: a Mastery line under a weapon's name gets its tier's star ------------------
    g.section()
    g.setter('Scan2', g.math('Add_IntInt', g.get('Scan2'), 1))

    def line_pass():
        g.setter('Scan2', 0)
        found = g.run(WidgetBlueprintLibrary.GetAllWidgetsOfClass, TopLevelOnly='false')
        pin(found, 'WidgetClass').default_object = bullet_class
        loop = g.for_each((found, 'FoundWidgets'))
        g.then(loop, 'Exec', 'Completed')
        after = list(g.loose)
        g.loose = [(loop, 'LoopBody')]
        line = g.cast(bullet_class, (loop, 'Array Element'))
        g.setter('Line', g.call(KismetTextLibrary.Conv_TextToString, InText=g.call(TextBlock.GetText, self=g.get('Text', bullet_class, of=line))))
        level_line = g.both(g.call(KismetStringLibrary.StartsWith, SourceString=g.get('Line'), InPrefix='Mastery '),
                            g.call(KismetMathLibrary.Not_PreBool, A=g.call(KismetStringLibrary.StartsWith,
                                                                             SourceString=g.get('Line'), InPrefix='Mastery progress')))

        def star():
            g.setter('Tier', 0)
            for k, word in enumerate(TIER_WORDS):
                tier = 5 - k
                g.either(g.both(g.math('EqualEqual_IntInt', g.get('Tier'), 0),
                                g.call(KismetStringLibrary.Contains, SearchIn=g.get('Line'), Substring=word)),
                         lambda tier=tier: g.setter('Tier', tier), lambda: None)
            g.run(DungeonsImage.SetBrushFromTexture, self=g.get('Bullet', bullet_class, of=line),
                  Texture=g.element(g.get('Stars'), g.get('Tier')), bMatchSize='false')
            g.run(DungeonsImage.SetColorAndOpacity, self=g.get('Bullet', bullet_class, of=line),
                  InColorAndOpacity=g.call(KismetMathLibrary.MakeColor, R=1, G=1, B=1, A=1))
            g.run(Widget.SetRenderOpacity, self=g.get('BulletShadow', bullet_class, of=line), InOpacity=0)

        g.either(level_line, star, lambda: None)
        g.loose = after

    g.either(g.both(known(), g.math('GreaterEqual_IntInt', g.get('Scan2'), 3)), line_pass, lambda: None)

    #--- every few seconds, open or not: a balance for every kind of weapon ---------------------------
    #The plugin counts use into a character's balance for the kind of weapon, but cannot make one; a
    #currency added once (1 second) makes it, and from then on the plugin adds to it.
    g.section()
    g.setter('Scan3', g.math('Add_IntInt', g.get('Scan3'), 1))

    def make_balances():
        g.setter('Scan3', 0)
        g.hero = g.cast(PlayerCharacter, g.call(UserWidget.GetOwningPlayerPawn))

        #One node per currency, never one in a loop: a SerializableItemId keeps a cached lookup in its
        #twelve hidden bytes, and the one struct a loop reuses stays pointed at the first currency it
        #was made for - every "add" went to the first family, and every "how many" read it.
        for key in COUNTS['keys']:
            have = g.call(WalletComponent.Balance, self=g.wallet(), Type=g.currency(CURRENCY + key))
            g.either(g.math('Less_IntInt', have, 1),
                     lambda key=key: g.run(WalletComponent.ClientAdd, self=g.wallet(), Type=g.currency(CURRENCY + key), Amount=1,
                                           reason='Default'), lambda: None)

    g.either(g.math('GreaterEqual_IntInt', g.get('Scan3'), 60), make_balances, lambda: None)

    #--- J opens and closes, Esc and the button close -------------------------------------------------
    g.section()
    g.either(key(KEY), lambda: (g.setter('Open', g.call(KismetMathLibrary.Not_PreBool, A=g.get('Open'))),
                                g.setter('Fresh', 0), g.setter('Page', 0)), lambda: None)
    g.either(g.math('BooleanOR', key('Escape'), g.call(Button.IsPressed, self=g.get('Close'))),
             lambda: g.setter('Open', 'false'), lambda: None)
    g.either(g.get('Open'), lambda: g.shows('Panel', SHOWN), lambda: g.shows('Panel', COLLAPSED))
    shown, hidden = g.branch(g.get('Open'))
    g.loose = shown

    #Pages.
    last_page = lambda: g.call(KismetMathLibrary.Max, A=g.math('Divide_IntInt', g.math('Subtract_IntInt', g.get('W'), 1), ROWS), B=0)
    press = lambda name: g.both(g.call(Button.IsPressed, self=g.get(name)), g.math('LessEqual_IntInt', g.get('Busy'), 0))
    g.either(press('Prev'), lambda: (g.setter('Page', g.call(KismetMathLibrary.Max, A=g.math('Subtract_IntInt', g.get('Page'), 1), B=0)),
                                     g.setter('Busy', DEBOUNCE), g.setter('Fresh', 0)), lambda: None)
    g.either(press('Next'), lambda: (g.setter('Page', g.call(KismetMathLibrary.Min, A=g.math('Add_IntInt', g.get('Page'), 1), B=last_page())),
                                     g.setter('Busy', DEBOUNCE), g.setter('Fresh', 0)), lambda: None)

    #--- the rows, every 20 frames while open: every kind of weapon, the used first -------------------
    g.section()
    g.setter('Fresh', g.math('Subtract_IntInt', g.get('Fresh'), 1))
    refresh, wait = g.branch(g.both(known(), g.math('LessEqual_IntInt', g.get('Fresh'), 0)))
    g.loose = refresh
    g.setter('Fresh', 20)
    g.setter('W', 0)
    hero = g.cast(PlayerCharacter, g.call(UserWidget.GetOwningPlayerPawn))
    g.hero = hero
    stash = lambda: g.get('ItemStashComponent', PlayerCharacter, of=hero)
    rows = lambda: g.get('Rows')

    def part(row_index, k, cls):
        row = g.cast(CanvasPanel, g.call(PanelWidget.GetChildAt, self=rows(), Index=row_index))
        return g.cast(cls, g.call(PanelWidget.GetChildAt, self=row, Index=k))

    def say_text(row_index, k, text):
        g.run(TextBlock.SetText, self=part(row_index, k, TextBlock), InText=g.call(KismetTextLibrary.Conv_StringToText, InString=text))

    def int_text(value):
        return g.call(KismetStringLibrary.Conv_IntToString, InInt=value)

    def concat(*parts):
        value = parts[0]
        for more in parts[1:]:
            value = g.call(KismetStringLibrary.Concat_StrStr, A=value, B=more)
        return value

    #Which kinds are worn, as "|Claymore|CogCrossbow|".
    g.setter('EquippedKeys', '|')
    for slot_name in ('MeleeGear', 'RangedGear'):
        slots = g.call(ItemStashComponent.GetEquipmentSlots, self=stash())
        x, y = g.at()
        found = g.page.graph_add_node_call_function(BlueprintMapLibrary.Map_Find, x, y)
        link(slots[0], slots[1], found, 'TargetMap')
        told(found, 'TargetMap')
        set_default(found, 'Key', slot_name)
        worn = lambda found=found: g.get('Item', InventoryItemSlot, of=(found, 'Value'))

        def mark(worn=worn):
            g.setter('WItem', worn())
            family_key(g, lambda: g.get('WItem'), custom_count)
            g.setter('EquippedKeys', concat(g.get('EquippedKeys'), g.get('Key'), '|'))

        g.either(g.both((found, 'ReturnValue'), g.valid(worn())), mark, lambda: None)

    #Rows off first; the ones filled are shown again.
    looping(g, for_range(g, 0, ROWS - 1), lambda r: g.run(Widget.SetVisibility, self=g.call(
        PanelWidget.GetChildAt, self=rows(), Index=r), InVisibility=COLLAPSED))

    def fill_row(k):
        row_index = g.math('Subtract_IntInt', g.math('Subtract_IntInt', g.get('W'), 1), g.math('Multiply_IntInt', g.get('Page'), ROWS))
        g.run(Widget.SetVisibility, self=g.call(PanelWidget.GetChildAt, self=rows(), Index=row_index), InVisibility=LOOKS)
        g.run(Image.SetBrushFromTexture, self=part(row_index, 0, Image), Texture=g.element(g.get('Stars'), tier_of(g, g.get('Lvl'))),
              bMatchSize='false')
        g.run(Widget.SetRenderOpacity, self=part(row_index, 0, Image),
              InOpacity=g.call(KismetMathLibrary.SelectFloat, A=1.0, B=0.15, bPickA=g.math('Greater_IntInt', g.get('Lvl'), 0)))
        say_text(row_index, 1, int_text(g.get('Lvl')))
        key = g.element(g.get('FamilyKeys'), k)
        worn = g.call(KismetStringLibrary.Contains, SearchIn=g.get('EquippedKeys'), Substring=concat('|', key, '|'))
        say_text(row_index, 2, concat(g.element(g.get('FamilyNames'), k),
                                      g.call(KismetMathLibrary.SelectString, A='  (equipped)', B='', bPickA=worn)))
        maxed = g.math('GreaterEqual_IntInt', g.get('Lvl'), LEVELS_MAX)
        fraction = g.call(KismetMathLibrary.SelectFloat, A=1.0, B=g.math('Divide_FloatFloat', g.call(
            KismetMathLibrary.Conv_IntToFloat, InInt=g.get('Step')), float(STEPS)), bPickA=maxed)
        g.run(Widget.SetRenderScale, self=part(row_index, 4, Widget), Scale=g.call(KismetMathLibrary.MakeVector2D, X=fraction, Y=1.0))
        say_text(row_index, 5, g.call(KismetMathLibrary.SelectString, A='MAX',
                                      B=concat(int_text(g.math('Multiply_IntInt', g.get('Step'), 100 // STEPS)), '%'), bPickA=maxed))
        tier = tier_of(g, g.get('Lvl'))
        bow = g.call(KismetStringLibrary.EqualEqual_StrStr, A=g.element(g.get('Kinds'), k), B='r')
        damage = concat('+', int_text(g.math('Multiply_IntInt', g.get('Lvl'), DAMAGE_PER_LEVEL)), '% ',
                        g.call(KismetMathLibrary.SelectString, A='ranged', B='melee', bPickA=bow), ' damage')
        second = g.call(KismetMathLibrary.SelectString,
                        A=concat(', +', int_text(g.math('Multiply_IntInt', tier_of(g, g.get('Lvl')), ROLL_PER_TIER)), '% roll speed'),
                        B=concat(', +', int_text(g.math('Multiply_IntInt', tier_of(g, g.get('Lvl')), SPEED_PER_TIER)), '% attack speed'),
                        bPickA=bow)
        bonus = g.call(KismetMathLibrary.SelectString, A=g.call(KismetStringLibrary.Concat_StrStr, A=damage, B=second), B=damage,
                       bPickA=g.math('Greater_IntInt', tier, 0))
        say_text(row_index, 6, g.call(KismetMathLibrary.SelectString, A='-', B=bonus,
                                      bPickA=g.math('EqualEqual_IntInt', g.get('Lvl'), 0)))

    def family(k, used_pass):
        """Family k: counted and shown on this pass - used kinds on the first, the rest on the second."""
        g.setter('Secs', g.element(g.get('Balances'), k))
        used = g.math('Greater_IntInt', g.get('Secs'), 1)

        def counted():
            level_of(g, g.get('Secs'))
            g.setter('W', g.math('Add_IntInt', g.get('W'), 1))
            at = g.math('Subtract_IntInt', g.math('Subtract_IntInt', g.get('W'), 1), g.math('Multiply_IntInt', g.get('Page'), ROWS))
            g.either(g.both(g.math('GreaterEqual_IntInt', at, 0), g.math('Less_IntInt', at, ROWS)), lambda: fill_row(k), lambda: None)

        g.either(used if used_pass else g.call(KismetMathLibrary.Not_PreBool, A=used), counted, lambda: None)

    if family_count:
        g.setter('Balances', g.make_array([g.call(WalletComponent.Balance, self=g.wallet(), Type=g.currency(CURRENCY + key))
                                           for key in COUNTS['keys']]))
        looping(g, for_range(g, 0, family_count - 1), lambda k: family(k, True))
        looping(g, for_range(g, 0, family_count - 1), lambda k: family(k, False))

    g.run(TextBlock.SetText, self=g.get('PageText'), InText=g.call(KismetTextLibrary.Conv_StringToText, InString=concat(
        'Page ', int_text(g.math('Add_IntInt', g.get('Page'), 1)), ' of ', int_text(g.math('Add_IntInt', last_page(), 1)),
        '   (', int_text(g.get('W')), ' kinds of weapon)')))
    g.either(g.math('EqualEqual_IntInt', g.get('W'), 0), lambda: g.shows('Empty', LOOKS), lambda: g.shows('Empty', COLLAPSED))
    g.loose += wait
    g.loose += hidden
    ue.compile_blueprint(widget)
    say('mastery graph built')


def build_widget(stars, tile_class, bullet_class, badge_class):
    there = on_disk(WIDGET, 'WidgetBlueprint')
    if there is not None:
        say('mastery already there: ' + WIDGET)
        return there
    factory = WidgetBlueprintFactory()
    factory.ParentClass = UserWidget
    widget = factory.factory_create_new(WIDGET)
    build_tree(widget, stars)
    variables(widget, stars)
    build_graph(widget, tile_class, bullet_class, badge_class)
    keep(widget)
    say('mastery built: ' + WIDGET)
    return widget


def build_actor(widget):
    there = on_disk(ACTOR, 'Blueprint')
    if there is not None:
        say('mastery actor already there: ' + ACTOR)
        return there
    actor = ue.create_blueprint(Actor, ACTOR)
    page = actor.UberGraphPages[0]
    begin = event(actor, Actor, 'ReceiveBeginPlay', 0, 0)
    made = page.graph_add_node_call_function(WidgetBlueprintLibrary.Create, 400, 0)
    pin(made, 'WidgetType').default_object = widget.GeneratedClass
    controller = page.graph_add_node_call_function(GameplayStatics.GetPlayerController, 150, 200)
    link(controller, 'ReturnValue', made, 'OwningPlayer')
    link(begin, 'then', made, 'execute')
    shown = page.graph_add_node_call_function(UserWidget.AddToViewport, 800, 0)
    link(made, 'ReturnValue', shown, 'self')
    set_default(shown, 'ZOrder', '9997')
    link(made, 'then', shown, 'execute')
    ue.blueprint_add_member_variable(actor, 'Screen', build_gems.EdGraphPinType(
        PinCategory='object', PinSubCategoryObject=UserWidget), False, '')
    ue.blueprint_mark_as_structurally_modified(actor)
    ue.compile_blueprint(actor)
    kept = page.graph_add_node_variable_set('Screen', None, 1100, 0)
    link(made, 'ReturnValue', kept, 'Screen')
    link(shown, 'then', kept, 'execute')
    ended = event(actor, Actor, 'ReceiveEndPlay', 0, 800)
    gone = page.graph_add_node_call_function(Widget.RemoveFromParent, 600, 800)
    link(page.graph_add_node_variable_get('Screen', None, 350, 900), 'Screen', gone, 'self')
    link(ended, 'then', gone, 'execute')
    ue.compile_blueprint(actor)
    keep(actor)
    say('mastery actor built: ' + ACTOR)
    return actor


def build_level(path, actor):
    if on_disk(path, 'World') is not None:
        say('level already there: ' + path)
        return
    world = WorldFactory().factory_create_new(path)
    world.actor_spawn(actor.GeneratedClass)
    keep(world)
    say('level built: ' + path)


def main():
    stars = import_pictures()
    tile_class = build_gems.build_tile_stub().GeneratedClass
    bullet_class = build_gems.build_bullet_stub().GeneratedClass
    badge = build_badge(stars, tile_class)
    widget = build_widget(stars, tile_class, bullet_class, badge.GeneratedClass)
    actor = build_actor(widget)
    for path in LEVELS:
        build_level(path, actor)
    say('mastery done - cook, then carry the levels, the actor, the widgets and the stars')


if __name__ == '__main__':
    main()
