"""
Makes the anchor keep checking, instead of checking once.

Ours created the loader widget on BeginPlay: one chance, at the instant the tent spawns. If the
widget is later destroyed - and it is, every time the world changes - nothing ever builds another,
so the loader is alive at the main menu and dead everywhere after it.

The community's anchor does not work that way. Read out of its cooked asset, it carries
ReceiveTick, bCanEverTick and a Delay, and runs its "is the widget there, and if not make one"
check on every tick forever. That is the whole difference, and it is why theirs runs everywhere
and ours ran once.

This moves our check from BeginPlay to Tick. It deliberately does not copy their extra condition
of only creating at the main menu: with the check running always, the widget is rebuilt in any
world that has a tent in it, which is one fewer thing that has to be true.

    UE4Editor-Cmd.exe <project>.uproject -run=Py <this file> -unattended -nopause -nosplash

Close the Unreal editor first.
"""

import unreal_engine as ue

from unreal_engine.classes import Actor, K2Node_CallFunction, K2Node_Event


ANCHOR = '/Game/Decor/Prefabs/Tent/BP_Tent'


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


def event_node(blueprint, name):
    for node in blueprint.UberGraphPages[0].Nodes:
        if node.is_a(K2Node_Event) and node.EventReference.MemberName == name:
            return node
    return None


def main():
    anchor = ue.find_asset(ANCHOR) or ue.load_object(ue.find_class('Blueprint'), ANCHOR)
    if anchor is None:
        raise Exception('could not find ' + ANCHOR)

    page = anchor.UberGraphPages[0]

    begin = event_node(anchor, 'ReceiveBeginPlay')
    if begin is None:
        raise Exception('no BeginPlay event on the anchor - has this already been done?')

    #Whatever BeginPlay currently runs is exactly what should run on every tick instead.
    downstream = pin(begin, 'then').get_linked_to()
    if not downstream:
        raise Exception('BeginPlay is not connected to anything, so there is nothing to move')

    say('moving %d connection(s) from BeginPlay to Tick' % len(downstream))
    pin(begin, 'then').break_all_pin_links(True)

    tick = event_node(anchor, 'ReceiveTick')
    if tick is None:
        tick = page.graph_add_node_event(Actor, 'ReceiveTick', 0, 400)
        say('added a Tick event')

    for other in downstream:
        pin(tick, 'then').make_link_to(other)

    #An actor only ticks if it is allowed to. The blueprint's own default decides that, and one
    #created from a bare Actor does not have it on - so the graph would be correct and never run.
    try:
        cdo = anchor.GeneratedClass.get_cdo()
        cdo.PrimaryActorTick = cdo.PrimaryActorTick  # touch, then set the flag below
    except Exception:
        pass

    try:
        anchor.GeneratedClass.get_cdo().set_property('PrimaryActorTick.bCanEverTick', True)
        say('turned ticking on via the class default')
    except Exception as problem:
        say('could not set bCanEverTick directly (%s) - set it by hand if nothing happens' % problem)

    ue.compile_blueprint(anchor)
    try:
        anchor.save_package()
    except Exception as problem:
        say('could not save: %s' % problem)

    say('the anchor now checks on every tick instead of once')


main()
