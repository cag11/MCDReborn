using MCDSaveEdit;
using MCDSaveEdit.Data;
using MCDSaveEdit.Save.Models.Profiles;
using MCDSaveEdit.Services;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using PakReader;
using PakReader.Pak;
using PakReader.Parsers.Objects;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
#nullable enable

namespace MCDSaveEditTests
{
    [TestClass]
    public class AppTests
    {
        private readonly AppModel _model = new AppModel();

        [TestMethod]
        public void TestExtractGameFiles()
        {
            //Needs the game installed and a real AES key, so it is inconclusive rather
            //than failed on a machine (or CI runner) that has neither.
            string? paksFolderPath = _model.usableGameContentIfExists();
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

            PakFilter? filter = new PakFilter(new[] { Constants.PAKS_FILTER_STRING }, false);
            PakIndex? pakIndex = new PakIndex(path: paksFolderPath!, cacheFiles: true, caseSensitive: true, filter: filter);
            pakIndex.UseKey(FGuid.Zero, Secrets.PAKS_AES_KEYS[0].key.Substring(2).ToBytesKey());
            //Floors, not equality: these move with every game patch and DLC.
            int entryCount = pakIndex.Count();
            Assert.IsTrue(entryCount >= 80386, $"expected at least 80386 pak entries, found {entryCount}");

            var pakImageResolver = new PakContentResolver(pakIndex, null);
            pakImageResolver.loadPakFiles();
            Assert.IsTrue(ItemDatabase.all.Count >= 269, $"items: {ItemDatabase.all.Count}");
            Assert.IsTrue(EnchantmentDatabase.allEnchantments.Count >= 118, $"enchantments: {EnchantmentDatabase.allEnchantments.Count}");

            var stringLibrary = pakImageResolver.loadLanguageStrings("ru-RU");
            //Using Russian language in order to guarantee every string will not match the english key
            Assert.IsNotNull(stringLibrary);
            R.loadExternalStrings(stringLibrary);
            Assert.IsTrue(R.totalStringCount >= 2537, $"strings: {R.totalStringCount}");

            //Find all the missing and mismatched strings

            foreach(var item in ItemDatabase.all)
            {
                Assert.AreNotEqual(item, R.itemName(item), $"itemName({item}) failed");
                Assert.AreNotEqual(item, R.itemDesc(item), $"itemDesc({item}) failed");
            }

            foreach(var enchantment in EnchantmentDatabase.allEnchantments)
            {
                Assert.AreNotEqual(enchantment, R.enchantmentName(enchantment), $"enchantmentName({enchantment}) failed");
                //R.enchantmentEffect(enchantment); //missing many of these strings
                Assert.AreNotEqual(enchantment, R.enchantmentDescription(enchantment), $"enchantmentDescription({enchantment}) failed");
            }

            foreach (var armorProperty in ItemDatabase.armorProperties)
            {
                Assert.AreNotEqual(armorProperty, R.armorProperty(armorProperty), $"armorProperty({armorProperty}) failed");
                Assert.AreNotEqual(armorProperty, R.armorPropertyDescription(armorProperty), $"armorPropertyDescription({armorProperty}) failed");
            }
        }

        [TestMethod]
        public async Task TestReadSaveFile()
        {
            var filePath = Path.Combine(TestUtilities.testDataDirectory(), "Blank.dat");
            //var filePath = Path.Combine(Constants.FILE_DIALOG_INITIAL_DIRECTORY, "2533274911688652", "Characters", "Blank.dat");
            using var stream = await TestUtilities.decryptFileIntoStream(filePath);
            stream!.Seek(0, SeekOrigin.Begin);
            var profile = await ProfileParser.Read(stream!);
            Assert.IsNotNull(profile);
            Assert.AreEqual(0, profile!.Xp);
            Assert.AreEqual(0, profile!.TotalGearPower);
        }

        private const string BLANK = @"
{
  ""bonus_prerequisites"": [],
  ""clone"": false,
  ""cosmetics"": [],
  ""cosmeticsEverEquipped"": [],
  ""creationDate"": ""Jun 23, 2020"",
  ""currency"": [],
  ""customized"": false,
  ""items"": [],
  ""itemsFound"": [],
  ""name"": """",
  ""playerId"": ""C2BC12F8-4800-51B5-D9E3-6F9E2865F96D"",
  ""progressionKeys"": [],
  ""skin"": ""steve"",
  ""timestamp"": 1592978132,
  ""totalGearPower"": 0,
  ""trialsCompleted"": [],
  ""version"": 1,
  ""xp"": 0
}
";
        [TestMethod]
        public async Task TestHandleReadAndWriteBlank()
        {
            using var stream = TestUtilities.generateStreamFromString(BLANK);

            var copy = new MemoryStream();
            await stream!.CopyToAsync(copy);

            stream!.Seek(0, SeekOrigin.Begin);
            var profile = await ProfileParser.Read(stream!);
            using var output = await ProfileParser.Write(profile);
            verifyNoDataLossOnWrite(copy, output);
        }

        [DataRow("NoEnchantments.dat")]
        [DataRow("ReasonableCheating.dat")]
        [DataRow("Casual.dat")]
        [DataRow("Power200.dat")]
        [DataRow("Blank.dat")]
        [DataRow("SwitchFile.dat")]
        [DataRow("UnreasonableCheating.dat")]
        [DataTestMethod]
        public async Task TestNoDataLossOnWrite(string filename)
        {
            //These used to point at the original author's own save folder
            //(profile 2533274911688652), so they could never pass anywhere else.
            //Anything not committed under TestData is skipped, not failed.
            var testDataDirectory = TestUtilities.testDataDirectory();
            var filePath = Path.Combine(testDataDirectory, filename);
            if (!File.Exists(filePath))
            {
                Assert.Inconclusive($"{filename} is not committed under TestData.");
                return;
            }
            using var stream = await TestUtilities.decryptFileIntoStream(filePath);

            var copy = new MemoryStream();
            await stream!.CopyToAsync(copy);

            stream!.Seek(0, SeekOrigin.Begin);
            var profile = await ProfileParser.Read(stream!);
            using var output = await ProfileParser.Write(profile);
            verifyNoDataLossOnWrite(copy, output);
        }

        private void verifyNoDataLossOnWrite(Stream input, Stream output)
        {
            //Compared as JSON rather than line by line. The old line diff skipped a single
            //"null," line to cope with fields the writer omits, but a save with several of
            //them (Blank.dat has three) left the two lists permanently out of step and every
            //later line was reported as a mismatch. Dropping a null-valued key loses nothing.
            var lost = TestUtilities.findLostValues(readJson(input), readJson(output)).ToArray();
            Assert.AreEqual(0, lost.Length, "values lost on write: " + string.Join(", ", lost));
        }

        private static string readJson(Stream stream)
        {
            stream.Seek(0, SeekOrigin.Begin);
            return stream.readAllText();
        }
        
        private IEnumerable<string> getLinesFromJsonStream(Stream stream)
        {
            stream.Seek(0, SeekOrigin.Begin);
            var streamStr = stream.readAllText();
            var reader = new StringReader(Utilities.prettyJson(streamStr));
            return reader.readAllLines();
        }

        [TestMethod]
        public void TestProfile()
        {
            var profile = new ProfileSaveFile();
            Assert.IsNotNull(profile);
        }
    }
}
