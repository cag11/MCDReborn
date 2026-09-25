using System;
using System.Collections.Generic;
using System.IO;
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
        public static byte[]? rename(byte[] uasset, Func<string, string?> renamedOf, out int changed)
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
            var at = nameOffset;
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
                if (now != null && now != text) { changed++; }
                var spelled = now ?? text;

                writer.Write(spelled.Length + 1);
                writer.Write(Encoding.ASCII.GetBytes(spelled));
                writer.Write((byte)0);
                writer.Write(NewContent.nonCaseHash(spelled));
                writer.Write(NewContent.caseHash(spelled));
                at += 4 + length + 4;
            }
            writer.Flush();
            bytes = splice(bytes, summary, nameOffset, at - nameOffset, table.ToArray());

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
