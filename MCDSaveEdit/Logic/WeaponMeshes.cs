using MCDSaveEdit.Services;
using PakReader.Pak;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
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

        public sealed class Mesh
        {
            public Mesh(string assetPath, Category category)
            {
                AssetPath = assetPath;
                Category = category;
                Name = prettyName(assetPath);
                Variant = folderName(assetPath);
            }

            public string AssetPath { get; }
            public Category Category { get; }
            /// <summary>The mesh's own name, spaced out for reading.</summary>
            public string Name { get; }
            /// <summary>The folder it sits in, which is what tells two uniques apart.</summary>
            public string Variant { get; }

            public override string ToString() => Name;
        }

        /// <summary>What a mesh looks like, far enough to draw it and to measure it.</summary>
        public sealed class Shape
        {
            public Shape(IReadOnlyList<MeshGeometry.Position> positions, IReadOnlyList<int> indices,
                MeshGeometry.Position origin, MeshGeometry.Position extent, float radius,
                IReadOnlyList<(float u, float v)>? texCoords = null)
            {
                Positions = positions;
                Indices = indices;
                Origin = origin;
                Extent = extent;
                Radius = radius;
                TexCoords = texCoords ?? Array.Empty<(float, float)>();
            }

            public IReadOnlyList<MeshGeometry.Position> Positions { get; }
            /// <summary>Flat triples, as a renderer wants them.</summary>
            public IReadOnlyList<int> Indices { get; }
            public MeshGeometry.Position Origin { get; }
            public MeshGeometry.Position Extent { get; }
            public float Radius { get; }
            /// <summary>One pair per vertex, the artwork channel only.</summary>
            public IReadOnlyList<(float u, float v)> TexCoords { get; }
            public int TriangleCount => Indices.Count / 3;

            /// <summary>The longest side, which is the number a scale has to be judged against.</summary>
            public float LongestSide => Math.Max(Extent.X, Math.Max(Extent.Y, Extent.Z)) * 2f;
        }

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

        private static List<Mesh>? _catalogue;

        public static bool ready => CustomSkins.ready;

        /// <summary>
        /// Every equipment mesh in the game, found once and remembered.
        ///
        /// Scanned rather than listed, because a list written by hand goes stale the first time a
        /// DLC adds a weapon, and this is a thing the paks can simply be asked.
        /// </summary>
        public static IReadOnlyList<Mesh> all()
        {
            if (_catalogue != null) { return _catalogue; }

            var paks = CustomSkins.index;
            if (paks == null) { return Array.Empty<Mesh>(); }

            var found = new List<Mesh>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (var entry in paks)
            {
                var path = entry.Replace('\\', '/');
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
                    found.Add(new Mesh(path, category));
                    break;
                }
            }

            _catalogue = found
                .OrderBy(mesh => mesh.Category)
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
        public static Shape? read(string assetPath)
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

            return new Shape(MeshGeometry.readPositions(uexp, vertices, vertices.Count), indices,
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
        /// A transform that drops an imported model roughly where the weapon it replaces sits.
        ///
        /// Roughly is the honest word. It matches the model's longest side to the weapon's and
        /// centres one box on the other, which gets the scale right - the difference is usually a
        /// factor of ten or more, and nobody wants to find that with a slider - and gets the
        /// position close. It cannot get the orientation right, because nothing in a model says
        /// which end is the handle. That is what the ghost and the rotation sliders are for.
        /// </summary>
        public static MeshEdit.Transform autoFit(GlbModel model, Shape donor)
        {
            var (modelOrigin, modelExtent, _) = CookedMesh.measure(model.Positions);

            var modelSide = Math.Max(modelExtent.X, Math.Max(modelExtent.Y, modelExtent.Z)) * 2f;
            var donorSide = donor.LongestSide;
            var scale = modelSide > 0.0001f && donorSide > 0.0001f ? donorSide / modelSide : 1f;

            //Centre on centre. The weapon's own origin is not its middle - a claymore's sits fifty
            //units down the blade - so the model is placed against the donor's box rather than
            //against its origin.
            var offset = new MeshGeometry.Position(
                donor.Origin.X - modelOrigin.X * scale,
                donor.Origin.Y - modelOrigin.Y * scale,
                donor.Origin.Z - modelOrigin.Z * scale);

            return new MeshEdit.Transform(scale, offset, new MeshGeometry.Position(0, 0, 0));
        }

        /// <summary>
        /// The imported model's geometry, moved into the weapon's space.
        ///
        /// Directions are turned but never moved or scaled: a normal says which way a surface
        /// faces, and adding an offset to it would point it somewhere meaningless. That is why the
        /// rotation is applied to them separately rather than by reusing the whole transform.
        /// </summary>
        public static CookedMesh.Geometry place(GlbModel model, MeshEdit.Transform transform, int texCoordSets)
        {
            var positions = new List<MeshGeometry.Position>(model.Positions.Count);
            foreach (var position in model.Positions) { positions.Add(transform.move(position)); }

            var turn = new MeshEdit.Transform(1f, new MeshGeometry.Position(0, 0, 0), transform.RotationDegrees);

            var normals = new List<VertexPacking.Direction>(model.Normals.Count);
            foreach (var normal in model.Normals) { normals.Add(turned(normal, turn)); }

            var tangents = new List<VertexPacking.Direction>(model.Tangents.Count);
            foreach (var tangent in model.Tangents) { tangents.Add(turned(tangent, turn)); }

            return new CookedMesh.Geometry {
                Positions = positions,
                Normals = normals,
                Tangents = tangents,
                TexCoords = spreadTexCoords(model.TexCoords, texCoordSets, positions.Count),
                Indices = model.Indices,
            };
        }

        /// <summary>
        /// One set of texture coordinates per vertex turned into as many as the weapon's mesh
        /// keeps.
        ///
        /// A cooked weapon here holds two: the one the artwork is painted with, and a second the
        /// engine reserves for baked lighting. A model exported from a modelling tool almost
        /// always has only the first, so the second is filled with a copy of it. That is the right
        /// filler rather than a lazy one - a weapon is a moving object lit dynamically, so nothing
        /// ever reads its lightmap channel, and leaving it empty would put zeroes where the engine
        /// expects coordinates.
        ///
        /// They are written per vertex rather than per channel: vertex zero's sets, then vertex
        /// one's. That is the order the engine reads them back in, and the order a flat copy of
        /// the game's own data already round-trips through.
        /// </summary>
        private static IReadOnlyList<(float u, float v)> spreadTexCoords(
            IReadOnlyList<(float u, float v)> supplied, int perVertex, int vertices)
        {
            if (perVertex <= 1) { return supplied; }

            var spread = new List<(float, float)>(vertices * perVertex);
            for (int i = 0; i < vertices; i++)
            {
                var pair = i < supplied.Count ? supplied[i] : (0f, 0f);
                for (int channel = 0; channel < perVertex; channel++) { spread.Add(pair); }
            }
            return spread;
        }

        private static VertexPacking.Direction turned(VertexPacking.Direction direction, MeshEdit.Transform turn)
        {
            var spun = turn.move(new MeshGeometry.Position(direction.X, direction.Y, direction.Z));
            //The fourth component says which way the bitangent runs and is not a direction, so it
            //is carried across untouched.
            return new VertexPacking.Direction(spun.X, spun.Y, spun.Z, direction.W);
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

            var (uasset, uexp) = mesh.rebuild(place(model, transform, mesh.Attributes.TexCoords));

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
        public static System.Windows.Media.Imaging.BitmapSource? textureFor(string meshAssetPath)
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
