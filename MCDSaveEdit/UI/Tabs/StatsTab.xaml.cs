using MCDSaveEdit.Data;
using MCDSaveEdit.Logic;
using MCDSaveEdit.Services;
using MCDSaveEdit.ViewModels;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
#nullable enable

namespace MCDSaveEdit.UI
{
    /// <summary>
    /// Interaction logic for StatsTab.xaml
    /// </summary>
    public partial class StatsTab : UserControl
    {
        private ProfileViewModel? _model;
        public ProfileViewModel? model {
            get { return _model; }
            set { _model = value; }
        }

        public StatsTab()
        {
            InitializeComponent();
            translateStaticStrings();

            //Clear out design/testing values
            updateUI();
        }

        public void updateUI()
        {
            fillStatsStack();
            fillMobKillsStack();
        }

        private void translateStaticStrings()
        {
            statsLabel.Content = R.PROGRESS_STAT_COUNTERS;
            mobKillsLabel.Content = R.MOB_KILLS;
        }

        private void fillStatsStack()
        {
            fill(statsStack, statsCountLabel, _model?.profile.value?.ProgressStatCounters, statsSearchBox.Text);
        }

        private void fillMobKillsStack()
        {
            fill(mobKillsStack, mobKillsCountLabel, _model?.profile.value?.MobKills, mobKillsSearchBox.Text);
        }

        /// <summary>
        /// Rebuilds one list, honouring the search box and tinting alternate rows.
        /// </summary>
        private void fill(Panel stack, TextBlock countLabel, IDictionary<string, long>? source, string? search)
        {
            stack.Children.Clear();
            if (source == null) { countLabel.Text = string.Empty; return; }

            var matches = source.Where(pair => matchesSearch(pair.Key, search)).ToList();

            int index = 0;
            foreach (var pair in matches)
            {
                stack.Children.Add(createStatField(pair.Key, pair.Value, index++));
            }

            //Only mention filtering when something is actually hidden.
            countLabel.Text = matches.Count == source.Count
                ? $"{source.Count}"
                : $"{matches.Count} of {source.Count}";
        }

        private static bool matchesSearch(string name, string? search)
        {
            if (string.IsNullOrWhiteSpace(search)) { return true; }
            return name.IndexOf(search!.Trim(), StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private FrameworkElement createStatField(string fieldName, long fieldValue, int index)
        {
            var label = new TextBlock() {
                VerticalAlignment = VerticalAlignment.Center,
                TextTrimming = TextTrimming.CharacterEllipsis,
                FontSize = 13,
                Text = fieldName,
                ToolTip = fieldName,
            };

            //No explicit Background: the themed TextBox style supplies one, and setting it
            //here as null is what made these fields look unstyled before.
            var textbox = new TextBox() {
                VerticalContentAlignment = VerticalAlignment.Center,
                TextAlignment = TextAlignment.Right,
                Width = 88,
                FontSize = 14,
                Padding = new Thickness(6, 2, 6, 2),
                Text = fieldValue.ToString(),
                Tag = fieldName,
            };
            textbox.TextChanged += statTextbox_TextChanged;

            var stepper = new Stepper() {
                Width = 22,
                Margin = new Thickness(4, 0, 0, 0),
                Tag = textbox,
            };
            stepper.UpButtonClick += statStepper_UpButtonClick;
            stepper.DownButtonClick += statStepper_DownButtonClick;

            //Grid rather than DockPanel so a long stat name is trimmed instead of shoving
            //the value box off the edge.
            var grid = new Grid() { Height = 34 };
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            Grid.SetColumn(label, 0);
            Grid.SetColumn(textbox, 1);
            Grid.SetColumn(stepper, 2);
            grid.Children.Add(label);
            grid.Children.Add(textbox);
            grid.Children.Add(stepper);

            var row = new Border {
                Style = (Style)FindResource(index % 2 == 0 ? "StatRowEven" : "StatRowOdd"),
                Child = grid,
            };
            return row;
        }

        private void statsSearchBox_TextChanged(object sender, TextChangedEventArgs e)
        {
            statsSearchHint.Visibility = string.IsNullOrEmpty(statsSearchBox.Text) ? Visibility.Visible : Visibility.Collapsed;
            fillStatsStack();
        }

        private void mobKillsSearchBox_TextChanged(object sender, TextChangedEventArgs e)
        {
            mobKillsSearchHint.Visibility = string.IsNullOrEmpty(mobKillsSearchBox.Text) ? Visibility.Visible : Visibility.Collapsed;
            fillMobKillsStack();
        }

        #region User Input Methods

        private void statStepper_UpButtonClick(object sender, RoutedEventArgs e)
        {
            if (_model?.profile.value == null) { return; }
            var stepper = sender as Stepper;
            if (stepper == null) { return; }
            var textBox = stepper.Tag as TextBox;
            if (textBox == null) { return; }
            if (long.TryParse(textBox.Text, out long currentValue))
            {
                textBox.Text = Math.Min(currentValue + 1, long.MaxValue).ToString();
            }
        }

        private void statStepper_DownButtonClick(object sender, RoutedEventArgs e)
        {
            if (_model?.profile.value == null) { return; }
            var stepper = sender as Stepper;
            if (stepper == null) { return; }
            var textBox = stepper.Tag as TextBox;
            if (textBox == null) { return; }
            if (long.TryParse(textBox.Text, out long currentValue))
            {
                textBox.Text = Math.Max(currentValue - 1, 0).ToString();
            }
        }

        private void statTextbox_TextChanged(object sender, TextChangedEventArgs e)
        {
            if (_model?.profile.value == null) { return; }
            var statTextBox = sender as TextBox;
            if (statTextBox == null) { return; }
            var fieldName = statTextBox.Tag as string;
            if (fieldName == null) { return; }

            if (long.TryParse(statTextBox.Text, out long newValue))
            {
                EventLogger.logEvent("statTextbox_TextChanged");
                statTextBox.ClearValue(TextBox.BorderBrushProperty);
                if (_model!.profile.value.ProgressStatCounters.ContainsKey(fieldName))
                {
                    _model!.profile.value.ProgressStatCounters[fieldName] = newValue;
                }
                else
                {
                    _model!.profile.value.MobKills[fieldName] = newValue;
                }
            }
            else
            {
                statTextBox.BorderBrush = (Brush)FindResource("Brush.Danger");
            }
        }

        #endregion
    }
}
