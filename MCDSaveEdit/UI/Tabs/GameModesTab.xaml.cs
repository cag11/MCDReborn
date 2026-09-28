using MCDSaveEdit.Logic;
using MCDSaveEdit.Services;
using System;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
#nullable enable

namespace MCDSaveEdit.UI
{
    /// <summary>
    /// Switches that change the game itself rather than a save: gems and sockets, the talent tree, and Apocalypse+
    /// past +25. Each is installed beside the game - the item plugin, its list and the paks it
    /// needs - so it is flipped while the game is closed and takes effect the next time it starts.
    /// </summary>
    public partial class GameModesTab : UserControl
    {
        private bool _filling;

        public GameModesTab()
        {
            InitializeComponent();
            translateStaticStrings();
            IsVisibleChanged += (_, _) => { if (IsVisible) { show(); } };
        }

        private void translateStaticStrings()
        {
            titleLabel.Content = R.MODES_TAB;
            hint.Text = R.MODES_HINT;
            gemsOn.Content = R.ITEMS_GEMS_ON;
            gemsWhy.Text = R.ITEMS_GEMS_WHY;
            talentsOn.Content = R.TALENTS_ON;
            talentsWhy.Text = R.TALENTS_WHY;
            masteryOn.Content = R.MASTERY_ON;
            masteryWhy.Text = R.MASTERY_WHY;
            enchantLevelsOn.Content = R.ENCHANT_LEVELS_ON;
            mythicOn.Content = R.MYTHIC_ON;
            mythicWhy.Text = string.Format(R.MYTHIC_WHY, Mythic.EMERALDS.ToString("N0"), Mythic.GOLD.ToString("N0"));
            enchantLevelsWhy.Text = R.ENCHANT_LEVELS_WHY;
            apocalypsePlusOn.Content = R.STATS_APOC_PLUS;
            apocalypsePlusWhy.Text = R.STATS_APOC_PLUS_WHY;
        }

        /// <summary>Both switches, as installed; neither can be used without the game's own folder.</summary>
        private void show()
        {
            _filling = true;
            var available = GamePlugin.gameFolder() != null;
            gemsOn.IsChecked = Gems.isOn;
            gemsOn.IsEnabled = available && CustomSkins.ready;
            talentsOn.IsChecked = Talents.isOn;
            talentsOn.IsEnabled = available && CustomSkins.ready;
            masteryOn.IsChecked = Mastery.isOn;
            masteryOn.IsEnabled = available && CustomSkins.ready;
            enchantLevelsOn.IsChecked = EnchantLevels.isOn;
            mythicOn.IsChecked = Mythic.isOn;
            mythicOn.IsEnabled = available && CustomSkins.ready;
            enchantLevelsOn.IsEnabled = CustomSkins.ready;
            apocalypsePlusOn.IsChecked = ApocalypsePlus.isOn;
            apocalypsePlusOn.IsEnabled = available;
            _filling = false;
        }

        /// <summary>
        /// Gems on or off: the switch, then the New Items pak, the plugin's list and the Gems pak
        /// rebuilt with or without them. Off asks first - the game drops what it no longer knows.
        /// </summary>
        private async void gemsOn_Changed(object sender, RoutedEventArgs e)
        {
            if (_filling) { return; }
            var on = gemsOn.IsChecked == true;
            if (GameRunning.isUp) { Notices.warn(R.MODS_GAME_RUNNING); show(); return; }
            if (!on && MessageBox.Show(R.ITEMS_GEMS_OFF_WARN, R.MODES_TAB, MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes)
            {
                show();
                return;
            }
            IsEnabled = false;
            statusLabel.Text = R.ITEMS_WORKING;
            try
            {
                Gems.set(on);
                await Task.Run(() => CustomItems.build(CustomItems.load()));
                statusLabel.Text = on ? R.ITEMS_GEMS_DONE : R.ITEMS_GEMS_REMOVED;
            }
            catch (Exception problem)
            {
                Gems.set(!on);
                statusLabel.Text = string.Format(R.ITEMS_FAILED, problem.Message);
            }
            finally
            {
                IsEnabled = true;
                show();
            }
        }

        /// <summary>
        /// The talent tree on or off: as gems - the New Items pak (its hidden currencies and
        /// properties), the plugin's list and the tree's pak rebuilt. Off asks first.
        /// </summary>
        private async void talentsOn_Changed(object sender, RoutedEventArgs e)
        {
            if (_filling) { return; }
            var on = talentsOn.IsChecked == true;
            if (GameRunning.isUp) { Notices.warn(R.MODS_GAME_RUNNING); show(); return; }
            if (!on && MessageBox.Show(R.TALENTS_OFF_WARN, R.MODES_TAB, MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes)
            {
                show();
                return;
            }
            IsEnabled = false;
            statusLabel.Text = R.ITEMS_WORKING;
            try
            {
                Talents.set(on);
                await Task.Run(() => CustomItems.build(CustomItems.load()));
                statusLabel.Text = on ? R.TALENTS_DONE : R.TALENTS_REMOVED;
            }
            catch (Exception problem)
            {
                Talents.set(!on);
                statusLabel.Text = string.Format(R.ITEMS_FAILED, problem.Message);
            }
            finally
            {
                IsEnabled = true;
                show();
            }
        }

        /// <summary>Weapon mastery on or off, as Talents: the items pak, the plugin's list and the screens rebuilt.</summary>
        private async void masteryOn_Changed(object sender, RoutedEventArgs e)
        {
            if (_filling) { return; }
            var on = masteryOn.IsChecked == true;
            if (GameRunning.isUp) { Notices.warn(R.MODS_GAME_RUNNING); show(); return; }
            if (!on && MessageBox.Show(R.MASTERY_OFF_WARN, R.MODES_TAB, MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes)
            {
                show();
                return;
            }
            IsEnabled = false;
            statusLabel.Text = R.ITEMS_WORKING;
            try
            {
                Mastery.set(on);
                await Task.Run(() => CustomItems.build(CustomItems.load()));
                statusLabel.Text = on ? R.MASTERY_DONE : R.MASTERY_REMOVED;
            }
            catch (Exception problem)
            {
                Mastery.set(!on);
                statusLabel.Text = string.Format(R.ITEMS_FAILED, problem.Message);
            }
            finally
            {
                IsEnabled = true;
                show();
            }
        }

        /// <summary>Mythic items on or off, as Weapon mastery: the items pak, the plugin's list and the forge rebuilt.</summary>
        private async void mythicOn_Changed(object sender, RoutedEventArgs e)
        {
            if (_filling) { return; }
            var on = mythicOn.IsChecked == true;
            if (GameRunning.isUp) { Notices.warn(R.MODS_GAME_RUNNING); show(); return; }
            if (!on && MessageBox.Show(R.MYTHIC_OFF_WARN, R.MODES_TAB, MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes)
            {
                show();
                return;
            }
            IsEnabled = false;
            statusLabel.Text = R.ITEMS_WORKING;
            try
            {
                Mythic.set(on);
                await Task.Run(() => CustomItems.build(CustomItems.load()));
                statusLabel.Text = on ? R.MYTHIC_DONE : R.MYTHIC_REMOVED;
            }
            catch (Exception problem)
            {
                Mythic.set(!on);
                statusLabel.Text = string.Format(R.ITEMS_FAILED, problem.Message);
            }
            finally
            {
                IsEnabled = true;
                show();
            }
        }

        /// <summary>Enchantment levels IV and V on or off: only the in-game badges, so only their pak.</summary>
        private void enchantLevelsOn_Changed(object sender, RoutedEventArgs e)
        {
            if (_filling) { return; }
            var on = enchantLevelsOn.IsChecked == true;
            if (GameRunning.isUp) { Notices.warn(R.MODS_GAME_RUNNING); show(); return; }
            try
            {
                EnchantLevels.set(on);
                var said = EnchantLevels.syncPanel();
                statusLabel.Text = said.Length > 0 ? said : (on ? R.ENCHANT_LEVELS_DONE : R.ENCHANT_LEVELS_REMOVED);
            }
            catch (Exception problem)
            {
                EnchantLevels.set(!on);
                statusLabel.Text = string.Format(R.ITEMS_FAILED, problem.Message);
            }
            show();
        }

        private void apocalypsePlusOn_Changed(object sender, RoutedEventArgs e)
        {
            if (_filling) { return; }
            var on = apocalypsePlusOn.IsChecked == true;
            //The plugin is a file the game holds while it runs.
            if (GameRunning.isUp) { Notices.warn(R.MODS_GAME_RUNNING); show(); return; }
            try
            {
                ApocalypsePlus.set(on);
                statusLabel.Text = on ? R.STATS_APOC_PLUS_DONE : R.STATS_APOC_PLUS_REMOVED;
                Notices.done(statusLabel.Text);
            }
            catch (Exception problem)
            {
                Notices.error(problem.Message);
            }
            show();
        }
    }
}
