"""
Stage one of an in-game camera: proof that a payload can move the camera at all.

CAUTION - this script is behind the asset it built. Two bugs were fixed in the cooked blueprint
and never folded back into here, so re-running it as it stands would put both of them back:

  * it scaled the hero as a sign of life, from before the text overlay existed and there was any
    other way to ask the game a question. The scale was written as the pin default "1.5,1.5,1.5",
    which is not how a vector literal is spelled - the engine parsed it as (0,0,0) and the hero
    became invisible while remaining perfectly controllable. That node is now gone from this file.
  * it kept its own Fov variable, which starts at zero, so the first press set the field of view
    to 4 degrees and the screen went black. The shipped asset has no such variable and reads
    CameraComponent.FieldOfView live instead. That part has NOT been changed here, because the
    change cannot be checked without running the editor - so if this is ever re-run, do that
    first.

The general lesson, which cost more than either bug: a generator and the thing it generated drift
apart the moment the output is edited by hand, and the generator is the copy that looks
authoritative.

No menu, no sliders, no presets. One actor in one level, which on every tick asks whether PageUp
or PageDown was pressed and nudges the field of view. Twelve nodes.

It is deliberately the smallest thing that answers the only question worth answering first: does
this game's camera respond to the engine's own CameraComponent, or does its custom rig drive the
field of view every frame and stamp on anything set from outside? A menu built on top of an
answer nobody checked would be three hundred nodes of nothing.

Built with UnrealEnginePython, the same way as the loader:

    UE4Editor-Cmd.exe <project>.uproject -run=Py <this file> -unattended -nopause -nosplash

Then cook, and install the level as a payload with MCD Reborn.

What it makes:

    /Game/MCDReborn/Actors/BP_MCDRebornCamera   the actor that does the work
    /Game/MCDReborn/Lobby/CamProof              a level holding one of them

The level is what the loader loads; the actor is what the level places. Anything the loader runs
has to be a level, which is why a twelve node experiment still needs two assets.
"""

import unreal_engine as ue

from unreal_engine.classes import (
    Actor,
    CameraComponent,
    GameplayStatics,
    KismetMathLibrary,
    PlayerController,
    WorldFactory,
    K2Node_Event,
    K2Node_IfThenElse,
)


ACTOR = '/Game/MCDReborn/Actors/BP_MCDRebornCamera'
LEVEL = '/Game/MCDReborn/Lobby/CamProof'

#How far each press moves it, and the keys that do it. PageUp and PageDown because nothing in this
#game uses them, so nothing is being taken away while the experiment runs.
STEP = 4.0
WIDER = 'PageUp'
NARROWER = 'PageDown'

#Where it starts from. The game's own value, near enough - the point of the experiment is whether
#the number is obeyed, not what it should be.
START = 70.0



def say(what):
    ue.log('[MCDReborn] ' + what)


def keep(asset):
    """Writes an asset to disk. editor_save_all saves nothing from a commandlet."""
    try:
        asset.save_package()
    except Exception as problem:
        say('could not save %s: %s' % (asset, problem))


def pins_of(node):
    try:
        return [p.name for p in node.node_pins()]
    except Exception:
        return []


def link(from_node, from_pin, to_node, to_pin):
    """One connection, loud when a pin is not where it was expected."""
    a = from_node.node_find_pin(from_pin)
    b = to_node.node_find_pin(to_pin)
    if a is None:
        raise Exception('no pin "%s" on %s (has %s)' % (from_pin, from_node.node_get_title(), pins_of(from_node)))
    if b is None:
        raise Exception('no pin "%s" on %s (has %s)' % (to_pin, to_node.node_get_title(), pins_of(to_node)))
    a.make_link_to(b)


def event_node(blueprint, name):
    for node in blueprint.UberGraphPages[0].Nodes:
        if node.is_a(K2Node_Event) and node.EventReference.MemberName == name:
            return node
    return None


def set_default(node, pin_name, value):
    """A pin's literal, when the pin is there. Reported rather than assumed - the key pins in
    particular are structs, and a struct literal that did not take is a key that never fires."""
    pin = node.node_find_pin(pin_name)
    if pin is None:
        say('no pin "%s" on %s, leaving its default' % (pin_name, node.node_get_title()))
        return False
    try:
        pin.default_value = value
        say('set %s to "%s", reads back as "%s"' % (pin_name, value, pin.default_value))
        return True
    except Exception as problem:
        say('could not set %s.%s: %s' % (node.node_get_title(), pin_name, problem))
        return False


def build_actor():
    if ue.find_asset(ACTOR):
        say('actor already there: ' + ACTOR)
        return ue.find_asset(ACTOR)

    actor = ue.create_blueprint(Actor, ACTOR)

    ue.blueprint_add_member_variable(actor, 'Fov', 'float')

    #Compiled before a single node is placed, so the getters have a property to bind to. A
    #variable that has only been added has no skeleton class entry yet, and a getter made before
    #that resolves to nothing at all.
    ue.blueprint_mark_as_structurally_modified(actor)
    ue.compile_blueprint(actor)

    page = actor.UberGraphPages[0]

    tick = event_node(actor, 'ReceiveTick')
    if tick is None:
        tick = page.graph_add_node_event(Actor, 'ReceiveTick', 0, 0)

    controller = page.graph_add_node_call_function(GameplayStatics.GetPlayerController, 300, 400)

    #The camera, reached through the pawn. GetComponentByClass answers with an ActorComponent, so
    #it has to be told what it found before its field of view can be set.
    pawn = page.graph_add_node_call_function(GameplayStatics.GetPlayerPawn, 300, 700)
    component = page.graph_add_node_call_function(Actor.GetComponentByClass, 600, 700)
    component.node_find_pin('ComponentClass').default_object = CameraComponent
    camera = page.graph_add_node_dynamic_cast(CameraComponent, 900, 700)

    link(pawn, 'ReturnValue', component, 'self')
    link(component, 'ReturnValue', camera, 'Object')

    #The cast runs, rather than hanging off to one side being read. A Cast node is impure - it has
    #exec pins - and an impure node with nothing connected to them is pruned out of the graph
    #entirely, which leaves everything downstream reading a null and complaining that Target needs
    #a connection. So the tick goes through it first and the branches follow.
    link(tick, 'then', camera, 'execute')

    #One branch per key, the second asked only when the first did not fire.
    previous = (camera, 'then')
    for index, (key, direction) in enumerate([(WIDER, KismetMathLibrary.Subtract_FloatFloat),
                                              (NARROWER, KismetMathLibrary.Add_FloatFloat)]):
        pressed = page.graph_add_node_call_function(
            PlayerController.WasInputKeyJustPressed, 600, index * 300)
        link(controller, 'ReturnValue', pressed, 'self')
        set_default(pressed, 'Key', key)

        gate = page.graph_add_node(K2Node_IfThenElse, 900, index * 300)
        link(pressed, 'ReturnValue', gate, 'Condition')

        #The exec pin only exists if the node is impure; WasInputKeyJustPressed is pure, so the
        #run goes straight from one branch to the next.
        link(previous[0], previous[1], gate, 'execute')

        moved = page.graph_add_node_call_function(direction, 1200, index * 300)
        link(page.graph_add_node_variable_get('Fov', None, 1000, index * 300 + 150), 'Fov', moved, 'A')
        set_default(moved, 'B', str(STEP))

        remember = page.graph_add_node_variable_set('Fov', None, 1500, index * 300)
        link(moved, 'ReturnValue', remember, 'Fov')
        link(gate, 'then', remember, 'execute')

        apply_it = page.graph_add_node_call_function(CameraComponent.SetFieldOfView, 1800, index * 300)
        link(camera, 'AsCamera Component', apply_it, 'self')
        link(page.graph_add_node_variable_get('Fov', None, 1650, index * 300 + 150), 'Fov', apply_it, 'InFieldOfView')
        link(remember, 'then', apply_it, 'execute')

        previous = (gate, 'else')

    ue.compile_blueprint(actor)
    keep(actor)
    say('actor built: ' + ACTOR)
    return actor


def build_level(actor):
    """
    The level the loader will load, holding one of the actor.

    A world rather than a level, in the engine's terms: creating a map makes a UWorld, and the
    ULevel inside it is what streaming talks about. Only the world has a factory.
    """
    if ue.find_asset(LEVEL):
        say('level already there: ' + LEVEL)
        return

    world = WorldFactory().factory_create_new(LEVEL)
    world.actor_spawn(actor.GeneratedClass)
    keep(world)
    say('level built: ' + LEVEL)


def main():
    say('building the camera proof')
    actor = build_actor()
    build_level(actor)
    say('done - now cook, and install %s as a Lobby payload' % LEVEL)


main()
