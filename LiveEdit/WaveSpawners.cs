using System;
using System.Collections.Generic;

namespace LiveEdit
{
    /// <summary>
    /// How many enemies a level sends at you, changed while it is running.
    ///
    /// The obvious way to make a game spawn more of something is to hook the function that spawns
    /// it and call it several times. That needs code inside the game - a library, a hook, and all
    /// the crashes that come with getting either slightly wrong.
    ///
    /// None of that is necessary here, because the counts are data rather than code. `AWaveSpawner`
    /// keeps its wave sizes, its rate and its radius as plain floats on the actor, so making a
    /// level denser is the same kind of write as moving the camera: attach, walk, write.
    ///
    /// Finding them is the only interesting part. An actor cannot be asked its class name in this
    /// build - there is no walkable name table - so instead every actor in the level is grouped by
    /// its vtable pointer, which every instance of a class shares, and a group is identified by
    /// whether its numbers look like wave sizes. A spawner that says it starts at 1.25 waves,
    /// grows by 0.25, stops at 5, within a radius of 2500, is not something other state resembles.
    /// </summary>
    public sealed class WaveSpawners
    {
        //UWorld, and the list of everything in the level. Dumper-7 annotates this one itself.
        private const int PERSISTENT_LEVEL = 0x0030;
        private const int LEVEL_ACTORS = 0x0098;
        private const int LEVEL_ACTORS_COUNT = 0x00A0;

        //AWaveSpawner's numbers, in the order they sit in memory.
        private const int SPAWN_MIN_DISTANCE = 0x0378;
        private const int SPAWN_RADIUS = 0x037C;
        private const int SPAWN_RATE_MAX = 0x0380;
        private const int WAVE_SIZE_START = 0x0384;
        private const int WAVE_SIZE_PER_WAVE = 0x0388;
        private const int WAVE_SIZE_MAX = 0x038C;

        //Enough of the level to be worth reading, and not so much that a wrong count walks off
        //into nothing.
        private const int MOST_ACTORS = 20000;

        private readonly GameProcess _game;

        /// <summary>What each spawner said before any of this touched it.</summary>
        private readonly Dictionary<long, float[]> _original = new Dictionary<long, float[]>();

        public WaveSpawners(GameProcess game)
        {
            _game = game;
        }

        /// <summary>The level the spawners were found in, so a new one can be noticed.</summary>
        public IntPtr Level { get; private set; }

        /// <summary>Where the spawners are, as of the last look.</summary>
        public IReadOnlyList<IntPtr> Found { get; private set; } = Array.Empty<IntPtr>();

        /// <summary>
        /// Looks through the level for anything shaped like a wave spawner.
        ///
        /// Cheap enough to do on a timer - a thousand or so actors is a few thousand reads, and
        /// only the ones that survive the first test are read past their vtable.
        /// </summary>
        public bool look()
        {
            var image = _game.image(out _);
            if (image == IntPtr.Zero) { return false; }

            var world = follow(new IntPtr(image.ToInt64() + LiveCamera.WorldOffset));
            if (world == IntPtr.Zero) { return false; }

            var level = follow(new IntPtr(world.ToInt64() + PERSISTENT_LEVEL));
            if (level == IntPtr.Zero) { return false; }

            var actors = follow(new IntPtr(level.ToInt64() + LEVEL_ACTORS));
            var countBytes = _game.read(new IntPtr(level.ToInt64() + LEVEL_ACTORS_COUNT), 4);
            if (actors == IntPtr.Zero || countBytes == null) { return false; }

            var count = BitConverter.ToInt32(countBytes, 0);
            if (count < 1 || count > MOST_ACTORS) { return false; }

            //A new level is a new set of spawners, and the old ones are gone with it.
            if (level != Level) { _original.Clear(); }
            Level = level;

            var found = new List<IntPtr>();
            for (int i = 0; i < count; i++)
            {
                var actor = follow(new IntPtr(actors.ToInt64() + i * 8));
                if (actor == IntPtr.Zero) { continue; }
                if (readNumbers(actor) == null) { continue; }

                found.Add(actor);
            }

            Found = found;
            return found.Count > 0;
        }

        /// <summary>
        /// A spawner's six numbers, or nothing if this actor is not one.
        ///
        /// The test is what the numbers mean rather than what they are. Waves start somewhere
        /// above zero and end somewhere sensible, a radius is a distance rather than a rounding
        /// error, and none of it is a denormal - which is what most of the false matches were
        /// when this was first tried across a whole level.
        /// </summary>
        private float[]? readNumbers(IntPtr actor)
        {
            var bytes = _game.read(new IntPtr(actor.ToInt64() + SPAWN_MIN_DISTANCE), 24);
            if (bytes == null) { return null; }

            var values = new float[6];
            for (int i = 0; i < 6; i++)
            {
                values[i] = BitConverter.ToSingle(bytes, i * 4);
                if (float.IsNaN(values[i]) || float.IsInfinity(values[i])) { return null; }
            }

            var minDistance = values[0];
            var radius = values[1];
            var rate = values[2];
            var start = values[3];
            var perWave = values[4];
            var most = values[5];

            if (minDistance < 0f || minDistance > 100000f) { return null; }
            if (radius < 1f || radius > 100000f) { return null; }
            if (rate <= 0f || rate > 1000f) { return null; }
            if (start <= 0f || start > 500f) { return null; }
            if (perWave < 0f || perWave > 500f) { return null; }
            if (most < start || most > 2000f) { return null; }

            return values;
        }

        /// <summary>
        /// Multiplies what every spawner in the level sends at you.
        ///
        /// Each spawner's own numbers are the starting point rather than one shared figure,
        /// because a level's encounters are not all the same size and multiplying them keeps
        /// whatever the designers meant by the difference between them.
        /// </summary>
        public int apply(float countTimes, float rateTimes, float radiusTimes, float minDistanceTimes)
        {
            var done = 0;

            foreach (var spawner in Found)
            {
                var were = originalOf(spawner);
                if (were == null) { continue; }

                //Rate is a delay, so more often means a smaller number - dividing rather than
                //multiplying is what makes the slider read the way somebody expects.
                var rate = rateTimes > 0.01f ? were[2] / rateTimes : were[2];

                var ok = writeFloat(spawner, SPAWN_MIN_DISTANCE, were[0] * minDistanceTimes)
                    && writeFloat(spawner, SPAWN_RADIUS, were[1] * radiusTimes)
                    && writeFloat(spawner, SPAWN_RATE_MAX, rate)
                    && writeFloat(spawner, WAVE_SIZE_START, were[3] * countTimes)
                    && writeFloat(spawner, WAVE_SIZE_PER_WAVE, were[4] * countTimes)
                    && writeFloat(spawner, WAVE_SIZE_MAX, were[5] * countTimes);

                if (ok) { done++; }
            }

            return done;
        }

        /// <summary>Puts every spawner back the way the level had it.</summary>
        public int restore()
        {
            var done = 0;

            foreach (var pair in _original)
            {
                var spawner = new IntPtr(pair.Key);
                var were = pair.Value;

                var ok = writeFloat(spawner, SPAWN_MIN_DISTANCE, were[0])
                    && writeFloat(spawner, SPAWN_RADIUS, were[1])
                    && writeFloat(spawner, SPAWN_RATE_MAX, were[2])
                    && writeFloat(spawner, WAVE_SIZE_START, were[3])
                    && writeFloat(spawner, WAVE_SIZE_PER_WAVE, were[4])
                    && writeFloat(spawner, WAVE_SIZE_MAX, were[5]);

                if (ok) { done++; }
            }

            _original.Clear();
            return done;
        }

        /// <summary>
        /// What a spawner said the first time it was seen.
        ///
        /// Taken once and kept, because every reading after the first is of a spawner this has
        /// already multiplied - and multiplying a multiplied number is how a slider set to three
        /// quietly becomes twenty seven.
        /// </summary>
        private float[]? originalOf(IntPtr spawner)
        {
            if (_original.TryGetValue(spawner.ToInt64(), out var already)) { return already; }

            var now = readNumbers(spawner);
            if (now == null) { return null; }

            _original[spawner.ToInt64()] = now;
            return now;
        }

        private bool writeFloat(IntPtr spawner, int offset, float value)
        {
            return _game.writeFloat(new IntPtr(spawner.ToInt64() + offset), value);
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
