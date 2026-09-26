using MCDSaveEdit.Logic;
using MCDSaveEdit.Services;
using Microsoft.Win32;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
#nullable enable

namespace MCDSaveEdit.UI
{
    /// <summary>
    /// Putting your own music in the game.
    ///
    /// The list is the game's own 112 music tracks, longest first, because length is the only thing
    /// that tells a track from a sting - everything under AudioForce is called bgm_ and most of it
    /// is one bar of something. Where the name gives away the mission it is spelled out beside it:
    /// the audio team used two letters per word, so crwo is Creeper Woods and sqco is Squid Coast,
    /// and without that bgm_env_crwoInn-001 is unguessable.
    /// </summary>
    public partial class MusicTab : UserControl
    {
        private IReadOnlyList<GameMusic.Track> _tracks = new List<GameMusic.Track>();
        private GameMusic.Track? _chosen;
        private string? _file;

        public MusicTab()
        {
            InitializeComponent();
            setStrings();

            //Read when the tab is first looked at rather than when the window is built. The paks
            //are not loaded yet at construction, so a catalogue built then would be permanently
            //empty with nothing to say why - which is how the Mods tab does it too.
            IsVisibleChanged += (_, _) => { if (IsVisible) { refresh(); } };
        }

        private void setStrings()
        {
            tracksLabel.Content = R.MUSIC_TRACKS;
            tracksHint.Text = R.MUSIC_TRACKS_HINT + "  " + R.MUSIC_HINT;
            pickButton.Content = R.MUSIC_CHOOSE;
            installButton.Content = R.MUSIC_INSTALL;
            removeButton.Content = R.MUSIC_REMOVE;
        }

        /// <summary>
        /// Reads the catalogue, once, when the tab is first looked at.
        ///
        /// Not in the constructor: the paks are not loaded when the window is built, and a list
        /// built then would be permanently empty with nothing to say why.
        /// </summary>
        public void refresh()
        {
            if (_tracks.Count == 0 && CustomSkins.ready)
            {
                _tracks = GameMusic.all();
            }

            fillList();
            fillInstalled();
            updateUI();
        }

        private void fillList()
        {
            var wanted = searchBox.Text?.Trim() ?? string.Empty;
            searchHint.Visibility = wanted.Length == 0 ? Visibility.Visible : Visibility.Collapsed;

            //Matched against the spelled-out label rather than the raw name, so searching for
            //"creeper" finds bgm_env_crwoInn - which is the entire reason the places are decoded.
            var shown = _tracks
                .Where(track => wanted.Length == 0
                    || track.Label.IndexOf(wanted, StringComparison.OrdinalIgnoreCase) >= 0)
                .ToList();

            trackList.ItemsSource = shown.Select(track => new {
                Track = track,
                Text = $"{track.Label}   —   {track.Bytes / 1024:N0} KB, about {track.Seconds}s",
            }).ToList();
            trackList.DisplayMemberPath = "Text";
        }

        private void fillInstalled()
        {
            installedStack.Children.Clear();

            foreach (var mod in MusicMod.installed())
            {
                var row = new DockPanel { Margin = new Thickness(0, 0, 0, 4) };

                var drop = new Button { Content = R.MUSIC_REMOVE, Padding = new Thickness(6, 2, 6, 2) };
                drop.Click += (_, _) =>
                {
                    try
                    {
                        System.IO.File.Delete(mod);
                        statusLabel.Text = string.Format(R.MUSIC_REMOVED,
                            System.IO.Path.GetFileName(mod));
                        fillInstalled();
                    }
                    catch (Exception problem) { statusLabel.Text = problem.Message; }
                };
                DockPanel.SetDock(drop, Dock.Right);
                row.Children.Add(drop);

                row.Children.Add(new TextBlock {
                    Text = System.IO.Path.GetFileName(mod),
                    VerticalAlignment = VerticalAlignment.Center,
                    TextTrimming = TextTrimming.CharacterEllipsis,
                });

                installedStack.Children.Add(row);
            }
        }

        private void updateUI()
        {
            var ready = CustomSkins.ready;

            pickButton.IsEnabled = ready;
            installButton.IsEnabled = ready && _chosen != null && _file != null;
            removeButton.IsEnabled = ready && _chosen != null;

            chosenTrackLabel.Text = _chosen?.Label ?? R.MUSIC_NONE_CHOSEN;
            chosenTrackDetail.Text = _chosen == null
                ? string.Empty
                : $"{_chosen.EnginePath}   —   {_chosen.Bytes / 1024:N0} KB";

            chosenFileLabel.Text = _file == null
                ? string.Empty
                : string.Format(R.MUSIC_FILE_CHOSEN, System.IO.Path.GetFileName(_file));
        }

        private void searchBox_TextChanged(object sender, TextChangedEventArgs e) => fillList();

        private void trackList_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            dynamic? picked = trackList.SelectedItem;
            _chosen = picked?.Track as GameMusic.Track;
            updateUI();
        }

        private void pickButton_Click(object sender, RoutedEventArgs e)
        {
            var dialog = new OpenFileDialog {
                Title = R.MUSIC_CHOOSE,
                Filter = "Audio (*.mp3;*.ogg;*.wav)|*.mp3;*.ogg;*.wav",
                CheckFileExists = true,
            };
            if (dialog.ShowDialog() != true) { return; }

            _file = dialog.FileName;
            updateUI();
        }

        private void installButton_Click(object sender, RoutedEventArgs e)
        {
            if (_chosen == null || _file == null) { return; }

            //The game holds its paks open, so nothing can be written over one while it is up - and
            //a new pak written now would not be read until it restarts anyway.
            if (GameRunning.isUp)
            {
                Notices.warn(R.MODS_GAME_RUNNING);
                return;
            }

            try
            {
                var mod = MusicMod.install(_chosen, _file);
                statusLabel.Text = string.Format(R.MUSIC_INSTALLED,
                    System.IO.Path.GetFileName(mod.Path), _chosen.Label);
                fillInstalled();
            }
            catch (Exception problem)
            {
                //Said in the line rather than thrown at a dialog. Most of the ways this fails are
                //information about the file somebody picked, not faults.
                statusLabel.Text = problem.Message;
            }

            updateUI();
        }

        private void removeButton_Click(object sender, RoutedEventArgs e)
        {
            if (_chosen == null) { return; }

            try
            {
                var gone = MusicMod.remove(_chosen);
                statusLabel.Text = gone
                    ? string.Format(R.MUSIC_REMOVED, _chosen.Name)
                    : R.MUSIC_NOT_INSTALLED;
                fillInstalled();
            }
            catch (Exception problem) { statusLabel.Text = problem.Message; }
        }
    }
}
