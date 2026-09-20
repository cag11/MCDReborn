using MCDSaveEdit.Services;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
#nullable enable

namespace MCDSaveEdit.Logic
{
    /// <summary>
    /// The game's music, biggest first.
    ///
    /// There are 1,658 assets under AudioForce whose names begin bgm_, and almost all of them are
    /// not music. Stings, one-bar cues, intro flourishes and combat layers all carry the same
    /// prefix as the full tracks, and nothing in the name says which is which - bgm_env_arhaMain
    /// and bgm_cin_cacaIntro look alike and are thirty seconds apart in length.
    ///
    /// Size sorts them properly, because a two minute track cannot help being larger than a two
    /// second cue. It is a proxy rather than a measurement, but it is a good one and it costs
    /// nothing: the pak index already records the size of every entry, so the whole catalogue is
    /// built without decoding a single note.
    /// </summary>
    public static class GameMusic
    {
        /// <summary>
        /// Where the game keeps its sound waves.
        ///
        /// No leading slash, deliberately. A PakFileReader spells its entries relative to that
        /// pak's own mount point - "AudioForce/02_audio_soundWave/x.uexp" - while the asset index
        /// spells the same thing "//Dungeons/Content/AudioForce/...". Matching on "/AudioForce"
        /// therefore found nothing at all, in every pak, silently.
        /// </summary>
        private const string FOLDER = "AudioForce/02_audio_soundWave/";

        /// <summary>What a music asset is called. Cues and tracks share it; size tells them apart.</summary>
        private const string PREFIX = "bgm";

        /// <summary>
        /// What the game's four letter codes mean, where that is actually known.
        ///
        /// The audio team named tracks after the mission they belong to, two letters per word:
        /// crwo is Creeper Woods, sqco is Squid Coast. Once you see it the whole list reads, and
        /// without it bgm_env_crwoInn-001 is unguessable.
        ///
        /// Only codes that are certain are listed. Several are not - rune2dOrganMain and rero
        /// appear repeatedly and could be argued either way, and a confident wrong label is worse
        /// than none at all when somebody is choosing which track to overwrite. Those simply show
        /// their raw name.
        ///
        /// Longest first, because the descriptive names have to be tested before the four letter
        /// codes - "mooshroomMain" would otherwise never be reached.
        /// </summary>
        private static readonly (string code, string place)[] PLACES =
        {
            ("loadingScreen", "Loading screen"),
            ("titleScreen", "Title screen"),
            ("missionFinished", "Mission complete"),
            ("missionPost", "Mission complete"),
            ("missionMap", "Mission map"),
            ("greatHall", "Great Hall"),
            ("mooshroom", "Mooshroom Island"),
            ("jukebox", "Jukebox"),
            ("credits", "Credits"),
            ("lobbyShop", "Camp - merchants"),
            ("lobbyMain", "Camp"),
            ("crypt", "Creepy Crypt"),
            ("menu", "Menu"),
            ("tent", "Camp - tent"),

            //The mission codes. "fifio" is the game's own typo for Fiery Forge and appears on one
            //of the longest tracks in the list, so it is matched rather than corrected.
            ("arha", "Arch Haven"),
            ("caca", "Cacti Canyon"),
            ("crwo", "Creeper Woods"),
            ("dete", "Desert Temple"),
            ("fifio", "Fiery Forge"),
            ("fifo", "Fiery Forge"),
            ("hiha", "Highblock Halls"),
            ("mois", "Mooshroom Island"),
            ("obpi", "Obsidian Pinnacle"),
            ("pupa", "Pumpkin Pastures"),
            ("remi", "Redstone Mines"),
            ("sosw", "Soggy Swamp"),
            ("sqco", "Squid Coast"),
        };

        public sealed class Track
        {
            public Track(string enginePath, string pakPath, long bytes)
            {
                EnginePath = enginePath;
                PakPath = pakPath;
                Bytes = bytes;
                Name = System.IO.Path.GetFileNameWithoutExtension(enginePath);
            }

            /// <summary>Where the game keeps it, spelled the way the engine wants: /Game/...</summary>
            public string EnginePath { get; }

            /// <summary>
            /// The same asset, spelled the way the pak index spells it.
            ///
            /// Two spellings because two readers want different ones, and mixing them up fails
            /// silently: extractPackage looks up "/Dungeons/Content/..." and simply returns nothing
            /// for a "/Game/..." path, while the pak writer wants the engine form. Keeping both
            /// beats converting between them at each call site and getting it wrong at one.
            /// </summary>
            public string PakPath { get; }

            public string Name { get; }

            /// <summary>The whole track: the .uexp's first chunk plus whatever streams from the .ubulk.</summary>
            public long Bytes { get; }

            /// <summary>
            /// Roughly how long it plays.
            ///
            /// Worth showing even though it is an estimate, because a number of seconds means
            /// something to a person and a number of kilobytes does not. Vorbis at the rate this
            /// game encodes at lands near 20 KB a second; the figure is only used for sorting and
            /// for telling a track from a sting, so being out by a fifth changes nothing.
            /// </summary>
            public int Seconds => (int)Math.Round(Bytes / 20000.0);

            /// <summary>Where it plays, if the name says so. Null when nothing is certain.</summary>
            public string? Place
            {
                get
                {
                    foreach (var (code, place) in PLACES)
                    {
                        if (Name.IndexOf(code, StringComparison.OrdinalIgnoreCase) >= 0) { return place; }
                    }
                    return null;
                }
            }

            /// <summary>The name with its place spelled out, for somebody choosing a track.</summary>
            public string Label => Place == null ? Name : $"{Name}  ({Place})";

            public override string ToString() => $"{Label} ({Bytes / 1024:N0} KB)";
        }

        /// <summary>
        /// What happened to each pak on the last scan.
        ///
        /// Kept because the failure mode here is silence: a pak that cannot be opened, or one whose
        /// key does not fit, produces no tracks and no complaint, which is indistinguishable from a
        /// game that has no music in it. That exact confusion has already cost one round.
        /// </summary>
        public static IReadOnlyList<string> Notes { get; private set; } = new List<string>();

        public static IReadOnlyList<Track> all()
        {
            var notes = new List<string>();
            Notes = notes;

            //The index the app already unlocked at startup, rather than opening the paks again.
            //
            //Opening them separately was tried and does not work: the game's paks are encrypted,
            //and even with the key a fresh PakFileReader came back initialised with zero entries.
            //The index has them, it is already paid for, and it knows every entry's size without
            //decompressing a byte - which for a thousand sound files is the difference between
            //instant and a minute of waiting.
            var index = CustomSkins.index;
            if (index == null)
            {
                notes.Add("the game's paks are not loaded");
                return new List<Track>();
            }

            var found = new Dictionary<string, (string pak, long bytes)>(StringComparer.OrdinalIgnoreCase);
            var seen = 0;

            foreach (var entry in index.AllEntries())
            {
                var path = entry.Key.Replace(System.IO.Path.DirectorySeparatorChar, '/');
                if (path.IndexOf(FOLDER, StringComparison.OrdinalIgnoreCase) < 0) { continue; }

                var name = System.IO.Path.GetFileName(path);
                if (!name.StartsWith(PREFIX, StringComparison.OrdinalIgnoreCase)) { continue; }

                seen++;

                //The .uexp AND the .ubulk, because a music track is both.
                //
                //Sorting by the .uexp alone put a wall of entries at exactly 256 KB at the top of
                //the list, which is a chunk boundary rather than a coincidence: these sound waves
                //stream, so the .uexp holds the first chunk and the rest is in a .ubulk beside it.
                //83 of the 112 have one, and one of them carries 1.5 MB there against 262 KB in
                //the .uexp - so measuring the .uexp is measuring the wrong thing entirely.
                //
                //Paks.Merge hangs both off the .uasset rather than listing them separately, which
                //is why the key has no extension and both payloads are reachable from one entry.
                var bytes = (entry.Value.Uexp?.UncompressedSize ?? entry.Value.UncompressedSize)
                          + (entry.Value.Ubulk?.UncompressedSize ?? 0);

                //Spelled the way the rest of the app spells an asset, so it can be handed straight
                //to the extractor and the pak writer.
                var at = path.IndexOf(FOLDER, StringComparison.OrdinalIgnoreCase);
                //One leading slash, not two. The index's enumerator joins its mount point onto a
                //key that already begins with one, so entries come out as "//Dungeons/Content/..."
                //- and extractPackage looks that up, finds nothing, and says nothing about it.
                var pak = "/" + path.TrimStart('/');

                found["/Game/" + path.Substring(at)] = (pak, bytes);
            }

            notes.Add($"{seen:N0} music assets in the index");

            return found
                .Select(one => new Track(one.Key, one.Value.pak, one.Value.bytes))
                .OrderByDescending(track => track.Bytes)
                .ThenBy(track => track.Name, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }
    }
}
