using System;
using System.Collections.Generic;

namespace LiveEdit
{
    /// <summary>
    /// Finding Unreal's own list of every object in the running game.
    ///
    /// This is the thing that makes everything else possible. The engine keeps a global array of
    /// every live object - `FUObjectArray GUObjectArray` - and with it a component can be found by
    /// what it *is*, rather than by hunting memory for a float that looks about right. Days were
    /// spent on the latter and it never distinguished the live camera from the templates that hold
    /// identical numbers.
    ///
    /// The usual way to reach it is a byte pattern lifted from the game's code, which is what
    /// UE4SS ships per game and what its signature files are for. That route is closed twice over
    /// here: UE4SS crashes on this engine before it would ever read a signature, and a pattern is
    /// a brittle thing that a single recompile invalidates.
    ///
    /// So it is found by its shape instead. `FUObjectArray` is a very particular arrangement of
    /// numbers - a pointer to an array of chunk pointers, a capacity in the millions, a count below
    /// that capacity, a chunk count below a chunk capacity - and almost nothing else in a process
    /// looks like it. The same approach that found a mesh's vertices by matching them against the
    /// bounds the file declared: do not walk to the thing, recognise it.
    ///
    /// The layout below is UE 4.22's, which is what this game reports itself as (4.22.3).
    /// </summary>
    public sealed class ObjectTables
    {
        //FUObjectArray, 4.22:
        //  0x00 int32  ObjFirstGCIndex
        //  0x04 int32  ObjLastNonGCIndex
        //  0x08 int32  MaxObjectsNotConsideredByGC
        //  0x0C bool   OpenForDisregardForGC
        //  0x10        FChunkedFixedUObjectArray ObjObjects
        private const int OBJECTS_IN_ARRAY = 0x10;

        //FChunkedFixedUObjectArray, which is the part with a recognisable shape:
        //  0x00 FUObjectItem** Objects        - array of chunk pointers
        //  0x08 FUObjectItem*  PreAllocated   - usually null
        //  0x10 int32          MaxElements
        //  0x14 int32          NumElements
        //  0x18 int32          MaxChunks
        //  0x1C int32          NumChunks
        private const int CHUNKED_SIZE = 0x20;

        //FUObjectItem: the object pointer, then flags and two indices.
        private const int ITEM_SIZE = 24;

        //A chunk holds this many items in every shipping build of this era.
        private const int ITEMS_PER_CHUNK = 64 * 1024;

        private readonly GameProcess _game;

        public ObjectTables(GameProcess game)
        {
            _game = game;
        }

        /// <summary>Where the object array is, and what it says about itself.</summary>
        public sealed class Found
        {
            public IntPtr ChunkedArray { get; set; }
            public IntPtr Chunks { get; set; }
            public int MaxElements { get; set; }
            public int NumElements { get; set; }
            public int MaxChunks { get; set; }
            public int NumChunks { get; set; }
            /// <summary>The first object's address, proving the chunks really hold objects.</summary>
            public IntPtr FirstObject { get; set; }
        }

        /// <summary>
        /// Every place in memory shaped like the engine's object array.
        ///
        /// Expected to find one. The shape is specific enough that a coincidence would be
        /// remarkable, and each candidate is confirmed by following its chunk pointers to a real
        /// object before it is reported.
        /// </summary>
        public IReadOnlyList<Found> search()
        {
            var found = new List<Found>();

            foreach (var (at, size) in _game.writableRegions())
            {
                var buffer = new byte[size];
                if (!_game.tryRead(at, buffer, (int)size)) { continue; }

                for (int i = 0; i + CHUNKED_SIZE <= buffer.Length; i += 8)
                {
                    var candidate = examine(buffer, i, at);
                    if (candidate != null) { found.Add(candidate); }
                }
            }

            return found;
        }

        /// <summary>
        /// Whether the bytes at this offset are a chunked object array, judged first by their own
        /// consistency and then by following them.
        ///
        /// The numeric tests come first because they are free, and they throw away all but a
        /// handful of offsets. Only what survives is worth a read into the game.
        /// </summary>
        private Found? examine(byte[] buffer, int at, IntPtr regionBase)
        {
            var chunks = BitConverter.ToInt64(buffer, at);
            var preAllocated = BitConverter.ToInt64(buffer, at + 8);
            var maxElements = BitConverter.ToInt32(buffer, at + 0x10);
            var numElements = BitConverter.ToInt32(buffer, at + 0x14);
            var maxChunks = BitConverter.ToInt32(buffer, at + 0x18);
            var numChunks = BitConverter.ToInt32(buffer, at + 0x1C);

            //A user mode pointer, and one that is not obviously rubbish.
            if (chunks <= 0x10000 || chunks > 0x7FFFFFFFFFFF) { return null; }
            //The pre-allocated block is null in a chunked array; that is what makes it chunked.
            if (preAllocated != 0) { return null; }

            //A game has tens of thousands of objects, not three and not a billion.
            if (numElements < 1000 || numElements > 20_000_000) { return null; }
            if (maxElements < numElements || maxElements > 20_000_000) { return null; }
            if (numChunks < 1 || numChunks > 10000 || maxChunks < numChunks) { return null; }

            //The numbers have to agree with each other. This is the test that does the work: a run
            //of plausible-looking integers will not normally have its capacity equal to its chunk
            //count times the chunk size.
            if (maxElements != maxChunks * ITEMS_PER_CHUNK) { return null; }
            if (numChunks != (numElements + ITEMS_PER_CHUNK - 1) / ITEMS_PER_CHUNK) { return null; }

            //Now it is worth following. The first chunk pointer, then the first object in it.
            var firstChunk = _game.read(new IntPtr(chunks), 8);
            if (firstChunk == null) { return null; }

            var chunkAddress = BitConverter.ToInt64(firstChunk, 0);
            if (chunkAddress <= 0x10000) { return null; }

            var firstItem = _game.read(new IntPtr(chunkAddress), ITEM_SIZE);
            if (firstItem == null) { return null; }

            var objectAddress = BitConverter.ToInt64(firstItem, 0);
            if (objectAddress <= 0x10000) { return null; }

            //An object begins with a vtable pointer, so there has to be something readable there.
            var vtable = _game.read(new IntPtr(objectAddress), 8);
            if (vtable == null) { return null; }
            if (BitConverter.ToInt64(vtable, 0) <= 0x10000) { return null; }

            return new Found {
                ChunkedArray = new IntPtr(regionBase.ToInt64() + at),
                Chunks = new IntPtr(chunks),
                MaxElements = maxElements,
                NumElements = numElements,
                MaxChunks = maxChunks,
                NumChunks = numChunks,
                FirstObject = new IntPtr(objectAddress),
            };
        }

        /// <summary>The address of one object by its index, or nothing.</summary>
        public IntPtr objectAt(Found array, int index)
        {
            if (index < 0 || index >= array.NumElements) { return IntPtr.Zero; }

            var chunk = index / ITEMS_PER_CHUNK;
            var within = index % ITEMS_PER_CHUNK;

            var chunkPointer = _game.read(new IntPtr(array.Chunks.ToInt64() + chunk * 8), 8);
            if (chunkPointer == null) { return IntPtr.Zero; }

            var chunkAddress = BitConverter.ToInt64(chunkPointer, 0);
            if (chunkAddress <= 0x10000) { return IntPtr.Zero; }

            var item = _game.read(new IntPtr(chunkAddress + within * ITEM_SIZE), 8);
            if (item == null) { return IntPtr.Zero; }

            return new IntPtr(BitConverter.ToInt64(item, 0));
        }
    }
}
