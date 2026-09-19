using NLayer;
using OggVorbisEncoder;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
#nullable enable

namespace MCDSaveEdit.Logic
{
    /// <summary>
    /// Turning somebody's audio file into what the game keeps inside a sound wave.
    ///
    /// Which is Ogg Vorbis, stereo, and nothing else - proven by reading the assets rather than
    /// assumed: every music asset's name table carries the literal "OGG", the payload is raw OggS
    /// pages, and the identification header reads "Xiph.Org libVorbis ... ENCODER=UnrealEngine4".
    ///
    /// Both libraries here are managed C#. That is deliberate and it is not a preference: this app
    /// publishes as a single self-contained file, and a native encoder would have to be unpacked
    /// beside it at runtime. The last time a native dependency went unnoticed the exe hung on
    /// "Loading UI images" with no error at all, because SkiaSharp's native half never made it into
    /// the bundle.
    /// </summary>
    public static class MusicEncode
    {
        /// <summary>What the game encodes at. Anything else is resampled to it.</summary>
        public const int SAMPLE_RATE = 44100;

        /// <summary>Every music track in the game is stereo, so a mono file is doubled up.</summary>
        public const int CHANNELS = 2;

        /// <summary>Roughly what the game's own encoder used - "nominal bitrate 118400".</summary>
        private const float QUALITY = 0.4f;

        public sealed class Encoded
        {
            public Encoded(byte[] ogg, int sampleRate, int channels, long samples)
            {
                Ogg = ogg;
                SampleRate = sampleRate;
                Channels = channels;
                Samples = samples;
            }

            public byte[] Ogg { get; }
            public int SampleRate { get; }
            public int Channels { get; }

            /// <summary>Samples per channel, which is what the game stores as TotalSamples.</summary>
            public long Samples { get; }

            public float Duration => (float)Samples / SampleRate;
        }

        /// <summary>
        /// Reads an audio file and returns it as Ogg Vorbis the game will accept.
        ///
        /// An .ogg is passed through untouched. Re-encoding one would only lose quality, and the
        /// whole point of the format check is that this is already what the game wants.
        /// </summary>
        public static Encoded read(string file)
        {
            var extension = System.IO.Path.GetExtension(file).ToLowerInvariant();

            if (extension == ".ogg")
            {
                var raw = File.ReadAllBytes(file);
                var (rate, channels, samples) = describeOgg(raw);
                return new Encoded(raw, rate, channels, samples);
            }

            var (pcm, sourceRate, sourceChannels) = extension switch
            {
                ".mp3" => readMp3(file),
                ".wav" => readWav(file),
                _ => throw new InvalidOperationException(
                    $"{extension} is not something this can read - use .mp3, .wav or .ogg."),
            };

            return encode(pcm, sourceRate, sourceChannels);
        }

        //--- reading ---------------------------------------------------------------------------

        private static (float[] pcm, int rate, int channels) readMp3(string file)
        {
            using var reader = new MpegFile(file);

            var rate = reader.SampleRate;
            var channels = reader.Channels;

            //Read in blocks rather than asking for the whole thing: a long track is tens of
            //millions of samples and MpegFile does not know the count until it has decoded them.
            var all = new List<float>(1 << 20);
            var block = new float[1 << 16];

            while (true)
            {
                var got = reader.ReadSamples(block, 0, block.Length);
                if (got <= 0) { break; }
                for (var i = 0; i < got; i++) { all.Add(block[i]); }
            }

            if (all.Count == 0)
            {
                throw new InvalidOperationException(
                    $"{System.IO.Path.GetFileName(file)} decoded to no audio at all.");
            }

            return (all.ToArray(), rate, channels);
        }

        /// <summary>
        /// A WAV file, read far enough to find its samples.
        ///
        /// Written out rather than taken from a library because a RIFF header is twenty lines and
        /// the alternative was a second dependency for the least interesting of the three formats.
        /// Only uncompressed PCM is handled - 8, 16, 24 and 32 bit integer, and 32 bit float.
        /// </summary>
        private static (float[] pcm, int rate, int channels) readWav(string file)
        {
            var raw = File.ReadAllBytes(file);

            if (raw.Length < 12
                || raw[0] != 'R' || raw[1] != 'I' || raw[2] != 'F' || raw[3] != 'F'
                || raw[8] != 'W' || raw[9] != 'A' || raw[10] != 'V' || raw[11] != 'E')
            {
                throw new InvalidOperationException("That is not a WAV file.");
            }

            var at = 12;
            int rate = 0, channels = 0, bits = 0, format = 1;
            byte[]? data = null;

            while (at + 8 <= raw.Length)
            {
                var id = System.Text.Encoding.ASCII.GetString(raw, at, 4);
                var size = BitConverter.ToInt32(raw, at + 4);
                var body = at + 8;

                if (id == "fmt " && body + 16 <= raw.Length)
                {
                    format = BitConverter.ToInt16(raw, body);
                    channels = BitConverter.ToInt16(raw, body + 2);
                    rate = BitConverter.ToInt32(raw, body + 4);
                    bits = BitConverter.ToInt16(raw, body + 14);
                }
                else if (id == "data")
                {
                    var length = Math.Min(size, raw.Length - body);
                    data = new byte[length];
                    Array.Copy(raw, body, data, 0, length);
                }

                //Chunks are word aligned, and a file whose size field lies would otherwise loop.
                at = body + size + (size & 1);
                if (size <= 0) { break; }
            }

            if (data == null || rate == 0 || channels == 0)
            {
                throw new InvalidOperationException("That WAV file has no readable audio in it.");
            }

            var pcm = toFloats(data, bits, format);
            return (pcm, rate, channels);
        }

        private static float[] toFloats(byte[] data, int bits, int format)
        {
            if (format == 3 && bits == 32)
            {
                var floats = new float[data.Length / 4];
                Buffer.BlockCopy(data, 0, floats, 0, floats.Length * 4);
                return floats;
            }

            switch (bits)
            {
                case 8:
                    return data.Select(b => (b - 128) / 128f).ToArray();

                case 16:
                {
                    var made = new float[data.Length / 2];
                    for (var i = 0; i < made.Length; i++)
                    {
                        made[i] = BitConverter.ToInt16(data, i * 2) / 32768f;
                    }
                    return made;
                }

                case 24:
                {
                    var made = new float[data.Length / 3];
                    for (var i = 0; i < made.Length; i++)
                    {
                        var at = i * 3;
                        var value = data[at] | (data[at + 1] << 8) | ((sbyte)data[at + 2] << 16);
                        made[i] = value / 8388608f;
                    }
                    return made;
                }

                case 32:
                {
                    var made = new float[data.Length / 4];
                    for (var i = 0; i < made.Length; i++)
                    {
                        made[i] = BitConverter.ToInt32(data, i * 4) / 2147483648f;
                    }
                    return made;
                }

                default:
                    throw new InvalidOperationException($"{bits} bit WAV audio cannot be read yet.");
            }
        }

        //--- writing ---------------------------------------------------------------------------

        /// <summary>
        /// Interleaved samples in, Ogg Vorbis out, at the rate and channel count the game uses.
        /// </summary>
        private static Encoded encode(float[] pcm, int rate, int channels)
        {
            var planar = toStereoPlanar(pcm, channels);

            if (rate != SAMPLE_RATE)
            {
                planar = resample(planar, rate, SAMPLE_RATE);
            }

            using var stream = new MemoryStream();
            writeOgg(stream, planar);

            return new Encoded(stream.ToArray(), SAMPLE_RATE, CHANNELS, planar[0].Length);
        }

        private static void writeOgg(MemoryStream into, float[][] planar)
        {
            var info = VorbisInfo.InitVariableBitRate(CHANNELS, SAMPLE_RATE, QUALITY);

            //The serial number identifies the logical bitstream. Any value works for a single
            //stream; it only matters when several are multiplexed, which never happens here.
            var stream = new OggStream(new Random().Next());

            var comments = new Comments();
            comments.AddTag("ENCODER", "MCD Reborn");

            //HeaderPacketBuilder is static in this version of the library.
            stream.PacketIn(HeaderPacketBuilder.BuildInfoPacket(info));
            stream.PacketIn(HeaderPacketBuilder.BuildCommentsPacket(comments));
            stream.PacketIn(HeaderPacketBuilder.BuildBooksPacket(info));

            //The three headers have to finish their own pages before any audio starts, which is
            //what the "true" means - flush rather than fill.
            while (stream.PageOut(out var header, true)) { write(into, header); }

            var encoder = ProcessingState.Create(info);
            const int BLOCK = 1024;

            //WriteData takes an offset into the arrays, so the whole track is handed over in
            //place rather than sliced into a new pair of arrays for every block.
            for (var at = 0; at < planar[0].Length; at += BLOCK)
            {
                var count = Math.Min(BLOCK, planar[0].Length - at);
                encoder.WriteData(planar, count, at);
                drain(encoder, stream, into);
            }

            encoder.WriteEndOfStream();
            drain(encoder, stream, into);

            while (stream.PageOut(out var last, true)) { write(into, last); }
        }

        private static void drain(ProcessingState encoder, OggStream stream, MemoryStream into)
        {
            while (encoder.PacketOut(out var packet))
            {
                stream.PacketIn(packet);
                while (stream.PageOut(out var page, false)) { write(into, page); }
            }
        }

        private static void write(MemoryStream into, OggPage page)
        {
            into.Write(page.Header, 0, page.Header.Length);
            into.Write(page.Body, 0, page.Body.Length);
        }

        //--- shaping ---------------------------------------------------------------------------

        /// <summary>
        /// Interleaved samples of any channel count, as two separate channels.
        ///
        /// Mono is copied to both sides rather than left silent on one, and anything with more than
        /// two channels keeps the first two - there is no music in this game that is not stereo, so
        /// a proper downmix would be care spent on a case that does not arise.
        /// </summary>
        private static float[][] toStereoPlanar(float[] pcm, int channels)
        {
            if (channels < 1) { throw new InvalidOperationException("That file claims no channels."); }

            var frames = pcm.Length / channels;
            var left = new float[frames];
            var right = new float[frames];

            for (var i = 0; i < frames; i++)
            {
                left[i] = pcm[i * channels];
                right[i] = channels > 1 ? pcm[i * channels + 1] : left[i];
            }

            return new[] { left, right };
        }

        /// <summary>
        /// Linear resampling to the rate the game uses.
        ///
        /// Linear rather than windowed, and that is a real compromise: it will add a little
        /// high-frequency noise to a 48 kHz source. It is chosen because the alternative is a
        /// signal-processing dependency for a case most people will not hit - an MP3 is usually
        /// 44100 already and is then copied through untouched.
        /// </summary>
        private static float[][] resample(float[][] planar, int from, int to)
        {
            var ratio = (double)to / from;
            var frames = (int)(planar[0].Length * ratio);
            var made = new float[planar.Length][];

            for (var c = 0; c < planar.Length; c++)
            {
                var source = planar[c];
                var target = new float[frames];

                for (var i = 0; i < frames; i++)
                {
                    var at = i / ratio;
                    var low = (int)at;
                    var high = Math.Min(low + 1, source.Length - 1);
                    var mix = (float)(at - low);

                    target[i] = source[low] * (1 - mix) + source[high] * mix;
                }

                made[c] = target;
            }

            return made;
        }

        //--- reading back an ogg ----------------------------------------------------------------

        /// <summary>
        /// What an Ogg Vorbis file says about itself: rate, channels, and how many samples.
        ///
        /// Read by hand rather than decoded. The identification header is at a known place in the
        /// first page, and the sample count is the granule position of the last page - which is
        /// exactly what the game stores as TotalSamples, and the reason it can be had without
        /// decoding a single frame.
        /// </summary>
        public static (int rate, int channels, long samples) describeOgg(byte[] ogg)
        {
            if (ogg.Length < 58 || ogg[0] != 'O' || ogg[1] != 'g' || ogg[2] != 'g' || ogg[3] != 'S')
            {
                throw new InvalidOperationException("That is not an Ogg file.");
            }

            //The vorbis identification header follows the first page's segment table. Its layout is
            //fixed: packet type 1, "vorbis", version, channels, then the rate.
            var at = Array.IndexOf(ogg, (byte)'v', 0, Math.Min(ogg.Length, 128));
            while (at > 0 && at + 11 < ogg.Length)
            {
                if (ogg[at] == 'v' && ogg[at + 1] == 'o' && ogg[at + 2] == 'r'
                    && ogg[at + 3] == 'b' && ogg[at + 4] == 'i' && ogg[at + 5] == 's'
                    && ogg[at - 1] == 1)
                {
                    var channels = ogg[at + 10];
                    var rate = BitConverter.ToInt32(ogg, at + 11);
                    return (rate, channels, lastGranule(ogg));
                }

                at = Array.IndexOf(ogg, (byte)'v', at + 1, Math.Min(128, ogg.Length - at - 1));
                if (at < 0) { break; }
            }

            throw new InvalidOperationException("That Ogg file has no Vorbis audio in it.");
        }

        /// <summary>The granule position of the last page, which is the sample count.</summary>
        private static long lastGranule(byte[] ogg)
        {
            for (var at = ogg.Length - 27; at >= 0; at--)
            {
                if (ogg[at] == 'O' && ogg[at + 1] == 'g' && ogg[at + 2] == 'g' && ogg[at + 3] == 'S')
                {
                    return BitConverter.ToInt64(ogg, at + 6);
                }
            }

            throw new InvalidOperationException("That Ogg file has no readable pages.");
        }
    }
}
