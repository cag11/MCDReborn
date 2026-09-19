using System;
using System.Collections.Generic;
#nullable enable

namespace MCDSaveEdit.Logic
{
    /// <summary>
    /// What a mesh looks like, far enough to draw it and to measure it.
    ///
    /// Deliberately not a weapon's shape or a creature's shape. A static mesh and a skeletal one
    /// are stored differently and rewritten differently, but once read they are the same handful
    /// of lists, and the preview that draws one should be the preview that draws the other.
    /// </summary>
    public sealed class MeshShape
    {
        public MeshShape(IReadOnlyList<MeshGeometry.Position> positions, IReadOnlyList<int> indices,
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
}
