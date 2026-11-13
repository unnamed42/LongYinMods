---
name: mod-recompile
description: 当手上只有第三方 mod 的 DLL、没有源码，却要改它的行为（修 bug、加日志、关功能）时使用。给出 ilspycmd 全限定反编译 + 补引用 + 构建 + 产物验证的完整流程，含 -m 仲裁类型归属与 ilspycmd 已知缺陷。
whenToUse: 要修改 gamedir/Mods 下别人的 mod、重建它的源码工程、或排查它的报错时。
---

# 反编译第三方 mod 并重编译（无源码）

适用：只有别人的 mod DLL，没有源码。本文是通用流程；**游戏源码本身的取证**用 `il2cpp-decompile`。

**完成判据**：`dotnet build` 通过、产物里 `MelonInfo` 特性仍在、类型数量与体积量级和原 DLL 一致。

## 0. 一条命令

```bash
# 从仓库根执行。★ -ds 与绝对路径 -r 缺一不可
ilspycmd -o output/<mod>_proj -p -ds "UsingDeclarations=false" \
  -r "$PWD/gamedir/MelonLoader/Il2CppAssemblies" \
  -r "$PWD/gamedir/MelonLoader/net6" \
  gamedir/Mods/<Mod>.dll
```

**核心思路：让反编译器输出「全限定类型名」，从根上消除 `using` 歧义。**

## 1. 为什么必须 `UsingDeclarations=false`

IL2CPP mod 会同时 `using System;` 和 `using Il2CppSystem;`，再裸写 `IntPtr` / `Object` / `List<>`。
原始 IL 没问题，反编译成 C# 就是 `CS0104`（`System.IntPtr` 与 `Il2CppSystem.IntPtr` 都可见）。

> ⚠️ **不要用「保留 using + 全局别名」消歧义** —— 需要逐个名字人工判断，而且**判断错了照样编译通过**
> （静默改变语义）。实测把 `_heldTabs` 从 `System.Collections.Generic.List` 误钉成 Il2Cpp 版，编译成功但类型变了。
> **全限定输出的价值就是不需要判断。**

## 2. ⚠️ 必须同时给 `-r`，否则等于没做

只加 `-ds` 会得到 **274 个 `CS0246`**。原因是 **`System.*` 被正常限定了**，看起来「全限定生效」，
但**游戏 / Harmony / MelonLoader 的命名空间被整个丢掉** —— 反编译器解析不到那些程序集。

> 💡 **诊断技巧**：`System.*` 都限定了、游戏类型全丢 ⇒ 缺 `-r`；
> **不是** csproj 缺 HintPath（那是另一个独立问题，症状相同、成因不同）。

## 3. 编译前三处必改

### 3.1 HintPath

- `-r` 传**相对路径** → 生成的 HintPath 相对 CWD，而 csproj 按项目目录解析 → 一堆 `CS0246`。
- `-r` 传**绝对路径**（`$PWD/...`）→ ilspycmd 会算出正确的相对路径，**开箱即用**。

> ✅ 推荐传绝对路径。仍要手改时，套用 `AGENTS.md` §3.3 的标准引用块，并补 `<PlatformTarget>x64</PlatformTarget>`。

### 3.2 `Properties/AssemblyInfo.cs` 补 using

全限定输出下**只有这个文件会挂**（`[assembly: ...]` 在顶层拿不到限定）：

```csharp
using MelonLoader;
using UnityEngine;
[assembly: MelonInfo(typeof(...), "名字", "0.0.2", "作者", null)]
[assembly: MelonGame("TppStudio", "LongYinLiZhiZhuan")]
```

验证特性真进了二进制：

```bash
python3 -c "d=open('bin/Release/net6.0/M.dll','rb').read(); print('名字' in d.decode('utf-8','ignore'))"
```

### 3.3 `in` → `ref`（ilspycmd 已知缺陷）

`Volatile.Read(ref x)` / `Interlocked.Read(ref x)` 会被反成 `in`，报 `CS1620`：

```bash
sed -i -E 's/(Volatile|Interlocked)\.Read\(in ([A-Za-z_][A-Za-z0-9_]*)\)/\1.Read(ref \2)/g' \
  $(grep -rl "Read(in " --include=*.cs .)
```

> 遇到 `CS1620` 一律先怀疑这里。

## 4. 验证产物（别只看「生成成功」）

```bash
monodis --typedef bin/Release/net6.0/<Mod>.dll | grep -c <Namespace>   # 类型齐全
python3 -c "d=open('bin/Release/net6.0/<Mod>.dll','rb').read(); print('<显示名>'.encode() in d)"  # MelonInfo
ls -la bin/Release/net6.0/<Mod>.dll gamedir/Mods/<Mod>.dll            # 体积量级
```

> ⚠️ **「构建成功」≠「产物正确」**：对应坑是「**编译通过但语义变了**」。

## 5. ★ 仲裁类型归属：`-m` 单成员模式

对某类型归属不确定时，用 `-m` 反编译**单个成员** —— 它只列该成员**真正需要的** using：

```bash
ilspycmd -m "F:WuMingPerformance.Patches.InfoFloodControlPatch._heldTabs" gamedir/Mods/WuMingPerformance.dll
```

```
using System.Collections.Generic;
using Il2Cpp;
private static readonly List<InfoTabData> _heldTabs;     ← 没有 Il2CppSystem.Collections.Generic
```

**结论：这是 `System.Collections.Generic.List`。** 整类型输出会同时列两个 `using`，根本分不出。

`-m` 接受 XML doc id 或 metadata token：`M:Ns.Type.Method(System.Int32)`、`0x06000005`。

## 6. 用脚本（已固化）

```bash
tools/recompile_mod.sh WuMingPerformance                 # 自动去 gamedir/Mods/ 找
tools/recompile_mod.sh WuMingPerformance --out output/foo
tools/recompile_mod.sh WuMingPerformance --keep-going     # 构建失败也跑完，便于诊断
tools/recompile_mod.sh WuMingPerformance --deploy         # 装到 gamedir/Mods/（需提权，核对 md5）
```

脚本步骤：`ilspycmd -p -ds` + 绝对路径 `-r` → 补 `AssemblyInfo.cs` using → **补全引用集** →
`Read(in x)`→`ref` → `dotnet build -c Release` → 验证体积与 `MelonInfo`。

两个手敲容易漏的坑：

1. **必须补全引用集**：ilspycmd 只写它直接观察到的引用，实际报 `CS0012`（`Il2CppObjectBase` 在未引用程序集中），
   **看不见任何指向 csproj 的线索**。补上 `AGENTS.md` §3.3 那套标准引用后全部通过。
2. **产物名 ≠ 文件名**：产物按 csproj 的 `<AssemblyName>` 命名。实测 `RefreshCraft.dll` 的 AssemblyName
   是 `LongYinLiZhiZhuanMelonLoader` —— 按 `$MOD.dll` 找会把构建成功误报成失败。脚本从 csproj 读真实 AssemblyName。

实测覆盖：28 个 mod 中 24 个一次通过；4 个失败是 ilspycmd 自身 bug（见 §8），不是流程问题。

## 7. 改代码前必须知道

| 事项 | 说明 |
|---|---|
| 语义等价性 | 反编译「忠实但不保证同形」；改前先用 `-m` 核实关键类型归属 |
| 别动类型归属 | 改了**照样编译通过**，但行为变了 |
| `Nullable` | 产物可能写 `disable`；别引入空引用 |
| 必须冷启动验证 | 新逻辑的验证不能靠热重载 |
| 部署核对 md5 与体积 | 见 `AGENTS.md` §3.2 |

## 8. 已知限制：ilspycmd 自身的 bug

24/28 一次通过。剩下 4 个是反编译产物**错误的或非法的 C#**，改 csproj 无用：

- **类型弄错**（`CS0029`/`CS0030`）：把游戏字段真实类型反成另一个（如 `Dictionary` 反成 `List`）。
  裁决用 `-m` 或直接 `monodis --property gamedir/MelonLoader/Il2CppAssemblies/Assembly-CSharp.dll`。
- **非法语法**（`CS1525`/`CS1003`）：个别 IL 模式反编译缺陷，与选项无关。

碰到时：个别文件手工修（先确认真实类型）；整个类型不对用 `-t <type>` 单独反；大量错误说明这 mod 不适合该流水线。

> ⚠️ **不要「为了让编译通过」乱改类型归属** —— 改错了照样编译通过，但语义变了。

## 收尾

- 细节与实例见 [`docs/mod-recompilation.md`](../../../docs/mod-recompilation.md)；
  具体 mod 的修复记录见 [`docs/forceoverflowdividend.md`](../../../docs/forceoverflowdividend.md)。
- 工具在 `tools/recompile_mod.sh`。
