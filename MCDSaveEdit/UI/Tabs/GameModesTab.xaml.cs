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
    /// Switches that change the game itself rather than a save: gems and sockets, and Apocalypse+
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
