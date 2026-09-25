using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
#nullable enable

namespace MCDSaveEdit.Logic
{
    /// <summary>
    /// The game's compiled text, `Localization/Game/&lt;culture&gt;/Game.locres`, read and written.
    ///
    /// An item's name is `NSLOCTEXT("ItemType", "&lt;id&gt;", "&lt;English&gt;")` in the game's C++.
    /// At run time the engine looks the namespace and key up in the locres and shows what it finds
    /// there - but only if the entry's SourceStringHash matches the English the C++ carries, so a
    /// translation written against old wording is not shown against new. Changing a name is
    /// therefore: keep the entry's hash, change its string. For a key the file does not have, the
    /// hash is computed from the English the game compiled in.
    ///
    /// This is the "optimized" format 4.22 writes, version 2: magic, version, the offset of a
    /// string table, the entry count, then each namespace and each key with its CRC32, its
    /// source hash and an index into the string table - which holds every string once, with how
    /// many entries use it. A file this writes back without changes is byte for byte the file it
    /// read, which is how the writer is checked.
    /// </summary>
    public sealed class Locres
    {
        private static readonly byte[] MAGIC =
        {
            0x0E, 0x14, 0x74, 0x75, 0x67, 0x4A, 0x03, 0xFC, 0x4A, 0x15, 0x90, 0x9D, 0xC3, 0x37, 0x7F, 0x1B,
        };

        public sealed class Entry
        {
            public string Key { get; set; } = "";
            /// <summary>An empty key the game wrote as length 1 and a terminator rather than 0.</summary>
            public bool KeyNul { get; set; }
            public uint KeyHash { get; set; }
            public uint SourceHash { get; set; }
            public int StringIndex { get; set; }
        }

        public sealed class Namespace
        {
            public string Name { get; set; } = "";
            /// <summary>
            /// An empty name the game wrote as length 1 and a lone terminator, rather than 0. The
            /// default namespace is spelled this way, and writing it the other way moves the file
            /// by a byte - harmless to the engine, but it breaks the byte-for-byte check.
            /// </summary>
            public bool NameNul { get; set; }
            public uint Hash { get; set; }
            public List<Entry> Entries { get; } = new();
        }

        private sealed class Text
        {
            public string Value = "";
            public bool Nul;
            public int RefCount;
        }

        public List<Namespace> Namespaces { get; } = new();
        private readonly List<Text> _strings = new();

        /// <summary>Null when this is not a version 2 locres.</summary>
        public static Locres? read(byte[] data)
        {
            try
            {
                if (data.Length < 33 || !data.AsSpan(0, 16).SequenceEqual(MAGIC) || data[16] != 2) { return null; }
                var made = new Locres();
                var at = 17;
                var tableAt = (int)BitConverter.ToInt64(data, at); at += 8;

                var table = tableAt;
                var count = BitConverter.ToInt32(data, table); table += 4;
                for (var i = 0; i < count; i++)
                {
                    var value = readString(data, ref table, out var nul);
                    made._strings.Add(new Text { Value = value, Nul = nul, RefCount = BitConverter.ToInt32(data, table) });
                    table += 4;
                }

                at += 4;                                                    //entry count, recomputed
                var namespaces = BitConverter.ToUInt32(data, at); at += 4;
                for (var n = 0; n < namespaces; n++)
                {
                    var space = new Namespace { Hash = BitConverter.ToUInt32(data, at) };
                    at += 4;
                    space.Name = readString(data, ref at, out var nameNul);
                    space.NameNul = nameNul;
                    var keys = BitConverter.ToUInt32(data, at); at += 4;
                    for (var k = 0; k < keys; k++)
                    {
                        var entry = new Entry { KeyHash = BitConverter.ToUInt32(data, at) };
                        at += 4;
                        entry.Key = readString(data, ref at, out var keyNul);
                        entry.KeyNul = keyNul;
                        entry.SourceHash = BitConverter.ToUInt32(data, at); at += 4;
                        entry.StringIndex = BitConverter.ToInt32(data, at); at += 4;
                        space.Entries.Add(entry);
                    }
                    made.Namespaces.Add(space);
                }
                return at == tableAt ? made : null;
            }
            catch (Exception)
            {
                return null;
            }
        }

        public byte[] write()
        {
            var body = new MemoryStream();
            var w = new BinaryWriter(body);
            w.Write(MAGIC);
            w.Write((byte)2);
            w.Write(0L);                                                    //string table offset, below
            w.Write(Namespaces.Sum(n => n.Entries.Count));
            w.Write(Namespaces.Count);
            foreach (var space in Namespaces)
            {
                w.Write(space.Hash);
                writeString(w, space.Name, space.NameNul);
                w.Write(space.Entries.Count);
                foreach (var entry in space.Entries)
                {
                    w.Write(entry.KeyHash);
                    writeString(w, entry.Key, entry.KeyNul);
                    w.Write(entry.SourceHash);
                    w.Write(entry.StringIndex);
                }
            }
            var tableAt = body.Position;
            w.Write(_strings.Count);
            foreach (var text in _strings)
            {
                writeString(w, text.Value, text.Nul);
                w.Write(text.RefCount);
            }
            w.Flush();
            var bytes = body.ToArray();
            BitConverter.GetBytes(tableAt).CopyTo(bytes, 17);
            return bytes;
        }

        public string? get(string space, string key)
        {
            var entry = Namespaces.FirstOrDefault(n => n.Name == space)?.Entries.FirstOrDefault(e => e.Key == key);
            return entry == null ? null : _strings[entry.StringIndex].Value;
        }

        /// <summary>
        /// Sets one string. An existing entry keeps its source hash - that is what the game checks it
        /// against - and a new one takes the hash of <paramref name="englishSource"/>, the text the
        /// game's C++ carries for this key. False when a new entry is needed and no source was given.
        /// </summary>
        public bool set(string space, string key, string value, string? englishSource)
        {
            var ns = Namespaces.FirstOrDefault(n => n.Name == space);
            if (ns == null)
            {
                if (englishSource == null) { return false; }
                ns = new Namespace { Name = space, Hash = strCrc32(space) };
                Namespaces.Add(ns);
            }
            var entry = ns.Entries.FirstOrDefault(e => e.Key == key);
            if (entry == null)
            {
                if (englishSource == null) { return false; }
                entry = new Entry { Key = key, KeyHash = strCrc32(key), SourceHash = strCrc32(englishSource), StringIndex = -1 };
                ns.Entries.Add(entry);
            }
            else if (_strings[entry.StringIndex].Value == value)
            {
                return true;
            }
            else
            {
                _strings[entry.StringIndex].RefCount--;
            }

            var existing = _strings.FindIndex(s => s.Value == value);
            if (existing < 0)
            {
                _strings.Add(new Text { Value = value });
                existing = _strings.Count - 1;
            }
            _strings[existing].RefCount++;
            entry.StringIndex = existing;
            return true;
        }

        /// <summary>
        /// FCrc::StrCrc32: the reflected CRC32 table, every character fed as four bytes, low first,
        /// starting from ~0 and inverted at the end. What 4.22 hashes locres keys and sources with.
        /// </summary>
        public static uint strCrc32(string text)
        {
            var crc = 0xFFFFFFFFu;
            foreach (var ch in text)
            {
                uint c = ch;
                for (var i = 0; i < 4; i++)
                {
                    crc = (crc >> 8) ^ TABLE[(crc ^ c) & 0xFF];
                    c >>= 8;
                }
            }
            return ~crc;
        }

        private static readonly uint[] TABLE = Enumerable.Range(0, 256).Select(i =>
        {
            var c = (uint)i;
            for (var k = 0; k < 8; k++) { c = (c & 1) != 0 ? (c >> 1) ^ 0xEDB88320 : c >> 1; }
            return c;
        }).ToArray();

        private static string readString(byte[] from, ref int at, out bool emptyWithNul)
        {
            var length = BitConverter.ToInt32(from, at);
            at += 4;
            emptyWithNul = length == 1 || length == -1;
            if (length == 0) { return string.Empty; }
            if (length > 0)
            {
                var text = Encoding.ASCII.GetString(from, at, length - 1);
                at += length;
                return text;
            }
            var wide = Encoding.Unicode.GetString(from, at, (-length - 1) * 2);
            at += -length * 2;
            return wide;
        }

        /// <summary>ANSI when every character is below 128, UTF-16 otherwise - the engine's own rule.</summary>
        private static void writeString(BinaryWriter writer, string text, bool emptyWithNul = false)
        {
            if (text.Length == 0 && emptyWithNul) { writer.Write(1); writer.Write((byte)0); return; }
            if (text.Length == 0) { writer.Write(0); return; }
            if (text.All(c => c < 128))
            {
                writer.Write(text.Length + 1);
                writer.Write(Encoding.ASCII.GetBytes(text));
                writer.Write((byte)0);
                return;
            }
            writer.Write(-(text.Length + 1));
            writer.Write(Encoding.Unicode.GetBytes(text));
            writer.Write((short)0);
        }
    }
}
