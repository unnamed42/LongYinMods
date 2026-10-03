using System;
using System.Collections.Generic;
using System.Text;
using Iced.Intel;
using static Iced.Intel.AssemblerRegisters;

namespace Unnamed42.FriendlyNoclip;

/// <summary>
/// 用 <b>Iced</b> 汇编器构造 detour stub —— 取代手写机器码。
///
/// <para>
/// 【为什么不再手写】本项目在手写字节上真实崩过两次，而两次的症状都<em>不是</em>「编译报错」：
/// </para>
/// <list type="number">
///   <item>
///     <b>SIGILL</b>：把 rel8 的「操作数偏移」当成「opcode 偏移」，
///     <c>PatchRel8</c> 把位移写盖在 <c>0x74</c> 操作码上，<c>74 16</c> 变成
///     <c>&lt;disp&gt; 16</c> —— 非法指令，战斗刚开始即崩。
///   </item>
///   <item>
///     <b>静默语义错误</b>：出口选在「接受路径的中段」，
///     跳过了 <c>AroundGridHaveEnemy</c>，表现为 <b>AI 站到玩家格子上</b> ——
///     一个和汇编看起来毫无关系的诡异现象。
///   </item>
/// </list>
/// <para>
/// 第 ② 类 Iced 帮不上忙（那是「有没有读懂代码」的问题，见 AGENTS.md §6）。
/// 第 ① 类 Iced 从根上消灭：偏移全部由汇编器算。
/// </para>
///
/// <para>
/// <b>⚠️ 有条件时优先用「标签」，而不是绝对地址。</b>
/// 出口写成 <c>asm.jmp(0x180A8D8BC)</c> 能跑，但它<b>不可重定位</b> ——
/// stub 一旦被搬到别处（离线基准、原地热替换），跳转仍指向旧地址。
/// 用 <see cref="ExitViaLabel"/>（尾部绑定标签 + RIP 相对寻址）则天然可重定位。
/// 本项目实测两者**输出完全一致**，所以没有理由不用标签版。
/// </para>
///
/// <para>
/// <b>三条实测得出的 Iced 硬性规则</b>（文档里没写，全部真踩过）：
/// </para>
/// <list type="number">
///   <item>
///     <b>一个指令位置最多绑一个标签</b>：连续两次 <c>Label(ref a); Label(ref b);</c>
///     抛 <c>ArgumentException: At most one label per instruction is allowed</c>。
///     → 两个出口分别放在两条 <c>mov</c> 上（本来也就是这样）。
///   </item>
///   <item>
///     <b>标签之后必须有指令</b>：在末尾绑一个「收尾标签」会抛
///     <c>Unused label end@N. You must emit an instruction after emitting a label.</c>
///     → <b>不要建收尾标签</b>；标签只绑在真正会被跳到的指令上。
///   </item>
///   <item>
///     <b>内存操作数不用 <c>dword_ptr(...)</c> 那种写法</b>，而是
///     <c>__dword_ptr[rax + 0x14]</c>（双下划线静态字段 + 索引器）。
///     <c>lea</c> 取标签地址时写成 <c>__qword_ptr[label]</c>。
///   </item>
/// </list>
/// </summary>
internal static class StubAssembler
{
    /// <summary>
    /// 汇编一段 stub，返回机器码。失败返回空数组（并已记 Error 日志），调用方须据此放弃安装。
    /// </summary>
    /// <param name="tag">日志前缀（各 hook 的 <c>Tag</c>）。</param>
    /// <param name="estimate">预估长度，仅用于预分配缓冲区。</param>
    /// <param name="rip">
    /// 这段代码将来<b>运行</b>的地址。<b>必须先分配内存再调用本方法</b>，
    /// 否则 rel8/rel32 与 RIP 相对寻址会算错。
    /// </param>
    /// <param name="emit">stub 本体。</param>
    internal static byte[] Build(string tag, int estimate, long rip, Action<Assembler> emit)
    {
        var asm = new Assembler(64);
        var writer = new Collector(estimate);

        try
        {
            emit(asm);

            // 用 TryAssemble 而非 Assemble：把汇编器的抱怨原样带进日志，而不是抛出去。
            // 最值钱的一条是 "Unused label … You must emit an instruction after emitting a label."
            // —— 那意味着某个出口标签后面什么都没绑，一定会跳进垃圾地址。
            if (!asm.TryAssemble(writer, (ulong)rip, out string? error, out _))
            {
                Plugin.Log.Error($"{tag} Iced 汇编失败（rip=0x{rip:x}）：{error}");
                return Array.Empty<byte>();
            }
        }
        catch (Exception e)
        {
            // Iced 对「漏绑标签」等结构性错误是**抛异常**而不是返回 false，
            // 所以这里必须兜住 —— 否则一个 stub 写错就会打断游戏启动。
            Plugin.Log.Error($"{tag} Iced 汇编异常（rip=0x{rip:x}）：{e.Message}");
            return Array.Empty<byte>();
        }

        byte[] code = writer.ToArray();

        if (code.Length == 0)
        {
            Plugin.Log.Error($"{tag} Iced 汇编产出 0 字节（rip=0x{rip:x}）。");
        }

        return code;
    }

    /// <summary>
    /// 发射出口跳转 —— <b>把目标地址直接作为立即数</b>：<c>mov r11, imm64</c> + <c>jmp r11</c>。
    ///
    /// <para>
    /// 用 <c>r11</c> 而不是 <c>rax</c> 是硬性要求：<c>r11</c> 是 Win64 易失寄存器，
    /// 本 mod 的所有出口目标后续代码均不读它（已逐条核实）。
    /// 而 <c>rax</c> <b>本项目真实崩过</b>：目标处第一条就是 <c>mov r9,[rax]</c>，
    /// 期望 rax 是邻格指针，却被 <c>mov rax,imm64</c> 改成了代码地址 → SIGSEGV。
    /// </para>
    /// <para>
    /// 另一处细节：<c>mov r11,imm64</c> <b>不读写标志位</b>。这很关键 ——
    /// 我们的 hook 点常常落在一个 <c>jcc</c> 上，替换后紧接着就要重判条件。
    /// </para>
    /// <para>
    /// ⚠️ 本方法产出的跳转<b>不可重定位</b>。<see cref="Jcc"/> / <see cref="Jmp"/> 才是
    /// 标签版；只有在「必须与既有手写字节逐字节一致」时才用本方法。
    /// </para>
    /// </summary>
    internal static void ExitViaImm64(this Assembler asm, long target)
    {
        asm.mov(r11, (ulong)target);
        asm.jmp(r11);
    }

    /// <summary>出口跳转（标签版，可重定位）：<c>lea r11,[label]</c> + <c>jmp r11</c>。</summary>
    internal static void ExitViaLabel(this Assembler asm, Label target)
    {
        asm.lea(r11, __qword_ptr[target]);
        asm.jmp(r11);
    }

    /// <summary>
    /// 用 Iced 自己的反汇编器把 stub 打出来。
    /// 失败时退化成十六进制串（不让「反汇编失败」把日志搞没了）。
    /// </summary>
    internal static string Describe(byte[] code, long rip)
    {
        if (code.Length == 0)
        {
            return "  <空>";
        }

        try
        {
            var sb = new StringBuilder();
            var decoder = Iced.Intel.Decoder.Create(64, new ByteArrayCodeReader(code));
            decoder.IP = (ulong)rip;

            while (true)
            {
                var insn = decoder.Decode();

                if (insn.Code == Code.INVALID)
                {
                    break;
                }

                sb.Append($"    +{insn.IP - (ulong)rip:x2}  {insn}");
                sb.Append('\n');
            }

            return sb.ToString().TrimEnd('\n');
        }
        catch (Exception e)
        {
            return $"  <反汇编失败：{e.Message}>";
        }
    }

    /// <summary>Iced 编码需要一个 <see cref="CodeWriter"/>；只有这一个成员必须实现。</summary>
    private sealed class Collector : CodeWriter
    {
        private byte[] _buffer;
        private int _length;

        internal Collector(int capacity)
        {
            _buffer = new byte[capacity <= 0 ? 128 : capacity];
        }

        public override void WriteByte(byte value)
        {
            if (_length == _buffer.Length)
            {
                Array.Resize(ref _buffer, _buffer.Length * 2);
            }

            _buffer[_length++] = value;
        }

        internal byte[] ToArray()
        {
            var result = new byte[_length];
            Array.Copy(_buffer, result, _length);
            return result;
        }
    }
}
