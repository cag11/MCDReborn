using MCDSaveEdit.Logic;
using MCDSaveEdit.Save.Models.Profiles;
using System;
using System.Collections.Generic;
#nullable enable

namespace MCDSaveEdit.Data
{
    [Flags]
    public enum EnchantmentCategory
    {
        None = 0,
        Melee = 1,
        Armor = 2,
        Ranged = 4,
        /// <summary>
        /// In none of the wiki's three lists: mob-exclusive, unused, or simply unknown to
        /// this table. Always shown, never filtered out.
        /// </summary>
        Other = 8,

        /// <summary>The three real gear categories.</summary>
        Gear = Melee | Armor | Ranged,

        All = Gear | Other,
    }

    /// <summary>
    /// Which gear each enchantment belongs on.
    ///
    /// The paks do not carry this. Enchantments are discovered from icon folder names
    /// (see PakContentResolver), and the icons say nothing about what the enchantment can
    /// be applied to - so this is transcribed from the three tables on the wiki:
    /// https://minecraft.fandom.com/wiki/Minecraft_Dungeons:Enchantment
    ///
    /// One id can be in several categories. The game has separate melee and ranged variants
    /// of a dozen enchantments (CommittedRanged, ProspectorArmor and so on), but they share
    /// a single icon folder, so this app only ever sees one id for the family - "Prospector"
    /// covers all three of the game's variants, and is marked accordingly.
    ///
    /// Other holds the enchantments the wiki lists as mob-exclusive or unused, which no gear
    /// can roll, along with any id this table has never heard of. Those are never filtered
    /// out: hiding an enchantment nothing can select would put it out of reach entirely, and
    /// forcing values onto a save is the point of this app.
    /// </summary>
    public static class EnchantmentCategories
    {
        private const EnchantmentCategory M = EnchantmentCategory.Melee;
        private const EnchantmentCategory A = EnchantmentCategory.Armor;
        private const EnchantmentCategory R = EnchantmentCategory.Ranged;
        private const EnchantmentCategory O = EnchantmentCategory.Other;

        private static readonly IReadOnlyDictionary<string, EnchantmentCategory> CATEGORIES =
            new Dictionary<string, EnchantmentCategory>(StringComparer.OrdinalIgnoreCase) {
                ["Accelerating"] = R,
                ["Acrobat"] = A,
                ["Altruistic"] = O,               //Heals Allies - unused
                ["AnimaConduitMelee"] = M,
                ["AnimaConduitRanged"] = R,
                ["ArtifactCharge"] = R,
                ["Backstabber"] = M,              //Ambush
                ["BagOfSouls"] = A,
                ["BaneOfIllagers"] = M,           //Illager's Bane
                ["Barrier"] = O,                  //unused
                ["BeastBoss"] = A,
                ["BeastBurst"] = A,
                ["BeastSurge"] = A,
                ["BonusShot"] = R,
                ["Burning"] = A,
                ["BurstBowstring"] = R,
                ["BusyBee"] = M,
                ["Celerity"] = A,                 //Cool Down
                ["ChainReaction"] = R,
                ["Chains"] = M,
                ["Chilling"] = A,
                ["Committed"] = M | R,            //CommittedRanged shares the icon
                ["CooldownShot"] = R,
                ["Cowardice"] = A,
                ["CriticalHit"] = M | R,          //CriticalHitRanged shares the icon
                ["DamageSynergy"] = M,            //Artifact Synergy
                ["DeathBarter"] = A,
                ["Deflecting"] = A,               //Deflect
                ["DippingPoison"] = R,
                ["DynamoMelee"] = M,
                ["DynamoRanged"] = R,
                ["Echo"] = M,
                ["Electrified"] = A,
                ["EmeraldDivination"] = A,        //Lucky Explorer
                ["EnigmaResonatorMelee"] = M,
                ["EnigmaResonatorRanged"] = R,
                ["Exploding"] = M | R,            //ExplodingRanged shares the icon
                ["Explorer"] = A,
                ["FinalShout"] = A,
                ["FireAspect"] = M | R,           //FireAspectRanged shares the icon
                ["FireFocus"] = A,
                ["FireTrail"] = A,
                ["Flee"] = A,                     //Rush
                ["FoodReserves"] = A,
                ["Freezing"] = M,
                ["Frenzied"] = A,
                ["FuseShot"] = R,
                ["Gravity"] = R,                  //the ranged one; melee is GravityMelee
                ["GravityMelee"] = M,
                ["GravityPulse"] = A,
                ["Growing"] = R,
                ["GuardingStrike"] = M,
                ["HealthSynergy"] = A,
                ["Infinity"] = R,
                ["JunglePoisonMelee"] = M,        //built into Vine Whip, Encrusted Anchor
                ["JunglePoisonRanged"] = R,
                ["Knockback"] = O,                //unused
                ["Leeching"] = M,
                ["LevitationShot"] = R,
                ["LightningFocus"] = A,
                ["Looting"] = M | R,              //LootingRanged shares the icon
                ["LuckOfTheSea"] = A,
                ["MultiCharge"] = R,              //Overcharge
                ["MultiDodge"] = A,               //Multi-Roll
                ["MultiShot"] = R,                //Multishot
                ["PainCycle"] = M,
                ["Piercing"] = R,
                ["PoisonFocus"] = A,
                ["PoisonedMelee"] = M,            //Poison Cloud
                ["PoisonedRanged"] = R,
                ["PotionFortification"] = A,      //Potion Barrier
                ["PotionThirstMelee"] = M,        //Refreshment
                ["PotionThirstRanged"] = R,
                ["Power"] = R,
                ["Prospector"] = M | R | A,       //the game has all three variants on one icon
                ["Protection"] = A,
                ["Punch"] = R,
                ["RadianceMelee"] = M,
                ["RadianceRanged"] = R,           //Radiance Shot
                ["Rampaging"] = M,
                ["RapidFire"] = R,
                ["Reckless"] = A,
                ["Recycler"] = A,
                ["Regeneration"] = O,             //unused
                ["ReliableRicochet"] = R,         //built into Bubble Burster, Gloopy Bow
                ["ResurrectionSurge"] = A,        //Life Boost
                ["Ricochet"] = R,
                ["RollCharge"] = R,
                ["Rushdown"] = M,                 //built into Tempest Knife and friends
                ["ShadowFeast"] = A,              //Shadow Surge
                ["ShadowFlash"] = A,              //Shadow Blast
                ["ShadowShot"] = R,               //built into the shadow crossbows
                ["SharedPain"] = M,               //built into The Starless Night
                ["Sharpness"] = M,
                ["Shielding"] = O,                //unused
                ["ShockWeb"] = R,
                ["Shockwave"] = M,
                ["Smiting"] = M | R,              //SmitingRanged shares the icon
                ["Snowing"] = A,                  //Snowball
                ["SoulFocus"] = A,
                ["SoulSiphon"] = M | R,           //SoulSiphonRanged shares the icon
                ["SpeedSynergy"] = A,
                ["SpiritSpeed"] = A,              //Soul Speed
                ["Stunning"] = M,
                ["Supercharge"] = R,
                ["SurpriseGift"] = A,
                ["Swiftfooted"] = A,
                ["Swirling"] = M,
                ["TempoTheft"] = R,
                ["Thorns"] = A,
                ["ThriveUnderPressure"] = A,      //built into the shulker armors
                ["Thundering"] = M,
                ["TumbleBee"] = A,                //Tumblebee
                ["Unchanting"] = M | R,           //UnchantingRanged shares the icon
                ["VoidTouchedMelee"] = M,         //Void Strike
                ["VoidTouchedRanged"] = R,
                ["Weakening"] = M | R,            //WeakeningRanged shares the icon
                ["WildRage"] = R,
            };

        /// <summary>
        /// Where an enchantment belongs. An id this table does not know - a future update, or
        /// a modded pak - comes back as Other rather than disappearing.
        /// </summary>
        public static EnchantmentCategory categoriesFor(string? enchantmentId)
        {
            if (enchantmentId == null) { return EnchantmentCategory.Other; }
            return CATEGORIES.TryGetValue(enchantmentId, out var categories)
                ? categories
                : EnchantmentCategory.Other;
        }

        /// <summary>Whether one enchantment passes the categories the user has turned on.</summary>
        public static bool matches(string? enchantmentId, EnchantmentCategory selected)
            => (categoriesFor(enchantmentId) & selected) != EnchantmentCategory.None;

        /// <summary>
        /// The category an item's own enchantments come from, or None where that is not a
        /// single answer - an artifact, or a type the loaded game content does not know.
        /// </summary>
        public static EnchantmentCategory categoryForItem(Item? item)
        {
            if (item == null) { return EnchantmentCategory.None; }
            if (item.isMeleeWeapon()) { return EnchantmentCategory.Melee; }
            if (item.isRangedWeapon()) { return EnchantmentCategory.Ranged; }
            if (item.isArmor()) { return EnchantmentCategory.Armor; }
            return EnchantmentCategory.None;
        }
    }
}
