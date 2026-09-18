using System;
using System.Collections.Generic;
using System.IO;
#nullable enable

namespace MCDSaveEdit.Logic
{
    /// <summary>
    /// A cooked SkeletalMesh taken apart far enough to put a different model in it.
    ///
    /// This is the creature counterpart of <see cref="CookedMesh"/>, and it exists because a
    /// creature is the only thing the engine moves on its own. A weapon can be reshaped into
    /// anything and it still hangs off a hand; a sheep replaced by a truck is a truck that walks,
    /// and that is the whole point of it.
    ///
    /// The layout is walked rather than assumed, and every step of the walk is checked against the
    /// step after it. Nothing here is found by an offset written down from one mesh - the sheep's
    /// numbers were how this was learned and they appear nowhere in the code, because the moment a
    /// second creature is opened they stop being true.
    ///
    /// What the engine writes, in order, once the property list is done:
    ///
    ///     ImportedBounds        seven floats, raw rather than a tagged property
    ///     materials, skeleton   carried across untouched
    ///     the render section    triangle count, bone map, vertex count, duplicated vertices
    ///     the index buffer      a size byte, then a count, then the triangles
    ///     the bone index lists  which bones this level of detail needs
    ///     positions             twelve bytes each
    ///     tangents and UVs      a shared header, then two arrays
    ///     skin weights          four bone slots and four weights per vertex
    ///     the adjacency buffer  twelve indices per triangle, for tessellation
    ///
    /// Each of those either has to be rebuilt at the new size or copied across, and the walk's job
    /// is to say which bytes are which. Where a region cannot be proven to be what it should be
    /// this refuses, because a mesh written half correctly is worse than one left alone: the game
    /// crashes on load rather than saying what it disliked.
    ///
    /// **The bone map is the part worth understanding.** Skin weights do not name skeleton bones,
    /// they index the section's bone map, so a model weighted entirely to slot zero is attached to
    /// whichever bone happens to sit there - on the sheep that is a leg, and a truck bolted to a
    /// leg swings with the walk cycle. So the bone map is rewritten to a single entry pointing at
    /// bone zero, the root, which every skeleton has and which nothing animates. The imported
    /// model then rides the creature rather than being deformed by it.
    /// </summary>
    public sealed class CookedSkeletalMesh
    {
        //An array in a cooked package is a count of bytes per element and a count of elements,
        //written together, and that pair is what every step of this walk looks for.
        private const int HEADER = 8;
        private const int POSITION_STRIDE = 12;

        //FClothingSectionData: a guid and which of the asset's levels of detail it belongs to.
        private const int CLOTHING_DATA = 20;

        //MaterialIndex, BaseIndex, NumTriangles, bRecomputeTangent, bCastShadow, BaseVertexIndex
        //and the cloth mapping, which sit in front of the bone map at a fixed width.
        private const int SECTION_HEAD = 26;

        private CookedSkeletalMesh(byte[] uasset, byte[] uexp)
        {
            UAsset = uasset;
            UExp = uexp;
        }

        public byte[] UAsset { get; }
        public byte[] UExp { get; }

        public int BoundsAt { get; private set; }
        public MeshGeometry.Position Origin { get; private set; }
        public MeshGeometry.Position Extent { get; private set; }
        public float Radius { get; private set; }

        /// <summary>The section's MaterialIndex, which is where the section begins.</summary>
        public int SectionAt { get; private set; }
        public int TriangleCount { get; private set; }
        public int BoneMapCountAt { get; private set; }
        public int BoneMapCount { get; private set; }
        public int NumVerticesAt { get; private set; }
        public int DupVertDataAt { get; private set; }
        public int DupVertIndexAt { get; private set; }
        public int DisabledAt { get; private set; }

        public int IndexTypeAt { get; private set; }
        public int IndexSize { get; private set; }
        public int IndexCount { get; private set; }
        public int IndexDataAt { get; private set; }
        public int IndexDataEnd => IndexDataAt + IndexCount * IndexSize;

        public int PositionHeaderAt { get; private set; }
        public int PositionDataAt { get; private set; }
        public int VertexCount { get; private set; }
        public int PositionDataEnd => PositionDataAt + VertexCount * POSITION_STRIDE;

        public int NumTexCoords { get; private set; }
        public bool FullPrecisionUVs { get; private set; }
        public bool HighPrecisionTangents { get; private set; }
        public int TangentDataAt { get; private set; }
        public int TangentStride { get; private set; }
        public int UVDataAt { get; private set; }
        public int UVStride { get; private set; }

        /// <summary>The skin weight buffer's two strip flag bytes, where its own header begins.</summary>
        public int SkinStripAt { get; private set; }
        public int SkinDataAt { get; private set; }
        public int SkinStride { get; private set; }
        public int SkinDataEnd => SkinDataAt + VertexCount * SkinStride;

        public int AdjacencyTypeAt { get; private set; }
        public int AdjacencySize { get; private set; }
        public int AdjacencyCount { get; private set; }
        public int AdjacencyDataAt { get; private set; }
        public int AdjacencyDataEnd => AdjacencyDataAt + AdjacencyCount * AdjacencySize;

        /// <summary>
        /// Opens a cooked skeletal mesh, or says why it could not be.
        ///
        /// The reason matters as much as the answer. Several of the game's creatures will not open
        /// - more than one level of detail, more than one material, cloth - and somebody choosing
        /// from a list deserves to know the one they picked is unsupported rather than that the
        /// tool is broken.
        /// </summary>
        public static CookedSkeletalMesh? open(byte[] uasset, byte[] uexp, out string why)
        {
            why = string.Empty;

            foreach (var candidate in boundsCandidates(uexp))
            {
                var mesh = new CookedSkeletalMesh(uasset, uexp);
                if (mesh.walk(candidate, out var complaint)) { return mesh; }

                //The first bounds-shaped run is almost always the real one, so its complaint is
                //the one worth reporting rather than the last candidate's.
                if (why.Length == 0) { why = complaint; }
            }

            if (why.Length == 0) { why = "Could not find this mesh's bounds, so nothing inside it can be located."; }
            return null;
        }

        /// <summary>
        /// Walks the whole level of detail from the bounds to the end of the export.
        ///
        /// Written as one method on purpose. Each step's answer is the next step's starting point,
        /// and the last step landing exactly on the end of the export is what proves the walk was
        /// right - splitting it up would hide that the chain is the check.
        /// </summary>
        private bool walk(int boundsAt, out string why)
        {
            BoundsAt = boundsAt;
            Origin = new MeshGeometry.Position(readFloat(boundsAt), readFloat(boundsAt + 4), readFloat(boundsAt + 8));
            Extent = new MeshGeometry.Position(readFloat(boundsAt + 12), readFloat(boundsAt + 16), readFloat(boundsAt + 20));
            Radius = readFloat(boundsAt + 24);

            var positions = MeshGeometry.findPositions(UExp, Origin, Extent);
            if (positions == null) { why = "Could not find the vertices inside this mesh."; return false; }

            //findPositions matches the array's own header. The buffer writes its stride and count
            //once more before that, and the pair agreeing is the first confirmation that this is a
            //position buffer rather than a run of floats that happens to fill the same box.
            PositionHeaderAt = positions.HeaderOffset - HEADER;
            PositionDataAt = positions.DataOffset;
            VertexCount = positions.Count;
            if (PositionHeaderAt < 0 ||
                readInt(PositionHeaderAt) != POSITION_STRIDE ||
                readInt(PositionHeaderAt + 4) != VertexCount)
            {
                why = "This mesh's position buffer is not laid out the way a skeletal one is.";
                return false;
            }

            if (countLevelsOfDetail() != 1)
            {
                why = "This creature is drawn at several levels of detail, and replacing only one would leave it changing shape with distance.";
                return false;
            }

            //---------------------------------------------------------------- forwards from here
            var at = PositionDataEnd;
            at += 2;                                    //the vertex buffer's two strip flag bytes
            NumTexCoords = readInt(at); at += 4;
            var declaredVertices = readInt(at); at += 4;
            FullPrecisionUVs = readInt(at) != 0; at += 4;
            HighPrecisionTangents = readInt(at) != 0; at += 4;

            if (NumTexCoords < 1 || NumTexCoords > 8 || declaredVertices != VertexCount)
            {
                why = "The tangents and texture coordinates do not follow this mesh's vertices where they should.";
                return false;
            }

            TangentStride = readInt(at);
            if (readInt(at + 4) != VertexCount || TangentStride != (HighPrecisionTangents ? 16 : 8))
            {
                why = "This mesh's tangents are not the size its own header says they are.";
                return false;
            }
            TangentDataAt = at + HEADER;
            at = TangentDataAt + VertexCount * TangentStride;

            UVStride = readInt(at);
            if (readInt(at + 4) != VertexCount * NumTexCoords || UVStride != (FullPrecisionUVs ? 8 : 4))
            {
                why = "This mesh's texture coordinates are not the size its own header says they are.";
                return false;
            }
            UVDataAt = at + HEADER;
            at = UVDataAt + VertexCount * NumTexCoords * UVStride;

            SkinStripAt = at;
            at += 2;                                    //the skin weight buffer's strip flags
            var extraInfluences = readInt(at) != 0; at += 4;
            if (readInt(at) != VertexCount)
            {
                why = "This mesh's skin weights do not follow its texture coordinates where they should.";
                return false;
            }
            at += 4;

            SkinStride = readInt(at);
            if (readInt(at + 4) != VertexCount || SkinStride != (extraInfluences ? 16 : 8))
            {
                why = "This mesh's skin weights are not the size its own header says they are.";
                return false;
            }
            SkinDataAt = at + HEADER;
            at = SkinDataEnd;

            //A creature with painted vertex colours or with cloth writes another buffer here, and
            //neither is something this can rebuild, so the adjacency buffer having moved is how
            //they are detected.
            if (!isIndexContainer(at, out var adjacencySize, out var adjacencyCount))
            {
                why = "This creature carries vertex colours or cloth, which this cannot rewrite.";
                return false;
            }
            AdjacencyTypeAt = at;
            AdjacencySize = adjacencySize;
            AdjacencyCount = adjacencyCount;
            AdjacencyDataAt = at + 1 + HEADER;

            //---------------------------------------------------------------- backwards from here
            //The two bone index lists sit between the triangles and the vertices. Their lengths
            //are not knowable from anything already read, and two arrays that happen to end where
            //the vertices begin are common enough in a file this size that the first such pair is
            //not to be trusted. So every pair is tried, and the one that is right is the one the
            //triangles and the section fall into place behind.
            why = "Could not find the bone lists this mesh's vertices follow.";

            foreach (var bonesAt in boneListCandidates())
            {
                if (!findIndexBuffer(bonesAt, out var typeAt))
                {
                    if (why.Length == 0) { why = "Could not find this mesh's triangles."; }
                    continue;
                }
                IndexTypeAt = typeAt;

                if (!findSection(out var complaint)) { why = complaint; continue; }

                why = string.Empty;
                return true;
            }

            return false;
        }

        /// <summary>
        /// How many position buffers the export holds, which is how many levels of detail it has.
        ///
        /// A level of detail is its own copy of everything, so rewriting one of several leaves a
        /// creature that is a truck up close and a sheep from across the room. Counting them is
        /// cheaper than supporting them.
        /// </summary>
        private int countLevelsOfDetail()
        {
            var found = 0;
            for (int at = 0; at + 16 < UExp.Length; at++)
            {
                if (readInt(at) != POSITION_STRIDE || readInt(at + 8) != POSITION_STRIDE) { continue; }

                var count = readInt(at + 4);
                if (count < 3 || count != readInt(at + 12)) { continue; }
                if (at + 16 + count * POSITION_STRIDE > UExp.Length) { continue; }
                found++;
            }
            return found;
        }

        /// <summary>
        /// Where two arrays of bone indices could begin, nearest the vertices first.
        ///
        /// Nearest first because the shorter reading is the likelier one and because it puts the
        /// cheap candidates before the expensive ones, but the order is a preference rather than
        /// an answer: what settles it is whether the triangles are found behind the pair.
        /// </summary>
        private IEnumerable<int> boneListCandidates()
        {
            //Far more room than two lists of bone indices could need, and bounded so that a mesh
            //this does not understand fails rather than searching the whole file for a coincidence.
            var earliest = Math.Max(0, PositionHeaderAt - 8192);

            for (int first = PositionHeaderAt - 8; first >= earliest; first--)
            {
                var firstCount = readInt(first);
                if (firstCount < 0 || firstCount > 2048) { continue; }

                var second = first + 4 + firstCount * 2;
                if (second + 4 > PositionHeaderAt) { continue; }

                var secondCount = readInt(second);
                if (secondCount < 0 || secondCount > 2048) { continue; }
                if (second + 4 + secondCount * 2 != PositionHeaderAt) { continue; }

                yield return first;
            }
        }

        /// <summary>The triangles, which end where the bone lists begin.</summary>
        private bool findIndexBuffer(int endsAt, out int typeAt)
        {
            typeAt = 0;

            for (int size = 2; size <= 4; size += 2)
            {
                //Walking the triangle count rather than the offset, because a header that does not
                //describe a whole number of triangles was never an index buffer.
                for (int triangles = 1; triangles * 3 * size + HEADER + 1 <= endsAt; triangles++)
                {
                    var count = triangles * 3;
                    var header = endsAt - count * size - HEADER;
                    if (header <= 0) { break; }
                    if (readInt(header) != size || readInt(header + 4) != count) { continue; }
                    if (UExp[header - 1] != size) { continue; }

                    IndexSize = size;
                    IndexCount = count;
                    IndexDataAt = header + HEADER;
                    typeAt = header - 1;
                    return true;
                }
            }

            return false;
        }

        /// <summary>
        /// The render section, found by the run of fields around its vertex count.
        ///
        /// Everything in a section is fixed width except the bone map and the duplicated vertex
        /// lists, and those are pinned by what comes after them, so the section is unpicked from
        /// the back: the triangles are known to start at a particular byte, the flag before them
        /// is the section's last field, and each array's length is whatever makes the next one
        /// land where it has to.
        ///
        /// Two of those lengths are not determined by anything already read, so both are searched
        /// rather than solved, and every combination is put to <see cref="sectionFits"/>. Taking
        /// the first arrangement that merely fits is what this did first and it was wrong within
        /// one mesh: a file this size has plenty of runs that look like a short array ending in
        /// the right place. What settles it is the seven fixed fields in front of the bone map all
        /// holding what a single-piece creature's section holds.
        /// </summary>
        private bool findSection(out string why)
        {
            why = "Could not find the section that describes how this mesh is drawn.";

            DisabledAt = IndexTypeAt - 4;
            if (DisabledAt < 0) { why = "This mesh's section does not end where its triangles begin."; return false; }

            //The duplicated vertex lists, which the engine keeps so it can recompute tangents on a
            //deforming mesh. The second holds one entry per vertex, which is what finds it.
            DupVertIndexAt = DisabledAt - VertexCount * 8 - 4;
            if (DupVertIndexAt < 0 || readInt(DupVertIndexAt) != VertexCount)
            {
                why = "Could not find this mesh's duplicated vertex list.";
                return false;
            }

            //Only offsets four bytes apart can hold a whole number of entries, so the first list's
            //length is stepped rather than scanned.
            for (int at = DupVertIndexAt - 4; at >= 0; at -= 4)
            {
                if (readInt(at) != (DupVertIndexAt - at - 4) / 4) { continue; }

                //NumVertices, MaxBoneInfluences, CorrespondClothAssetIndex, then the cloth data.
                var numVerticesAt = at - CLOTHING_DATA - 2 - 4 - 4;
                if (numVerticesAt < 0) { break; }
                if (readInt(numVerticesAt) != VertexCount) { continue; }

                for (int bones = 1; bones <= 1024; bones++)
                {
                    var boneMapAt = numVerticesAt - bones * 2 - 4;
                    if (boneMapAt - SECTION_HEAD < 0) { break; }
                    if (readInt(boneMapAt) != bones) { continue; }
                    if (!sectionFits(boneMapAt, out var complaint)) { why = complaint; continue; }

                    DupVertDataAt = at;
                    NumVerticesAt = numVerticesAt;
                    BoneMapCountAt = boneMapAt;
                    BoneMapCount = bones;
                    SectionAt = boneMapAt - SECTION_HEAD;
                    TriangleCount = readInt(SectionAt + 6);
                    why = string.Empty;
                    return true;
                }
            }

            return false;
        }

        /// <summary>
        /// Whether the fields in front of a bone map are the ones a section this can rewrite has.
        ///
        /// Each of these is a refusal rather than a mismatch when it is the real section: a
        /// creature drawn in two pieces, or with cloth on it, genuinely cannot be rewritten as one
        /// model. The same tests double as the proof that this is the real section at all, because
        /// seven fields in a row holding exactly what they should is not something a run of
        /// unrelated bytes manages.
        /// </summary>
        private bool sectionFits(int boneMapAt, out string why)
        {
            why = string.Empty;
            var sectionAt = boneMapAt - SECTION_HEAD;

            if (readInt(boneMapAt - 4) != 0)
            {
                why = "This creature has cloth on it, which this cannot rewrite.";
                return false;
            }

            //BaseVertexIndex and BaseIndex: a section that starts partway into either buffer is
            //one of several, and replacing one piece of a creature leaves the rest of it behind.
            if (readInt(boneMapAt - 8) != 0 || readInt(sectionAt + 2) != 0)
            {
                why = "This creature is drawn in several pieces, which this cannot rewrite.";
                return false;
            }

            var triangles = readInt(sectionAt + 6);
            if (triangles <= 0 || triangles * 3 != IndexCount)
            {
                why = "This mesh's section and its triangles disagree about how many there are, so it is drawn in several pieces.";
                return false;
            }

            //bRecomputeTangent and bCastShadow, which are flags and therefore nothing but nought
            //or one, and the material slot, which a creature has a handful of rather than a
            //thousand. Neither is a limitation - both are here to catch a wrong arrangement.
            var recompute = readInt(sectionAt + 10);
            var shadow = readInt(sectionAt + 14);
            var material = BitConverter.ToInt16(UExp, sectionAt);
            if (recompute < 0 || recompute > 1 || shadow < 0 || shadow > 1 || material < 0 || material > 64)
            {
                why = "Could not find the section that describes how this mesh is drawn.";
                return false;
            }

            return true;
        }

        /// <summary>Whether a size byte and an array header sit here, the way index data is written.</summary>
        private bool isIndexContainer(int at, out int size, out int count)
        {
            size = 0; count = 0;
            if (at < 0 || at + 1 + HEADER > UExp.Length) { return false; }

            var declared = UExp[at];
            if (declared != 2 && declared != 4) { return false; }
            if (readInt(at + 1) != declared) { return false; }

            count = readInt(at + 5);
            if (count < 0) { return false; }
            if (at + 1 + HEADER + count * declared > UExp.Length) { return false; }

            size = declared;
            return true;
        }

        /// <summary>
        /// Every run of seven floats that could be an FBoxSphereBounds.
        ///
        /// A skeletal mesh does not declare its bounds as a tagged property the way a static one
        /// does - the engine writes them as raw binary once the property list is done - so a
        /// property reader finds nothing and everything downstream loses its anchor. They have a
        /// signature worth trusting instead: seven floats where the last is the length of the
        /// middle three, which a run of unrelated bytes does not manage by accident.
        /// </summary>
        private static IEnumerable<int> boundsCandidates(byte[] uexp)
        {
            var found = 0;
            var limit = Math.Min(uexp.Length - 28, 65536);

            for (var at = 0; at <= limit; at++)
            {
                var f = new float[7];
                var usable = true;
                for (var i = 0; i < 7 && usable; i++)
                {
                    f[i] = BitConverter.ToSingle(uexp, at + i * 4);
                    usable = !float.IsNaN(f[i]) && !float.IsInfinity(f[i]);
                }
                if (!usable) { continue; }

                if (f[6] <= 0 || f[6] > 100000) { continue; }
                if (f[3] < 0 || f[4] < 0 || f[5] < 0) { continue; }
                if (Math.Max(f[3], Math.Max(f[4], f[5])) < 5) { continue; }

                var length = Math.Sqrt(f[3] * (double)f[3] + f[4] * (double)f[4] + f[5] * (double)f[5]);
                if (length <= 0 || Math.Abs(length - f[6]) / f[6] > 0.02) { continue; }

                yield return at;
                if (++found >= 8) { yield break; }
            }
        }

        /// <summary>What is in the mesh now, far enough to draw it.</summary>
        public CookedMesh.Geometry readGeometry()
        {
            var positions = new List<MeshGeometry.Position>(VertexCount);
            for (int i = 0; i < VertexCount; i++)
            {
                var at = PositionDataAt + i * POSITION_STRIDE;
                positions.Add(new MeshGeometry.Position(readFloat(at), readFloat(at + 4), readFloat(at + 8)));
            }

            var normals = new List<VertexPacking.Direction>(VertexCount);
            var tangents = new List<VertexPacking.Direction>(VertexCount);
            if (!HighPrecisionTangents)
            {
                for (int i = 0; i < VertexCount; i++)
                {
                    var at = TangentDataAt + i * TangentStride;
                    tangents.Add(VertexPacking.unpackNormal(UExp, at));
                    normals.Add(VertexPacking.unpackNormal(UExp, at + 4));
                }
            }

            var texCoords = new List<(float u, float v)>(VertexCount * NumTexCoords);
            for (int i = 0; i < VertexCount * NumTexCoords; i++)
            {
                var at = UVDataAt + i * UVStride;
                texCoords.Add(FullPrecisionUVs
                    ? (readFloat(at), readFloat(at + 4))
                    : (VertexPacking.unpackHalf(BitConverter.ToUInt16(UExp, at)),
                       VertexPacking.unpackHalf(BitConverter.ToUInt16(UExp, at + 2))));
            }

            var indices = new List<int>(IndexCount);
            for (int i = 0; i < IndexCount; i++)
            {
                var at = IndexDataAt + i * IndexSize;
                indices.Add(IndexSize == 2 ? BitConverter.ToUInt16(UExp, at) : readInt(at));
            }

            return new CookedMesh.Geometry {
                Positions = positions,
                Normals = normals,
                Tangents = tangents,
                TexCoords = texCoords,
                Indices = indices,
            };
        }

        /// <summary>
        /// The mesh with a different model in it.
        ///
        /// Everything between the rebuilt regions is copied rather than regenerated - the
        /// materials, the skeleton, the bone lists, the whole property list at the front. Those do
        /// not care how many vertices there are, and copying them is both safer than rewriting
        /// them and the only reason this is possible without understanding the entire format.
        /// </summary>
        public (byte[] uasset, byte[] uexp) rebuild(CookedMesh.Geometry geometry)
        {
            if (geometry.Count == 0) { throw new InvalidOperationException("That model has no vertices."); }
            if (geometry.Indices.Count == 0 || geometry.Indices.Count % 3 != 0)
            {
                throw new InvalidOperationException("That model's triangles are incomplete.");
            }
            if (geometry.TexCoords.Count != geometry.Count * NumTexCoords)
            {
                throw new InvalidOperationException(
                    $"That model needs {NumTexCoords} texture coordinate set(s) per vertex.");
            }

            var count = geometry.Count;
            var triangles = geometry.Indices.Count / 3;
            var indexSize = count > ushort.MaxValue ? 4 : 2;

            using var built = new MemoryStream();
            using var write = new BinaryWriter(built);

            //Everything up to the section, with the bounds corrected in place.
            var head = new byte[SectionAt];
            Buffer.BlockCopy(UExp, 0, head, 0, head.Length);
            writeBounds(head, geometry.Positions);
            write.Write(head);

            //-------------------------------------------------------------------- the section
            write.Write(BitConverter.ToInt16(UExp, SectionAt));      // MaterialIndex
            write.Write(0);                                          // BaseIndex
            write.Write(triangles);                                  // NumTriangles
            write.Write(0);                                          // bRecomputeTangent
            write.Write(readInt(SectionAt + 14));                    // bCastShadow
            write.Write(0);                                          // BaseVertexIndex
            write.Write(0);                                          // ClothMappingData

            //One bone, the root, for the reason at the top of this file.
            write.Write(1);
            write.Write((ushort)0);

            write.Write(count);                                      // NumVertices
            write.Write(1);                                          // MaxBoneInfluences
            write.Write((short)-1);                                  // CorrespondClothAssetIndex
            write.Write(new byte[16]);                               // the cloth asset's guid
            write.Write(-1);                                         // and which of its levels

            //The duplicated vertex lists. Nothing reads them once recomputing tangents is off, but
            //the second is indexed by vertex, so it is written at the new length rather than left
            //describing a mesh that is no longer there. One entry in the first rather than none,
            //because an empty buffer is a thing the engine dislikes more than a useless one.
            write.Write(1);
            write.Write(0);
            write.Write(count);
            for (int i = 0; i < count; i++) { write.Write(0L); }

            write.Write(0);                                          // bDisabled

            //-------------------------------------------------------------------- the triangles
            write.Write((byte)indexSize);
            write.Write(indexSize);
            write.Write(geometry.Indices.Count);
            foreach (var index in geometry.Indices)
            {
                if (index < 0 || index >= count)
                {
                    throw new InvalidOperationException("That model points at a vertex it does not have.");
                }
                if (indexSize == 2) { write.Write((ushort)index); } else { write.Write(index); }
            }

            //The bone lists, carried across: which bones exist has nothing to do with the shape.
            write.Write(UExp, IndexDataEnd, PositionHeaderAt - IndexDataEnd);

            //-------------------------------------------------------------------- the vertices
            write.Write(POSITION_STRIDE);
            write.Write(count);
            write.Write(POSITION_STRIDE);
            write.Write(count);
            foreach (var position in geometry.Positions)
            {
                write.Write(position.X);
                write.Write(position.Y);
                write.Write(position.Z);
            }

            write.Write(UExp, PositionDataEnd, 2);                   // strip flags, carried across
            write.Write(NumTexCoords);
            write.Write(count);
            write.Write(FullPrecisionUVs ? 1 : 0);
            write.Write(HighPrecisionTangents ? 1 : 0);

            write.Write(TangentStride);
            write.Write(count);
            var tangentBytes = new byte[TangentStride];
            for (int i = 0; i < count; i++)
            {
                Array.Clear(tangentBytes, 0, tangentBytes.Length);
                VertexPacking.packNormal(at(geometry.Tangents, i), tangentBytes, 0);
                VertexPacking.packNormal(at(geometry.Normals, i), tangentBytes, 4);
                write.Write(tangentBytes);
            }

            write.Write(UVStride);
            write.Write(count * NumTexCoords);
            foreach (var (u, v) in geometry.TexCoords)
            {
                if (FullPrecisionUVs) { write.Write(u); write.Write(v); }
                else { write.Write(VertexPacking.packHalf(u)); write.Write(VertexPacking.packHalf(v)); }
            }

            //-------------------------------------------------------------------- the skin weights
            write.Write(UExp, SkinStripAt, 2);                       // strip flags, carried across
            write.Write(0);                                          // bExtraBoneInfluences
            write.Write(count);
            write.Write(8);
            write.Write(count);
            for (int i = 0; i < count; i++)
            {
                //Four bone slots and four weights. All of a vertex's weight on the first slot,
                //which the bone map now points at the root, and nothing anywhere else.
                write.Write(RIGID_WEIGHT);
            }

            //The adjacency buffer holds twelve indices per triangle for tessellation, which
            //nothing in this game's creature materials asks for. Written empty rather than
            //generated, because generating it is a great deal of work for an effect that is off.
            write.Write((byte)AdjacencySize);
            write.Write(AdjacencySize);
            write.Write(0);

            //And whatever ends the export.
            write.Write(UExp, AdjacencyDataEnd, UExp.Length - AdjacencyDataEnd);

            var uexp = built.ToArray();
            var uasset = (byte[])UAsset.Clone();
            CookedPackage.correctHeader(uasset, uexp.Length - UExp.Length);
            return (uasset, uexp);
        }

        private static readonly byte[] RIGID_WEIGHT = { 0, 0, 0, 0, 255, 0, 0, 0 };

        /// <summary>
        /// The bounds rewritten to describe the model that is going in.
        ///
        /// The radius is the corner of the box rather than the furthest vertex, which is the
        /// looser of the two and the right one: ImportedBounds is what the engine builds from the
        /// imported box, so that is what it holds in every cooked creature here, and writing a
        /// tighter sphere leaves a file whose own bounds no longer look like bounds. That is not
        /// hypothetical - it is how this was caught, because the reader finds them by that shape
        /// and stopped recognising what the writer had just produced.
        /// </summary>
        private void writeBounds(byte[] head, IReadOnlyList<MeshGeometry.Position> positions)
        {
            if (BoundsAt + 28 > head.Length) { return; }

            var (origin, extent, _) = CookedMesh.measure(positions);
            var radius = (float)Math.Sqrt(
                extent.X * (double)extent.X + extent.Y * (double)extent.Y + extent.Z * (double)extent.Z);
            writeFloat(head, BoundsAt, origin.X);
            writeFloat(head, BoundsAt + 4, origin.Y);
            writeFloat(head, BoundsAt + 8, origin.Z);
            writeFloat(head, BoundsAt + 12, extent.X);
            writeFloat(head, BoundsAt + 16, extent.Y);
            writeFloat(head, BoundsAt + 20, extent.Z);
            writeFloat(head, BoundsAt + 24, radius);
        }

        private static VertexPacking.Direction at(IReadOnlyList<VertexPacking.Direction> list, int index)
            => index < list.Count ? list[index] : new VertexPacking.Direction(0, 0, 1, 1);

        private int readInt(int at) => BitConverter.ToInt32(UExp, at);
        private float readFloat(int at) => BitConverter.ToSingle(UExp, at);

        private static void writeFloat(byte[] destination, int at, float value)
            => Buffer.BlockCopy(BitConverter.GetBytes(value), 0, destination, at, 4);

        /// <summary>Every offset the walk found, for checking one mesh against another.</summary>
        public string describe() =>
            $"bounds {BoundsAt}, section {SectionAt}, {TriangleCount} triangles, " +
            $"bone map {BoneMapCount} at {BoneMapCountAt}, vertex count at {NumVerticesAt}, " +
            $"duplicates {DupVertDataAt}/{DupVertIndexAt}, indices {IndexCount}x{IndexSize} at {IndexDataAt}, " +
            $"positions {VertexCount} at {PositionDataAt}, tangents {TangentStride} at {TangentDataAt}, " +
            $"uvs {NumTexCoords}x{UVStride} at {UVDataAt}, skin {SkinStride} at {SkinDataAt}, " +
            $"adjacency {AdjacencyCount}x{AdjacencySize} at {AdjacencyDataAt}, ends {AdjacencyDataEnd} of {UExp.Length}";
    }
}
