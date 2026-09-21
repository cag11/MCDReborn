using MCDSaveEdit.Services;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
#nullable enable

namespace MCDSaveEdit.Logic
{
    /// <summary>
    /// Taking a mission out of the game to work on, and putting one back.
    ///
    /// Both directions are file moves rather than conversions, which is the whole reason this is
    /// possible: a mission is JSON and compressed block arrays, and a mod pak carrying the same
    /// paths as the game's own wins over them. Nothing in the game's files is touched, and
    /// removing the pak puts the original mission back exactly.
    ///
    /// The folder written by export is the folder import expects, so the round trip needs no
    /// bookkeeping from whoever is using it:
    ///
    ///     &lt;folder&gt;/map.json                               which mission this came from
    ///     &lt;folder&gt;/level.json                             the mission itself
    ///     &lt;folder&gt;/objectgroups/&lt;Name&gt;/objectgroup.json   the tiles it is built from
    ///     &lt;folder&gt;/resourcepacks/&lt;Name&gt;/blocks.json       what its block ids mean
    /// </summary>
    public static class MapMod
    {
        /// <summary>What a map mod is called, so it can be found and removed on its own.</summary>
        private const string PREFIX = "Map_";

        /// <summary>The note export leaves behind, so import knows what it is looking at.</summary>
        public const string MARKER = "map.json";

        /// <summary>
        /// The real file name, extension and all.
        ///
        /// Worth stating plainly because the app never sees it when reading. PakReader's index
        /// merge drops any entry whose name has no dot and strips the extension off the rest, so
        /// the game's "data/lovika/levels/creeperwoods.json" is spelled "…/creeperwoods"
        /// everywhere the index is asked. Reading through the index therefore works with the
        /// short name and WRITING does not: a pak built with the short names contains files the
        /// game will never look for, and reads back as empty.
        /// </summary>
        private const string LEVEL_FILE = "level.json";
        private const string GROUPS_FOLDER = "objectgroups";
        private const string PACKS_FOLDER = "resourcepacks";

        /// <summary>
        /// Takes the side-paths out of a level that has been welded into one tile.
        ///
        /// A mission's tiles each declare their `teleports`: the doors that lead off to a crypt,
        /// an inn, a side cave. Welding merges every room into a single tile and that tile ends
        /// up holding ALL of their declarations at once - Creeper Woods arrives with sixty-odd,
        /// most of them `door: travel`.
        ///
        /// The generator allows one travel entry door per tile, so it refuses the level outright:
        ///
        ///     For teleport def in tile id: mcdcustom01_whole.
        ///     Multiple(2) teleport entry doors found: travel
        ///
        /// Dropping them is right rather than merely expedient. The places those doors led to are
        /// not in the map any more - welding is what removed them - so every one of those entries
        /// points at somewhere that no longer exists. A merged map is one room; it has nowhere to
        /// travel to.
        ///
        /// Only tiles the level actually uses are touched, and only when they are over the limit,
        /// so a map whose tiles are still separate is left exactly as it was.
        /// </summary>
        /// <summary>
        /// Makes every mob in every group the shape the game reads.
        ///
        /// A group's `types` holds objects - {"type":"husk","weight":0.1} - in every level the
        /// game ships and in every level that works. The spawns editor wrote the first mob that
        /// way and appended the rest as bare strings, so a group came out as
        /// [{"type":"zombie"}, "skeleton"]. It displayed correctly, saved without complaint, and
        /// spawned nothing at all.
        ///
        /// Repaired here rather than only at the point it is written, because maps already made
        /// carry it, and nobody is going to know to rebuild their groups by hand.
        /// </summary>
        public static int tidyMobGroups(string folder)
        {
            var path = Path.Combine(folder, "level.json");
            if (!File.Exists(path)) { return 0; }

            try
            {
                if (JsonNode.Parse(GameMaps.stripComments(File.ReadAllText(path)),
                    documentOptions: new JsonDocumentOptions
                    {
                        AllowTrailingCommas = true,
                        CommentHandling = JsonCommentHandling.Skip,
                    }) is not JsonObject level)
                {
                    return 0;
                }

                var fixed_ = 0;

                foreach (var group in level["mob-groups"] as JsonArray ?? new JsonArray())
                {
                    if (group?["types"] is not JsonArray types) { continue; }

                    for (var at = 0; at < types.Count; at++)
                    {
                        //Only a bare name is rewritten. An object already says what it means,
                        //weights and difficulty gates included, and rebuilding it would throw
                        //those away.
                        if (types[at] is JsonObject) { continue; }

                        var said = types[at]?.GetValue<string>();
                        if (string.IsNullOrEmpty(said)) { continue; }

                        types[at] = new JsonObject { ["type"] = said };
                        fixed_++;
                    }
                }

                if (fixed_ == 0) { return 0; }

                File.WriteAllText(path, level.ToJsonString(
                    new JsonSerializerOptions { WriteIndented = true }));

                Console.WriteLine($"[map] rewrote {fixed_} mob(s) into the shape the game reads");
                return fixed_;
            }
            catch (Exception)
            {
                return 0;
            }
        }

        /// <summary>
        /// Makes sure the stretches actually ask for the mobs the level declares.
        ///
        /// A stretch says what roams it - "mobs": {"only": ["early-group"]} - and that is what
        /// the generator reads. Declaring a group and naming it in "default-mobs" is not enough
        /// on its own: the working maps in this game put the group on the STRETCH, and the one
        /// that spawned nothing had a perfectly good group, a perfectly good default-mobs entry,
        /// and no stretch asking for anything.
        ///
        /// Only ever fills in a level where NO stretch asks for mobs. A map that already wires
        /// its own - a mission, or somebody else's mod - is left exactly as it is, because
        /// choosing which group roams which stretch is the author's business and there is no way
        /// to guess it from here.
        /// </summary>
        public static int wireMobs(string folder)
        {
            var path = Path.Combine(folder, "level.json");
            if (!File.Exists(path)) { return 0; }

            try
            {
                if (JsonNode.Parse(GameMaps.stripComments(File.ReadAllText(path)),
                    documentOptions: new JsonDocumentOptions
                    {
                        AllowTrailingCommas = true,
                        CommentHandling = JsonCommentHandling.Skip,
                    }) is not JsonObject level)
                {
                    return 0;
                }

                var groups = (level["mob-groups"] as JsonArray ?? new JsonArray())
                    .Select(one => one?["id"]?.GetValue<string>())
                    .Where(one => !string.IsNullOrEmpty(one))
                    .Select(one => one!)
                    .ToList();

                if (groups.Count == 0) { return 0; }

                var stretches = level["stretches"] as JsonArray;
                if (stretches == null || stretches.Count == 0) { return 0; }

                //Somebody has already said what roams where. Leave it alone.
                if (stretches.Any(one => one?["mobs"] != null)) { return 0; }

                var wired = 0;

                foreach (var stretch in stretches)
                {
                    if (stretch is not JsonObject one) { continue; }

                    var only = new JsonArray();
                    foreach (var id in groups) { only.Add(JsonValue.Create(id)); }

                    one["mobs"] = new JsonObject { ["only"] = only };
                    wired++;
                }

                if (wired == 0) { return 0; }

                File.WriteAllText(path, level.ToJsonString(
                    new JsonSerializerOptions { WriteIndented = true }));

                Console.WriteLine($"[map] {wired} stretch(es) now ask for "
                    + $"{string.Join(", ", groups)}");

                return wired;
            }
            catch (Exception)
            {
                return 0;
            }
        }

        public static int dropSidePaths(string folder)
        {
            var path = Path.Combine(folder, "level.json");
            if (!File.Exists(path)) { return 0; }

            try
            {
                if (JsonNode.Parse(GameMaps.stripComments(File.ReadAllText(path)),
                    documentOptions: new JsonDocumentOptions
                    {
                        AllowTrailingCommas = true,
                        CommentHandling = JsonCommentHandling.Skip,
                    }) is not JsonObject level)
                {
                    return 0;
                }

                //Which tiles the level really uses. A mission's tile list is a library and most
                //of it is never referenced; rewriting all of it would change maps nobody asked
                //about.
                var used = new HashSet<string>(StringComparer.Ordinal);

                foreach (var stretch in level["stretches"] as JsonArray ?? new JsonArray())
                {
                    foreach (var tile in stretch?["tiles"] as JsonArray ?? new JsonArray())
                    {
                        var said = tile?.GetValue<string>();
                        if (said != null) { used.Add(said); }
                    }
                }

                var stripped = 0;

                foreach (var tile in level["tiles"] as JsonArray ?? new JsonArray())
                {
                    if (tile is not JsonObject one) { continue; }

                    var id = one["id"]?.GetValue<string>();
                    if (id == null || !used.Contains(id)) { continue; }

                    if (one["teleports"] is not JsonArray doors) { continue; }

                    //One entry door of a kind is what the generator allows, so anything with a
                    //repeat is what it would refuse.
                    var travels = doors.Count(door =>
                        string.Equals(door?["door"]?.GetValue<string>(), "travel",
                            StringComparison.Ordinal));

                    if (travels < 2) { continue; }

                    one.Remove("teleports");
                    stripped++;
                }

                if (stripped == 0) { return 0; }

                File.WriteAllText(path, level.ToJsonString(
                    new JsonSerializerOptions { WriteIndented = true }));

                return stripped;
            }
            catch (Exception)
            {
                return 0;
            }
        }

        /// <summary>
        /// Which block theme a map in a working folder is drawn with, or null.
        ///
        /// A level names its packs in PLAYING ORDER and later ones win, which is how a mission
        /// re-skins a handful of blocks without restating the other three hundred. The last one
        /// is therefore the one that decides how the map looks, and the one worth showing.
        /// </summary>
        public static string? themeOf(string folder)
        {
            try
            {
                var level = Path.Combine(folder, "level.json");
                if (!File.Exists(level)) { return null; }

                var parsed = JsonNode.Parse(
                    GameMaps.stripComments(File.ReadAllText(level)),
                    documentOptions: new JsonDocumentOptions
                    {
                        AllowTrailingCommas = true,
                        CommentHandling = JsonCommentHandling.Skip,
                    }) as JsonObject;

                var packs = parsed?["resource-packs"] as JsonArray;

                return packs == null || packs.Count == 0
                    ? null
                    : packs[packs.Count - 1]?.GetValue<string>();
            }
            catch (Exception)
            {
                return null;
            }
        }

        /// <summary>
        /// What a map's packs were before this app ever changed them.
        ///
        /// Written once, the first time a theme is applied, and read for ever after. The current
        /// list cannot answer this: by the second theme it is "the originals plus whatever was
        /// chosen last", and treating that as the base would accumulate every theme ever tried.
        /// </summary>
        private static List<string> basePacks(string folder, JsonObject level)
        {
            var kept = Path.Combine(folder, ".original-packs.json");

            try
            {
                if (File.Exists(kept))
                {
                    var read = JsonNode.Parse(File.ReadAllText(kept)) as JsonArray;

                    if (read != null)
                    {
                        return read.Select(one => one?.GetValue<string>())
                            .Where(one => !string.IsNullOrEmpty(one))
                            .Select(one => one!)
                            .ToList();
                    }
                }
            }
            catch (Exception)
            {
                //An unreadable note is the same as none: fall through and take what is there now.
            }

            var now = (level["resource-packs"] as JsonArray)?
                .Select(one => one?.GetValue<string>())
                .Where(one => !string.IsNullOrEmpty(one))
                .Select(one => one!)
                .ToList() ?? new List<string>();

            try { File.WriteAllText(kept, new JsonArray(now.Select(one =>
                (JsonNode?)JsonValue.Create(one)).ToArray()).ToJsonString()); }
            catch (Exception) { }

            return now;
        }

        /// <summary>
        /// Draws a map with a different theme.
        ///
        /// Only the level file changes - one string - because a map stores its blocks by name
        /// and a theme only says what those names look like. Nothing is rebuilt, no geometry
        /// moves, and switching back is the same edit in reverse.
        ///
        /// The theme REPLACES the list rather than being added to it. Every mission the game
        /// ships names exactly one pack - Creeper Woods "CreeperWoods", Soggy Swamp "SoggySwamp",
        /// Dingy Jungle "DingyJungle" - so a list of two is a shape the game never produces, and
        /// what it produced here was a map that kept its old skin: the base is listed first and
        /// the base is what drew.
        ///
        /// This was written the other way round first - the theme appended to the list rather
        /// than replacing it - because a map crashed around the time a theme was first swapped
        /// in, and the swap was blamed for it. That was wrong twice over. The crash was the
        /// level's own "id" naming a level the game does not have, which is fixed elsewhere and
        /// had nothing to do with packs; and the layering that was supposed to have cured it
        /// cured nothing, because a base pack listed first is the one that draws. What it
        /// actually did was make every theme a no-op, which reads exactly like a theme that does
        /// not work.
        ///
        /// A swapped-in pack has since been played and is fine. The real thing to know about
        /// packs is that they differ in COVERAGE - "dingyjungle" defines 375 block names against
        /// Creeper Woods' 376, "jungle" defines 210 - and a pack only has to cover the blocks a
        /// map actually uses, not every block the original defined. A map built inside jungle's
        /// 210 draws correctly under jungle. Whether a map that reaches outside them survives is
        /// untested, so a pack is not offered or refused on its coverage here.
        ///
        /// The map's ORIGINAL packs are remembered the first time a theme is applied, which is
        /// what makes going back possible at all: once the list has been overwritten, nothing
        /// else records what the map started as.
        /// </summary>
        public static bool setTheme(string folder, string theme)
        {
            var level = Path.Combine(folder, "level.json");
            if (!File.Exists(level)) { return false; }

            try
            {
                var text = File.ReadAllText(level);

                if (JsonNode.Parse(GameMaps.stripComments(text),
                    documentOptions: new JsonDocumentOptions
                    {
                        AllowTrailingCommas = true,
                        CommentHandling = JsonCommentHandling.Skip,
                    }) is not JsonObject parsed)
                {
                    return false;
                }

                //Called for what it WRITES, not for what it returns: this is the one moment the
                //map's original packs can still be seen, and after the line below they cannot.
                basePacks(folder, parsed);

                parsed["resource-packs"] = new JsonArray(JsonValue.Create(theme));

                File.WriteAllText(level, parsed.ToJsonString(
                    new JsonSerializerOptions { WriteIndented = true }));

                return true;
            }
            catch (Exception)
            {
                return false;
            }
        }

        public sealed class Exported
        {
            public Exported(string folder, int files, long bytes, IReadOnlyList<string> notes)
            {
                Folder = folder;
                Files = files;
                Bytes = bytes;
                Notes = notes;
            }

            public string Folder { get; }
            public int Files { get; }
            public long Bytes { get; }
            public IReadOnlyList<string> Notes { get; }
        }

        /// <summary>
        /// Writes everything a mission is made of into a folder.
        ///
        /// The whole resource pack is deliberately not copied. It is a thousand textures and
        /// twenty megabytes, and none of it is needed to change a map - only the blocks file is,
        /// because that is the table saying which block id means which block.
        /// </summary>
        public static Exported export(GameMaps.Mission mission, string folder)
        {
            var notes = new List<string>();
            var files = 0;
            var bytes = 0L;

            var level = GameMaps.read(mission.PakPath)
                ?? throw new InvalidOperationException(
                    $"{mission.Name} could not be read out of the game's paks.");

            Directory.CreateDirectory(folder);

            void write(string relative, byte[] data)
            {
                var full = Path.Combine(folder, relative.Replace('/', Path.DirectorySeparatorChar));
                Directory.CreateDirectory(Path.GetDirectoryName(full)!);
                File.WriteAllBytes(full, data);
                files++;
                bytes += data.Length;
            }

            write(LEVEL_FILE, level);

            var (groups, packs) = GameMaps.referencedBy(level);
            notes.Add($"{groups.Count} object group(s), {packs.Count} resource pack(s)");

            foreach (var group in groups)
            {
                //A level names a group as "Arcane/objectgroup" - the folder and the file together.
                var got = GameMaps.read("/Dungeons/Content/" + GameMaps.GROUPS + group);
                if (got == null)
                {
                    notes.Add($"object group not found: {group}");
                    continue;
                }
                write(GROUPS_FOLDER + "/" + group + ".json", got);
            }

            foreach (var pack in packs)
            {
                var got = GameMaps.read("/Dungeons/Content/" + GameMaps.PACKS + pack + "/blocks");
                if (got == null)
                {
                    notes.Add($"no blocks file for resource pack: {pack}");
                    continue;
                }
                write(PACKS_FOLDER + "/" + pack + "/blocks.json", got);
            }

            write(MARKER, Encoding.UTF8.GetBytes(marker(mission, groups, packs)));
            write("READ ME.txt", Encoding.UTF8.GetBytes(readMe(mission)));

            return new Exported(folder, files, bytes, notes);
        }

        /// <summary>
        /// Puts a folder back into the game, over whichever mission is chosen.
        ///
        /// The mission being replaced does not have to be the one it came from. That is the point
        /// of choosing: a map built from Creeper Woods' tiles can be installed over Lower Temple,
        /// and Creeper Woods stays as it was.
        /// </summary>
        /// <summary>
        /// Makes a level claim to be the mission it is being installed over.
        ///
        /// A level's id is not its own name. It is a lookup into the game's own table of levels -
        /// for the theme, the lighting, the ambience and the text - and the table is fixed at
        /// fifty-odd names that shipped with the game. A level whose id is something else asks
        /// for a level that does not exist.
        ///
        /// Blossoming Isles, which works, never gets this wrong: its three levels live in files
        /// called SakuraGarden, SakuraPagoda and SakuraUndercroft, and every one of them declares
        /// id "lowertemple" and borrows Cacti Canyon's ambience and text. None of its own names
        /// appear anywhere in the game's table, and none of them are used.
        ///
        /// Done HERE rather than when the map is made, because this is the only moment the answer
        /// is certain. A folder can be built from one mission, edited, and installed over
        /// another; it can arrive through Import map from somebody else entirely. What the level
        /// has to claim is whatever it is about to be loaded as, and that is known once and only
        /// once - now.
        /// </summary>
        private static byte[] claim(byte[] raw, GameMaps.Mission over)
        {
            try
            {
                var text = GameMaps.stripComments(
                    new UTF8Encoding(false).GetString(raw).TrimStart('\uFEFF'));

                if (JsonNode.Parse(text, documentOptions: new JsonDocumentOptions
                {
                    AllowTrailingCommas = true,
                    CommentHandling = JsonCommentHandling.Skip,
                }) is not JsonObject level)
                {
                    return raw;
                }

                var was = level["id"]?.GetValue<string>();
                if (string.Equals(was, over.Name, StringComparison.Ordinal)) { return raw; }

                //An id the game HAS is left exactly where it is. This only exists to rescue a
                //level whose id names nothing - "baseline" - and rewriting a good one does real
                //damage, because the id is not only the theme: the game looks for a tile's
                //companion sub-level at Decor/Maps/<id>/SubLevels/<tile>. Blossoming Isles keeps
                //its chests and its beacon script there under "lowertemple", so an install that
                //helpfully renamed the id to whichever mission it was going over sent the game
                //looking in a folder that does not exist, and it crashed on entering the level.
                //
                //A level's id need not match the file it is loaded as. That mod is the proof:
                //its files are SakuraGarden, SakuraPagoda and SakuraUndercroft, all three
                //declare "lowertemple", and all three load.
                if (was != null && GameMaps.all().Any(one =>
                        string.Equals(one.Name, was, StringComparison.OrdinalIgnoreCase)))
                {
                    Console.WriteLine($"[map] level id \"{was}\" is a mission the game has, left alone");
                    return raw;
                }

                level["id"] = over.Name;

                //These two are separate lookups into the same table. They come along when they
                //were following the id - which is what a map that never thought about them looks
                //like - and are left alone when they name something else, because borrowing one
                //level's ambience for another is deliberate and Blossoming Isles does exactly
                //that: id "lowertemple", ambience and text from Cacti Canyon.
                foreach (var also in new[] { "ambience-level-id", "loctable-id" })
                {
                    var had = level[also]?.GetValue<string>();

                    if (had == null || string.Equals(had, was, StringComparison.Ordinal))
                    {
                        level[also] = over.Name;
                    }
                }

                Console.WriteLine($"[map] level id \"{was}\" -> \"{over.Name}\" for install");

                return new UTF8Encoding(false).GetBytes(
                    level.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
            }
            catch (Exception problem)
            {
                //A level this cannot read is one the game may still be able to. Installing it
                //unchanged is the same behaviour as before this existed.
                Console.WriteLine($"[map] could not set the level id: {problem.Message}");
                return raw;
            }
        }

        /// <summary>
        /// Everything installing would put in the pak, without writing one.
        ///
        /// So that what goes into the game can be examined before it goes there. A crash on
        /// entering a level says nothing about which of a thousand files was wrong.
        /// </summary>
        public static IReadOnlyList<PakWriter.Entry> wouldShip(string folder, GameMaps.Mission over)
            => gather(folder, over);

        public static CustomSkins.InstalledMod install(string folder, GameMaps.Mission over)
        {
            //The same repair the custom slots get. A map welded into one tile carries every
            //side-path its rooms declared, and the generator refuses the level for it - which is
            //no more acceptable when the map is going over one of the game's missions than when
            //it is going into a slot.
            tidyMobGroups(folder);
            wireMobs(folder);
            dropSidePaths(folder);

            return CustomSkins.writeModPak(PREFIX + safe(over.Name), gather(folder, over));
        }

        /// <summary>
        /// Which string table a level will read once it is installed.
        ///
        /// Its own "loctable-id" if it names one, then its id, and the mission only as a last
        /// resort. Read back off the level AFTER claim has had its say, so what is measured is
        /// what the game will see rather than what the folder happened to hold.
        /// </summary>
        private static string loctableIn(byte[] claimed, GameMaps.Mission over)
        {
            try
            {
                var text = GameMaps.stripComments(
                    new UTF8Encoding(false).GetString(claimed).TrimStart('﻿'));

                if (JsonNode.Parse(text, documentOptions: new JsonDocumentOptions
                {
                    AllowTrailingCommas = true,
                    CommentHandling = JsonCommentHandling.Skip,
                }) is JsonObject level)
                {
                    return level["loctable-id"]?.GetValue<string>()
                        ?? level["id"]?.GetValue<string>()
                        ?? over.Name;
                }
            }
            catch
            {
                //A level that cannot be read here is one install is about to ship unchanged.
            }

            return over.Name;
        }

        private static List<PakWriter.Entry> gather(string folder, GameMaps.Mission over)
        {
            var level = Path.Combine(folder, LEVEL_FILE);

            //Folders written before the .json names were understood have "level" instead.
            if (!File.Exists(level)) { level = Path.Combine(folder, "level"); }

            if (!File.Exists(level))
            {
                throw new InvalidOperationException(
                    $"There is no file called \"{LEVEL_FILE}\" in that folder, so it is not a map "
                    + "exported by this tab.");
            }

            var claimed = claim(File.ReadAllBytes(level), over);

            var entries = new List<PakWriter.Entry>
            {
                //No leading slash inside a pak, and the game's own spelling of the path - this is
                //what makes the game read our file instead of its own.
                new PakWriter.Entry(
                    "Dungeons/Content/data/lovika/levels/" + over.Name + ".json",
                    claimed),
            };

            //Wording the map invented, as a table extending the one the level ACTUALLY READS.
            //
            //Which is not necessarily the mission being installed over. A level is free to name
            //another mission's table - Blossoming Isles' three all read Cacti Canyon's while
            //declaring the id of Lower Temple - and claim() leaves an id the game already has
            //alone. Choosing the table by the mission instead would write rows into a file the
            //level never opens, and the wording would come out blank with nothing said anywhere.
            //
            //See MapWords for why a CSV works where the compiled string table would not.
            var reads = loctableIn(claimed, over);

            var said = MapWords.tableFor(folder, reads);
            if (said != null)
            {
                //The same name for the contents and for the path, or the table is built out of
                //one mission's rows and filed under another's, and both are wrong at once.
                entries.Add(new PakWriter.Entry(MapWords.pakPathFor(reads), said));
                Console.WriteLine($"[map] shipping {said.Length} bytes of wording for {over.Name}");
            }

            var groups = Path.Combine(folder, GROUPS_FOLDER);
            if (Directory.Exists(groups))
            {
                foreach (var file in Directory.GetFiles(groups, "*", SearchOption.AllDirectories))
                {
                    if (isWorkingFile(file)) { continue; }

                    entries.Add(new PakWriter.Entry(
                        "Dungeons/Content/" + GameMaps.GROUPS + relative(groups, file),
                        File.ReadAllBytes(file)));
                }
            }

            var packs = Path.Combine(folder, PACKS_FOLDER);
            if (Directory.Exists(packs))
            {
                foreach (var file in Directory.GetFiles(packs, "*", SearchOption.AllDirectories))
                {
                    if (isWorkingFile(file)) { continue; }

                    entries.Add(new PakWriter.Entry(
                        "Dungeons/Content/" + GameMaps.PACKS + relative(packs, file),
                        File.ReadAllBytes(file)));
                }
            }

            if (entries.Count == 1)
            {
                //Not fatal - a level can be built entirely from another mission's tiles - but it
                //is nearly always a sign that the folder was moved without its object groups.
                //Said out loud rather than discovered in game as a mission that will not start.
                throw new InvalidOperationException(
                    "That folder has a level but no object groups. A mission with no tiles cannot "
                    + $"be generated. Expected them under \"{GROUPS_FOLDER}\".");
            }

            //A folder that came out of somebody else's mod carries far more than a map: fonts,
            //widgets, sub-levels, its own string table. ModPak wrote all of it down when it took
            //the pak apart, so it goes back at the paths it came from and the installed mod is
            //the mod, not a third of it. A folder this app exported has no manifest and nothing
            //changes for it.
            entries.AddRange(ModPak.extras(folder, entries));

            return entries;
        }

        /// <summary>Every map mod installed.</summary>
        /// <summary>
        /// Whether installing would actually change what the game loads.
        ///
        /// "Nothing changed yet" beside a live Save and install is a button offering to do
        /// nothing, and a press that does nothing is indistinguishable from a press that failed.
        /// But "nothing changed" is not the same as "nothing to install": a folder that arrived
        /// through Import, or one saved in an earlier session, has everything to install and no
        /// unsaved edits at all.
        ///
        /// So the question is asked of the PAK rather than of the edit list - is there anything
        /// in the folder the installed one has not got? File times only; nothing is read.
        /// </summary>
        public static bool worthInstalling(string folder, GameMaps.Mission over)
        {
            try
            {
                var pak = installedFor(over);
                if (pak == null || !File.Exists(pak)) { return true; }

                var packed = File.GetLastWriteTimeUtc(pak);

                foreach (var file in Directory.GetFiles(folder, "*", SearchOption.AllDirectories))
                {
                    //The kept originals and the pre-weld copy are not what gets packed, so a
                    //fresh one of those is not a reason to install.
                    if (file.EndsWith(".before", StringComparison.OrdinalIgnoreCase)
                        || file.EndsWith(".multitile", StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    if (File.GetLastWriteTimeUtc(file) > packed) { return true; }
                }

                return false;
            }
            catch
            {
                //A folder that cannot be looked at is one where the honest answer is "maybe",
                //and a button that is on when it need not be beats one that is off when it is
                //wanted.
                return true;
            }
        }

        public static IReadOnlyList<string> installed()
        {
            var folders = new List<string>();
            if (CustomSkins.paksFolder != null) { folders.Add(CustomSkins.paksFolder); }
            if (CustomSkins.modsFolder != null) { folders.Add(CustomSkins.modsFolder); }

            return folders
                .Where(Directory.Exists)
                .SelectMany(folder => Directory.GetFiles(
                    folder, CustomSkins.MOD_PREFIX + PREFIX + "*.pak"))
                .OrderBy(file => file, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        /// <summary>The mod that replaces one particular mission, if there is one.</summary>
        public static string? installedFor(GameMaps.Mission mission)
        {
            var wanted = CustomSkins.MOD_PREFIX + PREFIX + safe(mission.Name);

            return installed().FirstOrDefault(pak =>
                Path.GetFileNameWithoutExtension(pak)
                    .StartsWith(wanted, StringComparison.OrdinalIgnoreCase));
        }

        /// <summary>Puts a mission back to the game's own, by deleting what replaced it.</summary>
        public static bool remove(GameMaps.Mission mission)
        {
            var pak = installedFor(mission);
            if (pak == null || !File.Exists(pak)) { return false; }

            File.Delete(pak);
            return true;
        }

        /// <summary>Which mission a folder came from, if it says.</summary>
        public static string? cameFrom(string folder)
        {
            var marker = Path.Combine(folder, MARKER);
            if (!File.Exists(marker)) { return null; }

            try
            {
                using var document = System.Text.Json.JsonDocument.Parse(File.ReadAllText(marker));
                return document.RootElement.TryGetProperty("mission", out var name)
                    ? name.GetString()
                    : null;
            }
            catch (Exception)
            {
                return null;
            }
        }

        /// <summary>
        /// Whether a file in an exported folder is one of ours rather than one to ship.
        ///
        /// The converters keep the previous version of whatever they rewrite - objectgroup.json
        /// .before, level.json.random, level.json.multitile - which is exactly what you want on
        /// disk and exactly what you do not want in a pak. Shipped, they doubled the size of one
        /// and appeared in the index as a second object group, because the reader strips the last
        /// extension and ".json.before" comes back as ".json".
        /// </summary>
        private static bool isWorkingFile(string file)
        {
            var name = Path.GetFileName(file);
            return name.EndsWith(".before", StringComparison.OrdinalIgnoreCase)
                || name.EndsWith(".new", StringComparison.OrdinalIgnoreCase)
                || name.EndsWith(".random", StringComparison.OrdinalIgnoreCase)
                || name.EndsWith(".multitile", StringComparison.OrdinalIgnoreCase)
                || name.StartsWith(".", StringComparison.Ordinal);
        }

        private static string relative(string from, string file)
            => Path.GetRelativePath(from, file).Replace(Path.DirectorySeparatorChar, '/');

        private static string marker(GameMaps.Mission mission, List<string> groups, List<string> packs)
        {
            var quoted = (IEnumerable<string> of)
                => string.Join(", ", of.Select(one => "\"" + one + "\""));

            return "{\n"
                + $"  \"mission\": \"{mission.Name}\",\n"
                + $"  \"label\": \"{mission.Label}\",\n"
                + $"  \"object-groups\": [{quoted(groups)}],\n"
                + $"  \"resource-packs\": [{quoted(packs)}],\n"
                + $"  \"exported\": \"{DateTime.Now:yyyy-MM-dd HH:mm}\"\n"
                + "}\n";
        }

        private static string readMe(GameMaps.Mission mission) => string.Join("\n", new[]
        {
            $"{mission.Label}, exported from Minecraft Dungeons by MCD Reborn.",
            "",
            "  level                         the mission: which tiles, in what order, with what objectives",
            "  objectgroups/<Name>/objectgroup   the tiles themselves - Minecraft blocks, zlib'd and base64'd",
            "  resourcepacks/<Name>/blocks       what each block id means. Key order IS the id.",
            "",
            "A tile's blocks array is one byte of block id per cell followed by half a byte of",
            "metadata each, indexed x + sizeX * (z + sizeZ * y) - the same layout a Minecraft",
            ".schematic uses. The high nibble of each packed byte belongs to the even index.",
            "",
            "Edit what you like and bring the folder back to the Maps tab. Import asks which",
            "mission to install it over; the game's own files are never modified, and Remove",
            "deletes the mod pak and gives the original mission back.",
            "",
        });

        /// <summary>A name that is safe in a file name.</summary>
        private static string safe(string name)
        {
            var clean = new string(name
                .Select(c => char.IsLetterOrDigit(c) ? c : '_')
                .ToArray());

            return clean.Trim('_');
        }
    }
}
