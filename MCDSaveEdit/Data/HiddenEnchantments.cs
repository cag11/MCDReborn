using MCDSaveEdit.Services;
using System;
using System.Collections.Generic;
using System.Linq;
#nullable enable

namespace MCDSaveEdit.Data
{
    /// <summary>
    /// Enchantments the game carries but never offers.
    ///
    /// This app finds enchantments by looking for icons: an id exists because
    /// `.../Components/Enchantments/&lt;Name&gt;/T_&lt;Name&gt;_Icon` exists (see PakContentResolver).
    /// That is a sound rule for everything the game lets you pick, because anything selectable
    /// has to be drawn. It is also the reason a whole set of real enchantments was invisible
    /// here: they were never meant to be chosen, so nobody drew them.
    ///
    /// Counted in the paks on hand: 162 folders under Components/Enchantments, 118 with an icon
    /// - which is exactly the 118 this app listed - and 44 without. The ones below are from that
    /// 44. Each has a folder of its own and a BP_ component named after it, which is the same
    /// shape every working enchantment has; the folder name is the id, the same way
    /// VoidTouchedRanged and JunglePoisonMelee are ids.
    ///
    /// Excluded from that 44 on purpose:
    ///   Cues, Wavs          audio folders that happen to sit under Enchantments. Not enchantments.
    ///   Synergy             one shared particle system, no component of its own.
    ///   Poisoned            the shared poison status; PoisonedMelee and PoisonedRanged are the
    ///                       real enchantments and both are already listed.
    ///   PoisonQuill         the jungle abomination's projectile, a mob effect.
    ///   BardIdle,           internal state effects rather than anything gear carries.
    ///   BardUnique1Idle,
    ///   PlayerIdle
    ///
    /// They all land in the Other category, which is where the enchantments no gear can roll
    /// already live, and where nothing filters them away.
    ///
    /// Two things they do not get, because the game never made them: an icon, so the picker
    /// falls back to its generic enchantment image, and a translated name, so the friendly names
    /// below stand in for what the locres does not hold. The names are the community's, not the
    /// game's - there is no official text for something the game does not show.
    /// </summary>
    public static class HiddenEnchantments
    {
        /// <summary>The id as the save records it, and something readable to show for it.</summary>
        private static readonly (string id, string name)[] ENTRIES = {
            ("WitherEnchantmentMelee", "Withering (Melee)"),
            ("WitherEnchantmentRanged", "Withering (Ranged)"),
            ("Quick", "Quick"),
            ("FastAttack", "Fast Attack"),
            ("Huge", "Huge"),
            ("ResurrectSurroundingMobs", "Mob Resurrection Aura"),
            ("BowsBoon", "Bow's Boon"),
            ("CaveSpiderPoisonEnchantment", "Cave Spider Poison"),
            ("SpongeStrike", "Sponge Strike"),
            ("HeavyweightEnchantment", "Heavy Weight"),
            ("Blind", "Blind"),
            ("DamageCounter", "Damage Counter"),
            ("ProjectileCounter", "Projectile Counter"),
            ("Invisible", "Invisible"),
            ("ShulkerSentry", "Shulker Sentry"),
            ("VoidBlast", "Void Blast"),
            ("ShadowBarbRanged", "Shadow Barb (Ranged)"),

            //Bows that carry their own effect. The Cog Crossbow one is the Pride of the Piglins.
            ("HuntingBowEnchantment", "Hunting Bow"),
            ("HuntingBowTaggedEnchantment", "Hunting Bow (Tagged)"),
            ("SlowBowEnchantment", "Slow Bow"),
            ("SlowBowFreezing", "Slow Bow Freezing"),
            ("WindBowEnchantment", "Wind Bow"),
            ("CogCrossbowEnchantment", "Cog Crossbow"),
            ("ChargingAcceleration", "Charging Acceleration"),
            ("FreezingRanged", "Freezing (Ranged)"),
            ("FreezingAoe", "Freezing Area"),

            //The immunities. Each one answers a hazard a DLC introduced, so they do nothing in
            //a game that never shows you that hazard.
            ("SlowResistance", "Slow Resistance"),
            ("SlowImmunity", "Slow Immunity"),
            ("CurrentImmunity", "Current Immunity"),
            ("WindResistance", "Wind Resistance"),
            ("WindImmunity", "Wind Immunity"),
            ("PushVolumeImmunity", "Push Volume Immunity"),
            ("UnderwaterImmunity", "Underwater Immunity"),
            ("VoidStrikeImmunity", "Void Strike Immunity"),

            //Not on the list this came from, found in the same sweep and the same shape.
            ("DoubleDamage", "Double Damage"),
            ("PassiveRegen", "Passive Regeneration"),
        };

        private static readonly Dictionary<string, string> _names =
            ENTRIES.ToDictionary(entry => entry.id, entry => entry.name, StringComparer.OrdinalIgnoreCase);

        public static IEnumerable<string> ids => ENTRIES.Select(entry => entry.id);

        /// <summary>
        /// Adds them to the list the pickers read.
        ///
        /// Called once the paks have been scanned, so the count reported for what was found in
        /// the game stays honest about what the game actually drew. A HashSet makes this safe to
        /// call again, and safe for an id that does turn out to have an icon after all: the game
        /// keeps winning.
        /// </summary>
        public static void register()
        {
            foreach (var id in ids) { EnchantmentDatabase.allEnchantments.Add(id); }
        }

        /// <summary>The readable name for one of these, or null when it is not one of them.</summary>
        public static string? nameFor(string? id)
        {
            if (string.IsNullOrWhiteSpace(id)) { return null; }
            return _names.TryGetValue(id!, out var name) ? name : null;
        }

        public static bool isHidden(string? id) => nameFor(id) != null;
    }
}
