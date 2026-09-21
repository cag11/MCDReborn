"""
Builds MCD Reborn's blueprint loader, in Unreal, without anybody wiring a node.

Run this inside UE 4.22 with the UnrealEnginePython plugin installed. It creates everything the
loader is made of and compiles it:

    /Game/GameModes/Menu/BP_MenuGameMode       stub, never shipped
    /Game/GameModes/Lobby/BP_LobbyGameMode     stub, never shipped
    /Game/GameModes/Ingame/BP_IngameGameMode   stub, never shipped
    /Game/Decor/Prefabs/Tent/SM_Tent           stub, never shipped
    /Game/MCDReborn/UMG_MCDRebornLoader        the widget that watches and loads
    /Game/Decor/Prefabs/Tent/BP_Tent           the anchor that starts the widget

The stubs are empty assets at the game's own paths. They exist so the editor has something to
point at while building; at run time the reference is a path and the game resolves it to the real
asset. MCD Reborn refuses to ship anything the game already has, so they never leave this project
- which is what stops an empty BP_LobbyGameMode from being installed over the real one.

Why this exists at all: Epic's own Python API in 4.22 can create assets but cannot wire an event
graph. UnrealEnginePython can - graph_add_node_call_function, node_find_pin, make_link_to - which
is the whole difference between automating this and doing it by hand.

    https://github.com/20tab/UnrealEnginePython
    prebuilt: UnrealEnginePython_20190508_4_22_python36_embedded_win64.zip

Usage
-----
Drop the plugin in <Project>/Plugins/UnrealEnginePython, open the project, then either paste this
into the Python console or run it headless:

    UE4Editor-Cmd.exe <Project>.uproject -run=pythonscript -script="<path to this file>"

Then cook, and point MCD Reborn's "Install loader" at the cooked Content folder.
"""

import unreal_engine as ue

from unreal_engine.classes import (
    Actor,
    AssetRegistry,
    AssetRegistryHelpers,
    EdGraph,
    GameModeBase,
    GameplayStatics,
    KismetArrayLibrary,
    KismetMathLibrary,
    KismetStringLibrary,
    LevelStreamingDynamic,
    StaticMesh,
    StaticMeshComponent,
    UserWidget,
    WidgetBlueprintFactory,
    WidgetBlueprintLibrary,
    K2Node_BreakStruct,
    K2Node_Event,
    K2Node_IfThenElse,
    K2Node_MacroInstance,
    K2Node_MakeArray,
)
from unreal_engine.structs import GraphReference


# Where everything goes. These paths are not decoration - the anchor only works because it is at
# the path the game already places, and the widget only finds payloads because MCD Reborn installs
# them into the folder named here.
ANCHOR = '/Game/Decor/Prefabs/Tent/BP_Tent'
ANCHOR_MESH = '/Game/Decor/Prefabs/Tent/SM_Tent'
WIDGET = '/Game/MCDReborn/UMG_MCDRebornLoader'

# Trigger, the game mode that means it, and the folder payloads for it live in.
WATCHES = [
    ('Menu', '/Game/GameModes/Menu/BP_MenuGameMode', '/Game/MCDReborn/Menu'),
    ('Lobby', '/Game/GameModes/Lobby/BP_LobbyGameMode', '/Game/MCDReborn/Lobby'),
    ('Ingame', '/Game/GameModes/Ingame/BP_IngameGameMode', '/Game/MCDReborn/Ingame'),
]


#What this run created, kept because a freshly duplicated asset is in memory and not yet at the
#path it will eventually live at - so asking for it back by path finds nothing.
MADE = {}


def keep(asset):
    """
    Writes one asset to disk.

    editor_save_all() is the obvious call and it saves nothing here: it asks the editor to save
    its *dirty* packages, and a package built by a commandlet is not dirty in the way that check
    means. Saving each one by name is what actually produces files.
    """
    try:
        asset.save_package()
        return True
    except Exception as problem:
        say('could not save %s: %s' % (asset, problem))
        return False


def say(what):
    ue.log('[MCDReborn] ' + what)


def existing(path):
    """The asset at a path, or None. Re-running this script should not make a second of anything."""
    try:
        return ue.find_asset(path)
    except Exception:
        return None


def event_node(blueprint, name):
    """The event node a fresh blueprint already has, such as ReceiveBeginPlay."""
    for node in blueprint.UberGraphPages[0].Nodes:
        if node.is_a(K2Node_Event) and node.EventReference.MemberName == name:
            return node
    return None


def link(from_node, from_pin, to_node, to_pin):
    """
    One connection, said in one line, and loudly when it fails.

    Every pin here is found by name, and a name that is wrong in this engine version fails by
    returning nothing rather than by raising - so a silent None would produce a blueprint that
    compiles, runs, and does nothing. Better to stop at the line that was wrong.
    """
    a = from_node.node_find_pin(from_pin)
    b = to_node.node_find_pin(to_pin)
    if a is None:
        raise Exception('no pin "%s" on %s' % (from_pin, from_node.node_get_title()))
    if b is None:
        raise Exception('no pin "%s" on %s' % (to_pin, to_node.node_get_title()))
    a.make_link_to(b)

    #Both nodes are told, because a wildcard pin does not work out what it is from being connected
    #to - it works it out from being *notified* that it was. Without this the Make Array, the
    #ForEachLoop and the casts all compile as "type undetermined", which reads like a wiring
    #mistake and is really a missing nudge.
    try:
        from_node.node_pin_type_changed(a)
        to_node.node_pin_type_changed(b)
    except Exception:
        pass


# ---------------------------------------------------------------- stubs

def build_stubs():
    """
    The empty assets the editor needs to point at, at the game's own paths.

    A game mode stub has to be a GameModeBase so that a Cast node will accept it; nothing else
    about it matters, because the one the game loads is its own.
    """
    for trigger, game_mode, _ in WATCHES:
        if existing(game_mode):
            say('stub already there: ' + game_mode)
            continue
        stub = ue.create_blueprint(GameModeBase, game_mode)
        ue.compile_blueprint(stub)
        keep(stub)
        say('stub game mode: ' + game_mode)

    already = existing(ANCHOR_MESH)
    if already:
        MADE['mesh'] = already
        say('stub already there: ' + ANCHOR_MESH)
        return

    #Any mesh will do - the game's real tent is what loads. An engine cube is the one mesh every
    #installation is guaranteed to have, so it is the one copied.
    #The second argument is the whole package path, not the folder it sits in. Passing the folder
    #puts the asset one level up, named after the folder - which looks right in a log line and is
    #not the path the anchor refers to.
    name = ANCHOR_MESH.rsplit('/', 1)[1]
    MADE['mesh'] = ue.duplicate_asset('/Engine/BasicShapes/Cube.Cube', ANCHOR_MESH, name)
    keep(MADE['mesh'])
    say('stub mesh: ' + ANCHOR_MESH)


# ---------------------------------------------------------------- the widget

def build_widget():
    """
    The widget: on tick, work out where the game is, and load everything in the matching folder.

    It ticks rather than running once on construction because the game mode is not necessarily
    settled at the moment a widget is made, and a loader that asked too early would answer "no
    game mode" and give up. The Started flag is what makes it a one-shot anyway.
    """
    if existing(WIDGET):
        say('widget already there, leaving it alone: ' + WIDGET)
        return ue.find_asset(WIDGET)

    factory = WidgetBlueprintFactory()
    factory.ParentClass = UserWidget
    widget = factory.factory_create_new(WIDGET)

    ue.blueprint_add_member_variable(widget, 'Started', 'bool')
    ue.blueprint_add_member_variable(widget, 'Folder', 'string')

    #Compiled here, before a single node is placed. A variable that has only been *added* has no
    #property behind it yet, so a getter made now resolves to nothing - and a getter that resolves
    #to nothing is a wildcard, which is what every "type is undetermined" further down was really
    #complaining about.
    ue.blueprint_mark_as_structurally_modified(widget)
    ue.compile_blueprint(widget)

    page = widget.UberGraphPages[0]

    tick = event_node(widget, 'Tick')
    if tick is None:
        tick = page.graph_add_node_event(UserWidget, 'Tick', 0, 0)

    started_get = page.graph_add_node_variable_get('Started', None, 200, 200)
    gate = page.graph_add_node(K2Node_IfThenElse, 400, 0)

    link(tick, 'then', gate, 'execute')
    link(started_get, 'Started', gate, 'Condition')

    mode = page.graph_add_node_call_function(GameplayStatics.GetGameMode, 400, 300)

    #Everything the three branches share, built once and linked into from each of them.
    loader = build_loading_chain(page)

    #A chain of casts. Each failure falls through to the next, which is how three questions get
    #asked with one object and no game mode left unrecognised.
    previous_exec = (gate, 'else')
    for index, (trigger, game_mode, folder) in enumerate(WATCHES):
        game_mode_class = ue.find_class(game_mode.rsplit('/', 1)[1] + '_C')

        cast = page.graph_add_node_dynamic_cast(game_mode_class, 700, index * 250)
        link(previous_exec[0], previous_exec[1], cast, 'execute')
        link(mode, 'ReturnValue', cast, 'Object')

        set_folder = page.graph_add_node_variable_set('Folder', None, 1000, index * 250)
        set_folder.node_find_pin('Folder').default_value = folder

        link(cast, 'then', set_folder, 'execute')
        link(set_folder, 'then', loader['entry'], 'execute')

        previous_exec = (cast, 'CastFailed')

    ue.compile_blueprint(widget)
    keep(widget)
    say('widget built: ' + WIDGET)
    return widget


def build_loading_chain(page):
    """
    Scan the folder, and load every level in it.

    The scan is not optional. In a cooked build the asset registry does not know about a folder
    nothing has asked for, so GetAssetsByPath on its own returns an empty list and the loader
    looks like it works while loading nothing.
    """
    folder_get = page.graph_add_node_variable_get('Folder', None, 1300, 600)

    registry = page.graph_add_node_call_function(AssetRegistryHelpers.GetAssetRegistry, 1300, 800)

    #Not allocated a second time - the plugin has already done it, and asking again produces a
    #node with two of every pin.
    paths = page.graph_add_node(K2Node_MakeArray, 1300, 900)

    #On the registry itself rather than on the helpers. Measured: AssetRegistryHelpers has
    #GetAssetRegistry, GetAsset, ToSoftObjectPath and IsValid, and neither of these two.
    scan = page.graph_add_node_call_function(AssetRegistry.ScanPathsSynchronous, 1600, 800)

    to_name = page.graph_add_node_call_function(KismetStringLibrary.Conv_StringToName, 1600, 1000)

    assets = page.graph_add_node_call_function(AssetRegistry.GetAssetsByPath, 1900, 800)

    for_each = page.graph_add_node(K2Node_MacroInstance, 2200, 800)
    #find_object('ForEachLoop') finds nothing headless - the editor's macro package is not
    #loaded until something asks for it by name, and this is the asking.
    for_each.MacroGraphReference = GraphReference(MacroGraph=ue.load_object(
        EdGraph, '/Engine/EditorBlueprintResources/StandardMacros.StandardMacros:ForEachLoop'))
    for_each.node_allocate_default_pins()

    #There is no BreakAssetData function to call - it does not exist on the helpers in 4.22 -
    #so the struct is broken open directly, which is what that node is anyway.
    break_data = page.graph_add_node(K2Node_BreakStruct, 2500, 1000)
    break_data.StructType = ue.find_struct('AssetData')
    break_data.node_allocate_default_pins()
    to_string = page.graph_add_node_call_function(KismetStringLibrary.Conv_NameToString, 2800, 1000)
    load = page.graph_add_node_call_function(LevelStreamingDynamic.LoadLevelInstance, 3100, 800)

    done = page.graph_add_node_variable_set('Started', None, 3100, 1200)
    done.node_find_pin('Started').default_value = 'true'

    #The folder feeds both the scan and the lookup: one as an array of strings, one as a name.
    link(folder_get, 'Folder', paths, '[0]')
    link(folder_get, 'Folder', to_name, 'InString')

    link(registry, 'ReturnValue', scan, 'self')
    link(paths, 'Array', scan, 'InPaths')
    scan.node_find_pin('bForceRescan').default_value = 'true'

    #GetAssetsByPath is a pure node - it has no exec pins at all - so the run goes straight from
    #the scan to the loop, and the lookup simply feeds it.
    link(registry, 'ReturnValue', assets, 'self')
    link(to_name, 'ReturnValue', assets, 'PackagePath')

    link(scan, 'then', for_each, 'Exec')
    link(assets, 'OutAssetData', for_each, 'Array')

    link(for_each, 'LoopBody', load, 'execute')
    link(for_each, 'Array Element', break_data, 'AssetData')
    link(break_data, 'ObjectPath', to_string, 'InName')
    link(to_string, 'ReturnValue', load, 'LevelName')

    link(for_each, 'Completed', done, 'execute')

    return {'entry': scan}


# ---------------------------------------------------------------- the anchor

def build_anchor(widget):
    """
    The actor the game already places, replaced with one that also starts the widget.

    It keeps the tent's mesh because a replacement becomes the actor: whatever is not rebuilt here
    is simply gone from every level that places one.
    """
    if existing(ANCHOR):
        say('anchor already there, leaving it alone: ' + ANCHOR)
        return

    anchor = ue.create_blueprint(Actor, ANCHOR)

    mesh = ue.add_component_to_blueprint(anchor, StaticMeshComponent, 'Tent')

    #find_asset rather than load_object: the stub was duplicated into memory a moment ago and has
    #not been written to disk yet, so there is nothing at that path to load.
    tent = MADE.get('mesh') or ue.find_asset(ANCHOR_MESH)
    if tent is None:
        raise Exception('the tent mesh stub is missing: ' + ANCHOR_MESH)
    mesh.StaticMesh = tent

    page = anchor.UberGraphPages[0]

    begin = event_node(anchor, 'ReceiveBeginPlay')
    if begin is None:
        begin = page.graph_add_node_event(Actor, 'ReceiveBeginPlay', 0, 0)

    found = page.graph_add_node_call_function(
        WidgetBlueprintLibrary.GetAllWidgetsOfClass, 300, 0)
    found.node_find_pin('WidgetClass').default_object = widget.GeneratedClass

    count = page.graph_add_node_call_function(KismetArrayLibrary.Array_Length, 600, 200)
    none_yet = page.graph_add_node_call_function(KismetMathLibrary.EqualEqual_IntInt, 800, 200)
    none_yet.node_find_pin('B').default_value = '0'

    gate = page.graph_add_node(K2Node_IfThenElse, 1000, 0)

    make = page.graph_add_node_call_function(WidgetBlueprintLibrary.Create, 1300, 0)
    make.node_find_pin('WidgetType').default_object = widget.GeneratedClass

    show = page.graph_add_node_call_function(UserWidget.AddToViewport, 1600, 0)

    #Only when there is not one already. Several tents in one level would otherwise each make a
    #loader, and three loaders would load every payload three times.
    link(begin, 'then', found, 'execute')
    link(found, 'then', gate, 'execute')
    link(found, 'FoundWidgets', count, 'TargetArray')
    link(count, 'ReturnValue', none_yet, 'A')
    link(none_yet, 'ReturnValue', gate, 'Condition')

    link(gate, 'then', make, 'execute')
    link(make, 'then', show, 'execute')
    link(make, 'ReturnValue', show, 'self')

    ue.compile_blueprint(anchor)
    keep(anchor)
    say('anchor built: ' + ANCHOR)


# ---------------------------------------------------------------- go

def main():
    say('building the loader')
    build_stubs()
    widget = build_widget()
    build_anchor(widget)
    ue.editor_save_all()
    say('done - now cook, and point MCD Reborn at the cooked Content folder')


main()
