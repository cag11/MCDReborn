using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
#nullable enable

namespace MCDSaveEdit.Logic
{
    /// <summary>
    /// Whether the game is up, asked before anything is written into its paks folder.
    ///
    /// This exists because of the most expensive kind of failure this app can have: the silent
    /// one. Windows locks a file that a running process has open, so installing a mod over a pak
    /// the game is currently reading does not produce a corrupt game or a warning - it produces
    /// an exception whose message is about file sharing, several layers below anybody who would
    /// know what it meant. Worse, installing a *new* pak while the game is up appears to succeed
    /// completely, and then does nothing at all, because the game read that folder once at
    /// startup and will not read it again.
    ///
    /// Both of those look identical from the outside to a mod that does not work. Hours went into
    /// the second one before the actual cause - the game was open - was even considered. So it is
    /// asked first, and said plainly.
    /// </summary>
    public static class GameRunning
    {
        /// <summary>
        /// The processes that count as the game being up.
        ///
        /// Both names are needed, because this game ships under two of them and a machine can
        /// easily have both installed - this one does:
        ///
        ///   Steam  ...\steamapps\common\MinecraftDungeons\Dungeons.exe, which starts
        ///          Dungeons\Binaries\Win64\Dungeons-Win64-Shipping.exe. Both run at once, so
        ///          both names appear in the process list together.
        ///   Xbox   C:\XboxGames\Minecraft Dungeons\Content\Dungeons.exe, which uses the
        ///          plain name for the shipping binary as well.
        ///
        /// Checking only the Unreal default would therefore miss the Game Pass build entirely,
        /// and checking only the plain name would miss nothing today but is one Steam update
        /// away from doing so. Neither is worth guessing at: looking for a name that is not
        /// there costs one call that returns an empty array.
        ///
        /// gamelaunchhelper.exe, the Xbox shim, is deliberately not listed: it is not holding
        /// the paks, and it is the kind of thing that lingers after the game has gone, which
        /// would turn this into an app that refuses to install anything.
        ///
        /// Names are without .exe, which is how Windows reports them.
        /// </summary>
        private static readonly string[] NAMES =
        {
            "Dungeons",
            "Dungeons-Win64-Shipping",
        };

        /// <summary>The names of the game processes currently running, if any.</summary>
        public static IReadOnlyList<string> found()
        {
            var running = new List<string>();

            foreach (var name in NAMES)
            {
                try
                {
                    //Asked per name rather than by listing every process: enumerating them all
                    //needs rights this app has no reason to want, and fails outright on some
                    //machines. A name that matches nothing simply returns an empty array.
                    if (Process.GetProcessesByName(name).Length > 0) { running.Add(name); }
                }
                catch (Exception)
                {
                    //Not being able to look is not the same as it not being there, but there is
                    //nothing useful to say about it either - and refusing an install because the
                    //process list could not be read would be worse than the problem.
                }
            }

            return running;
        }

        public static bool isUp => found().Count > 0;
    }
}
