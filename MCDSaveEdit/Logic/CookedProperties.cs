using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
#nullable enable

namespace MCDSaveEdit.Logic
{
    /// <summary>
    /// The tagged property lists inside a cooked asset, and where each value sits.
    ///
    /// Every Unreal object is saved the same way: a name, a type, a length, a value, repeated
    /// until a "None" ends it. PakReader reads those for the classes it models and gives up on the
    /// rest - a Blueprint throws before it reaches the properties - which is unfortunate, because
    /// the properties are in plain sight and the class around them does not matter for reading a
    /// float out of one.
    ///
    /// So this walks the lists itself and reports the byte offset of every value. That offset is
    /// the whole point: knowing a camera's arm length is 2450 is interesting, and knowing it is
    /// the float at byte 1552 is what lets it be changed.
    ///
    /// Only what is needed to find a value is understood. Sizes are taken from the tags rather
    /// than computed, so a property whose type this has never heard of is stepped over intact
    /// rather than guessed at.
    /// </summary>
    public static class CookedProperties
    {
        private const uint PACKAGE_MAGIC = 0x9E2A83C1;
        private const int EXPORT_ENTRY_SIZE = 104;
        private const int OBJECT_NAME_IN_ENTRY = 16;
        private const int SERIAL_SIZE_IN_ENTRY = 28;
        private const int SERIAL_OFFSET_IN_ENTRY = 36;

        public sealed class Value
        {
            public string Export { get; set; } = "";
            public string Name { get; set; } = "";
            public string Type { get; set; } = "";
            /// <summary>For a struct, what kind: Rotator, Vector, Guid.</summary>
            public string? StructName { get; set; }
            /// <summary>Where the value's bytes start in the .uexp. For a bool, the single tag byte.</summary>
            public int At { get; set; }
            public int Size { get; set; }
            public bool Flag { get; set; }

            public override string ToString() => $"{Export}.{Name} ({Type}) @{At}";
        }

        /// <summary>
        /// Every tagged property in the asset, in the order they are stored.
        ///
        /// Exports that do not begin with a readable property list are skipped rather than
        /// reported as failures: a Blueprint is mostly functions and property *definitions*, and
        /// only a few of its exports are objects with values on them.
        /// </summary>
        public static IReadOnlyList<Value> readAll(byte[] uasset, byte[] uexp)
        {
            var found = new List<Value>();
            if (!tryReadSummary(uasset, out var headerSize, out var names, out var exportCount, out var exportOffset))
            {
                return found;
            }

            for (int i = 0; i < exportCount; i++)
            {
                var entry = exportOffset + i * EXPORT_ENTRY_SIZE;
                if (entry + SERIAL_OFFSET_IN_ENTRY + 8 > uasset.Length) { break; }

                var size = BitConverter.ToInt64(uasset, entry + SERIAL_SIZE_IN_ENTRY);
                var offset = BitConverter.ToInt64(uasset, entry + SERIAL_OFFSET_IN_ENTRY);
                var within = offset - headerSize;
                if (within < 0 || size <= 0 || within + size > uexp.Length) { continue; }

                var exportName = nameAt(uasset, entry + OBJECT_NAME_IN_ENTRY, names) ?? $"export{i}";
                walk(uexp, (int)within, (int)(within + size), names, exportName, found);
            }

            return found;
        }

        /// <summary>
        /// The asset's name table.
        ///
        /// Worth having on its own because a cooked package records its own path in here, spelled
        /// as the cooker spelled it - which is the only reliable way to learn the capitalisation a
        /// mod file has to match.
        /// </summary>
        public static IReadOnlyList<string> readNamesOf(byte[] uasset)
            => tryReadSummary(uasset, out _, out var names, out _, out _) ? names : Array.Empty<string>();

        /// <summary>The one property with this export and name, or nothing.</summary>
        public static Value? find(IReadOnlyList<Value> values, string export, string name)
        {
            Value? found = null;
            foreach (var value in values)
            {
                if (!string.Equals(value.Export, export, StringComparison.Ordinal)) { continue; }
                if (!string.Equals(value.Name, name, StringComparison.Ordinal)) { continue; }
                //More than one of the same name on the same export means this cannot say which is
                //meant, and writing to the wrong one is worse than not writing at all.
                if (found != null) { return null; }
                found = value;
            }
            return found;
        }

        /// <summary>
        /// Walks one property list. Stops at the first thing it cannot read, which is the normal
        /// outcome for an export that was never a property list to begin with.
        /// </summary>
        private static void walk(byte[] uexp, int start, int limit, IReadOnlyList<string> names,
            string exportName, List<Value> found)
        {
            var at = start;
            //A guard rather than a while(true): a corrupt list should end the walk, not spin.
            for (int guard = 0; guard < 4096; guard++)
            {
                if (at + 8 > limit) { return; }

                var name = nameAt(uexp, at, names);
                if (name == null) { return; }
                if (name == "None") { return; }
                at += 8;

                if (at + 8 > limit) { return; }
                var type = nameAt(uexp, at, names);
                if (type == null) { return; }
                at += 8;

                if (at + 8 > limit) { return; }
                var size = BitConverter.ToInt32(uexp, at);
                at += 4;
                at += 4;   // array index
                if (size < 0 || size > limit - start) { return; }

                string? structName = null;
                var flag = false;
                var boolAt = -1;

                switch (type)
                {
                    case "StructProperty":
                        if (at + 8 > limit) { return; }
                        structName = nameAt(uexp, at, names);
                        at += 8;
                        at += 16;   // the struct's guid
                        break;
                    case "ByteProperty":
                    case "EnumProperty":
                    case "ArrayProperty":
                    case "SetProperty":
                        if (at + 8 > limit) { return; }
                        at += 8;
                        break;
                    case "MapProperty":
                        if (at + 16 > limit) { return; }
                        at += 16;
                        break;
                    case "BoolProperty":
                        //A bool keeps its value in the tag and declares a size of zero.
                        if (at >= limit) { return; }
                        boolAt = at;
                        flag = uexp[at] != 0;
                        at += 1;
                        break;
                }

                if (at >= limit) { return; }
                var hasGuid = uexp[at] != 0;
                at += 1;
                if (hasGuid) { at += 16; }
                if (at > limit) { return; }

                found.Add(new Value {
                    Export = exportName,
                    Name = name,
                    Type = type,
                    StructName = structName,
                    At = type == "BoolProperty" ? boolAt : at,
                    Size = size,
                    Flag = flag,
                });

                if (type != "BoolProperty") { at += size; }
            }
        }

        private static string? nameAt(byte[] source, int at, IReadOnlyList<string> names)
        {
            if (at + 8 > source.Length) { return null; }
            var index = BitConverter.ToInt32(source, at);
            var number = BitConverter.ToInt32(source, at + 4);
            if (index < 0 || index >= names.Count) { return null; }

            //A name carries an optional number, which the engine prints as a suffix one lower than
            //it is stored. Kept because two components can differ only by it.
            return number == 0 ? names[index] : names[index] + "_" + (number - 1);
        }

        private static bool tryReadSummary(byte[] uasset, out int headerSize,
            out IReadOnlyList<string> names, out int exportCount, out int exportOffset)
        {
            headerSize = 0;
            names = Array.Empty<string>();
            exportCount = 0;
            exportOffset = 0;

            try
            {
                using var stream = new MemoryStream(uasset);
                using var reader = new BinaryReader(stream, Encoding.UTF8, leaveOpen: true);

                if (reader.ReadUInt32() != PACKAGE_MAGIC) { return false; }

                var legacy = reader.ReadInt32();
                if (legacy != -4) { reader.ReadInt32(); }
                reader.ReadInt32();   // ue4 version
                reader.ReadInt32();   // licensee version

                var customVersions = reader.ReadInt32();
                for (int i = 0; i < customVersions; i++) { reader.ReadBytes(20); }

                headerSize = reader.ReadInt32();
                skipString(reader);
                reader.ReadUInt32();  // package flags

                var nameCount = reader.ReadInt32();
                var nameOffset = reader.ReadInt32();
                reader.ReadInt32();   // gatherable text count
                reader.ReadInt32();   // gatherable text offset

                exportCount = reader.ReadInt32();
                exportOffset = reader.ReadInt32();

                names = readNames(uasset, nameCount, nameOffset);
                return names.Count > 0;
            }
            catch (Exception)
            {
                return false;
            }
        }

        private static IReadOnlyList<string> readNames(byte[] uasset, int count, int offset)
        {
            var names = new List<string>(Math.Max(0, count));
            if (offset <= 0 || offset >= uasset.Length) { return names; }

            using var stream = new MemoryStream(uasset);
            stream.Seek(offset, SeekOrigin.Begin);
            using var reader = new BinaryReader(stream, Encoding.UTF8, leaveOpen: true);

            for (int i = 0; i < count && stream.Position < uasset.Length; i++)
            {
                names.Add(readString(reader));
                //Two sixteen bit hashes follow every name in a cooked package.
                if (stream.Position + 4 <= uasset.Length) { reader.ReadUInt32(); }
            }
            return names;
        }

        private static void skipString(BinaryReader reader)
        {
            var length = reader.ReadInt32();
            if (length == 0) { return; }
            reader.ReadBytes(length < 0 ? -length * 2 : length);
        }

        private static string readString(BinaryReader reader)
        {
            var length = reader.ReadInt32();
            if (length == 0) { return string.Empty; }

            if (length < 0)
            {
                var wide = reader.ReadBytes(-length * 2);
                return Encoding.Unicode.GetString(wide).TrimEnd('\0');
            }
            return Encoding.UTF8.GetString(reader.ReadBytes(length)).TrimEnd('\0');
        }
    }
}
