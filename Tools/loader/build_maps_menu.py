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
    KismetTextLibrary,
    GameplayStatics,
    SaveGame,
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
from mcd_ui import (say, keep, on_disk, as_pin, link, pin, place, stretch, label, fill,      # noqa: E402
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


PREFS = '/Game/MCDReborn/BP_MCDRebornMapPrefs'

#ONE save file for the whole panel, not one per map.
#
#Per-map was built first and taken out again. The game itself does not remember a difficulty per
#mission - it remembers the last one you played - and matching that is both simpler and what
#somebody expects: setting Apocalypse +25 once should stick until it is changed, not until the
#next map is opened. It also deletes a hundred per-button nodes and the whole question of what a
#map that has never been played should inherit.


def prefs_class():
    """
    The tiny USaveGame the panel remembers a map's settings in.

    Built here rather than by hand because everything else in this folder is, and because the
    class has to exist at COOK time for the panel to reference it: a widget that names a class
    the pak does not carry resolves it to nothing at run time and every read comes back zero.

    Three ints, no methods. It is a record, not behaviour.
    """
    there = on_disk(PREFS, 'Blueprint')
    if there is None:
        there = ue.create_blueprint(SaveGame, PREFS)
        say('prefs save class created: ' + PREFS)

    for name in ['Diff', 'Threat', 'Endless']:
        try:
            ue.blueprint_add_member_variable(there, name, 'int')
        except Exception as problem:
            #Already present on a rebuild, which is not a failure.
            say('  prefs field %s: %s' % (name, problem))

    ue.blueprint_mark_as_structurally_modified(there)
    ue.compile_blueprint(there)
    keep(there)

    return there.GeneratedClass


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
            'endless': [], 'said': {}}

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

    #What is currently chosen, beside the heading that names the row.
    #
    #Three rows of buttons that light up in no way whatsoever, and a Play that reads variables
    #nothing on screen reports - so the only way to know what was about to be launched was to
    #remember which buttons had been pressed. Nobody does that, and the one setting where it
    #mattered most was the one nobody ever pressed: see the note in `variables` about difficulty
    #zero and the map with no mobs in it.
    #
    #Its starting caption is the value the variable starts at, not a placeholder. A row reading
    #"Difficulty -" would be honest about being unset and would still leave the player with no
    #idea what pressing Play now does.
    made['said']['Difficulty'] = {}
    place(playing, label(tree, 'DifficultyValue', 'Default', mcd_ui.GOLD, body_face,
                       typeface=mcd_ui.BODY_FACE, variable=True), 470, 150, 600, 30)

    for at, said in enumerate(['Default', 'Adventure', 'Apocalypse']):
        one = button(tree, 'Diff%d' % (at + 1), said, body_face,
                     typeface=mcd_ui.BODY_FACE)
        place(playing, one, 60 + at * 330, 190, 310, 80)
        made['difficulty'].append((at + 1, one))
        made['said']['Difficulty'][at + 1] = said

    place(playing, label(tree, 'ThreatLabel', 'Threat level', mcd_ui.DIM, body_face,
                       typeface=mcd_ui.BODY_FACE), 60, 300, 400, 30)

    made['said']['Threat'] = {}
    place(playing, label(tree, 'ThreatValue', '1', mcd_ui.GOLD, body_face,
                       typeface=mcd_ui.BODY_FACE, variable=True), 470, 300, 600, 30)

    for at in range(1, 8):
        one = button(tree, 'Threat%d' % at, str(at), body_face,
                     typeface=mcd_ui.BODY_FACE)
        place(playing, one, 60 + (at - 1) * 145, 340, 130, 80)
        made['threat'].append((at, one))
        made['said']['Threat'][at] = str(at)

    #--- Apocalypse+ ----------------------------------------------------------------------------
    #
    #A separate field, not more threat levels. EThreatLevel stops at seven - that is Apocalypse +1
    #to +7 - and the tiers above it are carried in EndlessStruggle.Value, which is why Blossoming
    #Isles has a whole extra screen with a 1 to 25 spinbox on it. Passing 0 leaves it off, which
    #is what every launch did until somebody noticed 25 was missing.
    place(playing, label(tree, 'EndlessLabel', 'Apocalypse+   (0 for none)', mcd_ui.DIM, body_face,
                       typeface=mcd_ui.BODY_FACE),
        60, 450, 600, 30)

    made['said']['Endless'] = {}
    place(playing, label(tree, 'EndlessValue', '0', mcd_ui.GOLD, body_face,
                       typeface=mcd_ui.BODY_FACE, variable=True), 670, 450, 400, 30)

    for at in range(0, 26):
        one = button(tree, 'Endless%d' % at, str(at), body_face, mcd_ui.BODY_SIZE,
                     typeface=mcd_ui.BODY_FACE)
        place(playing, one, 60 + (at % 13) * 105, 490 + (at // 13) * 80, 95, 70)
        made['endless'].append((at, one))
        made['said']['Endless'][at] = str(at)

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
    #Difficulty and Threat start at 1 rather than at nothing.
    #
    #An int variable added with no default is zero, and zero is not one of the values this panel
    #offers: the difficulty buttons are 1 to 3 and the threat buttons are 1 to 7. So a player who
    #pressed Play without first choosing how hard sent difficulty 0, threat 0 - which the game
    #accepts, loads, and plays as a level with NO MOBS IN IT. Nothing about that reads as an
    #unset field. It reads as a broken map, and it was diagnosed as one for most of a day: the
    #same level installed over a real mission spawned mobs perfectly, because a real mission is
    #launched by the game's own screen and that screen never sends zero.
    #
    #One is the right floor rather than a guess. Difficulty 1 is Default and threat level 1 is
    #the lowest the game has, so both are unlocked on a brand new save - which the player's OWN
    #furthest unlock is not, and a panel compiled into the exe cannot read a save anyway.
    starts_at = {'Difficulty': '1', 'Threat': '1'}

    #`Pending` says a map was just picked and the remembered settings have not been read back
    #yet. The read is ONE branch shared by all hundred buttons rather than a copy inside each -
    #the panel is a per-frame chain of tests, so a flag costs one node per button and the
    #alternative costs thirty.
    for name, kind in [('Chosen', 'string'), ('Mission', 'int'), ('Difficulty', 'int'),
                       ('Threat', 'int'), ('Endless', 'int'), ('Busy', 'int'),
                       ('Pending', 'int')]:
        ue.blueprint_add_member_variable(widget, name, kind, False, starts_at.get(name, ''))

    ue.blueprint_mark_as_structurally_modified(widget)
    ue.compile_blueprint(widget)
    say('variables added')


def says(node, words):
    """
    Puts a literal caption on a SetText node.

    `InText` is an FText pin, and an FText literal is NOT kept in a pin's DefaultValue - that
    field is for the string-ish pins. Assigning it there is accepted silently and leaves the pin
    holding an empty text, so the call still fires and the label is blanked instead of rewritten.
    Which is exactly what it looked like: the readouts showed their built-in captions until the
    first press and went empty from then on.

    Both are set because the two fields are not alternatives - Slate reads the text one and the
    editor shows the other, and a pin carrying only one of them reads as changed-but-empty in
    whichever half was missed.
    """
    pin = node.node_find_pin('InText')
    pin.default_value = words
    pin.default_text_value = words


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


def build_graph(widget, made, prefs):
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
        says(shown, said)
        link(numbered, 'then', shown, 'execute')

        #A note that the remembered settings have not been read back yet. One node per button;
        #the reading itself is one branch further down.
        asked = page.graph_add_node_variable_set('Pending', None, 1100, y + 320)
        asked.node_find_pin('Pending').default_value = '1'
        link(shown, 'then', asked, 'execute')

        turn = page.graph_add_node_call_function(WidgetSwitcher.SetActiveWidgetIndex, 1900, y)
        link(page.graph_add_node_variable_get('Pages', None, 1700, y + 140), 'Pages', turn, 'self')
        turn.node_find_pin('Index').default_value = '1'
        link(asked, 'then', turn, 'execute')

        held = hold(page, y)
        link(turn, 'then', held, 'execute')

        y += 340

    #--- what that map was last played at -------------------------------------------------------
    #
    #One copy, reached through the Pending flag, rather than one inside each of the hundred
    #buttons - which would be three thousand nodes for the same behaviour. It runs the frame
    #AFTER a map is picked, which is invisible: page two is already up and the readouts change
    #long before anything on it can be pressed.
    #
    #A map with no save file yet falls to Default and threat 1, not to nothing. That is the whole
    #reason this exists - zero is not a difficulty the panel offers, and a level launched at
    #difficulty zero plays as one with no mobs in it.
    waiting = page.graph_add_node_call_function(KismetMathLibrary.Greater_IntInt, 300, y)
    link(page.graph_add_node_variable_get('Pending', None, 60, y), 'Pending', waiting, 'A')
    waiting.node_find_pin('B').default_value = '0'

    gate = page.graph_add_node(K2Node_IfThenElse, 780, y)
    link(waiting, 'ReturnValue', gate, 'Condition')
    chain(gate)

    #The one remembered setting, read whenever a map is picked. Whichever map it was last set
    #on, it applies to all of them - see the note by PREFS_LAST about why this is not per map.
    there = page.graph_add_node_call_function(GameplayStatics.DoesSaveGameExist, 1100, y)
    mcd_ui.set_default(there, 'SlotName', mcd_ui.PREFS_LAST)
    there.node_find_pin('UserIndex').default_value = '0'
    link(gate, 'then', there, 'execute')

    known = page.graph_add_node(K2Node_IfThenElse, 1500, y)
    link(there, 'ReturnValue', known, 'Condition')
    link(there, 'then', known, 'execute')

    #--- something to go on: read it back
    read = page.graph_add_node_call_function(GameplayStatics.LoadGameFromSlot, 1800, y)
    mcd_ui.set_default(read, 'SlotName', mcd_ui.PREFS_LAST)
    read.node_find_pin('UserIndex').default_value = '0'
    link(known, 'then', read, 'execute')

    mine = page.graph_add_node_dynamic_cast(prefs, 2150, y)
    link(read, 'ReturnValue', mine, 'Object')
    link(read, 'then', mine, 'execute')
    mcd_ui.reconstruct(mine)
    holds = as_pin(mine)

    before = None
    for ours, theirs in [('Difficulty', 'Diff'), ('Threat', 'Threat'), ('Endless', 'Endless')]:
        got = page.graph_add_node_variable_get(theirs, prefs, 2450, y + 200)
        link(mine, holds, got, 'self')

        put = page.graph_add_node_variable_set(ours, None, 2700, y)
        link(got, theirs, put, ours)
        link(mine if before is None else before, 'then', put, 'execute')

        before, y = put, y + 170

    #--- nothing has been played yet on this install: the floor
    fresh = None
    for ours, first in [('Difficulty', '1'), ('Threat', '1'), ('Endless', '0')]:
        put = page.graph_add_node_variable_set(ours, None, 1800, y)
        put.node_find_pin(ours).default_value = first
        link(known, 'else', put, 'execute') if fresh is None else link(fresh, 'then', put, 'execute')
        fresh, y = put, y + 170

    #--- either way, say so on screen
    #
    #Difficulty is a WORD rather than its number, so it is chosen rather than converted. Two
    #nested picks cover the three the panel offers, which is the same thing as a switch and
    #fewer nodes.
    is_two = page.graph_add_node_call_function(KismetMathLibrary.EqualEqual_IntInt, 300, y)
    link(page.graph_add_node_variable_get('Difficulty', None, 60, y), 'Difficulty', is_two, 'A')
    is_two.node_find_pin('B').default_value = '2'

    is_three = page.graph_add_node_call_function(KismetMathLibrary.EqualEqual_IntInt, 300, y + 140)
    link(page.graph_add_node_variable_get('Difficulty', None, 60, y + 140), 'Difficulty',
         is_three, 'A')
    is_three.node_find_pin('B').default_value = '3'

    middle = page.graph_add_node_call_function(KismetMathLibrary.SelectString, 600, y)
    mcd_ui.set_default(middle, 'A', 'Adventure')
    mcd_ui.set_default(middle, 'B', 'Default')
    link(is_two, 'ReturnValue', middle, 'bPickA')

    worst = page.graph_add_node_call_function(KismetMathLibrary.SelectString, 900, y)
    mcd_ui.set_default(worst, 'A', 'Apocalypse')
    link(middle, 'ReturnValue', worst, 'B')
    link(is_three, 'ReturnValue', worst, 'bPickA')

    worded = page.graph_add_node_call_function(KismetTextLibrary.Conv_StringToText, 1200, y)
    link(worst, 'ReturnValue', worded, 'InString')

    told = page.graph_add_node_call_function(TextBlock.SetText, 1500, y)
    link(page.graph_add_node_variable_get('DifficultyValue', None, 1300, y + 140),
         'DifficultyValue', told, 'self')
    link(worded, 'ReturnValue', told, 'InText')

    #Both branches arrive here. Two exec outputs into one input is allowed, and it is what keeps
    #the readout wiring from being written twice.
    link(before, 'then', told, 'execute')
    link(fresh, 'then', told, 'execute')

    last = told
    for ours, label in [('Threat', 'ThreatValue'), ('Endless', 'EndlessValue')]:
        y += 170
        number = page.graph_add_node_call_function(KismetTextLibrary.Conv_IntToText, 1200, y)
        link(page.graph_add_node_variable_get(ours, None, 1000, y + 140), ours, number, 'Value')

        #Grouping off - it is the thing that puts a comma in a four figure number, and these are
        #a threat level and a tier rather than quantities.
        mcd_ui.set_default(number, 'bUseGrouping', 'false')

        one = page.graph_add_node_call_function(TextBlock.SetText, 1500, y)
        link(page.graph_add_node_variable_get(label, None, 1300, y + 140), label, one, 'self')
        link(number, 'ReturnValue', one, 'InText')
        link(last, 'then', one, 'execute')
        last = one

    y += 170
    settled = page.graph_add_node_variable_set('Pending', None, 1800, y)
    settled.node_find_pin('Pending').default_value = '0'
    link(last, 'then', settled, 'execute')

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

            #The caption is a literal rather than the number formatted at run time. Every one of
            #these presses knows its own value at build time, and "Adventure" is not something
            #the int 2 could be turned into on screen anyway.
            shown = page.graph_add_node_call_function(TextBlock.SetText, 1500, y)
            link(page.graph_add_node_variable_get(store + 'Value', None, 1300, y + 140),
                 store + 'Value', shown, 'self')
            says(shown, made['said'][store][value])
            link(remember, 'then', shown, 'execute')

            held = hold(page, y)
            link(shown, 'then', held, 'execute')

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
    wire_play.launch(page, widget, gate, y, prefs)
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
    build_graph(widget, made, prefs_class())

    keep(widget)
    say('panel built: ' + WIDGET)


main()
