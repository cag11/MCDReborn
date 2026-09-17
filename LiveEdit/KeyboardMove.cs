using System;
using System.Runtime.InteropServices;
using System.Threading;

namespace LiveEdit
{
    /// <summary>
    /// Walking with the keyboard, in a game that has no keys for walking.
    ///
    /// Dungeons is played by clicking where you want to go. There are no movement bindings to
    /// rebind - not hidden ones, not disabled ones - so nothing can be configured into existence,
    /// and the movement has to come from somewhere else.
    ///
    /// The first attempt was a virtual Xbox controller, which worked and was unusable: the game
    /// switches its whole interface between keyboard and controller depending on what it heard
    /// last, so a pad reporting sticks while a mouse reported motion made it change its mind many
    /// times a second, and the frame rate went with it.
    ///
    /// This does not pretend to be a device at all. `AddMovementInput` accumulates into one vector
    /// on the pawn, and the engine consumes it each frame and clears it - so writing
    /// that vector is not an imitation of a keypress, it is the same thing a keypress would
    /// eventually become. The game accelerates, animates, collides and turns exactly as it does
    /// for its own input, because past this point it is its own input.
    ///
    /// Written every frame by necessity rather than by choice: consuming it clears it, so a vector
    /// written once moves the character for a single frame and stops. Faster than the game ticks,
    /// too, and by enough of a margin to survive the scheduler - a frame that ticks with no input
    /// behind it reads as letting go of the key, and this game brakes hard enough to make one of
    /// those a visible stumble.
    ///
    /// A click still attacks, and is meant to. Melee needs the cursor to resolve what it is
    /// swinging at, so the click has to reach the game - the config's keyboard bindings for
    /// attacking turned out to do nothing in a shipped build, tested by pressing them.
    ///
    /// Making that click swing rather than walk took the game's own answer, which a player knew
    /// and none of this worked out: hold shift, and the character plants its feet. Cancelling the
    /// destination afterwards was never going to do it, because the game chooses between walking
    /// and swinging as the button goes down - by the time there is a destination to refuse, the
    /// swing has already been declined.
    ///
    /// It can also jump, which the game cannot. Not by asking the character to jump - the flag
    /// for that is ignored here - but by writing a launch velocity the movement component picks up
    /// by itself on its next tick. That keeps a jump on the right side of the line this whole
    /// project runs on: data can be written from out here, behaviour cannot be called.
    ///
    /// One thing comes with it. The character's facing normally follows the aim, which is right
    /// for a game played by clicking where you want to go and wrong the moment a keyboard is
    /// involved - the two come apart and the character slides across the floor facing somewhere
    /// else. So while this is running the movement component is asked to face where it is going
    /// instead, and put back the way it was when it stops.
    ///
    /// That clearing is also how this was proved rather than hoped for. Writing the vector and
    /// finding it zero a moment later says two things at once - the field is the one the engine
    /// reads, and the game is actually ticking rather than paused in the background. Measured at
    /// six times out of six, followed by the character walking 1381 units in two seconds at
    /// exactly its own MaxWalkSpeed.
    /// </summary>
    public sealed class KeyboardMove : IDisposable
    {
        //Faster than the game's frame rate on purpose, and by a wider margin than it looks.
        //
        //The vector is cleared when it is consumed, so a frame that ticks without a write behind
        //it is a frame of standing still. That sounds harmless and is not: this character has a
        //ground friction of 100 and a braking deceleration of nine and a half million, so one
        //input-less frame does not cost a little speed, it stops you. Measured while walking on
        //the keys, the speed collapsed and rebuilt over and over - 700, 645, 278, 182, 179, 204,
        //413 - while clicking to the same place held a steady 680, because a destination goes
        //through the requested velocity and never has a gap in it.
        //
        //Eight milliseconds was already meant to be twice a frame. It was not: Sleep on Windows
        //rounds up to the system timer, about 15.6ms by default, so the loop ran slower than the
        //game and skipped frames rather than doubling them.
        private const int EVERY_MS = 4;

        //Asking Windows for a timer that can actually do that.
        //
        //Without it every sleep below about 15ms is that long instead. It reaches outside this
        //process, so it is asked for on the first step, given back a second and a half after the
        //last one, and never held while a menu is open or the game is in the background.
        [DllImport("winmm.dll")] private static extern uint timeBeginPeriod(uint milliseconds);
        [DllImport("winmm.dll")] private static extern uint timeEndPeriod(uint milliseconds);

        private const uint TIMER_RESOLUTION_MS = 1;

        //How often to look when nobody is walking.
        //
        //The fast rate exists so that no frame of walking goes without input behind it. Standing
        //still needs none of that - the only reason to write anything is to refuse a destination
        //a click posted, and once every sixteen milliseconds is plenty for that. Four times fewer
        //wakeups for the whole time somebody is reading their inventory or picking a mission.
        private const int IDLE_MS = 16;

        //How long after the last step to keep asking for the fast timer.
        //
        //Raising the system timer is the only thing here that reaches outside this process, and
        //it was being held for the entire session - from the moment the keys were switched on
        //until they were switched off, whether anybody was walking or not. Now it is asked for on
        //the first step and given back shortly after the last one, which is the difference between
        //affecting the machine while you play and affecting it while you walk.
        private const int KEEP_TIMER_MS = 1500;

        //Virtual key codes.
        private const int W = 0x57, A = 0x41, S = 0x53, D = 0x44;

        //Control rather than shift, and deliberately.
        //
        //Shift is the game's own RootPlayer - hold it and the character plants its feet so a
        //click swings at something out of reach instead of walking to it. Taking shift for a
        //slow walk would have quietly broken that, and the two would have fought every time:
        //one asking to move gently, the other refusing to move at all.
        private const int SLOWLY = 0xA2;  // Left Control

        //Q, and not the space bar, which was the first idea and was a bad one.
        //
        //Sharing the key with the roll sounded like a way to get a roll that leaves the ground.
        //It can be made to work, and it is worse: a dodge and a jump at once is hard to aim and
        //hard to stop doing by accident. A jump is better on its own key, and Q is free.
        private const int JUMP = 0x51;

        //Not here: holding the character's body still.
        //
        //The body does not stay put relative to the capsule the camera is bolted to - recorded on a
        //staircase it swings about thirty units, and with the camera on the crown of the head that
        //is enough to bring the model into view. Holding it at the bottom of its capsule, where
        //Unreal puts it, looked like the obvious answer.
        //
        //Measured, it is four times worse: the swing went from 31.8 units to 124.4 with the hold
        //running at four milliseconds. Whatever moves the mesh moves it every frame and does not
        //take kindly to being argued with, and the argument is louder than the thing it was trying
        //to quiet. Same shape as correcting the arm length the camera is placed with, and the same
        //result.
        //
        //Left as a note, with the number, so this looks like a good idea to nobody again.

        //G, which nothing in this game uses, and the movement mode that turns gravity off.
        //
        //Flying is the one movement mode that does not argue. A walking character pulled upwards is
        //put straight back down - measured, a lift of 240 units came back as zero - and a flying one
        //stays exactly where it is put, drifting 0.0 over four seconds. That makes going up a matter
        //of writing a position rather than fighting for one.
        //
        //The direction comes from where the camera is pointed rather than from extra keys. Looking
        //down and holding forward flies downwards, which is how every flying camera works and needs
        //nothing bound to it.
        private const int FLY = 0x47;
        private const int FLYING_MODE = 5;
        private const int WALKING_MODE = 1;

        //And the speed flying actually reads, which is not the one walking reads.
        //
        //Writing MaxWalkSpeed and then flying is how the first version of this left you hanging in
        //the air at the game's own six hundred: a flying character never looks at MaxWalkSpeed. It
        //has its own number, forty two bytes further along, and that is the one that moves you.
        private const int MAX_FLY_SPEED = 0x01E8;
        private const int BRAKING_FLYING = 0x0210;

        //Up and down, because looking up is not always possible. The camera's tilt is clamped - as
        //far as five degrees above level from behind the character - so steering by the camera
        //alone means never being able to climb. Space goes up, left control goes down, and the
        //camera's tilt is still added on top for whoever wants to dive by looking.
        private const int ASCEND = 0x20;
        private const int DESCEND = 0xA2;

        //How a character here leaves the ground, which is not how it first looked.
        //
        //The obvious way is bPressedJump, the flag Unreal's own jump sets. It does nothing in this
        //game, and the test that said otherwise was measuring a character already falling off a
        //ledge - a measurement worth rather more than the conclusion that came out of it.
        //
        //PendingLaunchVelocity works. The movement component reads it every tick, copies it into
        //the velocity, switches to falling and zeroes it again, all by itself. Writing it is the
        //entire action: nothing to call, and nothing to clean up afterwards.
        private const int PENDING_LAUNCH_VELOCITY = 0x0408;
        private const int MOVEMENT_MODE = 0x01B0;
        private const byte WALKING = 1;

        //UCharacterMovementComponent. Steering in mid-air; the game's own value is 0.05, which is
        //almost none, and perfectly reasonable for a character that never jumps.
        private const int AIR_CONTROL = 0x0214;

        //The left mouse button, and the key the game roots the player with.
        private const int LEFT_BUTTON = 0x01;
        private const ushort LEFT_SHIFT = 0xA0;

        private const uint INPUT_KEYBOARD = 1;
        private const uint KEYEVENTF_KEYUP = 0x0002;

        //APawn, and not the movement component - which is where this was written first, and why
        //the keys did nothing. The offset is the same number in both places in the SDK listing,
        //so a glance at 0x0374 confirms nothing; the class it belongs to is the whole of it. It
        //sits beside bUseControllerRotationYaw at 0x0338 on the pawn, which is the giveaway.
        //
        //This is where AddMovementInput accumulates and where ConsumeInputVector takes it from.
        private const int CONTROL_INPUT_VECTOR = 0x0374;

        [DllImport("user32.dll")] private static extern short GetAsyncKeyState(int key);
        [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();

        [StructLayout(LayoutKind.Sequential)]
        private struct KeyboardInput
        {
            public ushort VirtualKey, ScanCode;
            public uint Flags, Time;
            public IntPtr Extra;
        }

        //An INPUT is a tag and a union laid out for its largest member, so the keyboard part is
        //followed by the room the mouse part would have needed.
        [StructLayout(LayoutKind.Sequential)]
        private struct Input
        {
            public uint Type;
            public KeyboardInput Key;
            public int PadA, PadB;
        }

        [DllImport("user32.dll", SetLastError = true)]
        private static extern uint SendInput(uint count, Input[] inputs, int size);

        private readonly GameProcess _game;
        private readonly LiveCamera _camera;

        //Walked again every time round, rather than remembered.
        //
        //The pawn and the movement component are the character's own, and a level change frees
        //both. Writing into them afterwards crashed the game on the way back to the camp - and
        //the worst of it was the tidying up: putting the rotation flags back wrote a byte into a
        //component that had already been destroyed.
        private readonly LiveCamera _live;

        //Which movement component the facing was changed on, and what it was before. Kept
        //together so the flags are only ever handed back to the component they were taken from,
        //and only while it is still the live one.
        private IntPtr _oriented;
        private byte? _flagsWere;

        private Thread? _thread;
        private volatile bool _running;

        private KeyboardMove(GameProcess game, LiveCamera camera)
        {
            _game = game;
            _camera = camera;
            _live = new LiveCamera(game);
        }

        /// <summary>
        /// Asks the character to face where it is going, remembering what it said before.
        ///
        /// Done again for each new character rather than once, because each level brings its own
        /// and the old one's setting is not something that can be handed back.
        /// </summary>
        private void orientToMovement(IntPtr movement)
        {
            var flags = _game.read(new IntPtr(movement.ToInt64() + MOVEMENT_ROTATION_FLAGS), 1);
            if (flags == null) { return; }

            _oriented = movement;
            _flagsWere = flags[0];
            _game.write(new IntPtr(movement.ToInt64() + MOVEMENT_ROTATION_FLAGS),
                new[] { (byte)(flags[0] | (1 << BIT_ORIENT_TO_MOVEMENT)) });
        }

        /// <summary>
        /// Puts the facing back, but only onto a component the game still has.
        ///
        /// The check is the point. Restoring blindly is what turned a level change into a crash:
        /// by the time anything here knows it is finished, the character it borrowed from may not
        /// exist, and a byte written into that is a byte written into whatever took its place.
        /// </summary>
        /// <summary>Hands the camera smoothing back, if a jump was holding it rigid.</summary>
        private void restoreLag()
        {
            if (!_rigid) { return; }

            if (_live.find(out _)) { _live.setLagSpeed(LagSpeedWalking); }
            _rigid = false;
        }

        private void restoreFacing()
        {
            if (_oriented == IntPtr.Zero || _flagsWere is not byte flags) { return; }

            if (_live.find(out _) && movementOf(_live.Pawn) == _oriented)
            {
                _game.write(new IntPtr(_oriented.ToInt64() + MOVEMENT_ROTATION_FLAGS), new[] { flags });
            }

            _oriented = IntPtr.Zero;
            _flagsWere = null;
        }

        private IntPtr movementOf(IntPtr pawn)
        {
            return pawn == IntPtr.Zero
                ? IntPtr.Zero
                : readPointer(_game, new IntPtr(pawn.ToInt64() + CHARACTER_MOVEMENT));
        }

        /// <summary>
        /// Attaches to the character's movement, or says why it could not.
        ///
        /// The movement component is checked rather than assumed, by reading a walk speed out of
        /// it: a plausible speed means the pointer is the thing it is supposed to be, and a zero
        /// or a wild number means the chain led somewhere else and nothing should be written.
        /// </summary>
        public static KeyboardMove? attach(GameProcess game, LiveCamera camera, out string problem)
        {
            problem = "";

            if (camera.Pawn == IntPtr.Zero)
            {
                problem = "No character to move yet.";
                return null;
            }

            var movement = readPointer(game, new IntPtr(camera.Pawn.ToInt64() + CHARACTER_MOVEMENT));
            if (movement == IntPtr.Zero)
            {
                problem = "The character has no movement component.";
                return null;
            }

            var walkSpeed = game.readFloat(new IntPtr(movement.ToInt64() + MAX_WALK_SPEED));
            if (walkSpeed == null || walkSpeed < 1f || walkSpeed > 100000f)
            {
                problem = "The movement component is not where it was expected - this build may differ.";
                return null;
            }

            //Neither pointer is kept. They are walked to again every time round the loop, because
            //a level change frees both and writing into them afterwards crashes the game.
            return new KeyboardMove(game, camera);
        }

        //ACharacter, and the speed used to prove the pointer.
        private const int CHARACTER_MOVEMENT = 0x0398;
        private const int MAX_WALK_SPEED = 0x01DC;

        //UCharacterMovementComponent's rotation flags, and the one that stops the moonwalking.
        //
        //Left alone, the character's facing comes from where the player is aiming, which is fine
        //in a game played by clicking where you want to go - you always walk towards what you are
        //pointing at. Give it a keyboard and the two come apart: measured at a hundred and sixty
        //eight degrees between the way it was going and the way it was facing, which is a
        //character sliding backwards across the floor.
        //
        //This is Unreal's own answer, and it takes precedence over the aim when there is input:
        //face where you are going. It suits this game particularly because there are no sideways
        //animations to play - a character that always runs the way it faces always has the right
        //animation for what it is doing.
        private const int MOVEMENT_ROTATION_FLAGS = 0x0240;
        private const int BIT_ORIENT_TO_MOVEMENT = 3;

        //Where a click sends you, and how to refuse it.
        //
        //The game binds a left click to two actions at once - MainAttack and SetDestination - and
        //there is no way to accept one and decline the other, because it is one key press. The
        //keyboard bindings the config offers for attacking, K and L, turn out to be inert in a
        //shipped build, so the click has to be allowed through to swing at all.
        //
        //Which leaves cancelling where it sends you, every frame, after it has been sent. A
        //destination becomes movement one of two ways and it is cheaper to refuse both than to
        //find out which: the input vector, and a requested velocity from path following. Three
        //writes, against a frame that was going to happen anyway.
        private const int MOVEMENT_STATE_FLAGS = 0x03D2;
        private const int BIT_HAS_REQUESTED_VELOCITY = 0;
        private const int REQUESTED_VELOCITY = 0x03E8;

        public bool IsRunning => _running;

        /// <summary>
        /// Whether clicking is allowed to walk the character somewhere.
        ///
        /// On, the keys are the only thing that moves you and a click is only ever an attack.
        /// Off, the game behaves as it always did, which is what somebody wants who is using the
        /// camera without the keys.
        /// </summary>
        public bool ClicksDoNotWalk { get; set; } = true;

        /// <summary>Whether Q leaves the ground.</summary>
        public bool CanJump { get; set; }

        /// <summary>
        /// How fast the launch is, which is not the same as how high it goes.
        ///
        /// Gravity here is 5000, so the height is this squared over ten thousand: 1000 clears your
        /// own waist, 1500 clears your head, and 3000 is nine hundred units and a long way down.
        /// </summary>
        public float JumpHeight { get; set; } = 1500f;

        /// <summary>How much steering there is in mid-air. The game gives 0.05, which is almost none.</summary>
        public float AirControl { get; set; } = 0.35f;

        /// <summary>How many jumps before touching the ground again.</summary>
        public int JumpCount { get; set; } = 1;

        /// <summary>
        /// How quickly the camera chases the character on the ground, and in the air.
        ///
        /// Two numbers because no single one works. The camera lag that stops a jump leaving the
        /// camera behind is the same lag that was smoothing out every stair step, and measuring it
        /// properly showed the trade is continuous - there is no value that does both. Recorded
        /// walking down a staircase and replayed through the engine's own lag maths:
        ///
        ///     lag speed   1     shake 0.015     lag speed  25     shake 0.177
        ///     lag speed  10     shake 0.082     lag speed 100     shake 0.680
        ///
        /// against a capsule that jitters 1.717 by itself. One is invisible and a hundred passes
        /// through two fifths of it.
        ///
        /// They are never needed at once, though. Stairs want smoothing while walking; a jump wants
        /// rigidity while airborne. So the camera is smooth until this launches one, rigid until the
        /// character lands, and smooth again after - and stairs never enter that state, because
        /// nothing here launched them.
        /// </summary>
        public float LagSpeedWalking { get; set; } = 1f;

        public float LagSpeedJumping { get; set; } = 100f;

        /// <summary>Whether G switches flying on and off.</summary>
        public bool CanFly { get; set; }

        /// <summary>How fast flying is, which wants to be a lot faster than walking.</summary>
        public float FlySpeed { get; set; } = 4000f;

        /// <summary>Whether it is currently flying, for anything that wants to show it.</summary>
        public bool Flying => _flying;

        private bool _flying;
        private bool _flyHeld;
        private bool _jumpHeld;
        private int _jumpsUsed;
        private bool _rigid;

        /// <summary>
        /// Whether holding the left button also plants the character's feet.
        ///
        /// This is the game's own answer to the problem, and it was a player who knew it: hold
        /// shift and a click swings where it is pointed instead of walking there. The action is
        /// called RootPlayer and the config binds it to LeftShift.
        ///
        /// Which is a better answer than anything reached for here. Cancelling the destination
        /// after the fact never worked, because the game decides between walking and swinging at
        /// the moment of the click - by the time there is a destination to refuse, the swing has
        /// already been declined. Rooting changes the decision rather than its consequence.
        ///
        /// Held only while the button is down, so it is the game's behaviour during an attack and
        /// nothing at all the rest of the time.
        /// </summary>
        public bool AttackRoots { get; set; } = true;

        private bool _rooting;

        //Set while a menu is open. Walking on through an inventory screen would be its own kind
        //of wrong, and the keys are the game's to read there.
        private volatile bool _suspended;

        /// <summary>Whether the keys are being left to the game for the moment.</summary>
        public bool Suspended
        {
            get => _suspended;
            set => _suspended = value;
        }

        /// <summary>Raised when it stops by itself, so a panel can untick its box.</summary>
        public event Action? stopped;

        public bool start()
        {
            if (_running) { return true; }

            _running = true;
            _thread = new Thread(run) { IsBackground = true, Name = "keyboard movement" };
            _thread.Start();
            return true;
        }

        public void stop() => _running = false;

        private void run()
        {
            try { drive(); }
            finally { lowerTimer(); }

            _running = false;
            stopped?.Invoke();
        }

        private bool _timerRaised;
        private int _lastStep;

        private void raiseTimer()
        {
            if (_timerRaised) { return; }

            timeBeginPeriod(TIMER_RESOLUTION_MS);
            _timerRaised = true;
        }

        private void lowerTimer()
        {
            if (!_timerRaised) { return; }

            timeEndPeriod(TIMER_RESOLUTION_MS);
            _timerRaised = false;
        }

        private void drive()
        {
            while (_running)
            {
                //Fast while walking, slow while not. Using last time round's answer costs one
                //idle frame of latency on the first step and saves three quarters of the wakeups
                //for all the time in between.
                Thread.Sleep(_timerRaised ? EVERY_MS : IDLE_MS);

                if (!_game.IsRunning) { break; }

                //Only while the game is in front. Otherwise typing a message with a W in it walks
                //somebody into a wall in a window they are not looking at.
                if (GetForegroundWindow() != _game.Process.MainWindowHandle)
                {
                    //A key held down when the window changed would stay held, and shift is not a
                    //thing to leave pressed in somebody else's window.
                    releaseRoot();
                    lowerTimer();
                    continue;
                }

                if (_suspended)
                {
                    //Whatever was being held is let go of, or it stays held into the menu.
                    releaseRoot();
                    lowerTimer();
                    continue;
                }

                //The character as it is now. Between levels there is not one, and this idles.
                if (!_live.find(out _)) { continue; }

                var pawn = _live.Pawn;
                var movement = movementOf(pawn);
                if (movement == IntPtr.Zero) { continue; }

                //A new character needs asking again, and the old one's setting is gone with it.
                if (movement != _oriented) { orientToMovement(movement); }

                if (AttackRoots) { rootWhileAttacking(); } else { releaseRoot(); }
                if (CanJump) { jumpOnPress(movement); }
                if (CanFly) { flyOnPress(movement); }

                var forward = 0f;
                var sideways = 0f;

                if (down(W)) { forward += 1f; }
                if (down(S)) { forward -= 1f; }
                if (down(D)) { sideways += 1f; }
                if (down(A)) { sideways -= 1f; }

                //Counted as movement while flying, so that rising straight up from a standstill
                //raises the timer and is written like any other direction. Without this, holding
                //space and nothing else is a key press that never reaches the game.
                var climbing = _flying && (down(ASCEND) || down(DESCEND));

                //Standing still is written too, rather than skipped. Leaving the vector alone is
                //what lets a destination posted by a click quietly move the character while no key
                //is held - which reads as the character wandering off on its own.
                if (forward != 0f || sideways != 0f || climbing)
                {
                    raiseTimer();
                    _lastStep = Environment.TickCount;
                }
                else if (_timerRaised && Environment.TickCount - _lastStep > KEEP_TIMER_MS)
                {
                    lowerTimer();
                }

                //Rising straight up counts as going somewhere. Without the climbing test this
                //returns here with a zero vector, which is why holding space while flying did
                //nothing at all: the vertical maths further down was never reached.
                if (forward == 0f && sideways == 0f && !climbing)
                {
                    if (ClicksDoNotWalk) { refuseDestination(movement); writeInput(pawn, 0f, 0f); }
                    continue;
                }

                //Relative to the camera, which is the only thing that makes sense once the camera
                //can be pointed anywhere: W is away from the viewer, not north. Read each frame
                //rather than remembered, because mouse look is turning it at the same time.
                var yaw = _camera.Yaw ?? 0f;
                var radians = yaw * Math.PI / 180.0;
                var cos = Math.Cos(radians);
                var sin = Math.Sin(radians);

                //Unreal's forward is X and its right is Y at a yaw of zero.
                var x = forward * cos - sideways * sin;
                var y = forward * sin + sideways * cos;

                //Diagonals would otherwise be a little over a fortieth faster than straight lines,
                //which is small enough to feel as the character subtly preferring corners.
                var length = Math.Sqrt(x * x + y * y);
                if (length > 0.0001)
                {
                    x /= length;
                    y /= length;
                }

                //A direction has no magnitude the way a stick does, so going slowly needs a key of
                //its own.
                var scale = !_flying && down(SLOWLY) ? 0.5 : 1.0;

                //Flying goes where the camera looks, so forward becomes forward and down at once.
                //Walking does not, because a walking character that tilts is a falling one.
                var up = 0.0;
                if (_flying)
                {
                    //Where the camera looks, which handles diving without a key for it.
                    var tilt = (_live.Pitch ?? 0f) * Math.PI / 180.0;
                    up = Math.Sin(tilt) * forward;

                    var flat = Math.Cos(tilt);
                    x *= flat;
                    y *= flat;

                    //And straight up or down regardless of where it is pointed, because the tilt
                    //is clamped and climbing by looking up is not always available.
                    if (down(ASCEND)) { up += 1.0; }
                    if (down(DESCEND)) { up -= 1.0; }

                    if (up > 1.0) { up = 1.0; }
                    if (up < -1.0) { up = -1.0; }
                }

                if (ClicksDoNotWalk) { refuseDestination(movement); }
                writeInput(pawn, (float)(x * scale), (float)(y * scale), (float)(up * scale));
            }

        }

        /// <summary>
        /// Presses the game's root key for as long as the left button is down.
        ///
        /// Edges only. Sending the key every eight milliseconds would be sixty held keypresses a
        /// second, which the game would read as sixty separate presses.
        /// </summary>
        private void rootWhileAttacking()
        {
            var attacking = (GetAsyncKeyState(LEFT_BUTTON) & 0x8000) != 0;

            if (attacking && !_rooting) { press(LEFT_SHIFT, false); _rooting = true; }
            else if (!attacking && _rooting) { press(LEFT_SHIFT, true); _rooting = false; }
        }

        private void releaseRoot()
        {
            if (!_rooting) { return; }

            press(LEFT_SHIFT, true);
            _rooting = false;
        }

        private static void press(ushort key, bool releasing)
        {
            var input = new Input {
                Type = INPUT_KEYBOARD,
                Key = new KeyboardInput { VirtualKey = key, Flags = releasing ? KEYEVENTF_KEYUP : 0 },
            };
            SendInput(1, new[] { input }, Marshal.SizeOf<Input>());
        }

        /// <summary>
        /// Launches the character upwards on a fresh press of the jump key.
        ///
        /// One write per press rather than one per frame, because the movement component consumes
        /// the launch and zeroes it - writing it every frame would be a character that never comes
        /// back down. The key has to be let go and pressed again for the next one.
        ///
        /// Air jumps are counted here rather than by the game, which has no idea any of this is
        /// happening. The count resets the moment the character is walking again.
        /// </summary>
        private void jumpOnPress(IntPtr movement)
        {
            _game.writeFloat(new IntPtr(movement.ToInt64() + AIR_CONTROL), AirControl);

            var mode = _game.read(new IntPtr(movement.ToInt64() + MOVEMENT_MODE), 1);
            if (mode != null && mode[0] == WALKING)
            {
                _jumpsUsed = 0;

                //Back on the ground, so the camera goes back to being smoothed. Landing is the
                //right moment for it: the camera is already exactly on the character, so there is
                //nothing to ease and nothing to see.
                if (_rigid) { _rigid = !_live.setLagSpeed(LagSpeedWalking); }
            }

            if (!down(JUMP)) { _jumpHeld = false; return; }
            if (_jumpHeld) { return; }

            _jumpHeld = true;
            if (_jumpsUsed >= JumpCount) { return; }

            var launch = new byte[12];
            Buffer.BlockCopy(BitConverter.GetBytes(JumpHeight), 0, launch, 8, 4);

            if (!_game.write(new IntPtr(movement.ToInt64() + PENDING_LAUNCH_VELOCITY), launch)) { return; }

            _jumpsUsed++;

            //Rigid for the length of the jump, or the character rises through a camera that is
            //still easing towards where it was standing.
            if (!_rigid) { _rigid = _live.setLagSpeed(LagSpeedJumping); }
        }

        /// <summary>
        /// Turns flying on and off, and holds it on while it is.
        ///
        /// Held rather than set once, because the movement mode belongs to the character and the
        /// game puts it back the moment anything lands, respawns or loads. The speed goes on every
        /// pass for the same reason.
        /// </summary>
        private void flyOnPress(IntPtr movement)
        {
            var wants = down(FLY);
            if (wants && !_flyHeld) { _flying = !_flying; }
            _flyHeld = wants;

            if (!_flying) { return; }

            _game.write(new IntPtr(movement.ToInt64() + MOVEMENT_MODE), new[] { (byte)FLYING_MODE });
            _game.writeFloat(new IntPtr(movement.ToInt64() + MAX_FLY_SPEED), FlySpeed);

            //And enough braking to stop when the keys come up, rather than drifting on for a
            //second and a half like something on ice.
            _game.writeFloat(new IntPtr(movement.ToInt64() + BRAKING_FLYING), FlySpeed * 4f);
        }

        /// <summary>Puts the character back on the floor, whatever it was doing.</summary>
        private void land()
        {
            if (!_flying) { return; }
            _flying = false;

            if (!_live.find(out _)) { return; }

            var movement = movementOf(_live.Pawn);
            if (movement == IntPtr.Zero) { return; }

            _game.write(new IntPtr(movement.ToInt64() + MOVEMENT_MODE), new[] { (byte)WALKING_MODE });
        }

        private void writeInput(IntPtr pawn, float x, float y, float z = 0f)
        {
            var bytes = new byte[12];
            BitConverter.GetBytes(x).CopyTo(bytes, 0);
            BitConverter.GetBytes(y).CopyTo(bytes, 4);
            BitConverter.GetBytes(z).CopyTo(bytes, 8);

            _game.write(new IntPtr(pawn.ToInt64() + CONTROL_INPUT_VECTOR), bytes);
        }

        /// <summary>
        /// Throws away anywhere the game has been told to walk to.
        ///
        /// Both ways at once, because a click could become movement through either and checking
        /// which would cost as much as simply refusing both.
        /// </summary>
        private void refuseDestination(IntPtr movement)
        {
            var flags = _game.read(new IntPtr(movement.ToInt64() + MOVEMENT_STATE_FLAGS), 1);
            if (flags != null && (flags[0] & (1 << BIT_HAS_REQUESTED_VELOCITY)) != 0)
            {
                _game.write(new IntPtr(movement.ToInt64() + MOVEMENT_STATE_FLAGS),
                    new[] { (byte)(flags[0] & ~(1 << BIT_HAS_REQUESTED_VELOCITY)) });

                _game.write(new IntPtr(movement.ToInt64() + REQUESTED_VELOCITY), new byte[12]);
            }
        }

        private static bool down(int key) => (GetAsyncKeyState(key) & 0x8000) != 0;

        private static IntPtr readPointer(GameProcess game, IntPtr at)
        {
            var bytes = game.read(at, 8);
            if (bytes == null) { return IntPtr.Zero; }

            var pointer = BitConverter.ToInt64(bytes, 0);
            return pointer > 0x10000 && pointer < 0x7FFFFFFFFFFF ? new IntPtr(pointer) : IntPtr.Zero;
        }

        public void Dispose()
        {
            stop();
            _thread?.Join(200);

            releaseRoot();
            restoreFacing();
            restoreLag();
            land();
        }
    }
}
