using MCDSaveEdit.Services;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;
#nullable enable

namespace MCDSaveEdit.Logic
{
    /// <summary>
    /// New enchantments: MCDR_Ench01 and on, each a copy of an existing enchantment - the 36 the
    /// game never offers included - under its own id, with its own name and texts.
    ///
    /// The game's enchantments are C++ like its items, so no pak adds one. The plugin does
    /// (ItemPlugin.cpp, addEnchantments): it copies the source's definition to a new id past the
    /// game's last, gives it the design's texts, and names it in EEnchantmentTypeID so a save
    /// keeps it. So a copy does what its source does, at its strength, with its icon.
    ///
    /// Written to the plugin's list as "@enchantment" lines by every install
    /// (GamePlugin.install), whatever tab started it: they need no pak.
    /// </summary>
    public static class CustomEnchantments
    {
        public const string PREFIX = "MCDR_Ench";

        /// <summary>One new enchantment. Saved as JSON beside the custom items.</summary>
        public sealed class Design
        {
            public string Id { get; set; } = "";
            /// <summary>The enchantment it is a copy of, as saves spell it: FireAspect.</summary>
            public string Source { get; set; } = "";
            /// <summary>Its name. Null keeps the source's.</summary>
            public string? Name { get; set; }
            /// <summary>What it does, as the tooltip explains it. Null keeps the source's.</summary>
            public string? Description { get; set; }
            /// <summary>The line an item shows when it has it built in. Null keeps the source's.</summary>
            public string? BuiltIn { get; set; }
            /// <summary>The number line under it: "{0} damage per second". Null keeps the source's.</summary>
            public string? Effect { get; set; }
            /// <summary>
            /// Its own numbers, by property (EnchantmentNumbers): written into a copy of the
            /// source's blueprint. Empty keeps the source's blueprint and every number with it.
            /// </summary>
            public Dictionary<string, double> Numbers { get; set; } = new();
        }

        public static Design copy(Design d) => new Design
        {
            Id = d.Id, Source = d.Source, Name = d.Name, Description = d.Description, BuiltIn = d.BuiltIn, Effect = d.Effect,
            Numbers = new Dictionary<string, double>(d.Numbers ?? new Dictionary<string, double>()),
        };

        // ------------------------------------------------------------------ its own blueprint

        /// <summary>Whether it needs a blueprint of its own: only for numbers of its own.</summary>
        public static bool hasBlueprint(Design d) => d.Numbers != null && d.Numbers.Count > 0;

        /// <summary>
        /// The copied blueprint's name: the source's, with its folder's name in it swapped for the
        /// id, as NewContent.cloneFolder renames. BP_HeavyweightEnchantment, in the folder
        /// HeavyweightEnchantment, becomes BP_MCDR_Ench02.
        /// </summary>
        public static string blueprintName(Design d)
        {
            var bp = EnchantmentNumbers.blueprintOf(d.Source);
            var folder = sourceFolder(d.Source);
            var old = folder == null ? d.Source : folder.Substring(folder.LastIndexOf('/') + 1);
            var at = bp.IndexOf(old, StringComparison.OrdinalIgnoreCase);
            return at < 0 ? bp : bp.Substring(0, at) + d.Id + bp.Substring(at + old.Length);
        }

        private static readonly Dictionary<string, string?> _folders = new(StringComparer.OrdinalIgnoreCase);

        /// <summary>
        /// Where an enchantment's blueprint lives, cooked-spelled: usually
        /// /Dungeons/Content/Components/Enchantments/FireAspect, but a DLC's are under its own root
        /// and not always named after the enchantment. Null when the game has no such blueprint.
        /// </summary>
        public static string? sourceFolder(string source)
        {
            if (_folders.TryGetValue(source, out var known)) { return known; }
            var wanted = "/Components/Enchantments/";
            //The index folds .uexp and .ubulk into the .uasset, and its keys carry no extension.
            var file = "/" + EnchantmentNumbers.blueprintOf(source);
            string? found = null;
            var index = CustomSkins.index;
            if (index == null) { return null; }
            foreach (var entry in index.AllEntries())
            {
                var key = entry.Key;
                if (!key.EndsWith(file, StringComparison.OrdinalIgnoreCase) || key.IndexOf(wanted, StringComparison.OrdinalIgnoreCase) < 0) { continue; }
                var folder = key.Substring(0, key.Length - file.Length);
                var root = folder.LastIndexOf("/Dungeons/Content/", StringComparison.OrdinalIgnoreCase);
                found = root < 0 ? folder : folder.Substring(root);
                break;
            }
            _folders[source] = found;
            return found;
        }

        /// <summary>
        /// The copied enchantment folder - Components/Enchantments/&lt;Source&gt; as
        /// Components/Enchantments/&lt;Id&gt;, every package renamed - with the design's numbers
        /// on its blueprint's default object. For the New Items pak, and the copy for its registry.
        /// </summary>
        public static (List<PakWriter.Entry> entries, NewContent.Made made) files(Design d, List<string> notes)
        {
            var folder = sourceFolder(d.Source)
                ?? throw new InvalidOperationException($"{d.Source} has no blueprint in the game's files, so {d.Id} cannot have numbers of its own.");
            var made = NewContent.cloneFolder(folder, d.Id)
                ?? throw new InvalidOperationException($"{d.Source} has no folder of its own to copy, so {d.Id} cannot have numbers of its own.");
            var entries = made.Entries.Select(e => new PakWriter.Entry(e.Path, (byte[])e.Data.Clone())).ToList();

            var bp = blueprintName(d);
            var asset = entries.FindIndex(e => e.Path.EndsWith("/" + bp + ".uasset", StringComparison.OrdinalIgnoreCase));
            var data = entries.FindIndex(e => e.Path.EndsWith("/" + bp + ".uexp", StringComparison.OrdinalIgnoreCase));
            if (asset < 0 || data < 0) { throw new InvalidOperationException($"The copy of {d.Source} has no {bp}."); }

            var known = EnchantmentNumbers.NUMBERS.TryGetValue(d.Source, out var list) ? list : Array.Empty<EnchantmentNumbers.Number>();
            var values = new List<(EnchantmentNumbers.Number, double)>();
            foreach (var pair in d.Numbers)
            {
                var number = known.FirstOrDefault(n => n.Property == pair.Key);
                if (number == null) { notes.Add($"{d.Id}: {d.Source} has no {pair.Key}; left out."); continue; }
                values.Add((number, pair.Value));
            }
            var (uasset, uexp) = EnchantmentBlueprint.withNumbers(entries[asset].Data, entries[data].Data, "Default__" + bp + "_C", values);
            entries[asset] = new PakWriter.Entry(entries[asset].Path, uasset);
            entries[data] = new PakWriter.Entry(entries[data].Path, uexp);
            notes.Add($"{d.Id}: a copy of {d.Source} with {values.Count} number(s) of its own.");
            return (entries, made);
        }

        public static bool isOurs(string? id) => id != null && id.StartsWith(PREFIX, StringComparison.OrdinalIgnoreCase);

        /// <summary>Every enchantment a copy can be made of, by the name saves use.</summary>
        public static IEnumerable<string> sources => GearTraits.ENCHANTMENT_IDS.Keys.Where(k => k != "Unset");

        private static string file => Path.Combine(CustomItems.folder, "custom-enchantments.json");

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

            //One the plugin already registers but that was never designed here - the hand-written
            //test one - is taken in, so the next install does not drop an id a character may hold.
            foreach (var installed in GamePlugin.installedEnchantments())
            {
                if (designs.Any(d => string.Equals(d.Id, installed.Id, StringComparison.OrdinalIgnoreCase))) { continue; }
                var source = GearTraits.ENCHANTMENT_IDS.FirstOrDefault(p => p.Value == installed.SourceType).Key;
                if (source == null) { continue; }
                designs.Add(new Design
                {
                    Id = installed.Id, Source = source,
                    Name = kept(installed.Name), Description = kept(installed.Description),
                    BuiltIn = kept(installed.BuiltIn), Effect = kept(installed.Effect),
                });
            }
            return designs;
        }

        private static string? kept(string text) => text == "-" || text.Length == 0 ? null : text;

        public static void save(IReadOnlyList<Design> designs)
            => File.WriteAllText(file, JsonSerializer.Serialize(designs, new JsonSerializerOptions { WriteIndented = true }));

        /// <summary>
        /// The next id: one past the highest ever handed out. A character may still hold a deleted
        /// one's id, and it should not quietly turn into a different enchantment.
        /// </summary>
        public static string newId(IEnumerable<Design> designs)
        {
            var highest = designs.Select(d => d.Id)
                .Concat(GamePlugin.installedEnchantments().Select(i => i.Id))
                .Where(isOurs)
                .Select(id => int.TryParse(id.Substring(PREFIX.Length), out var n) ? n : 0)
                .DefaultIfEmpty(0).Max();
            highest = Math.Max(highest, lastNumber());
            try { File.WriteAllText(numberFile, (highest + 1).ToString(CultureInfo.InvariantCulture)); }
            catch (IOException) { }
            return PREFIX + (highest + 1).ToString("00", CultureInfo.InvariantCulture);
        }

        private static string numberFile => Path.Combine(CustomItems.folder, "last-enchantment-id.txt");

        private static int lastNumber()
        {
            try { return File.Exists(numberFile) && int.TryParse(File.ReadAllText(numberFile).Trim(), out var n) ? n : 0; }
            catch (IOException) { return 0; }
        }

        /// <summary>What the plugin is told for each saved design, in the order they were made.</summary>
        public static IReadOnlyList<GamePlugin.Enchantment> forPlugin()
            => load()
                .Where(d => GearTraits.ENCHANTMENT_IDS.ContainsKey(d.Source))
                .Select(d => new GamePlugin.Enchantment(d.Id, GearTraits.ENCHANTMENT_IDS[d.Source],
                    d.Name ?? "-", d.Description ?? "-", d.BuiltIn ?? "-", d.Effect ?? "-",
                    hasBlueprint(d) ? d.Id + "/" + blueprintName(d) : "-"))
                .ToList();

        // ------------------------------------------------------------------ in the app

        /// <summary>
        /// Names the app gives its own enchantments, for the pickers: the design's, else its
        /// source's. Consulted by R.enchantmentName and the rest before the game's text.
        /// </summary>
        public static void showInApp()
        {
            R.enchantmentTextOverrides.Clear();
            foreach (var design in load())
            {
                R.enchantmentTextOverrides[design.Id] = design.Name ?? R.enchantmentName(design.Source);
                R.enchantmentTextOverrides[design.Id + "_desc"] = design.Description ?? R.enchantmentDescription(design.Source);
                R.enchantmentTextOverrides[design.Id + "_effect"] = design.Effect ?? R.enchantmentEffect(design.Source);
            }
        }

        // ------------------------------------------------------------------ sharing

        public const string SHARE_ENTRY = "enchantment.json";

        public sealed class Shared
        {
            public int Format { get; set; } = 1;
            public Design Design { get; set; } = new();
        }

        /// <summary>One design as a file somebody else can import: the same zip as an item's, holding enchantment.json.</summary>
        public static void export(Design design, string path)
        {
            using var zip = System.IO.Compression.ZipFile.Open(path, System.IO.Compression.ZipArchiveMode.Create);
            using var stream = zip.CreateEntry(SHARE_ENTRY).Open();
            var bytes = JsonSerializer.SerializeToUtf8Bytes(new Shared { Design = copy(design) }, new JsonSerializerOptions { WriteIndented = true });
            stream.Write(bytes, 0, bytes.Length);
        }

        /// <summary>The enchantment in an exported file, or null when the file holds an item instead.</summary>
        public static Design? readShared(string path)
        {
            using var zip = System.IO.Compression.ZipFile.OpenRead(path);
            var entry = zip.GetEntry(SHARE_ENTRY);
            if (entry == null) { return null; }
            using var stream = entry.Open();
            var shared = JsonSerializer.Deserialize<Shared>(stream) ?? throw new InvalidOperationException("That enchantment file is empty.");
            if (shared.Format != 1) { throw new InvalidOperationException("That enchantment was exported by a newer MCD Reborn."); }
            if (!GearTraits.ENCHANTMENT_IDS.ContainsKey(shared.Design.Source))
            {
                throw new InvalidOperationException($"It is a copy of {shared.Design.Source}, which this game does not have.");
            }
            return shared.Design;
        }
    }
}
