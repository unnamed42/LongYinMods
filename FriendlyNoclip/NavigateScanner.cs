using System;
using System.Collections.Generic;
using Iced.Intel;

namespace Unnamed42.FriendlyNoclip;

/// <summary>
/// 从 <c>MapNavigator.Navigate</c> 的函数体里**发现**出来的一个 hook 落点。
///
/// <para>
/// 【为什么要有它】本 mod 的两个 detour 都装在函数**中间**的 <c>jcc</c> 上，
/// 而游戏一更新这些**绝对地址就全变**（2026-10 的更新：整个函数搬了 <c>-0x3aa60</c>，
/// 且函数内部还插入了约 <c>0xee</c> 字节新代码 —— 连「函数内偏移」都不稳定）。
/// 硬编码 VA 的结果就是本次事故：落点校验失败、detour 静默不装。
/// </para>
///
/// <para>
/// 【判据】落点由**指令语义**决定，不由地址决定：
/// <list type="bullet">
///   <item>城防：<c>test rX,rX ; je L ; cmp dword [rX+0x14], 2 ; je L</c>
///         —— 「邻格为空 或 是障碍格 → 跳过」，两个分支去同一个 continue 块。</item>
///   <item>穿友方：<c>mov rC,[rsi+0x18] ; test rC,rC ; je Lx ; xor edx,edx ; call f ;
///         test al,al ; jne L</c> —— <c>BattleUnit.get_IsAlive</c> 的返回值判定。</item>
/// </list>
/// 两个落点各自带出「跳转目标（skip）」与「不跳落点（pass）」，所以连
/// <c>VaSkip</c>/<c>VaPass</c> 也不必再硬编码。
/// </para>
///
/// <para>
/// 【本类不做的事】不查 IL2CPP、不碰 MelonLoader —— 输入是一段字节 + 它将运行的地址，
/// 输出是纯数据。这样它可以在**活进程之外**用真实 <c>GameAssembly.dll</c> 跑测试
/// （见 <c>tools/native_scan_harness</c> 的离线 harness）。
/// </para>
/// </summary>
internal sealed class ScannedHook
{
    /// <summary>被替换的那条 6 字节 <c>jcc rel32</c> 的**运行时**地址。</summary>
    internal ulong Site;

    /// <summary>该 <c>jcc</c> 的跳转目标（函数内的 continue 块）。</summary>
    internal ulong Skip;

    /// <summary>该 <c>jcc</c> 不跳时的落点，即 <see cref="Site"/> + 6。</summary>
    internal ulong Pass;

    /// <summary>操作码字节：<c>0F 85</c> = <c>jne</c>，<c>0F 84</c> = <c>je</c>（仅日志展示）。</summary>
    internal byte Op0;
    internal byte Op1;

    /// <summary>候选是从哪种「字段偏移」上下文里读出来的（诊断用，0 = 无）。</summary>
    internal int FieldOffset;

    /// <summary>仅穿友方候选有值：那一次 <c>call</c> 的目标（<c>BattleUnit.get_IsAlive</c>，诊断用）。</summary>
    internal ulong IsAliveCallTarget;
}

/// <summary>一次扫描的全部产出：都是「函数体内部」的事实，不含任何外部推导。</summary>
internal sealed class NavigateScanResult
{
    internal ScannedHook? Wall;
    internal ScannedHook? Friendly;

    /// <summary><c>GridUnitData.gridType</c> 的偏移（由城防候选的 <c>cmp</c> 现场读出）。</summary>
    internal int GridTypeOffset;

    /// <summary><c>GridUnitData.battleUnit</c> 的偏移（由穿友方候选的 <c>mov</c> 现场读出）。</summary>
    internal int BattleUnitOffset;

    /// <summary><c>selfTeamID</c> 的栈槽偏移；0 表示没识别出来。</summary>
    internal int SelfTeamIdStackOffset;

    /// <summary><c>BattleUnit.get_IsAlive</c> 的调用目标（诊断用）。</summary>
    internal ulong IsAliveCall;

    /// <summary>两个候选的 skip 是否指向同一个 continue 块（不变式，仅诊断）。</summary>
    internal bool SameSkipTarget;

    /// <summary>解码出的指令条数（诊断用）。</summary>
    internal int InstructionCount;
}

/// <summary>
/// <c>MapNavigator.Navigate</c> 函数体的**语义扫描器**（见 <see cref="ScannedHook"/> 的说明）。
/// </summary>
internal static class NavigateScanner
{
    /// <summary><c>GridType.Obstacle</c> —— 枚举为 <c>None=0, Normal=1, Obstacle=2</c>。</summary>
    private const int GridTypeObstacle = 2;

    /// <summary>
    /// 城防候选里 <c>test rX,rX</c> / <c>cmp dword [rX+disp],2</c> 用的寄存器。
    /// 必须是 <c>rax</c>：stub 依赖 <c>rax</c> 承载邻格 <c>g</c>（见 <c>WallPassHook</c>）。
    /// 换寄存器是**游戏更新的合法可能**，届时这里会失败关闭并打出日志，由人改 stub。
    /// </summary>
    private static readonly Register WallGridRegister = Register.RAX;

    /// <summary>
    /// 穿友方候选里 <c>mov rC,[rG+disp]</c> 的基址寄存器。必须是 <c>rsi</c>：
    /// stub 依赖 <c>rsi</c> 承载邻格 <c>g</c>（见 <c>FriendlyPassHook</c>）。
    /// </summary>
    private static readonly Register FriendlyGridRegister = Register.RSI;

    /// <summary>从「不跳落点」往后最多看多少条指令去找 selfTeamID 的栈槽。</summary>
    private const int SelfTeamIdLookahead = 16;

    /// <summary>
    /// 扫描一段**边界已确定**的函数体。
    /// </summary>
    /// <param name="code">
    /// 函数体字节。<b>必须从函数第一条指令开始</b> —— 线性扫描从第一条指令切边界，
    /// 从中间开始时后面所有边界都会错位。
    /// </param>
    /// <param name="startVa"><paramref name="code"/>[0] 将来运行时的地址（Iced 用它算相对跳转目标）。</param>
    /// <param name="result">找到的落点集合。</param>
    /// <param name="reason">失败原因（面向日志：哪一步、期望什么、看到什么）。</param>
    internal static bool TryScan(byte[] code, ulong startVa, out NavigateScanResult? result, out string reason)
    {
        result = null;

        if (code.Length < 32)
        {
            reason = $"函数体只有 {code.Length} 字节，不足以扫描。";
            return false;
        }

        List<Instruction> instructions = Decode(code, startVa);

        List<ScannedHook> walls = FindWallCandidates(instructions);
        List<ScannedHook> friendlies = FindFriendlyCandidates(instructions);

        // 失败时也把「解码了多少条指令」带出去：
        // 「解码 0 条」（字节不对 / 读了错地方）与「解码了几百条但形态不匹配」（形态真变了）
        // 是两个完全不同的病因。success 时下面会用完整结果覆盖它。
        result = new NavigateScanResult { InstructionCount = instructions.Count };

        // ⚠️ 两个候选都必须是**唯一**的。
        //    不唯一说明代码形态变了（或扫描窗口串到了别的函数）——
        //    这时候「挑第一个」就是在赌，赌错会静默地改错地方，比不装更糟。
        if (walls.Count != 1)
        {
            reason = $"城防形态（test rX,rX / je / cmp dword [rX+disp],2 / je）命中 {walls.Count} 处，不唯一，拒绝猜测。";
            return false;
        }

        if (friendlies.Count != 1)
        {
            reason =
                $"穿友方形态（mov rC,[rsi+disp] / test / je / xor edx,edx / call / test al,al / jne）" +
                $"命中 {friendlies.Count} 处，不唯一，拒绝猜测。";
            return false;
        }

        ScannedHook wall = walls[0];
        ScannedHook friendly = friendlies[0];

        // 交叉不变式：两条分支的语义都是「跳过这个邻格」，所以在新旧两个构建里
        // 它们都指向**同一个** continue 块。这里只记下是否成立，**不作为拒绝理由** ——
        // 万一某次更新让编译器把尾部拆成两块，两个 hook 各自的出口仍然是对的。
        var scanned = new NavigateScanResult
        {
            InstructionCount = instructions.Count,
            Wall = wall,
            Friendly = friendly,
            GridTypeOffset = wall.FieldOffset,
            BattleUnitOffset = friendly.FieldOffset,
            IsAliveCall = friendly.IsAliveCallTarget,
            SameSkipTarget = wall.Skip == friendly.Skip,
        };

        // 分支目标必须在**函数体内部** —— 这条把「匹配到了别处函数里的相似形态」挡在外面。
        ulong endVa = startVa + (ulong)code.Length;

        foreach (ScannedHook hook in new[] { wall, friendly })
        {
            if (hook.Skip <= startVa || hook.Skip >= endVa)
            {
                reason = $"0x{hook.Site:x} 的跳转目标 0x{hook.Skip:x} 落在函数体外 [{startVa:x},{endVa:x})，形态可疑。";
                return false;
            }
        }

        // selfTeamID 的栈槽从「不跳落点」之后的代码里读，不写死 0xd8。
        scanned.SelfTeamIdStackOffset = FindSelfTeamIdStackOffset(instructions, friendly.Pass);

        if (scanned.SelfTeamIdStackOffset == 0)
        {
            reason =
                $"在 0x{friendly.Pass:x}（穿友方落点之后）的 {SelfTeamIdLookahead} 条指令内" +
                "找不到 selfTeamID 的读取形态（mov r32,[rsp+d] 紧跟 cmp r32,-1）。";
            return false;
        }

        result = scanned;
        reason = string.Empty;
        return true;
    }

    /// <summary>
    /// 收集函数体内所有**直接跳转**（<c>jmp rel32</c>）的目标。
    ///
    /// <para>
    /// 用途：某些方法的入口并不是真身 —— 例如 IL2CPP 的 class-init wrapper（它用
    /// 「顺序落入」，由 <c>NativeMemory.TryGetNextFunctionStart</c> 处理），
    /// 或人装的跳转 thunk。扫描失败时，这些目标就是下一批候选入口。
    /// </para>
    /// </summary>
    internal static List<ulong> CollectDirectJumpTargets(byte[] code, ulong startVa)
    {
        var targets = new List<ulong>();

        foreach (Instruction instruction in Decode(code, startVa))
        {
            if (instruction.Mnemonic == Mnemonic.Jmp &&
                instruction.Op0Kind == OpKind.NearBranch64 &&
                instruction.NearBranch64 != 0)
            {
                targets.Add(instruction.NearBranch64);
            }
        }

        return targets;
    }

    /// <summary>线性扫描整段函数体。必须从第一条指令开始（见 <see cref="TryScan"/> 的说明）。</summary>
    private static List<Instruction> Decode(byte[] code, ulong startVa)
    {
        Decoder decoder = Decoder.Create(64, code, startVa);

        var list = new List<Instruction>(code.Length / 3 + 16);
        ulong end = startVa + (ulong)code.Length;

        while (decoder.IP < end)
        {
            Instruction instruction = decoder.Decode();

            // 数据不足 / 无效编码：线性扫描到此为止 —— 再往后指令边界已不可信。
            if (instruction.Code == Code.INVALID)
            {
                break;
            }

            list.Add(instruction);
        }

        return list;
    }

    /// <summary>
    /// 找城防落点形态：<c>test rX,rX ; je L ; cmp dword [rX+disp], 2 ; je L</c>。
    /// <see cref="ScannedHook.FieldOffset"/> 记下那个 <c>disp</c>（即 <c>gridType</c> 的偏移）。
    /// </summary>
    private static List<ScannedHook> FindWallCandidates(List<Instruction> ins)
    {
        var found = new List<ScannedHook>();

        for (int i = 0; i + 3 < ins.Count; i++)
        {
            Instruction test = ins[i];
            Instruction jeNull = ins[i + 1];
            Instruction cmp = ins[i + 2];
            Instruction site = ins[i + 3];

            // ① test rX, rX
            if (test.Mnemonic != Mnemonic.Test || !IsSameRegister(test, WallGridRegister))
            {
                continue;
            }

            // ② je <skip> —— 6 字节 rel32（等长替换的硬要求）
            if (jeNull.Mnemonic != Mnemonic.Je || !IsRel32Branch(jeNull))
            {
                continue;
            }

            // ③ cmp dword [rX + disp], 2 —— disp 就是 gridType 的偏移（现场读出，不写死 0x14）
            if (cmp.Mnemonic != Mnemonic.Cmp ||
                cmp.Op0Kind != OpKind.Memory || cmp.MemoryBase != WallGridRegister ||
                cmp.MemoryIndex != Register.None || cmp.MemoryBase == Register.RIP)
            {
                continue;
            }

            if (!IsImmediate(cmp, GridTypeObstacle))
            {
                continue;
            }

            // ④ je <skip> —— 与 ② 去**同一个** continue 块（「空 或 障碍 → 跳过」）
            if (site.Mnemonic != Mnemonic.Je || !IsRel32Branch(site))
            {
                continue;
            }

            if (site.NearBranch64 != jeNull.NearBranch64)
            {
                continue;
            }

            found.Add(MakeHook(site, (int)cmp.MemoryDisplacement64));
        }

        return found;
    }

    /// <summary>
    /// 找穿友方落点形态：
    /// <c>mov rC,[rsi+disp] ; test rC,rC ; je Lx ; xor edx,edx ; call f ; test al,al ; jne L</c>。
    /// <see cref="ScannedHook.FieldOffset"/> 记下那个 <c>disp</c>（即 <c>battleUnit</c> 的偏移）。
    /// </summary>
    private static List<ScannedHook> FindFriendlyCandidates(List<Instruction> ins)
    {
        var found = new List<ScannedHook>();

        for (int i = 0; i + 6 < ins.Count; i++)
        {
            Instruction mov = ins[i];
            Instruction test = ins[i + 1];
            Instruction jeNull = ins[i + 2];
            Instruction xorEdx = ins[i + 3];
            Instruction call = ins[i + 4];
            Instruction testAl = ins[i + 5];
            Instruction site = ins[i + 6];

            // ① mov rC, [rsi + disp] —— rsi 承载邻格 g，disp 是 g.battleUnit 的偏移
            if (mov.Mnemonic != Mnemonic.Mov || mov.Op0Kind != OpKind.Register)
            {
                continue;
            }

            if (mov.Op1Kind != OpKind.Memory || mov.MemoryBase != FriendlyGridRegister ||
                mov.MemoryIndex != Register.None)
            {
                continue;
            }

            // 目标寄存器不能就是基址寄存器：stub 之后还要用 rsi 读 battleUnit。
            if (mov.Op0Register == FriendlyGridRegister)
            {
                continue;
            }

            // ② test rC, rC —— IL2CPP 的 null 检查
            if (test.Mnemonic != Mnemonic.Test || !IsSameRegister(test, mov.Op0Register))
            {
                continue;
            }

            // ③ je <raise_exception>（离场分支，不是 continue 块）
            if (jeNull.Mnemonic != Mnemonic.Je || !IsRel32Branch(jeNull))
            {
                continue;
            }

            // ④ xor edx, edx —— 清 MethodInfo* 槽（IL2CPP 调用的固定套路）
            if (xorEdx.Mnemonic != Mnemonic.Xor ||
                xorEdx.Op0Kind != OpKind.Register || xorEdx.Op0Register != Register.EDX ||
                xorEdx.Op1Kind != OpKind.Register || xorEdx.Op1Register != Register.EDX)
            {
                continue;
            }

            // ⑤ call <BattleUnit.get_IsAlive>
            if (call.Mnemonic != Mnemonic.Call || call.Op0Kind != OpKind.NearBranch64)
            {
                continue;
            }

            // ⑥ test al, al
            if (testAl.Mnemonic != Mnemonic.Test || !IsSameRegister(testAl, Register.AL))
            {
                continue;
            }

            // ⑦ jne <skip> —— ★ 本 hook 点
            if (site.Mnemonic != Mnemonic.Jne || !IsRel32Branch(site))
            {
                continue;
            }

            // ⑧ 两个分支必须去**不同**的地方：
            //    `je`  是 null 检查的离场分支（il2cpp 的 raise_exception）；
            //    `jne` 才是「存活 → 跳过该邻格」的 continue 块。
            //    （城防那条是两个分支都去 continue，与这里恰好相反 —— 别照搬。）
            if (site.NearBranch64 == jeNull.NearBranch64)
            {
                continue;
            }

            ScannedHook hook = MakeHook(site, (int)mov.MemoryDisplacement64);
            hook.IsAliveCallTarget = call.NearBranch64;
            found.Add(hook);
        }

        return found;
    }

    private static ScannedHook MakeHook(Instruction site, int fieldOffset)
    {
        bool isJne = site.Mnemonic == Mnemonic.Jne;

        return new ScannedHook
        {
            Site = site.IP,
            Skip = site.NearBranch64,
            Pass = site.IP + (ulong)site.Length,
            Op0 = 0x0F,
            Op1 = isJne ? (byte)0x85 : (byte)0x84,
            FieldOffset = fieldOffset,
        };
    }

    /// <summary>
    /// 从「不跳落点」往后找 <c>mov r32,[rsp+d]</c> 紧跟 <c>cmp r32,-1</c> ——
    /// 那就是 <c>if (selfTeamID != -1)</c>，<c>d</c> 即 <c>selfTeamID</c> 的栈槽偏移。
    ///
    /// <para>
    /// 【为什么匹配形态而不是写死 0xd8】栈槽偏移随函数序言/局部变量变化，属于
    /// 「游戏一更新就可能变」的那类常量。形态匹配把「谁是这个值」交回代码本身回答。
    /// </para>
    /// </summary>
    private static int FindSelfTeamIdStackOffset(List<Instruction> ins, ulong passVa)
    {
        int start = -1;

        for (int i = 0; i < ins.Count; i++)
        {
            if (ins[i].IP == passVa)
            {
                start = i;
                break;
            }
        }

        if (start < 0)
        {
            return 0;
        }

        int limit = Math.Min(ins.Count - 1, start + SelfTeamIdLookahead);

        for (int i = start; i < limit; i++)
        {
            Instruction mov = ins[i];
            Instruction cmp = ins[i + 1];

            if (mov.Mnemonic != Mnemonic.Mov || mov.Op0Kind != OpKind.Register || !IsGpr32(mov.Op0Register))
            {
                continue;
            }

            if (mov.Op1Kind != OpKind.Memory || mov.MemoryBase != Register.RSP ||
                mov.MemoryIndex != Register.None)
            {
                continue;
            }

            if (cmp.Mnemonic != Mnemonic.Cmp ||
                cmp.Op0Kind != OpKind.Register || cmp.Op0Register != mov.Op0Register ||
                !IsImmediate(cmp, -1))
            {
                continue;
            }

            return (int)mov.MemoryDisplacement64;
        }

        return 0;
    }

    /// <summary>6 字节 <c>jcc rel32</c>（等长替换的前提）。</summary>
    private static bool IsRel32Branch(in Instruction i)
        => i.Op0Kind == OpKind.NearBranch64 && i.Length == 6;

    private static bool IsSameRegister(in Instruction i, Register r)
        => i.Op0Kind == OpKind.Register && i.Op1Kind == OpKind.Register &&
           i.Op0Register == r && i.Op1Register == r;
    /// <summary>
    /// 判断一个 <c>cmp r/m, imm</c> 的立即数是不是 <paramref name="value"/>。
    ///
    /// <para>
    /// ⚠️ 不能只看 <c>Op1Kind == Immediate32</c>：汇编器会把能塞进**有符号 8 位**的立即数
    /// 编成 <c>83 /7 ib</c>，此时 Iced 报的是 <see cref="OpKind.Immediate8to32"/>。
    /// 本项目就因此让扫描器「一处都匹配不到」（离线 harness 拓到的）。
    /// </para>
    /// </summary>
    private static bool IsImmediate(in Instruction i, int value) => i.Op1Kind switch
    {
        OpKind.Immediate8 => i.Immediate8 == (byte)value,
        OpKind.Immediate8to16 => i.Immediate8to16 == (short)value,
        OpKind.Immediate8to32 => i.Immediate8to32 == value,
        OpKind.Immediate8to64 => i.Immediate8to64 == value,
        OpKind.Immediate32 => i.Immediate32 == (uint)value,
        OpKind.Immediate32to64 => i.Immediate32to64 == value,
        _ => false,
    };
    // ⚠️ 依赖 Iced 的 Register 枚举把同名宽度的通用寄存器排成连续区间
    //    （EAX..R15D / RAX..R15 各自连续）。Iced 的枚举顺序是稳定契约。
    private static bool IsGpr32(Register r) => r >= Register.EAX && r <= Register.R15D;
}
