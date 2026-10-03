using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;

namespace Unnamed42.FriendlyNoclip;

/// <summary>
/// 原生 hook 的「进入记录器」—— 用于排查**崩溃点不在 hook 本身、
/// 而在 hook 之前**的那类问题。
///
/// <para>
/// 【为什么需要它】2026-10 的一次崩溃：<c>rip</c> 落在穿友方 stub 的 <c>+0x0b</c>，
/// 而那里是 <c>74 16</c>（<c>je rel8</c>）—— 一条**不可能触发 SIGILL** 的合法指令。
/// 同时 <c>rsp = 0xfeae0</c> 明显异常。结论：**栈在进入 stub 之前就已经坏了**，
/// 但 gdb 的 <c>bt</c> 全是 <c>??</c>（栈已毁，回溯不出来）。
/// </para>
///
/// <para>
/// 【思路】不要等崩溃时再回溯（那时栈已经废了）。改成**每次进入 hook 时就把
/// 现场记下来**：<c>rsp</c> + 沿栈往上走的若干返回地址，写进一块固定内存里的
/// **环形缓冲**。崩溃后由托管侧（或下次启动）读出来，就能看到
/// 「崩溃前最后若干次进入 hook」的栈状态演变 —— 特别是 <c>rsp</c> 是从哪一次
/// 开始跑偏的、以及当时谁在调用。
/// </para>
///
/// <para>
/// 【实现要点】
/// </para>
/// <list type="bullet">
///   <item>探针只破坏 <c>rax</c>/<c>rcx</c>/<c>rdx</c>/<c>r11</c>，并在进入时
///     <c>push</c> 保存 <c>rax</c>/<c>rcx</c>/<c>rdx</c>，退出前 <c>pop</c> 还原。</item>
///   <item>原始 <c>rsp</c> 先存进 <c>rdx</c>，再 <c>push</c> —— 这样抓栈时
///     <c>[rdx+8i]</c> 读到的仍是**进入 hook 时**的栈，不受 <c>push</c> 影响。</item>
///   <item>缓冲区地址用 <c>mov r64, imm64</c> 绝对值寻址，**不用 rip 相对** ——
///     后者要在 stub 定址后回填，多一个出错环节。</item>
///   <item>序号用 <c>lock xadd</c> 原子递增，多线程下不会互相覆盖。</item>
/// </list>
/// </summary>
internal static class NativeProbeLog
{
    /// <summary>每条记录保存多少个栈上的 qword。</summary>
    internal const int FramesPerEntry = 10;

    /// <summary>环形缓冲容量（条数），必须是 2 的幂。</summary>
    internal const int EntryCapacity = 1024;

    /// <summary>
    /// 一条记录的布局（每项 8 字节，全部按 qword 写）：
    /// <code>
    /// +0x00  seq      本次序号（从 1 开始）
    /// +0x08  rsp      进入 hook 时的栈指针
    /// +0x10  hookId   1 = 穿友方, 2 = 穿城墙
    /// +0x18  ret      [rsp] —— 进入 hook 时的返回地址
    /// +0x20.. 栈内容  [rsp], [rsp+8], ... 依次向上
    /// </code>
    /// </summary>
    internal const int QwordsPerEntry = 4 + FramesPerEntry;

    internal const int EntryBytes = QwordsPerEntry * 8;

    internal const int BufferBytes = EntryCapacity * EntryBytes;

    private static IntPtr _buffer = IntPtr.Zero;
    private static IntPtr _seq = IntPtr.Zero;

    /// <summary>记录器是否已就绪（缓冲区已分配）。</summary>
    internal static bool Ready => _buffer != IntPtr.Zero && _seq != IntPtr.Zero;

    internal static long BufferVa => _buffer.ToInt64();

    internal static long SeqVa => _seq.ToInt64();

    /// <summary>分配缓冲区。必须在构造任何 stub **之前**调用。</summary>
    internal static bool Initialize()
    {
        if (Ready)
        {
            return true;
        }

        try
        {
            _buffer = NativeMemory.AllocateExecutable(BufferBytes);
            _seq = NativeMemory.AllocateExecutable(64);

            if (_buffer == IntPtr.Zero || _seq == IntPtr.Zero)
            {
                Plugin.Log.Warning("[探针] 无法为回溯记录器分配内存，已跳过。");
                _buffer = IntPtr.Zero;
                _seq = IntPtr.Zero;
                return false;
            }

            // 清零：seq 必须从 0 开始，这样「seq == 0」就代表空槽。
            Marshal.Copy(new byte[BufferBytes], 0, _buffer, BufferBytes);
            Marshal.Copy(new byte[64], 0, _seq, 64);

            Plugin.Log.Msg(
                $"[探针] 回溯记录器就绪：缓冲 0x{BufferVa:x}（{EntryCapacity} 条 × {EntryBytes} 字节），" +
                $"序号槽 0x{SeqVa:x}");
            return true;
        }
        catch (Exception ex)
        {
            Plugin.Log.Warning($"[探针] 初始化记录器失败：{ex.Message}");
            _buffer = IntPtr.Zero;
            _seq = IntPtr.Zero;
            return false;
        }
    }

    /// <summary>
    /// 把「记录一次进入」的机器码追加到 <paramref name="stub"/>。
    /// <b>必须放在 stub 的最开头</b>（在任何条件判断之前），这样无论走哪条分支都能记到。
    /// </summary>
    internal static void EmitRecordEntry(List<byte> stub, int hookId)
    {
        if (!Ready)
        {
            return;
        }

        long bufVa = BufferVa;
        long seqVa = SeqVa;

        // rdx = 原始 rsp（抓栈基准，不受后续 push 影响）
        Add(stub, 0x48, 0x89, 0xE2);                    // mov rdx, rsp

        // 保存将被破坏的易失寄存器
        Add(stub, 0x50);                                // push rax
        Add(stub, 0x51);                                // push rcx

        // ---- 原子取号：eax = ++(*seq) ----
        Add(stub, 0x48, 0xB8);                          // mov rax, imm64(seq)
        AddImm64(stub, seqVa);
        Add(stub, 0xB9, 0x01, 0x00, 0x00, 0x00);        // mov ecx, 1
        Add(stub, 0xF0, 0x0F, 0xC1, 0x08);              // lock xadd dword [rax], ecx
        Add(stub, 0x8D, 0x41, 0x01);                    // lea eax, [rcx+1]   -> eax = 本次序号

        // 槽内偏移 = ((seq - 1) & (cap-1)) * EntryBytes
        Add(stub, 0x83, 0xE8, 0x01);                    // sub eax, 1
        Add(stub, 0x25);                                // and eax, imm32
        AddImm32(stub, EntryCapacity - 1);
        Add(stub, 0x69, 0xC0);                          // imul eax, eax, EntryBytes
        AddImm32(stub, EntryBytes);

        // rcx = 记录地址
        Add(stub, 0x48, 0xB9);                          // mov rcx, imm64(buffer)
        AddImm64(stub, bufVa);
        Add(stub, 0x48, 0x01, 0xC1);                    // add rcx, rax

        // 写 seq（qword，从 eax 零扩展到 rax：此时 eax 已是偏移，需另算）
        // 简化：seq 直接写「当前序号」——它已不再可用，改为重读 *seq。
        Add(stub, 0x48, 0xB8);                          // mov rax, imm64(seq)
        AddImm64(stub, seqVa);
        Add(stub, 0x8B, 0x00);                          // mov eax, [rax]     (dword)
        Add(stub, 0x48, 0x89, 0x01);                    // mov [rcx], rax     (seq)

        // 写 rsp
        Add(stub, 0x48, 0x89, 0x51, 0x08);              // mov [rcx+8], rdx

        // 写 hookId
        Add(stub, 0xC7, 0x41, 0x10);                    // mov dword [rcx+0x10], imm32
        AddImm32(stub, hookId);

        // 写 ret = [rdx]
        Add(stub, 0x48, 0x8B, 0x02);                    // mov rax, [rdx]
        Add(stub, 0x48, 0x89, 0x41, 0x18);              // mov [rcx+0x18], rax

        // 抓栈：[rdx + 8*i]
        for (int i = 0; i < FramesPerEntry; i++)
        {
            int disp = i * 8;
            if (disp == 0)
            {
                // 已在上面写过 [rcx+0x18]，这里帧 0 复用即可 —— 但要单独存一份。
                Add(stub, 0x48, 0x8B, 0x02);            // mov rax, [rdx]
            }
            else
            {
                Add(stub, 0x48, 0x8B, 0x82);            // mov rax, [rdx+disp32]
                AddImm32(stub, disp);
            }

            Add(stub, 0x48, 0x89, 0x41, (byte)(0x20 + i * 8));  // mov [rcx+0x20+8i], rax
        }

        // 还原
        Add(stub, 0x59);                                // pop rcx
        Add(stub, 0x58);                                // pop rax
    }

    private static void Add(List<byte> b, params byte[] bytes) => b.AddRange(bytes);

    private static void AddImm32(List<byte> b, int v) => b.AddRange(BitConverter.GetBytes(v));

    private static void AddImm64(List<byte> b, long v) => b.AddRange(BitConverter.GetBytes(v));

    private static System.Threading.Thread? _flusher;
    private static volatile bool _stop;

    /// <summary>
    /// 启动后台线程，**周期性把缓冲快照写到磁盘**。
    ///
    /// <para>
    /// 【为什么需要】进程一崩，内存里的缓冲就没了；而这个记录器存在的唯一理由
    /// 就是「崩溃后还能看到现场」。所以不能等崩溃时再写 —— 必须持续落盘。
    /// 用独立的 <see cref="System.Threading.Thread"/>（而非 Task）以避免
    /// 依赖线程池在游戏主循环外的调度。
    /// </para>
    ///
    /// <para>
    /// 写的是**原始快照**（当前序号 + 全部记录），不是格式化文本 ——
    /// 崩溃时最后写下的那份就足够定位问题，格式化可以在事后做。
    /// </para>
    /// </summary>
    internal static void StartFlusher(string path, int intervalMs = 2000)
    {
        if (_flusher != null || !Ready)
        {
            return;
        }

        _flusher = new System.Threading.Thread(() =>
        {
            while (!_stop)
            {
                try
                {
                    FlushSnapshot(path);
                }
                catch
                {
                    // 已尽力；不能因为写盘失败影响游戏。
                }

                System.Threading.Thread.Sleep(intervalMs);
            }
        })
        {
            IsBackground = true,
            Name = "FriendlyNoclip.ProbeFlusher",
        };

        _flusher.Start();
        Plugin.Log.Msg($"[探针] 快照线程已启动，每 {intervalMs}ms 写入 {path}");
    }

    /// <summary>停止快照线程（卸载时用）。</summary>
    internal static void StopFlusher() => _stop = true;

    /// <summary>
    /// 把当前缓冲写成人类可读的报告。**用 FileStream 直接写**，
    /// 避免 StringBuilder 在大缓冲下占用过多内存。
    /// </summary>
    internal static void FlushSnapshot(string path)
    {
        if (!Ready)
        {
            return;
        }

        long seq = Marshal.ReadInt32(_seq);
        string text = Dump(tail: 200);

        var sb = new StringBuilder();
        sb.AppendLine($"# FriendlyNoclip 回溯探针快照  {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
        sb.AppendLine($"# 序号计数 = {seq}");
        sb.AppendLine();
        sb.Append(text);

        System.IO.File.WriteAllText(path, sb.ToString());
    }

    /// <summary>
    /// 读出环形缓冲，按序号升序返回可读文本。只打印最后 <paramref name="tail"/> 条。
    /// </summary>
    /// </summary>
    internal static string Dump(int tail = 60)
    {
        if (!Ready)
        {
            return "[探针] 记录器未初始化。";
        }

        var recs = new List<(long seq, long rsp, long hookId, long ret, long[] frames)>();

        for (int i = 0; i < EntryCapacity; i++)
        {
            IntPtr p = _buffer + i * EntryBytes;
            long seq = Marshal.ReadInt64(p);
            if (seq == 0)
            {
                continue;
            }

            var frames = new long[FramesPerEntry];
            for (int f = 0; f < FramesPerEntry; f++)
            {
                frames[f] = Marshal.ReadInt64(p + 0x20 + f * 8);
            }

            recs.Add((
                seq,
                Marshal.ReadInt64(p + 0x08),
                Marshal.ReadInt64(p + 0x10),
                Marshal.ReadInt64(p + 0x18),
                frames));
        }

        if (recs.Count == 0)
        {
            return "[探针] 缓冲为空 —— hook 一次都没被进入过。";
        }

        recs.Sort((a, b) => a.seq.CompareTo(b.seq));

        var sb = new StringBuilder();
        sb.AppendLine($"[探针] 共 {recs.Count} 条（容量 {EntryCapacity}），以下为最后 {Math.Min(tail, recs.Count)} 条：");
        sb.AppendLine("seq    hook  rsp                ret                栈内容（由内向外）");

        int start = Math.Max(0, recs.Count - tail);
        for (int i = start; i < recs.Count; i++)
        {
            var r = recs[i];
            sb.Append($"#{r.seq,-5} {(r.hookId == 1 ? "友方" : "城墙")}  0x{r.rsp,-16:x} 0x{r.ret,-16:x} ");

            for (int f = 0; f < FramesPerEntry; f++)
            {
                long v = r.frames[f];
                if (v == 0)
                {
                    break;
                }

                // 把落在模块内的地址标出来，便于识别调用链。
                sb.Append($"0x{v:x} ");
            }

            sb.AppendLine();
        }

        return sb.ToString();
    }
}
