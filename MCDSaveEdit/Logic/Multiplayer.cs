using MCDSaveEdit.Services;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
#nullable enable

namespace MCDSaveEdit.Logic
{
    /// <summary>
    /// Multiplayer mode: every mod put aside so the game starts as shipped and online play works, and
    /// every one put back when it ends.
    ///
    /// Put aside is MOVED, not deleted: to a shelf beside the paks folder (Dungeons\Content\
    /// MCDReborn_ModsOff), which the game never mounts. The shelf holds three folders mirroring where
    /// each file came from - Paks (this app's MCDReborn_*_P.pak and any other pak that is not the
    /// game's own pakchunk), ~mods (the whole folder, however deep), and Win64 (the plugin DLL, only
    /// when it is ours, and its item list). The shelf existing IS the mode being on, for the game copy
    /// that is loaded; nothing else records it, so it cannot disagree with the disk.
    ///
    /// The game without the plugin forgets anything it does not know: custom items leave the
    /// inventory, gem sockets and mastery lines become "Unset", talent points and mastery progress are
    /// dropped. So turning it on first copies every Characters folder aside, and turning it off offers
    /// that copy back.
    /// </summary>
    public static class Multiplayer
    {
        private const string SHELF = "MCDReborn_ModsOff";
        private const string PAKS = "Paks";
        private const string MODS = "~mods";
        private const string BINARIES = "Win64";
        private const string BACKUP_NOTE = "saves-backup.txt";

        public static string? shelfOf(string? paksFolder)
        {
            if (string.IsNullOrWhiteSpace(paksFolder)) { return null; }
            var content = Path.GetDirectoryName(paksFolder.TrimEnd('\\', '/'));
            return content == null ? null : Path.Combine(content, SHELF);
        }

        public static string? shelf => shelfOf(CustomSkins.paksFolder);

        /// <summary>Whether the loaded game copy has its mods put aside.</summary>
        public static bool isOn
        {
            get
            {
                try { var here = shelf; return here != null && Directory.Exists(here); }
                catch (Exception) { return false; }
            }
        }

        /// <summary>
        /// Refuses a write into the game while its mods are put aside: a pak or plugin written now
        /// would be read by the next start and break online play again, and the one put aside would
        /// overwrite it on the way back. Everything that installs into the game goes through here.
        /// </summary>
        public static void refuseWhileOn()
        {
            if (isOn) { throw new InvalidOperationException(R.MULTIPLAYER_BLOCKS); }
        }

        /// <summary>Whether `path` is inside the loaded copy's game folder (Dungeons\...), where refuseWhileOn applies.</summary>
        public static bool isInGame(string path)
        {
            var paks = CustomSkins.paksFolder;
            if (paks == null) { return false; }
            var game = Path.GetDirectoryName(Path.GetDirectoryName(paks.TrimEnd('\\', '/')));
            if (game == null) { return false; }
            var full = Path.GetFullPath(path);
            return full.StartsWith(Path.GetFullPath(game).TrimEnd('\\') + "\\", StringComparison.OrdinalIgnoreCase);
        }

        public sealed record Result(int Paks, int Mods, int Plugin, string? Backup);

        /// <summary>The game's own paks: pakchunkN-WindowsNoEditor.pak and their .sig files, on every store.</summary>
        private static bool isGames(string file) => Path.GetFileName(file).StartsWith("pakchunk", StringComparison.OrdinalIgnoreCase);

        /// <summary>A mod in the paks folder: this app's (whatever its ending, .pak.off too) or anybody's pak.</summary>
        private static bool isMod(string file)
        {
            if (isGames(file)) { return false; }
            var name = Path.GetFileName(file);
            if (name.StartsWith(CustomSkins.MOD_PREFIX, StringComparison.OrdinalIgnoreCase)) { return true; }
            var extension = Path.GetExtension(file).ToLowerInvariant();
            return extension == ".pak" || extension == ".sig" || extension == ".utoc" || extension == ".ucas";
        }

        /// <summary>Puts every mod aside, after copying the saves aside. The game must be closed.</summary>
        public static Result turnOn()
        {
            var paks = CustomSkins.paksFolder ?? throw new InvalidOperationException("No game paks folder is loaded.");
            var here = shelfOf(paks)!;
            if (Directory.Exists(here)) { throw new InvalidOperationException(R.MULTIPLAYER_ALREADY); }

            //The saves first: if they cannot be copied, nothing has moved yet.
            var backup = backUpSaves();

            Directory.CreateDirectory(here);
            File.WriteAllText(Path.Combine(here, BACKUP_NOTE), backup ?? string.Empty);

            int movedPaks = 0, movedMods = 0, movedPlugin = 0;
            foreach (var file in Directory.EnumerateFiles(paks).Where(isMod).ToList())
            {
                move(file, Path.Combine(here, PAKS, Path.GetFileName(file)));
                movedPaks++;
            }

            var mods = Path.Combine(paks, MODS);
            if (Directory.Exists(mods))
            {
                foreach (var file in Directory.EnumerateFiles(mods, "*", SearchOption.AllDirectories).ToList())
                {
                    move(file, Path.Combine(here, MODS, Path.GetRelativePath(mods, file)));
                    movedMods++;
                }
            }

            var binaries = GamePlugin.gameFolder(paks);
            if (binaries != null)
            {
                var dll = Path.Combine(binaries, GamePlugin.dllName(binaries));
                if (GamePlugin.isOurs(dll)) { move(dll, Path.Combine(here, BINARIES, Path.GetFileName(dll))); movedPlugin++; }
                var list = Path.Combine(binaries, GamePlugin.ITEMS_NAME);
                if (File.Exists(list)) { move(list, Path.Combine(here, BINARIES, GamePlugin.ITEMS_NAME)); movedPlugin++; }
            }

            Journal.note($"multiplayer: on - {movedPaks} pak(s), {movedMods} file(s) from {MODS} and {movedPlugin} plugin file(s) put aside in {here}; saves copied to {backup ?? "(no saves found)"}");
            return new Result(movedPaks, movedMods, movedPlugin, backup);
        }

        /// <summary>
        /// Puts every mod back where it came from and removes the shelf. A file already back in
        /// the game's folder (put there by hand meanwhile) is replaced by the one put aside.
        /// </summary>
        public static Result turnOff()
        {
            var paks = CustomSkins.paksFolder ?? throw new InvalidOperationException("No game paks folder is loaded.");
            var here = shelfOf(paks)!;
            if (!Directory.Exists(here)) { return new Result(0, 0, 0, null); }

            int backPaks = 0, backMods = 0, backPlugin = 0;
            var shelvedPaks = Path.Combine(here, PAKS);
            if (Directory.Exists(shelvedPaks))
            {
                foreach (var file in Directory.EnumerateFiles(shelvedPaks).ToList())
                {
                    move(file, Path.Combine(paks, Path.GetFileName(file)));
                    backPaks++;
                }
            }

            var shelvedMods = Path.Combine(here, MODS);
            if (Directory.Exists(shelvedMods))
            {
                var mods = Path.Combine(paks, MODS);
                foreach (var file in Directory.EnumerateFiles(shelvedMods, "*", SearchOption.AllDirectories).ToList())
                {
                    move(file, Path.Combine(mods, Path.GetRelativePath(shelvedMods, file)));
                    backMods++;
                }
            }

            var shelvedPlugin = Path.Combine(here, BINARIES);
            var binaries = GamePlugin.gameFolder(paks);
            if (Directory.Exists(shelvedPlugin) && binaries != null)
            {
                foreach (var file in Directory.EnumerateFiles(shelvedPlugin).ToList())
                {
                    move(file, Path.Combine(binaries, Path.GetFileName(file)));
                    backPlugin++;
                }
            }

            string? backup = null;
            try { backup = File.ReadAllText(Path.Combine(here, BACKUP_NOTE)).Trim(); } catch (Exception) { }
            if (string.IsNullOrWhiteSpace(backup) || !Directory.Exists(backup)) { backup = null; }

            //Only when everything is out: a file that would not move keeps the mode on, and says so.
            if (Directory.EnumerateFiles(here, "*", SearchOption.AllDirectories).Any(f => !Path.GetFileName(f).Equals(BACKUP_NOTE, StringComparison.OrdinalIgnoreCase)))
            {
                throw new IOException(string.Format(R.MULTIPLAYER_LEFT_BEHIND, here));
            }
            Directory.Delete(here, recursive: true);

            Journal.note($"multiplayer: off - {backPaks} pak(s), {backMods} file(s) into {MODS} and {backPlugin} plugin file(s) put back");
            return new Result(backPaks, backMods, backPlugin, backup);
        }

        /// <summary>How many files are put aside, for the Mods tab to say.</summary>
        public static int shelved()
        {
            try
            {
                var here = shelf;
                return here == null || !Directory.Exists(here) ? 0
                    : Directory.EnumerateFiles(here, "*", SearchOption.AllDirectories).Count(f => !Path.GetFileName(f).Equals(BACKUP_NOTE, StringComparison.OrdinalIgnoreCase));
            }
            catch (Exception) { return 0; }
        }

        /// <summary>
        /// Every Characters folder on this PC copied to %LOCALAPPDATA%\MCDReborn\SaveBackups\
        /// before-multiplayer-&lt;time&gt;\&lt;account&gt;. Null when there are no saves to copy.
        /// </summary>
        private static string? backUpSaves()
        {
            var folders = GameInstalls.characterFolders();
            if (folders.Count == 0) { return null; }
            var root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MCDReborn", "SaveBackups",
                "before-multiplayer-" + DateTime.Now.ToString("yyyy-MM-dd_HH-mm-ss"));
            foreach (var characters in folders)
            {
                var account = Path.GetFileName(Path.GetDirectoryName(characters)!) ?? "account";
                var into = Path.Combine(root, account, "Characters");
                Directory.CreateDirectory(into);
                foreach (var file in Directory.EnumerateFiles(characters))
                {
                    File.Copy(file, Path.Combine(into, Path.GetFileName(file)), overwrite: true);
                }
            }
            return root;
        }

        /// <summary>The saves copied when multiplayer mode went on, copied back over the account folders they came from.</summary>
        public static int restoreSaves(string backup)
        {
            var restored = 0;
            foreach (var characters in GameInstalls.characterFolders())
            {
                var account = Path.GetFileName(Path.GetDirectoryName(characters)!);
                var from = Path.Combine(backup, account ?? "", "Characters");
                if (!Directory.Exists(from)) { continue; }
                foreach (var file in Directory.EnumerateFiles(from))
                {
                    File.Copy(file, Path.Combine(characters, Path.GetFileName(file)), overwrite: true);
                    restored++;
                }
            }
            return restored;
        }

        private static void move(string from, string to)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(to)!);
            File.Move(from, to, overwrite: true);
        }
    }
}
