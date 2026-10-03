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

    /// <summary><c>ObstacleType.Wall</c>。</summary>
    private const int ObstacleTypeWall = 1;

    /// <summary>
    /// 普通格子的 <c>passes</c> 取值。实测 <c>normalGrids</c> 全部为 15（无例外），
    /// 所以这是「恢复成一个游戏本身就在用的合法值」，不是编造新值。
    /// </summary>
    private const int PassesWalkable = 15;

    /// <summary>已改写的格子及其**原值**，用于精确回滚。</summary>
    private static readonly List<(IntPtr Grid, int Original)> _modified = new();

    /// <summary>本轮是否已经置位（避免同一场战斗里反复遍历）。</summary>
    private static bool _applied;

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
            if (_applied && mapPtr != _lastMap)
            {
                Restore();
            }

            if (_applied && mapPtr == _lastMap)
            {
                return;
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

                int teamID = Marshal.ReadInt32(obstaclePtr + OffObstacleTeam);

                if (teamID != selfTeamID)
                {
                    continue;   // 他方城墙：保持原样
                }

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

            _applied = true;
            _lastMap = mapPtr;

            Plugin.Log.Msg(
                $"[城墙通行] 已放行 {changed} 面己方城墙（teamID={selfTeamID}）：" +
                $"passes {PassesWalkable}，可跨越但不入高亮。" +
                $"中立障碍与他方城墙未改动。");
        }
        catch (Exception e)
        {
            Plugin.Log.Warning($"[城墙通行] 置位异常：{e.Message}");
        }
    }

    /// <summary>
    /// 把 <see cref="Apply"/> 改过的格子恢复成原值。战斗结束时调用。
    /// </summary>
    internal static void Restore()
    {
        if (_modified.Count == 0)
        {
            _applied = false;
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

        Plugin.Log.Msg($"[城墙通行] 已恢复 {ok}/{_modified.Count} 面城墙的 passes。");

        _modified.Clear();
        _applied = false;
        _lastMap = IntPtr.Zero;
    }
}
