using MCDSaveEdit.Logic;
using MCDSaveEdit.Services;
using Microsoft.Win32;
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
#nullable enable

namespace MCDSaveEdit.UI
{
    /// <summary>
    /// New items, in the slots the game already knows about.
    ///
    /// The game registers nine equippable items it never shipped files for. Each can be filled
    /// with a copy of an existing item of the same type - its model, its combo, its icon - and then
    /// changed: a different item's icon or the user's own picture, and every number the copy's
    /// blueprint stores about how it swings. All of it is built into one pak, because the game's
    /// asset registry has to list every custom item and only one pak can supply that.
    ///
    /// Its name and description can be changed too, in every language at once. What cannot change
    /// is what the game compiled in: the slot's type and its unique frame.
    /// </summary>
    public partial class CustomItemsTab : UserControl
    {
        private List<CustomItems.Design> _designs = new();
        private CustomItems.Slot? _slot;
        private CustomItems.Design? _working;
        private List<ItemBehaviour.Number> _numbers = new();
        private readonly Dictionary<string, BitmapSource?> _icons = new(StringComparer.OrdinalIgnoreCase);
        private bool _loaded;
        private bool _filling;

        /// <summary>The settings shown without "Show every value", with what the grid calls them.</summary>
        private static readonly Dictionary<string, Func<string>> SETTINGS = new()
        {
            ["Damage"] = () => R.ITEMS_SET_DAMAGE,
            ["AttackAnimationTimeDurationSeconds"] = () => R.ITEMS_SET_SWING,
            ["AttackRange"] = () => R.ITEMS_SET_REACH,
            ["ConeAngleDegrees"] = () => R.ITEMS_SET_ARC,
            ["DamageSplashMultiplier"] = () => R.ITEMS_SET_SPLASH,
            ["StunMultiplier"] = () => R.ITEMS_SET_STUN,
            ["pushbackStrength"] = () => R.ITEMS_SET_KNOCKBACK,
            ["CooldownSeconds"] = () => R.ITEMS_SET_COOLDOWN,
            ["DamageDelaySeconds"] = () => R.ITEMS_SET_DELAY,
        };

        /// <summary>One number in the grid.</summary>
        public sealed class Row : INotifyPropertyChanged
        {
            private string _yours = "";
            public string Path { get; set; } = "";
            public string Swing { get; set; } = "";
            public string Setting { get; set; } = "";
            public string Leaf { get; set; } = "";
            public double GameValue { get; set; }
            public string Game => format(GameValue);
            public string Yours
            {
                get => _yours;
                set { _yours = value; PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Yours))); }
            }
            public event PropertyChangedEventHandler? PropertyChanged;
        }

        private List<Row> _rows = new();

        public CustomItemsTab()
        {
            InitializeComponent();
            setStrings();
            editor.IsEnabled = false;
            installButton.IsEnabled = clearButton.IsEnabled = false;
            IsVisibleChanged += (_, _) => { if (IsVisible) { refresh(); } };
        }

        private void setStrings()
        {
            slotsLabel.Content = R.ITEMS_SLOTS;
            nameLabel.Text = R.ITEMS_NAME;
            descriptionLabel.Text = R.ITEMS_DESCRIPTION;
            textHint.Text = R.ITEMS_TEXT_HINT;
            slotsHint.Text = R.ITEMS_SLOTS_HINT;
            copyLabel.Content = R.ITEMS_COPY_FROM;
            searchHint.Text = R.ITEMS_SEARCH;
            iconLabel.Content = R.ITEMS_ICON;
            iconCopied.Content = R.ITEMS_ICON_COPIED;
            iconOther.Content = R.ITEMS_ICON_OTHER;
            iconImage.Content = R.ITEMS_ICON_IMAGE;
            chooseImageButton.Content = R.ITEMS_ICON_CHOOSE;
            behaviourLabel.Content = R.ITEMS_BEHAVIOUR;
            behaviourHint.Text = R.ITEMS_BEHAVIOUR_HINT;
            allSwingsLabel.Text = R.ITEMS_ALL_SWINGS;
            damageXLabel.Text = R.ITEMS_DAMAGE_X;
            speedXLabel.Text = R.ITEMS_SPEED_X;
            reachXLabel.Text = R.ITEMS_REACH_X;
            applyButton.Content = R.ITEMS_APPLY;
            resetButton.Content = R.ITEMS_RESET;
            showAllBox.Content = R.ITEMS_SHOW_ALL;
            swingColumn.Header = R.ITEMS_COL_SWING;
            settingColumn.Header = R.ITEMS_COL_SETTING;
            gameColumn.Header = R.ITEMS_COL_GAME;
            yoursColumn.Header = R.ITEMS_COL_YOURS;
            installButton.Content = R.ITEMS_INSTALL;
            clearButton.Content = R.ITEMS_CLEAR;
        }

        // ------------------------------------------------------------------ loading

        /// <summary>
        /// Reads the game's item list once, the first time the tab is looked at. It comes out of
        /// the 48 MB asset registry, so it is read off the UI thread.
        /// </summary>
        public async void refresh()
        {
            if (_loaded) { return; }
            if (!CustomSkins.ready) { statusLabel.Text = R.ITEMS_NOT_READY; return; }
            _loaded = true;
            statusLabel.Text = R.ITEMS_WORKING;

            await Task.Run(() => CustomItems.gameItems());
            _designs = CustomItems.load();
            adoptTestPak();
            statusLabel.Text = string.Empty;
            fillSlots();
        }

        /// <summary>
        /// The SpiderCrossbow test was installed as a pak of its own. The first build replaces it,
        /// so it becomes a design here first - otherwise a character holding it would lose the
        /// files it needs and crash the game.
        /// </summary>
        private void adoptTestPak()
        {
            var mods = CustomSkins.modsFolder;
            if (mods == null || !File.Exists(Path.Combine(mods, "MCDReborn_SpiderCrossbow_P.pak"))) { return; }
            if (_designs.Any(d => d.Slot == "SpiderCrossbow")) { return; }
            _designs.Add(new CustomItems.Design { Slot = "SpiderCrossbow", Source = "HeavyCrossbow" });
            CustomItems.save(_designs);
        }

        private void fillSlots()
        {
            var keep = _slot?.Id;
            slotList.Items.Clear();
            foreach (var slot in CustomItems.slots)
            {
                var design = _designs.FirstOrDefault(d => d.Slot == slot.Id);
                var state = !slot.Ready ? R.ITEMS_SLOT_LATER
                    : design == null ? R.ITEMS_SLOT_EMPTY
                    : string.Format(R.ITEMS_SLOT_FILLED, R.itemName(design.Source));

                var text = new StackPanel { Margin = new Thickness(0, 3, 0, 3) };
                var title = string.IsNullOrWhiteSpace(design?.Name) ? slot.BuiltInName : design!.Name!;
                text.Children.Add(new TextBlock { Text = title, FontWeight = FontWeights.SemiBold });
                text.Children.Add(new TextBlock
                {
                    Text = $"{kindName(slot.Kind)}{(slot.Unique ? " · " + R.ITEMS_UNIQUE : "")} · {state}",
                    FontSize = 11,
                    Foreground = Brushes.Gray,
                });
                var item = new ListBoxItem { Content = text, Tag = slot, IsEnabled = slot.Ready };
                slotList.Items.Add(item);
                if (slot.Id == keep) { item.IsSelected = true; }
            }
        }

        private static string kindName(CustomItems.Kind kind) => kind switch
        {
            CustomItems.Kind.Melee => R.ITEMS_KIND_MELEE,
            CustomItems.Kind.Ranged => R.ITEMS_KIND_RANGED,
            CustomItems.Kind.Armor => R.ITEMS_KIND_ARMOR,
            _ => R.ITEMS_KIND_ARTIFACT,
        };

        // ------------------------------------------------------------------ choosing a slot

        private void slotList_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (slotList.SelectedItem is not ListBoxItem { Tag: CustomItems.Slot slot }) { return; }
            if (_slot?.Id == slot.Id && _working != null) { return; }
            _slot = slot;

            var saved = _designs.FirstOrDefault(d => d.Slot == slot.Id);
            _working = saved == null
                ? new CustomItems.Design { Slot = slot.Id }
                : new CustomItems.Design
                {
                    Slot = saved.Slot,
                    Source = saved.Source,
                    Icon = saved.Icon,
                    IconItem = saved.IconItem,
                    IconFile = saved.IconFile,
                    Values = new Dictionary<string, double>(saved.Values),
                    Name = saved.Name,
                    Description = saved.Description,
                    Model = saved.Model,
                };

            slotTitle.Text = slot.BuiltInName;
            slotDetail.Text = $"{slot.Id} · {kindName(slot.Kind)}{(slot.Unique ? " · " + R.ITEMS_UNIQUE : "")}";
            editor.IsEnabled = true;
            clearButton.IsEnabled = saved != null;

            _filling = true;
            nameBox.Text = _working.Name ?? string.Empty;
            descriptionBox.Text = _working.Description ?? string.Empty;
            //The game's own wording, shown where nothing has been typed, so it is clear what an
            //empty box keeps.
            nameBox.ToolTip = R.itemName(slot.Id);
            descriptionBox.ToolTip = R.itemDesc(slot.Id);
            iconCopied.IsChecked = _working.Icon == CustomItems.IconSource.Copied;
            iconOther.IsChecked = _working.Icon == CustomItems.IconSource.OtherItem;
            iconImage.IsChecked = _working.Icon == CustomItems.IconSource.Image;
            fillIconItems();
            _filling = false;

            fillSources();
            loadBehaviour();
            updatePreview();
            updateButtons();
        }

        private void searchBox_TextChanged(object sender, TextChangedEventArgs e) => fillSources();

        private void text_Changed(object sender, TextChangedEventArgs e)
        {
            if (_filling || _working == null) { return; }
            _working.Name = string.IsNullOrWhiteSpace(nameBox.Text) ? null : nameBox.Text.Trim();
            _working.Description = string.IsNullOrWhiteSpace(descriptionBox.Text) ? null : descriptionBox.Text.Trim();
        }

        private void fillSources()
        {
            if (_slot == null) { return; }
            var wanted = searchBox.Text?.Trim() ?? string.Empty;
            searchHint.Visibility = wanted.Length == 0 ? Visibility.Visible : Visibility.Collapsed;

            var shown = CustomItems.sourcesFor(_slot)
                .Select(item => new { Item = item, Name = R.itemName(item.Id) })
                .Where(x => wanted.Length == 0
                    || x.Name.IndexOf(wanted, StringComparison.OrdinalIgnoreCase) >= 0
                    || x.Item.Id.IndexOf(wanted, StringComparison.OrdinalIgnoreCase) >= 0)
                .OrderBy(x => x.Name, StringComparer.CurrentCultureIgnoreCase)
                .Select(x => new SourceRow(x.Item, iconFor(x.Item), $"{x.Name}   ({x.Item.Id})"))
                .ToList();

            _filling = true;
            sourceList.ItemsSource = shown;
            sourceList.SelectedItem = shown.FirstOrDefault(s => string.Equals(s.Item.Id, _working?.Source, StringComparison.OrdinalIgnoreCase));
            if (sourceList.SelectedItem != null) { sourceList.ScrollIntoView(sourceList.SelectedItem); }
            _filling = false;
        }

        public sealed record SourceRow(RegistryPatch.GameItem Item, BitmapSource? Icon, string Text);

        private BitmapSource? iconFor(RegistryPatch.GameItem item)
        {
            if (!_icons.TryGetValue(item.Id, out var icon))
            {
                try { icon = CustomItems.iconOf(item); } catch (Exception) { icon = null; }
                _icons[item.Id] = icon;
            }
            return icon;
        }

        private void sourceList_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_filling || _working == null || sourceList.SelectedItem is not SourceRow row) { return; }
            if (string.Equals(_working.Source, row.Item.Id, StringComparison.OrdinalIgnoreCase)) { return; }
            _working.Source = row.Item.Id;
            //Another item's numbers are another item's; carrying edits across would apply
            //"swing 3 damage" to a weapon whose third swing is something else entirely.
            _working.Values.Clear();
            loadBehaviour();
            updatePreview();
            updateButtons();
        }

        // ------------------------------------------------------------------ the icon

        private void fillIconItems()
        {
            var all = CustomItems.gameItems()
                .GroupBy(i => i.Id, StringComparer.OrdinalIgnoreCase).Select(g => g.First())
                .Select(i => new ComboBoxItem { Content = $"{R.itemName(i.Id)}   ({i.Id})", Tag = i.Id })
                .OrderBy(i => (string)i.Content, StringComparer.CurrentCultureIgnoreCase)
                .ToList();
            iconItemBox.ItemsSource = all;
            iconItemBox.SelectedItem = all.FirstOrDefault(i => string.Equals((string)i.Tag, _working?.IconItem, StringComparison.OrdinalIgnoreCase));
        }

        private void icon_Checked(object sender, RoutedEventArgs e)
        {
            if (_filling || _working == null) { return; }
            _working.Icon = iconOther.IsChecked == true ? CustomItems.IconSource.OtherItem
                : iconImage.IsChecked == true ? CustomItems.IconSource.Image
                : CustomItems.IconSource.Copied;
            updatePreview();
        }

        private void iconItemBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_filling || _working == null || iconItemBox.SelectedItem is not ComboBoxItem { Tag: string id }) { return; }
            _working.IconItem = id;
            _filling = true;
            iconOther.IsChecked = true;
            _filling = false;
            _working.Icon = CustomItems.IconSource.OtherItem;
            updatePreview();
        }

        private void chooseImageButton_Click(object sender, RoutedEventArgs e)
        {
            if (_working == null) { return; }
            var dialog = new OpenFileDialog { Filter = "PNG|*.png" };
            if (dialog.ShowDialog() != true) { return; }
            try
            {
                _working.IconFile = CustomItems.keepImage(_working.Slot, dialog.FileName);
                _working.Icon = CustomItems.IconSource.Image;
                _filling = true;
                iconImage.IsChecked = true;
                _filling = false;
                _icons.Remove("file:" + _working.Slot);
                updatePreview();
            }
            catch (Exception problem) { statusLabel.Text = problem.Message; }
        }

        private void updatePreview()
        {
            iconPreview.Source = null;
            if (_working == null) { return; }
            try
            {
                if (_working.Icon == CustomItems.IconSource.Image && _working.IconFile != null && File.Exists(_working.IconFile))
                {
                    iconPreview.Source = CustomSkins.imageFromPng(File.ReadAllBytes(_working.IconFile));
                }
                else if (_working.Icon == CustomItems.IconSource.OtherItem && _working.IconItem != null
                    && CustomItems.gameItem(_working.IconItem) is { } other)
                {
                    iconPreview.Source = iconFor(other);
                }
                else if (CustomItems.gameItem(_working.Source) is { } source)
                {
                    iconPreview.Source = iconFor(source);
                }
            }
            catch (Exception) { iconPreview.Source = null; }
        }

        // ------------------------------------------------------------------ behaviour

        private void loadBehaviour()
        {
            _numbers = new List<ItemBehaviour.Number>();
            if (_working != null && CustomItems.gameItem(_working.Source) is { } source)
            {
                try { _numbers = CustomItems.behaviourOf(source); } catch (Exception) { }
            }

            _rows = _numbers.Select(n =>
            {
                var leaf = n.Path.Substring(n.Path.LastIndexOf('.') + 1);
                var swing = "—";
                var open = n.Path.IndexOf('[');
                if (open >= 0 && int.TryParse(n.Path.Substring(open + 1, n.Path.IndexOf(']') - open - 1), out var index))
                {
                    swing = (index + 1).ToString(CultureInfo.CurrentCulture);
                }
                var yours = _working!.Values.TryGetValue(n.Path, out var mine) ? mine : n.Value;
                return new Row
                {
                    Path = n.Path,
                    Swing = swing,
                    Leaf = leaf,
                    Setting = SETTINGS.TryGetValue(leaf, out var name) ? name() : n.Path,
                    GameValue = n.Value,
                    Yours = format(yours),
                };
            }).ToList();
            showRows();
        }

        private void showRows()
        {
            behaviourGrid.ItemsSource = showAllBox.IsChecked == true
                ? _rows
                : _rows.Where(r => SETTINGS.ContainsKey(r.Leaf)).ToList();
        }

        private void showAllBox_Changed(object sender, RoutedEventArgs e) => showRows();

        private void behaviourGrid_CellEditEnding(object sender, DataGridCellEditEndingEventArgs e)
        {
            //The binding writes on focus loss; reading the row after it commits keeps Values in step.
            Dispatcher.BeginInvoke(new Action(collectValues));
        }

        /// <summary>The grid into the design: only what differs from the game's value is kept.</summary>
        private void collectValues()
        {
            if (_working == null) { return; }
            _working.Values.Clear();
            foreach (var row in _rows)
            {
                if (!tryParse(row.Yours, out var value)) { row.Yours = format(row.GameValue); continue; }
                if (Math.Abs(value - row.GameValue) > 1e-6) { _working.Values[row.Path] = value; }
            }
        }

        private void applyButton_Click(object sender, RoutedEventArgs e)
        {
            if (!tryParse(damageXBox.Text, out var damage) || !tryParse(speedXBox.Text, out var speed)
                || !tryParse(reachXBox.Text, out var reach) || damage <= 0 || speed <= 0 || reach <= 0)
            {
                return;
            }
            foreach (var row in _rows)
            {
                if (!tryParse(row.Yours, out var value)) { continue; }
                switch (row.Leaf)
                {
                    case "Damage": value *= damage; break;
                    //Faster means a shorter swing, and the hit has to land correspondingly sooner
                    //or it arrives after the animation has finished.
                    case "AttackAnimationTimeDurationSeconds":
                    case "DamageDelaySeconds": value /= speed; break;
                    case "AttackRange": value *= reach; break;
                    default: continue;
                }
                row.Yours = format(value);
            }
            damageXBox.Text = speedXBox.Text = reachXBox.Text = "1";
            collectValues();
        }

        private void resetButton_Click(object sender, RoutedEventArgs e)
        {
            if (_working == null) { return; }
            _working.Values.Clear();
            foreach (var row in _rows) { row.Yours = format(row.GameValue); }
        }

        private static string format(double value) => value.ToString("0.###", CultureInfo.InvariantCulture);

        /// <summary>Either decimal separator, whatever the machine's culture - a Turkish desktop types commas.</summary>
        private static bool tryParse(string? text, out double value)
            => double.TryParse((text ?? "").Trim().Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out value);

        // ------------------------------------------------------------------ installing

        private void updateButtons()
        {
            installButton.IsEnabled = _working != null && !string.IsNullOrEmpty(_working.Source);
            clearButton.IsEnabled = _slot != null && _designs.Any(d => d.Slot == _slot.Id);
        }

        private async void installButton_Click(object sender, RoutedEventArgs e)
        {
            if (_working == null) { return; }
            if (string.IsNullOrEmpty(_working.Source)) { statusLabel.Text = R.ITEMS_PICK_SOURCE; return; }
            if (GameRunning.isUp) { MessageBox.Show(R.MODS_GAME_RUNNING, R.ITEMS_TAB); return; }

            behaviourGrid.CommitEdit(DataGridEditingUnit.Row, true);
            collectValues();

            var designs = _designs.Where(d => d.Slot != _working.Slot).ToList();
            designs.Add(_working);
            await build(designs);
        }

        private async void clearButton_Click(object sender, RoutedEventArgs e)
        {
            if (_slot == null) { return; }
            if (GameRunning.isUp) { MessageBox.Show(R.MODS_GAME_RUNNING, R.ITEMS_TAB); return; }
            var ask = MessageBox.Show(string.Format(R.ITEMS_CLEAR_WARN, _slot.BuiltInName), R.ITEMS_TAB,
                MessageBoxButton.YesNo, MessageBoxImage.Warning);
            if (ask != MessageBoxResult.Yes) { return; }

            var designs = _designs.Where(d => d.Slot != _slot.Id).ToList();
            await build(designs);
            _working = new CustomItems.Design { Slot = _slot.Id };
            loadBehaviour();
            fillSources();
            updatePreview();
            updateButtons();
        }

        private async Task build(List<CustomItems.Design> designs)
        {
            //A model is set from the Weapons tab, which saves straight to disk. What this tab holds
            //may predate it, so the model on disk wins - unless the item is now a copy of something
            //else, whose mesh the old placement was never made for.
            var onDisk = CustomItems.load();
            foreach (var design in designs)
            {
                var saved = onDisk.FirstOrDefault(d => d.Slot == design.Slot);
                design.Model = saved != null && string.Equals(saved.Source, design.Source, StringComparison.OrdinalIgnoreCase)
                    ? saved.Model : null;
            }

            IsEnabled = false;
            statusLabel.Text = R.ITEMS_WORKING;
            try
            {
                var built = await Task.Run(() => CustomItems.build(designs));
                _designs = designs.Select(d => new CustomItems.Design
                {
                    Slot = d.Slot,
                    Source = d.Source,
                    Icon = d.Icon,
                    IconItem = d.IconItem,
                    IconFile = d.IconFile,
                    Values = new Dictionary<string, double>(d.Values),
                    Name = d.Name,
                    Description = d.Description,
                    Model = d.Model,
                }).ToList();
                CustomItems.save(_designs);
                //So the inventory's pickers offer the new ids straight away.
                NewContent.registerInstalled(CustomSkins.paksFolder);
                CustomItems.showInApp();
                statusLabel.Text = string.Format(R.ITEMS_INSTALLED, built.Items) + "   " + string.Join("  ", built.Notes);
            }
            catch (Exception problem)
            {
                statusLabel.Text = string.Format(R.ITEMS_FAILED, problem.Message);
            }
            finally
            {
                IsEnabled = true;
                fillSlots();
                updateButtons();
            }
        }
    }
}
