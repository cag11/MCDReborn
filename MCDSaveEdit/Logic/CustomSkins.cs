using MCDSaveEdit.Services;
using PakReader.Pak;
using PakReader.Parsers.Class;
using PakReader.Parsers.Objects;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Windows.Media;
using System.Windows.Media.Imaging;
#nullable enable

namespace MCDSaveEdit.Logic
{
    /// <summary>
    /// Replacing a piece of the game's artwork with your own.
    ///
    /// The save file has nothing to do with this. A cosmetic or an armour's appearance lives
    /// entirely in the game's paks, so changing how something looks means putting a different
    /// texture in front of the original - which is what a mod pak does. It sits alongside the
    /// game's own paks with a file at the same asset path, and the engine loads it instead.
    ///
    /// The original is never touched. Removing the mod pak is the whole of "undo", which is
    /// why there is no backup here and nothing to restore.
    ///
    /// Three steps, each proven before this existed:
    ///   1. read the texture out of the paks and save it as a PNG
    ///   2. write new pixels back into a copy of the asset
    ///   3. pack that copy into a pak the engine will load
    /// </summary>
    public static class CustomSkins
    {
        /// <summary>Where to go and recolour a texture once it has been exported.</summary>
        public const string DESIGNER_URL = "https://mcddesigner.vercel.app/";

        /// <summary>
        /// Mods this app makes are named so they can be told apart from the game's own paks and
        /// from anyone else's. The _P suffix is Unreal's own convention for a patch pak, which
        /// is what gives it priority over the base content.
        /// </summary>
        public const string MOD_PREFIX = "MCDReborn_";
        public const string MOD_SUFFIX = "_P.pak";

        /// <summary>
        /// Where mods go, by community convention and by every install guide for this game:
        /// a "~mods" folder inside the game's own Paks folder. The tilde is not decoration -
        /// the engine mounts what it finds in alphabetical order, and "~" sorts after every
        /// letter, so a mod there is loaded last and therefore wins.
        ///
        /// It is the same folder on Steam, on the Minecraft Launcher and on the Xbox app. Only
        /// the path to Paks differs between them, and that is already found for us.
        /// </summary>
        public const string MODS_FOLDER = "~mods";

        public sealed class InstalledMod
        {
            public string Name { get; }
            public string Path { get; }
            public long Size { get; }
            public DateTime Installed { get; }

            /// <summary>True for a pak the user brought themselves rather than one made here.</summary>
            public bool Manual { get; }

            /// <summary>
            /// A pak this app manages on the user's behalf rather than one they installed.
            ///
            /// The armour switch is the case: it is written and deleted by a checkbox, so listing
            /// it beside hand-installed mods invites someone to remove it there and wonder why
            /// the checkbox still says armour is hidden. The "~" that makes it load last is also
            /// what marks it, so anything named that way is ours.
            ///
            /// A mod the user brought themselves is never internal, whatever it is called - they
            /// put it there, so they get to see it and remove it.
            /// </summary>
            public bool Internal => !Manual && Name.StartsWith("~", StringComparison.Ordinal);

            public InstalledMod(string path, bool manual = false)
            {
                Path = path;
                Manual = manual;
                var file = new FileInfo(path);
                Size = file.Exists ? file.Length : 0;
                Installed = file.Exists ? file.LastWriteTime : DateTime.MinValue;
                var name = System.IO.Path.GetFileName(path);
                if (manual)
                {
                    //Someone else named this one; show it as it is, minus the extension.
                    Name = System.IO.Path.GetFileNameWithoutExtension(path);
                    return;
                }
                if (name.StartsWith(MOD_PREFIX, StringComparison.Ordinal)) { name = name.Substring(MOD_PREFIX.Length); }
                if (name.EndsWith(MOD_SUFFIX, StringComparison.Ordinal)) { name = name.Substring(0, name.Length - MOD_SUFFIX.Length); }
                Name = name;
            }
        }

        /// <summary>The mounted paks, or null when the game content has not been loaded.</summary>
        public static PakIndex? index =>
            (ImageResolver.instance as PakContentResolver)?.pakIndex;

        /// <summary>Where the game's paks live, which is also where a mod has to go.</summary>
        public static string? paksFolder =>
            (ImageResolver.instance as PakContentResolver)?.path;

        public static bool ready => index != null && !string.IsNullOrWhiteSpace(paksFolder);

        /// <summary>
        /// The colour texture for one piece of equipment.
        ///
        /// Found by looking rather than by building a path: a unique's texture does not
        /// always carry its own id - Fox Armour's lives under WolfArmor_Unique1 and is called
        /// T_FoxArmor - so the folder is what is known and the name is whatever is in it.
        /// The maps that are not colour (_S is specular, _Icon is the inventory sprite) are
        /// skipped.
        /// </summary>
        public static string? textureFor(string itemId)
        {
            var paks = index;
            if (paks == null) { return null; }

            var folder = $"/{itemId}/";
            var candidates = new List<string>();
            foreach (var entry in paks)
            {
                if (entry.IndexOf(folder, StringComparison.OrdinalIgnoreCase) < 0) { continue; }
                var name = System.IO.Path.GetFileName(entry);
                if (!name.StartsWith("T_", StringComparison.Ordinal)) { continue; }
                if (!isColourMap(name)) { continue; }

                //A unique named after the item is the sure thing when it exists.
                if (string.Equals(name, "T_" + itemId, StringComparison.OrdinalIgnoreCase)) { return assetPath(entry); }
                candidates.Add(entry);
            }

            //Otherwise the shortest name, which is the one carrying no extra suffix. Ordering by
            //name as a tiebreak keeps the choice the same from one run to the next.
            return candidates
                .OrderBy(entry => System.IO.Path.GetFileName(entry).Length)
                .ThenBy(entry => entry, StringComparer.Ordinal)
                .Select(assetPath)
                .FirstOrDefault();
        }

        /// <summary>
        /// Whether a texture name looks like the colour map rather than one of the maps that
        /// describe a surface instead of colouring it, or an inventory sprite.
        ///
        /// "icon" is matched anywhere rather than as "_Icon": Shadow Walker's sprite is
        /// T_OcelotArmor_Unique1_Gearicon, where the "icon" carries no underscore of its own,
        /// and matching the stricter spelling let a 256x256 inventory sprite through as if it
        /// were the armour.
        /// </summary>
        internal static bool isColourMap(string name)
        {
            if (name.IndexOf("icon", StringComparison.OrdinalIgnoreCase) >= 0) { return false; }

            //_S specular, _N normal, _M metallic, _E emissive, _AO ambient occlusion, _ORM packed.
            foreach (var suffix in new[] { "_S", "_N", "_M", "_E", "_R", "_AO", "_ORM" })
            {
                if (name.EndsWith(suffix, StringComparison.Ordinal)) { return false; }
            }
            return true;
        }

        /// <summary>
        /// The index yields entries carrying the mount point - "//Dungeons/Content/..." - but
        /// everything that reads an asset wants it dropped. Handing the raw entry onwards makes
        /// every lookup miss, silently, and look like the asset is not there.
        /// </summary>
        internal static string assetPath(string indexEntry)
        {
            var start = indexEntry.IndexOf("//", StringComparison.Ordinal);
            return start < 0 ? indexEntry : indexEntry.Substring(start + 1);
        }

        public static BitmapSource? preview(string assetPath)
            => ImageResolver.instance.imageSource(assetPath);

        /// <summary>Saves a texture as a PNG so it can be taken away and recoloured.</summary>
        public static void exportTexture(string assetPath, string outputPath)
        {
            var image = ImageResolver.instance.imageSource(assetPath)
                ?? throw new InvalidOperationException($"No texture at {assetPath}.");

            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(image));
            using var file = File.Create(outputPath);
            encoder.Save(file);
        }

        /// <summary>
        /// Builds and installs a mod pak that replaces one texture with the given PNG.
        /// Returns the pak that was written.
        ///
        /// The PNG has to be exactly the texture's own size. Rescaling would be the wrong
        /// favour: the payload is a fixed number of bytes, and a resample of pixel art looks
        /// like a mistake even when it fits.
        /// </summary>
        public static InstalledMod apply(string assetPath, string pngPath, string modName)
            => apply(assetPath, decodePng(pngPath), modName);

        /// <summary>
        /// The same thing from pixels already in hand - what comes back from the designer over a
        /// link, with no file ever written.
        /// </summary>
        public static InstalledMod apply(string assetPath, BitmapSource replacementImage, string modName)
            => writePak(modName, patchTexture(assetPath, (w, h) => toBgra(replacementImage, w, h)));

        /// <summary>
        /// Pixels that make a texture render as nothing.
        ///
        /// All four channels zero, so every pixel is fully transparent. Armour materials honour
        /// alpha - that is why a Wolf Armour sheet is 77% clear and the hero shows through it -
        /// so a sheet that is entirely clear leaves the armour with nothing to draw.
        /// </summary>
        public static byte[] invisiblePixels(int width, int height) => new byte[width * height * 4];

        /// <summary>
        /// One pak holding several patched textures.
        ///
        /// Everything the engine should load has to arrive together: a hero skin and the armour
        /// hidden to reveal it are one change, and splitting them across two paks would let a
        /// user end up with half of it.
        /// </summary>
        public static InstalledMod applyMany(
            IEnumerable<(string assetPath, Func<int, int, byte[]> pixels)> textures, string modName)
        {
            var entries = new List<PakWriter.Entry>();
            foreach (var (assetPath, pixels) in textures)
            {
                entries.AddRange(patchTexture(assetPath, pixels));
            }
            return writePak(modName, entries);
        }

        /// <summary>
        /// A mod pak from files somebody else assembled.
        ///
        /// Reshaped meshes are built elsewhere but installed exactly like a recoloured texture -
        /// same folder, same naming, same list of installed mods - so they share this rather than
        /// growing a second way to write a pak that would drift from this one.
        /// </summary>
        public static InstalledMod writeModPak(string modName, IEnumerable<PakWriter.Entry> entries)
            => writePak(modName, entries);

        private static InstalledMod writePak(string modName, IEnumerable<PakWriter.Entry> entries)
        {
            var folder = paksFolder ?? throw new InvalidOperationException("No paks folder.");
            var pakPath = System.IO.Path.Combine(folder, MOD_PREFIX + safeName(modName) + MOD_SUFFIX);
            PakWriter.write(pakPath, entries.ToList());
            return new InstalledMod(pakPath);
        }

        /// <summary>
        /// The files of one asset, with its pixels replaced by whatever the caller supplies for
        /// that texture's size. The size is not known until the asset has been read, which is
        /// why this takes a function rather than the bytes.
        /// </summary>
        /// <summary>
        /// That the rewritten asset still reads as the texture it was, holding the new pixels.
        ///
        /// Every part of it is checked against what was already known rather than against itself:
        /// the size and the format come from the asset as it was before, and the pixels are the
        /// ones just written. An asset that agrees with all three is one the game can load.
        /// </summary>
        private static void confirm(byte[] uasset, byte[] uexp, ArraySegment<byte>? ubulk,
            FTexturePlatformData was, byte[] written, string assetPath)
        {
            var name = System.IO.Path.GetFileName(assetPath);

            UTexture2D? texture;
            try
            {
                var package = new PakPackage(new ArraySegment<byte>(uasset), new ArraySegment<byte>(uexp), ubulk);
                texture = package.GetExport<UTexture2D>();
            }
            catch (Exception problem)
            {
                throw new InvalidOperationException(
                    $"{name} could not be rewritten - putting the artwork in left a file that no longer reads as a texture ({problem.Message}).");
            }

            if (texture == null || texture.PlatformDatas.Length == 0)
            {
                throw new InvalidOperationException(
                    $"{name} could not be rewritten - putting the artwork in left a file that no longer reads as a texture.");
            }

            var now = texture.PlatformDatas[0];
            if (now.SizeX != was.SizeX || now.SizeY != was.SizeY || now.PixelFormat != was.PixelFormat)
            {
                throw new InvalidOperationException(
                    $"{name} could not be rewritten - it came back describing a different texture than it was.");
            }

            var pixels = now.Mips.Length > 0 ? now.Mips[0].BulkData.Data : null;
            if (pixels == null || pixels.Length != written.Length)
            {
                throw new InvalidOperationException(
                    $"{name} could not be rewritten - the artwork did not land where its pixels are kept.");
            }

            for (int i = 0; i < written.Length; i++)
            {
                if (pixels[i] == written[i]) { continue; }

                throw new InvalidOperationException(
                    $"{name} could not be rewritten - the artwork did not land where its pixels are kept.");
            }
        }

        private static List<PakWriter.Entry> patchTexture(string assetPath, Func<int, int, byte[]> makePixels)
        {
            var paks = index ?? throw new InvalidOperationException("Game content is not loaded.");

            var package = paks.extractPackage(assetPath)
                ?? throw new InvalidOperationException($"Could not read {assetPath}.");
            var texture = package.GetExport<UTexture2D>()
                ?? throw new InvalidOperationException($"{assetPath} is not a texture.");

            var platform = texture.PlatformDatas[0];
            if (platform.PixelFormat != EPixelFormat.PF_B8G8R8A8)
            {
                //Everything seen so far is uncompressed, which is what lets the pixels be
                //swapped byte for byte. A compressed one would need re-encoding first.
                throw new InvalidOperationException(
                    $"{System.IO.Path.GetFileName(assetPath)} is stored as {platform.PixelFormat}, which this cannot rewrite yet.");
            }

            var original = platform.Mips[0].BulkData.Data
                ?? throw new InvalidOperationException("That texture has no pixel data.");
            var replacement = makePixels(platform.SizeX, platform.SizeY);
            if (replacement.Length != original.Length)
            {
                throw new InvalidOperationException(
                    $"That PNG gives {replacement.Length} bytes; the texture holds {original.Length}.");
            }

            //Locate the payload rather than compute an offset, so an asset laid out differently
            //fails loudly instead of being quietly corrupted.
            var uexp = package.UExp.ToArray();
            var offset = indexOf(uexp, original);
            if (offset < 0) { throw new InvalidOperationException("Could not find the pixels inside the asset."); }

            //And only where they are in one place. A small texture of mostly one colour can match
            //somewhere it is not, and then this writes a picture over whatever else lived there -
            //which does not fail, it produces an asset the game loads as nothing. That is what
            //reached somebody's game: a weapon drawn black and see-through, from a texture that no
            //longer parsed at all.
            if (indexOf(uexp, original, offset + 1) >= 0)
            {
                throw new InvalidOperationException(
                    $"{System.IO.Path.GetFileName(assetPath)} holds its pixels in a pattern that appears more than once, "
                    + "so where to write them cannot be told for certain.");
            }

            Buffer.BlockCopy(replacement, 0, uexp, offset, replacement.Length);

            //Then read back what is about to be shipped, the way the game will read it.
            //
            //Writing into a file by searching it is a reasonable way to find something and a poor
            //way to be sure of it, and the difference only shows up later, on somebody else's
            //screen. Parsing the result costs a millisecond and turns every way this can go wrong -
            //the wrong offset, a field clipped, a format this misread - into a refusal here.
            confirm(package.UAsset.ToArray(), uexp, package.UBulk, platform, replacement, assetPath);

            //Paths inside a pak are relative to the mount point and carry no leading slash.
            var insidePak = assetPath.TrimStart('/');
            var entries = new List<PakWriter.Entry> {
                new PakWriter.Entry(insidePak + ".uasset", package.UAsset.ToArray()),
                new PakWriter.Entry(insidePak + ".uexp", uexp),
            };
            if (package.UBulk != null)
            {
                entries.Add(new PakWriter.Entry(insidePak + ".ubulk", package.UBulk.Value.ToArray()));
            }
            return entries;
        }

        /// <summary>The ~mods folder for this install, or null when the paks folder is unknown.</summary>
        public static string? modsFolder =>
            paksFolder == null ? null : System.IO.Path.Combine(paksFolder, MODS_FOLDER);

        /// <summary>
        /// Makes the ~mods folder if it is not there yet and returns it.
        ///
        /// Creating it is the whole of "setting up mods" for this game, and it is the step most
        /// people get wrong - usually by dropping the tilde.
        /// </summary>
        public static string ensureModsFolder()
        {
            var folder = modsFolder ?? throw new InvalidOperationException("The game's paks folder is not known.");
            Directory.CreateDirectory(folder);
            return folder;
        }

        /// <summary>
        /// Copies someone else's pak into ~mods. Returns what was installed.
        ///
        /// Nothing is unpacked, inspected or changed - the file is put where the engine looks
        /// and that is all. Removing it again is deleting it, the same as for a skin made here.
        /// </summary>
        public static InstalledMod installPak(string sourcePath, bool overwrite = false)
        {
            if (!File.Exists(sourcePath)) { throw new FileNotFoundException("No such file.", sourcePath); }
            if (!looksLikePak(sourcePath, out _))
            {
                throw new InvalidOperationException(
                    $"{System.IO.Path.GetFileName(sourcePath)} is not a pak file.");
            }

            var folder = ensureModsFolder();
            var target = System.IO.Path.Combine(folder, System.IO.Path.GetFileName(sourcePath));
            if (File.Exists(target) && !overwrite)
            {
                throw new IOException($"{System.IO.Path.GetFileName(target)} is already in {MODS_FOLDER}.");
            }

            //Copying rather than moving: the file the user picked is theirs, and a mod manager
            //that eats the download is a mod manager people stop trusting.
            File.Copy(sourcePath, target, overwrite);
            return new InstalledMod(target, manual: true);
        }

        /// <summary>
        /// Whether a file is an Unreal pak, judged by the magic in its footer.
        ///
        /// The footer grew with almost every pak version - 44 bytes originally, then a byte for
        /// the encrypted-index flag, then sixteen for an encryption GUID, then a block of
        /// compression method names - so a fixed offset only recognises the one version it was
        /// written for. Searching the tail backwards costs nothing and accepts all of them.
        /// </summary>
        public static bool looksLikePak(string path, out int version)
        {
            version = 0;
            const uint PAK_MAGIC = 0x5A6F12E1;

            using var file = File.OpenRead(path);
            var length = (int)Math.Min(file.Length, 1024);
            if (length < 8) { return false; }

            file.Seek(-length, SeekOrigin.End);
            var tail = new byte[length];
            file.ReadExactly(tail, 0, length);

            for (int at = length - 8; at >= 0; at--)
            {
                if (BitConverter.ToUInt32(tail, at) != PAK_MAGIC) { continue; }
                version = BitConverter.ToInt32(tail, at + 4);
                return true;
            }
            return false;
        }

        /// <summary>
        /// Every mod this app can see, newest first: the skins it made, and whatever the user
        /// has put in ~mods themselves.
        ///
        /// Both are listed because both are loaded by the game, and a list that showed only half
        /// of what is installed would be worse than no list.
        /// </summary>
        public static IReadOnlyList<InstalledMod> installed()
        {
            var folder = paksFolder;
            if (string.IsNullOrWhiteSpace(folder) || !Directory.Exists(folder)) { return Array.Empty<InstalledMod>(); }

            var mods = Directory.EnumerateFiles(folder, MOD_PREFIX + "*" + MOD_SUFFIX)
                .Select(path => new InstalledMod(path));

            var mine = modsFolder;
            if (mine != null && Directory.Exists(mine))
            {
                mods = mods.Concat(Directory
                    .EnumerateFiles(mine, "*.pak")
                    .Select(path => new InstalledMod(path, manual: true)));
            }

            return mods.OrderByDescending(mod => mod.Installed).ToList();
        }

        /// <summary>
        /// Uninstalling is deleting the mod pak. The game's own files were never modified, so
        /// there is nothing to put back.
        /// </summary>
        public static void remove(InstalledMod mod) => File.Delete(mod.Path);

        /// <summary>
        /// Keeps a user-typed name usable as a filename.
        ///
        /// "~" survives because it carries meaning here: the engine mounts paks in alphabetical
        /// order and the last one wins, and "~" sorts after every letter. A mod that has to beat
        /// the others is named with one, which is the same reason the community's mods folder is
        /// called "~mods".
        /// </summary>
        public static string safeName(string name)
        {
            var cleaned = new string(name.Select(c => char.IsLetterOrDigit(c) || c == '~' ? c : '_').ToArray()).Trim('_');
            return cleaned.Length == 0 ? "Skin" : cleaned;
        }


        /// <summary>
        /// The files that put an imported model's own artwork onto the texture its weapon uses.
        ///
        /// An imported mesh brings its own texture coordinates, which have nothing to do with how
        /// the game's artist laid theirs out. So the new shape wearing the old texture is not a
        /// slightly wrong result, it is a meaningless one - the two have to travel together, which
        /// is why this returns pak entries to be written alongside the mesh rather than a pak of
        /// its own.
        ///
        /// The image is rescaled to the texture it replaces. That is the opposite of the rule for
        /// recolouring, where a resample of somebody's pixel art would be an unwanted favour and a
        /// size mismatch is worth refusing over. Here the source is a photograph-sized render from
        /// a modelling tool and the target is whatever the game happens to use, so the sizes will
        /// essentially never match and refusing would mean refusing every model.
        /// </summary>
        public static IEnumerable<PakWriter.Entry> texturePatchFor(string meshAssetPath, byte[] png)
        {
            var texture = textureBeside(meshAssetPath);
            if (texture == null) { return Array.Empty<PakWriter.Entry>(); }

            return texturePatchAt(texture, png);
        }

        /// <summary>The same, onto a texture the caller has already chosen.</summary>
        public static IEnumerable<PakWriter.Entry> texturePatchAt(string textureAssetPath, byte[] png)
        {
            var image = decodePng(png);
            return patchTexture(textureAssetPath, (width, height) => toBgra(scaled(image, width, height), width, height));
        }

        /// <summary>
        /// The colour texture kept in the same folder as a mesh.
        ///
        /// Found by looking rather than by building a name, for the same reason the item version
        /// of this does: a weapon's texture is not reliably named after its mesh. The maps that
        /// are not colour - specular, the inventory icon - are skipped.
        /// </summary>
        public static string? textureBeside(string meshAssetPath)
        {
            var paks = index;
            if (paks == null) { return null; }

            var cut = meshAssetPath.LastIndexOf('/');
            if (cut <= 0) { return null; }
            var folder = meshAssetPath.Substring(0, cut + 1);

            var candidates = new List<string>();
            foreach (var entry in paks)
            {
                var path = assetPath(entry);
                if (!path.StartsWith(folder, StringComparison.OrdinalIgnoreCase)) { continue; }

                var name = System.IO.Path.GetFileName(path);
                if (!name.StartsWith("T_", StringComparison.OrdinalIgnoreCase)) { continue; }
                if (!isColourMap(name)) { continue; }
                candidates.Add(path);
            }

            //The shortest name is the one carrying no extra suffix, and ordering by name as a
            //tiebreak keeps the choice the same from one run to the next.
            return candidates
                .OrderBy(path => System.IO.Path.GetFileName(path).Length)
                .ThenBy(path => path, StringComparer.Ordinal)
                .FirstOrDefault();
        }

        /// <summary>An image out of PNG bytes, for artwork that never touches the disk.</summary>
        public static BitmapSource imageFromPng(byte[] png) => decodePng(png);

        private static BitmapSource decodePng(byte[] png)
        {
            using var stream = new MemoryStream(png);
            var decoder = BitmapDecoder.Create(stream,
                BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
            return decoder.Frames[0];
        }

        private static BitmapSource scaled(BitmapSource image, int width, int height)
        {
            if (image.PixelWidth == width && image.PixelHeight == height) { return image; }

            var scale = new TransformedBitmap(image,
                new System.Windows.Media.ScaleTransform(
                    (double)width / image.PixelWidth,
                    (double)height / image.PixelHeight));

            //Rounding can leave the result a pixel out, and the texture wants exactly its own
            //size, so anything left over is cropped rather than allowed through.
            if (scale.PixelWidth == width && scale.PixelHeight == height) { return scale; }

            var cropWidth = Math.Min(width, scale.PixelWidth);
            var cropHeight = Math.Min(height, scale.PixelHeight);
            return new CroppedBitmap(scale, new System.Windows.Int32Rect(0, 0, cropWidth, cropHeight));
        }

        private static BitmapSource decodePng(string pngPath)
        {
            if (!File.Exists(pngPath)) { throw new FileNotFoundException("No such PNG.", pngPath); }

            var decoder = new PngBitmapDecoder(
                new Uri(System.IO.Path.GetFullPath(pngPath)),
                BitmapCreateOptions.PreservePixelFormat,
                BitmapCacheOption.OnLoad);
            return decoder.Frames[0];
        }

        private static byte[] toBgra(BitmapSource image, int width, int height)
        {
            if (image.PixelWidth != width || image.PixelHeight != height)
            {
                throw new InvalidOperationException(
                    $"That image is {image.PixelWidth}×{image.PixelHeight}; this texture is {width}×{height}.");
            }

            BitmapSource source = image.Format == PixelFormats.Bgra32
                ? image
                : new FormatConvertedBitmap(image, PixelFormats.Bgra32, null, 0);
            var stride = width * 4;
            var bytes = new byte[stride * height];
            source.CopyPixels(bytes, stride, 0);
            return bytes;
        }

        private static int indexOf(byte[] haystack, byte[] needle, int from = 0)
        {
            if (needle.Length == 0 || haystack.Length < needle.Length) { return -1; }
            for (int i = Math.Max(0, from); i <= haystack.Length - needle.Length; i++)
            {
                if (haystack[i] != needle[0]) { continue; }
                int j = 1;
                while (j < needle.Length && haystack[i + j] == needle[j]) { j++; }
                if (j == needle.Length) { return i; }
            }
            return -1;
        }
    }
}
