using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using MCDSaveEdit.Services;
using System.Windows.Media;
using System.Windows.Media.Imaging;
#nullable enable

namespace MCDSaveEdit.Logic
{
    /// <summary>
    /// A model somebody made, read out of a `.glb`.
    ///
    /// glTF binary is the right thing to ask people for, and not because it is small. It carries
    /// the four things a cooked mesh needs - positions, normals, tangents and texture coordinates -
    /// already computed, in one file, alongside the textures. An `.obj` carries none of the
    /// tangents and splits its textures across a folder; an `.fbx` is a format with several
    /// incompatible dialects and no specification worth the name. This is one binary chunk behind
    /// a piece of JSON describing where everything in it is.
    ///
    /// Only what a static mesh needs is read. Animation, skins, cameras, scene hierarchy and the
    /// material model are all skipped - a weapon in this game is a rigid lump with one texture on
    /// it, and reading more would be reading things that have nowhere to go.
    /// </summary>
    public sealed class GlbModel
    {
        private GlbModel(string name, IReadOnlyList<MeshGeometry.Position> positions,
            IReadOnlyList<VertexPacking.Direction> normals, IReadOnlyList<VertexPacking.Direction> tangents,
            IReadOnlyList<(float u, float v)> texCoords, IReadOnlyList<int> indices,
            byte[]? baseColourPng, string note)
        {
            Name = name;
            Positions = positions;
            Normals = normals;
            Tangents = tangents;
            TexCoords = texCoords;
            Indices = indices;
            BaseColourPng = baseColourPng;
            Note = note;
        }

        public string Name { get; }
        public IReadOnlyList<MeshGeometry.Position> Positions { get; }
        public IReadOnlyList<VertexPacking.Direction> Normals { get; }
        public IReadOnlyList<VertexPacking.Direction> Tangents { get; }
        public IReadOnlyList<(float u, float v)> TexCoords { get; }
        public IReadOnlyList<int> Indices { get; }
        /// <summary>The base colour image, if the file carries one. The other maps have nowhere to go.</summary>
        public byte[]? BaseColourPng { get; }

        /// <summary>
        /// What is missing from this model, in a sentence, or nothing when nothing is.
        ///
        /// Worth saying out loud rather than leaving somebody to work out from the result. A .glb
        /// converted from some other format regularly arrives with its artwork gone - no image,
        /// and materials left stating only how shiny they are - and glTF says a material with no
        /// base colour is white. So the import is correct and the model comes out white, which
        /// looks exactly like the tool having lost the colours rather than the file never having
        /// had them.
        /// </summary>
        public string Note { get; }

        public int VertexCount => Positions.Count;
        public int TriangleCount => Indices.Count / 3;

        private const uint GLB_MAGIC = 0x46546C67;   // "glTF"
        private const uint CHUNK_JSON = 0x4E4F534A;  // "JSON"
        private const uint CHUNK_BIN = 0x004E4942;   // "BIN"

        //Component types, as glTF numbers them.
        private const int BYTE = 5120, UNSIGNED_BYTE = 5121, SHORT = 5122,
            UNSIGNED_SHORT = 5123, UNSIGNED_INT = 5125, FLOAT = 5126;

        public static GlbModel read(string path) => read(File.ReadAllBytes(path), Path.GetFileNameWithoutExtension(path));

        /// <summary>
        /// Reads the file, merging every piece of every mesh into one.
        ///
        /// A modelled weapon usually arrives as several primitives - a blade, a grip, a pommel,
        /// whatever the artist kept separate - and a cooked StaticMesh with one section holds one
        /// run of triangles. So they are concatenated, with each piece's indices shifted past the
        /// vertices already taken. Keeping them apart would mean writing several sections, which
        /// means several materials, which is a different and larger problem.
        /// </summary>
        public static GlbModel read(byte[] file, string name)
        {
            if (file.Length < 12) { throw new InvalidOperationException("That file is too short to be a .glb."); }
            if (BitConverter.ToUInt32(file, 0) != GLB_MAGIC)
            {
                throw new InvalidOperationException("That is not a .glb - it does not start with the glTF marker.");
            }

            var total = (int)Math.Min(BitConverter.ToUInt32(file, 8), (uint)file.Length);

            JsonObject? json = null;
            byte[]? binary = null;

            var at = 12;
            while (at + 8 <= total)
            {
                var length = (int)BitConverter.ToUInt32(file, at);
                var kind = BitConverter.ToUInt32(file, at + 4);
                var from = at + 8;
                if (from + length > file.Length) { break; }

                if (kind == CHUNK_JSON && json == null)
                {
                    json = JsonNode.Parse(Encoding.UTF8.GetString(file, from, length)) as JsonObject;
                }
                else if (kind == CHUNK_BIN && binary == null)
                {
                    binary = new byte[length];
                    Buffer.BlockCopy(file, from, binary, 0, length);
                }

                at = from + length;
            }

            if (json == null) { throw new InvalidOperationException("That .glb has no JSON describing what is in it."); }
            binary ??= Array.Empty<byte>();

            var accessors = json["accessors"] as JsonArray ?? new JsonArray();
            var views = json["bufferViews"] as JsonArray ?? new JsonArray();

            var positions = new List<MeshGeometry.Position>();
            var normals = new List<VertexPacking.Direction>();
            var tangents = new List<VertexPacking.Direction>();
            var texCoords = new List<(float, float)>();
            var indices = new List<int>();

            //Which material each run of vertices came from, kept in case the model turns out to
            //have no image and has to be given one. See paletteFor.
            var runs = new List<(int at, int count, int material)>();

            var meshes = json["meshes"] as JsonArray ?? new JsonArray();

            foreach (var (meshIndex, world) in placements(json, meshes.Count))
            {
                var meshNode = meshes[meshIndex];

                foreach (var primitiveNode in (meshNode as JsonObject)?["primitives"] as JsonArray ?? new JsonArray())
                {
                    if (!(primitiveNode is JsonObject primitive)) { continue; }

                    //Mode 4 is a plain triangle list. Strips and fans exist in the format and
                    //essentially never come out of a modelling tool, so they are declined rather
                    //than converted on a guess.
                    var mode = intOf(primitive["mode"], 4);
                    if (mode != 4) { continue; }

                    var attributes = primitive["attributes"] as JsonObject;
                    if (attributes == null) { continue; }

                    var positionIndex = intOf(attributes["POSITION"], -1);
                    if (positionIndex < 0) { continue; }

                    var first = positions.Count;
                    var piece = readVectors(accessors, views, binary, positionIndex, 3);
                    foreach (var value in piece)
                    {
                        positions.Add(world.moves(new MeshGeometry.Position(value[0], value[1], value[2])));
                    }

                    var normalIndex = intOf(attributes["NORMAL"], -1);
                    var normalValues = normalIndex >= 0 ? readVectors(accessors, views, binary, normalIndex, 3) : null;

                    var tangentIndex = intOf(attributes["TANGENT"], -1);
                    var tangentValues = tangentIndex >= 0 ? readVectors(accessors, views, binary, tangentIndex, 4) : null;

                    var uvIndex = intOf(attributes["TEXCOORD_0"], -1);
                    var uvValues = uvIndex >= 0 ? readVectors(accessors, views, binary, uvIndex, 2) : null;

                    for (int i = 0; i < piece.Count; i++)
                    {
                        //A missing attribute is filled rather than refused. A model without
                        //tangents lights badly; a model this refuses to open cannot be used at all.
                        normals.Add(world.turns(normalValues != null && i < normalValues.Count
                            ? new VertexPacking.Direction(normalValues[i][0], normalValues[i][1], normalValues[i][2], 1f)
                            : new VertexPacking.Direction(0, 0, 1, 1)));

                        tangents.Add(world.turns(tangentValues != null && i < tangentValues.Count
                            ? new VertexPacking.Direction(tangentValues[i][0], tangentValues[i][1], tangentValues[i][2], tangentValues[i][3])
                            : new VertexPacking.Direction(1, 0, 0, 1)));

                        texCoords.Add(uvValues != null && i < uvValues.Count
                            ? (uvValues[i][0], uvValues[i][1])
                            : (0f, 0f));
                    }

                    runs.Add((first, piece.Count, intOf(primitive["material"], -1)));

                    var indexIndex = intOf(primitive["indices"], -1);
                    if (indexIndex >= 0)
                    {
                        foreach (var index in readScalars(accessors, views, binary, indexIndex))
                        {
                            indices.Add(first + index);
                        }
                    }
                    else
                    {
                        //No index buffer means the vertices are already in triangle order.
                        for (int i = 0; i < piece.Count; i++) { indices.Add(first + i); }
                    }
                }
            }

            if (positions.Count == 0) { throw new InvalidOperationException("That .glb has no triangles in it."); }

            //An image if the model brought one, and otherwise one made out of the flat colours its
            //materials are painted with. Without this a model that carries no image comes out
            //wearing whatever the weapon it replaced was wearing, sampled through coordinates that
            //have nothing to do with that artwork - the right shape in somebody else's colours,
            //mostly transparent where the old texture had nothing.
            var image = findBaseColour(json, views, binary);
            var artwork = image ?? paletteFor(json, runs, texCoords);

            return new GlbModel(name, positions, normals, tangents, texCoords, indices, artwork,
                image == null ? whatIsMissing(json, runs) : string.Empty);
        }

        /// <summary>
        /// The base colour image, which is the only one of a model's maps that has anywhere to go.
        ///
        /// A weapon here gets one texture. The normal, roughness, metallic and occlusion maps a
        /// modern model ships with describe a lighting model this game's weapon materials do not
        /// use, so they are left where they are rather than baked into something they are not.
        /// </summary>
        private static byte[]? findBaseColour(JsonObject json, JsonArray views, byte[] binary)
        {
            var materials = json["materials"] as JsonArray;
            var textures = json["textures"] as JsonArray;
            var images = json["images"] as JsonArray;
            if (materials == null || textures == null || images == null) { return null; }

            foreach (var materialNode in materials)
            {
                var pbr = (materialNode as JsonObject)?["pbrMetallicRoughness"] as JsonObject;
                var textureIndex = intOf((pbr?["baseColorTexture"] as JsonObject)?["index"], -1);
                if (textureIndex < 0 || textureIndex >= textures.Count) { continue; }

                var source = intOf((textures[textureIndex] as JsonObject)?["source"], -1);
                if (source < 0 || source >= images.Count) { continue; }

                var image = images[source] as JsonObject;
                var viewIndex = intOf(image?["bufferView"], -1);
                if (viewIndex < 0) { continue; }

                var (offset, length) = viewOf(views, viewIndex);
                if (offset < 0 || offset + length > binary.Length) { continue; }

                var bytes = new byte[length];
                Buffer.BlockCopy(binary, offset, bytes, 0, length);
                return bytes;
            }
            return null;
        }

        /// <summary>
        /// How much of a textureless model has no colour to fall back on either.
        ///
        /// Counted in vertices rather than in materials, because that is what somebody sees. Three
        /// materials out of six sounds like half; if the one carrying the body is among them it is
        /// nearly all of it, and the model arrives looking blank.
        /// </summary>
        private static string whatIsMissing(JsonObject json, List<(int at, int count, int material)> runs)
        {
            if (!(json["materials"] is JsonArray materials)) { return string.Empty; }

            var blank = 0;
            var total = 0;
            foreach (var (_, count, material) in runs)
            {
                total += count;
                if (material < 0 || material >= materials.Count ||
                    colourOf(materials[material] as JsonObject) == null)
                {
                    blank += count;
                }
            }

            if (blank == 0 || total == 0) { return string.Empty; }

            //Two different things, and saying the alarming one about both was wrong. A model where
            //*some* materials state no colour is usually a model that wants white there - a fish
            //with a white belly is exactly this - and it imports correctly. A model where *none* of
            //them state anything has lost its artwork and will arrive blank.
            return blank == total
                ? R.MODEL_NO_COLOUR_AT_ALL
                : string.Format(R.MODEL_NO_COLOUR, blank * 100 / total);
        }

        /// <summary>
        /// Every mesh the scene places, with the transform that places it.
        ///
        /// glTF keeps geometry and position apart: a mesh is a shape in its own coordinates, and a
        /// *node* says where that shape goes. Reading the meshes and ignoring the nodes therefore
        /// reads a model with every part left at its own local origin and its own local
        /// orientation, which is not what anybody modelled.
        ///
        /// How wrong that is depends entirely on the model, which is what made it hard to notice.
        /// A sword exported from Sketchfab has one matrix at the root turning Y-up into Z-up, and
        /// nothing below it - so ignoring the nodes leaves the whole thing rotated a quarter turn,
        /// which looks like a model that needs rotating and gets rotated. A fish from the same
        /// place has three matrices stacked up, one of them twenty one degrees off any axis, and
        /// ignoring those leaves a shape that no whole-axis slider can put straight again. Same
        /// exporter, same site, one works and one does not.
        ///
        /// A mesh named by two nodes is emitted twice, once per node, which is how glTF says to
        /// reuse a shape in several places.
        /// </summary>
        private static IEnumerable<(int mesh, Placement world)> placements(JsonObject json, int meshCount)
        {
            var nodes = json["nodes"] as JsonArray;
            var scenes = json["scenes"] as JsonArray;

            var found = new List<(int, Placement)>();

            if (nodes != null && scenes != null && scenes.Count > 0)
            {
                var which = intOf(json["scene"], 0);
                if (which < 0 || which >= scenes.Count) { which = 0; }

                foreach (var root in (scenes[which] as JsonObject)?["nodes"] as JsonArray ?? new JsonArray())
                {
                    walk(nodes, intOf(root, -1), Placement.identity, meshCount, found, 0);
                }
            }

            //A file with no scene, or one whose scene names no meshes, still has its meshes. Taking
            //them where they lie is what this did for every model before nodes were understood, so
            //it is the fallback rather than a failure.
            if (found.Count == 0)
            {
                for (int i = 0; i < meshCount; i++) { found.Add((i, Placement.identity)); }
            }

            return found;
        }

        private static void walk(JsonArray nodes, int index, Placement above, int meshCount,
            List<(int, Placement)> found, int depth)
        {
            //A node tree that refers to itself would otherwise be a stack overflow rather than a
            //bad file, and depth is the cheapest thing to bound it by.
            if (depth > 64 || index < 0 || index >= nodes.Count) { return; }
            if (!(nodes[index] is JsonObject node)) { return; }

            var here = above.then(Placement.of(node));

            var mesh = intOf(node["mesh"], -1);
            if (mesh >= 0 && mesh < meshCount) { found.Add((mesh, here)); }

            foreach (var child in node["children"] as JsonArray ?? new JsonArray())
            {
                walk(nodes, intOf(child, -1), here, meshCount, found, depth + 1);
            }
        }

        /// <summary>
        /// Where a node puts what hangs off it: a four by four, the way glTF writes one.
        ///
        /// Stored column major because that is how the format stores it, rather than transposed on
        /// the way in - the arithmetic is written once and the file is read many times, so the
        /// version worth keeping honest is the one that matches the file.
        /// </summary>
        private readonly struct Placement
        {
            private readonly float[]? _m;

            private Placement(float[] m) { _m = m; }

            public static Placement identity => new Placement(new float[] {
                1, 0, 0, 0,
                0, 1, 0, 0,
                0, 0, 1, 0,
                0, 0, 0, 1,
            });

            public bool isNothing => _m == null;

            /// <summary>One node's own transform, however it chose to say it.</summary>
            public static Placement of(JsonObject node)
            {
                if (node["matrix"] is JsonArray given && given.Count >= 16)
                {
                    var m = new float[16];
                    for (int i = 0; i < 16; i++) { m[i] = (float)floatOf(given[i]); }
                    return new Placement(m);
                }

                //Otherwise scale, then rotate, then translate - in that order, which the format
                //states and which is not the order the fields are written in.
                var t = node["translation"] as JsonArray;
                var r = node["rotation"] as JsonArray;
                var sc = node["scale"] as JsonArray;
                if (t == null && r == null && sc == null) { return identity; }

                double sx = sc != null && sc.Count > 0 ? floatOf(sc[0]) : 1;
                double sy = sc != null && sc.Count > 1 ? floatOf(sc[1]) : 1;
                double sz = sc != null && sc.Count > 2 ? floatOf(sc[2]) : 1;

                double qx = r != null && r.Count > 0 ? floatOf(r[0]) : 0;
                double qy = r != null && r.Count > 1 ? floatOf(r[1]) : 0;
                double qz = r != null && r.Count > 2 ? floatOf(r[2]) : 0;
                double qw = r != null && r.Count > 3 ? floatOf(r[3]) : 1;

                double tx = t != null && t.Count > 0 ? floatOf(t[0]) : 0;
                double ty = t != null && t.Count > 1 ? floatOf(t[1]) : 0;
                double tz = t != null && t.Count > 2 ? floatOf(t[2]) : 0;

                var built = new float[16];
                built[0] = (float)((1 - 2 * (qy * qy + qz * qz)) * sx);
                built[1] = (float)((2 * (qx * qy + qz * qw)) * sx);
                built[2] = (float)((2 * (qx * qz - qy * qw)) * sx);
                built[3] = 0;
                built[4] = (float)((2 * (qx * qy - qz * qw)) * sy);
                built[5] = (float)((1 - 2 * (qx * qx + qz * qz)) * sy);
                built[6] = (float)((2 * (qy * qz + qx * qw)) * sy);
                built[7] = 0;
                built[8] = (float)((2 * (qx * qz + qy * qw)) * sz);
                built[9] = (float)((2 * (qy * qz - qx * qw)) * sz);
                built[10] = (float)((1 - 2 * (qx * qx + qy * qy)) * sz);
                built[11] = 0;
                built[12] = (float)tx;
                built[13] = (float)ty;
                built[14] = (float)tz;
                built[15] = 1;
                return new Placement(built);
            }

            /// <summary>This one applied after the one given, which is parent then child.</summary>
            public Placement then(Placement inner)
            {
                var a = _m ?? identity._m!;
                var b = inner._m ?? identity._m!;
                var o = new float[16];

                for (int column = 0; column < 4; column++)
                {
                    for (int row = 0; row < 4; row++)
                    {
                        float sum = 0;
                        for (int k = 0; k < 4; k++) { sum += a[k * 4 + row] * b[column * 4 + k]; }
                        o[column * 4 + row] = sum;
                    }
                }

                return new Placement(o);
            }

            public MeshGeometry.Position moves(MeshGeometry.Position p)
            {
                var m = _m;
                if (m == null) { return p; }

                return new MeshGeometry.Position(
                    m[0] * p.X + m[4] * p.Y + m[8] * p.Z + m[12],
                    m[1] * p.X + m[5] * p.Y + m[9] * p.Z + m[13],
                    m[2] * p.X + m[6] * p.Y + m[10] * p.Z + m[14]);
            }

            /// <summary>
            /// A direction turned by the same transform, without being moved by it.
            ///
            /// The rotation part alone and then normalised, which is right for a rotation and for
            /// scaling that is the same on every axis. Scaling that is not - a model squashed on
            /// one axis only - wants the inverse transpose instead, and gets a normal a few degrees
            /// out without it. That is a lighting error on an unusual model rather than a shape
            /// error on an ordinary one, so it is named rather than solved.
            /// </summary>
            public VertexPacking.Direction turns(VertexPacking.Direction d)
            {
                var m = _m;
                if (m == null) { return d; }

                var x = m[0] * d.X + m[4] * d.Y + m[8] * d.Z;
                var y = m[1] * d.X + m[5] * d.Y + m[9] * d.Z;
                var z = m[2] * d.X + m[6] * d.Y + m[10] * d.Z;

                var length = Math.Sqrt((double)x * x + (double)y * y + (double)z * z);
                if (length < 1e-6) { return d; }

                return new VertexPacking.Direction(
                    (float)(x / length), (float)(y / length), (float)(z / length), d.W);
            }
        }

        private static double floatOf(JsonNode? node)
        {
            try { return node?.GetValue<double>() ?? 0.0; }
            catch (Exception) { return 0.0; }
        }

        /// <summary>
        /// A texture made out of the flat colours a model's materials declare, with the texture
        /// coordinates rewritten to point at them.
        ///
        /// Plenty of models carry no image at all. Everything a modelling tool exports from a
        /// low-poly scene tends to look like this: a handful of materials, each a single
        /// `baseColorFactor`, and no picture anywhere. Those used to import as a shape with no
        /// artwork, which is not a neutral result - the mesh then samples whatever texture the
        /// thing it replaced was using, through coordinates meant for a different layout, so it
        /// comes out in the old colours and transparent wherever the old artwork was empty.
        ///
        /// So the colours are turned into the smallest picture that can hold them: one cell per
        /// material in a square grid, and every vertex pointed at the middle of its own cell. The
        /// middle rather than anywhere in it, because this gets scaled up to whatever size the
        /// texture it replaces happens to be, and the blend that scaling does reaches in from the
        /// edges. The model's own coordinates are discarded, which costs nothing: without an image
        /// they were not addressing anything.
        ///
        /// Emissive is taken when there is no base colour, because a model that describes itself
        /// only by what it glows - a coin, a star - would otherwise come out black.
        /// </summary>
        private static byte[]? paletteFor(JsonObject json,
            List<(int at, int count, int material)> runs, List<(float, float)> texCoords)
        {
            if (!(json["materials"] is JsonArray materials) || materials.Count == 0) { return null; }

            var colours = new List<(byte b, byte g, byte r, byte a)>();
            var anyColour = false;
            foreach (var material in materials)
            {
                var found = colourOf(material as JsonObject);
                anyColour |= found != null;
                colours.Add(found ?? WHITE);
            }

            //Nothing said anything about colour, so there is nothing to bake and a flat white
            //picture would be a worse answer than none: it would hide that the model has no
            //artwork rather than showing it.
            if (!anyColour) { return null; }

            //One spare cell at the end for any run that names no material at all.
            colours.Add(WHITE);

            var side = (int)Math.Ceiling(Math.Sqrt(colours.Count));
            if (side < 1) { return null; }

            //Each cell is painted as a block rather than as a single pixel.
            //
            //A palette one pixel per colour is the right *idea* and the wrong *file*, because it
            //never reaches the game at that size - it is resized to whatever the texture it
            //replaces happens to be, and a resize of single pixels is a gradient with no pure
            //colour left in it except at the corners. Which is not where the coordinates point:
            //they point at cell centres, so every material ended up sampling a blend, and a
            //palette with white in half its cells blends to white. That is a white fish.
            //
            //Blocks survive it. Sixty four pixels a side is far more than any of these textures
            //need - some of this game's weapons are dressed by a twenty five pixel colour ramp -
            //so the resize is a reduction, and a reduction of a solid block is that solid colour
            //everywhere except its edges. The middle, which is what gets sampled, stays pure.
            const int cell = 64;
            var width = side * cell;
            var stride = width * 4;
            var pixels = new byte[stride * width];

            //Opaque white everywhere first, so the cells the grid does not fill are not holes.
            //A square number of cells rarely matches the number of materials, and leaving the
            //remainder at nothing would put fully transparent patches next to real colours - which
            //the scaling up to the game texture's size would then blend outwards into them.
            for (int i = 0; i < pixels.Length; i++) { pixels[i] = 255; }
            for (int i = 0; i < colours.Count; i++)
            {
                var (b, g, r, a) = colours[i];
                var left = (i % side) * cell;
                var top = (i / side) * cell;

                for (int y = top; y < top + cell; y++)
                {
                    for (int x = left; x < left + cell; x++)
                    {
                        var at = y * stride + x * 4;
                        pixels[at] = b;
                        pixels[at + 1] = g;
                        pixels[at + 2] = r;
                        pixels[at + 3] = a;
                    }
                }
            }

            foreach (var (from, count, material) in runs)
            {
                var slot = material >= 0 && material < colours.Count - 1 ? material : colours.Count - 1;
                var u = ((slot % side) + 0.5f) / side;
                var v = ((slot / side) + 0.5f) / side;

                for (int i = from; i < from + count && i < texCoords.Count; i++)
                {
                    texCoords[i] = (u, v);
                }
            }

            var bitmap = BitmapSource.Create(width, width, 96, 96, PixelFormats.Bgra32, null, pixels, stride);
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(bitmap));

            using var stream = new MemoryStream();
            encoder.Save(stream);
            return stream.ToArray();
        }

        private static readonly (byte b, byte g, byte r, byte a) WHITE = (255, 255, 255, 255);

        /// <summary>One material's flat colour, or nothing when it does not declare one.</summary>
        private static (byte b, byte g, byte r, byte a)? colourOf(JsonObject? material)
        {
            if (material == null) { return null; }

            var factor = material["pbrMetallicRoughness"]?["baseColorFactor"] as JsonArray;
            if (factor == null || factor.Count < 3)
            {
                factor = material["emissiveFactor"] as JsonArray;
                if (factor == null || factor.Count < 3) { return null; }
            }

            //Alpha straight across. It is a coverage rather than a colour, so the curve the three
            //colour channels go through would be wrong for it - a half transparent material would
            //come out three quarters opaque.
            return (channel(factor, 2), channel(factor, 1), channel(factor, 0),
                factor.Count > 3 ? plain(factor, 3) : (byte)255);
        }

        /// <summary>
        /// One channel, from glTF's nought to one into a byte.
        ///
        /// glTF colours are linear and a texture the engine samples is sRGB, so the value is
        /// converted rather than multiplied by 255. Skipping that step is what makes a baked
        /// colour come out visibly darker than the model looked in the tool it came from.
        /// </summary>
        private static byte plain(JsonArray factor, int index)
        {
            try
            {
                var value = factor[index]?.GetValue<double>() ?? 1.0;
                return (byte)Math.Round(Math.Max(0.0, Math.Min(1.0, value)) * 255.0);
            }
            catch (Exception) { return 255; }
        }

        private static byte channel(JsonArray factor, int index)
        {
            var value = 0.0;
            try { value = factor[index]?.GetValue<double>() ?? 0.0; }
            catch (Exception) { return 255; }

            value = Math.Max(0.0, Math.Min(1.0, value));

            var srgb = value <= 0.0031308
                ? value * 12.92
                : 1.055 * Math.Pow(value, 1.0 / 2.4) - 0.055;

            return (byte)Math.Round(Math.Max(0.0, Math.Min(1.0, srgb)) * 255.0);
        }

        private static (int offset, int length) viewOf(JsonArray views, int index)
        {
            if (index < 0 || index >= views.Count) { return (-1, 0); }
            var view = views[index] as JsonObject;
            return (intOf(view?["byteOffset"], 0), intOf(view?["byteLength"], 0));
        }

        /// <summary>
        /// One accessor's worth of vectors.
        ///
        /// The stride is honoured rather than assumed: an exporter is free to interleave several
        /// attributes in one buffer, and reading such a file as though it were packed tightly
        /// produces geometry that is subtly scrambled rather than obviously broken.
        /// </summary>
        private static List<float[]> readVectors(JsonArray accessors, JsonArray views, byte[] binary, int index, int components)
        {
            var result = new List<float[]>();
            if (index < 0 || index >= accessors.Count) { return result; }

            var accessor = accessors[index] as JsonObject;
            var count = intOf(accessor?["count"], 0);
            var componentType = intOf(accessor?["componentType"], FLOAT);
            var normalised = (accessor?["normalized"] as JsonValue)?.GetValue<bool>() ?? false;

            var viewIndex = intOf(accessor?["bufferView"], -1);
            if (viewIndex < 0) { return result; }

            var (viewOffset, viewLength) = viewOf(views, viewIndex);
            var view = views[viewIndex] as JsonObject;
            var stride = intOf(view?["byteStride"], 0);
            var start = viewOffset + intOf(accessor?["byteOffset"], 0);

            var componentSize = sizeOf(componentType);
            if (componentSize == 0) { return result; }
            if (stride == 0) { stride = componentSize * components; }

            for (int i = 0; i < count; i++)
            {
                var at = start + i * stride;
                if (at + componentSize * components > binary.Length) { break; }

                var vector = new float[components];
                for (int c = 0; c < components; c++)
                {
                    vector[c] = readComponent(binary, at + c * componentSize, componentType, normalised);
                }
                result.Add(vector);
            }
            return result;
        }

        private static List<int> readScalars(JsonArray accessors, JsonArray views, byte[] binary, int index)
        {
            var result = new List<int>();
            if (index < 0 || index >= accessors.Count) { return result; }

            var accessor = accessors[index] as JsonObject;
            var count = intOf(accessor?["count"], 0);
            var componentType = intOf(accessor?["componentType"], UNSIGNED_INT);

            var viewIndex = intOf(accessor?["bufferView"], -1);
            if (viewIndex < 0) { return result; }

            var (viewOffset, _) = viewOf(views, viewIndex);
            var view = views[viewIndex] as JsonObject;
            var componentSize = sizeOf(componentType);
            if (componentSize == 0) { return result; }

            var stride = intOf(view?["byteStride"], 0);
            if (stride == 0) { stride = componentSize; }
            var start = viewOffset + intOf(accessor?["byteOffset"], 0);

            for (int i = 0; i < count; i++)
            {
                var at = start + i * stride;
                if (at + componentSize > binary.Length) { break; }

                switch (componentType)
                {
                    case UNSIGNED_BYTE: result.Add(binary[at]); break;
                    case UNSIGNED_SHORT: result.Add(BitConverter.ToUInt16(binary, at)); break;
                    case SHORT: result.Add(BitConverter.ToInt16(binary, at)); break;
                    default: result.Add(BitConverter.ToInt32(binary, at)); break;
                }
            }
            return result;
        }

        private static float readComponent(byte[] binary, int at, int componentType, bool normalised)
        {
            switch (componentType)
            {
                case FLOAT: return BitConverter.ToSingle(binary, at);
                case UNSIGNED_BYTE: return normalised ? binary[at] / 255f : binary[at];
                case BYTE:
                    var signedByte = (sbyte)binary[at];
                    return normalised ? Math.Max(signedByte / 127f, -1f) : signedByte;
                case UNSIGNED_SHORT:
                    var word = BitConverter.ToUInt16(binary, at);
                    return normalised ? word / 65535f : word;
                case SHORT:
                    var signedWord = BitConverter.ToInt16(binary, at);
                    return normalised ? Math.Max(signedWord / 32767f, -1f) : signedWord;
                default: return 0f;
            }
        }

        private static int sizeOf(int componentType)
        {
            switch (componentType)
            {
                case BYTE:
                case UNSIGNED_BYTE: return 1;
                case SHORT:
                case UNSIGNED_SHORT: return 2;
                case UNSIGNED_INT:
                case FLOAT: return 4;
                default: return 0;
            }
        }

        /// <summary>
        /// A number out of the JSON, whatever shape the parser gave it.
        ///
        /// Parsed nodes come back backed by a JsonElement that will convert between number types
        /// on request, but asking for the wrong one throws rather than converting - so the type is
        /// tried rather than assumed.
        /// </summary>
        private static int intOf(JsonNode? node, int fallback)
        {
            if (!(node is JsonValue value)) { return fallback; }
            if (value.TryGetValue<int>(out var whole)) { return whole; }
            if (value.TryGetValue<long>(out var big)) { return (int)big; }
            if (value.TryGetValue<double>(out var real)) { return (int)real; }
            return fallback;
        }
    }
}
