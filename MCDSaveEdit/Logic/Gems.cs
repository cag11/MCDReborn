using MCDSaveEdit.Services;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
#nullable enable

namespace MCDSaveEdit.Logic
{
    /// <summary>
    /// Gems, as Diablo has them: seven kinds in three grades, owned in a count like emeralds and
    /// never taking a place in the inventory.
    ///
    /// Each is a currency - an item id the wallet counts. The game keeps a character's balances as a
    /// list of (id, count) in its save data and takes any registered id (tested with the cut
    /// DiamondDust), so a gem only has to be registered: a copy of Gold under its own id, its folder
    /// copied into the New Items pak with its own registry entries (so the game's asset finder never
    /// confuses it with Gold), and the id added by the plugin like any new item.
    ///
    /// On or off by a file, as Apocalypse+ is: <see cref="isOn"/>.
    /// </summary>
    public static class Gems
    {
        public const string PREFIX = "MCDR_Gem";

        /// <summary>The seven, in Diablo's order.</summary>
        public static readonly string[] KINDS = { "Ruby", "Sapphire", "Topaz", "Emerald", "Amethyst", "Diamond", "Skull" };

        /// <summary>The three grades, lowest first: 1, 2 and 3 in an id.</summary>
        public static readonly string[] GRADES = { "Chipped", "Flawless", "Perfect" };

        public static string idOf(string kind, int grade) => $"{PREFIX}{kind}{grade}";

        /// <summary>
        /// Gold, as a source to copy. The registry's item list is built from Instance blueprints -
        /// gear - and a currency has only its Storable, the pickup, so it is spelled out here.
        /// </summary>
        public static RegistryPatch.GameItem gold => new()
        {
            Id = "Gold", Folder = "/Game/Content_DLC4/Actors/Items/Gold", Instance = "BP_GoldStorable", NativeParent = "",
        };

        public static IEnumerable<string> allIds => KINDS.SelectMany(k => Enumerable.Range(1, GRADES.Length).Select(g => idOf(k, g)));

        private static string file => Path.Combine(CustomItems.folder, "gems.txt");

        public static bool isOn => File.Exists(file);

        public static void set(bool on)
        {
            if (on) { File.WriteAllText(file, "on"); }
            else if (File.Exists(file)) { File.Delete(file); }
        }

        /// <summary>Every gem as a copy of Gold, for the New Items pak and the plugin.</summary>
        public static IReadOnlyList<CustomItems.Extra> extras()
            => KINDS.SelectMany(kind => Enumerable.Range(1, GRADES.Length).Select(grade =>
                new CustomItems.Extra(idOf(kind, grade), "Gold", $"{GRADES[grade - 1]} {kind}",
                    $"A {GRADES[grade - 1].ToLowerInvariant()} {kind.ToLowerInvariant()}. Set it in a socket to gain its power.")))
                .ToList();

        /// <summary>
        /// What each gem does in a socket: the armour property it is a copy of. The same on a weapon
        /// and on armour for now.
        /// </summary>
        public static readonly IReadOnlyDictionary<string, string> SOURCES = new Dictionary<string, string>
        {
            ["Ruby"] = "MeleeDamageBoost",
            ["Sapphire"] = "ItemCooldownDecrease",
            ["Topaz"] = "RangedDamageBoost",
            ["Emerald"] = "MeleeAttackSpeedBoost",
            ["Amethyst"] = "ItemDamageBoost",
            ["Diamond"] = "DamageAbsorption",
            ["Skull"] = "LifeStealAura",
        };

        /// <summary>
        /// What each gem does set in armour - Diablo's way, a second effect of the same gem. Its
        /// own line (<see cref="armourOf"/>), so the line under the item's name says the right thing.
        /// </summary>
        public static readonly IReadOnlyDictionary<string, string> ARMOUR_SOURCES = new Dictionary<string, string>
        {
            ["Ruby"] = "HealingAura",
            ["Sapphire"] = "SoulGatheringBoost",
            ["Topaz"] = "DodgeSpeedIncrease",
            ["Emerald"] = "MoveSpeedAura",
            ["Amethyst"] = "AllyDamageBoost",
            ["Diamond"] = "MissChance",
            ["Skull"] = "TeleportChance",
        };

        /// <summary>The line a gem set in armour is.</summary>
        public static string armourOf(string kind, int grade) => $"MCDR_Armour{kind}{grade}";

        /// <summary>The line an empty socket is.</summary>
        public const string SOCKET = "MCDR_SocketEmpty";

        /// <summary>The line a set gem is.</summary>
        public static string socketOf(string kind, int grade) => $"MCDR_Socket{kind}{grade}";

        /// <summary>How strong each grade is, as a share of what its source property does: Chipped, Flawless, Perfect.</summary>
        public static readonly double[] STRENGTH = { 0.4, 0.7, 1.0 };

        /// <summary>
        /// The sources whose number is an amount (0 does nothing) rather than a multiplier (1 does
        /// nothing). DamageAbsorption is a multiplier of the damage taken - 0.9 is "10% less" - so
        /// it is not one; LifeStealAura's 0.06 is 6% of the damage dealt.
        /// </summary>
        private static readonly HashSet<string> AMOUNTS = new() { "LifeStealAura", "HealingAura", "MissChance", "TeleportChance" };

        /// <summary>
        /// Sockets and set gems as property lines, in the order the plugin numbers them: the empty
        /// socket is 41, then each gem's three grades set in a weapon - Ruby 1 is 42, Skull 3 is 62 -
        /// then the same set in armour, 63 to 83. The in-game
        /// screen (Tools/loader/build_gems.py) and the Dungeons stub's EArmorPropertyID are compiled
        /// against these numbers, so they come first, before any property of the user's own, and
        /// never change order.
        ///
        /// The empty socket copies EnvironmentalProtection and does nothing; a gem does what its
        /// source does, and its grade is the line's rarity (Chipped Common, Flawless Rare, Perfect
        /// Unique), which the screen writes.
        /// </summary>
        public static IReadOnlyList<CustomProperties.Design> properties()
        {
            var found = new List<CustomProperties.Design>
            {
                new() { Id = SOCKET, Source = "EnvironmentalProtection", Name = "Empty Socket", Line = "Empty Socket", Active = false },
            };
            foreach (var kind in KINDS)
            {
                for (var grade = 1; grade <= GRADES.Length; grade++)
                {
                    var name = $"{GRADES[grade - 1]} {kind}";
                    found.Add(new CustomProperties.Design
                    {
                        Id = socketOf(kind, grade), Source = SOURCES[kind], Name = name,
                        Line = $"{name}: {describe(SOURCES[kind])}", Active = true,
                        Factor = STRENGTH[grade - 1], Neutral = AMOUNTS.Contains(SOURCES[kind]) ? 0 : 1,
                    });
                }
            }
            foreach (var kind in KINDS)
            {
                for (var grade = 1; grade <= GRADES.Length; grade++)
                {
                    var name = $"{GRADES[grade - 1]} {kind}";
                    var source = ARMOUR_SOURCES[kind];
                    found.Add(new CustomProperties.Design
                    {
                        Id = armourOf(kind, grade), Source = source, Name = name,
                        Line = $"{name}: {describe(source)}", Active = true,
                        Factor = STRENGTH[grade - 1], Neutral = AMOUNTS.Contains(source) ? 0 : 1,
                    });
                }
            }
            return found;
        }

        /// <summary>
        /// The game's own line for a property, "{0}" and all, so the number the game fills in stays
        /// where the game put it. Before the game's strings are loaded, a plain English one.
        /// </summary>
        private static string describe(string source)
        {
            var said = R.armorPropertyDescription(source);
            if (!string.IsNullOrWhiteSpace(said) && said != source && said != source + "_description") { return said; }
            return source switch
            {
                "MeleeDamageBoost" => "{0} melee damage",
                "ItemCooldownDecrease" => "{0} artifact cooldown",
                "RangedDamageBoost" => "{0} ranged damage",
                "MeleeAttackSpeedBoost" => "{0} melee attack speed",
                "ItemDamageBoost" => "{0} artifact damage",
                "DamageAbsorption" => "{0} damage absorbed",
                "LifeStealAura" => "{0} life steal",
                "HealingAura" => "heals you and allies nearby",
                "SoulGatheringBoost" => "{0} souls gathered",
                "DodgeSpeedIncrease" => "{0} dodge speed",
                "MoveSpeedAura" => "{0} movement speed aura",
                "AllyDamageBoost" => "{0} ally damage",
                "MissChance" => "{0} chance for attacks to miss you",
                "TeleportChance" => "{0} chance to teleport when hit",
                _ => source,
            };
        }

        /// <summary>
        /// The Gems panel: K in the Camp or a mission shows every gem and how many are owned.
        ///
        /// A payload like the map table - a Lobby and an Ingame level the loader streams in, each
        /// placing an actor that adds the panel to the screen - carried inside this exe because
        /// nobody using the app has an Unreal editor. Built by Tools/loader/build_gems.py.
        /// </summary>
        private const string PANEL_PREFIX = "Gems";
        private const string PANEL_FOUND_AS = "MCDReborn_Gems";
        private const string CARRIED = "GemsAssets/";

        /// <summary>
        /// Where the Gem Merchant's booth stands in the Camp (Unreal centimetres) and which way it
        /// faces (yaw) - beside the Mystery Merchant. Written into its cooked level on install, as the
        /// map table's position is, so moving it needs no cook.
        /// </summary>
        public static readonly (float x, float y, float z, float yaw) STALL = (19796f, 7910f, 11492f, 180f);   // beside the map table, where the user stood; a block below their feet, or it floats

        /// <summary>The booth's level, edited in place: its placed actor's position and facing.</summary>
        private static void placeStall(List<PakWriter.Entry> entries)
        {
            var head = entries.FindIndex(e => e.Path.EndsWith("/Lobby/GemMerchant.umap", StringComparison.OrdinalIgnoreCase));
            var data = entries.FindIndex(e => e.Path.EndsWith("/Lobby/GemMerchant.uexp", StringComparison.OrdinalIgnoreCase));
            if (head < 0 || data < 0) { return; }
            var package = CookedEdit.read(entries[head].Data, entries[data].Data);
            var moved = CookedEdit.setVector(package, "RelativeLocation", STALL.x, STALL.y, STALL.z);
            var turned = CookedEdit.setVector(package, "RelativeRotation", 0f, STALL.yaw, 0f);
            if (moved != 1) { Journal.note($"gems: the Gem Merchant's level has {moved} position(s), not one; it stands where it was cooked"); }
            if (turned != 1) { Journal.note($"gems: the Gem Merchant's level has {turned} rotation(s), not one; it faces as it was cooked"); }
            entries[head] = new PakWriter.Entry(entries[head].Path, package.Header);
            entries[data] = new PakWriter.Entry(entries[data].Path, package.Data);
        }

        /// <summary>The installed panel pak, or null.</summary>
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

        /// <summary>
        /// Puts the panel in when gems are on and takes it out when they are off. The loader comes
        /// with it, because a level in a pak does nothing without the loader that streams it in.
        /// </summary>
        public static string syncPanel()
        {
            var old = panelInstalled();
            if (old != null) { File.Delete(old); }

            if (!isOn)
            {
                if (old != null && !Talents.isOn && Payloads.installed().Count == 0 && !MapTable.isInstalled) { Loader.remove(); }
                return old != null ? "Gems panel removed." : "";
            }

            if (!Loader.isInstalled) { Loader.installBuiltIn(); }

            var assembly = typeof(Gems).Assembly;
            var entries = new List<PakWriter.Entry>();
            foreach (var name in assembly.GetManifestResourceNames().Where(n => n.StartsWith(CARRIED, StringComparison.Ordinal)))
            {
                using var stream = assembly.GetManifestResourceStream(name)!;
                using var memory = new MemoryStream();
                stream.CopyTo(memory);
                var inside = name.Substring(CARRIED.Length).Replace('\\', '/');
                entries.Add(new PakWriter.Entry("Dungeons/Content/MCDReborn/" + inside, memory.ToArray()));
            }

            placeStall(entries);

            //Two levels, the actor, the panel and 21 pictures, each a header and its data.
            if (entries.Count < 2 * (4 + KINDS.Length * GRADES.Length))
            {
                throw new InvalidOperationException($"This build carries {entries.Count} of the Gems panel's files, not all of them.");
            }

            CustomSkins.writeModPak(PANEL_PREFIX, entries);
            return "Gems panel installed: press K in the Camp or a mission.";
        }
    }
}
