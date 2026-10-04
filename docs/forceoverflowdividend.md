# ForceOverflowDividend —— 修复记录（第三方 mod）

## 0. 这是什么

**别人的 mod**（作者 `SaintCirno9`），原仓库无源码，只有 `Mods/ForceOverflowDividend.dll`。
功能：门派资源每次变动后检查溢出，把溢出部分按资源类型折现成钱，
再按门派成员**人口权重**（`2^clamp(heroForceLv,0,5)`）分配给成员。

本仓库内的 [`ForceOverflowDividend/`](../ForceOverflowDividend/) 是**从反编译重建的源码**
（不是原作者代码，是按 DLL 的 IL 语义重写的等价实现 + 修复），用于让这个 mod 能继续维护。

> ⚠️ **本文是「项目文档」**，记的是**这一个 mod 修了什么**（具体签名、具体补丁、具体部署陷阱）——
> 这些**随游戏更新即失效**。
>
> **反编译重编的通用流程**（工具、命令、踩坑、为什么那样做）在
> [`mod-recompilation.md`](mod-recompilation.md)，那份**不随游戏构建变**。
> 再碰到无源码的 mod，看那份。

## 1. 报错与根因

用户看到的日志：

```
[ERROR] [ForceOverflowDividend] 处理资源变动后的门派资源溢出折现失败：
System.MissingMethodException: Method not found: 'System.String Il2Cpp.ForceData.GetForceName()'.
   at ForceOverflowDividend.ForceOverflowRuntime.SafeForceName(ForceData force)
   at ForceOverflowDividend.ForceOverflowRuntime.HandleForceResourceChanged(...)
   at ForceData_ChangeResource_Int_Patch.Postfix(...)
```

**根因：游戏更新改了 `ForceData.GetForceName` 的签名。**

| | 签名 |
|---|---|
| 原 mod 编译时 | `System.String ForceData.GetForceName()` —— 无参 |
| 当前游戏（实测 2026-10-04） | `System.String ForceData.GetForceName(Boolean replacedForce = true)` |

mod 的 IL 里编译期绑定的是**无参**版本。运行时该签名不存在 → 每次调用抛
`MissingMethodException`。实测该类型上 `GetForceName` **只有带 `bool` 的那一个重载**。

> 细节与通用写法见 [`game-internals.md`](game-internals.md) §2.2a。

### 1.1 ⚠️ 为什么 `try/catch` **没兜住**（这是理解本 bug 的关键）

原代码确实有 try/catch，但它**从来没生效过**：

```csharp
private static string SafeForceName(ForceData force)
{
    try   { return force.GetForceName() ?? "(未知门派)"; }   // ← 死代码
    catch { return "(未知门派)"; }
}
```

栈顶那两帧就是证据 —— `SafeForceName` **在栈帧上**，且栈里**没有一行方法内部的东西**：

```
at ForceOverflowDividend.ForceOverflowRuntime.SafeForceName(ForceData force)
at ForceOverflowDividend.ForceOverflowRuntime.HandleForceResourceChanged(...)
```

**原因：`MissingMethodException` 是 JIT 期异常，不是运行时异常。**
源码里的 `call` 指令指向 `Il2Cpp.ForceData::GetForceName()` 这个**已不存在的令牌**，
CLR 在**编译 `SafeForceName` 方法体时**就要解析它。此时方法还没开始执行，
**`try` 保护区域尚未建立** —— 于是异常直接从方法帧上抛出，`catch` 连一次机会都没有。

活进程实测（拿真实 `ForceData` 调旧版 `SafeForceName`）：

```
异常从方法**内部逃逸**出来: MissingMethodException

抛出异常的原始 StackTrace:
   at ForceOverflowDividend.ForceOverflowRuntime.SafeForceName(ForceData force)
```

异常逃逸路径：`SafeForceName` → `HandleForceResourceChanged`（无 try）→ `Postfix`。
**日志里那条 ERROR 来自 `Postfix` 的 catch，不是 `SafeForceName`**：

```csharp
// ForceData_ChangeResource_Int_Patch.Postfix
catch (Exception value)
{
    Main.Log.Error($"处理资源变动后的门派资源溢出折现失败：{value}");
}
```

| 位置 | 能否兜住本次异常 | 原因 |
|---|---|---|
| `SafeForceName` 内部 | ❌ **兜不住** | JIT 期抛出，`try` 区域未建立 |
| `Postfix` 那层 | ✅ 兜住了 | 它是**调用方**，拿到时已是普通运行时异常 |

> **教训（通用）**：`try/catch` 只能兜住**方法开始执行之后**的异常。
> 对「调用了不存在的成员」这类**绑定失败**（`MissingMethodException` /
> `MissingFieldException` / `TypeLoadException`），**在同一方法内**包 try/catch
> **无效** —— 必须从**调用方**包，或者改成**反射调用**（拿不到就返回 `null`，
> 从根上不产生这个异常）。这也是为什么本次修复做在反射上，而不是再补一个 `catch`。

### 1.2 实际症状：既发钱、又报错

异常炸穿的是**整个 `HandleForceResourceChanged`**，但发生位置很关键：

- `DistributeMoney(...)` 在 `SafeForceName` **之前**就执行完了 → **折现的钱确实发出去了**；
- 之后拼日志时 `SafeForceName` 抛异常 → 该次结算的 `LogVerbose` 被跳过，并向上冒泡成 ERROR；
- `Postfix` 的 catch 把它吞掉 → **游戏不会崩**，只是每结算一次刷一条 ERROR。

所以准确的说法是：**折现动作生效了，但每次结算都伴随一条异常日志**。
这里容易误判成「报错的是日志装饰、主逻辑没事」——实际上异常炸穿了
整个 `HandleForceResourceChanged`，只是恰好 `DistributeMoney` 排在它前面才没事。

## 2. 修了什么

### 2.1 主修复：签名漂移免疫

不再编译期绑定 `GetForceName`，改为**运行时反射探测**当前存在的重载
（优先 `(bool)`，其次无参，都没有就退化为直接读 `forceName` 字段）。

反射的好处是**不会因签名不符而抛异常** —— 找不到就返回 `null`。
所以下次游戏再改签名，最多退化成「少一个门派名」，而不是每次调用炸一次。
`SafeForceName` 另外加了「警告只报一次」的节流，避免刷屏。

### 2.2 其余审计：确认只有这一处过期

把 mod 调用的**所有**游戏 API 逐一对照活进程核实过：

| mod 用到的成员 | 现状 |
|---|---|
| `ForceData.ChangeResource(int,float,bool,bool)` | ✅ 存在（3 个重载都在） |
| `ForceData.ChangeResource(List<float>,bool,bool)` | ✅ |
| `ForceData.ChangeResource(List<ResourceData>,bool,bool)` | ✅ |
| `ForceData.resourceStore` / `resourceStoreMax` | ✅ |
| `ForceData.GetOwnHeros()` / `GetLeader()` | ✅ |
| `HeroData.heroID` / `heroName` / `dead` / `heroForceLv` / `belongForceID` | ✅ |
| `HeroData.ChangeMoney(int,bool)` | ✅ |
| `GlobalData.ResourceName` / `ResourceValue` | ✅ |
| `WorldData.Player()`、`GameController.Instance.worldData` | ✅ |
| **`ForceData.GetForceName()`** | ❌ **签名已变 → 本次修复** |

### 2.3 顺带修的两个小问题

- **构建指纹原本打不出来**：启动日志里 `构建 unknown`。现在打印**静态版本号 + 自身 md5**。
  这是本项目「跑的是旧产物」教训的直接应用 —— 一眼分辨跑的是哪个产物。
  > 中间曾用 `AssemblyInformationalVersion`（csproj 的 `BuildStamp`）携带构建时刻，
  > **已废弃**：时间戳是程序集内容，会破坏 `<Deterministic>`，
  > 使同源码两次构建 md5 不同（见 AGENTS.md §3.2.0）。
- **`SafeForceName` 的兜底**：反射和 `GetForceName` 都失败时，降级读
  `ForceData.forceName` 字段（字段不受方法签名变化影响）。

## 3. 重建源码时踩到的坑

**游戏侧集合类型与托管集合类型同名**，两个命名空间都 `using` 会 `CS0104`
（`List<T>` / `Dictionary<K,V>` 不明确引用）。

```csharp
using System.Collections.Generic;            // 托管集合（mod 自己的）
using Il2Cpp;
using GameFloatList        = Il2CppSystem.Collections.Generic.List<float>;
using GameStringList       = Il2CppSystem.Collections.Generic.List<string>;
using GameHeroList         = Il2CppSystem.Collections.Generic.List<Il2Cpp.HeroData>;
using GameResourceDataList = Il2CppSystem.Collections.Generic.List<Il2Cpp.ResourceData>;
```

两个要点：

1. **C# 别名不能带泛型参数**（`using GameList = ...List<>;` 不合法），
   只能按实际用到的元素类型逐个闭合声明。
2. **别名右侧要用完全限定名**（`Il2Cpp.HeroData`）。别名的解析先于
   `using Il2Cpp;` 生效，写短名会 `CS0246`。

另外：Harmony 补丁方法的参数类型**必须精确匹配**被补丁方法的签名，
所以三个 `ChangeResource` 重载各自用别名钉死，不能图省事混用。

## 4. 验证

1. 构建 0 warning / 0 error；部署后 `md5sum` 两边一致。
2. 活进程核实 `ForceData` 上 `GetForceName` 只有 `(Boolean)` 一个重载。
3. **复现原异常**：拿真实 `ForceData` 调用旧版 `SafeForceName` →
   抛出与用户日志**逐字一致**的
   `System.MissingMethodException: Method not found: 'System.String Il2Cpp.ForceData.GetForceName()'.`
4. **修复版对照**：同一批真实门派数据，修复版返回正确名字
   （`长乐帮 / 唐门 / 药王谷 / 丐帮 / 飞龙门`）。
5. 三个 Harmony 补丁都正确绑定到 `ChangeResource` 的对应重载
   （`hook_patch_info`：每个都是 prefix 1 / postfix 1）。
6. **证明 `catch` 无效**：抛出的异常栈**就停在** `SafeForceName` 帧上
   （方法内部一行都没有）→ 证实它是 JIT 期抛出、`try` 区域未建立。

## 5. ⚠️ 一个部署陷阱：HotReload 会让同一 mod 并存多份程序集

实测热重载后进程里同时存在 **3 个** `ForceOverflowDividend` 程序集：

```
ver=1.0.0.0  (游戏启动时加载的旧版，仍驻留)
ver=1.0.1.0  (热重载加载的修复版)
ver=1.0.1.0  (再次热重载)
```

**后果**：用 `AppDomain.CurrentDomain.GetAssemblies().First(...)` 取到的是**第一个**，
很可能是**旧版**，于是“修复没生效”的假象就出现了 —— 而实际原因只是取错了程序集。

**这一条要记住**：热重载后想验证「新版是否生效」，必须按**版本号**或按
**只有新版才有的方法**（本例是 `ResolveForceNameGetter`）来定位程序集，
不要用 `First(...)`。最干净的验证仍然是**冷启动**。

## 6. 产物

| 路径 | 说明 |
|---|---|
| [`ForceOverflowDividend/`](../ForceOverflowDividend/) | 重建的源码工程（4 个文件） |
| `output/tmp-mcp/ForceOverflowDividend.dll.orig.bak` | 原 DLL 备份 |
| `output/ForceOverflowDividend_decomp/` | 原 DLL 的反编译（诊断用） |
