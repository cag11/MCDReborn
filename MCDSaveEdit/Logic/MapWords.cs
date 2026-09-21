using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

#nullable enable

namespace MCDSaveEdit.Logic
{
    /// <summary>
    /// The wording a mission's objectives are allowed to use, and how to add to it.
    ///
    /// An objective's "description" is not text, it is a KEY. For a long time that looked like a
    /// hard limit: the key is looked up in one of the game's per-mission string tables, those
    /// tables are compiled into Game.locres, and a key the table has not got draws as
    /// &lt;MISSING STRING TABLE ENTRY&gt; across the mission banner with nothing said anywhere.
    ///
    /// It is not a hard limit. Beside the compiled table the game also ships a plain CSV per
    /// mission - Dungeons/Content/Decor/Text/&lt;name&gt;Labels - with two columns, Key and
    /// SourceString. A key present in the CSV but absent from the compiled table falls back to
    /// the CSV's own text. That is how Blossoming Isles shows 23 objective names it invented
    /// while declaring the stock loctable "CactiCanyon": it ships an extended CactiCanyonLabels
    /// and never touches the locres at all.
    ///
    /// So wording can be anything, as long as the row travels with the map. What a map keeps is
    /// only its OWN rows; the mission's existing ones are fetched from the game at install time,
    /// because which mission it is installed over - and therefore which table it reads - is not
    /// known until then.
    /// </summary>
    public static class MapWords
    {
        /// <summary>Where a map keeps the wording it invented.</summary>
        public const string FOLDER = "text";
        public const string FILE = "custom-labels.csv";

        /// <summary>What the game calls its label tables, minus the mission's own name.</summary>
        private const string SUFFIX = "Labels";

        /// <summary>
        /// The path the game keeps one mission's label CSV at.
        ///
        /// Searched rather than spelled, because the paks are not consistent about case -
        /// "creeperwoodsLabels" is lower where "CactiCanyonLabels" is not - and the reader is
        /// literal about it.
        /// </summary>
        public static string? pathFor(string loctable)
        {
            if (loctable.Length == 0) { return null; }

            //Remembered, because finding it means walking every entry in every pak and the
            //objective list asks for it on each redraw.
            lock (_found)
            {
                if (_found.TryGetValue(loctable, out var already)) { return already; }
            }

            var index = CustomSkins.index;
            if (index == null) { return null; }

            var wanted = loctable + SUFFIX;

            foreach (var item in index)
            {
                if (item == null) { continue; }
                if (item.IndexOf("/Text/", StringComparison.OrdinalIgnoreCase) < 0) { continue; }

                var leaf = item.Substring(item.LastIndexOf('/') + 1);
                if (!string.Equals(leaf, wanted, StringComparison.OrdinalIgnoreCase)) { continue; }

                //Past the mount point, which is what the reader wants.
                var at = item.IndexOf("//", StringComparison.Ordinal);
                var said = at < 0 ? item : item.Substring(at + 1);

                lock (_found) { _found[loctable] = said; }
                return said;
            }

            lock (_found) { _found[loctable] = null; }
            return null;
        }

        private static readonly Dictionary<string, string?> _found
            = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);



        /// <summary>One row of a label table.</summary>
        public sealed class Word
        {
            public Word(string key, string said)
            {
                Key = key;
                Said = said;
            }

            public string Key { get; }
            public string Said { get; }

            public override string ToString() => Said;
        }

        /// <summary>
        /// Splits one CSV line into its fields.
        ///
        /// Written out rather than taken from a library because these files are inconsistent:
        /// some quote every field, some quote none, and some quote only the ones that need it.
        /// Only the first two columns matter - several tables carry a comment and a length hint
        /// after them, and those are none of this editor's business.
        /// </summary>
        private static List<string> fields(string line)
        {
            var made = new List<string>();
            var built = new StringBuilder();
            var quoted = false;

            for (var at = 0; at < line.Length; at++)
            {
                var c = line[at];

                if (quoted)
                {
                    if (c != '"') { built.Append(c); continue; }

                    //A doubled quote inside a quoted field is one quote.
                    if (at + 1 < line.Length && line[at + 1] == '"') { built.Append('"'); at++; }
                    else { quoted = false; }

                    continue;
                }

                if (c == '"') { quoted = true; continue; }
                if (c == ',') { made.Add(built.ToString()); built.Clear(); continue; }

                built.Append(c);
            }

            made.Add(built.ToString());
            return made;
        }

        private static string quote(string said)
            => said.IndexOfAny(new[] { ',', '"', '\n', '\r' }) < 0
                ? said
                : "\"" + said.Replace("\"", "\"\"") + "\"";

        /// <summary>Reads a two-column table out of CSV text.</summary>
        public static List<Word> parse(string text)
        {
            var made = new List<Word>();

            foreach (var line in text.Replace("\r\n", "\n").Split('\n'))
            {
                if (line.Trim().Length == 0) { continue; }

                var parts = fields(line);
                if (parts.Count < 2) { continue; }

                var key = parts[0].Trim();
                if (key.Length == 0) { continue; }

                //The header, which every one of these carries.
                if (string.Equals(key, "Key", StringComparison.Ordinal)) { continue; }

                made.Add(new Word(key, parts[1].Trim()));
            }

            return made;
        }

        /// <summary>The wording the game already has for one mission.</summary>
        public static List<Word> fromGame(string loctable)
        {
            //Remembered. The objective list asks for this on every redraw, and answering it
            //means walking every entry of every pak to find the file.
            lock (_theirs)
            {
                if (_theirs.TryGetValue(loctable, out var already)) { return already; }
            }

            var made = readGame(loctable);
            lock (_theirs) { _theirs[loctable] = made; }
            return made;
        }

        private static readonly Dictionary<string, List<Word>> _theirs
            = new Dictionary<string, List<Word>>(StringComparer.OrdinalIgnoreCase);

        /// <summary>The game's own file, exactly as it ships it.</summary>
        public static byte[]? readRaw(string loctable)
        {
            var path = pathFor(loctable);
            return path == null ? null : GameMaps.read(path);
        }

        private static List<Word> readGame(string loctable)
        {
            var raw = readRaw(loctable);
            if (raw == null) { return new List<Word>(); }

            try
            {
                return parse(new UTF8Encoding(false).GetString(raw).TrimStart('﻿'));
            }
            catch
            {
                return new List<Word>();
            }
        }

        private static string fileIn(string folder)
            => Path.Combine(folder, FOLDER, FILE);

        /// <summary>The wording this map invented for itself.</summary>
        public static List<Word> fromFolder(string folder)
        {
            lock (_mine)
            {
                if (_mine.TryGetValue(folder, out var already)) { return already; }
            }

            var made = readFolder(folder);
            lock (_mine) { _mine[folder] = made; }
            return made;
        }

        private static readonly Dictionary<string, List<Word>> _mine
            = new Dictionary<string, List<Word>>(StringComparer.OrdinalIgnoreCase);

        /// <summary>Drops what was remembered about one folder, after writing to it.</summary>
        public static void forget(string folder)
        {
            lock (_mine) { _mine.Remove(folder); }
        }

        private static List<Word> readFolder(string folder)
        {
            var path = fileIn(folder);
            if (!File.Exists(path)) { return new List<Word>(); }

            try
            {
                return parse(File.ReadAllText(path, new UTF8Encoding(false)));
            }
            catch
            {
                return new List<Word>();
            }
        }

        /// <summary>
        /// Everything a map can say, its own rows last so they win a tie.
        /// </summary>
        public static List<Word> all(string folder, string loctable)
        {
            var made = new List<Word>(fromGame(loctable));
            var mine = fromFolder(folder);

            foreach (var word in mine)
            {
                var at = made.FindIndex(one =>
                    string.Equals(one.Key, word.Key, StringComparison.Ordinal));

                if (at >= 0) { made[at] = word; } else { made.Add(word); }
            }

            return made;
        }

        /// <summary>
        /// A key for some wording nobody has a key for.
        ///
        /// Made out of the words themselves so a person reading the level file can tell what it
        /// says without going and looking, which is the one thing the game's own keys get right.
        /// </summary>
        public static string mint(string said, IEnumerable<Word> taken, string stem = "description_")
        {
            var slug = new StringBuilder();

            foreach (var c in said.ToLowerInvariant())
            {
                if (char.IsLetterOrDigit(c)) { slug.Append(c); }
                else if (slug.Length > 0 && slug[slug.Length - 1] != '_') { slug.Append('_'); }
            }

            //Minted with the prefix it will actually carry. Minting a description key and
            //renaming it afterwards is how "name_ring_the_bell_2" happened: the rename moved it
            //out from under the very clash the number was added to avoid.
            var whole = stem + slug.ToString().Trim('_');
            if (whole.Length > 60) { whole = whole.Substring(0, 60).TrimEnd('_'); }
            if (whole == stem) { whole = stem + "step"; }

            var used = new HashSet<string>(taken.Select(one => one.Key), StringComparer.Ordinal);
            if (!used.Contains(whole)) { return whole; }

            for (var n = 2; ; n++)
            {
                var tried = whole + "_" + n.ToString(System.Globalization.CultureInfo.InvariantCulture);
                if (!used.Contains(tried)) { return tried; }
            }
        }

        /// <summary>
        /// Writes one piece of wording into the map's own table.
        ///
        /// Only the map's own rows are kept. The mission's existing ones are fetched from the
        /// game when it is installed, because which mission it is installed over - and so which
        /// table it extends - is not decided until that moment.
        /// </summary>
        public static void remember(string folder, string key, string said)
        {
            forget(folder);

            var mine = fromFolder(folder);

            var at = mine.FindIndex(one => string.Equals(one.Key, key, StringComparison.Ordinal));
            if (at >= 0) { mine[at] = new Word(key, said); } else { mine.Add(new Word(key, said)); }

            var path = fileIn(folder);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);

            var text = new StringBuilder();
            text.Append("Key,SourceString\r\n");

            foreach (var word in mine)
            {
                text.Append(quote(word.Key)).Append(',').Append(quote(word.Said)).Append("\r\n");
            }

            File.WriteAllText(path, text.ToString(), new UTF8Encoding(false));
        }

        /// <summary>
        /// The table to ship with a map, as the game's own rows plus the map's.
        ///
        /// Built at install rather than kept on disk, so a folder installed over a different
        /// mission extends THAT mission's table rather than carrying a stale copy of another
        /// one. Blossoming Isles gets this wrong in the other direction - it ships a whole
        /// stale Game.locres to rename five strings, and quietly reverts a few hundred others.
        /// </summary>
        public static byte[]? tableFor(string folder, string loctable)
        {
            var mine = fromFolder(folder);
            if (mine.Count == 0) { return null; }

            var theirs = fromGame(loctable);

            //The game's own file is APPENDED TO, never re-emitted. Reading its rows and writing
            //them back out looks harmless and is not: six of Creeper Woods' twenty-six carry a
            //trailing comma - an empty third column - which a two-column round trip silently
            //drops, and the file that comes out is no longer the file the game shipped.
            //
            //That file is loaded WITH THE MISSION, not at startup - loctable-id is a per-level
            //field - so a table this editor got slightly wrong takes the mission down on entry
            //and leaves the rest of the game working. Which is exactly how it presented.
            //
            //It is also what the one mod known to do this successfully does: Blossoming Isles'
            //CactiCanyonLabels is the game's ten rows verbatim, then its own twenty-three.
            var head = readRaw(loctable);

            //Nothing at all rather than a stub. If the game's table cannot be found - a mission
            //spelled differently, a reader that changed - then writing a file with only this
            //map's rows in it does not ADD wording, it REPLACES the mission's with two lines,
            //and every objective the mission already had goes blank.
            if (head == null) { return null; }

            var text = new StringBuilder();
            text.Append(new UTF8Encoding(false).GetString(head).TrimStart('﻿'));

            //Its own line ending, so a row is not appended onto the end of the last one.
            if (text.Length > 0 && text[text.Length - 1] != '\n') { text.Append("\r\n"); }

            var known = new HashSet<string>(theirs.Select(one => one.Key), StringComparer.Ordinal);
            var added = 0;

            foreach (var word in mine)
            {
                //A key the mission already has is one the compiled table answers first, so a
                //second row for it would change nothing and is a row that could disagree.
                if (!known.Add(word.Key)) { continue; }

                text.Append(quote(word.Key)).Append(',').Append(quote(word.Said)).Append("\r\n");
                added++;
            }

            if (added == 0) { return null; }

            return new UTF8Encoding(false).GetBytes(text.ToString());
        }

        /// <summary>Where the table goes inside a pak, for one mission.</summary>
        public static string pakPathFor(string loctable)
            => "Dungeons/Content/Decor/Text/" + loctable + SUFFIX + ".csv";
    }
}
