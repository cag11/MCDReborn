"""
Gives the loader a line of text that says what it is doing.

Every failure so far has looked identical from inside the game: nothing happens. A payload that
never loaded, a widget that never drew and a cast that never matched are indistinguishable when
the only channel is "did something appear". Print String is compiled out of shipping builds, so
the game cannot be asked anything directly.

The community's loader solved this and it is worth copying: its widget carries a text block
reading "Blueprint Loader v1.0 by CCCode", shown at the menu and hidden elsewhere. A loader that
reports on itself is the difference between debugging and guessing.

Ours shows the live game mode instead of a version string, because that is the question actually
open: the Camp loads nothing, and nobody knows whether that is because the widget is not running
there, or because it is running and no cast matches. The answer is one line of text away.

    UE4Editor-Cmd.exe <project>.uproject -run=Py <this file> -unattended -nopause -nosplash

Close the Unreal editor first.
"""

import unreal_engine as ue

from unreal_engine.classes import (
    GameplayStatics,
    KismetSystemLibrary,
    KismetTextLibrary,
    TextBlock,
    K2Node_Event,
)
from unreal_engine.structs import AnchorData, Anchors, LinearColor, Margin, SlateColor, SlateFontInfo, Vector2D


WIDGET = '/Game/MCDReborn/UMG_MCDRebornLoader'
FONT = '/Game/Fonts/NewFive'

#Bottom left, where the game draws nothing, so it cannot be mistaken for part of the interface.
WHERE = Margin(Left=40, Top=900, Right=1200, Bottom=980)
SIZE = 20


def say(what):
    ue.log('[MCDReborn] ' + what)


def title(node):
    try:
        return node.node_get_title().replace('\n', ' ')
    except Exception:
        return '<untitled>'


def pin(node, name):
    p = node.node_find_pin(name)
    if p is None:
        raise Exception('no pin "%s" on %s (has %s)'
                        % (name, title(node), [x.name for x in node.node_pins()]))
    return p


def link(a_node, a_pin, b_node, b_pin):
    pin(a_node, a_pin).make_link_to(pin(b_node, b_pin))


def event_node(blueprint, name):
    for node in blueprint.UberGraphPages[0].Nodes:
        if node.is_a(K2Node_Event) and node.EventReference.MemberName == name:
            return node
    return None


def main():
    widget = ue.find_asset(WIDGET) or ue.load_object(ue.find_class('WidgetBlueprint'), WIDGET)
    if widget is None:
        raise Exception('could not find ' + WIDGET)

    font = ue.find_asset(FONT) or ue.load_object(ue.find_class('Font'), FONT)
    if font is None:
        raise Exception('the font stub is missing: ' + FONT)

    widget.modify()
    tree = widget.WidgetTree

    #The text block goes in the tree first, so the graph has something to talk to. Added as a
    #child rather than by assigning Slots, which does not persist - the mistake that made an
    #earlier widget draw nothing while looking entirely correct.
    status = TextBlock('Status', tree)
    status.Text = 'MCD Reborn loader'
    status.Font = SlateFontInfo(FontObject=font, Size=SIZE)
    status.ColorAndOpacity = SlateColor(SpecifiedColor=LinearColor(R=1.0, G=0.85, B=0.3, A=1.0))
    status.bIsVariable = True

    slot = tree.RootWidget.AddChild(status)
    slot.LayoutData = AnchorData(
        Offsets=WHERE,
        Anchors=Anchors(Minimum=Vector2D(X=0, Y=0), Maximum=Vector2D(X=0, Y=0)))

    widget.post_edit_change()
    ue.blueprint_mark_as_structurally_modified(widget)
    ue.compile_blueprint(widget)
    say('added the status text block')

    page = widget.UberGraphPages[0]

    tick = event_node(widget, 'Tick')
    if tick is None:
        raise Exception('the widget has no Tick event to hang this on')

    #What the game mode actually is, as a name a person can read. This is the open question: the
    #Camp loads nothing, and this says in one word whether that is Lobby, Ingame or something
    #nobody guessed.
    mode = page.graph_add_node_call_function(GameplayStatics.GetGameMode, 200, 1600)
    named = page.graph_add_node_call_function(KismetSystemLibrary.GetDisplayName, 500, 1600)
    link(mode, 'ReturnValue', named, 'Object')

    as_text = page.graph_add_node_call_function(KismetTextLibrary.Conv_StringToText, 800, 1600)
    link(named, 'ReturnValue', as_text, 'InString')

    show = page.graph_add_node_call_function(TextBlock.SetText, 1100, 1550)
    link(as_text, 'ReturnValue', show, 'InText')

    #The text block itself, as a variable on the widget - which is what bIsVariable above is for.
    block = page.graph_add_node_variable_get('Status', None, 850, 1720)
    link(block, 'Status', show, 'self')

    #Run before anything else the tick does, so it reports even when every cast fails - which is
    #precisely the case being investigated.
    was = pin(tick, 'then').get_linked_to()
    pin(tick, 'then').break_all_pin_links(True)
    link(tick, 'then', show, 'execute')
    for other in was:
        pin(show, 'then').make_link_to(other)

    ue.compile_blueprint(widget)
    try:
        widget.save_package()
    except Exception as problem:
        say('could not save: %s' % problem)

    say('the loader now reports its game mode on screen')


main()
