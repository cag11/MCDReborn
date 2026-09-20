using MCDSaveEdit.Logic;
using MCDSaveEdit.Services;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
#nullable enable

namespace MCDSaveEdit.UI
{
    /// <summary>
    /// Editing what a mission spawns, and where.
    ///
    /// Seen from above, because that is the view that answers "where am I". A mission is a run of
    /// rooms and the thing somebody needs to know is which one they are looking at and what comes
    /// before it - not which of sixteen files it happens to live in.
    ///
    /// Saving writes back into the exported folder in place. That is the whole reason this is a
    /// window in the app rather than a page in a browser.
    /// </summary>
    public partial class SpawnsWindow : Window
    {
        private readonly MapSpawns.Map _map;
        private MapSpawns.Room? _room;

        /// <summary>
        /// Which build is the current one.
        ///
        /// Builds happen off the UI thread and a slider can start a dozen before the first
        /// finishes. Only the newest is allowed to reach the screen; the rest are dropped where
        /// they land rather than fighting over the view.
        /// </summary>
        private int _building;

        /// <summary>Told when a mission has been installed, so the tab behind can catch up.</summary>
        public event Action? Installed;

        private readonly GameMaps.Mission? _mission;

        public SpawnsWindow(MapSpawns.Map map, GameMaps.Mission? mission = null)
        {
            _map = map;
            _mission = mission;
            InitializeComponent();
            setStrings();

            foreach (var (tag, note) in MapSpawns.TAGS)
            {
                tagBox.Items.Add(new ComboBoxItem
                {
                    Content = (tag.Length == 0 ? "(ordinary)" : tag) + " — " + note,
                    Tag = tag,
                });
            }
            tagBox.SelectedIndex = 0;

            radiusBox.Text = "6";
            countBox.Text = "4";

            mapView.Picked += mapView_Picked;
            mapView.Hovered += mapView_Hovered;
            mapView.Confirmed += mapView_Confirmed;
            mapView.Grabbed += mapView_Grabbed;
            mapView.Dragged += mapView_Dragged;
            mapView.Dropped += mapView_Dropped;
            mapView.DragCancelled += mapView_DragCancelled;
            ceilingSlider_ValueChanged(this, new RoutedPropertyChangedEventArgs<double>(0, 255));

            fillRooms();
            fitPanels();
            fillGroups();
            draw();
            updateUI();

            statusLabel.Text = string.Join("   ·   ", _map.Notes.Take(3));
        }

        /// <summary>
        /// Whether the map actually made it onto the screen.
        ///
        /// Here so a probe can open this window and check, rather than shipping an exe and
        /// finding out from a screenshot that nothing loads. The last time the map came up empty
        /// it was because the room list - the only thing that ever chose a room - had just been
        /// hidden, which no amount of testing the layer underneath would have caught.
        /// </summary>
        internal bool mapReady => mapView.Ready;

        /// <summary>Which room the window settled on, or what it says instead.</summary>
        internal string roomChosen => roomLabel.Text;

        /// <summary>How many spawn points the chosen room holds.</summary>
        internal int spawnsShown => _room?.Spawns ?? -1;

        /// <summary>What each mob row is showing, for a probe to check none came up blank.</summary>
        internal string[] mobRows => mobStack.Children.OfType<DockPanel>()
            .Select(row => row.Children.OfType<ComboBox>().FirstOrDefault())
            .Select(one => one?.SelectedItem as string ?? one?.Text ?? "")
            .ToArray();

        /// <summary>
        /// How many mob rows are editable, which in this app means invisible.
        ///
        /// The theme's ComboBox template keeps PART_EditableTextBox collapsed, so an editable
        /// ComboBox has nowhere to draw and shows nothing however its value is set. Reading
        /// SelectedItem cannot catch that - it was correct the whole time the rows looked empty.
        /// </summary>
        internal int editableMobRows => mobStack.Children.OfType<DockPanel>()
            .Select(row => row.Children.OfType<ComboBox>().FirstOrDefault())
            .Count(one => one?.IsEditable == true);

        /// <summary>
        /// Clicks the first spawn point the room has, and says whether it was recognised.
        ///
        /// Clicking an existing point has to pick that point rather than aim at bare ground
        /// beside it, or there is no way to remove one.
        /// </summary>
        internal (bool picked, int before, int after) probeRemoveFirst()
        {
            if (_room == null) { return (false, 0, 0); }

            var before = _room.Spawns;

            foreach (var region in _room.Regions)
            {
                if (region?["type"]?.GetValue<string>() != "spawn") { continue; }
                if (region["pos"] is not JsonArray at || at.Count < 3) { continue; }

                mapView_Picked(at[0]!.GetValue<int>(), at[1]!.GetValue<int>(), at[2]!.GetValue<int>());
                if (_selected < 0) { return (false, before, before); }

                removeButton_Click(this, new RoutedEventArgs());
                return (true, before, _room.Spawns);
            }

            return (false, before, before);
        }

        /// <summary>
        /// Chooses a group other than the first, adds a mob, and reports what survived.
        ///
        /// Adding a mob refilled the group list, and refilling a list raises SelectionChanged,
        /// which snapped the panel back to the first group. The mob really was added - to the
        /// group that was chosen - but the rows on screen were now a different group's, so it
        /// looked as though the mob had been ignored and something else put in its place.
        /// </summary>
        internal (string chosen, string after, int mobs) probeAddMob()
        {
            if (groupBox.Items.Count < 2) { return ("", "", -1); }

            groupBox.SelectedIndex = 1;
            //By id, not by the label: the label carries the mob count, which is meant to change.
            var chosen = chosenGroup?["id"]?.GetValue<string>() ?? "";

            addMobButton_Click(this, new RoutedEventArgs());

            var after = chosenGroup?["id"]?.GetValue<string>() ?? "";
            var mobs = (chosenGroup?["types"] as JsonArray)?.Count ?? -1;

            return (chosen, after, mobs);
        }

        /// <summary>
        /// Where each action button actually ended up, and anything still off the edge.
        ///
        /// Clear room used to sit past the right edge of a panel too narrow for four buttons and
        /// simply was not there to click, which reads as a broken button rather than one with
        /// nowhere to go. Nothing complains when a child overflows, so it has to be measured.
        ///
        /// A WrapPanel cannot overflow sideways - it wraps - so the count of rows is the part
        /// worth reporting: one row means they all fitted, more means the wrap did its job. The
        /// overflow list still catches the case a wrap cannot fix, a single button wider than the
        /// panel, which is what a long translation would produce.
        /// </summary>
        internal (int rows, string[] over) buttonLayout
        {
            get
            {
                actionPanel.UpdateLayout();

                var wide = actionPanel.ActualWidth;
                var tops = new HashSet<double>();
                var over = new List<string>();

                foreach (var one in actionPanel.Children.OfType<Button>())
                {
                    var at = one.TranslatePoint(new Point(0, 0), actionPanel);
                    tops.Add(Math.Round(at.Y));

                    if (wide > 0 && at.X + one.ActualWidth > wide + 0.5)
                    {
                        over.Add(one.Content as string ?? one.Name);
                    }
                }

                return (tops.Count, over.ToArray());
            }
        }

        /// <summary>Whether placing points left the camera where it was.</summary>
        internal bool probePlaceKeepsCamera()
        {
            var before = mapView.cameraNow;

            //The middle of whatever room this is. Fixed coordinates fall outside a small one -
            //they were landing past the edge of the camp and placing nothing, which looks
            //exactly like a broken button.
            xBox.Text = ((_room?.Size[0] ?? 2) / 2).ToString();
            yBox.Text = ((_room?.Size[1] ?? 2) / 2).ToString();
            zBox.Text = ((_room?.Size[2] ?? 2) / 2).ToString();

            placeButton_Click(this, new RoutedEventArgs());
            return mapView.cameraNow == before;
        }

        /// <summary>How many spawn points the chosen room holds right now.</summary>
        internal int spawnsNow => _room?.Spawns ?? -1;

        //--- dragging a point, for a probe -------------------------------------------------------

        /// <summary>The 3D view itself, so a probe can press and drag it the way a hand does.</summary>
        internal MapView3D probeView => mapView;

        /// <summary>Where every spawn point in the chosen room is now, straight out of the JSON.</summary>
        internal List<(int x, int y, int z)> probePoints
        {
            get
            {
                var found = new List<(int x, int y, int z)>();
                if (_room == null) { return found; }

                foreach (var region in _room.Regions)
                {
                    if (region?["type"]?.GetValue<string>() != "spawn") { continue; }
                    if (region["pos"] is not JsonArray at || at.Count < 3) { continue; }
                    found.Add((at[0]!.GetValue<int>(), at[1]!.GetValue<int>(), at[2]!.GetValue<int>()));
                }

                return found;
            }
        }

        /// <summary>Whether the room's file has been written down as changed.</summary>
        internal bool probeChanged => _room != null && _map.Changed.Contains(_room.File);

        /// <summary>What the status line says, which is the only thing a person is told.</summary>
        internal string probeStatus => statusLabel.Text;

        /// <summary>
        /// How far the side panel can scroll, and whether the mob list is inside the part that
        /// scrolls into view.
        ///
        /// A ScrollViewer that is present but cannot scroll is the vacuous version of this check:
        /// it would pass on a panel whose content still overflowed a fixed-height child. What
        /// matters is that the content is taller than the window AND that the bottom of the mob
        /// list can be brought into view.
        /// </summary>
        internal (double scrollable, double content, double viewport, bool mobsReachable) panelScrollNow
        {
            get
            {
                panelScroll.UpdateLayout();

                var bottom = mobStack.TranslatePoint(new Point(0, mobStack.ActualHeight), panelScroll).Y
                    + panelScroll.VerticalOffset;

                return (panelScroll.ScrollableHeight, panelScroll.ExtentHeight,
                    panelScroll.ViewportHeight,
                    mobStack.ActualHeight > 0 && bottom <= panelScroll.ExtentHeight + 1);
            }
        }

        /// <summary>The objective chain as the list shows it.</summary>
        internal string[] questRows => questList.Items.OfType<MapSpawns.Objective>()
            .Select(one => one.ToString()).ToArray();

        internal string questHintNow => questHint.Text;

        internal void probeOnlyExit() => onlyExitButton_Click(this, new RoutedEventArgs());

        /// <summary>The exit gates as the list shows them.</summary>
        internal string[] exitRows => exitsList.Items.OfType<MapSpawns.Exit>()
            .Select(one => one.ToString()).ToArray();

        internal string exitHint => exitsHint.Text;

        internal bool exitObjectiveNow => MapSpawns.hasExitObjective(_map);

        internal void probeAddExit(int x, int y, int z)
        {
            xBox.Text = x.ToString();
            yBox.Text = y.ToString();
            zBox.Text = z.ToString();
            addExitButton_Click(this, new RoutedEventArgs());
        }

        /// <summary>The arrival areas as the list shows them.</summary>
        internal string[] startRows => startsList.Items.OfType<MapSpawns.Start>()
            .Select(one => one.ToString()).ToArray();

        internal string startHint => startsHint.Text;

        /// <summary>How wide the chosen room is, so a probe can scale what it asks for.</summary>
        internal int roomAcross => _room == null ? 0 : Math.Min(_room.Size[0], _room.Size[2]);

        internal void probeAddStart(int x, int y, int z)
        {
            xBox.Text = x.ToString();
            yBox.Text = y.ToString();
            zBox.Text = z.ToString();
            addStartButton_Click(this, new RoutedEventArgs());
        }

        internal void probePickStart(int row)
        {
            startsList.SelectedIndex = row;
            startsList_SelectionChanged(this, new SelectionChangedEventArgs(
                System.Windows.Controls.Primitives.Selector.SelectionChangedEvent,
                new object[0], new object[0]));
        }

        internal void probeRemoveStart() => removeStartButton_Click(this, new RoutedEventArgs());

        internal void probeMakeMain() => mainStartButton_Click(this, new RoutedEventArgs());

        /// <summary>The doors as the list shows them, which is what a person reads.</summary>
        internal string[] doorRows => doorsList.Items.OfType<MapSpawns.Door>()
            .Select(one => one.ToString()).ToArray();

        /// <summary>What the doors hint says - the warning about having none lives there.</summary>
        internal string doorHint => doorsHint.Text;

        /// <summary>The door named as the way in, straight out of the level.</summary>
        internal string entryDoorNow => _room == null
            ? string.Empty
            : MapSpawns.entryDoorOf(_map, _room);

        /// <summary>Presses Add door, having aimed and named it the way a person would.</summary>
        internal void probeAddDoor(string name, int x, int y, int z)
        {
            doorNameBox.Text = name;
            xBox.Text = x.ToString();
            yBox.Text = y.ToString();
            zBox.Text = z.ToString();
            addDoorButton_Click(this, new RoutedEventArgs());
        }

        /// <summary>Picks the door at that row, as clicking the list does.</summary>
        internal void probePickDoor(int row)
        {
            doorsList.SelectedIndex = row;
            doorsList_SelectionChanged(this, new SelectionChangedEventArgs(
                System.Windows.Controls.Primitives.Selector.SelectionChangedEvent,
                new object[0], new object[0]));
        }

        internal void probeMakeEntry() => entryDoorButton_Click(this, new RoutedEventArgs());

        internal void probeRemoveDoor() => removeDoorButton_Click(this, new RoutedEventArgs());

        /// <summary>Escape, as the view would deliver it mid-drag.</summary>
        internal void probeCancelDrag() => mapView_DragCancelled();

        /// <summary>How many mob groups the mission has, for a probe.</summary>
        internal int groupCount => groupBox.Items.Count;

        /// <summary>Makes a group the way the button does.</summary>
        internal void probeAddGroup() => addGroupButton_Click(this, new RoutedEventArgs());

        /// <summary>Whether holding W actually moves the view.</summary>
        internal bool probeWalks()
        {
            var before = mapView.cameraNow;
            mapView.probeWalk(Key.W);
            return mapView.cameraNow != before;
        }

        /// <summary>Whether removing a point left the camera where it was.</summary>
        internal bool probeRemoveKeepsCamera()
        {
            var before = mapView.cameraNow;
            probeRemoveFirst();
            return mapView.cameraNow == before;
        }

        /// <summary>The group list as it reads, so a probe can check it says where each is used.</summary>
        internal string[] groupRows => groupBox.Items.OfType<ComboBoxItem>()
            .Select(one => one.Content as string ?? "")
            .ToArray();

        /// <summary>Puts the whole mission back in view, so a probe can aim from a known place.</summary>
        internal void probeFrame() => mapView.frame();

        /// <summary>The ways in and out as the panel lists them, for a probe to read.</summary>
        internal string[] wayRows => (waysList.ItemsSource as System.Collections.IEnumerable)?
            .Cast<string>().ToArray() ?? Array.Empty<string>();

        /// <summary>What a click in the middle of the view would choose.</summary>
        internal (int x, int y, int z)? probeClick()
            => mapView.probeLook(new Point(mapView.ActualWidth / 2, mapView.ActualHeight / 2));

        /// <summary>How tall the chosen room is, so a probe can tell a real height from nonsense.</summary>
        internal int roomHeight => _room?.Size[1] ?? 0;

        /// <summary>The ground the view believes is under a column.</summary>
        internal int groundAt(int x, int z) => mapView.heightAt(x, z);

        private void setStrings()
        {
            Title = R.SPAWNS_TITLE;
            roomsLabel.Content = R.SPAWNS_ROOMS;
            placeLabel.Content = R.SPAWNS_PLACE;
            placeHint.Text = R.SPAWNS_TURN_HINT;
            radiusLabel.Text = R.SPAWNS_RADIUS;
            countLabel.Text = R.SPAWNS_COUNT;
            placeButton.Content = R.SPAWNS_PLACE_BUTTON;
            clearButton.Content = R.SPAWNS_CLEAR;
            removeButton.Content = R.SPAWNS_REMOVE_POINT;
            questLabel.Content = R.SPAWNS_QUEST;
            questHint.Text = R.SPAWNS_QUEST_WHY;
            onlyExitButton.Content = R.SPAWNS_QUEST_ONLY_EXIT;
            removeQuestButton.Content = R.SPAWNS_QUEST_REMOVE;
            exitsLabel.Content = R.SPAWNS_EXITS;
            exitsHint.Text = R.SPAWNS_EXITS_WHY;
            addExitButton.Content = R.SPAWNS_ADD_EXIT;
            removeExitButton.Content = R.SPAWNS_REMOVE_EXIT;
            startsLabel.Content = R.SPAWNS_STARTS;
            startsHint.Text = R.SPAWNS_STARTS_WHY;
            addStartButton.Content = R.SPAWNS_ADD_START;
            removeStartButton.Content = R.SPAWNS_REMOVE_START;
            mainStartButton.Content = R.SPAWNS_MAIN_START;
            doorsLabel.Content = R.SPAWNS_DOORS;
            doorsHint.Text = R.SPAWNS_DOORS_WHY;
            addDoorButton.Content = R.SPAWNS_ADD_DOOR;
            removeDoorButton.Content = R.SPAWNS_REMOVE_DOOR;
            entryDoorButton.Content = R.SPAWNS_ENTRY_DOOR;
            waysLabel.Content = R.SPAWNS_WAYS;
            rulesLabel.Content = R.SPAWNS_RULES;
            rulesHint.Text = R.SPAWNS_RULES_HINT;
            addMobButton.Content = R.SPAWNS_ADD_MOB;
            addGroupButton.Content = R.SPAWNS_ADD_GROUP;
            addGroupButton.ToolTip = R.SPAWNS_ADD_GROUP_WHY;
            saveButton.Content = R.SPAWNS_SAVE;
            revertButton.Content = R.SPAWNS_RELOAD;
            frameButton.Content = R.SPAWNS_FIT;
            installButton.Content = R.SPAWNS_INSTALL;
            installButton.ToolTip = R.SPAWNS_INSTALL_WHY;
            installButton.Visibility = _mission == null ? Visibility.Collapsed : Visibility.Visible;
            overheadButton.Content = R.SPAWNS_OVERHEAD;
            whereLabel.Text = "";
        }

        /// <summary>
        /// Hides the room list when there is only one room.
        ///
        /// A welded mission IS one room, and a list of one is three hundred pixels spent saying
        /// so. The map wants them more.
        /// </summary>
        private void fitPanels()
        {
            roomsPanel.Visibility = _map.Rooms.Count > 1
                ? Visibility.Visible
                : Visibility.Collapsed;
        }

        private void fillRooms()
        {
            //Whatever was chosen stays chosen. This is called again whenever a spawn point is
            //added or removed, only to refresh the counts in the list.
            var was = roomList.SelectedIndex;

            _filling = true;
            try
            {
                roomList.ItemsSource = _map.Rooms
                    .Select((one, at) => new { Room = one, Text = $"{at + 1}.  {one}" })
                    .ToList();
                roomList.DisplayMemberPath = "Text";

                //Choose the first one outright the first time. The list used to be the only way
                //in, so nothing ever selected for you; now that a welded mission hides the list
                //entirely, waiting to be clicked means waiting forever and no map is ever built.
                if (roomList.Items.Count > 0)
                {
                    roomList.SelectedIndex = was >= 0 && was < roomList.Items.Count ? was : 0;
                }
            }
            finally
            {
                _filling = false;
            }

            //Only the first fill has anything to tell anyone: the rest are refreshes of a list
            //whose selection has not moved.
            if (was < 0) { roomList_SelectionChanged(this, null!); }
        }

        //--- the map, from above ------------------------------------------------------------------

        /// <summary>
        /// The level seen from the top, rooms as boxes and spawn points as dots.
        ///
        /// Flat rather than three dimensional on purpose: placing a spawn is choosing a spot on a
        /// floor, and a plan view says where that is far more clearly than a perspective one.
        /// Height still matters, so a room's floor level is written on it.
        /// </summary>
        /// <summary>
        /// Builds the chosen room and hands it to the view.
        ///
        /// On a background thread, always. Building a relief decompresses the whole block array -
        /// twenty-two million cells for Creeper Woods - to find out what the top of every column
        /// is made of, and doing that on the UI thread locks the window solid for a second at a
        /// time, once per click of the slider.
        /// </summary>
        private async void draw(bool keepCamera = false)
        {
            if (_room == null)
            {
                busyLabel.Text = "";
                return;
            }

            var room = _room;
            var ceiling = (int)ceilingSlider.Value;
            if (ceiling >= 255) { ceiling = 0; }

            //Which build this is. A slider dragged across twenty values starts twenty builds, and
            //only the last one should be allowed to put anything on screen.
            var mine = ++_building;
            busyLabel.Text = R.SPAWNS_BUILDING;

            MapRelief.Relief? relief = null;
            try
            {
                relief = await System.Threading.Tasks.Task.Run(
                    () => MapRelief.build(room, palette, ceiling)).ConfigureAwait(true);
            }
            catch (Exception problem)
            {
                EventLogger.logError($"could not build {room.Id}: {problem.Message}");
            }

            if (mine != _building) { return; }

            busyLabel.Text = relief == null ? R.SPAWNS_NO_SHAPE : "";
            if (relief == null) { return; }

            mapView.show(relief, keepCamera);
            markSpawns();
        }

        /// <summary>
        /// The mission's colours, read from its own resource pack the first time.
        ///
        /// Under a lock because builds run off the UI thread and a dragged slider can have
        /// several of them in flight at once, all arriving here together on the first one.
        /// </summary>
        private BlockPalette.Look[] palette
        {
            get
            {
                lock (_paletteLock)
                {
                    return _palette ??= BlockPalette.forMission(_map.Level);
                }
            }
        }

        private BlockPalette.Look[]? _palette;
        private readonly object _paletteLock = new object();

        private void markSpawns()
        {
            markPoints();
            markWays();
            fillDoors();
            fillStarts();
            fillExits();
            fillQuest();
        }

        /// <summary>
        /// Just the spawn points, without going back over the teleports.
        ///
        /// Split out for dragging, which redraws on every block the pointer crosses. The ways in
        /// and out are read out of the level rather than the room and do not move while a spawn
        /// point is being dragged, so doing that lookup sixty times a second would buy nothing.
        /// </summary>
        private void markPoints()
        {
            if (_room == null) { return; }

            var found = new List<(int x, int y, int z)>();

            foreach (var region in _room.Regions)
            {
                if (region?["type"]?.GetValue<string>() != "spawn") { continue; }
                if (region["pos"] is not JsonArray at || at.Count < 3) { continue; }

                found.Add((at[0]!.GetValue<int>(), at[1]!.GetValue<int>(), at[2]!.GetValue<int>()));
            }

            mapView.mark(found, Color.FromRgb(255, 120, 60));
        }

        //--- what the mission asks of you -----------------------------------------------------------

        private int _quest = -1;

        /// <summary>
        /// The objective chain, and whether each step can still be finished.
        ///
        /// A step whose regions are not in the map can never complete, and every step behind it
        /// is then unreachable - including the exit gate, which is the last one in every mission
        /// the game ships. That is a gate that draws, lights up and does nothing, with no error
        /// anywhere, so it is worth saying out loud here.
        /// </summary>
        private void fillQuest()
        {
            if (_room == null) { questList.ItemsSource = null; return; }

            var steps = MapSpawns.objectivesOf(_map);

            //What regions this room actually has to offer, by name.
            var have = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var region in _room.Regions)
            {
                var name = region?["name"]?.GetValue<string>();
                if (!string.IsNullOrEmpty(name)) { have.Add(name!); }
            }

            _filling = true;
            var wasAt = _quest;
            questList.ItemsSource = steps;
            questList.SelectedIndex = steps.FindIndex(one => one.At == wasAt);
            _filling = false;

            var stuck = steps.FirstOrDefault(one => one.Needs.Any(need => !have.Contains(need)));

            questHint.Text = steps.Count == 0
                ? R.SPAWNS_QUEST_NONE
                : stuck != null
                    ? string.Format(R.SPAWNS_QUEST_STUCK, stuck.At + 1,
                        string.Join(", ", stuck.Needs.Where(need => !have.Contains(need))))
                    : string.Format(R.SPAWNS_QUEST_OK, steps.Count);
        }

        private void questList_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_filling) { return; }
            _quest = questList.SelectedItem is MapSpawns.Objective step ? step.At : -1;
            updateUI();
        }

        private void removeQuestButton_Click(object sender, RoutedEventArgs e)
        {
            if (_quest < 0) { return; }
            if (!MapSpawns.removeObjectiveAt(_map, _quest)) { return; }

            _map.Changed.Add("level.json");
            _quest = -1;

            statusLabel.Text = string.Format(R.SPAWNS_QUEST_REMOVED,
                MapSpawns.objectivesOf(_map).Count);

            fillQuest();
            updateUI();
        }

        private void onlyExitButton_Click(object sender, RoutedEventArgs e)
        {
            var gone = MapSpawns.keepOnlyExit(_map);

            if (gone == 0)
            {
                statusLabel.Text = R.SPAWNS_QUEST_NOTHING_TO_DROP;
                return;
            }

            _map.Changed.Add("level.json");
            _quest = -1;

            statusLabel.Text = MapSpawns.objectivesOf(_map).Count == 0
                ? string.Format(R.SPAWNS_QUEST_ALL_GONE, gone)
                : string.Format(R.SPAWNS_QUEST_ONLY_EXIT_LEFT, gone);

            fillQuest();
            updateUI();
        }

        //--- the way out ----------------------------------------------------------------------------

        private int _exit = -1;

        private void fillExits()
        {
            if (_room == null)
            {
                exitsList.ItemsSource = null;
                mapView.markExits(Array.Empty<(int, int, int)>());
                return;
            }

            var exits = MapSpawns.exitsOf(_map, _room);

            _filling = true;
            var wasAt = _exit;
            exitsList.ItemsSource = exits;
            exitsList.SelectedIndex = exits.FindIndex(one => one.At == wasAt);
            _filling = false;

            mapView.markExits(exits.Select(one => (one.Pos[0], one.Pos[1], one.Pos[2])));

            //Both halves are reported, because either alone silently does nothing.
            var claimed = MapSpawns.hasExitObjective(_map);

            exitsHint.Text = exits.Count == 0
                ? (claimed ? R.SPAWNS_EXITS_OBJECTIVE_ONLY : R.SPAWNS_EXITS_NONE)
                : claimed
                    ? string.Format(R.SPAWNS_EXITS_SOME, exits.Count)
                    : string.Format(R.SPAWNS_EXITS_UNCLAIMED, exits.Count);
        }

        private void exitsList_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_filling) { return; }
            if (exitsList.SelectedItem is not MapSpawns.Exit found) { _exit = -1; return; }

            _exit = found.At;
            mapView.aim(found.Pos[0], found.Pos[1], found.Pos[2], true);
            statusLabel.Text = string.Format(R.SPAWNS_EXIT_AT,
                found.Pos[0], found.Pos[1], found.Pos[2]);
            updateUI();
        }

        private void addExitButton_Click(object sender, RoutedEventArgs e)
        {
            if (_room == null) { return; }

            var had = MapSpawns.hasExitObjective(_map);

            var made = MapSpawns.addExit(_map, _room,
                number(xBox, _room.Size[0] / 2),
                number(yBox, _room.Size[1] / 2),
                number(zBox, _room.Size[2] / 2));

            _map.Changed.Add(_room.File);
            _exit = made.At;

            //The objective lives in the level, not the object group, so that file changed too -
            //and forgetting to say so is a gate that saves without anything pointing at it.
            if (!had) { _map.Changed.Add("level.json"); }

            statusLabel.Text = string.Format(
                had ? R.SPAWNS_EXIT_ADDED : R.SPAWNS_EXIT_ADDED_WITH_OBJECTIVE,
                made.Pos[0], made.Pos[1], made.Pos[2]);

            fillExits();
            updateUI();
        }

        private void removeExitButton_Click(object sender, RoutedEventArgs e)
        {
            if (_room == null || _exit < 0) { return; }
            if (!MapSpawns.removeExitAt(_room, _exit)) { return; }

            _map.Changed.Add(_room.File);
            _exit = -1;

            var left = MapSpawns.exitsOf(_map, _room).Count;
            statusLabel.Text = left == 0
                ? R.SPAWNS_EXIT_LAST_GONE
                : string.Format(R.SPAWNS_EXIT_REMOVED, left);

            fillExits();
            updateUI();
        }

        //--- where you come in ---------------------------------------------------------------------

        /// <summary>Which arrival area is picked, by its place in the region list.</summary>
        private int _start = -1;

        /// <summary>
        /// Draws the arrival areas in green and lists them.
        ///
        /// This is the thing a hand-built mission is most likely to be missing, and the hardest
        /// to notice: nothing about a map looks wrong without one. The game's own missions always
        /// have at least one, and a welded Creeper Woods carries two.
        /// </summary>
        private void fillStarts()
        {
            if (_room == null)
            {
                startsList.ItemsSource = null;
                mapView.markStarts(Array.Empty<(int, int, int, bool)>());
                return;
            }

            var starts = MapSpawns.startsOf(_room);

            _filling = true;
            var wasAt = _start;
            startsList.ItemsSource = starts;
            startsList.SelectedIndex = starts.FindIndex(one => one.At == wasAt);
            _filling = false;

            mapView.markStarts(starts.Select(one =>
                (one.Pos[0], one.Pos[1], one.Pos[2], one.IsMain)));

            startsHint.Text = starts.Count == 0
                ? R.SPAWNS_STARTS_NONE
                : starts.Count == 1
                    ? R.SPAWNS_STARTS_ONE
                    : string.Format(R.SPAWNS_STARTS_SOME, starts.Count);
        }

        private void startsList_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_filling) { return; }

            if (startsList.SelectedItem is not MapSpawns.Start start) { _start = -1; return; }

            _start = start.At;
            mapView.aim(start.Pos[0], start.Pos[1], start.Pos[2], true);

            statusLabel.Text = string.Format(R.SPAWNS_START_AT,
                start.Pos[0], start.Pos[1], start.Pos[2]);

            updateUI();
        }

        private void addStartButton_Click(object sender, RoutedEventArgs e)
        {
            if (_room == null) { return; }

            var made = MapSpawns.addStart(_room,
                number(xBox, _room.Size[0] / 2),
                number(yBox, _room.Size[1] / 2),
                number(zBox, _room.Size[2] / 2));

            _map.Changed.Add(_room.File);
            _start = made.At;

            //Whether the game can actually stand you there. An arrival area on ground it calls
            //unwalkable is a mission that loads and then does not know what to do with you.
            var ok = mapView.walkableAt(made.Pos[0], made.Pos[2]);

            statusLabel.Text = string.Format(ok ? R.SPAWNS_START_ADDED : R.SPAWNS_START_ADDED_UNWALKABLE,
                made.Pos[0], made.Pos[1], made.Pos[2]);

            fillStarts();
            updateUI();
        }

        private void mainStartButton_Click(object sender, RoutedEventArgs e)
        {
            if (_room == null || _start < 0) { return; }

            if (!MapSpawns.promoteStart(_room, _start))
            {
                statusLabel.Text = R.SPAWNS_START_ALREADY_MAIN;
                return;
            }

            _map.Changed.Add(_room.File);

            //Promoting moves the region up the array, so every index after it has shifted and
            //the one that was picked is no longer where it was.
            _start = MapSpawns.startsOf(_room).FirstOrDefault(one => one.IsMain)?.At ?? -1;

            statusLabel.Text = R.SPAWNS_START_NOW_MAIN;

            fillStarts();
            updateUI();
        }

        private void removeStartButton_Click(object sender, RoutedEventArgs e)
        {
            if (_room == null || _start < 0) { return; }

            if (!MapSpawns.removeStartAt(_room, _start)) { return; }

            _map.Changed.Add(_room.File);
            _start = -1;

            var left = MapSpawns.startsOf(_room).Count;

            statusLabel.Text = left == 0
                ? R.SPAWNS_START_LAST_GONE
                : string.Format(R.SPAWNS_START_REMOVED, left);

            fillStarts();
            updateUI();
        }

        //--- doors -------------------------------------------------------------------------------

        /// <summary>
        /// Which door is picked in the list, or -1. Held by its place in the tile's door array,
        /// which is what removing one needs.
        /// </summary>
        private int _door = -1;

        /// <summary>
        /// Draws the doors in pink and lists them.
        ///
        /// This is the half of a custom mission that is invisible in Minecraft. A door is not a
        /// block - it is four numbers in the tile's JSON - so somebody who builds a beautiful
        /// level and brings it home has no way to see that it has no way in, until the game
        /// refuses to load it.
        /// </summary>
        private void fillDoors()
        {
            if (_room == null)
            {
                doorsList.ItemsSource = null;
                mapView.markDoors(Array.Empty<(int, int, int, bool)>());
                return;
            }

            var doors = MapSpawns.doorsOf(_map, _room);

            _filling = true;
            var wasAt = _door;
            doorsList.ItemsSource = doors;
            doorsList.SelectedIndex = doors.FindIndex(one => one.At == wasAt);
            _filling = false;

            mapView.markDoors(doors.Select(one =>
                (one.Pos[0], one.Pos[1], one.Pos[2], one.IsEntry)));

            //Said here rather than discovered on a loading screen. A tile with no door is one the
            //generator cannot place, and a tile with doors but none named as the way in leaves
            //the game to pick - which works until it does not.
            var entry = doors.FirstOrDefault(one => one.IsEntry);

            doorsHint.Text = doors.Count == 0
                ? R.SPAWNS_DOORS_NONE
                : entry != null
                    ? string.Format(R.SPAWNS_DOORS_ENTRY, doors.Count, entry.Name)
                    : string.Format(R.SPAWNS_DOORS_NO_ENTRY, doors.Count);
        }

        private void doorsList_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_filling) { return; }

            if (doorsList.SelectedItem is not MapSpawns.Door door) { _door = -1; return; }

            _door = door.At;
            doorNameBox.Text = door.Name;

            mapView.aim(door.Pos[0], door.Pos[1], door.Pos[2], true);
            statusLabel.Text = string.Format(R.SPAWNS_DOOR_AT,
                door.Name.Length > 0 ? door.Name : "(unnamed)",
                door.Pos[0], door.Pos[1], door.Pos[2], door.Facing);

            updateUI();
        }

        /// <summary>
        /// Puts a door where the map is aimed.
        ///
        /// The name matters more than anything else about it, because everything that refers to a
        /// door refers to it by name - the entry-door field and every teleport. So an unnamed
        /// door is scenery, and the box is filled in with a sensible one rather than left empty:
        /// the first door a mission gets should be the way in.
        /// </summary>
        private void addDoorButton_Click(object sender, RoutedEventArgs e)
        {
            if (_room == null) { return; }

            var name = doorNameBox.Text.Trim();
            if (name.Length == 0) { name = MapSpawns.doorsOf(_map, _room).Count == 0 ? "enter" : "exit"; }

            var made = MapSpawns.addDoor(_map, _room, name,
                number(xBox, _room.Size[0] / 2),
                number(yBox, _room.Size[1] / 2),
                number(zBox, _room.Size[2] / 2));

            _map.Changed.Add(_room.File);
            _door = made.At;

            //The first door in a mission is the way in unless somebody says otherwise. A mission
            //whose only door is not named as the entry is the camp crash waiting to happen.
            if (MapSpawns.entryDoorOf(_map, _room).Length == 0)
            {
                MapSpawns.setEntryDoor(_map, _room, name);
                _map.Changed.Add("level.json");
            }

            //A door away from every edge is the mistake worth catching here: it looks placed,
            //it lists, and it does nothing - there is no outside beside it to arrive from.
            statusLabel.Text = string.Format(
                made.OnWall ? R.SPAWNS_DOOR_ADDED : R.SPAWNS_DOOR_ADDED_INNER,
                name, made.Pos[0], made.Pos[1], made.Pos[2], made.Facing);

            fillDoors();
            updateUI();
        }

        private void removeDoorButton_Click(object sender, RoutedEventArgs e)
        {
            if (_room == null || _door < 0) { return; }

            var doors = MapSpawns.doorsOf(_map, _room);
            var going = doors.FirstOrDefault(one => one.At == _door);

            if (going == null || !MapSpawns.removeDoorAt(_room, _door)) { return; }

            _map.Changed.Add(_room.File);
            _door = -1;

            //Taking out the last door is how the camp was crashed, so it is said plainly rather
            //than left to be found on a loading screen.
            var left = doors.Count - 1;
            statusLabel.Text = left == 0
                ? R.SPAWNS_DOOR_LAST_GONE
                : going.IsEntry
                    ? string.Format(R.SPAWNS_DOOR_ENTRY_GONE, going.Name, left)
                    : string.Format(R.SPAWNS_DOOR_REMOVED, left);

            fillDoors();
            updateUI();
        }

        private void entryDoorButton_Click(object sender, RoutedEventArgs e)
        {
            if (_room == null || _door < 0) { return; }

            var door = MapSpawns.doorsOf(_map, _room).FirstOrDefault(one => one.At == _door);
            if (door == null) { return; }

            if (door.Name.Length == 0)
            {
                statusLabel.Text = R.SPAWNS_DOOR_NEEDS_NAME;
                return;
            }

            MapSpawns.setEntryDoor(_map, _room, door.Name);
            _map.Changed.Add("level.json");

            statusLabel.Text = string.Format(R.SPAWNS_DOOR_IS_ENTRY, door.Name);

            fillDoors();
            updateUI();
        }

        /// <summary>
        /// Draws and lists the ways in and out of this room.
        ///
        /// Worth showing beside the spawn points because they are the other half of what a room
        /// is: where things come from, and where you can go. A teleport whose door has gone is
        /// listed too, and said so - that is the failure that crashed the camp, and it is
        /// invisible in Minecraft because a teleport is not a block.
        /// </summary>
        private void markWays()
        {
            if (_room == null) { waysList.ItemsSource = null; return; }

            var ways = MapSpawns.teleportsOf(_map, _room);

            mapView.markWays(ways
                .Where(one => one.At[0] >= 0)
                .Select(one => (one.At[0], one.At[1], one.At[2], one.Leaves)));

            waysList.ItemsSource = ways
                .Select(one => one.At[0] < 0
                    ? string.Format(R.SPAWNS_WAY_LOST, one.Door)
                    : one.Leaves
                        ? string.Format(R.SPAWNS_WAY_OUT, one.Door,
                            one.Dungeons.Length > 0 ? one.Dungeons : one.Exit)
                        : string.Format(R.SPAWNS_WAY_IN, one.Door))
                .ToList();

            waysHint.Text = ways.Count == 0
                ? R.SPAWNS_NO_WAYS
                : string.Format(R.SPAWNS_WAYS_HINT, ways.Count);
        }

        private void frameButton_Click(object sender, RoutedEventArgs e) => mapView.frame();

        private void overheadButton_Click(object sender, RoutedEventArgs e) => mapView.overhead();

        private void ceilingSlider_ValueChanged(object sender,
            RoutedPropertyChangedEventArgs<double> e)
        {
            if (ceilingLabel == null) { return; }

            var at = (int)ceilingSlider.Value;
            ceilingLabel.Text = at >= 255
                ? R.SPAWNS_CEILING_ALL
                : string.Format(R.SPAWNS_CEILING, at);
        }

        /// <summary>
        /// Rebuilt when the slider is let go, not while it moves.
        ///
        /// Every value costs a full rebuild, and a drag across the slider would queue a hundred
        /// of them to show one.
        /// </summary>
        private void ceilingSlider_DragCompleted(object sender,
            System.Windows.Controls.Primitives.DragCompletedEventArgs e) => draw(true);

        /// <summary>Which spawn point a click landed on, if it landed on one at all.</summary>
        private int _selected = -1;

        /// <summary>
        /// Set while a list is being refilled, so its own SelectionChanged stays quiet.
        ///
        /// Refilling a list clears it, and clearing a list raises SelectionChanged - which used
        /// to be taken as "the user picked something else". Removing one spawn point refreshed
        /// the room list, which counted as choosing the first room, which rebuilt the map and
        /// threw the camera back to the start. Adding a mob refreshed the group list, which
        /// counted as choosing the first group, so the mob went in and the panel jumped to a
        /// different group's mobs - which looks exactly like the mob was never added.
        /// </summary>
        private bool _filling;

        private void mapView_Picked(int x, int y, int z)
        {
            //A click on a spawn point means that one, not "another one here". Eight blocks is
            //about the size of the marker on screen when the whole mission is in view.
            var found = _room == null ? null : MapSpawns.nearest(_room, x, y, z, 8);
            _selected = found?.at ?? -1;

            if (found != null)
            {
                x = found.Value.x;
                y = found.Value.y;
                z = found.Value.z;
                statusLabel.Text = string.Format(R.SPAWNS_ON_POINT, x, y, z);
            }
            else
            {
                statusLabel.Text = string.Format(R.SPAWNS_AIMED, x, y, z,
                    number(countBox, 4), number(radiusBox, 6));
            }

            mapView.aim(x, y, z, found != null);

            xBox.Text = x.ToString();
            //Onto the ground rather than into it: the height plane holds the first free cell
            //above the top block, which is exactly where something should stand.
            yBox.Text = y.ToString();
            zBox.Text = z.ToString();
            updateUI();
        }

        private void mapView_Confirmed(int x, int y, int z)
        {
            mapView_Picked(x, y, z);
            placeButton_Click(this, new RoutedEventArgs());
        }

        /// <summary>
        /// Where the point being dragged sat before anybody took hold of it.
        ///
        /// Kept so Escape can put it back. The drag has already written the new position into the
        /// room by then - it has to, or the marker could not follow the pointer - so undoing it
        /// means remembering the old one rather than declining to write the new one.
        /// </summary>
        private (int x, int y, int z)? _held;

        /// <summary>Where one spawn point is now, by where it sits in the region list.</summary>
        private (int x, int y, int z)? positionOf(int at)
        {
            if (_room == null || at < 0 || at >= _room.Regions.Count) { return null; }
            if (_room.Regions[at]?["pos"] is not JsonArray pos || pos.Count < 3) { return null; }

            return (pos[0]!.GetValue<int>(), pos[1]!.GetValue<int>(), pos[2]!.GetValue<int>());
        }

        /// <summary>
        /// A pin has been taken hold of: pick the matching row, whichever kind it is.
        ///
        /// All three kinds drag the same way, and they have to - a person who has just learned
        /// that spawn points drag will try it on the green one within about four seconds, and an
        /// entrance that cannot be nudged is the one you most want to nudge.
        /// </summary>
        private void mapView_Grabbed(MapView3D.Pin kind, int x, int y, int z)
        {
            switch (kind)
            {
                case MapView3D.Pin.Door:
                    _door = MapSpawns.doorsOf(_map, _room!)
                        .FirstOrDefault(one => one.Pos[0] == x && one.Pos[1] == y && one.Pos[2] == z)
                        ?.At ?? -1;
                    selectRow(doorsList, one => one is MapSpawns.Door door && door.At == _door);
                    break;

                case MapView3D.Pin.Start:
                    _start = MapSpawns.startsOf(_room!)
                        .FirstOrDefault(one => one.Pos[0] == x && one.Pos[1] == y && one.Pos[2] == z)
                        ?.At ?? -1;
                    selectRow(startsList, one => one is MapSpawns.Start start && start.At == _start);
                    break;

                case MapView3D.Pin.Exit:
                    _exit = MapSpawns.exitsOf(_map, _room!)
                        .FirstOrDefault(one => one.Pos[0] == x && one.Pos[1] == y && one.Pos[2] == z)
                        ?.At ?? -1;
                    selectRow(exitsList, one => one is MapSpawns.Exit found && found.At == _exit);
                    break;

                default:
                    mapView_Picked(x, y, z);
                    break;
            }

            if (kind != MapView3D.Pin.Spawn)
            {
                mapView.aim(x, y, z, true);
                updateUI();
            }
        }

        /// <summary>Picks a row without the list's own handler treating it as a fresh choice.</summary>
        private void selectRow(System.Windows.Controls.ListBox list, Func<object, bool> which)
        {
            _filling = true;
            list.SelectedIndex = list.Items.Cast<object>().ToList().FindIndex(one => which(one));
            _filling = false;
        }

        /// <summary>Where the pin being dragged sits now, whichever kind it is.</summary>
        private (int x, int y, int z)? heldPosition()
        {
            if (_room == null) { return null; }

            switch (mapView.heldKind)
            {
                case MapView3D.Pin.Door:
                    var door = MapSpawns.doorsOf(_map, _room).FirstOrDefault(one => one.At == _door);
                    return door == null ? null : (door.Pos[0], door.Pos[1], door.Pos[2]);

                case MapView3D.Pin.Start:
                    var start = MapSpawns.startsOf(_room).FirstOrDefault(one => one.At == _start);
                    return start == null ? null : (start.Pos[0], start.Pos[1], start.Pos[2]);

                case MapView3D.Pin.Exit:
                    var found = MapSpawns.exitsOf(_map, _room).FirstOrDefault(one => one.At == _exit);
                    return found == null ? null : (found.Pos[0], found.Pos[1], found.Pos[2]);

                default:
                    return positionOf(_selected);
            }
        }

        /// <summary>Puts the pin being dragged somewhere, whichever kind it is.</summary>
        private bool moveHeld(int x, int y, int z)
        {
            if (_room == null) { return false; }

            return mapView.heldKind switch
            {
                MapView3D.Pin.Door => _door >= 0 && MapSpawns.moveDoor(_room, _door, x, y, z),
                MapView3D.Pin.Start => _start >= 0 && MapSpawns.moveStart(_room, _start, x, y, z),
                MapView3D.Pin.Exit => _exit >= 0 && MapSpawns.moveExit(_room, _exit, x, y, z),
                _ => _selected >= 0 && MapSpawns.moveTo(_room, _selected, x, y, z),
            };
        }

        private void mapView_Dragged(int x, int y, int z)
        {
            if (_room == null) { return; }

            _held ??= heldPosition();
            if (_held == null) { return; }

            if (!moveHeld(x, y, z)) { return; }

            //Only the markers, and no list rebuild. Both of those happen once on the drop - a
            //list that renumbers itself under the pointer is unreadable, and rebuilding it per
            //block crossed is work nobody sees.
            switch (mapView.heldKind)
            {
                case MapView3D.Pin.Door: redrawDoorPins(); break;
                case MapView3D.Pin.Start: redrawStartPins(); break;
                case MapView3D.Pin.Exit: redrawExitPins(); break;
                default: markPoints(); break;
            }

            mapView.aim(x, y, z, true);

            xBox.Text = x.ToString();
            yBox.Text = y.ToString();
            zBox.Text = z.ToString();

            statusLabel.Text = string.Format(R.SPAWNS_MOVING, x, y, z);
        }

        /// <summary>The pink pins alone, without rebuilding the list under the pointer.</summary>
        private void redrawDoorPins()
        {
            if (_room == null) { return; }
            mapView.markDoors(MapSpawns.doorsOf(_map, _room)
                .Select(one => (one.Pos[0], one.Pos[1], one.Pos[2], one.IsEntry)));
        }

        /// <summary>The red pins alone.</summary>
        private void redrawExitPins()
        {
            if (_room == null) { return; }
            mapView.markExits(MapSpawns.exitsOf(_map, _room)
                .Select(one => (one.Pos[0], one.Pos[1], one.Pos[2])));
        }

        /// <summary>The green pins alone.</summary>
        private void redrawStartPins()
        {
            if (_room == null) { return; }
            mapView.markStarts(MapSpawns.startsOf(_room)
                .Select(one => (one.Pos[0], one.Pos[1], one.Pos[2], one.IsMain)));
        }

        private void mapView_Dropped(int x, int y, int z)
        {
            var from = _held;
            _held = null;

            if (_room == null || from == null) { return; }

            //Nothing actually changed if it came back to where it started, and saying a file
            //changed when it did not means a rewrite and a .before backup for no reason.
            if (from.Value == (x, y, z)) { return; }

            _map.Changed.Add(_room.File);

            //Named for what it is. "Moved that spawn point" about the thing that decides where
            //you come into the mission is the sort of wrong that makes somebody undo a good edit.
            var said = mapView.heldKind switch
            {
                MapView3D.Pin.Door => R.SPAWNS_MOVED_DOOR,
                MapView3D.Pin.Start => R.SPAWNS_MOVED_START,
                MapView3D.Pin.Exit => R.SPAWNS_MOVED_EXIT,
                _ => R.SPAWNS_MOVED,
            };

            statusLabel.Text = string.Format(said,
                from.Value.x, from.Value.y, from.Value.z, x, y, z);

            //A door dragged into a different wall has been turned to suit it, and a door dragged
            //off every wall is no longer a door anybody can arrive through - both of which the
            //rows say, so they are rebuilt here rather than left stale.
            markSpawns();
            fillRooms();
            updateUI();
        }

        private void mapView_DragCancelled()
        {
            var from = _held;
            _held = null;

            if (_room == null || from == null) { return; }
            if (!moveHeld(from.Value.x, from.Value.y, from.Value.z)) { return; }

            markSpawns();
            mapView.aim(from.Value.x, from.Value.y, from.Value.z, true);

            xBox.Text = from.Value.x.ToString();
            yBox.Text = from.Value.y.ToString();
            zBox.Text = from.Value.z.ToString();

            statusLabel.Text = string.Format(R.SPAWNS_MOVE_OFF,
                from.Value.x, from.Value.y, from.Value.z);
        }

        private void mapView_Hovered(int x, int y, int z)
        {
            //Whether the game would let a mob stand here, because a spawn point on ground it
            //calls unwalkable is a mob it will not place.
            whereLabel.Text = $"{x,5}  {y,3}  {z,5}   "
                + (mapView.walkableAt(x, z) ? R.SPAWNS_WALKABLE : R.SPAWNS_UNWALKABLE);
        }

        private void roomList_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_filling) { return; }

            dynamic? picked = roomList.SelectedItem;
            _room = picked?.Room as MapSpawns.Room;

            //Doors are held by their place in one room's door array, so an index kept across a
            //room change points at a different door entirely. Same for arrival areas.
            _door = -1;
            _start = -1;
            _exit = -1;
            _quest = -1;
            _selected = -1;

            if (_room != null)
            {
                xBox.Text = (_room.Size[0] / 2).ToString();
                yBox.Text = (_room.Size[1] / 2).ToString();
                zBox.Text = (_room.Size[2] / 2).ToString();
            }

            draw();
            updateUI();
        }

        //--- placing --------------------------------------------------------------------------------

        private static int number(TextBox box, int fallback)
            => int.TryParse(box.Text, out var got) ? got : fallback;

        private void placeButton_Click(object sender, RoutedEventArgs e)
        {
            if (_room == null) { return; }

            var tag = (tagBox.SelectedItem as ComboBoxItem)?.Tag as string ?? string.Empty;
            var count = Math.Max(number(countBox, 4), 1);

            var made = MapSpawns.place(_room,
                number(xBox, _room.Size[0] / 2),
                number(yBox, _room.Size[1] / 2),
                number(zBox, _room.Size[2] / 2),
                Math.Max(number(radiusBox, 6), 0),
                count, tag,
                //A different seed each time, so pressing Place twice does not put the second lot
                //exactly on top of the first.
                Environment.TickCount);

            if (made > 0) { _map.Changed.Add(_room.File); }
            //The spawn points moved, so the markers standing on the map are stale.
            markSpawns();

            //Said plainly rather than discovered later: a spawn with no floor under it is one the
            //game has nowhere to put a mob, so fewer may be placed than asked for.
            statusLabel.Text = made < count
                ? string.Format(R.SPAWNS_PLACED_SOME, made, count)
                : string.Format(R.SPAWNS_PLACED, made);

            //No rebuild. Placing spawn points does not move a single block, and drawing the map
            //again threw the camera back to the start every time - which is a long way from
            //wherever you were standing when you decided to put them there.
            fillRooms();
            updateUI();
        }

        private void removeButton_Click(object sender, RoutedEventArgs e)
        {
            if (_room == null || _selected < 0) { return; }

            if (!MapSpawns.removeAt(_room, _selected))
            {
                statusLabel.Text = R.SPAWNS_NOT_A_POINT;
                return;
            }

            _map.Changed.Add(_room.File);
            _selected = -1;

            statusLabel.Text = string.Format(R.SPAWNS_REMOVED_ONE, _room.Spawns);
            markSpawns();
            fillRooms();
            updateUI();
        }

        private void clearButton_Click(object sender, RoutedEventArgs e)
        {
            if (_room == null) { return; }

            var gone = MapSpawns.clear(_room);
            if (gone > 0) { _map.Changed.Add(_room.File); }

            statusLabel.Text = string.Format(R.SPAWNS_CLEARED, gone, _room.Stretch);
            fillRooms();
            draw();
            updateUI();
        }

        //--- what spawns ----------------------------------------------------------------------------

        private JsonArray groups => (_map.Level["mob-groups"] as JsonArray) ?? new JsonArray();

        private void fillGroups()
        {
            //Held by identity, not by index: this is called after a mob is added or renamed, and
            //dropping back to the first group means the change lands somewhere the person is no
            //longer looking.
            var was = chosenGroup;

            _filling = true;
            try
            {
                groupBox.Items.Clear();

                //Groups the mission actually uses come first, and every one says where it is
                //used. Without this the list is twenty-three names with nothing to choose
                //between them, and editing the wrong one looks exactly like the editor ignoring
                //you - the mobs out in the level never came from that group in the first place.
                var used = MapSpawns.usage(_map.Level);

                var ordered = groups
                    .OfType<JsonObject>()
                    .Where(group => group["id"]?.GetValue<string>() != null)
                    .OrderByDescending(group =>
                        used.TryGetValue(group["id"]!.GetValue<string>(), out var use)
                            ? (use.Roaming > 0 ? 2 : use.Waves > 0 ? 1 : 0)
                            : 0)
                    .ToList();

                foreach (var group in ordered)
                {
                    var id = group["id"]!.GetValue<string>();
                    var types = (group["types"] as JsonArray)?.Count ?? 0;

                    var note = R.SPAWNS_GROUP_UNUSED;
                    if (used.TryGetValue(id, out var use) && use.Any)
                    {
                        note = use.Roaming > 0
                            ? string.Format(R.SPAWNS_GROUP_ROAMS, use.Roaming)
                            : string.Format(R.SPAWNS_GROUP_WAVES, use.Waves);
                    }

                    var gate = difficultyNote(group);

                    groupBox.Items.Add(new ComboBoxItem
                    {
                        Content = $"{id}  ({types})   ·   {note}"
                            + (gate.Length > 0 ? "   ·   " + gate : ""),
                        Tag = group,
                    });
                }

                var back = -1;
                for (var at = 0; at < groupBox.Items.Count; at++)
                {
                    if (!ReferenceEquals((groupBox.Items[at] as ComboBoxItem)?.Tag, was)) { continue; }
                    back = at;
                    break;
                }

                if (groupBox.Items.Count > 0)
                {
                    groupBox.SelectedIndex = back >= 0 ? back : 0;
                }
            }
            finally
            {
                _filling = false;
            }

            //The rows belong to whichever group is showing, and after a refill that is the same
            //group as before - so they are rebuilt here rather than by a selection that did not
            //really change.
            fillMobs();
        }

        private JsonObject? chosenGroup => (groupBox.SelectedItem as ComboBoxItem)?.Tag as JsonObject;

        /// <summary>
        /// The difficulty a group or a mob is gated to, said plainly.
        ///
        /// This is the quietest trap in the file. lowcomplexity-group-diff1 carries
        /// "max-difficulty": 1, so putting a boss in it and playing at anything above difficulty
        /// one changes nothing whatsoever - the game never picks the group, falls through to the
        /// others, and you get the zombie horde instead. Nothing about the edit is wrong and
        /// nothing reports a problem.
        /// </summary>
        private static string difficultyNote(JsonObject? node)
        {
            if (node == null) { return string.Empty; }

            var low = node["min-difficulty"]?.GetValue<double>();
            var high = node["max-difficulty"]?.GetValue<double>();

            if (low != null && high != null)
            {
                return string.Format(R.SPAWNS_DIFF_RANGE, low, high);
            }

            if (high != null) { return string.Format(R.SPAWNS_DIFF_MAX, high); }
            if (low != null) { return string.Format(R.SPAWNS_DIFF_MIN, low); }

            return string.Empty;
        }

        private void groupBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_filling) { return; }
            groupChanged();
        }

        private void groupChanged()
            => fillMobs();

        private void fillMobs()
        {
            mobStack.Children.Clear();

            var group = chosenGroup;
            if (group?["types"] is not JsonArray types)
            {
                //An empty list is not the same as an empty room. The camp has no mob groups at
                //all, and a blank dropdown says nothing about why.
                mobsLabel.Text = groupBox.Items.Count == 0 ? R.SPAWNS_NO_GROUPS : "";
                return;
            }

            //Say what the rows below are. Two dropdowns with nothing between them reads as one
            //control repeated by mistake, which is exactly how it was read.
            mobsLabel.Text = string.Format(R.SPAWNS_MOBS_IN, types.Count);

            for (var at = 0; at < types.Count; at++)
            {
                var index = at;
                var row = new DockPanel { Margin = new Thickness(0, 0, 0, 4) };

                var drop = new Button { Content = "×", Padding = new Thickness(6, 1, 6, 1) };
                drop.Click += (_, _) =>
                {
                    types.RemoveAt(index);
                    _map.Changed.Add("level.json");
                    fillGroups();
                    updateUI();
                };
                DockPanel.SetDock(drop, Dock.Right);
                row.Children.Add(drop);

                //Not editable. The app's own ComboBox template keeps PART_EditableTextBox
                //permanently collapsed, so an editable one has nowhere to draw its text and comes
                //up blank however the value is set - which is exactly what these rows were doing.
                //Every mob the game has is in the list anyway, so there is nothing to type.
                var pick = new ComboBox { Height = 24 };
                foreach (var mob in GameMobs.ALL)
                {
                    pick.Items.Add(mob.Boss ? mob.Id + "  ★" : mob.Id);
                }

                //A type is written either as a bare name or as an object with weights beside it,
                //and both forms are in the game's own files. Whichever it was stays what it is.
                var node = types[index];
                var name = node is JsonValue value && value.TryGetValue<string>(out var bare)
                    ? bare
                    : node?["type"]?.GetValue<string>() ?? string.Empty;

                //Selecting beats typing. An editable ComboBox has no text box until its template
                //is applied, and it is not applied until the row reaches the visual tree - so a
                //Text set here is dropped on the floor and every mob shows up blank. SelectedItem
                //survives templating; Text is only a fallback for a mob the game never lists, and
                //it waits for Loaded before trying.
                var listed = GameMobs.ALL.FirstOrDefault(one => one.Id == name);
                var label = listed == null
                    ? name
                    : (listed.Boss ? listed.Id + "  ★" : listed.Id);

                //A mob the game does not list still has to be shown, or editing a group would
                //silently drop it.
                if (label.Length > 0 && !pick.Items.Contains(label)) { pick.Items.Insert(0, label); }
                if (label.Length > 0) { pick.SelectedItem = label; }

                pick.SelectionChanged += (_, _) =>
                {
                    var wanted = (pick.SelectedItem as string)?.Replace("★", string.Empty).Trim();
                    if (string.IsNullOrEmpty(wanted) || wanted == name) { return; }

                    if (types[index] is JsonObject asObject) { asObject["type"] = wanted; }
                    else { types[index] = JsonValue.Create(wanted); }

                    _map.Changed.Add("level.json");
                    fillGroups();
                    updateUI();
                };
                //A type can be gated too - cw-mix keeps its wraith behind difficulty 2 and its
                //necromancer behind 3 - so a mob that never turns up may be doing as it was told.
                var gate = difficultyNote(types[index] as JsonObject);
                if (gate.Length > 0)
                {
                    var mark = new TextBlock
                    {
                        Text = gate,
                        FontSize = 10,
                        VerticalAlignment = VerticalAlignment.Center,
                        Margin = new Thickness(0, 0, 6, 0),
                        Foreground = (Brush)FindResource("Brush.TextMuted"),
                    };
                    DockPanel.SetDock(mark, Dock.Right);
                    row.Children.Add(mark);
                }

                row.Children.Add(pick);

                mobStack.Children.Add(row);
            }
        }

        /// <summary>
        /// Makes a mob group where there was none, and points the mission at it.
        ///
        /// The camp has no groups at all and nothing set to roam - nothing is meant to spawn
        /// there - so a spawn point put in it draws from nothing. This is the missing half.
        /// </summary>
        private void addGroupButton_Click(object sender, RoutedEventArgs e)
        {
            var made = MapSpawns.addGroup(_map);

            fillGroups();

            //Show the one just made rather than leaving them to find it.
            for (var at = 0; at < groupBox.Items.Count; at++)
            {
                if (!ReferenceEquals((groupBox.Items[at] as ComboBoxItem)?.Tag, made)) { continue; }
                groupBox.SelectedIndex = at;
                break;
            }

            statusLabel.Text = string.Format(R.SPAWNS_GROUP_MADE,
                made["id"]?.GetValue<string>() ?? "?");
            updateUI();
        }

        private void addMobButton_Click(object sender, RoutedEventArgs e)
        {
            var group = chosenGroup;
            if (group == null) { return; }

            if (group["types"] is not JsonArray types)
            {
                types = new JsonArray();
                group["types"] = types;
            }

            types.Add(JsonValue.Create(GameMobs.ALL.FirstOrDefault()?.Id ?? "zombie"));
            _map.Changed.Add("level.json");

            fillGroups();
            updateUI();
        }

        //--- saving ---------------------------------------------------------------------------------

        private async void saveButton_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                var many = MapSpawns.save(_map);
                statusLabel.Text = string.Format(R.SPAWNS_SAVED, many, _map.Folder);

                //The installed mission is a single welded tile built FROM the object groups, so
                //saving spawns into those changes nothing until the weld is rebuilt. Done here
                //rather than left as a step to remember.
                if (System.IO.File.Exists(
                        System.IO.Path.Combine(_map.Folder, "level.json.multitile"))
                    && MapTools.available)
                {
                    saveButton.IsEnabled = false;
                    var run = await MapTools.weld(_map.Folder);
                    statusLabel.Text += run.Ok ? "  " + R.SPAWNS_REWELDED : "  " + run.Last;
                }
            }
            catch (Exception problem)
            {
                statusLabel.Text = problem.Message;
            }

            updateUI();
        }

        /// <summary>
        /// Saves, rebuilds, and puts it in the game - all of it.
        ///
        /// Save on its own leaves the job half done: the edits are in a folder the game has never
        /// heard of, and reaching it means knowing to go back to the Maps tab and press Import
        /// map. Nobody knew that, and the status line saying "Import folder installs what you
        /// just changed" named a button that does not exist under that name. So the window that
        /// made the change is the window that finishes it.
        /// </summary>
        private async void installButton_Click(object sender, RoutedEventArgs e)
        {
            if (_mission == null) { return; }

            installButton.IsEnabled = false;
            saveButton.IsEnabled = false;

            try
            {
                saveButton_Click(sender, e);

                //Saving welds, and welding is a process. Installing the folder before it finishes
                //would pack the mission as it was a moment ago.
                while (!saveButton.IsEnabled && _map.Changed.Count > 0)
                {
                    await System.Threading.Tasks.Task.Delay(100);
                }

                var mod = MapMod.install(_map.Folder, _mission);
                statusLabel.Text = string.Format(R.SPAWNS_INSTALLED,
                    _mission.Label, System.IO.Path.GetFileName(mod.Path), mod.Size / 1024);

                Installed?.Invoke();
            }
            catch (Exception problem)
            {
                statusLabel.Text = problem.Message;
            }

            updateUI();
        }

        private void revertButton_Click(object sender, RoutedEventArgs e)
        {
            //Throwing away unsaved work is worth one question.
            if (_map.Changed.Count > 0)
            {
                var answer = MessageBox.Show(R.SPAWNS_DISCARD, R.SPAWNS_TITLE,
                    MessageBoxButton.OKCancel, MessageBoxImage.Warning);
                if (answer != MessageBoxResult.OK) { return; }
            }

            var folder = _map.Folder;
            var owner = Owner;
            Close();

            var again = new SpawnsWindow(MapSpawns.load(folder)) { Owner = owner };
            again.Show();
        }

        private void updateUI()
        {
            var has = _room != null;
            placeButton.IsEnabled = has;
            clearButton.IsEnabled = has && _room!.Spawns > 0;
            saveButton.IsEnabled = _map.Changed.Count > 0;

            //A door can be added wherever the map is aimed; the other two need one picked out of
            //the list, because they act on that one rather than on wherever you are looking.
            onlyExitButton.IsEnabled = has;
            removeQuestButton.IsEnabled = has && _quest >= 0;

            addExitButton.IsEnabled = has;
            removeExitButton.IsEnabled = has && _exit >= 0;

            addStartButton.IsEnabled = has;
            removeStartButton.IsEnabled = has && _start >= 0;
            mainStartButton.IsEnabled = has && _start >= 0;

            addDoorButton.IsEnabled = has;
            removeDoorButton.IsEnabled = has && _door >= 0;
            entryDoorButton.IsEnabled = has && _door >= 0;

            roomLabel.Text = _room == null
                ? R.SPAWNS_NO_ROOM
                : $"{_room.Stretch} — {_room.Id}  {_room.Size[0]}×{_room.Size[1]}×{_room.Size[2]}";

            changedLabel.Text = _map.Changed.Count == 0
                ? R.SPAWNS_NO_CHANGES
                : string.Format(R.SPAWNS_CHANGES, _map.Changed.Count);
        }
    }
}
