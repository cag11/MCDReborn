using MCDSaveEdit.Services;
using PakReader.Pak;
using System;
using System.Collections.Generic;
#nullable enable

namespace MCDSaveEdit.Logic
{
    /// <summary>
    /// Making something glow, by turning up numbers the game already has.
    ///
    /// The arrow every bow fires and the Torment arrow that glows blue are the same material with
    /// different values in it - both instances of `M_Arrow`, differing in four numbers:
    ///
    /// | | ordinary | Torment |
    /// | --- | --- | --- |
    /// | Emissive | 0, 0, 0 | 0, 0.33, 1.0 |
    /// | FullEmissive | 0, 0, 0 | 3, 3, 3 |
    /// | EmissivePower | 0 | 125 |
    /// | Emissive_SinusTempo | - | 1 (the pulse) |
    ///
    /// That is what makes this possible at all. The glow is not a different shader, so nothing has
    /// to be compiled: the shader that reads these values is already in the build, being used by
    /// every glowing arrow in the game. Turning a *static* switch on would be the other thing -
    /// static parameters decide which shader was compiled, and a cooked game cannot compile
    /// another - and none is touched here.
    ///
    /// Finding where to write is the whole difficulty. The existing emissive edit searches the
    /// file for the four floats it is replacing and refuses unless they appear exactly once, which
    /// works when turning a colour off and cannot work when turning one on: the value being
    /// replaced is zero, and zero is everywhere. So this finds the parameter by *name* instead -
    /// the name table gives it an index, the index appears in the parameter's own entry, and the
    /// value is the tagged property that follows it. No searching for the value at all.
    /// </summary>
    public static class Glow
    {
        /// <summary>What each of the four numbers is called inside the material.</summary>
        private const string EMISSIVE = "Emissive";
        private const string FULL_EMISSIVE = "FullEmissive";
        private const string EMISSIVE_POWER = "EmissivePower";
        private const string FULL_EMISSIVE_POWER = "FullEmissivePower";

        private const string SCALARS = "ScalarParameterValues";
        private const string VECTORS = "VectorParameterValues";

        /// <summary>The value a parameter's entry carries, and where it sits in the .uexp.</summary>
        private readonly struct Slot
        {
            public Slot(int at, int floats) { At = at; Floats = floats; }
            public int At { get; }
            public int Floats { get; }
            public bool found => At > 0;
        }

        /// <summary>How brightly the game's own glowing arrows are lit, for scale.</summary>
        public const float TORMENT_POWER = 125f;

        /// <summary>
        /// The materials a mesh wears, rewritten to glow, ready to go into a mod pak.
        ///
        /// Returns nothing when the parameters are not there to write. A material that has never
        /// had an emissive value has no entry to find, and adding one would mean growing the
        /// property list - every offset after it moves, and that is a different job from changing
        /// a number in place.
        /// </summary>
        public static IEnumerable<PakWriter.Entry> ignite(string meshAssetPath,
            float red, float green, float blue, float power, out List<string> lit)
        {
            lit = new List<string>();
            var entries = new List<PakWriter.Entry>();

            var paks = CustomSkins.index;
            if (paks == null) { return entries; }

            foreach (var path in CreatureVariants.materialsOf(meshAssetPath))
            {
                PakPackage package;
                try
                {
                    var read = paks.extractPackage(path);
                    if (read == null || !read.Value.HasExport()) { continue; }
                    package = read.Value;
                }
                catch (Exception) { continue; }

                var uasset = package.UAsset.ToArray();
                var uexp = package.UExp.ToArray();

                if (!light(uasset, uexp, red, green, blue, power)) { continue; }

                var insidePak = path.TrimStart('/');
                entries.Add(new PakWriter.Entry(insidePak + ".uasset", uasset));
                entries.Add(new PakWriter.Entry(insidePak + ".uexp", uexp));
                if (package.UBulk != null)
                {
                    entries.Add(new PakWriter.Entry(insidePak + ".ubulk", package.UBulk.Value.ToArray()));
                }
                lit.Add(System.IO.Path.GetFileName(path));
            }

            return entries;
        }

        /// <summary>Whether a mesh has anything that can be made to glow.</summary>
        public static bool canGlow(string meshAssetPath)
        {
            var paks = CustomSkins.index;
            if (paks == null) { return false; }

            foreach (var path in CreatureVariants.materialsOf(meshAssetPath))
            {
                try
                {
                    var read = paks.extractPackage(path);
                    if (read == null || !read.Value.HasExport()) { continue; }

                    var uasset = read.Value.UAsset.ToArray();
                    var uexp = read.Value.UExp.ToArray();
                    if (light(uasset, uexp.Clone() as byte[] ?? uexp, 1f, 1f, 1f, 1f)) { return true; }
                }
                catch (Exception) { }
            }
            return false;
        }

        /// <summary>
        /// Writes the four numbers into one material's bytes. Says whether anything was written.
        ///
        /// The colour goes into both emissive slots and the strength into both powers, because a
        /// material that has one usually has the other and the pair is what the game's own bright
        /// arrows set.
        /// </summary>
        private static bool light(byte[] uasset, byte[] uexp, float red, float green, float blue, float power)
        {
            var names = CookedProperties.readNamesOf(uasset);
            if (names.Count == 0) { return false; }

            var properties = CookedProperties.readAll(uasset, uexp);

            var scalars = arrayRange(properties, SCALARS);
            var vectors = arrayRange(properties, VECTORS);

            var wrote = false;

            foreach (var name in new[] { EMISSIVE, FULL_EMISSIVE })
            {
                var slot = slotOf(uexp, names, name, vectors);
                if (!slot.found || slot.Floats < 3) { continue; }

                //Alpha is left as it was. It is not a fourth channel of light and materials
                //disagree about what they use it for.
                write(uexp, slot.At, red);
                write(uexp, slot.At + 4, green);
                write(uexp, slot.At + 8, blue);
                wrote = true;
            }

            foreach (var name in new[] { EMISSIVE_POWER, FULL_EMISSIVE_POWER })
            {
                var slot = slotOf(uexp, names, name, scalars);
                if (!slot.found) { continue; }

                //FullEmissivePower is a multiplier of a few rather than a power of a hundred, so
                //the two are not given the same number.
                write(uexp, slot.At, name == EMISSIVE_POWER ? power : Math.Min(power / 40f, 8f));
                wrote = true;
            }

            return wrote;
        }

        private static (int at, int limit) arrayRange(IReadOnlyList<CookedProperties.Value> properties, string name)
        {
            foreach (var value in properties)
            {
                if (!string.Equals(value.Name, name, StringComparison.Ordinal)) { continue; }
                return (value.At, value.At + value.Size);
            }
            return (0, 0);
        }

        /// <summary>
        /// Where one named parameter keeps its value.
        ///
        /// The parameter's name appears as an index into the name table inside its own entry, and
        /// the entry that follows is an ordinary tagged property called ParameterValue. So the
        /// name is found first and the value is read relative to it, which is what makes this work
        /// on a parameter whose current value is zero.
        /// </summary>
        private static Slot slotOf(byte[] uexp, IReadOnlyList<string> names, string parameter,
            (int at, int limit) range)
        {
            if (range.limit <= range.at) { return default; }

            var index = indexOf(names, parameter);
            var valueName = indexOf(names, "ParameterValue");
            if (index < 0 || valueName < 0) { return default; }

            var floatType = indexOf(names, "FloatProperty");
            var structType = indexOf(names, "StructProperty");

            //Byte by byte, because none of this is aligned: the parameter entries sit wherever
            //the tagged property list before them happened to end.
            for (int at = range.at; at + 8 <= range.limit; at++)
            {
                //The parameter's own name: an index and a number, both little endian.
                if (BitConverter.ToInt32(uexp, at) != index) { continue; }
                if (BitConverter.ToInt32(uexp, at + 4) != 0) { continue; }

                //Its value is the next ParameterValue tag along. Bounded, because a parameter's
                //entry is small and anything further away belongs to a different one.
                for (int tag = at + 8; tag + 24 <= Math.Min(range.limit, at + 512); tag++)
                {
                    if (BitConverter.ToInt32(uexp, tag) != valueName) { continue; }
                    if (BitConverter.ToInt32(uexp, tag + 4) != 0) { continue; }

                    var type = BitConverter.ToInt32(uexp, tag + 8);

                    //name(8) type(8) size(4) arrayIndex(4) then, for a struct, its name(8) and
                    //guid(16), then the one byte saying whether a property guid follows.
                    if (type == floatType) { return new Slot(tag + 8 + 8 + 4 + 4 + 1, 1); }
                    if (type == structType) { return new Slot(tag + 8 + 8 + 4 + 4 + 8 + 16 + 1, 4); }
                    break;
                }
            }

            return default;
        }

        private static int indexOf(IReadOnlyList<string> names, string wanted)
        {
            for (int i = 0; i < names.Count; i++)
            {
                if (string.Equals(names[i], wanted, StringComparison.Ordinal)) { return i; }
            }
            return -1;
        }

        private static void write(byte[] uexp, int at, float value)
        {
            if (at < 0 || at + 4 > uexp.Length) { return; }
            BitConverter.GetBytes(value).CopyTo(uexp, at);
        }

        /// <summary>What a parameter reads right now, for proving the offset before trusting it.</summary>
        public static IReadOnlyList<(string name, float[] value)> readParameters(string materialPath)
        {
            var paks = CustomSkins.index;
            if (paks == null) { return Array.Empty<(string, float[])>(); }

            var read = paks.extractPackage(materialPath);
            if (read == null || !read.Value.HasExport()) { return Array.Empty<(string, float[])>(); }

            return readParameters(read.Value.UAsset.ToArray(), read.Value.UExp.ToArray());
        }

        /// <summary>The same, from bytes, so what was written can be read back before it is trusted.</summary>
        public static IReadOnlyList<(string name, float[] value)> readParameters(byte[] uasset, byte[] uexp)
        {
            var found = new List<(string, float[])>();
            var names = CookedProperties.readNamesOf(uasset);
            var properties = CookedProperties.readAll(uasset, uexp);

            foreach (var (parameter, range) in new[] {
                (EMISSIVE, arrayRange(properties, VECTORS)),
                (FULL_EMISSIVE, arrayRange(properties, VECTORS)),
                (EMISSIVE_POWER, arrayRange(properties, SCALARS)),
                (FULL_EMISSIVE_POWER, arrayRange(properties, SCALARS)),
            })
            {
                var slot = slotOf(uexp, names, parameter, range);
                if (!slot.found) { continue; }

                var value = new float[slot.Floats];
                for (int i = 0; i < slot.Floats && slot.At + i * 4 + 4 <= uexp.Length; i++)
                {
                    value[i] = BitConverter.ToSingle(uexp, slot.At + i * 4);
                }
                found.Add((parameter, value));
            }

            return found;
        }
    }
}
