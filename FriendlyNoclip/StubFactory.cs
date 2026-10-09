using System;
using Iced.Intel;
using static Iced.Intel.AssemblerRegisters;

namespace Unnamed42.FriendlyNoclip;

/// <summary>
/// 两个 detour stub 的**构码纯函数**：输入只有「字段偏移 + 出口地址」，
/// 不碰 IL2CPP、不碰 MelonLoader。
///
/// <para>
/// 【为什么单独抽出来】stub 的字节应当只由 <c>(偏移, 出口)</c> 唯一决定 ——
/// 抽成纯函数后，就可以用真实 <c>GameAssembly.dll</c> 在**活进程之外**把两个 stub 编出来，
/// 核对长度、出口位置与出口立即数（见 <c>tools/native_scan_harness</c>）。
/// 本项目的 stub 曾经因为「出口选在中段」「用 rax 中转」出过**静默语义错误**，
/// 这类错误只有在字节层面能提前暴露。
/// </para>
///
/// <para>
/// 所有偏移都来自 <see cref="NavigateLayout"/>（运行时发现 / IL2CPP 元数据），
/// 所有出口地址都来自 <see cref="ScannedHook"/>（同一函数体内的跳转目标），
/// 所以这里**没有一个写死的游戏地址**。
/// </para>
/// </summary>
internal static class StubFactory
{
    /// <summary>
    /// 穿友方 stub。
    ///
    /// <para>入口状态（Dobby 是<b>跳</b>进来，<c>rsp</c> 不变）：</para>
    /// <list type="bullet">
    ///   <item><c>al</c> = <c>get_IsAlive(g.battleUnit)</c> 的返回值</item>
    ///   <item><c>rsi</c> = 邻格 <c>g</c>（<c>GridUnitData*</c>）</item>
    ///   <item><c>[rsp + selfTeamIdStackOffset]</c> = <c>selfTeamID</c></item>
    /// </list>
    ///
    /// <para>
    /// 【职责边界】本 stub 只把阻挡判据从「有存活单位」改成「有敌方单位」，
    /// <b>不接管</b>「落点合不合法」；所以「同队」也只能送回
    /// <paramref name="pass"/>（原 <c>jne</c> 不跳时的落点 = 游戏自己的空格链，
    /// 里面还有 <c>AroundGridHaveEnemy</c> 要跑）。
    /// </para>
    /// </summary>
    internal static byte[] BuildFriendly(
        string tag,
        long stubRip,
        int offBattleUnit,
        int offBattleTeam,
        int offTeamId,
        int selfTeamIdStackOffset,
        long skip,
        long pass)
    {
        return StubAssembler.Build(tag, 64, stubRip, asm =>
        {
            Label exitSkip = asm.CreateLabel("skip");
            Label exitPass = asm.CreateLabel("pass");

            // al == 0（无存活单位）→ 空格链，保持原行为。
            asm.test(al, al);
            asm.je(exitPass);

            asm.mov(rax, __qword_ptr[rsi + offBattleUnit]);        // g.battleUnit
            asm.test(rax, rax);
            asm.jz(exitSkip);

            asm.mov(rax, __qword_ptr[rax + offBattleTeam]);        // battleUnit.battleTeam
            asm.test(rax, rax);
            asm.jz(exitSkip);

            asm.mov(eax, __dword_ptr[rax + offTeamId]);            // battleTeam.ID
            asm.cmp(eax, __dword_ptr[rsp + selfTeamIdStackOffset]);
            asm.je(exitPass);                                      // 同队 -> pass（走空格链，不可直接接受）

            // ---- 两个出口 ----
            // ⚠️ 两个标签必须落在**不同指令**上（Iced：At most one label per instruction is allowed）。
            asm.Label(ref exitSkip);
            asm.ExitViaImm64(skip);

            asm.Label(ref exitPass);
            asm.ExitViaImm64(pass);
        });
    }

    /// <summary>
    /// 城防 stub。
    ///
    /// <para>入口状态：<c>rax</c> = 邻格 <c>g</c>（上一条 <c>cmp</c> 刚读过它），
    /// <c>[rsp + selfTeamIdStackOffset]</c> = <c>selfTeamID</c>。</para>
    ///
    /// <para>
    /// ★★ hook 点落在 <c>je</c> 上，**两种情况都会执行到**：
    /// ① <c>gridType == Obstacle</c> → 原本会跳；② <c>!= Obstacle</c> → 原本不跳。
    /// 所以 stub 必须自己重判 <c>gridType</c>，否则会把普通格也当成障碍排除
    /// （实测后果：移动范围只剩脚下那一格）。
    /// </para>
    ///
    /// <para>
    /// ⚠️⚠️ 出口**不能用 <c>rax</c> 中转**：<paramref name="pass"/> 处是紧跟 <c>cmp</c> 的原始代码
    /// （首条 <c>mov r9,[rax]</c>），它期望 <c>rax</c> 仍是邻格指针。
    /// 用 <c>mov rax,imm64; jmp rax</c> 会把代码地址当类指针解引用 → SIGSEGV（gdb 现场已证）。
    /// </para>
    /// </summary>
    internal static byte[] BuildWall(
        string tag,
        long stubRip,
        int offGridType,
        int offObstale,
        int offObstacleType,
        int offObstacleTeam,
        int selfTeamIdStackOffset,
        int gridTypeObstacle,
        int obstacleTypeWall,
        long skip,
        long pass)
    {
        return StubAssembler.Build(tag, 64, stubRip, asm =>
        {
            Label exitSkip = asm.CreateLabel("skip");
            Label exitPass = asm.CreateLabel("pass");

            // g.gridType == Obstacle ?
            asm.cmp(__dword_ptr[rax + offGridType], gridTypeObstacle);

            // ── 出口 ①：非障碍格 → 原样继续（不可省！）
            asm.jne(exitPass);

            // ---- 以下只在「障碍格」时执行 ----
            asm.mov(rcx, __qword_ptr[rax + offObstale]);           // g.obstale
            asm.test(rcx, rcx);
            asm.jz(exitSkip);                                      // 无障碍物数据 -> 保持原行为

            asm.cmp(__dword_ptr[rcx + offObstacleType], obstacleTypeWall);
            asm.jne(exitSkip);                                     // 普通障碍（造景/木桶…）-> 保持原行为

            // == selfTeamID ?
            asm.mov(edx, __dword_ptr[rcx + offObstacleTeam]);
            asm.cmp(edx, __dword_ptr[rsp + selfTeamIdStackOffset]);
            asm.jne(exitSkip);                                     // 他方城墙 -> 保持原行为

            // ---- 两个出口 ----
            // ⚠️ pass 必须紧跟检查：三条全不跳时**顺序落入** pass。
            asm.Label(ref exitPass);
            asm.ExitViaImm64(pass);

            asm.Label(ref exitSkip);
            asm.ExitViaImm64(skip);
        });
    }
}
