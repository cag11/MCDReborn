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
    /// Things the game runs, installed into the folders a loader watches.
    ///
    /// Everything else this app writes is a replacement: this mesh instead of that one, these
    /// numbers instead of those. None of it can make the game *do* anything new, because doing
    /// needs calling and this project does not inject - a line written in the mount, the camera
    /// and the creature import alike.
    ///
    /// A loader is the way round it that the community found. A small mod replaces an actor the
    /// game already spawns, that actor starts a widget, and the widget loads any level it finds in
    /// three folders - one for the menu, one for the Camp, one for a mission. Whatever is in those
    /// levels runs. The loader supplies the hook; the levels are what somebody wants to happen.
    ///
    /// This installs those levels. It does not author them: a level with a blueprint in it is made
    /// in an editor, and no amount of writing bytes from out here is a substitute for that. What
    /// this does is take one somebody has cooked, put it where the loader will find it, and let it
    /// be listed and removed again like every other mod here.
    ///
    /// Its own folders rather than the existing loader's, so that both can be installed at once.
    /// Two mods claiming one path is a fight the alphabet settles, silently, and the loser is
    /// whichever sorts first - which would mean shipping this broke every mod built on the other.
    /// </summary>
    public static class Payloads
    {
        /// <summary>When the game runs what is in a folder.</summary>
        public static readonly string[] TRIGGERS = { "Menu", "Lobby", "Ingame" };

        /// <summary>Where this app's own loader looks, as the paks spell it.</summary>
        public const string ROOT = "/Dungeons/Content/MCDReborn/";

        /// <summary>
        /// Where the community's loader looks.
        ///
        /// Recognised, never written to. This used to be an offer - a checkbox, because this app
        /// had no loader of its own and a payload had to be proved against somebody else's. Now
        /// that there is one, the checkbox was doing harm: unticked, it wrote a valid pak into
        /// folders nothing was watching, and the payload silently never ran.
        ///
        /// It stays here because a tree imported from elsewhere has its levels cooked into these
        /// folders, and those have to be left where they are rather than dragged into ours.
        /// Reading somebody else's layout and writing our own are different things.
        /// </summary>
        public const string OTHER_ROOT = "/Dungeons/Content/BPLoader/";

        //Marked so that they can be found again. A payload is an ordinary mod pak otherwise, and
        //without a mark the only way to tell one from a recoloured axe is to open it.
        //
        //Two halves, because the writer adds its own prefix to whatever it is given: this is the
        //part handed over, and FOUND_AS is what the file ends up called. Written as one string
        //first, which produced `MCDReborn_MCDReborn_Payload_...` and a listing that found nothing
        //- so nothing could be removed either.
        private const string PREFIX = "Payload_";
        private const string FOUND_AS = "MCDReborn_Payload_";

        /// <summary>One payload, as it sits in the mods folder.</summary>
        public sealed class Payload
        {
            public Payload(string path, string trigger, string name)
            {
                Path = path;
                Trigger = trigger;
                Name = name;
            }

            public string Path { get; }
            public string Trigger { get; }
            public string Name { get; }

            public override string ToString() => $"{Trigger}: {Name}";
        }

        public static string folderFor(string trigger) => ROOT + trigger.Trim('/') + "/";

        /// <summary>
        /// Every payload installed, read from the file names rather than the paks.
        ///
        /// The name carries the trigger because opening each pak to ask would mean reading every
        /// mod in the folder to list three of them.
        /// </summary>
        public static IReadOnlyList<Payload> installed()
        {
            var found = new List<Payload>();

            //Both places. What this app writes goes straight into the paks folder, and what
            //somebody imports from elsewhere goes into ~mods beside it - and a payload is just as
            //valid in either. Looking in only the second one is what made the first version list
            //nothing while a pak it had written sat there.
            var folders = new List<string>();
            if (CustomSkins.paksFolder != null) { folders.Add(CustomSkins.paksFolder); }
            if (CustomSkins.modsFolder != null) { folders.Add(CustomSkins.modsFolder); }

            foreach (var file in folders
                .Where(Directory.Exists)
                .SelectMany(folder => Directory.GetFiles(folder, FOUND_AS + "*.pak")))
            {
                var stem = System.IO.Path.GetFileNameWithoutExtension(file).Substring(FOUND_AS.Length);
                if (stem.EndsWith("_P", StringComparison.Ordinal)) { stem = stem.Substring(0, stem.Length - 2); }

                var split = stem.IndexOf('_');
                if (split <= 0) { found.Add(new Payload(file, "?", stem)); continue; }

                found.Add(new Payload(file, stem.Substring(0, split), stem.Substring(split + 1)));
            }

            return found;
        }

        /// <summary>The file extensions a cooked asset is made of.</summary>
        private static readonly string[] COOKED = { ".uasset", ".umap", ".uexp", ".ubulk", ".ufont" };

        /// <summary>
        /// One cooked asset: where it believes it lives, and the files it is made of.
        ///
        /// Where it believes it lives is read out of the asset rather than taken from the folder
        /// it was found in. A cooked package records its own path in its name table, and that
        /// recording is what the engine matches against - so a tree that has been copied, zipped
        /// or half flattened on the way here still installs to the right places, and a file that
        /// was quietly moved is caught rather than installed somewhere it will never load from.
        /// </summary>
        private sealed class Cooked
        {
            public Cooked(string selfPath, string headerAs)
            {
                SelfPath = selfPath;
                HeaderAs = headerAs;
            }

            /// <summary>Its own path, as the engine spells it: "/Game/MyMod/BP_Thing".</summary>
            public string SelfPath { get; set; }

            /// <summary>".umap" for a level, ".uasset" for everything else.</summary>
            public string HeaderAs { get; }

            public bool IsLevel => HeaderAs == ".umap";

            /// <summary>Extension to contents, the header included.</summary>
            public Dictionary<string, byte[]> Files { get; } = new Dictionary<string, byte[]>(StringComparer.Ordinal);
        }

        /// <summary>
        /// Puts one cooked asset into a loader's folder.
        ///
        /// Kept for the case it was written for - somebody with a single level and nothing else -
        /// and a thin wrapper now, because one file is a folder with one asset in it.
        /// </summary>
        public static CustomSkins.InstalledMod install(string trigger, string uassetPath,
            string wantedName)
        {
            var stem = System.IO.Path.Combine(
                System.IO.Path.GetDirectoryName(uassetPath) ?? ".",
                System.IO.Path.GetFileNameWithoutExtension(uassetPath));

            var together = COOKED.Select(extension => stem + extension).Where(File.Exists).ToList();
            if (together.Count == 0)
            {
                throw new InvalidOperationException("That does not look like a cooked asset - no .uasset was found.");
            }

            return installAll(trigger, together, wantedName);
        }

        /// <summary>
        /// Takes one of the game's own levels and installs it as a payload.
        ///
        /// Which is the thing the loader was actually for. Everything else here moves something
        /// somebody made in an editor; this makes a payload out of what is already in the game,
        /// and the game has four thousand one hundred and fifty six levels in it.
        ///
        /// It works because a payload is only a level in a folder, and a cooked level says where
        /// it lives in its own name table. Rewrite that one string and the same bytes are a level
        /// at a different address - so the Camp's loot room, a boss arena, a stretch of the Nether
        /// can each be loaded somewhere they were never meant to be. Nothing is authored and
        /// nothing is copied out of the game: the pak holds one renamed level, and every model,
        /// material and sound in it is still resolved from the game's own files at run time.
        ///
        /// The name is chosen to fit rather than to read nicely, for the reason AssetCopy gives:
        /// the rename has to be the same length or every offset after it moves.
        /// </summary>
        public static CustomSkins.InstalledMod installGameLevel(string trigger, string enginePath,
            string wantedName)
        {
            if (!TRIGGERS.Contains(trigger, StringComparer.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException($"{trigger} is not one of {string.Join(", ", TRIGGERS)}.");
            }

            var paks = CustomSkins.index
                ?? throw new InvalidOperationException("The game's files have not been found.");

            var inPak = "/Dungeons/Content/" + enginePath.Substring("/Game/".Length);

            PakPackage package;
            try
            {
                var read = paks.extractPackage(inPak);
                if (read == null) { throw new InvalidOperationException($"{enginePath} could not be read."); }
                package = read.Value;
            }
            catch (InvalidOperationException) { throw; }
            catch (Exception problem)
            {
                throw new InvalidOperationException($"{enginePath} could not be read: {problem.Message}");
            }

            var header = package.UAsset.ToArray();
            var exports = package.UExp.ToArray();

            var was = AssetCopy.selfNameIn(header, enginePath.Substring(enginePath.LastIndexOf('/') + 1))
                ?? throw new InvalidOperationException(
                    $"{enginePath} does not name itself, so it cannot be moved to another path.");

            var now = AssetCopy.nameFor(was.Length, folderFor(trigger), tidy(wantedName))
                ?? throw new InvalidOperationException(
                    "That level's path is too short to fit under the loader's folder.");

            if (!AssetCopy.rename(header, was, now))
            {
                throw new InvalidOperationException("The level's name could not be rewritten.");
            }

            //The exports as well, where it may or may not appear. Nothing is wrong if it does not.
            AssetCopy.rename(exports, was, now);

            var inside = "Dungeons/Content/" + now.Substring("/Game/".Length);
            var entries = new List<PakWriter.Entry>
            {
                new PakWriter.Entry(inside + ".umap", header),
                new PakWriter.Entry(inside + ".uexp", exports),
            };

            if (package.UBulk != null)
            {
                entries.Add(new PakWriter.Entry(inside + ".ubulk", package.UBulk.Value.ToArray()));
            }

            Skipped = new List<string>();
            return CustomSkins.writeModPak(PREFIX + trigger + "_" + tidy(wantedName), entries);
        }

        /// <summary>
        /// Puts everything in a folder into the loader's folders, in one pak.
        ///
        /// Which is what a payload past the first one actually is. A level is a manifest: it holds
        /// placements and the paths of the blueprints placed, and those blueprints are a separate
        /// tree somewhere else entirely - one mod read while building this ships a three kilobyte
        /// level naming thirty four classes that live in a hundred and forty one files beside it.
        /// Installing the level by itself produces exactly the failure that looks like the loader
        /// being broken: the map loads, every actor in it fails to resolve, and nothing appears.
        ///
        /// So the unit is the cooked tree rather than the file. Everything under the folder goes
        /// in, each asset to the path it records for itself, and the level is moved into the
        /// chosen trigger folder if it is not already sitting in one.
        /// </summary>
        public static CustomSkins.InstalledMod installFolder(string trigger, string folder,
            string wantedName)
        {
            return installAll(trigger, cookedIn(folder), wantedName, extrasIn(folder));
        }

        /// <summary>
        /// The files in a tree that are not cooked assets, carried at where they sit.
        ///
        /// A payload is not all packages. One mod read while building this ships a thousand and
        /// twenty nine loose files - a Minecraft resource pack, which is how its level was built
        /// in the first place - beside a comma separated list of labels and a translation file.
        /// Carrying only the packages would have taken seventy one files out of eleven hundred
        /// and produced a mod missing everything it is made of.
        ///
        /// These have no name table to ask, so position is all there is: whatever sits under a
        /// folder called Content goes to the same place under the game's. Anything above that
        /// has no home to go to and is left, which is said rather than done quietly.
        /// </summary>
        private static List<PakWriter.Entry> extrasIn(string folder)
        {
            var found = new List<PakWriter.Entry>();

            foreach (var file in Directory.EnumerateFiles(folder, "*", SearchOption.AllDirectories))
            {
                var extension = System.IO.Path.GetExtension(file).ToLowerInvariant();
                if (COOKED.Contains(extension, StringComparer.Ordinal)) { continue; }

                var under = contentRelative(file);
                if (under == null) { continue; }

                found.Add(new PakWriter.Entry("Dungeons/Content/" + under, File.ReadAllBytes(file)));
            }

            return found;
        }

        /// <summary>Where a loose file sits below the nearest folder called Content.</summary>
        private static string? contentRelative(string file)
        {
            const string content = "/content/";

            var path = file.Replace('\\', '/');
            var at = path.ToLowerInvariant().LastIndexOf(content, StringComparison.Ordinal);
            if (at < 0) { return null; }

            return path.Substring(at + content.Length);
        }

        /// <summary>
        /// What installing that folder would put in the pak, without putting it anywhere.
        ///
        /// Worth having separately because the interesting part of this is a decision - which
        /// level runs, and what every asset believes it is called - and a decision is much easier
        /// to check when checking it does not mean writing a pak into the game folder and taking
        /// it out again afterwards.
        /// </summary>
        public static IReadOnlyList<string> preview(string trigger, string folder,
            string wantedName)
        {
            var left = new List<string>();
            var assets = gather(cookedIn(folder), left);
            place(assets, trigger, tidy(wantedName));
            Skipped = left;

            return assets
                .SelectMany(asset => asset.Files.Keys.Select(extension =>
                    "Dungeons/Content/" + asset.SelfPath.Substring("/Game/".Length) + extension))
                .Concat(extrasIn(folder).Select(entry => entry.Path))
                .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        /// <summary>Every cooked file under a folder, or a complaint about there being none.</summary>
        private static IReadOnlyList<string> cookedIn(string folder)
        {
            if (!Directory.Exists(folder))
            {
                throw new InvalidOperationException("That folder is not there.");
            }

            var files = Directory
                .EnumerateFiles(folder, "*", SearchOption.AllDirectories)
                .Where(file => COOKED.Contains(
                    System.IO.Path.GetExtension(file).ToLowerInvariant(), StringComparer.Ordinal))
                .ToList();

            if (files.Count == 0)
            {
                throw new InvalidOperationException(
                    "Nothing cooked was found in that folder. Point this at the Content folder "
                    + "under Saved/Cooked/WindowsNoEditor, or at a folder inside it.");
            }

            return files;
        }

        /// <summary>
        /// The whole of installing, however many files were handed over.
        ///
        /// One path through it rather than two, because the interesting parts - reading each
        /// asset's own path, deciding which level runs, moving it there - are the same whether
        /// there is one asset or a hundred and forty one.
        /// </summary>
        private static CustomSkins.InstalledMod installAll(string trigger, IReadOnlyList<string> files,
            string wantedName, List<PakWriter.Entry>? extras = null)
        {
            if (!TRIGGERS.Contains(trigger, StringComparer.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException($"{trigger} is not one of {string.Join(", ", TRIGGERS)}.");
            }

            var left = new List<string>();
            var assets = gather(files, left);
            if (assets.Count == 0)
            {
                throw new InvalidOperationException(
                    "That does not look like cooked output - nothing was found that names itself "
                    + "and has its .uexp beside it.");
            }

            place(assets, trigger, tidy(wantedName));

            var entries = new List<PakWriter.Entry>(extras ?? new List<PakWriter.Entry>());
            foreach (var asset in assets)
            {
                //"/Game/X/Y" is how the engine spells a path and "Dungeons/Content/X/Y" is how a
                //pak does. The two differ only in that prefix.
                var inside = "Dungeons/Content/" + asset.SelfPath.Substring("/Game/".Length);
                foreach (var file in asset.Files)
                {
                    entries.Add(new PakWriter.Entry(inside + file.Key, file.Value));
                }
            }

            var mod = CustomSkins.writeModPak(PREFIX + trigger + "_" + tidy(wantedName), entries);
            Skipped = left;
            return mod;
        }

        /// <summary>
        /// What the last install left out, and why.
        ///
        /// Said rather than swallowed. One mod read while building this has two assets cooked at a
        /// path they no longer sit at, and an installer that drops them quietly produces a mod
        /// with two invisible holes in it - which is the kind of thing that gets blamed on the
        /// loader a week later.
        /// </summary>
        public static IReadOnlyList<string> Skipped { get; private set; } = new List<string>();

        /// <summary>
        /// The cooked assets among a pile of files, each asked what it is called.
        ///
        /// Anything that cannot answer is left out rather than guessed at. A package that does not
        /// name itself cannot be placed, and placing it by its file name instead would be
        /// inventing the one fact that has to be right.
        /// </summary>
        private static List<Cooked> gather(IReadOnlyList<string> files, List<string>? skipped = null)
        {
            //Grouped by the file without its extension, which is what ties a .uasset to its .uexp.
            var byStem = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
            foreach (var file in files)
            {
                var stem = System.IO.Path.Combine(
                    System.IO.Path.GetDirectoryName(file) ?? ".",
                    System.IO.Path.GetFileNameWithoutExtension(file));

                if (!byStem.TryGetValue(stem, out var together))
                {
                    byStem[stem] = together = new List<string>();
                }
                together.Add(file);
            }

            var found = new List<Cooked>();
            foreach (var pair in byStem)
            {
                var parts = new Dictionary<string, byte[]>(StringComparer.Ordinal);
                foreach (var file in pair.Value)
                {
                    parts[System.IO.Path.GetExtension(file).ToLowerInvariant()] = File.ReadAllBytes(file);
                }

                //A cooked asset is a header and its exports. One without the other is half a file,
                //and half a file is a game that will not start rather than a mod that does nothing.
                var headerAs = parts.ContainsKey(".umap") ? ".umap"
                    : parts.ContainsKey(".uasset") ? ".uasset"
                    : null;
                if (headerAs == null || !parts.ContainsKey(".uexp"))
                {
                    //Only worth mentioning when there was a header: a lone .uexp beside one that
                    //was counted is not a missing asset, it is the same asset seen twice.
                    if (headerAs != null) { skipped?.Add(System.IO.Path.GetFileName(pair.Key) + " (no .uexp)"); }
                    continue;
                }

                var self = AssetCopy.selfNameIn(parts[headerAs], System.IO.Path.GetFileName(pair.Key));
                if (self == null)
                {
                    //Cooked at one path and sitting at another, which happens when a folder gets
                    //renamed after cooking. Where it belongs cannot be known from here - its own
                    //answer and its position disagree, and guessing either way installs it
                    //somewhere it will not load from or on top of something else.
                    skipped?.Add(System.IO.Path.GetFileName(pair.Key) + " (does not say what it is called)");
                    continue;
                }

                var asset = new Cooked(self, headerAs);
                foreach (var part in parts) { asset.Files[part.Key] = part.Value; }
                found.Add(asset);
            }

            return found;
        }

        /// <summary>
        /// Decides which level the loader will run, and moves it there if it is not there already.
        ///
        /// A tree can arrive with its levels already cooked into the loader's folders, which is
        /// how somebody who has done this before will have set it up - and a mod using all three
        /// moments at once can only say so that way, because one dropdown cannot say it. Those are
        /// left exactly as they are.
        ///
        /// What gets moved is a level cooked somewhere ordinary, by somebody who put it in a
        /// folder of their own and chose the moment from the menu here instead. One of those is a
        /// choice; several is a question this cannot answer, so it says so rather than picking.
        /// </summary>
        private static void place(List<Cooked> assets, string trigger, string wantedName)
        {
            var levels = assets.Where(asset => asset.IsLevel).ToList();
            if (levels.Count == 0)
            {
                throw new InvalidOperationException(
                    "There is no level in that folder. A loader runs levels, so a payload needs "
                    + "one - the blueprints go in it rather than beside it. A folder of assets "
                    + "with no level is a set of replacements rather than a payload, and nothing "
                    + "in it would ever be run.");
            }

            //A level already in one of those folders settles it, and settles it for the whole
            //tree: that is the payload, and every other level here is something it uses. A mod
            //read while building this adds three sublevels to the game's own Lower Temple and
            //loads its Camp payload from the loader's folder - all four are levels, only one of
            //them is the one that runs, and it is the one somebody already put in the folder.
            if (levels.Any(level => inATriggerFolder(level.SelfPath))) { return; }

            //Nothing placed, so one has to be chosen. Levels the game already has are out of the
            //running: those are replacements of its own maps, which the loader never looks at.
            var loose = levels.Where(level => !GameAssets.has(level.SelfPath)).ToList();

            if (loose.Count == 0)
            {
                throw new InvalidOperationException(
                    "Every level in that folder replaces one the game already has, so there is "
                    + "nothing here for a loader to run. A payload needs a level of its own.");
            }

            if (loose.Count > 1)
            {
                throw new InvalidOperationException(
                    "More than one level here is new and none is in a loader folder ("
                    + string.Join(", ", loose.Select(level => level.SelfPath))
                    + "), so which one should run cannot be worked out. Cook the one that runs "
                    + "into " + ROOT + "Menu, Lobby or Ingame and install again.");
            }

            var level = loose[0];
            var was = level.SelfPath;
            var now = AssetCopy.nameFor(was.Length, folderFor(trigger), wantedName)
                ?? throw new InvalidOperationException(
                    "That level's own path is too short to fit under the loader's folder. "
                    + "Cook it at a longer path, or straight into the folder it belongs in.");

            //Every file of it, not just the header: the path can appear in the exports too, and
            //one left behind is a reference to a level that is no longer at that address.
            if (!AssetCopy.rename(level.Files[level.HeaderAs], was, now))
            {
                throw new InvalidOperationException("The level's name could not be rewritten.");
            }
            foreach (var file in level.Files)
            {
                if (file.Key == level.HeaderAs) { continue; }
                AssetCopy.rename(file.Value, was, now);
            }

            level.SelfPath = now;
        }

        /// <summary>Whether a level is already sitting where some loader will find it.</summary>
        private static bool inATriggerFolder(string selfPath)
        {
            foreach (var root in new[] { ROOT, OTHER_ROOT })
            {
                var asEngine = "/Game/" + root.Substring("/Dungeons/Content/".Length);
                foreach (var trigger in TRIGGERS)
                {
                    if (selfPath.StartsWith(asEngine + trigger + "/", StringComparison.OrdinalIgnoreCase))
                    {
                        return true;
                    }
                }
            }

            return false;
        }

        /// <summary>A name with nothing in it that a file name or an asset path would object to.</summary>
        private static string tidy(string name)
        {
            var kept = new System.Text.StringBuilder();
            foreach (var letter in name)
            {
                if (char.IsLetterOrDigit(letter)) { kept.Append(letter); }
            }

            return kept.Length > 0 ? kept.ToString() : "payload";
        }

        public static void remove(Payload payload)
        {
            if (File.Exists(payload.Path)) { File.Delete(payload.Path); }
        }
    }
}
