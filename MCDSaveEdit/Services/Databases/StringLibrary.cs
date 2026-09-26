using MCDSaveEdit.Data;
using System;
using System.Collections.Generic;
using System.Linq;
#nullable enable

namespace MCDSaveEdit.Services
{
    public class R : Properties.Resources
    {
        private static Dictionary<string, string> _itemType = new Dictionary<string, string>();
        private static Dictionary<string, string> _enchantment = new Dictionary<string, string>();
        private static Dictionary<string, string> _armorProperties = new Dictionary<string, string>();
        private static Dictionary<string, string> _mission = new Dictionary<string, string>();
        private static Dictionary<string, string> _clickys = new Dictionary<string, string>();

        /// <summary>
        /// One table per mission, holding the wording its objectives are allowed to use.
        ///
        /// An objective's "description" is a KEY, and the table it is looked up in is chosen by
        /// the level's own loctable-id: a level saying "creeperwoods" reads "creeperwoodsLabels".
        /// A key that table has not got draws as &lt;MISSING STRING TABLE ENTRY&gt; on the
        /// mission banner, with nothing in any log.
        ///
        /// There are 36 of these and they share almost nothing - the most widely held key of the
        /// lot is in five of them. So there is no safe wording to offer blind; the editor has to
        /// ask which table first.
        /// </summary>
        ///
        /// Kept ORDINAL. Two of these differ only in case - "hypermissionLabels" and
        /// "HyperMissionLabels" are separate tables with different wording behind the same
        /// spelling - so a case-insensitive dictionary throws on the way in and takes the whole
        /// of the game content down with it.
        private static Dictionary<string, Dictionary<string, string>> _labels
            = new Dictionary<string, Dictionary<string, string>>(StringComparer.Ordinal);

        public static bool isStringsLoaded { get; private set; } = false;

        public static int totalStringCount {
            get {
                return _itemType.Count
                    + _enchantment.Count
                    + _armorProperties.Count
                    + _mission.Count
                    + _clickys.Count;
            }
        }

        public static void loadExternalStrings(Dictionary<string, Dictionary<string, string>> stringLibrary)
        {
            if (stringLibrary.TryGetValue("ItemType", out var itemDict))
            {
                _itemType = itemDict.ToDictionary(pair => pair.Key.Trim(), pair => pair.Value);
            }
            if (stringLibrary.TryGetValue("Enchantment", out var enchantmentDict))
            {
                _enchantment = enchantmentDict.ToDictionary(pair => pair.Key.Trim(), pair => pair.Value);
            }
            if (stringLibrary.TryGetValue("ArmorProperties", out var armorPropertyDict))
            {
                _armorProperties = armorPropertyDict.ToDictionary(pair => pair.Key.Trim(), pair => pair.Value);
            }
            if (stringLibrary.TryGetValue("Mission", out var missionDict))
            {
                _mission = missionDict.ToDictionary(pair => pair.Key.Trim(), pair => pair.Value);
            }
            if (stringLibrary.TryGetValue("", out var clickysDict)
                && stringLibrary.TryGetValue("Realms", out var realmsDict)
                && stringLibrary.TryGetValue("DLC", out var dlcDict)
                && stringLibrary.TryGetValue("AncientLabels", out var ancientLabelsDict)
                && stringLibrary.TryGetValue("MerchantLabels", out var merchantLabelsDict)
                && stringLibrary.TryGetValue("MissionInterest", out var missionInterestDict)
                && stringLibrary.TryGetValue("Difficulty", out var diffDict)
                && stringLibrary.TryGetValue("Content_Season1/Decor/Text/TowerUILabels.csv", out var towerDict)
                && stringLibrary.TryGetValue("Content_Season2/Decor/Text/Season2MerchantLabels.csv", out var s2MerchantLabelsDict)
                && stringLibrary.TryGetValue("ItemPowerEffect", out var itemEffectDict)
                )
            {
                _clickys = clickysDict
                    .Concat(realmsDict)
                    .Concat(dlcDict)
                    .Concat(ancientLabelsDict)
                    .Concat(merchantLabelsDict)
                    .Concat(missionInterestDict)
                    .Concat(diffDict)
                    .Concat(towerDict)
                    .Concat(s2MerchantLabelsDict)
                    .Concat(itemEffectDict)
                    .ToDictionary(pair => pair.Key.Trim(), pair => pair.Value);
            }
            _labels = new Dictionary<string, Dictionary<string, string>>(StringComparer.Ordinal);
            foreach (var pair in stringLibrary)
            {
                if (!pair.Key.EndsWith("Labels", StringComparison.Ordinal)) { continue; }

                //Assigned rather than Added: whatever the paks hand over twice, the last one
                //wins and nothing throws. Losing one table's wording is a dropdown with fewer
                //rows in it; throwing here is the app refusing to open at all.
                _labels[pair.Key] = pair.Value;
            }

            isStringsLoaded = true;
        }

        public static void unloadExternalStrings()
        {
            _itemType.Clear();
            _enchantment.Clear();
            _armorProperties.Clear();
            _mission.Clear();
            _clickys.Clear();
            _labels.Clear();
            isStringsLoaded = false;
        }

        internal static string formatFILE_IN_UNEXPECTED_FORMAT_ERROR_MESSAGE(string filename) { return string.Format(FILE_IN_UNEXPECTED_FORMAT_ERROR_MESSAGE, filename); }
        internal static string formatFILE_DECRYPT_ERROR_MESSAGE(string filename) { return string.Format(FILE_DECRYPT_ERROR_MESSAGE, filename); }
        internal static string formatITEMS_COUNT_LABEL(int items, int max) { return string.Format(ITEMS_COUNT_LABEL, items, max); }
        internal static string formatCUSTOM_SKINS_EXPORTED(string path) { return string.Format(CUSTOM_SKINS_EXPORTED, path); }
        internal static string formatCUSTOM_SKINS_APPLIED(string name) { return string.Format(CUSTOM_SKINS_APPLIED, name); }
        internal static string formatHERO_CURRENT(string name) { return string.Format(HERO_CURRENT, name); }
        internal static string formatHERO_NOW_WEARING(string name) { return string.Format(HERO_NOW_WEARING, name); }
        internal static string formatHERO_NOW_WEARING_COVERED(string name) { return string.Format(HERO_NOW_WEARING_COVERED, name); }
        internal static string formatHERO_IMPORTED(string names) { return string.Format(HERO_IMPORTED, names); }
        internal static string formatHERO_FORGET_SKIN(string name) { return string.Format(HERO_FORGET_SKIN, name); }
        internal static string formatHERO_APPLIED_ARMOUR_SHOWN(string name) { return string.Format(HERO_APPLIED_ARMOUR_SHOWN, name); }
        internal static string formatHERO_ARMOUR_ALL_NOTE(int count) { return string.Format(HERO_ARMOUR_ALL_NOTE, count); }
        internal static string formatHERO_NOW_PLAYING(string name) { return string.Format(HERO_NOW_PLAYING, name); }
        internal static string formatHERO_APPLIED_HIDDEN(string name, int hidden) { return string.Format(HERO_APPLIED_HIDDEN, name, hidden); }
        internal static string formatCUSTOM_SKINS_PAK_ADDED(string name) { return string.Format(CUSTOM_SKINS_PAK_ADDED, name); }
        internal static string formatCUSTOM_SKINS_PAK_REPLACE(string name) { return string.Format(CUSTOM_SKINS_PAK_REPLACE, name); }
        internal static string formatITEMS_DELETE_CONFIRM_BUTTON(int count) { return string.Format(ITEMS_DELETE_CONFIRM_BUTTON, count); }
        internal static string formatITEMS_DELETE_CHOSEN(int count) { return string.Format(ITEMS_DELETE_CHOSEN, count); }
        internal static string formatITEMS_DELETE_CONFIRM(int count) { return string.Format(ITEMS_DELETE_CONFIRM, count); }
        internal static string formatITEMS_DELETED(int count) { return string.Format(ITEMS_DELETED, count); }
        internal static string formatTOWER_NO_BUILD(string type) { return string.Format(TOWER_NO_BUILD, type); }
        internal static string formatTOWER_FLOOR_NUMBER(int number) { return string.Format(TOWER_FLOOR_NUMBER, number); }
        internal static string formatTOWER_SEED(int seed) { return string.Format(TOWER_SEED, seed); }
        internal static string formatTOWER_TILE(string tile) { return string.Format(TOWER_TILE, tile); }
        internal static string formatTOWER_CHALLENGE(string challenge) { return string.Format(TOWER_CHALLENGE, challenge); }
        internal static string formatTOWER_RUN_NUMBER(int number) { return string.Format(TOWER_RUN_NUMBER, number); }
        internal static string formatTOWER_OF_FLOORS(int floors) { return string.Format(TOWER_OF_FLOORS, floors); }
        internal static string formatTOWER_PLAYER(string id) { return string.Format(TOWER_PLAYER, id); }
        internal static string formatTOWER_MOVED_INVENTORY(string name) { return string.Format(TOWER_MOVED_INVENTORY, name); }
        internal static string formatTOWER_MOVED_CHEST(string name) { return string.Format(TOWER_MOVED_CHEST, name); }
        internal static string formatEQUIP_IN_SLOT(int slotNumber) { return string.Format(EQUIP_IN_SLOT, slotNumber); }
        internal static string formatEQUIP_OVER_IN_SLOT(int slotNumber) { return string.Format(EQUIP_OVER_IN_SLOT, slotNumber); }
        internal static string formatINVENTORY_FULL_ERROR_MESSAGE(int maximum) { return string.Format(INVENTORY_FULL_ERROR_MESSAGE, maximum); }
        internal static string formatVERSION(string versionLabel, string versionString) { return string.Format(VERSION_FORMAT, versionLabel, versionString); }
        internal static string formatMCD_VERSION(string versionString) { return string.Format(MCD_VERSION_FORMAT, versionString); }
        internal static string formatNEXT_LEVEL_LABEL(int nextFloor, int totalFloors, string floorType) { return string.Format(NEXT_LEVEL_LABEL, nextFloor, totalFloors, floorType); }

        public static string itemName(string type)
        {
            return getItemString(type) ?? type;
        }

        public static string itemDesc(string type)
        {
            var key = "Flavour_" + type;
            return getItemString(key) ?? type;
        }

        /// <summary>
        /// Names and descriptions this app gives items itself - the New Items tab's - consulted
        /// before the game's own table. The game's Game.locres is what the running game reads; this
        /// is the app reading its own intentions, since the app never reads a mod's locres back.
        /// </summary>
        public static readonly Dictionary<string, string> itemTextOverrides = new(StringComparer.OrdinalIgnoreCase);

        private static string? getItemString(string key)
        {
            if (itemTextOverrides.TryGetValue(key, out var mine)) { return mine; }
            if (!isStringsLoaded) { return key; }
            if (Constants.stringMismatches.ContainsKey(key))
            {
                key = Constants.stringMismatches[key];
            }
            if (_itemType.TryGetValue(key, out string value))
            {
                return value;
            }
            EventLogger.logError($"Could not find string for item {key}");
            return null;
        }


        /// <summary>
        /// What this app calls its own enchantments (CustomEnchantments): name by id, description
        /// and effect by id + "_desc" / "_effect". Consulted before the game's table.
        /// </summary>
        public static readonly Dictionary<string, string> enchantmentTextOverrides = new(StringComparer.OrdinalIgnoreCase);

        public static string enchantmentName(string enchantment)
        {
            var key = enchantment;
            if (enchantmentTextOverrides.TryGetValue(key, out var mine)) { return mine; }
            if (!isStringsLoaded) { return key; }
            if (Constants.stringMismatches.ContainsKey(key))
            {
                key = Constants.stringMismatches[key];
            }
            //A few of the enchantments the game never offers have a locres entry that is just
            //the id spelled out - "CogCrossbowEnchantment" - which is a placeholder rather than
            //a name. The readable name wins over that, and only that: real game text still wins
            //over anything this app would call something.
            if (_enchantment.TryGetValue(key, out string value)
                && string.Equals(value, enchantment, StringComparison.Ordinal))
            {
                var readable = Data.HiddenEnchantments.nameFor(enchantment);
                if (readable != null) { return readable; }
            }

            if (_enchantment.TryGetValue(key, out value))
            {
                //Not when the name already ends in it. The game calls one of these "Freezing
                //Ranged", and the suffix would have made that "Freezing Ranged (Ranged)".
                if(enchantment.ToLowerInvariant().Contains("ranged")
                    && !value.EndsWith("Ranged", StringComparison.OrdinalIgnoreCase))
                {
                    var classification = R.getString("ItemTag_Ranged") ?? R.RANGED_ITEMS_FILTER;
                    return $"{value} ({classification})";
                }
                else if (enchantment.ToLowerInvariant().Contains("melee")
                    && !value.EndsWith("Melee", StringComparison.OrdinalIgnoreCase))
                {
                    var classification = R.getString("ItemTag_Melee") ?? R.MELEE_ITEMS_FILTER;
                    return $"{value} ({classification})";
                }
                else
                {
                    return value;
                }
            }
            //The ones the game never shows have no text of their own to find, so this is not a
            //failure worth logging - it is the expected answer for them.
            var hidden = Data.HiddenEnchantments.nameFor(enchantment);
            if (hidden != null) { return hidden; }

            EventLogger.logError($"Could not find string for enchantment {key}");
            return enchantment;
        }

        public static string enchantmentDescription(string enchantment)
        {
            var key = enchantment + "_desc";
            return getEnchantmentString(key) ?? enchantment;
        }

        public static string enchantmentEffect(string enchantment)
        {
            var key = enchantment + "_effect";
            return getEnchantmentString(key) ?? enchantment;
        }

        private static string? getEnchantmentString(string key)
        {
            if (enchantmentTextOverrides.TryGetValue(key, out var mine)) { return mine; }
            if (!isStringsLoaded) { return key; }
            if (Constants.stringMismatches.ContainsKey(key))
            {
                key = Constants.stringMismatches[key];
            }
            if (_enchantment.TryGetValue(key, out string value))
            {
                return value;
            }
            EventLogger.logError($"Could not find string for enchantment {key}");
            return null;
        }

        public static string armorProperty(string armorPropertyId)
        {
            return getArmorPropertyString(armorPropertyId) ?? armorPropertyId;
        }

        public static string armorPropertyDescription(string armorPropertyId)
        {
            var key = armorPropertyId + "_description";
            return getArmorPropertyString(key) ?? armorPropertyId;
        }

        private static string? getArmorPropertyString(string key)
        {
            if (!isStringsLoaded) { return key; }
            if (Constants.stringMismatches.ContainsKey(key))
            {
                key = Constants.stringMismatches[key];
            }
            if (_armorProperties.TryGetValue(key, out string value))
            {
                return value;
            }
            EventLogger.logError($"Could not find string for armor {key}");
            return null;
        }

        public static string? getMissionName(string missionId)
        {
            var key = missionId + "_name";
            return getMissionString(key) ?? missionId;
        }

        private static string? getMissionString(string key)
        {
            if (!isStringsLoaded) { return key; }
            if (Constants.stringMismatches.ContainsKey(key))
            {
                key = Constants.stringMismatches[key];
            }
            if (_mission.TryGetValue(key, out string value))
            {
                return value;
            }
            EventLogger.logError($"Could not find string for mission {key}");
            return null;
        }

        /// <summary>
        /// Every mission string whose key starts with something, English text and all.
        ///
        /// The objective editor needs this because an objective's "description" is not text -
        /// it is a KEY into this table, and a key the table has not got draws as
        /// &lt;MISSING STRING TABLE ENTRY&gt; in game. So the editor offers what exists rather
        /// than letting somebody type a sentence into a lookup.
        /// </summary>
        /// <summary>
        /// The wording one mission's objectives can use, as (key, what it reads).
        ///
        /// Ordered by what it reads rather than by the key, because the key is the game's
        /// spelling and the reading is the part anybody is choosing between.
        /// </summary>
        public static IReadOnlyList<(string key, string said)> wordingFor(string loctable, string stem)
        {
            var table = tableFor(loctable);
            if (table == null) { return new List<(string, string)>(); }

            return table
                .Where(pair => pair.Key.StartsWith(stem, StringComparison.OrdinalIgnoreCase))
                .Select(pair => (pair.Key, pair.Value))
                .OrderBy(pair => pair.Value, StringComparer.CurrentCultureIgnoreCase)
                .ToList();
        }

        /// <summary>What one objective key reads as, or null when that table has not got it.</summary>
        public static string? wordFor(string loctable, string key)
        {
            var table = tableFor(loctable);
            return table != null && table.TryGetValue(key, out var said) ? said : null;
        }

        /// <summary>
        /// The table a level's loctable-id names.
        ///
        /// Its own spelling first, then any casing of it. The game's own names are inconsistent -
        /// the level "underhalls" reads a table called "UnderHallsLabels" - so an exact match
        /// alone finds nothing for several missions. Exact still wins where both exist, which is
        /// the case that matters: "hypermissionLabels" and "HyperMissionLabels" are two
        /// different tables.
        /// </summary>
        private static Dictionary<string, string>? tableFor(string loctable)
        {
            if (string.IsNullOrEmpty(loctable)) { return null; }

            var wanted = loctable + "Labels";
            if (_labels.TryGetValue(wanted, out var exact)) { return exact; }

            foreach (var pair in _labels)
            {
                if (string.Equals(pair.Key, wanted, StringComparison.OrdinalIgnoreCase))
                {
                    return pair.Value;
                }
            }

            return null;
        }

        public static IEnumerable<KeyValuePair<string, string>> missionStringsStartingWith(string stem)
            => everyString().Where(pair =>
                pair.Key.StartsWith(stem, System.StringComparison.OrdinalIgnoreCase));

        /// <summary>Every loaded string, with which table it came out of.</summary>
        public static IEnumerable<(string table, string key, string value)> everyTable()
            => _itemType.Select(p => ("ItemType", p.Key, p.Value))
                .Concat(_enchantment.Select(p => ("Enchantment", p.Key, p.Value)))
                .Concat(_armorProperties.Select(p => ("ArmorProperties", p.Key, p.Value)))
                .Concat(_mission.Select(p => ("Mission", p.Key, p.Value)))
                .Concat(_clickys.Select(p => ("Other", p.Key, p.Value)));

        private static IEnumerable<KeyValuePair<string, string>> everyString()
            => _mission.Concat(_clickys).Concat(_itemType).Concat(_enchantment);

        public static string? getString(string key)
        {
            if (!isStringsLoaded) { return null; }
            if (Constants.stringMismatches.ContainsKey(key))
            {
                key = Constants.stringMismatches[key];
            }
            if (_clickys.TryGetValue(key, out string value))
            {
                return value;
            }
            EventLogger.logError($"Could not find string for key -{key}-");
            return null;
        }

    }
}
