using MCDSaveEdit.Save.Models.Enums;
using MCDSaveEdit.Save.Models.Profiles;
using System;
using System.Collections.Generic;
using System.Linq;
#nullable enable

namespace MCDSaveEdit.Logic
{
    /// <summary>
    /// Mystery Armor's three rolled attributes, and how they cross to MCD Builder.
    ///
    /// Mystery Armor is the one armor whose properties are not fixed, so it is the one armor
    /// whose properties have to travel with the build. Every other piece of gear is fully
    /// described by its name.
    ///
    /// The two ends name the same thing differently. A save holds an armor property id -
    /// "SuperbDamageAbsorption" - while MCD Builder holds the effect as the wiki words it -
    /// "35% damage reduction". Neither can be derived from the other: the app's own description
    /// strings are templates with the number left as "{0}", so they cannot be matched against
    /// a filled-in effect, and the ids do not read the way the effects do. So the pairing below
    /// is written out rather than computed.
    ///
    /// Two of those pairs are traps, and both were settled before this file existed, in
    /// ArmorDefaults, against real armor pulled out of save files:
    ///
    ///   "35% damage reduction"  is SuperbDamageAbsorption, NOT DamageAbsorption
    ///   the ink cloud           is SquidRollLimited; the game has a second id, SquidRollQuick,
    ///                           with the same wording, and nothing on hand tells them apart.
    ///                           Matching ArmorDefaults matters more than the coin flip does:
    ///                           it keeps the Defaults button and this agreeing.
    ///
    /// Six armor property ids are deliberately unmapped - DamageAbsorption, HealingAura,
    /// Heavyweight, ImmunityBoost, SquidRollQuick and TeleportChance. They exist on other armor
    /// but are not in Mystery Armor's roll pool, so they have no effect string to become.
    /// </summary>
    public static class MysteryAttributes
    {
        /// <summary>The item type in a save; its display name is MCD Builder's "Mystery Armor".</summary>
        public const string ITEM_TYPE = "MysteryArmor";

        /// <summary>Mystery Armor rolls three, and MCD Builder always carries three.</summary>
        public const int SLOTS = 3;

        //Anything here can roll in any of the three slots.
        private static readonly (string id, string effect)[] SHARED = {
            ("IncreasedArrowBundleSize", "+10 arrows per bundle"),
            ("AreaHeal", "Health potions heal nearby allies"),
            ("SuperbDamageAbsorption", "35% damage reduction"),
            ("MeleeAttackSpeedBoost", "+25% melee attack speed"),
            ("ItemDamageBoost", "+50% artifact damage"),
            ("SoulGatheringBoost", "+50% souls gathered"),
            ("SlowResistance", "+50% freezing resistance"),
            ("LifeStealAura", "6% life steal aura"),
            ("PotionCooldownDecrease", "-40% potion cooldown"),
            ("MeleeDamageBoost", "+30% melee damage"),
            ("AllyDamageBoost", "+20% weapon damage boost aura"),
            ("DodgeSpeedIncrease", "50% faster roll"),
            ("MoveSpeedAura", "+15% movespeed aura"),
            ("PetBat", "Gives the player a pet bat"),
            ("MissChance", "30% chance to negate hits"),
            ("ItemCooldownDecrease", "-40% artifact cooldown"),
            ("DodgeInvulnerability", "Brief invulnerability when rolling"),
            ("RangedDamageBoost", "+30% ranged damage"),
            ("DodgeGhostForm", "Briefly gain Ghost Form when rolling"),
            ("SquidRollLimited", "Release an ink cloud when rolling"),
            ("DodgeRoot", "Traps and poisons nearby enemies"),
            ("EmeraldShield", "Invulnerability on emerald collection"),
            ("EnvironmentalProtection", "Environment Damage Resistance"),
            ("FallResistance", "75% Environmental Damage Reduction"),
            ("ItemCooldownReset", "Reset artifacts cooldown on potion use"),
            ("Beekeeper", "30% chance of summoning bees on hit (max 3)"),
            ("InstantTransmission", "Roll to teleport"),
            ("Resonant", "+30% positive status effect duration"),
        };

        //Drawbacks. The game only ever rolls these into the third slot, and MCD Builder only
        //offers them there.
        private static readonly (string id, string effect)[] THIRD_SLOT_ONLY = {
            ("IncreasedMobTargeting", "Mobs target the player more frequently"),
            ("MoveSpeedReduction", "-10% movement speed"),
            ("DodgeCooldownIncrease", "100% longer roll cooldown"),
        };

        private static readonly Dictionary<string, string> _effectById =
            SHARED.Concat(THIRD_SLOT_ONLY).ToDictionary(x => x.id, x => x.effect, StringComparer.OrdinalIgnoreCase);

        private static readonly Dictionary<string, string> _idByEffect =
            SHARED.Concat(THIRD_SLOT_ONLY).ToDictionary(x => x.effect, x => x.id, StringComparer.OrdinalIgnoreCase);

        private static readonly HashSet<string> _drawbacks =
            new HashSet<string>(THIRD_SLOT_ONLY.Select(x => x.id), StringComparer.OrdinalIgnoreCase);

        /// <summary>Every armor property id this map knows, for checking it against the game's own list.</summary>
        public static IEnumerable<string> allIds() => _effectById.Keys;

        /// <summary>The effect string MCD Builder uses for an armor property, or null when it does not roll on Mystery Armor.</summary>
        public static string? effectFor(string? propertyId)
        {
            if (string.IsNullOrWhiteSpace(propertyId)) { return null; }
            return _effectById.TryGetValue(propertyId!, out var effect) ? effect : null;
        }

        /// <summary>The armor property id behind an effect string, or null when nothing matches it.</summary>
        public static string? idForEffect(string? effect)
        {
            if (string.IsNullOrWhiteSpace(effect)) { return null; }
            return _idByEffect.TryGetValue(effect!.Trim(), out var id) ? id : null;
        }

        public static bool isMysteryArmor(Item? item)
            => string.Equals(item?.Type, ITEM_TYPE, StringComparison.OrdinalIgnoreCase);

        /// <summary>
        /// The three attributes as MCD Builder words them, with null for an empty slot.
        ///
        /// Order is the save's own, not sorted: the roll has an order and the two ends should
        /// show the same armor. An id with no effect string is passed over as an empty slot
        /// rather than guessed at.
        /// </summary>
        public static IReadOnlyList<string?> effectsFor(Item? item)
        {
            var effects = new string?[SLOTS];
            if (!isMysteryArmor(item) || item!.Armorproperties == null) { return effects; }

            var slot = 0;
            foreach (var property in item.Armorproperties)
            {
                if (slot >= SLOTS) { break; }
                if (property?.Id == null) { continue; }
                effects[slot++] = _effectById.TryGetValue(property.Id, out var effect) ? effect : null;
            }
            return effects;
        }

        /// <summary>
        /// The attributes as armor properties again.
        ///
        /// A drawback outside the third slot is dropped, because the game cannot roll one there
        /// and MCD Builder will not show one there either: reading the same link the same way
        /// the builder does is worth more than salvaging an impossible roll.
        /// </summary>
        public static IReadOnlyList<Armorproperty> propertiesFrom(IEnumerable<string?> effects)
        {
            var properties = new List<Armorproperty>();
            var slot = 0;
            foreach (var effect in effects.Take(SLOTS))
            {
                var index = slot++;
                if (effect == null) { continue; }
                if (!_idByEffect.TryGetValue(effect.Trim(), out var id)) { continue; }
                if (_drawbacks.Contains(id) && index != SLOTS - 1) { continue; }

                //Common, the rarity every rolled attribute carries; Unique belongs to the one
                //property that makes a unique armor unique, and Mystery Armor has no such thing.
                properties.Add(new Armorproperty { Id = id, Rarity = Rarity.Common });
            }
            return properties;
        }
    }
}
