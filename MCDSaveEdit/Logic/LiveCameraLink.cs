using LiveEdit;
using System;
using System.Windows.Threading;
#nullable enable

namespace MCDSaveEdit.Logic
{
    /// <summary>
    /// The running game, watched for and written to while the editor is open.
    ///
    /// The point of this over the pak is the wait. A pak means close the game, write the file,
    /// start the game, walk somewhere worth looking at, decide the angle is slightly wrong, and do
    /// it all again. This means dragging a slider and seeing the answer.
    ///
    /// It watches rather than being told. The requirement was one application to deal with, so
    /// there is no attach button and nothing to start in the right order: the game can be running
    /// before the editor or launched an hour later, and either way the panel notices within a
    /// couple of seconds. Losing the game is the same in reverse - it is not an error, it is just
    /// the sliders going back to writing paks.
    ///
    /// Nothing here is permanent, and that is the safety of it. No file is written, so quitting
    /// the game undoes everything; the pak stays the way to make a setting stick.
    /// </summary>
    public sealed class LiveCameraLink : IDisposable
    {
        //Slow enough to be free, quick enough that nobody alt-tabs back and wonders whether it
        //noticed. The check is a process lookup, not a scan.
        private static readonly TimeSpan LOOK_EVERY = TimeSpan.FromSeconds(2);

        private readonly DispatcherTimer _watch;
        private GameProcess? _game;
        private LiveCamera? _camera;
        private MouseLook? _look;
        private KeyboardMove? _walk;

        public LiveCameraLink()
        {
            _watch = new DispatcherTimer { Interval = LOOK_EVERY };
            _watch.Tick += (_, _) => look();
            _watch.Start();
            look();

            watchHotkey();
        }

        /// <summary>Raised when the game arrives or goes away, so a panel can redraw itself.</summary>
        public event Action? changed;

        public bool attached => _camera != null && _camera.SpringArm != IntPtr.Zero;

        /// <summary>Why it is not attached, in words worth showing someone.</summary>
        public string status { get; private set; } = "";

        /// <summary>
        /// Looks for the game, and for the camera inside it.
        ///
        /// Both halves can fail independently and they mean different things. No process is the
        /// ordinary case of the game not being open. A process whose camera cannot be reached is
        /// the interesting one - the player is at a menu with no character to hang a camera off,
        /// or the game has been updated and the offsets have moved - so it says which.
        /// </summary>
        private void look()
        {
            var was = attached;

            if (_game != null && !_game.IsRunning) { drop(); }

            if (_game == null)
            {
                _game = GameProcess.open(out var problem);
                if (_game == null)
                {
                    status = problem;
                    if (was) { changed?.Invoke(); }
                    return;
                }
            }

            //Re-walked every time rather than remembered. The chain is seven reads, and a pointer
            //held from before a loading screen points at a character who no longer exists.
            //
            //Refreshed in place rather than replaced, which matters: mouse look and the keys were
            //handed this object when they started, and swapping it out from under them left them
            //writing into the old character for ever. Walking the chain again into the same object
            //means everything holding it comes back with it.
            _camera ??= new LiveCamera(_game);

            if (!_camera.find(out var why))
            {
                status = why;
                if (was) { changed?.Invoke(); }
                return;
            }

            status = "";

            //The game's own camera, before anything has been done to it.
            _original ??= readPreset();

            //A different character than the one third person was set up against - a level load,
            //or a death. The values live on the character, so the new one has the game's own and
            //everything running has to be pointed at it again.
            if (thirdPersonOn && _camera.SpringArm != _boundTo) { reattach(); }

            if (attached != was) { changed?.Invoke(); }
        }

        /// <summary>
        /// Whether the camera points where the player is aiming, and the switch for it.
        ///
        /// Live only, which is why it is here rather than among the settings a pak can carry.
        /// </summary>
        public bool? followsAim => _camera?.FollowsAim;

        public bool setFollowsAim(bool on) => _camera?.setFollowsAim(on) == true;

        /// <summary>
        /// Whether the character already turns to face aim without being asked.
        ///
        /// It does, in this game, and the panel says so rather than offering a switch that
        /// appears to do nothing. The character's facing is driven by the movement component's
        /// own desired rotation, which tracks the aim at a thousand degrees a second - measured
        /// by watching the actor's yaw match the controller's exactly while somebody played.
        ///
        /// So the setting writes correctly and changes nothing observable. That is worth saying
        /// out loud, because "it did not work" and "it was already happening" look identical from
        /// the outside and only one of them is a bug.
        /// </summary>
        public bool aimTurningAlreadyOn => _camera?.ControllerYaw != null;

        /// <summary>Whether the mouse is currently turning the camera.</summary>
        public bool looking => _look?.IsRunning == true;

        /// <summary>Raised when mouse look stops by itself, so a panel can untick its box.</summary>
        public event Action? lookingStopped;

        /// <summary>
        /// Hands the camera to the mouse.
        ///
        /// The pointer is pinned to the middle of the game's window while this runs, which is the
        /// point of it rather than a side effect - Dungeons aims at the cursor, so a cursor in the
        /// middle is a character aiming at whatever the camera looks at. Escape gives it back, and
        /// so does alt tabbing away or closing the game.
        /// </summary>
        public bool startLooking(float sensitivity, bool invert)
        {
            if (_game == null || _camera == null) { return false; }

            _sensitivity = sensitivity;
            _invert = invert;

            stopLooking();

            _look = new MouseLook(_game, _camera) { Sensitivity = sensitivity, InvertPitch = invert };
            _look.Suspended = _suspended;
            _look.HoldArmLength = _holdArmLength;

            //And the tilt limits. This line is the entire reason the limits did nothing for two
            //releases: the distance is decided before the mouse look exists, so applyPitchLimits
            //ran with nothing to put them on, stored them here, and every mouse look started on the
            //defaults. The measurement that caught it asked for thirty five degrees and recorded
            //eighty five.
            _look.LowestPitch = _pitchLowest;
            _look.HighestPitch = _pitchHighest;

            _look.stopped += () => lookingStopped?.Invoke();
            return _look.start();
        }

        public void stopLooking()
        {
            _look?.Dispose();
            _look = null;
        }

        /// <summary>Whether the keyboard is currently moving the character.</summary>
        public bool walking => _walk?.IsRunning == true;

        /// <summary>Raised when keyboard movement stops by itself.</summary>
        public event Action? walkingStopped;

        /// <summary>
        /// Lets W, A, S and D move the character, relative to wherever the camera points.
        ///
        /// This game has no movement keys to bind, so the input is written straight into the
        /// vector the engine consumes each frame - which is what a keypress would have become
        /// anyway, several steps later.
        /// </summary>
        public bool startWalking(out string problem)
        {
            problem = "";
            if (_game == null || _camera == null) { problem = "Not attached to the game."; return false; }

            stopWalking();

            _walk = KeyboardMove.attach(_game, _camera, out problem);
            if (_walk == null) { return false; }

            _walk.ClicksDoNotWalk = _clicksDoNotWalk;
            _walk.Suspended = _suspended;
            _walk.CanJump = _canJump;
            _walk.JumpHeight = _jumpHeight;
            _walk.LagSpeedWalking = _lagSpeedWalking;
            _walk.AirControl = _airControl;
            _walk.JumpCount = _jumpCount;
            _walk.stopped += () => walkingStopped?.Invoke();
            return _walk.start();
        }

        public void stopWalking()
        {
            _walk?.Dispose();
            _walk = null;
        }

        /// <summary>
        /// Whether a click is only ever an attack, never somewhere to walk to.
        ///
        /// Remembered here rather than on the mover, so ticking it before the keys are switched
        /// on still means something.
        /// </summary>
        public bool clicksDoNotWalk
        {
            get => _clicksDoNotWalk;
            set
            {
                _clicksDoNotWalk = value;
                if (_walk != null) { _walk.ClicksDoNotWalk = value; }
            }
        }

        private bool _clicksDoNotWalk = true;

        /// <summary>
        /// Tells mouse look how far the camera may tip, given how far back it is.
        ///
        /// Called wherever the distance is decided. Nothing here writes anything the game also
        /// writes - it only clamps a number this app owns, which is why it is the one change around
        /// this camera that cannot fight the engine.
        /// </summary>
        private void applyPitchLimits(float armLength)
        {
            MouseLook.limitsFor(armLength, out var lowest, out var highest);

            _pitchLowest = lowest;
            _pitchHighest = highest;

            if (_look == null) { return; }

            _look.LowestPitch = lowest;
            _look.HighestPitch = highest;
        }

        private float _pitchLowest = -85f;
        private float _pitchHighest = 5f;

        //Below this the camera is in the character rather than behind it.
        private const float INSIDE_THE_CHARACTER = 150f;
        private float _lagSpeedWalking = 1f;


        /// <summary>Whether Q leaves the ground.</summary>
        public bool canJump
        {
            get => _canJump;
            set
            {
                _canJump = value;
                if (_walk != null) { _walk.CanJump = value; }
            }
        }

        public float jumpHeight
        {
            get => _jumpHeight;
            set
            {
                _jumpHeight = value;
                if (_walk != null) { _walk.JumpHeight = value; }
            }
        }

        public float airControl
        {
            get => _airControl;
            set
            {
                _airControl = value;
                if (_walk != null) { _walk.AirControl = value; }
            }
        }

        public int jumpCount
        {
            get => _jumpCount;
            set
            {
                _jumpCount = value;
                if (_walk != null) { _walk.JumpCount = value; }
            }
        }

        private bool _canJump;
        private float _jumpHeight = 1500f;
        private float _airControl = 0.35f;
        private int _jumpCount = 1;

        //What the camera should be held at, or nothing to let the world move it as it likes.
        private float? _holdArmLength;

        //How mouse look was asked for, kept so it can be asked for again on the other side of a
        //loading screen without the panel having to be involved.
        private float _sensitivity = 1f;
        private bool _invert;

        //The character third person was last set up against. A level load builds a new one, and
        //this is how that is noticed.
        private IntPtr _boundTo;

        //What the game's camera looked like before any of this touched it.
        //
        //Taken once, the first time a character is reached, and never taken again - because every
        //reading after the first is of a camera this has already changed. It is what "put it back"
        //puts it back to, and it is why nothing here needs the paks: the game tells us its own
        //settings simply by being asked before we write anything.
        private CameraPreset? _original;

        //What is currently applied, so a level change can put it back on the new character.
        private CameraPreset? _applied;

        /// <summary>
        /// How far back to keep the camera against the world's own camera volumes.
        ///
        /// Set by third person, and by the distance slider while third person is running, so that
        /// dragging it changes what is being held rather than being undone by the next trigger.
        /// </summary>
        public float? holdArmLength
        {
            get => _holdArmLength;
            set
            {
                _holdArmLength = value;
                if (_look != null) { _look.HoldArmLength = value; }
            }
        }

        public bool buttonsMapped => walking && _clicksDoNotWalk;

        /// <summary>
        /// Everything at once, at the distance and angle a third person camera wants.
        ///
        /// The separate switches stay, because being able to turn one thing on is how all of this
        /// was worked out. But nobody arriving at the tab should have to know that a third person
        /// view is five settings - and the distance is the one that is not guessable: the stock
        /// camera sits twenty five metres up, and at that height turning it with the mouse looks
        /// like moving a map rather than looking around.
        /// </summary>
        /// <summary>Whether third person is on, meaning any part of it is running.</summary>
        public bool thirdPersonOn => looking || walking;

        /// <summary>
        /// Puts everything back: the keys, the mouse, and the camera the caller hands over.
        ///
        /// The camera has to be given rather than remembered, because what counts as putting it
        /// back is what the paks say - which the panel knows and this does not.
        /// </summary>
        public void stopThirdPerson(CameraMod.Settings? restoreTo)
        {
            //Let go of the distance first, or the game's own volumes stay overruled after the
            //camera has been handed back.
            holdArmLength = null;
            _boundTo = IntPtr.Zero;

            stopLooking();
            stopWalking();



            if (restoreTo != null) { push(restoreTo, snap: true); }
        }

        public bool thirdPerson(float sensitivity, bool invert, out string problem)
        {
            problem = "";
            if (_camera == null) { problem = "Not attached to the game."; return false; }

            _camera.setArmLength(THIRD_PERSON_DISTANCE);
            _camera.setRotation(THIRD_PERSON_PITCH, _camera.Yaw ?? 45f);
            _camera.setFieldOfView(THIRD_PERSON_FIELD_OF_VIEW);

            //Framing, and the part that is easy to leave out. The game's own camera hangs its arm
            //at the character's feet and then pushes the camera end lower still - which cannot be
            //seen from twenty five metres up and is the entire view from seven, where it sits
            //somewhere around the knees.
            _camera.setTargetOffset(0f, 0f, THIRD_PERSON_PIVOT_HEIGHT);
            _camera.setSocketOffset(0f, THIRD_PERSON_SHOULDER, THIRD_PERSON_CAMERA_RISE);

            //Nothing should be in the way at head height, and the stock camera never needed to
            //care because nothing was ever between it and the floor.
            _camera.setCollisionTest(true);

            clicksDoNotWalk = true;
            holdArmLength = THIRD_PERSON_DISTANCE;
            applyPitchLimits(THIRD_PERSON_DISTANCE);

            if (!startWalking(out problem)) { return false; }
            if (!startLooking(sensitivity, invert)) { return false; }

            _boundTo = _camera.SpringArm;
            return true;
        }

        //Close enough to see the character rather than the floor plan, far enough back that a
        //swing still shows what it hits.
        private const float THIRD_PERSON_DISTANCE = 800f;
        private const float THIRD_PERSON_PITCH = -12f;
        //Sixty five rather than seventy five, because field of view turned out to be the only
        //thing that moves the frame rate: measured at a hundred and forty one enemies, 75 ran at
        //158 and 60 at 179. This keeps most of the width and gives most of the frames back.
        private const float THIRD_PERSON_FIELD_OF_VIEW = 65f;

        //The character's capsule is 110 half height, so its head is 110 above its origin and the
        //arm hangs 90 below it. Raising the pivot by 170 puts it around the shoulders.
        private const float THIRD_PERSON_PIVOT_HEIGHT = 170f;

        //Enough to the side that the character is not standing in the middle of what you are
        //trying to look at, and not so much that it reads as a camera pointed at their ear.
        private const float THIRD_PERSON_SHOULDER = 40f;
        private const float THIRD_PERSON_CAMERA_RISE = 20f;

        /// <summary>
        /// Sets third person up again on a character that did not exist a moment ago.
        ///
        /// Not the same as turning it on: nothing here asks for it, it is already on, and the
        /// person playing should not have to notice that the level changed. Only the framing needs
        /// putting back - the distance, the angle, where the arm hangs - because those live on the
        /// character and the new one has the game's own.
        /// </summary>
        private void reattach()
        {
            if (_camera == null) { return; }

            //Whatever was applied, rather than a fixed idea of third person - somebody who chose
            //first person should not arrive in the next level in third.
            var preset = _applied ?? new CameraPreset {
                Distance = _holdArmLength ?? THIRD_PERSON_DISTANCE,
                Pitch = THIRD_PERSON_PITCH,
                FieldOfView = THIRD_PERSON_FIELD_OF_VIEW,
                PivotHeight = THIRD_PERSON_PIVOT_HEIGHT,
                SocketSide = THIRD_PERSON_SHOULDER,
                SocketHeight = THIRD_PERSON_CAMERA_RISE,
            };

            _camera.setTargetOffset(0f, 0f, preset.PivotHeight);
            _camera.setSocketOffset(0f, preset.SocketSide, preset.SocketHeight);
            _camera.setFieldOfView(preset.FieldOfView);
            _camera.setRotationLagSpeed(preset.RotationLagSpeed);
            _camera.setLagSpeed(preset.LagSpeed);

            //And the value a jump hands back when it lands.
            _lagSpeedWalking = preset.LagSpeed;
            if (_walk != null) { _walk.LagSpeedWalking = preset.LagSpeed; }
            _camera.setCollisionTest(preset.Collision);
            _camera.setArmLength(preset.Distance);
            _camera.setRotation(preset.Pitch, _camera.Yaw ?? 45f);

            //The two loops are left alone. They walk to the character themselves every time round
            //now, so a new one is something they pick up without being restarted - and restarting
            //them here is what turned a level change into a crash, because stopping the keys hands
            //back a setting to a movement component the game has already destroyed.
            _boundTo = _camera.SpringArm;
        }

        private void drop()
        {
            stopLooking();
            stopWalking();
            _camera = null;
            _game?.Dispose();
            _game = null;
        }

        /// <summary>The camera the game started with, for putting it back.</summary>
        public CameraPreset? original => _original;

        /// <summary>What is applied at the moment, or nothing if the tab has not set anything.</summary>
        public CameraPreset? applied => _applied;

        /// <summary>
        /// Everything the camera is set to right now, as a preset.
        ///
        /// Used for two things that look the same and are not: remembering the game's own settings
        /// once at the start, and reading back what the sliders have produced so it can be saved
        /// under a name.
        /// </summary>
        public CameraPreset? readPreset()
        {
            if (_camera == null) { return null; }

            return new CameraPreset {
                Distance = _camera.DesiredLength ?? 2450f,
                Pitch = _camera.Pitch ?? -45f,
                FieldOfView = _camera.FieldOfView ?? 55f,
                PivotHeight = _camera.TargetOffsetHeight ?? 0f,
                SocketSide = _camera.SocketSide ?? 0f,
                SocketHeight = _camera.SocketHeight ?? -80f,
                RotationLagSpeed = _camera.RotationLagSpeed ?? 40f,
                LagSpeed = _camera.LagSpeed ?? 1f,
                Collision = _camera.CollisionTest ?? false,
                MouseLook = looking,
                Wasd = walking,
            };
        }

        /// <summary>
        /// Puts a whole way of looking at the game into the running game.
        ///
        /// Everything at once rather than setting by setting, because these numbers only make
        /// sense together - a distance of nothing wants the pivot at eye height and no shoulder
        /// offset, and applying half of that is a camera inside a chest.
        /// </summary>
        public bool apply(CameraPreset preset, float sensitivity, bool invert, out string problem)
        {
            problem = "";
            if (_camera == null) { problem = "Not attached to the game."; return false; }

            _applied = preset.copy();

            _camera.setTargetOffset(0f, 0f, preset.PivotHeight);
            _camera.setSocketOffset(0f, preset.SocketSide, preset.SocketHeight);
            _camera.setFieldOfView(preset.FieldOfView);
            _camera.setRotationLagSpeed(preset.RotationLagSpeed);
            _camera.setLagSpeed(preset.LagSpeed);

            //And the value a jump hands back when it lands.
            _lagSpeedWalking = preset.LagSpeed;
            if (_walk != null) { _walk.LagSpeedWalking = preset.LagSpeed; }
            _camera.setCollisionTest(preset.Collision);
            _camera.snapArmLength(preset.Distance);
            _camera.setRotation(preset.Pitch, _camera.Yaw ?? 45f);

            //Held from now on, or the first camera volume walked into undoes it.
            holdArmLength = preset.Distance;
            applyPitchLimits(preset.Distance);


            if (preset.Wasd && !walking && !startWalking(out problem)) { return false; }
            if (!preset.Wasd && walking) { stopWalking(); }

            if (preset.MouseLook && !looking && !startLooking(sensitivity, invert)) { return false; }
            if (!preset.MouseLook && looking) { stopLooking(); }

            _boundTo = _camera.SpringArm;
            return true;
        }

        /// <summary>Puts the game's own camera back, and lets go of everything.</summary>
        public void restoreOriginal()
        {
            holdArmLength = null;
            _boundTo = IntPtr.Zero;
            _applied = null;

            stopLooking();
            stopWalking();

            if (_camera == null || _original == null) { return; }

            _camera.setTargetOffset(0f, 0f, _original.PivotHeight);
            _camera.setSocketOffset(0f, _original.SocketSide, _original.SocketHeight);
            _camera.setFieldOfView(_original.FieldOfView);
            _camera.setRotationLagSpeed(_original.RotationLagSpeed);
            _camera.setLagSpeed(_original.LagSpeed);
            _camera.setCollisionTest(_original.Collision);
            _camera.snapArmLength(_original.Distance);
            _camera.setRotation(_original.Pitch, _camera.Yaw ?? 45f);
        }

        /// <summary>What the game's camera is set to right now, or nothing.</summary>
        public CameraMod.Settings? read()
        {
            if (_camera == null) { return null; }

            return new CameraMod.Settings {
                ArmLength = _camera.DesiredLength,
                Pitch = _camera.Pitch,
                Yaw = _camera.Yaw,
                Roll = _camera.Roll,
                FieldOfView = _camera.FieldOfView,
                RotationLagSpeed = _camera.RotationLagSpeed,
                InheritYaw = _camera.InheritYaw,
                InheritPitch = _camera.InheritPitch,
                CollisionTest = _camera.CollisionTest,
                ControllerYaw = _camera.ControllerYaw,
            };
        }

        /// <summary>
        /// Puts the settings into the running game, and says how many took.
        ///
        /// Only what was actually set is written - a null means the panel is leaving that value
        /// alone, which is not the same as wanting it zeroed.
        ///
        /// Only turn rate has no live equivalent, and it is skipped rather than faked - it
        /// belongs to the character's movement rather than to the camera. It still works through
        /// a pak. The seek length is skipped too, having turned out not to drive anything at all.
        /// </summary>
        public int push(CameraMod.Settings settings, bool snap = false)
        {
            if (_camera == null) { return 0; }

            var done = 0;

            if (settings.ArmLength is float arm)
            {
                //Dragging the slider moves what is being held, or the next camera volume would
                //put it straight back to whatever third person started with.
                if (_holdArmLength != null) { holdArmLength = arm; }
                applyPitchLimits(arm);

                if (snap ? _camera.snapArmLength(arm) : _camera.setArmLength(arm)) { done++; }
            }

            //Pitch and yaw go together in one write, so a camera being swung cannot be caught
            //with a new pitch and a stale yaw.
            if (settings.Pitch is float pitch || settings.Yaw is float yaw)
            {
                if (_camera.setRotation(
                        settings.Pitch ?? _camera.Pitch ?? 0f,
                        settings.Yaw ?? _camera.Yaw ?? 0f,
                        settings.Roll ?? _camera.Roll ?? 0f))
                {
                    done++;
                }
            }

            if (settings.FieldOfView is float fov && _camera.setFieldOfView(fov)) { done++; }
            if (settings.RotationLagSpeed is float lag && _camera.setRotationLagSpeed(lag)) { done++; }

            if (settings.SocketHeight is float || settings.SocketSide is float)
            {
                if (_camera.setSocketOffset(0f, settings.SocketSide ?? 0f, settings.SocketHeight ?? 0f))
                {
                    done++;
                }
            }

            //Not on the camera, and so missed the first time round: this one lives on the
            //character. Without it "camera follows player" has nothing to follow, because the
            //actor never turns - only its mesh does.
            if (settings.ControllerYaw is bool faceAim && _camera.setControllerYaw(faceAim)) { done++; }

            if (settings.InheritYaw is bool followYaw && _camera.setInheritYaw(followYaw)) { done++; }
            if (settings.InheritPitch is bool followPitch && _camera.setInheritPitch(followPitch)) { done++; }
            if (settings.CollisionTest is bool collide && _camera.setCollisionTest(collide)) { done++; }

            return done;
        }

        /// <summary>
        /// A key that toggles the whole thing, pressed from inside the game.
        ///
        /// Worth having rather than only a control in the window, because turning third person on
        /// pins the pointer to the middle of the screen - so the one moment somebody most wants
        /// to switch it off is the moment they cannot reach a button to do it with. Escape
        /// releases the pointer, and this puts everything back without leaving the game at all.
        /// </summary>
        public event Action? togglePressed;

        //F10. Out of the way of anything Dungeons binds, and of the usual screenshot keys.
        private const int TOGGLE_KEY = 0x79;

        //The keys that open something you need a pointer for.
        //
        //Watched rather than detected. The obvious way would be to ask the game whether a menu is
        //up, and the flags that ought to say so - the cursor being shown, click events being
        //enabled - are true the whole time in a game played by clicking, so they say nothing.
        //
        //These only ever hand the pointer *back*, and something else takes it again - because a
        //key that toggles is a key that can be wrong, and there is no way to be right about the
        //state to begin with. Nothing here knows whether a menu was already open when the game
        //started, or which key closed the one that was.
        private static readonly int[] MENU_KEYS = {
            0x49,  // I, inventory
            0x1B,  // Escape, pause
            0x09,  // Tab
            0x4D,  // M, map
        };

        //And this takes it back, because walking is proof of not being in a menu.
        //
        //Which is the answer to a question the toggle could not answer: the state is not tracked,
        //it is observed. Starting suspended assumes a menu, since assuming the wrong way pins the
        //pointer somewhere it cannot be used - and the first step taken says otherwise and hands
        //it straight back. Whatever closed the menu, and whatever was open when the game started,
        //one press of a movement key puts it right.
        private static readonly int[] PLAYING_KEYS = { 0x57, 0x41, 0x53, 0x44 };  // W A S D

        [System.Runtime.InteropServices.DllImport("user32.dll")]
        private static extern short GetAsyncKeyState(int key);

        private System.Threading.Thread? _hotkey;
        private volatile bool _watchingHotkey;
        private bool _wasDown;
        private bool _menuWasDown;

        //Starts true, on the assumption that nothing is known yet - the game corrects it within a
        //frame or two of being asked. See PLAYING_KEYS for what happens if it will not say.
        private bool _suspended = true;

        /// <summary>
        /// Whether the game has the pointer and the keys back for the moment.
        ///
        /// Turned on by the keys that open a menu, and off by the same key again. Nothing is
        /// stopped - the camera keeps the angle it had, and everything picks up where it left off.
        /// </summary>
        public bool suspended
        {
            get => _suspended;
            set
            {
                _suspended = value;
                if (_look != null) { _look.Suspended = value; }
                if (_walk != null) { _walk.Suspended = value; }
                suspendedChanged?.Invoke();
            }
        }

        /// <summary>Raised when a menu key hands the pointer over, or takes it back.</summary>
        public event Action? suspendedChanged;

        private void watchHotkey()
        {
            _watchingHotkey = true;
            _hotkey = new System.Threading.Thread(() => {
                while (_watchingHotkey)
                {
                    System.Threading.Thread.Sleep(60);

                    //On the way down only. Polling a held key sixteen times a second would
                    //otherwise toggle it sixteen times.
                    var down = (GetAsyncKeyState(TOGGLE_KEY) & 0x8000) != 0;
                    if (down && !_wasDown) { togglePressed?.Invoke(); }
                    _wasDown = down;

                    //The game itself, when it will say. This is the whole of it: the pointer goes
                    //back the instant a menu opens and is taken again the instant it closes, with
                    //nothing inferred and nothing to get out of step.
                    var menuOpen = _camera?.MenuOpen;
                    if (menuOpen != null)
                    {
                        if (menuOpen.Value != _suspended) { suspended = menuOpen.Value; }
                        continue;
                    }

                    //And when it will not - between levels, or if a future build moves the byte -
                    //the keys still work. Worse, but never stuck: whatever opened something hands
                    //the pointer back, and the next step taken picks it up again.
                    var menu = false;
                    foreach (var key in MENU_KEYS)
                    {
                        if ((GetAsyncKeyState(key) & 0x8000) != 0) { menu = true; break; }
                    }
                    if (menu && !_menuWasDown && !_suspended) { suspended = true; }
                    _menuWasDown = menu;

                    if (!_suspended) { continue; }

                    foreach (var key in PLAYING_KEYS)
                    {
                        if ((GetAsyncKeyState(key) & 0x8000) == 0) { continue; }

                        suspended = false;
                        break;
                    }
                }
            }) { IsBackground = true, Name = "third person hotkey" };
            _hotkey.Start();
        }

        public void Dispose()
        {
            _watch.Stop();
            _watchingHotkey = false;
            _hotkey?.Join(200);
            drop();
        }

    }
}
