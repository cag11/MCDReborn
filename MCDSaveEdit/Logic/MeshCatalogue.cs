using System.Collections.Generic;
using PakReader.Pak;
using System.Windows.Media.Imaging;
#nullable enable

namespace MCDSaveEdit.Logic
{
    /// <summary>One mesh somebody can pick, however it happens to be stored.</summary>
    public sealed class MeshEntry
    {
        public MeshEntry(string assetPath, string group, string name, string variant, string caution = "")
        {
            AssetPath = assetPath;
            Group = group;
            Name = name;
            Variant = variant;
            Caution = caution;
        }

        public string AssetPath { get; }
        /// <summary>Which heading it sits under, when the catalogue offers more than one.</summary>
        public string Group { get; }
        /// <summary>The mesh's own name, spaced out for reading.</summary>
        public string Name { get; }
        /// <summary>The folder it sits in, which is what tells two similar ones apart.</summary>
        public string Variant { get; }

        /// <summary>
        /// Why this one is likely to disappoint, or nothing when it is not.
        ///
        /// Said in the list rather than discovered afterwards. Finding out that a weapon cannot
        /// wear an imported model costs modelling it, fitting it, writing the pak, restarting the
        /// game and looking - and then leaves somebody with no way to tell whether the fault was
        /// theirs or the weapon's.
        /// </summary>
        public string Caution { get; }

        public override string ToString() => Name;
    }

    /// <summary>
    /// A set of meshes that can be replaced, and the replacing of them.
    ///
    /// This exists so that one tab can serve two of them. The weapon tab was written first and the
    /// creature tab wanted every part of it - the list, the search, the ghost, the sliders, the
    /// dragging, the framing, the wording that tells somebody what went wrong - and the only thing
    /// that genuinely differed was which meshes are offered and how they are written back. So the
    /// difference is here and the tab is shared, rather than there being two of a six hundred line
    /// control drifting apart a fix at a time.
    /// </summary>
    public abstract class MeshCatalogue
    {
        /// <summary>Whether the game's content has been found at all.</summary>
        public abstract bool ready { get; }

        public abstract IReadOnlyList<MeshEntry> all();

        /// <summary>The shape of one mesh, or nothing when its geometry cannot be read.</summary>
        public abstract MeshShape? read(string assetPath);

        /// <summary>The artwork it is painted with, for showing the preview as it really looks.</summary>
        public abstract BitmapSource? textureFor(string assetPath);

        /// <summary>
        /// Writes a mod pak replacing the mesh with an imported model.
        ///
        /// `extra` is anything else that belongs in the same pak - the glow, at the moment. It
        /// goes in here rather than being written as a pak of its own, because two paks changing
        /// one weapon are two things to remember to remove.
        /// </summary>
        public abstract CustomSkins.InstalledMod replace(string assetPath, GlbModel model,
            MeshEdit.Transform transform, string modName,
            IEnumerable<PakWriter.Entry>? extra = null);

        /// <summary>
        /// Writes a mod pak moving the mesh's own vertices, without changing how many there are.
        ///
        /// Only offered where it means something. Moving a weapon's vertices makes a longer sword;
        /// moving a creature's makes a creature whose skeleton no longer matches its skin, which
        /// looks like a fault rather than an edit, so the creature catalogue declines it.
        /// </summary>
        public virtual CustomSkins.InstalledMod reshape(string assetPath, MeshEdit.Transform transform,
            string modName, IEnumerable<PakWriter.Entry>? extra = null)
            => throw new System.NotSupportedException();

        public virtual bool canReshape => true;

        //------------------------------------------------------------------ what the tab is called
        public abstract string subjectLabel { get; }
        public abstract string countFormat { get; }
        /// <summary>The line under the list saying what is and is not in it.</summary>
        public abstract string scopeNote { get; }
        public abstract string ghostHint { get; }
        public abstract string importHint { get; }
        /// <summary>What to say when the sliders have been moved but nothing has been imported.</summary>
        public abstract string nothingToDo { get; }

        /// <summary>
        /// Anything worth saying about one heading in particular, under the list.
        ///
        /// Advice rather than warning. The mark against an entry means an import onto it comes
        /// out wrong, and putting ordinary guidance there makes every entry look broken.
        /// </summary>
        public virtual string noteFor(string? group) => string.Empty;

        /// <summary>The headings the list can be filtered by, or nothing when there is only one.</summary>
        public virtual IReadOnlyList<string> groups() => System.Array.Empty<string>();
    }
}
