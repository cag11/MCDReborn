using MCDSaveEdit.Services;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;
#nullable enable

namespace MCDSaveEdit.Logic
{
    /// <summary>
    /// The talent tree: a passive tree in the game, opened with P. A point every few hero levels,
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

        public sealed class Node
        {
            public int index { get; set; }
            public string name { get; set; } = "";
            public string kind { get; set; } = "";
            public string section { get; set; } = "";
            public string? source { get; set; }
            public double? factor { get; set; }
            public string? raw { get; set; }
            public int? rawId { get; set; }
            public string text { get; set; } = "";
            public int store { get; set; }
            public int bit { get; set; }
            public string? id { get; set; }
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
            => tree.nodes.Where(n => n.source != null).Select(n => n.source!).Distinct().Select(source => new CustomProperties.Design
            {
                Id = PREFIX + source, Source = source, Name = "Talent", Line = "Talent", Active = true,
                Factor = 1, Neutral = AMOUNTS.Contains(source) ? 0 : 1,
            }).ToList();

        /// <summary>"@talent \t store \t bit \t target \t factor": what each node adds.</summary>
        public static IReadOnlyList<string> forPlugin()
        {
            if (!isOn) { return Array.Empty<string>(); }
            return tree.nodes.Where(n => n.kind != "start").Select(n => string.Join("\t", "@talent",
                    n.store.ToString(CultureInfo.InvariantCulture), n.bit.ToString(CultureInfo.InvariantCulture),
                    n.source != null ? PREFIX + n.source : (n.rawId ?? 0).ToString(CultureInfo.InvariantCulture),
                    (n.factor ?? 0).ToString(CultureInfo.InvariantCulture)))
                .ToList();
        }

        /// <summary>
        /// The tree screen: a Lobby and an Ingame level, each placing the actor that puts it up, the
        /// screen and its pictures. Built by Tools/loader/build_talents.py, carried in this exe.
        /// </summary>
        private const string PANEL_PREFIX = "Talents";
        private const string PANEL_FOUND_AS = "MCDReborn_Talents";
        private const string CARRIED = "TalentsAssets/";

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
                return old != null ? "Talent tree removed." : "";
            }

            if (!Loader.isInstalled) { Loader.installBuiltIn(); }

            var assembly = typeof(Talents).Assembly;
            var entries = new List<PakWriter.Entry>();
            foreach (var name in assembly.GetManifestResourceNames().Where(n => n.StartsWith(CARRIED, StringComparison.Ordinal)))
            {
                using var stream = assembly.GetManifestResourceStream(name)!;
                using var memory = new MemoryStream();
                stream.CopyTo(memory);
                var inside = name.Substring(CARRIED.Length).Replace('\\', '/');
                entries.Add(new PakWriter.Entry("Dungeons/Content/MCDReborn/" + inside, memory.ToArray()));
            }

            //Two levels, the actor, the screen and five pictures, each a header and its data.
            if (entries.Count < 2 * (4 + 5))
            {
                throw new InvalidOperationException($"This build carries {entries.Count} of the talent tree's files, not all of them.");
            }

            CustomSkins.writeModPak(PANEL_PREFIX, entries);
            return "Talent tree installed: press P in the Camp or a mission.";
        }
    }
}
