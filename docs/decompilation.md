# 反编译工作流

> 归属：[`AGENTS.md`](../AGENTS.md) §4 的详细展开。
> **本文件写「怎么把原生 IL2CPP 代码看明白」** —— 工具命令、分层策略、踩过的坑。
> 具体游戏的地址 / 字段偏移 / 调用链结论写在 `docs/<项目>.md`（游戏一更新即失效）。

本文对应 AGENTS.md 的「第 1–5 层」信息获取链路：
签名（ilspycmd）→ 调用图（cpp2il）→ 反汇编（capstone）→ 伪代码（Ghidra）→ metadata 直读。

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

脚本本体就在仓库里（**不要抄进本文件**，避免两份漂移）：
[`output/dumper_out/DecompMany.java`](../output/dumper_out/DecompMany.java)
—— 核心是 `getScriptArgs()` 每两个参数一组「地址 输出名」，
用 `getFunctionContaining(addr) or getFunctionAt(addr)` 定位 + `DecompInterface` 输出 `.c`。

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
> **不再需要「靠地址认函数」**。详见本文的 ImportSymbols 小节。
>
> 仍保留的手段：拿地址去 Ghidra 要伪代码，用 `getFunctionContaining(addr)` 定位
> —— 即使有符号名，**按地址点名仍是最可靠的定位方式**（符号名可能对不上）。

### 4.6 不可用的路径（已试过，别重试）

- **`MethodAddressToToken.db`**（`gamedir/MelonLoader/`）读出来两侧都是 token，是 token→token 映射，**取不到原生地址**。
- 按 stride 12/8 解析 `parameters` 表取参数类型**没成功**（`parameterStart` 对不上简单 stride）。**直接用反编译 C# 给的签名即可。**
- 从 cpp2il thunk 的 rip-relative 槽读 metadata token **失败**：那是 BSS 区未初始化类型槽（`va2off` 返回 None），按 `(attrs<<16)|type` 解码全部 OUT OF RANGE。

---
