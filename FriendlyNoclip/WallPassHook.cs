using System;
using System.Collections.Generic;

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

    private static readonly byte[] OriginalBytes_ = { 0x0F, 0x84, 0xAF, 0x01, 0x00, 0x00 };

    /// <summary>单例：本 hook 全进程只会挂一次。</summary>
    internal static readonly WallPassHook Instance = new();

    private WallPassHook()
    {
    }

    protected override string Tag => "[城防 detour]";

    protected override ulong HookVa => VaHookSite;

    protected override byte[] OriginalBytes => OriginalBytes_;

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
    /// 构造 detour stub。
    ///
    /// <para>入口状态（Dobby 是**跳**进来，<c>rsp</c> 不变，所以 <c>[rsp+0xd8]</c> 有效）：</para>
    /// <list type="bullet">
    ///   <item><c>rax</c> = 邻格 <c>g</c>（<c>GridUnitData*</c>）—— 上一条 <c>cmp</c> 刚读过它</item>
    ///   <item><c>[rax+0x14]</c> 已确认 == 2（<c>gridType == Obstacle</c>）</item>
    ///   <item><c>[rsp+0xd8]</c> = <c>selfTeamID</c></item>
    /// </list>
    ///
    /// <para>只读 <c>rax</c> 与栈，**不依赖 <c>rsi</c>**（虽然 <c>rsi</c> 此刻也等于 <c>g</c>，
    /// 但用 <c>rax</c> 更贴近这条指令的原始语义）。</para>
    /// </summary>
    protected override byte[] BuildStub()
    {
        long skip = RuntimeVa(VaSkip);
        long pass = RuntimeVa(VaPass);

        var stub = new List<byte>(220);

        // ★ 探针：必须在任何条件判断之前 —— 这样无论走哪条分支都能记到。
        //   只读栈 + 写环形缓冲，不改语义。hookId=2 表示穿城墙。
        NativeProbeLog.EmitRecordEntry(stub, hookId: 2);
        // ★★ 关键：hook 点在 `je` 上，**两种情况都会执行到**：
        //   ① g.gridType == Obstacle  → 原本会跳（障碍格被排除）
        //   ② g.gridType != Obstacle  → 原本不跳，落到 0x180a8d8bc 继续
        //   所以 stub **必须自己再判一次 gridType**。
        //
        //   ⚠️ 初期漏了 ② 分支，把所有普通格也跳去 skip ——
        //      后果是移动范围只剩脚下那一格（实机复现）。这是本类 bug 的根源，
        //      所以下面所有 rel8 偏移都从**实际发射位置**回读校验，不再手算。
        //
        // rax = 邻格 g（上一条 cmp 刚读过）；rcx/rdx 为易失寄存器，可自由使用。

        // +0x00  cmp dword [rax+0x14], 2   ; g.gridType == Obstacle ?
        stub.AddRange(new byte[] { 0x83, 0x78, (byte)OffGridType, (byte)GridTypeObstacle });

        // +0x04  jne <pass>               ; 非障碍格 -> 原样继续（不可省略！）
        AddRel8(stub, 0x75, out int jNotObstacle);
        // ---- 以下只在「障碍格」时执行 ----

        // +0x06  mov rcx, [rax+0x30]       ; g.obstale
        stub.AddRange(new byte[] { 0x48, 0x8B, 0x48, (byte)OffObstale });

        // +0x0a  test rcx, rcx
        stub.AddRange(new byte[] { 0x48, 0x85, 0xC9 });

        // +0x0d  jz <skip>                 ; 无障碍物数据 -> 保持原行为
        AddRel8(stub, 0x74, out int jNoObstale);

        // +0x0f  cmp dword [rcx+0x10], 1   ; obstalceType == Wall ?
        stub.AddRange(new byte[] { 0x83, 0x79, (byte)OffObstacleType, (byte)ObstacleTypeWall });

        // +0x13  jne <skip>                ; 普通障碍（造景/木桶…）-> 保持原行为
        AddRel8(stub, 0x75, out int jNotWall);

        // +0x15  mov edx, [rcx+0x2c]       ; obstale.teamID
        stub.AddRange(new byte[] { 0x8B, 0x51, (byte)OffObstacleTeam });

        // +0x18  cmp edx, [rsp+0xd8]       ; == selfTeamID ?
        // +0x1f  jne <skip>                ; 他方城墙 -> 保持原行为
        stub.AddRange(new byte[] { 0x3B, 0x94, 0x24, 0xD8, 0x00, 0x00, 0x00 });
        AddRel8(stub, 0x75, out int jOtherTeam);

        // ---- 两个出口 ----
        //
        // ⚠️⚠️ 出口跳转**不能用 rax 做中转**（已踩坑，gdb 现场：
        //     rax=0x180a8d8bc 跳到该处后 `mov r9,[rax]` 读到代码字节当类指针，
        //     再 `[r9+0x140]` 即 SIGSEGV）。
        //
        //   原因：`pass`（0x180a8d8bc）是紧跟 `cmp` 的**原始代码**，
        //   它**依赖 `cmp` 留下的 rax = 邻格 g**。
        //   而 `mov rax,imm64; jmp rax` 会把 rax 改成代码地址。
        //
        //   两个出口后续代码均不读 r11（已核实），所以统一用 r11 中转：
        //     r11 是 Win64 易失寄存器。
        //
        // ⚠️ pass 必须紧跟检查：三条全不跳时**顺序落入** pass。
        int passOff = stub.Count;
        EmitJumpViaR11(stub, pass);

        int skipOff = stub.Count;
        EmitJumpViaR11(stub, skip);
        byte[] code = stub.ToArray();

        // 回填 rel8（相对**下一条指令**）。
        // 这里不再写死 code[5]/[14]… 而是从发射时记下的偏移直接定位，
        // 并在下方做一次自检，避免再次出现「偏移与实际布局脱节」。
        PatchRel8(code, jNotObstacle, passOff);
        PatchRel8(code, jNoObstale, skipOff);
        PatchRel8(code, jNotWall, skipOff);
        PatchRel8(code, jOtherTeam, skipOff);

        VerifyStub(code, passOff, skipOff, jNotObstacle, jNoObstale, jNotWall, jOtherTeam);

        return code;
    }

    /// <summary>发射一条 rel8 条件跳转，并返回其 rel8 操作数在缓冲区中的偏移。</summary>
    private static void AddRel8(List<byte> stub, byte opcode, out int rel8Offset)
    {
        stub.Add(opcode);
        rel8Offset = stub.Count;
        stub.Add(0);   // 占位，稍后回填
    }

    /// <summary>把 offset 处的 rel8 回填为跳向 <paramref name="targetOff"/>。</summary>
    private static void PatchRel8(byte[] code, int rel8Offset, int targetOff)
    {
        // rel8 是相对**下一条指令**的位移：下一条指令地址 = rel8Offset + 1
        code[rel8Offset] = (byte)(targetOff - (rel8Offset + 1));
    }

    /// <summary>
    /// 自检：反查每条条件跳转的目的地是否与预期出口一致。
    ///
    /// <para>
    /// 加这一步是因为本项目在此处真实踩过坑 —— 漏掉「非障碍格原样继续」分支时，
    /// 普通格全被跳过，表现为「移动范围只剩脚下那一格」，而静态看代码很难发现。
    /// 有这个自检，算错偏移会在安装时就去日志报警，而不是变成玄学现象。
    /// </para>
    ///
    /// <para>
    /// 这里**不写死偏移**，而是把 <see cref="BuildStub"/> 里记下的 rel8 操作数偏移
    /// 原样传进来。以前那版把偏移写死为 5/14/20/32，一旦指令布局变动就会
    /// 「自检报的错”与“实际错”脱节」，反而误导排查。
    /// </para>
    /// </summary>
    private void VerifyStub(
        byte[] code, int passOff, int skipOff,
        int jNotObstacle, int jNoObstale, int jNotWall, int jOtherTeam)
    {
        var checks = new System.Collections.Generic.List<(int at, int expect, string name)>
        {
            (jNotObstacle, passOff, "非障碍格 -> 原样继续（pass）"),
            (jNoObstale, skipOff, "obstale == null -> skip"),
            (jNotWall, skipOff, "非城墙 -> skip"),
            (jOtherTeam, skipOff, "他方城墙 -> skip"),
        };

        foreach (var (at, expect, name) in checks)
        {
            if (at < 0 || at >= code.Length)
            {
                Plugin.Log.Error($"{Tag} stub 自检失败：{name} 的 rel8 偏移 {at} 越界（长度 {code.Length}）。");
                continue;
            }

            int dest = at + 1 + (sbyte)code[at];

            if (dest != expect)
            {
                Plugin.Log.Error(
                    $"{Tag} stub 自检失败：{name} 的跳转目标算出 +{dest:x}，期望 +{expect:x}。" +
                    "参数判定会错乱，请检查 BuildStub 的指令布局。");
            }
        }

        // 两个出口都必须落在 stub 内部且互不相同。
        if (passOff == skipOff)
        {
            Plugin.Log.Error($"{Tag} stub 自检失败：出口重叠（pass={passOff} skip={skipOff}）。");
        }

        if (passOff >= code.Length || skipOff >= code.Length)
        {
            Plugin.Log.Error($"{Tag} stub 自检失败：出口越界（pass={passOff} skip={skipOff} 长度={code.Length}）。");
        }
    }
}
