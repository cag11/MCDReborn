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
    }
}
