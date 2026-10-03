using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace MelonMCP.Server
{
    /// <summary>
    /// Native memory access for the running game process.
    ///
    /// The game is a Windows PE (GameAssembly.dll) running under Wine/Proton, but it lives in this
    /// very process's address space, so it is directly readable.
    ///
    /// Two facts drive the design, both learned the hard way in this project:
    ///
    ///  1. The module base address changes on every launch (Proton maps it somewhere like
    ///     0x6ffff5f60000), so addresses must always be computed at runtime from the module base -
    ///     never hardcoded.
    ///
    ///  2. GameAssembly.dll only maps its header page as a named module region. The actual ~24 MB of
    ///     code sits in an ANONYMOUS r-xp region. Any scan that filters on module name will therefore
    ///     find almost nothing, and the caller must not conclude "code not present".
    /// </summary>
    public static class NativeMemory
    {
        /// <summary>Static image base of GameAssembly.dll as found on disk / in the PE headers.</summary>
        private const long StaticImageBase = 0x180000000L;

        private const string GameAssemblyName = "GameAssembly.dll";

        private static ProcessModule _module;
        private static long _moduleBase;
        private static long _moduleSize;

        /// <summary>
        /// Locates GameAssembly.dll in the current process. Cached, but re-resolved if the cached
        /// value goes stale (the process can be queried before the module shows up).
        /// </summary>
        public static bool TryResolveModule(out long baseAddress, out long size)
        {
            baseAddress = 0;
            size = 0;

            if (_moduleBase != 0)
            {
                baseAddress = _moduleBase;
                size = _moduleSize;
                return true;
            }

            try
            {
                var self = Process.GetCurrentProcess();
                foreach (ProcessModule m in self.Modules)
                {
                    if (m.ModuleName != null
                        && m.ModuleName.Equals(GameAssemblyName, StringComparison.OrdinalIgnoreCase))
                    {
                        _module = m;
                        _moduleBase = m.BaseAddress.ToInt64();
                        _moduleSize = m.ModuleMemorySize;
                        baseAddress = _moduleBase;
                        size = _moduleSize;
                        return true;
                    }
                }
            }
            catch (Exception)
            {
                // Module enumeration can fail under some Wine configurations; treat as not found.
            }

            return false;
        }

        /// <summary>
        /// Converts a static virtual address (as seen in the disassembly / Ghidra, based at
        /// 0x180000000) into an address valid in this process.
        /// </summary>
        public static long StaticVaToRuntime(long staticVa)
        {
            if (!TryResolveModule(out var baseAddress, out _)) return 0;
            return baseAddress + (staticVa - StaticImageBase);
        }

        /// <summary>
        /// Converts a runtime address back to its static VA, for cross-referencing with Ghidra.
        /// </summary>
        public static long RuntimeToStaticVa(long runtimeVa)
        {
            if (!TryResolveModule(out var baseAddress, out _)) return 0;
            return StaticImageBase + (runtimeVa - baseAddress);
        }

        public static bool TryReadBytes(long address, int length, out byte[] data)
        {
            data = null;
            if (address == 0 || length <= 0) return false;

            try
            {
                var buf = new byte[length];
                Marshal.Copy(new IntPtr(address), buf, 0, length);
                data = buf;
                return true;
            }
            catch (Exception)
            {
                return false;
            }
        }

        public static bool TryReadUInt64(long address, out ulong value)
        {
            value = 0;
            if (!TryReadBytes(address, 8, out var b)) return false;
            value = BitConverter.ToUInt64(b, 0);
            return true;
        }

        /// <summary>
        /// Makes a page range writable, runs the action, then restores the previous protection.
        /// Returns false if the protection change itself fails, so callers never write into memory
        /// they could not legitimately unlock.
        /// </summary>
        public static bool WithWritable(long address, int length, Func<bool> action, out string error)
        {
            error = null;
            if (address == 0 || length <= 0)
            {
                error = "invalid address or length";
                return false;
            }

            uint oldProtect;
            if (!VirtualProtect(new IntPtr(address), (UIntPtr)length, PageExecuteReadWrite, out oldProtect))
            {
                error = $"VirtualProtect failed (win32 error {Marshal.GetLastWin32Error()})";
                return false;
            }

            try
            {
                return action();
            }
            finally
            {
                VirtualProtect(new IntPtr(address), (UIntPtr)length, oldProtect, out _);
            }
        }

        /// <summary>
        /// Reports the executable regions in the process, which is how the anonymous code mapping
        /// behind GameAssembly.dll is discovered. Named-module lookups alone are not enough.
        /// </summary>
        public static List<MemoryRegion> GetExecutableRegions()
        {
            var regions = new List<MemoryRegion>();

            // /proc/self/maps is the portable way to see anonymous mappings under Wine/Proton, and it
            // is available here because the game process is this process.
            try
            {
                foreach (var line in System.IO.File.ReadAllLines("/proc/self/maps"))
                {
                    // Format: start-end perms offset dev inode pathname
                    var parts = line.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
                    if (parts.Length < 2) continue;

                    var perms = parts[1];
                    if (!perms.StartsWith("r-x", StringComparison.Ordinal)) continue;

                    var dash = parts[0].IndexOf('-');
                    if (dash <= 0) continue;

                    if (!long.TryParse(parts[0].Substring(0, dash),
                            System.Globalization.NumberStyles.HexNumber, null, out var start)) continue;
                    if (!long.TryParse(parts[0].Substring(dash + 1),
                            System.Globalization.NumberStyles.HexNumber, null, out var end)) continue;

                    var path = parts.Length >= 6 ? parts[5] : null;
                    regions.Add(new MemoryRegion
                    {
                        Start = start,
                        End = end,
                        Size = end - start,
                        Permissions = perms,
                        Path = path
                    });
                }
            }
            catch (Exception)
            {
                // /proc unavailable: fall back to module enumeration below.
            }

            if (regions.Count == 0)
            {
                try
                {
                    var self = Process.GetCurrentProcess();
                    foreach (ProcessModule m in self.Modules)
                    {
                        regions.Add(new MemoryRegion
                        {
                            Start = m.BaseAddress.ToInt64(),
                            End = m.BaseAddress.ToInt64() + m.ModuleMemorySize,
                            Size = m.ModuleMemorySize,
                            Permissions = "r-x (module)",
                            Path = m.FileName
                        });
                    }
                }
                catch (Exception) { }
            }

            return regions;
        }

        /// <summary>
        /// Finds the large anonymous executable region that holds the game's compiled C++ code.
        /// Returns false when nothing plausibly matches, rather than guessing.
        /// </summary>
        public static bool TryFindGameCodeRegion(out MemoryRegion region, long minimumSize = 8L * 1024 * 1024)
        {
            region = null;
            foreach (var r in GetExecutableRegions())
            {
                if (r.Path != null && r.Path.Length > 0) continue;   // named modules are not it
                if (r.Size < minimumSize) continue;
                if (region == null || r.Size > region.Size) region = r;
            }
            return region != null;
        }

        public sealed class MemoryRegion
        {
            public long Start { get; set; }
            public long End { get; set; }
            public long Size { get; set; }
            public string Permissions { get; set; }
            public string Path { get; set; }
        }

        private const uint PageExecuteReadWrite = 0x40;

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool VirtualProtect(IntPtr lpAddress, UIntPtr dwSize, uint flNewProtect, out uint lpflOldProtect);
    }
}
