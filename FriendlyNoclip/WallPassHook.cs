using System;
using System.Collections.Generic;
using Iced.Intel;
using static Iced.Intel.AssemblerRegisters;

namespace Unnamed42.FriendlyNoclip;

/// <summary>
/// 穿己方城墙：在 <c>MapNavigator.Navigate</c> 的「障碍格判定」那条 <c>je</c> 上装 detour，
/// 让**属于自己队伍**的城墙格可通行。
///
/// <para>
/// <b>落点、跳转目标、字段偏移、<c>selfTeamID</c> 栈槽全部在运行时发现</b>
/// （<see cref="NavigateLayout"/>），不再写死地址 —— 游戏一更新，那个函数整体会搬走。
/// </para>
///
/// <para><b>与 <see cref="FriendlyPassHook"/> 的关系</b>：这是**独立的第二个 hook 点**。
/// 城墙比「存活单位」判定**更早**被排除，所以穿友方那套逻辑根本碰不到城墙 ——
/// 实测证据：装了穿友方之后，NPC 能穿、城墙依然不能穿（敌我双方都不能）。</para>
///
/// <para><b>原始行为</b>（形态已逐条反汇编核实）：</para>
/// <code>
/// ...       call GetGridDataByDir          ; 邻格 g (rax/rsi)
/// ...       mov  rsi, rax
/// ...       test rax, rax
/// ...       je   &lt;continue&gt;               ; g == null -> skip
/// 0F 84 ... cmp  dword [rax+0x14], 2      ; g.gridType == Obstacle ?
/// ★ 本 hook 点 je  &lt;continue&gt;               ; 障碍格一律 skip
/// ...       mov  r9, [rax]                ; 非障碍格：继续原逻辑（fall-through）
/// </code>
///
/// <para><b>为什么 <c>gridType == Obstacle</c> 不够</b>：该分类同时包含城墙与
/// **中立障碍**（造景 / 雕像 / 木箱 / 木桶 / 灌木）。游戏原本对二者一视同仁地阻挡，
/// 所以那一行不读 <c>teamID</c> —— 这也解释了「敌方 AI 同样穿不过城墙」。
/// 因此判据必须下沉到 <c>ObstacleData</c> 的 <c>obstalceType</c>，只放行城墙。</para>
///
/// <para><b>字段偏移</b>（运行时从 IL2CPP 元数据读；见 <see cref="NavigateLayout"/>）：</para>
/// <list type="table">
///   <item><term>GridUnitData.gridType</term><description>+0x14（int：None=0, Normal=1, Obstacle=2）</description></item>
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
    /// 本构建（2026-10-09）实测的落点字节形态：<c>0F 84</c> = <c>je rel32</c>。
    /// 仅用于失败日志对照与「条件方向」自检 —— 地址本身是运行时发现的。
    /// </summary>
    private static readonly byte[] OriginalBytes_ = { 0x0F, 0x84, 0xAF, 0x01, 0x00, 0x00 };

    // ---- stub 用的枚举值（游戏里的字面量，与地址无关，不随更新漂移）----

    /// <summary><c>GridType.Obstacle</c> —— 枚举为 <c>None=0, Normal=1, Obstacle=2</c>。</summary>
    private const int GridTypeObstacle = 2;

    /// <summary><c>ObstacleType.Wall</c> —— 枚举实测为 <c>Normal=0, Wall=1</c>。</summary>
    private const int ObstacleTypeWall = 1;

    /// <summary>单例：本 hook 全进程只会挂一次。</summary>
    internal static readonly WallPassHook Instance = new();

    private static NavigateLayout? _layout;
    private static HookPoint? _point;

    private WallPassHook()
    {
    }

    protected override string Tag => "[城防 detour]";

    protected override byte[] OriginalBytes => OriginalBytes_;

    /// <summary>
    /// 运行时发现落点：<c>Navigate</c> 里 <c>cmp dword [rax+0x14],2</c> 之后那条 <c>je</c>
    /// （障碍格一律跳过）。
    /// </summary>
    protected override bool TryResolveSite(out IntPtr site, out string describe, out string reason)
    {
        site = IntPtr.Zero;
        describe = string.Empty;

        if (!NavigateLayout.TryGet(out NavigateLayout? layout, out reason))
        {
            return false;
        }

        _layout = layout;
        _point = layout!.Wall;
        site = (IntPtr)(long)_point.Site;
        describe = _point.Describe();
        return true;
    }

    private static HookPoint Point =>
        _point ?? throw new InvalidOperationException(
            "落点尚未解析 —— BuildStub/ExpectedExits 只应在 TryResolveSite 成功之后被调用。");

    private static NavigateLayout Layout =>
        _layout ?? throw new InvalidOperationException(
            "定位结果尚未解析 —— BuildStub/ExpectedExits 只应在 TryResolveSite 成功之后被调用。");

    /// <summary>
    /// 两个出口的预期运行时地址。安装后由 <c>NativeHookBase.VerifyExits</c> 对账
    /// （必须是**属性**，理由见 <see cref="FriendlyPassHook.ExpectedExits"/>）。
    /// </summary>
    protected override IReadOnlyDictionary<string, long> ExpectedExits =>
        new Dictionary<string, long>
        {
            ["pass"] = (long)Point.Pass,
            ["skip"] = (long)Point.Skip,
        };

    protected override bool ValidateSite(byte[] current, out string reason)
    {
        if (!IsRel32Jcc(current))
        {
            reason = "落点不是 6 字节 jcc rel32。";
            return false;
        }

        // ★ 条件**方向**必须与 stub 的语义一致：stub 假定「跳 = 障碍格 → 跳过」，
        //   所以这里只接受 `je`。若某天官方把它反写成 `jne`，
        //   落点仍能匹配（形态扫描已排除），但这里会立刻拒绝，而不是静默改错语义。
        if (current[1] != OriginalBytes_[1])
        {
            reason = $"落点条件方向不对：期望 0F {OriginalBytes_[1]:x2}（je），实际 0F {current[1]:x2}。";
            return false;
        }

        reason = string.Empty;
        return true;
    }

    /// <summary>
    /// 构造 detour stub —— <b>用 Iced 汇编器，不再手写机器码</b>。
    ///
    /// <para><b>入口状态</b>（Dobby 是<b>跳</b>进来，<c>rsp</c> 不变，所以 selfTeamID 栈槽有效）：</para>
    /// <list type="bullet">
    ///   <item><c>rax</c> = 邻格 <c>g</c>（<c>GridUnitData*</c>）—— 上一条 <c>cmp</c> 刚读过它</item>
    ///   <item><c>[rax+gridType]</c> 已确认 == 2（<c>gridType == Obstacle</c>）</item>
    ///   <item><c>[rsp + selfTeamID 槽]</c> = <c>selfTeamID</c></item>
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
        HookPoint point = Point;
        NavigateLayout layout = Layout;

        // stub 的构码逻辑抽在 StubFactory 里（纯函数，可用真实 DLL 离线核对字节）。
        // 这里只把「运行时发现」的两样东西交出去：字段/栈槽偏移 与 两个出口地址。
        return StubFactory.BuildWall(
            Tag,
            stubRip,
            layout.OffGridType,
            layout.OffObstale,
            layout.OffObstacleType,
            layout.OffObstacleTeam,
            layout.SelfTeamIdStackOffset,
            gridTypeObstacle: GridTypeObstacle,
            obstacleTypeWall: ObstacleTypeWall,
            skip: (long)point.Skip,
            pass: (long)point.Pass);
    }
}
