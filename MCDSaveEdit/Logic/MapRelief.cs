using System;
using System.Collections.Generic;
using System.Linq;
using System.IO;
using System.IO.Compression;
using System.Text.Json.Nodes;
#nullable enable

namespace MCDSaveEdit.Logic
{
    /// <summary>
    /// A mission turned into something you can look at from an angle.
    ///
    /// The flat picture was honest and useless: a mission is a place, and a place drawn as one
    /// grey shape tells you nothing about where the floor is, where the walls are, or which of
    /// two corridors is the one on the bridge. This builds the ground as actual geometry, coloured
    /// with the mission's own block textures.
    ///
    /// Two things keep it cheap enough for stock WPF 3D. The height plane means the shape is
    /// already known without walking twenty-two million blocks; and runs of columns standing at
    /// the same height merge into single large quads, which on hand-built rooms - long flat floors,
    /// long flat walls - collapses three hundred thousand quads into a few thousand. Colour does
    /// not have to survive that merge, because colour lives in a texture rather than in the
    /// geometry: every quad keeps a UV window onto the map, so a merged floor is still painted
    /// block by block.
    ///
    /// Which also sidesteps the fact that WPF's MeshGeometry3D has no colour channel at all.
    /// </summary>
    public static class MapRelief
    {
        /// <summary>The ground of one room, ready to hand to a Viewport3D.</summary>
        public sealed class Relief
        {
            public Relief(int sx, int sz, byte[] heights, uint[] texture, int textureHeight,
                          List<double> points, List<double> uvs, List<int> indices, int quads,
                          byte[]? walkable)
            {
                Walkable = walkable;
                Sx = sx;
                Sz = sz;
                Heights = heights;
                Texture = texture;
                TextureHeight = textureHeight;
                Points = points;
                Uvs = uvs;
                Indices = indices;
                Quads = quads;
            }

            public int Sx { get; }
            public int Sz { get; }

            /// <summary>Ground height under every column, zero where there is none.</summary>
            public byte[] Heights { get; }

            /// <summary>
            /// Where the game will let something stand, or nothing if the tile does not say.
            ///
            /// Worth showing, because a spawn point on ground the game calls unwalkable is a mob
            /// the game will not put there.
            /// </summary>
            public byte[]? Walkable { get; }

            /// <summary>Whether a column is ground a mob can be put on.</summary>
            public bool walkableAt(int x, int z)
            {
                if (Walkable == null || x < 0 || z < 0 || x >= Sx || z >= Sz) { return true; }
                return Walkable[x + z * Sx] != 0;
            }

            /// <summary>
            /// The map as pixels, Sx wide and twice Sz tall.
            ///
            /// Top halves and side halves in one image so the whole room is one material and one
            /// draw. A wall painted in its block's top colour looks wrong in a way that is hard
            /// to name and easy to see.
            /// </summary>
            public uint[] Texture { get; }

            public int TextureHeight { get; }

            /// <summary>Vertex positions, three doubles each.</summary>
            public List<double> Points { get; }

            /// <summary>Texture coordinates, two doubles each.</summary>
            public List<double> Uvs { get; }

            public List<int> Indices { get; }

            /// <summary>How many quads survived merging, for saying how well it went.</summary>
            public int Quads { get; }

            public int Highest { get; set; }
            public int Lowest { get; set; }

            /// <summary>
            /// Which blocks the mission's roof is actually made of, commonest first.
            ///
            /// A colour table can be 90% complete and still paint the map in grey, because the
            /// missing tenth is the tenth that covers everything. This says which blocks are
            /// worth having a colour for, rather than how many have one.
            /// </summary>
            public List<(ushort id, byte meta, int count)> Census { get; } =
                new List<(ushort id, byte meta, int count)>();

            /// <summary>Ground height under a column, or zero outside the room.</summary>
            public int heightAt(int x, int z)
            {
                if (x < 0 || z < 0 || x >= Sx || z >= Sz) { return 0; }
                return Heights[x + z * Sx];
            }
        }

        /// <summary>What went wrong, or how long it took, for a probe to print.</summary>
        public static List<string> Notes { get; } = new List<string>();

        /// <summary>
        /// Builds the ground of a room.
        ///
        /// <paramref name="ceiling"/> is the roof-peeling slider: columns standing higher than it
        /// are drawn at it instead, so the top of a building comes off and the rooms inside become
        /// visible. Zero or less means the whole thing.
        /// </summary>
        public static Relief? build(MapSpawns.Room room, BlockPalette.Look[] palette, int ceiling = 0)
        {
            var heights = MapSpawns.heightsOf(room);
            if (heights == null)
            {
                Notes.Add($"{room.Id}: no height plane");
                return null;
            }

            var sx = room.Size[0];
            var sy = room.Size[1];
            var sz = room.Size[2];

            if (ceiling > 0)
            {
                var capped = new byte[heights.Length];
                for (var i = 0; i < heights.Length; i++)
                {
                    capped[i] = (byte)Math.Min(heights[i], ceiling);
                }
                heights = capped;
            }

            //Walks down through anything the game does not draw, so the surface this builds is
            //the floor somebody can actually see and click on. It rewrites heights as it goes.
            heights = (byte[])heights.Clone();
            var tops = topBlocks(room, heights, sx, sy, sz, palette);
            var walkable = planeOf(room, "walkable-plane", sx * sz);
            var texture = paint(heights, tops, palette, sx, sz, walkable);

            var points = new List<double>();
            var uvs = new List<double>();
            var indices = new List<int>();

            var quads = surface(heights, sx, sz, points, uvs, indices);
            quads += walls(heights, sx, sz, points, uvs, indices);

            int highest = 0, lowest = int.MaxValue;
            foreach (var one in heights)
            {
                if (one == 0) { continue; }
                if (one > highest) { highest = one; }
                if (one < lowest) { lowest = one; }
            }

            var walkableCount = walkable == null ? -1 : walkable.Count(one => one != 0);
            Notes.Add($"{room.Id}: {quads:N0} quads for {sx * sz:N0} columns, heights {lowest}-{highest}, "
                + (walkable == null ? "no walkable plane" : $"{walkableCount:N0} walkable"));

            var made = new Relief(sx, sz, heights, texture, sz * 2, points, uvs, indices, quads,
                                  walkable)
            {
                Highest = highest,
                Lowest = lowest == int.MaxValue ? 0 : lowest,
            };

            if (tops != null)
            {
                var seen = new Dictionary<int, int>();
                for (var i = 0; i < heights.Length; i++)
                {
                    if (heights[i] == 0) { continue; }
                    var key = tops.Value.ids[i] << 4 | tops.Value.metas[i];
                    seen[key] = seen.TryGetValue(key, out var was) ? was + 1 : 1;
                }

                foreach (var one in seen.OrderByDescending(one => one.Value).Take(24))
                {
                    made.Census.Add(((ushort)(one.Key >> 4), (byte)(one.Key & 0x0F), one.Value));
                }
            }

            return made;
        }

        /// <summary>
        /// The topmost block of every column that the game actually draws.
        ///
        /// Not simply the top block. A mission is walled in by invisibleBedrock - a solid,
        /// completely invisible barrier that stops you walking off the edge - and the height
        /// plane counts it like anything else. Take the height plane at its word and the mission
        /// disappears under flat slabs standing where nothing is, and every click aimed at the
        /// floor hits one of them instead.
        ///
        /// So each column descends from its stated height until it finds something visible, and
        /// that becomes the surface. A column that is invisible all the way down has no floor at
        /// all and is left out, which is what opens the map up.
        ///
        /// The whole block array has to be decompressed for this and then almost all of it thrown
        /// away, which sounds wasteful and is the cheap option: the alternative is asking the
        /// game, and the game is not running.
        /// </summary>
        private static (ushort[] ids, byte[] metas)? topBlocks(MapSpawns.Room room, byte[] heights,
                                                               int sx, int sy, int sz,
                                                               BlockPalette.Look[] palette)
        {
            var encoded = room.Tile["blocks"]?.GetValue<string>();
            if (encoded == null) { return null; }

            byte[] bytes;
            try
            {
                using var raw = new MemoryStream(Convert.FromBase64String(encoded));
                using var unzip = new ZLibStream(raw, CompressionMode.Decompress);
                using var made = new MemoryStream();
                unzip.CopyTo(made);
                bytes = made.ToArray();
            }
            catch (Exception problem)
            {
                Notes.Add($"{room.Id}: blocks will not unpack - {problem.Message}");
                return null;
            }

            var count = sx * sy * sz;

            //Two shapes exist. Narrow ids are one byte each followed by the metadata nibbles;
            //wide ids are two bytes each, little endian, followed by the same. Anything else is
            //not a tile we understand, and guessing would paint the room in noise.
            var wide = bytes.Length >= count * 2;
            if (!wide && bytes.Length < count)
            {
                Notes.Add($"{room.Id}: blocks are {bytes.Length:N0} bytes for {count:N0} cells");
                return null;
            }

            var ids = new ushort[sx * sz];
            var metas = new byte[sx * sz];

            //The metadata follows the ids: half a byte a cell, and the HIGH nibble belongs to the
            //EVEN index. That is the opposite of Minecraft's own packing, and reading it the
            //Minecraft way swaps every pair of blocks - oak planks become spruce and back again
            //down the whole map, which looks like a texture bug and is not one.
            var metaAt = wide ? count * 2 : count;
            var hasMeta = bytes.Length >= metaAt + (count + 1) / 2;

            for (var z = 0; z < sz; z++)
            {
                for (var x = 0; x < sx; x++)
                {
                    var column = x + z * sx;
                    var height = heights[column];
                    if (height == 0) { continue; }

                    //The plane holds the topmost solid block plus one, so the block itself is the
                    //cell below. A height at the ceiling still points inside the tile.
                    var y = Math.Min(height - 1, sy - 1);

                    for (; y >= 0; y--)
                    {
                        var cell = x + sx * (z + sz * y);
                        var id = wide
                            ? (ushort)(bytes[cell * 2] | (bytes[cell * 2 + 1] << 8))
                            : bytes[cell];

                        if (id == 0) { continue; }

                        var look = id < palette.Length ? palette[id] : null;
                        if (look != null && look.Invisible) { continue; }

                        ids[column] = id;
                        heights[column] = (byte)Math.Min(byte.MaxValue, y + 1);

                        if (hasMeta)
                        {
                            var packed = bytes[metaAt + cell / 2];
                            metas[column] = (byte)(cell % 2 == 0 ? packed >> 4 : packed & 0x0F);
                        }

                        break;
                    }

                    //Invisible the whole way down, so there is nothing here to stand on or click.
                    if (y < 0) { heights[column] = 0; }
                }
            }

            return (ids, metas);
        }

        /// <summary>
        /// The map as a picture: top colours above, side colours below.
        ///
        /// The top half is shaded by the lie of the land as well - a column with lower ground to
        /// its north-west catches the light, one in a hollow does not. Without it a flat floor and
        /// a flat roof of the same stone are the same rectangle of grey, and the whole point of
        /// going to three dimensions was to tell them apart.
        /// </summary>
        /// <summary>
        /// One of the tile's own planes, if it carries one the right size.
        ///
        /// Same shape as the height plane: zlib, base64, one byte a column.
        /// </summary>
        public static byte[]? planeOf(MapSpawns.Room room, string named, int wanted)
        {
            var encoded = room.Tile[named]?.GetValue<string>();
            if (encoded == null) { return null; }

            try
            {
                using var raw = new MemoryStream(Convert.FromBase64String(encoded));
                using var unzip = new ZLibStream(raw, CompressionMode.Decompress);
                using var made = new MemoryStream();
                unzip.CopyTo(made);

                var bytes = made.ToArray();
                return bytes.Length == wanted ? bytes : null;
            }
            catch (Exception)
            {
                return null;
            }
        }

        private static uint[] paint(byte[] heights, (ushort[] ids, byte[] metas)? tops,
                                    BlockPalette.Look[] palette, int sx, int sz,
                                    byte[]? walkable)
        {
            var made = new uint[sx * sz * 2];

            for (var z = 0; z < sz; z++)
            {
                for (var x = 0; x < sx; x++)
                {
                    var column = x + z * sx;
                    if (heights[column] == 0) { continue; }

                    var id = tops == null ? 0 : tops.Value.ids[column];
                    var meta = tops == null ? 0 : tops.Value.metas[column];
                    var look = id < palette.Length ? palette[id] : null;

                    var top = look?.topOf(meta) ?? 0u;
                    var side = look?.sideOf(meta) ?? 0u;
                    if (top == 0) { top = 0xFF808080u; }
                    if (side == 0) { side = top; }

                    //North-west lighting, the way every map has drawn hills for four hundred
                    //years. Two neighbours is enough and costs nothing.
                    var here = heights[column];
                    var west = x > 0 ? heights[column - 1] : here;
                    var north = z > 0 ? heights[column - sx] : here;
                    var slope = (here - west) + (here - north);
                    var lit = 1.0 + Math.Max(-0.55, Math.Min(0.60, slope * 0.17));

                    //Ground the game will let a mob stand on is lifted; everything else is pushed
                    //down. Creeper Woods is a dark forest and honest colours make it a dark
                    //picture, so the one distinction that matters for placing a spawn point -
                    //can something stand here - is the one allowed to change the brightness.
                    if (walkable != null)
                    {
                        lit *= walkable[column] != 0 ? 1.35 : 0.78;
                    }

                    made[column] = shade(top, lit);
                    //Sides are dimmer than tops for the same reason they are in the game: a face
                    //that never points at the sky should not be as bright as one that does.
                    made[column + sx * sz] = shade(side, 0.72);
                }
            }

            return made;
        }

        private static uint shade(uint colour, double by)
        {
            var r = (int)Math.Round(((colour >> 16) & 0xFF) * by);
            var g = (int)Math.Round(((colour >> 8) & 0xFF) * by);
            var b = (int)Math.Round((colour & 0xFF) * by);

            return 0xFF000000u
                | ((uint)Math.Min(255, Math.Max(0, r)) << 16)
                | ((uint)Math.Min(255, Math.Max(0, g)) << 8)
                | (uint)Math.Min(255, Math.Max(0, b));
        }

        /// <summary>
        /// The ground itself, as few quads as it can be said in.
        ///
        /// Columns standing at the same height merge into the largest rectangle that will hold
        /// them: grow east while the height matches, then grow south while the whole row matches.
        /// Colour is not part of the test, because colour is in the texture - which is what makes
        /// the merge worth anything at all on a floor of mixed stone.
        /// </summary>
        private static int surface(byte[] heights, int sx, int sz,
                                   List<double> points, List<double> uvs, List<int> indices)
        {
            var used = new bool[sx * sz];
            var quads = 0;

            for (var z = 0; z < sz; z++)
            {
                for (var x = 0; x < sx; x++)
                {
                    var at = x + z * sx;
                    if (used[at]) { continue; }

                    var height = heights[at];
                    if (height == 0) { used[at] = true; continue; }

                    var wide = 1;
                    while (x + wide < sx
                           && !used[at + wide]
                           && heights[at + wide] == height)
                    {
                        wide++;
                    }

                    var deep = 1;
                    while (z + deep < sz)
                    {
                        var row = at + deep * sx;
                        var ok = true;
                        for (var i = 0; i < wide; i++)
                        {
                            if (!used[row + i] && heights[row + i] == height) { continue; }
                            ok = false;
                            break;
                        }
                        if (!ok) { break; }
                        deep++;
                    }

                    for (var dz = 0; dz < deep; dz++)
                    {
                        for (var dx = 0; dx < wide; dx++) { used[at + dz * sx + dx] = true; }
                    }

                    quad(points, uvs, indices,
                        x, height, z,
                        x + wide, height, z,
                        x + wide, height, z + deep,
                        x, height, z + deep,
                        x / (double)sx, z / (double)(sz * 2),
                        (x + wide) / (double)sx, (z + deep) / (double)(sz * 2));

                    quads++;
                }
            }

            return quads;
        }

        /// <summary>
        /// The drops between one height and the next.
        ///
        /// Without these the map is a set of floating slabs and reads as nothing. Each edge is
        /// walked in runs of equal drop so a cliff a hundred columns long is one quad, not a
        /// hundred - the same trick as the surface, in one dimension.
        /// </summary>
        private static int walls(byte[] heights, int sx, int sz,
                                 List<double> points, List<double> uvs, List<int> indices)
        {
            var quads = 0;
            var vStart = 0.5;

            int at(int x, int z) =>
                x < 0 || z < 0 || x >= sx || z >= sz ? 0 : heights[x + z * sx];

            //Faces along x: the wall between column x-1 and column x, which runs along z.
            for (var x = 0; x <= sx; x++)
            {
                var z = 0;
                while (z < sz)
                {
                    var here = at(x, z);
                    var there = at(x - 1, z);
                    if (here == there) { z++; continue; }

                    var run = 1;
                    while (z + run < sz && at(x, z + run) == here && at(x - 1, z + run) == there)
                    {
                        run++;
                    }

                    var high = Math.Max(here, there);
                    var low = Math.Min(here, there);
                    //The face wears the colour of whichever side is taller, because that is the
                    //block whose flank you are looking at.
                    var owner = here > there ? x : x - 1;
                    var u = Math.Min(sx - 1, Math.Max(0, owner)) / (double)sx;
                    var v = vStart + z / (double)(sz * 2);

                    quad(points, uvs, indices,
                        x, low, z,
                        x, low, z + run,
                        x, high, z + run,
                        x, high, z,
                        u, v, u + 1.0 / sx, v + run / (double)(sz * 2));

                    quads++;
                    z += run;
                }
            }

            //Faces along z.
            for (var z = 0; z <= sz; z++)
            {
                var x = 0;
                while (x < sx)
                {
                    var here = at(x, z);
                    var there = at(x, z - 1);
                    if (here == there) { x++; continue; }

                    var run = 1;
                    while (x + run < sx && at(x + run, z) == here && at(x + run, z - 1) == there)
                    {
                        run++;
                    }

                    var high = Math.Max(here, there);
                    var low = Math.Min(here, there);
                    var owner = here > there ? z : z - 1;
                    var u = x / (double)sx;
                    var v = vStart + Math.Min(sz - 1, Math.Max(0, owner)) / (double)(sz * 2);

                    quad(points, uvs, indices,
                        x, low, z,
                        x, high, z,
                        x + run, high, z,
                        x + run, low, z,
                        u, v, u + run / (double)sx, v + 1.0 / (sz * 2));

                    quads++;
                    x += run;
                }
            }

            return quads;
        }

        private static void quad(List<double> points, List<double> uvs, List<int> indices,
                                 double ax, double ay, double az,
                                 double bx, double by, double bz,
                                 double cx, double cy, double cz,
                                 double dx, double dy, double dz,
                                 double u0, double v0, double u1, double v1)
        {
            var first = points.Count / 3;

            points.Add(ax); points.Add(ay); points.Add(az);
            points.Add(bx); points.Add(by); points.Add(bz);
            points.Add(cx); points.Add(cy); points.Add(cz);
            points.Add(dx); points.Add(dy); points.Add(dz);

            uvs.Add(u0); uvs.Add(v0);
            uvs.Add(u1); uvs.Add(v0);
            uvs.Add(u1); uvs.Add(v1);
            uvs.Add(u0); uvs.Add(v1);

            indices.Add(first); indices.Add(first + 1); indices.Add(first + 2);
            indices.Add(first); indices.Add(first + 2); indices.Add(first + 3);
        }

        /// <summary>
        /// Which column a look lands on, and how high the ground is there.
        ///
        /// WPF can hit-test 3D itself, but it does it in software against every triangle, and its
        /// own guidance is to turn that off on anything large. Walking the height field instead
        /// costs one step per column crossed, needs no geometry at all, and answers with a block
        /// coordinate rather than a triangle - which is what a spawn point is placed at anyway.
        /// </summary>
        /// Ordered x, y, z - the same order as everything else that speaks about a position.
        /// It used to answer (x, z, y), which read fine here and was silently reshuffled on the
        /// way out: C# converts tuples by POSITION and ignores the names, so a caller declaring
        /// (x, y, z) got the depth in y and the ground height in z, and spawn points landed in
        /// mid-air a hundred blocks above a room only sixty-nine tall.
        public static (int x, int y, int z)? pick(Relief relief,
                                                  double ox, double oy, double oz,
                                                  double dx, double dy, double dz,
                                                  double far = 4096)
        {
            var length = Math.Sqrt(dx * dx + dy * dy + dz * dz);
            if (length <= 0) { return null; }
            dx /= length; dy /= length; dz /= length;

            //Half a block a step. Finer than a block so a ray coming in almost flat cannot skip
            //over a wall it should have struck, and coarse enough that a full-length look across
            //Creeper Woods is a few thousand steps rather than a stall.
            var step = 0.5;

            for (var travelled = 0.0; travelled < far; travelled += step)
            {
                var x = (int)Math.Floor(ox + dx * travelled);
                var y = oy + dy * travelled;
                var z = (int)Math.Floor(oz + dz * travelled);

                if (y < 0) { return null; }
                if (x < 0 || z < 0 || x >= relief.Sx || z >= relief.Sz) { continue; }

                var ground = relief.heightAt(x, z);
                if (ground > 0 && y <= ground) { return (x, ground, z); }
            }

            return null;
        }
    }
}
