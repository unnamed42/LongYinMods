using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using Il2Cpp;
using UnityEngine;

namespace Unnamed42.ShiftClickUpgrade;

/// <summary>
/// 把「Shift+单击升级」的候选钩点挂上去，并把点击链路的触发顺序打出来。
///
/// <para><b>为什么用「手挂」而不是 <c>[HarmonyPatch]</c> 特性</b>：
/// 目标是<b>运行时反射解析</b>的，而且需要「按参数个数选重载 + 打印真实签名 + IL 地址」。
/// 特性式自动扫描做不到这些，而且混用会引入
/// 「空的 <c>[HarmonyPatch]</c> 每次启动报 ERROR」那个坑（见 <c>docs/harmony-il2cpp.md</c> §5.8）。
/// <b>所以本文件里一个 <c>[HarmonyPatch]</c> 都不应该有。</b></para>
///
/// <para><b>本轮只做探针</b>：所有 Prefix 都<b>不改行为</b>（返回 void，不返回 bool），
/// 只记录「谁被点了、点的是哪栋建筑、各钩子的触发顺序」。</para>
/// </summary>
internal static class ShiftClickUpgradePatch
{
    /// <summary>候选钩子的触发计数（不受诊断开关门控 —— 取证仪表必须永不受门控）。</summary>
    private static int _iconClickHits;

    /// <summary>地块（含道路）点击次数 —— 与建筑图标分开计，便于区分两条入口。</summary>
    private static int _unitClickHits;
    private static int _buildModeHits;
    private static int _playerUpgradeHits;
    private static int _upgradeButtonHits;
    private static int _sureUpgradeHits;

    /// <summary>
    /// 挂载全部候选钩子。
    /// </summary>
    /// <returns>成功挂载的补丁数量。</returns>
    internal static int Install(HarmonyLib.Harmony harmony)
    {
        int ok = 0;

        // ── 钩点 1（★ 建筑入口）：建筑图标的点击回调 ───────────────────────
        //
        // ✅ **已实机验证触发**（2026-10-04，4/4 次点击全部命中）：
        //     [探针·图标点击] 第 1 次 | shift=False | buildingID=34 lv=0 areaID=8 name=药房
        //
        // 由 MouseController.Notify(go, "OnClick", obj) 反射派发 ——
        // 「有人点了这栋建筑的图标」这个事件必然经过这里，与鼠标/触摸无关。
        // 而且是**首个**接触点：锤子模式与否都不影响它触发。
        ok += TryPatchPrefix(
            harmony,
            typeof(AreaBuildingIconController), "OnClick",
            nameof(AreaBuildingIconController_OnClick_Prefix),
            parameterCount: 0);

        // ⚠️ 建筑也要挂 **Postfix** —— 菜单与「升级」按钮是 `OnClick`
        //   执行过程中才创建的，Prefix 里看不到（与道路完全同构）。
        ok += TryPatchPostfix(
            harmony,
            typeof(AreaBuildingIconController), "OnClick",
            nameof(AreaBuildingIconController_OnClick_Postfix),
            parameterCount: 0);

        // ── 钩点 1b（★ 道路入口）：地块的点击回调 ────────────────────────
        //
        // 【为什么必须单独挂 —— 用户发现的实机问题】
        //   「shift 点击道路（也是可以升级的）没有触发升级」。
        //
        // 【根因】**道路格子上根本没有 `AreaBuildingIconController`**。
        //   建筑图标与地块是**两个不同的点击接收者**：
        //
        //     建筑 → `AreaBuildingIconController`（建筑图标对象）
        //     道路 / 空地 → `AreaUnitController`（地块对象）
        //
        //   实测（MCP）：
        //     道路格 14_14 | tileType=EmptySpace | roadLv=0
        //       AreaUnitController = 有
        //       AreaBuildingIconController = **无**      ← 所以补丁从未触发过
        //
        //   全场景带道路数据的 `AreaUnitController` 共 **137 个**。
        //
        // 【数据结构】道路不走 `AreaBuildingData`，而是：
        //   `AreaUnitController.areaTileData : AreaTileData`
        //     └─ `areaRoadData : AreaRoadData`  (roadLv / upgradeTimeLeft)
        //   `AreaTileData` 上同时有 `building` 与 `areaRoadData` ——
        //   两者互斥：建筑格走前者，道路/空地格走后者。
        // ⚠️ Prefix 与 Postfix **两个都要挂**：
        //   Prefix  只记下意图（此刻菜单还不存在，找不到按钮）
        //   Postfix 真正升级（此刻菜单已建好）
        // 详见 AreaUnitController_OnClick_Postfix 的注释。
        ok += TryPatchPrefix(
            harmony,
            typeof(AreaUnitController), "OnClick",
            nameof(AreaUnitController_OnClick_Prefix),
            parameterCount: 0);

        ok += TryPatchPostfix(
            harmony,
            typeof(AreaUnitController), "OnClick",
            nameof(AreaUnitController_OnClick_Postfix),
            parameterCount: 0);

        // ── 钩点 2（★ 前置态）：锤子（建造模式）的切换 ─────────────────────────
        //
        // ⚠️⚠️ **这里必须钩 `ChangeBuildMode(bool)`，不能钩 `set_buildMode(bool)`。**
        //
        // 实机日志（12:26:52）明确报：
        //   [Il2CppInterop] Failed to init IL2CPP patch backend for
        //   void Il2Cpp.AreaBuildController::set_buildMode(bool ),
        //   using normal patch handlers: Method ... is a field accessor, it can't be patched.
        //
        // `hook_patch_info` 也显示它的 patcher 是 `ManagedMethodPatcher`（无原生 methodInfo），
        // 而不是 `Il2CppDetourMethodPatcher` —— 即**挂上去了也永远不会触发**。
        // 这是本项目「挂载成功 ≠ 触发」的又一实例，而且这次是**静默**的。
        //
        // `ChangeBuildMode(bool)` 是真正的原生方法（patcherIsValid=true）。
        ok += TryPatchPrefix(
            harmony,
            typeof(AreaBuildController), "ChangeBuildMode",
            nameof(AreaBuildController_ChangeBuildMode_Prefix),
            parameterCount: 1);

        // ── 钩点 3：真正的升级执行入口 ───────────────────────────────────────
        //
        // 参数就是「要升级的那栋建筑」。
        //
        // ✅ **已实机验证可调用**：在活进程里直接调 `BuildModeButtonClicked()` →
        //    `buildMode False -> True`，说明这几个方法在托管侧调用是通的。
        //
        // ❓ **但它在“点击升级”时是否被走到，仍未验证** —— 需要你在
        //    **锤子模式下**点「升级」，看这条探针是否打印。
        ok += TryPatchPrefix(
            harmony,
            typeof(AreaBuildController), "PlayerUpgradeBuilding",
            nameof(AreaBuildController_PlayerUpgradeBuilding_Prefix),
            parameterCount: 1);

        // ── 钩点 4 / 5：UI 按钮回调（可能夹带二次确认弹窗）──────────────────
        //
        // UpgradeButtonClicked -> （可能）SureUpgradeBuliding。
        // 挂它们是为了在日志里看清「Shift 点击之后，游戏自己走了哪几个回调」，
        // 从而判断**能不能直接复用游戏的升级流程**，而不是自己重写一遍。
        ok += TryPatchPrefix(
            harmony,
            typeof(BuildingUIController), "UpgradeButtonClicked",
            nameof(BuildingUIController_UpgradeButtonClicked_Prefix),
            parameterCount: 0);

        ok += TryPatchPrefix(
            harmony,
            typeof(BuildingUIController), "SureUpgradeBuliding",
            nameof(BuildingUIController_SureUpgradeBuliding_Prefix),
            parameterCount: 0);
        // ── 音效取证探针（默认关，排查时才开）─────────────────────────────
        //
        // 挂 `NGUITools.PlaySound(AudioClip, float, float)` 的 Prefix。
        // 它是**全部 UI 音效的汇聚点**（`UIPlaySound.Play()` 尾调用到它）。
        //
        // ✅ 本 mod 就是靠它找出「升级音 = WoodWork」的（见 §4.5）。
        // 保留它：以后游戏更新、音效变了，开这个开关点一下就能重新抓到真名。
        //
        // ⚠️ 按参数个数选重载在这里尤其重要：同名 3 个重载，
        //    随便取一个很可能是没被调用的那个（见 docs/harmony-il2cpp.md §5.1）。
        if (Plugin.SoundProbe.Value)
        {
            ok += TryPatchPrefix(
                harmony,
                typeof(NGUITools), "PlaySound",
                nameof(NGUITools_PlaySound_Prefix),
                parameterCount: 3);
        }

        return ok;
    }

    // ── 工具：Shift 状态 ─────────────────────────────────────────────────────

    /// <summary>
    /// 当前是否按住了 Shift。
    ///
    /// <para>
    /// 用 <c>UnityEngine.Input.GetKey</c>（旧输入系统）。<c>InputLegacyModule</c> 已在 csproj 引用 ——
    /// 不引会报「类型在未引用的程序集中定义」。
    /// </para>
    /// <para>
    /// ⚠️ 这里刻意**同时接受左右 Shift**（<c>LeftShift</c> / <c>RightShift</c>）：
    /// 只认 Left 会让「右手小指按 Shift」失效，而玩家不会猜到是这个原因。
    /// </para>
    /// </summary>
    private static bool ShiftHeld =>
        Input.GetKey(KeyCode.LeftShift) || Input.GetKey(KeyCode.RightShift);

    /// <summary>把建筑的关键信息压成一行（null 安全）。</summary>
    private static string DescribeBuilding(AreaBuildingData? b)
    {
        if (b == null)
        {
            return "null";
        }

        try
        {
            return $"buildingID={b.buildingID} lv={b.lv} areaID={b.areaID} name={b.Name(false)}";
        }
        catch (Exception e)
        {
            return $"<读取失败 {e.GetType().Name}>";
        }
    }

    // ── 补丁 1：AreaBuildingIconController.OnClick ───────────────────────────

    /// <summary>
    /// 建筑图标被点击时的探针。
    ///
    /// <para>
    /// ⚠️ <b>这里必须挂 Prefix 而不是 Postfix</b>：Prefix 才能在<b>游戏处理点击之前</b>
    /// 读到 Shift 状态并（将来）拦截。Postfix 时游戏已经打开建筑 UI 了。
    /// </para>
    /// </summary>
    internal static bool AreaBuildingIconController_OnClick_Prefix(
        AreaBuildingIconController __instance,
        out bool __state)
    {
        // ⚠️ 默认 false：异常时 Harmony 仍会调 Postfix，此时 `__state` 必须是确定值。
        __state = false;

        try
        {
            _iconClickHits++;

            bool shift = ShiftHeld;
            AreaBuildingData? data = __instance?.buildingData;

            // 受 `diagnostics` 门控。
            //
            // ⚠️ 这条曾经是**不受门控**的“补丁是否触发”证据（本项目吃过
            //    「日志被门控 → grep=0 → 误判补丁零触发」的亏）。
            //    但它挂在**每次点击建筑**的高频路径上，功能稳定后没必要常开；
            //    排查时把 `diagnostics` 打开即可。
            Plugin.LogInfo(() =>
                $"[探针·图标点击] 第 {_iconClickHits} 次 | shift={shift} | {DescribeBuilding(data)}");

            // 没按 Shift → 完全不管，放行原逻辑（行为与没装 mod 时一致）。
            if (!shift)
            {
                return true;
            }

            // ⚠️ 这里原本写成 `{DescribeBuilding(data)}` 与 GameObject 名拼在一起，
            //    少了一个开括号 —— 日志会变成「… name=药房AreaBuildingUnit(Clone)）」。
            //    日志文本出错不影响逻辑，但**排查时会把两个字段看成一个**，已修。
            Plugin.LogInfo(() =>
                $"[探针·Shift 点击] 命中建筑图标：{DescribeBuilding(data)}" +
                $"（物体={__instance?.gameObject?.name ?? "?"}）");

            // 闸①：功能开关。
            if (!Plugin.Enabled.Value)
            {
                return true;
            }

            // 闸①.5：探针模式 —— 只打日志、不改行为。
            // 接入真实升级后建议关掉，但排查时打开它就能回到安全态。
            if (Plugin.ProbeOnly.Value)
            {
                Plugin.LogInfo(() =>
                    "[Shift+单击升级] probe_only=true，仅记录不执行（此时若已满足条件，" +
                    "真实版本会在这里升级）。");
                return true;
            }

            // ── ★ 三道闸：全真才接管，否则完全放行 ──────────────────────────────
            //
            // 【关键：这个补丁方法现在返回 bool】
            //   返回 false = 跳过原方法（不弹建筑 UI）
            //   返回 true  = 继续执行原方法（行为与没装 mod 时一致）
            //
            // ⚠️ 返回 bool 的补丁**必须挂 Prefix**：写成 Postfix 会被 Harmony 当成
            //    「透传 postfix」而报 `Return type of pass through postfix …`。
            //    而 `OnClick()` 返回 void —— Prefix 返回 false 是**安全**的
            //    （没有返回值供调用方消费）。
            //    详见 docs/friendlynoclip.md §2.2d 的「返回值语义」表。

            var abc = AreaBuildController.Instance;

            // 闸②：锤子（建造模式）必须开着。
            if (abc == null || !abc.buildMode)
            {
                return true;
            }

            if (data == null)
            {
                Plugin.LogInfo(() => "[Shift+单击升级] 图标没有关联建筑，放行。");
                return true;
            }

            // 闸③：建筑自己得说可升级。
            // 先用 `CanUpgrade()` 筛一道 —— 不可升级时**放行原 UI**，
            // 让玩家拿到游戏原本的反馈（而不是我们的静默失败）。
            bool canUpgrade;

            try
            {
                canUpgrade = data.CanUpgrade();
            }
            catch (Exception e)
            {
                Plugin.LogError($"[Shift+单击升级] CanUpgrade() 抛异常，放行：{e.GetType().Name}");
                return true;
            }

            if (!canUpgrade)
            {
                Plugin.LogInfo(() => $"[Shift+单击升级] 不可升级，放行原 UI：{DescribeBuilding(data)}");
                return true;
            }
            // 闸④（防重复）与真正的升级都放到 **Postfix** ——
            // 因为菜单是 `OnClick` 执行过程中才创建的（与道路同构，见上方注释）。

            // ── ★ 与道路统一：不直接调 callback，而是走 UI 按钮 ────────────────
            //
            // 【为什么改成这样】用户提议统一两条路径，并先验证了可行性。
            //
            // 实测（2026-10-04）关键发现：
            //   **建筑与道路共用同一个菜单** `Canvas/AreaUIPanel/BuildChoiceGrid`。
            //   锤子开启时点建筑 → 同样的 BuildChoiceGrid 出现（拆除/迁移/升级）。
            //
            //   点它的「升级」按钮后：
            //     摊贩: upgradeTimeLeft 0 -> 1       ← 真的升级
            //     音效: Woosh + WoodWork + TabButton ← 与手动点击完全一致
            //
            // 所以建筑也可以（且应该）走同一条路径 ——
            // 这样就不再需要 `PlayerUpgradeBuilding` + 手工补音效那套了。
            //
            // ⚠️ **与道路一样，不能在 Prefix 里做**：
            //   菜单按钮是 `OnClick` 执行过程中才创建/激活的。
            //   实测建筑：调用前 UpgradeButton 的 activeInHierarchy=False，
            //   需要先放行原方法让游戏把菜单建出来。
            //   → 记下意图（__state），在 Postfix 里触发。

            // 已在升级中 → 不重复触发（闸④，在 Postfix 里判）。
            __state = true;
            return true;
        }
        catch (Exception e)
        {
            // ⚠️ 异常时必须返回 true（放行原方法），不能返回 false ——
            //    那样会让「我们的代码出错了」表现为「点了没反应」，极难排查。
            Plugin.LogError($"[ShiftClickUpgrade] OnClick 探针异常：{e}");
            return true;
        }
    }

    /// <summary>
    /// 建筑图标的 **Postfix** —— 升级在这里做（与道路完全同构）。
    ///
    /// <para>
    /// 【为何必须在 Postfix】菜单（`BuildChoiceGrid`）与「升级」按钮是
    /// <c>OnClick</c> 执行过程中才创建/激活的，Prefix 里看不到。
    /// </para>
    /// <para>
    /// 【为什么改成走 UI】以前这里是直接调 `PlayerUpgradeBuilding`
    /// 再手工补音效（`TabButton` + `WoodWork`）。
    /// 现在建筑与道路共用同一菜单，走同一个按钮就**不需要再手工补任何东西**。
    /// </para>
    /// </summary>
    internal static void AreaBuildingIconController_OnClick_Postfix(
        AreaBuildingIconController __instance,
        bool __state)
    {
        if (!__state)
        {
            return;
        }

        try
        {
            if (!Plugin.Enabled.Value || Plugin.ProbeOnly.Value)
            {
                return;
            }

            AreaBuildingData? data = null;

            try
            {
                data = __instance?.buildingData;
            }
            catch
            {
                // 对象可能已被销毁。
            }

            if (data == null)
            {
                return;
            }

            // ── 闸④：防重复触发（与道路同构）───────────────────────────
            int upgradeTimeLeft;

            try
            {
                upgradeTimeLeft = data.upgradeTimeLeft;
            }
            catch
            {
                upgradeTimeLeft = 0;
            }

            if (upgradeTimeLeft > 0)
            {
                Plugin.LogInfo(() =>
                    $"[Shift+单击升级] 已在升级中（剩 {upgradeTimeLeft}），不重复触发：{DescribeBuilding(data)}");

                CloseBuildMenuSafely();
                return;
            }

            // 同一次点击里，「地块」的 Postfix 可能已经升级过了（见 ShouldSkipAsDuplicate）。
            // 用**目标对象指针**做主判据：同一次点击的两个接收者指向同一栋建筑。
            if (ShouldSkipAsDuplicate(GetObjectPtr(__instance)))
            {
                return;
            }

            // 此刻菜单已建好，一定能找到「升级」按钮（实测 23/23 成功）。
            //
            // ⚠️ **不做回退**（旧版本会回到直接调 `PlayerUpgradeBuilding`，已删除）。
            // 理由：回退会**绕过按钮的 `interactable` 检查**，
            // 而 `interactable=false` 恰恰意味着“前置条件不满足”
            // （资源不足 / 已满级 / ForceLv 不够）。
            // 那样就与闸②③的语义相矛盾：本来该拦住的情况反而能升级。
            //
            // 所以按钮不可用时**什么都不做**，交互回到“点了没反应”——
            // 与玩家在原生 UI 里点一个灰按钮的体验一致。
            TryUpgradeViaUiButton(GetObjectPtr(__instance));
        }
        catch (Exception e)
        {
            Plugin.LogError($"[ShiftClickUpgrade] 建筑 Postfix 异常：{e}");
        }
    }

    // ── 钩点 1b：AreaUnitController.OnClick（道路 / 地块）──────────────────────

    /// <summary>
    /// 地块（包括**道路**）被 Shift 点击时的处理。
    ///
    /// <para>
    /// 【为什么需要它】用户报告：「shift 点击道路（也是可以升级的）没有触发升级」。
    /// </para>
    /// <para>
    /// 根因：**道路格上没有 `AreaBuildingIconController`** ——
    /// 建筑走图标对象、道路/空地走地块对象，是**两个不同的点击接收者**。
    /// 我们之前只挂了前者，所以道路从未触发过补丁。
    /// </para>
    /// <para>
    /// ✅ <b>实现思路（用户提出）：“触发 UI 元素”而不是“直接调 callback”</b>。
    /// 实测确认可行 —— 详见 <see cref="TryUpgradeViaUiButton"/>。
    /// </para>
    /// </summary>
    internal static bool AreaUnitController_OnClick_Prefix(
        AreaUnitController __instance,
        out bool __state)
    {
        // ⚠️ `__state` 默认必须置 false —— 若下面的 try 提前抛异常，
        //     Harmony 仍会调 Postfix，此时 `__state` 必须是确定值。
        __state = false;

        try
        {
            _unitClickHits++;

            bool shift = ShiftHeld;
            AreaTileData? tile = null;

            try
            {
                tile = __instance?.areaTileData;
            }
            catch
            {
                // 对象可能已被销毁，忽略。
            }

            bool isRoad = false;
            int roadLv = -1;

            try
            {
                AreaRoadData? road = tile?.areaRoadData;
                isRoad = road != null;
                roadLv = isRoad ? road!.roadLv : -1;
            }
            catch
            {
                // 同上。
            }

            // 受 `diagnostics` 门控。
            // `isRoad` 是关键 —— 它区分「建筑格」（走另一个补丁）与「道路格」，
            // 排查“点错地方”时打开 diagnostics 即可看到。
            Plugin.LogInfo(() =>
                $"[探针·地块点击] 第 {_unitClickHits} 次 | shift={shift} | isRoad={isRoad}" +
                $" roadLv={roadLv} | 物体={__instance?.gameObject?.name ?? "?"}");

            // 非道路（空地等）不处理 —— 只放行。
            if (!shift || !isRoad)
            {
                return true;
            }

            // ── ★ 不能在这里升级 ──────────────────────────────────────────
            //
            // 【为什么】实测（2026-10-04）：菜单的「升级」按钮是游戏在
            // `OnClick` **执行过程中**才 Instantiate 的。
            //
            //   调用前可见的「升级」按钮数 = **0**
            //   调用后可见的「升级」按钮数 = **1**
            //
            // 所以在 **Prefix** 里必然找不到按钮 —— 这正是
            // 用户报告的「需要点两次」：第一次只能把菜单弹出来，
            // 第二次菜单已在，才能找到按钮并升级。
            //
            // → 正确做法：Prefix 只**记下意图**，真正的升级放到
            //   **Postfix**（原方法跑完、菜单已创建）里做。
            //   用 `__state` 把意图传递给 Postfix（**不用静态字段**）——
            //   静态字段会被并发/重入污染，`__state` 是 Harmony 官方的
            //   per-invocation 机制（见 docs/harmony-il2cpp.md §5.4）。
            if (shift && isRoad)
            {
                __state = true;
            }

            // 放行原方法：让游戏正常创建菜单。
            // （Postfix 里拿到按钮后我们再把菜单关掉）
            return true;
        }
        catch (Exception e)
        {
            // 异常时放行（返回 true），不能让我们的错表现为“点了没反应”。
            Plugin.LogError($"[ShiftClickUpgrade] AreaUnitController 处理异常：{e}");
            return true;
        }
    }

    /// <summary>
    /// 道路点击的 **Postfix** —— 升级在这里做。
    ///
    /// <para>
    /// 【为什么必须在 Postfix】游戏的菜单与「升级」按钮是
    /// <c>OnClick</c> **执行过程中**才创建/激活的：
    /// Prefix 里看到 0 个按钮，Postfix 里才看到 1 个。
    /// 所以只有 Postfix 才能拿到那个按钮。
    /// </para>
    /// <para>
    /// 这同时也修复了用户报告的「需要点两次」。
    /// </para>
    /// </summary>
    internal static void AreaUnitController_OnClick_Postfix(
        AreaUnitController __instance,
        bool __state)
    {
        // Prefix 没标记（非 Shift、非道路）→ 什么都不做。
        if (!__state)
        {
            return;
        }


        try
        {
            if (!Plugin.Enabled.Value || Plugin.ProbeOnly.Value)
            {
                return;
            }

            // ── 闸④：防重复触发（与建筑同构，见 §4.6）─────────────────────
            //
            // 【现象】用户实测：前两次 Shift 点击都能触发升级，第三次才会被拦。
            // （升级行为本身只升一级是对的，但多播一次升级音/多走一遗流程仍是噪声）
            //
            // 【根因】与建筑一个道理：`AreaRoadData` 的 `upgradeTimeLeft`
            //   是“升级剩余时间” —— 但按钮在菜单里的可见性/可交互性
            //   不一定会立即反映“已在升级中”，所以得自己搪一道。
            //
            //   0  = 空闲（可升级）
            //   >0 = **正在升级中**
            int roadLv = -1;
            int upgradeTimeLeft = 0;

            try
            {
                AreaRoadData? road = __instance?.areaTileData?.areaRoadData;

                if (road != null)
                {
                    roadLv = road.roadLv;
                    upgradeTimeLeft = road.upgradeTimeLeft;
                }
            }
            catch
            {
                // 忽略：只影响日志里的数值。
            }

            if (upgradeTimeLeft > 0)
            {
                // 已在施工 → 不重复升级，也不弹菜单（与建筑行为一致）。
                Plugin.LogInfo(() =>
                    $"[Shift+单击升级] 道路已在升级中（剩 {upgradeTimeLeft}），不重复触发。");

                CloseBuildMenuSafely();
                return;
            }
            // 同一次点击里，「建筑图标」的 Postfix 可能已经升级过了（见 ShouldSkipAsDuplicate）。
            if (ShouldSkipAsDuplicate(GetObjectPtr(__instance)))
            {
                return;
            }

            // 此刻菜单已建好，能找到「升级」按钮。
            TryUpgradeViaUiButton(GetObjectPtr(__instance));
        }
        catch (Exception e)
        {
            Plugin.LogError($"[ShiftClickUpgrade] AreaUnitController Postfix 异常：{e}");
        }
    }


    // ── ★ 核心：通过“触发 UI 元素”实现升级（用户提出的方案）────────────────

    /// <summary>
    /// 通过**游戏自己的 UI 按钮**触发升级，而不是直接调 callback。
    ///
    /// <para>
    /// 【为什么这个方案更好】本 mod 前几轮一直在“找正确的 callback”上碰壁：
    /// 先试 `PlayerUpgradeBuilding`（建筑可用，但道路没有对应方法），
    /// 又得手工补音效（因为直接调 callback 绕过了 UI 层）。
    /// </para>
    /// <para>
    /// 用户提出的思路：「能不能通过触发 UI 元素，而不是直接代码调用各种 callback」——
    /// <b>已验证完全可行</b>。原理：游戏的按钮 `onClick` 上绑的本来就是完整入口，
    /// 调 `onClick.Invoke()` 等价于“玩家点了这个按钮”，于是：
    /// </para>
    /// <list type="number">
    ///   <item>**不需要知道**升级代码在哪里 —— 游戏自己走；</item>
    ///   <item>**音效/资源扣除/UI 刷新/计时全部自动正确** —— 不用再手工补；</item>
    ///   <item>**建筑与道路统一** —— 不用分别为两者找入口；</item>
    ///   <item>**前置检查由游戏自己做** —— 不满足时自然不执行。</item>
    /// </list>
    ///
    /// <para>
    /// 【实测证据】2026-10-04 14:01:17，对道路 `4_5` 调
    /// `BuildChoiceButton.onClick.Invoke()`：
    /// </para>
    /// <code>
    /// roadLv=2 upgradeTimeLeft: 0 -> 1     ← 真的开始升级了
    /// 音效: Woosh + WoodWork + TabButton    ← 与手动点击完全一致
    /// buildTargetObj: 4_5 -> null           ← 菜单关闭，流程完整走完
    /// </code>
    ///
    /// <para>
    /// ⚠️ <b>局限：按钮得先存在</b>。建筑菜单 / 道路菜单都是玩家点开后才 Instantiate 的，
    /// 所以本函数只能在“菜单已经开着”时工作（我们是通过 `OnClick` 进来的，
    /// 那时游戏刚处理完点击、菜单已在）。若找不到按钮就回退到直接调 callback。
    /// </para>
    /// </summary>
    /// <summary>
    /// 关掉建造菜单（若它在）。
    ///
    /// <para>
    /// 【为什么需要】Postfix 阶段菜单已经被游戏建出来了。
    /// 当我们决定**不升级**（例如“已在施工中”）时，
    /// 不能把菜单留在屏幕上 —— 那与“直接升级”的交互意图相悖，
    /// 也与建筑的“不弹菜单”行为不一致。
    /// </para>
    /// <para>
    /// ⚠️ 整个函数不得抛异常：关菜单只是收尾，不能因它影响主逻辑。
    /// </para>
    /// </summary>
    private static void CloseBuildMenuSafely()
    {
        try
        {
            AreaBuildController? abc = AreaBuildController.Instance;

            if (abc != null)
            {
                abc.CloseBuildMenu();
            }
        }
        catch (Exception e)
        {
            Plugin.LogInfo(() => $"[Shift+单击升级] 关闭菜单失败（忽略）：{e.GetType().Name}");
        }
    }


    /// <summary>
    /// 尝试通过**游戏的「升级」按钮**触发升级。
    ///
    /// <para>
    /// 【这个函数的历史值得一提】早期版本返回 <c>bool</c>，语义是
    /// 「true=放行原方法 / false=已接管」。而调用处写成
    /// <c>if (!TryUpgradeViaUiButton(...))</c> —— 于是在**成功**时
    /// 反而走了回退路径，**多升级一次**（实测多扣 1250）。
    /// 后来改成显式枚举 <c>UiUpgradeResult</c> 消除歧义；
    /// 再后来发现**回退路径本身就是错的**（会绕过按钮的前置检查），
    /// 连带删掉 —— 于是这个函数现在不再需要返回值，改为 <c>void</c>。
    /// </para>
    /// <para>
    /// 这个过程说明：**“回退路径”往往是在掩盖“主路径其实总是可用”这个事实**。
    /// 一旦确认主路径覆盖了全部场景，回退就是纯负担（还得为它维护语义）。
    /// </para>
    /// </summary>
    private static void TryUpgradeViaUiButton(long targetPtr)
    {
        try
        {
            UnityEngine.UI.Button? upgradeButton = FindActiveUpgradeButton();

            if (upgradeButton == null)
            {
                Plugin.LogInfo(() => "[Shift+单击升级] 未找到「升级」按钮（菜单未开？）。");
                return;
            }

            if (!upgradeButton.interactable)
            {
                // ⚠️ **不做回退** —— 按钮不可用说明前置条件不满足
                // （资源不足 / 满级 / ForceLv 不够），此时去直接调
                // `PlayerUpgradeBuilding` 会绕过这个检查，语义就错了。
                Plugin.LogInfo(() => "[Shift+单击升级] 「升级」按钮不可用（前置条件不满足），不做任何事。");
                return;
            }

            // ★ 等价于“玩家点了这个按钮”—— 走完整原生链路。
            //
            // 【日志分级】这是「功能生效」的证据，所以**不受** `diagnostics` 门控。
            // 但它每次升级都会打一行 —— 玩家快速点十几下时会刷屏。
            // 折中：只在**首次**成功时用 Msg（确认功能确实在工作），
            // 之后的归入 `diagnostics`。
            if (!_hasUpgradeTriggered)
            {
                Plugin.Log.Msg(
                    $"[Shift+单击升级] ★ 通过 UI 按钮触发升级（首次，按钮={upgradeButton.gameObject.name}）");
            }
            else
            {
                Plugin.LogInfo(() =>
                    $"[Shift+单击升级] ★ 通过 UI 按钮触发升级（按钮={upgradeButton.gameObject.name}）");
            }

            upgradeButton.onClick.Invoke();

            // 记下这次触发，供去重用（见 MarkUpgradeTriggered）。
            // 传入目标指针，使去重能区分“同一次点击的两个事件”与“玩家点了另一处”。
            MarkUpgradeTriggered(targetPtr);

            return;
        }
        catch (Exception e)
        {
            // 交给调用方决定回退 —— 不在这里擅自再升一次。
            Plugin.LogError($"[Shift+单击升级] UI 触发升级失败：{e}");
            return;
        }
    }

    /// <summary>最近一次升级触发的时间戳（毫秒）。</summary>
    private static int _lastUpgradeTick;

    /// <summary>是否已经**发生过**至少一次触发。</summary>
    /// <remarks>
    /// ⚠️ 必须有这个显式标志，**不能**靠给 <c>_lastUpgradeTick</c> 设哨兵值
    /// （例如 <c>int.MinValue</c>）来表示“还没触发过”。
    ///
    /// 踩过的坑（用户发现“完全不触发”）：用 <c>int.MinValue</c> 当初值时，
    /// <c>unchecked(now - int.MinValue)</c> 得到一个**巨大的负数**，
    /// 而 <c>负数 &lt; 250</c> 恒成立 —— **首次点击就被判为“重复”**，功能全废。
    /// 日志里表现为 <c>(−1762815977ms 内)，跳过重复</c>。
    /// </remarks>
    private static bool _hasUpgradeTriggered;

    /// <summary>
    /// 去重窗口（毫秒）。
    ///
    /// <para>
    /// 【为什么需要】实测（2026-10-04）一次 Shift 点击会触发**两次**升级：
    /// 同一屏幕位置上的「建筑图标」与「地块」**各自**收到 OnClick，
    /// 于是建筑 Postfix 与道路 Postfix 都跑了，两次都 Invoke 了同一个按钮。
    /// </para>
    ///
    /// <para>
    /// 【为什么是 75ms】（用户指出 250ms 太宽，会吞掉连续点击）
    /// 实测数据对比：
    /// </para>
    /// <code>
    /// 【真正的重复】同一物体的两次事件（间隔 2～6ms）
    ///   15:31:58.386  [探针·地块点击] 物体=8_7
    ///   15:31:58.640  [探针·地块点击] 物体=9_7    <- 不同物体，这才是真人点击
    ///
    /// 【被误判成重复的真点击】（旧 250ms 窗口下）
    ///   160ms / 190ms / 240ms / 249ms  <- 全是**不同物体**，是玩家真实连点
    /// </code>
    /// <para>
    /// 所以两个维度都能分开重复与真点击：
    /// </para>
    /// <list type="number">
    ///   <item><b>间隔</b>：重复只有 2～6ms；真人连点 ≥ 130ms（实测最小 160ms）</item>
    ///   <item><b>目标</b>：重复是**同一个**物体；不同物体必然是两次独立点击</item>
    /// </list>
    /// <para>
    /// 75ms 远大于重复间隔（2～6ms）、远小于真人连点（≥130ms），两边都有充裕余量。
    /// 另外还叠加了“同一目标”判断（见 <see cref="ShouldSkipAsDuplicate"/>）。
    /// </para>
    /// </summary>
    private const int UpgradeDedupWindowMs = 75;

    /// <summary>
    /// 最近一次触发所针对的目标对象指针。
    ///
    /// <para>
    /// 这是去重的**主判据**：同一次鼠标点击会让「建筑图标」与「地块」
    /// 两个接收者各发一次事件，但它们的 `OnClick` 处理的**是同一个目标**
    /// （同一栋建筑 / 同一格道路）。
    /// 而玩家真的连点两次时，即使之间只有 160ms，目标也是**不同**物体。
    /// </para>
    /// </summary>
    private static long _lastUpgradeTargetPtr;

    /// <summary>
    /// 取 IL2CPP 对象的原生指针（用于“同一目标”判定）。
    ///
    /// <para>
    /// 取不到时返回 0 —— 此时 <c>sameTarget</c> 为 false，
    /// 去重会退化为“不去重”（宁可能重，也不过杀）。
    /// </para>
    /// </summary>
    private static long GetObjectPtr(Il2CppInterop.Runtime.InteropTypes.Il2CppObjectBase? obj)
    {
        if (obj == null)
        {
            return 0;
        }

        try
        {
            return Il2CppInterop.Runtime.IL2CPP.Il2CppObjectBaseToPtr(obj).ToInt64();
        }
        catch
        {
            return 0;
        }
    }

    /// <summary>记录一次升级触发（用于跨 Postfix 去重）。</summary>
    /// <param name="targetPtr">本次升级目标对象的原生指针。</param>
    private static void MarkUpgradeTriggered(long targetPtr)
    {
        _lastUpgradeTick = Environment.TickCount;
        _lastUpgradeTargetPtr = targetPtr;
        _hasUpgradeTriggered = true;
    }

    /// <summary>
    /// 判断本次是否应因“刚刚已经触发过”而跳过。
    ///
    /// <para>
    /// 这是**跨补丁**的去重：建筑与地块两个 Postfix 共享同一份状态，
    /// 因为对玩家而言它们是**同一次点击**。
    /// </para>
    /// </summary>
    /// <param name="targetPtr">本次拟升级目标对象的原生指针（用于“同一目标”判定）。</param>
    private static bool ShouldSkipAsDuplicate(long targetPtr)
    {
        // 从未触发过 → 不是重复。
        //
        // ⚠️ 这个判断必须**显式**做，不能靠“时间戳的哨兵值”：
        //    若把 `_lastUpgradeTick` 初始化成 int.MinValue，
        //    `unchecked(now - int.MinValue)` 是个巨大的**负数**，
        //    而负数 < 阈值 恒成立 -> 首次点击就被当成重复，功能全废。
        if (!_hasUpgradeTriggered)
        {
            return false;
        }

        int now = Environment.TickCount;

        // TickCount 会回绕；用减法比较（int 溢出在 C# 里是定义良好的环绕）。
        // 此处 `_lastUpgradeTick` 已被 MarkUpgradeTriggered 赋过真实值，
        // 所以差值不会出现“哨兵导致的巨大负数”。
        int delta = unchecked(now - _lastUpgradeTick);

        // ── 主判据：同一目标 + 极短间隔 ────────────────────────────────
        //
        // 实测数据：
        //   真重复（同一次点击的两个接收者）：**同一个物体**，间隔 2～6ms
        //   真人连点（被旧 250ms 误杀的那些）：**不同物体**，间隔 160～249ms
        //
        // 两个维度都要求，误杀风险最低：
        //   ✓ 同目标 + <75ms  -> 同一次点击的两个事件
        //   ✗ 不同目标        -> 玩家点了别处，即使只隔 100ms 也该放行
        bool sameTarget = (targetPtr != 0) && (targetPtr == _lastUpgradeTargetPtr);

        if (sameTarget && delta < UpgradeDedupWindowMs)
        {
            Plugin.LogInfo(() =>
                $"[Shift+单击升级] 同一次点击已触发过升级" +
                $"（同一目标、{delta}ms 内），跳过重复。");

            return true;
        }

        return false;
    }

    /// <summary>
    /// 找当前激活的「升级」按钮。
    ///
    /// <para>两个候选（实测都存在）：</para>
    /// <list type="bullet">
    ///   <item><c>Canvas/AreaUIPanel/BuildChoiceGrid/BuildChoiceButton(Clone)</c> —— 锤子模式的建造菜单（建筑与道路**共用**）</item>
    ///   <item><c>Canvas/BuildingUIPanel/BuildingUI/ExtraButtonGrid/UpgradeButton</c> —— 另一套建筑 UI（当前流程不走）</item>
    /// </list>
    /// <para>
    /// ⚠️ 不能靠名字写死：菜单是运行时 Instantiate 的，层级会变。
    /// 所以按**文字内容**扫（两个按钮的文字都是「升级」），更耐改。
    /// </para>
    /// </summary>
    private static UnityEngine.UI.Button? FindActiveUpgradeButton()
    {
        try
        {
            UnityEngine.UI.Button[] buttons =
                Resources.FindObjectsOfTypeAll<UnityEngine.UI.Button>();

            foreach (UnityEngine.UI.Button b in buttons)
            {
                if (b == null)
                {
                    continue;
                }

                GameObject go = b.gameObject;

                if (go == null || !go.activeInHierarchy)
                {
                    continue;
                }

                // 按文字匹配（两个升级按钮的文字都是“升级”）。
                UnityEngine.UI.Text? label = go.GetComponentInChildren<UnityEngine.UI.Text>(true);

                if (label == null || label.text != "升级")
                {
                    continue;
                }

                return b;
            }
        }
        catch (Exception e)
        {
            Plugin.LogInfo(() => $"[Shift+单击升级] 扫描按钮失败：{e.GetType().Name}");
        }

        return null;
    }


    // ── 补丁 2：AreaBuildController.PlayerUpgradeBuilding ────────────────────

    /// <summary>
    /// 锤子（建造模式）切换的探针。
    ///
    /// <para>
    /// ⚠️ 钩的是 <c>ChangeBuildMode(bool)</c>，<b>不是</b> <c>set_buildMode</c> ——
    /// 后者是编译器生成的字段访问器，IL2CPP 下<b>根本无法挂原生补丁</b>（见 <c>Install</c> 里的注释）。
    /// </para>
    /// </summary>
    internal static void AreaBuildController_ChangeBuildMode_Prefix(
        AreaBuildController __instance,
        bool _buildMode)
    {
        try
        {
            _buildModeHits++;

            // 受 `diagnostics` 门控 —— 纯取证：
            // 记录锤子开关，用于排查“buildMode 前置态”类问题。
            Plugin.LogInfo(() =>
                $"[探针·锤子切换] 第 {_buildModeHits} 次 | _buildMode={_buildMode} | " +
                $"切换后 buildMode={__instance?.buildMode}");
        }
        catch (Exception e)
        {
            Plugin.LogError($"[ShiftClickUpgrade] ChangeBuildMode 探针异常：{e}");
        }
    }

    // ── 音效取证探针（默认关）────────────────────────────────────────────
    //
    // 【怎么找到真实升级音的】
    //   保留「升级」按钮的 `UIPlaySound.clip` 读出来是 `TabButton`，
    //   但它**只是点击音**。把那个 clip 改成 `BigButton` 后，
    //   用户听到 BigButton 响了、**同时升级音也还在响** ——
    //   两个声音并存 ⇒ 升级音在另一条路径上。
    //
    //   于是改挂 `NGUITools.PlaySound`（全部 UI 音的汇聚点），
    //   点一次真实「升级」，日志就给出了答案：
    //     TabButton（点击反馈）+ WoodWork（2.48s 施工声）。
    //
    // 就是游戏原生升级会发的声音。
    internal static void NGUITools_PlaySound_Prefix(AudioClip clip, float volume, float pitch)
    {
        try
        {
            // 本探针只在 `sound_probe=true` 时才挂（见 Install），
            // 所以这里无需再查门控；用 Msg 是为了让取证结果**一定看得见**
            // （不会因为忘了开 diagnostics 而白跑一次）。
            Plugin.Log.Msg(
                $"[探针·NGUITools.PlaySound] clip={(clip == null ? "<null>" : clip.name)}" +
                $" volume={volume} pitch={pitch}");
        }
        catch (Exception e)
        {
            Plugin.LogError($"[ShiftClickUpgrade] PlaySound 探针异常：{e}");
        }
    }

    // ── 钩点 3：AreaBuildController.PlayerUpgradeBuilding ────────────────────

    internal static void AreaBuildController_PlayerUpgradeBuilding_Prefix(
        AreaBuildController __instance,
        AreaBuildingData targetBuilding)
    {
        try
        {
            _playerUpgradeHits++;

            // 受 `diagnostics` 门控 —— 纯取证日志：
            // 现在升级走 UI 按钮路径，本钩子只在“真有人调了
            // PlayerUpgradeBuilding”时才响（含回退路径）。
            Plugin.LogInfo(() =>
                $"[探针·升级入口] 第 {_playerUpgradeHits} 次 | {DescribeBuilding(targetBuilding)}");

            bool can = false;

            try
            {
                can = targetBuilding != null && targetBuilding.CanUpgrade();
            }
            catch (Exception e)
            {
                Plugin.LogInfo(() => $"[探针·升级入口] CanUpgrade() 读取失败：{e.GetType().Name}");
            }

            Plugin.LogInfo(() => $"[探针·升级入口] CanUpgrade={can}");
        }
        catch (Exception e)
        {
            Plugin.LogError($"[ShiftClickUpgrade] PlayerUpgradeBuilding 探针异常：{e}");
        }
    }

    // ── 补丁 3 / 4：UI 按钮回调 ──────────────────────────────────────────────

    internal static void BuildingUIController_UpgradeButtonClicked_Prefix(BuildingUIController __instance)
    {
        try
        {
            _upgradeButtonHits++;

            AreaBuildingData? target = null;

            try
            {
                target = __instance?.targetBuildingData;
            }
            catch
            {
                // targetBuildingData 可能在 IL2CPP 侧被释放，忽略。
            }

            // 受 `diagnostics` 门控 —— 纯取证。
            Plugin.LogInfo(() =>
                $"[探针·升级按钮] 第 {_upgradeButtonHits} 次 | {DescribeBuilding(target)}");
        }
        catch (Exception e)
        {
            Plugin.LogError($"[ShiftClickUpgrade] UpgradeButtonClicked 探针异常：{e}");
        }
    }

    internal static void BuildingUIController_SureUpgradeBuliding_Prefix(BuildingUIController __instance)
    {
        try
        {
            _sureUpgradeHits++;

            AreaBuildingData? target = null;

            try
            {
                target = __instance?.targetBuildingData;
            }
            catch
            {
                // 同上。
            }

            // 受 `diagnostics` 门控 —— 纯取证。
            Plugin.LogInfo(() =>
                $"[探针·确认升级] 第 {_sureUpgradeHits} 次 | {DescribeBuilding(target)}");
        }
        catch (Exception e)
        {
            Plugin.LogError($"[ShiftClickUpgrade] SureUpgradeBuliding 探针异常：{e}");
        }
    }

    // ── 挂载工具 ─────────────────────────────────────────────────────────────

    /// <summary>
    /// 解析目标方法并挂一个 <b>Prefix</b>，成功后打印<b>真实签名 + IL 地址 + patcher 类型</b>。
    ///
    /// <para>
    /// ⚠️ <b>必须用 <c>Patch(prefix:)</c> 而不是 <c>Patch(postfix:)</c></b>：
    /// Harmony 会把「返回 <c>bool</c> 且首参是 <c>__instance</c>」的方法优先解释成
    /// <b>透传 postfix</b>，然后因为原方法返回 <c>void</c> 而报
    /// <c>Return type of pass through postfix … does not match</c>。
    /// 本项目的补丁方法将来会返回 <c>bool</c>（为了跳过原点击），所以这里一律用 Prefix。
    /// </para>
    ///
    /// <para>
    /// ⚠️ <b>不要给补丁类加 <c>[HarmonyPatch]</c> 特性</b>：MelonLoader 会对整个程序集跑
    /// <c>PatchAll()</c>，扫到「光秃秃的 <c>[HarmonyPatch]</c> + 看起来像补丁的方法」
    /// 却无目标可绑，会每次启动报 <c>Undefined target method</c>（详见 harmony-il2cpp.md §5.8）。
    /// </para>
    /// </summary>
    private static int TryPatchPrefix(
        HarmonyLib.Harmony harmony,
        Type targetType,
        string methodName,
        string patchMethodName,
        int? parameterCount)
    {
        MethodInfo? target = Plugin.SelectOverload(targetType, methodName, parameterCount);

        if (target == null)
        {
            return 0;
        }

        MethodInfo? patch = typeof(ShiftClickUpgradePatch).GetMethod(
            patchMethodName,
            BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public);

        if (patch == null)
        {
            Plugin.LogError($"找不到补丁方法 {patchMethodName}（编译期就该发现，请检查改名）。");
            return 0;
        }

        try
        {
            harmony.Patch(target, prefix: new HarmonyMethod(patch));

            // 打印**真实绑定**：只打「挂载成功」是没用的 ——
            // 挂到空桩上和挂到真实现上，日志一模一样。
            long il = target.MethodHandle.GetFunctionPointer().ToInt64();

            Plugin.Log.Msg(
                $"已挂载补丁：{targetType.Name}.{target.Name} (IL=0x{il:x}) -> {patchMethodName}" +
                $" | patcher={Plugin.DescribePatcher(target)}");

            return 1;
        }
        catch (Exception e)
        {
            Plugin.LogError($"挂载 {targetType.Name}.{methodName} 失败：{e}");
            return 0;
        }
    }

    /// <summary>
    /// 与 <see cref="TryPatchPrefix"/> 同构，但挂的是 **Postfix**。
    ///
    /// <para>
    /// 【什么时候用 Postfix】当需要“原方法跑完之后的副作用”时 ——
    /// 例如道路升级：菜单与按钮是 <c>OnClick</c> 执行中才创建的，
    /// 只有 Postfix 才能拿到它们。
    /// </para>
    /// <para>
    /// ⚠️ 这些 Postfix **必须返回 <c>void</c>** ——
    /// 返回 <c>bool</c> 且首参为 <c>__instance</c> 会被 Harmony 当成“透传 postfix”，
    /// 而原方法返回 <c>void</c>，于是报 <c>Return type of pass through postfix …</c>
    /// （见 docs/harmony-il2cpp.md §5.3b）。
    /// </para>
    /// </summary>
    private static int TryPatchPostfix(
        HarmonyLib.Harmony harmony,
        Type targetType,
        string methodName,
        string patchMethodName,
        int? parameterCount)
    {
        MethodInfo? target = Plugin.SelectOverload(targetType, methodName, parameterCount);

        if (target == null)
        {
            return 0;
        }

        MethodInfo? patch = typeof(ShiftClickUpgradePatch).GetMethod(
            patchMethodName,
            BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public);

        if (patch == null)
        {
            Plugin.LogError($"找不到补丁方法 {patchMethodName}（编译期就该发现，请检查改名）。");
            return 0;
        }

        try
        {
            harmony.Patch(target, postfix: new HarmonyMethod(patch));

            long il = target.MethodHandle.GetFunctionPointer().ToInt64();

            Plugin.Log.Msg(
                $"已挂载补丁（Postfix）：{targetType.Name}.{target.Name} (IL=0x{il:x}) -> {patchMethodName}" +
                $" | patcher={Plugin.DescribePatcher(target)}");

            return 1;
        }
        catch (Exception e)
        {
            Plugin.LogError($"挂载 Postfix {targetType.Name}.{methodName} 失败：{e}");
            return 0;
        }
    }
}
