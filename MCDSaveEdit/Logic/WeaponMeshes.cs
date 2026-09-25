using MCDSaveEdit.Services;
using PakReader.Pak;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Windows.Media.Imaging;
#nullable enable

namespace MCDSaveEdit.Logic
{
    /// <summary>
    /// The weapons and armour pieces whose shape can be changed, and the changing of it.
    ///
    /// This is the reshaping half of custom weapons, and it is worth being clear about which half
    /// that is. A mesh here is always the game's own: its vertices get moved, scaled or turned,
    /// and everything else in the file - the packed tangents, the UVs, the materials - is carried
    /// across untouched. Nothing new is modelled and no vertex is added, because adding one moves
    /// every byte after it and that needs a writer for a format nothing here can write yet.
    ///
    /// What that buys is real anyway: a claymore at half size, a dagger the length of a spear, a
    /// bow turned on its side. And the pak it produces is the ordinary kind, sitting beside the
    /// game's own with a file at the same asset path, so deleting it is the whole of undo.
    /// </summary>
    public static class WeaponMeshes
    {
        public enum Category { Melee, Ranged, Armor, Projectile }

        /// <summary>The headings the list can be narrowed to.</summary>
        public const string WEAPONS = "Weapons";
        public const string PROJECTILES = "Projectiles";
        public const string BOWS = "Bows and crossbows";

        /// <summary>
        /// Where the meshes that can be imported onto live.
        ///
        /// Melee only, for now, and the two that are missing are missing for reasons rather than
        /// for want of time:
        ///
        /// - **Ranged weapons are animated.** A bow is not one static shape - the game drives it
        ///   through draw states, and the mesh a new model would replace is only one of them. Swap
        ///   that one and the weapon changes shape halfway through being fired.
        /// - **Armour comes in sets.** A single piece is several meshes - helmet, shoulders, arms,
        ///   legs - that have to agree with each other and with the body underneath. Replacing one
        ///   of them leaves a character wearing a mismatch.
        ///
        /// Both are solvable and neither is solved, so they are left out rather than offered and
        /// quietly broken. The categories they used are still here, so restoring one is a line.
        /// </summary>
        private static readonly (string folder, Category category)[] PLACES = {
            ("/actors/equipment/meleeweapons/", Category.Melee),
            ("/actors/equipment/rangedweapons/", Category.Ranged),
        };

        /// <summary>
        /// The things the game throws, which are static meshes like any other.
        ///
        /// Named one at a time rather than found by a rule, because they do not follow one. They
        /// are scattered across the item folders, the enemy folders and a shared effects folder,
        /// and the ordinary arrow - the one every bow fires - is at `Models/Weapons/Arrow/Arrow`
        /// with no `SM_` in front of it, so every rule written around that prefix misses the most
        /// useful one in the game.
        ///
        /// Every path here was read through this catalogue's own reader before being listed, so
        /// nothing is offered that cannot be imported onto. The ordinary arrow is 120 vertices and
        /// 60 triangles; the smallest, a pumpkin seed, is 24 and 12.
        ///
        /// Matched by the end of the path, because the same asset arrives with a different prefix
        /// depending on which pak it came out of.
        /// </summary>
        private static readonly string[] PROJECTILE_ASSETS = {
            "/models/weapons/arrow/arrow",                              // every ordinary bow
            "/actors/items/tormentquiver/sm_tormentarrow",
            "/actors/items/heavyharpoon/sm_harpoonarrow",
            "/actors/items/fireworksarrowitem/sm_firework",
            "/actors/items/arrow/jackolantern/sm_pumpkinseed",
            "/actors/items/corruptedseeds/sm_corruptedseeds",
            "/rangedweapons/windbow/sm_galearrow_helix",
            "/arrows/traps/tntarrow/sm_tntarrowbox",
            "/illusioner_arrow/sm_illusioner_arrow",
            "/spider/vfx/sm_spiderwebprojectilemesh",
            "/effects/materials/projectiles/sm_fireballprojectile",
            "/effects/materials/projectiles/sm_projectile_rectangular",
        };

        private static List<MeshEntry>? _catalogue;

        //Every asset path, once. Built on demand and kept, because working out a bow's family
        //means asking whether a sibling exists, and asking eighty thousand entries that question
        //once per bow is two hundred passes over the index to answer what one pass could.
        private static HashSet<string>? _paths;

        private static HashSet<string> paths()
        {
            if (_paths != null) { return _paths; }

            var found = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var paks = CustomSkins.index;
            if (paks != null)
            {
                foreach (var entry in paks)
                {
                    var path = entry.Replace("\\", "/");
                    var at = path.IndexOf("//", StringComparison.Ordinal);
                    if (at >= 0) { path = path.Substring(at + 1); }
                    found.Add(path);
                }
            }

            _paths = found;
            return found;
        }

        /// <summary>A mesh's trailing number and the name in front of it, or nothing.</summary>
        private static (string stem, int number, int digits)? statedName(string assetPath)
        {
            var digits = 0;
            while (digits < assetPath.Length && char.IsDigit(assetPath[assetPath.Length - 1 - digits])) { digits++; }
            if (digits == 0) { return null; }

            if (!int.TryParse(assetPath.Substring(assetPath.Length - digits),
                    NumberStyles.Integer, CultureInfo.InvariantCulture, out var number)) { return null; }

            return (assetPath.Substring(0, assetPath.Length - digits), number, digits);
        }

        private static string stateNamed((string stem, int number, int digits) parts, int number)
            => parts.stem + number.ToString(new string('0', parts.digits), CultureInfo.InvariantCulture);

        /// <summary>
        /// Whether this is the first of its draw states, which is the only one worth listing.
        ///
        /// Asked by looking below rather than for a particular number, because a bow's states
        /// start at one in some folders and at nought in others.
        /// </summary>
        private static bool firstState(string assetPath)
        {
            var parts = statedName(assetPath);
            if (parts == null) { return false; }

            return !paths().Contains(stateNamed(parts.Value, parts.Value.number - 1));
        }

        public static bool ready => CustomSkins.ready;

        public static IReadOnlyList<MeshEntry> all()
        {
            //Custom items first, and never cached with the rest: they come and go with the New
            //Items tab, and the game's own list does not change while the app runs.
            var custom = CustomItems.meshesForWorkshop()
                .Select(m => new MeshEntry(m.assetPath, m.bow ? BOWS : WEAPONS, "★ " + m.name, R.ITEMS_TAB))
                .ToList();
            return custom.Count == 0 ? gameMeshes() : custom.Concat(gameMeshes()).ToList();
        }

        private static IReadOnlyList<MeshEntry> gameMeshes()
        {
            if (_catalogue != null) { return _catalogue; }

            var paks = CustomSkins.index;
            if (paks == null) { return Array.Empty<MeshEntry>(); }

            var found = new List<MeshEntry>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (var entry in paks)
            {
                var path = entry.Replace("\\", "/");
                var at = path.IndexOf("//", StringComparison.Ordinal);
                if (at >= 0) { path = path.Substring(at + 1); }

                //The things the game throws, before the prefix test below, because the one that
                //matters most does not carry the prefix.
                if (isProjectile(path))
                {
                    if (seen.Add(path))
                    {
                        //No caution. The tag in the list means "an import onto this comes out
                        //wrong", which is a thing that was measured on two weapons - and nothing
                        //of the sort is known about any projectile. What they do need saying is
                        //advice rather than a warning, so it goes under the list instead.
                        found.Add(new MeshEntry(path, PROJECTILES, prettyName(path),
                            folderName(path)));
                    }
                    continue;
                }

                //Only the static meshes. A name beginning SM_ is not a promise - the game uses the
                //same prefix for SoundMix assets and for Skeletons - but a path under an equipment
                //folder narrows it enough that the rest is caught when the geometry fails to read.
                var file = path.Substring(path.LastIndexOf('/') + 1);
                if (!file.StartsWith("sm_", StringComparison.OrdinalIgnoreCase)) { continue; }

                foreach (var (folder, category) in PLACES)
                {
                    if (path.IndexOf(folder, StringComparison.OrdinalIgnoreCase) < 0) { continue; }
                    if (!seen.Add(path)) { break; }

                    //A bow is its draw states, and only the first of them is offered: the model
                    //goes into all of them on apply, so listing four would be four ways to do a
                    //quarter of the job each. Anything else in that folder - a quiver, a mesh
                    //with no states - is not a bow and is left out rather than half handled.
                    if (category == Category.Ranged)
                    {
                        //The first of its states, and one that has states at all. A quiver or a
                        //one-off mesh in a ranged folder is not a bow.
                        if (!firstState(path) || drawStatesOf(path).Count < 2) { break; }
                        //The caution is the same one every other weapon gets - whether its
                        //material makes imports come out wrong - and nothing else. What a bow
                        //needs said is advice rather than a warning, and it goes under the list:
                        //the mark against an entry means "this one will disappoint you", and
                        //putting guidance there makes every entry look broken. Which it did,
                        //for the second time.
                        found.Add(new MeshEntry(path, BOWS, bowName(path),
                            folderName(path), cautionFor(path)));
                        break;
                    }

                    found.Add(new MeshEntry(path, WEAPONS, prettyName(path),
                        folderName(path), cautionFor(path)));
                    break;
                }
            }

            //Weapons first, projectiles second, whatever the alphabet thinks.
            _catalogue = found
                .OrderBy(mesh => mesh.Group == WEAPONS ? 0 : mesh.Group == BOWS ? 1 : 2)
                .ThenBy(mesh => mesh.Name, StringComparer.CurrentCultureIgnoreCase)
                .ToList();
            return _catalogue;
        }

        /// <summary>
        /// The shape of one mesh, or nothing when its geometry cannot be found.
        ///
        /// Returning nothing is a real answer rather than a failure: a few of the game's meshes
        /// are skeletal or have no declared bounds, and those simply cannot be reshaped this way.
        /// </summary>
        public static MeshShape? read(string assetPath)
        {
            var package = readPackage(assetPath);
            if (package == null) { return null; }

            if (!MeshBounds.tryRead(package.Value, out var origin, out var extent, out var radius)) { return null; }

            var uexp = package.Value.UExp.ToArray();
            var vertices = MeshGeometry.findPositions(uexp, origin, extent);
            if (vertices == null) { return null; }

            var buffers = MeshGeometry.findTriangles(uexp, vertices.Count, vertices.DataEnd);
            var triangles = buffers.FirstOrDefault(MeshGeometry.isTriangleList);

            var indices = new List<int>();
            if (triangles != null)
            {
                foreach (var (a, b, c) in MeshGeometry.readTriangles(uexp, triangles, triangles.Count))
                {
                    indices.Add(a);
                    indices.Add(b);
                    indices.Add(c);
                }
            }

            //Channel zero is the artwork. The second channel a weapon carries is the reserved
            //lightmap one, which paints nothing.
            var attributes = MeshGeometry.findAttributes(uexp, vertices);
            var texCoords = new List<(float, float)>(vertices.Count);
            if (attributes != null && !attributes.FullPrecisionUVs)
            {
                for (int i = 0; i < vertices.Count; i++)
                {
                    var at = attributes.UVsOffset + i * attributes.TexCoords * attributes.UVStride;
                    texCoords.Add((
                        VertexPacking.unpackHalf(BitConverter.ToUInt16(uexp, at)),
                        VertexPacking.unpackHalf(BitConverter.ToUInt16(uexp, at + 2))));
                }
            }

            return new MeshShape(MeshGeometry.readPositions(uexp, vertices, vertices.Count), indices,
                origin, extent, radius, texCoords);
        }

        /// <summary>
        /// Writes a mod pak holding the mesh with the transform applied.
        ///
        /// One pak per reshaped mesh, named after it, so that undoing one does not undo the rest
        /// and so the list of installed mods says what each of them did.
        /// </summary>
        public static CustomSkins.InstalledMod apply(string assetPath, MeshEdit.Transform transform,
            string modName, IEnumerable<PakWriter.Entry>? extra = null)
        {
            if (CustomItems.isCopied(assetPath)) { return CustomItems.setModel(assetPath, null, transform); }

            var package = readPackage(assetPath)
                ?? throw new InvalidOperationException($"Could not read {assetPath}.");

            if (!MeshBounds.tryRead(package, out var origin, out var extent, out var radius))
            {
                throw new InvalidOperationException("That mesh does not declare its bounds, so its geometry cannot be found.");
            }

            var uasset = package.UAsset.ToArray();
            var uexp = package.UExp.ToArray();

            var edit = MeshEdit.open(uasset, uexp, origin, extent, radius)
                ?? throw new InvalidOperationException("Could not find the vertices inside that mesh.");

            edit.apply(transform);
            var (editedAsset, editedExp) = edit.write();

            var insidePak = assetPath.TrimStart('/');
            var entries = new List<PakWriter.Entry> {
                new PakWriter.Entry(insidePak + ".uasset", editedAsset),
                new PakWriter.Entry(insidePak + ".uexp", editedExp),
            };
            if (package.UBulk != null)
            {
                entries.Add(new PakWriter.Entry(insidePak + ".ubulk", package.UBulk.Value.ToArray()));
            }

            return CustomSkins.writeModPak(modName, entries);
        }

        /// <summary>
        /// Writes a mod pak replacing the weapon's mesh with an imported model.
        ///
        /// The texture goes in the same pak when the model brought one, and that is not a
        /// convenience - it is required. An imported model carries its own texture coordinates,
        /// which have nothing to do with how the game's artwork was laid out, so the new mesh
        /// wearing the old texture would show the right shape painted with nonsense. Shipping them
        /// apart would let somebody install half of it.
        /// </summary>
        public static CustomSkins.InstalledMod import(string assetPath, GlbModel model,
            MeshEdit.Transform transform, string modName, IEnumerable<PakWriter.Entry>? extra = null)
        {
            //A custom item keeps its model in its own design and is rebuilt with the rest of the
            //New Items - a pak of its own would be a second copy of the same paths.
            if (CustomItems.isCopied(assetPath)) { return CustomItems.setModel(assetPath, model, transform); }

            var entries = new List<PakWriter.Entry>();

            //A bow is four meshes rather than one - the draw states - and the model goes into
            //every one of them. Anything else changes shape halfway through being fired.
            //
            //The same geometry into all four, which is what makes this work at all: the fit is
            //absolute, decided by the model and the sliders and nothing about the mesh being
            //replaced, so an arrangement made against the first state is the right arrangement
            //for the other three. What is lost is the draw animation, and a bow that does not
            //bend is a small price for a bow that is a fish.
            foreach (var state in drawStatesOf(assetPath))
            {
                //The first one has to work. A later state that cannot be read is skipped rather
                //than fatal - three states replaced is a weapon that mostly looks right, and
                //refusing outright would leave one that does not look right at all.
                try { entries.AddRange(rewrite(state, model, transform)); }
                catch (Exception) when (state != assetPath) { }
            }

            if (model.BaseColourPng != null)
            {
                entries.AddRange(CustomSkins.texturePatchFor(assetPath, model.BaseColourPng));
            }

            //And the weapon's cut-out turned off. Some of these materials decide whether to draw a
            //pixel at all from a texture that is not the one being replaced - see the note on
            //unmask - and an imported model then arrives with whole pieces of it missing.
            entries.AddRange(CreatureVariants.unmask(assetPath, out _));

            if (extra != null) { entries.AddRange(extra); }

            return CustomSkins.writeModPak(modName, entries);
        }



        /// <summary>
        /// Every draw state of a weapon, or just the weapon when it has only one.
        ///
        /// A bow is `SM_Bow1` through `SM_Bow4` in one folder, and the game swaps between them as
        /// the string is pulled. Only the first is offered in the list, because four entries for
        /// one weapon is four ways to do three quarters of the job.
        ///
        /// Found by looking rather than assumed to be four: the family is however many siblings
        /// the pak actually holds, counted up from the name ending in one.
        /// </summary>
        private static IReadOnlyList<string> drawStatesOf(string assetPath)
        {
            var family = new List<string> { assetPath };

            //However the states are numbered, which is not one way. `SM_Bow1` counts in single
            //digits and `SM_WindBow_01` counts in two, so the width is taken from the name rather
            //than assumed - padded wrongly, every bow but the plainest finds no siblings at all.
            var parts = statedName(assetPath);
            if (parts == null) { return family; }

            var known = paths();

            //Counting up until one is missing, rather than to four: nothing says a weapon has
            //exactly four states and a gap would be skipped in silence.
            for (int state = parts.Value.number + 1; state <= parts.Value.number + 15; state++)
            {
                var next = stateNamed(parts.Value, state);
                if (!known.Contains(next) && !CustomItems.isCopied(next)) { break; }
                family.Add(next);
            }

            return family;
        }


        /// <summary>
        /// A bow's name without the draw state number on the end of it.
        ///
        /// `SM_WindBow_01` is the Wind Bow, not the Wind Bow nought one. Trailing digits and
        /// whatever separates them come off - all of them, since they run to two digits in most
        /// folders and one in the rest.
        /// </summary>
        private static string bowName(string assetPath)
        {
            var cut = assetPath.Length;
            while (cut > 0 && char.IsDigit(assetPath[cut - 1])) { cut--; }
            while (cut > 0 && (assetPath[cut - 1] == '_' || assetPath[cut - 1] == ' ')) { cut--; }

            //Nothing but a number, which is not a name. Better the raw one than an empty row.
            var trimmed = assetPath.Substring(0, cut);
            return trimmed.EndsWith("/", StringComparison.Ordinal) || cut == 0
                ? prettyName(assetPath)
                : prettyName(trimmed);
        }

        /// <summary>One mesh rewritten to hold an imported model, as the files that go in a pak.</summary>
        private static IEnumerable<PakWriter.Entry> rewrite(string assetPath, GlbModel model,
            MeshEdit.Transform transform)
        {
            var package = readPackage(assetPath)
                ?? throw new InvalidOperationException($"Could not read {assetPath}.");

            if (!MeshBounds.tryRead(package, out var origin, out var extent, out var radius))
            {
                throw new InvalidOperationException("That mesh does not declare its bounds, so its geometry cannot be found.");
            }

            var mesh = CookedMesh.open(package.UAsset.ToArray(), package.UExp.ToArray(), origin, extent, radius)
                ?? throw new InvalidOperationException(
                    "This weapon's mesh is not one this can rewrite - it uses several materials, or its bounds cannot be located safely.");

            var (uasset, uexp) = mesh.rebuild(ModelFitting.place(model, transform, mesh.Attributes.TexCoords));

            var insidePak = assetPath.TrimStart('/');
            var entries = new List<PakWriter.Entry> {
                new PakWriter.Entry(insidePak + ".uasset", uasset),
                new PakWriter.Entry(insidePak + ".uexp", uexp),
            };
            if (package.UBulk != null)
            {
                entries.Add(new PakWriter.Entry(insidePak + ".ubulk", package.UBulk.Value.ToArray()));
            }

            return entries;
        }

        /// <summary>
        /// The artwork a weapon is painted with, for showing the preview as it really looks.
        ///
        /// A flat grey model says where a shape is but not which way round it is: a sword with its
        /// grip wrapping at one end is obvious the moment it is textured and guesswork before
        /// that. Since aligning the handle is the whole job this tab asks of somebody, showing the
        /// texture is not decoration.
        /// </summary>
        public static BitmapSource? textureFor(string meshAssetPath)
        {
            if (CustomItems.isCopied(meshAssetPath)) { return CustomItems.copiedTexture(meshAssetPath); }

            var texture = CustomSkins.textureBeside(meshAssetPath);
            if (texture == null) { return null; }

            try { return CustomSkins.preview(texture); }
            catch (Exception) { return null; }
        }

        private static PakPackage? readPackage(string assetPath)
        {
            //A custom item's copy is in no pak the index reads; it lives in memory until built.
            if (CustomItems.copiedPackage(assetPath) is { } copied) { return copied; }

            var paks = CustomSkins.index;
            if (paks == null) { return null; }

            try
            {
                var package = paks.extractPackage(assetPath);
                if (package == null || !package.Value.HasExport()) { return null; }
                return package;
            }
            catch (Exception)
            {
                //A mesh that will not parse is one this cannot reshape, which is worth nothing
                //more than leaving it out of the list.
                return null;
            }
        }

        /// <summary>
        /// Whether this weapon is one an imported model will not sit right on.
        ///
        /// Decided by which master material dresses it, which measured across the game's melee
        /// weapons splits them sixty two to two: everything descends from the equipment master
        /// except the Anchor and the Anchor Unique, which descend from the one the *decor* is
        /// built from. That master takes its cut-out and its shine from a texture named in a
        /// separate parameter, and on the Unique that parameter points at an ornament in another
        /// folder - so an import repaints the colour and the rest goes on coming from somewhere it
        /// cannot reach.
        ///
        /// Both Anchors were tried and both came out wrong, which is what makes this the master
        /// rather than the borrowing: the ordinary one repaints the very texture its shine comes
        /// from and is still wrong, so the fault is in how that master reads it.
        /// </summary>
        private static string cautionFor(string assetPath)
        {
            var masters = CreatureVariants.mastersOf(assetPath);

            //Nothing found is not a complaint. Some of these meshes are dressed from their parent
            //weapon's folder, and saying "this may not work" about every one of them would make
            //the warning worth ignoring.
            if (masters.Count == 0) { return string.Empty; }
            if (masters.Contains(CreatureVariants.EQUIPMENT_MASTER)) { return string.Empty; }

            return R.WEAPON_SKINS_WRONG_MASTER;
        }


        /// <summary>Whether a path is one of the things the game throws.</summary>
        private static bool isProjectile(string path)
        {
            foreach (var tail in PROJECTILE_ASSETS)
            {
                if (path.EndsWith(tail, StringComparison.OrdinalIgnoreCase)) { return true; }
            }
            return false;
        }

        private static string folderName(string assetPath)
        {
            var parts = assetPath.TrimEnd('/').Split('/');
            return parts.Length >= 2 ? spaced(parts[parts.Length - 2]) : string.Empty;
        }

        private static string prettyName(string assetPath)
        {
            var file = assetPath.Substring(assetPath.LastIndexOf('/') + 1);
            if (file.StartsWith("sm_", StringComparison.OrdinalIgnoreCase)) { file = file.Substring(3); }
            return spaced(file);
        }

        /// <summary>
        /// An asset name turned into something readable.
        ///
        /// The paks spell these in lower case with underscores, and CamelCase underneath that, so
        /// both are undone: "claymoreunique3_greataxeblade" reads as "Claymoreunique3 Greataxeblade"
        /// rather than as itself. Not perfect, and better than the raw path by a distance.
        /// </summary>
        /// <summary>This catalogue, as the shared workshop tab wants it.</summary>
        public static readonly MeshCatalogue catalogue = new Catalogue();

        private sealed class Catalogue : MeshCatalogue
        {
            public override bool ready => WeaponMeshes.ready;
            public override IReadOnlyList<MeshEntry> all() => WeaponMeshes.all();
            public override MeshShape? read(string assetPath) => WeaponMeshes.read(assetPath);
            public override BitmapSource? textureFor(string assetPath) => WeaponMeshes.textureFor(assetPath);

            public override CustomSkins.InstalledMod replace(string assetPath, GlbModel model,
                MeshEdit.Transform transform, string modName, IEnumerable<PakWriter.Entry>? extra = null)
                => import(assetPath, model, transform, modName, extra);

            public override CustomSkins.InstalledMod reshape(string assetPath, MeshEdit.Transform transform,
                string modName, IEnumerable<PakWriter.Entry>? extra = null)
                => apply(assetPath, transform, modName, extra);

            public override string subjectLabel => R.WEAPON_SKINS_WEAPON;
            public override string countFormat => R.WEAPON_SKINS_COUNT;
            public override IReadOnlyList<string> groups() => new[] { WEAPONS, BOWS, PROJECTILES };

            public override string noteFor(string? group)
                => group == PROJECTILES ? R.WEAPON_SKINS_PROJECTILE_NOTE
                : group == BOWS ? R.WEAPON_SKINS_BOW_NOTE
                : string.Empty;

            public override string scopeNote => R.WEAPON_SKINS_SCOPE;
            public override string ghostHint => R.WEAPON_SKINS_GHOST_HINT;
            public override string importHint => R.WEAPON_SKINS_IMPORT_HINT;
            public override string nothingToDo => R.WEAPON_SKINS_NOTHING_TO_DO;
        }

        private static string spaced(string raw)
        {
            var words = raw.Replace('_', ' ').Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
            var text = new StringBuilder();
            foreach (var word in words)
            {
                if (text.Length > 0) { text.Append(' '); }
                //Invariant rather than the machine's own language. These are English asset
                //names, and a Turkish Windows title-cases "Item" as "ıtem" - the dotless i is
                //correct for Turkish words and wrong for a file called FireworksArrowItem.
                text.Append(CultureInfo.InvariantCulture.TextInfo.ToTitleCase(word));
            }
            return text.Length == 0 ? raw : text.ToString();
        }
    }
}
