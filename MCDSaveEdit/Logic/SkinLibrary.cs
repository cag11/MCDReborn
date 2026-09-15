using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Windows.Media.Imaging;
#nullable enable

namespace MCDSaveEdit.Logic
{
    /// <summary>
    /// Skins the user has brought in, kept by name so they can be chosen again.
    ///
    /// Importing used to be a file dialog that applied one PNG and forgot it: to wear it again
    /// you went and found the file again, and there was nowhere to see what you had. A skin is
    /// something people collect, so it is kept - spiderman.png becomes "Spiderman", sits in the
    /// list beside the game's own heroes, and is picked the same way.
    ///
    /// The files live under the user's own application data rather than beside the exe, so a
    /// portable copy of the app on a stick does not scatter someone's collection, and an
    /// installed one does not need write access to Program Files.
    /// </summary>
    public static class SkinLibrary
    {
        public sealed class CustomSkin
        {
            public string Name { get; }
            public string Path { get; }

            public CustomSkin(string path)
            {
                Path = path;
                Name = System.IO.Path.GetFileNameWithoutExtension(path);
            }
        }

        public static string folder => System.IO.Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "MCDSaveEditReborn", "Skins");

        /// <summary>Everything imported, by name.</summary>
        public static IReadOnlyList<CustomSkin> all()
        {
            if (!Directory.Exists(folder)) { return Array.Empty<CustomSkin>(); }

            return Directory.EnumerateFiles(folder, "*.png")
                .Select(path => new CustomSkin(path))
                .OrderBy(skin => skin.Name, StringComparer.CurrentCultureIgnoreCase)
                .ToList();
        }

        /// <summary>
        /// Takes a copy of a PNG and keeps it under its own filename.
        ///
        /// Checked before it is kept rather than when it is worn: a sheet of the wrong size can
        /// never be applied, so letting it into the list would only be a promise to fail later.
        /// </summary>
        public static CustomSkin import(string pngPath)
        {
            if (!File.Exists(pngPath)) { throw new FileNotFoundException("No such file.", pngPath); }

            var image = read(pngPath);
            if (image.PixelWidth != 64 || image.PixelHeight != 64)
            {
                throw new InvalidOperationException(
                    $"That image is {image.PixelWidth}×{image.PixelHeight}. A Minecraft skin is 64×64.");
            }

            Directory.CreateDirectory(folder);
            var name = safeName(System.IO.Path.GetFileNameWithoutExtension(pngPath));
            var target = System.IO.Path.Combine(folder, name + ".png");

            //Copied rather than referenced: the file someone picked may be in Downloads and gone
            //next week, and a list of skins that stop working is worse than no list.
            File.Copy(pngPath, target, overwrite: true);
            return new CustomSkin(target);
        }

        public static void remove(CustomSkin skin) => File.Delete(skin.Path);

        public static BitmapSource? preview(CustomSkin skin)
        {
            try { return read(skin.Path); }
            catch { return null; }
        }

        private static BitmapSource read(string path)
        {
            var decoder = new PngBitmapDecoder(
                new Uri(System.IO.Path.GetFullPath(path)),
                BitmapCreateOptions.PreservePixelFormat,
                BitmapCacheOption.OnLoad);
            return decoder.Frames[0];
        }

        /// <summary>
        /// Which skin is currently worn by which hero.
        ///
        /// The pak alone cannot say: it is named after the hero whose texture it replaces, and
        /// its contents are pixels. So the name is written down when the skin is applied, and
        /// only believed while that hero's pak is still installed - a record without a pak is a
        /// leftover, not a fact.
        /// </summary>
        private static string recordPath => System.IO.Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "MCDSaveEditReborn", "worn.txt");

        public static void recordWorn(string heroId, string skinName)
        {
            var all = worn();
            all[heroId] = skinName;
            writeWorn(all);
        }

        public static void forgetWorn(string heroId)
        {
            var all = worn();
            if (all.Remove(heroId)) { writeWorn(all); }
        }

        /// <summary>The skin worn by a hero, or null when it is the game's own art.</summary>
        public static string? wornBy(string heroId)
            => worn().TryGetValue(heroId, out var name) ? name : null;

        public static CustomSkin? find(string name)
            => all().FirstOrDefault(skin => string.Equals(skin.Name, name, StringComparison.OrdinalIgnoreCase));

        private static Dictionary<string, string> worn()
        {
            var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            try
            {
                if (!File.Exists(recordPath)) { return map; }
                foreach (var line in File.ReadAllLines(recordPath))
                {
                    //One tab-separated pair per line: a skin name can hold almost anything else,
                    //and a malformed line should cost that line rather than the file.
                    var at = line.IndexOf('	');
                    if (at <= 0) { continue; }
                    map[line.Substring(0, at)] = line.Substring(at + 1);
                }
            }
            catch { }
            return map;
        }

        private static void writeWorn(Dictionary<string, string> map)
        {
            try
            {
                Directory.CreateDirectory(System.IO.Path.GetDirectoryName(recordPath)!);
                File.WriteAllLines(recordPath, map.Select(pair => pair.Key + "	" + pair.Value));
            }
            catch { }
        }

        /// <summary>Keeps the name the user gave the file, minus anything a path cannot hold.</summary>
        private static string safeName(string name)
        {
            var invalid = System.IO.Path.GetInvalidFileNameChars();
            var cleaned = new string(name.Select(c => invalid.Contains(c) ? '_' : c).ToArray()).Trim();
            return cleaned.Length == 0 ? "Skin" : cleaned;
        }
    }
}
