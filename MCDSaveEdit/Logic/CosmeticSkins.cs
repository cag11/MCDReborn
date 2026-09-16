using System;
using System.Collections.Generic;
using System.Linq;
#nullable enable

namespace MCDSaveEdit.Logic
{
    /// <summary>
    /// Capes and pets, which are recolourable and were never offered.
    ///
    /// Recolor Gear listed armor and the two weapon kinds, because those are what
    /// <see cref="Data.ItemDatabase"/> holds: it is built from the items a save can carry, and a
    /// cape is not one of those. A cape is a cosmetic, worn rather than carried, and it never
    /// appeared in any list this app builds. Nothing about it is harder to recolour - the paks
    /// keep each cape's texture in a folder named after the cape, which is the same shape armor
    /// uses - so this is a gap in what was listed rather than in what was possible.
    ///
    ///   capes   Actors/Characters/Player/Capes/Skins/&lt;Name&gt;/T_&lt;Name&gt;
    ///   pets    Actors/Characters/Friendlies/Pets/&lt;Name&gt;/T_&lt;something&gt;
    ///
    /// Pets are messier than capes and the naming is not to be trusted: the texture is not always
    /// named after the folder, and a few folders carry no texture of their own at all - they
    /// borrow another's, or tint a shared one through a material. Those are left out rather than
    /// listed as something that cannot be recoloured.
    ///
    /// Found by reading the paks rather than from a list written here, so what is offered follows
    /// the game and the DLC that are installed.
    /// </summary>
    public static class CosmeticSkins
    {
        /// <summary>A cape or a pet, and the texture that colours it.</summary>
        public sealed class Entry
        {
            public Entry(string id, string name, string texturePath)
            {
                Id = id; Name = name; TexturePath = texturePath;
            }

            /// <summary>The folder the game keeps it in, which is as close to an id as it has.</summary>
            public string Id { get; }

            /// <summary>Something readable, since none of these have a name in the game's text.</summary>
            public string Name { get; }

            public string TexturePath { get; }
        }

        private const string CAPES = "/player/capes/skins/";
        private const string PETS = "/friendlies/pets/";

        private static IReadOnlyList<Entry>? _capes;
        private static IReadOnlyList<Entry>? _pets;

        public static IReadOnlyList<Entry> capes() => _capes ??= scan(CAPES);

        public static IReadOnlyList<Entry> pets() => _pets ??= scan(PETS);

        /// <summary>Forgets what was found, for when different game content is loaded.</summary>
        public static void reset() { _capes = null; _pets = null; _enchantments = null; _ui = null; }

        public static Entry? find(string? id)
        {
            if (id == null) { return null; }
            return capes().Concat(pets()).Concat(enchantmentIcons()).Concat(userInterface())
                .FirstOrDefault(entry => string.Equals(entry.Id, id, StringComparison.OrdinalIgnoreCase));
        }

        //Only the folders the game keeps its interface art in. The rest of /UI/ is widgets and
        //logic that happen to sit beside it, and listing those meant most of a very long list
        //being things that cannot be opened at all.
        private const string UI = "/content/ui/materials/";

        private static IReadOnlyList<Entry>? _ui;

        //Everything under UI that is not a picture: widgets, materials, blueprints, enums. Sorted
        //out by name because the alternative is opening eighteen hundred assets to find out, and
        //that takes long enough to be felt every time the tab is opened.
        private static readonly string[] NOT_PICTURES = {
            "UMG_", "WBP_", "BP_", "BPL_", "MI_", "M_", "MPC_", "MF_", "PS_", "SM_", "SK_",
            "AnimBP", "E_", "S_", "Cue_", "DT_",
        };

        private static readonly string[] NOT_PICTURE_FOLDERS = {
            "/enums/", "/materialfunctions/", "/blueprints/", "/structs/",
        };

        /// <summary>
        /// The game's own interface art: the hotbar, the inventory, chests, the map, status
        /// effects, loading screens and the rest.
        ///
        /// The same textures as anything else here, and in the same folders the game draws them
        /// from, so repainting one repaints the interface. Names are kept as the game has them
        /// and shown with the folder they came from, since "hotbar_slot" on its own says less
        /// than "HotBar2 · hotbar_slot".
        ///
        /// Not all of them open. A few, the mouse cursors among them, are stored in a form this
        /// reader cannot decode, and those say so when they are picked rather than being hunted
        /// down and removed from the list at load, which would mean opening every one of them
        /// first.
        /// </summary>
        public static IReadOnlyList<Entry> userInterface()
        {
            if (_ui != null) { return _ui; }

            var found = new List<Entry>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (var entry in CustomSkins.index ?? Enumerable.Empty<string>())
            {
                var path = entry.Replace('\\', '/');
                var lower = path.ToLowerInvariant();
                if (lower.IndexOf(UI, StringComparison.Ordinal) < 0) { continue; }
                if (NOT_PICTURE_FOLDERS.Any(folder => lower.Contains(folder))) { continue; }

                var name = path.Substring(path.LastIndexOf('/') + 1);
                if (NOT_PICTURES.Any(prefix => name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))) { continue; }

                var asset = CustomSkins.assetPath(path);
                if (!seen.Add(asset)) { continue; }

                found.Add(new Entry(asset, label(asset), asset));
            }

            return _ui = found
                .OrderBy(entry => entry.Name, StringComparer.CurrentCultureIgnoreCase)
                .ToList();
        }

        /// <summary>The folder it is drawn from, then its own name.</summary>
        private static string label(string assetPath)
        {
            var parts = assetPath.Split('/');
            if (parts.Length < 2) { return assetPath; }
            return parts[parts.Length - 2] + "  \u00b7  " + parts[parts.Length - 1];
        }

        private const string ENCHANTMENTS = "/enchantments/";

        private static IReadOnlyList<Entry>? _enchantments;

        /// <summary>
        /// The picture on an enchantment, which is a texture like any other.
        ///
        /// Not found the way gear is. The gear search skips anything ending in "_Icon" on purpose,
        /// because for a weapon that is the little inventory sprite rather than the skin on the
        /// model - but an enchantment has no model, and that sprite is the whole of what it looks
        /// like. So it is looked up by the name the game gives it, T_&lt;Name&gt;_Icon, and the
        /// "Shine" beside it is left alone: that is the glow drawn over the top, not the icon.
        ///
        /// Only the enchantments the game actually draws have one. The ones it never offers have
        /// no icon at all, which is exactly why this app could not list them until they were named
        /// outright, and why there is nothing here to repaint for them.
        /// </summary>
        public static IReadOnlyList<Entry> enchantmentIcons()
        {
            if (_enchantments != null) { return _enchantments; }

            var found = new List<Entry>();
            foreach (var entry in CustomSkins.index ?? Enumerable.Empty<string>())
            {
                var path = entry.Replace('\\', '/');
                var at = path.ToLowerInvariant().IndexOf(ENCHANTMENTS, StringComparison.Ordinal);
                if (at < 0) { continue; }

                var rest = path.Substring(at + ENCHANTMENTS.Length).Trim('/');
                var parts = rest.Split('/');
                if (parts.Length < 2) { continue; }

                var folder = parts[0];
                var file = parts[parts.Length - 1];
                if (!string.Equals(file, "T_" + folder + "_Icon", StringComparison.OrdinalIgnoreCase)) { continue; }

                found.Add(new Entry(folder, Services.R.enchantmentName(folder), CustomSkins.assetPath(path)));
            }

            return _enchantments = found
                .GroupBy(entry => entry.Id, StringComparer.OrdinalIgnoreCase)
                .Select(group => group.First())
                .OrderBy(entry => entry.Name, StringComparer.CurrentCultureIgnoreCase)
                .ToList();
        }

        /// <summary>
        /// Every folder under one of those paths that owns a colour texture.
        ///
        /// A folder is only worth listing if something in it can be painted, so one without a
        /// colour map is dropped here rather than listed and then refusing to open.
        /// </summary>
        private static IReadOnlyList<Entry> scan(string marker)
        {
            var byFolder = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);

            foreach (var entry in CustomSkins.index ?? Enumerable.Empty<string>())
            {
                var path = entry.Replace('\\', '/');
                var at = path.ToLowerInvariant().IndexOf(marker, StringComparison.Ordinal);
                if (at < 0) { continue; }

                var rest = path.Substring(at + marker.Length).Trim('/');
                var parts = rest.Split('/');
                if (parts.Length < 2) { continue; }

                //Both tests, as the gear search does: "T_" is what marks a texture apart from the
                //blueprints and meshes beside it, and isColourMap then drops the maps that are
                //not colour. Without the first, a pet folder offered up its blueprint.
                var file = parts[parts.Length - 1];
                if (!file.StartsWith("T_", StringComparison.OrdinalIgnoreCase)) { continue; }
                if (!CustomSkins.isColourMap(file)) { continue; }

                if (!byFolder.TryGetValue(parts[0], out var textures))
                {
                    textures = new List<string>();
                    byFolder[parts[0]] = textures;
                }
                textures.Add(CustomSkins.assetPath(path));
            }

            return byFolder
                .Select(pair => new Entry(pair.Key, readable(pair.Key), pick(pair.Key, pair.Value)))
                .OrderBy(entry => entry.Name, StringComparer.CurrentCultureIgnoreCase)
                .ToList();
        }

        /// <summary>
        /// The one that carries the colour, where a folder holds more than one.
        ///
        /// A texture named after its folder is the sure thing when it exists; otherwise the
        /// shortest name wins, because the extras are variations on it - "_UV", "_E", a
        /// spritesheet - and the plain one is what the model wears.
        /// </summary>
        private static string pick(string folder, List<string> textures)
        {
            var named = textures.FirstOrDefault(path =>
                path.EndsWith("/T_" + folder, StringComparison.OrdinalIgnoreCase));
            return named ?? textures.OrderBy(path => path.Length).First();
        }

        /// <summary>
        /// A folder name made readable: "CowCrusader_Cape" becomes "Cow Crusader Cape".
        ///
        /// These have no entry in the game's text - the game never shows the player a cape's
        /// internal name - so the name has to be made rather than looked up.
        /// </summary>
        private static string readable(string folder)
        {
            var spaced = new System.Text.StringBuilder();
            for (int i = 0; i < folder.Length; i++)
            {
                var c = folder[i];
                if (c == '_') { spaced.Append(' '); continue; }

                //A capital starts a new word, unless it is part of a run of them such as UV.
                if (i > 0 && char.IsUpper(c) && !char.IsUpper(folder[i - 1]) && folder[i - 1] != ' ')
                {
                    spaced.Append(' ');
                }
                spaced.Append(c);
            }
            //"CowCrusader_Cape" would otherwise come out with two spaces in the middle, the
            //underscore's and the capital's.
            return string.Join(" ", spaced.ToString()
                .Split(' ', StringSplitOptions.RemoveEmptyEntries));
        }
    }
}
