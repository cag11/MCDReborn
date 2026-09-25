using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using MCDSaveEdit.Logic;
using R = MCDSaveEdit.Properties.Resources;

namespace MCDSaveEdit.UI
{
    /// <summary>
    /// The level's wiring, one relationship at a time.
    ///
    /// The first version of this drew the whole level at once: a box per room carrying every
    /// door and gate it holds, with everything else hanging off it. On a welded mission that is
    /// one box thirteen hundred pixels tall and a hundred and sixty wires crossing it, and the
    /// thing it was built to answer - "what opens this gate" - was the hardest question to ask
    /// of it.
    ///
    /// So it is tabs now, and a tab is not a filter over a drawing of the whole level: it IS the
    /// drawing. The Lock tab holds steps and gates and nothing else, because nothing else is on
    /// screen for them to connect to.
    ///
    /// Six of them, and only four are wires anybody draws. Everything else in a level file is
    /// either written by the generator or written by the app in the same call as the thing it
    /// belongs to - an objective is created together with the region it names, a challenge with
    /// its trigger and its reward. Those are not decisions, so they are drawn dashed and have no
    /// pin to pull. The last two tabs, Objectives and Challenges, exist only to read them.
    /// </summary>
    public partial class WiringWindow : Window
    {
        private enum Tab { Lock, Teleport, Descent, Mobs, Objectives, Challenges }

        private enum Wiring { Lock, Link, Descent, Mobs, Target, Key, Arena, Seal, Trigger, Reward }

        /// <summary>A pin. One in per box, and one out per kind of wire it can send.</summary>
        private sealed class Spot
        {
            public Spot(Box owner, Wiring kind, string label, bool right)
            {
                Owner = owner;
                Kind = kind;
                Label = label;
                Right = right;
            }

            public Box Owner { get; }
            public Wiring Kind { get; }
            public string Label { get; }
            public bool Right { get; }
            public double Down { get; set; }
            public Ellipse? Dot { get; set; }
        }

        /// <summary>
        /// One thing, not one room.
        ///
        /// <see cref="Key"/> is what a wire refers to it by and has to be unique across the tab;
        /// <see cref="Name"/> is the bare name the file writes, which is not unique at all -
        /// Creeper Woods has a caravan.*.objective and an end.*.objective.
        /// </summary>
        private sealed class Box
        {
            public Box(string kind, string key, string title)
            {
                Kind = kind;
                Key = key;
                Title = title;
            }

            public string Kind { get; }
            public string Key { get; }
            public string Title { get; }
            public string Name { get; set; } = string.Empty;
            public string Note { get; set; } = string.Empty;
            public double X { get; set; }
            public double Y { get; set; }
            public List<Spot> Spots { get; } = new List<Spot>();
            public Border? El { get; set; }

            /// <summary>The room a door belongs to. Null for anything the level owns.</summary>
            public MapSpawns.Room? Room { get; set; }

            /// <summary>Its index in objectives[] or challenges[], or -1.</summary>
            public int At { get; set; } = -1;

            /// <summary>True for a box this tab draws wires OUT of.</summary>
            public bool Sends { get; set; }

            public Spot? In => Spots.FirstOrDefault(one => !one.Right);
            public Spot? Out(Wiring kind) => Spots.FirstOrDefault(one => one.Right && one.Kind == kind);
        }

        private sealed class Wire
        {
            public Wire(Box from, Box to, Wiring kind)
            {
                From = from;
                To = to;
                Kind = kind;
            }

            public Box From { get; }
            public Box To { get; }
            public Wiring Kind { get; }
            public Path? Drawn { get; set; }
        }

        private const double BOX_WIDE = 208;
        private const double ROW_TALL = 22;
        private const double HEAD_TALL = 38;
        private const int WRAP = 11;

        private readonly MapSpawns.Map _map;
        private readonly List<Box> _boxes = new List<Box>();
        private readonly List<Wire> _wires = new List<Wire>();

        private Tab _tab = Tab.Lock;
        private bool _showAll;
        private bool _readOnly;

        /// <summary>
        /// For the unwelded inspection copy: every tab still reads, nothing can be drawn or cut.
        /// </summary>
        public void readOnly()
        {
            _readOnly = true;
            say(R.WIRING_READ_ONLY);
            detail();
        }

        private Box? _chosenBox;
        private Wire? _chosenWire;

        private Point _grabbed;
        private bool _panning;
        private Box? _moving;
        private Spot? _pulling;
        private Path? _ghost;
        private Spot? _wouldLand;

        public WiringWindow(MapSpawns.Map map)
        {
            _map = map;
            InitializeComponent();

            setStrings();
            rebuild();

            Loaded += (s, e) => { stage.Focus(); arrange(); fit(); };
        }

        /// <summary>What the caller should do once something has been changed.</summary>
        public event Action? Changed;

        /// <summary>
        /// What each wire looks like, what it writes, and whether anybody draws it.
        ///
        /// `hand` is the whole point of the window. Four of these ten have a button in the app
        /// whose only job is that wire; the other six the app writes as a side effect of adding
        /// the thing, and there is no way to have the thing without them.
        /// </summary>
        private static readonly Dictionary<Wiring, (Color colour, string name, string writes, bool hand)> LOOK =
            new Dictionary<Wiring, (Color, string, string, bool)>
        {
            { Wiring.Lock,    (Color.FromRgb(0xF5, 0xD6, 0x6F), "Lock",    "objectives[].click.locked-doors", true) },
            { Wiring.Link,    (Color.FromRgb(0x7E, 0x9B, 0xB5), "Teleport", "tiles[].teleports[].exit", true) },
            { Wiring.Descent, (Color.FromRgb(0x9B, 0x8B, 0xD0), "Descent", "tiles[].teleports[].dungeons", true) },
            { Wiring.Mobs,    (Color.FromRgb(0x4F, 0xD1, 0xC5), "Mobs",    "killgroup.mobs / arena.waves", true) },

            { Wiring.Target,  (Color.FromRgb(0x8F, 0xD6, 0x94), "Target",  "click.locations / gauntlet.end-region", false) },
            { Wiring.Key,     (Color.FromRgb(0xE8, 0xA0, 0xC0), "Key",     "click.key-locations", false) },
            { Wiring.Arena,   (Color.FromRgb(0x4F, 0xD1, 0xC5), "Arena",   "killgroup.spawn-regions", false) },
            { Wiring.Seal,    (Color.FromRgb(0xE9, 0x6E, 0x31), "Seal",    "arena.gate.regions", false) },
            { Wiring.Trigger, (Color.FromRgb(0xE9, 0x6E, 0x31), "Trigger", "challenges[].trigger", false) },
            { Wiring.Reward,  (Color.FromRgb(0xC9, 0xA2, 0x27), "Reward",  "challenges[].reward.region", false) },
        };

        /// <summary>Which wires each tab is about. The first one is what its senders pull.</summary>
        private static readonly Dictionary<Tab, Wiring[]> SHOWS = new Dictionary<Tab, Wiring[]>
        {
            { Tab.Lock,       new[] { Wiring.Lock } },
            { Tab.Teleport,   new[] { Wiring.Link } },
            { Tab.Descent,    new[] { Wiring.Descent } },
            { Tab.Mobs,       new[] { Wiring.Mobs } },
            { Tab.Objectives, new[] { Wiring.Target, Wiring.Key, Wiring.Lock, Wiring.Arena, Wiring.Seal } },
            { Tab.Challenges, new[] { Wiring.Trigger, Wiring.Mobs, Wiring.Seal, Wiring.Reward } },
        };

        private static Brush brushFor(Wiring kind) => new SolidColorBrush(LOOK[kind].colour);

        /// <summary>
        /// A themed brush, or a sane one when the theme is not there to ask.
        ///
        /// FindResource throws, and the boxes are built in the constructor - before the window
        /// is in a visual tree at all. That is fine when a person opens the window and fatal
        /// under PROBE_WIRING, which builds every tab without ever showing one. Asking politely
        /// costs nothing and turns a crash into the right colour anyway.
        /// </summary>
        private Brush themed(string key, byte red, byte green, byte blue)
            => TryFindResource(key) as Brush ?? new SolidColorBrush(Color.FromRgb(red, green, blue));

        private void setStrings()
        {
            Title = R.WIRING_TITLE;
            arrangeButton.Content = R.WIRING_ARRANGE;
            fitButton.Content = R.WIRING_FIT;
            showAllBox.Content = R.WIRING_SHOW_ALL;
        }

        // ------------------------------------------------------------------ reading the map

        private Box add(string kind, string key, string title, string note, bool sends)
        {
            var box = new Box(kind, key, title) { Note = note, Sends = sends };
            _boxes.Add(box);
            return box;
        }

        private Box? boxFor(string key)
            => _boxes.FirstOrDefault(one => string.Equals(one.Key, key, StringComparison.OrdinalIgnoreCase));

        private void join(Box? from, Wiring kind, Box? to)
        {
            if (from == null || to == null) { return; }
            if (_wires.Any(one => one.From == from && one.To == to && one.Kind == kind)) { return; }
            _wires.Add(new Wire(from, to, kind));
        }

        /// <summary>
        /// Builds the tab that is up, and only that tab.
        ///
        /// Read fresh every time anything changes rather than patched in place. The file is the
        /// truth and there is not enough of it to be slow about, and a graph kept in step by hand
        /// is a graph that disagrees with the file the first time an edit does something
        /// unexpected.
        /// </summary>
        private void rebuild()
        {
            var was = _boxes.ToDictionary(one => one.Kind + "/" + one.Key, one => (one.X, one.Y));

            _boxes.Clear();
            _wires.Clear();
            _chosenBox = null;
            _chosenWire = null;

            switch (_tab)
            {
                case Tab.Lock: buildLock(); break;
                case Tab.Teleport: buildTeleport(); break;
                case Tab.Descent: buildDescent(); break;
                case Tab.Mobs: buildMobs(); break;
                case Tab.Objectives: buildObjectives(); break;
                case Tab.Challenges: buildChallenges(); break;
            }

            /* Senders and receivers are not symmetrical, and pretending they were is what made
               the Lock tab unreadable. A gate nothing opens is NEWS - it is never shut, which is
               a bug. A step that opens nothing is the ordinary case: three of Creeper Woods'
               five objectives lock nothing. So a sender with no wire is hidden unless asked for,
               and every receiver stays. In the Teleport tab nothing hides either way, because
               there a door is both. */
            if (!_showAll)
            {
                _boxes.RemoveAll(one => one.Sends
                    && !_wires.Any(wire => wire.From == one || wire.To == one));
            }

            /* The two reading tabs are the exception to that, in the other direction. They are
               not for wiring, so a receiver nothing names has no job there - and the gate and
               group lists are the whole level's, which on Creeper Woods put thirty-three boxes
               into the Objectives tab that its five steps have nothing to do with. Show all
               brings them back for the same reason it does anywhere else. */
            if (!_showAll && (_tab == Tab.Objectives || _tab == Tab.Challenges))
            {
                _boxes.RemoveAll(one => !one.Sends
                    && !_wires.Any(wire => wire.From == one || wire.To == one));
            }

            //Pins, from what each box is allowed to do in this tab.
            foreach (var box in _boxes)
            {
                if (!box.Sends || _wires.Any(one => one.To == box))
                {
                    box.Spots.Add(new Spot(box, SHOWS[_tab][0], intoWord(box), false));
                }

                if (!box.Sends) { continue; }

                foreach (var kind in SHOWS[_tab])
                {
                    if (!canSend(box, kind)) { continue; }
                    box.Spots.Add(new Spot(box, kind, LOOK[kind].name.ToLowerInvariant(), true));
                }
            }

            foreach (var box in _boxes)
            {
                if (was.TryGetValue(box.Kind + "/" + box.Key, out var where))
                {
                    box.X = where.X;
                    box.Y = where.Y;
                }
            }

            paint();
            tabs();
            detail();
        }

        /// <summary>What actually landed on the one input pin, rather than what could.</summary>
        private string intoWord(Box box)
        {
            var landed = _wires.FirstOrDefault(one => one.To == box);
            if (landed == null) { return box.Sends ? "in" : "unwired"; }

            return landed.Kind switch
            {
                Wiring.Lock => "opened by",
                Wiring.Link => "arrive here",
                Wiring.Descent => "entered by",
                Wiring.Mobs => "fought by",
                Wiring.Target => "named by",
                Wiring.Key => "the key for",
                Wiring.Arena => "fought on",
                Wiring.Seal => "sealed by",
                Wiring.Trigger => "starts",
                Wiring.Reward => "paid at",
                _ => "in",
            };
        }

        private static bool canSend(Box box, Wiring kind) => kind switch
        {
            Wiring.Lock => box.Kind == "step",
            Wiring.Link => box.Kind == "door",
            Wiring.Descent => box.Kind == "door",
            Wiring.Mobs => box.Kind == "fight" || box.Kind == "challenge",
            Wiring.Target => box.Kind == "step",
            Wiring.Key => box.Kind == "step",
            Wiring.Arena => box.Kind == "fight",
            Wiring.Seal => box.Kind == "fight" || box.Kind == "challenge",
            Wiring.Trigger => box.Kind == "challenge",
            Wiring.Reward => box.Kind == "challenge",
            _ => false,
        };

        private static string leaf(string named)
        {
            var bits = named.Split('.').Where(one => one.Length > 0 && one != "*").ToArray();
            return bits.Length == 0 ? named : bits[^1];
        }

        /// <summary>
        /// Every gate region in the map, plus every gate an objective NAMES.
        ///
        /// The second half is what makes the Lock tab true on an unwelded copy. A gate region
        /// lives in a tile file, and an unwelded export only carries the tiles its pinned
        /// stretches named - so `start.*.objectivegate` is held shut by an objective and is
        /// nowhere in the rooms. Leaving it out draws a step that locks nothing, which is a lie
        /// about the level rather than a gap in the export.
        /// </summary>
        private void addGates()
        {
            foreach (var room in _map.Rooms)
            {
                foreach (var gate in MapSpawns.gatesOf(_map, room))
                {
                    if (string.IsNullOrWhiteSpace(gate.Name)) { continue; }
                    if (boxFor("gate/" + gate.Name) != null) { continue; }

                    var box = add("gate", "gate/" + gate.Name, gate.Name, room.Id, false);
                    box.Name = gate.Name;
                    box.Room = room;
                }
            }

            foreach (var step in MapSpawns.objectivesOf(_map))
            {
                foreach (var name in MapSpawns.lockedBy(_map, step.At))
                {
                    if (string.IsNullOrWhiteSpace(name)) { continue; }
                    if (boxFor("gate/" + name) != null) { continue; }

                    var box = add("gate", "gate/" + name, name, R.WIRING_NOT_HERE, false);
                    box.Name = name;
                }
            }
        }

        /// <summary>What holds each gate shut, read off the objectives.</summary>
        private void addLocks()
        {
            //Asked of the objective rather than of the region. gatesOf reports the holder by its
            //TITLE and the file names no index, so matching back through the title works only
            //while every step has a different one - and it cannot work at all when the gate's
            //region was never exported. lockedBy reads locked-doors straight off the body, and
            //the kill-group's gate beside it.
            //Clicks only. lockedBy also hands back a kill-group's gate, which is a SEAL - shut
            //while the fight runs, open when it ends - and the Objectives tab draws it as one
            //from arenasOf. Taking it here as well drew the same gate twice under two names on
            //Squid Coast and Arch Haven, the only two shipped levels with kill-group objectives.
            foreach (var step in MapSpawns.objectivesOf(_map))
            {
                if (!step.CanHoldGates) { continue; }

                foreach (var name in MapSpawns.lockedBy(_map, step.At))
                {
                    join(boxFor("step/" + step.At), Wiring.Lock, boxFor("gate/" + name));
                }
            }
        }

        private void buildLock()
        {
            foreach (var step in MapSpawns.objectivesOf(_map))
            {
                //Only a click can hold a gate. locked-doors appears in sixty click bodies across
                //the game's missions and in no other kind, so a reach step has nothing to pull.
                if (!step.CanHoldGates) { continue; }

                var box = add("step", "step/" + step.At, step.Title, step.Kind, true);
                box.At = step.At;
            }

            addGates();
            addLocks();
        }
        /// <summary>
        /// Every door the level's teleports mention, whichever tile it belongs to.
        ///
        /// Read out of level.json rather than out of the loaded rooms, and that distinction is
        /// the whole reason Inspect unwelded exists. A Room is a stretch that names exactly ONE
        /// tile - a stretch still picking from a tile-group has no single room to show - so an
        /// unwelded Creeper Woods loads six rooms out of a hundred and fifty-six tile entries.
        /// Going through the rooms would show six tiles' worth of teleports and call it the
        /// level.
        ///
        /// The teleports carry both ends by name, which is all a wiring view needs. A door on a
        /// tile no room was loaded for gets no Room, and dragging from it is refused rather than
        /// written somewhere wrong - reading is what that copy is for.
        /// </summary>
        private Box doorBox(string tile, string name, MapSpawns.Room? room)
        {
            //Keyed by tile as well as by name: two tiles can each have a door called "travel",
            //and merging them would draw a teleport between rooms that never touch.
            var key = "door/" + tile + "/" + name;
            var found = boxFor(key);
            if (found != null)
            {
                found.Room ??= room;
                return found;
            }

            var box = add("door", key, name, tile, true);
            box.Name = name;
            box.Room = room;
            return box;
        }

        /// <summary>Each tile row the level declares, with its room when one was loaded.</summary>
        private IEnumerable<(string id, JsonObject tile, MapSpawns.Room? room)> tilesOf()
        {
            foreach (var one in _map.Level["tiles"] as JsonArray ?? new JsonArray())
            {
                if (one is not JsonObject tile) { continue; }
                var id = tile["id"]?.GetValue<string>();
                if (string.IsNullOrEmpty(id)) { continue; }

                yield return (id!, tile, _map.Rooms.FirstOrDefault(room =>
                    string.Equals(room.Id, id, StringComparison.OrdinalIgnoreCase)));
            }
        }

        /// <summary>The door a teleport entry comes out at, or null.</summary>
        private static string? exitOf(JsonObject port)
            => port["exit"] is JsonValue value && value.TryGetValue<string>(out var said)
                ? said : null;

        private void buildTeleport()
        {
            foreach (var (id, tile, room) in tilesOf())
            {
                foreach (var one in tile["teleports"] as JsonArray ?? new JsonArray())
                {
                    if (one is not JsonObject port) { continue; }

                    var name = port["door"]?.GetValue<string>();
                    if (string.IsNullOrWhiteSpace(name)) { continue; }

                    var from = doorBox(id, name!, room);
                    var exit = exitOf(port);

                    //The far end is named within the SAME tile row, so it is looked up there.
                    if (!string.IsNullOrEmpty(exit))
                    {
                        join(from, Wiring.Link, doorBox(id, leaf(exit!), room));
                    }
                }
            }

            //Doors carrying no teleport at all still belong here - they are what a new one would
            //be drawn between - but only where a room was loaded to name them.
            foreach (var room in _map.Rooms)
            {
                foreach (var door in MapSpawns.doorsOf(_map, room))
                {
                    //A teleport points at a door by writing "*.*.<name>", so a door with no name
                    //cannot be the far end of anything and a box for it is a box nothing can ever
                    //land on. A welded Creeper Woods has about forty of them.
                    if (string.IsNullOrWhiteSpace(door.Name)) { continue; }
                    doorBox(room.Id, door.Name, room);
                }
            }
        }

        private void buildDescent()
        {
            foreach (var (id, tile, room) in tilesOf())
            {
                foreach (var one in tile["teleports"] as JsonArray ?? new JsonArray())
                {
                    if (one is JsonObject port)
                    {
                        var name = port["door"]?.GetValue<string>();
                        if (!string.IsNullOrWhiteSpace(name)) { doorBox(id, name!, room); }
                    }
                }
            }

            foreach (var room in _map.Rooms)
            {
                foreach (var door in MapSpawns.doorsOf(_map, room))
                {
                    if (string.IsNullOrWhiteSpace(door.Name)) { continue; }
                    doorBox(room.Id, door.Name, room);
                }
            }

            foreach (var one in _map.Level["dungeons"] as JsonArray ?? new JsonArray())
            {
                var id = (one as JsonObject)?["id"]?.GetValue<string>();
                if (string.IsNullOrEmpty(id) || boxFor("area/" + id) != null) { continue; }

                var stretches = ((one as JsonObject)?["stretches"] as JsonArray)?.Count ?? 0;
                var box = add("area", "area/" + id, id!, stretches + " stretch(es)", false);
                box.Name = id!;
            }

            foreach (var (id, tile, room) in tilesOf())
            {
                foreach (var one in tile["teleports"] as JsonArray ?? new JsonArray())
                {
                    if (one is not JsonObject port) { continue; }

                    var name = port["door"]?.GetValue<string>();
                    if (string.IsNullOrWhiteSpace(name)) { continue; }

                    foreach (var into in port["dungeons"] as JsonArray ?? new JsonArray())
                    {
                        //Either a bare name or an object carrying one beside a weight. Creeper
                        //Woods' travel doors are full of the second form.
                        var wanted = into is JsonObject body
                            ? body["id"]?.GetValue<string>()
                            : into is JsonValue value
                              && value.TryGetValue<string>(out var said) ? said : null;

                        if (string.IsNullOrEmpty(wanted)) { continue; }

                        //A name the level declares no dungeon for. Six of Creeper Woods' forty
                        //eight descents point at the literal "@hyperdungeon" - not a dungeon id
                        //at all, but a placeholder an Ancient Hunt substitutes itself into when
                        //the level is played as part of one. Dropping them would hide both the
                        //hunt and a genuine typo, which look identical in the file.
                        var area = boxFor("area/" + wanted!);
                        if (area == null)
                        {
                            area = add("area", "area/" + wanted!, wanted!,
                                wanted!.StartsWith("@", StringComparison.Ordinal)
                                    ? R.WIRING_RUNTIME : R.WIRING_UNDECLARED, false);
                            area.Name = wanted!;
                        }

                        join(doorBox(id, name!, room), Wiring.Descent, area);
                    }
                }
            }
        }

        /// <summary>
        /// The box for a mob group, made if nothing has made it yet.
        ///
        /// Wires used to land only on groups addGroups had already drawn, and that list is
        /// MapSpawns.usage - which does not hold every group a fight can name. Checked against
        /// all 56 shipped levels, that lost five of Squid Coast's seven fight-to-group wires,
        /// three of the Tower's and Warped Forest's `warped-ambush`: a wire with no box to land
        /// on was simply not drawn, and a missing wire looks exactly like a fight with no mobs.
        /// </summary>
        private Box groupBox(string name)
        {
            var found = boxFor("group/" + name);
            if (found != null) { return found; }

            var box = add("group", "group/" + name, name, "mob group", false);
            box.Name = name;
            return box;
        }

        /// <summary>
        /// The box for a gate region, made if nothing has made it yet - the same trap as
        /// <see cref="groupBox"/>. Gate boxes came only from the rooms and from what objectives
        /// lock, so a challenge's own gate had nowhere to land: every one of the 52 challenge
        /// seals in the shipped game went undrawn.
        /// </summary>
        private Box gateBox(string name)
        {
            var found = boxFor("gate/" + name);
            if (found != null) { return found; }

            var box = add("gate", "gate/" + name, name, R.WIRING_NOT_HERE, false);
            box.Name = name;
            return box;
        }

        private void addGroups()
        {
            foreach (var id in MapSpawns.usage(_map.Level).Keys
                .OrderBy(one => one, StringComparer.OrdinalIgnoreCase))
            {
                if (boxFor("group/" + id) != null) { continue; }
                var box = add("group", "group/" + id, id, "mob group", false);
                box.Name = id;
            }
        }

        private void buildMobs()
        {
            foreach (var fight in MapSpawns.arenasOf(_map))
            {
                var box = add("fight", "fight/" + fight.At, fight.Title,
                    fight.Count + " × " + fight.Group, true);
                box.At = fight.At;
            }

            foreach (var fight in MapSpawns.challengesOf(_map))
            {
                var box = add("challenge", "challenge/" + fight.At, fight.Id,
                    "challenge · " + fight.Waves.Length + " wave(s)", true);
                box.At = fight.At;
            }

            addGroups();

            foreach (var fight in MapSpawns.arenasOf(_map))
            {
                if (fight.Group.Length > 0) { join(boxFor("fight/" + fight.At), Wiring.Mobs, groupBox(fight.Group)); }
            }

            foreach (var fight in MapSpawns.challengesOf(_map))
            {
                foreach (var group in fight.Groups)
                {
                    join(boxFor("challenge/" + fight.At), Wiring.Mobs, groupBox(group));
                }
            }
        }

        /// <summary>
        /// The mission, and everything its steps name.
        ///
        /// For reading rather than for wiring - four of the five wires here the app wrote with
        /// the step, and the fifth, Lock, has its own tab. This is the only place the automatic
        /// ones are drawn at all, and the question it answers is the one the other tabs cannot:
        /// what does this step actually do.
        /// </summary>
        private void buildObjectives()
        {
            foreach (var step in MapSpawns.objectivesOf(_map))
            {
                var box = add("step", "step/" + step.At, step.Title, step.Kind, true);
                box.At = step.At;
            }

            addGates();
            addGroups();

            //What each step names. Needs is the region list the objective body carries.
            //
            //Not for a fight. A kill-group's Needs holds its spawn regions and its marker, and
            //those are drawn below as Arena from arenasOf - reading them here as well drew each
            //one twice, once as a Target it is not.
            foreach (var step in MapSpawns.objectivesOf(_map))
            {
                if (string.Equals(step.Kind, "fight", StringComparison.Ordinal)) { continue; }

                foreach (var named in step.Needs)
                {
                    var name = leaf(named);
                    if (name.Length == 0) { continue; }

                    var gate = boxFor("gate/" + name);
                    var to = gate ?? boxFor("spot/" + name);

                    if (to == null)
                    {
                        to = add("spot", "spot/" + name, name, named, false);
                        to.Name = name;
                    }

                    //A location is a Target even when the region happens to be a gate as well.
                    //Holding a gate shut is a separate field, locked-doors, drawn by addLocks -
                    //deciding it from the region's tags drew a click's own location as a Lock.
                    join(boxFor("step/" + step.At), Wiring.Target, to);
                }
            }

            //`locked-doors` is not in Objective.Needs, which carries the regions a step NAMES:
            //a gate is a region a step holds rather than one it points at. Same reading as the
            //Lock tab.
            addLocks();

            foreach (var fight in MapSpawns.arenasOf(_map))
            {
                var from = boxFor("step/" + fight.At);
                if (fight.Group.Length > 0) { join(from, Wiring.Mobs, groupBox(fight.Group)); }

                foreach (var wall in fight.Gates) { join(from, Wiring.Seal, gateBox(wall)); }

                foreach (var ground in fight.From)
                {
                    var box = boxFor("spot/" + ground);
                    if (box == null)
                    {
                        box = add("spot", "spot/" + ground, ground, "spawn region", false);
                        box.Name = ground;
                    }
                    join(from, Wiring.Arena, box);
                }
            }

            foreach (var door in MapSpawns.keyedOf(_map))
            {
                var from = boxFor("step/" + door.At);
                foreach (var where in door.Keys)
                {
                    var box = boxFor("spot/" + where);
                    if (box == null)
                    {
                        box = add("spot", "spot/" + where, where, "key spot", false);
                        box.Name = where;
                    }
                    join(from, Wiring.Key, box);
                }
            }
        }

        private void buildChallenges()
        {
            foreach (var fight in MapSpawns.challengesOf(_map))
            {
                var box = add("challenge", "challenge/" + fight.At, fight.Id,
                    fight.Waves.Length + " wave(s)", true);
                box.At = fight.At;
            }

            addGates();
            addGroups();

            foreach (var fight in MapSpawns.challengesOf(_map))
            {
                var from = boxFor("challenge/" + fight.At);

                if (fight.Trigger.Length > 0)
                {
                    var box = boxFor("spot/" + fight.Trigger);
                    if (box == null)
                    {
                        box = add("spot", "spot/" + fight.Trigger, fight.Trigger,
                            "trigger region", false);
                        box.Name = fight.Trigger;
                    }
                    join(from, Wiring.Trigger, box);
                }

                foreach (var group in fight.Groups)
                {
                    join(from, Wiring.Mobs, groupBox(group));
                }

                foreach (var wall in fight.Gates) { join(from, Wiring.Seal, gateBox(wall)); }

                if (fight.Reward.Length > 0)
                {
                    var box = boxFor("reward/" + fight.Reward);
                    if (box == null)
                    {
                        box = add("reward", "reward/" + fight.Reward, fight.Reward,
                            leaf(fight.Chest.Replace('/', '.')), false);
                        box.Name = fight.Reward;
                    }
                    join(from, Wiring.Reward, box);
                }
            }
        }

        /// <summary>
        /// Every tab's shape, without a screen.
        ///
        /// A wiring view that reads the map wrongly looks exactly like a level that is wired
        /// wrongly, and the only way to tell them apart is to check the numbers against the file.
        /// PROBE_WIRING does that for all six at once.
        /// </summary>
        /// <summary>
        /// Every tab's wires, counted by kind, with show-all on - one row per tab and kind. The
        /// machine-readable half of <see cref="probeTabs"/>, for checking all 56 shipped levels
        /// against a count taken straight out of their JSON by something that is not this window.
        /// </summary>
        internal IEnumerable<(string tab, string kind, int count)> probeCounts()
        {
            var was = (_tab, _showAll);

            foreach (Tab which in Enum.GetValues(typeof(Tab)))
            {
                _tab = which;
                _showAll = true;
                rebuild();

                foreach (var group in _wires.GroupBy(one => one.Kind))
                {
                    yield return (which.ToString(), LOOK[group.Key].name, group.Count());
                }
            }

            (_tab, _showAll) = was;
            rebuild();
        }

        internal string probeTabs()
        {
            var said = new System.Text.StringBuilder();

            foreach (Tab which in Enum.GetValues(typeof(Tab)))
            {
                foreach (var all in new[] { false, true })
                {
                    _tab = which;
                    _showAll = all;
                    rebuild();

                    var sends = _boxes.Count(one => one.Sends);
                    var wired = _boxes.Count(one => _wires.Any(w => w.From == one || w.To == one));

                    said.Append(which.ToString().PadRight(12));
                    said.Append(all ? "show all  " : "default   ");
                    said.Append(_wires.Count.ToString().PadLeft(4) + " wires  ");
                    said.Append(_boxes.Count.ToString().PadLeft(4) + " boxes  ");
                    said.Append(sends.ToString().PadLeft(4) + " send  ");
                    said.AppendLine(wired.ToString().PadLeft(4) + " touched");
                }

                var byKind = _wires.GroupBy(one => one.Kind)
                    .OrderByDescending(one => one.Count());

                foreach (var group in byKind)
                {
                    said.AppendLine("             " + LOOK[group.Key].name.PadRight(9)
                                    + group.Count());
                }
            }

            return said.ToString();
        }

        // ------------------------------------------------------------------ the tab strip

        private void tabs()
        {
            tabsPanel.Children.Clear();

            foreach (Tab which in Enum.GetValues(typeof(Tab)))
            {
                var mine = which;
                var on = which == _tab;

                var button = new Button
                {
                    Content = which.ToString(),
                    Padding = new Thickness(13, 7, 13, 7),
                    Margin = new Thickness(0),
                    BorderThickness = new Thickness(0, 0, 0, on ? 2 : 0),
                    BorderBrush = brushFor(SHOWS[which][0]),
                    Background = Brushes.Transparent,
                    FontWeight = on ? FontWeights.SemiBold : FontWeights.Normal,
                    Opacity = on ? 1 : 0.62,
                };

                button.Click += (s, e) =>
                {
                    if (_tab == mine) { return; }
                    _tab = mine;
                    rebuild();
                    arrange();
                    fit();
                };

                tabsPanel.Children.Add(button);
            }

            var drawn = _wires.Count;
            var shown = _boxes.Count;
            var could = _boxes.Count(one => one.Sends);

            whereLabel.Text = string.Format(R.WIRING_WHERE, shown, drawn);
            countLabel.Text = string.Format(R.WIRING_COUNT, drawn, shown, shown + could);
        }

        private void showAll_Changed(object sender, RoutedEventArgs e)
        {
            _showAll = showAllBox.IsChecked == true;
            rebuild();
            arrange();
            fit();
        }

        // ------------------------------------------------------------------ drawing

        private void paint()
        {
            sheet.Children.Clear();

            foreach (var wire in _wires) { draw(wire); }
            foreach (var box in _boxes) { draw(box); }

            place();
        }

        private void draw(Box box)
        {
            var body = new StackPanel();

            var head = new StackPanel { Margin = new Thickness(9, 6, 9, 5) };
            head.Children.Add(new TextBlock
            {
                Text = box.Title,
                FontWeight = FontWeights.SemiBold,
                FontSize = 12.5,
                TextTrimming = TextTrimming.CharacterEllipsis,
            });

            if (box.Note.Length > 0)
            {
                head.Children.Add(new TextBlock
                {
                    Text = box.Note,
                    FontSize = 10.5,
                    Opacity = 0.62,
                    TextTrimming = TextTrimming.CharacterEllipsis,
                });
            }

            body.Children.Add(head);

            var down = HEAD_TALL;
            foreach (var spot in box.Spots)
            {
                var row = new Grid { Height = ROW_TALL };
                var text = new TextBlock
                {
                    Text = spot.Label,
                    FontSize = 10.5,
                    Opacity = LOOK[spot.Kind].hand ? 0.95 : 0.6,
                    VerticalAlignment = VerticalAlignment.Center,
                    Margin = new Thickness(11, 0, 11, 0),
                    HorizontalAlignment = spot.Right ? HorizontalAlignment.Right
                                                     : HorizontalAlignment.Left,
                };
                row.Children.Add(text);
                body.Children.Add(row);

                spot.Down = down + ROW_TALL / 2;
                down += ROW_TALL;

                //An automatic wire gets a square stub rather than a socket, because there is
                //nothing to pull: the app writes it with the thing and cannot write the thing
                //without it.
                var dot = new Ellipse
                {
                    Width = LOOK[spot.Kind].hand ? 11 : 8,
                    Height = LOOK[spot.Kind].hand ? 11 : 8,
                    Stroke = brushFor(spot.Kind),
                    StrokeThickness = 2,
                    Fill = Brushes.Transparent,
                    Cursor = LOOK[spot.Kind].hand && spot.Right ? Cursors.Cross : Cursors.Arrow,
                    Tag = spot,
                    Opacity = LOOK[spot.Kind].hand ? 1 : 0.55,
                };

                spot.Dot = dot;
                sheet.Children.Add(dot);
            }

            var el = new Border
            {
                Width = BOX_WIDE,
                Child = body,
                CornerRadius = new CornerRadius(7),
                BorderThickness = new Thickness(1),
                BorderBrush = themed("Brush.Border", 0x26, 0x30, 0x3E),
                Background = themed("Brush.Background", 0x10, 0x14, 0x1B),
                Tag = box,
            };

            box.El = el;
            sheet.Children.Add(el);
        }

        private Point anchor(Spot spot, double towards)
        {
            var box = spot.Owner;

            //Which edge a wire leaves by is decided here rather than when the pin is made, so a
            //door that happens to sit right of the thing it points at still reads left to right.
            var right = spot.Right || towards > box.X + BOX_WIDE / 2;
            return new Point(box.X + (right ? BOX_WIDE : 0), box.Y + spot.Down);
        }

        private static PathGeometry curve(Point a, Point b)
        {
            var pull = Math.Max(40, Math.Abs(b.X - a.X) * 0.42);
            var figure = new PathFigure { StartPoint = a };
            figure.Segments.Add(new BezierSegment(
                new Point(a.X + pull, a.Y), new Point(b.X - pull, b.Y), b, true));

            var made = new PathGeometry();
            made.Figures.Add(figure);
            return made;
        }

        private void draw(Wire wire)
        {
            var path = new Path
            {
                Stroke = brushFor(wire.Kind),
                StrokeThickness = LOOK[wire.Kind].hand ? 2.4 : 1.8,
                StrokeEndLineCap = PenLineCap.Round,
                StrokeStartLineCap = PenLineCap.Round,
                Opacity = LOOK[wire.Kind].hand ? 0.95 : 0.7,
                Tag = wire,
            };

            if (!LOOK[wire.Kind].hand)
            {
                path.StrokeDashArray = new DoubleCollection { 3, 2 };
            }

            wire.Drawn = path;
            sheet.Children.Add(path);
        }

        /// <summary>Moves what is already drawn. No rebuilding, so dragging stays smooth.</summary>
        private void place()
        {
            foreach (var box in _boxes)
            {
                if (box.El == null) { continue; }

                Canvas.SetLeft(box.El, box.X);
                Canvas.SetTop(box.El, box.Y);
                Canvas.SetZIndex(box.El, 2);

                foreach (var spot in box.Spots)
                {
                    if (spot.Dot == null) { continue; }
                    var at = anchor(spot, spot.Right ? double.MaxValue : double.MinValue);
                    Canvas.SetLeft(spot.Dot, at.X - spot.Dot.Width / 2);
                    Canvas.SetTop(spot.Dot, at.Y - spot.Dot.Height / 2);
                    Canvas.SetZIndex(spot.Dot, 3);
                }
            }

            foreach (var wire in _wires)
            {
                if (wire.Drawn == null) { continue; }

                var from = wire.From.Out(wire.Kind) ?? wire.From.Spots.LastOrDefault();
                var to = wire.To.In ?? wire.To.Spots.FirstOrDefault();
                if (from == null || to == null) { continue; }

                var a = anchor(from, wire.To.X);
                var b = anchor(to, wire.From.X);

                wire.Drawn.Data = curve(a, b);
                wire.Drawn.StrokeThickness = wire == _chosenWire
                    ? 3.4 : LOOK[wire.Kind].hand ? 2.4 : 1.8;
                Canvas.SetZIndex(wire.Drawn, 1);
            }

            foreach (var box in _boxes)
            {
                if (box.El == null) { continue; }
                box.El.BorderBrush = box == _chosenBox
                    ? themed("Brush.Text", 0xF2, 0xF2, 0xF2)
                    : themed("Brush.Border", 0x26, 0x30, 0x3E);
            }
        }

        // ------------------------------------------------------------------ the inspector

        private void detail()
        {
            detailPanel.Children.Clear();

            void line(string what, bool dim = false, bool bold = false)
                => detailPanel.Children.Add(new TextBlock
                {
                    Text = what,
                    TextWrapping = TextWrapping.Wrap,
                    Margin = new Thickness(0, 0, 0, 6),
                    FontSize = dim ? 11.5 : 12.5,
                    FontWeight = bold ? FontWeights.SemiBold : FontWeights.Normal,
                    Opacity = dim ? 0.7 : 1,
                });

            if (_chosenWire != null)
            {
                var look = LOOK[_chosenWire.Kind];
                line(look.name, bold: true);
                line(_chosenWire.From.Title + "  ->  " + _chosenWire.To.Title);
                line(R.WIRING_WRITES + " " + look.writes, dim: true);

                if (!look.hand)
                {
                    line(R.WIRING_FIXED, dim: true);
                    return;
                }

                if (_readOnly) { return; }

                var cut = new Button { Content = R.WIRING_CUT, Padding = new Thickness(10, 4, 10, 4) };
                cut.Click += (s, e) => cutChosen();
                detailPanel.Children.Add(cut);
                return;
            }

            if (_chosenBox != null)
            {
                line(_chosenBox.Title, bold: true);
                line(_chosenBox.Note, dim: true);

                var mine = _wires.Where(one => one.From == _chosenBox || one.To == _chosenBox)
                    .ToList();

                foreach (var wire in mine)
                {
                    line(wire.From == _chosenBox
                        ? LOOK[wire.Kind].name + " -> " + wire.To.Title
                        : LOOK[wire.Kind].name + " <- " + wire.From.Title, dim: true);
                }

                if (mine.Count == 0) { line(R.WIRING_HELP_PULL, dim: true); }
                return;
            }

            //What is wrong with the level comes first when nothing is picked, because it is
            //the question the window is usually opened to answer.
            line(R.WIRING_PROBLEMS, bold: true);

            var problems = MapChecks.run(_map);
            if (problems.Count == 0) { line(R.WIRING_NO_PROBLEMS, dim: true); }

            foreach (var one in problems)
            {
                detailPanel.Children.Add(new TextBlock
                {
                    Text = (one.Severity == MapChecks.Severity.Bad ? "\u2716 " : "\u26A0 ") + one.What,
                    TextWrapping = TextWrapping.Wrap,
                    FontWeight = FontWeights.SemiBold,
                    FontSize = 12,
                    Margin = new Thickness(0, 4, 0, 1),
                    Foreground = new SolidColorBrush(one.Severity == MapChecks.Severity.Bad
                        ? Color.FromRgb(0xFF, 0x6B, 0x5A) : Color.FromRgb(0xF5, 0xA6, 0x5B)),
                });
                line(one.Why, dim: true);
            }

            detailPanel.Children.Add(new Separator { Margin = new Thickness(0, 8, 0, 10) });

            line(R.WIRING_HELP_TITLE, bold: true);
            line(R.WIRING_HELP_PULL, dim: true);
            line(R.WIRING_HELP_DROP, dim: true);
            line(R.WIRING_HELP_CUT, dim: true);
        }

        // ------------------------------------------------------------------ pointer

        private Point where(MouseEventArgs e)
        {
            var at = e.GetPosition(stage);
            return new Point((at.X - pan.X) / zoom.ScaleX, (at.Y - pan.Y) / zoom.ScaleY);
        }

        private Spot? spotUnder(Point at)
            => _boxes.SelectMany(one => one.Spots).FirstOrDefault(one =>
            {
                if (one.Dot == null) { return false; }
                var middle = anchor(one, one.Right ? double.MaxValue : double.MinValue);
                return Math.Abs(middle.X - at.X) < 9 && Math.Abs(middle.Y - at.Y) < 9;
            });

        private Box? boxUnder(Point at)
            => _boxes.LastOrDefault(one =>
                at.X >= one.X && at.X <= one.X + BOX_WIDE
                && at.Y >= one.Y && at.Y <= one.Y + HEAD_TALL + one.Spots.Count * ROW_TALL);

        private void stage_Down(object sender, MouseButtonEventArgs e)
        {
            stage.Focus();
            var at = where(e);
            _grabbed = at;

            var spot = spotUnder(at);
            if (spot != null && spot.Right)
            {
                if (!LOOK[spot.Kind].hand)
                {
                    say(string.Format(R.WIRING_ONLY_READ, LOOK[spot.Kind].writes));
                    return;
                }

                _pulling = spot;
                _ghost = new Path
                {
                    Stroke = brushFor(spot.Kind),
                    StrokeThickness = 2,
                    StrokeDashArray = new DoubleCollection { 4, 3 },
                };
                sheet.Children.Add(_ghost);
                stage.CaptureMouse();
                return;
            }

            var wire = _wires.FirstOrDefault(one => one.Drawn != null
                && one.Drawn.Data != null
                && one.Drawn.Data.StrokeContains(new Pen(Brushes.Black, 9), at));

            if (wire != null)
            {
                _chosenWire = wire;
                _chosenBox = null;
                place();
                detail();
                return;
            }

            var box = boxUnder(at);
            if (box != null)
            {
                _moving = box;
                _chosenBox = box;
                _chosenWire = null;
                place();
                detail();
                stage.CaptureMouse();
                return;
            }

            _panning = true;
            _chosenBox = null;
            _chosenWire = null;
            place();
            detail();
            stage.CaptureMouse();
        }

        private void stage_Move(object sender, MouseEventArgs e)
        {
            if (e.LeftButton != MouseButtonState.Pressed) { return; }

            var at = where(e);

            if (_pulling != null && _ghost != null)
            {
                var landing = spotUnder(at);
                _wouldLand = landing != null && !landing.Right && allowed(_pulling, landing)
                    ? landing : null;

                var target = _wouldLand != null
                    ? anchor(_wouldLand, _pulling.Owner.X) : at;

                _ghost.Data = curve(anchor(_pulling, at.X), target);

                foreach (var spot in _boxes.SelectMany(one => one.Spots))
                {
                    if (spot.Dot == null || spot.Right) { continue; }
                    spot.Dot.Opacity = allowed(_pulling, spot) ? 1 : 0.15;
                    spot.Dot.StrokeThickness = spot == _wouldLand ? 4 : 2;
                }
                return;
            }

            if (_moving != null)
            {
                _moving.X += at.X - _grabbed.X;
                _moving.Y += at.Y - _grabbed.Y;
                _grabbed = at;
                place();
                return;
            }

            if (_panning)
            {
                var screen = e.GetPosition(stage);
                pan.X = screen.X - _grabbed.X * zoom.ScaleX;
                pan.Y = screen.Y - _grabbed.Y * zoom.ScaleY;
            }
        }

        private void stage_Up(object sender, MouseButtonEventArgs e)
        {
            stage.ReleaseMouseCapture();

            if (_pulling != null)
            {
                var landing = _wouldLand;
                drop();
                if (landing != null) { land(_pulling!, landing); }
                _pulling = null;
                return;
            }

            _moving = null;
            _panning = false;
        }

        private void drop()
        {
            if (_ghost != null) { sheet.Children.Remove(_ghost); }
            _ghost = null;
            _wouldLand = null;

            foreach (var spot in _boxes.SelectMany(one => one.Spots))
            {
                if (spot.Dot == null) { continue; }
                spot.Dot.Opacity = LOOK[spot.Kind].hand ? 1 : 0.55;
                spot.Dot.StrokeThickness = 2;
            }
        }

        private void stage_Wheel(object sender, MouseWheelEventArgs e)
        {
            var before = where(e);
            var k = Math.Min(1.7, Math.Max(0.12, zoom.ScaleX * (e.Delta > 0 ? 1.1 : 1 / 1.1)));
            zoom.ScaleX = zoom.ScaleY = k;

            var screen = e.GetPosition(stage);
            pan.X = screen.X - before.X * k;
            pan.Y = screen.Y - before.Y * k;
        }

        private void stage_Key(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Delete || e.Key == Key.Back) { cutChosen(); }
            if (e.Key == Key.F) { fit(); }
        }

        // ------------------------------------------------------------------ writing

        private bool allowed(Spot from, Spot to)
        {
            if (from.Owner == to.Owner) { return false; }

            return from.Kind switch
            {
                Wiring.Lock => to.Owner.Kind == "gate",
                Wiring.Link => to.Owner.Kind == "door",
                Wiring.Descent => to.Owner.Kind == "area",
                Wiring.Mobs => to.Owner.Kind == "group",
                _ => false,
            };
        }

        private void land(Spot from, Spot to)
        {
            if (_readOnly) { say(R.WIRING_READ_ONLY); return; }

            var a = from.Owner;
            var b = to.Owner;

            if (!allowed(from, to)) { say(R.WIRING_NO_JOIN); return; }

            try
            {
                switch (from.Kind)
                {
                    case Wiring.Link:
                        //Both ends are written into ONE tile's row, which is what linkDoors does
                        //and why this refuses a pair that does not share a room.
                        if (a.Room == null || b.Room == null || a.Room != b.Room)
                        {
                            say(R.WIRING_SAME_TILE);
                            return;
                        }
                        MapSpawns.linkDoors(_map, a.Room, a.Name, b.Name, true, string.Empty);
                        break;

                    case Wiring.Descent:
                        if (a.Room == null) { return; }
                        MapSpawns.linkDungeon(_map, a.Room, a.Name, b.Name, string.Empty);
                        break;

                    case Wiring.Lock:
                        if (!MapSpawns.lockTo(_map, a.At, b.Name, string.Empty))
                        {
                            say(R.WIRING_NO_LOCK);
                            return;
                        }
                        break;

                    case Wiring.Mobs:
                        //The count and the reward are read back and written again unchanged, so
                        //pointing a fight at another group changes the group and nothing else.
                        if (a.Kind == "fight")
                        {
                            var was = MapSpawns.arenasOf(_map).FirstOrDefault(one => one.At == a.At);
                            if (was == null) { return; }
                            MapSpawns.reshapeArena(_map, a.At, was.Count, b.Name, was.Reward);
                        }
                        else if (a.Kind == "challenge")
                        {
                            var was = MapSpawns.challengesOf(_map).FirstOrDefault(one => one.At == a.At);
                            if (was == null) { return; }
                            MapSpawns.reshapeChallenge(_map, a.At,
                                was.Waves.Length > 0 ? was.Waves[0].count : 1, b.Name, was.Chest);
                        }
                        else { say(R.WIRING_NO_GROUP); return; }
                        break;

                    default:
                        return;
                }
            }
            catch (Exception problem)
            {
                say(problem.Message);
                return;
            }

            _map.Changed.Add("level.json");
            rebuild();
            place();
            Changed?.Invoke();
            say(string.Format(R.WIRING_MADE, LOOK[from.Kind].name));
        }

        private void cutChosen()
        {
            var wire = _chosenWire;
            if (wire == null || !LOOK[wire.Kind].hand) { return; }
            if (_readOnly) { say(R.WIRING_READ_ONLY); return; }

            try
            {
                switch (wire.Kind)
                {
                    case Wiring.Link:
                        if (wire.From.Room == null) { return; }
                        MapSpawns.unlinkDoor(_map, wire.From.Room, wire.From.Name);
                        MapSpawns.unlinkDoor(_map, wire.From.Room, wire.To.Name);
                        break;

                    case Wiring.Descent:
                        if (wire.From.Room == null) { return; }
                        MapSpawns.unlinkDungeon(_map, wire.From.Room, wire.From.Name);
                        break;

                    case Wiring.Lock:
                        MapSpawns.unlock(_map, wire.From.At, wire.To.Name);
                        break;

                    //A fight with no group is not a state the file can hold - it would spawn
                    //nothing and say nothing - so Mobs is rewired rather than cut.
                    default:
                        return;
                }
            }
            catch (Exception problem)
            {
                say(problem.Message);
                return;
            }

            _map.Changed.Add("level.json");
            rebuild();
            place();
            Changed?.Invoke();
            say(string.Format(R.WIRING_GONE, LOOK[wire.Kind].name));
        }

        private void say(string what) => statusLabel.Text = what;

        // ------------------------------------------------------------------ arranging

        private void arrangeButton_Click(object sender, RoutedEventArgs e) { arrange(); fit(); }

        private void fitButton_Click(object sender, RoutedEventArgs e) => fit();

        /// <summary>
        /// Senders down the left, receivers down the right.
        ///
        /// One-way, left to right, is the one rule that keeps a graph legible, and it is what
        /// Unreal's own guidance leads with. Wrapped into further columns past eleven, because a
        /// welded Creeper Woods puts a hundred and seventy doors in the Teleport tab and a single
        /// column of those is four thousand pixels tall.
        ///
        /// Wired first down each column, so the ones nothing has been drawn on fall to the bottom
        /// rather than being scattered through the ones that matter.
        /// </summary>
        private void arrange()
        {
            var wired = new HashSet<Box>(_wires.SelectMany(one => new[] { one.From, one.To }));

            var sides = new[]
            {
                _boxes.Where(one => one.Sends).ToList(),
                _boxes.Where(one => !one.Sends).ToList(),
            };

            var x = 40.0;

            foreach (var side in sides)
            {
                var order = side
                    .OrderByDescending(one => wired.Contains(one))
                    .ThenBy(one => one.Title, StringComparer.OrdinalIgnoreCase)
                    .ToList();

                for (var at = 0; at < order.Count; at++)
                {
                    order[at].X = x + at / WRAP * (BOX_WIDE + 44);
                    order[at].Y = 30 + at % WRAP * 104;
                }

                var columns = Math.Max(1, (order.Count + WRAP - 1) / WRAP);
                x += columns * (BOX_WIDE + 44) + 120;
            }

            place();
            say(R.WIRING_ARRANGED);
        }

        /// <summary>
        /// Frames whatever the tab is showing.
        ///
        /// A graph somebody has panned off the edge is indistinguishable from an empty one, so
        /// there is always a way back. Run after a tab change too, because the tabs are laid out
        /// to different widths and a view that framed one frames nothing in the next.
        /// </summary>
        private void fit()
        {
            if (_boxes.Count == 0)
            {
                zoom.ScaleX = zoom.ScaleY = 1;
                pan.X = 30;
                pan.Y = 20;
                say(R.WIRING_EMPTY);
                return;
            }

            var x0 = _boxes.Min(one => one.X);
            var y0 = _boxes.Min(one => one.Y);
            var x1 = _boxes.Max(one => one.X + BOX_WIDE);
            var y1 = _boxes.Max(one => one.Y + HEAD_TALL + one.Spots.Count * ROW_TALL);

            var wide = Math.Max(1, x1 - x0);
            var tall = Math.Max(1, y1 - y0);

            var room = new Size(Math.Max(100, stage.ActualWidth), Math.Max(100, stage.ActualHeight));
            const double pad = 30;

            var k = Math.Min(1.2, Math.Max(0.12,
                Math.Min((room.Width - pad * 2) / wide, (room.Height - pad * 2) / tall)));

            zoom.ScaleX = zoom.ScaleY = k;
            pan.X = (room.Width - wide * k) / 2 - x0 * k;
            pan.Y = (room.Height - tall * k) / 2 - y0 * k;
        }
    }
}
