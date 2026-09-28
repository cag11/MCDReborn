"""
The Gems screen: K in the Camp or a mission. Every gem the character owns with its count, the three
equipped items with their sockets, and socketing done in place - as Diablo does it.

    pick an empty socket, then a gem      the gem goes in, one fewer is owned
    click a gem that is in a socket       it comes out, one more is owned
    + beside a count                      three of that gem become one of the next grade

Gems are currencies (MCD Reborn registers MCDR_Gem<Kind><Grade> as copies of Gold), so counts and
payments are the game's own WalletComponent.Balance, Deduct and ClientAdd. Sockets are property
lines on the item (FInventoryItemData.ArmorProperties): an empty one is MCDR_SocketEmpty (41) and
a filled one is MCDR_Socket<Kind><Grade> (42 on, the plugin's numbering, spelled out in the
project's Dungeons stub). Setting a gem is changing that line's id in place, on a copy of the
item's data that is then written back whole - the game's struct keeps every member the stub does
not declare. The plugin turns the gems on worn gear into effects.

Things this engine's Python plugin cannot do, and what is done instead:

    KismetArrayLibrary nodes are never told their type, so there is no Length, Get or Set:
    a ForEachLoop (a macro, which is told) finds the sockets, K2Node_GetArrayItem (which is
    told, and hands back a reference) reads and edits an element.

    A button's OnClicked cannot be bound from here, so buttons are polled with IsPressed and a
    shared debounce, as the maps menu does.

What it makes:

    /Game/MCDReborn/UI/Gems/T_*                  the gem, socket and selection pictures
    /Game/MCDReborn/UI/UMG_MCDRebornGems         the screen
    /Game/MCDReborn/Actors/BP_MCDRebornGems      puts it on screen
    /Game/MCDReborn/Lobby/Gems                   a level holding one - the Camp
    /Game/MCDReborn/Ingame/Gems                  and another - missions

    UE4Editor-Cmd.exe <project>.uproject -run=Py <this file> -unattended -nopause -nosplash

Delete the widget, actor and levels first to rebuild them: every step leaves an existing asset alone.
"""

import os
import sys

#The editor runs this without __file__, so the folder comes from MCDREBORN_LOADER, as for the others.
HERE = os.environ.get('MCDREBORN_LOADER', r'C:/Users/Gaming/RiderProjects/MCDSaveEditReborn/Tools/loader')
sys.path.insert(0, HERE)

import unreal_engine as ue
import mcd_ui
from mcd_ui import (say, keep, on_disk, pin, link, set_default, event, put, place, label, fill,
                    stub_font, INK, GOLD, DIM, BACKING, SHADOW, ROW, TITLE_FONT, TITLE_FACE,
                    BODY_FONT, BODY_FACE)

from unreal_engine.classes import (
    Actor,
    BlueprintMapLibrary,
    Button,
    CanvasPanel,
    CanvasPanelSlot,
    DungeonsImage,
    K2Node_MakeArray,
    K2Node_SpawnActorFromClass,
    WidgetComponent,
    StaticMeshComponent,
    PrimitiveComponent,
    WrapBox,
    K2Node_Self,
    EdGraph,
    HorizontalBox,
    KismetStringLibrary,
    MerchantItemSlotBase,
    Overlay,
    PanelWidget,
    GameplayStatics,
    Image,
    InventoryItem,
    InventoryItemSlot,
    ItemStashComponent,
    KismetMathLibrary,
    KismetSystemLibrary,
    KismetTextLibrary,
    PlayerCharacter,
    PlayerController,
    TextBlock,
    Texture2D,
    UserWidget,
    WalletComponent,
    Widget,
    WidgetBlueprintFactory,
    WidgetBlueprintLibrary,
    WorldFactory,
    K2Node_BreakStruct,
    K2Node_GetArrayItem,
    K2Node_IfThenElse,
    K2Node_MacroInstance,
    K2Node_MakeStruct,
    K2Node_SetFieldsInStruct,
)
from unreal_engine.structs import (SlateBrush, Vector2D, EdGraphPinType, OptionalPinFromProperty,
                                   AnchorData, Anchors, Margin, LinearColor, IntPoint, Vector,
                                   GraphReference)


WIDGET = '/Game/MCDReborn/UI/UMG_MCDRebornGems'
ACTOR = '/Game/MCDReborn/Actors/BP_MCDRebornGems'
LEVELS = ['/Game/MCDReborn/Lobby/Gems', '/Game/MCDReborn/Ingame/Gems']
ICONS = '/Game/MCDReborn/UI/Gems'

#The strip of sockets laid over every item tile, and the game's tile it is laid into. The tile is a
#game blueprint, so the project carries an empty stand-in at its path with the two members used:
#InventoryItemSlot (what the tile shows) and ItemInfoPadder (a canvas over the tile). The stand-in
#never ships - the carry list names MCDReborn's own files only.
STRIP = '/Game/MCDReborn/UI/UMG_MCDRebornSockets'
TILE_STUB = '/Game/UI/Inventory/UMG_InventoryGenericSlot'
SCAN_EVERY = 10                                 # frames between looks for new tiles

#The item description's lines: the game's UMG_ItemBulletPoint, a DungeonsImage 'Bullet' (the rarity
#diamond) beside a TextBlock 'Text'. A socket or gem line is known by how its text starts, and its
#diamond is swapped for the socket or gem picture. Stubbed like the tile.
BULLET_STUB = '/Game/UI/Inventory/Inspector2/UMG_ItemBulletPoint'
LINE_EVERY = 3                                  # frames between passes over the lines

#Socket pictures on a tile, Diablo's way: small in the corner always, large over the item on hover.
SMALL_RING, SMALL_GEM, SMALL_PAD = 16.0, 12.0, 1.0
BIG_RING, BIG_GEM, BIG_PAD = 38.0, 28.0, 3.0


#Finding gems and sockets, Diablo's way: in the world, not in the app.
#
#Gems: in a mission, each pickup of emeralds (a mob's drop, a chest) has FIND_CHANCE of also finding a
#gem - any kind, mostly Chipped. A toast says which.
FIND_CHANCE = 0.06
FLAWLESS_FROM, PERFECT_FROM = 0.80, 0.97       # a roll at or above: 80% Chipped, 17% Flawless, 3% Perfect
TOAST_FRAMES = 180
FOUND_WITH = 'Emerald'                          # the currency whose pickups are watched

#Sockets: an item rolls its sockets once, while it is still marked new (just picked up, not yet
#looked at). The chance of at least 1, 2 and 3 sockets, by the item's rarity. The roll is worked out
#from the item's own power, so asking again gives the same answer and nothing needs remembering.
SOCKET_ODDS = [(0.20, 0.05, 0.00),              # Common
               (0.30, 0.12, 0.03),              # Rare
               (0.35, 0.25, 0.10)]              # Unique
ROLL_EVERY = 30
MOST_LINES = 4                                  # an item with more lines of its own is left alone


#The inventory's own Sockets section: laid into the item inspector (the game's
#UMG_InventoryItemInspector, stubbed with WholeCanvas and InspectedItem), for whichever item is
#inspected. It sits in the one empty part of the inspector - left of EXPAND and SALVAGE, under the
#description's line - measured from the inspector's top right corner. Only a compact bar lives
#there; the gem picker opens under it, over the POWER / SPEED row, while a socket is being filled.
DOCK = '/Game/MCDReborn/UI/UMG_MCDRebornDock'
INSPECTOR_STUB = '/Game/UI/Inventory/Inspector2/UMG_InventoryItemInspector'
#A shop's tile for an item on sale, which holds the merchant's slot; stubbed like the others.
SALE_STUB = '/Game/UI/Merchant/slot/base/UMG_MerchantItemSlotBase'
#And the shop tiles themselves: the sale tile (a UMG_MerchantItemSlotBase) holds a generic slot widget
#with a canvas over the tile (ContentPadder) and its own hover flag. The shop's socket strip goes there.
SHOP_TILE_STUB = '/Game/UI/Merchant/slot/UMG_MerchantItemSlot'
GENERIC_STUB = '/Game/UI/Grid/UMG_GenericSlotWidget'
SHOP_STRIP = '/Game/MCDReborn/UI/UMG_MCDRebornShopSockets'

#The Gem Merchant: a market booth of the game's in the Camp, near the Mystery Merchant, with a sign
#over it. Clicked, the shop opens; the close button, Esc, or walking off closes it. The booth only
#draws - the Gems screen, which has the wallet, finds it and answers the click, as the map table's
#own actor does for its panel.
STALL_ACTOR = '/Game/MCDReborn/Actors/BP_MCDRebornGemMerchant'
STALL_LEVEL = '/Game/MCDReborn/Lobby/GemMerchant'
STALL_LOOKS = '/Game/Decor/Prefabs/MarketBooths/BP_Marketbooth_Basic_Blue'
STALL_SIGN = '/Game/MCDReborn/UI/UMG_MCDRebornSign_GemMerchant'
#Placed where the level is authored; MCD Reborn moves it on install (Gems.STALL). Authored turned, so
#the level carries a rotation that can be rewritten too.
STALL_WHERE = (15950.0, 9750.0, 11800.0)
STALL_FACING = 180.0
STALL_REACH = 300.0                             # how near the booth a click has to land, in cm
STALL_LEAVE = 1000.0                            # walking this far from it closes the shop, in cm
GEM_PRICES = [500, 1500, 4500]                  # emeralds: Chipped, Flawless, Perfect - 3x, as joining is
#Centred on screen, in design pixels.
SHOP_W, SHOP_H = 1195.0, 915.0
SHOP_LEFT, SHOP_TOP = (1920.0 - SHOP_W) / 2.0, (1080.0 - SHOP_H) / 2.0
SHOP_COLS = [40.0, 430.0, 820.0]
SHOP_ROW_H = 106.0
SHOP_ROWS_Y = 96.0
SHOP_ICON = 72.0
SHOP_BACKING = LinearColor(R=0.0086, G=0.0100, B=0.0137, A=1.0)   # the merchant screen's own dark, opaque
DOCK_X, DOCK_Y = -222.0, 357.0                 # measured off a 1080p screenshot of the inventory
DOCK_W, DOCK_H = 360.0, 260.0                   # the bar and the picker's room under it
BAR_H = 90.0
BAR_W = 204.0                                   # the title and three sockets
DOCK_SOCKET = 52.0
CLEAR = LinearColor(R=0.0, G=0.0, B=0.0, A=0.0)    # a button that shows only what is in it
PICKER_Y = BAR_H + 6.0

#What each gem does, in words, for tooltips: set in a weapon, and set in armour.
EFFECTS = {
    'Ruby': 'more melee damage',
    'Sapphire': 'shorter artifact cooldowns',
    'Topaz': 'more ranged damage',
    'Emerald': 'faster melee attacks',
    'Amethyst': 'more artifact damage',
    'Diamond': 'absorbs part of the damage taken',
    'Skull': 'life steal, for you and allies nearby',
}
ARMOUR_EFFECTS = {
    'Ruby': 'heals you and allies nearby',
    'Sapphire': 'more souls gathered',
    'Topaz': 'faster dodge roll',
    'Emerald': 'faster movement, for you and allies nearby',
    'Amethyst': 'more damage for allies nearby',
    'Diamond': 'a chance for attacks to miss you',
    'Skull': 'a chance to teleport away when hit',
}
MASTERY_PREFIX = 'Mastery'                     # Weapon mastery's lines, whose bullets are its own
EMPTY_TIP = 'Empty socket. Click it, then click a gem to set it here.'


def gem_tip(kind, grade):
    return '%s %s. In a weapon: %s. In armour: %s.' % (GRADES[grade - 1], kind, EFFECTS[kind], ARMOUR_EFFECTS[kind])


def socket_tips():
    """What a socket holding each gem says, by line id - 42: the weapon lines, then the armour ones."""
    return ([('%s %s: %s.' % (GRADES[grade - 1], kind, EFFECTS[kind])) + ' Click to take it out.' for kind, grade in all_gems()]
            + [('%s %s: %s.' % (GRADES[grade - 1], kind, ARMOUR_EFFECTS[kind])) + ' Click to take it out.' for kind, grade in all_gems()])


def line_prefixes():
    """How each line a picture stands for starts, in the Pictures order: the socket, then the gems."""
    return ['Empty Socket'] + ['%s %s' % (GRADES[grade - 1], kind) for kind, grade in all_gems()]
PICTURES = os.path.join(HERE, 'gems')

KEY = 'K'

KINDS = ['Ruby', 'Sapphire', 'Topaz', 'Emerald', 'Amethyst', 'Diamond', 'Skull']
GRADES = ['Chipped', 'Flawless', 'Perfect']
RARITIES = ['Common', 'Rare', 'Unique']          # a set gem's line shows its grade by rarity

#The equipped items, in the order the screen lists them: caption, EEquipmentSlot.
SLOTS = [('Melee', 'MeleeGear'), ('Armour', 'ArmorGear'), ('Ranged', 'RangedGear')]
SOCKETS = 3                                     # the most an item shows

#EArmorPropertyID, as the plugin numbers MCD Reborn's lines: see the stub's Inventory.h.
#An empty socket, then each gem set in a weapon, then each gem set in armour - the same gem, its
#armour effect. Which one a socket gets is the item's: armour or not.
SOCKET_ID = 41
FIRST_GEM = 42
GEM_COUNT = len(KINDS) * len(GRADES)
FIRST_ARMOUR = FIRST_GEM + GEM_COUNT            # 63
LAST_GEM = FIRST_ARMOUR + GEM_COUNT - 1         # 83
EMPTY = 'MCDR_SocketEmpty'

DEBOUNCE = 20


def gem_id(kind, grade):
    """The currency: what the wallet counts."""
    return 'MCDR_Gem%s%d' % (kind, grade)


def gem_line(kind, grade, armour=False):
    """The property line a set gem is, as the stub's enum spells it: its weapon line or its armour one."""
    return ('MCDR_Armour%s%d' if armour else 'MCDR_Socket%s%d') % (kind, grade)


def all_gems():
    return [(kind, grade) for kind in KINDS for grade in (1, 2, 3)]


#--- layout, in the panel's own pixels --------------------------------------------------------------
PANEL_W = 1010.0
PAD = 22.0
TITLE_H = 44.0
HEAD_Y = 74.0
ROWS_Y = 104.0

#Equipped, on the left.
SLOT_H = 148.0
SOCKET = 64.0
SOCKET_GAP = 76.0

#The gems, on the right.
GRID_X = 430.0
ROW_H = 64.0
NAME_W = 130.0
CELL_X = [GRID_X + 140.0, GRID_X + 290.0, GRID_X + 440.0]
GEM = 52.0
ICON = 44.0
UP = 26.0

HINT_Y = ROWS_Y + ROW_H * len(KINDS) + 8.0
PANEL_H = HINT_Y + 44.0

PANEL_LEFT = (1920.0 - PANEL_W) / 2.0
PANEL_TOP = (1080.0 - PANEL_H) / 2.0

#ESlateVisibility, as pin literals.
COLLAPSED, SHOWN, CLICKABLE = 'Collapsed', 'SelfHitTestInvisible', 'Visible'
LOOKS = 'HitTestInvisible'


#--- pictures ----------------------------------------------------------------------------------------

def picture_names():
    return ['T_Socket', 'T_Select'] + ['T_%s%d' % (kind, grade) for kind, grade in all_gems()]


def import_pictures():
    """The pictures, as textures of our own that ship with the screen. None where one fails."""
    found = {}
    for name in picture_names():
        path = '%s/%s' % (ICONS, name)
        there = on_disk(path, 'Texture2D')
        if there is None:
            try:
                made = ue.import_asset(os.path.join(PICTURES, name + '.png'), ICONS)
                there = made[0] if isinstance(made, list) else made
            except Exception as problem:
                say('could not import %s: %s' % (name, problem))
                there = None
            if there is not None:
                #Pixel art: nearest-neighbour, no mips, the UI's own group - or it blurs.
                #TF_Nearest, TMGS_NoMipmaps, TC_EditorIcon (UserInterface2D), never streamed.
                for field, value in (('Filter', 0), ('MipGenSettings', 13), ('CompressionSettings', 7),
                                     ('NeverStream', True)):
                    try:
                        setattr(there, field, value)
                    except Exception as problem:
                        say('  %s.%s not set: %s' % (name, field, problem))
                keep(there)
        found[name] = there
    say('pictures: %d of %d' % (sum(1 for t in found.values() if t is not None), len(found)))
    return found


def object_path(texture_name):
    return '%s/%s.%s' % (ICONS, texture_name, texture_name)


#--- the tree ----------------------------------------------------------------------------------------

def image(tree, name, texture, size, variable=False):
    made = Image(name, tree)
    made.bIsVariable = variable
    if texture is not None:
        made.Brush = SlateBrush(ResourceObject=texture, ImageSize=Vector2D(X=size, Y=size))
    return made


def picture_button(tree, name, picture):
    """A dark button with a picture in it, polled by name."""
    made = Button(name, tree)
    made.bIsVariable = True
    made.BackgroundColor = ROW
    slot = made.AddChild(picture)
    try:
        slot.HorizontalAlignment = 2
        slot.VerticalAlignment = 2
    except Exception as problem:
        say('could not centre %s: %s' % (name, problem))
    return made


def build_tree(widget, pictures):
    widget.modify()
    tree = widget.WidgetTree
    title_face = stub_font(TITLE_FONT)
    body_face = stub_font(BODY_FONT)

    #Nothing here catches a click until K opens the panel, and then only the panel does.
    tree.RootWidget.Visibility = 4                  # SelfHitTestInvisible

    panel = CanvasPanel('Panel', tree)
    panel.bIsVariable = True
    panel.Visibility = 1                            # Collapsed until K
    place(tree.RootWidget, panel, PANEL_LEFT, PANEL_TOP, PANEL_W, PANEL_H)

    #The backing is Visible, so a click on the panel between buttons does not walk the hero there.
    put(panel, fill(tree, 'Backing', BACKING), 0.0, 0.0, PANEL_W, PANEL_H)
    put(panel, label(tree, 'Title', 'GEMS', GOLD, title_face, 34, typeface=TITLE_FACE, shadow=SHADOW),
        PAD, 12.0, PANEL_W - 2 * PAD, TITLE_H)

    #--- equipped -----------------------------------------------------------------------------
    put(panel, label(tree, 'HeadEquipped', 'Equipped', DIM, body_face, 16, typeface=BODY_FACE),
        PAD, HEAD_Y, 200.0, 24.0)
    for i, (caption, _) in enumerate(SLOTS):
        top = ROWS_Y + i * SLOT_H
        put(panel, label(tree, 'ItemName%d' % i, caption, INK, body_face, 18, variable=True,
                         typeface=BODY_FACE),
            PAD, top, GRID_X - 2 * PAD, 28.0)
        put(panel, label(tree, 'NoSockets%d' % i, 'No sockets', DIM, body_face, 16, variable=True,
                         typeface=BODY_FACE),
            PAD, top + 54.0, 300.0, 26.0)
        for j in range(SOCKETS):
            left = PAD + j * SOCKET_GAP
            inside = image(tree, 'SockPic%d%d' % (i, j), pictures.get('T_Socket'), 48.0, variable=True)
            socket = picture_button(tree, 'Sock%d%d' % (i, j), inside)
            socket.Visibility = 1
            put(panel, socket, left, top + 34.0, SOCKET, SOCKET)
            frame = image(tree, 'Frame%d%d' % (i, j), pictures.get('T_Select'), SOCKET, variable=True)
            frame.Visibility = 1
            put(panel, frame, left, top + 34.0, SOCKET, SOCKET)

    #--- gems ---------------------------------------------------------------------------------
    for column, grade in enumerate(GRADES):
        put(panel, label(tree, 'Head' + grade, grade, DIM, body_face, 16, typeface=BODY_FACE),
            CELL_X[column], HEAD_Y, 130.0, 24.0)

    for row, kind in enumerate(KINDS):
        top = ROWS_Y + row * ROW_H
        put(panel, label(tree, 'Name' + kind, kind, INK, body_face, 22, typeface=BODY_FACE),
            GRID_X, top + 14.0, NAME_W, 34.0)
        for column in range(3):
            grade = column + 1
            left = CELL_X[column]
            icon = image(tree, 'Icon%s%d' % (kind, grade), pictures.get('T_%s%d' % (kind, grade)), ICON)
            gem_button = picture_button(tree, 'Gem%s%d' % (kind, grade), icon)
            gem_button.ToolTipText = gem_tip(kind, grade) + ' Pick an empty socket first, then this.'
            put(panel, gem_button, left, top + 6.0, GEM, GEM)
            count = label(tree, 'Count%s%d' % (kind, grade), '0', INK, body_face, 22,
                          variable=True, typeface=BODY_FACE)
            put(panel, count, left + GEM + 6.0, top + 16.0, 50.0, 34.0)
            if grade < 3:
                up = mcd_ui.button(tree, 'Up%s%d' % (kind, grade), '+', body_face, 18,
                                   typeface=BODY_FACE, centred=True)
                up.ToolTipText = 'Join three %s %s into one %s %s.' % (GRADES[grade - 1], kind, GRADES[grade], kind)
                put(panel, up, left + GEM + 58.0, top + 19.0, UP, UP)

    #The Gem Merchant's stock, over the Mystery Merchant's own.
    shop = CanvasPanel('ShopPanel', tree)
    shop.bIsVariable = True
    shop.Visibility = 1
    place(tree.RootWidget, shop, SHOP_LEFT, SHOP_TOP, SHOP_W, SHOP_H)
    put(shop, fill(tree, 'ShopBacking', SHOP_BACKING), 0.0, 0.0, SHOP_W, SHOP_H)
    put(shop, label(tree, 'ShopTitle', 'GEM MERCHANT', GOLD, title_face, 30, typeface=TITLE_FACE, shadow=SHADOW),
        40.0, 24.0, 600.0, 44.0)
    close = mcd_ui.button(tree, 'ShopClose', 'CLOSE', body_face, 18, typeface=BODY_FACE, centred=True)
    close.ToolTipText = 'Close the shop (Esc).'
    put(shop, close, SHOP_W - 150.0, 26.0, 110.0, 40.0)
    for row, kind in enumerate(KINDS):
        for column in range(3):
            grade = column + 1
            left, top = SHOP_COLS[column], SHOP_ROWS_Y + row * SHOP_ROW_H
            icon = image(tree, 'ShopIcon%s%d' % (kind, grade), pictures.get('T_%s%d' % (kind, grade)), 56.0)
            buy = picture_button(tree, 'Buy%s%d' % (kind, grade), icon)
            buy.ToolTipText = gem_tip(kind, grade) + ' Click to buy one.'
            put(shop, buy, left, top, SHOP_ICON, SHOP_ICON)
            put(shop, label(tree, 'ShopName%s%d' % (kind, grade), '%s %s' % (GRADES[grade - 1], kind), INK,
                            body_face, 19, typeface=BODY_FACE), left + SHOP_ICON + 12.0, top + 2.0, 290.0, 26.0)
            put(shop, label(tree, 'ShopPrice%s%d' % (kind, grade), '{:,} emeralds'.format(GEM_PRICES[grade - 1]), GOLD,
                            body_face, 16, typeface=BODY_FACE), left + SHOP_ICON + 12.0, top + 28.0, 290.0, 22.0)
            put(shop, label(tree, 'ShopOwn%s%d' % (kind, grade), 'You have 0', DIM, body_face, 14, variable=True,
                            typeface=BODY_FACE), left + SHOP_ICON + 12.0, top + 50.0, 290.0, 20.0)
    put(shop, label(tree, 'ShopHint', 'Click a gem to buy one. Three of a grade join into one of the next on the Gems screen (K).',
                    DIM, body_face, 15, typeface=BODY_FACE), 40.0, SHOP_H - 44.0, SHOP_W - 80.0, 30.0)

    #A gem found in a mission: its picture and name, at the top of the screen for a few seconds.
    toast = CanvasPanel('Toast', tree)
    toast.bIsVariable = True
    toast.Visibility = 1
    place(tree.RootWidget, toast, 960.0 - 220.0, 150.0, 440.0, 64.0)
    put(toast, fill(tree, 'ToastBacking', BACKING), 0.0, 0.0, 440.0, 64.0)
    put(toast, image(tree, 'ToastPic', pictures.get('T_Ruby1'), 48.0, variable=True), 12.0, 8.0, 48.0, 48.0)
    put(toast, label(tree, 'ToastText', 'Found', GOLD, body_face, 22, variable=True, typeface=BODY_FACE),
        72.0, 14.0, 360.0, 36.0)

    put(panel, label(tree, 'Hint', 'Pick an empty socket, then a gem.  Click a set gem to take it out.  '
                     '+ joins three into one.', DIM, body_face, 15, typeface=BODY_FACE),
        PAD, HINT_Y, PANEL_W - 2 * PAD, 30.0)


#--- variables ---------------------------------------------------------------------------------------

def typed_variable(widget, name, default='', **pin_type):
    ue.blueprint_add_member_variable(widget, name, EdGraphPinType(**pin_type), False, default)


def pictures_variable(widget):
    """What a line's id shows: index id-41, the empty socket first, then the gems in id order."""
    shown = ['T_Socket'] + ['T_%s%d' % (kind, grade) for kind, grade in all_gems()]
    typed_variable(widget, 'Pictures', '(%s)' % ','.join(object_path(n) for n in shown),
                   PinCategory='object', PinSubCategoryObject=Texture2D, ContainerType=1)


def tips_variable(widget):
    typed_variable(widget, 'Tips', '(%s)' % ','.join('"%s"' % t for t in socket_tips()),
                   PinCategory='string', ContainerType=1)


def variables(widget):
    """
    What the screen remembers between frames. Compiled straight after adding them: a getter made
    before the skeleton class has the variable resolves to nothing, silently.
    """
    ints = {'Busy': '0', 'Sel': '-1', 'SelIndex': '0', 'WIndex': '0', 'Pending': '0'}
    for i in range(len(SLOTS)):
        ints['N%d' % i] = '0'
        ints['Base%d' % i] = '-1'
    for name, default in ints.items():
        ue.blueprint_add_member_variable(widget, name, 'int', False, default)

    item = dict(PinCategory='object', PinSubCategoryObject=InventoryItem)
    for name in ['Item%d' % i for i in range(len(SLOTS))] + ['SelItem', 'WItem']:
        typed_variable(widget, name, **item)

    typed_variable(widget, 'WId', EMPTY, PinCategory='byte', PinSubCategoryObject=ue.find_enum('EArmorPropertyID'))
    typed_variable(widget, 'WRarity', 'Common', PinCategory='byte', PinSubCategoryObject=ue.find_enum('EItemRarity'))
    typed_variable(widget, 'Work', PinCategory='struct', PinSubCategoryObject=ue.find_struct('InventoryItemData'))
    typed_variable(widget, 'WorkProps', PinCategory='struct', PinSubCategoryObject=ue.find_struct('ArmorPropertyData'),
                   ContainerType=1)

    ue.blueprint_add_member_variable(widget, 'Scan', 'int', False, '0')
    ue.blueprint_add_member_variable(widget, 'Seed', 'float', False, '0')
    ue.blueprint_add_member_variable(widget, 'ShopScan', 'int', False, '0')
    ue.blueprint_add_member_variable(widget, 'StallHere', 'bool', False, 'false')
    typed_variable(widget, 'StallAt', PinCategory='struct', PinSubCategoryObject=ue.find_struct('Vector'))
    ue.blueprint_add_member_variable(widget, 'ShopOpen', 'bool', False, 'false')
    ue.blueprint_add_member_variable(widget, 'ShopBusy', 'int', False, '0')
    ue.blueprint_add_member_variable(widget, 'Scan2', 'int', False, '0')
    ue.blueprint_add_member_variable(widget, 'Line', 'string', False, '')
    ue.blueprint_add_member_variable(widget, 'Matched', 'bool', False, 'false')
    ue.blueprint_add_member_variable(widget, 'Has', 'bool', False, 'false')
    tips_variable(widget)
    ue.blueprint_add_member_variable(widget, 'InMission', 'bool', False, 'false')
    for name, default in (('Roll', '0'), ('Seen', '-1'), ('Found', '0'), ('ToastTime', '0'),
                          ('Lines', '0'), ('Sockets', '0'), ('Want', '0')):
        ue.blueprint_add_member_variable(widget, name, 'int', False, default)
    typed_variable(widget, 'RItem', PinCategory='object', PinSubCategoryObject=InventoryItem)
    typed_variable(widget, 'Titles', '(%s)' % ','.join('"%s"' % t for t in line_prefixes()[1:]),
                   PinCategory='string', ContainerType=1)
    pictures_variable(widget)
    #Which currency a set gem is: index id-42.
    typed_variable(widget, 'GemNames', '(%s)' % ','.join(gem_id(k, g) for k, g in all_gems()),
                   PinCategory='name', ContainerType=1)

    ue.blueprint_mark_as_structurally_modified(widget)
    ue.compile_blueprint(widget)
    say('variables added')


#--- graph helpers -----------------------------------------------------------------------------------

class Graph(object):
    """
    The event graph, with the small vocabulary this screen is written in.

    Values are (node, pin) pairs; a plain number or string where a value is expected becomes the
    pin's literal. Exec runs through `me.loose` - the outputs the next node is linked from - so
    a straight line is `then(node)`, and a branch hands its two ends to the caller to continue.
    """

    def __init__(me, widget):
        me.widget = widget
        me.page = widget.UberGraphPages[0]
        me.x = 0
        me.y = 0
        me.loose = []

    #--- placement: nodes go down a column, a new column per section, so the graph is readable.
    def at(me, dx=0, dy=0):
        me.y += 60
        return me.x + dx, me.y + dy

    def section(me):
        me.x += 3000
        me.y = 0

    #--- values
    def feed(me, node, name, value):
        if isinstance(value, tuple):
            link(value[0], value[1], node, name)
        elif value is not None:
            set_default(node, name, str(value))

    def get(me, name, owner=None, of=None):
        x, y = me.at(-400)
        node = me.page.graph_add_node_variable_get(name, owner, x, y)
        if of is not None:
            me.feed(node, 'self', of)
        return (node, name)

    def call(me, function, out='ReturnValue', **inputs):
        x, y = me.at(-200)
        node = me.page.graph_add_node_call_function(function, x, y)
        for name, value in inputs.items():
            me.feed(node, name, value)
        return (node, out)

    def math(me, name, a, b):
        return me.call(getattr(KismetMathLibrary, name), A=a, B=b)

    def both(me, *conditions):
        value = conditions[0]
        for other in conditions[1:]:
            value = me.math('BooleanAND', value, other)
        return value

    def valid(me, value):
        return me.call(KismetSystemLibrary.IsValid, Object=value)

    def breaks(me, struct, value):
        x, y = me.at(-300)
        node = me.page.graph_add_node(K2Node_BreakStruct, x, y)
        node.StructType = ue.find_struct(struct)
        node.node_reconstruct()
        me.feed(node, struct, value)
        return node

    def makes(me, struct, **members):
        x, y = me.at(-300)
        node = me.page.graph_add_node(K2Node_MakeStruct, x, y)
        node.StructType = ue.find_struct(struct)
        node.node_reconstruct()
        for name, value in members.items():
            me.feed(node, name, value)
        return (node, struct)

    def element(me, array, index):
        """K2Node_GetArrayItem: typed by its array, and a reference to the element."""
        x, y = me.at(-300)
        node = me.page.graph_add_node(K2Node_GetArrayItem, x, y)
        #A reference, not a copy: the socket write edits the element it is handed.
        node.bReturnByRefDesired = True
        node.node_reconstruct()
        link(array[0], array[1], node, 'Array')
        told(node, 'Array')
        if not getattr(me, 'reported', False):
            me.reported = True
            say('element node: "%s", properties %s' % (node.node_get_title(), node.properties()))
        me.feed(node, 'Dimension 1', index)
        return (node, 'Output')

    def props(me, item):
        """An item's property lines, as a copy: item.Item.ArmorProperties."""
        data = me.get('Item', InventoryItem, of=item)
        return (me.breaks('InventoryItemData', data), 'ArmorProperties')

    def id_at(me, lines, index):
        """The EArmorPropertyID of one line, as an int."""
        entry = me.breaks('ArmorPropertyData', me.element(lines, index))
        return me.call(KismetMathLibrary.Conv_ByteToInt, InByte=(entry, 'ID'))

    def is_socket(me, id_value):
        return me.both(me.math('GreaterEqual_IntInt', id_value, SOCKET_ID),
                         me.math('LessEqual_IntInt', id_value, LAST_GEM))

    def armour_line(me, id_value):
        """1 for an armour gem line, 0 otherwise."""
        return me.call(KismetMathLibrary.Conv_BoolToInt, InBool=me.math('GreaterEqual_IntInt', id_value, FIRST_ARMOUR))

    def picture_of(me, id_value):
        """Which picture a line shows: 0 the empty socket, 1..21 the gems - a gem's weapon and armour lines alike."""
        return me.call(KismetMathLibrary.Clamp,
                       Value=me.math('Subtract_IntInt', me.math('Subtract_IntInt', id_value, SOCKET_ID),
                                     me.math('Multiply_IntInt', me.armour_line(id_value), GEM_COUNT)),
                       Min=0, Max=GEM_COUNT)

    def gem_of(me, id_value):
        """Which gem a gem line holds, 0..20 in the GemNames order."""
        return me.call(KismetMathLibrary.Clamp,
                       Value=me.math('Subtract_IntInt', me.math('Subtract_IntInt', id_value, FIRST_GEM),
                                     me.math('Multiply_IntInt', me.armour_line(id_value), GEM_COUNT)),
                       Min=0, Max=GEM_COUNT - 1)

    def currency(me, name_value):
        """A FSerializableItemId for a by-reference Type pin, which will not take a literal."""
        return me.makes('SerializableItemId', SerializedId=name_value)

    def wallet(me):
        return me.get('WalletComponent', PlayerCharacter, of=me.hero)

    def for_each(me, array):
        """
        A ForEachLoop over an array. connect, not link: it goes through the graph schema, which is
        what tells a macro its type - linked and then told, it compiles as "undetermined".
        """
        x, y = me.at()
        loop = me.page.graph_add_node(K2Node_MacroInstance, x, y)
        loop.MacroGraphReference = GraphReference(MacroGraph=ue.load_object(
            EdGraph, '/Engine/EditorBlueprintResources/StandardMacros.StandardMacros:ForEachLoop'))
        loop.node_allocate_default_pins()
        pin(array[0], array[1]).connect(pin(loop, 'Array'))
        return loop

    def tip(me, widget_value, text):
        """A widget's hover tooltip: a literal, or a string worked out in the graph."""
        #A const reference: even a literal has to be wired in, so it goes in as a string.
        return me.run(Widget.SetToolTipText, self=widget_value,
                      InToolTipText=me.call(KismetTextLibrary.Conv_StringToText, InString=text))

    def socket_tip(me, widget_value, line_id):
        """What a socket says on hover: empty, or which gem it holds and what that does."""
        me.either(me.math('EqualEqual_IntInt', line_id, SOCKET_ID),
                  lambda: me.tip(widget_value, EMPTY_TIP),
                  lambda: me.tip(widget_value, me.element(me.get('Tips'), me.call(
                      KismetMathLibrary.Clamp, Value=me.math('Subtract_IntInt', line_id, FIRST_GEM),
                      Min=0, Max=LAST_GEM - FIRST_GEM))))

    def make_array(me, values):
        """A Make Array of these values; connected through the schema, which types it."""
        x, y = me.at()
        node = me.page.graph_add_node(K2Node_MakeArray, x, y)
        node.NumInputs = len(values)
        node.node_reconstruct()
        for index, value in enumerate(values):
            pin(value[0], value[1]).connect(pin(node, '[%d]' % index))
        return (node, 'Array')

    def cast(me, to, value, carry_on='then'):
        """A Cast on the line; `carry_on` says which way the line goes on ('then' or 'CastFailed')."""
        x, y = me.at()
        node = me.page.graph_add_node_dynamic_cast(to, x, y)
        me.feed(node, 'Object', value)
        mcd_ui.reconstruct(node)
        me.then(node, 'execute', carry_on)
        return (node, mcd_ui.as_pin(node))

    #--- exec
    def then(me, node, into='execute', out='then'):
        for source, name in me.loose:
            link(source, name, node, into)
        me.loose = [(node, out)]
        return node

    def branch(me, condition):
        """A Branch on the line; returns (then, else) ends and leaves nothing loose."""
        x, y = me.at()
        gate = me.page.graph_add_node(K2Node_IfThenElse, x, y)
        me.feed(gate, 'Condition', condition)
        me.then(gate)
        me.loose = []
        return [(gate, 'then')], [(gate, 'else')]

    def setter(me, name, value, owner=None, of=None):
        x, y = me.at(200)
        node = me.page.graph_add_node_variable_set(name, owner, x, y)
        if of is not None:
            me.feed(node, 'self', of)
        me.feed(node, name, value)
        return me.then(node)

    def run(me, function, **inputs):
        x, y = me.at(200)
        node = me.page.graph_add_node_call_function(function, x, y)
        for name, value in inputs.items():
            me.feed(node, name, value)
        return me.then(node)

    def shows(me, widget_name, visibility):
        return me.run(Widget.SetVisibility, self=me.get(widget_name), InVisibility=visibility)

    def says(me, widget_name, text):
        """SetText with a literal FText, which lives in the pin's text value, not its default."""
        node = me.run(TextBlock.SetText, self=me.get(widget_name))
        found = node.node_find_pin('InText')
        found.default_value = text
        found.default_text_value = text
        return node

    def either(me, condition, when, otherwise):
        """if condition: when() else: otherwise(), both ends joined after."""
        yes, no = me.branch(condition)
        me.loose = yes
        when()
        ends = list(me.loose)
        me.loose = no
        otherwise()
        me.loose = ends + list(me.loose)


def for_range(g, first, last):
    """A ForLoop from `first` to `last` inclusive; its 'Index' is the counter."""
    x, y = g.at()
    loop = g.page.graph_add_node(K2Node_MacroInstance, x, y)
    loop.MacroGraphReference = GraphReference(MacroGraph=ue.load_object(
        EdGraph, '/Engine/EditorBlueprintResources/StandardMacros.StandardMacros:ForLoop'))
    loop.node_allocate_default_pins()
    g.feed(loop, 'FirstIndex', first)
    g.feed(loop, 'LastIndex', last)
    return loop


def looping(g, loop, body):
    """Runs `body(index)` for each turn of `loop`, and carries on after it."""
    g.then(loop, 'execute', 'Completed')
    after = list(g.loose)
    g.loose = [(loop, 'LoopBody')]
    body((loop, 'Index'))
    g.loose = after


def unless_holding(g, panel, cls):
    """
    Carries on only when none of `panel`'s children is a `cls` (the widget's bool 'Has' is the
    answer). Every child is looked at, not just the last: Gems and Weapon mastery both add to an
    item tile, and "the last child is ours" was true for neither once the other had added.
    """
    g.setter('Has', 'false')
    count = g.call(PanelWidget.GetChildrenCount, self=panel)

    def one(k):
        g.cast(cls, g.call(PanelWidget.GetChildAt, self=panel, Index=k))
        g.setter('Has', 'true')

    looping(g, for_range(g, 0, g.math('Subtract_IntInt', count, 1)), one)
    yes, no = g.branch(g.call(KismetMathLibrary.Not_PreBool, A=g.get('Has')))
    g.loose = yes


def told(node, pin_name, rebuild=True):
    """
    Tells a wildcard node what was just connected to it - see add_settings_splice.py.

    Not rebuilt for a macro: reconstructing a ForEachLoop forgets the type it was just told.
    """
    for attempt in ('node_pin_type_changed', 'node_reconstruct') if rebuild else ('node_pin_type_changed',):
        try:
            if attempt == 'node_pin_type_changed':
                node.node_pin_type_changed(pin(node, pin_name))
            else:
                node.node_reconstruct()
        except Exception as problem:
            say('  %s refused on %s: %s' % (attempt, node.node_get_title(), problem))


def set_fields(g, struct, target, **members):
    """
    'Set members in <struct>' on a variable or an element reference.

    Every member pin starts hidden. The list saying which are shown is built fresh here: editing
    the copy the plugin hands back crashes the editor.
    """
    x, y = g.at(200)
    node = g.page.graph_add_node(K2Node_SetFieldsInStruct, x, y)
    node.StructType = ue.find_struct(struct)
    node.node_reconstruct()
    node.ShowPinForProperties = [OptionalPinFromProperty(PropertyName=name, bShowPin=True,
                                                         bCanToggleVisibility=True) for name in members]
    node.node_reconstruct()
    g.feed(node, 'StructRef', target)
    for name, value in members.items():
        g.feed(node, name, value)
    return g.then(node)


#--- the graph ---------------------------------------------------------------------------------------

def build_graph(widget, tile_class, strip_class, bullet_class, inspector_class, dock_class, sale_class, shop,
                stall_class):
    shop_tile_class, generic_class, shop_strip_class = shop
    g = Graph(widget)
    tick = event(widget, UserWidget, 'Tick', 0, 0)
    g.loose = [(tick, 'then')]

    #The debounce counts down every frame, clamped at zero.
    g.setter('Busy', g.call(KismetMathLibrary.Max, A=g.math('Subtract_IntInt', g.get('Busy'), 1), B=0))

    #--- every few frames, a socket strip into each item tile that has none yet -------------------
    #A tile's canvas ends with our strip once it has one, so "is the last child ours" is the test -
    #one cast per tile, and tiles the game makes later (a chest, the merchant) are found next time.
    g.setter('Scan', g.math('Add_IntInt', g.get('Scan'), 1))

    def attach_pass():
        g.setter('Scan', 0)
        found = g.run(WidgetBlueprintLibrary.GetAllWidgetsOfClass, TopLevelOnly='false')
        pin(found, 'WidgetClass').default_object = tile_class
        loop = g.for_each((found, 'FoundWidgets'))
        g.then(loop, 'Exec', 'Completed')
        after = list(g.loose)

        g.loose = [(loop, 'LoopBody')]
        tile = g.cast(tile_class, (loop, 'Array Element'))
        padder = g.get('ItemInfoPadder', tile_class, of=tile)
        unless_holding(g, padder, strip_class)

        made = g.run(WidgetBlueprintLibrary.Create, OwningPlayer=g.call(UserWidget.GetOwningPlayer))
        pin(made, 'WidgetType').default_object = strip_class
        strip = g.cast(strip_class, (made, 'ReturnValue'))
        g.setter('Tile', tile, owner=strip_class, of=strip)
        placed = (g.run(CanvasPanel.AddChildToCanvas, self=padder, Content=strip), 'ReturnValue')
        #Over the whole tile, above everything else in it; the strip places its own sockets.
        g.run(CanvasPanelSlot.SetAnchors, self=placed,
              InAnchors='(Minimum=(X=0.000000,Y=0.000000),Maximum=(X=1.000000,Y=1.000000))')
        g.run(CanvasPanelSlot.SetOffsets, self=placed,
              InOffset='(Left=0.000000,Top=0.000000,Right=0.000000,Bottom=0.000000)')
        g.run(CanvasPanelSlot.SetZOrder, self=placed, InZOrder=50)
        g.loose = after

        #The same for the item inspector: our Sockets section, once.
        inspectors = g.run(WidgetBlueprintLibrary.GetAllWidgetsOfClass, TopLevelOnly='false')
        pin(inspectors, 'WidgetClass').default_object = inspector_class
        each = g.for_each((inspectors, 'FoundWidgets'))
        g.then(each, 'Exec', 'Completed')
        done = list(g.loose)
        g.loose = [(each, 'LoopBody')]
        inspector = g.cast(inspector_class, (each, 'Array Element'))
        canvas = g.get('WholeCanvas', inspector_class, of=inspector)
        unless_holding(g, canvas, dock_class)
        made = g.run(WidgetBlueprintLibrary.Create, OwningPlayer=g.call(UserWidget.GetOwningPlayer))
        pin(made, 'WidgetType').default_object = dock_class
        dock = g.cast(dock_class, (made, 'ReturnValue'))
        g.setter('Inspector', inspector, owner=dock_class, of=dock)
        docked = (g.run(CanvasPanel.AddChildToCanvas, self=canvas, Content=dock), 'ReturnValue')
        g.run(CanvasPanelSlot.SetAnchors, self=docked,
              InAnchors='(Minimum=(X=1.000000,Y=0.000000),Maximum=(X=1.000000,Y=0.000000))')
        g.run(CanvasPanelSlot.SetAlignment, self=docked, InAlignment='(X=1.000000,Y=0.000000)')
        g.run(CanvasPanelSlot.SetPosition, self=docked, InPosition='(X=%f,Y=%f)' % (DOCK_X, DOCK_Y))
        g.run(CanvasPanelSlot.SetSize, self=docked, InSize='(X=%f,Y=%f)' % (DOCK_W, DOCK_H))
        g.run(CanvasPanelSlot.SetZOrder, self=docked, InZOrder=60)
        g.loose = done

        #And each shop tile: a strip over its generic slot widget's canvas, once.
        shops = g.run(WidgetBlueprintLibrary.GetAllWidgetsOfClass, TopLevelOnly='false')
        pin(shops, 'WidgetClass').default_object = shop_tile_class
        each_shop = g.for_each((shops, 'FoundWidgets'))
        g.then(each_shop, 'Exec', 'Completed')
        shops_done = list(g.loose)
        g.loose = [(each_shop, 'LoopBody')]
        shop_tile = g.cast(shop_tile_class, (each_shop, 'Array Element'))
        holder = g.get('UMG_GenericSlotWidget', shop_tile_class, of=shop_tile)
        pad = g.get('ContentPadder', generic_class, of=holder)
        unless_holding(g, pad, shop_strip_class)
        made_shop = g.run(WidgetBlueprintLibrary.Create, OwningPlayer=g.call(UserWidget.GetOwningPlayer))
        pin(made_shop, 'WidgetType').default_object = shop_strip_class
        shop_strip = g.cast(shop_strip_class, (made_shop, 'ReturnValue'))
        g.setter('Sale', shop_tile, owner=shop_strip_class, of=shop_strip)
        g.setter('Holder', holder, owner=shop_strip_class, of=shop_strip)
        on_tile = (g.run(CanvasPanel.AddChildToCanvas, self=pad, Content=shop_strip), 'ReturnValue')
        g.run(CanvasPanelSlot.SetAnchors, self=on_tile,
              InAnchors='(Minimum=(X=0.000000,Y=0.000000),Maximum=(X=1.000000,Y=1.000000))')
        g.run(CanvasPanelSlot.SetOffsets, self=on_tile,
              InOffset='(Left=0.000000,Top=0.000000,Right=0.000000,Bottom=0.000000)')
        g.run(CanvasPanelSlot.SetZOrder, self=on_tile, InZOrder=50)
        g.loose = shops_done

    g.either(g.math('GreaterEqual_IntInt', g.get('Scan'), SCAN_EVERY), attach_pass, lambda: None)

    #--- every few frames, the item description's socket and gem lines get their pictures ---------
    g.setter('Scan2', g.math('Add_IntInt', g.get('Scan2'), 1))

    def line_pass():
        g.setter('Scan2', 0)
        found = g.run(WidgetBlueprintLibrary.GetAllWidgetsOfClass, TopLevelOnly='false')
        pin(found, 'WidgetClass').default_object = bullet_class
        loop = g.for_each((found, 'FoundWidgets'))
        g.then(loop, 'Exec', 'Completed')
        after = list(g.loose)

        g.loose = [(loop, 'LoopBody')]
        line = g.cast(bullet_class, (loop, 'Array Element'))
        said = g.call(TextBlock.GetText, self=g.get('Text', bullet_class, of=line))
        g.setter('Line', g.call(KismetTextLibrary.Conv_TextToString, InText=said))
        g.setter('Matched', 'false')
        for index, prefix in enumerate(line_prefixes()):
            def paint(index=index):
                g.setter('Matched', 'true')
                g.run(DungeonsImage.SetBrushFromTexture, self=g.get('Bullet', bullet_class, of=line),
                      Texture=g.element(g.get('Pictures'), index), bMatchSize='false')
                #The diamond is tinted to the line's rarity; a picture is shown as it is.
                g.run(DungeonsImage.SetColorAndOpacity, self=g.get('Bullet', bullet_class, of=line),
                      InColorAndOpacity=g.call(KismetMathLibrary.MakeColor, R=1, G=1, B=1, A=1))
            g.either(g.call(KismetStringLibrary.StartsWith, SourceString=g.get('Line'), InPrefix=prefix),
                     paint, lambda: None)
        #The dark diamond behind the bullet is right for a diamond and wrong behind a gem - or
        #behind a Mastery line's star, which Weapon mastery's own pass puts there.
        mastery = g.call(KismetStringLibrary.StartsWith, SourceString=g.get('Line'), InPrefix=MASTERY_PREFIX)
        g.either(g.math('BooleanOR', g.get('Matched'), mastery),
                 lambda: g.run(Widget.SetRenderOpacity, self=g.get('BulletShadow', bullet_class, of=line), InOpacity=0),
                 lambda: g.run(Widget.SetRenderOpacity, self=g.get('BulletShadow', bullet_class, of=line), InOpacity=1))
        g.loose = after

    g.either(g.math('GreaterEqual_IntInt', g.get('Scan2'), LINE_EVERY), line_pass, lambda: None)

    #--- K opens and closes; closing forgets a picked socket ------------------------------------
    owner = g.call(UserWidget.GetOwningPlayer)
    pressed = g.call(PlayerController.WasInputKeyJustPressed, self=owner, Key=None)
    set_default(pressed[0], 'Key', KEY)
    asked, idle = g.branch(pressed)
    g.loose = asked
    g.either(g.call(Widget.IsVisible, self=g.get('Panel')),
             lambda: (g.shows('Panel', COLLAPSED), g.setter('Sel', -1)),
             lambda: g.shows('Panel', SHOWN))
    g.loose += idle

    #--- the hero, and every count -------------------------------------------------------------
    g.section()
    pawn = g.call(UserWidget.GetOwningPlayerPawn)
    x, y = g.at()
    hero = g.page.graph_add_node_dynamic_cast(PlayerCharacter, x, y)
    link(pawn[0], pawn[1], hero, 'Object')
    mcd_ui.reconstruct(hero)
    g.then(hero, 'execute', 'then')
    g.hero = (hero, mcd_ui.as_pin(hero))

    #--- a gem found with emeralds, in a mission ------------------------------------------------
    emeralds = lambda: g.call(WalletComponent.Balance, self=g.wallet(), Type=g.currency(FOUND_WITH))
    picked_up = g.both(g.get('InMission'), g.math('GreaterEqual_IntInt', g.get('Seen'), 0),
                       g.math('Greater_IntInt', emeralds(), g.get('Seen')))

    def find():
        grade = g.math('Add_IntInt',
                       g.call(KismetMathLibrary.Conv_BoolToInt,
                              InBool=g.math('GreaterEqual_FloatFloat', g.call(KismetMathLibrary.RandomFloat), FLAWLESS_FROM)),
                       g.call(KismetMathLibrary.Conv_BoolToInt,
                              InBool=g.math('GreaterEqual_FloatFloat', g.call(KismetMathLibrary.RandomFloat), PERFECT_FROM)))
        kind = g.call(KismetMathLibrary.RandomIntegerInRange, Min=0, Max=len(KINDS) - 1)
        #Set once: a random node gives a new answer every time it is asked.
        g.setter('Found', g.math('Add_IntInt', g.math('Multiply_IntInt', kind, len(GRADES)), grade))
        g.run(WalletComponent.ClientAdd, self=g.wallet(),
              Type=g.currency(g.element(g.get('GemNames'), g.get('Found'))), Amount=1, reason='Pickup')
        words = g.call(KismetStringLibrary.Concat_StrStr, A='Found: ', B=g.element(g.get('Titles'), g.get('Found')))
        g.run(TextBlock.SetText, self=g.get('ToastText'), InText=g.call(KismetTextLibrary.Conv_StringToText, InString=words))
        g.run(Image.SetBrushFromTexture, self=g.get('ToastPic'),
              Texture=g.element(g.get('Pictures'), g.math('Add_IntInt', g.get('Found'), 1)), bMatchSize='false')
        g.setter('ToastTime', TOAST_FRAMES)

    g.either(picked_up,
             lambda: g.either(g.math('Less_FloatFloat', g.call(KismetMathLibrary.RandomFloat), FIND_CHANCE),
                              find, lambda: None),
             lambda: None)
    g.setter('Seen', emeralds())

    g.setter('ToastTime', g.call(KismetMathLibrary.Max, A=g.math('Subtract_IntInt', g.get('ToastTime'), 1), B=0))
    g.either(g.math('Greater_IntInt', g.get('ToastTime'), 0),
             lambda: g.shows('Toast', LOOKS), lambda: g.shows('Toast', COLLAPSED))

    #--- the Gem Merchant ------------------------------------------------------------------------
    #Every few frames: is the stall here (the Camp) and where.
    g.section()
    g.setter('ShopScan', g.math('Add_IntInt', g.get('ShopScan'), 1))

    def stall_scan():
        g.setter('ShopScan', 0)
        g.setter('StallHere', 'false')
        stalls = g.run(GameplayStatics.GetAllActorsOfClass)
        pin(stalls, 'ActorClass').default_object = stall_class
        loop = g.for_each((stalls, 'OutActors'))
        g.then(loop, 'Exec', 'Completed')
        after = list(g.loose)
        g.loose = [(loop, 'LoopBody')]
        g.setter('StallAt', g.call(Actor.K2_GetActorLocation, self=(loop, 'Array Element')))
        g.setter('StallHere', 'true')
        g.loose = after

    g.either(g.math('GreaterEqual_IntInt', g.get('ShopScan'), SCAN_EVERY), stall_scan, lambda: None)

    #A left click that lands near the stall opens it.
    player = lambda: g.call(UserWidget.GetOwningPlayer)
    clicked = lambda: g.call(PlayerController.WasInputKeyJustPressed, self=player(), Key=None)

    def maybe_open():
        pressed = g.call(PlayerController.WasInputKeyJustPressed, self=player(), Key=None)
        set_default(pressed[0], 'Key', 'LeftMouseButton')
        under = g.call(PlayerController.GetHitResultUnderCursorByChannel, self=player(),
                       TraceChannel='TraceTypeQuery1', bTraceComplex='false')
        hit = g.call(GameplayStatics.BreakHitResult, out='Location', Hit=(under[0], 'HitResult'))
        near = g.math('Less_FloatFloat', g.call(KismetMathLibrary.Vector_Distance, V1=hit, V2=g.get('StallAt')), STALL_REACH)
        g.either(g.both(pressed, under, near), lambda: g.setter('ShopOpen', 'true'), lambda: None)

    g.either(g.both(g.get('StallHere'), g.call(KismetMathLibrary.Not_PreBool, A=g.get('ShopOpen'))),
             maybe_open, lambda: None)

    #Closed by its button, Esc, walking off, or the stall going (a mission).
    def maybe_close():
        esc = g.call(PlayerController.WasInputKeyJustPressed, self=player(), Key=None)
        set_default(esc[0], 'Key', 'Escape')
        away = g.math('Greater_FloatFloat', g.call(KismetMathLibrary.Vector_Distance,
                      V1=g.call(Actor.K2_GetActorLocation, self=g.hero), V2=g.get('StallAt')), STALL_LEAVE)
        gone = g.call(KismetMathLibrary.Not_PreBool, A=g.get('StallHere'))
        shut = g.math('BooleanOR', g.math('BooleanOR', g.call(Button.IsPressed, self=g.get('ShopClose')), esc),
                      g.math('BooleanOR', away, gone))
        g.either(shut, lambda: g.setter('ShopOpen', 'false'), lambda: None)

    g.either(g.get('ShopOpen'), maybe_close, lambda: None)
    g.either(g.get('ShopOpen'), lambda: g.shows('ShopPanel', SHOWN), lambda: g.shows('ShopPanel', COLLAPSED))


    #Buying, while it is up: a click is one gem, its price in emeralds, if there are enough.
    g.setter('ShopBusy', g.call(KismetMathLibrary.Max, A=g.math('Subtract_IntInt', g.get('ShopBusy'), 1), B=0))

    def open_shop():
        for kind, grade in all_gems():
            owned = g.call(WalletComponent.Balance, self=g.wallet(), Type=g.currency(gem_id(kind, grade)))
            g.run(TextBlock.SetText, self=g.get('ShopOwn%s%d' % (kind, grade)),
                  InText=g.call(KismetTextLibrary.Conv_StringToText, InString=g.call(
                      KismetStringLibrary.Concat_StrStr, A='You have ',
                      B=g.call(KismetStringLibrary.Conv_IntToString, InInt=owned))))
        for index, (kind, grade) in enumerate(all_gems()):
            price = GEM_PRICES[grade - 1]
            fired, carry = g.branch(g.both(g.call(Button.IsPressed, self=g.get('Buy%s%d' % (kind, grade))),
                                           g.math('LessEqual_IntInt', g.get('ShopBusy'), 0)))
            g.loose = fired

            def buy(kind=kind, grade=grade, index=index, price=price):
                g.run(WalletComponent.Deduct, self=g.wallet(), Type=g.currency(FOUND_WITH), Amount=price)
                g.run(WalletComponent.ClientAdd, self=g.wallet(), Type=g.currency(gem_id(kind, grade)), Amount=1,
                      reason='Default')
                g.says('ToastText', 'Bought: %s %s' % (GRADES[grade - 1], kind))
                g.run(Image.SetBrushFromTexture, self=g.get('ToastPic'),
                      Texture=g.element(g.get('Pictures'), index + 1), bMatchSize='false')
                g.setter('ToastTime', TOAST_FRAMES)
                #Spent here, not found: the emerald watch must not read it as a pickup.
                g.setter('Seen', -1)

            def too_dear(kind=kind, grade=grade, index=index):
                g.says('ToastText', 'Not enough emeralds')
                g.run(Image.SetBrushFromTexture, self=g.get('ToastPic'),
                      Texture=g.element(g.get('Pictures'), index + 1), bMatchSize='false')
                g.setter('ToastTime', TOAST_FRAMES)

            g.either(g.math('GreaterEqual_IntInt', g.call(WalletComponent.Balance, self=g.wallet(),
                                                          Type=g.currency(FOUND_WITH)), price), buy, too_dear)
            g.setter('ShopBusy', DEBOUNCE)
            g.loose = carry

    g.either(g.get('ShopOpen'), open_shop, lambda: None)

    #--- sockets on found items -------------------------------------------------------------------
    g.section()
    g.setter('Roll', g.math('Add_IntInt', g.get('Roll'), 1))

    def roll_pass():
        g.setter('Roll', 0)
        stash = g.get('ItemStashComponent', PlayerCharacter, of=g.hero)
        loop = g.for_each(g.call(ItemStashComponent.GetInventorySlots, self=stash))
        g.then(loop, 'Exec', 'Completed')
        after = list(g.loose)
        g.loose = [(loop, 'LoopBody')]
        found_item = lambda: g.get('Item', InventoryItemSlot, of=(loop, 'Array Element'))
        g.either(g.valid(found_item()), lambda: roll_sockets(g, found_item, only_new=True), lambda: None)
        g.loose = after

        #And what the shops have on sale: the item each sale tile shows. Rolled the same way, so the
        #sockets it shows are the ones it arrives with - bought, it is marked new and rolls again from
        #the same power and rarity.
        tiles = g.run(WidgetBlueprintLibrary.GetAllWidgetsOfClass, TopLevelOnly='false')
        pin(tiles, 'WidgetClass').default_object = sale_class
        each = g.for_each((tiles, 'FoundWidgets'))
        g.then(each, 'Exec', 'Completed')
        done = list(g.loose)
        g.loose = [(each, 'LoopBody')]
        tile = g.cast(sale_class, (each, 'Array Element'))
        slot = lambda: g.get('merchantItemSlot', sale_class, of=tile)
        on_sale = lambda: g.call(MerchantItemSlotBase.GetDisplayItemCache, self=slot())
        g.either(g.valid(slot()),
                 lambda: g.either(g.valid(on_sale()), lambda: roll_sockets(g, on_sale, only_new=False), lambda: None),
                 lambda: None)
        g.loose = done

    g.either(g.math('GreaterEqual_IntInt', g.get('Roll'), ROLL_EVERY), roll_pass, lambda: None)

    for kind, grade in all_gems():
        balance = g.call(WalletComponent.Balance, self=g.wallet(), Type=g.currency(gem_id(kind, grade)))
        worded = g.call(KismetTextLibrary.Conv_IntToText, Value=balance, bUseGrouping='false')
        g.run(TextBlock.SetText, self=g.get('Count%s%d' % (kind, grade)), InText=worded)

    #Everything below is for an open panel only.
    open_now, closed = g.branch(g.call(Widget.IsVisible, self=g.get('Panel')))
    g.loose = open_now

    #--- a write asked for last frame -----------------------------------------------------------
    #The one place an item changes. A click only says what to write (WItem, WIndex, WId, WRarity)
    #and raises Pending, so this is built once rather than thirty times.
    g.section()
    ready = g.both(g.math('EqualEqual_IntInt', g.get('Pending'), 1), g.valid(g.get('WItem')))
    yes, no = g.branch(ready)
    g.loose = yes
    g.setter('Work', g.get('Item', InventoryItem, of=g.get('WItem')))
    g.setter('WorkProps', (g.breaks('InventoryItemData', g.get('Work')), 'ArmorProperties'))
    set_fields(g, 'ArmorPropertyData', g.element(g.get('WorkProps'), g.get('WIndex')),
               ID=g.get('WId'), Rarity=g.get('WRarity'))
    set_fields(g, 'InventoryItemData', g.get('Work'), ArmorProperties=g.get('WorkProps'))
    g.setter('Item', g.get('Work'), owner=InventoryItem, of=g.get('WItem'))
    g.loose += no
    g.setter('Pending', 0)

    #--- what is equipped, and where its sockets are --------------------------------------------
    for i, (caption, slot_name) in enumerate(SLOTS):
        g.section()
        slots = g.call(ItemStashComponent.GetEquipmentSlots,
                       self=g.get('ItemStashComponent', PlayerCharacter, of=g.hero))
        x, y = g.at()
        found = g.page.graph_add_node_call_function(BlueprintMapLibrary.Map_Find, x, y)
        link(slots[0], slots[1], found, 'TargetMap')
        told(found, 'TargetMap')
        set_default(found, 'Key', slot_name)
        g.setter('Item%d' % i, g.get('Item', InventoryItemSlot, of=(found, 'Value')))

        #Sockets are one run of lines: Base is the first, N how many (at most three).
        g.setter('Base%d' % i, -1)
        g.setter('N%d' % i, 0)
        worn = g.valid(g.get('Item%d' % i))

        def count(i=i):
            x, y = g.at()
            loop = g.page.graph_add_node(K2Node_MacroInstance, x, y)
            loop.MacroGraphReference = GraphReference(MacroGraph=ue.load_object(
                EdGraph, '/Engine/EditorBlueprintResources/StandardMacros.StandardMacros:ForEachLoop'))
            loop.node_allocate_default_pins()
            #connect, not link: it goes through the graph schema, which is what tells a macro its
            #type. Linked and then told, a ForEachLoop compiles as "undetermined".
            lines = g.props(g.get('Item%d' % i))
            pin(lines[0], lines[1]).connect(pin(loop, 'Array'))
            say('loop typed: %s' % pin(loop, 'Array Element').category)
            g.then(loop, 'Exec', 'Completed')
            after = list(g.loose)

            entry = g.breaks('ArmorPropertyData', (loop, 'Array Element'))
            line_id = g.call(KismetMathLibrary.Conv_ByteToInt, InByte=(entry, 'ID'))
            g.loose = [(loop, 'LoopBody')]
            take, skip = g.branch(g.both(g.is_socket(line_id),
                                         g.math('Less_IntInt', g.get('N%d' % i), SOCKETS)))
            g.loose = take
            g.either(g.math('EqualEqual_IntInt', g.get('Base%d' % i), -1),
                     lambda: g.setter('Base%d' % i, (loop, 'Array Index')),
                     lambda: None)
            g.setter('N%d' % i, g.math('Add_IntInt', g.get('N%d' % i), 1))
            g.loose = after

        g.either(worn, count, lambda: None)

        #Its name, or a dash.
        g.either(g.valid(g.get('Item%d' % i)),
                 lambda i=i: g.run(TextBlock.SetText, self=g.get('ItemName%d' % i),
                                   InText=g.call(InventoryItem.GetDisplayNameText, self=g.get('Item%d' % i))),
                 lambda i=i, caption=caption: g.says('ItemName%d' % i, '%s: nothing equipped' % caption))

        g.either(g.math('EqualEqual_IntInt', g.get('N%d' % i), 0),
                 lambda i=i: g.shows('NoSockets%d' % i, LOOKS),
                 lambda i=i: g.shows('NoSockets%d' % i, COLLAPSED))

        for j in range(SOCKETS):
            def socket_on(i=i, j=j):
                g.shows('Sock%d%d' % (i, j), CLICKABLE)
                line_id = g.id_at(g.props(g.get('Item%d' % i)), g.math('Add_IntInt', g.get('Base%d' % i), j))
                which = g.picture_of(line_id)
                g.run(Image.SetBrushFromTexture, self=g.get('SockPic%d%d' % (i, j)),
                      Texture=g.element(g.get('Pictures'), which), bMatchSize='false')
                g.socket_tip(g.get('Sock%d%d' % (i, j)), line_id)

            g.either(g.math('Less_IntInt', j, g.get('N%d' % i)), socket_on,
                     lambda i=i, j=j: g.shows('Sock%d%d' % (i, j), COLLAPSED))

            picked = g.both(g.math('EqualEqual_IntInt', g.get('Sel'), i * SOCKETS + j),
                            g.math('Less_IntInt', j, g.get('N%d' % i)))
            g.either(picked,
                     lambda i=i, j=j: g.shows('Frame%d%d' % (i, j), LOOKS),
                     lambda i=i, j=j: g.shows('Frame%d%d' % (i, j), COLLAPSED))

    #--- clicks: each test falls through to the next, and a press that fires ends the frame -------
    def pressed_test(button_name):
        return g.both(g.call(Button.IsPressed, self=g.get(button_name)),
                      g.math('LessEqual_IntInt', g.get('Busy'), 0))

    def hold():
        g.setter('Busy', DEBOUNCE)

    def ask_write(item, index, line, rarity):
        g.setter('WItem', item)
        g.setter('WIndex', index)
        g.setter('WId', line)
        g.setter('WRarity', rarity)
        g.setter('Pending', 1)

    for i in range(len(SLOTS)):
        for j in range(SOCKETS):
            g.section()
            fired, carry = g.branch(pressed_test('Sock%d%d' % (i, j)))
            g.loose = fired
            where = lambda i=i, j=j: g.math('Add_IntInt', g.get('Base%d' % i), j)
            line_id = g.id_at(g.props(g.get('Item%d' % i)), where())

            def pick(i=i, j=j):
                g.setter('SelItem', g.get('Item%d' % i))
                g.setter('SelIndex', where())
                g.setter('Sel', i * SOCKETS + j)

            def take_out(i=i, j=j, line_id=line_id):
                def give_back():
                    name = g.element(g.get('GemNames'), g.gem_of(line_id))
                    g.run(WalletComponent.ClientAdd, self=g.wallet(), Type=g.currency(name), Amount=1,
                          reason='Default')
                    ask_write(g.get('Item%d' % i), where(), EMPTY, 'Common')
                    g.setter('Sel', -1)
                g.either(g.math('GreaterEqual_IntInt', line_id, FIRST_GEM), give_back, lambda: None)

            g.either(g.math('EqualEqual_IntInt', line_id, SOCKET_ID), pick, take_out)
            hold()
            g.loose = carry

    for kind, grade in all_gems():
        g.section()
        fired, carry = g.branch(pressed_test('Gem%s%d' % (kind, grade)))
        g.loose = fired
        owned = g.call(WalletComponent.Balance, self=g.wallet(), Type=g.currency(gem_id(kind, grade)))
        still_empty = g.math('EqualEqual_IntInt',
                             g.id_at(g.props(g.get('SelItem')), g.get('SelIndex')), SOCKET_ID)
        ready = g.both(g.math('GreaterEqual_IntInt', g.get('Sel'), 0),
                       g.math('GreaterEqual_IntInt', owned, 1),
                       g.valid(g.get('SelItem')),
                       still_empty)

        def set_gem(kind=kind, grade=grade):
            g.run(WalletComponent.Deduct, self=g.wallet(), Type=g.currency(gem_id(kind, grade)), Amount=1)
            #The armour row's sockets are 3, 4 and 5.
            in_armour = g.both(g.math('GreaterEqual_IntInt', g.get('Sel'), SOCKETS),
                               g.math('Less_IntInt', g.get('Sel'), 2 * SOCKETS))
            g.either(in_armour,
                     lambda: ask_write(g.get('SelItem'), g.get('SelIndex'), gem_line(kind, grade, True), RARITIES[grade - 1]),
                     lambda: ask_write(g.get('SelItem'), g.get('SelIndex'), gem_line(kind, grade), RARITIES[grade - 1]))
            g.setter('Sel', -1)

        g.either(ready, set_gem, lambda: None)
        hold()
        g.loose = carry

    for kind, grade in all_gems():
        if grade == 3:
            continue
        g.section()
        fired, carry = g.branch(pressed_test('Up%s%d' % (kind, grade)))
        g.loose = fired
        owned = g.call(WalletComponent.Balance, self=g.wallet(), Type=g.currency(gem_id(kind, grade)))

        def join(kind=kind, grade=grade):
            g.run(WalletComponent.Deduct, self=g.wallet(), Type=g.currency(gem_id(kind, grade)), Amount=3)
            g.run(WalletComponent.ClientAdd, self=g.wallet(), Type=g.currency(gem_id(kind, grade + 1)),
                  Amount=1, reason='Default')

        g.either(g.math('GreaterEqual_IntInt', owned, 3), join, lambda: None)
        hold()
        g.loose = carry

    ue.compile_blueprint(widget)
    say('graph built')


#--- sockets on item tiles ---------------------------------------------------------------------------

def build_tile_stub():
    """The game's item tile, as far as the strip needs it. Never ships."""
    there = on_disk(TILE_STUB, 'WidgetBlueprint')
    if there is not None:
        say('tile stub already there: ' + TILE_STUB)
        return there
    factory = WidgetBlueprintFactory()
    factory.ParentClass = UserWidget
    stub = factory.factory_create_new(TILE_STUB)
    stub.modify()
    padder = CanvasPanel('ItemInfoPadder', stub.WidgetTree)
    padder.bIsVariable = True
    put(stub.WidgetTree.RootWidget, padder, 0.0, 0.0, 100.0, 100.0)
    typed_variable(stub, 'InventoryItemSlot', PinCategory='object', PinSubCategoryObject=InventoryItemSlot)
    ue.blueprint_add_member_variable(stub, 'Hovered', 'bool', False, 'false')
    ue.blueprint_mark_as_structurally_modified(stub)
    ue.compile_blueprint(stub)
    keep(stub)
    say('tile stub built: ' + TILE_STUB)
    return stub


def build_bullet_stub():
    """The game's item description line, as far as the line pass needs it. Never ships."""
    there = on_disk(BULLET_STUB, 'WidgetBlueprint')
    if there is not None:
        say('line stub already there: ' + BULLET_STUB)
        return there
    factory = WidgetBlueprintFactory()
    factory.ParentClass = UserWidget
    stub = factory.factory_create_new(BULLET_STUB)
    stub.modify()
    bullet = DungeonsImage('Bullet', stub.WidgetTree)
    bullet.bIsVariable = True
    put(stub.WidgetTree.RootWidget, bullet, 0.0, 0.0, 20.0, 20.0)
    shadow = DungeonsImage('BulletShadow', stub.WidgetTree)
    shadow.bIsVariable = True
    put(stub.WidgetTree.RootWidget, shadow, 0.0, 0.0, 20.0, 20.0)
    text = TextBlock('Text', stub.WidgetTree)
    text.bIsVariable = True
    put(stub.WidgetTree.RootWidget, text, 30.0, 0.0, 200.0, 20.0)
    ue.blueprint_mark_as_structurally_modified(stub)
    ue.compile_blueprint(stub)
    keep(stub)
    say('line stub built: ' + BULLET_STUB)
    return stub


def build_sale_stub():
    """A shop's sale tile, as far as the roll needs it: the merchant slot it shows. Never ships."""
    there = on_disk(SALE_STUB, 'WidgetBlueprint')
    if there is not None:
        say('sale stub already there: ' + SALE_STUB)
        return there
    factory = WidgetBlueprintFactory()
    factory.ParentClass = UserWidget
    stub = factory.factory_create_new(SALE_STUB)
    stub.modify()
    typed_variable(stub, 'merchantItemSlot', PinCategory='object', PinSubCategoryObject=MerchantItemSlotBase)
    ue.blueprint_mark_as_structurally_modified(stub)
    ue.compile_blueprint(stub)
    keep(stub)
    say('sale stub built: ' + SALE_STUB)
    return stub


def build_inspector_stub():
    """The game's item inspector, as far as the Sockets section needs it. Never ships."""
    there = on_disk(INSPECTOR_STUB, 'WidgetBlueprint')
    if there is not None:
        say('inspector stub already there: ' + INSPECTOR_STUB)
        return there
    factory = WidgetBlueprintFactory()
    factory.ParentClass = UserWidget
    stub = factory.factory_create_new(INSPECTOR_STUB)
    stub.modify()
    canvas = CanvasPanel('WholeCanvas', stub.WidgetTree)
    canvas.bIsVariable = True
    put(stub.WidgetTree.RootWidget, canvas, 0.0, 0.0, 800.0, 800.0)
    typed_variable(stub, 'InspectedItem', PinCategory='object', PinSubCategoryObject=InventoryItem)
    #The game's own redraw of the inspected item - what selecting it again does. Declared here, empty,
    #so a call compiles; the cooked call resolves by name to the game's function.
    ue.blueprint_add_function(stub, 'RefreshInspectedSlotItem')
    #And the game's SetInspectedItem(Item): a function's parameters are its entry node's outputs.
    entry = ue.blueprint_add_function(stub, 'SetInspectedItem').Nodes[0]
    entry.node_create_pin(1, EdGraphPinType(PinCategory='object', PinSubCategoryObject=InventoryItem), 'Item')
    ue.blueprint_mark_as_structurally_modified(stub)
    ue.compile_blueprint(stub)
    keep(stub)
    say('inspector stub built: ' + INSPECTOR_STUB)
    return stub


def roll_sockets(g, item, only_new):
    """
    An item's sockets, once: gear only (melee, ranged, armour - EItemTag 10 to 12, never an
    artifact), with no socket lines yet and at most MOST_LINES of its own. The count comes from the
    item's own power and rarity, so asking again - or asking of the same item bought from a shop -
    gives the same answer. `only_new`: only while the item is still marked new, as a found one is.
    """
    tag = g.call(KismetMathLibrary.Conv_ByteToInt, InByte=g.call(InventoryItem.GetTag, self=item()))
    gear = g.both(g.math('GreaterEqual_IntInt', tag, 10), g.math('LessEqual_IntInt', tag, 12))
    wanted = g.both(g.call(InventoryItem.IsMarkedNew, self=item()), gear) if only_new else gear

    def look_at():
        g.setter('RItem', item())
        g.setter('Work', g.get('Item', InventoryItem, of=g.get('RItem')))
        g.setter('Lines', 0)
        g.setter('Sockets', 0)
        lines = g.for_each((g.breaks('InventoryItemData', g.get('Work')), 'ArmorProperties'))
        g.then(lines, 'Exec', 'Completed')
        done = list(g.loose)
        entry = g.breaks('ArmorPropertyData', (lines, 'Array Element'))
        line_id = g.call(KismetMathLibrary.Conv_ByteToInt, InByte=(entry, 'ID'))
        g.loose = [(lines, 'LoopBody')]
        g.setter('Lines', g.math('Add_IntInt', g.get('Lines'), 1))
        g.either(g.is_socket(line_id),
                 lambda: g.setter('Sockets', g.math('Add_IntInt', g.get('Sockets'), 1)), lambda: None)
        g.loose = done
        g.either(g.both(g.math('EqualEqual_IntInt', g.get('Sockets'), 0),
                        g.math('LessEqual_IntInt', g.get('Lines'), MOST_LINES)),
                 roll, lambda: None)

    def roll():
        #The item's own number for "random". Its power alone is not enough - every item at the
        #level cap has the same one, so every max-level Rare rolled the same, and never a socket -
        #so its enchantment options are mixed in: rolled when it dropped, different item to item,
        #and the same on a shop's copy and on the one bought.
        g.setter('Seed', g.call(KismetMathLibrary.Fraction, A=g.math(
            'Multiply_FloatFloat', (g.breaks('InventoryItemData', g.get('Work')), 'ItemPower'), 91.7319)))
        options = g.for_each((g.breaks('InventoryItemData', g.get('Work')), 'Enchantments'))
        g.then(options, 'Exec', 'Completed')
        mixed = list(g.loose)
        option = g.breaks('EnchantmentData', (options, 'Array Element'))
        which = g.call(KismetMathLibrary.Conv_IntToFloat, InInt=g.call(KismetMathLibrary.Conv_ByteToInt, InByte=(option, 'TypeID')))
        g.loose = [(options, 'LoopBody')]
        g.setter('Seed', g.call(KismetMathLibrary.Fraction, A=g.math(
            'Add_FloatFloat', g.math('Multiply_FloatFloat', g.get('Seed'), 1.37), g.math('Multiply_FloatFloat', which, 0.6180339))))
        g.loose = mixed

        data = g.breaks('InventoryItemData', g.get('Work'))
        chance = g.call(KismetMathLibrary.Fraction, A=g.math('Multiply_FloatFloat', g.get('Seed'), 13.4471))
        rarity = g.call(KismetMathLibrary.Conv_ByteToInt, InByte=(data, 'Rarity'))
        g.setter('Want', 0)
        for level, odds in enumerate(SOCKET_ODDS):
            def by_rarity(odds=odds):
                count = None
                for at_least in odds:
                    one = g.call(KismetMathLibrary.Conv_BoolToInt, InBool=g.math('Less_FloatFloat', chance, at_least))
                    count = one if count is None else g.math('Add_IntInt', count, one)
                g.setter('Want', count)
            g.either(g.math('EqualEqual_IntInt', rarity, level), by_rarity, lambda: None)

        #A new list: the item's own lines as they were, then the sockets. Made whole rather than
        #added to, because an array node cannot be told its type from here and Make Array can.
        for own in range(MOST_LINES + 1):
            for sockets in range(1, SOCKETS + 1):
                def write(own=own, sockets=sockets):
                    kept = [g.element((g.breaks('InventoryItemData', g.get('Work')), 'ArmorProperties'), i)
                            for i in range(own)]
                    empty = [g.makes('ArmorPropertyData', ID=EMPTY, Rarity='Common') for _ in range(sockets)]
                    set_fields(g, 'InventoryItemData', g.get('Work'), ArmorProperties=g.make_array(kept + empty))
                    g.setter('Item', g.get('Work'), owner=InventoryItem, of=g.get('RItem'))
                g.either(g.both(g.math('EqualEqual_IntInt', g.get('Lines'), own),
                                g.math('EqualEqual_IntInt', g.get('Want'), sockets)),
                         write, lambda: None)

    g.either(wanted, look_at, lambda: None)


def pending_write(g, after_write=None):
    """The one place a dock changes an item, from WItem, WIndex, WId and WRarity - as the K screen does."""
    yes, no = g.branch(g.both(g.math('EqualEqual_IntInt', g.get('Pending'), 1), g.valid(g.get('WItem'))))
    g.loose = yes
    g.setter('Work', g.get('Item', InventoryItem, of=g.get('WItem')))
    g.setter('WorkProps', (g.breaks('InventoryItemData', g.get('Work')), 'ArmorProperties'))
    set_fields(g, 'ArmorPropertyData', g.element(g.get('WorkProps'), g.get('WIndex')),
               ID=g.get('WId'), Rarity=g.get('WRarity'))
    set_fields(g, 'InventoryItemData', g.get('Work'), ArmorProperties=g.get('WorkProps'))
    g.setter('Item', g.get('Work'), owner=InventoryItem, of=g.get('WItem'))
    if after_write is not None:
        after_write()
    g.loose += no
    g.setter('Pending', 0)


def count_sockets(g, item, n_name, base_name):
    """How many socket lines an item has (at most three) and where the run of them starts."""
    loop = g.for_each(g.props(item()))
    g.then(loop, 'Exec', 'Completed')
    after = list(g.loose)
    entry = g.breaks('ArmorPropertyData', (loop, 'Array Element'))
    line_id = g.call(KismetMathLibrary.Conv_ByteToInt, InByte=(entry, 'ID'))
    g.loose = [(loop, 'LoopBody')]
    take, skip = g.branch(g.both(g.is_socket(line_id), g.math('Less_IntInt', g.get(n_name), SOCKETS)))
    g.loose = take
    g.either(g.math('EqualEqual_IntInt', g.get(base_name), -1),
             lambda: g.setter(base_name, (loop, 'Array Index')), lambda: None)
    g.setter(n_name, g.math('Add_IntInt', g.get(n_name), 1))
    g.loose = after


def build_dock(pictures, inspector_class):
    """
    The inventory's Sockets section: the inspected item's sockets, large and clickable, and a tray of
    the gems owned. Pick an empty socket, then a gem; click a set gem to take it out. Every socket and
    gem says what it is on hover. Hidden for an item without sockets.
    """
    there = on_disk(DOCK, 'WidgetBlueprint')
    if there is not None:
        say('dock already there: ' + DOCK)
        return there
    factory = WidgetBlueprintFactory()
    factory.ParentClass = UserWidget
    widget = factory.factory_create_new(DOCK)
    widget.modify()
    tree = widget.WidgetTree
    title_face = stub_font(TITLE_FONT)
    body_face = stub_font(BODY_FONT)
    tree.RootWidget.Visibility = 4                  # SelfHitTestInvisible

    #Clicks go through everywhere but the bar and the picker's own backings and buttons.
    panel = CanvasPanel('Panel', tree)
    panel.bIsVariable = True
    panel.Visibility = 1
    put(tree.RootWidget, panel, 0.0, 0.0, DOCK_W, DOCK_H)

    #--- the bar: the item's sockets --------------------------------------------------------------
    #No backing: the title and the sockets sit on the inspector's own background.
    put(panel, label(tree, 'Title', 'SOCKETS', GOLD, title_face, 18, typeface=TITLE_FACE, shadow=SHADOW),
        12.0, 6.0, 200.0, 24.0)
    for j in range(SOCKETS):
        cell = Overlay('SCell%d' % j, tree)
        for part in (image(tree, 'SRing%d' % j, pictures.get('T_Socket'), 44.0),
                     image(tree, 'SGem%d' % j, pictures.get('T_Ruby1'), 34.0, variable=True)):
            placed = cell.AddChild(part)
            placed.HorizontalAlignment = 2
            placed.VerticalAlignment = 2
        button = picture_button(tree, 'Sock%d' % j, cell)
        button.BackgroundColor = CLEAR
        button.Visibility = 1
        put(panel, button, 12.0 + j * 60.0, 32.0, DOCK_SOCKET, DOCK_SOCKET)
        frame = image(tree, 'Frame%d' % j, pictures.get('T_Select'), DOCK_SOCKET, variable=True)
        frame.Visibility = 1
        put(panel, frame, 12.0 + j * 60.0, 32.0, DOCK_SOCKET, DOCK_SOCKET)

    #--- the picker: the gems owned, while a socket is picked ---------------------------------------
    picker = CanvasPanel('Picker', tree)
    picker.bIsVariable = True
    picker.Visibility = 1
    put(panel, picker, 0.0, PICKER_Y, DOCK_W, DOCK_H - PICKER_Y)
    put(picker, fill(tree, 'PickerBacking', BACKING), 0.0, 0.0, DOCK_W, DOCK_H - PICKER_Y)
    put(picker, label(tree, 'TrayHead', 'Pick a gem for the socket', DIM, body_face, 14, typeface=BODY_FACE),
        12.0, 6.0, DOCK_W - 24.0, 20.0)
    tray = WrapBox('Tray', tree)
    put(picker, tray, 10.0, 28.0, DOCK_W - 20.0, DOCK_H - PICKER_Y - 34.0)
    for kind, grade in all_gems():
        cell = Overlay('GCell%s%d' % (kind, grade), tree)
        placed = cell.AddChild(image(tree, 'GIcon%s%d' % (kind, grade), pictures.get('T_%s%d' % (kind, grade)), 34.0))
        placed.HorizontalAlignment = 2
        placed.VerticalAlignment = 2
        count = label(tree, 'Count%s%d' % (kind, grade), '0', INK, body_face, 13, variable=True, typeface=BODY_FACE)
        placed = cell.AddChild(count)
        placed.HorizontalAlignment = 3
        placed.VerticalAlignment = 3
        button = picture_button(tree, 'Gem%s%d' % (kind, grade), cell)
        button.ToolTipText = gem_tip(kind, grade) + ' Click to set it in the picked socket.'
        button.Visibility = 1
        slot = tray.AddChild(button)
        slot.Padding = Margin(Left=2.0, Top=2.0, Right=2.0, Bottom=2.0)

    ints = {'Busy': '0', 'Sel': '-1', 'SelIndex': '0', 'WIndex': '0', 'Pending': '0', 'N': '0', 'Base': '-1'}
    for name, default in ints.items():
        ue.blueprint_add_member_variable(widget, name, 'int', False, default)
    for name in ('DItem', 'LastItem', 'SelItem', 'WItem'):
        typed_variable(widget, name, PinCategory='object', PinSubCategoryObject=InventoryItem)
    typed_variable(widget, 'Inspector', PinCategory='object', PinSubCategoryObject=inspector_class)
    typed_variable(widget, 'WId', EMPTY, PinCategory='byte', PinSubCategoryObject=ue.find_enum('EArmorPropertyID'))
    typed_variable(widget, 'WRarity', 'Common', PinCategory='byte', PinSubCategoryObject=ue.find_enum('EItemRarity'))
    typed_variable(widget, 'Work', PinCategory='struct', PinSubCategoryObject=ue.find_struct('InventoryItemData'))
    typed_variable(widget, 'WorkProps', PinCategory='struct', PinSubCategoryObject=ue.find_struct('ArmorPropertyData'),
                   ContainerType=1)
    pictures_variable(widget)
    typed_variable(widget, 'GemNames', '(%s)' % ','.join(gem_id(k, g) for k, g in all_gems()),
                   PinCategory='name', ContainerType=1)
    tips_variable(widget)
    ue.blueprint_mark_as_structurally_modified(widget)
    ue.compile_blueprint(widget)

    g = Graph(widget)
    tick = event(widget, UserWidget, 'Tick', 0, 0)
    g.loose = [(tick, 'then')]
    g.setter('Busy', g.call(KismetMathLibrary.Max, A=g.math('Subtract_IntInt', g.get('Busy'), 1), B=0))

    pawn = g.call(UserWidget.GetOwningPlayerPawn)
    hero = g.cast(PlayerCharacter, pawn)
    g.hero = hero

    #The inspected item; another one forgets the picked socket.
    g.setter('DItem', g.get('InspectedItem', inspector_class, of=g.get('Inspector')))
    g.either(g.call(KismetMathLibrary.NotEqual_ObjectObject, A=g.get('DItem'), B=g.get('LastItem')),
             lambda: g.setter('Sel', -1), lambda: None)
    g.setter('LastItem', g.get('DItem'))

    #Written, the inspector redraws the item at once, as if it were selected again.
    #By its path: getattr on a blueprint class hands back a Python callable, not the function, and a
    #call node made from that is silently nothing.
    #Its refresh keeps the item it already shows, so it is shown nothing and then the item, which is
    #what selecting it again does.
    inspect = ue.find_object(inspector_class.get_path_name() + ':SetInspectedItem')
    if inspect is None:
        raise Exception('the inspector stub has no SetInspectedItem')
    say('inspector: %s' % inspect.get_path_name())

    def redraw():
        g.run(inspect, self=g.get('Inspector'))
        g.run(inspect, self=g.get('Inspector'), Item=g.get('WItem'))

    pending_write(g, redraw)

    g.section()
    g.setter('N', 0)
    g.setter('Base', -1)
    g.either(g.valid(g.get('DItem')), lambda: count_sockets(g, lambda: g.get('DItem'), 'N', 'Base'), lambda: None)
    any_socket = lambda: g.math('Greater_IntInt', g.get('N'), 0)
    g.either(any_socket(), lambda: g.shows('Panel', SHOWN), lambda: g.shows('Panel', COLLAPSED))
    open_now, closed = g.branch(any_socket())
    g.loose = open_now

    where = lambda j: g.math('Add_IntInt', g.get('Base'), j)
    line_at = lambda j: g.id_at(g.props(g.get('DItem')), where(j))
    for j in range(SOCKETS):
        def socket_on(j=j):
            g.shows('Sock%d' % j, CLICKABLE)
            line_id = line_at(j)
            which = g.picture_of(line_id)
            g.run(Image.SetBrushFromTexture, self=g.get('SGem%d' % j),
                  Texture=g.element(g.get('Pictures'), which), bMatchSize='false')
            g.either(g.math('GreaterEqual_IntInt', line_id, FIRST_GEM),
                     lambda: g.shows('SGem%d' % j, LOOKS), lambda: g.shows('SGem%d' % j, COLLAPSED))
            g.socket_tip(g.get('Sock%d' % j), line_id)
        g.either(g.math('Less_IntInt', j, g.get('N')), socket_on, lambda j=j: g.shows('Sock%d' % j, COLLAPSED))
        g.either(g.both(g.math('EqualEqual_IntInt', g.get('Sel'), j), g.math('Less_IntInt', j, g.get('N'))),
                 lambda j=j: g.shows('Frame%d' % j, LOOKS), lambda j=j: g.shows('Frame%d' % j, COLLAPSED))
    g.either(g.math('GreaterEqual_IntInt', g.get('Sel'), 0),
             lambda: g.shows('Picker', SHOWN), lambda: g.shows('Picker', COLLAPSED))

    g.section()
    for kind, grade in all_gems():
        owned = g.call(WalletComponent.Balance, self=g.wallet(), Type=g.currency(gem_id(kind, grade)))
        g.run(TextBlock.SetText, self=g.get('Count%s%d' % (kind, grade)),
              InText=g.call(KismetTextLibrary.Conv_IntToText, Value=owned, bUseGrouping='false'))
        g.either(g.math('Greater_IntInt', owned, 0),
                 lambda kind=kind, grade=grade: g.shows('Gem%s%d' % (kind, grade), CLICKABLE),
                 lambda kind=kind, grade=grade: g.shows('Gem%s%d' % (kind, grade), COLLAPSED))

    #Clicks, polled: each test falls through to the next.
    def pressed_test(button_name):
        return g.both(g.call(Button.IsPressed, self=g.get(button_name)),
                      g.math('LessEqual_IntInt', g.get('Busy'), 0))

    def ask_write(item, index, line, rarity):
        g.setter('WItem', item)
        g.setter('WIndex', index)
        g.setter('WId', line)
        g.setter('WRarity', rarity)
        g.setter('Pending', 1)

    for j in range(SOCKETS):
        g.section()
        fired, carry = g.branch(pressed_test('Sock%d' % j))
        g.loose = fired
        line_id = line_at(j)

        def pick(j=j):
            def choose():
                g.setter('SelItem', g.get('DItem'))
                g.setter('SelIndex', where(j))
                g.setter('Sel', j)
            g.either(g.math('EqualEqual_IntInt', g.get('Sel'), j), lambda: g.setter('Sel', -1), choose)

        def take_out(j=j, line_id=line_id):
            def give_back():
                name = g.element(g.get('GemNames'), g.gem_of(line_id))
                g.run(WalletComponent.ClientAdd, self=g.wallet(), Type=g.currency(name), Amount=1, reason='Default')
                ask_write(g.get('DItem'), where(j), EMPTY, 'Common')
                g.setter('Sel', -1)
            g.either(g.math('GreaterEqual_IntInt', line_id, FIRST_GEM), give_back, lambda: None)

        g.either(g.math('EqualEqual_IntInt', line_id, SOCKET_ID), pick, take_out)
        g.setter('Busy', DEBOUNCE)
        g.loose = carry

    for kind, grade in all_gems():
        g.section()
        fired, carry = g.branch(pressed_test('Gem%s%d' % (kind, grade)))
        g.loose = fired
        owned = g.call(WalletComponent.Balance, self=g.wallet(), Type=g.currency(gem_id(kind, grade)))
        ready = g.both(g.math('GreaterEqual_IntInt', g.get('Sel'), 0),
                       g.math('GreaterEqual_IntInt', owned, 1),
                       g.valid(g.get('SelItem')),
                       g.call(KismetMathLibrary.EqualEqual_ObjectObject, A=g.get('SelItem'), B=g.get('DItem')),
                       g.math('EqualEqual_IntInt', g.id_at(g.props(g.get('SelItem')), g.get('SelIndex')), SOCKET_ID))

        def set_gem(kind=kind, grade=grade):
            g.run(WalletComponent.Deduct, self=g.wallet(), Type=g.currency(gem_id(kind, grade)), Amount=1)
            in_armour = g.math('EqualEqual_IntInt', g.call(KismetMathLibrary.Conv_ByteToInt,
                               InByte=g.call(InventoryItem.GetTag, self=g.get('SelItem'))), 12)   # EItemTag::Armor
            g.either(in_armour,
                     lambda: ask_write(g.get('SelItem'), g.get('SelIndex'), gem_line(kind, grade, True), RARITIES[grade - 1]),
                     lambda: ask_write(g.get('SelItem'), g.get('SelIndex'), gem_line(kind, grade), RARITIES[grade - 1]))
            g.setter('Sel', -1)

        g.either(ready, set_gem, lambda: None)
        g.setter('Busy', DEBOUNCE)
        g.loose = carry

    g.loose += closed
    ue.compile_blueprint(widget)
    keep(widget)
    say('dock built: ' + DOCK)
    return widget


def anchored(parent, child, x, y, left=0.0, top=0.0):
    """A child sized to its content, pinned by its own point (x, y) to the parent's point (x, y)."""
    slot = parent.AddChild(child)
    slot.LayoutData = AnchorData(
        Offsets=Margin(Left=left, Top=top, Right=0.0, Bottom=0.0),
        Anchors=Anchors(Minimum=Vector2D(X=x, Y=y), Maximum=Vector2D(X=x, Y=y)),
        Alignment=Vector2D(X=x, Y=y))
    slot.bAutoSize = True
    return slot


def socket_row(tree, pictures, prefix, ring, gem, pad):
    """
    A row of socket cells - a socket ring with a gem in it - that shrinks to the sockets shown, so a
    row pinned by its middle stays centred on one socket, two or three.
    """
    row = HorizontalBox(prefix + 'Row', tree)
    row.bIsVariable = True
    row.Visibility = 1
    for j in range(SOCKETS):
        cell = Overlay('%sSock%d' % (prefix, j), tree)
        cell.bIsVariable = True
        cell.Visibility = 1
        for part in (image(tree, '%sRing%d' % (prefix, j), pictures.get('T_Socket'), ring),
                     image(tree, '%sGem%d' % (prefix, j), pictures.get('T_Ruby1'), gem, variable=True)):
            placed = cell.AddChild(part)
            placed.HorizontalAlignment = 2
            placed.VerticalAlignment = 2
        slot = row.AddChild(cell)
        slot.Padding = Margin(Left=pad, Top=0.0, Right=pad, Bottom=0.0)
    return row


def build_strip(pictures, path, holders, chain, hovered_of):
    """
    An item tile's sockets, Diablo's way. Always: small socket rings in the tile's top right corner,
    each with its gem if one is set. On hover: the same, large, over the middle of the item. Nothing
    on an item without sockets. Reads its tile's item every frame, so a tile reused for another item,
    or a gem set on the Gems screen, shows at once.

    For any kind of tile: `holders` are the (variable, class) the attaching pass fills in, `chain`
    the steps from them to the item (each given the step before, the last is the item, every one
    checked valid before the next), and `hovered_of` where the tile says it is hovered.
    """
    there = on_disk(path, 'WidgetBlueprint')
    if there is not None:
        say('strip already there: ' + path)
        return there
    factory = WidgetBlueprintFactory()
    factory.ParentClass = UserWidget
    widget = factory.factory_create_new(path)
    widget.modify()
    tree = widget.WidgetTree
    tree.RootWidget.Visibility = 3                  # HitTestInvisible: the tile keeps its clicks
    anchored(tree.RootWidget, socket_row(tree, pictures, 'S', SMALL_RING, SMALL_GEM, SMALL_PAD), 1.0, 0.0, -6.0, 6.0)
    anchored(tree.RootWidget, socket_row(tree, pictures, 'B', BIG_RING, BIG_GEM, BIG_PAD), 0.5, 0.5)

    for name, kind in holders:
        typed_variable(widget, name, PinCategory='object', PinSubCategoryObject=kind)
    for name in ['N'] + ['Id%d' % j for j in range(SOCKETS)]:
        ue.blueprint_add_member_variable(widget, name, 'int', False, '0')
    pictures_variable(widget)
    ue.blueprint_mark_as_structurally_modified(widget)
    ue.compile_blueprint(widget)

    g = Graph(widget)
    tick = event(widget, UserWidget, 'Tick', 0, 0)
    g.loose = [(tick, 'then')]
    g.setter('N', 0)

    #Which lines are sockets, and what is in each: N of them, their ids in Id0..Id2.
    def through(step, before):
        here = lambda: chain[step](g, before)
        if step == len(chain) - 1:
            g.either(g.valid(here()), lambda: with_item(here), lambda: None)
        else:
            g.either(g.valid(here()), lambda: through(step + 1, here), lambda: None)

    def with_item(item):
        loop = g.for_each(g.props(item()))
        g.then(loop, 'Exec', 'Completed')
        after = list(g.loose)
        entry = g.breaks('ArmorPropertyData', (loop, 'Array Element'))
        line_id = g.call(KismetMathLibrary.Conv_ByteToInt, InByte=(entry, 'ID'))
        g.loose = [(loop, 'LoopBody')]
        take, skip = g.branch(g.both(g.is_socket(line_id), g.math('Less_IntInt', g.get('N'), SOCKETS)))
        g.loose = take
        for j in range(SOCKETS):
            g.either(g.math('EqualEqual_IntInt', g.get('N'), j),
                     lambda j=j: g.setter('Id%d' % j, line_id), lambda: None)
        g.setter('N', g.math('Add_IntInt', g.get('N'), 1))
        g.loose = after

    through(0, None)

    #Both rows show the same sockets; which row is on screen is the hover.
    for prefix in ('S', 'B'):
        for j in range(SOCKETS):
            g.either(g.math('Less_IntInt', j, g.get('N')),
                     lambda prefix=prefix, j=j: g.shows('%sSock%d' % (prefix, j), LOOKS),
                     lambda prefix=prefix, j=j: g.shows('%sSock%d' % (prefix, j), COLLAPSED))

            def gem_in(prefix=prefix, j=j):
                which = g.picture_of(g.get('Id%d' % j))
                g.run(Image.SetBrushFromTexture, self=g.get('%sGem%d' % (prefix, j)),
                      Texture=g.element(g.get('Pictures'), which), bMatchSize='false')
                g.shows('%sGem%d' % (prefix, j), LOOKS)

            g.either(g.math('GreaterEqual_IntInt', g.get('Id%d' % j), FIRST_GEM), gem_in,
                     lambda prefix=prefix, j=j: g.shows('%sGem%d' % (prefix, j), COLLAPSED))

    hovered = lambda: hovered_of(g)
    any_socket = lambda: g.math('Greater_IntInt', g.get('N'), 0)
    g.either(g.both(any_socket(), g.call(KismetMathLibrary.Not_PreBool, A=hovered())),
             lambda: g.shows('SRow', LOOKS), lambda: g.shows('SRow', COLLAPSED))
    g.either(g.both(any_socket(), hovered()),
             lambda: g.shows('BRow', LOOKS), lambda: g.shows('BRow', COLLAPSED))

    ue.compile_blueprint(widget)
    keep(widget)
    say('strip built: ' + path)
    return widget


def build_stall(pictures, actor_path=STALL_ACTOR, level_path=STALL_LEVEL, looks_path=STALL_LOOKS,
                sign_path=STALL_SIGN, words='GEM MERCHANT', at=STALL_WHERE, facing=STALL_FACING, sign_height=330.0,
                material=None):
    """
    The Gem Merchant's booth: an actor that draws the game's blue market booth where it stands, with
    a GEM MERCHANT sign over it the way the Camp names its own, and a Lobby level holding one.
    Any other prop of the game's, with words of its own over it, is the same thing with other arguments.
    `material`, when given, goes on the prop's first mesh once it is spawned - the game's prop in
    colours of our own.
    """
    there = on_disk(actor_path, 'Blueprint')
    if there is None:
        looks = on_disk(looks_path, 'Blueprint')
        if looks is None:
            looks = ue.create_blueprint(Actor, looks_path)      # a stand-in at the game's path; never ships
            ue.compile_blueprint(looks)
            keep(looks)
        sign = build_stall_sign(sign_path, words)

        actor = ue.create_blueprint(Actor, actor_path)
        page = actor.UberGraphPages[0]
        begin = event(actor, Actor, 'ReceiveBeginPlay', 0, 0)
        spawn = page.graph_add_node(K2Node_SpawnActorFromClass, 500, 0)
        spawn.node_find_pin('Class').default_object = looks.GeneratedClass
        mcd_ui.reconstruct(spawn)
        where = page.graph_add_node_call_function(Actor.GetTransform, 200, 200)
        link(where, 'ReturnValue', spawn, 'SpawnTransform')
        link(begin, 'then', spawn, 'execute')
        if material is not None:
            g = Graph(actor)
            g.x = 900
            g.loose = [(spawn, 'then')]
            found = g.call(Actor.GetComponentByClass, self=(spawn, 'ReturnValue'))
            found[0].node_find_pin('ComponentClass').default_object = StaticMeshComponent
            mcd_ui.reconstruct(found[0])
            mesh = g.cast(StaticMeshComponent, found)
            dressed = g.run(PrimitiveComponent.SetMaterial, self=mesh, ElementIndex=0)
            dressed.node_find_pin('Material').default_object = material

        #The sign: screen space, so it faces the camera at a constant size - build_sign.py's way.
        made = ue.add_component_to_blueprint(actor, WidgetComponent, 'Sign')
        made.WidgetClass = sign.GeneratedClass
        made.Space = 1                                           # EWidgetSpace::Screen
        made.DrawSize = IntPoint(X=512, Y=96)
        made.RelativeLocation = Vector(X=0.0, Y=0.0, Z=sign_height)
        try:
            made.bGenerateOverlapEvents = False
        except Exception:
            pass
        ue.compile_blueprint(actor)
        keep(actor)
        say('stall built: ' + actor_path)
        there = actor

    if on_disk(level_path, 'World') is None:
        world = WorldFactory().factory_create_new(level_path)
        x, y, z = at
        world.actor_spawn(there.GeneratedClass, ue.FVector(x, y, z), ue.FRotator(0.0, facing, 0.0))
        keep(world)
        say('stall level built: ' + level_path)
    return there


def build_stall_sign(path=STALL_SIGN, words='GEM MERCHANT'):
    """The words over the booth, in the Camp labels' own font."""
    there = on_disk(path, 'WidgetBlueprint')
    if there is not None:
        return there
    factory = WidgetBlueprintFactory()
    factory.ParentClass = UserWidget
    widget = factory.factory_create_new(path)
    widget.modify()
    tree = widget.WidgetTree
    #As build_sign.py does it: a root of its own, the words stretched over it, compiled before it is
    #kept - kept uncompiled, the class went out without the font and drew in the engine's default.
    root = CanvasPanel('Root', tree)
    tree.RootWidget = root
    root.Visibility = 3
    words = label(tree, 'Says', words, INK, stub_font(TITLE_FONT), 20, typeface=TITLE_FACE, outline=2)
    try:
        words.Justification = 1                                  # ETextJustify::Center
    except Exception:
        pass
    mcd_ui.stretch(root, words)
    ue.compile_blueprint(widget)
    keep(widget)
    say('stall sign built: ' + path)
    return widget


def build_text_stub(path, member):
    """A game widget whose one TextBlock the Gem Merchant rewrites, as far as that needs it. Never ships."""
    there = on_disk(path, 'WidgetBlueprint')
    if there is not None:
        say('stub already there: ' + path)
        return there
    factory = WidgetBlueprintFactory()
    factory.ParentClass = UserWidget
    stub = factory.factory_create_new(path)
    stub.modify()
    if member is not None:
        text = TextBlock(member, stub.WidgetTree)
        text.bIsVariable = True
        put(stub.WidgetTree.RootWidget, text, 0.0, 0.0, 300.0, 40.0)
    ue.blueprint_mark_as_structurally_modified(stub)
    ue.compile_blueprint(stub)
    keep(stub)
    say('stub built: ' + path)
    return stub


def build_generic_stub():
    """The game's generic slot widget, as far as the shop strip needs it. Never ships."""
    there = on_disk(GENERIC_STUB, 'WidgetBlueprint')
    if there is not None:
        say('generic stub already there: ' + GENERIC_STUB)
        return there
    factory = WidgetBlueprintFactory()
    factory.ParentClass = UserWidget
    stub = factory.factory_create_new(GENERIC_STUB)
    stub.modify()
    canvas = CanvasPanel('ContentPadder', stub.WidgetTree)
    canvas.bIsVariable = True
    put(stub.WidgetTree.RootWidget, canvas, 0.0, 0.0, 100.0, 100.0)
    ue.blueprint_add_member_variable(stub, 'Hovered', 'bool', False, 'false')
    ue.blueprint_mark_as_structurally_modified(stub)
    ue.compile_blueprint(stub)
    keep(stub)
    say('generic stub built: ' + GENERIC_STUB)
    return stub


def build_shop_tile_stub(sale_class, generic_class):
    """The game's shop tile - a sale tile with its generic slot widget. Never ships."""
    there = on_disk(SHOP_TILE_STUB, 'WidgetBlueprint')
    if there is not None:
        say('shop tile stub already there: ' + SHOP_TILE_STUB)
        return there
    factory = WidgetBlueprintFactory()
    factory.ParentClass = sale_class
    stub = factory.factory_create_new(SHOP_TILE_STUB)
    stub.modify()
    typed_variable(stub, 'UMG_GenericSlotWidget', PinCategory='object', PinSubCategoryObject=generic_class)
    ue.blueprint_mark_as_structurally_modified(stub)
    ue.compile_blueprint(stub)
    keep(stub)
    say('shop tile stub built: ' + SHOP_TILE_STUB)
    return stub


#--- the rest ----------------------------------------------------------------------------------------

def build_widget(pictures, tile_class, strip_class, bullet_class, inspector_class, dock_class, sale_class, shop,
                 stall_class):
    there = on_disk(WIDGET, 'WidgetBlueprint')
    if there is not None:
        say('widget already there: ' + WIDGET)
        return there
    factory = WidgetBlueprintFactory()
    factory.ParentClass = UserWidget
    widget = factory.factory_create_new(WIDGET)
    build_tree(widget, pictures)
    variables(widget)
    build_graph(widget, tile_class, strip_class, bullet_class, inspector_class, dock_class, sale_class, shop,
                stall_class)
    keep(widget)
    say('widget built: ' + WIDGET)
    return widget


def build_actor(widget):
    """Puts the screen up when its level loads. Nothing else."""
    there = on_disk(ACTOR, 'Blueprint')
    if there is not None:
        say('actor already there: ' + ACTOR)
        return there
    actor = ue.create_blueprint(Actor, ACTOR)
    page = actor.UberGraphPages[0]
    begin = event(actor, Actor, 'ReceiveBeginPlay', 0, 0)

    made = page.graph_add_node_call_function(WidgetBlueprintLibrary.Create, 400, 0)
    pin(made, 'WidgetType').default_object = widget.GeneratedClass
    controller = page.graph_add_node_call_function(GameplayStatics.GetPlayerController, 150, 200)
    link(controller, 'ReturnValue', made, 'OwningPlayer')
    link(begin, 'then', made, 'execute')

    shown = page.graph_add_node_call_function(UserWidget.AddToViewport, 800, 0)
    link(made, 'ReturnValue', shown, 'self')
    set_default(shown, 'ZOrder', '9999')
    link(made, 'then', shown, 'execute')

    #Its level unloaded, its screen goes too - or every level visited leaves one ticking, and one
    #left from a mission still thinks it is in one.
    ue.blueprint_add_member_variable(actor, 'Screen', EdGraphPinType(PinCategory='object', PinSubCategoryObject=widget.GeneratedClass), False, '')
    ue.blueprint_mark_as_structurally_modified(actor)
    ue.compile_blueprint(actor)
    ended = event(actor, Actor, 'ReceiveEndPlay', 0, 800)
    gone = page.graph_add_node_call_function(Widget.RemoveFromParent, 600, 800)
    link(page.graph_add_node_variable_get('Screen', None, 350, 900), 'Screen', gone, 'self')
    link(ended, 'then', gone, 'execute')

    #In a mission or in the Camp: the loader streams this level from /MCDReborn/Ingame/ in missions
    #only, so the actor's own path says which.
    screen = page.graph_add_node_dynamic_cast(widget.GeneratedClass, 1100, 0)
    link(made, 'ReturnValue', screen, 'Object')
    mcd_ui.reconstruct(screen)
    link(shown, 'then', screen, 'execute')
    where = page.graph_add_node_call_function(KismetSystemLibrary.GetPathName, 900, 300)
    self_node = page.graph_add_node(K2Node_Self, 700, 300)
    link(self_node, 'self', where, 'Object')
    ingame = page.graph_add_node_call_function(KismetStringLibrary.Contains, 1100, 300)
    link(where, 'ReturnValue', ingame, 'SearchIn')
    set_default(ingame, 'Substring', '/Ingame/')
    told_it = page.graph_add_node_variable_set('InMission', widget.GeneratedClass, 1400, 0)
    link(screen, mcd_ui.as_pin(screen), told_it, 'self')
    link(ingame, 'ReturnValue', told_it, 'InMission')
    link(screen, 'then', told_it, 'execute')
    kept = page.graph_add_node_variable_set('Screen', None, 1700, 0)
    link(screen, mcd_ui.as_pin(screen), kept, 'Screen')
    link(told_it, 'then', kept, 'execute')

    ue.compile_blueprint(actor)
    keep(actor)
    say('actor built: ' + ACTOR)
    return actor


def build_level(path, actor):
    if on_disk(path, 'World') is not None:
        say('level already there: ' + path)
        return
    world = WorldFactory().factory_create_new(path)
    world.actor_spawn(actor.GeneratedClass)
    keep(world)
    say('level built: ' + path)


def main():
    say('building the gems screen')
    pictures = import_pictures()
    tile = build_tile_stub()
    tile_class = tile.GeneratedClass
    strip = build_strip(pictures, STRIP, [('Tile', tile_class)],
                        [lambda g, _: g.get('InventoryItemSlot', tile_class, of=g.get('Tile')),
                         lambda g, before: g.get('Item', InventoryItemSlot, of=before())],
                        lambda g: g.get('Hovered', tile_class, of=g.get('Tile')))
    sale_class = build_sale_stub().GeneratedClass
    generic_class = build_generic_stub().GeneratedClass
    shop_tile_class = build_shop_tile_stub(sale_class, generic_class).GeneratedClass
    shop_strip = build_strip(pictures, SHOP_STRIP, [('Sale', sale_class), ('Holder', generic_class)],
                             [lambda g, _: g.get('merchantItemSlot', sale_class, of=g.get('Sale')),
                              lambda g, before: g.call(MerchantItemSlotBase.GetDisplayItemCache, self=before())],
                             lambda g: g.get('Hovered', generic_class, of=g.get('Holder')))
    bullet = build_bullet_stub()
    inspector = build_inspector_stub()
    dock = build_dock(pictures, inspector.GeneratedClass)
    stall = build_stall(pictures)
    widget = build_widget(pictures, tile.GeneratedClass, strip.GeneratedClass, bullet.GeneratedClass,
                          inspector.GeneratedClass, dock.GeneratedClass, sale_class,
                          (shop_tile_class, generic_class, shop_strip.GeneratedClass), stall.GeneratedClass)
    actor = build_actor(widget)
    for path in LEVELS:
        build_level(path, actor)
    say('done - cook, then carry the levels, the actor, the screen and the pictures')


#Run as a script it builds; imported (build_talents.py borrows the graph vocabulary) it only defines.
if __name__ == '__main__':
    main()
