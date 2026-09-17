using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;

namespace LiveEdit
{
    /// <summary>
    /// Loading a library into the running game, after it has finished starting up.
    ///
    /// The obvious way to get code into an Unreal game is a proxy DLL - name a file after a system
    /// library the game loads, put it beside the executable, and the Windows loader brings it in
    /// for free. That was tried here and killed the game instantly with an access violation,
    /// before a single line of log was written. On a Microsoft Store build that is what it looks
    /// like when the loader brings in a file the runtime did not expect, while the GDK runtime is
    /// still setting itself up.
    ///
    /// Injecting after the main menu avoids all of that: the game is up, the runtime has done its
    /// checks, and a library arriving now is just a library. The Universal Unreal Engine Unlocker
    /// does exactly this to this game and works, which is the evidence that the game does not
    /// refuse injected code in general - only the proxy.
    ///
    /// Written here rather than reaching for one of the usual injectors, because the machinery is
    /// the same handful of calls this project already makes to read the game's memory, and a tool
    /// somebody has to download separately is a tool the editor cannot offer on its own.
    /// </summary>
    public static class DllInjector
    {
        private const int PROCESS_CREATE_THREAD = 0x0002;
        private const int PROCESS_QUERY_INFORMATION = 0x0400;
        private const int PROCESS_VM_OPERATION = 0x0008;
        private const int PROCESS_VM_WRITE = 0x0020;
        private const int PROCESS_VM_READ = 0x0010;

        private const uint MEM_COMMIT = 0x1000;
        private const uint MEM_RESERVE = 0x2000;
        private const uint MEM_RELEASE = 0x8000;
        private const uint PAGE_READWRITE = 0x04;

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern IntPtr OpenProcess(int access, bool inherit, int processId);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool CloseHandle(IntPtr handle);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern IntPtr VirtualAllocEx(IntPtr process, IntPtr address, IntPtr size,
            uint allocationType, uint protect);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool VirtualFreeEx(IntPtr process, IntPtr address, IntPtr size, uint freeType);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool WriteProcessMemory(IntPtr process, IntPtr address, byte[] buffer,
            IntPtr size, out IntPtr written);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern IntPtr CreateRemoteThread(IntPtr process, IntPtr attributes, IntPtr stackSize,
            IntPtr start, IntPtr parameter, uint flags, out uint threadId);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern uint WaitForSingleObject(IntPtr handle, uint milliseconds);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool GetExitCodeThread(IntPtr thread, out uint exitCode);

        [DllImport("kernel32.dll", CharSet = CharSet.Ansi, SetLastError = true)]
        private static extern IntPtr GetModuleHandle(string name);

        [DllImport("kernel32.dll", CharSet = CharSet.Ansi, SetLastError = true)]
        private static extern IntPtr GetProcAddress(IntPtr module, string name);

        /// <summary>
        /// Puts a library into the running game, and says what happened.
        ///
        /// The library has to be readable by the game, which on a packaged build is not the same
        /// as being readable by the person who owns it - a file everyone can read is still refused
        /// unless the app packages themselves are granted it. That is checked first, because the
        /// failure otherwise is a load that silently returns nothing.
        /// </summary>
        public static bool inject(string dllPath, out string problem)
        {
            problem = "";

            if (!File.Exists(dllPath))
            {
                problem = $"No such file: {dllPath}";
                return false;
            }
            dllPath = Path.GetFullPath(dllPath);

            var found = Array.Empty<Process>();
            foreach (var name in GameProcess.PROCESS_NAMES)
            {
                found = Process.GetProcessesByName(name);
                if (found.Length > 0) { break; }
            }
            if (found.Length == 0)
            {
                problem = "Minecraft Dungeons is not running. Start it and reach the main menu first.";
                return false;
            }

            Process? game = null;
            foreach (var candidate in found)
            {
                if (game == null || candidate.WorkingSet64 > game.WorkingSet64) { game = candidate; }
            }
            if (game == null) { problem = "Minecraft Dungeons is not running."; return false; }

            var access = PROCESS_CREATE_THREAD | PROCESS_QUERY_INFORMATION
                | PROCESS_VM_OPERATION | PROCESS_VM_WRITE | PROCESS_VM_READ;

            var process = OpenProcess(access, false, game.Id);
            if (process == IntPtr.Zero)
            {
                problem = $"Could not open the game for injection (Windows error {Marshal.GetLastWin32Error()}).";
                return false;
            }

            var remote = IntPtr.Zero;
            try
            {
                //LoadLibraryW lives at the same address in every process on a given boot, because
                //kernel32 is mapped at one place system-wide. So the address here is the address
                //there, and no lookup inside the game is needed.
                var kernel32 = GetModuleHandle("kernel32.dll");
                var loadLibrary = GetProcAddress(kernel32, "LoadLibraryW");
                if (loadLibrary == IntPtr.Zero)
                {
                    problem = "Could not find LoadLibraryW, which should not be possible.";
                    return false;
                }

                var pathBytes = System.Text.Encoding.Unicode.GetBytes(dllPath + "\0");
                remote = VirtualAllocEx(process, IntPtr.Zero, new IntPtr(pathBytes.Length),
                    MEM_COMMIT | MEM_RESERVE, PAGE_READWRITE);
                if (remote == IntPtr.Zero)
                {
                    problem = $"Could not reserve memory in the game (Windows error {Marshal.GetLastWin32Error()}).";
                    return false;
                }

                if (!WriteProcessMemory(process, remote, pathBytes, new IntPtr(pathBytes.Length), out _))
                {
                    problem = $"Could not write the library's path into the game (Windows error {Marshal.GetLastWin32Error()}).";
                    return false;
                }

                var thread = CreateRemoteThread(process, IntPtr.Zero, IntPtr.Zero, loadLibrary, remote, 0, out _);
                if (thread == IntPtr.Zero)
                {
                    problem = $"The game refused a new thread (Windows error {Marshal.GetLastWin32Error()}).";
                    return false;
                }

                //Generous, because a scripting system scans the engine as it initialises and that
                //is not quick.
                WaitForSingleObject(thread, 30000);
                GetExitCodeThread(thread, out var result);
                CloseHandle(thread);

                if (result == 0)
                {
                    problem = "The game loaded nothing. Usually that means the library is not readable by\n" +
                        "  the app itself - a packaged game needs ALL APPLICATION PACKAGES granted on the file.";
                    return false;
                }

                //Truncated to 32 bits by the thread exit code, so this is a sign of life rather
                //than a usable handle. Which is all that is wanted: it loaded.
                problem = $"Loaded. The game returned a module handle ending {result:X}.";
                return true;
            }
            finally
            {
                if (remote != IntPtr.Zero) { VirtualFreeEx(process, remote, IntPtr.Zero, MEM_RELEASE); }
                CloseHandle(process);
                game.Dispose();
            }
        }
    }
}
