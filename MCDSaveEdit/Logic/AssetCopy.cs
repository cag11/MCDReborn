using MCDSaveEdit.Services;
using PakReader.Pak;
using System;
using System.Collections.Generic;
using System.Text;
#nullable enable

namespace MCDSaveEdit.Logic
{
    /// <summary>
    /// One of the game's own assets, copied to a different path.
    ///
    /// Everything this project writes so far replaces an asset in place: the mesh at this path
    /// gets different geometry, the material at that path gets different numbers. Copying is a
    /// different thing and unlocks different work - putting one weapon's model on another without
    /// anybody modelling it, handing a glowing material to something dull, and putting a level
    /// where the Blueprint Loader will find it.
    ///
    /// A cooked package records the path it was cooked at, in its own name table:
    ///
    ///     [0] "/Game/Actors/Equipment/MeleeWeapons/Axe/M_Axe"    the material it uses
    ///     [1] "/Game/Actors/Equipment/MeleeWeapons/Axe/SM_Axe"   itself
    ///
    /// So a copy is that string rewritten. The difficulty is that name table entries are length
    /// prefixed and the header is a table of offsets into what follows: a longer or shorter name
    /// moves everything after it, and every one of those offsets - and every export's position in
    /// the .uexp - has to be corrected.
    ///
    /// None of which is necessary if the new name is the same length as the old one. The name of
    /// the copy is ours to choose, so it is chosen to fit: the path is padded or trimmed until it
    /// matches, and the rename becomes bytes written over bytes, with nothing moving. What that
    /// costs is a slightly odd file name and nothing else, because nothing reads these names but
    /// the engine.
    /// </summary>
    public static class AssetCopy
    {
        /// <summary>
        /// What a copy would be called, given where it is going.
        ///
        /// The folder is fixed and the name is stretched to make the whole path the same length as
        /// the original's. Padding is repeated letters rather than digits, because a trailing
        /// number reads as a draw state or a variant and this is neither.
        /// </summary>
        public static string? nameFor(string sourceAssetPath, string targetFolder, string wanted)
            => gamePathOf(sourceAssetPath) is string from
                ? nameFor(from.Length, targetFolder, wanted)
                : null;

        /// <summary>The same, for an asset whose own name is already known rather than looked up.</summary>
        public static string? nameFor(int sameLength, string targetFolder, string wanted)
        {
            var folder = gamePathOf(targetFolder.TrimEnd('/') + "/x");
            if (folder == null) { return null; }
            folder = folder.Substring(0, folder.Length - 1);   //without the placeholder

            var room = sameLength - folder.Length;
            if (room < 1) { return null; }   //no name short enough will fit

            if (wanted.Length == room) { return folder + wanted; }
            if (wanted.Length > room) { return folder + wanted.Substring(0, room); }

            return folder + wanted + new string('x', room - wanted.Length);
        }

        /// <summary>
        /// One asset written out under another name, as the files that go in a pak.
        ///
        /// Returns nothing when the package does not name itself where it is expected to, which is
        /// the one thing this relies on. Said rather than guessed at: a copy whose name was not
        /// rewritten is a file the engine will refuse, and refusing here is cheaper than finding
        /// out in the game.
        /// </summary>
        public static IEnumerable<PakWriter.Entry> copy(string sourceAssetPath, string targetAssetPath)
        {
            var entries = new List<PakWriter.Entry>();

            var paks = CustomSkins.index;
            if (paks == null) { return entries; }

            var from = gamePathOf(sourceAssetPath);
            var to = gamePathOf(targetAssetPath);
            if (from == null || to == null || from.Length != to.Length) { return entries; }

            PakReader.Pak.PakPackage package;
            try
            {
                var read = paks.extractPackage(sourceAssetPath);
                if (read == null) { return entries; }
                package = read.Value;
            }
            catch (Exception) { return entries; }

            var uasset = package.UAsset.ToArray();
            if (!rename(uasset, from, to)) { return entries; }

            var insidePak = targetAssetPath.TrimStart('/');
            entries.Add(new PakWriter.Entry(insidePak + ".uasset", uasset));
            entries.Add(new PakWriter.Entry(insidePak + ".uexp", package.UExp.ToArray()));
            if (package.UBulk != null)
            {
                entries.Add(new PakWriter.Entry(insidePak + ".ubulk", package.UBulk.Value.ToArray()));
            }

            return entries;
        }


        /// <summary>
        /// The path a cooked asset was cooked at, taken from its own name table.
        ///
        /// Found by its last part rather than by position. A package's names include everything it
        /// refers to - the materials, the classes, the packages beside it - and the one that is
        /// *itself* is the one ending in its own file name. Which is known, because the file it
        /// was read from is called that.
        /// </summary>
        public static string? selfNameIn(byte[] uasset, string fileName)
        {
            var wanted = "/" + fileName;

            foreach (var name in CookedProperties.readNamesOf(uasset))
            {
                if (!name.StartsWith("/Game/", StringComparison.OrdinalIgnoreCase)) { continue; }
                if (name.EndsWith(wanted, StringComparison.OrdinalIgnoreCase)) { return name; }
            }

            return null;
        }

        /// <summary>
        /// `/Dungeons/Content/Actors/Axe` as the engine spells it: `/Game/Actors/Axe`.
        ///
        /// Which is what the name table holds. The paks are addressed by the cooked layout and the
        /// engine by the project's own, and the two differ only in that prefix.
        /// </summary>
        private static string? gamePathOf(string assetPath)
        {
            const string cooked = "/Dungeons/Content/";

            var at = assetPath.IndexOf(cooked, StringComparison.OrdinalIgnoreCase);
            if (at < 0) { return null; }

            return "/Game/" + assetPath.Substring(at + cooked.Length);
        }

        /// <summary>
        /// Writes the new name over the old one, wherever the package says it.
        ///
        /// Every occurrence, because a package can name itself more than once - and one left
        /// behind is a reference pointing at the asset this was copied from, which is the kind of
        /// thing that works until the original is removed.
        /// </summary>
        public static bool rename(byte[] uasset, string from, string to)
        {
            if (from.Length != to.Length) { return false; }

            var was = Encoding.ASCII.GetBytes(from);
            var now = Encoding.ASCII.GetBytes(to);

            var written = 0;
            for (int at = 0; at + was.Length <= uasset.Length; at++)
            {
                var same = true;
                for (int i = 0; i < was.Length && same; i++)
                {
                    //The engine is not fussy about capitals in a path and the cooker is not
                    //consistent about them, so neither is this.
                    same = char.ToLowerInvariant((char)uasset[at + i]) == char.ToLowerInvariant((char)was[i]);
                }
                if (!same) { continue; }

                now.CopyTo(uasset, at);
                written++;
                at += was.Length - 1;
            }

            return written > 0;
        }
    }
}
