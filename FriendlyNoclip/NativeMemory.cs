using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace Unnamed42.FriendlyNoclip;

/// <summary>
/// 原生内存访问工具：定位 <c>GameAssembly.dll</c> 模块、按签名扫描代码、
/// 读写原生内存、以及临时放开页保护执行原地插桩。
///
/// 之所以不用 <c>Il2CppInterop.Runtime.MemoryUtils</c>：它是 <c>internal</c>，
/// 反射调用不稳且额外引入对实现细节的依赖。签名扫描逻辑很短，自己实现更可控。
/// </summary>
internal static class NativeMemory
{
    /// <summary>游戏程序集在 PE 里的首选基址，用于把静态 VA 换算成 RVA。</summary>
    internal const ulong PreferredImageBase = 0x180000000UL;

    private const int PageExecuteReadWrite = 0x40;
    private const int PageExecuteRead = 0x20;

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool VirtualProtect(IntPtr lpAddress, IntPtr dwSize, int flNewProtect, out int lpflOldProtect);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr VirtualAlloc(IntPtr lpAddress, IntPtr dwSize, int flAllocationType, int flProtect);

    private const int MemCommit = 0x1000;
    private const int MemReserve = 0x2000;
    private const int PageExecuteReadWriteValue = 0x40;

    private static IntPtr _moduleBase;
    private static long _moduleSize;

    /// <summary><c>GameAssembly.dll</c> 的加载基址；未找到时为 <see cref="IntPtr.Zero"/>。</summary>
    internal static IntPtr ModuleBase => _moduleBase;

    /// <summary><c>GameAssembly.dll</c> 的映像大小。</summary>
    internal static long ModuleSize => _moduleSize;

    /// <summary>定位 <c>GameAssembly.dll</c>。成功返回 true。</summary>
    internal static bool ResolveModule()
    {
        if (_moduleBase != IntPtr.Zero)
        {
            return true;
        }

        try
        {
            Process proc = Process.GetCurrentProcess();
            foreach (ProcessModule mod in proc.Modules)
            {
                string? name = mod.ModuleName;
                if (name == null)
                {
                    continue;
                }

                if (name.Equals("GameAssembly.dll", StringComparison.OrdinalIgnoreCase))
                {
                    _moduleBase = mod.BaseAddress;
                    _moduleSize = mod.ModuleMemorySize;
                    return true;
                }
            }
        }
        catch (Exception ex)
        {
            Plugin.Log.Warning($"枚举进程模块失败：{ex.Message}");
        }

        return false;
    }

    /// <summary>把静态分析得到的 VA（如 <c>0x180a8b04f</c>）换算成运行时地址。</summary>
    internal static IntPtr StaticVaToRuntime(ulong staticVa)
    {
        if (_moduleBase == IntPtr.Zero)
        {
            return IntPtr.Zero;
        }

        ulong rva = staticVa - PreferredImageBase;
        return (IntPtr)((ulong)_moduleBase.ToInt64() + rva);
    }

    /// <summary>
    /// 把运行时地址换算回静态 VA（imagebase <c>0x180000000</c>）。
    /// **只用于日志与文档对照** —— 判定与寻址一律用运行时地址。
    /// </summary>
    internal static ulong RuntimeToStaticVa(IntPtr address)
    {
        if (_moduleBase == IntPtr.Zero)
        {
            return 0;
        }

        return (ulong)address.ToInt64() - (ulong)_moduleBase.ToInt64() + PreferredImageBase;
    }

    /// <summary>
    /// 用 PE 的 <c>.pdata</c>（x64 异常目录）查 <paramref name="inside"/> 所在函数的 <c>[start, end)</c>。
    ///
    /// <para>
    /// 【为什么需要它】要按指令形态在函数体里扫描，就必须知道**函数在哪结束** ——
    /// IL2CPP 不提供函数长度，但 x64 PE 的 <c>.pdata</c> 里每个 <c>RUNTIME_FUNCTION</c>
    /// 都带 <c>[BeginAddress, EndAddress)</c>，且规范保证按 <c>BeginAddress</c> 升序、互不重叠，
    /// 所以可以二分。没有它就只能扫固定长度 —— 那容易串进相邻函数，
    /// 把「形态必须唯一命中」这条自检弄脏。
    /// </para>
    ///
    /// <para>读的是**已加载映像**的 PE 头（不是磁盘文件），所以与运行时布局完全一致。</para>
    /// </summary>
    internal static bool TryGetFunctionBounds(IntPtr inside, out IntPtr start, out IntPtr end)
    {
        start = IntPtr.Zero;
        end = IntPtr.Zero;

        if (_moduleBase == IntPtr.Zero || inside == IntPtr.Zero)
        {
            return false;
        }

        try
        {
            long imageBase = _moduleBase.ToInt64();
            long target = inside.ToInt64();

            if (target < imageBase || target >= imageBase + _moduleSize)
            {
                return false;
            }

            if (!TryGetPdataTable(out IntPtr table, out int count))
            {
                return false;
            }

            // ---- RUNTIME_FUNCTION[]（12 字节/项）按 BeginAddress 升序 → 二分 ----
            uint key = (uint)(target - imageBase);


            int lo = 0;
            int hi = count - 1;

            while (lo <= hi)
            {
                int mid = lo + ((hi - lo) / 2);
                IntPtr entry = (IntPtr)(table.ToInt64() + (mid * 12));

                uint begin = (uint)Marshal.ReadInt32(entry);
                uint stop = (uint)Marshal.ReadInt32(entry, 4);

                if (key < begin)
                {
                    hi = mid - 1;
                }
                else if (key >= stop)
                {
                    lo = mid + 1;
                }
                else
                {
                    start = (IntPtr)(imageBase + begin);
                    end = (IntPtr)(imageBase + stop);
                    return true;
                }
            }
        }
        catch (Exception ex)
        {
            Plugin.Log.Warning($"读取 .pdata 失败：{ex.Message}");
        }

        return false;
    }

    /// <summary>
    /// 取 <paramref name="inside"/> 所在函数在 <c>.pdata</c> 里的**下一个相邻函数**起点。
    ///
    /// <para>
    /// 【为什么需要】IL2CPP 会给「声明类的静态构造还没跑」的方法生成一个 **class-init wrapper**：
    /// <c>Il2CppMethodInfo.methodPointer</c> 指向 wrapper，而 wrapper 做完类初始化后
    /// **顺序落入**真正的函数体 —— 两者是 <c>.pdata</c> 里**首尾相接的两条函数记录**。
    ///
    /// 本项目 2026-10-09 实机现场：<c>MapNavigator.Navigate</c> 的 methodPointer =
    /// <c>0x180a52b40</c>（wrapper），真身 = <c>0x180a52c2e</c>（紧邻的下一个函数）。
    /// 当时「扫描 methodPointer 指向的函数」因此一处都匹配不到。
    /// </para>
    /// </summary>
    internal static bool TryGetNextFunctionStart(IntPtr inside, out IntPtr next)
    {
        next = IntPtr.Zero;

        if (_moduleBase == IntPtr.Zero || inside == IntPtr.Zero)
        {
            return false;
        }

        try
        {
            long imageBase = _moduleBase.ToInt64();
            long target = inside.ToInt64();

            if (target < imageBase || target >= imageBase + _moduleSize)
            {
                return false;
            }

            if (!TryGetPdataTable(out IntPtr table, out int count))
            {
                return false;
            }

            uint key = (uint)(target - imageBase);
            int lo = 0;
            int hi = count - 1;

            while (lo <= hi)
            {
                int mid = lo + ((hi - lo) / 2);
                IntPtr record = (IntPtr)(table.ToInt64() + (mid * 12));

                uint begin = (uint)Marshal.ReadInt32(record);
                uint stop = (uint)Marshal.ReadInt32(record, 4);

                if (key < begin)
                {
                    hi = mid - 1;
                }
                else if (key >= stop)
                {
                    lo = mid + 1;
                }
                else
                {
                    if (mid + 1 >= count)
                    {
                        return false;
                    }

                    IntPtr following = (IntPtr)(table.ToInt64() + ((mid + 1) * 12));
                    uint nextBegin = (uint)Marshal.ReadInt32(following);

                    // 只有**首尾相接**才算「顺序落入」的延续。
                    if (nextBegin != stop)
                    {
                        return false;
                    }

                    next = (IntPtr)(imageBase + nextBegin);
                    return true;
                }
            }
        }
        catch (Exception ex)
        {
            Plugin.Log.Warning($"读取 .pdata（下一个函数）失败：{ex.Message}");
        }

        return false;
    }

    /// <summary>定位 <c>.pdata</c> 表：返回表首地址与 <c>RUNTIME_FUNCTION</c> 条数。</summary>
    private static bool TryGetPdataTable(out IntPtr table, out int count)
    {
        table = IntPtr.Zero;
        count = 0;

        if (_moduleBase == IntPtr.Zero)
        {
            return false;
        }

        long imageBase = _moduleBase.ToInt64();

        // ---- PE 头 → 节表 ----
        int peOffset = Marshal.ReadInt32(_moduleBase, 0x3C);
        IntPtr pe = (IntPtr)(imageBase + peOffset);

        if (Marshal.ReadInt32(pe) != 0x0000_4550) // "PE\0\0"
        {
            return false;
        }

        short sectionCount = Marshal.ReadInt16(pe, 6);
        short optionalHeaderSize = Marshal.ReadInt16(pe, 20);
        IntPtr section = (IntPtr)(pe.ToInt64() + 24 + optionalHeaderSize);

        int pdataRva = 0;
        int pdataSize = 0;

        for (int i = 0; i < sectionCount; i++)
        {
            IntPtr header = (IntPtr)(section.ToInt64() + (i * 40));

            if (ReadSectionName(header) == ".pdata")
            {
                pdataSize = Marshal.ReadInt32(header, 8);   // VirtualSize
                pdataRva = Marshal.ReadInt32(header, 12);   // VirtualAddress
                break;
            }
        }

        if (pdataRva == 0 || pdataSize < 12)
        {
            return false;
        }

        table = (IntPtr)(imageBase + pdataRva);
        count = pdataSize / 12;
        return true;
    }

    private static string ReadSectionName(IntPtr header)
    {
        var sb = new System.Text.StringBuilder(8);

        for (int i = 0; i < 8; i++)
        {
            byte b = Marshal.ReadByte(header, i);

            if (b == 0)
            {
                break;
            }

            sb.Append((char)b);
        }

        return sb.ToString();
    }

    /// <summary>
    /// 在模块内按字节模式扫描。返回首个匹配的运行时地址，未找到返回 <see cref="IntPtr.Zero"/>。
    /// </summary>
    /// <param name="pattern">字节模式。</param>
    /// <param name="mask">
    /// 与 <paramref name="pattern"/> 等长；<c>'x'</c> 表示必须匹配，其他（通常 <c>'?'</c>）表示通配。
    /// </param>
    internal static IntPtr FindPattern(byte[] pattern, string mask)
    {
        if (_moduleBase == IntPtr.Zero || pattern.Length == 0 || mask.Length != pattern.Length)
        {
            return IntPtr.Zero;
        }

        try
        {
            unsafe
            {
                byte* start = (byte*)_moduleBase.ToPointer();
                long limit = _moduleSize - pattern.Length;

                for (long i = 0; i < limit; i++)
                {
                    bool hit = true;

                    for (int k = 0; k < pattern.Length; k++)
                    {
                        if (mask[k] == 'x' && start[i + k] != pattern[k])
                        {
                            hit = false;
                            break;
                        }
                    }

                    if (hit)
                    {
                        return (IntPtr)(start + i);
                    }
                }
            }
        }
        catch (Exception ex)
        {
            Plugin.Log.Warning($"签名扫描异常：{ex.Message}");
        }

        return IntPtr.Zero;
    }

    /// <summary>读取原生内存中的若干字节；失败返回 false。</summary>
    internal static bool TryReadBytes(IntPtr address, int count, out byte[] buffer)
    {
        buffer = new byte[count];

        if (address == IntPtr.Zero)
        {
            return false;
        }

        try
        {
            Marshal.Copy(address, buffer, 0, count);
            return true;
        }
        catch (Exception ex)
        {
            Plugin.Log.Warning($"读取 {address} 失败：{ex.Message}");
            return false;
        }
    }

    /// <summary>读取一个 64 位指针。</summary>
    internal static IntPtr ReadPointer(IntPtr address)
    {
        return Marshal.ReadIntPtr(address);
    }

    /// <summary>读取一个字节。</summary>
    internal static byte ReadByte(IntPtr address)
    {
        return Marshal.ReadByte(address);
    }

    /// <summary>
    /// 把一段 <paramref name="patch"/> 写入目标地址，自动切换页保护。
    /// 用于原地插桩（覆盖指令 / NOP 掉跳转）。
    /// </summary>
    internal static bool WriteBytes(IntPtr address, byte[] patch)
    {
        if (address == IntPtr.Zero || patch.Length == 0)
        {
            return false;
        }

        try
        {
            if (!VirtualProtect(address, (IntPtr)patch.Length, PageExecuteReadWrite, out int oldProtect))
            {
                Plugin.Log.Warning($"VirtualProtect(RWX) 失败 @ {address}，错误码 {Marshal.GetLastWin32Error()}。");
                return false;
            }

            try
            {
                Marshal.Copy(patch, 0, address, patch.Length);
            }
            finally
            {
                // 尽力恢复原保护属性。
                VirtualProtect(address, (IntPtr)patch.Length, oldProtect, out _);
            }

            return true;
        }
        catch (Exception ex)
        {
            Plugin.Log.Warning($"写入 {address} 失败：{ex.Message}");
            return false;
        }
    }

    /// <summary>
    /// 向原生地址写一个 int32（用于改写 IL2CPP 对象的字段）。
    ///
    /// <para>
    /// 【为什么需要单独一个】<see cref="WriteBytes"/> 接受 <c>byte[]</c>，
    /// 改写单个字段时每次都要构造数组、还要自己管字节序。
    /// 字段写入一律走小端（Win64 原生），所以直接在这里封死。
    /// </para>
    /// </summary>
    internal static bool WriteInt32(IntPtr address, int value)
    {
        if (address == IntPtr.Zero)
        {
            return false;
        }

        return WriteBytes(address, BitConverter.GetBytes(value));
    }

    /// <summary>从原生地址读一个 int32。</summary>
    internal static int ReadInt32(IntPtr address)
    {
        if (address == IntPtr.Zero)
        {
            return 0;
        }

        try
        {
            return Marshal.ReadInt32(address);
        }
        catch
        {
            return 0;
        }
    }

    /// <summary>把目标地址处若干字节改成 NOP（0x90）。</summary>
    internal static bool NopOut(IntPtr address, int count)
    {
        byte[] nops = new byte[count];
        for (int i = 0; i < count; i++)
        {
            nops[i] = 0x90;
        }

        return WriteBytes(address, nops);
    }

    /// <summary>
    /// 在模块映像的反向搜索范围内分配一小块可读可写可执行的“代码洞”，
    /// 供跳板（trampoline）使用。
    ///
    /// <para>
    /// 必须尽量靠近模块：x64 的相对跳转只有 ±2GB 范围，
    /// 而模块本身可能被加载到任意高位地址。
    /// </para>
    /// </summary>
    internal static IntPtr AllocateExecutable(int size)
    {
        if (_moduleBase == IntPtr.Zero)
        {
            return IntPtr.Zero;
        }

        ulong imageBase = (ulong)_moduleBase.ToInt64();
        ulong imageEnd = imageBase + (ulong)_moduleSize;

        // 从映像末尾开始，以 64KB 为粒度向上寻找空闲区域。
        const ulong Granularity = 0x10000UL;
        const ulong MaxDistance = 0x70000000UL; // 保持在 ±2GB 之内的安全余量

        for (ulong addr = (imageEnd + Granularity - 1) & ~(Granularity - 1);
             addr < imageEnd + MaxDistance;
             addr += Granularity)
        {
            IntPtr p = VirtualAlloc((IntPtr)(long)addr, (IntPtr)size, MemCommit | MemReserve, PageExecuteReadWriteValue);

            if (p != IntPtr.Zero)
            {
                // 校验落在相对跳转可达范围内。
                long delta = (long)p.ToInt64() - (long)imageBase;

                if (delta > int.MinValue && delta < int.MaxValue)
                {
                    return p;
                }
            }
        }

        Plugin.Log.Warning("在模块 ±2GB 范围内找不到可用的代码洞。");
        return IntPtr.Zero;
    }

    /// <summary>
    /// 读取 <paramref name="address"/> 处 <paramref name="count"/> 字节并格式化成十六进制，
    /// 用于日志核对签名是否命中预期位置。
    /// </summary>
    internal static string HexDump(IntPtr address, int count)
    {
        if (!TryReadBytes(address, count, out byte[] bytes))
        {
            return "<读取失败>";
        }

        var parts = new List<string>(count);
        foreach (byte b in bytes)
        {
            parts.Add(b.ToString("x2"));
        }

        return string.Join(' ', parts);
    }
}
