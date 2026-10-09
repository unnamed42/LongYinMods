using System;
using System.Collections.Generic;
using Iced.Intel;
using static Iced.Intel.AssemblerRegisters;

namespace Unnamed42.FriendlyNoclip;

/// <summary>
/// 穿友方单位：在 <c>MapNavigator.Navigate</c> 的「存活单位判定」那条 <c>jne</c> 上装 detour，
/// 把「任何存活单位都阻挡」改成「只阻挡**敌方**单位」。
///
/// <para>
/// <b>落点、跳转目标、字段偏移、<c>selfTeamID</c> 栈槽现在全部在运行时发现</b>
/// （<see cref="NavigateLayout"/>）—— 因为游戏一更新，整个函数会搬走
/// （2026-10：<c>-0x3aa60</c>，连函数内偏移都变了），写死的地址会**静默失效**。
/// 本类只保留这个 hook 真正的知识：<b>stub 怎么写、职责边界在哪</b>。
/// </para>
///
/// <para><b>为什么需要原生层改动</b>：<c>Navigate</c> 里那条 <c>jne</c> 不分敌我 ——
/// 邻格上只要站着存活单位就跳过。所以「只穿友方」必须自己比较队伍 ID，
/// 不能把这个判断交给游戏原有的任何一处。</para>
///
/// <para><b>★★ 出口必须是「原版 <c>jne</c> 不跳时的落点」，而且必须让游戏重跑后续判定</b>：
/// 本 hook 只负责回答「**这个格子上的人是不是队友**」，
/// 而**不能**代替游戏回答「**站到那个格子上合不合法**」。
/// 那个落点正是游戏自己的「空格链」—— 它会先调
/// <c>AroundGridHaveEnemy(row, col, selfTeamID)</c>（交战区 / 敌方邻接检查）再决定是否接受。
/// </para>
///
/// <para>
/// ⚠️⚠️ 2026-10 修正：这里**曾经**跳到一个「接受路径的中段」地址
/// （当时的静态 VA <c>0x180a8d963</c>）—— 那是错的，它位于
/// <c>AroundGridHaveEnemy</c> **已经返回通过之后**，跳过去等于**跳过占位 / 交战区判定**。
/// 真机后果：**AI 会把玩家所在的格子当成可落点，直接站到玩家头上**。
/// 这跟 <see cref="WallPassHook"/> 那个 <c>expand</c> bug 是**同一类错误**。
/// </para>
///
/// <para>
/// 旧注释曾声称「把友方格交给 <c>AroundGridHaveEnemy</c> 判定，在混战中必然失败」——
/// 那个结论是在**错误的出口地址**下得到的，已被推翻。
/// </para>
///
/// <para><b>为什么用 stub 而不是托管回调</b>：落点是函数中间地址，
/// 此刻有效状态在 <c>rsi</c>（邻格 <c>g</c>）与 <c>[rsp+selfTeamID]</c>（栈槽，运行时发现），
/// 而托管 delegate 的参数只映射 Win64 参数寄存器 <c>rcx/rdx/r8/r9</c>，
/// 拿不到这两者。所以这里放一段 x64 stub，直接读原寄存器与栈。</para>
///
/// <para><b>为什么不用自己分配跳板</b>：更早的版本自己算 rel32 + <c>VirtualAlloc</c>
/// 从映像末尾向上找洞，一装上就 coredump。改用 Dobby 后，
/// 指令搬迁与跳板分配都由它负责（near allocator 保证 rel32 可达）。</para>
/// </summary>
internal sealed class FriendlyPassHook : NativeHookBase
{
    /// <summary>
    /// 本构建（2026-10-09）实测的落点字节形态：<c>0F 85</c> = <c>jne rel32</c>。
    /// 仅用于失败日志里的对照 —— 真正的判定是「运行时是不是一条 6 字节 jcc」。
    /// </summary>
    private static readonly byte[] OriginalBytes_ = { 0x0F, 0x85, 0x3C, 0x01, 0x00, 0x00 };

    /// <summary>单例：本 hook 全进程只会挂一次。</summary>
    internal static readonly FriendlyPassHook Instance = new();

    private static NavigateLayout? _layout;
    private static HookPoint? _point;

    private FriendlyPassHook()
    {
    }

    protected override string Tag => "[原生 detour]";

    protected override byte[] OriginalBytes => OriginalBytes_;

    /// <summary>
    /// 运行时发现落点：<c>Navigate</c> 里 <c>test al,al</c> 之后那条 <c>jne</c>
    /// （<c>BattleUnit.get_IsAlive</c> 的返回值判定）。
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
        _point = layout!.Friendly;
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
    /// 两个出口的预期运行时地址。安装后由 <c>NativeHookBase.VerifyExits</c> 对账。
    ///
    /// <para>
    /// 这就是「手写汇编时代只能靠人肉核对」的那件事 —— 现在：标签地址由汇编器算，
    /// 我们只声明「应该跳到哪」，不匹配就报 ERROR。
    /// </para>
    ///
    /// <para>
    /// ⚠️ 必须是**属性**，不能是 <c>static readonly</c> 字段：
    /// 静态字段初始化可能早于定位（<see cref="NavigateLayout"/> 是运行时解析的），
    /// 那时读到的是 0，于是自检会报出「出口不在声明的出口集合里」——
    /// 一条**纯粹由初始化顺序造成的假警报**（本项目第一次跑就撞上过）。
    /// </para>
    /// </summary>
    protected override IReadOnlyDictionary<string, long> ExpectedExits =>
        new Dictionary<string, long>
        {
            ["pass"] = (long)Point.Pass,
            ["skip"] = (long)Point.Skip,
        };

    protected override bool ValidateSite(byte[] current, out string reason)
    {
        // 只要求它仍是一条 6 字节 rel32 jcc。
        // 曾经在同一地址做过原地改写探针，那样原值就变了 —— 但搬迁依然安全。
        if (!IsRel32Jcc(current))
        {
            reason = "落点不是 6 字节 jcc rel32。";
            return false;
        }

        // ★ 条件**方向**必须与 stub 的语义一致：stub 假定「跳 = 存活 → 跳过」，
        //   所以这里只接受 `jne`。
        if (current[1] != OriginalBytes_[1])
        {
            reason = $"落点条件方向不对：期望 0F {OriginalBytes_[1]:x2}（jne），实际 0F {current[1]:x2}。";
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
    ///   <item><c>[rsp + selfTeamID 槽]</c> = <c>selfTeamID</c>（相对 <c>Navigate</c> 的栈帧）</item>
    /// </list>
    /// <para>
    /// 关键：Dobby 是<b>跳</b>到 stub（不是 <c>call</c>），所以 <c>rsp</c> 不变，
    /// 栈槽偏移在 stub 内直接可用。
    /// </para>
    ///
    /// <para>
    /// <b>★★ 只有两个出口，而且「队友格」与「空格」合流：</b>
    /// 本 hook 的职责边界是「把阻挡判据从『有存活单位』改成『有敌方单位』」，
    /// <b>不碰</b>「落点合不合法」。所以只要不是敌方，就一律送回
    /// <c>jne</c> 不跳时的落点（空格链，会跑 <c>AroundGridHaveEnemy</c>），
    /// 而不是直接接受。
    /// </para>
    ///
    /// <para>
    /// 出口用 <see cref="StubAssembler.ExitViaImm64"/>（<c>mov r11,imm64; jmp r11</c>），
    /// 不依赖 stub 与目标之间的 rel32 距离。
    /// （不用 <c>rax</c>：落点之后的原代码可能依赖上一条指令在
    /// <c>rax</c> 里留下的值 —— 本项目在 <see cref="WallPassHook"/> 上踩过这个坑。）
    /// </para>
    /// </summary>
    protected override byte[] BuildStub(long stubRip)
    {
        // stub 的构码逻辑抽在 StubFactory 里（纯函数，可用真实 DLL 离线核对字节）。
        // 这里只把「运行时发现」的两样东西交出去：字段/栈槽偏移 与 两个出口地址。
        HookPoint point = Point;
        NavigateLayout layout = Layout;

        return StubFactory.BuildFriendly(
            Tag,
            stubRip,
            layout.OffBattleUnit,
            layout.OffBattleTeam,
            layout.OffTeamId,
            layout.SelfTeamIdStackOffset,
            skip: (long)point.Skip,
            pass: (long)point.Pass);
    }
}
