using MCDSaveEdit.Services;
using System;
using System.Collections.Generic;
using System.Linq;
#nullable enable

namespace MCDSaveEdit.Logic
{
    /// <summary>
    /// A switch that makes every armour stop drawing.
    ///
    /// Its own pak, separate from any skin, because it is its own decision. Welding it to
    /// "apply a skin" meant you could not change your mind without reapplying, and could not
    /// hide armour at all unless you happened to want a custom skin too.
    ///
    /// No geometry is touched. Armour materials honour alpha - that is why a Wolf Armour sheet
    /// is 77% clear and the hero shows through it - so an armour sheet that is entirely clear
    /// has nothing left to draw. The gear is still equipped and still counts for everything;
    /// it simply is not rendered.
    ///
    /// The name matters. Paks mount in filename order and the last one wins, so this has to
    /// sort after every other mod this app writes - otherwise a gear skin from Recolor Gear
    /// would put back the very armour this blanks. "~" is 0x7E, above every letter, which is
    /// the same reason the community's mods folder is called "~mods".
    /// </summary>
    public static class ArmourVisibility
    {
        /// <summary>Becomes MCDReborn_~HideArmour_P.pak, which sorts after everything else here.</summary>
        public const string MOD_NAME = "~HideArmour";

        /// <summary>Raised when the switch is thrown, so every screen showing it can agree.</summary>
        public static event Action? changed;

        /// <summary>The pak, if it is installed.</summary>
        public static CustomSkins.InstalledMod? installed()
            => CustomSkins.installed()
                .FirstOrDefault(mod => !mod.Manual && string.Equals(mod.Name, MOD_NAME, StringComparison.OrdinalIgnoreCase));

        public static bool armourHidden => installed() != null;

        /// <summary>Every armour texture in the game, each one only once.</summary>
        public static IReadOnlyList<string> allArmourTextures()
        {
            return ItemDatabase.armor
                .Select(CustomSkins.textureFor)
                .Where(path => path != null)
                .Select(path => path!)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        /// <summary>
        /// Hides or shows every armour. Hiding writes the pak; showing deletes it, which is the
        /// whole of the undo - the game's own files were never touched.
        /// </summary>
        public static void setHidden(bool hide)
        {
            var existing = installed();

            if (!hide)
            {
                if (existing != null) { CustomSkins.remove(existing); }
                changed?.Invoke();
                return;
            }

            if (existing == null)
            {
                var textures = allArmourTextures()
                    .Select(path => (path, (Func<int, int, byte[]>)CustomSkins.invisiblePixels));
                CustomSkins.applyMany(textures, MOD_NAME);
            }
            changed?.Invoke();
        }
    }
}
