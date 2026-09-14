using MCDSaveEdit.Save.Models.Enums;
using MCDSaveEdit.Save.Models.Profiles;
using System;
using System.Collections.Generic;
using System.Linq;
#nullable enable

namespace MCDSaveEdit.Data
{
    /// <summary>
    /// The armor properties each armor drops with in game.
    ///
    /// The paks do carry this, but not anywhere this app can read it: the per-armor data lives
    /// in Blueprint assets (`BP_ArchersStrappings` and friends), and PakReader here decodes
    /// textures and locres only. So the list below is transcribed from the wiki:
    /// https://minecraft.fandom.com/wiki/Minecraft_Dungeons:Armor
    ///
    /// The wiki describes properties by their effect ("35% damage reduction"), not by id, and
    /// the ids do not always read the way the effect does. Rather than guess, every effect was
    /// matched against real armor pulled out of actual save files, which is where two traps
    /// showed up:
    ///
    ///   "35% damage reduction"        is SuperbDamageAbsorption, NOT DamageAbsorption
    ///   "-30% negative status effect" is ImmunityBoost ("Resilience"), while the positive
    ///                                 one, "+30% positive status effect", is Resonant
    ///                                 ("Harmony") - the display names invite the opposite
    ///
    /// Order and rarity follow the same saves: on a unique armor the property that makes it
    /// unique comes first and is Unique rarity; the properties shared with its base armor
    /// follow, Common. Effects the wiki lists that are actually built-in *enchantments* -
    /// Goat Gear's extra roll, Ember Robe's Burning, Frost Bite's snowy companion - are not
    /// properties and are deliberately absent.
    ///
    /// MysteryArmor is deliberately absent too: its whole point is that its properties vary.
    /// </summary>
    public static class ArmorDefaults
    {
        private static readonly Rarity C = Rarity.Common;
        private static readonly Rarity U = Rarity.Unique;

        private static (string id, Rarity rarity)[] props(params (string, Rarity)[] entries) => entries;

        private static readonly IReadOnlyDictionary<string, (string id, Rarity rarity)[]> DEFAULTS =
            new Dictionary<string, (string, Rarity)[]>(StringComparer.OrdinalIgnoreCase) {
                ["ArchersStrappings"]         = props(("IncreasedArrowBundleSize", C), ("RangedDamageBoost", C)),
                ["ArchersStrappings_Unique1"] = props(("MoveSpeedAura", U), ("IncreasedArrowBundleSize", C), ("RangedDamageBoost", C)),

                ["AssassinArmor"]            = props(("MeleeAttackSpeedBoost", C)),
                ["AssassinArmor_Unique1"]    = props(("LifeStealAura", U), ("MeleeAttackSpeedBoost", C)),

                ["BardsGarb"]                = props(("AreaHeal", C), ("Resonant", C)),
                ["BardsGarb_Unique1"]        = props(("ImmunityBoost", U), ("AreaHeal", C), ("Resonant", C)),

                ["BattleRobe"]               = props(("ItemCooldownDecrease", C), ("MeleeDamageBoost", C)),
                ["BattleRobe_Unique1"]       = props(("ItemDamageBoost", U), ("ItemCooldownDecrease", C), ("MeleeDamageBoost", C)),

                ["BeenestArmor"]             = props(("AreaHeal", C), ("Beekeeper", C)),
                ["BeenestArmor_Unique1"]     = props(("SuperbDamageAbsorption", U), ("AreaHeal", C), ("Beekeeper", C)),

                ["ChampionsArmor"]           = props(("SuperbDamageAbsorption", C), ("PotionCooldownDecrease", C), ("IncreasedMobTargeting", C)),
                ["ChampionsArmor_Unique1"]   = props(("AreaHeal", U), ("SuperbDamageAbsorption", C), ("PotionCooldownDecrease", C), ("IncreasedMobTargeting", C)),

                ["ClimbingGear"]             = props(("ItemCooldownDecrease", C), ("Heavyweight", C)),
                //Rugged adds environmental resistance and freezing resistance; the "Environmental
                //Protection" it also lists is the built-in enchantment of the same name.
                ["ClimbingGear_Unique1"]     = props(("EnvironmentalProtection", U), ("SlowResistance", U), ("ItemCooldownDecrease", C), ("Heavyweight", C)),
                ["ClimbingGear_Unique2"]     = props(("ItemCooldownDecrease", C), ("Heavyweight", C)),

                ["CowardsArmor"]             = props(("ItemCooldownDecrease", C), ("IncreasedArrowBundleSize", C)),

                ["DarkArmor"]                = props(("SuperbDamageAbsorption", C), ("SoulGatheringBoost", C)),
                ["DarkArmor_Unique1"]        = props(("AllyDamageBoost", U), ("SuperbDamageAbsorption", C), ("SoulGatheringBoost", C)),

                //"Chance to spawn emeralds when exploring" is the Lucky Explorer enchantment.
                ["EmeraldArmor"]             = props(("MeleeAttackSpeedBoost", C)),
                ["EmeraldArmor_Unique1"]     = props(("EmeraldShield", U), ("MeleeAttackSpeedBoost", C)),
                ["EmeraldArmor_Unique2"]     = props(("MeleeAttackSpeedBoost", C)),

                ["EndRobes"]                 = props(("InstantTransmission", C), ("SoulGatheringBoost", C)),
                ["EndRobes_Unique1"]         = props(("InstantTransmission", C), ("SoulGatheringBoost", C)),

                ["EvocationRobe"]            = props(("ItemCooldownDecrease", C), ("MoveSpeedAura", C)),
                ["EvocationRobe_Unique1"]    = props(("ItemCooldownDecrease", C), ("MoveSpeedAura", C)),
                ["EvocationRobe_Unique2"]    = props(("ItemCooldownDecrease", C), ("MoveSpeedAura", C)),

                ["FullPlateArmor"]           = props(("MissChance", C), ("SuperbDamageAbsorption", C), ("DodgeCooldownIncrease", C)),
                ["FullPlateArmor_Unique1"]   = props(("MeleeDamageBoost", U), ("MissChance", C), ("SuperbDamageAbsorption", C), ("DodgeCooldownIncrease", C)),
                ["FullPlateArmor_Spooky2"]   = props(("MeleeDamageBoost", U), ("MissChance", C), ("SuperbDamageAbsorption", C), ("DodgeCooldownIncrease", C)),

                ["GhostArmor"]               = props(("DodgeGhostForm", C), ("MissChance", C)),
                ["GhostArmor_Unique1"]       = props(("DodgeGhostForm", C), ("MissChance", C)),
                ["GhostArmor_Spooky2"]       = props(("DodgeGhostForm", C), ("MissChance", C)),

                ["GrimArmor"]                = props(("SoulGatheringBoost", C), ("LifeStealAura", C)),
                ["GrimArmor_Unique1"]        = props(("SuperbDamageAbsorption", U), ("SoulGatheringBoost", C), ("LifeStealAura", C)),
                ["GrimArmor_Spooky2"]        = props(("SuperbDamageAbsorption", U), ("SoulGatheringBoost", C), ("LifeStealAura", C)),

                ["MercenaryArmor"]           = props(("SuperbDamageAbsorption", C), ("AllyDamageBoost", C)),
                ["MercenaryArmor_Unique1"]   = props(("MeleeAttackSpeedBoost", U), ("SuperbDamageAbsorption", C), ("AllyDamageBoost", C)),
                ["MercenaryArmor_Spooky1"]   = props(("MeleeAttackSpeedBoost", U), ("SuperbDamageAbsorption", C), ("AllyDamageBoost", C)),
                ["MercenaryArmor_Spooky2"]   = props(("MeleeAttackSpeedBoost", U), ("SuperbDamageAbsorption", C), ("AllyDamageBoost", C)),

                ["NatureArmor"]              = props(("ItemCooldownReset", C), ("AreaHeal", C)),
                ["NatureArmor_Unique1"]      = props(("ItemCooldownReset", C), ("AreaHeal", C)),

                ["OcelotArmor"]              = props(("DodgeSpeedIncrease", C), ("SuperbDamageAbsorption", C)),
                ["OcelotArmor_Unique1"]      = props(("DodgeInvulnerability", U), ("DodgeSpeedIncrease", C), ("SuperbDamageAbsorption", C)),

                ["PhantomArmor"]             = props(("SoulGatheringBoost", C), ("RangedDamageBoost", C)),
                ["PhantomArmor_Unique1"]     = props(("SoulGatheringBoost", C), ("RangedDamageBoost", C)),

                ["PiglinArmor"]              = props(("ItemCooldownReset", C), ("ItemDamageBoost", C)),
                ["PiglinArmor_Unique1"]      = props(("ItemCooldownReset", C), ("ItemDamageBoost", C)),

                ["ReinforcedMail"]           = props(("SuperbDamageAbsorption", C), ("MissChance", C), ("DodgeCooldownIncrease", C)),
                ["ReinforcedMail_Unique1"]   = props(("SuperbDamageAbsorption", C), ("MissChance", C), ("DodgeCooldownIncrease", C)),

                ["ScaleMail"]                = props(("SuperbDamageAbsorption", C), ("MeleeDamageBoost", C)),
                ["ScaleMail_Unique1"]        = props(("SuperbDamageAbsorption", C), ("MeleeDamageBoost", C)),

                //Deflect and Thrive Under Pressure are built-in enchantments, not properties.
                ["ShulkerArmor"]             = props(("IncreasedMobTargeting", C)),
                ["ShulkerArmor_Unique1"]     = props(("IncreasedMobTargeting", C)),

                ["SnowArmor"]                = props(("SuperbDamageAbsorption", C), ("SlowResistance", C)),
                ["SnowArmor_Unique1"]        = props(("SuperbDamageAbsorption", C), ("SlowResistance", C)),

                ["SoulRobe"]                 = props(("SoulGatheringBoost", C), ("ItemDamageBoost", C)),
                ["SoulRobe_Unique1"]         = props(("MissChance", U), ("SoulGatheringBoost", C), ("ItemDamageBoost", C)),

                ["SpelunkersArmor"]          = props(("AllyDamageBoost", C), ("PetBat", C)),
                ["SpelunkersArmor_Unique1"]  = props(("ItemDamageBoost", U), ("AllyDamageBoost", C), ("PetBat", C)),
                ["SpelunkersArmor_Year1"]    = props(("ItemDamageBoost", U), ("AllyDamageBoost", C), ("PetBat", C)),

                ["SproutArmor"]              = props(("AreaHeal", C), ("DodgeRoot", C)),
                ["SproutArmor_Unique1"]      = props(("AreaHeal", C), ("DodgeRoot", C)),

                //SquidRollLimited over SquidRollQuick: the game has two ids and the wiki gives
                //both squid armors the same "release an ink cloud when rolling", so which is
                //which cannot be told apart from the outside. Neither appears in any save on
                //hand to settle it.
                ["SquidArmor"]               = props(("SquidRollLimited", C), ("MoveSpeedAura", C)),
                ["SquidArmor_Unique1"]       = props(("DodgeInvulnerability", U), ("SquidRollLimited", C), ("MoveSpeedAura", C)),

                //HealingAura for "+25% healing boost" by elimination - AreaHeal is confirmed as
                //"health potions heal nearby allies", and nothing else is left for a heal boost.
                ["TurtleArmor"]              = props(("SuperbDamageAbsorption", C), ("HealingAura", C)),
                ["TurtleArmor_Unique1"]      = props(("SuperbDamageAbsorption", C), ("HealingAura", C)),

                ["WolfArmor"]                = props(("AllyDamageBoost", C), ("AreaHeal", C)),
                ["WolfArmor_Unique1"]        = props(("MissChance", U), ("AllyDamageBoost", C), ("AreaHeal", C)),
                ["WolfArmor_Unique2"]        = props(("AllyDamageBoost", C), ("AreaHeal", C)),
                ["WolfArmor_Winter1"]        = props(("MissChance", U), ("AllyDamageBoost", C), ("AreaHeal", C)),
            };

        /// <summary>Every armor id this table knows, for tests to check against the game's own list.</summary>
        public static IEnumerable<string> knownArmor => DEFAULTS.Keys;

        /// <summary>Every armor property id this table uses, likewise.</summary>
        public static IEnumerable<string> usedProperties =>
            DEFAULTS.Values.SelectMany(x => x.Select(y => y.id)).Distinct();

        /// <summary>
        /// Fresh <see cref="Armorproperty"/> instances for an armor type, or null where the
        /// table has nothing for it - a weapon, Mystery Armor, or a type added by a game
        /// update this build predates.
        /// </summary>
        public static Armorproperty[]? forItemType(string? itemType)
        {
            if (itemType == null) { return null; }
            if (!DEFAULTS.TryGetValue(itemType, out var entries)) { return null; }

            //New instances every time: these go onto an item and are edited from there.
            return entries
                .Select(x => new Armorproperty { Id = x.id, Rarity = x.rarity })
                .ToArray();
        }
    }
}
