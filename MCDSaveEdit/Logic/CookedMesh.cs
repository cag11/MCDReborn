using System;
using System.Collections.Generic;
using System.IO;
#nullable enable

namespace MCDSaveEdit.Logic
{
    /// <summary>
    /// A cooked StaticMesh taken apart far enough to put a different model in it.
    ///
    /// The approach throughout has been to rewrite what is understood and carry everything else
    /// across untouched, and that continues here - but the stakes change. Reshaping kept the
    /// vertex count, so nothing moved and no offset went stale. Replacing the geometry does not:
    /// the file changes length, which means the section that describes the triangles is wrong, the
    /// bounds are wrong, and the export table in the `.uasset` is wrong about how long the export
    /// is. Those are the three corrections, and missing any one produces a file the game rejects.
    ///
    /// The layout is not walked from the front. The position buffer is found by matching it
    /// against the bounds the export declares, and everything else is located relative to it -
    /// the section header sits thirty six bytes before it, the tangents and UVs immediately after.
    /// Every one of those is checked against something already known before it is believed. A
    /// section whose triangle count disagrees with the index buffer, or whose highest vertex is
    /// not one less than the vertex count, means this is not the layout being assumed, and the
    /// answer is to refuse rather than to write a file that is subtly wrong.
    /// </summary>
    public sealed class CookedMesh
    {
        //Where each field of the single FStaticMeshSection sits, counting back from the start of
        //the position buffer. Confirmed against the game's own Claymore rather than assumed.
        private const int SECTION_COUNT = -36;
        private const int MATERIAL_INDEX = -32;
        private const int FIRST_INDEX = -28;
        private const int NUM_TRIANGLES = -24;
        private const int MIN_VERTEX = -20;
        private const int MAX_VERTEX = -16;

        //And in the .uasset, the two numbers that have to move when the export changes length.
        private const int BULK_DATA_START_OFFSET = 169;
        private const int EXPORT_ENTRY_SIZE = 104;
        private const int SERIAL_SIZE_IN_ENTRY = 28;
        private const int SERIAL_OFFSET_IN_ENTRY = 36;

        private CookedMesh(byte[] uasset, byte[] uexp, MeshGeometry.Vertices positions,
            MeshGeometry.Attributes attributes, IReadOnlyList<MeshGeometry.Triangles> indexBuffers,
            int positionBufferStart, int boundsBlock,
            MeshGeometry.Position origin, MeshGeometry.Position extent, float radius)
        {
            _origin = origin;
            _extent = extent;
            _radius = radius;
            UAsset = uasset;
            UExp = uexp;
            Positions = positions;
            Attributes = attributes;
            IndexBuffers = indexBuffers;
            PositionBufferStart = positionBufferStart;
            BoundsBlock = boundsBlock;
        }

        private readonly MeshGeometry.Position _origin;
        private readonly MeshGeometry.Position _extent;
        private readonly float _radius;

        public byte[] UAsset { get; }
        public byte[] UExp { get; }
        public MeshGeometry.Vertices Positions { get; }
        public MeshGeometry.Attributes Attributes { get; }
        public IReadOnlyList<MeshGeometry.Triangles> IndexBuffers { get; }
        public int PositionBufferStart { get; }
        /// <summary>The contiguous FBoxSphereBounds in the render data, or -1 if it was not found.</summary>
        public int BoundsBlock { get; }

        private readonly List<(int at, int length)> _boundsWrites = new List<(int, int)>();

        /// <summary>
        /// Where the last rebuild wrote bounds.
        ///
        /// Recorded rather than inferred, so a check on the result can say "these bytes, and only
        /// these, were allowed to move" instead of working out from the values which ones were
        /// meant to. Working it out is how thousands of wrong index bytes went unnoticed behind a
        /// pair of numbers that both happened to be almost zero.
        /// </summary>
        public IReadOnlyList<(int at, int length)> BoundsWrites => _boundsWrites;

        public int VertexCount => Positions.Count;
        public int TriangleCount => IndexBuffers.Count > 0 ? IndexBuffers[0].Count : 0;

        /// <summary>
        /// Takes the mesh apart, or refuses.
        ///
        /// Refusing is the important half. Every mesh in the game reads, but not every mesh has
        /// the shape this writer can rebuild - several materials means several sections, and a
        /// section list this does not understand is a section list it must not rewrite.
        /// </summary>
        public static CookedMesh? open(byte[] uasset, byte[] uexp,
            MeshGeometry.Position origin, MeshGeometry.Position extent, float radius)
        {
            var positions = MeshGeometry.findPositions(uexp, origin, extent);
            if (positions == null) { return null; }

            var attributes = MeshGeometry.findAttributes(uexp, positions);
            if (attributes == null) { return null; }

            var indexBuffers = MeshGeometry.findTriangles(uexp, positions.Count, attributes.DataEnd);
            if (indexBuffers.Count == 0) { return null; }

            var start = positions.HeaderOffset - 8;
            if (start + SECTION_COUNT < 0) { return null; }

            //One section, one material. Anything else and this is not a mesh to rebuild.
            if (readInt(uexp, start + SECTION_COUNT) != 1) { return null; }
            if (readInt(uexp, start + MATERIAL_INDEX) != 0) { return null; }
            if (readInt(uexp, start + FIRST_INDEX) != 0) { return null; }
            if (readInt(uexp, start + MIN_VERTEX) != 0) { return null; }

            //The checks that say the section really is describing this geometry.
            if (readInt(uexp, start + NUM_TRIANGLES) != indexBuffers[0].Count) { return null; }
            if (readInt(uexp, start + MAX_VERTEX) != positions.Count - 1) { return null; }

            //And the position buffer's own two headers, which must agree with each other.
            if (readInt(uexp, start) != 12 || readInt(uexp, start + 4) != positions.Count) { return null; }

            var bounds = findBoundsBlock(uexp, origin, extent, radius, positions);

            return new CookedMesh(uasset, uexp, positions, attributes, indexBuffers, start, bounds,
                origin, extent, radius);
        }

        /// <summary>New geometry, ready to be written into a cooked mesh.</summary>
        public sealed class Geometry
        {
            public IReadOnlyList<MeshGeometry.Position> Positions { get; set; } = Array.Empty<MeshGeometry.Position>();
            public IReadOnlyList<VertexPacking.Direction> Normals { get; set; } = Array.Empty<VertexPacking.Direction>();
            public IReadOnlyList<VertexPacking.Direction> Tangents { get; set; } = Array.Empty<VertexPacking.Direction>();
            /// <summary>One pair per vertex per channel, channel 0 first.</summary>
            public IReadOnlyList<(float u, float v)> TexCoords { get; set; } = Array.Empty<(float, float)>();
            public IReadOnlyList<int> Indices { get; set; } = Array.Empty<int>();

            public int Count => Positions.Count;
        }

        /// <summary>
        /// The mesh's own geometry, read back out. Used to prove the writer before it is trusted:
        /// what comes out of here, put back through <see cref="rebuild"/>, must reproduce the file.
        /// </summary>
        public Geometry readGeometry()
        {
            var positions = MeshGeometry.readPositions(UExp, Positions, Positions.Count);

            var normals = new List<VertexPacking.Direction>(Positions.Count);
            var tangents = new List<VertexPacking.Direction>(Positions.Count);
            for (int i = 0; i < Positions.Count; i++)
            {
                var at = Attributes.TangentsOffset + i * Attributes.TangentStride;
                //Tangent first, then normal. That is the order the engine writes them in, and
                //swapping them produces lighting that is wrong in a way that looks like a
                //modelling mistake rather than a reader one.
                tangents.Add(VertexPacking.unpackNormal(UExp, at));
                normals.Add(VertexPacking.unpackNormal(UExp, at + 4));
            }

            var texCoords = new List<(float, float)>(Positions.Count * Attributes.TexCoords);
            for (int i = 0; i < Positions.Count * Attributes.TexCoords; i++)
            {
                var at = Attributes.UVsOffset + i * Attributes.UVStride;
                texCoords.Add((
                    VertexPacking.unpackHalf(BitConverter.ToUInt16(UExp, at)),
                    VertexPacking.unpackHalf(BitConverter.ToUInt16(UExp, at + 2))));
            }

            var indices = new List<int>();
            foreach (var (a, b, c) in MeshGeometry.readTriangles(UExp, IndexBuffers[0], IndexBuffers[0].Count))
            {
                indices.Add(a);
                indices.Add(b);
                indices.Add(c);
            }

            return new Geometry {
                Positions = positions,
                Normals = normals,
                Tangents = tangents,
                TexCoords = texCoords,
                Indices = indices,
            };
        }

        /// <summary>
        /// A new `.uasset` and `.uexp` holding the given geometry in place of the original's.
        ///
        /// Everything between the buffers is copied rather than regenerated - the colour buffer,
        /// the flags, the gaps between index buffers, the whole property list at the front. Those
        /// are fixed width and do not care how many vertices there are, and copying them is both
        /// safer than rewriting them and the only reason this is possible without understanding
        /// the entire format.
        /// </summary>
        public (byte[] uasset, byte[] uexp) rebuild(Geometry geometry)
        {
            if (geometry.Count == 0) { throw new InvalidOperationException("That model has no vertices."); }
            if (geometry.Indices.Count % 3 != 0) { throw new InvalidOperationException("That model's triangles are incomplete."); }
            if (geometry.TexCoords.Count != geometry.Count * Attributes.TexCoords)
            {
                throw new InvalidOperationException(
                    $"That model needs {Attributes.TexCoords} texture coordinate set(s) per vertex.");
            }

            _boundsWrites.Clear();

            var count = geometry.Count;
            var triangles = geometry.Indices.Count / 3;
            var sameTriangles = unchangedTriangles(geometry);

            //Two bytes per index until a mesh outgrows what two can count, exactly as the engine
            //decides it.
            var wide = count > ushort.MaxValue;
            var indexSize = wide ? 4 : 2;

            using var built = new MemoryStream();
            using var write = new BinaryWriter(built);

            //Everything before the position buffer, with the section corrected to describe the new
            //geometry.
            var head = new byte[PositionBufferStart];
            Buffer.BlockCopy(UExp, 0, head, 0, head.Length);
            writeInt(head, PositionBufferStart + NUM_TRIANGLES, triangles);
            writeInt(head, PositionBufferStart + MAX_VERTEX, count - 1);
            write.Write(head);

            //FPositionVertexBuffer: stride and count, then the same again for the array itself.
            write.Write(12);
            write.Write(count);
            write.Write(12);
            write.Write(count);
            foreach (var position in geometry.Positions)
            {
                write.Write(position.X);
                write.Write(position.Y);
                write.Write(position.Z);
            }

            //The two strip-flag bytes between the buffers, carried across.
            write.Write(UExp, Positions.DataEnd, 2);

            //FStaticMeshVertexBuffer.
            write.Write(Attributes.TexCoords);
            write.Write(count);
            write.Write(Attributes.FullPrecisionUVs ? 1 : 0);
            write.Write(Attributes.HighPrecisionTangents ? 1 : 0);
            write.Write(Attributes.TangentStride);
            write.Write(count);

            var tangentBytes = new byte[Attributes.TangentStride];
            for (int i = 0; i < count; i++)
            {
                Array.Clear(tangentBytes, 0, tangentBytes.Length);
                VertexPacking.packNormal(at(geometry.Tangents, i), tangentBytes, 0);
                VertexPacking.packNormal(at(geometry.Normals, i), tangentBytes, 4);
                write.Write(tangentBytes);
            }

            write.Write(Attributes.UVStride);
            write.Write(count * Attributes.TexCoords);
            foreach (var (u, v) in geometry.TexCoords)
            {
                if (Attributes.FullPrecisionUVs)
                {
                    write.Write(u);
                    write.Write(v);
                }
                else
                {
                    write.Write(VertexPacking.packHalf(u));
                    write.Write(VertexPacking.packHalf(v));
                }
            }

            //Whatever sits between the vertex data and the first index buffer - the colour buffer,
            //which is empty, and a handful of flags.
            var firstBuffer = IndexBuffers[0];
            write.Write(UExp, Attributes.DataEnd, firstBuffer.HeaderOffset - Attributes.DataEnd);

            //The index buffers. The game keeps the same triangles several times over - the mesh,
            //a reversed copy for mirrored meshes, and a depth only pair for shadows - and the same
            //data is written into each. The adjacency buffer is written empty: it holds twelve
            //indices per triangle for tessellation, which nothing in this game's weapon materials
            //asks for, and generating it would be a great deal of work for an effect that is not
            //used.
            for (int i = 0; i < IndexBuffers.Count; i++)
            {
                var buffer = IndexBuffers[i];
                if (i > 0)
                {
                    //The few bytes separating one buffer from the next, carried across.
                    var previous = IndexBuffers[i - 1];
                    write.Write(UExp, previous.DataEnd, buffer.HeaderOffset - previous.DataEnd);
                }

                //Every one of these buffers is derived from the triangles, so if the triangles
                //have not changed then none of them has. Copying rather than regenerating is not
                //just an optimisation: the depth only pair hold their own reduced index data -
                //254 of the Claymore's 674 vertices, not all of them - and writing the mesh's
                //indices into them would be writing something the game did not have there.
                if (sameTriangles)
                {
                    write.Write(1);
                    write.Write(buffer.ByteCount);
                    write.Write(UExp, buffer.DataOffset, buffer.ByteCount);
                    continue;
                }

                if (!MeshGeometry.isTriangleList(buffer))
                {
                    //The adjacency buffer describes the same triangles, twelve indices each, so
                    //that tessellation can see a triangle's neighbours. If the triangles have not
                    //changed then neither has it, and the original is still correct - which is
                    //also what lets a mesh rebuilt from its own geometry come back byte for byte.
                    //
                    //When the triangles have changed it is written empty rather than regenerated.
                    //Building it means finding, for every edge, the triangle on the other side,
                    //and nothing in this game's weapon materials asks for tessellation - so an
                    //empty buffer costs an effect that is not used, and a wrong one would cost
                    //correctness.
                    write.Write(1);
                    write.Write(0);
                    continue;
                }

                //A reversed copy really is reversed: the engine uses it when a mesh is mirrored,
                //and handing it the same winding as the mesh would light the mirrored version
                //inside out. The depth only pair are given the full triangles rather than a
                //reduced set - larger than the game's own, and correct, which is the right way
                //round to be wrong.
                var reversed = i == 1;

                write.Write(1);
                write.Write(geometry.Indices.Count * indexSize);
                for (int t = 0; t < geometry.Indices.Count; t += 3)
                {
                    var a = geometry.Indices[t];
                    var b = geometry.Indices[t + 1];
                    var c = geometry.Indices[t + 2];
                    if (reversed) { (a, c) = (c, a); }

                    if (wide) { write.Write(a); write.Write(b); write.Write(c); }
                    else { write.Write((ushort)a); write.Write((ushort)b); write.Write((ushort)c); }
                }
            }

            //Everything after the last index buffer, which holds the render data's own bounds.
            var last = IndexBuffers[IndexBuffers.Count - 1];
            var tailStart = last.DataEnd;
            var tail = new byte[UExp.Length - tailStart];
            Buffer.BlockCopy(UExp, tailStart, tail, 0, tail.Length);
            var tailLandsAt = (int)built.Position;
            if (BoundsBlock >= tailStart)
            {
                var within = BoundsBlock - tailStart;
                writeBounds(tail, within, geometry.Positions);
                _boundsWrites.Add((tailLandsAt + within, 28));
            }
            write.Write(tail);

            var uexp = built.ToArray();

            //And the header, which records how long the export is. The StaticMesh is the last
            //export, so nothing after it needs moving - only its own size and the offset that
            //marks where bulk data would start.
            var uasset = (byte[])UAsset.Clone();
            correctHeader(uasset, uexp.Length - UExp.Length);

            //The tagged bounds at the front of the export travel in the uexp, so they are fixed
            //there rather than here.
            fixTaggedBounds(uexp, geometry.Positions);

            return (uasset, uexp);
        }


        /// <summary>
        /// Whether the geometry being written has the same triangles the file already holds.
        ///
        /// Asked so that data derived from the triangles - the adjacency buffer - can be kept
        /// rather than discarded when nothing about them has changed.
        /// </summary>
        private bool unchangedTriangles(Geometry geometry)
        {
            if (IndexBuffers.Count == 0) { return false; }
            if (geometry.Count != Positions.Count) { return false; }

            var original = IndexBuffers[0];
            if (geometry.Indices.Count != original.IndexCount) { return false; }

            var i = 0;
            foreach (var (a, b, c) in MeshGeometry.readTriangles(UExp, original, original.Count))
            {
                if (geometry.Indices[i] != a || geometry.Indices[i + 1] != b || geometry.Indices[i + 2] != c)
                {
                    return false;
                }
                i += 3;
            }
            return true;
        }

        private static VertexPacking.Direction at(IReadOnlyList<VertexPacking.Direction> list, int index)
        {
            //A model without tangents is not an error worth refusing over: a flat direction is
            //wrong but harmless, where refusing would block a model that is otherwise fine.
            return index < list.Count ? list[index] : new VertexPacking.Direction(0, 0, 1, 1);
        }

        /// <summary>
        /// Moves the export's recorded length, and the bulk data marker after it, by however much
        /// the export grew or shrank.
        ///
        /// Only the last export needs this. Were the mesh not last, every export after it would
        /// need its offset moved too - so that is checked rather than assumed.
        /// </summary>
        private void correctHeader(byte[] uasset, int delta)
        {
            if (delta == 0) { return; }

            var exportCount = exportTableCount(uasset, out var exportOffset);
            if (exportCount <= 0) { throw new InvalidOperationException("Could not read the export table."); }

            var lastEntry = exportOffset + (exportCount - 1) * EXPORT_ENTRY_SIZE;
            if (lastEntry + SERIAL_OFFSET_IN_ENTRY + 8 > uasset.Length)
            {
                throw new InvalidOperationException("The export table is not where the header says it is.");
            }

            //The mesh has to be the last export for this to be the whole correction.
            var biggest = 0L;
            var biggestEntry = -1;
            for (int i = 0; i < exportCount; i++)
            {
                var entry = exportOffset + i * EXPORT_ENTRY_SIZE;
                var size = BitConverter.ToInt64(uasset, entry + SERIAL_SIZE_IN_ENTRY);
                if (size > biggest) { biggest = size; biggestEntry = entry; }
            }
            if (biggestEntry != lastEntry)
            {
                throw new InvalidOperationException("The mesh is not the last export, so more offsets would need moving.");
            }

            var serialSize = BitConverter.ToInt64(uasset, lastEntry + SERIAL_SIZE_IN_ENTRY);
            writeLong(uasset, lastEntry + SERIAL_SIZE_IN_ENTRY, serialSize + delta);

            var bulkStart = BitConverter.ToInt64(uasset, BULK_DATA_START_OFFSET);
            writeLong(uasset, BULK_DATA_START_OFFSET, bulkStart + delta);
        }

        /// <summary>
        /// How many exports there are and where the table begins.
        ///
        /// Read from the summary rather than searched for, because this part of the header is one
        /// of the few things in a cooked package laid out at a fixed place.
        /// </summary>
        private static int exportTableCount(byte[] uasset, out int exportOffset)
        {
            exportOffset = 0;
            if (uasset.Length < 64) { return 0; }

            //The summary's export count and offset sit together, after the name and gatherable
            //text entries. Located by stepping the same fields the describer already reads.
            using var stream = new MemoryStream(uasset);
            using var reader = new BinaryReader(stream);

            if (reader.ReadUInt32() != 0x9E2A83C1) { return 0; }
            var legacy = reader.ReadInt32();
            if (legacy != -4) { reader.ReadInt32(); }
            reader.ReadInt32(); // ue4 version
            reader.ReadInt32(); // licensee version
            var customVersions = reader.ReadInt32();
            for (int i = 0; i < customVersions; i++) { reader.ReadBytes(20); }
            reader.ReadInt32(); // total header size
            skipString(reader);
            reader.ReadUInt32(); // package flags
            reader.ReadInt32(); // name count
            reader.ReadInt32(); // name offset
            reader.ReadInt32(); // gatherable text count
            reader.ReadInt32(); // gatherable text offset

            var exportCount = reader.ReadInt32();
            exportOffset = reader.ReadInt32();
            return exportCount;
        }

        private static void skipString(BinaryReader reader)
        {
            var length = reader.ReadInt32();
            if (length == 0) { return; }
            reader.ReadBytes(length < 0 ? -length * 2 : length);
        }

        /// <summary>
        /// Corrects the copy of the bounds kept in the property list at the front of the export.
        ///
        /// There are two copies and both matter: this one is what the property list reports, and
        /// the one in the render data is what the renderer culls against. Correcting only the
        /// second leaves a weapon that renders correctly and reports its old size, which goes
        /// unnoticed until something asks the mesh how big it is.
        ///
        /// The copies are found by what they currently hold rather than by an offset, because the
        /// property list omits anything equal to its default - a mesh centred on nothing has no
        /// Origin entry at all - so their positions are not fixed between one mesh and the next.
        /// </summary>
        private void fixTaggedBounds(byte[] uexp, IReadOnlyList<MeshGeometry.Position> positions)
        {
            var (origin, extent, radius) = measure(positions);
            var limit = Math.Min(PositionBufferStart, uexp.Length);

            //The extent is the anchor, and it has to be the unique one. It is the distinctive
            //member of the three: a box has a real width, where an origin is very often exactly
            //zero and a radius is a single number that other properties can happen to share.
            //Searching for those two on their own finds a dozen places in a mesh centred on the
            //origin, because every run of twelve zero bytes looks like one.
            var extentAt = onlyPlace(uexp, limit, 12, offset =>
                near(uexp, offset, _extent.X) && near(uexp, offset + 4, _extent.Y) && near(uexp, offset + 8, _extent.Z),
                "bounds extent");
            if (extentAt < 0) { return; }

            //Inside ExtendedBounds the three are written in order, each behind its own property
            //header, so the origin is the nearest match before the extent and the radius the
            //nearest after it. Nearest, rather than only, because it is their position relative to
            //a known landmark that identifies them - not their value.
            var originAt = nearestBefore(uexp, extentAt, 12, offset =>
                near(uexp, offset, _origin.X) && near(uexp, offset + 4, _origin.Y) && near(uexp, offset + 8, _origin.Z));
            var radiusAt = nearestAfter(uexp, extentAt + 12, limit, 4, offset => near(uexp, offset, _radius));

            writeFloat(uexp, extentAt, extent.X);
            writeFloat(uexp, extentAt + 4, extent.Y);
            writeFloat(uexp, extentAt + 8, extent.Z);
            _boundsWrites.Add((extentAt, 12));

            if (originAt >= 0)
            {
                writeFloat(uexp, originAt, origin.X);
                writeFloat(uexp, originAt + 4, origin.Y);
                writeFloat(uexp, originAt + 8, origin.Z);
                _boundsWrites.Add((originAt, 12));
            }

            if (radiusAt >= 0)
            {
                writeFloat(uexp, radiusAt, radius);
                _boundsWrites.Add((radiusAt, 4));
            }
        }

        //How far from the extent the other two members can sit. A property header carries a name,
        //a type, a length and a struct name, so a couple of hundred bytes is generous for one and
        //far short of reaching anything unrelated.
        private const int WITHIN_THE_STRUCT = 256;

        private static int nearestBefore(byte[] uexp, int anchor, int width, Func<int, bool> matches)
        {
            var from = Math.Max(0, anchor - WITHIN_THE_STRUCT);
            for (int at = anchor - width; at >= from; at--)
            {
                if (matches(at)) { return at; }
            }
            return -1;
        }

        private static int nearestAfter(byte[] uexp, int anchor, int limit, int width, Func<int, bool> matches)
        {
            var to = Math.Min(limit - width, anchor + WITHIN_THE_STRUCT);
            for (int at = anchor; at <= to; at++)
            {
                if (matches(at)) { return at; }
            }
            return -1;
        }

        /// <summary>
        /// The single offset matching, or nothing.
        ///
        /// Nothing found is allowed: a tagged property list leaves out anything equal to its
        /// default. Several found is refused, because then this cannot say which is the real one
        /// and writing to all of them would corrupt whatever the others are.
        /// </summary>
        private static int onlyPlace(byte[] uexp, int limit, int width, Func<int, bool> matches, string what)
        {
            var found = -1;
            for (int at = 0; at + width <= limit; at++)
            {
                if (!matches(at)) { continue; }
                if (found >= 0)
                {
                    throw new InvalidOperationException(
                        $"The {what} appears more than once in this mesh's properties, so it cannot be corrected safely.");
                }
                found = at;
            }
            return found;
        }

        private static bool near(byte[] uexp, int at, float wanted)
        {
            var found = BitConverter.ToSingle(uexp, at);
            if (float.IsNaN(found) || float.IsInfinity(found)) { return false; }
            var slack = Math.Max(0.001f, Math.Abs(wanted) * 0.0005f);
            return Math.Abs(found - wanted) <= slack;
        }

        private static void writeBounds(byte[] destination, int at, IReadOnlyList<MeshGeometry.Position> positions)
        {
            if (at < 0 || at + 28 > destination.Length) { return; }

            var (origin, extent, radius) = measure(positions);
            writeFloat(destination, at, origin.X);
            writeFloat(destination, at + 4, origin.Y);
            writeFloat(destination, at + 8, origin.Z);
            writeFloat(destination, at + 12, extent.X);
            writeFloat(destination, at + 16, extent.Y);
            writeFloat(destination, at + 20, extent.Z);
            writeFloat(destination, at + 24, radius);
        }

        /// <summary>
        /// The box and sphere that hold a set of points.
        ///
        /// The sphere is measured to the furthest vertex rather than to the corner of the box.
        /// That is the engine's own definition - on the Claymore the corner gives 94.45 where the
        /// game stores 90.76 - and getting it wrong the other way, too small, makes a weapon
        /// flicker out of existence at certain camera angles.
        /// </summary>
        public static (MeshGeometry.Position origin, MeshGeometry.Position extent, float radius) measure(
            IReadOnlyList<MeshGeometry.Position> positions)
        {
            if (positions.Count == 0)
            {
                return (new MeshGeometry.Position(0, 0, 0), new MeshGeometry.Position(0, 0, 0), 0f);
            }

            float lowX = float.MaxValue, lowY = float.MaxValue, lowZ = float.MaxValue;
            float highX = float.MinValue, highY = float.MinValue, highZ = float.MinValue;
            foreach (var position in positions)
            {
                lowX = Math.Min(lowX, position.X); highX = Math.Max(highX, position.X);
                lowY = Math.Min(lowY, position.Y); highY = Math.Max(highY, position.Y);
                lowZ = Math.Min(lowZ, position.Z); highZ = Math.Max(highZ, position.Z);
            }

            var origin = new MeshGeometry.Position((lowX + highX) / 2f, (lowY + highY) / 2f, (lowZ + highZ) / 2f);
            var extent = new MeshGeometry.Position((highX - lowX) / 2f, (highY - lowY) / 2f, (highZ - lowZ) / 2f);

            var furthest = 0.0;
            foreach (var position in positions)
            {
                var dx = position.X - origin.X;
                var dy = position.Y - origin.Y;
                var dz = position.Z - origin.Z;
                furthest = Math.Max(furthest, Math.Sqrt(dx * dx + dy * dy + dz * dz));
            }

            return (origin, extent, (float)furthest);
        }

        private static int findBoundsBlock(byte[] uexp, MeshGeometry.Position origin,
            MeshGeometry.Position extent, float radius, MeshGeometry.Vertices positions)
        {
            var wanted = new[] { origin.X, origin.Y, origin.Z, extent.X, extent.Y, extent.Z, radius };
            for (int at = 0; at + 28 <= uexp.Length; at++)
            {
                if (at >= positions.DataOffset && at < positions.DataEnd) { continue; }

                var matches = true;
                for (int i = 0; i < 7; i++)
                {
                    var found = BitConverter.ToSingle(uexp, at + i * 4);
                    var slack = Math.Max(0.001f, Math.Abs(wanted[i]) * 0.0005f);
                    if (float.IsNaN(found) || Math.Abs(found - wanted[i]) > slack) { matches = false; break; }
                }
                if (matches) { return at; }
            }
            return -1;
        }

        private static int readInt(byte[] source, int at) => BitConverter.ToInt32(source, at);

        private static void writeInt(byte[] destination, int at, int value)
            => Buffer.BlockCopy(BitConverter.GetBytes(value), 0, destination, at, 4);

        private static void writeLong(byte[] destination, int at, long value)
            => Buffer.BlockCopy(BitConverter.GetBytes(value), 0, destination, at, 8);

        private static void writeFloat(byte[] destination, int at, float value)
            => Buffer.BlockCopy(BitConverter.GetBytes(value), 0, destination, at, 4);
    }
}
