using LiveEdit;
using MCDSaveEdit.Services;
using System;
using System.Windows.Threading;
#nullable enable

namespace MCDSaveEdit.Logic
{
    /// <summary>
    /// What the enemies and your character are made of, held against a game that keeps rebuilding
    /// them.
    ///
    /// Re-applied on a timer rather than set once, and for a better reason than the camera had.
    /// The camera is one object that occasionally gets overwritten; enemies are three hundred
    /// objects that are constantly being created. Anything that walks into the level after the
    /// slider moved arrives at the game's own numbers, so the only way for "tougher" to mean
    /// anything is to keep saying it.
    /// </summary>
    public sealed class LiveStatsLink : IDisposable
    {
        //Often enough that a level change, a death or a new enemy is put right before it is
        //noticed. A second was too slow for that: loading into a mission gave everybody a couple
        //of seconds of being ordinary, which reads as the settings having been forgotten.
        private static readonly TimeSpan APPLY_EVERY = TimeSpan.FromMilliseconds(400);

        private readonly DispatcherTimer _watch;
        private GameProcess? _game;
        private LiveStats? _stats;

        public LiveStatsLink()
        {
            _watch = new DispatcherTimer { Interval = APPLY_EVERY };
            _watch.Tick += (_, _) => tick();
            _watch.Start();
            tick();
        }

        public event Action? changed;

        public bool attached => _stats?.Ready == true;

        public int enemyCount => _stats?.Enemies.Count ?? 0;

        public string status { get; private set; } = "";

        public bool enemiesOn { get; set; }
        public bool playerOn { get; set; }

        public float enemyToughness { get; set; } = 2f;
        public float enemySpeed { get; set; } = 1f;

        public float yourSpeed { get; set; } = 1f;
        public float yourDodgeCooldown { get; set; } = 2.5f;
        public float yourDodgeCharges { get; set; } = 1f;
        public float yourGravity { get; set; } = 1f;
        public float yourAttackSpeed { get; set; } = 1f;

        /// <summary>What your character says right now, for filling the sliders in the first time.</summary>
        public (float speed, float cooldown, float charges, float gravity)? yours()
        {
            if (_stats == null) { return null; }

            var speed = _stats.YourSpeed;
            var cooldown = _stats.YourDodgeCooldown;
            var charges = _stats.YourDodgeCharges;
            var gravity = _stats.YourGravity;
            var attack = _stats.YourAttackSpeed;
            if (speed == null || cooldown == null || charges == null || gravity == null) { return null; }

            yourAttackSpeed = attack ?? 1f;
            return (speed.Value, cooldown.Value, charges.Value, gravity.Value);
        }

        private void tick()
        {
            var was = attached;
            var had = enemyCount;

            if (_game != null && !_game.IsRunning)
            {
                _game.Dispose();
                _game = null;
                _stats = null;
            }

            if (_game == null)
            {
                _game = GameProcess.open(out var problem);
                if (_game == null)
                {
                    status = problem;
                    _stats = null;
                    if (was) { changed?.Invoke(); }
                    return;
                }
                _stats = new LiveStats(_game);
            }

            if (!_stats!.look())
            {
                status = R.STATS_NO_CHARACTER;
                if (was) { changed?.Invoke(); }
                return;
            }

            if (enemiesOn) { _stats.applyToEnemies(enemyToughness, enemySpeed); }
            if (playerOn)
            {
                _stats.applyToPlayer(yourSpeed, yourDodgeCooldown, yourDodgeCharges, yourGravity);
                _stats.applyAttackSpeed(yourAttackSpeed);
            }

            status = string.Format(R.STATS_FOUND, enemyCount);

            if (attached != was || enemyCount != had) { changed?.Invoke(); }
        }

        /// <summary>Puts the enemies back to the game's own numbers.</summary>
        public void restoreEnemies()
        {
            enemiesOn = false;
            _stats?.restoreEnemies();
        }

        /// <summary>Puts your character back to what it was before any of this.</summary>
        public void restorePlayer()
        {
            playerOn = false;
            _stats?.applyToPlayer(1f, 2.5f, 1f, 1f);
            _stats?.applyAttackSpeed(1f);
        }

        public void Dispose()
        {
            _watch.Stop();

            //Nothing here was written down, so closing the editor should leave nothing behind.
            if (enemiesOn) { restoreEnemies(); }
            if (playerOn) { restorePlayer(); }

            _game?.Dispose();
            _game = null;
        }
    }
}
