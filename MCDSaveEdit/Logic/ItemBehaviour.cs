using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
#nullable enable

namespace MCDSaveEdit.Logic
{
    /// <summary>
    /// The numbers an item's blueprint stores about how it behaves, found at any depth.
    ///
    /// A melee weapon's combo is `ConfiguredAttackVariants` on its Instance: an array of structs,
    /// one per swing, each holding Damage, AttackRange, the swing's duration, the cone it hits in,
    /// stun and pushback. CookedProperties reads a property list one level deep; this walks into
    /// arrays of structs and structs of properties, so each number comes back with a path like
    /// `ConfiguredAttackVariants[2].Damage` and the place its four bytes live.
    ///
    /// Only what the package STORES can be changed here: a cooked blueprint keeps just the values
    /// that differ from its parent. Changing one is four bytes over four bytes, so no size in the
    /// package moves. A struct this cannot read as a property list - a vector, a guid - is left
    /// whole rather than guessed at.
    /// </summary>
    public static class ItemBehaviour
    {
        public sealed class Number
        {
            public string Export { get; set; } = "";
            public string Path { get; set; } = "";
            /// <summary>FloatProperty or IntProperty.</summary>
            public string Type { get; set; } = "";
            /// <summary>Where the value's bytes start in the .uexp.</summary>
            public int At { get; set; }
            public double Value { get; set; }

            public override string ToString() => $"{Path} = {Value.ToString(CultureInfo.InvariantCulture)}";
        }

        /// <summary>Every stored number on the package's default objects (`Default__...`).</summary>
        public static List<Number> numbersOf(byte[] uasset, byte[] uexp)
        {
            var found = new List<Number>();
            CookedEdit.Package package;
            try { package = CookedEdit.read(uasset, uexp); }
            catch (Exception) { return found; }

            foreach (var export in package.Exports.Where(e => e.Name.StartsWith("Default__", StringComparison.Ordinal)))
            {
                var start = (int)export.At;
                var end = start + export.Size;
                if (start < 0 || end > uexp.Length) { continue; }
                var here = new List<Number>();
                if (walk(package.Names, uexp, start, end, "", export.Name, here, out _)) { found.AddRange(here); }
            }
            return found;
        }

        /// <summary>Writes one number back, in place. False when it would not fit its type.</summary>
        public static bool set(byte[] uexp, Number number, double value)
        {
            if (number.At < 0 || number.At + 4 > uexp.Length) { return false; }
            if (number.Type == "FloatProperty")
            {
                BitConverter.GetBytes((float)value).CopyTo(uexp, number.At);
                return true;
            }
            if (number.Type == "IntProperty")
            {
                if (value < int.MinValue || value > int.MaxValue) { return false; }
                BitConverter.GetBytes((int)Math.Round(value)).CopyTo(uexp, number.At);
                return true;
            }
            return false;
        }

        /// <summary>
        /// One tagged property list from <paramref name="at"/>, ending at its None. True only when
        /// the whole list read cleanly - which is how a struct that is NOT a property list gets
        /// told apart from one that is, without keeping a list of which is which.
        /// </summary>
        private static bool walk(IReadOnlyList<string> names, byte[] data, int at, int limit, string prefix,
            string export, List<Number> found, out int end)
        {
            end = at;
            for (var guard = 0; guard < 4096; guard++)
            {
                var name = nameAt(names, data, at, limit);
                if (name == null) { return false; }
                at += 8;
                if (name == "None") { end = at; return true; }

                var type = nameAt(names, data, at, limit);
                if (type == null || !type.EndsWith("Property", StringComparison.Ordinal)) { return false; }
                at += 8;
                if (at + 8 > limit) { return false; }
                var size = BitConverter.ToInt32(data, at);
                var index = BitConverter.ToInt32(data, at + 4);
                at += 8;
                if (size < 0 || index < 0) { return false; }

                string? structName = null;
                string? innerType = null;
                switch (type)
                {
                    case "StructProperty":
                        structName = nameAt(names, data, at, limit);
                        if (structName == null) { return false; }
                        at += 8 + 16;
                        break;
                    case "ArrayProperty":
                    case "SetProperty":
                        innerType = nameAt(names, data, at, limit);
                        if (innerType == null) { return false; }
                        at += 8;
                        break;
                    case "ByteProperty":
                    case "EnumProperty":
                        at += 8;
                        break;
                    case "MapProperty":
                        at += 16;
                        break;
                    case "BoolProperty":
                        at += 1;
                        break;
                }
                if (at >= limit) { return false; }
                var hasGuid = data[at] != 0;
                at += 1 + (hasGuid ? 16 : 0);

                var valueAt = at;
                var valueEnd = at + (type == "BoolProperty" ? 0 : size);
                if (valueEnd > limit) { return false; }

                var path = prefix + name + (index > 0 ? $"({index})" : "");
                if (type == "FloatProperty" && size == 4)
                {
                    found.Add(new Number { Export = export, Path = path, Type = type, At = valueAt, Value = BitConverter.ToSingle(data, valueAt) });
                }
                else if (type == "IntProperty" && size == 4)
                {
                    found.Add(new Number { Export = export, Path = path, Type = type, At = valueAt, Value = BitConverter.ToInt32(data, valueAt) });
                }
                else if (type == "StructProperty" && size > 0)
                {
                    //A struct stored as its own property list reads to its None exactly at its end.
                    var inside = new List<Number>();
                    if (walk(names, data, valueAt, valueEnd, path + ".", export, inside, out var stop) && stop == valueEnd)
                    {
                        found.AddRange(inside);
                    }
                }
                else if (type == "ArrayProperty" && innerType == "StructProperty" && size >= 4)
                {
                    readStructArray(names, data, valueAt, valueEnd, path, export, found);
                }

                at = valueEnd;
            }
            return false;
        }

        /// <summary>An array of structs: a count, one inner tag naming the struct, then each element.</summary>
        private static void readStructArray(IReadOnlyList<string> names, byte[] data, int at, int limit, string path,
            string export, List<Number> found)
        {
            var count = BitConverter.ToInt32(data, at);
            at += 4;
            if (count <= 0 || count > 10000) { return; }

            //The inner tag: name, type, size, index, struct name, guid, has-guid.
            if (nameAt(names, data, at, limit) == null) { return; }
            if (nameAt(names, data, at + 8, limit) != "StructProperty") { return; }
            at += 8 + 8 + 8 + 8 + 16;
            if (at >= limit) { return; }
            at += 1 + (data[at] != 0 ? 16 : 0);

            var elements = new List<Number>();
            for (var i = 0; i < count; i++)
            {
                if (!walk(names, data, at, limit, $"{path}[{i}].", export, elements, out var stop)) { return; }
                at = stop;
            }
            if (at == limit) { found.AddRange(elements); }
        }

        private static string? nameAt(IReadOnlyList<string> names, byte[] data, int at, int limit)
        {
            if (at + 8 > limit || at + 8 > data.Length) { return null; }
            var index = BitConverter.ToInt32(data, at);
            var number = BitConverter.ToInt32(data, at + 4);
            if (index < 0 || index >= names.Count || number < 0) { return null; }
            return number == 0 ? names[index] : names[index] + "_" + (number - 1);
        }
    }
}
