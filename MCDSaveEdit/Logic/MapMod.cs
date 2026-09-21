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
    /// Taking a mission out of the game to work on, and putting one back.
    ///
    /// Both directions are file moves rather than conversions, which is the whole reason this is
    /// possible: a mission is JSON and compressed block arrays, and a mod pak carrying the same
    /// paths as the game's own wins over them. Nothing in the game's files is touched, and
    /// removing the pak puts the original mission back exactly.
    ///
    /// The folder written by export is the folder import expects, so the round trip needs no
    /// bookkeeping from whoever is using it:
    ///
    ///     &lt;folder&gt;/map.json                               which mission this came from
    ///     &lt;folder&gt;/level.json                             the mission itself
    ///     &lt;folder&gt;/objectgroups/&lt;Name&gt;/objectgroup.json   the tiles it is built from
    ///     &lt;folder&gt;/resourcepacks/&lt;Name&gt;/blocks.json       what its block ids mean
    /// </summary>
    public static class MapMod
    {
        /// <summary>What a map mod is called, so it can be found and removed on its own.</summary>
        private const string PREFIX = "Map_";

        /// <summary>The note export leaves behind, so import knows what it is looking at.</summary>
        public const string MARKER = "map.json";

        /// <summary>
        /// The real file name, extension and all.
        ///
        /// Worth stating plainly because the app never sees it when reading. PakReader's index
        /// merge drops any entry whose name has no dot and strips the extension off the rest, so
        /// the game's "data/lovika/levels/creeperwoods.json" is spelled "…/creeperwoods"
        /// everywhere the index is asked. Reading through the index therefore works with the
        /// short name and WRITING does not: a pak built with the short names contains files the
        /// game will never look for, and reads back as empty.
        /// </summary>
        private const string LEVEL_FILE = "level.json";
        private const string GROUPS_FOLDER = "objectgroups";
        private const string PACKS_FOLDER = "resourcepacks";

        public sealed class Exported
        {
            public Exported(string folder, int files, long bytes, IReadOnlyList<string> notes)
            {
                Folder = folder;
                Files = files;
                Bytes = bytes;
                Notes = notes;
            }

            public string Folder { get; }
            public int Files { get; }
            public long Bytes { get; }
            public IReadOnlyList<string> Notes { get; }
        }

        /// <summary>
        /// Writes everything a mission is made of into a folder.
        ///
        /// The whole resource pack is deliberately not copied. It is a thousand textures and
        /// twenty megabytes, and none of it is needed to change a map - only the blocks file is,
        /// because that is the table saying which block id means which block.
        /// </summary>
        public static Exported export(GameMaps.Mission mission, string folder)
        {
            var notes = new List<string>();
            var files = 0;
            var bytes = 0L;

            var level = GameMaps.read(mission.PakPath)
                ?? throw new InvalidOperationException(
                    $"{mission.Name} could not be read out of the game's paks.");

            Directory.CreateDirectory(folder);

            void write(string relative, byte[] data)
            {
                var full = Path.Combine(folder, relative.Replace('/', Path.DirectorySeparatorChar));
                Directory.CreateDirectory(Path.GetDirectoryName(full)!);
                File.WriteAllBytes(full, data);
                files++;
                bytes += data.Length;
            }

            write(LEVEL_FILE, level);

            var (groups, packs) = GameMaps.referencedBy(level);
            notes.Add($"{groups.Count} object group(s), {packs.Count} resource pack(s)");

            foreach (var group in groups)
            {
                //A level names a group as "Arcane/objectgroup" - the folder and the file together.
                var got = GameMaps.read("/Dungeons/Content/" + GameMaps.GROUPS + group);
                if (got == null)
                {
                    notes.Add($"object group not found: {group}");
                    continue;
                }
                write(GROUPS_FOLDER + "/" + group + ".json", got);
            }

            foreach (var pack in packs)
            {
                var got = GameMaps.read("/Dungeons/Content/" + GameMaps.PACKS + pack + "/blocks");
                if (got == null)
                {
                    notes.Add($"no blocks file for resource pack: {pack}");
                    continue;
                }
                write(PACKS_FOLDER + "/" + pack + "/blocks.json", got);
            }

            write(MARKER, Encoding.UTF8.GetBytes(marker(mission, groups, packs)));
            write("READ ME.txt", Encoding.UTF8.GetBytes(readMe(mission)));

            return new Exported(folder, files, bytes, notes);
        }

        /// <summary>
        /// Puts a folder back into the game, over whichever mission is chosen.
        ///
        /// The mission being replaced does not have to be the one it came from. That is the point
        /// of choosing: a map built from Creeper Woods' tiles can be installed over Lower Temple,
        /// and Creeper Woods stays as it was.
        /// </summary>
        /// <summary>
        /// Makes a level claim to be the mission it is being installed over.
        ///
        /// A level's id is not its own name. It is a lookup into the game's own table of levels -
        /// for the theme, the lighting, the ambience and the text - and the table is fixed at
        /// fifty-odd names that shipped with the game. A level whose id is something else asks
        /// for a level that does not exist.
        ///
        /// Blossoming Isles, which works, never gets this wrong: its three levels live in files
        /// called SakuraGarden, SakuraPagoda and SakuraUndercroft, and every one of them declares
        /// id "lowertemple" and borrows Cacti Canyon's ambience and text. None of its own names
        /// appear anywhere in the game's table, and none of them are used.
        ///
        /// Done HERE rather than when the map is made, because this is the only moment the answer
        /// is certain. A folder can be built from one mission, edited, and installed over
        /// another; it can arrive through Import map from somebody else entirely. What the level
        /// has to claim is whatever it is about to be loaded as, and that is known once and only
        /// once - now.
        /// </summary>
        private static byte[] claim(byte[] raw, GameMaps.Mission over)
        {
            try
            {
                var text = GameMaps.stripComments(
                    new UTF8Encoding(false).GetString(raw).TrimStart('\uFEFF'));

                if (JsonNode.Parse(text, documentOptions: new JsonDocumentOptions
                {
                    AllowTrailingCommas = true,
                    CommentHandling = JsonCommentHandling.Skip,
                }) is not JsonObject level)
                {
                    return raw;
                }

                var was = level["id"]?.GetValue<string>();
                if (string.Equals(was, over.Name, StringComparison.Ordinal)) { return raw; }

                level["id"] = over.Name;

                //These two are separate lookups into the same table. They come along when they
                //were following the id - which is what a map that never thought about them looks
                //like - and are left alone when they name something else, because borrowing one
                //level's ambience for another is deliberate and Blossoming Isles does exactly
                //that: id "lowertemple", ambience and text from Cacti Canyon.
                foreach (var also in new[] { "ambience-level-id", "loctable-id" })
                {
                    var had = level[also]?.GetValue<string>();

                    if (had == null || string.Equals(had, was, StringComparison.Ordinal))
                    {
                        level[also] = over.Name;
                    }
                }

                Console.WriteLine($"[map] level id \"{was}\" -> \"{over.Name}\" for install");

                return new UTF8Encoding(false).GetBytes(
                    level.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
            }
            catch (Exception problem)
            {
                //A level this cannot read is one the game may still be able to. Installing it
                //unchanged is the same behaviour as before this existed.
                Console.WriteLine($"[map] could not set the level id: {problem.Message}");
                return raw;
            }
        }

        public static CustomSkins.InstalledMod install(string folder, GameMaps.Mission over)
        {
            var level = Path.Combine(folder, LEVEL_FILE);

            //Folders written before the .json names were understood have "level" instead.
            if (!File.Exists(level)) { level = Path.Combine(folder, "level"); }

            if (!File.Exists(level))
            {
                throw new InvalidOperationException(
                    $"There is no file called \"{LEVEL_FILE}\" in that folder, so it is not a map "
                    + "exported by this tab.");
            }

            var entries = new List<PakWriter.Entry>
            {
                //No leading slash inside a pak, and the game's own spelling of the path - this is
                //what makes the game read our file instead of its own.
                new PakWriter.Entry(
                    "Dungeons/Content/data/lovika/levels/" + over.Name + ".json",
                    claim(File.ReadAllBytes(level), over)),
            };

            var groups = Path.Combine(folder, GROUPS_FOLDER);
            if (Directory.Exists(groups))
            {
                foreach (var file in Directory.GetFiles(groups, "*", SearchOption.AllDirectories))
                {
                    if (isWorkingFile(file)) { continue; }

                    entries.Add(new PakWriter.Entry(
                        "Dungeons/Content/" + GameMaps.GROUPS + relative(groups, file),
                        File.ReadAllBytes(file)));
                }
            }

            var packs = Path.Combine(folder, PACKS_FOLDER);
            if (Directory.Exists(packs))
            {
                foreach (var file in Directory.GetFiles(packs, "*", SearchOption.AllDirectories))
                {
                    if (isWorkingFile(file)) { continue; }

                    entries.Add(new PakWriter.Entry(
                        "Dungeons/Content/" + GameMaps.PACKS + relative(packs, file),
                        File.ReadAllBytes(file)));
                }
            }

            if (entries.Count == 1)
            {
                //Not fatal - a level can be built entirely from another mission's tiles - but it
                //is nearly always a sign that the folder was moved without its object groups.
                //Said out loud rather than discovered in game as a mission that will not start.
                throw new InvalidOperationException(
                    "That folder has a level but no object groups. A mission with no tiles cannot "
                    + $"be generated. Expected them under \"{GROUPS_FOLDER}\".");
            }

            return CustomSkins.writeModPak(PREFIX + safe(over.Name), entries);
        }

        /// <summary>Every map mod installed.</summary>
        /// <summary>
        /// Whether installing would actually change what the game loads.
        ///
        /// "Nothing changed yet" beside a live Save and install is a button offering to do
        /// nothing, and a press that does nothing is indistinguishable from a press that failed.
        /// But "nothing changed" is not the same as "nothing to install": a folder that arrived
        /// through Import, or one saved in an earlier session, has everything to install and no
        /// unsaved edits at all.
        ///
        /// So the question is asked of the PAK rather than of the edit list - is there anything
        /// in the folder the installed one has not got? File times only; nothing is read.
        /// </summary>
        public static bool worthInstalling(string folder, GameMaps.Mission over)
        {
            try
            {
                var pak = installedFor(over);
                if (pak == null || !File.Exists(pak)) { return true; }

                var packed = File.GetLastWriteTimeUtc(pak);

                foreach (var file in Directory.GetFiles(folder, "*", SearchOption.AllDirectories))
                {
                    //The kept originals and the pre-weld copy are not what gets packed, so a
                    //fresh one of those is not a reason to install.
                    if (file.EndsWith(".before", StringComparison.OrdinalIgnoreCase)
                        || file.EndsWith(".multitile", StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    if (File.GetLastWriteTimeUtc(file) > packed) { return true; }
                }

                return false;
            }
            catch
            {
                //A folder that cannot be looked at is one where the honest answer is "maybe",
                //and a button that is on when it need not be beats one that is off when it is
                //wanted.
                return true;
            }
        }

        public static IReadOnlyList<string> installed()
        {
            var folders = new List<string>();
            if (CustomSkins.paksFolder != null) { folders.Add(CustomSkins.paksFolder); }
            if (CustomSkins.modsFolder != null) { folders.Add(CustomSkins.modsFolder); }

            return folders
                .Where(Directory.Exists)
                .SelectMany(folder => Directory.GetFiles(
                    folder, CustomSkins.MOD_PREFIX + PREFIX + "*.pak"))
                .OrderBy(file => file, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        /// <summary>The mod that replaces one particular mission, if there is one.</summary>
        public static string? installedFor(GameMaps.Mission mission)
        {
            var wanted = CustomSkins.MOD_PREFIX + PREFIX + safe(mission.Name);

            return installed().FirstOrDefault(pak =>
                Path.GetFileNameWithoutExtension(pak)
                    .StartsWith(wanted, StringComparison.OrdinalIgnoreCase));
        }

        /// <summary>Puts a mission back to the game's own, by deleting what replaced it.</summary>
        public static bool remove(GameMaps.Mission mission)
        {
            var pak = installedFor(mission);
            if (pak == null || !File.Exists(pak)) { return false; }

            File.Delete(pak);
            return true;
        }

        /// <summary>Which mission a folder came from, if it says.</summary>
        public static string? cameFrom(string folder)
        {
            var marker = Path.Combine(folder, MARKER);
            if (!File.Exists(marker)) { return null; }

            try
            {
                using var document = System.Text.Json.JsonDocument.Parse(File.ReadAllText(marker));
                return document.RootElement.TryGetProperty("mission", out var name)
                    ? name.GetString()
                    : null;
            }
            catch (Exception)
            {
                return null;
            }
        }

        /// <summary>
        /// Whether a file in an exported folder is one of ours rather than one to ship.
        ///
        /// The converters keep the previous version of whatever they rewrite - objectgroup.json
        /// .before, level.json.random, level.json.multitile - which is exactly what you want on
        /// disk and exactly what you do not want in a pak. Shipped, they doubled the size of one
        /// and appeared in the index as a second object group, because the reader strips the last
        /// extension and ".json.before" comes back as ".json".
        /// </summary>
        private static bool isWorkingFile(string file)
        {
            var name = Path.GetFileName(file);
            return name.EndsWith(".before", StringComparison.OrdinalIgnoreCase)
                || name.EndsWith(".new", StringComparison.OrdinalIgnoreCase)
                || name.EndsWith(".random", StringComparison.OrdinalIgnoreCase)
                || name.EndsWith(".multitile", StringComparison.OrdinalIgnoreCase)
                || name.StartsWith(".", StringComparison.Ordinal);
        }

        private static string relative(string from, string file)
            => Path.GetRelativePath(from, file).Replace(Path.DirectorySeparatorChar, '/');

        private static string marker(GameMaps.Mission mission, List<string> groups, List<string> packs)
        {
            var quoted = (IEnumerable<string> of)
                => string.Join(", ", of.Select(one => "\"" + one + "\""));

            return "{\n"
                + $"  \"mission\": \"{mission.Name}\",\n"
                + $"  \"label\": \"{mission.Label}\",\n"
                + $"  \"object-groups\": [{quoted(groups)}],\n"
                + $"  \"resource-packs\": [{quoted(packs)}],\n"
                + $"  \"exported\": \"{DateTime.Now:yyyy-MM-dd HH:mm}\"\n"
                + "}\n";
        }

        private static string readMe(GameMaps.Mission mission) => string.Join("\n", new[]
        {
            $"{mission.Label}, exported from Minecraft Dungeons by MCD Reborn.",
            "",
            "  level                         the mission: which tiles, in what order, with what objectives",
            "  objectgroups/<Name>/objectgroup   the tiles themselves - Minecraft blocks, zlib'd and base64'd",
            "  resourcepacks/<Name>/blocks       what each block id means. Key order IS the id.",
            "",
            "A tile's blocks array is one byte of block id per cell followed by half a byte of",
            "metadata each, indexed x + sizeX * (z + sizeZ * y) - the same layout a Minecraft",
            ".schematic uses. The high nibble of each packed byte belongs to the even index.",
            "",
            "Edit what you like and bring the folder back to the Maps tab. Import asks which",
            "mission to install it over; the game's own files are never modified, and Remove",
            "deletes the mod pak and gives the original mission back.",
            "",
        });

        /// <summary>A name that is safe in a file name.</summary>
        private static string safe(string name)
        {
            var clean = new string(name
                .Select(c => char.IsLetterOrDigit(c) ? c : '_')
                .ToArray());

            return clean.Trim('_');
        }
    }
}
