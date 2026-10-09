using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Unnamed42.FriendlyNoclip;

namespace NativeScanHarness;

/// <summary>
/// 离线验证 <see cref="NavigateScanner"/> / <see cref="StubFactory"/>：输入是**真实的
/// <c>GameAssembly.dll</c>**（按 PE 节表把文件偏移还原成 VA），源码直接引用 mod 里的那几份
/// （见 csproj 的 <c>Compile Include</c>），所以测的就是将要上机运行的那段代码。
///
/// <para>
/// 【什么时候跑】游戏更新后**第一件事**：它回答「扫描器在新构建上还找得到那两个落点吗、
/// 两个 stub 还编得对吗」。找不到时它会告诉你**差在哪一步**，而不是等游戏里 detour 静默不装。
/// </para>
///
/// <para>
/// 【判据设计】断言分成两类：
/// <list type="bullet">
///   <item><b>不变式</b>（必须过）：形态唯一、两条分支的 skip 一致、stub 长度与出口位置、
///         出口立即数等于扫描结果 —— 这些**跨构建**都成立。</item>
///   <item><b>构建指纹</b>（只提示）：与「2026-10-09 那次实测」记录的地址/偏移对照。
///         不一致**不算失败**（游戏就是更新了），但会提示把新值写进
///         <c>docs/friendlynoclip.md</c>。</item>
/// </list>
/// </para>
/// </summary>
internal static class Program
{
    private const ulong ImageBase = 0x180000000UL;

    /// <summary>2026-10-09 构建（GameAssembly.dll md5=ac0ec1a3…）实测值，仅用于提示。</summary>
    private static readonly (string What, ulong Want)[] KnownBuild =
    {
        ("城防 site", 0x180a52e56UL),
        ("城防 skip", 0x180a5300bUL),
        ("城防 pass", 0x180a52e5cUL),
        ("穿友方 site", 0x180a52ec9UL),
        ("穿友方 skip", 0x180a5300bUL),
        ("穿友方 pass", 0x180a52ecfUL),
        ("函数入口", 0x180a52c2eUL),
    };

    private static int Main(string[] args)
    {
        string path = args.Length > 0 ? args[0] : "../../gamedir/GameAssembly.dll";
        ulong probe = args.Length > 1 ? Convert.ToUInt64(args[1], 16) : 0x180a52ec9UL;

        byte[] image = File.ReadAllBytes(path);
        List<Section> sections = ReadSections(image);
        List<(uint Begin, uint End)> pdata = ReadPdata(image, sections);

        (ulong start, ulong end) = FindFunction(pdata, probe);
        Console.WriteLine($"{path}");
        Console.WriteLine($"入口 0x{probe:x} → 函数 [0x{start:x}, 0x{end:x})  共 {end - start} 字节（.pdata）");

        int offset = VaToOffset(sections, start);
        byte[] code = image.Skip(offset).Take((int)(end - start)).ToArray();

        if (!NavigateScanner.TryScan(code, start, out NavigateScanResult? result, out string reason))
        {
            Console.WriteLine($"✗ 扫描失败：{reason}");
            return 1;
        }

        NavigateScanResult r = result!;
        Console.WriteLine($"解码 {r.InstructionCount} 条指令");
        Console.WriteLine($"城防   : site=0x{r.Wall!.Site:x} skip=0x{r.Wall.Skip:x} pass=0x{r.Wall.Pass:x} " +
                          $"op={r.Wall.Op0:x2} {r.Wall.Op1:x2} gridTypeDisp=+0x{r.GridTypeOffset:x}");
        Console.WriteLine($"穿友方 : site=0x{r.Friendly!.Site:x} skip=0x{r.Friendly.Skip:x} pass=0x{r.Friendly.Pass:x} " +
                          $"op={r.Friendly.Op0:x2} {r.Friendly.Op1:x2} battleUnitDisp=+0x{r.BattleUnitOffset:x}");
        Console.WriteLine($"selfTeamID 栈槽 = [rsp+0x{r.SelfTeamIdStackOffset:x}]；" +
                          $"get_IsAlive 调用目标 = 0x{r.IsAliveCall:x}");

        bool ok = true;

        // ---- 不变式（跨构建必须成立）----
        ok &= Check("城防落点是 je（0F 84）", r.Wall.Op0 == 0x0F && r.Wall.Op1 == 0x84);
        ok &= Check("穿友方落点是 jne（0F 85）", r.Friendly.Op0 == 0x0F && r.Friendly.Op1 == 0x85);
        ok &= Check("两条分支的 skip 指向同一个 continue 块", r.SameSkipTarget);
        ok &= Check("pass = site + 6", r.Wall.Pass == r.Wall.Site + 6 && r.Friendly.Pass == r.Friendly.Site + 6);
        ok &= Check("两个落点都在函数体内",
            r.Wall.Site >= start && r.Wall.Site < end && r.Friendly.Site >= start && r.Friendly.Site < end);

        Console.WriteLine();
        ok &= VerifyStubs(r);

        // ---- 构建指纹（只提示）----
        Console.WriteLine();
        Console.WriteLine("—— 与 2026-10-09 构建的实测值对照（不一致 = 游戏已更新，不算失败）——");
        bool sameBuild = true;

        foreach ((string what, ulong want) in KnownBuild)
        {
            ulong got = what switch
            {
                "城防 site" => r.Wall.Site,
                "城防 skip" => r.Wall.Skip,
                "城防 pass" => r.Wall.Pass,
                "穿友方 site" => r.Friendly.Site,
                "穿友方 skip" => r.Friendly.Skip,
                "穿友方 pass" => r.Friendly.Pass,
                _ => start,
            };

            sameBuild &= got == want;
            Console.WriteLine($"{(got == want ? "=" : "≠")} {what}: 0x{got:x}（2026-10-09 为 0x{want:x}）");
        }

        if (!sameBuild)
        {
            Console.WriteLine("ℹ️ 构建已变 —— 请把上面这组新地址/偏移写进 docs/friendlynoclip.md（§ 原生 detour 定位）。");
        }

        Console.WriteLine();
        Console.WriteLine(ok ? "== 不变式全部通过 ==" : "== 有不变式失败 ==");
        return ok ? 0 : 1;
    }

    private static bool Check(string what, bool hit)
    {
        Console.WriteLine($"{(hit ? "✓" : "✗")} {what}");
        return hit;
    }

    /// <summary>把两个 stub 真编出来，核对长度 / 出口位置 / 出口立即数。</summary>
    private static bool VerifyStubs(NavigateScanResult r)
    {
        // 真实运行时 stub 落在 Dobby 分配的近地址；这里给一个任意 64 位地址即可
        // （两个出口都是 mov r11,imm64，不受 stub 自身地址影响）。
        const long stubRip = 0x7f12_3456_0000L;

        StubAssembler.Report = m => Console.WriteLine($"[iced] {m}");

        byte[] friendly = StubFactory.BuildFriendly(
            "[harness]", stubRip, r.BattleUnitOffset, 0x58, 0x10, r.SelfTeamIdStackOffset,
            skip: (long)r.Friendly!.Skip, pass: (long)r.Friendly.Pass);

        byte[] wall = StubFactory.BuildWall(
            "[harness]", stubRip, r.GridTypeOffset, 0x30, 0x10, 0x2C, r.SelfTeamIdStackOffset,
            gridTypeObstacle: 2, obstacleTypeWall: 1,
            skip: (long)r.Wall!.Skip, pass: (long)r.Wall.Pass);

        bool ok = true;

        // 长度与出口位置：与文档 §「stub 布局」逐字节核对过的那张表一致。
        ok &= CheckStub("穿友方", friendly, 60, new (int At, ulong Target)[] { (0x22, r.Friendly.Skip), (0x2F, r.Friendly.Pass) });
        ok &= CheckStub("城防", wall, 59, new (int At, ulong Target)[] { (0x21, r.Wall.Pass), (0x2E, r.Wall.Skip) });

        Console.WriteLine();
        Console.WriteLine("穿友方 stub 反汇编：");
        Console.WriteLine(StubAssembler.Describe(friendly, stubRip));
        Console.WriteLine("城防 stub 反汇编：");
        Console.WriteLine(StubAssembler.Describe(wall, stubRip));

        return ok;
    }

    private static bool CheckStub(string name, byte[] code, int expectLength, (int At, ulong Target)[] exits)
    {
        bool ok = code.Length == expectLength;
        Console.WriteLine($"{(ok ? "✓" : "✗")} {name} stub 长度 {code.Length}（期望 {expectLength}）");

        foreach ((int at, ulong target) in exits)
        {
            // 出口形态固定为 `mov r11, imm64` = 49 BB + 8 字节立即数
            bool shape = at + 9 < code.Length && code[at] == 0x49 && code[at + 1] == 0xBB;
            ulong immediate = shape ? BitConverter.ToUInt64(code, at + 2) : 0;
            bool hit = shape && immediate == target;

            ok &= hit;
            Console.WriteLine($"{(hit ? "✓" : "✗")} {name} 出口 +0x{at:x}: 立即数 0x{immediate:x}（期望 0x{target:x}）" +
                              $"{(shape ? string.Empty : "（该处不是 49 BB mov r11,imm64）")}");
        }

        return ok;
    }

    private sealed record Section(string Name, uint VirtualAddress, uint VirtualSize, uint RawPointer, uint RawSize);

    private static List<Section> ReadSections(byte[] image)
    {
        int pe = BitConverter.ToInt32(image, 0x3C);
        int sectionCount = BitConverter.ToInt16(image, pe + 6);
        int optionalSize = BitConverter.ToInt16(image, pe + 20);
        int at = pe + 24 + optionalSize;
        var list = new List<Section>();

        for (int i = 0; i < sectionCount; i++, at += 40)
        {
            string name = System.Text.Encoding.ASCII.GetString(image, at, 8).TrimEnd('\0');
            list.Add(new Section(
                name,
                BitConverter.ToUInt32(image, at + 12),
                BitConverter.ToUInt32(image, at + 8),
                BitConverter.ToUInt32(image, at + 20),
                BitConverter.ToUInt32(image, at + 16)));
        }

        return list;
    }

    private static List<(uint Begin, uint End)> ReadPdata(byte[] image, List<Section> sections)
    {
        Section pdata = sections.First(s => s.Name == ".pdata");
        var list = new List<(uint, uint)>();

        for (uint at = 0; at + 12 <= pdata.VirtualSize; at += 12)
        {
            int off = (int)(pdata.RawPointer + at);
            uint begin = BitConverter.ToUInt32(image, off);
            uint end = BitConverter.ToUInt32(image, off + 4);

            if (begin != 0)
            {
                list.Add((begin, end));
            }
        }

        list.Sort();
        return list;
    }

    private static (ulong, ulong) FindFunction(List<(uint Begin, uint End)> pdata, ulong va)
    {
        uint key = (uint)(va - ImageBase);
        int lo = 0;
        int hi = pdata.Count - 1;

        while (lo <= hi)
        {
            int mid = lo + ((hi - lo) / 2);
            (uint begin, uint end) = pdata[mid];

            if (key < begin)
            {
                hi = mid - 1;
            }
            else if (key >= end)
            {
                lo = mid + 1;
            }
            else
            {
                return (ImageBase + begin, ImageBase + end);
            }
        }

        throw new InvalidOperationException($"0x{va:x} 不在任何 .pdata 函数里");
    }

    private static int VaToOffset(List<Section> sections, ulong va)
    {
        uint rva = (uint)(va - ImageBase);

        foreach (Section s in sections)
        {
            if (rva >= s.VirtualAddress && rva < s.VirtualAddress + s.VirtualSize)
            {
                return (int)(s.RawPointer + (rva - s.VirtualAddress));
            }
        }

        throw new InvalidOperationException($"VA 0x{va:x} 不在任何节里");
    }
}
