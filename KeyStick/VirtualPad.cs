using Nefarius.ViGEm.Client;
using Nefarius.ViGEm.Client.Targets;
using Nefarius.ViGEm.Client.Targets.Xbox360;
using System;

namespace KeyStick
{
    /// <summary>
    /// A controller that is not there.
    ///
    /// Minecraft Dungeons has no keyboard movement. Its own settings bind W, A, S and D twice
    /// over, and the keybinding screen offers no Move entry at all, because those bindings were
    /// never wired to anything. There is nothing in the game to fix and nothing in a mod pak that
    /// can add it.
    ///
    /// What the game does read, faultlessly, is a gamepad stick. So rather than argue with the
    /// game, this gives it a gamepad: ViGEmBus presents a virtual Xbox pad to Windows, the game
    /// sees an ordinary controller, and the keys are pushed in as stick deflection. Nothing is
    /// injected, no process is opened, no file of the game's is touched, and a patch cannot break
    /// it because it never knew this existed.
    /// </summary>
    public sealed class VirtualPad : IDisposable
    {
        private readonly ViGEmClient _client;
        private readonly IXbox360Controller _pad;

        private VirtualPad(ViGEmClient client, IXbox360Controller pad)
        {
            _client = client;
            _pad = pad;
        }

        /// <summary>
        /// Connects a virtual pad, or explains why it could not.
        ///
        /// The one thing that goes wrong here is the driver not being installed, and it is worth
        /// saying so in as many words: the exception it throws otherwise names a COM class and a
        /// device path, which tells somebody nothing about what to do next.
        /// </summary>
        public static VirtualPad connect()
        {
            ViGEmClient client;
            try
            {
                client = new ViGEmClient();
            }
            catch (Exception problem)
            {
                throw new InvalidOperationException(
                    "The ViGEmBus driver is not installed, so there is nothing to present a controller to.\n" +
                    "  Install it once from https://github.com/nefarius/ViGEmBus/releases and run this again.\n" +
                    "  (" + problem.Message + ")", problem);
            }

            var pad = client.CreateXbox360Controller();

            //Reports are submitted deliberately rather than on every change, so one pass of the
            //loop produces one report instead of four.
            pad.AutoSubmitReport = false;
            pad.Connect();

            return new VirtualPad(client, pad);
        }

        /// <summary>
        /// Where the left stick is pushed, as a pair from -1 to 1.
        ///
        /// Y is negated because a stick reads up as positive while everything above this counts
        /// forward as negative-free and screen-like. Doing it here keeps the sign confusion in one
        /// place rather than spread through the mapping.
        /// </summary>
        public void setLeftStick(double x, double y)
        {
            _pad.SetAxisValue(Xbox360Axis.LeftThumbX, toAxis(x));
            _pad.SetAxisValue(Xbox360Axis.LeftThumbY, toAxis(y));
        }

        public void press(Xbox360Button button, bool down)
        {
            _pad.SetButtonState(button, down);
        }

        /// <summary>
        /// A trigger, which is a one-sided axis rather than a button: nothing to all the way, with
        /// no negative end. A key can only say all or nothing, so that is what it says.
        /// </summary>
        public void setTrigger(bool right, bool down)
        {
            _pad.SetSliderValue(
                right ? Xbox360Slider.RightTrigger : Xbox360Slider.LeftTrigger,
                down ? byte.MaxValue : (byte)0);
        }

        public void submit() => _pad.SubmitReport();

        /// <summary>
        /// A stick axis is a signed sixteen bit number, and the negative end reaches one further
        /// than the positive one. Clamped rather than scaled, so full deflection is full.
        /// </summary>
        private static short toAxis(double value)
        {
            if (double.IsNaN(value)) { return 0; }
            var scaled = value * short.MaxValue;
            if (scaled > short.MaxValue) { return short.MaxValue; }
            if (scaled < short.MinValue) { return short.MinValue; }
            return (short)scaled;
        }

        public void Dispose()
        {
            try
            {
                //Centred before letting go. A pad that disconnects at full deflection can leave
                //the last value latched, and a character that walks into a wall for ever is a
                //puzzling way to find out this program exited.
                setLeftStick(0, 0);
                submit();
                _pad.Disconnect();
            }
            catch (Exception) { }

            _client.Dispose();
        }
    }
}
