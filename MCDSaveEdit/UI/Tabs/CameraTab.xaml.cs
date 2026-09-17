using MCDSaveEdit.Logic;
using MCDSaveEdit.Services;
using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
#nullable enable

namespace MCDSaveEdit.UI
{
    /// <summary>
    /// Where the camera sits, in the game that is running.
    ///
    /// This used to write a pak. That meant closing the game, writing a file, starting it again,
    /// walking somewhere worth looking at, deciding the angle was slightly wrong, and doing all of
    /// it again - so the tab was built around making one change at a time, because a test that
    /// takes two minutes is a test nobody repeats.
    ///
    /// None of that is true any more. Every setting here is written straight into the running game
    /// and takes effect as the slider moves, so the loop is drag and look rather than build and
    /// wait. Nothing is written to disk, and quitting the game undoes all of it.
    ///
    /// What replaced the pak is presets. Tuning a camera by dragging takes a few minutes and ends
    /// in numbers nobody wants to find twice, so what somebody arrives at gets a name and comes
    /// back in one click.
    /// </summary>
    public partial class CameraTab : UserControl
    {
        /// <summary>One slider, and the field of a preset it stands for.</summary>
        private sealed class Row
        {
            public string Caption { get; set; } = "";
            public Func<CameraPreset, float> Read { get; set; } = _ => 0f;
            public Action<CameraPreset, float> Write { get; set; } = (_, _) => { };
            public Slider? Slider { get; set; }
            public TextBlock? Value { get; set; }
        }

        private readonly List<Row> _rows = new List<Row>();
        private readonly List<CameraPreset> _presets = new List<CameraPreset>();

        /// <summary>What the sliders currently say, which is not any saved preset until it is named.</summary>
        private CameraPreset _current = new CameraPreset();

        private bool _filling;

        /// <summary>Whether a preset has been picked, and so is waiting for a game to arrive.</summary>
        private bool _chosen;

        private readonly LiveCameraLink _live = new LiveCameraLink();

        public CameraTab()
        {
            InitializeComponent();
            translateStaticStrings();

            _live.changed += showLive;

            _live.lookingStopped += () => Dispatcher.BeginInvoke(new Action(() => {
                _filling = true;
                mouseLook.IsChecked = false;
                _filling = false;
            }));

            _live.walkingStopped += () => Dispatcher.BeginInvoke(new Action(() => {
                _filling = true;
                wasd.IsChecked = false;
                _filling = false;
            }));

            _live.suspendedChanged += () => Dispatcher.BeginInvoke(new Action(() => {
                statusLabel.Text = _live.suspended ? R.CAMERA_SUSPENDED : R.CAMERA_RESUMED;
            }));

            //F10 from inside the game, which is the only way to reach this while the pointer is
            //pinned to the middle of the screen.
            _live.togglePressed += () => Dispatcher.BeginInvoke(new Action(() => {
                if (!_live.attached) { return; }

                if (_live.thirdPersonOn) { _live.restoreOriginal(); }
                else { _live.apply(_current, (float)sensitivity.Value, invertPitch.IsChecked == true, out _); }

                showSwitches();
            }));

            Unloaded += (s, e) => _live.Dispose();

            buildRows();
            loadPresets();
            updateUI();
        }

        private void translateStaticStrings()
        {
            settingsLabel.Content = R.CAMERA_SETTINGS;
            presetsLabel.Content = R.CAMERA_PRESETS;
            presetsHint.Text = R.CAMERA_PRESETS_HINT;
            saveLabel.Text = R.CAMERA_SAVE_HINT;
            saveButton.Content = R.CAMERA_SAVE;
            deleteButton.Content = R.CAMERA_DELETE;
            restoreButton.Content = R.CAMERA_LIVE_REVERT;
            mouseLook.Content = R.CAMERA_MOUSE_LOOK;
            wasd.Content = R.CAMERA_WASD;
            mouseButtons.Content = R.CAMERA_BUTTONS;
            followsAim.Content = R.CAMERA_FOLLOWS_AIM;
            sensitivityLabel.Text = R.CAMERA_SENSITIVITY;
            invertPitch.Content = R.CAMERA_INVERT;
            canJump.Content = R.CAMERA_JUMP;
            jumpWhy.Text = R.CAMERA_JUMP_WHY;
            jumpHeightLabel.Text = R.CAMERA_JUMP_HEIGHT;
            airControlLabel.Text = R.CAMERA_AIR_CONTROL;
            jumpCountCaption.Text = R.CAMERA_JUMP_COUNT;
        }

        public void updateUI()
        {
            showLive();
            showRows();
        }

        #region The values

        /// <summary>
        /// One slider per number, with fixed ranges.
        ///
        /// They used to be built around whatever the paks said, so that the game's own value sat
        /// somewhere sensible in each slider's travel. There are no paks to ask now, and it turns
        /// out not to matter: these ranges are what the settings can usefully be, and a preset
        /// puts the handle where it belongs.
        /// </summary>
        private void buildRows()
        {
            settingStack.Children.Clear();
            _rows.Clear();

            addRow(R.CAMERA_DISTANCE, 0, 4000, 10,
                p => p.Distance, (p, v) => p.Distance = v);
            addRow(R.CAMERA_PITCH, -89, 15, 1,
                p => p.Pitch, (p, v) => p.Pitch = v);
            addRow(R.CAMERA_FOV, 30, 120, 1,
                p => p.FieldOfView, (p, v) => p.FieldOfView = v);
            addRow(R.CAMERA_PIVOT, -100, 320, 5,
                p => p.PivotHeight, (p, v) => p.PivotHeight = v);
            addRow(R.CAMERA_SIDE, -200, 200, 5,
                p => p.SocketSide, (p, v) => p.SocketSide = v);
            addRow(R.CAMERA_HEIGHT, -200, 200, 5,
                p => p.SocketHeight, (p, v) => p.SocketHeight = v);
            addRow(R.CAMERA_LAG, 1, 60, 1,
                p => p.RotationLagSpeed, (p, v) => p.RotationLagSpeed = v);

            //How hard the camera is bolted to the character, and the one setting here that is a
            //matter of taste rather than a right answer. Low floats and rides over stairs; high
            //follows exactly and shows every step the character takes. The game ships it at 1.
            //
            //A slider rather than a number chosen here, because both ends of it have been tried
            //and disliked for opposite reasons, and the person playing can find the middle in ten
            //seconds where guessing at it took six builds.
            addRow(R.CAMERA_SMOOTHING, 0.2, 40, 0.2,
                p => p.LagSpeed, (p, v) => p.LagSpeed = v);
        }

        private void addRow(string caption, double minimum, double maximum, double step,
            Func<CameraPreset, float> read, Action<CameraPreset, float> write)
        {
            var row = new Row { Caption = caption, Read = read, Write = write };

            var panel = new StackPanel { Margin = new Thickness(0, 6, 0, 0) };

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
                Minimum = minimum, Maximum = maximum,
                TickFrequency = step, IsSnapToTickEnabled = true,
            };
            row.Slider.ValueChanged += (s, e) => {
                if (row.Value != null) { row.Value.Text = row.Slider!.Value.ToString("0.#"); }
                if (_filling) { return; }

                //Straight into the game. There is no apply step because there is nothing to
                //build - the number is the setting, and the game is already running.
                row.Write(_current, (float)row.Slider!.Value);
                pushLive();
            };
            panel.Children.Add(row.Slider);

            settingStack.Children.Add(panel);
            _rows.Add(row);
        }

        /// <summary>Puts the sliders where the current settings say, without sending them back.</summary>
        private void showRows()
        {
            _filling = true;
            foreach (var row in _rows)
            {
                if (row.Slider == null) { continue; }

                var value = row.Read(_current);
                row.Slider.Value = Math.Max(row.Slider.Minimum, Math.Min(row.Slider.Maximum, value));
                if (row.Value != null) { row.Value.Text = row.Slider.Value.ToString("0.#"); }
            }
            _filling = false;
        }

        private void pushLive()
        {
            if (!_live.attached) { return; }

            _current.MouseLook = mouseLook.IsChecked == true;
            _current.Wasd = wasd.IsChecked == true;

            _chosen = true;

            _live.apply(_current, (float)sensitivity.Value, invertPitch.IsChecked == true, out var problem);
            if (problem.Length > 0) { statusLabel.Text = problem; }
        }

        #endregion

        #region Presets

        private void loadPresets()
        {
            _presets.Clear();
            _presets.AddRange(CameraPresets.all());
            showPresets();
        }

        private void showPresets()
        {
            _filling = true;
            presetList.Items.Clear();
            foreach (var preset in _presets)
            {
                presetList.Items.Add(new ListBoxItem {
                    Content = preset.BuiltIn ? preset.Name : preset.Name + "  *",
                    Tag = preset,
                });
            }
            _filling = false;
        }

        private CameraPreset? selected()
        {
            return (presetList.SelectedItem as ListBoxItem)?.Tag as CameraPreset;
        }

        /// <summary>
        /// Picking one applies all of it at once.
        ///
        /// Whole settings rather than one change at a time, which is the opposite of how this tab
        /// started. That was right when nothing was known about which settings broke the game and
        /// a preset changing six things proved nothing when it failed. The settings are understood
        /// now, and a camera is not one number.
        /// </summary>
        private void presetList_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_filling) { return; }

            var preset = selected();
            if (preset == null) { return; }

            _current = preset.copy();
            _chosen = true;
            showRows();

            _filling = true;
            mouseLook.IsChecked = _current.MouseLook;
            wasd.IsChecked = _current.Wasd;
            _filling = false;

            pushLive();

            deleteButton.IsEnabled = !preset.BuiltIn;
            saveName.Text = preset.BuiltIn ? "" : preset.Name;
            statusLabel.Text = string.Format(R.CAMERA_PRESET_APPLIED, preset.Name);
        }

        /// <summary>
        /// Keeps whatever the sliders say, under a name.
        ///
        /// A name that already belongs to a saved preset writes over it, which is what somebody
        /// adjusting their own setting expects. The three that ship cannot be written over - they
        /// are what "start again" means.
        /// </summary>
        private void saveButton_Click(object sender, RoutedEventArgs e)
        {
            var name = saveName.Text.Trim();
            if (name.Length == 0) { statusLabel.Text = R.CAMERA_SAVE_NEEDS_NAME; return; }

            foreach (var one in _presets)
            {
                if (!one.BuiltIn || !string.Equals(one.Name, name, StringComparison.OrdinalIgnoreCase)) { continue; }

                statusLabel.Text = R.CAMERA_SAVE_BUILT_IN;
                return;
            }

            var saved = _current.copy();
            saved.Name = name;
            saved.BuiltIn = false;
            saved.MouseLook = mouseLook.IsChecked == true;
            saved.Wasd = wasd.IsChecked == true;

            var replaced = false;
            for (int i = 0; i < _presets.Count; i++)
            {
                if (_presets[i].BuiltIn || !string.Equals(_presets[i].Name, name, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                _presets[i] = saved;
                replaced = true;
                break;
            }
            if (!replaced) { _presets.Add(saved); }

            CameraPresets.save(_presets);
            showPresets();

            statusLabel.Text = string.Format(
                replaced ? R.CAMERA_SAVE_REPLACED : R.CAMERA_SAVE_DONE, name);
        }

        private void saveName_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Enter) { saveButton_Click(sender, e); }
        }

        private void deleteButton_Click(object sender, RoutedEventArgs e)
        {
            var preset = selected();
            if (preset == null || preset.BuiltIn) { return; }

            _presets.Remove(preset);
            CameraPresets.save(_presets);
            showPresets();

            statusLabel.Text = string.Format(R.CAMERA_DELETED, preset.Name);
        }

        #endregion

        #region The running game

        /// <summary>
        /// Says whether the game is there, and enables everything only when it is.
        ///
        /// Every setting on this tab needs a running game now, so the whole panel depends on it
        /// rather than only a preview box. A tab full of sliders that quietly do nothing would be
        /// worse than one that says what it is waiting for.
        /// </summary>
        private void showLive()
        {
            var on = _live.attached;

            //The sliders and the preset list stay usable whether the game is running or not.
            //
            //Partly because picking how you want to play before starting the game is a reasonable
            //thing to do, and partly because a disabled ListBox in WPF is painted by a trigger in
            //its default template - SystemColors.ControlBrushKey, which is near white - and that
            //trigger sets the border directly, so it beats any background this tab sets. Three
            //attempts at colouring it failed for that reason before the greyed out item text gave
            //it away.
            mouseLook.IsEnabled = on;
            wasd.IsEnabled = on;
            mouseButtons.IsEnabled = on;
            canJump.IsEnabled = on;
            jumpHeight.IsEnabled = on;
            airControl.IsEnabled = on;
            jumpCount.IsEnabled = on;
            followsAim.IsEnabled = on;
            sensitivity.IsEnabled = on;
            invertPitch.IsEnabled = on;
            restoreButton.IsEnabled = on;

            liveHint.Text = on
                ? R.CAMERA_LIVE_HINT
                : string.Format(R.CAMERA_LIVE_WAITING, _live.status);

            if (!on)
            {
                _filling = true;
                mouseLook.IsChecked = false;
                wasd.IsChecked = false;
                _filling = false;
                return;
            }

            //The game may already have been set up from an earlier session, so the boxes are shown
            //rather than assumed.
            _filling = true;
            followsAim.IsChecked = _live.followsAim == true;
            mouseButtons.IsChecked = _live.clicksDoNotWalk;
            _filling = false;

            //Something picked while the game was closed is applied now that there is one, which
            //is the whole point of leaving the list usable.
            if (_chosen) { pushLive(); }

            showSwitches();
        }

        /// <summary>Puts the boxes back in step with what is actually running.</summary>
        private void showSwitches()
        {
            _filling = true;
            mouseLook.IsChecked = _live.looking;
            wasd.IsChecked = _live.walking;
            _filling = false;
        }

        private void mouseLook_Changed(object sender, RoutedEventArgs e)
        {
            if (_filling) { return; }

            if (mouseLook.IsChecked == true)
            {
                if (_live.startLooking((float)sensitivity.Value, invertPitch.IsChecked == true)) { return; }

                _filling = true;
                mouseLook.IsChecked = false;
                _filling = false;
                return;
            }

            _live.stopLooking();
        }

        private void wasd_Changed(object sender, RoutedEventArgs e)
        {
            if (_filling) { return; }

            if (wasd.IsChecked != true) { _live.stopWalking(); return; }

            if (!_live.startWalking(out var problem))
            {
                _filling = true;
                wasd.IsChecked = false;
                _filling = false;
                statusLabel.Text = problem;
            }
        }

        /// <summary>
        /// Lets space leave the ground as well as roll.
        ///
        /// Sharing the key with the roll is the point rather than a compromise - pressing it once
        /// rolls and jumps together, which is the leap this was asked for.
        /// </summary>
        private void canJump_Changed(object sender, RoutedEventArgs e)
        {
            if (_filling) { return; }

            _live.canJump = canJump.IsChecked == true;
        }

        private void jumpHeight_Changed(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (jumpCountLabel != null) { jumpCountLabel.Text = ((int)jumpHeight.Value).ToString(); }
            if (_filling) { return; }

            _live.jumpHeight = (float)jumpHeight.Value;
            if (canJump.IsChecked != true) { canJump.IsChecked = true; }
        }

        private void airControl_Changed(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (jumpsLabel != null) { jumpsLabel.Text = airControl.Value.ToString("0.00"); }
            if (_filling) { return; }

            _live.airControl = (float)airControl.Value;
            if (canJump.IsChecked != true) { canJump.IsChecked = true; }
        }

        private void jumpCount_Changed(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (_filling) { return; }

            _live.jumpCount = (int)jumpCount.Value;
            if (canJump.IsChecked != true) { canJump.IsChecked = true; }
        }

        private void mouseButtons_Changed(object sender, RoutedEventArgs e)
        {
            if (_filling) { return; }

            _live.clicksDoNotWalk = mouseButtons.IsChecked == true;
        }

        private void followsAim_Changed(object sender, RoutedEventArgs e)
        {
            if (_filling) { return; }

            _live.setFollowsAim(followsAim.IsChecked == true);
        }

        /// <summary>Restarts the look if it is running, so the change is felt rather than queued.</summary>
        private void invertPitch_Changed(object sender, RoutedEventArgs e)
        {
            if (_filling || !_live.looking) { return; }

            _live.startLooking((float)sensitivity.Value, invertPitch.IsChecked == true);
        }

        /// <summary>
        /// Puts the game's own camera back.
        ///
        /// What it puts back is what the game had before this tab touched anything, read once when
        /// the character was first reached - so it does not need the paks to know what stock looks
        /// like. Quitting the game does the same thing, since none of this was ever written down.
        /// </summary>
        private void restoreButton_Click(object sender, RoutedEventArgs e)
        {
            _live.restoreOriginal();
            showSwitches();

            _filling = true;
            presetList.SelectedItem = null;
            _filling = false;

            statusLabel.Text = R.CAMERA_RESTORED;
        }

        #endregion
    }
}
