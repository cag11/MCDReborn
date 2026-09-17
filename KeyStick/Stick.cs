using System;

namespace KeyStick
{
    /// <summary>
    /// Four on-or-off keys turned into something a stick would have produced.
    ///
    /// Two things have to be got right or it reads as a keyboard pretending, which is exactly what
    /// it is:
    ///
    /// **Diagonals.** Holding W and D is two full deflections, and a stick pushed to its corner is
    /// still only pushed as far as its rim. Left alone, moving diagonally would be forty per cent
    /// faster than moving straight - the classic bug of every homemade movement system.
    ///
    /// **The ramp.** A key goes from nothing to everything in one pass, which a thumb cannot do. A
    /// short climb to full deflection is what separates a character that accelerates from one that
    /// teleports into motion, and the same on release stops the abrupt halt.
    /// </summary>
    public sealed class Stick
    {
        private double _x;
        private double _y;

        /// <summary>
        /// How long full deflection takes, in seconds. Short enough to feel immediate, long enough
        /// that the character leans into a turn rather than snapping round it.
        /// </summary>
        public double RampSeconds { get; set; } = 0.08;

        public double X => _x;
        public double Y => _y;

        /// <summary>Where the keys are asking to go, before any smoothing.</summary>
        public static (double x, double y) wanted(bool west, bool east, bool north, bool south)
        {
            var x = (east ? 1.0 : 0.0) - (west ? 1.0 : 0.0);
            var y = (north ? 1.0 : 0.0) - (south ? 1.0 : 0.0);

            //The corner is brought back onto the rim. A stick cannot reach 1.41 in any direction
            //and neither should this.
            var length = Math.Sqrt(x * x + y * y);
            if (length > 1.0)
            {
                x /= length;
                y /= length;
            }
            return (x, y);
        }

        /// <summary>Moves the stick towards where the keys are asking, and says where it now is.</summary>
        public (double x, double y) advance(double targetX, double targetY, double seconds)
        {
            //A ramp measured in seconds rather than in passes, so the feel does not change with
            //how fast the loop happens to run.
            var step = RampSeconds <= 0 ? 1.0 : Math.Min(1.0, seconds / RampSeconds);

            _x += (targetX - _x) * step;
            _y += (targetY - _y) * step;

            //Anything this close to the middle is the middle. Without it the stick creeps towards
            //zero for ever and the game keeps reading a nudge that nobody is making.
            if (Math.Abs(_x) < 0.001) { _x = 0; }
            if (Math.Abs(_y) < 0.001) { _y = 0; }

            return (_x, _y);
        }

        public void centre()
        {
            _x = 0;
            _y = 0;
        }
    }
}
