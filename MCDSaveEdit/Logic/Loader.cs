using MCDSaveEdit.Services;
using PakReader.Pak;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
#nullable enable

namespace MCDSaveEdit.Logic
{
    /// <summary>
    /// This app's own blueprint loader: the thing that makes a payload run.
    ///
    /// Nothing this program writes can make the game *do* anything new, because doing needs
    /// calling and this project does not inject. A loader is the way round it. It is two pieces:
    ///
    ///   an ANCHOR - one of the game's own actors, replaced with one that also starts a widget
    ///   a WIDGET  - which watches what the game is running and loads any level in the folder
    ///               that matches
    ///
    /// The anchor is why it starts at all: the game spawns that actor by itself, so replacing it
    /// is a way of being run without being called. The widget is what persists, because a widget
    /// added to the viewport outlives the actor that made it.
    ///
    /// Both halves are authored in Unreal. That is not a gap in this file - a blueprint is
    /// compiled bytecode and there is no honest way to write one from out here. What this does is
    /// everything either side of that: choosing the anchor, packaging the result, installing it,
    /// and saying whether it is there.
    /// </summary>
    public static class Loader
    {
        /// <summary>
        /// The actor replaced to get the loader started.
        ///
        /// The same one the community's loader uses, which is not copying them - which of the
        /// game's actors to replace is a fact about the game rather than anybody's work, and it
        /// is the right answer for reasons that were measured here before they were recognised
        /// there.
        ///
        /// The first choice here was BP_GodRay_Light, on coverage: every one of the game's 4,156
        /// levels was read and its placements counted, and that appears in 308 of them against
        /// the tent's 18. Counting where rather than how many is what corrected it. The 308 are
        /// mission tiles and not one of them is the Camp, so a loader anchored there would never
        /// start at the one moment somebody is testing a payload. The tent is in the Camp, and
        /// the Camp is the only place an anchor genuinely has to be: the widget it starts outlives
        /// the level that started it, so being passed through once is enough.
        ///
        /// What else was passed over, once Camp presence was the question:
        ///
        ///   BP_MerchantActor          26 Camp levels, and gameplay. Not decor.
        ///   BP_SoundCue               17 Camp levels, 220 names, driven by per-instance
        ///                             variables - getting it subtly wrong means silence, which
        ///                             is the hardest kind of fault to trace back to a mod.
        ///   BP_Hideable_Pointlight    18 Camp levels and no asset references at all, which
        ///                             looked ideal until the per-placement light settings it
        ///                             carries made a faithful rebuild the hard part.
        ///
        /// A worry that turned out to be nothing: every one of these imports /Script/Dungeons,
        /// the game's own native module, which cannot be recreated in a blank project. It does
        /// not matter, because a replacement does not inherit from what it replaces - it simply
        /// occupies the path. The community loader's own BP_Tent imports CoreUObject, Engine and
        /// UMG and nothing else.
        ///
        /// The real consequence of sharing an anchor is a practical one rather than a legal one,
        /// and it is handled: see clashes.
        /// </summary>
        public const string ANCHOR = "/Game/Decor/Prefabs/Tent/BP_Tent";

        /// <summary>What the anchor has to carry over to still look like itself.</summary>
        public static readonly string[] ANCHOR_KEEPS =
        {
            "/Game/Decor/Prefabs/Tent/SM_Tent",
            "/Game/Decor/Prefabs/Tent/MI_Tent",
        };

        /// <summary>The widget that does the watching and the loading.</summary>
        public const string WIDGET = "/Game/MCDReborn/UMG_MCDRebornLoader";

        /// <summary>
        /// The game modes the widget tells apart, and the folder each one loads from.
        ///
        /// The names are the game's own: a widget asks the current game mode what class it is and
        /// compares. Which is the only reliable way to know where you are - level names change
        /// between missions and DLC, and the mode does not.
        /// </summary>
        public static readonly IReadOnlyList<(string trigger, string gameMode)> WATCHES = new[]
        {
            ("Menu", "/Game/GameModes/Menu/BP_MenuGameMode"),
            ("Lobby", "/Game/GameModes/Lobby/BP_LobbyGameMode"),
            ("Ingame", "/Game/GameModes/Ingame/BP_IngameGameMode"),
        };

        //Its own name, so that it can be found and removed on its own. A payload is marked
        //Payload_; this is the thing that runs them.
        private const string PREFIX = "Loader";
        private const string FOUND_AS = "MCDReborn_Loader";

        /// <summary>The installed loader pak, or nothing.</summary>
        public static string? installed()
        {
            var folders = new List<string>();
            if (CustomSkins.paksFolder != null) { folders.Add(CustomSkins.paksFolder); }
            if (CustomSkins.modsFolder != null) { folders.Add(CustomSkins.modsFolder); }

            return folders
                .Where(Directory.Exists)
                .SelectMany(folder => Directory.GetFiles(folder, FOUND_AS + "*.pak"))
                .OrderBy(file => file, StringComparer.OrdinalIgnoreCase)
                .FirstOrDefault();
        }

        public static bool isInstalled => installed() != null;

        /// <summary>
        /// Other mods installed that replace the same actor this loader does.
        ///
        /// Sharing an anchor with the community's loader is the right call - it is the actor in
        /// the Camp that can be rebuilt from one static mesh, and there is no second answer to
        /// that question worth having. What it costs is that both cannot be installed at once:
        /// two paks replacing one path is a fight the alphabet settles, silently, and the loser's
        /// loader simply never starts.
        ///
        /// So it is said out loud instead. Not refused - somebody may well want theirs to win
        /// while testing something built against it - but never left to be discovered by a
        /// payload that does nothing.
        /// </summary>
        public static IReadOnlyList<string> clashes()
        {
            var found = new List<string>();

            var ours = installed();
            var wanted = "Decor/Prefabs/Tent/BP_Tent";

            var folders = new List<string>();
            if (CustomSkins.paksFolder != null) { folders.Add(CustomSkins.paksFolder); }
            if (CustomSkins.modsFolder != null) { folders.Add(CustomSkins.modsFolder); }

            foreach (var pak in folders
                .Where(Directory.Exists)
                .SelectMany(folder => Directory.GetFiles(folder, "*.pak")))
            {
                //Ours is meant to replace it, so it is not a clash with itself.
                if (ours != null && string.Equals(pak, ours, StringComparison.OrdinalIgnoreCase)) { continue; }

                try
                {
                    var reader = new PakFileReader(pak);

                    //The constructor reads the footer and stops; the index is a separate ask.
                    //Leaving it out is what made the first version of this find nothing at all,
                    //quietly, with the clashing pak sitting right there in the folder - so the
                    //result is checked rather than assumed.
                    reader.ReadIndex(null);
                    if (!reader.Initialized) { continue; }

                    //Iterated rather than asked by key: the reader spells its entries relative to
                    //its own mount point, which is not something to guess at from out here.
                    foreach (var entry in reader)
                    {
                        if (entry.Key.Replace('\\', '/')
                            .IndexOf(wanted, StringComparison.OrdinalIgnoreCase) < 0)
                        {
                            continue;
                        }

                        found.Add(System.IO.Path.GetFileName(pak));
                        break;
                    }
                }
                catch (Exception)
                {
                    //A pak this cannot open is not a pak this can judge. Saying nothing about it
                    //is better than accusing it.
                }
            }

            return found;
        }

        /// <summary>
        /// Installs the loader this app carries inside itself.
        ///
        /// Which is the whole point of carrying it. Both halves are blueprints, blueprints are
        /// compiled bytecode, and there is no honest way to write one from out here - so they
        /// were built once in Unreal 4.22 and the twenty kilobytes of cooked result live in the
        /// exe. Everyone after that gets a loader by pressing a button.
        ///
        /// Rebuilt with Tools/loader/build_loader.py when it needs changing, cooked, and the four
        /// files copied back over Logic/LoaderAssets.
        /// </summary>
        public static CustomSkins.InstalledMod installBuiltIn()
        {
            var entries = new List<PakWriter.Entry>();

            foreach (var (enginePath, file) in new[]
            {
                (ANCHOR, "BP_Tent"),
                (WIDGET, "UMG_MCDRebornLoader"),
            })
            {
                var inside = "Dungeons/Content/" + enginePath.Substring("/Game/".Length);

                foreach (var extension in new[] { ".uasset", ".uexp" })
                {
                    var bytes = carried(file + extension);

                    if (string.Equals(extension, ".uexp", StringComparison.Ordinal))
                    {
                        hush(bytes);
                    }

                    entries.Add(new PakWriter.Entry(inside + extension, bytes));
                }
            }

            HeldBack = new List<string>();
            return CustomSkins.writeModPak(PREFIX, entries);
        }

        /// <summary>
        /// The loader's debug readout, made invisible on the way into the pak.
        ///
        /// The widget prints the current game mode in the corner of the screen - the thing that
        /// proved the loader runs at all, and that has no business being on a stranger's title
        /// screen. It is still built and still updated; it is simply drawn at zero alpha, which
        /// keeps the diagnostic for anybody reading the asset and takes it off the screen for
        /// everybody else.
        ///
        /// FADED rather than hidden, and hidden is what you would reach for first. A widget
        /// authored Visible has NO Visibility property in the cooked asset at all - Unreal writes
        /// down only what differs from the class default - so there is nothing to flip, which is
        /// the same wall the map table's slots hit. The colour, by contrast, was set explicitly
        /// by the generator, so it IS written down, and an FLinearColor is four floats of fixed
        /// width. Sixteen bytes for sixteen, and no offset in the package moves.
        ///
        /// BOTH copies are changed, because a cooked widget serialises its tree more than once -
        /// under the generated class and again under the archetype - and writing one leaves the
        /// two disagreeing, with the winner decided by whichever loads second.
        ///
        /// Refuses rather than guesses when it does not find exactly the two it expects. The
        /// colour is matched by VALUE, which is only safe while that value belongs to this one
        /// label; a loader that grew a second gold thing would make this ambiguous, and a silent
        /// partial edit is worse than a visible debug line.
        /// </summary>
        private static void hush(byte[] data)
        {
            //The gold the generator gives the status line: (1.0, 0.85, 0.3, 1.0).
            var gold = new List<byte>();

            foreach (var one in new[] { 1.0f, 0.85f, 0.3f, 1.0f })
            {
                gold.AddRange(BitConverter.GetBytes(one));
            }

            var found = new List<int>();

            for (var at = 0; at + gold.Count <= data.Length; at++)
            {
                var same = true;

                for (var i = 0; i < gold.Count && same; i++)
                {
                    same = data[at + i] == gold[i];
                }

                if (same) { found.Add(at); }
            }

            if (found.Count != 2)
            {
                Console.WriteLine("[loader] expected two copies of the status colour, found "
                    + $"{found.Count} - leaving the debug line on screen rather than writing "
                    + "into something else");
                return;
            }

            //Only the alpha. Leaving the colour alone means the asset still records what it was
            //meant to look like, and one float is the smallest change that does the job.
            foreach (var at in found)
            {
                BitConverter.GetBytes(0f).CopyTo(data, at + 12);
            }
        }

        /// <summary>One of the files built into this exe.</summary>
        private static byte[] carried(string name)
        {
            var assembly = typeof(Loader).Assembly;

            //Matched by its ending rather than by a full resource name. The prefix depends on the
            //assembly and the folder, and a rename of either would otherwise turn the loader into
            //a file-not-found at the one moment somebody is trying to install it.
            var resource = assembly.GetManifestResourceNames()
                .FirstOrDefault(one => one.EndsWith("." + name, StringComparison.OrdinalIgnoreCase))
                ?? throw new InvalidOperationException(
                    $"This build does not carry {name}, so the loader cannot be installed from it.");

            using var stream = assembly.GetManifestResourceStream(resource)
                ?? throw new InvalidOperationException($"{name} could not be read out of this build.");

            using var memory = new MemoryStream();
            stream.CopyTo(memory);
            return memory.ToArray();
        }

        /// <summary>
        /// Installs the loader from a folder cooked in Unreal.
        ///
        /// Checked rather than trusted: a folder that does not hold both halves is refused, and
        /// each half is recognised by the path it records for itself rather than by its file name.
        /// A loader installed with the anchor missing is a loader that never starts, and a loader
        /// installed with the widget missing is an anchor that starts nothing - neither of which
        /// says anything about itself when the game is running.
        /// </summary>
        public static CustomSkins.InstalledMod install(string folder)
        {
            if (!Directory.Exists(folder))
            {
                throw new InvalidOperationException("That folder is not there.");
            }

            var assets = cookedIn(folder);

            var anchor = assets.FirstOrDefault(one =>
                one.self.Equals(ANCHOR, StringComparison.OrdinalIgnoreCase));
            var widget = assets.FirstOrDefault(one =>
                one.self.Equals(WIDGET, StringComparison.OrdinalIgnoreCase));

            if (anchor.self == null)
            {
                throw new InvalidOperationException(
                    "The anchor is not in that folder. It has to be cooked at exactly " + ANCHOR
                    + " - that is the game's own actor, and the path is how the game finds this "
                    + "one instead.");
            }

            if (widget.self == null)
            {
                throw new InvalidOperationException(
                    "The widget is not in that folder. It has to be cooked at exactly " + WIDGET + ".");
            }

            var entries = new List<PakWriter.Entry>();
            var held = new List<string>();

            foreach (var asset in assets)
            {
                //Everything the game already has is left behind, and the anchor is the single
                //exception because replacing it is the entire point.
                //
                //This is a guard rather than a tidy-up. Referring to one of the game's classes in
                //the editor means making an empty asset of the same name at the same path to
                //point at - that is how every content mod does it - and the loader's widget has
                //to refer to three of them, the game modes it tells apart. Shipping those stubs
                //would put an empty BP_LobbyGameMode over the real one, and the game would come
                //up with no game mode at all. So the rule is the one the Mod Kit writes as a
                //robocopy filter, applied here instead: build against them, never ship them.
                if (!asset.self.Equals(ANCHOR, StringComparison.OrdinalIgnoreCase)
                    && GameAssets.has(asset.self))
                {
                    held.Add(asset.self);
                    continue;
                }

                var inside = "Dungeons/Content/" + asset.self.Substring("/Game/".Length);
                foreach (var file in asset.files)
                {
                    entries.Add(new PakWriter.Entry(inside + file.Key, file.Value));
                }
            }

            HeldBack = held;
            return CustomSkins.writeModPak(PREFIX, entries);
        }

        /// <summary>
        /// The stubs the last install refused to ship, and so left the game's own versions alone.
        ///
        /// Worth reporting rather than hiding: somebody who cooked a whole project into one folder
        /// should be told that most of it was not installed, and somebody who expected their stubs
        /// to be stubs gets that confirmed instead of hoped for.
        /// </summary>
        public static IReadOnlyList<string> HeldBack { get; private set; } = new List<string>();

        public static void remove()
        {
            var pak = installed();
            if (pak != null && File.Exists(pak)) { File.Delete(pak); }
        }

        /// <summary>
        /// Every cooked asset under a folder, each asked what it is called.
        ///
        /// The same reading Payloads does, kept separate because what is being looked for is
        /// different: there, anything cooked is carried; here, two particular paths have to be
        /// present and everything else is whatever they are built from.
        /// </summary>
        private static List<(string self, Dictionary<string, byte[]> files)> cookedIn(string folder)
        {
            var found = new List<(string, Dictionary<string, byte[]>)>();

            var byStem = Directory
                .EnumerateFiles(folder, "*", SearchOption.AllDirectories)
                .Where(file => COOKED.Contains(
                    System.IO.Path.GetExtension(file).ToLowerInvariant(), StringComparer.Ordinal))
                .GroupBy(file => System.IO.Path.Combine(
                    System.IO.Path.GetDirectoryName(file) ?? ".",
                    System.IO.Path.GetFileNameWithoutExtension(file)), StringComparer.OrdinalIgnoreCase);

            foreach (var together in byStem)
            {
                var parts = new Dictionary<string, byte[]>(StringComparer.Ordinal);
                foreach (var file in together)
                {
                    parts[System.IO.Path.GetExtension(file).ToLowerInvariant()] = File.ReadAllBytes(file);
                }

                var headerAs = parts.ContainsKey(".umap") ? ".umap"
                    : parts.ContainsKey(".uasset") ? ".uasset"
                    : null;
                if (headerAs == null || !parts.ContainsKey(".uexp")) { continue; }

                var self = AssetCopy.selfNameIn(parts[headerAs], System.IO.Path.GetFileName(together.Key));
                if (self == null) { continue; }

                found.Add((self, parts));
            }

            return found;
        }

        private static readonly string[] COOKED = { ".uasset", ".umap", ".uexp", ".ubulk", ".ufont" };
    }
}
