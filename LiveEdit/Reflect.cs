using System;
using System.Collections.Generic;

namespace LiveEdit
{
    /// <summary>
    /// The game's own description of itself, read out of the running process.
    ///
    /// Unreal keeps a complete account of every class, struct, enum and function it was built
    /// with - names, types, and where each field sits inside its owner. That account is what the
    /// engine uses to serialise a save and to let Blueprint touch C++ at all, and it is sitting in
    /// memory the whole time the game is open.
    ///
    /// Which matters here because the alternative is guessing. To call one of this game's own
    /// functions from a modded blueprint, the blueprint has to be compiled against a declaration
    /// that matches the real one field for field; a field named wrong or typed wrong does not fail
    /// loudly, it writes into the wrong place. Published offset tables are no help - every one of
    /// them online is for a different engine version, an editor build, or both. Asking the binary
    /// is the only answer that is about THIS executable.
    ///
    /// Three things are found, in order, and none of them is hardcoded:
    ///
    ///   GNames        by the compiled code of FName::GetNames, whose `mov ecx, 808h` is the size
    ///                 of the 4.22 name table and is not said anywhere else in fifty megabytes
    ///   GUObjectArray by sweeping the statics for something whose chunk arithmetic agrees with
    ///                 itself, which is a stiff enough test that rubbish does not pass it
    ///   everything    by name, out of the object array
    ///
    /// The one thing that IS hardcoded is the shape of UObject and UProperty themselves, because
    /// they are what the account is written in and there is nothing underneath them to ask. Those
    /// offsets are stated below with their reasoning, and one of them is the trap that every
    /// published table falls into - see <see cref="SUPER"/>.
    ///
    /// Read-only throughout. Nothing here writes to the game.
    /// </summary>
    public sealed class Reflect
    {
        //--- UObject ---------------------------------------------------------------------------
        //vtable 0x00, ObjectFlags 0x08, InternalIndex 0x0C, Class 0x10, Name 0x18, Outer 0x20.
        private const int CLASS_AT = 0x10;
        private const int NAME_AT = 0x18;
        private const int OUTER_AT = 0x20;

        //--- UField ----------------------------------------------------------------------------
        //One link, so that a struct's fields are a list rather than an array.
        private const int NEXT = 0x28;

        /// <summary>
        /// Where a struct records what it inherits from - and the offset that costs everybody a
        /// day.
        ///
        /// In the engine's source UStruct's first member sits at 0x30. In a SHIPPING build it sits
        /// at 0x40, because a non-editor build makes UStruct privately inherit FStructBaseChain,
        /// which adds two pointers. Every offset table published for this engine is the editor
        /// layout, and reading with it yields plausible rubbish rather than an error: the pointers
        /// land on real objects, the names come back as words, and only the answers are wrong.
        /// </summary>
        private const int SUPER = 0x40;

        private const int CHILDREN = 0x48;

        //--- UProperty -------------------------------------------------------------------------
        //ArrayDim 0x30, ElementSize 0x34, PropertyFlags 0x38, RepIndex 0x40, Offset_Internal 0x44.
        private const int ARRAY_DIM = 0x30;
        private const int ELEMENT_SIZE = 0x34;
        private const int PROPERTY_FLAGS = 0x38;
        private const int OFFSET_AT = 0x44;

        /// <summary>
        /// Where a property that refers to another type keeps the reference.
        ///
        /// sizeof(UProperty) is 0x70, so whatever a subclass adds begins there - and every subclass
        /// worth reading adds a pointer first: UStructProperty::Struct, UObjectProperty::
        /// PropertyClass, UArrayProperty::Inner, UByteProperty::Enum, UEnumProperty::UnderlyingProp.
        /// So one read answers "a struct of what", "an array of what" and "an enum of what" without
        /// a table of five offsets that would all have to be right.
        /// </summary>
        private const int INNER_AT = 0x70;

        /// <summary>
        /// Where a UEnumProperty keeps the enum.
        ///
        /// It adds TWO members: UnderlyingProp at 0x70 and Enum at 0x78. The first is a real
        /// UProperty whose name is literally "UnderlyingType", so reading 0x70 for an enum
        /// property yields that word for every enum in the game - which looks like a type and is
        /// not one.
        /// </summary>
        private const int ENUM_AT = 0x78;

        //--- UEnum -----------------------------------------------------------------------------
        //UField, then CppType (FString, 0x10 bytes), then the pairs.
        private const int ENUM_NAMES = 0x40;

        //CPF_Parm and CPF_ReturnParm, which is how a function's arguments are told from its locals.
        private const ulong IS_PARAMETER = 0x0000000000000080;
        private const ulong IS_RETURN = 0x0000000000000400;

        private readonly GameProcess _game;
        private readonly NameTable _names;
        private readonly long _objects;
        private readonly int _perChunk;

        private Reflect(GameProcess game, NameTable names, long objects, int perChunk, int count)
        {
            _game = game;
            _names = names;
            _objects = objects;
            _perChunk = perChunk;
            Count = count;
        }

        /// <summary>How many objects the game is holding.</summary>
        public int Count { get; }

        /// <summary>One field of a struct, or one argument of a function.</summary>
        public sealed class Field
        {
            public string Name { get; set; } = string.Empty;

            /// <summary>The property's own class - "IntProperty", "StructProperty", and so on.</summary>
            public string Kind { get; set; } = string.Empty;

            /// <summary>What it is a struct of, an array of, an object of, an enum of.</summary>
            public string? Of { get; set; }

            public int Offset { get; set; }
            public int Size { get; set; }
            public int Count { get; set; }
            public ulong Flags { get; set; }

            /// <summary>
            /// For a BoolProperty, which byte and which bit.
            ///
            /// A bool in Unreal is not always a byte. `uint32 bEnableClickEvents:1` is one bit of
            /// a shared word, and reading the whole byte as a bool gives whichever neighbouring
            /// flags happen to be set. UBoolProperty carries the byte offset and the mask so the
            /// question can be asked properly.
            /// </summary>
            public int BoolByte { get; set; }

            public byte BoolMask { get; set; }

            public bool IsParameter => (Flags & IS_PARAMETER) != 0;
            public bool IsReturn => (Flags & IS_RETURN) != 0;

            /// <summary>The field as a C++ declaration would spell it, as near as can be told.</summary>
            public string Cpp => cppFor(Kind, Of) + (Count > 1 ? $"[{Count}]" : string.Empty);

            public override string ToString()
                => $"+0x{Offset:x3}  {Size,4}  {Cpp,-42} {Name}";
        }

        /// <summary>
        /// Finds the three things, or says which one it could not find.
        ///
        /// <paramref name="say"/> is handed each step, because when this fails the useful
        /// information is how far it got.
        /// </summary>
        public static Reflect? open(GameProcess game, Action<string>? say = null)
        {
            void said(string what) => say?.Invoke(what);

            var image = game.image(out var imageSize);
            var baseAt = image.ToInt64();

            //--- the sections, out of the header ------------------------------------------------
            //The names are blanked by the protector but the table itself is intact, so the code
            //and data ranges are taken by flags and size rather than by looking for ".text".
            long codeAt = baseAt + 0x1000, codeEnd = baseAt + imageSize;
            long dataAt = 0, dataEnd = 0;

            var dos = game.read(image, 0x40);
            if (dos != null && dos[0] == 'M' && dos[1] == 'Z')
            {
                var peAt = BitConverter.ToInt32(dos, 0x3c);
                var pe = game.read(new IntPtr(baseAt + peAt), 0x108);

                if (pe != null)
                {
                    var sections = BitConverter.ToUInt16(pe, 6);
                    var firstSection = peAt + 24 + BitConverter.ToUInt16(pe, 20);
                    var haveCode = false;

                    for (var i = 0; i < sections; i++)
                    {
                        var row = game.read(new IntPtr(baseAt + firstSection + i * 40), 40);
                        if (row == null) { continue; }

                        var flags = BitConverter.ToUInt32(row, 36);
                        var rva = BitConverter.ToUInt32(row, 12);
                        var size = BitConverter.ToUInt32(row, 8);

                        if (size < 0x100000) { continue; }

                        var executable = (flags & 0x20000000) != 0;

                        if (executable && !haveCode)
                        {
                            codeAt = baseAt + rva;
                            codeEnd = codeAt + size;
                            haveCode = true;
                        }

                        //Initialised, writable, not executable: the statics.
                        if (!executable && (flags & 0x80000000) != 0 && (flags & 0x40000000) != 0
                            && dataAt == 0)
                        {
                            dataAt = baseAt + rva;
                            dataEnd = dataAt + size;
                        }
                    }
                }
            }

            said($"module at {baseAt:x}, code +{codeAt - baseAt:x}, data +{dataAt - baseAt:x}");

            //--- GNames, by the code that makes it ----------------------------------------------
            //
            //  48 83 EC 28              sub  rsp, 28h
            //  48 8B 05 ?? ?? ?? ??     mov  rax, cs:Names        <- the address wanted
            //  48 85 C0                 test rax, rax
            //  75 ??                    jnz  short already
            //  B9 08 08 00 00           mov  ecx, 808h            <- sizeof the 4.22 table
            //
            //Shape does not work here. A table of pointers to pointers is not a rare thing to look
            //like, and sweeping this game for one finds forty-two wrong answers that all
            //dereference twice onto something spelling a word.
            var want = new byte?[]
            {
                0x48, 0x83, 0xEC, 0x28, 0x48, 0x8B, 0x05, null, null, null, null,
                0x48, 0x85, 0xC0, 0x75, null, 0xB9, 0x08, 0x08, 0x00, 0x00,
            };

            long table = 0;
            const int CHUNK = 4 * 1024 * 1024;
            var buffer = new byte[CHUNK + 64];

            for (var at = codeAt; at < codeEnd && table == 0; at += CHUNK)
            {
                var take = (int)Math.Min(CHUNK + 64, codeEnd - at);
                if (!game.tryRead(new IntPtr(at), buffer, take)) { continue; }

                for (var i = 0; i + want.Length <= take; i++)
                {
                    if (buffer[i] != 0x48) { continue; }

                    var same = true;
                    for (var j = 1; j < want.Length; j++)
                    {
                        if (want[j] == null) { continue; }
                        if (buffer[i + j] != want[j]!.Value) { same = false; break; }
                    }

                    if (!same) { continue; }

                    var pointerAt = at + i + 11 + BitConverter.ToInt32(buffer, i + 7);
                    var held = game.read(new IntPtr(pointerAt), 8);
                    if (held == null) { continue; }

                    table = BitConverter.ToInt64(held, 0);
                    break;
                }
            }

            if (table == 0) { said("could not find the name table"); return null; }

            var names = new NameTable(game);
            names.useChunks(new IntPtr(table));

            //Index zero is `None` in every Unreal game ever built, so this is proof rather than a
            //plausibility check.
            if (names.nameOf(0) != "None") { said("the name table does not read back"); return null; }

            said($"names at {table:x}");

            //--- GUObjectArray, by arithmetic that has to agree with itself ---------------------
            long objects = 0;
            var count = 0;
            var perChunk = 0;

            if (dataAt != 0)
            {
                var data = new byte[Math.Min(dataEnd - dataAt, 64 * 1024 * 1024)];

                if (game.tryRead(new IntPtr(dataAt), data, data.Length))
                {
                    for (var i = 0; i + 0x20 <= data.Length; i += 4)
                    {
                        var chunks = BitConverter.ToInt64(data, i);
                        if (chunks < 0x10000 || chunks > 0x7fffffffffff) { continue; }

                        var maxElements = BitConverter.ToInt32(data, i + 0x10);
                        var numElements = BitConverter.ToInt32(data, i + 0x14);
                        var maxChunks = BitConverter.ToInt32(data, i + 0x18);
                        var numChunks = BitConverter.ToInt32(data, i + 0x1C);

                        if (numChunks < 1 || numChunks > 0x14) { continue; }
                        if (maxChunks < 6 || maxChunks > 0x5FF) { continue; }
                        if (numElements <= 0x800 || maxElements <= 0x10000) { continue; }
                        if (numElements > maxElements || numChunks > maxChunks) { continue; }
                        if (maxElements % 0x10 != 0) { continue; }

                        var each = maxElements / maxChunks;
                        if (each % 0x10 != 0 || each < 0x8000 || each > 0x80000) { continue; }
                        if (numElements / each + 1 != numChunks) { continue; }
                        if (maxElements / each != maxChunks) { continue; }

                        var ok = true;
                        for (var c = 0; c < numChunks && ok; c++)
                        {
                            var one = game.read(new IntPtr(chunks + c * 8), 8);
                            ok = one != null && BitConverter.ToInt64(one, 0) > 0x10000;
                        }

                        if (!ok) { continue; }

                        objects = chunks;
                        count = numElements;
                        perChunk = each;

                        said($"objects at {dataAt + i:x}: {numElements:N0} in "
                            + $"{numChunks} chunk(s) of {each:N0}");
                        break;
                    }
                }
            }

            if (objects == 0) { said("could not find the object array"); return null; }

            return new Reflect(game, names, objects, perChunk, count);
        }

        private IntPtr deref(long address, int offset)
        {
            var raw = _game.read(new IntPtr(address + offset), 8);
            return raw == null ? IntPtr.Zero : new IntPtr(BitConverter.ToInt64(raw, 0));
        }

        private int? int32(long address, int offset)
        {
            var raw = _game.read(new IntPtr(address + offset), 4);
            return raw == null ? null : BitConverter.ToInt32(raw, 0);
        }

        /// <summary>What an object is called, or null when it is not there.</summary>
        public string? nameOf(long obj)
        {
            if (obj == 0) { return null; }
            var raw = _game.read(new IntPtr(obj + NAME_AT), 4);
            return raw == null ? null : _names.nameOf(BitConverter.ToInt32(raw, 0));
        }

        /// <summary>What an object's class is called - "Class", "ScriptStruct", "Function".</summary>
        public string? kindOf(long obj) => nameOf(deref(obj, CLASS_AT).ToInt64());

        /// <summary>What it lives inside, which for a class is its package.</summary>
        public string? outerOf(long obj) => nameOf(deref(obj, OUTER_AT).ToInt64());

        public long objectAt(int index)
        {
            var chunk = _game.read(new IntPtr(_objects + (index / _perChunk) * 8), 8);
            if (chunk == null) { return 0; }

            var where = BitConverter.ToInt64(chunk, 0);
            if (where <= 0x10000) { return 0; }

            var item = _game.read(new IntPtr(where + (index % _perChunk) * 0x18), 8);
            return item == null ? 0 : BitConverter.ToInt64(item, 0);
        }

        /// <summary>
        /// The one object of that name, optionally of that kind.
        ///
        /// A UClass is an instance of the class called `Class`, so "the class named LevelSettings"
        /// and "an object named LevelSettings" are different questions and only the first one has
        /// a useful answer. <paramref name="kind"/> is how the second is excluded.
        /// </summary>
        public long find(string name, string? kind = null)
        {
            for (var i = 0; i < Count; i++)
            {
                var one = objectAt(i);
                if (one == 0) { continue; }
                if (!string.Equals(nameOf(one), name, StringComparison.Ordinal)) { continue; }
                if (kind != null && !string.Equals(kindOf(one), kind, StringComparison.Ordinal))
                {
                    continue;
                }

                return one;
            }

            return 0;
        }

        /// <summary>Everything of that name, whatever it is - for when the kind is the question.</summary>
        public List<(long at, string kind, string outer)> findAll(string name)
        {
            var found = new List<(long, string, string)>();

            for (var i = 0; i < Count; i++)
            {
                var one = objectAt(i);
                if (one == 0) { continue; }
                if (!string.Equals(nameOf(one), name, StringComparison.Ordinal)) { continue; }

                found.Add((one, kindOf(one) ?? "?", outerOf(one) ?? "?"));
            }

            return found;
        }

        /// <summary>What it inherits from, nearest first, itself included.</summary>
        public List<string> chainOf(long structure)
        {
            var chain = new List<string>();

            for (var at = structure; at != 0; at = deref(at, SUPER).ToInt64())
            {
                var said = nameOf(at);
                if (said == null) { break; }

                chain.Add(said);
                if (chain.Count > 24) { break; }
            }

            return chain;
        }

        /// <summary>
        /// Every field a struct, class or function declares, in the order it declares them.
        ///
        /// Its own only. Inherited fields belong to the parent and are read by walking
        /// <see cref="chainOf"/> - which is what a declaration wants anyway, since a stub written
        /// to match has its own parent supplying them.
        /// </summary>
        public List<Field> fieldsOf(long structure)
        {
            var fields = new List<Field>();

            for (var at = deref(structure, CHILDREN).ToInt64(); at != 0;
                 at = deref(at, NEXT).ToInt64())
            {
                var said = nameOf(at);
                if (said == null) { break; }

                var kind = kindOf(at) ?? "?";

                //A Function is a child of its class too, and it is not a field. It has its own
                //children, which are the arguments, so it is read by asking for it separately.
                if (kind == "Function" || kind == "Enum" || kind == "ScriptStruct")
                {
                    continue;
                }

                var flags = _game.read(new IntPtr(at + PROPERTY_FLAGS), 8);

                //UBoolProperty: FieldSize 0x70, ByteOffset 0x71, ByteMask 0x72, FieldMask 0x73.
                var bits = kind == "BoolProperty" ? _game.read(new IntPtr(at + INNER_AT), 4) : null;

                fields.Add(new Field
                {
                    Name = said,
                    Kind = kind,
                    Of = ofWhat(at, kind),
                    Offset = int32(at, OFFSET_AT) ?? -1,
                    Size = int32(at, ELEMENT_SIZE) ?? -1,
                    Count = int32(at, ARRAY_DIM) ?? 1,
                    Flags = flags == null ? 0 : BitConverter.ToUInt64(flags, 0),
                    BoolByte = bits == null ? 0 : bits[1],
                    BoolMask = bits == null ? (byte)0xFF : bits[2],
                });

                if (fields.Count > 512) { break; }
            }

            return fields;
        }

        /// <summary>
        /// What a property refers to, when it refers to something.
        ///
        /// A container is the awkward case. UArrayProperty::Inner is not a type, it is another
        /// PROPERTY - and one that carries the array's own name, so reading its name back gives
        /// `TArray of ownedDLCs` where `TArray of EDLCName` was wanted. What is needed is the inner
        /// property's own kind, and then what THAT refers to, which is one step of recursion and
        /// the reason this is a method rather than a field read.
        /// </summary>
        private string? ofWhat(long property, string kind)
        {
            if (kind == "EnumProperty") { return nameOf(deref(property, ENUM_AT).ToInt64()); }

            if (kind == "ArrayProperty" || kind == "SetProperty" || kind == "MapProperty")
            {
                var inner = deref(property, INNER_AT).ToInt64();
                if (inner == 0) { return null; }

                var innerKind = kindOf(inner) ?? "?";
                return cppFor(innerKind, ofWhat(inner, innerKind));
            }

            return nameOf(deref(property, INNER_AT).ToInt64());
        }

        /// <summary>
        /// Every name an enum knows and the number behind it.
        ///
        /// Which is the question a caller usually has: not what an enum is called but which of its
        /// values means Creeper Woods. Stored as pairs of an FName and an int64, sixteen bytes
        /// each.
        /// </summary>
        public List<(string name, long value)> valuesOf(long enumeration)
        {
            var found = new List<(string, long)>();

            var raw = _game.read(new IntPtr(enumeration + ENUM_NAMES), 16);
            if (raw == null) { return found; }

            var at = BitConverter.ToInt64(raw, 0);
            var many = BitConverter.ToInt32(raw, 8);

            if (at <= 0x10000 || many <= 0 || many > 4096) { return found; }

            var all = _game.read(new IntPtr(at), many * 16);
            if (all == null) { return found; }

            for (var i = 0; i < many; i++)
            {
                var said = _names.nameOf(BitConverter.ToInt32(all, i * 16));
                if (said == null) { continue; }

                //An FName is an index AND a number, and the number is not decoration. The
                //engine spells a trailing digit by reusing one name entry: Difficulty01 is the
                //name "Difficulty" with number 2. Read the index alone and an enum of seven
                //threat levels comes back as the word "Threat" seven times over, which looks
                //like the read having failed and is the read being half done.
                var number = BitConverter.ToInt32(all, i * 16 + 4);
                if (number > 0) { said += "_" + (number - 1); }

                found.Add((said, BitConverter.ToInt64(all, i * 16 + 8)));
            }

            return found;
        }

        /// <summary>
        /// A named field of a live object, as text, or null when it cannot be read.
        ///
        /// Walks the object's own class chain rather than being told an offset, so it answers for
        /// whatever the game actually made - which for a player controller is its own subclass,
        /// not the engine's.
        /// </summary>
        public string? valueOf(long obj, string field)
        {
            for (var klass = deref(obj, CLASS_AT).ToInt64(); klass != 0;
                 klass = deref(klass, SUPER).ToInt64())
            {
                foreach (var one in fieldsOf(klass))
                {
                    if (!string.Equals(one.Name, field, StringComparison.Ordinal)) { continue; }

                    var at = obj + one.Offset;

                    switch (one.Kind)
                    {
                        case "BoolProperty":
                        {
                            var raw = _game.read(new IntPtr(at + one.BoolByte), 1);
                            if (raw == null) { return null; }
                            return ((raw[0] & one.BoolMask) != 0).ToString().ToLowerInvariant()
                                + $"   (byte +0x{one.Offset + one.BoolByte:x}, mask 0x{one.BoolMask:x2})";
                        }

                        case "IntProperty":
                        {
                            var raw = _game.read(new IntPtr(at), 4);
                            return raw == null ? null : BitConverter.ToInt32(raw, 0).ToString();
                        }

                        case "FloatProperty":
                        {
                            var raw = _game.read(new IntPtr(at), 4);
                            return raw == null ? null : BitConverter.ToSingle(raw, 0).ToString("F2");
                        }

                        case "ByteProperty":
                        case "EnumProperty":
                        {
                            var raw = _game.read(new IntPtr(at), 1);
                            return raw == null ? null : raw[0].ToString();
                        }

                        case "StructProperty" when one.Of == "Vector":
                        {
                            var raw = _game.read(new IntPtr(at), 12);
                            if (raw == null) { return null; }
                            return $"{BitConverter.ToSingle(raw, 0):F0}, "
                                + $"{BitConverter.ToSingle(raw, 4):F0}, "
                                + $"{BitConverter.ToSingle(raw, 8):F0}";
                        }

                        case "NameProperty":
                        {
                            var raw = _game.read(new IntPtr(at), 4);
                            return raw == null ? null : _names.nameOf(BitConverter.ToInt32(raw, 0));
                        }

                        default:
                            return $"({one.Kind}, not read)";
                    }
                }
            }

            return null;
        }

        /// <summary>
        /// The property class's name written the way a header would say it.
        ///
        /// Best effort and said so: a FloatProperty is a float and there is nothing to argue
        /// about, but an ArrayProperty only knows what its Inner is called and not what KIND its
        /// inner is, so `TArray&lt;Vector&gt;` comes back where `TArray&lt;FVector&gt;` was meant.
        /// The names are right; the prefixes and the templates are a convenience.
        /// </summary>
        private static string cppFor(string kind, string? of) => kind switch
        {
            "BoolProperty" => "bool",
            "Int8Property" => "int8",
            "Int16Property" => "int16",
            "IntProperty" => "int32",
            "Int64Property" => "int64",
            "ByteProperty" => of == null ? "uint8" : $"TEnumAsByte<{of}>",
            "UInt16Property" => "uint16",
            "UInt32Property" => "uint32",
            "UInt64Property" => "uint64",
            "FloatProperty" => "float",
            "DoubleProperty" => "double",
            "StrProperty" => "FString",
            "NameProperty" => "FName",
            "TextProperty" => "FText",
            "StructProperty" => "F" + (of ?? "?"),
            "EnumProperty" => of ?? "?",
            "ObjectProperty" => "U" + (of ?? "Object") + "*",
            "ClassProperty" => "TSubclassOf<U" + (of ?? "Object") + ">",
            "SoftObjectProperty" => "TSoftObjectPtr<U" + (of ?? "Object") + ">",
            "SoftClassProperty" => "TSoftClassPtr<U" + (of ?? "Object") + ">",
            "WeakObjectProperty" => "TWeakObjectPtr<U" + (of ?? "Object") + ">",
            "LazyObjectProperty" => "TLazyObjectPtr<U" + (of ?? "Object") + ">",
            "InterfaceProperty" => "TScriptInterface<I" + (of ?? "?") + ">",
            "ArrayProperty" => $"TArray<{of ?? "?"}>",
            "SetProperty" => $"TSet<{of ?? "?"}>",
            "MapProperty" => $"TMap<{of ?? "?"}, ?>",
            "DelegateProperty" => "FScriptDelegate",
            "MulticastDelegateProperty" => "FMulticastScriptDelegate",
            _ => kind,
        };
    }
}
