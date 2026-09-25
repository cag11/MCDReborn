using MCDSaveEdit.Services;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
#nullable enable

namespace MCDSaveEdit.Logic
{
    /// <summary>
    /// New enchantments and new items, under ids the game has never shipped.
    ///
    /// The game keeps both one-folder-per-id, and has no table listing either: an enchantment is
    /// `Components/Enchantments/&lt;Id&gt;/BP_&lt;Id&gt;` - an EMPTY blueprint, 1 KB, whose parent is
    /// the game's own C++ class - and an item is a folder holding `BP_&lt;Id&gt;Storable`. The
    /// only data tables in the paks are Cosmetics, Skins, the Endless Struggle scaling and a
    /// preload list holding arrows and the health potion. So the one question that decides both
    /// features is whether the game finds a thing by where it is, or by a list compiled into its
    /// C++ - and only the game can answer it. This makes the thing it answers about.
    ///
    /// A copy is a whole source folder written under a new id, with every name that belongs to
    /// the copy renamed and nothing else: the parent class (`/Script/Dungeons.Stunning`) stays,
    /// and so does anything in a subfolder that was not copied, which keeps pointing at the
    /// original. The new id may be any length: PackageRename rebuilds the name table and the
    /// asset registry block and moves every header offset after them.
    /// </summary>
    public static class NewContent
    {
        public sealed class Made
        {
            public Made(string id, IReadOnlyList<PakWriter.Entry> entries, IReadOnlyList<string> files,
                string gameFrom, Func<string, string?> rename)
            {
                Id = id;
                Entries = entries;
                Files = files;
                GameFrom = gameFrom;
                Rename = rename;
            }

            /// <summary>The source folder as the engine spells it, `/Game/...`.</summary>
            public string GameFrom { get; }

            /// <summary>What every name belonging to the copy becomes - the same rule the
            /// packages were renamed with, for the asset registry to follow.</summary>
            public Func<string, string?> Rename { get; }

            public string Id { get; }
            public IReadOnlyList<PakWriter.Entry> Entries { get; }
            public IReadOnlyList<string> Files { get; }
        }

        // ------------------------------------------------------------------ cloning a folder

        /// <summary>
        /// Every asset directly inside <paramref name="sourceFolder"/>, written under
        /// <paramref name="newId"/>. `sourceFolder` is cooked-spelled -
        /// `/Dungeons/Content/Components/Enchantments/Stunning` - and its last part is the id.
        /// </summary>
        /// <remarks>
        /// <paramref name="ownsBareId"/> is the difference between the two kinds of thing.
        ///
        /// An ITEM carries its own id inside it: `ItemIdName` and `ItemId` on both the Storable and
        /// the Instance point at a name-table entry that is just "Katana_Unique1". Leave it and the
        /// copy is the Dark Katana living in another folder, whatever the folder is called. So for
        /// an item the bare id is renamed too.
        ///
        /// An ENCHANTMENT does not - BP_Stunning stores nothing at all - and the bare "Stunning" in
        /// its name table is its parent, `/Script/Dungeons.Stunning`, the C++ class that does the
        /// work. Renaming that would point the copy at a class that does not exist.
        /// </remarks>
        public static Made? cloneFolder(string sourceFolder, string newId, bool ownsBareId = false,
            IReadOnlyDictionary<string, string>? alsoRename = null)
        {
            var paks = CustomSkins.index;
            if (paks == null) { return null; }

            sourceFolder = sourceFolder.TrimEnd('/');
            var oldId = sourceFolder.Substring(sourceFolder.LastIndexOf('/') + 1);
            //Any length: PackageRename rebuilds the header around a spelling that grew or shrank.
            if (oldId.Length == 0 || newId.Length == 0) { return null; }

            var targetFolder = sourceFolder.Substring(0, sourceFolder.Length - oldId.Length) + newId;

            //What is directly in the folder, by package. The index spells the game's own paths
            //with a doubled root ("/Dungeons/Content//Dungeons/Content/..."), so it is matched
            //on the part that matters.
            var want = sourceFolder.Replace("/Dungeons/Content/", string.Empty).TrimStart('/') + "/";
            var packages = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (var entry in paks.AllEntries())
            {
                var key = entry.Key;
                var at = key.IndexOf(want, StringComparison.OrdinalIgnoreCase);
                if (at < 0) { continue; }

                var rest = key.Substring(at + want.Length);
                if (rest.Length == 0 || rest.Contains('/')) { continue; }   //subfolders stay behind

                var dot = rest.IndexOf('.');
                packages.Add(dot >= 0 ? rest.Substring(0, dot) : rest);
            }

            if (packages.Count == 0) { return null; }

            //Every name that belongs to the copy, as the engine spells it, and what it becomes.
            var gameFrom = "/Game/" + sourceFolder.Substring(sourceFolder.IndexOf("/Dungeons/Content/",
                StringComparison.OrdinalIgnoreCase) + "/Dungeons/Content/".Length);
            var gameTo = gameFrom.Substring(0, gameFrom.Length - oldId.Length) + newId;

            var swaps = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            var renamed = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

            foreach (var name in packages)
            {
                var now = replaceId(name, oldId, newId);
                renamed[name] = now;

                swaps[gameFrom + "/" + name] = gameTo + "/" + now;
                swaps[name] = now;
                swaps[name + "_C"] = now + "_C";
                swaps["Default__" + name + "_C"] = "Default__" + now + "_C";
            }

            if (ownsBareId) { swaps[oldId] = newId; }

            //The folder itself, which the asset registry records as each asset's package path.
            swaps[gameFrom] = gameTo;

            //An item whose folder is not its id - Pickaxe_Unique2 lives in Pickaxe_Unique2_Steel -
            //carries its bare id separately, and the caller says what that becomes.
            foreach (var pair in alsoRename ?? new Dictionary<string, string>()) { swaps[pair.Key] = pair.Value; }

            var entries = new List<PakWriter.Entry>();
            var files = new List<string>();

            foreach (var name in packages)
            {
                PakReader.Pak.PakPackage package;
                try
                {
                    var read = paks.extractPackage(sourceFolder + "/" + name);
                    if (read == null) { continue; }
                    package = read.Value;
                }
                catch (Exception) { continue; }

                var uasset = PackageRename.rename(package.UAsset.ToArray(),
                    text => renamedOf(text, swaps), out var changed);
                if (uasset == null || changed == 0) { continue; }

                var inside = (targetFolder + "/" + renamed[name]).TrimStart('/');
                entries.Add(new PakWriter.Entry(inside + ".uasset", uasset));
                entries.Add(new PakWriter.Entry(inside + ".uexp", package.UExp.ToArray()));
                if (package.UBulk != null)
                {
                    entries.Add(new PakWriter.Entry(inside + ".ubulk", package.UBulk.Value.ToArray()));
                }
                files.Add(renamed[name]);
            }

            return files.Count == 0 ? null
                : new Made(newId, entries, files, gameFrom, text => renamedOf(text, swaps));
        }

        /// <summary>The id swapped wherever it appears in a file name, case and all.</summary>
        private static string replaceId(string name, string oldId, string newId)
        {
            var at = name.IndexOf(oldId, StringComparison.OrdinalIgnoreCase);
            return at < 0 ? name : name.Substring(0, at) + newId + name.Substring(at + oldId.Length);
        }

        // ------------------------------------------------------------------ the name table

        /// <summary>
        /// Renames name-table entries in place, and gives each one it touched the hashes the
        /// engine will expect of its new spelling.
        ///
        /// An entry is renamed when it IS one of the keys, or is one of them followed by a dot -
        /// the form a soft reference takes, `/Game/.../BP_X.BP_X_C`. Nothing is renamed because it
        /// merely contains a key: "BP_Stunning" is inside "BP_StunningSomethingElse", and a
        /// substring rename of that is a reference quietly moved onto something that is not there.
        /// </summary>
        public static bool renameNames(byte[] uasset, IReadOnlyDictionary<string, string> swaps)
        {
            if (!nameTable(uasset, out var count, out var offset)) { return false; }

            var changed = 0;
            var at = offset;

            for (int i = 0; i < count; i++)
            {
                if (at + 4 > uasset.Length) { return false; }
                var length = BitConverter.ToInt32(uasset, at);
                at += 4;

                //Wide names are left alone: nothing this renames is ever spelled outside ASCII.
                if (length <= 0) { at += -length * 2 + 4; continue; }

                var text = Encoding.ASCII.GetString(uasset, at, length - 1);
                var now = renamedOf(text, swaps);

                if (now != null && now.Length == text.Length)
                {
                    Encoding.ASCII.GetBytes(now).CopyTo(uasset, at);
                    changed++;
                }

                //Recomputed for every name, touched or not. On an untouched name this writes back
                //exactly what was there - checked against 93 names in four game assets, all
                //matching - so it cannot break one, and it mends any an earlier rename left stale.
                var spelled = now ?? text;
                var hashAt = at + length;
                BitConverter.GetBytes(nonCaseHash(spelled)).CopyTo(uasset, hashAt);
                BitConverter.GetBytes(caseHash(spelled)).CopyTo(uasset, hashAt + 2);

                at += length + 4;
            }

            return changed > 0;
        }

        /// <summary>
        /// How many names in a package carry the hashes this class would give them. Run over the
        /// game's own untouched assets it is the test of the hash functions themselves: anything
        /// short of all of them means the port is wrong, not the file.
        /// </summary>
        public static (int matching, int total) checkHashes(byte[] uasset)
        {
            if (!nameTable(uasset, out var count, out var offset)) { return (0, 0); }

            var matching = 0;
            var at = offset;
            for (int i = 0; i < count; i++)
            {
                var length = BitConverter.ToInt32(uasset, at);
                at += 4;
                if (length <= 0) { at += -length * 2 + 4; continue; }

                var text = Encoding.ASCII.GetString(uasset, at, length - 1);
                var stored1 = BitConverter.ToUInt16(uasset, at + length);
                var stored2 = BitConverter.ToUInt16(uasset, at + length + 2);
                if (stored1 == nonCaseHash(text) && stored2 == caseHash(text)) { matching++; }

                at += length + 4;
            }
            return (matching, count);
        }

        private static string? renamedOf(string text, IReadOnlyDictionary<string, string> swaps)
        {
            if (swaps.TryGetValue(text, out var whole)) { return whole; }

            var dot = text.IndexOf('.');
            if (dot > 0 && swaps.TryGetValue(text.Substring(0, dot), out var path))
            {
                var tail = text.Substring(dot + 1);
                return path + "." + (swaps.TryGetValue(tail, out var name) ? name : tail);
            }

            return null;
        }

        /// <summary>Where the name table starts, read the same way CookedProperties reads it.</summary>
        private static bool nameTable(byte[] uasset, out int count, out int offset)
        {
            count = 0;
            offset = 0;
            try
            {
                var at = 4;
                var legacy = BitConverter.ToInt32(uasset, at); at += 4;
                if (legacy != -4) { at += 4; }
                at += 8;                                            //ue4 and licensee versions
                var custom = BitConverter.ToInt32(uasset, at); at += 4;
                at += custom * 20;
                at += 4;                                            //total header size
                var folder = BitConverter.ToInt32(uasset, at); at += 4;
                at += folder >= 0 ? folder : -folder * 2;
                at += 4;                                            //package flags
                count = BitConverter.ToInt32(uasset, at);
                offset = BitConverter.ToInt32(uasset, at + 4);
                return count > 0 && offset > 0 && offset < uasset.Length;
            }
            catch (Exception)
            {
                return false;
            }
        }

        // ------------------------------------------------------------------ UE 4.22 name hashes

        /// <summary>
        /// The two sixteen-bit hashes stored beside every name in a cooked package.
        ///
        /// Found by reproducing the ones the game ships, not taken from a guide: every one of 93
        /// names across BP_Sharpness and three data tables matched both, and no other variant
        /// matched any. Non-case is FCrc::Strihash_DEPRECATED - upper-cased bytes through the
        /// MSB-first CRC table. Case is FCrc::StrCrc32 - reflected table, and each character run
        /// through as four bytes, because TCHAR is shifted a byte at a time until it is empty.
        /// </summary>
        public static ushort nonCaseHash(string name)
        {
            uint hash = 0;
            foreach (var ch in name)
            {
                var b = (byte)char.ToUpperInvariant(ch);
                hash = ((hash >> 8) & 0x00FFFFFF) ^ MSB[(hash ^ b) & 0xFF];
            }
            return (ushort)(hash & 0xFFFF);
        }

        public static ushort caseHash(string name)
        {
            var crc = 0xFFFFFFFFu;
            foreach (var ch in name)
            {
                uint c = ch;
                for (int i = 0; i < 4; i++)
                {
                    crc = (crc >> 8) ^ REFLECTED[(crc ^ c) & 0xFF];
                    c >>= 8;
                }
            }
            return (ushort)(~crc & 0xFFFF);
        }

        private static readonly uint[] MSB = Enumerable.Range(0, 256).Select(i =>
        {
            var c = (uint)i << 24;
            for (int k = 0; k < 8; k++) { c = (c & 0x80000000) != 0 ? (c << 1) ^ 0x04C11DB7 : c << 1; }
            return c;
        }).ToArray();

        private static readonly uint[] REFLECTED = Enumerable.Range(0, 256).Select(i =>
        {
            var c = (uint)i;
            for (int k = 0; k < 8; k++) { c = (c & 1) != 0 ? (c >> 1) ^ 0xEDB88320 : c >> 1; }
            return c;
        }).ToArray();

        // ------------------------------------------------------------------ what is installed

        /// <summary>
        /// Enchantments and items that installed mods add, put where the pickers look.
        ///
        /// Found the same way the game's own are: by icon. An enchantment is the folder holding a
        /// `T_*_Icon` under Components/Enchantments; an item is the folder holding a
        /// `T_*_Icon_inventory` under Equipment. Without this a new enchantment could exist in
        /// the game and still be impossible to put on anything here, because the picker only
        /// offered what the game's own paks draw.
        /// </summary>
        /// <summary>
        /// Enchantments that installed mods add, and the gear they belong on.
        ///
        /// Taken from the parent: BP_Stunlock's parent is the game's `Stunning`, which is a melee
        /// enchantment, so Stunlock is too. Without this every one of them fell into Other, and
        /// Other is switched off when the picker opens from a weapon - so a new enchantment was
        /// installed, listed, and still nowhere to be seen. One whose parent is not known is put
        /// on all gear rather than hidden.
        /// </summary>
        public static readonly Dictionary<string, Data.EnchantmentCategory> modEnchantments =
            new Dictionary<string, Data.EnchantmentCategory>(StringComparer.OrdinalIgnoreCase);

        public static (int enchantments, int items) registerInstalled(string? paksFolder = null)
        {
            var enchantments = 0;
            var items = 0;

            foreach (var pak in installedPaks(paksFolder ?? CustomSkins.paksFolder))
            {
                IReadOnlyList<ModPak.Item> inside;
                try { inside = ModPak.read(pak); }
                catch (Exception) { continue; }

                //Every blueprint in the pak by its folder, so an enchantment's parent can be read
                //from the BP_ beside the icon that announced it.
                var blueprints = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase);
                foreach (var one in inside)
                {
                    var path = one.Path.Replace('\\', '/');
                    var slash = path.LastIndexOf('/');
                    if (slash < 0 || !path.EndsWith(".uasset", StringComparison.OrdinalIgnoreCase)) { continue; }

                    var file = path.Substring(slash + 1, path.Length - slash - 1 - ".uasset".Length);
                    var folder = path.Substring(0, slash);
                    folder = folder.Substring(folder.LastIndexOf('/') + 1);
                    if (string.Equals(file, "BP_" + folder, StringComparison.OrdinalIgnoreCase))
                    {
                        blueprints[folder] = one.Data;
                    }
                }

                foreach (var one in inside)
                {
                    var path = one.Path.Replace('\\', '/');
                    if (!path.EndsWith(".uasset", StringComparison.OrdinalIgnoreCase)) { continue; }

                    var bits = path.Substring(0, path.Length - ".uasset".Length).Split('/');
                    if (bits.Length < 3) { continue; }

                    var file = bits[bits.Length - 1];
                    var folder = bits[bits.Length - 2];
                    var lower = path.ToLowerInvariant();

                    if (lower.Contains("/components/enchantments/")
                        && file.StartsWith("T_", StringComparison.OrdinalIgnoreCase)
                        && file.EndsWith("_Icon", StringComparison.OrdinalIgnoreCase)
                        && !file.EndsWith("Shine_Icon", StringComparison.OrdinalIgnoreCase))
                    {
                        if (EnchantmentDatabase.allEnchantments.Add(folder)) { enchantments++; }
                        modEnchantments[folder] = blueprints.TryGetValue(folder, out var bp)
                            ? inheritedCategory(bp, folder)
                            : Data.EnchantmentCategory.Gear;
                        continue;
                    }

                    if (lower.Contains("/actors/equipment/")
                        && file.EndsWith("_Icon_inventory", StringComparison.OrdinalIgnoreCase))
                    {
                        //A custom item's folder is the slot's folder, which is not always its id:
                        //Pickaxe_Unique2 lives in Pickaxe_Unique2_Steel. Offering the folder name
                        //would put an id in the save that the game deletes on load.
                        var id = CustomItems.slotForFolder(folder)?.Id ?? folder;
                        if (!ItemDatabase.all.Add(id)) { continue; }
                        items++;

                        if (lower.Contains("/meleeweapons/")) { ItemDatabase.meleeWeapons.Add(id); }
                        else if (lower.Contains("/rangedweapons/")) { ItemDatabase.rangedWeapons.Add(id); }
                        else if (lower.Contains("/armor")) { ItemDatabase.armor.Add(id); }
                    }
                }
            }

            return (enchantments, items);
        }

        /// <summary>
        /// The gear an enchantment belongs on, read off the game enchantment its blueprint is
        /// built on. The parent is named in the blueprint's own name table, so any name there
        /// that the category table knows - other than the new id itself - is it.
        /// </summary>
        private static Data.EnchantmentCategory inheritedCategory(byte[] uasset, string id)
        {
            foreach (var name in CookedProperties.readNamesOf(uasset))
            {
                if (string.Equals(name, id, StringComparison.OrdinalIgnoreCase)) { continue; }

                var known = Data.EnchantmentCategories.known(name);
                if (known != Data.EnchantmentCategory.None) { return known; }
            }

            return Data.EnchantmentCategory.Gear;
        }

        /// <summary>The mod paks the game will load - its ~mods folder, and this app's own.</summary>
        private static IEnumerable<string> installedPaks(string? root)
        {
            var found = new List<string>();
            if (string.IsNullOrWhiteSpace(root)) { return found; }

            var mods = Path.Combine(root!, CustomSkins.MODS_FOLDER);
            if (Directory.Exists(mods))
            {
                found.AddRange(Directory.GetFiles(mods, "*.pak", SearchOption.AllDirectories));
            }

            if (Directory.Exists(root))
            {
                found.AddRange(Directory.GetFiles(root, "MCDReborn_*.pak"));
            }

            return found.Distinct(StringComparer.OrdinalIgnoreCase);
        }
    }
}
