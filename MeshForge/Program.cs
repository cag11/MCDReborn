using System;
using System.IO;
using System.Linq;

namespace MeshForge
{
    /// <summary>
    /// A workbench for getting custom weapon meshes into Minecraft Dungeons.
    ///
    /// The goal is a weapon somebody modelled themselves, loaded by the game, without needing
    /// Unreal Engine installed to do it. That is a long way off and this is the first step of it.
    ///
    /// What is already known, from taking apart a mod that works (Sabers, which replaces the
    /// Claymore and the Heartstealer):
    ///
    ///   - A weapon mesh is a cooked StaticMesh: SM_Claymore arrives as a 2.5 KB .uasset header
    ///     beside a 14 KB .uexp holding the geometry. 81 of the 84 melee weapons are static
    ///     meshes; only the whips are skeletal.
    ///   - That mod ships complete cooked assets - meshes, materials, textures, animations, the
    ///     sound effects and even a replacement locres to rename the weapon. It was built by
    ///     cooking real assets in Unreal, which is what the community's Mod Kit is for.
    ///   - The game loads them out of ~mods happily. Whatever is in the pak, it is accepted as
    ///     long as it is a valid cooked asset at a path the game already knows.
    ///
    /// So the question is not whether the game will take a new mesh. It will. The question is
    /// whether a mesh can be built without Unreal, and that comes down to writing the cooked
    /// StaticMesh byte layout by hand.
    ///
    /// **Milestone one is deliberately not that.** Before any of it is worth attempting, one
    /// thing has to be true: an asset taken out of the game and put straight back, unchanged,
    /// has to load. If a byte for byte round trip will not load, nothing further matters, and
    /// the week is better spent elsewhere. That is what `roundtrip` produces, and only the game
    /// can answer it.
    /// </summary>
    public static class Program
    {
        public static int Main(string[] args)
        {
            if (args.Length == 0) { usage(); return 1; }

            try
            {
                switch (args[0].ToLowerInvariant())
                {
                    case "find": return find(rest(args));
                    case "dump": return dump(rest(args));
                    case "roundtrip": return roundtrip(rest(args));
                    case "geometry": return geometry(rest(args));
                    case "extract": return extract(rest(args));
                    case "survey": return survey(rest(args));
                    default: usage(); return 1;
                }
            }
            catch (Exception e)
            {
                Console.Error.WriteLine($"{e.GetType().Name}: {e.Message}");
                return 1;
            }
        }

        private static string[] rest(string[] args) => args.Skip(1).ToArray();

        private static void usage()
        {
            Console.WriteLine("MeshForge - a workbench for custom weapon meshes");
            Console.WriteLine();
            Console.WriteLine("  find <text>                 asset paths containing that text");
            Console.WriteLine("  dump <asset path>           what the cooked asset is made of");
            Console.WriteLine("  roundtrip <asset> <out.pak> pack it back unchanged, to prove the loop");
            Console.WriteLine("  geometry <asset path>       find the vertices in the cooked mesh");
            Console.WriteLine("  extract <asset> <folder>    write the raw .uasset and .uexp out to look at");
            Console.WriteLine("  survey <text>               read the geometry of every matching mesh, and tally it");
            Console.WriteLine();
            Console.WriteLine("The game folder comes from the editor's own setting. Pass --paks <folder> to override.");
        }

        private static (string[] rest, string? paks) takePaksOption(string[] args)
        {
            var paks = (string?)null;
            var kept = new System.Collections.Generic.List<string>();
            for (int i = 0; i < args.Length; i++)
            {
                if (args[i] == "--paks" && i + 1 < args.Length) { paks = args[++i]; continue; }
                kept.Add(args[i]);
            }
            return (kept.ToArray(), paks);
        }

        private static int find(string[] args)
        {
            var (rest, paksOption) = takePaksOption(args);
            if (rest.Length < 1) { usage(); return 1; }

            var folder = GameFiles.paksFolder(paksOption);
            if (folder == null) { Console.Error.WriteLine("No game folder. Open the editor once, or pass --paks."); return 1; }

            var index = GameFiles.open(folder);
            var hits = GameFiles.find(index, rest[0]);
            Console.WriteLine($"{hits.Count} matching:");
            foreach (var path in hits.Take(60)) { Console.WriteLine($"  {path}"); }
            if (hits.Count > 60) { Console.WriteLine($"  ... and {hits.Count - 60} more"); }
            return 0;
        }

        private static int dump(string[] args)
        {
            var (rest, paksOption) = takePaksOption(args);
            if (rest.Length < 1) { usage(); return 1; }

            var folder = GameFiles.paksFolder(paksOption);
            if (folder == null) { Console.Error.WriteLine("No game folder. Open the editor once, or pass --paks."); return 1; }

            var index = GameFiles.open(folder);
            var package = GameFiles.read(index, rest[0]);
            if (package == null) { Console.Error.WriteLine($"Could not read {rest[0]}"); return 1; }

            Console.WriteLine($"{rest[0]}");
            Console.WriteLine($"  uasset {package.Value.UAsset.Count:N0} bytes");
            Console.WriteLine($"  uexp   {package.Value.UExp.Count:N0} bytes");
            Console.WriteLine($"  ubulk  {(package.Value.UBulk?.Count ?? 0):N0} bytes");
            Console.WriteLine();

            CookedAsset.describe(package.Value.UAsset.ToArray());
            Console.WriteLine();

            try
            {
                StaticMeshReader.describe(package.Value);
                StaticMeshReader.reportBounds(package.Value);
            }
            catch (Exception e)
            {
                //Worth reporting rather than hiding: a cooked mesh is exactly the kind of export
                //the generic reader was not written for, and where it gives up is information.
                Console.WriteLine($"  reading the exports stopped at {e.GetType().Name}: {e.Message}");
            }
            return 0;
        }

        /// <summary>
        /// The go or no go test: the game's own asset, packed back at its own path, changed in no
        /// way whatsoever.
        ///
        /// Drop the result in ~mods and start the game. If the weapon looks and behaves exactly as
        /// it always did, the loop is sound and the only thing left to solve is the geometry. If
        /// it does not load, the rest of the plan is worthless and better known now than in a
        /// week.
        /// </summary>
        private static int roundtrip(string[] args)
        {
            var (rest, paksOption) = takePaksOption(args);
            if (rest.Length < 2) { usage(); return 1; }

            var folder = GameFiles.paksFolder(paksOption);
            if (folder == null) { Console.Error.WriteLine("No game folder. Open the editor once, or pass --paks."); return 1; }

            var index = GameFiles.open(folder);
            var package = GameFiles.read(index, rest[0]);
            if (package == null) { Console.Error.WriteLine($"Could not read {rest[0]}"); return 1; }

            var uasset = package.Value.UAsset.ToArray();
            var uexp = package.Value.UExp.ToArray();
            GameFiles.writeMod(rest[1], new[] { (rest[0], uasset, uexp) });

            Console.WriteLine($"Wrote {rest[1]} ({new FileInfo(rest[1]).Length:N0} bytes)");
            Console.WriteLine($"  {rest[0]}.uasset  {uasset.Length:N0} bytes");
            Console.WriteLine($"  {rest[0]}.uexp    {uexp.Length:N0} bytes");
            Console.WriteLine();
            Console.WriteLine("Unchanged on purpose. Put it in the game's ~mods folder and start the game:");
            Console.WriteLine("  the weapon looking exactly as it always did is the result being looked for.");
            Console.WriteLine("  anything else - a crash, an invisible weapon - and packing is the problem,");
            Console.WriteLine("  not the geometry, and that is worth knowing before a line of it is written.");
            return 0;
        }

        /// <summary>
        /// Where the vertices are, found by their contents rather than by walking the layout.
        ///
        /// The bounds come off the tagged property list at the front of the export, and every run
        /// of bytes in the `.uexp` that reads like a position buffer is then measured against that
        /// box. The real buffer is the one whose points all land inside it - which is not a thing
        /// that happens by accident, and is the reason this can be trusted without knowing the
        /// serialisation order in advance.
        /// </summary>
        private static int geometry(string[] args)
        {
            var (rest, paksOption) = takePaksOption(args);
            if (rest.Length < 1) { usage(); return 1; }

            var folder = GameFiles.paksFolder(paksOption);
            if (folder == null) { Console.Error.WriteLine("No game folder. Open the editor once, or pass --paks."); return 1; }

            var index = GameFiles.open(folder);
            var package = GameFiles.read(index, rest[0]);
            if (package == null) { Console.Error.WriteLine($"Could not read {rest[0]}"); return 1; }

            if (!StaticMeshReader.tryReadBounds(package.Value, out var origin, out var extent))
            {
                Console.Error.WriteLine("No ExtendedBounds on that asset, so there is nothing to check a buffer against.");
                return 1;
            }

            var uexp = package.Value.UExp.ToArray();
            Console.WriteLine($"{rest[0]}");
            Console.WriteLine($"  uexp {uexp.Length:N0} bytes");
            Console.WriteLine($"  bounds origin {origin} extent {extent}");
            Console.WriteLine();

            var vertices = Geometry.findPositions(uexp, origin, extent);
            if (vertices == null)
            {
                Console.WriteLine("  Nothing in the file spans that box, so the positions are not plain floats here.");
                return 0;
            }

            Console.WriteLine($"  {vertices.Count:N0} vertices");
            Console.WriteLine($"    header at {vertices.HeaderOffset:N0}, data {vertices.DataOffset:N0}..{vertices.DataEnd:N0}");
            Console.WriteLine($"    spans {vertices.Min} to {vertices.Max}");
            Console.WriteLine($"    which is the declared box to within {vertices.BoundsError:0.####} units");
            Console.WriteLine();

            foreach (var position in Geometry.readPositions(uexp, vertices, 4))
            {
                Console.WriteLine($"      {position}");
            }
            Console.WriteLine();

            var buffers = Geometry.findTriangles(uexp, vertices.Count, vertices.DataEnd);
            if (buffers.Count == 0)
            {
                Console.WriteLine("  No index buffer points only at vertices that exist, so the triangles are elsewhere.");
                return 0;
            }

            //Four of these is the healthy answer, not a fault: the ordinary buffer, the reversed
            //one for mirrored meshes, and a depth only pair for shadows.
            Console.WriteLine($"  {buffers.Count} index buffer(s):");
            foreach (var buffer in buffers)
            {
                var size = Geometry.isTriangleList(buffer)
                    ? $"{buffer.Count:N0} triangles"
                    : $"{buffer.IndexCount:N0} indices";
                Console.WriteLine($"    {size} at {buffer.DataOffset:N0}..{buffer.DataEnd:N0}" +
                    $" ({buffer.ByteCount:N0} bytes) - {buffer.Role}");
            }
            Console.WriteLine();

            var first = buffers[0];
            Console.WriteLine("  First triangles:");
            foreach (var (a, b, c) in Geometry.readTriangles(uexp, first, 4))
            {
                Console.WriteLine($"    {a}, {b}, {c}");
            }
            Console.WriteLine();

            //The cross check worth printing, because it is the one that makes this a fact rather
            //than a good guess: the triangles reach every vertex and no further.
            var reaches = first.HighestIndex + 1 == vertices.Count && first.DistinctIndices == vertices.Count;
            Console.WriteLine(reaches
                ? $"  The triangles use all {vertices.Count:N0} vertices and none beyond them, which agrees with the header."
                : $"  The triangles use {first.DistinctIndices:N0} of {vertices.Count:N0} vertices, highest {first.HighestIndex:N0}.");

            //What is still unaccounted for, because that is what step three has to survive. The
            //gap between the positions and the first index buffer is the tangents and the UVs,
            //which are packed rather than float and so cannot be found the same way.
            var last = buffers[buffers.Count - 1];
            var between = first.HeaderOffset - vertices.DataEnd;
            var tail = uexp.Length - last.DataEnd;
            Console.WriteLine();
            Console.WriteLine($"  Accounted for: positions {vertices.DataEnd - vertices.HeaderOffset:N0} bytes," +
                $" indices {last.DataEnd - first.HeaderOffset:N0} bytes, of {uexp.Length:N0}.");
            Console.WriteLine($"  Still unread: {vertices.HeaderOffset:N0} before, {between:N0} between" +
                $" (the packed tangents and UVs), {tail:N0} after.");
            return 0;
        }

        /// <summary>
        /// The two halves of a cooked asset, written out as they are.
        ///
        /// Because an unsolved byte layout is studied with whatever is to hand, and none of those
        /// things can mount a pak. Getting the bytes onto disk turns a question about this
        /// codebase into a question about a file, which is a far easier thing to answer.
        /// </summary>
        private static int extract(string[] args)
        {
            var (rest, paksOption) = takePaksOption(args);
            if (rest.Length < 2) { usage(); return 1; }

            var folder = GameFiles.paksFolder(paksOption);
            if (folder == null) { Console.Error.WriteLine("No game folder. Open the editor once, or pass --paks."); return 1; }

            var index = GameFiles.open(folder);
            var package = GameFiles.read(index, rest[0]);
            if (package == null) { Console.Error.WriteLine($"Could not read {rest[0]}"); return 1; }

            Directory.CreateDirectory(rest[1]);
            var name = rest[0].Substring(rest[0].LastIndexOf('/') + 1);

            var uasset = Path.Combine(rest[1], name + ".uasset");
            var uexp = Path.Combine(rest[1], name + ".uexp");
            File.WriteAllBytes(uasset, package.Value.UAsset.ToArray());
            File.WriteAllBytes(uexp, package.Value.UExp.ToArray());

            Console.WriteLine($"{uasset}  {new FileInfo(uasset).Length:N0} bytes");
            Console.WriteLine($"{uexp}  {new FileInfo(uexp).Length:N0} bytes");

            if (package.Value.UBulk != null)
            {
                var ubulk = Path.Combine(rest[1], name + ".ubulk");
                File.WriteAllBytes(ubulk, package.Value.UBulk.Value.ToArray());
                Console.WriteLine($"{ubulk}  {new FileInfo(ubulk).Length:N0} bytes");
            }
            return 0;
        }

        /// <summary>
        /// The geometry of every mesh whose path matches, tallied.
        ///
        /// One asset proves the reader works on that asset. A few hundred prove it works on the
        /// format, which is the claim actually being made - and it is the cheap way to find the
        /// shapes that were not thought of, because a mesh that reads wrongly here says so by
        /// failing its own cross check rather than by looking fine.
        ///
        /// Everything happens in one process because mounting the paks costs far more than
        /// reading a mesh does.
        /// </summary>
        private static int survey(string[] args)
        {
            var (rest, paksOption) = takePaksOption(args);
            if (rest.Length < 1) { usage(); return 1; }

            var folder = GameFiles.paksFolder(paksOption);
            if (folder == null) { Console.Error.WriteLine("No game folder. Open the editor once, or pass --paks."); return 1; }

            var index = GameFiles.open(folder);
            var paths = GameFiles.find(index, rest[0]);
            Console.WriteLine($"{paths.Count} matching assets");
            Console.WriteLine();

            int read = 0, notMeshes = 0, noBounds = 0, noPositions = 0, noTriangles = 0, clean = 0, odd = 0;
            long vertices = 0, triangles = 0;
            var worstError = 0.0;
            var complaints = new System.Collections.Generic.List<string>();

            foreach (var path in paths)
            {
                PakReader.Pak.PakPackage? package;
                try { package = GameFiles.read(index, path); }
                catch (Exception) { continue; }
                if (package == null) { continue; }
                read++;

                //A name beginning SM_ is not a promise. The game uses the same prefix for SoundMix
                //assets and for Skeletons, and counting those as meshes that failed to read would
                //be inventing a problem.
                if (!package.Value.ExportTypes.Any(type => type.String == "StaticMesh")) { notMeshes++; continue; }

                if (!StaticMeshReader.tryReadBounds(package.Value, out var origin, out var extent))
                {
                    noBounds++;
                    complaints.Add($"no bounds: {path}");
                    continue;
                }

                var uexp = package.Value.UExp.ToArray();
                var found = Geometry.findPositions(uexp, origin, extent);
                if (found == null) { noPositions++; complaints.Add($"no positions: {path}"); continue; }

                var buffers = Geometry.findTriangles(uexp, found.Count, found.DataEnd);
                if (buffers.Count == 0) { noTriangles++; complaints.Add($"no triangles: {path}"); continue; }

                vertices += found.Count;
                triangles += buffers[0].Count;
                worstError = Math.Max(worstError, found.BoundsError);

                //The cross check: the mesh's own triangles should reach every vertex the header
                //promised and none beyond it.
                if (buffers[0].HighestIndex + 1 == found.Count && buffers[0].DistinctIndices == found.Count) { clean++; }
                else
                {
                    odd++;
                    complaints.Add($"{found.Count} vertices but triangles reach {buffers[0].HighestIndex + 1}" +
                        $" and use {buffers[0].DistinctIndices}: {path}");
                }
            }

            Console.WriteLine($"  read            {read}");
            Console.WriteLine($"  not meshes      {notMeshes}");
            Console.WriteLine($"  no bounds       {noBounds}");
            Console.WriteLine($"  no positions    {noPositions}");
            Console.WriteLine($"  no triangles    {noTriangles}");
            Console.WriteLine($"  cross check ok  {clean}");
            Console.WriteLine($"  cross check off {odd}");
            Console.WriteLine();
            Console.WriteLine($"  {vertices:N0} vertices and {triangles:N0} triangles read in total");
            Console.WriteLine($"  worst disagreement with the declared bounds: {worstError:0.#####} units");

            if (complaints.Count > 0)
            {
                Console.WriteLine();
                Console.WriteLine($"  {complaints.Count} worth looking at:");
                foreach (var complaint in complaints.Take(20)) { Console.WriteLine($"    {complaint}"); }
                if (complaints.Count > 20) { Console.WriteLine($"    ... and {complaints.Count - 20} more"); }
            }
            return 0;
        }
    }
}
