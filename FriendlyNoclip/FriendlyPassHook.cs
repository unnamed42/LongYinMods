using System;
using System.Collections.Generic;
using Iced.Intel;
using static Iced.Intel.AssemblerRegisters;

namespace Unnamed42.FriendlyNoclip;

/// <summary>
/// 穿友方单位：在 <c>MapNavigator.Navigate</c> 内的 <c>0x180a8d929</c> 装 detour，
/// 把「任何存活单位都阻挡」改成「只阻挡**敌方**单位」。
///
/// <para><b>为什么需要原生层改动</b>：<c>Navigate</c> 里 <c>0x180a8d929</c> 的
/// <c>jne</c> 不分敌我 —— 邻格上只要站着存活单位就跳过。所以「只穿友方」
/// 必须自己比较队伍 ID，不能把这个判断交给游戏原有的任何一处。</para>
///
/// <para><b>★★ 出口必须是 <c>0x180a8d92f</c>，而且必须重跑 <c>AroundGridHaveEnemy</c></b>：
/// 本 hook 只负责回答「**这个格子上的人是不是队友**」，
/// 而**不能**代替游戏回答「**站到那个格子上合不合法**」。
/// <c>0x180a8d92f</c> 正是原版 <c>jne</c> **不跳**时的落点 —— 即游戏自己的
/// 「空格链」，它先调 <c>AroundGridHaveEnemy(row, col, selfTeamID)</c>
/// （<c>0x1808c41f0</c>，交战区 / 敌方邻接检查）再决定是否接受。
/// </para>
///
/// <para>
/// ⚠️⚠️ 2026-10 修正：这里**曾经**跳到 <c>0x180a8d963</c>（当时叫 <c>expand</c>），
/// 那是错的 —— 该地址在 <c>AroundGridHaveEnemy</c> **已经返回通过之后**，
/// 跳过去等于**跳过占位 / 交战区判定**。真机后果：
/// **AI 会把玩家所在的格子当成可落点，直接站到玩家头上**。
/// 这跟 <see cref="WallPassHook"/> 那个 <c>expand</c> bug 是**同一类错误**。
/// </para>
///
/// <para>
/// 旧注释曾声称「把友方格交给 <c>AroundGridHaveEnemy</c> 判定，在混战中必然失败，
/// 已由实机验证」—— 那个结论是在**错误的出口地址**下得到的，已被推翻：
/// 逃掉交战区检查会把原本不可落的格子放行，所以当时看到的「失败」混杂了两个原因。
/// </para>
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

    /// <summary>
    /// ★ 放行出口：原 <c>jne</c> **不跳**时的落点，游戏自己的「空格链」——
    /// 它会先调 <c>AroundGridHaveEnemy</c>（<c>0x1808c41f0</c>）再决定是否接受。
    /// <para>
    /// 「队友所在格」与「空格」走**同一条**判定链，这样本 hook 只负责改
    /// 「谁算阻挡」，不负责改「落点合不合法」。
    /// </para>
    /// </summary>
    private const ulong VaPass = 0x180A8D92FUL;

    private static readonly byte[] OriginalBytes_ = { 0x0F, 0x85, 0x3C, 0x01, 0x00, 0x00 };

    /// <summary>单例：本 hook 全进程只会挂一次。</summary>
    internal static readonly FriendlyPassHook Instance = new();

    private FriendlyPassHook()
    {
    }

    protected override string Tag => "[原生 detour]";

    protected override ulong HookVa => VaHookSite;

    protected override byte[] OriginalBytes => OriginalBytes_;

    /// <summary>
    /// 两个出口的预期运行时地址。安装后由 <c>NativeHookBase.VerifyExits</c> 对账。
    ///
    /// <para>
    /// 这就是「手写汇编时代只能靠人肉核对」的那件事 —— 现在：
    /// 标签地址由汇编器算，我们只声明「应该跳到哪」，不匹配就报 ERROR。
    /// </para>
    /// </summary>
    protected override IReadOnlyDictionary<string, long> ExpectedExits => ExitExpectations;

    /// <summary>
    /// ⚠️ 必须是**属性**，不能是 <c>static readonly</c> 字段。
    ///
    /// <para>
    /// 静态字段初始化发生在「首次触碰该类型」时 —— 那可能早于
    /// <see cref="NativeMemory.ResolveModule"/>（模块基址是在 <c>OnInitializeMelon</c> 里才解析的）。
    /// 此时 <c>RuntimeVa</c> 返回 <b>0</b>，于是自检会报
    /// 「出口指向 0x…，不在声明的出口集合里（pass=0x0 skip=0x0）」——
    /// 一条**纯粹由初始化顺序造成的假警报**。
    /// </para>
    /// <para>
    /// 本项目第一次跑就撞上了这个（活进程日志，2026-10）：自检本身工作正常，
    /// 错的是「什么时候算这个值」。改成属性后每次读取都重新换算，问题消失。
    /// </para>
    /// </summary>
    private static IReadOnlyDictionary<string, long> ExitExpectations =>
        new Dictionary<string, long>
        {
            ["pass"] = RuntimeVa(VaPass),
            ["skip"] = RuntimeVa(VaSkip),
        };

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
    /// 构造 detour stub —— <b>用 Iced 汇编器，不再手写机器码</b>。
    ///
    /// <para>入口状态（已逐条反汇编确认）：</para>
    /// <list type="bullet">
    ///   <item><c>al</c> = <c>get_IsAlive(g.battleUnit)</c> 的返回值</item>
    ///   <item><c>rsi</c> = 邻格 <c>g</c>（<c>GridUnitData</c>）</item>
    ///   <item><c>[rsp+0xd8]</c> = <c>selfTeamID</c>（相对 <c>Navigate</c> 的栈帧）</item>
    /// </list>
    /// <para>
    /// 关键：Dobby 是<b>跳</b>到 stub（不是 <c>call</c>），所以 <c>rsp</c> 不变，
    /// <c>[rsp+0xd8]</c> 仍是 <c>selfTeamID</c>。
    /// </para>
    ///
    /// <para>
    /// <b>★★ 只有两个出口，而且「队友格」与「空格」合流：</b>
    /// 本 hook 的职责边界是「把阻挡判据从『有存活单位』改成『有敌方单位』」，
    /// <b>不碰</b>「落点合不合法」。所以只要不是敌方，就一律送回
    /// <c>0x180a8d92f</c>（空格链，会跑 <c>AroundGridHaveEnemy</c>），
    /// 而不是直接接受。
    /// </para>
    ///
    /// <para>
    /// 出口用 <see cref="StubAssembler.ExitViaImm64"/>（<c>mov r11,imm64; jmp r11</c>），
    /// 不依赖 stub 与目标之间的 rel32 距离。
    /// （不用 <c>rax</c>：<c>0x180a8d92f</c> 之后的原代码可能依赖上一条指令在
    /// <c>rax</c> 里留下的值 —— 本项目在 <see cref="WallPassHook"/> 上踩过这个坑。）
    /// </para>
    /// </summary>
    protected override byte[] BuildStub(long stubRip)
    {
        return StubAssembler.Build(Tag, 64, stubRip, asm =>
        {
            Label exitSkip = asm.CreateLabel("skip");
            Label exitPass = asm.CreateLabel("pass");

            // al == 0（无存活单位）→ 空格链，保持原行为。
            asm.test(al, al);
            asm.je(exitPass);

            asm.mov(rax, __qword_ptr[rsi + 0x18]);        // g.battleUnit
            asm.test(rax, rax);
            asm.jz(exitSkip);

            asm.mov(rax, __qword_ptr[rax + 0x58]);        // battleUnit.battleTeam
            asm.test(rax, rax);
            asm.jz(exitSkip);

            asm.mov(eax, __dword_ptr[rax + 0x10]);        // battleTeam.ID
            asm.cmp(eax, __dword_ptr[rsp + SelfTeamIdStackOffset]);
            asm.je(exitPass);                             // 同队 -> pass（走空格链，不可直接接受）

            // ---- 两个出口 ----
            // ⚠️ exitPass 必须与 exitSkip 分列两条指令，不能绑在同一位置
            //    （Iced：At most one label per instruction is allowed）。
            asm.Label(ref exitSkip);
            asm.ExitViaImm64(RuntimeVa(VaSkip));

            asm.Label(ref exitPass);
            asm.ExitViaImm64(RuntimeVa(VaPass));
        });
    }

    /// <summary>
    /// <c>selfTeamID</c> 相对 <c>Navigate</c> 栈帧的偏移（即 <c>[rsp+0xd8]</c>）。
    /// Dobby 是<b>跳</b>进 stub，<c>rsp</c> 完全不变，故该偏移在 stub 内直接可用。
    /// </summary>
    private const int SelfTeamIdStackOffset = 0xD8;
}
