"""
The Play button: build a LevelSettings and hand it to the game's own mission launcher.

Imported by build_maps_menu.py rather than run on its own. It is a separate file because this is
the half that reaches into the GAME's types rather than the engine's, and when it breaks it should
be obvious which half broke.

What it calls
-------------
    UDungeonsGameInstance::BeginLoadingScreenWithTravel(
        LevelSettings, mapLoadType, fadeOutTime, fadeinTime, PlayerController, connectionString)

Six arguments, read off the real UFunction with MCDReborn.exe PROBE_STRUCT. `mapLoadType` 6 is
EMapLoadType::TravelIngameServer - travel to the ingame map as the server, which is what starting
a mission from the Camp means.

Why the stub resolves to the real thing
---------------------------------------
Everything here is built against declarations in this project's `Dungeons` C++ module, which
declares nothing that runs. A cooked asset records an import as `/Script/<Module>.<Class>`, the
module is named `Dungeons`, and the game's own module is called Dungeons - so at run time the
import lands on the game's real class. The engine resolves it by NAME: name, outer, and the
UProperty class of each member. Field order and offsets are irrelevant and are recomputed from
the real struct, which is why a stub that omits the tower fields is safe.

That holds because this game is UE 4.22. Unversioned property serialization - offsets rather than
names - arrived in 4.25, and none of this would survive it.
"""

import os
import sys


def _here():
    said = os.environ.get('MCDREBORN_LOADER')
    if said and os.path.isdir(said):
        return said
    try:
        return os.path.dirname(os.path.abspath(__file__))
    except NameError:
        return os.getcwd()


HERE = _here()
sys.path.insert(0, HERE)

import unreal_engine as ue                                                        # noqa: E402

from unreal_engine.classes import (                                               # noqa: E402
    GameplayStatics,
    KismetMathLibrary,
    UserWidget,
    K2Node_MakeArray,
    K2Node_MakeStruct,
)

#The game's own game instance, reached the same way every other class here is. It exists in this
#editor because this project's `Dungeons` module declares a stub of it - see the note at the top
#about why a stub is enough.
from unreal_engine.classes import DungeonsGameInstance                            # noqa: E402

import mcd_ui                                                                     # noqa: E402
from mcd_ui import say, keep, on_disk, link, pin                                  # noqa: E402


WIDGET = '/Game/MCDReborn/UI/UMG_MCDRebornMaps'

#EMapLoadType::TravelIngameServer. A number, because the stub types this parameter as a uint8 -
#an enum pin refuses a number outright ("'6' is not a valid enumerant of '<EMapLoadType>'") and
#cannot take a wire from a byte either, which is the whole reason the stub uses bytes. See the
#note above FMissionDifficulty in DungeonsLevelSettings.h.
TRAVEL = '6'

#Every DLC. The game is asked what the player owns rather than told, everywhere else - but this
#struct carries the list, and Blossoming Isles fills it with all six rather than leaving it
#empty, so that is what is copied.
OWNED_DLC = ['1', '2', '3', '4', '5', '6']

#The Unreal map every mission is played on. Missions are voxel data loaded INTO it, so this is
#the same string for all fifty-six and is not the thing that picks one.
INGAME = 'ingame'

#What the game rolls for its own missions - Blossoming Isles copies the range exactly.
SEED_LOW, SEED_HIGH = '1', '563'

#Blossoming Isles passes one literal here on all sixty-three of its buttons rather than one per
#mission, which says the game does not read it for anything that matters. Copied rather than
#invented, because a value known to work beats a plausible one.
GUID = '371f2008-ba74-4a80-b64e-3286df40ca65'


def maker(page, struct, x, y):
    """
    A MakeStruct node for one of the game's structs.

    node_reconstruct is what turns the node from a blank into one with a pin per member: setting
    StructType alone leaves it believing it has no type, and every link made afterwards goes
    nowhere. The pins that come back are this project's STUB's members, which is the point - a
    stub that omits a field simply never writes it, and the real struct's own constructor decides
    what is there instead.
    """
    node = page.graph_add_node(K2Node_MakeStruct, x, y)
    node.StructType = ue.find_struct(struct)
    node.node_reconstruct()

    say('%s pins: %s' % (struct, [p.name for p in node.node_pins()]))
    return node


def array(page, of, x, y, values=()):
    """
    A MakeArray node, which an array pin cannot do without.

    An unwired array pin is a compile error - "Array inputs must have an input wired into them" -
    even when the array wanted is empty. So every one of them gets a node, and an empty array is
    a node with NO input pins rather than one holding a default element: MakeArray starts with a
    single pin, and leaving it would pass a one-element array of nothing, which for ownedDLCs
    would read as owning DLC number zero.

    NumInputs is a UPROPERTY, so it can simply be set. The node is returned UNRECONSTRUCTED,
    because a MakeArray with no inputs has no idea what it is an array of - "The type of Array
    is undetermined" - and learns it from whatever its output is plugged into. So the caller
    links it first and reconstructs after.
    """
    node = page.graph_add_node(K2Node_MakeArray, x, y)
    node.NumInputs = len(values)
    return node


def launch(page, widget, gate, y, prefs):
    """
    Everything that happens when Play is pressed.

    Handed a branch that is already wired into the panel's chain of tests, because working out
    where that chain ends from outside is guesswork - an earlier attempt hunted the graph for an
    unconnected `else` pin, which is both fragile and unnecessary when the generator that built
    the chain knows perfectly well where it stops.
    """
    #--- the numbers the game wants as bytes -----------------------------------------------
    #An int variable cannot go straight into an enum pin, but a byte can: Blossoming Isles
    #feeds these pins with MakeLiteralByte and raw numbers, which is also why it never has to
    #import ELevelNames at all.
    bytes_of = {}
    for at, name in enumerate(['Mission', 'Difficulty', 'Threat']):
        conv = page.graph_add_node_call_function(KismetMathLibrary.Conv_IntToByte,
                                                 400, y + 400 + at * 150)
        link(page.graph_add_node_variable_get(name, None, 150, y + 400 + at * 150), name,
             conv, 'InInt')
        bytes_of[name] = conv

    #--- the structs, innermost first ------------------------------------------------------
    #Apocalypse+ - the tiers above threat level seven, and a different field entirely. Zero
    #means none, which is what every launch sent before the panel could say otherwise.
    struggle = maker(page, 'EndlessStruggle', 700, y + 1000)
    link(page.graph_add_node_variable_get('Endless', None, 450, y + 1000), 'Endless',
         struggle, 'Value')

    hard = maker(page, 'MissionDifficulty', 1000, y + 400)
    link(bytes_of['Mission'], 'ReturnValue', hard, 'mission')
    link(bytes_of['Difficulty'], 'ReturnValue', hard, 'difficulty')
    link(bytes_of['Threat'], 'ReturnValue', hard, 'threatLevel')
    link(struggle, 'EndlessStruggle', hard, 'EndlessStruggle')

    seed = page.graph_add_node_call_function(KismetMathLibrary.RandomIntegerInRange,
                                             1000, y + 900)
    mcd_ui.set_default(seed, 'Min', SEED_LOW)
    mcd_ui.set_default(seed, 'Max', SEED_HIGH)

    state = maker(page, 'MissionState', 1400, y + 400)
    link(hard, 'MissionDifficulty', state, 'MissionDifficulty')
    link(seed, 'ReturnValue', state, 'Seed')
    mcd_ui.set_default(state, 'Guid', GUID)

    #Empty, because the panel offers no items and the game fills them itself.
    items = array(page, 'InventoryItemData', 1100, y + 1500)
    link(items, 'Array', state, 'offeredItems')
    mcd_ui.reconstruct(items)

    dlc = array(page, 'EDLCName', 1100, y + 1700, OWNED_DLC)
    link(dlc, 'Array', state, 'ownedDLCs')
    mcd_ui.reconstruct(dlc)

    for at, value in enumerate(OWNED_DLC):
        mcd_ui.set_default(dlc, '[%d]' % at, value)

    say('step: emergent')
    emergent = maker(page, 'EmergentDifficulty', 1400, y + 1200)
    mcd_ui.set_default(emergent, 'raidDifficulty', '0')
    mcd_ui.set_default(emergent, 'midGameAffectorsNum', '0')

    say('step: settings')
    settings = maker(page, 'LevelSettings', 1800, y + 400)
    link(state, 'MissionState', settings, 'MissionState')
    link(emergent, 'EmergentDifficulty', settings, 'EmergentDifficulty')
    mcd_ui.set_default(settings, 'unrealMapName', INGAME)

    #The one field that actually picks the mission. A bare level name - no folder, no .json -
    #and when it names nothing the game loads Creeper Woods without saying so.
    link(page.graph_add_node_variable_get('Chosen', None, 1600, y + 800), 'Chosen',
         settings, 'levelFilename')

    say('step: keys')
    keys = array(page, 'String', 1600, y + 1000)
    link(keys, 'Array', settings, 'progressionKeys')
    mcd_ui.reconstruct(keys)

    #--- and the call -----------------------------------------------------------------------
    say('step: game instance')
    instance = page.graph_add_node_call_function(GameplayStatics.GetGameInstance, 1800, y + 1600)
    cast = page.graph_add_node_dynamic_cast(DungeonsGameInstance, 2100, y + 1600)
    link(instance, 'ReturnValue', cast, 'Object')

    #--- remembered, before anything travels -----------------------------------------------
    #
    #Written on Play rather than on each button press, because Play is the moment the choice is
    #real: somebody who opens a map, looks at the numbers and backs out has not decided anything,
    #and saving as they browsed would rewrite that map's settings from a glance.
    #
    #Created fresh rather than loaded and amended. The object holds three ints and all three are
    #about to be overwritten, so reading the old one first would be a load, a cast and a branch
    #to arrive at exactly the same bytes.
    say('step: remember')
    made = page.graph_add_node_call_function(GameplayStatics.CreateSaveGameObject, 1800, y + 2000)
    made.node_find_pin('SaveGameClass').default_object = prefs
    link(gate, 'then', made, 'execute')

    ours = page.graph_add_node_dynamic_cast(prefs, 2100, y + 2000)
    link(made, 'ReturnValue', ours, 'Object')
    link(made, 'then', ours, 'execute')
    mcd_ui.reconstruct(ours)
    holds = mcd_ui.as_pin(ours)

    before = ours
    for mine, theirs in [('Difficulty', 'Diff'), ('Threat', 'Threat'), ('Endless', 'Endless')]:
        put = page.graph_add_node_variable_set(theirs, prefs, 2400, y + 2000)
        link(ours, holds, put, 'self')
        link(page.graph_add_node_variable_get(mine, None, 2200, y + 2150), mine, put, theirs)
        link(before, 'then', put, 'execute')
        before = put

    written = page.graph_add_node_call_function(GameplayStatics.SaveGameToSlot, 2700, y + 2000)
    link(ours, holds, written, 'SaveGameObject')
    mcd_ui.set_default(written, 'SlotName', mcd_ui.PREFS_LAST)
    written.node_find_pin('UserIndex').default_value = '0'
    link(before, 'then', written, 'execute')

    #A Cast is impure, so it has to be RUN rather than read - an impure node with nothing on its
    #exec pins is pruned out of the graph entirely, and everything downstream then complains that
    #its target is unset.
    link(written, 'then', cast, 'execute')
    mcd_ui.reconstruct(cast)

    #Reached as an attribute of the class, which is how every other call node in this folder is
    #made. Asking the UObject for it by name does not work - there is no get_function - and the
    #attribute is the plugin's own route to the UFunction.
    say('step: the call')
    go = page.graph_add_node_call_function(
        DungeonsGameInstance.BeginLoadingScreenWithTravel, 2500, y + 400)

    link(cast, 'AsDungeons Game Instance', go, 'self')
    link(settings, 'LevelSettings', go, 'LevelSettings')
    mcd_ui.set_default(go, 'mapLoadType', TRAVEL)
    mcd_ui.set_default(go, 'fadeOutTime', '1.0')
    mcd_ui.set_default(go, 'fadeinTime', '1.0')
    mcd_ui.set_default(go, 'connectionString', '')

    who = page.graph_add_node_call_function(GameplayStatics.GetPlayerController, 2200, y + 1100)
    link(who, 'ReturnValue', go, 'PlayerController')

    link(cast, 'then', go, 'execute')

    #Gone before the level changes, so that nothing is left over the loading screen.
    say('step: remove')
    away = page.graph_add_node_call_function(UserWidget.RemoveFromParent, 2900, y + 400)
    link(go, 'then', away, 'execute')

    say('step: compile')
    ue.compile_blueprint(widget)
    keep(widget)
    say('Play wired')
