using MCDSaveEdit.Services;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
#nullable enable

namespace MCDSaveEdit.Logic
{
    /// <summary>
    /// The other skins a creature is drawn with, and the colour the game paints over them.
    ///
    /// One skeletal mesh can be worn by several materials, and this is not an edge case - it is
    /// what a summoned creature *is*. Enchanted Grass does not spawn the sheep that wanders a
    /// level; it spawns one of three variants kept in the game's `Patch2` folder, each with its
    /// own material and its own texture, all sharing `SK_Sheep`. So replacing the mesh changes
    /// every sheep, and replacing the texture beside the mesh changes only the ordinary one.
    ///
    /// Two things then paint over an imported model, and both were found by reading the variant
    /// rather than by guessing:
    ///
    /// - **Its own texture.** `M_SheepFireRed` samples `Patch2/.../T_Sheep_Red`, which sits in a
    ///   different folder from the mesh and is therefore invisible to a search that looks beside
    ///   the mesh.
    /// - **A constant emissive.** The same material sets `ConstantEmissiveColor` to (250, 8.5, 0)
    ///   with the emissive switch on - a hot red added on top of whatever the texture says. Over a
    ///   light model that reads as pink.
    ///
    /// The variants are found by folder name. A creature's variant folder is the same leaf name
    /// under a different root, and its assets keep the base name with a suffix, so requiring both
    /// - the folder to match and the asset name to extend the base one - is tight enough that
    /// nothing unrelated is caught.
    /// </summary>
    public static class CreatureVariants
    {
        /// <summary>Every folder anywhere in the paks whose last segment matches this mesh's.</summary>
        private static IEnumerable<string> around(string meshAssetPath)
        {
            var paks = CustomSkins.index;
            if (paks == null) { yield break; }

            var leaf = folderLeaf(meshAssetPath);
            if (leaf.Length == 0) { yield break; }

            foreach (var entry in paks)
            {
                var path = CustomSkins.assetPath(entry);
                if (!string.Equals(folderLeaf(path), leaf, StringComparison.OrdinalIgnoreCase)) { continue; }
                yield return path;
            }
        }

        /// <summary>
        /// Every colour texture this creature is painted with, the ordinary one first.
        ///
        /// A variant's texture extends the base one's name - `T_Sheep` becomes `T_Sheep_Red` - so
        /// that is the test, rather than taking every texture in a folder that happens to share a
        /// name. Specular and normal maps are dropped by the same rule the rest of the app uses.
        /// </summary>
        public static IReadOnlyList<string> coloursOf(string meshAssetPath)
        {
            var primary = CustomSkins.textureBeside(meshAssetPath);
            if (primary == null) { return Array.Empty<string>(); }

            var baseName = System.IO.Path.GetFileName(primary);
            var found = new List<string> { primary };

            foreach (var path in around(meshAssetPath))
            {
                if (string.Equals(path, primary, StringComparison.OrdinalIgnoreCase)) { continue; }

                var name = System.IO.Path.GetFileName(path);
                if (!extends(name, baseName)) { continue; }
                if (!CustomSkins.isColourMap(name)) { continue; }
                found.Add(path);
            }

            return found;
        }

        /// <summary>Every material instance that might be painting this creature.</summary>
        public static IReadOnlyList<string> materialsOf(string meshAssetPath)
        {
            var found = new List<string>();

            foreach (var path in around(meshAssetPath))
            {
                var name = System.IO.Path.GetFileName(path);
                if (!name.StartsWith("M_", StringComparison.OrdinalIgnoreCase) &&
                    !name.StartsWith("MI_", StringComparison.OrdinalIgnoreCase)) { continue; }
                found.Add(path);
            }

            return found;
        }

        /// <summary>
        /// The materials with their constant emissive turned black, so the game stops colouring
        /// whatever is wearing them.
        ///
        /// The colour is zeroed rather than the switch that enables it being turned off. The
        /// switch is a *static* parameter: it decides which shader was compiled, and a cooked game
        /// cannot compile another one, so flipping it would ask for a shader that is not in the
        /// build. Multiplying by black is the same picture and asks for nothing.
        ///
        /// Each value is found by searching for the four floats the material says it holds, and
        /// only rewritten where those four appear exactly once. Anything else means the offset was
        /// not proven, and an unproven offset in a material is a file the game refuses to load.
        /// </summary>
        public static IEnumerable<PakWriter.Entry> calm(string meshAssetPath, out List<string> calmed)
        {
            calmed = new List<string>();
            var entries = new List<PakWriter.Entry>();

            var paks = CustomSkins.index;
            if (paks == null) { return entries; }

            foreach (var path in materialsOf(meshAssetPath))
            {
                PakReader.Pak.PakPackage package;
                try
                {
                    var read = paks.extractPackage(path);
                    if (read == null || !read.Value.HasExport()) { continue; }
                    package = read.Value;
                }
                catch (Exception) { continue; }

                var wanted = emissivesIn(package);
                if (wanted.Count == 0) { continue; }

                var uexp = package.UExp.ToArray();
                var changed = 0;
                foreach (var colour in wanted)
                {
                    if (blacken(uexp, colour)) { changed++; }
                }
                if (changed == 0) { continue; }

                var insidePak = path.TrimStart('/');
                entries.Add(new PakWriter.Entry(insidePak + ".uasset", package.UAsset.ToArray()));
                entries.Add(new PakWriter.Entry(insidePak + ".uexp", uexp));
                if (package.UBulk != null)
                {
                    entries.Add(new PakWriter.Entry(insidePak + ".ubulk", package.UBulk.Value.ToArray()));
                }
                calmed.Add(System.IO.Path.GetFileName(path));
            }

            return entries;
        }

        /// <summary>
        /// The emissive colours a material instance overrides, read from the parsed asset.
        ///
        /// Read rather than searched for. A material instance is one of the types PakReader models
        /// properly, so the values are already available with their names attached - which is what
        /// makes finding them in the bytes afterwards a confirmation rather than a guess.
        /// </summary>
        private static List<float[]> emissivesIn(PakReader.Pak.PakPackage package)
        {
            var found = new List<float[]>();

            JsonNode? root;
            try { root = JsonNode.Parse(package.JsonData); }
            catch (JsonException) { return found; }

            if (root is not JsonArray exports) { return found; }

            foreach (var export in exports)
            {
                var vectors = export?["ExportValue"]?["VectorParameterValues"] as JsonArray;
                if (vectors == null) { continue; }

                foreach (var parameter in vectors)
                {
                    var name = parameter?["ParameterInfo"]?["Name"]?.GetValue<string>();
                    if (name == null || name.IndexOf("Emissive", StringComparison.OrdinalIgnoreCase) < 0) { continue; }

                    var value = parameter?["ParameterValue"];
                    if (value == null) { continue; }

                    var colour = new[] {
                        number(value["R"]), number(value["G"]), number(value["B"]), number(value["A"]),
                    };

                    //Already black, so there is nothing to turn off and nothing to prove.
                    if (colour[0] == 0 && colour[1] == 0 && colour[2] == 0) { continue; }
                    found.Add(colour);
                }
            }

            return found;
        }

        private static float number(JsonNode? node)
        {
            try { return node == null ? 0f : node.GetValue<float>(); }
            catch (Exception) { return 0f; }
        }

        /// <summary>
        /// Zeroes one colour where it sits, having first proved there is only one place it sits.
        ///
        /// The alpha is left alone. It is not a colour channel here - the engine uses it as the
        /// strength of the thing being multiplied - and writing over it would change more than the
        /// hue.
        /// </summary>
        private static bool blacken(byte[] uexp, float[] colour)
        {
            var pattern = new byte[16];
            for (int i = 0; i < 4; i++)
            {
                Buffer.BlockCopy(BitConverter.GetBytes(colour[i]), 0, pattern, i * 4, 4);
            }

            var at = -1;
            for (int i = 0; i + pattern.Length <= uexp.Length; i++)
            {
                var same = true;
                for (int b = 0; b < pattern.Length && same; b++) { same = uexp[i + b] == pattern[b]; }
                if (!same) { continue; }

                //A second one means this cannot be told apart from something else holding the same
                //four numbers, so nothing is written rather than the wrong thing.
                if (at >= 0) { return false; }
                at = i;
            }

            if (at < 0) { return false; }

            Array.Clear(uexp, at, 12);
            return true;
        }

        /// <summary>Whether a name is the base one with a suffix, rather than merely starting alike.</summary>
        private static bool extends(string name, string baseName)
        {
            if (!name.StartsWith(baseName, StringComparison.OrdinalIgnoreCase)) { return false; }
            return name.Length == baseName.Length || name[baseName.Length] == '_';
        }

        private static string folderLeaf(string assetPath)
        {
            var parts = assetPath.TrimEnd('/').Split('/');
            return parts.Length >= 2 ? parts[parts.Length - 2] : string.Empty;
        }
    }
}
