"""
The attacher: the one asset that is finished by hand, once, and then never touched again.

It exists because of a limit that is now well understood. Array function nodes take their type from
whatever is connected to them, and this plugin will make the connection without telling the node -
so the compile says "The type of Target Array is undetermined" with a wire sitting right there.
Four ways were tried and all four report success and change nothing: make_link_to, connect,
node_reconstruct and node_pin_type_changed. Unlike a Cast node, which stores its target class and
can therefore be rebuilt from its own state, an array node stores nothing. There is nothing to
rebuild from.

So the array work is quarantined here, in eight nodes that will never need regenerating, and the
camera panel - which is regenerated constantly - never touches an array at all. That is the same
division the community mods use, and it costs one editor session ever.

What it does:

    Tick -> already attached?
         -> no: GetAllWidgetsOfClass(UMG_SettingsEntry_C)   a real row of the settings screen
                -> Length > 0?                              the screen is open
                -> Get[0] -> GetParent()                    the VerticalBox the rows live in
                -> Create(camera panel) -> AddChild          our panel becomes a row in it
                -> remember

The panel is created *into* the settings screen rather than added to the viewport, so it is never
an overlay at any point. The escape menu already releases the cursor and already pauses the game,
which is the whole reason this is worth doing.

    UE4Editor-Cmd.exe <project>.uproject -run=Py <this file> -unattended -nopause -nosplash

Then, once, in the editor: open UMG_MCDRebornAttacher and drag Found Widgets into the two
Target Array pins. The script prints the exact pins when it finishes.
"""

import unreal_engine as ue

from unreal_engine.classes import (
    GameplayStatics,
    KismetArrayLibrary,
    KismetMathLibrary,
    PanelWidget,
    UserWidget,
    Widget,
    WidgetBlueprintFactory,
    WidgetBlueprintLibrary,
    K2Node_Event,
    K2Node_IfThenElse,
)


ATTACHER = '/Game/MCDReborn/UI/UMG_MCDRebornAttacher'
PANEL = '/Game/MCDReborn/UI/UMG_MCDRebornCamMenu'

#One of the game's own settings rows, stubbed so the editor has a class to name. Build against it,
#never ship it: an empty one installed over the real row would blank every line of the settings
#screen. The payload staging copies three folders and this is in none of them.
STUB = '/Game/UI/Menu/Buttons/UMG_SettingsEntry'

TRACE = open(r'C:\Users\Gaming\AppData\Local\Temp\attacher_trace.txt', 'w')


def say(what):
    TRACE.write(str(what) + '\n')
    TRACE.flush()
    try:
        ue.log('[MCDReborn] ' + str(what))
    except Exception:
        pass


def keep(asset):
    try:
        asset.save_package()
    except Exception as problem:
        say('could not save %s: %s' % (asset, problem))


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
    keep(made)
    say('stub built: ' + STUB)
    return made


def main():
    say('building the attacher')

    make_stub()

    if on_disk(PANEL, 'WidgetBlueprint') is None:
        raise Exception('the camera panel is not there yet: ' + PANEL)

    panel_class = ue.find_class('UMG_MCDRebornCamMenu_C')
    row_class = ue.find_class('UMG_SettingsEntry_C')
    if panel_class is None or row_class is None:
        raise Exception('panel=%s row=%s - one of the classes did not resolve'
                        % (panel_class, row_class))

    there = on_disk(ATTACHER, 'WidgetBlueprint')
    if there is not None:
        say('attacher already there, leaving it alone: ' + ATTACHER)
        say('(delete it first if you want it regenerated - the hand-drawn wires would be lost)')
        return

    factory = WidgetBlueprintFactory()
    factory.ParentClass = UserWidget
    widget = factory.factory_create_new(ATTACHER)

    #Nothing is drawn by this widget. It needs to be on screen only because a UserWidget does not
    #tick until it is, and ticking is the whole of its job.
    ue.blueprint_add_member_variable(widget, 'Attached', 'bool')
    ue.blueprint_mark_as_structurally_modified(widget)
    ue.compile_blueprint(widget)

    page = widget.UberGraphPages[0]

    tick = event_node(widget, 'Tick')
    if tick is None:
        tick = page.graph_add_node_event(UserWidget, 'Tick', 0, 0)

    #--- once only -------------------------------------------------------------------------------
    done = page.graph_add_node_variable_get('Attached', None, 60, 200)
    gate = page.graph_add_node(K2Node_IfThenElse, 300, 0)
    link(done, 'Attached', gate, 'Condition')
    link(tick, 'then', gate, 'execute')

    #--- is the settings screen open -------------------------------------------------------------
    found = page.graph_add_node_call_function(
        WidgetBlueprintLibrary.GetAllWidgetsOfClass, 560, 0)
    pin(found, 'WidgetClass').default_object = row_class
    pin(found, 'TopLevelOnly').default_value = 'false'
    link(gate, 'else', found, 'execute')

    how_many = page.graph_add_node_call_function(KismetArrayLibrary.Array_Length, 900, 320)
    #TargetArray is left for a human - see the note at the top.

    any_of_them = page.graph_add_node_call_function(KismetMathLibrary.Greater_IntInt, 1150, 320)
    link(how_many, 'ReturnValue', any_of_them, 'A')
    pin(any_of_them, 'B').default_value = '0'

    open_now = page.graph_add_node(K2Node_IfThenElse, 1400, 0)
    link(any_of_them, 'ReturnValue', open_now, 'Condition')
    link(found, 'then', open_now, 'execute')

    #--- where the rows live ---------------------------------------------------------------------
    first = page.graph_add_node_call_function(KismetArrayLibrary.Array_Get, 900, 520)
    pin(first, 'Index').default_value = '0'
    #TargetArray is left for a human as well.

    holder = page.graph_add_node_call_function(Widget.GetParent, 1150, 520)
    link(first, 'Item', holder, 'self')

    #--- the panel, made straight into it --------------------------------------------------------
    controller = page.graph_add_node_call_function(GameplayStatics.GetPlayerController, 1400, 700)
    made = page.graph_add_node_call_function(WidgetBlueprintLibrary.Create, 1700, 0)
    pin(made, 'WidgetType').default_object = panel_class
    link(controller, 'ReturnValue', made, 'OwningPlayer')
    link(open_now, 'then', made, 'execute')

    placed = page.graph_add_node_call_function(PanelWidget.AddChild, 2000, 0)
    link(holder, 'ReturnValue', placed, 'self')
    link(made, 'ReturnValue', placed, 'Content')
    link(made, 'then', placed, 'execute')

    remember = page.graph_add_node_variable_set('Attached', None, 2300, 0)
    pin(remember, 'Attached').default_value = 'true'
    link(placed, 'then', remember, 'execute')

    ue.compile_blueprint(widget)
    keep(widget)

    say('')
    say('built: ' + ATTACHER)
    say('')
    say('TWO WIRES LEFT, and they cannot be made from here. In the editor, open the attacher and')
    say('drag the "Found Widgets" output of Get All Widgets Of Class into:')
    say('')
    say('   1. Length . Target Array')
    say('   2. Get . Target Array')
    say('')
    say('then compile and save. Nothing else in this project needs the editor.')


main()
