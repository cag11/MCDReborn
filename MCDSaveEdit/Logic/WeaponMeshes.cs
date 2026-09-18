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
        public enum Category { Melee, Ranged, Armor }

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
        };

        private static List<MeshEntry>? _catalogue;

        public static bool ready => CustomSkins.ready;

        public static IReadOnlyList<MeshEntry> all()
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

                //Only the static meshes. A name beginning SM_ is not a promise - the game uses the
                //same prefix for SoundMix assets and for Skeletons - but a path under an equipment
                //folder narrows it enough that the rest is caught when the geometry fails to read.
                var file = path.Substring(path.LastIndexOf('/') + 1);
                if (!file.StartsWith("sm_", StringComparison.OrdinalIgnoreCase)) { continue; }

                foreach (var (folder, category) in PLACES)
                {
                    if (path.IndexOf(folder, StringComparison.OrdinalIgnoreCase) < 0) { continue; }
                    if (!seen.Add(path)) { break; }
                    found.Add(new MeshEntry(path, category.ToString(), prettyName(path), folderName(path)));
                    break;
                }
            }

            _catalogue = found
                .OrderBy(mesh => mesh.Group, StringComparer.Ordinal)
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
        public static CustomSkins.InstalledMod apply(string assetPath, MeshEdit.Transform transform, string modName)
        {
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
            MeshEdit.Transform transform, string modName)
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

            if (model.BaseColourPng != null)
            {
                entries.AddRange(CustomSkins.texturePatchFor(assetPath, model.BaseColourPng));
            }

            //And the weapon's cut-out turned off. Some of these materials decide whether to draw a
            //pixel at all from a texture that is not the one being replaced - see the note on
            //unmask - and an imported model then arrives with whole pieces of it missing.
            entries.AddRange(CreatureVariants.unmask(assetPath, out _));

            return CustomSkins.writeModPak(modName, entries);
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
            var texture = CustomSkins.textureBeside(meshAssetPath);
            if (texture == null) { return null; }

            try { return CustomSkins.preview(texture); }
            catch (Exception) { return null; }
        }

        private static PakPackage? readPackage(string assetPath)
        {
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
                MeshEdit.Transform transform, string modName)
                => import(assetPath, model, transform, modName);

            public override CustomSkins.InstalledMod reshape(string assetPath, MeshEdit.Transform transform, string modName)
                => apply(assetPath, transform, modName);

            public override string subjectLabel => R.WEAPON_SKINS_WEAPON;
            public override string countFormat => R.WEAPON_SKINS_COUNT;
            public override string scopeNote => R.WEAPON_SKINS_MELEE_ONLY;
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
                text.Append(CultureInfo.CurrentCulture.TextInfo.ToTitleCase(word));
            }
            return text.Length == 0 ? raw : text.ToString();
        }
    }
}
