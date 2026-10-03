using System;
using System.Collections.Generic;
using Iced.Intel;
using static Iced.Intel.AssemblerRegisters;

namespace Unnamed42.FriendlyNoclip;

/// <summary>
/// <b>⚠️ 实验性：本类会导致崩溃，默认关闭（<c>wall_highlight_hook=false</c>），
/// 且已被证实并非功能所必需。</b>
///
/// <para><b>2026-10-04 二分结论（实机确认）</b></para>
/// <list type="number">
///   <item>开启本 hook → 打完一场**必崩**（FailFast）；关闭 → 不崩，
///     且**城墙穿越依然正常可用**。</item>
///   <item>崩溃的 dump 显示故障帧紧跟 <c>call GetMoveRangeGrids</c> 之后，
///     与本类装的两个 <c>ff25</c> detour 位置完全对应。</item>
///   <item><b>为什么它不是必需的</b>：<c>WallPassData</c> 写 <c>passes</c> 之后，
///     <c>Navigate</c> 已经能穿墙；而「墙对面格子亮不亮」本来就由**游戏自己的**
///     <c>GetMoveRangeGrids</c> 调 <c>Navigate</c> 来决定 ——
///     那一侧不需要我们去改判定。</item>
/// </list>
///
/// <para>
/// <b>保留本类的原因</b>：它记录了一次完整的排查，且「让城墙格本身也亮」
/// （当前语义是「不可停留」，所以城墙**不该**亮）这个需求将来可能还会出现。
/// 若要用它，需先查清崩溃原因 —— 怀疑与「无条件放行」有关：
/// 障碍格被送进普通格的后续代码路径，而那条路径假定格子不是障碍。
/// </para>
///
/// <para><b>原始设计意图（供参考）</b></para>
/// 「范围高亮」侧的障碍格放行 —— <c>BattleMapData.GetMoveRangeGrids</c> 内的两处
/// <c>gridType == Obstacle → 跳过</c>。
///
/// <para><b>为什么必须有这第二个 hook</b>：游戏里存在<b>两套互相独立</b>的
/// 「障碍格不可通行」判定，分别服务两个不同用途：</para>
/// <list type="table">
///   <item>
///     <term><c>MapNavigator.Navigate</c>（<c>0x180A8D8B2</c>）</term>
///     <description>「点击寻路 / 能不能走过去」。<see cref="WallPassHook"/> 已覆盖。</description>
///   </item>
///   <item>
///     <term><c>GetMoveRangeGrids</c>（<c>0x1808C8CBC</c> 与 <c>0x1808C8FEF</c>）</term>
///     <description>「移动范围高亮 / 格子亮不亮」。<b>本类</b>覆盖。</description>
///   </item>
/// </list>
///
/// <para>
/// ★ 实测症状（2026-10）：只 hook 了 <c>Navigate</c> 时，<c>Navigate</c> 对己方城墙
/// 确实返回 <c>True</c>（可走），但**格子始终不亮** —— 因为高亮走的是另一套判定。
/// </para>
///
/// <para><b>两个 gate 的分工</b>（都由本类放行）：</para>
/// <list type="bullet">
///   <item>
///     <b>Gate A</b> <c>0x1808C8CBC</c> —— 在**扩散循环**里。障碍格跳到
///     <c>0x1808C8E07</c>（直接继续扩散，**不加入范围、也不成为新前沿**）。
///     不放行的话，城墙格永远不会沿墙扩散出去。
///   </item>
///   <item>
///     <b>Gate B</b> <c>0x1808C8FEF</c> —— 在**外层/种子**循环里。障碍格跳到
///     <c>0x1808C9133</c>（推进循环，不加入范围）。
///   </item>
/// </list>
///
/// <para><b>★ 设计要点：让 <c>Navigate</c> 成为唯一的可通行性裁判</b></para>
/// <para>
/// 放行两个 gate 后，障碍格会走**普通格同款**的路径，而那条路径里**本来就会调用**
/// <c>MapNavigator.Navigate</c>（<c>0x1808C8D99</c>）做可达性测试，且只有返回 true
/// 才 <c>grids.Add</c>。而 <c>Navigate</c> 里的障碍判定**已经**被
/// <see cref="WallPassHook"/> 按「己方城墙」精确放行了。
/// </para>
/// <para>
/// 所以判据只有一处（<see cref="WallPassHook"/>），本类<b>不做任何队伍/类型判断</b>，
/// 纯放行即可：
/// </para>
/// <list type="bullet">
///   <item>己方城墙 → <c>Navigate</c> 返回 true → 加入范围 + 沿墙继续扩散 ✅</item>
///   <item>他方城墙 / 中立障碍 → <c>Navigate</c> 返回 false → 不加入范围 ✅</item>
/// </list>
/// <para>
/// 附带的好处：**不会出现「格子亮了却走不过去」或反之**的错位 ——
/// 因为「亮」与「走」现在由同一个 <c>Navigate</c> 回答。
/// </para>
///
/// <para><b>为什么放行是安全的（对空格与障碍格等价）</b>：普通路径的第一道门是
/// <c>Object.op_Equality(cell.battleUnit, null)</c>（<c>0x180D8E790</c>）。
/// 空格与障碍格的 <c>battleUnit</c> 都是 <c>null</c> → <c>op_Equality(null,null)</c>
/// 为 true → 两者走**完全相同**的后续流程。所以把障碍格放进来不会引入
/// 空格不存在的分支。站在障碍格上的单位（若有）会由 <c>IsAlive</c> 那条门自然处理。
/// </para>
/// </summary>
internal sealed class WallHighlightHook : NativeHookBase
{
    /// <summary>Gate A：扩散循环里的障碍判定（<c>cmp [rax+0x14],2 ; je 0x1808C8E07</c>）。</summary>
    private const ulong VaGateA = 0x1808C8CBCUL;

    /// <summary>Gate A 原本跳过到的位置（扩散继续）。</summary>
    private const ulong VaGateASkip = 0x1808C8E07UL;

    /// <summary>Gate B：外层/种子循环里的障碍判定（<c>cmp [rax+0x14],2 ; je 0x1808C9133</c>）。</summary>
    private const ulong VaGateB = 0x1808C8FEFUL;

    /// <summary>Gate B 原本跳过到的位置（推进循环）。</summary>
    private const ulong VaGateBSkip = 0x1808C9133UL;

    /// <summary>两条 <c>je</c> 都紧跟 4 字节的 <c>cmp</c>，所以 jcc 本身固定偏移 +4。</summary>
    private const int JccOffset = 4;


    internal static readonly WallHighlightHook Instance = new();

    private WallHighlightHook()
    {
    }

    protected override string Tag => "[高亮 detour]";

    protected override ulong HookVa => VaGateA;

    /// <summary>gate A 的落点是 <c>je</c> 本身（在 <c>cmp</c> 之后 4 字节）。</summary>
    protected override ulong HookSiteVa => VaGateA + JccOffset;

    /// <summary>
    /// 期望的落点形态：<c>cmp dword [reg+0x14], 2</c>（4 字节）+ <c>je rel32</c>（6 字节）。
    ///
    /// ⚠️ <b>必须是完整 10 字节</b>：<see cref="NativeHookBase.Install"/> 按
    /// <c>OriginalBytes.Length</c> 决定读多少字节给 <see cref="ValidateSite"/>，
    /// 而校验需要看到 <c>cmp</c> 与 <c>je</c> 两段。
    /// 早期版本这里只写 6 字节，导致基准只读 6 字节、校验报「读取长度不足」。
    ///
    /// <para>
    /// <c>je</c> 的 rel32 写 0 是因为它随两个 gate 而不同，不参与形态比对
    /// （只验前两字节 <c>0F 84</c>）。
    /// </para>
    /// </summary>
    private static readonly byte[] OriginalBytes_ = { 0x83, 0x78, 0x14, 0x02, 0x0F, 0x84, 0x00, 0x00, 0x00, 0x00 };

    protected override byte[] OriginalBytes => OriginalBytes_;

    /// <summary>
    /// 两个 gate 都是 <c>cmp dword [reg+0x14], 2</c> 后跟 6 字节 <c>je</c>。
    /// 这里只校验「<c>cmp ... 14 02</c> + <c>0F 84</c>」这个形状，
    /// 不写死寄存器（<c>rax</c>），因为两个 gate 用的是同一个形态但不同位置。
    /// </summary>
    protected override bool ValidateSite(byte[] current, out string reason)
    {
        if (current.Length < JccOffset + 6)
        {
            reason = "读取长度不足。";
            return false;
        }

        // ★ 逐字节比对：cmp dword ptr [reg+0x14], 2  ==  83 <modrm> 14 02
        //
        // ⚠️⚠️ ModRM 的正确解读（本项目在此处错过一次）：
        //   0x78 = 01 111 000
        //          mod=01（disp8！不是 disp32） reg=111(/7 = cmp) rm=000
        //   所以 83 78 14 02 是 **4 字节**：opcode / modrm / disp8=0x14 / imm8=02。
        //
        //   早期版本写成 `(current[1] & 0xC0) != 0x80` —— 那是要求 mod=10（disp32），
        //   而 0x78 & 0xC0 == 0x40，永远不成立 → 校验必然失败，
        //   日志里就表现为「实际 83 78 14 02」但就是不放行。
        //
        // 正确做法：只要求 opcode==0x83 、modrm 的 reg 字段==7（group 1 /7 = cmp）、
        // rm 字段任意（不同 gate 可能用不同寄存器），disp8==0x14、imm8==0x02。
        if (current[0] != 0x83
            || (current[1] & 0x38) != 0x38      // reg 字段（bits 3-5）必须为 111 = cmp
            || (current[1] & 0xC0) != 0x40      // mod=01 -> 后面跟 disp8
            || current[2] != 0x14               // disp8 = gridType 字段偏移
            || current[3] != 0x02)              // imm8 = GridType.Obstacle
        {
            reason = $"不是 `cmp dword [reg+0x14], 2`（实际 {Hex(current.AsSpan(0, 4).ToArray())}）。";
            return false;
        }

        if (current[JccOffset] != 0x0F || current[JccOffset + 1] != 0x84)
        {
            reason = $"紧跟的不是 6 字节 `je rel32`（实际 {current[JccOffset]:x2} {current[JccOffset + 1]:x2}）。";
            return false;
        }

        reason = string.Empty;
        return true;
    }

    /// <summary>
    /// 本类的 stub 覆盖 <b>两个</b> gate，所以 <see cref="NativeHookBase.Install"/> 那条
    /// 「一个 hook 一个落点」的骨架不够用 —— 这里只让基类负责分配/校验/日志，
    /// 第二个 gate 由 <see cref="InstallSecondGate"/> 补上。
    /// </summary>
    internal bool Installed2 { get; private set; }

    /// <summary>
    /// 第二个 gate 的独立安装（与主 gate 同一套校验 + 同一个 stub）。
    ///
    /// <para>
    /// ⚠️ <b>两个地址要分清</b>：
    /// </para>
    /// <list type="bullet">
    ///   <item><b>校验基准</b> = <c>VaGateB</c>（<c>cmp</c> 的起始）——
    ///     因为 <see cref="ValidateSite"/> 期望传入的就是以 <c>cmp</c> 开头的 10 字节。</item>
    ///   <item><b>hook 落点</b> = <c>VaGateB + JccOffset</c>（<c>je</c> 本身）——
    ///     我们替换的是那条 6 字节 <c>je</c>，不是前面的 <c>cmp</c>。</item>
    /// </list>
    /// 早期版本把校验基准与落点混为一谈（都读 <c>site</c>=落点，却按 <c>cmp</c> 开头解析），
    /// 于是报「不是 cmp dword [reg+0x14], 2（实际 83 78 14 02）」——
    /// 那四个字节恰好就是 <c>cmp</c> 本身，而读取起点错位在它之后。
    /// </summary>
    internal bool InstallSecondGate()
    {
        try
        {
            // 落点：je 本身（6 字节 rel32 jcc）。
            IntPtr hookSite = NativeMemory.StaticVaToRuntime(VaGateB + JccOffset);

            // 校验基准：cmp 的起始，共 10 字节。
            IntPtr checkSite = NativeMemory.StaticVaToRuntime(VaGateB);

            if (!NativeMemory.TryReadBytes(checkSite, OriginalBytes.Length, out byte[] current))
            {
                Plugin.Log.Warning($"{Tag} 无法读取 0x{checkSite.ToInt64():x} 的字节，跳过 gate B。");
                return false;
            }

            if (!ValidateSite(current, out string reason))
            {
                Plugin.Log.Warning($"{Tag} gate B（0x{VaGateB:x}）校验失败，拒绝安装：{reason}");
                return false;
            }


            // gate B 用同一个 stub 形态：放行 = 落到 je 的**下一条指令**（即 cmp 之后）。
            //
            // 与主 gate 一样：先分配地址、再构码（可重定位出口需要 rip）。
            IntPtr stub = NativeMemory.AllocateExecutable(AllocatedStubLength);

            if (stub == IntPtr.Zero)
            {
                Plugin.Log.Warning($"{Tag} gate B 分配可执行内存失败。");
                return false;
            }

            byte[] code = BuildPassStub(Tag, stub.ToInt64(), RuntimeVa(VaGateB + JccOffset + 6));

            if (code.Length == 0 || code.Length > AllocatedStubLength)
            {
                Plugin.Log.Warning($"{Tag} gate B stub 构码失败或超长（{code.Length} 字节），放弃。");
                return false;
            }

            System.Runtime.InteropServices.Marshal.Copy(code, 0, stub, code.Length);

            var hook = new MelonLoader.NativeUtils.NativeHook<DetourSignature>(hookSite, stub);
            hook.Attach();

            if (!hook.IsHooked)
            {
                Plugin.Log.Warning($"{Tag} gate B NativeHook.Attach() 后 IsHooked=false，放弃。");
                return false;
            }

            _gateBHook = hook;
            Installed2 = true;

            Plugin.LogInfo(() =>
                $"{Tag} gate B 已挂上 0x{VaGateB + JccOffset:x}（运行时 0x{hookSite.ToInt64():x}）" +
                $"→ stub 0x{stub.ToInt64():x}。落点现状：{NativeMemory.HexDump(hookSite, 10)}");

            return true;
        }
        catch (Exception e)
        {
            Plugin.Log.Warning($"{Tag} gate B 安装异常：{e.Message}");
            return false;
        }
    }

    private MelonLoader.NativeUtils.NativeHook<DetourSignature>? _gateBHook;

    /// <summary>
    /// 本 hook 的 stub：<b>无条件放行</b>。
    ///
    /// <para>
    /// 因为「是不是己方城墙」这个判断**不需要在这里做** —— 放行之后走的普通路径里
    /// 会调用 <c>Navigate</c>，而 <c>Navigate</c> 已经把己方城墙放行、把其他障碍挡住。
    /// 在这里重复判断反而会引入「两处判据不一致」的风险。
    /// </para>
    /// </summary>
    protected override byte[] BuildStub(long stubRip) =>
        BuildPassStub(Tag, stubRip, RuntimeVa(VaGateA + JccOffset + 6));

    /// <summary>
    /// 生成「一律放行」stub：直接跳到 <c>je</c> 的落空点（即 <c>cmp</c> 之后的下一条指令）。
    ///
    /// <para>
    /// 用 <c>r11</c> 而非 <c>rax</c> —— 落空点紧跟 <c>cmp</c>，后续代码要用 <c>cmp</c>
    /// 留下的标志位，且本项目在 <c>rax</c> 中转上真实崩过（见 AGENTS.md §6.1）。
    /// </para>
    /// <para>
    /// <c>mov r11,imm64</c> 与 <c>lea r11,[rip+x]</c> 都<b>不读写标志位</b>，
    /// 所以两种出口形态对这里都安全。
    /// </para>
    /// </summary>
    private static byte[] BuildPassStub(string tag, long stubRip, long fallThrough) =>
        StubAssembler.Build(tag, 16, stubRip, asm => asm.ExitViaImm64(fallThrough));
}
