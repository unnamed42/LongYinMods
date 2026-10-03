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

    /// <summary>构造本 hook 的 stub 机器码。出口地址用 <see cref="RuntimeVa"/> 换算。</summary>
    protected abstract byte[] BuildStub();

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

            if (!BytesEqual(current, OriginalBytes))
            {
                Plugin.Log.Warning(
                    $"{Tag} 注意：0x{site.ToInt64():x} 处字节已被其他补丁改写" +
                    $"（实际：{Hex(current)}，原始：{Hex(OriginalBytes)}），仍按当前形态安装。");
            }

            byte[] code = BuildStub();

            _stub = NativeMemory.AllocateExecutable(code.Length);

            if (_stub == IntPtr.Zero)
            {
                Plugin.Log.Warning($"{Tag} 分配可执行内存失败，跳过。");
                return false;
            }

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

            Plugin.Log.Msg(
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

                Plugin.Log.Msg(
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
    /// 发射 <c>mov rax, imm64</c> + <c>jmp rax</c>（12 字节，绝对跳转）。
    ///
    /// <para>
    /// ⚠️ <b>会覆盖 rax</b>。仅当目标处**不依赖 rax 原值**时可用。
    /// </para>
    ///
    /// <para>
    /// 🚫 <b>本项目现在不再使用它</b>：两个 hook 的出口都已改为「原版代码的落点」
    /// （<c>0x180a8d8bc</c> / <c>0x180a8d92f</c> / <c>0x180a8da6b</c>），
    /// 而原版代码**必须**保留 <c>rax</c>。新写 stub 请一律用 <see cref="EmitJumpViaR11"/>。
    /// 保留此方法仅为将来「跳到自己的跳转目标」时使用。
    /// </para>
    /// </summary>
    protected static void EmitAbsoluteJump(List<byte> buffer, long target)
    {
        buffer.AddRange(new byte[] { 0x48, 0xB8 });      // mov rax, imm64
        buffer.AddRange(BitConverter.GetBytes(target));
        buffer.AddRange(new byte[] { 0xFF, 0xE0 });      // jmp rax
    }

    /// <summary>
    /// 发射 <c>mov r11, imm64</c> + <c>jmp r11</c>（12 字节，绝对跳转），
    /// **不破坏 rax**。
    ///
    /// <para>
    /// 【为什么需要它】本项目在此处真实崩过：用 rax 中转跳到 <c>0x180a8d8bc</c> 后，
    /// 该处第一条指令是 <c>mov r9,[rax]</c> —— 它期望 <c>rax</c> 还是上一条 <c>cmp</c>
    /// 留下的邻格指针，但 <c>mov rax,imm64</c> 已把它改成了代码地址，
    /// 于是把代码字节当类指针解引用 → SIGSEGV（gdb 现场：<c>rip=0x180a8d8ca</c>，
    /// <c>rax=0x180a8d8bc</c>）。
    /// </para>
    ///
    /// <para>
    /// <c>r11</c> 是 Win64 的易失寄存器，本 mod 的跳转目标
    /// （<c>0x180a8d8bc</c> / <c>0x180a8d92f</c> / <c>0x180a8da6b</c>）后续代码均不读它
    /// （已逐条核实）。
    /// </summary>
    protected static void EmitJumpViaR11(List<byte> buffer, long target)
    {
        buffer.AddRange(new byte[] { 0x49, 0xBB });      // mov r11, imm64
        buffer.AddRange(BitConverter.GetBytes(target));
        buffer.AddRange(new byte[] { 0x41, 0xFF, 0xE3 }); // jmp r11
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

    protected static bool BytesEqual(byte[] a, byte[] b)
    {
        if (a.Length != b.Length)
        {
            return false;
        }

        for (int i = 0; i < a.Length; i++)
        {
            if (a[i] != b[i])
            {
                return false;
            }
        }

        return true;
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
