#nullable enable

using System;

namespace MCDSaveEdit.Data
{
    public static partial class Constants
    {
        //Continuing upstream's numbering rather than restarting at 1.0 - this is a
        //fork, not a new product. AssemblyInfo.cs derives AssemblyVersion from this.
        public const string CURRENT_VERSION_NUMBER = "1.6.8.9";
        //This fork's releases. Config.downloadAsync() follows the redirect and takes
        //the version from the last path segment, so the tag must parse as a Version.
        public const string LATEST_RELEASE_GITHUB_URL = "https://github.com/cag11/MCDReborn/releases/latest";
        public const string UPSTREAM_GITHUB_URL = "https://github.com/CutFlame/MCDSaveEdit";
        public const string LATEST_RELEASE_MOD_URL_FORMAT = "https://www.nexusmods.com/minecraftdungeons/mods/{0}?tab=files";

        // The application's name used for identification in the registry.
        // Deliberately still "MCDSaveEdit" even though the project is now
        // MCD Reborn: renaming it would orphan every existing user's settings
        // under HKCU\Software\MCDSaveEdit - pak path, language and recent files.
        public const string APPLICATION_NAME = "MCDSaveEdit";

        // What the product is called: the assembly title, and so what Windows shows in the
        // exe's file properties. Separate from APPLICATION_NAME above precisely because that
        // one is a registry path and cannot move.
        public const string PRODUCT_NAME = "MCD Reborn";
        public const string PAK_FILE_LOCATION_REGISTRY_KEY = "PakFilesPath";
        public const string LANG_SPECIFIER_REGISTRY_KEY = "LangSpecifier";
        public const int MAX_RECENT_FILES = 10;

        public const int MAXIMUM_INVENTORY_ITEM_COUNT = 300;

        public const int MINIMUM_ENCHANTMENT_TIER = 0;
        public const int MAXIMUM_ENCHANTMENT_TIER = 3;

        public const int MINIMUM_CHARACTER_LEVEL = 1;
        public const int MAXIMUM_CHARACTER_LEVEL = 1_000_000_000;

        public const int MINIMUM_ITEM_LEVEL = 0;
        public const int MAXIMUM_ITEM_LEVEL = 1_000_000_000;

        public const int MAXIMUM_ENCHANTMENT_OPTIONS_PER_ITEM = 9;
        public const string DEFAULT_ENCHANTMENT_ID = "Unset";

        //These are the names found in the character save files
        public const string EMERALD_CURRENCY_NAME = "Emerald";
        public const string GOLD_CURRENCY_NAME = "Gold";
        public const string EYE_OF_ENDER_CURRENCY_NAME = "EyeOfEnder";

        public const string DEFAULT_LANG_SPECIFIER = "en";

        public static readonly Version CURRENT_VERSION = new Version(CURRENT_VERSION_NUMBER);
        public static bool IS_DEBUG {
            get {
#if DEBUG
                return true;
#else
                return false;
#endif
            }
        }

    }
}
