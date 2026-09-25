#nullable enable

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace MCDSaveEdit.Logic
{
    /// <summary>
    /// The things about a level that the game will not tell you are wrong.
    ///
    /// None of these fail loudly. A mission nothing tells you to leave loads and plays and cannot
    /// be finished. A fight naming a group that does not exist spawns nothing, and in the file it
    /// looks exactly like one that works. That silence is the whole case for checking: by the time
    /// anybody notices in the game, the question is "what did I break", and the answer is several
    /// edits back.
    ///
    /// Read off the level JSON alone - no rooms, no tiles - so the same checks run on a map
    /// somebody is building and on every mission the game ships. That second use is the test:
    /// each rule here was run over all 56 shipped levels, and a rule that flags a level the game
    /// ships is a rule that is wrong, not a level that is.
    ///
    /// Flags only. There is no "fix it" here and there should not be - see the Remove button for
    /// the way out of a broken state; a repair that guesses is a second bug.
    /// </summary>
    public static class MapChecks
    {
        public enum Severity { Warn, Bad }

        public sealed class Problem
        {
            public Problem(Severity severity, string what, string why)
            {
                Severity = severity;
                What = what;
                Why = why;
            }

            /// <summary>Bad stops a mission from working. Warn is almost certainly a mistake.</summary>
            public Severity Severity { get; }

            /// <summary>Short, naming the thing - "exit_through_the_gate: frees nobody".</summary>
            public string What { get; }

            /// <summary>What it will do in the game, which is what makes it worth fixing.</summary>
            public string Why { get; }

            public override string ToString()
                => (Severity == Severity.Bad ? "BAD   " : "warn  ") + What + " - " + Why;
        }

        /// <summary>
        /// Prefabs you free rather than press. Every one of the seven in the game holds a gate
        /// shut - BP_CapturedVillager twice, BP_CapturedPanda twice, BP_MinerVillager twice,
        /// BP_TrappedTurtle once - because the reward for a rescue is the way on.
        /// </summary>
        private static readonly string[] CAPTIVES =
            { "CapturedVillager", "CapturedPanda", "MinerVillager", "TrappedTurtle" };

        /// <summary>
        /// Everything in <see cref="run(JsonObject)"/>, plus what only the rooms can say.
        ///
        /// An exit gate is a region, and regions live in the tile files rather than in the level,
        /// so whether one exists at all can only be asked of a loaded map.
        /// </summary>
        public static List<Problem> run(MapSpawns.Map map)
        {
            var found = run(map.Level);

            foreach (var room in map.Rooms)
            {
                if (MapSpawns.exitsOf(map, room).Any(one => !one.Claimed))
                {
                    found.Add(new Problem(Severity.Warn, "an exit gate nothing sends you to",
                        "the gate is only drawn where an objective clicks it, so this one never "
                        + "appears - Put the exit gate here makes the objective with it"));
                    break;
                }
            }

            return found;
        }

        public static List<Problem> run(JsonObject level)
        {
            var found = new List<Problem>();

            finishable(level, found);
            rescues(level, found);
            fights(level, found);
            groups(level, found);

            return found;
        }

        // ------------------------------------------------------------------ the rules

        /// <summary>
        /// Every objective has to be something the player can actually finish.
        ///
        /// This replaced "an objective has to name the exit", which was checked against the 56
        /// shipped levels and was wrong for seventeen of them. There is no exit marker in the
        /// game. A mission ends when its last objective completes, and the shipped ones end on
        /// anything: a reach step to end.*.objective in Arch Haven, an elevator in Fiery Forge, a
        /// beacon on Obsidian Pinnacle, a Nether door at endgate in Warped Forest. "exit" is a
        /// region NAME some missions and MapSpawns happen to use.
        ///
        /// What is true of every shipped mission is weaker and more useful: each step names
        /// something to do. A click with no location has nowhere to be clicked, a reach step with
        /// no end region has nowhere to reach, and either one stalls the chain for good.
        /// </summary>
        private static void finishable(JsonObject level, List<Problem> found)
        {
            var steps = (level["objectives"] as JsonArray ?? new JsonArray())
                .OfType<JsonObject>().ToList();

            //The lobby, the camp, the Tower and the hunt templates have none, and are not
            //missions. A map somebody builds always starts from one that has them.
            if (steps.Count == 0)
            {
                found.Add(new Problem(Severity.Bad, "no objectives",
                    "a mission with nothing to do never completes"));
                return;
            }

            foreach (var step in steps)
            {
                if (step["click"] is JsonObject click && !names(click["locations"]).Any())
                {
                    found.Add(new Problem(Severity.Bad, title(step) + ": nothing to click",
                        "a click step with no location can never be done, and the mission "
                        + "stops there"));
                }

                if (step["gauntlet"] is JsonObject walk
                    && string.IsNullOrWhiteSpace(text(walk["end-region"])))
                {
                    found.Add(new Problem(Severity.Bad, title(step) + ": nowhere to reach",
                        "a reach step with no end region can never be done"));
                }
            }
        }

        private static void rescues(JsonObject level, List<Problem> found)
        {
            foreach (var step in (level["objectives"] as JsonArray ?? new JsonArray()).OfType<JsonObject>())
            {
                if (step["click"] is not JsonObject click) { continue; }

                var prefab = leaf(text(click["object"]));
                if (!CAPTIVES.Any(one => prefab.IndexOf(one, StringComparison.OrdinalIgnoreCase) >= 0))
                {
                    continue;
                }

                if (names(click["locked-doors"]).Any()) { continue; }

                found.Add(new Problem(Severity.Warn, title(step) + ": frees nobody into anywhere",
                    "a rescue that opens no gate - all seven in the game hold one shut"));
            }
        }

        /// <summary>A fight with no group to draw from spawns nothing, and says nothing.</summary>
        private static void fights(JsonObject level, List<Problem> found)
        {
            foreach (var step in (level["objectives"] as JsonArray ?? new JsonArray()).OfType<JsonObject>())
            {
                if (step["killgroup"] is not JsonObject fight) { continue; }

                var mobs = fight["mobs"] as JsonArray;
                var group = mobs?.Count > 1 ? text(mobs[1]) : null;

                if (string.IsNullOrWhiteSpace(group))
                {
                    found.Add(new Problem(Severity.Bad, title(step) + ": no mob group",
                        "a fight that names no group spawns nothing"));
                }
            }

            foreach (var one in (level["challenges"] as JsonArray ?? new JsonArray()).OfType<JsonObject>())
            {
                //Not every challenge is a fight: 248 of the game's 1,304 have no arena at all.
                if (one["arena"] is not JsonObject arena) { continue; }

                var waves = (arena["waves"] as JsonArray ?? new JsonArray())
                    .Where(wave => wave is JsonArray || wave is JsonObject).ToList();
                var id = text(one["id"]) ?? "a challenge";

                //A boss arena has no waves and needs none: the Tower's tempest golem and tower
                //guardian spawn through is-boss and mob-activations, and its ArenaBattle arenas
                //run from a prespawn-mob blueprint. Waves are how an ordinary arena spawns, not
                //the only way any arena can.
                var spawnsAnotherWay = arena["is-boss"] != null
                    || arena["mob-activations"] != null
                    || !string.IsNullOrWhiteSpace(text(arena["prespawn-mob"]));

                if (waves.Count == 0 && !spawnsAnotherWay)
                {
                    found.Add(new Problem(Severity.Bad, id + ": no waves",
                        "an arena with no waves spawns nothing"));
                    continue;
                }

                if (waves.Any(wave => !groupsOf(wave).Any()))
                {
                    found.Add(new Problem(Severity.Bad, id + ": a wave with no mob group",
                        "that wave spawns nothing"));
                }
            }
        }

        /// <summary>
        /// Every group a level names has to be declared - but not necessarily by this level.
        ///
        /// 646 of the 4,701 group references across the 56 levels name a group another level
        /// declares, and that is right: a level that embeds another's sub-area inherits its mobs
        /// with it. Creeper Woods names 25 groups in its waves and declares 9. So the question is
        /// whether ANYTHING declares it, against the pool of every shipped level.
        ///
        /// Warn rather than Bad, because run over the shipped game this finds two kinds of thing.
        /// Some are plain typos in the game's own data: the Enchanted-Sentry cave copied into
        /// nine levels names "miniarea-singles" once where every other reference - forty of
        /// them - says "miniarena-singles", and Enderwilds has "endling-mix-default,Entermites"
        /// as ONE name with a comma in it. Others carry a difficulty suffix -
        /// settlement-mix-easy, ts-twr-mix-2-hard - and may well be resolved by something that is
        /// not a level file. Until that is known, this is a strong hint and not a certainty.
        /// </summary>
        private static void groups(JsonObject level, List<Problem> found)
        {
            var here = new HashSet<string>(
                (level["mob-groups"] as JsonArray ?? new JsonArray()).OfType<JsonObject>()
                    .Select(one => text(one["id"]))
                    .Where(one => !string.IsNullOrEmpty(one))!
                    .Cast<string>(),
                StringComparer.OrdinalIgnoreCase);

            var anywhere = pool();
            var said = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (var group in referenced(level))
            {
                if (here.Contains(group) || anywhere.Contains(group)) { continue; }
                if (!said.Add(group)) { continue; }

                found.Add(new Problem(Severity.Warn, group + ": no level file declares it",
                    "not in this level's mob-groups nor any other shipped level's - most likely a "
                    + "misspelling, and whatever names it spawns nothing"));
            }
        }

        /// <summary>Every group id the level names, wherever it names one.</summary>
        private static IEnumerable<string> referenced(JsonObject level)
        {
            foreach (var step in (level["objectives"] as JsonArray ?? new JsonArray()).OfType<JsonObject>())
            {
                var mobs = (step["killgroup"] as JsonObject)?["mobs"] as JsonArray;
                var group = mobs?.Count > 1 ? text(mobs[1]) : null;
                if (!string.IsNullOrWhiteSpace(group)) { yield return group!; }
            }

            foreach (var one in (level["challenges"] as JsonArray ?? new JsonArray()).OfType<JsonObject>())
            {
                foreach (var wave in (one["arena"] as JsonObject)?["waves"] as JsonArray ?? new JsonArray())
                {
                    foreach (var group in groupsOf(wave)) { yield return group; }
                }
            }

            foreach (var id in ids((level["default-mobs"] as JsonObject)?["only"]))
            {
                yield return id;
            }

            //Stretches and sub-areas carry their own "mobs": {"only": [...]}, anywhere down.
            foreach (var id in onlyIds(level["stretches"])) { yield return id; }
            foreach (var id in onlyIds(level["dungeons"])) { yield return id; }
        }

        private static IEnumerable<string> onlyIds(JsonNode? node)
        {
            if (node is JsonObject body)
            {
                if (body["mobs"] is JsonObject mobs)
                {
                    foreach (var id in ids(mobs["only"])) { yield return id; }
                }

                foreach (var pair in body)
                {
                    foreach (var id in onlyIds(pair.Value)) { yield return id; }
                }
            }
            else if (node is JsonArray list)
            {
                foreach (var one in list)
                {
                    foreach (var id in onlyIds(one)) { yield return id; }
                }
            }
        }

        /// <summary>
        /// The groups one wave draws from, in either of the two shapes the game writes.
        ///
        /// Nearly every wave is [count, "group"] - 3,781 of them. Nineteen, all in the Tower and
        /// Warped Forest, are {"count": 5, "groups": [...], "spawn-at-region": "..."} instead, and
        /// reading only the first shape flagged Warped Forest's key arena as spawning nothing.
        /// </summary>
        public static IEnumerable<string> groupsOf(JsonNode? wave)
        {
            if (wave is JsonArray pair)
            {
                var group = pair.Count > 1 ? text(pair[1]) : null;
                if (!string.IsNullOrWhiteSpace(group)) { yield return group!; }
            }
            else if (wave is JsonObject body)
            {
                foreach (var group in ids(body["groups"])) { yield return group; }
            }
        }

        // ------------------------------------------------------------------ every shipped group

        private static HashSet<string>? _pool;
        private static readonly object _poolLock = new object();

        /// <summary>
        /// Every mob-group id any shipped level declares. Read once, the first time it is asked
        /// for, out of the game's own paks - 522 of them on the current game.
        /// </summary>
        public static HashSet<string> pool()
        {
            lock (_poolLock)
            {
                if (_pool != null) { return _pool; }

                var made = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

                foreach (var mission in GameMaps.all())
                {
                    var level = parse(GameMaps.read(mission.PakPath));
                    foreach (var one in (level?["mob-groups"] as JsonArray ?? new JsonArray()).OfType<JsonObject>())
                    {
                        var id = text(one["id"]);
                        if (!string.IsNullOrEmpty(id)) { made.Add(id!); }
                    }
                }

                _pool = made;
                return _pool;
            }
        }

        /// <summary>A shipped level file, read the way the game's own loader tolerates it.</summary>
        public static JsonObject? parse(byte[]? raw)
        {
            if (raw == null) { return null; }

            try
            {
                var text = GameMaps.stripComments(new UTF8Encoding(false).GetString(raw).TrimStart('﻿'));
                return JsonNode.Parse(text, documentOptions: new JsonDocumentOptions
                {
                    AllowTrailingCommas = true,
                    CommentHandling = JsonCommentHandling.Skip,
                }) as JsonObject;
            }
            catch (Exception)
            {
                return null;
            }
        }

        // ------------------------------------------------------------------ reading helpers

        private static string title(JsonObject step)
            => (text(step["description"]) ?? text(step["name"]) ?? "an objective")
                .Replace("description_", string.Empty);

        private static string? text(JsonNode? node)
            => node is JsonValue value && value.TryGetValue<string>(out var said) ? said : null;

        private static string leaf(string? path)
        {
            if (string.IsNullOrEmpty(path)) { return string.Empty; }
            var cut = path!.LastIndexOfAny(new[] { '.', '/' });
            return cut >= 0 ? path.Substring(cut + 1) : path;
        }

        private static IEnumerable<string> names(JsonNode? node)
            => (node as JsonArray ?? new JsonArray()).Select(text)
                .Where(one => !string.IsNullOrEmpty(one)).Cast<string>();

        /// <summary>Either bare names or {id, weight} objects, the shape default-mobs uses.</summary>
        private static IEnumerable<string> ids(JsonNode? node)
        {
            foreach (var one in node as JsonArray ?? new JsonArray())
            {
                var id = one is JsonObject body ? text(body["id"]) : text(one);
                if (!string.IsNullOrWhiteSpace(id)) { yield return id!; }
            }
        }
    }
}
