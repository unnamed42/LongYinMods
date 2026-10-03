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
