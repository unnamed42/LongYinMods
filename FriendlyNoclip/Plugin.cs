using System;
using System.Collections.Generic;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Threading;
using System.Text;
using HarmonyLib;
using Il2Cpp;
using Il2CppInterop.Runtime;
using MelonLoader;
using UnityEngine;

namespace Unnamed42.FriendlyNoclip;

/// <summary>
/// 战斗格子地图：移动时允许穿越友方单位。
///
/// 【当前阶段：运行时只读探针】
/// 真实寻路链路（已用 IL2CPP dumper 交叉验证）：
/// <code>
/// BattleMapData.GetMoveRangeGrids (0x1808c8b60) --+
/// BattleMapData.GetEmptyGrid      (0x1808c88a0) --+--> MapNavigator.Navigate (0x180a8d5a0)
/// </code>
/// 两个 <c>BattleMapData</c> 方法都只是薄壳，真正的过滤在 <c>MapNavigator.Navigate</c>（A*）里：
/// <list type="bullet">
///   <item>方向可行性由 <c>bt g.passes, dir</c>（<c>0x180a8d875</c>）决定，
///         <c>passes</c> 是 <c>GridUnitData+0x20</c> 的 4 位掩码。</item>
///   <item><c>AroundGridHaveEnemy</c>（<c>0x180a8d956</c>）只看敌方，天然不拦友方。</item>
///   <item>另有一处未定性的 <c>call [klass+0x138]</c>（<c>0x180a8d8d1</c>）。</item>
/// </list>
/// 【已排除】
/// <list type="bullet">
///   <item><c>passes</c>：实测 <c>Obstacle</c> 格为 0、<c>Normal</c> 格恒为 15
///         （无论空还是站着我方单位），故它是**地形**通行表，与占位无关。</item>
///   <item><c>+0xd0</c>/<c>+0x118</c>：位于不可达代码（旧的错误地址），已作废。</item>
/// </list>
/// 【当前目标】行为实验：把 <c>Navigate</c> 内 <c>0x180a8d8da</c> 的 <c>jne</c>
/// 强行 NOP 掉（等价于「无论占位判定结果如何都放行该方向」），看能否走出友方包围。
/// 这是运行时内存补丁，按 <c>F9</c> 开关，重启游戏即恢复。
/// </summary>
public class Plugin : MelonMod
{
    internal const string HarmonyId = "com.unnamed42.friendlynoclip";

    private const string CategoryId = "FriendlyNoclip";
    private const string CategoryDisplay = "Friendly Noclip";

    internal static MelonPreferences_Category Category = null!;


    /// <summary>
    /// 最近一次 <c>GetMoveRangeGrids</c> 调用时的地图实例。
    /// 邻格查询要靠它（<c>GetGridDataByDir</c> 是实例方法）。
    /// </summary>
    private static BattleMapData? _activeMap;

    /// <summary>
    /// 诊断日志总开关，**控制全部信息性日志**。
    ///
    /// <para>
    /// 【为什么全部走它】本 mod 的钩子挂在**高频路径**上（逐格移动、每帧渲染类型），
    /// 而游戏**战斗很频繁** —— 不控制量级会让 Latest.log 迅速膨胀。
    /// （实测一局就能产出 85 KB、其中 [穿越] 261 行、[命中] 120 行。）
    /// </para>
    ///
    /// <para>
    /// 关闭时**所有** <see cref="LogInfo"/> 静默（连字符串都不拼接）。
    /// 但**真正的错误不受它控制** —— 见 <see cref="LogError"/>。
    /// </para>
    /// <para>
    /// 它读的是 <c>.Value</c>，所以改 cfg 后**立即生效**，不需要重启。
    /// </para>
    /// </summary>
    internal static MelonPreferences_Entry<bool> Diagnostics = null!;

    /// <summary>每个移动范围计算都 dump 全部网格（日志量大）。</summary>
    internal static MelonPreferences_Entry<bool> DumpAllGrids = null!;

    /// <summary>原生 detour：只让**敌方**阻挡，友方可穿过。</summary>
    internal static MelonPreferences_Entry<bool> NativeDetourEnabled = null!;

    /// <summary>原生 detour：允许穿越**己方城墙**。</summary>
    internal static MelonPreferences_Entry<bool> WallPassEnabled = null!;



    private HarmonyLib.Harmony? _harmony;

    internal static MelonLogger.Instance Log = null!;

    // ---- 日志门控（诊断开关，见 Diagnostics 条目）----
    //
    // 【为什么要集中门控】本 mod 的钩子挂在**高频路径**上（逐格移动、每帧渲染类型）：
    //
    //     GridUnitData.OnLeave / BattleUnit.EnterGrid / set_GridRenderType
    //
    // 一局战斗就能产生上千行，而游戏**战斗很频繁** —— 不控制量级会让
    // Latest.log 迅速膨胀到难以阅读。
    //
    // 【策略：两类分开】
    //   · Info  —— 一切**信息性**输出（命中计数、拦截记录、审计、dump）。
    //     全部受 Diagnostics 门控。
    //   · Error —— 真正的**错误**（异常、写失败、安装失败）。
    //     **永不门控** —— 出问题时必须看得见，否则排查时会被误导。
    //
    // 把「判门控」这件事收在这里，而不是让每个调用点各自 if ——
    // 免得新增日志时漏掉（这正是本次要修的问题）。

    /// <summary>
    /// 写一条**信息性**日志，受 <see cref="Diagnostics"/> 门控。
    /// 未开诊断时**完全静默**（不拼接字符串、不分配）。
    /// </summary>
    /// <param name="message">惰性求值：未开诊断时不会执行。</param>
    internal static void LogInfo(Func<string> message)
    {
        if (!Diagnostics.Value)
        {
            return;
        }

        try
        {
            Log.Msg(message());
        }
        catch
        {
            // 日志本身出错绝不能影响游戏逻辑。
        }
    }

    /// <summary>
    /// 写一条**错误**日志，**永不受门控**。
    ///
    /// <para>
    /// 只用于「出错了、用户需要知道」的场景。
    /// 正常但值得记录的流程（如「拦下一面墙」）属于信息，请用 <see cref="LogInfo"/>。
    /// </para>
    /// </summary>
    internal static void LogError(string message) => Log.Warning(message);

    /// <summary>OnLeave Prefix 命中次数（不受诊断开关门控）。</summary>
    private static int _onLeaveHits;

    /// <summary>EnterGrid Prefix 命中次数（不受诊断开关门控）。</summary>
    private static int _enterGridHits;
    public override void OnInitializeMelon()
    {
        Log = LoggerInstance;

        Category = MelonPreferences.CreateCategory(CategoryId, CategoryDisplay);

        // ★ 面向用户只有两个功能开关（穿友方 / 穿城墙）+ 两个调试开关。
        //
        //   曾经有 9 个，其中 4 个是「配套实现」：
        //     fix_occupancy / wall_pass_hook / wall_no_stop / wall_highlight_hook
        //   前三个已合并进各自的功能开关，最后一个已删除。
        //   理由见下方各自的注册处，以及 docs/friendlynoclip.md §2.1。

        Diagnostics = Category.CreateEntry(
            "diagnostics", false,
            "诊断日志",
            "总开关：控制全部信息性日志（命中计数、拦截记录、审计、dump）。" +
            "游戏战斗频繁，不控制量级会让日志迅速膨胀。关闭时只保留真正的错误。");

        DumpAllGrids = Category.CreateEntry(
            "dump_all_grids", false,
            "Dump 全部网格", "每次调用都输出整张地图的网格明细（极慢，排查时才开）。");

        // 原生 detour：真正的实现。用 Dobby 在 Navigate 内部装 detour，
        // 只把**敌方**单位当阻挡，友方可以穿过。
        NativeDetourEnabled = Category.CreateEntry(
            "native_detour", true,
            "穿越友方单位",
            "友方单位所在格可以路过（敌方仍然阻挡）。" +
            "包含配套的「穿越不留痕」修复 —— 不让被穿过的单位失去反应、" +
            "也不让同一个格子站上两个人。关闭后穿不过友方。");

        // 原生 detour：城防。独立 hook 点 —— 城墙在「存活单位」判定之前就被排除了。
        // ★ 城墙功能是**一个整体**，只留这一个开关。
        //
        //   合并进来的配套项（曾经各有独立开关，现已内置）：
        //     · wall_pass_hook   —— 已被 passes 方案覆盖，关掉不影响功能
        //     · wall_no_stop     —— 「不可停留」是「可跨越」的必要配套
        //       （没有它，AI 会站到城墙上，见 §2.2d）
        //
        //   为什么合并：配套功能**不应该能单独生效**。分开时用户可以配出
        //   「能穿但能站上去」这种**坏状态**（正是用户实测到的 AI 站墙）。
        //   收成一个开关后，这种错误配置在结构上就不存在了。
        WallPassEnabled = Category.CreateEntry(
            "wall_pass", true,
            "穿越己方城墙",
            "允许跨越属于自己队伍的城墙（可跨越、不可停留）。" +
            "守方 AI 自动获得同样能力；中立障碍与他方城墙不受影响。" +
            "关闭后穿不过己方城墙。");

        _harmony = new HarmonyLib.Harmony(HarmonyId);

        // 两个原生 detour，各自独立开关。都在 MapNavigator.Navigate 内，但 hook 点不同：
        //   穿友方 —— 0x180a8d929（存活单位判定）
        //   穿己墙 —— 0x180a8d8b6（障碍格判定）—— 城墙比单位判定更早被排除
        if (NativeDetourEnabled.Value)
        {
            FriendlyPassHook.Instance.Install();
        }

        // ★★ 城防：`wall_pass` **一个开关管全部**（跨越 + 禁停 + 必要的 detour）。
        //
        //   合并进来的配套项（曾经各有独立开关，见 docs/friendlynoclip.md）：
        //     · wall_pass_hook   —— 已被 passes 方案覆盖，关掉不影响功能
        //     · wall_no_stop     —— 「不可停留」是「可跨越」的必要配套：
        //         没有它，passes 会把城墙变成「完全可通行」，AI 会站上去（§2.2d）
        //     · wall_highlight_hook —— 经验证会崩溃且非功能所需，**已删除**
        //
        //   为什么合并成一个：配套功能**不应该能单独生效**。分开时用户可以
        //   配出「能穿但能站上去」这种坏状态 —— 那正是用户实测到的 AI 站墙。
        if (WallPassEnabled.Value)
        {
            // 原生 detour：改 Navigate 里的障碍格判定。
            WallPassHook.Instance.Install();
        }

        int patched = 0;
        patched += TryPatch(
            nameof(BattleMapData), "GetMoveRangeGrids",
            nameof(BattleMapData_GetMoveRangeGrids_Postfix));

        // 渲染探针：GridUnitData.set_GridRenderType 是"点亮某格"的唯一出口。
        // 记下每次被点亮的格子及其占用状态，直接回答
        // 「亮的格子里有没有站着人的」——不依赖任何对 +0x1d8 的推理。
        patched += TryPatch(
            nameof(GridUnitController), "set_GridRenderType",
            nameof(GridUnitController_set_GridRenderType_Prefix));

        // 落点判定探针：BattleGridClicked 是"点击某格"的入口，state 7 分支里
        // 用 moveRangeGridUnits.Contains(被点格) 决定是否写 moveTargetGrid。
        // 这里在点击入口记录：被点格的占用情况 + 它是否在移动范围里。
        // 目的：验证「能走上去」是否严格等于「Contains 为真」。
        patched += TryPatch(
            nameof(BattleController), "BattleGridClicked",
            nameof(BattleController_BattleGridClicked_Prefix));

        // ★ 登记写者取证：OnLeave 是 battleUnit 的两个写者之一。
        // 挂 Prefix 是为了在字段被改之前读到"改之前是谁"。
        //
        // 注意：OnLeave 由 BattleUnit.LeaveGrid / LeaveBattleField 调用，
        // 那两个方法在 native 侧……但 LeaveGrid/LeaveBattleField 本身是**托管可调用**的
        // （会走 DMD 包装），所以这个补丁能触发。OnEnter 则是纯 native 调用路径，
        // 必须用 native detour（见下），不能靠 Harmony。
        patched += TryPatchPrefix(
            nameof(GridUnitData), "OnLeave",
            nameof(GridUnitData_OnLeave_Prefix),
            parameterCount: 0);

        // ★★ 「穿越不留痕」的真正修复点：OnLeave 的 **Postfix**。
        // 必须在 Postfix 而非 Prefix —— OnLeave 原函数第一条指令就把 [grid+0x18] 清 0，
        // Prefix 里的写回会被它立刻抹掉（实测日志 15:52:42 已完整演示）。
        patched += TryPatch(
            nameof(GridUnitData), "OnLeave",
            nameof(GridUnitData_OnLeave_Postfix),
            parameterCount: 0);

        // ★★ 「穿越不留痕」的探测：EnterGrid 是 BattleUnit 的**唯一**移动入口。
        //
        // 已核实（objdump，`0x1808d12d0`）的 6 个调用点全部在托管方法/状态机里：
        //   RegretMove(+0x68) / EnterBattleField(+0x385, +0x5b1) /
        //   <HeroEnterGridDelay>d__253.MoveNext(+0xaf) /
        //   <MoveFromTarget>d__90.MoveNext(+0x47c) /
        //   <PlayBattleUnitMove>d__273.MoveNext(+0x2f9)
        // 全部经托管路径 → 按本项目规律（经托管代理的补丁能触发）应当能挂上。
        //
        // 为什么是 EnterGrid 而不是 OnEnter：EnterGrid(unit, targetGrid, ...) 的
        // 两个参数就足以判定穿越 —— 只要 `targetGrid.battleUnit` 非空且 != unit，
        // 那就是要覆盖一个合法的原主。而 OnEnter(grid, unit) 缺「谁在移动」的上下文，
        // 才被迫引入旁表（而旁表需要跟踪战斗开始/结束/死亡/撤销，复杂度不划算）。
        patched += TryPatchPrefix(
            nameof(BattleUnit), "EnterGrid",
            nameof(BattleUnit_EnterGrid_Prefix),
            parameterCount: 3);
        // ★★ 「穿越不留痕」的实现已移到 BattleUnit.EnterGrid 的 Prefix/Postfix
        //   与 GridUnitData.OnLeave 的 Postfix（纯托管层），
        //   不再需要任何 native detour —— 详见下方 TryPatch 调用处的注释。

        // ★★ 城墙「可跨越」：在障碍物建好后改写 GridUnitData.passes。
        //
        // ⚠️⚠️ 钩点必须是 **GenerateMapObjs**，不能是 Generate。
        //   实测日志：[城墙通行] 已放行 **0** 面己方城墙 —— 因为
        //   battleMapData.Generate() 只做**布局**，障碍物还没建，
        //   那时 obstacleGrids 是空的。真实的创建链是：
        //
        //     BattleController.PrepareBattleMap
        //       ├─ BattleMapData.Generate()        @0x180816368  ← 曾经钩在这里（太早）
        //       └─ BattleMapData.GenerateMapObjs() @0x18081683f  ← 现在钩这里 ✅
        //            └─ GenerateBuildingObstacle
        //                 ├─ GenerateWallData      ← 城墙在这里诞生
        //                 └─ GenerateObstacleData
        //
        //   两者都只有 1 个直接调用者（均在 PrepareBattleMap 内），
        //   所以钩哪个都是一对一；区别只在**时机**。
        //
        // 为什么不用 PrepareBattleMap：它有 11 个重载、CallerCount 分散
        //   （73/189/58/…），不是单一入口，容易挂错重载。
        if (WallPassEnabled.Value)
        {
            patched += TryPatch(
                nameof(BattleMapData), "GenerateMapObjs",
                nameof(BattleMapData_GenerateMapObjs_Postfix));

            // 战斗结束恢复原值（passes 的写入者未定位，采用可回滚策略）。
            patched += TryPatch(
                nameof(BattleController), "BattleRealEnd",
                nameof(BattleController_BattleRealEnd_Postfix),
                parameterCount: 0);
        }

        // ★★ 城墙「不可停留」：拦下把城墙格当作**落点**的移动请求。
        //
        // 【为什么还需要这一步 —— 实机问题】
        //   WallPassData 把己方城墙的 passes 写成 15 后，城墙从
        //   「完全不可通行」变成了「**完全可通行**」：
        //     ✅ 可从上面跨到对面（要的）
        //     ❌ **也能停在上面**（不要的）—— 实机看到 AI 站在城墙上。
        //
        //   根因：passes 是**类别标志**，一刀切。它同时驱动
        //   「能不能过」（Navigate 的搜索上限 = row × passes）
        //   与「能不能停」（GetMoveRangeGrids 的可达性），无法只改一半。
        //
        //   而且 GetMoveRangeGrids 的 gridType != 2 过滤只挡得住**扩散进来的**
        //   障碍格；**中心格自身永远进入范围**（实测：以城墙为中心算范围时，
        //   它自己会出现在结果里）。所以一旦 AI 选到它就停不下来。
        //
        // 【为什么**不**钩 GenerateMovePath —— 一次真实的错判】
        //   最初把城墙禁停挂在 BattleController.GenerateMovePath 上，理由是
        //   「它是点击与 AI 的交汇点」。**这个前提是错的**：
        //
        //     GenerateMovePath 的调用者只有 2 处，全在 UI / 点击路径；
        //     AI 走的是 PlayBattleUnitMove → MoveFromTarget → 逐格 EnterGrid，
        //     根本不经过 GenerateMovePath。
        //
        //   实测后果：玩家点击被拦住，但 **AI 仍然站到了城墙上**。
        //
        //   现在改钩 BattleUnit.EnterGrid（见上方 TryPatchPrefix），
        //   它是单位占据格子的唯一汇聚点（6 个调用者覆盖入场/AI移动/点击/撤销）。

        // 版本号：回答「现在跑的是哪个产物」。
        //
        // 本项目因为「跑的是旧产物」白耗过整整两轮，而症状是「代码不生效」——
        // 与真正的逻辑 bug 无法区分（见 AGENTS.md §3.2）。
        //
        // ★ 判断「跑的是不是新产物」靠**部署后核对 md5**，不靠日志里的时间戳：
        //   <Deterministic> 已生效，同一份源码两次构建逐字节相同，
        //   md5 是可靠的。所以这里只打静态版本号（不含时间）。
        var asm = System.Reflection.Assembly.GetExecutingAssembly();
        string buildInfo = asm.GetName().Version?.ToString() ?? "?";

        if (!string.IsNullOrEmpty(asm.Location))
        {
            // 多打一行 md5，部署时可以同这条日志直接对账（不必再去翻文件）。
            buildInfo += $" md5={AssemblyVersionHash.OfFile(asm.Location)}";
        }

        LoggerInstance.Msg(
            $"FriendlyNoclip 初始化完成：挂载 {patched} 个补丁" +
            $"，穿友方 detour={(FriendlyPassHook.Instance.Installed ? "已启用" : "未启用")}" +
            $"，穿己墙 detour={(WallPassHook.Instance.Installed ? "已启用" : "未启用")}" +

            $"，城门放行钩子 {(WallPassEnabled.Value ? "已注册" : "未注册")}。" +
            $"【构建 {buildInfo}】");
    }


    public override void OnDeinitializeMelon()
    {
        // 先还原原生改写，再摘 Harmony 补丁。
        WallPassHook.Instance.Uninstall();
        FriendlyPassHook.Instance.Uninstall();
        _harmony?.UnpatchSelf();
        _harmony = null;
        LoggerInstance.Msg("FriendlyNoclip 已卸载补丁。");
    }

    /// <summary>
    /// 按<b>名称 + 参数个数</b>选取目标重载。
    ///
    /// <para>
    /// 【为什么必须这样】`GridUnitData` 上存在同名重载：
    /// <c>void OnEnter()</c>（RVA <c>0x870160</c>，空桩）与
    /// <c>void OnEnter(BattleUnit)</c>（RVA <c>0x873b20</c>，真正的实现）；
    /// `OnLeave` 也有两个。<see cref="Type.GetMethod(string, BindingFlags)"/> 在重载
    /// 歧义时返回的是**不确定的一个**，会造成「日志说挂载成功、运行时却一次都不触发」。
    /// 本函数用参数个数把重载唯一化，找不到或找到多个都明确报警。
    /// </para>
    /// </summary>
    /// <param name="targetType">目标类型（Il2Cpp 包装类型）。</param>
    /// <param name="methodName">方法名。</param>
    /// <param name="parameterCount">期望的参数个数；<c>null</c> 表示不限。</param>
    private static MethodInfo? SelectOverload(Type targetType, string methodName, int? parameterCount)
    {
        MethodInfo[] candidates = targetType.GetMethods(
            BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static);

        var matched = new List<MethodInfo>();

        foreach (MethodInfo m in candidates)
        {
            if (m.Name != methodName)
            {
                continue;
            }

            if (parameterCount.HasValue && m.GetParameters().Length != parameterCount.Value)
            {
                continue;
            }

            matched.Add(m);
        }

        if (matched.Count == 0)
        {
            Log.Warning(
                $"找不到方法 {targetType.Name}.{methodName}" +
                (parameterCount.HasValue ? $"（参数个数={parameterCount.Value}）" : string.Empty) +
                "。同名候选：" +
                DescribeOverloads(candidates, methodName));
            return null;
        }

        if (matched.Count > 1)
        {
            // 仍然歧义：打印全部候选，让人能一眼看出该补哪个过滤条件。
            Log.Warning(
                $"{targetType.Name}.{methodName} 仍有 {matched.Count} 个候选，取第一个：" +
                DescribeOverloads(matched.ToArray(), methodName));
        }

        return matched[0];
    }

    /// <summary>把同名方法的签名拼成一行，用于诊断日志。</summary>
    private static string DescribeOverloads(MethodInfo[] methods, string methodName)
    {
        var parts = new List<string>();

        foreach (MethodInfo m in methods)
        {
            if (m.Name != methodName)
            {
                continue;
            }

            ParameterInfo[] ps = m.GetParameters();
            var args = new string[ps.Length];

            for (int i = 0; i < ps.Length; i++)
            {
                args[i] = ps[i].ParameterType.Name;
            }

            parts.Add($"{m.Name}({string.Join(", ", args)})");
        }

        return parts.Count == 0 ? "（无）" : string.Join(" | ", parts);
    }

    /// <summary>
    /// 查 Harmony 为某个目标方法实际选中的 <c>MethodPatcher</c> 是不是
    /// <c>Il2CppDetourMethodPatcher</c>，以及它的 <c>IsValid</c>。
    ///
    /// <para>
    /// 【为什么要这个】<c>Il2CppDetourMethodPatcher</c> 是 <c>internal</c>，<c>IsValid</c>
    /// 也是 <c>internal</c>，编译期拿不到。但“Harmony 到底有没有把原生 detour 装上”
    /// 是本次调查的<b>核心未知数</b>，不能靠读源码推断 —— 必须运行时量出来。
    /// </para>
    /// <para>
    /// <c>TryResolve</c> 只在 <c>IsValid == true</c> 时才赋值 <c>args.MethodPatcher</c>，
    /// 所以：类型是 <c>Il2CppDetourMethodPatcher</c> ⇒ 原生 detour 已装（补丁应触发）；
    /// 类型是默认的 <c>MethodPatcher</c> ⇒ 静默回退到托管 IL 补丁（原生调用碰不到）。
    /// </para>
    /// </summary>
    private static string DescribePatcher(MethodBase target)
    {
        try
        {
            Type? pm = AccessTools.TypeByName("HarmonyLib.Public.Patching.PatchManager");
            MethodInfo? get = pm?.GetMethod(
                "GetMethodPatcher",
                BindingFlags.Public | BindingFlags.Static,
                null,
                new[] { typeof(MethodBase) },
                null);

            object? patcher = get?.Invoke(null, new object[] { target });

            if (patcher == null)
            {
                return "GetMethodPatcher 返回 null";
            }

            Type pt = patcher.GetType();
            string name = pt.Name;

            // IsValid 是 internal 属性，显式声明在 Il2CppDetourMethodPatcher 自身上。
            PropertyInfo? isValid = pt.GetProperty(
                "IsValid",
                BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);

            string valid = isValid == null
                ? "（该类型无 IsValid）"
                : $"IsValid={isValid.GetValue(patcher)}";

            return $"{name} [{valid}]";
        }
        catch (Exception e)
        {
            return $"探测失败：{e.GetType().Name}: {e.Message}";
        }
    }

    private int TryPatch(
        string targetTypeName,
        string targetMethodName,
        string patchName,
        int? parameterCount = null)
    {
        try
        {
            Type? targetType = FindIl2CppType(targetTypeName);
            if (targetType == null)
            {
                LoggerInstance.Warning($"找不到游戏类型 Il2Cpp.{targetTypeName}。");
                return 0;
            }

            MethodInfo? target = SelectOverload(targetType, targetMethodName, parameterCount);

            if (target == null)
            {
                return 0;
            }

            MethodInfo? patch = typeof(Plugin).GetMethod(
                patchName, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static);

            if (patch == null)
            {
                LoggerInstance.Warning($"找不到补丁方法 {patchName}。");
                return 0;
            }

            _harmony!.Patch(target, postfix: new HarmonyMethod(patch));

            // ★ 打印真实绑定的签名 —— "挂载成功"从此是可核对的断言，而不是布尔值。
            LoggerInstance.Msg(
                $"已挂载补丁：{targetTypeName}.{DescribeOverloads(new[] { target }, targetMethodName)} " +
                $"(IL={target.MethodHandle.GetFunctionPointer().ToInt64():x}) -> {patchName} " +
                $"| patcher={DescribePatcher(target)}");
            return 1;
        }
        catch (Exception e)
        {
            LoggerInstance.Error($"挂载补丁 {targetTypeName}.{targetMethodName} 失败：{e}");
            return 0;
        }
    }

    /// <summary>与 <see cref="TryPatch"/> 相同，但挂成 <b>Prefix</b>。</summary>
    private int TryPatchPrefix(
        string targetTypeName,
        string targetMethodName,
        string patchName,
        int? parameterCount = null)
    {
        try
        {
            Type? targetType = FindIl2CppType(targetTypeName);
            if (targetType == null)
            {
                LoggerInstance.Warning($"找不到游戏类型 Il2Cpp.{targetTypeName}。");
                return 0;
            }

            MethodInfo? target = SelectOverload(targetType, targetMethodName, parameterCount);

            if (target == null)
            {
                return 0;
            }

            MethodInfo? patch = typeof(Plugin).GetMethod(
                patchName, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static);

            if (patch == null)
            {
                LoggerInstance.Warning($"找不到补丁方法 {patchName}。");
                return 0;
            }

            _harmony!.Patch(target, prefix: new HarmonyMethod(patch));

            LoggerInstance.Msg(
                $"已挂载前缀补丁：{targetTypeName}.{DescribeOverloads(new[] { target }, targetMethodName)} " +
                $"(IL={target.MethodHandle.GetFunctionPointer().ToInt64():x}) -> {patchName} " +
                $"| patcher={DescribePatcher(target)}");
            return 1;
        }
        catch (Exception e)
        {
            LoggerInstance.Error($"挂载前缀补丁 {targetTypeName}.{targetMethodName} 失败：{e}");
            return 0;
        }
    }

    private static Type? FindIl2CppType(string typeName)
    {
        foreach (Assembly asm in AppDomain.CurrentDomain.GetAssemblies())
        {
            try
            {
                Type? t = asm.GetType($"Il2Cpp.{typeName}", throwOnError: false);
                if (t != null)
                {
                    return t;
                }
            }
            catch
            {
                // 忽略无法加载的程序集
            }
        }

        return null;
    }

    // =================================================================
    // 【功能 A】穿友方（native_detour）
    //   EnterGrid 拦「停在别人格上」；OnLeave Postfix 写回被踩掉的登记
    // =================================================================

    /// <summary>
    /// `GridUnitData.OnLeave()` 的 Prefix —— 记录"谁把某格的 battleUnit 清成 null"。
    ///
    /// <para>
    /// 动机：普查已确证「每移动一次，占据数就掉几个」，而 4 个合法写者全部成对。
    /// 所以要么有第 5 个调用路径，要么这 4 个之一被用在了错的格子上。
    /// 这里把**调用者返回地址**打出来，直接定位是哪一条。
    /// </para>
    /// </summary>
    internal static void GridUnitData_OnLeave_Prefix(GridUnitData __instance)
    {
        try
        {
            // 计数**与日志分开**：计数永远做（无开销、无副作用），
            // 日志受 Diagnostics 门控。
            //
            // 历史上这里的前 20 次是**不受门控**的 —— 目的是把「补丁零触发」
            // 与「诊断开关没开」分开。现在改为全部门控（用户要求：战斗频繁、
            // 不能让日志膨胀）；代价是开诊断前看不到这些命中行 ——
            // 但 §「排查顺序」已要求先确认 md5 与冷启动，那条路更可靠。
            int n = Interlocked.Increment(ref _onLeaveHits);
            _ = n;

            if (Diagnostics.Value && n <= 20)
            {
                LogInfo(() => $"[命中] OnLeave 第 {n} 次被调用");
            }

            if (__instance == null)
            {
                return;
            }

            // ★★ 「穿越不留痕」修复在 **Postfix** 里做，不在 Prefix —— 见
            //    GridUnitData_OnLeave_Postfix 的注释（Prefix 的写回会被原函数立刻抹掉）。

            if (Diagnostics.Value)
            {
                LogUnitWrite("OnLeave", __instance, null);
            }
        }
        catch (Exception e)
        {
            Log.Warning($"[登记] OnLeave 探针异常：{e.Message}");
        }
    }

    /// <summary>
    /// <c>GridUnitData.OnLeave()</c> 的 Postfix —— 「穿越不留痕」的真正修复点。
    ///
    /// <para>
    /// 【为什么必须是 Postfix】<c>OnLeave</c> 原函数的**第一条指令**就是
    /// <c>[grid+0x18] = 0</c>（见 objdump <c>0x180873b60</c>）。所以任何在 Prefix 里的
    /// 写回都会被它立刻抹掉 —— 实测日志 15:52:42.828 完整演示了这个失败：
    /// <code>
    /// [修复·登记] ★已写回 (r9,c0) 原主 ptr=0x13439f6c0      ← Prefix 写进去了
    /// [登记] OnLeave 格(r9,c0) 改前=...ptr=0x13439f6c0     ← 原函数读到的确实是我写的值
    ///                                                     ↑ 但它紧接着就写 0
    /// </code>
    /// 点击时读到的仍然是 <c>battleUnit=null</c>。
    /// </para>
    ///
    /// <para>
    /// 【Postfix 的时机】原函数完整跑完（清空 + 撤特效 + 发通知）之后才执行，
    /// 所以这里的写回不会再被覆盖，同时又不干扰游戏自己的清理逻辑 —— 符合
    /// 「不破坏 AI 占位与行为」的约束。
    /// </para>
    /// </summary>
    internal static void GridUnitData_OnLeave_Postfix(GridUnitData __instance)
    {
        try
        {
            if (__instance == null || !NativeDetourEnabled.Value)
            {
                return;
            }

            RepairCoveredOccupant(__instance);
        }
        catch (Exception e)
        {
            Log.Warning($"[修复·登记] OnLeave Postfix 异常：{e.Message}");
        }
    }
    /// <summary>
    /// <c>BattleUnit.EnterGrid(GridUnitData, bool, bool)</c> 的 Prefix ——
    /// 「穿越不留痕」的**探测点**（配合 <c>GridUnitData_OnLeave_Postfix</c> 完成修复）。
    ///
    /// <para>
    /// （<c>RegretMove</c>/<c>EnterBattleField</c>×2/<c>HeroEnterGridDelay</c>/<c>MoveFromTarget</c>/
    /// <c>PlayBattleUnitMove</c>）全部汇到这里。它同时拿到 <c>__instance</c>（移动者）、
    /// <paramref name="grid"/>（目标格），而判定「这是穿越」所需的全部信息恰好就是这两个：
    /// <b>目标格上已有别的单位</b> = 将要覆盖一个合法的原主。
    /// </para>
    ///
    /// <para>
    /// 【与 OnEnter/OnLeave 的关系】<c>OnEnter(grid, unit)</c> 拿不到「谁在移动」，
    /// <c>OnLeave()</c> 更是只有一个格子参数 —— 两者都缺上下文，所以只能靠旁表，
    /// 而旁表必须跟踪战斗开始/结束/死亡/撤销才能不失步，复杂度不划算。
    /// <c>EnterGrid</c> 直接消除了这个需求。
    /// </para>
    ///
    /// <para>
    /// 【本探测要回答的两个问题】
    /// <list type="number">
    ///   <item>这个补丁到底触不触发？（与 <c>OnEnter</c>/<c>OnLeave</c> 同源的疑问：
    ///         两者都只在原生路径上被调用，补丁行为不一致。）</item>
    ///   <item>穿越现场的真实字段组合是什么？能否用
    ///         <c>grid.battleUnit != null &amp;&amp; grid.battleUnit != __instance</c>
    ///         干净地区分「穿越」与「正常进入空格」？</item>
    /// </list>
    /// </para>
    /// </summary>
    internal static bool BattleUnit_EnterGrid_Prefix(
        BattleUnit __instance,
        GridUnitData grid,
        bool noTurnRotation,
        bool teleport)
    {
        // ★★ 职责一：城墙不可停留（唯一有效的拦截点）。
        //
        // 【为什么必须是这里 —— 本项目真实踩坑】
        //   上一版把「城墙禁停」钩在 BattleController.GenerateMovePath 上，
        //   能拦住玩家点击，但 AI 仍然站到了墙上。反汇编查清原因：
        //
        //     GenerateMovePath 的调用者只有 2 处（均在 UI / 点击路径）
        //     AI 走的是 PlayBattleUnitMove → MoveFromTarget → 逐格 EnterGrid
        //     —— 根本不经过 GenerateMovePath
        //
        //   而 EnterGrid 有 6 个调用者，**覆盖了单位占据格子的全部途径**：
        //     EnterBattleField ×2（入场）/ MoveNext ×3（AI 与点击的实际移动）
        //     / RegretMove（撤销）。且 GridUnitData.OnEnter（写 [格+0x18]=单位）
        //     全二进制**只有 EnterGrid 一个调用者**。
        //
        // 【为什么返回 false 是安全的】
        //   EnterGrid 返回 void —— 没有返回值供调用方消费。
        //   （对比：MoveFromTarget 返回 IEnumerator，调用方直接把它丢给
        //    StartCoroutine 且不做 null 检查；在那里返回 false 会把 null
        //    交给协程驱动 → 极可能崩。所以不能钩那里。）
        //
        // 拦截后「什么都不做」：不替游戏改写意图，等价于该格不可达。
        if (WallPassEnabled.Value && ShouldBlockWallStep(grid))
        {
            LogInfo(() =>
                $"[城墙禁停] 拦下进入（{SafeRow(grid)},{SafeCol(grid)}）：城墙可跨越，但不得停留。");
            return false;
        }

        // ★★ 职责二：「可路过，不可停留」—— 不得停在已被占用的格子上。
        //
        // 【要解决的问题】穿友方的本意是「能踏过队友的格子」，但之前没区分
        //   「路过」与「停靠」，于是 AI 直接停在了友方单位 / **设施**的格子上：
        //
        //     实机现象（2026-10，守城图）：两个 NPC 与**箭塔重叠**。
        //     箭塔（箭塔·三）是普通 BattleUnit（不是 obstale），所以它归穿友方
        //     这条线按队伍处理 —— 同队即被当成「队友」放行，于是被踩。
        //
        // 【为什么必须在 EnterGrid 而不是 Navigate】
        //   Navigate 只回答「寻路时这格算不算通」—— 而「路过」正需要它算通。
        //   本钩子在**落格那一刻**才拦，于是：路径仍可穿过，但落点不会是占用格。
        //   这正是「可路过，不可停留」的实现方式。
        //
        // 【为什么不会让 AI 无路可走】
        //   与城墙不同，占用格是**动态**的：AI 只需换一个空落点即可，
        //   而空格总是大量存在（实测一局里 400 格中仅 43 格有登记）。
        //
        // ⚠️ 只拦「别人」：自己走进自己当前所在的格子必须放行，
        //   否则单位会被自家登记卡住。
        //
        // 【与上一道检查（城墙禁停）是否重复？—— 不重复，且不可能同时命中】
        //
        //   两者的判据**可以同时为真**：一格既是活着的城墙、上面又站着单位
        //   （那正是「AI 站到城墙上」那个 bug 的现场）。
        //   但因为 A 在前、且 A 命中时**立即 return false**，
        //   B 在这个格子上**永远走不到** —— 短路掉了。
        //
        //   所以语义是清晰的两级：
        //     A 管「墙不能停」（静态判据：格子的类型）
        //     B 管「人不能停」（动态判据：格子的占用者）
        //   同一格最多只产生一条日志、也只拦截一次。
        //
        //   实测（2026-10）：
        //     · 空城墙 (0,11)      → A 拦下，格保持空       ✅
        //     · 被占普通格 (0,0)   → B 拦下，占用者未被覆盖 ✅
        if (NativeDetourEnabled.Value && IsBlockedByOccupant(__instance, grid))
        {
            LogInfo(() =>
                $"[禁停占用格] 拦下进入（{SafeRow(grid)},{SafeCol(grid)}）：" +
                $"该格已有单位 0x{ReadOccupantPtr(grid):x}（进入者 0x{__instance.Pointer.ToInt64():x}）。");
            return false;
        }


        try
        {
            // 计数永远做，日志受门控（同 OnLeave）。
            int n = Interlocked.Increment(ref _enterGridHits);
            _ = n;

            if (Diagnostics.Value && n <= 40)
            {
                LogInfo(() => $"[命中·EnterGrid] 第 {n} 次被调用");
            }

            if (__instance == null || grid == null)
            {
                return true;   // 信息不全：放行，不阻断游戏
            }

            BattleUnit? occupant;

            try
            {
                occupant = grid.battleUnit;
            }
            catch (Exception e)
            {
                Log.Warning($"[穿越] 读 grid.battleUnit 失败：{e.Message}");
                return true;   // 读不了就放行 —— 宁可多一格可停，不要弄坏移动
            }

            int row, col;

            try
            {
                row = grid.row;
                col = grid.column;
            }
            catch (Exception)
            {
                row = col = -9999;
            }

            // 移动者自己认为它站在哪 —— 用来核对「先摘旧、再挂新」的成对性。
            string selfPos;

            try
            {
                GridUnitData? mg = __instance.mapGrid;
                selfPos = mg == null
                    ? "null"
                    : $"(r{mg.row},c{mg.column})";
            }
            catch (Exception)
            {
                selfPos = "读取失败";
            }

            // 占用者自己的 mapGrid 指向哪 —— 穿越受害者的特征是
            // 「mapGrid 正指向被穿的那格」（它没动过，是被覆盖的）。
            string occPos = "-";
            bool isTraversal = false;

            if (occupant != null)
            {
                try
                {
                    GridUnitData? omg = occupant.mapGrid;
                    occPos = omg == null
                        ? "null"
                        : $"(r{omg.row},c{omg.column})";

                    // ★ 判据（已由实测日志 15:21:31 坐实）：目标格上已有单位，
                    //   且不是移动者自己 → 这就是要覆盖一个合法原主。
                    //   实测中每个 ★穿越 的占用者 mapGrid 都精确指向被穿的那一格。
                    isTraversal = occupant.Pointer != __instance.Pointer;

                    // 记下「即将被覆盖的原主」，交给紧随其后的 OnLeave 写回。
                    if (isTraversal && NativeDetourEnabled.Value)
                    {
                        RecordPendingRepair(
                            grid, occupant, __instance, row, col);
                    }
                }
                catch (Exception e)
                {
                    occPos = $"读取失败：{e.Message}";
                }
            }

            // 高频（移动时逐格调用）—— LogInfo 自带门控，无需再套 if。
            LogInfo(() =>
                    $"[穿越] EnterGrid {(isTraversal ? "★穿越" : "普通")} " +
                    $"目标格(r{row},c{col}) mapID={SafeMapId(grid)} | " +
                    $"移动者 ptr=0x{__instance.Pointer.ToInt64():x} 队伍={ReadTeamId(__instance)} 原位置={selfPos} | " +
                    $"占用={(occupant == null ? "空" : $"ptr=0x{occupant.Pointer.ToInt64():x} 队伍={ReadTeamId(occupant)} mapGrid={occPos}")} | " +
                    $"noTurnRotation={noTurnRotation} teleport={teleport}");
        }
        catch (Exception e)
        {
            Log.Warning($"[穿越] EnterGrid 探针异常：{e.Message}");
            // 探针出错绝不能阻断移动 —— 这里是所有移动的必经之路。
        }
        // 默认放行：本钩子只拦「进入己方城墙」，其余交给游戏原逻辑。
        // 这里是所有移动的必经之路 —— 任何意外的返回值都会弄坏移动。
        return true;
    }

    /// <summary>
    /// 目标格是不是<b>不可停留的己方城墙</b>。
    ///
    /// <para>
    /// 判据与 <see cref="WallPassData.Apply"/> 共用 <see cref="WallPassData.TryGetWallTeam"/>：
    /// 两处必须看同一个字段，否则会出现「放行了但不让停」这类不一致。
    /// </para>
    /// <para>
    /// 只拦城墙，不拦中立障碍 —— 后者原版本来就不可达，不重复干预。
    /// </para>
    /// </summary>
    private static bool ShouldBlockWallStep(GridUnitData? grid)
    {
        try
        {
            return WallPassData.TryGetWallTeam(grid, out _);
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// 目标格是否已被**别的单位**占据 —— 用于实现「可路过，不可停留」。
    ///
    /// <para>
    /// 【为什么需要它】穿友方让 AI 能踏过队友格子（这是本意），但之前没有区分
    /// 「路过」与「停靠」，于是 AI 直接**停**在了友方单位 / 设施（箭塔·三）的格子上，
    /// 把对方的占位登记踩掉 —— 实机表现为「两个 NPC 与箭塔重叠」。
    /// </para>
    ///
    /// <para>
    /// 【判据】目标格上已有 <c>battleUnit</c>，且**不是进入者自己**。
    /// </para>
    /// <list type="bullet">
    ///   <item>自己走进自己所在的格 → <b>放行</b>。否则单位会被自家登记卡死
    ///     （例如原地转向、或引擎重设位置）。</item>
    ///   <item>目标是空格 → 放行。</item>
    ///   <item>目标有别人 → 拦下。这是唯一被拦的情形。</item>
    /// </list>
    ///
    /// <para>
    /// ⚠️ 任何读取异常都返回 <c>false</c>（放行）—— 本钩子在所有移动的必经之路上，
    /// 宁可漏拦一格，也不能因为读异常弄坏移动。
    /// </para>
    /// </summary>
    private static bool IsBlockedByOccupant(BattleUnit? mover, GridUnitData? grid)
    {
        if (mover == null || grid == null)
        {
            return false;
        }

        try
        {
            BattleUnit? occupant = grid.battleUnit;

            if (occupant == null)
            {
                return false;
            }

            return occupant.Pointer != mover.Pointer;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>读目标格的占用者指针；仅供日志，异常时返回 0。</summary>
    private static long ReadOccupantPtr(GridUnitData? grid)
    {
        try
        {
            return grid?.battleUnit?.Pointer.ToInt64() ?? 0;
        }
        catch
        {
            return 0;
        }
    }

    /// <summary>读格子行号；异常时返回哨兵值（仅用于日志）。</summary>
    private static int SafeRow(GridUnitData? grid)
    {
        try { return grid?.row ?? -1; } catch { return -1; }
    }

    /// <summary>读格子列号；异常时返回哨兵值（仅用于日志）。</summary>
    private static int SafeCol(GridUnitData? grid)
    {
        try { return grid?.column ?? -1; } catch { return -1; }
    }

    /// <summary>
    /// 记下即将被穿越覆盖的原主，等下一次 <c>OnLeave</c> 把它写回。
    /// 只在 <see cref="NativeDetourEnabled"/> 开启时被调用。
    /// </summary>
    private static void RecordPendingRepair(
        GridUnitData grid,
        BattleUnit occupant,
        BattleUnit mover,
        int row,
        int col)
    {
        try
        {
            lock (_pendingLock)
            {
                // 上限保护：正常移动最多同时 1～2 条，超出说明有异常路径。
                if (_pendingRepairs.Count >= MaxPendingRepairs)
                {
                    _pendingRepairs.Clear();
                }

                _pendingRepairs[grid.Pointer] = new PendingRepair
                {
                    Grid = grid.Pointer,
                    Occupant = occupant.Pointer,
                    Mover = mover.Pointer,
                    Row = row,
                    Col = col,
                };
            }

            LogInfo(() =>
                $"[修复·登记] 记录待恢复：格(r{row},c{col}) " +
                $"原主 ptr=0x{occupant.Pointer.ToInt64():x}（被穿越者 0x{mover.Pointer.ToInt64():x} 覆盖）");
        }
        catch (Exception e)
        {
            Log.Warning($"[修复·登记] 记录失败：{e.Message}");
        }
    }

    /// <summary>
    /// 在 <c>OnLeave(grid)</c> 把本格清空**之前**，把上一步被覆盖的合法原主写回。
    ///
    /// <para>
    /// 【为什么安全】只有当本格当前登记的是当初的穿越者（而不是原主、也不是空）时
    /// 才写回 —— 这恰好确认了「本格现在被穿越者占着，而原主才是合法主人」。
    /// 其余一切情况（真离开、空格、别的单位）都原样放行，不干预游戏的正常运行。
    /// </para>
    /// </summary>
    private static void RepairCoveredOccupant(GridUnitData grid)
    {
        try
        {
            PendingRepair? pending;

            lock (_pendingLock)
            {
                if (!_pendingRepairs.TryGetValue(grid.Pointer, out pending))
                {
                    return;
                }

                // 无论后续是否真的写回，这条记录都已消费。
                _pendingRepairs.Remove(grid.Pointer);
            }

            if (pending == null)
            {
                return;
            }

            // ★ 判据修正（2026-10-05）：不再看「本格当前是不是穿越者」。
            //
            // OnLeave 的语义就是「离开」，它原函数第一条指令必定把本格清空，
            // 所以 Postfix 里读到的 current 天然是 null。实测日志 18:04:58 完整
            // 演示了这一点：
            //   [穿越] EnterGrid ★穿越 目标格(r7,c0) 占用 mapGrid=(r7,c0)
            //   [修复·登记] 跳过 (r7,c0)：本格当前=空，不是当初的穿越者
            // —— 旧条件把每一次修复都跳过了。
            //
            // 真实判据：要看**原主自己**是否仍然认为它在本格。
            // 若原主 alive 且它的 mapGrid 仍指回本格，说明它从未搬走，
            // 只是登记被人踩掉了 —— 这才是该写回的情形。
            BattleUnit? current = grid.battleUnit;

            if (current != null && current.Pointer != pending.Mover)
            {
                // 本格已被别的单位占用，现场与记录不符，不插手。
                LogInfo(() =>
                    $"[修复·登记] 跳过 (r{pending.Row},c{pending.Col})：" +
                    $"本格当前被 ptr=0x{current.Pointer.ToInt64():x} 占用。");
                return;
            }
            BattleUnit? original = WrapUnit(pending.Occupant);

            if (original == null)
            {
                Log.Warning(
                    $"[修复·登记] 无法还原原主 ptr=0x{pending.Occupant.ToInt64():x}"
                    + $"（格(r{pending.Row},c{pending.Col})），跳过。");
                return;
            }

            // ★ 关键安全阀：原主必须**仍然认为自己在本格**。
            // 否则说明它是真的搬走了（正常离开），而不是被穿越踩掉登记 ——
            // 那种情况绝对不能写回，否则会在空格上凭空造出一个占位。
            if (!IsAliveSafe(original))
            {
                return;
            }

            try
            {
                GridUnitData? omg = original.mapGrid;

                if (omg == null || omg.Pointer != pending.Grid)
                {
                    LogInfo(() =>
                        $"[修复·登记] 跳过 (r{pending.Row},c{pending.Col})：" +
                        $"原主 0x{original.Pointer.ToInt64():x} 的 mapGrid=" +
                        (omg == null
                            ? "null"
                            : $"(r{omg.row},c{omg.column})") +
                        $"，已不指向本格（它是真的离开了）。");
                    return;
                }
            }
            catch (Exception e)
            {
                Log.Warning($"[修复·登记] 读原主 mapGrid 失败：{e.Message}");
                return;
            }

            // ★ 真正的修复：把合法原主写回本格。
            // battleUnit 的 setter 带 GC write barrier，托管层写入是安全的。
            grid.battleUnit = original;

            LogInfo(() =>
                $"[修复·登记] ★已写回 (r{pending.Row},c{pending.Col}) " +
                $"原主 ptr=0x{original.Pointer.ToInt64():x} 队伍={ReadTeamId(original)} " +
                $"（此前被穿越者 0x{pending.Mover.ToInt64():x} 覆盖）");
        }
        catch (Exception e)
        {
            Log.Warning($"[修复·登记] 写回异常：{e.Message}");
        }
    }

    /// <summary>
    /// 待恢复的占位：<c>EnterGrid</c> 即将覆盖的合法原主。
    ///
    /// <para>
    /// 【生命周期】一条记录只活「一次 EnterGrid Prefix」到「紧随其后的 OnLeave」之间，
    /// 被消费后立即移除。**没有跨战斗、跨回合的残留** —— 这正是它不需要跟踪
    /// 战斗开始/结束/死亡/撤销的原因（旁表方案就是死在这里）。
    /// </para>
    /// </summary>
    private sealed class PendingRepair
    {
        internal IntPtr Grid;
        internal IntPtr Occupant;
        internal IntPtr Mover;
        internal int Row;
        internal int Col;
    }

    private static readonly object _pendingLock = new();

    /// <summary>格指针 → 待恢复记录（最多同时 1～2 条）。</summary>
    private static readonly Dictionary<IntPtr, PendingRepair> _pendingRepairs = new();

    /// <summary>剩余待恢复条数上限 —— 防止异常情况下无限增长。</summary>
    private const int MaxPendingRepairs = 8;

    /// <summary>


    /// <summary>读 <c>BattleUnit.get_IsAlive()</c>，失败时保守地当作"活着"。</summary>
    private static bool IsAliveSafe(BattleUnit unit)
    {
        try
        {
            return unit.IsAlive;
        }
        catch (Exception)
        {
            return true;
        }
    }

    /// <summary>把裸指针还原成托管 <c>BattleUnit</c> 包装，失败返回 null。</summary>
    private static BattleUnit? WrapUnit(IntPtr pointer)
    {
        if (pointer == IntPtr.Zero)
        {
            return null;
        }

        try
        {
            return new BattleUnit(pointer);
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>

    /// <summary>输出一次登记写入：目标格、改前的值、单位、以及调用者返回地址。</summary>
    private static void LogUnitWrite(string op, GridUnitData grid, BattleUnit? unit)
    {
        int row, col;

        try
        {
            row = grid.row;
            col = grid.column;
        }
        catch (Exception)
        {
            row = col = -9999;
        }

        // 改之前该格登记的是谁 —— 这正是"被谁覆盖/清掉"的直接证据。
        string before;

        try
        {
            BattleUnit? prev = grid.battleUnit;
            before = prev == null
                ? "空"
                : $"队伍={ReadTeamId(prev)},mapGrid={(prev.mapGrid == null ? "null" : $"r{prev.mapGrid.row}c{prev.mapGrid.column}")},ptr=0x{prev.Pointer.ToInt64():x}";
        }
        catch (Exception)
        {
            before = "读取失败";
        }

        // 单位自己认为它站在哪 —— 与 grid 对比即可看出"脱钩"。
        string self;

        if (unit == null)
        {
            self = "-";
        }
        else
        {
            try
            {
                GridUnitData? mg = unit.mapGrid;
                self = mg == null
                    ? "mapGrid=null"
                    : $"mapGrid=r{mg.row}c{mg.column},mapID={mg.mapID}";
            }
            catch (Exception)
            {
                self = "mapGrid 读取失败";
            }
        }

        LogInfo(() =>
            $"[登记] {op} 格(r{row},c{col}) mapID={SafeMapId(grid)} | " +
            $"改前={before} | 写入={(unit == null ? "null" : $"队伍={ReadTeamId(unit)},ptr=0x{unit.Pointer.ToInt64():x}")} | " +
            $"{self}");

        // 只有"清掉一个原本有人的格子"才是我们要找的元凶 —— 高亮它。
        try
        {
            BattleUnit? prev2 = grid.battleUnit;

            if (unit == null && prev2 != null)
            {
                Log.Warning(
                    $"[登记·可疑] OnLeave 清掉了 (r{row},c{col}) 上原有的单位" +
                    $"(队伍={ReadTeamId(prev2)})，而该单位 mapGrid=" +
                    $"{(prev2.mapGrid == null ? "null" : $"r{prev2.mapGrid.row}c{prev2.mapGrid.column}")}" +
                    $" —— 若二者相同，则该单位被「注销」了却没搬走。");
            }
        }
        catch (Exception)
        {
            // 忽略
        }
    }

    private static int SafeMapId(GridUnitData g)
    {
        try
        {
            return g.mapID;
        }
        catch (Exception)
        {
            return -1;
        }
    }

    // ← 2026-10-05 删除了 CallerReturnAddress()。
    //   IL2CPP 下 StackTrace 只能看到 DMD 包装与 il2cpp_runtime_invoke 的帧，
    //   拿不到游戏侧调用者（实测输出恒为 `#0 .DMD<...OnLeave> #1 .(il2cpp -> managed)
    //   #2 IL2CPP.il2cpp_runtime_invoke`），对定位无帮助。

    // ------------------------------------------------------------------
    // 城墙「可跨越」：战斗开始置位 / 战斗结束恢复
    // ------------------------------------------------------------------

    /// <summary>
    /// <c>BattleMapData.GenerateMapObjs</c> 的 Postfix —— 城墙放行的唯一钩子。
    ///
    /// <para>
    /// 用 Postfix（而非 Prefix）：本方法**体内**才创建障碍物
    /// （<c>GenerateBuildingObstacle</c> → <c>GenerateWallData</c>），
    /// 必须在它**跑完之后**才能遍历 <c>obstacleGrids</c>。
    /// </para>
    ///
    /// <para>
    /// ⚠️ <b>不要改回 <c>Generate</c></b>：那个方法只做布局，
    /// 跑完时 <c>obstacleGrids</c> 还是空的 —— 实测会打印
    /// 「已放行 <b>0</b> 面己方城墙」。两个方法的调用链见注册处的注释。
    /// </para>
    /// </summary>
    internal static void BattleMapData_GenerateMapObjs_Postfix(BattleMapData __instance)
    {
        try
        {
            if (__instance == null)
            {
                return;
            }

            _activeMap = __instance;

            // selfTeamID：用「玩家操控的队伍」与「玩家所在队伍」都试一次。
            // 实测守城战时 GetPlayerControlTeamID 与城墙 teamID 一致；
            // 两个都写不会误伤 —— 因为判据仍是 obstacleType==Wall，
            // 而一份城墙只会属于一个队伍。
            int selfTeamID = ResolveSelfTeamID();

            WallPassData.Apply(__instance, selfTeamID);

            // 若玩家队伍与「玩家操控队伍」不同（例如观战/AI 托管），
            // 把另一个也放行，保证守方 AI 同样能穿越自己城墙。
            int playerTeamID = ResolvePlayerTeamID();

            if (playerTeamID != selfTeamID)
            {
                WallPassData.Apply(__instance, playerTeamID);
            }
        }
        catch (Exception e)
        {
            Log.Warning($"[城墙通行] Generate Postfix 异常：{e.Message}");
        }
    }

    /// <summary>
    /// <c>BattleController.BattleRealEnd</c> 的 Postfix —— 战斗结束时把
    /// <c>passes</c> 恢复成原值。
    /// </summary>
    internal static void BattleController_BattleRealEnd_Postfix()
    {
        try
        {
            WallPassData.Restore();
        }
        catch (Exception e)
        {
            Log.Warning($"[城墙通行] BattleRealEnd Postfix 异常：{e.Message}");
        }
    }

    /// <summary>取「玩家操控的队伍 ID」，失败时返回一个不会匹配任何队伍的哨兵。</summary>
    private static int ResolveSelfTeamID()
    {
        try
        {
            var bc = GetBattleController();

            if (bc != null)
            {
                return bc.GetPlayerControlTeamID();
            }
        }
        catch (Exception e)
        {
            Log.Warning($"[城墙通行] 取 selfTeamID 失败：{e.Message}");
        }

        return int.MinValue;
    }

    /// <summary>取「玩家所在队伍 ID」，失败时返回哨兵。</summary>
    private static int ResolvePlayerTeamID()
    {
        try
        {
            var bc = GetBattleController();
            var team = bc?.GetPlayerTeam();

            if (team != null)
            {
                return team.ID;
            }
        }
        catch (Exception e)
        {
            Log.Warning($"[城墙通行] 取 playerTeamID 失败：{e.Message}");
        }

        return int.MinValue;
    }

    /// <summary>找当前的 <c>BattleController</c> 实例。</summary>
    private static BattleController? GetBattleController()
    {
        try
        {
            return BattleController.Instance;
        }
        catch
        {
            return null;
        }
    }

    // ------------------------------------------------------------------
    // 探针 1：GetMoveRangeGrids Postfix
    // ------------------------------------------------------------------

    // =================================================================
    // 【功能 B】穿城墙（wall_pass）
    //   写 passes → 可跨越；BattleRealEnd 恢复；EnterGrid 里禁停
    // =================================================================

    /// <summary>
    /// 游戏算完移动范围后触发。记录本次调用的参数、输出列表内容，
    /// 并把整张地图的网格明细写入文件，用于反推占位过滤的依据。
    /// </summary>
    internal static void BattleMapData_GetMoveRangeGrids_Postfix(
        BattleMapData __instance,
        int row,
        int column,
        int minRange,
        int maxRange,
        Il2CppSystem.Collections.Generic.List<GridUnitData> grids,
        int selfTeamID)
    {
        try
        {
            if (__instance == null || grids == null)
            {
                return;
            }

            _activeMap = __instance;

            if (!Diagnostics.Value)
            {
                return;
            }

            DumpMoveRange(__instance, row, column, minRange, maxRange, grids, selfTeamID);
        }
        catch (Exception e)
        {
            Log.Error($"GetMoveRangeGrids Postfix 异常：{e}");
        }
    }

    /// <summary>读一个单位的队伍 ID，失败时返回一个不会匹配任何队伍的哨兵值。</summary>
    /// <summary>
    /// 占位登记一致性审计（只读）。
    ///
    /// <para>
    /// 现场：用户在 <c>state=7</c> 点击 <c>(r7,c0)</c>，探针读到该格
    /// <c>占用=空</c> 且 <c>在范围内=是</c>，于是合法落点；但 <c>(r7,c0)</c>
    /// 其实是<b>另一个尚未行动过的 NPC 的初始位置</b>，走上去后视觉上就重叠了。
    /// </para>
    /// <para>
    /// 假设：格子的 <c>battleUnit</c> 与单位的 <c>mapGrid</c> 登记脱钩。
    /// 两个方向都要查，因为不知道是哪一边没更新：
    /// </para>
    /// <list type="bullet">
    ///   <item><b>反向</b>（主）：扫 <c>BattleMapData.normalGrids</c>，找
    ///   <c>battleUnit != null</c> 却与该单位的 <c>mapGrid</c> 不一致的格。
    ///   它只依赖地图格表，不依赖任何 BattleUnit 列表 —— 上一版就是因为
    ///   <c>managedUnits</c> 为空而白跑。</item>
    ///   <item><b>正向</b>（辅）：扫各个单位列表，找 <c>mapGrid</c> 所指格的
    ///   <c>battleUnit</c> 并非自己的单位。顺带报告各列表的元素数，
    ///   好确认到底哪个列表才是"场上单位"。</item>
    /// </list>
    /// </summary>
    private static void AuditOccupancy(BattleController controller, GridUnitData clicked)
    {
        try
        {
            LogInfo(() =>
                $"[审计] 被点格=(r{clicked.row},c{clicked.column}) " +
                $"battleUnit={(clicked.battleUnit == null ? "null" : "非null")} " +
                $"gridType={clicked.gridType}");

            AuditGrids(controller);

            BattleMapData? map = _activeMap;

            if (map != null)
            {
                AuditTableCrossCheck(map, clicked);
                AuditTableCensus(map);
            }

            AuditUnitLists(controller);
        }
        catch (Exception e)
        {
            Log.Warning($"[审计] 异常：{e.Message}");
        }
    }

    /// <summary>反向：从地图格表出发，找"格子上有人但登记自相矛盾"的格子。</summary>
    private static void AuditGrids(BattleController controller)
    {
        BattleMapData? map = _activeMap;

        if (map == null)
        {
            LogInfo(() =>"[审计·格] _activeMap 为空，跳过。");
            return;
        }

        Il2CppSystem.Collections.Generic.List<GridUnitData>? grids;

        try
        {
            grids = map.normalGrids;
        }
        catch (Exception e)
        {
            LogInfo(() =>$"[审计·格] 读 normalGrids 失败：{e.Message}");
            return;
        }

        if (grids == null)
        {
            LogInfo(() =>"[审计·格] normalGrids 为空。");
            return;
        }

        int total = 0;
        int occupied = 0;
        int broken = 0;
        var sb = new System.Text.StringBuilder();

        for (int i = 0; i < grids.Count; i++)
        {
            GridUnitData? g;

            try
            {
                g = grids[i];
            }
            catch (Exception)
            {
                continue;
            }

            if (g == null)
            {
                continue;
            }

            total++;

            BattleUnit? bu;

            try
            {
                bu = g.battleUnit;
            }
            catch (Exception)
            {
                continue;
            }

            if (bu == null)
            {
                continue;
            }

            occupied++;

            // 该格登记了一个单位；这个单位的 mapGrid 应该指回该格。
            GridUnitData? mg;

            try
            {
                mg = bu.mapGrid;
            }
            catch (Exception)
            {
                continue;
            }

            if (mg == null || mg.Pointer != g.Pointer)
            {
                broken++;
                sb.Append(
                    $"\n    格(r{g.row},c{g.column}) 登记单位(队伍={ReadTeamId(bu)}) " +
                    $"但该单位 mapGrid={(mg == null ? "null" : $"(r{mg.row},c{mg.column})")}");
            }
        }

        LogInfo(() =>
            $"[审计·格] normalGrids={total} 有单位登记={occupied} 自相矛盾={broken}{sb}");
    }

    /// <summary>
    /// 交叉核对两张表：<c>mapGrids</c>（寻路/落点判定实际用的表，`BattleMapData+0x30`，
    /// 列主序）与 <c>normalGrids</c>（`+0x40`，我的审计一直在读的表）。
    ///
    /// <para>
    /// 动机：写者分析已经查死了「野写者」这条路 —— <c>GridUnitData.battleUnit</c>
    /// 只有 <c>OnEnter</c>/<c>OnLeave</c> 两个写者，且 4 个调用点全部成对。
    /// 那么「有单位登记数从 16 掉到 11」就可能根本不是游戏状态变了，
    /// 而是**我在数另一张表**。只要同一坐标上两张表给出不同的
    /// <c>GridUnitData</c> 指针，方向就定了。
    /// </para>
    /// </summary>
    private static void AuditTableCrossCheck(BattleMapData map, GridUnitData clicked)
    {
        try
        {
            int row = clicked.row;
            int col = clicked.column;

            // 路径 A：游戏自己的按行列查询（读 mapGrids）。
            GridUnitData? viaRowCol = null;
            string viaErr = "无";

            try
            {
                viaRowCol = map.GetGridData(row, col);
            }
            catch (Exception e)
            {
                viaErr = e.Message;
            }

            // 路径 B：我正在用的 normalGrids 扫描。
            GridUnitData? viaList = null;

            try
            {
                Il2CppSystem.Collections.Generic.List<GridUnitData>? all = map.normalGrids;

                if (all != null)
                {
                    for (int i = 0; i < all.Count; i++)
                    {
                        GridUnitData? t = all[i];

                        if (t != null && t.row == row && t.column == col)
                        {
                            viaList = t;
                            break;
                        }
                    }
                }
            }
            catch (Exception)
            {
                // 忽略
            }

            string pa = viaRowCol == null
                ? $"null(查询异常={viaErr})"
                : $"0x{viaRowCol.Pointer.ToInt64():x}";

            string pb = viaList == null ? "null" : $"0x{viaList.Pointer.ToInt64():x}";

            bool same = viaRowCol != null && viaList != null
                && viaRowCol.Pointer == viaList.Pointer;

            string occA = viaRowCol == null
                ? "-"
                : (viaRowCol.battleUnit == null ? "空" : $"占(队伍={ReadTeamId(viaRowCol.battleUnit)})");

            string occB = viaList == null
                ? "-"
                : (viaList.battleUnit == null ? "空" : $"占(队伍={ReadTeamId(viaList.battleUnit)})");

            LogInfo(() =>
                $"[审计·对照] (r{row},c{col}) mapGrids→{pa} {occA} | " +
                $"normalGrids→{pb} {occB} | 同一对象={(same ? "是" : "否")}");

            // 被点格自身也算一路：它来自 GridUnitController.gridData（视觉对象）。
            LogInfo(() =>
                $"[审计·对照] 被点格自身 0x{clicked.Pointer.ToInt64():x} " +
                $"battleUnit={(clicked.battleUnit == null ? "null" : "非null")} | " +
                $"与mapGrids同对象={(viaRowCol != null && viaRowCol.Pointer == clicked.Pointer ? "是" : "否")} | " +
                $"与normalGrids同对象={(viaList != null && viaList.Pointer == clicked.Pointer ? "是" : "否")}");
        }
        catch (Exception e)
        {
            Log.Warning($"[审计·对照] 异常：{e.Message}");
        }
    }

    /// <summary>
    /// 一次性普查：整张 <c>mapGrids</c>（逐行列走 <c>GetGridByRowCol</c>，读 mapGrids）
    /// 与整张 <c>normalGrids</c> 各收一遍格子，比较两张表的规模、空/占分布、
    /// 以及**同一坐标上是否同一个对象**。
    ///
    /// <para>
    /// 只跑前 <see cref="CensusStride"/> 行做抽样，避免 320 次跨界调用拖慢战斗。
    /// </para>
    /// </summary>
    private static void AuditTableCensus(BattleMapData map)
    {
        try
        {
            Il2CppSystem.Collections.Generic.List<GridUnitData>? all = map.normalGrids;

            if (all == null)
            {
                LogInfo(() =>"[普查] normalGrids 为空，跳过。");
                return;
            }

            int listCount = all.Count;
            int listOccupied = 0;

            // 建表：坐标 -> (指针, 是否占据)
            var byCoord = new System.Collections.Generic.Dictionary<long, (long ptr, bool occ)>();

            for (int i = 0; i < listCount; i++)
            {
                GridUnitData? t;

                try
                {
                    t = all[i];
                }
                catch (Exception)
                {
                    continue;
                }

                if (t == null)
                {
                    continue;
                }

                int r, c;

                try
                {
                    r = t.row;
                    c = t.column;
                }
                catch (Exception)
                {
                    continue;
                }

                bool occ;

                try
                {
                    occ = t.battleUnit != null;
                }
                catch (Exception)
                {
                    occ = false;
                }

                if (occ)
                {
                    listOccupied++;
                }

                byCoord[((long)r << 32) | (uint)c] = (t.Pointer.ToInt64(), occ);
            }

            // 扫 mapGrids：逐行逐列。行数上限用 mapHeight，列数上限用 mapWidth。
            int h = map.mapHeight;
            int w = map.mapWidth;

            int mapCells = 0;
            int mapOccupied = 0;
            int sameObj = 0;
            int diffObj = 0;
            int missingInList = 0;
            int occMismatch = 0;

            var details = new System.Text.StringBuilder();

            for (int r = 0; r < h; r++)
            {
                for (int c = 0; c < w; c++)
                {
                    GridUnitData? t;

                    try
                    {
                        t = map.GetGridData(r, c);
                    }
                    catch (Exception)
                    {
                        continue;
                    }

                    if (t == null)
                    {
                        continue;
                    }

                    mapCells++;

                    bool occ;

                    try
                    {
                        occ = t.battleUnit != null;
                    }
                    catch (Exception)
                    {
                        occ = false;
                    }

                    if (occ)
                    {
                        mapOccupied++;
                    }

                    if (!byCoord.TryGetValue(((long)r << 32) | (uint)c, out var e))
                    {
                        missingInList++;
                        continue;
                    }

                    if (e.ptr == t.Pointer.ToInt64())
                    {
                        sameObj++;
                    }
                    else
                    {
                        diffObj++;
                    }

                    if (e.occ != occ)
                    {
                        occMismatch++;

                        if (details.Length < 1200)
                        {
                            details.Append(
                                $"\n    (r{r},c{c}) mapGrids占={occ} normalGrids占={e.occ} " +
                                $"ptr {t.Pointer.ToInt64():x} vs {e.ptr:x}");
                        }
                    }
                }
            }

            LogInfo(() =>
                $"[普查] mapGrids {w}x{h} 有效={mapCells} 占据={mapOccupied} | " +
                $"normalGrids={listCount} 占据={listOccupied} | " +
                $"同对象={sameObj} 异对象={diffObj} 表里没有={missingInList} " +
                $"占据判定不一致={occMismatch}{details}");
        }
        catch (Exception e)
        {
            Log.Warning($"[普查] 异常：{e.Message}");
        }
    }

    /// <summary>正向：扫各候选单位列表，报告规模并找 mapGrid 回指不一致的单位。</summary>
    private static void AuditUnitLists(BattleController controller)
    {
        ReportUnitList("activeUnitList", SafeList(() => controller.activeUnitList));
        ReportUnitList("managedUnits", SafeList(() => controller.managedUnits));
    }

    private static Il2CppSystem.Collections.Generic.List<BattleUnit>? SafeList(
        Func<Il2CppSystem.Collections.Generic.List<BattleUnit>?> get)
    {
        try
        {
            return get();
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static void ReportUnitList(
        string label,
        Il2CppSystem.Collections.Generic.List<BattleUnit>? units)
    {
        if (units == null)
        {
            LogInfo(() =>$"[审计·单位] {label}=null");
            return;
        }

        int total = 0;
        int alive = 0;
        int broken = 0;
        var sb = new System.Text.StringBuilder();

        for (int i = 0; i < units.Count; i++)
        {
            BattleUnit? u;

            try
            {
                u = units[i];
            }
            catch (Exception)
            {
                continue;
            }

            if (u == null)
            {
                continue;
            }

            total++;

            bool isAlive;

            try
            {
                isAlive = u.IsAlive;
            }
            catch (Exception)
            {
                isAlive = false;
            }

            if (!isAlive)
            {
                continue;
            }

            alive++;

            GridUnitData? mg;

            try
            {
                mg = u.mapGrid;
            }
            catch (Exception)
            {
                continue;
            }

            if (mg == null)
            {
                broken++;
                sb.Append($"\n    单位(队伍={ReadTeamId(u)}) mapGrid=null");
                continue;
            }

            BattleUnit? back;

            try
            {
                back = mg.battleUnit;
            }
            catch (Exception)
            {
                continue;
            }

            if (back == null || back.Pointer != u.Pointer)
            {
                broken++;
                sb.Append(
                    $"\n    单位(队伍={ReadTeamId(u)}) mapGrid=(r{mg.row},c{mg.column}) " +
                    $"但该格 battleUnit={(back == null ? "null" : "别人")}");
            }
        }

        LogInfo(() =>$"[审计·单位] {label} 总数={total} 存活={alive} 不一致={broken}{sb}");
    }

    /// <summary>
    /// 落点判定探针（只读）：记录每次点击落在哪一格、该格是否被占、
    /// 以及它是否在 <c>moveRangeGridUnits</c>（<c>+0x1d8</c>）里。
    ///
    /// <para>
    /// 用户实测「站着人的格子是亮的、点击也真能走上去」，而
    /// <c>GetMoveRangeGrids</c> 的探针从未在其输出看到非起点的占位格。
    /// 本探针把「点击被判定为合法」与「Contains 为真」是否等价测出来：
    /// 若出现 <c>在范围内=否</c> 却仍走成，则落点判定被绕过了。
    /// </para>
    /// </summary>
    internal static void BattleController_BattleGridClicked_Prefix(
        BattleController __instance,
        GameObject clickedGrid)
    {
        try
        {
            if (__instance == null || clickedGrid == null)
            {
                return;
            }

            int state;

            try
            {
                state = (int)__instance.nowActiveState;
            }
            catch (Exception)
            {
                return;
            }

            GridUnitData? g = ResolveGridFromObject(__instance, clickedGrid);

            if (g == null)
            {
                return;
            }

            BattleUnit? u = g.battleUnit;
            string occ = u == null ? "空" : $"占用(队伍={ReadTeamId(u)})";

            int inRange;

            try
            {
                Il2CppSystem.Collections.Generic.List<GridUnitData>? range =
                    __instance.moveRangeGridUnits;

                inRange = range != null && range.Contains(g) ? 1 : 0;
            }
            catch (Exception)
            {
                inRange = -1;
            }

            if (Diagnostics.Value)
            {
                LogInfo(() =>
                    $"[点击] (r{g.row},c{g.column}) state={state} {occ} " +
                    $"在范围内={(inRange == 1 ? "是" : inRange == 0 ? "否" : "读取失败")}");
            }

            // 只在"点到被判定为空的格子"时做全量登记审计 —— 这正是重叠的入口。
            if (u == null)
            {
                AuditOccupancy(__instance, g);
            }
        }
        catch (Exception e)
        {
            Log.Warning($"[点击] 探针异常：{e.Message}");
        }
    }

    /// <summary>
    /// 从被点的 <c>GameObject</c> 反查它属于哪一格。
    /// 用游戏自己的 <c>GridUnitController</c> 组件，避免猜层级结构。
    /// </summary>
    private static GridUnitData? ResolveGridFromObject(
        BattleController controller,
        GameObject clickedGrid)
    {
        try
        {
            GridUnitController? c = clickedGrid.GetComponent<GridUnitController>();

            return c?.gridData;
        }
        catch (Exception)
        {
            return null;
        }
    }

    // =================================================================
    // 【诊断区】只读探针 —— 不改变任何游戏行为，全部受 diagnostics 门控
    // =================================================================

    /// <summary>
    /// 渲染探针（只读）：<c>GridUnitData.set_GridRenderType</c> 是"把某格染成某种状态"
    /// 的唯一出口。可达区域高亮走 <c>Range = 6</c>，悬停路径走 <c>Path = 4</c>。
    ///
    /// <para>
    /// 目的：用户实测「站着人的格子是亮的、且能走上去」，而
    /// <c>GetMoveRangeGrids</c> 的探针从未在其输出里看到非起点的占位格 ——
    /// 两者矛盾。这里直接记录<b>实际被点亮的每一格</b>及其占用情况，
    /// 用运行时事实取代对 <c>+0x1d8</c> 内容的推理。
    /// </para>
    /// </summary>
    internal static void GridUnitController_set_GridRenderType_Prefix(
        GridUnitController __instance,
        GridRenderType value)
    {
        try
        {
            if (__instance == null)
            {
                return;
            }

            // 只关心"可达区域"与"悬停路径"两类染色，其余值（Normal/Selected/...）跳过。
            int v = (int)value;

            if (v != 6 && v != 4 && v != 5)
            {
                return;
            }

            // GridUnitController 不是 GridUnitData —— 要先回到它所属的格子，
            // 才能读出坐标与占用情况。
            GridUnitData? grid = __instance.gridData;

            if (grid == null)
            {
                if (Diagnostics.Value)
                {
                    LogInfo(() =>$"[染色] type={v} 但 GridUnitData 为空。");
                }
                return;
            }

            BattleUnit? u = grid.battleUnit;
            string occ;

            if (u == null)
            {
                occ = "空";
            }
            else
            {
                bool alive;

                try
                {
                    alive = u.IsAlive;
                }
                catch (Exception)
                {
                    alive = false;
                }

                occ = alive ? $"占用(队伍={ReadTeamId(u)})" : "尸体";
            }

            // 染色日志量极大（每次刷新范围都会逐格打印），受 diagnostics 门控。
            if (Diagnostics.Value)
            {
                LogInfo(() =>
                    $"[染色] (r{grid.row},c{grid.column}) " +
                    $"type={v}（{(v == 6 ? "Range" : v == 4 ? "Path" : "Searched")}） {occ}");
            }
        }
        catch (Exception e)
        {
            Log.Warning($"[染色] 探针异常：{e.Message}");
        }
    }

    private static int ReadTeamId(BattleUnit unit)
    {
        try
        {
            BattleTeam? team = unit.battleTeam;

            if (team == null)
            {
                return int.MinValue;
            }

            return team.ID;
        }
        catch (Exception)
        {
            return int.MinValue;
        }
    }

    /// <summary>
    /// 取邻格，走游戏自己的 O(1) 查询。
    ///
    /// <para>
    /// **不要**手写遍历 <c>normalGrids</c> —— 那是 O(n) 的托管/native 跨界循环，
    /// 在 AI 回合里被频繁调用足以拖死游戏（本项目踩过一次）。
    /// </para>
    /// </summary>
    private static GridUnitData? TryGetNeighbor(BattleMapData map, int row, int col, int dir)
    {
        try
        {
            return map.GetGridDataByDir(row, col, dir);
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static void DumpMoveRange(
        BattleMapData map,
        int row,
        int column,
        int minRange,
        int maxRange,
        Il2CppSystem.Collections.Generic.List<GridUnitData> grids,
        int selfTeamID)
    {
        int total = grids.Count;

        int occupied = 0, allyOccupied = 0, enemyOccupied = 0;
        int withObstacle = 0, wall = 0;
        int withSpe = 0;

        var counts = new System.Collections.Generic.Dictionary<string, int>();

        for (int i = 0; i < total; i++)
        {
            GridUnitData? g = grids[i];
            if (g == null)
            {
                continue;
            }

            BattleUnit? unit = g.battleUnit;
            ObstacleData? obs = g.obstale;
            SpeGridObjData? spe = g.speGridObjData;

            if (unit != null)
            {
                occupied++;
                BattleTeam? team = unit.battleTeam;
                int teamId = team != null ? team.ID : -999;
                if (teamId == selfTeamID)
                {
                    allyOccupied++;
                }
                else
                {
                    enemyOccupied++;
                }
            }

            if (obs != null)
            {
                withObstacle++;
                if (obs.obstalceType == ObstacleType.Wall)
                {
                    wall++;
                }
            }

            // 注意：speGridObjData 在 speGridObjType == None 时也可能非空。
            if (spe != null && !IsNoneSpeType(spe))
            {
                withSpe++;
            }

            string key = g.gridType.ToString() ?? "?";
            counts[key] = counts.TryGetValue(key, out int c) ? c + 1 : 1;
        }

        var sbTypes = new StringBuilder();
        foreach (var kv in counts)
        {
            sbTypes.Append(' ').Append(kv.Key).Append('=').Append(kv.Value);
        }

        LogInfo(() =>
            $"[探针] GetMoveRangeGrids from(r{row},c{column}) min={minRange} max={maxRange} " +
            $"selfTeamID={selfTeamID} → Count={total} " +
            $"单位={occupied}(我{allyOccupied}/敌{enemyOccupied}) " +
            $"障碍={withObstacle}(墙{wall}) 特殊元素={withSpe} gridType[{sbTypes}]");

        if (!DumpAllGrids.Value)
        {
            return;
        }

        try
        {
            var sb = new StringBuilder();

            sb.AppendLine(
                $"### from(r{row},c{column}) min={minRange} max={maxRange} selfTeamID={selfTeamID} " +
                $"Count={total} 单位={occupied}(我{allyOccupied}/敌{enemyOccupied}) " +
                $"障碍={withObstacle}(墙{wall}) 特殊元素={withSpe}");

            sb.AppendLine("--- 移动范围内的格子 ---");
            sb.AppendLine(Header());
            AppendGrids(sb, grids, selfTeamID);

            Il2CppSystem.Collections.Generic.List<GridUnitData>? all = map.normalGrids;
            if (all != null)
            {
                sb.AppendLine();
                sb.AppendLine($"--- normalGrids 全部格子（共 {all.Count}）---");
                sb.AppendLine(Header());
                AppendGrids(sb, all, selfTeamID);
            }

            // 写入游戏根目录，避免 AppContext.BaseDirectory 在 Proton 下的相对回退问题。
            string root = MelonLoader.Utils.MelonEnvironment.GameRootDirectory;
            string path = System.IO.Path.Combine(root, "FriendlyNoclip_Dump.txt");
            System.IO.File.AppendAllText(path, sb.ToString());
        }
        catch (Exception e)
        {
            Log.Warning($"写探针明细失败：{e.Message}");
        }
    }

    private static bool IsNoneSpeType(SpeGridObjData spe)
    {
        try
        {
            return spe.speGridObjType == SpeGridObjType.None;
        }
        catch
        {
            return false;
        }
    }

    private static string Header()
    {
        return "row\tcol\tgridType\tpasses\tunit\tunitTeam\talive\tobstype\tobsTeam\tspeType\tspeTeam\tspeOnGround";
    }

    private static void AppendGrids(
        StringBuilder sb,
        Il2CppSystem.Collections.Generic.List<GridUnitData> list,
        int selfTeamID)
    {
        for (int i = 0; i < list.Count; i++)
        {
            GridUnitData? g = list[i];
            if (g == null)
            {
                continue;
            }

            BattleUnit? unit = g.battleUnit;
            ObstacleData? obs = g.obstale;
            SpeGridObjData? spe = g.speGridObjData;

            string unitTeam = "-";
            string alive = "-";

            if (unit != null)
            {
                BattleTeam? team = unit.battleTeam;
                unitTeam = team != null ? team.ID.ToString() : "?";
                unitTeam += (team != null && team.ID == selfTeamID) ? "(我)" : "(敌)";

                try
                {
                    alive = unit.IsAlive ? "Y" : "N";
                }
                catch
                {
                    alive = "?";
                }
            }

            sb.Append(g.row).Append('\t')
              .Append(g.column).Append('\t')
              .Append(g.gridType).Append('\t')
              .Append(g.passes).Append('\t')
              .Append(unit != null ? "Y" : "-").Append('\t')
              .Append(unitTeam).Append('\t')
              .Append(alive).Append('\t')
              .Append(obs != null ? obs.obstalceType.ToString() : "-").Append('\t')
              .Append(obs != null ? obs.teamID.ToString() : "-").Append('\t')
              .Append(spe != null ? spe.speGridObjType.ToString() : "-").Append('\t')
              .Append(spe != null ? spe.teamID.ToString() : "-").Append('\t')
              .Append(spe != null ? (spe.onGround ? "Y" : "N") : "-")
              .AppendLine();
        }
    }
}
