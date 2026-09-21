using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
#nullable enable

namespace MCDSaveEdit.Logic
{
    /// <summary>
    /// Small, in-place edits to a cooked package, with nothing allowed to move.
    ///
    /// The Camp's map table is built in Unreal and shipped inside this exe, and nobody installing
    /// it has an editor - so the only way the panel can change after it is cooked is if this app
    /// rewrites bytes in it. That is a thing worth doing carefully and a thing worth refusing to
    /// do loosely.
    ///
    /// The rule here is that **nothing changes length**. Not the package, not an export, not a
    /// property. Every offset recorded anywhere in the file stays true, so no header needs
    /// correcting and no other export needs moving - which is the whole category of silent
    /// corruption this could otherwise cause. An edit that would resize anything is refused
    /// rather than attempted.
    ///
    /// Two things make that possible rather than merely desirable:
    ///
    ///   * an enum property's value is an FName - a name index and a number, eight bytes - so
    ///     swapping `Collapsed` for `Visible` is eight bytes for eight bytes, PROVIDED both names
    ///     are already in the package's name table. The generator makes sure they are;
    ///   * the widget is cooked with the same slot count it will always have, so nothing is ever
    ///     added or removed, only pointed somewhere else.
    ///
    /// What this deliberately does NOT do is touch bytecode. A level name in a compiled graph is
    /// a null-terminated constant behind eighty-six absolute jump operands, and resizing one
    /// means relinking the function. The slots are named once at cook time for exactly that
    /// reason.
    /// </summary>
    public static class CookedEdit
    {
        /// <summary>One export, as far as this needs to understand one.</summary>
        public sealed class Export
        {
            public Export(string name, string className, long at, int size)
            {
                Name = name;
                ClassName = className;
                At = at;
                Size = size;
            }

            public string Name { get; }
            public string ClassName { get; }

            /// <summary>Where its data starts in the .uexp.</summary>
            public long At { get; }

            public int Size { get; }
        }

        /// <summary>
        /// The package's names, its exports, and where each export's data sits in the .uexp.
        ///
        /// Written rather than reused because the existing reader answers a different question -
        /// it walks the header to fix sizes after something grew - and this one must never let
        /// anything grow. Keeping them apart means neither has to carry the other's assumptions.
        /// </summary>
        public sealed class Package
        {
            public Package(byte[] header, byte[] data)
            {
                Header = header;
                Data = data;
                Names = new List<string>();
                Exports = new List<Export>();
            }

            public byte[] Header { get; }
            public byte[] Data { get; }
            public List<string> Names { get; }
            public List<Export> Exports { get; }

            /// <summary>Where a name sits in the table, or -1.</summary>
            public int indexOf(string name)
                => Names.FindIndex(one => string.Equals(one, name, StringComparison.Ordinal));
        }

        /// <summary>
        /// Where the counts and offsets begin, found by stepping rather than by knowing.
        ///
        /// NOTHING about a package summary before this point is fixed width. There is a variable
        /// number of custom versions - the game's own assets carry none and every asset this
        /// project cooks carries two - and a variable-length folder name after them. Hardcoding
        /// the position works against whichever asset it was measured on and is forty bytes out
        /// on the next one, which is a header written into the middle of the engine-version
        /// fields and a package that is quietly wrong.
        /// </summary>
        private static int summaryAt(byte[] header)
        {
            //Tag, legacy version, legacy UE3 version, file version, licensee version.
            var at = 4 + 4 + 4 + 4 + 4;

            //Each custom version is a 16-byte guid and an int.
            var versions = BitConverter.ToInt32(header, at);
            at += 4 + versions * 20;

            //TotalHeaderSize, then the folder name.
            at += 4;
            readString(header, ref at);

            //PackageFlags. What follows is NameCount.
            return at + 4;
        }

        /// <summary>
        /// Reads a cooked package far enough to find a property and change it.
        ///
        /// The export table's position is not assumed - it is read from the summary - and neither
        /// is its entry size, because a 4.22 FObjectExport ends with FIVE trailing int32s
        /// (FirstExportDependency and four counts) and a reader that expects four desyncs by
        /// twenty bytes per entry and reads convincing rubbish thereafter.
        /// </summary>
        public static Package read(byte[] header, byte[] data)
        {
            var made = new Package(header, data);

            var summary = summaryAt(header);

            var nameCount = BitConverter.ToInt32(header, summary);
            var nameOffset = BitConverter.ToInt32(header, summary + 4);

            var at = nameOffset;
            for (var i = 0; i < nameCount; i++)
            {
                made.Names.Add(readString(header, ref at));

                //Each name carries two case-preserving hashes after it.
                at += 4;
            }

            //NameCount, NameOffset, GatherableTextCount, GatherableTextOffset, then the exports.
            var exportCount = BitConverter.ToInt32(header, summary + 16);
            var exportOffset = BitConverter.ToInt32(header, summary + 20);

            //The whole header, which is what an export's offset is measured from.
            var headerSize = header.Length;

            const int ENTRY = 104;

            for (var i = 0; i < exportCount; i++)
            {
                var entry = exportOffset + i * ENTRY;
                if (entry + ENTRY > header.Length) { break; }

                //ClassIndex 0, SuperIndex 4, TemplateIndex 8, OuterIndex 12, ObjectName 16,
                //ObjectFlags 24, SerialSize 28, SerialOffset 36. The name is at 16 and not 12:
                //read from 12 it comes back as the OUTER's name with a number attached, which
                //looks enough like a name to be believed.
                var classIndex = BitConverter.ToInt32(header, entry);
                var name = nameAt(made, header, entry + 16);
                var size = (int)BitConverter.ToInt64(header, entry + 28);
                var where = BitConverter.ToInt64(header, entry + 36);

                //An export's offset is absolute across the header and the data together, which
                //is why the .uexp is addressed by subtracting the header's length.
                made.Exports.Add(new Export(name, classOf(made, header, classIndex),
                    where - headerSize, size));
            }

            return made;
        }

        /// <summary>
        /// Points a named enum property at a different enumerator, in place.
        ///
        /// Returns false when the property is not there, when either name is missing from the
        /// table, or when anything about the change would move a byte. Never throws for those -
        /// the caller is usually walking a hundred slots and a slot that cannot be changed is a
        /// slot left alone, not a failure.
        /// </summary>
        public static bool setEnum(Package package, string exportName, string property,
            string toValue)
        {
            var wanted = package.indexOf(toValue);
            if (wanted < 0) { return false; }

            //EVERY export of that name, not the first.
            //
            //A cooked widget blueprint serialises its tree TWICE - once under the generated
            //class and once under the widget archetype - and the two copies are otherwise
            //identical. Writing one and leaving the other means the two disagree, and the loser
            //is whichever loads second, which is not a thing to leave to chance. The first
            //attempt at this changed one copy, reported success, and showed nothing in game.
            var done = 0;

            foreach (var export in package.Exports)
            {
                if (!string.Equals(export.Name, exportName, StringComparison.Ordinal)) { continue; }

                var found = findTag(package, export, property);
                if (found == null) { continue; }

                var (valueAt, size, kind) = found.Value;

                //Only an enum property, and only one whose value is the eight bytes an FName
                //takes. A byte-backed one would be a different write and is not what the
                //generator produces.
                if (kind != "EnumProperty" || size != 8) { continue; }

                BitConverter.GetBytes(wanted).CopyTo(package.Data, valueAt);
                BitConverter.GetBytes(0).CopyTo(package.Data, valueAt + 4);
                done++;
            }

            return done > 0;
        }

        /// <summary>
        /// Rewrites a TextBlock's text without the property changing size.
        ///
        /// A widget's `Text` is an FText, and a cooked one is five fields laid end to end:
        ///
        ///     Flags        uint32
        ///     HistoryType  int8      0, meaning Base
        ///     Namespace    FString   empty here
        ///     Key          FString   a guid nothing reads
        ///     SourceString FString   the words on screen
        ///
        /// The words can therefore be made longer or shorter WITHOUT the property growing or
        /// shrinking, by taking the difference out of the key. Nothing reads the key - it is a
        /// lookup handle for a localisation entry that does not exist for these strings - so its
        /// length is free real estate, and spending it means no offset in the package moves and
        /// no header has to be corrected.
        ///
        /// The key still has to be UNIQUE, because two texts sharing one key share one display
        /// string and the one that loads second wins. It is rebuilt from the export's own name
        /// for that reason rather than by trimming the guid, which could collide.
        ///
        /// Returns false rather than throwing when the text will not fit, when the property is
        /// not there, or when the text is not plain ASCII - Unreal writes a non-ASCII FString as
        /// UTF-16 with a negative length, which is a different shape and half the room.
        /// </summary>
        public static bool setText(Package package, string exportName, string said)
        {
            if (said.Any(one => one > 126 || one < 32)) { return false; }

            var done = 0;

            foreach (var export in package.Exports)
            {
                if (!string.Equals(export.Name, exportName, StringComparison.Ordinal)) { continue; }

                var found = findTag(package, export, "Text");
                if (found == null) { continue; }

                var (at, size, kind) = found.Value;
                if (kind != "TextProperty") { continue; }

                //Flags, history type, then three strings. Anything else is a shape this does not
                //understand, and guessing at it would write into the next property.
                if (size < 17 || package.Data[at + 4] != 0) { continue; }

                //17 is everything that is not the key's or the text's characters: four bytes of
                //flags, one of history, and three four-byte lengths.
                var room = size - 17 - (said.Length + 1);

                //The key keeps a floor so that a long caption cannot squeeze it to nothing, and
                //two slots cannot end up sharing one.
                if (room < 9) { continue; }

                var key = exportName;
                key = key.Length >= room - 1
                    ? key.Substring(0, room - 1)
                    : key.PadRight(room - 1, 'x');

                using var into = new MemoryStream();
                using var writer = new BinaryWriter(into, Encoding.ASCII);

                writer.Write(BitConverter.ToUInt32(package.Data, at));      //flags, as they were
                writer.Write((byte)0);                                      //history: Base
                writer.Write(0);                                            //namespace: empty
                writeString(writer, key);
                writeString(writer, said);

                var made = into.ToArray();

                //The one check worth making loudly: if this is not exactly the size it replaces,
                //everything after it in the file is now at the wrong offset.
                if (made.Length != size) { continue; }

                made.CopyTo(package.Data, at);
                done++;
            }

            return done > 0;
        }

        private static void writeString(BinaryWriter writer, string said)
        {
            writer.Write(said.Length + 1);
            writer.Write(Encoding.ASCII.GetBytes(said));
            writer.Write((byte)0);
        }

        /// <summary>
        /// Walks an export's tagged properties looking for one by name.
        ///
        /// Returns where its VALUE begins, how long the value is, and what kind it is. The walk
        /// has to know each tag's shape because the type-specific part sits between the header
        /// and the value, and getting it wrong does not fail - it reads the next tag from the
        /// middle of this one and carries on confidently.
        /// </summary>
        private static (int at, int size, string kind)? findTag(Package package, Export export,
            string property)
        {
            var at = (int)export.At;
            var end = at + export.Size;

            while (at + 8 <= end)
            {
                var name = nameAt(package, package.Data, at);
                at += 8;

                if (name == "None") { return null; }

                var kind = nameAt(package, package.Data, at);
                at += 8;

                if (at + 8 > end) { return null; }

                var size = BitConverter.ToInt32(package.Data, at);
                at += 8;                                        //size, then array index

                //The type-specific tail, which differs per kind and is easy to forget.
                switch (kind)
                {
                    case "StructProperty": at += 8 + 16; break; //struct name, then its guid
                    case "BoolProperty": at += 1; break;        //the value lives HERE, not after
                    case "ByteProperty":
                    case "EnumProperty": at += 8; break;        //the enum's name
                    case "ArrayProperty":
                    case "SetProperty": at += 8; break;         //the inner type's name
                    case "MapProperty": at += 16; break;        //key and value type names
                }

                //Every tag then carries one byte saying whether a property guid follows.
                if (at >= end) { return null; }
                var hasGuid = package.Data[at];
                at += 1;
                if (hasGuid != 0) { at += 16; }

                if (string.Equals(name, property, StringComparison.Ordinal))
                {
                    return (at, size, kind);
                }

                //A bool's value was already consumed above; everything else follows the tag.
                at += kind == "BoolProperty" ? 0 : size;
            }

            return null;
        }

        private static string nameAt(Package package, byte[] from, int at)
        {
            var index = BitConverter.ToInt32(from, at);
            var number = BitConverter.ToInt32(from, at + 4);

            if (index < 0 || index >= package.Names.Count) { return "?"; }

            //The engine spells a trailing digit by reusing one entry and counting from one.
            return number > 0 ? package.Names[index] + "_" + (number - 1) : package.Names[index];
        }

        private static string classOf(Package package, byte[] header, int index)
        {
            //Positive is an export, negative an import; only the name is wanted and the import
            //table is where a class almost always lives.
            if (index >= 0) { return "?"; }
            return "import";
        }

        private static string readString(byte[] from, ref int at)
        {
            var length = BitConverter.ToInt32(from, at);
            at += 4;

            if (length == 0) { return string.Empty; }

            if (length < 0)
            {
                //Negative means UTF-16, counted in characters.
                var chars = -length;
                var said = Encoding.Unicode.GetString(from, at, (chars - 1) * 2);
                at += chars * 2;
                return said;
            }

            var text = Encoding.ASCII.GetString(from, at, length - 1);
            at += length;
            return text;
        }
    }
}
