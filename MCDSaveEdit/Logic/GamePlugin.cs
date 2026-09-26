using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
#nullable enable

namespace MCDSaveEdit.Logic
{
    /// <summary>
    /// MCD Reborn's item plugin, installed beside the game as xinput1_3.dll.
    ///
    /// The free slots are ids the game already has. Anything beyond them needs the id added to the
    /// game's item list while it runs, and that list is C++ - no pak reaches it. The plugin
    /// (ItemPlugin/ItemPlugin.cpp) does it from inside: Windows loads it with the game because the
    /// game imports an xinput1_3.dll and looks beside itself first, it passes every XInput call on
    /// to the real one, and it registers each item in MCDRebornItems.txt with the game's own
    /// registration routine, as a copy of the item it was copied from. The item's files, icons
    /// and registry entries are in the New Items pak like any slot's.
    ///
    /// Steam and the Minecraft Launcher only. The Xbox app's install cannot be written to - or
    /// even read.
    /// </summary>
    public static class GamePlugin
    {
        public const string DLL_NAME = "xinput1_3.dll";
        public const string ITEMS_NAME = "MCDRebornItems.txt";
        public const string LOG_NAME = "MCDRebornItems.log";

        /// <summary>What the plugin exports so that its copy can be told from anybody else's.</summary>
        private static readonly byte[] MARK = Encoding.ASCII.GetBytes("MCDRebornPlugin");

        /// <summary>
        /// One item for the plugin to register. Skills ("5:1;9:1") and Lines ("key=text|...") replace
        /// the copied item's when given; "-" keeps them.
        /// </summary>
        public sealed record Item(string Id, string Source, string Folder, string Name, string Description,
            string Skills = "-", string Lines = "-");

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
            return File.Exists(Path.Combine(win64, "Dungeons-Win64-Shipping.exe")) ? win64 : null;
        }

        /// <summary>Whether the xinput1_3.dll at this path is the plugin rather than somebody else's.</summary>
        public static bool isOurs(string dllPath)
        {
            try { return File.Exists(dllPath) && indexOf(File.ReadAllBytes(dllPath), MARK) >= 0; }
            catch (IOException) { return false; }
        }

        /// <summary>
        /// The plugin and its item list, installed - or both removed when there are no items, so a
        /// game with no custom items beyond the slots runs exactly as shipped. Refuses rather than
        /// overwrite another mod's xinput1_3.dll.
        /// </summary>
        public static string install(IReadOnlyList<Item> items, string? paksFolder = null)
        {
            var folder = gameFolder(paksFolder)
                ?? throw new InvalidOperationException("Items beyond the free slots need the Steam or Minecraft Launcher version of the game; this one's folder cannot take the plugin.");
            var dll = Path.Combine(folder, DLL_NAME);
            var list = Path.Combine(folder, ITEMS_NAME);

            if (items.Count == 0)
            {
                if (isOurs(dll)) { File.Delete(dll); }
                if (File.Exists(list)) { File.Delete(list); }
                return "The item plugin was removed: there are no items beyond the free slots.";
            }

            if (File.Exists(dll) && !isOurs(dll))
            {
                throw new InvalidOperationException($"Another mod already installed an {DLL_NAME} beside the game (in {folder}). Remove it to use items beyond the free slots.");
            }

            var carried = carriedDll();
            if (!File.Exists(dll) || !File.ReadAllBytes(dll).AsSpan().SequenceEqual(carried))
            {
                File.WriteAllBytes(dll, carried);
            }

            //Tab-separated, one item a line. A tab or a line break typed into a name would split
            //it, so they become spaces.
            string clean(string text) => text.Replace('\t', ' ').Replace('\r', ' ').Replace('\n', ' ').Trim();
            var lines = new List<string> { "# id\tsource\tfolder\tname\tdescription\tskills\tlines - written by MCD Reborn, rewritten on every install" };
            lines.AddRange(items.Select(i => string.Join("\t", i.Id, i.Source, i.Folder, clean(i.Name), clean(i.Description), clean(i.Skills), clean(i.Lines))));
            File.WriteAllLines(list, lines, new UTF8Encoding(false));
            return $"The item plugin will register {items.Count} item(s) the next time the game starts.";
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

        private static byte[] carriedDll()
        {
            var assembly = typeof(GamePlugin).Assembly;
            var resource = assembly.GetManifestResourceNames()
                .FirstOrDefault(one => one.EndsWith(".MCDRebornItems.dll", StringComparison.OrdinalIgnoreCase))
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
