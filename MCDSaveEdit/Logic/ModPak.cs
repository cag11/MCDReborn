using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
#nullable enable

namespace MCDSaveEdit.Logic
{
    /// <summary>
    /// Somebody else's mission mod, taken apart and put back together WITHOUT losing anything.
    ///
    /// The maps tab already knows how to read a mod's levels, object groups and block tables,
    /// because those are the parts it has a shape for. A real mod is far more than that:
    /// Blossoming Isles ships a thousand resource-pack images, its own fonts, its own UI
    /// widgets, four sub-level umaps, a blueprint or two and - the interesting one - its own
    /// copy of the game's English string table. Extracting only the three known kinds and
    /// packing those back gives a pak that is a fraction of the original and does not work.
    ///
    /// So this reads the pak itself rather than going through PakReader's index. The index
    /// merge is built for READING the game: it hangs .uexp and .ubulk off the .uasset, strips
    /// the last extension off every name and drops anything with no extension at all. That is
    /// exactly right for asking "what is in creeperwoods" and exactly wrong here, where the
    /// whole point is to get every entry back at the name it had.
    ///
    /// The folder it writes is the folder Import map already expects, with everything else
    /// beside it and a manifest saying where each piece came from:
    ///
    ///     &lt;folder&gt;/level.json                           one chosen level, as Import map wants it
    ///     &lt;folder&gt;/objectgroups/&lt;Name&gt;/objectgroup.json  the tiles, at the paths install uses
    ///     &lt;folder&gt;/resourcepacks/&lt;Name&gt;/...             the WHOLE pack, images and all
    ///     &lt;folder&gt;/extra/&lt;original path&gt;                everything else, byte for byte
    ///     &lt;folder&gt;/modpak.json                          which local file was which pak entry
    ///
    /// Every entry lands in exactly one local file, so nothing is duplicated and nothing has to
    /// be guessed at on the way back: <see cref="pack"/> walks the manifest, reads each local
    /// file as it stands now, and writes it at the path it came from, in the original order.
    /// </summary>
    public static class ModPak
    {
        /// <summary>The note unpak leaves behind, so pack knows what each file was.</summary>
        public const string MANIFEST = "modpak.json";

        /// <summary>Where everything the maps tab has no shape for is kept.</summary>
        public const string EXTRA = "extra";

        private const uint PAK_MAGIC = 0x5A6F12E1;

        private const string LEVELS = "data/lovika/levels/";
        private const string GROUPS = "data/lovika/objectgroups/";
        private const string PACKS = "data/resourcepacks/";

        /// <summary>One file inside a pak: the name it is stored under and its bytes.</summary>
        public sealed class Item
        {
            public Item(string path, byte[] data)
            {
                Path = path;
                Data = data;
            }

            /// <summary>The full name as the pak's index spells it, extension and all.</summary>
            public string Path { get; }
            public byte[] Data { get; }
        }

        /// <summary>One line of the manifest: a pak entry and the local file holding it.</summary>
        public sealed class Slot
        {
            public string pak { get; set; } = "";
            public string file { get; set; } = "";
            public long size { get; set; }
            public string sha1 { get; set; } = "";

            /// <summary>Set on the one level that was written out as level.json.</summary>
            public bool primary { get; set; }
        }

        public sealed class Manifest
        {
            public string format { get; set; } = "mcdreborn-modpak/1";
            public string source { get; set; } = "";
            public string mount { get; set; } = PakWriter.MOUNT_POINT;
            public int version { get; set; }
            public string? level { get; set; }
            public List<Slot> entries { get; set; } = new List<Slot>();
        }

        // ------------------------------------------------------------------ reading a pak

        /// <summary>
        /// Every file in a pak, in index order, under its real name.
        ///
        /// Deliberately a small reader rather than PakReader's. Only what a mod pak actually
        /// is has to be understood - version 3 to 9, plaintext index, entries either stored or
        /// zlib/gzip'd - and in return the names come back exactly as written and the bytes are
        /// never merged, renamed or dropped.
        /// </summary>
        public static IReadOnlyList<Item> read(string pakPath)
        {
            using var stream = File.OpenRead(pakPath);
            using var reader = new BinaryReader(stream, Encoding.ASCII, leaveOpen: true);

            var (version, indexOffset, indexSize, encryptedIndex, methods) = footer(stream, reader);

            if (encryptedIndex)
            {
                throw new InvalidOperationException(
                    "That pak's index is encrypted. Mod paks are not, and an encrypted one "
                    + "cannot be taken apart without the game's key.");
            }

            if (version >= 10)
            {
                throw new InvalidOperationException(
                    $"That pak is version {version}, which stores its index as a path hash. "
                    + "Mod paks are version 3 to 9 and this reader only understands those.");
            }

            stream.Position = indexOffset;
            var index = new BinaryReader(new MemoryStream(reader.ReadBytes((int)indexSize)),
                Encoding.ASCII);

            readString(index);                          //mount point, kept by the caller's manifest
            var count = index.ReadInt32();

            var items = new List<Item>(Math.Max(0, count));

            for (var i = 0; i < count; i++)
            {
                var name = readString(index).Replace('\\', '/').TrimStart('/');

                var start = index.BaseStream.Position;

                var offset = index.ReadInt64();
                var size = index.ReadInt64();
                var uncompressed = index.ReadInt64();
                var method = version < 8 ? index.ReadInt32() : (int)index.ReadUInt32();

                if (version <= 1) { index.ReadInt64(); }    //timestamp, gone after version 1

                index.ReadBytes(20);                        //hash, recomputed on the way out

                var blocks = 0;
                if (version >= 3)
                {
                    if (method != 0)
                    {
                        blocks = index.ReadInt32();
                        index.ReadBytes(blocks * 16);
                    }

                    var flags = index.ReadByte();
                    index.ReadUInt32();                     //compression block size

                    if ((flags & 0x01) != 0)
                    {
                        throw new InvalidOperationException(
                            $"\"{name}\" is encrypted, which this does not handle.");
                    }

                    if ((flags & 0x02) != 0) { continue; }  //a delete record: nothing to carry
                }

                var header = (int)(index.BaseStream.Position - start);

                stream.Position = offset + header;
                var raw = reader.ReadBytes((int)size);

                items.Add(new Item(name, method == 0
                    ? raw
                    : inflate(raw, (int)uncompressed, method, version, methods, name)));
            }

            return items;
        }

        /// <summary>
        /// The footer, found rather than assumed.
        ///
        /// Its size moves with the version - version 4 added an encrypted-index flag, 7 a key
        /// guid, 8 a list of compression method names - so the magic is looked for in the last
        /// few hundred bytes and everything is read relative to where it turns up.
        /// </summary>
        private static (int version, long indexOffset, long indexSize, bool encrypted, string[] methods)
            footer(Stream stream, BinaryReader reader)
        {
            var window = (int)Math.Min(stream.Length, 1024);
            stream.Position = stream.Length - window;
            var tail = reader.ReadBytes(window);

            for (var at = tail.Length - 4; at >= 0; at--)
            {
                if (BitConverter.ToUInt32(tail, at) != PAK_MAGIC) { continue; }
                if (at + 44 > tail.Length) { continue; }

                var version = BitConverter.ToInt32(tail, at + 4);
                if (version < 1 || version > 11) { continue; }

                var indexOffset = BitConverter.ToInt64(tail, at + 8);
                var indexSize = BitConverter.ToInt64(tail, at + 16);

                if (indexOffset < 0 || indexSize < 0
                    || indexOffset + indexSize > stream.Length) { continue; }

                //The flag sits immediately before the magic from version 4 on; before that the
                //byte there belongs to the file data and means nothing.
                var encrypted = version >= 4 && at >= 1 && tail[at - 1] != 0;

                var methods = new List<string>();
                if (version >= 8)
                {
                    var names = at + 44;
                    while (names + 32 <= tail.Length - 0 && methods.Count < 5)
                    {
                        var name = Encoding.ASCII.GetString(tail, names, 32).TrimEnd('\0');
                        if (name.Length == 0) { break; }
                        methods.Add(name);
                        names += 32;
                    }
                }

                return (version, indexOffset, indexSize, encrypted, methods.ToArray());
            }

            throw new InvalidOperationException("That file does not end like a pak.");
        }

        private static byte[] inflate(byte[] raw, int size, int method, int version,
            string[] methods, string name)
        {
            //Before version 8 the number WAS the method; after it, it is a place in the list the
            //footer carries.
            var how = version >= 8
                ? (method >= 1 && method <= methods.Length ? methods[method - 1] : "?")
                : method switch { 1 => "Zlib", 2 => "Gzip", _ => "?" };

            using var from = new MemoryStream(raw);
            using var into = new MemoryStream(size);

            switch (how.ToLowerInvariant())
            {
                case "zlib":
                    using (var zip = new ZLibStream(from, CompressionMode.Decompress)) { zip.CopyTo(into); }
                    break;
                case "gzip":
                    using (var zip = new GZipStream(from, CompressionMode.Decompress)) { zip.CopyTo(into); }
                    break;
                default:
                    throw new InvalidOperationException(
                        $"\"{name}\" is compressed with {how}, which this does not handle. "
                        + "Mod paks are normally stored uncompressed.");
            }

            return into.ToArray();
        }

        private static string readString(BinaryReader reader)
        {
            var length = reader.ReadInt32();

            if (length == 0) { return ""; }

            if (length < 0)
            {
                var wide = reader.ReadBytes(-length * 2);
                return Encoding.Unicode.GetString(wide).TrimEnd('\0');
            }

            var thin = reader.ReadBytes(length);
            return Encoding.UTF8.GetString(thin).TrimEnd('\0');
        }

        // ------------------------------------------------------------------ pak -> folder

        /// <summary>
        /// Writes every entry of a pak into a folder, and a manifest saying what went where.
        /// </summary>
        /// <param name="wantedLevel">
        /// Which of the mod's levels becomes level.json. A mod can carry several - Blossoming
        /// Isles has three - and only one of them can be the one the maps tab opens. The rest
        /// keep their own paths under extra, so nothing is lost and a second run with another
        /// name gives a folder for that one.
        /// </param>
        public static Manifest unpak(string pakPath, string folder, string? wantedLevel = null)
        {
            var items = read(pakPath);

            Directory.CreateDirectory(folder);

            var levels = items
                .Where(one => kindOf(one.Path) == Kind.Level)
                .ToList();

            var primary = levels.FirstOrDefault(one =>
                wantedLevel != null
                && string.Equals(Path.GetFileNameWithoutExtension(one.Path), wantedLevel,
                    StringComparison.OrdinalIgnoreCase))
                ?? levels.FirstOrDefault();

            var manifest = new Manifest
            {
                source = Path.GetFileName(pakPath),
                level = primary?.Path,
            };

            var taken = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (var item in items)
            {
                var local = placeFor(item, ReferenceEquals(item, primary));

                //Two entries that differ only in case would otherwise land on the same file on
                //Windows and the second would quietly eat the first.
                if (!taken.Add(local))
                {
                    local = Path.Combine(EXTRA, "_same", sha1(item.Data) + Path.GetExtension(item.Path));
                    taken.Add(local);
                }

                var full = Path.Combine(folder, local.Replace('/', Path.DirectorySeparatorChar));
                Directory.CreateDirectory(Path.GetDirectoryName(full)!);
                File.WriteAllBytes(full, item.Data);

                manifest.entries.Add(new Slot
                {
                    pak = item.Path,
                    file = local.Replace(Path.DirectorySeparatorChar, '/'),
                    size = item.Data.LongLength,
                    sha1 = sha1(item.Data),
                    primary = ReferenceEquals(item, primary),
                });
            }

            File.WriteAllText(Path.Combine(folder, MANIFEST),
                JsonSerializer.Serialize(manifest, new JsonSerializerOptions { WriteIndented = true }),
                new UTF8Encoding(false));

            //The same note export leaves, so Import map can say which mission this belongs over
            //rather than making the choice silently. A level's own file name is not the answer -
            //Blossoming Isles' three levels are called SakuraGarden, SakuraPagoda and
            //SakuraUndercroft and every one of them declares itself "lowertemple".
            var over = idOf(primary);
            if (over != null)
            {
                File.WriteAllText(Path.Combine(folder, MapMod.MARKER),
                    "{\n"
                    + $"  \"mission\": \"{over}\",\n"
                    + $"  \"from-pak\": \"{Path.GetFileName(pakPath)}\",\n"
                    + $"  \"level\": \"{Path.GetFileNameWithoutExtension(primary!.Path)}\",\n"
                    + $"  \"imported\": \"{DateTime.Now:yyyy-MM-dd HH:mm}\"\n"
                    + "}\n",
                    new UTF8Encoding(false));
            }

            return manifest;
        }

        /// <summary>Which of the game's missions a level claims to be, if it says.</summary>
        private static string? idOf(Item? level)
        {
            if (level == null) { return null; }

            try
            {
                var text = GameMaps.stripComments(
                    new UTF8Encoding(false).GetString(level.Data).TrimStart('﻿'));

                return (JsonNode.Parse(text, documentOptions: new JsonDocumentOptions
                {
                    AllowTrailingCommas = true,
                    CommentHandling = JsonCommentHandling.Skip,
                }) as JsonObject)?["id"]?.GetValue<string>();
            }
            catch (Exception)
            {
                return null;
            }
        }

        private enum Kind { Level, Group, Pack, Other }

        private static Kind kindOf(string path)
        {
            if (path.IndexOf(LEVELS, StringComparison.OrdinalIgnoreCase) >= 0
                && path.EndsWith(".json", StringComparison.OrdinalIgnoreCase)) { return Kind.Level; }
            if (path.IndexOf(GROUPS, StringComparison.OrdinalIgnoreCase) >= 0) { return Kind.Group; }
            if (path.IndexOf(PACKS, StringComparison.OrdinalIgnoreCase) >= 0) { return Kind.Pack; }
            return Kind.Other;
        }

        private static string after(string path, string marker)
            => path.Substring(path.IndexOf(marker, StringComparison.OrdinalIgnoreCase) + marker.Length);

        /// <summary>
        /// Where one entry lives in the folder.
        ///
        /// The three known kinds land exactly where MapMod.install would put them back from, so
        /// a folder taken apart this way is an ordinary map folder as far as the rest of the app
        /// is concerned. Everything else keeps its full path under extra, which is what makes
        /// the way back possible at all.
        /// </summary>
        private static string placeFor(Item item, bool primary)
        {
            var local = kindOf(item.Path) switch
            {
                Kind.Level when primary => "level.json",
                Kind.Group => "objectgroups/" + after(item.Path, GROUPS),
                Kind.Pack => "resourcepacks/" + after(item.Path, PACKS),
                _ => EXTRA + "/" + item.Path,
            };

            return writable(local)
                ? local
                //A name Windows will not take is worth keeping the bytes of anyway; the manifest
                //remembers which entry it was.
                : EXTRA + "/_odd/" + sha1(item.Data) + Path.GetExtension(item.Path);
        }

        private static bool writable(string local)
        {
            var bad = Path.GetInvalidFileNameChars();

            foreach (var part in local.Split('/'))
            {
                if (part.Length == 0 || part == "." || part == "..") { return false; }
                if (part.IndexOfAny(bad) >= 0) { return false; }
                if (part.EndsWith(" ") || part.EndsWith(".")) { return false; }

                var stem = Path.GetFileNameWithoutExtension(part).ToUpperInvariant();
                if (stem is "CON" or "PRN" or "AUX" or "NUL" or "COM1" or "COM2" or "COM3"
                    or "COM4" or "COM5" or "COM6" or "COM7" or "COM8" or "COM9" or "LPT1"
                    or "LPT2" or "LPT3" or "LPT4" or "LPT5" or "LPT6" or "LPT7" or "LPT8"
                    or "LPT9") { return false; }
            }

            return local.Length < 240;
        }

        private static string sha1(byte[] data) => Convert.ToHexString(SHA1.HashData(data)).ToLowerInvariant();

        // ------------------------------------------------------------------ folder -> pak

        /// <summary>The manifest a folder was taken apart with, if it has one.</summary>
        public static Manifest? manifestOf(string folder)
        {
            var file = Path.Combine(folder, MANIFEST);
            if (!File.Exists(file)) { return null; }

            try
            {
                return JsonSerializer.Deserialize<Manifest>(File.ReadAllText(file));
            }
            catch (Exception problem)
            {
                Console.WriteLine($"[modpak] the manifest would not read: {problem.Message}");
                return null;
            }
        }

        /// <summary>
        /// Everything a folder should be packed as: the manifest's entries at the paths they
        /// came from, in the order they were in, followed by whatever has been added since.
        /// </summary>
        public static IReadOnlyList<PakWriter.Entry> entriesFor(string folder, IList<string>? notes = null)
        {
            var manifest = manifestOf(folder);
            var entries = new List<PakWriter.Entry>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            if (manifest != null)
            {
                foreach (var slot in manifest.entries)
                {
                    var file = Path.Combine(folder, slot.file.Replace('/', Path.DirectorySeparatorChar));

                    if (!File.Exists(file))
                    {
                        notes?.Add($"gone from the folder, so dropped: {slot.pak}");
                        continue;
                    }

                    if (!seen.Add(slot.pak))
                    {
                        notes?.Add($"listed twice, packed once: {slot.pak}");
                        continue;
                    }

                    entries.Add(new PakWriter.Entry(slot.pak, File.ReadAllBytes(file)));
                }
            }

            //Anything the folder has gained since - a new object group, a new image in a pack -
            //belongs in the pak too, at the path the rest of the app would have given it.
            foreach (var (local, pak) in newcomers(folder))
            {
                if (!seen.Add(pak)) { continue; }
                entries.Add(new PakWriter.Entry(pak, File.ReadAllBytes(local)));
                notes?.Add($"added since it was taken apart: {pak}");
            }

            return entries;
        }

        private static IEnumerable<(string local, string pak)> newcomers(string folder)
        {
            var known = new[]
            {
                ("objectgroups", "Dungeons/Content/" + GROUPS),
                ("resourcepacks", "Dungeons/Content/" + PACKS),
            };

            foreach (var (name, prefix) in known)
            {
                var under = Path.Combine(folder, name);
                if (!Directory.Exists(under)) { continue; }

                foreach (var file in Directory.GetFiles(under, "*", SearchOption.AllDirectories))
                {
                    var leaf = Path.GetFileName(file);
                    if (leaf.StartsWith(".", StringComparison.Ordinal)
                        || leaf.EndsWith(".before", StringComparison.OrdinalIgnoreCase)
                        || leaf.EndsWith(".new", StringComparison.OrdinalIgnoreCase)
                        || leaf.EndsWith(".random", StringComparison.OrdinalIgnoreCase)
                        || leaf.EndsWith(".multitile", StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    yield return (file, prefix
                        + Path.GetRelativePath(under, file).Replace(Path.DirectorySeparatorChar, '/'));
                }
            }
        }

        /// <summary>
        /// Packs a folder back into a pak carrying every entry it was made from.
        /// </summary>
        public static int pack(string folder, string outPath, IList<string>? notes = null)
        {
            var entries = entriesFor(folder, notes);

            if (entries.Count == 0)
            {
                throw new InvalidOperationException(
                    $"There is nothing to pack in {folder} - no {MANIFEST} and no object groups.");
            }

            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(outPath))!);
            PakWriter.write(outPath, entries);

            return entries.Count;
        }

        /// <summary>
        /// The rest of what a mod shipped, for an install that is already carrying the level,
        /// the groups and the packs.
        ///
        /// Install answers a different question from <see cref="pack"/>: it is putting this
        /// folder over one particular mission, so the level goes in under that mission's name
        /// and claims its id. Everything else the mod arrived with - its fonts, its widgets, its
        /// string table, its sub-levels - has no such question hanging over it and goes back
        /// exactly as it came.
        /// </summary>
        public static IReadOnlyList<PakWriter.Entry> extras(string folder,
            IEnumerable<PakWriter.Entry> already)
        {
            var manifest = manifestOf(folder);
            if (manifest == null) { return Array.Empty<PakWriter.Entry>(); }

            var have = new HashSet<string>(already.Select(one => one.Path),
                StringComparer.OrdinalIgnoreCase);

            var rest = new List<PakWriter.Entry>();

            foreach (var slot in manifest.entries)
            {
                //The level install is writing itself, under the mission's name rather than the
                //mod's. Shipping the mod's copy as well would put a second level in the pak that
                //nothing asks for.
                if (slot.primary) { continue; }

                if (!have.Add(slot.pak)) { continue; }

                var file = Path.Combine(folder, slot.file.Replace('/', Path.DirectorySeparatorChar));
                if (!File.Exists(file)) { continue; }

                rest.Add(new PakWriter.Entry(slot.pak, File.ReadAllBytes(file)));
            }

            return rest;
        }

        // ------------------------------------------------------------------ checking

        public sealed class Difference
        {
            public List<string> Missing { get; } = new List<string>();
            public List<string> Extra { get; } = new List<string>();
            public List<string> Changed { get; } = new List<string>();
            public int Matched { get; set; }
            public int Before { get; set; }
            public int After { get; set; }

            public bool Same => Missing.Count == 0 && Extra.Count == 0 && Changed.Count == 0;
        }

        /// <summary>
        /// Entry for entry and byte for byte, what a repack lost, gained or altered.
        /// </summary>
        public static Difference compare(string before, string after)
        {
            var theirs = read(before);
            var ours = read(after);

            var was = new Dictionary<string, byte[]>(StringComparer.Ordinal);
            foreach (var one in theirs) { was[one.Path] = one.Data; }

            var now = new Dictionary<string, byte[]>(StringComparer.Ordinal);
            foreach (var one in ours) { now[one.Path] = one.Data; }

            var difference = new Difference { Before = theirs.Count, After = ours.Count };

            foreach (var (path, data) in was)
            {
                if (!now.TryGetValue(path, out var mine)) { difference.Missing.Add(path); continue; }
                if (!data.AsSpan().SequenceEqual(mine)) { difference.Changed.Add(path); continue; }
                difference.Matched++;
            }

            foreach (var path in now.Keys)
            {
                if (!was.ContainsKey(path)) { difference.Extra.Add(path); }
            }

            return difference;
        }

        /// <summary>
        /// What a mod's levels say about themselves, for the report a probe prints.
        /// </summary>
        public static string describe(IReadOnlyList<Item> items)
        {
            var lines = new StringBuilder();

            foreach (var item in items.Where(one => kindOf(one.Path) == Kind.Level))
            {
                var name = Path.GetFileNameWithoutExtension(item.Path);

                try
                {
                    var text = GameMaps.stripComments(
                        new UTF8Encoding(false).GetString(item.Data).TrimStart('﻿'));

                    var level = JsonNode.Parse(text, documentOptions: new JsonDocumentOptions
                    {
                        AllowTrailingCommas = true,
                        CommentHandling = JsonCommentHandling.Skip,
                    }) as JsonObject;

                    lines.AppendLine($"{name}: id \"{level?["id"]}\", "
                        + $"loctable \"{level?["loctable-id"]}\", "
                        + $"ambience \"{level?["ambience-level-id"]}\"");

                    if (level?["objectives"] is JsonArray objectives)
                    {
                        foreach (var one in objectives.OfType<JsonObject>())
                        {
                            lines.AppendLine($"    objective name \"{one["name"]}\" "
                                + $"description \"{one["description"]}\"");
                        }
                    }
                }
                catch (Exception problem)
                {
                    lines.AppendLine($"{name}: would not parse - {problem.Message}");
                }
            }

            return lines.ToString();
        }
    }
}
