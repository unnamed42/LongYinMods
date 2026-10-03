using System;
using System.Collections.Generic;
using Iced.Intel;
using static Iced.Intel.AssemblerRegisters;

namespace Unnamed42.FriendlyNoclip;

/// <summary>
/// 穿己方城墙：在 <c>MapNavigator.Navigate</c> 内的 <c>0x180a8d8b6</c> 装 detour，
/// 让**属于自己队伍**的城墙格可通行。
///
/// <para><b>与 <see cref="FriendlyPassHook"/> 的关系</b>：这是**独立的第二个 hook 点**。
/// 城墙比「存活单位」判定**更早**被排除，所以穿友方那套逻辑根本碰不到城墙 ——
/// 实测证据：装了穿友方之后，NPC 能穿、城墙依然不能穿（敌我双方都不能）。</para>
///
/// <para><b>原始行为</b>（<c>0x180a8d8b2</c> 起，已反汇编核实）：</para>
/// <code>
/// 0x180a8d8a1  call 0x1808c8a50          ; GetGridDataByDir -> 邻格 g (rax/rsi)
/// 0x180a8d8a9  test rax, rax
/// 0x180a8d8ac  je   0x180a8da6b          ; g == null -> skip
/// 0x180a8d8b2  cmp  dword [rax+0x14], 2   ; g.gridType == Obstacle ?
/// 0x180a8d8b6  je   0x180a8da6b          ; ★ 本 hook 点：障碍格一律 skip
/// </code>
///
/// <para><b>为什么 <c>gridType == Obstacle</c> 不够</b>：该分类同时包含城墙与
/// **中立障碍**（造景 / 雕像 / 木箱 / 木桶 / 灌木）。游戏原本对二者一视同仁地阻挡，
/// 所以那一行不读 <c>teamID</c> —— 这也解释了「敌方 AI 同样穿不过城墙」。
/// 因此判据必须下沉到 <c>ObstacleData</c> 的 <c>obstalceType</c>，只放行城墙。</para>
///
/// <para><b>字段偏移（2026-10 用 MCP 在活进程实测 + 原生内存交叉验证）</b>：</para>
/// <list type="table">
///   <item><term>GridUnitData.obstale</term><description>+0x30（ObstacleData*，可能为 0）</description></item>
///   <item><term>ObstacleData.obstalceType</term><description>+0x10（int：Normal=0, Wall=1）</description></item>
///   <item><term>ObstacleData.teamID</term><description>+0x2c（int：中立=-1）</description></item>
/// </list>
///
/// <para><b>判据 = 己方城墙</b>：<c>obstalceType == Wall(1) &amp;&amp; teamID == selfTeamID</c>。
/// 因为有 <c>selfTeamID</c> 参与，守方 AI 会**自动**获得穿越自己城墙的能力，
/// 与用户「每个 AI 都要有这个能力」的一贯要求一致，无需额外处理。</para>
///
/// <para><b>不会误伤箭塔 / 战鼓 / 分舵</b>：那些是普通 <c>BattleUnit</c>
/// （挂在 <c>g.battleUnit</c>，且 <c>g.obstale == null</c>，已实测），
/// 本钩子在 <c>obstale == null</c> 时直接放行到 skip，碰不到它们。
/// 它们归 <see cref="FriendlyPassHook"/> 那条线按队伍处理。</para>
/// </summary>
internal sealed class WallPassHook : NativeHookBase
{
    /// <summary>
    /// hook 点：<c>0x180a8d8b6</c> 的 <c>je 0x180a8da6b</c>（6 字节 rel32 jcc，
    /// 与 <see cref="FriendlyPassHook"/> 的落点同构，可等长替换）。
    /// </summary>
    private const ulong VaHookSite = 0x180A8D8B6UL;

    /// <summary>原 <c>je</c> 的目标：跳过该邻格（保持「障碍不可通行」的原行为）。</summary>
    private const ulong VaSkip = 0x180A8DA6BUL;

    /// <summary>
    /// 「放行」出口 —— <c>0x180a8d8bc</c>，即原 <c>je</c> <b>不跳</b>时的落点，
    /// 与 <see cref="VaFallThrough"/> 是**同一个地址**。
    ///
    /// <para>
    /// ★★ 2026-10 修正：这里**曾经**指向 <c>0x180a8d963</c>（<c>expand</c>），那是错的。
    /// 用 MCP 在活进程里把 stub 出口分别改成两个候选、再调 <c>MapNavigator.Navigate</c>
    /// 走同一个「相邻己方城墙格」，得到干净的三方对照：
    /// </para>
    /// <list type="table">
    ///   <item><term>出口 = <c>fallThrough</c></term><description><b>True</b>，path 长度 1 ✅</description></item>
    ///   <item><term>出口 = <c>skip</c></term><description><b>False</b>，长度 0 ❌</description></item>
    ///   <item><term>出口 = <c>expand</c>（旧实现）</term><description><b>False</b>，长度 0 ❌（真机表现：格子不亮）</description></item>
    /// </list>
    /// <para>
    /// <b>原因</b>：<c>0x180a8d963</c> 并**不是**「接受这个格子」的入口，它是接受路径的**中段**
    /// —— 位于 <c>0x180a8d8bc</c> 处那个占位判定（虚调用 <c>[r9+0x138]</c>，<c>r9=[rax]</c>）
    /// **已经返回通过之后**。从那里跳进去会绕过该判定，而且它期望
    /// <c>rsi</c>/<c>rbp</c>/<c>[rsp+0xa0]</c>/<c>[rsp+0xa8]</c> 已被前一段代码铺垫好
    /// （首条即 <c>mov rdi,[rsi+0x40]</c> = <c>tempRef</c>），对障碍格并不成立。
    /// </para>
    /// <para>
    /// 正解是走 <c>fallThrough</c>：它**重跑游戏自己的准入判定**
    /// （<c>cmp [rax+0x14],2</c> 之后紧随的虚调用 <c>[r9+0x138]</c>），
    /// 让城墙格由游戏原版规则去裁决，而不是由我们猜。实测该判定对**己方城墙返回通过**。
    /// </para>
    /// </summary>
    private const ulong VaPass = 0x180A8D8BCUL;

    /// <summary>
    /// ★ 「非障碍格」也必须送回 <see cref="VaPass"/>，这是必需的：
    /// hook 点落在 <c>je</c> 上，而 <c>je</c> 在 <c>gridType == Obstacle</c>
    /// 与 <c>!= Obstacle</c> **两种情况下都会执行**。
    /// stub 必须自己重判 <c>gridType</c>，否则会把普通格也当成障碍排除。
    /// <para>
    /// ⚠️ 初期漏了这个分支时，表现是「移动范围只剩脚下那一格」（实机复现）。
    /// </para>
    /// </summary>

    // ---- 现场实测的字段偏移（MCP 在活进程读出 + 原生内存交叉验证）----
    private const int OffGridType = 0x14;       // GridUnitData.gridType
    private const int OffObstale = 0x30;        // GridUnitData.obstale
    private const int OffObstacleType = 0x10;   // ObstacleData.obstalceType
    private const int OffObstacleTeam = 0x2C;   // ObstacleData.teamID

    /// <summary><c>GridType.Obstacle</c> —— 枚举为 <c>None=0, Normal=1, Obstacle=2</c>。</summary>
    private const int GridTypeObstacle = 2;

    /// <summary><c>ObstacleType.Wall</c> —— 枚举实测为 <c>Normal=0, Wall=1</c>。</summary>
    private const int ObstacleTypeWall = 1;

    /// <summary>
    /// <c>selfTeamID</c> 相对 <c>Navigate</c> 栈帧的偏移（即 <c>[rsp+0xd8]</c>）。
    ///
    /// <para>
    /// Dobby 是<b>跳</b>到 stub（不是 <c>call</c>）——<b><c>rsp</c> 完全不变</b>，
    /// 所以这个偏移在 stub 内直接可用，不需要调整任何帧指针。
    /// </para>
    /// <para>
    /// ⚠️ 它属于「游戏一更新就失效」的那类常量。改这里之前先重新反汇编
    /// <c>Navigate</c> 确认 <c>selfTeamID</c> 仍在同一栈槽。
    /// </para>
    /// </summary>
    private const int SelfTeamIdStackOffset = 0xD8;

    private static readonly byte[] OriginalBytes_ = { 0x0F, 0x84, 0xAF, 0x01, 0x00, 0x00 };

    /// <summary>单例：本 hook 全进程只会挂一次。</summary>
    internal static readonly WallPassHook Instance = new();

    private WallPassHook()
    {
    }

    protected override string Tag => "[城防 detour]";

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
        if (!IsRel32Jcc(current))
        {
            reason = "落点不是 6 字节 jcc rel32。";
            return false;
        }

        // 这条是 `je`（0F 84），与 FriendlyPassHook 那条 `jne`（0F 85）不同。
        // 不强行要求操作码一致（别的补丁可能改写过），但记下来便于排查。
        reason = string.Empty;
        return true;
    }

    /// <summary>
    /// 构造 detour stub —— <b>用 Iced 汇编器，不再手写机器码</b>。
    ///
    /// <para><b>入口状态</b>（Dobby 是<b>跳</b>进来，<c>rsp</c> 不变，所以 <c>[rsp+0xd8]</c> 有效）：</para>
    /// <list type="bullet">
    ///   <item><c>rax</c> = 邻格 <c>g</c>（<c>GridUnitData*</c>）—— 上一条 <c>cmp</c> 刚读过它</item>
    ///   <item><c>[rax+0x14]</c> 已确认 == 2（<c>gridType == Obstacle</c>）</item>
    ///   <item><c>[rsp+0xd8]</c> = <c>selfTeamID</c></item>
    /// </list>
    ///
    /// <para>只读 <c>rax</c> 与栈，<b>不依赖 <c>rsi</c></b>（虽然 <c>rsi</c> 此刻也等于 <c>g</c>，
    /// 但用 <c>rax</c> 更贴近这条指令的原始语义）。</para>
    ///
    /// <para>
    /// 【两级退出】<c>exitSkip</c> = 保持「障碍不可通行」的原行为；
    /// <c>exitPass</c> = 送回原 <c>je</c> 不跳时的落点，由游戏自己裁决。
    /// 出口用 <see cref="StubAssembler.ExitViaImm64"/>（<c>mov r11,imm64; jmp r11</c>）：
    /// <c>r11</c> 是 Win64 易失寄存器，两个出口后续代码均不读它（已逐条核实）。
    /// </para>
    /// </summary>
    protected override byte[] BuildStub(long stubRip)
    {
        return StubAssembler.Build(Tag, 64, stubRip, asm =>
        {
            // 两个出口（见下方注释）；在 emit 返回前必须全部绑定。
            Label exitSkip = asm.CreateLabel("skip");
            Label exitPass = asm.CreateLabel("pass");

            // ★★ 关键：hook 点在 `je` 上，**两种情况都会执行到**：
            //   ① g.gridType == Obstacle  → 原本会跳（障碍格被排除）
            //   ② g.gridType != Obstacle  → 原本不跳，落到 0x180a8d8bc 继续
            //   所以 stub **必须自己再判一次 gridType**。
            //
            //   ⚠️ 初期漏了 ② 分支，把所有普通格也跳去 skip ——
            //      后果是移动范围只剩脚下那一格（实机复现）。
            //
            // rax = 邻格 g（上一条 cmp 刚读过）；rcx/rdx 为易失寄存器，可自由使用。

            // g.gridType == Obstacle ?
            asm.cmp(__dword_ptr[rax + OffGridType], GridTypeObstacle);

            // ── 出口 ①：非障碍格 → 原样继续（不可省！）
            asm.jne(exitPass);

            // ---- 以下只在「障碍格」时执行 ----
            asm.mov(rcx, __qword_ptr[rax + OffObstale]);   // g.obstale
            asm.test(rcx, rcx);
            asm.jz(exitSkip);                              // 无障碍物数据 -> 保持原行为

            asm.cmp(__dword_ptr[rcx + OffObstacleType], ObstacleTypeWall);
            asm.jne(exitSkip);                             // 普通障碍（造景/木桶…）-> 保持原行为

            // == selfTeamID ?
            asm.mov(edx, __dword_ptr[rcx + OffObstacleTeam]);
            asm.cmp(edx, __dword_ptr[rsp + SelfTeamIdStackOffset]);
            asm.jne(exitSkip);                             // 他方城墙 -> 保持原行为

            // ---- 两个出口 ----
            //
            // ⚠️⚠️ 出口跳转**不能用 rax 做中转**（已踩坑，gdb 现场：
            //     rax=0x180a8d8bc 跳到该处后 `mov r9,[rax]` 读到代码字节当类指针，
            //     再 `[r9+0x140]` 即 SIGSEGV）。
            //
            //   原因：`pass`（0x180a8d8bc）是紧跟 `cmp` 的**原始代码**，
            //   它**依赖 `cmp` 留下的 rax = 邻格 g**。
            //
            //   两个出口后续代码均不读 r11（已核实），所以统一用 r11 中转。
            //
            // ⚠️ pass 必须紧跟检查：三条全不跳时**顺序落入** pass。
            asm.Label(ref exitPass);
            asm.ExitViaImm64(RuntimeVa(VaPass));
            asm.Label(ref exitSkip);
            asm.ExitViaImm64(RuntimeVa(VaSkip));
        });
    }
}
