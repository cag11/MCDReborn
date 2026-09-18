using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
#nullable enable

namespace MCDSaveEdit.Logic
{
    /// <summary>
    /// A folder of mods in one file, for sending somebody.
    ///
    /// Sharing mods is otherwise "find the ~mods folder, pick out the paks you mean, zip those,
    /// and tell the other person where to put them", and every step of that is somewhere to go
    /// wrong. One file out, one file in.
    ///
    /// **Zip both ways, deliberately.** Nothing can write a rar without WinRAR installed - the
    /// compression is proprietary and no library implements it - so a rar export would work on the
    /// machines that happen to have it and fail on the rest. Zip is written by the framework
    /// itself, opens in Windows Explorer with no software at all, and every archiver including
    /// WinRAR reads it. Reading is the other way round: a rar somebody was sent still opens here,
    /// because being able to *receive* one costs nothing.
    ///
    /// What an archive actually contains is the part worth being careful about. It may hold the
    /// paks loose, or a folder with them inside, or several folders, or a readme and a screenshot
    /// beside them, or a file called something.pak that is not one. So nothing is assumed: every
    /// entry at every depth is looked at, only the ones that really are paks are taken, and each
    /// is taken by its own name with the path thrown away - which is also what stops an entry
    /// called ..\\..\\something.pak writing outside the mods folder.
    /// </summary>
    public static class ModArchive
    {
        /// <summary>What can be read. Zip is the one this writes; the others are what people send.</summary>
        public const string READ_FILTER =
            "Mod archives|*.zip;*.rar|Zip archives|*.zip|All files|*.*";

        public const string WRITE_FILTER = "Zip archive|*.zip";

        /// <summary>What came of opening somebody's archive, in enough detail to say so.</summary>
        public sealed class Haul
        {
            public List<string> Installed { get; } = new List<string>();
            public List<string> Replaced { get; } = new List<string>();
            public List<string> Skipped { get; } = new List<string>();
            /// <summary>Entries that were not paks at all, which is normal and worth counting.</summary>
            public int Ignored { get; set; }
            /// <summary>Named a pak and was not one.</summary>
            public List<string> Rejected { get; } = new List<string>();
        }

        /// <summary>
        /// Installs every pak an archive holds, wherever inside it they are.
        ///
        /// <paramref name="replace"/> is asked about each name already in the mods folder, so the
        /// question is put by whoever has a window to put it in rather than here.
        /// </summary>
        public static Haul install(string archivePath, Func<string, bool> replace)
        {
            if (!File.Exists(archivePath)) { throw new FileNotFoundException("No such file.", archivePath); }

            var haul = new Haul();
            var unpacked = Path.Combine(Path.GetTempPath(), "MCDReborn_mods_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(unpacked);

            try
            {
                using (var reader = SharpCompress.Readers.ReaderFactory.OpenReader(archivePath, null))
                {
                    while (reader.MoveToNextEntry())
                    {
                        var entry = reader.Entry;
                        if (entry.IsDirectory) { continue; }

                        //Its own name and nothing else. An archive can carry a whole folder tree,
                        //and the path inside it is the sender's business rather than something to
                        //recreate - the mods folder is flat. It is also the thing that stops an
                        //entry named to climb out of that folder from doing so.
                        var name = Path.GetFileName(entry.Key?.Replace('\\', '/') ?? string.Empty);
                        if (name.Length == 0) { continue; }

                        if (!name.EndsWith(".pak", StringComparison.OrdinalIgnoreCase))
                        {
                            //A readme, a screenshot, a folder entry. Counted rather than
                            //complained about: people put those in on purpose.
                            haul.Ignored++;
                            continue;
                        }

                        var temp = Path.Combine(unpacked, name);
                        if (File.Exists(temp))
                        {
                            //Two folders inside one archive holding a pak of the same name. The
                            //first is kept, because there is no way to tell which was meant and
                            //silently overwriting one with the other is the worse guess.
                            haul.Skipped.Add(name);
                            continue;
                        }

                        using (var to = File.Create(temp))
                        {
                            reader.WriteEntryTo(to);
                        }

                        take(temp, name, replace, haul);
                    }
                }
            }
            finally
            {
                try { Directory.Delete(unpacked, recursive: true); } catch (IOException) { }
            }

            return haul;
        }

        /// <summary>One unpacked file into the mods folder, asking before it replaces anything.</summary>
        private static void take(string temp, string name, Func<string, bool> replace, Haul haul)
        {
            try
            {
                CustomSkins.installPak(temp);
                haul.Installed.Add(name);
            }
            catch (IOException)
            {
                //Already installed. Worth asking rather than either quietly replacing somebody's
                //mod or refusing to update one.
                if (!replace(name)) { haul.Skipped.Add(name); return; }

                try
                {
                    CustomSkins.installPak(temp, overwrite: true);
                    haul.Replaced.Add(name);
                }
                catch (Exception) { haul.Skipped.Add(name); }
            }
            catch (InvalidOperationException)
            {
                //Named .pak and is not one. This is the check that makes opening a stranger's
                //archive reasonable rather than hopeful.
                haul.Rejected.Add(name);
            }
        }

        /// <summary>
        /// Every installed mod written into one zip, flat.
        ///
        /// The ones this app drives from a checkbox are left out, exactly as the list leaves them
        /// out: they are managed from their own tab, and somebody unpacking this archive somewhere
        /// else would get a mod they cannot turn off from the place that turns it off.
        /// </summary>
        public static int writeAll(string targetPath)
        {
            var mods = CustomSkins.installed().Where(mod => !mod.Internal).ToList();
            if (mods.Count == 0) { throw new InvalidOperationException("There are no mods to export."); }

            //Written beside the target and moved into place, so a failure halfway through does not
            //leave a half-written archive where a whole one was.
            var working = targetPath + ".part";
            if (File.Exists(working)) { File.Delete(working); }

            try
            {
                using (var stream = File.Create(working))
                using (var zip = new ZipArchive(stream, ZipArchiveMode.Create))
                {
                    var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

                    foreach (var mod in mods)
                    {
                        var name = Path.GetFileName(mod.Path);
                        if (!used.Add(name)) { continue; }

                        zip.CreateEntryFromFile(mod.Path, name, CompressionLevel.Optimal);
                    }
                }

                if (File.Exists(targetPath)) { File.Delete(targetPath); }
                File.Move(working, targetPath);
            }
            catch (Exception)
            {
                try { if (File.Exists(working)) { File.Delete(working); } } catch (IOException) { }
                throw;
            }

            return mods.Count;
        }
    }
}
