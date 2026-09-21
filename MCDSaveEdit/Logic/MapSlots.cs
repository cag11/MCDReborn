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

                //The label table is deliberately left behind. It is the game's file with this
                //map's rows appended, and writing it into the folder would have the next install
                //append them all over again.
                if (into == null) { continue; }

                var full = Path.Combine(folder, into.Replace('/', Path.DirectorySeparatorChar));
                Directory.CreateDirectory(Path.GetDirectoryName(full)!);
                File.WriteAllBytes(full, one.Data);
                written++;
            }

            return written;
        }

        private static string? cut(string path, string after)
        {
            var at = path.IndexOf(after, StringComparison.OrdinalIgnoreCase);
            return at < 0 ? null : path.Substring(at + after.Length);
        }

        /// <summary>Whatever is in that slot, or null.</summary>
        public static Filled? inSlot(int slot)
            => installed().FirstOrDefault(one => one.Slot == slot);

        /// <summary>Empties a slot, and takes it off the table.</summary>
        public static void clear(int slot)
        {
            var found = inSlot(slot);
            if (found != null) { File.Delete(found.Path); }

            sync();
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
            var slots = installed();

            if (slots.Count == 0)
            {
                MapTable.remove();
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

                if (level["music-override"] == null && level["ambience-level-id"] == null)
                {
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
