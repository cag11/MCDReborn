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
    /// New items: melee weapons, ranged weapons, armour and artifacts under ids of MCD Reborn's own
    /// (MCDR_Item01 and on), as many as the user likes.
    ///
    /// Each is a copy of an existing item - its model, its combo, its icon - and then changed: its
    /// name and description, a different item's icon or the user's own picture, and every number
    /// the copy's blueprint stores. All of it is built into one pak, because the game's asset
    /// registry has to list every custom item and only one pak can supply that, and the plugin
    /// (GamePlugin) registers the ids when the game starts. A new one is "not saved yet" until it
    /// is installed.
    ///
    /// The free slots the game already knew - nine cut ids - came first and were dropped: each was
    /// one fixed type and frame, and having two kinds of custom item confused more than it helped.
    /// </summary>
    public partial class CustomItemsTab : UserControl
    {
        private List<CustomItems.Design> _designs = new();
        private CustomItems.Slot? _slot;
        /// <summary>New plugin items made with the New buttons and not installed yet.</summary>
        private readonly List<CustomItems.Slot> _pending = new();
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
            clearButton.Content = R.ITEMS_DELETE;
            exportButton.Content = R.ITEMS_EXPORT;
            importButton.Content = R.ITEMS_IMPORT;
            newMeleeButton.Content = R.ITEMS_NEW_MELEE;
            newRangedButton.Content = R.ITEMS_NEW_RANGED;
            newArmorButton.Content = R.ITEMS_NEW_ARMOR;
            newArtifactButton.Content = R.ITEMS_NEW_ARTIFACT;
            pluginNote.Text = R.ITEMS_PLUGIN_KEEP;
            skillsLabel.Content = R.ITEMS_SKILLS;
            skillsHint.Text = R.ITEMS_SKILLS_HINT;
            addSkillButton.Content = R.ITEMS_SKILL_ADD;
            traitsResetButton.Content = R.ITEMS_TRAITS_RESET;
            linesLabel.Content = R.ITEMS_LINES;
            linesHint.Text = R.ITEMS_LINES_HINT;
            armorPropertiesLabel.Content = R.ITEMS_ARMOR_PROPERTIES;
            armorPropertiesHint.Text = R.ITEMS_ARMOR_PROPERTIES_HINT;
            addArmorPropertyButton.Content = R.ITEMS_SKILL_ADD;
            artifactLabel.Content = R.ITEMS_ARTIFACT;
            artifactHint.Text = R.ITEMS_ARTIFACT_HINT;
            cooldownLabel.Text = R.ITEMS_COOLDOWN;
            durationLabel.Text = R.ITEMS_DURATION;
            soulCostLabel.Text = R.ITEMS_SOUL_COST;
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
            statusLabel.Text = string.Empty;
            fillSlots();
            showPluginStatus();
        }

        /// <summary>
        /// Whether items can be added here, and what the plugin said the last time the game
        /// started - the one place to see that it registered them.
        /// </summary>
        private void showPluginStatus()
        {
            var available = GamePlugin.gameFolder() != null;
            newButtons.IsEnabled = newArtifactButton.IsEnabled = available;
            //Only a reason to show: on the Xbox app's install, items cannot be made at all.
            slotsHint.Text = available ? string.Empty : R.ITEMS_NEW_UNAVAILABLE;
            slotsHint.Visibility = available ? Visibility.Collapsed : Visibility.Visible;
            pluginStatus.Text = string.Empty;
            if (!available) { return; }

            var log = GamePlugin.lastLog();
            //Its lines start with the time: "07:46:32.184  done: 1 item(s), registry now holds 325".
            var last = log?.LastOrDefault(l => l.Contains("done:") || l.Contains("nothing was changed") || l.Contains("NOT "));
            if (last != null)
            {
                var text = last.Length > 14 && last[2] == ':' ? last.Substring(14).Trim() : last;
                pluginStatus.Text = string.Format(R.ITEMS_PLUGIN_LAST, text);
            }
            else if (_designs.Any(d => CustomItems.isPluginId(d.Slot)))
            {
                pluginStatus.Text = R.ITEMS_PLUGIN_NEVER;
            }
        }

        /// <summary>The items installed, then any new ones not installed yet.</summary>
        private IEnumerable<CustomItems.Slot> allSlots()
        {
            foreach (var design in _designs)
            {
                CustomItems.Slot? slot = null;
                try { slot = CustomItems.slotOf(design); } catch (Exception) { }
                if (slot != null) { yield return slot; }
            }
            foreach (var slot in _pending.Where(p => _designs.All(d => d.Slot != p.Id))) { yield return slot; }
        }

        /// <summary>What an item is called in this tab: its name, else its source's, else - new - its type.</summary>
        private string displayName(CustomItems.Slot slot)
        {
            var design = _designs.FirstOrDefault(d => d.Slot == slot.Id);
            if (!string.IsNullOrWhiteSpace(design?.Name)) { return design!.Name!; }
            if (design != null && !string.IsNullOrEmpty(design.Source)) { return R.itemName(design.Source); }
            return string.Format(R.ITEMS_NEW_TITLE, kindName(slot.Kind).ToLower(CultureInfo.CurrentCulture));
        }

        private void fillSlots()
        {
            var keep = _slot?.Id;
            slotList.Items.Clear();
            foreach (var slot in allSlots())
            {
                var design = _designs.FirstOrDefault(d => d.Slot == slot.Id);
                var state = design == null ? R.ITEMS_NEW_UNSAVED : string.Format(R.ITEMS_SLOT_FILLED, R.itemName(design.Source));

                var text = new StackPanel { Margin = new Thickness(0, 3, 0, 3) };
                var title = displayName(slot);
                text.Children.Add(new TextBlock { Text = title, FontWeight = FontWeights.SemiBold });
                text.Children.Add(new TextBlock
                {
                    Text = $"{kindName(slot.Kind)} · {state}",
                    FontSize = 11,
                    Foreground = Brushes.Gray,
                });
                var item = new ListBoxItem { Content = text, Tag = slot };
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
                ? new CustomItems.Design { Slot = slot.Id, PluginKind = slot.Plugin ? slot.Kind : null }
                : CustomItems.copy(saved);

            slotTitle.Text = displayName(slot);
            slotDetail.Text = $"{slot.Id} · {kindName(slot.Kind)}";
            editor.IsEnabled = true;
            clearButton.IsEnabled = saved != null;

            _filling = true;
            nameBox.Text = _working.Name ?? string.Empty;
            descriptionBox.Text = _working.Description ?? string.Empty;
            //The source's wording, shown where nothing has been typed, so it is clear what an empty
            //box keeps.
            var own = string.IsNullOrEmpty(_working.Source) ? null : _working.Source;
            nameBox.ToolTip = own == null ? null : R.itemName(own);
            descriptionBox.ToolTip = own == null ? null : R.itemDesc(own);
            iconCopied.IsChecked = _working.Icon == CustomItems.IconSource.Copied;
            iconOther.IsChecked = _working.Icon == CustomItems.IconSource.OtherItem;
            iconImage.IsChecked = _working.Icon == CustomItems.IconSource.Image;
            fillIconItems();
            _filling = false;

            //An armour stores none of its numbers in its blueprints: its stats are compiled into the
            //game, and its armour properties are saved on each character. So there is no grid. An
            //artifact's main numbers are its native class's; the grid shows the few extras its
            //blueprint stores, and the per-swing multipliers mean nothing for it.
            var armour = slot.Kind == CustomItems.Kind.Armor;
            var artifact = slot.Kind == CustomItems.Kind.Artifact;
            behaviourHint.Text = armour ? R.ITEMS_BEHAVIOUR_ARMOR : artifact ? R.ITEMS_BEHAVIOUR_ARTIFACT : R.ITEMS_BEHAVIOUR_HINT;
            behaviourGrid.Visibility = armour ? Visibility.Collapsed : Visibility.Visible;
            multipliers.Visibility = armour || artifact ? Visibility.Collapsed : Visibility.Visible;

            fillSources();
            loadBehaviour();
            showTraits();
            updatePreview();
            updateButtons();
        }

        private void searchBox_TextChanged(object sender, TextChangedEventArgs e) => fillSources();

        // ------------------------------------------------------------------ new items

        private void newMeleeButton_Click(object sender, RoutedEventArgs e) => addNew(CustomItems.Kind.Melee);

        private void newRangedButton_Click(object sender, RoutedEventArgs e) => addNew(CustomItems.Kind.Ranged);

        private void newArmorButton_Click(object sender, RoutedEventArgs e) => addNew(CustomItems.Kind.Armor);

        private void newArtifactButton_Click(object sender, RoutedEventArgs e) => addNew(CustomItems.Kind.Artifact);

        /// <summary>A new id, listed and opened. Nothing is written until it is installed.</summary>
        private void addNew(CustomItems.Kind kind)
        {
            if (!_loaded) { statusLabel.Text = R.ITEMS_NOT_READY; return; }
            var slot = CustomItems.pluginSlot(CustomItems.newPluginId(_designs), kind);
            _pending.Add(slot);
            _slot = null;
            _working = null;
            fillSlots();
            select(slot.Id);
        }

        private void select(string id)
        {
            foreach (ListBoxItem item in slotList.Items)
            {
                if (item.Tag is CustomItems.Slot s && s.Id == id) { item.IsSelected = true; slotList.ScrollIntoView(item); }
            }
        }

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
            showTraits();
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

        // ------------------------------------------------------------------ skills and property lines

        /// <summary>
        /// A weapon's built-in skills and property lines, as the design will give them: its own
        /// picks, else the copied weapon's (CustomItems.skillsOf / linesOf). The first change turns
        /// the copied weapon's into the design's own, so a later change of source leaves them be.
        /// Each type offers what its own weapons have: a bow is not offered a melee enchantment.
        /// </summary>
        private void showTraits()
        {
            var shown = _slot != null && _working != null && CustomItems.hasTraits(_slot.Kind);
            traitsPanel.Visibility = shown ? Visibility.Visible : Visibility.Collapsed;
            if (!shown) { return; }
            var kind = _slot!.Kind;
            //Each section only where the type has one: artifacts have no skills, armor no lines.
            skillsSection.Visibility = GearTraits.SKILLS[kind].Count > 0 ? Visibility.Visible : Visibility.Collapsed;
            showArtifactNumbers(kind);

            var skills = CustomItems.skillsOf(_working!);
            skillsList.Children.Clear();
            if (skills.Count == 0)
            {
                skillsList.Children.Add(new TextBlock { Text = R.ITEMS_SKILLS_NONE, Foreground = Brushes.Gray, Margin = new Thickness(2, 0, 0, 0) });
            }
            for (var i = 0; i < skills.Count; i++)
            {
                var at = i;
                var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 3) };
                row.Children.Add(new TextBlock { Text = skillName(skills[i].Skill), Width = 220, VerticalAlignment = VerticalAlignment.Center });
                row.Children.Add(new TextBlock { Text = R.ITEMS_SKILL_LEVEL, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 6, 0) });
                //1 to 3 as enchantments go, and 99: every Trickbow's Ricochet is 99, its "always".
                var levels = new[] { 1, 2, 3, 99 }.Union(new[] { skills[i].Level }).OrderBy(l => l).ToList();
                var level = new ComboBox { Width = 56, ItemsSource = levels, SelectedItem = skills[i].Level };
                level.SelectionChanged += (_, _) =>
                {
                    if (level.SelectedItem is not int chosen) { return; }
                    ownSkills()[at].Level = chosen;
                };
                row.Children.Add(level);
                var remove = new Button { Content = "✕", Padding = new Thickness(6, 0, 6, 0), Margin = new Thickness(8, 0, 0, 0), ToolTip = R.ITEMS_SKILL_REMOVE };
                remove.Click += (_, _) =>
                {
                    ownSkills().RemoveAt(at);
                    showTraits();
                };
                row.Children.Add(remove);
                skillsList.Children.Add(row);
            }

            addSkillBox.ItemsSource = GearTraits.SKILLS[kind]
                .Where(s => skills.All(k => k.Skill != s))
                .Select(s => new ComboBoxItem { Content = skillName(s), Tag = s })
                .OrderBy(i => (string)i.Content, StringComparer.CurrentCultureIgnoreCase)
                .ToList();

            var lines = CustomItems.linesOf(_working!);
            linesPanel.Children.Clear();
            showArmorProperties(kind);
            var catalogue = GearTraits.LINES[kind];
            //Armor has no property lines: its tooltip lists its armor properties instead.
            linesSection.Visibility = catalogue.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
            foreach (var key in catalogue.Keys.OrderBy(k => lineName(k, catalogue), StringComparer.CurrentCultureIgnoreCase))
            {
                var box = new CheckBox { Content = lineName(key, catalogue), IsChecked = lines.Contains(key), Margin = new Thickness(0, 0, 14, 4), Tag = key };
                box.Checked += line_Changed;
                box.Unchecked += line_Changed;
                linesPanel.Children.Add(box);
            }
        }

        /// <summary>The design's own skills, made from the copied weapon's the first time one is changed.</summary>
        private List<CustomItems.SkillPick> ownSkills()
        {
            _working!.Skills ??= CustomItems.skillsOf(_working);
            return _working.Skills;
        }

        private void addSkillButton_Click(object sender, RoutedEventArgs e)
        {
            if (_working == null || addSkillBox.SelectedItem is not ComboBoxItem { Tag: string skill }) { return; }
            ownSkills().Add(new CustomItems.SkillPick { Skill = skill, Level = 1 });
            showTraits();
        }

        private void line_Changed(object sender, RoutedEventArgs e)
        {
            if (_working == null || sender is not CheckBox { Tag: string key } box) { return; }
            _working.Lines ??= CustomItems.linesOf(_working);
            _working.Lines.Remove(key);
            if (box.IsChecked == true) { _working.Lines.Add(key); }
        }

        private void traitsResetButton_Click(object sender, RoutedEventArgs e)
        {
            if (_working == null) { return; }
            _working.Skills = null;
            _working.Lines = null;
            _working.ArmorProperties = null;
            _working.Cooldown = null;
            _working.Duration = null;
            _working.SoulCost = null;
            showTraits();
        }

        /// <summary>
        /// An artifact's cooldown, duration and soul cost: what the design gives, blank where it keeps
        /// the copied artifact's - whose own number is shown under each box.
        /// </summary>
        private void showArtifactNumbers(CustomItems.Kind kind)
        {
            var artifact = kind == CustomItems.Kind.Artifact && _working != null;
            artifactSection.Visibility = artifact ? Visibility.Visible : Visibility.Collapsed;
            if (!artifact) { return; }
            var own = CustomItems.artifactNumbersOf(_working!);
            string shown(double? value) => value is { } v ? format(v) : string.Empty;
            _filling = true;
            cooldownBox.Text = shown(_working!.Cooldown);
            durationBox.Text = shown(_working.Duration);
            soulCostBox.Text = shown(_working.SoulCost);
            _filling = false;
            cooldownBox.ToolTip = string.Format(R.ITEMS_COPIED_VALUE, format(own.cooldown));
            durationBox.ToolTip = string.Format(R.ITEMS_COPIED_VALUE, format(own.duration));
            soulCostBox.ToolTip = string.Format(R.ITEMS_COPIED_VALUE, format(own.souls));
            artifactHint.Text = R.ITEMS_ARTIFACT_HINT + " " + string.Format(R.ITEMS_ARTIFACT_OWN,
                format(own.cooldown), format(own.duration), format(own.souls));
        }

        private void artifactNumber_Changed(object sender, TextChangedEventArgs e)
        {
            if (_filling || _working == null) { return; }
            double? read(TextBox box) => tryParse(box.Text, out var v) && v >= 0 ? v : null;
            _working.Cooldown = read(cooldownBox);
            _working.Duration = read(durationBox);
            _working.SoulCost = read(soulCostBox);
        }

        /// <summary>
        /// An armor's default armor properties, as the design will give them: its own picks, else
        /// the copied armor's. Each has a rarity: common, or unique for the gold line a unique
        /// armor leads with.
        /// </summary>
        private void showArmorProperties(CustomItems.Kind kind)
        {
            var armour = kind == CustomItems.Kind.Armor;
            armorSection.Visibility = armour ? Visibility.Visible : Visibility.Collapsed;
            if (!armour || _working == null) { return; }

            var properties = CustomItems.armorPropertiesOf(_working);
            armorPropertiesList.Children.Clear();
            if (properties.Count == 0)
            {
                armorPropertiesList.Children.Add(new TextBlock { Text = R.ITEMS_ARMOR_PROPERTIES_NONE, Foreground = Brushes.Gray, Margin = new Thickness(2, 0, 0, 0) });
            }
            for (var i = 0; i < properties.Count; i++)
            {
                var at = i;
                var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 3) };
                row.Children.Add(new TextBlock { Text = R.armorProperty(properties[i].Property), Width = 260, VerticalAlignment = VerticalAlignment.Center,
                    ToolTip = R.armorPropertyDescription(properties[i].Property) });
                var rarity = new ComboBox { Width = 110, ItemsSource = new[] { R.ITEMS_RARITY_COMMON, R.ITEMS_RARITY_UNIQUE },
                    SelectedIndex = properties[i].Rarity == 2 ? 1 : 0 };
                rarity.SelectionChanged += (_, _) => { ownArmorProperties()[at].Rarity = rarity.SelectedIndex == 1 ? 2 : 0; };
                row.Children.Add(rarity);
                var remove = new Button { Content = "✕", Padding = new Thickness(6, 0, 6, 0), Margin = new Thickness(8, 0, 0, 0), ToolTip = R.ITEMS_SKILL_REMOVE };
                remove.Click += (_, _) =>
                {
                    ownArmorProperties().RemoveAt(at);
                    showArmorProperties(kind);
                };
                row.Children.Add(remove);
                armorPropertiesList.Children.Add(row);
            }

            addArmorPropertyBox.ItemsSource = GearTraits.ARMOR_PROPERTIES
                .Where(p => properties.All(k => k.Property != p))
                .Select(p => new ComboBoxItem { Content = R.armorProperty(p), Tag = p, ToolTip = R.armorPropertyDescription(p) })
                .OrderBy(i => (string)i.Content, StringComparer.CurrentCultureIgnoreCase)
                .ToList();
        }

        private List<CustomItems.ArmorPick> ownArmorProperties()
        {
            _working!.ArmorProperties ??= CustomItems.armorPropertiesOf(_working);
            return _working.ArmorProperties;
        }

        private void addArmorPropertyButton_Click(object sender, RoutedEventArgs e)
        {
            if (_working == null || addArmorPropertyBox.SelectedItem is not ComboBoxItem { Tag: string property }) { return; }
            ownArmorProperties().Add(new CustomItems.ArmorPick { Property = property, Rarity = 0 });
            showArmorProperties(CustomItems.Kind.Armor);
        }

        /// <summary>
        /// An enchantment's name in the player's language. The game's melee and ranged variants
        /// carry a suffix its text does not ("GravityMelee" is Gravity, "PoisonedRanged" is
        /// Poisoned), so that is tried too.
        /// </summary>
        private static string skillName(string skill)
        {
            var name = R.enchantmentName(skill);
            if (name != skill) { return name; }
            foreach (var suffix in new[] { "Melee", "Ranged" })
            {
                if (!skill.EndsWith(suffix, StringComparison.Ordinal)) { continue; }
                var bare = skill.Substring(0, skill.Length - suffix.Length);
                var shorter = R.enchantmentName(bare);
                if (shorter != bare) { return shorter; }
            }
            return skill;
        }

        /// <summary>A property line in the player's language: the game's own ItemType text, else its English.</summary>
        private static string lineName(string key, IReadOnlyDictionary<string, string> catalogue)
        {
            var name = R.itemName(key);
            return name != key ? name : catalogue[key];
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
            var saved = _slot != null && _designs.Any(d => d.Slot == _slot.Id);
            clearButton.IsEnabled = saved || _slot?.Plugin == true;
            exportButton.IsEnabled = saved;
        }

        private void exportButton_Click(object sender, RoutedEventArgs e)
        {
            if (_slot == null) { return; }
            //From disk rather than from this tab: a model set in the Weapons tab is saved there.
            var design = CustomItems.load().FirstOrDefault(d => d.Slot == _slot.Id);
            if (design == null) { return; }

            var name = displayName(_slot);
            var dialog = new SaveFileDialog
            {
                FileName = CustomSkins.safeName(name) + CustomItems.SHARE_EXTENSION,
                Filter = $"{R.ITEMS_FILE_KIND}|*{CustomItems.SHARE_EXTENSION}",
            };
            if (dialog.ShowDialog() != true) { return; }
            try
            {
                if (File.Exists(dialog.FileName)) { File.Delete(dialog.FileName); }
                CustomItems.export(design, dialog.FileName);
                statusLabel.Text = string.Format(R.ITEMS_EXPORTED, Path.GetFileName(dialog.FileName));
            }
            catch (Exception problem) { statusLabel.Text = problem.Message; }
        }

        private async void importButton_Click(object sender, RoutedEventArgs e)
        {
            if (!_loaded) { statusLabel.Text = R.ITEMS_NOT_READY; return; }
            if (GameRunning.isUp) { MessageBox.Show(R.MODS_GAME_RUNNING, R.ITEMS_TAB); return; }
            var dialog = new OpenFileDialog { Filter = $"{R.ITEMS_FILE_KIND}|*{CustomItems.SHARE_EXTENSION}" };
            if (dialog.ShowDialog() != true) { return; }

            try
            {
                var shared = CustomItems.readShared(dialog.FileName);
                if (GamePlugin.gameFolder() == null) { statusLabel.Text = R.ITEMS_NEW_UNAVAILABLE; return; }
                //Always a new item: it never takes the place of one the user already has.
                var slot = CustomItems.pluginSlot(CustomItems.newPluginId(_designs), shared.Kind);

                var design = CustomItems.unpack(dialog.FileName, shared, slot);
                var designs = _designs.Where(d => d.Slot != slot.Id).Select(CustomItems.copy).ToList();
                designs.Add(design);
                await build(designs, fresh: slot.Id);

                //Show it, read afresh from what was just saved.
                _slot = null;
                _working = null;
                select(slot.Id);
                statusLabel.Text = string.Format(R.ITEMS_IMPORTED, displayName(slot)) + "   " + statusLabel.Text;
            }
            catch (Exception problem) { statusLabel.Text = string.Format(R.ITEMS_FAILED, problem.Message); }
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
            var gone = _slot.Id;
            if (_designs.All(d => d.Slot != gone))
            {
                //Never installed: nothing to take out of the game.
                _pending.RemoveAll(p => p.Id == gone);
                _slot = null;
                _working = null;
                editor.IsEnabled = false;
                fillSlots();
                updateButtons();
                return;
            }
            if (GameRunning.isUp) { MessageBox.Show(R.MODS_GAME_RUNNING, R.ITEMS_TAB); return; }
            var ask = MessageBox.Show(string.Format(R.ITEMS_DELETE_WARN, displayName(_slot)), R.ITEMS_TAB,
                MessageBoxButton.YesNo, MessageBoxImage.Warning);
            if (ask != MessageBoxResult.Yes) { return; }

            //A deleted item leaves the list; its id is not handed out again.
            _pending.RemoveAll(p => p.Id == gone);
            _slot = null;
            _working = null;
            editor.IsEnabled = false;
            await build(_designs.Where(d => d.Slot != gone).ToList());
        }

        private async Task build(List<CustomItems.Design> designs, string? fresh = null)
        {
            //A model is set from the Weapons tab and a recolour from the Recolor Gear tab, both
            //saved straight to disk. What this tab holds may predate them, so the disk wins - unless
            //the item is now a copy of something else, which neither was made for.
            var onDisk = CustomItems.load();
            foreach (var design in designs)
            {
                //An imported item brings its own model; the slot's old one is not it.
                if (design.Slot == fresh) { continue; }
                var saved = onDisk.FirstOrDefault(d => d.Slot == design.Slot);
                var sameSource = saved != null && string.Equals(saved.Source, design.Source, StringComparison.OrdinalIgnoreCase);
                design.Model = sameSource ? saved!.Model : null;
                //The Recolor Gear tab's picture, saved the same way; it fits only the texture it
                //was made for.
                design.Texture = sameSource ? saved!.Texture : null;
            }

            IsEnabled = false;
            statusLabel.Text = R.ITEMS_WORKING;
            try
            {
                var built = await Task.Run(() => CustomItems.build(designs));
                _designs = designs.Select(CustomItems.copy).ToList();
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
                _pending.RemoveAll(p => _designs.Any(d => d.Slot == p.Id));
                fillSlots();
                updateButtons();
                showPluginStatus();
            }
        }
    }
}
