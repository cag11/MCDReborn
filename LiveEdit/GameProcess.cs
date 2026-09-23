using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace LiveEdit
{
    /// <summary>
    /// The running game, read and written from outside it.
    ///
    /// Everything this project does to a live game is done through this. Note what it is *not*: no
    /// DLL is loaded into the game, no code of ours runs inside it, and nothing is hooked. That
    /// matters more here than it usually would, because this build of the game is a packaged app
    /// in an AppContainer - getting a library loaded into one means administrator rights and
    /// permission work on the file, while opening it for reading and writing is an ordinary thing
    /// a program may ask to do. People already attach to this exact Game Pass build with
    /// off-the-shelf tools, so the request is one Windows is willing to grant.
    ///
    /// The cost of staying outside is that changes have to be made repeatedly rather than once -
    /// there is no hook to sit inside, so a value the game recomputes has to be written again each
    /// time. For a camera angle at sixty times a second that is affordable. For something the game
    /// rewrites in a tight loop it would not be.
    /// </summary>
    public sealed class GameProcess : IDisposable
    {
        /// <summary>
        /// What the game calls itself, which depends on where it was bought.
        ///
        /// The Store build ships as `Dungeons.exe` with a small launcher of the same name beside
        /// it; the Steam build uses Unreal's usual naming and is `Dungeons-Win64-Shipping.exe`.
        /// Both are looked for, because a tool that silently fails to find the game is worse than
        /// one that says which it was looking for.
        /// </summary>
        public static readonly string[] PROCESS_NAMES = { "Dungeons-Win64-Shipping", "Dungeons" };

        public const string PROCESS_NAME = "Dungeons-Win64-Shipping";

        private const int PROCESS_VM_READ = 0x0010;
        private const int PROCESS_VM_WRITE = 0x0020;
        private const int PROCESS_VM_OPERATION = 0x0008;
        private const int PROCESS_QUERY_INFORMATION = 0x0400;

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern IntPtr OpenProcess(int access, bool inherit, int processId);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool CloseHandle(IntPtr handle);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool ReadProcessMemory(IntPtr process, IntPtr address,
            byte[] buffer, int size, out IntPtr read);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool WriteProcessMemory(IntPtr process, IntPtr address,
            byte[] buffer, int size, out IntPtr written);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern int VirtualQueryEx(IntPtr process, IntPtr address,
            out MemoryRegion region, int length);

        [StructLayout(LayoutKind.Sequential)]
        public struct MemoryRegion
        {
            public IntPtr BaseAddress;
            public IntPtr AllocationBase;
            public uint AllocationProtect;
            public IntPtr RegionSize;
            public uint State;
            public uint Protect;
            public uint Type;
        }

        private const uint MEM_COMMIT = 0x1000;
        private const uint PAGE_GUARD = 0x100;
        private const uint PAGE_NOACCESS = 0x01;

        //The protections a page must have for the things being looked for to live in it. Game
        //state is read and written by the game, so anything read-only is not it.
        private const uint PAGE_READWRITE = 0x04;
        private const uint PAGE_EXECUTE_READWRITE = 0x40;
        private const uint PAGE_WRITECOPY = 0x08;
        private const uint PAGE_EXECUTE_WRITECOPY = 0x80;

        private readonly IntPtr _handle;

        private GameProcess(Process process, IntPtr handle)
        {
            Process = process;
            Id = process.Id;
            _handle = handle;
        }

        public Process Process { get; }

        /// <summary>
        /// Which process this is, taken while it is certainly alive.
        ///
        /// Read once here rather than asked for later, for the same reason the module is: a
        /// process that has gone answers questions about itself by throwing.
        /// </summary>
        public int Id { get; }
        public bool IsRunning => !Process.HasExited;

        /// <summary>
        /// True when this is not the game itself and the game has since turned up.
        ///
        /// On Steam, Dungeons.exe is a launcher stub that starts the real game -
        /// Dungeons-Win64-Shipping - about a second later and then sits there for as long as the
        /// game runs. "Dungeons" is in PROCESS_NAMES because some installs call the game itself
        /// that, so a link that polls in the gap between the two finds the stub, attaches, and
        /// has no reason ever to look again: the stub never exits. Everything after that reads
        /// the launcher's memory, finds no character, and every slider writes to nothing.
        ///
        /// Only asked of a process that is not already the shipping one, so the cost - a
        /// process lookup per poll - is paid only in the case that needs it.
        /// </summary>
        public bool Superseded
        {
            get
            {
                try
                {
                    if (string.Equals(Process.ProcessName, PROCESS_NAME, StringComparison.OrdinalIgnoreCase))
                    {
                        return false;
                    }

                    return Process.GetProcessesByName(PROCESS_NAME).Length > 0;
                }
                catch (Exception)
                {
                    return false;
                }
            }
        }

        private IntPtr _image;
        private int _imageSize;

        /// <summary>
        /// Where the game's own module sits and how big it is, or zero once the game has gone.
        ///
        /// Asking Windows for a process's main module is not a question that stays answerable.
        /// When the game closes it throws rather than returning null - "No process is associated
        /// with this object" - and it is asked from the loops that run hundreds of times a second,
        /// so the moment somebody quits the game while the camera is on, a loop dies of it. That
        /// was the crash with nothing on screen to explain it: the application closing while
        /// somebody was, as far as they could tell, only playing with the sliders.
        ///
        /// Asked once and remembered. A module does not move for as long as the process it belongs
        /// to is alive, so nothing is lost by caching it, and a system call per loop iteration is
        /// saved as well.
        /// </summary>
        public IntPtr image(out int size)
        {
            if (_image == IntPtr.Zero)
            {
                try
                {
                    var module = Process.MainModule;
                    _image = module?.BaseAddress ?? IntPtr.Zero;
                    _imageSize = module?.ModuleMemorySize ?? 0;
                }
                catch (Exception)
                {
                    //Gone, or going. Either way there is nothing here to read and saying so is
                    //the whole job - the caller has a message for it and the loop carries on.
                    _image = IntPtr.Zero;
                    _imageSize = 0;
                }
            }

            size = _imageSize;
            return _image;
        }

        /// <summary>
        /// Opens the running game, or says why it could not.
        ///
        /// The two failures worth telling apart are "the game is not running", which is ordinary
        /// and expected, and "Windows refused", which is the one that decides whether any of this
        /// is possible on a packaged build at all.
        /// </summary>
        public static GameProcess? open(out string problem)
        {
            problem = "";

            var found = Array.Empty<Process>();
            foreach (var name in PROCESS_NAMES)
            {
                found = Process.GetProcessesByName(name);
                if (found.Length > 0) { break; }
            }
            if (found.Length == 0)
            {
                problem = "Minecraft Dungeons is not running.";
                return null;
            }

            //The launcher shares its name with the game in some installs, so the one with a real
            //main window and the larger footprint is the one meant.
            Process? game = null;
            foreach (var candidate in found)
            {
                if (game == null || candidate.WorkingSet64 > game.WorkingSet64) { game = candidate; }
            }
            if (game == null) { problem = "Minecraft Dungeons is not running."; return null; }

            var handle = OpenProcess(
                PROCESS_VM_READ | PROCESS_VM_WRITE | PROCESS_VM_OPERATION | PROCESS_QUERY_INFORMATION,
                false, game.Id);

            if (handle == IntPtr.Zero)
            {
                var error = Marshal.GetLastWin32Error();
                problem = error == 5
                    ? "Windows refused access to the game's memory (error 5, access denied).\n" +
                      "  This build is a packaged app, so try running the editor as administrator."
                    : $"Could not open the game's memory (Windows error {error}).";
                return null;
            }

            return new GameProcess(game, handle);
        }

        public bool tryRead(IntPtr address, byte[] into, int length)
        {
            return ReadProcessMemory(_handle, address, into, length, out var read)
                && read.ToInt64() == length;
        }

        public byte[]? read(IntPtr address, int length)
        {
            var buffer = new byte[length];
            return tryRead(address, buffer, length) ? buffer : null;
        }

        public bool write(IntPtr address, byte[] bytes)
        {
            return WriteProcessMemory(_handle, address, bytes, bytes.Length, out var written)
                && written.ToInt64() == bytes.Length;
        }

        public bool writeFloat(IntPtr address, float value) => write(address, BitConverter.GetBytes(value));

        public float? readFloat(IntPtr address)
        {
            var bytes = read(address, 4);
            return bytes == null ? (float?)null : BitConverter.ToSingle(bytes, 0);
        }

        /// <summary>
        /// Every stretch of memory the game can write to.
        ///
        /// Which is where its own state lives. Skipping everything else is not an optimisation so
        /// much as the difference between a search that takes a moment and one that walks the
        /// whole address space, most of which is unmapped or belongs to the code.
        /// </summary>
        public IEnumerable<(IntPtr at, long size)> writableRegions(long largest = 256L * 1024 * 1024)
        {
            var address = IntPtr.Zero;
            var regionSize = Marshal.SizeOf<MemoryRegion>();

            while (true)
            {
                if (VirtualQueryEx(_handle, address, out var region, regionSize) == 0) { yield break; }

                var size = region.RegionSize.ToInt64();
                if (size <= 0) { yield break; }

                var usable = region.State == MEM_COMMIT
                    && (region.Protect & PAGE_GUARD) == 0
                    && (region.Protect & PAGE_NOACCESS) == 0
                    && (region.Protect == PAGE_READWRITE
                        || region.Protect == PAGE_EXECUTE_READWRITE
                        || region.Protect == PAGE_WRITECOPY
                        || region.Protect == PAGE_EXECUTE_WRITECOPY);

                //A single enormous region is almost always a reservation rather than anything
                //holding game state, and reading it would cost more than it could return.
                if (usable && size <= largest) { yield return (region.BaseAddress, size); }

                var next = region.BaseAddress.ToInt64() + size;
                if (next <= address.ToInt64()) { yield break; }
                address = new IntPtr(next);
            }
        }

        public void Dispose()
        {
            if (_handle != IntPtr.Zero) { CloseHandle(_handle); }
            Process.Dispose();
        }
    }
}
