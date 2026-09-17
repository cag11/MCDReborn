using System;
using System.Collections.Generic;

namespace LiveEdit
{
    /// <summary>
    /// What the enemies and your character are made of, changed while the game runs.
    ///
    /// This began as an attempt to make levels denser, which turned out to be the hard version of
    /// the question - enemy counts are decided when a room is built and are not a number sitting
    /// anywhere convenient. How strong those enemies are, though, is exactly that kind of number.
    ///
    /// Every character in this game - yours and each of theirs - carries gameplay attributes:
    /// health, a speed multiplier, a dodge cooldown, how much damage they take. They live in
    /// attribute sets hanging off the character's ability system, and they are all plain floats.
    ///
    /// Which set is which cannot be asked, because there is no name table to ask. So they are
    /// identified on your own character, where the numbers say plainly what they are - a health
    /// set is the one whose current and maximum agree, a movement set is the one with a dodge
    /// cooldown among its multipliers - and each set is an object of its own class, so the vtable
    /// learned from you finds the same set on all three hundred enemies.
    /// </summary>
    public sealed class LiveStats
    {
        private const int ABILITY_SYSTEM = 0x08B0;
        private const int SPAWNED_ATTRIBUTES = 0x0188;
        private const int SPAWNED_COUNT = 0x0190;

        private const int MOVEMENT_COMPONENT = 0x0398;
        private const int MAX_WALK_SPEED = 0x01DC;

        private const int PERSISTENT_LEVEL = 0x0030;
        private const int LEVEL_ACTORS = 0x0098;
        private const int LEVEL_ACTORS_COUNT = 0x00A0;

        //UHealthAttributeSet.
        private const int HEALTH = 0x0034;
        private const int MAX_HEALTH = 0x0038;
        private const int TAKE_DAMAGE = 0x0050;

        //UMovementAttributeSet.
        private const int SPEED_MULTIPLIER = 0x0030;
        private const int DODGE_COOLDOWN = 0x003C;
        private const int DODGE_CHARGES = 0x0044;
        private const int MAX_DODGE_CHARGES = 0x0048;
        private const int GRAVITY = 0x004C;

        //UMeleeAttributeSet and URangedAttributeSet, which are laid out the same way where it
        //matters: an item power, a damage multiplier, and the attack speed this changes.
        private const int ATTACK_ITEM_POWER = 0x0030;
        private const int ATTACK_DAMAGE_MULTIPLIER = 0x0038;
        private const int ATTACK_SPEED_MULTIPLIER = 0x003C;

        //ACharacter's skeletal mesh, and the setting on it that decides what a crowd costs.
        //
        //Three is OnlyTickPoseWhenRendered - Unreal's own instruction not to animate a skeleton
        //nobody is looking at. This game does not use it, so in a room of two hundred enemies
        //every one of them is posed every frame whether it is on screen or behind you.
        //
        //Measured at 54 frames a second before and 138 after, in a fight with 198 enemies. It is
        //the largest single thing in this whole project.
        private const int CHARACTER_MESH = 0x0390;
        private const int VISIBILITY_ANIM_TICK = 0x06B4;
        private const int ANIM_FLAGS = 0x06B7;
        private const int BIT_UPDATE_RATE_OPTIMISATIONS = 0;
        private const byte ONLY_TICK_POSE_WHEN_RENDERED = 3;
        private const byte ALWAYS_TICK_POSE_AND_REFRESH = 0;

        //What did not matter.
        //
        //Turning off enemy shadows, stopping enemies being posed while off screen, and capping how
        //far away they are drawn all sounded like the obvious answers to a crowd costing frames.
        //In a room with a hundred and forty one enemies they were worth minus four, minus three and
        //minus two frames - noise, and slightly the wrong way.
        //
        //What actually cost the frames was how much of the world was on screen. Field of view 75
        //to 60 was worth twenty one frames on the same scene, which is more than every other lever
        //put together and is a setting the camera tab already has.
        //
        //Turning off enemy shadows was worth seven tenths of a frame - nothing - and capping how
        //far away they are drawn was no better. The obvious answer was the wrong one twice, and
        //the unobvious one was worth more than doubling the frame rate.
        //
        //Both were first measured as useless by a frame counter that was counting this app's own
        //writes rather than the game's frames. Anything measured with a broken instrument is worth
        //measuring again.

        private const int MOST_ACTORS = 40000;

        private readonly GameProcess _game;
        private readonly LiveCamera _chain;

        //Learned from your own character, then used to find the same sets on everything else.
        private long _healthSetClass;
        private long _movementSetClass;

        //Both the melee and the ranged set, because a character swings one and shoots the other
        //and nobody wants only half of "attack faster".
        private readonly List<long> _attackSetClasses = new List<long>();

        //What each enemy walks at before anything here touched it, so speed can be a multiplier
        //of its own kind rather than one number for a skeleton and a phantom alike. Dropped when
        //the level changes, because the addresses go with it.
        private readonly Dictionary<long, float> _enemyWalk = new Dictionary<long, float>();
        private IntPtr _walkFor;

        public LiveStats(GameProcess game)
        {
            _game = game;
            _chain = new LiveCamera(game);
        }

        public IntPtr Player { get; private set; }

        /// <summary>Every enemy in the level that has stats worth changing.</summary>
        public IReadOnlyList<IntPtr> Enemies { get; private set; } = Array.Empty<IntPtr>();

        public bool Ready => _healthSetClass != 0 && _movementSetClass != 0;

        /// <summary>
        /// Finds your character, works out which set is which, and lists the enemies.
        ///
        /// Done again each time rather than remembered, for the reason everything else here is:
        /// a level change frees all of it, and a pointer kept across one is a pointer into memory
        /// the game has taken back.
        /// </summary>
        public bool look()
        {
            Player = IntPtr.Zero;
            Enemies = Array.Empty<IntPtr>();

            if (!_chain.find(out _)) { return false; }

            Player = _chain.Pawn;
            if (Player == IntPtr.Zero) { return false; }

            learnFromPlayer();
            if (!Ready) { return false; }

            var image = _game.Process.MainModule?.BaseAddress ?? IntPtr.Zero;
            var world = follow(new IntPtr(image.ToInt64() + LiveCamera.GWORLD_OFFSET));
            var level = world == IntPtr.Zero ? IntPtr.Zero : follow(new IntPtr(world.ToInt64() + PERSISTENT_LEVEL));
            if (level == IntPtr.Zero) { return true; }

            var actors = follow(new IntPtr(level.ToInt64() + LEVEL_ACTORS));
            var countBytes = _game.read(new IntPtr(level.ToInt64() + LEVEL_ACTORS_COUNT), 4);
            if (actors == IntPtr.Zero || countBytes == null) { return true; }

            var count = BitConverter.ToInt32(countBytes, 0);
            if (count < 1 || count > MOST_ACTORS) { return true; }

            var enemies = new List<IntPtr>();
            for (int i = 0; i < count; i++)
            {
                var actor = follow(new IntPtr(actors.ToInt64() + i * 8));
                if (actor == IntPtr.Zero || actor == Player) { continue; }

                //A believable walk speed is what tells a character from the other things in a
                //level that happen to have two pointers in the right places - which is what an
                //earlier, looser test kept picking up.
                var movement = follow(new IntPtr(actor.ToInt64() + MOVEMENT_COMPONENT));
                if (movement == IntPtr.Zero) { continue; }

                var walk = _game.readFloat(new IntPtr(movement.ToInt64() + MAX_WALK_SPEED));
                if (walk == null || walk <= 0f || walk > 20000f) { continue; }

                if (setOf(actor, _healthSetClass) == IntPtr.Zero) { continue; }

                enemies.Add(actor);
            }

            Enemies = enemies;
            return true;
        }

        /// <summary>
        /// Works out which attribute set is which, by looking at your own.
        ///
        /// Only done once. The classes do not change while the game runs, and your character is
        /// the one place the numbers are unambiguous - a health set with a current and a maximum
        /// that agree, and a movement set with a dodge cooldown in seconds sitting among a row of
        /// multipliers.
        /// </summary>
        private void learnFromPlayer()
        {
            //Checked rather than trusted. Identification used to happen once and be believed for
            //ever, which went wrong the first time it ran at an awkward moment - it settled on
            //something that looked enough like a movement set, and the tab then showed a roll
            //cooldown of 0.4 seconds and a gravity of 0.1 for a character whose real numbers are
            //2.5 and 1. Believing that would have written those onto somebody's character.
            if (Ready && stillMakesSense()) { return; }

            _healthSetClass = 0;
            _movementSetClass = 0;
            _attackSetClasses.Clear();

            foreach (var set in setsOf(Player))
            {
                var kind = classOf(set);
                if (kind == 0) { continue; }

                if (_healthSetClass == 0 && looksLikeHealth(set))
                {
                    _healthSetClass = kind;
                    continue;
                }

                if (_movementSetClass == 0 && looksLikeMovement(set))
                {
                    _movementSetClass = kind;
                    continue;
                }

                if (looksLikeAttack(set) && !_attackSetClasses.Contains(kind))
                {
                    _attackSetClasses.Add(kind);
                }
            }
        }

        /// <summary>
        /// Whether a set really is the movement one.
        ///
        /// Gravity is what makes this reliable. A movement set has several multipliers that sit
        /// at one and a cooldown in seconds, and plenty of other sets have a float near one in
        /// their first slot - but gravity being one, a dodge count being a whole number, and a
        /// cooldown in the range a cooldown is actually written in, do not line up by accident.
        /// </summary>
        private bool looksLikeMovement(IntPtr set)
        {
            var speed = _game.readFloat(new IntPtr(set.ToInt64() + SPEED_MULTIPLIER));
            var cooldown = _game.readFloat(new IntPtr(set.ToInt64() + DODGE_COOLDOWN));
            var charges = _game.readFloat(new IntPtr(set.ToInt64() + MAX_DODGE_CHARGES));
            var gravity = _game.readFloat(new IntPtr(set.ToInt64() + GRAVITY));

            if (speed == null || cooldown == null || charges == null || gravity == null) { return false; }

            //Wide, on purpose. These are the numbers the sliders move, so anything tight enough
            //to be a good test is also tight enough to stop recognising a character whose owner
            //has turned gravity down - which is exactly what happened: the set was identified,
            //then rejected a moment later for holding the value it had just been given.
            //
            //Gravity carries the test instead. The impostor set that fooled an earlier version
            //has a zero there, and zero is not a gravity anybody would ask for.
            if (speed <= 0.01f || speed > 50f) { return false; }
            if (cooldown < 0.01f || cooldown > 60f) { return false; }
            if (gravity < 0.005f || gravity > 20f) { return false; }

            //Charges are counted, not measured.
            if (charges < 1f || charges > 20f) { return false; }
            return Math.Abs(charges.Value - MathF.Round(charges.Value)) < 0.01f;
        }

        /// <summary>
        /// Whether a set is one a weapon swings through.
        ///
        /// Both the melee and the ranged set begin with an item power - a real number in the tens
        /// of thousands on a levelled character, never a multiplier - followed by a damage and a
        /// speed that sit near one. Nothing else on the character has that combination.
        /// </summary>
        private bool looksLikeAttack(IntPtr set)
        {
            var power = _game.readFloat(new IntPtr(set.ToInt64() + ATTACK_ITEM_POWER));
            var damage = _game.readFloat(new IntPtr(set.ToInt64() + ATTACK_DAMAGE_MULTIPLIER));
            var speed = _game.readFloat(new IntPtr(set.ToInt64() + ATTACK_SPEED_MULTIPLIER));

            if (power == null || damage == null || speed == null) { return false; }
            if (float.IsNaN(power.Value) || float.IsNaN(damage.Value) || float.IsNaN(speed.Value)) { return false; }

            return power > 1f && damage >= 0.01f && damage <= 1000f
                && speed >= 0.01f && speed <= 50f;
        }

        private bool looksLikeHealth(IntPtr set)
        {
            var health = _game.readFloat(new IntPtr(set.ToInt64() + HEALTH));
            var maxHealth = _game.readFloat(new IntPtr(set.ToInt64() + MAX_HEALTH));

            return health != null && maxHealth != null
                && maxHealth > 1f && health > 0f && health <= maxHealth * 1.01f;
        }

        /// <summary>Whether what was identified earlier still reads like itself.</summary>
        private bool stillMakesSense()
        {
            var movement = setOf(Player, _movementSetClass);
            var health = setOf(Player, _healthSetClass);

            return movement != IntPtr.Zero && health != IntPtr.Zero
                && looksLikeMovement(movement) && looksLikeHealth(health);
        }

        #region What the enemies are made of

        /// <summary>
        /// Makes every enemy tougher and faster, or puts them back.
        ///
        /// Toughness is how much damage they take rather than how much health they have, which is
        /// the same thing to play against and far safer to write: every enemy starts at exactly
        /// one, so it can be set outright, while health is a different number for every kind of
        /// enemy and would have to be remembered per enemy and put back per enemy.
        ///
        /// Applied over and over rather than once, because enemies that appear later are built
        /// fresh and arrive at the game's own numbers.
        /// </summary>
        public int applyToEnemies(float toughness, float speed)
        {
            var done = 0;

            foreach (var enemy in Enemies)
            {
                var health = setOf(enemy, _healthSetClass);
                var movement = setOf(enemy, _movementSetClass);

                //Tougher means taking less of each hit, so the multiplier goes down as the slider
                //goes up.
                if (health != IntPtr.Zero && toughness > 0.01f)
                {
                    if (writeFloat(health, TAKE_DAMAGE, 1f / toughness)) { done++; }
                }

                if (movement != IntPtr.Zero)
                {
                    writeFloat(movement, SPEED_MULTIPLIER, speed);
                }

                //And the walk speed itself.
                //
                //The multiplier is the game's own way of saying this, and whether it reaches a
                //mob's movement was never established - there were no enemies in the level the
                //day it was tested. The walk speed is not in question: it is the number the
                //movement component uses, the same one the player's own speed was measured from.
                //Writing both means the slider works whichever way the game does it.
                var walker = follow(new IntPtr(enemy.ToInt64() + MOVEMENT_COMPONENT));
                if (walker != IntPtr.Zero)
                {
                    var wasWalking = baseWalkOf(enemy, walker);
                    if (wasWalking != null)
                    {
                        _game.writeFloat(new IntPtr(walker.ToInt64() + MAX_WALK_SPEED), wasWalking.Value * speed);
                    }
                }
            }

            return done;
        }

        public int restoreEnemies() => applyToEnemies(1f, 1f);

        /// <summary>
        /// Stops the game animating enemies nobody can see.
        ///
        /// Worth 54 frames a second to 138 in a fight with two hundred enemies, which is more than
        /// every other thing in this project put together. Unreal ships the setting; the game
        /// simply does not use it.
        ///
        /// Applied over and over like everything else here, because an enemy that walks in later
        /// arrives animating the way the game intended.
        /// </summary>
        public int applyPosing(bool onlyWhenSeen)
        {
            var done = 0;

            foreach (var enemy in Enemies)
            {
                var mesh = follow(new IntPtr(enemy.ToInt64() + CHARACTER_MESH));
                if (mesh == IntPtr.Zero) { continue; }

                _game.write(new IntPtr(mesh.ToInt64() + VISIBILITY_ANIM_TICK),
                    new[] { onlyWhenSeen ? ONLY_TICK_POSE_WHEN_RENDERED : ALWAYS_TICK_POSE_AND_REFRESH });

                var flags = _game.read(new IntPtr(mesh.ToInt64() + ANIM_FLAGS), 1);
                if (flags != null)
                {
                    var value = onlyWhenSeen
                        ? (byte)(flags[0] | (1 << BIT_UPDATE_RATE_OPTIMISATIONS))
                        : (byte)(flags[0] & ~(1 << BIT_UPDATE_RATE_OPTIMISATIONS));

                    _game.write(new IntPtr(mesh.ToInt64() + ANIM_FLAGS), new[] { value });
                }

                done++;
            }

            return done;
        }


        /// <summary>
        /// What an enemy walked at before this touched it.
        ///
        /// Remembered rather than read each time, because after the first write the number on the
        /// enemy is already a multiple of itself - reading it again and multiplying would make a
        /// slider set to two mean four, then eight.
        /// </summary>
        private float? baseWalkOf(IntPtr enemy, IntPtr movement)
        {
            //Addresses belong to a level. Keeping them across one would be remembering a speed
            //for whatever now lives at that address.
            if (_walkFor != _chain.Pawn) { _enemyWalk.Clear(); _walkFor = _chain.Pawn; }

            if (_enemyWalk.TryGetValue(enemy.ToInt64(), out var already)) { return already; }

            var now = _game.readFloat(new IntPtr(movement.ToInt64() + MAX_WALK_SPEED));
            if (now == null || now <= 0f || now > 20000f) { return null; }

            _enemyWalk[enemy.ToInt64()] = now.Value;
            return now.Value;
        }

        #endregion

        #region What you are made of

        public float? YourSpeed => readFrom(Player, _movementSetClass, SPEED_MULTIPLIER);
        public float? YourDodgeCooldown => readFrom(Player, _movementSetClass, DODGE_COOLDOWN);
        public float? YourDodgeCharges => readFrom(Player, _movementSetClass, MAX_DODGE_CHARGES);
        public float? YourGravity => readFrom(Player, _movementSetClass, GRAVITY);
        public float? YourHealth => readFrom(Player, _healthSetClass, HEALTH);
        public float? YourMaxHealth => readFrom(Player, _healthSetClass, MAX_HEALTH);

        /// <summary>
        /// Changes what your own character can do.
        ///
        /// These four and no more, deliberately. They sit at one by default and mean the same
        /// thing on any character, so writing them is safe. What is not offered is how much damage
        /// you take - yours reads 0.65 rather than 1, because your gear put it there, and a slider
        /// that overwrote it would quietly delete the protection you earned.
        /// </summary>
        /// <summary>How fast you swing and shoot, as the game's own multiplier.</summary>
        public float? YourAttackSpeed
        {
            get
            {
                foreach (var kind in _attackSetClasses)
                {
                    var set = setOf(Player, kind);
                    if (set == IntPtr.Zero) { continue; }

                    var value = _game.readFloat(new IntPtr(set.ToInt64() + ATTACK_SPEED_MULTIPLIER));
                    if (value != null) { return value; }
                }
                return null;
            }
        }

        public bool applyAttackSpeed(float times)
        {
            var done = false;
            foreach (var kind in _attackSetClasses)
            {
                var set = setOf(Player, kind);
                if (set == IntPtr.Zero) { continue; }

                if (writeFloat(set, ATTACK_SPEED_MULTIPLIER, times)) { done = true; }
            }
            return done;
        }

        public bool applyToPlayer(float speed, float dodgeCooldown, float dodgeCharges, float gravity)
        {
            var movement = setOf(Player, _movementSetClass);
            if (movement == IntPtr.Zero) { return false; }

            writeFloat(movement, SPEED_MULTIPLIER, speed);
            writeFloat(movement, DODGE_COOLDOWN, dodgeCooldown);
            writeFloat(movement, GRAVITY, gravity);

            //Both, or the extra charges exist and cannot be used until something refills them.
            writeFloat(movement, MAX_DODGE_CHARGES, dodgeCharges);

            var have = _game.readFloat(new IntPtr(movement.ToInt64() + DODGE_CHARGES));
            if (have != null && have < dodgeCharges) { writeFloat(movement, DODGE_CHARGES, dodgeCharges); }

            return true;
        }

        #endregion

        private IEnumerable<IntPtr> setsOf(IntPtr actor)
        {
            var ability = follow(new IntPtr(actor.ToInt64() + ABILITY_SYSTEM));
            if (ability == IntPtr.Zero) { yield break; }

            var array = follow(new IntPtr(ability.ToInt64() + SPAWNED_ATTRIBUTES));
            var countBytes = _game.read(new IntPtr(ability.ToInt64() + SPAWNED_COUNT), 4);
            if (array == IntPtr.Zero || countBytes == null) { yield break; }

            var count = BitConverter.ToInt32(countBytes, 0);
            if (count < 1 || count > 64) { yield break; }

            for (int i = 0; i < count; i++)
            {
                var one = follow(new IntPtr(array.ToInt64() + i * 8));
                if (one != IntPtr.Zero) { yield return one; }
            }
        }

        /// <summary>One character's set of a particular kind, found by the class it belongs to.</summary>
        private IntPtr setOf(IntPtr actor, long kind)
        {
            if (kind == 0) { return IntPtr.Zero; }

            foreach (var set in setsOf(actor))
            {
                if (classOf(set) == kind) { return set; }
            }
            return IntPtr.Zero;
        }

        /// <summary>
        /// What class an object is, as far as this can tell without names: its vtable.
        ///
        /// Every instance of a class shares one, so it works as an identity even though it cannot
        /// be turned into a word.
        /// </summary>
        private long classOf(IntPtr instance)
        {
            var bytes = _game.read(instance, 8);
            if (bytes == null) { return 0; }

            var vtable = BitConverter.ToInt64(bytes, 0);
            return vtable > 0x10000 && vtable < 0x7FFFFFFFFFFF ? vtable : 0;
        }

        private float? readFrom(IntPtr actor, long kind, int offset)
        {
            if (actor == IntPtr.Zero) { return null; }

            var set = setOf(actor, kind);
            return set == IntPtr.Zero ? null : _game.readFloat(new IntPtr(set.ToInt64() + offset));
        }

        private bool writeFloat(IntPtr set, int offset, float value)
        {
            return _game.writeFloat(new IntPtr(set.ToInt64() + offset), value);
        }

        private IntPtr follow(IntPtr at)
        {
            var bytes = _game.read(at, 8);
            if (bytes == null) { return IntPtr.Zero; }

            var pointer = BitConverter.ToInt64(bytes, 0);
            return pointer > 0x10000 && pointer < 0x7FFFFFFFFFFF ? new IntPtr(pointer) : IntPtr.Zero;
        }
    }
}
