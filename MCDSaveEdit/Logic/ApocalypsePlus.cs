using MCDSaveEdit.Services;
using System;
using System.Collections.Generic;
using System.IO;
#nullable enable

namespace MCDSaveEdit.Logic
{
    /// <summary>
    /// Apocalypse +26 to +35: a switch on the Difficulty tab, made of two parts.
    ///
    /// **The levels.** The game stops at +25 because four small functions in its code return that
    /// number, and the mission screen's slider asks them. The item plugin wraps them when the game
    /// starts (ItemPlugin's extendStruggles) - through their UFunctions, which are data; the code
    /// itself is never changed, because the copy protection watches it.
    ///
    /// **How hard they are.** Item power needs nothing: the game adds 1/19 of a threat step to its
    /// global threat for every level, with no ceiling, and gear power follows it - so +26 drops
    /// stronger gear than +25 on exactly the same line. The eighteen mob and loot multipliers are
    /// the game's EndlessStruggle table: each is min + (max - min) × curve(t), with
    /// t = (level - start) / (25 - start), along EndlessStruggleLinearCurve. That curve runs from
    /// (0, 0) to (1, 1) and holds flat after, so on its own a level past 25 would play exactly like
    /// +25. The copy installed here has one more point, (2, 1.5): past +25 every multiplier keeps
    /// climbing at half its old step. Half, because at the full step stun and knockback would be
    /// gone by +35 and mobs over twice as fast; at half they end near 15% and 2x. Levels up to +25
    /// never reach past t = 1 and are exactly as before.
    ///
    /// Switched on by a file beside the custom items, so the plugin's list can be rewritten from
    /// anywhere - an item install, a pack import - without losing it.
    /// </summary>
    public static class ApocalypsePlus
    {
        public const int TOP = 35;

        public const string PAK_NAME = CustomSkins.MOD_PREFIX + "ApocalypsePlus" + CustomSkins.MOD_SUFFIX;
        private const string CURVE = "/Dungeons/Content/DataTables/Assets/EndlessStruggleLinearCurve";
        private const float PAST_END_TIME = 2f;
        private const float PAST_END_VALUE = 1.5f;

        private static string file => Path.Combine(CustomItems.folder, "apocalypse-plus.txt");

        public static bool isOn => File.Exists(file);

        /// <summary>Turns it on or off: the curve's pak and the plugin's list. Returns what the plugin was told.</summary>
        public static string set(bool on)
        {
            var paks = CustomSkins.paksFolder ?? throw new InvalidOperationException("The game's paks folder is not known.");
            if (GamePlugin.gameFolder() == null)
            {
                throw new InvalidOperationException("Apocalypse +26 to +35 need the Steam or Minecraft Launcher version of the game.");
            }
            var pak = Path.Combine(paks, PAK_NAME);
            var was = isOn;
            try
            {
                if (on)
                {
                    var (uasset, uexp) = extendedCurve();
                    var inside = CURVE.TrimStart('/');
                    PakWriter.write(pak, new List<PakWriter.Entry> {
                        new PakWriter.Entry(inside + ".uasset", uasset),
                        new PakWriter.Entry(inside + ".uexp", uexp),
                    });
                    File.WriteAllText(file, TOP.ToString(System.Globalization.CultureInfo.InvariantCulture));
                }
                else
                {
                    if (File.Exists(pak)) { File.Delete(pak); }
                    if (File.Exists(file)) { File.Delete(file); }
                }
                return GamePlugin.install(CustomItems.pluginItems(CustomItems.load()));
            }
            catch (Exception)
            {
                //Back to what the game has, so the switch says the same.
                if (was) { File.WriteAllText(file, TOP.ToString(System.Globalization.CultureInfo.InvariantCulture)); }
                else
                {
                    if (File.Exists(file)) { File.Delete(file); }
                    if (File.Exists(pak)) { File.Delete(pak); }
                }
                throw;
            }
        }

        /// <summary>
        /// The game's linear curve with a third point past its end.
        ///
        /// Cooked, the curve is one tag - FloatCurve, a RichCurve struct - holding one tag, Keys, an
        /// array of RichCurveKey: three mode bytes and six floats each, 27 bytes. The second key is
        /// made linear, so the new segment is a straight line, and the point is appended; the three
        /// sizes that enclose it and the export's own size grow by one key.
        /// </summary>
        public static (byte[] uasset, byte[] uexp) extendedCurve()
        {
            var read = CustomSkins.index?.extractPackage(CURVE) ?? throw new InvalidOperationException("The game's Apocalypse+ curve could not be read.");
            var uasset = read.UAsset.ToArray();
            var uexp = read.UExp.ToArray();
            var package = CookedEdit.read(uasset, uexp);
            string name(int at)
            {
                var index = BitConverter.ToInt32(uexp, at);
                if (index < 0 || index >= package.Names.Count) { throw new InvalidOperationException("The curve does not read as expected."); }
                return package.Names[index];
            }
            void expect(bool ok) { if (!ok) { throw new InvalidOperationException("The curve is not the one this was written for."); } }

            var start = (int)package.Exports[0].At;
            //FloatCurve: name, type, size, index, struct name, guid, has-guid.
            expect(name(start) == "FloatCurve" && name(start + 8) == "StructProperty" && name(start + 24) == "RichCurve" && uexp[start + 48] == 0);
            var curveSize = start + 16;
            var keysTag = start + 49;
            //Keys: name, type, size, index, inner type, has-guid; then the count and the inner tag.
            expect(name(keysTag) == "Keys" && name(keysTag + 8) == "ArrayProperty" && name(keysTag + 24) == "StructProperty" && uexp[keysTag + 32] == 0);
            var keysSize = keysTag + 16;
            var countAt = keysTag + 33;
            var count = BitConverter.ToInt32(uexp, countAt);
            var inner = countAt + 4;
            expect(name(inner + 24) == "RichCurveKey" && uexp[inner + 48] == 0);
            var innerSize = inner + 16;
            const int KEY = 27;
            expect(count == 2 && BitConverter.ToInt32(uexp, innerSize) == count * KEY);
            var keys = inner + 49;
            var lastTime = BitConverter.ToSingle(uexp, keys + KEY + 3);
            expect(lastTime > 0.99f && lastTime < 1.01f);

            //The second key's segment becomes linear; it had none, being last.
            uexp[keys + KEY] = 0;
            var added = new byte[KEY];
            BitConverter.GetBytes(PAST_END_TIME).CopyTo(added, 3);
            BitConverter.GetBytes(PAST_END_VALUE).CopyTo(added, 7);

            var at = keys + count * KEY;
            var grown = new byte[uexp.Length + KEY];
            Buffer.BlockCopy(uexp, 0, grown, 0, at);
            Buffer.BlockCopy(added, 0, grown, at, KEY);
            Buffer.BlockCopy(uexp, at, grown, at + KEY, uexp.Length - at);
            BitConverter.GetBytes(count + 1).CopyTo(grown, countAt);
            foreach (var size in new[] { innerSize, keysSize, curveSize })
            {
                BitConverter.GetBytes(BitConverter.ToInt32(grown, size) + KEY).CopyTo(grown, size);
            }
            if (!PackageRename.exportGrew(uasset, 0, KEY)) { throw new InvalidOperationException("The curve's export table could not be moved."); }
            return (uasset, grown);
        }
    }
}
