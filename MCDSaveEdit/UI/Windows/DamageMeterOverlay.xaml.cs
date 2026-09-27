using MCDSaveEdit.Logic;
using MCDSaveEdit.Services;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
#nullable enable

namespace MCDSaveEdit.UI
{
    /// <summary>
    /// The damage meter, drawn over the game in the game's own look: a square dark panel with a
    /// light frame and corner studs, gold small capitals for the heading, the game's pixel fonts
    /// with their hard shadow, and a bar for each of the last thirty seconds.
    ///
    /// A window of its own rather than anything inside the game, for the reasons the escalation
    /// clock gives: it sits over the game's window, never takes focus and lets every click through.
    /// Right-hand side, a third of the way down, clear of the hotbar and the objectives.
    /// </summary>
    public partial class DamageMeterOverlay : Window
    {
        private const int GWL_EXSTYLE = -20;
        private const int WS_EX_TRANSPARENT = 0x00000020;
        private const int WS_EX_LAYERED = 0x00080000;
        private const int WS_EX_TOOLWINDOW = 0x00000080;
        private const int WS_EX_NOACTIVATE = 0x08000000;

        [DllImport("user32.dll")] private static extern int GetWindowLong(IntPtr window, int index);
        [DllImport("user32.dll")] private static extern int SetWindowLong(IntPtr window, int index, int value);
        [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr window, out Rect area);

        [StructLayout(LayoutKind.Sequential)]
        private struct Rect { public int Left, Top, Right, Bottom; }

        private static readonly Brush GOLD = frozen(0xFF, 0xE8, 0xA3, 0x3D);
        private static readonly Brush GOLD_BRIGHT = frozen(0xFF, 0xFF, 0xD3, 0x7A);
        private static readonly Brush EMPTY = frozen(0xFF, 0x2C, 0x2F, 0x35);
        private const double BAR_HEIGHT = 31;

        private readonly LiveStatsLink? _live;
        private readonly DamageMeter _meter;
        private readonly DispatcherTimer _tick;
        private readonly List<Border> _bars = new();
        private readonly bool _preview;

        public DamageMeterOverlay(LiveStatsLink live) : this(live, live.damage, false) { }

        /// <summary>A meter that shows made-up numbers where no game is running - for checking the look.</summary>
        public static DamageMeterOverlay preview() => new DamageMeterOverlay(null, new DamageMeter(), true);

        private DamageMeterOverlay(LiveStatsLink? live, DamageMeter meter, bool preview)
        {
            InitializeComponent();
            _live = live;
            _meter = meter;
            _preview = preview;

            titleLabel.FontFamily = GameFonts.Five;
            stateLabel.FontFamily = GameFonts.Five;
            dpsLabel.FontFamily = GameFonts.Ten;
            dpsUnit.FontFamily = GameFonts.Five;
            foreach (var text in new[] { firstName, firstValue, secondName, secondValue }) { text.FontFamily = GameFonts.Seven; }
            titleLabel.Text = R.DPS_TITLE;
            dpsUnit.Text = R.DPS_UNIT;

            for (var i = 0; i < DamageMeter.HISTORY; i++)
            {
                var bar = new Border { Width = 6, Height = 2, Margin = new Thickness(i == 0 ? 0 : 1, 0, 0, 0), Background = EMPTY, VerticalAlignment = VerticalAlignment.Bottom };
                _bars.Add(bar);
                bars.Items.Add(bar);
            }

            _tick = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(150) };
            _tick.Tick += (_, _) => show();
            Loaded += (_, _) => { if (!_preview) { passClicksThrough(); } _tick.Start(); show(); };
            Closed += (_, _) => _tick.Stop();
        }

        private void passClicksThrough()
        {
            var handle = new WindowInteropHelper(this).Handle;
            var was = GetWindowLong(handle, GWL_EXSTYLE);
            SetWindowLong(handle, GWL_EXSTYLE, was | WS_EX_LAYERED | WS_EX_TRANSPARENT | WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE);
        }

        private void show()
        {
            if (!_preview && !sitOverTheGame()) { return; }

            var inFight = _preview || _meter.InFight;
            var history = _preview ? (IReadOnlyList<double>)PREVIEW_HISTORY : _meter.History;
            var dps = _preview ? 18420 : _meter.Dps;

            stateLabel.Text = inFight ? R.DPS_IN_FIGHT : R.DPS_IDLE;
            stateLabel.Foreground = inFight ? GOLD : new SolidColorBrush(Color.FromRgb(0x8E, 0x93, 0x9C));

            if (inFight)
            {
                dpsLabel.Text = DamageMeter.shortly(dps);
                firstName.Text = R.DPS_FIGHT;
                firstValue.Text = string.Format(R.DPS_TOTAL_IN, DamageMeter.shortly(_preview ? 221040 : _meter.FightTotal),
                    clock(_preview ? 12 : _meter.FightSeconds));
                secondName.Text = R.DPS_PEAK;
                secondValue.Text = DamageMeter.shortly(_preview ? 24310 : _meter.FightPeak) + " " + R.DPS_UNIT;
            }
            else
            {
                dpsLabel.Text = _meter.LastFightDps > 0 ? DamageMeter.shortly(_meter.LastFightDps) : "0";
                firstName.Text = R.DPS_LAST;
                firstValue.Text = _meter.LastFightTotal > 0 ? DamageMeter.shortly(_meter.LastFightTotal) : "-";
                secondName.Text = R.DPS_PEAK;
                secondValue.Text = _meter.FightPeak > 0 ? DamageMeter.shortly(_meter.FightPeak) + " " + R.DPS_UNIT : "-";
            }

            //Scaled to the busiest second shown, so a build's rhythm reads whatever its size.
            var top = Math.Max(1.0, history.Max());
            for (var i = 0; i < _bars.Count; i++)
            {
                var value = history[i];
                var bar = _bars[i];
                bar.Height = value <= 0 ? 2 : Math.Max(3, Math.Round(BAR_HEIGHT * value / top));
                bar.Background = value <= 0 ? EMPTY : i == _bars.Count - 1 ? GOLD_BRIGHT : GOLD;
            }
        }

        private static string clock(double seconds)
        {
            var whole = (int)Math.Round(seconds);
            return $"{whole / 60}:{whole % 60:00}";
        }

        /// <summary>Over the game's right-hand side, or hidden while there is no game to be over.</summary>
        private bool sitOverTheGame()
        {
            var game = _live?.gameWindow ?? IntPtr.Zero;
            if (game == IntPtr.Zero || !GetWindowRect(game, out var area))
            {
                Visibility = Visibility.Collapsed;
                return false;
            }
            Visibility = Visibility.Visible;

            var source = PresentationSource.FromVisual(this);
            var scaleX = source?.CompositionTarget?.TransformToDevice.M11 ?? 1.0;
            var scaleY = source?.CompositionTarget?.TransformToDevice.M22 ?? 1.0;
            if (scaleX <= 0) { scaleX = 1.0; }
            if (scaleY <= 0) { scaleY = 1.0; }

            Left = area.Right / scaleX - Width - 24;
            Top = area.Top / scaleY + (area.Bottom - area.Top) / scaleY * 0.30;
            return true;
        }

        private static readonly double[] PREVIEW_HISTORY =
        {
            0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0,
            9200, 21400, 16800, 24310, 18800, 12600, 19900, 22400, 15100, 17700, 20500, 18900, 18420,
        };

        private static Brush frozen(byte a, byte r, byte g, byte b)
        {
            var brush = new SolidColorBrush(Color.FromArgb(a, r, g, b));
            brush.Freeze();
            return brush;
        }
    }
}
