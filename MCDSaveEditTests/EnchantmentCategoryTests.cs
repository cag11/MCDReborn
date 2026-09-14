using MCDSaveEdit;
using MCDSaveEdit.Data;
using MCDSaveEdit.Services;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using PakReader;
using PakReader.Pak;
using PakReader.Parsers.Objects;
using System.Collections.Generic;
using System.Linq;
#nullable enable

namespace MCDSaveEditTests
{
    /// <summary>
    /// The enchantment-to-gear table is transcribed by hand from the wiki, because the paks
    /// do not carry it. These tests are what keeps the transcription honest.
    /// </summary>
    [TestClass]
    public class EnchantmentCategoryTests
    {
        private const EnchantmentCategory M = EnchantmentCategory.Melee;
        private const EnchantmentCategory A = EnchantmentCategory.Armor;
        private const EnchantmentCategory R = EnchantmentCategory.Ranged;
        private const EnchantmentCategory O = EnchantmentCategory.Other;

        /// <summary>
        /// The enchantments the wiki lists as mob-exclusive or unused. Nothing else in the
        /// game's content should land outside the three gear lists.
        /// </summary>
        private static readonly HashSet<string> EXPECTED_OTHER = new HashSet<string> {
            "Altruistic",     //Heals Allies
            "Barrier",
            "Knockback",
            "Regeneration",
            "Shielding",
        };

        [TestMethod]
        public void TestMeleeAndRangedVariantsAreSeparate()
        {
            //These come from the game as two ids with two icons, so they must not blur.
            Assert.AreEqual(M, EnchantmentCategories.categoriesFor("GravityMelee"));
            Assert.AreEqual(R, EnchantmentCategories.categoriesFor("Gravity"));
            Assert.AreEqual(M, EnchantmentCategories.categoriesFor("VoidTouchedMelee"));
            Assert.AreEqual(R, EnchantmentCategories.categoriesFor("VoidTouchedRanged"));
            Assert.AreEqual(M, EnchantmentCategories.categoriesFor("AnimaConduitMelee"));
            Assert.AreEqual(R, EnchantmentCategories.categoriesFor("AnimaConduitRanged"));
            Assert.AreEqual(M, EnchantmentCategories.categoriesFor("DynamoMelee"));
            Assert.AreEqual(R, EnchantmentCategories.categoriesFor("DynamoRanged"));
        }

        [TestMethod]
        public void TestSharedIconsCoverEveryCategoryTheGameAllows()
        {
            //One icon folder, several of the game's variants behind it, so one id has to
            //stand for all of them or it would vanish from lists it belongs in.
            foreach (var id in new[] { "Committed", "CriticalHit", "Exploding", "FireAspect",
                                       "Looting", "Smiting", "SoulSiphon", "Unchanting", "Weakening" })
            {
                Assert.AreEqual(M | R, EnchantmentCategories.categoriesFor(id), id);
            }
            Assert.AreEqual(M | R | A, EnchantmentCategories.categoriesFor("Prospector"));
        }

        [TestMethod]
        public void TestSpotChecksAgainstTheWiki()
        {
            Assert.AreEqual(M, EnchantmentCategories.categoriesFor("Sharpness"));
            Assert.AreEqual(M, EnchantmentCategories.categoriesFor("Backstabber"));
            Assert.AreEqual(A, EnchantmentCategories.categoriesFor("Acrobat"));
            Assert.AreEqual(A, EnchantmentCategories.categoriesFor("ShadowFeast"));
            Assert.AreEqual(A, EnchantmentCategories.categoriesFor("SpiritSpeed"));
            Assert.AreEqual(R, EnchantmentCategories.categoriesFor("Piercing"));
            Assert.AreEqual(R, EnchantmentCategories.categoriesFor("MultiCharge"));
        }

        [TestMethod]
        public void TestUnknownIdsFallIntoOther()
        {
            //A game update or a modded pak must not make an enchantment unreachable.
            Assert.AreEqual(O, EnchantmentCategories.categoriesFor("SomethingAddedLater"));
            Assert.AreEqual(O, EnchantmentCategories.categoriesFor(null));
        }

        [TestMethod]
        public void TestMatchesRespectsTheSelectedCategories()
        {
            Assert.IsTrue(EnchantmentCategories.matches("Sharpness", M));
            Assert.IsFalse(EnchantmentCategories.matches("Sharpness", A));
            Assert.IsTrue(EnchantmentCategories.matches("Sharpness", M | A));

            //A shared id shows up under either of its categories.
            Assert.IsTrue(EnchantmentCategories.matches("Looting", M));
            Assert.IsTrue(EnchantmentCategories.matches("Looting", R));
            Assert.IsFalse(EnchantmentCategories.matches("Looting", A));
        }

        [TestMethod]
        public void TestOtherIsShownOnlyWhileItsToggleIsOn()
        {
            Assert.IsTrue(EnchantmentCategories.matches("Knockback", O));
            Assert.IsTrue(EnchantmentCategories.matches("Knockback", EnchantmentCategory.All));
            Assert.IsFalse(EnchantmentCategories.matches("Knockback", EnchantmentCategory.Gear));
        }

        [TestMethod]
        public void TestEveryEnchantmentInTheGameContentIsClassified()
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

            Assert.IsTrue(EnchantmentDatabase.allEnchantments.Count >= 118,
                $"enchantments: {EnchantmentDatabase.allEnchantments.Count}");

            //Anything the table does not know falls into Other, which is a silent way to lose
            //an enchantment from the melee/armor/ranged lists. Pin the exact set.
            var unclassified = EnchantmentDatabase.allEnchantments
                .Where(x => EnchantmentCategories.categoriesFor(x) == EnchantmentCategory.Other)
                .OrderBy(x => x)
                .ToList();

            CollectionAssert.AreEqual(
                EXPECTED_OTHER.OrderBy(x => x).ToList(),
                unclassified,
                $"unclassified enchantments: {string.Join(", ", unclassified)}");
        }
    }
}
