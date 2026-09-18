using MCDSaveEdit.Services;
using PakReader.Pak;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Windows.Media.Imaging;
#nullable enable

namespace MCDSaveEdit.Logic
{
    /// <summary>
    /// The creatures whose shape can be replaced, and the replacing of it.
    ///
    /// A weapon hangs off a hand; a creature walks. That is the whole reason this exists as well
    /// as the weapon catalogue rather than instead of it - a model put on a sheep is a model that
    /// wanders the level under its own power, gets summoned by an artifact, and can be stood on.
    /// A skateboard, a mount, a truck.
    ///
    /// What is offered is every skeletal mesh that <see cref="CookedSkeletalMesh"/> can actually
    /// walk, which is about a fifth of the ones in the game. The rest are left out rather than
    /// listed and refused: a creature drawn at several levels of detail, or in several pieces, or
    /// with cloth on it, is one this cannot rewrite, and finding that out by clicking it is worse
    /// than not seeing it. The list is built by opening every one of them, which sounds expensive
    /// and takes about a second, so it is done properly rather than guessed at from the path.
    /// </summary>
    public static class MobMeshes
    {
        public const string CREATURES = "Creatures";
        public const string PROPS = "Props";

        private static List<MeshEntry>? _catalogue;

        public static bool ready => CustomSkins.ready;

        /// <summary>
        /// Every skeletal mesh this can rewrite.
        ///
        /// Built once and kept, because it is built by reading several hundred packages and the
        /// answer cannot change while the game's paks sit where they are.
        /// </summary>
        public static IReadOnlyList<MeshEntry> all()
        {
            if (_catalogue != null) { return _catalogue; }

            var paks = CustomSkins.index;
            if (paks == null) { return Array.Empty<MeshEntry>(); }

            var found = new List<MeshEntry>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (var entry in paks)
            {
                var path = entry.Replace("\\", "/");
                var at = path.IndexOf("//", StringComparison.Ordinal);
                if (at >= 0) { path = path.Substring(at + 1); }

                //A name beginning SK_ is the engine's own convention for a skeletal mesh and is
                //not a promise - it is used for a few other things too - but whatever is not one
                //fails to open, which is a stricter test than any name could be.
                var file = path.Substring(path.LastIndexOf('/') + 1);
                if (!file.StartsWith("sk_", StringComparison.OrdinalIgnoreCase)) { continue; }
                if (!seen.Add(path)) { continue; }

                if (!usable(path)) { continue; }
                found.Add(new MeshEntry(path, groupOf(path), prettyName(path), folderName(path)));
            }

            _catalogue = found
                .OrderBy(mesh => mesh.Group == CREATURES ? 0 : 1)
                .ThenBy(mesh => mesh.Name, StringComparer.CurrentCultureIgnoreCase)
                .ToList();
            return _catalogue;
        }

        private static bool usable(string assetPath)
        {
            var package = readPackage(assetPath);
            if (package == null) { return false; }

            try
            {
                return CookedSkeletalMesh.open(package.Value.UAsset.ToArray(), package.Value.UExp.ToArray(), out _) != null;
            }
            catch (Exception)
            {
                //A mesh that throws on the way through is one this cannot rewrite, which is worth
                //nothing more than leaving it out of the list.
                return false;
            }
        }

        /// <summary>
        /// Where in the list a mesh belongs.
        ///
        /// Creatures first, because a creature is the thing worth replacing - it moves. The rest
        /// are gates, doors, windmills and banners, which are animated props rather than static
        /// ones and are perfectly replaceable, just standing still while they are.
        /// </summary>
        private static string groupOf(string assetPath) =>
            assetPath.IndexOf("/characters/", StringComparison.OrdinalIgnoreCase) >= 0 ||
            assetPath.IndexOf("/animals/", StringComparison.OrdinalIgnoreCase) >= 0 ||
            assetPath.IndexOf("/enemies/", StringComparison.OrdinalIgnoreCase) >= 0
                ? CREATURES
                : PROPS;

        /// <summary>The shape of one creature, or nothing when its geometry cannot be read.</summary>
        public static MeshShape? read(string assetPath)
        {
            var package = readPackage(assetPath);
            if (package == null) { return null; }

            var mesh = CookedSkeletalMesh.open(package.Value.UAsset.ToArray(), package.Value.UExp.ToArray(), out _);
            if (mesh == null) { return null; }

            var geometry = mesh.readGeometry();

            //Channel zero is the artwork, and on a creature it is usually the only one there is.
            var texCoords = new List<(float u, float v)>(mesh.VertexCount);
            for (int i = 0; i < mesh.VertexCount; i++)
            {
                var pick = i * mesh.NumTexCoords;
                texCoords.Add(pick < geometry.TexCoords.Count ? geometry.TexCoords[pick] : (0f, 0f));
            }

            return new MeshShape(geometry.Positions, geometry.Indices,
                mesh.Origin, mesh.Extent, mesh.Radius, texCoords);
        }

        /// <summary>
        /// Writes a mod pak replacing a creature's mesh with an imported model.
        ///
        /// The texture goes in the same pak when the model brought one, and that is not a
        /// convenience - it is required. An imported model carries its own texture coordinates,
        /// which have nothing to do with how the creature's artwork was laid out, so the new mesh
        /// wearing the old texture would show the right shape painted with nonsense.
        ///
        /// It goes onto *every* skin the creature has rather than the one beside the mesh, and the
        /// game's own colouring is turned off at the same time. Both of those are about summoned
        /// creatures, which is what a mount is: Enchanted Grass spawns one of three variants that
        /// share this mesh but keep their own texture in another folder and add a hot red, green
        /// or blue on top of it. Painting only the ordinary skin left a model that came out right
        /// everywhere except the one place somebody actually wanted it. See
        /// <see cref="CreatureVariants"/>.
        /// </summary>
        public static CustomSkins.InstalledMod import(string assetPath, GlbModel model,
            MeshEdit.Transform transform, string modName)
        {
            var package = readPackage(assetPath)
                ?? throw new InvalidOperationException($"Could not read {assetPath}.");

            var mesh = CookedSkeletalMesh.open(package.UAsset.ToArray(), package.UExp.ToArray(), out var why)
                ?? throw new InvalidOperationException(why);

            var (uasset, uexp) = mesh.rebuild(ModelFitting.place(model, transform, mesh.NumTexCoords));

            var insidePak = assetPath.TrimStart('/');
            var entries = new List<PakWriter.Entry> {
                new PakWriter.Entry(insidePak + ".uasset", uasset),
                new PakWriter.Entry(insidePak + ".uexp", uexp),
            };
            if (package.UBulk != null)
            {
                entries.Add(new PakWriter.Entry(insidePak + ".ubulk", package.UBulk.Value.ToArray()));
            }

            if (model.BaseColourPng != null)
            {
                foreach (var texture in CreatureVariants.coloursOf(assetPath))
                {
                    //Not caught. A skin this cannot rewrite would leave the creature wearing the
                    //game's artwork in whichever form it was summoned in, which is the fault this
                    //whole path exists to fix, so it is said rather than swallowed.
                    entries.AddRange(CustomSkins.texturePatchAt(texture, model.BaseColourPng));
                }
            }

            entries.AddRange(CreatureVariants.calm(assetPath, out _));

            return CustomSkins.writeModPak(modName, entries);
        }

        public static BitmapSource? textureFor(string meshAssetPath)
        {
            var texture = CustomSkins.textureBeside(meshAssetPath);
            if (texture == null) { return null; }

            try { return CustomSkins.preview(texture); }
            catch (Exception) { return null; }
        }

        private static PakPackage? readPackage(string assetPath)
        {
            var paks = CustomSkins.index;
            if (paks == null) { return null; }

            try
            {
                var package = paks.extractPackage(assetPath);
                if (package == null || !package.Value.HasExport()) { return null; }
                return package;
            }
            catch (Exception) { return null; }
        }

        private static string folderName(string assetPath)
        {
            var parts = assetPath.TrimEnd('/').Split('/');
            return parts.Length >= 2 ? spaced(parts[parts.Length - 2]) : string.Empty;
        }

        private static string prettyName(string assetPath)
        {
            var file = assetPath.Substring(assetPath.LastIndexOf('/') + 1);
            if (file.StartsWith("sk_", StringComparison.OrdinalIgnoreCase)) { file = file.Substring(3); }
            return spaced(file);
        }

        private static string spaced(string raw)
        {
            var words = raw.Replace('_', ' ').Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
            var text = new StringBuilder();
            foreach (var word in words)
            {
                if (text.Length > 0) { text.Append(' '); }
                text.Append(CultureInfo.CurrentCulture.TextInfo.ToTitleCase(word));
            }
            return text.Length == 0 ? raw : text.ToString();
        }

        /// <summary>This catalogue, as the shared workshop tab wants it.</summary>
        public static readonly MeshCatalogue catalogue = new Catalogue();

        private sealed class Catalogue : MeshCatalogue
        {
            public override bool ready => MobMeshes.ready;
            public override IReadOnlyList<MeshEntry> all() => MobMeshes.all();
            public override MeshShape? read(string assetPath) => MobMeshes.read(assetPath);
            public override BitmapSource? textureFor(string assetPath) => MobMeshes.textureFor(assetPath);

            public override CustomSkins.InstalledMod replace(string assetPath, GlbModel model,
                MeshEdit.Transform transform, string modName)
                => import(assetPath, model, transform, modName);

            //A creature's vertices are driven by its skeleton, so moving them without moving the
            //bones leaves a mesh that comes apart the moment it walks. Replacing is offered and
            //reshaping is not, which is the honest way round.
            public override bool canReshape => false;

            public override IReadOnlyList<string> groups() => new[] { CREATURES, PROPS };

            public override string subjectLabel => R.MOB_SKINS_MOB;
            public override string countFormat => R.MOB_SKINS_COUNT;
            public override string scopeNote => R.MOB_SKINS_SCOPE;
            public override string ghostHint => R.MOB_SKINS_GHOST_HINT;
            public override string importHint => R.MOB_SKINS_IMPORT_HINT;
            public override string nothingToDo => R.MOB_SKINS_NEEDS_MODEL;
        }
    }
}
