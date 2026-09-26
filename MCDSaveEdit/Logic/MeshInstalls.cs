using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
#nullable enable

namespace MCDSaveEdit.Logic
{
    /// <summary>
    /// What the Weapons and Mob Import tabs installed on each of the game's meshes: the model, if
    /// one was imported, where the sliders put it, and the pak it went into.
    ///
    /// The installed mesh is in a mod pak, which the app's index of the game leaves out, and the
    /// .glb it was built from was never kept - so the tab could only ever show the game's own mesh
    /// again, and changing a placement meant importing and lining it up from scratch. Kept here,
    /// the tab opens a mesh the way it was left. A custom item keeps the same thing in its design
    /// (CustomItems.ModelEdit), so it is not recorded here.
    ///
    /// A record whose pak has since been removed - Installed Mods' Remove, or by hand - is what
    /// is no longer installed, and is forgotten rather than shown.
    /// </summary>
    public static class MeshInstalls
    {
        public sealed class Record
        {
            public string Catalogue { get; set; } = "";
            public string AssetPath { get; set; } = "";
            public string Pak { get; set; } = "";
            public CustomItems.ModelEdit Edit { get; set; } = new();
            /// <summary>The imported model's own name, for saying what is shown.</summary>
            public string? ModelName { get; set; }
        }

        private static string folder
        {
            get
            {
                var at = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MCDReborn", "MeshInstalls");
                Directory.CreateDirectory(at);
                return at;
            }
        }

        private static string file => Path.Combine(folder, "installs.json");

        private static List<Record> load()
        {
            try
            {
                return File.Exists(file)
                    ? JsonSerializer.Deserialize<List<Record>>(File.ReadAllText(file)) ?? new List<Record>()
                    : new List<Record>();
            }
            catch (Exception) { return new List<Record>(); }
        }

        private static void save(List<Record> records)
            => File.WriteAllText(file, JsonSerializer.Serialize(records, new JsonSerializerOptions { WriteIndented = true }));

        private static bool same(Record r, string catalogue, string assetPath)
            => string.Equals(r.Catalogue, catalogue, StringComparison.Ordinal)
                && string.Equals(r.AssetPath, assetPath, StringComparison.OrdinalIgnoreCase);

        /// <summary>
        /// Remembers an install. Never throws: forgetting what was installed costs the next preview,
        /// and failing the install over it would cost the install.
        /// </summary>
        public static void remember(string catalogue, string assetPath, GlbModel? model, MeshEdit.Transform transform, string pakPath)
        {
            try
            {
                var records = load();
                var old = records.FirstOrDefault(r => same(r, catalogue, assetPath));
                records.RemoveAll(r => same(r, catalogue, assetPath));

                string? kept = null;
                if (model?.Source != null)
                {
                    //Named after the asset, so one mesh never has two files and a reinstall replaces its own.
                    var name = catalogue + "_" + string.Concat(assetPath.Trim('/').Select(c => char.IsLetterOrDigit(c) ? c : '_'));
                    kept = Path.Combine(folder, name + ".glb");
                    File.WriteAllBytes(kept, model.Source);
                }
                else if (old?.Edit.File != null && File.Exists(old.Edit.File))
                {
                    File.Delete(old.Edit.File);
                }

                records.Add(new Record
                {
                    Catalogue = catalogue,
                    AssetPath = assetPath,
                    Pak = pakPath,
                    Edit = CustomItems.ModelEdit.of(transform, kept),
                    ModelName = model?.Name,
                });
                save(records);
            }
            catch (Exception e)
            {
                Services.EventLogger.logError($"Could not remember the install on {assetPath}: {e.Message}");
            }
        }

        /// <summary>What is installed on this mesh, or null when nothing this app remembers is.</summary>
        public static Record? recall(string catalogue, string assetPath)
        {
            var records = load();
            var record = records.FirstOrDefault(r => same(r, catalogue, assetPath));
            if (record == null) { return null; }
            if (File.Exists(record.Pak)) { return record; }

            //Removed since: forget it, and its copy of the model with it.
            try
            {
                if (record.Edit.File != null && File.Exists(record.Edit.File)) { File.Delete(record.Edit.File); }
                records.Remove(record);
                save(records);
            }
            catch (Exception) { }
            return null;
        }
    }
}
