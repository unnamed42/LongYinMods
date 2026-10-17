using System;
using System.Reflection;
using HarmonyLib;
using Il2Cpp;

namespace Unnamed42.BookStorageCap;

/// <summary>
/// 补丁安装 + 两个 Postfix。
///
/// <para><b>手工挂载（不用 <c>[HarmonyPatch]</c> + <c>PatchAll</c>）的原因</b>：
/// 目标方法一旦找不到（游戏更新改签名），<c>PatchAll</c> 会直接抛异常让整个 mod 加载失败；
/// 而按 AGENTS.md §4 纪律 7，正确行为是<b>只记日志、优雅降级、绝不影响游戏</b>。
/// 手工挂载还能把「Harmony 实际选了哪种 patcher」打出来 —— 见
/// <see cref="DescribePatcher"/>：挂到空桩上和挂到真实现，日志长得一模一样。</para>
/// </summary>
internal static class BookStorageCapPatch
{
    /// <summary>
    /// 10 参数重载的精确定位签名。
    /// ⚠️ 必须把 <c>Type[]</c> 写全：<c>ShowTradeUI</c> 有 4 个重载，
    /// 只给名字会选中不确定的那一个（项目里有「同名空桩」的前科）。
    /// </summary>
    private static readonly Type[] ShowTradeUi10 =
    {
        typeof(TradeUIType),
        typeof(ItemListType),
        typeof(ItemListData),
        typeof(ItemListData),
        typeof(int),
        typeof(int),
        typeof(bool),
        typeof(bool),
        typeof(float),
        typeof(float),
    };

    internal static int Install(HarmonyLib.Harmony harmony)
    {
        int ok = 0;

        // ⚠️ 整段包在 try 里：AccessTools.Method / typeof(Il2Cpp.*) 的求值本身也可能抛
        //    （游戏更新后类型初始化失败）。那不该冒到 MelonLoader 去 —— 按纪律 7 只记日志。
        try
        {
            // 游戏在 forceDetailDirty 置位时重算 bookStorage.maxWeight —— 我们在它算完后立刻覆盖。
            ok += TryPatch(
                harmony,
                "GameController.CountForceData(ForceData)",
                AccessTools.Method(typeof(GameController), "CountForceData", new[] { typeof(ForceData) }),
                AccessTools.Method(typeof(CountForceDataPostfix), nameof(CountForceDataPostfix.Postfix)));

            // 藏经阁界面内部可能再算一次容量；界面上显示/校验用的就是右栏容器。
            ok += TryPatch(
                harmony,
                "TradeUIController.ShowTradeUI(10 参数)",
                AccessTools.Method(typeof(TradeUIController), "ShowTradeUI", ShowTradeUi10),
                AccessTools.Method(typeof(ShowTradeUIPostfix), nameof(ShowTradeUIPostfix.Postfix)));
        }
        catch (Exception e)
        {
            Plugin.Log.Warning($"[挂载失败] 补丁安装过程异常：{e.GetType().Name}: {e.Message}");
        }

        return ok;
    }

    private static int TryPatch(
        HarmonyLib.Harmony harmony,
        string label,
        MethodInfo? target,
        MethodInfo? postfix)
    {
        if (target == null || postfix == null)
        {
            Plugin.Log.Warning(
                $"[挂载失败] 找不到目标 {label}（游戏可能已更新）—— 该钩点静默降级，游戏不受影响。");

            return 0;
        }

        try
        {
            harmony.Patch(target, postfix: new HarmonyMethod(postfix));

            Plugin.Log.Msg(
                $"[挂载] {label} → {postfix.DeclaringType?.Name}.{postfix.Name}；" +
                $"patcher={DescribePatcher(target)}");

            return 1;
        }
        catch (Exception e)
        {
            Plugin.Log.Warning($"[挂载失败] {label}：{e.GetType().Name}: {e.Message}");
            return 0;
        }
    }

    internal static class CountForceDataPostfix
    {
        /// <summary>
        /// <c>__0</c> = <c>ForceData force</c>。
        /// 这里刻意用位置参数而不是形参名 —— 参数名属于游戏侧实现细节，
        /// 改名不该让补丁失效（AGENTS.md §4 纪律 7）。
        /// </summary>
        internal static void Postfix(ForceData __0)
        {
            try
            {
                Core.OnForceCounted(__0);
            }
            catch (Exception e)
            {
                Plugin.LogOnce("postfix-count", $"[藏经阁] CountForceData Postfix 异常（已忽略）：{e.Message}");
            }
        }
    }

    internal static class ShowTradeUIPostfix
    {
        /// <summary>
        /// <c>__1</c> = <c>ItemListType targetItemListType</c>，<c>__3</c> = <c>ItemListData rightItemList</c>。
        ///
        /// <para><b>这是与第三方 mod 最本质的区别</b>：它无差别写 <c>__3</c>，
        /// 所以私人存储（同样从 <c>__3</c> 进）也被改了。这里先用
        /// <c>BookType</c> 把范围收到藏经阁一条链，再在
        /// <see cref="Core.OnBookContainerShown"/> 里做实例身份校验。</para>
        /// </summary>
        internal static void Postfix(ItemListType __1, ItemListData __3)
        {
            try
            {
                if (__1 != ItemListType.BookType)
                {
                    return;
                }

                Core.OnBookContainerShown(__3);
            }
            catch (Exception e)
            {
                Plugin.LogOnce("postfix-trade", $"[藏经阁] ShowTradeUI Postfix 异常（已忽略）：{e.Message}");
            }
        }
    }

    /// <summary>
    /// 问 Harmony「你给这个目标选了哪种 patcher、IsValid 是不是 true」。
    ///
    /// <para><c>Il2CppDetourMethodPatcher [IsValid=True]</c> ⇒ 原生 detour 真装上了；
    /// 退回默认 <c>MethodPatcher</c> ⇒ 静默变成托管 IL 补丁，原生调用<b>碰不到</b>它。
    /// 所以「挂载成功」这四个字本身不构成证据（AGENTS.md §4 纪律 2）。</para>
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

            PropertyInfo? isValid = pt.GetProperty(
                "IsValid",
                BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);

            string valid = isValid == null
                ? "（无 IsValid）"
                : $"IsValid={isValid.GetValue(patcher)}";

            return $"{pt.Name} [{valid}]";
        }
        catch (Exception e)
        {
            return $"探测失败：{e.GetType().Name}";
        }
    }
}
