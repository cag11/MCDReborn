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
            /// <summary>
            /// A model put on it from the Mobs tab: a .glb kept in the app's folder and its placement.
            /// Null wears the copied mob's own. With one, the mob gets copies of its blueprints and
            /// mesh of its own (lookFiles), so the mob it copies keeps its look.
            /// </summary>
            public CustomItems.ModelEdit? Model { get; set; }
        }

        public static Design copy(Design d) => new Design
        {
            Id = d.Id, Source = d.Source, Name = d.Name,
            Model = d.Model == null ? null : new CustomItems.ModelEdit
            {
                File = d.Model.File, Scale = d.Model.Scale,
                Offset = (float[])d.Model.Offset.Clone(), Rotation = (float[])d.Model.Rotation.Clone(),
            },
        };

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

        /// <summary>What the plugin is told for each saved design: its own blueprint when it has a look of its own.</summary>
        public static IReadOnlyList<GamePlugin.Mob> forPlugin()
            => load()
                .Select(d => (design: d, source: sourceOf(d)))
                .Where(x => x.source != null)
                .Select(x => new GamePlugin.Mob(x.design.Id, x.source!.Type, nameOf(x.design),
                    hasLook(x.design) ? ownBlueprint(x.design, x.source) : x.source.Blueprint))
                .ToList();

        // ------------------------------------------------------------------ its own look

        public static bool hasLook(Design d) => d.Model?.File != null && File.Exists(d.Model.File);

        /// <summary>
        /// Which of a mob's folder to copy for a look of its own: its character blueprints (the one
        /// it is and the parent it gets its mesh from), its mesh, its material and textures. Not
        /// its animation blueprints, skeleton or physics asset: the animations beside them are
        /// bound to the game's skeleton, and a copied mesh that keeps that skeleton plays them all.
        /// </summary>
        /// <remarks>
        /// By prefix alone. The skeleton and physics asset are Skeleton_Skeleton and
        /// Skeleton_PhysicsAsset, which no prefix here takes; excluding names ending _Skeleton
        /// instead also threw out SK_Skeleton, MI_Skeleton and T_Skeleton, the very mesh and
        /// textures a Skeleton copy needs.
        /// </remarks>
        private static bool copiedForLook(string package)
        {
            return package.StartsWith("BP_", StringComparison.OrdinalIgnoreCase)
                || package.StartsWith("SK_", StringComparison.OrdinalIgnoreCase)
                || package.StartsWith("MI_", StringComparison.OrdinalIgnoreCase)
                || package.StartsWith("T_", StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>The source's folder, cooked-spelled, and its last part - what the copy's names swap for the id.</summary>
        private static (string folder, string name) sourceFolder(GameMobTypes.MobType source)
        {
            var relative = source.Blueprint.Substring(0, source.Blueprint.LastIndexOf('/'));
            return ("/Dungeons/Content/" + relative, relative.Substring(relative.LastIndexOf('/') + 1));
        }

        /// <summary>The copy's blueprint, as the plugin wants it: under /Game/, its folder and name carrying the id.</summary>
        public static string ownBlueprint(Design d, GameMobTypes.MobType source)
        {
            var (_, folderName) = sourceFolder(source);
            var cut = source.Blueprint.LastIndexOf('/');
            var parent = source.Blueprint.Substring(0, cut);
            var bp = source.Blueprint.Substring(cut + 1);
            var at = bp.IndexOf(folderName, StringComparison.OrdinalIgnoreCase);
            var renamed = at < 0 ? bp : bp.Substring(0, at) + d.Id + bp.Substring(at + folderName.Length);
            return parent.Substring(0, parent.Length - folderName.Length) + d.Id + "/" + renamed;
        }

        private static readonly Dictionary<string, NewContent.Made> _copies = new(StringComparer.OrdinalIgnoreCase);

        /// <summary>The copy as it would be before any look goes on it. Kept: the Mobs tab previews it many times over.</summary>
        public static NewContent.Made? copyOf(Design d)
        {
            var source = sourceOf(d);
            if (source == null) { return null; }
            var key = d.Id + "|" + d.Source;
            if (_copies.TryGetValue(key, out var kept)) { return kept; }
            var (folder, _) = sourceFolder(source);
            var made = NewContent.cloneFolder(folder, d.Id, ownsBareId: false, include: copiedForLook);
            if (made != null) { _copies[key] = made; }
            return made;
        }

        /// <summary>The copy's skeletal meshes, by asset path without extension: /Dungeons/.../SK_MCDR_Mob03.</summary>
        public static IEnumerable<string> meshesOf(NewContent.Made made)
            => made.Entries.Select(e => e.Path)
                .Where(p => p.EndsWith(".uasset", StringComparison.OrdinalIgnoreCase)
                    && Path.GetFileName(p).StartsWith("SK_", StringComparison.OrdinalIgnoreCase))
                .Select(p => "/" + p.Substring(0, p.Length - ".uasset".Length));

        /// <summary>
        /// The copy with its look on: the model fitted to every copied mesh, its texture painted
        /// over the copy's colour maps. For the New Items pak, and the copy for its registry.
        /// </summary>
        public static (List<PakWriter.Entry> entries, NewContent.Made made) lookFiles(Design d, List<string> notes)
        {
            var made = copyOf(d) ?? throw new InvalidOperationException($"{d.Source}'s folder could not be copied for {d.Id}.");
            var files = made.Entries.ToDictionary(e => e.Path, e => (byte[])e.Data.Clone(), StringComparer.OrdinalIgnoreCase);
            var model = GlbModel.read(d.Model!.File!);
            var meshes = 0;
            foreach (var mesh in meshesOf(made))
            {
                var stem = mesh.TrimStart('/');
                var skeletal = CookedSkeletalMesh.open(files[stem + ".uasset"], files[stem + ".uexp"], out var why);
                if (skeletal == null) { notes.Add($"{d.Id}: {Path.GetFileName(stem)} kept as copied - {why}."); continue; }
                var (uasset, uexp) = skeletal.rebuild(ModelFitting.place(model, d.Model.transform, skeletal.NumTexCoords));
                files[stem + ".uasset"] = uasset;
                files[stem + ".uexp"] = uexp;
                meshes++;
            }
            var painted = 0;
            if (model.BaseColourPng != null)
            {
                var picture = CustomSkins.imageFromPng(model.BaseColourPng);
                var textures = files.Keys.Where(k => k.EndsWith(".uasset", StringComparison.OrdinalIgnoreCase)
                    && Path.GetFileName(k).StartsWith("T_", StringComparison.OrdinalIgnoreCase)
                    && CustomSkins.isColourMap(Path.GetFileNameWithoutExtension(k))).ToList();
                foreach (var key in textures)
                {
                    var stem = key.Substring(0, key.Length - ".uasset".Length);
                    files.TryGetValue(stem + ".ubulk", out var ubulk);
                    var why = CustomItems.repaint(files[key], files[stem + ".uexp"], ubulk, picture, out var uexp, out var bulk);
                    if (why != null) { notes.Add($"{d.Id}: {Path.GetFileName(stem)} not painted - {why}."); continue; }
                    files[stem + ".uexp"] = uexp;
                    if (bulk != null) { files[stem + ".ubulk"] = bulk; }
                    painted++;
                }
            }
            notes.Add($"{d.Id}: its own look - {meshes} mesh(es) replaced, {painted} texture(s) painted.");
            return (files.Select(f => new PakWriter.Entry(f.Key, f.Value)).ToList(), made);
        }

        // ------------------------------------------------------------------ the Mobs tab

        /// <summary>Custom mobs' copied meshes, for the Mobs tab to put a model on, under the mob's name.</summary>
        public static IReadOnlyList<(string assetPath, string name)> meshesForWorkshop()
        {
            var found = new List<(string, string)>();
            foreach (var design in load())
            {
                NewContent.Made? made = null;
                try { made = copyOf(design); } catch (Exception) { }
                if (made == null) { continue; }
                foreach (var mesh in meshesOf(made)) { found.Add((mesh, nameOf(design))); }
            }
            return found;
        }

        private static Design? designOfMesh(string meshAssetPath, List<Design>? designs = null)
            => (designs ?? load()).FirstOrDefault(d =>
            {
                try { return copyOf(d) is { } made && meshesOf(made).Any(m => string.Equals(m, meshAssetPath, StringComparison.OrdinalIgnoreCase)); }
                catch (Exception) { return false; }
            });

        public static bool isCopied(string meshAssetPath)
            => meshAssetPath.IndexOf("/" + PREFIX, StringComparison.OrdinalIgnoreCase) >= 0 && designOfMesh(meshAssetPath) != null;

        /// <summary>A copied package, read as the game's would be, for the Mobs tab's preview.</summary>
        public static PakReader.Pak.PakPackage? copiedPackage(string assetPath)
        {
            if (assetPath.IndexOf("/" + PREFIX, StringComparison.OrdinalIgnoreCase) < 0) { return null; }
            var stem = assetPath.TrimStart('/');
            foreach (var made in _copies.Values)
            {
                var asset = made.Entries.FirstOrDefault(e => string.Equals(e.Path, stem + ".uasset", StringComparison.OrdinalIgnoreCase));
                if (asset == null) { continue; }
                var data = made.Entries.First(e => string.Equals(e.Path, stem + ".uexp", StringComparison.OrdinalIgnoreCase));
                var bulk = made.Entries.FirstOrDefault(e => string.Equals(e.Path, stem + ".ubulk", StringComparison.OrdinalIgnoreCase));
                return new PakReader.Pak.PakPackage(new ArraySegment<byte>(asset.Data), new ArraySegment<byte>(data.Data),
                    bulk == null ? (ArraySegment<byte>?)null : new ArraySegment<byte>(bulk.Data));
            }
            return null;
        }

        /// <summary>The copy's colour texture, for the preview.</summary>
        public static System.Windows.Media.Imaging.BitmapSource? copiedTexture(string meshAssetPath)
        {
            var design = designOfMesh(meshAssetPath);
            var made = design == null ? null : copyOf(design);
            if (made == null) { return null; }
            var texture = made.Entries.Select(e => e.Path)
                .Where(p => p.EndsWith(".uasset", StringComparison.OrdinalIgnoreCase)
                    && Path.GetFileName(p).StartsWith("T_", StringComparison.OrdinalIgnoreCase)
                    && CustomSkins.isColourMap(Path.GetFileNameWithoutExtension(p)))
                .OrderBy(p => p.Length).FirstOrDefault();
            if (texture == null) { return null; }
            try
            {
                var image = copiedPackage("/" + texture.Substring(0, texture.Length - ".uasset".Length))?.GetExport<PakReader.Parsers.Class.UTexture2D>()?.Image;
                return image == null ? null : Services.PakIndexExtensions.bitmapImageFromSKImage(image);
            }
            catch (Exception) { return null; }
        }

        /// <summary>
        /// A model put on a custom mob from the Mobs tab: kept in the mob's design and built into
        /// the New Items pak with everything else. No model takes its look off again.
        /// </summary>
        public static CustomSkins.InstalledMod setModel(string meshAssetPath, GlbModel? model, MeshEdit.Transform transform)
        {
            var designs = load();
            var design = designOfMesh(meshAssetPath, designs) ?? throw new InvalidOperationException("That mesh belongs to no custom mob.");
            string? kept = null;
            if (model != null)
            {
                if (model.Source == null) { throw new InvalidOperationException("The model's file could not be kept."); }
                kept = Path.Combine(CustomItems.folder, design.Id + ".glb");
                File.WriteAllBytes(kept, model.Source);
            }
            design.Model = model == null ? null : CustomItems.ModelEdit.of(transform, kept);
            save(designs);
            var built = CustomItems.build(CustomItems.load());
            return new CustomSkins.InstalledMod(built.PakPath ?? throw new InvalidOperationException("Nothing was built."));
        }

        /// <summary>What the Mobs tab put on this copied mesh, for opening the tab the way it was left.</summary>
        public static CustomItems.InstalledLook? installedLook(string meshAssetPath)
        {
            var design = designOfMesh(meshAssetPath);
            if (design?.Model?.File == null) { return null; }
            if (!File.Exists(design.Model.File)) { return new CustomItems.InstalledLook(null, design.Model.transform, null, nameOf(design), true); }
            try
            {
                var model = GlbModel.read(design.Model.File);
                return new CustomItems.InstalledLook(model, design.Model.transform, null, model.Name, false);
            }
            catch (Exception) { return new CustomItems.InstalledLook(null, design.Model.transform, null, nameOf(design), true); }
        }

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
