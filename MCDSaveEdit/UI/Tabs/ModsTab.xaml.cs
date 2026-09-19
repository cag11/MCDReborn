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
            installPayloadButton.Content = R.MODS_INSTALL_PAYLOAD;
            installPayloadButton.ToolTip = R.MODS_INSTALL_PAYLOAD_WHY;
            installPayloadFolderButton.Content = R.MODS_INSTALL_PAYLOAD_FOLDER;
            installPayloadFolderButton.ToolTip = R.MODS_INSTALL_PAYLOAD_FOLDER_WHY;
            gameAssetsButton.Content = R.MODS_GAME_ASSETS;
            gameAssetsButton.ToolTip = R.MODS_GAME_ASSETS_WHY;
            installLoaderButton.Content = R.MODS_INSTALL_LOADER;
            installLoaderButton.ToolTip = R.MODS_INSTALL_LOADER_WHY;

            if (payloadTrigger.Items.Count == 0)
            {
                foreach (var trigger in Logic.Payloads.TRIGGERS)
                {
                    payloadTrigger.Items.Add(new ComboBoxItem { Content = trigger, Tag = trigger });
                }
                //Menu, and not because it is first in the list.
                //
                //A trigger is not really a preference about where something appears, it is a
                //lifetime. The main menu is a rendered camp scene, so a payload loaded there is
                //created before anything else and outlives every level after it - which is why
                //the community's own overlay mod ships as a Menu payload and is visible for the
                //whole session, in the Camp and in missions alike.
                //
                //Lobby and Ingame are for the narrower case of something that should exist only
                //at that moment. Anything meant to be there throughout belongs here.
                payloadTrigger.SelectedIndex = 0;   //Menu
            }
            modsNoteLabel.Text = R.CUSTOM_SKINS_MODS_NOTE;
            importButton.Content = R.MODS_IMPORT;
            openFolderButton.Content = R.MODS_OPEN_FOLDER;
            importZipButton.Content = R.MODS_IMPORT_ZIP;
            exportZipButton.Content = R.MODS_EXPORT_ZIP;
            importZipButton.ToolTip = R.MODS_IMPORT_ZIP_WHY;
            exportZipButton.ToolTip = R.MODS_EXPORT_ZIP_WHY;
        }

        public void updateUI() => fillInstalled();

        #region The folder

        /// <summary>
        /// Whether the game being open should stop this, said rather than discovered.
        ///
        /// Everything on this tab ends in a file written into the game's own paks folder, and a
        /// running game makes that either impossible or pointless - it holds the paks open, and
        /// it read that folder once at startup and never looks again. The first shows up as a
        /// file-sharing exception from several layers down; the second shows up as nothing
        /// whatsoever, which is the same thing a broken mod looks like.
        ///
        /// A refusal, not a question. There is no version of this that works with the game up, so
        /// offering to try anyway would only be offering to waste somebody's evening.
        /// </summary>
        private bool gameIsInTheWay()
        {
            if (!Logic.GameRunning.isUp) { return false; }

            statusLabel.Text = R.MODS_GAME_RUNNING;
            MessageBox.Show(R.MODS_GAME_RUNNING, R.MODS_TAB);
            return true;
        }

        /// <summary>
        /// Whether to go ahead with a payload when there is no loader to run it.
        ///
        /// Asked rather than refused, because installing payloads before the loader is a perfectly
        /// reasonable order to do things in, and because the note above the list says the same
        /// thing already. What it prevents is the case that keeps happening: a payload installed,
        /// a game started, nothing there, and no way to tell that from a payload that is wrong.
        /// </summary>
        private bool payloadWithoutLoaderRefused()
        {
            if (Logic.Loader.isInstalled) { return false; }

            return MessageBox.Show(R.MODS_NO_LOADER_ASK, R.MODS_TAB, MessageBoxButton.YesNo)
                != MessageBoxResult.Yes;
        }

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
            if (gameIsInTheWay()) { return; }

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

        /// <summary>
        /// Installs every mod an archive holds, however it was put together.
        ///
        /// One file is the whole point: somebody zips their mods folder, sends it, and the other
        /// end gets all of it in one go rather than a folder of paks and instructions about where
        /// to drop them.
        /// </summary>
        private void importZipButton_Click(object sender, RoutedEventArgs e)
        {
            EventLogger.logEvent("modsImportArchive");
            if (gameIsInTheWay()) { return; }

            var dialog = new OpenFileDialog {
                Filter = ModArchive.READ_FILTER,
                Title = R.MODS_IMPORT_ZIP,
            };
            if (dialog.ShowDialog() != true) { return; }

            try
            {
                //The question about replacing is asked here rather than inside, because this is
                //the half of the program with a window to ask it in.
                var haul = ModArchive.install(dialog.FileName, name =>
                    MessageBox.Show(R.formatCUSTOM_SKINS_PAK_REPLACE(name), R.MODS_TAB,
                        MessageBoxButton.YesNo) == MessageBoxResult.Yes);

                fillInstalled();
                MessageBox.Show(describe(haul), R.MODS_TAB);
            }
            catch (Exception exception)
            {
                //Not an archive, an archive this cannot read, or the game holding the folder open.
                MessageBox.Show(exception.Message, R.ERROR);
            }
        }

        /// <summary>
        /// What came of an archive, said in full.
        ///
        /// Every count rather than just the good one. "Four installed" hides that two were
        /// skipped, and somebody who gets four of six mods and is told about four will spend the
        /// evening wondering why the other two do nothing.
        /// </summary>
        private static string describe(ModArchive.Haul haul)
        {
            var said = new List<string>();

            if (haul.Installed.Count > 0) { said.Add(string.Format(R.MODS_ZIP_INSTALLED, haul.Installed.Count)); }
            if (haul.Replaced.Count > 0) { said.Add(string.Format(R.MODS_ZIP_REPLACED, haul.Replaced.Count)); }
            if (haul.Skipped.Count > 0) { said.Add(string.Format(R.MODS_ZIP_SKIPPED, haul.Skipped.Count)); }
            if (haul.Rejected.Count > 0)
            {
                said.Add(string.Format(R.MODS_ZIP_REJECTED, string.Join(", ", haul.Rejected)));
            }
            if (haul.Ignored > 0) { said.Add(string.Format(R.MODS_ZIP_IGNORED, haul.Ignored)); }

            return said.Count == 0 ? R.MODS_ZIP_NOTHING : string.Join(Environment.NewLine, said);
        }

        /// <summary>Every installed mod in one zip, for sending somebody.</summary>
        private void exportZipButton_Click(object sender, RoutedEventArgs e)
        {
            EventLogger.logEvent("modsExportArchive");

            var dialog = new SaveFileDialog {
                Filter = ModArchive.WRITE_FILTER,
                Title = R.MODS_EXPORT_ZIP,
                FileName = "MCDReborn mods.zip",
                AddExtension = true,
                DefaultExt = "zip",
            };
            if (dialog.ShowDialog() != true) { return; }

            try
            {
                var many = ModArchive.writeAll(dialog.FileName);
                MessageBox.Show(string.Format(R.MODS_ZIP_EXPORTED, many, dialog.FileName), R.MODS_TAB);
            }
            catch (Exception exception)
            {
                MessageBox.Show(exception.Message, R.ERROR);
            }
        }

        /// <summary>Opens ~mods in Explorer, making it first if it is not there.</summary>

        /// <summary>
        /// Installs something cooked in an editor into the folder a loader watches.
        ///
        /// This is the one thing in the app that cannot be done from the app: a level with a
        /// blueprint in it is authored in Unreal, and what happens here is only the moving of it.
        /// So the dialogue asks for a .uasset and nothing else - whoever has one knows what it is,
        /// and whoever does not is not helped by a longer explanation on a button.
        /// </summary>
        private void installPayloadButton_Click(object sender, RoutedEventArgs e)
        {
            if (gameIsInTheWay()) { return; }
            if (payloadWithoutLoaderRefused()) { return; }

            var trigger = (payloadTrigger.SelectedItem as ComboBoxItem)?.Tag as string
                ?? Logic.Payloads.TRIGGERS[0];

            var picker = new OpenFileDialog {
                Title = R.MODS_INSTALL_PAYLOAD,
                Filter = "Cooked level or asset (*.umap;*.uasset)|*.umap;*.uasset",
                CheckFileExists = true,
            };
            if (picker.ShowDialog() != true) { return; }

            try
            {
                var name = System.IO.Path.GetFileNameWithoutExtension(picker.FileName);
                var mod = Logic.Payloads.install(trigger, picker.FileName, name);
                statusLabel.Text = string.Format(R.MODS_PAYLOAD_INSTALLED,
                    System.IO.Path.GetFileName(mod.Path), trigger);
                updateUI();
            }
            catch (Exception problem)
            {
                //Said in the line rather than thrown at a dialog. Most of the ways this fails are
                //information - cooked at too long a path, missing its .uexp - rather than faults.
                statusLabel.Text = problem.Message;
            }
        }

        /// <summary>
        /// Installs a whole cooked tree, which is what a payload past the simplest one is.
        ///
        /// A level records what is placed and where; the things placed are a tree of their own
        /// beside it. Every content mod read while building this is shaped that way - one ships a
        /// three kilobyte level naming thirty four classes that live in a hundred and forty one
        /// files elsewhere - and installing only the level gives a map that loads with nothing in
        /// it, which looks exactly like the loader being broken.
        /// </summary>
        private void installPayloadFolderButton_Click(object sender, RoutedEventArgs e)
        {
            EventLogger.logEvent("modsInstallPayloadFolder");
            if (gameIsInTheWay()) { return; }
            if (payloadWithoutLoaderRefused()) { return; }

            var trigger = (payloadTrigger.SelectedItem as ComboBoxItem)?.Tag as string
                ?? Logic.Payloads.TRIGGERS[0];

            var picker = new OpenFolderDialog { Title = R.MODS_PAYLOAD_FOLDER_PICK };
            if (picker.ShowDialog() != true) { return; }

            try
            {
                var name = System.IO.Path.GetFileName(picker.FolderName.TrimEnd(
                    System.IO.Path.DirectorySeparatorChar, System.IO.Path.AltDirectorySeparatorChar));

                var mod = Logic.Payloads.installFolder(trigger, picker.FolderName, name);

                //The file count rather than just the name: the whole point of this button over the
                //other one is that it carried more than one thing, and a number is how somebody
                //checks it carried what they expected.
                statusLabel.Text = string.Format(R.MODS_PAYLOAD_INSTALLED_MANY,
                    System.IO.Path.GetFileName(mod.Path), mod.Size / 1024 + " KB", trigger);

                //Anything left behind is said in the same breath as the success, because a mod
                //with a hole in it looks exactly like a mod that worked until it is played.
                if (Logic.Payloads.Skipped.Count > 0)
                {
                    statusLabel.Text += " " + string.Format(R.MODS_PAYLOAD_LEFT_OUT,
                        Logic.Payloads.Skipped.Count,
                        string.Join(", ", Logic.Payloads.Skipped.Take(4)));
                }
                updateUI();
            }
            catch (Exception problem)
            {
                //Said in the line rather than thrown at a dialog. Most of the ways this fails are
                //information - no level in the folder, two levels and no way to tell which runs -
                //rather than faults, and they are long enough to want reading rather than dismissing.
                statusLabel.Text = problem.Message;
            }
        }

        /// <summary>
        /// Opens the list of everything in the game, so a path can be copied out of it.
        ///
        /// Here because this is the modding tab and that is what it is for, though the window it
        /// opens is not about the mods folder at all - it is for the other half of the work, the
        /// half that happens in Unreal.
        /// </summary>
        private void gameAssetsButton_Click(object sender, RoutedEventArgs e)
        {
            EventLogger.logEvent("modsGameAssets");

            var window = new GameAssetsWindow { Owner = Window.GetWindow(this) };
            window.Show();
        }

        /// <summary>
        /// Installs the loader, without which no payload ever runs.
        ///
        /// Both halves of it are authored in Unreal, so this takes the folder they were cooked
        /// into. It is deliberately a separate button from the payload ones: a payload is
        /// something somebody makes often, and the loader is a thing installed once and then
        /// forgotten about until it is missing.
        /// </summary>
        private void installLoaderButton_Click(object sender, RoutedEventArgs e)
        {
            EventLogger.logEvent("modsInstallLoader");
            if (gameIsInTheWay()) { return; }

            //Already there is a question rather than a refusal: reinstalling is how a rebuilt
            //loader gets in, and that is the normal thing to be doing while making one.
            if (Logic.Loader.isInstalled)
            {
                var answer = MessageBox.Show(R.MODS_LOADER_REPLACE, R.MODS_TAB, MessageBoxButton.YesNo);
                if (answer != MessageBoxResult.Yes) { return; }
                try { Logic.Loader.remove(); }
                catch (Exception problem) { statusLabel.Text = problem.Message; return; }
            }

            try
            {
                //The one this app carries, rather than a folder to point at. Authoring the loader
                //needs Unreal; installing it should not, and that is the entire reason the cooked
                //bytes are built into the exe.
                var mod = Logic.Loader.installBuiltIn();
                statusLabel.Text = string.Format(R.MODS_LOADER_INSTALLED,
                    System.IO.Path.GetFileName(mod.Path));

                //Another mod on the same anchor means one of the two never starts, and which
                //one is decided by the alphabet. Worth saying at the moment of installing.
                var clashes = Logic.Loader.clashes();
                if (clashes.Count > 0)
                {
                    statusLabel.Text += " " + string.Format(R.MODS_LOADER_CLASH,
                        string.Join(", ", clashes.Take(3)));
                }

                //Stubs are expected here rather than exceptional, so this reads as confirmation
                //rather than as a warning.
                if (Logic.Loader.HeldBack.Count > 0)
                {
                    statusLabel.Text += " " + string.Format(R.MODS_LOADER_HELD_BACK,
                        Logic.Loader.HeldBack.Count);
                }
                updateUI();
            }
            catch (Exception problem)
            {
                //Said in the line. Every way this fails is a sentence worth reading - the anchor
                //cooked at the wrong path, the widget missing - rather than a fault.
                statusLabel.Text = problem.Message;
            }
        }

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
            importZipButton.IsEnabled = CustomSkins.ready;
            installPayloadButton.IsEnabled = CustomSkins.ready;
            installPayloadFolderButton.IsEnabled = CustomSkins.ready;

            //The asset list is read straight out of the paks, so it needs them found and nothing
            //else - no save file, no mods folder.
            gameAssetsButton.IsEnabled = CustomSkins.ready;
            installLoaderButton.IsEnabled = CustomSkins.ready;

            //Said before anything is installed rather than after nothing happens. A payload
            //without a loader writes a perfectly good pak that no part of the game ever reads,
            //and that is indistinguishable from a broken payload unless somebody says so here.
            if (CustomSkins.ready && !Logic.Loader.isInstalled)
            {
                modsNoteLabel.Text = R.MODS_NO_LOADER;
            }
            else
            {
                modsNoteLabel.Text = R.CUSTOM_SKINS_MODS_NOTE;
            }

            if (!CustomSkins.ready) { installedCountLabel.Text = string.Empty; return; }

            //Anything this app drives from a checkbox is left out: it is managed there, and a
            //Remove button beside it would just be a second, contradictory control.
            var mods = CustomSkins.installed().Where(mod => !mod.Internal).ToList();
            installedCountLabel.Text = mods.Count.ToString();

            //Nothing to pack is not an error worth a dialog, so the button says so by being off.
            exportZipButton.IsEnabled = mods.Count > 0;

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
