using MCDSaveEdit.Services;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Windows.Media.Imaging;
#nullable enable

namespace MCDSaveEdit.Logic
{
    /// <summary>
    /// New item properties: MCDR_Prop01 and on, each a copy of one of the game's armour properties
    /// under its own id, name, line and icon. What gem sockets are made of - an item's property
    /// lines are listed under its name, apart from its enchantments, which is where a socket and
    /// the gem in it belong.
    ///
    /// The game keeps its properties in a table it indexes by EArmorPropertyID, built once at
    /// start-up, like its enchantments. The plugin (ItemPlugin.cpp, addProperties) copies the
    /// source's entry to a new id past the game's last, gives it the design's texts and icon, and
    /// names it in EArmorPropertyID so a save keeps it.
    ///
    /// A property on a weapon is kept by the game and shown, but does nothing: only the armour
    /// slot's properties are turned into effects. So a copy does what its source does on armour
    /// and nothing on a weapon.
    /// </summary>
    public static class CustomProperties
    {
        public const string PREFIX = "MCDR_Prop";

        public sealed class Design
        {
            public string Id { get; set; } = "";
            /// <summary>The property it copies, as saves spell it: MeleeAttackSpeedBoost.</summary>
            public string Source { get; set; } = "";
            /// <summary>Its name. Null keeps the source's.</summary>
            public string? Name { get; set; }
            /// <summary>Its line under the item's name - "{0} melee attack speed". Null keeps the source's.</summary>
            public string? Line { get; set; }
            /// <summary>Its own icon: a PNG in the app's folder, painted over the copied icon. Null keeps the source's.</summary>
            public string? IconFile { get; set; }
            /// <summary>
            /// Whether it does what its source does. A gem does; an Empty Socket is only a line, and
            /// the plugin keeps it out of the effects the game builds, on armour as on weapons.
            /// </summary>
            public bool Active { get; set; }
            /// <summary>
            /// Given a class of its own - its source's folder copied under its id - whose one number
            /// the plugin sets, once the game has loaded it, to Neutral + (the source's - Neutral) *
            /// Factor. A gem's grade. Null: the source's class, as it is.
            /// </summary>
            public double? Factor { get; set; }
            /// <summary>What the number is when the property does nothing: 1 for a multiplier, 0 for an amount.</summary>
            public double Neutral { get; set; } = 1;
        }

        /// <summary>
        /// The class a design with its own class uses, as the engine names it:
        /// /Game/Components/ArmorProperties/MCDR_SocketRuby1/BP_MCDR_SocketRuby1.BP_MCDR_SocketRuby1_C - the
        /// source's folder and blueprint, copied and renamed to the id. Null when the source has no folder.
        /// </summary>
        public static string? classPath(Design d)
        {
            var folder = sourceFolder(d.Source);
            if (folder == null) { return null; }
            var inside = folder.Substring(folder.IndexOf("/Dungeons/Content/", StringComparison.OrdinalIgnoreCase) + "/Dungeons/Content/".Length);
            var parent = inside.Substring(0, inside.Length - d.Source.Length);
            return $"/Game/{parent}{d.Id}/BP_{d.Id}.BP_{d.Id}_C";
        }

        private static string file => Path.Combine(CustomItems.folder, "custom-properties.json");

        public static List<Design> load()
        {
            try
            {
                return File.Exists(file)
                    ? JsonSerializer.Deserialize<List<Design>>(File.ReadAllText(file)) ?? new List<Design>()
                    : new List<Design>();
            }
            catch (Exception) { return new List<Design>(); }
        }

        public static void save(IReadOnlyList<Design> designs)
            => File.WriteAllText(file, JsonSerializer.Serialize(designs, new JsonSerializerOptions { WriteIndented = true }));

        /// <summary>One past the highest id ever handed out, so a deleted one's id is never reused.</summary>
        public static string newId(IEnumerable<Design> designs)
        {
            var highest = designs.Select(d => d.Id)
                .Where(id => id.StartsWith(PREFIX, StringComparison.OrdinalIgnoreCase))
                .Select(id => int.TryParse(id.Substring(PREFIX.Length), out var n) ? n : 0)
                .DefaultIfEmpty(0).Max();
            return PREFIX + (highest + 1).ToString("00", System.Globalization.CultureInfo.InvariantCulture);
        }

        public static bool hasIcon(Design d) => d.IconFile != null && File.Exists(d.IconFile);

        private const string FOLDER = "/Components/ArmorProperties/";

        /// <summary>
        /// Where a property's blueprint and icons live, cooked-spelled -
        /// /Dungeons/Content/Components/ArmorProperties/MeleeAttackSpeedBoost - or null.
        /// </summary>
        public static string? sourceFolder(string source)
        {
            var index = CustomSkins.index;
            if (index == null) { return null; }
            var wanted = FOLDER + source + "/";
            foreach (var entry in index.AllEntries())
            {
                var at = entry.Key.IndexOf(wanted, StringComparison.OrdinalIgnoreCase);
                if (at < 0) { continue; }
                var folder = entry.Key.Substring(0, at + wanted.Length - 1);
                var root = folder.LastIndexOf("/Dungeons/Content/", StringComparison.OrdinalIgnoreCase);
                return root < 0 ? folder : folder.Substring(root);
            }
            return null;
        }

        /// <summary>
        /// The copy's icon texture and material as the engine names the objects -
        /// "/Game/Components/ArmorProperties/MCDR_Prop01/T_MCDR_Prop01_Icon.T_MCDR_Prop01_Icon" -
        /// found in the source's folder and renamed as the folder copy renames. Null if either is missing.
        /// </summary>
        public static (string texture, string material)? iconObjects(Design d)
        {
            var folder = sourceFolder(d.Source);
            var index = CustomSkins.index;
            if (folder == null || index == null) { return null; }
            var inside = folder.Substring(folder.IndexOf("/Dungeons/Content/", StringComparison.OrdinalIgnoreCase) + "/Dungeons/Content/".Length);
            var gameFolder = "/Game/" + inside.Substring(0, inside.Length - d.Source.Length) + d.Id;
            string? texture = null, material = null;
            var wanted = inside + "/";
            foreach (var entry in index.AllEntries())
            {
                var at = entry.Key.IndexOf(wanted, StringComparison.OrdinalIgnoreCase);
                if (at < 0) { continue; }
                var name = entry.Key.Substring(at + wanted.Length);
                if (name.Contains('/')) { continue; }
                //Most properties have no icon of their own - the line shows a diamond in the
                //rarity's colour. The DLC's do: T_<Name>_Icon, its shine beside it, and a material.
                if (name.StartsWith("T_", StringComparison.OrdinalIgnoreCase) && name.EndsWith("_Icon", StringComparison.OrdinalIgnoreCase)
                    && name.IndexOf("Shine", StringComparison.OrdinalIgnoreCase) < 0) { texture = name; }
                if (name.StartsWith("MI_", StringComparison.OrdinalIgnoreCase)) { material = name; }
            }
            if (texture == null || material == null) { return null; }
            string renamed(string name)
            {
                var i = name.IndexOf(d.Source, StringComparison.OrdinalIgnoreCase);
                var now = i < 0 ? name : name.Substring(0, i) + d.Id + name.Substring(i + d.Source.Length);
                return $"{gameFolder}/{now}.{now}";
            }
            return (renamed(texture), renamed(material));
        }

        /// <summary>
        /// The source's folder copied under the new id with the design's picture painted over its
        /// icon, for the New Items pak - only designs with an icon of their own need one.
        /// </summary>
        public static (List<PakWriter.Entry> entries, NewContent.Made made) files(Design d, List<string> notes)
        {
            var folder = sourceFolder(d.Source)
                ?? throw new InvalidOperationException($"{d.Source} has no folder in the game's files to copy for {d.Id}.");
            var made = NewContent.cloneFolder(folder, d.Id)
                ?? throw new InvalidOperationException($"{d.Source}'s folder could not be copied for {d.Id}.");
            var entries = made.Entries.Select(e => new PakWriter.Entry(e.Path, (byte[])e.Data.Clone())).ToList();
            if (!hasIcon(d)) { notes.Add($"{d.Id}: a copy of {d.Source} for a class of its own."); return (entries, made); }

            var objects = iconObjects(d);
            if (objects == null) { notes.Add($"{d.Id}: {d.Source} has no icon to paint; it shows the source's."); return (entries, made); }
            var name = objects.Value.texture.Substring(objects.Value.texture.LastIndexOf('.') + 1);
            int find(string extension) => entries.FindIndex(e => e.Path.EndsWith("/" + name + extension, StringComparison.OrdinalIgnoreCase));
            int asset = find(".uasset"), data = find(".uexp"), bulk = find(".ubulk");
            if (asset < 0 || data < 0) { notes.Add($"{d.Id}: the copy has no {name}; it shows the source's icon."); return (entries, made); }
            var picture = CustomSkins.imageFromPng(File.ReadAllBytes(d.IconFile!));
            //Read from the source's own texture: the copy's header is renamed, and a shorter id
            //leaves its bulk offsets unreadable here (the game reads them fine).
            string? paint(string copyName, BitmapSource what, int copyAsset, int copyData, int copyBulk, out byte[] uexp, out byte[]? ubulk)
            {
                uexp = entries[copyData].Data; ubulk = copyBulk < 0 ? null : entries[copyBulk].Data;
                var original = CustomSkins.index?.extractPackage(folder + "/" + copyName.Replace(d.Id, d.Source));
                if (original == null) { return "the source's texture could not be read"; }
                return CustomItems.repaintInto(original.Value.UAsset.ToArray(), original.Value.UExp.ToArray(), original.Value.UBulk?.ToArray(),
                    entries[copyData].Data, copyBulk < 0 ? null : entries[copyBulk].Data, what, out uexp, out ubulk);
            }
            var why = paint(name, picture, asset, data, bulk, out var uexp, out var ubulk);
            if (why != null) { notes.Add($"{d.Id}: its icon was not painted - {why}."); return (entries, made); }
            entries[data] = new PakWriter.Entry(entries[data].Path, uexp);
            if (bulk >= 0 && ubulk != null) { entries[bulk] = new PakWriter.Entry(entries[bulk].Path, ubulk); }
            notes.Add($"{d.Id}: a copy of {d.Source} with its own icon.");

            //The sheen's masks beside it, made from the picture as an enchantment's are.
            var shineAsset = entries.FindIndex(e => e.Path.EndsWith("Shine_Icon.uasset", StringComparison.OrdinalIgnoreCase)
                && Path.GetFileName(e.Path).StartsWith("T_", StringComparison.OrdinalIgnoreCase));
            if (shineAsset < 0) { return (entries, made); }
            var stem = entries[shineAsset].Path.Substring(0, entries[shineAsset].Path.Length - ".uasset".Length);
            int shineFind(string extension) => entries.FindIndex(e => e.Path.Equals(stem + extension, StringComparison.OrdinalIgnoreCase));
            int shineData = shineFind(".uexp"), shineBulk = shineFind(".ubulk");
            if (shineData < 0) { return (entries, made); }
            why = paint(Path.GetFileName(stem), EnchantmentShine.from(picture), shineAsset, shineData, shineBulk, out var shineUexp, out var shineUbulk);
            if (why != null) { notes.Add($"{d.Id}: its shine was not made - {why}."); return (entries, made); }
            entries[shineData] = new PakWriter.Entry(entries[shineData].Path, shineUexp);
            if (shineBulk >= 0 && shineUbulk != null) { entries[shineBulk] = new PakWriter.Entry(entries[shineBulk].Path, shineUbulk); }
            return (entries, made);
        }

        /// <summary>
        /// What the plugin is told: "@property" lines. Sockets and gems first when gems are on, so
        /// their numbers (41 on) never move; then the user's own, in the order they were made.
        /// </summary>
        public static IReadOnlyList<string> forPlugin(Func<string, string> clean)
            => (Gems.isOn ? Gems.properties() : Array.Empty<Design>()).Concat(load())
                .Where(d => GearTraits.ARMOR_PROPERTY_IDS.ContainsKey(d.Source))
                .Select(d => string.Join("\t", "@property", d.Id,
                    GearTraits.ARMOR_PROPERTY_IDS[d.Source].ToString(System.Globalization.CultureInfo.InvariantCulture),
                    clean(d.Name ?? "-"), clean(d.Line ?? "-"),
                    hasIcon(d) && iconObjects(d) is { } icon ? icon.texture + "|" + icon.material : "-",
                    d.Active ? "1" : "0",
                    d.Factor is { } factor && classPath(d) is { } path ? path : "-",
                    d.Factor is { } scale ? string.Format(System.Globalization.CultureInfo.InvariantCulture, "{0}|{1}", scale, d.Neutral) : "-"))
                .ToList();
    }
}
