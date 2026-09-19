using System;
#nullable enable

namespace MCDSaveEdit.Logic
{
    /// <summary>
    /// Enemies that get worse the longer you stay in a level.
    ///
    /// The mechanic is Risk of Rain's: the clock is the real enemy, and a level explored
    /// thoroughly is a level fought at a difficulty nobody chose. Every minute the enemies get a
    /// little tougher and a little faster, so looking in every corner for a chest costs something
    /// that cannot be bought back.
    ///
    /// The arithmetic is here rather than in the link that applies it, because it is the part
    /// worth being able to reason about without a game running: given how long somebody has been
    /// in a level, what should the numbers be.
    ///
    /// Both halves of it were measured before it was written. Toughness reaches every enemy and
    /// stays written - 300 damage a hit became 80 at four times. Speed reaches them through the
    /// game's own multiplier, and takes hold as each mob is next recalculated rather than at once,
    /// which for a ramp is right: the level gets worse around you rather than in a step.
    /// </summary>
    public sealed class Escalation
    {
        /// <summary>
        /// How long a stage lasts, in seconds.
        ///
        /// Adjustable, along with everything else here, because what this is worth is entirely a
        /// matter of taste and of how fast somebody clears a level. A minute is a starting point
        /// rather than a finding.
        /// </summary>
        public float every { get; set; } = 60f;

        /// <summary>What each stage adds to toughness.</summary>
        public float toughnessStep { get; set; } = 1f;

        /// <summary>
        /// And to speed, which is a much smaller number for a reason.
        ///
        /// Toughness at ten times is a fight that takes ten times as long. Speed at ten times is
        /// not a fight at all - enemies cross the room faster than the camera turns - so the two
        /// climb at different rates or the second one ends every run before the first is
        /// interesting.
        /// </summary>
        public float speedStep { get; set; } = 0.2f;

        //Where it stops, and both are settable.
        //
        //Capped rather than open ended by default, which was a choice worth writing down. Uncapped
        //is the purer version of the mechanic - eventually nothing can be killed and the level has
        //won - but this is a game with no run timer and no way to bank progress, so an hour spent
        //in one mission would end in a fight nobody can finish and nothing to show for it. Whoever
        //wants that can raise the caps and have it.
        //
        //Three times speed is about as fast as an enemy can be and still be something you can back
        //away from. Ten times toughness is a tenth of the damage landing, which is slow rather
        //than impossible.
        public float mostSpeed { get; set; } = 3f;
        public float mostToughness { get; set; } = 10f;

        /// <summary>
        /// What each stage is called, in the order they arrive.
        ///
        /// Borrowed in spirit from the game this is taken from, where the difficulty having a name
        /// - and the name getting less reassuring - does more than a number would.
        /// </summary>
        private static readonly string[] NAMES = {
            "Easy", "Medium", "Hard", "Very hard", "Insane",
            "Impossible", "I see you", "I'm coming", "HAHAHA",
        };

        /// <summary>How long the player has been in this level.</summary>
        public TimeSpan inLevel { get; private set; }

        /// <summary>Which stage that is, counting from zero.</summary>
        public int stage => every <= 0f ? 0 : (int)(inLevel.TotalSeconds / every);

        public string stageName => NAMES[Math.Min(stage, NAMES.Length - 1)];

        /// <summary>How far through the current stage, from zero to one, for a bar to fill.</summary>
        public double through
        {
            get
            {
                if (every <= 0f) { return 0.0; }

                var into = inLevel.TotalSeconds % every;
                return Math.Min(1.0, Math.Max(0.0, into / every));
            }
        }

        public TimeSpan untilNext => every <= 0f
            ? TimeSpan.Zero
            : TimeSpan.FromSeconds(every - inLevel.TotalSeconds % every);

        /// <summary>Whether both multipliers have stopped rising.</summary>
        public bool atTheTop(float baseToughness, float baseSpeed)
            => toughnessFrom(baseToughness) >= mostToughness && speedFrom(baseSpeed) >= mostSpeed;

        /// <summary>The toughness to write, given what the slider says to start from.</summary>
        public float toughnessFrom(float start) => Math.Min(mostToughness, start + toughnessStep * stage);

        /// <summary>The speed to write, given what the slider says to start from.</summary>
        public float speedFrom(float start) => Math.Min(mostSpeed, start + speedStep * stage);

        //Which character the clock is being kept for. A level change gives the player a new pawn -
        //the same signal the enemy caches are dropped on - and there is no other moment worth
        //resetting at: dying and carrying on in the same level should not hand back the minutes.
        private IntPtr _for;
        private DateTime _since = DateTime.UtcNow;

        /// <summary>
        /// Moves the clock on, and starts it again when the level changes.
        ///
        /// Called with whoever the player currently is. A pawn of zero means there is no character
        /// to time - a menu, a loading screen - and the clock is left where it is rather than
        /// reset, so that walking through a door does not look like a new level.
        /// </summary>
        public void watch(IntPtr pawn)
        {
            if (pawn == IntPtr.Zero) { return; }

            if (pawn != _for)
            {
                _for = pawn;
                _since = DateTime.UtcNow;
            }

            inLevel = DateTime.UtcNow - _since;
        }

        /// <summary>Starts the clock again, for switching the whole thing on.</summary>
        public void restart()
        {
            _since = DateTime.UtcNow;
            inLevel = TimeSpan.Zero;
        }
    }
}
