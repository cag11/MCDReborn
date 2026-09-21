"""
The pieces every MCD Reborn in-game panel is made of.

Written as a module rather than copied into each script because there are going to be several:
a map table, and whatever props come after it. Everything in here was learned the expensive way
somewhere else in this folder, and each piece carries the reason it is shaped the way it is - a
helper whose rule is not written down gets "simplified" back into the bug it was avoiding.

Import it from a build script that the editor runs:

    import sys, os
    sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
    import mcd_ui

Nothing here ships. The fonts and meshes it points at are stubs standing in for the game's own,
and MCD Reborn refuses to install anything the game already has - which is the only thing stopping
an empty Roboto from being installed over every glyph in the game.
"""

import unreal_engine as ue

from unreal_engine.classes import (
    Border,
    Button,
    CanvasPanel,
    Image,
    SizeBox,
    TextBlock,
    VerticalBox,
    K2Node_Event,
)
from unreal_engine.structs import (
    AnchorData,
    Anchors,
    LinearColor,
    Margin,
    SlateColor,
    SlateFontInfo,
    Vector2D,
)


#--- the game's own look -------------------------------------------------------------------------
#
#These are the GAME's fonts, at the paths the game keeps them. Referring to them by path is the
#whole reason a panel of ours can look like it belongs rather than like Roboto in a box: an empty
#asset of the same name at the same path is enough for the editor to build against, and at run time
#the real font loads.
#
#The stubs must NEVER be shipped. Staging copies only the folders a payload needs and leaves Fonts
#behind - a stub font installed over the real one replaces every glyph in the game with nothing.
#A SlateFontInfo wants a UFont - a COMPOSITE - and `/Game/Fonts/MinecraftTen` is not one. It is
#a FontFace, the raw typeface, and pointing a widget at it silently draws in Slate's own default
#instead. Worse, stubbing it here duplicated Roboto, a UFont, onto a FontFace path, so the classes
#did not even match at run time.
#
#The composites are these, and each names its faces:
#
#   /Game/Fonts/Minecraft   typefaces Ten, Symbol, Noto, NotoBold
#   /Game/Fonts/NewSeven    typefaces Seven
#   /Game/Fonts/NewFive     typefaces Five, FiveBold
#
#So the pair matters: the font asset AND the typeface inside it. Naming the wrong typeface is the
#same silent fallback as naming no typeface at all.
TITLE_FONT, TITLE_FACE = '/Game/Fonts/Minecraft', 'Ten'
BODY_FONT, BODY_FACE = '/Game/Fonts/NewSeven', 'Seven'

TITLE_SIZE = 34
BUTTON_SIZE = 20
BODY_SIZE = 16

#--- the palette, read out of the game rather than guessed ----------------------------------------
#
#These were picked by eye once and every one of them was wrong, in the same way and for the same
#reason: **a UMG LinearColor is LINEAR, and a colour picked by eye is sRGB**. The old backing of
#(0.05, 0.05, 0.07) reads as "nearly black" and RENDERS as #3F3F4B, a medium slate grey. That one
#mistake was most of why the panel never looked like it belonged.
#
#The values below come from the game's own cooked widgets - UMG_IngameMenu,
#UMG_MissionSelectMapWidget, UMG_ModalCloseSurface, UMG_9SliceWindow - cross-checked against a
#pixel dump of the popup_frame texture the game actually draws. Two independent sources agree to
#one LSB: the texture's fill is #10141B and UMG_9SliceButtonFilled's tint struct is #0F141B.
#
#Each is written with the sRGB it renders as, because that is the number a human can check.

#Neutral, NOT warm. The game's body text is a flat grey; the warm cast here before was invented.
#The game does also use pure white on dark panels, but every caption it draws carries an outline,
#which is what stops white reading as a light source. See `label`.
INK = LinearColor(R=0.8854, G=0.8854, B=0.8854, A=1.0)          # #F2F2F2

#Three independent headings in the game cluster on this. The old (1.0, 0.82, 0.28) rendered
##FFEA90 - a pale washed-out yellow. Gold reads as gold largely because of the burnt-orange drop
#shadow the game puts under it, not because of the fill; see SHADOW.
GOLD = LinearColor(R=0.9131, G=0.6724, B=0.1590, A=1.0)         # #F5D66F

#Also neutral. The old one rendered #CECBC7, far too light to read as secondary text.
DIM = LinearColor(R=0.40, G=0.40, B=0.40, A=1.0)                # #AAAAAA

#The panel itself. Cool near-black - the game tints everything blue-black, never neutral and never
#pure black. The alpha is the texture's own: 230/255.
BACKING = LinearColor(R=0.0052, G=0.0070, B=0.0110, A=0.902)    # #10141B

#What a modal lays over the world behind it, taken from UMG_ModalCloseSurface - which is literally
#the game's widget for this job.
CURTAIN = LinearColor(R=0.0056, G=0.0091, B=0.0152, A=0.40)     # #111821

#A row. The game's menu rows are a dark 9-slice a good deal lighter than the panel they sit on,
#which is what separates them from the background without a border.
ROW = LinearColor(R=0.0319, G=0.0319, B=0.0319, A=1.0)          # #323232

#And what a row does when the pointer is over it. The game's pressed state is gold rather than a
#lighter grey, which is most of what makes its menus feel like its menus.
ROW_OVER = LinearColor(R=0.0685, G=0.0685, B=0.0685, A=1.0)     # #484848
ROW_DOWN = LinearColor(R=0.6354, G=0.3493, B=0.0563, A=1.0)     # #D1A043

#Under a heading. Burnt orange, not black - the game's gold titles are drawn over this and it is
#doing more work than the fill colour is.
SHADOW = LinearColor(R=0.8148, G=0.1559, B=0.0307, A=1.0)       # #E96E31

#Around a caption. #2A2A2A rather than pure black, which is what every label in the game uses.
OUTLINE = LinearColor(R=0.0232, G=0.0232, B=0.0232, A=1.0)      # #2A2A2A


def say(what):
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

    find_asset alone only sees what the asset registry has indexed, so a package written moments
    ago by a previous run is invisible to it - and the next create then fails with "a blueprint
    with this name already exists in the package", which is the registry and the disk disagreeing
    rather than anything being wrong.
    """
    found = ue.find_asset(path)
    if found is not None:
        return found
    try:
        return ue.load_object(ue.find_class(kind), path)
    except Exception:
        return None


#--- graphs --------------------------------------------------------------------------------------

def pins_of(node):
    try:
        return [p.name for p in node.node_pins()]
    except Exception:
        return []


def pin(node, name):
    """
    One pin, or an exception that says what the node actually has.

    node_find_pin RAISES on a name it does not know rather than returning nothing, and its own
    message is just `unable to find pin "X"` - which tells you the name you already typed and
    not the list you needed. Catching it and re-raising with the pins is the difference between
    one run and five.
    """
    try:
        found = node.node_find_pin(name)
    except Exception:
        found = None

    if found is None:
        raise Exception('no pin "%s" on %s (has %s)'
                        % (name, node.node_get_title(), pins_of(node)))
    return found


def link(from_node, from_pin, to_node, to_pin):
    """One connection, loud when a pin is not where it was expected."""
    pin(from_node, from_pin).make_link_to(pin(to_node, to_pin))


def set_default(node, name, value):
    """
    A pin's literal, reported rather than assumed.

    Reported because a literal that did not take is silent and looks like the graph not running. A
    vector written as "1.5,1.5,1.5" parses as (0,0,0), which once scaled a hero to nothing while
    leaving them perfectly controllable.
    """
    found = node.node_find_pin(name)
    if found is None:
        say('no pin "%s" on %s, leaving its default' % (name, node.node_get_title()))
        return False
    try:
        found.default_value = value
        say('  %s = "%s" (reads back "%s")' % (name, value, found.default_value))
        return True
    except Exception as problem:
        say('could not set %s.%s: %s' % (node.node_get_title(), name, problem))
        return False


def event_node(blueprint, name):
    for node in blueprint.UberGraphPages[0].Nodes:
        if node.is_a(K2Node_Event) and node.EventReference.MemberName == name:
            return node
    return None


def event(blueprint, owner, name, x=0, y=0):
    """The event node, whether the blueprint came with one or needs one placed."""
    found = event_node(blueprint, name)
    return found if found is not None else blueprint.UberGraphPages[0].graph_add_node_event(
        owner, name, x, y)


def reconstruct(node):
    """
    Tells a node that something was connected to it.

    The plugin CAN link a Cast node's Object pin - the belief that it cannot cost a day of hand
    wiring - but it cannot ANNOUNCE the link, so the node goes on believing nothing is attached and
    the compiler agrees. This is the announcement. Call it after linking into any node that changes
    its own pins based on what it is given.
    """
    try:
        node.node_reconstruct()
    except Exception as problem:
        say('could not reconstruct %s: %s' % (node.node_get_title(), problem))


#--- widgets ---------------------------------------------------------------------------------------

def stub_font(path):
    """An empty stand-in for one of the game's fonts, so the editor has something to point at."""
    there = on_disk(path, 'Font')
    if there is not None:
        return there
    made = ue.duplicate_asset('/Engine/EngineFonts/Roboto.Roboto', path, path.rsplit('/', 1)[1])
    keep(made)
    return made


def put(parent, child, left, top, wide, high):
    """
    Adds a child to a canvas at a position and size.

    The last two are a SIZE, not the far edge - that is what a canvas slot means by Offsets.Right
    and .Bottom while the anchors are a single point. Passing absolute edges is what once put a
    control off the side of its panel with its label pushed out of view, so they are named
    wide/high to make the mistake harder to repeat.

    AddChild rather than assigning Slots: the plugin's own example does the latter and it does not
    persist - the saved widget comes out with every part present, no Slots at all, and draws
    nothing while looking entirely correct.
    """
    slot = parent.AddChild(child)
    slot.LayoutData = AnchorData(
        Offsets=Margin(Left=left, Top=top, Right=wide, Bottom=high),
        Anchors=Anchors(Minimum=Vector2D(X=0, Y=0), Maximum=Vector2D(X=0, Y=0)))
    return slot


#The canvas everything is measured against. Nothing is DRAWN at this size - the numbers are
#turned into fractions of whatever the screen turns out to be - but laying a panel out in pixels
#reads better than laying it out in decimals.
DESIGN = (1920.0, 1080.0)


def place(parent, child, left, top, wide, high):
    """
    A child positioned by FRACTIONS of its parent, written in design pixels.

    The difference from put() is what happens on somebody else's screen. put() uses a point
    anchor, so its offsets are absolute pixels measured from the top left - and when the viewport
    is not the size the panel was drawn for, UMG scales the whole canvas and everything drifts.
    The first build of this panel had its title clipped off the left edge for exactly that
    reason, which is the same defect Blossoming Isles shipped with its third Play button parked
    at x=2112 on a 1920 canvas.

    A rect anchor has no such problem: the corners are fractions of the parent, so the layout is
    the same shape at any size.
    """
    from unreal_engine.structs import AnchorData, Anchors, Margin, Vector2D

    slot = parent.AddChild(child)
    slot.LayoutData = AnchorData(
        Offsets=Margin(Left=0.0, Top=0.0, Right=0.0, Bottom=0.0),
        Anchors=Anchors(
            Minimum=Vector2D(X=left / DESIGN[0], Y=top / DESIGN[1]),
            Maximum=Vector2D(X=(left + wide) / DESIGN[0], Y=(top + high) / DESIGN[1])))
    return slot


def stretch(parent, child):
    """A child filling its parent completely, however big the parent turns out to be."""
    slot = parent.AddChild(child)
    slot.LayoutData = AnchorData(
        Offsets=Margin(Left=0.0, Top=0.0, Right=0.0, Bottom=0.0),
        Anchors=Anchors(Minimum=Vector2D(X=0, Y=0), Maximum=Vector2D(X=1, Y=1)))
    return slot


def label(tree, name, caption, colour=INK, face=None, size=BODY_SIZE, variable=False,
          typeface='Default', outline=1, shadow=None):
    """
    One piece of text.

    `outline` defaults to ON, because in this game it always is. Every menu label the game draws
    sets one, and so does every world label over a prop - and the reason is the same in both
    places: the thing behind the text is not a flat colour. Over the Camp it is grass and stone
    and water; over a panel it is whatever shows through a backing that is not fully opaque.

    `shadow` is for headings. The game's gold titles carry a BURNT ORANGE drop shadow, and it is
    doing more work than the gold is - the same fill without it reads as pale yellow.
    """
    block = TextBlock(name, tree)
    block.bIsVariable = variable
    block.Text = caption
    block.ColorAndOpacity = SlateColor(SpecifiedColor=colour)

    if shadow is not None:
        say('  %s: shadow colour' % name)
        block.ShadowColorAndOpacity = shadow
        say('  %s: shadow offset' % name)
        block.ShadowOffset = Vector2D(X=2.0, Y=2.0)
        say('  %s: shadow done' % name)
    if face is not None:
        #TypefaceFontName is not optional. A SlateFontInfo with a FontObject and the wrong
        #typeface named falls back to Slate's own default, silently - and so does one pointing at
        #a FontFace rather than a UFont. Both of those were true at once for a while.
        #
        #Built in ONE call rather than assigned field by field: the plugin hands back structs by
        #VALUE, so `font.OutlineSettings = ...` writes to a copy that is then thrown away, and
        #the text draws without an outline while the script reports having set one.
        made = {'FontObject': face, 'Size': size, 'TypefaceFontName': typeface}
        say('  %s: font' % name)

        if outline:
            try:
                from unreal_engine.structs import FontOutlineSettings
                made['OutlineSettings'] = FontOutlineSettings(
                    OutlineSize=outline,
                    OutlineColor=OUTLINE)
            except Exception as problem:
                say('no outline on %s: %s' % (name, problem))

        block.Font = SlateFontInfo(**made)
        say('  %s: font set' % name)
    return block


def fill(tree, name, colour):
    """
    A plain block of colour.

    A Border with no texture set draws its brush tinted by BrushColor, which is a filled rectangle
    and the cheapest one available. Used for the panel's backing and for the curtain over the
    world behind it.
    """
    block = Border(name, tree)
    block.BrushColor = colour
    return block


def button(tree, name, caption, face=None, size=BUTTON_SIZE, colour=INK,
           typeface='Default', centred=False):
    """
    A button with a caption inside it.

    The caption is a child rather than a property, because a Button is a container - it has no text
    of its own, and a button built without a child is a correctly working rectangle with nothing
    written on it.

    Marked bIsVariable so the graph can bind OnClicked to it. A button that is not a variable has
    no name the event graph can reach and nothing can be attached to it.

    **BackgroundColor, not WidgetStyle.** A UButton tints its brush by that one FLinearColor, so a
    row can be made dark without building an FButtonStyle out of four FSlateBrushes - and a brush
    constructed here with no ResourceObject draws NOTHING, which looks like the button having
    vanished rather than like a style that did not take. Tinting the default rounded brush dark is
    both safer and closer to what the game does, which is a dark 9-slice.

    Captions are LEFT aligned by default. The game's menu rows read as a list; centred captions
    read as a row of dialog buttons, which is what made this panel look like a web form.
    """
    made = Button(name, tree)
    made.bIsVariable = True
    made.BackgroundColor = ROW

    slot = made.AddChild(label(tree, name + 'Text', caption, colour, face, size,
                               typeface=typeface))

    try:
        #EHorizontalAlignment: Fill 0, Left 1, Center 2, Right 3. Reported rather than assumed,
        #because an alignment that did not take is a caption in the middle and no error anywhere.
        slot.HorizontalAlignment = 2 if centred else 1
        slot.Padding = Margin(Left=0.0 if centred else 24.0, Top=4.0,
                              Right=0.0 if centred else 8.0, Bottom=4.0)
        say('  %s caption aligned %s' % (name, slot.HorizontalAlignment))
    except Exception as problem:
        say('could not align %s: %s' % (name, problem))

    return made
