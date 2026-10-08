# HeroVitalsFix —— 修复「切换人物后三维显示不刷新」

> # ⚠️ 已被官方修复取代（2026-10-08）
>
> 游戏更新 `ac0ec1a3064d0b82f9e7b5adda628232` 已自行修掉这个 bug（修法与验证见 §5 / §7），
> **本 mod 已从 `Mods/` 删除，且不要再启用**。本文保留的是：
> - 一套**可复用的发布方法论**：官方可能自修的 bug，补丁应该怎么做（§3 指纹门控）；
> - 一次**完整的“验证官方修复”实录**（§7）—— 包括怎么排除“看起来好了”的假阳性。
>
> 当时代码仍在仓库（`HeroVitalsFix/`），仅供参考；**它对新构建已经挂不上了**（签名已变）。
> bug 本身的机制（字段语义、调用链、实测数据）在
> [`game-internals.md`](game-internals.md) §4。

---

## 1. 修的什么

游戏里每条「生命 / 内力 / 体力」bar 都由 `HeroData.SetHpBar/SetMpBar/SetPowerBar(GameObject)`
绘制。切过一次角色之后**再切回来**，守卫会误判「控件上已经是我的值」而提前返回，
于是三条 bar 保持**上一个人物**的数值。

两条复现路径（都已实测）：

| 路径 | 入口 |
|---|---|
| 角色详情面板点另一个角色 tab | `HeroDetailTabController.OnClick` → `HeroDetailController.FreshNowHeroDetail` |
| 战斗中切换当前操作角色 | `BattleController.RefreshActiveUnitUI` |

其中**体力**最容易被看到：守卫只在「数值变了」时才发现异常，生命/内力在游玩中频繁变化会自愈，
体力不常变 ⇒ 缓存永远"自认正确"。

机制细节、实测对照表、以及「怎么判断官方是否已修」→ [`game-internals.md`](game-internals.md) §4。

---

## 2. 怎么修的

在三个 setter 上挂 **Prefix**（不是 Postfix —— 必须赶在游戏自己的守卫判断之前）：

```
HeroData.SetHpBar(GameObject)    ─┐
HeroData.SetMpBar(GameObject)     ├─ Prefix：若「该控件上次是别的角色写的」⇒ 清掉本角色的记账
HeroData.SetPowerBar(GameObject) ─┘            （于是游戏自己的守卫不再跳过，用它自己的原生逻辑+格式重画）
```

### 2.1 为什么不在「切人入口」清缓存

也想过在 `FreshNowHeroDetail` / `RefreshActiveUnitUI` 的 Prefix 里清 —— 那样**不需要跨调用状态**。
没选它，因为：

- 战斗侧的入口不确定：`RefreshActiveUnitUI` 的调用者分析器只报 `RunBattle` 一处，
  真正「切人」是否经过它**没有证实**（`SetNowActiveUnitUI` 等也有嫌疑）。
- 挂 setter 不依赖"哪个入口"这个未证实的前提，**任何**路径换控件都会被覆盖。

代价是需要一个「控件 → 上次写入者」的表（见 2.3）。

### 2.2 安全性：这个补丁的动作是**单调**的

本补丁唯一做的事是**清空一个缓存字段**。它只会让游戏**多写**一次，
**不可能**让它**少写**。所以：

- 占用者表过期（instanceID 被回收）最多 ⇒ 多清一次 ⇒ 多余一次重绘；
- 官方以后自己修了 ⇒ 我们最多让它多画一遍。

**不会**因为我们的表出错而产生错误数值。这条性质是选这个方案的主要理由。

### 2.3 占用者表

`Dictionary<int, Entry>`：键 = 控件 `GetInstanceID()`，值 = 上次写入它的 `HeroData` 指针 + 它在 LRU 链上的节点。

- **容量硬上限 1024 条，满了按 LRU 淘汰一条** ⇒ **不会无限膨胀**（上限内存约几十 KB）。
  实测一个正常会话（详情面板 + 若干场战斗）约 20~30 条。
- **为什么用 LRU 而不是整体 `Clear()`**：过期条目只会造成「多清一次缓存」（多余重绘，见 §2.2），
  本身无害；但整体 `Clear()` 会让**所有**控件在一小段时间内同时失去保护 ——
  万一此刻正切人，就会出现「这一次没修」。LRU 只淘汰最久没用过的条目，
  而真正共享的那 6 条（详情面板 3 + 战斗 `NowActiveHero` 3）一直在被触碰，不会被淘汰。
- **不需要加锁**：`Set*Bar` 的调用者全是 `Update` / UI 回调，都在 Unity 主线程。
- 是否有泄漏、有没有真的触发，都可以在活进程里反射核对（见 §4.3）—— 不要靠推测。
### 2.3b 为什么它不会泄漏（也不该用弱表）

**这张表不持有任何游戏对象的引用。** 值是 `(IntPtr 原生地址, int heroID)`，键是 `int` 实例 id：

- `IntPtr` 只是**地址的数值** —— GC 不把它当引用，所以它不会阻止任何 `HeroData` / `GameObject` 被回收。
- 表本身有硬上限（1024），且只引用**它自己的**链表节点。

**反过来说，这里正是不能存托管引用的地方**：Il2CppInterop 的 `HeroData` 代理会为原生对象保留
`GCHandle` —— 存代理就等于把那个角色**钉住**。战斗中会产生大量临时角色，那才会变成真泄漏。

**C# 有弱表吗？** 有 —— `System.Runtime.CompilerServices.ConditionalWeakTable<TKey, TValue>`（键弱、值强）。
但它在这里**不适用**：

| 问题 | 说明 |
|---|---|
| 键的身份不稳定 | 它按**托管对象引用**相等判断；而 Il2CppInterop 的代理是池化的，同一个原生对象可能拿到不同代理实例 ⇒ 查表 miss ⇒ 补丁**静默失效** |
| 值仍然保活 | 键存活期间 `ConditionalWeakTable` **强引用**值；存 `HeroData` 一样会把角色钉住 |
| 不是 Unity 语义 | 对已销毁的 `UnityEngine.Object`，它用引用相等而非 Unity 重载的 `==`，销毁对象的代理会赖着 |

结论：这里**不需要**弱引用，因为压根没存引用。

> ⚠️ 用 `IntPtr` 的代价：地址只是数值，**理论上**会被复用（对象回收后新对象拿到同一地址）。
> 那会让我们把新角色误判成「还是他」，从而**漏掉一次**失效（表现为 bug 闪现一次，下一次写入自愈）。
> `heroID` 一起比就是为这个加的保险；「正确性最好」的做法是把状态记在控件上，
> 但要付每次 `GetComponent` 的代价（见 §2.4 的性能取舍）。

### 2.4 性能

只在「同一控件换角色」时触发一次。同一角色重复调用仍走游戏原本的提前返回 ——
**`HudController.Update` 那条每帧路径不受影响**（这条路径正是游戏加缓存的理由，不能被我们破坏）。

---

## 3. 构建指纹门控（发布运维）

**动机**：官方很可能顺手修掉这个小 bug，而 Steam 会随时更新。
一个会持续对已修版本动手的补丁是负担，所以本 mod **默认 fail closed**：
只在「已知有 bug 的构建」上生效，其它构建一律不挂补丁。

指纹 = **`GameAssembly.dll` 的 md5**（用 `FileShare.ReadWrite` 读，它已被加载为模块）。
同时把 `global-metadata.dat` 的字节数打进日志备查。

已知存在该 bug 的构建（= 默认允许列表，写在 `Plugin.KnownBadBuilds`）：

```
ea039aad075f14316a07016ba3625036   # GameAssembly.dll 33661952 字节 / metadata 7959028 字节
```

### 3.1 配置项（`UserData/MelonPreferences.cfg` → `[HeroVitalsFix]`）

| 键 | 默认 | 说明 |
|---|---|---|
| `build_gate` | `AllowList` | `AllowList` 只给列表内构建打补丁；`BlockList` 除屏蔽列表外都打；`Off` 无条件打 |
| `allowed_builds` | 上面那串 md5 | 逗号/分号分隔的 md5 列表 |
| `blocked_builds` | 空 | 仅 `BlockList` 用 |
| `diagnostics` | `false` | 打开后每次「强制失效」打一行；关闭时仍计数 |

### 3.2 游戏更新后的三步处理

1. 启动日志会出现：
   `[指纹] 该构建不在放行范围内（BuildGate=AllowList）—— 本 mod 已停用，未挂任何补丁。`
   同时打印**当前构建指纹**。
2. **先测现象**：详情面板点另一个角色 → 切回来，三维是否跟随？（A→B→A，见 §4.2）
3. **官方已修** ⇒ 删掉本 mod；
   **仍有 bug** ⇒ 把日志里那串指纹填进 `allowed_builds`（发布新版本时追加到 `KnownBadBuilds` 并重新构建）。

---

## 4. 构建、部署与验证

### 4.1 构建部署

```bash
dotnet build HeroVitalsFix/HeroVitalsFix.csproj -c Debug
cp HeroVitalsFix/bin/Debug/net6.0/HeroVitalsFix.dll gamedir/Mods/
md5sum gamedir/Mods/HeroVitalsFix.dll HeroVitalsFix/bin/Debug/net6.0/HeroVitalsFix.dll   # 两边必须一致
```

⚠️ 改完代码**必须冷启动**（Harmony 补丁在 `OnInitializeMelon` 注册，且指纹在启动时读一次）—— 见 `AGENTS.md` §3.2.1。

### 4.2 验证清单

| # | 看什么 | 期望 |
|---|---|---|
| 1 | 启动日志 | `[自检] HeroVitalsFix vX 自身 md5 = …`（与部署核对） |
| 2 | 启动日志 | `[指纹] 当前构建指纹 = ea039aad…` |
| 3 | 启动日志 | 三条 `已挂载前缀补丁：HeroData.SetXBar(...)`，且 `patcher=Il2CppDetourMethodPatcher [IsValid=True]` |
| 4 | 启动日志 | `[自检] 三维刷新补丁：3/3 已挂载。` |
| 5 | 开 `diagnostics` 后切角色 | `[失效] <角色>(id=…) 的 <X> 条 —— 控件换角色，记账已清空（第 n 次）` |
| 6 | 现象（详情面板） | 记下 A 的三维 → 切到 B → **切回 A** ⇒ 三条 bar 跟随 A |
| 7 | 现象（战斗） | 战斗中切当前操作角色 ⇒ 体力跟随新角色 |

第 3 项的 `patcher` 一行很关键：**「挂载成功」≠「能触发」** ——
若显示的不是 `Il2CppDetourMethodPatcher`，补丁装在了托管转发上、原生调用碰不到。
可用 MCP 的 `hook_patch_info` 直接核对（实测输出：`patcherType=Il2CppDetourMethodPatcher`、
`patcherIsValid=true`、`prefixes=1`、`attached=true`）。除此之外，§4.3 的计数器是“真的跑了”的硬证据。
### 4.3 在活进程里核对（推荐）

`execute_csharp`（MCP）反射读本 mod 的几个 `static` 字段，就能直接证明「补丁在跑 / 表没涨」：

```csharp
// 注意：热重载可能同时加载多份程序集；逐份读，只有一份会非 0
var a  = AppDomain.CurrentDomain.GetAssemblies().First(x => x.GetName().Name == "HeroVitalsFix");
var pf = a.GetType("Unnamed42.HeroVitalsFix.Plugin");
var bo = a.GetType("Unnamed42.HeroVitalsFix.Plugin+BarOccupancy");
var forced = pf.GetField("_forcedInvalidations", BindingFlags.NonPublic | BindingFlags.Static).GetValue(null);
var dict   = bo.GetField("Map",   BindingFlags.NonPublic | BindingFlags.Static).GetValue(null);
var evict  = bo.GetField("_evictions", BindingFlags.NonPublic | BindingFlags.Static).GetValue(null);
var count  = dict.GetType().GetProperty("Count").GetValue(dict);
```

| 字段 | 含义 | 健康值 |
|---|---|---|
| `_forcedInvalidations` | 本补丁介入次数（只增） | 每次切人 −3 左右 |
| `BarOccupancy.Map.Count` | 已跟踪的控件数 | 远小于 1024（实测 20~30） |
| `BarOccupancy._evictions` | LRU 淘汰次数 | 长期为 0；非 0 也正常，只意味着控件数超过 1024 |

⚠️ 只读这些字段**不要写** —— 它们正是 [`game-internals.md`](game-internals.md) §4.6 说的「被判定逻辑读取的缓存」那类东西。

---

## 5. 与官方修复的关系（已发生）

**2026-10-08 的更新把问题自己修了。** 本 mod 的指纹门控按设计生效 —— 自动停用、未挂任何补丁：

```
[指纹] 当前构建指纹 = ac0ec1a3064d0b82f9e7b5adda628232（GameAssembly.dll 33669120 字节 / global-metadata.dat 7960432 字节）
[WARNING] [指纹] 该构建不在放行范围内（BuildGate=AllowList）—— **本 mod 已停用，未挂任何补丁**。
```

随即从 `gamedir/Mods/` 删除了本 mod。

**官方是怎么修的**（与我们的方案同一个思路，但分层更细）：

| | 本 mod（prefix） | 官方（`ac0ec1a3…`） |
|---|---|---|
| 失效什么东西 | 清 `shownXBarRoot` | 三个 setter 加 `bool force = false`；另有 `InvalidateBarCache()` |
| 什么时候失效 | **只在“控件换人”时**（占用者表判定） | **切人那一刻传 `force:true`** |
| 周期刷新 | 不动，仍走缓存跳过 | 不动，仍走缓存跳过 |
| 能不能改签名 | 不能（只能在固定签名上 patch） | **能**（持源码，随手加参数/加方法） |

> 📌 **这就是“持源码”与“patch 固定签名”的根本差异**：官方可以给三个 setter 直接加参数，
> 我们只能依赖确定的签名。所以签名一变，我们的补丁就**挂不上** —— 这恰好是指纹门控要拦的情况。
> 完整机制表见 [`game-internals.md`](game-internals.md) §4.7。

---

## 6. 已知限制

- 只覆盖「经 `HeroData.SetXBar` 写 bar」的路径。若有别处直接写 `Text`，本补丁不生效（**目前未观察到**）。
- 指纹用 `GameAssembly.dll` 的 md5：**官方任何一次更新都会让本 mod 停用**。这是刻意的（fail closed），
  不是缺陷；处理流程见 §3.2。
- 未做「启动时自动探测 bug 是否还在」：探针必须真的切一次角色（会动 UI / 骨架 / 物品列表），
  侵入性比 bug 本身更大，且 `FreshNowHeroDetail` 在面板未激活时**提前返回**、不能在启动阶段跑。

---

## 7. 验证官方修复的实录（2026-10-08）

> 这是一次“**验证别人（官方）修好了没有**”的完整方法记录，不是本 mod 的运行说明。
> 以后遇到“游戏更新后，我那个 bug 还在吗”可以直接照这套走。

### 7.1 先证明“我测的不是自己的补丁”

指纹门控的输出（见 §5）确认了补丁已停用。另外复核了三个独立证据：

- `list_patches --owner herovitalsfix` → **0 条**；
- `hook_patch_info SetPowerBar` → `attached:false`、`prefixes:0`，**入口字节是原始函数序言**（不是 detour 跳转）；
- 启动日志里**没有**「已挂载前缀补丁」行。

### 7.2 结构性核一：签名变了

`hook_patch_info` 的活进程签名直接给出：

```
Void HeroData.SetPowerBar(GameObject powerBarRoot, Boolean force)
```

⚠️ 这同时意味着：**本 mod 若被强行启用（`build_gate=Off`）也挂不上** ——
`TryPatchPrefix(..., parameterCount: 1)` 找不到 1 参重载，会退化成 «0/3 已挂载» 并打警告。
这是预期的优雅降级（见 `AGENTS.md` 一条纪律：挂载失败不许崩游戏）。

### 7.3 机制：不能只看 `[Calls]`

新旧 cpp2il 对比得出官方加了 `force` + `InvalidateBarCache()`，但：

> ⚠️ `bool force = false` 是**默认参数** —— `SetHpBar(go)` 与 `SetHpBar(go, true)`
> 编译出的签名**完全相同**。所以 `[Calls]` / `MemberParameters` **看不出调用点传了什么**。

要判定“是不是 `force` 在起作用”，只能**受控实验**：把目标角色的缓存设成
“这个控件 + 数值完全一致”（守卫必然命中），再分别调 `(W,false)` / `(W,true)`：

| 调用 | 结果 |
|---|---|
| `SetPowerBar(W, false)` | 未写 ⇒ **老守卫语义没变** |
| `SetPowerBar(W, true)` | 写了 ⇒ `force` 是真正的绕过开关 |
| `InvalidateBarCache()` | 三个 `shown*BarRoot` → `null` |

### 7.4 行为：怎么避免“看起来好了”的假阳性

**最大的陷阱**：数值变化（buff / 受伤 / 耗体力）会让守卫**失配而自愈** ——
现象看着正常了，其实只是那一次“跳过”恰好不成立。

所以两次切换都必须构造成**守卫必然命中**：

| 路径 | 怎么造“必然命中” | 结果 |
|---|---|---|
| 详情面板 | B 的缓存本来就命中（`shownPowerBarRoot=W`、`shownPower=power`），直接点 tab | 三项跟随 ✅ |
| 战斗 | 全员预置「缓存 = 战场控件 + 自身当前值」的**陷阱**，再让回合自然推进 | 两次都跟随新上场角色 ✅ |

排除过的三个干扰（这类实验的通用清单）：

1. **补丁本身** —— 见 §7.1；
2. **别处每帧调 `InvalidateBarCache`** —— 战斗里 `HeroIconController` 实例 **9 个全 `activeInHierarchy=false`**
   ⇒ `Update` 不跑 ⇒ 不可能靠它清缓存；
3. **每单位跟随条顺手写** —— 把非当前单位的缓存设成**假控件**，过一段时间读回**仍是原值**
   ⇒ 没有任何代码在动它，陷阱成立。

### 7.5 操作层面的三个坑

- ⚠️ **战斗是回合制，无法“即时切人”** —— 用「预置陷阱 + 让回合自然推进」代替。
- ⚠️ **点击已经选中的 tab 是空操作**（`OnClick` 不重画）—— 探针必须**真的换人**，
  否则会把“没反应”误读成“没修”。
- ⚠️ `BattleController.RefreshActiveUnitUI()` **不是切换路径**，它是**周期刷新**（实测 `force=false`）；
  直接调它只能测“缓存命中会不会写”，**不能**代替真实切人。
