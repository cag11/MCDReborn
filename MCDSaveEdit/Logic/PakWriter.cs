using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Text;
#nullable enable

namespace MCDSaveEdit.Logic
{
    /// <summary>
    /// Writes a Minecraft Dungeons mod pak: a handful of files at the game's own asset paths,
    /// which the engine then loads in place of the originals.
    ///
    /// Deliberately the simplest pak that works - version 3, no compression, no encryption.
    /// The game's own paks are encrypted and compressed, but a mod does not have to be either,
    /// which is what the Squid Game armour pak on this machine is: version 3, plaintext index.
    /// Matching that keeps this to a few hundred bytes of structure around the file data.
    ///
    /// Layout, per UE4's FPakFile:
    ///
    ///   for each file:  entry header (skipped on read), then the bytes
    ///   index:          mount point, count, then name + entry for each file
    ///   footer:         magic, version, index offset, index size, index hash
    ///
    /// The entry header before each file is the same shape as the one in the index. Readers
    /// seek past it using the size they worked out from the index, so its contents are never
    /// looked at - UnrealPak writes a zero offset there and so does this.
    /// </summary>
    public static class PakWriter
    {
        private const uint PAK_MAGIC = 0x5A6F12E1;
        private const int PAK_VERSION = 3;

        /// <summary>What UE mounts the paths in the index relative to.</summary>
        public const string MOUNT_POINT = "../../../";

        public sealed class Entry
        {
            /// <summary>Path inside the pak, relative to the mount point, e.g. "Dungeons/Content/...".</summary>
            public string Path { get; }
            public byte[] Data { get; }

            public Entry(string path, byte[] data)
            {
                Path = path.Replace('\\', '/').TrimStart('/');
                Data = data;
            }
        }

        public static void write(string outputPath, IEnumerable<Entry> entries)
        {
            var list = new List<Entry>(entries);
            if (list.Count == 0) { throw new ArgumentException("A pak needs at least one file.", nameof(entries)); }

            using var stream = File.Create(outputPath);
            using var writer = new BinaryWriter(stream, Encoding.ASCII, leaveOpen: true);

            var records = new List<(Entry entry, long offset, byte[] hash)>(list.Count);
            foreach (var entry in list)
            {
                var offset = stream.Position;
                var hash = SHA1.HashData(entry.Data);
                //The header copy: same fields, zero offset, never read back.
                writeEntry(writer, 0, entry.Data.LongLength, hash);
                writer.Write(entry.Data);
                records.Add((entry, offset, hash));
            }

            //The index, built in memory because the footer has to state its size and hash.
            byte[] index;
            using (var indexStream = new MemoryStream())
            using (var indexWriter = new BinaryWriter(indexStream, Encoding.ASCII, leaveOpen: true))
            {
                writeString(indexWriter, MOUNT_POINT);
                indexWriter.Write(records.Count);
                foreach (var (entry, offset, hash) in records)
                {
                    writeString(indexWriter, entry.Path);
                    writeEntry(indexWriter, offset, entry.Data.LongLength, hash);
                }
                indexWriter.Flush();
                index = indexStream.ToArray();
            }

            var indexOffset = stream.Position;
            writer.Write(index);

            writer.Write(PAK_MAGIC);
            writer.Write(PAK_VERSION);
            writer.Write(indexOffset);
            writer.Write((long)index.Length);
            writer.Write(SHA1.HashData(index));
            writer.Flush();
        }

        /// <summary>
        /// One FPakEntry as version 3 writes it. Size and uncompressed size are equal because
        /// nothing here is compressed, and the trailing flags and block size are the fields
        /// COMPRESSION_ENCRYPTION added - both zero, meaning plain and unencrypted.
        /// </summary>
        private static void writeEntry(BinaryWriter writer, long offset, long size, byte[] hash)
        {
            writer.Write(offset);
            writer.Write(size);
            writer.Write(size);
            writer.Write(0);            //compression method: none
            writer.Write(hash);         //20 bytes
            writer.Write((byte)0);      //flags: not encrypted, not deleted
            writer.Write(0u);           //compression block size
        }

        /// <summary>
        /// UE's FString: a length that counts the terminator, then ASCII with that terminator.
        /// </summary>
        private static void writeString(BinaryWriter writer, string value)
        {
            var bytes = Encoding.ASCII.GetBytes(value);
            writer.Write(bytes.Length + 1);
            writer.Write(bytes);
            writer.Write((byte)0);
        }
    }
}
