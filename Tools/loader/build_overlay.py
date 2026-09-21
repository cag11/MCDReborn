"""
Text on the screen, from a payload. The debug channel this project did not have.

Print String is the usual way to ask a running game a question, and it is compiled out of
shipping builds - which is what Minecraft Dungeons is - so it answers nothing. That left every
failure looking the same: a payload that never loaded, an actor that never spawned and a
function that did nothing were all just "nothing happened".

A UMG text block does work in a shipping build. That is not a guess: the published Camera
Coordinates Overlay mod is exactly this, and it works. Its shape is copied here - an actor that
creates a widget on begin play and adds it to the viewport - because it is known to work, not
because it is the only way.

Two things learned from reading it that are not obvious:

  * the text block needs a real font or it draws nothing, and it uses the game's own
    /Game/Fonts/NewFive, referenced but not shipped
  * the widget is added by an ordinary actor, so anything that can place an actor can put text
    on the screen

This first version has no graph in the widget at all. The text is a literal, set on the widget
tree itself, because the question being asked is only "did any of this run" and every node that
could answer it is also a node that could be the reason it did not.

    UE4Editor-Cmd.exe <project>.uproject -run=Py <this file> -unattended -nopause -nosplash

Close the Unreal editor first - it holds the project, and this cannot open it while it does.

What it makes:

    /Game/Fonts/NewFive                        stub, never shipped - the game's own font
    /Game/MCDReborn/UMG_MCDRebornDebug         a text block saying it is alive
    /Game/MCDReborn/Actors/BP_MCDRebornDebug   puts that widget on the screen
    /Game/MCDReborn/Lobby/DebugText            a level holding one of them
"""

import unreal_engine as ue

from unreal_engine.classes import (
    Actor,
    CanvasPanelSlot,
    GameplayStatics,
    TextBlock,
    UserWidget,
    WidgetBlueprintFactory,
    WidgetBlueprintLibrary,
    WorldFactory,
    K2Node_Event,
)
from unreal_engine.structs import AnchorData, Anchors, Margin, SlateColor, SlateFontInfo, Vector2D, LinearColor


FONT = '/Game/Fonts/NewFive'
WIDGET = '/Game/MCDReborn/UMG_MCDRebornDebug'
ACTOR = '/Game/MCDReborn/Actors/BP_MCDRebornDebug'
LEVEL = '/Game/MCDReborn/Lobby/DebugText'

#Said plainly, because the only thing it has to prove is that it is on the screen.
MESSAGE = 'MCD Reborn payload running'

#Top left, inset a little from the corner so it is not under anything the game draws there.
WHERE = Margin(Left=40, Top=40, Right=900, Bottom=120)
SIZE = 28


def say(what):
    ue.log('[MCDReborn] ' + what)


def keep(asset):
    try:
        asset.save_package()
    except Exception as problem:
        say('could not save %s: %s' % (asset, problem))


def event_node(blueprint, name):
    for node in blueprint.UberGraphPages[0].Nodes:
        if node.is_a(K2Node_Event) and node.EventReference.MemberName == name:
            return node
    return None


def link(from_node, from_pin, to_node, to_pin):
    a = from_node.node_find_pin(from_pin)
    b = to_node.node_find_pin(to_pin)
    if a is None or b is None:
        raise Exception('missing pin %s / %s between %s and %s'
                        % (from_pin, to_pin, from_node.node_get_title(), to_node.node_get_title()))
    a.make_link_to(b)


def build_font():
    """
    The game's own font, stubbed so the text block has something to point at.

    Duplicated from an engine font, which makes a real Font asset at the right path in a line -
    and it is thrown away at install time like every other stub, because the game has its own.
    What ends up on screen is the game's font, not this one.
    """
    if ue.find_asset(FONT):
        say('stub already there: ' + FONT)
        return ue.find_asset(FONT)

    stub = ue.duplicate_asset('/Engine/EngineFonts/Roboto.Roboto', FONT, FONT.rsplit('/', 1)[1])
    keep(stub)
    say('stub font: ' + FONT)
    return stub


def build_widget(font):
    """
    One text block, with no graph behind it.

    The text is set on the widget tree rather than by an event, so there is nothing to run and
    nothing to fail. If this does not appear, the problem is upstream of anything this widget
    could have done wrong.
    """
    if ue.find_asset(WIDGET):
        say('widget already there: ' + WIDGET)
        return ue.find_asset(WIDGET)

    widget = WidgetBlueprintFactory().factory_create_new(WIDGET)
    widget.modify()

    tree = widget.WidgetTree

    text = TextBlock('OverlayText', tree)
    text.Text = MESSAGE
    text.Font = SlateFontInfo(FontObject=font, Size=SIZE)
    text.ColorAndOpacity = SlateColor(SpecifiedColor=LinearColor(R=1.0, G=1.0, B=1.0, A=1.0))

    #Added as a child rather than by assigning the panel's Slots list.
    #
    #The plugin's own example builds the slot by hand and assigns RootWidget.Slots, and that does
    #not persist: the saved widget came out with the text block, the canvas, the font and the
    #message all present and no Slots at all - three and a half kilobytes against the thirteen of
    #a mod that works. A widget whose layout holds nothing draws nothing, and every part of it
    #looks right in isolation, which is the worst kind of wrong.
    #
    #AddChild is the engine's own way and it makes the slot itself.
    slot = tree.RootWidget.AddChild(text)
    slot.LayoutData = AnchorData(
        Offsets=WHERE,
        Anchors=Anchors(Minimum=Vector2D(X=0, Y=0), Maximum=Vector2D(X=0, Y=0)))

    widget.post_edit_change()
    ue.compile_blueprint(widget)
    keep(widget)
    say('widget built: ' + WIDGET)
    return widget


def build_actor(widget):
    """The actor that puts the widget on the screen, shaped after the one mod known to work."""
    if ue.find_asset(ACTOR):
        say('actor already there: ' + ACTOR)
        return ue.find_asset(ACTOR)

    actor = ue.create_blueprint(Actor, ACTOR)
    page = actor.UberGraphPages[0]

    begin = event_node(actor, 'ReceiveBeginPlay')
    if begin is None:
        begin = page.graph_add_node_event(Actor, 'ReceiveBeginPlay', 0, 0)

    controller = page.graph_add_node_call_function(GameplayStatics.GetPlayerController, 300, 300)

    make = page.graph_add_node_call_function(WidgetBlueprintLibrary.Create, 600, 0)
    make.node_find_pin('WidgetType').default_object = widget.GeneratedClass

    show = page.graph_add_node_call_function(UserWidget.AddToViewport, 900, 0)

    link(begin, 'then', make, 'execute')
    link(controller, 'ReturnValue', make, 'OwningPlayer')
    link(make, 'then', show, 'execute')
    link(make, 'ReturnValue', show, 'self')

    ue.compile_blueprint(actor)
    keep(actor)
    say('actor built: ' + ACTOR)
    return actor


def build_level(actor):
    if ue.find_asset(LEVEL):
        say('level already there: ' + LEVEL)
        return

    world = WorldFactory().factory_create_new(LEVEL)
    world.actor_spawn(actor.GeneratedClass)
    keep(world)
    say('level built: ' + LEVEL)


def main():
    say('building the debug overlay')
    font = build_font()
    widget = build_widget(font)
    actor = build_actor(widget)
    build_level(actor)
    say('done - cook, then install /Game/MCDReborn as a Lobby payload folder')


main()
