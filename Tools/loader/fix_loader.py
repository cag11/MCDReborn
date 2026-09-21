"""
Replaces the loader's one-shot latch with a memory of what it last loaded.

The bug this fixes: the widget carried a single `Started` flag, set after the first game mode it
recognised. The main menu is a rendered camp scene and it contains tents, so the anchor spawns
there, the widget starts ticking there, matches the menu, scans an empty folder, sets the flag and
blocks itself forever - and it persists into the Camp, where it will never load anything again.
A Menu payload therefore worked and a Lobby payload could not.

What it becomes: on every tick, work out which folder this moment wants, and load it whenever it
is not the one already loaded. A remembered string rather than a flag, which also covers the case
three separate flags would not - coming back to the Camp after a mission, where the widget was
destroyed on the way out and has to build itself again.

This edits the existing widget rather than regenerating it, because the graph was finished by hand
and regenerating would throw that away. Every connection it makes is a string or a bool, so none
of the wildcard trouble from building it the first time applies here.

    UE4Editor-Cmd.exe <project>.uproject -run=Py <this file> -unattended -nopause -nosplash

Close the Unreal editor first.
"""

import unreal_engine as ue

from unreal_engine.classes import (
    KismetStringLibrary,
    K2Node_CallFunction,
    K2Node_DynamicCast,
    K2Node_Event,
    K2Node_IfThenElse,
    K2Node_MacroInstance,
    K2Node_VariableGet,
    K2Node_VariableSet,
)


WIDGET = '/Game/MCDReborn/UMG_MCDRebornLoader'

#The variable that replaces the flag. A string, because what has to be remembered is which folder
#was loaded, not merely that something was.
REMEMBERS = 'LastFolder'
OLD_FLAG = 'Started'


def say(what):
    ue.log('[MCDReborn] ' + what)


def title(node):
    try:
        return node.node_get_title().replace('\n', ' ')
    except Exception:
        return '<untitled>'


def find(page, kind, text=None):
    """Every node of a kind, optionally whose title starts with some text."""
    found = []
    for node in page.Nodes:
        if not node.is_a(kind):
            continue
        if text is not None and not title(node).startswith(text):
            continue
        found.append(node)
    return found


def one(page, kind, text=None):
    found = find(page, kind, text)
    if len(found) != 1:
        raise Exception('expected one %s%s, found %d: %s'
                        % (kind, '' if text is None else ' called "%s"' % text,
                           len(found), [title(n) for n in found]))
    return found[0]


def pin(node, name):
    p = node.node_find_pin(name)
    if p is None:
        raise Exception('no pin "%s" on %s' % (name, title(node)))
    return p


def link(from_node, from_pin, to_node, to_pin):
    pin(from_node, from_pin).make_link_to(pin(to_node, to_pin))


def drop(page, node):
    """
    Cuts a node out of the graph by disconnecting it, rather than deleting it.

    graph_remove_node is in the plugin's source but not in the 4.22 binary this is run against -
    the published build is from 2019 and the source read to write this is master. It does not
    matter: a node with nothing connected to its exec pins is pruned by the compiler anyway, and
    an unconnected variable getter contributes nothing. What is left is a few orphans sitting in
    the graph doing nothing, which is untidy and harmless.
    """
    for p in node.node_pins():
        try:
            p.break_all_pin_links(True)
        except Exception:
            pass

    try:
        page.graph_remove_node(node)
    except Exception:
        pass


def main():
    widget = ue.find_asset(WIDGET) or ue.load_object(ue.find_class('WidgetBlueprint'), WIDGET)
    if widget is None:
        raise Exception('could not find ' + WIDGET)
    say('patching ' + WIDGET)

    #Added and compiled before any node refers to it, so the getters have a property to bind to.
    ue.blueprint_add_member_variable(widget, REMEMBERS, 'string')
    ue.blueprint_mark_as_structurally_modified(widget)
    ue.compile_blueprint(widget)

    page = widget.UberGraphPages[0]

    tick = one(page, K2Node_Event, 'Event Tick')
    scan = one(page, K2Node_CallFunction, 'Scan Paths Synchronous')
    loop = one(page, K2Node_MacroInstance)
    first_cast = one(page, K2Node_DynamicCast, 'Cast To BP_MenuGameMode')
    setters = find(page, K2Node_VariableSet, 'Set Folder')
    if len(setters) != 3:
        raise Exception('expected three Set Folder nodes, found %d' % len(setters))

    #--- out with the latch -------------------------------------------------------------------
    for node in find(page, K2Node_VariableGet, 'Get Started') + find(page, K2Node_VariableSet, 'Set Started'):
        say('disconnecting ' + title(node))
        drop(page, node)

    for branch in find(page, K2Node_IfThenElse):
        say('disconnecting the old ' + title(branch))
        drop(page, branch)

    #The tick now goes straight to the first question instead of through a flag.
    pin(tick, 'then').break_all_pin_links(True)
    link(tick, 'then', first_cast, 'execute')

    #--- in with the memory -------------------------------------------------------------------
    folder_now = page.graph_add_node_variable_get('Folder', None, 1050, 780)
    folder_was = page.graph_add_node_variable_get(REMEMBERS, None, 1050, 900)

    changed = page.graph_add_node_call_function(KismetStringLibrary.NotEqual_StrStr, 1250, 820)
    link(folder_now, 'Folder', changed, 'A')
    link(folder_was, REMEMBERS, changed, 'B')

    gate = page.graph_add_node(K2Node_IfThenElse, 1450, 760)
    link(changed, 'ReturnValue', gate, 'Condition')

    #Every branch of the cast chain now arrives at the gate rather than at the scan.
    pin(scan, 'execute').break_all_pin_links(True)
    for setter in setters:
        pin(setter, 'then').break_all_pin_links(True)
        link(setter, 'then', gate, 'execute')
    link(gate, 'then', scan, 'execute')

    #And what was loaded is remembered, where the flag used to be set.
    remember = page.graph_add_node_variable_set(REMEMBERS, None, 3100, 1200)
    folder_again = page.graph_add_node_variable_get('Folder', None, 2900, 1320)
    pin(loop, 'Completed').break_all_pin_links(True)
    link(loop, 'Completed', remember, 'execute')
    link(folder_again, 'Folder', remember, REMEMBERS)

    ue.compile_blueprint(widget)
    try:
        widget.save_package()
    except Exception as problem:
        say('could not save: %s' % problem)

    say('patched - the loader now reloads whenever the folder for the moment changes')


main()
