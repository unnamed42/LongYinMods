# HeroVitalsFix —— 修复「切换人物后三维显示不刷新」

> ⚠️ **本 mod 由构建指纹门控，游戏一更新即自动停用**（详见 §3）。
> 被修的那个 bug 的**机制**（字段语义、调用链、实测数据）记在
> [`game-internals.md`](game-internals.md) §4，本文只讲**这个 mod 自己**的设计与运维。

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

## 5. 与官方修复的关系

| 官方怎么修 | 本 mod 的结局 |
|---|---|
| 改了任何游戏代码 | 指纹变了 ⇒ **自动停用**（§3.2） |
| 只改 native 守卫、保留 `shown*` 字段 | 指纹变了 ⇒ 自动停用 |
| 什么都不改 | 继续生效 |

即使指纹门控被用户用 `build_gate=Off` 绕过，最坏后果也只是"多画一次"（§2.2），**不会**造成错误数值。

**建议**：把 A→B→A 的复现 + 根因（记账缓存挂在 `HeroData`（每角色一份）上，
而详情面板 / 战斗的 bar 是**共享控件**）报给开发者 —— 修法是一两行
（切人时清缓存，或把记账挪到控件上）。

---

## 6. 已知限制

- 只覆盖「经 `HeroData.SetXBar` 写 bar」的路径。若有别处直接写 `Text`，本补丁不生效（**目前未观察到**）。
- 指纹用 `GameAssembly.dll` 的 md5：**官方任何一次更新都会让本 mod 停用**。这是刻意的（fail closed），
  不是缺陷；处理流程见 §3.2。
- 未做「启动时自动探测 bug 是否还在」：探针必须真的切一次角色（会动 UI / 骨架 / 物品列表），
  侵入性比 bug 本身更大，且 `FreshNowHeroDetail` 在面板未激活时**提前返回**、不能在启动阶段跑。
