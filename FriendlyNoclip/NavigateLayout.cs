using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;
using Il2CppInterop.Runtime;

namespace Unnamed42.FriendlyNoclip;

/// <summary>一个 hook 落点（全部是**运行时**地址）。</summary>
internal sealed class HookPoint
{
    /// <summary>被替换的那条 6 字节 <c>jcc rel32</c>。</summary>
    internal ulong Site;

    /// <summary>该 <c>jcc</c> 的跳转目标（函数内的 continue 块）。</summary>
    internal ulong Skip;

    /// <summary>该 <c>jcc</c> 不跳时的落点（= <see cref="Site"/> + 6）。</summary>
    internal ulong Pass;

    /// <summary>操作码首字节（<c>0F</c>）。</summary>
    internal byte Op0;

    /// <summary>操作码次字节（<c>85</c> = <c>jne</c> / <c>84</c> = <c>je</c>）。</summary>
    internal byte Op1;

    internal string Describe() =>
        $"落点 0x{Site:x}（静态 0x{NativeMemory.RuntimeToStaticVa((IntPtr)(long)Site):x}，" +
        $"操作码 {Op0:x2} {Op1:x2}）→ skip 0x{Skip:x}（静态 0x{NativeMemory.RuntimeToStaticVa((IntPtr)(long)Skip):x}）、" +
        $"pass 0x{Pass:x}（静态 0x{NativeMemory.RuntimeToStaticVa((IntPtr)(long)Pass):x}）";
}

/// <summary>
/// 本 mod 两个原生 detour 的**全部定位信息** —— 在运行时从游戏自己那里问出来，
/// 而不是写死地址。
///
/// <para>
/// 【要解决的问题】游戏一更新，<c>MapNavigator.Navigate</c> 的绝对地址全变
/// （2026-10：整个函数搬了 <c>-0x3aa60</c>，连函数内偏移都变了）。
/// 于是「硬编码 VA + 字节校验」的模式必然演变成本次事故：
/// <b>落点校验失败 → detour 静默不装 → 功能消失且没人知道</b>。
/// </para>
///
/// <para>
/// 【三层来源，从硬到软】
/// <list type="number">
///   <item><b>函数入口</b>：问 IL2CPP 要 <c>MapNavigator.Navigate</c> 的
///         <c>methodPointer</c>。这是游戏自己的符号表，随 metadata 走，不随地址走。</item>
///   <item><b>函数边界</b>：从已加载映像的 PE <c>.pdata</c> 二分出 <c>[start, end)</c>，
///         使「函数体内的形态匹配」有确定范围。</item>
///   <item><b>指令落点</b>：用 <see cref="NavigateScanner"/> 在函数体内按**指令语义**
///         找两个 <c>jcc</c>，并顺带读出 <c>gridType</c>/<c>battleUnit</c> 的字段偏移
///         与 <c>selfTeamID</c> 的栈槽偏移。</item>
/// </list>
/// <b>字段偏移</b>再向 IL2CPP 元数据（<c>il2cpp_field_get_offset</c>）核对一次：
/// 反汇编给的值与元数据不一致就直接失败关闭 —— 两个独立来源互相证伪时不许猜。
/// 元数据取不到（类尚未初始化等）时退回本构建实测常量，并打 WARNING（看得见的不确定，好过静默）。
/// </para>
///
/// <para>
/// 【失败策略】任何一步不成立都返回 <c>false</c> + 一句人读得懂的原因，
/// 由调用方记 WARNING 后**放弃安装** —— 绝不猜、绝不抛异常、绝不让游戏崩。
/// </para>
/// </summary>
internal sealed class NavigateLayout
{
    private const string AssemblyName = "Assembly-CSharp.dll";
    private const string NavigateClass = "MapNavigator";
    private const string NavigateMethod = "Navigate";

    /// <summary><c>Navigate(BattleMapData, GridUnitData, GridUnitData, List, List, int, int)</c> = 7 个参数。</summary>
    private const int NavigateParamCount = 7;

    /// <summary>读函数体的上限：<c>.pdata</c> 不可用时按它截断，防止读爆内存。</summary>
    private const int MaxFunctionBytes = 0x4000;

    /// <summary>候选入口（methodPointer / 下一个相邻函数 / 跳转目标）最多试几个。</summary>
    private const int MaxCandidates = 8;

    /// <summary>字段偏移的合法上限（超过说明拿到的不是实例字段偏移）。</summary>
    private const uint MaxFieldOffset = 0x1000;

    // ---- 本构建（2026-10-09，GameAssembly.dll md5=ac0ec1a3…）实测的兜底常量 ----
    //
    // 只在 IL2CPP 元数据取不到字段偏移时使用，且**每用一次打一条 WARNING**。
    // 它们同时也是「上一版能工作的那组值」，所以退回去不会比现状更差。
    private const int FallbackGridType = 0x14;
    private const int FallbackBattleUnit = 0x18;
    private const int FallbackObstale = 0x30;
    private const int FallbackObstacleType = 0x10;
    private const int FallbackObstacleTeam = 0x2C;
    private const int FallbackBattleTeam = 0x58;
    private const int FallbackTeamId = 0x10;

    /// <summary>城防 hook 的落点。</summary>
    internal HookPoint Wall = null!;

    /// <summary>穿友方 hook 的落点。</summary>
    internal HookPoint Friendly = null!;

    // ---- stub 要用到的字段/栈槽偏移 ----
    internal int OffGridType;
    internal int OffBattleUnit;
    internal int OffObstale;
    internal int OffObstacleType;
    internal int OffObstacleTeam;
    internal int OffBattleTeam;
    internal int OffTeamId;
    internal int SelfTeamIdStackOffset;

    // ---- 诊断信息 ----
    internal ulong Entry;
    internal ulong FunctionStart;
    internal ulong FunctionEnd;
    internal bool BoundsFromPdata;
    internal int InstructionCount;

    /// <summary>本次定位的耗时（毫秒）—— 只为了回答「会不会拖慢启动」。</summary>
    internal double ElapsedMs;

    internal int FallbackOffsetCount;
    internal ulong IsAliveCall;
    internal bool SameSkipTarget;

    private static NavigateLayout? _cached;
    private static string _failure = string.Empty;
    private static bool _attempted;

    /// <summary>
    /// 拿到（并缓存）定位结果。失败时 <paramref name="reason"/> 是一句可读的原因。
    /// 本方法**只记日志、不抛异常**。
    /// </summary>
    internal static bool TryGet(out NavigateLayout? layout, out string reason)
    {
        if (!_attempted)
        {
            _attempted = true;
            _cached = Resolve(out _failure);
        }

        layout = _cached;
        reason = _failure;
        return layout != null;
    }

    /// <summary>
    /// 定位的**唯一入口**：把一切异常收成「一句原因」。
    ///
    /// <para>
    /// 本方法在 mod 初始化路径上被调用，**任何异常都不允许穿过它** ——
    /// 定位失败只应让功能不生效，而不是打断游戏启动。
    /// </para>
    /// </summary>
    private static NavigateLayout? Resolve(out string reason)
    {
        try
        {
            return ResolveCore(out reason);
        }
        catch (Exception e)
        {
            reason = $"定位时异常：{e.GetType().Name}: {e.Message}";
            return null;
        }
    }

    private static NavigateLayout? ResolveCore(out string reason)
    {
        reason = string.Empty;

        // 只为了回答「会不会拖慢启动」—— 结果写进下面的汇总日志。
        var stopwatch = Stopwatch.StartNew();


        if (NativeMemory.ModuleBase == IntPtr.Zero && !NativeMemory.ResolveModule())
        {
            reason = "找不到 GameAssembly.dll 模块基址。";
            return null;
        }

        // ---- ① 函数入口：问 IL2CPP，而不是问地址 ----
        if (!TryResolveMethodPointer(out IntPtr entry, out reason))
        {
            return null;
        }

        // ---- ②③ 函数边界 + 指令形态匹配（候选 BFS）----
        //
        // ⚠️⭐ **methodPointer 常常不是真身**。2026-10-09 实机现场：
        //    MapNavigator.Navigate 的 methodPointer = 0x180a52b40，那是 IL2CPP 的
        //    **class-init wrapper**（先跑类初始化，再**顺序落入**真身 0x180a52c2e）。
        //    两者是 .pdata 里首尾相接的两条函数记录 —— 所以「下一个相邻函数」是头号候选。
        //    另外人装的 detour / 跳转 thunk 也常见，那类靠「函数体内的直接跳转目标」覆盖。
        IntPtr start = IntPtr.Zero;
        IntPtr end = IntPtr.Zero;
        bool fromPdata = false;
        NavigateScanResult? scan = null;
        string firstFailure = string.Empty;

        var tried = new List<IntPtr>();
        var pending = new Queue<IntPtr>();
        pending.Enqueue(entry);

        while (pending.Count > 0)
        {
            IntPtr candidate = pending.Dequeue();

            if (tried.Contains(candidate))
            {
                continue;
            }

            tried.Add(candidate);

            if (TryScanFunction(candidate, out start, out end, out fromPdata, out scan, out reason))
            {
                if (candidate.ToInt64() != entry.ToInt64())
                {
                    Plugin.Log.Msg(
                        $"[定位] methodPointer 0x{entry.ToInt64():x} 不是函数真身；" +
                        $"真身在 0x{candidate.ToInt64():x}（静态 0x{NativeMemory.RuntimeToStaticVa(candidate):x}）—— 已按真身定位。");
                }

                entry = candidate;
                break;
            }

            if (firstFailure.Length == 0)
            {
                firstFailure = reason;
            }

            if (tried.Count >= MaxCandidates)
            {
                continue;
            }

            // 候选的「逻辑延续」：下一个相邻函数 + 它体内的直接跳转目标。
            if (NativeMemory.TryGetNextFunctionStart(candidate, out IntPtr following))
            {
                pending.Enqueue(following);
            }

            EnqueueJumpTargets(candidate, pending);
        }

        if (scan == null)
        {
            reason = $"试了 {tried.Count} 个候选入口都不匹配。首个失败原因：{firstFailure}";
            return null;
        }

        long size = end.ToInt64() - start.ToInt64();

        var scanned = scan!;

        var layout = new NavigateLayout
        {
            Entry = (ulong)entry.ToInt64(),
            FunctionStart = (ulong)start.ToInt64(),
            FunctionEnd = (ulong)start.ToInt64() + (ulong)size,
            BoundsFromPdata = fromPdata,
            InstructionCount = scanned.InstructionCount,
            IsAliveCall = scanned.IsAliveCall,
            SameSkipTarget = scanned.SameSkipTarget,

            Wall = ToHookPoint(scanned.Wall!),
            Friendly = ToHookPoint(scanned.Friendly!),
        };

        // ---- ④ 字段偏移：反汇编给的两个是**权威**，其余问 IL2CPP 元数据 ----
        if (!ResolveFieldOffsets(layout, scanned, out reason))
        {
            return null;
        }

        layout.SelfTeamIdStackOffset = scanned.SelfTeamIdStackOffset;

        // ---- ⑤ 一次性把「找到了什么」打出来 ----
        //    早期这里只在 diagnostics 打开时输出，结果排查「为什么没装上」时
        //    连一句地址都看不到 —— 定位结果是**启动期一次性**信息，不该被门控。
        layout.ElapsedMs = stopwatch.Elapsed.TotalMilliseconds;
        Plugin.Log.Msg(layout.Summary());

        return layout;
    }

    private static HookPoint ToHookPoint(ScannedHook hook) => new()
    {
        Site = hook.Site,
        Skip = hook.Skip,
        Pass = hook.Pass,
        Op0 = hook.Op0,
        Op1 = hook.Op1,
    };

    private static bool ResolveFieldOffsets(NavigateLayout layout, NavigateScanResult scan, out string reason)
    {
        reason = string.Empty;

        IntPtr gridUnit = TryGetClass("GridUnitData", out bool gridUnitInited);
        IntPtr obstacle = TryGetClass("ObstacleData", out bool obstacleInited);
        IntPtr battleUnit = TryGetClass("BattleUnit", out bool battleUnitInited);
        IntPtr battleTeam = TryGetClass("BattleTeam", out bool battleTeamInited);

        // gridType / battleUnit：**反汇编是权威**（stub 就是要复现它看到的那条指令）。
        // 元数据只用来互相印证；两者不一致说明有一方错了，直接失败关闭。
        layout.OffGridType = scan.GridTypeOffset;
        layout.OffBattleUnit = scan.BattleUnitOffset;

        if (TryFieldOffset(gridUnit, "gridType", out int metaGridType) &&
            metaGridType != scan.GridTypeOffset)
        {
            reason =
                $"gridType 偏移互相矛盾：Navigate 里的 cmp 用 +0x{scan.GridTypeOffset:x}，" +
                $"而 IL2CPP 元数据说 +0x{metaGridType:x}。拒绝按猜测定制 stub。";
            return false;
        }

        if (TryFieldOffset(gridUnit, "battleUnit", out int metaBattleUnit) &&
            metaBattleUnit != scan.BattleUnitOffset)
        {
            reason =
                $"battleUnit 偏移互相矛盾：Navigate 里的 mov 用 +0x{scan.BattleUnitOffset:x}，" +
                $"而 IL2CPP 元数据说 +0x{metaBattleUnit:x}。拒绝按猜测定制 stub。";
            return false;
        }

        // 其余四个没有「同函数的指令」可以印证，只能以元数据为准，
        // 取不到时才退回本构建实测常量（并计数，汇总日志里会写出来）。
        layout.OffObstale = OffsetOrFallback(gridUnit, gridUnitInited, "obstale", FallbackObstale, layout);
        layout.OffObstacleType = OffsetOrFallback(obstacle, obstacleInited, "obstalceType", FallbackObstacleType, layout);
        layout.OffObstacleTeam = OffsetOrFallback(obstacle, obstacleInited, "teamID", FallbackObstacleTeam, layout);
        layout.OffBattleTeam = OffsetOrFallback(battleUnit, battleUnitInited, "battleTeam", FallbackBattleTeam, layout);
        layout.OffTeamId = OffsetOrFallback(battleTeam, battleTeamInited, "ID", FallbackTeamId, layout);

        return true;
    }

    private static int OffsetOrFallback(
        IntPtr klass, bool inited, string fieldName, int fallback, NavigateLayout layout)
    {
        if (TryFieldOffset(klass, fieldName, out int offset))
        {
            return offset;
        }

        layout.FallbackOffsetCount++;

        Plugin.Log.Warning(
            $"[定位] 取不到字段偏移 {fieldName}（类{(klass == IntPtr.Zero ? "未找到" : inited ? "已初始化" : "未初始化")}），" +
            $"暂用本构建实测值 +0x{fallback:x}。");

        return fallback;
    }

    /// <summary>
    /// 在 <paramref name="entry"/> 所在函数的 <c>[start, end)</c> 里跑一次形态扫描。
    ///
    /// <para>
    /// 失败时 <paramref name="reason"/> 带**诊断信息**（解码了多少条指令 + 首 16 字节）——
    /// 这两个数字能把病因分开：「解码 0 条」= 字节不对 / 读了错地方；
    /// 「解码几百条但形态不匹配」= 代码形态真的变了。
    /// </para>
    /// </summary>
    private static bool TryScanFunction(
        IntPtr entry, out IntPtr start, out IntPtr end, out bool fromPdata,
        out NavigateScanResult? scan, out string reason)
    {
        reason = string.Empty;
        scan = null;

        // ---- 函数边界：.pdata ----
        fromPdata = NativeMemory.TryGetFunctionBounds(entry, out start, out end);

        if (fromPdata && start.ToInt64() != entry.ToInt64())
        {
            Plugin.Log.Warning(
                $"[定位] 方法指针 0x{entry.ToInt64():x} 不是 .pdata 里函数的起点 " +
                $"0x{start.ToInt64():x}（可能是 thunk），按函数起点扫描。");
        }

        if (!fromPdata)
        {
            start = entry;
            end = (IntPtr)(entry.ToInt64() + MaxFunctionBytes);

            Plugin.Log.Warning("[定位] 读不到 .pdata 的函数边界，改按固定窗口扫描（形态仍要求唯一命中）。");
        }

        long size = Math.Min(end.ToInt64() - start.ToInt64(), MaxFunctionBytes);

        if (size < 32)
        {
            reason = $"函数体只有 {size} 字节（.pdata: {fromPdata}），不足以扫描。";
            return false;
        }

        if (!NativeMemory.TryReadBytes(start, (int)size, out byte[] code))
        {
            reason = $"读不出函数体字节（0x{start.ToInt64():x}，{size} 字节）。";
            return false;
        }

        // ---- 指令层面的形态匹配（见 NavigateScanner）----
        if (!NavigateScanner.TryScan(code, (ulong)start.ToInt64(), out scan, out reason))
        {
            int decoded = scan?.InstructionCount ?? 0;
            int head = Math.Min(16, code.Length);

            reason =
                $"在 [{start.ToInt64():x},{start.ToInt64() + size:x}) 里匹配 hook 形态失败：{reason}" +
                $"（入口 0x{entry.ToInt64():x} / 静态 0x{NativeMemory.RuntimeToStaticVa(entry):x}；" +
                $"解码 {decoded} 条指令；首 {head} 字节 {Hex(code, head)}）";

            scan = null;
            return false;
        }

        return true;
    }

    /// <summary>
    /// 把一个候选函数的「逻辑延续」加入队列：
    /// ① 入口处就是一条跳转（thunk / 别人装的 detour）→ 跳转目标；
    /// ② 函数体内所有**直接 <c>jmp</c>** 的目标。
    /// </summary>
    private static void EnqueueJumpTargets(IntPtr candidate, Queue<IntPtr> pending)
    {
        if (TryFollowDirectJump(candidate, out IntPtr jumped, out _))
        {
            pending.Enqueue(jumped);
        }

        if (!NativeMemory.TryGetFunctionBounds(candidate, out IntPtr start, out IntPtr end))
        {
            return;
        }

        long size = Math.Min(end.ToInt64() - start.ToInt64(), MaxFunctionBytes);

        if (size < 5 || size > 0x10000 || !NativeMemory.TryReadBytes(start, (int)size, out byte[] code))
        {
            return;
        }

        foreach (ulong target in NavigateScanner.CollectDirectJumpTargets(code, (ulong)start.ToInt64()))
        {
            pending.Enqueue((IntPtr)(long)target);
        }
    }

    /// <summary>
    /// 入口处若是一条**直接跳转**（IL2CPP 的 thunk、或别人装的 detour），给出跳转目标。
    ///
    /// <para>只认 <c>E9 rel32</c> 与 <c>FF 25 disp32</c>（后者要去指针槽里读目标）。</para>
    /// </summary>
    private static bool TryFollowDirectJump(IntPtr entry, out IntPtr target, out string kind)
    {
        target = IntPtr.Zero;
        kind = string.Empty;

        try
        {
            if (!NativeMemory.TryReadBytes(entry, 6, out byte[] bytes) || bytes.Length < 6)
            {
                return false;
            }

            if (bytes[0] == 0xE9)
            {
                target = (IntPtr)(entry.ToInt64() + 5 + BitConverter.ToInt32(bytes, 1));
                kind = "jmp rel32（thunk）";
                return true;
            }

            if (bytes[0] == 0xFF && bytes[1] == 0x25)
            {
                IntPtr slot = (IntPtr)(entry.ToInt64() + 6 + BitConverter.ToInt32(bytes, 2));
                IntPtr pointer = NativeMemory.ReadPointer(slot);

                if (pointer != IntPtr.Zero)
                {
                    target = pointer;
                    kind = "jmp [rip+disp]（detour）";
                    return true;
                }
            }
        }
        catch (Exception e)
        {
            Plugin.Log.Warning($"[定位] 读取入口 0x{entry.ToInt64():x} 的跳转失败：{e.Message}");
        }

        return false;
    }

    private static string Hex(byte[] bytes, int count)
    {
        var sb = new System.Text.StringBuilder(count * 3);

        for (int i = 0; i < count; i++)
        {
            if (i > 0)
            {
                sb.Append(' ');
            }

            sb.Append(bytes[i].ToString("x2"));
        }

        return sb.ToString();
    }

    /// <summary>
    /// 解析 <c>MapNavigator.Navigate</c> 的原生入口地址。
    ///
    /// <para>
    /// <c>Il2CppMethodInfo</c> 的**第 0 个字段**就是 <c>methodPointer</c>
    /// （metadata v27 —— 见 Il2CppInterop 的 <c>NativeMethodInfoStructHandler_24_1</c>），
    /// 所以 <c>Marshal.ReadIntPtr(methodInfo)</c> 即入口地址。
    /// </para>
    /// </summary>
    private static bool TryResolveMethodPointer(out IntPtr entry, out string reason)
    {
        entry = IntPtr.Zero;
        reason = string.Empty;

        IntPtr klass = TryGetClass(NavigateClass, out _);

        if (klass == IntPtr.Zero)
        {
            reason = $"IL2CPP 里找不到类 {NavigateClass}（assembly={AssemblyName}）。";
            return false;
        }

        IntPtr method = IL2CPP.il2cpp_class_get_method_from_name(klass, NavigateMethod, NavigateParamCount);

        if (method == IntPtr.Zero)
        {
            reason = $"{NavigateClass} 里找不到 {NavigateMethod}/{NavigateParamCount} 参数的方法（签名可能变了）。";
            return false;
        }

        entry = Marshal.ReadIntPtr(method);

        if (entry == IntPtr.Zero)
        {
            reason = $"{NavigateClass}.{NavigateMethod} 的 methodPointer 为空（方法可能未被注册/被裁剪）。";
            return false;
        }

        return true;
    }

    /// <summary>
    /// 取一个类并尽量保证它的**字段偏移已算好**。
    ///
    /// <para>
    /// IL2CPP 的字段偏移在 <c>Class::Init</c> 里才填。没有静态构造函数的类，
    /// <c>il2cpp_runtime_class_init</c> 只是把布局算出来，没有副作用；
    /// 而有静态构造函数的类（本例只有 <c>BattleUnit</c>）**不强制 Init** ——
    /// 免得在 mod 初始化期提前触发游戏自己的静态构造。
    /// </para>
    /// </summary>
    private static IntPtr TryGetClass(string className, out bool inited)
    {
        inited = false;

        IntPtr klass = IL2CPP.GetIl2CppClass(AssemblyName, string.Empty, className);

        if (klass == IntPtr.Zero)
        {
            return IntPtr.Zero;
        }

        bool hasStaticCtor =
            IL2CPP.il2cpp_class_get_method_from_name(klass, ".cctor", 0) != IntPtr.Zero;

        if (!hasStaticCtor)
        {
            IL2CPP.il2cpp_runtime_class_init(klass);
            inited = true;
        }

        return klass;
    }

    /// <summary>读实例字段偏移。静态字段、未初始化类都会拿到 0 或超大值 → 视为失败。</summary>
    private static bool TryFieldOffset(IntPtr klass, string fieldName, out int offset)
    {
        offset = 0;

        if (klass == IntPtr.Zero)
        {
            return false;
        }

        IntPtr field = IL2CPP.il2cpp_class_get_field_from_name(klass, fieldName);

        if (field == IntPtr.Zero)
        {
            return false;
        }

        uint raw = IL2CPP.il2cpp_field_get_offset(field);

        if (raw == 0 || raw >= MaxFieldOffset)
        {
            // 0 = 还没算出来（类未初始化）；超大 = 静态字段的存储偏移。
            return false;
        }

        offset = (int)raw;
        return true;
    }

    private string Summary()
    {
        string bounds = BoundsFromPdata ? ".pdata" : "固定窗口（.pdata 不可用）";

        var lines = new List<string>
        {
            $"[定位] {NavigateClass}.{NavigateMethod}：入口 0x{Entry:x}" +
            $"（静态 0x{NativeMemory.RuntimeToStaticVa((IntPtr)(long)Entry):x}），" +
            $"函数体 [{FunctionStart:x},{FunctionEnd:x}) 来源={bounds}，解码 {InstructionCount} 条指令" +
            $"，定位耗时 {ElapsedMs:F2} ms（每进程只做一次）",

            $"[定位] 城防 hook：{Wall.Describe()}",
            SameSkipTarget
                ? "[定位] 两个 hook 的 skip 指向同一个 continue 块（与历代构建一致）。"
                : "[定位] ⚠️ 两个 hook 的 skip 指**不同**地址 —— 不在已知形态内，请人工核对反汇编。",
            $"[定位] 穿友方 hook：{Friendly.Describe()}（get_IsAlive 调用目标 0x{IsAliveCall:x}）",

            $"[定位] 偏移：gridType=+0x{OffGridType:x} battleUnit=+0x{OffBattleUnit:x} " +
            $"obstale=+0x{OffObstale:x} obstalceType=+0x{OffObstacleType:x} obstacleTeam=+0x{OffObstacleTeam:x} " +
            $"battleTeam=+0x{OffBattleTeam:x} teamID=+0x{OffTeamId:x} selfTeamID=[rsp+0x{SelfTeamIdStackOffset:x}]" +
            (FallbackOffsetCount == 0
                ? "（全部来自 IL2CPP 元数据）"
                : $"（其中 {FallbackOffsetCount} 项回退到本构建实测常量 —— 见上面的 WARNING）"),
        };

        return string.Join("\n", lines);
    }
}
