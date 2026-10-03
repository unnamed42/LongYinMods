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

            // Validate the range against the process memory map BEFORE touching it.
            //
            // This is not belt-and-braces: a Marshal.Copy against an unmapped address does NOT raise a
            // catchable managed exception on .NET 6 - it faults the process. The try/catch below would
            // never run. A bad address therefore used to kill the game outright, which is exactly how
            // a mis-normalised static VA took the process down.
            if (!IsRangeMapped(address, length)) return false;

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

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern UIntPtr VirtualQuery(IntPtr lpAddress, out MEMORY_BASIC_INFORMATION lpBuffer, UIntPtr dwLength);

        /// <summary>
        /// Windows MEMORY_BASIC_INFORMATION for x64. Field order matters and is taken from the SDK; do
        /// not reshuffle it.
        /// </summary>
        [StructLayout(LayoutKind.Sequential)]
        private struct MEMORY_BASIC_INFORMATION
        {
            public IntPtr BaseAddress;
            public IntPtr AllocationBase;
            public uint AllocationProtect;
            public uint __alignment1;
            public IntPtr RegionSize;
            public uint State;
            public uint Protect;
            public uint Type;
            public uint __alignment2;
        }

        private const uint MEM_COMMIT = 0x1000;
        private const uint PAGE_NOACCESS = 0x01;
        private const uint PAGE_GUARD = 0x100;

        /// <summary>
        /// True when [address, address+length) is committed and readable in the WINDOWS address space.
        ///
        /// Why VirtualQuery and not /proc/self/maps: under Proton the game runs as a Windows
        /// process inside the Wine host. /proc/self/maps describes the LINUX host layout (entries
        /// starting 0x55.. / 0x7f..), while GameAssembly.dll lives at Windows addresses such as
        /// 0x6ffff... that simply do not appear there. An earlier version of this check parsed
        /// /proc/self/maps and consequently rejected every legitimate game address, making the whole
        /// native tool set unusable.
        ///
        /// Marshal.Copy faults the process on an unmapped address instead of raising a catchable
        /// exception, which is why a pre-check is needed at all - VirtualQuery is the Windows-side
        /// call that answers the same question in the correct address space.
        /// </summary>
        public static bool IsRangeMapped(long address, int length)
        {
            if (address <= 0 || length <= 0) return false;

            long cursor = address;
            long end = address + length;

            // Walk the region chain; a single query covers one contiguous region, so a range that
            // straddles a boundary needs more than one step. Bounded to avoid looping forever on a
            // pathological map.
            for (int guard = 0; guard < 64 && cursor < end; guard++)
            {
                UIntPtr got;
                MEMORY_BASIC_INFORMATION mbi;
                try
                {
                    got = VirtualQuery(new IntPtr(cursor), out mbi, (UIntPtr)Marshal.SizeOf<MEMORY_BASIC_INFORMATION>());
                }
                catch (Exception)
                {
                    // VirtualQuery unavailable (non-Windows host): we cannot prove the range is bad,
                    // so allow the read and let the caller's own error handling deal with it.
                    return true;
                }

                if (got == UIntPtr.Zero) return false;

                bool readable = mbi.State == MEM_COMMIT
                             && (mbi.Protect & PAGE_NOACCESS) == 0
                             && (mbi.Protect & PAGE_GUARD) == 0;

                if (!readable) return false;

                long regionEnd = mbi.BaseAddress.ToInt64() + mbi.RegionSize.ToInt64();
                if (regionEnd <= cursor) return false;   // no forward progress

                cursor = regionEnd;
            }

            return cursor >= end;
        }
    }
}
