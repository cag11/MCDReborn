using MCDSaveEdit.Services;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
#nullable enable

namespace MCDSaveEdit.Logic
{
    /// <summary>
    /// Installing a map as its own mission, rather than over one of the game's.
    ///
    /// <see cref="MapMod"/> installs a map by taking a mission's place: the level is written as
    /// `creeperwoods.json` and the real Creeper Woods is gone until the pak is removed. That works
    /// and it costs a mission.
    ///
    /// This does not. The level is written under a SLOT name - `mcdcustom07.json` - which the game
    /// has never heard of and which nothing of its own reads. The Camp's map table offers a
    /// hundred of these, and each is simply a file this app decides the contents of. Nothing the
    /// game ships is replaced, and removing the pak leaves no trace.
    ///
    /// Two things are still borrowed, because neither can be created from a pak:
    ///
    /// **A label table.** Objective wording resolves through a string table that the game
    /// REGISTERS IN C++ - one `LOCTABLE_FROMFILE_GAME` line per mission - so a name the game does
    /// not already know resolves to nothing. A custom map therefore has to name one of the
    /// registered tables and add its rows to that file. <see cref="BORROWED"/> is the one chosen
    /// and the note there says why.
    ///
    /// **A mission identity.** The panel launches every slot as `randommission`, so the game
    /// treats it as a mission with no entry of its own: the objective banner works, the map
    /// screens show the game's own `&lt;Unknown&gt;` placeholder, and nothing is written against a real
    /// mission's progress. Borrowing a REAL mission's identity would look tidier and would mark
    /// that mission complete in the save when the custom map is finished, which feeds difficulty
    /// and threat unlocks. An inert key is the better failure.
    /// </summary>
    public static class MapSlots
    {
        /// <summary>
        /// The label table every slot borrows.
        ///
        /// `slimysewers` was tried first and is the better idea on paper: it is the one table
        /// that ships a CSV and which no level references, so extending it could cost no mission
        /// its wording. In game every objective read `&lt;MISSING STRING TABLE ENTRY&gt;` - the level
        /// named the table, the file was in the pak, byte-perfect, and the game still had nothing
        /// to look up. "No level references it" evidently also means "the game never registers
        /// it", and a table the game does not register is a table nothing can be added to.
        ///
        /// So it borrows the one that is known to work instead. Two independent mods depend on
        /// it: Blossoming Isles ships its own wording this way, and LukeFZ's merger states the
        /// rule outright - set `loctable-id` to `creeperwoods` for custom strings to load.
        ///
        /// It costs Creeper Woods nothing, which is the part that makes this acceptable rather
        /// than merely necessary: the game's own rows are kept BYTE FOR BYTE and new ones are
        /// appended after them, so the mission keeps every line it had. See
        /// <see cref="MapWords.tableWith"/> for why rebuilding one of these crashes on entering
        /// a mission rather than on loading the table.
        /// </summary>
        public const string BORROWED = "creeperwoods";

        private const string PREFIX = "Slot";
        private const string FOUND_AS = "MCDReborn_Slot";

        /// <summary>What is in a slot, read back from the mods folder.</summary>
        public sealed class Filled
        {
            public Filled(int slot, string name, string path)
            {
                Slot = slot;
                Name = name;
                Path = path;
            }

            public int Slot { get; }

            /// <summary>What the map was called when it was installed.</summary>
            public string Name { get; }

            public string Path { get; }

            public override string ToString() => $"{Slot:00}: {Name}";
        }

        /// <summary>
        /// Every slot that has something in it.
        ///
        /// Read from the pak file names rather than by opening them, the same way payloads are
        /// listed - opening each would mean reading every mod in the folder to answer a question
        /// the name already answers.
        /// </summary>
        public static IReadOnlyList<Filled> installed()
        {
            var found = new List<Filled>();

            foreach (var folder in new[] { CustomSkins.paksFolder, CustomSkins.modsFolder })
            {
                if (folder == null || !Directory.Exists(folder)) { continue; }

                foreach (var file in Directory.EnumerateFiles(folder, FOUND_AS + "*.pak"))
                {
                    var stem = Path.GetFileNameWithoutExtension(file);
                    var rest = stem.Substring(FOUND_AS.Length).TrimEnd();

                    //`MCDReborn_Slot07_MyMap_P` - the number, then the name it was given.
                    if (rest.EndsWith("_P", StringComparison.Ordinal))
                    {
                        rest = rest.Substring(0, rest.Length - 2);
                    }

                    var cut = rest.IndexOf('_');
                    var digits = cut < 0 ? rest : rest.Substring(0, cut);

                    if (!int.TryParse(digits, out var slot)) { continue; }

                    found.Add(new Filled(slot,
                        cut < 0 ? string.Empty : rest.Substring(cut + 1), file));
                }
            }

            return found.OrderBy(one => one.Slot).ToList();
        }

        /// <summary>
        /// The hundred slots, as missions the Maps tab can select.
        ///
        /// Empty ones are listed too, and that is the point rather than an oversight: a slot is
        /// where a map GOES, so it has to be selectable before it has anything in it. Choosing an
        /// empty one and pressing Import map is how a map gets there at all.
        ///
        /// The size is the working folder's rather than the pak's. What somebody is about to edit
        /// is the folder; the pak is what was built from it last time.
        /// </summary>
        public static List<GameMaps.Mission> missions()
        {
            var filled = installed().ToDictionary(one => one.Slot, one => one.Name);
            var made = new List<GameMaps.Mission>();

            for (var slot = 1; slot <= MapTable.SLOTS; slot++)
            {
                var name = MapTable.slotName(slot);
                var folder = MapWorkshop.folderFor(name);

                var bytes = 0L;
                if (Directory.Exists(folder))
                {
                    foreach (var file in Directory.EnumerateFiles(folder, "*",
                        SearchOption.AllDirectories))
                    {
                        bytes += new FileInfo(file).Length;
                    }
                }

                made.Add(new GameMaps.Mission(name,
                    "/Dungeons/Content/data/lovika/levels/" + name,
                    bytes, slot, filled.TryGetValue(slot, out var said) ? said : null));
            }

            return made;
        }

        /// <summary>
        /// A slot's level and everything with it, written out to a folder to work on.
        ///
        /// The counterpart of <see cref="MapMod.export"/>, and it reads somewhere else entirely.
        /// One of the game's missions is read from the game's own paks; a slot is not in them -
        /// mod paks are not indexed - so its contents come back out of the pak this app wrote.
        ///
        /// Which means a slot can be recovered on a machine that has the pak and not the folder,
        /// rather than being a map that exists and cannot be opened.
        /// </summary>
        public static int export(int slot, string folder)
        {
            var found = inSlot(slot)
                ?? throw new InvalidOperationException(
                    $"Slot {slot:00} is empty, so there is nothing to work on. Import a map into "
                    + "it first.");

            Directory.CreateDirectory(folder);

            var name = MapTable.slotName(slot);
            var written = 0;

            //Kept rather than skipped, so the map's WORDING can come out with it. See the note
            //where it is unpacked below.
            byte[]? said = null;
            byte[]? levelRaw = null;

            foreach (var one in ModPak.read(found.Path))
            {
                //Back to the shape the Maps tab expects, which is not the shape a pak has: the
                //level is `level.json` whatever it is called inside, and the groups and packs sit
                //under their own folders.
                var path = one.Path.Replace(Path.DirectorySeparatorChar, '/');
                string? into = null;

                if (path.IndexOf("/levels/", StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    into = "level.json";
                }
                else if (cut(path, "/objectgroups/") is string groups)
                {
                    into = "objectgroups/" + groups;
                }
                else if (cut(path, "/resourcepacks/") is string packs)
                {
                    into = "resourcepacks/" + packs;
                }

                //The label table is not written out AS IT IS, and that part was always right:
                //it is the game's whole file with this map's rows appended, so putting it in the
                //folder would have the next install append them a second time.
                //
                //But leaving it behind entirely is what made an exported map lose its objectives
                //the moment it went to somebody else. It is kept here and mined below for the
                //rows that belong to this map alone.
                if (into == null)
                {
                    if (path.EndsWith(".csv", StringComparison.OrdinalIgnoreCase))
                    {
                        said = one.Data;
                    }

                    continue;
                }

                if (string.Equals(into, "level.json", StringComparison.Ordinal))
                {
                    levelRaw = one.Data;
                }

                var full = Path.Combine(folder, into.Replace('/', Path.DirectorySeparatorChar));
                Directory.CreateDirectory(Path.GetDirectoryName(full)!);
                File.WriteAllBytes(full, one.Data);
                written++;
            }

            written += wordsOut(folder, levelRaw, said);

            return written;
        }

        /// <summary>
        /// Puts the map's own wording into the exported folder.
        ///
        /// Without this a zip is a map with no objectives for anybody but the person who made it.
        /// The wording lives in the borrowed label table, which on THIS machine already holds the
        /// rows - so a round trip here looks perfect while the same file opened by somebody else
        /// shows `&lt;MISSING STRING TABLE ENTRY&gt;` on every objective.
        ///
        /// Only the rows this map actually uses, and only the ones the game does not already
        /// have. The packed table is Creeper Woods' own file with these appended; writing all of
        /// it back would re-append the game's rows on the next install, and writing none of it is
        /// what was happening before.
        ///
        /// The keys are read from the LEVEL rather than guessed from the table, because the table
        /// is shared and the level is the only thing that says which rows are its own.
        /// </summary>
        private static int wordsOut(string folder, byte[]? levelRaw, byte[]? said)
        {
            if (levelRaw == null || said == null) { return 0; }

            try
            {
                var text = GameMaps.stripComments(
                    new UTF8Encoding(false).GetString(levelRaw).TrimStart('\uFEFF'));

                if (JsonNode.Parse(text, documentOptions: new JsonDocumentOptions
                {
                    AllowTrailingCommas = true,
                    CommentHandling = JsonCommentHandling.Skip,
                }) is not JsonObject level)
                {
                    return 0;
                }

                var wanted = new HashSet<string>(StringComparer.Ordinal);
                keysIn(level, wanted);

                if (wanted.Count == 0) { return 0; }

                //What the game ships already. Anything of its own is not this map's to carry,
                //and exporting it would mean the receiving install appends Creeper Woods' rows
                //to Creeper Woods' table.
                var theirs = new HashSet<string>(
                    MapWords.fromGame(BORROWED).Select(one => one.Key), StringComparer.Ordinal);

                var mine = MapWords.parse(new UTF8Encoding(false).GetString(said))
                    .Where(one => wanted.Contains(one.Key) && !theirs.Contains(one.Key))
                    .ToList();

                if (mine.Count == 0) { return 0; }

                MapWords.forget(folder);

                foreach (var word in mine)
                {
                    MapWords.remember(folder, word.Key, word.Said);
                }

                Console.WriteLine($"[slot] wrote {mine.Count} row(s) of wording into the folder");
                return 1;
            }
            catch (Exception problem)
            {
                //A map that travels without its words is worse than one that does, and better
                //than an export that failed - so this is said and not thrown.
                Console.WriteLine($"[slot] the wording could not be exported: {problem.Message}");
                return 0;
            }
        }

        private static string? cut(string path, string after)
        {
            var at = path.IndexOf(after, StringComparison.OrdinalIgnoreCase);
            return at < 0 ? null : path.Substring(at + after.Length);
        }

        /// <summary>
        /// Whether custom maps are offered in the game at all.
        ///
        /// On by default, because somebody who has just imported a custom map wants to play it,
        /// and a map installed correctly with no way to reach it is indistinguishable from one
        /// that failed to install.
        ///
        /// It exists as an OFF switch, and there is one case that makes it necessary rather than
        /// tidy. The loader works by replacing `/Game/Decor/Prefabs/Tent/BP_Tent`, which is the
        /// anchor the whole modding community uses - Blossoming Isles replaces the same actor.
        /// Only one pak can win a file, so installing both means one loader never runs, and the
        /// symptom is that the other mod silently stops working with nothing said anywhere about
        /// why. Being able to take ours out without deleting every custom map is the difference
        /// between a conflict somebody can resolve and one they can only suffer.
        ///
        /// Remembered outside the paks, like the table's position, because the answer has to
        /// survive the reinstall that every slot change performs.
        /// </summary>
        public static bool inGame
        {
            get
            {
                try { return !File.Exists(refusedAt()); }
                catch (Exception) { return true; }
            }

            set
            {
                var file = refusedAt();

                try
                {
                    if (value)
                    {
                        if (File.Exists(file)) { File.Delete(file); }
                    }
                    else
                    {
                        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
                        File.WriteAllText(file, "custom maps are not offered in the Camp");
                    }
                }
                catch (Exception problem)
                {
                    Console.WriteLine($"[slot] the choice could not be saved: {problem.Message}");
                }

                sync();
            }
        }

        /// <summary>
        /// Where the refusal is kept.
        ///
        /// Written as the ABSENCE of a file rather than a "true" in one, so that the default for
        /// somebody who has never touched it - and for anybody whose settings are lost - is on.
        /// A missing preference file and a preference file that cannot be read both mean yes,
        /// which is the answer that leaves the feature working.
        /// </summary>
        private static string refusedAt()
            => Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "MCDReborn", "no-custom-maps-in-game.txt");

        /// <summary>Whatever is in that slot, or null.</summary>        /// <summary>Whatever is in that slot, or null.</summary>
        public static Filled? inSlot(int slot)
            => installed().FirstOrDefault(one => one.Slot == slot);

        /// <summary>
        /// Renames what a slot is CALLED, without touching the map in it.
        ///
        /// Cheap because the name was never stored anywhere of its own: a slot's pak is called
        /// `MCDReborn_Slot07_HiddenGarden_P.pak`, and the middle of that filename IS the name.
        /// So renaming is renaming a file, and the caption in the Camp follows on the next
        /// install - which <see cref="sync"/> performs immediately.
        ///
        /// The level inside is untouched, so nothing has to be repacked and nothing can be lost.
        /// </summary>
        /// <param name="shownAs">
        /// What to call it. Trimmed to what the panel can show, because the caption is rewritten
        /// inside a cooked widget WITHOUT the property changing size - the spare room is taken
        /// out of a key nothing reads - and a name too long to fit would simply be left as it
        /// was, which reads as the rename not working.
        /// </param>
        public static void rename(int slot, string shownAs)
        {
            var found = inSlot(slot);
            if (found == null)
            {
                throw new InvalidOperationException(
                    $"Slot {slot:00} is empty, so there is nothing to rename.");
            }

            var tidied = tidy(shownAs);

            if (tidied.Length == 0)
            {
                throw new InvalidOperationException(
                    "A name needs at least one letter or number in it.");
            }

            if (tidied.Length > MapTable.CAPTION)
            {
                tidied = tidied.Substring(0, MapTable.CAPTION);
            }

            var folder = Path.GetDirectoryName(found.Path)!;
            var wanted = Path.Combine(folder,
                $"{FOUND_AS}{slot:00}_{tidied}_P.pak");

            if (!string.Equals(wanted, found.Path, StringComparison.OrdinalIgnoreCase))
            {
                //Replace rather than refuse: the name is a label, and two slots are allowed to
                //want the same one. Only the same slot's own file can be in the way.
                if (File.Exists(wanted)) { File.Delete(wanted); }

                File.Move(found.Path, wanted);
            }

            sync();
        }

        /// <summary>
        /// A slot's map, written out as one zip somebody else can import.
        ///
        /// The WORKING FOLDER is what travels, not the pak. A pak plays and cannot be edited -
        /// its level is packed, its object groups are inside it, and getting a map back out of
        /// one is the awkward path this app already has to take when it imports somebody's mod.
        /// The folder is the editable form: the level as json, its object groups, its block
        /// packs, its wording. Shipping that means the person on the other end can open it in
        /// this app, change it, and install it into whichever slot they like - rather than
        /// receiving a thing that can only be run.
        /// </summary>
        public static string zipTo(int slot, string zipPath)
        {
            var found = inSlot(slot)
                ?? throw new InvalidOperationException(
                    $"Slot {slot:00} is empty, so there is nothing to export.");

            var staging = Path.Combine(Path.GetTempPath(), "mcd-zip",
                $"{found.Name}-{Guid.NewGuid():N}");

            try
            {
                Directory.CreateDirectory(staging);

                //Straight through the same unpack the Maps tab uses, so a zip and an "edit in
                //Minecraft" contain the same thing. Two routes that produce different folders is
                //how one of them quietly rots.
                export(slot, staging);

                if (File.Exists(zipPath)) { File.Delete(zipPath); }

                System.IO.Compression.ZipFile.CreateFromDirectory(staging, zipPath);

                return zipPath;
            }
            finally
            {
                try { if (Directory.Exists(staging)) { Directory.Delete(staging, true); } }
                catch (Exception) { /* a temp folder left behind is not worth failing over */ }
            }
        }

        /// <summary>
        /// Somebody else's zip, into a slot.
        ///
        /// The archive is unpacked somewhere temporary and then installed by the ordinary path,
        /// which is what makes this safe: everything the installer checks - the level being
        /// readable, its wording travelling with it, the slot being rewritten rather than added
        /// to - happens exactly as it does for a folder picked by hand.
        ///
        /// A zip may have its files at the top or inside one folder, because both are what people
        /// send, so the level is FOUND rather than assumed.
        /// </summary>
        public static CustomSkins.InstalledMod zipFrom(string zipPath, int slot, string shownAs)
        {
            var staging = Path.Combine(Path.GetTempPath(), "mcd-unzip", Guid.NewGuid().ToString("N"));

            try
            {
                Directory.CreateDirectory(staging);
                System.IO.Compression.ZipFile.ExtractToDirectory(zipPath, staging);

                var level = Directory
                    .EnumerateFiles(staging, "level.json", SearchOption.AllDirectories)
                    .OrderBy(one => one.Length)
                    .FirstOrDefault()
                    ?? throw new InvalidOperationException(
                        "That zip has no level.json in it, so it is not a map this app wrote. "
                        + "A map exported from the Maps tab has one at its top.");

                return install(Path.GetDirectoryName(level)!, slot, shownAs);
            }
            finally
            {
                try { if (Directory.Exists(staging)) { Directory.Delete(staging, true); } }
                catch (Exception) { }
            }
        }

        /// <summary>Empties a slot, and takes it off the table.</summary>
        public static void clear(int slot)
        {
            var found = inSlot(slot);
            if (found != null) { File.Delete(found.Path); }

            shadows(slot, found?.Path);

            sync();
        }

        /// <summary>
        /// Removes anything ELSE installed that claims this slot's level.
        ///
        /// A slot's map lives at one address - "data/lovika/levels/mcdcustom01.json" - and more
        /// than one thing writes there. Installing the same folder over one of the game's own
        /// missions produces "MCDReborn_Map_mcdcustom01_P.pak" holding that exact path, and
        /// clearing the slot used to delete the Slot pak and leave it. Two paks then offered the
        /// game one file and the game read whichever it read, which is not a coin toss anybody
        /// can see: the map loads, it is simply not the one that was just installed.
        ///
        /// That cost most of a day. Every repair made to a map went into the slot, the game kept
        /// reading a copy made hours earlier, and the outcome never changed no matter what was
        /// fixed - so each fix looked wrong and was abandoned for the next guess.
        ///
        /// Matched on what a pak CONTAINS rather than on what it is called, because the naming is
        /// the part that differs: the two install paths agree about the address and about nothing
        /// else. Only this app's own paks are considered, and only the file being argued over -
        /// a pak that happens to sit nearby and holds something different is left alone.
        /// </summary>
        private static void shadows(int slot, string? alreadyGone)
        {
            var folder = CustomSkins.paksFolder;
            if (folder == null || !Directory.Exists(folder)) { return; }

            var wanted = "Dungeons/Content/data/lovika/levels/"
                + MapTable.slotName(slot) + ".json";

            foreach (var pak in Directory.EnumerateFiles(
                folder, CustomSkins.MOD_PREFIX + "*" + CustomSkins.MOD_SUFFIX))
            {
                if (string.Equals(pak, alreadyGone, StringComparison.OrdinalIgnoreCase)) { continue; }

                //A pak's index is plaintext, so a file that does not mention the address anywhere
                //in its bytes cannot be offering it. Cheap, and it matters: this runs on every
                //install, and there can be a hundred slots to walk. Reading them is quick and
                //INFLATING them is not, so the ones that obviously do not match never are.
                try
                {
                    if (File.ReadAllBytes(pak).AsSpan().IndexOf(
                        System.Text.Encoding.ASCII.GetBytes(wanted)) < 0)
                    {
                        continue;
                    }
                }
                catch (Exception) { continue; }

                bool claims;
                try
                {
                    //ModPak.read already hands paths back with forward slashes and no leading
                    //one, which is the shape `wanted` is written in.
                    claims = ModPak.read(pak).Any(one => string.Equals(
                        one.Path, wanted, StringComparison.OrdinalIgnoreCase));
                }
                catch (Exception)
                {
                    //Not every pak beside ours is one this reader understands, and a pak it
                    //cannot open is not evidence of anything. Left where it is.
                    continue;
                }

                if (!claims) { continue; }

                try
                {
                    File.Delete(pak);
                    Console.WriteLine($"[slot] also removed {Path.GetFileName(pak)}, "
                        + $"which claimed the same level");
                }
                catch (Exception problem)
                {
                    //Said rather than thrown: the slot HAS been emptied, and the game holding a
                    //pak open is the ordinary reason this fails.
                    Console.WriteLine($"[slot] {Path.GetFileName(pak)} claims the same level and "
                        + $"could not be removed: {problem.Message}");
                }
            }
        }

        /// <summary>
        /// Makes the Camp's table match what is actually installed.
        ///
        /// Called by anything that fills or empties a slot, rather than left to whoever is using
        /// the app. Which rows the panel shows is baked into a cooked widget, so the table has to
        /// be rewritten every time the set of filled slots changes - and a person who has just
        /// installed a map has no way of knowing that. Leaving it to them means a map that is
        /// installed, correct, and invisible, with nothing on screen explaining why.
        ///
        /// With nothing installed the table is REMOVED rather than written empty. A prop in the
        /// Camp that opens a menu of nothing is worse than no prop: it looks broken, and it is
        /// the state somebody lands in after tidying up.
        /// </summary>
        public static void sync()
        {
            //Said before anything is attempted, because the failure it prevents is invisible.
            //
            //The game holds its paks open for as long as it is running, so rewriting the table
            //while it is up throws - and every caller here treats that as "nothing to do". The
            //app then reports success, the OLD table stays mounted, and the change quietly did
            //not happen. That cost a round trip of "the statue moved back on its own".
            if (GameRunning.isUp)
            {
                Console.WriteLine("[slot] the game is running, so the Camp table was not "
                    + "updated - close the game and change a slot again, or the table will "
                    + "keep whatever it had");
            }

            var slots = installed();

            //An EMPTY table still stands in the Camp, and only the toggle takes it away.
            //
            //It used to go when the last map went, on the reasoning that a menu with no rows is
            //a menu of nothing. That reasoning was about the menu and ignored the prop: the
            //statue is a thing somebody placed, walked to, and positioned by eye over several
            //restarts, and having it disappear because they cleared their maps reads as the mod
            //breaking rather than as a tidy-up.
            //
            //It also cost the position. The installed table is where the last good coordinate
            //lives - see MapTable.whereItStands - so removing it threw away the only record
            //that survives a preference file the app cannot read.
            if (!inGame)
            {
                MapTable.remove();

                //The loader goes too, but ONLY if nothing else is standing on it.
                //
                //It is a mod in somebody's game folder, and one they did not ask for once the
                //thing it was installed for is gone. But payloads are installed INTO its folders
                //- a camera, whatever comes next - and pulling it out from under those would
                //break features the person never touched, silently, in a different part of the
                //app.
                if (Payloads.installed().Count == 0)
                {
                    Loader.remove();
                }

                return;
            }

            //The table is a level, and a level in a pak does nothing without the loader that
            //streams it in. Installing it here rather than asking is the difference between a
            //custom map that appears in the Camp and one that is installed, correct, and
            //invisible - with a button somewhere else that nobody has a reason to press.
            //
            //Only when something is actually going to use it. A loader installed for its own
            //sake is a mod in somebody's game folder that they did not ask for.
            if (!Loader.isInstalled)
            {
                try
                {
                    Loader.installBuiltIn();
                }
                catch (Exception problem)
                {
                    Console.WriteLine($"[slot] the loader could not be installed: "
                        + $"{problem.Message}");
                }
            }

            MapTable.installBuiltIn(
                slots.Select(one => one.Slot).ToList(),
                slots.ToDictionary(one => one.Slot, one => one.Name));
        }

        /// <summary>
        /// Puts a map exported by the Maps tab into a slot.
        ///
        /// The folder is the same one <see cref="MapMod"/> reads, so a map can be installed either
        /// way without being exported twice.
        /// </summary>
        public static CustomSkins.InstalledMod install(string folder, int slot, string shownAs)
        {
            if (slot < 1 || slot > MapTable.SLOTS)
            {
                throw new InvalidOperationException(
                    $"There are {MapTable.SLOTS} slots, numbered 1 to {MapTable.SLOTS}.");
            }

            var level = Path.Combine(folder, "level.json");
            if (!File.Exists(level)) { level = Path.Combine(folder, "level"); }

            if (!File.Exists(level))
            {
                throw new InvalidOperationException(
                    "There is no file called \"level.json\" in that folder, so it is not a map "
                    + "exported by this tab.");
            }

            //Every route into the game passes through here, so this is where a level that the
            //generator would refuse gets repaired - not in whichever button happened to be
            //pressed. A map can arrive from a Minecraft world, from somebody else's zip, or
            //from a folder welded by an older build, and only one of those was being checked.
            //
            //It changes nothing unless the level is already invalid: a tile is only touched when
            //it declares two or more travel entry doors, which the game rejects outright.
            //Markers onto real ground, before anything is packed.
            //
            //Here rather than in the button that imports, because there are two import paths and
            //only one of them was covered: a map that still has several tiles is welded first and
            //went through the fix, a map that is already one tile is installed directly and
            //skipped it entirely. The second is what a re-import of an existing custom map does,
            //so the case most likely to be repeated was the one not covered.
            try
            {
                var plan = MapSpawns.load(folder);
                var settled = MapSpawns.settle(plan);

                if (settled > 0)
                {
                    MapSpawns.save(plan);
                    Console.WriteLine($"[slot] settled {settled} marker(s) onto the ground");

                    foreach (var note in plan.Notes)
                    {
                        Services.Journal.note("  " + note);
                    }
                }
            }
            catch (Exception problem)
            {
                //A map that installs with its markers where they were is no worse than before
                //this existed; refusing to install at all would be.
                Console.WriteLine($"[slot] could not settle the markers: {problem.Message}");
            }

            MapMod.tidyMobGroups(folder);
            MapMod.wireMobs(folder);
            var untangled = MapMod.dropSidePaths(folder);
            if (untangled > 0)
            {
                Console.WriteLine($"[slot] dropped the dead side-paths from {untangled} tile(s)");
            }

            clear(slot);

            var name = MapTable.slotName(slot);
            var entries = new List<PakWriter.Entry>
            {
                new PakWriter.Entry(
                    "Dungeons/Content/data/lovika/levels/" + name + ".json",
                    standalone(File.ReadAllBytes(level))),
            };

            //Wording, appended to the borrowed table's own rows rather than replacing them.
            //
            //Two sources, and the second is the one that is easy to forget. Whatever this editor
            //minted for the map is in the folder - but a map that came from somewhere else names
            //keys belonging to whichever table it used to read, and that table is not the one it
            //reads now. Those rows have to come along or every objective is blank.
            var words = new List<MapWords.Word>(MapWords.fromFolder(folder));
            words.AddRange(carried(File.ReadAllBytes(level), words));

            var said = MapWords.tableWith(BORROWED, words);
            if (said != null)
            {
                entries.Add(new PakWriter.Entry(MapWords.writePathFor(BORROWED), said));
                Console.WriteLine($"[slot] shipping {words.Count} row(s) of wording");
            }
            else
            {
                Console.WriteLine("[slot] the map names no wording of its own");
            }

            foreach (var (from, into) in new[]
            {
                (Path.Combine(folder, "objectgroups"), GameMaps.GROUPS),
                (Path.Combine(folder, "resourcepacks"), GameMaps.PACKS),
            })
            {
                if (!Directory.Exists(from)) { continue; }

                foreach (var file in Directory.GetFiles(from, "*", SearchOption.AllDirectories))
                {
                    //Working copies the editor leaves beside the real files.
                    if (file.EndsWith(".before", StringComparison.OrdinalIgnoreCase)
                        || file.EndsWith(".random", StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    var tail = file.Substring(from.Length).Replace(
                        Path.DirectorySeparatorChar, '/').TrimStart('/');

                    entries.Add(new PakWriter.Entry(
                        "Dungeons/Content/" + into + tail, File.ReadAllBytes(file)));
                }
            }

            var made = CustomSkins.writeModPak($"{PREFIX}{slot:00}_{tidy(shownAs)}", entries);

            //The table lists what is installed, so installing something changes the table.
            sync();

            return made;
        }

        /// <summary>
        /// The level, made to stand on its own.
        ///
        /// Three edits, and each one is a thing that silently does nothing if left out:
        ///
        /// `loctable-id` is pointed at the borrowed table, because a level's own id is what it
        /// defaults to and the game has no table by that name.
        ///
        /// `ambience-level-id` is given a real mission's name if the level has neither it nor a
        /// `music-override`. The game resolves music by parsing this as one of its own level
        /// names, and an id it cannot parse means the map plays in silence.
        ///
        /// The level's `id` is left exactly as it is. It is not only the theme: the game looks
        /// for a tile's companion sub-level at `Decor/Maps/&lt;id&gt;/SubLevels/&lt;tile&gt;`, and rewriting
        /// it sends the game looking in a folder that does not exist - which crashes on entering
        /// the level, as it once did.
        /// </summary>
        private static byte[] standalone(byte[] raw)
        {
            try
            {
                var text = GameMaps.stripComments(
                    new UTF8Encoding(false).GetString(raw).TrimStart('﻿'));

                if (JsonNode.Parse(text, documentOptions: new JsonDocumentOptions
                {
                    AllowTrailingCommas = true,
                    CommentHandling = JsonCommentHandling.Skip,
                }) is not JsonObject level)
                {
                    return raw;
                }

                level["loctable-id"] = BORROWED;

                //The level's own `id` has to be a level THE GAME HAS, and this is the field that
                //was crashing every map this app made.
                //
                //Proved by holding everything else still: the same folder installed over Creeper
                //Woods loads, installed into a slot crashes, and the two level files differ in
                //exactly one field - `id`, "creeperwoods" against "mcdcustom02". Blossoming's map
                //survives a slot because its id is "lowertemple", which is a real mission; it was
                //never the slot machinery that worked for it, only the borrowed name.
                //
                //The game looks the id up. A name it does not know is not a missing lookup, it is
                //a crash on the loading screen with nothing said about which field was wrong.
                //
                //What this does NOT do is change how the map is LAUNCHED. That stays
                //`randommission`, so nothing is recorded against the real mission's progress -
                //the id is borrowed for lookups, not for identity. Which is the same bargain the
                //loctable and the ambience already make.
                var was = level["id"]?.GetValue<string>();

                var real = was != null && GameMaps.all().Any(one =>
                    string.Equals(one.Name, was, StringComparison.OrdinalIgnoreCase));

                if (!real)
                {
                    if (was != null)
                    {
                        Console.WriteLine($"[slot] \"{was}\" is not a level this game has, so "
                            + $"the map is identified as {BORROWED} instead");
                    }

                    level["id"] = BORROWED;
                }

                //Ambience has to name a level THE GAME HAS, and it was only being filled in
                //when the level named none at all.
                //
                //That left the worst case untouched. A map made here starts with its own id in
                //that field - "mcdcustom01" - which is not null, so nothing replaced it, and the
                //game went looking for the ambience of a level it has never heard of. It does
                //not survive that, and the crash arrives on the loading screen with nothing to
                //say which field was wrong.
                //
                //A real name is kept: a map imported from another mod may legitimately say
                //CactiCanyon, and that works because Cacti Canyon exists. Anything the game
                //cannot find is replaced with the mission this slot already borrows everything
                //else from.
                var ambience = level["ambience-level-id"]?.GetValue<string>();

                var known = ambience != null && GameMaps.all().Any(one =>
                    string.Equals(one.Name, ambience, StringComparison.OrdinalIgnoreCase));

                if (level["music-override"] == null && !known)
                {
                    if (ambience != null)
                    {
                        Console.WriteLine($"[slot] \"{ambience}\" is not a level this game has, "
                            + $"so its ambience comes from {BORROWED} instead");
                    }

                    level["ambience-level-id"] = "creeperwoods";
                }

                return new UTF8Encoding(false).GetBytes(
                    level.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
            }
            catch (Exception problem)
            {
                //Shipped unchanged rather than not at all. A level this app cannot parse may
                //still be one the game can read, and saying so beats refusing silently.
                Console.WriteLine($"[slot] the level could not be re-read ({problem.Message}), "
                    + "shipping it exactly as it is");
                return raw;
            }
        }

        /// <summary>
        /// The rows this level's objectives need, taken from the table it used to read.
        ///
        /// Every `name` and `description` in the level is a KEY, not a sentence, so the wording
        /// lives in a CSV somewhere else. Which CSV is the level's own `loctable-id`, or failing
        /// that its `id` - the same rule the game uses when it has not been told otherwise.
        ///
        /// Candidates are collected loosely and then filtered by whether that table actually
        /// defines them. Collecting loosely is deliberate: an object group's name looks exactly
        /// like an objective's key from the outside, and the filter costs nothing while guessing
        /// which is which would cost wording.
        /// </summary>
        private static List<MapWords.Word> carried(byte[] raw, List<MapWords.Word> already)
        {
            var found = new List<MapWords.Word>();

            try
            {
                var text = GameMaps.stripComments(
                    new UTF8Encoding(false).GetString(raw).TrimStart('﻿'));

                if (JsonNode.Parse(text, documentOptions: new JsonDocumentOptions
                {
                    AllowTrailingCommas = true,
                    CommentHandling = JsonCommentHandling.Skip,
                }) is not JsonObject level)
                {
                    return found;
                }

                var was = level["loctable-id"]?.GetValue<string>()
                    ?? level["id"]?.GetValue<string>();

                //Nothing to carry when the level already reads the table it is moving to.
                if (string.IsNullOrEmpty(was)
                    || string.Equals(was, BORROWED, StringComparison.OrdinalIgnoreCase))
                {
                    return found;
                }

                var theirs = MapWords.fromGame(was!);
                if (theirs.Count == 0) { return found; }

                var wanted = new HashSet<string>(StringComparer.Ordinal);
                keysIn(level, wanted);

                var have = new HashSet<string>(already.Select(one => one.Key),
                    StringComparer.Ordinal);

                foreach (var word in theirs)
                {
                    if (wanted.Contains(word.Key) && have.Add(word.Key)) { found.Add(word); }
                }

                Console.WriteLine($"[slot] carried {found.Count} row(s) of wording over from "
                    + $"\"{was}\"");
            }
            catch (Exception problem)
            {
                Console.WriteLine($"[slot] could not read the level's wording: {problem.Message}");
            }

            return found;
        }

        /// <summary>Every string under a "name" or "description", anywhere in the level.</summary>
        private static void keysIn(JsonNode? node, HashSet<string> into)
        {
            switch (node)
            {
                case JsonObject body:
                    foreach (var pair in body)
                    {
                        if ((pair.Key == "name" || pair.Key == "description")
                            && pair.Value is JsonValue said
                            && said.TryGetValue<string>(out var key)
                            && key.Length > 0)
                        {
                            into.Add(key);
                        }

                        keysIn(pair.Value, into);
                    }
                    break;

                case JsonArray list:
                    foreach (var one in list) { keysIn(one, into); }
                    break;
            }
        }

        private static string tidy(string name)
        {
            var kept = new string(name.Where(char.IsLetterOrDigit).ToArray());
            return kept.Length == 0 ? "map" : kept;
        }
    }
}
