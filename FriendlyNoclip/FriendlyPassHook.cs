using System;
using System.Collections.Generic;

namespace Unnamed42.FriendlyNoclip;

/// <summary>
/// 穿友方单位：在 <c>MapNavigator.Navigate</c> 内的 <c>0x180a8d929</c> 装 detour，
/// 把「任何存活单位都阻挡」改成「只阻挡**敌方**单位」。
///
/// <para><b>为什么需要原生层改动</b>：<c>Navigate</c> 里 <c>0x180a8d929</c> 的
/// <c>jne</c> 不分敌我 —— 邻格上只要站着存活单位就跳过。所以「只穿友方」
/// 必须自己比较队伍 ID，不能把这个判断交给游戏原有的任何一处。</para>
///
/// <para><b>★★ 出口必须是 <c>0x180a8d92f</c>，而且必须重跑 <c>AroundGridHaveEnemy</c></b>：
/// 本 hook 只负责回答「**这个格子上的人是不是队友**」，
/// 而**不能**代替游戏回答「**站到那个格子上合不合法**」。
/// <c>0x180a8d92f</c> 正是原版 <c>jne</c> **不跳**时的落点 —— 即游戏自己的
/// 「空格链」，它先调 <c>AroundGridHaveEnemy(row, col, selfTeamID)</c>
/// （<c>0x1808c41f0</c>，交战区 / 敌方邻接检查）再决定是否接受。
/// </para>
///
/// <para>
/// ⚠️⚠️ 2026-10 修正：这里**曾经**跳到 <c>0x180a8d963</c>（当时叫 <c>expand</c>），
/// 那是错的 —— 该地址在 <c>AroundGridHaveEnemy</c> **已经返回通过之后**，
/// 跳过去等于**跳过占位 / 交战区判定**。真机后果：
/// **AI 会把玩家所在的格子当成可落点，直接站到玩家头上**。
/// 这跟 <see cref="WallPassHook"/> 那个 <c>expand</c> bug 是**同一类错误**。
/// </para>
///
/// <para>
/// 旧注释曾声称「把友方格交给 <c>AroundGridHaveEnemy</c> 判定，在混战中必然失败，
/// 已由实机验证」—— 那个结论是在**错误的出口地址**下得到的，已被推翻：
/// 逃掉交战区检查会把原本不可落的格子放行，所以当时看到的「失败」混杂了两个原因。
/// </para>
///
/// <para><b>为什么用 stub 而不是托管回调</b>：<c>0x180a8d929</c> 是函数中间地址，
/// 此刻有效状态在 <c>rsi</c>（邻格 <c>g</c>）与 <c>[rsp+0xd8]</c>（<c>selfTeamID</c>），
/// 而托管 delegate 的参数只映射 Win64 参数寄存器 <c>rcx/rdx/r8/r9</c>，
/// 拿不到这两者。所以这里放一段手写 x64 stub，直接读原寄存器与栈。</para>
///
/// <para><b>为什么不用自己分配跳板</b>：此前的版本自己算 rel32 + <c>VirtualAlloc</c>
/// 从映像末尾向上找洞，一装上就 coredump。改用 Dobby 后，
/// 指令搬迁与跳板分配都由它负责（near allocator 保证 rel32 可达）。</para>
/// </summary>
internal sealed class FriendlyPassHook : NativeHookBase
{
    /// <summary>
    /// hook 点：<c>Navigate</c> 内 <c>0x180a8d929</c> 的 <c>jne 0x180a8da6b</c>。
    /// 该地址是干净的单条指令起点，且镜像内零个分支跳入（已全量扫描确认）。
    /// </summary>
    private const ulong VaHookSite = 0x180A8D929UL;

    /// <summary>原 <c>jne</c> 的目标：方向递增后继续循环（跳过该邻格）。</summary>
    private const ulong VaSkip = 0x180A8DA6BUL;

    /// <summary>
    /// ★ 放行出口：原 <c>jne</c> **不跳**时的落点，游戏自己的「空格链」——
    /// 它会先调 <c>AroundGridHaveEnemy</c>（<c>0x1808c41f0</c>）再决定是否接受。
    /// <para>
    /// 「队友所在格」与「空格」走**同一条**判定链，这样本 hook 只负责改
    /// 「谁算阻挡」，不负责改「落点合不合法」。
    /// </para>
    /// </summary>
    private const ulong VaPass = 0x180A8D92FUL;

    private static readonly byte[] OriginalBytes_ = { 0x0F, 0x85, 0x3C, 0x01, 0x00, 0x00 };

    /// <summary>单例：本 hook 全进程只会挂一次。</summary>
    internal static readonly FriendlyPassHook Instance = new();

    private FriendlyPassHook()
    {
    }

    protected override string Tag => "[原生 detour]";

    protected override ulong HookVa => VaHookSite;

    protected override byte[] OriginalBytes => OriginalBytes_;

    protected override bool ValidateSite(byte[] current, out string reason)
    {
        // 只要求它仍是一条 6 字节 rel32 jcc（`jne`，或已被改过位移的 `jne`）。
        // 曾经在同一地址做过原地改写探针，那样原值就变了 —— 但搬迁依然安全。
        if (!IsRel32Jcc(current))
        {
            reason = "落点不是 6 字节 jcc rel32。";
            return false;
        }

        reason = string.Empty;
        return true;
    }

    /// <summary>
    /// 构造 detour stub。
    ///
    /// <para>入口状态（已逐条反汇编确认）：</para>
    /// <list type="bullet">
    ///   <item><c>al</c> = <c>get_IsAlive(g.battleUnit)</c> 的返回值</item>
    ///   <item><c>rsi</c> = 邻格 <c>g</c>（<c>GridUnitData</c>）</item>
    ///   <item><c>[rsp+0xd8]</c> = <c>selfTeamID</c>（相对 <c>Navigate</c> 的栈帧）</item>
    /// </list>
    /// <para>
    /// 关键：Dobby 是**跳**到 stub（不是 <c>call</c>），所以 <c>rsp</c> 不变，
    /// <c>[rsp+0xd8]</c> 仍是 <c>selfTeamID</c>。
    /// </para>
    ///
    /// <para>
    /// <b>★★ 只有两个出口，而且「队友格」与「空格」合流：</b>
    /// 本 hook 的职责边界是「把阻挡判据从『有存活单位』改成『有敌方单位』」，
    /// **不碰**「落点合不合法」。所以只要不是敌方，就一律送回
    /// <c>0x180a8d92f</c>（空格链，会跑 <c>AroundGridHaveEnemy</c>），
    /// 而不是直接接受。
    /// </para>
    ///
    /// <para>
    /// 两个出口都用 <c>mov r11,imm64; jmp r11</c> 的绝对跳转，
    /// 不依赖 stub 与目标之间的 rel32 距离。
    /// （不用 <c>rax</c>：<c>0x180a8d92f</c> 之后的原代码可能依赖上一条指令在
    /// <c>rax</c> 里留下的值 —— 本项目在 <see cref="WallPassHook"/> 上踩过这个坑。）
    /// </para>
    /// </summary>
    /// </summary>
    protected override byte[] BuildStub()
    {
        long skip = RuntimeVa(VaSkip);
        long pass = RuntimeVa(VaPass);

        var stub = new List<byte>(220);

        // ★ 探针：必须在任何条件判断之前 —— 这样无论走哪条分支都能记到。
        //   只读栈 + 写环形缓冲，不改语义。hookId=1 表示穿友方。
        NativeProbeLog.EmitRecordEntry(stub, hookId: 1);

        // +0  test al, al
        stub.AddRange(new byte[] { 0x84, 0xC0 });

        // +2  je <pass>      （al==0：无存活单位 -> 空格链，保持原行为）
        //
        // ⚠️⚠️ 必须用 AddRel8：它返回的是 **rel8 操作数**的偏移。
        //   早期版本写成 `int x = stub.Count; stub.Add(0x74); stub.Add(0);`，
        //   于是 x 指向 **opcode**，PatchRel8 把位移写到了 0x74 上，
        //   把 `74 16` 变成 `<disp> 16` —— 非法指令，真机直接 SIGILL。
        //   这是本项目代价最大的手写汇编错误（崩溃在 战斗刚开始 时）。
        AddRel8(stub, 0x74, out int jeAlZero);

        // +4  mov rax, [rsi+0x18]   ; g.battleUnit
        stub.AddRange(new byte[] { 0x48, 0x8B, 0x46, 0x18 });

        // +8  test rax, rax
        stub.AddRange(new byte[] { 0x48, 0x85, 0xC0 });

        // +11 je <skip>
        AddRel8(stub, 0x74, out int jeUnitNull);

        // +13 mov rax, [rax+0x58]   ; battleUnit.battleTeam
        stub.AddRange(new byte[] { 0x48, 0x8B, 0x40, 0x58 });

        // +17 test rax, rax
        stub.AddRange(new byte[] { 0x48, 0x85, 0xC0 });

        // +20 je <skip>
        AddRel8(stub, 0x74, out int jeTeamNull);

        // +22 mov eax, [rax+0x10]   ; battleTeam.ID
        stub.AddRange(new byte[] { 0x8B, 0x40, 0x10 });

        // +25 cmp eax, [rsp+0xd8]   ; selfTeamID
        stub.AddRange(new byte[] { 0x3B, 0x84, 0x24, 0xD8, 0x00, 0x00, 0x00 });

        // +32 je <pass>           （同队 -> 放行，但**仍走空格链**）
        AddRel8(stub, 0x74, out int jeSameTeam);

        int skipOff = stub.Count;
        EmitJumpViaR11(stub, skip);

        int passOff = stub.Count;
        EmitJumpViaR11(stub, pass);

        // 回填 rel8（相对**下一条指令**的位移：下一条指令 = rel8 偏移 + 1）。
        //
        // ⚠️ 这里**不再写死 code[3]/[12]/[21]/[33]** —— 那是本项目的已知坑：
        //    指令布局一变，写死的下标就会静默错位，跳转落到垃圾地址。
        //    改为从**发射时的实际位置**回填，并在下面自检（同 WallPassHook）。
        byte[] code = stub.ToArray();
        PatchRel8(code, jeAlZero, passOff);
        PatchRel8(code, jeUnitNull, skipOff);
        PatchRel8(code, jeTeamNull, skipOff);
        PatchRel8(code, jeSameTeam, passOff);

        VerifyStub(code, passOff, skipOff, jeAlZero, jeUnitNull, jeTeamNull, jeSameTeam);

        return code;
    }

    /// <summary>
    /// 发射一条 rel8 条件跳转，并返回其 **rel8 操作数**在缓冲区中的偏移。
    ///
    /// <para>
    /// ⚠️ 返回值必须是**操作数**的偏移，不是 opcode 的偏移 ——
    /// 后者会让 <see cref="PatchRel8"/> 把位移写盖到 opcode 上。
    /// </para>
    /// </summary>
    private static void AddRel8(List<byte> stub, byte opcode, out int rel8Offset)
    {
        stub.Add(opcode);
        rel8Offset = stub.Count;   // ← 指向下面那个占位字节，而不是 opcode
        stub.Add(0);
    }

    /// <summary>把 <paramref name="rel8Offset"/> 处的 rel8 回填为跳向 <paramref name="targetOff"/>。</summary>
    private static void PatchRel8(byte[] code, int rel8Offset, int targetOff)
    {
        // rel8 相对**下一条指令**：下一条指令地址 = rel8Offset + 1
        code[rel8Offset] = (byte)(targetOff - (rel8Offset + 1));
    }

    /// <summary>
    /// 自检：反查每条条件跳转的目的地是否与预期出口一致。
    ///
    /// <para>
    /// 本项目在此处真实踩过坑 —— 跳转出口算错时不会报错，只会表现为
    /// 「AI 能站到玩家格子上」这种看上去毫不相关的行为异常。
    /// 有这个自检，算错偏移会在安装瞬间就去日志报警。
    /// </para>
    ///
    /// <para>
    /// ❗ 特别注意 <paramref name="jeSameTeam"/> 必须指向 <paramref name="passOff"/>（**不能**是
    /// 「直接接受」），否则会跳过 <c>AroundGridHaveEnemy</c>，
    /// 重现「AI 站到玩家头上」的 bug。
    /// </para>
    /// </summary>
    private void VerifyStub(
        byte[] code, int passOff, int skipOff,
        int jeAlZero, int jeUnitNull, int jeTeamNull, int jeSameTeam)
    {
        var checks = new System.Collections.Generic.List<(int at, int expect, string name)>
        {
            (jeAlZero, passOff, "无存活单位 -> pass（空格链）"),
            (jeUnitNull, skipOff, "battleUnit == null -> skip"),
            (jeTeamNull, skipOff, "battleTeam == null -> skip"),
            (jeSameTeam, passOff, "同队 -> pass（空格链，不可直接接受）"),
        };

        foreach (var (at, expect, name) in checks)
        {
            if (at < 0 || at >= code.Length)
            {
                Plugin.Log.Error($"{Tag} stub 自检失败：{name} 的 rel8 偏移 {at} 越界（长度 {code.Length}）。");
                continue;
            }

            int dest = at + 1 + (sbyte)code[at];

            if (dest != expect)
            {
                Plugin.Log.Error(
                    $"{Tag} stub 自检失败：{name} 的跳转目标算出 +{dest:x}，期望 +{expect:x}。" +
                    "寻路判定会错乱（典型症状：AI 能站到玩家格子上），请检查 BuildStub 的指令布局。");
            }
        }

        if (passOff == skipOff)
        {
            Plugin.Log.Error($"{Tag} stub 自检失败：出口重叠（pass={passOff} skip={skipOff}）。");
        }

        if (passOff >= code.Length || skipOff >= code.Length)
        {
            Plugin.Log.Error($"{Tag} stub 自检失败：出口越界（pass={passOff} skip={skipOff} 长度={code.Length}）。");
        }
    }
}
