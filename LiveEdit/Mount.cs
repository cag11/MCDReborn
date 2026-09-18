using System;
using System.Threading;

namespace LiveEdit
{
    /// <summary>
    /// Riding something, in a game where everything is on foot.
    ///
    /// The design this came from wanted a custom actor spawned from a pak, the player's root bone
    /// attached to a socket on it, and the walk animation swapped for a riding pose. None of that is
    /// reachable from outside a process. Spawning is a function call, attaching is a function call,
    /// and picking an animation is a function call. This project does not inject, so it cannot call.
    ///
    /// What it can do is write. So nothing is spawned and nothing is attached: a creature the level
    /// already placed is taken over, parked underneath the character, and moved every frame to stay
    /// there. From the outside that is indistinguishable from a mount, because being carried and
    /// being followed exactly look the same.
    ///
    /// The one thing that makes it hold still is stopping the creature walking first. Its movement
    /// component will overwrite any position written underneath it - the same fight that made
    /// holding the player's own mesh four times worse - but a walk speed of zero means there is no
    /// fight at all. Measured: the mount stayed within 0.0 units of where it was put over six
    /// seconds.
    ///
    /// The rider's legs are frozen rather than posed. bPauseAnims stops the skeleton updating, which
    /// is a flag the animation update reads rather than one the renderer caches, so unlike almost
    /// every other visual setting tried here it takes effect immediately.
    /// </summary>
    public sealed class Mount : IDisposable
    {
        //Fast enough to be worth mounting for, and written to the character's own walk speed, which
        //is the number the game accelerates towards rather than a multiplier applied afterwards.
        //How often the mount is put back under the rider.
        //
        //This is the whole of why a mount shook while moving. The mount does not follow anybody -
        //nothing in the engine moves it, its walk speed is zero, and the position the renderer
        //draws is exactly the last one written here. So between two writes it is perfectly still
        //while the rider carries on, and every write is a step: at four thousand units a second,
        //eight milliseconds is a thirty two unit jump, about a third of a character's height,
        //arriving sixty times a second. Standing still it is invisible, which is why this looked
        //like a flying problem rather than a clock problem.
        //
        //Two milliseconds puts the step under ten units at the fastest this can fly, and the same
        //number was already arrived at for walking - see the note on EVERY_MS in KeyboardMove,
        //which is the same mistake found from the other end.
        private const int EVERY_MS = 2;

        //And asking Windows for a clock that can keep it. Without this every sleep below about
        //fifteen milliseconds is fifteen milliseconds, so the constant above would be a wish.
        //KeyboardMove raises it too and only while walking, which would have left the mount's
        //cadence depending on whether somebody had a key down.
        [System.Runtime.InteropServices.DllImport("winmm.dll")]
        private static extern uint timeBeginPeriod(uint milliseconds);
        [System.Runtime.InteropServices.DllImport("winmm.dll")]
        private static extern uint timeEndPeriod(uint milliseconds);

        private const uint TIMER_RESOLUTION_MS = 1;

        //Everything that is not "put the mount under the rider" happens at the old rate. Resolving
        //the pawn is a seven read walk and looking for something to ride walks the level, and
        //neither is worth doing five hundred times a second.
        private const int HOUSEKEEPING_EVERY = 4;

        private IntPtr _pawn;

        //AActor and its root, and the two places a position has to be written for it to hold. The
        //relative one is what the engine recomputes from, the world one is what is drawn this frame;
        //writing only the second gets undone the moment anything touches the actor.
        private const int ROOT_COMPONENT = 0x0158;
        private const int RELATIVE_LOCATION = 0x0164;
        private const int RELATIVE_ROTATION = 0x0170;
        private const int WORLD_ROTATION = 0x0190;
        private const int WORLD_LOCATION = 0x01A0;

        //ACharacter.
        private const int CHARACTER_MOVEMENT = 0x0398;
        private const int CHARACTER_MESH = 0x0390;
        private const int MAX_WALK_SPEED = 0x01DC;

        //UCapsuleComponent, for standing the rider on top of whatever is underneath them rather
        //than inside it.
        private const int CAPSULE_HALF_HEIGHT = 0x0590;

        //Twice the half height would sit the rider exactly on top of the capsule. The drawn creature
        //is smaller than the capsule around it, so that leaves a gap; this sits them into it.
        private const float SIT_INTO = 1.8f;

        //Looking again is a walk of the level's actor list, so it goes by a count of housekeeping
        //passes rather than by a clock: at eight milliseconds each, forty of them is about a third
        //of a second.
        private const int LOOKS_EVERY = 40;
        private int _untilNextLook;

        //Close enough that whatever is picked up is something standing next to you.
        private const float WITHIN_REACH = 1500f;

        //UCharacterMovementComponent's movement mode, and the value that turns gravity off.
        //
        //Standing on a mount needs the rider held above the floor, and holding a walking character
        //up is a fight with its own falling that gets lost sixty times a second. Flying is not a
        //fight: the engine simply stops pulling down, so the rider stays at whatever height it is
        //put at and the mount can be parked underneath.
        //
        //Which does mean the rider hovers over holes rather than falling into them. That is the
        //price of being on top of something instead of inside it.
        private const int MOVEMENT_MODE = 0x01B0;
        private const byte WALKING = 1;
        private const byte FLYING = 5;

        //USkeletalMeshComponent. Freezing the skeleton is the closest thing to a riding pose that
        //can be had without choosing an animation, which would be a call.
        private const int ANIM_FLAGS = 0x07E3;
        private const int BIT_PAUSE_ANIMS = 6;

        private readonly GameProcess _game;
        private readonly LiveCamera _live;

        private Thread? _thread;
        private volatile bool _running;

        private IntPtr _mount;
        private float _mountSpeedWas;
        private bool _riderFrozen;

        //What the mount was, so it can be told apart from whatever now occupies its address.
        //
        //A level change frees every actor in the level, and an address that pointed at a creature a
        //moment ago points at nothing, or at something else entirely. Writing a position into that
        //is how entering a mission while riding took the game down.
        //
        //Two checks rather than one. The level pointer changing means the whole level went, which
        //is the common case and cheap to notice. The vtable is the backstop for a mount that dies
        //or despawns on its own while the level stays up - every instance of a class shares one, so
        //an address that no longer holds the same class is not the mount any more.
        private IntPtr _level;
        private IntPtr _vtable;
        private bool _lifted;

        public Mount(GameProcess game)
        {
            _game = game;
            _live = new LiveCamera(game);
        }

        /// <summary>How fast the character travels while mounted, in the game's own units.</summary>
        public float Speed { get; set; } = 2400f;

        /// <summary>Whether the loop is running, with or without something underneath.</summary>
        public bool Riding => _running;

        /// <summary>Whether something is actually being carried along.</summary>
        public bool HasMount => _mount != IntPtr.Zero;

        /// <summary>How tall the thing being ridden is, for whoever has to frame it.</summary>
        public float MountHeight { get; private set; }

        public event Action? stopped;

        /// <summary>Something has been got onto, with how tall it is.</summary>
        public event Action<float>? mounted;

        /// <summary>
        /// Takes over the nearest creature and parks it under the character.
        ///
        /// The nearest rather than a chosen one, because without name resolution there is no way to
        /// ask for a llama specifically - this build's GNames is null, so everything here is
        /// identified by shape. Whatever is closest and can walk becomes the mount.
        /// </summary>
        public bool start(IntPtr chosen, out string problem)
        {
            problem = "";
            stop();

            if (!_live.find(out problem)) { return false; }

            var pawn = _live.Pawn;
            var mount = chosen != IntPtr.Zero ? chosen : nearestCreature(pawn);

            //Riding nothing is allowed, and is the half of this that actually changes how a level
            //plays. The creature underneath is decoration; refusing to start without one meant the
            //whole feature did nothing in exactly the places it would have helped.
            if (mount != IntPtr.Zero) { takeOver(mount); }

            //Said properly, because "speed only" was hiding a real answer: the thing you picked
            //cannot be ridden, and which thing that is matters.
            if (_mount == IntPtr.Zero)
            {
                problem = mount == IntPtr.Zero
                    ? "Nothing nearby to ride yet - still looking, and this is speed only meanwhile."
                    : "That one cannot be carried - only creatures can, for now. Speed only.";
            }

            _level = levelOf();
            _running = true;
            _thread = new Thread(loop) { IsBackground = true, Name = "mount" };
            _thread.Start();
            return true;
        }

        /// <summary>
        /// Takes one particular creature as the mount, and puts the rider on top of it.
        ///
        /// Its own method because it happens twice now: once when the key is pressed, and again
        /// whenever the loop finds something while riding with nothing underneath. Pressing the
        /// key used to be the only chance - miss it and the answer was speed only until the key
        /// was pressed again, which is exactly the summoning case, where the creature arrives a
        /// second *after* anybody would have pressed it.
        /// </summary>
        private bool takeOver(IntPtr mount)
        {
            {
                //Only things that can walk, and that is a safety check as much as a filter.
                //
                //It was removed once, because a totem and a barrel have no movement component and
                //were being silently dropped. That was a real bug and this is the wrong fix for it:
                //requiring a movement component is also the only thing guaranteeing this is a
                //character, with a layout where 0x0158 is a root component and 0x01A0 is a
                //position. Without it, a position gets written into whatever happens to live at
                //those offsets on an arbitrary actor.
                //
                //Measured, with the check removed: a prop driven under the player ended up 23,367
                //units from where it was put, and the game lost its connection shortly after. That
                //is not a mount that did not work, that is a write into somebody else's memory.
                //
                //Riding props needs the actor's layout established first - that its root really is
                //a scene component, and that its transform is one the engine reads back. Until then
                //this only rides things it understands.
                var movement = readPointer(new IntPtr(mount.ToInt64() + CHARACTER_MOVEMENT));
                if (movement != IntPtr.Zero)
                {
                    _mountSpeedWas = _game.readFloat(new IntPtr(movement.ToInt64() + MAX_WALK_SPEED)) ?? 0f;
                    _game.writeFloat(new IntPtr(movement.ToInt64() + MAX_WALK_SPEED), 0f);

                    _mount = mount;
                    _vtable = readPointer(mount);

                    var theirRoot = readPointer(new IntPtr(mount.ToInt64() + ROOT_COMPONENT));
                    MountHeight = theirRoot == IntPtr.Zero
                        ? 100f
                        : sane(_game.readFloat(new IntPtr(theirRoot.ToInt64() + CAPSULE_HALF_HEIGHT)), 50f) * 2f;
                }
            }

            if (_mount == IntPtr.Zero) { return false; }

            //Lifted once, onto whatever is about to be parked underneath. Gravity goes off with it,
            //or the lift lasts exactly one frame.
            lift(_live.Pawn);
            mounted?.Invoke(MountHeight);
            return true;
        }

        /// <summary>
        /// Stops riding, from wherever.
        ///
        /// The thread is only waited on when this is somebody else asking. The loop itself calls
        /// this when it notices the mount has gone, and a thread cannot wait for itself to finish -
        /// which is the sort of thing that turns a crash fix into a hang.
        /// </summary>
        public void stop()
        {
            _running = false;

            var thread = _thread;
            if (thread != null && thread != Thread.CurrentThread) { thread.Join(200); }
            _thread = null;

            release();
        }

        private void release()
        {
            //Only given its speed back if it is still the thing we took it from. If the level has
            //gone, so has the creature, and the tidying up would be the same bad write as the loop.
            if (_mount != IntPtr.Zero && stillThere() && _mountSpeedWas > 0f)
            {
                var movement = readPointer(new IntPtr(_mount.ToInt64() + CHARACTER_MOVEMENT));
                if (movement != IntPtr.Zero)
                {
                    _game.writeFloat(new IntPtr(movement.ToInt64() + MAX_WALK_SPEED), _mountSpeedWas);
                }
            }

            _mountSpeedWas = 0f;

            _mount = IntPtr.Zero;
            MountHeight = 0f;
            _level = IntPtr.Zero;
            _vtable = IntPtr.Zero;

            drop();
            freezeRider(false);
            stopped?.Invoke();
        }

        public void Dispose() => stop();

        /// <summary>Something in the level that could be ridden.</summary>
        public sealed class Candidate
        {
            public IntPtr Actor;
            public float Away;
            public float Tall;
            public float Walks;

            /// <summary>
            /// Whether this is something that can actually be carried.
            ///
            /// Which is not the same as being nearby, and the difference is the whole of why this
            /// used to need the key pressing over and over: the list holds everything with a
            /// position, so the nearest thing is usually a crate, and the nearest thing was what
            /// got picked. Riding needs a character movement component - see the note in start -
            /// so whether there is one is recorded while the actor is already being read.
            /// </summary>
            public bool Rideable;

            /// <summary>How many of this kind are in the level.</summary>
            public int HowMany;

            /// <summary>
            /// What it can be called, which is not its name.
            ///
            /// This build's GNames is null, so nothing here has a name to show. What it has is a
            /// shape: how far off it is, how tall, and how fast it walks - which between them are
            /// enough to tell a cow from a chicken from whatever is guarding the corridor.
            /// </summary>
            public override string ToString()
            {
                var many = HowMany > 1 ? string.Format("{0,3} of them", HowMany) : "    just one";

                return Walks > 0f
                    ? string.Format("{0}   {1,3:0} tall   walks {2,4:0}   nearest {3,5:0} away",
                        many, Tall, Walks, Away)
                    : string.Format("{0}   {1,3:0} tall   stays put    nearest {2,5:0} away",
                        many, Tall, Away);
            }
        }

        /// <summary>
        /// Everything nearby worth sitting on, nearest first, and a line saying what was looked at.
        ///
        /// Widened after the first version came back empty in the middle of a herd of mooshrooms.
        /// It only accepted actors with a character movement component, which is what an enemy has
        /// and what a wandering animal in this game apparently does not. Anything with a position
        /// counts now, whether it can walk or not - and the ones that cannot are better mounts, not
        /// worse, because nothing is trying to walk them anywhere.
        ///
        /// The tally comes back too. An empty list that cannot say whether it scanned two thousand
        /// actors or none is not a result, it is a shrug, and this one has already wasted a build.
        /// </summary>
        public System.Collections.Generic.List<Candidate> nearby(out string tell, int most = 40)
        {
            var found = new System.Collections.Generic.List<Candidate>();

            tell = "";
            if (!_live.find(out var why)) { tell = why; return found; }

            var pawn = _live.Pawn;
            var level = levelOf();
            if (pawn == IntPtr.Zero) { tell = "No character."; return found; }
            if (level == IntPtr.Zero) { tell = "No level loaded."; return found; }

            var actors = readPointer(new IntPtr(level.ToInt64() + 0x0098));
            var countBytes = _game.read(new IntPtr(level.ToInt64() + 0x00A0), 4);
            if (actors == IntPtr.Zero || countBytes == null) { tell = "The level has no actor list."; return found; }

            var count = BitConverter.ToInt32(countBytes, 0);
            if (count < 1 || count > 40000) { tell = "The actor count read as " + count + "."; return found; }

            var root = readPointer(new IntPtr(pawn.ToInt64() + ROOT_COMPONENT));
            var here = root == IntPtr.Zero ? null : _game.read(new IntPtr(root.ToInt64() + WORLD_LOCATION), 12);
            if (here == null) { tell = "Could not read where you are standing."; return found; }

            var hx = BitConverter.ToSingle(here, 0);
            var hy = BitConverter.ToSingle(here, 4);
            var hz = BitConverter.ToSingle(here, 8);

            var placed = 0;
            var walkers = 0;

            for (var i = 0; i < count; i++)
            {
                var actor = readPointer(new IntPtr(actors.ToInt64() + i * 8));
                if (actor == IntPtr.Zero || actor == pawn) { continue; }

                var theirRoot = readPointer(new IntPtr(actor.ToInt64() + ROOT_COMPONENT));
                if (theirRoot == IntPtr.Zero) { continue; }

                var there = _game.read(new IntPtr(theirRoot.ToInt64() + WORLD_LOCATION), 12);
                if (there == null) { continue; }

                var x = BitConverter.ToSingle(there, 0);
                var y = BitConverter.ToSingle(there, 4);
                var z = BitConverter.ToSingle(there, 8);

                //A position rather than whatever happened to be in those bytes.
                if (float.IsNaN(x) || float.IsNaN(y) || float.IsNaN(z)) { continue; }
                if (Math.Abs(x) > 1e7 || Math.Abs(y) > 1e7 || Math.Abs(z) > 1e7) { continue; }

                placed++;

                var dx = x - hx;
                var dy = y - hy;
                var dz = z - hz;
                var away = (float)Math.Sqrt((double)dx * dx + (double)dy * dy + (double)dz * dz);

                //Close enough to be something you can see and point at.
                if (away > 4000f) { continue; }

                var speed = 0f;
                var movement = readPointer(new IntPtr(actor.ToInt64() + CHARACTER_MOVEMENT));
                if (movement != IntPtr.Zero)
                {
                    var walks = _game.readFloat(new IntPtr(movement.ToInt64() + MAX_WALK_SPEED));
                    if (walks is float v && v > 0f && v < 5000f) { speed = v; walkers++; }
                }

                found.Add(new Candidate {
                    Actor = actor,
                    Away = away,
                    Tall = sane(_game.readFloat(new IntPtr(theirRoot.ToInt64() + CAPSULE_HALF_HEIGHT)), 50f) * 2f,
                    Walks = speed,
                    Rideable = movement != IntPtr.Zero,
                });
            }

            //One row per kind, not per individual.
            //
            //A herd of mooshrooms was forty rows that all read the same, which is a list you cannot
            //choose from. Every instance of a class shares a vtable, so grouping on it gives one row
            //per kind of thing - and the nearest of each kind is the one worth offering, since it is
            //the one you can see.
            //
            //Names would be better and are not available: this build's GNames is null, so a kind can
            //be counted and measured but not called anything.
            var kinds = new System.Collections.Generic.Dictionary<long, Candidate>();
            foreach (var one in found)
            {
                var kind = readPointer(one.Actor).ToInt64();
                if (kinds.TryGetValue(kind, out var already))
                {
                    already.HowMany++;
                    if (one.Away < already.Away)
                    {
                        already.Away = one.Away;
                        already.Actor = one.Actor;
                        already.Rideable = one.Rideable;
                    }
                    continue;
                }

                one.HowMany = 1;
                kinds[kind] = one;
            }

            found = new System.Collections.Generic.List<Candidate>(kinds.Values);
            found.Sort((a, b) => a.Away.CompareTo(b.Away));
            if (found.Count > most) { found.RemoveRange(most, found.Count - most); }

            tell = string.Format("{0} actors, {1} with a position, {2} that walk, {3} kinds within range.",
                count, placed, walkers, found.Count);
            return found;
        }

        /// <summary>
        /// Whether the mount is still the creature it was, rather than freed memory.
        /// </summary>
        private bool stillThere()
        {
            if (_mount == IntPtr.Zero) { return false; }
            if (levelOf() != _level) { return false; }

            return readPointer(_mount) == _vtable;
        }

        private IntPtr levelOf()
        {
            var world = _live.World;
            return world == IntPtr.Zero
                ? IntPtr.Zero
                : readPointer(new IntPtr(world.ToInt64() + 0x0030));
        }

        private void loop()
        {
            timeBeginPeriod(TIMER_RESOLUTION_MS);

            try
            {
                var untilHousekeeping = 0;

                while (_running)
                {
                    Thread.Sleep(EVERY_MS);

                    if (--untilHousekeeping <= 0)
                    {
                        untilHousekeeping = HOUSEKEEPING_EVERY;
                        housekeeping();
                    }

                    if (_pawn == IntPtr.Zero) { continue; }
                    carry(_pawn);
                }
            }
            finally
            {
                timeEndPeriod(TIMER_RESOLUTION_MS);
            }

            _running = false;
            release();
        }

        /// <summary>
        /// Everything that is not worth doing at the rate the mount is moved at.
        ///
        /// The pawn is re-resolved here rather than held indefinitely. A level change frees all of
        /// this and a pointer kept across one is a write into somebody else's memory - so it is
        /// held for one housekeeping interval and no longer, which is the same exposure the loop
        /// had when it resolved every pass at that rate.
        /// </summary>
        private void housekeeping()
        {
            if (!_live.find(out _)) { _pawn = IntPtr.Zero; return; }

            _pawn = _live.Pawn;
            if (_pawn == IntPtr.Zero) { return; }

            //Before anything is written to it. Losing the mount is normal - a mission starts, the
            //level goes - and the answer is to let it go and keep the speed, rather than to write
            //into whatever owns that memory now.
            if (_mount != IntPtr.Zero && !stillThere()) { _mount = IntPtr.Zero; }

            //And with nothing underneath, keep looking for something.
            //
            //This is what riding a summoned creature needs. Enchanted Grass puts a sheep beside you
            //a moment *after* anybody would have pressed the key, and that moment used to be the
            //only chance there was - miss it and the answer stayed "speed only" however long you
            //stood next to the sheep. Now the key says start riding and the loop finds what to
            //ride, whenever it turns up.
            //
            //Occasionally rather than every pass: a look walks the whole actor list, which is
            //thousands of reads, and doing that often would cost more than it is worth for
            //something that changes on a human timescale.
            if (_mount == IntPtr.Zero)
            {
                if (--_untilNextLook <= 0)
                {
                    _untilNextLook = LOOKS_EVERY;
                    //Beside you rather than anywhere in range, because this one is not asked for -
                    //it happens on its own, and picking up a creature across the room that somebody
                    //never pointed at would be a surprise rather than a mount.
                    var found = nearestCreature(_pawn, WITHIN_REACH);
                    if (found != IntPtr.Zero) { takeOver(found); }
                }
            }

            //The legs stop while there is something underneath and start again when there is not,
            //rather than staying stopped because the mount went. A rider left paused in mid-stride
            //after the creature they were on despawned looks like a crash.
            freezeRider(_mount != IntPtr.Zero);
        }

        /// <summary>
        /// Puts the mount under the character, facing the same way, every frame.
        /// </summary>
        private void carry(IntPtr pawn)
        {
            //The speed first, so it happens with or without something to sit on.
            var movement = readPointer(new IntPtr(pawn.ToInt64() + CHARACTER_MOVEMENT));
            if (movement != IntPtr.Zero)
            {
                _game.writeFloat(new IntPtr(movement.ToInt64() + MAX_WALK_SPEED), Speed);
            }

            if (_mount == IntPtr.Zero) { return; }

            var rider = readPointer(new IntPtr(pawn.ToInt64() + ROOT_COMPONENT));
            var mount = readPointer(new IntPtr(_mount.ToInt64() + ROOT_COMPONENT));
            if (rider == IntPtr.Zero || mount == IntPtr.Zero) { return; }

            var at = _game.read(new IntPtr(rider.ToInt64() + WORLD_LOCATION), 12);
            if (at == null) { return; }

            var x = BitConverter.ToSingle(at, 0);
            var y = BitConverter.ToSingle(at, 4);
            var z = BitConverter.ToSingle(at, 8);

            //Standing on it rather than in it: the rider's feet are their own capsule's half height
            //below them, and the mount's middle is its half height below that.
            //
            //Both reads are checked rather than trusted. That offset is a capsule's half height, and
            //it is only a capsule if the actor's root happens to be one - read it off anything else
            //and it is whatever float lives at that address. That is how the first version of this
            //put the mount hundreds of metres under the floor, which looked exactly like riding
            //nothing at all.
            var riderHalf = sane(_game.readFloat(new IntPtr(rider.ToInt64() + CAPSULE_HALF_HEIGHT)), 110f);
            var mountHalf = sane(_game.readFloat(new IntPtr(mount.ToInt64() + CAPSULE_HALF_HEIGHT)), 50f);

            //Underneath, with its top at the rider's feet - which only works because the rider was
            //lifted by the same amount when they mounted, and is being held up there by having
            //gravity switched off rather than by being argued with.
            //Worked through rather than guessed, because the first attempt subtracted the mount's
            //half height twice and would have opened a bigger gap than the one it was closing.
            //
            //Ground is the rider's middle less their half height less the lift. The mount stands on
            //that ground, so its middle is one half height above it:
            //
            //    ground      = z - riderHalf - mountHalf * SIT_INTO
            //    mount middle = ground + mountHalf
            //
            //which is the line below, with the two half heights collected.
            var under = z - riderHalf - mountHalf * (SIT_INTO - 1f);

            var where = new byte[12];
            Buffer.BlockCopy(BitConverter.GetBytes(x), 0, where, 0, 4);
            Buffer.BlockCopy(BitConverter.GetBytes(y), 0, where, 4, 4);
            Buffer.BlockCopy(BitConverter.GetBytes(under), 0, where, 8, 4);

            _game.write(new IntPtr(mount.ToInt64() + WORLD_LOCATION), where);
            _game.write(new IntPtr(mount.ToInt64() + RELATIVE_LOCATION), where);

            //And pointed where the rider is pointed, in both of the places a rotation lives.
            //
            //Writing the rotator alone did nothing, which is the same trap the camera fell into
            //earlier in this project. The rotator is what the engine recomputes the world transform
            //from, and it only recomputes when something moves the component. Nothing moves this
            //one - its walk speed is zero and it is being placed by hand - so the rotator changed
            //and the drawn rotation never did. The world quaternion has to be written too, exactly
            //as the position is.
            var facing = _game.read(new IntPtr(rider.ToInt64() + RELATIVE_ROTATION), 12);
            if (facing != null)
            {
                _game.write(new IntPtr(mount.ToInt64() + RELATIVE_ROTATION), facing);
            }

            var turned = _game.read(new IntPtr(rider.ToInt64() + WORLD_ROTATION), 16);
            if (turned != null)
            {
                _game.write(new IntPtr(mount.ToInt64() + WORLD_ROTATION), turned);
            }

        }

        /// <summary>
        /// Puts the rider on top of the mount and stops them falling off it.
        ///
        /// Both at once and once only. The lift is a single write, because a height written every
        /// frame is a fight; turning gravity off is what makes one write enough.
        /// </summary>
        private void lift(IntPtr pawn)
        {
            var rider = readPointer(new IntPtr(pawn.ToInt64() + ROOT_COMPONENT));
            var mountRoot = readPointer(new IntPtr(_mount.ToInt64() + ROOT_COMPONENT));
            var movement = readPointer(new IntPtr(pawn.ToInt64() + CHARACTER_MOVEMENT));
            if (rider == IntPtr.Zero || mountRoot == IntPtr.Zero || movement == IntPtr.Zero) { return; }

            var mountHalf = sane(_game.readFloat(new IntPtr(mountRoot.ToInt64() + CAPSULE_HALF_HEIGHT)), 50f);

            var at = _game.read(new IntPtr(rider.ToInt64() + WORLD_LOCATION), 12);
            if (at == null) { return; }

            var up = new byte[12];
            Buffer.BlockCopy(at, 0, up, 0, 8);
            //Nine tenths of the mount's height rather than all of it. The capsule is taller than
            //the thing drawn inside it, so lifting by the full height leaves the rider hovering a
            //hand's width above its back; sinking slightly into it reads as sitting on it.
            Buffer.BlockCopy(BitConverter.GetBytes(BitConverter.ToSingle(at, 8) + mountHalf * SIT_INTO), 0, up, 8, 4);

            _game.write(new IntPtr(movement.ToInt64() + MOVEMENT_MODE), new[] { FLYING });
            _game.write(new IntPtr(rider.ToInt64() + WORLD_LOCATION), up);
            _game.write(new IntPtr(rider.ToInt64() + RELATIVE_LOCATION), up);

            _lifted = true;
        }

        /// <summary>Puts the rider back on the floor, or they hover for the rest of the level.</summary>
        private void drop()
        {
            if (!_lifted) { return; }
            _lifted = false;

            if (!_live.find(out _)) { return; }

            var movement = readPointer(new IntPtr(_live.Pawn.ToInt64() + CHARACTER_MOVEMENT));
            if (movement == IntPtr.Zero) { return; }

            _game.write(new IntPtr(movement.ToInt64() + MOVEMENT_MODE), new[] { WALKING });
        }

        /// <summary>A height that could belong to something alive, or the fallback if it could not.</summary>
        private static float sane(float? value, float fallback)
        {
            return value is float v && v > 5f && v < 400f ? v : fallback;
        }

        private void freezeRider(bool frozen)
        {
            if (frozen == _riderFrozen && !frozen) { return; }

            //Re-walked rather than remembered, for the same reason as everything else here.
            if (!_live.find(out _)) { _riderFrozen = false; return; }

            var pawn = _live.Pawn;
            if (pawn == IntPtr.Zero) { return; }

            var mesh = readPointer(new IntPtr(pawn.ToInt64() + CHARACTER_MESH));
            if (mesh == IntPtr.Zero) { return; }

            var at = new IntPtr(mesh.ToInt64() + ANIM_FLAGS);
            var flags = _game.read(at, 1);
            if (flags == null) { return; }

            var value = frozen
                ? (byte)(flags[0] | (1 << BIT_PAUSE_ANIMS))
                : (byte)(flags[0] & ~(1 << BIT_PAUSE_ANIMS));

            if (_game.write(at, new[] { value })) { _riderFrozen = frozen; }
        }

        /// <summary>
        /// The closest thing that can actually be sat on.
        ///
        /// The top of the list used to do, and it was wrong in the way that is hardest to see: the
        /// list is everything with a position, so in a room with a crate in it the crate wins, the
        /// take-over refuses it because it has no movement component, and riding comes out as
        /// speed only. Pressing the key again picks the crate again. That is what "spam R until it
        /// finds the creature" was - not a slow search, but the same wrong answer every time,
        /// until something wandered close enough to beat the crate.
        /// </summary>
        private IntPtr nearestCreature(IntPtr pawn, float within = float.MaxValue)
        {
            foreach (var one in nearby(out _))
            {
                if (!one.Rideable) { continue; }
                if (one.Away > within) { break; }
                return one.Actor;
            }

            return IntPtr.Zero;
        }

        /// <summary>
        /// Remembers what is in the level, so that something arriving can be noticed.
        ///
        /// Which is the whole trick behind summoning a mount. Nothing here can spawn an actor, but
        /// Enchanted Grass can - the game summons a sheep through its own code, and a sheep that
        /// was not in the level a moment ago and is now standing next to you is not ambiguous.
        /// Riding the newest thing beats riding the nearest, because the nearest might be a crate.
        /// </summary>
        public void remember()
        {
            _known.Clear();

            foreach (var one in nearby(out _, 200)) { _known.Add(one.Actor); }
        }

        /// <summary>
        /// Whatever has appeared since the last look, nearest first, or zero if nothing has.
        /// </summary>
        public IntPtr newcomer()
        {
            if (_known.Count == 0) { return IntPtr.Zero; }

            foreach (var one in nearby(out _, 200))
            {
                //Rideable, or this hands back something that cannot be carried and the answer
                //comes out as speed only - which is the same fault the nearest-anything search
                //had, arriving by a different route.
                if (!one.Rideable) { continue; }
                if (!_known.Contains(one.Actor)) { return one.Actor; }
            }

            return IntPtr.Zero;
        }

        private readonly System.Collections.Generic.HashSet<IntPtr> _known =
            new System.Collections.Generic.HashSet<IntPtr>();

        private IntPtr readPointer(IntPtr at)
        {
            var bytes = _game.read(at, 8);
            if (bytes == null) { return IntPtr.Zero; }

            var pointer = BitConverter.ToInt64(bytes, 0);
            return pointer > 0x10000 && pointer < 0x7FFFFFFFFFFF ? new IntPtr(pointer) : IntPtr.Zero;
        }
    }
}
