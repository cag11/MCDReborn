using MCDSaveEdit.Logic;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using System;
using System.IO;
using System.Linq;
#nullable enable

namespace MCDSaveEditTests
{
    [TestClass]
    public class SaveBackupTests
    {
        private string _directory = string.Empty;

        [TestInitialize]
        public void Setup()
        {
            _directory = Path.Combine(Path.GetTempPath(), "MCDReborn_backup_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_directory);
        }

        [TestCleanup]
        public void Cleanup()
        {
            try { Directory.Delete(_directory, recursive: true); } catch { }
        }

        private string writeSave(string name, string content)
        {
            var path = Path.Combine(_directory, name);
            File.WriteAllText(path, content);
            return path;
        }

        private string[] backupsOf(string savePath)
            => Directory.GetFiles(_directory, Path.GetFileName(savePath) + ".*" + SaveBackup.BACKUP_EXTENSION);

        [TestMethod]
        public void TestBackupCopiesTheFileAsItWas()
        {
            var save = writeSave("character.dat", "original");

            var backup = SaveBackup.backup(save);

            Assert.IsNotNull(backup);
            Assert.IsTrue(File.Exists(backup!));
            Assert.AreEqual("original", File.ReadAllText(backup!));
            //The save itself is untouched; backing up is a copy, not a move.
            Assert.AreEqual("original", File.ReadAllText(save));
        }

        [TestMethod]
        public void TestOnlyOneBackupIsKept()
        {
            var save = writeSave("character.dat", "first");
            var first = SaveBackup.backup(save);

            File.WriteAllText(save, "second");
            //Distinct timestamp, since the name is to the second.
            System.Threading.Thread.Sleep(1100);
            var second = SaveBackup.backup(save);

            Assert.AreNotEqual(first, second);
            var remaining = backupsOf(save);
            Assert.AreEqual(1, remaining.Length, string.Join(", ", remaining.Select(Path.GetFileName)));
            Assert.AreEqual(second, remaining[0]);
            //And it holds what the file looked like before this save, not before the last one.
            Assert.AreEqual("second", File.ReadAllText(second!));
        }

        [TestMethod]
        public void TestBackupsOfOtherSavesInTheSameFolderSurvive()
        {
            //Characters live side by side in one folder, so pruning must not reach across.
            var mine = writeSave("mine.dat", "mine");
            var theirs = writeSave("theirs.dat", "theirs");
            var theirBackup = SaveBackup.backup(theirs);

            SaveBackup.backup(mine);

            Assert.IsTrue(File.Exists(theirBackup!));
            Assert.AreEqual(1, backupsOf(mine).Length);
            Assert.AreEqual(1, backupsOf(theirs).Length);
        }

        [TestMethod]
        public void TestNothingToBackUpWhenTheFileDoesNotExist()
        {
            //Save As to a new path: there is no previous version to preserve.
            var path = Path.Combine(_directory, "brand-new.dat");
            Assert.IsNull(SaveBackup.backup(path));
            Assert.AreEqual(0, Directory.GetFiles(_directory).Length);
        }

        [TestMethod]
        public void TestNullAndEmptyPathsAreIgnored()
        {
            Assert.IsNull(SaveBackup.backup(null));
            Assert.IsNull(SaveBackup.backup(string.Empty));
            Assert.IsNull(SaveBackup.backup("   "));
        }

        [TestMethod]
        public void TestBackupNameCarriesTheOriginalNameAndATimestamp()
        {
            var when = new DateTime(2026, 9, 14, 16, 30, 12);
            var path = SaveBackup.pathFor(@"C:\saves\character.dat", when);

            Assert.AreEqual(@"C:\saves\character.dat.2026-09-14_163012.bak", path);
        }

        [TestMethod]
        public void TestAFailedBackupDoesNotThrow()
        {
            //A save must never be blocked by the backup failing - a path that cannot exist
            //stands in for a read-only folder or a full disk.
            Assert.IsNull(SaveBackup.backup(Path.Combine(_directory, "no-such-folder", "character.dat")));
        }
    }
}
