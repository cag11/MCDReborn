using MCDSaveEdit.Logic;
using MCDSaveEdit.Services;
using Microsoft.Win32;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media.Imaging;
#nullable enable

namespace MCDSaveEdit.UI
{
    /// <summary>
    /// Replacing the game's artwork with your own.
    ///
    /// Nothing here touches a save file, and nothing here modifies the game. A mod pak sits
    /// beside the game's own paks holding a file at the same asset path, and the engine loads
    /// that instead - so removing the pak is the whole of "undo", and the originals are never
    /// at risk.
    ///
    /// Two steps, because the middle one happens in a browser and people need to know that
    /// before they start. The texture travels there and back inside the link itself, so there
    /// is no file to export, find and upload - the PNG buttons are only for a texture that came
    /// from somewhere else.
    /// </summary>
    public partial class CustomSkinsTab : UserControl
    {
        private string? _selectedItem;
        private string? _selectedTexture;

        public CustomSkinsTab()
        {
            InitializeComponent();
            translateStaticStrings();
            updateUI();
        }

        private void translateStaticStrings()
        {
            gearLabel.Content = R.CUSTOM_SKINS_GEAR;
            installedLabel.Content = R.CUSTOM_SKINS_INSTALLED;
            exportButton.Content = R.CUSTOM_SKINS_EXPORT;
            designerButton.Content = R.CUSTOM_SKINS_DESIGNER;
            pasteButton.Content = R.CUSTOM_SKINS_PASTE;
            applyButton.Content = R.CUSTOM_SKINS_APPLY;
            filesLabel.Text = R.CUSTOM_SKINS_FILES;
            hintLabel.Text = R.CUSTOM_SKINS_HINT;
            modsNoteLabel.Text = R.CUSTOM_SKINS_MODS_NOTE;
            showArmourCheckBox.Content = R.ARMOUR_SHOW;

            //Loaded, not the constructor. A TabControl unloads the content of whichever tab is
            //not selected, so an Unloaded that unsubscribes without a Loaded that subscribes
            //again lasts exactly until the first time someone looks at another tab - after which
            //the hint below the box froze while the box itself still worked.
            Loaded += (s, e) => { ArmourVisibility.changed += refreshArmourBox; refreshArmourBox(); };
            Unloaded += (s, e) => ArmourVisibility.changed -= refreshArmourBox;
            addPakButton.ToolTip = R.CUSTOM_SKINS_ADD_PAK;
            openModsButton.ToolTip = R.CUSTOM_SKINS_OPEN_MODS;
        }

        public void updateUI()
        {
            fillCategories();
            fillGearList();
            fillInstalled();
            refreshArmourBox();
            updateSelection();
        }

        #region Gear

        /// <summary>
        /// What can be recoloured, in the groups someone would look for.
        ///
        /// Capes and pets are here for the same reason the rest is: their textures sit in a
        /// folder named after them, exactly as armour's does. They were missing because this list
        /// was built from the items a save can carry, and a cape is worn rather than carried, so
        /// it was never in any list this app had.
        /// </summary>
        private enum GearCategory { Armor, Melee, Ranged, Artifacts, Capes, Pets }

        private static readonly (GearCategory category, Func<string> label)[] CATEGORIES = {
            (GearCategory.Armor, () => R.getString("ItemTag_Armor") ?? R.ARMOR_ITEMS_FILTER),
            (GearCategory.Melee, () => R.getString("ItemTag_Melee") ?? R.MELEE_ITEMS_FILTER),
            (GearCategory.Ranged, () => R.getString("ItemTag_Ranged") ?? R.RANGED_ITEMS_FILTER),
            (GearCategory.Artifacts, () => R.getString("ItemTag_Items") ?? R.ARTIFACT_ITEMS_FILTER),
            (GearCategory.Capes, () => R.CUSTOM_SKINS_CAPES),
            (GearCategory.Pets, () => R.CUSTOM_SKINS_PETS),
        };

        private void fillCategories()
        {
            categoryCombo.Items.Clear();
            foreach (var (category, label) in CATEGORIES)
            {
                categoryCombo.Items.Add(new ComboBoxItem { Content = label(), Tag = category });
            }
            categoryCombo.SelectedIndex = 0;
        }

        private GearCategory selectedCategory
            => (categoryCombo.SelectedItem as ComboBoxItem)?.Tag as GearCategory? ?? GearCategory.Armor;

        private void categoryCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            fillGearList();
        }

        private void fillGearList()
        {
            gearList.Items.Clear();
            if (!CustomSkins.ready) { return; }

            var search = searchBox.Text;

            //Capes and pets are not items and have no name in the game's text, so they carry the
            //one worked out from the folder they live in.
            if (selectedCategory == GearCategory.Capes || selectedCategory == GearCategory.Pets)
            {
                var cosmetics = selectedCategory == GearCategory.Capes
                    ? CosmeticSkins.capes()
                    : CosmeticSkins.pets();

                foreach (var entry in cosmetics)
                {
                    if (!matchesText(entry.Name, entry.Id, search)) { continue; }
                    gearList.Items.Add(new ListBoxItem {
                        Content = entry.Name,
                        Tag = entry.Id,
                        ToolTip = entry.TexturePath,
                    });
                }
                return;
            }

            var items = selectedCategory switch {
                GearCategory.Melee => ItemDatabase.meleeWeapons,
                GearCategory.Ranged => ItemDatabase.rangedWeapons,
                GearCategory.Artifacts => ItemDatabase.artifacts,
                _ => ItemDatabase.armor,
            };

            foreach (var id in items.Distinct().OrderBy(id => R.itemName(id), StringComparer.CurrentCultureIgnoreCase))
            {
                if (!matchesText(R.itemName(id), id, search)) { continue; }
                gearList.Items.Add(new ListBoxItem {
                    Content = R.itemName(id),
                    Tag = id,
                    ToolTip = id,
                });
            }
        }

        private static bool matchesText(string name, string id, string? search)
        {
            if (string.IsNullOrWhiteSpace(search)) { return true; }
            var term = search!.Trim();
            return name.IndexOf(term, StringComparison.OrdinalIgnoreCase) >= 0
                || id.IndexOf(term, StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private void searchBox_TextChanged(object sender, TextChangedEventArgs e)
        {
            searchHint.Visibility = string.IsNullOrEmpty(searchBox.Text) ? Visibility.Visible : Visibility.Collapsed;
            fillGearList();
        }

        private void gearList_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            _selectedItem = (gearList.SelectedItem as ListBoxItem)?.Tag as string;

            //A cape or a pet was found by its texture in the first place, so it carries the path
            //rather than being searched for again.
            var cosmetic = CosmeticSkins.find(_selectedItem);
            _selectedTexture = cosmetic != null ? cosmetic.TexturePath
                : _selectedItem == null ? null
                : CustomSkins.textureFor(_selectedItem);
            updateSelection();
        }

        private void updateSelection()
        {
            //Every button here needs pixels, not just a path. Gating on the decoded image rather
            //than on "a texture was named" is what keeps a button from opening a dialog that
            //only says the thing the preview already says.
            var image = _selectedTexture == null ? null : CustomSkins.preview(_selectedTexture);
            var hasTexture = image != null;
            exportButton.IsEnabled = hasTexture;
            applyButton.IsEnabled = hasTexture;
            designerButton.IsEnabled = hasTexture;
            pasteButton.IsEnabled = hasTexture;

            if (!CustomSkins.ready)
            {
                selectedLabel.Content = string.Empty;
                setPreview(null, R.CUSTOM_SKINS_NO_CONTENT);
                return;
            }
            if (_selectedItem == null)
            {
                selectedLabel.Content = string.Empty;
                setPreview(null, R.CUSTOM_SKINS_PICK_ONE);
                return;
            }

            //A cape or a pet has no name in the game's text, so R.itemName would hand back the
            //folder it lives in. The readable one worked out when it was found is used instead.
            selectedLabel.Content = CosmeticSkins.find(_selectedItem)?.Name ?? R.itemName(_selectedItem);
            setPreview(image, hasTexture ? null : R.CUSTOM_SKINS_NO_TEXTURE);
        }

        private void setPreview(System.Windows.Media.ImageSource? image, string? emptyMessage)
        {
            previewImage.Source = image;
            previewEmptyLabel.Text = emptyMessage ?? string.Empty;
            previewEmptyLabel.Visibility = emptyMessage == null ? Visibility.Collapsed : Visibility.Visible;
        }

        #endregion

        #region The round trip

        private void exportButton_Click(object sender, RoutedEventArgs e)
        {
            if (_selectedTexture == null || _selectedItem == null) { return; }
            EventLogger.logEvent("customSkinExport", new Dictionary<string, object>() { { "item", _selectedItem } });

            var dialog = new SaveFileDialog {
                FileName = System.IO.Path.GetFileName(_selectedTexture) + ".png",
                Filter = "PNG image|*.png",
                Title = R.CUSTOM_SKINS_EXPORT,
            };
            if (dialog.ShowDialog() != true) { return; }

            try
            {
                CustomSkins.exportTexture(_selectedTexture!, dialog.FileName);
                MessageBox.Show(R.formatCUSTOM_SKINS_EXPORTED(dialog.FileName), R.CUSTOM_SKINS_TAB);
            }
            catch (Exception exception)
            {
                MessageBox.Show(exception.Message, R.ERROR);
            }
        }

        /// <summary>
        /// Opens the designer with this texture already loaded.
        ///
        /// The pixels ride in the link's fragment, which never leaves the browser - it is not
        /// sent to the server, not logged and not length-limited the way a query string is. So
        /// nothing is uploaded anywhere, and there is no file to save, find and upload by hand.
        /// </summary>
        private void designerButton_Click(object sender, RoutedEventArgs e)
        {
            if (_selectedTexture == null || _selectedItem == null) { return; }
            EventLogger.logEvent("customSkinDesigner", new Dictionary<string, object>() { { "item", _selectedItem } });

            try
            {
                var image = CustomSkins.preview(_selectedTexture!)
                    ?? throw new InvalidOperationException(R.CUSTOM_SKINS_NO_TEXTURE);
                var url = SkinCodec.designerUrl(image, R.itemName(_selectedItem!));

                if (!LinkLauncher.open(url))
                {
                    //A link this long can defeat a badly registered browser. Handing it over on
                    //the clipboard beats a dead button with no explanation.
                    Clipboard.SetText(url);
                    MessageBox.Show(R.CUSTOM_SKINS_LINK_COPIED, R.CUSTOM_SKINS_TAB);
                }
            }
            catch (Exception exception)
            {
                MessageBox.Show(exception.Message, R.ERROR);
            }
        }

        /// <summary>The way back: the designer puts its result on the clipboard as the same link.</summary>
        private void pasteButton_Click(object sender, RoutedEventArgs e)
        {
            if (_selectedTexture == null || _selectedItem == null) { return; }
            EventLogger.logEvent("customSkinPaste", new Dictionary<string, object>() { { "item", _selectedItem } });

            if (!Clipboard.ContainsText())
            {
                MessageBox.Show(R.CUSTOM_SKINS_NOTHING_COPIED, R.CUSTOM_SKINS_TAB);
                return;
            }

            BitmapSource image;
            try
            {
                image = SkinCodec.fromPastedText(Clipboard.GetText());
            }
            catch (Exception)
            {
                //Anything at all could be on the clipboard; that is not an error worth a stack
                //trace, just the wrong thing copied.
                MessageBox.Show(R.CUSTOM_SKINS_NOTHING_COPIED, R.CUSTOM_SKINS_TAB);
                return;
            }

            install(image);
        }

        private void applyButton_Click(object sender, RoutedEventArgs e)
        {
            if (_selectedTexture == null || _selectedItem == null) { return; }
            EventLogger.logEvent("customSkinApply", new Dictionary<string, object>() { { "item", _selectedItem } });

            var dialog = new OpenFileDialog {
                Filter = "PNG image|*.png",
                Title = R.CUSTOM_SKINS_APPLY,
            };
            if (dialog.ShowDialog() != true) { return; }

            try
            {
                install(CustomSkins.apply(_selectedTexture!, dialog.FileName, _selectedItem!));
            }
            catch (Exception exception)
            {
                MessageBox.Show(exception.Message, R.ERROR);
            }
        }

        private void install(BitmapSource image)
        {
            try
            {
                install(CustomSkins.apply(_selectedTexture!, image, _selectedItem!));
            }
            catch (Exception exception)
            {
                //Writing into the game's folder can fail for reasons worth reading: the game is
                //running, or the install needs elevation.
                MessageBox.Show(exception.Message, R.ERROR);
            }
        }

        private void install(CustomSkins.InstalledMod mod)
        {
            fillInstalled();
            updateSelection();
            MessageBox.Show(R.formatCUSTOM_SKINS_APPLIED(mod.Name), R.CUSTOM_SKINS_TAB);
        }

        #endregion


        #region Show armours

        //One switch backed by one pak, shown on two screens. Each listens so they cannot
        //disagree: flipping it here has to tick the box over there too.
        private bool _settingArmourBox;

        private void refreshArmourBox()
        {
            _settingArmourBox = true;
            showArmourCheckBox.IsEnabled = CustomSkins.ready;
            showArmourCheckBox.IsChecked = !ArmourVisibility.armourHidden;
            showArmourHintLabel.Text = ArmourVisibility.armourHidden
                ? R.ARMOUR_HIDDEN_HINT
                : R.ARMOUR_SHOWN_HINT;
            _settingArmourBox = false;
        }

        private void showArmourCheckBox_Changed(object sender, RoutedEventArgs e)
        {
            if (_settingArmourBox) { return; }

            var show = showArmourCheckBox.IsChecked == true;
            EventLogger.logEvent("showArmour", new Dictionary<string, object>() { { "show", show } });
            try
            {
                ArmourVisibility.setHidden(!show);
                //Directly, as well as through the event. The event is what keeps the other screen
                //in step; the screen being clicked should not need it to describe its own state.
                refreshArmourBox();
            }
            catch (Exception exception)
            {
                //Writing into the game's folder can fail: the game is running, or it needs
                //elevation. Put the box back rather than leave it lying about the state.
                MessageBox.Show(exception.Message, R.ERROR);
                refreshArmourBox();
            }
        }

        #endregion

        #region Mods brought from elsewhere

        /// <summary>
        /// Installs a pak this app did not make.
        ///
        /// Everything about modding this game is one folder - "~mods" beside the game's own
        /// paks - and the two things people get wrong are dropping the tilde and not making the
        /// folder at all. Doing it here removes both.
        /// </summary>
        private void addPakButton_Click(object sender, RoutedEventArgs e)
        {
            EventLogger.logEvent("customSkinAddPak");

            var dialog = new OpenFileDialog {
                Filter = "Unreal pak|*.pak",
                Title = R.CUSTOM_SKINS_ADD_PAK,
                Multiselect = true,
            };
            if (dialog.ShowDialog() != true) { return; }

            var added = new List<string>();
            foreach (var file in dialog.FileNames)
            {
                try
                {
                    added.Add(install(file, overwrite: false).Name);
                }
                catch (IOException)
                {
                    //Already there. Worth asking rather than either silently replacing someone's
                    //mod or refusing to update one.
                    var name = System.IO.Path.GetFileName(file);
                    var answer = MessageBox.Show(
                        R.formatCUSTOM_SKINS_PAK_REPLACE(name), R.CUSTOM_SKINS_TAB, MessageBoxButton.YesNo);
                    if (answer != MessageBoxResult.Yes) { continue; }
                    try { added.Add(install(file, overwrite: true).Name); }
                    catch (Exception retry) { MessageBox.Show(retry.Message, R.ERROR); }
                }
                catch (Exception exception)
                {
                    //Not a pak, the game holding the folder open, or an install needing elevation.
                    MessageBox.Show(exception.Message, R.ERROR);
                }
            }

            fillInstalled();
            if (added.Count > 0)
            {
                MessageBox.Show(R.formatCUSTOM_SKINS_PAK_ADDED(string.Join(", ", added)), R.CUSTOM_SKINS_TAB);
            }
        }

        private static CustomSkins.InstalledMod install(string file, bool overwrite)
            => CustomSkins.installPak(file, overwrite);

        /// <summary>Opens ~mods in Explorer, making it first if it is not there.</summary>
        private void openModsButton_Click(object sender, RoutedEventArgs e)
        {
            EventLogger.logEvent("customSkinOpenMods");
            try
            {
                var folder = CustomSkins.ensureModsFolder();
                if (!LinkLauncher.open(folder))
                {
                    MessageBox.Show(folder, R.CUSTOM_SKINS_OPEN_MODS);
                }
            }
            catch (Exception exception)
            {
                MessageBox.Show(exception.Message, R.ERROR);
            }
        }

        #endregion

        #region Installed

        private void fillInstalled()
        {
            installedStack.Children.Clear();

            //These two need the paks folder and nothing else - not a save file, not a piece of
            //gear picked - so they follow the content rather than the selection.
            addPakButton.IsEnabled = CustomSkins.ready;
            openModsButton.IsEnabled = CustomSkins.ready;

            if (!CustomSkins.ready) { installedCountLabel.Text = string.Empty; return; }

            //Anything this app drives from a checkbox is left out: it is managed there, and a
            //Remove button beside it would just be a second, contradictory control.
            var mods = CustomSkins.installed().Where(mod => !mod.Internal).ToList();
            installedCountLabel.Text = mods.Count.ToString();

            if (mods.Count == 0)
            {
                installedStack.Children.Add(new TextBlock {
                    Text = R.CUSTOM_SKINS_NONE_INSTALLED,
                    Margin = new Thickness(8, 8, 8, 8),
                    FontSize = 12,
                    TextWrapping = TextWrapping.Wrap,
                    Foreground = (System.Windows.Media.Brush)FindResource("Brush.TextDisabled"),
                });
                return;
            }

            int index = 0;
            foreach (var mod in mods)
            {
                installedStack.Children.Add(createInstalledRow(mod, index++));
            }
        }

        private FrameworkElement createInstalledRow(CustomSkins.InstalledMod mod, int index)
        {
            var name = new TextBlock {
                //A skin made here is named after the item id, because that is stable and does not
                //move with the Language menu - two names for one armour would mean two paks both
                //replacing it. The list shows the readable name instead; an id it does not know
                //comes back unchanged, so a manual pak's filename survives this untouched.
                Text = R.itemName(mod.Name),
                VerticalAlignment = VerticalAlignment.Center,
                FontSize = 13,
                TextTrimming = TextTrimming.CharacterEllipsis,
                ToolTip = mod.Path,
            };

            var detail = new TextBlock {
                //A pak someone else made is marked, because Remove deletes it and the user should
                //know which of these this app is responsible for.
                Text = mod.Manual
                    ? $"{R.CUSTOM_SKINS_MANUAL_TAG} · {mod.Size / 1024:N0} KB · {mod.Installed:g}"
                    : $"{mod.Size / 1024:N0} KB · {mod.Installed:g}",
                VerticalAlignment = VerticalAlignment.Center,
                HorizontalAlignment = HorizontalAlignment.Right,
                Margin = new Thickness(0, 0, 8, 0),
                FontSize = 12,
            };
            detail.SetResourceReference(ForegroundProperty, "Brush.TextMuted");

            var remove = new Button { Content = R.CUSTOM_SKINS_REMOVE, Padding = new Thickness(8, 2, 8, 2) };
            remove.Click += (s, e) => removeMod(mod);

            var grid = new Grid { Height = 34 };
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            Grid.SetColumn(name, 0);
            Grid.SetColumn(detail, 1);
            Grid.SetColumn(remove, 2);
            grid.Children.Add(name);
            grid.Children.Add(detail);
            grid.Children.Add(remove);

            var row = new Border { Child = grid };
            row.SetResourceReference(StyleProperty, index % 2 == 0 ? "StatRowEven" : "StatRowOdd");
            return row;
        }

        private void removeMod(CustomSkins.InstalledMod mod)
        {
            EventLogger.logEvent("customSkinRemove");
            try
            {
                CustomSkins.remove(mod);
                fillInstalled();
            }
            catch (Exception exception)
            {
                MessageBox.Show(exception.Message, R.ERROR);
            }
        }

        #endregion
    }
}
