using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using Il2Cpp;
using MelonLoader;

namespace Unnamed42.ShiftClickUpgrade;

/// <summary>
/// Shift+单击直接升级建筑。
///
/// <para><b>【当前阶段：脚手架 + 只读探针】</b> —— 本文件只搭好 MelonLoader / Harmony
/// 的骨架与开关，<b>还没有接上真正的升级调用</b>。</para>
///
/// <para>
/// 之所以先落在「探针」而不是直接改行为，是因为这条链路上有一个**尚未验证的前提**：
/// 「按住 Shift 点建筑图标」到「升级按钮被点击」之间，游戏到底走了哪条路。
/// 本项目（见 <c>docs/friendlynoclip.md</c> §2.2d）真金白银买到的教训是：
/// <i>「X 是两条路径的交汇点」这种判断必须用调用者扫描 / 活体验证确认，
/// 不能因为名字听起来像入口就当成入口。</i>
/// </para>
///
/// <para><b>候选钩点（按优先级，均已确认是 IL2CPP 托管可调用方法）：</b></para>
/// <list type="number">
///   <item><c>AreaBuildingIconController.OnClick()</c> —— 建筑图标的点击回调。
///     由 <c>MouseController.Notify(go, "OnClick", obj)</c> 反射派发，
///     所以「按住 Shift 时点击」这个事件必然经过它。<b>这是最可靠的拦截点</b>。</item>
///   <item><c>AreaBuildController.PlayerUpgradeBuilding(AreaBuildingData)</c> ——
///     带参数的真正入口，参数就是**要升级的那栋建筑**，
///     因此不需要从「当前选中的 UI」反推目标。</item>
///   <item><c>BuildingUIController.UpgradeButtonClicked()</c> /
///     <c>SureUpgradeBuliding()</c> —— UI 按钮回调，可能带二次确认弹窗。</item>
/// </list>
///
/// <para>
/// 探针阶段只做一件事：<b>把「谁被点了、点是哪栋建筑、各候选钩子的触发顺序」
/// 打成不受门控的日志</b>，用一次冷启动 + 几次 Shift 点击把链路钉死，
/// 再在 <see cref="ShiftClickUpgradePatch"/> 里接上真正的升级调用。
/// </para>
/// </summary>
public class Plugin : MelonMod
{
    internal const string HarmonyId = "com.unnamed42.shiftclickupgrade";

    private const string CategoryId = "ShiftClickUpgrade";
    private const string CategoryDisplay = "Shift Click Upgrade";

    /// <summary>
    /// 静态日志句柄。
    /// <see cref="MelonBase.LoggerInstance"/> 是<b>实例</b>成员，静态补丁方法里直接用会 CS0120。
    /// </summary>
    internal static MelonLogger.Instance Log = null!;

    internal static MelonPreferences_Category Category = null!;

    /// <summary>
    /// 功能总开关。关闭后补丁仍然挂着（用于观察触发），但不会真的触发升级。
    /// </summary>
    internal static MelonPreferences_Entry<bool> Enabled = null!;

    /// <summary>
    /// 诊断日志总开关，控制全部信息性日志（见 <see cref="LogInfo"/>）。
    ///
    /// <para>
    /// 沿用 <c>FriendlyNoclip</c> 的策略：把门控收到 <see cref="LogInfo"/> <b>一处</b>，
    /// 而不是让每个调用点各自 <c>if (Diagnostics.Value)</c> ——
    /// 后者在新增日志时<b>必然漏掉</b>。
    /// </para>
    /// </summary>
    internal static MelonPreferences_Entry<bool> Diagnostics = null!;


    /// <summary>
    /// 音效取证探针：挂 `NGUITools.PlaySound` 记录每个 UI 音效的 clip 名。
    ///
    /// <para>
    /// 默认关。它的作用是**以后游戏更新、音效变了时能重新抓到真名** ——
    /// 本 mod 正是靠它得出「升级音 = WoodWork」的。
    /// </para>
    /// </summary>
    internal static MelonPreferences_Entry<bool> SoundProbe = null!;

    /// <summary>
    /// 探针模式：只打日志、不改行为。脚手架阶段默认 <c>true</c>。
    /// </summary>
    internal static MelonPreferences_Entry<bool> ProbeOnly = null!;

    private HarmonyLib.Harmony? _harmony;

    /// <summary>构建指纹（版本号 + 自身 md5），启动时打印。判断产物是否一致以 md5 为准。</summary>
    internal static string BuildInfo { get; private set; } = "?";

    public override void OnInitializeMelon()
    {
        Log = LoggerInstance;

        Category = MelonPreferences.CreateCategory(CategoryId, CategoryDisplay);

        Enabled = Category.CreateEntry(
            "enabled", true,
            "启用 Shift+单击升级",
            "按住 Shift 单击建筑图标时，直接触发该建筑的升级。");

        Diagnostics = Category.CreateEntry(
            "diagnostics", true,
            "诊断日志",
            "总开关：控制全部排查型日志（点击/地块/锤子/升级入口/按钮/确认）。" +
            "关闭时只保留「★ 通过 UI 按钮触发升级」、挂载失败等关键行。建议日常关掉。");

        SoundProbe = Category.CreateEntry(
            "sound_probe", false,
            "音效取证探针",
            "挂 NGUITools.PlaySound 并把每次播放的 clip 名打进日志。" +
            "仅用于游戏更新后重新确认音效名，默认关。");

        ProbeOnly = Category.CreateEntry(
            "probe_only", true,
            "只探针不改行为",
            "脚手架阶段：只打印点击链路，不真的触发升级。确认链路后再关掉。");

        _harmony = new HarmonyLib.Harmony(HarmonyId);

        int patched = ShiftClickUpgradePatch.Install(_harmony);

        BuildInfo = ReadBuildStamp();

        LoggerInstance.Msg(
            $"ShiftClickUpgrade 初始化完成：挂载 {patched} 个补丁" +
            $"，enabled={Enabled.Value}" +
            $"，probe_only={ProbeOnly.Value}" +
            $"。【构建 {BuildInfo}】");
    }

    public override void OnDeinitializeMelon()
    {
        _harmony?.UnpatchSelf();
        _harmony = null;
        LoggerInstance.Msg("ShiftClickUpgrade 已卸载补丁。");
    }

    /// <summary>
    /// 构建指纹：静态版本号 + 自身 md5。
    /// 取不到就算了 —— 诊断信息绝不能影响启动。
    ///
    /// ★ 不读 InformationalVersion：那曾是「构建时刻」的载体，
    ///   但时间戳进程序集内容会破坏 <Deterministic>，
    ///   使同名源码两次构建 checksum 不同（已移除）。
    ///   现在「跑的是不是新产物」以 md5 为准，启动日志直接打出来便于对账。
    /// </summary>
    private static string ReadBuildStamp()
    {
        try
        {
            Assembly asm = Assembly.GetExecutingAssembly();
            string v = asm.GetName().Version?.ToString() ?? "?";

            return string.IsNullOrEmpty(asm.Location)
                ? v
                : $"{v} md5={AssemblyVersionHash.OfFile(asm.Location)}";
        }
        catch
        {
            return "?";
        }
    }

    /// <summary>
    /// 写一条<b>信息性</b>日志，受 <see cref="Diagnostics"/> 门控。
    /// 未开诊断时完全静默（连字符串都不拼接）。
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

    /// <summary>写一条<b>错误</b>日志，<b>永不受门控</b> —— 出错时必须看得见。</summary>
    internal static void LogError(string message) => Log.Warning(message);

    /// <summary>
    /// 查 Harmony 为目标方法实际选中的 <c>MethodPatcher</c> 类型与 <c>IsValid</c>。
    ///
    /// <para>
    /// <c>Il2CppDetourMethodPatcher</c> 是 <c>internal</c>，<c>IsValid</c> 也是 internal，
    /// 编译期拿不到，但「Harmony 到底有没有把原生 detour 装上」不能靠读源码推断 ——
    /// 必须运行时量出来（见 <c>docs/harmony-il2cpp.md</c> §5.5）。
    /// </para>
    /// <para>
    /// 类型是 <c>Il2CppDetourMethodPatcher [IsValid=True]</c> ⇒ 原生 detour 已装，补丁应触发；
    /// 是默认的 <c>MethodPatcher</c> ⇒ 静默回退到托管 IL 补丁，原生调用碰不到。
    /// </para>
    /// </summary>
    internal static string DescribePatcher(MethodBase target)
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
                ? "（该类型无 IsValid）"
                : $"IsValid={isValid.GetValue(patcher)}";

            return $"{pt.Name} [{valid}]";
        }
        catch (Exception e)
        {
            return $"探测失败：{e.GetType().Name}: {e.Message}";
        }
    }

    /// <summary>
    /// 按<b>名称 + 参数个数</b>选取目标重载，并打印真实绑定的签名 + IL 地址。
    ///
    /// <para>
    /// 【为什么必须这样】<c>Type.GetMethod(name)</c> 在<b>同名重载</b>下返回哪一个<b>不确定</b>。
    /// 本项目真实踩过：同一个类型同时有 <c>void Foo()</c>（空桩）与 <c>void Foo(Bar)</c>（真实现），
    /// 而 <c>OnLeave()</c> 的同名条目在 <c>script.json</c> 里<b>出现两条</b>。
    /// 用参数个数把重载唯一化，并<b>打印真实签名 + IL 地址</b> ——
    /// 只打「挂载成功」是没用的，挂到空桩上和挂到真实现上日志一模一样。
    /// </para>
    /// </summary>
    internal static MethodInfo? SelectOverload(Type targetType, string methodName, int? parameterCount)
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
                "。同名候选：" + DescribeOverloads(candidates, methodName));
            return null;
        }

        if (matched.Count > 1)
        {
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
}
