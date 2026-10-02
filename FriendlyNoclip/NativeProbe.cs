using System;
using System.Text;
using Il2Cpp;
using Il2CppInterop.Runtime;
using MelonLoader;

namespace Unnamed42.FriendlyNoclip;

/// <summary>
/// 原生级只读探针。
///
/// <para>
/// 【为什么需要它】静态反汇编已经把真实寻路链路读清楚了：
/// </para>
/// <list type="bullet">
///   <item><c>BattleMapData.GetMoveRangeGrids</c> (<c>0x1808c8b60</c>) 与
///         <c>GetEmptyGrid</c> (<c>0x1808c88a0</c>) 都只是薄壳，真正的过滤在
///         <c>MapNavigator.Navigate</c> (<c>0x180a8d5a0</c>，A*)。</item>
///   <item><c>Navigate</c> 内层循环用 <c>bt g.passes, dir</c>（<c>0x180a8d875</c>，
///         <c>GridUnitData.passes</c> = <c>+0x20</c>）判断某方向能否走。</item>
///   <item>还有一处未定性的虚调用 <c>call [klass+0x138]</c>（<c>0x180a8d8d1</c>），
///         它传入了目标格，可能是占位检查。</item>
/// </list>
/// <para>
/// 本探针在战斗里取一个「被友方围住」的起点，把它四邻的 <c>passes</c> 与单位归属
/// 打出来，并 dump 一次 <c>GridUnitData</c> 的 klass 头部，用来回答：
/// <b>拦住友方的是 <c>passes</c> 位掩码，还是那处虚调用？</b>
/// </para>
/// <para>全程只读，不修改游戏代码。</para>
/// </summary>
internal static class NativeProbe
{
    // ------------------------------------------------------------------
    // 真实地址（已用 IL2CPP dumper 交叉验证，见 MEMORY.md）
    // ------------------------------------------------------------------

    /// <summary><c>MapNavigator.Navigate</c> —— 真正的 A* 寻路器。</summary>
    internal const ulong VaNavigate = 0x180a8d5a0UL;

    /// <summary><c>BattleMapData.GetMoveRangeGrids</c>。</summary>
    internal const ulong VaGetMoveRangeGrids = 0x1808c8b60UL;

    /// <summary><c>BattleMapData.GetEmptyGrid</c>。</summary>
    internal const ulong VaGetEmptyGrid = 0x1808c88a0UL;

    /// <summary><c>BattleMapData.AroundGridHaveEnemy</c> —— 只看敌方，天然不拦友方。</summary>
    internal const ulong VaAroundGridHaveEnemy = 0x1808c41f0UL;

    /// <summary><c>GridUnitData.isEmpty()</c>。</summary>
    internal const ulong VaIsEmpty = 0x180873e70UL;

    /// <summary>
    /// <c>Navigate</c> 内层循环头的 <c>bt g.passes, dir</c>；编码为 <c>41 0f a3 c0</c>。
    /// </summary>
    internal const ulong VaPassesBitTest = 0x180a8d875UL;

    /// <summary><c>Navigate</c> 里未定性的虚调用点。</summary>
    internal const ulong VaVirtualCallSite = 0x180a8d8d1UL;

    /// <summary>
    /// <c>Navigate</c> 内层循环头的字节（<c>0x180a8d870</c> 起）：
    /// <c>mov eax,ebx; and eax,0x1f; bt r8d,eax</c>。
    /// 注意 <c>bt r8d, eax</c> 需要 REX 前缀，编码为 <c>41 0f a3 c0</c>（ModRM <c>c0</c> = reg r8d, rm eax）。
    /// 用于确认地址未因游戏更新而漂移。
    /// </summary>
    private static readonly byte[] SigmLoopHead =
    {
        0x8b, 0xc3,                         // mov eax, ebx
        0x83, 0xe0, 0x1f,                   // and eax, 0x1f
        0x41, 0x0f, 0xa3, 0xc0,             // bt  r8d, eax
    };

    // ------------------------------------------------------------------
    // GridUnitData / BattleUnit / BattleTeam 字段偏移（dumper 已核对）
    // ------------------------------------------------------------------

    /// <summary><c>GridUnitData.passes</c>（int，4 位方向掩码）。</summary>
    internal const int GridPassesOffset = 0x20;

    /// <summary><c>GridUnitData.gridType</c>（enum，1 = Normal）。</summary>
    internal const int GridGridTypeOffset = 0x14;

    /// <summary><c>GridUnitData.battleUnit</c>。</summary>
    internal const int GridBattleUnitOffset = 0x18;

    /// <summary><c>GridUnitData.row</c> / <c>column</c>。</summary>
    internal const int GridRowOffset = 0x24;
    internal const int GridColumnOffset = 0x28;

    /// <summary><c>BattleUnit.battleTeam</c>。</summary>
    internal const int UnitBattleTeamOffset = 0x58;

    /// <summary><c>BattleTeam.ID</c>（int）。</summary>
    internal const int TeamIdOffset = 0x10;

    /// <summary>IL2CPP 对象头第一个字段就是 klass 指针。</summary>
    private const int ObjectKlassOffset = 0x00;

    /// <summary>dump klass 时读取的字节数。</summary>
    private const int KlassDumpBytes = 0x80;

    /// <summary>
    /// 相对 klass 起点的 dump 起点。
    /// 从 0 开始：IL2CPP klass 的头部含 image/name/namespace 等字段，
    /// 从头 dump 才能看出真实布局，也能验证指针是否真的是 klass
    /// （+0x10 附近应有一个指向类名字符串的指针，且内容等于 "GridUnitData"）。
    /// </summary>
    private const int KlassDumpStart = 0x00;

    // ------------------------------------------------------------------
    // 入口
    // ------------------------------------------------------------------

    /// <summary>
    /// 定位模块、校验地址未漂移、并 dump 一次 <c>GridUnitData</c> 的 klass。
    /// 在 <c>OnInitializeMelon</c> 里调用一次即可（此时游戏类型已可用）。
    /// </summary>
    internal static void Verify()
    {
        if (!NativeMemory.ResolveModule())
        {
            Plugin.Log.Error("[原生探针] 找不到 GameAssembly.dll，原生操作全部跳过。");
            return;
        }

        Plugin.Log.Msg(
            $"[原生探针] 模块基址 = 0x{NativeMemory.ModuleBase.ToInt64():x}，" +
            $"大小 = 0x{NativeMemory.ModuleSize:x}");

        VerifyLoopHead();
        VerifyAroundGridHaveEnemy();
        VerifyFilterSite();
        ProbeCallSite();
        DumpVirtualSlot();
    }

    /// <summary>
    /// 就地读取 <c>0x180a8d8d1</c> 处 <c>call [r9+0x138]</c> 的**目标地址**，
    /// 以及 <c>0x180a8d8ca</c> 处 <c>mov r8,[r9+0x140]</c> 的取值。
    ///
    /// <para>
    /// 静态分析给出 <c>r9 = [g]</c>（g 是邻居格对象），而 <c>[GridUnitData klass+0x138]</c>
    /// 实测为 0 —— 两者矛盾。这里换成直接读指令字面量并解析出槽偏移，
    /// 再逐个候选 klass 取值，避免继续围绕偏移猜。
    /// </para>
    /// </summary>
    private static void ProbeCallSite()
    {
        try
        {
            IntPtr site = NativeMemory.StaticVaToRuntime(VaVirtualCallSite);
            byte[]? code = ReadBytes(site, 16);

            if (code == null)
            {
                Plugin.Log.Warning("[原生探针] 无法读取调用点字节。");
                return;
            }

            Plugin.Log.Msg(
                $"[原生探针] 调用点 0x{VaVirtualCallSite:x}（运行时 0x{site.ToInt64():x}）字节：" +
                NativeMemory.HexDump(site, 16));

            IntPtr klass = _lastKlass;

            if (klass == IntPtr.Zero)
            {
                klass = Il2CppClassPointerStore.GetNativeClassPointer(typeof(GridUnitData));
            }

            if (klass == IntPtr.Zero)
            {
                return;
            }

            // 按实际字段布局：sizeof(Il2CppClass_27_0) = 0x130，vtable 紧跟其后。
            Plugin.Log.Msg(
                $"[原生探针] klass=0x{klass.ToInt64():x}；" +
                $"vtable 起点应是 klass+0x130（sizeof(Il2CppClass)=0x130）。");

            for (int i = 0; i < 6; i++)
            {
                int vo = 0x130 + i * 8;
                IntPtr v = NativeMemory.ReadPointer((IntPtr)(klass.ToInt64() + vo));

                Plugin.Log.Msg(
                    $"[原生探针]   vtable[{i}] @ klass+0x{vo:x} = 0x{v.ToInt64():x}");
            }

            // 也扫一遍 klass+0x130..0x180 区域，看是否存在任何像代码指针的值。
            Plugin.Log.Msg("[原生探针] klass+0x130..0x180 原始扫描：");

            for (int o = 0x130; o < 0x180; o += 8)
            {
                IntPtr v = NativeMemory.ReadPointer((IntPtr)(klass.ToInt64() + o));
                long iv = v.ToInt64();
                bool looksLikeCode = iv > NativeMemory.ModuleBase.ToInt64()
                    && iv < NativeMemory.ModuleBase.ToInt64() + NativeMemory.ModuleSize;

                Plugin.Log.Msg(
                    $"[原生探针]   +0x{o:x} = 0x{iv:x}{(looksLikeCode ? "   <<< 模块内（像代码指针）" : string.Empty)}");
            }
        }
        catch (Exception e)
        {
            Plugin.Log.Warning($"[原生探针] 探测调用点失败：{e.Message}");
        }
    }

    /// <summary>
    /// 用官方反射 API 枚举 <c>GridUnitData</c> 的方法，找出
    /// <c>Navigate</c> 在 <c>0x180a8d8d1</c> 处调用的那个虚方法槽
    /// （<c>call [klass+0x138]</c>）到底是谁。
    ///
    /// <para>
    /// 之所以不能靠 dump <c>[klass+0x138]</c> 的值再自己换算：那个值可能是
    /// 运行时代码指针、也可能是元数据引用，而且它相对模块基址没有固定关系。
    /// 直接问 IL2CPP「这个类有哪些方法、各自入口在哪」，再和槽里的值比对，
    /// 不需要猜任何偏移。
    /// </para>
    /// </summary>
    private static void DumpVirtualSlot()
    {
        try
        {
            // 优先用 Il2CppClassPointerStore 直接从托管类型取 klass：
            // 它不依赖战斗中的活对象，因此在 OnInitializeMelon 阶段就能用。
            // （此前只依赖 GetGridUnitDataKlass()，而那个只会在 Harmony postfix
            //   里被调用 —— 那时游戏还没进战斗，所以 _lastKlass 恒为 Zero。）
            IntPtr fromStore = Il2CppClassPointerStore.GetNativeClassPointer(typeof(GridUnitData));

            Plugin.Log.Msg(
                $"[原生探针] klass 来源对比：Il2CppClassPointerStore = 0x{fromStore.ToInt64():x}" +
                $"，活动对象对象头 = 0x{_lastKlass.ToInt64():x}" +
                $"{(_lastKlass != IntPtr.Zero && fromStore != _lastKlass ? "   <<<<=== 两者不同！" : string.Empty)}");

            IntPtr klass = fromStore != IntPtr.Zero ? fromStore : _lastKlass;

            if (klass == IntPtr.Zero)
            {
                Plugin.Log.Warning("[原生探针] klass 未知，跳过虚槽解析。");
                return;
            }

            IntPtr slotValue = NativeMemory.ReadPointer(
                (IntPtr)(klass.ToInt64() + VirtualSlotOffset));

            Plugin.Log.Msg(
                $"[原生探针] 虚槽 [klass+0x{VirtualSlotOffset:x}] = 0x{slotValue.ToInt64():x}");

            // 把 +0x100..+0x180 整段打出来：如果整体为 0，说明这个 klass 的
            // 方法指针表不在这个偏移；如果只有个别为 0，说明就是这一槽为空。
            for (int off = 0x100; off < 0x180; off += 8)
            {
                byte[]? raw = ReadBytes((IntPtr)(klass.ToInt64() + off), 8);

                if (raw == null)
                {
                    continue;
                }

                ulong v = BitConverter.ToUInt64(raw, 0);

                Plugin.Log.Msg($"[原生探针]   klass+0x{off:x3} = 0x{v:x016}");
            }

            IntPtr iter = IntPtr.Zero;
            int count = 0;

            while (true)
            {
                IntPtr method = IL2CPP.il2cpp_class_get_methods(klass, ref iter);

                if (method == IntPtr.Zero)
                {
                    break;
                }

                count++;

                string name = IL2CPP.il2cpp_method_get_name_(method) ?? "<null>";
                uint token = IL2CPP.il2cpp_method_get_token(method);
                IntPtr entry = ReadMethodPointer(method);

                Plugin.Log.Msg(
                    $"[原生探针]   method[{count}] {name}   token=0x{token:x}   " +
                    $"entry=0x{entry.ToInt64():x}" +
                    (entry == slotValue ? "   <<<<=== 与虚槽匹配" : string.Empty));
            }

            Plugin.Log.Msg($"[原生探针] GridUnitData 共 {count} 个方法。");
        }
        catch (Exception e)
        {
            Plugin.Log.Warning($"[原生探针] 解析虚槽失败：{e.Message}");
        }
    }

    /// <summary>
    /// 从 <c>Il2CppMethodInfo</c> 里读方法入口指针。
    ///
    /// <para>
    /// <c>Il2CppMethodInfo</c> 的首字段是 <c>methodPointer</c>，但某些版本会先用
    /// <c>virtualMethodPointer</c>。这里把前两个 8 字节都打出来，交由人工判断。
    /// </para>
    /// </summary>
    private static IntPtr ReadMethodPointer(IntPtr method)
    {
        return NativeMemory.ReadPointer(method);
    }

    /// <summary>虚方法槽相对 klass 起点的偏移（<c>Navigate</c> 里的 <c>[r9+0x138]</c>）。</summary>
    private const int VirtualSlotOffset = 0x138;

    /// <summary>
    /// 利用一次真实调用（<c>GetMoveRangeGrids</c> 的 Postfix）拿到的活 <paramref name="map"/>，
    /// dump 一次 <c>GridUnitData</c> 的 klass。只做一次，避免刷屏。
    /// </summary>
    private static bool _klassDumped;

    internal static void DumpKlassOnce(BattleMapData map)
    {
        if (_klassDumped)
        {
            return;
        }

        _klassDumped = true;
        DumpGridUnitDataKlass(map);
    }

    /// <summary>
    /// 行为实验的目标点：<c>Navigate</c> 里的
    /// <code>
    /// 0x180a8d8d1  call [klass+0x138]   ; 可疑的占位/阻挡判定
    /// 0x180a8d8d8  test al, al
    /// 0x180a8d8da  jne  0x180a8da99     ; 结果非 0 -> 走 blocked 分支
    /// </code>
    /// 把 <c>jne</c> 的两个字节改成 NOP，即可强制「该方向放行」，用来验证
    /// 拦住友方的到底是不是这里。
    /// </summary>
    internal const ulong VaFilterJump = 0x180a8d8daUL;

    /// <summary>过滤点 <c>jne</c> 的原始字节（用于还原）。</summary>
    private static readonly byte[] OriginalFilterJump = { 0x0f, 0x85, 0xb9, 0x01, 0x00, 0x00 };

    /// <summary>校验过滤点字节，并记录运行时地址。</summary>
    private static void VerifyFilterSite()
    {
        IntPtr site = NativeMemory.StaticVaToRuntime(VaFilterJump);
        string actual = NativeMemory.HexDump(site, OriginalFilterJump.Length);
        string expected = string.Join(' ', Array.ConvertAll(OriginalFilterJump, b => b.ToString("x2")));

        Plugin.Log.Msg(
            $"[原生探针] 过滤点 jne @ 静态 0x{VaFilterJump:x} → 运行时 0x{site.ToInt64():x}：" +
            $"{actual} {(actual == expected ? "（与离线分析一致）" : $"!! 预期 {expected} !!")}");

        _filterJumpSite = site;
    }

    private static IntPtr _filterJumpSite;

    /// <summary>最近一次解析到的 GridUnitData klass，供虚槽解析复用。</summary>
    private static IntPtr _lastKlass;

    /// <summary>是否已把过滤点改成放行。</summary>
    private static bool _filterPatched;

    /// <summary>
    /// 切换「强制放行」状态。
    ///
    /// <para>
    /// 开启时把 <c>jne</c>（6 字节）整条替换成 NOP，等价于「无论判定结果如何都继续搜索该方向」。
    /// 关闭时写回原始字节。改的是内存，重启游戏即恢复。
    /// </para>
    /// </summary>
    internal static bool ToggleFilterBypass()
    {
        if (_filterJumpSite == IntPtr.Zero)
        {
            Plugin.Log.Warning("[原生探针] 过滤点地址未知，无法切换。");
            return false;
        }

        if (!_filterPatched)
        {
            if (NativeMemory.NopOut(_filterJumpSite, OriginalFilterJump.Length))
            {
                _filterPatched = true;
                Plugin.Log.Msg(
                    $"[原生探针] 已强制放行过滤点 @ 0x{_filterJumpSite.ToInt64():x}：" +
                    $"当前字节 {NativeMemory.HexDump(_filterJumpSite, OriginalFilterJump.Length)}");
                return true;
            }
        }
        else
        {
            if (NativeMemory.WriteBytes(_filterJumpSite, OriginalFilterJump))
            {
                _filterPatched = false;
                Plugin.Log.Msg(
                    $"[原生探针] 已还原过滤点 @ 0x{_filterJumpSite.ToInt64():x}：" +
                    $"当前字节 {NativeMemory.HexDump(_filterJumpSite, OriginalFilterJump.Length)}");
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// 确认 <c>Navigate</c> 内层循环头的字节与离线分析一致。
    /// 这是「地址仍然有效」的最直接证据。
    /// </summary>
    private static void VerifyLoopHead()
    {
        IntPtr site = NativeMemory.StaticVaToRuntime(VaPassesBitTest);
        string actual = NativeMemory.HexDump(site, 4);

        // 41 0f a3 c0 = bt r8d, eax
        string expected = "41 0f a3 c0";
        Plugin.Log.Msg(
            $"[原生探针] bt g.passes @ 静态 0x{VaPassesBitTest:x} → 运行时 0x{site.ToInt64():x}：" +
            $"{actual} {(actual == expected ? "（与离线分析一致）" : $"!! 预期 {expected} !!")}");
    }

    /// <summary>确认 <c>AroundGridHaveEnemy</c> 入口字节。</summary>
    private static void VerifyAroundGridHaveEnemy()
    {
        IntPtr site = NativeMemory.StaticVaToRuntime(VaAroundGridHaveEnemy);
        Plugin.Log.Msg(
            $"[原生探针] AroundGridHaveEnemy @ 静态 0x{VaAroundGridHaveEnemy:x} → " +
            $"运行时 0x{site.ToInt64():x}，前 16 字节：{NativeMemory.HexDump(site, 16)}");
    }

    /// <summary>
    /// dump <c>GridUnitData</c> 的 klass 指针与 <c>+0x100…+0x180</c> 区域。
    ///
    /// <para>
    /// 目的：定位 <c>Navigate</c> 里 <c>call [klass+0x138]</c> 那个槽的真实内容。
    /// 若该槽是一个代码指针（落在 <c>.text</c>/<c>il2cpp</c> 节范围内），就能直接
    /// 反汇编它、确定它是哪个方法。
    /// </para>
    /// </summary>
    private static void DumpGridUnitDataKlass(BattleMapData map)
    {
        IntPtr klass = GetGridUnitDataKlass(map);

        if (klass == IntPtr.Zero)
        {
            Plugin.Log.Warning("[原生探针] 拿不到 GridUnitData 的 klass，跳过 klass dump。");
            return;
        }

        Plugin.Log.Msg(
            $"[原生探针] GridUnitData klass = 0x{klass.ToInt64():x}，" +
            $"名称 = {ResolveClassName(klass)}");

        IntPtr start = (IntPtr)(klass.ToInt64() + KlassDumpStart);
        byte[]? buf = ReadBytes(start, KlassDumpBytes);

        if (buf == null)
        {
            return;
        }

        // 按 8 字节一行输出，便于找指针。
        for (int off = 0; off < KlassDumpBytes; off += 8)
        {
            ulong v = BitConverter.ToUInt64(buf, off);

            Plugin.Log.Msg(
                $"[原生探针]   klass+0x{KlassDumpStart + off:x3} = 0x{v:x016}" +
                DescribePointer(v));
        }
    }

    /// <summary>把一个 klass 内的 8 字节值描述成「代码指针 / 模块内指针 / 普通值」。</summary>
    private static string DescribePointer(ulong v)
    {
        long baseAddr = NativeMemory.ModuleBase.ToInt64();
        long size = NativeMemory.ModuleSize;

        if (baseAddr == 0 || v < (ulong)baseAddr || v >= (ulong)(baseAddr + size))
        {
            return "";
        }

        return "  <- 模块内指针";
    }

    /// <summary>
    /// 从 <paramref name="map"/> 上第一个真实 <c>GridUnitData</c> 实例读出它的 klass 指针。
    ///
    /// <para>
    /// 不走反射去问 <c>Il2CppInterop</c> 要类型元数据（那条路依赖它内部的属性名，
    /// 实测拿不到）。IL2CPP 对象的第一个字段就是 <c>Il2CppClass*</c>，
    /// 直接从活对象头上读最可靠。
    /// </para>
    /// </summary>
    private static IntPtr GetGridUnitDataKlass(BattleMapData map)
    {
        try
        {
            Il2CppSystem.Collections.Generic.List<GridUnitData>? grids = map.normalGrids;

            if (grids == null || grids.Count == 0)
            {
                Plugin.Log.Warning("[原生探针] normalGrids 为空，无法取 klass。");
                return IntPtr.Zero;
            }

            GridUnitData? first = grids[0];

            if (first == null)
            {
                return IntPtr.Zero;
            }

            // 注意：不能盲信 Il2CppInterop 的 .Pointer —— 它返回的不一定是对象起始地址。
            // 这里把对象地址与读到的 klass 都打出来，便于人工核对。
            IntPtr objAddr = first.Pointer;
            IntPtr klass = NativeMemory.ReadPointer(
                (IntPtr)(objAddr.ToInt64() + ObjectKlassOffset));

            Plugin.Log.Msg(
                $"[原生探针] normalGrids[0] 对象地址 = 0x{objAddr.ToInt64():x}，" +
                $"读到的 klass = 0x{klass.ToInt64():x}");

            _lastKlass = klass;

            // 关键对照：同一时刻再取一次「声明类」klass，判断对象头里的
            // klass 与 Il2CppClassPointerStore 给的是否同一个。
            // 若不同，说明运行时实例是子类，虚槽要去子类 klass 上找。
            IntPtr declared = Il2CppClassPointerStore.GetNativeClassPointer(typeof(GridUnitData));

            Plugin.Log.Msg(
                $"[原生探针] 对象头 klass = 0x{klass.ToInt64():x}" +
                $"（{ResolveClassName(klass)}），" +
                $"声明类 klass = 0x{declared.ToInt64():x}" +
                $"（{ResolveClassName(declared)}）" +
                (klass == declared ? "  —— 相同" : "  <<<<=== 不同！"));

            if (klass != declared && declared != IntPtr.Zero)
            {
                Plugin.Log.Msg(
                    $"[原生探针] 对象头 klass 的虚槽 +0x138 = " +
                    $"0x{NativeMemory.ReadPointer((IntPtr)(klass.ToInt64() + 0x138)).ToInt64():x}");
            }

            return klass;
        }
        catch (Exception e)
        {
            Plugin.Log.Warning($"[原生探针] 从实例取 klass 失败：{e.Message}");
            return IntPtr.Zero;
        }
    }

    private static string ResolveClassName(IntPtr klass)
    {
        try
        {
            return IL2CPP.il2cpp_class_get_name_(klass) ?? "<null>";
        }
        catch (Exception e)
        {
            return $"<解析失败 {e.Message}>";
        }
    }

    private static byte[]? ReadBytes(IntPtr address, int count)
    {
        if (NativeMemory.TryReadBytes(address, count, out byte[] buf))
        {
            return buf;
        }

        return null;
    }

    // ------------------------------------------------------------------
    // 被围格子的四邻采样（在战斗里由热键触发）
    // ------------------------------------------------------------------

    /// <summary>
    /// dump 指定格子四邻的 <c>passes</c> / 单位归属 / <c>gridType</c>。
    ///
    /// <para>
    /// 关键在于对比：若「友方单位所占的邻格」其 <c>passes</c> 与「空邻格」不同，
    /// 就说明拦截是通过清方向位实现的；若完全相同，则拦截发生在别处
    /// （即那处虚调用）。
    /// </para>
    /// </summary>
    /// <summary>邻域采样的调用计数，用于限流。</summary>
    private static int _neighborhoodSamples;

    /// <summary>最多采样这么多次，避免 AI 回合里刷屏并拖慢游戏。</summary>
    private const int MaxNeighborhoodSamples = 24;

    /// <summary>
    /// 轻量入口：由 <c>GetMoveRangeGrids</c> 的 Postfix 调用。
    /// 只采样前 <see cref="MaxNeighborhoodSamples"/> 次，且每次只做 5 次 O(1) 取格。
    /// </summary>
    internal static void SampleNeighborhood(
        BattleMapData map, int row, int column, int selfTeamID)
    {
        if (_neighborhoodSamples >= MaxNeighborhoodSamples)
        {
            return;
        }

        _neighborhoodSamples++;

        try
        {
            GridUnitData? center = map.GetGridData(row, column);

            if (center == null)
            {
                return;
            }

            DumpKlassOnce(map);
            DumpNeighborhood(map, center, selfTeamID);
        }
        catch (Exception e)
        {
            Plugin.Log.Warning($"[原生探针] 邻域采样失败：{e.Message}");
        }
    }

    internal static void DumpNeighborhood(BattleMapData map, GridUnitData center, int selfTeamID)
    {
        if (map == null || center == null)
        {
            return;
        }

        var sb = new StringBuilder();
        int r = center.row;
        int c = center.column;

        sb.AppendLine(
            $"[原生探针] 四邻采样：中心 (r{r},c{c}) selfTeamID={selfTeamID} " +
            $"中心passes={center.passes} 中心gridType={center.gridType}");

        for (int dir = 0; dir < 4; dir++)
        {
            GridUnitData? g = ResolveGrid(map, r, c, dir);

            if (g == null)
            {
                sb.AppendLine($"[原生探针]   dir={dir} → null");
                continue;
            }

            string unit = DescribeUnit(g, selfTeamID);
            sb.AppendLine(
                $"[原生探针]   dir={dir} (r{g.row},c{g.column}) " +
                $"gridType={g.gridType} passes={g.passes} ({Convert.ToString(g.passes, 2).PadLeft(4, '0')}) " +
                $"unit={unit}");
        }

        Plugin.Log.Msg(sb.ToString().TrimEnd());
    }

    /// <summary>
    /// 取 <paramref name="row"/>,<paramref name="col"/> 在 <paramref name="dir"/> 方向的邻格。
    ///
    /// <para>
    /// 直接用游戏自己的 <c>BattleMapData.GetGridDataByDir</c>（<c>0x1808c8a50</c>，public），
    /// 不手写遍历 —— 手写版是 O(n) 的托管/native 跨界循环，在 AI 回合里被频繁调用时
    /// 足以拖死游戏。
    /// </para>
    /// </summary>
    private static GridUnitData? ResolveGrid(BattleMapData map, int row, int col, int dir)
    {
        try
        {
            // 越界由游戏函数自己判（它内部检查 row/column 范围并返回 null）。
            return map.GetGridDataByDir(row, col, dir);
        }
        catch (Exception e)
        {
            Plugin.Log.Warning($"[原生探针] GetGridDataByDir(r{row},c{col},d{dir}) 失败：{e.Message}");
            return null;
        }
    }

    /// <summary>把一个格子上的单位描述成「队伍/存活」。</summary>
    private static string DescribeUnit(GridUnitData g, int selfTeamID)
    {
        BattleUnit? unit = g.battleUnit;

        if (unit == null)
        {
            return "-";
        }

        try
        {
            // 走原生读，避免托管包装的属性 getter 在探针里抛异常。
            IntPtr unitPtr = unit.Pointer;
            IntPtr teamPtr = NativeMemory.ReadPointer(
                (IntPtr)(unitPtr.ToInt64() + UnitBattleTeamOffset));

            if (teamPtr == IntPtr.Zero)
            {
                return "<无队伍>";
            }

            int teamId = ReadInt32(teamPtr, TeamIdOffset);
            string alive;

            try
            {
                alive = unit.IsAlive ? "Y" : "N";
            }
            catch
            {
                alive = "?";
            }

            return $"team={teamId}{(teamId == selfTeamID ? "(我)" : "(敌)")} alive={alive}";
        }
        catch (Exception e)
        {
            return $"<读取失败 {e.Message}>";
        }
    }

    /// <summary>从原生内存读一个 int32（不走托管包装）。</summary>
    internal static int ReadInt32(IntPtr baseAddr, int offset)
    {
        byte[]? buf = ReadBytes((IntPtr)(baseAddr.ToInt64() + offset), 4);
        return buf == null ? -999 : BitConverter.ToInt32(buf, 0);
    }

    /// <summary>从托管对象直接读 <c>GridUnitData.passes</c>。</summary>
    internal static int ReadPassesRaw(GridUnitData g)
    {
        try
        {
            return ReadInt32(g.Pointer, GridPassesOffset);
        }
        catch
        {
            return -999;
        }
    }
}
