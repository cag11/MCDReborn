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

            public void setReward(int slot, string value)
            {
                if (!(_config?["rewards"] is JsonArray rewards)) { return; }
                if (slot < 0 || slot >= rewards.Count) { return; }
                rewards[slot] = JsonValue.Create(value);
            }
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

        /// <summary>The run still being played, if there is one.</summary>
        public static Run? liveRun(ProfileSaveFile? profile)
            => runsIn(profile).FirstOrDefault(run => run.InProgress);

        #region Reading a node without trusting it

        private static string text(JsonObject? owner, string key)
        {
            var value = owner?[key];
            if (value == null) { return string.Empty; }
            try { return value.GetValueKind() == JsonValueKind.String ? value.GetValue<string>() : value.ToJsonString(); }
            catch (Exception) { return string.Empty; }
        }

        private static bool? readBool(JsonObject? owner, string key)
        {
            var value = owner?[key];
            if (value == null) { return null; }
            try
            {
                return value.GetValueKind() switch {
                    JsonValueKind.True => true,
                    JsonValueKind.False => false,
                    JsonValueKind.Number => value.GetValue<double>() != 0,
                    _ => (bool?)null,
                };
            }
            catch (Exception) { return null; }
        }

        private static int? readInt(JsonObject? owner, string key)
        {
            var value = owner?[key];
            if (value == null || value.GetValueKind() != JsonValueKind.Number) { return null; }
            try { return (int)value.GetValue<double>(); }
            catch (Exception) { return null; }
        }

        private static long? readLong(JsonObject? owner, string key)
        {
            var value = owner?[key];
            if (value == null || value.GetValueKind() != JsonValueKind.Number) { return null; }
            try { return (long)value.GetValue<double>(); }
            catch (Exception) { return null; }
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
