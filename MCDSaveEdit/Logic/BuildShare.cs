using MCDSaveEdit.Data;
using MCDSaveEdit.Save.Models.Enums;
using MCDSaveEdit.Save.Models.Profiles;
using MCDSaveEdit.Services;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
#nullable enable

namespace MCDSaveEdit.Logic
{
    /// <summary>
    /// Converts a character's loadout to and from the share format used by
    /// MCD Builder (https://mcdbuilder.vercel.app).
    ///
    /// The format was read off a live share link rather than guessed:
    ///
    ///   {"v":1,
    ///    "g":[{"e":[["Burning",2],0,0,0],"i":"Archer's Armor"},
    ///         {"e":[0,0,0,0]},
    ///         {"e":[0,0,0,0]}],
    ///    "a":["Boots of Swiftness",null,null]}
    ///
    ///   g  three gear slots, in order: armour, melee, ranged
    ///   i  the item's DISPLAY name - not an internal type id
    ///   e  four enchantment slots; each is 0 when empty, or ["Display Name", tier]
    ///   a  three artifacts, by display name or null
    ///
    /// The whole document is base64url encoded (no padding) into the ?b= query parameter.
    ///
    /// Everything crosses the boundary as display names, so both directions go through
    /// R.itemName / R.enchantmentName. That means a save opened with a non-English
    /// language selected will produce names MCD Builder does not recognise.
    /// </summary>
    public static class BuildShare
    {
        public const string MCD_BUILDER_URL = "https://mcdbuilder.vercel.app/";
        private const string QUERY_PARAMETER = "b";
        private const int SCHEMA_VERSION = 1;
        private const int ENCHANTMENT_SLOTS = 4;
        //Three ordinary enchantments; the fourth slot belongs to the gild and is shown only
        //on gilded gear.
        private const int BASE_ENCHANTMENT_SLOTS = 3;
        private const int GILD_SLOT_INDEX = 3;

        //The order MCD Builder expects in "g".
        private static readonly EquipmentSlotEnum[] GEAR_SLOTS = {
            EquipmentSlotEnum.ArmorGear,
            EquipmentSlotEnum.MeleeGear,
            EquipmentSlotEnum.RangedGear,
        };

        private static readonly EquipmentSlotEnum[] ARTIFACT_SLOTS = {
            EquipmentSlotEnum.HotbarSlot1,
            EquipmentSlotEnum.HotbarSlot2,
            EquipmentSlotEnum.HotbarSlot3,
        };

        #region Export

        public static string toJson(IEnumerable<Item>? equippedItems)
        {
            var items = equippedItems?.ToList() ?? new List<Item>();

            var gear = new JsonArray();
            foreach (var slot in GEAR_SLOTS)
            {
                gear.Add(gearNode(itemInSlot(items, slot)));
            }

            var artifacts = new JsonArray();
            foreach (var slot in ARTIFACT_SLOTS)
            {
                var item = itemInSlot(items, slot);
                artifacts.Add(item?.Type == null ? null : JsonValue.Create(builderItem(item.Type)));
            }

            var build = new JsonObject {
                ["v"] = SCHEMA_VERSION,
                ["g"] = gear,
                ["a"] = artifacts,
            };

            //Mystery Armor is the one armor whose properties are rolled rather than fixed, so it
            //is the one armor a name does not fully describe. Carried only when it is equipped
            //and something actually rolled, which is what MCD Builder does with it too.
            var mystery = MysteryAttributes.effectsFor(itemInSlot(items, EquipmentSlotEnum.ArmorGear));
            if (mystery.Any(effect => effect != null))
            {
                var rolled = new JsonArray();
                foreach (var effect in mystery)
                {
                    rolled.Add(effect == null ? null : JsonValue.Create(effect));
                }
                build["m"] = rolled;
            }
            return build.ToJsonString(new JsonSerializerOptions {
                WriteIndented = false,
                //Default escaping turns ' into ', which MCD Builder never emits
                //and which needlessly inflates the share link.
                Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
            });
        }

        public static string toShareUrl(IEnumerable<Item>? equippedItems)
        {
            var encoded = toBase64Url(toJson(equippedItems));
            return $"{MCD_BUILDER_URL}?{QUERY_PARAMETER}={encoded}";
        }

        private static JsonObject gearNode(Item? item)
        {
            var enchantments = new JsonArray();
            foreach (var applied in investedEnchantments(item).Take(BASE_ENCHANTMENT_SLOTS))
            {
                enchantments.Add(enchantmentSlot(applied));
            }
            //MCD Builder always writes four slots and uses 0 for an empty one. The first three
            //are padded out before the gild is added, because the gild IS the fourth slot rather
            //than the next free one: an item wearing two enchantments and a gild still has to
            //put that gild in slot four, or the builder shows it as an ordinary third
            //enchantment and leaves the gilded slot empty.
            while (enchantments.Count < BASE_ENCHANTMENT_SLOTS) { enchantments.Add(JsonValue.Create(0)); }

            var gild = gildEnchantment(item);
            if (gild == null) { enchantments.Add(JsonValue.Create(0)); }
            else { enchantments.Add(enchantmentSlot(gild.Value)); }

            var node = new JsonObject { ["e"] = enchantments };
            if (item?.Type != null) { node["i"] = builderItem(item.Type); }

            //The flag that says the gear is gilded, which is what actually reveals the fourth
            //slot. Without it the builder reads the gear as ungilded and never draws that slot,
            //whatever was sent in it - the enchantment crossed over and simply could not be seen.
            if (isGilded(item)) { node["f"] = 1; }
            return node;
        }

        private static JsonArray enchantmentSlot((string name, long tier) applied)
            => new JsonArray { JsonValue.Create(applied.name), JsonValue.Create(applied.tier) };

        /// <summary>
        /// Gilded means the item carries a netherite enchantment at all, which is the same test
        /// the gilded badge on an item tile uses. An item gilded but not yet given an
        /// enchantment still counts: it is gilded gear with an empty fourth slot, and saying so
        /// is more truthful than sending it across as ordinary gear.
        /// </summary>
        private static bool isGilded(Item? item) => item?.NetheriteEnchant != null;

        /// <summary>The gild's enchantment, when one has actually been chosen and invested in.</summary>
        private static (string name, long tier)? gildEnchantment(Item? item)
        {
            var netherite = item?.NetheriteEnchant;
            if (netherite == null) { return null; }
            if (string.IsNullOrEmpty(netherite.Id) || netherite.Level <= 0) { return null; }
            return (builderEnchantment(netherite.Id), netherite.Level);
        }

        private static IEnumerable<(string name, long tier)> investedEnchantments(Item? item)
        {
            if (item?.Enchantments == null) { yield break; }

            //Saves hold every option the item could have had; only the ones with a level
            //above zero were actually invested in.
            foreach (var enchantment in item.Enchantments)
            {
                if (enchantment == null || enchantment.Level <= 0) { continue; }
                if (string.IsNullOrEmpty(enchantment.Id)) { continue; }
                yield return (builderEnchantment(enchantment.Id), enchantment.Level);
            }
        }

        /// <summary>
        /// MCDSaveEdit appends a bracketed suffix to enchantments that exist in more than
        /// one variant - "Void Strike (Melee)", "Dynamo (Melee)". That is this app's own
        /// disambiguation, not a name from the game, and MCD Builder lists the plain form,
        /// so it has to come off before the name crosses over.
        /// </summary>
        private static string builderName(string name)
        {
            var bracket = name.IndexOf(" (", StringComparison.Ordinal);
            return bracket > 0 ? name.Substring(0, bracket) : name;
        }

        /// <summary>
        /// An enchantment as MCD Builder names it. The map is consulted first, for the few the
        /// two apps genuinely call different things; everything else is this app's display name
        /// with its own bracketed suffix taken off.
        /// </summary>
        private static string builderEnchantment(string id)
            => BuilderNames.enchantmentName(id) ?? builderName(R.enchantmentName(id));

        private static string builderItem(string type)
            => BuilderNames.itemName(type) ?? R.itemName(type);

        private static Item? itemInSlot(IEnumerable<Item> items, EquipmentSlotEnum slot)
        {
            var name = slot.ToString();
            return items.FirstOrDefault(x => x.EquipmentSlot == name);
        }

        #endregion

        #region Import

        /// <summary>
        /// Accepts a full MCD Builder share link, a bare ?b= payload, or raw JSON.
        /// Returns unequipped items ready to be added to an inventory, or null if the
        /// input is not a build at all.
        /// </summary>
        public static IReadOnlyList<Item>? itemsFromShare(string? input, double power)
        {
            var json = jsonFromInput(input);
            if (json == null) { return null; }

            JsonNode? root;
            try { root = JsonNode.Parse(json!); }
            catch (JsonException) { return null; }
            if (root is not JsonObject build) { return null; }

            var items = new List<Item>();

            if (build["g"] is JsonArray gear)
            {
                for (int i = 0; i < gear.Count && i < GEAR_SLOTS.Length; i++)
                {
                    var item = itemFromGearNode(gear[i] as JsonObject, GEAR_SLOTS[i], power);
                    if (item != null) { items.Add(item!); }
                }
            }

            //The rolled attributes belong to the build rather than to the gear node, because only
            //one armor can ever have them. They are applied after the gear is built, to the armor
            //that came with it, and only when that armor is the one they can belong to.
            if (build["m"] is JsonArray mystery)
            {
                var armour = items.FirstOrDefault(MysteryAttributes.isMysteryArmor);
                if (armour != null)
                {
                    var effects = mystery.Select(node => {
                        try { return node?.GetValue<string>(); } catch (Exception) { return null; }
                    });
                    var properties = MysteryAttributes.propertiesFrom(effects);
                    if (properties.Count > 0) { armour.Armorproperties = properties.ToArray(); }
                }
            }

            if (build["a"] is JsonArray artifacts)
            {
                for (int i = 0; i < artifacts.Count && i < ARTIFACT_SLOTS.Length; i++)
                {
                    var type = itemTypeForDisplayName(artifacts[i]?.GetValue<string>());
                    if (type == null) { continue; }
                    items.Add(newItem(type!, power, ARTIFACT_SLOTS[i]));
                }
            }

            return items;
        }

        private static string? jsonFromInput(string? input)
        {
            if (string.IsNullOrWhiteSpace(input)) { return null; }
            var text = input!.Trim();

            if (text.StartsWith("{")) { return text; }

            //Pull the payload out of a share link, or take the whole string as one.
            var marker = QUERY_PARAMETER + "=";
            var at = text.IndexOf(marker, StringComparison.Ordinal);
            var payload = at >= 0 ? text.Substring(at + marker.Length) : text;

            var ampersand = payload.IndexOf('&');
            if (ampersand >= 0) { payload = payload.Substring(0, ampersand); }

            return fromBase64Url(payload);
        }

        private static Item? itemFromGearNode(JsonObject? node, EquipmentSlotEnum slot, double power)
        {
            var type = itemTypeForDisplayName(node?["i"]?.GetValue<string>());
            if (type == null) { return null; }

            var item = newItem(type!, power, slot);

            //Gilded gear is flagged rather than implied, and the builder itself only shows the
            //fourth slot when the flag is set. So the flag decides what the fourth entry means:
            //under gilded gear it is the gild, and under ordinary gear it is stale data the
            //builder is not showing either, so it is left where it is.
            var gilded = isGildedNode(node!);

            var enchantments = new List<Enchantment>();
            if (node!["e"] is JsonArray slots)
            {
                for (int i = 0; i < slots.Count; i++)
                {
                    var parsed = enchantmentFromSlot(slots[i]);
                    if (i == GILD_SLOT_INDEX)
                    {
                        if (gilded && parsed != null) { item.NetheriteEnchant = parsed; }
                        continue;
                    }
                    if (parsed != null) { enchantments.Add(parsed!); }
                }
            }
            if (enchantments.Count > 0) { item.Enchantments = enchantments.ToArray(); }

            //Gilded, but with nothing chosen for the slot. The item is still gilded, so it is
            //marked the same way the Gilded button marks one: an unset enchantment, which is
            //what shows the badge and leaves the slot waiting to be filled.
            if (gilded && item.NetheriteEnchant == null)
            {
                item.NetheriteEnchant = new Enchantment { Id = Constants.DEFAULT_ENCHANTMENT_ID, Level = 0 };
            }
            return item;
        }

        /// <summary>One "e" entry: ["Display Name", tier], or the number 0 for an empty slot.</summary>
        private static Enchantment? enchantmentFromSlot(JsonNode? entry)
        {
            if (entry is not JsonArray pair || pair.Count < 2) { return null; }
            var id = enchantmentIdForDisplayName(pair[0]?.GetValue<string>());
            if (id == null) { return null; }

            long tier = 0;
            try { tier = pair[1]!.GetValue<long>(); } catch (Exception) { }
            return new Enchantment { Id = id!, Level = Math.Max(0, tier) };
        }

        /// <summary>
        /// The gilded flag, read without trusting its type. It is written as the number 1, but
        /// a hand-edited link can hold anything, and a build that is merely odd should import
        /// as ungilded rather than throw.
        /// </summary>
        private static bool isGildedNode(JsonObject node)
        {
            var flag = node["f"];
            if (flag == null) { return false; }
            try
            {
                return flag.GetValueKind() switch {
                    JsonValueKind.Number => flag.GetValue<long>() == 1,
                    JsonValueKind.True => true,
                    JsonValueKind.String => flag.GetValue<string>() == "1",
                    _ => false,
                };
            }
            catch (Exception) { return false; }
        }

        /// <summary>
        /// The slot the build intended this item for is recorded on the item itself. The
        /// caller decides what to do with it: a plain import clears it so everything lands
        /// in the inventory, while "import and equip" keeps it.
        /// </summary>
        private static Item newItem(string type, double power, EquipmentSlotEnum slot)
        {
            return new Item {
                Type = type,
                Power = power,
                Rarity = Rarity.Common,
                EquipmentSlot = slot.ToString(),
                MarkedNew = true,
            };
        }

        //Display name back to type id. Built lazily because the string library is only
        //populated once game content has loaded.
        private static Dictionary<string, string>? _itemsByName;
        private static Dictionary<string, string>? _enchantmentsByName;

        private static string? itemTypeForDisplayName(string? name)
        {
            if (string.IsNullOrWhiteSpace(name)) { return null; }

            //The map first: these are names this app's own list does not hold at all.
            var mapped = BuilderNames.itemType(name);
            if (mapped != null) { return mapped; }

            _itemsByName ??= buildLookup(ItemDatabase.all, R.itemName);
            var trimmed = name!.Trim();
            if (_itemsByName!.TryGetValue(trimmed, out var exact)) { return exact; }
            return _itemsByName!.TryGetValue(NameMatching.normalize(trimmed), out var loose) ? loose : null;
        }

        private static string? enchantmentIdForDisplayName(string? name)
        {
            if (string.IsNullOrWhiteSpace(name)) { return null; }

            var mapped = BuilderNames.enchantmentId(name);
            if (mapped != null) { return mapped; }

            _enchantmentsByName ??= buildLookup(EnchantmentDatabase.allEnchantments, R.enchantmentName);
            var trimmed = name!.Trim();
            if (_enchantmentsByName!.TryGetValue(trimmed, out var exact)) { return exact; }
            return _enchantmentsByName!.TryGetValue(NameMatching.normalize(trimmed), out var loose) ? loose : null;
        }

        private static Dictionary<string, string> buildLookup(IEnumerable<string> ids, Func<string, string> displayName)
        {
            var lookup = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var id in ids)
            {
                var name = displayName(id);
                if (string.IsNullOrWhiteSpace(name)) { continue; }
                //First wins: ids map many-to-one onto names for a few reskins.
                if (!lookup.ContainsKey(name)) { lookup[name] = id; }

                //A loose key alongside it, so a name from MCD Builder that differs only
                //by apostrophe, bracket or case still resolves. Exact keys are added
                //first, so they always win.
                var loose = NameMatching.normalize(name);
                if (loose.Length > 0 && !lookup.ContainsKey(loose)) { lookup[loose] = id; }
            }
            return lookup;
        }

        /// <summary>Clears the cached name lookups when the language or game content changes.</summary>
        public static void resetLookups()
        {
            _itemsByName = null;
            _enchantmentsByName = null;
        }

        #endregion

        #region base64url

        private static string toBase64Url(string text)
        {
            var bytes = Encoding.UTF8.GetBytes(text);
            return Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        }

        private static string? fromBase64Url(string payload)
        {
            try
            {
                var padded = payload.Replace('-', '+').Replace('_', '/');
                padded += new string('=', (4 - padded.Length % 4) % 4);
                return Encoding.UTF8.GetString(Convert.FromBase64String(padded));
            }
            catch (FormatException)
            {
                return null;
            }
        }

        #endregion
    }
}
