using MCDSaveEdit.Data;
using PakReader;
using PakReader.Pak;
using PakReader.Parsers;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Microsoft.Win32;

namespace MeshForge
{
    /// <summary>
    /// Getting assets out of the game, and mod paks back in.
    ///
    /// The same route the editor uses: mount the game's paks, unlock them with the key, and read
    /// packages out by path. Kept apart from the editor because this is a workbench - it is meant
    /// to be run, printed at, and thrown away between attempts, which is not how a tab behaves.
    /// </summary>
    public static class GameFiles
    {
        /// <summary>
        /// Where the game is, as the editor already worked out and wrote down.
        ///
        /// Read from the editor's own setting rather than asked for again: anyone running this has
        /// already pointed the editor at their game, and a second prompt for the same folder is a
        /// second thing to get wrong.
        /// </summary>
        public static string? paksFolder(string? given = null)
        {
            if (!string.IsNullOrWhiteSpace(given)) { return given; }

            try
            {
                using var key = Registry.CurrentUser.OpenSubKey(@"SOFTWARE\MCDSaveEdit");
                var saved = key?.GetValue("PakFilesPath") as string;
                if (!string.IsNullOrWhiteSpace(saved) && Directory.Exists(saved)) { return saved; }
            }
            catch (Exception) { }

            return null;
        }

        /// <summary>
        /// The game's own paks, unlocked, leaving out anything this app installed.
        ///
        /// Mods are skipped for the same reason the editor skips them: a mod competes with the
        /// game's pak for the same path, and reading the wrong one means studying your own output
        /// while believing it is the original.
        /// </summary>
        public static PakIndex open(string paksFolder)
        {
            var paks = Directory.EnumerateFiles(paksFolder, "*.pak")
                .Where(path => !Path.GetFileName(path).StartsWith("MCDReborn_", StringComparison.OrdinalIgnoreCase))
                .ToList();
            if (paks.Count == 0) { throw new FileNotFoundException($"No paks in {paksFolder}"); }

            var index = new PakIndex(paks, cacheFiles: true, caseSensitive: false);

            var unlocked = 0;
            foreach (var aes in Secrets.PAKS_AES_KEYS)
            {
                var text = aes.key;
                if (string.IsNullOrWhiteSpace(text)) { continue; }
                var bytes = text.StartsWith("0x", StringComparison.OrdinalIgnoreCase)
                    ? text.Substring(2).ToBytesKey()
                    : text.ToBytesKey();
                unlocked += index.UseKey(bytes);
            }
            if (unlocked == 0) { throw new InvalidOperationException("None of the keys unlocked those paks."); }

            return index;
        }

        /// <summary>Every asset path holding the given text, as the index spells them.</summary>
        public static IReadOnlyList<string> find(PakIndex index, string fragment)
        {
            return index
                .Where(entry => entry.IndexOf(fragment, StringComparison.OrdinalIgnoreCase) >= 0)
                .Select(assetPath)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        /// <summary>
        /// The index spells paths with the mount point still on the front - "//Dungeons/..." or
        /// worse, doubled. The readers want it from the single leading slash.
        /// </summary>
        public static string assetPath(string indexEntry)
        {
            var path = indexEntry.Replace('\\', '/');
            var at = path.IndexOf("//", StringComparison.Ordinal);
            return at < 0 ? path : path.Substring(at + 1);
        }

        /// <summary>
        /// The raw halves of a cooked asset: the header, and the data beside it.
        ///
        /// The editor has its own version of this, but that one sits in a file that also decodes
        /// textures and so drags WPF and Skia along behind it. This needs neither, and a console
        /// tool that pulls in a window toolkit to read a byte array has something wrong with it.
        /// </summary>
        public static PakPackage? read(PakIndex index, string assetPath)
        {
            if (!index.TryGetPackage(assetPath, out var package)) { return null; }
            if (!package.HasExport()) { return null; }
            return package;
        }

        /// <summary>
        /// Writes the pieces back out as a mod pak, at the paths the game keeps them under.
        ///
        /// The pak the game loads is the same shape whatever is inside it, so this is the same
        /// writer the editor uses for skins. What changes is only what is put in it.
        /// </summary>
        public static void writeMod(string outputPath, IEnumerable<(string assetPath, byte[] uasset, byte[] uexp)> assets)
        {
            var entries = new List<MCDSaveEdit.Logic.PakWriter.Entry>();
            foreach (var (path, uasset, uexp) in assets)
            {
                //Inside a pak the path loses its leading slash and keeps the extension, which the
                //index does not carry.
                var inside = path.TrimStart('/');
                entries.Add(new MCDSaveEdit.Logic.PakWriter.Entry(inside + ".uasset", uasset));
                entries.Add(new MCDSaveEdit.Logic.PakWriter.Entry(inside + ".uexp", uexp));
            }

            Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);
            MCDSaveEdit.Logic.PakWriter.write(outputPath, entries);
        }
    }
}
