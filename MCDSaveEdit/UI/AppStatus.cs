using MCDSaveEdit.Logic;
using MCDSaveEdit.Services;
using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using Ellipse = System.Windows.Shapes.Ellipse;
using Shape = System.Windows.Shapes.Shape;
using System.Windows.Threading;
#nullable enable

namespace MCDSaveEdit.UI
{
    /// <summary>
    /// The strip along the foot of the main window: which save is open, whether the game is
    /// running, what the item plugin will register, and which game the paks came from.
    ///
    /// Each of these was a thing found out by trying. Whether the game was running came as a
    /// dialog on pressing Install, in five tabs; what the plugin holds was on the New Items tab
    /// only. Here they are always in view, and kept current: the game is looked for every few
    /// seconds, away from the window's thread, and the plugin's list is read again only when
    /// the file changes.
    /// </summary>
    public sealed class AppStatus : Border
    {
        private readonly TextBlock _save;
        private readonly Ellipse _gameDot;
        private readonly TextBlock _game;
        private readonly StackPanel _pluginPart;
        private readonly TextBlock _plugin;
        private readonly TextBlock _version;

        private readonly DispatcherTimer _timer;
        private bool _looking;
        private bool? _running;
        private DateTime _pluginSeen = DateTime.MinValue;

        /// <summary>Raised when the game starts or stops, for pages that care.</summary>
        public static event Action<bool>? gameRunningChanged;

        public AppStatus()
        {
            BorderThickness = new Thickness(0, 1, 0, 0);
            Padding = new Thickness(12, 3, 12, 4);
            SetResourceReference(BackgroundProperty, "Brush.Menu");
            SetResourceReference(BorderBrushProperty, "Brush.Border");

            var row = new DockPanel { LastChildFill = false };
            Child = row;

            _save = new TextBlock();
            row.Children.Add(part("", _save, Dock.Left));

            _gameDot = new Ellipse { Width = 8, Height = 8, Margin = new Thickness(0, 1, 7, 0), VerticalAlignment = VerticalAlignment.Center };
            _gameDot.SetResourceReference(Shape.FillProperty, "Brush.TextDisabled");
            _game = new TextBlock { Text = R.STATUS_GAME_CLOSED };
            var game = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 22, 0), Background = System.Windows.Media.Brushes.Transparent };
            game.Children.Add(_gameDot);
            game.Children.Add(muted(_game));
            game.ToolTip = R.STATUS_GAME_WHY;
            DockPanel.SetDock(game, Dock.Left);
            row.Children.Add(game);

            _plugin = new TextBlock();
            _pluginPart = part("", _plugin, Dock.Left);
            _pluginPart.Visibility = Visibility.Collapsed;
            row.Children.Add(_pluginPart);

            _version = new TextBlock();
            var version = part("", _version, Dock.Right);
            version.Margin = new Thickness(0);
            row.Children.Add(version);

            _timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(3) };
            _timer.Tick += (_, _) => refresh();
            Loaded += (_, _) => { refresh(); _timer.Start(); };
            Unloaded += (_, _) => _timer.Stop();
            setSave(null);
        }

        /// <summary>The open save, by name; the whole path on the pointer.</summary>
        public void setSave(string? path)
        {
            _save.Text = path == null ? R.STATUS_NO_SAVE : Path.GetFileName(path);
            ((FrameworkElement)_save.Parent).ToolTip = path;
        }

        /// <summary>The game the paks were read from, and where.</summary>
        public void setGame(string version, string? folder)
        {
            _version.Text = version;
            ((FrameworkElement)_version.Parent).ToolTip = folder;
        }

        /// <summary>Looks again now, rather than at the next tick - after an install, say.</summary>
        public void refresh()
        {
            refreshPlugin();
            if (_looking) { return; }
            _looking = true;
            Task.Run(() => GameRunning.isUp).ContinueWith(done =>
            {
                _looking = false;
                if (done.IsFaulted) { return; }
                var running = done.Result;
                if (running == _running) { return; }
                var first = _running == null;
                _running = running;
                _game.Text = running ? R.STATUS_GAME_RUNNING : R.STATUS_GAME_CLOSED;
                _gameDot.SetResourceReference(Shape.FillProperty, running ? "Brush.Accent" : "Brush.TextDisabled");
                if (!first) { gameRunningChanged?.Invoke(running); }
            }, TaskScheduler.FromCurrentSynchronizationContext());
        }

        /// <summary>
        /// What the plugin's list holds: items, enchantments and mobs. Hidden where the plugin
        /// cannot go at all - the Microsoft Store build - since there is nothing to report there.
        /// </summary>
        private void refreshPlugin()
        {
            string? folder;
            try { folder = GamePlugin.gameFolder(); }
            catch (Exception) { folder = null; }
            if (folder == null) { _pluginPart.Visibility = Visibility.Collapsed; return; }
            _pluginPart.Visibility = Visibility.Visible;

            var list = Path.Combine(folder, GamePlugin.ITEMS_NAME);
            var log = Path.Combine(folder, GamePlugin.LOG_NAME);
            DateTime changed;
            try { changed = new[] { list, log }.Where(File.Exists).Select(File.GetLastWriteTimeUtc).DefaultIfEmpty(DateTime.MinValue).Max(); }
            catch (Exception) { return; }
            if (changed == _pluginSeen && _plugin.Text.Length > 0) { return; }
            _pluginSeen = changed;

            if (!File.Exists(list))
            {
                _plugin.Text = R.STATUS_PLUGIN_NONE;
                _pluginPart.ToolTip = null;
                return;
            }
            string[] lines;
            try
            {
                using var stream = new FileStream(list, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                using var reader = new StreamReader(stream);
                lines = reader.ReadToEnd().Split('\n').Select(l => l.TrimEnd('\r')).Where(l => l.Length > 0 && !l.StartsWith("#", StringComparison.Ordinal)).ToArray();
            }
            catch (Exception) { return; }
            var enchantments = lines.Count(l => l.StartsWith("@enchantment\t", StringComparison.Ordinal));
            var mobs = lines.Count(l => l.StartsWith("@mob\t", StringComparison.Ordinal));
            var items = lines.Count(l => !l.StartsWith("@", StringComparison.Ordinal));
            _plugin.Text = string.Format(R.STATUS_PLUGIN, items, enchantments, mobs);

            //What happened the last time the game started with it, on the pointer.
            var last = GamePlugin.lastLog()?.LastOrDefault(l => l.Contains("done:") || l.Contains("nothing was changed") || l.Contains("NOT "));
            if (last != null && last.Length > 14 && last[2] == ':') { last = last.Substring(14).Trim(); }
            _pluginPart.ToolTip = last == null ? R.ITEMS_PLUGIN_NEVER : string.Format(R.ITEMS_PLUGIN_LAST, last);
        }

        private static StackPanel part(string glyph, TextBlock text, Dock dock)
        {
            var icon = new TextBlock { Text = glyph, FontSize = 12, Margin = new Thickness(0, 1, 7, 0), VerticalAlignment = VerticalAlignment.Center };
            icon.SetResourceReference(TextBlock.FontFamilyProperty, "Font.Icons");
            icon.SetResourceReference(TextBlock.ForegroundProperty, "Brush.TextMuted");
            var panel = new StackPanel {
                Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 22, 0),
                Background = System.Windows.Media.Brushes.Transparent,
            };
            panel.Children.Add(icon);
            panel.Children.Add(muted(text));
            DockPanel.SetDock(panel, dock);
            return panel;
        }

        private static TextBlock muted(TextBlock text)
        {
            text.FontSize = 12;
            text.VerticalAlignment = VerticalAlignment.Center;
            text.SetResourceReference(TextBlock.ForegroundProperty, "Brush.TextMuted");
            return text;
        }
    }
}
