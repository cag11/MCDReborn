using System;
using System.Collections.Generic;
using System.Text;

namespace LiveEdit
{
    /// <summary>
    /// Unreal's table of every name it knows, and the reading of names out of it.
    ///
    /// The object array says what exists; this says what each one is called. Together they end the
    /// guessing: instead of hunting memory for a float that might be a camera's arm length, the
    /// question becomes "which object is called LovikaSpringArm", which has one answer.
    ///
    /// Found the same way the object array was - by shape, and then by proof. The name table is
    /// three levels of indirection deep, and at the bottom of the very first one is the string
    /// "None", because index zero is always `None` in every Unreal game ever built. So the test is
    /// not a byte pattern that a recompile would invalidate; it is following the pointers and
    /// seeing whether the engine's oldest constant is sitting where it must be.
    ///
    /// Layout is UE 4.22's `TStaticIndirectArrayThreadSafeRead`:
    ///   FNameEntry** Chunks[8192]   - each chunk is an array of pointers to entries
    ///   int32        NumElements
    ///   int32        NumChunks
    /// </summary>
    public sealed class NameTable
    {
        private const int ELEMENTS_PER_CHUNK = 16 * 1024;

        //FNameEntry, 4.22:
        //  0x00 int32        Index      - low bit set means the name is wide
        //  0x08 FNameEntry*  HashNext
        //  0x10 char[]       the name itself, ansi or wide
        private const int ENTRY_NAME = 0x10;
        private const int ENTRY_INDEX = 0x00;

        private readonly GameProcess _game;
        private readonly Dictionary<int, string> _known = new Dictionary<int, string>();

        public NameTable(GameProcess game)
        {
            _game = game;
        }

        /// <summary>Where the table is, once it has been found.</summary>
        public IntPtr Chunks { get; private set; }

        public int Count { get; private set; }

        /// <summary>
        /// Finds the name table by following it to "None".
        ///
        /// Every candidate is three dereferences and a string comparison, which sounds expensive
        /// and is not: the first dereference throws away everything that is not a plausible
        /// pointer, and almost nothing survives to the second.
        /// </summary>
        public bool find()
        {
            foreach (var (at, size) in _game.writableRegions())
            {
                var buffer = new byte[size];
                if (!_game.tryRead(at, buffer, (int)size)) { continue; }

                for (int i = 0; i + 8 <= buffer.Length; i += 8)
                {
                    var firstChunk = BitConverter.ToInt64(buffer, i);
                    if (!plausible(firstChunk)) { continue; }

                    //Chunks[0] is an array of entry pointers; its first entry is name zero.
                    var entryPointer = _game.read(new IntPtr(firstChunk), 8);
                    if (entryPointer == null) { continue; }

                    var entry = BitConverter.ToInt64(entryPointer, 0);
                    if (!plausible(entry)) { continue; }

                    if (!string.Equals(readEntry(new IntPtr(entry)), "None", StringComparison.Ordinal)) { continue; }

                    //"None" alone is not enough. Any stray pointer that happens to reach the one
                    //FNameEntry every game has will pass that test, and then every other lookup
                    //comes back empty - which is exactly what a first attempt here did.
                    //
                    //A real table has the engine's own names packed behind it: index one is
                    //ByteProperty, and the handful after are the rest of the property types. So
                    //the table is only believed if reading *through* it keeps working.
                    Chunks = new IntPtr(at.ToInt64() + i);
                    _known.Clear();

                    if (!leadsSomewhere()) { continue; }

                    Count = countFrom(buffer, i);
                    return true;
                }
            }
            return false;
        }

        /// <summary>
        /// How many names the table holds, read from the two integers after the chunk pointers.
        ///
        /// Best effort: the count sits past eight thousand pointer slots, and the table may end
        /// beyond the region that was read. Not knowing it costs nothing - it is a sanity figure,
        /// not something any lookup depends on.
        /// </summary>
        private static int countFrom(byte[] buffer, int at)
        {
            var countAt = at + 8192 * 8;
            if (countAt + 4 > buffer.Length) { return 0; }

            var count = BitConverter.ToInt32(buffer, countAt);
            return count > 0 && count < 10_000_000 ? count : 0;
        }


        /// <summary>
        /// Whether the names after the first one are real.
        ///
        /// Checked by reading a short run of them and requiring most to be the sort of thing a
        /// name is: printable, not empty, not absurdly long. A false table gives nulls and empty
        /// strings almost immediately, so this separates the two without knowing what any
        /// particular game's names should be.
        /// </summary>
        private bool leadsSomewhere()
        {
            var real = 0;
            for (int index = 1; index <= 16; index++)
            {
                var name = nameOf(index);
                if (string.IsNullOrEmpty(name) || name.Length > 100) { continue; }

                var printable = true;
                foreach (var c in name)
                {
                    if (c < 0x20 || c > 0x7e) { printable = false; break; }
                }
                if (printable) { real++; }
            }
            return real >= 12;
        }

        /// <summary>The name at an index, or null. Remembered, because the same few recur endlessly.</summary>
        public string? nameOf(int index)
        {
            if (index < 0) { return null; }
            if (_known.TryGetValue(index, out var already)) { return already; }

            var chunk = index / ELEMENTS_PER_CHUNK;
            var within = index % ELEMENTS_PER_CHUNK;

            var chunkPointer = _game.read(new IntPtr(Chunks.ToInt64() + chunk * 8), 8);
            if (chunkPointer == null) { return null; }

            var chunkAddress = BitConverter.ToInt64(chunkPointer, 0);
            if (!plausible(chunkAddress)) { return null; }

            var entryPointer = _game.read(new IntPtr(chunkAddress + within * 8), 8);
            if (entryPointer == null) { return null; }

            var entry = BitConverter.ToInt64(entryPointer, 0);
            if (!plausible(entry)) { return null; }

            var name = readEntry(new IntPtr(entry));
            if (name != null) { _known[index] = name; }
            return name;
        }

        /// <summary>
        /// One entry's text.
        ///
        /// The low bit of the entry's index says whether the characters are wide. Almost every
        /// name in a game is ansi, but getting this wrong on the few that are not would produce
        /// names that look like noise rather than an obvious failure.
        /// </summary>
        private string? readEntry(IntPtr entry)
        {
            var header = _game.read(entry, 4);
            if (header == null) { return null; }

            var wide = (BitConverter.ToInt32(header, 0) & 1) != 0;

            //Names are short. Reading a fixed block and stopping at the terminator avoids a read
            //per character, which over half a million objects is the difference between seconds
            //and minutes.
            var bytes = _game.read(new IntPtr(entry.ToInt64() + ENTRY_NAME), wide ? 256 : 128);
            if (bytes == null) { return null; }

            if (wide)
            {
                for (int i = 0; i + 1 < bytes.Length; i += 2)
                {
                    if (bytes[i] == 0 && bytes[i + 1] == 0)
                    {
                        return Encoding.Unicode.GetString(bytes, 0, i);
                    }
                }
                return null;
            }

            for (int i = 0; i < bytes.Length; i++)
            {
                if (bytes[i] != 0) { continue; }
                if (i == 0) { return string.Empty; }
                return Encoding.ASCII.GetString(bytes, 0, i);
            }
            return null;
        }

        private static bool plausible(long pointer) => pointer > 0x10000 && pointer < 0x7FFFFFFFFFFF;
    }
}
