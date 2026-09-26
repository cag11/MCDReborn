using MCDSaveEdit.Data;
using System;
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
#nullable enable

namespace MCDSaveEdit.UI.Theme
{
    /// <summary>
    /// The main window's tabs as a side menu, in the manner of Windows 11's navigation pane.
    ///
    /// It is still the same TabControl - every tab, its name and everything that selects one
    /// is untouched - wearing Controls.xaml's NavTabControl style. What this adds is the part a
    /// style cannot hold: the icon and group heading of each entry, and when the menu is open.
    ///
    /// The pages were laid out for the full width of the window, so the menu only takes room
    /// when there is room to take. On a wide window it stands open beside the page, names and
    /// all. On a narrow one it is a strip of icons, and ☰ opens it OVER the page, closing again
    /// once a page is chosen - the page never gets narrower than it was under the old tab strip.
    /// </summary>
    public static class Nav
    {
        /// <summary>Wide enough for the menu at full width beside a page laid out for 1200.</summary>
        public const double DOCK_FROM = 1400;

        private const string OPEN_REGISTRY_KEY = "NavOpen";

        /// <summary>A Segoe Fluent Icons glyph for an entry.</summary>
        public static readonly DependencyProperty IconProperty = DependencyProperty.RegisterAttached(
            "Icon", typeof(string), typeof(Nav), new PropertyMetadata(null));

        public static string? GetIcon(DependencyObject o) => (string?)o.GetValue(IconProperty);
        public static void SetIcon(DependencyObject o, string? value) => o.SetValue(IconProperty, value);

        /// <summary>
        /// The heading above an entry that starts a group. Empty draws only the dividing line,
        /// for the entry pinned at the foot.
        /// </summary>
        public static readonly DependencyProperty GroupProperty = DependencyProperty.RegisterAttached(
            "Group", typeof(string), typeof(Nav), new PropertyMetadata(null));

        public static string? GetGroup(DependencyObject o) => (string?)o.GetValue(GroupProperty);
        public static void SetGroup(DependencyObject o, string? value) => o.SetValue(GroupProperty, value);

        /// <summary>Whether the menu shows names or only icons. Inherited, so each entry can follow.</summary>
        public static readonly DependencyProperty IsOpenProperty = DependencyProperty.RegisterAttached(
            "IsOpen", typeof(bool), typeof(Nav),
            new FrameworkPropertyMetadata(true, FrameworkPropertyMetadataOptions.Inherits));

        public static bool GetIsOpen(DependencyObject o) => (bool)o.GetValue(IsOpenProperty);
        public static void SetIsOpen(DependencyObject o, bool value) => o.SetValue(IsOpenProperty, value);

        /// <summary>Whether an open menu floats over the page rather than standing beside it.</summary>
        public static readonly DependencyProperty IsOverlayProperty = DependencyProperty.RegisterAttached(
            "IsOverlay", typeof(bool), typeof(Nav),
            new FrameworkPropertyMetadata(false, FrameworkPropertyMetadataOptions.Inherits));

        public static bool GetIsOverlay(DependencyObject o) => (bool)o.GetValue(IsOverlayProperty);
        public static void SetIsOverlay(DependencyObject o, bool value) => o.SetValue(IsOverlayProperty, value);

        /// <summary>
        /// Makes a TabControl wearing NavTabControl behave as the menu: open or not by the
        /// window's width and the last choice made on a wide one.
        /// </summary>
        public static void attach(TabControl tabs)
        {
            var wantOpen = loadOpen();
            var settling = true;

            void fit()
            {
                var overlay = tabs.ActualWidth > 0 && tabs.ActualWidth < DOCK_FROM;
                if (overlay == GetIsOverlay(tabs) && !settling) { return; }
                settling = false;
                SetIsOverlay(tabs, overlay);
                //A narrow window starts with the menu shut; a wide one as it was last left.
                SetIsOpen(tabs, !overlay && wantOpen);
            }

            tabs.SizeChanged += (_, _) => fit();

            //Chosen on a wide window, the choice is kept; on a narrow one opening is a way to
            //reach a page, not a preference.
            DependencyPropertyDescriptor.FromProperty(IsOpenProperty, typeof(TabControl))
                .AddValueChanged(tabs, (_, _) =>
                {
                    if (GetIsOverlay(tabs)) { return; }
                    wantOpen = GetIsOpen(tabs);
                    saveOpen(wantOpen);
                });

            tabs.SelectionChanged += (_, e) =>
            {
                if (e.OriginalSource == tabs && GetIsOverlay(tabs)) { SetIsOpen(tabs, false); }
            };

            //A click on the page, while the menu floats over it, puts the menu away.
            tabs.PreviewMouseDown += (_, e) =>
            {
                if (!GetIsOverlay(tabs) || !GetIsOpen(tabs)) { return; }
                if (tabs.Template?.FindName("rail", tabs) is FrameworkElement rail
                    && e.OriginalSource is DependencyObject clicked
                    && !rail.IsAncestorOf(clicked))
                {
                    SetIsOpen(tabs, false);
                }
            };

            tabs.PreviewKeyDown += (_, e) =>
            {
                if (e.Key == Key.Escape && GetIsOverlay(tabs) && GetIsOpen(tabs))
                {
                    SetIsOpen(tabs, false);
                    e.Handled = true;
                }
            };
        }

        private static bool loadOpen()
        {
            try
            {
                var saved = RegistryTools.GetSetting(Constants.APPLICATION_NAME, OPEN_REGISTRY_KEY, string.Empty);
                return !bool.TryParse(saved, out var open) || open;
            }
            catch (Exception) { return true; }
        }

        private static void saveOpen(bool open)
        {
            try { RegistryTools.SaveSetting(Constants.APPLICATION_NAME, OPEN_REGISTRY_KEY, open.ToString()); }
            catch (Exception e) { Console.WriteLine($"Could not save the menu state: {e.Message}"); }
        }
    }
}
