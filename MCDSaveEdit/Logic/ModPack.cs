using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
#nullable enable

namespace MCDSaveEdit.Logic
{
    /// <summary>
    /// Everything this app made, in one file another player imports in one go.
    ///
    /// The paks alone are not enough. A custom item, enchantment or mob is a line the plugin reads
    /// plus files in a pak built from designs, and a map that spawns a custom mob depends on that
    /// mob being defined - so a map sent without its mobs loads with nothing where they stand.
    /// This sends the designs too, and the importer rebuilds from them, which is what makes the
    /// ids line up on the other machine.
    ///
    /// It is still a plain zip with the paks at the top, as <see cref="ModArchive"/> writes one, so
    /// an older build or somebody unzipping by hand still gets the paks. Beside them, under
    /// <see cref="FOLDER"/>, a manifest and the design files with the pictures and models they
    /// point at. Those paths are absolute on the sender's machine; they go in as "{pack}/name"
    /// and come out pointing into the receiver's own folder.
    ///
    /// Left out: the built New Items pak, which the importer builds again from the designs, and
    /// the Camp's map table and its loader, which <see cref="MapSlots.sync"/> writes for
    /// whatever slots end up installed. Both would be the SENDER's and wrong on arrival.
    /// </summary>
    public static class ModPack
    {
        public const string FOLDER = "MCDReborn/";
        private const string MANIFEST = FOLDER + "pack.json";
        private const string CONTENT = FOLDER + "content/";
        private const string PLACEHOLDER = "{pack}/";
        private static readonly string[] DESIGNS = { "custom-items.json", "custom-enchantments.json", "custom-mobs.json" };
        private static readonly Regex SLOT_PAK = new Regex(@"^MCDReborn_Slot(\d+)_", RegexOptions.IgnoreCase);

        /// <summary>Raised after an import has replaced the designs, so a tab holding them reloads.</summary>
        public static event Action? DesignsReplaced;

        public sealed class Manifest
        {
            public int Version { get; set; } = 1;
            public string? App { get; set; }
            /// <summary>Paks that live beside the game's own rather than in ~mods, by file name.</summary>
            public List<string> AppPaks { get; set; } = new();
            public int Paks { get; set; }
            public bool HasDesigns { get; set; }
            public int Items { get; set; }
            public int Enchantments { get; set; }
            public int Mobs { get; set; }
        }

        public sealed class Result
        {
            public ModArchive.Haul Haul { get; } = new ModArchive.Haul();
            public Manifest? Pack { get; set; }
            public bool DesignsTaken { get; set; }
            public string? Backup { get; set; }
            public List<string> Notes { get; } = new();
        }

        // ------------------------------------------------------------------ export

        /// <summary>
        /// Every mod and every design, into one zip. Returns the manifest written, for saying
        /// what went in.
        /// </summary>
        public static Manifest export(string targetPath)
        {
            var built = CustomSkins.MOD_PREFIX + CustomItems.MOD_NAME + CustomSkins.MOD_SUFFIX;
            var mods = CustomSkins.installed()
                .Where(mod => !mod.Internal)
                .Where(mod => !isRebuilt(Path.GetFileName(mod.Path), built))
                .ToList();

            var manifest = new Manifest { App = Data.Constants.CURRENT_VERSION_NUMBER };
            var designs = DESIGNS.ToDictionary(name => name, name => readDesigns(Path.Combine(CustomItems.folder, name)));
            manifest.Items = designs[DESIGNS[0]]?.Count ?? 0;
            manifest.Enchantments = designs[DESIGNS[1]]?.Count ?? 0;
            manifest.Mobs = designs[DESIGNS[2]]?.Count ?? 0;
            manifest.HasDesigns = manifest.Items + manifest.Enchantments + manifest.Mobs > 0;

            if (mods.Count == 0 && !manifest.HasDesigns) { throw new InvalidOperationException("There are no mods to export."); }

            var working = targetPath + ".part";
            if (File.Exists(working)) { File.Delete(working); }

            try
            {
                using (var stream = File.Create(working))
                using (var zip = new ZipArchive(stream, ZipArchiveMode.Create))
                {
                    var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    foreach (var mod in mods)
                    {
                        var name = Path.GetFileName(mod.Path);
                        if (!used.Add(name)) { continue; }
                        zip.CreateEntryFromFile(mod.Path, name, CompressionLevel.Optimal);
                        manifest.Paks++;
                        if (!mod.Manual) { manifest.AppPaks.Add(name); }
                    }

                    if (manifest.HasDesigns)
                    {
                        //Each file a design points at goes in once, under its own name - or a
                        //numbered one if two different files share a name.
                        var packed = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                        string? pack(string path)
                        {
                            if (packed.TryGetValue(path, out var known)) { return known; }
                            var name = Path.GetFileName(path);
                            for (var n = 2; !names.Add(name); n++) { name = $"{n}_{Path.GetFileName(path)}"; }
                            zip.CreateEntryFromFile(path, CONTENT + name, CompressionLevel.Optimal);
                            packed[path] = name;
                            return name;
                        }

                        foreach (var (name, list) in designs)
                        {
                            var node = list ?? new JsonArray();
                            rewrite(node, text =>
                            {
                                if (!Path.IsPathRooted(text) || !File.Exists(text)) { return null; }
                                return PLACEHOLDER + pack(text);
                            });
                            write(zip, FOLDER + name, node.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
                        }
                    }

                    write(zip, MANIFEST, JsonSerializer.Serialize(manifest, new JsonSerializerOptions { WriteIndented = true }));
                }

                if (File.Exists(targetPath)) { File.Delete(targetPath); }
                File.Move(working, targetPath);
            }
            catch (Exception)
            {
                try { if (File.Exists(working)) { File.Delete(working); } } catch (IOException) { }
                throw;
            }

            return manifest;
        }

        /// <summary>The paks the receiver makes for itself: the New Items pak, the Camp table, its loader.</summary>
        private static bool isRebuilt(string file, string built)
            => string.Equals(file, built, StringComparison.OrdinalIgnoreCase)
                || file.StartsWith(CustomSkins.MOD_PREFIX + "Table", StringComparison.OrdinalIgnoreCase)
                || file.StartsWith(CustomSkins.MOD_PREFIX + "Loader", StringComparison.OrdinalIgnoreCase);

        // ------------------------------------------------------------------ import

        /// <summary>
        /// Opens a pack. Null when the file is not one - a zip of paks from anywhere else, or a
        /// rar - which the caller hands to <see cref="ModArchive.install"/> as before.
        /// </summary>
        public static Manifest? read(string archivePath)
        {
            try
            {
                using var zip = ZipFile.OpenRead(archivePath);
                var entry = zip.GetEntry(MANIFEST);
                if (entry == null) { return null; }
                using var reader = new StreamReader(entry.Open());
                return JsonSerializer.Deserialize<Manifest>(reader.ReadToEnd());
            }
            catch (InvalidDataException) { return null; }
            catch (JsonException) { return null; }
        }

        /// <summary>Whether there are designs here that a pack's designs would replace.</summary>
        public static bool hasOwnDesigns()
            => DESIGNS.Any(name => (readDesigns(Path.Combine(CustomItems.folder, name))?.Count ?? 0) > 0);

        /// <summary>
        /// Installs a pack: its designs in place of these (kept in a backup folder first), the files
        /// they point at, its paks, and then the New Items pak and the plugin built from the lot.
        ///
        /// <paramref name="replacePak"/> is asked about a pak already installed under the same
        /// name; <paramref name="replaceSlot"/> about a map slot already holding a different map,
        /// with the slot number, what is there and what would go in.
        /// </summary>
        public static Result install(string archivePath, Manifest manifest,
            Func<string, bool> replacePak, Func<int, string, string, bool> replaceSlot)
        {
            var result = new Result { Pack = manifest };
            var paks = CustomSkins.paksFolder ?? throw new InvalidOperationException("The game's paks folder is not known.");
            var appPaks = new HashSet<string>(manifest.AppPaks, StringComparer.OrdinalIgnoreCase);
            var slotsChanged = false;

            var unpacked = Path.Combine(Path.GetTempPath(), "MCDReborn_pack_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(unpacked);
            try
            {
                using var zip = ZipFile.OpenRead(archivePath);

                if (manifest.HasDesigns)
                {
                    result.Backup = backUp();
                    var folder = CustomItems.folder;
                    foreach (var entry in zip.Entries.Where(e => e.FullName.StartsWith(CONTENT, StringComparison.Ordinal)))
                    {
                        //Its own name only, so nothing in a pack writes outside the folder.
                        var name = Path.GetFileName(entry.FullName);
                        if (name.Length == 0) { continue; }
                        entry.ExtractToFile(Path.Combine(folder, name), overwrite: true);
                    }
                    foreach (var name in DESIGNS)
                    {
                        var entry = zip.GetEntry(FOLDER + name);
                        JsonNode node = new JsonArray();
                        if (entry != null)
                        {
                            using var reader = new StreamReader(entry.Open());
                            node = JsonNode.Parse(reader.ReadToEnd()) ?? new JsonArray();
                        }
                        rewrite(node, text => text.StartsWith(PLACEHOLDER, StringComparison.Ordinal)
                            ? Path.Combine(folder, Path.GetFileName(text.Substring(PLACEHOLDER.Length)))
                            : null);
                        File.WriteAllText(Path.Combine(folder, name), node.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
                    }
                    result.DesignsTaken = true;
                }

                foreach (var entry in zip.Entries)
                {
                    var name = Path.GetFileName(entry.FullName.Replace('\\', '/'));
                    if (entry.FullName.StartsWith(FOLDER, StringComparison.Ordinal)) { continue; }
                    if (!name.EndsWith(".pak", StringComparison.OrdinalIgnoreCase))
                    {
                        if (name.Length > 0) { result.Haul.Ignored++; }
                        continue;
                    }

                    var temp = Path.Combine(unpacked, name);
                    if (File.Exists(temp)) { result.Haul.Skipped.Add(name); continue; }
                    entry.ExtractToFile(temp);
                    if (!CustomSkins.looksLikePak(temp, out _)) { result.Haul.Rejected.Add(name); continue; }

                    //A map slot is one address in the game, whatever the pak is called: a
                    //different map already in that slot has to go, or two paks offer one level.
                    var slot = SLOT_PAK.Match(name);
                    if (slot.Success && int.TryParse(slot.Groups[1].Value, out var number))
                    {
                        var there = MapSlots.inSlot(number);
                        if (there != null && !string.Equals(Path.GetFileName(there.Path), name, StringComparison.OrdinalIgnoreCase))
                        {
                            if (!replaceSlot(number, there.Name, name)) { result.Haul.Skipped.Add(name); continue; }
                            File.Delete(there.Path);
                        }
                        slotsChanged = true;
                    }

                    if (appPaks.Contains(name)) { takeApp(temp, name, paks, replacePak, result.Haul); }
                    else { take(temp, name, replacePak, result.Haul); }
                }
            }
            finally
            {
                try { Directory.Delete(unpacked, recursive: true); } catch (IOException) { }
            }

            if (result.DesignsTaken)
            {
                try
                {
                    var built = CustomItems.build(CustomItems.load());
                    result.Notes.AddRange(built.Notes);
                }
                catch (Exception problem)
                {
                    //The designs are in and can be installed again from New Items; what failed
                    //was only building them, which is said rather than undone.
                    result.Notes.Add(problem.Message);
                }
                CustomItems.showInApp();
                CustomEnchantments.showInApp();
                GamePlugin.registerEnchantments();
                DesignsReplaced?.Invoke();
            }

            if (slotsChanged) { MapSlots.sync(); }
            return result;
        }

        /// <summary>A pak this app made goes back beside the game's own, where the app looks for it.</summary>
        private static void takeApp(string temp, string name, string paks, Func<string, bool> replace, ModArchive.Haul haul)
        {
            var target = Path.Combine(paks, name);
            var existed = File.Exists(target);
            if (existed && !replace(name)) { haul.Skipped.Add(name); return; }
            try
            {
                File.Copy(temp, target, overwrite: true);
                (existed ? haul.Replaced : haul.Installed).Add(name);
            }
            catch (Exception) { haul.Skipped.Add(name); }
        }

        private static void take(string temp, string name, Func<string, bool> replace, ModArchive.Haul haul)
        {
            try
            {
                CustomSkins.installPak(temp);
                haul.Installed.Add(name);
            }
            catch (IOException)
            {
                if (!replace(name)) { haul.Skipped.Add(name); return; }
                try
                {
                    CustomSkins.installPak(temp, overwrite: true);
                    haul.Replaced.Add(name);
                }
                catch (Exception) { haul.Skipped.Add(name); }
            }
            catch (InvalidOperationException) { haul.Rejected.Add(name); }
        }

        /// <summary>
        /// This machine's designs and everything beside them, copied into a dated folder before a
        /// pack replaces them. Returns the folder, or null when there was nothing to keep.
        /// </summary>
        private static string? backUp()
        {
            var folder = CustomItems.folder;
            var files = Directory.Exists(folder)
                ? Directory.GetFiles(folder).Where(f => !Path.GetFileName(f).Contains(".before-", StringComparison.OrdinalIgnoreCase)).ToList()
                : new List<string>();
            if (files.Count == 0) { return null; }

            var into = Path.Combine(folder, "before-pack-" + DateTime.Now.ToString("yyyy-MM-dd-HHmmss"));
            Directory.CreateDirectory(into);
            foreach (var file in files) { File.Copy(file, Path.Combine(into, Path.GetFileName(file)), overwrite: true); }
            return into;
        }

        // ------------------------------------------------------------------ json

        private static JsonArray? readDesigns(string path)
        {
            try { return File.Exists(path) ? JsonNode.Parse(File.ReadAllText(path)) as JsonArray : null; }
            catch (Exception) { return null; }
        }

        /// <summary>Every string in the tree through <paramref name="change"/>; null leaves it as it is.</summary>
        private static void rewrite(JsonNode node, Func<string, string?> change)
        {
            switch (node)
            {
                case JsonObject obj:
                    foreach (var key in obj.Select(p => p.Key).ToList())
                    {
                        var child = obj[key];
                        if (child == null) { continue; }
                        if (child is JsonValue value && value.TryGetValue<string>(out var text))
                        {
                            if (change(text) is { } changed) { obj[key] = changed; }
                        }
                        else { rewrite(child, change); }
                    }
                    break;
                case JsonArray array:
                    for (var i = 0; i < array.Count; i++)
                    {
                        var child = array[i];
                        if (child == null) { continue; }
                        if (child is JsonValue value && value.TryGetValue<string>(out var text))
                        {
                            if (change(text) is { } changed) { array[i] = changed; }
                        }
                        else { rewrite(child, change); }
                    }
                    break;
            }
        }

        private static void write(ZipArchive zip, string name, string text)
        {
            using var writer = new StreamWriter(zip.CreateEntry(name, CompressionLevel.Optimal).Open());
            writer.Write(text);
        }
    }
}
