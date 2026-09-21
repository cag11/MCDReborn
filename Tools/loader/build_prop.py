"""
Props: things that stand in the world and open a panel when clicked.

The Camp's map table is the first one and deliberately not the only one - this is a table of
props, and adding the next is an entry rather than a script. Each entry becomes two assets: an
actor that carries the mesh and the click, and a level holding one of that actor at a place. The
level is what the loader streams in; the actor is what the level places.

    UE4Editor-Cmd.exe <project>.uproject -run=Py <this file> -unattended -nopause -nosplash

Then cook, and install each level as a payload for its trigger with MCD Reborn.

Where the shape came from
-------------------------
Blossoming Isles, taken apart. Its `BP_SakuTable` is a static mesh with one event -
ReceiveActorOnClicked - which counts the widgets of its panel's class, and makes one if there are
none. That count is not a nicety: without it every click stacks another copy of the panel.

That mod proves the thing everything here rests on: a level streamed in by a loader can carry a
blueprint that the game then runs. The rest is furniture.

What it does NOT prove is that clicking works by itself. `APlayerController::bEnableClickEvents`
is a bitfield the 4.22 constructor never initialises, so it is false until something sets it, and
while it is false ReceiveActorOnClicked is never dispatched at all. Blossoming ships nothing that
sets it and its table works - so this game turns it on somewhere - but nobody has said where, and
a feature resting on an unexamined "somewhere" is a feature that breaks for one person and nobody
can say why. So it is set here, on BeginPlay, every time. Writing true over true costs nothing.

What it will not do
-------------------
The meshes are STUBS at the game's own paths - an engine cube standing in, so the editor has
something to point at. At run time the game's real mesh loads. They must never be shipped, which
is what MCD Reborn's rule about refusing to install anything the game already has is for.
"""

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
    Actor,
    GameplayStatics,
    KismetMathLibrary,
    KismetSystemLibrary,
    UserWidget,
    WidgetBlueprintLibrary,
    PlayerController,
    WorldFactory,
    K2Node_IfThenElse,
    K2Node_SpawnActorFromClass,
)
from unreal_engine.structs import Vector                                          # noqa: E402

import mcd_ui                                                                     # noqa: E402
from mcd_ui import say, keep, on_disk, link, event                                # noqa: E402


#--- the props -----------------------------------------------------------------------------------
#
#  name     what the actor and level are called
#  looks    a GAME blueprint that draws something, by the path the game keeps it at
#  hit      half-extents of the invisible box that catches the click, in centimetres
#  where    Unreal centimetres, Z up. A Dungeons block is a metre, so this is blocks x 100.
#  opens    the widget class to put on screen when it is clicked, or None for a prop that just
#           stands there
#  trigger  Menu, Lobby or Ingame - which of the loader's folders the level is installed into
#
#The map table's position is Blossoming Isles': its own table sits at (15950, 9150, 11800), which
#is a spot in the Camp somebody has already stood in front of and clicked. Borrowing a proven
#coordinate is cheaper than finding one by installing and looking, and it can be moved once there
#is something to move.
PROPS = [
    {
        'name': 'MapTable',

        #What it looks like: the GAME's own blueprint, stubbed empty here and real at run time.
        #Drawn by a ChildActorComponent rather than rebuilt out of a mesh, which means the table
        #arrives with its own mesh, material, scale and 90-degree roll already right instead of
        #us reproducing four values the game states itself.
        #A signpost with a hanging board rather than a second map table. Screened against four
        #checks readable from the paks without launching anything: its meshes carry ConvexElems
        #(the click trace uses bTraceComplex=false, so a mesh with no simple collision can never
        #be hit), their profile is BlockAll and not NoCollision, the blueprint declares ZERO
        #functions, and it imports no /Script/Dungeons.
        #
        #That last pair is the one that matters. Anything with an ubergraph RUNS IT, in the Camp,
        #on our actor's transform, with bEnableClickEvents freshly forced true - and the game's
        #own interactables are exactly the things that would then compete for the same click. The
        #previous choice passed by luck; this one passed on purpose.
        #
        #It is also placed in seven mission sublevels and in NO Lobby sublevel, so the Camp does
        #not already contain one. Its siblings Smithy and Swords do - they are the blacksmith's
        #and the luxury merchant's signs - which is why the Tavern board is the one borrowed.
        'looks': '/Game/Decor/Prefabs/ArchIllegerStatue/BP_VillagerStatuePodium',

        #How close to the prop a click has to land, in centimetres.
        #
        #There is no collision shape of our own any more. A BoxComponent was tried and did two
        #things wrong at once: it never received a click, and being BlockAll it blocked the PAWN
        #channel too, so it walled the player away from the table. The screenshot of somebody
        #stuck against nothing is what named it.
        #
        #The click is traced here instead, against whatever the world already has - which for
        #the table is the game's own mesh, with real per-triangle collision - and accepted when
        #the hit lands within this distance of the prop.
        #Retuned for a signpost. This is a radius around the actor's PIVOT, not a hitbox, so it
        #accepts any Visibility hit within it - including the bare floor beside the prop, which in
        #a click-to-move game is being walked on constantly. Four metres was survivable around a
        #three metre table and would be four metres of clickable ground around a sign half that
        #size. Bytecode, so getting it wrong costs another cook rather than a nudge.
        'reach': 250.0,

        'where': (15950.0, 9150.0, 11800.0),

        #Which way it faces, in degrees of yaw.
        #
        #Authored NON-ZERO on purpose, and that is the whole reason this key exists rather than
        #being left at the default. Unreal serialises only what differs from the class default,
        #so a prop spawned facing forward has NO RelativeRotation in the cooked level at all -
        #there is no property to edit and nothing to patch, which is the same wall the panel's
        #slots hit with Visibility. Writing a real angle here creates the twelve bytes, and from
        #then on MCD Reborn can turn the prop in place with no editor and no cook.
        #
        #180 because the statue arrived with its back to the plaza.
        'facing': 180.0,
        'opens': '/Game/MCDReborn/UI/UMG_MCDRebornMaps',
        'trigger': 'Lobby',
    },
]

ACTORS = '/Game/MCDReborn/Actors/'
LEVELS = '/Game/MCDReborn/%s/'

#Above the game's own interface. The Cosmetics mod uses 112 and Blossoming Isles 9999, so there is
#no magic number here - only "higher than whatever the game added its HUD with".
Z_ORDER = '9999'


def stub_blueprint(path):
    """
    An empty stand-in for one of the game's blueprints, at the game's own path.

    Blueprints stub cleanly; STATIC MESHES DO NOT, and that is why nothing here duplicates one.
    Three ways were tried inside a `-run=Py` commandlet and all three fail:

      * duplicating an engine mesh (/Engine/BasicShapes/Cube) rebuilds it, and rebuilding a
        static mesh headlessly corrupts the heap and takes the process with it;
      * duplicating a mesh already in the project reports success and writes an 878 byte package
        against a 10,275 byte source - everything is lost and it will not load back;
      * making an empty StaticMesh trips `Assertion failed: Owner->IsMeshDescriptionValid(0)`,
        because a mesh with no geometry cannot be saved at all.

    Which turned out to be worth hitting. Drawing the game's own BLUEPRINT instead of rebuilding
    its mesh is the better answer anyway: BP_MapTableMesh already carries the mesh, the material,
    the x100 scale and the 90 degree roll, so none of those four has to be copied correctly.
    """
    there = on_disk(path, 'Blueprint')
    if there is not None:
        return there

    made = ue.create_blueprint(Actor, path)
    ue.compile_blueprint(made)
    keep(made)
    say('stub blueprint: ' + path)
    return made


def panel_class(path):
    """
    The widget class a prop opens, when its asset has been built.

    find_class only sees classes this session has loaded, and it RAISES rather than returning
    nothing - so asking for a class whose asset is merely sitting on disk kills the script
    outright. Hence the asset check first and the catch after it.
    """
    if path is None:
        return None

    if on_disk(path, 'WidgetBlueprint') is None:
        say('no panel built yet at %s - the prop will be placed without one' % path)
        return None

    try:
        return ue.find_class(path.rsplit('/', 1)[1] + '_C')
    except Exception as problem:
        say('the panel asset is there but its class would not resolve: %s' % problem)
        return None


def build_actor(prop):
    """
    The prop itself: a mesh, and a click that opens a panel.

    Built once and then left alone. A prop whose asset already exists is not rebuilt, because the
    generator and the thing it generated drift apart the moment the output is touched by hand, and
    the generator is the copy that looks authoritative.
    """
    path = ACTORS + 'BP_MCDReborn' + prop['name']

    there = on_disk(path, 'Blueprint')
    if there is not None:
        say('prop already there, leaving it alone: ' + path)
        return there

    actor = ue.create_blueprint(Actor, path)

    #What it looks like is spawned in the graph, not carried as a ChildActorComponent.
    #
    #A ChildActorComponent was tried first and put the table at the world ORIGIN: the component
    #itself sat correctly at 15950,9150,11800 while the actor it spawned read 0,0,0, because the
    #child is created before the owner's transform reaches it. Measured, not guessed - the
    #component and its child were read out of the running game and disagreed.
    #
    #Spawning it from BeginPlay at this actor's own transform cannot disagree with anything.

    panel = panel_class(prop.get('opens'))

    if panel is not None:
        page = actor.UberGraphPages[0]

        #ReceiveActorOnClicked rather than a trigger volume or a key. It is what the game's own
        #interactables answer to and what Blossoming's table uses, and it needs nothing set up on
        #the player controller in a game that is already click-to-move.
        #--- and first, the thing that makes any of it possible ---------------------------
        #
        #APlayerController::bEnableClickEvents is a bitfield the 4.22 constructor never sets, so
        #it is FALSE until somebody turns it on - and with it false ReceiveActorOnClicked is
        #never dispatched at all. Blossoming Isles ships nothing that sets it and its table
        #works, so this game turns it on somewhere; but "somewhere" is not a thing to bet a
        #feature on, and writing true over true costs nothing.
        begin = event(actor, Actor, 'ReceiveBeginPlay', 0, -500)

        who = page.graph_add_node_call_function(GameplayStatics.GetPlayerController, 300, -500)

        #By name rather than through a cast: PlayerController is the engine's, but the one this
        #game makes is its own subclass, and SetBoolPropertyByName needs no class at all.
        allow = page.graph_add_node_call_function(
            KismetSystemLibrary.SetBoolPropertyByName, 600, -500)
        link(who, 'ReturnValue', allow, 'Object')
        mcd_ui.set_default(allow, 'PropertyName', 'bEnableClickEvents')
        mcd_ui.set_default(allow, 'Value', 'true')
        link(begin, 'then', allow, 'execute')

        #--- and the thing people actually see -------------------------------------------
        if prop.get('looks'):
            looks = stub_blueprint(prop['looks'])

            spawn = page.graph_add_node(K2Node_SpawnActorFromClass, 1000, -500)
            spawn.node_find_pin('Class').default_object = looks.GeneratedClass
            mcd_ui.reconstruct(spawn)

            where = page.graph_add_node_call_function(Actor.GetTransform, 700, -300)
            link(where, 'ReturnValue', spawn, 'SpawnTransform')
            link(allow, 'then', spawn, 'execute')

        #--- the click, traced rather than waited for ------------------------------------
        #
        #ReceiveActorOnClicked was the obvious way and it never fired once. The reason is not
        #the player controller - bEnableClickEvents, bShowMouseCursor and bEnableMouseOverEvents
        #all read true out of the running game - and not the collision either, because the
        #BoxComponent that was supposed to catch the click blocked the PLAYER so thoroughly that
        #you could not walk to the table. It collided with a pawn and was never hit by a click.
        #
        #The difference is the trace: APlayerController::ClickEventsInternal uses
        #GetHitResultAtScreenPosition with bTraceComplex = TRUE, and a shape component has no
        #complex geometry to hit. Nothing about the box's size or profile was ever going to fix
        #that.
        #
        #So the trace is done here, where the flag is ours to set, against the collision the
        #world already has - which for the table is the game's own mesh, triangles and all.
        tick = event(actor, Actor, 'ReceiveTick', 0, 0)

        pc = page.graph_add_node_call_function(GameplayStatics.GetPlayerController, 200, 0)

        pressed = page.graph_add_node_call_function(
            PlayerController.WasInputKeyJustPressed, 500, 0)
        link(pc, 'ReturnValue', pressed, 'self')
        mcd_ui.set_default(pressed, 'Key', 'LeftMouseButton')

        asked = page.graph_add_node(K2Node_IfThenElse, 800, 0)
        link(pressed, 'ReturnValue', asked, 'Condition')
        link(tick, 'then', asked, 'execute')

        #TraceTypeQuery1 is Visibility - the first entry of ETraceTypeQuery, which is how the
        #engine spells the channel to Blueprint. And bTraceComplex stays FALSE, which is the
        #whole point of doing this here.
        under = page.graph_add_node_call_function(
            PlayerController.GetHitResultUnderCursorByChannel, 1100, 0)
        link(pc, 'ReturnValue', under, 'self')
        mcd_ui.set_default(under, 'TraceChannel', 'TraceTypeQuery1')
        mcd_ui.set_default(under, 'bTraceComplex', 'false')

        #No exec pins on it: the function is const, and UHT exposes a const BlueprintCallable as
        #PURE. So it is read where it is needed rather than run, and the chain goes straight from
        #the key test to the branch that asks what it found.
        got = page.graph_add_node(K2Node_IfThenElse, 1500, 0)
        link(under, 'ReturnValue', got, 'Condition')
        link(asked, 'then', got, 'execute')

        #Which prop was clicked, decided by distance rather than by identity. Comparing the hit
        #actor against this one would need an object variable and a self reference; measuring how
        #far the hit landed from where this prop stands needs neither, and reads the same.
        #GameplayStatics.BreakHitResult, not a generic BreakStruct node. FHitResult declares a
        #NATIVE break - meta=(HasNativeBreak="Engine.GameplayStatics.BreakHitResult") - so the
        #generic node refuses to expose its members and comes back with one lonely input pin and
        #no outputs at all.
        broken = page.graph_add_node_call_function(GameplayStatics.BreakHitResult, 1500, 400)
        link(under, 'HitResult', broken, 'Hit')

        mine = page.graph_add_node_call_function(Actor.K2_GetActorLocation, 1500, 700)

        gap = page.graph_add_node_call_function(KismetMathLibrary.Vector_Distance, 1800, 500)
        link(broken, 'Location', gap, 'V1')
        link(mine, 'ReturnValue', gap, 'V2')

        near = page.graph_add_node_call_function(KismetMathLibrary.Less_FloatFloat, 2100, 500)
        link(gap, 'ReturnValue', near, 'A')
        mcd_ui.set_default(near, 'B', str(prop.get('reach', 400.0)))

        close = page.graph_add_node(K2Node_IfThenElse, 2400, 0)
        link(near, 'ReturnValue', close, 'Condition')
        link(got, 'then', close, 'execute')

        make = page.graph_add_node_call_function(WidgetBlueprintLibrary.Create, 2700, 0)
        make.node_find_pin('WidgetType').default_object = panel

        link(pc, 'ReturnValue', make, 'OwningPlayer')
        link(close, 'then', make, 'execute')

        shown = page.graph_add_node_call_function(UserWidget.AddToViewport, 3000, 0)
        link(make, 'ReturnValue', shown, 'self')
        mcd_ui.set_default(shown, 'ZOrder', Z_ORDER)
        link(make, 'then', shown, 'execute')

    ue.compile_blueprint(actor)
    keep(actor)
    say('prop built: ' + path)
    return actor


def build_level(prop, actor):
    """
    The level the loader streams in, holding one of the prop where it goes.

    A world rather than a level, in the engine's terms: creating a map makes a UWorld and the
    ULevel inside it is what streaming talks about. Only the world has a factory.
    """
    path = (LEVELS % prop['trigger']) + prop['name']

    if on_disk(path, 'World') is not None:
        say('level already there: ' + path)
        return path

    world = WorldFactory().factory_create_new(path)
    x, y, z = prop['where']

    #ue.FVector, not the struct of the same meaning. actor_spawn is plugin API rather than
    #reflection, and it wants the plugin's own vector type - handed a
    #unreal_engine.structs.Vector it answers "location must be an FVector", which reads like the
    #numbers being wrong rather than the type.
    #ue.FRotator is (pitch, yaw, roll), the same order the struct serialises in - so a value put
    #in the wrong slot tips the prop over instead of turning it, which is easy to see and easy to
    #misread as the rotation not having applied at all.
    turn = ue.FRotator(0.0, float(prop.get('facing', 0.0)), 0.0)

    try:
        placed = world.actor_spawn(actor.GeneratedClass, ue.FVector(x, y, z), turn)
    except Exception as problem:
        say('could not spawn with a rotation (%s) - placing it unturned, which means the level '
            'will carry no RelativeRotation to edit later' % problem)
        placed = world.actor_spawn(actor.GeneratedClass, ue.FVector(x, y, z))

    #Said out loud because a prop at the origin is the failure this produces, and a Camp is big
    #enough that the origin is somewhere nobody walks.
    say('placed %s at %s' % (prop['name'], getattr(placed, 'get_actor_location', lambda: '?')()))

    keep(world)
    say('level built: ' + path)
    return path


def main():
    say('building %d prop(s)' % len(PROPS))

    for prop in PROPS:
        actor = build_actor(prop)
        where = build_level(prop, actor)
        say('%s done - cook, then install %s as a %s payload'
            % (prop['name'], where, prop['trigger']))


main()
