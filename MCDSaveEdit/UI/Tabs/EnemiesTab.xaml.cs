using MCDSaveEdit.Logic;
using MCDSaveEdit.Services;
using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
#nullable enable

namespace MCDSaveEdit.UI
{
    /// <summary>
    /// How hard the game is, changed while it is running.
    ///
    /// This started out trying to make levels denser, which is the harder version of the question.
    /// Enemy counts are decided when a room is built, and are not a number sitting anywhere that
    /// can be multiplied - the spawn points were found and read, and the numbers on them turned
    /// out not to mean what they looked like.
    ///
    /// How strong those enemies are is exactly that kind of number, and better for difficulty
    /// anyway: a room with twice as many skeletons in it is the same fight twice, while a room of
    /// skeletons that take two hits instead of one is a different fight. Both enemies and your own
    /// character keep their attributes as plain multipliers, so this is the same write as the
    /// camera, done to three hundred things instead of one.
    /// </summary>
    public partial class EnemiesTab : UserControl
    {
        private sealed class Row
        {
            public Func<LiveStatsLink, float> Read { get; set; } = _ => 1f;
            public Action<LiveStatsLink, float> Write { get; set; } = (_, _) => { };
            public Slider? Slider { get; set; }
            public TextBlock? Value { get; set; }
            public string Suffix { get; set; } = "x";
            public CheckBox? Switch { get; set; }
        }

        private readonly List<Row> _enemyRows = new List<Row>();
        private readonly List<Row> _playerRows = new List<Row>();
        private readonly LiveStatsLink _live = new LiveStatsLink();
        private bool _filling;
        private bool _tookYours;

        public EnemiesTab()
        {
            InitializeComponent();
            translateStaticStrings();

            _live.changed += () => Dispatcher.BeginInvoke(new Action(showLive));

            //Not disposed when this page goes away.
            //
            //A TabControl takes the old page out of the tree when another is chosen, so hanging
            //disposal off Unloaded meant that looking at any other tab quietly stopped holding
            //the settings and put everything back - which looks exactly like the game having
            //forgotten them. It belongs to the window, so it is let go of when the window closes.
            Loaded += (s, e) => keepUntilTheWindowCloses();

            buildRows();
            showLive();
        }

        private bool _hookedClose;

        private void keepUntilTheWindowCloses()
        {
            if (_hookedClose) { return; }

            var window = Window.GetWindow(this);
            if (window == null) { return; }

            _hookedClose = true;
            window.Closed += (s, e) => _live.Dispose();
        }

        private void translateStaticStrings()
        {
            titleLabel.Content = R.STATS_TAB;
            enemiesOn.Content = R.STATS_ENEMIES_ON;
            playerOn.Content = R.STATS_PLAYER_ON;
            restoreButton.Content = R.STATS_RESTORE;
        }

        public void updateUI() => showLive();

        private void buildRows()
        {
            enemyStack.Children.Clear();
            playerStack.Children.Clear();
            _enemyRows.Clear();
            _playerRows.Clear();

            //Each slider knows which switch it belongs to, so moving one can turn it on.
            addRow(enemyStack, _enemyRows, R.STATS_TOUGH, R.STATS_TOUGH_WHY, 1, 20, 0.5, "x",
                l => l.enemyToughness, (l, v) => l.enemyToughness = v);
            addRow(enemyStack, _enemyRows, R.STATS_ENEMY_SPEED, R.STATS_ENEMY_SPEED_WHY, 0.25, 4, 0.05, "x",
                l => l.enemySpeed, (l, v) => l.enemySpeed = v);

            addRow(playerStack, _playerRows, R.STATS_YOUR_SPEED, R.STATS_YOUR_SPEED_WHY, 0.25, 5, 0.05, "x",
                l => l.yourSpeed, (l, v) => l.yourSpeed = v);
            addRow(playerStack, _playerRows, R.STATS_DODGE_COOLDOWN, R.STATS_DODGE_COOLDOWN_WHY, 0.1, 5, 0.1, "s",
                l => l.yourDodgeCooldown, (l, v) => l.yourDodgeCooldown = v);
            addRow(playerStack, _playerRows, R.STATS_DODGE_CHARGES, R.STATS_DODGE_CHARGES_WHY, 1, 5, 1, "",
                l => l.yourDodgeCharges, (l, v) => l.yourDodgeCharges = v);
            //Down to a fortieth, because floating is the interesting end of this one and a tenth
            //barely reads as different from normal.
            addRow(playerStack, _playerRows, R.STATS_GRAVITY, R.STATS_GRAVITY_WHY, 0.025, 3, 0.025, "x",
                l => l.yourGravity, (l, v) => l.yourGravity = v);
            addRow(playerStack, _playerRows, R.STATS_ATTACK_SPEED, R.STATS_ATTACK_SPEED_WHY, 0.25, 5, 0.05, "x",
                l => l.yourAttackSpeed, (l, v) => l.yourAttackSpeed = v);
        }

        private void addRow(StackPanel into, List<Row> list, string caption, string why,
            double minimum, double maximum, double step, string suffix,
            Func<LiveStatsLink, float> read, Action<LiveStatsLink, float> write)
        {
            var row = new Row { Read = read, Write = write, Suffix = suffix };

            var panel = new StackPanel { Margin = new Thickness(0, 10, 0, 0) };

            var header = new DockPanel();
            row.Value = new TextBlock {
                Width = 90, TextAlignment = TextAlignment.Right,
                Foreground = System.Windows.Media.Brushes.Gray,
            };
            DockPanel.SetDock(row.Value, Dock.Right);
            header.Children.Add(row.Value);
            header.Children.Add(new TextBlock { Text = caption });
            panel.Children.Add(header);

            row.Slider = new Slider {
                Minimum = minimum, Maximum = maximum, Value = read(_live),
                TickFrequency = step, IsSnapToTickEnabled = true,
            };
            row.Slider.ValueChanged += (s, e) => {
                if (row.Value != null) { row.Value.Text = row.Slider!.Value.ToString("0.##") + row.Suffix; }
                if (_filling) { return; }

                row.Write(_live, (float)row.Slider!.Value);

                //Moving a slider is as plain a statement of intent as ticking the box, so it
                //ticks it. Requiring both was the reason this page looked like it did nothing.
                if (row.Switch != null && row.Switch.IsChecked != true) { row.Switch.IsChecked = true; }
            };
            panel.Children.Add(row.Slider);

            panel.Children.Add(new TextBlock {
                Text = why, Foreground = System.Windows.Media.Brushes.Gray,
                TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 2, 0, 0),
            });

            row.Switch = ReferenceEquals(into, enemyStack) ? enemiesOn : playerOn;

            into.Children.Add(panel);
            list.Add(row);

            if (row.Value != null) { row.Value.Text = row.Slider.Value.ToString("0.##") + row.Suffix; }
        }

        private void showLive()
        {
            var on = _live.attached;

            enemiesOn.IsEnabled = on;
            playerOn.IsEnabled = on;
            restoreButton.IsEnabled = on;

            liveHint.Text = on ? R.STATS_HINT : string.Format(R.CAMERA_LIVE_WAITING, _live.status);
            if (on) { statusLabel.Text = _live.status; }

            //Your own numbers, taken once so the sliders start where your character actually is
            //rather than at somebody's idea of normal.
            if (on && !_tookYours && _live.yours() is (float speed, float cooldown, float charges, float gravity))
            {
                _tookYours = true;
                _live.yourSpeed = speed;
                _live.yourDodgeCooldown = cooldown;
                _live.yourDodgeCharges = charges;
                _live.yourGravity = gravity;

                _filling = true;
                foreach (var row in _playerRows)
                {
                    if (row.Slider == null) { continue; }

                    var value = row.Read(_live);
                    row.Slider.Value = Math.Max(row.Slider.Minimum, Math.Min(row.Slider.Maximum, value));
                }
                _filling = false;
            }

            _filling = true;
            enemiesOn.IsChecked = _live.enemiesOn;
            playerOn.IsChecked = _live.playerOn;
            _filling = false;
        }

        private void enemiesOn_Changed(object sender, RoutedEventArgs e)
        {
            if (_filling) { return; }

            if (enemiesOn.IsChecked == true) { _live.enemiesOn = true; return; }

            _live.restoreEnemies();
            statusLabel.Text = R.STATS_ENEMIES_BACK;
        }

        private void playerOn_Changed(object sender, RoutedEventArgs e)
        {
            if (_filling) { return; }

            if (playerOn.IsChecked == true) { _live.playerOn = true; return; }

            _live.restorePlayer();
            statusLabel.Text = R.STATS_PLAYER_BACK;
        }

        private void restoreButton_Click(object sender, RoutedEventArgs e)
        {
            _live.restoreEnemies();
            _live.restorePlayer();

            _filling = true;
            enemiesOn.IsChecked = false;
            playerOn.IsChecked = false;
            _filling = false;

            statusLabel.Text = R.STATS_ALL_BACK;
        }
    }
}
