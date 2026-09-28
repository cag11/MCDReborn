"""
Enchantment levels IV and V, as the game shows them. The game keeps an enchantment above III and its
effects keep scaling (Celerity IV measured live: -46% artifact cooldown), but every place it draws a
level only knows I-III:

    UMG_EnchantmentBigLevelPlate    the badge on the big icon: a switcher of three pictures (I, II,
                                    III); at 4 it stays on its first - "I"
    UMG_EnchantmentSelectedWidget   the enchantment icons under an item: a text from GetLevelNumeral,
                                    which has nothing past III - blank

and the enchantment scroll (UMG_EnchantmentInspectorContents2) says "Max Tier Reached" from III on.

So a widget of ours looks every few frames: a badge at 4 or 5 gets our IV or V picture in its first
slot, selected, and gets the game's own I back as soon as it shows 1-3 again; an icon at 4 or 5 gets
"IV" or "V" as its text (the game writes its own again whenever the level changes). A scroll whose
enchantment is at III or IV shows the game's own UPGRADE button again, with our cost on it (dimmed when
the hero cannot pay); a press on it (its ButtonSurface, as the menu takes the mouse from the player's
input) rewrites the enchantment through the item's ReplaceEnchantment
- level + 1, the cost added to its invested points, which the game takes from the hero's points and
gives back on salvage. Costs as EnchantLevels.costOf in the app: IV 5 (7 powerful), V 8 (10 powerful).
The scroll's UPGRADE TIERS list builds NumberOfLevels rows (3) from the game's own
GetLevelEffectDescriptionForEnchantmentType, which computes a level's numbers from per-enchantment
formulas rather than a table of three; it is set to 5 and the rows rebuilt (it makes them once, when it
is made, and only ever adds), and the rows' small badges get IV and V as the big one does.

    /Game/MCDReborn/UI/EnchantLevels/T_MCDRebornEnchantPlateIV, V   our numerals (enchant_numerals.py)
    /Game/MCDReborn/UI/UMG_MCDRebornEnchantLevels                    the widget
    /Game/MCDReborn/Actors/BP_MCDRebornEnchantLevels, Lobby/EnchantLevels, Ingame/EnchantLevels

    UE4Editor-Cmd.exe <project>.uproject -run=Py <this file> -unattended -nopause -nosplash

Delete the widget, actor and levels first to rebuild them.
"""

import os
import sys

HERE = os.environ.get('MCDREBORN_LOADER', r'C:/Users/Gaming/RiderProjects/MCDSaveEditReborn/Tools/loader')
sys.path.insert(0, HERE)

import unreal_engine as ue
import mcd_ui
from mcd_ui import say, keep, on_disk, pin, link, set_default, event
import build_gems
from build_gems import Graph, typed_variable, set_fields

from unreal_engine.classes import (
    Actor,
    Button,
    GameplayStatics,
    PanelWidget,
    Image,
    InventoryItem,
    ItemStashComponent,
    PlayerCharacter,
    PlayerController,
    KismetMathLibrary,
    KismetTextLibrary,
    TextBlock,
    Texture2D,
    UserWidget,
    Widget,
    WidgetBlueprintFactory,
    WidgetBlueprintLibrary,
    WidgetSwitcher,
    WorldFactory,
)

WIDGET = '/Game/MCDReborn/UI/UMG_MCDRebornEnchantLevels'
ACTOR = '/Game/MCDReborn/Actors/BP_MCDRebornEnchantLevels'
LEVELS = ['/Game/MCDReborn/Lobby/EnchantLevels', '/Game/MCDReborn/Ingame/EnchantLevels']
NUMERALS = '/Game/MCDReborn/UI/EnchantLevels'
PICTURES = os.path.join(HERE, 'enchantlevels')

#The game's, as far as this needs them. Never shipped.
PLATE_STUB = '/Game/UI/Inventory/Inspector2/UMG_EnchantmentBigLevelPlate'
BASE_STUB = '/Game/UI/Inventory/Enchantment2/UMG_EnchantmentWidgetBase'
SELECTED_STUB = '/Game/UI/Inventory/Enchantment2/UMG_EnchantmentSelectedWidget'
SCROLL_STUB = '/Game/UI/Inventory/Inspector2/UMG_EnchantmentInspectorContents2'
SMALL_STUB = '/Game/UI/Inventory/Inspector2/UMG_EnchantmentSmallLevelPlate'
TIERS_STUB = '/Game/UI/Inventory/Inspector2/UMG_EnchantmentLevelBreakdown'
TIERS = 5
BUTTON_STUB = '/Game/UI/Inventory/Inspector/UMG_TextButtonEnchant'
TEXT_BUTTON_STUB = '/Game/UI/Common/Button/Types/UMG_TextButton'
COST = {(4, False): 5, (4, True): 7, (5, False): 8, (5, True): 10}     # as EnchantLevels.costOf
NO1_FOLDER = '/Game/UI/Materials/Inventory2/Enchantment/Inspector2'     # visual_plate_no1, the game's I

EVERY = 5                                  # frames between looks


def texture(folder, name, ui=True):
    path = '%s/%s' % (folder, name)
    try:
        there = ue.load_object(ue.find_class('Texture2D'), '%s.%s' % (path, name))
    except Exception:
        there = None
    if there is not None:
        return there
    made = ue.import_asset(os.path.join(PICTURES, name + '.png'), folder)
    there = made[0] if isinstance(made, list) else made
    if ui:
        #As the game's numerals: drawn whole, never streamed (a picture only a widget shows would stay
        #at its lowest mip), uncompressed.
        for field, value in (('MipGenSettings', 13), ('CompressionSettings', 7), ('NeverStream', True)):
            try:
                setattr(there, field, value)
            except Exception as problem:
                say('  %s.%s not set: %s' % (name, field, problem))
    keep(there)
    return there


def stub(path, parent, build):
    there = on_disk(path, 'WidgetBlueprint')
    if there is not None:
        say('stub already there: ' + path)
        return there
    factory = WidgetBlueprintFactory()
    factory.ParentClass = parent
    made = factory.factory_create_new(path)
    made.modify()
    build(made)
    ue.blueprint_mark_as_structurally_modified(made)
    ue.compile_blueprint(made)
    keep(made)
    say('stub built: ' + path)
    return made


def member(tree, kind, name):
    widget = kind(name, tree)
    widget.bIsVariable = True
    mcd_ui.put(tree.RootWidget, widget, 0.0, 0.0, 100.0, 100.0)
    return widget


def build_stubs():
    def plate(made):
        member(made.WidgetTree, WidgetSwitcher, 'BadgeLevelSwitcher')
        member(made.WidgetTree, Image, 'BadgeLevel1')
        ue.blueprint_add_member_variable(made, 'Level', 'int', False, '0')

    def base(made):
        ue.blueprint_add_member_variable(made, 'Level', 'int', False, '0')

    def selected(made):
        member(made.WidgetTree, TextBlock, 'EnchantCountLabel')

    def text_button(made):
        typed_variable(made, 'ButtonSurface', PinCategory='object', PinSubCategoryObject=Button)

    text_button_stub = stub(TEXT_BUTTON_STUB, UserWidget, text_button)

    def button(made):
        typed_variable(made, 'costText', PinCategory='object', PinSubCategoryObject=TextBlock)
        typed_variable(made, 'MouseButton', PinCategory='object', PinSubCategoryObject=text_button_stub.GeneratedClass)

    plate_stub = stub(PLATE_STUB, UserWidget, plate)
    base_stub = stub(BASE_STUB, UserWidget, base)
    selected_stub = stub(SELECTED_STUB, base_stub.GeneratedClass, selected)
    button_stub = stub(BUTTON_STUB, UserWidget, button)

    def scroll(made):
        typed_variable(made, 'item', PinCategory='object', PinSubCategoryObject=InventoryItem)
        ue.blueprint_add_member_variable(made, 'index', 'int', False, '0')
        typed_variable(made, 'UpgradeButton', PinCategory='object', PinSubCategoryObject=button_stub.GeneratedClass)
        typed_variable(made, 'UpgradeButtonSwitcher', PinCategory='object', PinSubCategoryObject=WidgetSwitcher)
        typed_variable(made, 'can_upgrade', PinCategory='object', PinSubCategoryObject=Widget)
        typed_variable(made, 'UpgradeMaxedPanel', PinCategory='object', PinSubCategoryObject=Widget)
        typed_variable(made, 'Powerful', PinCategory='object', PinSubCategoryObject=Widget)

    scroll_stub = stub(SCROLL_STUB, UserWidget, scroll)

    def small(made):
        ue.blueprint_add_member_variable(made, 'Level', 'int', False, '0')
        for name in ('NormalSwitcher', 'HighlightSwitcher'):
            typed_variable(made, name, PinCategory='object', PinSubCategoryObject=WidgetSwitcher)
        for name in ('Normal1', 'Highlight1'):
            typed_variable(made, name, PinCategory='object', PinSubCategoryObject=Image)

    def tiers(made):
        ue.blueprint_add_member_variable(made, 'NumberOfLevels', 'int', False, '3')
        typed_variable(made, 'Levels', PinCategory='object', PinSubCategoryObject=PanelWidget)
        typed_variable(made, 'LevelWidgets', PinCategory='object', PinSubCategoryObject=UserWidget, ContainerType=1)
        #The game's own: rows made (NumberOfLevels of them, added to Levels and LevelWidgets), then filled.
        ue.blueprint_add_function(made, 'RecreateCounterWidgets')
        ue.blueprint_add_function(made, 'UpdateData')

    small_stub = stub(SMALL_STUB, UserWidget, small)
    tiers_stub = stub(TIERS_STUB, UserWidget, tiers)
    return (plate_stub.GeneratedClass, base_stub.GeneratedClass, selected_stub.GeneratedClass,
            scroll_stub.GeneratedClass, button_stub.GeneratedClass, small_stub.GeneratedClass, tiers_stub.GeneratedClass,
            text_button_stub.GeneratedClass)


def build_widget(plate_class, base_class, selected_class, scroll_class, button_class, small_class, tiers_class,
                 text_button_class, pictures):
    four, five, one = pictures['four'], pictures['five'], pictures['one']
    there = on_disk(WIDGET, 'WidgetBlueprint')
    if there is not None:
        say('widget already there: ' + WIDGET)
        return there
    factory = WidgetBlueprintFactory()
    factory.ParentClass = UserWidget
    widget = factory.factory_create_new(WIDGET)
    widget.modify()
    widget.WidgetTree.RootWidget.Visibility = 3            # HitTestInvisible: draws nothing, takes no clicks
    ue.blueprint_add_member_variable(widget, 'Count', 'int', False, '0')
    for name in ('Held', 'NowHeld'):
        ue.blueprint_add_member_variable(widget, name, 'bool', False, 'false')
    #The item's data being rewritten (Work, its enchantments WorkEnch), the enchantment as it was (Old)
    #and what the next level costs (Cost), kept before anything changes.
    typed_variable(widget, 'Work', PinCategory='struct', PinSubCategoryObject=ue.find_struct('InventoryItemData'))
    typed_variable(widget, 'WorkEnch', PinCategory='struct', PinSubCategoryObject=ue.find_struct('EnchantmentData'), ContainerType=1)
    typed_variable(widget, 'Old', PinCategory='struct', PinSubCategoryObject=ue.find_struct('EnchantmentData'))
    ue.blueprint_add_member_variable(widget, 'Cost', 'int', False, '0')
    #Always empty: an array set needs an array wired in.
    typed_variable(widget, 'Nothing', PinCategory='object', PinSubCategoryObject=UserWidget, ContainerType=1)
    ue.blueprint_mark_as_structurally_modified(widget)
    ue.compile_blueprint(widget)

    g = Graph(widget)
    tick = event(widget, UserWidget, 'Tick', 0, 0)
    g.loose = [(tick, 'then')]

    #--- the enchantment scrolls: what one shows, what buying the next level costs -------------------
    def each_scroll(body):
        found = g.run(WidgetBlueprintLibrary.GetAllWidgetsOfClass, TopLevelOnly='false')
        pin(found, 'WidgetClass').default_object = scroll_class
        each = g.for_each((found, 'FoundWidgets'))
        g.then(each, 'Exec', 'Completed')
        after = list(g.loose)
        g.loose = [(each, 'LoopBody')]
        body(g.cast(scroll_class, (each, 'Array Element')))
        g.loose = after

    def of_scroll(scroll):
        item = lambda: g.get('item', scroll_class, of=scroll)
        index = lambda: g.get('index', scroll_class, of=scroll)
        entry = lambda: g.breaks('EnchantmentData', g.element(
            (g.breaks('InventoryItemData', g.get('Item', InventoryItem, of=item())), 'Enchantments'), index()))
        level = lambda: (entry(), 'Level')
        button = lambda: g.get('UpgradeButton', scroll_class, of=scroll)
        powerful = lambda: g.call(Widget.IsVisible, self=g.get('Powerful', scroll_class, of=scroll))
        pick = lambda a, b, first: g.call(KismetMathLibrary.SelectInt, A=a, B=b, bPickA=first)
        cost = lambda: pick(pick(COST[(4, True)], COST[(4, False)], powerful()), pick(COST[(5, True)], COST[(5, False)], powerful()),
                            g.math('EqualEqual_IntInt', level(), 3))
        buyable = lambda: g.both(g.valid(item()), g.math('GreaterEqual_IntInt', index(), 0),
                                 g.math('GreaterEqual_IntInt', level(), 3), g.math('LessEqual_IntInt', level(), 4))
        return item, index, entry, level, button, cost, buyable

    def points():
        hero = g.cast(PlayerCharacter, g.call(UserWidget.GetOwningPlayerPawn))
        return g.call(ItemStashComponent.AvailableEnchantmentPoints, self=g.get('ItemStashComponent', PlayerCharacter, of=hero))

    #A press of the UPGRADE button of a scroll at III or IV buys the next level, once a press. Every frame.
    #Its ButtonSurface is asked, not the player's mouse: the open menu takes the mouse from the player.
    def buy_pass(scroll):
        item, index, entry, level, button, cost, buyable = of_scroll(scroll)
        surface = lambda: g.get('ButtonSurface', text_button_class, of=g.get('MouseButton', button_class, of=button()))
        pressed = lambda: g.both(buyable(), g.call(Button.IsPressed, self=surface()))
        g.either(pressed(), lambda: g.setter('NowHeld', 'true'), lambda: None)
        have = points()

        def buy():
            #The item's ReplaceEnchantment only fills an option at level 0 (it returns false for one with
            #points in it). So: the option as it is and the cost, kept; its level set to 0 in the item's
            #data (as the Gems screen writes sockets: a copy, edited, written back); then
            #ReplaceEnchantment with the next level - which writes it, marks the item modified, remakes
            #its enchantments and tells the screens.
            g.setter('Old', g.element((g.breaks('InventoryItemData', g.get('Item', InventoryItem, of=item())), 'Enchantments'), index()))
            g.setter('Cost', cost())
            g.setter('Work', g.get('Item', InventoryItem, of=item()))
            g.setter('WorkEnch', (g.breaks('InventoryItemData', g.get('Work')), 'Enchantments'))
            set_fields(g, 'EnchantmentData', g.element(g.get('WorkEnch'), index()), Level=0)
            set_fields(g, 'InventoryItemData', g.get('Work'), Enchantments=g.get('WorkEnch'))
            g.setter('Item', g.get('Work'), owner=InventoryItem, of=item())
            old = lambda: g.breaks('EnchantmentData', g.get('Old'))
            fresh = g.makes('EnchantmentData', TypeID=(old(), 'TypeID'), Level=g.math('Add_IntInt', (old(), 'Level'), 1),
                            Category=(old(), 'Category'), Source=(old(), 'Source'),
                            InvestedPoints=g.math('Add_IntInt', (old(), 'InvestedPoints'), g.get('Cost')))
            g.run(InventoryItem.ReplaceEnchantment, self=item(), Index=index(), Enchantment=fresh)

        g.either(g.both(pressed(), g.call(KismetMathLibrary.Not_PreBool, A=g.get('Held')),
                        g.math('GreaterEqual_IntInt', have, cost())),
                 buy, lambda: None)

    g.setter('NowHeld', 'false')
    each_scroll(buy_pass)
    g.setter('Held', g.get('NowHeld'))

    g.setter('Count', g.math('Add_IntInt', g.get('Count'), 1))
    now, later = g.branch(g.math('GreaterEqual_IntInt', g.get('Count'), EVERY))
    g.loose = now
    g.setter('Count', 0)

    def picture_on(image, which):
        node = g.run(Image.SetBrushFromTexture, self=image, bMatchSize='false')
        node.node_find_pin('Texture').default_object = which

    #The big badges.
    found = g.run(WidgetBlueprintLibrary.GetAllWidgetsOfClass, TopLevelOnly='false')
    pin(found, 'WidgetClass').default_object = plate_class
    each = g.for_each((found, 'FoundWidgets'))
    g.then(each, 'Exec', 'Completed')
    after = list(g.loose)
    g.loose = [(each, 'LoopBody')]
    plate = g.cast(plate_class, (each, 'Array Element'))
    level = lambda: g.get('Level', plate_class, of=plate)
    first = lambda: g.get('BadgeLevel1', plate_class, of=plate)

    def ours():
        g.run(WidgetSwitcher.SetActiveWidgetIndex, self=g.get('BadgeLevelSwitcher', plate_class, of=plate), Index=0)
        g.either(g.math('EqualEqual_IntInt', level(), 4), lambda: picture_on(first(), four), lambda: picture_on(first(), five))

    g.either(g.math('GreaterEqual_IntInt', level(), 4), ours, lambda: picture_on(first(), one))
    g.loose = after

    #The scrolls: at III or IV, the game's UPGRADE button back, with our cost, dimmed when unaffordable.
    def show_pass(scroll):
        item, index, entry, level, button, cost, buyable = of_scroll(scroll)
        have = points()

        def offer():
            #UpgradeButtonSwitcher's pages: 0 "Max Tier Reached", 1 the button (hidden at max). The button's
            #page, shown; the Max Tier page is left as it is, for when the game shows it again at V.
            g.run(WidgetSwitcher.SetActiveWidget, self=g.get('UpgradeButtonSwitcher', scroll_class, of=scroll), Widget=button())
            g.run(Widget.SetVisibility, self=button(), InVisibility='Visible')
            g.run(Widget.SetIsEnabled, self=button(), bInIsEnabled='true')
            g.run(TextBlock.SetText, self=g.get('costText', button_class, of=button()),
                  InText=g.call(KismetTextLibrary.Conv_IntToText, Value=cost()))
            g.run(Widget.SetRenderOpacity, self=button(), InOpacity=g.call(
                KismetMathLibrary.SelectFloat, A=1.0, B=0.45, bPickA=g.math('GreaterEqual_IntInt', have, cost())))

        g.either(buyable(), offer, lambda: g.run(Widget.SetRenderOpacity, self=button(), InOpacity=1.0))

    each_scroll(show_pass)

    #The upgrade tiers lists: five rows, built by the game the next time each fills.
    found = g.run(WidgetBlueprintLibrary.GetAllWidgetsOfClass, TopLevelOnly='false')
    pin(found, 'WidgetClass').default_object = tiers_class
    each = g.for_each((found, 'FoundWidgets'))
    g.then(each, 'Exec', 'Completed')
    after = list(g.loose)
    g.loose = [(each, 'LoopBody')]
    tiers = g.cast(tiers_class, (each, 'Array Element'))
    remake = ue.find_object(tiers_class.get_path_name() + ':RecreateCounterWidgets')
    refill = ue.find_object(tiers_class.get_path_name() + ':UpdateData')
    if remake is None or refill is None:
        raise Exception('the tiers stub has no RecreateCounterWidgets / UpdateData')

    def rebuild():
        g.setter('NumberOfLevels', TIERS, owner=tiers_class, of=tiers)
        g.run(PanelWidget.ClearChildren, self=g.get('Levels', tiers_class, of=tiers))
        g.setter('LevelWidgets', g.get('Nothing'), owner=tiers_class, of=tiers)
        g.run(remake, self=tiers)
        g.run(refill, self=tiers)

    g.either(g.math('NotEqual_IntInt', g.get('NumberOfLevels', tiers_class, of=tiers), TIERS), rebuild, lambda: None)
    g.loose = after

    #Their rows' small badges: IV and V as the big badge has them, the game's I back below IV.
    found = g.run(WidgetBlueprintLibrary.GetAllWidgetsOfClass, TopLevelOnly='false')
    pin(found, 'WidgetClass').default_object = small_class
    each = g.for_each((found, 'FoundWidgets'))
    g.then(each, 'Exec', 'Completed')
    after = list(g.loose)
    g.loose = [(each, 'LoopBody')]
    badge = g.cast(small_class, (each, 'Array Element'))
    small_level = lambda: g.get('Level', small_class, of=badge)
    normal = lambda: g.get('Normal1', small_class, of=badge)
    hover = lambda: g.get('Highlight1', small_class, of=badge)

    def small_ours():
        for switcher in ('NormalSwitcher', 'HighlightSwitcher'):
            g.run(WidgetSwitcher.SetActiveWidgetIndex, self=g.get(switcher, small_class, of=badge), Index=0)
        g.either(g.math('EqualEqual_IntInt', small_level(), 4),
                 lambda: (picture_on(normal(), pictures['small_four']), picture_on(hover(), pictures['small_four_hover'])),
                 lambda: (picture_on(normal(), pictures['small_five']), picture_on(hover(), pictures['small_five_hover'])))

    g.either(g.math('GreaterEqual_IntInt', small_level(), 4), small_ours,
             lambda: (picture_on(normal(), pictures['small_one']), picture_on(hover(), pictures['small_one_hover'])))
    g.loose = after

    #The icons under an item.
    found = g.run(WidgetBlueprintLibrary.GetAllWidgetsOfClass, TopLevelOnly='false')
    pin(found, 'WidgetClass').default_object = selected_class
    each = g.for_each((found, 'FoundWidgets'))
    g.then(each, 'Exec', 'Completed')
    after = list(g.loose)
    g.loose = [(each, 'LoopBody')]
    icon = g.cast(selected_class, (each, 'Array Element'))
    level = lambda: g.get('Level', base_class, of=icon)
    label = lambda: g.get('EnchantCountLabel', selected_class, of=icon)

    def words(text):
        g.run(TextBlock.SetText, self=label(), InText=g.call(KismetTextLibrary.Conv_StringToText, InString=text))

    g.either(g.math('GreaterEqual_IntInt', level(), 4),
             lambda: g.either(g.math('EqualEqual_IntInt', level(), 4), lambda: words('IV'), lambda: words('V')),
             lambda: None)
    g.loose = after + later
    ue.compile_blueprint(widget)
    keep(widget)
    say('widget built: ' + WIDGET)
    return widget


def build_actor(widget):
    there = on_disk(ACTOR, 'Blueprint')
    if there is not None:
        say('actor already there: ' + ACTOR)
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
    set_default(shown, 'ZOrder', '-10')
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
    say('actor built: ' + ACTOR)
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
    pictures = {
        'four': texture(NUMERALS, 'T_MCDRebornEnchantPlateIV'),
        'five': texture(NUMERALS, 'T_MCDRebornEnchantPlateV'),
        'small_four': texture(NUMERALS, 'T_MCDRebornEnchantSmallIV'),
        'small_four_hover': texture(NUMERALS, 'T_MCDRebornEnchantSmallIVHover'),
        'small_five': texture(NUMERALS, 'T_MCDRebornEnchantSmallV'),
        'small_five_hover': texture(NUMERALS, 'T_MCDRebornEnchantSmallVHover'),
        #Stand-ins for the game's own I's, which a badge gets back below IV. Never shipped.
        'one': texture(NO1_FOLDER, 'visual_plate_no1'),
        'small_one': texture(NO1_FOLDER, 'level_1_normal_text'),
        'small_one_hover': texture(NO1_FOLDER, 'level_1_hover_text'),
    }
    plate_class, base_class, selected_class, scroll_class, button_class, small_class, tiers_class, text_button_class = build_stubs()
    widget = build_widget(plate_class, base_class, selected_class, scroll_class, button_class, small_class, tiers_class,
                          text_button_class, pictures)
    actor = build_actor(widget)
    for path in LEVELS:
        build_level(path, actor)
    say('enchant levels done - cook, then carry the levels, the actor, the widget and the numerals')


if __name__ == '__main__':
    main()
