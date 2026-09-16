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
        public static void reset() { _capes = null; _pets = null; }

        public static Entry? find(string? id)
        {
            if (id == null) { return null; }
            return capes().Concat(pets())
                .FirstOrDefault(entry => string.Equals(entry.Id, id, StringComparison.OrdinalIgnoreCase));
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
