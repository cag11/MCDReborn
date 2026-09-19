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
            watchJumpKey();
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

        /// <summary>How big enemies are drawn. One is the size the game made them.</summary>
        public float enemySize { get; set; } = 1f;

        /// <summary>How heavily enemies fall, as a multiple of their own weight.</summary>
        public float enemyGravity { get; set; } = 1f;

        /// <summary>Whether J throws every enemy into the air.</summary>
        public bool enemyJumpKey { get; set; }

        /// <summary>How hard it throws them.</summary>
        public float enemyJumpPower { get; set; } = 1200f;

        /// <summary>How many went up, for saying so afterwards.</summary>
        public event Action<int>? enemiesLaunched;

        public float yourSpeed { get; set; } = 1f;
        public float yourDodgeCooldown { get; set; } = 2.5f;
        public float yourDodgeCharges { get; set; } = 1f;
        public float yourGravity { get; set; } = 1f;
        public float yourAttackSpeed { get; set; } = 1f;

        /// <summary>
        /// Whether enemies nobody can see are left unanimated.
        ///
        /// On by default, which is unusual for anything here - everything else waits to be asked.
        /// This one is worth more than doubling the frame rate in a crowded fight and changes
        /// nothing anybody can see, so asking first would be asking whether somebody would like
        /// their game to run badly.
        /// </summary>
        public bool poseOnlyWhenSeen { get; set; } = true;


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


        #region The jump key

        //J. Dungeons binds nothing to it, and it is nowhere near the movement keys - a key that
        //throws every enemy in the level upwards is not one to hit by accident while walking.
        private const int JUMP_KEY = 0x4A;

        [System.Runtime.InteropServices.DllImport("user32.dll")]
        private static extern short GetAsyncKeyState(int key);

        [System.Runtime.InteropServices.DllImport("user32.dll")]
        private static extern IntPtr GetForegroundWindow();

        private System.Threading.Thread? _keyWatch;
        private volatile bool _watching;
        private bool _jumpWasDown;

        private void watchJumpKey()
        {
            _watching = true;
            _keyWatch = LiveEdit.Trouble.start("enemy jump key", () => {
                while (_watching)
                {
                    //Only while the game has the keyboard. Otherwise typing a J into the search
                    //box on any other tab would throw the level into the air.
                    var down = enemyJumpKey && playing() && (GetAsyncKeyState(JUMP_KEY) & 0x8000) != 0;

                    if (down && !_jumpWasDown)
                    {
                        //Onto the thread that owns the reading, so a launch cannot arrive halfway
                        //through the pass that lists the enemies.
                        System.Windows.Application.Current?.Dispatcher.BeginInvoke(new Action(launchNow));
                    }
                    _jumpWasDown = down;

                    System.Threading.Thread.Sleep(30);
                }
            });
        }

        private bool playing()
        {
            var game = _game;
            if (game == null) { return false; }

            try { return game.Process.MainWindowHandle == GetForegroundWindow(); }
            catch (Exception) { return false; }
        }

        private void launchNow()
        {
            if (_stats == null || !_stats.Ready) { return; }

            var thrown = _stats.launchEnemies(enemyJumpPower);
            if (thrown > 0) { enemiesLaunched?.Invoke(thrown); }
        }

        #endregion

        private bool _saidEnemiesOn;

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
                Services.Journal.note($"enemy loop attached to pid {_game.Id} ({_game.Process.ProcessName})");
                _stats = new LiveStats(_game);
            }

            if (!_stats!.look())
            {
                status = R.STATS_NO_CHARACTER;
                if (was) { changed?.Invoke(); }
                return;
            }

            if (enemiesOn != _saidEnemiesOn)
            {
                _saidEnemiesOn = enemiesOn;
                Services.Journal.note($"enemy settings {(enemiesOn ? "on" : "off")}"
                    + $" - toughness {enemyToughness}, speed {enemySpeed}, size {enemySize}, gravity {enemyGravity}");
            }

            if (enemiesOn) { _stats.applyToEnemies(enemyToughness, enemySpeed, enemySize, enemyGravity); }
            _stats.applyPosing(poseOnlyWhenSeen);
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
            _watching = false;
            _keyWatch?.Join(200);

            //Nothing here was written down, so closing the editor should leave nothing behind.
            if (enemiesOn) { restoreEnemies(); }
            if (playerOn) { restorePlayer(); }
            _stats?.applyPosing(false);

            _game?.Dispose();
            _game = null;
        }
    }
}
