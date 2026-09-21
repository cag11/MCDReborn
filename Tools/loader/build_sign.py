"""
Signs: the floating name over a prop, the way the Camp names its own.

    UE4Editor-Cmd.exe <project>.uproject -run=Py <this file> -unattended -nopause -nosplash

Then cook, and re-ship the prop's assets. This ADDS to a prop that already exists rather than
rebuilding it, because build_prop.py deliberately leaves a built prop alone - a generator and the
thing it generated drift apart the moment the output is touched, and rebuilding from scratch to
add one component would throw away a working table to change a label.

Why the text is baked in rather than looked up
----------------------------------------------
The game's own labels - MISSION SELECT, BLACKSMITH, GIFT WRAPPER - are almost certainly keys into
a string table, and a string table in this game is REGISTERED IN C++ (`LOCTABLE_FROMFILE_GAME`,
one line per table). A key the game does not already know resolves to nothing at all: that is the
`<MISSING STRING TABLE ENTRY>` that `slimysewers` produced, after the file was verified byte for
byte inside the pak.

So hooking our prop into the game's labelling system is the version that looks correct and cannot
work. A TextBlock with the words compiled into it asks the game for nothing, and there is nothing
to be missing.

It also stays editable afterwards. A cooked FText is Flags, HistoryType, Namespace, Key and
SourceString laid end to end, and the words can be made longer or shorter by taking the difference
out of the key - so MCD Reborn can rewrite this caption later without the property changing size
and without anybody cooking anything. See CookedEdit.setText.

Why a WidgetComponent in SCREEN space
-------------------------------------
World space would mean the sign faces one direction and is read at an angle from everywhere else,
and turning it to face the camera means doing that maths every frame. Screen space is the engine
doing it: the component's world position is projected, and the widget is drawn upright at that
point at a constant size. Which is what the game's own labels do - they do not shrink with
distance, and all of them face you at once.
"""

import os
import sys


def _here():
    """The folder these scripts live in. See build_prop.py for why this is three ways."""
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
    CanvasPanel,
    UserWidget,
    WidgetBlueprintFactory,
    WidgetComponent,
)
from unreal_engine.structs import IntPoint, Vector                                # noqa: E402

import mcd_ui                                                                     # noqa: E402
from mcd_ui import say, keep, on_disk, stretch, label, stub_font                   # noqa: E402


#--- the signs ------------------------------------------------------------------------------------
#
#A table like the props one, for the same reason: there will be more of these than there are
#today, and the next is meant to be an entry rather than a script.
#
#  actor    the prop this hangs over, by the path it was built at
#  says     the words. Upper case because the game's are, not because anything requires it
#  up       centimetres above the ACTOR'S ORIGIN, which is not the same as above the ground - a
#           blueprint's pivot is wherever the artist left it, and for the map table it is some way
#           below the mesh. Measured with PROBE_SITS rather than guessed: it reports how tall the
#           prop stands above its own pivot, which is the number this wants to clear.
SIGNS = [
    {
        'actor': '/Game/MCDReborn/Actors/BP_MCDRebornMapTable',
        'says': 'CUSTOM MAPS',

        #Lower than the prop is tall. 300 cleared the map table, which stood three metres up; a
        #signpost is half that and the label was left hanging in the air above it with nothing
        #underneath. The Camp's own labels sit just clear of the thing they name.
        'up': 160.0,
    },
]

UI = '/Game/MCDReborn/UI/'

#Matched to the Camp's own labels rather than chosen. BLACKSMITH and MISSION SELECT are what this
#sits among, and a label larger than theirs does not read as "one more of these" - it reads as ours,
#which is the opposite of the point.
SIGN_SIZE = 22

#The canvas the words are drawn on, in pixels. A screen-space WidgetComponent draws at this size
#whatever the distance, so it is the label's real size on screen and wants to be generous: text
#that does not fit is CLIPPED here, not shrunk.
DRAW = (512, 96)


def build_widget(sign, at):
    """The words themselves, as a widget asset."""
    there = on_disk(at, 'WidgetBlueprint')
    if there is not None:
        say('sign already there, leaving it alone: ' + at)
        return there

    factory = WidgetBlueprintFactory()
    factory.ParentClass = UserWidget
    widget = factory.factory_create_new(at)

    widget.modify()
    tree = widget.WidgetTree

    root = CanvasPanel('Root', tree)
    tree.RootWidget = root

    #The TITLE font, which is the game's own Minecraft Ten - the face its Camp labels are set in.
    #The pair matters: /Game/Fonts/Minecraft is the composite and 'Ten' is the typeface inside it,
    #and naming either one wrong falls back to Slate's default without saying so.
    face = stub_font(mcd_ui.TITLE_FONT)

    words = label(tree, 'Says', sign['says'], mcd_ui.INK, face, SIGN_SIZE,
                  typeface=mcd_ui.TITLE_FACE, outline=2)

    #Centred, so the sign hangs over the middle of the prop rather than starting there. The
    #enumerator is set by NUMBER because ETextJustify is a byte the plugin takes as one, and
    #reported either way - a justification that did not take looks like a sign that is simply
    #off to one side.
    try:
        words.Justification = 1                                      # ETextJustify::Center
        say('  justification reads back %s' % words.Justification)
    except Exception as problem:
        say('could not centre the sign: %s' % problem)

    stretch(root, words)

    keep(widget)
    say('sign built: ' + at)
    return widget


def hang(sign, widget):
    """Puts the sign on the prop."""
    actor = on_disk(sign['actor'], 'Blueprint')
    if actor is None:
        raise Exception('no prop at %s to hang a sign on - run build_prop.py first'
                        % sign['actor'])

    #One sign, however many times this runs.
    #
    #add_component_to_blueprint ADDS - it does not replace - so a second run against a surviving
    #actor gives a second WidgetComponent drawing the same words in the same place. Two labels at
    #identical positions look like one slightly bolder label, which is the worst possible symptom:
    #nothing is obviously wrong and the cause is invisible.
    for already in (actor.SimpleConstructionScript.AllNodes or []):
        try:
            if already.get_variable_name() == 'Sign':
                say('a sign is already on %s, leaving it alone' % sign['actor'])
                return
        except Exception:
            continue

    made = ue.add_component_to_blueprint(actor, WidgetComponent, 'Sign')
    made.WidgetClass = widget.GeneratedClass

    #Screen, not World. See the module note.
    made.Space = 1                                                   # EWidgetSpace::Screen
    made.DrawSize = IntPoint(X=DRAW[0], Y=DRAW[1])
    made.RelativeLocation = Vector(X=0.0, Y=0.0, Z=sign['up'])

    #Nothing about a label should collide with anything. A widget component defaults to having
    #collision, and this prop's click is a TRACE against whatever the world has - so a sign with
    #collision is a sign that can be clicked instead of the table, from several metres away,
    #because it is the thing nearest the cursor.
    try:
        made.bGenerateOverlapEvents = False
        made.SetCollisionEnabled = 0
    except Exception:
        pass

    ue.compile_blueprint(actor)
    keep(actor)
    say('sign hung on %s at +%.0f' % (sign['actor'], sign['up']))


def main():
    say('building %d sign(s)' % len(SIGNS))

    for sign in SIGNS:
        at = UI + 'UMG_MCDRebornSign_' + sign['actor'].rsplit('/', 1)[1].replace('BP_MCDReborn', '')
        widget = build_widget(sign, at)
        hang(sign, widget)

    say('signs done - cook, then re-ship the prop')


main()
