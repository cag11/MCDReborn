using MCDSaveEdit.Services;
using PakReader.Pak;
using PakReader.Parsers.Class;
using PakReader.Parsers.Objects;
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
    /// New items under ids of MCD Reborn's own - MCDR_Item01 and on - as many as the user likes.
    ///
    /// The game's item list is C++: 324 ids, each with its folder, English name, whether it is
    /// unique and what slot it goes in, all compiled in, and an id it does not know is deleted
    /// from a character on load. So a new item is three things, all found by making it crash
    /// first:
    ///
    ///   1. the files, copied from an existing item and renamed into a folder named after the new
    ///      id beside the source's, with the item id inside them pointed at the new one
    ///      (NewContent.cloneFolder);
    ///   2. the game's asset registry, with those files added (RegistryPatch) - the game finds an
    ///      item's blueprint through it, and without the entries the item shows in the inventory
    ///      and crashes the moment it is clicked. Only one pak can own the registry, so every
    ///      custom item is built into one pak together;
    ///   3. the id itself, registered in the game's item list every time it starts by MCD Reborn's
    ///      plugin (GamePlugin, ItemPlugin/), as a copy of the source's entry - which is why the
    ///      type and the unique frame are the source's. The plugin passes the name and
    ///      description with it.
    ///
    /// The first version filled the nine cut ids the game registers but ships nothing for (the
    /// "free slots"). They were dropped: each one fixed a type and a frame, and two kinds of custom
    /// item confused more than they helped. `retiredSlots` is kept so a design made for one can be
    /// recognised and moved to an id of its own (PROBE_MIGRATESLOTS).
    /// </summary>
    public static class CustomItems
    {
        public const string MOD_NAME = "CustomItems";

        public enum Kind { Melee, Ranged, Armor, Artifact }

        /// <summary>Where a custom item lives and what it is: its id, folder and type.</summary>
        public sealed class Slot
        {
            public Slot(string id, string folder, Kind kind, string nativeParent, bool unique, string builtInName, bool ready, bool plugin = false)
            {
                Plugin = plugin;
                Id = id;
                Folder = folder;
                Kind = kind;
                NativeParent = nativeParent;
                Unique = unique;
                BuiltInName = builtInName;
                Ready = ready;
            }

            public string Id { get; }
            /// <summary>Where the game looks for it, under a content root's Actors/Equipment or Actors/Items.</summary>
            public string Folder { get; }
            public Kind Kind { get; }
            /// <summary>The native class the copied Instance must have, so a bow never lands in a sword's slot.</summary>
            public string NativeParent { get; }
            public bool Unique { get; }
            public string BuiltInName { get; }
            /// <summary>Tested in game. The others are listed so it is clear what exists, not offered yet.</summary>
            public bool Ready { get; }
            /// <summary>An id the game never had, registered by the plugin (GamePlugin) rather than a cut one.</summary>
            public bool Plugin { get; }

            public string FolderId => Folder.Substring(Folder.LastIndexOf('/') + 1);
        }

        /// <summary>
        /// The nine cut ids the first version filled, no longer offered. Read from the game's own
        /// registry records: the type byte at +0x89, unique at +0x8C, the folder from the record's
        /// paths - see changelog.md. Kept only to recognise a design made for one.
        /// </summary>
        public static readonly IReadOnlyList<Slot> retiredSlots = new[]
        {
            new Slot("Pickaxe_Unique2", "MeleeWeapons/Pickaxe_Unique2_Steel", Kind.Melee, "MeleeWeaponGearItemInstance", true, "The Monkey Motivator", true),
            new Slot("SpiderCrossbow", "RangedWeapons/SpiderCrossbow", Kind.Ranged, "RangedWeaponGearItemInstance", false, "Spider Crossbow", true),
            new Slot("CowardsArmor_Unique1", "Armor/CowardsArmor_Unique1", Kind.Armor, "ArmorGearItemInstance", true, "Curious Armor", true),
            new Slot("MysteryArmor_Unique1", "Armor/MysteryArmor_Unique1", Kind.Armor, "ArmorGearItemInstance", true, "Mystery Armor", true),
            new Slot("Harvester_Unique1", "Harvester_Unique1", Kind.Artifact, "", true, "Blightbearer", true),
            new Slot("TotemOfShielding_Unique1", "TotemOfShielding_Unique1", Kind.Artifact, "", true, "Totem of Resistance", true),
            new Slot("TotemOfSoulProtection", "TotemOfSoulProtection", Kind.Artifact, "", false, "Totem of Soul Protection", true),
            new Slot("FireworkBomb", "FireworkBomb", Kind.Artifact, "", false, "Firework Bomb", true),
            new Slot("EnderPearl", "EnderPearl", Kind.Artifact, "", false, "Ender Pearl", true),
        };

        public static Slot? retiredSlot(string id) => retiredSlots.FirstOrDefault(s => string.Equals(s.Id, id, StringComparison.OrdinalIgnoreCase));

        // ------------------------------------------------------------------ ids

        /// <summary>
        /// Every custom item's id: MCDR_Item01, MCDR_Item02 and on. Melee, ranged, armour and
        /// artifacts alike.
        /// </summary>
        public const string PLUGIN_PREFIX = "MCDR_Item";

        public static bool isPluginId(string id) => id.StartsWith("MCDR_", StringComparison.OrdinalIgnoreCase);

        /// <summary>
        /// The next id: one past the highest ever handed out, never a gap. A character may still
        /// hold an emptied item's id, and it should not quietly turn into a different item.
        /// </summary>
        public static string newPluginId(IEnumerable<Design> designs)
        {
            var highest = designs.Select(d => d.Slot)
                .Where(id => id.StartsWith(PLUGIN_PREFIX, StringComparison.OrdinalIgnoreCase))
                .Select(id => int.TryParse(id.Substring(PLUGIN_PREFIX.Length), out var n) ? n : 0)
                .DefaultIfEmpty(0).Max();
            highest = Math.Max(highest, lastPluginNumber());
            rememberPluginNumber(highest + 1);
            return PLUGIN_PREFIX + (highest + 1).ToString("00", System.Globalization.CultureInfo.InvariantCulture);
        }

        private static string numberFile => Path.Combine(folder, "last-plugin-id.txt");

        private static int lastPluginNumber()
        {
            try { return File.Exists(numberFile) && int.TryParse(File.ReadAllText(numberFile).Trim(), out var n) ? n : 0; }
            catch (IOException) { return 0; }
        }

        private static void rememberPluginNumber(int number)
        {
            try { File.WriteAllText(numberFile, number.ToString(System.Globalization.CultureInfo.InvariantCulture)); }
            catch (IOException) { }
        }

        /// <summary>A custom item's slot. Its folder is beside its source's once that is chosen.</summary>
        public static Slot pluginSlot(string id, Kind kind, RegistryPatch.GameItem? source = null)
        {
            var (parent, native) = kind switch
            {
                Kind.Melee => ("MeleeWeapons", "MeleeWeaponGearItemInstance"),
                Kind.Ranged => ("RangedWeapons", "RangedWeaponGearItemInstance"),
                Kind.Armor => ("Armor", "ArmorGearItemInstance"),
                //Artifacts sit straight in Actors/Items, and each has a native class of its own.
                _ => ("", ""),
            };
            var folder = source == null ? (parent.Length == 0 ? id : parent + "/" + id) : extraFolder(source, id);
            return new Slot(id, folder, kind, native, false, id, true, plugin: true);
        }

        /// <summary>The slot a design fills, made from the design.</summary>
        public static Slot slotOf(Design design)
        {
            if (design.PluginKind is not { } kind || !isPluginId(design.Slot))
            {
                throw new InvalidOperationException(retiredSlot(design.Slot) != null
                    ? $"{design.Slot} is one of the free slots, which are no longer built: move it to an id of its own first (PROBE_MIGRATESLOTS)."
                    : $"{design.Slot} is not a custom item id.");
            }
            return pluginSlot(design.Slot, kind, string.IsNullOrEmpty(design.Source) ? null : gameItem(design.Source));
        }

        // ------------------------------------------------------------------ what the user designed

        public enum IconSource { Copied, OtherItem, Image }

        /// <summary>One slot, filled. Saved as JSON beside the app's other settings.</summary>
        public sealed class Design
        {
            public string Slot { get; set; } = "";
            /// <summary>The game item it is copied from, by id.</summary>
            public string Source { get; set; } = "";
            public IconSource Icon { get; set; } = IconSource.Copied;
            /// <summary>For IconSource.OtherItem: whose icons to use.</summary>
            public string? IconItem { get; set; }
            /// <summary>For IconSource.Image: a PNG kept in the app's own folder.</summary>
            public string? IconFile { get; set; }
            /// <summary>Behaviour numbers changed from the source's, by path (`ConfiguredAttackVariants[2].Damage`).</summary>
            public Dictionary<string, double> Values { get; set; } = new();
            /// <summary>What the game calls it, in every language. Null keeps the game's own name.</summary>
            public string? Name { get; set; }
            /// <summary>The flavour line under the name. Null keeps the game's own.</summary>
            public string? Description { get; set; }
            /// <summary>A model put on it from the Weapons tab, or its own reshaped. Null keeps the copy's.</summary>
            public ModelEdit? Model { get; set; }
            /// <summary>For an item beyond the free slots (MCDR_ItemNN): its type. Null for a free slot, whose type is fixed.</summary>
            public Kind? PluginKind { get; set; }
            /// <summary>
            /// Artwork from the Recolor Gear tab, painted over the copy's own colour texture: a PNG
            /// kept in the app's folder, at that texture's size. Null keeps the copy's.
            /// </summary>
            public string? Texture { get; set; }
        }

        /// <summary>
        /// The Weapons tab's work on a custom item: an imported model and where the sliders put it,
        /// or - with no file - the copy's own mesh moved, turned and scaled.
        /// </summary>
        public sealed class ModelEdit
        {
            /// <summary>A .glb kept in the app's own folder. Null means reshape the copy's own mesh.</summary>
            public string? File { get; set; }
            public float Scale { get; set; } = 1f;
            public float[] Offset { get; set; } = new float[3];
            public float[] Rotation { get; set; } = new float[3];

            public MeshEdit.Transform transform => new MeshEdit.Transform(Scale,
                new MeshGeometry.Position(Offset[0], Offset[1], Offset[2]),
                new MeshGeometry.Position(Rotation[0], Rotation[1], Rotation[2]));

            public static ModelEdit of(MeshEdit.Transform transform, string? file) => new ModelEdit
            {
                File = file,
                Scale = transform.Scale,
                Offset = new[] { transform.Offset.X, transform.Offset.Y, transform.Offset.Z },
                Rotation = new[] { transform.RotationDegrees.X, transform.RotationDegrees.Y, transform.RotationDegrees.Z },
            };
        }

        public static string folder
        {
            get
            {
                var at = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MCDReborn", "CustomItems");
                Directory.CreateDirectory(at);
                return at;
            }
        }

        private static string file => Path.Combine(folder, "custom-items.json");

        public static List<Design> load()
        {
            try
            {
                if (!File.Exists(file)) { return new List<Design>(); }
                return JsonSerializer.Deserialize<List<Design>>(File.ReadAllText(file)) ?? new List<Design>();
            }
            catch (Exception)
            {
                return new List<Design>();
            }
        }

        public static void save(IReadOnlyList<Design> designs)
            => File.WriteAllText(file, JsonSerializer.Serialize(designs, new JsonSerializerOptions { WriteIndented = true }));

        /// <summary>Copies a picture in, so a design never depends on a file the user later moves.</summary>
        public static string keepImage(string slot, string pngPath)
        {
            var kept = Path.Combine(folder, slot + ".png");
            File.Copy(pngPath, kept, overwrite: true);
            return kept;
        }

        // ------------------------------------------------------------------ sharing

        public const string SHARE_EXTENSION = ".mcditem";

        /// <summary>
        /// One design as a file somebody else can import: a zip holding `item.json` - the design, with
        /// its paths taken out - and beside it the picture and the model the design points at. The
        /// look travels with it; nothing refers back to this machine.
        /// </summary>
        public static void export(Design design, string path)
        {
            var slot = slotOf(design);
            var shared = copy(design);
            //Which id it had here means nothing on another machine; the kind says what it is.
            shared.IconFile = null;
            var hasTexture = design.Texture != null && File.Exists(design.Texture);
            shared.Texture = hasTexture ? "texture.png" : null;
            if (shared.Model != null) { shared.Model.File = null; }

            using var zip = System.IO.Compression.ZipFile.Open(path, System.IO.Compression.ZipArchiveMode.Create);
            void add(string name, byte[] bytes)
            {
                using var stream = zip.CreateEntry(name).Open();
                stream.Write(bytes, 0, bytes.Length);
            }

            add("item.json", JsonSerializer.SerializeToUtf8Bytes(new SharedItem
            {
                Format = 1,
                Kind = slot.Kind,
                Design = shared,
                HasModel = design.Model?.File != null,
            }, new JsonSerializerOptions { WriteIndented = true }));
            if (design.Icon == IconSource.Image && design.IconFile != null && File.Exists(design.IconFile))
            {
                add("icon.png", File.ReadAllBytes(design.IconFile));
            }
            if (design.Model?.File != null && File.Exists(design.Model.File))
            {
                add("model.glb", File.ReadAllBytes(design.Model.File));
            }
            if (hasTexture) { add("texture.png", File.ReadAllBytes(design.Texture!)); }
        }

        /// <summary>What an exported file holds, read without putting it anywhere yet.</summary>
        public sealed class SharedItem
        {
            public int Format { get; set; }
            public Kind Kind { get; set; }
            public Design Design { get; set; } = new();
            public bool HasModel { get; set; }
        }

        public static SharedItem readShared(string path)
        {
            using var zip = System.IO.Compression.ZipFile.OpenRead(path);
            var entry = zip.GetEntry("item.json") ?? throw new InvalidOperationException("That is not an exported item.");
            using var stream = entry.Open();
            var shared = JsonSerializer.Deserialize<SharedItem>(stream) ?? throw new InvalidOperationException("That item file is empty.");
            if (shared.Format != 1) { throw new InvalidOperationException("That item was exported by a newer MCD Reborn."); }
            if (gameItem(shared.Design.Source) == null)
            {
                throw new InvalidOperationException($"It is a copy of {shared.Design.Source}, which this game does not have.");
            }
            return shared;
        }

        /// <summary>
        /// The shared design, unpacked into <paramref name="slot"/>: its picture and model are copied
        /// into this app's folder under that slot's name, and the design points at them there.
        /// </summary>
        public static Design unpack(string path, SharedItem shared, Slot slot)
        {
            if (slot.Kind != shared.Kind) { throw new InvalidOperationException($"{slot.Id} is not that kind of item."); }
            var design = copy(shared.Design);
            design.Slot = slot.Id;
            design.PluginKind = slot.Plugin ? slot.Kind : null;

            using var zip = System.IO.Compression.ZipFile.OpenRead(path);
            byte[]? read(string name)
            {
                var entry = zip.GetEntry(name);
                if (entry == null) { return null; }
                using var stream = entry.Open();
                using var memory = new MemoryStream();
                stream.CopyTo(memory);
                return memory.ToArray();
            }

            if (design.Icon == IconSource.Image)
            {
                var png = read("icon.png");
                if (png == null) { design.Icon = IconSource.Copied; }
                else
                {
                    design.IconFile = Path.Combine(folder, slot.Id + ".png");
                    File.WriteAllBytes(design.IconFile, png);
                }
            }
            if (design.Texture != null)
            {
                var png = read("texture.png");
                design.Texture = png == null ? null : Path.Combine(folder, slot.Id + ".texture.png");
                if (png != null) { File.WriteAllBytes(design.Texture!, png); }
            }
            if (design.Model != null && shared.HasModel)
            {
                var glb = read("model.glb");
                if (glb == null) { design.Model = null; }
                else
                {
                    design.Model.File = Path.Combine(folder, slot.Id + ".glb");
                    File.WriteAllBytes(design.Model.File, glb);
                }
            }
            return design;
        }

        /// <summary>A design with nothing shared with the original, the model's arrays included.</summary>
        public static Design copy(Design d) => new Design
        {
            Slot = d.Slot,
            Source = d.Source,
            Icon = d.Icon,
            IconItem = d.IconItem,
            IconFile = d.IconFile,
            Values = new Dictionary<string, double>(d.Values),
            Name = d.Name,
            Description = d.Description,
            PluginKind = d.PluginKind,
            Texture = d.Texture,
            Model = d.Model == null ? null : new ModelEdit
            {
                File = d.Model.File,
                Scale = d.Model.Scale,
                Offset = (float[])d.Model.Offset.Clone(),
                Rotation = (float[])d.Model.Rotation.Clone(),
            },
        };

        // ------------------------------------------------------------------ the game's items

        private static List<RegistryPatch.GameItem>? _gameItems;

        /// <summary>Every item the game ships, from its own asset registry. Read once.</summary>
        public static IReadOnlyList<RegistryPatch.GameItem> gameItems()
        {
            if (_gameItems != null) { return _gameItems; }
            var paks = CustomSkins.paksFolder;
            var registry = paks == null ? null : RegistryPatch.readGameRegistry(paks);
            _gameItems = registry == null ? new List<RegistryPatch.GameItem>() : RegistryPatch.items(registry);
            return _gameItems;
        }

        /// <summary>What a custom item can be a copy of: game items of the same type.</summary>
        public static IReadOnlyList<RegistryPatch.GameItem> sourcesFor(Slot slot)
            => gameItems()
                .Where(i => canFill(slot, i))
                .Where(i => !isPluginId(i.Id))
                .GroupBy(i => i.Id, StringComparer.OrdinalIgnoreCase).Select(g => g.First())
                .OrderBy(i => i.Id, StringComparer.OrdinalIgnoreCase)
                .ToList();

        /// <summary>
        /// Whether a game item can be copied into a slot. Gear by the native class its Instance
        /// derives from, which is what the game's type is. An artifact has a native class of its
        /// own - HarvesterInstance, TotemOfShieldingInstance - that IS its behaviour, so there is no
        /// shared one to match: it is an artifact when the inventory lists it as one, and it has
        /// to sit straight in an Actors/Items folder, where the slots' own folders are.
        /// </summary>
        public static bool canFill(Slot slot, RegistryPatch.GameItem item)
        {
            if (slot.Kind != Kind.Artifact)
            {
                return string.Equals(item.NativeParent, slot.NativeParent, StringComparison.Ordinal);
            }
            var parent = item.Folder.Substring(0, Math.Max(0, item.Folder.LastIndexOf('/')));
            return ItemDatabase.artifacts.Contains(item.Id)
                && parent.EndsWith("/Actors/Items", StringComparison.OrdinalIgnoreCase);
        }

        public static RegistryPatch.GameItem? gameItem(string id)
            => gameItems().FirstOrDefault(i => string.Equals(i.Id, id, StringComparison.OrdinalIgnoreCase));

        /// <summary>`/Game/X` as the pak index spells it, `/Dungeons/Content/X`.</summary>
        private static string cooked(string gamePath) => "/Dungeons/Content/" + gamePath.Substring("/Game/".Length);

        /// <summary>The numbers the source item's Instance stores - what a design can change.</summary>
        public static List<ItemBehaviour.Number> behaviourOf(RegistryPatch.GameItem source)
        {
            var package = CustomSkins.index?.extractPackage(cooked(source.Folder) + "/" + source.Instance);
            if (package == null) { return new List<ItemBehaviour.Number>(); }
            return ItemBehaviour.numbersOf(package.Value.UAsset.ToArray(), package.Value.UExp.ToArray());
        }

        /// <summary>The icon the inventory shows for a game item, for the preview.</summary>
        public static BitmapSource? iconOf(RegistryPatch.GameItem item)
        {
            var name = item.Instance.EndsWith("Instance", StringComparison.Ordinal)
                ? item.Instance.Substring(3, item.Instance.Length - 3 - "Instance".Length) : item.Id;
            foreach (var suffix in new[] { "_Icon_inventory", "_Icon_Inventory", "_Icon" })
            {
                var image = CustomSkins.preview(cooked(item.Folder) + "/T_" + name + suffix);
                if (image != null) { return image; }
            }
            return null;
        }

        // ------------------------------------------------------------------ building

        public sealed class Built
        {
            public string? PakPath { get; set; }
            public List<string> Notes { get; } = new();
            public int Items { get; set; }
        }

        /// <summary>
        /// Every design, built into one pak with one registry, and installed. An empty list removes
        /// the pak instead - there is nothing for the registry to carry.
        /// </summary>
        /// <summary>
        /// An item under a brand-new id, beyond the free slots: its files go in the same pak and its
        /// entries in the same registry, and MCD Reborn's plugin registers the id inside the game.
        /// </summary>
        public sealed record Extra(string Id, string Source, string Name, string Description);

        /// <summary>Where a copy of <paramref name="source"/> named <paramref name="id"/> lives, as the plugin needs it: `MeleeWeapons/<id>`.</summary>
        public static string extraFolder(RegistryPatch.GameItem source, string id)
        {
            var folder = source.Folder;
            foreach (var place in new[] { "/Actors/Equipment/", "/Actors/Items/" })
            {
                var at = folder.IndexOf(place, StringComparison.OrdinalIgnoreCase);
                if (at < 0) { continue; }
                var relative = folder.Substring(at + place.Length);
                var cut = relative.LastIndexOf('/');
                return cut < 0 ? id : relative.Substring(0, cut + 1) + id;
            }
            throw new InvalidOperationException($"{source.Id} is not under Actors/Equipment or Actors/Items.");
        }

        public static Built build(IReadOnlyList<Design> designs, string? into = null, IReadOnlyList<Extra>? extras = null)
        {
            extras ??= Array.Empty<Extra>();
            var result = new Built();
            var paks = CustomSkins.paksFolder ?? throw new InvalidOperationException("The game's paks folder is not known.");
            var pakPath = into ?? Path.Combine(paks, CustomSkins.MOD_PREFIX + MOD_NAME + "_P.pak");

            if (designs.Count == 0 && extras.Count == 0)
            {
                if (File.Exists(pakPath)) { File.Delete(pakPath); }
                result.Notes.Add("No custom items: the pak was removed.");
                if (into == null && GamePlugin.gameFolder(paks) != null) { result.Notes.Add(GamePlugin.install(Array.Empty<GamePlugin.Item>(), paks)); }
                return result;
            }

            //Checked before anything is written: a plugin item with no plugin to register it is an
            //id the game does not know.
            var pluginItems = new List<GamePlugin.Item>();
            foreach (var design in designs)
            {
                var slot = slotOf(design);
                if (!slot.Plugin) { continue; }
                var source = gameItem(design.Source) ?? throw new InvalidOperationException($"{design.Source} is not a game item.");
                pluginItems.Add(new GamePlugin.Item(slot.Id, source.Id, slot.Folder,
                    string.IsNullOrWhiteSpace(design.Name) ? R.itemName(source.Id) : design.Name!,
                    string.IsNullOrWhiteSpace(design.Description) ? R.itemDesc(source.Id) : design.Description!));
            }
            if (pluginItems.Count > 0 && into == null && GamePlugin.gameFolder(paks) == null)
            {
                throw new InvalidOperationException("Items beyond the free slots need the Steam or Minecraft Launcher version of the game.");
            }

            var registry = RegistryPatch.readGameRegistry(paks)
                ?? throw new InvalidOperationException("Could not read the game's asset registry.");

            var entries = new List<PakWriter.Entry>();
            var copies = new List<(string, Func<string, string?>)>();

            foreach (var design in designs)
            {
                var (slot, made) = copyOf(design);
                //Copies of the bytes: the copy itself is kept for the Weapons tab's preview, and
                //a behaviour edit writes into a .uexp where it lies.
                var files = made.Entries.ToDictionary(e => e.Path, e => (byte[])e.Data.Clone(), StringComparer.OrdinalIgnoreCase);

                applyBehaviour(files, slot, design, result.Notes);
                applyIcons(files, slot, design, result.Notes);
                applyModel(files, slot, design, result.Notes);
                applyTexture(files, slot, design, result.Notes);

                entries.AddRange(files.Select(f => new PakWriter.Entry(f.Key, f.Value)));
                copies.Add((made.GameFrom, made.Rename));
                result.Items++;
            }

            foreach (var extra in extras)
            {
                var source = gameItem(extra.Source) ?? throw new InvalidOperationException($"{extra.Source} is not a game item.");
                var made = NewContent.cloneFolder(cooked(source.Folder), extra.Id, ownsBareId: false,
                    alsoRename: new Dictionary<string, string> { [source.Id] = extra.Id })
                    ?? throw new InvalidOperationException($"Could not copy {source.Id}.");
                entries.AddRange(made.Entries);
                copies.Add((made.GameFrom, made.Rename));
                result.Items++;
                result.Notes.Add($"{extra.Id}: a copy of {source.Id} in {extraFolder(source, extra.Id)}.");
            }


            var patched = RegistryPatch.withClones(registry, copies, out var added)
                ?? throw new InvalidOperationException("The game's asset registry is not in a shape this can add to.");
            if (added == 0) { throw new InvalidOperationException("Nothing was added to the asset registry."); }
            entries.Add(new PakWriter.Entry(RegistryPatch.PAK_PATH, patched));

            PakWriter.write(pakPath, entries);
            result.PakPath = pakPath;
            result.Notes.Add($"{result.Items} item(s), {added} registry entries.");
            if (into == null && (pluginItems.Count > 0 || GamePlugin.gameFolder(paks) != null))
            {
                result.Notes.Add(GamePlugin.install(pluginItems, paks));
            }

            //The SpiderCrossbow test pak carried its own registry. Two registries means only one
            //wins, and the other pak's items crash on click - so that one goes, and its item is
            //expected to be one of the designs.
            var test = Path.Combine(paks, CustomSkins.MODS_FOLDER, "MCDReborn_SpiderCrossbow_P.pak");
            if (into == null && File.Exists(test))
            {
                File.Delete(test);
                result.Notes.Add("Removed the old SpiderCrossbow test pak.");
            }
            return result;
        }

        /// <summary>
        /// The source item's folder copied into the slot, as it would be before any design changes.
        /// Kept, because the Weapons tab reads the copy's mesh for its preview many times over.
        /// </summary>
        private static (Slot slot, NewContent.Made made) copyOf(Design design)
        {
            var slot = slotOf(design);
            var source = gameItem(design.Source) ?? throw new InvalidOperationException($"{design.Source} is not a game item.");
            if (!canFill(slot, source))
            {
                throw new InvalidOperationException(slot.Kind == Kind.Artifact
                    ? $"{source.Id} is not an artifact that can be copied into {slot.Id}."
                    : $"{source.Id} is a {source.NativeParent}, and {slot.Id} only takes a {slot.NativeParent}.");
            }

            var key = slot.Id + "|" + source.Id;
            if (_copies.TryGetValue(key, out var kept)) { return (slot, kept); }

            //The copy lands beside the source, so the source must already sit where the slot
            //expects: MeleeWeapons/<folder> under a root's Actors/Equipment.
            var parent = slot.Folder.Contains('/') ? slot.Folder.Substring(0, slot.Folder.LastIndexOf('/')) : "";
            if (parent.Length > 0 && !source.Folder.Substring(0, source.Folder.LastIndexOf('/')).EndsWith("/" + parent, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException($"{source.Id} is not in a {parent} folder, so its copy would not be where the game looks.");
            }

            var made = NewContent.cloneFolder(cooked(source.Folder), slot.FolderId, ownsBareId: false,
                alsoRename: new Dictionary<string, string> { [source.Id] = slot.Id })
                ?? throw new InvalidOperationException($"Could not copy {source.Id}.");
            _copies[key] = made;
            return (slot, made);
        }

        private static readonly Dictionary<string, NewContent.Made> _copies = new(StringComparer.OrdinalIgnoreCase);

        /// <summary>
        /// The model from the Weapons tab, into every mesh of the copy - a bow's four draw states
        /// alike, as the tab does for a stock bow - with its texture painted over the copy's own and
        /// the copy's materials told not to cut pieces out of it. All of it the copy's: the stock
        /// weapon it came from is in another folder and is never read for writing.
        /// </summary>
        private static void applyModel(Dictionary<string, byte[]> files, Slot slot, Design design, List<string> notes)
        {
            var edit = design.Model;
            if (edit == null) { return; }

            GlbModel? model = null;
            if (edit.File != null)
            {
                if (!File.Exists(edit.File)) { notes.Add($"{slot.Id}: its model file is gone, the copy's own mesh kept."); return; }
                model = GlbModel.read(edit.File);
            }

            var meshes = files.Keys.Where(k => k.EndsWith(".uasset", StringComparison.OrdinalIgnoreCase)
                && Path.GetFileName(k).StartsWith("SM_", StringComparison.OrdinalIgnoreCase)).ToList();
            var changed = 0;
            foreach (var key in meshes)
            {
                var stem = key.Substring(0, key.Length - ".uasset".Length);
                files.TryGetValue(stem + ".ubulk", out var ubulk);
                var package = packageOf(files[key], files[stem + ".uexp"], ubulk);
                if (!MeshBounds.tryRead(package, out var origin, out var extent, out var radius)) { continue; }

                byte[] uasset, uexp;
                if (model == null)
                {
                    var mesh = MeshEdit.open(files[key], files[stem + ".uexp"], origin, extent, radius);
                    if (mesh == null) { continue; }
                    mesh.apply(edit.transform);
                    (uasset, uexp) = mesh.write();
                }
                else
                {
                    var mesh = CookedMesh.open(files[key], files[stem + ".uexp"], origin, extent, radius);
                    if (mesh == null) { continue; }
                    (uasset, uexp) = mesh.rebuild(ModelFitting.place(model, edit.transform, mesh.Attributes.TexCoords));
                }
                files[key] = uasset;
                files[stem + ".uexp"] = uexp;
                changed++;
            }
            notes.Add($"{slot.Id}: {changed} mesh(es) {(model == null ? "reshaped" : "replaced")}.");
            if (model == null) { return; }

            if (model.BaseColourPng != null && colourTextureOf(files, slot) is { } texture)
            {
                var stem = texture.Substring(0, texture.Length - ".uasset".Length);
                files.TryGetValue(stem + ".ubulk", out var ubulk);
                var why = repaint(files[texture], files[stem + ".uexp"], ubulk, CustomSkins.imageFromPng(model.BaseColourPng),
                    out var uexp, out var bulk);
                if (why == null)
                {
                    files[stem + ".uexp"] = uexp;
                    if (bulk != null) { files[stem + ".ubulk"] = bulk; }
                }
                else { notes.Add($"{slot.Id}: the model's texture was not applied - {why}."); }
            }

            foreach (var key in files.Keys.Where(k => k.EndsWith(".uasset", StringComparison.OrdinalIgnoreCase)
                && Path.GetFileName(k).StartsWith("MI_", StringComparison.OrdinalIgnoreCase)).ToList())
            {
                var stem = key.Substring(0, key.Length - ".uasset".Length);
                files.TryGetValue(stem + ".ubulk", out var ubulk);
                var uexp = (byte[])files[stem + ".uexp"].Clone();
                if (CreatureVariants.unmask(packageOf(files[key], files[stem + ".uexp"], ubulk), uexp)) { files[stem + ".uexp"] = uexp; }
            }
        }

        /// <summary>
        /// The Recolor Gear tab's artwork over the copy's colour texture - after the model, whose own
        /// texture it then replaces, as a recolour of a stock item would.
        /// </summary>
        private static void applyTexture(Dictionary<string, byte[]> files, Slot slot, Design design, List<string> notes)
        {
            if (design.Texture == null) { return; }
            if (!File.Exists(design.Texture)) { notes.Add($"{slot.Id}: its recolour file is gone, the copy's own texture kept."); return; }
            var texture = colourTextureOf(files, slot);
            if (texture == null) { notes.Add($"{slot.Id}: no colour texture to recolour."); return; }

            var stem = texture.Substring(0, texture.Length - ".uasset".Length);
            files.TryGetValue(stem + ".ubulk", out var ubulk);
            var why = repaint(files[texture], files[stem + ".uexp"], ubulk, CustomSkins.imageFromPng(File.ReadAllBytes(design.Texture)),
                out var uexp, out var bulk);
            if (why != null) { notes.Add($"{slot.Id}: the recolour was not applied - {why}."); return; }
            files[stem + ".uexp"] = uexp;
            if (bulk != null) { files[stem + ".ubulk"] = bulk; }
            notes.Add($"{slot.Id}: recoloured.");
        }

        /// <summary>The copy's weapon texture: its colour map, the shortest name that is not an icon.</summary>
        private static string? colourTextureOf(Dictionary<string, byte[]> files, Slot slot)
            => files.Keys
                .Where(k => k.EndsWith(".uasset", StringComparison.OrdinalIgnoreCase))
                .Where(k => Path.GetFileName(k).StartsWith("T_", StringComparison.OrdinalIgnoreCase))
                .Where(k => Path.GetFileNameWithoutExtension(k).IndexOf("Icon", StringComparison.OrdinalIgnoreCase) < 0)
                .Where(k => CustomSkins.isColourMap(Path.GetFileNameWithoutExtension(k)))
                .OrderBy(k => Path.GetFileName(k).Length).ThenBy(k => k, StringComparer.Ordinal)
                .FirstOrDefault();

        private static PakPackage packageOf(byte[] uasset, byte[] uexp, byte[]? ubulk)
            => new PakPackage(new ArraySegment<byte>(uasset), new ArraySegment<byte>(uexp),
                ubulk == null ? (ArraySegment<byte>?)null : new ArraySegment<byte>(ubulk));

        // ------------------------------------------------------------------ the Weapons tab

        /// <summary>
        /// The designs as last saved, for everything outside the New Items tab that asks about
        /// custom items - the Weapons tab's list, the inventory's names and pictures.
        /// </summary>
        private static List<Design> _saved = new();

        /// <summary>
        /// Custom items the Weapons tab can put a model on: each one's copied meshes, under the
        /// item's own name. A bow offers only its first draw state, as the tab does for stock bows.
        /// </summary>
        public static IReadOnlyList<(string assetPath, bool bow, string name)> meshesForWorkshop()
        {
            var found = new List<(string, bool, string)>();
            foreach (var design in _saved)
            {
                Slot slot;
                try { slot = slotOf(design); } catch (Exception) { continue; }
                if (slot.Kind != Kind.Melee && slot.Kind != Kind.Ranged) { continue; }
                NewContent.Made made;
                try { made = copyOf(design).made; } catch (Exception) { continue; }
                var meshes = made.Entries.Select(e => e.Path)
                    .Where(p => p.EndsWith(".uasset", StringComparison.OrdinalIgnoreCase)
                        && Path.GetFileName(p).StartsWith("SM_", StringComparison.OrdinalIgnoreCase))
                    .Select(p => "/" + p.Substring(0, p.Length - ".uasset".Length))
                    .OrderBy(p => p, StringComparer.Ordinal)
                    .ToList();
                var name = string.IsNullOrWhiteSpace(design.Name) ? (slot.Plugin ? R.itemName(design.Slot) : slot.BuiltInName) : design.Name!;
                if (slot.Kind == Kind.Ranged)
                {
                    if (meshes.Count > 0) { found.Add((meshes[0], true, name)); }
                    continue;
                }
                foreach (var mesh in meshes) { found.Add((mesh, false, name)); }
            }
            return found;
        }

        /// <summary>Whether this asset is one of a custom item's copies rather than the game's.</summary>
        public static bool isCopied(string assetPath) => copiedFiles(assetPath) != null;

        /// <summary>A custom item's copied asset, read from memory, for the Weapons tab's preview.</summary>
        public static PakPackage? copiedPackage(string assetPath)
        {
            var files = copiedFiles(assetPath);
            if (files == null) { return null; }
            var stem = assetPath.TrimStart('/');
            files.TryGetValue(stem + ".ubulk", out var ubulk);
            return packageOf(files[stem + ".uasset"], files[stem + ".uexp"], ubulk);
        }

        /// <summary>The weapon texture beside a copied mesh, for the preview.</summary>
        public static BitmapSource? copiedTexture(string meshAssetPath)
        {
            foreach (var design in _saved)
            {
                NewContent.Made made;
                Slot slot;
                try { (slot, made) = copyOf(design); } catch (Exception) { continue; }
                var files = made.Entries.ToDictionary(e => e.Path, e => e.Data, StringComparer.OrdinalIgnoreCase);
                if (!files.ContainsKey(meshAssetPath.TrimStart('/') + ".uasset")) { continue; }
                var texture = colourTextureOf(files, slot);
                if (texture == null) { return null; }
                var stem = texture.Substring(0, texture.Length - ".uasset".Length);
                files.TryGetValue(stem + ".ubulk", out var ubulk);
                try
                {
                    var image = packageOf(files[texture], files[stem + ".uexp"], ubulk).GetExport<UTexture2D>()?.Image;
                    return image == null ? null : Services.PakIndexExtensions.bitmapImageFromSKImage(image);
                }
                catch (Exception) { return null; }
            }
            return null;
        }

        private static Dictionary<string, byte[]>? copiedFiles(string assetPath)
        {
            var wanted = assetPath.TrimStart('/') + ".uasset";
            foreach (var design in _saved)
            {
                NewContent.Made made;
                try { made = copyOf(design).made; } catch (Exception) { continue; }
                if (made.Entries.Any(e => string.Equals(e.Path, wanted, StringComparison.OrdinalIgnoreCase)))
                {
                    return made.Entries.ToDictionary(e => e.Path, e => e.Data, StringComparer.OrdinalIgnoreCase);
                }
            }
            return null;
        }

        /// <summary>
        /// What Install does in the Weapons tab for a custom item: the model and its placement go
        /// into the item's design, and the New Items pak is rebuilt. `model` null means reshape.
        /// </summary>
        public static CustomSkins.InstalledMod setModel(string meshAssetPath, GlbModel? model, MeshEdit.Transform transform)
        {
            var designs = load();
            var wanted = meshAssetPath.TrimStart('/') + ".uasset";
            var design = designs.FirstOrDefault(d =>
            {
                try { return copyOf(d).made.Entries.Any(e => string.Equals(e.Path, wanted, StringComparison.OrdinalIgnoreCase)); }
                catch (Exception) { return false; }
            }) ?? throw new InvalidOperationException("That mesh belongs to no custom item.");

            string? kept = null;
            if (model != null)
            {
                if (model.Source == null) { throw new InvalidOperationException("The model's file could not be kept."); }
                kept = Path.Combine(folder, design.Slot + ".glb");
                File.WriteAllBytes(kept, model.Source);
            }
            design.Model = ModelEdit.of(transform, kept);
            var built = build(designs);
            save(designs);
            showInApp();
            return new CustomSkins.InstalledMod(built.PakPath ?? throw new InvalidOperationException("Nothing was built."));
        }

        // ------------------------------------------------------------------ the Recolor Gear tab

        /// <summary>Whether this id is one of the user's custom items, as last saved.</summary>
        public static bool isCustom(string itemId) => _saved.Any(d => string.Equals(d.Slot, itemId, StringComparison.OrdinalIgnoreCase));

        /// <summary>Whether a custom item wears a recolour of the user's.</summary>
        public static bool isRecoloured(string itemId)
            => _saved.Any(d => string.Equals(d.Slot, itemId, StringComparison.OrdinalIgnoreCase) && d.Texture != null);

        /// <summary>The user's custom items of one type, for the Recolor Gear tab to list first.</summary>
        public static IReadOnlyList<string> customIdsOf(Kind kind)
            => _saved.Where(d => d.PluginKind == kind && !string.IsNullOrEmpty(d.Source)).Select(d => d.Slot).ToList();

        /// <summary>
        /// The colour texture a custom item wears now, in the order the build lays them down: the
        /// user's recolour, else an imported model's own texture at the copy's size, else the
        /// copy's own. Its files are in the items pak, which the app's index of the game leaves
        /// out, so the Recolor Gear tab asks here instead.
        /// </summary>
        public static BitmapSource? colourTexture(string itemId)
        {
            var design = _saved.FirstOrDefault(d => string.Equals(d.Slot, itemId, StringComparison.OrdinalIgnoreCase));
            if (design == null) { return null; }
            try
            {
                if (design.Texture != null && File.Exists(design.Texture)) { return CustomSkins.imageFromPng(File.ReadAllBytes(design.Texture)); }
                var own = copiedColourTexture(design);
                if (own != null && design.Model?.File is { } glb && File.Exists(glb) && GlbModel.read(glb).BaseColourPng is { } png)
                {
                    var w = own.PixelWidth;
                    var h = own.PixelHeight;
                    return BitmapSource.Create(w, h, 96, 96, System.Windows.Media.PixelFormats.Bgra32, null,
                        CustomSkins.pixelsAt(CustomSkins.imageFromPng(png), w, h), w * 4);
                }
                return own;
            }
            catch (Exception) { return null; }
        }

        private static BitmapSource? copiedColourTexture(Design design)
        {
            var (slot, made) = copyOf(design);
            var files = made.Entries.ToDictionary(e => e.Path, e => e.Data, StringComparer.OrdinalIgnoreCase);
            var texture = colourTextureOf(files, slot);
            if (texture == null) { return null; }
            var stem = texture.Substring(0, texture.Length - ".uasset".Length);
            files.TryGetValue(stem + ".ubulk", out var ubulk);
            var image = packageOf(files[texture], files[stem + ".uexp"], ubulk).GetExport<UTexture2D>()?.Image;
            return image == null ? null : Services.PakIndexExtensions.bitmapImageFromSKImage(image);
        }

        /// <summary>
        /// What Apply does in the Recolor Gear tab for a custom item: the picture goes into the
        /// item's design and the items pak is rebuilt. A pak of its own would have to override a
        /// file the items pak also supplies, and which of two paks wins is decided by how their
        /// names sort. The picture must be the copy's texture's own size, as for any recolour.
        /// </summary>
        /// <param name="image">The new picture, or null to go back to the copy's own texture.</param>
        public static CustomSkins.InstalledMod setTexture(string itemId, BitmapSource? image)
        {
            var designs = load();
            var design = designs.FirstOrDefault(d => string.Equals(d.Slot, itemId, StringComparison.OrdinalIgnoreCase))
                ?? throw new InvalidOperationException($"{itemId} is not a custom item.");
            if (image == null)
            {
                design.Texture = null;
            }
            else
            {
                var own = copiedColourTexture(design) ?? throw new InvalidOperationException($"{itemId} has no colour texture to recolour.");
                if (image.PixelWidth != own.PixelWidth || image.PixelHeight != own.PixelHeight)
                {
                    throw new InvalidOperationException(
                        $"That image is {image.PixelWidth}×{image.PixelHeight}; this texture is {own.PixelWidth}×{own.PixelHeight}.");
                }

                var kept = Path.Combine(folder, design.Slot + ".texture.png");
                var encoder = new PngBitmapEncoder();
                encoder.Frames.Add(BitmapFrame.Create(image));
                using (var file = File.Create(kept)) { encoder.Save(file); }
                design.Texture = kept;
            }

            var built = build(designs);
            save(designs);
            showInApp();
            return new CustomSkins.InstalledMod(built.PakPath ?? throw new InvalidOperationException("Nothing was built."));
        }

        // ------------------------------------------------------------------ the rest of the app

        private static readonly Dictionary<string, System.Windows.Media.Imaging.BitmapImage?> _appIcons = new(StringComparer.OrdinalIgnoreCase);

        /// <summary>
        /// Tells the rest of the app what the custom items are called and look like: names and
        /// descriptions into the item text the pickers read, pictures for the inventory. Run when
        /// the game's content is loaded and after every build.
        /// </summary>
        public static void showInApp()
        {
            foreach (var old in _saved)
            {
                R.itemTextOverrides.Remove(old.Slot);
                R.itemTextOverrides.Remove("Flavour_" + old.Slot);
            }
            _saved = load();
            _appIcons.Clear();
            foreach (var design in _saved)
            {
                //A plugin item has no text of the game's to fall back on: unnamed, it is called what
                //the plugin tells the game to call it, its source's name.
                var plugin = isPluginId(design.Slot) && !string.IsNullOrEmpty(design.Source);
                if (!string.IsNullOrWhiteSpace(design.Name)) { R.itemTextOverrides[design.Slot] = design.Name!; }
                else if (plugin) { R.itemTextOverrides[design.Slot] = R.itemName(design.Source); }
                if (!string.IsNullOrWhiteSpace(design.Description)) { R.itemTextOverrides["Flavour_" + design.Slot] = design.Description!; }
                else if (plugin) { R.itemTextOverrides["Flavour_" + design.Slot] = R.itemDesc(design.Source); }
            }
        }

        /// <summary>A custom item's picture for the inventory, or null for anything else.</summary>
        public static System.Windows.Media.Imaging.BitmapImage? iconForApp(string itemId)
        {
            if (_appIcons.TryGetValue(itemId, out var known)) { return known; }
            var design = _saved.FirstOrDefault(d => string.Equals(d.Slot, itemId, StringComparison.OrdinalIgnoreCase));
            if (design == null) { return null; }

            BitmapSource? picture = null;
            try
            {
                if (design.Icon == IconSource.Image && design.IconFile != null && File.Exists(design.IconFile))
                {
                    picture = CustomSkins.imageFromPng(File.ReadAllBytes(design.IconFile));
                }
                else if (design.Icon == IconSource.OtherItem && design.IconItem != null && gameItem(design.IconItem) is { } other)
                {
                    picture = iconOf(other);
                }
                else if (gameItem(design.Source) is { } source)
                {
                    picture = iconOf(source);
                }
            }
            catch (Exception) { picture = null; }

            var image = picture == null ? null : asBitmapImage(picture);
            _appIcons[itemId] = image;
            return image;
        }

        private static System.Windows.Media.Imaging.BitmapImage asBitmapImage(BitmapSource source)
        {
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(source));
            using var stream = new MemoryStream();
            encoder.Save(stream);
            stream.Position = 0;
            var image = new System.Windows.Media.Imaging.BitmapImage();
            image.BeginInit();
            image.CacheOption = BitmapCacheOption.OnLoad;
            image.StreamSource = stream;
            image.EndInit();
            image.Freeze();
            return image;
        }

        /// <summary>The design's numbers, written over the copied Instance's.</summary>
        private static void applyBehaviour(Dictionary<string, byte[]> files, Slot slot, Design design, List<string> notes)
        {
            if (design.Values.Count == 0) { return; }
            var stem = files.Keys.FirstOrDefault(k => k.EndsWith("/BP_" + slot.FolderId + "Instance.uasset", StringComparison.OrdinalIgnoreCase));
            if (stem == null) { notes.Add($"{slot.Id}: no Instance blueprint to change."); return; }
            var uexpKey = stem.Substring(0, stem.Length - ".uasset".Length) + ".uexp";
            var uexp = files[uexpKey];
            var numbers = ItemBehaviour.numbersOf(files[stem], uexp).ToDictionary(n => n.Path, n => n);
            var written = 0;
            foreach (var (path, value) in design.Values)
            {
                if (numbers.TryGetValue(path, out var number) && ItemBehaviour.set(uexp, number, value)) { written++; }
                else { notes.Add($"{slot.Id}: {path} is not stored by {design.Source}, left as it is."); }
            }
            files[uexpKey] = uexp;
            notes.Add($"{slot.Id}: {written} behaviour value(s) changed.");
        }

        /// <summary>The copied icons, repainted from another item's or from the user's picture.</summary>
        private static void applyIcons(Dictionary<string, byte[]> files, Slot slot, Design design, List<string> notes)
        {
            BitmapSource? picture = null;
            if (design.Icon == IconSource.Image && design.IconFile != null && File.Exists(design.IconFile))
            {
                picture = CustomSkins.imageFromPng(File.ReadAllBytes(design.IconFile));
            }
            else if (design.Icon == IconSource.OtherItem && design.IconItem != null && gameItem(design.IconItem) is { } other)
            {
                picture = iconOf(other);
            }
            if (picture == null) { return; }

            var painted = 0;
            foreach (var key in files.Keys.Where(k => k.EndsWith(".uasset", StringComparison.OrdinalIgnoreCase)).ToList())
            {
                var name = Path.GetFileNameWithoutExtension(key);
                //The inventory picture, the big one and the small gear one. Not T_<id> itself: that
                //is the weapon's own skin, painted onto its mesh.
                if (!name.StartsWith("T_" + slot.FolderId + "_", StringComparison.OrdinalIgnoreCase)) { continue; }
                if (name.IndexOf("Icon", StringComparison.OrdinalIgnoreCase) < 0) { continue; }
                var stem = key.Substring(0, key.Length - ".uasset".Length);
                files.TryGetValue(stem + ".ubulk", out var ubulk);
                var why = repaint(files[key], files[stem + ".uexp"], ubulk, picture, out var uexp, out var bulk);
                if (why == null)
                {
                    files[stem + ".uexp"] = uexp;
                    if (bulk != null) { files[stem + ".ubulk"] = bulk; }
                    painted++;
                }
                else
                {
                    notes.Add($"{slot.Id}: {name} kept as copied - {why}.");
                }
            }
            notes.Add($"{slot.Id}: {painted} icon(s) repainted.");
        }

        /// <summary>
        /// Every mip of one texture replaced by the picture at that mip's size. The pixels are found
        /// where they are - in the .uexp or the .ubulk - and must be found exactly once.
        /// </summary>
        private static string? repaint(byte[] uasset, byte[] uexp, byte[]? ubulk, BitmapSource picture,
            out byte[] newUexp, out byte[]? newBulk)
        {
            newUexp = uexp;
            newBulk = ubulk;
            UTexture2D? texture;
            try
            {
                //Not `ubulk == null ? null : ...`: byte[] converts to ArraySegment implicitly, so that
                //null becomes an EMPTY segment, and the reader goes looking in a bulk file of nothing.
                ArraySegment<byte>? bulkSegment = ubulk == null ? (ArraySegment<byte>?)null : new ArraySegment<byte>(ubulk);
                var package = new PakPackage(new ArraySegment<byte>(uasset), new ArraySegment<byte>(uexp), bulkSegment);
                texture = package.GetExport<UTexture2D>();
            }
            catch (Exception e) { return "it could not be read (" + e.Message + ")"; }
            if (texture == null || texture.PlatformDatas.Length == 0) { return "it is not a texture"; }

            var platform = texture.PlatformDatas[0];
            var format = platform.PixelFormat;
            if (format != EPixelFormat.PF_B8G8R8A8 && format != EPixelFormat.PF_DXT1 && format != EPixelFormat.PF_DXT5)
            {
                return $"it is stored as {format}";
            }

            var exp = (byte[])uexp.Clone();
            var bulk = ubulk == null ? null : (byte[])ubulk.Clone();
            foreach (var mip in platform.Mips)
            {
                var old = mip.BulkData.Data;
                if (old == null || old.Length == 0 || mip.SizeX <= 0 || mip.SizeY <= 0) { continue; }
                var pixels = CustomSkins.pixelsAt(picture, mip.SizeX, mip.SizeY);
                if (format == EPixelFormat.PF_DXT1) { pixels = BlockCompression.toDxt1(pixels, mip.SizeX, mip.SizeY); }
                else if (format == EPixelFormat.PF_DXT5) { pixels = BlockCompression.toDxt5(pixels, mip.SizeX, mip.SizeY); }
                if (pixels.Length != old.Length) { return $"a {mip.SizeX}x{mip.SizeY} mip holds {old.Length} bytes, not {pixels.Length}"; }

                if (!replaceOnce(exp, old, pixels) && (bulk == null || !replaceOnce(bulk, old, pixels)))
                {
                    return $"the {mip.SizeX}x{mip.SizeY} pixels could not be found exactly once";
                }
            }
            newUexp = exp;
            newBulk = bulk;
            return null;
        }

        private static bool replaceOnce(byte[] haystack, byte[] needle, byte[] with)
        {
            var at = indexOf(haystack, needle, 0);
            if (at < 0 || indexOf(haystack, needle, at + 1) >= 0) { return false; }
            Buffer.BlockCopy(with, 0, haystack, at, with.Length);
            return true;
        }

        private static int indexOf(byte[] haystack, byte[] needle, int from)
        {
            if (needle.Length == 0 || haystack.Length < needle.Length) { return -1; }
            var span = haystack.AsSpan();
            var at = from;
            while (at <= haystack.Length - needle.Length)
            {
                var found = span.Slice(at).IndexOf(needle);
                if (found < 0) { return -1; }
                return at + found;
            }
            return -1;
        }
    }
}
