using MCDSaveEdit.Services;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
#nullable enable

namespace MCDSaveEdit.Logic
{
    /// <summary>
    /// The game's missions, as the files that describe them.
    ///
    /// A mission in this game is not an Unreal map. It is generated at run time from JSON in the
    /// paks: data/lovika/levels/&lt;name&gt; lists a sequence of stretches, each picking randomly
    /// from a set of tiles, and the tiles come from one or more object groups named in the same
    /// file. The geometry inside those tiles is Minecraft blocks.
    ///
    /// Which is why this tab can exist at all without an Unreal editor. Everything a mission is
    /// made of is data, and a mod pak that ships the same path wins over the game's own - so a
    /// mission can be replaced, and removing the pak puts it straight back.
    /// </summary>
    public static class GameMaps
    {
        /// <summary>
        /// Where the level files live, spelled the way a pak spells its own entries.
        ///
        /// No leading slash, for the reason the music catalogue learned the hard way: the index
        /// joins a mount point onto keys that already begin with one, so a filter written with a
        /// leading slash matches nothing at all and says nothing about it.
        /// </summary>
        private const string LEVELS = "data/lovika/levels/";

        public const string GROUPS = "data/lovika/objectgroups/";
        public const string PACKS = "data/resourcepacks/";

        /// <summary>What went wrong, or what was skipped, while building the list.</summary>
        public static List<string> Notes { get; } = new List<string>();

        public sealed class Mission
        {
            public Mission(string name, string pakPath, long bytes)
            {
                Name = name;
                PakPath = pakPath;
                Bytes = bytes;
            }

            /// <summary>The file name, which is the mission's identity - "creeperwoods".</summary>
            public string Name { get; }

            /// <summary>Where it sits in the paks, with exactly one leading slash.</summary>
            public string PakPath { get; }

            public long Bytes { get; }

            /// <summary>The name to show, with the file name beside it when they differ.</summary>
            public string Label
            {
                get
                {
                    var pretty = prettyName(Name);
                    return pretty == Name ? Name : $"{pretty}  ({Name})";
                }
            }
        }

        /// <summary>
        /// The missions whose names are known for certain.
        ///
        /// The file names are lower case and run together, so "creeperwoods" cannot be split by
        /// any rule - it needs a dictionary. Only missions that are certain are listed, and the
        /// rest show their raw file name, on the same principle the music catalogue uses: a
        /// confident wrong label is worse than none when somebody is choosing what to overwrite.
        /// </summary>
        private static readonly Dictionary<string, string> NAMES =
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            { "creeperwoods", "Creeper Woods" },
            { "creepycrypt", "Creepy Crypt" },
            { "soggyswamp", "Soggy Swamp" },
            { "pumpkinpastures", "Pumpkin Pastures" },
            { "archhaven", "Arch Haven" },
            { "redstonemines", "Redstone Mines" },
            { "fieryforge", "Fiery Forge" },
            { "highblockhalls", "Highblock Halls" },
            { "obsidianpinnacle", "Obsidian Pinnacle" },
            { "deserttemple", "Desert Temple" },
            { "cacticanyon", "Cacti Canyon" },
            { "soggycave", "Soggy Cave" },
            { "lowertemple", "Lower Temple" },
            { "underhalls", "Underhalls" },
            { "arrowgorge", "Arrow Gorge" },
            { "mooshroomisland", "Mooshroom Island" },
            { "dingyjungle", "Dingy Jungle" },
            { "pandaplateau", "Panda Plateau" },
            { "coralrise", "Coral Rise" },
            { "abyssalmonument", "Abyssal Monument" },
            { "frozenfjord", "Frozen Fjord" },
            { "lonelyfortress", "Lonely Fortress" },
            { "lostsettlement", "Lost Settlement" },
            { "blightedcitadel", "Blighted Citadel" },
            { "gauntletgales", "Gauntlet of Gales" },
            { "galesanctum", "Gale Sanctum" },
            { "windsweptpeaks", "Windswept Peaks" },
            { "colddepths", "Cold Depths" },
            { "mooncorecaverns", "Mooncore Caverns" },
            { "bamboobluff", "Bamboo Bluff" },
            { "netherwastes", "Nether Wastes" },
            { "soulsandvalley", "Soul Sand Valley" },
            { "crimsonforest", "Crimson Forest" },
            { "basaltdeltas", "Basalt Deltas" },
            { "netherfortress", "Nether Fortress" },
            { "warpedforest", "Warped Forest" },
            { "ancientdungeons", "Ancient Dungeons" },
            { "endlessrampart", "Endless Rampart" },
            { "enderwilds", "Ender Wilds" },
            { "freejungle", "Jungle Awakens" },
            { "lobby", "The Camp" },
            { "menudummy", "Main menu backdrop" },
        };

        /// <summary>
        /// A readable name for a mission file.
        ///
        /// Known ones come from the table. Anything camel cased is split, because several files
        /// already are - AbyssalMonument, HighblockHalls - and splitting those is safe where
        /// guessing at a run-together lower case name is not. The hm_ prefix marks a hyper
        /// mission and is kept as a suffix rather than dropped, since it distinguishes two files
        /// that are otherwise the same mission.
        /// </summary>
        public static string prettyName(string file)
        {
            var hyper = file.StartsWith("hm_", StringComparison.OrdinalIgnoreCase);
            var bare = hyper ? file.Substring(3) : file;

            string found;
            if (NAMES.TryGetValue(bare, out var known)) { found = known; }
            else if (bare.Skip(1).Any(char.IsUpper)) { found = splitCamelCase(bare); }
            else { return file; }

            return hyper ? found + " (hyper)" : found;
        }

        private static string splitCamelCase(string word)
        {
            var made = new StringBuilder();
            for (var i = 0; i < word.Length; i++)
            {
                if (i > 0 && char.IsUpper(word[i]) && !char.IsUpper(word[i - 1])) { made.Append(' '); }
                made.Append(i == 0 ? char.ToUpperInvariant(word[i]) : word[i]);
            }
            return made.ToString();
        }

        /// <summary>Every mission the game ships, by name.</summary>
        public static IReadOnlyList<Mission> all()
        {
            Notes.Clear();

            var index = CustomSkins.index;
            if (index == null)
            {
                Notes.Add("the game's paks are not loaded");
                return new List<Mission>();
            }

            var found = new Dictionary<string, Mission>(StringComparer.OrdinalIgnoreCase);

            foreach (var entry in index.AllEntries())
            {
                var path = entry.Key.Replace(Path.DirectorySeparatorChar, '/');
                var at = path.IndexOf(LEVELS, StringComparison.OrdinalIgnoreCase);
                if (at < 0) { continue; }

                var name = path.Substring(at + LEVELS.Length);

                //Only the file itself. Anything with a further slash is inside a folder that
                //happens to sit under levels/ and is not a mission.
                if (name.Length == 0 || name.Contains('/')) { continue; }

                //One leading slash, not two: the enumerator joins the mount point onto a key that
                //already begins with one, and GetFile returns nothing for the doubled spelling
                //without complaining about it.
                found[name] = new Mission(name, "/" + path.TrimStart('/'), entry.Value.UncompressedSize);
            }

            Notes.Add($"{found.Count:N0} missions in the index");

            return found.Values
                .OrderBy(one => one.Label, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        /// <summary>The bytes of one file in the game's paks, or nothing.</summary>
        public static byte[]? read(string pakPath)
        {
            var index = CustomSkins.index;
            if (index == null) { return null; }

            var got = index.GetFile(pakPath);
            if (got != null) { return got.Value.ToArray(); }

            //Case. The index is built case sensitive, and a level file does not spell its own
            //references the way the paks spell them: Creeper Woods asks for
            //"creeperwoods/objectgroup" and the pak holds "CreeperWoods/objectgroup", and asks
            //for the resource pack "CreeperWoods" where the pak holds "creeperwoods". Four of
            //that mission's sixteen object groups and its whole block palette went missing this
            //way, silently, because GetFile returns nothing rather than complaining.
            var actual = resolve(pakPath);
            if (actual == null) { return null; }

            return index.GetFile(actual)?.ToArray();
        }

        /// <summary>
        /// The pak's own spelling of a path, found without regard to case.
        ///
        /// Only the data folder is indexed. That is where every path this tab follows lives, and
        /// it keeps the map to a few thousand entries rather than the eighty thousand in the
        /// index as a whole.
        /// </summary>
        private static Dictionary<string, string>? _dataPaths;

        public static string? resolve(string pakPath)
        {
            var index = CustomSkins.index;
            if (index == null) { return null; }

            if (_dataPaths == null)
            {
                _dataPaths = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

                foreach (var entry in index)
                {
                    var path = entry.Replace(Path.DirectorySeparatorChar, '/');
                    if (path.IndexOf("/data/", StringComparison.OrdinalIgnoreCase) < 0) { continue; }

                    //One leading slash: the enumerator joins a mount point onto a key that
                    //already begins with one.
                    _dataPaths["/" + path.TrimStart('/')] = "/" + path.TrimStart('/');
                }
            }

            return _dataPaths.TryGetValue(pakPath, out var found) ? found : null;
        }

        /// <summary>Forgets the path map, for when the paks are reloaded.</summary>
        public static void forget() => _dataPaths = null;

        /// <summary>
        /// The object groups and resource packs a level names.
        ///
        /// Read rather than guessed: a mission draws its tiles from one or more object groups and
        /// its look from one or more resource packs, and which ones is written in the level file.
        /// Exporting a mission without them gives somebody a list of tile names and no tiles.
        /// </summary>
        public static (List<string> groups, List<string> packs) referencedBy(byte[] levelJson)
        {
            var groups = new List<string>();
            var packs = new List<string>();

            try
            {
                var text = Encoding.UTF8.GetString(levelJson);
                using var document = System.Text.Json.JsonDocument.Parse(
                    stripComments(text),
                    new System.Text.Json.JsonDocumentOptions
                    {
                        AllowTrailingCommas = true,
                        CommentHandling = System.Text.Json.JsonCommentHandling.Skip,
                    });

                void collect(string property, List<string> into)
                {
                    if (!document.RootElement.TryGetProperty(property, out var array)) { return; }
                    if (array.ValueKind != System.Text.Json.JsonValueKind.Array) { return; }

                    foreach (var one in array.EnumerateArray())
                    {
                        var value = one.GetString();
                        if (!string.IsNullOrWhiteSpace(value)) { into.Add(value!); }
                    }
                }

                collect("object-groups", groups);
                collect("resource-packs", packs);
            }
            catch (Exception problem)
            {
                Notes.Add($"that level file could not be read: {problem.Message}");
            }

            return (groups, packs);
        }

        /// <summary>
        /// The game's data files are JSON with // comments in places.
        ///
        /// Quotes are tracked, because a path inside a string can contain two slashes and losing
        /// the rest of that line would quietly change the file.
        /// </summary>
        public static string stripComments(string text)
        {
            var made = new StringBuilder(text.Length);
            var inString = false;

            for (var i = 0; i < text.Length; i++)
            {
                var c = text[i];

                if (inString)
                {
                    made.Append(c);
                    if (c == '\\' && i + 1 < text.Length) { made.Append(text[++i]); continue; }
                    if (c == '"') { inString = false; }
                    continue;
                }

                if (c == '"') { inString = true; made.Append(c); continue; }

                if (c == '/' && i + 1 < text.Length && text[i + 1] == '/')
                {
                    var end = text.IndexOf('\n', i);
                    if (end < 0) { break; }
                    i = end - 1;
                    continue;
                }

                made.Append(c);
            }

            return made.ToString();
        }
    }
}
