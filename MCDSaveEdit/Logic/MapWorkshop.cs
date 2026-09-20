using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
#nullable enable

namespace MCDSaveEdit.Logic
{
    /// <summary>
    /// Where a mission was put when it was exported.
    ///
    /// Export used to ask for a location while everything afterwards assumed a fixed one under
    /// AppData. Exporting to the Desktop therefore meant editing the copy on the Desktop, pressing
    /// Edit spawns, and being quietly handed a different copy of the same mission - or told there
    /// was no export at all, with the folder right there.
    ///
    /// Export no longer asks. This still exists because a folder can arrive from elsewhere -
    /// Import map takes any folder, including one somebody else made - and from then on every
    /// button that works on that mission has to look in the same place. Nothing is moved and
    /// nothing is copied: this only records an answer that was already given.
    /// </summary>
    public static class MapWorkshop
    {
        /// <summary>The default, and where the record itself lives.</summary>
        public static string root => Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "MCDReborn", "maps");

        private static string where => Path.Combine(root, "where.json");

        /// <summary>
        /// Whether the folder holds a map somebody built, rather than a copy of the game's.
        ///
        /// Re-exporting rebuilds the folder from the game's own paks, which is right when the
        /// folder is a stale copy of a mission and catastrophic when it is an afternoon in
        /// Minecraft. The two look identical from outside, so the difference has to be read out
        /// of the level: a mission straight from the game has many stretches drawing on many
        /// object groups, while anything welded or hand-built is one stretch playing one tile.
        ///
        /// Wrong in the safe direction. A stock mission that has been welded counts as built,
        /// and the worst that costs is a confirmation somebody did not strictly need.
        /// </summary>
        public static bool looksBuilt(string folder)
        {
            try
            {
                var path = Path.Combine(folder, "level.json");
                if (!File.Exists(path)) { path = Path.Combine(folder, "level"); }
                if (!File.Exists(path)) { return false; }

                var level = System.Text.Json.Nodes.JsonNode.Parse(
                    GameMaps.stripComments(File.ReadAllText(path)),
                    documentOptions: new System.Text.Json.JsonDocumentOptions
                    {
                        AllowTrailingCommas = true,
                        CommentHandling = System.Text.Json.JsonCommentHandling.Skip,
                    }) as System.Text.Json.Nodes.JsonObject;

                if (level?["stretches"] is not System.Text.Json.Nodes.JsonArray stretches) { return false; }

                return stretches.Count == 1;
            }
            catch (Exception)
            {
                return false;
            }
        }

        /// <summary>Roughly how much is in a built map, for a warning that means something.</summary>
        public static long weightOf(string folder)
        {
            try
            {
                var groups = Path.Combine(folder, "objectgroups");
                if (!Directory.Exists(groups)) { return 0; }

                long total = 0;
                foreach (var file in Directory.GetFiles(groups, "objectgroup.json",
                    SearchOption.AllDirectories))
                {
                    total += new FileInfo(file).Length;
                }

                return total;
            }
            catch (Exception)
            {
                return 0;
            }
        }

        //Read once. It is a handful of lines and the disk should not be touched on every repaint.
        private static Dictionary<string, string>? _known;

        private static Dictionary<string, string> known()
        {
            if (_known != null) { return _known; }

            _known = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

            try
            {
                if (File.Exists(where)
                    && JsonNode.Parse(File.ReadAllText(where)) is JsonObject parsed)
                {
                    foreach (var one in parsed)
                    {
                        var path = one.Value?.GetValue<string>();
                        if (!string.IsNullOrEmpty(path)) { _known[one.Key] = path!; }
                    }
                }
            }
            catch (Exception)
            {
                //A record that will not parse is a record worth ignoring, not a reason to refuse
                //to open the tab.
            }

            return _known;
        }

        /// <summary>
        /// The folder a mission's files are in.
        ///
        /// The remembered one if it is still there, otherwise the default - which may not exist
        /// either, and <see cref="exported"/> is how a caller asks about that before acting.
        /// </summary>
        public static string folderFor(string mission)
        {
            //Made on the way past. A folder that does not exist is silently ignored by every file
            //dialog and every "is it there" check, which turns a missing folder into a wrong
            //answer somewhere else entirely.
            try { Directory.CreateDirectory(root); } catch (Exception) { }

            if (known().TryGetValue(mission, out var found) && Directory.Exists(found))
            {
                return found;
            }

            return Path.Combine(root, mission);
        }

        /// <summary>Whether that folder holds an export rather than just existing.</summary>
        public static bool exported(string mission)
        {
            var folder = folderFor(mission);
            return File.Exists(Path.Combine(folder, "level.json"))
                || File.Exists(Path.Combine(folder, "level"));
        }

        /// <summary>Records where a mission's files went, so everything else can find them.</summary>
        public static void remember(string mission, string folder)
        {
            if (string.IsNullOrWhiteSpace(folder)) { return; }

            known()[mission] = folder;

            try
            {
                Directory.CreateDirectory(root);

                var made = new JsonObject();
                foreach (var one in known()) { made[one.Key] = JsonValue.Create(one.Value); }

                File.WriteAllText(where,
                    made.ToJsonString(new JsonSerializerOptions { WriteIndented = true }),
                    new UTF8Encoding(false));
            }
            catch (Exception)
            {
                //Failing to write it back costs the memory across restarts and nothing else, so
                //it must not take the export down with it.
            }
        }
    }
}
