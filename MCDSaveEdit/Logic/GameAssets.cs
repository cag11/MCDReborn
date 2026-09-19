using MCDSaveEdit.Services;
using System;
using System.Collections.Generic;
using System.Linq;
#nullable enable

namespace MCDSaveEdit.Logic
{
    /// <summary>
    /// Everything in the game's paks, addressed the way Unreal addresses it.
    ///
    /// This exists because of what reading other people's mods turned up. A mod does not ship the
    /// game's content and it does not need to: it refers to the game's own materials, textures,
    /// skeletons and sound cues by path, ships nothing for them, and the engine resolves each
    /// path against the game's paks when the level loads. A cosmetics mod read while building this
    /// names twenty seven of them and ships none; a rebuilt player blueprint names a hundred and
    /// five. The whole trick is that an import is a string.
    ///
    /// What that costs the person doing it is an empty asset of the same name in the same folder,
    /// so the editor has something to point at while they work - and then leaving that empty one
    /// out of the pak. The Mod Kit does the leaving-out as a rule, excluding every M_ and MI_ from
    /// what it copies, which is the same idea written as a robocopy filter.
    ///
    /// So the hard part is not the technique, it is knowing the exact path and spelling. That is
    /// what this is for. The index is already loaded, every path is already in it, and nothing
    /// else in this program was letting anybody read it.
    /// </summary>
    public static class GameAssets
    {
        /// <summary>One asset, as the engine would name it.</summary>
        public sealed class Asset
        {
            public Asset(string enginePath, string kind)
            {
                EnginePath = enginePath;
                Kind = kind;

                var cut = enginePath.LastIndexOf('/');
                Name = cut < 0 ? enginePath : enginePath.Substring(cut + 1);
                Folder = cut < 0 ? enginePath : enginePath.Substring(0, cut);
            }

            /// <summary>"/Game/Actors/Characters/Enemies/Creeper/MI_Creeper".</summary>
            public string EnginePath { get; }

            /// <summary>"MI_Creeper".</summary>
            public string Name { get; }

            /// <summary>"/Game/Actors/Characters/Enemies/Creeper".</summary>
            public string Folder { get; }

            /// <summary>What it looks like it is. See kindOf for how much that is worth.</summary>
            public string Kind { get; }

            public override string ToString() => EnginePath;
        }

        /// <summary>The kinds worth filtering by, in the order they are offered.</summary>
        public static readonly string[] KINDS =
        {
            "Blueprint", "Widget", "Level", "Material", "Material instance", "Texture",
            "Skeletal mesh", "Static mesh", "Skeleton", "Physics asset", "Animation",
            "Particles", "Sound", "Data table", "Other",
        };

        private static IReadOnlyList<Asset>? _all;

        /// <summary>
        /// Every asset in the paks, read once.
        ///
        /// Tens of thousands of them, so the list is built on the first ask and kept. The paks do
        /// not change while the program runs - and if the folder is repointed, everything else
        /// that cached anything about them is stale too.
        /// </summary>
        public static IReadOnlyList<Asset> all()
        {
            if (_all != null) { return _all; }

            var paks = CustomSkins.index;
            if (paks == null) { return Array.Empty<Asset>(); }

            var found = new List<Asset>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (var entry in paks)
            {
                //The index is a list of packages rather than of files: one entry per asset, with
                //no extension on it. Which is convenient - the .uexp and .ubulk an asset is made
                //of never appear, so there is nothing to filter out - and was worth finding out
                //the hard way, having first written this to keep only the .uasset entries and
                //got an empty list for it.
                var engine = enginePathOf(CustomSkins.assetPath(entry).Replace('\\', '/'));
                if (engine == null || !seen.Add(engine)) { continue; }

                found.Add(new Asset(engine, kindOf(engine)));
            }

            return _all = found
                .OrderBy(asset => asset.EnginePath, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        private static HashSet<string>? _paths;

        /// <summary>
        /// Whether the game already has an asset at that path.
        ///
        /// Which is the difference between a mod adding something and a mod replacing something,
        /// and there is no other way to tell: both are a file in a pak at a path, and only the
        /// game's own contents say which of the two it is.
        /// </summary>
        public static bool has(string enginePath)
        {
            if (_paths == null)
            {
                _paths = new HashSet<string>(
                    all().Select(asset => asset.EnginePath), StringComparer.OrdinalIgnoreCase);
            }

            return _paths.Contains(enginePath);
        }

        /// <summary>
        /// The assets matching what was typed, and how many there were before the cap.
        ///
        /// Every word has to appear somewhere in the path, in any order, which is how somebody
        /// looking for the creeper's material types "creeper mi" and finds it. Matching the words
        /// as one string instead would need them typed in the order the game happens to use.
        /// </summary>
        public static (IReadOnlyList<Asset> shown, int matched) search(string? query, string? kind, int cap = 3000)
        {
            IEnumerable<Asset> matching = all();

            if (!string.IsNullOrWhiteSpace(kind) && kind != KINDS[KINDS.Length - 1])
            {
                matching = matching.Where(asset => asset.Kind == kind);
            }
            else if (kind == KINDS[KINDS.Length - 1])
            {
                matching = matching.Where(asset => asset.Kind == "Other");
            }

            var words = (query ?? string.Empty)
                .Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);

            foreach (var word in words)
            {
                var it = word;
                matching = matching.Where(asset =>
                    asset.EnginePath.IndexOf(it, StringComparison.OrdinalIgnoreCase) >= 0);
            }

            //Counted before the cap rather than after, because "3000 of 3000" would hide that
            //there are eleven thousand and the one being looked for is not among those shown.
            var all2 = matching.ToList();
            return (all2.Count > cap ? all2.Take(cap).ToList() : all2, all2.Count);
        }

        /// <summary>
        /// What an asset actually is, read out of the package rather than guessed from its name.
        ///
        /// Costs a read, so it is asked for one asset at a time - the one being looked at - rather
        /// than for the whole list. A cooked package names its own class in its name table, so
        /// this is a matter of looking for one of a known set. Nothing recognisable means the
        /// guess from the name stands, which is the honest answer rather than a wrong one.
        /// </summary>
        public static string? readKindOf(string enginePath)
        {
            var paks = CustomSkins.index;
            if (paks == null) { return null; }

            //Back to how the paks spell it, which is the only spelling the index answers to.
            var inPak = "/Dungeons/Content/" + enginePath.Substring("/Game/".Length);

            try
            {
                var package = paks.extractPackage(inPak);
                if (package == null) { return null; }

                var names = new HashSet<string>(
                    CookedProperties.readNamesOf(package.Value.UAsset.ToArray()),
                    StringComparer.Ordinal);

                //Most specific first: a widget's package names both WidgetBlueprintGeneratedClass
                //and BlueprintGeneratedClass, and answering "Blueprint" to a widget is the sort of
                //half-right that sends somebody to make the wrong kind of stub.
                foreach (var pair in CLASSES)
                {
                    if (names.Contains(pair.Key)) { return pair.Value; }
                }
            }
            catch (Exception)
            {
                //An asset this reader cannot open is not worth a dialog: the guess still stands,
                //and the path - which is the thing actually wanted here - is right either way.
                return null;
            }

            return null;
        }

        /// <summary>The class names a cooked package can carry, most specific first.</summary>
        private static readonly KeyValuePair<string, string>[] CLASSES =
        {
            //A level goes first, before any blueprint. Almost every map in this game carries a
            //level blueprint, so its package names BlueprintGeneratedClass too - and asked in the
            //other order, every map in the game came back as a blueprint.
            new KeyValuePair<string, string>("World", "Level"),
            new KeyValuePair<string, string>("DataTable", "Data table"),
            new KeyValuePair<string, string>("WidgetBlueprintGeneratedClass", "Widget"),
            new KeyValuePair<string, string>("AnimBlueprintGeneratedClass", "Animation"),
            new KeyValuePair<string, string>("BlueprintGeneratedClass", "Blueprint"),
            new KeyValuePair<string, string>("MaterialInstanceConstant", "Material instance"),
            new KeyValuePair<string, string>("PhysicsAsset", "Physics asset"),
            new KeyValuePair<string, string>("SkeletalMesh", "Skeletal mesh"),
            new KeyValuePair<string, string>("StaticMesh", "Static mesh"),
            new KeyValuePair<string, string>("TextureCube", "Texture"),
            new KeyValuePair<string, string>("Texture2D", "Texture"),
            new KeyValuePair<string, string>("AnimMontage", "Animation"),
            new KeyValuePair<string, string>("AnimSequence", "Animation"),
            new KeyValuePair<string, string>("ParticleSystem", "Particles"),
            new KeyValuePair<string, string>("SoundCue", "Sound"),
            new KeyValuePair<string, string>("SoundWave", "Sound"),
            new KeyValuePair<string, string>("Skeleton", "Skeleton"),
            new KeyValuePair<string, string>("MaterialFunction", "Material"),
            new KeyValuePair<string, string>("Material", "Material"),
            new KeyValuePair<string, string>("World", "Level"),
        };

        /// <summary>
        /// What an asset looks like it is, from its name.
        ///
        /// A guess, and said to be one. Reading eighty thousand packages to fill a list nobody has
        /// scrolled to yet is not worth it, and this game names things carefully enough that the
        /// guess is nearly always right: SM_ and SK_ and T_ and MI_ are used consistently across
        /// every pak. The one being looked at gets read properly - see readKindOf.
        /// </summary>
        private static string kindOf(string enginePath)
        {
            //Nothing in the entry says whether a package is a level, because the index carries no
            //extension - so the folder has to. This game keeps its maps under Maps and SubLevels
            //and nothing else there, which makes it a good guess and still only a guess.
            if (enginePath.IndexOf("/Maps/", StringComparison.OrdinalIgnoreCase) >= 0
                || enginePath.IndexOf("/SubLevels/", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                return "Level";
            }

            //Animations are the one family this game does not prefix - a creeper's walk cycle is
            //"Creeper_Walk" and nothing about the name says what it is. The folder does, and
            //reading a few back against their packages is what turned that up: every one of them
            //came out of the guess as "Other" and out of the package as an animation.
            if (enginePath.IndexOf("/Animations/", StringComparison.OrdinalIgnoreCase) >= 0
                || enginePath.IndexOf("/Montages/", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                return "Animation";
            }

            //A data table does not open with the package reader here, so the folder is the only
            //thing that will say - and the folder is reliable, because this game keeps all of
            //them in one place and puts nothing else there.
            if (enginePath.IndexOf("/DataTables/", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                return "Data table";
            }

            var cut = enginePath.LastIndexOf('/');
            var name = cut < 0 ? enginePath : enginePath.Substring(cut + 1);

            if (starts(name, "AnimBP_") || starts(name, "ABP_")) { return "Animation"; }
            if (name.EndsWith("_PhysicsAsset", StringComparison.OrdinalIgnoreCase)) { return "Physics asset"; }
            if (name.EndsWith("_Skeleton", StringComparison.OrdinalIgnoreCase)) { return "Skeleton"; }
            if (name.EndsWith("_Montage", StringComparison.OrdinalIgnoreCase)) { return "Animation"; }

            if (starts(name, "BP_")) { return "Blueprint"; }
            if (starts(name, "UMG_") || starts(name, "WBP_") || starts(name, "W_")) { return "Widget"; }
            if (starts(name, "MI_")) { return "Material instance"; }
            if (starts(name, "M_") || starts(name, "Master_")) { return "Material"; }
            if (starts(name, "T_")) { return "Texture"; }
            if (starts(name, "SK_")) { return "Skeletal mesh"; }
            if (starts(name, "SM_")) { return "Static mesh"; }
            if (starts(name, "PS_") || starts(name, "P_")) { return "Particles"; }
            if (starts(name, "A_") || starts(name, "AS_")) { return "Animation"; }
            if (starts(name, "sfx_") || starts(name, "mus_")
                || enginePath.IndexOf("/AudioForce/", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                return "Sound";
            }

            return "Other";
        }

        private static bool starts(string name, string prefix)
            => name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase);

        /// <summary>
        /// "/Dungeons/Content/Actors/Axe" as the engine spells it: "/Game/Actors/Axe".
        ///
        /// The paks are addressed by the cooked layout and the engine by the project's own, and
        /// the two differ only in that prefix. Anything outside Content - the raw data folders,
        /// the resource packs - has no engine path and is left out.
        /// </summary>
        private static string? enginePathOf(string pakPath)
        {
            const string cooked = "/Dungeons/Content/";

            var at = pakPath.IndexOf(cooked, StringComparison.OrdinalIgnoreCase);
            if (at < 0) { return null; }

            return "/Game/" + pakPath.Substring(at + cooked.Length);
        }
    }
}
