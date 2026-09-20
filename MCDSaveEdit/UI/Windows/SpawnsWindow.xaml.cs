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
            xBox.Text = "500"; yBox.Text = "40"; zBox.Text = "180";
            placeButton_Click(this, new RoutedEventArgs());
            return mapView.cameraNow == before;
        }

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
            rulesLabel.Content = R.SPAWNS_RULES;
            rulesHint.Text = R.SPAWNS_RULES_HINT;
            addMobButton.Content = R.SPAWNS_ADD_MOB;
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
            if (group?["types"] is not JsonArray types) { mobsLabel.Text = ""; return; }

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

            roomLabel.Text = _room == null
                ? R.SPAWNS_NO_ROOM
                : $"{_room.Stretch} — {_room.Id}  {_room.Size[0]}×{_room.Size[1]}×{_room.Size[2]}";

            changedLabel.Text = _map.Changed.Count == 0
                ? R.SPAWNS_NO_CHANGES
                : string.Format(R.SPAWNS_CHANGES, _map.Changed.Count);
        }
    }
}
