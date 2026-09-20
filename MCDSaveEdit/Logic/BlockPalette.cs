using SkiaSharp;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
#nullable enable

namespace MCDSaveEdit.Logic
{
    /// <summary>
    /// What a block looks like, taken from the game rather than guessed.
    ///
    /// Every mission carries its own resource pack - data/resourcepacks/&lt;Name&gt; - and getting a
    /// colour out of one is a chain of three files, none of which can be skipped:
    ///
    ///   blocks                   block NAME -> a texture KEY per face
    ///   images/terrain_texture   key -> a LIST of texture names, indexed by the metadata nibble
    ///   images/blocks/&lt;name&gt;     the texture, as a plain 16x16 PNG
    ///
    /// The middle step is the one that is easy to miss and the one that matters most. "planks" is
    /// not a texture, it is a key standing for six of them - oak, spruce, birch, jungle, acacia,
    /// big oak - and which you get depends on the block's metadata. Read blocks alone and a third
    /// of the game has no texture at all, because names like "planks" and "stone" are not files.
    ///
    /// So the colours here are the mission's own, down to the variant. Creeper Woods' grass is
    /// Creeper Woods' grass, and its granite is granite rather than an average of every stone.
    ///
    /// Note what is NOT here: the numeric id. The pack's key order looks like it - air 0, stone 1,
    /// grass 2 - and stops being it somewhere around a hundred, because the pack is written in
    /// Bedrock's order and a mission's blocks are numbered in Java's. See [[GameBlocks]], which
    /// holds the real table; everything here is looked up by name.
    /// </summary>
    public static class BlockPalette
    {
        private const string PACKS = "/Dungeons/Content/data/resourcepacks/";

        /// <summary>One block id, as far as a renderer cares.</summary>
        public sealed class Look
        {
            public Look(string name, string shape, uint[] top, uint[] side, int forced = -1)
            {
                Name = name;
                Shape = shape;
                _top = top;
                _side = side;
                _forced = forced;
            }

            private readonly uint[] _top;
            private readonly uint[] _side;

            //Some blocks are a whole id in Dungeons and one nibble of another in the pack - every
            //colour of concrete, every wood of plank. Those ask the pack for a fixed variant and
            //ignore whatever nibble the map cell happens to carry.
            private readonly int _forced;

            /// <summary>What the pack calls it: "grass", "planks", "sponge.dry".</summary>
            public string Name { get; }

            /// <summary>The pack's own blockshape, or "" when it did not say.</summary>
            public string Shape { get; }

            /// <summary>How many variants the metadata nibble can pick between.</summary>
            public int Variants => Math.Max(_top.Length, _side.Length);

            /// <summary>The up face of one variant, packed 0xAARRGGBB, or zero for none.</summary>
            public uint topOf(int meta) => pick(_top, _forced >= 0 ? _forced : meta);

            /// <summary>A side face of one variant.</summary>
            public uint sideOf(int meta) => pick(_side, _forced >= 0 ? _forced : meta);

            private static uint pick(uint[] from, int meta)
            {
                if (from.Length == 0) { return 0u; }
                //A list shorter than the metadata it is indexed by is normal: most blocks state
                //one texture and wear it whatever their nibble says.
                return from[meta < 0 || meta >= from.Length ? 0 : meta];
            }

            /// <summary>
            /// Air and anything the pack draws as nothing.
            ///
            /// invisibleBedrock is the one that matters: it is the wall the game puts round the
            /// edge of a mission so you cannot walk off it, and it is exactly as invisible in
            /// play as its name says. Drawing it buries the map under flat slabs.
            /// </summary>
            public bool Invisible => Shape == "invisible" || Shape == "void";

            /// <summary>Water and lava: seen, walked through, never stood on.</summary>
            public bool Liquid => Shape == "water";

            /// <summary>
            /// Grass tufts, flowers, vines - two crossed quads, not a cube.
            ///
            /// Worth knowing separately because a mesher that treats these as solid fills a
            /// forest floor with opaque boxes, and one that treats them as air loses the
            /// undergrowth entirely.
            /// </summary>
            public bool Foliage => Shape == "cross_texture";

            /// <summary>Something a mob can stand on.</summary>
            public bool Solid => !Invisible && !Liquid && !Foliage;

            public override string ToString() => $"{Name} ({Shape}, {Variants} variants)";
        }

        private static readonly JsonDocumentOptions LENIENT = new JsonDocumentOptions
        {
            AllowTrailingCommas = true,
            CommentHandling = JsonCommentHandling.Skip,
        };

        //A pack is read once. Missions share packs, and a texture decoded twice is a texture
        //decoded once too often.
        private static readonly Dictionary<string, Look[]> _packs =
            new Dictionary<string, Look[]>(StringComparer.OrdinalIgnoreCase);

        private static readonly Dictionary<string, uint> _colours =
            new Dictionary<string, uint>(StringComparer.OrdinalIgnoreCase);

        /// <summary>What went wrong, or how much was found, for a probe to print.</summary>
        public static List<string> Notes { get; } = new List<string>();

        /// <summary>
        /// The table a mission paints with.
        ///
        /// A level names its packs in playing order and later ones win, which is how a mission
        /// re-skins a handful of blocks without restating the other three hundred.
        /// </summary>
        public static Look[] forMission(JsonObject level)
        {
            var packs = (level["resource-packs"] as JsonArray)?
                .Select(one => one?.GetValue<string>())
                .Where(one => !string.IsNullOrEmpty(one))
                .Select(one => one!)
                .ToList();

            if (packs == null || packs.Count == 0)
            {
                Notes.Add("the level names no resource pack");
                return Array.Empty<Look>();
            }

            var built = new List<Look>();

            foreach (var pack in packs)
            {
                var table = forPack(pack);
                for (var id = 0; id < table.Length; id++)
                {
                    //A later pack replaces a block it mentions and leaves the rest alone. It does
                    //not shorten the table: a pack naming ten blocks must not erase block 200.
                    while (built.Count <= id) { built.Add(null!); }
                    if (table[id] != null) { built[id] = table[id]; }
                }
            }

            return built.ToArray();
        }

        /// <summary>One resource pack's blocks, by id.</summary>
        public static Look[] forPack(string pack)
        {
            if (_packs.TryGetValue(pack, out var already)) { return already; }

            var made = build(pack);
            _packs[pack] = made;
            return made;
        }

        /// <summary>A texture name and whatever the pack wants multiplied over it.</summary>
        private readonly struct Skin
        {
            public Skin(string name, uint tint) { Name = name; Tint = tint; }
            public string Name { get; }
            public uint Tint { get; }
        }

        private static Look[] build(string pack)
        {
            var paths = readPaths(pack);
            var keys = readKeys(pack);

            var bytes = GameMaps.read(PACKS + pack + "/blocks");
            if (bytes == null)
            {
                Notes.Add($"{pack}: no blocks file");
                return Array.Empty<Look>();
            }

            JsonObject? parsed;
            try
            {
                parsed = JsonNode.Parse(bytes, null, LENIENT) as JsonObject;
            }
            catch (Exception problem)
            {
                Notes.Add($"{pack}: blocks file will not parse - {problem.Message}");
                return Array.Empty<Look>();
            }

            if (parsed == null) { return Array.Empty<Look>(); }

            //By name, never by position. The pack's key order is Bedrock's and a mission's block
            //ids are Java's; they agree up to about a hundred and then drift, which is how a
            //forest ends up painted in redstone. GameBlocks holds which name each id wears.
            var byName = new Dictionary<string, JsonObject>(StringComparer.OrdinalIgnoreCase);
            foreach (var one in parsed)
            {
                if (one.Value is JsonObject body) { byName[one.Key] = body; }
            }

            var top = GameBlocks.ALL.Keys.DefaultIfEmpty(0).Max();
            var made = new Look[top + 1];
            var coloured = 0;
            var guessed = 0;

            foreach (var entry in GameBlocks.ALL)
            {
                var block = entry.Value;
                var body = block.Key.Length > 0 && byName.TryGetValue(block.Key, out var found)
                    ? found
                    : null;

                var shape = body?["blockshape"]?.GetValue<string>() ?? "";
                var textures = body?["textures"];

                var tops = coloursFor(pack, paths, keys, textureKey(textures, "up"));
                var sides = coloursFor(pack, paths, keys, textureKey(textures, "side"));

                //Nothing in this pack draws it, so Minecraft's own colour stands in.
                if ((tops.Length == 0 || tops[0] == 0) && block.Fallback != 0)
                {
                    tops = new[] { block.Fallback };
                    sides = new[] { block.Fallback };
                    guessed++;
                }

                var look = new Look(
                    body != null ? block.Key : block.Name,
                    shape,
                    tops,
                    sides,
                    block.Meta);

                made[entry.Key] = look;
                if (look.topOf(0) != 0) { coloured++; }
            }

            Notes.Add($"{pack}: {GameBlocks.ALL.Count:N0} ids, {coloured:N0} coloured "
                + $"({guessed:N0} from Minecraft rather than the pack), "
                + $"{byName.Count:N0} pack blocks, {keys.Count:N0} texture keys");

            return made;
        }

        /// <summary>
        /// The pack's own name-to-file table, for the handful it does not spell by convention.
        ///
        /// Most names turn into a path by rule - block.planks.big.oak is images/blocks/planks_big_oak
        /// - but the pack keeps an explicit map too, and where it disagrees it is right.
        /// </summary>
        private static Dictionary<string, string> readPaths(string pack)
        {
            var found = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

            var bytes = GameMaps.read(PACKS + pack + "/resources");
            if (bytes == null) { return found; }

            try
            {
                var parsed = JsonNode.Parse(bytes, null, LENIENT) as JsonObject;
                var textures = parsed?["resources"]?["textures"] as JsonObject;
                if (textures == null) { return found; }

                foreach (var one in textures)
                {
                    if (one.Value is not JsonValue value
                        || !value.TryGetValue<string>(out var path)) { continue; }
                    found[one.Key] = path;
                }
            }
            catch (Exception)
            {
                //A pack without a readable resources file still works by convention.
            }

            return found;
        }

        /// <summary>Texture key to its variants, in metadata order.</summary>
        private static Dictionary<string, Skin[]> readKeys(string pack)
        {
            var found = new Dictionary<string, Skin[]>(StringComparer.OrdinalIgnoreCase);

            var bytes = GameMaps.read(PACKS + pack + "/images/terrain_texture");
            if (bytes == null)
            {
                Notes.Add($"{pack}: no terrain_texture - names like \"planks\" cannot be resolved");
                return found;
            }

            try
            {
                var parsed = JsonNode.Parse(bytes, null, LENIENT) as JsonObject;
                var data = parsed?["texture_data"] as JsonObject;
                if (data == null) { return found; }

                foreach (var one in data)
                {
                    var textures = (one.Value as JsonObject)?["textures"];
                    if (textures == null) { continue; }

                    var list = textures as JsonArray;
                    var skins = list == null
                        ? new[] { skinOf(textures) }
                        : list.Select(skinOf).ToArray();

                    found[one.Key] = skins.Where(skin => skin.Name.Length > 0).ToArray();
                }
            }
            catch (Exception problem)
            {
                Notes.Add($"{pack}: terrain_texture will not parse - {problem.Message}");
            }

            return found;
        }

        /// <summary>
        /// One entry of a variant list.
        ///
        /// Usually just a name. Sometimes an object carrying an overlay colour, which is how the
        /// game tints grass and leaves per mission - the texture itself is grey.
        /// </summary>
        private static Skin skinOf(JsonNode? node)
        {
            if (node is JsonValue value && value.TryGetValue<string>(out var flat))
            {
                return new Skin(flat, 0u);
            }

            if (node is JsonObject body)
            {
                var name = body["path"]?.GetValue<string>() ?? "";
                var tint = body["overlay_color"]?.GetValue<string>()
                    ?? body["tint_color"]?.GetValue<string>();
                return new Skin(name, hexOf(tint));
            }

            return new Skin("", 0u);
        }

        private static uint hexOf(string? text)
        {
            if (string.IsNullOrEmpty(text)) { return 0u; }
            var body = text!.TrimStart('#');
            if (body.Length < 6) { return 0u; }
            return uint.TryParse(body.Substring(0, 6),
                System.Globalization.NumberStyles.HexNumber, null, out var made)
                ? 0xFF000000u | made
                : 0u;
        }

        /// <summary>
        /// Which texture key a face wears.
        ///
        /// "textures" is either one key for the whole cube or an object naming up, down and side
        /// separately. A face the object does not mention falls back to side, then to whatever it
        /// does name, because a block with only an "up" is still better drawn in that colour than
        /// in nothing at all.
        /// </summary>
        private static string? textureKey(JsonNode? textures, string face)
        {
            if (textures == null) { return null; }

            if (textures is JsonValue only)
            {
                return only.TryGetValue<string>(out var flat) ? flat : null;
            }

            if (textures is not JsonObject byFace) { return null; }

            foreach (var want in new[] { face, "side", "up", "down" })
            {
                var got = byFace[want];
                if (got is JsonValue value && value.TryGetValue<string>(out var name)) { return name; }

                if (got is JsonArray list && list.Count > 0
                    && list[0] is JsonValue first && first.TryGetValue<string>(out var firstName))
                {
                    return firstName;
                }
            }

            return null;
        }

        private static uint[] coloursFor(string pack, Dictionary<string, string> paths,
                                         Dictionary<string, Skin[]> keys, string? key)
        {
            if (string.IsNullOrEmpty(key)) { return Array.Empty<uint>(); }

            //A key the atlas never heard of may still be a file: some packs name the texture
            //directly, and refusing on principle loses a colour that was right there.
            var skins = keys.TryGetValue(key!, out var found) && found.Length > 0
                ? found
                : new[] { new Skin(key!, 0u) };

            return skins.Select(skin => colourOf(pack, paths, skin)).ToArray();
        }

        /// <summary>
        /// A texture's average colour, ignoring what you can see through.
        ///
        /// Averaging the alpha in would drag every leaf and every pane of glass toward black,
        /// because a transparent pixel in a PNG is usually black as well as invisible. Weighting
        /// by alpha instead gives leaves the colour of their leaves.
        /// </summary>
        private static uint colourOf(string pack, Dictionary<string, string> paths, Skin skin)
        {
            if (skin.Name.Length == 0) { return 0u; }

            var key = pack + "/" + skin.Name + "/" + skin.Tint;
            if (_colours.TryGetValue(key, out var already)) { return already; }

            var made = tint(decode(pack, paths, skin.Name), skin.Tint);
            _colours[key] = made;
            return made;
        }

        private static uint tint(uint colour, uint over)
        {
            if (colour == 0 || over == 0) { return colour; }

            //Multiply, the way the game does it: the texture is a grey mask and the overlay says
            //what colour the mission's grass actually is.
            var r = ((colour >> 16) & 0xFF) * ((over >> 16) & 0xFF) / 255;
            var g = ((colour >> 8) & 0xFF) * ((over >> 8) & 0xFF) / 255;
            var b = (colour & 0xFF) * (over & 0xFF) / 255;

            return 0xFF000000u | (r << 16) | (g << 8) | b;
        }

        private static uint decode(string pack, Dictionary<string, string> paths, string name)
        {
            var bytes = read(pack, paths, name);
            if (bytes == null) { return 0u; }

            try
            {
                using var bitmap = SKBitmap.Decode(bytes);
                if (bitmap == null) { return 0u; }

                double r = 0, g = 0, b = 0, weight = 0;

                for (var y = 0; y < bitmap.Height; y++)
                {
                    for (var x = 0; x < bitmap.Width; x++)
                    {
                        var pixel = bitmap.GetPixel(x, y);
                        if (pixel.Alpha == 0) { continue; }

                        var a = pixel.Alpha / 255.0;
                        r += pixel.Red * a;
                        g += pixel.Green * a;
                        b += pixel.Blue * a;
                        weight += a;
                    }
                }

                if (weight <= 0) { return 0u; }

                //Alpha stays at full. How much of a texture is see-through is the mesher's
                //business, and it reads blockshape for that; a colour with a hole in it only
                //makes every later blend wrong.
                return 0xFF000000u
                    | ((uint)Math.Round(r / weight) << 16)
                    | ((uint)Math.Round(g / weight) << 8)
                    | (uint)Math.Round(b / weight);
            }
            catch (Exception)
            {
                return 0u;
            }
        }

        /// <summary>The bytes of one texture, by whichever spelling finds it.</summary>
        private static byte[]? read(string pack, Dictionary<string, string> paths, string name)
        {
            if (paths.TryGetValue(name, out var stated))
            {
                //The index strips the last extension from every entry it holds, so the .png the
                //pack states has to come off again before anything will match.
                var trimmed = stated.EndsWith(".png", StringComparison.OrdinalIgnoreCase)
                    ? stated.Substring(0, stated.Length - 4)
                    : stated;

                var got = GameMaps.read(PACKS + pack + "/" + trimmed.TrimStart('/'));
                if (got != null) { return got; }
            }

            //By rule: block.planks.big.oak is images/blocks/planks_big_oak.
            var plain = name.StartsWith("block.", StringComparison.OrdinalIgnoreCase)
                ? name.Substring("block.".Length)
                : name;

            return GameMaps.read(PACKS + pack + "/images/blocks/" + plain.Replace('.', '_'))
                ?? GameMaps.read(PACKS + pack + "/images/" + plain.Replace('.', '_'));
        }
    }
}
