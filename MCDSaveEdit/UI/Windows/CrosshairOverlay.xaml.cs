using MCDSaveEdit.Logic;
using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Threading;
#nullable enable

namespace MCDSaveEdit.UI
{
    /// <summary>
    /// A crosshair, in a game that has none.
    ///
    /// It does not need one: the camera looks down at a character who aims at whatever the cursor
    /// is over, so the cursor is the crosshair. First person takes both of those away - the cursor
    /// is pinned to the middle of the screen and hidden, and what is being aimed at is wherever the
    /// camera points - and the middle of the screen becomes a place you have to guess at.
    ///
    /// The same trick as the escalation bar: a layered, click-through window over the game, which
    /// works because this game runs borderless. It is drawn rather than an image so that it stays
    /// sharp at any scaling, and twice over - a dark wide stroke under a bright thin one - because
    /// a single colour disappears against half the floors in this game.
    /// </summary>
    public partial class CrosshairOverlay : Window
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

        private readonly LiveCameraLink _live;
        private readonly DispatcherTimer _tick;

        //Slower than the escalation bar, which has a moving clock on it. This only has to keep up
        //with a window being dragged.
        private static readonly TimeSpan EVERY = TimeSpan.FromMilliseconds(250);

        public CrosshairOverlay(LiveCameraLink live)
        {
            InitializeComponent();
            _live = live;

            _tick = new DispatcherTimer { Interval = EVERY };
            _tick.Tick += (_, _) => sitOverTheGame();

            Loaded += (_, _) => { passClicksThrough(); _tick.Start(); sitOverTheGame(); };
            Closed += (_, _) => _tick.Stop();
        }

        private void passClicksThrough()
        {
            var handle = new WindowInteropHelper(this).Handle;
            var was = GetWindowLong(handle, GWL_EXSTYLE);
            SetWindowLong(handle, GWL_EXSTYLE,
                was | WS_EX_LAYERED | WS_EX_TRANSPARENT | WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE);
        }

        /// <summary>
        /// Centres it on the game, and hides while there is no game to be centred on.
        ///
        /// The middle of the game's window rather than the middle of the screen, which are the
        /// same thing until somebody plays windowed or on a second monitor.
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

            var source = PresentationSource.FromVisual(this);
            var scaleX = source?.CompositionTarget?.TransformToDevice.M11 ?? 1.0;
            var scaleY = source?.CompositionTarget?.TransformToDevice.M22 ?? 1.0;
            if (scaleX <= 0) { scaleX = 1.0; }
            if (scaleY <= 0) { scaleY = 1.0; }

            Left = (area.Left + area.Right) / 2.0 / scaleX - Width / 2.0;
            Top = (area.Top + area.Bottom) / 2.0 / scaleY - Height / 2.0;
        }
    }
}
