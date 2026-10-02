using System;
using System.Runtime.InteropServices;
using MelonLoader;
using MelonLoader.NativeUtils;

namespace Unnamed42.FriendlyNoclip;

/// <summary>
/// 用 MelonLoader 自带的 <c>NativeHook</c>（Dobby 内联替换）在 <c>MapNavigator.Navigate</c>
/// 内部装一个 detour，把「任何存活单位都阻挡」改成「只阻挡**敌方**单位」。
///
/// <para>
/// <b>为什么需要原生层改动</b>：<c>Navigate</c> 里 <c>0x180a8d929</c> 的
/// <c>jne</c> 不分敌我 —— 邻格上只要站着存活单位就跳过。游戏另一处
/// <c>AroundGridHaveEnemy</c> 回答的是「**空格**旁边有没有敌人」，
/// 那是另一个问题：把友方格交给它判定，在混战中（友方紧邻敌方）必然失败，
/// 已由实机验证。所以「只穿友方」必须自己比较队伍 ID。
/// </para>
///
/// <para>
/// <b>为什么用 stub 而不是托管回调</b>：<c>0x180a8d929</c> 是函数中间地址，
/// 此刻有效状态在 <c>rsi</c>（邻格 <c>g</c>）与 <c>[rsp+0xd8]</c>（<c>selfTeamID</c>），
/// 而托管 delegate 的参数只映射 Win64 参数寄存器 <c>rcx/rdx/r8/r9</c>，
/// 拿不到这两者。所以这里放一段手写 x64 stub，直接读原寄存器与栈。
/// </para>
///
/// <para>
/// <b>为什么不用自己分配跳板</b>：此前的版本自己算 rel32 + <c>VirtualAlloc</c>
/// 从映像末尾向上找洞，一装上就 coredump。改用 Dobby 后，
/// 指令搬迁与跳板分配都由它负责（near allocator 保证 rel32 可达）。
/// </para>
///
/// <para>
/// <b>为什么用 <see cref="NativeHook{T}"/> 而不是 <c>MelonUtils.NativeHookAttach</c></b>：
/// 后者带 <c>[Obsolete(..., error: true)]</c>（MelonLoader 0.7.3），调用是编译错误；
/// 同名的 <c>NativeHookAttachDirect</c> 则是 <c>internal</c>。
/// <see cref="NativeHook{T}"/> 是公开类型，其 <c>Attach()</c> 内部走
/// <c>BootstrapInterop.NativeHookAttach</c>，在 Wine/Proton 下 <c>||</c> 短路，
/// 不会触发 <c>SanityCheckDetour</c>（那在 Linux 下会做全量 coredump）。
/// </para>
///
/// <para>
/// <b>关于泛型参数</b>：<see cref="NativeHook{T}"/> 的 <c>HookAttach()</c> 会把跳板地址
/// <c>Marshal.GetDelegateForFunctionPointer</c> 成 <c>T</c>。我们的跳板是 Dobby 生成的
/// 原函数代码，签名并不匹配 <c>T</c> —— 但该转换<em>不执行</em>任何代码，
/// 且本类<em>从不读取</em> <c>Trampoline</c> 属性，所以只是声明层面的占位，不会触发 UB。
/// </para>
/// </summary>
internal static class NativeDetour
{
    /// <summary>
    /// <see cref="NativeHook{T}"/> 的泛型占位签名。永不调用，仅满足 <c>where T : Delegate</c>。
    /// </summary>
    private delegate void DetourSignature();
    /// <summary>
    /// hook 点：<c>Navigate</c> 内 <c>0x180a8d929</c> 的 <c>jne 0x180a8da6b</c>。
    /// 该地址是干净的单条指令起点，且镜像内零个分支跳入（已全量扫描确认）。
    /// </summary>
    internal const ulong VaHookSite = 0x180A8D929UL;

    /// <summary>原 <c>jne</c> 的目标：方向递增后继续循环（跳过该邻格）。</summary>
    private const ulong VaSkip = 0x180A8DA6BUL;

    /// <summary>原 <c>jne</c> 不跳时的落点：走「空格」判定（<c>AroundGridHaveEnemy</c>）。</summary>
    private const ulong VaEmptyCell = 0x180A8D92FUL;

    /// <summary>直接扩展邻格（<c>[rsi+0x40]</c> 起，跳过两处占位判定）。</summary>
    private const ulong VaExpand = 0x180A8D963UL;

    /// <summary>原指令字节，用于安装前校验落点。</summary>
    private static readonly byte[] OriginalBytes = { 0x0F, 0x85, 0x3C, 0x01, 0x00, 0x00 };

    private static IntPtr _stub = IntPtr.Zero;
    private static NativeHook<DetourSignature>? _hook;
    private static bool _installed;
    private static bool _detached;

    /// <summary>是否已成功安装 detour。</summary>
    internal static bool Installed => _installed;

    /// <summary>上一次 <see cref="Uninstall"/> 是否真的走到了 detach。</summary>
    internal static bool Detached => _detached;

    /// <summary>
    /// 构造 stub、分配可执行内存、并用 <c>NativeHook</c> 挂上。
    /// 失败只记日志，绝不抛异常打断游戏启动。
    /// </summary>
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
                Plugin.Log.Warning("[原生 detour] 找不到 GameAssembly.dll 模块基址，跳过。");
                return false;
            }

            IntPtr site = NativeMemory.StaticVaToRuntime(VaHookSite);

            // 安装前校验：确认落点是一条 6 字节的 jcc rel32（`jne` 或已被改过目标的 `jne`）。
            //
            // 注意：这里**不能**要求字节与原始值完全一致 ——
            // NativePatchProbe 会在同一地址做原地改写探针（把目标从 0x180a8da6b
            // 改成 0x180a8d93b），那样原始值就变了。只要它仍是一条 6 字节 rel32 jcc，
            // Dobby 的搬迁就是安全的（subagent 已确认这种指令落「原样复制」分支）。
            if (!NativeMemory.TryReadBytes(site, OriginalBytes.Length, out byte[] current))
            {
                Plugin.Log.Warning($"[原生 detour] 无法读取 0x{site.ToInt64():x} 的字节，跳过。");
                return false;
            }

            if (!IsRel32Jcc(current))
            {
                Plugin.Log.Warning(
                    $"[原生 detour] 0x{site.ToInt64():x} 处不是 6 字节 jcc rel32，拒绝安装。" +
                    $"实际：{Hex(current)}");
                return false;
            }

            if (!BytesEqual(current, OriginalBytes))
            {
                Plugin.Log.Warning(
                    $"[原生 detour] 注意：0x{site.ToInt64():x} 处字节已被其他补丁改写" +
                    $"（实际：{Hex(current)}，原始：{Hex(OriginalBytes)}），" +
                    "仍按当前形态安装 detour。");
            }

            byte[] code = BuildStub();

            _stub = NativeMemory.AllocateExecutable(code.Length);

            if (_stub == IntPtr.Zero)
            {
                return false;
            }

            Marshal.Copy(code, 0, _stub, code.Length);

            // 用官方公开类型 NativeHook<T> 挂载。
            //
            // Attach() 内部走 BootstrapInterop.NativeHookAttach，该函数在 Wine/Proton 下
            // 因 || 短路而不求值 SanityCheckDetour（后者在原生 Linux 会做全量 coredump）。
            // 排错时不再需要自己维护 target/跳板地址 —— 全部由 NativeHook<T> 持有。
            _hook = new NativeHook<DetourSignature>(site, _stub);

            _hook.Attach();

            if (!_hook.IsHooked)
            {
                Plugin.Log.Warning(
                    $"[原生 detour] NativeHook.Attach() 返回后 IsHooked=false（落点 0x{site.ToInt64():x}），拒绝。");
                return false;
            }

            _installed = true;

            // 回读确认 hook 生效：Dobby 会在 site 处写入一条 E9。
            string siteNow = NativeMemory.HexDump(site, 8);

            Plugin.Log.Msg(
                $"[原生 detour] 已挂上 0x{VaHookSite:x}（运行时 0x{site.ToInt64():x}）→ stub 0x{_stub.ToInt64():x}，" +
                $"跳板 0x{_hook.TrampolineHandle.ToInt64():x}。落点现状：{siteNow}");

            return true;
        }
        catch (Exception e)
        {
            Plugin.Log.Error($"[原生 detour] 安装异常：{e}");
            return false;
        }
    }

    /// <summary>摘掉 detour 并释放 stub 内存。</summary>
    internal static void Uninstall()
    {
        if (!_installed)
        {
            return;
        }

        try
        {
            if (_hook is { IsHooked: true })
            {
                // HookDetach() 内部走 BootstrapInterop.NativeHookDetach，
                // 并自行清空 _trampoline / _trampolineHandle（字段直赋，不走会抛异常的 setter）。
                _hook.Detach();

                Plugin.Log.Msg(
                    $"[原生 detour] 已摘除（IsHooked={_hook.IsHooked}，落点 0x{_hook.Target.ToInt64():x}）。");
                _detached = true;
            }
            else
            {
                // 不谎报成功：状态必须与现实一致。
                Plugin.Log.Warning("[原生 detour] 摘除被跳过：hook 不存在或未挂上。hook 可能仍挂着。");
            }
        }
        catch (Exception e)
        {
            Plugin.Log.Warning($"[原生 detour] 摘除异常：{e.Message}");
        }
        finally
        {
            _installed = false;
            _hook = null;
        }
    }

    /// <summary>
    /// 构造 70 字节的 detour stub。
    ///
    /// <para>入口状态（已逐条反汇编确认）：</para>
    /// <list type="bullet">
    ///   <item><c>al</c> = <c>get_IsAlive(g.battleUnit)</c> 的返回值</item>
    ///   <item><c>rsi</c> = 邻格 <c>g</c>（<c>GridUnitData</c>）</item>
    ///   <item><c>[rsp+0xd8]</c> = <c>selfTeamID</c>（相对 <c>Navigate</c> 的栈帧）</item>
    /// </list>
    /// <para>
    /// 关键：Dobby 是**跳**到 stub（不是 <c>call</c>），所以 <c>rsp</c> 不变，
    /// <c>[rsp+0xd8]</c> 仍是 <c>selfTeamID</c>。
    /// </para>
    /// <para>
    /// 三个出口都用 <c>mov rax,imm64; jmp rax</c> 的绝对跳转，
    /// 不依赖 stub 与目标之间的 rel32 距离。
    /// </para>
    /// </summary>
    private static byte[] BuildStub()
    {
        // 出口必须是**运行时**地址，不是静态 VA。
        long skip = (long)(ulong)NativeMemory.StaticVaToRuntime(VaSkip);
        long empty = (long)(ulong)NativeMemory.StaticVaToRuntime(VaEmptyCell);
        long expand = (long)(ulong)NativeMemory.StaticVaToRuntime(VaExpand);

        var stub = new System.Collections.Generic.List<byte>(70);

        // +0  test al, al
        stub.AddRange(new byte[] { 0x84, 0xC0 });

        // +2  je <empty>      （al==0：无存活单位，保持原行为）
        stub.Add(0x74);
        stub.Add(0);

        // +4  mov rax, [rsi+0x18]   ; g.battleUnit
        stub.AddRange(new byte[] { 0x48, 0x8B, 0x46, 0x18 });

        // +8  test rax, rax
        stub.AddRange(new byte[] { 0x48, 0x85, 0xC0 });

        // +11 je <skip>
        stub.Add(0x74);
        stub.Add(0);

        // +13 mov rax, [rax+0x58]   ; battleUnit.battleTeam
        stub.AddRange(new byte[] { 0x48, 0x8B, 0x40, 0x58 });

        // +17 test rax, rax
        stub.AddRange(new byte[] { 0x48, 0x85, 0xC0 });

        // +20 je <skip>
        stub.Add(0x74);
        stub.Add(0);

        // +22 mov eax, [rax+0x10]   ; battleTeam.ID
        stub.AddRange(new byte[] { 0x8B, 0x40, 0x10 });

        // +25 cmp eax, [rsp+0xd8]   ; selfTeamID
        stub.AddRange(new byte[] { 0x3B, 0x84, 0x24, 0xD8, 0x00, 0x00, 0x00 });

        // +32 je <expand>           （同队 -> 放行）
        stub.Add(0x74);
        stub.Add(0);

        int skipOff = stub.Count;
        EmitAbsoluteJump(stub, skip);

        int emptyOff = stub.Count;
        EmitAbsoluteJump(stub, empty);

        int expandOff = stub.Count;
        EmitAbsoluteJump(stub, expand);

        // 回填 rel8。
        byte[] code = stub.ToArray();
        code[3] = (byte)(emptyOff - (2 + 2));
        code[12] = (byte)(skipOff - (11 + 2));
        code[21] = (byte)(skipOff - (20 + 2));
        code[33] = (byte)(expandOff - (32 + 2));

        return code;
    }

    /// <summary>发射 <c>mov rax, imm64</c> + <c>jmp rax</c>（12 字节，绝对跳转）。</summary>
    private static void EmitAbsoluteJump(System.Collections.Generic.List<byte> buffer, long target)
    {
        buffer.AddRange(new byte[] { 0x48, 0xB8 });
        buffer.AddRange(BitConverter.GetBytes(target));
        buffer.AddRange(new byte[] { 0xFF, 0xE0 });
    }

    /// <summary>
    /// 判断字节序列是否为一条 6 字节的 <c>jcc rel32</c>（<c>0F 8x</c>）。
    ///
    /// <para>
    /// 安装校验**不能**要求字节与出厂原值完全一致：<see cref="NativePatchProbe"/>
    /// 会在同一地址做原地改写（把跳转目标从 <c>0x180a8da6b</c> 换成 <c>0x180a8d93b</c>），
    /// 那样原值就变了。只要它仍是一条 6 字节 rel32 jcc，
    /// Dobby 的搬迁就是安全的（已确认这类指令落「原样复制」分支）。
    /// </para>
    /// </summary>
    private static bool IsRel32Jcc(byte[] bytes)
    {
        // 0F 80..0F 8F = jcc rel32
        return bytes.Length >= 2 && bytes[0] == 0x0F && bytes[1] >= 0x80 && bytes[1] <= 0x8F;
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
