using System;
using System.Collections.Generic;

namespace MCDSaveEdit.Logic
{
    /// <summary>
    /// Changing a cooked mesh without being able to write one.
    ///
    /// Step three of the plan asks for a re-serialiser that reproduces the file byte for byte.
    /// This is not that, and deliberately so. A re-serialiser has to understand every field it
    /// writes, which means decoding the packed tangents and the UVs and the half dozen version
    /// dependent flags around them - and none of that is needed to move a vertex. So this edits in
    /// place instead: the bytes it understands are rewritten, and every byte it does not
    /// understand is carried across untouched.
    ///
    /// The payoff is that byte identity comes for free. Everything unrecognised is copied, and
    /// everything recognised is fixed width - a position is twelve bytes before and after, a
    /// bounds is twenty eight - so nothing shifts, no offset in the export table goes stale, and
    /// an edit of nothing at all produces the original file. That last part is worth testing
    /// rather than assuming, which is what a transform of identity does.
    ///
    /// The honest limit: this changes where vertices are, not how many there are. Importing a
    /// model with a different vertex count moves every offset after it, and that needs the
    /// re-serialiser this avoids.
    /// </summary>
    public sealed class MeshEdit
    {
        private readonly byte[] _uasset;
        private readonly byte[] _uexp;

        private MeshEdit(byte[] uasset, byte[] uexp, MeshGeometry.Vertices vertices,
            IReadOnlyList<MeshGeometry.Triangles> triangles, IReadOnlyList<int> boundsBlocks,
            IReadOnlyList<int> originFloats, IReadOnlyList<int> extentFloats, IReadOnlyList<int> radiusFloats)
        {
            _uasset = uasset;
            _uexp = uexp;
            Vertices = vertices;
            Triangles = triangles;
            BoundsBlocks = boundsBlocks;
            OriginFloats = originFloats;
            ExtentFloats = extentFloats;
            RadiusFloats = radiusFloats;
        }

        public MeshGeometry.Vertices Vertices { get; }
        public IReadOnlyList<MeshGeometry.Triangles> Triangles { get; }

        /// <summary>Contiguous FBoxSphereBounds in the render data: origin, extent, radius.</summary>
        public IReadOnlyList<int> BoundsBlocks { get; }

        /// <summary>The same numbers again, as separate tagged properties near the front.</summary>
        public IReadOnlyList<int> OriginFloats { get; }
        public IReadOnlyList<int> ExtentFloats { get; }
        public IReadOnlyList<int> RadiusFloats { get; }

        /// <summary>
        /// Locates everything that has to move together when geometry changes.
        ///
        /// The bounds are kept twice over, and both copies matter: the tagged one at the front is
        /// what the property list reports, and the raw one in the render data is what the renderer
        /// culls against. Changing the vertices and only one of them gets a weapon that vanishes
        /// when the camera turns, which looks like a packing failure and is not one.
        /// </summary>
        public static MeshEdit? open(byte[] uasset, byte[] uexp,
            MeshGeometry.Position origin, MeshGeometry.Position extent, float radius)
        {
            var vertices = MeshGeometry.findPositions(uexp, origin, extent);
            if (vertices == null) { return null; }

            var triangles = MeshGeometry.findTriangles(uexp, vertices.Count, vertices.DataEnd);

            //Searched for by value rather than by walking to them, for the same reason the
            //vertices were: seven numbers matching at once is not somewhere else's data.
            var blocks = findBoundsBlocks(uexp, origin, extent, radius, vertices);

            //The tagged copies sit in the property list, which is everything before the geometry.
            //Restricting the search to that region keeps a vertex that happens to equal the origin
            //from being mistaken for the origin.
            var header = vertices.HeaderOffset;
            var originFloats = findVector(uexp, origin, header);
            var extentFloats = findVector(uexp, extent, header);
            var radiusFloats = findScalar(uexp, radius, header);

            return new MeshEdit(uasset, uexp, vertices, triangles,
                blocks, originFloats, extentFloats, radiusFloats);
        }

        /// <summary>How a mesh is to be moved. Scale, then rotate, then shift.</summary>
        public readonly struct Transform
        {
            public Transform(float scale, MeshGeometry.Position offset, MeshGeometry.Position rotationDegrees)
            {
                Scale = scale;
                Offset = offset;
                RotationDegrees = rotationDegrees;
            }

            public float Scale { get; }
            public MeshGeometry.Position Offset { get; }
            public MeshGeometry.Position RotationDegrees { get; }

            public static Transform none => new Transform(1f, new MeshGeometry.Position(0, 0, 0), new MeshGeometry.Position(0, 0, 0));

            public bool isNothing =>
                Scale == 1f &&
                Offset.X == 0 && Offset.Y == 0 && Offset.Z == 0 &&
                RotationDegrees.X == 0 && RotationDegrees.Y == 0 && RotationDegrees.Z == 0;

            /// <summary>
            /// Where one point ends up. Scale, then rotate about X, Y and Z in that order, then
            /// shift.
            ///
            /// Public, and the only place this arithmetic is written, because a preview that
            /// computes it separately is a preview that can disagree with the file - and it would
            /// disagree in exactly the way nobody checks, where the picture looks right and the
            /// weapon in game does not match it.
            /// </summary>
            public MeshGeometry.Position move(MeshGeometry.Position point)
            {
                var (sinX, cosX) = sinCos(RotationDegrees.X);
                var (sinY, cosY) = sinCos(RotationDegrees.Y);
                var (sinZ, cosZ) = sinCos(RotationDegrees.Z);

                var x = point.X * Scale;
                var y = point.Y * Scale;
                var z = point.Z * Scale;

                (y, z) = (y * cosX - z * sinX, y * sinX + z * cosX);
                (x, z) = (x * cosY + z * sinY, -x * sinY + z * cosY);
                (x, y) = (x * cosZ - y * sinZ, x * sinZ + y * cosZ);

                return new MeshGeometry.Position(x + Offset.X, y + Offset.Y, z + Offset.Z);
            }
        }

        /// <summary>
        /// Moves every vertex, then rewrites the bounds to match where they ended up.
        ///
        /// The bounds are recomputed from the vertices rather than transformed alongside them.
        /// Scaling a box by the same factor as its contents happens to work; rotating one does
        /// not, because the box that contained a blade lying flat is not the box that contains it
        /// stood on end. Measuring the result afterwards is right for every transform instead of
        /// for one of them, and it is also self correcting - whatever the vertices really are is
        /// what the bounds end up describing.
        /// </summary>
        public void apply(Transform transform)
        {
            float lowX = float.MaxValue, lowY = float.MaxValue, lowZ = float.MaxValue;
            float highX = float.MinValue, highY = float.MinValue, highZ = float.MinValue;

            for (int i = 0; i < Vertices.Count; i++)
            {
                var at = Vertices.DataOffset + i * 12;

                var moved = transform.move(new MeshGeometry.Position(
                    readFloat(at), readFloat(at + 4), readFloat(at + 8)));

                writeFloat(at, moved.X);
                writeFloat(at + 4, moved.Y);
                writeFloat(at + 8, moved.Z);

                lowX = Math.Min(lowX, moved.X); highX = Math.Max(highX, moved.X);
                lowY = Math.Min(lowY, moved.Y); highY = Math.Max(highY, moved.Y);
                lowZ = Math.Min(lowZ, moved.Z); highZ = Math.Max(highZ, moved.Z);
            }

            if (Vertices.Count == 0) { return; }

            var originX = (lowX + highX) / 2f;
            var originY = (lowY + highY) / 2f;
            var originZ = (lowZ + highZ) / 2f;
            var extentX = (highX - lowX) / 2f;
            var extentY = (highY - lowY) / 2f;
            var extentZ = (highZ - lowZ) / 2f;

            //The sphere is measured to the furthest vertex, not to the corner of the box. The
            //corner is the obvious guess and it is wrong: on the Claymore it gives 94.45 where the
            //game stores 90.76, because no vertex actually reaches the corner of a blade's box.
            //Taking the real furthest vertex reproduces the game's own number to six decimal
            //places, which is what says this is the definition the engine uses rather than a
            //quantity that merely sounds right.
            var furthest = 0.0;
            for (int i = 0; i < Vertices.Count; i++)
            {
                var at = Vertices.DataOffset + i * 12;
                var dx = readFloat(at) - originX;
                var dy = readFloat(at + 4) - originY;
                var dz = readFloat(at + 8) - originZ;
                furthest = Math.Max(furthest, Math.Sqrt(dx * dx + dy * dy + dz * dz));
            }
            var radius = (float)furthest;

            foreach (var block in BoundsBlocks)
            {
                writeFloat(block, originX);
                writeFloat(block + 4, originY);
                writeFloat(block + 8, originZ);
                writeFloat(block + 12, extentX);
                writeFloat(block + 16, extentY);
                writeFloat(block + 20, extentZ);
                writeFloat(block + 24, radius);
            }

            foreach (var at in OriginFloats)
            {
                writeFloat(at, originX);
                writeFloat(at + 4, originY);
                writeFloat(at + 8, originZ);
            }
            foreach (var at in ExtentFloats)
            {
                writeFloat(at, extentX);
                writeFloat(at + 4, extentY);
                writeFloat(at + 8, extentZ);
            }
            foreach (var at in RadiusFloats) { writeFloat(at, radius); }
        }

        /// <summary>The two halves, with the edits in them and everything else as it was.</summary>
        public (byte[] uasset, byte[] uexp) write() => (_uasset, _uexp);

        internal static (float sin, float cos) sinCos(float degrees)
        {
            var radians = degrees * Math.PI / 180.0;
            return ((float)Math.Sin(radians), (float)Math.Cos(radians));
        }

        private float readFloat(int at) => BitConverter.ToSingle(_uexp, at);

        private void writeFloat(int at, float value)
        {
            var bytes = BitConverter.GetBytes(value);
            Array.Copy(bytes, 0, _uexp, at, 4);
        }

        /// <summary>
        /// Every place the whole bounds struct appears, laid out as the engine writes it.
        ///
        /// Seven floats in a row, all matching, is specific enough that a false hit would be
        /// remarkable - but any run inside the position data is refused anyway, since vertices are
        /// the one place a stream of matching floats is expected.
        /// </summary>
        private static IReadOnlyList<int> findBoundsBlocks(byte[] uexp,
            MeshGeometry.Position origin, MeshGeometry.Position extent, float radius, MeshGeometry.Vertices vertices)
        {
            var wanted = new[] { origin.X, origin.Y, origin.Z, extent.X, extent.Y, extent.Z, radius };
            var found = new List<int>();

            for (int at = 0; at + 28 <= uexp.Length; at++)
            {
                if (at >= vertices.DataOffset && at < vertices.DataEnd) { continue; }

                var matches = true;
                for (int i = 0; i < 7; i++)
                {
                    if (!close(BitConverter.ToSingle(uexp, at + i * 4), wanted[i])) { matches = false; break; }
                }
                if (matches) { found.Add(at); }
            }

            return found;
        }

        private static IReadOnlyList<int> findVector(byte[] uexp, MeshGeometry.Position wanted, int before)
        {
            var found = new List<int>();
            var limit = Math.Min(before, uexp.Length - 12);
            for (int at = 0; at <= limit; at++)
            {
                if (close(BitConverter.ToSingle(uexp, at), wanted.X) &&
                    close(BitConverter.ToSingle(uexp, at + 4), wanted.Y) &&
                    close(BitConverter.ToSingle(uexp, at + 8), wanted.Z))
                {
                    found.Add(at);
                }
            }
            return found;
        }

        private static IReadOnlyList<int> findScalar(byte[] uexp, float wanted, int before)
        {
            var found = new List<int>();
            var limit = Math.Min(before, uexp.Length - 4);
            for (int at = 0; at <= limit; at++)
            {
                if (close(BitConverter.ToSingle(uexp, at), wanted)) { found.Add(at); }
            }
            return found;
        }

        /// <summary>
        /// Equal to within what the property reader rounded away.
        ///
        /// The values being matched came back through a printed decimal, so they are close to the
        /// stored float rather than equal to it. The tolerance is relative because a sphere radius
        /// of ninety and a coordinate of a thousandth cannot share an absolute one.
        /// </summary>
        private static bool close(float found, float wanted)
        {
            if (float.IsNaN(found) || float.IsInfinity(found)) { return false; }
            var slack = Math.Max(0.001f, Math.Abs(wanted) * 0.0005f);
            return Math.Abs(found - wanted) <= slack;
        }
    }
}
