using System;
using Il2Cpp;

namespace Unnamed42.BookStorageCap;

/// <summary>
/// 全部改写逻辑集中在这里 —— 三个入口（游戏重算后 / UI 打开后 / 定时扫描）
/// 都只走 <see cref="ApplyToForce"/> / <see cref="ApplyToList"/>，
/// 保证「改哪一层」这件事只有一处定义。
/// </summary>
internal static class Core
{
    /// <summary>
    /// 配置值 → 目标上限。返回 <c>null</c> 表示「不干预」。
    ///
    /// <para>语义刻意做成三态：<c>&gt;0</c> 设值；<c>-1</c> 是游戏自己的「不限」哨兵
    /// （实测依据：<c>governStorage</c> 在 <c>maxWeight=-1</c> 下持有 104 负重 ⇒ -1 不是 0；
    /// ⚠️ 但「-1 对 <b>bookStorage</b> 也等于不限」<b>尚未实测</b>，别当成已验证）；
    /// 其余值（含 0 / 负数 / NaN / 无穷）视为写错、不碰数据 ——
    /// TOML 里 <c>inf</c> 是合法字面量，不拦会把容量标签显示成很大的负数。</para>
    /// </summary>
    internal static float? TargetValue()
    {
        float v = Plugin.MaxWeight.Value;

        if (float.IsNaN(v) || float.IsInfinity(v))
        {
            Plugin.LogOnce(
                "bad-max-weight",
                $"[藏经阁] max_weight={v} 不是有限数，本次不干预游戏数据。");

            return null;
        }

        if (v > 0f)
        {
            return v;
        }

        if (v == -1f)
        {
            return -1f;
        }

        Plugin.LogOnce(
            "bad-max-weight",
            $"[藏经阁] max_weight={v} 不是合法值（>0 或 -1），本次不干预游戏数据。");

        return null;
    }

    // ── 三个入口 ─────────────────────────────────────────────────────────────

    /// <summary>入口 1：<c>GameController.CountForceData</c> 的 Postfix —— 游戏刚重算完，立刻覆盖回去。</summary>
    internal static void OnForceCounted(ForceData? force)
    {
        if (!Plugin.Enabled.Value || force == null)
        {
            return;
        }

        float? target = TargetValue();

        if (target == null)
        {
            return;
        }

        if (Plugin.OnlyPlayerForce.Value && !IsPlayerForce(force))
        {
            return;
        }

        ApplyToForce(force, target.Value, "CountForceData");
    }

    /// <summary>
    /// 入口 2：藏经阁界面打开/刷新之后。
    ///
    /// <para>调用方已按 <c>ItemListType.BookType</c> 过滤（全游戏只有藏经阁传这个值），
    /// 这里再做一次<b>实例身份</b>校验：必须是本门派 <c>bookStorage</c> 那一个对象。
    /// 两道门叠加，私人存储 / 门派仓库 / 商店都不在射程内。</para>
    /// </summary>
    internal static void OnBookContainerShown(ItemListData? right)
    {
        if (!Plugin.Enabled.Value || right == null)
        {
            return;
        }

        float? target = TargetValue();

        if (target == null)
        {
            return;
        }

        if (Plugin.OnlyPlayerForce.Value)
        {
            ItemListData? playerBook = PlayerBookStorage();

            if (playerBook == null || playerBook.Pointer != right.Pointer)
            {
                return;
            }
        }

        ApplyToList(right, target.Value, "ShowTradeUI(BookType)");
    }

    /// <summary>
    /// 入口 3：兜底扫描（约每秒一次）。
    /// 负责「读档后首次生效」与「被重算掉的漂移自愈」。
    /// </summary>
    internal static void Sweep()
    {
        float? target = TargetValue();

        if (target == null)
        {
            return;
        }

        WorldData? wd = World();

        if (wd == null)
        {
            return;
        }

        if (Plugin.OnlyPlayerForce.Value)
        {
            ForceData? f = GetForce(wd, PlayerForceId(wd));

            if (f != null)
            {
                ApplyToForce(f, target.Value, "扫描");
            }

            return;
        }

        Il2CppSystem.Collections.Generic.List<ForceData>? forces;

        try
        {
            forces = wd.Forces;
        }
        catch (Exception e)
        {
            Plugin.LogOnce("forces-read", $"[藏经阁] 读门派列表失败（已忽略）：{e.Message}");
            return;
        }

        if (forces == null)
        {
            return;
        }

        int n = forces.Count;

        for (int i = 0; i < n; i++)
        {
            ApplyToForce(forces[i], target.Value, "扫描(全部门派)");
        }
    }

    // ── 唯一的两个写点 ───────────────────────────────────────────────────────

    internal static void ApplyToForce(ForceData? force, float target, string source)
    {
        if (force == null)
        {
            return;
        }

        ItemListData? storage;

        try
        {
            storage = force.bookStorage;
        }
        catch (Exception e)
        {
            Plugin.LogOnce("bookstorage-read", $"[藏经阁] 读 bookStorage 失败（已忽略）：{e.Message}");
            return;
        }

        if (storage == null)
        {
            return;
        }

        ApplyToList(storage, target, $"{source} force={force.forceID}");
    }

    internal static void ApplyToList(ItemListData? list, float target, string source)
    {
        if (list == null)
        {
            return;
        }

        float current;

        try
        {
            current = list.maxWeight;
        }
        catch (Exception e)
        {
            Plugin.LogOnce("maxweight-read", $"[藏经阁] 读 maxWeight 失败（已忽略）：{e.Message}");
            return;
        }

        if (current == target)
        {
            return;   // 已是我们想要的值：不写、不记，天然免疫「反复重算」带来的日志洪水。
        }

        try
        {
            list.maxWeight = target;
        }
        catch (Exception e)
        {
            Plugin.LogOnce("maxweight-write", $"[藏经阁] 写 maxWeight 失败（已忽略）：{e.Message}");
            return;
        }

        LogWrite(current, target, source);
    }

    // ── 取世界/玩家 ─────────────────────────────────────────────────────────

    internal static WorldData? World()
    {
        try
        {
            GameDataController? gdc = GameDataController.Instance;

            if (gdc == null)
            {
                return null;
            }

            GameSaveData? gsd = gdc.gameSaveData;

            return gsd?.WorldData;
        }
        catch
        {
            // 主菜单/读档中这些对象可能尚未就绪 —— 静默等下一个周期。
            return null;
        }
    }

    internal static int PlayerForceId(WorldData wd)
    {
        try
        {
            HeroData? p = wd.Player();

            return p == null ? int.MinValue : p.belongForceID;
        }
        catch
        {
            return int.MinValue;
        }
    }

    private static bool IsPlayerForce(ForceData force)
    {
        WorldData? wd = World();

        if (wd == null)
        {
            return false;
        }

        return force.forceID == PlayerForceId(wd);
    }

    private static ItemListData? PlayerBookStorage()
    {
        WorldData? wd = World();

        if (wd == null)
        {
            return null;
        }

        ForceData? f = GetForce(wd, PlayerForceId(wd));

        if (f == null)
        {
            return null;
        }

        try
        {
            return f.bookStorage;
        }
        catch
        {
            return null;
        }
    }

    private static ForceData? GetForce(WorldData wd, int forceId)
    {
        if (forceId == int.MinValue)
        {
            return null;
        }

        try
        {
            return wd.GetForce(forceId);
        }
        catch
        {
            return null;
        }
    }

    // ── 日志（限流，绝不刷屏） ────────────────────────────────────────────────

    private static float _lastLoggedTarget = float.NaN;
    private static DateTime _lastDiagLog = DateTime.MinValue;

    private static void LogWrite(float oldValue, float newValue, string source)
    {
        try
        {
            // 首次生效 / 配置值变了：无条件记一条（这是用户最需要看到的那条）。
            if (!(Math.Abs(_lastLoggedTarget - newValue) < 0.0001f))
            {
                _lastLoggedTarget = newValue;
                Plugin.Log.Msg($"[藏经阁] 容量上限生效：{oldValue} → {newValue}（{source}）");
                return;
            }

            // 之后只剩「被游戏重算后又复位」的重复写入 —— 只在诊断模式下、且每秒最多一条。
            if (!Plugin.Diagnostics.Value)
            {
                return;
            }

            DateTime now = DateTime.UtcNow;

            if ((now - _lastDiagLog).TotalSeconds < 1.0)
            {
                return;
            }

            _lastDiagLog = now;
            Plugin.Log.Msg($"[藏经阁] 复位漂移：{oldValue} → {newValue}（{source}）");
        }
        catch
        {
            // 日志本身出错绝不能影响游戏逻辑。
        }
    }
}
