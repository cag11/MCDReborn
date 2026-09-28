using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
#nullable enable

namespace MCDSaveEdit.Logic
{
    /// <summary>
    /// Enchantment levels IV and V.
    ///
    /// The game keeps an enchantment above III, and what it does keeps scaling: measured live, Celerity
    /// IV divides artifact cooldowns by 1.854 (-46%, where III is -38%). Its enchanting screens only
    /// know I-III, so they draw IV as "I" (the big badge) or nothing (the icons under an item). The
    /// in-game half (Tools/loader/build_enchant_levels.py, carried in this exe) draws IV and V there.
    ///
    /// Buying a level costs enchantment points as I-III do, steeper: a level's cost goes into the
    /// enchantment's invested points, which is what the game takes from the hero's points and gives
    /// back on salvage.
    /// </summary>
    public static class EnchantLevels
    {
        public const int MAX = 5;

        /// <summary>What level IV and V cost on top of III: common enchantments 5 then 8, powerful 7 then 10.</summary>
        public static int costOf(int level, bool powerful) => level switch
        {
            4 => powerful ? 7 : 5,
            5 => powerful ? 10 : 8,
            _ => 0,
        };

        private static string file => Path.Combine(CustomItems.folder, "enchant-levels.txt");

        public static bool isOn => File.Exists(file);

        public static void set(bool on)
        {
            if (on) { File.WriteAllText(file, "on"); }
            else if (File.Exists(file)) { File.Delete(file); }
        }

        //--- the in-game half --------------------------------------------------------------------------
        private const string PANEL_PREFIX = "EnchantLevels";
        private const string PANEL_FOUND_AS = "MCDReborn_EnchantLevels";
        private const string CARRIED = "EnchantLevelsAssets/";

        public static string? panelInstalled()
        {
            foreach (var folder in new[] { CustomSkins.paksFolder, CustomSkins.modsFolder })
            {
                if (folder == null || !Directory.Exists(folder)) { continue; }
                var found = Directory.EnumerateFiles(folder, PANEL_FOUND_AS + "*.pak").FirstOrDefault();
                if (found != null) { return found; }
            }
            return null;
        }

        /// <summary>Puts the IV/V badges in when the switch is on and takes them out when it is off.</summary>
        public static string syncPanel()
        {
            var old = panelInstalled();
            if (old != null) { File.Delete(old); }

            if (!isOn)
            {
                if (old != null && !Loader.stillNeeded()) { Loader.remove(); }
                return old != null ? "Enchantment levels IV and V removed." : "";
            }

            var assembly = typeof(EnchantLevels).Assembly;
            var entries = new List<PakWriter.Entry>();
            foreach (var name in assembly.GetManifestResourceNames().Where(n => n.StartsWith(CARRIED, StringComparison.Ordinal)))
            {
                using var stream = assembly.GetManifestResourceStream(name)!;
                using var memory = new MemoryStream();
                stream.CopyTo(memory);
                entries.Add(new PakWriter.Entry("Dungeons/Content/MCDReborn/" + name.Substring(CARRIED.Length).Replace('\\', '/'), memory.ToArray()));
            }
            if (entries.Count == 0)
            {
                return "Enchantment levels IV and V work, but this build carries no in-game badges for them.";
            }

            if (!Loader.isInstalled) { Loader.installBuiltIn(); }
            CustomSkins.writeModPak(PANEL_PREFIX, entries);
            return "Enchantment levels IV and V installed: the game shows IV and V on enchantments above III.";
        }
    }
}
