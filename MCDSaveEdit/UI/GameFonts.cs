using MCDSaveEdit.Logic;
using MCDSaveEdit.Services;
using System;
using System.IO;
using System.Windows.Media;
#nullable enable

namespace MCDSaveEdit.UI
{
    /// <summary>
    /// The game's own fonts, for anything drawn over the game.
    ///
    /// Read from the player's own game files rather than carried in the app: each is a FontFace
    /// asset whose .ufont - an ordinary OpenType file - the pak reader files as the package's bulk
    /// data. Written out once beside the app's other data and loaded from there. Where the game
    /// files are not loaded, an ordinary font stands in, so nothing is ever drawn in nothing.
    ///
    ///   Minecraft Ten   - the titles ("CREEPER WOODS"), big numbers
    ///   Minecraft Five  - the small capitals of buttons and headings ("EXPAND")
    ///   Minecraft Seven - body text
    /// </summary>
    public static class GameFonts
    {
        private static readonly (string asset, string family)[] FONTS =
        {
            ("MinecraftTen", "Minecraft Ten"),
            ("MinecraftFive", "Minecraft Five"),
            ("MinecraftSeven", "Minecraft Seven"),
        };

        private static string folder => Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MCDReborn", "Fonts");

        private static bool _ready;

        public static FontFamily Ten => family("Minecraft Ten");
        public static FontFamily Five => family("Minecraft Five");
        public static FontFamily Seven => family("Minecraft Seven");

        private static FontFamily family(string name)
        {
            ensure();
            var file = Path.Combine(folder, name.Replace(" ", "") + ".otf");
            return File.Exists(file)
                ? new FontFamily(new Uri(folder + Path.DirectorySeparatorChar), "./#" + name)
                : new FontFamily("Segoe UI");
        }

        private static void ensure()
        {
            if (_ready) { return; }
            _ready = true;
            try
            {
                Directory.CreateDirectory(folder);
                foreach (var (asset, name) in FONTS)
                {
                    var file = Path.Combine(folder, name.Replace(" ", "") + ".otf");
                    if (File.Exists(file)) { continue; }
                    var package = CustomSkins.index?.extractPackage("/Dungeons/Content/Fonts/" + asset);
                    var bytes = package?.UBulk?.ToArray();
                    //An OpenType file starts OTTO; anything else is not what this expects.
                    if (bytes == null || bytes.Length < 4 || bytes[0] != 'O' || bytes[1] != 'T' || bytes[2] != 'T' || bytes[3] != 'O') { continue; }
                    File.WriteAllBytes(file, bytes);
                }
            }
            catch (Exception problem)
            {
                Console.WriteLine($"[fonts] the game's fonts could not be written out: {problem.Message}");
            }
        }
    }
}
