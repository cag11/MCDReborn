"""
Makes the anchor run its check on begin play AND on every tick.

The first attempt moved the check from BeginPlay to Tick, and broke the one case that worked. The
reason is a flag that is easy to miss: an actor has both `bCanEverTick` and
`bStartWithTickEnabled`, and this one had the first set and the second clear - allowed to tick,
and starting with ticking switched off. So Tick never ran, and because BeginPlay had been
disconnected, nothing ran at all, at the menu or anywhere else.

Two lessons, both applied here:

  * BeginPlay is kept rather than replaced. It is the path known to work, and a second trigger
    should add to it, not take its place.
  * Ticking is turned on by calling SetActorTickEnabled rather than by writing the property on the
    class default - writing that struct did not persist, and read back as False immediately after
    being set, which was a warning worth heeding the first time.

    UE4Editor-Cmd.exe <project>.uproject -run=Py <this file> -unattended -nopause -nosplash
"""

import unreal_engine as ue
from unreal_engine.classes import Actor, K2Node_Event


ANCHOR = '/Game/Decor/Prefabs/Tent/BP_Tent'

#Written to a file as well as the log: a hard crash loses whatever the log had buffered, which is
#how an earlier run of this produced no output and no error either.
TRACE = open(r'C:\Users\Gaming\AppData\Local\Temp\anchor_trace.txt', 'w')


def say(what):
    TRACE.write(str(what) + '\n')
    TRACE.flush()
    try:
        ue.log('[MCDReborn] ' + str(what))
    except Exception:
        pass


def pin(node, name):
    p = node.node_find_pin(name)
    if p is None:
        raise Exception('no pin "%s" on %s (has %s)'
                        % (name, node.node_get_title(), [x.name for x in node.node_pins()]))
    return p


def event_node(bp, name):
    for n in bp.UberGraphPages[0].Nodes:
        if n.is_a(K2Node_Event) and n.EventReference.MemberName == name:
            return n
    return None


def main():
    say('start')
    bp = ue.find_asset(ANCHOR) or ue.load_object(ue.find_class('Blueprint'), ANCHOR)
    say('loaded %s' % bp)

    page = bp.UberGraphPages[0]
    say('got the graph')

    tick = event_node(bp, 'ReceiveTick')
    begin = event_node(bp, 'ReceiveBeginPlay')
    say('tick=%s begin=%s' % (tick is not None, begin is not None))
    if tick is None or begin is None:
        raise Exception('expected both a Tick and a BeginPlay event')

    #Whatever the tick currently runs is the check; begin play should run the same thing.
    check = pin(tick, 'then').get_linked_to()
    say('the check has %d entry connection(s)' % len(check))
    if not check:
        raise Exception('Tick is not connected to the check')

    enable = page.graph_add_node_call_function(Actor.SetActorTickEnabled, 200, 300)
    say('added SetActorTickEnabled, pins: %s' % [p.name for p in enable.node_pins()])
    pin(enable, 'bEnabled').default_value = 'true'

    pin(begin, 'then').break_all_pin_links(True)
    pin(begin, 'then').make_link_to(pin(enable, 'execute'))
    for other in check:
        pin(enable, 'then').make_link_to(other)
    say('begin play now enables ticking and runs the check')

    ue.compile_blueprint(bp)
    try:
        bp.save_package()
        say('saved')
    except Exception as problem:
        say('could not save: %s' % problem)

    cdo = bp.GeneratedClass.get_cdo()
    say('bCanEverTick=%s bStartWithTickEnabled=%s'
        % (cdo.PrimaryActorTick.bCanEverTick, cdo.PrimaryActorTick.bStartWithTickEnabled))
    say('done')


main()
