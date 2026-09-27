using LiveEdit;
using System;
using System.Collections.Generic;
using System.Linq;
#nullable enable

namespace MCDSaveEdit.Logic
{
    /// <summary>
    /// Damage per second, counted from the enemies' side.
    ///
    /// The game keeps no running total of what you have dealt, and nothing it draws on screen can
    /// be read from outside. What can be read is every enemy's health. So a hit is a drop in an
    /// enemy's health between one look and the next, and the damage dealt is the sum of those
    /// drops. Healing is not counted (a rise is a new starting point, not negative damage), and
    /// neither is an enemy vanishing - one that leaves the level unhurt did not take its health
    /// with it as damage. Killing blows are counted because a dying enemy lingers at zero health
    /// for its death animation, long past the next look.
    ///
    /// Everything on your side is left out by its team: your pets and summons taking hits is not
    /// damage you dealt. What they deal is counted with yours, which is the number worth comparing
    /// builds by - a pet is part of the build. In co-op every player's damage is counted together.
    /// </summary>
    public sealed class DamageMeter
    {
        /// <summary>How far back "now" reaches.</summary>
        public const double WINDOW = 4.0;

        /// <summary>A fight ends after this long with no damage dealt.</summary>
        public const double FIGHT_ENDS = 5.0;

        /// <summary>Seconds of history kept for the bars.</summary>
        public const int HISTORY = 30;

        private readonly Dictionary<long, float> _health = new();
        private readonly Queue<(double at, double amount)> _recent = new();
        private readonly double[] _seconds = new double[HISTORY];
        private long _secondNow = -1;

        public double Dps { get; private set; }
        public bool InFight { get; private set; }
        public double FightTotal { get; private set; }
        public double FightSeconds { get; private set; }
        public double FightPeak { get; private set; }

        /// <summary>The last finished fight's average, shown while nothing is happening.</summary>
        public double LastFightDps { get; private set; }
        public double LastFightTotal { get; private set; }

        private double _fightStart;
        private double _lastHit;

        /// <summary>Damage dealt in each of the last <see cref="HISTORY"/> seconds, oldest first.</summary>
        public IReadOnlyList<double> History
        {
            get
            {
                var list = new double[HISTORY];
                for (var i = 0; i < HISTORY; i++)
                {
                    var second = _secondNow - (HISTORY - 1) + i;
                    list[i] = second < 0 ? 0 : _seconds[second % HISTORY];
                }
                return list;
            }
        }

        public void reset()
        {
            _health.Clear();
            _recent.Clear();
            Array.Clear(_seconds);
            Dps = FightTotal = FightSeconds = FightPeak = LastFightDps = LastFightTotal = 0;
            InFight = false;
        }

        /// <summary>One look at every enemy's health. <paramref name="now"/> is in seconds.</summary>
        public void sample(LiveStats stats, IReadOnlyList<IntPtr> enemies, double now)
        {
            advanceSeconds(now);

            var dealt = 0.0;
            var seen = new HashSet<long>();
            foreach (var enemy in enemies)
            {
                var key = enemy.ToInt64();
                if (!seen.Add(key)) { continue; }
                var health = stats.healthOf(enemy);
                if (health == null) { continue; }
                if (_health.TryGetValue(key, out var before) && health.Value < before)
                {
                    //Counted only on things that are not on your side - checked at the hit rather
                    //than when the list is made, because it is only needed this rarely.
                    if (!stats.isOnYourSide(enemy)) { dealt += before - health.Value; }
                }
                _health[key] = health.Value;
            }
            //Forgotten once gone, so a new enemy built at a freed address starts fresh.
            foreach (var gone in _health.Keys.Where(k => !seen.Contains(k)).ToList()) { _health.Remove(gone); }

            if (dealt > 0) { hit(dealt, now); }
            settle(now);
        }

        private void hit(double amount, double now)
        {
            if (!InFight)
            {
                InFight = true;
                _fightStart = now;
                FightTotal = 0;
                FightPeak = 0;
            }
            _lastHit = now;
            FightTotal += amount;
            _recent.Enqueue((now, amount));
            _seconds[_secondNow % HISTORY] += amount;
        }

        private void settle(double now)
        {
            while (_recent.Count > 0 && now - _recent.Peek().at > WINDOW) { _recent.Dequeue(); }

            if (InFight)
            {
                FightSeconds = Math.Max(0, _lastHit - _fightStart);
                //Over the window, or over the fight while it is younger than the window - one hit
                //a quarter of a second in is not a quarter of its damage per second.
                var span = Math.Clamp(now - _fightStart, 1.0, WINDOW);
                Dps = _recent.Sum(r => r.amount) / span;
                FightPeak = Math.Max(FightPeak, Dps);

                if (now - _lastHit > FIGHT_ENDS)
                {
                    InFight = false;
                    LastFightTotal = FightTotal;
                    LastFightDps = FightTotal / Math.Max(1.0, FightSeconds);
                    Dps = 0;
                }
            }
            else
            {
                Dps = 0;
            }
        }

        private void advanceSeconds(double now)
        {
            var second = (long)Math.Floor(now);
            if (_secondNow < 0) { _secondNow = second; _seconds[second % HISTORY] = 0; return; }
            while (_secondNow < second)
            {
                _secondNow++;
                _seconds[_secondNow % HISTORY] = 0;
            }
        }

        /// <summary>A number the way the game writes big ones: 842, 12.4K, 1.25M.</summary>
        public static string shortly(double value)
        {
            if (value < 1000) { return Math.Round(value).ToString("0", System.Globalization.CultureInfo.InvariantCulture); }
            if (value < 1_000_000) { return (value / 1000).ToString(value < 10_000 ? "0.00" : value < 100_000 ? "0.0" : "0", System.Globalization.CultureInfo.InvariantCulture) + "K"; }
            if (value < 1_000_000_000) { return (value / 1_000_000).ToString(value < 10_000_000 ? "0.00" : value < 100_000_000 ? "0.0" : "0", System.Globalization.CultureInfo.InvariantCulture) + "M"; }
            return (value / 1_000_000_000).ToString("0.00", System.Globalization.CultureInfo.InvariantCulture) + "B";
        }
    }
}
