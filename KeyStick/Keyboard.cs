using System;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace KeyStick
{
    /// <summary>
    /// Which keys and mouse buttons are down, and whether the game is the window listening.
    ///
    /// Read by polling rather than by a keyboard hook. A low level hook sees every keystroke on
    /// the machine, has to be installed from a message pump, and is the kind of thing that makes
    /// security software take an interest - all to answer a question that can be asked directly a
    /// few times per pass.
    ///
    /// The focus test matters more than it looks. Without it the virtual stick keeps pushing while
    /// you are in a browser, which both drives the game in the background and holds it in gamepad
    /// mode; with it, the pad falls still the moment you leave the game.
    /// </summary>
    public static class Keyboard
    {
        [DllImport("user32.dll")]
        private static extern short GetAsyncKeyState(int key);

        [DllImport("user32.dll")]
        private static extern IntPtr GetForegroundWindow();

        [DllImport("user32.dll")]
        private static extern uint GetWindowThreadProcessId(IntPtr window, out uint processId);

        /// <summary>True while the key is held. The high bit is the one that means "down now".</summary>
        public static bool down(int key) => (GetAsyncKeyState(key) & 0x8000) != 0;

        public const int W = 0x57, A = 0x41, S = 0x53, D = 0x44;
        public const int One = 0x31, Two = 0x32, Three = 0x33;
        public const int Q = 0x51, E = 0x45, F = 0x46, I = 0x49, M = 0x4D, V = 0x56;
        public const int Space = 0x20, Shift = 0x10, Escape = 0x1B, Tab = 0x09, Control = 0x11;

        //Mouse buttons come through the same call. The thumb button is the one this game uses for
        //dodge, so it is worth having.
        public const int MouseLeft = 0x01, MouseRight = 0x02, MouseMiddle = 0x04, MouseThumb = 0x05;

        //The last window asked about, and the answer. Naming the process behind a window means
        //opening it, which allocates and costs a handle - and doing that a hundred and twenty
        //times a second is enough to take frames off the game being asked about. The window
        //handle is free to read, and it only changes when somebody alt-tabs.
        private static IntPtr _lastWindow = IntPtr.Zero;
        private static string? _lastProcess;

        /// <summary>
        /// The process owning the window currently being typed into, or nothing.
        ///
        /// Answered from memory unless the foreground window has actually changed.
        /// </summary>
        public static string? foregroundProcess()
        {
            var window = GetForegroundWindow();
            if (window == _lastWindow) { return _lastProcess; }

            _lastWindow = window;
            _lastProcess = nameOf(window);
            return _lastProcess;
        }

        private static string? nameOf(IntPtr window)
        {
            if (window == IntPtr.Zero) { return null; }
            if (GetWindowThreadProcessId(window, out var processId) == 0) { return null; }

            try
            {
                using var process = Process.GetProcessById((int)processId);
                return process.ProcessName;
            }
            catch (Exception)
            {
                //A window whose process has already gone, or one this account may not ask about.
                //Either way the answer is that it is not the game.
                return null;
            }
        }
    }
}
