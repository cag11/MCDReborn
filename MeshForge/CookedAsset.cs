using System;
using System.IO;
using System.Text;

namespace MeshForge
{
    /// <summary>
    /// Enough of a cooked .uasset header to see what is in one.
    ///
    /// A cooked asset is a summary, then a table of the names it uses, then a table of what it
    /// imports and a table of what it exports. The geometry of a mesh is not here - it is in the
    /// .uexp beside it - but the export table says how long each export is and where it starts,
    /// which is what has to be corrected if the geometry is ever rewritten. Reading it first is
    /// how you find out whether that correction is one number or twenty.
    ///
    /// Written out by hand rather than reached for through a library because PakReader, which
    /// this app already leans on, parses textures and sounds and stops there: it has no mesh
    /// support of any kind. Whatever ends up doing the geometry, the header has to be understood
    /// either way.
    ///
    /// The layout is UE4's FPackageFileSummary. Only the parts that are needed are read, and the
    /// offsets are taken from the summary rather than assumed, because a cooked package leaves
    /// several of the editor-only sections out entirely.
    /// </summary>
    public static class CookedAsset
    {
        //A cooked package starts with this, backwards. Anything else is not one.
        private const uint PACKAGE_MAGIC = 0x9E2A83C1;

        public static void describe(byte[] uasset)
        {
            using var stream = new MemoryStream(uasset);
            using var reader = new BinaryReader(stream, Encoding.UTF8, leaveOpen: true);

            var magic = reader.ReadUInt32();
            if (magic != PACKAGE_MAGIC)
            {
                Console.WriteLine($"  not a cooked package: starts 0x{magic:X8}, expected 0x{PACKAGE_MAGIC:X8}");
                return;
            }

            var legacyVersion = reader.ReadInt32();
            Console.WriteLine($"  legacy file version  {legacyVersion}");

            //The versions the package was saved with. A negative legacy version means the older
            //UE3 field is still there and has to be stepped over.
            if (legacyVersion != -4) { reader.ReadInt32(); }
            var fileVersionUE4 = reader.ReadInt32();
            var fileVersionLicensee = reader.ReadInt32();
            Console.WriteLine($"  ue4 version          {fileVersionUE4} (4.22 saves 514)");
            Console.WriteLine($"  licensee version     {fileVersionLicensee}");

            //Custom versions: a count, then that many guid + number pairs.
            var customVersionCount = reader.ReadInt32();
            for (int i = 0; i < customVersionCount; i++)
            {
                reader.ReadBytes(16);
                reader.ReadInt32();
            }
            Console.WriteLine($"  custom versions      {customVersionCount}");

            var totalHeaderSize = reader.ReadInt32();
            var folderName = readString(reader);
            var packageFlags = reader.ReadUInt32();
            Console.WriteLine($"  header size          {totalHeaderSize:N0} bytes");
            Console.WriteLine($"  folder               {(string.IsNullOrEmpty(folderName) ? "(none)" : folderName)}");
            Console.WriteLine($"  package flags        0x{packageFlags:X8}");

            var nameCount = reader.ReadInt32();
            var nameOffset = reader.ReadInt32();
            Console.WriteLine($"  names                {nameCount} at {nameOffset:N0}");

            //Cooked packages skip the localisation id and the gatherable text section.
            reader.ReadInt32(); // gatherable text count
            reader.ReadInt32(); // gatherable text offset

            var exportCount = reader.ReadInt32();
            var exportOffset = reader.ReadInt32();
            var importCount = reader.ReadInt32();
            var importOffset = reader.ReadInt32();
            Console.WriteLine($"  exports              {exportCount} at {exportOffset:N0}");
            Console.WriteLine($"  imports              {importCount} at {importOffset:N0}");

            if (nameCount > 0 && nameOffset > 0 && nameOffset < uasset.Length)
            {
                Console.WriteLine();
                Console.WriteLine("  names it uses, which say what kind of thing this is:");
                stream.Seek(nameOffset, SeekOrigin.Begin);
                var shown = 0;
                for (int i = 0; i < nameCount && stream.Position < uasset.Length; i++)
                {
                    var name = readString(reader);
                    //Each name is followed by two hashes in a cooked package.
                    if (stream.Position + 4 <= uasset.Length) { reader.ReadUInt32(); }

                    if (interesting(name) && shown < 16)
                    {
                        Console.WriteLine($"    {name}");
                        shown++;
                    }
                }
                if (shown == 0) { Console.WriteLine("    (none stood out)"); }
            }
        }

        /// <summary>
        /// The names worth printing: the classes and the buffers, not the hundreds of numbers and
        /// property names that make up the rest of the table.
        /// </summary>
        private static bool interesting(string name)
        {
            if (string.IsNullOrWhiteSpace(name)) { return false; }
            foreach (var mark in new[] { "StaticMesh", "VertexBuffer", "IndexBuffer", "Material", "Bounds", "LOD", "Section", "Body" })
            {
                if (name.IndexOf(mark, StringComparison.OrdinalIgnoreCase) >= 0) { return true; }
            }
            return false;
        }

        /// <summary>
        /// An FString: a length, then the bytes, with the terminator counted in the length. A
        /// negative length means it is UTF-16, which cooked packages use for anything non-ascii.
        /// </summary>
        private static string readString(BinaryReader reader)
        {
            var length = reader.ReadInt32();
            if (length == 0) { return string.Empty; }

            if (length < 0)
            {
                var wide = reader.ReadBytes(-length * 2);
                return Encoding.Unicode.GetString(wide).TrimEnd('\0');
            }

            var bytes = reader.ReadBytes(length);
            return Encoding.UTF8.GetString(bytes).TrimEnd('\0');
        }
    }
}
