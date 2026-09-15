using System;
using System.Collections.Generic;
using System.Linq;
#nullable enable

namespace MCDSaveEdit.Logic
{
    /// <summary>
    /// The handful of things MCD Builder calls by a different name than this app does.
    ///
    /// Everything crosses the boundary as a display name, and almost all of them already agree:
    /// checked name by name against the builder's own data, 222 of 224 gear, 46 of 46 artifacts
    /// and 104 of 108 enchantments match outright, allowing for the tolerance both ends apply
    /// (apostrophes and a bracketed suffix are ignored on either side). What is left is this
    /// map, and without it a name that does not match is simply dropped, silently, which is how
    /// a bow's third enchantment went missing on the way in.
    ///
    /// Only pairs that are the same thing under two names belong here. Where one side has an
    /// entry the other has never heard of, there is nothing to map to and a guess would put the
    /// wrong item in someone's save. Those are left alone, and there are not many:
    ///
    ///   only MCD Builder has  Curious Armor; the Emerald Shield, Environmental Protection and
    ///                         Swarm Resistance armor enchantments; Totem of Soul Protection
    ///   only this app has     Corrupted Crossbow, and the enchantments built into unique gear
    ///                         rather than chosen - Barrier, Regeneration, Shielding, Knockback,
    ///                         Heals Allies, Poison, Reliable Ricochet, Thrive Under Pressure
    /// </summary>
    public static class BuilderNames
    {
        /// <summary>
        /// Void Shot is the ranged half of a pair the game's own text does not separate: this
        /// app reads both out of the game as "Void Strike" and tells them apart with its own
        /// "(Melee)" and "(Ranged)" suffix, while MCD Builder uses the two names the wiki uses.
        /// Stripping the suffix is not enough, because "Void Strike" in the ranged slot is not
        /// something the builder has.
        /// </summary>
        private static readonly (string id, string builder)[] ENCHANTMENTS = {
            ("VoidTouchedRanged", "Void Shot"),
        };

        /// <summary>
        /// The Halloween variant of Grim Armor. The game calls it The Spooky Gourdian and the
        /// wiki calls it Gourdian Armor. It is the only armor left unmatched on either side
        /// once every other name is paired off, which is what settles it.
        /// </summary>
        private static readonly (string id, string builder)[] ITEMS = {
            ("GrimArmor_Spooky2", "Gourdian Armor"),
        };

        private static readonly Dictionary<string, string> _enchantmentToBuilder =
            ENCHANTMENTS.ToDictionary(x => x.id, x => x.builder, StringComparer.OrdinalIgnoreCase);
        private static readonly Dictionary<string, string> _enchantmentFromBuilder =
            ENCHANTMENTS.ToDictionary(x => x.builder, x => x.id, StringComparer.OrdinalIgnoreCase);

        private static readonly Dictionary<string, string> _itemToBuilder =
            ITEMS.ToDictionary(x => x.id, x => x.builder, StringComparer.OrdinalIgnoreCase);
        private static readonly Dictionary<string, string> _itemFromBuilder =
            ITEMS.ToDictionary(x => x.builder, x => x.id, StringComparer.OrdinalIgnoreCase);

        /// <summary>The name MCD Builder knows this enchantment by, or null when the two agree.</summary>
        public static string? enchantmentName(string? id)
            => id != null && _enchantmentToBuilder.TryGetValue(id, out var name) ? name : null;

        /// <summary>The enchantment id behind a name only MCD Builder uses, or null.</summary>
        public static string? enchantmentId(string? builderName)
            => builderName != null && _enchantmentFromBuilder.TryGetValue(builderName.Trim(), out var id) ? id : null;

        /// <summary>The name MCD Builder knows this item by, or null when the two agree.</summary>
        public static string? itemName(string? type)
            => type != null && _itemToBuilder.TryGetValue(type, out var name) ? name : null;

        /// <summary>The item type behind a name only MCD Builder uses, or null.</summary>
        public static string? itemType(string? builderName)
            => builderName != null && _itemFromBuilder.TryGetValue(builderName.Trim(), out var type) ? type : null;
    }
}
