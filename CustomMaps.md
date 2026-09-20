# Custom maps

Building Minecraft Dungeons levels in Minecraft.

Nothing you do here modifies the game's own files. A map installs as a mod pak beside them, and
**Remove** deletes that pak and gives the original mission back.

## What you need

| | |
|---|---|
| **MCD Reborn** | the Maps tab |
| **Minecraft Java** | any version |
| **Chunker** | one jar, see [Setting up Chunker](#setting-up-chunker) |
| **Java 17 or newer** | what runs Chunker - [Adoptium](https://adoptium.net) if you have not got it |

If the tools folder is missing the two Minecraft buttons grey out and the tab says where it should
be.

### Setting up Chunker

[Chunker](https://github.com/HiveGamesOSS/Chunker) converts Minecraft worlds between versions.

1. Open the [latest release](https://github.com/HiveGamesOSS/Chunker/releases/latest). **Always
   take the newest one**
2. Under **Assets**, download **`chunker-cli-<version>.jar`** — the plain jar, about 30 MB.
   *Not* the installers: the `.exe`, `.zip`, `.deb`, `.dmg` and `.AppImage` files are the desktop
   app and will not work here.
3. Create a `chunker` folder inside MCD-MapTools, and **rename the jar to `chunker-cli.jar`** —
   drop the version number, so the app does not have to guess it:

```
C:\Users\<you>\MCD-MapTools\chunker\chunker-cli.jar
```

So `chunker-cli-1.20.0.jar` becomes `chunker-cli.jar`, and so does `chunker-cli-1.25.0.jar` when
that comes along. **Updating later is the same three steps** — download the newest, rename it, drop
it over the old one. The app needs no change and no new release; it asks the jar what it can do
rather than assuming.

### Which Minecraft to build in

Whichever you like. A world is handed over exactly as it was written — a 1.16.2 one — and
**Minecraft upgrades it on open**.

You will see "this world was made in an older version" once. Say yes. The upgrade is irreversible
and that costs nothing, because coming home converts back down anyway.

## Building Map 

1. **Maps tab** → pick a mission.
2. **Edit in Minecraft…** — click this to export the mission. Then it will appear in your Minecraft world list as one
   connected run of rooms you can walk start to end.
3. Open it in Minecraft and build. It arrives as a **26.3** world, so current Litematica and
   current mods work.
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
   removes it. **New group** makes one from nothing and sets it roaming — the camp ships with no
   mob groups at all, because nothing is meant to spawn there, so spawn points in it draw from
   nothing until you make one.
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
