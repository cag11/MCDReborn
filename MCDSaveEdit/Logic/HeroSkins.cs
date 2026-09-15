using MCDSaveEdit.Services;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows.Media.Imaging;
#nullable enable

namespace MCDSaveEdit.Logic
{
    /// <summary>
    /// Putting a Minecraft skin on the hero.
    ///
    /// This is the one place in the game where an ordinary Minecraft skin fits. Armour is
    /// sculpted geometry, so a skin painted for the player's boxes lands stretched and broken on
    /// it - proven in game, not assumed. The hero underneath is the Minecraft shape, and the
    /// hero's own texture is a 64x64 sheet on the standard unwrap: measured seam continuity of
    /// 0.52 against 0.47 for armour, which is the same authoring.
    ///
    /// So no mesh is generated and none needs to be. The skin replaces T_(Hero)_Skin, exactly
    /// the texture swap this app already does.
    ///
    /// The catch is that armour covers most of the hero. The answer is not to reshape the
    /// armour but to stop it drawing: armour materials honour alpha - that is why a Wolf Armour
    /// sheet is 77% clear and the hero shows through it - so an armour sheet that is entirely
    /// clear has nothing left to draw, and the whole hero is visible. Geometry untouched, one
    /// texture swap, same machinery.
    /// </summary>
    public static class HeroSkins
    {
        private const string SKINS_FOLDER = "/Master/Skins/";
        private const string SKIN_SUFFIX = "_Skin";

        public sealed class Hero
        {
            /// <summary>The folder name, which is also what the save's Skin field holds.</summary>
            public string Id { get; }
            public string Asset { get; }

            public Hero(string id, string asset)
            {
                Id = id;
                Asset = asset;
            }

            /// <summary>"CowOnesie" reads better as "Cow Onesie".</summary>
            public string Name => System.Text.RegularExpressions.Regex
                .Replace(Id, "(?<=[a-z0-9])(?=[A-Z])", " ")
                .Replace("_", " ");
        }

        /// <summary>
        /// Every hero skin the loaded content carries, by name.
        ///
        /// Found by looking rather than by a hardcoded list: the game gained skins across six
        /// DLCs and a list written today would be wrong after the next one.
        /// </summary>
        public static IReadOnlyList<Hero> all()
        {
            var paks = CustomSkins.index;
            if (paks == null) { return Array.Empty<Hero>(); }

            var found = new Dictionary<string, Hero>(StringComparer.OrdinalIgnoreCase);
            foreach (var entry in paks)
            {
                if (entry.IndexOf(SKINS_FOLDER, StringComparison.OrdinalIgnoreCase) < 0) { continue; }

                var name = System.IO.Path.GetFileName(entry);
                if (!name.StartsWith("T_", StringComparison.Ordinal)) { continue; }
                if (!name.EndsWith(SKIN_SUFFIX, StringComparison.OrdinalIgnoreCase)) { continue; }

                //The folder is the hero's id; the texture is named after it but not always with
                //the same casing, so the folder is the more reliable of the two.
                var folder = System.IO.Path.GetFileName(System.IO.Path.GetDirectoryName(entry) ?? string.Empty);
                if (string.IsNullOrEmpty(folder) || found.ContainsKey(folder)) { continue; }

                found[folder] = new Hero(folder, CustomSkins.assetPath(entry));
            }

            return found.Values.OrderBy(hero => hero.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
        }

        /// <summary>The hero a save is set to, matched against what the content actually has.</summary>
        public static Hero? forSaveValue(string? skinValue)
        {
            if (string.IsNullOrWhiteSpace(skinValue)) { return null; }
            return all().FirstOrDefault(hero => string.Equals(hero.Id, skinValue, StringComparison.OrdinalIgnoreCase));
        }

        public static BitmapSource? preview(Hero hero) => ImageResolver.instance.imageSource(hero.Asset);

        /// <summary>
        /// The name of the pak carrying this hero's skin.
        ///
        /// No "~" here: nothing else this app writes touches a hero texture, so there is no
        /// conflict to win. Hiding armour is a separate pak with its own name, and that one does
        /// need to sort last.
        /// </summary>
        public static string modName(Hero hero) => "Hero_" + CustomSkins.safeName(hero.Id);

        /// <summary>That pak, if it is installed.</summary>
        public static CustomSkins.InstalledMod? installedFor(Hero hero)
        {
            var wanted = modName(hero);
            return CustomSkins.installed()
                .FirstOrDefault(mod => !mod.Manual && string.Equals(mod.Name, wanted, StringComparison.OrdinalIgnoreCase));
        }

        /// <summary>
        /// Writes the pak carrying this hero's skin, and nothing else.
        ///
        /// Whether armour is drawn is a separate switch in its own pak, so a skin can be changed
        /// without touching it and armour can be hidden without wanting a custom skin.
        /// </summary>
        public static CustomSkins.InstalledMod apply(Hero hero, BitmapSource skin)
        {
            var textures = new (string, Func<int, int, byte[]>)[] {
                (hero.Asset, (w, h) => toBgra(skin, w, h)),
            };
            return CustomSkins.applyMany(textures, modName(hero));
        }

        private static byte[] toBgra(BitmapSource image, int width, int height)
        {
            if (image.PixelWidth != width || image.PixelHeight != height)
            {
                throw new InvalidOperationException(
                    $"That skin is {image.PixelWidth}×{image.PixelHeight}; a hero skin is {width}×{height}.");
            }

            BitmapSource source = image.Format == System.Windows.Media.PixelFormats.Bgra32
                ? image
                : new FormatConvertedBitmap(image, System.Windows.Media.PixelFormats.Bgra32, null, 0);
            var stride = width * 4;
            var bytes = new byte[stride * height];
            source.CopyPixels(bytes, stride, 0);
            return bytes;
        }
    }
}
