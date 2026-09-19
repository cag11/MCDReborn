using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Windows.Media;
using System.Windows.Media.Imaging;
#nullable enable

namespace MCDSaveEdit.Logic
{
    /// <summary>
    /// A texture packed small enough to travel in a URL.
    ///
    /// The point is to delete a step. Exporting a PNG, finding it in a folder, uploading it to a
    /// website and then doing the same in reverse is four chances to pick the wrong file; a link
    /// is one click. That only works if a 64x64 armour fits in a URL, so the format is chosen to
    /// make it fit rather than to be a general image format.
    ///
    /// What makes it small is what these textures are: a 64x64 armour holds 27 distinct colours
    /// across 4096 pixels. Stored as a palette plus one index per pixel and deflated, the real
    /// Wolf Armour is 1051 characters and the Amethyst Cape 398 - against 2647 for its PNG in
    /// base64. Both are comfortably inside the ~2000 characters that is safe everywhere.
    ///
    /// It is lossless, and that is not negotiable. Two things ruled out the obvious alternatives:
    /// the pixels go back into the game as a fixed number of bytes, so resizing is not an option
    /// at any quality; and lossy compression turns those 27 colours into 737, which destroys the
    /// material families the whole recolouring tool is built on. Measured, not assumed - and
    /// lossy WebP came out larger here anyway, because there is no noise to throw away.
    ///
    /// The layout, all little-endian, deflated raw and then base64url:
    ///
    ///   0      version (2)
    ///   1..2   width
    ///   3..4   height
    ///   5..6   palette length N
    ///   7..    N colours, BGRA, one byte each
    ///   ..     width*height indices into the palette, one byte each, or two when N is over 256
    ///
    /// Colours are BGRA because that is how the game stores them and how WPF hands them over,
    /// so neither end of the trip has to swap channels.
    /// </summary>
    public static class SkinCodec
    {
        //Version 1 said width and height in one byte each, which put a ceiling of 255 on both and
        //made an enchantment icon - 256 square, by one pixel too wide - the one thing that could
        //not travel. Two bytes each now, and version 1 is still read, so a link made before this
        //still opens.
        private const byte VERSION = 2;

        //Where the pixels start, per version.
        private const int HEADER = 7;
        private const int HEADER_V1 = 5;

        /// <summary>
        /// Above this a link starts being refused or truncated by something in the chain. No
        /// texture seen so far comes close, but a hand-made one could, and silently producing a
        /// link that fails later is worse than saying so now.
        /// </summary>
        public const int MAX_URL_PAYLOAD = 8000;

        public static string encode(BitmapSource image)
        {
            var width = image.PixelWidth;
            var height = image.PixelHeight;
            if (width > ushort.MaxValue || height > ushort.MaxValue)
            {
                throw new InvalidOperationException($"{width}x{height} is too large to put in a link.");
            }

            var bgra = toBgra(image, width, height);

            //One entry per distinct colour, in first-seen order. Sorting would compress slightly
            //better but costs the ability to compare two encodings of the same texture.
            var lookup = new Dictionary<uint, int>();
            var palette = new List<uint>();
            var indices = new int[width * height];
            for (int i = 0; i < indices.Length; i++)
            {
                var colour = BitConverter.ToUInt32(bgra, i * 4);
                if (!lookup.TryGetValue(colour, out var index))
                {
                    index = palette.Count;
                    lookup[colour] = index;
                    palette.Add(colour);
                }
                indices[i] = index;
            }

            var wide = palette.Count > 256;
            var raw = new byte[HEADER + palette.Count * 4 + indices.Length * (wide ? 2 : 1)];
            raw[0] = VERSION;
            raw[1] = (byte)width;
            raw[2] = (byte)(width >> 8);
            raw[3] = (byte)height;
            raw[4] = (byte)(height >> 8);
            raw[5] = (byte)(palette.Count & 0xFF);
            raw[6] = (byte)(palette.Count >> 8);

            int at = HEADER;
            foreach (var colour in palette)
            {
                raw[at++] = (byte)colour;
                raw[at++] = (byte)(colour >> 8);
                raw[at++] = (byte)(colour >> 16);
                raw[at++] = (byte)(colour >> 24);
            }
            foreach (var index in indices)
            {
                raw[at++] = (byte)index;
                if (wide) { raw[at++] = (byte)(index >> 8); }
            }

            var payload = toBase64Url(deflate(raw));
            if (payload.Length > MAX_URL_PAYLOAD)
            {
                throw new InvalidOperationException(
                    $"That texture packs to {payload.Length} characters, which is too long for a link.");
            }
            return payload;
        }

        public static BitmapSource decode(string payload)
        {
            var raw = inflate(fromBase64Url(payload));
            if (raw.Length < HEADER_V1 || (raw[0] != VERSION && raw[0] != 1))
            {
                throw new InvalidOperationException("That is not a texture this version understands.");
            }

            //A link made before the size was widened still opens, because the only thing that
            //moved is how many bytes the two numbers take.
            var older = raw[0] == 1;
            var header = older ? HEADER_V1 : HEADER;
            int width = older ? raw[1] : raw[1] | (raw[2] << 8);
            int height = older ? raw[2] : raw[3] | (raw[4] << 8);
            int count = older ? raw[3] | (raw[4] << 8) : raw[5] | (raw[6] << 8);
            var wide = count > 256;
            var expected = header + count * 4 + width * height * (wide ? 2 : 1);
            if (width == 0 || height == 0 || count == 0 || raw.Length != expected)
            {
                throw new InvalidOperationException("That texture data is incomplete.");
            }

            var bgra = new byte[width * height * 4];
            int body = header + count * 4;
            for (int i = 0; i < width * height; i++)
            {
                int index = wide
                    ? raw[body + i * 2] | (raw[body + i * 2 + 1] << 8)
                    : raw[body + i];
                if (index >= count) { throw new InvalidOperationException("That texture data is damaged."); }
                Buffer.BlockCopy(raw, header + index * 4, bgra, i * 4, 4);
            }

            var bitmap = BitmapSource.Create(width, height, 96, 96, PixelFormats.Bgra32, null, bgra, width * 4);
            bitmap.Freeze();
            return bitmap;
        }

        /// <summary>The whole link, ready to be opened.</summary>
        public static string designerUrl(BitmapSource image, string? name)
        {
            var url = CustomSkins.DESIGNER_URL + "#t=" + encode(image);
            if (!string.IsNullOrWhiteSpace(name)) { url += "&n=" + Uri.EscapeDataString(name!); }
            return url;
        }

        /// <summary>
        /// Pulls the texture out of whatever the user pasted - the whole link, or just the
        /// payload on its own. Accepting both means never having to explain which part to copy.
        /// </summary>
        public static BitmapSource fromPastedText(string text)
        {
            var trimmed = (text ?? string.Empty).Trim();
            if (trimmed.Length == 0) { throw new InvalidOperationException("Nothing to paste."); }

            var marker = trimmed.IndexOf("t=", StringComparison.Ordinal);
            if (marker >= 0) { trimmed = trimmed.Substring(marker + 2); }
            var end = trimmed.IndexOf('&');
            if (end >= 0) { trimmed = trimmed.Substring(0, end); }

            return decode(trimmed);
        }

        private static byte[] toBgra(BitmapSource image, int width, int height)
        {
            BitmapSource source = image.Format == PixelFormats.Bgra32
                ? image
                : new FormatConvertedBitmap(image, PixelFormats.Bgra32, null, 0);
            var bytes = new byte[width * height * 4];
            source.CopyPixels(bytes, width * 4, 0);
            return bytes;
        }

        private static byte[] deflate(byte[] raw)
        {
            using var output = new MemoryStream();
            using (var stream = new DeflateStream(output, CompressionLevel.Optimal, true))
            {
                stream.Write(raw, 0, raw.Length);
            }
            return output.ToArray();
        }

        private static byte[] inflate(byte[] compressed)
        {
            using var input = new MemoryStream(compressed);
            using var stream = new DeflateStream(input, CompressionMode.Decompress);
            using var output = new MemoryStream();
            stream.CopyTo(output);
            return output.ToArray();
        }

        //base64url with the padding dropped: '+' and '/' are not safe unescaped in a URL, and
        //'=' is routinely mangled by anything that treats the link as a query string.
        private static string toBase64Url(byte[] bytes)
            => Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

        private static byte[] fromBase64Url(string text)
        {
            var padded = text.Replace('-', '+').Replace('_', '/');
            switch (padded.Length % 4)
            {
                case 2: padded += "=="; break;
                case 3: padded += "="; break;
                case 1: throw new InvalidOperationException("That link is incomplete.");
            }
            return Convert.FromBase64String(padded);
        }
    }
}
