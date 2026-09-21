using System;
using System.Collections.Generic;
using System.IO;
using System.Globalization;
using System.Linq;
#nullable enable

namespace MCDSaveEdit.Logic
{
    /// <summary>
    /// The map table in the Camp, and the panel it opens.
    ///
    /// A prop beside the Mystery Merchant that lists custom maps and starts one at a chosen
    /// difficulty and threat level. It is a Lobby payload like any other - a level the loader
    /// streams into the Camp - except that this one is carried inside the exe rather than cooked
    /// by whoever installs it, because nobody using this app has an Unreal editor.
    ///
    /// Which is also the reason for <see cref="SLOTS"/>. The panel's list is compiled into the
    /// widget: each row's level name is a string constant inside bytecode, sitting behind
    /// eighty-six absolute jump offsets, so editing one would mean relinking the whole graph.
    /// The names therefore never change. Slot 7 launches "mcdcustom07" for ever, and this app
    /// decides what that file IS by writing an imported map out under that name.
    ///
    /// Rather than making the menu match the maps, the maps match the menu.
    ///
    /// What it cannot do, for the record, because none of it is fixable from a pak:
    ///
    ///   * a custom map has no entry in the game's own mission enum, so every screen that asks
    ///     for a mission's display name shows the game's `&lt;Unknown&gt;` placeholder;
    ///   * it cannot appear on, or advance, the mission select map;
    ///   * rewards, map colours and the loading screen are inherited from the `randommission`
    ///     row, which is compiled into the executable.
    ///
    /// Borrowing a REAL mission's identity would fix the first two and is not worth it: the save
    /// records completion against the mission's own name, so finishing a custom map launched as
    /// Creeper Woods would mark the real Creeper Woods complete and feed the difficulty and
    /// threat unlocks. An inert `randommission` key is the better failure.
    /// </summary>
    public static class MapTable
    {
        /// <summary>How many custom maps the panel can hold. Cooked in; see the class note.</summary>
        public const int SLOTS = 100;

        /// <summary>What a slot's level file is called. Never changes.</summary>
        public static string slotName(int slot) => $"mcdcustom{slot:00}";

        /// <summary>
        /// What a slot's button is called inside the cooked panel.
        ///
        /// Named for the slot it IS rather than for where it sits in the list, so this app and
        /// the generator agree about which is which without either having to count what else is
        /// on screen.
        /// </summary>
        public static string slotButton(int slot) => $"Slot{slot:00}";

        /// <summary>The caption inside that button, which is a widget of its own.</summary>
        public static string slotCaption(int slot) => slotButton(slot) + "Text";

        /// <summary>
        /// How long a name the panel can show.
        ///
        /// The caption is rewritten without its property changing size, by taking the difference
        /// out of a key nothing reads - so there is a budget rather than a free hand. Anything
        /// longer is cut, because a name that does not fit is still better than a row that says
        /// "Custom 07".
        /// </summary>
        public const int CAPTION = 30;

        /// <summary>The panel's own package, which is the one that gets edited on the way out.</summary>
        private const string PANEL = "UMG_MCDRebornMaps";

        /// <summary>The level the loader streams in, which is the one that says WHERE.</summary>
        private const string LEVEL = "MapTable";

        /// <summary>
        /// The game blueprint the prop borrows its looks from, as the running game names its
        /// instances.
        ///
        /// Here rather than in each probe that wants it. The prop is DRAWN by spawning one of
        /// these at the actor's own transform, so anything measuring the prop - how far it floats,
        /// how tall it stands - has to find that spawned actor, and every copy of this string is
        /// a place a swap would fail silently. A probe looking for a class name nothing has
        /// reports "not in the world", which reads as the table being broken rather than as the
        /// probe being out of date.
        ///
        /// Changing the prop means changing this AND `looks` in Tools/loader/build_prop.py, and
        /// the two cannot be checked against each other from here - one is a cooked asset, the
        /// other is a generator this app never runs.
        /// </summary>
        public const string VISUAL = "BP_VillagerStatuePodium_C";

        /// <summary>Which of the loader's folders this runs in.</summary>
        public const string TRIGGER = "Lobby";

        private const string PREFIX = "Table";
        private const string FOUND_AS = "MCDReborn_Table";

        /// <summary>
        /// The three packages, at the paths the panel and the level refer to each other by.
        ///
        /// The level is what the loader loads; the actor is what the level places; the panel is
        /// what the actor opens. Installing fewer than all three gives a level that loads with
        /// everything in it failing to resolve, which reads exactly like the loader being broken.
        /// </summary>
        private static readonly (string enginePath, string file, string header)[] CARRIES =
        {
            ("/Game/MCDReborn/Lobby/MapTable", "MapTable", ".umap"),
            ("/Game/MCDReborn/Actors/BP_MCDRebornMapTable", "BP_MCDRebornMapTable", ".uasset"),
            ("/Game/MCDReborn/UI/UMG_MCDRebornMaps", "UMG_MCDRebornMaps", ".uasset"),

            //The floating name over the table, which is a widget of its own rather than part of
            //the panel - it is on screen the whole time the Camp is, and the panel only exists
            //after a click. Leaving it out of a shipped table gives a WidgetComponent pointing at
            //a class that is not there, which draws nothing and says nothing about why.
            ("/Game/MCDReborn/UI/UMG_MCDRebornSign_MapTable", "UMG_MCDRebornSign_MapTable",
                ".uasset"),
        };

        /// <summary>
        /// Where the table stands when nobody has moved it: beside the Mystery Merchant.
        ///
        /// Kept as a named constant rather than left as whatever the asset happens to be cooked
        /// with, because it is the only way back. This is a spot somebody has stood in front of
        /// and clicked - it was Blossoming Isles' before it was ours - and a position picked
        /// later by standing somewhere is a guess by comparison. Moving the table is one write;
        /// knowing what to write to undo it is this line.
        /// </summary>
        public static readonly (float x, float y, float z) HOME = (15950f, 9150f, 11800f);

        /// <summary>Where a moved table is remembered, so the next install does not undo it.</summary>
        private static string rememberedAt()
            => Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "MCDReborn", "table-where.txt");

        /// <summary>
        /// Where the table is to stand, and how far into the ground it sits there.
        ///
        /// Remembered OUTSIDE the pak on purpose. The table is rebuilt every time a slot changes
        /// - <see cref="MapSlots.sync"/> reinstalls it to reveal the new row - so a position that
        /// lived only in the installed file would survive until the next import and then quietly
        /// walk back to the Mystery Merchant.
        ///
        /// The fourth number is the one that is hard to get and easy to lose. Where a prop looks
        /// right vertically CANNOT be measured: its bounds describe a volume the mesh fits inside
        /// and say nothing about where the drawn part of it ends, so the offset is found by eye
        /// over several restarts. Keeping it means a later move is free; keeping only the
        /// absolute height means every move to ground of a different height throws it away, and
        /// the table floats again for a reason that looks like a new bug.
        /// </summary>
        public static (float x, float y, float z, float sink) where()
        {
            try
            {
                var said = File.ReadAllText(rememberedAt())
                    .Split(' ', StringSplitOptions.RemoveEmptyEntries);

                float at(int one)
                    => said.Length > one && float.TryParse(said[one], NumberStyles.Float,
                        CultureInfo.InvariantCulture, out var got) ? got : 0f;

                if (said.Length >= 3
                    && float.TryParse(said[0], NumberStyles.Float, CultureInfo.InvariantCulture,
                        out var x)
                    && float.TryParse(said[1], NumberStyles.Float, CultureInfo.InvariantCulture,
                        out var y)
                    && float.TryParse(said[2], NumberStyles.Float, CultureInfo.InvariantCulture,
                        out var z))
                {
                    //A file written before the sink existed has three numbers, and zero is the
                    //right answer for it: it was placed at the height it was placed at.
                    return (x, y, z, at(3));
                }
            }
            catch (Exception)
            {
                //A position that cannot be read is a table at home, not an error. The file is a
                //preference and the Camp gets its table either way.
            }

            return (HOME.x, HOME.y, HOME.z, 0f);
        }

        /// <summary>Whether the table is where it was cooked to stand.</summary>
        public static bool isHome
        {
            get { var (x, y, z, _) = where(); return x == HOME.x && y == HOME.y && z == HOME.z; }
        }

        /// <summary>
        /// Stands the table somewhere else, from the next install onwards.
        ///
        /// Written in INVARIANT digits. This app runs in whatever culture the machine is set to,
        /// and a position saved as "11502,00" and read back expecting a point is three numbers
        /// that all fail to parse - which is a table back at the Mystery Merchant with nothing
        /// said about why.
        /// </summary>
        /// <param name="sink">
        /// How far below the ground the origin has to go for the table to LOOK right, found by
        /// eye. Carried so that moving the table somewhere with a different floor height keeps
        /// the look instead of starting again.
        /// </param>
        public static void moveTo(float x, float y, float z, float sink = 0f)
        {
            var file = rememberedAt();
            Directory.CreateDirectory(Path.GetDirectoryName(file)!);

            File.WriteAllText(file, string.Format(CultureInfo.InvariantCulture,
                "{0} {1} {2} {3}", x, y, z, sink));
        }

        /// <summary>Puts it back beside the Mystery Merchant.</summary>
        public static void moveHome()
        {
            var file = rememberedAt();
            if (File.Exists(file)) { File.Delete(file); }
        }

        /// <summary>Whether the table is in the game's mods folder.</summary>
        public static bool isInstalled => installed() != null;

        /// <summary>The installed pak, or null.</summary>
        public static string? installed()
        {
            foreach (var folder in new[] { CustomSkins.paksFolder, CustomSkins.modsFolder })
            {
                if (folder == null || !Directory.Exists(folder)) { continue; }

                var found = Directory.EnumerateFiles(folder, FOUND_AS + "*.pak").FirstOrDefault();
                if (found != null) { return found; }
            }

            return null;
        }

        /// <summary>Takes it out again. The Camp goes back to having no table.</summary>
        public static void remove()
        {
            var found = installed();
            if (found != null) { File.Delete(found); }
        }

        /// <summary>
        /// Installs the table from the copy built into this exe.
        ///
        /// Replaces an older one rather than refusing, because the panel is the part that changes
        /// between versions and somebody updating the app should get the new one without having
        /// to know it exists.
        /// </summary>
        /// <param name="filled">
        /// Which slots have something behind them. Passed in rather than looked up, so this
        /// knows nothing about where maps come from and the two halves are not each other's
        /// prerequisite. A slot with no level file is left hidden, because the game answers a
        /// missing level by silently loading Creeper Woods - so a visible empty row is not an
        /// empty row, it is a trapdoor.
        /// </param>
        public static CustomSkins.InstalledMod installBuiltIn(ICollection<int> filled)
            => installBuiltIn(filled, new Dictionary<int, string>());

        /// <param name="names">
        /// What to call each slot on screen. A slot with no name here keeps the one it was cooked
        /// with, which says "Custom 07" - correct, and duller than it needs to be.
        /// </param>
        public static CustomSkins.InstalledMod installBuiltIn(ICollection<int> filled,
            IReadOnlyDictionary<int, string> names)
        {
            remove();

            var entries = new List<PakWriter.Entry>();

            foreach (var (enginePath, file, header) in CARRIES)
            {
                var inside = "Dungeons/Content/" + enginePath.Substring("/Game/".Length);

                var head = carried(file + header);
                var data = carried(file + ".uexp");

                if (string.Equals(file, PANEL, StringComparison.Ordinal))
                {
                    Shown = reveal(head, data, filled, names);
                }

                if (string.Equals(file, LEVEL, StringComparison.Ordinal))
                {
                    stand(head, data);
                }

                entries.Add(new PakWriter.Entry(inside + header, head));
                entries.Add(new PakWriter.Entry(inside + ".uexp", data));
            }

            return CustomSkins.writeModPak(PREFIX, entries);
        }

        /// <summary>
        /// Puts the placed actor where <see cref="where"/> says, by editing the cooked level.
        ///
        /// The actor's position is its root component's RelativeLocation - twelve bytes of tagged
        /// property - so this moves the table without the level changing size. No editor, no
        /// cook, nothing downstream to correct.
        ///
        /// One write moves THREE things, which is worth knowing before anybody goes looking for
        /// the other two: the mesh is spawned at the actor's own transform on BeginPlay, and the
        /// click is accepted by measuring from the actor's own location on Tick. There is no
        /// second coordinate anywhere that could disagree with this one.
        ///
        /// What does not move is how close a click has to land - that is a float literal in
        /// compiled bytecode. It only matters if the table is parked within a few metres of
        /// something else clickable, and then it matters a great deal.
        /// </summary>
        private static void stand(byte[] header, byte[] data)
        {
            var (x, y, z, _) = where();

            try
            {
                var package = CookedEdit.read(header, data);
                var moved = CookedEdit.setVector(package, "RelativeLocation", x, y, z);

                //Exactly one actor is placed in this level, so anything but one means the level
                //is not the shape this was written against and the table may be half-moved.
                if (moved != 1)
                {
                    Console.WriteLine($"[table] expected one placed actor, found {moved} - "
                        + "the table's position may not be what was asked for");
                }
            }
            catch (Exception problem)
            {
                //A table beside the Mystery Merchant beats no table at all.
                Console.WriteLine($"[table] could not move the table: {problem.Message}");
            }
        }

        /// <summary>How many slots the last install put on screen.</summary>
        public static int Shown { get; private set; }

        /// <summary>
        /// Shows the slots that have maps in them, by editing the cooked panel in place.
        ///
        /// Every slot is cooked hidden, and this is the only thing that ever reveals one. The
        /// edit swaps one name index for another - eight bytes for eight - so the package does
        /// not move a byte and no offset recorded anywhere in it stops being true.
        ///
        /// A slot that cannot be changed is left hidden rather than treated as a failure. The
        /// panel still works, that map simply is not listed, and an installer that refused to
        /// write the table at all because of one row would be worse.
        /// </summary>
        private static int reveal(byte[] header, byte[] data, ICollection<int> slots,
            IReadOnlyDictionary<int, string> names)
        {
            if (slots.Count == 0) { return 0; }

            var package = CookedEdit.read(header, data);
            var shown = 0;

            foreach (var slot in slots)
            {
                if (CookedEdit.setEnum(package, slotButton(slot), "Visibility",
                    "ESlateVisibility::Visible"))
                {
                    shown++;
                }
                else
                {
                    Console.WriteLine($"[table] slot {slot} could not be shown, leaving it hidden");
                    continue;
                }

                if (!names.TryGetValue(slot, out var said) || said.Length == 0) { continue; }

                if (said.Length > CAPTION) { said = said.Substring(0, CAPTION); }

                //A row that is listed under the wrong name would be worse than one listed under a
                //dull one, so a caption that will not go in is left alone and said out loud.
                if (!CookedEdit.setText(package, slotCaption(slot), said))
                {
                    Console.WriteLine($"[table] slot {slot} kept its cooked name; "
                        + $"\"{said}\" would not fit");
                }
            }

            return shown;
        }

        /// <summary>One of the files built into this exe.</summary>
        private static byte[] carried(string name)
        {
            var assembly = typeof(MapTable).Assembly;

            //By its ending rather than its full resource name: the prefix depends on the assembly
            //and the folder, so renaming either would turn this into a file-not-found at the one
            //moment somebody is trying to install.
            var resource = assembly.GetManifestResourceNames()
                .FirstOrDefault(one => one.EndsWith("." + name, StringComparison.OrdinalIgnoreCase))
                ?? throw new InvalidOperationException(
                    $"This build does not carry {name}, so the map table cannot be installed "
                    + "from it.");

            using var stream = assembly.GetManifestResourceStream(resource)
                ?? throw new InvalidOperationException($"{name} could not be read out of this build.");

            using var memory = new MemoryStream();
            stream.CopyTo(memory);
            return memory.ToArray();
        }
    }
}
