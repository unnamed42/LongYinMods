using System;
using System.Collections.Generic;
using System.Reflection;
using System.Runtime.InteropServices;
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

    /// <summary>允许穿过友方单位所在的格子。</summary>
    internal static MelonPreferences_Entry<bool> AllowFriendly = null!;


    /// <summary>
    /// 最近一次 <c>GetMoveRangeGrids</c> 调用时的地图实例。
    /// 邻格查询要靠它（<c>GetGridDataByDir</c> 是实例方法）。
    /// </summary>
    private static BattleMapData? _activeMap;

    /// <summary>输出诊断日志（探针阶段默认开启）。</summary>
    internal static MelonPreferences_Entry<bool> Diagnostics = null!;

    /// <summary>每个移动范围计算都 dump 全部网格（日志量大）。</summary>
    internal static MelonPreferences_Entry<bool> DumpAllGrids = null!;

    /// <summary>原生级探针：读取过滤点机器码，确认签名与偏移。</summary>
    internal static MelonPreferences_Entry<bool> NativeProbeEnabled = null!;

    /// <summary>是否启用原地改写探针（会让敌我都能穿）。</summary>
    internal static MelonPreferences_Entry<bool> NativePatchEnabled = null!;

    /// <summary>原生 detour：只让**敌方**阻挡，友方可穿过。</summary>
    internal static MelonPreferences_Entry<bool> NativeDetourEnabled = null!;

    private HarmonyLib.Harmony? _harmony;

    internal static MelonLogger.Instance Log = null!;

    public override void OnInitializeMelon()
    {
        Log = LoggerInstance;

        Category = MelonPreferences.CreateCategory(CategoryId, CategoryDisplay);

        // 已退役：早期版本用它驱动「补格 BFS / 剔除占位格」，两条路都被证伪 ——
        // GetMoveRangeGrids 从不输出被占据的格子（起点除外，那是原版语义），
        // 所以托管层没有任何可做的减法。穿越友方完全由 native_detour 负责。
        // 条目保留只为不破坏已有 cfg。
        AllowFriendly = Category.CreateEntry(
            "allow_friendly", false,
            "（已退役）托管层补丁",
            "保留项，当前不产生任何行为。穿越友方由 native_detour 负责。");

        Diagnostics = Category.CreateEntry(
            "diagnostics", false,
            "诊断日志", "打印移动范围与网格的详细内容（日志量大，排查时才开）。");

        DumpAllGrids = Category.CreateEntry(
            "dump_all_grids", false,
            "Dump 全部网格", "每次调用都输出整张地图的网格明细（极慢，排查时才开）。");

        NativeProbeEnabled = Category.CreateEntry(
            "native_probe", true,
            "原生探针", "定位并校验原生过滤点（只读，不修改游戏代码）。");

        // 行为探针：原地等长 NOP 掉 Navigate 的「存活即阻挡」判定。
        // 默认关闭 —— 它会让敌我都能穿，只是用来验证「原地改写不崩」。
        NativePatchEnabled = Category.CreateEntry(
            "native_patch_probe", false,
            "原生原地改写（探针）",
            "原地 NOP 掉寻路的阻挡判定。敌我都能穿，仅用于验证改动可行性。");

        // 原生 detour：真正的实现。用 Dobby 在 Navigate 内部装 detour，
        // 只把**敌方**单位当阻挡，友方可以穿过。
        NativeDetourEnabled = Category.CreateEntry(
            "native_detour", true,
            "原生 detour（只穿友方）",
            "在 MapNavigator.Navigate 内装 detour：友方格子可通过，敌方仍然阻挡。");

        _harmony = new HarmonyLib.Harmony(HarmonyId);

        if (NativeProbeEnabled.Value)
        {
            NativeProbe.Verify();
        }

        // 原地等长改写：先做可行性验证（不分配、不跳转）。
        // 与 NativeDetour 互斥 —— 两者改的是同一个地址，
        // 谁先装谁赢，另一方会因字节校验失败而拒绝。见下方 detour 分支。
        if (NativePatchEnabled.Value && !NativeDetourEnabled.Value)
        {
            NativePatchProbe.Install();
        }

        // 正式的只穿友方实现：Dobby detour + 手写 stub。
        if (NativeDetourEnabled.Value)
        {
            if (NativePatchEnabled.Value)
            {
                LoggerInstance.Warning(
                    "native_patch_probe 与 native_detour 同时开启：两者改写同一地址，已跳过原地改写探针，只装 detour。");
            }

            NativeDetour.Install();
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

        // ★★ 「穿越友方」的正式实现：OnEnter 的**原生入口** detour。
        //
        // 不能再用 Harmony：实测 Harmony 绑定的是 Il2CppInterop 的托管 DMD 包装
        // （日志 IL=6fff97ab04b8，与 OnLeave 的 6fff97ab04c0 同区），
        // 而 BattleUnit.EnterGrid 里是一条 `call 0x180873b20` 直达函数体
        // （全镜像唯一调用点），**不经过 DMD 包装** → Harmony 补丁在真实移动中零触发。
        //
        // 判据：调用者是 native 还是 managed。BattleGridClicked 等由托管侧调用的方法
        // 走 DMD，所以那些 Harmony 补丁正常；OnEnter 由 native 调用，必须 native hook。
        if (NativeDetourEnabled.Value)
        {
            NativeOnEnterDetour.Install();
        }

        LoggerInstance.Msg(
            $"FriendlyNoclip 初始化完成：挂载 {patched} 个补丁" +
            $"，原地改写探针={(NativePatchProbe.Installed ? "已启用" : "未启用")}" +
            $"，Navigate detour={(NativeDetour.Installed ? "已启用" : "未启用")}" +
            $"，OnEnter detour={(NativeOnEnterDetour.Installed ? "已启用" : "未启用")}。");
    }


    public override void OnDeinitializeMelon()
    {
        // 先还原原生改写，再摘 Harmony 补丁。
        NativeDetour.Uninstall();
        NativeOnEnterDetour.Uninstall();
        NativePatchProbe.Uninstall();
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
                $"(IL={target.MethodHandle.GetFunctionPointer().ToInt64():x}) -> {patchName}");
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
                $"(IL={target.MethodHandle.GetFunctionPointer().ToInt64():x}) -> {patchName}");
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
            if (__instance == null)
            {
                return;
            }

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

        Log.Msg(
            $"[登记] {op} 格(r{row},c{col}) mapID={SafeMapId(grid)} | " +
            $"改前={before} | 写入={(unit == null ? "null" : $"队伍={ReadTeamId(unit)},ptr=0x{unit.Pointer.ToInt64():x}")} | " +
            $"{self} | 调用者={CallerReturnAddress()}");

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

    /// <summary>
    /// 取调用者的返回地址。
    ///
    /// <para>
    /// 用 <see cref="System.Diagnostics.StackTrace"/> 且**跳过本 mod 的帧**，
    /// 这样拿到的就是游戏代码里的地址；再减去模块基址即得 RVA，
    /// 可与 `script.json` 的调用点表直接比对（本项目的 VAs 都是静态 VA，
    /// 故用 <c>rva + 0x180000000</c> 去对照）。
    /// </para>
    /// </summary>
    private static string CallerReturnAddress()
    {
        try
        {
            var st = new System.Diagnostics.StackTrace(0, false);
            var sb = new System.Text.StringBuilder();

            int shown = 0;

            for (int i = 0; i < st.FrameCount && shown < 3; i++)
            {
                System.Diagnostics.StackFrame? f = st.GetFrame(i);

                if (f == null)
                {
                    continue;
                }

                MethodBase? m = f.GetMethod();

                if (m == null)
                {
                    continue;
                }

                // 跳过本 mod 自己的帧。
                if (m.DeclaringType != null &&
                    m.DeclaringType.Namespace == "Unnamed42.FriendlyNoclip")
                {
                    continue;
                }

                sb.Append(
                    $"#{shown} {m.DeclaringType?.Name}.{m.Name}" +
                    $"(IL=0x{f.GetILOffset():x}) ");

                shown++;
            }

            return shown == 0 ? "未知" : sb.ToString().TrimEnd();
        }
        catch (Exception e)
        {
            return $"取栈失败:{e.Message}";
        }
    }

    // ------------------------------------------------------------------
    // 探针 1：GetMoveRangeGrids Postfix
    // ------------------------------------------------------------------

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
            Log.Msg(
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
            Log.Msg("[审计·格] _activeMap 为空，跳过。");
            return;
        }

        Il2CppSystem.Collections.Generic.List<GridUnitData>? grids;

        try
        {
            grids = map.normalGrids;
        }
        catch (Exception e)
        {
            Log.Msg($"[审计·格] 读 normalGrids 失败：{e.Message}");
            return;
        }

        if (grids == null)
        {
            Log.Msg("[审计·格] normalGrids 为空。");
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

        Log.Msg(
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

            Log.Msg(
                $"[审计·对照] (r{row},c{col}) mapGrids→{pa} {occA} | " +
                $"normalGrids→{pb} {occB} | 同一对象={(same ? "是" : "否")}");

            // 被点格自身也算一路：它来自 GridUnitController.gridData（视觉对象）。
            Log.Msg(
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
                Log.Msg("[普查] normalGrids 为空，跳过。");
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

            Log.Msg(
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
            Log.Msg($"[审计·单位] {label}=null");
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

        Log.Msg($"[审计·单位] {label} 总数={total} 存活={alive} 不一致={broken}{sb}");
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

            Log.Msg(
                $"[点击] (r{g.row},c{g.column}) state={state} {occ} " +
                $"在范围内={(inRange == 1 ? "是" : inRange == 0 ? "否" : "读取失败")}");

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
                Log.Msg($"[染色] type={v} 但 GridUnitData 为空。");
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

            Log.Msg(
                $"[染色] (r{grid.row},c{grid.column}) " +
                $"type={v}（{(v == 6 ? "Range" : v == 4 ? "Path" : "Searched")}） {occ}");
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

        Log.Msg(
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
