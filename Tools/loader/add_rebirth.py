"""
Makes a widget rebuild itself when the engine tears it down.

This is the mechanism that makes the community's overlay appear to survive a level change, found
by disassembling the Kismet bytecode of its Destruct handler:

    Destruct -> Create(WorldContext = Self, WidgetType = its own class, OwningPlayer = None)
             -> AddToViewport(0)

It is not persistence. On a level change the engine walks the viewport removing widgets, which
calls Destruct on each - and theirs uses that call to construct a fresh copy of itself and add it
back. The new one is made after the removal pass has already passed over it, so it survives, and
because OwningPlayer is None the new widget is outered to the GameInstance rather than to a player
controller belonging to the world being destroyed. The overlay is reborn every time, not kept.

Ours had no event graph at all - zero function exports - so nothing ran when it was destroyed and
it simply died at the first level change. That is the whole of the difference: every templating
flag, the widget trees, and both spawning actors were already identical.

Applied to both widgets, because both need to outlive the menu: the loader, so payloads for the
Camp and for missions are ever looked at, and the debug overlay, so it can report from anywhere.

    UE4Editor-Cmd.exe <project>.uproject -run=Py <this file> -unattended -nopause -nosplash
"""

import unreal_engine as ue

from unreal_engine.classes import UserWidget, WidgetBlueprintLibrary, K2Node_Event, K2Node_Self


WIDGETS = [
    '/Game/MCDReborn/UMG_MCDRebornLoader',
    '/Game/MCDReborn/UMG_MCDRebornDebug',
]

TRACE = open(r'C:\Users\Gaming\AppData\Local\Temp\rebirth_trace.txt', 'w')


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


def give_rebirth(path):
    widget = ue.find_asset(path) or ue.load_object(ue.find_class('WidgetBlueprint'), path)
    if widget is None:
        say('could not find %s' % path)
        return

    if event_node(widget, 'Destruct') is not None:
        say('%s already has a Destruct event, leaving it alone' % path)
        return

    page = widget.UberGraphPages[0]

    dying = page.graph_add_node_event(UserWidget, 'Destruct', 0, 2400)

    born = page.graph_add_node_call_function(WidgetBlueprintLibrary.Create, 400, 2400)
    pin(born, 'WidgetType').default_object = widget.GeneratedClass

    #Owning player is deliberately left empty. That is what outers the new widget to the game
    #instance instead of to a player controller belonging to the world being torn down, and it is
    #what the community's own Destruct handler does - the bytecode passes NoObject there.

    #The world context is the dying widget itself.
    me = page.graph_add_node(K2Node_Self, 200, 2560)
    try:
        pin(born, 'WorldContextObject').make_link_to(pin(me, 'self'))
    except Exception as problem:
        say('  could not wire the world context (%s) - it may be hidden and filled in' % problem)

    shown = page.graph_add_node_call_function(UserWidget.AddToViewport, 750, 2400)
    pin(shown, 'ZOrder').default_value = '0'

    pin(dying, 'then').make_link_to(pin(born, 'execute'))
    pin(born, 'then').make_link_to(pin(shown, 'execute'))
    pin(born, 'ReturnValue').make_link_to(pin(shown, 'self'))

    ue.compile_blueprint(widget)
    try:
        widget.save_package()
    except Exception as problem:
        say('  could not save: %s' % problem)

    say('%s now rebuilds itself on destruct' % path)


def main():
    say('start')
    for path in WIDGETS:
        give_rebirth(path)
    say('done')


main()
