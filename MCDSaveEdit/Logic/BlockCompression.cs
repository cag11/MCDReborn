using System;
#nullable enable

namespace MCDSaveEdit.Logic
{
    /// <summary>
    /// Packing pixels the way the GPU keeps them.
    ///
    /// Most of the game's artwork is stored uncompressed, which is what lets a replacement be
    /// dropped in byte for byte. The big pictures are not: loading screens are PF_DXT1, where
    /// every 4x4 patch of pixels is squeezed into eight bytes - two colours at the ends of a line
    /// through the patch, and two bits per pixel saying how far along that line each one sits.
    ///
    /// That fixed eight bytes a block is what makes this worth doing at all. The replacement is
    /// exactly as long as the original, so the same swap-it-in-place machinery works and none of
    /// the careful checking around it has to change.
    ///
    /// The quality is the honest cost. Four colours per block is what the format allows, so a
    /// smooth gradient across one block comes back banded, and re-encoding a picture that was
    /// already encoded once loses a little more. For a loading screen behind a progress bar that
    /// is a fair trade against not being able to change it at all.
    /// </summary>
    public static class BlockCompression
    {
        /// <summary>How many bytes one BC1 image of this size takes.</summary>
        public static int dxt1Size(int width, int height)
            => Math.Max(1, (width + 3) / 4) * Math.Max(1, (height + 3) / 4) * 8;

        /// <summary>
        /// Squeezes BGRA pixels into BC1, the format Unreal calls PF_DXT1.
        /// </summary>
        public static byte[] toDxt1(byte[] bgra, int width, int height)
        {
            var across = Math.Max(1, (width + 3) / 4);
            var down = Math.Max(1, (height + 3) / 4);
            var made = new byte[across * down * 8];

            var block = new byte[16 * 4];

            for (var by = 0; by < down; by++)
            {
                for (var bx = 0; bx < across; bx++)
                {
                    gather(bgra, width, height, bx * 4, by * 4, block);
                    encode(block, made, (by * across + bx) * 8);
                }
            }

            return made;
        }

        /// <summary>
        /// BGRA into BC3, which Unreal calls PF_DXT5: the same colour block as BC1, after an alpha
        /// block of its own. Item icons are stored this way - their edges are see-through, and
        /// BC1 has one bit of alpha where they need a gradient.
        /// </summary>
        public static byte[] toDxt5(byte[] bgra, int width, int height)
        {
            var across = Math.Max(1, (width + 3) / 4);
            var down = Math.Max(1, (height + 3) / 4);
            var made = new byte[across * down * 16];

            var block = new byte[16 * 4];

            for (var by = 0; by < down; by++)
            {
                for (var bx = 0; bx < across; bx++)
                {
                    gather(bgra, width, height, bx * 4, by * 4, block);
                    var at = (by * across + bx) * 16;
                    encodeAlpha(block, made, at);
                    //BC3's colour block is always read in four-colour mode, which is the mode
                    //the BC1 encoder already forces.
                    encode(block, made, at + 8);
                }
            }

            return made;
        }

        /// <summary>
        /// A block's alpha into eight bytes: the two extremes, then a three-bit choice per pixel
        /// among them and the six steps between.
        /// </summary>
        private static void encodeAlpha(byte[] block, byte[] into, int at)
        {
            int low = 255, high = 0;
            for (var i = 0; i < 16; i++)
            {
                int a = block[i * 4 + 3];
                if (a < low) { low = a; }
                if (a > high) { high = a; }
            }

            into[at + 0] = (byte)high;
            into[at + 1] = (byte)low;

            //high > low is the eight-value mode; equal means every pixel is index 0 anyway.
            var palette = new int[8];
            palette[0] = high;
            palette[1] = low;
            for (var step = 1; step <= 6; step++)
            {
                palette[1 + step] = ((7 - step) * high + step * low) / 7;
            }

            ulong indices = 0;
            for (var i = 0; i < 16; i++)
            {
                int a = block[i * 4 + 3];
                var best = 0;
                var bestGap = int.MaxValue;
                for (var choice = 0; choice < 8; choice++)
                {
                    var gap = Math.Abs(a - palette[choice]);
                    if (gap >= bestGap) { continue; }
                    bestGap = gap;
                    best = choice;
                }
                indices |= (ulong)best << (i * 3);
            }

            for (var b = 0; b < 6; b++) { into[at + 2 + b] = (byte)((indices >> (b * 8)) & 0xFF); }
        }

        /// <summary>
        /// The sixteen pixels of one block, in BGRA.
        ///
        /// An image whose width or height is not a multiple of four still has to fill the last
        /// block, so the edge pixel is repeated rather than left black - a black fringe on every
        /// right and bottom edge is very visible and entirely avoidable.
        /// </summary>
        private static void gather(byte[] bgra, int width, int height, int x0, int y0, byte[] block)
        {
            for (var y = 0; y < 4; y++)
            {
                var sy = Math.Min(y0 + y, height - 1);

                for (var x = 0; x < 4; x++)
                {
                    var sx = Math.Min(x0 + x, width - 1);
                    var from = (sy * width + sx) * 4;
                    var to = (y * 4 + x) * 4;

                    block[to + 0] = bgra[from + 0];
                    block[to + 1] = bgra[from + 1];
                    block[to + 2] = bgra[from + 2];
                    block[to + 3] = bgra[from + 3];
                }
            }
        }

        /// <summary>
        /// One 4x4 block into eight bytes.
        ///
        /// The two endpoints are the corners of the block's colour bounding box. There are better
        /// ways - fitting a line through the colours by least squares is the usual one - but the
        /// bounding box is within a whisker of it on photographic content and is a tenth of the
        /// code, which matters more here than the last fraction of a decibel.
        /// </summary>
        private static void encode(byte[] block, byte[] into, int at)
        {
            int lowR = 255, lowG = 255, lowB = 255;
            int highR = 0, highG = 0, highB = 0;

            for (var i = 0; i < 16; i++)
            {
                int b = block[i * 4 + 0], g = block[i * 4 + 1], r = block[i * 4 + 2];

                if (r < lowR) { lowR = r; }
                if (g < lowG) { lowG = g; }
                if (b < lowB) { lowB = b; }
                if (r > highR) { highR = r; }
                if (g > highG) { highG = g; }
                if (b > highB) { highB = b; }
            }

            var first = pack565(highR, highG, highB);
            var second = pack565(lowR, lowG, lowB);

            //The order of the two endpoints is the flag that says which mode the block is in.
            //First greater than second means four colours and no transparency, which is what an
            //opaque picture wants. Equal endpoints - a flat block - would read as the three
            //colour mode, so they are nudged apart.
            if (first < second)
            {
                (first, second) = (second, first);
            }
            else if (first == second && second > 0)
            {
                second--;
            }

            var palette = new int[4 * 3];
            unpack565(first, palette, 0);
            unpack565(second, palette, 3);

            for (var channel = 0; channel < 3; channel++)
            {
                palette[6 + channel] = (2 * palette[channel] + palette[3 + channel]) / 3;
                palette[9 + channel] = (palette[channel] + 2 * palette[3 + channel]) / 3;
            }

            uint indices = 0;

            for (var i = 0; i < 16; i++)
            {
                int b = block[i * 4 + 0], g = block[i * 4 + 1], r = block[i * 4 + 2];

                var best = 0;
                var bestGap = int.MaxValue;

                for (var choice = 0; choice < 4; choice++)
                {
                    var dr = r - palette[choice * 3 + 0];
                    var dg = g - palette[choice * 3 + 1];
                    var db = b - palette[choice * 3 + 2];

                    //Weighted the way eyes are: green carries most of the brightness, blue least.
                    var gap = dr * dr * 3 + dg * dg * 6 + db * db;
                    if (gap >= bestGap) { continue; }

                    bestGap = gap;
                    best = choice;
                }

                indices |= (uint)best << (i * 2);
            }

            into[at + 0] = (byte)(first & 0xFF);
            into[at + 1] = (byte)(first >> 8);
            into[at + 2] = (byte)(second & 0xFF);
            into[at + 3] = (byte)(second >> 8);
            into[at + 4] = (byte)(indices & 0xFF);
            into[at + 5] = (byte)((indices >> 8) & 0xFF);
            into[at + 6] = (byte)((indices >> 16) & 0xFF);
            into[at + 7] = (byte)((indices >> 24) & 0xFF);
        }

        /// <summary>
        /// Unpacks BC1 back to BGRA, which is only here to check the encoder.
        ///
        /// A size that matches proves nothing about a picture - an encoder that wrote zeros would
        /// pass that. Decoding what was just encoded and comparing it to what went in is the only
        /// way to say whether the artwork survived.
        /// </summary>
        public static byte[] fromDxt1(byte[] dxt1, int width, int height)
        {
            var across = Math.Max(1, (width + 3) / 4);
            var down = Math.Max(1, (height + 3) / 4);
            var made = new byte[width * height * 4];
            var palette = new int[4 * 3];

            for (var by = 0; by < down; by++)
            {
                for (var bx = 0; bx < across; bx++)
                {
                    var at = (by * across + bx) * 8;

                    var first = dxt1[at] | (dxt1[at + 1] << 8);
                    var second = dxt1[at + 2] | (dxt1[at + 3] << 8);

                    unpack565(first, palette, 0);
                    unpack565(second, palette, 3);

                    for (var channel = 0; channel < 3; channel++)
                    {
                        palette[6 + channel] = first > second
                            ? (2 * palette[channel] + palette[3 + channel]) / 3
                            : (palette[channel] + palette[3 + channel]) / 2;
                        palette[9 + channel] = first > second
                            ? (palette[channel] + 2 * palette[3 + channel]) / 3
                            : 0;
                    }

                    var indices = (uint)(dxt1[at + 4] | (dxt1[at + 5] << 8)
                        | (dxt1[at + 6] << 16) | (dxt1[at + 7] << 24));

                    for (var i = 0; i < 16; i++)
                    {
                        var x = bx * 4 + (i % 4);
                        var y = by * 4 + (i / 4);
                        if (x >= width || y >= height) { continue; }

                        var choice = (int)((indices >> (i * 2)) & 3);
                        var to = (y * width + x) * 4;

                        made[to + 0] = (byte)palette[choice * 3 + 2];
                        made[to + 1] = (byte)palette[choice * 3 + 1];
                        made[to + 2] = (byte)palette[choice * 3 + 0];
                        made[to + 3] = 255;
                    }
                }
            }

            return made;
        }

        private static int pack565(int r, int g, int b)
            => ((r >> 3) << 11) | ((g >> 2) << 5) | (b >> 3);

        /// <summary>
        /// Back out to eight bits per channel, the way hardware does it.
        ///
        /// The top bits are repeated into the bottom rather than the gap being zero filled, so
        /// white stays white - shifting alone turns 31 into 248 and a white block comes back grey.
        /// </summary>
        private static void unpack565(int packed, int[] into, int at)
        {
            var r = (packed >> 11) & 0x1F;
            var g = (packed >> 5) & 0x3F;
            var b = packed & 0x1F;

            into[at + 0] = (r << 3) | (r >> 2);
            into[at + 1] = (g << 2) | (g >> 4);
            into[at + 2] = (b << 3) | (b >> 2);
        }
    }
}
