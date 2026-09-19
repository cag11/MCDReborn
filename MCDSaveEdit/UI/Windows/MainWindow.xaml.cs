using MCDSaveEdit.Data;
using MCDSaveEdit.Logic;
using MCDSaveEdit.Save.Models.Profiles;
using MCDSaveEdit.Services;
using MCDSaveEdit.ViewModels;
using Microsoft.Win32;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
#nullable enable

namespace MCDSaveEdit.UI
{
    /// <summary>
    /// Interaction logic for MainWindow.xaml
    /// </summary>
    public partial class MainWindow : Window
    {
        public Action? onRelaunch;
        public Action<string?, ProfileSaveFile?>? onReload;

        private readonly MainViewModel _model;

        private Window? _busyWindow = null;

        public MainWindow(MainViewModel model)
        {
            _model = model;
            InitializeComponent();
            translateStaticStrings();

            _model.showError = showError;
            gameFilesLocationMenuItem.Header = ImageResolver.instance.path ?? R.GAME_FILES_WINDOW_NO_CONTENT_BUTTON;
            var detectedGameVersion = _model.detectedGameVersion;
            if (detectedGameVersion == null)
            {
                gameFilesVersionMenuItem.Header = R.NO_GAME_VERSION_DETECTED;
            }
            else
            {
                gameFilesVersionMenuItem.Header = R.formatMCD_VERSION(detectedGameVersion);
            }

            buildThemeMenu();
            refreshThemeMenu(Theme.ThemeManager.current);
            Theme.ThemeManager.themeChanged += refreshThemeMenu;
            Closed += (s2, e2) => Theme.ThemeManager.themeChanged -= refreshThemeMenu;

            refreshRecentFilesList();

            createLangMenuItems();


#if HIDE_CHEST_TAB
            chestTab.Visibility = Visibility.Collapsed;
#else
            chestTab.model = _model.profileModel;
#endif

            inventoryTab.model = _model.profileModel;
            statsTab.model = _model.profileModel;
            heroTab.model = _model.profileModel;
            heroTab.requestSave = () => handleFileSaveAsync(_model.profileModel.filePath);
            towerTab.model = _model.profileModel;
            //A tower edit is a save edit with nothing to review, same as changing hero: it goes
            //to disk through the window's own save, which keeps the backup that comes with it.
            towerTab.requestSave = () => handleFileSaveAsync(_model.profileModel.filePath);
            _model.profileModel.profile.subscribe(_ => this.updateUI());

            //Clear out design/testing values
            updateUI();

            checkForNewVersionAsync();
        }

        #region UI

        public void updateUI()
        {
            updateTitleUI();
            statsTab.updateUI();
            customSkinsTab.updateUI();
            heroTab.updateUI();
            inventoryTab.updateUI();
            chestTab.updateUI();
            towerTab.updateUI();
            closeBusyIndicator();
        }

        private void updateTitleUI()
        {
            if (_model.profileModel.filePath != null)
            {
                Title = string.Format("{0} - {1}", R.APPLICATION_TITLE, Path.GetFileName(_model.profileModel.filePath));
                saveMenuItem.IsEnabled = saveAsMenuItem.IsEnabled = true;
            }
            else
            {
                Title = R.APPLICATION_TITLE;
                saveMenuItem.IsEnabled = saveAsMenuItem.IsEnabled = false;
            }
        }

        private void OnTabSelected(object sender, RoutedEventArgs e)
        {
            var tab = sender as TabItem;
            if (tab != null)
            {
                // this tab is selected!
                this._model.profileModel.mainEquipmentModel.updateEnchantmentPoints();
                this._model.profileModel.storageChestEquipmentModel.updateEnchantmentPoints();
            }
        }

        #endregion

        #region Setup

        private void refreshRecentFilesList()
        {
            recentFilesMenuItem.Items.Clear();
            foreach(var menuItem in _model.recentFilesInfos.Select(createRecentFileMenuItem))
            {
                recentFilesMenuItem.Items.Add(menuItem);
            }
            recentFilesMenuItem.IsEnabled = recentFilesMenuItem.Items.Count > 0;
        }

        private MenuItem createRecentFileMenuItem(FileInfo fileInfo)
        {
            var menuItem = new MenuItem();
            menuItem.Header = fileInfo.Name;
            menuItem.CommandParameter = fileInfo;
            menuItem.Command = new RelayCommand<FileInfo>(openRecentFileCommandBinding_Executed);
            return menuItem;
        }

        private void translateStaticStrings()
        {
            inventoryTabItem.Header = R.getString("Quickaction_inventory") ?? R.INVENTORY;
            statsTabItem.Header = R.STATS_COUNTERS;
            customSkinsTabItem.Header = R.CUSTOM_SKINS_TAB;
            weaponSkinsTabItem.Header = R.WEAPON_SKINS_TAB;
            mobSkinsTabItem.Header = R.MOB_SKINS_TAB;
            modsTabItem.Header = R.MODS_TAB;
            cameraTabItem.Header = R.CAMERA_TAB;
            enemiesTabItem.Header = R.STATS_TAB;
            heroTabItem.Header = R.HERO_TAB;
            chestTabItem.Header = R.getString("StorageChest") ?? R.CHEST;
            towerTabItem.Header = R.getString("TheTower") ?? R.THE_TOWER;
        }

        private void createLangMenuItems()
        {
            langMenuItem.Items.Clear();
            var noneMenuItem = createLangMenuItem(R.getString("rebind_none") ?? R.NONE);
            langMenuItem.Items.Add(noneMenuItem);
            langMenuItem.Items.Add(new Separator());
            foreach(var menuItem in LanguageResolver.instance.localizationOptions.Select(createLangMenuItem))
            {
                langMenuItem.Items.Add(menuItem);
            }
        }

        private MenuItem createLangMenuItem(string lang)
        {
            var specificLangMenuItem = new MenuItem();
            string header;
            try
            {
                header = CultureInfo.GetCultureInfo(lang).NativeName;
            }
            catch
            {
                header = lang;
            }
            specificLangMenuItem.Header = header;
            specificLangMenuItem.IsChecked = AppModel.currentLangSpecifier == lang;
            specificLangMenuItem.CommandParameter = lang;
            specificLangMenuItem.Command = new RelayCommand<string>(languageSelectedMenuItem_Click);
            return specificLangMenuItem;
        }

#endregion

#region Version Check

        private async void checkForNewVersionAsync()
        {
            await Config.instance.downloadAsync();
            if (Config.instance.isNewBetaVersionAvailable())
            {
                updateMenuItem.Header = R.BETA_UPDATE_MENU_ITEM_HEADER;
                updateMenuItem.Visibility = Visibility.Visible;
            }
            else if (Config.instance.isNewStableVersionAvailable())
            {
                updateMenuItem.Header = R.STABLE_UPDATE_MENU_ITEM_HEADER;
                updateMenuItem.Visibility = Visibility.Visible;
            }
            else
            {
                updateMenuItem.Visibility = Visibility.Collapsed;
            }
        }

#endregion

#region User Input Methods

#region Keyboard Captures

        protected override void OnKeyUp(KeyEventArgs e)
        {
            base.OnKeyUp(e);

            //Capture the delete key
            if (e.Key == Key.Delete)
            {
                if (inventoryTab.IsVisible && !(Keyboard.FocusedElement is TextBox))
                {
                    inventoryTab.deleteCurrentSelectedItem();
                }
                else if (chestTab.IsVisible && !(Keyboard.FocusedElement is TextBox))
                {
                    chestTab.deleteCurrentSelectedItem();
                }
            }
        }

#endregion

#region Menu Items

        private void exitCommandBinding_Executed(object sender, ExecutedRoutedEventArgs e)
        {
            EventLogger.logEvent("exitCommandBinding_Executed");
            Application.Current?.Shutdown();
        }

        private void relaunchMenuItem_Click(object sender, RoutedEventArgs e)
        {
            EventLogger.logEvent("relaunchMenuItem_Click");
            onRelaunch?.Invoke();
        }

        private void openCommandBinding_Executed(object sender, ExecutedRoutedEventArgs e)
        {
            EventLogger.logEvent("openCommandBinding_Executed");
            var openFileDialog = new OpenFileDialog();
            openFileDialog.CheckFileExists = true;
            openFileDialog.Filter = constructOpenFileDialogFilterString(ProfileViewModel.supportedFileTypesDict);
            openFileDialog.FilterIndex = 0;
            if(!string.IsNullOrWhiteSpace(_model.profileModel.filePath))
            {
                var directory = Path.GetDirectoryName(_model.profileModel.filePath!);
                openFileDialog.InitialDirectory = directory;
            }
            else
            {
                openFileDialog.InitialDirectory = Constants.FILE_DIALOG_INITIAL_DIRECTORY;
            }
            if (openFileDialog.ShowDialog() == true)
            {
                handleFileOpenAsync(openFileDialog.FileName);
            }
        }

        private void saveAsCommandBinding_Executed(object sender, ExecutedRoutedEventArgs e)
        {
            EventLogger.logEvent("saveAsCommandBinding_Executed");
            var saveFileDialog = new SaveFileDialog();
            saveFileDialog.Filter = constructOpenFileDialogFilterString(ProfileViewModel.supportedFileTypesDict);
            saveFileDialog.FilterIndex = 0;
            saveFileDialog.InitialDirectory = Path.GetDirectoryName(_model.profileModel.filePath!); //Constants.FILE_DIALOG_INITIAL_DIRECTORY;
            if (saveFileDialog.ShowDialog() == true)
            {
                handleFileSaveAsync(saveFileDialog.FileName);
            }
        }

        private void saveCommandBinding_Executed(object sender, ExecutedRoutedEventArgs e)
        {
            EventLogger.logEvent("saveCommandBinding_Executed");
            handleFileSaveAsync(_model.profileModel.filePath);
        }

        private void openRecentFileCommandBinding_Executed(FileInfo fileInfo)
        {
            handleFileOpenAsync(fileInfo.FullName);
        }

        private void languageSelectedMenuItem_Click(string langSpecifier)
        {
            EventLogger.logEvent("languageSelectedMenuItem_Click", new Dictionary<string, object> { { "langSpecifier", langSpecifier } });
            AppModel.loadLanguageStrings(langSpecifier);
            onReload?.Invoke(_model.profileModel.filePath, _model.profileModel.profile.value);
        }

        /// <summary>
        /// One tickable entry per palette, built from ThemeManager rather than the XAML, so
        /// adding a theme is a palette file and a line in ThemeManager - nothing here.
        /// </summary>
        private void buildThemeMenu()
        {
            viewMenuItem.Items.Clear();
            foreach (var theme in Theme.ThemeManager.allThemes)
            {
                var captured = theme;
                var entry = new MenuItem {
                    Header = theme.ToString(),
                    IsCheckable = true,
                    IsChecked = theme == Theme.ThemeManager.current,
                    Tag = theme,
                    //Registered below so these behave like the menu items declared in XAML.
                    Name = $"theme{theme}MenuItem",
                };
                entry.Click += (s, e) => {
                    EventLogger.logEvent("themeMenuItem_Click");
                    Theme.ThemeManager.apply(captured);
                };
                viewMenuItem.Items.Add(entry);
                if (FindName(entry.Name) == null) { RegisterName(entry.Name, entry); }
            }
        }

        /// <summary>Keeps exactly one tick against the active theme.</summary>
        private void refreshThemeMenu(Theme.AppTheme theme)
        {
            foreach (var item in viewMenuItem.Items)
            {
                if (item is MenuItem entry && entry.Tag is Theme.AppTheme tagged)
                {
                    entry.IsChecked = tagged == theme;
                }
            }
        }

        #region MCD Builder

        private IEnumerable<Item>? equippedItems()
            => _model.profileModel.mainEquipmentModel.equippedItemList.value;

        /// <summary>
        /// Imported gear takes the power of the character's strongest equipped item. The
        /// share format carries no power or rarity, so without this a shared build would
        /// arrive at power 0 and be unusable.
        /// </summary>
        private double powerForImportedItems()
        {
            var equipped = equippedItems();
            if (equipped == null) { return 0; }
            var powers = equipped.Select(x => x.Power).ToList();
            return powers.Count > 0 ? powers.Max() : 0;
        }

        private bool requireProfile()
        {
            if (_model.profileModel.profile.value != null) { return true; }
            showError("Open a save file first.");
            return false;
        }

        private void openInBuilderMenuItem_Click(object sender, RoutedEventArgs e)
        {
            if (!requireProfile()) { return; }
            EventLogger.logEvent("openInBuilderMenuItem_Click");
            LinkLauncher.open(BuildShare.toShareUrl(equippedItems()));
        }

        private void copyBuildJsonMenuItem_Click(object sender, RoutedEventArgs e)
        {
            if (!requireProfile()) { return; }
            EventLogger.logEvent("copyBuildJsonMenuItem_Click");
            try { Clipboard.SetText(BuildShare.toJson(equippedItems())); }
            catch (Exception exception) { showError(exception.Message); }
        }

        private void importBuildMenuItem_Click(object sender, RoutedEventArgs e)
            => importBuild("importBuildMenuItem_Click", equip: false);

        private void importEquipBuildMenuItem_Click(object sender, RoutedEventArgs e)
            => importBuild("importEquipBuildMenuItem_Click", equip: true);

        /// <summary>
        /// Reads a build off the clipboard and either drops it in the inventory or wears it.
        ///
        /// The space check runs BEFORE anything is added and refuses the whole import if it
        /// will not fit - a half-applied build is worse than none. Equipping needs room only
        /// for the gear it displaces, so an empty slot costs nothing and the two paths count
        /// different things.
        /// </summary>
        private void importBuild(string eventId, bool equip)
        {
            if (!requireProfile()) { return; }
            EventLogger.logEvent(eventId);

            string clipboard;
            try { clipboard = Clipboard.GetText(); }
            catch (Exception exception) { showError(exception.Message); return; }

            var items = BuildShare.itemsFromShare(clipboard, powerForImportedItems());
            if (items == null)
            {
                showError("The clipboard does not contain an MCD Builder share link or build JSON.");
                return;
            }
            if (items.Count == 0)
            {
                showError("That build is empty, or none of its items are in the loaded game content.");
                return;
            }

            var model = _model.profileModel.mainEquipmentModel;
            var equipped = equippedItems()?.ToList() ?? new List<Item>();

            int needed = equip
                ? items.Count(x => equipped.Any(y => y.EquipmentSlot == x.EquipmentSlot))
                : items.Count;
            int free = Constants.MAXIMUM_INVENTORY_ITEM_COUNT - model.totalItemCount;

            if (needed > free)
            {
                showError($"Not enough inventory space: this build needs {needed} free slot(s) and only {free} are available.");
                return;
            }

            foreach (var item in items)
            {
                if (equip)
                {
                    //Unequip the current occupant so it lands in the inventory rather than
                    //leaving two items claiming the same slot.
                    var occupant = equipped.FirstOrDefault(x => x.EquipmentSlot == item.EquipmentSlot);
                    if (occupant != null) { model.unequipItem(occupant); }
                }
                else
                {
                    item.EquipmentSlot = null;
                }
                model.addItemToList(item);
            }

            //Nudge the list so the grid and the equipped slots both redraw.
            model.filter.setValue = model.filter.value;
        }

        #endregion

        private void aboutMenuItem_Click(object sender, RoutedEventArgs e)
        {
            EventLogger.logEvent("aboutMenuItem_Click");
            var aboutWindow = WindowFactory.createAboutWindow();
            aboutWindow.ShowDialog();
        }

        private void updateMenuItem_Click(object sender, RoutedEventArgs e)
        {
            EventLogger.logEvent("updateMenuItem_Click");
            //Process.Start(url) throws on .NET 5+ without UseShellExecute; see LinkLauncher.
            LinkLauncher.open(Config.instance.newVersionDownloadURL());
        }

#endregion

#region File Drop Capture

        private void window_File_Drop(object sender, DragEventArgs e)
        {
            EventLogger.logEvent("window_File_Drop");
            if (e.Data.GetDataPresent(DataFormats.FileDrop))
            {
                // Note that you can have more than one file.
                string[] files = (string[])e.Data.GetData(DataFormats.FileDrop);

                // Assuming you have one file that you care about, pass it off to whatever
                // handling code you have defined.
                handleFileOpenAsync(files[0]);
            }
            else
            {
                showError(R.FILE_DROP_ERROR_MESSAGE);
            }
        }

#endregion

#endregion

#region Helper Functions

        private string constructOpenFileDialogFilterString(Dictionary<string, string> dict)
        {
            return string.Join("|", dict.Select(x => string.Join("|", string.Format("{0} ({1})", x.Value, x.Key), x.Key)));
        }

        public async void handleFileOpenAsync(string? fileName)
        {
            if(string.IsNullOrWhiteSpace(fileName)) { return; }
            if (!File.Exists(fileName))
            {
                showError(R.FILE_DOESNT_EXIST_ERROR_MESSAGE);
                return;
            }
            showBusyIndicator();
            string extension = Path.GetExtension(fileName!);
            EventLogger.logEvent("handleFileOpenAsync", new Dictionary<string, object>() { { "extension", extension } });
            await _model.handleFileOpenAsync(fileName!);
            updateTitleUI();
            refreshRecentFilesList();
            closeBusyIndicator();
        }

        private async void handleFileSaveAsync(string? fileName)
        {
            if (_model.profileModel.profile.value == null || string.IsNullOrWhiteSpace(fileName)) { return; }
            showBusyIndicator();
            string extension = Path.GetExtension(fileName!);
            EventLogger.logEvent("handleFileSaveAsync", new Dictionary<string, object>() { { "extension", extension } });
            await _model.handleFileSaveAsync(fileName!, _model.profileModel.profile.value!);
            updateTitleUI();
            refreshRecentFilesList();
            closeBusyIndicator();
        }
        
        private void showBusyIndicator()
        {
            closeBusyIndicator();

            _busyWindow = WindowFactory.createBusyWindow();
            _busyWindow.Owner = this;
            _busyWindow.Show();
        }

        private void closeBusyIndicator()
        {
            if (_busyWindow != null)
            {
                _busyWindow!.Close();
                _busyWindow = null;
            }
        }

        private void showError(string message)
        {
            EventLogger.logEvent("showError", new Dictionary<string, object>() { { "message", message } });
            MessageBox.Show(message, R.ERROR);
            closeBusyIndicator();
        }

#endregion
    }
}
