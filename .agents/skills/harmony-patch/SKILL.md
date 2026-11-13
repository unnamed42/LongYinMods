---
name: harmony-patch
description: 当要给龙胤立志传的 IL2CPP 托管方法挂 Harmony 补丁（Prefix/Postfix），或排查「补丁说挂上了但不生效」时使用。覆盖重载选择、Patch 类型、__state、挂载与触发的分离验证、探针污染。
whenToUse: 写 Harmony 补丁、挂临时探针、确认补丁是否真的触发、遇到 Patch 报错但行为异常时。
---

# 给 IL2CPP 方法挂 Harmony 补丁

采集、改写还是探针，都走同一条链路。**本项目的最大坑区：挂载成功、触发、生效是三件不同的事。**

**完成判据**：日志里有**你自己代码打的、不受诊断开关门控的**运行时日志，且行为可观测。

## 1. 选目标：必须显式选重载

`Type.GetMethod(name)` 在同名重载下返回哪一个**不确定**。真实案例：同一类型同时有
`void Foo()`（空桩）和 `void Foo(Bar)`（真实现）。

```csharp
MethodInfo target = methods.First(m => m.GetParameters().Length == parameterCount);
Log.Msg($"已挂载补丁：{type.Name}.{target} (IL={target.MethodHandle.GetFunctionPointer().ToInt64():x}) -> {patchName}");
```

**只打「挂载成功」没用** —— 挂到空桩和挂到真实现，日志一模一样。打印**真实绑定的签名 + IL 地址**。

## 2. Prefix 和 Postfix 是两个独立 patch

`TryPatch` 挂 Postfix、`TryPatchPrefix` 挂 Prefix。别以为挂了一个等于两个都挂。

```csharp
HarmonyLib.Public.Patching.PatchManager.GetPatchInfo(m).postfixes.Count()
```

## 3. ⚠️ `bool Prefix` 不能用 `TryPatch` 挂

Harmony 把「返回 `bool` 且首参类型 = `__instance`」优先解释成**透传 postfix**，而原方法返回 `void` 时类型对不上：

```
InvalidHarmonyPatchArgumentException: Return type of pass through postfix ... does not match type of its first parameter
```

**修法：改成 `TryPatchPrefix`。同一个签名在 prefix 下完全合法。**

> 看到 `pass through postfix` 而你想写的是 prefix，**先查挂载方式，别改签名** ——
> 改签名会真的失去「跳过原方法」的能力。

## 4. `__state` 传 per-call 值

官方语义：`__state` 只在 **Prefix 与 Postfix 定义在同一个类**时有效（Harmony 用声明类型做 key）。
**不要用静态字段传 per-call 值** —— 被逐格连续调用的方法会串味。

```csharp
internal static void Target_Prefix(TargetType __instance, out MyState? __state) { __state = new MyState(...); }
internal static void Target_Postfix(TargetType __instance, MyState? __state) { /* 用 __state */ }
```

## 5. 三层验证：挂载 ≠ 触发 ≠ 生效

1. **挂载** —— `Patch()` 不抛异常。
2. **触发** —— 有运行时触发日志，且该日志**不能被诊断开关门控**。
3. **生效** —— 行为可观测。

> ⚠️ **「日志没打印」≠「代码没执行」**：先查该日志是否被门控。
> 本项目曾因登记日志受 `Diagnostics` 门控而 `grep -c` = 0，误判「零触发」，白耗一轮。

## 6. IL2CPP 特有陷阱

| 现象 | 真相 | 做法 |
|---|---|---|
| 运行时入口是 `ff 25 <disp32>` | Il2CppInterop 已装 detour，**不是故障** | 别再叠自己的 detour，用 Harmony |
| `MethodHandle.GetFunctionPointer()` 地址远离模块 | IL2CPP 代理返回的是**托管包装地址** | 可用来区分重载，**不能**判断补丁会不会触发 |
| `new StackTrace()` 没有 native 帧 | IL2CPP 下拿不到调用者 | 要 native 调用者得走原生手段 |

> ⚠️ 不要因为入口是 `ff 25` 就断言「Harmony 挂的是托管包装、原生调用绕过了它」——
> 这个结论是**错的**，本项目在错误方向上浪费过一轮。

想确认 patcher 是否真的接管了，把运行时类型和 `IsValid` **打出来**：

```csharp
var patcher = HarmonyLib.Public.Patching.PatchManager.GetMethodPatcher(m);
Log.Msg($"patcher={patcher.GetType().Name} valid={patcher.GetType().GetProperty("IsValid")?.GetValue(patcher)}");
```

## 7. ⚠️ 空的 `[HarmonyPatch]` 每次启动报 ERROR

光秃秃的 `[HarmonyPatch]`（没带 `typeof` / 方法名）+ 类里有个像补丁的方法 → `PatchAll()` 扫到但没东西可绑：

```
Failed to HarmonyInit PatchAll: ... Undefined target method for patch method
```

**这不是 IL2CPP 问题，Mono 下同样复现。** 修法：

- 手挂模式（运行时反射 + `HarmonyInstance.Patch`）→ **删掉那个 `[HarmonyPatch]`**；
- 想自动挂 → 给具体目标 `[HarmonyPatch(typeof(X), "Method")]` + `[HarmonyPrefix]` / `[HarmonyPostfix]`。

定位：`grep -rn "\[HarmonyPatch" --include=*.cs`；IL 层核实 `monodis --customattr MyMod.dll | grep -i HarmonyPatch`。

## 8. ⚠️ `Patch()` 报错也可能已经挂上了（探针污染）

`Harmony.Patch()` 抛 `IL Compile Error (unknown location)`，但补丁**已注册**：`hook_patch_info` 里
`attached=true`、`patcherIsValid=true`。后果链：

1. 多次失败尝试**累积多个 prefix**；
2. 入口字节从 `ff 25 …` **退回原始序言**；
3. **原有补丁静默不再执行**。

于是探针在跑、真补丁没跑，得到**污染的测量结果**。

| 怎么发现 | 征兆 |
|---|---|
| `hook_patch_info` 的 `patchCounts.prefixes` | 数量多于预期 |
| 同工具的 `entryBytes` | 退回原始序言而非 `ff 25 …` |
| 自家代码的计数器停止增长 | 最直接的信号 |

```csharp
// ✗ UnpatchSelf() 会报同一个错
harmony.UnpatchSelf();
// ✓ 显式形式可行
harmony.Unpatch(methodInfo, HarmonyPatchType.Prefix, ownerId);
```

清完**必须再 `hook_patch_info` 核对一次**（数量回到预期 + `entryBytes` 恢复 `ff 25`）。

**纪律**：

- 临时补丁挂完立刻 `hook_patch_info` 核对，用完立刻显式卸掉。
- 看到「挂载失败」不要假定没挂上。
- **能用轮询就不要挂补丁**；需要每帧采样时，优先挂在你自己可控的地方。
- 任何影响目标方法的探针，先问：**它会不会改变我要观察的那个东西？**

## 收尾

- 结论与坑写进 `docs/<项目>.md`；框架用法拿不准**先 websearch**，别反编译框架。
- 细节与实测见 [`docs/harmony-il2cpp.md`](../../../docs/harmony-il2cpp.md)。
