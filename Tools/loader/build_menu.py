"""
The in-game settings panel: tick-boxes, spliced into the game's own Settings screen.

One setting at the moment - friendly fire - and the whole point of the file is that adding the next
one is a line in TOGGLES rather than a graph.

The camera sliders that used to be here are gone. They were duplicating the app's Camera tab, which
does all of it live over memory, with saved presets and text entry and no pak to install, and puts
everything back when the game quits. The in-game copy was the worse of the two.

Everything here is generated. That is worth stating because it was believed impossible: an earlier
actor had about eight wires dragged in by hand, on the conclusion that the plugin cannot connect a
Cast node's Object pin. It can. What it cannot do is *announce* the connection, so the node goes on
believing nothing is attached and the compiler agrees with it. node_reconstruct is the announcement.

The one thing still finished by hand is the attacher, and only because array nodes take their type
from a connection the plugin will not announce, and unlike a Cast they store nothing to rebuild it
from. It is quarantined in its own asset and never regenerated - see build_attacher.py.

What it makes:

    /Game/MCDReborn/UI/UMG_MCDRebornCamMenu     the panel
    /Game/MCDReborn/Actors/BP_MCDRebornCamMenu  spawns the attacher, which splices the panel in
    /Game/MCDReborn/Lobby/CamMenu               a level holding one of the actor

The names still say CamMenu. They are what the hand-wired attacher refers to by class, so renaming
them would mean regenerating it and losing the two wires that had to be drawn in the editor.

    UE4Editor-Cmd.exe <project>.uproject -run=Py <this file> -unattended -nopause -nosplash

Then cook, and install the level as a Lobby payload with MCD Reborn.
"""

import unreal_engine as ue

from unreal_engine.classes import (
    Actor,
    Border,
    CanvasPanel,
    CheckBox,
    GameplayStatics,
    KismetStringLibrary,
    KismetSystemLibrary,
    KismetTextLibrary,
    PlayerCharacter,
    SizeBox,
    TextBlock,
    UserWidget,
    WidgetBlueprintFactory,
    WidgetBlueprintLibrary,
    WorldFactory,
    K2Node_Event,
    K2Node_IfThenElse,
)
from unreal_engine.structs import (
    AnchorData,
    Anchors,
    LinearColor,
    Margin,
    SlateColor,
    SlateFontInfo,
    Vector2D,
    WidgetTransform,
)


WIDGET = '/Game/MCDReborn/UI/UMG_MCDRebornCamMenu'
ACTOR = '/Game/MCDReborn/Actors/BP_MCDRebornCamMenu'
LEVEL = '/Game/MCDReborn/Lobby/CamMenu'

#The hand-finished attacher, if this machine has done that step. Never regenerated.
ATTACHER = '/Game/MCDReborn/UI/UMG_MCDRebornAttacher'

#--- the settings --------------------------------------------------------------------------------
#
#Friendly fire is lifted from the PVP mod, whose entire implementation is one property write - no
#combat code at all. The game already has teams because enemies need them, and friendly fire was
#always implemented and simply gated behind everyone being ETeamName::Heroes. Move a player off
#Heroes and the existing damage rules do the rest.
#
#Copied exactly rather than improved on: it writes players 0 AND 2, both to the same value. Both
#being on team 2 still lets them hurt each other, which says the rule is "Heroes do not damage
#Heroes" rather than "different teams fight" - so the value matters less than not being Heroes.
#
#Written by NAME rather than through a cast. PlayerCharacter and BaseCharacter are native
#/Script/Dungeons classes and cannot be stubbed, so the cast that mod uses is not available without
#a C++ module. SetBytePropertyByName needs no class at all - the pawn as a plain object, the
#property as a string.
#
#The open question, and the reason this is worth testing before anybody installs Visual Studio: if
#TeamName is a UEnumProperty rather than a UByteProperty, SetBytePropertyByName will not match it
#and the tick-box will do nothing at all. That result is itself the answer about whether the
#property-by-name route is viable for everything else.
#
#    name, caption, property, value when ticked, value when not
TOGGLES = [
    ('Pvp', 'Friendly fire (PVP)', 'TeamName', '2', '0'),
]

#Which players each write is applied to. The PVP mod uses 0 and 2 - local co-op indices.
PLAYERS = (0, 2)

#--- layout --------------------------------------------------------------------------------------
#A margin on a widget anchored to the top left is left, top, then the far edge rather than the size.
PANEL = Margin(Left=0.0, Top=0.0, Right=460.0, Bottom=120.0)

PAD = 16.0
TITLE_H = 30.0
ROW_H = 36.0
ROW_GAP = 6.0
BOX_W = 26.0

#How much bigger than stock the tick-box is drawn.
BOX_SCALE = 2.2

#The game's own fonts, referred to by the paths the game keeps them at.
#
#The stub technique, and the whole reason the panel can look like it belongs: an empty asset of the
#same name at the same path is enough to build against, and at runtime the game's real font loads.
#
#These stubs must NEVER be shipped. A stub font installed over the real one would replace every
#glyph in the game with an empty Roboto - which is why staging copies only the three folders the
#payload needs and leaves Fonts behind.
TITLE_FONT = '/Game/Fonts/MinecraftTen'
BODY_FONT = '/Game/Fonts/MinecraftSeven'

#The title. It briefly also reported whether the write had run - "MCD Reborn - PVP written" - which
#existed to split one ambiguity: a tick-box that does nothing looks identical whether the graph
#never ran or the property write silently failed to match. It ran, so the question is answered and
#the diagnostic is gone. Leaving one in is how a scale call once shrank the hero to nothing.
TITLE = 'MCD Reborn'

#TEMPORARY. Shows the live TeamName on the title, and exists to answer one question that could not
#be asked before: what value the game actually uses for Heroes.
#
#Until the C++ module existed there was no way to read it. KismetSystemLibrary has Set*PropertyByName
#and no getter, and nothing outside the game can see the property without hunting its memory offset.
#With a real class to cast to, TeamName is BlueprintReadWrite and simply readable.
#
#This matters because the tick-box's *off* value is a guess. It writes 0, on the assumption that
#Heroes is 0. If it is not, unticking has been putting the player on some other wrong team all
#along, and it would look exactly like working.
#
#Answered: the title read "team 0" untouched and "team 2" ticked, so Heroes is 0 and the off value
#below is right. Off now - a diagnostic kept past its question is how a scale call once shrank the
#hero to nothing.
SHOW_TEAM = False

TITLE_SIZE = 22
BODY_SIZE = 15

INK = LinearColor(R=0.93, G=0.93, B=0.90, A=1.0)
GOLD = LinearColor(R=1.0, G=0.82, B=0.28, A=1.0)
BACKING = LinearColor(R=0.05, G=0.05, B=0.07, A=0.88)

TRACE = open(r'C:\Users\Gaming\AppData\Local\Temp\menu_trace.txt', 'w')


def say(what):
    TRACE.write(str(what) + '\n')
    TRACE.flush()
    try:
        ue.log('[MCDReborn] ' + str(what))
    except Exception:
        pass


def keep(asset):
    """Writes an asset to disk. editor_save_all saves nothing from a commandlet."""
    try:
        asset.save_package()
    except Exception as problem:
        say('could not save %s: %s' % (asset, problem))


def on_disk(path, kind):
    """
    An asset that is already there, whether or not this session has it loaded.

    find_asset alone only sees what the asset registry has indexed, so a package written moments ago
    by a previous run is invisible to it - and the next create then fails with "a blueprint with
    this name already exists in the package", which is the registry and the disk disagreeing rather
    than anything being wrong.
    """
    found = ue.find_asset(path)
    if found is not None:
        return found
    try:
        return ue.load_object(ue.find_class(kind), path)
    except Exception:
        return None


def stub_font(path):
    """An empty stand-in for one of the game's fonts, so the editor has something to point at."""
    there = on_disk(path, 'Font')
    if there is not None:
        return there
    made = ue.duplicate_asset('/Engine/EngineFonts/Roboto.Roboto', path, path.rsplit('/', 1)[1])
    keep(made)
    say('stub font: ' + path)
    return made


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


def set_default(node, pin_name, value):
    p = node.node_find_pin(pin_name)
    if p is None:
        say('  no pin "%s" on %s, leaving its default' % (pin_name, node.node_get_title()))
        return False
    p.default_value = value
    return True


def put(parent, child, left, top, wide, high):
    """
    Adds a child to a canvas at a position and size.

    The last two arguments are a SIZE, not the far edge. That is what a canvas slot means by
    Offsets.Right and .Bottom when the anchors are a single point, and passing absolute edges into
    them is what put the tick-box off the side of the panel with its label pushed out of view.
    Named wide/high so the mistake is harder to repeat.

    AddChild rather than assigning Slots. The plugin's own example does the latter and it does not
    persist: the saved widget came out with every part present and no Slots at all, and drew nothing
    while looking entirely correct.
    """
    slot = parent.AddChild(child)
    slot.LayoutData = AnchorData(
        Offsets=Margin(Left=left, Top=top, Right=wide, Bottom=high),
        Anchors=Anchors(Minimum=Vector2D(X=0, Y=0), Maximum=Vector2D(X=0, Y=0)))
    return slot


def label(tree, name, caption, colour, font=None, size=BODY_SIZE, variable=False):
    block = TextBlock(name, tree)
    block.bIsVariable = variable
    block.Text = caption
    block.ColorAndOpacity = SlateColor(SpecifiedColor=colour)
    if font is not None:
        block.Font = SlateFontInfo(FontObject=font, Size=size)
    return block


def build_tree(widget):
    """A caption and a tick-box per setting."""
    widget.modify()
    tree = widget.WidgetTree

    title_face = stub_font(TITLE_FONT)
    body_face = stub_font(BODY_FONT)

    width = PANEL.Right - PANEL.Left
    height = PAD * 2 + TITLE_H + len(TOGGLES) * (ROW_H + ROW_GAP)

    #A SizeBox around everything, and it is not decoration.
    #
    #This widget is not added to the viewport - the attacher puts it inside the game's settings
    #VerticalBox instead. A box asks its children how big they want to be, and a CanvasPanel's
    #answer is zero, because everything in it is positioned absolutely. Without an explicit size the
    #panel would be spliced in correctly and render as nothing at all, which looks exactly like the
    #splice having failed.
    #A SizeBox around everything, and it is not decoration.
    #
    #This widget is not added to the viewport - the attacher puts it inside the game's menu
    #VerticalBox instead. A box asks its children how big they want to be, and a CanvasPanel's
    #answer is zero, because everything in it is positioned absolutely. Without an explicit size the
    #panel is spliced in correctly and renders as nothing at all.
    #
    #A fixed width rather than letting it stretch. Stretching was tried and looked worse: the row
    #spanned the whole menu with the tick-box stranded at the far edge, a screen away from its
    #label.
    sizer = SizeBox('Sizer', tree)
    sizer.bOverride_WidthOverride = True
    sizer.WidthOverride = width
    sizer.bOverride_HeightOverride = True
    sizer.HeightOverride = height
    put(tree.RootWidget, sizer, 0.0, 0.0, width, height)

    panel = CanvasPanel('Panel', tree)
    panel.bIsVariable = True
    sizer.AddChild(panel)

    #A Border used as nothing but a filled rectangle: it draws its brush tinted by BrushColor, and
    #with no texture set that is a plain block of colour. First in, so it sits behind everything.
    backing = Border('Backing', tree)
    backing.BrushColor = BACKING
    put(panel, backing, 0.0, 0.0, width, height)

    put(panel, label(tree, 'Caption', TITLE, GOLD, title_face, TITLE_SIZE, variable=SHOW_TEAM),
        PAD, PAD, width - 2 * PAD, TITLE_H)

    top = PAD + TITLE_H + 6.0
    for name, caption, _, _, _ in TOGGLES:
        put(panel, label(tree, name + 'Label', caption, INK, body_face),
            PAD, top + 4.0, width - 2 * PAD - BOX_W - 20.0, ROW_H)

        box = CheckBox(name, tree)
        box.bIsVariable = True

        #A CheckBox draws at the size of its style's brushes, so a bigger slot does nothing at all -
        #it just sits small inside it. Scaling the rendered widget is the cheap way to make it a
        #reasonable target without rebuilding the whole SlateBrush style.
        #
        #Pivot at the top left, not the default centre: scaling about the centre moves the widget as
        #well as growing it, which walked it off the edge of the panel.
        box.RenderTransform = WidgetTransform(Scale=Vector2D(X=BOX_SCALE, Y=BOX_SCALE))
        box.RenderTransformPivot = Vector2D(X=0.0, Y=0.0)

        put(panel, box, width - PAD - BOX_W * BOX_SCALE, top + 4.0, BOX_W, BOX_W)

        top += ROW_H + ROW_GAP

    #Nothing sets Visibility anywhere here, on purpose. A UserWidget is already
    #SelfHitTestInvisible, which is exactly "do not eat clicks, but the children still take them".
    #Setting a container to HitTestInvisible instead makes every control inside it dead.

    widget.post_edit_change()
    ue.blueprint_mark_as_structurally_modified(widget)
    ue.compile_blueprint(widget)
    say('tree built: %d tick-box(es)' % len(TOGGLES))


def build_graph(widget):
    page = widget.UberGraphPages[0]

    tick = event_node(widget, 'Tick')
    if tick is None:
        tick = page.graph_add_node_event(UserWidget, 'Tick', 0, 0)

    #Polled with IsChecked rather than subscribed to. A CheckBox's OnCheckStateChanged compiles to a
    #ComponentDelegateBinding, which cannot be generated from outside the editor. Writing the
    #property every tick is wasteful and completely harmless - the same value to the same place -
    #and it means the setting survives anything the game does to the pawn, including spawning a new
    #one after a death or a level change.
    loose = [(tick, 'then')]

    for index, (name, _, prop, on_value, off_value) in enumerate(TOGGLES):
        y = index * 700

        ticked = page.graph_add_node_call_function(CheckBox.IsChecked, 300, y)
        link(page.graph_add_node_variable_get(name, None, 60, y), name, ticked, 'self')

        choose = page.graph_add_node(K2Node_IfThenElse, 560, y)
        link(ticked, 'ReturnValue', choose, 'Condition')
        for end in loose:
            pin(end[0], end[1]).make_link_to(pin(choose, 'execute'))

        ends = []
        for arm, value in (('then', on_value), ('else', off_value)):
            drop = 0 if arm == 'then' else 300
            step = (choose, arm)

            for slot, player in enumerate(PLAYERS):
                who = page.graph_add_node_call_function(
                    GameplayStatics.GetPlayerCharacter, 820 + slot * 420, y + drop + 110)
                set_default(who, 'PlayerIndex', str(player))

                write = page.graph_add_node_call_function(
                    KismetSystemLibrary.SetBytePropertyByName, 820 + slot * 420, y + drop)
                link(who, 'ReturnValue', write, 'Object')
                set_default(write, 'PropertyName', prop)
                set_default(write, 'Value', value)

                pin(step[0], step[1]).make_link_to(pin(write, 'execute'))
                step = (write, 'then')

            ends.append(step)

        #Both arms have to reach whatever comes next, or the tick simply stops on one of them.
        #An exec *input* accepts many connections, so both ends are kept and both get linked.
        loose = ends
        say('%s -> %s on players %s' % (name, prop, ', '.join(str(p) for p in PLAYERS)))

    if SHOW_TEAM:
        y = len(TOGGLES) * 700 + 400

        #The pawn, cast to the game's own class. This is the thing the C++ module bought: before it,
        #PlayerCharacter was a native /Script/Dungeons class with nothing to compile a cast against.
        who = page.graph_add_node_call_function(GameplayStatics.GetPlayerCharacter, 60, y + 140)
        set_default(who, 'PlayerIndex', '0')

        hero = page.graph_add_node_dynamic_cast(PlayerCharacter, 320, y)
        link(who, 'ReturnValue', hero, 'Object')
        for end in loose:
            pin(end[0], end[1]).make_link_to(pin(hero, 'execute'))

        #The announcement the plugin never makes by itself - without it the Object pin stays
        #undetermined however many times it is connected.
        hero.node_reconstruct()

        reading = page.graph_add_node_variable_get('TeamName', PlayerCharacter, 620, y + 160)
        pin(hero, 'AsPlayer Character').make_link_to(pin(reading, 'self'))

        as_string = page.graph_add_node_call_function(
            KismetStringLibrary.Conv_ByteToString, 880, y + 160)
        link(reading, 'TeamName', as_string, 'InByte')

        titled = page.graph_add_node_call_function(
            KismetStringLibrary.Concat_StrStr, 1140, y + 160)
        set_default(titled, 'A', TITLE + ' - team ')
        link(as_string, 'ReturnValue', titled, 'B')

        as_text = page.graph_add_node_call_function(
            KismetTextLibrary.Conv_StringToText, 1400, y + 160)
        link(titled, 'ReturnValue', as_text, 'InString')

        shown = page.graph_add_node_call_function(TextBlock.SetText, 1660, y)
        link(page.graph_add_node_variable_get('Caption', None, 1520, y + 240),
             'Caption', shown, 'self')
        link(as_text, 'ReturnValue', shown, 'InText')
        link(hero, 'then', shown, 'execute')

        say('TEMPORARY: the title will report the live TeamName')

    ue.compile_blueprint(widget)
    say('graph built')


def build_widget():
    there = on_disk(WIDGET, 'WidgetBlueprint')
    if there is not None:
        say('widget already there: ' + WIDGET)
        return there

    factory = WidgetBlueprintFactory()
    factory.ParentClass = UserWidget
    widget = factory.factory_create_new(WIDGET)

    build_tree(widget)
    build_graph(widget)

    keep(widget)
    say('widget built: ' + WIDGET)
    return widget


def build_actor(widget):
    """
    The thing in the level that puts the panel on screen. It does nothing else.

    The same shape as the Cosmetics mod's own widget adder, which is 219 bytes of bytecode: on begin
    play, make a widget and show it.
    """
    there = on_disk(ACTOR, 'Blueprint')
    if there is not None:
        say('actor already there: ' + ACTOR)
        return there

    actor = ue.create_blueprint(Actor, ACTOR)
    page = actor.UberGraphPages[0]

    begin = event_node(actor, 'ReceiveBeginPlay')
    if begin is None:
        begin = page.graph_add_node_event(Actor, 'ReceiveBeginPlay', 0, 0)

    #The attacher if it exists, the panel itself if it does not.
    #
    #Loaded before it is asked for: find_class only sees classes this session has loaded, and it
    #*raises* rather than returning nothing, so asking for a class whose asset is merely sitting on
    #disk kills the script outright.
    attacher = None
    if on_disk(ATTACHER, 'WidgetBlueprint') is not None:
        try:
            attacher = ue.find_class('UMG_MCDRebornAttacher_C')
        except Exception as problem:
            say('the attacher asset is there but its class would not resolve: %s' % problem)

    say('spawning %s' % ('the attacher' if attacher else 'the panel directly (no attacher found)'))

    made = page.graph_add_node_call_function(WidgetBlueprintLibrary.Create, 400, 0)
    pin(made, 'WidgetType').default_object = attacher or widget.GeneratedClass

    controller = page.graph_add_node_call_function(GameplayStatics.GetPlayerController, 150, 200)
    link(controller, 'ReturnValue', made, 'OwningPlayer')
    link(begin, 'then', made, 'execute')

    shown = page.graph_add_node_call_function(UserWidget.AddToViewport, 800, 0)
    link(made, 'ReturnValue', shown, 'self')

    #Above the game's own interface. The Cosmetics mod uses 112 and Blossoming Isles 9999, so there
    #is no magic number here - only "higher than whatever the game added its HUD with".
    set_default(shown, 'ZOrder', '9999')
    link(made, 'then', shown, 'execute')

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
    if on_disk(LEVEL, 'World') is not None:
        say('level already there: ' + LEVEL)
        return
    world = WorldFactory().factory_create_new(LEVEL)
    world.actor_spawn(actor.GeneratedClass)
    keep(world)
    say('level built: ' + LEVEL)


def main():
    say('building the settings panel')
    widget = build_widget()
    actor = build_actor(widget)
    build_level(actor)
    say('done - cook, then install %s as a Lobby payload' % LEVEL)


main()
