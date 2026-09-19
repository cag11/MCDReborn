using System;
using System.Threading;
#nullable enable

namespace LiveEdit
{
    /// <summary>
    /// Where the background threads say that something went wrong.
    ///
    /// This half of the program runs four loops of its own - the mount, the keyboard, the mouse
    /// and the hotkey watcher - and an exception on any of them takes the whole application down
    /// with it. That is what .NET does with an unhandled exception on a thread, and it happens
    /// without a window, without a message and without anything written anywhere: the app is
    /// simply gone. Which is exactly what "it crashes when I play with the camera settings"
    /// looks like from the outside.
    ///
    /// So each loop catches what escapes it and reports it here, and whoever is hosting this
    /// decides what to do with it - the editor writes it to a file. Nothing in this project knows
    /// about files or windows, and it stays that way, because it is also driven from a probe with
    /// neither.
    ///
    /// Catching is not hiding. A loop that throws is stopped rather than restarted, and the
    /// failure is reported rather than swallowed: the point is to lose one feature with an
    /// explanation instead of losing the application without one.
    /// </summary>
    public static class Trouble
    {
        /// <summary>Called with where it happened and what happened. Never throws back.</summary>
        public static Action<string, Exception>? reporter;

        public static void report(string where, Exception problem)
        {
            try { reporter?.Invoke(where, problem); }
            catch (Exception) { /* A reporter that throws must not take the thread with it. */ }
        }

        /// <summary>
        /// Runs a loop, and reports rather than dies.
        ///
        /// Everything that starts a thread in this project goes through here, so that adding a
        /// fifth loop cannot quietly reintroduce the whole problem.
        /// </summary>
        public static Thread start(string name, Action loop)
        {
            var thread = new Thread(() => {
                try { loop(); }
                catch (Exception problem) { report(name, problem); }
            }) {
                IsBackground = true,
                Name = name,
            };

            thread.Start();
            return thread;
        }
    }
}
