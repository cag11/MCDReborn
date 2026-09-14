using System;
using System.IO;
using System.Linq;
#nullable enable

namespace MCDSaveEdit.Data
{
    public static partial class Constants
    {
        public const string PAKS_FILTER_STRING = "/Dungeons/Content";

        public const string FIRST_PAK_FILENAME = "pakchunk0-WindowsNoEditor.pak";

        //NOTE: default location of files for Launcher version: %localappdata%\Mojang\products\dungeons\dungeons\Dungeons\Content\Paks
        public static string LAUNCHER_PAKS_FOLDER_PATH {
            get {
                var folderPath = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
                return Path.Combine(folderPath, "Mojang", "products", "dungeons", "dungeons", "Dungeons", "Content", "Paks");
            }
        }

        //NOTE: default location of files for Steam version: C:\Program Files (x86)\Steam\steamapps\common\MinecraftDungeons\Dungeons\Content\Paks
        public static string STEAM_PAKS_FOLDER_PATH {
            get {
                var folderPath = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);
                return Path.Combine(folderPath, "Steam", "steamapps", "common", "MinecraftDungeons", "Dungeons", "Content", "Paks");
            }
        }

        //NOTE: default location of files for XBoxGames version: C:\XboxGames\Minecraft Dungeons\Content\Dungeons\Content\Paks
        public static string XBOX_PC_GAMES_PAKS_FOLDER_PATH {
            get {
                var folderPath = Path.GetPathRoot(Environment.GetFolderPath(Environment.SpecialFolder.Windows));
                return Path.Combine(folderPath, "XboxGames", "Minecraft Dungeons", "Content", "Dungeons", "Content", "Paks");
            }
        }

        // NOTE: the WinStore/UWP lookup that used to live here has been removed.
        // It required a Windows.winmd (WinRT) reference and the only call site in
        // AppModel.contentPathsToCheck() was already disabled, so it was dead code.
        // See commit "Stop checking the WinStore path to be more compatible with Linux".

        //NOTE: default location of save game files: %userprofile%\Saved Games\Mojang Studios\Dungeons\2533274911688652\Characters
        public static string FILE_DIALOG_INITIAL_DIRECTORY {
            get {
                var userFolderPath = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
                return Path.Combine(userFolderPath, "Saved Games", "Mojang Studios", "Dungeons");
            }
        }

        public const string ENCRYPTED_FILE_EXTENSION = ".dat";
        public const string DECRYPTED_FILE_EXTENSION = ".json";
    }
}
