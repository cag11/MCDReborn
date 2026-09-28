"""
Mythic items, in the game: the Mythic Forge in the Camp, which awakens a worn melee weapon, bow or
armour for emeralds and gold, and (build_mythic_look, next) the red Mythic look wherever the game
draws the item.

The look, every 10 frames, wherever the game draws an item: an inventory tile's border and corner glow
and a worn slot's colour in red (the game's own Unique frames, reddened by the app on install); in the
item's details MYTHIC on a red plate where the rarity is said; its Mythic line in red. Anything not
Mythic that was drawn so is given back to the game's own redraw (UpdateFrameTextures,
SetDisplayRarity) - a tile or details panel is reused for the next item.

An item is Mythic when it carries one of the Mythic lines - MCDR_MythicMelee, MCDR_MythicRanged,
MCDR_MythicArmor, property copies the item plugin registers and applies (MCDSaveEdit/Logic/Mythic.cs).
Their numbers depend on what else is registered, so the screen finds them at run time by name.

Awakening appends the line to the item's own list: the item's data copied, its lines counted, a new
list made whole (its lines, then the Mythic one - an array node cannot be typed from here, Make Array
can) and written back, as the Gems screen writes sockets.

    /Game/MCDReborn/UI/UMG_MCDRebornMythic                 the forge screen and the passes
    /Game/MCDReborn/Actors/BP_MCDRebornMythic, Lobby/Mythic, Ingame/Mythic
    /Game/MCDReborn/Actors/BP_MCDRebornMythicForge, Lobby/MythicForge   the forge in the Camp

    UE4Editor-Cmd.exe <project>.uproject -run=Py <this file> -unattended -nopause -nosplash

Delete the widget, actor and levels first to rebuild them.
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
from build_gems import Graph, typed_variable, set_fields, DEBOUNCE, SHOWN, COLLAPSED, for_range, looping, told
import build_mastery

from unreal_engine.classes import (
    Actor,
    BlueprintMapLibrary,
    Button,
    CanvasPanel,
    DungeonsImage,
    Image,
    Overlay,
    GameplayStatics,
    InventoryItem,
    InventoryItemSlot,
    ItemStashComponent,
    KismetMathLibrary,
    KismetNodeHelperLibrary,
    KismetStringLibrary,
    KismetTextLibrary,
    PanelWidget,
    PlayerCharacter,
    PlayerController,
    TextBlock,
    UserWidget,
    WalletComponent,
    Widget,
    WidgetBlueprintFactory,
    WidgetBlueprintLibrary,
    WorldFactory,
)
from unreal_engine.structs import LinearColor

WIDGET = '/Game/MCDReborn/UI/UMG_MCDRebornMythic'
ACTOR = '/Game/MCDReborn/Actors/BP_MCDRebornMythic'
LEVELS = ['/Game/MCDReborn/Lobby/Mythic', '/Game/MCDReborn/Ingame/Mythic']

#The forge: the mining levels' bellows forge under a Camp sign; a click near it opens the screen. Where
#it stands is written into its level on install (Mythic.FORGE).
PROP_ACTOR = '/Game/MCDReborn/Actors/BP_MCDRebornMythicForge'
PROP_LEVEL = '/Game/MCDReborn/Lobby/MythicForge'
PROP_LOOKS = '/Game/Decor/Prefabs/MiningDevices/BP_BellowForge'
PROP_SIGN = '/Game/MCDReborn/UI/UMG_MCDRebornSign_Mythic'
PROP_WHERE = (19045.0, 9542.0, 11402.0)
PROP_FACING = 180.0
PROP_REACH = 250.0

EMERALDS, GOLD_COST = 20000, 1000                    # as Mythic.EMERALDS / GOLD in the app
LINES = ['MCDR_MythicMelee', 'MCDR_MythicArmor', 'MCDR_MythicRanged']
SLOTS = ['MeleeGear', 'ArmorGear', 'RangedGear']     # the rows, in this order, with LINES
KINDS = ['Melee weapon', 'Armour', 'Ranged weapon']
BONUS = ['+30% melee damage', '+20% damage reduction', '+30% ranged damage']
MOST_LINES = 12                                     # an item's own lines, at most, that awakening keeps

PANEL_W, PANEL_H = 1100.0, 560.0
BACK = LinearColor(R=0.0060, G=0.0070, B=0.0110, A=0.965)
ROW_BACK = LinearColor(R=0.0319, G=0.0319, B=0.0319, A=1.0)
RED = LinearColor(R=0.85, G=0.06, B=0.05, A=1.0)
ROW_Y = [150.0, 260.0, 370.0]

LOOK_EVERY = 10
ICON_CLASS = []                                     # the item icon stub's class, for the details stub's member
MARK = 0.999                                        # a render opacity no game widget has: "drawn Mythic by us"
PICTURES = os.path.join(HERE, 'mythic')
RED_FOLDER = '/Game/MCDReborn/UI/Mythic'
#The game's, as far as the look needs them. Never shipped.
INFO_STUB = build_mastery.INFO_STUB
TAG_ROW_STUB = '/Game/UI/Inventory/UMG_ItemTagIconName'
RARITY_STUB = '/Game/UI/Item/UMG_ItemRarityBlock'
SLICE_STUB = '/Game/UI/Common/UMG_3SliceRarityBig'
SPARKLE_STUB = '/Game/UI/Inventory/Inspector2/UMG_ItemRaritySparkle'
ICON_STUB = '/Game/UI/Item/UMG_InventoryInspectorItemIcon'
SPARKLE_RED = (255, 32, 24)                         # the lights behind a Mythic item, as an FColor


def texture(name):
    path = '%s/%s' % (RED_FOLDER, name)
    try:
        there = ue.load_object(ue.find_class('Texture2D'), '%s.%s' % (path, name))
    except Exception:
        there = None
    if there is not None:
        return there
    made = ue.import_asset(os.path.join(PICTURES, name + '.png'), RED_FOLDER)
    there = made[0] if isinstance(made, list) else made
    #Uncompressed and whole, as the app paints over it on install; never streamed (a widget's picture).
    for field, value in (('MipGenSettings', 13), ('CompressionSettings', 7), ('NeverStream', True)):
        try:
            setattr(there, field, value)
        except Exception as problem:
            say('  %s.%s not set: %s' % (name, field, problem))
    keep(there)
    return there


def stub(path, build):
    there = on_disk(path, 'WidgetBlueprint')
    if there is not None:
        say('stub already there: ' + path)
        return there
    factory = WidgetBlueprintFactory()
    factory.ParentClass = UserWidget
    made = factory.factory_create_new(path)
    made.modify()
    build(made)
    ue.blueprint_mark_as_structurally_modified(made)
    ue.compile_blueprint(made)
    keep(made)
    say('stub built: ' + path)
    return made


def build_stubs():
    slice_stub = stub(SLICE_STUB, lambda made: typed_variable(made, 'background', PinCategory='object', PinSubCategoryObject=DungeonsImage))

    def rarity(made):
        typed_variable(made, 'Text', PinCategory='object', PinSubCategoryObject=TextBlock)
        typed_variable(made, 'UMG_3SliceRarityBig', PinCategory='object', PinSubCategoryObject=slice_stub.GeneratedClass)
        typed_variable(made, 'DisplayRarity', PinCategory='byte', PinSubCategoryObject=ue.find_enum('EItemRarity'))
        #The game's own: the label drawn for a rarity again.
        entry = ue.blueprint_add_function(made, 'SetDisplayRarity').Nodes[0]
        entry.node_create_pin(1, build_gems.EdGraphPinType(PinCategory='byte', PinSubCategoryObject=ue.find_enum('EItemRarity')), 'Rarity')

    rarity_stub = stub(RARITY_STUB, rarity)
    tag_row_stub = stub(TAG_ROW_STUB, lambda made: typed_variable(made, 'UMG_ItemRarity', PinCategory='object',
                                                                  PinSubCategoryObject=rarity_stub.GeneratedClass))

    def info(made):
        #As build_mastery's (the chip needs these), and the tag row.
        box = Overlay('ItemNameContainer', made.WidgetTree)
        box.bIsVariable = True
        put(made.WidgetTree.RootWidget, box, 0.0, 0.0, 600.0, 60.0)
        name = TextBlock('ItemName', made.WidgetTree)
        name.bIsVariable = True
        box.AddChild(name)
        typed_variable(made, 'InspectedItem', PinCategory='object', PinSubCategoryObject=InventoryItem)
        typed_variable(made, 'UMG_ItemTagIconName', PinCategory='object', PinSubCategoryObject=tag_row_stub.GeneratedClass)
        #The item's picture, whose lights (its UMG_ItemRaritySparkle) take the rarity's colour.
        typed_variable(made, 'UMG_InventoryInspectorItemIcon', PinCategory='object', PinSubCategoryObject=ICON_CLASS[0])

    def sparkle(made):
        #The game's own: the lights' colour, into their material.
        entry = ue.blueprint_add_function(made, 'SetColor').Nodes[0]
        entry.node_create_pin(1, build_gems.EdGraphPinType(PinCategory='struct', PinSubCategoryObject=ue.find_struct('Color')), 'Color')

    sparkle_stub = stub(SPARKLE_STUB, sparkle)
    ICON_CLASS.clear()
    icon_stub = stub(ICON_STUB, lambda made: typed_variable(made, 'UMG_ItemRaritySparkle', PinCategory='object',
                                                            PinSubCategoryObject=sparkle_stub.GeneratedClass))
    ICON_CLASS.append(icon_stub.GeneratedClass)
    info_stub = stub(INFO_STUB, info)
    tile_stub = build_gems.build_tile_stub()
    bullet_stub = build_gems.build_bullet_stub()
    return (tile_stub.GeneratedClass, info_stub.GeneratedClass, tag_row_stub.GeneratedClass, rarity_stub.GeneratedClass,
            slice_stub.GeneratedClass, bullet_stub.GeneratedClass, sparkle_stub.GeneratedClass, icon_stub.GeneratedClass)


def build_tree(widget):
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
    put(panel, fill(wt, 'Edge', RED), 0.0, 0.0, PANEL_W, 4.0)
    put(panel, label(wt, 'Title', 'MYTHIC FORGE', RED, title_face, 32, typeface=TITLE_FACE, shadow=SHADOW),
        40.0, 22.0, 600.0, 48.0)
    close = mcd_ui.button(wt, 'Close', 'CLOSE', body_face, 18, typeface=BODY_FACE, centred=True)
    close.ToolTipText = 'Close (Esc).'
    put(panel, close, PANEL_W - 190.0, 26.0, 150.0, 42.0)
    put(panel, label(wt, 'Hint', 'Upgrade what you wear to Mythic rarity, for %s emeralds and %s gold.'
                     % ('{:,}'.format(EMERALDS), '{:,}'.format(GOLD_COST)), INK, body_face, 17, typeface=BODY_FACE),
        40.0, 88.0, PANEL_W - 80.0, 28.0)
    for r in range(3):
        y = ROW_Y[r]
        put(panel, fill(wt, 'RowBack%d' % r, ROW_BACK), 30.0, y, PANEL_W - 60.0, 96.0)
        put(panel, label(wt, 'Kind%d' % r, KINDS[r], DIM, body_face, 16, typeface=BODY_FACE), 50.0, y + 10.0, 400.0, 24.0)
        put(panel, label(wt, 'Name%d' % r, '-', INK, body_face, 22, variable=True, typeface=BODY_FACE),
            50.0, y + 36.0, 600.0, 32.0)
        put(panel, label(wt, 'State%d' % r, '', DIM, body_face, 17, variable=True, typeface=BODY_FACE),
            50.0, y + 68.0, 640.0, 24.0)
        awaken = mcd_ui.button(wt, 'Awaken%d' % r, 'AWAKEN', body_face, 20, typeface=BODY_FACE, centred=True)
        awaken.bIsVariable = True
        put(panel, awaken, PANEL_W - 290.0, y + 24.0, 230.0, 48.0)
    put(panel, label(wt, 'Wallet', '', DIM, body_face, 18, variable=True, typeface=BODY_FACE), 40.0, PANEL_H - 70.0, 700.0, 28.0)
    put(panel, label(wt, 'Toast', '', RED, body_face, 20, variable=True, typeface=BODY_FACE), 40.0, PANEL_H - 40.0, PANEL_W - 80.0, 30.0)


def variables(widget):
    for name, kind, default in (('Busy', 'int', '0'), ('Open', 'bool', 'false'), ('Fresh', 'int', '0'),
                                ('PropScan', 'int', '0'), ('PropHere', 'bool', 'false'), ('Looked', 'bool', 'false'),
                                ('Lines', 'int', '0'), ('IsMythic', 'bool', 'false'), ('ToastTime', 'int', '0'),
                                ('LookCount', 'int', '0')):
        ue.blueprint_add_member_variable(widget, name, kind, False, default)
    for line in LINES:
        ue.blueprint_add_member_variable(widget, 'Id' + line, 'int', False, '-1')
    typed_variable(widget, 'PropAt', PinCategory='struct', PinSubCategoryObject=ue.find_struct('Vector'))
    typed_variable(widget, 'WItem', PinCategory='object', PinSubCategoryObject=InventoryItem)
    typed_variable(widget, 'Work', PinCategory='struct', PinSubCategoryObject=ue.find_struct('InventoryItemData'))
    #The line being added, typed as the game's enum so a Make of ArmorPropertyData takes it.
    typed_variable(widget, 'NewId', PinCategory='byte', PinSubCategoryObject=ue.find_enum('EArmorPropertyID'))
    #A tile's frame pictures: widgets inside it, not variables of it - found by name each look.
    for name in ('Frame', 'Glow', 'WornColour'):
        typed_variable(widget, name, PinCategory='object', PinSubCategoryObject=DungeonsImage)
    ue.blueprint_mark_as_structurally_modified(widget)
    ue.compile_blueprint(widget)


def worn(g, slot_name):
    """What the player wears in an equipment slot, as (found, item) values."""
    hero = g.call(UserWidget.GetOwningPlayerPawn)
    stash = g.get('ItemStashComponent', PlayerCharacter, of=g.cast(PlayerCharacter, hero))
    slots = g.call(ItemStashComponent.GetEquipmentSlots, self=stash)
    x, y = g.at()
    found = g.page.graph_add_node_call_function(BlueprintMapLibrary.Map_Find, x, y)
    link(slots[0], slots[1], found, 'TargetMap')
    told(found, 'TargetMap')
    set_default(found, 'Key', slot_name)
    return (found, 'ReturnValue'), lambda: g.get('Item', InventoryItemSlot, of=(found, 'Value'))


def text(g, widget_name, value):
    g.run(TextBlock.SetText, self=g.get(widget_name), InText=g.call(KismetTextLibrary.Conv_StringToText, InString=value))


def concat(g, *parts):
    value = parts[0]
    for part in parts[1:]:
        value = g.call(KismetStringLibrary.Concat_StrStr, A=value, B=part)
    return value


def mythic_check(g, item):
    """IsMythic: whether the item already carries any Mythic line."""
    g.setter('IsMythic', 'false')
    lines = g.for_each((g.breaks('InventoryItemData', g.get('Item', InventoryItem, of=item())), 'ArmorProperties'))
    g.then(lines, 'Exec', 'Completed')
    done = list(g.loose)
    g.loose = [(lines, 'LoopBody')]
    line_id = g.call(KismetMathLibrary.Conv_ByteToInt, InByte=(g.breaks('ArmorPropertyData', (lines, 'Array Element')), 'ID'))
    for name in LINES:
        g.either(g.both(g.math('GreaterEqual_IntInt', g.get('Id' + name), 0), g.math('EqualEqual_IntInt', line_id, g.get('Id' + name))),
                 lambda: g.setter('IsMythic', 'true'), lambda: None)
    g.loose = done


def append_line(g, item, line_name):
    """The item's lines, then the Mythic one: counted, made whole, written back."""
    g.setter('WItem', item())
    g.setter('Work', g.get('Item', InventoryItem, of=g.get('WItem')))
    g.setter('Lines', 0)
    lines = g.for_each((g.breaks('InventoryItemData', g.get('Work')), 'ArmorProperties'))
    g.then(lines, 'Exec', 'Completed')
    done = list(g.loose)
    g.loose = [(lines, 'LoopBody')]
    g.setter('Lines', g.math('Add_IntInt', g.get('Lines'), 1))
    g.loose = done
    #The line's number as the game's enum: a byte into an enum variable, through the schema, which
    #puts in the conversion.
    x, y = g.at(200)
    setter = g.page.graph_add_node_variable_set('NewId', None, x, y)
    to_byte = g.call(KismetMathLibrary.Conv_IntToByte, InInt=g.get('Id' + line_name))
    pin(to_byte[0], to_byte[1]).connect(pin(setter, 'NewId'))
    g.then(setter)
    for own in range(MOST_LINES + 1):
        def write(own=own):
            kept = [g.element((g.breaks('InventoryItemData', g.get('Work')), 'ArmorProperties'), i) for i in range(own)]
            added = [g.makes('ArmorPropertyData', ID=g.get('NewId'), Rarity='Unique')]
            set_fields(g, 'InventoryItemData', g.get('Work'), ArmorProperties=g.make_array(kept + added))
            g.setter('Item', g.get('Work'), owner=InventoryItem, of=g.get('WItem'))
        g.either(g.math('EqualEqual_IntInt', g.get('Lines'), own), write, lambda: None)


def build_graph(widget, prop_class, stubs, reds):
    tile_class, info_class, tag_row_class, rarity_class, slice_class, bullet_class, sparkle_class, icon_class = stubs
    g = Graph(widget)
    tick = event(widget, UserWidget, 'Tick', 0, 0)
    g.loose = [(tick, 'then')]
    player = lambda: g.call(UserWidget.GetOwningPlayer)

    def key(name):
        pressed = g.call(PlayerController.WasInputKeyJustPressed, self=player(), Key=None)
        set_default(pressed[0], 'Key', name)
        return pressed

    g.setter('Busy', g.call(KismetMathLibrary.Max, A=g.math('Subtract_IntInt', g.get('Busy'), 1), B=0))

    #--- once: the Mythic lines' numbers, by name ----------------------------------------------------
    g.section()

    def look():
        g.setter('Looked', 'true')

        def one(v):
            enum_name = g.call(KismetNodeHelperLibrary.GetEnumeratorName, EnumeratorValue=g.call(KismetMathLibrary.Conv_IntToByte, InInt=v))
            pin(enum_name[0], 'Enum').default_object = ue.find_enum('EArmorPropertyID')
            name = g.call(KismetStringLibrary.Conv_NameToString, InName=enum_name)
            #Contains, not equal: the name comes back with its enum's prefix (as build_mastery's lookup finds).
            for line in LINES:
                g.either(g.call(KismetStringLibrary.Contains, SearchIn=name, Substring=line), lambda line=line: g.setter('Id' + line, v), lambda: None)

        looping(g, for_range(g, 41, 250), one)

    #Until all three are found - the plugin names them as the game starts, maybe after this first looks.
    all_found = g.both(*[g.math('GreaterEqual_IntInt', g.get('Id' + line), 0) for line in LINES])
    g.either(g.both(g.call(KismetMathLibrary.Not_PreBool, A=all_found), g.call(KismetMathLibrary.Not_PreBool, A=g.get('Looked'))),
             look, lambda: None)
    g.either(all_found, lambda: None, lambda: g.setter('Looked', 'false'))
    known = lambda: g.math('GreaterEqual_IntInt', g.get('Id' + LINES[0]), 0)

    #--- the forge: whether it is here (the Camp) and where; a click near it opens the screen ---------
    g.section()
    g.setter('PropScan', g.math('Add_IntInt', g.get('PropScan'), 1))

    def prop_scan():
        g.setter('PropScan', 0)
        g.setter('PropHere', 'false')
        props = g.run(GameplayStatics.GetAllActorsOfClass)
        pin(props, 'ActorClass').default_object = prop_class
        loop = g.for_each((props, 'OutActors'))
        g.then(loop, 'Exec', 'Completed')
        after = list(g.loose)
        g.loose = [(loop, 'LoopBody')]
        g.setter('PropAt', g.call(Actor.K2_GetActorLocation, self=(loop, 'Array Element')))
        g.setter('PropHere', 'true')
        g.loose = after

    g.either(g.math('GreaterEqual_IntInt', g.get('PropScan'), 10), prop_scan, lambda: None)

    def maybe_open():
        under = g.call(PlayerController.GetHitResultUnderCursorByChannel, self=player(),
                       TraceChannel='TraceTypeQuery1', bTraceComplex='false')
        hit = g.call(GameplayStatics.BreakHitResult, out='Location', Hit=(under[0], 'HitResult'))
        near = g.math('Less_FloatFloat', g.call(KismetMathLibrary.Vector_Distance, V1=hit, V2=g.get('PropAt')), PROP_REACH)
        g.either(g.both(key('LeftMouseButton'), under, near),
                 lambda: (g.setter('Open', 'true'), g.setter('Fresh', 0), g.setter('Busy', DEBOUNCE)), lambda: None)

    g.either(g.both(g.get('PropHere'), g.call(KismetMathLibrary.Not_PreBool, A=g.get('Open'))), maybe_open, lambda: None)

    #--- the Mythic look, every LOOK_EVERY frames --------------------------------------------------------
    g.section()
    g.setter('LookCount', g.math('Add_IntInt', g.get('LookCount'), 1))

    def picture(image_value, which):
        node = g.run(DungeonsImage.SetBrushFromTexture, self=image_value, bMatchSize='false')
        node.node_find_pin('Texture').default_object = which

    def each_of(cls, body):
        found = g.run(WidgetBlueprintLibrary.GetAllWidgetsOfClass, TopLevelOnly='false')
        pin(found, 'WidgetClass').default_object = cls
        each = g.for_each((found, 'FoundWidgets'))
        g.then(each, 'Exec', 'Completed')
        after = list(g.loose)
        g.loose = [(each, 'LoopBody')]
        body(g.cast(cls, (each, 'Array Element')))
        g.loose = after

    marked = lambda widget_value: g.math('Less_FloatFloat', g.call(Widget.GetRenderOpacity, self=widget_value), 1.0)

    def tiles(tile):
        slot = lambda: g.get('InventoryItemSlot', tile_class, of=tile)
        item = lambda: g.get('Item', InventoryItemSlot, of=slot())
        #The frame pictures are not variables of the tile, and a worn slot's come from the equipment panel
        #around it, so they are walked to from the tile's own SlotDefault (a live dump showed both):
        #  an inventory tile  SlotDefault > Overlay > [ItemSlotDefaultImage, ItemSlotRarity]
        #  a worn slot        SlotDefault > Overlay > CanvasPanel > [GearSlotDefaultImage, ScaleBox > GearSlotRarity]
        for var in ('Frame', 'Glow', 'WornColour'):
            g.setter(var, None)
        default = g.get('SlotDefault', tile_class, of=tile)
        overlay = g.cast(PanelWidget, g.call(PanelWidget.GetChildAt, self=default, Index=0))
        first = lambda: g.call(PanelWidget.GetChildAt, self=overlay, Index=0)
        kind = g.call(GameplayStatics.GetObjectClass, Object=first())
        is_image = g.call(KismetMathLibrary.ClassIsChildOf, TestClass=kind)
        is_image[0].node_find_pin('ParentClass').default_object = DungeonsImage

        def inventory_tile():
            g.setter('Frame', g.cast(DungeonsImage, first()))
            g.setter('Glow', g.cast(DungeonsImage, g.call(PanelWidget.GetChildAt, self=overlay, Index=1)))

        def worn_slot():
            canvas = g.cast(PanelWidget, first())
            scale = g.cast(PanelWidget, g.call(PanelWidget.GetChildAt, self=canvas, Index=1))
            g.setter('WornColour', g.cast(DungeonsImage, g.call(PanelWidget.GetChildAt, self=scale, Index=0)))

        g.either(is_image, inventory_tile, worn_slot)
        frame = lambda: g.get('Frame')
        glow = lambda: g.get('Glow')
        worn_colour = lambda: g.get('WornColour')
        g.setter('IsMythic', 'false')
        g.either(g.valid(slot()), lambda: g.either(g.valid(item()), lambda: mythic_check(g, item), lambda: None), lambda: None)

        def red():
            g.either(g.valid(frame()), lambda: (
                picture(frame(), reds['T_MCDRebornMythicGearSlot']),
                picture(glow(), reds['T_MCDRebornMythicOverlay']),
                g.run(Widget.SetVisibility, self=glow(), InVisibility='HitTestInvisible'),
                g.run(Widget.SetRenderOpacity, self=frame(), InOpacity=MARK)), lambda: None)
            g.either(g.valid(worn_colour()), lambda: (
                picture(worn_colour(), reds['T_MCDRebornMythicWorn']),
                g.run(Widget.SetRenderOpacity, self=worn_colour(), InOpacity=MARK)), lambda: None)

        def given_back():
            remake = ue.find_object(tile_class.get_path_name() + ':UpdateFrameTextures')
            g.run(remake, self=tile)
            g.either(g.valid(frame()), lambda: g.run(Widget.SetRenderOpacity, self=frame(), InOpacity=1.0), lambda: None)
            g.either(g.valid(worn_colour()), lambda: g.run(Widget.SetRenderOpacity, self=worn_colour(), InOpacity=1.0), lambda: None)

        was_red = g.math('BooleanOR', g.both(g.valid(frame()), marked(frame())), g.both(g.valid(worn_colour()), marked(worn_colour())))
        g.either(g.get('IsMythic'), red, lambda: g.either(was_red, given_back, lambda: None))

    def details(info):
        item = lambda: g.get('InspectedItem', info_class, of=info)
        label_block = lambda: g.get('UMG_ItemRarity', tag_row_class, of=g.get('UMG_ItemTagIconName', info_class, of=info))
        words = lambda: g.get('Text', rarity_class, of=label_block())
        g.setter('IsMythic', 'false')
        g.either(g.valid(item()), lambda: mythic_check(g, item), lambda: None)

        def red():
            g.run(TextBlock.SetText, self=words(), InText=g.call(KismetTextLibrary.Conv_StringToText, InString='MYTHIC'))
            picture(g.get('background', slice_class, of=g.get('UMG_3SliceRarityBig', rarity_class, of=label_block())),
                    reds['T_MCDRebornMythicPlate'])
            g.run(Widget.SetRenderOpacity, self=words(), InOpacity=MARK)
            #The lights behind the item's picture: red. The game sets them again for the next item it shows.
            lights = g.get('UMG_ItemRaritySparkle', icon_class, of=g.get('UMG_InventoryInspectorItemIcon', info_class, of=info))
            recolour = ue.find_object(sparkle_class.get_path_name() + ':SetColor')
            r, gg, b = SPARKLE_RED
            g.run(recolour, self=lights, Color=g.makes('Color', R=r, G=gg, B=b, A=255))

        def given_back():
            again = ue.find_object(rarity_class.get_path_name() + ':SetDisplayRarity')
            g.run(again, self=label_block(), Rarity=g.get('DisplayRarity', rarity_class, of=label_block()))
            g.run(Widget.SetRenderOpacity, self=words(), InOpacity=1.0)

        g.either(g.valid(label_block()), lambda: g.either(g.get('IsMythic'), red,
                                                           lambda: g.either(marked(words()), given_back, lambda: None)),
                 lambda: None)

    def lines(line):
        text_value = lambda: g.get('Text', bullet_class, of=line)
        said = g.call(KismetTextLibrary.Conv_TextToString, InText=g.call(TextBlock.GetText, self=text_value()))
        mythic = g.call(KismetStringLibrary.StartsWith, SourceString=said, InPrefix='Mythic:')
        colour = lambda r, gg, b: g.makes('SlateColor', SpecifiedColor=g.call(KismetMathLibrary.MakeColor, R=r, G=gg, B=b, A=1))
        g.either(mythic, lambda: g.run(TextBlock.SetColorAndOpacity, self=text_value(), InColorAndOpacity=colour(1.0, 0.16, 0.12)),
                 lambda: g.run(TextBlock.SetColorAndOpacity, self=text_value(), InColorAndOpacity=colour(1.0, 1.0, 1.0)))

    def look_pass():
        g.setter('LookCount', 0)
        each_of(tile_class, tiles)
        each_of(info_class, details)
        each_of(bullet_class, lines)

    g.either(g.both(known(), g.math('GreaterEqual_IntInt', g.get('LookCount'), LOOK_EVERY)), look_pass, lambda: None)

    #--- Esc and the button close; the screen shows while open -----------------------------------------
    g.section()
    g.either(g.math('BooleanOR', key('Escape'), g.call(Button.IsPressed, self=g.get('Close'))),
             lambda: g.setter('Open', 'false'), lambda: None)
    g.either(g.get('Open'), lambda: g.shows('Panel', SHOWN), lambda: g.shows('Panel', COLLAPSED))
    shown, hidden = g.branch(g.both(g.get('Open'), known()))
    g.loose = shown

    hero = g.cast(PlayerCharacter, g.call(UserWidget.GetOwningPlayerPawn))
    g.hero = hero
    emeralds = lambda: g.call(WalletComponent.Balance, self=g.wallet(), Type=g.currency('Emerald'))
    gold = lambda: g.call(WalletComponent.Balance, self=g.wallet(), Type=g.currency('Gold'))
    afford = lambda: g.both(g.math('GreaterEqual_IntInt', emeralds(), EMERALDS), g.math('GreaterEqual_IntInt', gold(), GOLD_COST))

    #The rows, every 10 frames: what is worn, and whether it is Mythic already.
    g.setter('Fresh', g.math('Subtract_IntInt', g.get('Fresh'), 1))

    def refresh():
        g.setter('Fresh', 10)
        text(g, 'Wallet', concat(g, 'You have ', g.call(KismetStringLibrary.Conv_IntToString, InInt=emeralds()), ' emeralds and ',
                                 g.call(KismetStringLibrary.Conv_IntToString, InInt=gold()), ' gold.'))
        for r, slot in enumerate(SLOTS):
            found, item = worn(g, slot)

            def with_item(r=r, item=item):
                g.run(TextBlock.SetText, self=g.get('Name%d' % r), InText=g.call(InventoryItem.GetDisplayNameText, self=item()))
                mythic_check(g, item)
                g.either(g.get('IsMythic'),
                         lambda: (text(g, 'State%d' % r, 'MYTHIC - ' + BONUS[r]),
                                  g.run(Widget.SetIsEnabled, self=g.get('Awaken%d' % r), bInIsEnabled='false')),
                         lambda: (text(g, 'State%d' % r, 'Awakening gives ' + BONUS[r]),
                                  g.run(Widget.SetIsEnabled, self=g.get('Awaken%d' % r), bInIsEnabled='true')))

            g.either(g.both(found, g.valid(item())), with_item,
                     lambda r=r: (text(g, 'Name%d' % r, 'Nothing worn'), text(g, 'State%d' % r, ''),
                                  g.run(Widget.SetIsEnabled, self=g.get('Awaken%d' % r), bInIsEnabled='false')))

    g.either(g.math('LessEqual_IntInt', g.get('Fresh'), 0), refresh, lambda: None)

    #A press of AWAKEN: paid, the line added, said.
    for r, slot in enumerate(SLOTS):
        pressed = g.both(g.call(Button.IsPressed, self=g.get('Awaken%d' % r)), g.math('LessEqual_IntInt', g.get('Busy'), 0))

        def awaken(r=r, slot=slot):
            g.setter('Busy', DEBOUNCE)
            found, item = worn(g, slot)
            g.either(g.both(found, g.valid(item())), lambda: mythic_check(g, item), lambda: g.setter('IsMythic', 'true'))

            def pay_and_awaken():
                g.run(WalletComponent.Deduct, self=g.wallet(), Type=g.currency('Emerald'), Amount=EMERALDS)
                g.run(WalletComponent.Deduct, self=g.wallet(), Type=g.currency('Gold'), Amount=GOLD_COST)
                append_line(g, item, LINES[r])
                text(g, 'Toast', concat(g, g.call(KismetTextLibrary.Conv_TextToString, InText=g.call(
                    InventoryItem.GetDisplayNameText, self=item())), ' is now Mythic!'))
                g.setter('ToastTime', 240)
                g.setter('Fresh', 0)

            def cannot():
                g.either(g.get('IsMythic'), lambda: text(g, 'Toast', 'That item is Mythic already.'),
                         lambda: text(g, 'Toast', 'Not enough: awakening costs %s emeralds and %s gold.'
                                      % ('{:,}'.format(EMERALDS), '{:,}'.format(GOLD_COST))))
                g.setter('ToastTime', 240)

            g.either(g.both(g.call(KismetMathLibrary.Not_PreBool, A=g.get('IsMythic')), afford()), pay_and_awaken, cannot)

        g.either(pressed, awaken, lambda: None)

    #The toast fades after a while.
    g.setter('ToastTime', g.call(KismetMathLibrary.Max, A=g.math('Subtract_IntInt', g.get('ToastTime'), 1), B=0))
    g.either(g.math('EqualEqual_IntInt', g.get('ToastTime'), 1), lambda: text(g, 'Toast', ''), lambda: None)
    g.loose += hidden
    ue.compile_blueprint(widget)
    say('mythic graph built')


def build_widget(prop_class, stubs, reds):
    there = on_disk(WIDGET, 'WidgetBlueprint')
    if there is not None:
        say('mythic already there: ' + WIDGET)
        return there
    factory = WidgetBlueprintFactory()
    factory.ParentClass = UserWidget
    widget = factory.factory_create_new(WIDGET)
    build_tree(widget)
    variables(widget)
    build_graph(widget, prop_class, stubs, reds)
    keep(widget)
    say('mythic built: ' + WIDGET)
    return widget


def build_actor(widget):
    there = on_disk(ACTOR, 'Blueprint')
    if there is not None:
        say('mythic actor already there: ' + ACTOR)
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
    set_default(shown, 'ZOrder', '9996')
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
    say('mythic actor built: ' + ACTOR)
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
    prop = build_gems.build_stall(None, actor_path=PROP_ACTOR, level_path=PROP_LEVEL, looks_path=PROP_LOOKS,
                                  sign_path=PROP_SIGN, words='MYTHIC FORGE', at=PROP_WHERE, facing=PROP_FACING,
                                  sign_height=300.0)
    reds = {name: texture(name) for name in ('T_MCDRebornMythicGearSlot', 'T_MCDRebornMythicOverlay',
                                             'T_MCDRebornMythicWorn', 'T_MCDRebornMythicPlate')}
    stubs = build_stubs()
    widget = build_widget(prop.GeneratedClass, stubs, reds)
    actor = build_actor(widget)
    for path in LEVELS:
        build_level(path, actor)
    say('mythic done - cook, then carry the levels, the actors, the widget and the sign')


if __name__ == '__main__':
    main()
