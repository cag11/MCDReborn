# MeshForge

A workbench for getting custom weapon meshes into Minecraft Dungeons without needing Unreal
Engine installed.

Not part of the editor. It is a console tool meant to be run, printed at, and changed between
attempts, which is not how a tab behaves. If the geometry problem is ever solved here, the result
moves into the app as a tab; until then this stays a workbench.

## What is already known

Taken from a mod that works. **Sabers** replaces the Claymore and the Heartstealer with
lightsabers, and unpacking it shows exactly how much a weapon mod is:

| What it replaces | Example |
| --- | --- |
| The mesh | `SM_Claymore`, 2.5 KB header beside a 14 KB `.uexp` of geometry |
| A second mesh | `SM_Claymore_new` |
| Materials | `MI_Claymore`, `MI_Claymore_beacon`, `MI_Claymore_glass` |
| Textures | `T_Claymore`, its icon, its inventory icon, its specular |
| Animations | `Player_Master_Idle_Claymore`, `..._Run_Claymore`, the combo |
| Sound | eight `SoundWave` assets and nine `SoundCue` assets |
| Text | the whole `Localization/Game/en/Game` locres, to rename the weapon |

Every one of those is a **complete cooked asset**, not a patched one. That mod was built by
cooking real assets in Unreal with the community's Mod Kit, and the game loads them out of `~mods`
without complaint.

So the game is not the obstacle. It will take a new mesh at a path it already knows. The obstacle
is building a valid cooked `StaticMesh` without Unreal, which means writing the cooked byte layout
by hand: per-LOD sections, position buffer stride, packed tangents, UV precision, 16 or 32 bit
index buffers, and bounds.

Two facts that shape the work:

- **PakReader has no mesh support.** It parses `UTexture2D`, `USoundWave`, `UDataTable`,
  `UCurveTable`, `UStringTable` and `UFontFace`. Nothing else. Geometry is unexplored ground here.
- **UAssetAPI will not hand over a vertex buffer either.** For a cooked export it cannot model it
  gives back raw bytes. It is useful for re-serialising a package consistently, not for
  understanding this one.

## The plan, and why it starts where it does

**1. Prove the loop, before anything else. DONE.** The Claymore was taken out of the game, packed
back unchanged, loaded from `~mods`, and looked exactly as it always did. The game accepts a pak
written by this code, so packing is not the problem and the geometry is the only thing left.

Repeat it on any asset with:

```
dotnet run --project MeshForge -- roundtrip "/Dungeons/Content/Actors/Equipment/MeleeWeapons/Claymore/SM_Claymore" out.pak
```

Drop the result in `Dungeons/Content/Paks/~mods` and start the game. The weapon looking exactly
as it always did is the answer being looked for; a crash or an invisible weapon would have meant
packing was the problem rather than the geometry.

**2. Read the geometry. DONE.** Both halves read. The tagged property list at the front gives the
bounds - the half that has to be corrected whenever geometry changes, or the weapon is culled and
turns invisible - and the untagged `FStaticMeshRenderData` behind it now gives up its vertices and
triangles.

Not by walking the layout. Those are raw structs written in the order the engine reads them, so
walking from the front means getting every field of every version check right before anything can
be found at all. The buffers are found by their contents instead, and the export's own properties
are the answer key: the real position buffer is the one whose bounding box reproduces the declared
`ExtendedBounds`. On the vanilla Claymore:

```
674 vertices, data 1,441..9,529
  spans (-26.516, -141.421, -3.125) to (26.517, 39.775, 3.125)
  which is the declared box to within 0 units

504 triangles - the mesh
504 triangles - a reversed copy, for mirrored meshes
504 triangles - a depth only copy, for shadows
504 triangles - a depth only copy, for shadows
6,048 indices - adjacency, 12 indices per triangle, for tessellation

The triangles use all 674 vertices and none beyond them, which agrees with the header.
```

Three independent facts about one file, in agreement: the header says 674, the box says 674
vertices' worth of space, and the triangles reach vertex 673 while touching exactly 674 distinct
ones. That agreement is the evidence - a reader that is merely plausible does not produce it.

Checked across the whole game rather than one weapon, with `survey`:

```
2,730 matching assets
  not meshes      130      (SM_ is also SoundMix and Skeleton)
  cross check ok  2600
  cross check off    0

  2,536,080 vertices and 1,442,207 triangles read in total
  worst disagreement with the declared bounds: 0.00081 units
```

Two things worth knowing, because both cost an afternoon:

- **Index data is an array of bytes, not of indices.** A header count of 3,024 is 1,512 indices and
  504 triangles.
- **Nothing is aligned.** The Claymore's indices start on an odd byte. Scanning every second or
  fourth byte finds no geometry at all, which is what the first few passes concluded.

The offset origin is the first sight of the pivot problem: the mesh does not sit centred on its own
handle, so a model imported centred on nothing in particular gets held by the wrong end.

Still unread is the stretch between the positions and the indices - 10,832 bytes on the Claymore of
packed tangents and UVs, none of it float, so none of it findable the same way. Step three needs it.

**3. Write the geometry back unchanged.** Re-serialise what was just read and get a byte identical
file. Until this holds, nothing written by hand can be trusted.

**4. Change one number.** Move a single vertex, or scale every one by a half, and see it in game.
This is the first point at which anything is proven.

**5. Import a model.** Only now does reading a `.obj` matter, along with the two problems that come
with it: the pivot, so the weapon is held by the handle rather than the blade, and the scale.

Step 3 is now the one that decides whether the rest is possible.

## Commands

```
find <text>                   asset paths containing that text
dump <asset path>             what the cooked asset is made of
geometry <asset path>         find the vertices and triangles in the cooked mesh
survey <text>                 read the geometry of every matching mesh, and tally it
extract <asset> <folder>      write the raw .uasset and .uexp out to look at
roundtrip <asset> <out.pak>   pack it back unchanged, to prove the loop
```

The game folder is read from the editor's own setting, so opening the editor once is enough.
`--paks <folder>` overrides it.

## What it reads today

`dump` on the vanilla Claymore:

```
uasset 2,389 bytes   uexp 44,787 bytes
names 64 at 193, exports 3 at 2,017, imports 10 at 1,737
names: StaticMesh, BodySetup, BoxSphereBounds, ExtendedBounds,
       StaticMaterials, MaterialInstanceConstant, ...
```

The summary parses and its offsets agree with the file, which is the footing the export table
needs before anything is rewritten.
