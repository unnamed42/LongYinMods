using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using MelonLoader.NativeUtils;

namespace Unnamed42.FriendlyNoclip;

/// <summary>
/// 一个原生 detour 钩子的公共骨架。
///
/// <para>
/// 【为什么要有基类】本 mod 需要在 <c>MapNavigator.Navigate</c> 内**两处**不同位置
/// 装 detour（穿友方单位、穿己方城墙）。这两个钩子的「安装 / 校验 / 卸载」流程
/// 完全一样，只有 hook 地址、校验规则、stub 汇编不同。
/// </para>
///
/// <para>
/// **历史上这里踩过坑**：早期 <c>NativeOnEnterDetour</c> 自带一套**平行复制**的
/// 安装逻辑（自己的字节校验、自己的跳板回填），结果那套逻辑的前提错了而无人察觉，
/// 最终以「一进战斗即崩」收场。所以现在把骨架收成**唯一一份**，子类只写差异。
/// </para>
///
/// <para>
/// 【安装流程】解析模块基址 → 读落点字节 → 子类校验 → 构造 stub → 分配可执行内存 →
/// 拷贝 → <see cref="NativeHook{T}"/> 挂载 → 确认 <c>IsHooked</c> → 日志。
/// 失败只记日志，**绝不抛异常打断游戏启动**。
/// </para>
///
/// <para>
/// 【为什么用 <see cref="NativeHook{T}"/> 而不是 <c>MelonUtils.NativeHookAttach</c>】：
/// 后者带 <c>[Obsolete(..., error: true)]</c>（MelonLoader 0.7.3），调用是编译错误；
/// 同名的 <c>NativeHookAttachDirect</c> 则是 <c>internal</c>。
/// <see cref="NativeHook{T}"/> 是公开类型，其 <c>Attach()</c> 内部走
/// <c>BootstrapInterop.NativeHookAttach</c>，在 Wine/Proton 下 <c>||</c> 短路，
/// 不会触发 <c>SanityCheckDetour</c>（那在 Linux 下会做全量 coredump）。
/// </para>
///
/// <para>
/// 【关于泛型参数】<see cref="NativeHook{T}"/> 的 <c>HookAttach()</c> 会把跳板地址
/// <c>Marshal.GetDelegateForFunctionPointer</c> 成 <c>T</c>。我们的跳板是 Dobby 生成的
/// 原函数代码，签名并不匹配 <c>T</c> —— 但该转换<em>不执行</em>任何代码，
/// 且本类<em>从不读取</em> <c>Trampoline</c> 属性，所以只是声明层面的占位。
/// </para>
/// </summary>
internal abstract class NativeHookBase
{
    /// <summary><see cref="NativeHook{T}"/> 的泛型占位签名。永不调用，仅满足 <c>where T : Delegate</c>。</summary>
    internal delegate void DetourSignature();

    // ---- 每个子类各自的 hook 状态 ----
    private IntPtr _stub = IntPtr.Zero;
    private NativeHook<DetourSignature>? _hook;
    private bool _installed;
    private bool _detached;

    /// <summary>
    /// 给 stub 预留的字节数。
    ///
    /// <para>
    /// 【为什么要预留够】先分配、再构码，所以分配时并不知道码有多长。
    /// 本项目的 stub 都在 60 字节上下（两道出口各 13 字节），256 字节余量充足。
    /// 超出会被 <see cref="Install"/> 拒绝（不会越界写坏邻居内存）。
    /// </para>
    /// </summary>
    protected virtual int AllocatedStubLength => 256;

    /// <summary>是否已成功安装 detour。</summary>
    internal bool Installed => _installed;

    /// <summary>上一次 <see cref="Uninstall"/> 是否真的走到了 detach。</summary>
    internal bool Detached => _detached;

    // ---- 子类必须提供的差异 ----

    /// <summary>日志前缀，如 <c>[原生 detour]</c> / <c>[城防 detour]</c>。</summary>
    protected abstract string Tag { get; }

    /// <summary>
    /// <b>校验基准</b>的静态 VA（imagebase <c>0x180000000</c>）。
    /// 从该地址开始读 <see cref="OriginalBytes"/>.Length 字节交给 <see cref="ValidateSite"/>。
    /// </summary>
    protected abstract ulong HookVa { get; }

    /// <summary>
    /// <b>hook 落点</b>的静态 VA。默认等于 <see cref="HookVa"/>。
    ///
    /// <para>
    /// 【为什么需要它】有些 hook 的「校验单元」与「替换单元」不是同一条指令：
    /// 例如 <c>cmp dword [reg+0x14], 2</c>（4 字节）+ <c>je rel32</c>（6 字节）这种形态，
    /// 校验需要看到整个 10 字节才能确认形态正确，
    /// 而真正被替换的只有后面那条 6 字节的 <c>je</c>。
    /// </para>
    ///
    /// <para>
    /// 早期版本把两者混为一谈，导致：
    /// 基类从 <c>cmp</c> 处读字节、却去 <c>je</c> 处挂钩（或反之），
    /// 校验报出「不是 cmp …（实际 83 78 14 02）」这类错位信息。
    /// </para>
    /// </summary>
    protected virtual ulong HookSiteVa => HookVa;

    /// <summary>落点原始字节，仅供日志展示「原始值」。</summary>
    protected abstract byte[] OriginalBytes { get; }

    /// <summary>
    /// 安装前校验落点字节。返回 false 则拒绝安装。
    /// <paramref name="current"/> 是运行时的实际字节（可能已被其他补丁改写）。
    /// </summary>
    protected abstract bool ValidateSite(byte[] current, out string reason);

    /// <summary>
    /// 构造本 hook 的 stub 机器码。
    ///
    /// <para>
    /// 【为什么要把地址传进来】<paramref name="stubRip"/> 是 stub 将来<b>运行</b>的地址。
    /// 出口跳转写成 <c>mov r11,imm64</c> 时用不到它，但写成<b>可重定位</b>的
    /// <c>lea r11,[label]</c> 时 rel32 就要靠它来算。
    /// </para>
    /// <para>
    /// 所以安装顺序必须是：<b>先 <c>AllocateExecutable</c> → 再 <c>BuildStub(rip)</c> → 再拷贝</b>。
    /// 早期版本先构码再分配，那时 stub 还不知道自己会落在哪里
    /// —— 这在「所有出口都是绝对地址」时恰好能用，但本质上是个隐患。
    /// </para>
    /// </summary>
    protected abstract byte[] BuildStub(long stubRip);

    /// <summary>
    /// 出口目标的预期地址（名字 -> 运行时地址）。安装后会自动核对，不一致就报 ERROR。
    /// 返回 null 表示「本 hook 不登记该名字」。默认没有。
    /// </summary>
    protected virtual IReadOnlyDictionary<string, long> ExpectedExits => EmptyExits;

    private static readonly IReadOnlyDictionary<string, long> EmptyExits =
        new Dictionary<string, long>();


    // ---- 骨架 ----

    /// <summary>
    /// 构造 stub、分配可执行内存、并用 <see cref="NativeHook{T}"/> 挂上。
    /// 失败只记日志，绝不抛异常打断游戏启动。
    /// </summary>
    internal bool Install()
    {
        if (_installed)
        {
            return true;
        }

        try
        {
            if (NativeMemory.ModuleBase == IntPtr.Zero)
            {
                NativeMemory.ResolveModule();
            }

            if (NativeMemory.ModuleBase == IntPtr.Zero)
            {
                Plugin.Log.Warning($"{Tag} 找不到 GameAssembly.dll 模块基址，跳过。");
                return false;
            }

            // 校验基准：通常是 HookVa（指令起点）。
            // 落点另看 HookSiteVa —— 二者可以不同（见 HookSiteVa 的注释）。
            IntPtr checkSite = NativeMemory.StaticVaToRuntime(HookVa);
            IntPtr site = NativeMemory.StaticVaToRuntime(HookSiteVa);

            if (!NativeMemory.TryReadBytes(checkSite, OriginalBytes.Length, out byte[] current))
            {
                Plugin.Log.Warning($"{Tag} 无法读取 0x{checkSite.ToInt64():x} 的字节，跳过。");
                return false;
            }

            if (!ValidateSite(current, out string reason))
            {
                Plugin.Log.Warning(
                    $"{Tag} 0x{checkSite.ToInt64():x} 落点校验失败（落点 0x{site.ToInt64():x}），拒绝安装：{reason}" +
                    $"（实际：{Hex(current)}，原始：{Hex(OriginalBytes)}）");
                return false;
            }

            // ⚠️ 这里**不再**用 BytesEqual 去报告「字节被其他补丁改写」。
            //
            // 原因：子类的 OriginalBytes 里**带可变字段**（如 rel32 位移）时，
            // 只能写占位值（0），于是与真实字节永远不等 ——
            // 每次启动都会刷一条唬人的 WARNING，而实际什么都没被改写。
            // 本项目实测：高亮 hook 的 10 字节形态里 rel32 两处不同，
            // 每次都误报「已被其他补丁改写」。
            //
            // 形态正确性已经由子类的 ValidateSite 负责（它按字段分解校验），
            // 这里就不再做一个必然会误报的宇节级比较。
            //
            // 保留能力：子类若确实想核对固定字节，可在自己的 ValidateSite 里做。

            // ★ 顺序有讲究：**先分配地址，再构码**。
            //
            // 出口如果用可重定位形态（lea r11,[label]），rel32 必须知道
            // stub 自己会落在哪里 —— 所以 BuildStub 需要拿到 rip。
            // 早期版本先构码再分配，那时 stub 还不知道自己的地址，
            // 只能用绝对地址出口（恰好能用，但本质上是个隐患）。
            //
            // 先分配再构码后，二者都能用；而宽裕预留也让
            // 「构建出来的码比预估长」不会变成踩坏邻居内存。
            _stub = NativeMemory.AllocateExecutable(AllocatedStubLength);

            if (_stub == IntPtr.Zero)
            {
                Plugin.Log.Warning($"{Tag} 分配可执行内存失败，跳过。");
                return false;
            }

            byte[] code = BuildStub(_stub.ToInt64());

            if (code.Length == 0)
            {
                Plugin.Log.Warning($"{Tag} stub 构码失败（汇编器未产出机器码），拒绝安装。");
                return false;
            }

            if (code.Length > AllocatedStubLength)
            {
                Plugin.Log.Warning(
                    $"{Tag} stub 码长 {code.Length} 超过预留的 {AllocatedStubLength} 字节，拒绝安装。");
                return false;
            }

            // 【为什么要核验出口】这一步是汇编器带来的**免费自检**：
            //
            // 手写时代，出口是否正确只能靠人肉核对 rel8 数值 —— 算错不会报错，
            // 只会变成玄学现象（本项目两次：格子不亮 / AI 站到玩家头上）。
            // 现在标签的最终地址由汇编器算，我们只需把它与「静态 VA 换算出的
            // 运行时目标」比一下：不等就说明 stub 里某条跳转指向了别处。
            VerifyExits(code);

            Marshal.Copy(code, 0, _stub, code.Length);

            _hook = new NativeHook<DetourSignature>(site, _stub);
            _hook.Attach();

            if (!_hook.IsHooked)
            {
                Plugin.Log.Warning(
                    $"{Tag} NativeHook.Attach() 返回后 IsHooked=false（落点 0x{site.ToInt64():x}），拒绝。");
                return false;
            }

            _installed = true;

            // 回读确认 hook 生效：Dobby 会在 site 处写入一条跳转。
            string siteNow = NativeMemory.HexDump(site, 8);

            Plugin.LogInfo(() =>
                $"{Tag} 已挂上 0x{HookVa:x}（运行时 0x{site.ToInt64():x}）→ stub 0x{_stub.ToInt64():x}，" +
                $"跳板 0x{_hook.TrampolineHandle.ToInt64():x}。落点现状：{siteNow}");

            return true;
        }
        catch (Exception e)
        {
            Plugin.Log.Error($"{Tag} 安装异常：{e}");
            return false;
        }
    }

    /// <summary>摘掉 detour 并释放引用。</summary>
    internal void Uninstall()
    {
        if (!_installed)
        {
            return;
        }

        try
        {
            if (_hook is { IsHooked: true })
            {
                // HookDetach() 内部走 BootstrapInterop.NativeHookDetach，
                // 并自行清空 _trampoline / _trampolineHandle（字段直赋，不走会抛异常的 setter）。
                _hook.Detach();

                Plugin.LogInfo(() =>
                    $"{Tag} 已摘除（IsHooked={_hook.IsHooked}，落点 0x{_hook.Target.ToInt64():x}）。");
                _detached = true;
            }
            else
            {
                // 不谎报成功：状态必须与现实一致。
                Plugin.Log.Warning($"{Tag} 摘除被跳过：hook 不存在或未挂上。");
            }
        }
        catch (Exception e)
        {
            Plugin.Log.Warning($"{Tag} 摘除异常：{e.Message}");
        }
        finally
        {
            _installed = false;
            _hook = null;
        }
    }

    // ---- 共用工具 ----

    /// <summary>把静态 VA 换算成**运行时**地址。stub 的出口必须是运行时地址。</summary>
    protected static long RuntimeVa(ulong staticVa)
    {
        return (long)(ulong)NativeMemory.StaticVaToRuntime(staticVa);
    }

    /// <summary>
    /// 安装后核对 stub 里每条出口跳转的实际目标。
    ///
    /// <para>
    /// 【为什么值得做】本项目在出口上错过<b>两次</b>，而两次都<b>不报错</b>：
    /// 一次是格子不亮，一次是 AI 站到玩家头上（跳过了 <c>AroundGridHaveEnemy</c>）。
    /// 共同点是「跳转确实执行了，只是落到了别处」—— 手写汇编时代这只能靠人肉核对。
    /// </para>
    /// <para>
    /// 现在出口地址由汇编器算，于是可以拿它与「静态 VA 换算出的运行时目标」对账。
    /// 不一致就报 ERROR，把「玄学现象」提前成「安装日志里的一行」。
    /// </para>
    /// <para>
    /// 只报错、不拒绝安装：出口对不上时功能一定不对，但<b>直接摘掉 hook 会让整个特性失效</b>，
    /// 用户反而看不到问题。保留安装 + 大字号 ERROR 更利于定位。
    /// </para>
    /// </summary>
    private void VerifyExits(byte[] code)
    {
        if (_stub == IntPtr.Zero || ExpectedExits.Count == 0)
        {
            return;
        }

        // 出口形态固定为 `mov r11, imm64` (49 BB + 8 字节) —— 立即数就是目标地址。
        // 逐个扫出来，与子类声明的预期集合比对：既查「值对不对」，也查「数量对不对」。
        var found = new List<(int At, long Target)>();

        for (int i = 0; i + 9 < code.Length; i++)
        {
            if (code[i] == 0x49 && code[i + 1] == 0xBB)
            {
                found.Add((i, BitConverter.ToInt64(code, i + 2)));
            }
        }

        if (found.Count == 0)
        {
            Plugin.Log.Error(
                $"{Tag} stub 里**一条出口跳转都没找到**（码长 {code.Length}）。功能必然不生效，请检查 BuildStub。");
            return;
        }

        foreach ((int at, long target) in found)
        {
            if (!IsExpectedExit(target))
            {
                Plugin.Log.Error(
                    $"{Tag} stub 自检失败：+{at:x2} 处的出口指向 0x{target:x}，" +
                    $"不在声明的出口集合里（{DescribeExpectedExits()}）。跳转会落到错误位置。");
            }
        }

        foreach (var kv in ExpectedExits)
        {
            bool hit = false;

            foreach ((int _, long target) in found)
            {
                if (target == kv.Value)
                {
                    hit = true;
                    break;
                }
            }

            if (!hit)
            {
                Plugin.Log.Error(
                    $"{Tag} stub 自检失败：声明的出口 {kv.Key}=0x{kv.Value:x} 在 stub 里**没有出现**。");
            }
        }
    }

    private bool IsExpectedExit(long target)
    {
        foreach (var kv in ExpectedExits)
        {
            if (kv.Value == target)
            {
                return true;
            }
        }

        return false;
    }

    private string DescribeExpectedExits()
    {
        var parts = new List<string>();

        foreach (var kv in ExpectedExits)
        {
            parts.Add($"{kv.Key}=0x{kv.Value:x}");
        }

        return string.Join(" ", parts);
    }

    /// <summary>
    /// 把 stub 的机器码反汇编后打进日志（诊断用）。
    /// </summary>
    protected void LogStubDisassembly(byte[] code)
    {
        Plugin.LogInfo(() =>$"{Tag} stub（{code.Length} 字节）@0x{_stub.ToInt64():x}：\n" +
                       StubAssembler.Describe(code, _stub.ToInt64()));
    }

    /// <summary>
    /// 判断字节序列是否为一条 6 字节的 <c>jcc rel32</c>（<c>0F 8x</c>）。
    ///
    /// <para>
    /// 安装校验**不能**要求字节与出厂原值完全一致：同址的其他原地改写探针
    /// 会改掉跳转目标<em>位移</em>。只要它仍是一条 6 字节 rel32 jcc，
    /// Dobby 的搬迁就是安全的（这类指令落「原样复制」分支）。
    /// </para>
    /// </summary>
    protected static bool IsRel32Jcc(byte[] bytes)
    {
        // 0F 80..0F 8F = jcc rel32
        return bytes.Length >= 2 && bytes[0] == 0x0F && bytes[1] >= 0x80 && bytes[1] <= 0x8F;
    }

    protected static string Hex(byte[] bytes)
    {
        var sb = new System.Text.StringBuilder(bytes.Length * 3);

        for (int i = 0; i < bytes.Length; i++)
        {
            if (i > 0)
            {
                sb.Append(' ');
            }

            sb.Append(bytes[i].ToString("x2"));
        }

        return sb.ToString();
    }
}
