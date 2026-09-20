# Custom maps

Building Minecraft Dungeons levels in Minecraft.

Nothing you do here modifies the game's own files. A map installs as a mod pak beside them, and
**Remove** deletes that pak and gives the original mission back.

## What you need

| | |
|---|---|
| **MCD Reborn** | the Maps tab |
| **MCD-MapTools** | `C:\Users\<you>\MCD-MapTools` — carries its own Python, nothing to install |
| **Minecraft Java** | with a **1.16.2** profile (see [Versions](#versions)) |

If the tools folder is missing the two Minecraft buttons grey out and the tab says where it should
be.

## Versions

The converter writes worlds as **Minecraft 1.16.2**, and that is what you should open them with.

Opening one in a newer Minecraft upgrades it irreversibly — 1.17 changed world height from 0–255 to
−64–319 — and how such a world converts back has never been tested.

## Building Map 

1. **Maps tab** → pick a mission.
2. **Edit in Minecraft…** — click this to export the mission. Then it will appear in your Minecraft world list as one
   connected run of rooms you can walk start to end.
3. Open it with a **1.16.2** profile, and build.
4. **Bring back…** — pick that world. Your blocks are written back into the tiles they came from
   and installed over the mission you have selected.
5. Play it. **Remove** puts the original mission back.

### The buttons

**Edit in Minecraft…** is the one you want. It does two things first: it *pins* the level so
it builds the same way every time, then lays the rooms out in playing order, joined at their doors.

Pinning matters more than it sounds. A mission is normally assembled from a random choice of tiles,
in a random number of rooms, with random side-paths — so the room you edited might not even appear
in a given run. Pinning gives each stretch exactly one room, no random count, no branches. The
original level is kept beside it as `level.json.random`.

**Export map** writes the mission out as plain files and installs nothing. It does not ask where to
put them; the app keeps a folder per mission and every other button looks in the same place. The
folder is emptied before it is written, so an old export can never leave a file behind in a new one.
**Import map…** takes such a folder back, including one somebody else made. **Clear** deletes the
working folder so the next export starts from the game's own files again.

## Placing mobs

1. **Maps tab** → pick a mission.
2. **Export map** — pulls the mission out of the game's paks into a folder you can change. The
   folder is emptied first, so nothing from a previous export is left in it. Skip this and
   **Edit spawns…** will do it for you, but only when there is nothing there yet — it never
   throws away a folder you have already edited.
3. **Edit spawns…** — opens the mission in 3D, in its own block colours. It pins and welds first,
   so what you are editing is what gets installed.
4. Click the ground to aim, then **Place**. Or double-click to drop them straight away. `radius`
   and `count` say how far they scatter and how many. Each one lands on the floor beneath the
   spot you chose.
5. Click an existing point to pick it out — it turns gold — and **Remove point** takes that one
   out. **Clear room** removes every one in the mission.
6. **What spawns** decides *which* mobs turn up. Choose a group marked **roams the level** and
   edit its list. **Add mob** appends one; the dropdowns replace the mob on that row; **×**
   removes it.
7. **Save and install** — saves, rebuilds the mission, and installs it over the game's own.
8. Play it. **Remove** in the Maps tab puts the original mission back. **Clear** throws away the
   working folder, so the next export starts from the game's own files again.

Moving about: drag to turn, **WASD** or shift-drag to move, **Q**/**E** down and up, wheel to
zoom, **R** to refit, **Top down** for the angle the game plays at. Click the map once so the keys
reach it. The slider takes the roof off above a height, so you can see inside buildings.

Ground the game will let something stand on is lit up; everything else is dimmed. The readout in
the corner says `walkable` or `not walkable` for whatever is under the pointer — a spawn point on
ground the game calls unwalkable is a mob it will not place.

Two separate things decide what you fight:

* **Spawn points** — *where* and *how many*. Regions of type `spawn` inside a tile.
* **Mob groups** — *what*. Named lists of mobs in `level.json`. Bosses are ordinary entries here;
  ★ marks them.

### Three reasons an edit does nothing

All three are silent. The file is right, the install is right, and the game quietly ignores you.

**1. The group is not used.** Creeper Woods ships 23 mob groups and most of them never spawn
anything as you walk around. What roams the level is `default-mobs`:

```json
"default-mobs": { "only": [
    { "id": "lowcomplexity-group-diff1", "weight": 2 },
    { "id": "cw-mix", "weight": 3 },
    "zombie-horde", "animals", "caravan-illagers"
  ], "density": 1 }
```

Everything else is an arena group, named by a trigger's `waves`, and fires only inside that fight —
`enderboss` is the Deepwood Brook endersent, and editing it changes nothing until you walk in
there. No stretch of Creeper Woods has its own `mob-groups`, before or after welding.

The group dropdown says which is which: *roams the level, weight 3* against *8 arena wave(s) only*
against *not used by this mission*. Roaming groups sort to the top.

**2. The group is gated to a difficulty.** This is the quietest one:

```json
{ "id": "lowcomplexity-group-diff1", "max-difficulty": 1, "pick-types": 1, "types": [ ... ] }
```

`max-difficulty: 1` means the game only picks this group on difficulty 1. Put a boss in it, play at
anything higher, and the game skips the group, falls through to the others, and hands you the
zombie horde. Individual mobs carry the same gates — `cw-mix` keeps its wraith behind
`min-difficulty: 2` and its necromancer behind 3. Both are now shown beside the group and beside
the mob.

**3. Weight and `pick-types`.** A group picks `pick-types` kinds per spawn — `[2, 3]` means two or
three — and `weight` decides how often each is chosen. One entry at weight 1 against seven others
will turn up, but not often. Raise the weight if you want it everywhere.

So: **to change what you fight throughout a mission, edit a roaming group with no difficulty cap.**
In Creeper Woods that is `cw-mix`.

### What gets written

Spawn points go to `objectgroups\<Name>\objectgroup.json`; mob groups go to `level.json`, and to
`level.json.multitile` beside it when that exists, so the next weld does not throw them away. The
first time a file is touched the original is kept as `<name>.before`.


## Which blocks work

The converter maps Java blocks onto Dungeons blocks through a table of **1,040 mappings covering
344 distinct Java blocks**. A block in the table converts. A block that is not in the table
**becomes air**, and the converter prints a warning naming it.

The table lives at `MCD-MapTools\tools\BlockMap.py` and you can add to it.

### Rule of thumb

**Build with Minecraft 1.16 blocks and you will be fine. Everything added in 1.17 or later is
missing.**

That is not a coincidence — Dungeons is built on a 1.16-era block set, so those blocks have no
Dungeons equivalent to map onto.

### Families that work completely

- **All 16 colours** of wool, carpet, concrete, terracotta
- **Stone family** — stone, granite, diorite, andesite and their polished forms, cobblestone,
  mossy cobblestone, stone bricks (plain, mossy, cracked, chiseled)
- **Blackstone family** — blackstone, polished, bricks, cracked, chiseled, gilded, basalt,
  polished basalt, ancient debris, netherite, lodestone, crying obsidian
- **Wood** — oak, spruce, birch, jungle, acacia, dark oak, crimson, warped: planks, logs, wood,
  leaves, saplings, fences, fence gates, doors, stairs, slabs
- **Nether and End** — netherrack, nether bricks, soul sand, crimson and warped nylium, warped
  wart block, quartz in every form
- **Shapes** — 30 slabs, 18 walls, 13 stairs, trapdoors, buttons, pressure plates
- **Ores** — coal, iron, gold, diamond, emerald, lapis, redstone, quartz, nether gold

### Individual blocks that work

```
air, allium, ancient_debris, andesite, azure_bluet, basalt, blackstone, blue_orchid,
bookshelf, brewing_stand, bricks, cactus, carrots, carved_pumpkin, cauldron, chest,
chiseled_polished_blackstone, chiseled_quartz_block, chiseled_stone_bricks, clay,
coal_block, coarse_dirt, cobblestone, cobweb, cocoa, comparator,
cracked_polished_blackstone_bricks, cracked_stone_bricks, crafting_table, crimson_nylium,
crimson_stem, crying_obsidian, dandelion, diamond_block, diorite, dirt, dirt_path,
dispenser, dried_kelp_block, dropper, emerald_block, enchanting_table, end_portal_frame,
farmland, fern, fire, furnace, gilded_blackstone, glowstone, gold_block, granite, grass,
grass_block, gravel, hay_block, honeycomb_block, ice, infested_chiseled_stone_bricks,
iron_bars, iron_block, jack_o_lantern, ladder, lapis_block, large_fern, lava, lilac,
lily_of_the_valley, lily_pad, lodestone, melon, melon_stem, mossy_cobblestone,
mossy_stone_bricks, mycelium, nether_bricks, netherite_block, netherrack, note_block,
observer, obsidian, orange_tulip, oxeye_daisy, packed_ice, peony, pink_tulip, podzol,
polished_andesite, polished_basalt, polished_blackstone, polished_blackstone_bricks,
polished_diorite, polished_granite, poppy, pumpkin_stem, quartz_block, quartz_bricks,
quartz_pillar, red_sand, red_tulip, redstone_block, redstone_lamp, redstone_torch,
redstone_wire, repeater, rose_bush, sand, sea_lantern, skeleton_skull, smooth_quartz,
snow, snow_block, soul_sand, spawner, stone, stone_bricks, stonecutter, sugar_cane,
tall_grass, tnt, torch, vine, warped_nylium, warped_wart_block, water, wheat, white_tulip
```

Plus glass, glass panes, iron bars, rails, anvils, sponge, sticky piston, mushrooms and mushroom
blocks, dead coral blocks, sandstone and red sandstone in all their cut and smooth forms.

### Blocks that do NOT work

Anything from **1.17 onwards**: deepslate and all its variants, copper, amethyst, calcite, tuff,
dripstone, moss, azalea, sculk, froglight, mud, candles, mangrove, cherry, bamboo blocks.

Also missing despite being old enough: **lantern, beacon, end stone, purpur, prismarine, magma
block**. The table is partial rather than a clean version cutoff, so if a block matters to you,
check `BlockMap.py` before building a hundred of them.

### Blocks with a special meaning

These four are not scenery. The converter reads them and turns them into level structure:

| Block | Becomes |
|---|---|
| `barrier` | a **boundary** — an invisible wall players cannot pass |
| `player_head` | a **player spawn** |
| `structure_block` named `door:entrance` | a **door** |
| `structure_block` named `region:arena1` | a **region** |

For a region's type and tags, set the structure block's *data* to JSON:
`{ "type": "trigger", "tags": "death" }`. Name and size come from the structure block's own
fields, editable in SAVE mode; the data field is editable in DATA mode.

`air` and `cave_air` are skipped entirely, which is what makes conversion fast.

## What survives the trip

Measured on Creeper Woods: **11,534,265 non-air blocks, 1,879 with no Java equivalent - 0.02%.**

| | |
|---|---|
| **Block ids** | everything in the block map survives |
| **Block metadata** | 100% on every non-air block |
| **Doors** | kept |
| **Regions and boundaries** | kept **from the original**, not read back |

Of 93 distinct block ids in the mission, exactly **one** has no mapping: an undocumented `id 223`,
1,877 blocks of it. Anything unmapped becomes air, and the converter names it as it goes.

Adding a block to the map is one line in `MCD-MapTools	ools\BlockMap.py`:

```python
{ 'dungeons': [223, 0], 'java': [ 'minecraft:some_block_you_never_use' ] }
```

Pick a Java block you would not otherwise place and it becomes a stand-in: it goes out to
Minecraft as that block and comes back as the Dungeons one.

Regions travel into Minecraft as structure blocks, and a structure block needs a free air cell
inside the region's footprint to sit in - which a packed dungeon often has not got. Measured on one
mission, doors came back 5 of 5 but regions only 3 of 8. So geometry comes from Minecraft and the
gameplay markup stays as it was. This means **moving a trigger region by moving its structure block
will not take effect**; to change regions, edit the object group's JSON.

**Mob spawns and bosses are not blocks.** They are regions with `"type": "spawn"` plus entries in
the level's `mob-groups`, so placing a Minecraft spawner will not create a Dungeons spawn.

Two cosmetic quirks: going *back* to Minecraft, fences, walls and stairs are not re-connected, so
they can look wrong while you build. The block and its metadata are both intact - it only affects
how Minecraft draws them.

## Seams, and why the level is welded

The game assembles a mission from tiles at run time and picks its own doors. So a structure built
**across a seam** would line up in Minecraft and not in game.

**Bring back** therefore welds the whole level into **one tile** before installing it. A level made
of one tile has no joins for the generator to choose, so what you built is what you play.

This is not a refinement, it is what makes the feature work at all. Pinning a level to one room
per stretch still leaves the generator a chain to connect, and with one forced tile each there may
be no arrangement whose doors meet. Creeper Woods crashed on the loading screen **every time**,
whichever tiles were chosen, with side-paths kept or removed and with door matching on or off.
Welded, it loads. Lower Temple survived pinning only by luck - its forced tiles happened to fit.

Creeper Woods welds to a single **1034 x 69 x 314** tile: 22.4 million blocks, 542 KB compressed,
because 83% of the bounding box is air. It takes about two seconds.

A welded tile must still look like a tile. Getting this wrong crashed the game twice, and the
causes are worth knowing if you write your own:

- **it must keep doors.** A tile with none is not something the generator can place.
- **it must carry `walkable-plane`, `y`, `tags`, `locked`, `is-leaky`** - every real tile has them.
- **`region-y-plane` is not a copy of `height-plane`.** The community library defaults to copying
  it; the game's own `trial_start002` has it exactly one *less*, in every column.
- **boundaries are big-endian unsigned 16-bit** - `x = bytes[0] << 8 | bytes[1]`. Reading them
  little-endian signed produced values like `x = 32512` and `height = 8704` for a tile 100 blocks
  across, every one a multiple of 256. The game loads such a tile without complaint; the invisible
  walls are simply in the wrong place. Sanity-check the numbers against the tile's own size.

`make_single.py` does the welding, and `--rooms=N` welds only the first N if you need to find out
where a limit is.

## Where things live

```
%LOCALAPPDATA%\MCDReborn\maps\<mission>      the working folder
    level.json                               the mission: rooms, order, objectives, mob groups
    level.json.random                        the original, before pinning
    level.json.multitile                     the pinned level, before welding
    objectgroups\<Name>\objectgroup.json     the tiles, and the spawn points in them
    <any of the above>.before                kept the first time that file is changed
    resourcepacks\<Name>\blocks.json         what each block id means
%APPDATA%\.minecraft\saves\                  the converted worlds
<game>\Dungeons\Content\Paks\MCDReborn_Map_*.pak    what is installed
C:\Users\<you>\MCD-MapTools\                 the converter
```
