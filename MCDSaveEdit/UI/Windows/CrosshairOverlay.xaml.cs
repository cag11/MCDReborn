using MCDSaveEdit.Logic;
using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Threading;
#nullable enable

namespace MCDSaveEdit.UI
{
    /// <summary>
    /// A crosshair, in a game that has none.
    ///
    /// It does not need one: the camera looks down at a character who aims at whatever the cursor
    /// is over, so the cursor is the crosshair. First person takes both away - the pointer is
    /// pinned to the middle of the screen and hidden, and what is being aimed at is wherever the
    /// camera points - and the middle of the screen becomes a place you have to guess at.
    ///
    /// The same trick as the escalation bar: a layered, click-through window over the game, which
    /// works because this game runs borderless.
    ///
    /// Drawn from code rather than laid out in markup, because the shape is a choice and there are
    /// six of them. Every one is drawn twice - a dark wide stroke under the bright one - because a
    /// single colour disappears against half the floors in this game, and a crosshair that
    /// vanishes over sand is worse than none.
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

        /// <summary>The shapes on offer, in the order they are listed.</summary>
        public static readonly string[] STYLES = { "Cross", "Chevron", "Circle", "Dot", "Brackets" };

        /// <summary>
        /// The colours on offer.
        ///
        /// Bright and saturated, because this is drawn over a game rather than over a document.
        /// Green first: it is the one that stays legible over stone, sand, grass and lava alike,
        /// which is most of what this game is made of.
        /// </summary>
        public static readonly (string name, Color colour)[] COLOURS = {
            ("Green", Color.FromRgb(0x4C, 0xFF, 0x5A)),
            ("White", Color.FromRgb(0xFF, 0xFF, 0xFF)),
            ("Red", Color.FromRgb(0xFF, 0x3B, 0x30)),
            ("Cyan", Color.FromRgb(0x3C, 0xE0, 0xFF)),
            ("Yellow", Color.FromRgb(0xFF, 0xD1, 0x2E)),
            ("Magenta", Color.FromRgb(0xFF, 0x4C, 0xE0)),
            ("Orange", Color.FromRgb(0xFF, 0x8A, 0x1E)),
            ("Black", Color.FromRgb(0x10, 0x10, 0x10)),
        };

        private readonly LiveCameraLink _live;
        private readonly DispatcherTimer _tick;
        private static readonly TimeSpan EVERY = TimeSpan.FromMilliseconds(250);

        private string _style = "Cross";
        private string _colour = "Green";
        private double _size = 1.0;

        public CrosshairOverlay(LiveCameraLink live)
        {
            InitializeComponent();
            _live = live;

            _tick = new DispatcherTimer { Interval = EVERY };
            _tick.Tick += (_, _) => sitOverTheGame();

            Loaded += (_, _) => { passClicksThrough(); draw(); _tick.Start(); sitOverTheGame(); };
            Closed += (_, _) => _tick.Stop();
        }

        /// <summary>Changes what is drawn, while it is being looked at.</summary>
        public void look(string style, string colour, double size)
        {
            _style = string.IsNullOrWhiteSpace(style) ? "Cross" : style;
            _colour = string.IsNullOrWhiteSpace(colour) ? "Green" : colour;
            _size = size <= 0 ? 1.0 : size;
            if (IsLoaded) { draw(); }
        }

        private static Color colourNamed(string name)
        {
            foreach (var (known, colour) in COLOURS)
            {
                if (string.Equals(known, name, StringComparison.OrdinalIgnoreCase)) { return colour; }
            }
            return COLOURS[0].colour;
        }

        //--------------------------------------------------------------------------- the drawing

        private void draw()
        {
            board.Children.Clear();

            var bright = new SolidColorBrush(colourNamed(_colour));
            bright.Freeze();

            //Not pure black: a hard black edge reads as a sticker on the screen, while a dark
            //translucent one reads as a shadow under the mark.
            var under = new SolidColorBrush(Color.FromArgb(0xB4, 0, 0, 0));
            under.Freeze();

            var middle = new Point(Width / 2, Height / 2);

            //Two passes, the dark one first and wider, so the bright shape sits inside its own
            //outline rather than beside it.
            foreach (var pass in new[] { true, false })
            {
                var brush = pass ? under : bright;
                var extra = pass ? 3.0 : 0.0;

                foreach (var shape in shapesFor(_style, middle, _size, brush, extra))
                {
                    board.Children.Add(shape);
                }
            }
        }

        private static IEnumerable<Shape> shapesFor(string style, Point middle, double size,
            Brush brush, double extra)
        {
            switch (style)
            {
                case "Cross":
                    return new[] {
                        bar(middle, 0, -9 * size, 0, -42 * size, 3, size, brush, extra),
                        bar(middle, 0, 9 * size, 0, 42 * size, 3, size, brush, extra),
                        bar(middle, -9 * size, 0, -42 * size, 0, 3, size, brush, extra),
                        bar(middle, 9 * size, 0, 42 * size, 0, 3, size, brush, extra),
                    };

                //Two strokes meeting under the middle, so the point of aim is the empty space
                //above them rather than anything covered up.
                case "Chevron":
                    return new Shape[] {
                        //Sat about the middle rather than under it, with the point below and the
                        //dot in the mouth of the V - which is where the eye goes.
                        bar(middle, -27 * size, -10 * size, 0, 17 * size, 6, size, brush, extra),
                        bar(middle, 27 * size, -10 * size, 0, 17 * size, 6, size, brush, extra),
                        dot(middle, 3 * size, brush, extra),
                    };

                case "Circle":
                    return new Shape[] {
                        ring(middle, 36 * size, 3, size, brush, extra),
                        dot(middle, 3.5 * size, brush, extra),
                    };

                case "Dot":
                    return new[] { dot(middle, 6.5 * size, brush, extra) };

                //Corners of a box, which frames what is being aimed at without crossing it.
                default:
                    var reach = 36 * size;
                    var arm = 15 * size;
                    return new[] {
                        bar(middle, -reach, -reach, -reach + arm, -reach, 4.5, size, brush, extra),
                        bar(middle, -reach, -reach, -reach, -reach + arm, 4.5, size, brush, extra),
                        bar(middle, reach, -reach, reach - arm, -reach, 4.5, size, brush, extra),
                        bar(middle, reach, -reach, reach, -reach + arm, 4.5, size, brush, extra),
                        bar(middle, -reach, reach, -reach + arm, reach, 4.5, size, brush, extra),
                        bar(middle, -reach, reach, -reach, reach - arm, 4.5, size, brush, extra),
                        bar(middle, reach, reach, reach - arm, reach, 4.5, size, brush, extra),
                        bar(middle, reach, reach, reach, reach - arm, 4.5, size, brush, extra),
                    };
            }
        }

        private static Line bar(Point middle, double x1, double y1, double x2, double y2,
            double thickness, double size, Brush brush, double extra)
            => new Line {
                X1 = middle.X + x1, Y1 = middle.Y + y1,
                X2 = middle.X + x2, Y2 = middle.Y + y2,
                Stroke = brush,
                StrokeThickness = thickness * size + extra,
                StrokeStartLineCap = PenLineCap.Round,
                StrokeEndLineCap = PenLineCap.Round,
            };

        private static Ellipse dot(Point middle, double radius, Brush brush, double extra)
        {
            var r = radius + extra / 2;
            return new Ellipse {
                Width = r * 2, Height = r * 2, Fill = brush,
                Margin = new Thickness(middle.X - r, middle.Y - r, 0, 0),
                HorizontalAlignment = HorizontalAlignment.Left,
                VerticalAlignment = VerticalAlignment.Top,
            };
        }

        private static Ellipse ring(Point middle, double radius, double thickness, double size,
            Brush brush, double extra)
            => new Ellipse {
                Width = radius * 2, Height = radius * 2,
                Stroke = brush, StrokeThickness = thickness * size + extra,
                Margin = new Thickness(middle.X - radius, middle.Y - radius, 0, 0),
                HorizontalAlignment = HorizontalAlignment.Left,
                VerticalAlignment = VerticalAlignment.Top,
            };

        //------------------------------------------------------------------------- where it sits

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
        /// The middle of the game's window rather than of the screen, which are the same thing
        /// until somebody plays windowed or on a second monitor.
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
