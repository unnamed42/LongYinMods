using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using MelonLoader;
using MelonLoader.NativeUtils;

namespace Unnamed42.FriendlyNoclip;

/// <summary>
/// 在 <c>GridUnitData.OnEnter(BattleUnit)</c> 的<b>原生入口</b>装 detour，
/// 让「穿越友方」不留痕迹：路过别人的格子时，事后把原主登记写回。
///
/// <para>
/// <b>为什么不能用 Harmony</b>：实测 <c>Harmony.Patch</c> 绑定的
/// <c>MethodHandle.GetFunctionPointer()</c> 是 Il2CppInterop 生成的 <b>托管 DMD 包装</b>
/// （日志 <c>IL=6fff97ab04b8</c>，与 <c>OnLeave</c> 的 <c>6fff97ab04c0</c> 等挤在同一片
/// <c>0x6fff97aa…</c> 区域）。而 <c>BattleUnit.EnterGrid</c> 里是一条
/// <c>call 0x180873b20</c>（全镜像仅此一个调用点），直达函数体，
/// <b>不经过 DMD 包装</b> —— 所以 Harmony 补丁在真实移动中一次都不触发。
/// 托管侧调用的方法（<c>BattleGridClicked</c> 等）会走 DMD，因此那些补丁正常。
/// </para>
///
/// <para>
/// <b>为什么用 stub 而不是托管回调</b>：需要「调用原函数<b>之后</b>」才写回，
/// 而原主指针必须跨这次调用存活。托管 delegate 无法在被调函数返回后可靠持有现场，
/// 手写 stub 用自己的栈保存状态，语义完全可控。
/// </para>
///
/// <para>
/// <b>入参寄存器</b>（Win64）：<c>rcx</c> = <c>this</c>（目标 <c>GridUnitData</c>），
/// <c>rdx</c> = <c>battleUnit</c>（进入该格的单位）。两者在进入时保存到本 stub 的栈上。
/// </para>
///
/// <para>
/// <b>栈对齐</b>：进入 stub 时 <c>rsp % 16 == 8</c>（call 压入返回地址后的标准状态）。
/// <c>push rbx</c> 后为 <c>% 16 == 0</c>，<c>sub rsp,0x28</c> 后为 <c>% 16 == 8</c>。
/// 随后 <c>call rax</c> 压入返回地址变回 <c>% 16 == 0</c>——这正是被调函数
/// 入口所期望的状态（它自己再 <c>push rbx</c> / <c>sub rsp,0x20</c>）。
/// </para>
/// </summary>
internal static class NativeOnEnterDetour
{
    /// <summary><see cref="NativeHook{T}"/> 的泛型占位签名。永不调用，仅满足 <c>where T : Delegate</c>。</summary>
    private delegate void DetourSignature();

    /// <summary><c>GridUnitData.OnEnter(BattleUnit)</c> 的静态 VA（RVA <c>0x873b20</c>）。</summary>
    internal const ulong VaOnEnter = 0x180873B20UL;

    /// <summary>函数入口原始字节（<c>push rbx; sub rsp,0x20</c>），安装前校验用。</summary>
    private static readonly byte[] OriginalBytes = { 0x40, 0x53, 0x48, 0x83, 0xEC, 0x20 };

    /// <summary><c>GridUnitData.battleUnit</c> 字段偏移。</summary>
    private const int OffBattleUnit = 0x18;

    /// <summary><c>BattleUnit.mapGrid</c> 字段偏移。</summary>
    private const int OffMapGrid = 0x60;

    /// <summary><c>BattleUnit.destroyed</c> 字段偏移。</summary>
    private const int OffDestroyed = 0xD5;

    // 本 stub 的栈槽布局（相对进入 stub 后的 rsp）：
    //   [rsp+0x00] = this
    //   [rsp+0x08] = battleUnit（进入该格的单位）
    //   [rsp+0x10] = 原主（被覆盖的那个 BattleUnit），进入前此格的主人
    //   [rsp+0x18] = 写回标志：非 0 表示返回后要把 [rsp+0x10] 写回 this.battleUnit
    private const int SlotThis = 0x00;
    private const int SlotIncoming = 0x08;
    private const int SlotOccupant = 0x10;
    private const int SlotRestore = 0x18;

    private static IntPtr _stub = IntPtr.Zero;
    private static NativeHook<DetourSignature>? _hook;
    private static bool _installed;
    private static bool _detached;

    /// <summary>stub 内 <c>mov rax, imm64</c> 立即数的字节偏移（Attach 后回填跳板地址）。</summary>
    private static int _trampolineImmOffset = -1;

    /// <summary>Attach 前生成的 stub 字节，Attach 后回填跳板地址并重写内存。</summary>
    private static byte[]? _code;

    /// <summary>是否已成功安装 detour。</summary>
    internal static bool Installed => _installed;

    /// <summary>上一次 <see cref="Uninstall"/> 是否真的走到了 detach。</summary>
    internal static bool Detached => _detached;

    /// <summary>
    /// 安装 detour。失败只记日志，绝不抛异常打断游戏启动。
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
                Plugin.Log.Warning("[OnEnter detour] 找不到 GameAssembly.dll 模块基址，跳过。");
                return false;
            }

            IntPtr site = NativeMemory.StaticVaToRuntime(VaOnEnter);

            if (!NativeMemory.TryReadBytes(site, OriginalBytes.Length, out byte[] current))
            {
                Plugin.Log.Warning($"[OnEnter detour] 无法读取 0x{site.ToInt64():x} 的字节，跳过。");
                return false;
            }

            if (!BytesEqual(current, OriginalBytes))
            {
                Plugin.Log.Warning(
                    $"[OnEnter detour] 0x{site.ToInt64():x} 处字节与预期不符" +
                    $"（实际：{Hex(current)}，预期：{Hex(OriginalBytes)}），拒绝安装。");
                return false;
            }

            _code = BuildStub();

            _stub = NativeMemory.AllocateExecutable(_code.Length);

            if (_stub == IntPtr.Zero)
            {
                return false;
            }

            Marshal.Copy(_code, 0, _stub, _code.Length);

            _hook = new NativeHook<DetourSignature>(site, _stub);
            _hook.Attach();

            if (!_hook.IsHooked)
            {
                Plugin.Log.Warning(
                    $"[OnEnter detour] NativeHook.Attach() 返回后 IsHooked=false（落点 0x{site.ToInt64():x}），拒绝。");
                return false;
            }

            // Attach 之后才知道 Dobby 跳板地址，此时回填 stub 里的 mov rax, imm64。
            FillTrampoline();

            _installed = true;

            Plugin.Log.Msg(
                $"[OnEnter detour] 已挂上 0x{VaOnEnter:x}（运行时 0x{site.ToInt64():x}）→ stub 0x{_stub.ToInt64():x}，" +
                $"跳板 0x{_hook.TrampolineHandle.ToInt64():x}。落点现状：{NativeMemory.HexDump(site, 8)}");

            return true;
        }
        catch (Exception e)
        {
            Plugin.Log.Error($"[OnEnter detour] 安装异常：{e}");
            return false;
        }
    }

    /// <summary>把 Dobby 跳板地址写进 stub 的 <c>mov rax, imm64</c>，使 stub 能回调原函数。</summary>
    private static void FillTrampoline()
    {
        if (_stub == IntPtr.Zero || _hook == null || _code == null || _trampolineImmOffset < 0)
        {
            return;
        }

        long tramp = _hook.TrampolineHandle.ToInt64();

        if (tramp == 0)
        {
            Plugin.Log.Error("[OnEnter detour] 跳板地址为 0，stub 无法回调原函数，拒绝安装。");
            throw new InvalidOperationException("跳板地址为 0");
        }

        Array.Copy(BitConverter.GetBytes(tramp), 0, _code, _trampolineImmOffset, 8);
        Marshal.Copy(_code, 0, _stub, _code.Length);

        Plugin.Log.Msg($"[OnEnter detour] stub 已回填跳板 0x{tramp:x}。");
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
                _hook.Detach();
                Plugin.Log.Msg($"[OnEnter detour] 已摘除（IsHooked={_hook.IsHooked}）。");
                _detached = true;
            }
            else
            {
                Plugin.Log.Warning("[OnEnter detour] 摘除被跳过：hook 不存在或未挂上。hook 可能仍挂着。");
            }
        }
        catch (Exception e)
        {
            Plugin.Log.Warning($"[OnEnter detour] 摘除异常：{e.Message}");
        }
        finally
        {
            _installed = false;
            _hook = null;
        }
    }

    /// <summary>
    /// 构造 detour stub：记录原主 → 调用原函数 → 若原主活着且其 <c>mapGrid</c> 仍指向本格，
    /// 则把登记写回。
    ///
    /// <para>
    /// <b>恒调用原函数</b>（B 方案）：渲染、特效、UI 全部照常执行，只在返回后修登记。
    /// 三条「不写回」的早退路径统一跳到 <c>call_original</c>，由 <c>SlotRestore</c> 是否为 0
    /// 决定最终是否写回 —— 这样只有一处写回代码，减少出错面。
    /// </para>
    /// </summary>
    private static byte[] BuildStub()
    {
        var s = new List<byte>(192);

        // ---------- 栈帧 ----------
        s.Add(0x53);                                          // push rbx
        s.AddRange(new byte[] { 0x48, 0x83, 0xEC, 0x28 });    // sub rsp, 0x28

        // ---------- 保存入参 ----------
        // mov [rsp+0x00], rcx   ; this
        EmitStore(s, 0x48, 0x89, 0x4C, SlotThis);
        // mov [rsp+0x08], rdx   ; battleUnit
        EmitStore(s, 0x48, 0x89, 0x54, SlotIncoming);

        // ---------- 默认不写回 ----------
        // xor eax, eax
        s.AddRange(new byte[] { 0x33, 0xC0 });
        // mov [rsp+0x18], rax
        EmitStore(s, 0x48, 0x89, 0x44, SlotRestore);

        // ---------- occupant = this.battleUnit ----------
        // mov rax, [rsp+0x00]         ; this
        EmitLoad(s, 0x48, 0x8B, 0x44, SlotThis);
        // mov rax, [rax+0x18]
        s.AddRange(new byte[] { 0x48, 0x8B, 0x40, OffBattleUnit });
        // mov [rsp+0x10], rax
        EmitStore(s, 0x48, 0x89, 0x44, SlotOccupant);
        // test rax, rax
        s.AddRange(new byte[] { 0x48, 0x85, 0xC0 });
        // jz call_original
        s.AddRange(new byte[] { 0x0F, 0x84, 0, 0, 0, 0 });
        int jzNoOccupant = s.Count - 4;

        // ---------- 原主必须存活 ----------
        // cmp byte [rax+0xd5], 0
        s.AddRange(new byte[] { 0x80, 0xB8, OffDestroyed, 0x00, 0x00, 0x00, 0x00 });
        // jne call_original
        s.AddRange(new byte[] { 0x0F, 0x85, 0, 0, 0, 0 });
        int jneDead = s.Count - 4;

        // ---------- 原主的 mapGrid 必须正指向本格 ----------
        // mov rax, [rax+0x60]         ; occupant.mapGrid
        s.AddRange(new byte[] { 0x48, 0x8B, 0x40, OffMapGrid });
        // cmp rax, [rsp+0x00]         ; == this ?
        EmitLoad(s, 0x48, 0x3B, 0x44, SlotThis);
        // jne call_original
        s.AddRange(new byte[] { 0x0F, 0x85, 0, 0, 0, 0 });
        int jneNoMatch = s.Count - 4;

        // ---------- 标记写回 ----------
        // mov rax, [rsp+0x10]
        EmitLoad(s, 0x48, 0x8B, 0x44, SlotOccupant);
        // mov [rsp+0x18], rax
        EmitStore(s, 0x48, 0x89, 0x44, SlotRestore);

        // ---------- call_original ----------
        int callOriginal = s.Count;

        // mov rcx, [rsp+0x00]         ; this
        EmitLoad(s, 0x48, 0x8B, 0x4C, SlotThis);
        // mov rdx, [rsp+0x08]         ; battleUnit
        EmitLoad(s, 0x48, 0x8B, 0x54, SlotIncoming);
        // mov rax, imm64              ; 由 FillTrampoline() 回填
        s.AddRange(new byte[] { 0x48, 0xB8, 0, 0, 0, 0, 0, 0, 0, 0 });
        _trampolineImmOffset = s.Count - 8;
        // call rax
        s.AddRange(new byte[] { 0xFF, 0xD0 });

        // ---------- 写回 ----------
        // mov rax, [rsp+0x18]
        EmitLoad(s, 0x48, 0x8B, 0x44, SlotRestore);
        // test rax, rax
        s.AddRange(new byte[] { 0x48, 0x85, 0xC0 });
        // jz done
        s.Add(0x74);
        int jzDone = s.Count;
        s.Add(0);

        // mov rcx, [rsp+0x00]         ; this
        EmitLoad(s, 0x48, 0x8B, 0x4C, SlotThis);
        // mov [rcx+0x18], rax         ; this.battleUnit = occupant
        s.AddRange(new byte[] { 0x48, 0x89, 0x41, OffBattleUnit });

        // done:
        int done = s.Count;
        s.AddRange(new byte[] { 0x48, 0x83, 0xC4, 0x28 });    // add rsp, 0x28
        s.Add(0x5B);                                          // pop rbx
        s.Add(0xC3);                                          // ret

        byte[] code = s.ToArray();

        PatchRel32(code, jzNoOccupant, callOriginal);
        PatchRel32(code, jneDead, callOriginal);
        PatchRel32(code, jneNoMatch, callOriginal);
        code[jzDone] = (byte)(done - (jzDone + 1));

        return code;
    }

    /// <summary>把 <paramref name="at"/> 处的 rel32 补成指向 <paramref name="target"/>。</summary>
    private static void PatchRel32(byte[] code, int at, int target)
    {
        Array.Copy(BitConverter.GetBytes(target - (at + 4)), 0, code, at, 4);
    }

    /// <summary><c>mov r/m64, reg</c> 形式：<c>48 89 &lt;modrm&gt; 24 disp8</c>。</summary>
    private static void EmitStore(List<byte> s, byte rex, byte opcode, int modrm, int disp)
    {
        s.AddRange(new byte[] { rex, opcode, (byte)(modrm | 0x04), 0x24, (byte)disp });
    }

    /// <summary><c>mov reg, [rsp+disp8]</c> 或 <c>cmp reg, [rsp+disp8]</c> 形式。</summary>
    private static void EmitLoad(List<byte> s, byte rex, byte opcode, int modrm, int disp)
    {
        s.AddRange(new byte[] { rex, opcode, (byte)(modrm | 0x04), 0x24, (byte)disp });
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
