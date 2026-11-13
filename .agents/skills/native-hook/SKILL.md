---
name: native-hook
description: 当要读写龙胤立志传的原生内存、自建 detour、用 Iced 汇编 stub，或在游戏更新后重新定位 hook 落点时使用。覆盖地址运行时计算、钩点选择、stub 出口纪律、不透明字段三步排查、函数入口运行时定位。
whenToUse: 需要直接改 GameAssembly.dll 代码段或读原生字段、Harmony 不够用、游戏更新导致原有 detour 静默失效时。
---

# 原生内存与 detour

游戏是 Windows PE 在 Wine/Proton 里加载，但**就在当前进程地址空间内**，可读可写。
本 skill 只管「怎么安全地改原生代码」；反编译取证见 `il2cpp-decompile`，活进程读数见 `runtime-probe`。

**完成判据**：安装日志证明落点校验通过、出口与声明一致，且冷启动后目标行为按预期变化。

## 1. 地址与内存纪律

| 函数 | 作用 |
|---|---|
| `ResolveModule()` | 从 `Process.GetCurrentProcess().Modules` 找 `GameAssembly.dll`，拿 `BaseAddress` / `ModuleMemorySize` |
| `StaticVaToRuntime(va)` | `运行时地址 = 模块基址 + (静态 VA - 0x180000000)` |
| `FindPattern(byte[], mask)` | 签名扫描（`'x'` = 必须匹配，其余通配）；不用 `MemoryUtils`（它是 internal） |
| `TryReadBytes` / `ReadPointer` / `ReadByte` | `Marshal.Copy` / `Marshal.Read*` |
| `WriteBytes` / `NopOut` | 先 `VirtualProtect(PAGE_EXECUTE_READWRITE)`，写完恢复原保护 |

- ⚠️ **模块基址每次启动都变** → 地址必须运行时算，绝不落盘。
- ⚠️ **签名要含足够区分性字节**：`cmp byte[rax+0xd0],0` 在 `il2cpp` 节可能命中 17 次，要把前面的
  `e8 xx xx xx xx`（call 位移）纳入签名才唯一。
- ✅ **离线先预校验签名**（`output/decomp/il2cpp_map.py` 扫真实 DLL）：命中数必须 = 1，
  且「签名起点 + 固定偏移」精确落在目标指令上。
- ⚠️ **热重载对代码段改写无效**：`Marshal.Copy` 写过的字节不会被热重载还原 → **必须冷启动**。
- ✅ **装 detour 前先校验目标字节**。本项目一个探针模块因为字节校验正确拒绝安装，
  才没在已被 Il2CppInterop 占据的入口上叠第二层 detour。**保留这个模式。**

## 2. 选钩点：覆盖范围 + 返回值语义，缺一不可

### 覆盖范围：用调用者扫描，不要靠名字猜

第一版钩了 `GenerateMovePath`（名字像移动入口），实际只有 2 个调用者且全在 UI 路径；
AI 走的是 `PlayBattleUnitMove → MoveFromTarget → 逐格 EnterGrid`，**根本不经过它**。

```python
callers(data, secs, target_va)   # 全量扫 e8 rel32，见 il2cpp-decompile skill
```

> 「它应该会被所有路径调用」是**假设**；调用者扫描是**证据**。

### 返回值语义：决定 `false` 是「优雅拒绝」还是「崩溃」

| 原方法返回 | prefix 返回 `false` | 原因 |
|---|---|---|
| `void` | ✅ 安全 | 没有返回值供调用方消费 |
| 非 `void`（如 `IEnumerator`） | ❌ 危险 | 跳过原方法 → 返回 `null` → 调用方通常不判空 |

> 本项目：`BattleUnit.MoveFromTarget` 返回 `IEnumerator`，调用方直接丢给协程驱动，无 null 检查。

## 3. 不手写机器码，用 Iced 汇编

Iced 1.21 随 MelonLoader 自带（`gamedir/MelonLoader/net6/Iced.dll`），零新依赖。

```csharp
var asm = new Assembler(64);
var lbl = asm.CreateLabel("skip");
asm.cmp(rax, 2);
asm.je(lbl);
asm.Label(ref lbl);

var writer = new CodeWriterImpl();      // 实现 Iced.Intel.CodeWriter，只需 WriteByte
asm.Assemble(writer, rip, BlockEncoderOptions.None);
// 或 TryAssemble(writer, rip, out err, out result) —— 拿得到错误信息
```

`Assemble(..., rip, ...)` 的 **`rip` 是「这段代码将运行在哪个地址」** → **先分配 stub 地址，再构码**。

它消灭：ModRM 掩码手算错、rel8 偏移写反（自动放宽到 rel32）。
它不消灭：**选错钩点、破坏调用约定、漏分支** —— 那些要读懂反汇编。

### Iced 四条实测规则

1. ⚠️ **一个指令位置最多绑一个标签**（`At most one label per instruction`）→ 两个出口必须落在两条指令上。
2. ⚠️ **标签之后必须有指令** → 不要建收尾标签（`Unused label end@3`）。用 `TryAssemble` + `try/catch` 兜住。
3. ⚠️ **内存操作数 DSL 不是 `dword_ptr(...)`**：是 `AssemblerRegisters.__dword_ptr[rax + 0x14]`；
   裸写 `rax` 需要 `using static Iced.Intel.AssemblerRegisters;`；
   `Decoder` 与 `System.Text` 同名，必须写全 `Iced.Intel.Decoder.Create(...)`。
4. ⚠️ **出口不要用绝对地址**（`asm.jmp(0x180A8D8BC)` 不可重定位），用尾部绑定标签：
   `asm.jne(exitSkip); asm.Label(ref exitSkip); asm.ExitViaImm64(RuntimeVa(VaSkip));`

## 4. stub 纪律（代价最大的一课）

- ⚠️⚠️ **「放行」出口要送回「游戏原版的落点」，不要跳到「接受路径的中段」。**
  中段地址往往位于某个准入判定**已经通过之后**，且默认 `rsi`/`rbp`/栈槽已被前一段代码铺垫好 →
  真机表现为**静默地什么都不发生**，比崩溃难查得多。
  正确做法：让目标走**原版代码**（`jcc` 不跳时的落点），由游戏自己裁决。
- ⚠️ **同一个中段地址的危害不止一种，且「不崩」不等于「对」**：城墙 hook 完全不生效；
  穿友方 hook 看着能用，但静默跳过了 `AroundGridHaveEnemy` 检查 → AI 站到玩家头上。
  **宣判「这个出口能用」前必须同时验证：①没崩 ②语义对（游戏原有检查是否仍全部跑到）。**
- 💡 **只改判据，不接管「落点合不合法」**。hook 回答「谁算阻挡」，落点合法性交给游戏。
- ⚠️ **stub 出口不要用 `rax` 中转** → 用 `r11`（Win64 易失且很少被读），否则目标代码把代码字节当指针解引用。
- ⚠️ **rel8 偏移不要写死 `code[N]`**：发射时记偏移 → 回填 → 自检跳转目标，算错在安装瞬间报错。
- ⚠️ **hook 落在条件跳转上时，stub 必须自己重判那个条件**，只处理「跳」的一侧会劫持另一侧。
- ⚠️ **原生 detour 与尾调用不兼容**：目标以 `jmp <shared_tail>` 结尾时压帧会让 `rsp` 错位 → 改用 Harmony。
- 💡 **免费自检**：`NativeHookBase.VerifyExits` 扫描 stub 内的出口立即数，与子类 `ExpectedExits` 比对，
  不一致报 `ERROR` —— 把「出口跳错」从玄学现象提前成安装日志里的一行。
  > 依赖「运行时已初始化状态」的值不能放静态字段初始化（那时 `RuntimeVa` 返回 0），改成**属性**。
- 💡 **定位「不崩但不生效」：在活进程里改 stub 出口做单变量对照** —— 同格、同调用、只换出口，
  一次拿到干净因果（`49 BB <imm64> 41 FF E3`）。

## 5. 不透明字段三步排查（先做，别猜）

实例：`GridUnitData.passes`。

1. **看 dump 里的声明类型**：`grep -n "\bpasses\b" output/dumper_out/dump.cs`
   —— `private GridType gridType;` 是真 enum，`public int passes;` 是 int。**一个字段是 enum，不代表旁边的也是。**
2. **运行时读 `typeKind`**：`IL2CPP.il2cpp_type_get_type(ft)`，`8` = I4，`17` = VALUETYPE（枚举）；
   顺手 `il2cpp_field_get_offset` 交叉验证（返回 **uint**，直接赋 int 报 `CS0266`）。
3. **全量取值直方图**：把整张地图的值数一遍。每类**只有 1 个取值** ⇒ 它是「按类别整体赋值」的类别标志，不是逐格属性。

> 三步只能「排除错路 + 缩小范围」，**不能得出正解**：`passes=15` 为什么是 15、
> 它被 `Navigate` 当搜索深度上限（`row × passes`）用 —— 这些只有读反汇编才知道。

## 6. 游戏更新后重新定位（不要写死地址）

游戏一更新，代码位置整体洗牌（实测一个函数搬了 `-0x3aa60`，函数**内部**还插了 `0xee` 字节，
**连函数内偏移都不稳**）。写死地址 = 校验失败 → 拒绝安装 → **静默失效**。

**判据：落点只能用「函数入口 + 指令形态」表达。**

| 层 | 手段 | 备注 |
|---|---|---|
| ① 函数入口 | `il2cpp_class_get_method_from_name` → `Il2CppMethodInfo` 第 0 个字段 `methodPointer` | 跟 metadata 走，不跟地址走 |
| ② 函数边界 | 已加载映像的 PE `.pdata`（`RUNTIME_FUNCTION[]`）二分 | IL2CPP 不提供函数长度；读**内存里的 PE 头**，不是磁盘文件 |
| ③ 指令落点 | Iced 线性反汇编整个函数体，按**语义**匹配（指令形态 + 寄存器/偏移关系） | 起点必须是函数第一条指令 |

> ⚠️ **`methodPointer` 只是「入口」，不是「真身」**：IL2CPP 会给「声明类静态构造还没跑」的方法
> 生成 class-init wrapper，真身是**紧邻的下一个 `.pdata` 函数**（首尾相接，不是 `jmp`）。
> → 定位做**候选 BFS**：① methodPointer 函数；② 下一个相邻 `.pdata` 函数；③ 函数体内直接跳转目标。
> 每个候选都过同一套形态判据，**唯一命中**才算数。

配套经验（每条都掉过坑）：

- ⚠️ **偏移也要问不要写**：字段偏移用 `il2cpp_field_get_offset`，栈槽用指令形态找；两边**互相印证**，不一致失败关闭。
- ⚠️ **两个来源矛盾时不许猜**。
- ⚠️ **形态必须唯一命中**：0 处（形态变了）或 >1 处（窗口串进别的函数）都拒绝，「挑第一个」等于赌。
- ⚠️ **`cmp r/m, imm8` 的立即数不是 `Immediate32`**：能塞进有符号 8 位的会编成 `83 /7 ib`，
  Iced 报 `OpKind.Immediate8to32`；只认 `Immediate32` 会一处都匹配不到。
- ⚠️ **stub 依赖的寄存器要在形态里守住**（如要求 `test rax,rax`），换寄存器应失败关闭。
- 💡 **把不依赖运行时的部分抽成纯函数 + 离线 harness**（`tools/native_scan_harness/`），
  用真实 `GameAssembly.dll` 跑同一份源码。**游戏更新后先跑它。**
- 💡 **实测地址写进日志和文档，不写进代码**。
- ⚠️ **构建指纹（md5 AllowList）只能当门控/告警，不能当定位手段**。

## 7. 运行时读类型描述符槽（省掉大量机器码）

`mov rdx,[rip+X]` 的 `X` 是类型/属性描述符槽：文件期 BSS 未初始化，**跑起来后已被 IL2CPP 填好**。

```csharp
IntPtr slot    = NativeMemory.StaticVaToRuntime(0x181d86d20);
IntPtr info    = NativeMemory.ReadPointer(slot);
IntPtr namePtr = NativeMemory.ReadPointer(info + 0x08);
IntPtr parent  = NativeMemory.ReadPointer(info + 0x00);
string prop    = Marshal.PtrToStringAnsi(namePtr);
string cls     = IL2CPP.il2cpp_class_get_name_(parent);
```

`Il2CppPropertyInfo` 布局（v24+，来自 `NativePropertyInfoStructHandler_24_0`）：
`+0x00 parent` / `+0x08 name` / `+0x10 get` / `+0x18 set` / `+0x20 attrs` / `+0x24 token`。

> ⚠️ **写这类偏移前，先去 `Il2CppInterop.Runtime.dll` 找 `Native*StructHandler_*` 反编译出真实布局**，
> 不要按 C 直觉排字段 —— `parent` 排第一就是反例。

## 收尾

- 细节与实测见 [`docs/native-hooks.md`](../../../docs/native-hooks.md)；
  游戏更新事件见 [`docs/friendlynoclip.md`](../../../docs/friendlynoclip.md) §2.2h。
- 改完**冷启动**验证，并把新的实测地址写进 `docs/`。
