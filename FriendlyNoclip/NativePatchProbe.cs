using System;
using System.Runtime.InteropServices;
using MelonLoader;

namespace Unnamed42.FriendlyNoclip;

/// <summary>
/// 原地等长改写：让战斗寻路只把**敌方**单位视作阻挡，友方可以穿过。
///
/// <para>
/// 改写对象是 <c>MapNavigator.Navigate</c> 里 <c>0x180a8d929</c> 处的
/// <c>jne 0x180a8da6b</c>（原版语义：邻格上只要站着**任何**存活单位，
/// 就跳过该格）。改成跳到 <c>0x180a8d93b</c> —— 即 <b>跳过判定，直接执行
/// 游戏自己原有的敌我判定</b>：
/// </para>
/// <code>
/// 0x180a8d92f  mov  eax, [rsp+0xd8]   ; selfTeamID
/// 0x180a8d936  cmp  eax, -1
/// 0x180a8d939  je   0x180a8d963       ; 调用方不关心队伍 -> 放行
/// 0x180a8d93b  mov  r8d, [rsi+0x28]   ; ★新跳转目标★
/// 0x180a8d93f  mov  r9d, eax
/// 0x180a8d942  mov  edx, [rsi+0x24]
/// 0x180a8d945  mov  rcx, [rsp+0xa8]   ; battleMap
/// 0x180a8d94d  mov  qword [rsp+0x20], 0
/// 0x180a8d956  call 0x1808c41f0       ; AroundGridHaveEnemy(row, col, selfTeamID)
/// 0x180a8d95b  test al, al
/// 0x180a8d95d  jne  0x180a8da6b       ; 邻接敌方 -> 跳过；否则继续扩展
/// </code>
///
/// <para>
/// 关键在于这个「跳过」是**条件性**的：敌我判定完全交给游戏自己那段代码
/// （<c>AroundGridHaveEnemy</c>，语义是「该格四邻有 <b>非</b> selfTeamID 的
/// 存活单位」）。所以：
/// </para>
/// <list type="bullet">
/// <item>友方挡路 → 放行，可穿过</item>
/// <item>敌方挡路 → <c>jne</c> 跳走，仍然不可通行（保住战斗对抗性）</item>
/// <item>不会把任何格子变成「可落点」—— 友方格上仍有单位，游戏自己的
/// <c>isEmpty</c> 会拦住落点，因此<b>不会重叠</b></item>
/// </list>
///
/// <para>
/// <b>为什么这个改法是安全的</b>：跳转目标落在同一函数内、同一基本块群的
/// 已知指令边界上，<b>字节数不变、不分配内存、不建跳板</b>。此前用
/// 「自建跳板 + <c>VirtualAlloc</c>」的版本一装上就在点击人物时 coredump；
/// 改成纯原地等长改写后不再崩溃。
/// </para>
/// <para>
/// 不做任何写入前的隐含假设：改写前逐字节比对预期原值，不一致就拒绝并记日志。
/// </para>
/// </summary>
internal static class NativePatchProbe
{
    /// <summary>
    /// 目标指令的静态 VA：<c>0x180a8d929</c> 处的 <c>jne 0x180a8da6b</c>。
    /// </summary>
    internal const ulong VaAliveJump = 0x180A8D929UL;

    /// <summary>
    /// 改写后的跳转目标：<c>0x180a8d93b</c>（跳过「任何存活单位即阻挡」，
    /// 直接进入游戏自己的敌我判定）。
    /// </summary>
    internal const ulong VaSkipToTeamCheck = 0x180A8D93BUL;

    private const int JumpLength = 6;

    /// <summary>改写前的原始字节（<c>jne 0x180a8da6b</c>，离线从 <c>GameAssembly.dll</c> 读得）。</summary>
    private static readonly byte[] OriginalBytes = { 0x0F, 0x85, 0x3C, 0x01, 0x00, 0x00 };

    private static bool _installed;
    private static IntPtr _site = IntPtr.Zero;

    /// <summary>是否已成功改写。</summary>
    internal static bool Installed => _installed;

    /// <summary>
    /// 执行原地等长改写。失败只记日志，绝不抛异常打断游戏启动。
    /// </summary>
    /// <returns>成功返回 <c>true</c>。</returns>
    internal static bool Install()
    {
        if (_installed)
        {
            return true;
        }

        try
        {
            if (NativeMemory.ModuleBase == IntPtr.Zero)
            {
                NativeMemory.ResolveModule();
            }

            if (NativeMemory.ModuleBase == IntPtr.Zero)
            {
                Plugin.Log.Warning("[原地改写] 找不到 GameAssembly.dll 模块基址，跳过。");
                return false;
            }

            _site = NativeMemory.StaticVaToRuntime(VaAliveJump);

            // 首先确认目标位置就是预期的那条指令。
            if (!NativeMemory.TryReadBytes(_site, JumpLength, out byte[] current))
            {
                Plugin.Log.Warning($"[原地改写] 无法读取 0x{_site.ToInt64():x} 的字节，跳过。");
                return false;
            }

            if (!BytesEqual(current, OriginalBytes))
            {
                Plugin.Log.Warning(
                    $"[原地改写] 0x{_site.ToInt64():x} 处字节不符，拒绝改写（避免破坏未知代码）。" +
                    $"实际：{Hex(current)}，预期：{Hex(OriginalBytes)}");

                return false;
            }

            // 构造改写后的字节：同样的 jne 指令，只把 rel32 换成新目标。
            // 长度不变（仍 6 字节）、不分配、不越出函数 —— 只是改跳向哪里。
            ulong newTarget = NativeMemory.StaticVaToRuntime(VaSkipToTeamCheck).ToInt64() < 0
                ? 0UL
                : (ulong)NativeMemory.StaticVaToRuntime(VaSkipToTeamCheck).ToInt64();

            if (newTarget == 0UL)
            {
                Plugin.Log.Warning("[原地改写] 无法换算新的跳转目标地址，跳过。");
                return false;
            }

            long rel = (long)newTarget - (_site.ToInt64() + JumpLength);

            if (rel < int.MinValue || rel > int.MaxValue)
            {
                Plugin.Log.Warning($"[原地改写] 新跳转目标超出 rel32 范围（{rel}），跳过。");
                return false;
            }

            byte[] patched =
            {
                0x0F,
                0x85,
                (byte)(rel & 0xFF),
                (byte)((rel >> 8) & 0xFF),
                (byte)((rel >> 16) & 0xFF),
                (byte)((rel >> 24) & 0xFF),
            };

            if (!NativeMemory.WriteBytes(_site, patched))
            {
                return false;
            }

            // 回读确认写入生效。
            if (!NativeMemory.TryReadBytes(_site, JumpLength, out byte[] after))
            {
                Plugin.Log.Warning("[原地改写] 写入后无法回读，状态未知。");
                return false;
            }

            if (!BytesEqual(after, patched))
            {
                Plugin.Log.Warning($"[原地改写] 回读不符，实际：{Hex(after)}（写入可能被拒绝）。");
                return false;
            }

            _installed = true;

            Plugin.Log.Msg(
                $"[原地改写] 0x{VaAliveJump:x} 的 jne 目标已改为 0x{VaSkipToTeamCheck:x}" +
                $"（运行时 0x{_site.ToInt64():x}，字节 {Hex(OriginalBytes)} → {Hex(after)}）。" +
                "只放行友方。");

            return true;
        }
        catch (Exception e)
        {
            Plugin.Log.Error($"[原地改写] 异常：{e.Message}");
            return false;
        }
    }

    /// <summary>还原原始字节。用于关闭开关或卸载。</summary>
    internal static void Uninstall()
    {
        if (!_installed || _site == IntPtr.Zero)
        {
            return;
        }

        try
        {
            if (NativeMemory.WriteBytes(_site, OriginalBytes))
            {
                Plugin.Log.Msg($"[原地改写] 已还原 0x{_site.ToInt64():x} 的原始字节。");
            }
            else
            {
                Plugin.Log.Warning($"[原地改写] 还原 0x{_site.ToInt64():x} 失败。");
            }
        }
        catch (Exception e)
        {
            Plugin.Log.Warning($"[原地改写] 还原异常：{e.Message}");
        }
        finally
        {
            _installed = false;
            _site = IntPtr.Zero;
        }
    }

    private static bool BytesEqual(byte[] a, byte[] b)
    {
        if (a.Length != b.Length)
        {
            return false;
        }

        for (int i = 0; i < a.Length; i++)
        {
            if (a[i] != b[i])
            {
                return false;
            }
        }

        return true;
    }

    private static string Hex(byte[] bytes)
    {
        var parts = new string[bytes.Length];

        for (int i = 0; i < bytes.Length; i++)
        {
            parts[i] = bytes[i].ToString("x2");
        }

        return string.Join(' ', parts);
    }
}
