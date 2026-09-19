using System;
using System.IO;
#nullable enable

namespace MCDSaveEdit.Logic
{
    /// <summary>
    /// The two numbers in a cooked package's header that stop being true when its export changes
    /// length.
    ///
    /// A `.uasset` says how long its export is and where the bulk data after it begins, and both
    /// are absolute rather than derived, so growing the `.uexp` by a byte leaves the pair a byte
    /// out and the game refuses the file. Neither is difficult to correct once found; finding them
    /// is the part worth writing down, because the export table's position is only readable by
    /// stepping the summary's variable length fields in order.
    ///
    /// This lives apart from the meshes because both kinds need it and the correction has nothing
    /// to do with geometry - it is true of any export that is rewritten at a different size.
    /// </summary>
    internal static class CookedPackage
    {
        //Where the bulk data offset sits in the summary. Fixed in this engine version, and checked
        //against the game's own assets rather than taken from the format's documentation.
        private const int BULK_DATA_START_OFFSET = 169;
        private const int EXPORT_ENTRY_SIZE = 104;
        private const int SERIAL_SIZE_IN_ENTRY = 28;
        private const int SERIAL_OFFSET_IN_ENTRY = 36;

        /// <summary>
        /// The recorded length of the biggest export - the one an asset is really about.
        ///
        /// Exposed because a caller rebuilding an export has to know what it was before, in order
        /// to work out the delta to pass back to correctHeader. Reading it from a fixed offset does
        /// not work: the export table's position is written in the summary, after a name string and
        /// a variable number of custom versions, so it moves between assets. Doing it by hand gave
        /// a serial size of seven quintillion and a bulk offset two gigabytes into negative.
        /// </summary>
        public static long largestExportSize(byte[] uasset)
        {
            var exportCount = exportTableCount(uasset, out var exportOffset);
            if (exportCount <= 0) { return 0; }

            var biggest = 0L;
            for (int i = 0; i < exportCount; i++)
            {
                var entry = exportOffset + i * EXPORT_ENTRY_SIZE;
                if (entry + SERIAL_SIZE_IN_ENTRY + 8 > uasset.Length) { break; }

                var size = BitConverter.ToInt64(uasset, entry + SERIAL_SIZE_IN_ENTRY);
                if (size > biggest) { biggest = size; }
            }

            return biggest;
        }

        /// <summary>
        /// Moves the export's recorded length, and the bulk data marker after it, by however much
        /// the export grew or shrank.
        ///
        /// Only the last export needs this. Were the rewritten export not last, every export after
        /// it would need its offset moved too - so that is checked rather than assumed.
        /// </summary>
        public static void correctHeader(byte[] uasset, int delta)
        {
            if (delta == 0) { return; }

            var exportCount = exportTableCount(uasset, out var exportOffset);
            if (exportCount <= 0) { throw new InvalidOperationException("Could not read the export table."); }

            var lastEntry = exportOffset + (exportCount - 1) * EXPORT_ENTRY_SIZE;
            if (lastEntry + SERIAL_OFFSET_IN_ENTRY + 8 > uasset.Length)
            {
                throw new InvalidOperationException("The export table is not where the header says it is.");
            }

            //The rewritten export has to be the last one for this to be the whole correction.
            var biggest = 0L;
            var biggestEntry = -1;
            for (int i = 0; i < exportCount; i++)
            {
                var entry = exportOffset + i * EXPORT_ENTRY_SIZE;
                var size = BitConverter.ToInt64(uasset, entry + SERIAL_SIZE_IN_ENTRY);
                if (size > biggest) { biggest = size; biggestEntry = entry; }
            }
            if (biggestEntry != lastEntry)
            {
                throw new InvalidOperationException("The mesh is not the last export, so more offsets would need moving.");
            }

            var serialSize = BitConverter.ToInt64(uasset, lastEntry + SERIAL_SIZE_IN_ENTRY);
            writeLong(uasset, lastEntry + SERIAL_SIZE_IN_ENTRY, serialSize + delta);

            var bulkStart = BitConverter.ToInt64(uasset, BULK_DATA_START_OFFSET);
            writeLong(uasset, BULK_DATA_START_OFFSET, bulkStart + delta);
        }

        /// <summary>
        /// How many exports there are and where the table begins.
        ///
        /// Read from the summary rather than searched for, because this part of a cooked package
        /// is one of the few things laid out at a knowable place.
        /// </summary>
        private static int exportTableCount(byte[] uasset, out int exportOffset)
        {
            exportOffset = 0;
            if (uasset.Length < 64) { return 0; }

            using var stream = new MemoryStream(uasset);
            using var reader = new BinaryReader(stream);

            if (reader.ReadUInt32() != 0x9E2A83C1) { return 0; }
            var legacy = reader.ReadInt32();
            if (legacy != -4) { reader.ReadInt32(); }
            reader.ReadInt32(); // ue4 version
            reader.ReadInt32(); // licensee version
            var customVersions = reader.ReadInt32();
            for (int i = 0; i < customVersions; i++) { reader.ReadBytes(20); }
            reader.ReadInt32(); // total header size
            skipString(reader);
            reader.ReadUInt32(); // package flags
            reader.ReadInt32(); // name count
            reader.ReadInt32(); // name offset
            reader.ReadInt32(); // gatherable text count
            reader.ReadInt32(); // gatherable text offset

            var exportCount = reader.ReadInt32();
            exportOffset = reader.ReadInt32();
            return exportCount;
        }

        private static void skipString(BinaryReader reader)
        {
            var length = reader.ReadInt32();
            if (length == 0) { return; }
            reader.ReadBytes(length < 0 ? -length * 2 : length);
        }

        private static void writeLong(byte[] destination, int at, long value)
        {
            Buffer.BlockCopy(BitConverter.GetBytes(value), 0, destination, at, 8);
        }
    }
}
