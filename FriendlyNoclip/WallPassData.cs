using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using Il2Cpp;
using Il2CppInterop.Runtime;

namespace Unnamed42.FriendlyNoclip;

/// <summary>
/// 「己方城墙可跨越」的数据层实现 —— 直接改写 <c>GridUnitData.passes</c>。
///
/// <para><b>★★ 为什么是 <c>passes</c> 而不是 hook</b></para>
///
/// <para>
/// <c>MapNavigator.Navigate</c> 的**搜索深度上限**是：
/// </para>
/// <code>
/// 0x180a8d6df  call 0x1808CA250      ; 符号名 BattleMapData.get_GridCount
/// 0x180a8d6eb  mov  [rsp+0x38], eax  ; 存为循环上界 iVar3
/// 0x1808CA250: mov eax,[rcx+0x24] ; imul eax,[rcx+0x20] ; ret
/// </code>
///
/// <para>
/// ⚠️ 这里 <c>rcx</c> 实际是 **<c>from</c>（一个 <c>GridUnitData</c>）**，
/// 不是 <c>BattleMapData</c>（序言 <c>mov rsi,rdx</c> 已核实）。
/// 于是读的是 <c>GridUnitData.row (+0x24)</c> × <c>GridUnitData.passes (+0x20)</c>。
/// </para>
///
/// <para>
/// 而 **<c>GridUnitData.passes</c> 对障碍格恒为 <c>0</c>**（实测：空地全 15、障碍全 0，
/// 各自只有 1 种取值 —— 它其实是个「是否可行走」的类别标记）。
/// 所以从城墙格出发时上限 = <c>row × 0 = 0</c> → **主循环一次都不执行**
/// → 城墙格永远无法作为**中转节点** → 跨墙不可能。
/// </para>
///
/// <para>
/// 把己方城墙的 <c>passes</c> 写成 15 之后一切正常（实测）：跨墙变成直线路径、
/// 高亮里出现墙对面的格子，而**城墙自己仍不进高亮**（<c>GetMoveRangeGrids</c>
/// 里有独立的 <c>gridType != 2</c> 拦阻）—— 正是需求要的
/// 「**不可停留，但可以跨越**」。
/// </para>
///
/// <para><b>★ 定向写入，绝不批量</b></para>
/// <para>
/// 只写满足 <c>obstacleType == Wall &amp;&amp; teamID == selfTeamID</c> 的格子。
/// 按类别批量写会连**中立造景**（树/木箱/雕像，同样是 <c>gridType==2</c>）一起放行 ——
/// 实测：只改己方城墙时，21 个中立障碍**全部仍被正常阻挡**。
/// </para>
///
/// <para><b>为什么要恢复</b></para>
/// <para>
/// <c>passes</c> 的**写入者至今未定位**，也无法确认除 <c>Navigate</c> 外还有谁读它。
/// 所以采取「进战斗时置位、战斗结束恢复」的可回滚策略，
/// 把改动限制在战斗期间，不在存档/长驻状态里留痕。
/// </para>
/// </summary>
internal static class WallPassData
{
    /// <summary><c>GridUnitData.passes</c>（int32）的偏移。</summary>
    private const int OffPasses = 0x20;

    /// <summary><c>GridUnitData.obstale</c>（<c>ObstacleData*</c>）的偏移。</summary>
    private const int OffObstale = 0x30;

    /// <summary><c>ObstacleData.obstalceType</c>（int32）的偏移。</summary>
    private const int OffObstacleType = 0x10;

    /// <summary><c>ObstacleData.teamID</c>（int32）的偏移。</summary>
    private const int OffObstacleTeam = 0x2C;

    /// <summary>
    /// <c>ObstacleData.obstacleHp</c>（float32）的偏移。
    ///
    /// <para>
    /// 【为什么要看它】城墙被击毁后，游戏**不会**把 <c>obstale</c> 置 null，
    /// 也不同步改 <c>obstalceType</c>（仍是 <c>Wall</c>）——
    /// 它只把格子改成 <c>Normal</c> 并把 <c>hp</c> 打到负数。
    /// 实机实测（2026-10，城墙被轰毁）：
    /// </para>
    /// <code>
    /// (15,13) gridType=Normal passes=15 obstale=Wall hp=-5.1   ← 已毁，实为空地
    /// (14,13) gridType=Obstacle passes=15 obstale=Wall hp=260  ← 完好
    /// </code>
    /// <para>
    /// 所以判「这还是一面墙吗」必须连 <c>hp &gt; 0</c> 一起看，
    /// 否则会把已毁的城墙当成完好城墙拦下。
    /// </para>
    /// </summary>
    private const int OffObstacleHp = 0x24;

    /// <summary>
    /// <c>ObstacleType.Wall</c>。
    ///
    /// <para>
    /// 公开给 <c>Plugin.BattleController_GenerateMovePath_Prefix</c> 用（城墙禁停）——
    /// 两处必须用**同一个判据**，否则「放行哪些墙」与「禁停哪些墙」会不一致。
    /// </para>
    /// </summary>
    internal const int ObstacleTypeWall = 1;

    /// <summary>
    /// 普通格子的 <c>passes</c> 取值。实测 <c>normalGrids</c> 全部为 15（无例外），
    /// 所以这是「恢复成一个游戏本身就在用的合法值」，不是编造新值。
    /// </summary>
    private const int PassesWalkable = 15;

    /// <summary>已改写的格子及其**原值**，用于精确回滚。</summary>
    private static readonly List<(IntPtr Grid, int Original)> _modified = new();

    /// <summary>
    /// 本张地图上**已经置位过的目标**（队伍 ID；<see cref="AllWallOwners"/> 表示「按墙主」）。
    ///
    /// <para>
    /// 【为什么不是一个 bool】早先用单个 <c>_applied</c> 标记「本图已做过」，
    /// 结果 postfix 的第二次 <c>Apply(playerTeamID)</c> **永远直接返回** ——
    /// 「玩家队伍与玩家操控队伍不同时把另一个也放行」这条设计**从未执行过**。
    /// </para>
    /// </summary>
    private static readonly HashSet<int> _appliedKeys = new();

    /// <summary>
    /// 「按墙主放行」的键：不过滤队伍 —— 每面墙都属于它自己的队伍。
    /// 用于**无玩家的剧情战斗**（见 <see cref="ApplyForWallOwners"/>）。
    /// </summary>
    internal const int AllWallOwners = int.MinValue;

    /// <summary>上次置位时的地图实例，用于检测换场。</summary>
    private static IntPtr _lastMap;

    /// <summary>已改写格数（供日志/诊断）。</summary>
    internal static int ModifiedCount => _modified.Count;

    /// <summary>
    /// 对**己方城墙**置 <c>passes</c>，使其可作中转格。
    /// 幂等：同一张地图只做一次；换地图会自动重新做。
    /// </summary>
    internal static void Apply(BattleMapData map, int selfTeamID)
    {
        if (map == null)
        {
            return;
        }

        try
        {
            IntPtr mapPtr = IL2CPP.Il2CppObjectBaseToPtr(map);

            // 换场（或首次）→ 先恢复旧地图上的改动，再重新置位。
            if (mapPtr != _lastMap)
            {
                Restore();
                _lastMap = mapPtr;
            }

            if (_appliedKeys.Contains(selfTeamID))
            {
                return;   // 本图 + 本目标已做过（换目标仍会继续）
            }

            var obstacles = map.obstacleGrids;

            if (obstacles == null)
            {
                return;
            }

            int changed = 0;

            foreach (GridUnitData grid in obstacles)
            {
                if (grid == null)
                {
                    continue;
                }

                IntPtr gridPtr = IL2CPP.Il2CppObjectBaseToPtr(grid);

                if (gridPtr == IntPtr.Zero)
                {
                    continue;
                }

                IntPtr obstaclePtr = Marshal.ReadIntPtr(gridPtr + OffObstale);

                if (obstaclePtr == IntPtr.Zero)
                {
                    continue;
                }

                int obstacleType = Marshal.ReadInt32(obstaclePtr + OffObstacleType);

                if (obstacleType != ObstacleTypeWall)
                {
                    continue;   // 中立造景等：保持原样
                }

                if (selfTeamID != AllWallOwners)
                {
                    int teamID = Marshal.ReadInt32(obstaclePtr + OffObstacleTeam);

                    if (teamID != selfTeamID)
                    {
                        continue;   // 他方城墙：保持原样
                    }
                }
                // selfTeamID == AllWallOwners：**不过滤队伍** ——
                // 每面墙都属于它自己的队伍，按「墙主可跨」放行。

                int original = Marshal.ReadInt32(gridPtr + OffPasses);

                if (original == PassesWalkable)
                {
                    continue;   // 已经是目标值（例如游戏自己设过）
                }

                if (NativeMemory.WriteInt32(gridPtr + OffPasses, PassesWalkable))
                {
                    _modified.Add((gridPtr, original));
                    changed++;
                }
            }

            _appliedKeys.Add(selfTeamID);
            _lastMap = mapPtr;

            string scope = selfTeamID == AllWallOwners
                ? "按墙主放行（无玩家）"
                : $"teamID={selfTeamID}";

            Plugin.LogInfo(() =>
                $"[城墙通行] 已放行 {changed} 面城墙（{scope}）：" +
                $"passes {PassesWalkable}，可跨越但不入高亮。");
        }
        catch (Exception e)
        {
            Plugin.Log.Warning($"[城墙通行] 置位异常：{e.Message}");
        }
    }
    /// <summary>
    /// **无玩家的剧情战斗**专用：按「墙主」放行 —— 每面墙都属于它自己的队伍。
    ///
    /// <para>
    /// 【为什么要这个回退】实测：无玩家的战斗里
    /// <c>BattleController.GetPlayerControlTeamID()</c> 与 <c>GetPlayerTeam()</c>
    /// 都**不返回「没有」**，而是静默返回 team 0（攻方）。
    /// 而城墙全属**守方**（team 1）⇒ 一面都不匹配 ⇒
    /// 配置里承诺的「守方 AI 自动获得同样能力」在剧情攻城战里**从未生效**。
    /// </para>
    ///
    /// <para>
    /// 剧情战斗没有玩家角色可供参照，所以按「每面墙属于它自己的队伍」放行 ——
    /// 在攻城图里就等于守方（唯 一持有城墙的一方）。
    /// </para>
    /// </summary>
    internal static void ApplyForWallOwners(BattleMapData map)
    {
        Apply(map, AllWallOwners);
    }

    /// <summary>
    ///
    /// <para>
    /// 【为什么单独抽出来】<see cref="Apply"/> 用这套偏移决定「放行哪面墙」，
    /// 而城墙禁停钩子用同一套决定「禁停哪面墙」—— <b>两处必须看同一个字段</b>，
    /// 否则会出现「放行了但不让停」「没放行却按城墙拦」这类不一致。
    /// 抽成一个方法就不会走偏。
    /// </para>
    ///
    /// <para>
    /// 与 <see cref="Apply"/> 一样用<b>原生指针读</b>，不走托管代理：
    /// 代理属性名带游戏自己的拼写错误（<c>obstalceType</c>），
    /// 一旦上游改名就会静默读到别的字段。
    /// </para>
    /// </summary>
    /// <param name="grid">待判定的格子；为 null 或非城墙时返回 false。</param>
    /// <param name="teamID">城墙所属队伍；非城墙时为 0。</param>
    internal static bool TryGetWallTeam(GridUnitData? grid, out int teamID)
    {
        teamID = 0;

        try
        {
            if (grid == null)
            {
                return false;
            }

            IntPtr gridPtr = IL2CPP.Il2CppObjectBaseToPtr(grid);

            if (gridPtr == IntPtr.Zero)
            {
                return false;
            }

            IntPtr obstaclePtr = Marshal.ReadIntPtr(gridPtr + OffObstale);

            if (obstaclePtr == IntPtr.Zero)
            {
                return false;
            }

            if (Marshal.ReadInt32(obstaclePtr + OffObstacleType) != ObstacleTypeWall)
            {
                return false;
            }
            // ★ 还必须确认这面墙**仍然存在**。
            //
            // 城墙被击毁后，游戏只把 gridType 改成 Normal、把 hp 打到负数，
            // 并**不**清 obstale、也**不**改 obstalceType —— 所以单看类型会把
            // 「废墟」当成完好城墙。实测 (15,13)/(17,13) 就是 hp<0 的废墟。
            float hp = BitConverter.ToSingle(
                BitConverter.GetBytes(Marshal.ReadInt32(obstaclePtr + OffObstacleHp)), 0);

            if (!(hp > 0f))
            {
                return false;   // 已毁（hp<=0）或读异常：当作不是墙 -> 放行
            }

            teamID = Marshal.ReadInt32(obstaclePtr + OffObstacleTeam);
            return true;
        }
        catch
        {
            // 读不了就一律当作「不是城墙」—— 让调用方放行，而不是拦错。
            return false;
        }
    }

    /// <summary>
    /// 把 <see cref="Apply"/> 改过的格子恢复成原值。战斗结束时调用。
    /// </summary>
    internal static void Restore()
    {
        if (_modified.Count == 0)
        {
            _appliedKeys.Clear();
            _lastMap = IntPtr.Zero;
            return;
        }

        int ok = 0;

        foreach ((IntPtr grid, int original) in _modified)
        {
            if (NativeMemory.WriteInt32(grid + OffPasses, original))
            {
                ok++;
            }
        }

        Plugin.LogInfo(() =>$"[城墙通行] 已恢复 {ok}/{_modified.Count} 面城墙的 passes。");

        _modified.Clear();
        _appliedKeys.Clear();
        _lastMap = IntPtr.Zero;
    }
}
