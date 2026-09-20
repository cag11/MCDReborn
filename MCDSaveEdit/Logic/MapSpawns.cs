using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
#nullable enable

namespace MCDSaveEdit.Logic
{
    /// <summary>
    /// The gameplay layer of a mission: what spawns, where, and how much of it.
    ///
    /// Two things decide that, and they live in different files. A level's mob groups say WHAT
    /// appears - and bosses are ordinary entries there, so swapping `redstonemonstrosity` for
    /// `zombie` is the same edit as any other. A tile's regions of type "spawn" say WHERE, one
    /// small box per spawn point; the game ships 22,530 of them, so this is the normal way mobs
    /// are placed rather than an exception.
    ///
    /// Everything is read and written as a JSON document rather than a model, because these files
    /// carry fields nothing here understands and rewriting them from a model would quietly drop
    /// whatever was not modelled.
    /// </summary>
    public static class MapSpawns
    {
        /// <summary>One room, where it sits once the level is assembled.</summary>
        public sealed class Room
        {
            public Room(string id, string stretch, JsonObject tile, string file,
                int[] size, int[] pos)
            {
                Id = id;
                Stretch = stretch;
                Tile = tile;
                File = file;
                Size = size;
                Pos = pos;
            }

            public string Id { get; }

            /// <summary>Which stretch plays it, which is how somebody knows where they are.</summary>
            public string Stretch { get; }

            public JsonObject Tile { get; }

            /// <summary>The object group it came from, so an edit goes home to the right file.</summary>
            public string File { get; }

            public int[] Size { get; }
            public int[] Pos { get; set; }

            public JsonArray Regions
            {
                get
                {
                    if (Tile["regions"] is not JsonArray found)
                    {
                        found = new JsonArray();
                        Tile["regions"] = found;
                    }
                    return found;
                }
            }

            public int Spawns => Regions.Count(one =>
                one?["type"]?.GetValue<string>() == "spawn");

            public override string ToString()
                => Spawns > 0 ? $"{Stretch}   —   {Id}  ·  {Spawns} spawns" : $"{Stretch}   —   {Id}";
        }

        public sealed class Map
        {
            public Map(string folder, JsonObject level, Dictionary<string, JsonObject> groups,
                List<Room> rooms, List<string> notes)
            {
                Folder = folder;
                Level = level;
                Groups = groups;
                Rooms = rooms;
                Notes = notes;
            }

            public string Folder { get; }
            public JsonObject Level { get; }

            /// <summary>Each object group file, by its path relative to the map folder.</summary>
            public Dictionary<string, JsonObject> Groups { get; }

            public List<Room> Rooms { get; }
            public List<string> Notes { get; }

            /// <summary>Files touched since loading, so saving writes only what changed.</summary>
            public HashSet<string> Changed { get; } = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        }

        private static readonly JsonDocumentOptions LENIENT = new JsonDocumentOptions
        {
            AllowTrailingCommas = true,
            CommentHandling = JsonCommentHandling.Skip,
        };

        private static JsonObject? read(string path)
        {
            try
            {
                var text = GameMaps.stripComments(File.ReadAllText(path));
                return JsonNode.Parse(text, documentOptions: LENIENT) as JsonObject;
            }
            catch (Exception)
            {
                return null;
            }
        }

        /// <summary>
        /// Reads a whole exported mission.
        ///
        /// A mission is a level file plus up to sixteen object groups, and choosing between them by
        /// hand is not something to ask of anybody - so all of it is read at once and assembled.
        /// </summary>
        public static Map load(string folder)
        {
            var notes = new List<string>();

            //The level as it is, welded and all.
            //
            //A welded mission is one room a thousand blocks across, and that is the right thing to
            //show: it is one mission, and somebody editing it is editing a map rather than a set
            //of parts. The floor plan comes from the tile's own height plane, so one room is still
            //a map to look at and click on rather than an empty box.
            var levelPath = Path.Combine(folder, "level.json");
            if (!File.Exists(levelPath)) { levelPath = Path.Combine(folder, "level"); }

            var level = read(levelPath)
                ?? throw new InvalidOperationException(
                    $"No level file in {folder}. Export the mission first.");

            var groups = new Dictionary<string, JsonObject>(StringComparer.OrdinalIgnoreCase);
            var tiles = new Dictionary<string, (JsonObject tile, string file)>(StringComparer.OrdinalIgnoreCase);

            var root = Path.Combine(folder, "objectgroups");
            if (Directory.Exists(root))
            {
                foreach (var file in Directory.GetFiles(root, "objectgroup.json", SearchOption.AllDirectories))
                {
                    var parsed = read(file);
                    if (parsed == null) { notes.Add($"could not read {Path.GetFileName(file)}"); continue; }

                    var inside = Path.GetRelativePath(folder, file).Replace('\\', '/');
                    groups[inside] = parsed;

                    foreach (var one in (parsed["objects"] as JsonArray) ?? new JsonArray())
                    {
                        if (one is not JsonObject tile) { continue; }
                        var id = tile["id"]?.GetValue<string>();
                        //The first file wins. Two groups naming the same tile is the game's
                        //business, and silently preferring the later one would change which room
                        //a level draws.
                        if (id != null && !tiles.ContainsKey(id)) { tiles[id] = (tile, inside); }
                    }
                }
            }

            var rooms = assemble(order(level, notes), tiles, notes);
            notes.Add($"{rooms.Count} rooms, {tiles.Count} tiles across {groups.Count} files");

            return new Map(folder, level, groups, rooms, notes);
        }

        /// <summary>The rooms a level plays, in order, as far as each stretch names just one.</summary>
        private static List<(string id, string stretch)> order(JsonObject level, List<string> notes)
        {
            var found = new List<(string, string)>();
            var stretches = level["stretches"] as JsonArray ?? new JsonArray();
            var random = 0;

            for (var at = 0; at < stretches.Count; at++)
            {
                if (stretches[at] is not JsonObject stretch) { continue; }
                var tiles = stretch["tiles"] as JsonArray;

                //A stretch that still picks at random has no single room to show, and guessing
                //would draw a level the game will not build.
                if (tiles == null || tiles.Count != 1) { random++; continue; }

                var id = tiles[0] is JsonValue value && value.TryGetValue<string>(out var name)
                    ? name
                    : tiles[0]?["id"]?.GetValue<string>();
                if (id == null) { random++; continue; }

                var label = stretch["id"]?.GetValue<string>();
                found.Add((id, string.IsNullOrWhiteSpace(label) ? $"stretch {at}" : label!));
            }

            if (random > 0)
            {
                notes.Add($"{random} stretch(es) still pick at random and are not shown - "
                    + "run Edit in Minecraft once to pin the level");
            }

            return found;
        }

        //--- laying the rooms out ----------------------------------------------------------------

        private static readonly Dictionary<string, string> OPPOSITE = new Dictionary<string, string>
        {
            { "x-", "x+" }, { "x+", "x-" }, { "z-", "z+" }, { "z+", "z-" },
        };

        private static readonly Dictionary<string, int[]> STEP = new Dictionary<string, int[]>
        {
            { "x+", new[] { 1, 0, 0 } }, { "x-", new[] { -1, 0, 0 } },
            { "z+", new[] { 0, 0, 1 } }, { "z-", new[] { 0, 0, -1 } },
        };

        private static int[] ints(JsonNode? node, int count)
        {
            var array = node as JsonArray;
            var got = new int[count];
            for (var i = 0; i < count && array != null && i < array.Count; i++)
            {
                got[i] = array[i]?.GetValue<int>() ?? 0;
            }
            return got;
        }

        private static string? faceOf(JsonObject door, int[] size)
        {
            var pos = ints(door["pos"], 3);
            if (pos[0] == 0) { return "x-"; }
            if (pos[0] >= size[0] - 1) { return "x+"; }
            if (pos[2] == 0) { return "z-"; }
            if (pos[2] >= size[2] - 1) { return "z+"; }
            return null;
        }

        private static bool overlaps(int[] aPos, int[] aSize, int[] bPos, int[] bSize)
        {
            for (var axis = 0; axis < 3; axis++)
            {
                if (aPos[axis] + aSize[axis] <= bPos[axis]) { return false; }
                if (bPos[axis] + bSize[axis] <= aPos[axis]) { return false; }
            }
            return true;
        }

        /// <summary>
        /// Puts the rooms where the level plays them, joined at their doors.
        ///
        /// The same arrangement the Minecraft export produces, so what is on screen here is what
        /// gets walked there. Doors are tried forward-first so a level marches away from its start
        /// rather than doubling back into itself, which is what makes rooms collide; where a join
        /// would overlap, the room slides along that axis until clear.
        /// </summary>
        private static List<Room> assemble(List<(string id, string stretch)> order,
            Dictionary<string, (JsonObject tile, string file)> tiles, List<string> notes)
        {
            var placed = new List<Room>();
            var preferred = new[] { "x+", "z+", "x-", "z-" };

            foreach (var (id, stretch) in order)
            {
                if (!tiles.TryGetValue(id, out var found))
                {
                    notes.Add($"{id} is named by the level but not in any object group");
                    continue;
                }

                var size = ints(found.tile["size"], 3);

                if (placed.Count == 0)
                {
                    placed.Add(new Room(id, stretch, found.tile, found.file, size, new[] { 0, 0, 0 }));
                    continue;
                }

                var before = placed[placed.Count - 1];
                int[]? best = null;
                var bestSlid = int.MaxValue;

                foreach (var face in preferred)
                {
                    foreach (var outNode in (before.Tile["doors"] as JsonArray) ?? new JsonArray())
                    {
                        if (outNode is not JsonObject outDoor) { continue; }
                        if (faceOf(outDoor, before.Size) != face) { continue; }

                        foreach (var intoNode in (found.tile["doors"] as JsonArray) ?? new JsonArray())
                        {
                            if (intoNode is not JsonObject into) { continue; }
                            if (faceOf(into, size) != OPPOSITE[face]) { continue; }

                            var step = STEP[face];
                            var axis = face[0] == 'x' ? 0 : 2;
                            var outPos = ints(outDoor["pos"], 3);
                            var intoPos = ints(into["pos"], 3);

                            var pos = new int[3];
                            for (var i = 0; i < 3; i++)
                            {
                                pos[i] = before.Pos[i] + outPos[i] - intoPos[i] + step[i];
                            }

                            var slid = 0;
                            var ok = true;
                            while (placed.Any(one => overlaps(pos, size, one.Pos, one.Size)))
                            {
                                pos[axis] += step[axis] * 8;
                                slid += 8;
                                if (slid > 4096) { ok = false; break; }
                            }
                            if (!ok) { continue; }

                            if (slid < bestSlid) { best = pos; bestSlid = slid; }
                            if (slid == 0) { break; }
                        }
                        if (bestSlid == 0) { break; }
                    }
                    if (bestSlid == 0) { break; }
                }

                //Nothing matched, so it goes beyond everything rather than inside something.
                best ??= new[] { placed.Max(one => one.Pos[0] + one.Size[0]) + 16, 0, 0 };
                placed.Add(new Room(id, stretch, found.tile, found.file, size, best));
            }

            //Lift the run so nothing sits below zero. Rooms join at doors, doors sit at different
            //heights, and a level that steps downward goes negative.
            if (placed.Count > 0)
            {
                var lowest = placed.Min(one => one.Pos[1]);
                if (lowest < 0)
                {
                    foreach (var one in placed) { one.Pos[1] -= lowest; }
                }
            }

            return placed;
        }

        //--- the blocks, for standing spawns on the floor -----------------------------------------

        /// <summary>
        /// A room's block ids, so a spawn can be put on the ground rather than inside it.
        ///
        /// One byte of id per block, indexed x + sizeX * (z + sizeZ * y), after zlib and base64.
        /// Tiles using ids above 255 store two bytes each instead, which is told apart by length.
        /// </summary>
        public static byte[]? blocksOf(Room room)
        {
            var encoded = room.Tile["blocks"]?.GetValue<string>();
            if (encoded == null) { return null; }

            try
            {
                using var raw = new MemoryStream(Convert.FromBase64String(encoded));
                using var unzip = new ZLibStream(raw, CompressionMode.Decompress);
                using var made = new MemoryStream();
                unzip.CopyTo(made);

                var bytes = made.ToArray();
                var count = room.Size[0] * room.Size[1] * room.Size[2];

                if (bytes.Length > count * 2)
                {
                    //16-bit ids. Only the low byte matters for "is there anything here".
                    var wide = new byte[count];
                    for (var i = 0; i < count; i++)
                    {
                        wide[i] = (byte)(bytes[i * 2] != 0 || bytes[i * 2 + 1] != 0 ? 1 : 0);
                    }
                    return wide;
                }

                return bytes.Take(count).ToArray();
            }
            catch (Exception)
            {
                return null;
            }
        }

        /// <summary>
        /// The room's floor plan: how high the ground is under every column.
        ///
        /// This is what makes one welded room look like a mission rather than an empty box. The
        /// tile already carries it - a height plane is one byte per column, which is exactly a
        /// top-down picture - so nothing has to be computed from three million blocks.
        ///
        /// Zero means no ground at all there, which is the space between the rooms.
        /// </summary>
        public static byte[]? heightsOf(Room room)
        {
            var encoded = room.Tile["height-plane"]?.GetValue<string>();
            if (encoded == null) { return null; }

            try
            {
                using var raw = new MemoryStream(Convert.FromBase64String(encoded));
                using var unzip = new ZLibStream(raw, CompressionMode.Decompress);
                using var made = new MemoryStream();
                unzip.CopyTo(made);

                var bytes = made.ToArray();
                var wanted = room.Size[0] * room.Size[2];
                return bytes.Length == wanted ? bytes : null;
            }
            catch (Exception)
            {
                return null;
            }
        }

        /// <summary>The first free cell above solid ground under a column, or nothing.</summary>
        public static int? floorUnder(Room room, byte[] blocks, int x, int z, int from)
        {
            var sx = room.Size[0];
            var sy = room.Size[1];
            var sz = room.Size[2];

            if (x < 0 || z < 0 || x >= sx || z >= sz) { return null; }

            int at(int xx, int yy, int zz) => xx + sx * (zz + sz * yy);

            for (var y = Math.Min(Math.Max(from, 0), sy - 1); y >= 0; y--)
            {
                if (blocks[at(x, y, z)] == 0) { continue; }
                var above = y + 1;
                if (above >= sy) { return null; }
                if (blocks[at(x, above, z)] != 0) { continue; }
                return above;
            }

            return null;
        }

        /// <summary>What the game tags its own spawn points with, and how often.</summary>
        public static readonly (string tag, string note)[] TAGS =
        {
            ("", "ordinary - the generator decides (14,694 in the game)"),
            ("static", "placed exactly here rather than wandered in (4,186)"),
            ("arena", "part of an arena fight (1,661)"),
            ("immob", "does not move (1,610)"),
            ("fixed", "fixed placement (270)"),
        };

        /// <summary>
        /// Scatters spawn points about a spot, each dropped onto the floor beneath it.
        ///
        /// Deterministic for a given seed, because an editor that reshuffles everything when a
        /// number is nudged is hard to work with. Tries many more places than it needs: some land
        /// in walls or off the room, and stopping after exactly `count` attempts would quietly
        /// place fewer than asked for.
        /// </summary>
        public static int place(Room room, int cx, int cy, int cz, int radius, int count,
            string tag, int seed = 1)
        {
            var blocks = blocksOf(room);
            if (blocks == null) { return 0; }

            var state = (uint)seed;
            double next()
            {
                state = state * 1664525 + 1013904223;
                return state / 4294967296.0;
            }

            var made = 0;
            var tries = Math.Max(count * 12, 48);
            var put = new List<(int x, int y, int z)>();

            for (var attempt = 0; attempt < tries && made < count; attempt++)
            {
                var angle = next() * Math.PI * 2;
                //Square-rooted so points spread evenly over the circle rather than bunching up
                //in the middle.
                var away = Math.Sqrt(next()) * radius;

                var x = (int)Math.Round(cx + Math.Cos(angle) * away);
                var z = (int)Math.Round(cz + Math.Sin(angle) * away);

                var y = floorUnder(room, blocks, x, z, cy + 2);
                if (y == null) { continue; }

                if (put.Any(one => one.x == x && one.y == y.Value && one.z == z)) { continue; }
                put.Add((x, y.Value, z));

                room.Regions.Add(new JsonObject
                {
                    ["locked"] = false,
                    ["name"] = "",
                    ["pos"] = new JsonArray(x, y.Value, z),
                    ["size"] = new JsonArray(2, 1, 2),
                    ["tags"] = tag,
                    ["type"] = "spawn",
                });
                made++;
            }

            return made;
        }

        /// <summary>Takes every spawn point out of a room.</summary>
        /// <summary>
        /// The spawn point nearest a spot, if one is close enough to have been meant.
        ///
        /// Clicking a map is how somebody says "that one". Without this, spawn points could be
        /// added and the whole lot thrown away, and nothing in between - which is no good when
        /// one of a hundred and eighteen is in the wrong place.
        /// </summary>
        public static (int at, int x, int y, int z)? nearest(Room room, int x, int y, int z,
                                                             double within)
        {
            var regions = room.Regions;
            var best = -1;
            var bestGap = within * within;
            int bx = 0, by = 0, bz = 0;

            for (var at = 0; at < regions.Count; at++)
            {
                var region = regions[at];
                if (region?["type"]?.GetValue<string>() != "spawn") { continue; }
                if (region["pos"] is not JsonArray pos || pos.Count < 3) { continue; }

                var px = pos[0]!.GetValue<int>();
                var py = pos[1]!.GetValue<int>();
                var pz = pos[2]!.GetValue<int>();

                //Height counts for less than the floor plan does. Two spawn points one above the
                //other are rare; a click a few blocks off in x or z is not.
                var gap = (px - x) * (double)(px - x)
                    + (pz - z) * (double)(pz - z)
                    + (py - y) * (double)(py - y) * 0.25;

                if (gap > bestGap) { continue; }

                bestGap = gap;
                best = at;
                bx = px; by = py; bz = pz;
            }

            return best < 0 ? null : (best, bx, by, bz);
        }

        /// <summary>
        /// Puts one spawn point somewhere else, by where it sits in the region list.
        ///
        /// Only the position moves. Radius, tags and whichever mob group the point belongs to are
        /// the reason somebody placed it there in the first place, and dragging it across the room
        /// is not a statement about any of them.
        /// </summary>
        public static bool moveTo(Room room, int at, int x, int y, int z)
        {
            var regions = room.Regions;
            if (at < 0 || at >= regions.Count) { return false; }
            if (regions[at] is not JsonObject region) { return false; }
            if (region["type"]?.GetValue<string>() != "spawn") { return false; }

            //A fresh array rather than three assignments into the old one. A JsonNode already
            //sitting in a document has a parent, and moving its children about is how you get an
            //exception halfway through and a half-moved point.
            region["pos"] = new JsonArray(x, y, z);
            return true;
        }

        /// <summary>Takes one spawn point out, by where it sits in the region list.</summary>
        public static bool removeAt(Room room, int at)
        {
            var regions = room.Regions;
            if (at < 0 || at >= regions.Count) { return false; }
            if (regions[at]?["type"]?.GetValue<string>() != "spawn") { return false; }

            regions.RemoveAt(at);
            return true;
        }

        //--- what the mission asks of you ------------------------------------------------------------

        /// <summary>
        /// One step of the mission's objective chain.
        ///
        /// They are a SEQUENCE, not a set. Creeper Woods asks for one villager, then for you to
        /// reach the caravan, then for five more villagers, then for you to reach the end, and
        /// only then does the exit gate become clickable. A step nobody can finish stops every
        /// step after it - and the symptom is a gate that is drawn, and lit, and does nothing.
        ///
        /// That matters most for a hand-built mission, which inherits whichever chain belonged to
        /// the mission it was installed over. Somebody who replaced Creeper Woods with a city has
        /// no villagers to free and no caravan to find, so the gate at the end of their city can
        /// never be reached.
        /// </summary>
        public sealed class Objective
        {
            public Objective(int at, string description, string kind, int count,
                             string[] needs, bool isExit)
            {
                At = at;
                Description = description;
                Kind = kind;
                Count = count;
                Needs = needs;
                IsExit = isExit;
            }

            public int At { get; }
            public string Description { get; }

            /// <summary>"click" something, or "reach" somewhere.</summary>
            public string Kind { get; }

            public int Count { get; }

            /// <summary>The region names it needs, without the stretch and tile parts.</summary>
            public string[] Needs { get; }

            /// <summary>Whether this is the one that clicks the exit gate.</summary>
            public bool IsExit { get; }

            public override string ToString()
            {
                var what = Count > 1 ? $"{Kind} \u00d7{Count}" : Kind;
                var where = Needs.Length > 0 ? "  \u2192  " + string.Join(", ", Needs) : string.Empty;
                var note = IsExit ? "   \u2190  the way out" : string.Empty;

                return $"{At + 1}. {tidy(Description)}   \u00b7  {what}{where}{note}";
            }

            /// <summary>The game's own string keys, made readable.</summary>
            private static string tidy(string key)
            {
                var said = key.StartsWith("description_", StringComparison.OrdinalIgnoreCase)
                    ? key.Substring("description_".Length)
                    : key;

                return said.Replace('_', ' ');
            }
        }

        /// <summary>The region name out of a "stretch.tile.region" reference.</summary>
        private static string lastPart(string reference)
        {
            var at = reference.LastIndexOf('.');
            return at < 0 ? reference : reference.Substring(at + 1);
        }

        /// <summary>The mission's objective chain, in the order it is asked of you.</summary>
        public static List<Objective> objectivesOf(Map map)
        {
            var made = new List<Objective>();
            var all = map.Level["objectives"] as JsonArray ?? new JsonArray();

            for (var at = 0; at < all.Count; at++)
            {
                if (all[at] is not JsonObject objective) { continue; }

                var click = objective["click"] as JsonObject;
                var gauntlet = objective["gauntlet"] as JsonObject;
                var body = click ?? gauntlet;

                var needs = new List<string>();

                if (body?["locations"] is JsonArray places)
                {
                    foreach (var one in places)
                    {
                        var said = one?.GetValue<string>();
                        if (said != null) { needs.Add(lastPart(said)); }
                    }
                }

                var region = body?["end-region"]?.GetValue<string>();
                if (region != null) { needs.Add(lastPart(region)); }

                made.Add(new Objective(at,
                    objective["description"]?.GetValue<string>() ?? "(no description)",
                    click != null ? "click" : gauntlet != null ? "reach" : "?",
                    body?["count"]?.GetValue<int>() ?? 1,
                    needs.ToArray(),
                    click != null && string.Equals(click["object"]?.GetValue<string>(), EXIT_DOOR,
                        StringComparison.OrdinalIgnoreCase)));
            }

            return made;
        }

        /// <summary>Takes one step out of the chain.</summary>
        public static bool removeObjectiveAt(Map map, int at)
        {
            if (map.Level["objectives"] is not JsonArray all) { return false; }
            if (at < 0 || at >= all.Count) { return false; }

            all.RemoveAt(at);
            return true;
        }

        /// <summary>
        /// Strips the chain down to the exit gate alone.
        ///
        /// What a hand-built mission usually wants: walk in, do whatever the map is for, click
        /// the gate, leave. Everything the old mission asked for refers to things that are no
        /// longer in the map, and each one blocks the steps behind it.
        /// </summary>
        public static int keepOnlyExit(Map map)
        {
            if (map.Level["objectives"] is not JsonArray all) { return 0; }

            var gone = 0;

            for (var at = all.Count - 1; at >= 0; at--)
            {
                if (all[at] is JsonObject objective
                    && objective["click"] is JsonObject click
                    && string.Equals(click["object"]?.GetValue<string>(), EXIT_DOOR,
                        StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                all.RemoveAt(at);
                gone++;
            }

            return gone;
        }

        //--- the way out ---------------------------------------------------------------------------

        /// <summary>What the region marking the exit gate is called, and what it is tagged.</summary>
        public const string EXIT = "exit";
        public const string GATE = "gate";

        /// <summary>The glowing gate the game draws and you click to leave.</summary>
        public const string EXIT_DOOR = "Decor/Prefabs/DoorExit/BP_DoorExit_CW";

        /// <summary>
        /// One way out of the mission.
        ///
        /// Two things have to agree for this to work, which is why leaving it to somebody to hand
        /// craft went wrong:
        ///
        ///   * a REGION named "exit", tagged "gate" - a single cell saying where the gate stands
        ///   * an OBJECTIVE whose click.object is the gate prefab and whose click.locations names
        ///     that region as "stretch.tile.region"
        ///
        /// Either alone does nothing. The region on its own is an unmarked cell; the objective on
        /// its own points at a region that is not there, and the mission simply has no way out -
        /// which is exactly what it looks like in game, with no error anywhere.
        ///
        /// It is NOT a teleport. Teleports are the glowing doors BETWEEN places - side areas,
        /// crypts, the camp's own rooms - and they name a door. This names a region and finishes
        /// the mission.
        /// </summary>
        public sealed class Exit
        {
            public Exit(int at, int[] pos, bool claimed)
            {
                At = at;
                Pos = pos;
                Claimed = claimed;
            }

            public int At { get; }
            public int[] Pos { get; }

            /// <summary>Whether an objective actually points at it.</summary>
            public bool Claimed { get; }

            public override string ToString()
                => $"{Pos[0]}, {Pos[1]}, {Pos[2]}"
                + (Claimed ? "   \u2190  the way out" : "   \u00b7  NO objective points at it");
        }

        private static bool isExit(JsonObject region)
            => region["type"]?.GetValue<string>() == "trigger"
            && string.Equals(region["name"]?.GetValue<string>(), EXIT,
                   StringComparison.OrdinalIgnoreCase);

        /// <summary>Whether the level has an objective that clicks an exit gate.</summary>
        public static bool hasExitObjective(Map map)
        {
            foreach (var one in map.Level["objectives"] as JsonArray ?? new JsonArray())
            {
                if (one is not JsonObject objective) { continue; }
                if (objective["click"] is not JsonObject click) { continue; }

                if (string.Equals(click["object"]?.GetValue<string>(), EXIT_DOOR,
                    StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }

            return false;
        }

        /// <summary>Every exit gate in a room.</summary>
        public static List<Exit> exitsOf(Map map, Room room)
        {
            var made = new List<Exit>();
            var claimed = hasExitObjective(map);
            var regions = room.Regions;

            for (var at = 0; at < regions.Count; at++)
            {
                if (regions[at] is not JsonObject region || !isExit(region)) { continue; }

                made.Add(new Exit(at, ints(region["pos"], 3), claimed));
            }

            return made;
        }

        /// <summary>
        /// Puts a way out somewhere, and makes sure something points at it.
        ///
        /// The objective is added too, because a gate nobody has claimed is the failure this is
        /// here to prevent. The reference is written loose - "*.*.exit" - so it matches whichever
        /// stretch and tile the region ends up in, which is what welding leaves behind anyway.
        /// </summary>
        public static Exit addExit(Map map, Room room, int x, int y, int z)
        {
            room.Regions.Add(new JsonObject
            {
                ["locked"] = false,
                ["name"] = EXIT,
                ["pos"] = new JsonArray(x, y, z),
                ["size"] = new JsonArray(1, 1, 1),
                ["tags"] = GATE,
                ["type"] = "trigger",
            });

            if (!hasExitObjective(map))
            {
                if (map.Level["objectives"] is not JsonArray objectives)
                {
                    objectives = new JsonArray();
                    map.Level["objectives"] = objectives;
                }

                objectives.Add(new JsonObject
                {
                    ["name"] = "name_the_escape",
                    ["description"] = "description_exit_through_the_gate",
                    ["displayMode"] = "MainObjective",
                    ["click"] = new JsonObject
                    {
                        ["object"] = EXIT_DOOR,
                        ["count"] = 1,
                        ["locations"] = new JsonArray("*.*." + EXIT),
                    },
                });
            }

            return new Exit(room.Regions.Count - 1, new[] { x, y, z }, true);
        }

        /// <summary>Puts the exit gate somewhere else.</summary>
        public static bool moveExit(Room room, int at, int x, int y, int z)
        {
            var regions = room.Regions;
            if (at < 0 || at >= regions.Count) { return false; }
            if (regions[at] is not JsonObject region || !isExit(region)) { return false; }

            region["pos"] = new JsonArray(x, y, z);
            return true;
        }

        /// <summary>Takes one exit gate out. The objective is left alone - it may claim another.</summary>
        public static bool removeExitAt(Room room, int at)
        {
            var regions = room.Regions;
            if (at < 0 || at >= regions.Count) { return false; }
            if (regions[at] is not JsonObject region || !isExit(region)) { return false; }

            regions.RemoveAt(at);
            return true;
        }

        //--- where you come in -------------------------------------------------------------------

        /// <summary>The tag and name the game marks the player's arrival area with.</summary>
        public const string PLAYERSTART = "playerstart";

        /// <summary>
        /// One place the mission puts you when it starts.
        ///
        /// Not a door, which is what it looks like from the outside and is worth saying plainly:
        /// a door is how two tiles join and what a teleport attaches to, and it is perfectly
        /// possible to have several and still materialise nowhere near any of them. Arriving is a
        /// trigger region called "playerstart" - an AREA you appear in, three to six cells across.
        /// </summary>
        public sealed class Start
        {
            public Start(int at, int[] pos, int[] size, bool isMain)
            {
                At = at;
                Pos = pos;
                Size = size;
                IsMain = isMain;
            }

            /// <summary>
            /// Whether this is the one the mission starts you at.
            ///
            /// A mission can have several arrival areas and they are identical - same name, same
            /// tags, same type - so nothing in the region itself says which is which. What says
            /// it is ORDER: welding appends each room's regions in playing order, so the start
            /// room's land at the front, and the first one in the array is the way in. The rest
            /// are where teleports drop you.
            ///
            /// Checked against Creeper Woods: of its two, the one at index 1 of 220 comes from
            /// cw_start_a001 - the tile the first stretch plays - and the one at index 148 comes
            /// from cw_obj_alt, which is reached by teleport.
            /// </summary>
            public bool IsMain { get; }

            /// <summary>Where it sits in the room's region list, which is how it is removed.</summary>
            public int At { get; }

            public int[] Pos { get; }
            public int[] Size { get; }

            public override string ToString()
            {
                var note = IsMain ? "   \u2190  the main way in" : "   \u00b7  teleport arrival";
                return $"{Pos[0]}, {Pos[1]}, {Pos[2]}   \u00b7  {Size[0]}\u00d7{Size[2]} area{note}";
            }
        }

        /// <summary>Whether a region is the player's arrival area.</summary>
        private static bool isStart(JsonObject region)
        {
            if (region["type"]?.GetValue<string>() != "trigger") { return false; }

            //Matched on either, because the game's own data sets both and a region carrying only
            //one of them is still plainly meant to be the same thing.
            return string.Equals(region["tags"]?.GetValue<string>(), PLAYERSTART,
                       StringComparison.OrdinalIgnoreCase)
                || string.Equals(region["name"]?.GetValue<string>(), PLAYERSTART,
                       StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>Every place this room can put you when the mission starts.</summary>
        public static List<Start> startsOf(Room room)
        {
            var made = new List<Start>();
            var regions = room.Regions;

            for (var at = 0; at < regions.Count; at++)
            {
                if (regions[at] is not JsonObject region || !isStart(region)) { continue; }

                //The first one found is the main way in, because that is what order means here.
                made.Add(new Start(at, ints(region["pos"], 3), ints(region["size"], 3),
                    made.Count == 0));
            }

            return made;
        }

        /// <summary>
        /// Puts the player's arrival area somewhere.
        ///
        /// Three by three, which is at the small end of what the game ships - they run from 3x3
        /// to 5x6 - and small is the safer default: an arrival area that overlaps a wall is worse
        /// than one that is snug.
        /// </summary>
        public static Start addStart(Room room, int x, int y, int z)
        {
            var region = new JsonObject
            {
                ["locked"] = false,
                ["name"] = PLAYERSTART,
                ["pos"] = new JsonArray(x, y, z),
                ["size"] = new JsonArray(3, 1, 3),
                ["tags"] = PLAYERSTART,
                ["type"] = "trigger",
            };

            room.Regions.Add(region);

            //Appended, so it is the main way in only when it is the first. A second one added to
            //a mission that already has one is a teleport arrival until somebody promotes it.
            var main = startsOf(room).Count == 1;

            return new Start(room.Regions.Count - 1, new[] { x, y, z }, new[] { 3, 1, 3 }, main);
        }

        /// <summary>
        /// Makes one arrival area the main way in, by moving it in front of the others.
        ///
        /// Order is the only thing that distinguishes them, so promoting one is literally moving
        /// it up the array - there is no flag to set. The node is copied rather than moved
        /// because a JsonNode already in a document has a parent, and re-inserting the same
        /// instance throws halfway through.
        /// </summary>
        public static bool promoteStart(Room room, int at)
        {
            var regions = room.Regions;
            if (at < 0 || at >= regions.Count) { return false; }
            if (regions[at] is not JsonObject region || !isStart(region)) { return false; }

            var first = -1;
            for (var i = 0; i < regions.Count; i++)
            {
                if (regions[i] is JsonObject found && isStart(found)) { first = i; break; }
            }

            //Already at the front, so there is nothing to do and saying so is better than
            //rewriting the file to produce an identical one.
            if (first < 0 || first == at) { return false; }

            if (JsonNode.Parse(region.ToJsonString()) is not JsonObject copy) { return false; }

            //Taken out first. `at` is always after `first`, so removing it cannot shift `first`.
            regions.RemoveAt(at);
            regions.Insert(first, copy);
            return true;
        }

        /// <summary>Puts the arrival area somewhere else, by where it sits in the region list.</summary>
        public static bool moveStart(Room room, int at, int x, int y, int z)
        {
            var regions = room.Regions;
            if (at < 0 || at >= regions.Count) { return false; }
            if (regions[at] is not JsonObject region || !isStart(region)) { return false; }

            region["pos"] = new JsonArray(x, y, z);
            return true;
        }

        /// <summary>Takes one arrival area out, by where it sits in the region list.</summary>
        public static bool removeStartAt(Room room, int at)
        {
            var regions = room.Regions;
            if (at < 0 || at >= regions.Count) { return false; }
            if (regions[at] is not JsonObject region || !isStart(region)) { return false; }

            regions.RemoveAt(at);
            return true;
        }

        //--- the doors themselves ----------------------------------------------------------------

        /// <summary>
        /// One door in a tile's wall.
        ///
        /// A door is the same four fields a region is - name, position, size and tags - just kept
        /// in a different array. What makes it a door is what names it: the tile's entry-door
        /// field, or a teleport, both of which refer to a door BY NAME rather than by position.
        /// </summary>
        public sealed class Door
        {
            public Door(int at, string name, int[] pos, int[] size, string tags, bool isEntry,
                        bool onWall)
            {
                At = at;
                Name = name;
                Pos = pos;
                Size = size;
                Tags = tags;
                IsEntry = isEntry;
                OnWall = onWall;
            }

            /// <summary>Where it sits in the tile's door list, which is how it is removed.</summary>
            public int At { get; }

            public string Name { get; }
            public int[] Pos { get; }
            public int[] Size { get; }
            public string Tags { get; }

            /// <summary>Whether the level names this one as the way in.</summary>
            public bool IsEntry { get; }

            /// <summary>
            /// Whether it sits in the tile's outer wall.
            ///
            /// The same test welding uses to decide which doors to carry across, so a door that
            /// is not on a wall is one a later weld would throw away - and one nobody can walk in
            /// through, because there is no outside next to it.
            /// </summary>
            public bool OnWall { get; }

            /// <summary>Which way it faces, worked out from which axis it is wide along.</summary>
            public string Facing => Size[0] >= Size[2] ? "along x" : "along z";

            public override string ToString()
            {
                var called = Name.Length > 0 ? Name : "(unnamed)";
                var where = $"{Pos[0]}, {Pos[1]}, {Pos[2]}";
                var note = IsEntry ? "   \u2190  the way in"
                    : Tags.Length > 0 ? $"   \u00b7  {Tags}" : string.Empty;

                //The facing is on the row because it is the thing that is easy to get wrong and
                //impossible to see otherwise: a door lying along the wrong axis is buried in the
                //wall it was meant to be a hole in.
                var wall = OnWall ? string.Empty : "   \u00b7  NOT in a wall";

                return $"{called}   \u2014   {where}   \u00b7  {Facing}{wall}{note}";
            }
        }

        /// <summary>The name of the door the level starts you at, if it names one.</summary>
        public static string entryDoorOf(Map map, Room room)
        {
            foreach (var declared in map.Level["tiles"] as JsonArray ?? new JsonArray())
            {
                if (declared is not JsonObject tile) { continue; }
                if (!string.Equals(tile["id"]?.GetValue<string>(), room.Id,
                    StringComparison.OrdinalIgnoreCase)) { continue; }

                return tile["entry-door"]?.GetValue<string>() ?? string.Empty;
            }

            return string.Empty;
        }

        /// <summary>Every door in a room's own wall.</summary>
        public static List<Door> doorsOf(Map map, Room room)
        {
            var made = new List<Door>();
            var entry = entryDoorOf(map, room);

            var doors = room.Tile["doors"] as JsonArray ?? new JsonArray();

            for (var at = 0; at < doors.Count; at++)
            {
                if (doors[at] is not JsonObject door) { continue; }

                var name = door["name"]?.GetValue<string>() ?? string.Empty;

                var pos = ints(door["pos"], 3);

                made.Add(new Door(at, name, pos,
                    ints(door["size"], 3),
                    door["tags"]?.GetValue<string>() ?? string.Empty,
                    name.Length > 0 && string.Equals(name, entry, StringComparison.OrdinalIgnoreCase),
                    onWall(room, pos)));
            }

            return made;
        }

        /// <summary>
        /// Which way a door in this spot has to lie.
        ///
        /// A door is three cells along one axis and one along the other, and which axis follows
        /// from the wall: in an x wall it spans z, and the other way about. Checked against every
        /// door the game ships - 622 of the 622 that sit in an outer wall agree with this.
        /// </summary>
        private static JsonArray facing(Room room, int x, int z)
        {
            var nearestX = Math.Min(x, Math.Max(0, room.Size[0] - 1 - x));
            var nearestZ = Math.Min(z, Math.Max(0, room.Size[2] - 1 - z));

            return nearestX <= nearestZ ? new JsonArray(1, 1, 3) : new JsonArray(3, 1, 1);
        }

        /// <summary>
        /// Whether a spot is in the tile's outer wall - the same test welding applies.
        /// </summary>
        private static bool onWall(Room room, int[] pos)
            => pos[0] == 0 || pos[0] >= room.Size[0] - 1
            || pos[2] == 0 || pos[2] >= room.Size[2] - 1;

        /// <summary>
        /// Puts a door in the wall.
        ///
        /// The size is not asked for. Every door in the game is three cells wide along one axis
        /// and one along the other, and which axis is decided by the wall it is in - so it is
        /// taken from whichever edge of the tile the spot is nearest rather than left to somebody
        /// to get right. A door lying along the wrong axis is a door buried in a wall.
        /// </summary>
        public static Door addDoor(Map map, Room room, string name, int x, int y, int z)
        {
            if (room.Tile["doors"] is not JsonArray doors)
            {
                doors = new JsonArray();
                room.Tile["doors"] = doors;
            }

            var made = new JsonObject
            {
                ["name"] = name,
                ["pos"] = new JsonArray(x, y, z),
                ["size"] = facing(room, x, z),
                ["tags"] = string.Empty,
            };

            doors.Add(made);

            return doorsOf(map, room)[doors.Count - 1];
        }

        /// <summary>
        /// Puts a door somewhere else.
        ///
        /// The size is recomputed rather than carried along, because a door's facing belongs to
        /// the wall it is in, not to the door. Dragging one from a north wall to an east wall
        /// without turning it leaves it lying across the opening instead of filling it.
        /// </summary>
        public static bool moveDoor(Room room, int at, int x, int y, int z)
        {
            if (room.Tile["doors"] is not JsonArray doors) { return false; }
            if (at < 0 || at >= doors.Count) { return false; }
            if (doors[at] is not JsonObject door) { return false; }

            door["pos"] = new JsonArray(x, y, z);
            door["size"] = facing(room, x, z);
            return true;
        }

        /// <summary>Takes a door out, by where it sits in the tile's door list.</summary>
        public static bool removeDoorAt(Room room, int at)
        {
            if (room.Tile["doors"] is not JsonArray doors) { return false; }
            if (at < 0 || at >= doors.Count) { return false; }

            doors.RemoveAt(at);
            return true;
        }

        /// <summary>The door nearest a spot, if one is close enough to have been meant.</summary>
        public static Door? nearestDoor(Map map, Room room, int x, int y, int z, double within)
        {
            Door? best = null;
            var bestGap = within * within;

            foreach (var door in doorsOf(map, room))
            {
                var dx = (double)(door.Pos[0] - x);
                var dy = (double)(door.Pos[1] - y);
                var dz = (double)(door.Pos[2] - z);

                var gap = dx * dx + dz * dz + dy * dy * 0.25;
                if (gap > bestGap) { continue; }

                bestGap = gap;
                best = door;
            }

            return best;
        }

        /// <summary>
        /// Says which door the level starts you at.
        ///
        /// Written onto the level's own tile table rather than onto the door, because that is
        /// where the game looks - a door called "enter" is a convention, and a convention is not
        /// something to rely on for a tile nobody at Mojang ever saw. Welding drops the field, so
        /// a welded mission has nothing saying where to come in until this puts it back.
        /// </summary>
        public static bool setEntryDoor(Map map, Room room, string name)
        {
            if (map.Level["tiles"] is not JsonArray tiles)
            {
                tiles = new JsonArray();
                map.Level["tiles"] = tiles;
            }

            foreach (var declared in tiles)
            {
                if (declared is not JsonObject tile) { continue; }
                if (!string.Equals(tile["id"]?.GetValue<string>(), room.Id,
                    StringComparison.OrdinalIgnoreCase)) { continue; }

                tile["entry-door"] = name;
                return true;
            }

            //A tile named by a stretch but missing from the table is a tile the game looks up and
            //does not find, so the row is made rather than the setting being dropped.
            tiles.Add(new JsonObject
            {
                ["id"] = room.Id,
                ["rotations"] = 0,
                ["entry-door"] = name,
            });

            return true;
        }

        /// <summary>One way in or out of a room, and where it goes.</summary>
        public sealed class Teleport
        {
            public Teleport(string door, string? exit, string dungeons, int[] at, JsonObject node)
            {
                Door = door;
                Exit = exit;
                Dungeons = dungeons;
                At = at;
                Node = node;
            }

            /// <summary>The door it is attached to, by name.</summary>
            public string Door { get; }

            /// <summary>
            /// Where it comes out, as dungeon.tile.door, or nothing.
            /// </summary>
            public string? Exit { get; }

            /// <summary>Which sub-areas it leads into, comma separated, or empty.</summary>
            public string Dungeons { get; }

            /// <summary>Where the door sits in the room.</summary>
            public int[] At { get; }

            /// <summary>The entry itself, so an edit reaches the file.</summary>
            public JsonObject Node { get; }

            /// <summary>
            /// Whether this is a way OUT rather than a place you arrive.
            ///
            /// A teleport naming an exit takes you somewhere. One without is the other end - the
            /// door you step out of on the way back.
            /// </summary>
            public bool Leaves => !string.IsNullOrEmpty(Exit);

            public override string ToString() => Leaves
                ? $"{Door}  →  {Exit}"
                : $"{Door}  (arrival)";
        }

        /// <summary>
        /// The ways in and out of a room.
        ///
        /// A teleport is not a block and not a region. It lives in the LEVEL file, on the tile's
        /// declaration, and names a DOOR of that tile - so it can only be drawn by finding the
        /// door it points at and borrowing its position. That is also why none of this survives
        /// welding: the doors between rooms are dropped when the rooms become one room, and a
        /// teleport naming one is left pointing at nothing.
        /// </summary>
        public static List<Teleport> teleportsOf(Map map, Room room)
        {
            var made = new List<Teleport>();

            //Where the doors are, by name.
            var doors = new Dictionary<string, int[]>(StringComparer.OrdinalIgnoreCase);
            foreach (var one in room.Tile["doors"] as JsonArray ?? new JsonArray())
            {
                if (one is not JsonObject door) { continue; }
                var name = door["name"]?.GetValue<string>();
                if (string.IsNullOrEmpty(name)) { continue; }
                doors[name!] = ints(door["pos"], 3);
            }

            foreach (var declared in map.Level["tiles"] as JsonArray ?? new JsonArray())
            {
                if (declared is not JsonObject tile) { continue; }
                if (!string.Equals(tile["id"]?.GetValue<string>(), room.Id,
                    StringComparison.OrdinalIgnoreCase)) { continue; }

                foreach (var one in tile["teleports"] as JsonArray ?? new JsonArray())
                {
                    if (one is not JsonObject port) { continue; }

                    var door = port["door"]?.GetValue<string>() ?? string.Empty;
                    if (!doors.TryGetValue(door, out var at))
                    {
                        //A teleport whose door is gone. Worth keeping in the list - it is exactly
                        //the thing somebody needs to see - but it has nowhere to stand.
                        at = new[] { -1, -1, -1 };
                    }

                    var into = string.Join(", ", (port["dungeons"] as JsonArray ?? new JsonArray())
                        .Select(two => two?.GetValue<string>())
                        .Where(two => !string.IsNullOrEmpty(two)));

                    made.Add(new Teleport(door, port["exit"]?.GetValue<string>(), into, at, port));
                }
            }

            return made;
        }

        /// <summary>
        /// Adds a mob group, and makes it one the mission actually uses.
        ///
        /// Both halves matter. A group nobody refers to spawns nothing, which is the trap the
        /// list already warns about - so a new one goes into default-mobs at the same time, and
        /// starts roaming the level straight away.
        ///
        /// This is what the camp needs. It ships with no mob groups and nothing set to roam,
        /// because nothing is supposed to spawn there; putting spawn points in it does nothing at
        /// all until something exists for them to draw from.
        /// </summary>
        public static JsonObject addGroup(Map map, string mob = "zombie")
        {
            if (map.Level["mob-groups"] is not JsonArray groups)
            {
                groups = new JsonArray();
                map.Level["mob-groups"] = groups;
            }

            var taken = new HashSet<string>(groups
                .OfType<JsonObject>()
                .Select(one => one["id"]?.GetValue<string>() ?? string.Empty),
                StringComparer.OrdinalIgnoreCase);

            var id = "custom";
            for (var at = 1; taken.Contains(id); at++) { id = $"custom-{at}"; }

            var made = new JsonObject
            {
                ["id"] = id,
                ["types"] = new JsonArray(new JsonObject { ["type"] = mob }),
            };

            groups.Add(made);

            //And referenced, so it is not born unused.
            if (map.Level["default-mobs"] is not JsonObject roaming)
            {
                roaming = new JsonObject { ["density"] = 1 };
                map.Level["default-mobs"] = roaming;
            }

            if (roaming["only"] is not JsonArray only)
            {
                only = new JsonArray();
                roaming["only"] = only;
            }

            only.Add(new JsonObject { ["id"] = id, ["weight"] = 1 });

            map.Changed.Add("level.json");
            return made;
        }

        /// <summary>Where a mob group is actually used, and how much.</summary>
        public readonly struct Use
        {
            public Use(double roaming, int waves) { Roaming = roaming; Waves = waves; }

            /// <summary>Its weight in default-mobs, which is what roams the level.</summary>
            public double Roaming { get; }

            /// <summary>How many arena waves call for it.</summary>
            public int Waves { get; }

            public bool Any => Roaming > 0 || Waves > 0;
        }

        /// <summary>
        /// Which mob groups the mission actually uses, and for what.
        ///
        /// This is the difference between an edit that does something and an edit that does
        /// nothing, and the file gives no hint of it. Creeper Woods ships twenty-three groups.
        /// What roams the level is the handful listed in "default-mobs", each with a weight. The
        /// rest are arena groups, named by a trigger's "waves" - Deepwood Brook's endersent boss
        /// is one - and they fire only when you walk into that fight.
        ///
        /// So changing "enderboss" and then wandering the level shows you nothing: the mobs out
        /// there came from default-mobs the whole time, and the boss is waiting in its arena.
        /// </summary>
        public static Dictionary<string, Use> usage(JsonObject level)
        {
            var roaming = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
            var waves = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

            //What roams. Entries are either a bare name or an object with a weight beside it.
            if (level["default-mobs"]?["only"] is JsonArray only)
            {
                foreach (var one in only)
                {
                    string? id = null;
                    double weight = 1;

                    if (one is JsonValue value && value.TryGetValue<string>(out var bare))
                    {
                        id = bare;
                    }
                    else if (one is JsonObject body)
                    {
                        id = body["id"]?.GetValue<string>();
                        if (body["weight"] is JsonValue got && got.TryGetValue<double>(out var w))
                        {
                            weight = w;
                        }
                    }

                    if (id == null) { continue; }
                    roaming[id] = roaming.TryGetValue(id, out var was) ? was + weight : weight;
                }
            }

            //What the arenas call for. A wave is [count, "group"], and they are scattered through
            //the objects rather than kept in one place, so the whole tree is walked.
            void walk(JsonNode? node)
            {
                if (node is JsonArray list)
                {
                    foreach (var one in list) { walk(one); }
                    return;
                }

                if (node is not JsonObject body) { return; }

                if (body["waves"] is JsonArray theirs)
                {
                    foreach (var wave in theirs)
                    {
                        if (wave is not JsonArray pair) { continue; }
                        foreach (var part in pair)
                        {
                            if (part is not JsonValue value
                                || !value.TryGetValue<string>(out var id)
                                || id.Length == 0) { continue; }
                            waves[id] = waves.TryGetValue(id, out var was) ? was + 1 : 1;
                        }
                    }
                }

                foreach (var one in body) { walk(one.Value); }
            }

            walk(level);

            var made = new Dictionary<string, Use>(StringComparer.OrdinalIgnoreCase);
            foreach (var id in roaming.Keys.Concat(waves.Keys).Distinct(StringComparer.OrdinalIgnoreCase))
            {
                made[id] = new Use(
                    roaming.TryGetValue(id, out var w) ? w : 0,
                    waves.TryGetValue(id, out var n) ? n : 0);
            }

            return made;
        }

        public static int clear(Room room)
        {
            var regions = room.Regions;
            var gone = 0;

            for (var at = regions.Count - 1; at >= 0; at--)
            {
                if (regions[at]?["type"]?.GetValue<string>() != "spawn") { continue; }
                regions.RemoveAt(at);
                gone++;
            }

            return gone;
        }

        /// <summary>
        /// Writes the changed files back where they came from.
        ///
        /// In place, because the whole point of doing this here rather than in a browser is that
        /// the app already owns these files - no downloads, no putting things back by hand.
        /// </summary>
        public static int save(Map map)
        {
            var written = 0;
            var options = new JsonSerializerOptions { WriteIndented = true };

            foreach (var path in map.Changed)
            {
                var full = Path.Combine(map.Folder, path.Replace('/', Path.DirectorySeparatorChar));

                JsonObject? document = path.Equals("level.json", StringComparison.OrdinalIgnoreCase)
                    ? map.Level
                    : map.Groups.TryGetValue(path, out var found) ? found : null;

                //Mob groups belong to the mission that gets rebuilt, not only to the copy that
                //is installed - so when a pre-weld level is kept beside this one, it gets them
                //too. Otherwise the next weld would quietly throw them away.
                if (document == map.Level && File.Exists(full + ".multitile"))
                {
                    File.WriteAllText(full + ".multitile", document.ToJsonString(options),
                        new UTF8Encoding(false));
                }

                if (document == null) { continue; }

                //Kept once, the first time a file is touched, so the original is always a copy
                //away rather than gone.
                var backup = full + ".before";
                if (File.Exists(full) && !File.Exists(backup)) { File.Copy(full, backup); }

                File.WriteAllText(full, document.ToJsonString(options), new UTF8Encoding(false));
                written++;
            }

            map.Changed.Clear();
            return written;
        }
    }
}
