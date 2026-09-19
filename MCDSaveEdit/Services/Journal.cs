using System;
using System.IO;
using System.Text;
#nullable enable

namespace MCDSaveEdit.Services
{
    /// <summary>
    /// What happened, written down, for the times the app is gone before it can say anything.
    ///
    /// The fault this exists for is the one that leaves nothing behind: an exception on one of the
    /// background threads that drive the live camera takes the whole process with it, with no
    /// window, no message and nothing written anywhere. From the outside the app simply vanishes
    /// while somebody is dragging a slider, and there is no way to tell which slider.
    ///
    /// Two levels, because they answer different questions:
    ///
    /// - **Notes** are always written, and there are very few of them: the app starting, the app
    ///   stopping, and anything that went wrong. That is what makes this safe to ship - a build
    ///   nobody is debugging writes a handful of lines a session and never touches the file again.
    /// - **Remarks** are written only when the log is turned up, and there are hundreds: every
    ///   event the app already reports internally. That is the debug build, and it is where the
    ///   answer to "what was I doing when it died" lives.
    ///
    /// Turned up by the debug build, or by putting a file called `verbose.txt` beside the exe -
    /// the second because the person who can reproduce a crash is rarely the person holding a
    /// debug build, and asking them to drop an empty file somewhere is a smaller favour than
    /// asking them to install a different application.
    /// </summary>
    public static class Journal
    {
        /// <summary>Whether everything is written, or only what went wrong.</summary>
        public static bool loud { get; set; }

        //Somewhere always writable. Beside the exe would be friendlier to find and is wrong:
        //plenty of people keep this in Program Files, where writing needs elevation, and a log
        //that fails to open on the machines most likely to need it is not a log.
        private static readonly string _folder = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MCDReborn");

        private static readonly string _file = Path.Combine(_folder, "log.txt");

        //One session per file would lose the crash that matters as soon as the app is opened
        //again, and never rolling would grow without limit. So it rolls once, keeping the run
        //before this one - which is the one somebody is usually asking about.
        private const long BIGGEST = 4L * 1024 * 1024;

        private static readonly object _lock = new object();
        private static bool _tried;
        private static bool _usable;

        public static string path => _file;

        /// <summary>Always written. For the start, the end, and anything that went wrong.</summary>
        public static void note(string text) => write("  ", text);

        /// <summary>Written only when the log is turned up.</summary>
        public static void remark(string text)
        {
            if (!loud) { return; }
            write("  ", text);
        }

        /// <summary>
        /// Something that went wrong, with everything known about it.
        ///
        /// The whole exception rather than its message: a message says "Object reference not set",
        /// and the stack says which object and which line, and only one of those can be acted on.
        /// </summary>
        public static void trouble(string where, Exception problem)
        {
            var said = new StringBuilder();
            said.Append("TROUBLE in ").Append(where).AppendLine();

            for (Exception? at = problem; at != null; at = at.InnerException)
            {
                said.Append("    ").Append(at.GetType().Name).Append(": ").AppendLine(at.Message);
                if (!string.IsNullOrWhiteSpace(at.StackTrace)) { said.AppendLine(at.StackTrace); }
            }

            write("!!", said.ToString().TrimEnd());
        }

        /// <summary>Opens the log for the session, and says what is running.</summary>
        public static void begin()
        {
            //A file dropped beside the exe turns it up without a different build.
            try
            {
                var beside = AppContext.BaseDirectory;
                if (File.Exists(Path.Combine(beside, "verbose.txt"))) { loud = true; }
            }
            catch (Exception) { }

            roll();

            note($"---- MCD Reborn {Data.Constants.CURRENT_VERSION_NUMBER}"
                + $" on {Environment.OSVersion.VersionString}, {(loud ? "full log" : "quiet log")} ----");
        }

        public static void end() => note("---- closed ----");

        private static void roll()
        {
            try
            {
                Directory.CreateDirectory(_folder);
                var now = new FileInfo(_file);
                if (!now.Exists || now.Length < BIGGEST) { return; }

                var previous = _file + ".old";
                if (File.Exists(previous)) { File.Delete(previous); }
                File.Move(_file, previous);
            }
            catch (Exception) { /* A log that cannot roll is still a log. */ }
        }

        private static void write(string mark, string text)
        {
            lock (_lock)
            {
                if (!_tried)
                {
                    _tried = true;
                    try { Directory.CreateDirectory(_folder); _usable = true; }
                    catch (Exception) { _usable = false; }
                }

                if (!_usable) { return; }

                try
                {
                    File.AppendAllText(_file,
                        $"{DateTime.Now:HH:mm:ss.fff} {mark} {text}{Environment.NewLine}");
                }
                catch (Exception)
                {
                    //Nothing to be done and nowhere to say it. Writing a log must never be the
                    //thing that brings an application down.
                }
            }
        }
    }
}
