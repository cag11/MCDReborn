using PakReader;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
#nullable enable

namespace MCDSaveEdit.Logic
{
    /// <summary>
    /// The game's cooked asset registry, with copies of existing entries added.
    ///
    /// A new item's files in a pak are not enough. The game finds an item's blueprints through
    /// its ItemAssetFinder, which asks the asset registry - `Dungeons/AssetRegistry.bin`, cooked
    /// once and shipped in pakchunk0 - for what lives under each content root. Nothing in a mod
    /// pak is in it, so the finder never learns the copy exists, the item's class comes back
    /// null, and the game dereferences it the moment the item is clicked.
    ///
    /// A pak can replace the file itself - it sits outside Content, but a `_P` pak overrides any
    /// path. So the registry is rebuilt from the game's own, with the source folder's entries
    /// cloned under the new names, and shipped beside the copy.
    ///
    /// The cooked file is simple, and this leans on it: a version header, the offset of the name
    /// table, every asset, then zero dependency nodes and zero package entries, then the names.
    /// New entries go after the last asset and new names after the last name; nothing that was
    /// there moves except the name table, whose offset is rewritten. A registry with anything in
    /// its dependency or package sections is refused rather than guessed at.
    /// </summary>
    public static class RegistryPatch
    {
        public const string PAK_PATH = "Dungeons/AssetRegistry.bin";

        private sealed class Asset
        {
            public int Start;
            public int End;
            public (int index, int number) ObjectPath, PackagePath, AssetClass, PackageName, AssetName;
            public List<((int index, int number) key, string value)> Tags = new();
            public byte[] Tail = Array.Empty<byte>();      //chunk ids and package flags, verbatim
        }

        /// <summary>
        /// The registry with every asset whose package path is <paramref name="fromFolder"/>
        /// (`/Game/Actors/Equipment/RangedWeapons/HeavyCrossbow`) copied through
        /// <paramref name="renamedOf"/>. Null when the file is not the shape this understands.
        /// </summary>
        public static byte[]? withClones(byte[] registry, string fromFolder, Func<string, string?> renamedOf,
            out int added)
            => withClones(registry, new[] { (fromFolder, renamedOf) }, out added);

        /// <summary>Several folders at once - one registry has to carry every item a pak adds.</summary>
        public static byte[]? withClones(byte[] registry,
            IReadOnlyList<(string fromFolder, Func<string, string?> renamedOf)> copies, out int added)
        {
            added = 0;
            var parsed = parse(registry);
            if (parsed == null) { return null; }
            var (names, assets, nameOffset, nameCount, assetCount, assetsEnd) = parsed.Value;

            string spell((int index, int number) name)
                => name.number == 0 ? names[name.index] : names[name.index] + "_" + (name.number - 1);

            var lookup = new Dictionary<string, int>(StringComparer.Ordinal);
            for (var i = 0; i < names.Count; i++) { lookup.TryAdd(names[i], i); }
            var addedNames = new List<string>();

            (int, int) nameFor(string spelled, int number)
            {
                //Keep the number the original had: "SM_X_2" is stored as "SM_X" and 3.
                var baseName = spelled;
                if (number > 0)
                {
                    var suffix = "_" + (number - 1);
                    if (!spelled.EndsWith(suffix, StringComparison.Ordinal)) { number = 0; }
                    else { baseName = spelled[..^suffix.Length]; }
                }
                if (!lookup.TryGetValue(baseName, out var index))
                {
                    index = names.Count + addedNames.Count;
                    addedNames.Add(baseName);
                    lookup[baseName] = index;
                }
                return (index, number);
            }

            var body = new MemoryStream();
            var writer = new BinaryWriter(body);
            var existing = new HashSet<string>(assets.Select(a => spell(a.ObjectPath)), StringComparer.OrdinalIgnoreCase);

            foreach (var (fromFolder, renamedOf) in copies)
            foreach (var one in assets.Where(a => string.Equals(spell(a.PackagePath), fromFolder.TrimEnd('/'), StringComparison.OrdinalIgnoreCase)).ToList())
            {
                (int, int) rename((int index, int number) name)
                {
                    var was = spell(name);
                    var now = renamedOf(was) ?? was;
                    return now == was ? name : nameFor(now, name.number);
                }

                var wasPath = spell(one.ObjectPath);
                var nowPath = renamedOf(wasPath);
                if (nowPath == null || nowPath == wasPath) { continue; }  //nothing about it was renamed
                if (existing.Contains(nowPath)) { continue; }            //already there: patched twice

                write(writer, nameFor(nowPath, one.ObjectPath.number));
                write(writer, rename(one.PackagePath));
                write(writer, one.AssetClass);
                write(writer, rename(one.PackageName));
                write(writer, rename(one.AssetName));
                writer.Write(one.Tags.Count);
                foreach (var (key, value) in one.Tags)
                {
                    write(writer, key);
                    writeString(writer, renameValue(value, renamedOf));
                }
                writer.Write(one.Tail);
                existing.Add(nowPath);
                added++;
            }
            writer.Flush();
            if (added == 0) { return registry; }

            var clones = body.ToArray();
            var output = new MemoryStream(registry.Length + clones.Length + addedNames.Count * 64);
            var o = new BinaryWriter(output);
            o.Write(registry, 0, 20);
            o.Write((long)(nameOffset + clones.Length));
            o.Write(assetCount + added);
            o.Write(registry, 32, assetsEnd - 32);
            o.Write(clones);
            o.Write(registry, assetsEnd, nameOffset - assetsEnd);
            o.Write(nameCount + addedNames.Count);
            o.Write(registry, nameOffset + 4, registry.Length - nameOffset - 4);
            foreach (var name in addedNames)
            {
                writeString(o, name);
                o.Write(NewContent.nonCaseHash(name));
                o.Write(NewContent.caseHash(name));
            }
            o.Flush();
            return output.ToArray();
        }

        private static (List<string> names, List<Asset> assets, int nameOffset, int nameCount, int assetCount, int assetsEnd)?
            parse(byte[] registry)
        {
            if (registry.Length < 40) { return null; }
            if (BitConverter.ToInt32(registry, 16) != 6) { return null; }

            var nameOffset = (int)BitConverter.ToInt64(registry, 20);
            var names = new List<string>();
            var at = nameOffset;
            var nameCount = BitConverter.ToInt32(registry, at); at += 4;
            for (var i = 0; i < nameCount; i++)
            {
                names.Add(readString(registry, ref at));
                at += 4;                                        //the two name hashes
            }
            if (at != registry.Length) { return null; }

            at = 28;
            var assetCount = BitConverter.ToInt32(registry, at); at += 4;
            var assets = new List<Asset>(assetCount);
            for (var i = 0; i < assetCount; i++)
            {
                var one = new Asset { Start = at };
                one.ObjectPath = readName(registry, ref at);
                one.PackagePath = readName(registry, ref at);
                one.AssetClass = readName(registry, ref at);
                one.PackageName = readName(registry, ref at);
                one.AssetName = readName(registry, ref at);
                var tags = BitConverter.ToInt32(registry, at); at += 4;
                for (var t = 0; t < tags; t++)
                {
                    var key = readName(registry, ref at);
                    one.Tags.Add((key, readString(registry, ref at)));
                }
                var tailAt = at;
                var chunks = BitConverter.ToInt32(registry, at); at += 4 + chunks * 4;
                at += 4;                                        //package flags
                one.Tail = registry.AsSpan(tailAt, at - tailAt).ToArray();
                one.End = at;
                assets.Add(one);
            }
            var assetsEnd = at;

            //Nothing between the assets and the names but two zero counts, or this is not the
            //cooked shape and inserting would corrupt whatever is there.
            if (nameOffset != assetsEnd + 8 || BitConverter.ToInt32(registry, assetsEnd) != 0
                || BitConverter.ToInt32(registry, assetsEnd + 4) != 0) { return null; }

            return (names, assets, nameOffset, nameCount, assetCount, assetsEnd);
        }

        /// <summary>One item the game ships, as its registry entry describes it.</summary>
        public sealed class GameItem
        {
            public string Id { get; set; } = "";
            /// <summary>`/Game/Actors/Equipment/MeleeWeapons/Katana_Unique1`.</summary>
            public string Folder { get; set; } = "";
            /// <summary>`BP_Katana_Unique1Instance`.</summary>
            public string Instance { get; set; } = "";
            /// <summary>`MeleeWeaponGearItemInstance`, `RangedWeaponGearItemInstance`, ...</summary>
            public string NativeParent { get; set; } = "";
        }

        /// <summary>
        /// Every item Instance blueprint in the registry: its id (the ItemIdName tag), folder and
        /// native class - which is what says whether it is a melee weapon, a bow or an armour.
        /// </summary>
        public static List<GameItem> items(byte[] registry)
        {
            var found = new List<GameItem>();
            var parsed = parse(registry);
            if (parsed == null) { return found; }
            var (names, assets, _, _, _, _) = parsed.Value;
            string spell((int index, int number) name)
                => name.number == 0 ? names[name.index] : names[name.index] + "_" + (name.number - 1);

            foreach (var one in assets)
            {
                var asset = spell(one.AssetName);
                if (!asset.EndsWith("Instance", StringComparison.Ordinal)) { continue; }
                string? id = null, parent = null;
                foreach (var (key, value) in one.Tags)
                {
                    var k = spell(key);
                    if (k == "ItemIdName") { id = value; }
                    else if (k == "NativeParentClass")
                    {
                        var m = Regex.Match(value, @"\.([A-Za-z0-9_]+)'?$");
                        parent = m.Success ? m.Groups[1].Value : value;
                    }
                }
                if (string.IsNullOrEmpty(id)) { continue; }
                found.Add(new GameItem { Id = id!, Folder = spell(one.PackagePath), Instance = asset, NativeParent = parent ?? "" });
            }
            return found;
        }

        /// <summary>
        /// A tag's value renamed. Class references are spelled `Kind'/Game/Path.Name_C'`, so the
        /// quoted part is what gets looked up; anything else is looked up whole.
        /// </summary>
        private static string renameValue(string value, Func<string, string?> renamedOf)
        {
            var quoted = Regex.Match(value, "^([A-Za-z0-9_]+)'(.*)'$");
            if (quoted.Success)
            {
                var inner = renamedOf(quoted.Groups[2].Value);
                return inner == null ? value : quoted.Groups[1].Value + "'" + inner + "'";
            }
            return renamedOf(value) ?? value;
        }

        /// <summary>The game's own registry, read from its paks - never from a mod.</summary>
        public static byte[]? readGameRegistry(string paksFolder)
        {
            foreach (var pakPath in Directory.GetFiles(paksFolder, "*.pak", SearchOption.TopDirectoryOnly)
                .Where(p => !Path.GetFileName(p).StartsWith("MCDReborn", StringComparison.OrdinalIgnoreCase)))
            {
                var reader = new PakReader.Pak.PakFileReader(pakPath);
                var opened = false;
                foreach (var key in Data.Secrets.PAKS_AES_KEYS)
                {
                    var k = key.key.StartsWith("0x") ? key.key[2..] : key.key;
                    if (reader.TryReadIndex(k.ToBytesKey())) { opened = true; break; }
                }
                if (!opened) { continue; }
                var name = reader.Select(kv => kv.Key)
                    .FirstOrDefault(n => n.EndsWith("/Dungeons/AssetRegistry", StringComparison.OrdinalIgnoreCase));
                if (name != null) { return reader.GetFile(name).ToArray(); }
            }
            return null;
        }

        private static (int, int) readName(byte[] from, ref int at)
        {
            var index = BitConverter.ToInt32(from, at);
            var number = BitConverter.ToInt32(from, at + 4);
            at += 8;
            return (index, number);
        }

        private static void write(BinaryWriter writer, (int index, int number) name)
        {
            writer.Write(name.index);
            writer.Write(name.number);
        }

        private static string readString(byte[] from, ref int at)
        {
            var length = BitConverter.ToInt32(from, at);
            at += 4;
            if (length == 0) { return string.Empty; }
            if (length > 0)
            {
                var text = Encoding.ASCII.GetString(from, at, length - 1);
                at += length;
                return text;
            }
            var wide = Encoding.Unicode.GetString(from, at, (-length - 1) * 2);
            at += -length * 2;
            return wide;
        }

        private static void writeString(BinaryWriter writer, string text)
        {
            if (text.Length == 0) { writer.Write(0); return; }
            if (text.All(c => c < 128))
            {
                writer.Write(text.Length + 1);
                writer.Write(Encoding.ASCII.GetBytes(text));
                writer.Write((byte)0);
                return;
            }
            writer.Write(-(text.Length + 1));
            writer.Write(Encoding.Unicode.GetBytes(text));
            writer.Write((short)0);
        }
    }
}
