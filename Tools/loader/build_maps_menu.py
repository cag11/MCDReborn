"""
The map table's panel: pick a mission, pick how hard, go.

    UE4Editor-Cmd.exe <project>.uproject -run=Py <this file> -unattended -nopause -nosplash

Then run build_prop.py to make the table that opens it, cook both, and install the level as a
Lobby payload with MCD Reborn.

Why it is shaped differently from the mod it learned from
---------------------------------------------------------
Blossoming Isles cooks one screen per map and twenty-one buttons per screen, each carrying its own
complete copy of the launch. That is what building by hand in an editor pushes you towards, and it
does not survive being asked for fifty-six missions - it would be 1,176 buttons.

So the choice is split up: a mission is picked on one page, a difficulty and a threat level are
picked on another, and ONE Play button reads the three variables and launches. Eleven buttons
instead of twenty-one, and the mission list is the only thing that grows.

Two things inherited from that mod are worth keeping, and one is worth not keeping. Keep: the
count-the-widgets guard before opening, and the pause. Do not keep: absolute 1920x1080 offsets
everywhere. Its own third Play button sits at x=2112 on a 1920 canvas with an inverted height, so
it is off screen and unclickable in the shipped build - which is what authoring against one
resolution in absolute pixels eventually costs. Everything here is anchored proportionally.

Buttons are polled, not subscribed to
-------------------------------------
A Button's OnClicked compiles to a ComponentDelegateBinding, and those cannot be generated from
outside the editor - the same wall the settings panel's tick-boxes hit, which is why they are
polled with IsChecked. So Tick asks each button whether it is pressed.

Pressing lasts many frames, so a press would otherwise fire every frame it is held. `Busy` is a
countdown: an action sets it, and nothing else may fire until it reaches zero. One variable covers
every button, because only one can be pressed at a time.
"""

import io
import os
import sys

def _here():
    r"""
    The folder these scripts live in.

    Three ways, because each of the obvious two is broken under `-run=Py`:

      * `__file__` is not defined at all - the commandlet execs the source rather than
        importing it, so the module globals a normal run would have are simply absent.
      * `sys.argv[0]` IS the script's path, and the engine has already eaten the backslash
        escapes in it: a path through AppData\Local\temp arrives with a literal TAB where the
        \t was. Any path containing \t, \b, \n or \U comes through mangled - and a file called
        build_something.py, after a backslash, is exactly \b.

    So the launcher sets MCDREBORN_LOADER, which passes through the environment untouched, and
    argv is kept only as a fallback for a path that happens to have no escapes in it.
    """
    said = os.environ.get('MCDREBORN_LOADER')
    if said and os.path.isdir(said):
        return said

    try:
        return os.path.dirname(os.path.abspath(__file__))
    except NameError:
        pass

    for arg in sys.argv:
        if arg.lower().endswith('.py') and os.path.isfile(arg):
            return os.path.dirname(os.path.abspath(arg))

    return os.getcwd()


HERE = _here()
sys.path.insert(0, HERE)

import unreal_engine as ue                                                        # noqa: E402

from unreal_engine.classes import (                                               # noqa: E402
    Button,
    CanvasPanel,
    KismetMathLibrary,
    ScrollBox,
    TextBlock,
    UserWidget,
    VerticalBox,
    WidgetBlueprintFactory,
    WidgetSwitcher,
    K2Node_IfThenElse,
)

import mcd_ui                                                                     # noqa: E402
import wire_play                                                                  # noqa: E402
from mcd_ui import (say, keep, on_disk, link, pin, place, stretch, label, fill,      # noqa: E402
                    button, stub_font, event)


WIDGET = '/Game/MCDReborn/UI/UMG_MCDRebornMaps'

#Where the mission list comes from: `id | display name | ELevelNames value`, one per line.
#
#All three columns are read out of the running game rather than typed - the names by
#MCDReborn.exe PROBE_MISSIONS, the numbers by PROBE_STRUCT=ELevelNames. A file rather than a list
#in here, because the game's missions are the game's to change and a copy pasted into a script is
#a copy that goes stale silently.
LIST = os.path.join(HERE, 'missions.txt')

#Not worth offering. The Camp is where the panel is being read FROM, and the menu backdrop is a
#set rather than a mission.
SKIP = {'lobby', 'menudummy'}

#--- the slots ------------------------------------------------------------------------------------
#
#How many custom maps the panel can ever hold, and the whole reason it can hold any.
#
#The list is cooked into the widget and nobody using MCD Reborn has an Unreal editor, so whatever
#the menu is born with it dies with. Worse, a slot's level name is a string constant inside
#compiled bytecode, sitting behind eighty-six absolute jump offsets - editing one would mean
#relinking the entire graph.
#
#So the names never change. Slot 7 launches `mcdcustom07` for ever, and MCD Reborn decides what
#that file IS by writing an imported map out under that name. Rather than making the menu match
#the maps, the maps match the menu.
#
#Raising this costs a rebuild HERE, never one on anybody else's machine - but every rebuild means
#re-shipping the exe, so it is set high enough to never need raising. A hundred slots cost a
#hundred buttons and about a thousand generated nodes, which is a bigger widget and no other
#difference: the click test is a chain of trivial comparisons run once a frame, and the graph is
#generated either way.
#
#It does make collapsing empty slots necessary rather than tidy. An empty slot launches a level
#file that does not exist, and this game answers a missing level by silently loading Creeper
#Woods - so ninety-odd unused rows are ninety-odd ways to end up somewhere unexplained.
SLOTS = 100
SLOT_PREFIX = 'mcdcustom'
SLOT_ID = SLOT_PREFIX + '%02d'
SLOT_NAME = 'Custom %02d'

#ELevelNames::randommission - a custom map has no entry of its own, and this is what Blossoming
#Isles passes for every map it ships.
SLOT_MISSION = 157

#ESlateVisibility::Collapsed - takes no space in its parent, and cannot be clicked.
COLLAPSED = 1

#ESlateVisibility::Visible. Never used as a setting anybody wanted - see the note by `rows`.
VISIBLE = 0

#How many frames an action blocks further presses for. Twenty is a third of a second, which is
#longer than anybody holds a button deliberately and shorter than anybody notices.
DEBOUNCE = 20

#--- layout, in fractions of the screen ----------------------------------------------------------
#Fractions rather than pixels. See the note above about the button that ended up off the canvas.
PANEL = (0.14, 0.10, 0.86, 0.90)
PAD = 0.02


def missions():
    """
    The missions to offer: id, display name, and the number the game calls each one.

    Falls back to nothing rather than to a guess. A menu with no missions is obviously broken,
    where a menu with a stale hardcoded twelve looks like it is working - and worse, a wrong
    entry does not announce itself either: when a levelFilename does not exist this game loads
    Creeper Woods instead, silently, which reads as the menu working and sending you elsewhere.
    """
    found = []

    if not os.path.isfile(LIST):
        say('no mission list at %s - see the comment on LIST for how it is made' % LIST)
        return found

    with io.open(LIST, encoding='utf-8', errors='replace') as file:
        for line in file:
            line = line.strip()
            if not line or line.startswith('#'):
                continue

            bits = [one.strip() for one in line.split('|')]
            if len(bits) < 3:
                say('skipping a line that is not id|name|number|show: ' + line)
                continue

            name, said, number = bits[0], bits[1], bits[2]
            shown = len(bits) > 3 and bits[3].lower() == 'show'

            #Only what is worth launching from the Camp. The game's own missions are already on
            #the game's own map table, and offering all fifty-six here was duplicating something
            #that works - so a row is offered when it is marked, and the rest stay in the file
            #for their numbers.
            if not shown or name in SKIP:
                continue

            found.append((name, said or name, int(number)))

    #And the slots, which are the point. They are offered whether or not anything is installed
    #in one yet - an empty slot launches a level file that is not there, and this game answers a
    #missing level by silently loading Creeper Woods. Hiding the empty ones is a later job; for
    #now the name says which is which.
    for at in range(1, SLOTS + 1):
        found.append((SLOT_ID % at, SLOT_NAME % at, SLOT_MISSION))

    return found


def build_tree(widget, list_of):
    widget.modify()
    tree = widget.WidgetTree

    title_face = stub_font(mcd_ui.TITLE_FONT)
    body_face = stub_font(mcd_ui.BODY_FONT)

    root = CanvasPanel('Root', tree)
    tree.RootWidget = root

    #A curtain over the whole screen first, so the Camp reads as being behind a panel rather than
    #having a panel dropped on it.
    stretch(root, fill(tree, 'Curtain', mcd_ui.CURTAIN))

    switcher = WidgetSwitcher('Pages', tree)
    switcher.bIsVariable = True
    stretch(root, switcher)

    made = {'switcher': switcher, 'missions': [], 'difficulty': [], 'threat': [],
            'endless': []}

    #--- page one: which mission ---------------------------------------------------------------
    picking = CanvasPanel('Picking', tree)
    switcher.AddChild(picking)

    #stretch, not place(...0,0,0,0). put's last two arguments are a SIZE, so that asked for a
    #backing zero pixels across - which is exactly the mistake the helper's own docstring warns
    #about, made by the person who wrote the warning.
    stretch(picking, fill(tree, 'PickBacking', mcd_ui.BACKING))
    place(picking, label(tree, 'PickTitle', 'Choose a mission', mcd_ui.GOLD,
                       title_face, mcd_ui.TITLE_SIZE, typeface=mcd_ui.TITLE_FACE,
                       outline=2, shadow=mcd_ui.SHADOW), 150, 55, 900, 60)

    scroller = ScrollBox('Scroller', tree)
    place(picking, scroller, 150, 165, 900, 780)

    rows = VerticalBox('Rows', tree)

    #Set to Visible on purpose, and not for how it looks.
    #
    #Revealing a slot later means writing ESlateVisibility::Visible into its visibility property -
    #and an enum property's VALUE is an FName, so that name has to already be in the package's
    #name table or there is nothing to point at. Adding one would move every offset in the file.
    #
    #Unreal only serialises a property that differs from the class default, so nothing gets the
    #name into the table by being ordinary: a Button already defaults to Visible. A panel does
    #not - VerticalBox, CanvasPanel and ScrollBox all default to SelfHitTestInvisible - so saying
    #Visible here is a real difference, it serialises, and the name lands in the table.
    #
    #One line to make a hundred one-byte edits possible.
    rows.Visibility = VISIBLE

    scroller.AddChild(rows)

    for at, (name, said, number) in enumerate(list_of):
        #A slot is named for the slot it IS, not for its position in the list.
        #
        #MCD Reborn has to find a slot's button in the cooked asset to show or hide it, and
        #`Mission7` only means slot 7 while nothing else is listed above the slots. Naming it
        #`Slot07` means the two halves agree about which is which without either counting.
        button_name = ('Slot%02d' % int(name[len(SLOT_PREFIX):])
                       if name.startswith(SLOT_PREFIX) else 'Mission%d' % at)

        one = button(tree, button_name, said, body_face, mcd_ui.BUTTON_SIZE,
                     typeface=mcd_ui.BODY_FACE)

        #A slot starts COLLAPSED, and that is two things at once.
        #
        #It is what the menu should look like out of the box: a hundred rows pointing at level
        #files that do not exist, and this game answers a missing level by silently loading
        #Creeper Woods. An empty menu is correct; a hundred ways to end up somewhere unexplained
        #is not.
        #
        #It is also the only way there is a byte to change later. Unreal serialises a property
        #only when it differs from the class default, and a Button's default visibility is
        #Visible - so a slot authored Visible has NO visibility recorded in the cooked asset and
        #nothing for MCD Reborn to flip. Authored Collapsed, the tag is there, and revealing a
        #slot is one byte in place: same length, nothing moves.
        if name.startswith(SLOT_PREFIX):
            one.Visibility = COLLAPSED

        seat = rows.AddChild(one)

        #Six pixels of air. The game's rows are separated by a gap rather than by a border, which
        #is why a list of them does not read as a table.
        try:
            seat.Padding = mcd_ui.Margin(Left=0.0, Top=0.0, Right=0.0, Bottom=6.0)
        except Exception as problem:
            say('could not space %s: %s' % (button_name, problem))
        made['missions'].append((button_name, name, said, number))

    made['close'] = button(tree, 'Close', 'Close', body_face, mcd_ui.BODY_SIZE, centred=True,
                             typeface=mcd_ui.BODY_FACE)
    place(picking, made['close'], 1280, 40, 200, 60)

    #--- page two: how hard --------------------------------------------------------------------
    playing = CanvasPanel('Playing', tree)
    switcher.AddChild(playing)

    stretch(playing, fill(tree, 'PlayBacking', mcd_ui.BACKING))

    #Variable, because it is rewritten with whichever mission was picked. Without that the second
    #page gives no sign of which one is about to start.
    made['chosen_label'] = label(tree, 'ChosenLabel', '', mcd_ui.GOLD, title_face,
                                 mcd_ui.TITLE_SIZE, variable=True,
                                 shadow=mcd_ui.SHADOW)
    place(playing, made['chosen_label'], 60, 40, 1100, 60)

    place(playing, label(tree, 'DiffLabel', 'Difficulty', mcd_ui.DIM, body_face,
                       typeface=mcd_ui.BODY_FACE), 60, 150, 400, 30)

    for at, said in enumerate(['Default', 'Adventure', 'Apocalypse']):
        one = button(tree, 'Diff%d' % (at + 1), said, body_face,
                     typeface=mcd_ui.BODY_FACE)
        place(playing, one, 60 + at * 330, 190, 310, 80)
        made['difficulty'].append((at + 1, one))

    place(playing, label(tree, 'ThreatLabel', 'Threat level', mcd_ui.DIM, body_face,
                       typeface=mcd_ui.BODY_FACE), 60, 300, 400, 30)

    for at in range(1, 8):
        one = button(tree, 'Threat%d' % at, str(at), body_face,
                     typeface=mcd_ui.BODY_FACE)
        place(playing, one, 60 + (at - 1) * 145, 340, 130, 80)
        made['threat'].append((at, one))

    #--- Apocalypse+ ----------------------------------------------------------------------------
    #
    #A separate field, not more threat levels. EThreatLevel stops at seven - that is Apocalypse +1
    #to +7 - and the tiers above it are carried in EndlessStruggle.Value, which is why Blossoming
    #Isles has a whole extra screen with a 1 to 25 spinbox on it. Passing 0 leaves it off, which
    #is what every launch did until somebody noticed 25 was missing.
    place(playing, label(tree, 'EndlessLabel', 'Apocalypse+   (0 for none)', mcd_ui.DIM, body_face,
                       typeface=mcd_ui.BODY_FACE),
        60, 450, 600, 30)

    for at in range(0, 26):
        one = button(tree, 'Endless%d' % at, str(at), body_face, mcd_ui.BODY_SIZE,
                     typeface=mcd_ui.BODY_FACE)
        place(playing, one, 60 + (at % 13) * 105, 490 + (at // 13) * 80, 95, 70)
        made['endless'].append((at, one))

    made['play'] = button(tree, 'Play', 'Play', title_face, 40, mcd_ui.GOLD,
                            typeface=mcd_ui.TITLE_FACE)
    place(playing, made['play'], 60, 680, 400, 110)

    made['back'] = button(tree, 'Back', 'Back', body_face, mcd_ui.BODY_SIZE, centred=True,
                            typeface=mcd_ui.BODY_FACE)
    place(playing, made['back'], 1280, 40, 200, 60)

    ue.blueprint_mark_as_structurally_modified(widget)
    ue.compile_blueprint(widget)

    say('tree built: %d mission(s), 3 difficulties, 7 threat levels, 26 endless tiers'
        % len(list_of))
    return made


def variables(widget):
    """
    What the panel remembers between one press and the next.

    Compiled straight after adding them, because a variable that has only been ADDED has no entry
    in the skeleton class yet, and a getter made before that resolves to nothing at all - silently.
    """
    for name, kind in [('Chosen', 'string'), ('Mission', 'int'), ('Difficulty', 'int'),
                       ('Threat', 'int'), ('Endless', 'int'), ('Busy', 'int')]:
        ue.blueprint_add_member_variable(widget, name, kind)

    ue.blueprint_mark_as_structurally_modified(widget)
    ue.compile_blueprint(widget)
    say('variables added')


def pressed(page, widget, name, y):
    """
    The test every button's action hangs off: this one is down, and nothing else just fired.

    Returns the Branch node, so a caller wires its own work onto `then`.
    """
    holding = page.graph_add_node_call_function(Button.IsPressed, 300, y)
    link(page.graph_add_node_variable_get(name, None, 60, y), name, holding, 'self')

    free = page.graph_add_node_call_function(KismetMathLibrary.LessEqual_IntInt, 300, y + 120)
    link(page.graph_add_node_variable_get('Busy', None, 60, y + 120), 'Busy', free, 'A')
    free.node_find_pin('B').default_value = '0'

    both = page.graph_add_node_call_function(KismetMathLibrary.BooleanAND, 560, y + 40)
    link(holding, 'ReturnValue', both, 'A')
    link(free, 'ReturnValue', both, 'B')

    gate = page.graph_add_node(K2Node_IfThenElse, 780, y)
    link(both, 'ReturnValue', gate, 'Condition')
    return gate


def hold(page, y):
    """Sets the debounce, so the press that just fired does not fire again next frame."""
    node = page.graph_add_node_variable_set('Busy', None, 2400, y)
    node.node_find_pin('Busy').default_value = str(DEBOUNCE)
    return node


def build_graph(widget, made):
    page = widget.UberGraphPages[0]
    tick = event(widget, UserWidget, 'Tick', 0, 0)

    #The countdown, first thing every frame. Clamped at zero rather than allowed to run negative,
    #because "has it reached zero" is the only question asked of it.
    older = page.graph_add_node_call_function(KismetMathLibrary.Subtract_IntInt, 300, -400)
    link(page.graph_add_node_variable_get('Busy', None, 60, -400), 'Busy', older, 'A')
    older.node_find_pin('B').default_value = '1'

    floored = page.graph_add_node_call_function(KismetMathLibrary.Max, 560, -400)
    link(older, 'ReturnValue', floored, 'A')
    floored.node_find_pin('B').default_value = '0'

    counted = page.graph_add_node_variable_set('Busy', None, 800, -400)
    link(floored, 'ReturnValue', counted, 'Busy')
    link(tick, 'then', counted, 'execute')

    loose = [(counted, 'then')]
    y = 0

    def chain(gate):
        """Every test runs every frame: a press that is not this one falls through to the next."""
        for node, out in loose:
            pin(node, out).make_link_to(pin(gate, 'execute'))
        del loose[:]
        loose.append((gate, 'else'))

    #--- a mission was picked -------------------------------------------------------------------
    for button_name, name, said, number in made['missions']:
        gate = pressed(page, widget, button_name, y)
        chain(gate)

        remember = page.graph_add_node_variable_set('Chosen', None, 1100, y)
        remember.node_find_pin('Chosen').default_value = name
        link(gate, 'then', remember, 'execute')

        #What the game calls this one. Forty-two of the fifty-six have an ELevelNames entry of
        #their own; the rest get randommission, the value Blossoming Isles passes for every
        #custom map it ships and therefore the proven answer for "not one of the built-ins".
        numbered = page.graph_add_node_variable_set('Mission', None, 1100, y + 170)
        numbered.node_find_pin('Mission').default_value = str(number)
        link(remember, 'then', numbered, 'execute')

        shown = page.graph_add_node_call_function(TextBlock.SetText, 1500, y)
        link(page.graph_add_node_variable_get('ChosenLabel', None, 1300, y + 140),
             'ChosenLabel', shown, 'self')
        shown.node_find_pin('InText').default_value = said
        link(numbered, 'then', shown, 'execute')

        turn = page.graph_add_node_call_function(WidgetSwitcher.SetActiveWidgetIndex, 1900, y)
        link(page.graph_add_node_variable_get('Pages', None, 1700, y + 140), 'Pages', turn, 'self')
        turn.node_find_pin('Index').default_value = '1'
        link(shown, 'then', turn, 'execute')

        held = hold(page, y)
        link(turn, 'then', held, 'execute')

        y += 340

    #--- how hard -------------------------------------------------------------------------------
    for store, prefix, entries in [('Difficulty', 'Diff', made['difficulty']),
                                   ('Threat', 'Threat', made['threat']),
                                   ('Endless', 'Endless', made['endless'])]:
        for value, _ in entries:
            gate = pressed(page, widget, '%s%d' % (prefix, value), y)
            chain(gate)

            remember = page.graph_add_node_variable_set(store, None, 1100, y)
            remember.node_find_pin(store).default_value = str(value)
            link(gate, 'then', remember, 'execute')

            held = hold(page, y)
            link(remember, 'then', held, 'execute')

            y += 340

    #--- back, and close ------------------------------------------------------------------------
    for name, index in [('Back', 0)]:
        gate = pressed(page, widget, name, y)
        chain(gate)

        turn = page.graph_add_node_call_function(WidgetSwitcher.SetActiveWidgetIndex, 1100, y)
        link(page.graph_add_node_variable_get('Pages', None, 900, y + 140), 'Pages', turn, 'self')
        turn.node_find_pin('Index').default_value = str(index)
        link(gate, 'then', turn, 'execute')

        held = hold(page, y)
        link(turn, 'then', held, 'execute')
        y += 340

    #--- and the one that starts a mission ------------------------------------------------
    #Its own file, because everything up to here is the engine's own types and this reaches into
    #the GAME's. When the panel works and nothing launches, that is the line between the halves.
    gate = pressed(page, widget, 'Play', y)
    chain(gate)
    wire_play.launch(page, widget, gate, y)
    y += 340

    gate = pressed(page, widget, 'Close', y)
    chain(gate)
    gone = page.graph_add_node_call_function(UserWidget.RemoveFromParent, 1100, y)
    link(gate, 'then', gone, 'execute')

    ue.compile_blueprint(widget)
    say('graph built - %d mission button(s) polled' % len(made['missions']))


def main():
    there = on_disk(WIDGET, 'WidgetBlueprint')
    if there is not None:
        say('panel already there, leaving it alone: ' + WIDGET)
        return

    list_of = missions()
    if not list_of:
        say('nothing to offer - stopping before building an empty menu')
        return

    say('building the map panel for %d mission(s): %s'
        % (len(list_of), ', '.join(one[0] for one in list_of)))

    factory = WidgetBlueprintFactory()
    factory.ParentClass = UserWidget
    widget = factory.factory_create_new(WIDGET)

    variables(widget)
    made = build_tree(widget, list_of)
    build_graph(widget, made)

    keep(widget)
    say('panel built: ' + WIDGET)


main()
