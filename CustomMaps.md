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

1. **Maps tab** → pick the mission your map will replace.
2. Either **Edit in Minecraft…** to start from that mission - it pulls it out of the game and lays
   its rooms out in playing order, one connected run you can walk start to end - or **New empty
   map…** to start from nothing but a platform.
3. Open it in Minecraft and build. It is handed over as a 1.16.2 world and Minecraft upgrades it
   the first time you open it, so current Litematica and current mods work. Say yes to the
   "made in an older version" prompt; the upgrade costs nothing, because coming home converts
   back down anyway.
4. **Bring back…** — pick that world. Your blocks are written back into the tiles they came from
   and installed over the mission you selected. Build past the edge of the platform and the tile
   grows to cover it, in every direction, so nothing is lost sideways. **Height is different**:
   anything above 69 blocks is left behind, because the game will not load a tile taller than
   that. See [How tall you can build](#how-tall-you-can-build).
5. Play it. **Remove** puts the original mission back.

There is no export step, and nothing to press between building and playing. **Bring back…**
installs as well as reads, so after it the map is already in the game.

### How tall you can build

**69 blocks.** Anything above that is not brought back, and the import tells you, with the number,
when it happens.

This is a limit of the game rather than of the conversion. A tile 112 blocks tall crashes Minecraft
Dungeons on load - every time, with no error anywhere - on a map whose level file, block ids,
planes and resource pack all matched a known-working custom mission field for field. The same map
at 69 loads and plays.

69 is the height of the whole of Creeper Woods once the game welds it out of its own tiles, which
makes it the tallest thing in that mission known to work. For scale: none of Creeper Woods' own 92
tiles is taller than 44, and Blossoming Isles - a custom mission that works - tops out at 60.

Somewhere between 69 and 112 is a real ceiling that nobody has measured. To go looking for it:

```
env\Scripts\python.exe from_minecraft_level.py "<your world>" --max-height 96
```

`--max-height 0` removes the limit altogether, which is how to crash the game on purpose.

What counts against the 69 is the floor of your build, not sea level. Put a tower on a hill and the
hill is part of the budget.

### The buttons

**Edit in Minecraft…** is the one you want. It does two things first: it *pins* the level so
it builds the same way every time, then lays the rooms out in playing order, joined at their doors.

Pinning matters more than it sounds. A mission is normally assembled from a random choice of tiles,
in a random number of rooms, with random side-paths — so the room you edited might not even appear
in a given run. Pinning gives each stretch exactly one room, no random count, no branches. The
original level is kept beside it as `level.json.random`.

**New empty map…** starts from nothing instead: a 30x30 platform with a place to arrive and a gate
to leave by, and none of the objectives the mission it replaces came with. The platform is a place
to stand while you work out where things go, not a budget - build past its edge and the tile grows
to meet you when you bring the world home.

**Import map…** takes a map folder back into the game, including one somebody else made. **Clear**
deletes the working folder, so the next **Edit in Minecraft…** or **Edit spawns…** starts from the
game's own files again.

There is no Export button. Both of the buttons that need a folder make one for themselves when
there is nothing there, and pressing an export on top of a map that came back from Minecraft threw
it away - which is a bad trade for a button that saved nobody a click. To get back to the game's
version of a mission: **Remove** puts the original back in the game, and **Clear** throws away the
working folder.

## Placing mobs

1. **Maps tab** → pick a mission.
2. **Edit spawns…** — opens the mission in 3D, in its own block colours. It pulls the mission out
   of the game's paks for you when there is nothing there yet, and never throws away a folder you
   have already edited. It pins and welds first, so what you are editing is what gets installed.

   Coming back from Minecraft, go straight here: **Bring back…** has already put your map in the
   folder and installed it.
3. Click the ground to aim, then **Place**. Or double-click to drop them straight away. `radius`
   and `count` say how far they scatter and how many. Each one lands on the floor beneath the
   spot you chose.
4. Click an existing point to pick it out — it turns gold — and **Remove point** takes that one
   out. **Clear room** removes every one in the mission.
5. **What spawns** decides *which* mobs turn up. Choose a group marked **roams the level** and
   edit its list. **Add mob** appends one; the dropdowns replace the mob on that row; **×**
   removes it. **New group** makes one from nothing and sets it roaming — the camp ships with no
   mob groups at all, because nothing is meant to spawn there, so spawn points in it draw from
   nothing until you make one.
6. **Save and install** — saves, rebuilds the mission, and installs it over the game's own.
7. Play it. **Remove** in the Maps tab puts the original mission back. **Clear** throws away the
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

## Objectives, and gates that open

The **Objectives** tab in Edit spawns holds the mission's chain: what it asks of you, in order.
A new map has one step in it - *exit through the gate* - and that is the whole mission.

The chain is a **sequence**, not a set. A step nobody can finish blocks every step behind it,
including the way out, and the symptom is a gate that draws, lights up and does nothing with no
error anywhere. That is also why every new step goes in **ahead** of the exit: clicking the exit
ends the mission, so a step behind it would never be asked for.

### Adding a step

**Add a step** makes one of two kinds where the map is aimed. Two dropdowns above them choose
what it says - and they are dropdowns rather than boxes you type in, because **an objective's
text is not text**. It is a key into one of the game's 36 mission string tables, picked by your
level's `loctable-id`. Type your own sentence in there and the mission banner reads
`<MISSING STRING TABLE ENTRY>`, with nothing in any log to say why.

So the editor offers that table's real wording, read straight out of the game - 26 entries for a
map installed over Creeper Woods, things like *Escape Creeper Woods*, *Find the Gold Key*,
*Free the Villagers*, *Survive The Fight*. The chain marks any step whose wording the table has
not got, which is how you spot one made before this existed; remove it and add it again.

The tables share almost nothing - the single most widely held key in the game is in five of the
36 - so installing the same folder over a different mission changes which wording is available.
The hint above the pickers always names the table in use.

The two kinds:

* **Click a thing** - a bell, a lever, a beacon. The game draws it there and you walk up and use
  it. Pick which from the dropdown; they are all base-game prefabs, so nobody needs any DLC.
* **Reach a spot** - a 5x5 patch of floor. Walk into it and the step is done.

Each one leaves an **amber pin** on the map, listed under *The spots those steps use*. Drag the
pin to move it, exactly like the other five kinds. A step whose spot is missing says so in that
list - that is the one thing that silently kills a mission.

### Gates

A **gate** is a wall that stays shut until some step is finished. Put one down with **Put a gate
here**, then **Turn** it so it lies *across* the way through rather than along it, and **Wider** /
**Narrower** to fit the gap. The taller post marks the end it is anchored at, which is the cell a
drag moves.

Then pick the gate, choose a step under **Opened by**, choose a look under **Drawn as**, and press
**This opens it**. **Nothing opens it** takes the wire off again. Picking either a gate or a step
draws a line between them, so you can find the other end on a map where they are nowhere near each
other.

**Drawn as** is not decoration. A gate with no look still blocks you - invisibly - which in game
is indistinguishable from the map being broken. The list stretches to whatever size the gate is, so
there is nothing to match up: 187 of the game's own 374 held gates name no prefab either, but those
are doorways whose tile already has a door built out of blocks, and a gate you carved into your own
map has none.

The look belongs to the **step**, not the gate - that is the game's shape, not a shortcut - so every
gate the same step opens is drawn the same way. The list marks any held gate with no look as
`INVISIBLE`.

**Only a step that asks you to click something can open a gate.** This is the game's rule, not the
editor's: across all fifty-six of its own missions, eighty-five objectives hold a gate shut, and
every one of them is a click. Not one is a *reach*. So *reach* steps are not offered in that
dropdown - if they were, the field would be written, ignored, and you would have a solid gate that
opens for nobody.

The way out is not offered either, although it *is* a click. A gate held shut by the exit opens at
the moment the mission ends.

So the smallest working lock is: **add a click step, put a gate somewhere else, and wire the two
together.** If the dropdown is empty, that is the missing half - there is no click step yet.

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
