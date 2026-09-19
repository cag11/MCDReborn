using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
#nullable enable

namespace MCDSaveEdit.Logic
{
    /// <summary>
    /// Rebuilding a cooked USoundWave around different audio.
    ///
    /// The format was read out of the game's own assets rather than guessed, and the model was
    /// checked by rebuilding eight tracks byte-for-byte from their own extracted audio before any
    /// of this was written. What follows is that layout, for a streamed sound wave:
    ///
    ///     tagged properties ... FName "None"
    ///     int32  0                      the object-guid flag
    ///     int32  1                      bCooked
    ///     FGuid                         CompressedDataGuid - a derived-data cache key, never
    ///                                   checked at runtime, so it is carried over untouched
    ///     int32  NumChunks
    ///     FName  "OGG"                  the audio format, already in every music asset's names
    ///     chunk 0: 24 byte bulk header, then CHUNK bytes of payload, then DataSize, AudioDataSize
    ///     chunk n: 24 byte bulk header, then DataSize, AudioDataSize - payload is in the .ubulk
    ///
    /// Three numbers move together when the length changes, and missing the third is the one that
    /// corrupts audio silently:
    ///
    ///   1. the export's SerialSize
    ///   2. Summary.BulkDataStartOffset, at a fixed offset of 169 in the .uasset
    ///   3. every non-inline chunk's offset, because those are stored RELATIVE to (2) - so when
    ///      BulkDataStartOffset moves by d, each stored offset must move by -d
    ///
    /// CookedPackage.correctHeader already does the first two. The third is done here.
    /// </summary>
    public static class MusicSoundWave
    {
        /// <summary>The cooker's chunk size. Every chunk is padded to it, including the last.</summary>
        public const int CHUNK = 262144;

        /// <summary>Inline payload - the chunk lives in the .uexp.</summary>
        private const int FLAG_INLINE = 0x40;

        /// <summary>At end of file, in a separate file, not inline - the chunk lives in the .ubulk.</summary>
        private const int FLAG_STREAMED = 0x501;

        private const int BULK_DATA_START_OFFSET = 169;

        public sealed class Rebuilt
        {
            public Rebuilt(byte[] uasset, byte[] uexp, byte[]? ubulk, int chunks,
                IReadOnlyList<string> notes)
            {
                UAsset = uasset;
                UExp = uexp;
                UBulk = ubulk;
                Chunks = chunks;
                Notes = notes;
            }

            /// <summary>The geometry it worked out, for when the result will not read back.</summary>
            public IReadOnlyList<string> Notes { get; }

            public byte[] UAsset { get; }
            public byte[] UExp { get; }

            /// <summary>Nothing when the audio fits in one chunk - then there is no .ubulk at all.</summary>
            public byte[]? UBulk { get; }

            public int Chunks { get; }
        }

        /// <summary>
        /// Puts new Ogg Vorbis audio into a sound wave, and returns the files to ship.
        /// </summary>
        public static Rebuilt rebuild(byte[] uasset, byte[] uexp, MusicEncode.Encoded audio)
        {
            var names = CookedProperties.readNamesOf(uasset);
            var oggName = names.ToList().IndexOf("OGG");
            if (oggName < 0)
            {
                throw new InvalidOperationException(
                    "That asset does not mention OGG, so it is not a sound wave this can rewrite.");
            }

            var values = CookedProperties.readAll(uasset, uexp);
            if (values.Count == 0)
            {
                throw new InvalidOperationException("That asset has no readable properties.");
            }

            var propsEnd = endOfProperties(uexp, values, names);

            //--- the properties, patched where they sit ------------------------------------------
            //
            //Every field being written is fixed width, so the block never changes length and the
            //rest of the file is unaffected. Which fields exist varies between tracks - TotalSamples
            //is simply missing on several - so each is written only if it is there, and the tags are
            //walked rather than trusted to be at a fixed offset. They are not: Duration sits at
            //0x103 in one track and 0xE9 in another.
            var head = new byte[propsEnd];
            Array.Copy(uexp, head, propsEnd);

            writeInt(head, values, "SampleRate", audio.SampleRate);
            writeInt(head, values, "NumChannels", audio.Channels);
            writeFloat(head, values, "Duration", audio.Duration);
            writeFloat(head, values, "TotalSamples", audio.Samples);

            //--- the fixed 24 bytes that follow, carried over verbatim ---------------------------
            if (propsEnd + 24 > uexp.Length)
            {
                throw new InvalidOperationException("That sound wave ends sooner than it should.");
            }

            var carried = new byte[24];
            Array.Copy(uexp, propsEnd, carried, 0, 24);

            //--- geometry -------------------------------------------------------------------------
            var ogg = audio.Ogg;
            var chunks = Math.Max(1, (ogg.Length + CHUNK - 1) / CHUNK);

            var sizes = new int[chunks];
            for (var i = 0; i < chunks; i++)
            {
                sizes[i] = Math.Min(CHUNK, ogg.Length - i * CHUNK);
            }

            //Worked out before anything is written, because the streamed chunks' offsets are
            //relative to the new BulkDataStartOffset, which depends on the new length.
            var body = 4 + 8 + (24 + CHUNK + 8) + (chunks - 1) * (24 + 8);
            var newSerial = propsEnd + 24 + body;

            var oldSerial = CookedPackage.largestExportSize(uasset);
            var oldBulkStart = BitConverter.ToInt64(uasset, BULK_DATA_START_OFFSET);
            //An int, because correctHeader takes one - and the difference between two
            //asset lengths cannot overflow it.
            var delta = (int)(newSerial - oldSerial);
            var newBulkStart = oldBulkStart + delta;

            //--- the .uexp -------------------------------------------------------------------------
            using var made = new MemoryStream(newSerial + 4);
            using var write = new BinaryWriter(made);

            write.Write(head);
            write.Write(carried);
            write.Write(chunks);
            write.Write(oggName);
            write.Write(0);

            //The inline chunk records where its payload sits in the file as a whole, which is the
            //.uasset's length plus how far into the .uexp we are once its own header is written.
            var inlineAt = uasset.Length + made.Position + 24;
            write.Write(1);
            write.Write(FLAG_INLINE);
            write.Write(CHUNK);
            write.Write(CHUNK);
            write.Write((long)inlineAt);

            write.Write(ogg, 0, sizes[0]);
            write.Write(new byte[CHUNK - sizes[0]]);
            write.Write(CHUNK);
            write.Write(sizes[0]);

            var bulk = new MemoryStream();
            for (var c = 1; c < chunks; c++)
            {
                write.Write(1);
                write.Write(FLAG_STREAMED);
                write.Write(CHUNK);
                write.Write(CHUNK);

                //Relative to BulkDataStartOffset, so it comes out negative. That is correct and it
                //is the field that quietly ruins the audio if it is left pointing at the old place.
                write.Write((long)((c - 1) * (long)CHUNK - newBulkStart));

                write.Write(CHUNK);
                write.Write(sizes[c]);

                bulk.Write(ogg, c * CHUNK, sizes[c]);
                bulk.Write(new byte[CHUNK - sizes[c]], 0, CHUNK - sizes[c]);
            }

            if (made.Position != newSerial)
            {
                throw new InvalidOperationException(
                    $"The rebuilt sound wave came out {made.Position:N0} bytes where {newSerial:N0} "
                    + "was worked out - the layout above does not match what was written.");
            }

            //The package tag, which sits after the serialised export and is not counted in its size.
            write.Write(new byte[] { 0xC1, 0x83, 0x2A, 0x9E });

            //--- the .uasset ----------------------------------------------------------------------
            var header = (byte[])uasset.Clone();
            CookedPackage.correctHeader(header, delta);

            var notes = new List<string>
            {
                $"propsEnd {propsEnd}, exports read {values.Count}",
                $"serial {oldSerial:N0} -> {newSerial:N0} (delta {delta:N0})",
                $"bulkStart {oldBulkStart:N0} -> {newBulkStart:N0}",
                $"chunks {chunks}, ogg {ogg.Length:N0}, uexp {made.Length:N0}, ubulk {bulk.Length:N0}",
                $"uasset {uasset.Length:N0}, invariant bulkStart == uasset+serial: "
                    + $"{uasset.Length + newSerial} vs {newBulkStart}",
            };

            return new Rebuilt(header, made.ToArray(), chunks > 1 ? bulk.ToArray() : null, chunks, notes);
        }

        /// <summary>
        /// Where the tagged properties stop.
        ///
        /// Taken as the end of the last value plus the eight bytes of the FName "None" that closes
        /// the list - and then checked, because getting this wrong shifts everything after it and
        /// the result would still look like a plausible file.
        /// </summary>
        private static int endOfProperties(byte[] uexp, IReadOnlyList<CookedProperties.Value> values,
            IReadOnlyList<string> names)
        {
            var last = values.Max(value => value.At + value.Size);

            if (last + 8 > uexp.Length)
            {
                throw new InvalidOperationException("That sound wave's properties run past its end.");
            }

            var none = names.ToList().IndexOf("None");
            var found = BitConverter.ToInt32(uexp, last);

            if (none < 0 || found != none)
            {
                throw new InvalidOperationException(
                    "The end of that sound wave's properties is not where it should be - expected "
                    + $"the name \"None\" and found index {found}.");
            }

            return last + 8;
        }

        private static void writeInt(byte[] into, IReadOnlyList<CookedProperties.Value> values,
            string name, int value)
        {
            var found = values.FirstOrDefault(one => one.Name == name);
            if (found == null || found.At + 4 > into.Length) { return; }

            BitConverter.GetBytes(value).CopyTo(into, found.At);
        }

        private static void writeFloat(byte[] into, IReadOnlyList<CookedProperties.Value> values,
            string name, float value)
        {
            var found = values.FirstOrDefault(one => one.Name == name);
            if (found == null || found.At + 4 > into.Length) { return; }

            BitConverter.GetBytes(value).CopyTo(into, found.At);
        }

    }
}
