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
    /// New items in the ids the game registers but never shipped.
    ///
    /// The game's item list is C++: 324 ids, each with its folder, English name, whether it is
    /// unique and what slot it goes in, all compiled in. Fifteen of them have no files in any pak -
    /// cut content - and nine of those can be equipped. Filling one is three things, and all three
    /// were found by making it crash first:
    ///
    ///   1. the files, copied from an existing item of the same type and renamed into the slot's
    ///      folder, with the item id inside them pointed at the slot (NewContent.cloneFolder);
    ///   2. the game's asset registry, with those files added (RegistryPatch) - the game finds an
    ///      item's blueprint through it, and without the entries the item shows in the inventory
    ///      and crashes the moment it is clicked;
    ///   3. optionally its name and description, written into every language's Game.locres under
    ///      the keys the game already has for the slot (Locres). The slot's type and its unique
    ///      frame come from the C++ entry and cannot be changed from a pak: a melee slot takes a
    ///      melee weapon.
    ///
    /// Only one pak can own the registry, so every custom item is built into one pak together.
    /// </summary>
    public static class CustomItems
    {
        public const string MOD_NAME = "CustomItems";

        public enum Kind { Melee, Ranged, Armor, Artifact }

        /// <summary>A free id the game already knows, and what it will always be.</summary>
        public sealed class Slot
        {
            public Slot(string id, string folder, Kind kind, string nativeParent, bool unique, string builtInName, bool ready)
            {
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

            public string FolderId => Folder.Substring(Folder.LastIndexOf('/') + 1);
        }

        /// <summary>
        /// The nine cut ids that can be equipped. Read from the game's own registry records: the type
        /// byte at +0x89, unique at +0x8C, the folder from the record's paths - see changelog.md.
        /// </summary>
        public static readonly IReadOnlyList<Slot> slots = new[]
        {
            new Slot("Pickaxe_Unique2", "MeleeWeapons/Pickaxe_Unique2_Steel", Kind.Melee, "MeleeWeaponGearItemInstance", true, "The Monkey Motivator", true),
            new Slot("SpiderCrossbow", "RangedWeapons/SpiderCrossbow", Kind.Ranged, "RangedWeaponGearItemInstance", false, "Spider Crossbow", true),
            new Slot("CowardsArmor_Unique1", "Armor/CowardsArmor_Unique1", Kind.Armor, "ArmorGearItemInstance", true, "Curious Armor", false),
            new Slot("MysteryArmor_Unique1", "Armor/MysteryArmor_Unique1", Kind.Armor, "ArmorGearItemInstance", true, "Mystery Armor", false),
            new Slot("Harvester_Unique1", "Harvester_Unique1", Kind.Artifact, "", true, "Blightbearer", false),
            new Slot("TotemOfShielding_Unique1", "TotemOfShielding_Unique1", Kind.Artifact, "", true, "Totem of Resistance", false),
            new Slot("TotemOfSoulProtection", "TotemOfSoulProtection", Kind.Artifact, "", false, "Totem of Soul Protection", false),
            new Slot("FireworkBomb", "FireworkBomb", Kind.Artifact, "", false, "Firework Bomb", false),
            new Slot("EnderPearl", "EnderPearl", Kind.Artifact, "", false, "Ender Pearl", false),
        };

        public static Slot? slotFor(string id) => slots.FirstOrDefault(s => string.Equals(s.Id, id, StringComparison.OrdinalIgnoreCase));

        /// <summary>The slot whose folder this is, so a mod pak's `Pickaxe_Unique2_Steel` reads as `Pickaxe_Unique2`.</summary>
        public static Slot? slotForFolder(string folderId)
            => slots.FirstOrDefault(s => string.Equals(s.FolderId, folderId, StringComparison.OrdinalIgnoreCase));

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

        /// <summary>What can go in a slot: game items of the same native type, the cut ids themselves excepted.</summary>
        public static IReadOnlyList<RegistryPatch.GameItem> sourcesFor(Slot slot)
            => gameItems()
                .Where(i => string.Equals(i.NativeParent, slot.NativeParent, StringComparison.Ordinal))
                .Where(i => slotFor(i.Id) == null)
                .GroupBy(i => i.Id, StringComparer.OrdinalIgnoreCase).Select(g => g.First())
                .OrderBy(i => i.Id, StringComparer.OrdinalIgnoreCase)
                .ToList();

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
        public static Built build(IReadOnlyList<Design> designs, string? into = null)
        {
            var result = new Built();
            var paks = CustomSkins.paksFolder ?? throw new InvalidOperationException("The game's paks folder is not known.");
            var pakPath = into ?? Path.Combine(paks, CustomSkins.MOD_PREFIX + MOD_NAME + "_P.pak");

            if (designs.Count == 0)
            {
                if (File.Exists(pakPath)) { File.Delete(pakPath); }
                result.Notes.Add("No custom items: the pak was removed.");
                return result;
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

                entries.AddRange(files.Select(f => new PakWriter.Entry(f.Key, f.Value)));
                copies.Add((made.GameFrom, made.Rename));
                result.Items++;
            }

            applyText(designs, entries, result.Notes);

            var patched = RegistryPatch.withClones(registry, copies, out var added)
                ?? throw new InvalidOperationException("The game's asset registry is not in a shape this can add to.");
            if (added == 0) { throw new InvalidOperationException("Nothing was added to the asset registry."); }
            entries.Add(new PakWriter.Entry(RegistryPatch.PAK_PATH, patched));

            PakWriter.write(pakPath, entries);
            result.PakPath = pakPath;
            result.Notes.Add($"{result.Items} item(s), {added} registry entries.");

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
        /// Names and descriptions, into a copy of every language's Game.locres. The keys are the
        /// game's own - `ItemType/&lt;id&gt;` and `ItemType/Flavour_&lt;id&gt;`, present for every slot -
        /// so each entry keeps the source hash the game checks it against and only its text changes.
        /// Written into all fifteen languages alike: a name typed once should not turn back into
        /// "The Monkey Motivator" for somebody playing in German.
        /// </summary>
        private static void applyText(IReadOnlyList<Design> designs, List<PakWriter.Entry> entries, List<string> notes)
        {
            var named = designs.Where(d => !string.IsNullOrWhiteSpace(d.Name) || !string.IsNullOrWhiteSpace(d.Description)).ToList();
            if (named.Count == 0) { return; }
            var index = CustomSkins.index ?? throw new InvalidOperationException("Game content is not loaded.");

            var cultures = index.Where(p => p.Contains("/Localization/Game/", StringComparison.OrdinalIgnoreCase)
                    && p.EndsWith("/Game", StringComparison.Ordinal))
                .Select(p => p.StartsWith("//", StringComparison.Ordinal) ? p.Substring(1) : p)
                .ToList();

            var english = cultures.FirstOrDefault(c => c.EndsWith("/en/Game", StringComparison.OrdinalIgnoreCase));
            var englishTable = english == null || index.GetFile(english) is not { } en ? null : Locres.read(en.ToArray());

            var written = 0;
            foreach (var culture in cultures)
            {
                if (index.GetFile(culture) is not { } raw) { continue; }
                var table = Locres.read(raw.ToArray());
                if (table == null) { continue; }             //the locmeta beside them, not a culture

                foreach (var design in named)
                {
                    foreach (var (key, text) in new[] { (design.Slot, design.Name), ("Flavour_" + design.Slot, design.Description) })
                    {
                        if (string.IsNullOrWhiteSpace(text)) { continue; }
                        //A key a language lacks is added with the English as its source, which is the
                        //text the game's C++ compiled in for it.
                        table.set("ItemType", key, text!.Trim(), englishTable?.get("ItemType", key));
                    }
                }
                entries.Add(new PakWriter.Entry(culture.TrimStart('/') + ".locres", table.write()));
                written++;
            }
            notes.Add($"Names written into {written} language(s).");
        }

        /// <summary>
        /// The source item's folder copied into the slot, as it would be before any design changes.
        /// Kept, because the Weapons tab reads the copy's mesh for its preview many times over.
        /// </summary>
        private static (Slot slot, NewContent.Made made) copyOf(Design design)
        {
            var slot = slotFor(design.Slot) ?? throw new InvalidOperationException($"{design.Slot} is not a free slot.");
            var source = gameItem(design.Source) ?? throw new InvalidOperationException($"{design.Source} is not a game item.");
            if (!string.Equals(source.NativeParent, slot.NativeParent, StringComparison.Ordinal))
            {
                throw new InvalidOperationException($"{source.Id} is a {source.NativeParent}, and {slot.Id} only takes a {slot.NativeParent}.");
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
                var slot = slotFor(design.Slot);
                if (slot == null || (slot.Kind != Kind.Melee && slot.Kind != Kind.Ranged)) { continue; }
                NewContent.Made made;
                try { made = copyOf(design).made; } catch (Exception) { continue; }
                var meshes = made.Entries.Select(e => e.Path)
                    .Where(p => p.EndsWith(".uasset", StringComparison.OrdinalIgnoreCase)
                        && Path.GetFileName(p).StartsWith("SM_", StringComparison.OrdinalIgnoreCase))
                    .Select(p => "/" + p.Substring(0, p.Length - ".uasset".Length))
                    .OrderBy(p => p, StringComparer.Ordinal)
                    .ToList();
                var name = string.IsNullOrWhiteSpace(design.Name) ? slot.BuiltInName : design.Name!;
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
                if (!string.IsNullOrWhiteSpace(design.Name)) { R.itemTextOverrides[design.Slot] = design.Name!; }
                if (!string.IsNullOrWhiteSpace(design.Description)) { R.itemTextOverrides["Flavour_" + design.Slot] = design.Description!; }
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
