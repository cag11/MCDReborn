using MCDSaveEdit.Services;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Windows.Media.Imaging;
#nullable enable

namespace MCDSaveEdit.Logic
{
    /// <summary>
    /// Talents: a passive tree in the game, opened with P or by clicking the TALENTS display case in the Camp. A point every few hero levels,
    /// spent on nodes joined to one already taken; a reset in the Camp gives them all back for
    /// emeralds.
    ///
    /// The tree - its nodes, where they sit, their links and what each does - is Talents.json,
    /// written by Tools/loader/talent_tree.py and built into the screen by build_talents.py. What a
    /// character has taken is kept in its own save, as the bits of hidden currencies (copies of
    /// Gold, like gems): <see cref="STORE"/>0.. thirty nodes to one, and <see cref="SPENT"/>
    /// counting them.
    ///
    /// The effects are the plugin's: one property per game property the tree uses
    /// (MCDR_Talent&lt;Source&gt;, a copy with its own class), whose number the plugin sets from the
    /// nodes taken, and "@talent" lines saying which node adds what.
    /// </summary>
    public static class Talents
    {
        public const string STORE = "MCDR_Talents";
        public const string SPENT = "MCDR_TalentSpent";
        public const string PREFIX = "MCDR_Talent";

        /// <summary>One thing a node does: a scaled source and its factor, or a game property as it is.</summary>
        public sealed class Effect
        {
            public string? source { get; set; }
            public double? factor { get; set; }
            public string? raw { get; set; }
            public int? rawId { get; set; }
        }

        public sealed class Node
        {
            public int index { get; set; }
            public string name { get; set; } = "";
            public string kind { get; set; } = "";
            public string region { get; set; } = "";
            public List<Effect> effects { get; set; } = new();
            public string text { get; set; } = "";
            public int store { get; set; }
            public int bit { get; set; }
        }

        public sealed class Points
        {
            public int perLevels { get; set; }
            public int cap { get; set; }
        }

        public sealed class Tree
        {
            public Points points { get; set; } = new();
            public int respec { get; set; }
            public int stores { get; set; }
            public List<Node> nodes { get; set; } = new();
        }

        private static Tree? _tree;

        /// <summary>The tree, from the copy carried in this exe.</summary>
        public static Tree tree
        {
            get
            {
                if (_tree != null) { return _tree; }
                using var stream = typeof(Talents).Assembly.GetManifestResourceStream("Talents.json")
                    ?? throw new InvalidOperationException("This build does not carry the talent tree.");
                return _tree = JsonSerializer.Deserialize<Tree>(stream) ?? throw new InvalidOperationException("The talent tree could not be read.");
            }
        }

        private static string file => Path.Combine(CustomItems.folder, "talents.txt");

        public static bool isOn => File.Exists(file);

        public static void set(bool on)
        {
            if (on) { File.WriteAllText(file, "on"); }
            else if (File.Exists(file)) { File.Delete(file); }
        }

        /// <summary>The hidden currencies: the stores and the count, copies of Gold.</summary>
        public static IReadOnlyList<CustomItems.Extra> extras()
            => Enumerable.Range(0, tree.stores).Select(i => new CustomItems.Extra($"{STORE}{i}", "Gold", "Talents", "The talents this hero has taken."))
                .Append(new CustomItems.Extra(SPENT, "Gold", "Talent points spent", "How many talent points this hero has spent."))
                .ToList();

        /// <summary>
        /// The sources whose number is an amount (0 does nothing) rather than a multiplier (1 does
        /// nothing), as for gems.
        /// </summary>
        private static readonly HashSet<string> AMOUNTS = new() { "LifeStealAura", "HealingAura", "MissChance", "TeleportChance" };

        /// <summary>
        /// One property per source the tree scales. Its factor here is only where it starts; the
        /// plugin sets its number from the nodes taken, every time they change.
        /// </summary>
        public static IReadOnlyList<CustomProperties.Design> properties()
            => tree.nodes.SelectMany(n => n.effects).Where(e => e.source != null).Select(e => e.source!).Distinct().Select(source => new CustomProperties.Design
            {
                Id = PREFIX + source, Source = source, Name = "Talent", Line = "Talent", Active = true,
                Factor = 1, Neutral = AMOUNTS.Contains(source) ? 0 : 1,
            }).ToList();

        /// <summary>"@talent \t store \t bit \t target \t factor": what each node adds, a line an effect.</summary>
        public static IReadOnlyList<string> forPlugin()
        {
            if (!isOn) { return Array.Empty<string>(); }
            return tree.nodes.Where(n => n.kind != "start").SelectMany(n => n.effects.Select(e => string.Join("\t", "@talent",
                    n.store.ToString(CultureInfo.InvariantCulture), n.bit.ToString(CultureInfo.InvariantCulture),
                    e.source != null ? PREFIX + e.source : (e.rawId ?? 0).ToString(CultureInfo.InvariantCulture),
                    (e.factor ?? 0).ToString(CultureInfo.InvariantCulture))))
                .ToList();
        }

        /// <summary>
        /// The tree screen: a Lobby and an Ingame level, each placing the actor that puts it up, the
        /// screen and its pictures. Built by Tools/loader/build_talents.py, carried in this exe.
        /// </summary>
        private const string PANEL_PREFIX = "Talents";
        private const string PANEL_FOUND_AS = "MCDReborn_Talents";
        private const string CARRIED = "TalentsAssets/";
        private const string TREE_MARKER = "tree.txt";

        /// <summary>
        /// Where the TALENTS display case stands in the Camp (Unreal centimetres) and which way it
        /// faces (yaw): where the user sat. Written into its cooked level on install, as the Gem
        /// Merchant's booth is, so moving it needs no cook.
        /// </summary>
        public static readonly (float x, float y, float z, float yaw) PROP = (19065f, 7604f, 11402f, 180f);

        private static void placeProp(List<PakWriter.Entry> entries)
        {
            var head = entries.FindIndex(e => e.Path.EndsWith("/Lobby/TalentsProp.umap", StringComparison.OrdinalIgnoreCase));
            var data = entries.FindIndex(e => e.Path.EndsWith("/Lobby/TalentsProp.uexp", StringComparison.OrdinalIgnoreCase));
            if (head < 0 || data < 0) { return; }
            var package = CookedEdit.read(entries[head].Data, entries[data].Data);
            var moved = CookedEdit.setVector(package, "RelativeLocation", PROP.x, PROP.y, PROP.z);
            var turned = CookedEdit.setVector(package, "RelativeRotation", 0f, PROP.yaw, 0f);
            if (moved != 1) { Journal.note($"talents: the display case's level has {moved} position(s), not one; it stands where it was cooked"); }
            if (turned != 1) { Journal.note($"talents: the display case's level has {turned} rotation(s), not one; it faces as it was cooked"); }
            entries[head] = new PakWriter.Entry(entries[head].Path, package.Header);
            entries[data] = new PakWriter.Entry(entries[data].Path, package.Data);
        }

        /// <summary>
        /// The node pictures: the screen carries blank textures of its own (UI/Talents/Icons/T_TI_&lt;Name&gt;,
        /// never streamed, so a widget draws them whole), each painted here with the game's own
        /// picture of that enchantment, read from the paks this app has loaded. The game's texture
        /// itself cannot be used by path: it streams, and one shown only by a widget stays a blur.
        /// </summary>
        private static int paintIcons(List<PakWriter.Entry> entries)
        {
            const string icons = "/UI/Talents/Icons/T_TI_";
            var painted = 0;
            for (var i = 0; i < entries.Count; i++)
            {
                var path = entries[i].Path;
                var at = path.IndexOf(icons, StringComparison.OrdinalIgnoreCase);
                if (at < 0 || !path.EndsWith(".uasset", StringComparison.OrdinalIgnoreCase)) { continue; }
                var name = path.Substring(at + icons.Length, path.Length - at - icons.Length - ".uasset".Length);
                var data = entries.FindIndex(e => e.Path.Equals(path.Substring(0, path.Length - ".uasset".Length) + ".uexp", StringComparison.OrdinalIgnoreCase));
                if (data < 0) { continue; }
                BitmapSource? picture = null;
                try { picture = ImageResolver.instance.imageSource($"/Dungeons/Content/Components/Enchantments/{name}/T_{name}_Icon"); }
                catch (Exception) { }
                if (picture == null) { Journal.note($"talents: the game's picture for {name} was not found; its nodes show a blank"); continue; }
                var why = CustomItems.repaint(entries[i].Data, entries[data].Data, null, picture, out var uexp, out _);
                if (why != null) { Journal.note($"talents: {name}'s picture was not painted - {why}"); continue; }
                entries[data] = new PakWriter.Entry(entries[data].Path, uexp);
                painted++;
            }
            return painted;
        }

        public static string? panelInstalled()
        {
            foreach (var folder in new[] { CustomSkins.paksFolder, CustomSkins.modsFolder })
            {
                if (folder == null || !Directory.Exists(folder)) { continue; }
                var found = Directory.EnumerateFiles(folder, PANEL_FOUND_AS + "*.pak").FirstOrDefault();
                if (found != null) { return found; }
            }
            return null;
        }

        /// <summary>Puts the screen in when talents are on and takes it out when they are off.</summary>
        public static string syncPanel()
        {
            var old = panelInstalled();
            if (old != null) { File.Delete(old); }

            if (!isOn)
            {
                if (old != null && !Gems.isOn && Payloads.installed().Count == 0 && !MapTable.isInstalled) { Loader.remove(); }
                return old != null ? "Talents removed." : "";
            }

            //The cooked screen is built from one tree; TalentsAssets/tree.txt says how many nodes it has.
            //A screen for another tree would store and read the nodes' bits differently, so none goes in.
            var assembly = typeof(Talents).Assembly;
            using (var marker = assembly.GetManifestResourceStream(CARRIED + TREE_MARKER))
            {
                var cooked = marker == null ? "" : new StreamReader(marker).ReadToEnd().Trim();
                if (cooked != tree.nodes.Count.ToString(CultureInfo.InvariantCulture))
                {
                    return "Talents not installed: this build's tree screen does not match its tree.";
                }
            }

            if (!Loader.isInstalled) { Loader.installBuiltIn(); }

            var entries = new List<PakWriter.Entry>();
            foreach (var name in assembly.GetManifestResourceNames().Where(n => n.StartsWith(CARRIED, StringComparison.Ordinal)))
            {
                if (name.EndsWith(TREE_MARKER, StringComparison.Ordinal)) { continue; }
                using var stream = assembly.GetManifestResourceStream(name)!;
                using var memory = new MemoryStream();
                stream.CopyTo(memory);
                var inside = name.Substring(CARRIED.Length).Replace('\\', '/');
                entries.Add(new PakWriter.Entry("Dungeons/Content/MCDReborn/" + inside, memory.ToArray()));
            }

            placeProp(entries);
            var painted = paintIcons(entries);

            //Three levels, two actors, the screen, the sign and five pictures, each a header and its data.
            if (entries.Count < 2 * (7 + 5))
            {
                throw new InvalidOperationException($"This build carries {entries.Count} of the Talents files, not all of them.");
            }

            CustomSkins.writeModPak(PANEL_PREFIX, entries);
            return $"Talents installed ({painted} pictures from the game): press P in the Camp or a mission, or click the TALENTS display case in the Camp.";
        }
    }
}
