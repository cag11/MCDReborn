using MCDSaveEdit.Services;
using MCDSaveEdit.Data;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
#nullable enable

namespace MCDSaveEdit.Logic
{
    /// <summary>
    /// Weapon mastery: melee weapons and bows earn experience while they are used, and at every
    /// level do more damage; every five levels a star tier (bronze, silver, gold, platinum, full
    /// platinum) adds a second stat.
    ///
    /// All of it lives on the weapon, as property lines the game keeps and shows like any other:
    /// a level line (MCDR_MasteryMelee01..25 / MCDR_MasteryRanged01..25, its text saying what that
    /// level gives) and a progress line (MCDR_MasteryXp0..9, tenths of the way to the next level).
    /// The item plugin earns the experience - a quarter second of damage dealt is a quarter second
    /// of use, credited to the bow while the ranged button is held and to the melee weapon
    /// otherwise - moves the lines on, and applies the bonus through four properties of its own
    /// whose numbers it sets from the worn weapons' levels, as for Talents.
    ///
    /// The in-game half (a star on each weapon's tile, star pictures on the lines, a screen listing
    /// every weapon) is built by Tools/loader/build_mastery.py and carried in this exe.
    /// </summary>
    public static class Mastery
    {
        public const int LEVELS = 25;
        public const int STEPS = 10;

        /// <summary>Per level: +2% weapon damage. Per tier reached: +4% attack speed (melee), +5% roll speed (bows).</summary>
        public const double DAMAGE_PER_LEVEL = 2, SPEED_PER_TIER = 4, ROLL_PER_TIER = 5;

        /// <summary>Seconds of fighting from level n to n+1: BASE + STEP * n (about three hours to 25).</summary>
        public const int SECONDS_BASE = 60, SECONDS_STEP = 30;

        public static readonly string[] TIERS = { "", "Bronze", "Silver", "Gold", "Platinum", "Full Platinum" };

        /// <summary>0 plain (1-4), 1 bronze (5+), 2 silver (10+), 3 gold (15+), 4 platinum (20+), 5 full platinum (25).</summary>
        public static int tierOf(int level) => Math.Min(level / 5, TIERS.Length - 1);

        public static string levelId(bool melee, int level) => $"MCDR_Mastery{(melee ? "Melee" : "Ranged")}{level:00}";
        public static string progressId(int step) => $"MCDR_MasteryXp{step}";

        private static string file => Path.Combine(CustomItems.folder, "mastery.txt");

        public static bool isOn => File.Exists(file);

        public static void set(bool on)
        {
            if (on) { File.WriteAllText(file, "on"); }
            else if (File.Exists(file)) { File.Delete(file); }
        }

        /// <summary>The text a level line shows under the weapon's name.</summary>
        public static string lineOf(bool melee, int level)
        {
            var tier = tierOf(level);
            var head = tier == 0 ? $"Mastery {level}" : $"Mastery {level} ({TIERS[tier]})";
            var damage = $"+{DAMAGE_PER_LEVEL * level:0}% {(melee ? "melee" : "ranged")} damage";
            if (tier == 0) { return $"{head}: {damage}"; }
            var second = melee ? $"+{SPEED_PER_TIER * tier:0}% attack speed" : $"+{ROLL_PER_TIER * tier:0}% roll speed";
            return $"{head}: {damage}, {second}";
        }

        //The game's numbers for the sources, to turn a percent into a factor of the source's own.
        private const double MELEE_DAMAGE = 0.30, MELEE_SPEED = 0.25, RANGED_DAMAGE = 0.30, ROLL_SPEED = 0.50;

        /// <summary>
        /// The lines and the four effects, in the order the plugin numbers them: melee levels,
        /// ranged levels, progress, then the effects. The in-game screen finds them by name, so
        /// where they start does not matter.
        /// </summary>
        public static IReadOnlyList<CustomProperties.Design> properties()
        {
            var found = new List<CustomProperties.Design>();
            foreach (var melee in new[] { true, false })
            {
                for (var level = 1; level <= LEVELS; level++)
                {
                    found.Add(new CustomProperties.Design
                    {
                        Id = levelId(melee, level), Source = "EnvironmentalProtection", Name = $"Mastery {level}",
                        Line = lineOf(melee, level), Active = false,
                    });
                }
            }
            for (var step = 0; step < STEPS; step++)
            {
                found.Add(new CustomProperties.Design
                {
                    Id = progressId(step), Source = "EnvironmentalProtection", Name = "Mastery progress",
                    Line = $"Mastery progress: {step * 100 / STEPS}% to the next level", Active = false,
                });
            }
            foreach (var (id, source) in EFFECTS)
            {
                found.Add(new CustomProperties.Design
                {
                    Id = id, Source = source, Name = "Mastery", Line = "Mastery", Active = true, Factor = 1, Neutral = 1,
                });
            }
            return found;
        }

        private static readonly (string id, string source)[] EFFECTS =
        {
            ("MCDR_MasteryMeleeDamage", "MeleeDamageBoost"), ("MCDR_MasteryMeleeSpeed", "MeleeAttackSpeedBoost"),
            ("MCDR_MasteryRangedDamage", "RangedDamageBoost"), ("MCDR_MasteryRangedRoll", "DodgeSpeedIncrease"),
        };

        /// <summary>The effects, which need classes of their own (the plugin sets their numbers).</summary>
        public static IEnumerable<CustomProperties.Design> effects() => properties().Where(p => p.Factor != null);

        /// <summary>
        /// "@mastery \t melee damage/level \t attack speed/tier \t ranged damage/level \t roll speed/tier
        /// \t seconds base \t seconds step", the percents as factors of each source's own number.
        /// </summary>
        public static IReadOnlyList<string> forPlugin()
        {
            if (!isOn) { return Array.Empty<string>(); }
            string f(double value) => value.ToString("0.#####", CultureInfo.InvariantCulture);
            return new[]
            {
                string.Join("\t", "@mastery",
                    f(DAMAGE_PER_LEVEL / 100 / MELEE_DAMAGE), f(SPEED_PER_TIER / 100 / MELEE_SPEED),
                    f(DAMAGE_PER_LEVEL / 100 / RANGED_DAMAGE), f(ROLL_PER_TIER / 100 / ROLL_SPEED),
                    SECONDS_BASE.ToString(CultureInfo.InvariantCulture), SECONDS_STEP.ToString(CultureInfo.InvariantCulture)),
            }.Concat(families().Select(f => string.Join("	", "@masteryfamily", f.melee ? "m" : "r", f.name, string.Join(",", f.ids)))).ToList();
        }

        /// <summary>The hidden currency a weapon family's mastery is kept in, per character: seconds of use.</summary>
        public static string currencyOf(string family) => "MCDR_Mastery_" + family;

        /// <summary>
        /// Every family's currency, copies of Gold registered like the talents' stores: a character's
        /// mastery of a kind of weapon is kept on the character, so it outlives every weapon of it.
        /// </summary>
        public static IReadOnlyList<CustomItems.Extra> extras()
            => families().Select(f => new CustomItems.Extra(currencyOf(f.name), "Gold", f.name + " mastery",
                "How long this hero has fought with this kind of weapon.")).ToList();

        /// <summary>
        /// Weapon families, which share one mastery: every item id of a kind of weapon - "Claymore",
        /// "Claymore_Unique1" and on - by the part of the id before its first underscore, and each of
        /// this app's own items in the family of the weapon it is a copy of.
        /// </summary>
        public static IReadOnlyList<(bool melee, string name, IReadOnlyList<string> ids)> families()
        {
            static string familyOf(string id) => id.Split('_')[0];
            var found = new Dictionary<string, (bool melee, List<string> ids)>(StringComparer.OrdinalIgnoreCase);
            void add(string id, string family, bool melee)
            {
                if (!found.TryGetValue(family, out var f)) { found[family] = f = (melee, new List<string>()); }
                if (!f.ids.Contains(id, StringComparer.OrdinalIgnoreCase)) { f.ids.Add(id); }
            }
            foreach (var id in ItemDatabase.meleeWeapons.Where(i => !i.StartsWith("MCDR_", StringComparison.Ordinal))) { add(id, familyOf(id), true); }
            foreach (var id in ItemDatabase.rangedWeapons.Where(i => !i.StartsWith("MCDR_", StringComparison.Ordinal))) { add(id, familyOf(id), false); }
            foreach (var mine in CustomItems.pluginItems(CustomItems.load()))
            {
                var family = familyOf(mine.Source);
                if (found.TryGetValue(family, out var f)) { add(mine.Id, family, f.melee); }
            }
            return found.OrderBy(f => f.Key).Select(f => (f.Value.melee, f.Key, (IReadOnlyList<string>)f.Value.ids)).ToList();
        }

        //--- the in-game half --------------------------------------------------------------------------
        private const string PANEL_PREFIX = "Mastery";
        private const string PANEL_FOUND_AS = "MCDReborn_Mastery";
        private const string CARRIED = "MasteryAssets/";

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

        /// <summary>Puts the mastery screens in when mastery is on and takes them out when it is off.</summary>
        public static string syncPanel()
        {
            var old = panelInstalled();
            if (old != null) { File.Delete(old); }

            if (!isOn)
            {
                if (old != null && !Gems.isOn && !Talents.isOn && Payloads.installed().Count == 0 && !MapTable.isInstalled) { Loader.remove(); }
                return old != null ? "Weapon mastery removed." : "";
            }

            var assembly = typeof(Mastery).Assembly;
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
                return "Weapon mastery works, but this build carries no mastery screens.";
            }

            if (!Loader.isInstalled) { Loader.installBuiltIn(); }
            CustomSkins.writeModPak(PANEL_PREFIX, entries);
            return "Weapon mastery installed: press J in the Camp or a mission for every weapon's mastery.";
        }
    }
}
