# Harmony 补丁 IL2CPP 方法

> 归属：[`AGENTS.md`](../AGENTS.md) §5 的详细展开。
> **本项目最大的坑区** —— 挂载成功、触发、生效是三件不同的事。

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

### 5.3b ⚠️ 用 `TryPatch` 挂 `bool Prefix(...)` 会报「透传 postfix」错

**症状**（本项目 2026-10 真实踩到）：把一个返回 `bool`、首参是 `__instance` 的方法
用 `_harmony.Patch(target, postfix: …)` 挂上，Harmony 报：

```
HarmonyLib.InvalidHarmonyPatchArgumentException:
  (static bool MyMod.Plugin::X_Prefix(X __instance, GridUnitData targetGrid)):
  Return type of pass through postfix ... does not match type of its first parameter
```

**成因**：Harmony 把「**返回 `bool` 且首参类型 = `__instance` 类型**」这个形态
优先解释成了**透传 postfix**（pass-through postfix）—— 即「postfix 的返回值本身就是
原方法的返回值，直接透传」。而 `GenerateMovePath` 返回 `void`，于是类型对不上。

**修法**：把挂载方式改成 `TryPatchPrefix`（= `_harmony.Patch(target, prefix: …)`）。
**同一个签名在 prefix 下完全合法，在 postfix 下必然报错。**

> 💡 **报错里的 `postfix` 二字就是线索**。看到 `pass through postfix` 而你想写的明明是
> prefix，**先查挂载方式，别去改签名** —— 改签名（比如把返回值改成 `void`）会真的
> 失去「跳过原方法」的能力，把一个小失误变成功能性 bug。
>
> 这也是「同一个方法名、只差一个词」这类错误的典型：`TryPatch` / `TryPatchPrefix`
> 长得极像，挂错了只会在运行时报这种拐弯抹角的错误。

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
