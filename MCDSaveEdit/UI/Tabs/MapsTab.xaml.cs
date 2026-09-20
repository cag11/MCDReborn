using MCDSaveEdit.Logic;
using MCDSaveEdit.Services;
using Microsoft.Win32;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.IO;
using System.Threading.Tasks;
#nullable enable

namespace MCDSaveEdit.UI
{
    /// <summary>
    /// Taking a mission out of the game to work on, and putting one back.
    ///
    /// A mission in this game is data rather than an Unreal map - JSON naming a sequence of
    /// stretches, and tiles whose geometry is Minecraft blocks - so both halves of this are file
    /// moves. Export writes everything a mission is made of into a folder; import writes a folder
    /// back as a mod pak over whichever mission you choose. The game's own files are never
    /// touched, and Remove deletes the pak and gives the original mission back.
    /// </summary>
    public partial class MapsTab : UserControl
    {
        private IReadOnlyList<GameMaps.Mission> _missions = new List<GameMaps.Mission>();
        private GameMaps.Mission? _chosen;
        private string? _folder;
        private bool _busy;

        public MapsTab()
        {
            InitializeComponent();
            setStrings();

            //Read when the tab is first looked at rather than when the window is built: the paks
            //are not loaded at construction, so a list built then would be permanently empty with
            //nothing to say why.
            IsVisibleChanged += (_, _) => { if (IsVisible) { refresh(); } };
        }

        private void setStrings()
        {
            missionsLabel.Content = R.MAPS_MISSIONS;
            missionsHint.Text = R.MAPS_HINT;
            exportButton.Content = R.MAPS_EXPORT;
            exportButton.ToolTip = R.MAPS_EXPORT_WHY;
            clearButton.Content = R.MAPS_CLEAR;
            clearButton.ToolTip = R.MAPS_CLEAR_WHY;
            importButton.Content = R.MAPS_IMPORT;
            importButton.ToolTip = R.MAPS_IMPORT_WHY;
            removeButton.Content = R.MAPS_REMOVE;
            fixedToMinecraftButton.Content = R.MAPS_FIXED_TO_MINECRAFT;
            fixedToMinecraftButton.ToolTip = R.MAPS_FIXED_TO_MINECRAFT_WHY;
            spawnsButton.Content = R.MAPS_SPAWNS;
            spawnsButton.ToolTip = R.MAPS_SPAWNS_WHY;
            fromMinecraftButton.Content = R.MAPS_FROM_MINECRAFT;
            fromMinecraftButton.ToolTip = R.MAPS_FROM_MINECRAFT_WHY;

            terrainGroupLabel.Text = R.MAPS_GROUP_TERRAIN;
            spawnsGroupLabel.Text = R.MAPS_GROUP_SPAWNS;
            spawnsWhyLabel.Text = R.MAPS_GROUP_SPAWNS_WHY;
            undoWhyLabel.Text = R.MAPS_GROUP_UNDO_WHY;
            undoLabel.Text = R.MAPS_GROUP_UNDO;
        }

        public void refresh()
        {
            if (_missions.Count == 0 && CustomSkins.ready)
            {
                _missions = GameMaps.all();
            }

            fillList();
            fillInstalled();
            updateUI();
        }

        private void fillList()
        {
            var wanted = searchBox.Text?.Trim() ?? string.Empty;
            searchHint.Visibility = wanted.Length == 0 ? Visibility.Visible : Visibility.Collapsed;

            //Matched against the readable label as well as the file name, so "creeper" finds
            //creeperwoods - which is the reason the names are spelled out at all.
            var shown = _missions
                .Where(one => wanted.Length == 0
                    || one.Label.IndexOf(wanted, StringComparison.OrdinalIgnoreCase) >= 0)
                .ToList();

            missionList.ItemsSource = shown.Select(one => new
            {
                Mission = one,
                Text = $"{one.Label}   —   {one.Bytes / 1024:N0} KB"
                     + (MapMod.installedFor(one) != null ? "   ·   replaced" : string.Empty),
            }).ToList();
            missionList.DisplayMemberPath = "Text";
        }

        private void fillInstalled()
        {
            installedStack.Children.Clear();

            foreach (var mod in MapMod.installed())
            {
                var row = new DockPanel { Margin = new Thickness(0, 0, 0, 4) };

                var drop = new Button { Content = R.MAPS_REMOVE, Padding = new Thickness(6, 2, 6, 2) };
                drop.Click += (_, _) =>
                {
                    try
                    {
                        System.IO.File.Delete(mod);
                        statusLabel.Text = string.Format(R.MAPS_REMOVED,
                            System.IO.Path.GetFileName(mod));
                        fillInstalled();
                        fillList();
                    }
                    catch (Exception problem) { statusLabel.Text = problem.Message; }
                };
                DockPanel.SetDock(drop, Dock.Right);
                row.Children.Add(drop);

                row.Children.Add(new TextBlock
                {
                    Text = System.IO.Path.GetFileName(mod),
                    VerticalAlignment = VerticalAlignment.Center,
                    TextTrimming = TextTrimming.CharacterEllipsis,
                });

                installedStack.Children.Add(row);
            }
        }

        private void updateUI()
        {
            var ready = CustomSkins.ready && !_busy;
            var tools = MapTools.available;

            exportButton.IsEnabled = ready && _chosen != null;
            clearButton.IsEnabled = ready && _chosen != null
                && Directory.Exists(workshopFor(_chosen));
            importButton.IsEnabled = ready && _chosen != null;
            removeButton.IsEnabled = ready && _chosen != null && MapMod.installedFor(_chosen) != null;

            fixedToMinecraftButton.IsEnabled = ready && tools && _chosen != null;

            //The spawn editor needs no converter - it reads the exported folder, which the tab
            //writes on its own.
            spawnsButton.IsEnabled = ready && _chosen != null;
            fromMinecraftButton.IsEnabled = ready && tools && _chosen != null;

            toolsLabel.Text = tools
                ? (MapTools.saves == null ? R.MAPS_NO_MINECRAFT : string.Empty)
                : string.Format(R.MAPS_NO_TOOLS, MapTools.wanted);

            chosenLabel.Text = _chosen?.Label ?? R.MAPS_NONE_CHOSEN;
            chosenDetail.Text = _chosen == null
                ? string.Empty
                : $"{_chosen.PakPath}   —   {_chosen.Bytes / 1024:N0} KB";

            importedLabel.Text = _folder == null
                ? string.Empty
                : string.Format(R.MAPS_FOLDER_CHOSEN, System.IO.Path.GetFileName(
                    _folder.TrimEnd(System.IO.Path.DirectorySeparatorChar)));
        }

        private void searchBox_TextChanged(object sender, TextChangedEventArgs e) => fillList();

        private void missionList_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            dynamic? picked = missionList.SelectedItem;
            _chosen = picked?.Mission as GameMaps.Mission;
            updateUI();
        }

        private void exportButton_Click(object sender, RoutedEventArgs e)
        {
            if (_chosen == null) { return; }

            //No folder picker. Every other button here already works on a folder the app keeps
            //for the mission - Edit in Minecraft exports without asking, Edit spawns and Save and
            //install never ask - so making this one button demand an address made it look like a
            //different kind of operation, and answering it with somewhere unexpected sent the
            //rest of the tab looking in the wrong place.
            var folder = MapWorkshop.folderFor(_chosen.Name);

            //Exporting again rebuilds the folder from the game's paks, which is the right thing
            //to want and the wrong thing to do by accident on top of an afternoon's editing.
            if (MapWorkshop.exported(_chosen.Name))
            {
                var answer = MessageBox.Show(
                    string.Format(R.MAPS_ALREADY_EXPORTED, folder),
                    R.MAPS_EXPORT, MessageBoxButton.OKCancel, MessageBoxImage.Warning);

                if (answer != MessageBoxResult.OK) { return; }
            }

            try
            {
                //Emptied first, always. Writing an export on top of an old one leaves behind
                //whatever the new mission has no file for - an object group that used to exist,
                //a working file from a half-finished weld - and the leftovers are packed and
                //installed along with everything else.
                if (Directory.Exists(folder)) { erase(folder); }

                var made = MapMod.export(_chosen, folder);
                _folder = made.Folder;

                MapWorkshop.remember(_chosen.Name, made.Folder);

                statusLabel.Text = string.Format(R.MAPS_EXPORTED,
                    made.Files, made.Bytes / 1024, made.Folder);

                foreach (var note in made.Notes.Take(2))
                {
                    statusLabel.Text += "   ·   " + note;
                }
            }
            catch (Exception problem)
            {
                statusLabel.Text = problem.Message;
            }

            updateUI();
        }

        private void importButton_Click(object sender, RoutedEventArgs e)
        {
            if (_chosen == null) { return; }

            var picker = new OpenFolderDialog { Title = R.MAPS_IMPORT_PICK };
            if (picker.ShowDialog() != true) { return; }

            try
            {
                //Said plainly before anything is written. Installing a map over a mission other
                //than the one it came from is a real thing to want, but doing it by accident -
                //because the wrong row was selected - is not, and afterwards it looks like the
                //export was broken rather than like it went somewhere else.
                var from = MapMod.cameFrom(picker.FolderName);
                if (from != null && !string.Equals(from, _chosen.Name, StringComparison.OrdinalIgnoreCase))
                {
                    var answer = MessageBox.Show(
                        string.Format(R.MAPS_DIFFERENT_MISSION, GameMaps.prettyName(from), _chosen.Label),
                        R.MAPS_IMPORT, MessageBoxButton.OKCancel, MessageBoxImage.Question);

                    if (answer != MessageBoxResult.OK) { return; }
                }

                var mod = MapMod.install(picker.FolderName, _chosen);
                _folder = picker.FolderName;

                //Importing from somewhere says where this mission lives just as plainly as
                //exporting to it does.
                MapWorkshop.remember(_chosen.Name, picker.FolderName);

                statusLabel.Text = string.Format(R.MAPS_IMPORTED,
                    _chosen.Label, System.IO.Path.GetFileName(mod.Path), mod.Size / 1024);

                fillInstalled();
                fillList();
            }
            catch (Exception problem)
            {
                statusLabel.Text = problem.Message;
            }

            updateUI();
        }

        /// <summary>
        /// Where a mission is kept while it is being worked on.
        ///
        /// Not asked for. The folder is scaffolding - the mission comes out of the paks and goes
        /// back into them, and the only thing anybody wants to see in between is the Minecraft
        /// world. A folder picker here would be a question with one sensible answer.
        /// </summary>
        /// <summary>
        /// Where this mission's files are.
        ///
        /// Wherever the last export put them, which is not necessarily under AppData - Export map
        /// asks, and answering "Desktop" used to leave every other button looking somewhere else.
        /// </summary>
        private static string workshopFor(GameMaps.Mission mission)
            => MapWorkshop.folderFor(mission.Name);

        /// <summary>
        /// The whole mission, start to end, as one world.
        ///
        /// Pinned first, because a stretch that still picks at random has no single room to
        /// place, and then laid out room by room against the doors of the one before.
        /// </summary>
        private async void fixedToMinecraftButton_Click(object sender, RoutedEventArgs e)
        {
            if (_chosen == null) { return; }

            var mission = _chosen;
            var folder = workshopFor(mission);

            _busy = true;
            statusLabel.Text = string.Format(R.MAPS_WORKING, mission.Name);
            updateUI();

            try
            {
                var made = await Task.Run(() => MapMod.export(mission, folder));
                _folder = made.Folder;

                //This route exports too, so it settles where the mission lives just as much as
                //Export map does - and Edit spawns afterwards should find the same folder.
                MapWorkshop.remember(mission.Name, made.Folder);

                var fix = await MapTools.makeFixed(folder);
                if (!fix.Ok)
                {
                    statusLabel.Text = fix.Last.Length > 0 ? fix.Last : R.MAPS_CONVERT_FAILED;
                }
                else
                {
                    var run = await MapTools.toMinecraftLevel(folder, mission);

                    //Handed over as it was written, and Minecraft upgrades it on open.
                    //
                    //There used to be a conversion here, to spare somebody the "this world was
                    //made in an older version" prompt. It was not worth it. Minecraft's own
                    //upgrade is the better one - it relights the world, recomputes heightmaps and
                    //fixes up everything a converter has to guess at - and the upgrade being
                    //irreversible costs nothing, because coming home converts back down anyway.
                    //
                    //The conversion on the way IN is the one that has to exist. This one only
                    //replaced a job Minecraft already does well with a job we do adequately.
                    statusLabel.Text = run.Ok && run.World != null
                        ? string.Format(R.MAPS_WORLD_READY_FIXED, Path.GetFileName(run.World))
                        : (run.Last.Length > 0 ? run.Last : R.MAPS_CONVERT_FAILED);
                }
            }
            catch (Exception problem)
            {
                statusLabel.Text = problem.Message;
            }

            _busy = false;
            updateUI();
        }

        private async void fromMinecraftButton_Click(object sender, RoutedEventArgs e)
        {
            if (_chosen == null) { return; }

            var picker = new OpenFolderDialog { Title = R.MAPS_PICK_WORLD };
            if (MapTools.saves != null) { picker.InitialDirectory = MapTools.saves; }
            if (picker.ShowDialog() != true) { return; }

            //A whole-level world holds rooms from several object groups and carries its own note
            //saying which file each belongs to, so it comes back a different way.
            if (MapTools.isWholeLevel(picker.FolderName))
            {
                await bringBackLevel(picker.FolderName, _chosen);
                return;
            }

            var origin = MapTools.originOf(picker.FolderName);
            if (origin == null)
            {
                //Every world this tab makes is left a note saying which object group it came
                //from. One without that note is either somebody else's world or one that was
                //renamed, and writing its tiles into a guessed file would ruin a different map.
                statusLabel.Text = R.MAPS_NOT_OURS;
                return;
            }

            var mission = _chosen;

            if (!string.Equals(origin.Mission, mission.Name, StringComparison.OrdinalIgnoreCase))
            {
                var answer = MessageBox.Show(
                    string.Format(R.MAPS_DIFFERENT_MISSION,
                        GameMaps.prettyName(origin.Mission), mission.Label),
                    R.MAPS_FROM_MINECRAFT, MessageBoxButton.OKCancel, MessageBoxImage.Question);

                if (answer != MessageBoxResult.OK) { return; }
            }

            _busy = true;
            statusLabel.Text = string.Format(R.MAPS_WORKING, origin.Group);
            updateUI();

            try
            {
                var run = await MapTools.fromMinecraft(picker.FolderName, origin);
                if (!run.Ok)
                {
                    statusLabel.Text = run.Last.Length > 0 ? run.Last : R.MAPS_CONVERT_FAILED;
                }
                else
                {
                    var mod = await Task.Run(() => MapMod.install(origin.Map, mission));
                    _folder = origin.Map;

                    statusLabel.Text = string.Format(R.MAPS_IMPORTED,
                        mission.Label, Path.GetFileName(mod.Path), mod.Size / 1024);

                    fillInstalled();
                    fillList();
                }
            }
            catch (Exception problem)
            {
                statusLabel.Text = problem.Message;
            }

            _busy = false;
            updateUI();
        }

        private async Task bringBackLevel(string world, GameMaps.Mission mission)
        {
            _busy = true;
            statusLabel.Text = string.Format(R.MAPS_WORKING, mission.Name);
            updateUI();

            try
            {
                //Down to something the reader understands, first. A world a modern client has
                //touched is laid out in a way nothing downstream has ever seen, and Minecraft's
                //own upgrader only ever goes the other way.
                if (MapTools.canConvert)
                {
                    statusLabel.Text = string.Format(R.MAPS_LEGACYISING, mission.Label);
                    var drop = await MapTools.convert(world, MapTools.LEGACY);
                    if (!drop.Ok)
                    {
                        statusLabel.Text = drop.Last.Length > 0 ? drop.Last : R.MAPS_CONVERT_FAILED;
                        _busy = false;
                        updateUI();
                        return;
                    }
                }

                var run = await MapTools.fromMinecraftLevel(world);
                if (!run.Ok)
                {
                    statusLabel.Text = run.Last.Length > 0 ? run.Last : R.MAPS_CONVERT_FAILED;
                }
                else
                {
                    var folder = workshopFor(mission);

                    //Not every level survives welding - the camp's tiles declare its teleports,
                    //and a merged tile declares nothing. See MapTools.weldable.
                    if (!MapTools.weldable(folder, out var whyNot))
                    {
                        statusLabel.Text = string.Format(R.MAPS_NOT_WELDABLE, whyNot);

                        var straight = await Task.Run(() => MapMod.install(folder, mission));
                        _folder = folder;

                        statusLabel.Text += "   " + string.Format(R.MAPS_IMPORTED,
                            mission.Label, Path.GetFileName(straight.Path), straight.Size / 1024);

                        fillInstalled();
                        fillList();
                        _busy = false;
                        updateUI();
                        return;
                    }

                    //Welded before it is installed. A pinned level still leaves the generator a
                    //chain of rooms to connect, and it can fail to find an arrangement - which
                    //arrives as a crash on the loading screen, not an error. One tile has nothing
                    //to connect.
                    var weld = await MapTools.weld(folder);
                    if (!weld.Ok)
                    {
                        statusLabel.Text = weld.Last.Length > 0 ? weld.Last : R.MAPS_CONVERT_FAILED;
                        _busy = false;
                        updateUI();
                        return;
                    }

                    var mod = await Task.Run(() => MapMod.install(folder, mission));
                    _folder = folder;

                    statusLabel.Text = string.Format(R.MAPS_IMPORTED,
                        mission.Label, Path.GetFileName(mod.Path), mod.Size / 1024);

                    fillInstalled();
                    fillList();
                }
            }
            catch (Exception problem)
            {
                statusLabel.Text = problem.Message;
            }

            _busy = false;
            updateUI();
        }

        /// <summary>
        /// Opens the mission in its own window: the rooms from above, their spawn points, and the
        /// mob groups that decide what appears.
        ///
        /// It works on the exported folder rather than the paks, so the mission has to have been
        /// exported once - and it saves straight back there, which is the whole reason this is a
        /// window here rather than a page somewhere else.
        /// </summary>
        private async void spawnsButton_Click(object sender, RoutedEventArgs e)
            => await openSpawns().ConfigureAwait(true);

        /// <summary>
        /// What Edit spawns does, as something that can be awaited.
        ///
        /// Split out so a probe can run the button itself - exporting, welding and
        /// opening - instead of a copy of its steps, which is the arrangement where the
        /// test passes and the button does not.
        /// </summary>
        internal async Task openSpawns()
        {
            if (_chosen == null) { return; }

            var mission = _chosen;
            var folder = workshopFor(mission);

            //Exports itself when there is nothing to open. Telling somebody to press Export map
            //first is a step that exists only because the app would not take it - and the folder
            //is the app's own working copy, not something anybody chose to make.
            //
            //It does NOT start again when a folder is already there. That folder is where your
            //spawn points live, and rebuilding it from the game's files on every open would
            //throw away the work of whoever opened the editor twice. Clear is the deliberate way
            //to start over, and it asks first.
            if (!MapWorkshop.exported(mission.Name))
            {
                //A folder that exists without a level in it is a failed or half-deleted export,
                //and exporting on top of it leaves the leftovers behind. That one is cleared.
                if (Directory.Exists(folder))
                {
                    try { erase(folder); } catch (Exception) { }
                }


                _busy = true;
                statusLabel.Text = string.Format(R.MAPS_EXPORTING, mission.Label);
                updateUI();

                try
                {
                    var made = await Task.Run(() => MapMod.export(mission, folder));
                    MapWorkshop.remember(mission.Name, made.Folder);
                    _folder = made.Folder;
                    folder = made.Folder;

                    statusLabel.Text = string.Format(R.MAPS_EXPORTED,
                        made.Files, made.Bytes / 1024, made.Folder);
                }
                catch (Exception problem)
                {
                    statusLabel.Text = problem.Message;
                    _busy = false;
                    updateUI();
                    return;
                }
                finally
                {
                    _busy = false;
                    updateUI();
                }
            }

            //Welded first, so a mission is one place here as well as in Minecraft and in the game.
            //
            //The alternative is editing a sequence of stretches, which is how the file is written
            //but not how anybody thinks about a level - and worse, it is not what gets installed.
            //Bringing a world back from Minecraft welds before installing, because a pinned level
            //still leaves the generator a chain of rooms to connect and it can fail to find an
            //arrangement, which arrives as a crash on the loading screen rather than an error.
            //Editing the unwelded form would mean editing something the game never sees.
            //
            //It costs the mission its shuffle: the generator stops choosing tiles and every run
            //is the same level. That is the trade, and it is the one worth making for a map
            //somebody built on purpose.
            if (!System.IO.File.Exists(System.IO.Path.Combine(folder, "level.json.multitile")))
            {
                if (!MapTools.available)
                {
                    //Without the converters there is no weld, so the rooms are shown as they are
                    //rather than refusing to open at all.
                    statusLabel.Text = R.MAPS_NO_WELD;
                }
                else
                {
                    _busy = true;
                    statusLabel.Text = string.Format(R.MAPS_WELDING, mission.Label);
                    updateUI();

                    try
                    {
                        var fix = await MapTools.makeFixed(folder);
                        //Pinning is safe for anything; welding is not. A level whose tiles carry
                        //teleports is shown as it is rather than merged into something that has
                        //none of them.
                        var weld = fix.Ok && MapTools.weldable(folder, out _)
                            ? await MapTools.weld(folder)
                            : fix;

                        if (!weld.Ok)
                        {
                            statusLabel.Text = weld.Last.Length > 0
                                ? weld.Last
                                : R.MAPS_CONVERT_FAILED;
                            _busy = false;
                            updateUI();
                            return;
                        }

                        statusLabel.Text = string.Format(R.MAPS_WELDED, mission.Label);
                    }
                    finally
                    {
                        _busy = false;
                        updateUI();
                    }
                }
            }

            try
            {
                var window = new SpawnsWindow(MapSpawns.load(folder), mission)
                {
                    Owner = Window.GetWindow(this),
                };

                //Whatever it installs, this tab should stop claiming the mission is untouched.
                window.Installed += () => { fillInstalled(); fillList(); };
                window.Show();
            }
            catch (Exception problem)
            {
                statusLabel.Text = problem.Message;
            }
        }

        /// <summary>
        /// Throws the exported folder away, so the next export starts from the game's own files.
        ///
        /// Worth a button because the folder is not somewhere anybody would go looking - it sits
        /// under AppData unless it came from somewhere else - and because Windows will not always
        /// let you delete it by hand once the app has been near it.
        ///
        /// This does not touch the game. A mission installed over is undone by Remove; clearing
        /// only gets rid of the working copy.
        /// </summary>
        private void clearButton_Click(object sender, RoutedEventArgs e)
        {
            if (_chosen == null) { return; }

            var folder = workshopFor(_chosen);
            if (!Directory.Exists(folder))
            {
                statusLabel.Text = string.Format(R.MAPS_NOTHING_TO_CLEAR, _chosen.Label);
                return;
            }

            var files = 0;
            var edited = 0;
            try
            {
                foreach (var file in Directory.GetFiles(folder, "*", SearchOption.AllDirectories))
                {
                    files++;
                    //A .before is only written when something was changed, so counting them says
                    //whether this folder holds an afternoon's work or just an export.
                    if (file.EndsWith(".before", StringComparison.OrdinalIgnoreCase)) { edited++; }
                }
            }
            catch (Exception)
            {
                //Counting is for the warning, not for the delete.
            }

            var answer = MessageBox.Show(
                string.Format(R.MAPS_CLEAR_CONFIRM, folder, files)
                    + (edited > 0 ? Environment.NewLine + Environment.NewLine
                        + string.Format(R.MAPS_CLEAR_EDITED, edited) : string.Empty),
                R.MAPS_CLEAR, MessageBoxButton.OKCancel,
                edited > 0 ? MessageBoxImage.Warning : MessageBoxImage.Question);

            if (answer != MessageBoxResult.OK) { return; }

            try
            {
                erase(folder);
                statusLabel.Text = string.Format(R.MAPS_CLEARED, folder);
                _folder = null;
            }
            catch (Exception problem)
            {
                statusLabel.Text = problem.Message;
            }

            updateUI();
        }

        /// <summary>
        /// Deletes a folder and everything under it.
        ///
        /// Tried more than once, because Windows releases the directory handle after the contents
        /// go: a single Delete leaves the empty folder standing often enough that it looks like
        /// the clear did nothing.
        /// </summary>
        internal static void erase(string folder)
        {
            Directory.Delete(folder, true);

            for (var tries = 0; tries < 5 && Directory.Exists(folder); tries++)
            {
                System.Threading.Thread.Sleep(60);
                try { Directory.Delete(folder, true); } catch (Exception) { }
            }
        }

        /// <summary>Chooses a mission the way clicking the list would, for a probe.</summary>
        internal bool probePick(string name)
        {
            refresh();

            for (var at = 0; at < missionList.Items.Count; at++)
            {
                dynamic? row = missionList.Items[at];
                if ((row?.Mission as GameMaps.Mission)?.Name != name) { continue; }
                missionList.SelectedIndex = at;
                return _chosen != null;
            }

            return false;
        }

        /// <summary>What the tab is saying, for a probe to read back.</summary>
        internal string probeStatus => statusLabel.Text;

        private void removeButton_Click(object sender, RoutedEventArgs e)
        {
            if (_chosen == null) { return; }

            try
            {
                statusLabel.Text = MapMod.remove(_chosen)
                    ? string.Format(R.MAPS_PUT_BACK, _chosen.Label)
                    : string.Format(R.MAPS_NOT_REPLACED, _chosen.Label);

                fillInstalled();
                fillList();
            }
            catch (Exception problem)
            {
                statusLabel.Text = problem.Message;
            }

            updateUI();
        }
    }
}
