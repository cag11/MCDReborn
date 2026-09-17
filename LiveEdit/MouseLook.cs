using System;
using System.Runtime.InteropServices;
using System.Threading;

namespace LiveEdit
{
    /// <summary>
    /// Turning the camera with the mouse, by reading where the pointer went and putting it back.
    ///
    /// The camera can already be pointed anywhere from outside the game. What was missing was
    /// somewhere for the angle to come from, and this is the cheapest thing that works: take the
    /// pointer's position sixty times a second, turn how far it moved into pitch and yaw, and
    /// move it back to the middle of the window so it can move again.
    ///
    /// Recentring is not a trick to get a delta - it is the feature. Dungeons aims at the cursor,
    /// so a cursor pinned to the middle of the screen is a character aiming at whatever the camera
    /// is pointed at. The mouse turns the view and the character faces the view, which is what
    /// third person means. Letting the pointer roam would give a camera that turns while the
    /// character swings round to face a corner of the screen.
    ///
    /// Nothing is injected and nothing is hooked. This reads a cursor position that any program
    /// may read and writes one that any program may write, which is the same bargain the rest of
    /// this project makes: stay outside, and pay for it by doing the work every frame.
    ///
    /// It also holds the camera where it was put. The world has trigger volumes that pull the
    /// view out and swing it overhead, and they do it by writing the same two things this does -
    /// so the only way to decline them is to keep writing back. That is why the angle goes out
    /// every frame rather than only when the mouse moves.
    ///
    /// The one real hazard is a trapped pointer, and there are four ways out: opening any menu,
    /// alt tabbing away, F10, and closing the game. All of them are checked before the pointer is
    /// moved anywhere.
    /// </summary>
    public sealed class MouseLook : IDisposable
    {
        //Sixty a second. The camera is written directly rather than eased, so this is the frame
        //rate of the mouse looking rather than a poll interval to be traded off.
        private const int EVERY_MS = 16;

        //Degrees per pixel at a sensitivity of one, picked so that a normal desk sweep turns a
        //bit more than all the way round.
        private const float DEGREES_PER_PIXEL = 0.12f;

        //How far the camera can tip, which is one answer from behind a character and another from
        //inside one.
        //
        //From seven metres back, eighty five degrees down is the game's own top-down view and five
        //up is as far as the camera can rise before it swings under the floor. With the camera on
        //the crown of the head those are backwards: eighty five down is aimed at the head itself,
        //and five up means you cannot look up at all.
        //
        //This is the only thing that ever explained the head near walls, and it is the one thing
        //that had never actually run - see the note in LiveCameraLink.startLooking. Measured over
        //forty seconds of walking into corners, the arm length, the pivot, both offsets and the
        //field of view did not move a thousandth of a unit. The pitch went to minus eighty five.
        //
        //Forty five rather than thirty five, which was a guess made when this was competing with
        //four other changes, or sixty, which was a guess made when it was believed not to matter.
        private const float THIRD_PERSON_LOWEST = -85f;
        private const float THIRD_PERSON_HIGHEST = 5f;
        private const float FIRST_PERSON_LOWEST = -45f;
        private const float FIRST_PERSON_HIGHEST = 70f;

        //Below this the camera is in the character rather than behind it.
        private const float INSIDE_THE_CHARACTER = 150f;

        [StructLayout(LayoutKind.Sequential)]
        private struct NativePoint { public int X; public int Y; }

        [StructLayout(LayoutKind.Sequential)]
        private struct NativeRect { public int Left, Top, Right, Bottom; }

        [DllImport("user32.dll")] private static extern bool GetCursorPos(out NativePoint point);
        [DllImport("user32.dll")] private static extern bool SetCursorPos(int x, int y);
        [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
        [DllImport("user32.dll")] private static extern bool GetClientRect(IntPtr window, out NativeRect rect);
        [DllImport("user32.dll")] private static extern bool ClientToScreen(IntPtr window, ref NativePoint point);
        [DllImport("user32.dll")] private static extern short GetAsyncKeyState(int key);

        private readonly LiveCamera _camera;
        private readonly GameProcess _game;

        //Its own walk of the chain, re-done every frame.
        //
        //Holding a pointer was what crashed the game. This writes the camera sixty times a second,
        //and a level change frees the character underneath it - so between the character being
        //destroyed and anything noticing, every one of those writes went into memory the game had
        //taken back. It survived the bridge because nothing there is freed; going back to the camp
        //destroys the whole level and it crashed on the way out.
        //
        //Seven reads a frame is the price of never doing that again. When the walk fails there is
        //no character at that moment, and the right thing to do is nothing at all.
        private readonly LiveCamera _live;

        private Thread? _thread;
        private volatile bool _running;

        //Where the angle is kept while looking around. Read from the game once when it starts:
        //picking up from wherever the camera already points is the difference between taking hold
        //of the view and having it jump somewhere first.
        private float _pitch;
        private float _yaw;

        //Whether the pointer has been handed back for the moment.
        //
        //Stopping and starting again would do almost the same thing, and be worse in the one way
        //that matters: the angle would be re-read on the way back in, so every look at a menu
        //would end with the camera somewhere slightly different. Suspending keeps the view
        //exactly where it was left.
        private volatile bool _suspended;
        private bool _wasSuspended;

        public MouseLook(GameProcess game, LiveCamera camera)
        {
            _game = game;
            _camera = camera;
            _live = new LiveCamera(game);
        }

        /// <summary>How far the camera turns for a given amount of mouse. One is ordinary.</summary>
        public float Sensitivity { get; set; } = 1f;

        /// <summary>Whether moving the mouse down looks up, as flight sticks and some people prefer.</summary>
        public bool InvertPitch { get; set; }

        /// <summary>
        /// How far back to keep the camera, against anything in the world that moves it.
        ///
        /// The game has trigger volumes that pull the camera out - under the bridge in the camp is
        /// one - and they work by calling the same SetDesiredArmLegnth this does. Measured: walking
        /// under it writes 3500 in one go, and the arm then eases from 800 out to 3490 over six
        /// seconds and stays there.
        ///
        /// It stayed there because of an asymmetry that looked harmless when it was written: the
        /// distance was set once, when third person was switched on, while the volume sets it
        /// every time it is entered. One write against many is not a contest.
        ///
        /// So it is held instead of set. Null leaves the game's own volumes alone, which is what
        /// somebody wants who is not in third person.
        /// </summary>
        public float? HoldArmLength { get; set; }

        /// <summary>How far the camera may tip down and up, in degrees.</summary>
        public float LowestPitch { get; set; } = THIRD_PERSON_LOWEST;

        public float HighestPitch { get; set; } = THIRD_PERSON_HIGHEST;

        /// <summary>The pitch limits that suit a camera this far back.</summary>
        public static void limitsFor(float armLength, out float lowest, out float highest)
        {
            var inside = armLength < INSIDE_THE_CHARACTER;

            lowest = inside ? FIRST_PERSON_LOWEST : THIRD_PERSON_LOWEST;
            highest = inside ? FIRST_PERSON_HIGHEST : THIRD_PERSON_HIGHEST;
        }


        /// <summary>
        /// A key that stops it outright, or zero for none. Zero, now.
        ///
        /// This was Escape, chosen when a pinned pointer had no other way out and somebody needed
        /// one they could guess at. Then menus learned to hand the pointer back on their own, and
        /// the two changes met badly: Escape is how a menu is closed, so closing one did not
        /// resume mouse look, it ended it - and the way back was a checkbox in another window.
        ///
        /// There is no need for it any more. A menu releases the pointer, alt tabbing releases it,
        /// and F10 turns the whole thing off from inside the game. Nothing is trapped, so nothing
        /// needs a panic key that collides with the game's own.
        /// </summary>
        public int ReleaseKey { get; set; }

        public bool IsRunning => _running;

        /// <summary>
        /// Gives the pointer back while a menu is open, without losing the camera's angle.
        ///
        /// A pointer pinned to the middle of the screen is the whole trick while playing and
        /// useless the moment an inventory is open, because there is no way to move it onto
        /// anything. This is the release, and the camera stays exactly where it was.
        /// </summary>
        public bool Suspended
        {
            get => _suspended;
            set => _suspended = value;
        }

        /// <summary>Raised when it stops on its own, so a panel can untick its own box.</summary>
        public event Action? stopped;

        /// <summary>
        /// Starts looking, from wherever the camera is pointing now.
        /// </summary>
        public bool start()
        {
            if (_running) { return true; }
            if (_camera.SpringArm == IntPtr.Zero) { return false; }

            _pitch = _camera.Pitch ?? -45f;

            //Whatever the camera was already tipped to, brought inside the limits that apply now.
            _pitch = Math.Max(LowestPitch, Math.Min(HighestPitch, _pitch));

            _yaw = _camera.Yaw ?? 45f;

            _running = true;
            _thread = new Thread(run) { IsBackground = true, Name = "mouse look" };
            _thread.Start();
            return true;
        }

        public void stop()
        {
            _running = false;
        }

        /// <summary>
        /// The loop, on its own thread.
        ///
        /// Its own thread rather than a timer on the interface, because this runs at frame rate
        /// and a dropped frame here is a camera that stutters while somebody is turning it. The
        /// only thing it touches is the game, which is another process - so there is nothing here
        /// for the interface thread to contend with.
        /// </summary>
        private void run()
        {
            var middle = centreOf(_game.Process.MainWindowHandle);
            if (middle == null) { _running = false; stopped?.Invoke(); return; }

            SetCursorPos(middle.Value.X, middle.Value.Y);

            while (_running)
            {
                Thread.Sleep(EVERY_MS);

                if (!_game.IsRunning) { break; }

                //Pressed, rather than toggled - the high bit is down now, which is what a loop
                //polling sixty times a second wants to know.
                if (ReleaseKey != 0 && (GetAsyncKeyState(ReleaseKey) & 0x8000) != 0) { break; }

                //Alt tabbing away has to give the pointer back, or the editor's own window cannot
                //be clicked to turn this off.
                var foreground = GetForegroundWindow();
                if (foreground != _game.Process.MainWindowHandle) { continue; }

                if (_suspended) { _wasSuspended = true; continue; }

                //Where the character is right now, or nothing. Between levels this fails for a
                //second or two and the loop simply idles.
                if (!_live.find(out _)) { continue; }

                //Coming back, the pointer is put in the middle before anything is measured from
                //it. Otherwise wherever it was left in the menu reads as one enormous flick.
                if (_wasSuspended)
                {
                    _wasSuspended = false;

                    var centre = centreOf(_game.Process.MainWindowHandle);
                    if (centre != null) { SetCursorPos(centre.Value.X, centre.Value.Y); }
                    continue;
                }

                //Re-read each frame. A window that moves or resizes while this is running would
                //otherwise recentre the pointer to the wrong place, and the camera would drift on
                //its own with the mouse sitting still.
                middle = centreOf(_game.Process.MainWindowHandle);
                if (middle == null) { break; }

                if (!GetCursorPos(out var where)) { continue; }

                var acrossBy = where.X - middle.Value.X;
                var downBy = where.Y - middle.Value.Y;

                if (acrossBy != 0 || downBy != 0)
                {
                    var step = DEGREES_PER_PIXEL * Sensitivity;

                    _yaw += acrossBy * step;
                    _pitch += (InvertPitch ? downBy : -downBy) * step;

                    //Kept in a turn of the circle so the number stays readable, and because a yaw
                    //that climbs for an hour eventually loses precision where it matters.
                    if (_yaw > 180f) { _yaw -= 360f; }
                    if (_yaw < -180f) { _yaw += 360f; }

                    _pitch = Math.Max(LowestPitch, Math.Min(HighestPitch, _pitch));

                    SetCursorPos(middle.Value.X, middle.Value.Y);
                }

                //Written every frame, not only when the mouse moved.
                //
                //Standing still used to mean writing nothing, which left whatever the world had
                //done to the camera in place - and the world does plenty. A rotate task in a
                //camera volume swings the pitch to seventy degrees over about a fifth of a
                //second, and with nothing writing back it simply stayed there.
                //
                //The cost is one write a frame to a value that is usually the same as it was.
                _live.setRotation(_pitch, _yaw);

                //And the distance, against the volumes that set it.
                //
                //The desired length only, and gently. Writing the target length as well was tried,
                //for a good reason - a camera volume writes the target and not the desired, so this
                //check does not fire while the camera is pulled out and easing back in.
                //
                //It was still wrong. The target is what the camera is placed with and the game
                //writes it every tick, so correcting it every frame is a tug of war at sixty hertz
                //and the camera shakes itself apart. The engine eases the target towards the
                //desired by itself; nudging the desired lets it do that, which is the whole reason
                //this was written this way to begin with.
                if (HoldArmLength is float wanted)
                {
                    var now = _live.DesiredLength;
                    if (now == null || Math.Abs(now.Value - wanted) > 2f)
                    {
                        _live.setArmLength(wanted);
                    }
                }
            }

            _running = false;
            stopped?.Invoke();
        }

        /// <summary>
        /// The middle of the game's window, in screen coordinates.
        ///
        /// The client area rather than the window, so a title bar does not put the centre slightly
        /// above where the game thinks the middle is - which would read as the character aiming a
        /// little high all the time.
        /// </summary>
        private static NativePoint? centreOf(IntPtr window)
        {
            if (window == IntPtr.Zero) { return null; }
            if (!GetClientRect(window, out var client)) { return null; }

            var middle = new NativePoint {
                X = (client.Right - client.Left) / 2,
                Y = (client.Bottom - client.Top) / 2,
            };
            return ClientToScreen(window, ref middle) ? middle : (NativePoint?)null;
        }

        public void Dispose()
        {
            stop();
            _thread?.Join(200);
        }
    }
}
