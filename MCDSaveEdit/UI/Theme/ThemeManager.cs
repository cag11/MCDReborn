using MCDSaveEdit.Data;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
#nullable enable

namespace MCDSaveEdit.UI.Theme
{
    public enum AppTheme
    {
        Dark,
        Light,
        Nether,
        End,
        Frost,
    }

    /// <summary>
    /// Swaps the palette dictionary at runtime and remembers the choice.
    ///
    /// Only the palette is swapped; Controls.xaml stays loaded and refers to the palette
    /// with DynamicResource, so every themed control repaints without being rebuilt and
    /// without restarting the app.
    /// </summary>
    public static class ThemeManager
    {
        public const string THEME_REGISTRY_KEY = "Theme";

        //Adding a theme is a palette file plus one line here; Controls.xaml never changes.
        private static readonly IReadOnlyDictionary<AppTheme, string> PALETTES =
            new Dictionary<AppTheme, string> {
                [AppTheme.Dark]   = "UI/Theme/Palette.Dark.xaml",
                [AppTheme.Light]  = "UI/Theme/Palette.Light.xaml",
                [AppTheme.Nether] = "UI/Theme/Palette.Nether.xaml",
                [AppTheme.End]    = "UI/Theme/Palette.End.xaml",
                [AppTheme.Frost]  = "UI/Theme/Palette.Frost.xaml",
            };

        public static IEnumerable<AppTheme> allThemes => PALETTES.Keys;

        public static AppTheme current { get; private set; } = AppTheme.Dark;

        /// <summary>
        /// Whether the active palette is a dark one, measured from its own ground colour
        /// rather than listed here - a new palette then needs no extra wiring, and a palette
        /// that is retuned cannot drift out of step with a hard-coded list.
        /// </summary>
        public static bool isDark
        {
            get
            {
                if (Application.Current?.TryFindResource("Brush.Ground") is SolidColorBrush brush)
                {
                    //Rec. 601 luma against the usual half-way point.
                    var color = brush.Color;
                    var luma = (0.299 * color.R + 0.587 * color.G + 0.114 * color.B) / 255.0;
                    return luma < 0.5;
                }
                //Dark is the default theme, so assume dark when the palette is not up yet.
                return true;
            }
        }

        /// <summary>Raised after the palette has been swapped.</summary>
        public static event Action<AppTheme>? themeChanged;

        /// <summary>
        /// Applies the saved theme, defaulting to dark. Call once at startup, before any
        /// window is shown.
        /// </summary>
        public static void initialize()
        {
            apply(loadSavedTheme());
            useThemeFont();
        }

        private static bool _fontHooked;

        /// <summary>
        /// Every window in Controls.xaml's Font.Body - Windows 11's Segoe UI Variable - unless
        /// the window picks a font of its own.
        ///
        /// A window's font is what everything in it inherits, and an implicit style cannot reach
        /// it: styles match the exact type, and every window here is a subclass of Window. So it
        /// is set on each one as it loads, by reference, so a later change to the resource
        /// follows.
        /// </summary>
        private static void useThemeFont()
        {
            if (_fontHooked) { return; }
            _fontHooked = true;
            EventManager.RegisterClassHandler(typeof(Window), FrameworkElement.LoadedEvent, new RoutedEventHandler((sender, _) =>
            {
                if (sender is Window window
                    && window.ReadLocalValue(Control.FontFamilyProperty) == DependencyProperty.UnsetValue)
                {
                    window.SetResourceReference(Control.FontFamilyProperty, "Font.Body");
                }
            }));
        }

        public static void apply(AppTheme theme)
        {
            var application = Application.Current;
            if (application == null) { return; }

            var dictionaries = application.Resources.MergedDictionaries;
            if (!PALETTES.TryGetValue(theme, out var path)) { return; }
            var wanted = new Uri(path, UriKind.Relative);

            //Drop whichever palette is loaded, then insert the new one at the front so the
            //control styles that follow it can resolve the brushes.
            var existing = dictionaries.FirstOrDefault(isPalette);
            var replacement = new ResourceDictionary { Source = wanted };

            if (existing != null)
            {
                dictionaries[dictionaries.IndexOf(existing)] = replacement;
            }
            else
            {
                dictionaries.Insert(0, replacement);
            }

            current = theme;
            saveTheme(theme);
            themeChanged?.Invoke(theme);
        }

        /// <summary>Steps to the next theme in declaration order, wrapping at the end.</summary>
        public static void toggle()
        {
            var order = new List<AppTheme>(PALETTES.Keys);
            var next = (order.IndexOf(current) + 1) % order.Count;
            apply(order[next]);
        }

        private static bool isPalette(ResourceDictionary dictionary)
        {
            var source = dictionary.Source?.OriginalString;
            if (source == null) { return false; }
            foreach (var path in PALETTES.Values)
            {
                var file = path.Substring(path.LastIndexOf('/') + 1);
                if (source.EndsWith(file, StringComparison.OrdinalIgnoreCase)) { return true; }
            }
            return false;
        }

        private static AppTheme loadSavedTheme()
        {
            try
            {
                var saved = RegistryTools.GetSetting(Constants.APPLICATION_NAME, THEME_REGISTRY_KEY, string.Empty);
                if (Enum.TryParse<AppTheme>(saved, ignoreCase: true, out var theme)) { return theme; }
            }
            catch (Exception e)
            {
                //A missing or unreadable registry key is not worth failing startup over.
                Console.WriteLine($"Could not read the saved theme: {e.Message}");
            }
            //Dark is the default: it is what the game looks like.
            return AppTheme.Dark;
        }

        private static void saveTheme(AppTheme theme)
        {
            try
            {
                RegistryTools.SaveSetting(Constants.APPLICATION_NAME, THEME_REGISTRY_KEY, theme.ToString());
            }
            catch (Exception e)
            {
                Console.WriteLine($"Could not save the theme: {e.Message}");
            }
        }
    }
}
