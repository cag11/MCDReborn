using MCDSaveEdit;
using MCDSaveEdit.Data;
using MCDSaveEdit.Save.Models.Enums;
using MCDSaveEdit.Services;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using PakReader;
using PakReader.Pak;
using PakReader.Parsers.Objects;
using System.Linq;
#nullable enable

namespace MCDSaveEditTests
{
    /// <summary>
    /// The armor-defaults table is transcribed by hand, so these tests are what stop a typo
    /// writing an id the game has never heard of into somebody's save.
    /// </summary>
    [TestClass]
    public class ArmorDefaultsTests
    {
        [TestMethod]
        public void TestEveryArmorTypeHasProperties()
        {
            foreach (var armor in ArmorDefaults.knownArmor)
            {
                var properties = ArmorDefaults.forItemType(armor);
                Assert.IsNotNull(properties, armor);
                Assert.IsTrue(properties!.Length > 0, armor);
                Assert.IsTrue(properties.All(x => !string.IsNullOrWhiteSpace(x.Id)), armor);
            }
        }

        [TestMethod]
        public void TestUnknownTypesReturnNull()
        {
            //A weapon, Mystery Armor and anything newer than this build keep what they have
            //rather than being blanked.
            Assert.IsNull(ArmorDefaults.forItemType("Sword"));
            Assert.IsNull(ArmorDefaults.forItemType("MysteryArmor"));
            Assert.IsNull(ArmorDefaults.forItemType("SomeArmorAddedLater"));
            Assert.IsNull(ArmorDefaults.forItemType(null));
        }

        [TestMethod]
        public void TestPropertiesAreFreshInstancesEveryTime()
        {
            //They are handed to an item and edited from there, so two items must not end up
            //sharing one object.
            var first = ArmorDefaults.forItemType("WolfArmor")!;
            var second = ArmorDefaults.forItemType("WolfArmor")!;
            Assert.AreNotSame(first[0], second[0]);

            first[0].Id = "Changed";
            Assert.AreNotEqual("Changed", second[0].Id);
        }

        [TestMethod]
        public void TestUniquePropertyComesFirstAndIsUniqueRarity()
        {
            //The shape real saves show: the property that makes a unique armor unique leads,
            //at Unique rarity, and the ones shared with its base armor follow as Common.
            var fox = ArmorDefaults.forItemType("WolfArmor_Unique1")!;
            Assert.AreEqual("MissChance", fox[0].Id);
            Assert.AreEqual(Rarity.Unique, fox[0].Rarity);
            Assert.IsTrue(fox.Skip(1).All(x => x.Rarity == Rarity.Common));

            //And a base armor has no Unique property at all.
            var wolf = ArmorDefaults.forItemType("WolfArmor")!;
            Assert.IsTrue(wolf.All(x => x.Rarity == Rarity.Common));
        }

        [TestMethod]
        public void TestTheTwoIdsThatAreEasyToGetWrong()
        {
            //"35% damage reduction" is SuperbDamageAbsorption, not DamageAbsorption. Confirmed
            //against real Scale Mail out of a save file.
            var scaleMail = ArmorDefaults.forItemType("ScaleMail")!;
            CollectionAssert.AreEqual(
                new[] { "SuperbDamageAbsorption", "MeleeDamageBoost" },
                scaleMail.Select(x => x.Id).ToArray());

            //The Troubadour's negative-status property is ImmunityBoost ("Resilience") and the
            //positive-status one is Resonant ("Harmony") - the display names invite the swap.
            var troubadour = ArmorDefaults.forItemType("BardsGarb_Unique1")!;
            CollectionAssert.AreEqual(
                new[] { "ImmunityBoost", "AreaHeal", "Resonant" },
                troubadour.Select(x => x.Id).ToArray());
        }

        [TestMethod]
        public void TestEveryIdInTheTableExistsInTheGame()
        {
            var model = new AppModel();
            string? paksFolderPath = model.usableGameContentIfExists();
            if (string.IsNullOrWhiteSpace(paksFolderPath))
            {
                Assert.Inconclusive("No Minecraft Dungeons install found.");
                return;
            }
            if (Secrets.PAKS_AES_KEYS.Length == 0 || string.IsNullOrWhiteSpace(Secrets.PAKS_AES_KEYS[0].key))
            {
                Assert.Inconclusive("No pak AES key configured in Secrets.cs.");
                return;
            }

            var filter = new PakFilter(new[] { Constants.PAKS_FILTER_STRING }, false);
            var pakIndex = new PakIndex(path: paksFolderPath!, cacheFiles: true, caseSensitive: true, filter: filter);
            pakIndex.UseKey(FGuid.Zero, Secrets.PAKS_AES_KEYS[0].key.Substring(2).ToBytesKey());
            new PakContentResolver(pakIndex, null).loadPakFiles();

            Assert.IsTrue(ItemDatabase.armor.Count >= 71, $"armor: {ItemDatabase.armor.Count}");
            Assert.IsTrue(ItemDatabase.armorProperties.Count >= 37, $"properties: {ItemDatabase.armorProperties.Count}");

            //Nothing in the table may name an armor the game does not have...
            var unknownArmor = ArmorDefaults.knownArmor
                .Where(x => !ItemDatabase.armor.Contains(x)).OrderBy(x => x).ToList();
            Assert.AreEqual(0, unknownArmor.Count,
                $"armor ids not in the game: {string.Join(", ", unknownArmor)}");

            //...nor a property it does not have. This is the one that stops a bad id reaching
            //a save file.
            var unknownProperties = ArmorDefaults.usedProperties
                .Where(x => !ItemDatabase.armorProperties.Contains(x)).OrderBy(x => x).ToList();
            Assert.AreEqual(0, unknownProperties.Count,
                $"property ids not in the game: {string.Join(", ", unknownProperties)}");

            //And every armor the game has should be covered, bar Mystery Armor, whose
            //properties are random by design.
            var uncovered = ItemDatabase.armor
                .Where(x => x != "MysteryArmor" && !ArmorDefaults.knownArmor.Contains(x))
                .OrderBy(x => x).ToList();
            Assert.AreEqual(0, uncovered.Count,
                $"armor with no defaults: {string.Join(", ", uncovered)}");
        }
    }
}
