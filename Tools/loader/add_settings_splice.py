"""
Moves the camera panel out of a floating overlay and into the game's own Settings screen.

The idea is Too Many Outfits': it does not build a cosmetics screen, it finds the game's real one
and splices itself into it. Read out of its bytecode, the trick is three calls -
GetAllWidgetsOfClass on a *vanilla* widget class, GetParent on one of the results, AddChild onto
that parent. It never needs to know the panel's name, its type or its layout.

Applied here that means:

    GetAllWidgetsOfClass(UMG_SettingsEntry_C)   one of the game's own settings rows
        -> Get[0] -> GetParent()               the VerticalBox the settings list lives in
        -> AddChild(our panel)                 our panel, now a row in the real screen

Why this is better than the F8 overlay it replaces, and not by a little: the escape menu already
releases the cursor and already pauses the game. Those were the two pieces of the overlay design
that were never proven, and splicing makes them somebody else's problem. It also means no key to
press, no key to clash with the Cosmetics mod over, and the panel appears and disappears with the
menu because it is part of it.

Read out of the extracted UMG_SettingsSubMenu (204 KB) and UMG_IngameMenu (261 KB):

  * the settings list is a VerticalBox - SlotAsVerticalBoxSlot, ContentHolder, ScrollboxExtended_C
  * its rows are UMG_SettingsEntry_C and UMG_Settings_Select_C
  * UMG_IngameMenu has RegisterSubmenu / SetCurrentSubMenu / subMenus, so a *proper* submenu may
    be a supported operation rather than a splice - worth trying after this works

Deliberately not reaching ContentHolder by name. A stub would have to declare properties matching
the real widget's children, which is guesswork that fails silently. GetParent asks the widget where
it lives instead, and cannot be wrong.

    UE4Editor-Cmd.exe <project>.uproject -run=Py <this file> -unattended -nopause -nosplash

The stub it creates must NEVER be shipped - see STUB.
"""

import unreal_engine as ue

from unreal_engine.classes import (
    UserWidget,
    Widget,
    PanelWidget,
    WidgetBlueprintFactory,
    WidgetBlueprintLibrary,
    KismetArrayLibrary,
    KismetMathLibrary,
    K2Node_Event,
    K2Node_IfThenElse,
)


WIDGET = '/Game/MCDReborn/UI/UMG_MCDRebornCamMenu'

#One of the game's own settings rows, stubbed so the editor has a class to name.
#
#This is the same technique the loader uses for the game modes, and it carries the same rule: build
#against it, never ship it. Installing an empty UMG_SettingsEntry over the real one would replace
#every row of the game's settings screen with nothing. The payload staging copies three folders and
#this is not in any of them, which is what keeps it honest.
STUB = '/Game/UI/Menu/Buttons/UMG_SettingsEntry'

TRACE = open(r'C:\Users\Gaming\AppData\Local\Temp\splice_trace.txt', 'w')


def say(what):
    TRACE.write(str(what) + '\n')
    TRACE.flush()
    try:
        ue.log('[MCDReborn] ' + str(what))
    except Exception:
        pass


def on_disk(path, kind):
    found = ue.find_asset(path)
    if found is not None:
        return found
    try:
        return ue.load_object(ue.find_class(kind), path)
    except Exception:
        return None


def pins_of(node):
    try:
        return [p.name for p in node.node_pins()]
    except Exception:
        return []


def pin(node, name):
    p = node.node_find_pin(name)
    if p is None:
        raise Exception('no pin "%s" on %s (has %s)'
                        % (name, node.node_get_title(), pins_of(node)))
    return p


def link(a_node, a_pin, b_node, b_pin):
    pin(a_node, a_pin).make_link_to(pin(b_node, b_pin))


def event_node(blueprint, name):
    for node in blueprint.UberGraphPages[0].Nodes:
        if node.is_a(K2Node_Event) and node.EventReference.MemberName == name:
            return node
    return None


def make_stub():
    there = on_disk(STUB, 'WidgetBlueprint')
    if there is not None:
        say('stub already there: ' + STUB)
        return there

    factory = WidgetBlueprintFactory()
    factory.ParentClass = UserWidget
    made = factory.factory_create_new(STUB)
    ue.compile_blueprint(made)
    try:
        made.save_package()
    except Exception as problem:
        say('could not save the stub: %s' % problem)
    say('stub built: ' + STUB)
    return made


def main():
    say('splicing the camera panel into the settings screen')

    make_stub()

    widget = on_disk(WIDGET, 'WidgetBlueprint')
    if widget is None:
        raise Exception('the camera panel is not there: ' + WIDGET)

    ue.blueprint_add_member_variable(widget, 'Attached', 'bool')
    ue.blueprint_mark_as_structurally_modified(widget)
    ue.compile_blueprint(widget)

    page = widget.UberGraphPages[0]

    tick = event_node(widget, 'Tick')
    if tick is None:
        raise Exception('the panel has no Tick to hang this on')

    row_class = ue.find_class('UMG_SettingsEntry_C')
    if row_class is None:
        raise Exception('the stub did not produce a UMG_SettingsEntry_C class')

    y = 5200

    #Once only. The settings screen is built and torn down every time it is opened, so this has to
    #run again after each one - but not on every tick of the same screen, or the panel is re-parented
    #to itself sixty times a second.
    done = page.graph_add_node_variable_get('Attached', None, 60, y + 160)
    gate = page.graph_add_node(K2Node_IfThenElse, 300, y)
    link(done, 'Attached', gate, 'Condition')

    found = page.graph_add_node_call_function(
        WidgetBlueprintLibrary.GetAllWidgetsOfClass, 560, y + 240)
    pin(found, 'WidgetClass').default_object = row_class
    pin(found, 'TopLevelOnly').default_value = 'false'

    def array_node(call, x, at, pin_name='TargetArray'):
        """
        An array node, told what it holds.

        Array functions are wildcards until something is connected, and the plugin makes the
        connection without telling the node - so the compile says "The type of Target Array is
        undetermined" however many times it is linked. node_reconstruct is the announcement that
        fixes this for Cast nodes, but it rebuilds the pins, which can drop the very link that was
        meant to inform it. So: link, reconstruct, link again, and say what actually stuck.
        """
        node = page.graph_add_node_call_function(call, x, at)

        #connect rather than make_link_to. Both attach the pins; the difference is that the pin
        #object exposes two methods and only one of them appears to go through the graph schema,
        #which is what tells a wildcard node what it is now holding. make_link_to leaves the link
        #in place and the node none the wiser - "The type of Target Array is undetermined" with a
        #connection sitting right there. node_reconstruct cannot rescue it either, because unlike
        #a Cast (which stores its target class) an array node has nothing to rebuild the type from.
        link(found, 'FoundWidgets', node, pin_name)

        #node_pin_type_changed is the plugin's name for NotifyPinConnectionListChanged, which is
        #exactly the call the editor makes when you drop a wire and the node works out what it now
        #holds. Tried in this order because each earlier attempt failed for a reason worth keeping:
        #make_link_to alone leaves the node uninformed; connect goes through the schema and still
        #does not inform it; node_reconstruct rebuilds pins from stored data, and an array node has
        #no stored type to rebuild from - which is why the same call rescued Cast nodes and cannot
        #rescue these.
        for attempt in ('node_pin_type_changed', 'node_reconstruct'):
            try:
                if attempt == 'node_pin_type_changed':
                    node.node_pin_type_changed(pin(node, pin_name))
                else:
                    node.node_reconstruct()
                say('  %s ok' % attempt)
            except Exception as problem:
                say('  %s refused: %s' % (attempt, problem))

        if not pin(node, pin_name).get_linked_to():
            link(found, 'FoundWidgets', node, pin_name)

        say('  %s.%s has %d connection(s)'
            % (call, pin_name, len(pin(node, pin_name).get_linked_to())))
        return node

    how_many = array_node(KismetArrayLibrary.Array_Length, 900, y + 380)

    any_of_them = page.graph_add_node_call_function(KismetMathLibrary.Greater_IntInt, 1150, y + 380)
    link(how_many, 'ReturnValue', any_of_them, 'A')
    pin(any_of_them, 'B').default_value = '0'

    #The screen is open only when one of its rows exists.
    open_now = page.graph_add_node(K2Node_IfThenElse, 1400, y + 240)
    link(any_of_them, 'ReturnValue', open_now, 'Condition')

    #Any row will do - they all live in the same box.
    first = array_node(KismetArrayLibrary.Array_Get, 1650, y + 380)
    pin(first, 'Index').default_value = '0'

    #Where that row lives, asked of the row itself rather than guessed from a name.
    holder = page.graph_add_node_call_function(Widget.GetParent, 1900, y + 380)
    link(first, 'Item', holder, 'self')

    moved = page.graph_add_node_call_function(PanelWidget.AddChild, 2200, y + 240)
    link(holder, 'ReturnValue', moved, 'self')
    link(page.graph_add_node_variable_get('Panel', None, 2050, y + 480), 'Panel', moved, 'Content')

    remember = page.graph_add_node_variable_set('Attached', None, 2550, y + 240)
    pin(remember, 'Attached').default_value = 'true'

    #--- into the tick chain, not branched off it ------------------------------------------------
    #
    #An exec *output* takes exactly one connection - forking Tick straight into this is the error
    #"Exec output pin cannot have more than one connection". An exec *input* takes many, so the
    #three ways out of this section can all rejoin whatever the tick already did.
    carry_on = pin(tick, 'then').get_linked_to()
    say('the tick already drives %d node(s); splicing in front of them' % len(carry_on))
    pin(tick, 'then').break_all_pin_links(True)
    link(tick, 'then', gate, 'execute')

    link(gate, 'else', found, 'execute')
    link(found, 'then', open_now, 'execute')
    link(open_now, 'then', moved, 'execute')
    link(moved, 'then', remember, 'execute')

    #Already attached, the screen is not open, or it has just been attached - all three carry on to
    #the camera work, so the sliders keep driving the camera either way.
    for onward in carry_on:
        pin(gate, 'then').make_link_to(onward)
        pin(open_now, 'else').make_link_to(onward)
        pin(remember, 'then').make_link_to(onward)

    ue.compile_blueprint(widget)
    try:
        widget.save_package()
    except Exception as problem:
        say('could not save: %s' % problem)

    say('done - the panel now joins the settings screen when it opens')


main()
