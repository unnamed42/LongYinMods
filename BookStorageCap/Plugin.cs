using System;
using System.Reflection;
using MelonLoader;

namespace Unnamed42.BookStorageCap;

/// <summary>
/// 只改「藏经阁」容量上限 —— 不碰私人存储，也不碰门派仓库。
///
/// <para><b>为什么不能像第三方 mod 那样只写一次 <c>maxWeight</c>：</b>
/// <c>ForceData.bookStorage.maxWeight</c> 虽然是存档字段，但它是
/// <b>游戏重算出来的缓存</b> —— <c>GameController.CountForceData</c> 会把它
/// 覆盖成「藏经阁等级推导值」（实测反汇编：<c>movss [force+0xB8]+0x20</c>）。
/// 所以本 mod 的写法是「<b>在游戏重算之后再覆盖回去</b>」，而不是「写一次就完事」。</para>
///
/// <para>⚠️ <b>重算不是每帧发生的</b>（别照抄错判）：<c>GameController.LateUpdate</c> 里是
/// <c>cmp byte [force+0x10C],0; je skip; call CountForceData</c>，而 <c>+0x10C</c> 是
/// <c>ForceData.forceDetailDirty</c> ⇒ <b>脏标记驱动</b>。所以光靠 Postfix 会漏掉
/// 「读档后一直没置脏」的情形 —— 这正是下面兜底扫描存在的理由。</para>
///
/// <para><b>为什么不会误伤私人存储：</b>第三方 mod 挂的是
/// <c>TradeUIController.ShowTradeUI</c> 的 10 参数重载并**无差别**写形参
/// <c>rightItemList</c>；而私人存储（<c>HeroData.selfStorage</c>）与藏经阁
/// （<c>ForceData.bookStorage</c>）都从这里进 — 于是两个上限一起被改。
/// 本 mod 只按<b>容器身份</b>下手：<c>CountForceData</c> 的参数就是 <c>ForceData</c>，
/// 天然只可能是 <c>bookStorage</c>；UI 侧则要求
/// <c>ItemListType.BookType</c>（实测：该 10 参数重载共 6 个调用点，
/// <b>只有</b> <c>BuildingUIController.ManageBookStore</c> 这一条传 <c>BookType=3</c>，
/// 私人存储那条传的是 <c>None(-1)</c>）<b>并且</b>指针等于本门派 <c>bookStorage</c>。</para>
/// </summary>
public class Plugin : MelonMod
{
    internal const string HarmonyId = "com.unnamed42.bookstoragecap";

    private const string CategoryId = "BookStorageCap";
    private const string CategoryDisplay = "藏经阁上限";

    /// <summary>
    /// 静态日志句柄。<see cref="MelonBase.LoggerInstance"/> 是<b>实例</b>成员，
    /// 静态补丁方法里直接用会 CS0120（AGENTS.md §3.4）。
    /// </summary>
    internal static MelonLogger.Instance Log = null!;

    internal static MelonPreferences_Category Category = null!;

    /// <summary>总开关。关闭后本 mod 一个字节的游戏数据都不写。</summary>
    internal static MelonPreferences_Entry<bool> Enabled = null!;

    /// <summary>
    /// 目标上限。语义见 <see cref="Core.TargetValue"/>：
    /// <c>&gt;0</c> 设为该值；<c>-1</c> 不限（游戏原生哨兵）；<c>0</c> 或 <c>&lt;-1</c> 视为无效、不干预。
    /// </summary>
    internal static MelonPreferences_Entry<float> MaxWeight = null!;

    /// <summary>只改玩家所属门派的藏经阁（默认）；取消则遍历所有门派（会影响 NPC 门派）。</summary>
    internal static MelonPreferences_Entry<bool> OnlyPlayerForce = null!;

    /// <summary>排查用：打印每次写入的细节。默认关。</summary>
    internal static MelonPreferences_Entry<bool> Diagnostics = null!;

    private HarmonyLib.Harmony? _harmony;

    /// <summary>下一次兜底扫描的时间点，见 <see cref="OnUpdate"/>。</summary>
    private DateTime _nextSweep = DateTime.MinValue;

    /// <summary>构建指纹（版本 + 自身 md5），启动日志打印，用于和 <c>md5sum</c> 对账。</summary>
    internal static string BuildInfo { get; private set; } = "?";

    public override void OnInitializeMelon()
    {
        Log = LoggerInstance;

        Category = MelonPreferences.CreateCategory(CategoryId, CategoryDisplay);

        // 独立 cfg：便于配置管理器定位，也避免和别的 mod 挤在 MelonPreferences.cfg。
        try
        {
            Category.SetFilePath(System.IO.Path.Combine(
                AppDomain.CurrentDomain.BaseDirectory, "UserData", "BookStorageCap.cfg"));
        }
        catch (Exception e)
        {
            // 配置落盘路径失败不能影响功能：Entry 仍存在于内存模型里，配置管理器照样能改。
            LoggerInstance.Warning($"[BookStorageCap] 设置 cfg 路径失败（改用默认位置）：{e.Message}");
        }

        Enabled = Category.CreateEntry(
            "enabled", true,
            "启用藏经阁上限",
            "关闭后本 mod 完全不改写游戏数据（补丁仍在，但直接返回）。");

        MaxWeight = Category.CreateEntry(
            "max_weight", 999f,
            "藏经阁容量上限",
            ">0 = 设为该值；-1 = 尝试表示「不限」（游戏原生哨兵，但对藏经阁未实测过）；" +
            "0 / 负数 / NaN / 无穷 = 视为无效、不干预。" +
            "改完 ≤0.5 秒生效，不需要重开游戏 —— " +
            "但仅限「用配置管理器改」；直接手改 UserData/BookStorageCap.cfg 文件不会热加载。");

        OnlyPlayerForce = Category.CreateEntry(
            "only_player_force", true,
            "只改本门派藏经阁",
            "勾选（默认）：只改玩家所属门派的藏经阁。" +
            "取消：所有门派的藏经阁都改成该上限 —— 会影响 NPC 门派，慎用。");

        Diagnostics = Category.CreateEntry(
            "diagnostics", false,
            "诊断日志",
            "每次写入都打一条日志（含旧值/新值/触发来源）。排查用，日常请关掉。");

        _harmony = new HarmonyLib.Harmony(HarmonyId);
        int patched = BookStorageCapPatch.Install(_harmony);

        BuildInfo = ReadBuildStamp();

        LoggerInstance.Msg(
            $"BookStorageCap 初始化完成：挂载 {patched} 个补丁，" +
            $"enabled={Enabled.Value}, max_weight={MaxWeight.Value}, " +
            $"only_player_force={OnlyPlayerForce.Value}。" +
            $"【构建 {BuildInfo}】");
    }

    public override void OnDeinitializeMelon()
    {
        try
        {
            _harmony?.UnpatchSelf();
        }
        catch (Exception e)
        {
            // 卸载路径出错也不能把异常抛进 MelonLoader 的 teardown 流程。
            LoggerInstance.Warning($"[BookStorageCap] 卸载补丁失败（已忽略）：{e.Message}");
        }

        _harmony = null;
        LoggerInstance.Msg("BookStorageCap 已卸载补丁。");
    }

    /// <summary>
    /// 兜底扫描（每 0.5 秒一次，按<b>时间</b>而不是帧数 —— 帧数在 30fps 下会变成 2 秒）。
    ///
    /// <para><b>为什么需要它</b>：<c>CountForceData</c> 由 <c>ForceData.forceDetailDirty</c>
    /// 驱动（实测 <c>LateUpdate</c> 里 <c>cmp byte [force+0x10C],0; je skip</c>），
    /// 只在游戏自己认为脏了才重算。刚读档、或一直没置脏时，光靠那个 Postfix 就
    /// <b>永远轮不到我们</b> —— 上限会停在存档里的旧值。这个扫描保证「读档后 ≤0.5 秒」
    /// 配置值就生效，并且任何被游戏重算掉的漂移都会在一个扫描周期内自愈。</para>
    ///
    /// <para>代价：每 0.5 秒几次字段读取，可忽略。</para>
    /// </summary>
    public override void OnUpdate()
    {
        try
        {
            if (!Enabled.Value)
            {
                return;
            }

            DateTime now = DateTime.UtcNow;

            if (now < _nextSweep)
            {
                return;
            }

            _nextSweep = now.AddMilliseconds(500);
            Core.Sweep();
        }
        catch (Exception e)
        {
            // OnUpdate 抛异常会污染整个 Unity 主循环 —— 这里必须吞掉（AGENTS.md §3.4）。
            LogOnce("sweep-error", $"[BookStorageCap] 扫描异常（已忽略）：{e.GetType().Name}: {e.Message}");
        }
    }

    /// <summary>启动日志里打印的版本 + md5；取不到就算了（诊断绝不能影响启动）。</summary>
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
    /// 同一 key 只打一次的日志 —— 用于「每秒都会发生」的错误，避免刷屏。
    /// </summary>
    private static readonly System.Collections.Generic.HashSet<string> LoggedOnce = new();

    internal static void LogOnce(string key, string message)
    {
        try
        {
            if (LoggedOnce.Add(key))
            {
                Log.Warning(message);
            }
        }
        catch
        {
            // 日志本身出错绝不能影响游戏逻辑。
        }
    }
}
