using System;
#nullable enable

namespace MCDSaveEdit.Logic
{
    /// <summary>
    /// The small number formats a cooked mesh stores its vertices in.
    ///
    /// Positions are ordinary floats, but nothing else is. A normal is squeezed into four bytes, a
    /// texture coordinate into a sixteen bit float, and both are lossy on the way in. That matters
    /// for a reason that is easy to miss: it means a mesh cannot be checked by reading it back and
    /// comparing to what was supplied. It can only be checked by reading the game's own bytes,
    /// unpacking them, packing them again, and requiring the result to be identical - which is
    /// what proves the conversion matches the engine's rather than merely resembling it.
    /// </summary>
    public static class VertexPacking
    {
        /// <summary>A direction, as it exists before or after being squeezed into four bytes.</summary>
        public readonly struct Direction
        {
            public Direction(float x, float y, float z, float w)
            {
                X = x; Y = y; Z = z; W = w;
            }

            public float X { get; }
            public float Y { get; }
            public float Z { get; }
            /// <summary>Which way the bitangent goes. Only ever +1 or -1 in practice.</summary>
            public float W { get; }

            public override string ToString() => $"({X:0.###}, {Y:0.###}, {Z:0.###}, w {W:+0;-0})";
        }

        #region FPackedNormal

        /// <summary>
        /// Four bytes back into a direction.
        ///
        /// Each component is a byte with 128 meaning zero, so the range is a little lopsided:
        /// 127 steps one way and 128 the other. The engine divides by 127.5 and subtracts one,
        /// and getting that constant wrong is the kind of error that leaves lighting subtly
        /// crooked rather than obviously broken.
        /// </summary>
        public static Direction unpackNormal(byte[] source, int at)
        {
            return new Direction(
                fromByte(source[at]),
                fromByte(source[at + 1]),
                fromByte(source[at + 2]),
                fromByte(source[at + 3]));
        }

        public static void packNormal(Direction direction, byte[] destination, int at)
        {
            destination[at] = toByte(direction.X);
            destination[at + 1] = toByte(direction.Y);
            destination[at + 2] = toByte(direction.Z);
            destination[at + 3] = toByte(direction.W);
        }

        private static float fromByte(byte value) => value / 127.5f - 1.0f;

        private static byte toByte(float value)
        {
            //Rounded rather than truncated, and clamped, because a normal that arrives a hair
            //outside the unit range - which floating point arithmetic routinely produces - would
            //otherwise wrap around to point the opposite way.
            var scaled = (value + 1.0f) * 127.5f;
            var rounded = (int)Math.Round(scaled, MidpointRounding.AwayFromZero);
            if (rounded < 0) { rounded = 0; }
            if (rounded > 255) { rounded = 255; }
            return (byte)rounded;
        }

        #endregion

        #region FVector2DHalf

        /// <summary>
        /// A sixteen bit float, which is what a texture coordinate is stored as.
        ///
        /// Written out by hand rather than handed to the runtime's Half, because the two disagree
        /// about what to do with values that cannot be represented, and a UV that lands one bit
        /// away from the game's own is a seam in the texture.
        /// </summary>
        public static float unpackHalf(ushort bits)
        {
            var sign = (bits >> 15) & 0x1;
            var exponent = (bits >> 10) & 0x1F;
            var mantissa = bits & 0x3FF;

            int single;
            if (exponent == 0)
            {
                if (mantissa == 0)
                {
                    //Signed zero, which is worth keeping: the sign bit survives a round trip.
                    single = sign << 31;
                }
                else
                {
                    //Subnormal. Normalise it by shifting until the implied bit appears.
                    exponent = 127 - 15 + 1;
                    while ((mantissa & 0x400) == 0)
                    {
                        mantissa <<= 1;
                        exponent--;
                    }
                    mantissa &= 0x3FF;
                    single = (sign << 31) | (exponent << 23) | (mantissa << 13);
                }
            }
            else if (exponent == 0x1F)
            {
                //Infinity or not-a-number, carried across rather than collapsed.
                single = (sign << 31) | (0xFF << 23) | (mantissa << 13);
            }
            else
            {
                single = (sign << 31) | ((exponent - 15 + 127) << 23) | (mantissa << 13);
            }

            return BitConverter.ToSingle(BitConverter.GetBytes(single), 0);
        }

        public static ushort packHalf(float value)
        {
            var bits = BitConverter.ToInt32(BitConverter.GetBytes(value), 0);
            var sign = (bits >> 16) & 0x8000;
            var exponent = ((bits >> 23) & 0xFF) - 127 + 15;
            var mantissa = bits & 0x7FFFFF;

            if (((bits >> 23) & 0xFF) == 0xFF)
            {
                //Infinity and not-a-number keep their shape.
                return (ushort)(sign | 0x7C00 | (mantissa != 0 ? 0x200 : 0));
            }

            if (exponent >= 0x1F)
            {
                //Too large to represent, so it saturates to infinity rather than wrapping.
                return (ushort)(sign | 0x7C00);
            }

            if (exponent <= 0)
            {
                if (exponent < -10) { return (ushort)sign; }

                //Subnormal. Put the implied bit back and shift it down into range.
                mantissa |= 0x800000;
                var shift = 14 - exponent;
                var subnormal = mantissa >> shift;

                //Round to nearest, ties away from zero, matching the engine's own conversion.
                if (((mantissa >> (shift - 1)) & 1) != 0) { subnormal++; }
                return (ushort)(sign | subnormal);
            }

            var rounded = (exponent << 10) | (mantissa >> 13);
            if ((mantissa & 0x1000) != 0)
            {
                //Carrying into the exponent is handled for free: adding one to the packed value
                //overflows the mantissa into the exponent exactly as it should.
                rounded++;
            }
            return (ushort)(sign | rounded);
        }

        #endregion
    }
}
