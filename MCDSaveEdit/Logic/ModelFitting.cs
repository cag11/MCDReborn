using System;
using System.Collections.Generic;
#nullable enable

namespace MCDSaveEdit.Logic
{
    /// <summary>
    /// Getting an imported model into the space of the thing it is replacing.
    ///
    /// Two jobs, and neither of them knows what is being replaced. A weapon and a creature are
    /// stored differently and rewritten differently, but a model arriving from a modelling tool
    /// has the same two problems either way: it is the wrong size, and its directions have to be
    /// turned without being moved.
    /// </summary>
    public static class ModelFitting
    {
        /// <summary>
        /// A transform that drops an imported model roughly where what it replaces sits.
        ///
        /// Roughly is the honest word. It matches the model's longest side to the donor's and
        /// centres one box on the other, which gets the scale right - the difference is usually a
        /// factor of ten or more, and nobody wants to find that with a slider - and gets the
        /// position close. It cannot get the orientation right, because nothing in a model says
        /// which end is the front. That is what the ghost and the rotation sliders are for.
        /// </summary>
        public static MeshEdit.Transform autoFit(GlbModel model, MeshShape donor)
        {
            var (modelOrigin, modelExtent, _) = CookedMesh.measure(model.Positions);

            var modelSide = Math.Max(modelExtent.X, Math.Max(modelExtent.Y, modelExtent.Z)) * 2f;
            var donorSide = donor.LongestSide;
            var scale = modelSide > 0.0001f && donorSide > 0.0001f ? donorSide / modelSide : 1f;

            //Centre on centre. The donor's own origin is not its middle - a claymore's sits fifty
            //units down the blade - so the model is placed against the donor's box rather than
            //against its origin.
            var offset = new MeshGeometry.Position(
                donor.Origin.X - modelOrigin.X * scale,
                donor.Origin.Y - modelOrigin.Y * scale,
                donor.Origin.Z - modelOrigin.Z * scale);

            return new MeshEdit.Transform(scale, offset, new MeshGeometry.Position(0, 0, 0));
        }

        /// <summary>
        /// The imported model's geometry, moved into the donor's space.
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
        /// One set of texture coordinates per vertex turned into as many as the mesh keeps.
        ///
        /// A cooked weapon here holds two: the one the artwork is painted with, and a second the
        /// engine reserves for baked lighting. A creature usually holds one. A model exported from
        /// a modelling tool almost always has only the first, so any others are filled with a copy
        /// of it. That is the right filler rather than a lazy one - these are moving objects lit
        /// dynamically, so nothing ever reads the lightmap channel, and leaving it empty would put
        /// zeroes where the engine expects coordinates.
        ///
        /// They are written per vertex rather than per channel: vertex zero's sets, then vertex
        /// one's. That is the order the engine reads them back in, and the order a flat copy of
        /// the game's own data already round-trips through.
        /// </summary>
        private static IReadOnlyList<(float u, float v)> spreadTexCoords(
            IReadOnlyList<(float u, float v)> supplied, int perVertex, int vertices)
        {
            if (perVertex <= 1)
            {
                //Still one per vertex, because a model with fewer coordinates than vertices would
                //otherwise be refused for a mismatch it cannot be blamed for.
                if (supplied.Count == vertices) { return supplied; }

                var exact = new List<(float, float)>(vertices);
                for (int i = 0; i < vertices; i++) { exact.Add(i < supplied.Count ? supplied[i] : (0f, 0f)); }
                return exact;
            }

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
    }
}
