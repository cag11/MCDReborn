using MCDSaveEdit.Services;
using System;
using System.Globalization;
using System.IO;
using System.Linq;
#nullable enable

namespace MCDSaveEdit.Logic
{
    /// <summary>
    /// Copies a save file aside before the editor overwrites it.
    ///
    /// The README has always told people to keep backups by hand, which is a poor thing to ask
    /// of a tool that can corrupt a character in one click. This does it for them.
    ///
    /// Exactly one backup is kept per save file: the new one is written first, and only then
    /// are older ones removed, so there is never a moment with no backup at all. The timestamp
    /// is in the name because "when" is the only thing worth knowing about a backup, and
    /// keeping one means they cannot pile up.
    /// </summary>
    public static class SaveBackup
    {
        public const string BACKUP_EXTENSION = ".bak";

        /// <summary>
        /// Backs up <paramref name="filePath"/> if it exists, then prunes older backups of it.
        /// Returns the new backup's path, or null when there was nothing to back up.
        ///
        /// Never throws: a save must not be blocked because the folder is read-only or the
        /// disk is full. A failure is logged and the save goes ahead.
        /// </summary>
        public static string? backup(string? filePath)
        {
            if (string.IsNullOrWhiteSpace(filePath)) { return null; }

            try
            {
                //Nothing to preserve when saving to a path that does not exist yet.
                if (!File.Exists(filePath)) { return null; }

                var backupPath = pathFor(filePath!, DateTime.Now);
                File.Copy(filePath!, backupPath, overwrite: true);
                Console.WriteLine($"Backed up to {Path.GetFileName(backupPath)}");

                prune(filePath!, keep: backupPath);
                return backupPath;
            }
            catch (Exception e)
            {
                //Losing the backup is bad; losing the save because the backup failed is worse.
                EventLogger.logError($"Could not back up {filePath}: {e.Message}");
                return null;
            }
        }

        /// <summary>`character.dat` becomes `character.dat.2026-09-14_163012.bak`.</summary>
        public static string pathFor(string filePath, DateTime when)
        {
            var stamp = when.ToString("yyyy-MM-dd_HHmmss", CultureInfo.InvariantCulture);
            return $"{filePath}.{stamp}{BACKUP_EXTENSION}";
        }

        /// <summary>
        /// Removes every backup of this save file except <paramref name="keep"/>.
        ///
        /// Matched on the save file's own name, so two characters in the same folder do not
        /// delete each other's backups.
        /// </summary>
        public static void prune(string filePath, string? keep)
        {
            var directory = Path.GetDirectoryName(filePath);
            if (string.IsNullOrEmpty(directory)) { return; }

            var pattern = $"{Path.GetFileName(filePath)}.*{BACKUP_EXTENSION}";
            foreach (var existing in Directory.EnumerateFiles(directory!, pattern).ToList())
            {
                if (keep != null && string.Equals(existing, keep, StringComparison.OrdinalIgnoreCase)) { continue; }
                try { File.Delete(existing); }
                catch (Exception e) { EventLogger.logError($"Could not remove old backup {existing}: {e.Message}"); }
            }
        }
    }
}
