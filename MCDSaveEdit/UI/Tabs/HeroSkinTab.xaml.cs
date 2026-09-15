using MCDSaveEdit.Logic;
using MCDSaveEdit.Save.Models.Enums;
using MCDSaveEdit.Services;
using MCDSaveEdit.ViewModels;
using Microsoft.Win32;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media.Imaging;
#nullable enable

namespace MCDSaveEdit.UI
{
    /// <summary>
    /// The hero, rather than the gear.
    ///
    /// Two things live here because they are the same question asked at different depths. The
    /// save records which hero skin is worn, and changing it is a save edit with no mod at all.
    /// Wearing a skin the game does not ship needs a mod, and this is the one place in the game
    /// where an ordinary Minecraft skin fits: the hero is the Minecraft shape and the hero's
    /// texture is a 64x64 sheet on the standard unwrap. Armour is sculpted geometry and a skin
    /// lands broken on it, which is why none of this touches armour textures except to stop
    /// them drawing.
    /// </summary>
    public partial class HeroSkinTab : UserControl
    {
        /// <summary>
        /// A row in the list: either one of the game's heroes or a skin the user imported.
        ///
        /// They sit together because they are the same choice - what the character looks like -
        /// and differ only in what choosing does. A hero is recorded in the save; a skin of your
        /// own has to be written as a mod, because the game has never heard of it.
        /// </summary>
        private sealed class Entry
        {
            public string Name { get; }
            public HeroSkins.Hero? Hero { get; }
            public SkinLibrary.CustomSkin? Custom { get; }

            public Entry(HeroSkins.Hero hero) { Hero = hero; Name = hero.Name; }
            public Entry(SkinLibrary.CustomSkin custom) { Custom = custom; Name = custom.Name; }

            public bool IsCustom => Custom != null;

            public BitmapSource? image()
                => Hero != null ? HeroSkins.preview(Hero) : SkinLibrary.preview(Custom!);
        }

        private Entry? _selected;
        private IReadOnlyList<Entry> _entries = Array.Empty<Entry>();

        //The hero the save named when the file was opened, so Reset has something to go back to.
        //Held against the profile it came from, or opening a second save would restore the first
        //one's hero into it.
        private object? _capturedProfile;
        private string? _originalSkin;

        private ProfileViewModel? _model;
        public ProfileViewModel? model {
            get { return _model; }
            set { _model = value; }
        }

        /// <summary>
        /// Writes the save file, through whatever the window uses for File > Save.
        ///
        /// Changing hero is a save edit, and a change that has to be remembered separately is a
        /// change waiting to be lost. Borrowing the window's own save keeps the backup that
        /// happens with it - the file is copied aside before every write - which matters more
        /// now that a write can happen without anyone asking for one.
        /// </summary>
        public Action? requestSave { get; set; }

        public HeroSkinTab()
        {
            InitializeComponent();
            translateStaticStrings();
            updateUI();
        }

        private void translateStaticStrings()
        {
            heroesLabel.Content = R.HERO_HEROES;
            ownSkinLabel.Content = R.HERO_OWN_SKIN;
            importButton.Content = R.HERO_IMPORT_SKIN;
            exportButton.Content = R.CUSTOM_SKINS_EXPORT;
            removeButton.Content = R.HERO_FORGET;
            resetButton.Content = R.HERO_RESET;
            viewResetButton.Content = R.HERO_RECENTRE;
            previewHintLabel.Text = R.HERO_DRAG_HINT;
            updateViewToggle();
            ownSkinHintLabel.Text = R.HERO_OWN_SKIN_HINT;
        }

        public void updateUI()
        {
            //Imported skins first: they are the short list, and the one someone just added is
            //what they came back to find.
            _entries = SkinLibrary.all().Select(skin => new Entry(skin))
                .Concat(HeroSkins.all().Select(hero => new Entry(hero)))
                .ToList();
            rememberOriginalHero();
            fillHeroList();
            updateSelection();
        }

        /// <summary>The hero the character actually plays as, which is what a skin has to go on.</summary>
        private HeroSkins.Hero? currentHero() => HeroSkins.forSaveValue(_model?.profile.value?.Skin);

        private void rememberOriginalHero()
        {
            var profile = _model?.profile.value;
            if (profile == null || ReferenceEquals(profile, _capturedProfile)) { return; }
            _capturedProfile = profile;
            _originalSkin = profile.Skin;
        }

        #region Heroes

        private void fillHeroList()
        {
            heroList.Items.Clear();
            if (!CustomSkins.ready) { return; }

            var search = searchBox.Text;
            ListBoxItem? landOn = null;
            var current = currentHero();

            foreach (var entry in _entries)
            {
                if (!matches(entry, search)) { continue; }
                var row = new ListBoxItem {
                    Content = entry.IsCustom ? entry.Name + "  ★" : entry.Name,
                    Tag = entry,
                    ToolTip = entry.IsCustom ? R.HERO_YOUR_SKIN : entry.Hero!.Id,
                };
                heroList.Items.Add(row);

                //Hold the selection across a refresh, so importing a skin does not throw away
                //what was being looked at. Failing that, open on whoever the character is: an
                //empty preview wastes the first look at a screen whose point is the preview.
                if (_selected != null && entry.Name == _selected.Name && entry.IsCustom == _selected.IsCustom)
                {
                    landOn = row;
                }
                else if (landOn == null && _selected == null && current != null && entry.Hero != null
                         && string.Equals(entry.Hero.Id, current.Id, StringComparison.OrdinalIgnoreCase))
                {
                    landOn = row;
                }
            }

            if (landOn != null)
            {
                heroList.SelectedItem = landOn;
                landOn.BringIntoView();
            }
        }

        private static bool matches(Entry entry, string? search)
        {
            if (string.IsNullOrWhiteSpace(search)) { return true; }
            return entry.Name.IndexOf(search!.Trim(), StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private void searchBox_TextChanged(object sender, TextChangedEventArgs e)
        {
            searchHint.Visibility = string.IsNullOrEmpty(searchBox.Text) ? Visibility.Visible : Visibility.Collapsed;
            fillHeroList();
        }

        private void heroList_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            _selected = (heroList.SelectedItem as ListBoxItem)?.Tag as Entry;
            updateSelection();
        }

        private void updateSelection()
        {
            rememberOriginalHero();

            var image = _selected?.image();
            var usable = image != null;
            var current = currentHero();

            //One button, two meanings, because it is one decision: make the character look like
            //the thing that is selected. A hero the game ships is recorded in the save; a skin of
            //your own has to be written as a mod, since the game has never heard of it.
            useButton.IsEnabled = usable && (!_selected!.IsCustom || current != null);
            useButton.Content = _selected == null || !_selected.IsCustom
                ? R.HERO_PLAY_AS
                : R.HERO_WEAR_THIS;

            exportButton.IsEnabled = usable;
            removeButton.Visibility = _selected != null && _selected.IsCustom
                ? Visibility.Visible
                : Visibility.Collapsed;

            updateCurrentLook(current);

            if (!CustomSkins.ready)
            {
                selectedLabel.Content = string.Empty;
                setPreview(null, R.CUSTOM_SKINS_NO_CONTENT);
                return;
            }
            if (_selected == null)
            {
                selectedLabel.Content = string.Empty;
                setPreview(null, R.HERO_PICK_ONE);
                return;
            }

            selectedLabel.Content = _selected.Name;
            setPreview(image, usable ? null : R.CUSTOM_SKINS_NO_TEXTURE);
        }

        /// <summary>
        /// What the character is right now: the hero the save names, its texture, and whether a
        /// skin of your own is sitting on top of it.
        ///
        /// The thumbnail is the game's own art for that hero. A skin you installed lives in a pak
        /// this app deliberately does not mount - mounting it would lock the file and stop Reset
        /// deleting it - so the line underneath says when one is in place rather than drawing it.
        /// </summary>
        private void updateCurrentLook(HeroSkins.Hero? current)
        {
            var installed = current == null ? null : HeroSkins.installedFor(current);

            //Only believed while the pak is actually there. A name left over from a skin that has
            //since been removed by hand would otherwise claim the character still wears it.
            var wornName = installed == null || current == null ? null : SkinLibrary.wornBy(current.Id);
            var wornSkin = wornName == null ? null : SkinLibrary.find(wornName);

            //What the character looks like is the skin when one is on, and the hero otherwise.
            currentLookImage.Source = wornSkin != null
                ? SkinLibrary.preview(wornSkin)
                : (current == null ? null : HeroSkins.preview(current));

            currentHeroLabel.Text = wornName != null
                ? R.formatHERO_CURRENT(wornName)
                : currentHeroDescription();

            //Nothing to add when the skin is named above. The line is only for a pak whose skin
            //cannot be named - one applied before this app recorded them, or by hand.
            currentModLabel.Text = wornName == null && installed != null
                ? R.HERO_CUSTOM_INSTALLED
                : string.Empty;

            var heroChanged = _originalSkin != null && _model?.profile.value != null
                && !string.Equals(_model.profile.value.Skin, _originalSkin, StringComparison.OrdinalIgnoreCase);
            resetButton.IsEnabled = installed != null || heroChanged || ArmourVisibility.armourHidden;
        }

        private string currentHeroDescription()
        {
            var saved = _model?.profile.value?.Skin;
            if (string.IsNullOrWhiteSpace(saved)) { return string.Empty; }
            var hero = HeroSkins.forSaveValue(saved);
            return R.formatHERO_CURRENT(hero?.Name ?? saved!);
        }

        //The figure is the point of this screen, so it is what opens. The flat sheet stays one
        //click away for anyone who wants to see the unwrap.
        private bool _showTexture;

        private void setPreview(BitmapSource? image, string? emptyMessage)
        {
            previewImage.Source = image;
            preview3D.skin = image;

            var empty = emptyMessage != null;
            previewEmptyLabel.Text = emptyMessage ?? string.Empty;
            previewEmptyLabel.Visibility = empty ? Visibility.Visible : Visibility.Collapsed;

            preview3D.Visibility = !empty && !_showTexture ? Visibility.Visible : Visibility.Collapsed;
            previewImage.Visibility = !empty && _showTexture ? Visibility.Visible : Visibility.Collapsed;
            previewHintLabel.Visibility = preview3D.Visibility;
            viewToggleButton.Visibility = empty ? Visibility.Collapsed : Visibility.Visible;
            viewResetButton.Visibility = preview3D.Visibility;
        }

        private void updateViewToggle()
            => viewToggleButton.Content = _showTexture ? R.HERO_VIEW_MODEL : R.HERO_VIEW_TEXTURE;

        private void viewToggleButton_Click(object sender, RoutedEventArgs e)
        {
            _showTexture = !_showTexture;
            updateViewToggle();
            updateSelection();
        }

        private void viewResetButton_Click(object sender, RoutedEventArgs e) => preview3D.resetView();

        #endregion

        #region Choosing a hero the game already ships

        /// <summary>
        /// Makes the character look like whatever is selected.
        ///
        /// A hero the game ships is a save edit and nothing more. A skin of your own is written
        /// onto the hero the save already names, because that is the texture the game will load.
        /// </summary>
        private void useButton_Click(object sender, RoutedEventArgs e)
        {
            if (_selected == null) { return; }

            if (_selected.IsCustom)
            {
                var image = _selected.image();
                if (image != null) { install(image, _selected.Name); }
                return;
            }

            var profile = _model?.profile.value;
            if (profile == null)
            {
                MessageBox.Show(R.HERO_NO_SAVE, R.HERO_TAB);
                return;
            }

            EventLogger.logEvent("chooseHero", new Dictionary<string, object>() { { "hero", _selected.Hero!.Id } });
            profile.Skin = _selected.Hero!.Id;
            requestSave?.Invoke();
            updateSelection();
        }

        /// <summary>
        /// Puts back what was there: the pak this tab wrote, and the hero the save named when the
        /// file was opened. Either may be absent, and the button is only offered when one is not.
        /// </summary>
        private void resetButton_Click(object sender, RoutedEventArgs e)
        {
            EventLogger.logEvent("heroReset");
            var undone = new List<string>();

            try
            {
                var current = currentHero();
                var installed = current == null ? null : HeroSkins.installedFor(current);
                if (installed != null)
                {
                    CustomSkins.remove(installed);
                    if (current != null) { SkinLibrary.forgetWorn(current.Id); }
                    undone.Add(R.HERO_RESET_SKIN);
                }
            }
            catch (Exception exception)
            {
                MessageBox.Show(exception.Message, R.ERROR);
                return;
            }

            if (ArmourVisibility.armourHidden)
            {
                try
                {
                    ArmourVisibility.setHidden(false);
                    undone.Add(R.HERO_RESET_ARMOUR);
                }
                catch (Exception exception) { MessageBox.Show(exception.Message, R.ERROR); }
            }

            var profile = _model?.profile.value;
            if (profile != null && _originalSkin != null
                && !string.Equals(profile.Skin, _originalSkin, StringComparison.OrdinalIgnoreCase))
            {
                profile.Skin = _originalSkin;
                requestSave?.Invoke();
                undone.Add(R.HERO_RESET_HERO);
            }

            updateSelection();
            status(undone.Count == 0 ? R.HERO_RESET_NOTHING : string.Join(" ", undone));
        }

        #endregion



        #region Wearing a skin of your own

        private void exportButton_Click(object sender, RoutedEventArgs e)
        {
            if (_selected == null) { return; }
            EventLogger.logEvent("heroSkinExport");

            var dialog = new SaveFileDialog {
                FileName = _selected.Name + ".png",
                Filter = "PNG image|*.png",
                Title = R.CUSTOM_SKINS_EXPORT,
            };
            if (dialog.ShowDialog() != true) { return; }

            try
            {
                //Whatever is selected, from either half of the list: a hero's own art comes out
                //of the paks, an imported one is already a file.
                if (_selected.Hero != null)
                {
                    CustomSkins.exportTexture(_selected.Hero.Asset, dialog.FileName);
                }
                else
                {
                    System.IO.File.Copy(_selected.Custom!.Path, dialog.FileName, overwrite: true);
                }
                MessageBox.Show(R.formatCUSTOM_SKINS_EXPORTED(dialog.FileName), R.HERO_TAB);
            }
            catch (Exception exception)
            {
                MessageBox.Show(exception.Message, R.ERROR);
            }
        }

        /// <summary>
        /// Keeps a PNG under its own name, so it joins the list and can be chosen again.
        ///
        /// It is not applied here. Importing and wearing are different acts: someone building a
        /// collection should not have to put each one on to keep it.
        /// </summary>
        private void importButton_Click(object sender, RoutedEventArgs e)
        {
            EventLogger.logEvent("importSkin");

            var dialog = new OpenFileDialog {
                Filter = "PNG image|*.png",
                Title = R.HERO_IMPORT_SKIN,
                Multiselect = true,
            };
            if (dialog.ShowDialog() != true) { return; }

            var added = new List<string>();
            foreach (var file in dialog.FileNames)
            {
                try { added.Add(SkinLibrary.import(file).Name); }
                catch (Exception exception) { MessageBox.Show(exception.Message, R.ERROR); }
            }

            if (added.Count == 0) { return; }

            //Land on the last one imported, which is what someone wants to look at.
            _selected = null;
            updateUI();
            selectByName(added[added.Count - 1]);
            status(R.formatHERO_IMPORTED(string.Join(", ", added)));
        }

        private void selectByName(string name)
        {
            foreach (ListBoxItem row in heroList.Items)
            {
                if (row.Tag is Entry entry && entry.IsCustom && entry.Name == name)
                {
                    heroList.SelectedItem = row;
                    row.BringIntoView();
                    return;
                }
            }
        }

        private void removeButton_Click(object sender, RoutedEventArgs e)
        {
            if (_selected?.Custom == null) { return; }
            EventLogger.logEvent("removeSkin");

            var answer = MessageBox.Show(
                R.formatHERO_FORGET_SKIN(_selected.Name), R.HERO_TAB, MessageBoxButton.YesNo);
            if (answer != MessageBoxResult.Yes) { return; }

            try
            {
                SkinLibrary.remove(_selected.Custom);
                _selected = null;
                updateUI();
            }
            catch (Exception exception)
            {
                MessageBox.Show(exception.Message, R.ERROR);
            }
        }

        private void install(BitmapSource image, string skinName)
        {
            //Onto the hero the save plays as. Patching the highlighted one instead was the bug
            //here: a skin applied to a hero you are not would change nothing in game.
            var target = currentHero();
            if (target == null)
            {
                MessageBox.Show(R.HERO_NO_SAVE, R.HERO_TAB);
                return;
            }

            try
            {
                HeroSkins.apply(target, image);
                SkinLibrary.recordWorn(target.Id, skinName);

                //Armour goes with it. Wearing a skin and then finding it hidden under gear is the
                //complaint this whole screen exists to answer, so it is not worth offering as a
                //choice here - the switch on Recolor Gear is there for putting armour back.
                if (!ArmourVisibility.armourHidden) { ArmourVisibility.setHidden(true); }

                //Named for the skin, not the pak. The file is called after the hero whose texture
                //it replaces - Hero_Elaine for a skin worn by Elaine - which is correct and means
                //nothing to the person who just picked "spiderman".
                var message = R.formatHERO_NOW_WEARING(skinName);
                status(message);
                MessageBox.Show(message, R.HERO_TAB);
            }
            catch (Exception exception)
            {
                //Writing into the game's folder can fail for reasons worth reading: the game is
                //running, or the install needs elevation.
                MessageBox.Show(exception.Message, R.ERROR);
            }
        }

        private void status(string text) => statusLabel.Text = text;

        #endregion
    }
}
