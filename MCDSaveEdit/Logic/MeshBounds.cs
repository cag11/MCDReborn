using PakReader.Pak;
using PakReader.Parsers.Class;
using System.Collections.Generic;
#nullable enable

namespace MCDSaveEdit.Logic
{
    /// <summary>
    /// The box a cooked mesh declares around itself.
    ///
    /// Small, and load bearing. The geometry in a cooked `StaticMesh` is untagged - raw structs
    /// with nothing to navigate by - so it is found by matching it against this box rather than by
    /// walking to it. Everything in <see cref="MeshGeometry"/> stands on these seven numbers.
    ///
    /// They matter a second time when geometry changes: the bounds are what the renderer culls
    /// against, so a mesh that moves without them moves out of its own box and the weapon
    /// disappears at certain camera angles.
    /// </summary>
    public static class MeshBounds
    {
        public static bool tryRead(PakPackage package,
            out MeshGeometry.Position origin, out MeshGeometry.Position extent, out float radius)
        {
            origin = default;
            extent = default;
            radius = 0f;

            foreach (var export in package.Exports)
            {
                if (!(export is UObject properties)) { continue; }
                if (!properties.TryGetValue("ExtendedBounds", out var boundsRaw)) { continue; }
                if (!(unwrap(boundsRaw) is UObject bounds)) { continue; }

                //The extent has to be there. The origin does not: a tagged property list leaves
                //out anything equal to its default, so a mesh centred on nothing has no Origin key
                //at all. Treating that as a failure loses meshes for being centred.
                if (!tryReadVector(bounds, "BoxExtent", out extent)) { continue; }
                if (!tryReadVector(bounds, "Origin", out origin)) { origin = new MeshGeometry.Position(0, 0, 0); }

                if (unwrap(bounds.TryGetValue("SphereRadius", out var radiusRaw) ? radiusRaw : null) is float found)
                {
                    radius = found;
                }
                return true;
            }
            return false;
        }

        /// <summary>
        /// One FVector out of a struct, by reflection.
        ///
        /// The parser hands back its own vector type rather than anything declared here, so the
        /// three floats are reached by asking the object what it holds instead of naming a type
        /// that would have to be kept in step with the parser.
        /// </summary>
        private static bool tryReadVector(UObject owner, string key, out MeshGeometry.Position vector)
        {
            vector = default;
            if (!owner.TryGetValue(key, out var raw)) { return false; }

            var value = unwrap(raw);
            if (value == null) { return false; }

            var numbers = new List<float>();
            foreach (var field in value.GetType().GetFields())
            {
                if (field.GetValue(value) is float number) { numbers.Add(number); }
            }
            if (numbers.Count < 3) { return false; }

            vector = new MeshGeometry.Position(numbers[0], numbers[1], numbers[2]);
            return true;
        }

        /// <summary>
        /// The value inside a property wrapper.
        ///
        /// Every parsed property is a BaseProperty&lt;T&gt; around what was read, so the useful
        /// thing is one reflection hop away - done this way rather than by switching over twenty
        /// property types, because the same question is being asked of all of them.
        /// </summary>
        public static object? unwrap(object? value)
        {
            if (value == null) { return null; }

            var property = value.GetType().GetProperty("Value");
            if (property == null) { return value; }

            var inner = property.GetValue(value);
            //Wrappers nest: an array of structs is an ArrayProperty of StructProperty of UObject.
            return inner == null || ReferenceEquals(inner, value) ? inner : unwrap(inner);
        }
    }
}
