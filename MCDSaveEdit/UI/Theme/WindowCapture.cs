using System;
using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
#nullable enable

namespace MCDSaveEdit.UI.Theme
{
    /// <summary>
    /// Development aid for working on the theme: renders a window's visual tree to a PNG.
    /// Screen-grabbing a WPF window from outside the process returns a blank client area
    /// because the content is composited on the GPU, so this goes through
    /// RenderTargetBitmap instead, which sees exactly what WPF drew.
    ///
    /// Triggered with SCREENSHOT=&lt;path&gt; on the command line; the app exits afterwards.
    /// </summary>
    public static class WindowCapture
    {
        public const string ARGUMENT_PREFIX = "SCREENSHOT=";
        public const string TOGGLED_ARGUMENT_PREFIX = "SCREENSHOT_TOGGLED=";
        public const string ABOUT_ARGUMENT_PREFIX = "SCREENSHOT_ABOUT=";
        public const string TAB_ARGUMENT_PREFIX = "SCREENSHOT_TAB=";
        public const string SEARCH_ARGUMENT_PREFIX = "SCREENSHOT_SEARCH=";

        public static string? pathFromArguments(string[] arguments)
            => valueFor(arguments, ARGUMENT_PREFIX);

        /// <summary>
        /// Optional second capture taken after ThemeManager.toggle(). Proves the palette
        /// really is swapped on a live window rather than only at startup.
        /// </summary>
        public static string? toggledPathFromArguments(string[] arguments)
            => valueFor(arguments, TOGGLED_ARGUMENT_PREFIX);

        /// <summary>Capture the About dialog instead of the main window.</summary>
        public static string? aboutPathFromArguments(string[] arguments)
            => valueFor(arguments, ABOUT_ARGUMENT_PREFIX);

        /// <summary>Select a tab by index before capturing, so other screens can be shot.</summary>
        public static void selectTab(DependencyObject root, string[] arguments)
        {
            var value = valueFor(arguments, TAB_ARGUMENT_PREFIX);
            if (value == null || !int.TryParse(value, out int index)) { return; }

            var tabControl = findVisualChild<System.Windows.Controls.TabControl>(root);
            if (tabControl != null && index >= 0 && index < tabControl.Items.Count)
            {
                tabControl.SelectedIndex = index;
            }
        }

        /// <summary>Type into a named TextBox before capturing, to shoot a filtered state.</summary>
        public static void applySearch(FrameworkElement root, string[] arguments, string elementName)
        {
            var term = valueFor(arguments, SEARCH_ARGUMENT_PREFIX);
            if (term == null) { return; }

            if (root.FindName(elementName) is System.Windows.Controls.TextBox box)
            {
                box.Text = term;
                return;
            }
            //FindName only sees the window's own namescope, so fall back to a tree walk.
            var found = findVisualChildByName<System.Windows.Controls.TextBox>(root, elementName);
            if (found != null) { found.Text = term; }
        }

        /// <summary>
        /// Type into a window's search box, for shooting a filtered state.
        ///
        /// Two shapes to find: the shared SearchBox control, and the older bound TextBox that
        /// SelectionWindow uses for its item list.
        /// </summary>
        public static void typeInto(FrameworkElement root, string term)
        {
            var search = findVisualChild<MCDSaveEdit.UI.SearchBox>(root);
            if (search != null) { search.setTerm(term); return; }

            var box = findVisualChildByName<System.Windows.Controls.TextBox>(root, "textBox");
            if (box != null) { box.Text = term; }
        }

        /// <summary>Locate the first element of a type, for windows built without names.</summary>
        public static T? findFirst<T>(DependencyObject root) where T : DependencyObject
            => findVisualChild<T>(root);

        /// <summary>Locate any named element in a window, for diagnostics.</summary>
        public static FrameworkElement? findByName(FrameworkElement root, string name)
            => root.FindName(name) as FrameworkElement ?? findVisualChildByName<FrameworkElement>(root, name);

        private static T? findVisualChildByName<T>(DependencyObject parent, string name) where T : FrameworkElement
        {
            int count = VisualTreeHelper.GetChildrenCount(parent);
            for (int i = 0; i < count; i++)
            {
                var child = VisualTreeHelper.GetChild(parent, i);
                if (child is T match && match.Name == name) { return match; }
                var deeper = findVisualChildByName<T>(child, name);
                if (deeper != null) { return deeper; }
            }
            return null;
        }

        private static T? findVisualChild<T>(DependencyObject parent) where T : DependencyObject
        {
            int count = VisualTreeHelper.GetChildrenCount(parent);
            for (int i = 0; i < count; i++)
            {
                var child = VisualTreeHelper.GetChild(parent, i);
                if (child is T match) { return match; }
                var deeper = findVisualChild<T>(child);
                if (deeper != null) { return deeper; }
            }
            return null;
        }

        private static string? valueFor(string[] arguments, string prefix)
        {
            foreach (var argument in arguments)
            {
                if (argument.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                {
                    return argument.Substring(prefix.Length).Trim('"');
                }
            }
            return null;
        }

        /// <summary>
        /// Waits for the window to finish laying out and rendering, writes the PNG, then
        /// shuts the application down.
        /// </summary>
        public static void captureThenExit(Window window, string path, string? toggledPath = null)
        {
            //ApplicationIdle fires once the layout and render passes have drained, so the
            //captured frame is the settled one rather than a half-measured first pass.
            window.Dispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, new Action(() =>
            {
                try
                {
                    capture(window, path);
                    Console.WriteLine($"[screenshot] wrote {path}");

                    if (toggledPath != null)
                    {
                        ThemeManager.toggle();
                        window.UpdateLayout();
                        capture(window, toggledPath!);
                        Console.WriteLine($"[screenshot] wrote {toggledPath} after toggling to {ThemeManager.current}");
                    }
                }
                catch (Exception e)
                {
                    Console.WriteLine($"[screenshot] failed: {e.Message}");
                }
                Application.Current?.Shutdown();
            }));
        }

        public static void capture(Window window, string path)
        {
            window.UpdateLayout();

            //Capture the content root rather than the Window: ActualWidth/Height include
            //the non-client frame, which leaves a transparent strip in the PNG.
            var root = window.Content as FrameworkElement ?? (FrameworkElement)window;
            int width = (int)Math.Ceiling(root.ActualWidth);
            int height = (int)Math.Ceiling(root.ActualHeight);
            if (width <= 0 || height <= 0)
            {
                throw new InvalidOperationException($"window has no size yet ({width}x{height})");
            }

            var target = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);

            //Render the content through a DrawingVisual so the window's own Background is
            //included; rendering the Window directly can come back transparent.
            var visual = new DrawingVisual();
            using (var context = visual.RenderOpen())
            {
                var area = new Rect(0, 0, width, height);
                //The content root is usually transparent, so paint the window's own ground
                //first or the PNG comes back with the UI floating on nothing.
                if (window.Background != null) { context.DrawRectangle(window.Background, null, area); }
                var brush = new VisualBrush(root) { Stretch = Stretch.None, AlignmentX = AlignmentX.Left, AlignmentY = AlignmentY.Top };
                context.DrawRectangle(brush, null, area);
            }
            target.Render(visual);

            var directory = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(directory)) { Directory.CreateDirectory(directory!); }

            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(target));
            using var stream = File.Create(path);
            encoder.Save(stream);
        }
    }
}
