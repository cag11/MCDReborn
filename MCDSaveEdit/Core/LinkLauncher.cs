using System;
using System.Diagnostics;
#nullable enable

namespace MCDSaveEdit
{
    /// <summary>
    /// Opens a URL in the user's browser.
    ///
    /// On .NET Framework, Process.Start("https://...") worked because UseShellExecute
    /// defaulted to true. From .NET Core onwards it defaults to false, and the same call
    /// throws Win32Exception "The system cannot find the file specified" - so every link
    /// in this app was silently broken by the .NET 10 port until this was added.
    /// </summary>
    public static class LinkLauncher
    {
        /// <summary>Returns whether the browser actually took it, for callers with a fallback.</summary>
        public static bool open(string? url)
        {
            if (string.IsNullOrWhiteSpace(url)) { return false; }

            try
            {
                Process.Start(new ProcessStartInfo(url!) { UseShellExecute = true });
                return true;
            }
            catch (Exception e)
            {
                //No browser, a blocked protocol, or a malformed URL. Not worth crashing over.
                Console.WriteLine($"Could not open \"{url}\": {e.Message}");
                return false;
            }
        }
    }
}
