using System;
using System.Linq;
using System.Windows.Media;
using System.Windows.Media.Imaging;
#nullable enable

namespace MCDSaveEdit.Logic
{
    /// <summary>
    /// The sheen that sweeps across an enchantment's icon, made to follow a new picture.
    ///
    /// Beside each enchantment's icon the game keeps T_&lt;Name&gt;Shine_Icon: three hand-painted
    /// masks packed into red, green and blue, which the icon material animates a highlight
    /// across. They have no fixed roles - Fire Aspect's red is the sword with a glow, green the
    /// flames, blue the outlines; Stunning's green is the glowing spiral and blue the solid shape -
    /// but all three follow the icon's own shapes and nothing outside them lights. A copy with a
    /// new picture keeps the source's masks, and shines along the old outline.
    ///
    /// So one is made from the picture, in the same spirit: blue all but its darkest fifth at half
    /// strength, the broad sheen; green its brighter half, the details; red its brightest fifth
    /// with a soft glow around it, the hot spot every one of the game's has somewhere.
    /// </summary>
    public static class EnchantmentShine
    {
        public const int SIZE = 256;

        /// <summary>Three masks from <paramref name="picture"/>, at the game's 256 square.</summary>
        public static BitmapSource from(BitmapSource picture)
        {
            var pixels = CustomSkins.pixelsAt(picture, SIZE, SIZE);
            var count = SIZE * SIZE;
            var alpha = new float[count];
            var light = new float[count];
            for (var i = 0; i < count; i++)
            {
                alpha[i] = pixels[i * 4 + 3] / 255f;
                //Perceived brightness, from BGRA.
                light[i] = (0.114f * pixels[i * 4] + 0.587f * pixels[i * 4 + 1] + 0.299f * pixels[i * 4 + 2]) / 255f;
            }

            //Brightness is ranked within the shape, so a dark picture still gets its brighter half
            //and its brightest parts, and a pale one is not lit all over.
            var inside = Enumerable.Range(0, count).Where(i => alpha[i] > 0.5f).Select(i => light[i]).OrderBy(v => v).ToArray();
            float rank(double at) => inside.Length == 0 ? 1f : inside[Math.Min(inside.Length - 1, (int)(inside.Length * at))];
            //By rank, ties included: a flat picture is mostly one or two values, and a band between
            //two ranks of the same value would light nothing.
            var dark = rank(0.2);
            var half = rank(0.5);
            var brightest = rank(0.8);
            var peak = rank(0.98);
            bool atLeast(float value, float threshold) => value >= threshold - 0.0001f;

            var red = new float[count];
            var green = new float[count];
            var blue = new float[count];
            for (var i = 0; i < count; i++)
            {
                if (alpha[i] <= 0.5f) { continue; }
                //The darkest fifth is usually the background the game's masks leave dark.
                if (atLeast(light[i], dark)) { blue[i] = 0.5f; }
                if (atLeast(light[i], half)) { green[i] = 0.6f + 0.4f * ramp(light[i], half, peak); }
                if (atLeast(light[i], brightest)) { red[i] = 1f; }
            }

            //The glow: the hot spot blurred, laid under its sharp self, allowed a little past the
            //shape's edge as the game's own glows are.
            var glow = blur(blur(red, 6), 6);
            for (var i = 0; i < count; i++) { red[i] = Math.Min(1f, Math.Max(red[i], glow[i] * 1.6f)); }

            var made = new byte[count * 4];
            for (var i = 0; i < count; i++)
            {
                made[i * 4] = (byte)(blue[i] * 255);
                made[i * 4 + 1] = (byte)(green[i] * 255);
                made[i * 4 + 2] = (byte)(red[i] * 255);
                made[i * 4 + 3] = 255;
            }
            return picture_(made);
        }

        /// <summary>No sheen at all: every mask empty.</summary>
        public static BitmapSource none()
        {
            var made = new byte[SIZE * SIZE * 4];
            for (var i = 3; i < made.Length; i += 4) { made[i] = 255; }
            return picture_(made);
        }

        private static BitmapSource picture_(byte[] bgra)
        {
            var image = BitmapSource.Create(SIZE, SIZE, 96, 96, PixelFormats.Bgra32, null, bgra, SIZE * 4);
            image.Freeze();
            return image;
        }

        private static float ramp(float value, float from, float to)
            => value <= from ? 0f : value >= to ? 1f : (value - from) / (to - from);

        /// <summary>A box blur, one pass across and one down.</summary>
        private static float[] blur(float[] from, int radius)
        {
            var across = new float[from.Length];
            var down = new float[from.Length];
            var span = radius * 2 + 1;
            for (var y = 0; y < SIZE; y++)
            {
                for (var x = 0; x < SIZE; x++)
                {
                    float sum = 0;
                    for (var k = -radius; k <= radius; k++) { sum += from[y * SIZE + Math.Clamp(x + k, 0, SIZE - 1)]; }
                    across[y * SIZE + x] = sum / span;
                }
            }
            for (var y = 0; y < SIZE; y++)
            {
                for (var x = 0; x < SIZE; x++)
                {
                    float sum = 0;
                    for (var k = -radius; k <= radius; k++) { sum += across[Math.Clamp(y + k, 0, SIZE - 1) * SIZE + x]; }
                    down[y * SIZE + x] = sum / span;
                }
            }
            return down;
        }
    }
}
