using System;
using System.Collections.Generic;

namespace Unnamed42.FriendlyNoclip;

/// <summary>
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

    /// <summary>6 字节 rel32 jcc。</summary>
    private static readonly byte[] OriginalBytes_ = { 0x0F, 0x84, 0x00, 0x00, 0x00, 0x00 };

    internal static readonly WallHighlightHook Instance = new();

    private WallHighlightHook()
    {
    }

    protected override string Tag => "[高亮 detour]";

    protected override ulong HookVa => VaGateA;

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

        // cmp dword ptr [rXX + 0x14], 2   —— ModRM 高两位是 10（disp32），
        // 低三位任意寄存器；末字节为 2。
        if (current[0] != 0x83 || (current[1] & 0xC0) != 0x80 || current[2] != 0x14 || current[3] != 0x02)
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

    /// <summary>第二个 gate 的独立安装（与主 gate 同一套校验 + 同一个 stub）。</summary>
    internal bool InstallSecondGate()
    {
        try
        {
            IntPtr site = NativeMemory.StaticVaToRuntime(VaGateB);

            if (!NativeMemory.TryReadBytes(site, OriginalBytes.Length + JccOffset, out byte[] current))
            {
                Plugin.Log.Warning($"{Tag} 无法读取 0x{site.ToInt64():x} 的字节，跳过 gate B。");
                return false;
            }

            if (!ValidateSite(current, out string reason))
            {
                Plugin.Log.Warning($"{Tag} gate B（0x{VaGateB:x}）校验失败，拒绝安装：{reason}");
                return false;
            }

            // gate B 用同一个 stub 形态：放行 = 落到 je 的**下一条指令**（即 cmp 之后）。
            long fallThrough = (long)(VaGateB + JccOffset + 6);
            byte[] code = BuildPassStub(RuntimeVa(VaGateBSkip), fallThrough);

            IntPtr stub = NativeMemory.AllocateExecutable(code.Length);

            if (stub == IntPtr.Zero)
            {
                Plugin.Log.Warning($"{Tag} gate B 分配可执行内存失败。");
                return false;
            }

            System.Runtime.InteropServices.Marshal.Copy(code, 0, stub, code.Length);

            var hook = new MelonLoader.NativeUtils.NativeHook<DetourSignature>(site, stub);
            hook.Attach();

            if (!hook.IsHooked)
            {
                Plugin.Log.Warning($"{Tag} gate B NativeHook.Attach() 后 IsHooked=false，放弃。");
                return false;
            }

            _gateBHook = hook;
            Installed2 = true;

            Plugin.Log.Msg(
                $"{Tag} gate B 已挂上 0x{VaGateB:x}（运行时 0x{site.ToInt64():x}）→ stub 0x{stub.ToInt64():x}。" +
                $"落点现状：{NativeMemory.HexDump(site, 10)}");

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
    protected override byte[] BuildStub() =>
        BuildPassStub(RuntimeVa(VaGateASkip), RuntimeVa(VaGateA + JccOffset + 6));

    /// <summary>
    /// 生成「一律放行」stub：直接跳到 <c>je</c> 的落空点（即 cmp 之后的下一条指令）。
    ///
    /// <para>
    /// <paramref name="unusedSkip"/> 保留仅为日志对照，证明我们**没有**选它。
    /// </para>
    /// </summary>
    private static byte[] BuildPassStub(long unusedSkip, long fallThrough)
    {
        var stub = new List<byte>(16);

        // mov r11, imm64 ; jmp r11
        // 用 r11 而非 rax —— 落空点紧跟 cmp，后续代码要用 cmp 留下的标志位，
        // 且本项目在 rax 中转上真实崩过（见 AGENTS.md）。
        stub.AddRange(new byte[] { 0x49, 0xBB });
        stub.AddRange(BitConverter.GetBytes(fallThrough));
        stub.AddRange(new byte[] { 0x41, 0xFF, 0xE3 });

        return stub.ToArray();
    }
}
