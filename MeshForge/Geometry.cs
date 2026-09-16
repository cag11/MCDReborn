using System;
using System.Collections.Generic;
using System.Linq;

namespace MeshForge
{
    /// <summary>
    /// Finding the vertices and triangles in a cooked mesh, by looking for them rather than by
    /// knowing where they are.
    ///
    /// The geometry sits in `FStaticMeshRenderData`, which is written as raw structs in the order
    /// the engine expects to read them back, with no tags to navigate by. Walking that layout from
    /// the front means getting every field of every version check right before anything can be
    /// found at all, and being wrong is indistinguishable from the data being somewhere else.
    ///
    /// So this goes the other way round. A position buffer announces itself: a stride of twelve, a
    /// vertex count, and then that many groups of three floats. That shape alone is not enough -
    /// runs of packed tangent bytes read as tiny denormal floats and will happily pass - so the
    /// test is not that the numbers are plausible but that they are *these* numbers. The bounds
    /// were already read from the tagged property list at the front of the export, and the real
    /// buffer is the one whose own bounding box reproduces them. A box that agrees to five
    /// thousandths of a unit is not a coincidence.
    ///
    /// The index buffers are then checked against the vertex count, which is what turns a good
    /// guess into a settled fact: the triangles have to point at vertices that exist.
    /// </summary>
    public static class Geometry
    {
        /// <summary>Three floats, one vertex.</summary>
        public readonly struct Position
        {
            public Position(float x, float y, float z) { X = x; Y = y; Z = z; }
            public float X { get; }
            public float Y { get; }
            public float Z { get; }
            public override string ToString() => $"({X:0.###}, {Y:0.###}, {Z:0.###})";
        }

        public sealed class Vertices
        {
            public int HeaderOffset { get; set; }
            public int DataOffset { get; set; }
            public int Count { get; set; }
            public Position Min { get; set; }
            public Position Max { get; set; }
            /// <summary>How far the buffer's own box sits from the bounds the export declares.</summary>
            public double BoundsError { get; set; }
            public int DataEnd => DataOffset + Count * 12;
        }

        public sealed class Triangles
        {
            public int HeaderOffset { get; set; }
            public int DataOffset { get; set; }
            public int ByteCount { get; set; }
            /// <summary>Two bytes per index, or four once a mesh outgrows what two can count.</summary>
            public int IndexSize { get; set; } = 2;
            public int IndexCount => ByteCount / IndexSize;
            public int Count => IndexCount / 3;
            public int HighestIndex { get; set; }
            public int DistinctIndices { get; set; }
            public int DataEnd => DataOffset + ByteCount;
            /// <summary>Which of the several copies this one is.</summary>
            public string Role { get; set; } = "";
        }

        private const int POSITION_STRIDE = 12;

        //Three, because a triangle is the smallest thing a mesh can be and the game really does
        //ship four vertex quads - every blade of grass and every billboard card is one. Excluding
        //them loses real meshes, and the bounds test is strict enough to carry the small cases.
        private const int FEWEST_VERTICES = 3;
        private const int MOST_VERTICES = 500_000;

        /// <summary>
        /// The position buffer, identified by its box agreeing with the bounds.
        ///
        /// Every offset is tried rather than every fourth, because the buffer sits wherever the
        /// preceding fields leave it and there is no reason for that to be aligned. On the vanilla
        /// Claymore it lands on an odd byte, which is exactly the case an aligned scan misses.
        /// </summary>
        public static Vertices? findPositions(byte[] uexp, Position origin, Position extent)
        {
            //Most meshes span their declared box exactly, and that is the strongest anchor there
            //is, so it is tried first and on its own.
            var exact = search(uexp, origin, extent, exactly: true);
            if (exact != null) { return exact; }

            //Some do not, and they are not broken. A mesh whose vertices are moved by its material
            //- tickertape, banners, ivy, petals, hanging cable - has its bounds deliberately
            //padded by the artist so the game does not cull it the moment the wind pushes a corner
            //outside. For those the vertices sit inside the box rather than filling it, so the
            //test becomes containment plus filling enough of the box to not be a run of noise.
            return search(uexp, origin, extent, exactly: false);
        }

        private static Vertices? search(byte[] uexp, Position origin, Position extent, bool exactly)
        {
            var lowest = new[] { origin.X - extent.X, origin.Y - extent.Y, origin.Z - extent.Z };
            var highest = new[] { origin.X + extent.X, origin.Y + extent.Y, origin.Z + extent.Z };

            Vertices? best = null;

            for (int header = 0; header + 8 < uexp.Length; header++)
            {
                if (BitConverter.ToInt32(uexp, header) != POSITION_STRIDE) { continue; }

                var count = BitConverter.ToInt32(uexp, header + 4);
                if (count < FEWEST_VERTICES || count > MOST_VERTICES) { continue; }

                var data = header + 8;
                if (data + count * POSITION_STRIDE > uexp.Length) { continue; }

                var box = boxOf(uexp, data, count);
                if (box == null) { continue; }

                var (min, max) = box.Value;

                //The whole test. Not "are these numbers plausible" but "are these the numbers the
                //export said its own vertices span".
                var error = Math.Max(
                    Math.Max(Math.Abs(min.X - lowest[0]), Math.Abs(max.X - highest[0])),
                    Math.Max(
                        Math.Max(Math.Abs(min.Y - lowest[1]), Math.Abs(max.Y - highest[1])),
                        Math.Max(Math.Abs(min.Z - lowest[2]), Math.Abs(max.Z - highest[2]))));

                if (exactly)
                {
                    //Loose enough for rounding, far tighter than anything a run of unrelated bytes
                    //could manage by chance.
                    if (error > 1.0) { continue; }
                }
                else
                {
                    //Inside the box, with a hair of slack for rounding.
                    var contained =
                        min.X >= lowest[0] - 1 && max.X <= highest[0] + 1 &&
                        min.Y >= lowest[1] - 1 && max.Y <= highest[1] + 1 &&
                        min.Z >= lowest[2] - 1 && max.Z <= highest[2] + 1;
                    if (!contained) { continue; }

                    //Without the exact box to lean on, a handful of vertices is not enough to
                    //tell a buffer from a coincidence, so the small cases are left to the pass
                    //that can prove them.
                    if (count < 8) { continue; }

                    //And filling a fair part of it. This is what rules out the packed tangent
                    //bytes, which read as floats a hair either side of zero and are therefore
                    //contained in every box there is while spanning none of it.
                    if (filled(max.X - min.X, extent.X * 2) +
                        filled(max.Y - min.Y, extent.Y * 2) +
                        filled(max.Z - min.Z, extent.Z * 2) < 2) { continue; }
                }

                //With no exact box to rank by, the honest preference is the longest run: a real
                //buffer is far longer than any accident that resembles one.
                if (best == null || (exactly ? error < best.BoundsError : count > best.Count))
                {
                    best = new Vertices {
                        HeaderOffset = header,
                        DataOffset = data,
                        Count = count,
                        Min = min,
                        Max = max,
                        BoundsError = error,
                    };
                }
            }

            return best;
        }

        /// <summary>
        /// The triangles, which a cooked mesh keeps four times over.
        ///
        /// The engine stores index data as an array of *bytes* rather than of indices, so the
        /// count in the header is the byte length and the triangle count is a sixth of it. There
        /// are normally four of these buffers holding the same geometry - the ordinary one, the
        /// reversed one used when a mesh is mirrored, and a depth only pair for shadows - which is
        /// why finding several of the same size is the expected answer rather than a sign of
        /// something having gone wrong.
        ///
        /// Every candidate is checked against the vertex count. A triangle that points at a vertex
        /// which does not exist means this was never an index buffer.
        /// </summary>
        public static IReadOnlyList<Triangles> findTriangles(byte[] uexp, int vertexCount, int searchFrom)
        {
            var found = new List<Triangles>();

            for (int header = searchFrom; header + 8 < uexp.Length; header++)
            {
                //A byte array announces its element size first, and for index data that is one.
                if (BitConverter.ToInt32(uexp, header) != 1) { continue; }

                var bytes = BitConverter.ToInt32(uexp, header + 4);
                if (bytes < 6 || bytes % 6 != 0) { continue; }

                var data = header + 8;
                if (data + bytes > uexp.Length) { continue; }

                //An index is two bytes until a mesh has more vertices than two bytes can count,
                //at which point the whole buffer switches to four. Usually the reading that gives
                //triangles pointing at vertices that exist settles which one this is - but not
                //when the mesh is the large kind, because then every two byte value is below the
                //vertex count by definition and the wrong reading passes its own test. So a mesh
                //too big for two byte indices is read as four byte first.
                var wide = vertexCount > ushort.MaxValue;
                var read = wide
                    ? readIndices(uexp, data, bytes, vertexCount, 4) ?? readIndices(uexp, data, bytes, vertexCount, 2)
                    : readIndices(uexp, data, bytes, vertexCount, 2) ?? readIndices(uexp, data, bytes, vertexCount, 4);
                if (read == null) { continue; }

                var (highest, distinct, indexBytes) = read.Value;

                //A real buffer uses most of the mesh. A short run of small numbers is not one.
                if (distinct < Math.Min(16, vertexCount / 4)) { continue; }

                found.Add(new Triangles {
                    HeaderOffset = header,
                    DataOffset = data,
                    ByteCount = bytes,
                    IndexSize = indexBytes,
                    HighestIndex = highest,
                    DistinctIndices = distinct,
                });

                //Past this buffer, so the same data is not reported again from an offset inside it.
                header = data + bytes - 1;
            }

            nameRoles(found);
            return found;
        }

        /// <summary>
        /// Which copy each buffer is, judged by its size against the first.
        ///
        /// Worth doing because one of them is not triangles at all. The adjacency buffer keeps
        /// twelve indices per triangle instead of three so that tessellation can see each
        /// triangle's neighbours, which makes it four times the size - and reading it as an
        /// ordinary index buffer gives a triangle count four times too high, which is exactly the
        /// kind of wrong number that looks reasonable enough to go unnoticed.
        /// </summary>
        private static void nameRoles(List<Triangles> buffers)
        {
            if (buffers.Count == 0) { return; }

            var meshSize = buffers[0].ByteCount;
            for (int i = 0; i < buffers.Count; i++)
            {
                var buffer = buffers[i];
                if (buffer.ByteCount == meshSize * 4)
                {
                    buffer.Role = "adjacency, 12 indices per triangle, for tessellation";
                }
                else if (i == 0)
                {
                    buffer.Role = "the mesh";
                }
                else
                {
                    //The engine keeps a reversed copy for mirrored meshes and a depth only pair
                    //for shadows. The depth only ones drop the vertices only a material needs,
                    //which is why they touch fewer of them.
                    buffer.Role = buffer.DistinctIndices < buffers[0].DistinctIndices
                        ? "a depth only copy, for shadows"
                        : "a reversed copy, for mirrored meshes";
                }
            }
        }

        /// <summary>True when this buffer holds triangles rather than adjacency data.</summary>
        public static bool isTriangleList(Triangles buffer) => !buffer.Role.StartsWith("adjacency");

        /// <summary>
        /// The box a run of floats spans, or nothing if the run is not floats.
        ///
        /// Anything not a number, or far larger than a level is wide, ends it. Those are the
        /// values that appear when the bytes being read are really something else.
        /// </summary>
        private static (Position min, Position max)? boxOf(byte[] uexp, int data, int count)
        {
            const float absurd = 1e5f;

            float lowX = float.MaxValue, lowY = float.MaxValue, lowZ = float.MaxValue;
            float highX = float.MinValue, highY = float.MinValue, highZ = float.MinValue;

            for (int i = 0; i < count; i++)
            {
                var at = data + i * POSITION_STRIDE;
                var x = BitConverter.ToSingle(uexp, at);
                var y = BitConverter.ToSingle(uexp, at + 4);
                var z = BitConverter.ToSingle(uexp, at + 8);

                if (!sane(x, absurd) || !sane(y, absurd) || !sane(z, absurd)) { return null; }

                lowX = Math.Min(lowX, x); highX = Math.Max(highX, x);
                lowY = Math.Min(lowY, y); highY = Math.Max(highY, y);
                lowZ = Math.Min(lowZ, z); highZ = Math.Max(highZ, z);
            }

            return (new Position(lowX, lowY, lowZ), new Position(highX, highY, highZ));
        }

        /// <summary>
        /// Reads a run as indices of the given width, or gives up the moment one of them points
        /// past the end of the mesh.
        ///
        /// That single check does all the work. Reading a 32 bit buffer as 16 bit produces a
        /// stream of zeroes and nonsense, and reading a 16 bit buffer as 32 bit produces enormous
        /// numbers; either way a vertex that does not exist appears almost immediately.
        /// </summary>
        private static (int highest, int distinct, int indexBytes)? readIndices(
            byte[] uexp, int data, int bytes, int vertexCount, int indexBytes)
        {
            if (bytes % (indexBytes * 3) != 0) { return null; }

            var highest = -1;
            var distinct = new HashSet<int>();

            for (int at = data; at < data + bytes; at += indexBytes)
            {
                var index = indexBytes == 2
                    ? BitConverter.ToUInt16(uexp, at)
                    : BitConverter.ToInt32(uexp, at);

                if (index < 0 || index >= vertexCount) { return null; }
                if (index > highest) { highest = index; }
                distinct.Add(index);
            }

            return (highest, distinct.Count, indexBytes);
        }

        /// <summary>One if this axis is filled enough to be real geometry rather than noise.</summary>
        private static int filled(float spanned, float declared) =>
            declared > 0.001f && spanned / declared >= 0.25f ? 1 : 0;

        private static bool sane(float value, float absurd)
        {
            if (float.IsNaN(value) || float.IsInfinity(value)) { return false; }
            return Math.Abs(value) <= absurd;
        }

        /// <summary>The positions, once the buffer has been settled on.</summary>
        public static IReadOnlyList<Position> readPositions(byte[] uexp, Vertices vertices, int howMany)
        {
            var positions = new List<Position>();
            foreach (var i in Enumerable.Range(0, Math.Min(howMany, vertices.Count)))
            {
                var at = vertices.DataOffset + i * POSITION_STRIDE;
                positions.Add(new Position(
                    BitConverter.ToSingle(uexp, at),
                    BitConverter.ToSingle(uexp, at + 4),
                    BitConverter.ToSingle(uexp, at + 8)));
            }
            return positions;
        }

        /// <summary>The triangles of one index buffer, as vertex numbers.</summary>
        public static IReadOnlyList<(int a, int b, int c)> readTriangles(byte[] uexp, Triangles buffer, int howMany)
        {
            var triangles = new List<(int, int, int)>();
            foreach (var i in Enumerable.Range(0, Math.Min(howMany, buffer.Count)))
            {
                var at = buffer.DataOffset + i * buffer.IndexSize * 3;
                triangles.Add((
                    indexAt(uexp, at, buffer.IndexSize),
                    indexAt(uexp, at + buffer.IndexSize, buffer.IndexSize),
                    indexAt(uexp, at + buffer.IndexSize * 2, buffer.IndexSize)));
            }
            return triangles;
        }

        private static int indexAt(byte[] uexp, int at, int indexBytes) =>
            indexBytes == 2 ? BitConverter.ToUInt16(uexp, at) : BitConverter.ToInt32(uexp, at);
    }
}
