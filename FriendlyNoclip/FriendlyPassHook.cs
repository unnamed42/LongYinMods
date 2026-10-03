using System;
using System.Collections.Generic;

namespace Unnamed42.FriendlyNoclip;

/// <summary>
/// 穿友方单位：在 <c>MapNavigator.Navigate</c> 内的 <c>0x180a8d929</c> 装 detour，
/// 把「任何存活单位都阻挡」改成「只阻挡**敌方**单位」。
///
/// <para><b>为什么需要原生层改动</b>：<c>Navigate</c> 里 <c>0x180a8d929</c> 的
/// <c>jne</c> 不分敌我 —— 邻格上只要站着存活单位就跳过。游戏另一处
/// <c>AroundGridHaveEnemy</c> 回答的是「**空格**旁边有没有敌人」，
/// 那是另一个问题：把友方格交给它判定，在混战中（友方紧邻敌方）必然失败，
/// 已由实机验证。所以「只穿友方」必须自己比较队伍 ID。</para>
///
/// <para><b>为什么用 stub 而不是托管回调</b>：<c>0x180a8d929</c> 是函数中间地址，
/// 此刻有效状态在 <c>rsi</c>（邻格 <c>g</c>）与 <c>[rsp+0xd8]</c>（<c>selfTeamID</c>），
/// 而托管 delegate 的参数只映射 Win64 参数寄存器 <c>rcx/rdx/r8/r9</c>，
/// 拿不到这两者。所以这里放一段手写 x64 stub，直接读原寄存器与栈。</para>
///
/// <para><b>为什么不用自己分配跳板</b>：此前的版本自己算 rel32 + <c>VirtualAlloc</c>
/// 从映像末尾向上找洞，一装上就 coredump。改用 Dobby 后，
/// 指令搬迁与跳板分配都由它负责（near allocator 保证 rel32 可达）。</para>
/// </summary>
internal sealed class FriendlyPassHook : NativeHookBase
{
    /// <summary>
    /// hook 点：<c>Navigate</c> 内 <c>0x180a8d929</c> 的 <c>jne 0x180a8da6b</c>。
    /// 该地址是干净的单条指令起点，且镜像内零个分支跳入（已全量扫描确认）。
    /// </summary>
    private const ulong VaHookSite = 0x180A8D929UL;

    /// <summary>原 <c>jne</c> 的目标：方向递增后继续循环（跳过该邻格）。</summary>
    private const ulong VaSkip = 0x180A8DA6BUL;

    /// <summary>原 <c>jne</c> 不跳时的落点：走「空格」判定（<c>AroundGridHaveEnemy</c>）。</summary>
    private const ulong VaEmptyCell = 0x180A8D92FUL;

    /// <summary>直接扩展邻格（<c>[rsi+0x40]</c> 起，跳过两处占位判定）。</summary>
    private const ulong VaExpand = 0x180A8D963UL;

    private static readonly byte[] OriginalBytes_ = { 0x0F, 0x85, 0x3C, 0x01, 0x00, 0x00 };

    /// <summary>单例：本 hook 全进程只会挂一次。</summary>
    internal static readonly FriendlyPassHook Instance = new();

    private FriendlyPassHook()
    {
    }

    protected override string Tag => "[原生 detour]";

    protected override ulong HookVa => VaHookSite;

    protected override byte[] OriginalBytes => OriginalBytes_;

    protected override bool ValidateSite(byte[] current, out string reason)
    {
        // 只要求它仍是一条 6 字节 rel32 jcc（`jne`，或已被改过位移的 `jne`）。
        // 曾经在同一地址做过原地改写探针，那样原值就变了 —— 但搬迁依然安全。
        if (!IsRel32Jcc(current))
        {
            reason = "落点不是 6 字节 jcc rel32。";
            return false;
        }

        reason = string.Empty;
        return true;
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
    protected override byte[] BuildStub()
    {
        long skip = RuntimeVa(VaSkip);
        long empty = RuntimeVa(VaEmptyCell);
        long expand = RuntimeVa(VaExpand);

        var stub = new List<byte>(70);

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
}
