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
            /// <summary>
            /// Its own icon: a PNG kept in the app's folder, painted over the copied icon texture.
            /// Null keeps the source's.
            /// </summary>
            public string? IconFile { get; set; }
        }

        public static Design copy(Design d) => new Design
        {
            Id = d.Id, Source = d.Source, Name = d.Name, Description = d.Description, BuiltIn = d.BuiltIn, Effect = d.Effect,
            Numbers = new Dictionary<string, double>(d.Numbers ?? new Dictionary<string, double>()),
            IconFile = d.IconFile,
        };

        // ------------------------------------------------------------------ its own blueprint

        /// <summary>Whether it needs a copy of the source's folder: for numbers or an icon of its own.</summary>
        public static bool hasBlueprint(Design d) => (d.Numbers != null && d.Numbers.Count > 0) || hasIcon(d);

        public static bool hasIcon(Design d) => d.IconFile != null && File.Exists(d.IconFile);

        /// <summary>
        /// The copy's icon texture and icon material, as the engine names the objects -
        /// "/Game/Components/Enchantments/MCDR_Ench04/T_MCDR_Ench04_Icon.T_MCDR_Ench04_Icon" - found
        /// in the source's folder (T_..._Icon and MI_..._Icon, not the Shine mask) and renamed as
        /// NewContent.cloneFolder renames. Null when the source has either missing.
        /// </summary>
        public static (string texture, string material)? iconObjects(Design d)
        {
            var folder = sourceFolder(d.Source);
            var index = CustomSkins.index;
            if (folder == null || index == null) { return null; }
            var old = folder.Substring(folder.LastIndexOf('/') + 1);
            var inside = folder.Substring(folder.IndexOf("/Dungeons/Content/", StringComparison.OrdinalIgnoreCase) + "/Dungeons/Content/".Length);
            var gameFolder = "/Game/" + inside.Substring(0, inside.Length - old.Length) + d.Id;
            string? texture = null, material = null;
            var wanted = inside + "/";
            foreach (var entry in index.AllEntries())
            {
                var at = entry.Key.IndexOf(wanted, StringComparison.OrdinalIgnoreCase);
                if (at < 0) { continue; }
                var name = entry.Key.Substring(at + wanted.Length);
                if (name.Contains('/') || !name.EndsWith("_Icon", StringComparison.OrdinalIgnoreCase) || name.IndexOf("Shine", StringComparison.OrdinalIgnoreCase) >= 0) { continue; }
                if (name.StartsWith("T_", StringComparison.OrdinalIgnoreCase)) { texture = name; }
                if (name.StartsWith("MI_", StringComparison.OrdinalIgnoreCase)) { material = name; }
            }
            if (texture == null || material == null) { return null; }
            string renamed(string name)
            {
                var i = name.IndexOf(old, StringComparison.OrdinalIgnoreCase);
                var now = i < 0 ? name : name.Substring(0, i) + d.Id + name.Substring(i + old.Length);
                return $"{gameFolder}/{now}.{now}";
            }
            return (renamed(texture), renamed(material));
        }

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
            if (hasIcon(d)) { paintIcon(d, entries, notes); }
            return (entries, made);
        }

        /// <summary>The design's picture over the copy's icon texture, every mip at its own size.</summary>
        private static void paintIcon(Design d, List<PakWriter.Entry> entries, List<string> notes)
        {
            var objects = iconObjects(d);
            if (objects == null) { notes.Add($"{d.Id}: {d.Source} has no icon texture to paint; it shows the source's."); return; }
            var texture = objects.Value.texture;
            var name = texture.Substring(texture.LastIndexOf('.') + 1);
            int find(string extension) => entries.FindIndex(e => e.Path.EndsWith("/" + name + extension, StringComparison.OrdinalIgnoreCase));
            int asset = find(".uasset"), data = find(".uexp"), bulk = find(".ubulk");
            if (asset < 0 || data < 0) { notes.Add($"{d.Id}: the copy has no {name}; it shows the source's icon."); return; }
            var picture = CustomSkins.imageFromPng(File.ReadAllBytes(d.IconFile!));
            var why = CustomItems.repaint(entries[asset].Data, entries[data].Data, bulk < 0 ? null : entries[bulk].Data, picture, out var uexp, out var ubulk);
            if (why != null) { notes.Add($"{d.Id}: its icon was not painted - {why}."); return; }
            entries[data] = new PakWriter.Entry(entries[data].Path, uexp);
            if (bulk >= 0 && ubulk != null) { entries[bulk] = new PakWriter.Entry(entries[bulk].Path, ubulk); }
            notes.Add($"{d.Id}: its own icon.");
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
                    hasBlueprint(d) ? d.Id + "/" + blueprintName(d) : "-",
                    hasIcon(d) && iconObjects(d) is { } icon ? icon.texture + "|" + icon.material : "-"))
                .ToList();

        // ------------------------------------------------------------------ in the app

        /// <summary>
        /// Names the app gives its own enchantments, for the pickers: the design's, else its
        /// source's. Consulted by R.enchantmentName and the rest before the game's text.
        /// </summary>
        public static void showInApp()
        {
            R.enchantmentTextOverrides.Clear();
            _pictures.Clear();
            _saved = load();
            foreach (var design in _saved)
            {
                R.enchantmentTextOverrides[design.Id] = design.Name ?? R.enchantmentName(design.Source);
                R.enchantmentTextOverrides[design.Id + "_desc"] = design.Description ?? R.enchantmentDescription(design.Source);
                R.enchantmentTextOverrides[design.Id + "_effect"] = design.Effect ?? R.enchantmentEffect(design.Source);
            }
        }

        private static List<Design> _saved = new();
        private static readonly Dictionary<string, System.Windows.Media.Imaging.BitmapImage?> _pictures = new(StringComparer.OrdinalIgnoreCase);

        /// <summary>An enchantment's own picture for the app's pickers, or null to show its source's.</summary>
        public static System.Windows.Media.Imaging.BitmapImage? iconForApp(string id)
        {
            if (!isOurs(id)) { return null; }
            if (_pictures.TryGetValue(id, out var known)) { return known; }
            var design = _saved.FirstOrDefault(d => string.Equals(d.Id, id, StringComparison.OrdinalIgnoreCase));
            System.Windows.Media.Imaging.BitmapImage? image = null;
            if (design != null && hasIcon(design))
            {
                try
                {
                    image = new System.Windows.Media.Imaging.BitmapImage();
                    image.BeginInit();
                    image.CacheOption = System.Windows.Media.Imaging.BitmapCacheOption.OnLoad;
                    image.StreamSource = new MemoryStream(File.ReadAllBytes(design.IconFile!));
                    image.EndInit();
                    image.Freeze();
                }
                catch (Exception) { image = null; }
            }
            _pictures[id] = image;
            return image;
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
            var shared = copy(design);
            //A path on this machine means nothing on another; the picture travels beside it.
            shared.IconFile = hasIcon(design) ? "icon.png" : null;
            using (var stream = zip.CreateEntry(SHARE_ENTRY).Open())
            {
                var bytes = JsonSerializer.SerializeToUtf8Bytes(new Shared { Design = shared }, new JsonSerializerOptions { WriteIndented = true });
                stream.Write(bytes, 0, bytes.Length);
            }
            if (shared.IconFile != null)
            {
                using var picture = zip.CreateEntry("icon.png").Open();
                var png = File.ReadAllBytes(design.IconFile!);
                picture.Write(png, 0, png.Length);
            }
        }

        /// <summary>An imported design's picture, out of its file and kept under its new id.</summary>
        public static void unpackIcon(string path, Design design)
        {
            if (design.IconFile == null) { return; }
            design.IconFile = null;
            using var zip = System.IO.Compression.ZipFile.OpenRead(path);
            var entry = zip.GetEntry("icon.png");
            if (entry == null) { return; }
            var kept = Path.Combine(CustomItems.folder, design.Id + ".png");
            using (var from = entry.Open())
            using (var to = File.Create(kept)) { from.CopyTo(to); }
            design.IconFile = kept;
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
