"""
The talent tree screen: P in the Camp or a mission. A passive tree in the manner of Path of Exile -
drag it about with the mouse, zoom with the wheel, click a lit node to take it. Points come from the
hero's level; a reset in the Camp gives them all back for emeralds.

The tree itself - nodes, where they sit, links, what each does - is Talents.json, written by
talent_tree.py and carried by MCD Reborn too, which registers the effects and tells the plugin.

What is taken is kept in the character's own save, as the bits of a few hidden currencies
(MCDR_Talents0.., 30 nodes to one) with MCDR_TalentSpent counting them: the wallet is per character,
saved by the game, and readable both here (Balance) and by the plugin, which applies the effects.

What it makes:

    /Game/MCDReborn/UI/Talents/T_Talent*         node rings and the link line
    /Game/MCDReborn/UI/UMG_MCDRebornTalents      the screen
    /Game/MCDReborn/Actors/BP_MCDRebornTalents   puts it up, takes it down with its level
    /Game/MCDReborn/Lobby/Talents, Ingame/Talents

    UE4Editor-Cmd.exe <project>.uproject -run=Py <this file> -unattended -nopause -nosplash

Delete the widget, actor and levels first to rebuild them.
"""

import json
import math
import os
import sys

HERE = os.environ.get('MCDREBORN_LOADER', r'C:/Users/Gaming/RiderProjects/MCDSaveEditReborn/Tools/loader')
sys.path.insert(0, HERE)

import unreal_engine as ue
import mcd_ui
from mcd_ui import (say, keep, on_disk, pin, link, set_default, event, put, place, label, fill,
                    stub_font, INK, GOLD, DIM, SHADOW, TITLE_FONT, TITLE_FACE, BODY_FONT, BODY_FACE)
import build_gems
from build_gems import Graph, typed_variable, image, CLEAR, DEBOUNCE, FOUND_WITH

from unreal_engine.classes import (
    Actor,
    Button,
    CanvasPanel,
    GameplayStatics,
    Image,
    KismetMathLibrary,
    KismetStringLibrary,
    KismetSystemLibrary,
    KismetTextLibrary,
    PlayerCharacter,
    PlayerController,
    TextBlock,
    UserWidget,
    WalletComponent,
    Widget,
    WidgetBlueprintFactory,
    WidgetBlueprintLibrary,
    WidgetLayoutLibrary,
    WorldFactory,
    K2Node_Self,
)
from unreal_engine.structs import SlateBrush, Vector2D, LinearColor, WidgetTransform, Margin

WIDGET = '/Game/MCDReborn/UI/UMG_MCDRebornTalents'
ACTOR = '/Game/MCDReborn/Actors/BP_MCDRebornTalents'
LEVELS = ['/Game/MCDReborn/Lobby/Talents', '/Game/MCDReborn/Ingame/Talents']
ICONS = '/Game/MCDReborn/UI/Talents'
PICTURES = os.path.join(HERE, 'talents')
TREE_JSON = os.path.join(HERE, '..', '..', 'MCDSaveEdit', 'Logic', 'Talents.json')

KEY = 'P'
STORE = 'MCDR_Talents%d'
SPENT = 'MCDR_TalentSpent'

#The tree's canvas, and where its middle is: node coordinates are measured from it.
TREE_W, TREE_H = 1700.0, 1100.0
TREE_MID = (TREE_W / 2.0, TREE_H / 2.0)
HEADER_H = 80.0
SIZES = {'start': 40.0, 'small': 28.0, 'notable': 44.0, 'keystone': 62.0}
PICTURE = {'start': 'T_TalentStart', 'small': 'T_TalentSmall', 'notable': 'T_TalentNotable',
           'keystone': 'T_TalentKeystone'}
LINE = 4.0
ZOOM_MIN, ZOOM_MAX, ZOOM_STEP = 0.45, 2.2, 1.15

TAKEN = '(R=1.000000,G=0.720000,B=0.220000,A=1.000000)'
OPEN_NODE = '(R=0.950000,G=0.950000,B=0.950000,A=1.000000)'
LOCKED = '(R=0.270000,G=0.270000,B=0.310000,A=1.000000)'
LINK_TAKEN = '(R=1.000000,G=0.700000,B=0.200000,A=1.000000)'
LINK_HALF = '(R=0.520000,G=0.480000,B=0.380000,A=1.000000)'
LINK_OFF = '(R=0.160000,G=0.160000,B=0.190000,A=1.000000)'
BACK = LinearColor(R=0.0060, G=0.0070, B=0.0110, A=0.965)
HEADER = LinearColor(R=0.0120, G=0.0130, B=0.0180, A=1.0)

SECTION_TITLES = {'Warrior': -90, 'Ranger': -30, 'Mystic': 30, 'Guardian': 90, 'Rogue': 150, 'Shepherd': 210}


def load_tree():
    with open(TREE_JSON, encoding='utf-8') as source:
        return json.load(source)


def import_pictures():
    found = {}
    for name in ['T_TalentSmall', 'T_TalentNotable', 'T_TalentKeystone', 'T_TalentStart', 'T_TalentLine']:
        path = '%s/%s' % (ICONS, name)
        there = on_disk(path, 'Texture2D')
        if there is None:
            made = ue.import_asset(os.path.join(PICTURES, name + '.png'), ICONS)
            there = made[0] if isinstance(made, list) else made
            for field, value in (('Filter', 0), ('MipGenSettings', 13), ('CompressionSettings', 7), ('NeverStream', True)):
                try:
                    setattr(there, field, value)
                except Exception as problem:
                    say('  %s.%s not set: %s' % (name, field, problem))
            keep(there)
        found[name] = there
    return found


def node_button(tree, name, ring):
    """A node: its ring and nothing else - no backing, no padding squeezing the ring."""
    made = Button(name, tree)
    made.bIsVariable = True
    made.BackgroundColor = CLEAR
    slot = made.AddChild(ring)
    try:
        slot.Padding = Margin(Left=0.0, Top=0.0, Right=0.0, Bottom=0.0)
        slot.HorizontalAlignment = 0
        slot.VerticalAlignment = 0
    except Exception as problem:
        say('could not fill %s: %s' % (name, problem))
    return made


def tooltip(node):
    kind = {'start': '', 'small': '', 'notable': 'Notable. ', 'keystone': 'Keystone. '}[node['kind']]
    return '%s\n%s%s' % (node['name'], kind, node['text'])


#--- the tree ------------------------------------------------------------------------------------------

def build_tree(widget, pictures, tree):
    widget.modify()
    wt = widget.WidgetTree
    title_face = stub_font(TITLE_FONT)
    body_face = stub_font(BODY_FONT)
    wt.RootWidget.Visibility = 4                                    # SelfHitTestInvisible

    panel = CanvasPanel('Panel', wt)
    panel.bIsVariable = True
    panel.Visibility = 1                                           # Collapsed until P
    place(wt.RootWidget, panel, 0.0, 0.0, 1920.0, 1080.0)
    put(panel, fill(wt, 'Backing', BACK), 0.0, 0.0, 1920.0, 1080.0)

    #Pressing on empty space is a drag: a clear button over everything behind the tree.
    drag = Button('DragArea', wt)
    drag.bIsVariable = True
    drag.BackgroundColor = CLEAR
    put(panel, drag, 0.0, HEADER_H, 1920.0, 1080.0 - HEADER_H)

    canvas = CanvasPanel('Tree', wt)
    canvas.bIsVariable = True
    canvas.Visibility = 4
    put(panel, canvas, 960.0 - TREE_MID[0], HEADER_H + (1080.0 - HEADER_H) / 2.0 - TREE_MID[1], TREE_W, TREE_H)

    nodes = tree['nodes']
    #Links first, so the rings sit over them.
    for number, (a, b) in enumerate(tree['edges']):
        ax, ay = nodes[a]['x'] + TREE_MID[0], nodes[a]['y'] + TREE_MID[1]
        bx, by = nodes[b]['x'] + TREE_MID[0], nodes[b]['y'] + TREE_MID[1]
        length = math.hypot(bx - ax, by - ay)
        angle = math.degrees(math.atan2(by - ay, bx - ax))
        line = image(wt, 'Edge%03d' % number, pictures['T_TalentLine'], LINE, variable=True)
        line.Visibility = 3
        line.RenderTransform = WidgetTransform(Translation=Vector2D(X=0.0, Y=0.0), Scale=Vector2D(X=1.0, Y=1.0),
                                               Shear=Vector2D(X=0.0, Y=0.0), Angle=angle)
        put(canvas, line, (ax + bx) / 2.0 - length / 2.0, (ay + by) / 2.0 - LINE / 2.0, length, LINE)

    for node in nodes:
        size = SIZES[node['kind']]
        x, y = node['x'] + TREE_MID[0] - size / 2.0, node['y'] + TREE_MID[1] - size / 2.0
        ring = image(wt, 'Pic%02d' % node['index'], pictures[PICTURE[node['kind']]], size, variable=True)
        button = node_button(wt, 'Node%02d' % node['index'], ring)
        button.ToolTipText = tooltip(node)
        put(canvas, button, x, y, size, size)

    for section, angle in SECTION_TITLES.items():
        a = math.radians(angle)
        x = 690.0 * math.cos(a) * 1.18 + TREE_MID[0]
        y = 640.0 * math.sin(a) * 0.80 + TREE_MID[1]
        title = label(wt, 'Section' + section, section.upper(), DIM, title_face, 22, typeface=TITLE_FACE)
        title.Visibility = 3                                        # a drag starting on it still drags
        put(canvas, title, x - 90.0, y - 16.0, 180.0, 32.0)

    #The header, over the tree.
    put(panel, fill(wt, 'HeaderBack', HEADER), 0.0, 0.0, 1920.0, HEADER_H)
    put(panel, label(wt, 'Title', 'TALENTS', GOLD, title_face, 32, typeface=TITLE_FACE, shadow=SHADOW),
        40.0, 16.0, 300.0, 48.0)
    put(panel, label(wt, 'Points', 'Points: 0', INK, body_face, 22, variable=True, typeface=BODY_FACE),
        300.0, 24.0, 420.0, 34.0)
    put(panel, label(wt, 'Note', 'Drag to move - wheel to zoom - click a lit node to take it', DIM, body_face, 16,
                     variable=True, typeface=BODY_FACE), 720.0, 28.0, 640.0, 26.0)
    for name, caption, left, wide in (('ZoomOut', '-', 1380.0, 44.0), ('ZoomIn', '+', 1430.0, 44.0),
                                      ('Reset', 'RESET  %d' % tree['respec'], 1490.0, 190.0),
                                      ('Close', 'CLOSE', 1700.0, 180.0)):
        button = mcd_ui.button(wt, name, caption, body_face, 18, typeface=BODY_FACE, centred=True)
        if name == 'Reset':
            button.ToolTipText = 'Give every point back, for %d emeralds. Only in the Camp.' % tree['respec']
        put(panel, button, left, 20.0, wide, 42.0)


def variables(widget, tree):
    for name, kind, default in (('Busy', 'int', '0'), ('Open', 'bool', 'false'), ('Dragging', 'bool', 'false'),
                                ('Zoom', 'float', '1.0'), ('Spent', 'int', '0'), ('Earned', 'int', '0'),
                                ('Free', 'int', '0'), ('InMission', 'bool', 'false')):
        ue.blueprint_add_member_variable(widget, name, kind, False, default)
    for store in range(tree['stores']):
        ue.blueprint_add_member_variable(widget, 'M%d' % store, 'int', False, '0')
    for node in tree['nodes']:
        ue.blueprint_add_member_variable(widget, 'A%02d' % node['index'], 'bool', False,
                                         'true' if node['kind'] == 'start' else 'false')
    vector = dict(PinCategory='struct', PinSubCategoryObject=ue.find_struct('Vector2D'))
    typed_variable(widget, 'Last', **vector)
    typed_variable(widget, 'Offset', **vector)
    ue.blueprint_mark_as_structurally_modified(widget)
    ue.compile_blueprint(widget)


#--- the graph -----------------------------------------------------------------------------------------

def build_graph(widget, tree):
    g = Graph(widget)
    tick = event(widget, UserWidget, 'Tick', 0, 0)
    g.loose = [(tick, 'then')]
    nodes = tree['nodes']
    links = {n['index']: [] for n in nodes}
    for a, b in tree['edges']:
        links[a].append(b)
        links[b].append(a)

    g.setter('Busy', g.call(KismetMathLibrary.Max, A=g.math('Subtract_IntInt', g.get('Busy'), 1), B=0))

    player = lambda: g.call(UserWidget.GetOwningPlayer)

    def key(name):
        pressed = g.call(PlayerController.WasInputKeyJustPressed, self=player(), Key=None)
        set_default(pressed[0], 'Key', name)
        return pressed

    #P opens and closes; Esc and the close button close.
    g.either(key(KEY), lambda: g.setter('Open', g.call(KismetMathLibrary.Not_PreBool, A=g.get('Open'))), lambda: None)
    g.either(g.math('BooleanOR', key('Escape'), g.call(Button.IsPressed, self=g.get('Close'))),
             lambda: g.setter('Open', 'false'), lambda: None)
    g.either(g.get('Open'), lambda: g.shows('Panel', build_gems.SHOWN), lambda: g.shows('Panel', build_gems.COLLAPSED))
    shown, hidden = g.branch(g.get('Open'))
    g.loose = shown

    pawn = g.call(UserWidget.GetOwningPlayerPawn)
    g.hero = g.cast(PlayerCharacter, pawn)

    #--- what is taken, and what is left to spend ---------------------------------------------
    g.section()
    for store in range(tree['stores']):
        g.setter('M%d' % store, g.call(WalletComponent.Balance, self=g.wallet(), Type=g.currency(STORE % store)))
    g.setter('Spent', g.call(WalletComponent.Balance, self=g.wallet(), Type=g.currency(SPENT)))
    level = g.call(PlayerCharacter.GetCharacterLevel, self=g.hero)
    g.setter('Earned', g.math('Divide_IntInt', g.call(KismetMathLibrary.Min, A=level, B=tree['points']['cap'] * tree['points']['perLevels']),
                              tree['points']['perLevels']))
    g.setter('Free', g.math('Subtract_IntInt', g.get('Earned'), g.get('Spent')))
    said = g.call(KismetStringLibrary.Concat_StrStr,
                  A=g.call(KismetStringLibrary.Concat_StrStr, A='Points: ',
                           B=g.call(KismetStringLibrary.Conv_IntToString, InInt=g.get('Free'))),
                  B=g.call(KismetStringLibrary.Concat_StrStr, A=' of ',
                           B=g.call(KismetStringLibrary.Conv_IntToString, InInt=g.get('Earned'))))
    g.run(TextBlock.SetText, self=g.get('Points'), InText=g.call(KismetTextLibrary.Conv_StringToText, InString=said))

    for node in nodes:
        if node['kind'] == 'start':
            continue
        bits = g.math('Divide_IntInt', g.get('M%d' % node['store']), 2 ** node['bit'])
        g.setter('A%02d' % node['index'], g.math('EqualEqual_IntInt', g.math('Percent_IntInt', bits, 2), 1))

    def taken(i):
        return g.get('A%02d' % i)

    def open_to(i):
        near = None
        for j in links[i]:
            near = taken(j) if near is None else g.math('BooleanOR', near, taken(j))
        return g.both(near, g.call(KismetMathLibrary.Not_PreBool, A=taken(i)))

    #--- colours: the rings, then the links -------------------------------------------------------
    g.section()
    for node in nodes:
        i = node['index']
        tint = lambda colour, i=i: g.run(Image.SetColorAndOpacity, self=g.get('Pic%02d' % i), InColorAndOpacity=colour)
        if node['kind'] == 'start':
            tint(TAKEN)
            continue
        g.either(taken(i), lambda tint=tint: tint(TAKEN),
                 lambda i=i, tint=tint: g.either(open_to(i), lambda: tint(OPEN_NODE), lambda: tint(LOCKED)))
    g.section()
    for number, (a, b) in enumerate(tree['edges']):
        tint = lambda colour, number=number: g.run(Image.SetColorAndOpacity, self=g.get('Edge%03d' % number),
                                                   InColorAndOpacity=colour)
        g.either(g.both(taken(a), taken(b)), lambda tint=tint: tint(LINK_TAKEN),
                 lambda a=a, b=b, tint=tint: g.either(g.math('BooleanOR', taken(a), taken(b)),
                                                     lambda: tint(LINK_HALF), lambda: tint(LINK_OFF)))

    #--- taking a node -----------------------------------------------------------------------------
    for node in nodes:
        if node['kind'] == 'start':
            continue
        i = node['index']
        g.section()
        fired, carry = g.branch(g.both(g.call(Button.IsPressed, self=g.get('Node%02d' % i)),
                                       g.math('LessEqual_IntInt', g.get('Busy'), 0)))
        g.loose = fired

        def take(node=node):
            g.run(WalletComponent.ClientAdd, self=g.wallet(), Type=g.currency(STORE % node['store']),
                  Amount=2 ** node['bit'], reason='Default')
            g.run(WalletComponent.ClientAdd, self=g.wallet(), Type=g.currency(SPENT), Amount=1, reason='Default')
            g.says('Note', '%s taken.' % node['name'])

        def why_not(i=i):
            g.either(taken(i), lambda: g.says('Note', 'Already taken.'),
                     lambda: g.either(g.math('LessEqual_IntInt', g.get('Free'), 0),
                                      lambda: g.says('Note', 'No points left - they come with levels.'),
                                      lambda: g.says('Note', 'Take a node next to one you have first.')))

        g.either(g.both(open_to(i), g.math('Greater_IntInt', g.get('Free'), 0)), take, why_not)
        g.setter('Busy', DEBOUNCE)
        g.loose = carry

    #--- the reset, in the Camp -------------------------------------------------------------------
    g.section()
    fired, carry = g.branch(g.both(g.call(Button.IsPressed, self=g.get('Reset')), g.math('LessEqual_IntInt', g.get('Busy'), 0)))
    g.loose = fired

    def reset():
        for store in range(tree['stores']):
            g.run(WalletComponent.Deduct, self=g.wallet(), Type=g.currency(STORE % store), Amount=g.get('M%d' % store))
        g.run(WalletComponent.Deduct, self=g.wallet(), Type=g.currency(SPENT), Amount=g.get('Spent'))
        g.run(WalletComponent.Deduct, self=g.wallet(), Type=g.currency(FOUND_WITH), Amount=tree['respec'])
        g.says('Note', 'Every point given back.')

    emeralds = g.call(WalletComponent.Balance, self=g.wallet(), Type=g.currency(FOUND_WITH))
    g.either(g.get('InMission'), lambda: g.says('Note', 'Talents can only be reset in the Camp.'),
             lambda: g.either(g.math('LessEqual_IntInt', g.get('Spent'), 0), lambda: g.says('Note', 'Nothing to reset.'),
                              lambda: g.either(g.math('GreaterEqual_IntInt', emeralds, tree['respec']), reset,
                                               lambda: g.says('Note', 'A reset costs %d emeralds.' % tree['respec']))))
    g.setter('Busy', DEBOUNCE)
    g.loose = carry

    #--- dragging and zooming -----------------------------------------------------------------------
    g.section()
    #Not a pure node in 4.22: it has to sit on the line, and its answer is read from there.
    def mouse():
        return (g.run(WidgetLayoutLibrary.GetMousePositionOnViewport), 'ReturnValue')

    def held():
        def move():
            now = mouse()
            g.setter('Offset', g.call(KismetMathLibrary.Add_Vector2DVector2D, A=g.get('Offset'),
                                      B=g.call(KismetMathLibrary.Subtract_Vector2DVector2D, A=now, B=g.get('Last'))))
            g.setter('Last', now)

        def begin():
            g.setter('Dragging', 'true')
            g.setter('Last', mouse())
        g.either(g.get('Dragging'), move, begin)

    g.either(g.call(Button.IsPressed, self=g.get('DragArea')), held, lambda: g.setter('Dragging', 'false'))

    bigger = g.math('BooleanOR', key('MouseScrollUp'),
                    g.both(g.call(Button.IsPressed, self=g.get('ZoomIn')), g.math('LessEqual_IntInt', g.get('Busy'), 0)))
    smaller = g.math('BooleanOR', key('MouseScrollDown'),
                     g.both(g.call(Button.IsPressed, self=g.get('ZoomOut')), g.math('LessEqual_IntInt', g.get('Busy'), 0)))
    g.either(bigger, lambda: (g.setter('Zoom', g.call(KismetMathLibrary.FMin, A=g.math('Multiply_FloatFloat', g.get('Zoom'), ZOOM_STEP), B=ZOOM_MAX)),
                              g.setter('Busy', 8)), lambda: None)
    g.either(smaller, lambda: (g.setter('Zoom', g.call(KismetMathLibrary.FMax, A=g.math('Divide_FloatFloat', g.get('Zoom'), ZOOM_STEP), B=ZOOM_MIN)),
                               g.setter('Busy', 8)), lambda: None)
    g.run(Widget.SetRenderTranslation, self=g.get('Tree'), Translation=g.get('Offset'))
    g.run(Widget.SetRenderScale, self=g.get('Tree'), Scale=g.call(KismetMathLibrary.MakeVector2D, X=g.get('Zoom'), Y=g.get('Zoom')))

    g.loose += hidden
    ue.compile_blueprint(widget)
    say('talent graph built')


def build_widget(pictures, tree):
    there = on_disk(WIDGET, 'WidgetBlueprint')
    if there is not None:
        say('talents already there: ' + WIDGET)
        return there
    factory = WidgetBlueprintFactory()
    factory.ParentClass = UserWidget
    widget = factory.factory_create_new(WIDGET)
    build_tree(widget, pictures, tree)
    variables(widget, tree)
    build_graph(widget, tree)
    keep(widget)
    say('talents built: ' + WIDGET)
    return widget


def build_actor(widget):
    there = on_disk(ACTOR, 'Blueprint')
    if there is not None:
        say('talents actor already there: ' + ACTOR)
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
    set_default(shown, 'ZOrder', '9998')
    link(made, 'then', shown, 'execute')

    ue.blueprint_add_member_variable(actor, 'Screen', build_gems.EdGraphPinType(
        PinCategory='object', PinSubCategoryObject=widget.GeneratedClass), False, '')
    ue.blueprint_mark_as_structurally_modified(actor)
    ue.compile_blueprint(actor)
    ended = event(actor, Actor, 'ReceiveEndPlay', 0, 800)
    gone = page.graph_add_node_call_function(Widget.RemoveFromParent, 600, 800)
    link(page.graph_add_node_variable_get('Screen', None, 350, 900), 'Screen', gone, 'self')
    link(ended, 'then', gone, 'execute')

    screen = page.graph_add_node_dynamic_cast(widget.GeneratedClass, 1100, 0)
    link(made, 'ReturnValue', screen, 'Object')
    mcd_ui.reconstruct(screen)
    link(shown, 'then', screen, 'execute')
    where = page.graph_add_node_call_function(KismetSystemLibrary.GetPathName, 900, 300)
    link(page.graph_add_node(K2Node_Self, 700, 300), 'self', where, 'Object')
    ingame = page.graph_add_node_call_function(KismetStringLibrary.Contains, 1100, 300)
    link(where, 'ReturnValue', ingame, 'SearchIn')
    set_default(ingame, 'Substring', '/Ingame/')
    told_it = page.graph_add_node_variable_set('InMission', widget.GeneratedClass, 1400, 0)
    link(screen, mcd_ui.as_pin(screen), told_it, 'self')
    link(ingame, 'ReturnValue', told_it, 'InMission')
    link(screen, 'then', told_it, 'execute')
    kept = page.graph_add_node_variable_set('Screen', None, 1700, 0)
    link(screen, mcd_ui.as_pin(screen), kept, 'Screen')
    link(told_it, 'then', kept, 'execute')

    ue.compile_blueprint(actor)
    keep(actor)
    say('talents actor built: ' + ACTOR)
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
    tree = load_tree()
    say('building the talent tree: %d nodes, %d links' % (len(tree['nodes']), len(tree['edges'])))
    pictures = import_pictures()
    widget = build_widget(pictures, tree)
    actor = build_actor(widget)
    for path in LEVELS:
        build_level(path, actor)
    say('talents done - cook, then carry the levels, the actor, the screen and the pictures')


if __name__ == '__main__':
    main()
