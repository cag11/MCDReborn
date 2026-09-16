using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
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
            byte[]? baseColourPng)
        {
            Name = name;
            Positions = positions;
            Normals = normals;
            Tangents = tangents;
            TexCoords = texCoords;
            Indices = indices;
            BaseColourPng = baseColourPng;
        }

        public string Name { get; }
        public IReadOnlyList<MeshGeometry.Position> Positions { get; }
        public IReadOnlyList<VertexPacking.Direction> Normals { get; }
        public IReadOnlyList<VertexPacking.Direction> Tangents { get; }
        public IReadOnlyList<(float u, float v)> TexCoords { get; }
        public IReadOnlyList<int> Indices { get; }
        /// <summary>The base colour image, if the file carries one. The other maps have nowhere to go.</summary>
        public byte[]? BaseColourPng { get; }

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

            foreach (var meshNode in json["meshes"] as JsonArray ?? new JsonArray())
            {
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
                        positions.Add(new MeshGeometry.Position(value[0], value[1], value[2]));
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
                        normals.Add(normalValues != null && i < normalValues.Count
                            ? new VertexPacking.Direction(normalValues[i][0], normalValues[i][1], normalValues[i][2], 1f)
                            : new VertexPacking.Direction(0, 0, 1, 1));

                        tangents.Add(tangentValues != null && i < tangentValues.Count
                            ? new VertexPacking.Direction(tangentValues[i][0], tangentValues[i][1], tangentValues[i][2], tangentValues[i][3])
                            : new VertexPacking.Direction(1, 0, 0, 1));

                        texCoords.Add(uvValues != null && i < uvValues.Count
                            ? (uvValues[i][0], uvValues[i][1])
                            : (0f, 0f));
                    }

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

            return new GlbModel(name, positions, normals, tangents, texCoords, indices,
                findBaseColour(json, views, binary));
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
