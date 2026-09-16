using System;
using System.Collections.Generic;
using System.Linq;
#nullable enable

namespace MCDSaveEdit.Logic
{
    /// <summary>
    /// Every level the tower can build a floor from, taken out of the game's own paks.
    ///
    /// A run only ever generates a few of these - thirty one floors out of a hundred and forty
    /// seven levels - so listing what a run happens to contain is a small fraction of what
    /// exists. The paks hold all of them, under Decor/Maps/thetower*/SubLevels, and reading them
    /// from there means the list follows whatever game and DLC are installed rather than a list
    /// written down here that goes stale.
    ///
    /// Each level also ships themed copies, "_S1_theme" and friends. Those are the same floor
    /// dressed for a season, and the save never names one, so they are left out: offering a name
    /// the game does not put in a save is offering a way to break a run.
    ///
    /// The encounters are not here, because they are not assets. A tile is a level with a path;
    /// a challenge is a row of data inside one, and nothing in the pak index names it. So
    /// encounters come from the run itself while tiles come from the game.
    /// </summary>
    public static class TowerTiles
    {
        /// <summary>Levels live under this, one folder per tower area.</summary>
        private const string MAPS = "/decor/maps/thetower";
        private const string SUBLEVELS = "/sublevels/";

        private static IReadOnlyDictionary<string, IReadOnlyList<string>>? _byType;

        /// <summary>
        /// Which kind of floor a level builds, decided by its name.
        ///
        /// Checked against a real run: all thirty one floors of it classify the way the save
        /// itself labels them, none misfiled and none missing.
        /// </summary>
        public static string typeOf(string tile)
        {
            var name = tile.ToLowerInvariant();
            if (name.StartsWith("twr_inhabitant", StringComparison.Ordinal)) { return "Merchant"; }
            if (name.StartsWith("twr_boss", StringComparison.Ordinal) || name == "twr_floor_top") { return "Boss"; }
            if (name == "twr_floor_entrance" || name == "twr_floor_bottom") { return "Empty"; }
            return "Combat";
        }

        /// <summary>The levels the loaded game content offers, by the kind of floor they build.</summary>
        public static IReadOnlyList<string> forType(string type)
        {
            _byType ??= scan();
            return _byType.TryGetValue(type, out var tiles) ? tiles : Array.Empty<string>();
        }

        /// <summary>True once the paks have been read and there is something to offer.</summary>
        public static bool any => forType("Combat").Count > 0;

        /// <summary>Forgets what was found, for when different game content is loaded.</summary>
        public static void reset() => _byType = null;

        private static IReadOnlyDictionary<string, IReadOnlyList<string>> scan()
        {
            var found = new Dictionary<string, SortedSet<string>>(StringComparer.OrdinalIgnoreCase);

            foreach (var entry in CustomSkins.index ?? Enumerable.Empty<string>())
            {
                var path = entry.Replace('\\', '/');
                var lower = path.ToLowerInvariant();
                if (lower.IndexOf(MAPS, StringComparison.Ordinal) < 0) { continue; }
                if (lower.IndexOf(SUBLEVELS, StringComparison.Ordinal) < 0) { continue; }

                var name = path.Substring(path.LastIndexOf('/') + 1);
                if (!name.StartsWith("twr_", StringComparison.OrdinalIgnoreCase)) { continue; }
                if (isThemedCopy(name)) { continue; }

                var type = typeOf(name);
                if (!found.TryGetValue(type, out var set))
                {
                    set = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
                    found[type] = set;
                }
                set.Add(name);
            }

            return found.ToDictionary(
                pair => pair.Key,
                pair => (IReadOnlyList<string>)pair.Value.ToList(),
                StringComparer.OrdinalIgnoreCase);
        }

        /// <summary>A seasonal dressing of another level - "_S1_theme" and the like.</summary>
        private static bool isThemedCopy(string name)
        {
            var at = name.LastIndexOf("_theme", StringComparison.OrdinalIgnoreCase);
            if (at <= 0) { return false; }

            //Everything between the previous underscore and "_theme" has to read like S1.
            var before = name.LastIndexOf('_', at - 1);
            if (before < 0) { return false; }

            var marker = name.Substring(before + 1, at - before - 1);
            return marker.Length >= 2
                && (marker[0] == 's' || marker[0] == 'S')
                && marker.Skip(1).All(char.IsDigit);
        }
    }
}
