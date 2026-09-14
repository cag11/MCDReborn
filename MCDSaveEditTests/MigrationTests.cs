using DungeonTools.Save.File;
using MCDSaveEdit.Save.Models.Profiles;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using PakReader;
using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
#nullable enable

namespace MCDSaveEditTests
{
    /// <summary>
    /// Regression cover for the net48 -> net10 migration. Each of these pins down a
    /// behaviour that was verified by hand while porting, so a future change cannot
    /// quietly undo it. They run under both target frameworks.
    /// </summary>
    [TestClass]
    public class MigrationTests
    {
        /// <summary>
        /// A Stream that never returns more than <see cref="_maxPerRead"/> bytes at a time,
        /// which is legal and is exactly what the old code assumed would never happen.
        /// </summary>
        private sealed class DripStream : Stream
        {
            private readonly byte[] _data;
            private readonly int _maxPerRead;
            private int _position;

            public DripStream(byte[] data, int maxPerRead)
            {
                _data = data;
                _maxPerRead = maxPerRead;
            }

            public override int Read(byte[] buffer, int offset, int count)
            {
                int remaining = _data.Length - _position;
                int toCopy = Math.Min(Math.Min(count, _maxPerRead), remaining);
                Array.Copy(_data, _position, buffer, offset, toCopy);
                _position += toCopy;
                return toCopy;
            }

            public override bool CanRead => true;
            public override bool CanSeek => true;
            public override bool CanWrite => false;
            public override long Length => _data.Length;
            public override long Position { get => _position; set => _position = (int)value; }
            public override long Seek(long offset, SeekOrigin origin)
            {
                _position = origin switch
                {
                    SeekOrigin.Begin => (int)offset,
                    SeekOrigin.Current => _position + (int)offset,
                    _ => _data.Length + (int)offset,
                };
                return _position;
            }
            public override void Flush() { }
            public override void SetLength(long value) => throw new NotSupportedException();
            public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        }

        // ---------- BinaryHelper.ReadFully: the CA2022 fix in PakReader ----------

        [TestMethod]
        public void ReadFully_FillsBufferEvenWhenStreamDripsOneByteAtATime()
        {
            var payload = Enumerable.Range(0, 64).Select(i => (byte)i).ToArray();
            var buffer = new byte[payload.Length];

            BinaryHelper.ReadFully(new DripStream(payload, maxPerRead: 1), buffer, 0, buffer.Length);

            CollectionAssert.AreEqual(payload, buffer,
                "a short read must be retried until the buffer is full, not silently accepted");
        }

        [TestMethod]
        public void ReadFully_RespectsOffsetAndCount()
        {
            var payload = new byte[] { 1, 2, 3, 4 };
            var buffer = new byte[8];

            BinaryHelper.ReadFully(new DripStream(payload, maxPerRead: 2), buffer, 2, 4);

            CollectionAssert.AreEqual(new byte[] { 0, 0, 1, 2, 3, 4, 0, 0 }, buffer);
        }

        [TestMethod]
        public void ReadFully_ThrowsWhenStreamEndsEarly()
        {
            var buffer = new byte[16];
            Assert.ThrowsException<EndOfStreamException>(
                () => BinaryHelper.ReadFully(new DripStream(new byte[4], maxPerRead: 4), buffer, 0, buffer.Length),
                "running out of data must be loud, not a half-filled buffer");
        }

        // ---------- SaveFileHandler: the CA2022 fix in DungeonTools ----------

        private static readonly byte[] Magic = { 0x44, 0x30, 0x30, 0x31, 0x00, 0x00, 0x00, 0x00 };

        [TestMethod]
        public void StartsWithMagic_DetectsEncryptedFileEvenWhenStreamDrips()
        {
            var data = Magic.Concat(Encoding.ASCII.GetBytes("payload")).ToArray();

            // One byte per read is where the original code silently reported "not encrypted".
            Assert.IsTrue(SaveFileHandler.StartsWithMagic(new DripStream(data, maxPerRead: 1)),
                "a drip-fed encrypted file must still be recognised as encrypted");
        }

        [TestMethod]
        public void StartsWithMagic_RejectsPlainJson()
        {
            var data = Encoding.ASCII.GetBytes("{\"xp\":0,\"version\":1,\"padding\":\"xxxxxxxx\"}");
            Assert.IsFalse(SaveFileHandler.StartsWithMagic(new MemoryStream(data)));
        }

        [TestMethod]
        public void StartsWithMagic_RejectsStreamShorterThanTheMagic()
        {
            Assert.IsFalse(SaveFileHandler.StartsWithMagic(new MemoryStream(new byte[] { 0x44, 0x30 })));
        }

        // ---------- AesEncryptionProvider: the SYSLIB0021 Aes.Create() change ----------

        [TestMethod]
        public async Task Aes_EncryptThenDecrypt_RoundTripsExactly()
        {
            // 1024 is a whole number of 16-byte blocks, so Zeros padding adds nothing back.
            var plaintext = Enumerable.Range(0, 1024).Select(i => (byte)(i % 251)).ToArray();

            using var encrypted = await AesEncryptionProvider.EncryptAsync(new MemoryStream(plaintext));
            encrypted.Seek(0, SeekOrigin.Begin);
            using var decrypted = await AesEncryptionProvider.DecryptAsync(encrypted);

            var result = new MemoryStream();
            decrypted.Seek(0, SeekOrigin.Begin);
            await decrypted.CopyToAsync(result);

            CollectionAssert.AreEqual(plaintext, result.ToArray().Take(plaintext.Length).ToArray());
        }

        [TestMethod]
        public async Task Aes_IsDeterministic()
        {
            // ECB with a fixed key and IV: the same input must always give the same bytes.
            var plaintext = Encoding.UTF8.GetBytes("minecraft dungeons save payload");

            using var first = await AesEncryptionProvider.EncryptAsync(new MemoryStream(plaintext));
            using var second = await AesEncryptionProvider.EncryptAsync(new MemoryStream(plaintext));

            CollectionAssert.AreEqual(readAll(first), readAll(second));
        }

        // ---------- End to end on a committed save ----------

        [TestMethod]
        public async Task BlankSave_DecryptsToParseableJsonAndSurvivesAWriteCycle()
        {
            var filePath = Path.Combine(TestUtilities.testDataDirectory(), "Blank.dat");
            Assert.IsTrue(File.Exists(filePath), $"missing test data: {filePath}");

            using var decrypted = await TestUtilities.decryptFileIntoStream(filePath);
            Assert.IsNotNull(decrypted);

            decrypted!.Seek(0, SeekOrigin.Begin);
            var text = new StreamReader(decrypted, Encoding.UTF8).ReadToEnd();
            StringAssert.StartsWith(text.TrimStart(), "{", "decryption should yield JSON, not noise");

            decrypted.Seek(0, SeekOrigin.Begin);
            var profile = await ProfileParser.Read(decrypted);
            Assert.IsNotNull(profile);

            using var written = await ProfileParser.Write(profile);
            Assert.IsTrue(written.Length > 0, "writing the profile back must produce output");
        }

        [TestMethod]
        public void PrependMagicToEncrypted_ProducesAStreamThatReadsBackAsEncrypted()
        {
            var body = Encoding.ASCII.GetBytes("ciphertext-stand-in");

            var withMagic = SaveFileHandler.PrependMagicToEncrypted(new MemoryStream(body));
            withMagic.Seek(0, SeekOrigin.Begin);

            Assert.IsTrue(SaveFileHandler.IsFileEncrypted(withMagic));
        }

        private static byte[] readAll(Stream stream)
        {
            stream.Seek(0, SeekOrigin.Begin);
            var memory = new MemoryStream();
            stream.CopyTo(memory);
            return memory.ToArray();
        }
    }
}
