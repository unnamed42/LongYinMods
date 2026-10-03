# 原生内存与 detour

> 归属：[`AGENTS.md`](../AGENTS.md) §6 的详细展开。
> 包含：原生内存读写纪律、自建 detour 的踩坑、用 **Iced** 取代手写机器码、
> 以及「不透明字段的三步排查法」。

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
### 6.1b ★ 手写机器码的正确替代：用 MelonLoader 自带的 **Iced** 汇编

**结论：不要手写字节。运行时就有一个完整的 x86 汇编器可用，零新依赖、纯托管。**

| 库 | 位置 | 能做什么 |
|---|---|---|
| **Iced** 1.21 | `MelonLoader/net6/Iced.dll`（1.9MB，**已随 MelonLoader 加载**） | **汇编 + 反汇编 + 编码**，完整 x64 |
| **Dobby** | `gamedir/version.dll`（MelonLoader 原生宿主）内，由 `MelonUtils.NativeHookAttach` / `NativeHook<T>` 封装 | 装 detour + **指令搬迁**（自己负责 rel32 可达的跳板） |

> ✅ **2026-10 实测**（活进程 + 离线两路）：用 `Iced.Assembler` 重建本 mod 的两个 stub，
> 输出与手写字节**逐字节完全一致**（`WallPassHook` 59 字节、`FriendlyPassHook` 60 字节，
> 含四个 `jcc` 位移 `1B`/`1F`/`19`/`0D` 与两个出口）。**说明它确实能替掉手算，不只是「能编码」。**

```csharp
var asm = new Assembler(64);
var lbl = asm.CreateLabel("skip");     // 可带名字，报错信息里会显示
asm.cmp(rax, 2);                       // 每个助记符一个方法
asm.je(lbl);                           // 按标签跳，偏移由它算
asm.Label(ref lbl);                    // 绑定标签（必须在 emit 结束前全部绑完）

var writer = new CodeWriterImpl();     // 自己实现 Iced.Intel.CodeWriter（只需 WriteByte）
asm.Assemble(writer, rip, BlockEncoderOptions.None);
// 或 TryAssemble(writer, rip, out err, out result) —— 拿得到错误信息
```

**`Assemble(..., rip, ...)` 的 `rip` 就是「这段代码将运行在哪个地址」**，
与本项目「运行时分配 stub 地址」天然合身。⚠️ 这反过来要求**先分配、再构码**。

**它消灭的坑**：ModRM 掩码手算错；rel8 操作数偏移 vs opcode 偏移写反 → SIGILL
（偏移由汇编器算，**目标太远会自动放宽到 rel32**）。
**它不消灭的坑**：选错 hook 地址、破坏调用约定、漏分支 —— 那些要读懂反汇编。

> ⚠️ **引用方式**：csproj 里 `<Private>False</Private>` 引用即可，**不拷输出、不需要 ILRepack**。

### 6.1c 用 Iced 写 stub 的四条实测规则

文档里没写、但**不遵守就一定出问题**的四条（本项目逐条踩过）：

1. ⚠️ **一个指令位置最多绑一个标签**。连续两次 `Label(ref a); Label(ref b);` 抛
   `ArgumentException: At most one label per instruction is allowed`。
   → 两个出口必须落在两条不同指令上。

2. ⚠️ **标签之后必须有指令**。在 stub 末尾绑「收尾标签」会抛
   `InvalidOperationException: Unused label end@3. You must emit an instruction after emitting a label.`
   → **不要建收尾标签**。
   > 这条对排查很有用：`Assemble` 对「漏绑 / 悬空标签」是**抛异常**而非返回 false，
   > 所以一定要 `TryAssemble` + `try/catch` 兜住 —— 否则一个 stub 写错会直接打断游戏启动。

3. ⚠️ **内存操作数的 DSL 不是 `dword_ptr(...)`**（Rust 版 Iced 是那样），而是：
   ```csharp
   AssemblerRegisters.__dword_ptr[rax + 0x14]   // 工厂是结构体实例，双下划线静态字段
   __qword_ptr[rcx + 0x30]
   __qword_ptr[label]                           // lea 取标签地址
   ```
   寄存器同样是静态字段（`AssemblerRegisters.rax`）。裸写 `rax` 需要
   `using static Iced.Intel.AssemblerRegisters;`，否则 `CS0103`。
   另：`Decoder` 在 `Iced.Intel` 与 `System.Text` 里**同名** ——
   同时有 `using System.Text;` 时必须写全 `Iced.Intel.Decoder.Create(...)`（否则 `CS0104`）。

4. ⚠️ **出口不要用绝对地址**（除非有特别理由）。`asm.jmp(0x180A8D8BC)` 能编码，但**不可重定位**
   —— stub 一旦被搬到别处（离线基准、原地热替换），跳转还指向旧地址。
   用**尾部绑定标签**（出口是「发射一段跳转」，不是回调）：
   ```csharp
   Label exitSkip = asm.CreateLabel("skip");
   asm.jne(exitSkip);                                              // 引用
   asm.Label(ref exitSkip); asm.ExitViaImm64(RuntimeVa(VaSkip));   // 尾部绑定
   ```
   > `mov r11,imm64`（11 字节）与 `lea r11,[label]`（7 字节）**都可行、输出逐字节相同**，
   > 且都**不读写标志位** —— 这点关键，因为 hook 点常落在 `jcc` 上，替换后紧接着要重判条件。
   > ⚠️ **用 `r11` 不要用 `rax`**（`rax` 中转在本项目真实崩过，见本文「关键纪律」）。

**⭐ 顺带拿到的收益 —— 免费自检**：标签最终地址由汇编器算，可把「预期出口」声明出来自动对账。
本项目在 `NativeHookBase.VerifyExits` 里扫描 stub 内的出口立即数，与子类 `ExpectedExits` 比对，
不一致就报 `ERROR` —— 把「出口跳错」从**玄学现象**（格子不亮 / AI 站到玩家头上）
提前成**安装日志里的一行**。

> 💡 **它第一次跑就拓到一只真虫子**：日志报
> 「出口指向 0x…，不在声明的出口集合里（`pass=0x0 skip=0x0`）」——
> 原因是预期值写成 `static readonly` 字段，而**静态字段初始化早于模块基址解析**，
> 那时 `RuntimeVa` 返回 0。改成**属性**即好。
> **教训：依赖「运行时已初始化状态」的值不能放在静态字段初始化里。**

> ⚠️ **Iced 只解决「编码」，不解决「选对 hook 点与出口」**。
> 本项目最大的两个坑（跳到路径中段 → 静默失效；跳过 `AroundGridHaveEnemy` → AI 站到玩家头上）
> 都不是编码问题。详见本文「关键纪律」。「能汇编」不等于「能挂钩」。

### 6.1d ★ 不透明字段的三步排查法（先做这三步，别猜）

遇到一个**你不明白含义的数值字段**（本项目实例：`GridUnitData.passes`）时，
先做下面三件事。**每一步都很快，而且能把「是不是枚举」「有哪几档取值」一次性答掉**。

#### 第一步：看 dump 里的**声明类型**

```bash
# dump.cs 是 Il2CppDumper 产物，一行就能答「是不是枚举」
grep -n "\bpasses\b" output/dumper_out/dump.cs
```

```csharp
private GridType gridType; // 0x14     ← 真 enum
public  int      passes;   // 0x20     ← 普通 int
```

> 💡 **对比旁边同一结构体的其他字段**。本项目里 `gridType` 是 `enum`（`{None,Normal,Obstacle}` = 0/1/2），
> `passes` 是 `int` —— 同一张表里两行就把结论说清楚了。
> **一个字段是 enum，不代表它旁边的也是。**

#### 第二步：运行时间 **typeKind**（区分「枚举」与「刚好取值像枚举的 int」）

比名字更硬：IL2CPP 自己带类型分类码。

```csharp
var K   = IL2CPP.GetIl2CppClass("Assembly-CSharp.dll", "", "GridUnitData");
var fld = IL2CPP.il2cpp_class_get_field_from_name(K, "passes");
var ft  = IL2CPP.il2cpp_field_get_type(fld);

IL2CPP.il2cpp_type_get_type(ft)        // 8  = IL2CPP_TYPE_I4（普通 int）
                                       // 17 = IL2CPP_TYPE_VALUETYPE（枚举走这条）
IL2CPP.il2cpp_field_get_offset(fld);   // 字段偏移（顺手交叉验证 dump）
```

实测对照：

| 字段 | `typeKind` | `class_from_type` 的名字 | 结论 |
|---|---|---|---|
| `gridType` | **17** | `GridType` | 枚举 |
| `passes` | **8** | `Int32` | 普通 int |
| `row` | **8** | `Int32` | 普通 int |

> 用 `IL2CPP.il2cpp_type_get_name(ft)` 能直接拿到人读的类型名。
> 注意 `il2cpp_field_get_offset` 返回 **`uint`**（不是 `int`）—— 写代码时要显式转换，否则 `CS0266`。

#### 第三步：全量**取值直方图**（看语义分档）

把整张地图/整个对象的取值数一遍。**一步就能看出「它是不是按类别整体赋值」**。

```csharp
var hist = new Dictionary<int,int>();
for (int r = 0; r < map.mapHeight; r++)
for (int c = 0; c < map.mapWidth;  c++) {
    var g = map.GetGridData(r, c);
    if (g == null) continue;
    hist[g.passes] = hist.GetValueOrDefault(g.passes) + 1;
}
```

本项目实测（**原版、未修改状态**）：

| 类别 | 格数 | `passes` 取值 | 不同取值数 |
|---|---|---|---|
| `normalGrids` | 357 | 全部 `15` | **1** |
| `obstacleGrids` | 43 | 全部 `0` | **1** |

→ 每类**只有 1 个取值**，这就是决定性证据：**它是「按类别整体赋值」的类别标志，
不是逐格属性**。后续「改一个格的 passes 会不会影响别的格」这类问题直接不用猜了。

#### ⚠️ 三步能做什么、不能做什么

| 能 | 不能 |
|---|---|
| 确定**是不是枚举**（第一步 + 第二步） | 确定字面量**是什么含义**（`15` 为什么是 15？没线索） |
| 拿到**全部取值**与**分布**（第三步） | 发现字段的**隐藏副作用** |
| 判断「逐格属性」还是「类别标志」（第三步） | 替代读代码 |

> ⚠️ **本项目真实教训**：这三步能在几分钟内告诉你「`passes` 是普通 `int`、按类别赋值、
> 只有 0 和 15 两档」，**但不会告诉你 `Navigate` 拿它当搜索深度上限（`row × passes`）**。
> 那个副作用只有**读反汇编**才能发现。
>
> 所以：**类型信息用来「排除错路」和「缩小范围」，不能拿来「得出正解」。**
> 先做这三步避免猜错方向，再去读代码/反汇编定论。

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
