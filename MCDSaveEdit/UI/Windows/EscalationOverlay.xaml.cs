using MCDSaveEdit.Logic;
using MCDSaveEdit.Services;
using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Threading;
#nullable enable

namespace MCDSaveEdit.UI
{
    /// <summary>
    /// The escalation clock, drawn on top of the game.
    ///
    /// A window rather than anything inside the game, because nothing can be drawn inside it from
    /// out here - this project does not inject, so the only surface available is one of Windows'
    /// own. It sits over the game's window, it is never focused, and clicks pass straight through
    /// it to whatever is underneath.
    ///
    /// It only works over a borderless window, which is how this game runs by default - measured:
    /// 1920 by 1080 at the origin with no caption. In *exclusive* fullscreen the display belongs
    /// to the game and nothing can be put over it. That is a rule of the platform rather than
    /// something to work around, so the overlay simply will not be seen there.
    ///
    /// Positioned against the game's own window rather than the screen, so that a windowed game,
    /// a second monitor or a different resolution all put it in the same place relative to what it
    /// is annotating.
    /// </summary>
    public partial class EscalationOverlay : Window
    {
        //Window styles. Layered and transparent together are what make a window click through:
        //the pointer finds whatever is behind it, so the game never loses a click to this.
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

        private readonly LiveStatsLink _live;
        private readonly DispatcherTimer _tick;

        //Often enough that a bar moves smoothly and a clock counts in whole seconds without
        //stuttering, and far short of anything worth measuring.
        private static readonly TimeSpan EVERY = TimeSpan.FromMilliseconds(100);

        public EscalationOverlay(LiveStatsLink live)
        {
            InitializeComponent();
            _live = live;

            _tick = new DispatcherTimer { Interval = EVERY };
            _tick.Tick += (_, _) => show();

            Loaded += (_, _) => { passClicksThrough(); _tick.Start(); show(); };
            Closed += (_, _) => _tick.Stop();
        }

        private void passClicksThrough()
        {
            var handle = new WindowInteropHelper(this).Handle;
            var was = GetWindowLong(handle, GWL_EXSTYLE);
            SetWindowLong(handle, GWL_EXSTYLE,
                was | WS_EX_LAYERED | WS_EX_TRANSPARENT | WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE);
        }

        private void show()
        {
            sitOverTheGame();

            var escalation = _live.escalation;

            stageLabel.Text = string.Format(R.ESCALATION_STAGE, escalation.stage + 1, escalation.stageName);
            numbersLabel.Text = string.Format(R.ESCALATION_NUMBERS, _live.toughnessNow, _live.speedNow);

            //Counting down to the next one, which is the number that decides whether to open the
            //next chest or leave.
            var left = escalation.untilNext;
            clockLabel.Text = _live.escalation.atTheTop(_live.enemyToughness, _live.enemySpeed)
                ? R.ESCALATION_TOPPED
                : string.Format(R.ESCALATION_NEXT, (int)left.TotalMinutes, left.Seconds);

            //The track is the border's inner width, so the fill is measured against that rather
            //than the window, which has padding either side.
            var track = Math.Max(0, ActualWidth - 26);
            barFill.Width = track * escalation.through;
        }

        /// <summary>
        /// Puts it over the game, or hides it while there is no game to be over.
        ///
        /// Followed every tick rather than positioned once, because the game can be moved, resized
        /// or alt-tabbed away from, and an overlay pinned where the window used to be is worse
        /// than none.
        /// </summary>
        private void sitOverTheGame()
        {
            var game = _live.gameWindow;
            if (game == IntPtr.Zero || !GetWindowRect(game, out var area))
            {
                Visibility = Visibility.Collapsed;
                return;
            }

            Visibility = Visibility.Visible;

            //Windows gives pixels and WPF wants its own units, which differ the moment anything
            //is scaled - a display at 150 per cent would put this a third of the way off screen.
            var source = PresentationSource.FromVisual(this);
            var scaleX = source?.CompositionTarget?.TransformToDevice.M11 ?? 1.0;
            var scaleY = source?.CompositionTarget?.TransformToDevice.M22 ?? 1.0;
            if (scaleX <= 0) { scaleX = 1.0; }
            if (scaleY <= 0) { scaleY = 1.0; }

            var middle = (area.Left + area.Right) / 2.0 / scaleX;
            Left = middle - Width / 2.0;
            Top = area.Top / scaleY + 18;
        }
    }
}
