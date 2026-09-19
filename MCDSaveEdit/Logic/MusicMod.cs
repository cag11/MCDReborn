using MCDSaveEdit.Services;
using PakReader.Pak;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
#nullable enable

namespace MCDSaveEdit.Logic
{
    /// <summary>
    /// Replacing one of the game's music tracks with a file of your own.
    ///
    /// The shape is the same as Recolor Gear: read the game's cooked asset, put something else
    /// inside it, write the result into a mod pak beside the game's own. What makes music harder
    /// than a texture is that music streams. A sound wave keeps its first chunk in the .uexp and
    /// the rest in a .ubulk next to it - 83 of the game's 112 tracks have one, and the longest
    /// carries 1.5 MB there against 262 KB in the .uexp. Both files have to be written, and the
    /// chunk table that describes them has to agree with what was written.
    /// </summary>
    public static class MusicMod
    {
        /// <summary>What a music mod is called, so it can be found and removed on its own.</summary>
        private const string PREFIX = "Music_";

        /// <summary>Every music mod installed.</summary>
        public static IReadOnlyList<string> installed()
        {
            var folders = new List<string>();
            if (CustomSkins.paksFolder != null) { folders.Add(CustomSkins.paksFolder); }
            if (CustomSkins.modsFolder != null) { folders.Add(CustomSkins.modsFolder); }

            return folders
                .Where(Directory.Exists)
                .SelectMany(folder => Directory.GetFiles(
                    folder, CustomSkins.MOD_PREFIX + PREFIX + "*.pak"))
                .OrderBy(file => file, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        /// <summary>The mod that replaces one particular track, if there is one.</summary>
        public static string? installedFor(GameMusic.Track track)
        {
            var wanted = CustomSkins.MOD_PREFIX + PREFIX + safe(track.Name);

            return installed().FirstOrDefault(pak =>
                System.IO.Path.GetFileNameWithoutExtension(pak)
                    .StartsWith(wanted, StringComparison.OrdinalIgnoreCase));
        }

        /// <summary>Puts a track back to the game's own, by deleting what replaced it.</summary>
        public static bool remove(GameMusic.Track track)
        {
            var pak = installedFor(track);
            if (pak == null || !File.Exists(pak)) { return false; }

            File.Delete(pak);
            return true;
        }

        /// <summary>
        /// Replaces one track with the audio in a file.
        ///
        /// Reads the game's own asset, puts the new audio inside it, and writes the result into a
        /// mod pak beside the game's own paks. The game's files are never touched - removing the
        /// pak puts the original track back.
        /// </summary>
        public static CustomSkins.InstalledMod install(GameMusic.Track track, string file)
        {
            if (!File.Exists(file))
            {
                throw new InvalidOperationException("That file is not there.");
            }

            //Encoded once and handed on. A long track decodes to tens of millions of floats, and
            //doing it twice holds two copies of that at the same time for no reason at all.
            return install(track, MusicEncode.read(file));
        }

        /// <summary>The same, for audio that has already been read and encoded.</summary>
        public static CustomSkins.InstalledMod install(GameMusic.Track track, MusicEncode.Encoded audio)
        {
            var index = CustomSkins.index
                ?? throw new InvalidOperationException("The game's paks are not loaded.");

            //The index spelling, not the engine one - extractPackage returns nothing at all for
            //a /Game/... path rather than complaining about it.
            var package = index.extractPackage(track.PakPath)
                ?? throw new InvalidOperationException(
                    $"{track.Name} could not be read out of the game's paks.");

            var rebuilt = MusicSoundWave.rebuild(
                package.UAsset.ToArray(), package.UExp.ToArray(), audio);

            //Checked before it ships rather than after it is heard. A sound wave written wrongly
            //does not announce itself - it plays silence - so the rebuilt asset is read back with
            //the same reader the game's own assets go through, and the fields that were changed are
            //compared against what was asked for.
            confirm(rebuilt, audio, track);

            var inside = "Dungeons/Content/" + track.EnginePath.Substring("/Game/".Length);

            var entries = new List<PakWriter.Entry>
            {
                new PakWriter.Entry(inside + ".uasset", rebuilt.UAsset),
                new PakWriter.Entry(inside + ".uexp", rebuilt.UExp),
            };

            //No .ubulk at all when the audio fits in one chunk. Shipping an empty one would leave
            //the game reading a file that says it holds streamed chunks and does not.
            if (rebuilt.UBulk != null)
            {
                entries.Add(new PakWriter.Entry(inside + ".ubulk", rebuilt.UBulk));
            }

            return CustomSkins.writeModPak(PREFIX + safe(track.Name), entries);
        }

        /// <summary>
        /// That the rebuilt sound wave still reads as one, holding the audio just written.
        ///
        /// Modelled on CustomSkins.confirm, and for the same reason: every part is checked against
        /// something known beforehand rather than against itself. The properties are read back with
        /// the app's own reader - the one the game's assets go through - and the numbers compared
        /// with what the encoder reported.
        /// </summary>
        private static void confirm(MusicSoundWave.Rebuilt rebuilt, MusicEncode.Encoded audio,
            GameMusic.Track track)
        {
            IReadOnlyList<CookedProperties.Value> values;
            try
            {
                values = CookedProperties.readAll(rebuilt.UAsset, rebuilt.UExp);
            }
            catch (Exception problem)
            {
                throw new InvalidOperationException(
                    $"{track.Name} could not be rewritten - what came out no longer reads as a "
                    + $"cooked asset ({problem.Message}).");
            }

            if (values.Count == 0)
            {
                throw new InvalidOperationException(
                    $"{track.Name} could not be rewritten - what came out has no properties left.");
            }

            check(values, rebuilt.UExp, "SampleRate", audio.SampleRate, track);
            check(values, rebuilt.UExp, "NumChannels", audio.Channels, track);
        }

        private static void check(IReadOnlyList<CookedProperties.Value> values, byte[] uexp,
            string name, int wanted, GameMusic.Track track)
        {
            var found = values.FirstOrDefault(one => one.Name == name);

            //Absent is not wrong. Several of the game's own tracks simply do not carry every
            //field, and writing one that was never there would change the asset's length.
            if (found == null || found.At + 4 > uexp.Length) { return; }

            var got = BitConverter.ToInt32(uexp, found.At);
            if (got != wanted)
            {
                throw new InvalidOperationException(
                    $"{track.Name} came out with {name} = {got} where {wanted} was written.");
            }
        }

        /// <summary>A track name that is safe in a file name.</summary>
        private static string safe(string name)
        {
            var clean = new string(name
                .Select(c => char.IsLetterOrDigit(c) ? c : '_')
                .ToArray());

            return clean.Trim('_');
        }
    }
}
