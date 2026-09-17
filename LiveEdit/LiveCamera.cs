 using System;

namespace LiveEdit
{
    /// <summary>
    /// The camera the game is actually looking through, reached by walking to it.
    ///
    /// Everything before this searched memory for numbers that looked like a camera, and it never
    /// worked: the authored values - 2450 back, 2800 seeking, minus 45 by 45 - exist in the loaded
    /// asset and in every class default as well as in the live component, all identical, and
    /// writing to the wrong one changes nothing on screen. No amount of looking harder separates
    /// copies that are the same.
    ///
    /// So this does not search at all. An SDK dumped from the game gives the engine's own layout,
    /// and with it the camera has an address rather than a fingerprint: from the world, to the
    /// game instance, to the local player, to their controller, to the pawn it possesses, to the
    /// spring arm hanging off it. Seven pointers, no guessing, and the thing at the end is the
    /// camera by construction rather than by resemblance.
    ///
    /// Offsets are from a Dumper-7 run against this exact build (4.22.3). They are written down
    /// here rather than searched for, which makes them the one thing that a game update would
    /// invalidate - so they are checked as they are followed, and a chain that stops making sense
    /// says so instead of returning a wrong answer.
    /// </summary>
    public sealed class LiveCamera
    {
        //Where the engine keeps the world, as an offset into the game's own image.
        //
        //Shared, because everything that reaches into this game starts from the same world - the
        //camera hangs off the character in it, and the spawners are actors in its level.
        internal const int GWORLD_OFFSET = 0x04795230;
        private const int GWORLD = GWORLD_OFFSET;

        private const int WORLD_GAME_INSTANCE = 0x0160;
        private const int GAME_INSTANCE_LOCAL_PLAYERS = 0x0038;
        private const int PLAYER_CONTROLLER = 0x0030;
        private const int CONTROLLER_PAWN = 0x03B0;
        private const int CHARACTER_SPRING_ARM = 0x0F50;

        //Inherited from USceneComponent, which is where the arm's angle actually lives. The pak
        //calls this the camera's rotation and so does everything else; it is a plain FRotator of
        //pitch, yaw and roll, and unlike the arm length nothing recomputes it - a write sticks
        //until something else changes it.
        private const int RELATIVE_LOCATION = 0x0164;
        private const int RELATIVE_ROTATION = 0x0170;

        //Also USceneComponent: the components hanging off this one, as a TArray of pointer,
        //count, capacity. The spring arm has exactly one child and it is the camera, which is
        //how field of view is reached - it lives on the camera rather than on the arm.
        private const int ATTACH_CHILDREN = 0x0118;
        private const int ATTACH_CHILDREN_COUNT = 0x0120;

        //Also USceneComponent: the component this one hangs from, and this one's rotation in the
        //world rather than relative to its parent. The world one is an FQuat inside the 56 bytes
        //the dumper calls Pad_188 - it is ComponentToWorld, which is not a marked up property and
        //so has no name in the SDK, sitting at the only 16 byte aligned offset that leaves room
        //for a whole transform before ComponentVelocity.
        private const int ATTACH_PARENT = 0x0108;
        private const int WORLD_ROTATION = 0x0190;

        //Not here: hiding the character in first person.
        //
        //bHiddenInGame is at 0x01CC bit 6 and the bit flips perfectly. Nothing happens. The flag
        //is only read when the component's render state is rebuilt, which SetHiddenInGame does by
        //calling MarkRenderStateDirty - and calling is the one thing this project cannot do.
        //Verified by watching LastRenderTime, which kept climbing with the bit set.
        //
        //A note rather than code, because the bit flipping is convincing and the effect is nil.

        //UCameraComponent.
        private const int FIELD_OF_VIEW = 0x0258;

        //The game's own player controller, and a byte on it that is 1 whenever a menu is up and 0
        //while playing.
        //
        //That way round, and it is worth being exact about because it was written down backwards
        //once already: the measurement said playing 0x00, menu 0x01, and the code that followed
        //asked whether it was zero. The result was a permanent menu, so the pointer was never
        //taken and the camera never turned - a bug that looks nothing like an inverted comparison
        //from the outside.
        //
        //Not in any dump - it has no name, because it is this game's rather than the engine's. It
        //was found by photographing the controller and the character twenty times while standing
        //in the world and twenty more with the inventory open, throwing away every byte that
        //moved during either, and looking at what was left.
        //
        //Four bytes survived that, and they were not equally good. One of them, on the character,
        //turned out to track the inventory and ignore the pause menu completely - it would have
        //half worked. What separated them was watching a timeline while running about and
        //swinging a weapon: the near misses mean something closer to "cannot act right now" and
        //drop during combat, and this one does not.
        private const int MENU_FLAG = 0x09B0;

        //APawn. Whether the character itself turns to face where the player is aiming, which is
        //not on the camera at all - it is what gives a following camera something to follow.
        private const int PAWN_ROTATION_FLAGS = 0x0338;
        private const int BIT_CONTROLLER_PITCH = 0;
        private const int BIT_CONTROLLER_YAW = 1;

        //On the spring arm itself.
        private const int TARGET_ARM_LENGTH = 0x0258;
        private const int SOCKET_OFFSET = 0x025C;
        private const int TARGET_OFFSET = 0x0268;
        private const int FLAGS = 0x027C;
        private const int CAMERA_LAG_SPEED = 0x0280;
        private const int CAMERA_ROTATION_LAG_SPEED = 0x0284;
        private const int SEEK_ARM_LENGTH = 0x02E0;
        private const int ARM_LAG_SPEED = 0x02E4;

        //The field that actually decides where the camera sits, and the one no dump names.
        //
        //Reflection only sees properties the game marked up, so the SDK stops at ArmLagSpeed and
        //calls the next 24 bytes padding. Writing the named fields looks like it works and then
        //unwinds over about three seconds: the component eases TargetArmLength towards a private
        //desired length every tick, so setting the target sets the thing being overwritten, and
        //setting SeekArmLength does nothing at all - it was held at a new value while the camera
        //slid back to where it started, which is what ruled it out.
        //
        //This is where SetDesiredArmLegnth puts its argument. Written once, it stays, and the
        //camera eases to it - no loop racing the game, which is the difference between a slider
        //that works and a fight with the tick.
        private const int DESIRED_ARM_LENGTH = 0x02E8;

        //The flags share one byte, each a single bit.
        private const int BIT_DO_COLLISION_TEST = 0;

        //The one that turns an isometric camera into a camera that looks where you aim.
        //
        //Overlooked for a long time because the obvious pair looked like the answer: tell the
        //camera to inherit its character's yaw, and tell the character to turn to face aim. In
        //this game the second of those is already being done by the movement component, so the
        //setting reads as broken while changing nothing, and the first only follows the character
        //rather than the aim.
        //
        //This skips both. The arm takes its rotation straight from the pawn's view rotation,
        //which is where the player is pointing.
        private const int BIT_USE_PAWN_CONTROL_ROTATION = 1;
        private const int BIT_INHERIT_PITCH = 2;
        private const int BIT_INHERIT_YAW = 3;
        private const int BIT_INHERIT_ROLL = 4;
        private const int BIT_ENABLE_CAMERA_LAG = 5;
        private const int BIT_ENABLE_ROTATION_LAG = 6;

        private readonly GameProcess _game;

        public LiveCamera(GameProcess game)
        {
            _game = game;
        }

        /// <summary>The spring arm, or zero when the chain does not lead anywhere.</summary>
        public IntPtr SpringArm { get; private set; }

        public IntPtr Pawn { get; private set; }

        /// <summary>The player's controller, which is where the game keeps what it is doing.</summary>
        public IntPtr Controller { get; private set; }

        /// <summary>The camera hanging off the arm, which owns the field of view.</summary>
        public IntPtr Camera { get; private set; }

        /// <summary>Each step of the walk, for saying where it stopped when it stops.</summary>
        public string Trail { get; private set; } = "";

        /// <summary>
        /// Walks from the world to the camera.
        ///
        /// Fails loudly and specifically. A null halfway along means something real - no pawn
        /// possessed yet, sitting in a menu rather than a level - and knowing which pointer was
        /// null is the difference between "wait until you are in a level" and "these offsets are
        /// wrong now".
        /// </summary>
        public bool find(out string problem)
        {
            problem = "";
            SpringArm = IntPtr.Zero;

            var image = _game.Process.MainModule?.BaseAddress ?? IntPtr.Zero;
            if (image == IntPtr.Zero) { problem = "Could not find the game's own module."; return false; }

            var world = follow(new IntPtr(image.ToInt64() + GWORLD));
            if (world == IntPtr.Zero) { problem = "GWorld is null - the game has no world loaded."; return false; }

            var instance = follow(new IntPtr(world.ToInt64() + WORLD_GAME_INSTANCE));
            if (instance == IntPtr.Zero) { problem = "The world has no game instance."; return false; }

            //A TArray is a pointer, a count and a capacity. The first local player is the one
            //sitting at this keyboard.
            var players = follow(new IntPtr(instance.ToInt64() + GAME_INSTANCE_LOCAL_PLAYERS));
            if (players == IntPtr.Zero) { problem = "There are no local players yet."; return false; }

            var player = follow(players);
            if (player == IntPtr.Zero) { problem = "The first local player is null."; return false; }

            Controller = follow(new IntPtr(player.ToInt64() + PLAYER_CONTROLLER));
            if (Controller == IntPtr.Zero) { problem = "The local player has no controller yet."; return false; }

            var controller = Controller;

            Pawn = follow(new IntPtr(controller.ToInt64() + CONTROLLER_PAWN));
            if (Pawn == IntPtr.Zero)
            {
                problem = "The controller is not possessing a character - load into the Camp or a mission.";
                return false;
            }

            SpringArm = follow(new IntPtr(Pawn.ToInt64() + CHARACTER_SPRING_ARM));
            if (SpringArm == IntPtr.Zero) { problem = "The character has no camera spring arm."; return false; }

            //The camera is a child of the arm rather than a named field, so it is found by looking
            //at what is attached. Not fatal when it is missing - everything except field of view
            //lives on the arm itself, and a camera panel that works apart from one slider beats
            //one that refuses to open.
            Camera = onlyChildOf(SpringArm);

            Trail = $"world 0x{world.ToInt64():X} -> pawn 0x{Pawn.ToInt64():X} -> arm 0x{SpringArm.ToInt64():X}";
            return true;
        }

        /// <summary>
        /// Whether a menu is open, straight from the game rather than guessed at.
        ///
        /// Null when it cannot be read or reads as something other than a plain yes or no, which
        /// is the caller's cue to fall back on watching keys rather than to believe a wrong
        /// answer. A byte that has stopped being a bool is a byte that has moved.
        /// </summary>
        public bool? MenuOpen
        {
            get
            {
                if (Controller == IntPtr.Zero) { return null; }

                var bytes = _game.read(new IntPtr(Controller.ToInt64() + MENU_FLAG), 1);
                if (bytes == null || bytes[0] > 1) { return null; }

                return bytes[0] == 1;
            }
        }

        public float? ArmLength => readFloat(TARGET_ARM_LENGTH);
        public float? SeekLength => readFloat(SEEK_ARM_LENGTH);
        public float? LagSpeed => readFloat(CAMERA_LAG_SPEED);

        /// <summary>Sideways from the character, at the camera end of the arm.</summary>
        public float? SocketSide => readFloat(SOCKET_OFFSET + 4);

        public float? SocketHeight => readFloat(SOCKET_OFFSET + 8);

        /// <summary>How far above the character the arm orbits and looks.</summary>
        public float? TargetOffsetHeight => readFloat(TARGET_OFFSET + 8);
        public float? RotationLagSpeed => readFloat(CAMERA_ROTATION_LAG_SPEED);
        public float? ArmLag => readFloat(ARM_LAG_SPEED);

        /// <summary>
        /// Whether the camera points where the player is aiming.
        ///
        /// Worth knowing before turning it on: the pitch comes from the aim too, and this game's
        /// aim pitch sits at zero and then jumps tens of degrees, so the view can swing in a way
        /// an isometric camera never does.
        /// </summary>
        public bool? FollowsAim => readFlag(BIT_USE_PAWN_CONTROL_ROTATION);

        public bool setFollowsAim(bool on) => writeFlag(BIT_USE_PAWN_CONTROL_ROTATION, on);

        public bool? InheritYaw => readFlag(BIT_INHERIT_YAW);
        public bool? InheritPitch => readFlag(BIT_INHERIT_PITCH);
        public bool? CollisionTest => readFlag(BIT_DO_COLLISION_TEST);

        /// <summary>What the camera is easing towards, which is the one worth writing.</summary>
        public float? DesiredLength => readFloat(DESIRED_ARM_LENGTH);

        /// <summary>
        /// How far back the camera sits.
        ///
        /// Writes the desired length and lets the component ease to it, rather than writing the
        /// arm length directly - the latter is recomputed from this every tick, so it reverts.
        /// The current length is left alone deliberately: easing into place is how the game's own
        /// zoom volumes behave, and snapping would look wrong beside them.
        /// </summary>
        public bool setArmLength(float units)
        {
            return writeFloat(DESIRED_ARM_LENGTH, units);
        }

        /// <summary>
        /// The same, arriving immediately.
        ///
        /// For a slider being dragged, where easing towards each value in turn reads as lag
        /// rather than as smoothing.
        /// </summary>
        public bool snapArmLength(float units)
        {
            return writeFloat(DESIRED_ARM_LENGTH, units)
                && writeFloat(TARGET_ARM_LENGTH, units);
        }

        /// <summary>How far the camera is tipped down towards the character, in degrees.</summary>
        public float? Pitch => readFloat(RELATIVE_ROTATION);

        /// <summary>Which way round the character the camera sits, in degrees.</summary>
        public float? Yaw => readFloat(RELATIVE_ROTATION + 4);

        public float? Roll => readFloat(RELATIVE_ROTATION + 8);

        public bool setPitch(float degrees) => writeFloat(RELATIVE_ROTATION, degrees);
        public bool setYaw(float degrees) => writeFloat(RELATIVE_ROTATION + 4, degrees);
        public bool setRoll(float degrees) => writeFloat(RELATIVE_ROTATION + 8, degrees);

        /// <summary>
        /// The whole angle at once.
        ///
        /// Written as one twelve byte value rather than three, so a camera being swung around
        /// cannot be caught mid update with a new pitch and an old yaw.
        ///
        /// Then written a second time, into the world transform, because the arm reads its angle
        /// from two different places depending on how it is set up:
        ///
        ///     FRotator DesiredRot = GetComponentRotation();            // the world transform
        ///     if (!bInheritYaw) DesiredRot.Yaw = RelativeRotation.Yaw;
        ///
        /// With the inherit flags off it reads the relative rotation, so writing that is enough
        /// and always was. Turn "camera follows player" on and it reads the world transform
        /// instead - which setting a field in memory does not recompute, because the engine only
        /// refreshes it inside SetRelativeRotation. The value changed and nothing moved.
        ///
        /// So the world rotation is composed here and written too. Not a permanent override: the
        /// engine recomputes it from the relative rotation whenever the character moves, and
        /// recomputes it to the same answer, so this closes the gap until the game next does it
        /// properly rather than fighting it.
        /// </summary>
        public bool setRotation(float pitch, float yaw, float roll = 0f)
        {
            if (SpringArm == IntPtr.Zero) { return false; }

            var bytes = new byte[12];
            BitConverter.GetBytes(pitch).CopyTo(bytes, 0);
            BitConverter.GetBytes(yaw).CopyTo(bytes, 4);
            BitConverter.GetBytes(roll).CopyTo(bytes, 8);

            if (!_game.write(new IntPtr(SpringArm.ToInt64() + RELATIVE_ROTATION), bytes)) { return false; }

            refreshWorldRotation(pitch, yaw, roll);
            return true;
        }

        /// <summary>
        /// Recomputes what the arm's rotation is in the world, the way the engine would have.
        ///
        /// The world rotation is the parent's with this one's applied first, so the parent has to
        /// be read rather than assumed - a character standing still has an identity rotation and
        /// the two are then the same, which would make a wrong assumption look correct right up
        /// until somebody turned around.
        ///
        /// Best effort by design. Failing to reach the parent means the relative rotation has
        /// still been written, which is the whole of the behaviour with the inherit flags off.
        /// </summary>
        private void refreshWorldRotation(float pitch, float yaw, float roll)
        {
            //This offset is deduced rather than dumped. ComponentToWorld is not a marked up
            //property, so it has no name in the SDK - it is the only place a 48 byte transform
            //fits, 16 byte aligned, inside the pad that ends where ComponentVelocity begins.
            //
            //Deduced is not known, and this writes sixteen bytes into a live game. So both ends
            //are checked first, and a rotation is unusually good at proving itself: four floats
            //that happen to be a unit quaternion are not what arbitrary other state looks like.
            //Anything that fails the test means the offset is wrong - a different build, most
            //likely - and nothing is written at all.
            if (!looksLikeRotation(SpringArm, out _)) { return; }

            var parent = follow(new IntPtr(SpringArm.ToInt64() + ATTACH_PARENT));
            if (parent == IntPtr.Zero) { return; }

            if (!looksLikeRotation(parent, out var p)) { return; }

            var world = multiply(p, quaternionOf(pitch, yaw, roll));

            var bytes = new byte[16];
            for (int i = 0; i < 4; i++) { BitConverter.GetBytes(world[i]).CopyTo(bytes, i * 4); }
            _game.write(new IntPtr(SpringArm.ToInt64() + WORLD_ROTATION), bytes);
        }

        /// <summary>
        /// Whether a component's world rotation really is where it is believed to be.
        ///
        /// A unit quaternion is a strong thing to test for. Four consecutive floats whose squares
        /// sum to one is not a coincidence that stray pointers, counts or flags produce, so this
        /// separates "the offset is right" from "the offset moved in an update" without needing
        /// to know anything about the particular build.
        /// </summary>
        private bool looksLikeRotation(IntPtr component, out float[] rotation)
        {
            rotation = new float[4];

            var bytes = _game.read(new IntPtr(component.ToInt64() + WORLD_ROTATION), 16);
            if (bytes == null) { return false; }

            var length = 0.0;
            for (int i = 0; i < 4; i++)
            {
                rotation[i] = BitConverter.ToSingle(bytes, i * 4);
                if (float.IsNaN(rotation[i]) || float.IsInfinity(rotation[i])) { return false; }
                length += (double)rotation[i] * rotation[i];
            }

            length = Math.Sqrt(length);
            return length > 0.99 && length < 1.01;
        }

        /// <summary>A rotator as the quaternion the engine stores, following FRotator::Quaternion.</summary>
        private static float[] quaternionOf(float pitch, float yaw, float roll)
        {
            const double HALF_A_DEGREE = Math.PI / 360.0;

            var sp = Math.Sin(pitch * HALF_A_DEGREE); var cp = Math.Cos(pitch * HALF_A_DEGREE);
            var sy = Math.Sin(yaw * HALF_A_DEGREE);   var cy = Math.Cos(yaw * HALF_A_DEGREE);
            var sr = Math.Sin(roll * HALF_A_DEGREE);  var cr = Math.Cos(roll * HALF_A_DEGREE);

            return new[] {
                (float)(cr * sp * sy - sr * cp * cy),
                (float)(-cr * sp * cy - sr * cp * sy),
                (float)(cr * cp * sy - sr * sp * cy),
                (float)(cr * cp * cy + sr * sp * sy),
            };
        }

        /// <summary>Two rotations as one: b happens, then a.</summary>
        private static float[] multiply(float[] a, float[] b)
        {
            return new[] {
                a[3] * b[0] + a[0] * b[3] + a[1] * b[2] - a[2] * b[1],
                a[3] * b[1] - a[0] * b[2] + a[1] * b[3] + a[2] * b[0],
                a[3] * b[2] + a[0] * b[1] - a[1] * b[0] + a[2] * b[3],
                a[3] * b[3] - a[0] * b[0] - a[1] * b[1] - a[2] * b[2],
            };
        }

        /// <summary>
        /// Whether the character turns to face where the player is aiming.
        ///
        /// On the pawn rather than on the camera, which is how it came to be missed: everything
        /// else here lives on the spring arm, and this one sits two pointers away on the
        /// character. It was offered in the panel and then never written.
        ///
        /// It is also the half that makes "camera follows player" mean anything. A camera told to
        /// inherit its character's yaw inherits nothing at all if the character never turns - and
        /// in a game played from above, only the mesh is swung round to face the cursor while the
        /// actor underneath holds still.
        /// </summary>
        public bool? ControllerYaw => pawnFlag(BIT_CONTROLLER_YAW);

        public bool setControllerYaw(bool on) => setPawnFlag(BIT_CONTROLLER_YAW, on);

        public bool? ControllerPitch => pawnFlag(BIT_CONTROLLER_PITCH);

        public bool setControllerPitch(bool on) => setPawnFlag(BIT_CONTROLLER_PITCH, on);

        private bool? pawnFlag(int bit)
        {
            if (Pawn == IntPtr.Zero) { return null; }

            var bytes = _game.read(new IntPtr(Pawn.ToInt64() + PAWN_ROTATION_FLAGS), 1);
            return bytes == null ? (bool?)null : (bytes[0] & (1 << bit)) != 0;
        }

        private bool setPawnFlag(int bit, bool on)
        {
            if (Pawn == IntPtr.Zero) { return false; }

            var at = new IntPtr(Pawn.ToInt64() + PAWN_ROTATION_FLAGS);
            var bytes = _game.read(at, 1);
            if (bytes == null) { return false; }

            bytes[0] = (byte)(on ? bytes[0] | (1 << bit) : bytes[0] & ~(1 << bit));
            return _game.write(at, bytes);
        }

        /// <summary>How wide the view is, in degrees. Stock is 55.</summary>
        public float? FieldOfView
        {
            get
            {
                if (Camera == IntPtr.Zero) { return null; }
                return _game.readFloat(new IntPtr(Camera.ToInt64() + FIELD_OF_VIEW));
            }
        }

        public bool setFieldOfView(float degrees)
        {
            if (Camera == IntPtr.Zero) { return false; }
            return _game.writeFloat(new IntPtr(Camera.ToInt64() + FIELD_OF_VIEW), degrees);
        }

        /// <summary>
        /// The single component attached to another, or nothing.
        ///
        /// Deliberately refuses to guess when there is more than one: picking the first of several
        /// would be the kind of assumption that works until it quietly does not.
        /// </summary>
        private IntPtr onlyChildOf(IntPtr component)
        {
            var count = _game.read(new IntPtr(component.ToInt64() + ATTACH_CHILDREN_COUNT), 4);
            if (count == null || BitConverter.ToInt32(count, 0) != 1) { return IntPtr.Zero; }

            var children = follow(new IntPtr(component.ToInt64() + ATTACH_CHILDREN));
            return children == IntPtr.Zero ? IntPtr.Zero : follow(children);
        }

        public bool setLagSpeed(float value) => writeFloat(CAMERA_LAG_SPEED, value);
        public bool setRotationLagSpeed(float value) => writeFloat(CAMERA_ROTATION_LAG_SPEED, value);

        public bool setInheritYaw(bool on) => writeFlag(BIT_INHERIT_YAW, on);
        public bool setInheritPitch(bool on) => writeFlag(BIT_INHERIT_PITCH, on);
        public bool setCollisionTest(bool on) => writeFlag(BIT_DO_COLLISION_TEST, on);

        /// <summary>
        /// What the camera orbits and looks at, relative to where the arm hangs.
        ///
        /// The arm hangs ninety units below the character's origin, which is about its feet, and
        /// that is invisible from twenty five metres up. Pull the camera in to seven and it is
        /// the whole picture: the view orbits the character's knees, and with the socket offset
        /// dragging the camera end eighty lower still, it ends up inside the legs.
        ///
        /// Raising this lifts the pivot without moving the character, so the camera swings around
        /// the shoulders and looks at what is in front of them.
        /// </summary>
        public bool setTargetOffset(float x, float y, float z)
        {
            return writeFloat(TARGET_OFFSET, x)
                && writeFloat(TARGET_OFFSET + 4, y)
                && writeFloat(TARGET_OFFSET + 8, z);
        }

        /// <summary>Where the arm hangs from, relative to the character.</summary>
        public bool setSocketOffset(float x, float y, float z)
        {
            return writeFloat(SOCKET_OFFSET, x)
                && writeFloat(SOCKET_OFFSET + 4, y)
                && writeFloat(SOCKET_OFFSET + 8, z);
        }

        private IntPtr follow(IntPtr at)
        {
            var bytes = _game.read(at, 8);
            if (bytes == null) { return IntPtr.Zero; }

            var pointer = BitConverter.ToInt64(bytes, 0);
            //A user mode pointer. Anything else is a null, a tag, or a field this is not.
            return pointer > 0x10000 && pointer < 0x7FFFFFFFFFFF ? new IntPtr(pointer) : IntPtr.Zero;
        }

        private float? readFloat(int offset)
        {
            if (SpringArm == IntPtr.Zero) { return null; }
            return _game.readFloat(new IntPtr(SpringArm.ToInt64() + offset));
        }

        private bool writeFloat(int offset, float value)
        {
            if (SpringArm == IntPtr.Zero) { return false; }
            return _game.writeFloat(new IntPtr(SpringArm.ToInt64() + offset), value);
        }

        /// <summary>
        /// One bit out of the byte the spring arm keeps its switches in.
        ///
        /// Six of them share a single byte, so a flag cannot be written without reading its
        /// neighbours first - writing a whole byte would silently clear five other settings.
        /// </summary>
        private bool? readFlag(int bit)
        {
            if (SpringArm == IntPtr.Zero) { return null; }

            var bytes = _game.read(new IntPtr(SpringArm.ToInt64() + FLAGS), 1);
            return bytes == null ? (bool?)null : (bytes[0] & (1 << bit)) != 0;
        }

        private bool writeFlag(int bit, bool on)
        {
            if (SpringArm == IntPtr.Zero) { return false; }

            var at = new IntPtr(SpringArm.ToInt64() + FLAGS);
            var bytes = _game.read(at, 1);
            if (bytes == null) { return false; }

            bytes[0] = (byte)(on ? bytes[0] | (1 << bit) : bytes[0] & ~(1 << bit));
            return _game.write(at, bytes);
        }
    }
}
