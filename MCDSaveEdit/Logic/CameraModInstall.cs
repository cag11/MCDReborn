using MCDSaveEdit.Services;
using System;
using System.Collections.Generic;
#nullable enable

namespace MCDSaveEdit.Logic
{
    /// <summary>
    /// Installing a camera change, as opposed to working out what it should be.
    ///
    /// Kept apart from the rest of <see cref="CameraMod"/> because the workbench links that file
    /// and has no window, no image resolver and no notion of an installed mod - so anything
    /// reaching for those has to live where only the app will compile it. The reading and the
    /// patching are shared; only the installing is not.
    /// </summary>
    public static partial class CameraMod
    {
        public static bool ready => CustomSkins.ready;

        private static Dictionary<string, string>? _realPaths;

        /// <summary>
        /// The asset at this path, whatever case the index happens to want.
        ///
        /// The exact spelling is tried first and is what normally answers. The fallback exists
        /// because getting a capital wrong here does not raise anything - the lookup simply
        /// returns nothing, and a tab appears with no values in it and no explanation. A silent
        /// miss is worth one dictionary.
        /// </summary>
        private static PakReader.Pak.PakPackage? packageAt(PakReader.Pak.PakIndex paks, string assetPath)
        {
            try
            {
                var exact = paks.extractPackage(assetPath);
                if (exact != null) { return exact; }
            }
            catch (Exception) { }

            if (_realPaths == null)
            {
                _realPaths = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                foreach (var entry in paks)
                {
                    var path = entry.Replace('\\', '/');
                    var at = path.IndexOf("//", StringComparison.Ordinal);
                    if (at >= 0) { path = path.Substring(at + 1); }
                    _realPaths[path] = path;
                }
            }

            if (!_realPaths.TryGetValue(assetPath, out var real)) { return null; }

            try { return paks.extractPackage(real); }
            catch (Exception) { return null; }
        }


        /// <summary>
        /// What the camera is set to in the game as installed, read from the player character.
        ///
        /// Read rather than written down, so the numbers shown are the game's own even if a patch
        /// changes them. Everything offered is relative to these.
        /// </summary>
        public static Settings? readStock()
        {
            var paks = CustomSkins.index;
            if (paks == null) { return null; }

            foreach (var path in ASSETS)
            {
                try
                {
                    var package = packageAt(paks, path);
                    if (package == null) { continue; }

                    var found = read(package.Value.UAsset.ToArray(), package.Value.UExp.ToArray());
                    if (found != null) { return found; }
                }
                catch (Exception) { }
            }
            return null;
        }

        /// <summary>
        /// Writes a mod pak setting the camera to the given values.
        ///
        /// Both cameras, and the zoom volumes with them when the distance changed - a close camera
        /// without them is yanked out to the stock 3500 the first time you walk into a wide room,
        /// which reads as the mod failing rather than as the game doing what it always did.
        /// </summary>
        public static CustomSkins.InstalledMod apply(Settings settings, string modName)
        {
            var paks = CustomSkins.index
                ?? throw new InvalidOperationException("Game content is not loaded.");

            var entries = new List<PakWriter.Entry>();
            float? stockArm = null;

            foreach (var path in ASSETS)
            {
                var package = packageAt(paks, path);
                if (package == null) { continue; }

                var uasset = package.Value.UAsset.ToArray();
                var uexp = package.Value.UExp.ToArray();

                var current = read(uasset, uexp);
                if (current == null) { continue; }
                stockArm ??= current.ArmLength;

                //The seek length is what the arm reaches towards, so moving one without the other
                //leaves the camera pulling back to where it started.
                var forThis = copyOf(settings);
                if (forThis.ArmLength != null && forThis.SeekArmLength == null
                    && current.ArmLength > 0 && current.SeekArmLength != null)
                {
                    forThis.SeekArmLength = forThis.ArmLength * (current.SeekArmLength / current.ArmLength);
                }

                var patched = patch(uasset, uexp, forThis);
                var inside = path.TrimStart('/');
                entries.Add(new PakWriter.Entry(inside + ".uasset", uasset));
                entries.Add(new PakWriter.Entry(inside + ".uexp", patched));
            }

            if (entries.Count == 0) { throw new InvalidOperationException("No camera could be read from the game."); }

            if (settings.ArmLength != null && stockArm != null && stockArm > 0)
            {
                var factor = settings.ArmLength.Value / stockArm.Value;
                foreach (var path in ZOOM_VOLUMES)
                {
                    var volume = packageAt(paks, path);
                    if (volume == null) { continue; }

                    var volumeAsset = volume.Value.UAsset.ToArray();
                    var scaled = scaleZoom(volumeAsset, volume.Value.UExp.ToArray(), factor);
                    if (scaled == null) { continue; }

                    var inside = path.TrimStart('/');
                    entries.Add(new PakWriter.Entry(inside + ".uasset", volumeAsset));
                    entries.Add(new PakWriter.Entry(inside + ".uexp", scaled));
                }
            }

            return CustomSkins.writeModPak(modName, entries);
        }

        private static Settings copyOf(Settings from) => new Settings {
            ArmLength = from.ArmLength,
            SeekArmLength = from.SeekArmLength,
            Pitch = from.Pitch,
            Yaw = from.Yaw,
            Roll = from.Roll,
            FieldOfView = from.FieldOfView,
            InheritPitch = from.InheritPitch,
            InheritYaw = from.InheritYaw,
            InheritRoll = from.InheritRoll,
            CollisionTest = from.CollisionTest,
            RotationLagSpeed = from.RotationLagSpeed,
            SocketSide = from.SocketSide,
            SocketHeight = from.SocketHeight,
            TurnRate = from.TurnRate,
        };
    }
}
