"""
Enchantment levels IV and V, as the game shows them. The game keeps an enchantment above III and its
effects keep scaling (Celerity IV measured live: -46% artifact cooldown), but every place it draws a
level only knows I-III:

    UMG_EnchantmentBigLevelPlate    the badge on the big icon: a switcher of three pictures (I, II,
                                    III); at 4 it stays on its first - "I"
    UMG_EnchantmentSelectedWidget   the enchantment icons under an item: a text from GetLevelNumeral,
                                    which has nothing past III - blank

So a widget of ours looks every few frames: a badge at 4 or 5 gets our IV or V picture in its first
slot, selected, and gets the game's own I back as soon as it shows 1-3 again; an icon at 4 or 5 gets
"IV" or "V" as its text (the game writes its own again whenever the level changes).

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
from build_gems import Graph, typed_variable

from unreal_engine.classes import (
    Actor,
    GameplayStatics,
    Image,
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

    plate_stub = stub(PLATE_STUB, UserWidget, plate)
    base_stub = stub(BASE_STUB, UserWidget, base)
    selected_stub = stub(SELECTED_STUB, base_stub.GeneratedClass, selected)
    return plate_stub.GeneratedClass, base_stub.GeneratedClass, selected_stub.GeneratedClass


def build_widget(plate_class, base_class, selected_class, four, five, one):
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
    ue.blueprint_mark_as_structurally_modified(widget)
    ue.compile_blueprint(widget)

    g = Graph(widget)
    tick = event(widget, UserWidget, 'Tick', 0, 0)
    g.loose = [(tick, 'then')]
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
    four = texture(NUMERALS, 'T_MCDRebornEnchantPlateIV')
    five = texture(NUMERALS, 'T_MCDRebornEnchantPlateV')
    one = texture(NO1_FOLDER, 'visual_plate_no1')           # stand-in for the game's; never ships
    plate_class, base_class, selected_class = build_stubs()
    widget = build_widget(plate_class, base_class, selected_class, four, five, one)
    actor = build_actor(widget)
    for path in LEVELS:
        build_level(path, actor)
    say('enchant levels done - cook, then carry the levels, the actor, the widget and the numerals')


if __name__ == '__main__':
    main()
