using MCDSaveEdit.Logic;
using MCDSaveEdit.Services;
using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
#nullable enable

namespace MCDSaveEdit.UI
{
    /// <summary>
    /// Every asset in the game, searchable, so that a path can be copied out of it.
    ///
    /// A reference tool rather than an editor: nothing here changes anything. It is a window
    /// instead of a panel in the tab because of when it gets used - beside Unreal, while building
    /// something there - and a window can be left open next to the editor where a tab cannot.
    ///
    /// The list is capped rather than paged. Eighty thousand rows scroll fine when virtualised,
    /// but nobody reads past the first screen of a search that matched eleven thousand things;
    /// what they do is type another word. So the count says how many matched, and the cap is only
    /// about how many are built.
    /// </summary>
    public partial class GameAssetsWindow : Window
    {
        //Typing is faster than searching eighty thousand strings, so the search waits for a pause
        //rather than running per keystroke. Long enough to swallow a burst of typing, short enough
        //that it never feels like waiting.
        private static readonly TimeSpan SETTLE = TimeSpan.FromMilliseconds(180);

        private readonly DispatcherTimer _settle;

        //Reading an asset's real class means opening a package, which is slow enough to be worth
        //not doing on the click. Cancelled when the selection moves on, so a fast scroll through
        //the list does not queue up a hundred reads whose answers nobody wants any more.
        private CancellationTokenSource? _reading;

        public GameAssetsWindow()
        {
            InitializeComponent();
            translateStaticStrings();

            _settle = new DispatcherTimer { Interval = SETTLE };
            _settle.Tick += (_, _) => { _settle.Stop(); fill(); };

            Loaded += (_, _) => { fill(); searchBox.Focus(); };
            Closed += (_, _) => _reading?.Cancel();
        }

        private void translateStaticStrings()
        {
            Title = R.ASSETS_TITLE;
            howLabel.Text = R.ASSETS_HOW;
            searchLabel.Content = R.ASSETS_SEARCH;
            copyPathButton.Content = R.ASSETS_COPY_PATH;
            copyFolderButton.Content = R.ASSETS_COPY_FOLDER;
            kindColumn.Header = R.ASSETS_KIND;
            pathColumn.Header = R.ASSETS_PATH;

            if (kindBox.Items.Count == 0)
            {
                //Nothing chosen means everything, which wants a row of its own rather than a
                //checkbox beside the menu.
                kindBox.Items.Add(new ComboBoxItem { Content = R.ALL_ITEMS_FILTER, Tag = null });
                foreach (var kind in GameAssets.KINDS)
                {
                    kindBox.Items.Add(new ComboBoxItem { Content = kind, Tag = kind });
                }
                kindBox.SelectedIndex = 0;
            }
        }

        private void searchBox_TextChanged(object sender, TextChangedEventArgs e)
        {
            _settle.Stop();
            _settle.Start();
        }

        private void kindBox_SelectionChanged(object sender, SelectionChangedEventArgs e) => fill();

        private void fill()
        {
            if (!IsInitialized) { return; }

            var kind = (kindBox.SelectedItem as ComboBoxItem)?.Tag as string;
            var (shown, matched) = GameAssets.search(searchBox.Text, kind);

            list.ItemsSource = shown;
            countLabel.Text = string.Format(R.ASSETS_SHOWING, shown.Count, matched);

            if (matched == 0)
            {
                statusLabel.Text = R.ASSETS_NONE;
                pathBox.Text = string.Empty;
            }
        }

        private void list_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            _reading?.Cancel();

            if (list.SelectedItem is not GameAssets.Asset asset)
            {
                pathBox.Text = string.Empty;
                statusLabel.Text = string.Empty;
                return;
            }

            pathBox.Text = asset.EnginePath;
            statusLabel.Text = R.ASSETS_READING;

            //Now the real class, from the package. The guess in the list comes from the name, and
            //the names in this game are consistent enough that it is nearly always right - but
            //"nearly always" is not what somebody about to make a stub of the wrong kind wants.
            var reading = new CancellationTokenSource();
            _reading = reading;

            var wanted = asset.EnginePath;
            var guessed = asset.Kind;

            Task.Run(() =>
            {
                var real = GameAssets.readKindOf(wanted);
                if (reading.IsCancellationRequested) { return; }

                Dispatcher.BeginInvoke(() =>
                {
                    if (reading.IsCancellationRequested) { return; }

                    //Said plainly either way. An asset whose package would not open still has the
                    //right path, which is the thing actually being copied out of here.
                    statusLabel.Text = real == null
                        ? $"{guessed} (from the name)"
                        : real == guessed ? real : $"{real} (the name suggested {guessed})";
                });
            }, reading.Token);
        }

        private void copyPathButton_Click(object sender, RoutedEventArgs e) => copy(pathBox.Text);

        private void copyFolderButton_Click(object sender, RoutedEventArgs e)
            => copy((list.SelectedItem as GameAssets.Asset)?.Folder);

        private void copy(string? what)
        {
            if (string.IsNullOrEmpty(what)) { return; }

            try
            {
                Clipboard.SetText(what);
                statusLabel.Text = string.Format(R.ASSETS_COPIED, what);
            }
            catch (Exception problem)
            {
                //The clipboard belongs to whatever last asked for it and can refuse. Worth saying
                //rather than looking like the button does nothing.
                statusLabel.Text = problem.Message;
            }
        }
    }
}
