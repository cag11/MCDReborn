using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
#nullable enable

namespace MCDSaveEdit.UI.Theme
{
    /// <summary>
    /// Paints the OS title bar from the active palette, so the window chrome stops being a
    /// white strip above a dark app.
    ///
    /// WPF has no managed API for this; it goes through DWM. Windows 11 (build 22000+) added
    /// caption, text and border colour attributes, which is what lets the bar take the
    /// theme's actual colour rather than a generic dark. On older Windows those attributes
    /// are rejected and only the immersive dark-mode flag applies, which still beats white.
    /// Every call is best-effort: an unsupported attribute returns non-zero and is ignored.
    /// </summary>
    public static class TitleBarTheme
    {
        //Windows 10 1809+. Builds 18985-19041 used 19 instead, hence the fallback.
        private const int DWMWA_USE_IMMERSIVE_DARK_MODE = 20;
        private const int DWMWA_USE_IMMERSIVE_DARK_MODE_LEGACY = 19;

        //Windows 11 22000+.
        private const int DWMWA_BORDER_COLOR = 34;
        private const int DWMWA_CAPTION_COLOR = 35;
        private const int DWMWA_TEXT_COLOR = 36;

        [DllImport("dwmapi.dll", PreserveSig = true)]
        private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);

        /// <summary>Applies the current palette to one window, now or once it has a handle.</summary>
        public static void apply(Window? window)
        {
            if (window == null) { return; }

            var handle = new WindowInteropHelper(window).Handle;
            if (handle == IntPtr.Zero)
            {
                window.SourceInitialized += (s, e) => applyToHandle(new WindowInteropHelper(window).Handle);
                return;
            }
            applyToHandle(handle);
        }

        /// <summary>Re-paints every open window, for when the theme changes under them.</summary>
        public static void applyToAllWindows()
        {
            var application = Application.Current;
            if (application == null) { return; }

            foreach (Window window in application.Windows)
            {
                apply(window);
            }
        }

        private static void applyToHandle(IntPtr handle)
        {
            if (handle == IntPtr.Zero) { return; }

            try
            {
                //Drives the colour of the minimise/maximise/close glyphs.
                int dark = ThemeManager.isDark ? 1 : 0;
                if (DwmSetWindowAttribute(handle, DWMWA_USE_IMMERSIVE_DARK_MODE, ref dark, sizeof(int)) != 0)
                {
                    DwmSetWindowAttribute(handle, DWMWA_USE_IMMERSIVE_DARK_MODE_LEGACY, ref dark, sizeof(int));
                }

                //The caption takes the menu bar's colour rather than the page ground, so the
                //bar reads as one continuous strip with the menu directly beneath it.
                trySetColor(handle, DWMWA_CAPTION_COLOR, "Brush.Menu");
                trySetColor(handle, DWMWA_TEXT_COLOR, "Brush.Text");
                trySetColor(handle, DWMWA_BORDER_COLOR, "Brush.Border");
            }
            catch (DllNotFoundException) { }      //No dwmapi.dll: leave the chrome alone.
            catch (EntryPointNotFoundException) { }
        }

        private static void trySetColor(IntPtr handle, int attribute, string resourceKey)
        {
            var color = colorFromResource(resourceKey);
            if (color == null) { return; }

            int value = toColorRef(color.Value);
            DwmSetWindowAttribute(handle, attribute, ref value, sizeof(int));
        }

        private static Color? colorFromResource(string key)
        {
            return Application.Current?.TryFindResource(key) is SolidColorBrush brush ? brush.Color : (Color?)null;
        }

        /// <summary>DWM wants a COLORREF, which is 0x00BBGGRR - the reverse of #RRGGBB.</summary>
        private static int toColorRef(Color color) => color.R | (color.G << 8) | (color.B << 16);
    }
}
