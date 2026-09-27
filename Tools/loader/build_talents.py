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
    /Game/MCDReborn/Actors/BP_MCDRebornTalentsProp, Lobby/TalentsProp, UI/UMG_MCDRebornSign_Talents
                                                 the TALENTS prop in the Camp, which opens it too

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
    PanelWidget,
    EdGraph,
    K2Node_MacroInstance,
    WidgetBlueprintFactory,
    WidgetBlueprintLibrary,
    WidgetLayoutLibrary,
    WorldFactory,
    K2Node_Self,
)
from unreal_engine.structs import SlateBrush, Vector2D, LinearColor, WidgetTransform, Margin, GraphReference

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
TREE_W, TREE_H = 5400.0, 5400.0                    # the whole tree, round its middle; the rest is zoom
TREE_MID = (TREE_W / 2.0, TREE_H / 2.0)
REGION_TITLE_R = 2480.0                             # the region names, outside the outer ring
ARC_PIECE = 40.0
FRESH = 2                                           # frames everything is coloured after a change
ICON_CACHE = {}
GROUP_SIZE = 24                                     # buttons a hover group holds
HEADER_H = 80.0
SIZES = {'start': 52.0, 'small': 40.0, 'notable': 58.0, 'keystone': 80.0}
ICON_SHARE = 0.72                                   # of the ring, for the picture inside it

#What each node shows inside its ring: one of the game's own enchantment pictures, chosen for what
#the node does. Referred to by path (a stand-in here, never shipped), so the game's own draws.
ENCHANTMENTS = '/Game/Components/Enchantments'
ICON_OF = {
    'MeleeDamageBoost': 'Sharpness', 'MeleeAttackSpeedBoost': 'Rampaging', 'RangedDamageBoost': 'Power',
    'ItemDamageBoost': 'Thundering', 'ItemCooldownDecrease': 'Celerity', 'SoulGatheringBoost': 'SoulSiphon',
    'DodgeSpeedIncrease': 'Swiftfooted', 'MoveSpeedAura': 'SpeedSynergy', 'AllyDamageBoost': 'Frenzied',
    'DamageAbsorption': 'Protection', 'LifeStealAura': 'Leeching', 'HealingAura': 'HealthSynergy',
    'MissChance': 'Deflecting', 'TeleportChance': 'Unchanting',
    'Heavyweight': 'Committed', 'IncreasedArrowBundleSize': 'MultiShot', 'DodgeGhostForm': 'EnigmaResonatorMelee',
    'ItemCooldownReset': 'Recycler', 'EnvironmentalProtection': 'Shielding', 'EmeraldShield': 'Prospector',
    'DodgeInvulnerability': 'Swirling', 'InstantTransmission': 'Gravity', 'AreaHeal': 'PotionFortification',
    'Beekeeper': 'ChainReaction', 'PetBat': 'Weakening', 'DodgeRoot': 'Growing',
    'start': 'Infinity',
}
#The picture's tint: full taken, a little dim when it can be taken, dark while locked.
ICON_TAKEN = '(R=1.000000,G=1.000000,B=1.000000,A=1.000000)'
ICON_OPEN = '(R=0.800000,G=0.800000,B=0.800000,A=1.000000)'
ICON_LOCKED = '(R=0.220000,G=0.220000,B=0.250000,A=0.850000)'

#The TALENTS prop in the Camp: the game's soul display case with the Camp's kind of sign over it.
#Clicking near it opens the screen. Where it stands is written into its level again on install.
PROP_ACTOR = '/Game/MCDReborn/Actors/BP_MCDRebornTalentsProp'
PROP_LEVEL = '/Game/MCDReborn/Lobby/TalentsProp'
PROP_LOOKS = '/Game/Decor/Prefabs/_LobbyHouse/Trophy/BP_SoulDisplay'
PROP_SIGN = '/Game/MCDReborn/UI/UMG_MCDRebornSign_Talents'
PROP_WHERE = (19065.0, 7604.0, 11402.0)             # where the user sat
PROP_FACING = 180.0
PROP_REACH = 250.0
SCAN_EVERY = 10
PICTURE = {'start': 'T_TalentStart', 'small': 'T_TalentSmall', 'notable': 'T_TalentNotable',
           'keystone': 'T_TalentKeystone'}
LINE = 4.0
ZOOM_MIN, ZOOM_MAX, ZOOM_STEP, ZOOM_START = 0.16, 1.6, 1.15, 0.5

TAKEN = '(R=1.000000,G=0.720000,B=0.220000,A=1.000000)'
OPEN_NODE = '(R=0.950000,G=0.950000,B=0.950000,A=1.000000)'
LOCKED = '(R=0.270000,G=0.270000,B=0.310000,A=1.000000)'
LINK_TAKEN = '(R=1.000000,G=0.700000,B=0.200000,A=1.000000)'
LINK_HALF = '(R=0.520000,G=0.480000,B=0.380000,A=1.000000)'
LINK_OFF = '(R=0.160000,G=0.160000,B=0.190000,A=1.000000)'
BACK = LinearColor(R=0.0060, G=0.0070, B=0.0110, A=0.965)
HEADER = LinearColor(R=0.0120, G=0.0130, B=0.0180, A=1.0)

GROUP_ART = LinearColor(R=1.0, G=1.0, B=1.0, A=0.07)   # a group's picture behind it, barely there


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


def icon_key(node):
    if node['kind'] == 'start':
        return 'start'
    first = node['effects'][0]
    return first.get('source') or first.get('raw')


def stub_texture(pictures, name):
    """
    The picture for an enchantment: a texture of OUR OWN, never streamed, which MCD Reborn paints
    with the game's picture of that enchantment when it installs Talents (Talents.paintIcons).

    Not the game's texture by path: those stream, and a streamed texture that only a widget shows is
    never asked for its detail - it draws from its smallest mip, a blur of one colour, which is what
    every node but three showed (and the faint group pictures, as grey squares). Nor a copy of the
    game's picture in this build: the art comes from the player's own game, at install.
    """
    if name in ICON_CACHE:
        return ICON_CACHE[name]
    path = '%s/Icons/T_TI_%s' % (ICONS, name)
    #The TEXTURE, by its object path, never on_disk's answer: once the texture is loaded that hands
    #back its package, and a brush given a package draws nothing - every node but the three whose
    #pictures no group had asked for first came out blank.
    try:
        there = ue.load_object(ue.find_class('Texture2D'), '%s.T_TI_%s' % (path, name))
    except Exception:
        there = None
    if there is None:
        made = ue.import_asset(os.path.join(PICTURES, 'icons', 'T_TI_%s.png' % name), ICONS + '/Icons')
        there = made[0] if isinstance(made, list) else made
        #Uncompressed (UserInterface2D), one mip, never streamed: what the painter writes into.
        for field, value in (('MipGenSettings', 13), ('CompressionSettings', 7), ('NeverStream', True)):
            try:
                setattr(there, field, value)
            except Exception as problem:
                say('  %s.%s not set: %s' % (name, field, problem))
        keep(there)
    ICON_CACHE[name] = there
    return there


def tooltip(node):
    kind = {'start': '', 'small': '', 'notable': 'Notable\n', 'keystone': 'Keystone\n'}[node['kind']]
    return '%s\n%s%s' % (node['name'], kind, node.get('text', ''))


def segments(tree, edge):
    """
    A link as straight pieces: one for a straight link, and for an arc (two nodes on one orbit of a
    group) as many as keep it round, a piece every ARC_PIECE or so.
    """
    nodes = tree['nodes']
    a, b = nodes[edge[0]], nodes[edge[1]]
    if len(edge) == 2:
        return [((a['x'], a['y']), (b['x'], b['y']))]
    cx, cy = edge[2], edge[3]
    a0 = math.atan2(a['y'] - cy, a['x'] - cx)
    a1 = math.atan2(b['y'] - cy, b['x'] - cx)
    turn = (a1 - a0 + math.pi) % (2 * math.pi) - math.pi
    r = math.hypot(a['x'] - cx, a['y'] - cy)
    pieces = max(1, int(math.ceil(abs(turn) * r / ARC_PIECE)))
    points = [(cx + r * math.cos(a0 + turn * k / pieces), cy + r * math.sin(a0 + turn * k / pieces)) for k in range(pieces + 1)]
    return list(zip(points, points[1:]))


#--- the tree as data ----------------------------------------------------------------------------------
#
#The screen's logic is LOOPS over arrays, not a copy per node: every node added to a widget's graph
#recompiles its skeleton, and a graph written out node by node (35,000 of them, with a variable per
#ring, icon and link) ran the editor out of objects at 14 GB. So the tree becomes a few arrays of
#numbers, the pictures sit in canvases in node order (child i is node i's), and the graph is a few
#hundred nodes whatever the tree's size.

def tree_data(tree):
    nodes = tree['nodes']
    count = len(nodes)
    always = tree['stores']                     # the start's store: a constant 1, so it is always taken
    store = [always if n['kind'] == 'start' else n['store'] for n in nodes]
    power = [1 if n['kind'] == 'start' else 2 ** n['bit'] for n in nodes]
    near = [[] for _ in range(count)]
    for edge in tree['edges']:
        near[edge[0]].append(edge[1])
        near[edge[1]].append(edge[0])
    adj_start, adj = [], []
    for i in range(count):
        adj_start.append(len(adj))
        adj.extend(near[i])
    adj_start.append(len(adj))
    piece_start, total = [], 0
    for edge in tree['edges']:
        piece_start.append(total)
        total += len(segments(tree, edge))
    piece_start.append(total)
    group_start = list(range(0, count, GROUP_SIZE)) + [count]
    return {
        'NodeStore': store, 'NodePow': power, 'AdjStart': adj_start, 'Adj': adj,
        'EdgeA': [e[0] for e in tree['edges']], 'EdgeB': [e[1] for e in tree['edges']],
        'PieceStart': piece_start, 'GroupStart': group_start,
        'NodeNames': [n['name'] for n in nodes],
        'StoreNames': [STORE % s for s in range(tree['stores'])],
        'pieces': total, 'groups': len(group_start) - 1,
    }


#--- the tree ------------------------------------------------------------------------------------------

def full_canvas(wt, parent, name, visibility):
    made = CanvasPanel(name, wt)
    made.bIsVariable = True
    made.Visibility = visibility
    put(parent, made, 0.0, 0.0, TREE_W, TREE_H)
    return made


def build_tree(widget, pictures, tree, data):
    widget.modify()
    wt = widget.WidgetTree
    title_face = stub_font(TITLE_FONT)
    body_face = stub_font(BODY_FONT)
    wt.RootWidget.Visibility = 4                                    # SelfHitTestInvisible
    mid_x, mid_y = TREE_MID
    nodes = tree['nodes']

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
    put(panel, canvas, 960.0 - mid_x, HEADER_H + (1080.0 - HEADER_H) / 2.0 - mid_y, TREE_W, TREE_H)

    #Behind everything, each group's picture, large and faint - the tree's own background art.
    for number, group in enumerate(tree['groups']):
        size = group['r'] * 1.25
        art = image(wt, 'Art%02d' % number, stub_texture(pictures, ICON_OF[group['icon']]), size)
        art.Visibility = 3
        art.ColorAndOpacity = GROUP_ART
        put(canvas, art, group['x'] + mid_x - size / 2.0, group['y'] + mid_y - size / 2.0, size, size)

    for region in tree['regions']:
        a = math.radians(region['angle'])
        x, y = REGION_TITLE_R * math.cos(a) + mid_x, REGION_TITLE_R * math.sin(a) + mid_y
        title = label(wt, 'Region' + region['name'], region['name'].upper(), DIM, title_face, 44, typeface=TITLE_FACE)
        title.Visibility = 3                                        # a drag starting on it still drags
        try:
            title.Justification = 1
        except Exception:
            pass
        put(canvas, title, x - 200.0, y - 30.0, 400.0, 60.0)

    #The links' pieces, in link order: child k of Pieces is piece k.
    pieces = full_canvas(wt, canvas, 'Pieces', 3)
    number = 0
    for edge in tree['edges']:
        for (ax, ay), (bx, by) in segments(tree, edge):
            ax, ay, bx, by = ax + mid_x, ay + mid_y, bx + mid_x, by + mid_y
            length = math.hypot(bx - ax, by - ay) + 1.5             # a hair over, so an arc's pieces meet
            angle = math.degrees(math.atan2(by - ay, bx - ax))
            line = image(wt, 'Piece%04d' % number, pictures['T_TalentLine'], LINE)
            line.Visibility = 3
            line.RenderTransform = WidgetTransform(Translation=Vector2D(X=0.0, Y=0.0), Scale=Vector2D(X=1.0, Y=1.0),
                                                   Shear=Vector2D(X=0.0, Y=0.0), Angle=angle)
            line.ColorAndOpacity = LinearColor(R=0.16, G=0.16, B=0.19, A=1.0)
            put(pieces, line, (ax + bx) / 2.0 - length / 2.0, (ay + by) / 2.0 - LINE / 2.0, length, LINE)
            number += 1

    #The rings, then the pictures in them, in node order.
    rings = full_canvas(wt, canvas, 'Rings', 3)
    for node in nodes:
        size = SIZES[node['kind']]
        ring = image(wt, 'Ring%03d' % node['index'], pictures[PICTURE[node['kind']]], size)
        ring.ColorAndOpacity = LinearColor(R=0.27, G=0.27, B=0.31, A=1.0)
        put(rings, ring, node['x'] + mid_x - size / 2.0, node['y'] + mid_y - size / 2.0, size, size)
    icons = full_canvas(wt, canvas, 'Icons', 3)
    for node in nodes:
        inner = SIZES[node['kind']] * ICON_SHARE
        icon = image(wt, 'Ico%03d' % node['index'], stub_texture(pictures, ICON_OF[icon_key(node)]), inner)
        icon.ColorAndOpacity = LinearColor(R=0.22, G=0.22, B=0.25, A=0.85)
        put(icons, icon, node['x'] + mid_x - inner / 2.0, node['y'] + mid_y - inner / 2.0, inner, inner)

    #The buttons: clear, over everything, in groups - a group reports the mouse over any of its
    #buttons, so only the buttons of the one under the mouse are asked whether they are pressed.
    starts = data['GroupStart']
    for g in range(data['groups']):
        group = full_canvas(wt, canvas, 'G%02d' % g, 4)
        for node in nodes[starts[g]:starts[g + 1]]:
            size = SIZES[node['kind']]
            button = Button('Node%03d' % node['index'], wt)
            button.BackgroundColor = CLEAR
            button.ToolTipText = tooltip(node)
            put(group, button, node['x'] + mid_x - size / 2.0, node['y'] + mid_y - size / 2.0, size, size)

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


def variables(widget, tree, data):
    for name, kind, default in (('Busy', 'int', '0'), ('Open', 'bool', 'false'), ('WasOpen', 'bool', 'false'),
                                ('Fresh', 'int', '0'), ('Dragging', 'bool', 'false'),
                                ('Zoom', 'float', str(ZOOM_START)), ('Ratio', 'float', '1.0'),
                                ('Spent', 'int', '0'), ('Earned', 'int', '0'),
                                ('Free', 'int', '0'), ('InMission', 'bool', 'false'),
                                ('PropScan', 'int', '0'), ('PropHere', 'bool', 'false'), ('Near', 'bool', 'false'),
                                ('Ready', 'bool', 'false')):
        ue.blueprint_add_member_variable(widget, name, kind, False, default)
    for name in ('NodeStore', 'NodePow', 'AdjStart', 'Adj', 'EdgeA', 'EdgeB', 'PieceStart', 'GroupStart'):
        typed_variable(widget, name, '(%s)' % ','.join(str(v) for v in data[name]), PinCategory='int', ContainerType=1)
    typed_variable(widget, 'NodeNames', '(%s)' % ','.join('"%s"' % n.replace('"', "'") for n in data['NodeNames']),
                   PinCategory='string', ContainerType=1)
    typed_variable(widget, 'StoreNames', '(%s)' % ','.join(data['StoreNames']), PinCategory='name', ContainerType=1)
    typed_variable(widget, 'Stores', PinCategory='int', ContainerType=1)
    typed_variable(widget, 'Groups', PinCategory='object', PinSubCategoryObject=CanvasPanel, ContainerType=1)
    vector = dict(PinCategory='struct', PinSubCategoryObject=ue.find_struct('Vector2D'))
    typed_variable(widget, 'Last', **vector)
    typed_variable(widget, 'Offset', **vector)
    typed_variable(widget, 'PropAt', PinCategory='struct', PinSubCategoryObject=ue.find_struct('Vector'))
    colour = dict(PinCategory='struct', PinSubCategoryObject=ue.find_struct('LinearColor'))
    typed_variable(widget, 'Ring', **colour)
    typed_variable(widget, 'Inside', **colour)
    typed_variable(widget, 'Link', **colour)
    ue.blueprint_mark_as_structurally_modified(widget)
    ue.compile_blueprint(widget)


#--- the graph -----------------------------------------------------------------------------------------

def for_range(g, first, last):
    """A ForLoop from `first` to `last` inclusive. Returns the loop; its 'Index' is the counter."""
    x, y = g.at()
    loop = g.page.graph_add_node(K2Node_MacroInstance, x, y)
    loop.MacroGraphReference = GraphReference(MacroGraph=ue.load_object(
        EdGraph, '/Engine/EditorBlueprintResources/StandardMacros.StandardMacros:ForLoop'))
    loop.node_allocate_default_pins()
    g.feed(loop, 'FirstIndex', first)
    g.feed(loop, 'LastIndex', last)
    return loop


def looping(g, loop, body):
    """Runs `body(index)` for each turn of `loop`, and carries on after it."""
    g.then(loop, 'execute', 'Completed')
    after = list(g.loose)
    g.loose = [(loop, 'LoopBody')]
    body((loop, 'Index'))
    g.loose = after


def build_graph(widget, tree, data, prop_class):
    g = Graph(widget)
    tick = event(widget, UserWidget, 'Tick', 0, 0)
    g.loose = [(tick, 'then')]
    count = len(tree['nodes'])
    at = lambda name, index: g.element(g.get(name), index)
    minus_one = lambda value: g.math('Subtract_IntInt', value, 1)
    plus_one = lambda value: g.math('Add_IntInt', value, 1)

    def taken(index):
        """Whether node `index` is taken: its bit in its store."""
        store = g.element(g.get('Stores'), at('NodeStore', index))
        return g.math('EqualEqual_IntInt', g.math('Percent_IntInt', g.math('Divide_IntInt', store, at('NodePow', index)), 2), 1)

    def work_out_near(index):
        """Near = whether any neighbour of node `index` is taken."""
        g.setter('Near', 'false')
        loop = for_range(g, at('AdjStart', index), minus_one(at('AdjStart', plus_one(index))))
        looping(g, loop, lambda k: g.either(taken(at('Adj', k)), lambda: g.setter('Near', 'true'), lambda: None))

    def read_stores():
        balances = [g.call(WalletComponent.Balance, self=g.wallet(), Type=g.currency(STORE % s)) for s in range(tree['stores'])]
        g.setter('Stores', g.make_array(balances + [g.call(KismetMathLibrary.Conv_BoolToInt, InBool='true')]))

    g.setter('Busy', g.call(KismetMathLibrary.Max, A=g.math('Subtract_IntInt', g.get('Busy'), 1), B=0))
    player = lambda: g.call(UserWidget.GetOwningPlayer)

    def key(name):
        pressed = g.call(PlayerController.WasInputKeyJustPressed, self=player(), Key=None)
        set_default(pressed[0], 'Key', name)
        return pressed

    #The button groups, into an array once.
    def gather():
        g.setter('Groups', g.make_array([g.get('G%02d' % i) for i in range(data['groups'])]))
        g.setter('Ready', 'true')

    g.either(g.get('Ready'), lambda: None, gather)

    #The TALENTS prop: every few frames, whether it is here (the Camp) and where; a left click
    #that lands near it opens the screen, as the Gem Merchant's booth does its shop.
    g.setter('PropScan', g.math('Add_IntInt', g.get('PropScan'), 1))

    def scan():
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

    g.either(g.math('GreaterEqual_IntInt', g.get('PropScan'), SCAN_EVERY), scan, lambda: None)

    def maybe_open():
        under = g.call(PlayerController.GetHitResultUnderCursorByChannel, self=player(),
                       TraceChannel='TraceTypeQuery1', bTraceComplex='false')
        hit = g.call(GameplayStatics.BreakHitResult, out='Location', Hit=(under[0], 'HitResult'))
        near = g.math('Less_FloatFloat', g.call(KismetMathLibrary.Vector_Distance, V1=hit, V2=g.get('PropAt')), PROP_REACH)
        g.either(g.both(key('LeftMouseButton'), under, near), lambda: g.setter('Open', 'true'), lambda: None)

    g.either(g.both(g.get('PropHere'), g.call(KismetMathLibrary.Not_PreBool, A=g.get('Open'))), maybe_open, lambda: None)

    #P opens and closes; Esc and the close button close.
    g.either(key(KEY), lambda: g.setter('Open', g.call(KismetMathLibrary.Not_PreBool, A=g.get('Open'))), lambda: None)
    g.either(g.math('BooleanOR', key('Escape'), g.call(Button.IsPressed, self=g.get('Close'))),
             lambda: g.setter('Open', 'false'), lambda: None)
    g.either(g.get('Open'), lambda: g.shows('Panel', build_gems.SHOWN), lambda: g.shows('Panel', build_gems.COLLAPSED))
    shown, hidden = g.branch(g.get('Open'))
    g.loose = hidden
    g.setter('WasOpen', 'false')
    hidden = list(g.loose)
    g.loose = shown
    #Just opened: everything is drawn again.
    g.either(g.get('WasOpen'), lambda: None, lambda: g.setter('Fresh', FRESH))
    g.setter('WasOpen', 'true')

    pawn = g.call(UserWidget.GetOwningPlayerPawn)
    g.hero = g.cast(PlayerCharacter, pawn)

    #--- what is left to spend, every frame ---------------------------------------------------------
    g.section()
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

    #--- every colour, a few frames after anything changed -------------------------------------------
    g.section()
    fresh, stale = g.branch(g.math('Greater_IntInt', g.get('Fresh'), 0))
    g.loose = fresh
    g.setter('Fresh', g.math('Subtract_IntInt', g.get('Fresh'), 1))
    read_stores()

    def colour_node(i):
        def pick(ring, inside):
            g.setter('Ring', ring)
            g.setter('Inside', inside)

        def untaken():
            work_out_near(i)
            g.either(g.get('Near'), lambda: pick(OPEN_NODE, ICON_OPEN), lambda: pick(LOCKED, ICON_LOCKED))

        g.either(taken(i), lambda: pick(TAKEN, ICON_TAKEN), untaken)
        ring = g.cast(Image, g.call(PanelWidget.GetChildAt, self=g.get('Rings'), Index=i))
        g.run(Image.SetColorAndOpacity, self=ring, InColorAndOpacity=g.get('Ring'))
        icon = g.cast(Image, g.call(PanelWidget.GetChildAt, self=g.get('Icons'), Index=i))
        g.run(Image.SetColorAndOpacity, self=icon, InColorAndOpacity=g.get('Inside'))

    looping(g, for_range(g, 0, count - 1), colour_node)

    def colour_link(e):
        a, b = at('EdgeA', e), at('EdgeB', e)
        g.either(g.both(taken(a), taken(b)), lambda: g.setter('Link', LINK_TAKEN),
                 lambda: g.either(g.math('BooleanOR', taken(at('EdgeA', e)), taken(at('EdgeB', e))),
                                  lambda: g.setter('Link', LINK_HALF), lambda: g.setter('Link', LINK_OFF)))

        def piece(k):
            line = g.cast(Image, g.call(PanelWidget.GetChildAt, self=g.get('Pieces'), Index=k))
            g.run(Image.SetColorAndOpacity, self=line, InColorAndOpacity=g.get('Link'))

        looping(g, for_range(g, at('PieceStart', e), minus_one(at('PieceStart', plus_one(e)))), piece)

    looping(g, for_range(g, 0, len(tree['edges']) - 1), colour_link)
    g.loose += stale

    #--- taking a node: only the buttons of the group under the mouse are asked -----------------------
    g.section()

    def take(i):
        store_name = g.element(g.get('StoreNames'), at('NodeStore', i))
        g.run(WalletComponent.ClientAdd, self=g.wallet(), Type=g.currency(store_name), Amount=at('NodePow', i), reason='Default')
        g.run(WalletComponent.ClientAdd, self=g.wallet(), Type=g.currency(SPENT), Amount=1, reason='Default')
        g.run(TextBlock.SetText, self=g.get('Note'), InText=g.call(KismetTextLibrary.Conv_StringToText, InString=g.call(
            KismetStringLibrary.Concat_StrStr, A=at('NodeNames', i), B=' taken.')))

    def pressed(i):
        def why_not():
            g.either(taken(i), lambda: g.says('Note', 'Already taken.'),
                     lambda: g.either(g.math('LessEqual_IntInt', g.get('Free'), 0),
                                      lambda: g.says('Note', 'No points left - they come with levels.'),
                                      lambda: g.says('Note', 'Take a node next to one you have first.')))

        read_stores()
        work_out_near(i)
        g.either(g.both(g.get('Near'), g.call(KismetMathLibrary.Not_PreBool, A=taken(i)),
                        g.math('Greater_IntInt', g.get('Free'), 0)), lambda: take(i), why_not)
        g.setter('Busy', DEBOUNCE)
        g.setter('Fresh', FRESH)

    def in_group(group_index):
        group = g.element(g.get('Groups'), group_index)
        first = at('GroupStart', group_index)

        def ask(i):
            button = g.cast(Button, g.call(PanelWidget.GetChildAt, self=g.element(g.get('Groups'), group_index),
                                           Index=g.math('Subtract_IntInt', i, at('GroupStart', group_index))))
            g.either(g.both(g.call(Button.IsPressed, self=button), g.math('LessEqual_IntInt', g.get('Busy'), 0)),
                     lambda: pressed(i), lambda: None)

        g.either(g.call(Widget.IsHovered, self=group),
                 lambda: looping(g, for_range(g, first, minus_one(at('GroupStart', plus_one(group_index)))), ask),
                 lambda: None)

    looping(g, for_range(g, 0, data['groups'] - 1), in_group)

    #--- the reset, in the Camp -------------------------------------------------------------------
    g.section()
    fired, carry = g.branch(g.both(g.call(Button.IsPressed, self=g.get('Reset')), g.math('LessEqual_IntInt', g.get('Busy'), 0)))
    g.loose = fired

    def reset():
        read_stores()
        looping(g, for_range(g, 0, tree['stores'] - 1), lambda s: g.run(
            WalletComponent.Deduct, self=g.wallet(), Type=g.currency(g.element(g.get('StoreNames'), s)),
            Amount=g.element(g.get('Stores'), s)))
        g.run(WalletComponent.Deduct, self=g.wallet(), Type=g.currency(SPENT), Amount=g.get('Spent'))
        g.run(WalletComponent.Deduct, self=g.wallet(), Type=g.currency(FOUND_WITH), Amount=tree['respec'])
        g.says('Note', 'Every point given back.')

    emeralds = g.call(WalletComponent.Balance, self=g.wallet(), Type=g.currency(FOUND_WITH))
    g.either(g.get('InMission'), lambda: g.says('Note', 'Talents can only be reset in the Camp.'),
             lambda: g.either(g.math('LessEqual_IntInt', g.get('Spent'), 0), lambda: g.says('Note', 'Nothing to reset.'),
                              lambda: g.either(g.math('GreaterEqual_IntInt', emeralds, tree['respec']), reset,
                                               lambda: g.says('Note', 'A reset costs %d emeralds.' % tree['respec']))))
    g.setter('Busy', DEBOUNCE)
    g.setter('Fresh', FRESH)
    g.loose = g.loose + carry

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

    #Zooming keeps the middle of the screen where it is: the offset grows and shrinks with the scale.
    def zoom_by(new_zoom):
        g.setter('Ratio', g.math('Divide_FloatFloat', new_zoom, g.get('Zoom')))
        g.setter('Offset', g.call(KismetMathLibrary.Multiply_Vector2DFloat, A=g.get('Offset'), B=g.get('Ratio')))
        g.setter('Zoom', g.math('Multiply_FloatFloat', g.get('Zoom'), g.get('Ratio')))
        g.setter('Busy', 8)

    bigger = g.math('BooleanOR', key('MouseScrollUp'),
                    g.both(g.call(Button.IsPressed, self=g.get('ZoomIn')), g.math('LessEqual_IntInt', g.get('Busy'), 0)))
    smaller = g.math('BooleanOR', key('MouseScrollDown'),
                     g.both(g.call(Button.IsPressed, self=g.get('ZoomOut')), g.math('LessEqual_IntInt', g.get('Busy'), 0)))
    g.either(bigger, lambda: zoom_by(g.call(KismetMathLibrary.FMin, A=g.math('Multiply_FloatFloat', g.get('Zoom'), ZOOM_STEP), B=ZOOM_MAX)),
             lambda: None)
    g.either(smaller, lambda: zoom_by(g.call(KismetMathLibrary.FMax, A=g.math('Divide_FloatFloat', g.get('Zoom'), ZOOM_STEP), B=ZOOM_MIN)),
             lambda: None)
    g.run(Widget.SetRenderTranslation, self=g.get('Tree'), Translation=g.get('Offset'))
    g.run(Widget.SetRenderScale, self=g.get('Tree'), Scale=g.call(KismetMathLibrary.MakeVector2D, X=g.get('Zoom'), Y=g.get('Zoom')))

    g.loose += hidden
    ue.compile_blueprint(widget)
    say('talent graph built')


def build_widget(pictures, tree, prop_class):
    there = on_disk(WIDGET, 'WidgetBlueprint')
    if there is not None:
        say('talents already there: ' + WIDGET)
        return there
    data = tree_data(tree)
    say('talent data: %d pieces, %d groups' % (data['pieces'], data['groups']))
    factory = WidgetBlueprintFactory()
    factory.ParentClass = UserWidget
    widget = factory.factory_create_new(WIDGET)
    build_tree(widget, pictures, tree, data)
    variables(widget, tree, data)
    build_graph(widget, tree, data, prop_class)
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
    prop = build_gems.build_stall(pictures, actor_path=PROP_ACTOR, level_path=PROP_LEVEL, looks_path=PROP_LOOKS,
                                  sign_path=PROP_SIGN, words='TALENTS', at=PROP_WHERE, facing=PROP_FACING,
                                  sign_height=260.0)
    widget = build_widget(pictures, tree, prop.GeneratedClass)
    actor = build_actor(widget)
    for path in LEVELS:
        build_level(path, actor)
    say('talents done - cook, then carry the levels, the actor, the screen and the pictures')


if __name__ == '__main__':
    main()
