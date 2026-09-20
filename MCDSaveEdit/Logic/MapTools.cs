using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
#nullable enable

namespace MCDSaveEdit.Logic
{
    /// <summary>
    /// The bridge between a mission and Minecraft.
    ///
    /// A tile's geometry is Minecraft blocks, so the natural editor for one is Minecraft. The
    /// conversion both ways is the community's, in Python, and it is good - better than a rewrite
    /// would be - so this drives it rather than replacing it. What it adds is the part that was
    /// missing: knowing which object group a world came from, so coming back needs no arguments.
    ///
    /// Everything it needs lives in one folder with its own Python inside it, so nothing has to
    /// be installed and nothing on the machine is changed.
    /// </summary>
    public static class MapTools
    {
        /// <summary>The note left in a converted world, so it can find its way home.</summary>
        public const string MARKER = "mcdreborn.json";

        private const string TO = "to_minecraft.py";
        private const string FROM = "from_minecraft.py";
        private const string FIX = "make_fixed.py";
        private const string WELD = "make_single.py";
        private const string TO_LEVEL = "to_minecraft_level.py";
        private const string FROM_LEVEL = "from_minecraft_level.py";

        //Chunker converts a world between Minecraft versions. It is a jar rather than a script,
        //because nothing in Python can do this: the chunk format has been rewritten twice since
        //1.16 - sections moved to the chunk root, world height went to -64..319, level.dat was
        //split apart - and the only maintained converter that keeps up is this one.
        private const string CHUNKER = "chunker-cli.jar";
        private const string MAPPINGS = "block_mappings.json";

        /// <summary>
        /// The Minecraft everything downstream of a world understands.
        ///
        /// The only version this has to name. A world goes OUT as it was written and Minecraft
        /// upgrades it on open - relighting it, rebuilding heightmaps, fixing up everything a
        /// converter would otherwise have to guess at - and it does that for every version that
        /// will ever exist without anybody here hearing about it. Coming back is the direction
        /// Minecraft cannot do, and the only one worth owning.
        /// </summary>
        public const string LEGACY = "JAVA_1_16_2";

        /// <summary>The note a whole-level world carries, naming the file each room belongs to.</summary>
        public const string LEVEL_MARKER = "mcdreborn-origin.json";

        /// <summary>
        /// Where the converter is.
        ///
        /// Looked for rather than configured, in the places somebody would reasonably put it. A
        /// setting would be one more thing to get wrong for a folder that either exists or does
        /// not.
        /// </summary>
        public static string? folder
        {
            get
            {
                foreach (var candidate in places())
                {
                    if (File.Exists(Path.Combine(candidate, TO))
                        && File.Exists(python(candidate)))
                    {
                        return candidate;
                    }
                }
                return null;
            }
        }

        private static IEnumerable<string> places()
        {
            var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);

            yield return Path.Combine(home, "MCD-MapTools");
            yield return Path.Combine(local, "MCDReborn", "MapTools");
            yield return Path.Combine(AppContext.BaseDirectory, "MapTools");
        }

        /// <summary>Where the converter would go, for telling somebody who has not got it.</summary>
        public static string wanted => places().First();

        public static bool available => folder != null;

        private static string python(string tools)
            => Path.Combine(tools, "env", "Scripts", "python.exe");

        /// <summary>Minecraft's world list, if it is installed.</summary>
        public static string? saves
        {
            get
            {
                var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
                var found = Path.Combine(appData, ".minecraft", "saves");
                return Directory.Exists(found) ? found : null;
            }
        }

        public sealed class Run
        {
            public Run(bool ok, string output, string? world)
            {
                Ok = ok;
                Output = output;
                World = world;
            }

            public bool Ok { get; }
            public string Output { get; }

            /// <summary>Where the world ended up, for the trip out.</summary>
            public string? World { get; }

            /// <summary>The last line that said anything, for a one line status.</summary>
            public string Last => Output
                .Split('\n')
                .Select(one => one.Trim())
                .LastOrDefault(one => one.Length > 0) ?? string.Empty;
        }

        /// <summary>
        /// Turns one object group of an exported mission into a Minecraft world.
        ///
        /// The world is named after the mission and the group together, because a mission has
        /// several groups and they are separate sets of tiles - Creeper Woods has sixteen, and a
        /// world holding all of them at once would be thousands of tiles laid out wherever their
        /// authors happened to build them.
        /// </summary>
        public static async Task<Run> toMinecraft(string mapFolder, string group,
            GameMaps.Mission mission, CancellationToken cancel = default)
        {
            var tools = folder ?? throw new InvalidOperationException(missing());

            var world = safe($"{mission.Name} - {group.Replace('/', ' ')}");
            var run = await start(tools, TO, new[] { mapFolder, group, world }, cancel)
                .ConfigureAwait(false);

            var into = saves != null
                ? Path.Combine(saves, world)
                : Path.Combine(mapFolder, "minecraft", world);

            if (!Directory.Exists(into))
            {
                return new Run(false, run.Output, null);
            }

            //The note that makes coming back one click. Without it, importing would have to ask
            //which object group of which mission a world belongs to - which nobody remembers a
            //week later, and getting it wrong writes tiles into the wrong file.
            var groupFile = Path.Combine(mapFolder, "objectgroups",
                group.Replace('/', Path.DirectorySeparatorChar), "objectgroup.json");

            File.WriteAllText(Path.Combine(into, MARKER), JsonSerializer.Serialize(new
            {
                map = mapFolder,
                mission = mission.Name,
                group,
                objectgroup = groupFile,
                exported = DateTime.Now.ToString("yyyy-MM-dd HH:mm"),
            }, new JsonSerializerOptions { WriteIndented = true }));

            return new Run(true, run.Output, into);
        }

        /// <summary>
        /// Rewrites an exported level so it builds the same way every time.
        ///
        /// A mission is a list of stretches, and a stretch varies three ways: which tile it takes
        /// from a list, how many it lays down, and whether side-paths branch off it. Pinning all
        /// three turns a mission that is different on every run into a fixed sequence of rooms -
        /// which is the difference between finding the room you edited and hunting for it.
        ///
        /// The tiles are chosen to be different from one another rather than simply first. Lower
        /// Temple has four stretches drawing on one group, so taking the first option gave the
        /// same corridor four times, which is no easier to navigate than a random level.
        ///
        /// The original level is kept beside the new one as level.json.random.
        /// </summary>
        public static async Task<Run> makeFixed(string mapFolder, CancellationToken cancel = default)
        {
            var tools = folder ?? throw new InvalidOperationException(missing());
            return await start(tools, FIX, new[] { mapFolder }, cancel).ConfigureAwait(false);
        }

        /// <summary>
        /// The whole mission as one world, its rooms joined at their doors in playing order.
        ///
        /// The plain export puts tiles wherever their authors built them - scattered, in no
        /// order, with gaps. This walks the pinned level start to end and places each room
        /// against a door of the one before it, which is what the game's generator does.
        ///
        /// It is a working view, not a change to how the mission is built. The game still
        /// assembles it at run time and picks its own doors, so something built across a seam
        /// lines up here and may not line up in game. Inside a room everything is exact.
        /// </summary>
        public static async Task<Run> toMinecraftLevel(string mapFolder, GameMaps.Mission mission,
            CancellationToken cancel = default)
        {
            var tools = folder ?? throw new InvalidOperationException(missing());

            var world = safe($"{mission.Name} - whole level");
            var run = await start(tools, TO_LEVEL, new[] { mapFolder, world }, cancel)
                .ConfigureAwait(false);

            var into = saves != null
                ? Path.Combine(saves, world)
                : Path.Combine(mapFolder, "minecraft", world);

            return Directory.Exists(into)
                ? new Run(true, run.Output, into)
                : new Run(false, run.Output, null);
        }

        /// <summary>
        /// Welds a pinned level into a single tile.
        ///
        /// This is what makes a hand-built level work, and it is not optional. A pinned level
        /// still leaves the generator a chain of stretches to connect, and with one forced tile
        /// each there may be no arrangement whose doors meet - Creeper Woods crashed on the
        /// loading screen every time, whichever tiles were chosen, whether or not side-paths and
        /// door matching were left alone. Welded, there is one stretch holding one tile and
        /// nothing to connect, and it loads.
        ///
        /// Lower Temple survived pinning without this only by luck: its forced tiles happened to
        /// fit together.
        /// </summary>
        public static async Task<Run> weld(string mapFolder, CancellationToken cancel = default)
        {
            var tools = folder ?? throw new InvalidOperationException(missing());
            return await start(tools, WELD, new[] { mapFolder }, cancel).ConfigureAwait(false);
        }

        /// <summary>Whether a world holds a whole level rather than one set of tiles.</summary>
        public static bool isWholeLevel(string world)
            => File.Exists(Path.Combine(world, LEVEL_MARKER));

        /// <summary>
        /// Writes a whole-level world back, each room into the object group it came from.
        ///
        /// A level draws on several groups - Lower Temple on three - so the rooms cannot all go
        /// into one file. The world carries a note saying where each belongs.
        /// </summary>
        public static async Task<Run> fromMinecraftLevel(string world,
            CancellationToken cancel = default)
        {
            var tools = folder ?? throw new InvalidOperationException(missing());
            return await start(tools, FROM_LEVEL, new[] { world }, cancel).ConfigureAwait(false);
        }

        /// <summary>What a converted world remembers about where it came from.</summary>
        public sealed class Origin
        {
            public Origin(string map, string mission, string group, string objectGroup)
            {
                Map = map;
                Mission = mission;
                Group = group;
                ObjectGroup = objectGroup;
            }

            public string Map { get; }
            public string Mission { get; }
            public string Group { get; }
            public string ObjectGroup { get; }
        }

        public static Origin? originOf(string world)
        {
            var marker = Path.Combine(world, MARKER);
            if (!File.Exists(marker)) { return null; }

            try
            {
                using var document = JsonDocument.Parse(File.ReadAllText(marker));
                var root = document.RootElement;

                string read(string name)
                    => root.TryGetProperty(name, out var value) ? value.GetString() ?? "" : "";

                var map = read("map");
                var group = read("objectgroup");

                return map.Length == 0 || group.Length == 0
                    ? null
                    : new Origin(map, read("mission"), read("group"), group);
            }
            catch (Exception)
            {
                return null;
            }
        }

        /// <summary>
        /// Writes a Minecraft world back over the object group it came from.
        ///
        /// Blocks and planes are taken from the world; doors, regions and boundaries are kept as
        /// they were. That is not caution for its own sake - it was measured. Regions travel into
        /// Minecraft as structure blocks, and a structure block needs a free air cell inside the
        /// region's footprint to sit in, which a packed dungeon often has not got. On one
        /// mission's four tiles, doors came back 5 of 5 and regions only 3 of 8.
        /// </summary>
        public static async Task<Run> fromMinecraft(string world, Origin origin,
            CancellationToken cancel = default)
        {
            var tools = folder ?? throw new InvalidOperationException(missing());
            return await start(tools, FROM, new[] { world, origin.ObjectGroup }, cancel)
                .ConfigureAwait(false);
        }

        /// <summary>Where the converter jar lives, or nothing when it was never put there.</summary>
        public static string? chunker
        {
            get
            {
                var tools = folder;
                if (tools == null) { return null; }

                var jar = Path.Combine(tools, "chunker", CHUNKER);
                return File.Exists(jar) ? jar : null;
            }
        }

        /// <summary>Whether a world can be moved between Minecraft versions at all.</summary>
        public static bool canConvert => chunker != null;

        /// <summary>
        /// Whether this level should be welded into one tile.
        ///
        /// The camp should not be. Welding it crashed the game fourteen seconds into loading -
        /// a different crash from the six that welding was introduced to fix, and reproducible.
        ///
        /// I do not know exactly which part of the camp resents it. The merged tile declares none
        /// of the teleports lobby001 declared, which is the obvious suspect, except that Creeper
        /// Woods has played tiles carrying teleports too and welds perfectly well. So the rule
        /// here is not a clever property test that would be wrong in both directions; it is the
        /// one thing that is actually known.
        ///
        /// And there is no loss in it. Welding exists to stop the generator shuffling a mission's
        /// rooms into an arrangement that was never built. The camp is not generated - it is a
        /// fixed hub of two stretches with its sub-areas listed out - so there is nothing for
        /// welding to prevent, and it was only ever being welded because everything was.
        /// </summary>
        public static bool weldable(string mapFolder, out string why)
        {
            why = string.Empty;

            var name = Path.GetFileName(mapFolder.TrimEnd(Path.DirectorySeparatorChar));
            if (!string.Equals(name, "lobby", StringComparison.OrdinalIgnoreCase)) { return true; }

            why = "the camp is a fixed hub, not a generated mission";
            return false;
        }

        /// <summary>
        /// Rewrites a world into another Minecraft version, in place of a copy.
        ///
        /// One direction, in practice: down. Minecraft upgrades a world itself on open and does it
        /// better than any converter can, so nothing here touches a world on the way out. Coming
        /// home is the direction Minecraft refuses - its upgrader has never run backwards - and
        /// without this nothing downstream could read a world a modern client had opened.
        ///
        /// The block table goes with it, so a wall of deepslate arrives as stone rather than as
        /// the hole Chunker would otherwise leave.
        /// </summary>
        public static async Task<Run> convert(string world, string format,
            CancellationToken cancel = default)
        {
            var jar = chunker;
            if (jar == null)
            {
                return new Run(false, "The world converter is not installed.", null);
            }

            var beside = Path.Combine(Path.GetDirectoryName(world)!,
                Path.GetFileName(world) + ".converting");

            if (Directory.Exists(beside)) { Directory.Delete(beside, true); }

            var arguments = new List<string> { "-jar", jar, "-i", world, "-f", format, "-o", beside };

            var mappings = Path.Combine(Path.GetDirectoryName(jar)!, MAPPINGS);
            if (File.Exists(mappings))
            {
                arguments.Add("-m");
                arguments.Add(mappings);
            }

            var run = await java(arguments.ToArray(), cancel).ConfigureAwait(false);

            if (!run.Ok || !Directory.Exists(beside))
            {
                if (Directory.Exists(beside)) { Directory.Delete(beside, true); }
                return new Run(false, run.Output, null);
            }

            //Swapped rather than written over, so a converter that dies halfway leaves the world
            //it was given rather than half of another one.
            var old = world + ".before-convert";
            if (Directory.Exists(old)) { Directory.Delete(old, true); }

            Directory.Move(world, old);
            Directory.Move(beside, world);

            carryOver(old, world);

            Directory.Delete(old, true);

            return new Run(true, run.Output, world);
        }

        /// <summary>
        /// Puts back whatever the converter did not know to keep.
        ///
        /// Chunker writes a world, not a copy of a folder: it emits level.dat, the regions and the
        /// dimensions, and everything else it was handed is gone. A Minecraft world made by these
        /// tools carries more than that - the origin marker saying which mission it came from, the
        /// object group, and the region, region-y and walkable planes, none of which Minecraft has
        /// any concept of and all of which the trip home needs.
        ///
        /// Losing them turned Bring back into "Use to_minecraft_level.py to make a world this can
        /// read", about a world these tools had made themselves an hour earlier.
        ///
        /// Only what is missing is restored. Anything the converter produced is its business and
        /// stays as it wrote it.
        /// </summary>
        private static void carryOver(string from, string to)
        {
            foreach (var file in Directory.GetFiles(from))
            {
                var landing = Path.Combine(to, Path.GetFileName(file));
                if (!File.Exists(landing)) { File.Copy(file, landing); }
            }

            foreach (var directory in Directory.GetDirectories(from))
            {
                var landing = Path.Combine(to, Path.GetFileName(directory));
                if (Directory.Exists(landing)) { continue; }
                copyTree(directory, landing);
            }
        }

        private static void copyTree(string from, string to)
        {
            Directory.CreateDirectory(to);

            foreach (var file in Directory.GetFiles(from))
            {
                File.Copy(file, Path.Combine(to, Path.GetFileName(file)), true);
            }

            foreach (var directory in Directory.GetDirectories(from))
            {
                copyTree(directory, Path.Combine(to, Path.GetFileName(directory)));
            }
        }

        private static async Task<Run> java(string[] arguments, CancellationToken cancel)
        {
            var info = new ProcessStartInfo
            {
                FileName = "java",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };

            foreach (var one in arguments) { info.ArgumentList.Add(one); }

            try
            {
                using var process = new Process { StartInfo = info };

                var said = new StringBuilder();
                process.OutputDataReceived += (_, e) => { if (e.Data != null) { said.AppendLine(e.Data); } };
                process.ErrorDataReceived += (_, e) => { if (e.Data != null) { said.AppendLine(e.Data); } };

                process.Start();
                process.BeginOutputReadLine();
                process.BeginErrorReadLine();

                await process.WaitForExitAsync(cancel).ConfigureAwait(false);

                return new Run(process.ExitCode == 0, said.ToString(), null);
            }
            catch (Exception problem)
            {
                //No Java on the machine is the common case, and it is worth saying so plainly
                //rather than as a Win32 error nobody can act on.
                return new Run(false, "Java is needed to convert worlds: " + problem.Message, null);
            }
        }

        private static async Task<Run> start(string tools, string script, string[] arguments,
            CancellationToken cancel)
        {
            var info = new ProcessStartInfo
            {
                FileName = python(tools),
                WorkingDirectory = tools,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };

            info.ArgumentList.Add(Path.Combine(tools, script));
            foreach (var one in arguments) { info.ArgumentList.Add(one); }

            using var process = new Process { StartInfo = info };

            var said = new StringBuilder();
            process.OutputDataReceived += (_, e) => { if (e.Data != null) { said.AppendLine(e.Data); } };
            process.ErrorDataReceived += (_, e) => { if (e.Data != null) { said.AppendLine(e.Data); } };

            process.Start();
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();

            //ConfigureAwait(false) throughout, so none of this needs the caller's thread to come
            //back to. Without it, anything that blocks on these tasks - a probe, a test - waits
            //for a continuation that is queued behind the very call that is waiting, and the app
            //simply stops with no error at all.
            await process.WaitForExitAsync(cancel).ConfigureAwait(false);

            return new Run(process.ExitCode == 0, said.ToString(), null);
        }

        private static string missing()
            => "The Minecraft converter is not installed. It is a folder called MCD-MapTools, "
             + $"with its own Python inside it, expected at {wanted}.";

        /// <summary>A world name Minecraft and Windows will both accept.</summary>
        private static string safe(string name)
        {
            var bad = Path.GetInvalidFileNameChars();
            var clean = new string(name.Select(c => bad.Contains(c) ? '_' : c).ToArray());
            return clean.Trim();
        }
    }
}
