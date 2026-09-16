using MCDSaveEdit.Save.Models.Profiles;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
#nullable enable

namespace MCDSaveEdit.Logic
{
    /// <summary>
    /// The Tower, as a save file records it.
    ///
    /// It lives under missionStatesMap, which this app had always carried as an opaque blob:
    /// parsed, kept, written back, never read. That is why nothing here could show a tower run
    /// even though every save has been carrying them all along.
    ///
    /// The shape, read off a live run rather than guessed:
    ///
    ///   missionStatesMap
    ///     thetower
    ///       missionStates[]              one per run, oldest first
    ///         completedOnce              whether that run was finished
    ///         livesLost
    ///         missionDifficulty          difficulty and threat level
    ///         towerInfo                  null on a finished run, filled in on the live one
    ///           towerFinished
    ///           towerCurrentFloorWasCompleted
    ///           towerConfig.floors[]      the 31 floors, their tiles and reward offers
    ///           towerInfo                 nested, and the useful half:
    ///             towerInfoCurrentFloor
    ///             towerInfoBossesKilled
    ///             towerInfoFloors[]       Empty / Combat / Merchant / Boss, in order
    ///           towerPlayersData[]        one per hero in the run
    ///             playerArrowsAmmount     the game's spelling, not a typo here
    ///             playerEnchantmentPointsGranted
    ///             playerIsTowerOwner
    ///             playerItems[]           the gear carried in the tower, the same shape as
    ///                                     any other item, minus the slot and index
    ///
    /// Everything is reached through the node rather than copied into models, so a field this
    /// app has never heard of survives being written back. The game's own missions sit in the
    /// same map, and a model would have dropped every one of them.
    /// </summary>
    public static class TowerRuns
    {
        private const string TOWER_KEY = "thetower";

        /// <summary>One tower run, as a view onto the save's own nodes.</summary>
        public sealed class Run
        {
            private readonly JsonObject _state;

            public Run(JsonObject state, int index) { _state = state; Index = index; }

            public int Index { get; }

            /// <summary>Filled in while a run is live; null once it is over.</summary>
            private JsonObject? outer => _state["towerInfo"] as JsonObject;

            /// <summary>The nested towerInfo, which is where the floor and the kills are.</summary>
            private JsonObject? inner => outer?["towerInfo"] as JsonObject;

            /// <summary>A run still in progress, which is the only kind there is anything to edit on.</summary>
            public bool InProgress => outer != null && !Finished;

            public bool CompletedOnce => readBool(_state, "completedOnce") ?? false;

            public bool Finished => readBool(outer, "towerFinished") ?? false;

            public bool CurrentFloorWasCompleted => readBool(outer, "towerCurrentFloorWasCompleted") ?? false;

            public string? TowerId => (outer?["towerId"] as JsonValue)?.ToString();

            public string Difficulty => text(_state["missionDifficulty"] as JsonObject, "difficulty");

            public string ThreatLevel => text(_state["missionDifficulty"] as JsonObject, "threatLevel");

            public int LivesLost {
                get => readInt(_state, "livesLost") ?? 0;
                set => write(_state, "livesLost", value);
            }

            public int CurrentFloor {
                get => readInt(inner, "towerInfoCurrentFloor") ?? 0;
                set => write(inner, "towerInfoCurrentFloor", Math.Max(0, Math.Min(value, FloorCount)));
            }

            public int BossesKilled {
                get => readInt(inner, "towerInfoBossesKilled") ?? 0;
                set => write(inner, "towerInfoBossesKilled", Math.Max(0, value));
            }

            /// <summary>The number the run was generated from. Changing it changes nothing already generated.</summary>
            public int Seed => readInt(outer?["towerConfig"] as JsonObject, "seed") ?? 0;

            /// <summary>
            /// The floors, as things that can be read and changed.
            ///
            /// A floor is split across two arrays that the game keeps in step by position: what
            /// kind of floor it is lives in towerInfoFloors, while what it offers and what it is
            /// built from live in towerConfig.floors. Neither half is much use alone, so a Floor
            /// here holds both and hides the fact that they were ever apart.
            /// </summary>
            public IReadOnlyList<Floor> Floors
            {
                get {
                    var kinds = inner?["towerInfoFloors"] as JsonArray;
                    var configs = (outer?["towerConfig"] as JsonObject)?["floors"] as JsonArray;
                    var count = Math.Max(kinds?.Count ?? 0, configs?.Count ?? 0);

                    var floors = new List<Floor>();
                    for (int i = 0; i < count; i++)
                    {
                        floors.Add(new Floor(
                            i,
                            i < (kinds?.Count ?? 0) ? kinds![i] as JsonObject : null,
                            i < (configs?.Count ?? 0) ? configs![i] as JsonObject : null));
                    }
                    return floors;
                }
            }

            public int FloorCount => Floors.Count;

            /// <summary>The heroes in the run. The owner is the one this save belongs to.</summary>
            public IReadOnlyList<Player> Players
            {
                get {
                    if (!(outer?["towerPlayersData"] is JsonArray players)) { return Array.Empty<Player>(); }
                    return players.OfType<JsonObject>().Select(player => new Player(player)).ToList();
                }
            }

            public Player? Owner => Players.FirstOrDefault(player => player.IsOwner) ?? Players.FirstOrDefault();

            /// <summary>
            /// Every floor build this run contains, one of each, grouped by kind.
            ///
            /// The run itself is the source rather than a list written here, because everything
            /// in it is something the game generated for this tower: every tile exists, every
            /// encounter exists, and each is already paired with the other correctly. A list of
            /// names invented here could not promise any of that.
            /// </summary>
            public IReadOnlyList<Layout> layouts()
            {
                var found = new List<Layout>();
                var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

                foreach (var floor in Floors)
                {
                    if (string.IsNullOrEmpty(floor.Type) || string.IsNullOrEmpty(floor.Tile)) { continue; }
                    var layout = new Layout(floor.Type, floor.Tile, floor.Challenges);
                    if (seen.Add(layout.key)) { found.Add(layout); }
                }
                return found;
            }

            /// <summary>The builds available for one kind of floor.</summary>
            public IReadOnlyList<Layout> layoutsFor(string type)
                => layouts().Where(layout => string.Equals(layout.Type, type, StringComparison.OrdinalIgnoreCase)).ToList();

            /// <summary>The levels this run builds for one kind of floor.</summary>
            public IReadOnlyList<string> tilesFor(string type)
                => layoutsFor(type).Select(layout => layout.Tile)
                    .Distinct(StringComparer.OrdinalIgnoreCase).ToList();

            /// <summary>
            /// The encounters this run runs on one kind of floor.
            ///
            /// Empty for merchants and for the entrance, which is the point: those floors have no
            /// encounter at all, so there is nothing to offer and nothing to pick.
            /// </summary>
            public IReadOnlyList<string> encountersFor(string type)
                => layoutsFor(type).SelectMany(layout => layout.Challenges)
                    .Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        }

        /// <summary>
        /// One floor of a run.
        ///
        /// The kinds and the rewards are what anyone would want to change: a floor that was going
        /// to be another fight can be made a merchant, and the five reward slots decide what the
        /// floor offers when it is cleared. The tile and the challenges are the level the game
        /// will actually build and the encounter it will run, so they are shown but left alone -
        /// a tile name that does not exist is a floor the game cannot load.
        /// </summary>
        public sealed class Floor
        {
            private readonly JsonObject? _kind;
            private readonly JsonObject? _config;

            public Floor(int index, JsonObject? kind, JsonObject? config)
            {
                Index = index; _kind = kind; _config = config;
            }

            public int Index { get; }

            /// <summary>Empty, Combat, Merchant or Boss.</summary>
            public string Type {
                get => text(_kind, "towerFloorType");
                set { if (_kind != null && _kind["towerFloorType"] != null) { _kind["towerFloorType"] = JsonValue.Create(value); } }
            }

            public string Tile => text(_config, "tile");

            public IReadOnlyList<string> Challenges
            {
                get {
                    if (!(_config?["challenges"] is JsonArray challenges)) { return Array.Empty<string>(); }
                    return challenges.Select(entry => entry?.ToString() ?? string.Empty).ToList();
                }
            }

            /// <summary>The five reward slots offered when the floor is cleared.</summary>
            public IReadOnlyList<string> Rewards
            {
                get {
                    if (!(_config?["rewards"] is JsonArray rewards)) { return Array.Empty<string>(); }
                    return rewards.Select(entry => entry?.ToString() ?? string.Empty).ToList();
                }
            }

            /// <summary>
            /// Makes this floor into another kind of floor, properly.
            ///
            /// The type on its own is only a label. What the game actually builds is the tile,
            /// and what it runs there is the challenge, so a Combat floor relabelled Merchant
            /// still loaded an arena and still made you fight. All three move together now.
            /// </summary>
            public void apply(Layout layout)
            {
                Type = layout.Type;
                setTile(layout.Tile);
                setChallenges(layout.Challenges);
            }

            public void setTile(string tile)
            {
                if (_config == null) { return; }
                _config["tile"] = JsonValue.Create(tile);
            }

            /// <summary>
            /// Sets the encounter, or takes the key away entirely when there is none.
            ///
            /// Removed rather than emptied because that is what the game writes: a merchant floor
            /// and the entrance have no "challenges" key at all, while every fighting floor has
            /// one. An empty array would be this app inventing a shape the game never produces.
            /// </summary>
            public void setChallenges(IReadOnlyList<string> challenges)
            {
                if (_config == null) { return; }

                if (challenges.Count == 0) { _config.Remove("challenges"); return; }

                var array = new JsonArray();
                foreach (var challenge in challenges) { array.Add(JsonValue.Create(challenge)); }
                _config["challenges"] = array;
            }

            public void setReward(int slot, string value)
            {
                if (!(_config?["rewards"] is JsonArray rewards)) { return; }
                if (slot < 0 || slot >= rewards.Count) { return; }
                rewards[slot] = JsonValue.Create(value);
            }
        }

        /// <summary>
        /// What a floor is made of: the kind, the level built for it, and the encounter run there.
        ///
        /// The three belong together. A merchant tile with a boss encounter is not something the
        /// game ever writes, and there is no way to know from outside whether it would even load,
        /// so the pieces are only ever offered in the combinations the game itself produced.
        /// </summary>
        public sealed class Layout
        {
            public Layout(string type, string tile, IReadOnlyList<string> challenges)
            {
                Type = type; Tile = tile; Challenges = challenges;
            }

            public string Type { get; }
            public string Tile { get; }
            public IReadOnlyList<string> Challenges { get; }

            public string Encounter => string.Join(", ", Challenges);

            internal string key => Type + "|" + Tile + "|" + Encounter;
        }

        /// <summary>The kinds a floor can be, as the save spells them.</summary>
        public static readonly IReadOnlyList<string> FLOOR_TYPES = new[] { "Empty", "Combat", "Merchant", "Boss" };

        /// <summary>
        /// What a reward slot can offer. "artefact" is the game's spelling, not a mistake here,
        /// and writing "artifact" instead would be writing a value the game does not know.
        /// </summary>
        public static readonly IReadOnlyList<string> REWARD_KINDS = new[] { "any", "armor", "melee", "ranged", "artefact" };

        /// <summary>One hero in a tower run, and the gear they are carrying through it.</summary>
        public sealed class Player
        {
            private readonly JsonObject _player;

            public Player(JsonObject player) { _player = player; }

            public bool IsOwner => readBool(_player, "playerIsTowerOwner") ?? false;

            public long PlayerId => readLong(_player, "playerId") ?? 0;

            //The game spells it "Ammount". Matching the save matters more than spelling does.
            public int Arrows {
                get => readInt(_player, "playerArrowsAmmount") ?? 0;
                set => write(_player, "playerArrowsAmmount", Math.Max(0, value));
            }

            public int EnchantmentPoints {
                get => readInt(_player, "playerEnchantmentPointsGranted") ?? 0;
                set => write(_player, "playerEnchantmentPointsGranted", Math.Max(0, value));
            }

            public int LastFloorIndex => readInt(_player, "playerLastFloorIndex") ?? 0;

            private JsonArray? itemsNode => _player["playerItems"] as JsonArray;

            /// <summary>
            /// The gear carried in the tower.
            ///
            /// Deserialised into the same Item this app edits everywhere else, because it is the
            /// same thing: a tower item is an ordinary item without a slot or an inventory index.
            /// That means the item screen, the enchantment picker and the pickers all work on it
            /// without knowing where it came from.
            /// </summary>
            public IReadOnlyList<Item> items()
            {
                var node = itemsNode;
                if (node == null) { return Array.Empty<Item>(); }

                var items = new List<Item>();
                foreach (var entry in node.OfType<JsonObject>())
                {
                    try
                    {
                        var item = entry.Deserialize<Item>(options);
                        if (item != null) { items.Add(item); }
                    }
                    catch (JsonException) { /* An item shape this app cannot read is left alone. */ }
                }
                return items;
            }

            /// <summary>
            /// Writes the list back over the save's own array.
            ///
            /// The whole array is replaced rather than patched in place: the items came out of it
            /// by position, and an edit that changed the count would otherwise have to guess which
            /// node belonged to which item.
            /// </summary>
            public void setItems(IEnumerable<Item> items)
            {
                if (itemsNode == null) { return; }

                var replacement = new JsonArray();
                foreach (var item in items)
                {
                    var node = JsonSerializer.SerializeToNode(item, options);
                    if (node != null) { replacement.Add(node); }
                }
                _player["playerItems"] = replacement;
            }
        }

        //The same settings the save itself is parsed with, so an item written back here matches
        //one written back by the rest of the app - enums as strings, nulls left out.
        private static readonly JsonSerializerOptions options = buildOptions();

        private static JsonSerializerOptions buildOptions()
        {
            var settings = new JsonSerializerOptions {
                DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
            };
            settings.Converters.Add(new Save.Models.Mapping.AttributeBasedConverterFactory());
            settings.Converters.Add(new Save.Models.Mapping.GuidConverterFactory());
            settings.Converters.Add(new System.Text.Json.Serialization.JsonStringEnumConverter());
            return settings;
        }

        /// <summary>Every tower run in a save, oldest first, or nothing when it has never been played.</summary>
        public static IReadOnlyList<Run> runsIn(ProfileSaveFile? profile)
        {
            var tower = profile?.MissionStatesMap?[TOWER_KEY] as JsonObject;
            if (!(tower?["missionStates"] is JsonArray states)) { return Array.Empty<Run>(); }

            var runs = new List<Run>();
            var index = 0;
            foreach (var state in states)
            {
                if (state is JsonObject stateObject) { runs.Add(new Run(stateObject, index)); }
                index++;
            }
            return runs;
        }

        /// <summary>
        /// The run being played, which is the newest one that still has its detail.
        ///
        /// The last rather than the first, because more than one can look live at once. A run
        /// that was walked out of rather than finished keeps its towerInfo, so the game leaves it
        /// sitting there looking exactly like a run in progress, and starting another one appends
        /// rather than replaces. Taking the first meant opening on a run abandoned weeks ago.
        ///
        /// Both are still listed. One of them being stale is a guess from the outside - the save
        /// gives no field that says so - and quietly hiding a run someone might want back is
        /// worse than showing one they will ignore.
        /// </summary>
        public static Run? liveRun(ProfileSaveFile? profile)
        {
            var runs = runsIn(profile);
            return runs.LastOrDefault(run => run.InProgress) ?? runs.LastOrDefault();
        }

        #region Reading a node without trusting it

        /// <summary>
        /// Reading a value has to cope with two kinds of node.
        ///
        /// A node parsed from the file is backed by a JsonElement and will convert between number
        /// types on demand. A node this app has written is backed by the CLR value it was made
        /// from, and asking it for a different type throws - so an int written here and read back
        /// as a double failed, was swallowed, and came back as zero. Everything edited on this
        /// screen then showed 0 the moment it was set, and the next edit would have saved that.
        ///
        /// TryGetValue handles both, so each reader asks for the types the value might be in.
        /// </summary>
        private static string text(JsonObject? owner, string key)
        {
            if (!(owner?[key] is JsonValue value)) { return string.Empty; }
            if (value.TryGetValue<string>(out var s)) { return s ?? string.Empty; }
            try { return value.ToJsonString().Trim('"'); }
            catch (Exception) { return string.Empty; }
        }

        private static bool? readBool(JsonObject? owner, string key)
        {
            if (!(owner?[key] is JsonValue value)) { return null; }
            if (value.TryGetValue<bool>(out var b)) { return b; }
            if (value.TryGetValue<int>(out var i)) { return i != 0; }
            if (value.TryGetValue<double>(out var d)) { return d != 0; }
            return null;
        }

        private static int? readInt(JsonObject? owner, string key)
        {
            var number = readLong(owner, key);
            return number == null ? (int?)null : (int)number.Value;
        }

        private static long? readLong(JsonObject? owner, string key)
        {
            if (!(owner?[key] is JsonValue value)) { return null; }
            if (value.TryGetValue<long>(out var l)) { return l; }
            if (value.TryGetValue<int>(out var i)) { return i; }
            if (value.TryGetValue<double>(out var d)) { return (long)d; }
            if (value.TryGetValue<decimal>(out var m)) { return (long)m; }
            return null;
        }

        //Only ever over a key that is already there. Inventing one would be inventing a field the
        //game does not read, and the tower is a structure the game wrote.
        private static void write(JsonObject? owner, string key, int value)
        {
            if (owner == null || owner[key] == null) { return; }
            owner[key] = JsonValue.Create(value);
        }

        #endregion
    }
}
