---
name: il2cpp-decompile
description: 当需要弄清龙胤立志传游戏内部某个方法、字段或系统的真实行为时使用。按 ilspycmd → cpp2il → capstone/Ghidra 四层递进取证，附带每层的命令、判据与已知坑（含 find_callers 找调用者）。
whenToUse: 需要确认游戏方法签名、真实调用关系、字段偏移、枚举取值，或反编译结论要作为补丁依据时。
---

# 反编译：看明白游戏的原生代码

游戏逻辑全在 `gamedir/GameAssembly.dll` 里。托管侧 `Il2CppAssemblies/*.dll` 的方法体全是
`il2cpp_runtime_invoke` 转发，**没有实现**。所以「看代码」= 分层取证，缺一层都可能得出错误结论。

**完成判据**：结论能落到一个可复核的证据（签名 / 反汇编片段 / 伪代码），而不是「cpp2il 属性看起来像」。
结论最后写进 `docs/game-internals.md` 或 `docs/<项目>.md`。

## 选层：先问「我要什么」

| 我要 | 去哪层 | 工具 |
|---|---|---|
| 方法签名 / 参数名 / 字段名 / 枚举值 / metadata token | 1 | `ilspycmd` |
| 谁调用了谁（大致） | 2 | `cpp2il` + `tools/find_callers` |
| 真实调用点、指令形态、字段偏移用法 | 3 | `capstone` |
| 函数级 C 伪代码 | 3.5 | `Ghidra`（已建工程） |
| 结构化元数据（类型/字段/方法表） | 4 | 直读 `global-metadata.dat` |

> 框架（MelonLoader / Harmony / Unity）的用法**不在这里** —— 先 websearch，见 `AGENTS.md` §3.5。

## 第 1 层：ilspycmd —— 签名与字段

```bash
/usr/bin/ilspycmd -p -o output/decomp/full gamedir/MelonLoader/Il2CppAssemblies/Assembly-CSharp.dll
/usr/bin/ilspycmd -t Il2Cpp.SomeType -o output/decomp gamedir/MelonLoader/Il2CppAssemblies/Assembly-CSharp.dll
```

没有真实方法体，但**保留**：精确签名与**参数名**（Harmony 按参数名绑定，必需）、字段名、枚举值、
`[CallerCount(N)]`、`NativeMethodInfoPtr_* = IL2CPP.GetIl2CppMethodByToken(..., <token>)` 里的 **metadata token**。

想看运行时 DLL 的真实实现（如 Il2CppInterop 内部）：
`ilspycmd -t <完整类名> gamedir/MelonLoader/net6/Il2CppInterop.Runtime.dll`。

## 第 2 层：cpp2il —— 调用图

```bash
DOTNET_BUNDLE_EXTRACT_BASE_DIR=/tmp/dotnet_bundle ~/.local/bin/cpp2il \
  --game-path gamedir \
  --exe-name <游戏 exe 名，不带 .exe> \
  --use-processor attributeanalyzer,callanalyzer \
  --output-as dll_il_recovery \
  --output-to output/cpp2il_out
```

三个必踩的坑：

1. `--exe-name` **不能带 `.exe`**（会拼成 `xxx.exe.exe` 报找不到文件）。
2. `--force-binary-path` 与 `--force-metadata-path` **同时给会报错**，只择一或都不用。
3. 必须设 `DOTNET_BUNDLE_EXTRACT_BASE_DIR`，否则报 `Failed to determine location for extracting embedded files`。

产物反编译后方法体仍是 `throw null`，但多了 `[Calls]` / `[CalledBy]` / `[CallerCount]`。

> ⚠️ **`[Calls]` 是传递/近似推导，不是精确调用列表。** 本项目曾据此认定某函数是判定点并写好补丁，
> 随后 `e8 rel32` 全量扫描发现该函数**零调用者**，补丁根本不会生效。
> **规则：`[Calls]` 只能当线索，补丁目标必须用第 3 层核实。**

## 找调用者：`tools/find_callers`

```bash
tools/find_callers/find_callers.sh <方法名> [-t 类型] [-o] [-d cpp2il 目录] [-v]
```

退出码：`0` 有结果 · `1` 参数/输入问题 · `2` 跑通但没找到。

> ⚠️ **两个方向，属性指向相反**：`[Calls]` 挂在**调用者**上；`[CalledBy]` 挂在**被调用者自己**上、
> 列的是它的调用者 —— 拿目标方法去查 `CalledBy` 会**得 0 命中**。
> ⚠️ 精度：这是线索不是真值。作为补丁目标前**必须**用第 3 层反汇编核实。

## 第 3 层：capstone —— 反汇编核实（最终依据）

辅助模块 `output/decomp/il2cpp_map.py`（工作目录为项目根）：

```python
import sys; sys.path.insert(0, 'output/decomp')
import il2cpp_map as m
from capstone import *

data, secs = m.load()          # 返回 (bytes, 节区列表)，不是单值
md = Cs(CS_ARCH_X86, CS_MODE_64)

va  = 0x180000000 + 0x8aee0
off = m.va2off(secs, va)
for ins in md.disasm(data[off:off+200*8], va):
    print(f"{ins.address:#012x}  {ins.mnemonic:<8} {ins.op_str}")
```

**找调用者（比 cpp2il 属性可靠）—— 全量扫 `e8 rel32`**：

```python
import re, struct
def callers(data, secs, target):
    hits = []
    for nm, va, vs, ra, rs in secs:
        if nm not in ('il2cpp', '.text'): continue
        seg = data[ra:ra+rs]
        for mm in re.finditer(b'\xe8', seg):
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
# Il2CppCodeGenModule: [0] moduleName [1] methodPointerCount [2] methodPointers VA
# 索引换算：ptrs[(methodToken & 0x00FFFFFF) - 1]
ptrs = struct.unpack_from('<%dQ' % COUNT, data, POINTERS_FILE_OFF)
va = ptrs[(token & 0x00FFFFFF) - 1]
```

> ⚠️ **禁止**用「全局方法号 − 类型 methodStart」索引 `methodPointers` —— 本项目因此算错过地址。

全量字节模式扫描（确认字段偏移被哪些函数使用），例如 `cmp byte ptr [rax+0xd0], 0` = `80 b8 d0 00 00 00 00`。

节区布局（实测，imagebase `0x180000000`）：

| 节 | VA | VS | RAW |
|---|---|---|---|
| `.text` | 0x1000 | 0x20ded0 | 0x400 |
| `il2cpp` | 0x20f000 | 0x16ae8d9 | 0x20e400 |
| `.rdata` | 0x18be000 | 0x42f760 | 0x18bce00 |
| `.data` | 0x1cee000 | 0x45b114 | 0x1cec600 |
| `.pdata` | 0x214a000 | 0x132a14 | 0x1e89c00 |

> **游戏托管代码几乎全在 `il2cpp` 节**，`.text` 是 IL2CPP 运行时本身。只扫 `.text` 会得到 0 命中并误判路径不通。

**识别辅助函数**：`il2cpp` 节里大量 generic-instantiation thunk（`mov rax,[rip+X]; test rax,rax; jne …`）。
**不要**从它们的 `lea rcx,[rip+Y]` 读类型名字符串 —— 读到的是相邻断言字符串。

工具纪律：**手算地址必错**，一律在代码里算；**Capstone 从指令边界之外开始反汇编会静默返回 0 条指令** ——
那不是「没有指令」，是起点错了。

## 第 3.5 层：Ghidra —— 唯一能读 C 伪代码的地方

工程已建好（`output/ghidra_proj/LongYin`），**不必重新导入分析**。Ghidra 12.1.2 @ `/opt/ghidra`。

- ⚠️ **脚本一律写 `.java`**，不要用 `.py`（会被路由给 PyGhidra，旧脚本有 Py2/Py3 差异，`askFile()` 在 headless 下卡住）。
- ⚠️ **`analyzeHeadless` 必须 `danger-full-access` 提权** —— Ghidra 启动时会无条件重写
  `~/.config/ghidra/<ver>/java_home.save`，只读时连 `--help` 都出不来，预先创建该文件也没用。

**先导入符号名（强烈推荐，一次即可）**：

```bash
/opt/ghidra/support/analyzeHeadless \
    $PWD/output/ghidra_proj LongYin \
    -process GameAssembly.dll -noanalysis \
    -scriptPath $PWD/tools/il2cppdumper \
    -postScript ImportSymbols.java \
        $PWD/output/dumper_out/script.json
```

导入后 `FUN_180a8d5a0` 变成 `MapNavigator_Navigate`，可读性天差地别。

> ⚠️ **符号名会让你误判类型**：`script.json` 的 `Address` 是 **RVA**，符号名来自 metadata，
> 不反映调用点实际参数类型。看到名字后仍要结合调用点寄存器内容判断。

**批量反编译**（`output/dumper_out/DecompMany.java`，必须 `-postScript` + 脚本参数）：

```bash
/opt/ghidra/support/analyzeHeadless \
    $PWD/output/ghidra_proj LongYin \
    -process GameAssembly.dll -noanalysis \
    -scriptPath $PWD/output/dumper_out \
    -postScript DecompMany.java \
        $PWD/output/decomp_gud \
        0x180000000 SomeFunction \
        0x180000000 OtherFunction
```

要点：`-noanalysis` 省时间；`-scriptPath` 找脚本但**脚本名不带路径**；地址可带/不带 `0x`；
`getFunctionContaining(addr)` 定位**传函数内任意地址都行**；输出文件名是**你给的**，与 Ghidra 符号名无关。
⚠️ 脚本日志去 `~/.config/ghidra/<ver>/application.log` 看，不是终端。

## 第 4 层：metadata 直读

`global-metadata.dat` 在 `gamedir/<Game>_Data/il2cpp_data/Metadata/`（sanity `0xfab11baf`，**v27**）。

header 为 `<64i`（偶数下标为 offset）：`[0]sanity`、`[1]version`、`[6]stringOffset`、`[12]methodsOffset`、
`[22]parametersOffset`、`[24]fieldsOffset`、`[30]genericContainersOffset`。

记录 stride（实测，别猜）：

| 记录 | stride | 布局 |
|---|---|---|
| `Il2CppMethodDefinition` | **32** | `<8i`: `(nameIndex, declaringTypeIndex, returnTypeIndex, parameterStart, genericContainerIndex, token, flags, iflags)` |
| `Il2CppTypeDefinition` | **88 (0x58)** | 16 个 int（`[0]nameIndex [8]fieldStart [9]methodStart …`），+64 处 `<4H`；token 在 +0x54 |
| `Il2CppFieldDefinition` | **12** | `<3i`: `(nameIndex, typeIndex, token)` |

> 32 字节的方法定义 stride 用 64 也「看起来对」（方法数算成一半并错位）。**务必用已知类型的字段数交叉验证。**

## 走不通的路（别重试）

- **`MethodAddressToToken.db`** 是 token→token 映射，取不到原生地址。
- 按 stride 12/8 解析 parameters 表**没成功**；直接用反编译 C# 的签名。
- 从 cpp2il thunk 的 rip-relative 槽读 metadata token **失败**（BSS 未初始化槽，va2off 返回 None）。

## 收尾

1. 结论写 `docs/game-internals.md`（随构建变的机制）或 `docs/<项目>.md`（具体地址/调用链）。
2. 只写「证据 + 结论」，推导过程留在文档里，别往 `AGENTS.md` 塞。
3. 细节与实测数据见 [`docs/decompilation.md`](../../../docs/decompilation.md)。
