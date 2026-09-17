using System;
using System.Diagnostics;
using System.Linq;
using System.Threading;

namespace KeyStick
{
    /// <summary>
    /// WASD for a game that does not have it.
    ///
    /// Minecraft Dungeons cannot be moved with the keyboard. That is not a setting anybody has
    /// missed: the game binds W, A, S and D in its own config, twice, and those bindings do
    /// nothing, and its keybinding screen has no Move entry to change. They were never connected.
    /// No mod pak can add them, because what is missing is code and not data.
    ///
    /// Everything *else* on the keyboard works - attacking, artifacts, the inventory are all bound
    /// and all functioning. Only movement is dead.
    ///
    /// The game reads a gamepad stick perfectly well, though. So this hands it one. A virtual Xbox
    /// pad is presented to Windows through ViGEmBus, the keys are pushed in as stick deflection and
    /// button presses, and as far as the game is concerned somebody plugged in a controller.
    /// Nothing is injected, no process is opened, nothing belonging to the game is modified, and a
    /// patch cannot break it - the game never learns this is here.
    /// </summary>
    public static class Program
    {
        //The game's own process, which is the one in Binaries and not the small launcher beside it.
        private const string GAME = "Dungeons";

        //Often enough that a keypress is never noticeably late, and cheap now that the expensive
        //question in the loop is only asked when its answer can have changed.
        private const int HZ = 120;

        public static int Main(string[] args)
        {
            var anywhere = args.Any(a => a.Equals("--anywhere", StringComparison.OrdinalIgnoreCase));
            var moveOnly = args.Any(a => a.Equals("--move-only", StringComparison.OrdinalIgnoreCase));

            Console.WriteLine("KeyStick - WASD for Minecraft Dungeons, through a virtual controller");
            Console.WriteLine();

            VirtualPad pad;
            try
            {
                pad = VirtualPad.connect();
            }
            catch (Exception problem)
            {
                Console.Error.WriteLine(problem.Message);
                return 1;
            }

            using (pad)
            {
                Console.WriteLine("  A virtual Xbox controller is now connected.");
                Console.WriteLine(anywhere
                    ? "  Sending to whatever has focus, because --anywhere was passed."
                    : $"  Sending only while {GAME} is the window in front.");
                Console.WriteLine();

                if (moveOnly)
                {
                    Console.WriteLine("  W A S D      move");
                    Console.WriteLine("  (buttons left alone, because --move-only was passed)");
                }
                else
                {
                    foreach (var line in Mapping.describe()) { Console.WriteLine(line); }
                    Console.WriteLine();
                    Console.WriteLine("  The keyboard already works for all of those on its own. They are");
                    Console.WriteLine("  mapped here in case the game stops reading it once a pad appears.");
                }

                Console.WriteLine();
                Console.WriteLine("  Ctrl+C to stop. The controller disappears when this closes.");
                Console.WriteLine();

                run(pad, anywhere, moveOnly);
            }

            return 0;
        }

        private static void run(VirtualPad pad, bool anywhere, bool moveOnly)
        {
            var stick = new Stick();
            var stopping = false;

            Console.CancelKeyPress += (s, e) => {
                //Handled rather than allowed to kill the process, so the pad is centred and
                //disconnected on the way out instead of being left latched.
                e.Cancel = true;
                stopping = true;
            };

            var clock = Stopwatch.StartNew();
            var last = clock.Elapsed.TotalSeconds;
            var wasPlaying = false;

            while (!stopping)
            {
                var now = clock.Elapsed.TotalSeconds;
                var seconds = now - last;
                last = now;

                var playing = anywhere
                    || string.Equals(Keyboard.foregroundProcess(), GAME, StringComparison.OrdinalIgnoreCase);
                if (playing != wasPlaying)
                {
                    Console.WriteLine(playing ? "  game in front - sending" : "  game not in front - idle");
                    wasPlaying = playing;
                }

                double targetX = 0, targetY = 0;
                if (playing)
                {
                    (targetX, targetY) = Stick.wanted(
                        west: Keyboard.down(Keyboard.A),
                        east: Keyboard.down(Keyboard.D),
                        north: Keyboard.down(Keyboard.W),
                        south: Keyboard.down(Keyboard.S));
                }

                var (x, y) = stick.advance(targetX, targetY, seconds);
                pad.setLeftStick(x, y);

                if (!moveOnly)
                {
                    foreach (var trigger in Mapping.TRIGGERS)
                    {
                        pad.setTrigger(trigger.Right, playing && Keyboard.down(trigger.Key));
                    }
                    foreach (var bind in Mapping.BUTTONS)
                    {
                        pad.press(bind.Button, playing && Keyboard.down(bind.Key));
                    }
                }

                //One report per pass, whatever changed in it.
                pad.submit();

                Thread.Sleep(1000 / HZ);
            }

            Console.WriteLine();
            Console.WriteLine("  Stopped. The controller is gone.");
        }
    }
}
