using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
#nullable enable

namespace MCDSaveEdit.Logic
{
    /// <summary>
    /// Renames inside a cooked 4.22 package when the new spelling is a different length.
    ///
    /// NewContent.renameNames only ever writes bytes over bytes, which is why it could never
    /// copy HeavyCrossbow to SpiderCrossbow: one letter longer, every path it touches. This
    /// rebuilds the two places a package spells its own paths - the name table and the asset
    /// registry block - and moves everything the header says lives after them.
    ///
    /// Every offset in a package header is absolute from the start of the .uasset, and the
    /// .uexp continues it. So growing the header by d means: each summary offset past the
    /// change moves by d, TotalHeaderSize moves by d, BulkDataStartOffset moves by d, and each
    /// export's SerialOffset moves by d. The .uexp itself does not change a byte - an export's
    /// place in it is SerialOffset minus TotalHeaderSize, and both moved together.
    /// </summary>
    public static class PackageRename
    {
        /// <summary>Where each thing the summary records sits, found by stepping.</summary>
        private sealed class Summary
        {
            public int TotalHeaderSizeAt;
            public int NameCountAt;
            public int[] OffsetsAt = Array.Empty<int>();    //int32 offsets into the header
            public int BulkDataStartAt;                      //the one int64
            public int ExportCountAt;
            public int ExportOffsetAt;
            public int AssetRegistryAt;
            public int End;
        }

        /// <summary>
        /// A renamed copy of <paramref name="uasset"/>, or null when it is not a package this
        /// understands. <paramref name="changed"/> counts the names and strings that moved.
        /// </summary>
        /// <param name="keepForNativeImports">
        /// Names that keep their old spelling where they name a native class this package imports.
        /// A name entry is shared by everything that spells it, and an item's bare id is often
        /// also its C++ class: CorruptedBeacon's Instance derives from /Script/Dungeons.CorruptedBeacon.
        /// Renaming the id would repoint that parent at a class that does not exist. For these, the
        /// renamed entry keeps serving the id's values, and a new entry with the old spelling is
        /// added for the class import alone.
        /// </param>
        public static byte[]? rename(byte[] uasset, Func<string, string?> renamedOf, out int changed,
            ICollection<string>? keepForNativeImports = null)
        {
            changed = 0;
            var summary = read(uasset);
            if (summary == null) { return null; }

            var bytes = uasset;

            //The name table.
            var nameCount = BitConverter.ToInt32(bytes, summary.NameCountAt);
            var nameOffset = BitConverter.ToInt32(bytes, summary.NameCountAt + 4);
            var table = new MemoryStream();
            var writer = new BinaryWriter(table);
            var kept = new List<(int index, string text)>();
            var at = nameOffset;
            void writeName(string spelled)
            {
                writer.Write(spelled.Length + 1);
                writer.Write(Encoding.ASCII.GetBytes(spelled));
                writer.Write((byte)0);
                writer.Write(NewContent.nonCaseHash(spelled));
                writer.Write(NewContent.caseHash(spelled));
            }
            for (var i = 0; i < nameCount; i++)
            {
                var length = BitConverter.ToInt32(bytes, at);
                if (length < 0)
                {
                    //Wide: copied untouched, hashes and all. Nothing renamed here is ever wide.
                    var size = 4 + -length * 2 + 4;
                    writer.Write(bytes, at, size);
                    at += size;
                    continue;
                }

                var text = Encoding.ASCII.GetString(bytes, at + 4, Math.Max(0, length - 1));
                var now = renamedOf(text);
                if (now != null && now != text)
                {
                    changed++;
                    if (keepForNativeImports != null && keepForNativeImports.Contains(text)) { kept.Add((i, text)); }
                }
                writeName(now ?? text);
                at += 4 + length + 4;
            }

            //Only those that really are a native class import get their old spelling back.
            var nativeImports = kept.Count == 0 ? new List<int>() : nativeClassImports(bytes, summary, kept.ConvertAll(k => k.index));
            var restored = new Dictionary<int, int>();
            foreach (var (index, text) in kept)
            {
                if (!nativeImports.Contains(index)) { continue; }
                restored[index] = nameCount + restored.Count;
                writeName(text);
            }
            writer.Flush();
            bytes = splice(bytes, summary, nameOffset, at - nameOffset, table.ToArray());

            if (restored.Count > 0)
            {
                BitConverter.GetBytes(nameCount + restored.Count).CopyTo(bytes, summary.NameCountAt);
                var importCount = BitConverter.ToInt32(bytes, summary.NameCountAt + 24);
                var importOffset = BitConverter.ToInt32(bytes, summary.NameCountAt + 28);
                foreach (var entry in nativeImportEntries(bytes, importCount, importOffset, restored.Keys))
                {
                    var index = BitConverter.ToInt32(bytes, entry + 20);
                    BitConverter.GetBytes(restored[index]).CopyTo(bytes, entry + 20);
                }
            }

            //The asset registry block: object paths, class names and tag values, as FStrings. The
            //game's own tag here is ItemIdName, so a copy left saying HeavyCrossbow would claim to
            //be HeavyCrossbow to anything that asks the registry.
            var registryAt = BitConverter.ToInt32(bytes, summary.AssetRegistryAt);
            if (registryAt > 0 && registryAt < BitConverter.ToInt32(bytes, summary.TotalHeaderSizeAt))
            {
                var registry = new MemoryStream();
                var out2 = new BinaryWriter(registry);
                at = registryAt;
                var objects = BitConverter.ToInt32(bytes, at); at += 4;
                out2.Write(objects);
                for (var i = 0; i < objects; i++)
                {
                    copyString(bytes, ref at, out2, renamedOf, ref changed);    //object path
                    copyString(bytes, ref at, out2, renamedOf, ref changed);    //class name
                    var tags = BitConverter.ToInt32(bytes, at); at += 4;
                    out2.Write(tags);
                    for (var t = 0; t < tags; t++)
                    {
                        copyString(bytes, ref at, out2, renamedOf, ref changed);
                        copyString(bytes, ref at, out2, renamedOf, ref changed);
                    }
                }
                out2.Flush();
                bytes = splice(bytes, summary, registryAt, at - registryAt, registry.ToArray());
            }

            return bytes;
        }

        /// <summary>
        /// <paramref name="uasset"/> with <paramref name="names"/> added to the end of its name
        /// table - those it lacks - and every offset after the table moved. Null when it is not a
        /// package this understands. Existing names keep their indexes, so nothing that refers to
        /// one needs changing.
        /// </summary>
        public static byte[]? withNames(byte[] uasset, IEnumerable<string> names)
        {
            var summary = read(uasset);
            if (summary == null) { return null; }
            var have = new HashSet<string>(CookedProperties.readNamesOf(uasset), StringComparer.Ordinal);
            var missing = names.Where(n => have.Add(n)).ToList();
            if (missing.Count == 0) { return uasset; }

            var nameCount = BitConverter.ToInt32(uasset, summary.NameCountAt);
            var at = BitConverter.ToInt32(uasset, summary.NameCountAt + 4);
            for (var i = 0; i < nameCount; i++)
            {
                var length = BitConverter.ToInt32(uasset, at);
                at += 4 + (length < 0 ? -length * 2 : length) + 4;
            }

            var table = new MemoryStream();
            var writer = new BinaryWriter(table);
            foreach (var spelled in missing)
            {
                writer.Write(spelled.Length + 1);
                writer.Write(Encoding.ASCII.GetBytes(spelled));
                writer.Write((byte)0);
                writer.Write(NewContent.nonCaseHash(spelled));
                writer.Write(NewContent.caseHash(spelled));
            }
            writer.Flush();
            var made = splice(uasset, summary, at, 0, table.ToArray());
            BitConverter.GetBytes(nameCount + missing.Count).CopyTo(made, summary.NameCountAt);
            return made;
        }

        /// <summary>
        /// Records that export <paramref name="index"/> grew by <paramref name="delta"/> bytes of
        /// data in the .uexp: its SerialSize, the SerialOffset of every export whose data lies
        /// after it, and BulkDataStartOffset. The header's own length does not change.
        /// </summary>
        public static bool exportGrew(byte[] uasset, int index, int delta)
        {
            var summary = read(uasset);
            if (summary == null) { return false; }
            var exports = BitConverter.ToInt32(uasset, summary.ExportCountAt);
            var table = BitConverter.ToInt32(uasset, summary.ExportOffsetAt);
            const int ENTRY = 104;
            if (index < 0 || index >= exports) { return false; }
            var entry = table + index * ENTRY;
            var mine = BitConverter.ToInt64(uasset, entry + 36);
            BitConverter.GetBytes(BitConverter.ToInt64(uasset, entry + 28) + delta).CopyTo(uasset, entry + 28);
            for (var i = 0; i < exports; i++)
            {
                var serial = table + i * ENTRY + 36;
                var value = BitConverter.ToInt64(uasset, serial);
                if (value > mine) { BitConverter.GetBytes(value + delta).CopyTo(uasset, serial); }
            }
            var bulk = BitConverter.ToInt64(uasset, summary.BulkDataStartAt);
            if (bulk > mine) { BitConverter.GetBytes(bulk + delta).CopyTo(uasset, summary.BulkDataStartAt); }
            return true;
        }

        /// <summary>The name indexes, out of <paramref name="candidates"/>, that some native class import is named by.</summary>
        private static List<int> nativeClassImports(byte[] bytes, Summary summary, List<int> candidates)
        {
            var importCount = BitConverter.ToInt32(bytes, summary.NameCountAt + 24);
            var importOffset = BitConverter.ToInt32(bytes, summary.NameCountAt + 28);
            var found = new List<int>();
            foreach (var entry in nativeImportEntries(bytes, importCount, importOffset, candidates))
            {
                var index = BitConverter.ToInt32(bytes, entry + 20);
                if (!found.Contains(index)) { found.Add(index); }
            }
            return found;
        }

        /// <summary>
        /// The import entries - ClassPackage, ClassName, OuterIndex, ObjectName, 28 bytes - of class
        /// `Class` whose outer is a /Script package and whose object name is one of <paramref name="names"/>.
        /// Read by name index, which renaming leaves where it was.
        /// </summary>
        private static IEnumerable<int> nativeImportEntries(byte[] bytes, int importCount, int importOffset, IEnumerable<int> names)
        {
            var wanted = new HashSet<int>(names);
            var nameTable = new List<string>(CookedProperties.readNamesOf(bytes));
            string nameAt(int at)
            {
                var index = BitConverter.ToInt32(bytes, at);
                return index >= 0 && index < nameTable.Count ? nameTable[index] : string.Empty;
            }
            const int ENTRY = 28;
            for (var i = 0; i < importCount; i++)
            {
                var entry = importOffset + i * ENTRY;
                if (!wanted.Contains(BitConverter.ToInt32(bytes, entry + 20))) { continue; }
                if (nameAt(entry + 8) != "Class") { continue; }
                var outer = BitConverter.ToInt32(bytes, entry + 16);
                if (outer >= 0 || -outer - 1 >= importCount) { continue; }
                if (!nameAt(importOffset + (-outer - 1) * ENTRY + 20).StartsWith("/Script/", StringComparison.Ordinal)) { continue; }
                yield return entry;
            }
        }

        /// <summary>
        /// Replaces <paramref name="length"/> bytes at <paramref name="at"/> and moves every
        /// recorded offset that lay after them.
        /// </summary>
        private static byte[] splice(byte[] bytes, Summary summary, int at, int length, byte[] with)
        {
            var delta = with.Length - length;
            var made = new byte[bytes.Length + delta];
            Buffer.BlockCopy(bytes, 0, made, 0, at);
            Buffer.BlockCopy(with, 0, made, at, with.Length);
            Buffer.BlockCopy(bytes, at + length, made, at + with.Length, bytes.Length - at - length);
            if (delta == 0) { return made; }

            var end = at + length;
            void move(int field)
            {
                var value = BitConverter.ToInt32(made, field);
                if (value >= end) { BitConverter.GetBytes(value + delta).CopyTo(made, field); }
            }

            move(summary.TotalHeaderSizeAt);
            foreach (var field in summary.OffsetsAt) { move(field); }

            var bulk = BitConverter.ToInt64(made, summary.BulkDataStartAt);
            if (bulk >= end) { BitConverter.GetBytes(bulk + delta).CopyTo(made, summary.BulkDataStartAt); }

            //Exports: read where the table is NOW, since it may just have moved.
            var exports = BitConverter.ToInt32(made, summary.ExportCountAt);
            var table = BitConverter.ToInt32(made, summary.ExportOffsetAt);
            const int ENTRY = 104;
            for (var i = 0; i < exports; i++)
            {
                var serial = table + i * ENTRY + 36;
                var value = BitConverter.ToInt64(made, serial);
                if (value >= end) { BitConverter.GetBytes(value + delta).CopyTo(made, serial); }
            }

            return made;
        }

        private static void copyString(byte[] bytes, ref int at, BinaryWriter into,
            Func<string, string?> renamedOf, ref int changed)
        {
            var length = BitConverter.ToInt32(bytes, at);
            if (length <= 0)
            {
                var size = 4 + (length < 0 ? -length * 2 : 0);
                into.Write(bytes, at, size);
                at += size;
                return;
            }

            var text = Encoding.ASCII.GetString(bytes, at + 4, length - 1);
            at += 4 + length;
            var now = renamedOf(text);
            if (now != null && now != text) { changed++; }
            var spelled = now ?? text;
            into.Write(spelled.Length + 1);
            into.Write(Encoding.ASCII.GetBytes(spelled));
            into.Write((byte)0);
        }

        /// <summary>
        /// The 4.22 summary, walked field by field. Returns null for anything it does not
        /// recognise, so a package of some other shape is left alone rather than damaged.
        /// </summary>
        private static Summary? read(byte[] b)
        {
            try
            {
                if (BitConverter.ToUInt32(b, 0) != 0x9E2A83C1) { return null; }
                var s = new Summary();
                var at = 4;
                var legacy = BitConverter.ToInt32(b, at); at += 4;
                if (legacy != -7) { return null; }
                at += 4 + 4 + 4;                                     //UE3, UE4, licensee
                var custom = BitConverter.ToInt32(b, at); at += 4 + custom * 20;
                s.TotalHeaderSizeAt = at; at += 4;
                skipString(b, ref at);                               //folder name
                at += 4;                                             //package flags
                s.NameCountAt = at;
                var offsets = new List<int>();
                //NameCount, NameOffset, GatherableTextDataCount, GatherableTextDataOffset,
                //ExportCount, ExportOffset, ImportCount, ImportOffset, DependsOffset,
                //SoftPackageReferencesCount, SoftPackageReferencesOffset, SearchableNamesOffset,
                //ThumbnailTableOffset.
                offsets.Add(at + 4);                                 //NameOffset
                offsets.Add(at + 12);                                //GatherableTextDataOffset
                s.ExportCountAt = at + 16;
                s.ExportOffsetAt = at + 20;
                offsets.Add(at + 20);                                //ExportOffset
                offsets.Add(at + 28);                                //ImportOffset
                offsets.Add(at + 32);                                //DependsOffset
                offsets.Add(at + 40);                                //SoftPackageReferencesOffset
                offsets.Add(at + 44);                                //SearchableNamesOffset
                offsets.Add(at + 48);                                //ThumbnailTableOffset
                at += 52;
                at += 16;                                            //guid
                var generations = BitConverter.ToInt32(b, at); at += 4 + generations * 8;
                for (var v = 0; v < 2; v++)                          //saved-by, compatible-with
                {
                    at += 2 + 2 + 2 + 4;
                    skipString(b, ref at);
                }
                at += 4;                                             //compression flags
                var chunks = BitConverter.ToInt32(b, at); at += 4;
                if (chunks != 0) { return null; }                    //a compressed package: not ours
                at += 4;                                             //package source
                var extra = BitConverter.ToInt32(b, at); at += 4;
                for (var i = 0; i < extra; i++) { skipString(b, ref at); }
                s.AssetRegistryAt = at;
                offsets.Add(at); at += 4;                            //AssetRegistryDataOffset
                s.BulkDataStartAt = at; at += 8;
                offsets.Add(at); at += 4;                            //WorldTileInfoDataOffset
                var ids = BitConverter.ToInt32(b, at); at += 4 + ids * 4;
                at += 4;                                             //PreloadDependencyCount
                offsets.Add(at); at += 4;                            //PreloadDependencyOffset
                s.OffsetsAt = offsets.ToArray();
                s.End = at;

                //The summary has to end where the name table begins, or this walked it wrong.
                return BitConverter.ToInt32(b, s.NameCountAt + 4) == s.End ? s : null;
            }
            catch (Exception)
            {
                return null;
            }
        }

        private static void skipString(byte[] b, ref int at)
        {
            var length = BitConverter.ToInt32(b, at);
            at += 4 + (length >= 0 ? length : -length * 2);
        }
    }
}
