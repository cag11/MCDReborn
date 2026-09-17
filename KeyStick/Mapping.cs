using Nefarius.ViGEm.Client.Targets.Xbox360;
using System.Collections.Generic;

namespace KeyStick
{
    /// <summary>
    /// Which key or mouse button stands in for which part of a controller.
    ///
    /// Taken from the game's own config rather than invented. Its keyboard bindings say what each
    /// action is meant to be reached by - `MainAttack = LeftMouseButton`, `Dodge = ThumbMouseButton`,
    /// `Use Item Slot 1 = One` - and its gamepad bindings say which part of a pad the game listens
    /// to for the same things. Matching the two gives a layout that already feels right, because
    /// it is the one the game was designed around.
    ///
    /// Worth knowing why this exists at all: **the keyboard already works for everything except
    /// moving**. Attacks, artifacts, the inventory - all bound and all functioning. The only dead
    /// bindings are the movement axes. So this mapping is insurance rather than necessity: if the
    /// game switches wholly to controller mode when it sees a pad and stops reading the mouse,
    /// every action still has a way through. If it keeps reading the mouse, the two simply agree.
    /// </summary>
    public static class Mapping
    {
        public sealed class Bind
        {
            public Bind(int key, Xbox360Button button, string what)
            {
                Key = key;
                Button = button;
                What = what;
            }

            public int Key { get; }
            public Xbox360Button Button { get; }
            /// <summary>What it does in game, for the listing this prints on startup.</summary>
            public string What { get; }
        }

        public sealed class Trigger
        {
            public Trigger(int key, bool right, string what)
            {
                Key = key;
                Right = right;
                What = what;
            }

            public int Key { get; }
            /// <summary>Right trigger, or left.</summary>
            public bool Right { get; }
            public string What { get; }
        }

        /// <summary>
        /// The two triggers, which is where this game puts attacking.
        ///
        /// Left mouse to the right trigger looks crossed over and is not: the right trigger is a
        /// pad's main attack in the same way the left mouse button is a mouse's.
        /// </summary>
        public static readonly Trigger[] TRIGGERS = {
            new Trigger(Keyboard.MouseLeft, right: true, "attack"),
            new Trigger(Keyboard.MouseRight, right: false, "second attack"),
        };

        public static readonly Bind[] BUTTONS = {
            //Space is Interact, Skip and DodgeForward on the keyboard, and the bottom face button
            //is the game's do-this-thing button, so they line up.
            new Bind(Keyboard.Space, Xbox360Button.A, "interact, skip, dodge forward"),
            new Bind(Keyboard.MouseThumb, Xbox360Button.LeftThumb, "dodge"),

            //The three artifact slots, on the keys the game already uses for them.
            new Bind(Keyboard.One, Xbox360Button.X, "artifact 1"),
            new Bind(Keyboard.Two, Xbox360Button.Y, "artifact 2"),
            new Bind(Keyboard.Three, Xbox360Button.B, "artifact 3"),

            new Bind(Keyboard.Q, Xbox360Button.LeftShoulder, "shoulder, left"),
            new Bind(Keyboard.E, Xbox360Button.RightShoulder, "shoulder, right"),

            new Bind(Keyboard.Escape, Xbox360Button.Start, "pause"),
            new Bind(Keyboard.Tab, Xbox360Button.Back, "back"),
            new Bind(Keyboard.I, Xbox360Button.Back, "inventory"),

            //The pad down, for the emote wheel the game puts on d-pad right.
            new Bind(Keyboard.V, Xbox360Button.Right, "emote"),
        };

        /// <summary>Everything, for the listing printed when this starts.</summary>
        public static IEnumerable<string> describe()
        {
            yield return "  W A S D      move";
            yield return "  mouse left   attack";
            yield return "  mouse right  second attack";
            yield return "  mouse thumb  dodge";
            yield return "  Space        interact, skip, dodge forward";
            yield return "  1 2 3        artifacts";
            yield return "  Q E          shoulder buttons";
            yield return "  I / Tab      inventory, back";
            yield return "  V            emote";
            yield return "  Escape       pause";
        }
    }
}
