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
            clearButton.Content = R.MAPS_CLEAR;
            clearButton.ToolTip = R.MAPS_CLEAR_WHY;
            baselineButton.Content = R.MAPS_BASELINE;
            baselineButton.ToolTip = R.MAPS_BASELINE_WHY;
            importButton.Content = R.MAPS_IMPORT;
            importButton.ToolTip = R.MAPS_IMPORT_WHY;
            removeButton.Content = R.MAPS_REMOVE;
            fixedToMinecraftButton.Content = R.MAPS_FIXED_TO_MINECRAFT;
            fixedToMinecraftButton.ToolTip = R.MAPS_FIXED_TO_MINECRAFT_WHY;
            spawnsButton.Content = R.MAPS_SPAWNS;
            spawnsButton.ToolTip = R.MAPS_SPAWNS_WHY;
            unweldedButton.Content = R.MAPS_UNWELDED;
            unweldedButton.ToolTip = R.MAPS_UNWELDED_WHY;
            fromMinecraftButton.Content = R.MAPS_FROM_MINECRAFT;
            fromMinecraftButton.ToolTip = R.MAPS_FROM_MINECRAFT_WHY;

            terrainGroupLabel.Text = R.MAPS_GROUP_TERRAIN;
            spawnsGroupLabel.Text = R.MAPS_GROUP_SPAWNS;
            spawnsWhyLabel.Text = R.MAPS_GROUP_SPAWNS_WHY;
            undoWhyLabel.Text = R.MAPS_GROUP_UNDO_WHY;
            undoLabel.Text = R.MAPS_GROUP_UNDO;
            //Built here rather than in XAML so the three labels are localised like everything
            //else. SelectedIndex is set last, and _showing guards the handler it raises.
            if (kindBox.Items.Count == 0)
            {
                _showing = true;
                kindBox.Items.Add(R.MAPS_KIND_CUSTOM);
                kindBox.Items.Add(R.MAPS_KIND_GAME);
                kindBox.SelectedIndex = 0;
                _showing = false;
            }

            inGameBox.Content = R.MAPS_IN_GAME;
            inGameBox.ToolTip = R.MAPS_IN_GAME_WHY;

            //These three were added without being registered here, and the asterisks in the
            //XAML are exactly what that looks like: a *Caption* is the placeholder convention,
            //left visible on purpose so a control nobody localised cannot ship looking finished.
            renameButton.Content = R.MAPS_RENAME;
            renameButton.ToolTip = R.MAPS_RENAME_WHY;
            zipOutButton.Content = R.MAPS_ZIP_SAVE;
            zipOutButton.ToolTip = R.MAPS_ZIP_SAVE_WHY;
            zipInButton.Content = R.MAPS_ZIP_OPEN;
            zipInButton.ToolTip = R.MAPS_ZIP_OPEN_WHY;
        }

        /// <summary>
        /// Turns the Camp's custom-map prop on or off.
        ///
        /// Guarded against its own refresh: setting IsChecked in code raises Checked, which would
        /// come straight back in here and reinstall - so a tick the person did and a tick the app
        /// did would be indistinguishable, and opening the tab would quietly rewrite two paks.
        /// </summary>
        private bool _settingTheBox;

        private void inGameBox_Changed(object sender, RoutedEventArgs e)
        {
            if (_settingTheBox) { return; }

            try
            {
                MapSlots.inGame = inGameBox.IsChecked == true;
            }
            catch (Exception problem)
            {
                MessageBox.Show(problem.Message, R.ERROR, MessageBoxButton.OK,
                    MessageBoxImage.Warning);
            }

            fillList();
        }

        public void refresh()
        {
            _settingTheBox = true;
            try { inGameBox.IsChecked = MapSlots.inGame; }
            finally { _settingTheBox = false; }

            if (_missions.Count == 0 && CustomSkins.ready)
            {
                _missions = GameMaps.all();
            }

            fillList();
            fillInstalled();

            updateUI();
        }

        /// <summary>
        /// Shows the chosen map's block theme, and changes it.
        ///
        /// A theme is a resource pack: it says what a block NAME looks like, and a map stores
        /// its blocks by name. So this is a re-skin - no geometry moves, nothing is rebuilt, and
        /// switching back is the same edit in reverse. That is the only reason it can be a
        /// dropdown that applies the moment it is used.
        ///
        /// The edit lands in the working folder. Installing is what puts it in the game, and a
        /// map that is already installed is reinstalled here so the two do not disagree - a
        /// theme shown in the app and not in the game would be worse than no dropdown at all.
        /// </summary>
        private void themeBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_showing || _chosen == null) { return; }

            var wanted = themeBox.SelectedItem as string;
            if (wanted == null) { return; }

            var folder = workshopFor(_chosen);

            if (!MapMod.setTheme(folder, wanted))
            {
                statusLabel.Text = string.Format(R.MAPS_THEME_FAILED, _chosen.Label);
                showTheme();
                return;
            }

            statusLabel.Text = string.Format(R.MAPS_THEME_SET, wanted, _chosen.Label);

            //Only when it is already in the game. Re-installing a map nobody has installed would
            //put it there, which is not what changing how it looks asked for.
            try
            {
                if (_chosen.IsSlot && MapSlots.inSlot(_chosen.Slot) != null)
                {
                    MapSlots.install(folder, _chosen.Slot,
                        MapSlots.inSlot(_chosen.Slot)!.Name);

                    statusLabel.Text += "   " + R.MAPS_THEME_REINSTALLED;
                }
                else if (!_chosen.IsSlot && MapMod.installedFor(_chosen) != null)
                {
                    installFrom(_chosen, folder);
                    statusLabel.Text += "   " + R.MAPS_THEME_REINSTALLED;
                }
            }
            catch (Exception problem)
            {
                statusLabel.Text += "   " + problem.Message;
            }

            fillList();
        }

        /// <summary>Puts the chosen map's theme in the box without setting it off.</summary>
        private void showTheme()
        {
            var themes = BlockPalette.themes();

            _showing = true;
            try
            {
                if (themeBox.Items.Count == 0)
                {
                    foreach (var one in themes) { themeBox.Items.Add(one); }
                }

                var folder = _chosen == null ? null : workshopFor(_chosen);
                var now = folder == null ? null : MapMod.themeOf(folder);

                themeBox.SelectedItem = now;

                //Said when there is no map to have a theme, rather than left showing the last
                //one looked at - which reads as this map being drawn that way.
                themeLabel.Text = now == null ? R.MAPS_THEME_NONE : R.MAPS_THEME;
                themeBox.IsEnabled = now != null;
            }
            finally { _showing = false; }
        }

        /// <summary>Guards the dropdown's own handler while the list is being built.</summary>
        private bool _showing;

        private void kindBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_showing) { return; }

            fillList();
        }

        private void fillList()
        {
            var wanted = searchBox.Text?.Trim() ?? string.Empty;
            searchHint.Visibility = wanted.Length == 0 ? Visibility.Visible : Visibility.Collapsed;

            //Matched against the readable label as well as the file name, so "creeper" finds
            //creeperwoods - which is the reason the names are spelled out at all.
            //The game's own, then the custom slots. Read fresh rather than cached, because a
            //slot's contents change from this very tab and a stale list would have somebody
            //overwrite a map they meant to keep.
            //One kind or the other, never both. Index rather than text, because the labels are
            //translated and comparing against an English string would quietly show the wrong
            //list in every other language.
            var all = new List<GameMaps.Mission>(kindBox.SelectedIndex == 1
                ? _missions
                : MapSlots.missions());

            var shown = all
                .Where(one => wanted.Length == 0
                    || one.Label.IndexOf(wanted, StringComparison.OrdinalIgnoreCase) >= 0)
                .ToList();

            missionList.ItemsSource = shown.Select(one => new
            {
                Mission = one,
                Text = one.IsSlot
                    ? $"{one.Label}"
                        + (one.Bytes > 0 ? $"   —   {one.Bytes / 1024:N0} KB" : string.Empty)
                        + (MapSlots.inSlot(one.Slot) != null ? "   ·   installed" : string.Empty)
                    : $"{one.Label}   —   {one.Bytes / 1024:N0} KB"
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
            showTheme();

            var ready = CustomSkins.ready && !_busy;
            var tools = MapTools.available;

            clearButton.IsEnabled = ready && _chosen != null
                && Directory.Exists(workshopFor(_chosen));
            importButton.IsEnabled = ready && _chosen != null;

            //Needs a mission chosen, because an empty map is not a thing on its own - it is
            //something that gets installed OVER a mission, same as any other custom map.
            baselineButton.IsEnabled = ready && _chosen != null && !_busy && MapTools.available;
            removeButton.IsEnabled = ready && _chosen != null && (_chosen.IsSlot
                ? MapSlots.inSlot(_chosen.Slot) != null
                : MapMod.installedFor(_chosen) != null);

            fixedToMinecraftButton.IsEnabled = ready && tools && _chosen != null;

            //The spawn editor needs no converter - it reads the exported folder, which the tab
            //writes on its own.
            spawnsButton.IsEnabled = ready && _chosen != null;

            //A custom slot has no unwelded form: it was built as one tile, so there is nothing
            //for the weld to have flattened and nothing here to compare against.
            unweldedButton.Visibility = _chosen is { IsSlot: false }
                ? Visibility.Visible : Visibility.Collapsed;
            unweldedButton.IsEnabled = ready && _chosen is { IsSlot: false };
            fromMinecraftButton.IsEnabled = ready && tools && _chosen != null;

            toolsLabel.Text = tools
                ? (MapTools.saves == null ? R.MAPS_NO_MINECRAFT : string.Empty)
                : string.Format(R.MAPS_NO_TOOLS, MapTools.wanted);

            chosenLabel.Text = _chosen?.Label ?? R.MAPS_NONE_CHOSEN;
            chosenDetail.Text = _chosen is { IsSlot: true } slotted
                ? (slotted.Bytes > 0
                    ? string.Format(R.MAPS_SLOT_WORKING, slotted.Bytes / 1024)
                    : R.MAPS_SLOT_NOTHING)
                : _chosen == null
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


        /// <summary>
        /// Builds an empty mission with a platform, and opens it in Minecraft.
        ///
        /// The alternative - export a real mission and delete everything - is how this was done
        /// before, and it is a bad start: you inherit Creeper Woods' objective chain, its
        /// villagers and its shape, and the first thing that happens is an exit gate that will
        /// not respond because some step in front of it asks for villagers nobody placed.
        /// </summary>
        private async void baselineButton_Click(object sender, RoutedEventArgs e)
        {
            if (_chosen == null) { return; }

            var mission = _chosen;
            var folder = workshopFor(mission);

            //Asked before anything is deleted. This throws away whatever is in the workshop for
            //that mission, which may be somebody's half-finished map.
            if (MapWorkshop.exported(mission.Name))
            {
                var sure = MessageBox.Show(
                    string.Format(R.MAPS_BASELINE_REPLACE, mission.Label),
                    R.MAPS_BASELINE, MessageBoxButton.OKCancel, MessageBoxImage.Warning);

                if (sure != MessageBoxResult.OK) { return; }
            }

            _busy = true;
            statusLabel.Text = R.MAPS_BASELINE_WORKING;
            updateUI();

            try
            {
                //make_baseline.py clears the folder itself now, so a failure here is not fatal -
                //but it is still worth saying, because a folder that will not empty is usually
                //the game or Explorer holding a file open and that will bite later too.
                if (Directory.Exists(folder))
                {
                    try { erase(folder); }
                    catch (Exception problem)
                    {
                        Console.WriteLine($"[baseline] could not clear {folder}: {problem.Message}");
                    }
                }

                Directory.CreateDirectory(folder);

                var made = await MapTools.baseline(folder, BASELINE_SIDE);
                if (!made.Ok)
                {
                    statusLabel.Text = made.Last.Length > 0 ? made.Last : R.MAPS_CONVERT_FAILED;
                    return;
                }

                MapWorkshop.remember(mission.Name, folder);
                _folder = folder;

                //Straight into Minecraft, because a platform is not something to look at - the
                //entire point of it is that something gets built on top.
                var world = await MapTools.toMinecraftLevel(folder, mission);

                statusLabel.Text = world.Ok
                    ? string.Format(R.MAPS_BASELINE_READY, BASELINE_SIDE, mission.Label)
                    : world.Last.Length > 0 ? world.Last : R.MAPS_CONVERT_FAILED;

                fillInstalled();
                fillList();
            }
            catch (Exception problem)
            {
                statusLabel.Text = problem.Message;
            }
            finally
            {
                _busy = false;
                updateUI();
            }
        }

        /// <summary>
        /// How big the starting platform is, in blocks along each side.
        ///
        /// Fixed rather than asked, because it stopped mattering: bringing a world home measures
        /// what was actually built and stretches the tile to fit, in every direction. The
        /// platform is a place to stand while you work out where things go, not a budget.
        ///
        /// Thirty is enough to lay out a start and an exit and see both at once, and it loads
        /// instantly. Build past its edge and the tile grows to meet you.
        /// </summary>
        private const int BASELINE_SIDE = 30;

        /// <summary>
        /// Whether the chosen row has a map behind it, said plainly when it does not.
        ///
        /// An empty custom slot is a row like any other - that is the point of listing all
        /// hundred - but nothing downstream can work on one, and what came out instead was the
        /// raw failure from whichever file happened to be opened first: a FileNotFoundError
        /// naming an internal path. That blames a missing file and reads like the app being
        /// broken rather than like the slot being empty.
        ///
        /// Only custom slots are ever asked about. The game's own missions are always there.
        /// </summary>
        private bool hasMap(GameMaps.Mission chosen)
        {
            if (!chosen.IsSlot || hasSomething(chosen)) { return true; }

            statusLabel.Text = string.Format(R.MAPS_SLOT_NOTHING_YET, chosen.Slot);
            return false;
        }

        /// <summary>
        /// Whether a slot has a map at all - installed in the game, or merely in its folder.
        ///
        /// Those are different things and only one of them was being checked. New empty map
        /// writes a folder and installs nothing, so a slot could hold a real map, say so in the
        /// line under the list, and still be refused by everything on the grounds of being
        /// empty. The pak is how a map gets INTO the game; the folder is where a map lives while
        /// it is being made.
        /// </summary>
        private bool hasSomething(GameMaps.Mission chosen)
            => MapSlots.inSlot(chosen.Slot) != null
                || File.Exists(Path.Combine(workshopFor(chosen), "level.json"));

        /// <summary>
        /// Renames a custom map.
        ///
        /// Only a custom one. A mission's name belongs to the game, and a map installed over
        /// Creeper Woods is still Creeper Woods as far as everything else is concerned - the save,
        /// the mission select screen, the objective banner. Offering to rename that would be
        /// offering something this app cannot deliver.
        /// </summary>
        private void renameButton_Click(object sender, RoutedEventArgs e)
        {
            if (_chosen == null) { return; }

            if (!_chosen.IsSlot)
            {
                statusLabel.Text = R.MAPS_RENAME_ONLY_CUSTOM;
                return;
            }

            var already = MapSlots.inSlot(_chosen.Slot);
            if (already == null)
            {
                statusLabel.Text = string.Format(R.MAPS_SLOT_EMPTY, _chosen.Slot);
                return;
            }

            var asked = new Windows.AskWindow(R.MAPS_RENAME, R.MAPS_RENAME_WHAT, already.Name)
            {
                Owner = Window.GetWindow(this),
            };

            if (asked.ShowDialog() != true) { return; }

            try
            {
                MapSlots.rename(_chosen.Slot, asked.Answer);
                statusLabel.Text = string.Format(R.MAPS_RENAMED, asked.Answer);
                fillList();
            }
            catch (Exception problem) { statusLabel.Text = problem.Message; }
        }

        /// <summary>A custom map, written out as one file somebody else can open.</summary>
        private void zipOutButton_Click(object sender, RoutedEventArgs e)
        {
            if (_chosen == null) { return; }

            if (!_chosen.IsSlot)
            {
                statusLabel.Text = R.MAPS_ZIP_ONLY_CUSTOM;
                return;
            }

            var already = MapSlots.inSlot(_chosen.Slot);
            if (already == null)
            {
                statusLabel.Text = string.Format(R.MAPS_SLOT_EMPTY, _chosen.Slot);
                return;
            }

            var picker = new SaveFileDialog
            {
                Title = R.MAPS_ZIP_OUT,
                Filter = "Zip archive|*.zip",
                FileName = already.Name + ".zip",
            };

            if (picker.ShowDialog() != true) { return; }

            try
            {
                MapSlots.zipTo(_chosen.Slot, picker.FileName);
                statusLabel.Text = string.Format(R.MAPS_ZIPPED,
                    System.IO.Path.GetFileName(picker.FileName));
            }
            catch (Exception problem) { statusLabel.Text = problem.Message; }
        }

        /// <summary>Somebody else's zip, into the chosen slot.</summary>
        private void zipInButton_Click(object sender, RoutedEventArgs e)
        {
            if (_chosen == null) { return; }

            if (!_chosen.IsSlot)
            {
                statusLabel.Text = R.MAPS_ZIP_ONLY_CUSTOM;
                return;
            }

            var picker = new OpenFileDialog
            {
                Title = R.MAPS_ZIP_IN,
                Filter = "Zip archive|*.zip|All files|*.*",
            };

            if (picker.ShowDialog() != true) { return; }

            //Asked before it is written, not after. A slot with a map in it is somebody's work,
            //and there is no undo for having quietly replaced it.
            if (MapSlots.inSlot(_chosen.Slot) is MapSlots.Filled standing)
            {
                var answer = MessageBox.Show(
                    string.Format(R.MAPS_ZIP_REPLACE, standing.Name, _chosen.Slot),
                    R.MAPS_ZIP_IN, MessageBoxButton.YesNo, MessageBoxImage.Question);

                if (answer != MessageBoxResult.Yes) { return; }
            }

            try
            {
                //Named after the file, because the name inside a zip is not recorded anywhere -
                //a slot is called whatever its pak is called, and the archive's own name is the
                //closest thing to what the person who made it called the map.
                var called = System.IO.Path.GetFileNameWithoutExtension(picker.FileName);

                MapSlots.zipFrom(picker.FileName, _chosen.Slot, called);

                statusLabel.Text = string.Format(R.MAPS_ZIP_OPENED, called, _chosen.Slot);
                fillList();
            }
            catch (Exception problem) { statusLabel.Text = problem.Message; }
        }

        private void importButton_Click(object sender, RoutedEventArgs e)
        {
            if (_chosen == null) { return; }

            //Opened where the working folders are. Importing one map into another slot is the
            //normal way to move a map around this app, and the folders it would be picked from
            //are all in one place - so starting the dialog anywhere else makes the common case
            //a navigation exercise.
            var picker = new OpenFolderDialog { Title = R.MAPS_IMPORT_PICK };
            if (Directory.Exists(MapWorkshop.root)) { picker.InitialDirectory = MapWorkshop.root; }
            if (picker.ShowDialog() != true) { return; }

            try
            {
                //Said plainly before anything is written. Installing a map over a mission other
                //than the one it came from is a real thing to want, but doing it by accident -
                //because the wrong row was selected - is not, and afterwards it looks like the
                //export was broken rather than like it went somewhere else.
                //Only worth asking about when installing OVER a mission. A slot has no mission
                //of its own to be the wrong one, so the question would be noise.
                if (!_chosen.IsSlot)
                {
                    //Said plainly before anything is written. Installing a map over a mission
                    //other than the one it came from is a real thing to want, but doing it by
                    //accident - because the wrong row was selected - is not, and afterwards it
                    //looks like the export was broken rather than like it went somewhere else.
                    var from = MapMod.cameFrom(picker.FolderName);
                    if (from != null
                        && !string.Equals(from, _chosen.Name, StringComparison.OrdinalIgnoreCase))
                    {
                        var answer = MessageBox.Show(
                            string.Format(R.MAPS_DIFFERENT_MISSION,
                                GameMaps.prettyName(from), _chosen.Label),
                            R.MAPS_IMPORT, MessageBoxButton.OKCancel, MessageBoxImage.Question);

                        if (answer != MessageBoxResult.OK) { return; }
                    }
                }
                else if (MapSlots.inSlot(_chosen.Slot) is MapSlots.Filled already)
                {
                    var answer = MessageBox.Show(
                        string.Format(R.MAPS_SLOT_OCCUPIED, _chosen.Slot, already.Name),
                        R.MAPS_IMPORT, MessageBoxButton.OKCancel, MessageBoxImage.Question);

                    if (answer != MessageBoxResult.OK) { return; }
                }

                var mod = _chosen.IsSlot
                    ? MapSlots.install(picker.FolderName, _chosen.Slot,
                        System.IO.Path.GetFileName(picker.FolderName))
                    : MapMod.install(picker.FolderName, _chosen);
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
        /// <summary>
        /// Puts a folder back into the game, as whatever the selected map IS.
        ///
        /// The counterpart of <see cref="exportTo"/>, and the reason the round trip works for a
        /// custom slot at all: bringing terrain back from Minecraft, or editing spawns, changes
        /// the FOLDER - and a folder is not in the game until something packs it. For a mission
        /// that means writing over the mission; for a slot it means rebuilding the slot's pak and
        /// the Camp's table with it.
        /// </summary>
        private static CustomSkins.InstalledMod installFrom(GameMaps.Mission mission, string folder)
            => mission.IsSlot
                ? MapSlots.install(folder, mission.Slot,
                    mission.ShownAs ?? System.IO.Path.GetFileName(folder))
                : MapMod.install(folder, mission);

        /// <summary>
        /// Writes a map out to a folder to work on, wherever it actually lives.
        ///
        /// One of the game's missions comes out of the game's own paks. A custom slot does not -
        /// mod paks are not in the index - so it comes back out of the pak this app wrote for it.
        /// Every tool downstream reads the folder and neither knows nor cares which it was.
        ///
        /// An empty slot throws rather than writing an empty folder, because "there is nothing
        /// here yet" is a useful thing to be told and a bare folder is not.
        /// </summary>
        private static MapMod.Exported exportTo(GameMaps.Mission mission, string folder)
        {
            if (!mission.IsSlot) { return MapMod.export(mission, folder); }

            var files = MapSlots.export(mission.Slot, folder);

            var bytes = 0L;
            foreach (var file in Directory.EnumerateFiles(folder, "*", SearchOption.AllDirectories))
            {
                bytes += new FileInfo(file).Length;
            }

            return new MapMod.Exported(folder, files, bytes, new List<string>());
        }

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
            if (!hasMap(_chosen)) { return; }

            var mission = _chosen;
            var folder = workshopFor(mission);

            //This route exports, and exporting downloads the game's own mission over whatever is
            //in the folder. That is right when the folder holds a stale copy and ruinous when it
            //holds a map somebody brought back from Minecraft - which is how a finished map got
            //replaced by stock Creeper Woods by somebody pressing this to look at their world
            //again. Export map was removed for exactly this; the same door was still open here.
            if (MapWorkshop.exported(mission.Name) && MapWorkshop.looksBuilt(folder))
            {
                var answer = MessageBox.Show(
                    string.Format(R.MAPS_EXPORT_OVER_BUILT,
                        mission.Label, MapWorkshop.weightOf(folder) / 1024, folder),
                    R.MAPS_FIXED_TO_MINECRAFT, MessageBoxButton.OKCancel, MessageBoxImage.Warning);

                if (answer != MessageBoxResult.OK) { return; }
            }

            _busy = true;
            statusLabel.Text = string.Format(R.MAPS_WORKING, mission.Name);
            updateUI();

            try
            {
                var made = await Task.Run(() => exportTo(mission, folder));
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

            //Not "the slot is empty" but WHY that stops this one. Bringing a world back writes
            //its blocks into a map that already exists - the converter opens the level file
            //before it writes anything - so an empty slot has nothing to write into. Importing a
            //folder or a zip does not, because those carry a whole map with them.
            if (_chosen.IsSlot && !hasSomething(_chosen))
            {
                statusLabel.Text = string.Format(R.MAPS_SLOT_NEEDS_BASE, _chosen.Slot);
                return;
            }

            var picker = new OpenFolderDialog { Title = R.MAPS_PICK_WORLD };
            if (MapTools.saves != null) { picker.InitialDirectory = MapTools.saves; }
            if (picker.ShowDialog() != true) { return; }

            //A whole-level world holds rooms from several object groups and carries its own note
            //saying which file each belongs to, so it comes back a different way.
            //Recorded before any decision is taken on it.
            //
            //Every import so far has ended with an empty map in the game and every line of this
            //reporting success, and each time the only way to find out what happened was to read
            //the folders afterwards and guess backwards. The decisions are cheap to write down
            //and the guessing is not.
            Services.Journal.note($"bring back: world \"{picker.FolderName}\", whole level "
                + $"{MapTools.isWholeLevel(picker.FolderName)}, came from "
                + $"\"{MapTools.mapOf(picker.FolderName) ?? "(no note)"}\", into "
                + $"\"{workshopFor(_chosen)}\"");

            if (MapTools.isWholeLevel(picker.FolderName))
            {
                //Which map this world came from, checked BEFORE anything is converted.
                //
                //The converter writes a world back to the map recorded inside it, not to the row
                //selected here. Those were never compared, so picking a world exported from one
                //map while another is selected wrote the blocks somewhere else entirely and then
                //installed the selected map's own untouched contents - announcing success, with
                //an empty map arriving in the game.
                var was = MapTools.mapOf(picker.FolderName);
                var here = workshopFor(_chosen);

                var elsewhere = was != null && !string.Equals(
                    Path.GetFullPath(was).TrimEnd(Path.DirectorySeparatorChar),
                    Path.GetFullPath(here).TrimEnd(Path.DirectorySeparatorChar),
                    StringComparison.OrdinalIgnoreCase);

                if (!elsewhere)
                {
                    await bringBackLevel(picker.FolderName, _chosen);
                    return;
                }

                //Starting a custom map from one of the game's, which is the ordinary way to make
                //one - a mission is the only interesting thing to begin with. The world knows it
                //came from that mission and the converter obeys the world, so this brings the
                //MAP across first and then points the world at the copy.
                //
                //Copying is not optional. The rooms alone are not a map: the level file says
                //which stretches exist and which tiles they draw, the object groups hold the
                //rooms, and the pack says what the blocks look like. Writing rooms into a folder
                //that has none of that is what produced an empty 30x30 and a cheerful success
                //message.
                //If the slot ALREADY holds a map, nothing is copied into it.
                //
                //A world remembers the folder it was exported from, and that folder is often a
                //blank level - New empty map writes one, and exporting it leaves a note naming
                //whichever mission was selected at the time. So "came from creeperwoods" does
                //not mean the world contains Creeper Woods; it can mean somebody made an empty
                //map while Creeper Woods happened to be highlighted.
                //
                //Copying the source across in that case is destructive twice over: it replaces
                //the slot's own map, and it hands the converter a real mission to write an
                //island into - which is how a custom island came back as Creeper Woods. The
                //converter only needs the object groups its note names, and a slot with a map in
                //it already has them.
                if (File.Exists(Path.Combine(here, "level.json")))
                {
                    Services.Journal.note($"bring back: \"{here}\" already holds a map, so "
                        + "nothing was copied into it");

                    string? kept = null;

                    try
                    {
                        kept = MapTools.retarget(picker.FolderName, here);
                        if (kept == null)
                        {
                            statusLabel.Text = R.MAPS_WORLD_NOT_AIMED;
                            return;
                        }

                        await bringBackLevel(picker.FolderName, _chosen);
                    }
                    finally
                    {
                        MapTools.restore(picker.FolderName, kept);
                    }

                    return;
                }

                if (!Directory.Exists(was!) || !File.Exists(Path.Combine(was!, "level.json")))
                {
                    //Rebuilt from the game rather than demanded from the person.
                    //
                    //The folder a world points at can be gone - cleared, tidied, never there on
                    //this machine - and telling somebody to export the mission to Minecraft
                    //again to repair it is asking them to perform a round trip whose only
                    //purpose is a side effect. The app has the game's own files; the folder is
                    //something it can simply make.
                    var name = Path.GetFileName(was!.TrimEnd(Path.DirectorySeparatorChar));

                    var source = _missions.FirstOrDefault(one => !one.IsSlot
                        && string.Equals(one.Name, name, StringComparison.OrdinalIgnoreCase));

                    if (source == null)
                    {
                        //Not one of the game's, so there is nothing to rebuild it from.
                        statusLabel.Text = string.Format(R.MAPS_WORLD_SOURCE_GONE, name);
                        return;
                    }

                    statusLabel.Text = string.Format(R.MAPS_WORLD_REBUILDING, name);
                    _busy = true;
                    updateUI();

                    try
                    {
                        await Task.Run(() => MapMod.export(source, was!));
                        Services.Journal.note($"bring back: rebuilt \"{was}\" from the game");
                    }
                    catch (Exception problem)
                    {
                        statusLabel.Text = problem.Message;
                        _busy = false;
                        updateUI();
                        return;
                    }

                    _busy = false;
                    updateUI();

                    if (!File.Exists(Path.Combine(was!, "level.json")))
                    {
                        statusLabel.Text = string.Format(R.MAPS_WORLD_SOURCE_GONE, name);
                        return;
                    }
                }

                if (MapSlots.inSlot(_chosen.Slot) != null || Directory.Exists(here))
                {
                    var answer = MessageBox.Show(
                        string.Format(R.MAPS_WORLD_OVERWRITE,
                            Path.GetFileName(was!.TrimEnd(Path.DirectorySeparatorChar)),
                            _chosen.Label),
                        R.MAPS_FROM_MINECRAFT, MessageBoxButton.YesNo, MessageBoxImage.Question);

                    if (answer != MessageBoxResult.Yes) { return; }
                }

                string? note = null;

                try
                {
                    copyInto(was!, here);
                    Services.Journal.note($"bring back: copied \"{was}\" into \"{here}\"");

                    note = MapTools.retarget(picker.FolderName, here);
                    if (note == null)
                    {
                        statusLabel.Text = R.MAPS_WORLD_NOT_AIMED;
                        return;
                    }

                    await bringBackLevel(picker.FolderName, _chosen);
                }
                finally
                {
                    //Always, even when the conversion failed. A world left pointing at a slot
                    //would send the NEXT bring-back there too, and nothing would say why.
                    MapTools.restore(picker.FolderName, note);
                }

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
                    var mod = await Task.Run(() => installFrom(mission, origin.Map));
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

                //What the map looked like before, so "the converter changed nothing" can be told
                //from "the converter worked". An exit code cannot tell them apart, and that is
                //the whole failure: a clean exit, an untouched folder, and an empty map installed
                //over the top announcing success.
                var before = stamp(workshopFor(mission));

                var run = await MapTools.fromMinecraftLevel(world);

                //The converter's own words, kept whether it succeeded or not. They were being
                //thrown away on success, which is where the useful sentence lived.
                Services.Journal.note($"bring back: converter ok={run.Ok}, said: "
                    + (run.Last.Length == 0 ? "(nothing)" : run.Last.Trim()));

                if (run.Ok && stamp(workshopFor(mission)) == before)
                {
                    //Nothing was written. Installing now would pack whatever the folder already
                    //held - for a fresh slot, an empty baseline - and hand somebody a map that
                    //crashes the game, having told them it worked.
                    statusLabel.Text = run.Last.Trim().Length > 0
                        ? run.Last.Trim()
                        : R.MAPS_CONVERT_NOTHING;

                    Services.Journal.note("bring back: the folder is unchanged, so nothing was "
                        + "installed");

                    _busy = false;
                    updateUI();
                    return;
                }

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

                        var straight = await Task.Run(() => installFrom(mission, folder));
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

                    //Welding merges every room into one tile, and that tile inherits every
                    //side-path door the originals declared. The generator allows one travel
                    //entry per tile and refuses the level otherwise - and the doors point at
                    //crypts and inns that welding has just removed, so they are dead anyway.
                    //Markers back onto the ground before anything else looks at the level.
                    //
                    //A tile that has grown keeps its player start, exit and doors at the heights
                    //the old, smaller tile had - which in a bigger map is underground or in the
                    //air, and the game does not survive a spawn inside rock.
                    try
                    {
                        //Loaded ONCE. settle mutates the map it is given, so loading a second
                        //time to save would write a copy that was never changed - the edit would
                        //vanish silently and the spawn would still be underground.
                        var plan = MapSpawns.load(folder);
                        var settled = MapSpawns.settle(plan);

                        if (settled > 0)
                        {
                            MapSpawns.save(plan);
                            Services.Journal.note($"bring back: settled {settled} marker(s) onto "
                                + "the ground");

                            //Each one said out loud. Moving a player start sideways is a guess,
                            //and somebody who ends up spawning somewhere unexpected should be
                            //able to find out that it happened rather than wonder.
                            foreach (var said in plan.Notes) { Services.Journal.note("  " + said); }
                        }
                    }
                    catch (Exception problem)
                    {
                        Services.Journal.note($"bring back: could not settle the markers: "
                            + problem.Message);
                    }

                    var stripped = MapMod.dropSidePaths(folder);
                    if (stripped > 0)
                    {
                        Services.Journal.note($"bring back: dropped the side-paths from "
                            + $"{stripped} welded tile(s)");
                    }

                    if (!weld.Ok)
                    {
                        statusLabel.Text = weld.Last.Length > 0 ? weld.Last : R.MAPS_CONVERT_FAILED;
                        _busy = false;
                        updateUI();
                        return;
                    }

                    var mod = await Task.Run(() => installFrom(mission, folder));
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
            if (_chosen != null && !hasMap(_chosen)) { return; }

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
                    var made = await Task.Run(() => exportTo(mission, folder));
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
            //Skipped outright for a map somebody built. It is already one stretch playing one
            //tile - there is no shuffle to pin and nothing to join - so pinning and welding it
            //can only do harm, and has: run over a folder holding another mission's leftovers it
            //rewrote the level to play the leftover instead.
            //
            //The old gate was "has this been welded before", read off a level.json.multitile
            //sitting beside it. That is a proxy for the question rather than the question, and it
            //gives the wrong answer whenever the file is missing for some other reason - which is
            //every map that came home from Minecraft without ever having been welded.
            var alreadyOne = MapWorkshop.looksBuilt(folder);

            if (!alreadyOne
                && !System.IO.File.Exists(System.IO.Path.Combine(folder, "level.json.multitile")))
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

        private async void unweldedButton_Click(object sender, RoutedEventArgs e)
            => await openUnwelded().ConfigureAwait(true);

        /// <summary>
        /// The mission as the game's own files hold it, before anything is fused.
        ///
        /// Edit spawns welds, and it has to: a welded mission is one place, which is how anybody
        /// thinks about a level and what actually gets installed. But welding is lossy in a way
        /// that is invisible until you go looking for what it took. Every tile becomes one tile,
        /// and a teleport that pointed from one to another has nowhere left to point: Creeper
        /// Woods drops from 59 links to 3, and from 48 descents into its crypts to 14. Creepy
        /// Crypt keeps all 26 of its sub-areas declared and not one door that reaches them.
        ///
        /// So this exports a SECOND copy and never welds it. It is for reading - the Wiring
        /// window on this copy is the only place the real shape of a shipped mission shows up -
        /// and it is deliberately not the folder Edit spawns uses, is never remembered against
        /// the mission, and is never installed. Saving in it changes the inspection copy and
        /// nothing else.
        /// </summary>
        internal async Task openUnwelded()
        {
            if (_chosen == null) { return; }

            if (_chosen.IsSlot)
            {
                statusLabel.Text = R.MAPS_UNWELDED_ONLY_GAME;
                return;
            }

            var mission = _chosen;
            var folder = workshopFor(mission) + ".unwelded";

            //Exported once and kept. Re-exporting on every press would be slower and no more
            //truthful - the paks do not change under the app - and it would throw away a note
            //somebody left in the copy while reading it.
            var level = Directory.Exists(folder)
                && Directory.GetFiles(folder, "level.json", SearchOption.AllDirectories).Length > 0;

            if (!level)
            {
                if (Directory.Exists(folder))
                {
                    try { erase(folder); } catch (Exception) { }
                }

                _busy = true;
                statusLabel.Text = string.Format(R.MAPS_UNWELDED_MAKING, mission.Label);
                updateUI();

                try
                {
                    await Task.Run(() => exportTo(mission, folder));
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

                /* Pinned, and NOT welded. The two are separate steps and only the second one is
                   lossy.

                   Pinning replaces a stretch that still picks from a tile-group with the one
                   tile it picked, which is what makes it a room at all - a Room is a stretch
                   naming exactly one tile. Straight out of the paks Creeper Woods pins six of
                   its nineteen stretches and the other thirteen are simply not there: no tiles,
                   no regions, and the gates the objectives name nowhere to be found.

                   Welding is the step after, and it is the one that fuses every pinned tile into
                   one and takes the teleports between them with it. Skipping it is the entire
                   point of this copy. */
                if (!MapTools.available)
                {
                    statusLabel.Text = R.MAPS_UNWELDED_NO_PIN;
                }
                else
                {
                    _busy = true;
                    statusLabel.Text = string.Format(R.MAPS_UNWELDED_PINNING, mission.Label);
                    updateUI();

                    try
                    {
                        var pin = await MapTools.makeFixed(folder);
                        if (!pin.Ok)
                        {
                            statusLabel.Text = pin.Last.Length > 0
                                ? pin.Last : R.MAPS_CONVERT_FAILED;
                        }
                    }
                    catch (Exception problem)
                    {
                        statusLabel.Text = problem.Message;
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
                var map = MapSpawns.load(folder);

                var window = new SpawnsWindow(map, mission)
                {
                    Owner = Window.GetWindow(this),
                };

                //No Installed hook. This copy is not installable and the tab has nothing to
                //re-read when it changes.
                window.inspectOnly();
                window.Show();

                statusLabel.Text = string.Format(R.MAPS_UNWELDED_PINNED,
                    mission.Label, map.Rooms.Count, folder);
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

            //The row has to be rebuilt, not just the buttons. A slot's size and its "(empty)" /
            //"(not installed)" wording are read off the folder that was just deleted, so leaving
            //the list alone shows a map that is no longer there - which reads as Clear having
            //quietly failed. Every other destructive handler here already does this; this one
            //was the exception.
            fillInstalled();
            fillList();
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

        /// <summary>
        /// Everything in one map folder, into another.
        ///
        /// Overwrites rather than merges. A map is a level file plus the object groups it names,
        /// and a folder holding half of one map and half of another is not a map - it is a level
        /// naming tiles that are not there, which fails at load with nothing to point at.
        /// </summary>
        /// <summary>
        /// A cheap fingerprint of a map folder: how many files, how big, how recently written.
        ///
        /// Enough to answer "did anything actually happen", which an exit code cannot.
        /// </summary>
        private static string stamp(string folder)
        {
            try
            {
                if (!Directory.Exists(folder)) { return "none"; }

                long bytes = 0;
                long newest = 0;
                var count = 0;

                foreach (var file in Directory.GetFiles(folder, "*", SearchOption.AllDirectories))
                {
                    var about = new FileInfo(file);
                    bytes += about.Length;
                    newest = Math.Max(newest, about.LastWriteTimeUtc.Ticks);
                    count++;
                }

                return $"{count}/{bytes}/{newest}";
            }
            catch (Exception)
            {
                //Unreadable is its own fingerprint, and it will not match a readable one.
                return Guid.NewGuid().ToString("N");
            }
        }

        private static void copyInto(string from, string to)
        {
            Directory.CreateDirectory(to);

            foreach (var file in Directory.GetFiles(from, "*", SearchOption.AllDirectories))
            {
                var landing = Path.Combine(to, file.Substring(from.Length).TrimStart(
                    Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));

                Directory.CreateDirectory(Path.GetDirectoryName(landing)!);
                File.Copy(file, landing, true);
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
                if (_chosen.IsSlot)
                {
                    //Nothing to put back - a slot replaced nothing. It empties, the Camp stops
                    //listing it, and it can be filled again from a folder or a zip.
                    MapSlots.clear(_chosen.Slot);
                    statusLabel.Text = string.Format(R.MAPS_SLOT_CLEARED, _chosen.Slot);
                }
                else
                {
                    statusLabel.Text = MapMod.remove(_chosen)
                        ? string.Format(R.MAPS_PUT_BACK, _chosen.Label)
                        : string.Format(R.MAPS_NOT_REPLACED, _chosen.Label);
                }

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
