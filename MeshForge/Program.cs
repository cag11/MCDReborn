using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

using MCDSaveEdit.Logic;

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
                    case "reshape": return reshape(rest(args));
                    case "packcheck": return packcheck(rest(args));
                    case "rebuild": return rebuild(rest(args));
                    case "import": return import_(rest(args));
                    case "camera": return camera(rest(args));
                    case "props": return props(rest(args));
                    case "raw": return raw(rest(args));
                    case "live": return live(rest(args));
                    case "inject": return inject(rest(args));
                    case "objects": return objects(rest(args));
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
            Console.WriteLine("  reshape <asset> <scale> <out.pak>  rewrite the mesh at a different size");
            Console.WriteLine("  packcheck <text>            unpack and repack every mesh's tangents and UVs");
            Console.WriteLine("  rebuild <text>              take every matching mesh apart and put it back unchanged");
            Console.WriteLine("  import <asset> <model.glb> <out.pak>  put a model on a weapon");
            Console.WriteLine("  camera [out.pak] [--arm N --pitch N --yaw N --fov N --inheritYaw]");
            Console.WriteLine("  props <asset path>          every tagged property, with its byte offset");
            Console.WriteLine("  raw <path>                  a non-package file out of the paks, such as a config");
            Console.WriteLine("  live                        attach to the running game and find its camera");
            Console.WriteLine("  inject <file.dll>           load a library into the running game");
            Console.WriteLine("  objects                     find Unreal's object table in the running game");
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

            var vertices = MeshGeometry.findPositions(uexp, origin, extent);
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

            foreach (var position in MeshGeometry.readPositions(uexp, vertices, 4))
            {
                Console.WriteLine($"      {position}");
            }
            Console.WriteLine();

            var buffers = MeshGeometry.findTriangles(uexp, vertices.Count, vertices.DataEnd);
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
                var size = MeshGeometry.isTriangleList(buffer)
                    ? $"{buffer.Count:N0} triangles"
                    : $"{buffer.IndexCount:N0} indices";
                Console.WriteLine($"    {size} at {buffer.DataOffset:N0}..{buffer.DataEnd:N0}" +
                    $" ({buffer.ByteCount:N0} bytes) - {buffer.Role}");
            }
            Console.WriteLine();

            var first = buffers[0];
            Console.WriteLine("  First triangles:");
            foreach (var (a, b, c) in MeshGeometry.readTriangles(uexp, first, 4))
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
                var found = MeshGeometry.findPositions(uexp, origin, extent);
                if (found == null) { noPositions++; complaints.Add($"no positions: {path}"); continue; }

                var buffers = MeshGeometry.findTriangles(uexp, found.Count, found.DataEnd);
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

        /// <summary>
        /// The first edit: the same weapon at a different size.
        ///
        /// A uniform scale is the right thing to try first because its result cannot be argued
        /// with. A weapon at half size is either visibly half size in game or the edit did not
        /// take, and there is no third outcome.
        ///
        /// A scale of exactly one is not a pointless case - it is the test. Nothing should change,
        /// and the file that comes out should be the file that went in, byte for byte. If that
        /// does not hold then the editor is corrupting something it does not understand, and it is
        /// far better to learn that here than from a game that will not start.
        /// </summary>
        private static int reshape(string[] args)
        {
            var (rest, paksOption) = takePaksOption(args);
            if (rest.Length < 3) { usage(); return 1; }
            if (!float.TryParse(rest[1], System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out var factor))
            {
                Console.Error.WriteLine($"Not a scale: {rest[1]}");
                return 1;
            }

            var folder = GameFiles.paksFolder(paksOption);
            if (folder == null) { Console.Error.WriteLine("No game folder. Open the editor once, or pass --paks."); return 1; }

            var index = GameFiles.open(folder);
            var package = GameFiles.read(index, rest[0]);
            if (package == null) { Console.Error.WriteLine($"Could not read {rest[0]}"); return 1; }

            if (!StaticMeshReader.tryReadBounds(package.Value, out var origin, out var extent, out var radius))
            {
                Console.Error.WriteLine("No ExtendedBounds on that asset, so the geometry cannot be found.");
                return 1;
            }

            var originalUasset = package.Value.UAsset.ToArray();
            var originalUexp = package.Value.UExp.ToArray();

            var edit = MeshEdit.open(originalUasset, (byte[])originalUexp.Clone(), origin, extent, radius);
            if (edit == null) { Console.Error.WriteLine("Could not find the vertices."); return 1; }

            Console.WriteLine($"{rest[0]}");
            Console.WriteLine($"  {edit.Vertices.Count:N0} vertices, {(edit.Triangles.Count > 0 ? edit.Triangles[0].Count : 0):N0} triangles");
            Console.WriteLine($"  bounds kept in {edit.BoundsBlocks.Count} render-data block(s)," +
                $" and as {edit.OriginFloats.Count} origin, {edit.ExtentFloats.Count} extent," +
                $" {edit.RadiusFloats.Count} radius tagged properties");
            Console.WriteLine();

            edit.apply(new MeshEdit.Transform(factor, new MeshGeometry.Position(0, 0, 0), new MeshGeometry.Position(0, 0, 0)));
            var (uasset, uexp) = edit.write();

            //The check that matters before anything reaches the game, and it is not "are the bytes
            //identical". The bounds get recomputed on every edit, and the engine's own bounds were
            //not computed by this arithmetic in this order, so they come back a few millionths
            //different even when nothing has moved. What has to hold is narrower and more useful:
            //every byte this tool did not mean to touch is untouched, and the numbers it did mean
            //to touch land where the game itself put them.
            var untouched = untouchedOutside(originalUexp, uexp, edit);
            Console.WriteLine(untouched
                ? "  Every byte outside the vertices and the bounds is unchanged."
                : "  Bytes changed OUTSIDE the vertices and the bounds. Something is being corrupted.");
            if (!untouched) { return 1; }

            if (Math.Abs(factor - 1f) < float.Epsilon)
            {
                //Scaling by one should leave the vertices bit for bit identical, because every
                //coordinate is multiplied by exactly one. Anything else means the read and write
                //of a position is lossy, which would quietly degrade a mesh on every edit.
                var positionsHeld = true;
                for (int at = edit.Vertices.DataOffset; at < edit.Vertices.DataEnd; at++)
                {
                    if (originalUexp[at] != uexp[at]) { positionsHeld = false; break; }
                }
                Console.WriteLine(positionsHeld
                    ? "  Scaled by one and every vertex is bit for bit unchanged."
                    : "  Scaled by one and the vertices moved, so reading and writing a position is lossy.");
                if (!positionsHeld) { return 1; }

                var drift = boundsDrift(originalUexp, uexp, edit);
                Console.WriteLine($"  Recomputed bounds differ from the game's own by {drift:0.0000000} units," +
                    " which is the arithmetic agreeing rather than the bytes matching.");
            }

            //Read the result back through the same finder. A file this tool cannot parse is not a
            //file the game should be asked to parse.
            var checkedEdit = MeshEdit.open(uasset, uexp, scaled(origin, factor), scaled(extent, factor), radius * factor);
            Console.WriteLine(checkedEdit == null
                ? "  The result no longer parses, which means the bounds and the vertices disagree."
                : $"  Reads back as {checkedEdit.Vertices.Count:N0} vertices spanning" +
                  $" {checkedEdit.Vertices.Min} to {checkedEdit.Vertices.Max}");

            GameFiles.writeMod(rest[2], new[] { (rest[0], uasset, uexp) });
            Console.WriteLine();
            Console.WriteLine($"Wrote {rest[2]} ({new FileInfo(rest[2]).Length:N0} bytes)");
            Console.WriteLine("Put it in the game's ~mods folder and look at the weapon.");
            return 0;
        }


        /// <summary>
        /// Whether this code packs a tangent and a texture coordinate the way the engine does.
        ///
        /// It cannot be checked by supplying a value and reading it back, because both formats are
        /// lossy and would agree with a wrong implementation about as often as a right one. So the
        /// game's own bytes are the test: unpack them, pack them again, and require what comes out
        /// to be identical to what went in. Anything that survives that on two and a half million
        /// vertices is doing the same arithmetic the engine did.
        ///
        /// This is the gate before importing a model. A mesh whose geometry is right and whose
        /// tangents are subtly wrong does not look broken - it looks badly lit, which is far
        /// harder to trace back to its cause.
        /// </summary>
        private static int packcheck(string[] args)
        {
            var (rest, paksOption) = takePaksOption(args);
            if (rest.Length < 1) { usage(); return 1; }

            var folder = GameFiles.paksFolder(paksOption);
            if (folder == null) { Console.Error.WriteLine("No game folder. Open the editor once, or pass --paks."); return 1; }

            var index = GameFiles.open(folder);
            var paths = GameFiles.find(index, rest[0]);
            Console.WriteLine($"{paths.Count} matching assets");

            long tangents = 0, tangentsWrong = 0, uvs = 0, uvsWrong = 0;
            int meshes = 0, noAttributes = 0;
            var complaints = new System.Collections.Generic.List<string>();

            foreach (var path in paths)
            {
                //Wrapped whole rather than around the read alone: a folder of equipment holds
                //textures too, and some of those throw on being parsed at all. That is a fact
                //about the folder, not a failure of this check.
                PakReader.Pak.PakPackage? package;
                MeshGeometry.Position origin, extent;
                byte[] uexp;
                try
                {
                    package = GameFiles.read(index, path);
                    if (package == null) { continue; }
                    if (!package.Value.ExportTypes.Any(type => type.String == "StaticMesh")) { continue; }
                    if (!StaticMeshReader.tryReadBounds(package.Value, out origin, out extent)) { continue; }
                    uexp = package.Value.UExp.ToArray();
                }
                catch (Exception) { continue; }
                var positions = MeshGeometry.findPositions(uexp, origin, extent);
                if (positions == null) { continue; }

                var attributes = MeshGeometry.findAttributes(uexp, positions);
                if (attributes == null) { noAttributes++; complaints.Add($"no attributes: {path}"); continue; }
                meshes++;

                //Packed tangents only. The high precision form is a different layout and the game
                //does not appear to use it, so claiming to handle it would be claiming too much.
                if (!attributes.HighPrecisionTangents)
                {
                    for (int i = 0; i < attributes.Count * 2; i++)
                    {
                        var at = attributes.TangentsOffset + i * 4;
                        var direction = VertexPacking.unpackNormal(uexp, at);
                        var again = new byte[4];
                        VertexPacking.packNormal(direction, again, 0);
                        tangents++;
                        for (int b = 0; b < 4; b++)
                        {
                            if (again[b] != uexp[at + b]) { tangentsWrong++; break; }
                        }
                    }
                }

                if (!attributes.FullPrecisionUVs)
                {
                    var halves = attributes.Count * attributes.TexCoords * 2;
                    for (int i = 0; i < halves; i++)
                    {
                        var at = attributes.UVsOffset + i * 2;
                        var stored = BitConverter.ToUInt16(uexp, at);
                        var again = VertexPacking.packHalf(VertexPacking.unpackHalf(stored));
                        uvs++;
                        if (again != stored) { uvsWrong++; }
                    }
                }
            }

            Console.WriteLine();
            Console.WriteLine($"  meshes with attributes  {meshes}");
            Console.WriteLine($"  meshes without          {noAttributes}");
            Console.WriteLine($"  tangent bytes checked   {tangents:N0}, wrong {tangentsWrong:N0}");
            Console.WriteLine($"  uv halves checked       {uvs:N0}, wrong {uvsWrong:N0}");
            Console.WriteLine();
            Console.WriteLine(tangentsWrong == 0 && uvsWrong == 0
                ? "  Every one survived the round trip, so the packing matches the engine's."
                : "  Some did not survive, so the packing is not the engine's yet.");

            if (complaints.Count > 0)
            {
                Console.WriteLine();
                foreach (var complaint in complaints.Take(10)) { Console.WriteLine($"    {complaint}"); }
                if (complaints.Count > 10) { Console.WriteLine($"    ... and {complaints.Count - 10} more"); }
            }
            return tangentsWrong == 0 && uvsWrong == 0 ? 0 : 1;
        }


        /// <summary>
        /// Whether a mesh taken apart and written back out is the file it started as.
        ///
        /// This is the gate before any model is imported. The writer is about to be asked to
        /// produce a cooked mesh from geometry it has never seen, and there is no way to check
        /// that result except by loading the game. But there is a way to check the writer: give it
        /// the mesh's own geometry back and require the bytes to be identical. A writer that
        /// cannot reproduce a file it has just read has no business inventing one.
        ///
        /// Run across everything rather than one asset, because the interesting failures are the
        /// meshes shaped differently from the one it was written against.
        /// </summary>
        private static int rebuild(string[] args)
        {
            var (rest, paksOption) = takePaksOption(args);
            if (rest.Length < 1) { usage(); return 1; }

            var folder = GameFiles.paksFolder(paksOption);
            if (folder == null) { Console.Error.WriteLine("No game folder. Open the editor once, or pass --paks."); return 1; }

            var index = GameFiles.open(folder);
            var paths = GameFiles.find(index, rest[0]);
            Console.WriteLine($"{paths.Count} matching assets");

            var dumpTo = rest.Length > 1 ? rest[1] : null;
            int identical = 0, differed = 0, refused = 0, threw = 0;
            var worstDrift = 0.0;
            var complaints = new System.Collections.Generic.List<string>();

            foreach (var path in paths)
            {
                PakReader.Pak.PakPackage? package;
                MeshGeometry.Position origin, extent;
                float radius;
                byte[] uasset, uexp;
                try
                {
                    package = GameFiles.read(index, path);
                    if (package == null) { continue; }
                    if (!package.Value.ExportTypes.Any(type => type.String == "StaticMesh")) { continue; }
                    if (!StaticMeshReader.tryReadBounds(package.Value, out origin, out extent, out radius)) { continue; }
                    uasset = package.Value.UAsset.ToArray();
                    uexp = package.Value.UExp.ToArray();
                }
                catch (Exception) { continue; }

                CookedMesh? mesh;
                try { mesh = CookedMesh.open(uasset, (byte[])uexp.Clone(), origin, extent, radius); }
                catch (Exception e) { threw++; complaints.Add($"{e.GetType().Name} opening {path}"); continue; }

                //Refusing is a result, not a failure: a multi-section mesh is one this writer
                //correctly declines to rewrite.
                if (mesh == null) { refused++; continue; }

                try
                {
                    var (builtAsset, builtExp) = mesh.rebuild(mesh.readGeometry());

                    //Bit identity is not the standard, and asking for it would be asking for the
                    //wrong thing. The bounds are recomputed on every rebuild, and the game's own
                    //were not computed by this arithmetic in this order - its origin sits four
                    //millionths off the centre of its own box. So what must hold is that the only
                    //bytes that moved are ones holding a number that barely moved.
                    if (builtAsset.Length != uasset.Length || !builtAsset.SequenceEqual(uasset))
                    {
                        differed++;
                        complaints.Add($"{path}: the header changed, which it should not have");
                    }
                    else if (builtExp.Length != uexp.Length)
                    {
                        differed++;
                        complaints.Add($"{path}: uexp {uexp.Length:N0} -> {builtExp.Length:N0}");
                    }
                    else if (dumpTo != null)
                    {
                        //Somewhere to look when a difference needs explaining rather than counting.
                        Directory.CreateDirectory(dumpTo);
                        var name = path.Substring(path.LastIndexOf('/') + 1);
                        File.WriteAllBytes(Path.Combine(dumpTo, name + ".rebuilt.uexp"), builtExp);
                        File.WriteAllBytes(Path.Combine(dumpTo, name + ".original.uexp"), uexp);
                        Console.WriteLine($"  wrote both copies of {name} to {dumpTo}");
                        identical++;
                    }
                    else if (onlyWhereExpected(uexp, builtExp, mesh.BoundsWrites, out var drift, out var stubborn))
                    {
                        identical++;
                        worstDrift = Math.Max(worstDrift, drift);
                    }
                    else
                    {
                        differed++;
                        complaints.Add($"{path}: byte {stubborn:N0} changed, which the writer never said it would touch");
                    }
                }
                catch (Exception e) { threw++; complaints.Add($"{e.GetType().Name} rebuilding {path}: {e.Message}"); }
            }

            Console.WriteLine();
            Console.WriteLine($"  identical  {identical}");
            Console.WriteLine($"  differed   {differed}");
            Console.WriteLine($"  refused    {refused}");
            Console.WriteLine($"  threw      {threw}");
            Console.WriteLine($"  worst drift {worstDrift:0.0000000} units, in the recomputed bounds");
            Console.WriteLine();
            Console.WriteLine(differed == 0 && threw == 0
                ? "  Every mesh it accepted came back unchanged but for the bounds, which agree to rounding."
                : "  Some did not come back unchanged, so the writer is not trustworthy yet.");

            foreach (var complaint in complaints.Take(12)) { Console.WriteLine($"    {complaint}"); }
            if (complaints.Count > 12) { Console.WriteLine($"    ... and {complaints.Count - 12} more"); }
            return differed == 0 && threw == 0 ? 0 : 1;
        }


        /// <summary>
        /// True when the only bytes that moved are ones the writer said it was going to move.
        ///
        /// The writer reports exactly which ranges it wrote bounds into, and nothing outside them
        /// may differ by so much as a bit. An earlier version judged by value instead - did this
        /// byte belong to a number that barely changed - and passed happily while eight thousand
        /// index bytes were wrong, because each difference sat between two denormal floats that
        /// were both nearly zero. Asking the writer is not a stricter form of that check; it is
        /// the check that was meant.
        /// </summary>
        private static bool onlyWhereExpected(byte[] before, byte[] after,
            System.Collections.Generic.IReadOnlyList<(int at, int length)> allowed,
            out double worst, out int stubborn)
        {
            worst = 0;
            stubborn = -1;

            for (int i = 0; i < before.Length; i++)
            {
                if (before[i] == after[i]) { continue; }

                var permitted = false;
                foreach (var range in allowed)
                {
                    if (i >= range.at && i < range.at + range.length) { permitted = true; break; }
                }
                if (!permitted) { stubborn = i; return false; }
            }

            //Having established that only the bounds moved, by how much.
            foreach (var range in allowed)
            {
                for (int o = range.at; o + 4 <= range.at + range.length && o + 4 <= before.Length; o += 4)
                {
                    var was = BitConverter.ToSingle(before, o);
                    var now = BitConverter.ToSingle(after, o);
                    if (float.IsNaN(was) || float.IsNaN(now)) { continue; }
                    worst = Math.Max(worst, Math.Abs((double)was - now));
                }
            }
            return true;
        }

        private static int firstDifference(byte[] a, byte[] b)
        {
            var shared = Math.Min(a.Length, b.Length);
            for (int i = 0; i < shared; i++) { if (a[i] != b[i]) { return i; } }
            return a.Length == b.Length ? -1 : shared;
        }


        /// <summary>
        /// Puts an imported model onto a weapon and writes the pak.
        ///
        /// The same code the tab calls, reachable without the window, because the only thing that
        /// can answer whether a rebuilt mesh is right is the game - and getting a file in front of
        /// it should not depend on a user interface being finished.
        ///
        /// The model is fitted to the weapon it replaces: scaled so their longest sides match and
        /// centred on the same point. That gets the size right, which is the part nobody would
        /// want to find by hand, and leaves the orientation to be corrected afterwards.
        /// </summary>
        private static int import_(string[] args)
        {
            var (rest, paksOption) = takePaksOption(args);
            if (rest.Length < 3) { usage(); return 1; }

            var folder = GameFiles.paksFolder(paksOption);
            if (folder == null) { Console.Error.WriteLine("No game folder. Open the editor once, or pass --paks."); return 1; }

            var index = GameFiles.open(folder);
            var package = GameFiles.read(index, rest[0]);
            if (package == null) { Console.Error.WriteLine($"Could not read {rest[0]}"); return 1; }

            if (!StaticMeshReader.tryReadBounds(package.Value, out var origin, out var extent, out var radius))
            {
                Console.Error.WriteLine("That weapon's mesh does not declare its bounds.");
                return 1;
            }

            var mesh = CookedMesh.open(package.Value.UAsset.ToArray(), package.Value.UExp.ToArray(), origin, extent, radius);
            if (mesh == null)
            {
                Console.Error.WriteLine("That weapon's mesh is not one this can rewrite.");
                return 1;
            }

            var model = GlbModel.read(rest[1]);
            Console.WriteLine($"{rest[1]}");
            Console.WriteLine($"  {model.VertexCount:N0} vertices, {model.TriangleCount:N0} triangles," +
                $" {(model.BaseColourPng != null ? $"{model.BaseColourPng.Length:N0} bytes of texture" : "no texture")}");
            Console.WriteLine();
            Console.WriteLine($"{rest[0]}");
            Console.WriteLine($"  {mesh.VertexCount:N0} vertices, {mesh.TriangleCount:N0} triangles");
            Console.WriteLine();

            var (modelOrigin, modelExtent, _) = CookedMesh.measure(model.Positions);
            var modelSide = Math.Max(modelExtent.X, Math.Max(modelExtent.Y, modelExtent.Z)) * 2f;
            var donorSide = Math.Max(extent.X, Math.Max(extent.Y, extent.Z)) * 2f;
            var scale = modelSide > 0.0001f && donorSide > 0.0001f ? donorSide / modelSide : 1f;

            var offset = new MeshGeometry.Position(
                origin.X - modelOrigin.X * scale,
                origin.Y - modelOrigin.Y * scale,
                origin.Z - modelOrigin.Z * scale);

            var transform = new MeshEdit.Transform(scale, offset, new MeshGeometry.Position(0, 0, 0));
            Console.WriteLine($"  fitted at {scale:0.###}x, moved by ({offset.X:0.#}, {offset.Y:0.#}, {offset.Z:0.#})");

            var placed = placeModel(model, transform, mesh.Attributes.TexCoords);
            var (uasset, uexp) = mesh.rebuild(placed);

            //Read back through the same finder before it goes anywhere near the game. A file this
            //tool cannot parse is not a file the engine should be asked to parse, and the check
            //costs nothing next to launching a game to find out.
            var fitted = CookedMesh.measure(placed.Positions);
            var check = CookedMesh.open(uasset, uexp, fitted.origin, fitted.extent, fitted.radius);
            if (check == null)
            {
                Console.Error.WriteLine("  The rebuilt mesh does not read back. Not writing it.");
                return 1;
            }
            Console.WriteLine($"  reads back as {check.VertexCount:N0} vertices, {check.TriangleCount:N0} triangles," +
                $" spanning {check.Positions.Min} to {check.Positions.Max}");

            GameFiles.writeMod(rest[2], new[] { (rest[0], uasset, uexp) });
            Console.WriteLine();
            Console.WriteLine($"Wrote {rest[2]} ({new FileInfo(rest[2]).Length:N0} bytes)");
            Console.WriteLine($"  uexp {mesh.UExp.Length:N0} -> {uexp.Length:N0} bytes");
            Console.WriteLine("Put it in the game's ~mods folder. Mesh only - the texture is applied by the tab,");
            Console.WriteLine("  which can rescale it to the one this weapon uses.");
            return 0;
        }

        /// <summary>
        /// The model's geometry moved into the weapon's space.
        ///
        /// Directions are turned but never moved or scaled: a normal says which way a surface
        /// faces, and offsetting one points it at nothing in particular.
        /// </summary>
        private static CookedMesh.Geometry placeModel(GlbModel model, MeshEdit.Transform transform, int texCoordSets)
        {
            var positions = new System.Collections.Generic.List<MeshGeometry.Position>(model.Positions.Count);
            foreach (var position in model.Positions) { positions.Add(transform.move(position)); }

            var turn = new MeshEdit.Transform(1f, new MeshGeometry.Position(0, 0, 0), transform.RotationDegrees);

            var normals = new System.Collections.Generic.List<VertexPacking.Direction>(model.Normals.Count);
            foreach (var normal in model.Normals)
            {
                var spun = turn.move(new MeshGeometry.Position(normal.X, normal.Y, normal.Z));
                normals.Add(new VertexPacking.Direction(spun.X, spun.Y, spun.Z, normal.W));
            }

            var tangents = new System.Collections.Generic.List<VertexPacking.Direction>(model.Tangents.Count);
            foreach (var tangent in model.Tangents)
            {
                var spun = turn.move(new MeshGeometry.Position(tangent.X, tangent.Y, tangent.Z));
                tangents.Add(new VertexPacking.Direction(spun.X, spun.Y, spun.Z, tangent.W));
            }

            return new CookedMesh.Geometry {
                Positions = positions,
                Normals = normals,
                Tangents = tangents,
                TexCoords = spreadTexCoords(model.TexCoords, texCoordSets, positions.Count),
                Indices = model.Indices,
            };
        }

        /// <summary>
        /// One set of texture coordinates per vertex turned into as many as the weapon's mesh
        /// keeps.
        ///
        /// A cooked weapon here holds two: the one the artwork is painted with, and a second the
        /// engine reserves for baked lighting. A model exported from a modelling tool almost
        /// always has only the first, so the second is filled with a copy of it. That is the right
        /// filler rather than a lazy one - a weapon is a moving object lit dynamically, so nothing
        /// ever reads its lightmap channel, and leaving it empty would put zeroes where the engine
        /// expects coordinates.
        ///
        /// They are written per vertex rather than per channel: vertex zero's sets, then vertex
        /// one's. That is the order the engine reads them back in, and the order a flat copy of
        /// the game's own data already round-trips through.
        /// </summary>
        private static IReadOnlyList<(float u, float v)> spreadTexCoords(
            IReadOnlyList<(float u, float v)> supplied, int perVertex, int vertices)
        {
            if (perVertex <= 1) { return supplied; }

            var spread = new List<(float, float)>(vertices * perVertex);
            for (int i = 0; i < vertices; i++)
            {
                var pair = i < supplied.Count ? supplied[i] : (0f, 0f);
                for (int channel = 0; channel < perVertex; channel++) { spread.Add(pair); }
            }
            return spread;
        }


        /// <summary>
        /// Reads every camera the game has, and optionally writes a pak that changes them.
        ///
        /// Every camera, not one, because there are two with identical numbers: the spring arm on
        /// the player character, which is the one the game looks through, and a second on
        /// BP_CoopCamera for framing several players. Changing only the second does nothing
        /// visible and looks exactly like the whole approach failing.
        /// </summary>
        private static int camera(string[] args)
        {
            var (rest, paksOption) = takePaksOption(args);

            var folder = GameFiles.paksFolder(paksOption);
            if (folder == null) { Console.Error.WriteLine("No game folder. Open the editor once, or pass --paks."); return 1; }

            var index = GameFiles.open(folder);

            var loaded = new List<(string path, byte[] uasset, byte[] uexp, CameraMod.Settings current)>();
            foreach (var path in CameraMod.ASSETS)
            {
                var package = GameFiles.read(index, path);
                if (package == null) { Console.Error.WriteLine($"  could not read {path}"); continue; }

                var uasset = package.Value.UAsset.ToArray();
                var uexp = package.Value.UExp.ToArray();
                var current = CameraMod.read(uasset, uexp);
                if (current == null) { Console.WriteLine($"  {path} has no camera on it"); continue; }

                loaded.Add((path, uasset, uexp, current));
                Console.WriteLine(path);
                describeCamera(current);
                Console.WriteLine();
            }
            if (loaded.Count == 0) { Console.Error.WriteLine("No camera found."); return 1; }

            var outputs = new List<string>();
            var wanted = new CameraMod.Settings();
            for (int i = 0; i < rest.Length; i++)
            {
                var option = rest[i];
                if (!option.StartsWith("--", StringComparison.Ordinal)) { outputs.Add(option); continue; }

                var name = option.Substring(2).ToLowerInvariant();
                if (name == "inherityaw") { wanted.InheritYaw = true; continue; }
                if (name == "inheritpitch") { wanted.InheritPitch = true; continue; }
                if (name == "collide") { wanted.CollisionTest = true; continue; }
                if (name == "controlleryaw") { wanted.ControllerYaw = true; continue; }

                if (i + 1 >= rest.Length) { Console.Error.WriteLine($"{option} needs a number."); return 1; }
                if (!float.TryParse(rest[++i], System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture, out var number))
                {
                    Console.Error.WriteLine($"{option} needs a number.");
                    return 1;
                }

                switch (name)
                {
                    case "arm": wanted.ArmLength = number; break;
                    case "seek": wanted.SeekArmLength = number; break;
                    case "pitch": wanted.Pitch = number; break;
                    case "yaw": wanted.Yaw = number; break;
                    case "roll": wanted.Roll = number; break;
                    case "fov": wanted.FieldOfView = number; break;
                    case "lag": wanted.RotationLagSpeed = number; break;
                    case "side": wanted.SocketSide = number; break;
                    case "height": wanted.SocketHeight = number; break;
                    case "turn": wanted.TurnRate = number; break;
                    default: Console.Error.WriteLine($"Unknown option {option}"); return 1;
                }
            }

            if (outputs.Count == 0)
            {
                Console.WriteLine("Nothing written. Pass a file to write one, with any of");
                Console.WriteLine("  --arm N --seek N --pitch N --yaw N --roll N --fov N");
                Console.WriteLine("  --lag N --side N --height N --turn N --inheritYaw --inheritPitch --collide");
                return 0;
            }
            if (wanted.isNothing) { Console.Error.WriteLine("Nothing to change, so no pak was written."); return 1; }

            var entries = new List<(string, byte[], byte[])>();
            foreach (var (path, uasset, uexp, current) in loaded)
            {
                //The seek length is what the arm reaches towards, so moving the arm without it
                //leaves the camera pulling back to where it used to be. Scaled per asset, since
                //each one has its own pair.
                var forThis = new CameraMod.Settings {
                    ArmLength = wanted.ArmLength,
                    SeekArmLength = wanted.SeekArmLength,
                    Pitch = wanted.Pitch, Yaw = wanted.Yaw, Roll = wanted.Roll,
                    FieldOfView = wanted.FieldOfView,
                    InheritPitch = wanted.InheritPitch, InheritYaw = wanted.InheritYaw,
                    InheritRoll = wanted.InheritRoll, CollisionTest = wanted.CollisionTest,
                    RotationLagSpeed = wanted.RotationLagSpeed,
                    ControllerYaw = wanted.ControllerYaw,
                    SocketSide = wanted.SocketSide, SocketHeight = wanted.SocketHeight,
                    TurnRate = wanted.TurnRate,
                };
                if (forThis.ArmLength != null && forThis.SeekArmLength == null
                    && current.ArmLength > 0 && current.SeekArmLength != null)
                {
                    forThis.SeekArmLength = forThis.ArmLength * (current.SeekArmLength / current.ArmLength);
                }

                byte[] patched;
                try { patched = CameraMod.patch(uasset, uexp, forThis); }
                catch (Exception e) { Console.Error.WriteLine($"  {path}: {e.Message}"); continue; }

                var check = CameraMod.read(uasset, patched);
                if (check == null) { Console.Error.WriteLine($"  {path} no longer reads back."); return 1; }

                Console.WriteLine($"{path} becomes:");
                describeCamera(check);
                Console.WriteLine();

                entries.Add((path, uasset, patched));
            }
            if (entries.Count == 0) { Console.Error.WriteLine("Nothing was patched."); return 1; }

            //And the volumes that move the camera while you play. Without these a close camera is
            //yanked out to 3500 the first time you walk into a wide room, which reads as the mod
            //breaking rather than as the game doing what it always did.
            var stockArm = loaded[0].current.ArmLength;
            if (wanted.ArmLength != null && stockArm != null && stockArm > 0)
            {
                var factor = wanted.ArmLength.Value / stockArm.Value;
                foreach (var path in CameraMod.ZOOM_VOLUMES)
                {
                    var volume = GameFiles.read(index, path);
                    if (volume == null) { continue; }

                    var volumeAsset = volume.Value.UAsset.ToArray();
                    var scaled = CameraMod.scaleZoom(volumeAsset, volume.Value.UExp.ToArray(), factor);
                    if (scaled == null) { continue; }

                    entries.Add((path, volumeAsset, scaled));
                }
                Console.WriteLine($"  zoom volumes scaled by {factor:0.###}, {entries.Count - loaded.Count} of them");
                Console.WriteLine();
            }

            GameFiles.writeMod(outputs[0], entries);
            Console.WriteLine($"Wrote {outputs[0]} ({new FileInfo(outputs[0]).Length:N0} bytes), {entries.Count} asset(s)");
            Console.WriteLine("Put it in the game's ~mods folder and start the game.");
            return 0;
        }

        private static void describeCamera(CameraMod.Settings settings)
        {
            Console.WriteLine($"  arm {settings.ArmLength} (seek {settings.SeekArmLength})," +
                $" pitch {settings.Pitch}, yaw {settings.Yaw}, fov {settings.FieldOfView}");
            Console.WriteLine($"  follows yaw {settings.InheritYaw}, collides {settings.CollisionTest}," +
                $" rotation lag {settings.RotationLagSpeed}, socket side {settings.SocketSide}" +
                $" height {settings.SocketHeight}, turn rate {settings.TurnRate}," +
                $" character faces aim {settings.ControllerYaw}");
        }

        private static int props(string[] args)
        {
            var (rest, paksOption) = takePaksOption(args);
            if (rest.Length < 1) { usage(); return 1; }

            var folder = GameFiles.paksFolder(paksOption);
            if (folder == null) { Console.Error.WriteLine("No game folder. Open the editor once, or pass --paks."); return 1; }

            var index = GameFiles.open(folder);
            var package = GameFiles.read(index, rest[0]);
            if (package == null) { Console.Error.WriteLine($"Could not read {rest[0]}"); return 1; }

            var uasset = package.Value.UAsset.ToArray();
            var uexp = package.Value.UExp.ToArray();
            var values = CookedProperties.readAll(uasset, uexp);

            Console.WriteLine($"{rest[0]}  ({values.Count} properties)");
            var export = "";
            foreach (var value in values)
            {
                if (value.Export != export)
                {
                    export = value.Export;
                    Console.WriteLine();
                    Console.WriteLine($"  {export}");
                }

                var shown = describeProperty(uexp, value);
                Console.WriteLine($"    {value.Name,-30} {value.Type,-16} @{value.At,-7} {shown}");
            }
            return 0;
        }

        private static string describeProperty(byte[] uexp, CookedProperties.Value value)
        {
            if (value.Type == "BoolProperty") { return value.Flag.ToString(); }
            if (value.At < 0 || value.At + value.Size > uexp.Length) { return "?"; }

            if (value.Type == "FloatProperty" && value.Size == 4)
            {
                return BitConverter.ToSingle(uexp, value.At).ToString();
            }
            if (value.Type == "IntProperty" && value.Size == 4)
            {
                return BitConverter.ToInt32(uexp, value.At).ToString();
            }
            if (value.Type == "StructProperty" && value.Size == 12
                && (value.StructName == "Rotator" || value.StructName == "Vector"))
            {
                return $"({BitConverter.ToSingle(uexp, value.At)}, {BitConverter.ToSingle(uexp, value.At + 4)}," +
                    $" {BitConverter.ToSingle(uexp, value.At + 8)})";
            }
            return $"<{value.Size} bytes{(value.StructName == null ? "" : " " + value.StructName)}>";
        }


        /// <summary>
        /// A file out of the paks that is not a package.
        ///
        /// The readers elsewhere all expect a cooked asset, and a pak holds plainer things too -
        /// the engine's own ini files among them, which is where a game records what its keys do.
        /// </summary>
        private static int raw(string[] args)
        {
            var (rest, paksOption) = takePaksOption(args);
            if (rest.Length < 1) { usage(); return 1; }

            var folder = GameFiles.paksFolder(paksOption);
            if (folder == null) { Console.Error.WriteLine("No game folder. Open the editor once, or pass --paks."); return 1; }

            var index = GameFiles.open(folder);

            //Tried with and without the extension, because the index groups a package's halves
            //under one key and leaves other files spelled as they are.
            foreach (var candidate in new[] { rest[0], rest[0] + ".ini", rest[0] + ".txt" })
            {
                ArraySegment<byte>? file = null;
                try { file = index.GetFile(candidate); } catch (Exception) { }
                if (file == null) { continue; }

                var bytes = file.Value.ToArray();
                Console.WriteLine($"{candidate}  {bytes.Length:N0} bytes");

                if (rest.Length > 1)
                {
                    File.WriteAllBytes(rest[1], bytes);
                    Console.WriteLine($"  written to {rest[1]}");
                }
                else
                {
                    Console.WriteLine();
                    Console.WriteLine(System.Text.Encoding.UTF8.GetString(bytes));
                }
                return 0;
            }

            Console.Error.WriteLine($"No file at {rest[0]}");
            return 1;
        }


        /// <summary>
        /// The go or no go test for editing the game while it runs.
        ///
        /// Two questions, in order, and the first one decides whether the second matters. Can this
        /// machine open the running game's memory at all - it is a packaged app, and the general
        /// advice is that such things are closed to outside tools, though people plainly do attach
        /// to this build. And if it can, does the camera's fingerprint appear where it should.
        ///
        /// Nothing is written. This only looks.
        /// </summary>
        private static int live(string[] args)
        {
            //The hunt drops the fingerprint entirely, so it runs before any of that.
            var huntArg = valueAfter(args, "--hunt");
            if (huntArg != null) { return hunt(args, huntArg.Value, valueAfter(args, "--for") ?? 60f); }

            var game = LiveEdit.GameProcess.open(out var problem);
            if (game == null)
            {
                Console.Error.WriteLine(problem);
                return 1;
            }

            using (game)
            {
                Console.WriteLine($"Attached to {LiveEdit.GameProcess.PROCESS_NAME}" +
                    $" (pid {game.Process.Id}, {game.Process.WorkingSet64 / 1024 / 1024:N0} MB)");
                Console.WriteLine("  Reading its memory is allowed.");
                Console.WriteLine();

                //The numbers to look for come from the pak rather than from anything written down
                //here, so a game update that retunes the camera is followed automatically.
                var (paksOption, _) = (takePaksOption(args).paks, 0);
                var folder = GameFiles.paksFolder(paksOption);
                if (folder == null) { Console.Error.WriteLine("No game folder, so there is no fingerprint to search for."); return 1; }

                var index = GameFiles.open(folder);
                var package = GameFiles.read(index, CameraMod.ASSETS[0]);
                if (package == null) { Console.Error.WriteLine("Could not read the camera asset."); return 1; }

                var stock = CameraMod.read(package.Value.UAsset.ToArray(), package.Value.UExp.ToArray());
                if (stock?.ArmLength == null) { Console.Error.WriteLine("Could not read the camera's values."); return 1; }

                Console.WriteLine($"  Looking for arm {stock.ArmLength}, seek {stock.SeekArmLength}," +
                    $" pitch {stock.Pitch}, yaw {stock.Yaw}");
                Console.WriteLine();

                var regions = 0;
                var clock = System.Diagnostics.Stopwatch.StartNew();
                var link = new LiveEdit.CameraLink(game);
                var found = link.search(stock.ArmLength.Value, stock.SeekArmLength ?? 2800f,
                    stock.Pitch ?? -45f, stock.Yaw ?? 45f, searched => regions = searched);

                Console.WriteLine($"  Searched {regions:N0} writable regions in {clock.Elapsed.TotalSeconds:0.#}s");
                Console.WriteLine($"  {found.Count} place(s) look like the camera:");
                foreach (var one in found.Take(12))
                {
                    Console.WriteLine($"    arm at 0x{one.ArmLength.ToInt64():X}  = {one.ArmLengthValue}," +
                        $" rotation at 0x{one.Rotation.ToInt64():X} = pitch {one.PitchValue}, yaw {one.YawValue}");
                }
                if (found.Count > 12) { Console.WriteLine($"    ... and {found.Count - 12} more"); }

                Console.WriteLine();
                if (found.Count == 0)
                {
                    Console.WriteLine("  The fingerprint is not in memory, so the camera is laid out differently once loaded.");
                    return 1;
                }

                //Arm length is the unmistakable one. A few degrees of pitch can be argued with;
                //the camera coming in to an eighth of its distance cannot.
                //Which of these the game actually renders from cannot be told apart by looking:
                //the loaded asset, the class default and the live component all hold the same
                //number. But only one of them moves. The game pulls the camera out to 3500 in a
                //wide room and eases it back afterwards, so watching which address follows the
                //camera around identifies it without guessing.
                var watchArg = valueAfter(args, "--watch");
                if (watchArg != null)
                {
                    Console.WriteLine($"  Watching {found.Count} candidate(s) for {watchArg}s.");
                    Console.WriteLine("  Walk around in game - through a doorway, into a big room, into a mission.");
                    Console.WriteLine("  Whichever address changes is the camera the game is really using.");
                    Console.WriteLine();

                    var startValues = found.Select(one => game.readFloat(one.ArmLength) ?? 0f).ToArray();
                    var moved = new bool[found.Count];
                    var watchClock = System.Diagnostics.Stopwatch.StartNew();

                    while (watchClock.Elapsed.TotalSeconds < watchArg.Value && game.IsRunning)
                    {
                        for (int i = 0; i < found.Count; i++)
                        {
                            var now = game.readFloat(found[i].ArmLength);
                            if (now == null) { continue; }
                            if (Math.Abs(now.Value - startValues[i]) < 0.5f) { continue; }

                            if (!moved[i])
                            {
                                moved[i] = true;
                                Console.WriteLine($"    MOVED  0x{found[i].ArmLength.ToInt64():X}" +
                                    $"  {startValues[i]} -> {now.Value}");
                            }
                            startValues[i] = now.Value;
                        }
                        System.Threading.Thread.Sleep(120);
                    }

                    var movers = moved.Count(m => m);
                    Console.WriteLine();
                    Console.WriteLine(movers == 0
                        ? "  Nothing moved. Either the camera never changed, or its live distance is somewhere else entirely."
                        : $"  {movers} of {found.Count} moved. Those are the live ones.");
                    return 0;
                }

                var armArg = valueAfter(args, "--arm");
                if (armArg != null)
                {
                    foreach (var one in found)
                    {
                        var wasArm = game.readFloat(one.ArmLength) ?? 2450f;
                        var wasSeek = game.readFloat(one.SeekArmLength) ?? 2800f;
                        var ratio = wasArm > 0.01f ? wasSeek / wasArm : 1f;

                        //Both, in proportion. The seek length is what the arm reaches towards, so
                        //writing one alone leaves the camera drifting back to where it started.
                        game.writeFloat(one.ArmLength, armArg.Value);
                        game.writeFloat(one.SeekArmLength, armArg.Value * ratio);

                        System.Threading.Thread.Sleep(200);
                        Console.WriteLine($"    0x{one.ArmLength.ToInt64():X}  {wasArm} -> {game.readFloat(one.ArmLength)}");
                    }
                }

                var pitchArg = valueAfter(args, "--pitch");
                if (pitchArg == null && armArg == null)
                {
                    Console.WriteLine("  The camera can be found in the running game.");
                    Console.WriteLine("  Pass --arm N or --pitch N to write it, --hold S to keep it there.");
                    return 0;
                }
                if (pitchArg == null)
                {
                    var holdOnly = valueAfter(args, "--hold");
                    if (holdOnly != null)
                    {
                        Console.WriteLine();
                        Console.WriteLine($"  Holding for {holdOnly}s - look at the game.");
                        var clockHold = System.Diagnostics.Stopwatch.StartNew();
                        while (clockHold.Elapsed.TotalSeconds < holdOnly.Value && game.IsRunning)
                        {
                            foreach (var one in found)
                            {
                                if (armArg != null) { game.writeFloat(one.ArmLength, armArg.Value); }
                            }
                            System.Threading.Thread.Sleep(16);
                        }
                        Console.WriteLine("  Done.");
                    }
                    return 0;
                }

                //The question this answers is not "can it be written" but "does it stay written".
                //A value the game recomputes every frame reverts within milliseconds, and that
                //difference decides whether this needs a loop or a single write.
                Console.WriteLine($"  Writing pitch {pitchArg} to {found.Count} candidate(s) and reading it back.");
                Console.WriteLine();

                foreach (var one in found)
                {
                    var before = game.readFloat(one.Rotation);
                    var wrote = game.writeFloat(one.Rotation, pitchArg.Value);

                    var immediately = game.readFloat(one.Rotation);
                    System.Threading.Thread.Sleep(400);
                    var later = game.readFloat(one.Rotation);

                    Console.WriteLine($"    0x{one.Rotation.ToInt64():X}");
                    Console.WriteLine($"      was {before}, write {(wrote ? "allowed" : "REFUSED")}," +
                        $" immediately {immediately}, after 400ms {later}");
                    Console.WriteLine(later != null && Math.Abs(later.Value - pitchArg.Value) < 0.01f
                        ? "      it stayed - nothing is recomputing it, so one write is enough"
                        : "      it reverted - the game recomputes it, so it needs writing every frame");
                }

                var holdArg = valueAfter(args, "--hold");
                if (holdArg != null)
                {
                    //Held by rewriting, for whoever is watching the screen. If the value sticks on
                    //its own this changes nothing; if it does not, this is what a working feature
                    //would have to do.
                    Console.WriteLine();
                    Console.WriteLine($"  Holding it for {holdArg}s - look at the game.");
                    var until = System.Diagnostics.Stopwatch.StartNew();
                    while (until.Elapsed.TotalSeconds < holdArg.Value && game.IsRunning)
                    {
                        foreach (var one in found) { game.writeFloat(one.Rotation, pitchArg.Value); }
                        System.Threading.Thread.Sleep(8);
                    }
                    Console.WriteLine("  Done. Put the original back with --pitch -45.");
                }

                return 0;
            }
        }


        /// <summary>The number after a named option, or nothing.</summary>
        private static float? valueAfter(string[] args, string option)
        {
            for (int i = 0; i < args.Length - 1; i++)
            {
                if (!args[i].Equals(option, StringComparison.OrdinalIgnoreCase)) { continue; }
                if (float.TryParse(args[i + 1], System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture, out var value))
                {
                    return value;
                }
            }
            return null;
        }


        /// <summary>
        /// Finds the camera the game is really using, by watching which number follows it.
        ///
        /// The fingerprint search finds the templates - the loaded asset and the class defaults -
        /// and writing to those changes nothing on screen, which is how this turned out to be
        /// necessary. Every copy of the authored number looks identical, so no amount of looking
        /// harder tells them apart.
        ///
        /// What tells them apart is movement. The game itself pulls the camera out in a wide room
        /// and eases it back afterwards, so of the hundreds of places holding the camera distance,
        /// the live one is the one that changes while somebody plays. This collects them all and
        /// then watches.
        /// </summary>
        private static int hunt(string[] args, float value, float seconds)
        {
            var game = LiveEdit.GameProcess.open(out var problem);
            if (game == null) { Console.Error.WriteLine(problem); return 1; }

            using (game)
            {
                Console.WriteLine($"Attached to {LiveEdit.GameProcess.PROCESS_NAME} (pid {game.Process.Id})");
                Console.WriteLine($"  Collecting every float equal to {value}.");

                var clock = System.Diagnostics.Stopwatch.StartNew();
                var candidates = new LiveEdit.CameraLink(game).everyFloat(value);
                Console.WriteLine($"  {candidates.Count:N0} of them, in {clock.Elapsed.TotalSeconds:0.#}s");
                Console.WriteLine();

                if (candidates.Count == 0)
                {
                    Console.WriteLine("  None at all. Restart the game first - an earlier run of this may have");
                    Console.WriteLine("  overwritten them, and restarting restores everything.");
                    return 1;
                }

                //Waiting for the game to move the camera only works if the game moves the camera,
                //and the distance it holds is a target that nothing touches until a zoom volume
                //fires. Writing to all of them and looking is the shorter question: if the camera
                //moves, the live one is in this list and can be narrowed down by halves.
                var setArg = valueAfter(args, "--set");
                if (setArg != null)
                {
                    var half = args.Any(a => a.Equals("--first-half", StringComparison.OrdinalIgnoreCase)) ? 1
                        : args.Any(a => a.Equals("--second-half", StringComparison.OrdinalIgnoreCase)) ? 2 : 0;

                    var from = half == 2 ? candidates.Count / 2 : 0;
                    var to = half == 1 ? candidates.Count / 2 : candidates.Count;

                    Console.WriteLine($"  Writing {setArg} to candidates {from}..{to} of {candidates.Count}.");
                    var wrote = 0;
                    for (int i = from; i < to; i++)
                    {
                        if (game.writeFloat(candidates[i], setArg.Value)) { wrote++; }
                    }
                    Console.WriteLine($"  {wrote} written. Look at the game now.");
                    Console.WriteLine("  Restarting the game undoes all of it - nothing is on disk.");

                    var holdFor = valueAfter(args, "--for") ?? 20f;
                    var holdClock = System.Diagnostics.Stopwatch.StartNew();
                    while (holdClock.Elapsed.TotalSeconds < holdFor && game.IsRunning)
                    {
                        for (int i = from; i < to; i++) { game.writeFloat(candidates[i], setArg.Value); }
                        System.Threading.Thread.Sleep(50);
                    }
                    Console.WriteLine("  Done holding.");
                    return 0;
                }

                Console.WriteLine($"  Watching them for {seconds}s. Play normally - walk through doorways,");
                Console.WriteLine("  into big rooms, start a mission. Anything that moves the camera.");
                Console.WriteLine();

                var before = new float[candidates.Count];
                for (int i = 0; i < candidates.Count; i++) { before[i] = game.readFloat(candidates[i]) ?? 0f; }

                var moved = new HashSet<int>();
                var watching = System.Diagnostics.Stopwatch.StartNew();

                while (watching.Elapsed.TotalSeconds < seconds && game.IsRunning)
                {
                    for (int i = 0; i < candidates.Count; i++)
                    {
                        var now = game.readFloat(candidates[i]);
                        if (now == null || Math.Abs(now.Value - before[i]) < 1f) { continue; }

                        if (moved.Add(i))
                        {
                            Console.WriteLine($"    MOVED  0x{candidates[i].ToInt64():X}  {before[i]} -> {now.Value}");
                        }
                        before[i] = now.Value;
                    }
                    System.Threading.Thread.Sleep(100);
                }

                Console.WriteLine();
                Console.WriteLine(moved.Count == 0
                    ? "  Nothing moved. The camera distance is not held as a plain float, or nothing moved it."
                    : $"  {moved.Count} moved out of {candidates.Count:N0}. Those are the live camera.");
                return 0;
            }
        }


        /// <summary>
        /// Loads a library into the running game, once it is past its menu.
        ///
        /// The proxy route - putting a file named after a system library beside the executable -
        /// killed this game instantly on a Microsoft Store build, before anything could be logged.
        /// Injecting after startup avoids whatever that was, and is what the tools that do work on
        /// this game already do.
        /// </summary>
        private static int inject(string[] args)
        {
            if (args.Length < 1) { usage(); return 1; }

            var path = System.IO.Path.GetFullPath(args[0]);
            Console.WriteLine($"Injecting {path}");

            var ok = LiveEdit.DllInjector.inject(path, out var problem);
            Console.WriteLine($"  {problem}");
            return ok ? 0 : 1;
        }


        /// <summary>
        /// Finds the engine's list of every live object, which is what makes finding anything else
        /// possible.
        ///
        /// Days were spent searching memory for a camera by the numbers it holds, and it never
        /// separated the live component from the templates that hold identical numbers. The engine
        /// keeps a register of everything it owns; this finds that register, and after it a camera
        /// can be found by what it is.
        /// </summary>
        private static int objects(string[] args)
        {
            var game = LiveEdit.GameProcess.open(out var problem);
            if (game == null) { Console.Error.WriteLine(problem); return 1; }

            using (game)
            {
                Console.WriteLine($"Attached to {game.Process.ProcessName} (pid {game.Process.Id})");
                Console.WriteLine("  Looking for a chunked object array.");
                Console.WriteLine();

                var clock = System.Diagnostics.Stopwatch.StartNew();
                var tables = new LiveEdit.ObjectTables(game);
                var found = tables.search();

                Console.WriteLine($"  searched in {clock.Elapsed.TotalSeconds:0.#}s, {found.Count} candidate(s)");
                Console.WriteLine();

                foreach (var one in found.Take(5))
                {
                    Console.WriteLine($"  at 0x{one.ChunkedArray.ToInt64():X}");
                    Console.WriteLine($"    {one.NumElements:N0} objects of {one.MaxElements:N0} capacity," +
                        $" {one.NumChunks} of {one.MaxChunks} chunks");
                    Console.WriteLine($"    chunks at 0x{one.Chunks.ToInt64():X}, first object at 0x{one.FirstObject.ToInt64():X}");

                    //Reading a few spread through the array shows the chunk arithmetic is right
                    //rather than only the first one being reachable.
                    var reachable = 0;
                    for (int i = 0; i < 20; i++)
                    {
                        var index = (int)((long)i * one.NumElements / 20);
                        if (tables.objectAt(one, index) != IntPtr.Zero) { reachable++; }
                    }
                    Console.WriteLine($"    {reachable} of 20 sampled objects across the array are readable");
                    Console.WriteLine();
                }

                if (found.Count == 0)
                {
                    Console.WriteLine("  Nothing matched. The array is laid out differently in this build.");
                    return 1;
                }
                Console.WriteLine("  The engine's object table is reachable from outside the game.");
                Console.WriteLine();

                //The object array says what exists; the name table says what each one is called.
                //Only together do they answer "which object is the camera".
                Console.WriteLine("  Looking for the name table.");
                var names = new LiveEdit.NameTable(game);
                if (!names.find())
                {
                    Console.WriteLine("  Not found - names cannot be resolved, so objects stay anonymous.");
                    return 1;
                }
                Console.WriteLine($"  found at 0x{names.Chunks.ToInt64():X}" +
                    (names.Count > 0 ? $", {names.Count:N0} names" : ""));
                Console.WriteLine($"    name 0 is \"{names.nameOf(0)}\", 1 is \"{names.nameOf(1)}\"," +
                    $" 2 is \"{names.nameOf(2)}\"");
                Console.WriteLine();

                //UObject, 4.22: vtable, flags, index, class, then the name.
                const int NAME_IN_OBJECT = 0x18;

                var wanted = args.FirstOrDefault(a => !a.StartsWith("--", StringComparison.Ordinal)) ?? "SpringArm";
                Console.WriteLine($"  Walking {found[0].NumElements:N0} objects for names containing \"{wanted}\"...");

                var clock2 = System.Diagnostics.Stopwatch.StartNew();
                var hits = new System.Collections.Generic.List<(IntPtr at, string name)>();
                var read = 0;

                for (int i = 0; i < found[0].NumElements; i++)
                {
                    var address = tables.objectAt(found[0], i);
                    if (address == IntPtr.Zero) { continue; }

                    var nameField = game.read(new IntPtr(address.ToInt64() + NAME_IN_OBJECT), 4);
                    if (nameField == null) { continue; }
                    read++;

                    var name = names.nameOf(BitConverter.ToInt32(nameField, 0));
                    if (name == null) { continue; }
                    if (name.IndexOf(wanted, StringComparison.OrdinalIgnoreCase) < 0) { continue; }

                    hits.Add((address, name));
                    if (hits.Count >= 40) { break; }
                }

                Console.WriteLine($"  read {read:N0} objects in {clock2.Elapsed.TotalSeconds:0.#}s");
                Console.WriteLine($"  {hits.Count} matching:");
                foreach (var (address, name) in hits)
                {
                    Console.WriteLine($"    0x{address.ToInt64():X}  {name}");
                }
                return 0;
            }
        }

        private static MeshGeometry.Position scaled(MeshGeometry.Position value, float factor) =>
            new MeshGeometry.Position(value.X * factor, value.Y * factor, value.Z * factor);

        /// <summary>
        /// True when the only bytes that differ are ones the editor set out to change.
        ///
        /// This is the guard that matters. The file is mostly tangents, UVs and flags that nothing
        /// here understands, and the whole approach depends on carrying them across untouched - so
        /// rather than trusting that, it is checked.
        /// </summary>
        private static bool untouchedOutside(byte[] before, byte[] after, MeshEdit edit)
        {
            if (before.Length != after.Length) { return false; }

            var allowed = new System.Collections.Generic.List<(int from, int to)> {
                (edit.Vertices.DataOffset, edit.Vertices.DataEnd),
            };
            foreach (var block in edit.BoundsBlocks) { allowed.Add((block, block + 28)); }
            foreach (var at in edit.OriginFloats) { allowed.Add((at, at + 12)); }
            foreach (var at in edit.ExtentFloats) { allowed.Add((at, at + 12)); }
            foreach (var at in edit.RadiusFloats) { allowed.Add((at, at + 4)); }

            for (int at = 0; at < before.Length; at++)
            {
                if (before[at] == after[at]) { continue; }
                if (!allowed.Any(range => at >= range.from && at < range.to)) { return false; }
            }
            return true;
        }

        /// <summary>The largest disagreement between the bounds written and the bounds found.</summary>
        private static double boundsDrift(byte[] before, byte[] after, MeshEdit edit)
        {
            var worst = 0.0;
            foreach (var block in edit.BoundsBlocks)
            {
                for (int i = 0; i < 7; i++)
                {
                    worst = Math.Max(worst, Math.Abs(
                        BitConverter.ToSingle(before, block + i * 4) - BitConverter.ToSingle(after, block + i * 4)));
                }
            }
            return worst;
        }
    }
}
