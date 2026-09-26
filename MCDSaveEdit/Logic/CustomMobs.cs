using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;
#nullable enable

namespace MCDSaveEdit.Logic
{
    /// <summary>
    /// New mobs: MCDR_Mob01 and on, each a copy of one of the game's mobs under its own EntityType
    /// and name - summoned by an artifact, or spawned by a map's mob groups under its lowercase id.
    ///
    /// A mob is C++ in this game like an item or an enchantment, so the plugin makes one
    /// (ItemPlugin.cpp: nameMobs, addMobs, nameMobsForLevels, copyMobInfo): a new EntityType with
    /// its source's category flags, named in the enum early, a definition with its name and its
    /// source's base mob, its blueprint in the mob registry, its names for levels and its source's
    /// mob info (tags and numbers), which a level's setup reads.
    ///
    /// Written to the plugin's list as "@mob" lines by every install (GamePlugin.install).
    /// </summary>
    public static class CustomMobs
    {
        public const string PREFIX = "MCDR_Mob";

        /// <summary>One new mob. Saved as JSON beside the custom items.</summary>
        public sealed class Design
        {
            public string Id { get; set; } = "";
            /// <summary>The mob it is a copy of, by EntityType name: ZombieVariant1.</summary>
            public string Source { get; set; } = "";
            /// <summary>What the game calls it. Null keeps a name made from the source's.</summary>
            public string? Name { get; set; }
        }

        public static Design copy(Design d) => new Design { Id = d.Id, Source = d.Source, Name = d.Name };

        public static bool isOurs(string? id) => id != null && id.StartsWith(PREFIX, StringComparison.OrdinalIgnoreCase);

        /// <summary>What a map's mob group calls it: its id in lowercase.</summary>
        public static string levelName(string id) => id.ToLowerInvariant();

        public static GameMobTypes.MobType? sourceOf(Design d)
            => GameMobTypes.ALL.FirstOrDefault(m => string.Equals(m.Name, d.Source, StringComparison.Ordinal));

        /// <summary>"ZombieVariant1" as "Zombie Variant 1": the game's mob names are its enum's.</summary>
        public static string readable(string entityType)
            => Regex.Replace(entityType, "(?<=[a-z])(?=[A-Z0-9])|(?<=[0-9])(?=[A-Z])", " ");

        public static string nameOf(Design d) => string.IsNullOrWhiteSpace(d.Name) ? readable(d.Source) : d.Name!;

        private static string file => Path.Combine(CustomItems.folder, "custom-mobs.json");

        public static List<Design> load()
        {
            List<Design> designs;
            try
            {
                designs = File.Exists(file)
                    ? JsonSerializer.Deserialize<List<Design>>(File.ReadAllText(file)) ?? new List<Design>()
                    : new List<Design>();
            }
            catch (Exception) { designs = new List<Design>(); }

            //Mobs the plugin already registers but that were never designed here - the hand-written
            //test ones - are taken in, so the next install does not drop an id an artifact or a
            //map may name.
            foreach (var installed in GamePlugin.installedMobs())
            {
                if (designs.Any(d => string.Equals(d.Id, installed.Id, StringComparison.OrdinalIgnoreCase))) { continue; }
                var source = GameMobTypes.ALL.FirstOrDefault(m => m.Type == installed.SourceType);
                if (source == null) { continue; }
                designs.Add(new Design { Id = installed.Id, Source = source.Name, Name = installed.Name.Length == 0 ? null : installed.Name });
            }
            return designs;
        }

        public static void save(IReadOnlyList<Design> designs)
            => File.WriteAllText(file, JsonSerializer.Serialize(designs, new JsonSerializerOptions { WriteIndented = true }));

        /// <summary>
        /// The next id: one past the highest ever handed out. An artifact or a map may still name a
        /// deleted one, and it should not quietly turn into a different mob.
        /// </summary>
        public static string newId(IEnumerable<Design> designs)
        {
            var highest = designs.Select(d => d.Id)
                .Concat(GamePlugin.installedMobs().Select(m => m.Id))
                .Where(isOurs)
                .Select(id => int.TryParse(id.Substring(PREFIX.Length), out var n) ? n : 0)
                .DefaultIfEmpty(0).Max();
            highest = Math.Max(highest, lastNumber());
            try { File.WriteAllText(numberFile, (highest + 1).ToString(CultureInfo.InvariantCulture)); }
            catch (IOException) { }
            return PREFIX + (highest + 1).ToString("00", CultureInfo.InvariantCulture);
        }

        private static string numberFile => Path.Combine(CustomItems.folder, "last-mob-id.txt");

        private static int lastNumber()
        {
            try { return File.Exists(numberFile) && int.TryParse(File.ReadAllText(numberFile).Trim(), out var n) ? n : 0; }
            catch (IOException) { return 0; }
        }

        /// <summary>What the plugin is told for each saved design.</summary>
        public static IReadOnlyList<GamePlugin.Mob> forPlugin()
            => load()
                .Select(d => (design: d, source: sourceOf(d)))
                .Where(x => x.source != null)
                .Select(x => new GamePlugin.Mob(x.design.Id, x.source!.Type, nameOf(x.design), x.source.Blueprint))
                .ToList();

        // ------------------------------------------------------------------ sharing

        public const string SHARE_ENTRY = "mob.json";

        public sealed class Shared
        {
            public int Format { get; set; } = 1;
            public Design Design { get; set; } = new();
        }

        /// <summary>One design as a file somebody else can import: the same zip as an item's, holding mob.json.</summary>
        public static void export(Design design, string path)
        {
            using var zip = System.IO.Compression.ZipFile.Open(path, System.IO.Compression.ZipArchiveMode.Create);
            using var stream = zip.CreateEntry(SHARE_ENTRY).Open();
            var bytes = JsonSerializer.SerializeToUtf8Bytes(new Shared { Design = copy(design) }, new JsonSerializerOptions { WriteIndented = true });
            stream.Write(bytes, 0, bytes.Length);
        }

        /// <summary>The mob in an exported file, or null when the file holds something else.</summary>
        public static Design? readShared(string path)
        {
            using var zip = System.IO.Compression.ZipFile.OpenRead(path);
            var entry = zip.GetEntry(SHARE_ENTRY);
            if (entry == null) { return null; }
            using var stream = entry.Open();
            var shared = JsonSerializer.Deserialize<Shared>(stream) ?? throw new InvalidOperationException("That mob file is empty.");
            if (shared.Format != 1) { throw new InvalidOperationException("That mob was exported by a newer MCD Reborn."); }
            if (sourceOf(shared.Design) == null)
            {
                throw new InvalidOperationException($"It is a copy of {shared.Design.Source}, which this game does not have.");
            }
            return shared.Design;
        }
    }
}
