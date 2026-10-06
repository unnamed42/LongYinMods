using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Security.Cryptography;
using System.Threading;
using HarmonyLib;
using Il2Cpp;
using MelonLoader;
using MelonLoader.Utils;
using UnityEngine;

namespace Unnamed42.HeroVitalsFix;

/// <summary>
/// 修复「切换人物后，三维数值（生命 / 内力 / 体力）仍显示上一个人物的值」这个游戏显示 bug。
///
/// <para><b>根因</b>（机制详见 <c>docs/game-internals.md</c> §4）：</para>
/// <para>
/// 游戏为了让「每帧都调」的 <c>HeroData.SetHpBar/SetMpBar/SetPowerBar</c> 能提前返回，
/// 在 <c>HeroData</c> 上加了一组记账字段（<c>shownHpBarRoot</c> / <c>shownHp</c> / …）：
/// 守卫的语义是「<b>同一个控件 + 同样的数值 ⇒ 跳过</b>」。
/// </para>
/// <para>
/// 但记账挂在<b>角色</b>上，而详情面板的 <c>Hp/Mp/Power</c> 与战斗里的
/// <c>BattleUIPanel/NowActiveHero/*</c> 是<b>一个控件轮流给所有角色用</b>。
/// 于是「A → B → A」切回来时，A 的记账仍写着「控件上是我的值」⇒ 守卫跳过 ⇒ 控件留着 B 的数值。
/// </para>
///
/// <para><b>本补丁做什么</b>：在三个 setter 的 <b>Prefix</b> 里，如果发现「这个控件上一次是
/// 另一个角色写的」，就把当前角色的记账清空一次，于是游戏自己的守卫不再跳过、
/// 用<b>它自己的原生逻辑与格式</b>把三维重新画一遍。</para>
///
/// <para><b>为什么安全（单调性）</b>：本补丁唯一做的动作是「清空一个缓存」，
/// 它只可能让游戏<b>多写</b>、绝不可能让它<b>少写</b>。所以即使占用者表过期、
/// 即使官方后来自己修了这个 bug，最坏后果也只是多余的一次重绘，不会产生错误的数值。</para>
///
/// <para><b>性能</b>：只在「同一控件换人」时触发一次；同一角色重复调用
/// （<c>HudController.Update</c> 那条每帧路径）仍走游戏原本的提前返回，不增加开销。</para>
/// </summary>
public class Plugin : MelonMod
{
    internal const string HarmonyId = "com.unnamed42.herovitalsfix";

    private const string CategoryId = "HeroVitalsFix";
    private const string CategoryDisplay = "HeroVitalsFix";

    /// <summary>
    /// 已确认存在该 bug 的构建指纹（<c>GameAssembly.dll</c> 的 md5，逗号分隔）。
    /// 默认允许列表就是它 —— 游戏一更新，指纹就变了，本 mod 会自动拦截自己（见 <c>BuildGate</c>）。
    /// 发布新版本时把新指纹追到这里。
    /// </summary>
    internal const string KnownBadBuilds = "ea039aad075f14316a07016ba3625036";

    internal static MelonPreferences_Category Category = null!;
    internal static MelonLogger.Instance Log = null!;

    /// <summary>AllowList（默认，只在允许列表内的构建生效）/ BlockList / Off。</summary>
    internal static MelonPreferences_Entry<string> BuildGate = null!;

    internal static MelonPreferences_Entry<string> AllowedBuilds = null!;
    internal static MelonPreferences_Entry<string> BlockedBuilds = null!;

    /// <summary>信息性日志总开关（每次强制失效都会计数，日志受此门控）。</summary>
    internal static MelonPreferences_Entry<bool> Diagnostics = null!;

    private HarmonyLib.Harmony? _harmony;

    /// <summary>强制失效次数。**不受诊断开关门控** —— 「补丁到底跑没跑」的硬证据。</summary>
    private static int _forcedInvalidations;

    /// <summary>Prefix 内异常只报一次（每帧路径上不能刷屏）。</summary>
    private static int _prefixErrorLogged;

    // =================================================================
    // 生命周期
    // =================================================================

    public override void OnInitializeMelon()
    {
        Log = LoggerInstance;

        Category = MelonPreferences.CreateCategory(CategoryId, CategoryDisplay);

        BuildGate = Category.CreateEntry(
            "build_gate", "AllowList",
            "构建指纹放行模式",
            "AllowList = 只给「允许列表」内的构建打补丁（默认，游戏更新后自动失效、不误伤）；" +
            "BlockList = 除「屏蔽列表」外都打（想在未验证构建上试用时用）；" +
            "Off = 无条件打补丁（不推荐）。");

        AllowedBuilds = Category.CreateEntry(
            "allowed_builds", KnownBadBuilds,
            "允许的构建指纹",
            "逗号分隔的 GameAssembly.dll md5。游戏更新后若确认本 mod 仍需要，把启动日志里" +
            "那一行「当前构建指纹」复制到这里即可。");

        BlockedBuilds = Category.CreateEntry(
            "blocked_builds", "",
            "屏蔽的构建指纹",
            "仅 BuildGate=BlockList 时使用：这些 md5 的构建不打补丁（例如官方已自行修复的版本）。");

        Diagnostics = Category.CreateEntry(
            "diagnostics", false,
            "诊断日志",
            "打开后，每次「强制失效」（= 该控件换了角色、本补丁介入）都打一行。" +
            "关闭时只保留真正的错误；计数仍然进行。");

        // 先把自己是谁、跑的是哪一版打出来 —— 与部署核对 md5 直接对账。
        Assembly self = Assembly.GetExecutingAssembly();
        Log.Msg($"[自检] HeroVitalsFix v{self.GetName().Version} 自身 md5 = {Md5Of(self.Location)}");
        Log.Msg($"[自检] HarmonyId = {HarmonyId}");

        string fingerprint = ComputeFingerprint(out long asmSize, out long metaSize);

        Log.Msg(
            $"[指纹] 当前构建指纹 = {(fingerprint.Length == 0 ? "<读取失败>" : fingerprint)}" +
            $"（GameAssembly.dll {asmSize} 字节 / global-metadata.dat {metaSize} 字节）");

        if (!IsBuildAllowed(fingerprint))
        {
            // ★ 这是「按指纹拦截」的正常出口，不是错误：游戏更新后指纹对不上，就不动它。
            Log.Warning(
                $"[指纹] 该构建不在放行范围内（BuildGate={BuildGate.Value}）—— **本 mod 已停用，未挂任何补丁**。\n" +
                "         若确认这个版本仍有「切换人物后三维不刷新」的问题：\n" +
                $"         把上面那行指纹填入 UserData/MelonPreferences.cfg → [{CategoryId}] allowed_builds；\n" +
                "         若官方已修复，请直接删除本 mod。");
            return;
        }

        _harmony = new HarmonyLib.Harmony(HarmonyId);

        int patched = 0;
        patched += TryPatchPrefix("HeroData", "SetHpBar", nameof(Prefix_HeroData_SetHpBar), 1);
        patched += TryPatchPrefix("HeroData", "SetMpBar", nameof(Prefix_HeroData_SetMpBar), 1);
        patched += TryPatchPrefix("HeroData", "SetPowerBar", nameof(Prefix_HeroData_SetPowerBar), 1);

        Log.Msg($"[自检] 三维刷新补丁：{patched}/3 已挂载。");
    }

    public override void OnDeinitializeMelon()
    {
        _harmony?.UnpatchSelf();
        _harmony = null;
    }

    // =================================================================
    // 补丁：三个 setter 的 Prefix
    //
    // 参数名必须与游戏方法一致（hpBarRoot / mpBarRoot / powerBarRoot）——
    // Harmony 按名字绑定；名字错了会绑不上（前缀拿到 null），不会崩，但也不生效。
    // =================================================================

    internal static void Prefix_HeroData_SetHpBar(HeroData __instance, GameObject hpBarRoot)
        => InvalidateIfWidgetWasStolen(__instance, hpBarRoot, BarKind.Hp);

    internal static void Prefix_HeroData_SetMpBar(HeroData __instance, GameObject mpBarRoot)
        => InvalidateIfWidgetWasStolen(__instance, mpBarRoot, BarKind.Mp);

    internal static void Prefix_HeroData_SetPowerBar(HeroData __instance, GameObject powerBarRoot)
        => InvalidateIfWidgetWasStolen(__instance, powerBarRoot, BarKind.Power);

    private enum BarKind
    {
        Hp,
        Mp,
        Power,
    }

    /// <summary>
    /// 若该控件上一次是<b>别的角色</b>写的，就把当前角色的记账清掉，
    /// 让游戏自己的守卫不再提前返回。
    ///
    /// <para>然后无论是否清过，都把「本控件最后写入者」记成自己 —— 下次换人时就能发现。</para>
    /// </summary>
    private static void InvalidateIfWidgetWasStolen(HeroData hero, GameObject barRoot, BarKind kind)
    {
        try
        {
            if (hero == null || barRoot == null)
            {
                return;
            }

            if (BarOccupancy.OccupiedByOther(barRoot, hero))
            {
                switch (kind)
                {
                    case BarKind.Hp:
                        hero.shownHpBarRoot = null;
                        break;
                    case BarKind.Mp:
                        hero.shownMpBarRoot = null;
                        break;
                    default:
                        hero.shownPowerBarRoot = null;
                        break;
                }

                int n = Interlocked.Increment(ref _forcedInvalidations);
                if (Diagnostics.Value && n <= 50)
                {
                    Log.Msg($"[失效] {hero.heroName}(id={hero.heroID}) 的 {kind} 条 —— 控件换角色，记账已清空（第 {n} 次）");
                }
            }

            BarOccupancy.Take(barRoot, hero);
        }
        catch (Exception e)
        {
            // ⚠️ Prefix 绝不允许抛出去 —— 那会把游戏自己的调用打断。
            if (Interlocked.Exchange(ref _prefixErrorLogged, 1) == 0)
            {
                Log.Error($"[失效] Prefix 异常（已吞掉，后续同类不再报）：{e}");
            }
        }
    }

    // =================================================================
    // 控件占用者表
    // =================================================================

    /// <summary>
    /// 记住「每个控件最后是谁写的」。
    ///
    /// <para>键是控件的 <c>GetInstanceID()</c>。<b>容量有硬上限</b>（<see cref="MaxEntries"/>），
    /// 满了按 <b>LRU</b> 淘汰一条 —— 所以它<b>不会无限膨胀</b>（上限内存约几十 KB）。</para>
    ///
    /// <para><b>为什么用 LRU 而不是整体 `Clear()`</b>：过期条目在本补丁里只会造成「多清一次缓存」
    /// （多余重绘），本身无害；但整体 `Clear()` 会让<b>所有</b>控件在一小段时间内同时失去保护，
    /// 万一此刻正好切人，就会出现「这一次没修」。
    /// LRU 只淘汰最久没用过的条目 —— 战斗中大量产生/销毁的是各单位自己的跟随血条，
    /// 而真正共享的那 6 条（详情面板 3 + 战斗 NowActiveHero 3）一直在被触碰，不会被淘汰。</para>
    /// </summary>
    private static class BarOccupancy
    {
        private const int MaxEntries = 1024;

        /// <summary>
        /// 一条占用记录。
        ///
        /// <para><b>它不持有任何游戏对象的引用</b>：只存原生地址的<b>数值</b>（<see cref="IntPtr"/>）
        /// 与控件实例 id（<c>int</c> 键），所以不会阻止任何 <c>HeroData</c> / <c>GameObject</c>
        /// 被回收 —— 不存在「把对象钉住」那种泄漏。</para>
        ///
        /// <para>同理<b>不能</b>改成持有 <c>HeroData</c> 托管代理：Il2CppInterop 的代理会为原生对象
        /// 保留 GCHandle，那才会真的把对象钉住（例如战斗中产生的大量临时角色）。</para>
        /// </summary>
        private struct Entry
        {
            /// <summary>上次写入者的原生地址（仅作身份比较，**不解引用**）。</summary>
            public IntPtr Hero;

            /// <summary>上次写入者的 heroID —— 与地址一起比较，理由见 <see cref="OccupiedByOther"/>。</summary>
            public int HeroID;

            public LinkedListNode<int> Node;
        }

        private static readonly Dictionary<int, Entry> Map = new();
        private static readonly LinkedList<int> Recency = new();

        /// <summary>LRU 淘汰次数。**不受诊断开关门控** —— 供运行时核对容量行为。</summary>
        private static int _evictions;

        public static bool OccupiedByOther(GameObject barRoot, HeroData hero)
        {
            if (!Map.TryGetValue(barRoot.GetInstanceID(), out Entry entry))
            {
                return false; // 不知道谁写的 ⇒ 不干预，交给游戏自己的守卫
            }

            // 地址 + heroID **一起**比：只比地址的话，某个 HeroData 被回收、地址又在
            // 另一个 HeroData 上复用时，会被误判成「还是他」，从而漏掉一次失效。
            // （后果只是那一次不修、下一次写入就自愈；但多比一个 int 几乎不要钱。）
            bool sameHero = entry.Hero == hero.Pointer && entry.HeroID == hero.heroID;
            return !sameHero;
        }

        public static void Take(GameObject barRoot, HeroData hero)
        {
            int id = barRoot.GetInstanceID();

            if (Map.TryGetValue(id, out Entry entry))
            {
                // 已在表中 ⇒ 刷新为「最近使用」
                Recency.Remove(entry.Node);
                entry.Hero = hero.Pointer;
                entry.HeroID = hero.heroID;
                entry.Node = Recency.AddLast(id);
                Map[id] = entry;
                return;
            }

            if (Map.Count >= MaxEntries)
            {
                LinkedListNode<int>? oldest = Recency.First;
                if (oldest != null)
                {
                    Recency.RemoveFirst();
                    Map.Remove(oldest.Value);

                    int n = Interlocked.Increment(ref _evictions);
                    if (Diagnostics.Value && n <= 50)
                    {
                        Log.Msg($"[占用者表] 已达上限 {MaxEntries}，按 LRU 淘汰 1 条（第 {n} 次）");
                    }
                }
            }

            Map[id] = new Entry { Hero = hero.Pointer, HeroID = hero.heroID, Node = Recency.AddLast(id) };
        }
    }

    // =================================================================
    // 构建指纹
    // =================================================================

    /// <summary>
    /// 指纹 = <c>GameAssembly.dll</c> 的 md5。读不到就返回空串（⇒ 按「未验证构建」处理）。
    ///
    /// <para>用 <c>FileShare.ReadWrite</c> 打开：该文件此时已被加载为模块，必须允许共享读。</para>
    /// </summary>
    private static string ComputeFingerprint(out long asmSize, out long metaSize)
    {
        asmSize = 0;
        metaSize = 0;
        string md5 = string.Empty;

        try
        {
            string asmPath = Path.Combine(MelonEnvironment.GameRootDirectory, "GameAssembly.dll");
            var info = new FileInfo(asmPath);
            if (info.Exists)
            {
                asmSize = info.Length;
                md5 = Md5Of(asmPath);
            }
            else
            {
                Log.Warning($"[指纹] 找不到 {asmPath}");
            }
        }
        catch (Exception e)
        {
            Log.Warning($"[指纹] 读取 GameAssembly.dll 失败：{e.GetType().Name}: {e.Message}");
        }

        try
        {
            string metaPath = Path.Combine(Application.dataPath, "il2cpp_data", "Metadata", "global-metadata.dat");
            var info = new FileInfo(metaPath);
            if (info.Exists)
            {
                metaSize = info.Length;
            }
        }
        catch (Exception e)
        {
            Log.Warning($"[指纹] 读取 global-metadata.dat 失败：{e.GetType().Name}: {e.Message}");
        }

        return md5;
    }

    private static string Md5Of(string path)
    {
        try
        {
            using var stream = new FileStream(
                path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using MD5 md5 = MD5.Create();
            return Convert.ToHexString(md5.ComputeHash(stream)).ToLowerInvariant();
        }
        catch (Exception e)
        {
            Log.Warning($"[指纹] 计算 md5 失败（{path}）：{e.GetType().Name}: {e.Message}");
            return string.Empty;
        }
    }

    private static bool IsBuildAllowed(string fingerprint)
    {
        string mode = (BuildGate.Value ?? string.Empty).Trim();

        if (mode.Equals("Off", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        // 指纹读不到 ⇒ 无法确认构建 ⇒ 拦截（宁可不动，也不要误伤）。
        if (fingerprint.Length == 0)
        {
            return false;
        }

        if (mode.Equals("BlockList", StringComparison.OrdinalIgnoreCase))
        {
            return !ListContains(BlockedBuilds.Value, fingerprint);
        }

        return ListContains(AllowedBuilds.Value, fingerprint);
    }

    private static bool ListContains(string list, string value)
    {
        if (string.IsNullOrWhiteSpace(list))
        {
            return false;
        }

        string[] parts = list.Split(
            new[] { ',', ';', ' ', '\t', '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);

        foreach (string part in parts)
        {
            if (string.Equals(part.Trim(), value, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    // =================================================================
    // Harmony 挂载（沿用 FriendlyNoclip 的 TryPatch 写法：全部在 try/catch 内，
    // 目标缺失只记日志、绝不让 mod 崩游戏）
    // =================================================================

    /// <summary>与 FriendlyNoclip 的 <c>TryPatch</c> 相同，但挂成 <b>Prefix</b>。</summary>
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
                Log.Warning($"找不到游戏类型 Il2Cpp.{targetTypeName}。");
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
                Log.Warning($"找不到补丁方法 {patchName}。");
                return 0;
            }

            _harmony!.Patch(target, prefix: new HarmonyMethod(patch));

            // ★ 打印真实绑定的签名 ——「挂载成功」从此是可核对的断言，而不是布尔值。
            Log.Msg(
                $"已挂载前缀补丁：{targetTypeName}.{DescribeOverloads(new[] { target }, targetMethodName)} " +
                $"(IL={target.MethodHandle.GetFunctionPointer().ToInt64():x}) -> {patchName} " +
                $"| patcher={DescribePatcher(target)}");
            return 1;
        }
        catch (Exception e)
        {
            Log.Error($"挂载前缀补丁 {targetTypeName}.{targetMethodName} 失败：{e}");
            return 0;
        }
    }

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
    /// 查 Harmony 为某个目标方法实际选中的 MethodPatcher 是不是 <c>Il2CppDetourMethodPatcher</c>。
    /// 类型是它 ⇒ 原生 detour 已装（补丁会触发）；若是默认 MethodPatcher ⇒ 静默回退到托管 IL 补丁。
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

            string name = patcher.GetType().Name;

            PropertyInfo? isValid = patcher.GetType().GetProperty(
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
}
