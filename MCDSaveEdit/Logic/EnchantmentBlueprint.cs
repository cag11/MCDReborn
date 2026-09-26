using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
#nullable enable

namespace MCDSaveEdit.Logic
{
    /// <summary>
    /// A new enchantment's own numbers, written into its copied blueprint.
    ///
    /// An enchantment's numbers are UPROPERTYs of its C++ class - Fire Aspect's damagePerSecond,
    /// Chains' ChainRange - and the game's BP_FireAspect sets none of them: its default object
    /// holds one property, the gameplay effect, and then "None". Every enchantment the game makes
    /// from that class starts as a copy of that default object. So a tag added to the copy's
    /// default object, before its "None", overrides the C++ value for that copy alone.
    ///
    /// Unlike CookedEdit, this changes lengths: the names a tag spells are added to the header's
    /// name table (PackageRename.withNames), the tags are inserted into the .uexp, and the
    /// export's size and every offset after it move (PackageRename.exportGrew).
    /// </summary>
    public static class EnchantmentBlueprint
    {
        /// <summary>
        /// The package with <paramref name="values"/> set on the default object named
        /// <paramref name="export"/>: a tag already there is overwritten, the rest added.
        /// </summary>
        public static (byte[] uasset, byte[] uexp) withNumbers(byte[] uasset, byte[] uexp, string export,
            IReadOnlyList<(EnchantmentNumbers.Number number, double value)> values)
        {
            if (values.Count == 0) { return (uasset, uexp); }

            var needed = new List<string> { "None" };
            foreach (var (number, _) in values)
            {
                needed.Add(split(number.Property).text);
                needed.Add(typeOf(number.Kind));
            }
            var header = PackageRename.withNames(uasset, needed)
                ?? throw new InvalidOperationException("The blueprint's header is not one this can add to.");

            var package = CookedEdit.read(header, uexp);
            var index = package.Exports.FindIndex(e => string.Equals(e.Name, export, StringComparison.Ordinal));
            if (index < 0) { throw new InvalidOperationException($"The blueprint has no {export}."); }
            var target = package.Exports[index];
            int nameIndex(string text)
            {
                var at = package.indexOf(text);
                if (at < 0) { throw new InvalidOperationException($"The name {text} did not go in."); }
                return at;
            }

            //Walk the tags to the "None" that ends them, overwriting any that are already there.
            var data = (byte[])uexp.Clone();
            var left = values.ToList();
            var pos = (int)target.At;
            var end = pos + target.Size;
            string name(int at)
            {
                var i = BitConverter.ToInt32(data, at);
                if (i < 0 || i >= package.Names.Count) { throw new InvalidOperationException("The default object's tags do not read."); }
                return package.Names[i];
            }
            while (true)
            {
                if (pos + 8 > end) { throw new InvalidOperationException("The default object has no end to its tags."); }
                var tag = name(pos);
                if (tag == "None") { break; }
                var type = name(pos + 8);
                var size = BitConverter.ToInt32(data, pos + 16);
                var p = pos + 24;
                switch (type)
                {
                    case "StructProperty": p += 8 + 16; break;
                    case "BoolProperty": p += 1; break;
                    case "ByteProperty":
                    case "EnumProperty":
                    case "ArrayProperty":
                    case "SetProperty": p += 8; break;
                    case "MapProperty": p += 16; break;
                }
                var hasGuid = data[p] != 0;
                p += 1 + (hasGuid ? 16 : 0);

                var number = BitConverter.ToInt32(data, pos + 4);
                var full = number > 0 ? $"{tag}_{number - 1}" : tag;
                var mine = left.FindIndex(v => v.number.Property == full && typeOf(v.number.Kind) == type);
                if (mine >= 0)
                {
                    var (n, value) = left[mine];
                    switch (n.Kind)
                    {
                        case EnchantmentNumbers.Kind.Float: BitConverter.GetBytes((float)value).CopyTo(data, p); break;
                        case EnchantmentNumbers.Kind.Int: BitConverter.GetBytes((int)Math.Round(value)).CopyTo(data, p); break;
                        case EnchantmentNumbers.Kind.Bool: data[pos + 24] = (byte)(value != 0 ? 1 : 0); break;
                    }
                    left.RemoveAt(mine);
                }
                pos = p + size;
            }
            if (left.Count == 0) { return (header, data); }

            //The rest, as tags of their own, before the "None".
            var tags = new MemoryStream();
            var writer = new BinaryWriter(tags);
            foreach (var (n, value) in left)
            {
                var (text, number) = split(n.Property);
                writer.Write(nameIndex(text));
                writer.Write(number);
                writer.Write(nameIndex(typeOf(n.Kind)));
                writer.Write(0);
                writer.Write(n.Kind == EnchantmentNumbers.Kind.Bool ? 0 : 4);
                writer.Write(0);                                         //array index
                if (n.Kind == EnchantmentNumbers.Kind.Bool)
                {
                    writer.Write((byte)(value != 0 ? 1 : 0));
                    writer.Write((byte)0);                               //no property guid
                    continue;
                }
                writer.Write((byte)0);                                   //no property guid
                if (n.Kind == EnchantmentNumbers.Kind.Float) { writer.Write((float)value); }
                else { writer.Write((int)Math.Round(value)); }
            }
            writer.Flush();
            var added = tags.ToArray();

            var grown = new byte[data.Length + added.Length];
            Buffer.BlockCopy(data, 0, grown, 0, pos);
            Buffer.BlockCopy(added, 0, grown, pos, added.Length);
            Buffer.BlockCopy(data, pos, grown, pos + added.Length, data.Length - pos);
            if (!PackageRename.exportGrew(header, index, added.Length))
            {
                throw new InvalidOperationException("The blueprint's export table could not be moved.");
            }
            return (header, grown);
        }

        private static string typeOf(EnchantmentNumbers.Kind kind) => kind switch
        {
            EnchantmentNumbers.Kind.Int => "IntProperty",
            EnchantmentNumbers.Kind.Bool => "BoolProperty",
            _ => "FloatProperty",
        };

        /// <summary>
        /// An FName as a package stores it: "Damage_2" is the entry "Damage" with number 3. A
        /// trailing number with a leading zero stays part of the text, as the engine does.
        /// </summary>
        private static (string text, int number) split(string name)
        {
            var m = Regex.Match(name, @"^(.+)_(0|[1-9]\d{0,8})$");
            return m.Success ? (m.Groups[1].Value, int.Parse(m.Groups[2].Value) + 1) : (name, 0);
        }
    }
}
