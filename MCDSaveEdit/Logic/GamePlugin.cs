using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
#nullable enable

namespace MCDSaveEdit.Logic
{
    /// <summary>
    /// MCD Reborn's item plugin, installed beside the game as xinput1_3.dll - or as dsound.dll on
    /// the Xbox app.
    ///
    /// The free slots are ids the game already has. Anything beyond them needs the id added to the
    /// game's item list while it runs, and that list is C++ - no pak reaches it. The plugin
    /// (ItemPlugin/ItemPlugin.cpp) does it from inside: Windows loads it with the game because the
    /// game imports an xinput1_3.dll and looks beside itself first, it passes every XInput call on
    /// to the real one, and it registers each item in MCDRebornItems.txt with the game's own
    /// registration routine, as a copy of the item it was copied from. The item's files, icons
    /// and registry entries are in the New Items pak like any slot's.
    ///
    /// Steam, the Minecraft Launcher and the Xbox app. The Xbox app's install (C:\XboxGames, which
    /// is the same folder the running game sees under WindowsApps) names its executable Dungeons.exe
    /// rather than Dungeons-Win64-Shipping.exe; the exe itself cannot be read, but its folder takes
    /// new files like the others. That build never loads XInput - it reads controllers through the
    /// Xbox game runtime - so there the plugin is the DirectSound stand-in, dsound.dll.
    /// </summary>
    public static class GamePlugin
    {
        public const string DLL_NAME = "xinput1_3.dll";

        /// <summary>
        /// The name the plugin goes in as on the Xbox app, whose build reads controllers through
        /// the Xbox game runtime and never loads XInput - but loads dsound.dll from beside itself
        /// (tested 2026-09-27: loaded, patterns found, cross-check passed).
        /// </summary>
        public const string XBOX_DLL_NAME = "dsound.dll";

        /// <summary>Whether the game folder is the Xbox app's: its executable is Dungeons.exe.</summary>
        public static bool isXbox(string gameFolder) => !File.Exists(Path.Combine(gameFolder, "Dungeons-Win64-Shipping.exe"));

        /// <summary>The name the plugin is installed under in this game folder.</summary>
        public static string dllName(string gameFolder) => isXbox(gameFolder) ? XBOX_DLL_NAME : DLL_NAME;
        public const string ITEMS_NAME = "MCDRebornItems.txt";
        public const string LOG_NAME = "MCDRebornItems.log";

        /// <summary>What the plugin exports so that its copy can be told from anybody else's.</summary>
        private static readonly byte[] MARK = Encoding.ASCII.GetBytes("MCDRebornPlugin");

        /// <summary>
        /// One item for the plugin to register. Skills ("5:1;9:1") and Lines ("key=text|...") replace
        /// the copied item's when given; "-" keeps them.
        /// </summary>
        public sealed record Item(string Id, string Source, string Folder, string Name, string Description,
            string Skills = "-", string Lines = "-", string ArmorProperties = "-", string Numbers = "-");

        /// <summary>
        /// One enchantment for the plugin to register: a copy of the enchantment whose
        /// EEnchantmentTypeID is SourceType. Each text "-" keeps the source's. Blueprint is its own,
        /// relative to Components/Enchantments ("MCDR_Ench02/BP_MCDR_Ench02"), or "-" for the source's.
        /// </summary>
        public sealed record Enchantment(string Id, int SourceType, string Name, string Description, string BuiltIn, string Effect,
            string Blueprint = "-", string Icon = "-");

        /// <summary>
        /// The folder with the game's executable, from its paks folder: Dungeons\Content\Paks is
        /// beside Dungeons\Binaries\Win64. Null when there is no game there to load the plugin.
        /// </summary>
        public static string? gameFolder(string? paksFolder = null)
        {
            paksFolder ??= CustomSkins.paksFolder;
            if (paksFolder == null) { return null; }
            var dungeons = Directory.GetParent(Directory.GetParent(paksFolder.TrimEnd('\\', '/'))?.FullName ?? "")?.FullName;
            if (dungeons == null) { return null; }
            var win64 = Path.Combine(dungeons, "Binaries", "Win64");
            //Steam and the Minecraft Launcher, then the Xbox app.
            return File.Exists(Path.Combine(win64, "Dungeons-Win64-Shipping.exe")) || File.Exists(Path.Combine(win64, "Dungeons.exe"))
                ? win64 : null;
        }

        /// <summary>Whether the xinput1_3.dll at this path is the plugin rather than somebody else's.</summary>
        public static bool isOurs(string dllPath)
        {
            try { return File.Exists(dllPath) && indexOf(File.ReadAllBytes(dllPath), MARK) >= 0; }
            catch (IOException) { return false; }
        }

        /// <summary>
        /// The plugin and its item list, installed - or both removed when there are no items and no
        /// enchantments, so a game with none runs exactly as shipped. Refuses rather than
        /// overwrite another mod's xinput1_3.dll. The saved enchantments (CustomEnchantments) go
        /// in whatever tab asked, so reinstalling items never drops them.
        /// </summary>
        public static string install(IReadOnlyList<Item> items, string? paksFolder = null)
        {
            var enchantments = CustomEnchantments.forPlugin();
            var mobs = CustomMobs.forPlugin();
            var folder = gameFolder(paksFolder)
                ?? throw new InvalidOperationException("Items beyond the free slots need the game's own folder beside its paks (Steam, the Minecraft Launcher or the Xbox app); this one has no game executable there.");
            var dll = Path.Combine(folder, dllName(folder));
            var list = Path.Combine(folder, ITEMS_NAME);

            //Tab-separated, one item a line. A tab or a line break typed into a name would split
            //it, so they become spaces.
            string clean(string text) => text.Replace('\t', ' ').Replace('\r', ' ').Replace('\n', ' ').Trim();
            var properties = CustomProperties.forPlugin(clean);
            var struggles = ApocalypsePlus.isOn;
            if (items.Count == 0 && enchantments.Count == 0 && mobs.Count == 0 && properties.Count == 0 && !struggles)
            {
                if (isOurs(dll)) { File.Delete(dll); }
                if (File.Exists(list)) { File.Delete(list); }
                return "The item plugin was removed: there are no custom items, enchantments or mobs.";
            }

            if (File.Exists(dll) && !isOurs(dll))
            {
                throw new InvalidOperationException($"Another mod already installed a {dllName(folder)} beside the game (in {folder}). Remove it to use items beyond the free slots.");
            }

            var carried = carriedDll(isXbox(folder));
            if (!File.Exists(dll) || !File.ReadAllBytes(dll).AsSpan().SequenceEqual(carried))
            {
                File.WriteAllBytes(dll, carried);
            }

            var lines = new List<string> { "# id\tsource\tfolder\tname\tdescription\tskills\tlines\tarmor properties\tnumbers - written by MCD Reborn, rewritten on every install" };
            lines.AddRange(items.Select(i => string.Join("\t", i.Id, i.Source, i.Folder, clean(i.Name), clean(i.Description), clean(i.Skills), clean(i.Lines), clean(i.ArmorProperties), clean(i.Numbers))));
            if (enchantments.Count > 0)
            {
                lines.Add("# @enchantment\tid\tsource type id\tname\tdescription\tbuilt-in line\teffect\tblueprint\ticon (texture|material) - \"-\" keeps the source's");
                lines.AddRange(enchantments.Select(e => string.Join("\t", "@enchantment", e.Id, e.SourceType.ToString(System.Globalization.CultureInfo.InvariantCulture),
                    clean(e.Name), clean(e.Description), clean(e.BuiltIn), clean(e.Effect), e.Blueprint, e.Icon)));
            }
            if (mobs.Count > 0)
            {
                lines.Add("# @mob	id	source EntityType	name	blueprint under /Game/");
                lines.AddRange(mobs.Select(m => string.Join("	", "@mob", m.Id, m.SourceType.ToString(System.Globalization.CultureInfo.InvariantCulture), clean(m.Name), m.Blueprint)));
            }
            if (properties.Count > 0)
            {
                lines.Add("# @property\tid\tsource EArmorPropertyID\tname\tline under the item's name\ticon (texture|material) - \"-\" keeps the source's\tactive: 1 does what its source does, 0 is only a line");
                lines.AddRange(properties);
            }
            var talents = Talents.forPlugin();
            if (talents.Count > 0)
            {
                lines.Add("# @talent	store	bit	target: a talent property's id or a game property's number	factor of its source's number");
                lines.AddRange(talents);
            }
            if (struggles)
            {
                lines.Add("# @struggle	highest Apocalypse+ level - past 25 the game's own step from +24 to +25 is carried on");
                lines.Add("@struggle	" + ApocalypsePlus.TOP.ToString(System.Globalization.CultureInfo.InvariantCulture));
            }
            File.WriteAllLines(list, lines, new UTF8Encoding(false));
            return $"The item plugin will register {items.Count} item(s), {enchantments.Count} enchantment(s), {properties.Count} propert(ies) and {mobs.Count} mob(s)"
                + (struggles ? $", and Apocalypse+ up to +{ApocalypsePlus.TOP}," : "") + " the next time the game starts.";
        }

        /// <summary>
        /// What the plugin wrote the last time the game ran, or null if it has not run. Read
        /// shared, because the game keeps it open while it runs.
        /// </summary>
        public static string[]? lastLog(string? paksFolder = null)
        {
            var folder = gameFolder(paksFolder);
            if (folder == null) { return null; }
            var log = Path.Combine(folder, LOG_NAME);
            if (!File.Exists(log)) { return null; }
            try
            {
                using var stream = new FileStream(log, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                using var reader = new StreamReader(stream);
                return reader.ReadToEnd().Split('\n').Select(l => l.TrimEnd()).Where(l => l.Length > 0).ToArray();
            }
            catch (IOException) { return null; }
        }

        /// <summary>
        /// The enchantments the plugin registers, from the installed item list's "@enchantment"
        /// lines, into the app's own list - so the inventory's picker offers them - under the
        /// category of the enchantment each copies.
        /// </summary>
        public static int registerEnchantments(string? paksFolder = null)
        {
            var names = GearTraits.ENCHANTMENT_IDS.ToDictionary(p => p.Value, p => p.Key);
            var added = 0;
            foreach (var one in installedEnchantments(paksFolder))
            {
                if (Services.EnchantmentDatabase.allEnchantments.Add(one.Id)) { added++; }
                if (names.TryGetValue(one.SourceType, out var sourceName))
                {
                    NewContent.modEnchantments[one.Id] = Data.EnchantmentCategories.known(sourceName);
                    _sources[one.Id] = sourceName;
                }
            }
            CustomEnchantments.showInApp();
            return added;
        }

        private static readonly Dictionary<string, string> _sources = new(StringComparer.OrdinalIgnoreCase);

        /// <summary>The enchantment a plugin enchantment copies, by name, for its icon; null for any other.</summary>
        public static string? sourceOf(string enchantmentId) => _sources.TryGetValue(enchantmentId, out var source) ? source : null;

        /// <summary>One mob the plugin registers: a copy of the EntityType SourceType, under its own id and name.</summary>
        public sealed record Mob(string Id, int SourceType, string Name, string Blueprint);

        /// <summary>The "@mob" lines of the installed item list, as the plugin will read them.</summary>
        public static List<Mob> installedMobs(string? paksFolder = null)
        {
            var found = new List<Mob>();
            var folder = gameFolder(paksFolder);
            var list = folder == null ? null : Path.Combine(folder, ITEMS_NAME);
            if (list == null || !File.Exists(list)) { return found; }
            string[] lines;
            try { lines = File.ReadAllLines(list); }
            catch (IOException) { return found; }
            foreach (var line in lines)
            {
                if (!line.StartsWith("@mob	", StringComparison.Ordinal)) { continue; }
                var parts = line.Split('	');
                if (parts.Length < 5 || !int.TryParse(parts[2], out var source)) { continue; }
                found.Add(new Mob(parts[1], source, parts[3], parts[4]));
            }
            return found;
        }

        /// <summary>The "@enchantment" lines of the installed item list, as the plugin will read them.</summary>
        public static List<Enchantment> installedEnchantments(string? paksFolder = null)
        {
            var found = new List<Enchantment>();
            var folder = gameFolder(paksFolder);
            var list = folder == null ? null : Path.Combine(folder, ITEMS_NAME);
            if (list == null || !File.Exists(list)) { return found; }
            string[] lines;
            try { lines = File.ReadAllLines(list); }
            catch (IOException) { return found; }
            foreach (var line in lines)
            {
                if (!line.StartsWith("@enchantment\t", StringComparison.Ordinal)) { continue; }
                var parts = line.Split('\t');
                if (parts.Length < 7 || !int.TryParse(parts[2], out var source)) { continue; }
                found.Add(new Enchantment(parts[1], source, parts[3], parts[4], parts[5], parts[6], parts.Length > 7 ? parts[7] : "-", parts.Length > 8 ? parts[8] : "-"));
            }
            return found;
        }

        /// <summary>The plugin this build carries: the XInput stand-in, or the DirectSound one for the Xbox app.</summary>
        private static byte[] carriedDll(bool xbox)
        {
            var assembly = typeof(GamePlugin).Assembly;
            var wanted = xbox ? ".MCDRebornDSound.dll" : ".MCDRebornItems.dll";
            var resource = assembly.GetManifestResourceNames()
                .FirstOrDefault(one => one.EndsWith(wanted, StringComparison.OrdinalIgnoreCase))
                ?? throw new InvalidOperationException("This build does not carry the item plugin.");
            using var stream = assembly.GetManifestResourceStream(resource)
                ?? throw new InvalidOperationException("The item plugin could not be read out of this build.");
            using var memory = new MemoryStream();
            stream.CopyTo(memory);
            return memory.ToArray();
        }

        private static int indexOf(byte[] haystack, byte[] needle) => haystack.AsSpan().IndexOf(needle);
    }
}
