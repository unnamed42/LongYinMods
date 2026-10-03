# AGENTS.md — 龙胤立志传 Mod 开发

面向 AI 助手与开发者的**通用工作手册**：项目结构、构建环境、反编译与运行时探查工作流。

> **本文件只写不随游戏构建变化的内容**（工具命令、环境、API 用法、踩坑、项目约定）。
> **具体游戏的地址 / 字段偏移 / metadata token / 调用链 / 某函数的实测行为，以及各 mod 的设计与取舍，写在 `docs/<项目>.md`** —— 那类内容**游戏一更新即失效**。

---

## 1. 这是什么项目

**龙胤立志传**（LongYinLiZhiZhuan）的 **MelonLoader IL2CPP mod 集合**。不是游戏本体开发，而是通过 Harmony / 原生内存改写扩展一个已发行 Unity 游戏的行为。

- 游戏：Unity **2020.3.48f1c1** / **IL2CPP** / **x64** / IL2CPP metadata **v27**
- Mod 框架：**MelonLoader 0.7.x Open-Beta** + **CoreCLR**（`net6.0`）
- 运行方式：Linux 下通过 **Wine / Proton**

因此所有代码都在**两个世界之间的边界**上：托管 C# 侧（MelonLoader / Harmony / Il2CppInterop）与原生侧（`GameAssembly.dll` 里编译后的 C++）。**本项目的大部分难度来自这条边界。**

---

## 2. 目录结构

```
LongYinMods/
├── AGENTS.md              本文件 —— 通用工作手册
├── README.md              项目简介
├── docs/
│   └── <project>.md       各 mod 的设计 / 实现 / 经验 / 取舍
├── gamedir -> ...         指向游戏根目录的软链接（必需，见下）
├── <Project>/             各 mod 的 C# 工程（csproj + 源码）

├── output/                本地临时产物（全部不提交 git，见下）
│   ├── decomp/            反编译辅助（il2cpp_map.py 等）
│   ├── decomp/full/Il2Cpp/  ilspycmd 产出的完整托管反编译
│   ├── decomp_gud/        Ghidra 反编译的 C 伪代码（关键函数）
│   ├── decomp_ghidra/     早期 Ghidra 产物
│   ├── cpp2il_out/        cpp2il 产物（含调用图属性）
│   ├── dumper_out/        Il2CppDumper 产物（dump.cs / script.json）+ 自建 Ghidra 脚本
│   ├── ghidra_proj/       已分析好的 Ghidra 工程（勿重新导入）
│   └── logs/              Ghidra 等工具的日志
├── tools/                 自建脚本（gdb_catch.sh 等）
├── MelonMCP/              自建的 Unity MCP 服务端（见 §7.1）
│   ├── MelonMCP/          工程本体（csproj / 源码 / Server / Tools）
│   ├── lib/net6/mcs.dll   内嵌依赖（Mono.CSharp），构建必需，**要进 git**
│   └── nuget.config       NuGet 缓存重定向
```

> **`gamedir` 只建在仓库根，各工程用相对路径上溯过去。**
>
> ```bash
> ln -s <游戏根目录> gamedir          # 只在仓库根建一个
> ```
>
> 各 csproj 里的写法是「从自己所在目录上溯到仓库根」，**深度不同、写法不同**：
>
> | 工程位置 | HintPath 前缀 |
> |---|---|
> | `<Project>/<Project>.csproj` | `..\gamedir\...` |
> | `<Project>/<Project>/<Project>.csproj`（多一层，如 MelonMCP） | `..\..\gamedir\...` |
>
> ⚠️ **不要在每个工程目录下各建一个 `gamedir` 软链接。** 那种做法看着方便，但：
> ①它们是独立文件，换机器 / 重装时要逐个重建，忘一个就构建失败；
> ②它们会被当成待提交内容（绝对路径的机器相关符号链接，**不能进 git**）；
> ③路径一旦挪动就静默失效。
> 统一从仓库根出发，只需维护**一个**链接（它在 `.gitignore` 里）。
>
> ⚠️ **`gamedir` 是跨工作区边界的软链接**（指向游戏安装盘），**默认沙箱策略拒绝写入**。
> 但**可以提权写入** —— 部署 DLL 这类写操作，**AI 自己提权完成即可**，
> 不必推给用户（见 §3.2）。只有提权也做不到的操作才需要用户介入。
>
> **`output/` 下全部是本地分析产物，不进 git**（`.gitignore` 已忽略 `output/`）。用 §4 的流程随时可重新生成。
---

## 3. 构建环境

### 3.1 构建

```bash
dotnet build <Project>/<Project>.csproj -c Debug
# 产物：<Project>/bin/Debug/net6.0/<Project>.dll
```

### 3.2 部署：AI **可以**提权写 `Mods/`，不要把它当成用户的义务

`gamedir/` 是跨工作区边界的软链接，**默认沙箱拒绝写入**；但**不是做不到** ——
用 `sandbox_permissions: danger-full-access` + `justification` 提权即可直接写入 `gamedir/Mods/`。

**⭐ 推荐：AI 自己提权部署，并在同一次调用里核对 md5。**

```bash
cp <Project>/bin/Debug/net6.0/<Project>.dll gamedir/Mods/
# 两边必须一致才算部署成功
md5sum gamedir/Mods/<Project>.dll <Project>/bin/Debug/net6.0/<Project>.dll
```

**为什么这条要写进手册**：本项目**因为跑的是旧产物而白耗过整整两轮**，
而且症状本身就是「代码不生效」—— **与真正的逻辑 bug 无法区分**，
于是 AI 会跑去读一堆正确的代码，找不存在的 bug。
让它自己在提权调用里顺手把 DLL 拷过去，就从根上消除了这一类误导。

> ⚠️ 无论谁拷的，**都必须核对两边 md5 一致**才算部署成功。

### 3.2.1 ⚠️ 改代码后必须**冷启动**（比拷错文件更隐蔽）

托管补丁（Harmony）才可能热重载；**原生改写不会**，且模块基址每次启动都变。
更关键的是：**很多钩子只在特定时机触发一次**（例如战斗开局建地图）。
在已经跑起来的进程里重新加载 DLL，**那个时机已经过去了**。

本项目的真实教训：`BattleMapData.Generate` 的 Postfix 明明
`patcherIsValid=true`、`attached=true`，但因为补丁是在该场战斗的地图
**生成之后**才注册的，postfix **从头到尾没执行过**（日志里一条都没有）。

**验证结论前必做**：

1. 确认 `Mods/` 里的 md5 == 刚构建的 md5；
2. **冷启动游戏**，重新走到触发路径；
3. 找**自己代码打的那条日志**（而非只看 Harmony 的「已挂上」）。

> 💡 「补丁已挂上」≠「代码跑了」（详见 §5.2）。
> 排查时**先确认产物与时机**，再去读代码 —— 否则很容易在正确的代码里
> 找不存在的 bug。

### 3.3 csproj 必需引用

基础四个：`MelonLoader.dll`、`0Harmony.dll`、`Il2CppInterop.Runtime.dll`、`Assembly-CSharp.dll`。

**外加（漏了会报奇怪的错）**：

| 引用 | 不引会怎样 |
|---|---|
| `Il2Cppmscorlib.dll`、`Il2CppSystem.dll`、`Il2CppSystem.Core.dll` | `Il2CppSystem` / `Il2CppSystem.Collections.Generic` 命名空间**不在这三个之外**（不在 Il2CppInterop.Runtime 里） |
| `UnityEngine.CoreModule.dll` | `error CS0012: 类型"MonoBehaviour"在未引用的程序集中定义` |
| `UnityEngine.dll` | 同上，类型解析失败 |

全部用 `<Private>False</Private>`（不要把游戏 DLL 拷进输出目录），HintPath 走 `$(Il2cppDir)` / `$(MelonLoaderDir)`。建议加 `<Nullable>enable</Nullable>`（免得 CS8632 警告淹没日志）。

### 3.4 常见编译坑

- **`MelonBase.LoggerInstance` 是实例成员**，静态补丁方法里直接用会 `CS0120`。
  解法：`internal static MelonLogger.Instance Log = null!;`，在 `OnInitializeMelon` 里 `Log = LoggerInstance;`
- **日志 API 是 `Warning(...)` 不是 `Warn(...)`**（只有 `Msg` / `Warning` / `Error`），写成 `Warn` 报 `CS1061`。
- 跨文件引用日志要写 **`Plugin.Log.Warning(...)`**，不能裸写 `Log`。
- 碰原生内存需在 csproj 加 `<AllowUnsafeBlocks>true</AllowUnsafeBlocks>`，否则 `CS0227`。

### 3.5 往 mod 里内嵌第三方 DLL（ILRepack）

有时需要把 MelonLoader **没提供**的库随 mod 一起发布（本项目实例：`mcs.dll` / Mono.CSharp，
用于 REPL）。目标是**仍产出单个 DLL**，不需要用户多拷几个依赖。

MelonLoader 自带的依赖（`Newtonsoft.Json` / `Iced` / `Mono.Cecil` / `Il2CppInterop.*` …）
**不需要内嵌** —— 用 `<Private>false</Private>` 引用即可（运行时已经有）。

配方（已在本项目验证，产出单 DLL）：

```xml
<!-- 1. 把 DLL 收进仓库，例如 lib/net6/mcs.dll -->
<Reference Include="mcs">
  <HintPath>$(LibDir)\net6\mcs.dll</HintPath>
  <Private>true</Private>          <!-- 要拷到输出目录，ILRepack 才能看到它 -->
</Reference>

<PackageReference Include="ILRepack.Lib.MSBuild.Task" Version="2.0.48">
  <PrivateAssets>all</PrivateAssets>
</PackageReference>
```

```xml
<Target Name="MergeThirdParty" AfterTargets="Build" Condition="'$(Configuration)'=='Release'">
  <PropertyGroup>
    <MergedModPath>$(TargetDir)MyMod.dll</MergedModPath>
    <PristineModPath>$(IntermediateOutputPath)MyMod.dll</PristineModPath>
  </PropertyGroup>
  <ItemGroup>
    <MergeInputs Include="$(PristineModPath)" />
    <MergeInputs Include="$(LibDir)\net6\mcs.dll" />
  </ItemGroup>
  <ILRepack TargetKind="SameAsPrimaryAssembly" OutputFile="$(MergedModPath)"
            InputAssemblies="@(MergeInputs)"
            LibraryPath="$(TargetDir);$(LibDir)\net6;$(MelonDir);$(UnityPath)"
            Internalize="false" Parallel="true" DebugInfo="false"
            Union="true" AllowDuplicateResources="true"
            AllowedDuplicateNamespaces="Mono.CompilerServices" />
</Target>
```

**三个必踩的坑**（每一个都真耗过时间）：

1. ⚠️ **必须放一个空的 `<Project>/ILRepack.targets`**。`ILRepack.Lib.MSBuild.Task` 会
   **自动导入**一个 target，它把 `$(OutputPath)*.dll` **全部**合并。你已经有自己的合并步骤时，
   它会把自己的产物**再合一次** → `Duplicate type ... was also present in ...`。
   该自动 target 的条件是 `!Exists('$(ILRepackTargetsFile)')`，所以**放一个空文件即可关掉它**。
2. ⚠️ **合并的输入必须是 `$(IntermediateOutputPath)MyMod.dll`（obj 里的原始产物），
   不能是 `$(TargetPath)`**。读 `TargetPath` 会让增量构建把「已合并的 DLL」当成输入再合一次，
   同样报重复类型 —— 而且**清一次 obj/bin 后会“好一次”**，极容易被误判成偶然问题。
3. ⚠️ **`TargetKind` 用 `SameAsPrimaryAssembly`**，写 `Library` 会得到 MSB4064 警告。

**参数名以实际暴露的为准** —— 猜错会报 `MSB4064: 不支持"xxx"参数`。
本项目实测 `ILRepack.Lib.MSBuild.Task` 2.0.48 暴露的可写属性（用
`monodis --property <task.dll>` 枚举，不要猜）：

```
AllowDuplicateResources  AllowedDuplicateNamespaces  Internalize  InternalizeExclude
InternalizeAssembly  RenameInternalized  ExcludeInternalizeSerializable  Union
TargetKind  OutputFile  InputAssemblies  LibraryPath  Parallel  DebugInfo
KeyFile  DelaySign  XmlDocumentation  RepackDropAttribute  …
```

**不暴露**（写了必报 MSB4064）：`AllowedDuplicateTypes`、`UnionMerge`、`AllowZeroInputAssemblies`。
注意正确拼写是 `AllowedDuplicateNamespaces`（Duplicate 是复数）。

**验证合并成功**（不是看构建退出码）：

```bash
# 1) 第三方类型真的进去了
monodis --typedef MyMod/bin/Release/net6.0/MyMod.dll | grep -c "Mono.CSharp"
# 2) 它已不再是外部引用（应该没有输出）
monodis --assemblyref MyMod/bin/Release/net6.0/MyMod.dll | grep -i mcs
```

> 补充：被内嵌的 DLL 本身可能**已经是别人的合并产物**（`mcs.dll` 就内含 MonoMod，
> 自带一份 `Mono.CompilerServices.SymbolWriter`）。这就是 `Union="true"` +
> `AllowedDuplicateNamespaces` 这两个参数存在的原因 —— 不能省。

**NuGet 缓存重定向**：若 `~/.nuget` 或 `~/.local/share/NuGet` 在只读/受限位置，
在仓库根放 `nuget.config`：

```xml
<configuration>
  <config>
    <add key="globalPackagesFolder" value=".nuget/packages" />
    <add key="http_cache_path" value=".nuget/http-cache" />
  </config>
</configuration>
```
---

## 4. 反编译工作流

游戏逻辑全在原生 `GameAssembly.dll` 里，托管侧 `Il2CppAssemblies/*.dll` **只是转发代理**（方法体全是 `il2cpp_runtime_invoke`）。因此需要多层信息，**缺一不可**。

### 4.1 第 1 层：ilspycmd —— 签名与字段

```bash
/usr/bin/ilspycmd -p -o output/decomp/full gamedir/MelonLoader/Il2CppAssemblies/Assembly-CSharp.dll
# 单类
/usr/bin/ilspycmd -t Il2Cpp.SomeType -o output/decomp gamedir/MelonLoader/Il2CppAssemblies/Assembly-CSharp.dll
```

能看到真实逻辑是没有的，但**保留这几样**：
- 精确签名与**参数名**（Harmony 按参数名绑定，必需）
- 字段名、枚举值
- `[CallerCount(N)]`、`[CachedScanResults(...)]`
- `NativeMethodInfoPtr_* = IL2CPP.GetIl2CppMethodByToken(..., <token>)` —— **metadata token**

> 想看某个运行时 DLL 的**真实实现**（如 Il2CppInterop 内部）时：
> `ilspycmd -t <完整类名> gamedir/MelonLoader/net6/Il2CppInterop.Runtime.dll`

### 4.2 第 2 层：cpp2il —— 调用图

```bash
DOTNET_BUNDLE_EXTRACT_BASE_DIR=/tmp/dotnet_bundle ~/.local/bin/cpp2il \
  --game-path gamedir \
  --exe-name <游戏 exe 名，不带 .exe> \
  --use-processor attributeanalyzer,callanalyzer \
  --output-as dll_il_recovery \
  --output-to output/cpp2il_out
```

三个必踩的坑：
1. **`--exe-name` 不能带 `.exe`** —— 带了会拼成 `xxx.exe.exe` 然后报找不到文件。
2. `--force-binary-path` 与 `--force-metadata-path` **同时给会报错**（`Invalid force option configuration`），只择一或都不用。
3. 必须设 `DOTNET_BUNDLE_EXTRACT_BASE_DIR`，否则 `Failed to determine location for extracting embedded files`。

产物用 ilspycmd 反编译后方法体仍是 `throw null`，但**多了 `[Calls]` / `[CalledBy]` / `[CallerCount]`**。

> ⚠️ **重要陷阱**：`[Calls]` 是**传递/近似推导，不是精确调用列表**。本项目曾据此认定某函数是目标判定点并写好补丁，随后用 `e8 rel32` 全量扫描发现该函数**在整个二进制里零调用者**，补丁根本不会生效。
>
> **规则：`[Calls]` 只能当线索，任何补丁目标都必须用第 3 层的反汇编验证真实调用关系。**

### 4.3 第 3 层：capstone 反汇编核实（最终依据）

辅助模块 `output/decomp/il2cpp_map.py`（工作目录为项目根）：

```python
import sys; sys.path.insert(0, 'output/decomp')
import il2cpp_map as m
from capstone import *

data, secs = m.load()          # 返回 (bytes, 节区列表)，不是单值
md = Cs(CS_ARCH_X86, CS_MODE_64)

va  = 0x180000000 + 0x8aee0    # 绝对虚拟地址（imagebase 0x180000000）
off = m.va2off(secs, va)
for ins in md.disasm(data[off:off+200*8], va):
    print(f"{ins.address:#012x}  {ins.mnemonic:<8} {ins.op_str}")
```

**找调用者（比 cpp2il 属性可靠）** —— 全量扫 `e8 rel32`：

```python
import re, struct
def callers(data, secs, target):
    hits = []
    for nm, va, vs, ra, rs in secs:
        if nm not in ('il2cpp', '.text'): continue
        seg = data[ra:ra+rs]
        for mm in re.finditer(b'\xe8', seg):          # e8 = call rel32
            o = mm.start()
            if o + 5 > len(seg): continue
            rel = struct.unpack_from('<i', seg, o+1)[0]
            ins_va = m.B + va + o
            if ins_va + 5 + rel == target:
                hits.append(ins_va)
    return hits
```

**反查函数地址（方法指针表，最可靠）**：

```python
import struct
# Il2CppCodeGenModule 在文件偏移 0x1a5bc60：
#   [0] moduleName  [1] methodPointerCount  [2] methodPointers VA
# 索引换算：ptrs[(methodToken & 0x00FFFFFF) - 1]
ptrs = struct.unpack_from('<%dQ' % COUNT, data, POINTERS_FILE_OFF)
va = ptrs[(token & 0x00FFFFFF) - 1]
```

> ⚠️ **禁止**用「全局方法号 − 类型 methodStart」索引 `methodPointers` —— 本项目因此算错过地址。
> 正确索引是 **`ptrs[(token & 0x00FFFFFF) − 1]`**。

**全量字节模式扫描**（用于确认某字段偏移被哪些函数使用）：

```python
# 例：cmp byte ptr [rax+0xd0], 0  ==  80 b8 d0 00 00 00 00
for nm, va, vs, ra, rs in secs:
    seg = data[ra:ra+rs]
    n = len(re.findall(re.escape(bytes.fromhex('80b8d0000000')), seg))
    if n: print(nm, n)
```

**节区布局**（实测，imagebase `0x180000000`）：

| 节 | VA | VS | RAW |
|---|---|---|---|
| `.text` | 0x1000 | 0x20ded0 | 0x400 |
| `il2cpp` | 0x20f000 | 0x16ae8d9 | 0x20e400 |
| `.rdata` | 0x18be000 | 0x42f760 | 0x18bce00 |
| `.data` | 0x1cee000 | 0x45b114 | 0x1cec600 |
| `.pdata` | 0x214a000 | 0x132a14 | 0x1e89c00 |

> **游戏托管代码（C#→C++ 编译产物）几乎全在 `il2cpp` 节**，`.text` 是 IL2CPP 运行时本身。
> 扫描字节模式时**别只扫 `.text`**（本项目曾因此得到 0 命中而误判路径不通）。

**识别 IL2CPP 辅助函数**：`il2cpp` 节里大量函数是 **generic-instantiation thunk**，模板为
`mov rax,[rip+X]; test rax,rax; jne skip; lea rcx,[rip+Y]; call ...; mov [rip+X],rax; jmp rax`。
**不要**试图从它们的 `lea rcx,[rip+Y]` 读类型名字符串 —— 读到的是相邻断言字符串，不可靠。

工具纪律：**手算地址必错**，一律在代码里算。**Capstone 从指令边界之外开始反汇编会静默返回 0 条指令** —— 那不是「没有指令」，是起点错了。

### 4.4 第 4 层：metadata 直读

`global-metadata.dat` 在 `gamedir/<Game>_Data/il2cpp_data/Metadata/`（sanity `0xfab11baf`，**v27**）。

实测 header（`<64i`，偶数下标为 offset），例如 `[0]sanity`、`[1]version`、`[6]stringOffset [7]stringSize`、`[12]methodsOffset [13]methodsSize`、`[22]parametersOffset`、`[24]fieldsOffset`、`[30]genericContainersOffset`。

**记录 stride（实测，别猜）**：

| 记录 | stride | 布局 |
|---|---|---|
| `Il2CppMethodDefinition` | **32** | `<8i`: `(nameIndex, declaringTypeIndex, returnTypeIndex, parameterStart, genericContainerIndex, token, flags, iflags)` |
| `Il2CppTypeDefinition` | **88 (0x58)** | 16 个 int（`[0]nameIndex [1]namespaceIndex … [8]fieldStart [9]methodStart … [15]interfaceOffsetsStart`），+64 处 `<4H`: `method_count, property_count, field_count, event_count`；token 在 +0x54 |
| `Il2CppFieldDefinition` | **12** | `<3i`: `(nameIndex, typeIndex, token)` |

> 32 字节这个方法定义 stride，用 64 也「看起来对」（方法数会算成一半并错位）—— 这类错误很隐蔽，**务必用已知类型的字段数交叉验证**。

```python
import struct
M = open('gamedir/<Game>_Data/il2cpp_data/Metadata/global-metadata.dat','rb').read()
TYPES, TSTRIDE, STR, FIELDS, FSTRIDE = ..., 88, ..., ..., 12
def s(i):
    e = M.index(b'\0', STR+i); return M[STR+i:e].decode('utf8','replace')
def typedef(name):
    for idx in range(...):
        o = TYPES + idx*TSTRIDE
        ni = struct.unpack_from('<i', M, o)[0]
        if 0 < ni < ... and s(ni) == name:
            fs, ms = struct.unpack_from('<ii', M, o+32)
            mc, pc, fc, ec = struct.unpack_from('<4H', M, o+64)
            return dict(idx=idx, fieldStart=fs, methodStart=ms, fc=fc, mc=mc)
```

**Il2CppCodeGenModule**：全局 methods 表按模块拼接。本项目实测 8 个模块（`mscorlib` / `Assembly-CSharp` / `Assembly-CSharp-firstpass` / `System.Xml` / `System` / `com.rlabrecque.steamworks.net` / `System.Core` / `System.Data`）。

### 4.5 第 3.5 层：Ghidra —— **读 C 伪代码的唯一来源**

第 3 层 capstone 只能看汇编。**要看函数级 C 伪代码，必须用 Ghidra。** 本项目已建好工程，**不必重新导入分析**（导入+分析 `GameAssembly.dll` 要很久）。

- Ghidra **12.1.2** @ `/opt/ghidra`，启动器 `/usr/bin/ghidra`
- **工程**：`output/ghidra_proj/LongYin`（headless 用）

> ⚠️ **脚本用 `.java`，不要用 `.py`**（即使装了 PyGhidra）。Ghidra 12.1.2 把 `.py` 路由给
> PyGhidra；本机**已装 PyGhidra 3.0.2**，但 Jython 时代的脚本（`ghidra_with_struct.py`）
> 仍有 Python2/3 语法与 API 差异，且 `askFile()` 在 headless 下会卡住。
> **写 Ghidra 脚本一律用 `.java`**（GhidraScript 原生 API，最稳）。
>
> ⚠️ **Ghidra 启动时会无条件重写** `~/.config/ghidra/<ver>/java_home.save`。
> 沙箱只读时必然报 `FileNotFoundException: ... java_home.save (只读文件系统)`，
> 而且**预先创建该文件也没用**（它照样要重写）。
> → **运行 `analyzeHeadless` 必须用 `danger-full-access` 提权**，否则连 `--help` 都出不来。
> 提权后会自动使用 `/usr/lib/jvm/java-21-openjdk`（Ghidra 12.1.2 要求 Java 21）。

**⭐ 导入符号名（强烈推荐，先做这一步）**：

`tools/il2cppdumper/ImportSymbols.java` 把 Il2CppDumper 的 `script.json` 里的
「类名.方法名」写回 Ghidra 工程，**一次性 62278 条全部成功**。做完之后反编译输出里
`FUN_180a8d5a0` 会变成 `MapNavigator_Navigate`、`FUN_180873a10` 会变成
`GridUnitData_Distance`、`0x18181e6b0` 会变成 `System_Collections_ArrayList_Add` ——
**可读性天差地别**。

```bash
# 只需跑一次；结果保存在工程里（Save succeeded）
/opt/ghidra/support/analyzeHeadless \
    $PWD/output/ghidra_proj LongYin \
    -process GameAssembly.dll -noanalysis \
    -scriptPath $PWD/tools/il2cppdumper \
    -postScript ImportSymbols.java \
        $PWD/output/dumper_out/script.json
```

> ⚠️ **符号名会让你误判类型**！`script.json` 的 `Address` 是 **RVA**（不是 VA），
> 且符号名来自 metadata，**不反映调用点的实际参数类型**。
> 实例：`0x1808CA250` 的符号名是 `BattleMapData.get_GridCount`（读 `[rcx+0x20]*[rcx+0x24]`），
> 而 `Navigate` 调用它时 `rcx` 是 **`from`（一个 `GridUnitData`）**，于是实际算的
> 是 `passes * row`。**看到符号名后，还要结合调用点的寄存器内容判断。**

**批量反编译脚本 `output/dumper_out/DecompMany.java`**（本项目自建，**必须用 `-postScript` + 脚本参数**）：

```
用法：-postScript DecompMany.java <outdir> <addr1> <name1> <addr2> <name2> ...
```

```bash
/opt/ghidra/support/analyzeHeadless \
    $PWD/output/ghidra_proj LongYin \
    -process GameAssembly.dll \
    -noanalysis \
    -scriptPath $PWD/output/dumper_out \
    -postScript DecompMany.java \
        $PWD/output/decomp_gud \
        0x180000000 SomeFunction \
        0x180000000 OtherFunction
```

脚本本体（`getScriptArgs()` 取参，每两个参数为一组「地址 输出名」）：

```python
import os
from ghidra.app.decompiler import DecompInterface

args = getScriptArgs()
outdir = args[0]
if not os.path.isdir(outdir): os.makedirs(outdir)
d = DecompInterface(); d.openProgram(currentProgram)
af = currentProgram.getAddressFactory().getDefaultAddressSpace()
for k in range(1, len(args), 2):
    addr_s, name = args[k], args[k+1]
    func = getFunctionContaining(af.getAddress(addr_s)) or getFunctionAt(af.getAddress(addr_s))
    if func is None:
        print("NO FUNCTION %s (%s)" % (addr_s, name)); continue
    res = d.decompileFunction(func, 120, monitor)      # 120 = 超时秒
    if res and res.decompileCompleted():
        open(os.path.join(outdir, name + ".c"), 'w').write(res.getDecompiledFunction().getC())
        print("OK %s @ %s" % (name, func.getEntryPoint()))
    else:
        print("FAIL %s: %s" % (name, res.getErrorMessage() if res else "no result"))
```

要点：
- **`-noanalysis`**：工程已分析过，加它省时间。
- `-scriptPath` 找脚本，**脚本名不要带路径**。
- 地址**可带或不带 `0x`**。
- 脚本内用 `getFunctionContaining(addr) or getFunctionAt(addr)` → **传函数内部任意地址都能定位**，不必非给入口。
- 输出文件名由**你**给，**与 Ghidra 内符号名无关** → 文件名里的方法名是**人工标注**，可能与托管名不符。
- ⚠️ **脚本日志要去 Ghidra 的 log 里看，不是终端**：`~/.config/ghidra/<ver>/application.log`（含 `Execute script:` / `NO FUNCTION` / `Save succeeded`）。

> ✅ **符号名已可用（2026-10）**：早期的结论「`ghidra_with_struct.py` 在本环境从未成功运行」
> 已**过时**。现在用自建的 `tools/il2cppdumper/ImportSymbols.java` 一次性导入 62278 条符号，
> 反编译输出直接显示 `MapNavigator_Navigate` / `GridUnitData_Distance` 这样的真名，
> **不再需要「靠地址认函数」**。详见 §4.5 的 ImportSymbols 小节。
>
> 仍保留的手段：拿地址去 Ghidra 要伪代码，用 `getFunctionContaining(addr)` 定位
> —— 即使有符号名，**按地址点名仍是最可靠的定位方式**（符号名可能对不上）。

### 4.6 不可用的路径（已试过，别重试）

- **`MethodAddressToToken.db`**（`gamedir/MelonLoader/`）读出来两侧都是 token，是 token→token 映射，**取不到原生地址**。
- 按 stride 12/8 解析 `parameters` 表取参数类型**没成功**（`parameterStart` 对不上简单 stride）。**直接用反编译 C# 给的签名即可。**
- 从 cpp2il thunk 的 rip-relative 槽读 metadata token **失败**：那是 BSS 区未初始化类型槽（`va2off` 返回 None），按 `(attrs<<16)|type` 解码全部 OUT OF RANGE。

---

## 5. Harmony 补丁 IL2CPP 方法（**最大的坑区**）

### 5.1 ⚠️ 必须显式选重载，禁止按名取方法

`Type.GetMethod(name)` 在**同名重载**下返回哪一个**不确定**。本项目真实踩过：同一个类型同时有 `void Foo()`（**空桩**）和 `void Foo(Bar)`（真实现），而某个方法的同名条目在 `script.json` 里**出现两条**。

**规则：挂补丁时必须按参数个数选重载**，并在日志里打印**真实绑定的签名 + IL 地址**：

```csharp
MethodInfo target = methods.First(m => m.GetParameters().Length == parameterCount);
Log.Msg($"已挂载补丁：{type.Name}.{target} (IL={target.MethodHandle.GetFunctionPointer().ToInt64():x}) -> {patchName}");
```

**只打「挂载成功」是没用的** —— 挂到空桩上和挂到真实现上，日志一模一样。

### 5.2 「挂载成功」≠「触发」≠「代码执行了」

Harmony 报「已挂载」只说明 `Patch()` 没抛异常。三层要分开：

1. **挂载成功** —— `Patch()` 不抛。
2. **触发** —— 必须有**运行时触发日志**（且该日志**不能被诊断开关门控**）。
3. **生效** —— 行为可观测。

> ⚠️ **「日志没打印」≠「代码没执行」**：**先查该日志是否被门控**。本项目曾因 `[登记]` 受 `Diagnostics` 门控而 `grep -c` = 0，误判为「补丁零触发」，白耗一轮。
> **取证仪表要不受门控。**

### 5.3 Prefix / Postfix 是**两个独立 patch**

`TryPatch` 挂 Postfix、`TryPatchPrefix` 挂 Prefix —— 别以为挂了一个就等于两个都挂了。运行时用这条确认：

```csharp
HarmonyLib.Public.Patching.PatchManager.GetPatchInfo(m).postfixes.Count()
```

### 5.4 `__state` 的官方语义

[Harmony prefix 文档](https://harmony.pardeike.net/articles/patching-prefix.html)：
> "use `__state` (with the `ref` or `out` keyword)… **This only works if Prefix and Postfix are defined in the same class since Harmony internally uses the declaring type as a key**"

[Choose a patch type](https://harmony.pardeike.net/v3/articles/patching.html)：
> "Use `__state` for values that belong to one patched invocation… Static fields are appropriate for deliberately shared state, not independent per-call values."

→ **不要用静态字段传 per-call 值**（被逐格连续调用的方法会串味）。

```csharp
internal static void Target_Prefix(TargetType __instance, out MyState? __state) { __state = new MyState(...); }
internal static void Target_Postfix(TargetType __instance, MyState? __state) { /* 用 __state */ }
```

### 5.5 IL2CPP 原生方法入口在运行时可能已被改写

Il2CppInterop 会**在原生函数入口装 detour**。特征是入口变成 `ff 25 <disp32>`（`jmp qword ptr [rip+disp32]`），**跳转槽落在模块映像之外**（Il2CppInterop 自有内存）。

- **磁盘上**（`GameAssembly.dll`）读到的仍是原始序言（如 `40 53 48 83 ec 20`）。
- **运行时**读到 `ff 25 …` **不是故障**，是 detour 已装好的形态。

依据 [BepInEx `IL2CPPDetourMethodPatcher.Init()`](https://github.com/BepInEx/BepInEx/blob/ec79ad057b20c302c17b34e63906ee398352d852/BepInEx.IL2CPP/Hook/IL2CPPDetourMethodPatcher.cs)：

```csharp
originalNativeMethodInfo = UnityVersionHandler.Wrap((Il2CppMethodInfo*)(IntPtr)methodField.GetValue(null));
var trampolinePtr = DetourGenerator.CreateTrampolineFromFunction(originalNativeMethodInfo.MethodPointer, out _, out _);
nativeDetour = new FastNativeDetour(originalNativeMethodInfo.MethodPointer, detourPtr);
nativeDetour.Apply();
```

→ **detour 打的就是 `Il2CppMethodInfo.methodPointer`，即函数入口本体**；游戏对该函数的 `call` 直达该地址，**所以 Harmony 补丁处于必经之路上**。注意这条只在 `IsValid == true` 时成立 —— 若生成方法里找不到 `ldsfld NativeMethodInfoPtr_*`，`Il2CppDetourMethodPatcher` **不设 `IsValid` 且无日志**，`args.MethodPatcher` 从不被设置，Harmony 静默回退到托管 IL 补丁。用 `DescribePatcher` 风格反射把 `PatchManager.GetMethodPatcher(m)` 的运行时类型 + `IsValid` **打出来**，别猜。

> ⚠️ **不要**因为入口是 `ff 25` 就断言「Harmony 挂的是托管 DMD 包装、原生调用绕过了它」—— 这个结论是**错的**（本项目在错误方向上浪费过一轮）。`IL=<某个远离模块的地址>` 是这条间接链的远端，**不是无关包装**。

**推论：在已被 Il2CppInterop 占据的入口上再叠一层自己的 detour 是自找麻烦。** 装之前先读入口字节：若已是 `ff 25`，说明该入口已被接管，**改用 Harmony**。

### 5.6 `MethodHandle.GetFunctionPointer()` 对 IL2CPP 代理返回的是托管包装地址

不要拿它去和原生函数体地址比较 —— 两者本来就不同。可用于**区分同名重载**（不同重载 IL 地址不同），但**不能**用来判断「补丁是否会触发」。

### 5.7 调用者地址在 IL2CPP 下取不到

`new StackTrace()` 拿到的托管栈**不含 native 帧**，永远是
`#0 .DMD<Il2Cpp.X::Y> #1 .(il2cpp -> managed) Y #2 IL2CPP.il2cpp_runtime_invoke`。
想拿 native 调用者必须走原生级手段。

### 5.8 ⚠️ 空的 `[HarmonyPatch]` 会在每次启动报 ERROR

**症状**（每次启动都有，不崩溃、不影响功能）：

```
[ERROR] Failed to HarmonyInit PatchAll: MyMod.SomePatch
HarmonyLib.HarmonyException: Patching exception in method null
 ---> System.ArgumentException: Undefined target method for patch method
      static bool MyMod.SomePatch::Prefix(bool value)
   at HarmonyLib.PatchClassProcessor.PatchWithAttributes(MethodBase& lastOriginal)
```

**成因**：类上写了**光秃秃的** `[HarmonyPatch]`（没带 `typeof` / 方法名），同时类里有个
看起来像补丁的方法（`static bool Prefix(...)`）。MelonLoader 的 `MelonBase.HarmonyInit()`
会对整个程序集跑 `PatchAll()`，它扫到这个类、也扫到了那个“像补丁”的方法，
**但没东西可绑** → `method null` / `Undefined target method`。

> ⚠️ **这不是 IL2CPP 问题** —— Mono 下同样复现。看到 `method null` 不要往 IL2CPP 方向排查。

**修法**：

- 若目标是在运行时反射解析、再用 `HarmonyInstance.Patch` **手动挂**的（手挂模式），
  **删掉那个 `[HarmonyPatch]`**——它会把手挂的补丁拉进自动扫描，两者机制不兼容。
- 若这个类**确实想**被自动挂，就必须给它具体目标：
  `[HarmonyPatch(typeof(X), "Method")]` + 方法上标 `[HarmonyPrefix]` / `[HarmonyPostfix]`。

**为什么值得修**（而非“反正是无害的”）：两种情况下都没挂上任何补丁，所以功能确实不受影响；
但每次启动一行 ERROR 会**污染日志、掩盖真实错误**，下次排查时极可能又被当成线索查一轮。

**定位手法**：

```bash
grep -rn "\[HarmonyPatch" --include=*.cs          # 类级、且没带参数的即是元凶
monodis --customattr MyMod.dll | grep -i HarmonyPatch   # IL 层核实已清干净
```
---

## 6. 原生内存操作（Proton 下可用）

游戏是 Windows PE（`GameAssembly.dll`）在 Wine/Proton 里加载，但**它就在当前进程地址空间内**，所以 `Process.GetCurrentProcess().Modules` 能枚举到，且可读可写。

封装路径：

| 函数 | 作用 |
|---|---|
| `ResolveModule()` | 用 `Process.GetCurrentProcess().Modules` 找 `GameAssembly.dll`，拿 `BaseAddress` / `ModuleMemorySize` |
| `StaticVaToRuntime(va)` | `运行时地址 = 模块基址 + (静态VA - 0x180000000)` |
| `FindPattern(byte[], string mask)` | 签名扫描（`'x'` = 必须匹配，其他 = 通配）。不用 `Il2CppInterop.Runtime.MemoryUtils`（它是 `internal`） |
| `TryReadBytes` / `ReadPointer` / `ReadByte` | `Marshal.Copy` / `Marshal.Read*` |
| `WriteBytes` / `NopOut` | 先 `VirtualProtect(PAGE_EXECUTE_READWRITE)`，写完恢复原保护 |

### 6.1 关键纪律

- **模块基址每次启动都变**（Proton 下见 `0x6ffff5f60000` / `0x6ffff5f70000` 之类）→ **地址必须运行时算**。
- **签名要含足够区分性字节**。同一个 `cmp byte[rax+0xd0],0` 模式可能在 `il2cpp` 节里出现 17 次，必须把前面的 `call` 位移（`e8 xx xx xx xx`）也纳入签名才唯一。相邻两条 `cmp` 之间只有 call 位移不同 —— 这是区分「同一函数内不同过滤点」和「不同函数内过滤点」的关键判据。
- **离线用 Python 预校验签名**（必做，能省一轮实机）：用 `output/decomp/il2cpp_map.py` 在 `GameAssembly.dll` 上直接扫，确认命中数是 1 且「签名起点 + 固定偏移」精确落在目标指令上。
- ⚠️ **离线预校验的比较基准要算准**：拿「签名起点」的静态 VA 去比「目标指令」的预期地址必然误报。
- **热重载对代码段改写无效**：`Marshal.Copy` 写过的代码段不会被 Harmony 热重载还原；且模块基址每次启动都变 → **改原生代码后必须冷启动**。
- **装 detour 前先校验目标字节**。本项目一个探针模块因为**字节校验正确地拒绝了安装**，才没有在已被 Il2CppInterop 占据的入口上叠第二层 detour。**这个校验模式值得保留。**
- ⚠️ **原生 detour 与尾调用（tail call）不兼容**。若目标函数以 `jmp <shared_tail>` 结尾，自建 stub 压帧会让到达共享尾部块时 `rsp` 偏移错误，被恢复的寄存器（`xmm6`/`r15`/`r14` 之类）来自垃圾内存 → 一运行即崩。**Dobby 的跳板只处理「跳回原函数」，对尾调用语义零处理。** 遇到这种函数改用 Harmony。
- ⚠️ **hook 在「条件跳转」上时，stub 必须自己重判那个条件**。`jcc` 在两种情况下都会被执行，只处理「跳」的那一侧会把「不跳」的路径也劫持 → 行为全面错乱（实测：移动范围只剩脚下那一格）。
- ⚠️ **stub 的出口跳转不要用 `rax` 中转**，除非确认目标不读它。`mov rax,imm64; jmp rax` 会把 `rax` 改成代码地址；而目标如果是一段**原有代码**，它常常依赖上一条指令在 `rax` 里留下的值 → 把代码字节当指针解引用 → SIGSEGV。改用 `mov r11,imm64; jmp r11`（`r11` 是 Win64 易失寄存器，且很少被读）。
- ⚠️ **stub 的 rel8 偏移不要写死为 `code[N]`**。改成「发射时记下偏移 → 回填 → **自检跳转目标**」三步；算错会在安装瞬间打 `ERROR` 日志，而不是变成玄学现象（本项目两个 inline stub 都因此受益）。
- ⚠️⚠️ **stub 的「放行」出口不要跳到「接受路径的中段」，要跳到「游戏自己的判定入口」**。
  这是本项目代价最大的一个坑：同一函数里看起来「后面就是处理逻辑」的那个地址，往往位于
  某个**准入判定已经通过之后**，它既会跳过判定本身，又默认 `rsi`/`rbp`/栈槽已被前一段代码铺垫好
  → 真机表现为**静默地什么都不发生**（不崩、不报错、功能就是不生效），比崩溃难查得多。
  正确做法：让目标格走**原版代码**（即 `jcc` **不跳**时的落点），由游戏自己裁决。
  **同一地址在两条不同调用路径上的寄存器状态可能完全不同** —— 不能因为「A 路径用 `expand` 能用」
  就假定 B 路径也能用。
- ⚠️⚠️ **同一个「中段地址」的危害不止一种，而且「不崩」不等于「对」。**
  本项目在**两个**不同 hook 上都误用了同一个 `0x180a8d963`，症状却完全不同：
  - 城墙 hook：**功能完全不生效**（格子不亮）—— 因为进入该地址的前置寄存器未就绪。
  - 穿友方 hook：**看上去能用**（格子亮、能进范围），但**静默地跳过了
    `AroundGridHaveEnemy`（交战区 / 敌方邻接）检查**，导致寻路把「队友格子」
    当成合法落点 → **AI 直接站到玩家头上**。
  → 教训：出口选错时，如果那段代码刚好还能跑，你会得到一个**看起来正常但语义错误**的功能，
  而它的 bug 会以**完全不相干的现象**（AI 站位）暴露出来。
  **宣判定「这个出口能用」之前，必须同时验证：①没崩 ②语义对（游戏原有的检查是否仍全部跑到）。**
- 💡 **写 stub 时把「我的职责」与「游戏的职责」分清楚。**
  本项目的正确划分：hook **只改判据**（「谁算阻挡」），**绝不接管「落点合不合法」**。
  具体做法就是：「放行」时一律送回**原版 `jcc` 不跳时的落点**，让它继续跑完游戏的全套检查。
  这样既不用猜寄存器状态，也不会漏掉任何后续校验。
- 💡 **定位这类「不崩但不生效」的兵家大法：在活进程里改 stub 出口，做单变量对照。**
  用 MCP 把同一个 stub 的出口字节分别换成各候选地址（`49 BB <imm64> 41 FF E3` 即可，
  因为 stub 在可写内存里），每改一次调一次目标函数，比较返回值。
  **同格、同调用、只有出口不同** —— 一次就能拿到干净的因果，远快于反复改源码 + 冷启动。
  本项目因此把「三候选出口」从「靠猜」变成一张对照表。

### 6.2 运行时读类型描述符槽（强烈推荐，省掉手写机器码）

图像里那些 `mov rdx,[rip+X]` 的 `X` 是**类型/属性描述符槽**。它们文件期在 BSS 里是未初始化的（离线 `va2off` 返回 None、解码全 OUT OF RANGE），**但游戏跑起来后已被 IL2CPP 填好**，所以在 mod 里读它们能直接拿到类型名/属性名/方法名 —— **不需要插桩、不需要写机器码**。

这是判定「某原生函数读的 `[obj+off]` 属于哪个类的哪个字段」的**最省力路径**，优先于反汇编猜偏移。

```csharp
IntPtr slot    = NativeMemory.StaticVaToRuntime(0x181d86d20);   // rip 目标地址
IntPtr info    = NativeMemory.ReadPointer(slot);                // PropertyInfo*
IntPtr namePtr = NativeMemory.ReadPointer(info + 0x08);        // name
IntPtr parent  = NativeMemory.ReadPointer(info + 0x00);        // 所属类
string prop    = Marshal.PtrToStringAnsi(namePtr);
string cls     = IL2CPP.il2cpp_class_get_name_(parent);
```

**`Il2CppPropertyInfo` 布局（v24+）** —— 来自 Il2CppInterop 的 `NativePropertyInfoStructHandler_24_0`，**别凭直觉猜**：

```
+0x00 Il2CppClass*       parent     <- 最容易漏，漏了会让 name 读到类指针
+0x08 byte*              name
+0x10 Il2CppMethodInfo*  get
+0x18 Il2CppMethodInfo*  set
+0x20 uint32             attrs
+0x24 uint32             token
```

**类名/方法名解析**（`Il2CppInterop.Runtime.IL2CPP` 的 public 方法，直接用）：

```csharp
IL2CPP.il2cpp_class_get_name_(IntPtr klass)
IL2CPP.il2cpp_method_get_name_(IntPtr method)
IL2CPP.il2cpp_field_get_name_(IntPtr field)
IL2CPP.il2cpp_type_get_name_(IntPtr type)
```

**教训**：写这类偏移前，先去 `Il2CppInterop.Runtime.dll` 找对应的 `Native*StructHandler_*` 类反编译出真实布局，**不要按 C 直觉排字段顺序** —— `parent` 排第一就是反例。

---

## 7. 运行时探查

静态分析到极限时，用运行时手段定论。**有活进程就别猜日志。**

### 7.1 Unity MCP（MelonMCP）★ 推荐

**可以对运行中的游戏执行 C# 表达式、直接读写活对象** —— 排查效率远高于读日志。

- 服务端源码在 `MelonMCP/`（本项目自建，Mono.CSharp REPL 已 ILRepack 内嵌，可执行完整 C# 语句）。
- **核心工具**：`execute_csharp` / `evaluate_expression` / `find_objects_of_type` / `list_game_objects` / `get_type_info` / `list_types` / `list_assemblies` / `read_logs`。
- **排查补丁用的工具**（2026-10 新增）：`hook_patch_info`（补丁挂载/触发/生效 + patcher 类型 + 入口字节）、`list_patches`（全进程补丁清单，含其他 mod）、`disasm` / `read_mem` / `resolve_jump`（**运行时**字节与跳转解析）、`watch_field` / `unwatch_field`（轮询字段变化）。
- **持久化的知识库**：`get_game_knowledge` / `add_game_knowledge`。
  ⚠️ **它只用于存「游戏本身的知识」**（世界观、设定、数值规则、游戏机制这类**与 mod 开发无关**、
  且**游戏更新也大体不变**的内容）。
  **不要往里写开发侧的东西** —— 工具用法、构建配方、反编译流程、踩坑、地址/字段偏移、
  调用链结论一律**不属于**它：
  - 通用工具/构建/API 知识 → **本文件 `AGENTS.md`**
  - 具体游戏的地址 / 字段偏移 / 调用链 / 实测行为 → **`docs/<project>.md`**

  两个理由：①落盘在 `gamedir/UserData/MelonMCP/game_knowledge.json`，**在游戏目录里、不在仓库里**，
  不随 git 同步，换机 / 校验 / 重装即丢；②别的 agent 读 `AGENTS.md` / `docs/` 时根本看不到它。
  它只保证**跨 MCP session** 可见，**不保证跨仓库/跨机器**。

**`execute_csharp` / `evaluate_expression` 的实际能力边界**（引擎为内嵌的 Mono.CSharp）：

- ✅ **支持**：运算符与字符串拼接、`new` 对象构造、索引器、`foreach`、LINQ
  （`Where`/`Select`/`OrderBy`/`ToArray`）、`string.Join`、`typeof`、泛型类型、带局部变量的多语句。
- ✅ **会话状态跨调用保持**（变量 / using / 自定义类型）；`execute_csharp` 传 `reset=true` 清空。
- ❌ **不支持 `return x`** —— 见下方“注意事项”表（用裸尾表达式）。
- ⚠️ **已知问题**：**一整条超长单行表达式**（如整串 `string.Join(...Where(...Select(...)))`）
  会**静默**返回 `Execution completed (no result)`；拆成两条语句（先赋值再拼接）即正常。

⚠️ **已禁用的工具（不要再尝试，它们会让 MCP 客户端看到一个名字却永远失败）**：

| 工具 | 禁用原因 |
|---|---|
| `list_components` / `inspect_component` | IL2CPP 下组件列表塌缩为 `UnityEngine.Component` 代理，所有组件都报成 `Component`，`inspect_component` 永远匹配不到类型名 |
| `toggle_behaviour` / `set_property` / `invoke_method` | 同上：靠 `GetComponents` + `GetType().Name` 定位目标组件 |
| `find_game_object` | 受 `instanceId` 分支牵连（`path` 分支本身可用）；改用 `execute_csharp` + `UnityEngine.GameObject.Find` |
| `inspect_material` | 靠 `GetComponent(gameObject, "Renderer")` 拿 renderer，永远报 `No renderer found` |
| `take_screenshot` | 该 IL2CPP/CoreCLR 运行时下 `Texture2D` + `ReadPixels` 路径返回 null |

实现在源码里保留了，注册处用 `#if MELONMCP_ENABLE_BROKEN_INSPECTION_TOOLS` / `#if MELONMCP_ENABLE_BROKEN_SCREENSHOT` 关掉——**修好后开宏即可，不要重写**。

仍有部分工具**只有 `instanceId` 分支是死的**（`path` 分支正常）：`instantiate_object` / `set_transform` / `destroy_object`。它们依赖 `UnityHelper.FindObjectByInstanceId`，后者依赖一个在 IL2CPP 下解析为 null 的非泛型 `FindObjectsOfType` 反射查找。**用 `path` 寻址，别传 `instanceId`。**

**注意事项（踩过的坑）**：

| 现象 | 原因 / 对策 |
|---|---|
| **表达式里不能用 `return x`** | 交互式宿主方法返回 `void`，`return x` 报 `error CS0127`。**用裸尾表达式**：`var n = "x"; "hello " + n` |
| 一整条超长单行表达式（如整串 `string.Join(...Where(...Select(...)))`）静默无结果 | 拆成两条语句 |
| `Count` 报错 | 它是**方法**不是属性 → 写 `.Count()` |
| `FindObjectsOfType<T>()` 找不到游戏数据类 | `GridUnitData` / `BattleUnit` 之类**不是 `UnityEngine.Object`**；要经游戏自己的容器取（如 `BattleController.battleMapData.GetGridData(r, c)`） |
| `FindObjectOfType<Il2Cpp.Xxx>()` 报 `Method unstripping failed` | 改用 `FindObjectsOfType<MonoBehaviour>(true)` 按 `GetType().Name` 过滤 |
| 偶发卡顿 / 失败 | 网络问题，**直接重试** |
| 局部变量重名 | `CS0136` —— 会话状态跨调用保持，换个名字 |
| lambda 语句体里带 `new object[]{...}` | 编译失败，拆开写 |

### 7.2 日志与文件路径

- **日志**：`gamedir/MelonLoader/Latest.log`
- **配置落盘**：`gamedir/UserData/MelonPreferences.cfg` —— ⚠️ 若源码**没有**调 `SetFile`，所有 `[Category]` 都写在**这一个文件**里，不要去找 `UserData/<ModName>.cfg`。MelonPreferences **不会**自动删除已废弃的键（僵尸键无害，但别被骗）。
- **写文件必须用** `MelonLoader.Utils.MelonEnvironment.GameRootDirectory`。**不要**用 `AppContext.BaseDirectory + "..\\.."` —— 在 Proton 下相对回退会失败（本项目踩过）。

### 7.3 MelonLoader 0.7.3 生命周期

- `OnApplicationStart()` 在该版本是 `[Obsolete(error: true)]` → 用 **`OnInitializeMelon()`**；teardown 用 `OnDeinitializeMelon()`。
- `UnregisterInstance` 顺序：`OnDeinitializeMelon()` → `UnregisterInternal()` → … → **`HarmonyInstance.UnpatchSelf()`**。
  → **插件不需要自己撤销 Harmony 补丁**，MelonLoader 会做。teardown 只需释放非 Harmony 资源。
- `MelonLogger` 的静态事件（`MsgCallbackHandler` / `WarningCallbackHandler` / `ErrorCallbackHandler`）若不退订会**钉住程序集、阻止 ALC 回收**。

### 7.4 崩溃排查

- **游戏崩溃时 MelonLoader 来不及写日志** → 用 coredump 或**二分隔离**。
- **二分隔离比读代码快得多**：把可疑功能各配一个开关，逐项关掉观察是否还崩。
- **附加 gdb**：沙箱默认是 `bwrap --ro-bind / / --dev /dev --unshare-pid`，**PID 命名空间隔离**（只能看到几个 PID）—— 这是**命名空间问题不是权限问题，`sudo` 无用**。需要 `danger-full-access` 级别的提权才能看到宿主机进程。
- gdb 会在 .NET 常规信号（SIGUSR1/SIGUSR2）上误停 → 必须加 `handle SIGUSR1 nostop noprint pass`（及 SIGUSR2/SIG32/SIG33/SIGPIPE）。
- ⚠️ **`GameAssembly.dll` 只映射头部一页，真正的代码在匿名 `r-xp` 区**（约 24MB）→ 按 `r-xp` + 大小筛选候选区。
- **VA→文件偏移映射**：`file_off = VA - 0x180000000 - 0xc00`（PE 头 + 节对齐差）。直接用 `VA - 0x180000000` 会读错字节。

#### 首选：让 .NET 自己产出 minidump（已验证可用）

实测有效，**不用装任何东西、不用写代码**。在 Steam 启动选项里设：

```
DOTNET_DbgEnableMiniDump=1 DOTNET_DbgMiniDumpType=1 DOTNET_DbgMiniDumpName="S:\coredump\crash_%t.dmp" DOTNET_EnableCrashReport=1 %command%
```

- 环境变量要写在 `%command%` **前面**；Proton 直接继承父进程 env（`proton` 里 `self.env = dict(os.environ)`）
- **`S:` 是游戏目录**（Proton 把游戏盘映射为 `S:`）→ `S:\coredump\` 对应 `.../SteamLibrary/coredump/`，比 `C:` 好找得多
- **不指定 `DOTNET_DbgMiniDumpName` 时默认落在 `%TEMP%`**（`createdump` 内部调 `GetTempPath2A`），**不是** CWD
- 占位符：`%p`=pid、**`%t`=Unix 时间戳**（推荐，比 pid 好排序）、`%e`=exe 名
- `createdump.exe` 在 Wine 下**确实可用**（实测产出合法 `MDMP`，8 个流，85 线程 + 301 模块）

#### 离线回溯：`tools/il2cpp_unwind.py`

```bash
tools/il2cpp_unwind.py /run/media/.../coredump/crash_<ts>.dmp [--all-threads]
```

**为什么 gdb 不行**：IL2CPP 编译的 x64 代码**不用 frame pointer**，`rbp` 常是堆指针，gdb 的 `bt` 只能打印 `#1 0x2 in ?? ()`。
**正确做法**：x64 Windows 用 **`.pdata` 表驱动回溯**（`RUNTIME_FUNCTION` → `UNWIND_INFO`）。本二进制有 **104663** 条；脚本已实现解析 + 回溯 + 符号化。

**写这类工具的三个坑**（都真踩了）：

1. ⚠️ **`os.path.basename` 在 Linux 上不切 Windows 反斜杠路径**。minidump 里模块名是 `S:\...\GameAssembly.dll`，要手写 `name.replace('\\','/').rsplit('/',1)[-1]`。
2. ⚠️ **「在模块内」的边界必须是模块真实大小**，不能用很大的常数（如 `0x100000000`），否则越界地址被当成模块内、产生假帧。大小从 `ModuleList` 取。
3. ⚠️ **二分查找要防空集回绕**：`bisect_right` 对**小于**最小元素的值返回 `i=0`，其前驱是最后一个元素 → 看似命中实则荒谬。先判 `rva < sorted[0]`。

**符号化只能命中 `il2cpp` 节**：`script.json` 地址范围 `0x20F000..0x18A3E10`，起点恰是 `il2cpp` 节开头。**`.text`（`0x1000..0x20DED0`）是 IL2CPP 运行时本体，本来就没托管符号**，显示成 `<GameAssembly+0x...>` 是**正确行为而非 bug**。

**ASLR 不是问题**：运行时基址（`0x6FFF...`）与静态 imagebase（`0x180000000`）差一个固定偏移，`base + RVA` 换算后 `.pdata` 能正确命中。怀疑偏移时，**拿一个已知 rip 反查 `.pdata`** —— 命中即说明换算正确。

#### 为什么外部手段都抓不到

真实案例：`Marshal.Copy` 打在未映射地址上，**无日志、无 coredump（`coredumpctl` 计数不变）、journalctl 无 wine segv**。原因：

- CoreCLR 只处理**发生在托管代码或它自己原生运行时里**的硬件异常，其余直接 `PROCAbort`
- Wine 的 `segv_handler` 把异常转成 Windows 异常，不走 Linux 信号给 systemd-coredump
- 结果**两边都以为对方会处理**

→ 所以**必须显式开 `DOTNET_DbgEnableMiniDump`**。

> ⚠️ **`FailFast` 产出的 dump 里没有 `Exception` 流**（不产生硬件异常），看不到故障地址。
> 真实野指针崩溃会有 `EXCEPTION_ACCESS_VIOLATION` + 故障地址，**信息量大得多**。
> 另：`FailFast` 跑在触发它的线程上（如 MCP 的 socket 线程），**栈上没有游戏代码** —— 要验证符号化得让崩溃发生在游戏逻辑里。

---

## 8. 工具链

**所有工具由用户安装，AI 一律不得自行下载或安装 —— 包括装到工作区内的情况。**

这条曾被执行成「只禁止写 `~/.dotnet` 等沙箱外路径」，于是有人把单个可执行脚本
`curl` 到工作区的 `tools/bin/` 里当作「不算安装」。**那是钻字面，不是守约定。**
判断标准是**意图**而不是落地路径：只要一个工具在当前环境里原本不存在、需要你额外
获取才能用，就应该停下来请用户装，不要自己想办法绕。

- ❌ `pip install` / `npm install` / `curl` 下载脚本 / 解压 tar 到任何位置
- ❌ `~/.dotnet/tools`（会报 `Read-only file system`）
- ✅ 发现缺工具 → 告诉用户「需要装 X」→ 用户装完继续

> 实际上多数工具已经装好了，先 `which <tool>` 确认，不要提前假设缺失。

| 工具 | 用途 |
|---|---|
| `/usr/bin/dotnet` | 构建（SDK 10.0.x） |
| `/usr/bin/ilspycmd` | 托管反编译（第 1 层） |
| `~/.local/bin/cpp2il` | 调用图恢复（第 2 层） |
| python + `capstone` | 反汇编核实（第 3 层） |
| `/opt/ghidra` | C 伪代码（第 3.5 层）。**运行需 `danger-full-access` 提权**（它要重写自己的 java_home.save） |
| `pyghidra` 3.0.2 | 已装；但脚本仍推荐写 `.java`（见 §4.5） |
| `git-filter-repo` 2.47.0 | 历史重写（`filter-branch` 已被官方劝退）。`/usr/bin/git-filter-repo` |
| `objdump` / `monodis` / `gdb` | 备用：反汇编 / 程序集 IL / 调试 |

---

## 9. 各项目文档

| 项目 | 说明 | 文档 |
|---|---|---|
| FriendlyNoclip | 战斗格子地图允许穿越友方 | [docs/friendlynoclip.md](docs/friendlynoclip.md) |

> **通用内容写本文件，项目内容写 `docs/<project>.md`，新发现随代码改动一起更新（不是以后补）。**
