using PakReader.Pak;
using PakReader.Parsers.Class;
using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;

using MCDSaveEdit.Logic;

namespace MeshForge
{
    /// <summary>
    /// What can be learned about a weapon's mesh without decoding the geometry itself.
    ///
    /// A cooked StaticMesh is two things joined together. The front of it is an ordinary tagged
    /// property list - the same shape every Unreal object is saved in, a name, a type, a length,
    /// a value, repeated until a "None" ends it - and PakReader already reads those into a
    /// dictionary. The back of it is `FStaticMeshRenderData`, which is not tagged at all: it is
    /// raw structs written in the order the engine expects to read them, and nothing here can
    /// parse that yet.
    ///
    /// So this reads the half that is readable. It is worth having on its own - the bounds say
    /// how big the weapon is and the materials say what it is painted with - and it is worth
    /// having first, because the properties end exactly where the geometry begins. Finding that
    /// boundary reliably is the thing the next step stands on.
    /// </summary>
    public static class StaticMeshReader
    {
        public static void describe(PakPackage package)
        {
            var types = package.ExportTypes;
            var exports = package.Exports;

            Console.WriteLine($"  {exports.Length} exports:");
            for (int i = 0; i < exports.Length; i++)
            {
                var type = i < types.Length ? types[i].String : "?";
                Console.WriteLine($"    [{i}] {type}  ({exports[i].GetType().Name})");
            }
            Console.WriteLine();

            for (int i = 0; i < exports.Length; i++)
            {
                var type = i < types.Length ? types[i].String : "?";
                if (!(exports[i] is UObject properties)) { continue; }

                Console.WriteLine($"  [{i}] {type} has {properties.Count} properties:");
                foreach (var key in properties.Keys)
                {
                    Console.WriteLine($"    {key} = {describeValue(properties[key])}");
                }
                Console.WriteLine();
            }
        }

        /// <summary>
        /// The bounds of the mesh, which is the one number here worth checking against the world.
        ///
        /// A weapon's extent should read like a weapon: long in one direction and thin in the
        /// other two. If it does, the property list is being read correctly, and that is the
        /// point of looking.
        /// </summary>
        public static void reportBounds(PakPackage package)
        {
            foreach (var export in package.Exports)
            {
                if (!(export is UObject properties)) { continue; }
                if (!properties.TryGetValue("ExtendedBounds", out var boundsRaw)) { continue; }
                var bounds = boundsRaw;

                Console.WriteLine("  ExtendedBounds:");
                foreach (var line in flatten(bounds, "    ")) { Console.WriteLine(line); }
                return;
            }
            Console.WriteLine("  no ExtendedBounds among the properties");
        }

        /// <summary>
        /// The bounds as numbers rather than as printed text, for the geometry search to aim at.
        ///
        /// The box is what makes finding the vertices possible at all: it came out of the same
        /// export and was written to contain exactly the points being looked for, so it is the one
        /// piece of ground truth available before anything untagged has been decoded.
        /// </summary>
        public static bool tryReadBounds(PakPackage package, out MeshGeometry.Position origin, out MeshGeometry.Position extent)
            => MeshBounds.tryRead(package, out origin, out extent, out _);

        public static bool tryReadBounds(PakPackage package,
            out MeshGeometry.Position origin, out MeshGeometry.Position extent, out float radius)
            => MeshBounds.tryRead(package, out origin, out extent, out radius);

        /// <summary>
        /// The value inside a property wrapper.
        ///
        /// Every parsed property is a BaseProperty&lt;T&gt; holding what was read, so the useful
        /// thing is one reflection hop away. Done by reflection rather than by switching over
        /// twenty property types, because the only question being asked of them is the same one.
        /// </summary>
        private static object? unwrap(object? value)
        {
            if (value == null) { return null; }

            var property = value.GetType().GetProperty("Value");
            if (property == null) { return value; }

            var inner = property.GetValue(value);
            //Wrappers nest: an array of structs is an ArrayProperty of StructProperty of UObject.
            return inner == null || ReferenceEquals(inner, value) ? inner : unwrap(inner);
        }

        private static string describeValue(object? raw)
        {
            var value = unwrap(raw);
            switch (value)
            {
                case null: return "null";
                case string text: return "\"" + text + "\"";
                case UObject nested: return $"{{{string.Join(", ", nested.Keys.Take(6))}{(nested.Count > 6 ? ", ..." : "")}}}";
                case IDictionary map: return $"map of {map.Count}";
                case IEnumerable list when !(value is string):
                    var items = list.Cast<object>().ToList();
                    return $"[{items.Count}] {string.Join(", ", items.Take(3).Select(shortValue))}{(items.Count > 3 ? ", ..." : "")}";
                default: return shortValue(value);
            }
        }

        private static string shortValue(object? raw)
        {
            var value = unwrap(raw);
            if (value == null) { return "null"; }
            if (value is UObject nested) { return "{" + string.Join(", ", nested.Keys.Take(4)) + "}"; }

            //A vector prints as its own type name otherwise, which says nothing. These are the
            //numbers worth seeing: a weapon's extent should be long in one direction and thin in
            //the other two, and that is the check on whether any of this is being read correctly.
            var vector = value.GetType();
            if (vector.Name == "FVector" || vector.Name == "FVector2D" || vector.Name == "FVector4")
            {
                var parts = new List<string>();
                foreach (var field in vector.GetFields())
                {
                    if (field.GetValue(value) is float number) { parts.Add(number.ToString("0.##")); }
                }
                if (parts.Count > 0) { return "(" + string.Join(", ", parts) + ")"; }
            }

            var text = value.ToString() ?? "";
            return text.Length > 60 ? text.Substring(0, 60) + "..." : text;
        }

        /// <summary>A nested struct printed one line per leaf, so numbers can be read off it.</summary>
        private static IEnumerable<string> flatten(object? raw, string indent)
        {
            var value = unwrap(raw);
            if (value is UObject nested)
            {
                foreach (var key in nested.Keys)
                {
                    var inner = nested[key];
                    if (inner is UObject)
                    {
                        yield return $"{indent}{key}:";
                        foreach (var line in flatten(inner, indent + "  ")) { yield return line; }
                    }
                    else
                    {
                        yield return $"{indent}{key} = {shortValue(inner)}";
                    }
                }
                yield break;
            }
            yield return $"{indent}{shortValue(value)}";
        }
    }
}
