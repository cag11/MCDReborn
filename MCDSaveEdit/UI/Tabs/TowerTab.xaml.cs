using MCDSaveEdit.Data;
using MCDSaveEdit.Logic;
using MCDSaveEdit.Save.Models.Enums;
using MCDSaveEdit.Save.Models.Profiles;
using MCDSaveEdit.Services;
using MCDSaveEdit.ViewModels;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
#nullable enable

namespace MCDSaveEdit.UI
{
    /// <summary>
    /// The Tower, which every save has been carrying and none of them could show.
    ///
    /// A run is not the inventory. It is a thing in progress: a floor you are standing on, lives
    /// you have spent, thirty one floors ahead of you, and gear picked up inside the tower that
    /// exists nowhere else in the save. So the run comes first, then the floor you pick, then the
    /// gear.
    ///
    /// Floors are chosen one at a time rather than laid out all at once. There are thirty one of
    /// them and each has a kind and five reward slots; putting all of that on screen together is
    /// a wall of controls where only one row ever matters.
    ///
    /// Only a live run can be edited, because a finished one has no towerInfo left - the game
    /// throws it away and keeps the fact that the run happened. Finished runs are still listed
    /// and say so, since a list that quietly omits them reads like a bug.
    ///
    /// The way this is meant to be used, which the save format forces: leave the tower, close the
    /// game, edit here, then go back in. The game holds the run in memory while it is playing and
    /// writes it out at its own moments, so an edit made underneath a running game is overwritten.
    /// </summary>
    public partial class TowerTab : UserControl
    {
        public static void preload() { }

        private ProfileViewModel? _model;
        public ProfileViewModel? model {
            get { return _model; }
            set { _model = value; setupCommands(); updateUI(); }
        }

        /// <summary>Writes the save, through whatever the window uses for File > Save.</summary>
        public Action? requestSave { get; set; }

        private IReadOnlyList<TowerRuns.Run> _runs = Array.Empty<TowerRuns.Run>();
        private TowerRuns.Run? _run;
        private TowerRuns.Floor? _floor;
        private TowerRuns.Player? _player;
        private List<Item> _items = new List<Item>();
        private Item? _selected;

        //Filling a control from the save raises its changed event, which would read the value
        //straight back and write it again. Harmless once, a loop the moment one is clamped.
        private bool _filling;

        public TowerTab()
        {
            InitializeComponent();
            translateStaticStrings();
            updateUI();
        }

        private void translateStaticStrings()
        {
            floorsLabel.Content = R.TOWER_FLOORS;
            itemsLabel.Content = R.TOWER_ITEMS;
            floorCaption.Text = R.TOWER_FLOOR;
            bossesCaption.Text = R.TOWER_BOSSES;
            livesCaption.Text = R.TOWER_LIVES;
            arrowsCaption.Text = R.TOWER_ARROWS;
            pointsCaption.Text = R.TOWER_POINTS;
            typeCaption.Text = R.TOWER_TYPE;
            rewardsCaption.Text = R.TOWER_REWARDS;
        }

        private void setupCommands()
        {
            if (_model == null) { return; }

            _model.profile.subscribe(_ => updateUI());

            selectedItemScreen.itemLocation = ItemLocationEnum.Tower;
            selectedItemScreen.saveChanges = new RelayCommand<Item>(saveItem);
            selectedItemScreen.deleteItem = new RelayCommand<Item>(removeItem);
            selectedItemScreen.duplicateItem = new RelayCommand<Item>(duplicateItem);
            selectedItemScreen.moveItemToInventory = new RelayCommand<Item>(item => moveItem(item, toChest: false));
            selectedItemScreen.moveItemToChest = new RelayCommand<Item>(item => moveItem(item, toChest: true));

            //Enchantments are edited here rather than through an equipment view model. Those
            //write to whichever item their own screen has selected - the inventory's or the
            //chest's - and that is never the tower item being looked at, so clicking an
            //enchantment on a tower item did nothing at all.
            selectedItemScreen.selectEnchantment = new RelayCommand<Enchantment>(selectEnchantment);
            selectedItemScreen.addEnchantmentSlot = new RelayCommand<object>(addEnchantmentSlot);
            selectedEnchantmentScreen.close = new RelayCommand<Enchantment>(_ => selectEnchantment(null));
            selectedEnchantmentScreen.saveChanges = new RelayCommand<Enchantment>(saveEnchantment);
        }

        #region Enchantments on a tower item

        private Enchantment? _enchantment;

        private void selectEnchantment(Enchantment? enchantment)
        {
            _enchantment = enchantment;

            if (enchantment == null || _selected == null)
            {
                _enchantment = null;
                selectedEnchantmentScreen.Visibility = Visibility.Collapsed;
                enchantmentShade.Visibility = Visibility.Collapsed;
                return;
            }

            selectedEnchantmentScreen.Visibility = Visibility.Visible;
            enchantmentShade.Visibility = Visibility.Visible;
            selectedEnchantmentScreen.enchantment = enchantment;
            selectedEnchantmentScreen.item = _selected;
            selectedEnchantmentScreen.isGilded = _selected.NetheriteEnchant != null;
            selectedItemScreen.updateEnchantmentsUI();
        }

        /// <summary>
        /// Puts the chosen enchantment back in the slot it came from.
        ///
        /// By position, found from the one that was open: the editor hands back a new object
        /// rather than the one it was given, so it has to replace the old one where it sat. An
        /// item can carry the same enchantment twice, which is why the object is looked up rather
        /// than the id.
        /// </summary>
        private void saveEnchantment(Enchantment enchantment)
        {
            if (enchantment == null || _selected == null || _enchantment == null) { return; }

            var enchantments = _selected.Enchantments?.ToList() ?? new List<Enchantment>();
            var index = enchantments.IndexOf(_enchantment);
            if (index < 0) { return; }

            enchantments[index] = enchantment;
            _selected.Enchantments = enchantments.ToArray();
            _enchantment = enchantment;

            commit();
            selectedEnchantmentScreen.enchantment = enchantment;
            selectedItemScreen.updateEnchantmentsUI();
        }

        /// <summary>
        /// Adds another row of enchantment options, three at a time.
        ///
        /// Three because that is what a row is in game - you are offered a choice of three and
        /// take one - and the same count the inventory adds.
        /// </summary>
        private void addEnchantmentSlot(object sender)
        {
            if (_selected == null) { return; }

            var enchantments = _selected.Enchantments?.ToList() ?? new List<Enchantment>();
            if (enchantments.Count >= Constants.MAXIMUM_ENCHANTMENT_OPTIONS_PER_ITEM) { return; }

            for (int i = 0; i < 3; i++)
            {
                enchantments.Add(new Enchantment { Id = Constants.DEFAULT_ENCHANTMENT_ID, Level = 0 });
            }
            _selected.Enchantments = enchantments.Take(Constants.MAXIMUM_ENCHANTMENT_OPTIONS_PER_ITEM).ToArray();

            commit();
            selectedItemScreen.updateEnchantmentsUI();
        }

        #endregion

        public void updateUI()
        {
            _runs = TowerRuns.runsIn(_model?.profile.value);
            fillRunCombo();
            updateSelection();
        }

        #region Which run

        private void fillRunCombo()
        {
            _filling = true;
            runCombo.Items.Clear();
            foreach (var run in _runs)
            {
                runCombo.Items.Add(new ComboBoxItem { Content = describe(run), Tag = run });
            }
            _filling = false;

            //The newest one is what anybody opening this tab came for. Not merely the first that
            //looks live: an abandoned run keeps its detail and goes on looking live forever, so
            //the first can be one from months ago while the real one sits below it.
            var live = _runs.LastOrDefault(run => run.InProgress) ?? _runs.LastOrDefault();
            _run = live;
            var entry = runCombo.Items.OfType<ComboBoxItem>().FirstOrDefault(x => ReferenceEquals(x.Tag, live));
            if (entry != null) { runCombo.SelectedItem = entry; }
        }

        private static string describe(TowerRuns.Run run)
        {
            var state = run.InProgress ? R.TOWER_IN_PROGRESS
                : run.CompletedOnce ? R.TOWER_COMPLETED
                : R.TOWER_ABANDONED;
            var where = run.InProgress && !string.IsNullOrEmpty(run.Difficulty)
                ? "  ·  " + run.Difficulty.Replace("_", " ")
                : string.Empty;
            return R.formatTOWER_RUN_NUMBER(run.Index + 1) + "  ·  " + state + where;
        }

        private void runCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_filling) { return; }
            _run = (runCombo.SelectedItem as ComboBoxItem)?.Tag as TowerRuns.Run;
            _floor = null;
            _player = null;
            _selected = null;
            updateSelection();
        }

        private void updateSelection()
        {
            var live = _run != null && _run.InProgress;

            floorsPanel.Visibility = live ? Visibility.Visible : Visibility.Collapsed;
            floorPanel.Visibility = live ? Visibility.Visible : Visibility.Collapsed;
            playerPanel.Visibility = live ? Visibility.Visible : Visibility.Collapsed;
            itemsLabel.Visibility = live ? Visibility.Visible : Visibility.Collapsed;
            floorCombo.IsEnabled = live;
            livesBox.IsEnabled = live;

            emptyLabel.Text = _runs.Count == 0 ? R.TOWER_NONE
                : _run == null ? R.TOWER_PICK_ONE
                : live ? string.Empty
                : R.TOWER_OVER;

            _filling = true;
            fillFloorCombo(live);
            floorTotalLabel.Text = live ? R.formatTOWER_OF_FLOORS(_run!.FloorCount) : string.Empty;
            bossesLabel.Text = live ? _run!.BossesKilled.ToString() : string.Empty;
            livesBox.Text = live ? _run!.LivesLost.ToString() : string.Empty;
            seedLabel.Text = live ? R.formatTOWER_SEED(_run!.Seed) : string.Empty;
            _filling = false;

            if (!live)
            {
                floorList.Items.Clear();
                itemsPanel.Children.Clear();
                selectedItemScreen.item = null;
                return;
            }

            fillFloorList();
            fillPlayers();
        }

        #endregion

        #region The floors

        /// <summary>One row per floor, chipped by kind and marked where the run has reached.</summary>
        private void fillFloorList()
        {
            floorList.Items.Clear();
            if (_run == null) { return; }

            var current = _run.CurrentFloor;
            foreach (var floor in _run.Floors)
            {
                var row = new StackPanel { Orientation = Orientation.Horizontal };
                row.Children.Add(new Border {
                    Width = 10,
                    Height = 10,
                    CornerRadius = new CornerRadius(2),
                    Background = floorBrush(floor.Type),
                    VerticalAlignment = VerticalAlignment.Center,
                    Margin = new Thickness(0, 0, 8, 0),
                });
                row.Children.Add(new TextBlock {
                    Text = floor.Index.ToString("00"),
                    FontSize = 12,
                    Width = 24,
                    Foreground = (Brush)FindResource("Brush.TextMuted"),
                    VerticalAlignment = VerticalAlignment.Center,
                });
                row.Children.Add(new TextBlock {
                    Text = floor.Type,
                    FontSize = 12,
                    VerticalAlignment = VerticalAlignment.Center,
                });
                if (floor.Index == current)
                {
                    row.Children.Add(new TextBlock {
                        Text = R.TOWER_YOU_ARE_HERE,
                        FontSize = 11,
                        Margin = new Thickness(8, 0, 0, 0),
                        Foreground = (Brush)FindResource("Brush.Accent"),
                        VerticalAlignment = VerticalAlignment.Center,
                    });
                }

                var listItem = new ListBoxItem {
                    Content = row,
                    Tag = floor,
                    Padding = new Thickness(8, 4, 8, 4),
                    ToolTip = R.TOWER_GO_HINT,
                };
                //Single click picks a floor to look at, double click moves the run to it. Two
                //different questions about the same row, and the double click is the one that
                //changes the save.
                listItem.MouseDoubleClick += floorEntry_DoubleClick;
                floorList.Items.Add(listItem);
            }

            var wanted = _floor?.Index ?? current;
            var entry = floorList.Items.OfType<ListBoxItem>()
                .FirstOrDefault(x => (x.Tag as TowerRuns.Floor)?.Index == wanted);
            floorList.SelectedItem = entry ?? floorList.Items.OfType<ListBoxItem>().FirstOrDefault();
        }

        private Brush floorBrush(string type)
        {
            //Boss floors are the landmarks, merchants the breathers, the rest is fighting.
            var key = type switch {
                "Boss" => "Brush.Danger",
                "Merchant" => "Brush.Positive",
                "Combat" => "Brush.Accent",
                _ => "Brush.BorderStrong",
            };
            return (Brush)FindResource(key);
        }

        private void floorEntry_DoubleClick(object sender, System.Windows.Input.MouseButtonEventArgs e)
        {
            if (!((sender as ListBoxItem)?.Tag is TowerRuns.Floor floor)) { return; }
            goToFloor(floor.Index);
        }

        private void floorList_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            _floor = (floorList.SelectedItem as ListBoxItem)?.Tag as TowerRuns.Floor;
            updateFloor();
        }

        private void updateFloor()
        {
            floorPanel.Visibility = _floor == null ? Visibility.Collapsed : Visibility.Visible;
            if (_floor == null) { return; }

            _filling = true;

            floorTitleLabel.Text = R.formatTOWER_FLOOR_NUMBER(_floor.Index);

            typeLabel.Text = _floor.Type;

            //Five slots, each deciding what kind of gear that slot offers when the floor is
            //cleared. Built here rather than in the markup because a floor with no reward array
            //has no slots to show.
            rewardRow.Children.Clear();
            for (int slot = 0; slot < _floor.Rewards.Count; slot++)
            {
                var combo = new ComboBox {
                    Width = 96,
                    FontSize = 12,
                    Margin = new Thickness(0, 0, 6, 0),
                    VerticalContentAlignment = VerticalAlignment.Center,
                    Tag = slot,
                };
                foreach (var kind in TowerRuns.REWARD_KINDS)
                {
                    combo.Items.Add(new ComboBoxItem { Content = kind, Tag = kind });
                }
                combo.SelectedItem = combo.Items.OfType<ComboBoxItem>()
                    .FirstOrDefault(x => (string?)x.Tag == _floor.Rewards[slot]);
                combo.SelectionChanged += reward_SelectionChanged;
                rewardRow.Children.Add(combo);
            }

            fillBuild();

            _filling = false;
        }

        /// <summary>
        /// What the floor is built from, as a statement rather than a choice.
        ///
        /// The level and the encounter were both editable for a while and both crash the game on
        /// load, as does the kind. They are shown because they explain what the floor is - a boss
        /// tile tells you more than the word Boss does - and left alone because the tower's shape
        /// is settled when the run is generated.
        /// </summary>
        private void fillBuild()
        {
            if (_floor == null) { buildLabel.Text = string.Empty; return; }

            var lines = new List<string> { R.formatTOWER_TILE(_floor.Tile) };
            lines.Add(_floor.Challenges.Count > 0
                ? R.formatTOWER_CHALLENGE(string.Join(", ", _floor.Challenges))
                : R.TOWER_NO_ENCOUNTER);
            buildLabel.Text = string.Join(Environment.NewLine, lines);
        }

        /// <summary>
        /// One of the five reward slots, which decide what kind of gear the floor offers when it
        /// is cleared. Plain data rather than a reference to a level, which is why these are
        /// still a choice when the rest of the floor is not.
        /// </summary>
        private void reward_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_filling || _floor == null) { return; }
            if (!(sender is ComboBox combo) || !(combo.Tag is int slot)) { return; }

            var kind = (combo.SelectedItem as ComboBoxItem)?.Tag as string;
            if (kind == null || slot >= _floor.Rewards.Count || kind == _floor.Rewards[slot]) { return; }

            _floor.setReward(slot, kind);
            requestSave?.Invoke();
        }

        /// <summary>Every floor of the run, named the way the list on the left names them.</summary>
        private void fillFloorCombo(bool live)
        {
            floorCombo.Items.Clear();
            if (!live || _run == null) { return; }

            foreach (var floor in _run.Floors)
            {
                floorCombo.Items.Add(new ComboBoxItem {
                    Content = floor.Index.ToString("00") + "  " + floor.Type,
                    Tag = floor.Index,
                });
            }
            floorCombo.SelectedItem = floorCombo.Items.OfType<ComboBoxItem>()
                .FirstOrDefault(x => (int?)x.Tag == _run.CurrentFloor);
        }

        private void floorCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_filling || _run == null) { return; }

            var index = (floorCombo.SelectedItem as ComboBoxItem)?.Tag as int?;
            if (index == null || index == _run.CurrentFloor) { return; }

            goToFloor(index.Value);
        }

        /// <summary>
        /// Moves the run to a floor.
        ///
        /// One route for both ways of asking - the list at the top and a double click on the left
        /// - so the two cannot drift apart, and so the marker, the dropdown and the save all move
        /// together whichever was used.
        /// </summary>
        private void goToFloor(int index)
        {
            if (_run == null) { return; }

            _run.CurrentFloor = index;
            requestSave?.Invoke();

            _filling = true;
            fillFloorCombo(true);
            _filling = false;
            fillFloorList();
        }

        private void livesBox_TextChanged(object sender, TextChangedEventArgs e)
            => applyNumber(livesBox, value => _run!.LivesLost = value);

        private void arrowsBox_TextChanged(object sender, TextChangedEventArgs e)
            => applyNumber(arrowsBox, value => _player!.Arrows = value, needsPlayer: true);

        private void pointsBox_TextChanged(object sender, TextChangedEventArgs e)
            => applyNumber(pointsBox, value => _player!.EnchantmentPoints = value, needsPlayer: true);

        /// <summary>
        /// One route for every number here: refuse what will not parse by colouring the box
        /// rather than by rejecting the keystroke, write what will, and save it.
        /// </summary>
        private void applyNumber(TextBox box, Action<int> apply, bool needsPlayer = false)
        {
            if (_filling || _run == null || (needsPlayer && _player == null)) { return; }

            if (!int.TryParse(box.Text, out int value) || value < 0)
            {
                box.BorderBrush = (Brush)FindResource("Brush.Danger");
                return;
            }

            box.ClearValue(BorderBrushProperty);
            apply(value);
            requestSave?.Invoke();
        }

        #endregion

        #region Who is in it

        private void fillPlayers()
        {
            var players = _run?.Players ?? Array.Empty<TowerRuns.Player>();

            _filling = true;
            playerCombo.Items.Clear();
            foreach (var player in players)
            {
                playerCombo.Items.Add(new ComboBoxItem {
                    Content = R.formatTOWER_PLAYER(player.PlayerId.ToString()),
                    Tag = player,
                });
            }
            //Hidden rather than absent on a solo run: a picker with one entry asks a question
            //with one answer.
            playerCombo.Visibility = players.Count > 1 ? Visibility.Visible : Visibility.Collapsed;
            if (playerCombo.Items.Count > 0) { playerCombo.SelectedIndex = 0; }
            _filling = false;

            _player = players.FirstOrDefault();
            updatePlayer();
        }

        private void playerCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_filling) { return; }
            _player = (playerCombo.SelectedItem as ComboBoxItem)?.Tag as TowerRuns.Player;
            _selected = null;
            updatePlayer();
        }

        private void updatePlayer()
        {
            _filling = true;
            arrowsBox.Text = _player?.Arrows.ToString() ?? string.Empty;
            pointsBox.Text = _player?.EnchantmentPoints.ToString() ?? string.Empty;
            _filling = false;

            _items = _player?.items().ToList() ?? new List<Item>();
            fillItems();
        }

        #endregion

        #region The gear carried through it

        private const double TILE = 90;

        private void fillItems()
        {
            itemsPanel.Children.Clear();
            foreach (var item in _items)
            {
                var control = new ItemControl { item = item, Width = TILE, Height = TILE };
                itemsPanel.Children.Add(new Button {
                    Content = control,
                    Width = TILE,
                    Height = TILE,
                    Margin = new Thickness(0, 0, 4, 4),
                    Background = null,
                    Command = new RelayCommand<Item>(selectItem),
                    CommandParameter = item,
                });
            }

            if (_selected != null && !_items.Contains(_selected)) { _selected = null; }
            selectedItemScreen.item = _selected;
        }

        private void selectItem(Item item)
        {
            _selected = item;
            selectedItemScreen.item = item;
            //The open enchantment belonged to the item that was open a moment ago.
            selectEnchantment(null);
        }

        /// <summary>
        /// Pushes the edited list back into the run and saves.
        ///
        /// The whole list goes back rather than the one item that changed: the items were read
        /// out of the save's array by position, so replacing the array is the only write that
        /// cannot put an edit on the wrong item.
        /// </summary>
        private void commit()
        {
            _player?.setItems(_items);
            requestSave?.Invoke();
            fillItems();
        }

        private void saveItem(Item item)
        {
            if (item == null) { return; }
            commit();
        }

        private void removeItem(Item item)
        {
            if (item == null) { return; }
            _items.Remove(item);
            _selected = null;
            selectedItemScreen.item = null;
            selectEnchantment(null);
            commit();
        }

        private void duplicateItem(Item item)
        {
            if (item == null) { return; }
            var copy = item.Copy();
            _items.Add(copy);
            _selected = copy;
            commit();
            selectedItemScreen.item = copy;
        }

        /// <summary>
        /// Takes an item out of the tower and puts it where it can be used.
        ///
        /// Gear won inside a run stays in the run until the run ends, so this is the whole reason
        /// to open this tab with an item selected. It leaves the tower, which is why it is
        /// removed here rather than copied.
        /// </summary>
        private void moveItem(Item item, bool toChest)
        {
            if (item == null || _model == null) { return; }

            var moved = item.Copy();
            moved.EquipmentSlot = null;
            moved.MarkedNew = true;

            if (toChest)
            {
                _model.storageChestEquipmentModel.selectEnchantment(null);
                _model.storageChestEquipmentModel.addItemToList(moved);
            }
            else
            {
                _model.mainEquipmentModel.selectEnchantment(null);
                _model.mainEquipmentModel.addItemToList(moved);
            }

            _items.Remove(item);
            _selected = null;
            selectedItemScreen.item = null;
            commit();

            MessageBox.Show(
                toChest
                    ? R.formatTOWER_MOVED_CHEST(R.itemName(moved.Type))
                    : R.formatTOWER_MOVED_INVENTORY(R.itemName(moved.Type)),
                R.THE_TOWER);
        }

        #endregion
    }
}
