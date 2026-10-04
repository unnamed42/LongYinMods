# 反编译第三方 mod 并重新编译（无源码）

> 归属：[`AGENTS.md`](../AGENTS.md) §7 的专题展开。
> 适用场景：**手上只有别人的 mod DLL，没有源码**，但需要改它的行为（改 bug、加日志、关掉某个功能）。
> 本文是**通用流程**（工具 / 命令 / 踩坑 / 为什么那样做），**不随游戏构建变**。
>
> 本项目已完成的两个实例（具体改了什么，**会随游戏更新失效**）：
> - [`forceoverflowdividend.md`](forceoverflowdividend.md) —— 修签名漂移导致的 `MissingMethodException`
> - `WuMingPerformance`（龙胤性能优化 v0.0.2，用于排查退出卡死）
>
> 相关文档：[`decompilation.md`](decompilation.md)（看懂**游戏**的原生代码）。

---

## 0. 一句话结论

```bash
# 从仓库根执行。★ -r 是关键，-ds 是关键，两者缺一不可
ilspycmd -o output/<mod>_proj -p -ds "UsingDeclarations=false" \
  -r "$PWD/gamedir/MelonLoader/Il2CppAssemblies" \
  -r "$PWD/gamedir/MelonLoader/net6" \
  gamedir/Mods/<Mod>.dll

# 然后补三个东西（见 §3），dotnet build，编译通过
```

**核心思路：让反编译器输出「全限定类型名」，从根本上消除 `using` 歧义。**

---

## 1. 为什么必须用 `UsingDeclarations=false`

IL2CPP 的 mod 会**同时** `using System;` 和 `using Il2CppSystem;`，然后裸写 `IntPtr` / `Object` / `List<>`。

- 在**原始 IL 里**这没问题：编译器早就解析完了。
- 反编译成 **C# 源码后就是歧义**（`CS0104`）：`System.IntPtr` 和 `Il2CppSystem.IntPtr` 都可见。

```
error CS0104: “IntPtr”是“Il2CppSystem.IntPtr”和“System.IntPtr”之间的不明确的引用
```

`-ds "UsingDeclarations=false"` 让 ilspycmd 输出全限定名，从根上避免这个问题：

```csharp
internal sealed class NativeLockScope : System.IDisposable
{
    private readonly System.IntPtr _obj;
    [System.Runtime.InteropServices.DllImport("GameAssembly", ...)]
```

### ⚠️ 不要用「保留 using + 全局别名」来消歧义

曾经试过：正常反编译，然后加一个 `GlobalUsings.cs` 把 `IntPtr`/`Object`/`List<>` 钉成 System 类型。
**这个方案是错的，而且错得很隐蔽：**

| 问题 | 说明 |
|---|---|
| 需要**逐个名字人工判断** | `Random` 在这份代码里必须解析成 `Il2CppSystem.Random`（`GlobalData.RandomRange(..., (Random)null)`），`Object` 在某些文件是 `System`、某些是 `Il2CppSystem`、某些是 `UnityEngine.Object` |
| **判断错了照样编译通过** | 属于「静默改变语义」——本项目反复强调要避免的那类问题 |
| 实测踩坑 | 把 `_heldTabs` 从 `System.Collections.Generic.List` 误钉成 `Il2CppSystem.Collections.Generic.List`，**编译成功但类型变了** |

**全限定输出的价值就在于：不需要判断。** 反编译器已经替你解析好了。

---

## 2. ⚠️ 必须同时给 `-r` 参考程序集，否则等于没做

只加 `-ds "UsingDeclarations=false"` 会得到 **274 个 `CS0246`（找不到类型）**：

```
56 × HarmonyPatchAttribute    56 × GameController
 8 × GameDataController        6 × InfoController
 6 × HeroData                  6 × GameSaveData
 6 × BigMapController          ...
```

原因很容易误判：**`System.*` 被正常限定了**（`System.IntPtr`、`System.Threading.Volatile`），
所以看起来「全限定是生效的」。但实际上 **游戏 / Harmony / MelonLoader 的命名空间被整个丢掉了** ——
因为反编译器**解析不到那些程序集**，于是既不敢限定、也无法保留 `using`。

```
56 × HarmonyLib.HarmonyPatch          ← 加了 -r 之后
typeof(Il2Cpp.GameController)          ← 加了 -r 之后
```

> 💡 **诊断技巧**：如果 `System.*` 都限定了、但游戏类型全丢，那一定是缺 `-r`，
> **不是** csproj 缺 HintPath（见 §3.1，那是另一个独立问题）。
> 两者症状都是「找不到类型」，但**成因不同**，别混。

---

## 3. 编译前三处必改

生成出来的工程**不保证能编译**。三处固定问题：

### 3.1 csproj 的 HintPath（视 `-r` 传法而定）

- **传 `-r` 相对路径** → ilspycmd 会把 HintPath 写成**相对 CWD** 的路径。
  例如从仓库根传 `gamedir/MelonLoader/...`，生成 `output/<mod>_proj/` 里的 csproj 会写
  `gamedir/MelonLoader/...` —— 但那是相对 **项目目录** 解析的，**实际不存在**。
  → 报一堆 `CS0246`，但 `ls` 一下 `输出目录/gamedir` 就知道是路径错，不是类型错。
- **传 `-r` 绝对路径**（如上 §0 的 `$PWD/...`）→ ilspycmd 会算出正确的相对路径
  （`../../gamedir/MelonLoader/net6/MelonLoader.dll`），**开箱即用**。

> ✅ **推荐传绝对路径**，省掉这一步。

若仍需手改，可套用本项目的标准引用块（见 [`AGENTS.md`](../AGENTS.md) §3.3 的引用清单）：

```xml
<MelonDir>..\..\gamedir\MelonLoader\net6</MelonDir>
<UnityPath>..\..\gamedir\MelonLoader\Il2CppAssemblies</UnityPath>
...
<Reference Include="MelonLoader"><HintPath>$(MelonDir)\MelonLoader.dll</HintPath><Private>false</Private></Reference>
<Reference Include="0Harmony"><HintPath>$(MelonDir)\0Harmony.dll</HintPath><Private>false</Private></Reference>
<Reference Include="Il2CppInterop.Runtime"><HintPath>$(MelonDir)\Il2CppInterop.Runtime.dll</HintPath><Private>false</Private></Reference>
<Reference Include="Assembly-CSharp"><HintPath>$(UnityPath)\Assembly-CSharp.dll</HintPath><Private>false</Private></Reference>
<Reference Include="Il2Cppmscorlib"><HintPath>$(UnityPath)\Il2Cppmscorlib.dll</HintPath><Private>false</Private></Reference>
<Reference Include="Il2CppSystem"><HintPath>$(UnityPath)\Il2CppSystem.dll</HintPath><Private>false</Private></Reference>
<Reference Include="UnityEngine.CoreModule"><HintPath>$(UnityPath)\UnityEngine.CoreModule.dll</HintPath><Private>false</Private></Reference>
```

另外建议补 `<PlatformTarget>x64</PlatformTarget>`（理由见 [`AGENTS.md`](../AGENTS.md)：不写会变 AnyCPU/i386）。

### 3.2 `Properties/AssemblyInfo.cs` 补 using

全限定输出下，**只有这个文件会挂**，因为 `[assembly: ...]` 特性写在文件顶层、拿不到限定：

```csharp
using MelonLoader;      // ← 手工加这两行
using UnityEngine;      // ←
[assembly: MelonInfo(typeof(...), "名字", "0.0.2", "作者", null)]
[assembly: MelonGame("TppStudio", "LongYinLiZhiZhuan")]
```

不补就是 `MelonInfoAttribute` / `MelonGameAttribute` 找不到。

> ⚠️ **验证特性真的进了二进制**（这关系到 MelonLoader 能否识别它）：
> ```bash
> python3 -c "d=open('bin/Release/net6.0/M.dll','rb').read(); print('名字' in d.decode('utf-8','ignore'))"
> ```

### 3.3 `in` → `ref`（ilspycmd 的已知缺陷）

ilspycmd 会把 `Volatile.Read(ref x)` / `Interlocked.Read(ref x)` 反编译成 **`in`**，
而这两个 API 的参数是 **`ref`**：

```csharp
System.Threading.Volatile.Read(in _running)      // ✗ CS1620: 参数 1 必须与关键字"ref"一起传递
System.Threading.Volatile.Read(ref _running)     // ✓
```

一条命令修完（本项目实测 10 处）：

```bash
sed -i -E 's/(Volatile|Interlocked)\.Read\(in ([A-Za-z_][A-Za-z0-9_]*)\)/\1.Read(ref \2)/g' \
  $(grep -rl "Read(in " --include=*.cs .)
```

> 同类问题可能还有别的「`in`/`out`/`ref` 丢失」，但本项目只碰到这一种。
> 遇到 `CS1620` 一律先怀疑这里。

---

## 4. 验证产物（别只看「生成成功」）

```bash
# 1. 类型齐全（与原始 DLL 的 typedef 数量对比）
monodis --typedef bin/Release/net6.0/<Mod>.dll | grep -c <Namespace>

# 2. MelonInfo 特性仍在
python3 -c "d=open('bin/Release/net6.0/<Mod>.dll','rb').read(); print('<显示名>'.encode() in d)"

# 3. 体积量级接近原始（本项目 41,472 vs 原始 42,496）
ls -la bin/Release/net6.0/<Mod>.dll gamedir/Mods/<Mod>.dll
```

> ⚠️ 本项目的 [`ilrepack.md`](ilrepack.md) 有教训：**「构建成功」≠「产物正确」**。
> 那个坑是「合并步骤静默跳过」；这里对应的坑是「**编译通过但语义变了**」（见 §5）。

---

## 5. ★ 仲裁歧义：`-m` 单成员模式

**这是本流程最有用的一个技巧。** 改名/改代码前若对某个类型归属不确定，
用 `-m` 反编译**单个成员**——它只列出**该成员真正需要的** using：

```bash
ilspycmd -m "F:WuMingPerformance.Patches.InfoFloodControlPatch._heldTabs" gamedir/Mods/WuMingPerformance.dll
```

```
using System.Collections.Generic;
using Il2Cpp;

private static readonly List<InfoTabData> _heldTabs;     ← 没有 Il2CppSystem.Collections.Generic
```

**结论：`_heldTabs` 是 `System.Collections.Generic.List`**，不是 Il2Cpp 的。

对比一下——直接看整个类型的输出（默认模式）会同时列出 `using System.Collections.Generic;`
**和** `using Il2CppSystem.Collections.Generic;`，**根本分不出**：

```csharp
using System.Collections.Generic;
using Il2CppSystem.Collections.Generic;   // ← 两个都在，无法判断 List<> 到底是谁
```

> ✅ **`-m` 给出的是最小 using 集，相当于反编译器告诉你「这一处真正用到哪些命名空间」。**
> 任何对类型归属有疑问的地方，都可以用它来裁决，
> 而**不用**去猜、更不用去看 IL。

`-m` 接受 XML doc id 或 metadata token，例如：

```bash
ilspycmd -m "M:Ns.Type.Method(System.Int32)" file.dll
ilspycmd -m "0x06000005" file.dll
```

---

## 6. 用脚本（已固化）

上述流程已固化为 **[`tools/recompile_mod.sh`](../tools/recompile_mod.sh)**，直接跑：

```bash
# 传文件名（自动去 gamedir/Mods/ 找）或完整路径均可
tools/recompile_mod.sh WuMingPerformance

# 自定义输出目录
tools/recompile_mod.sh WuMingPerformance --out output/foo

# 构建失败时也继续跑完后续步骤（便于诊断）
tools/recompile_mod.sh WuMingPerformance --keep-going

# 装到 gamedir/Mods/（需提权；会核对 md5）
tools/recompile_mod.sh WuMingPerformance --deploy
```

脚本做的事（与上面手敲的步骤一一对应）：

| 步 | 动作 |
|---|---|
| 1 | `ilspycmd -p -ds UsingDeclarations=false` + 绝对路径 `-r` |
| 2 | 补 `AssemblyInfo.cs` 的 `using` |
| **2b** | **补全引用集**（见下方“为什么需要这一步”） |
| 3 | `Read(in x)` → `Read(ref x)` |
| 4 | `dotnet build -c Release` |
| 5 | **验证产物**：体积量级 + `MelonInfo` 是否真的进了二进制 |

### 脚本覆盖的两个额外坑（手敲时容易漏）

**① 必须补全引用集（步骤 2b）**

ilspycmd 只写它**直接观察到**的引用，不够用。实际报错是 `CS0012`，
**看不见任何指向 csproj 的线索**：

```
error CS0012: 类型“Il2CppObjectBase”在未引用的程序集中定义。
              必须添加对程序集“Il2CppInterop.Runtime”的引用
```

实测：`EndTagMod` / `RefreshCraft` / `大地图瞬移MOD` 都因此失败，
补上 `AGENTS.md` §3.3 那套标准引用后**全部通过**。

**② 产物名 ≠ 文件名**

产物按 csproj 里的 `<AssemblyName>` 命名，**不一定等于磁盘上的 DLL 文件名**。
实测 `RefreshCraft.dll` 的 `AssemblyName` 是 **`LongYinLiZhiZhuanMelonLoader`** ——
脚本若按 `$MOD.dll` 去找，会把**构建成功**误报成失败。
脚本现在从 csproj 读真实 `AssemblyName`，并在二者不一致时出提示。

### 实测覆盖（本项目 28 个 mod）

```
OK:   24 / 28
FAIL:  4  ->  LYMod  MelonPrefManager.IL2CPP  RelationManagerMod  UnityExplorer.ML.IL2CPP.CoreCLR
```

**那 4 个失败不是流程问题，是 ilspycmd 自身的反编译 bug**，见 §9。

---

## 7. 修改代码时的注意事项

反编译产物**能编译**，但改它之前要知道：

| 事项 | 说明 |
|---|---|
| **语义等价性** | 反编译是「忠实但不保证同形」。改之前先用 §5 的 `-m` 核实关键类型的归属 |
| **别动类型归属** | 例如 `_heldTabs` 是 System list —— 改成 Il2Cpp 版**照样编译通过**，但行为变了 |
| **`Nullable`** | 产物可能写 `disable`；原 mod 若启用了可空引用类型，注意别引入空引用 |
| **改完必须冷启动验证** | 见 [`AGENTS.md`](../AGENTS.md) §3.2.1 —— 托管补丁可能热重载，但**新逻辑的验证必须冷启动** |
| **部署前核对 md5 与体积** | 见 [`AGENTS.md`](../AGENTS.md) §3.2 与 [`ilrepack.md`](ilrepack.md) |

---

## 8. 与相邻文档的分工

| 我要…… | 读 |
|---|---|
| 看懂**游戏**的原生代码（签名 / 调用图 / 伪代码） | [`decompilation.md`](decompilation.md) |
| **反编译别人的 mod 并重编**（通用流程，本文） | 本文件 |
| 看**一个具体**无源码 mod 修了什么（本文的实例） | [`forceoverflowdividend.md`](forceoverflowdividend.md) |
| 往自己的 mod 里内嵌第三方 DLL | [`ilrepack.md`](ilrepack.md) |

---

## 9. 已知限制：ilspycmd 自身的反编译 bug

本流程在 **24/28** 个 mod 上一次通过。剩下 4 个失败的**不是流程问题**，
而是 ilspycmd 输出了错误的或非法的 C# —— **改 csproj 无用**，
只能靠原源码、手改每一处、或换反编译器。

### ① 类型弄错（`CS0029` / `CS0030`）

ilspycmd 把某个**游戏字段的真实类型**反成了另一个类型。实测：

```csharp
// LYMod.Helpers/OtherHelper.cs:121 —— 反编译产物：
Il2CppSystem.Collections.Generic.List<Il2Cpp.HeroData> speHeroDataBase = instance.SpeHeroDataBase;
//                  ^^^^ 错的

error CS0029: 无法将 Dictionary<int, HeroData> 隐式转换为 List<HeroData>
```

而游戏的**真实类型**确实是 Dictionary：

```
$ monodis --property gamedir/MelonLoader/Il2CppAssemblies/Assembly-CSharp.dll | grep SpeHeroDataBase
class Il2CppSystem.Collections.Generic.Dictionary`2<int32, class Il2Cpp.HeroData> SpeHeroDataBase ()
```

同类：`RelationManagerMod`（`KeyValuePair` vs 值类型，`CS0030`）。

> 💡 **裁决方法**：用 §5 的 `ilspycmd -m` 或者直接查游戏程序集，
> 别信全量反编译的输出。

### ② 输出非法语法（`CS1525` / `CS1003`）

反编译产物里有**根本编译不过的 C#**。实测：

```csharp
// UnityExplorer.../InteractiveEnum.cs:43 —— 产物：
IsFlags = System.Linq.Enumerable.Any(EnumType.GetCustomAttributes(typeof(System.FlagsAttribute), inherit: true)?) ?? false;
//                                                                                                        ^ 非法
```

这是 ilspycmd 对某些 IL 模式（此处是 `Any(...)` 的 null 合并）的反编译缺陷，
**与 `UsingDeclarations` 等选项无关**。

### 碰到这些怎么办

| 情况 | 建议 |
|---|---|
| 只个别文件出错 | 手工修那几处（先确认真实类型，别改错） |
| 整个类型反得不对 | 用 `ilspycmd -t <type>` 单独反该类型看看 |
| 大量此类错误 | 这 mod 不适合这条流水线；找原源码，或用其他反编译器（如 dnSpy 在 Windows 下图形化修） |

> ⚠️ **不要“为了让编译通过”而乱改类型归属**。
> §1 已经记过一次教训：改错了**照样编译通过**，但**语义变了**。
