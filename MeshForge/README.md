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

**2. Read the geometry. Half done.** The tagged property list at the front of the export reads,
and with it the bounds - which is the half that has to be corrected whenever geometry changes, or
the weapon is culled and turns invisible. On the vanilla Claymore:

```
ExtendedBounds:
  Origin      = (0, -50.82, 0)
  BoxExtent   = (26.52, 90.6, 3.13)
  SphereRadius = 90.76
```

Which is a claymore: about 181 units long, 53 across, 6 thick. A blade. Numbers that describe the
right shape are the evidence that the list is being read correctly rather than plausibly.

The offset origin is also the first sight of the pivot problem - the mesh does not sit centred on
its own handle, so a model imported centred on nothing in particular will be held by the wrong end.

What is left of this step is the untagged half: `FStaticMeshRenderData` in the `.uexp`, where the
vertices actually are.

**3. Write the geometry back unchanged.** Re-serialise what was just read and get a byte identical
file. Until this holds, nothing written by hand can be trusted.

**4. Change one number.** Move a single vertex, or scale every one by a half, and see it in game.
This is the first point at which anything is proven.

**5. Import a model.** Only now does reading a `.obj` matter, along with the two problems that come
with it: the pivot, so the weapon is held by the handle rather than the blade, and the scale.

Step 2 is now the one that decides whether the rest is possible.

## Commands

```
find <text>                   asset paths containing that text
dump <asset path>             what the cooked asset is made of
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
