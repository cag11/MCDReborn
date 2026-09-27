using MCDSaveEdit.Data;
using Microsoft.Win32;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
#nullable enable

namespace MCDSaveEdit.Logic
{
    /// <summary>
    /// Every copy of the game on this PC, by where it came from: Steam, the Xbox app and the
    /// Minecraft Launcher. Whichever one the app loads is the one everything targets - mods, the
    /// item plugin, Gems, Talents - because they all follow the loaded paks folder.
    ///
    /// Looked for further than the defaults: Steam in every one of its libraries (libraryfolders.vdf),
    /// the Xbox app's XboxGames on every fixed drive, since both let a game live on another disk.
    /// </summary>
    public static class GameInstalls
    {
        public enum Store { Steam, Xbox, Launcher }

        public sealed record Install(Store Store, string PaksFolder);

        /// <summary>The installs found, one per store at most (the first found), in the menu's order.</summary>
        public static IReadOnlyList<Install> find()
        {
            var found = new List<Install>();
            void add(Store store, IEnumerable<string> candidates)
            {
                var there = candidates.FirstOrDefault(isGame);
                if (there != null) { found.Add(new Install(store, there)); }
            }
            add(Store.Steam, steamCandidates());
            add(Store.Xbox, xboxCandidates());
            add(Store.Launcher, new[] { Constants.LAUNCHER_PAKS_FOLDER_PATH });
            return found;
        }

        /// <summary>Which store a paks folder belongs to, from its path; null for one chosen by hand elsewhere.</summary>
        public static Store? storeOf(string? paksFolder)
        {
            if (string.IsNullOrWhiteSpace(paksFolder)) { return null; }
            var path = paksFolder.Replace('/', '\\');
            if (path.IndexOf("\\steamapps\\", StringComparison.OrdinalIgnoreCase) >= 0) { return Store.Steam; }
            if (path.IndexOf("\\XboxGames\\", StringComparison.OrdinalIgnoreCase) >= 0
                || path.IndexOf("\\WindowsApps\\", StringComparison.OrdinalIgnoreCase) >= 0) { return Store.Xbox; }
            if (path.IndexOf("\\Mojang\\products\\", StringComparison.OrdinalIgnoreCase) >= 0) { return Store.Launcher; }
            return null;
        }

        /// <summary>Whether two paks folders are the same place.</summary>
        public static bool same(string? a, string? b)
            => a != null && b != null
               && string.Equals(Path.GetFullPath(a).TrimEnd('\\', '/'), Path.GetFullPath(b).TrimEnd('\\', '/'), StringComparison.OrdinalIgnoreCase);

        //--- saves ------------------------------------------------------------------------------------
        //
        //Every store keeps its characters in an account folder of the same kind -
        //Saved Games\Mojang Studios\Dungeons\<account id>\Characters (the old Microsoft Store build in
        //its package's LocalCache instead) - and nothing in the folder says which store wrote it; the
        //game's logs do not either. So a store's folder is LEARNT: whichever account folder a save was
        //opened from while that store was loaded. Until one is, the most recently played account
        //folder no other store has claimed is taken.

        private const string SAVES_KEY = "SaveFolder_";

        private static IEnumerable<string> saveRoots()
        {
            yield return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Saved Games", "Mojang Studios", "Dungeons");
            yield return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "Packages", "Microsoft.Lovika_8wekyb3d8bbwe", "LocalCache", "Local", "Dungeons");
        }

        /// <summary>Every account's Characters folder on this PC, the most recently played first.</summary>
        public static IReadOnlyList<string> characterFolders()
        {
            var found = new List<(string path, DateTime played)>();
            foreach (var root in saveRoots())
            {
                try
                {
                    if (!Directory.Exists(root)) { continue; }
                    foreach (var account in Directory.GetDirectories(root))
                    {
                        var characters = Path.Combine(account, "Characters");
                        if (!Directory.Exists(characters)) { continue; }
                        var played = Directory.GetFiles(characters, "*.dat").Select(File.GetLastWriteTimeUtc).DefaultIfEmpty(Directory.GetLastWriteTimeUtc(characters)).Max();
                        found.Add((characters, played));
                    }
                }
                catch (Exception) { }
            }
            return found.OrderByDescending(f => f.played).Select(f => f.path).ToList();
        }

        /// <summary>The Characters folder a save in `file` is in, or null when it is not in one.</summary>
        public static string? charactersFolderOf(string? file)
        {
            if (string.IsNullOrWhiteSpace(file)) { return null; }
            var folder = Path.GetDirectoryName(Path.GetFullPath(file));
            return folder != null && string.Equals(Path.GetFileName(folder), "Characters", StringComparison.OrdinalIgnoreCase) ? folder : null;
        }

        /// <summary>Learns that `store` plays from the Characters folder this save is in.</summary>
        public static void rememberSave(Store? store, string? file)
        {
            var folder = charactersFolderOf(file);
            if (store == null || folder == null) { return; }
            try { RegistryTools.SaveSetting(Constants.APPLICATION_NAME, SAVES_KEY + store, folder); }
            catch (Exception) { }
        }

        private static string? remembered(Store store)
        {
            try
            {
                var said = RegistryTools.GetSetting(Constants.APPLICATION_NAME, SAVES_KEY + store, string.Empty);
                return !string.IsNullOrWhiteSpace(said) && Directory.Exists(said) ? said : null;
            }
            catch (Exception) { return null; }
        }

        /// <summary>
        /// Where `store`'s characters are: the folder learnt for it, else the most recently played one
        /// no other store has, else the most recently played. Null when there are no saves at all.
        /// </summary>
        public static string? saveFolder(Store? store)
        {
            var all = characterFolders();
            if (store == null) { return all.FirstOrDefault(); }
            var mine = remembered(store.Value);
            if (mine != null) { return mine; }
            var others = Enum.GetValues(typeof(Store)).Cast<Store>().Where(s => s != store).Select(remembered).Where(p => p != null).ToList();
            return all.FirstOrDefault(p => !others.Any(o => same(o, p))) ?? all.FirstOrDefault();
        }

        /// <summary>
        /// The loaded version, when it has learnt no folder yet, takes the one it would guess - the
        /// most recently played - so a switch to another version guesses the next one along.
        /// </summary>
        public static void claimLoaded(string? paksFolder)
        {
            var store = storeOf(paksFolder);
            if (store == null || remembered(store.Value) != null) { return; }
            var guess = saveFolder(store);
            if (guess == null) { return; }
            try { RegistryTools.SaveSetting(Constants.APPLICATION_NAME, SAVES_KEY + store, guess); }
            catch (Exception) { }
        }

        /// <summary>A paks folder with the game's first pak in it.</summary>
        private static bool isGame(string? paks)
        {
            try { return !string.IsNullOrWhiteSpace(paks) && File.Exists(Path.Combine(paks, Constants.FIRST_PAK_FILENAME)); }
            catch (Exception) { return false; }
        }

        private const string STEAM_GAME = @"steamapps\common\MinecraftDungeons\Dungeons\Content\Paks";

        /// <summary>The default Steam folder, then every library Steam lists.</summary>
        private static IEnumerable<string> steamCandidates()
        {
            yield return Constants.STEAM_PAKS_FOLDER_PATH;
            foreach (var library in steamLibraries())
            {
                yield return Path.Combine(library, STEAM_GAME);
            }
        }

        private static IEnumerable<string> steamLibraries()
        {
            string? steam = null;
            try { steam = Registry.GetValue(@"HKEY_CURRENT_USER\Software\Valve\Steam", "SteamPath", null) as string; }
            catch (Exception) { }
            if (string.IsNullOrWhiteSpace(steam)) { steam = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "Steam"); }
            var list = Path.Combine(steam!.Replace('/', '\\'), "steamapps", "libraryfolders.vdf");
            var libraries = new List<string> { steam!.Replace('/', '\\') };
            try
            {
                if (File.Exists(list))
                {
                    //"path"		"D:\\SteamLibrary" - backslashes doubled, as the file keeps them.
                    foreach (Match m in Regex.Matches(File.ReadAllText(list), "\"path\"\\s+\"([^\"]+)\""))
                    {
                        libraries.Add(m.Groups[1].Value.Replace("\\\\", "\\"));
                    }
                }
            }
            catch (Exception) { }
            return libraries.Distinct(StringComparer.OrdinalIgnoreCase);
        }

        /// <summary>XboxGames on every fixed drive, the system drive first.</summary>
        private static IEnumerable<string> xboxCandidates()
        {
            yield return Constants.XBOX_PC_GAMES_PAKS_FOLDER_PATH;
            DriveInfo[] drives;
            try { drives = DriveInfo.GetDrives(); }
            catch (Exception) { yield break; }
            foreach (var drive in drives)
            {
                bool usable;
                try { usable = drive.DriveType == DriveType.Fixed && drive.IsReady; }
                catch (Exception) { usable = false; }
                if (usable) { yield return Path.Combine(drive.RootDirectory.FullName, "XboxGames", "Minecraft Dungeons", "Content", "Dungeons", "Content", "Paks"); }
            }
        }
    }
}
