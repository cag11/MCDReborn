using MCDSaveEdit.Services;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Windows.Media;
using System.Windows.Media.Imaging;
#nullable enable

namespace MCDSaveEdit.Logic
{
    /// <summary>
    /// Mythic items: any melee weapon, bow or armour, awakened at the Mythic Forge in the Camp for
    /// emeralds and gold, gains one red bonus line and a red frame wherever the game draws the item.
    ///
    /// Rarity is a byte the game's code switches on (Common, Rare, Unique), so Mythic is not a fourth
    /// rarity: it is the line. An item with a MCDR_Mythic* line is Mythic, and the in-game half
    /// (Tools/loader/build_mythic.py, carried in this exe) draws it so - the frame, the MYTHIC tag, the
    /// line in red - and runs the forge. The lines are property copies with numbers of their own, as
    /// the gems are, so the item plugin applies them.
    /// </summary>
    public static class Mythic
    {
        public const int EMERALDS = 20000;
        public const int GOLD = 1000;

        public const string MELEE = "MCDR_MythicMelee";
        public const string RANGED = "MCDR_MythicRanged";
        public const string ARMOUR = "MCDR_MythicArmor";

        private static string file => Path.Combine(CustomItems.folder, "mythic.txt");

        public static bool isOn => File.Exists(file);

        public static void set(bool on)
        {
            if (on) { File.WriteAllText(file, "on"); }
            else if (File.Exists(file)) { File.Delete(file); }
        }

        /// <summary>
        /// The three bonus lines, each a copy of a game property with its number set: melee and ranged
        /// damage +30% (their sources' own 1.3, factor 1), and damage reduction 20% (DamageAbsorption's
        /// 0.9 is 10%, so twice as far from 1). After Mastery's in the plugin's list, so no earlier
        /// number moves.
        /// </summary>
        public static IReadOnlyList<CustomProperties.Design> properties() => new[]
        {
            line(MELEE, "MeleeDamageBoost", 1),
            line(RANGED, "RangedDamageBoost", 1),
            line(ARMOUR, "DamageAbsorption", 2),
        };

        private static CustomProperties.Design line(string id, string source, double factor) => new()
        {
            Id = id, Source = source, Name = "Mythic", Line = "Mythic: " + Gems.describe(source),
            Active = true, Factor = factor, Neutral = 1,
        };

        //--- the in-game half --------------------------------------------------------------------------
        private const string PANEL_PREFIX = "Mythic";
        private const string PANEL_FOUND_AS = "MCDReborn_Mythic";
        private const string CARRIED = "MythicAssets/";

        /// <summary>
        /// Where the Mythic Forge stands in the Camp (Unreal centimetres) and which way it faces (yaw).
        /// Written into its cooked level on install, so moving it needs no cook.
        /// </summary>
        public static (float x, float y, float z, float yaw) FORGE = (18910f, 10411f, 11402f, 180f);

        /// <summary>
        /// The red frames: shipped as noise (UI/Mythic/T_MCDRebornMythic*) and painted here from the
        /// game's own Unique frame textures, orange turned red, so they match the game exactly.
        /// </summary>
        private static readonly (string ours, string game)[] FRAMES =
        {
            ("T_MCDRebornMythicGearSlot", "/Dungeons/Content/UI/Materials/Inventory2/Slot/v2_gear_unique_slot"),     // an inventory tile's border
            ("T_MCDRebornMythicOverlay", "/Dungeons/Content/UI/Materials/Inventory2/Slot/v2_unique_overlay"),        // its corner glow
            ("T_MCDRebornMythicWorn", "/Dungeons/Content/UI/Materials/Inventory2/Slot/Equipped/unique_slot_color"),  // a worn slot's colour
            ("T_MCDRebornMythicPlate", "/Dungeons/Content/UI/Materials/Rarity/unique_hover_rarity"),               // the plate behind MYTHIC
        };

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

        /// <summary>Puts the forge and the Mythic look in when the switch is on and takes them out when it is off.</summary>
        public static string syncPanel()
        {
            var old = panelInstalled();
            if (old != null) { File.Delete(old); }

            if (!isOn)
            {
                if (old != null && !Loader.stillNeeded()) { Loader.remove(); }
                return old != null ? "Mythic items removed." : "";
            }

            var assembly = typeof(Mythic).Assembly;
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
                return "Mythic items work, but this build carries no Mythic Forge.";
            }

            placeForge(entries);
            paintFrames(entries);
            if (!Loader.isInstalled) { Loader.installBuiltIn(); }
            CustomSkins.writeModPak(PANEL_PREFIX, entries);
            return "Mythic items installed: the Mythic Forge stands in the Camp.";
        }

        private static void placeForge(List<PakWriter.Entry> entries)
        {
            var head = entries.FindIndex(e => e.Path.EndsWith("/Lobby/MythicForge.umap", StringComparison.OrdinalIgnoreCase));
            var data = entries.FindIndex(e => e.Path.EndsWith("/Lobby/MythicForge.uexp", StringComparison.OrdinalIgnoreCase));
            if (head < 0 || data < 0) { return; }
            var package = CookedEdit.read(entries[head].Data, entries[data].Data);
            var moved = CookedEdit.setVector(package, "RelativeLocation", FORGE.x, FORGE.y, FORGE.z);
            var turned = CookedEdit.setVector(package, "RelativeRotation", 0f, FORGE.yaw, 0f);
            if (moved != 1) { Journal.note($"mythic: the forge's level has {moved} position(s), not one; it stands where it was cooked"); }
            if (turned != 1) { Journal.note($"mythic: the forge's level has {turned} rotation(s), not one; it faces as it was cooked"); }
            entries[head] = new PakWriter.Entry(entries[head].Path, package.Header);
            entries[data] = new PakWriter.Entry(entries[data].Path, package.Data);
        }

        private static void paintFrames(List<PakWriter.Entry> entries)
        {
            foreach (var (ours, game) in FRAMES)
            {
                var head = entries.FindIndex(e => e.Path.EndsWith("/UI/Mythic/" + ours + ".uasset", StringComparison.OrdinalIgnoreCase));
                var data = entries.FindIndex(e => e.Path.EndsWith("/UI/Mythic/" + ours + ".uexp", StringComparison.OrdinalIgnoreCase));
                if (head < 0 || data < 0) { continue; }
                BitmapSource? picture = null;
                try { picture = ImageResolver.instance.imageSource(game); }
                catch (Exception) { }
                if (picture == null) { Journal.note($"mythic: the game's {game} was not found; that red frame stays unpainted"); continue; }
                var why = CustomItems.repaint(entries[head].Data, entries[data].Data, null, reddened(picture), out var uexp, out _);
                if (why != null) { Journal.note($"mythic: {ours} was not painted - {why}"); continue; }
                entries[data] = new PakWriter.Entry(entries[data].Path, uexp);
            }
        }

        /// <summary>The game's orange Unique frame turned full red: every pixel that shows, grey or coloured, red at its own brightness.</summary>
        private static BitmapSource reddened(BitmapSource picture)
        {
            var source = new FormatConvertedBitmap(picture, PixelFormats.Bgra32, null, 0);
            int width = source.PixelWidth, height = source.PixelHeight, stride = width * 4;
            var pixels = new byte[stride * height];
            source.CopyPixels(pixels, stride, 0);
            for (var i = 0; i < pixels.Length; i += 4)
            {
                double b = pixels[i] / 255.0, g = pixels[i + 1] / 255.0, r = pixels[i + 2] / 255.0;
                if (pixels[i + 3] == 0) { continue; }
                //Its brightness, lifted a little so a dark grey edge still reads red, on a deep red.
                var bright = Math.Min(1.0, 0.25 + 0.9 * Math.Max(r, Math.Max(g, b)));
                pixels[i + 2] = (byte)Math.Round(bright * 235);
                pixels[i + 1] = (byte)Math.Round(bright * 22);
                pixels[i] = (byte)Math.Round(bright * 18);
            }
            return BitmapSource.Create(width, height, 96, 96, PixelFormats.Bgra32, null, pixels, stride);
        }
    }
}
