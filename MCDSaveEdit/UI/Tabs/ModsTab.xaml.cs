using MCDSaveEdit.Logic;
using MCDSaveEdit.Services;
using Microsoft.Win32;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
#nullable enable

namespace MCDSaveEdit.UI
{
    /// <summary>
    /// Every mod pak installed beside the game, and the two ways of getting one there.
    ///
    /// This used to live in a corner of the recolouring tab, which is where it was born and not
    /// where it belongs: by now four different parts of this app write paks - recoloured gear,
    /// reshaped weapons, imported models, replaced creatures - and the list of what is installed
    /// is about all of them rather than about any one. Somebody wanting to remove a mount should
    /// not have to go looking under gear recolouring to find it.
    ///
    /// It is the last tab on purpose, because that is where a list you go to occasionally rather
    /// than work in belongs, and because it is easy to find at the end.
    ///
    /// Nothing here is subtle: the folder is `~mods` beside the game's own paks, removing a pak is
    /// the whole of undo, and a pak somebody else made is marked as theirs because Remove deletes
    /// it from disk.
    /// </summary>
    public partial class ModsTab : UserControl
    {
        public ModsTab()
        {
            InitializeComponent();
            translateStaticStrings();

            //Filled whenever it is looked at rather than kept in step with the tabs that write
            //paks. Four of them do now, and a list that refreshes on being opened cannot be out of
            //date, where one told about each new pak can miss the one nobody remembered to wire up.
            IsVisibleChanged += (s, e) => { if (IsVisible) { updateUI(); } };
        }

        private void translateStaticStrings()
        {
            installedLabel.Content = R.CUSTOM_SKINS_INSTALLED;
            modsNoteLabel.Text = R.CUSTOM_SKINS_MODS_NOTE;
            importButton.Content = R.MODS_IMPORT;
            openFolderButton.Content = R.MODS_OPEN_FOLDER;
        }

        public void updateUI() => fillInstalled();

        #region The folder

        /// <summary>
        /// Installs a pak this app did not make.
        ///
        /// Everything about modding this game is one folder - "~mods" beside the game's own
        /// paks - and the two things people get wrong are dropping the tilde and not making the
        /// folder at all. Doing it here removes both.
        /// </summary>
        private void importButton_Click(object sender, RoutedEventArgs e)
        {
            EventLogger.logEvent("modsImportPak");

            var dialog = new OpenFileDialog {
                Filter = "Unreal pak|*.pak",
                Title = R.MODS_IMPORT,
                Multiselect = true,
            };
            if (dialog.ShowDialog() != true) { return; }

            var added = new List<string>();
            foreach (var file in dialog.FileNames)
            {
                try
                {
                    added.Add(CustomSkins.installPak(file, overwrite: false).Name);
                }
                catch (IOException)
                {
                    //Already there. Worth asking rather than either silently replacing someone's
                    //mod or refusing to update one.
                    var name = System.IO.Path.GetFileName(file);
                    var answer = MessageBox.Show(
                        R.formatCUSTOM_SKINS_PAK_REPLACE(name), R.MODS_TAB, MessageBoxButton.YesNo);
                    if (answer != MessageBoxResult.Yes) { continue; }
                    try { added.Add(CustomSkins.installPak(file, overwrite: true).Name); }
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
                MessageBox.Show(R.formatCUSTOM_SKINS_PAK_ADDED(string.Join(", ", added)), R.MODS_TAB);
            }
        }

        /// <summary>Opens ~mods in Explorer, making it first if it is not there.</summary>
        private void openFolderButton_Click(object sender, RoutedEventArgs e)
        {
            EventLogger.logEvent("modsOpenFolder");
            try
            {
                var folder = CustomSkins.ensureModsFolder();
                if (!LinkLauncher.open(folder))
                {
                    MessageBox.Show(folder, R.MODS_OPEN_FOLDER);
                }
            }
            catch (Exception exception)
            {
                MessageBox.Show(exception.Message, R.ERROR);
            }
        }

        #endregion

        #region The list

        private void fillInstalled()
        {
            if (!IsInitialized) { return; }

            installedStack.Children.Clear();

            //These two need the paks folder and nothing else - not a save file, not a piece of
            //gear picked - so they follow the content rather than any selection.
            importButton.IsEnabled = CustomSkins.ready;
            openFolderButton.IsEnabled = CustomSkins.ready;

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
            EventLogger.logEvent("modsRemove");
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
