# FriendlyNoclip — 战斗格子地图穿越友方

> ⚠️ **本文件的地址、偏移、metadata token、调用链结论全部绑定到当前游戏构建**
> （`GameAssembly.dll` 33661952 字节 / `global-metadata.dat` 7959028 字节 / IL2CPP metadata **v27** / Unity 2020.3.48f1c1）。
> **游戏一更新即失效**，需按 `AGENTS.md` §4 的流程重新推导。
>
> 通用工具用法见 `AGENTS.md`，不重复。

**状态**：✅ **两个功能均已实机确认可用**

- **穿友方**（用户 m01137 确认）
- **穿越己方城墙**（用户 m-confirmed：「已达成穿越城墙的效果」）

**核心实现已收敛**（2026-10-04）：不再依赖 hook 改「高亮」侧，
而是在战斗开局直接写 **`GridUnitData.passes`** —— 见 §2.2c。
> ⚠️ 本文件下面 §2.2b 保留了「用 `WallPassHook` 改 `Navigate` 判定」的推演过程，
> 那些探索**大部分已被 `passes` 方案取代**（这也是当时一直不生效的原因 ——
> 障碍格的 `passes=0` 使 `Navigate` 的搜索循环根本不执行）。
> **先读 §2.2c，再回头看历史推演。**
---

## 1. 目标

让战斗格子地图中**移动时允许穿越友方单位**。

起因（用户 m02592）：「一开始做这个mod是因为群战时一大群人挤在一起动弹不得，非常影响体验」。

### 硬约束

| # | 约束 | 来源 |
|---|---|---|
| 1 | 仅**战斗**格子地图，与大地图探索无关 | m00228 |
| 2 | **格子不能重叠站人**（原版有可重叠的"药丸"类元素，不可破坏）→ **不许改 `isEmpty()` 语义** | m00228 |
| 3 | **AI 也必须获得该能力**（原文：「我还是希望每个ai都能获得这个能力」） | m02592 |
| 4 | 敌方 AI 穿越其**自己**的友方可接受，但**不能破坏 AI 占位与行为** | m00793 |
| 5 | **只能穿越友方**（不是无差别穿越） | m02695 |
| 6 | 城防（`ObstacleData.teamID`）穿越：**暂缓** | m00566 |
| 7 | AI 不得自行安装工具 | m00045 |

> 约束 2 的直接推论：**要改的是「哪些格子进入移动范围」，不是「格子是否为空」。**
> 注意 `GridUnitData.isEmpty()` **不区分敌我**（callers=6），所以「友方阻挡」判定不在它里面。

---

## 2. 实现

### 2.1 开关一览（含已弃用的）

```toml
[FriendlyNoclip]
# ——— 功能开关（日常只需要这三个）———
native_detour = true         # ★ 能穿友方单位
wall_pass     = true         # ★ 能穿己方城墙（写 passes）
fix_occupancy = true         # ★ 穿越不留痕（写回被踩掉的登记）

# ——— 城防的两个原生 detour（二分级）———
wall_pass_hook      = true   # WallPassHook（改 Navigate）—— 安全，保留
wall_highlight_hook = false  # ★ 保持关闭：已验证会崩溃且非功能所需（§7.1c）

# ——— 诊断（平时关）———
diagnostics    = false
dump_all_grids = false
```

| 开关 | 作用 | 关掉会怎样 |
|---|---|---|
| `native_detour` | `Navigate` 内 `0x180a8d929` 的 detour，**允许穿过友方单位** | **穿不过友方** |
| `wall_pass` | 写 `GridUnitData.passes`（§2.2c），**允许穿过己方城墙** | 穿不过己方城墙 |
| `fix_occupancy` | `EnterGrid` Prefix + `OnLeave` Postfix，**写回被踩掉的登记** | 能穿，但**被穿的 NPC 点不动** |
| `wall_no_stop` | `BattleUnit.EnterGrid` Prefix，**城墙不可停留**（§2.2d） | **AI 会站到城墙上** |
| `fix_occupancy` | 同上钩子，**不得停在已占用的格子**（§2.2e） | **NPC 与箭塔等设施重叠** |
| `wall_pass_hook` | `Navigate` 内 `0x180a8d8b6` 的 detour | 不影响（已被 `passes` 方案覆盖） |
| `wall_highlight_hook` | 改 `GetMoveRangeGrids` 的两处判定 | **无影响，且它开着会崩** |

> ⚠️ **僵尸键**：`wall_ignore_team` 与 `wall_native_hooks` 是二分定位时期的开关，
> 已从代码删除，cfg 里的两条也**已手工清理**（2026-10）。
> 以后再遇到这类键直接删掉即可 —— MelonPreferences **不会自动清理已废弃的键**。

### 2.1b 日志策略：`diagnostics` 是**总开关**

**动机**（用户提出）：游戏**战斗很频繁** —— 本 mod 的钩子又挂在
**高频路径**上（逐格移动、每帧渲染类型），不控制量级会让 `Latest.log` 迅速膨胀。

实测确认：**单局就到 85 KB**，其中 `[穿越]` 261 行、`[命中]` 120 行、
`[禁停占用格]` 64 行、`[审计]` 28 行 —— 绝大多数是「每格都打」的噪音。

#### 两类分开

| 类别 | 走哪个 API | 受 `diagnostics` 控制？ |
|---|---|---|
| **信息性**（命中计数、拦截记录、审计、dump） | `Plugin.LogInfo(() => ...)` | ✅ **是** |
| **真正的错误**（安装被拒、分配失败、写回异常、汇编失败） | `Plugin.LogError(...)` / `Log.Warning` | ❌ **否，永不门控** |

**为什么错误不门控**：出错时必须看得见，否则排查会被误导
（本项目历史上就吃过「日志被门控 → 误判补丁没触发」的亏）。

#### 实现要点

判门控收到 `LogInfo` **一处**，而不是让 25 个调用点各自 `if (Diagnostics.Value)` ——
后者在新增日志时**必然漏掉**，那正是引入这次改动的起因。

`LogInfo` 接受 `Func<string>`（惰性），所以关闭诊断时**连字符串都不拼接**：

```csharp
Plugin.LogInfo(() => $"[穿越] EnterGrid 目标格(r{row},c{col}) ...");
```

`Diagnostics` 读的是 `.Value`，改 cfg 后**立即生效**，不需要重启。

> 启动横幅与「已挂载补丁」仍走 `LoggerInstance.Msg`（不经 `LogInfo`）——
> 它们每次启动只打一次，不在热路径上，而且**是确认「跑的是哪个产物」的关键**。

### 2.2 能力来源：「只穿友方」的原生 detour

拦截点在 `MapNavigator.Navigate`，它扩张邻居时有这条判定，**不区分敌我**：

```asm
0x180a8d913  mov  rcx, [rsi+0x18]   ; rcx = g.battleUnit
0x180a8d91a  je   0x180a8dc24       ; null -> il2cpp_raise_exception
0x180a8d920  xor  edx, edx
0x180a8d922  call 0x1808d6840       ; get_IsAlive
0x180a8d927  test al, al
0x180a8d929  jne  0x180a8da6b       ; ★ 存活 -> continue（跳过该邻居）★
```

这就是「被己方围住走不出去」的根因。

**为什么改这里**：`MapNavigator` 同时服务「范围高亮」和「点击寻路」，**只改一处不会出现「格子亮了却走不过去」的分裂**。

`FriendlyNoclip/FriendlyPassHook.cs` 在 `0x180a8d929` 处装 detour，比较 `selfTeamID`（`[rsp+0xd8]`）与 `g.battleUnit.battleTeam.ID`，**同队放行、异队跳过**。
hook 点常量：`VaHookSite = 0x180A8D929`、`VaSkip = 0x180A8DA6B`、`VaPass = 0x180A8D92F`。

### ★★ 修正（2026-10）：`FriendlyPassHook` 的出口也必须是 `0x180a8d92f`，不能是 `0x180a8d963`

**真机症状（用户报告，并出现了回归）**：装了穿友方之后，**AI 会直接站到玩家所在的格子上**。

**根因**：与 §2.2 的城墙 bug 是**同一类错误**。`0x180a8d963` 位于
`0x180a8d92f` 处那个「空格链」**已经跑完之后** —— 而空格链里包含
`AroundGridHaveEnemy(row, col, selfTeamID)`（`0x1808c41f0`，交战区 / 敌方邻接检查）。
跳到 `0x180a8d963` 等于**跳过占位与交战区判定**，于是寻路被告知
「队友所在的格子可以直接站上去」，AI 就把玩家的格子当成合法落点。

**职责边界**：本 hook 只回答「**这个格子上的人是不是队友**」，
**不回答**「**站到那个格子上合不合法**」。后者必须继续交给游戏。
所以「队友格」与「空格」应当**合流**到同一条链（`0x180a8d92f`），
而不是队友格走一条跳过检查的捷径。

**修正后的 stub**（60 字节，两出口）：

```
+0x00  test al, al
+0x02  je   <pass>        ; 无存活单位 -> 空格链
+0x04  mov  rax, [rsi+0x18]        ; g.battleUnit
+0x08  test rax, rax
+0x0B  je   <skip>        ; battleUnit == null
+0x0D  mov  rax, [rax+0x58]        ; battleUnit.battleTeam
+0x11  test rax, rax
+0x14  je   <skip>        ; battleTeam == null
+0x16  mov  eax, [rax+0x10]        ; battleTeam.ID
+0x19  cmp  eax, [rsp+0xd8]        ; selfTeamID
+0x20  je   <pass>        ; ★ 同队 -> 仍走空格链（不可直接接受！）
+0x22  skip  (0x180a8da6b)
+0x2F  pass  (0x180a8d92f)
```

> ⚠️ 旧文档曾写「把友方格交给 `AroundGridHaveEnemy` 判定，在混战中必然失败，已由实机验证」。
> **那个结论是在错误的出口地址下得到的，已推翻** —— 当时「失败」里混杂了
> 「逃掉交战区检查会把不可落的格子放行」这个因素。现在队友格与空格走同一条链，
> 职责单一，不再需要绕过任何检查。

### 2.2b 能力来源：「只穿己方城墙」的原生 detour

**城墙比「存活单位」判定更早被排除** —— 所以穿友方那套逻辑根本碰不到城墙（实测：装了穿友方之后，NPC 能穿、城墙依然不能穿，**敌我双方都不能**）。

拦截点在同一个 `Navigate` 内，但位置更前：

```asm
0x180a8d8a1  call 0x1808c8a50          ; GetGridDataByDir -> 邻格 g (rax/rsi)
0x180a8d8a9  test rax, rax
0x180a8d8ac  je   0x180a8da6b          ; g == null          -> skip
0x180a8d8b2  cmp  dword [rax+0x14], 2   ; g.gridType == Obstacle ?
0x180a8d8b6  je   0x180a8da6b          ; ★ 本 hook 点：障碍格一律 skip
0x180a8d8bc  ...                        ; 非障碍格：继续原逻辑（fall-through）
```

Ghidra 反编译把它重构成一个复合条件，**证实两条 `je` 是同一逻辑的两个分支**：
```c
if ((plVar11 != 0) && (*(int *)((longlong)plVar11 + 0x14) != 2)) { ... }
```

**为什么 `gridType == Obstacle` 不够**：该分类同时包含城墙与**中立障碍**
（造景 / 雕像 / 木箱 / 木桶 / 灌木）。现场实测：`obstacleGrids` 共 31 个，
其中 `obstalceType = Normal, teamID = -1` 的 13 个，`Wall, teamID = 1` 的 18 个。
游戏对二者一视同仁地阻挡，**所以那一行不读 `teamID`**。
→ 判据必须下沉到 `ObstacleData`，只放行城墙。

**字段偏移（MCP 在活进程读出 + 原生内存交叉验证）**：

| 字段 | 偏移 | 取值 |
|---|---|---|
| `GridUnitData.gridType` | `+0x14` | `None=0, Normal=1, Obstacle=2` |
| `GridUnitData.obstale` | `+0x30` | `ObstacleData*`，可能为 0 |
| `ObstacleData.obstalceType` | `+0x10` | `Normal=0, Wall=1` |
| `ObstacleData.teamID` | `+0x2C` | 中立 `-1`；城墙现场观测为 `1` |

**判据 = 己方城墙**：`obstalceType == Wall(1) && teamID == selfTeamID`。
因为有 `selfTeamID` 参与，**守方 AI 自动获得穿越自己城墙的能力**，
与用户「每个 AI 都要有这个能力」的一贯要求一致，无需额外处理。

**不会误伤箭塔 / 战鼓 / 分舵**：现场实测它们是**普通 `BattleUnit`**
（挂在 `g.battleUnit`，且 `g.obstale == null`）—— 本钩子在 `obstale == null` 时直接回 `skip`，
碰不到它们；它们归 §2.2 那条线按队伍处理。

**stub 的出口**（本项目第二个 native stub）：

| 情况 | 去向 |
|---|---|
| `gridType != Obstacle` | → `0x180a8d8bc`（**pass**） |
| `obstale == null` | → `0x180a8da6b`（skip，保持原行为） |
| `obstalceType != Wall`（中立障碍） | → skip |
| `teamID != selfTeamID`（他方城墙） | → skip |
| **己方城墙** | → **`0x180a8d8bc`（pass）** |

### ★★ 重大修正：己方城墙的出口必须是 `0x180a8d8bc`，**不是** `0x180a8d963`

初期实现把「己方城墙」送到 `0x180a8d963`（当时叫 `expand`），**真机表现为完全不能穿**
（用户："能走到城墙边，但点不动墙对面的格子（格子不亮）"）。

**根因**：`0x180a8d963` 并**不是**「接受这个格子」的入口，而是接受路径的**中段**。
看 `0x180a8d8bc` 起的真实代码：

```
0x180a8d8bc  mov  r9, [rax]              ; r9 = g 的 Il2CppClass*
0x180a8d8c2  mov  rdx, [rsp+0xb8]
0x180a8d8ca  mov  r8,  [r9+0x140]
0x180a8d8d1  call qword ptr [r9+0x138]   ; ★ 判定：这个格子能不能落
0x180a8d8d8  test al, al
0x180a8d8da  jne  0x180a8da99            ; 能落 -> 接受
```

`0x180a8d963` 位于这个判定**已经返回通过之后**，而且它期望 `rsi`/`rbp`/`[rsp+0xa0]`/`[rsp+0xa8]`
已被前一段代码铺垫好（首条即 `mov rdi,[rsi+0x40]` = 读 `tempRef`）—— 对障碍格并不成立。

**证据（活进程三方对照）**：用 MCP 把**同一个** `WallPassHook` stub 的出口分别改成三个候选，
再调 `MapNavigator.Navigate` 走同一个「相邻己方城墙格 `(6,13)`」（`gridType=2`、
`obstalceType=1`、`teamID=1`，起点 `(6,14)` 为空地）：

| stub 出口 | `Navigate` 结果 | path 长度 |
|---|---|---|
| `0x180a8d8bc`（pass） | **True** | 1 ✅ |
| `0x180a8da6b`（skip） | **False** | 0 ❌ |
| `0x180a8d963`（expand，旧实现） | **False** | 0 ❌ |

同格、同调用、**只有出口不同** —— 因果干净。

**正解**：两个出口合一 —— 己方城墙与非障碍格**都走 `0x180a8d8bc`**，
让游戏自己的准入判定（`[r9+0x138]`）去裁决。它对本例中的己方城墙**返回通过**。
stub 因此从 72 字节缩到 59 字节，且只剩两个出口。

> **旧结论已推翻**：曾经认为「`FriendlyPassHook` 用 `0x180a8d963` 能工作，所以那个地址没问题」。
> 实际上它**并不是能工作** —— 它只是**不崩**：同样跳过了 `AroundGridHaveEnemy`，
> 导致「格子亮且能进范围」，但把「队友格子」错误地当成合法落点，
> 于是 AI 会站到玩家头上（见 §2.2a）。**「没崩」不等于「对」。**
> 两个 hook 现在都不再使用 `0x180a8d963`。
### 2.2c ★ 最终方案：「城墙可跨越」= 写 `GridUnitData.passes`

这是城墙功能**真正生效**的实现，也是整个项目最关键的发现。

**根因：`Navigate` 的搜索上限是 `from.row × from.passes`，而障碍格的 `passes` 恒为 0。**

```
0x180a8d6df  call 0x1808CA250      ; 符号名 BattleMapData.get_GridCount
0x180a8d6eb  mov  [rsp+0x38], eax  ; 存为循环上界 iVar3

0x1808CA250: mov eax,[rcx+0x24]    ; GridUnitData.row
             imul eax,[rcx+0x20]   ;  × GridUnitData.passes
             ret
```

⚠️ 这里 `rcx` 实际是 **`from`（GridUnitData）**，不是 `BattleMapData`
（序言 `mov rsi,rdx` 已核实）。所以：

| 起点 | `passes` | 搜索上限 | 后果 |
|---|---|---|---|
| 空地 | 15 | 75 | 正常搜索 |
| **城墙** | **0** | **0** | **主循环一次都不执行** |

→ 从城墙格出发的搜索**永远不跑**，所以城墙**不可能作为中转节点**，
跨墙**从原理上做不到** —— 无论怎么调判定、怎么选出口地址都没用。

**实测证据（A/B，同一场战斗）**：

| 起点 `(5,12)`，范围 3 | 格子数 | 包含的格子 |
|---|---|---|
| `passes=0`（原版） | 13 | 不含 `(5,14)` |
| `passes=15` | 16 | **多出 `(5,14)(5,15)(6,14)`** ← 墙对面 |

两次 `walls` 都是 0 —— **城墙自己始终不入高亮**。

#### 为什么这「基本」满足需求 —— 以及它漏掉的那一面

需求是「**城墙不可停留，但能从上面跨过去**」。`passes` 一改，两边看上去同时满足：

- `GetMoveRangeGrids` 里有独立的 `gridType != 2` 拦阻 → 城墙不入高亮
  → 不可点、不可停留
- `Navigate` 能把城墙当中转格 → **可跨越**

而且「亮」与「走」由**同一个 `Navigate`** 回答，不会出现「亮了却走不过去」。

> ⚠️⚠️ **但上面第一条是错的，2026-10 实机发现。**
> `gridType != 2` 只能挡住**扩散进来的**障碍格；
> **中心格自身会被无条件加入范围**。实测：
>
> | 中心格 | 范围里的障碍格数 |
> |---|---|
> | 普通格 `(7,13)` | **0** ✅ |
> | 障碍格 `(6,13)` | **1 —— 它自己** ❌ |
>
> 后果：**AI 自动寻路站到了城墙上**（用户发现 `(6,13)` 有一名 team 1 单位）。
> 一旦站上去，那面墙就成了它的合法落点，而且能沿着墙一格一格走下去。
> 修复见 **§2.2d**。
而且「亮」与「走」由**同一个 `Navigate`** 回答，不会出现「亮了却走不过去」。

#### 实现：`WallPassData` + 两个 Harmony 钩子

| 时机 | 钩子 | 动作 |
|---|---|---|
| 战斗开局 | `BattleMapData.GenerateMapObjs` Postfix | 对己方城墙写 `passes=15` |
| 战斗结束 | `BattleController.BattleRealEnd` Postfix | 按记录的原值回滚 |

**为什么是 `GenerateMapObjs` 而不是 `Generate`**：后者只做**布局**，
跑完时 `obstacleGrids` 还是空的 —— 实测会打印「已放行 **0** 面己方城墙」。
真实创建链：

```
BattleController.PrepareBattleMap
  ├─ BattleMapData.Generate()        @0x180816368   ← 太早
  └─ BattleMapData.GenerateMapObjs() @0x18081683f   ← 钩这里 ✅
       └─ GenerateBuildingObstacle
            ├─ GenerateWallData      ← 城墙在这里诞生
            └─ GenerateObstacleData
```

**定向写入，绝不批量**：只写 `obstacleType == Wall && teamID == selfTeamID`
的格子。按类别批量写会连**中立造景**（树/木箱，同样是 `gridType==2`）一起放行。
实测：只改己方城墙时，21 个中立障碍**全部仍被阻挡**。

**`passes` 是分类标记而非逐格调优值**：`normalGrids` 357 格全部 = 15，
`obstacleGrids` 43 格全部 = 0，各自只有 1 种取值。所以写 15 是「恢复成
一个游戏本身就在用的合法值」。

> ⚠️ **未解风险**：`passes` 的**写入者至今未定位**，也无法确认除 `Navigate` 外
> 还有谁读它。因此采用「进战斗置位 / 结束恢复」的可回滚策略，
> 改动不落到存档。

### 2.2d ★ 补完：城墙「不可停留」

**实机问题**（用户发现）：AI 自动寻路**站到了城墙上**。
现场：`(6,13)` 有一名 team 1 单位，该格 `gridType=Obstacle`、
`obstacleType=Wall`、`obstacle.teamID=1`。手动点击不受影响。

#### 根因：`passes` 是类别标志，一刀切

`passes` 同时驱动**两件不同的事**，无法只改一半：

| 作用 | 机制 |
|---|---|
| 「能不能**穿过**」 | `Navigate` 的搜索深度上限 = `row × passes`（§2.2c） |
| 「能不能**停止**」 | `GetMoveRangeGrids` 的可达性判定 |

把它从 `0` 改成 `15` 后，城墙从「完全不可通行」变成了「**完全可通行**」——
既能跨过去（要的），**也能停上去**（不要的）。

而且 `GetMoveRangeGrids` 的 `gridType != 2` 过滤只挡得住**扩散进来的**障碍格；
**中心格自身永远被加入范围**（§2.2c 末尾的实测表）。所以一旦某单位站上墙，
那面墙就成为它的合法落点，且可沿墙继续走 —— **自我延续**。

#### 修法：钩 `BattleUnit.EnterGrid`（Prefix）

**⚠️ 这里有过一次代价明确的错判，值得完整记下。**

##### 第一次尝试：钩 `BattleController.GenerateMovePath` —— 失败

当时的理由是「它是**玩家点击**与 **AI 自动**两条路径的交汇点」。
**这个前提本身是错的**，实测后反汇编查明：

| 路径 | 是否经过 `GenerateMovePath` |
|---|---|
| 玩家点击 | ✅ 经过 |
| **AI 自动** | ❌ **不经过** |

```
GenerateMovePath 的调用者：2 处，全在 UI / 点击路径
AI 的实际链路：PlayBattleUnitMove → MoveFromTarget → 逐格 EnterGrid
```

**实测症状**：玩家点击确实被拦住了，但 **AI 依然站到了城墙上**（用户复现）。
钩子日志里也确有命中（`拦下落点（18,13）`）—— 说明**钩子在工作，只是 AI 不走这条路**。

> 💡 **教训**：「X 是两条路径的交汇点」这种判断**必须用调用者扫描验证**，
> 不能因为「名字听起来像入口」就当成入口。本项目当时只确认了它有 2 个调用者，
> **没确认 AI 那条链是否经过它** —— 这就是漏掉的那一步。

##### 第二次（当前）：钩 `BattleUnit.EnterGrid` —— 成功

`EnterGrid` 是**单位占据格子的唯一汇聚点**，6 个调用者覆盖全部途径：

| 调用者 | 用途 |
|---|---|
| `EnterBattleField` ×2 | 入场 |
| `MoveNext` ×3 | **AI 与玩家点击的实际移动** |
| `RegretMove` | 撤销移动 |

且底层 `GridUnitData.OnEnter`（全部动作就是 `[格+0x18] = 单位`）
**全二进制只有 `EnterGrid` 一个调用者**。

##### 判据与行为

| 条件 | 行为 |
|---|---|
| 目标格 `obstale == null` | 放行（普通格） |
| 目标格是中立障碍（`obstacleType != Wall`） | 放行 —— 原版本来就不可达，不重复干预 |
| 目标格是**城墙** | 返回 `false` → 跳过原方法，格子不被占据 |

「什么都不做」是刻意选择 —— **不替游戏改写意图**（不做「改走到墙前」这种降级）。

判据与 `WallPassData.Apply` 共用 **`WallPassData.TryGetWallTeam`**（原生指针读）：
两处必须看**同一个字段**，否则会出现「放行了但不让停」这类不一致。
实测：**22 面城墙被拦，366 个普通格 + 12 个中立障碍不受影响**。

##### ⚠️ 为什么这里能安全返回 `false`

| 方法 | 返回值 | 能否 prefix 返回 false |
|---|---|---|
| `GridUnitData.OnEnter` / `BattleUnit.EnterGrid` | **`void`** | ✅ **可以** —— 没有返回值供调用方消费 |
| `BattleUnit.MoveFromTarget` | `IEnumerator` | ❌ **不可以** |

`MoveFromTarget` 虽然也是候选钩点（全二进制只有 1 个调用者），但调用方拿到返回值后
**直接丢给协程驱动且不做 null 检查**：

```
call MoveFromTarget
mov  rdx, rax          ← 返回值
call <协程驱动>         ← 无 null 检查
```

在那里返回 `false` 会把 `null` 交给协程驱动 → **极可能崩溃**，而不是优雅拒绝。
**所以钩点选择必须同时看「覆盖范围」与「返回值语义」。**

##### 活体验证（2026-10）

| 调用 | 调用前 | 调用后 | 结论 |
|---|---|---|---|
| `EnterGrid(普通格 (18,12))` | 空 | **有** | ✅ 正常放行 |
| `EnterGrid(空城墙 (0,13))` | 空 | **空** | ✅ 拦下 |
| 日志 | — | `[城墙禁停] 拦下进入（18,13）` | ✅ 命中 |

#### ⭐ 为什么不会造成「AI 反复选同一格」空转

这是实现前最值得担心的一点（**用户提出**）：如果 AI 的候选里始终含墙格，
拒绝岂不会让它反复选同一格而空转？

实测发现一条关键**不对称性**，它正好否定了这个担心：

| 中心格类型 | 范围里的障碍格数 | 含义 |
|---|---|---|
| **普通格** `(7,13)` | **0 个** | 从普通格出发时，城墙格**从来就不在候选里** |
| **障碍格** `(6,13)` | **1 个（它自己）** | 只有「已经站在墙上」时它才入范围 |

所以拒掉城墙落点：

- **不会缩小正常候选集合** —— 正常回合（从普通格出发）它本就不在里面；
- 只是斩断了「已在墙上 → 再走一格墙 → 仍在墙上」的**自我延续**链条。

实测一个**站在墙上**的单位，其范围内仍有 **64 个普通格**可选，不会无路可走。

#### 踩坑：报错里的 「postfix」 就是线索

首次挂载失败，Harmony 报：

```
Return type of pass through postfix … does not match type of its first parameter
```

原因：挂载时误用了 `TryPatch`（= **postfix**）。Harmony 把
「返回 `bool` 且首参是 `__instance`」的方法解释成了**透传 postfix**
（postfix 的返回值会替换原返回值），而原方法返回 `void`，类型对不上。

→ **同一个签名在 prefix 下合法、在 postfix 下报错。**
报错里的 `postfix` 二字就是线索：**改挂载方式，别去改签名**。

### 2.2e ★ 补完二：「可路过，不可停留」

**实机问题**（用户发现）：**两个 NPC 与箭塔重叠**。

#### 根因：穿友方只做了一半

箭塔（箭塔·三）是**普通 `BattleUnit`**，不是 `obstale`（本文 §2.2 早已记录这一点）。
所以它归「穿友方」这条线按**队伍**处理 —— 同队即被当作「队友」放行。

而穿友方的实现只改了 `Navigate` 的判据（「谁算阻挡」），**没有区分「路过」与「停靠」**：
AI 于是直接**停**在了箭塔的格子上，把它的占位登记踩掉。

日志坐实：

```
[01:47:08.929] 记录待恢复：格(r9,c6)   → ★已写回 原主 0x1322c8000   ✅
[01:47:11.152] 记录待恢复：格(r4,c14)  → 【无写回】原主 0x130975240（箭塔·三）
[01:47:11.595] 记录待恢复：格(r15,c14) → 【无写回】原主 0x12f2f7ea0（箭塔·三）
```

结果：箭塔的 `mapGrid` 仍指向原格、但登记已被踩 → **「无家可归」**，视觉上就是重叠。

> 💡 **本文 §8 早就标过这个风险**：「箭塔/战鼓/分舵实测是普通 `BattleUnit`，
> 归穿友方那条线按队伍处理。**当前无异常**，但守城战里需再看一眼」。
> 这次正是守城图触发了那行预警 —— **「当前无异常」不等于「没问题」**。

#### 修法：在 `EnterGrid` 里拦「目标是别人的格」

与城墙禁停共用同一个钩子（`BattleUnit.EnterGrid` Prefix），活体验证：

| 调用 | 结果 |
|---|---|
| 进占用格 `(0,19)` | 占用者前后不变 ✅ 拦下 |
| 进空格 `(0,0)` | 空 → 有 ✅ 放行 |

**为什么必须在 `EnterGrid` 而不是 `Navigate`**：
`Navigate` 只回答「寻路时这格算不算通」—— 而「**路过**」正需要它算通。
本钩子在**落格那一刻**才拦，于是路径仍可穿过、但落点不会是占用格。
**这就是「可路过，不可停留」的实现方式。**

**⚠️ 只拦「别人」**：自己走进自己当前格必须放行，否则单位会被自家登记卡死。

**为什么不会让 AI 无路可走**：与城墙不同，占用格是**动态**的 ——
AI 换一个空落点即可，而空格总占绝大多数（实测一局 400 格中仅 43 格有登记）。

### 2.2f ★ 补完三：城墙**损毁后必须视为空地**

**实机问题**（用户发现并提问）：战斗中被摧毁的城墙格，其 `passes` 与可通行状态
是否和空地等同？

#### 检查结果：`passes` 对，但可通行状态**不对**

实测两面被摧毁的城墙：

| 格 | `gridType` | `passes` | `obstale` | HP |
|---|---|---|---|---|
| `(15,13)` | `Normal` | `15` | 仍在（`Wall`） | **-5.1** |
| `(17,13)` | `Normal` | `15` | 仍在（`Wall`） | **-29.2** |

- **`passes = 15` 是对的** —— 游戏把 `gridType` 改成 `Normal` 时自己设的；
  我方 `WallPassData` 只是把它从 0 改成 15，损毁后**恰好与空地一致**。
- **但可通行状态是不对的** —— `obstale` 仍指向一个 `obstalceType == Wall` 的对象
  （只是 `hp < 0`），而我的两道禁停判据**都只看 `obstalceType`**，
  于是**把废墟当成完好城墙拦了下来**。

实测：对 `(15,13)` 调 `EnterGrid` → **被拦**（前后都是空）。

> 💡 **游戏清理城墙的方式很反直觉**：它**不**把 `obstale` 置 `null`，
> 也**不**同步改 `obstalceType` —— 只改 `gridType` 并把 `hp` 打到负数。
> 所以「这面墙还在吗」**唯一可靠的信号是 `hp > 0`**，不能只看类型。

#### 修法

`WallPassData.TryGetWallTeam` 补上 `hp > 0` 检查（`ObstacleData.obstacleHp` 在 **+0x24**，float）。

**验证**：

| 项 | 结果 |
|---|---|
| 偏移正确性 | 指针读 `@0x24` 与托管属性 `obstacleHp` **完全一致**（四格比对） |
| 损毁城墙 `(17,13)` | 空 → 有 ✅ **放行**（修复生效） |
| 完好城墙 `(14,13)` | 空 → 空 ✅ **仍拦下**（未误放行） |

> ⚠️ **回归提醒**：`obstale` / `obstalceType` / `hp` 这套判据同时服务
> 「放行哪些墙」（§2.2c）与「禁停哪些墙」（§2.2d/§2.2e），
> 改动时两处必须一起过一遍 —— 它们共用 `TryGetWallTeam`，共享同一个判据。

### 2.3 「穿越不留痕」：纯托管层的两步配合

**问题**：能穿之后，被穿过的格子会**丢掉占位登记** → 后续单位能踩上同一格（重叠），该格 NPC「点不动、不弹角色信息」。

**完整因果链（已逐条坐实）**：

```
我的 detour 放行「有人的格子」
 → 移动路径包含有人的格子
 → MoveFromTarget 逐格 EnterGrid，路过时对每格调用 OnEnter(该格, 我)
 → GridUnitData.OnEnter 无占用检查，直接 grid.battleUnit = 我
 → 原主的登记被覆盖抹掉
 → 那格变成 battleUnit == null（逻辑空格，但模型仍在）
 → 下一轮 GetMoveRangeGrids 认为它是空格 → 进 moveRangeGridUnits → 点亮
 → 落点判定（state 7 的 Contains）通过 → 能走上去 → 重叠
 → 原主：点不动、不弹角色信息
```

**原版为何不出问题**：原版寻路绝不会把有人的格子放进路径 → `OnEnter` 永不覆盖。

**修复方案（两个纯托管点）**：

| 位置 | 角色 |
|---|---|
| `BattleUnit.EnterGrid` 的 Harmony **Prefix** | 探测穿越。`occupant = grid.battleUnit`；若 `occupant != null && occupant.Pointer != __instance.Pointer` 即为穿越，把 `{格指针, 原主, 穿越者, r, c}` 记入 `_pendingRepairs[grid.Pointer]` |
| `GridUnitData.OnLeave` 的 Harmony **Postfix** | 执行写回。若原主 `IsAlive` **且其 `mapGrid` 仍指回本格**，说明它从未搬走、只是登记被踩掉 → 写回 |

**关键函数**：

- `BattleUnit_EnterGrid_Prefix(BattleUnit __instance, GridUnitData grid, bool noTurnRotation, bool teleport)`
- `GridUnitData_OnLeave_Postfix(GridUnitData __instance)` → 调 `RepairCoveredOccupant(__instance)`
- `RepairCoveredOccupant(GridUnitData grid)` —— **安全阀**：原主须 `IsAlive` 且 `original.mapGrid.Pointer == pending.Grid` 才写回
- `_pendingRepairs`（`Dictionary<IntPtr, PendingRepair>`，`MaxPendingRepairs = 8`）—— 一条记录只活「一次 `EnterGrid` Prefix」到「紧随其后的 `OnLeave` Postfix」之间，消费后立即移除。**没有跨战斗/跨回合残留**，这正是它不需要跟踪战斗开始/结束的原因

**为什么必须是 Postfix**：`OnLeave` 原函数的**第一条指令**就是 `[grid+0x18] = 0`。放在 Prefix 里的写回会被它立刻抹掉 —— 实测日志完整演示过。

**为什么判据要看原主而不是本格**：Postfix 里 `grid.battleUnit` **天然是 null**（`OnLeave` 的语义就是离开）。早期版本要求「本格当前 == 穿越者」，导致每一次修复都被跳过。

**为什么不用旁表**（用户 m00784 提问「旁表的生命周期怎么维护？那是不是还要 hook 掉战斗开始/结束？」）：旁表要正确必须跟踪战斗开始/结束/死亡/撤销，其中好几个不在 `OnLeave`/`OnEnter` 路径上，复杂度不划算且引入失步风险。→ **改用判据完全局部的方案。**

### 2.4 当前挂载的补丁（8 个 Harmony + 2 个 native detour）

**Harmony：**
```
BattleMapData.GetMoveRangeGrids        -> Postfix  （审计）
GridUnitController.set_GridRenderType  -> Prefix   （审计/染色）
BattleController.BattleGridClicked     -> Prefix   （审计/点击）
GridUnitData.OnLeave                   -> Prefix   （[命中] 计数 + [登记] 诊断）
GridUnitData.OnLeave                   -> Postfix  （★ 穿越不留痕：写回原主）
BattleUnit.EnterGrid                   -> Prefix   （★ 穿越不留痕：记录被覆盖的原主）
BattleMapData.GenerateMapObjs          -> Postfix  （★ 城墙可跨越：写 passes）
BattleController.BattleRealEnd         -> Postfix  （★ 城墙可跨越：恢复 passes）
BattleUnit.EnterGrid                   -> Prefix   （★ 城墙不可停留，兼「穿越不留痕」记录，§2.2d）
```

> ⚠️ **返回 `bool` 的补丁必须挂 Prefix**：写成 Postfix 会被 Harmony 当成
> 「透传 postfix」（返回 `bool` 且首参是 `__instance`），而原方法返回 `void`，
> 于是报 `Return type of pass through postfix …`。详见 §2.2d。

**Native detour（均在 `MapNavigator.Navigate` 内）：**
```
FriendlyPassHook  -> 0x180a8d929（存活单位判定）  ★ 穿友方
WallPassHook      -> 0x180a8d8b6（障碍格判定）    ★ 穿己方城墙
```

---

## 3. 游戏结构（本次调查查清）

### 3.1 战斗移动链路

三个入口最终都汇入同一个寻路器：

```
BattleController.GenerateMovePath(targetGrid)
BattleMapData.GetMoveRangeGrids(row,col,min,max,grids,selfTeamID)   // 算移动范围（高亮）
BattleMapData.GetEmptyGrid(from,to,path,mobility)
        \____________________ 汇入 ____________________/
                            v
MapNavigator.Navigate(battleMap, from, to, path, searched, stepLimit, selfTeamID)
```

`MapNavigator` 是 A\* 实现：字段仅 `instance / curUsedIdx / navigationDataPool`；方法 `.ctor / get_Instance / GetEmptyNavigationData / ResetPool / Init / Navigate`。

### 3.2 关键地址

`ptrs[local] = global_method_idx - 59969`（59969 = `BattleMapData` 的 metadata `methodStart`；方法指针表 VA `0x181de3120`，文件偏移 `0x1de1720`）。

| 方法 | 地址 |
|---|---|
| `MapNavigator.Navigate` | `0x180a80530`（691 条指令） |
| `MapNavigator.Init` | `0x180a802e0` |
| `BattleMapData.GetMoveRangeGrids` | `0x1808C8B60` |
| `BattleMapData.GetEmptyGrid` | `0x1808C88A0` |
| `BattleMapData.GetGridData`（**不叫 `GetGridByRowCol`**） | `0x1808c8b20` |
| `BattleMapData.GetGridDataByDir` | `0x1808c8a50` |
| `BattleController.BattleGridClicked` | `0x1807f9e70` |
| `GridUnitController.set_GridRenderType` | `0x180873a00` |
| `GridUnitData.OnEnter(BattleUnit)` | `0x180873b20` |
| `GridUnitData.OnLeave()` | `0x180873b60` |

`BattleMapData` 字段顺序：`mapID / battleMapTypeData / mapWidth / mapHeight / wallColumn / mapGrids / mustEmptyGrids / normalGrids / obstacleGrids / DefenceTrapID / DefenceGuardMapGridsOffset`。

### 3.3 `battleUnit` 的写者（**只有 2 个**）

`.18` 是**公开字段**，`GridUnitData` **没有** `get_battleUnit`/`set_battleUnit` → **无法 hook setter，只能 hook 写者函数**。

| 方法 | VA | 行为 |
|---|---|---|
| `GridUnitData.OnEnter(BattleUnit)` | `0x180873b20` | `[this+0x18] = unit` + write barrier → 再做特效/UI |
| `GridUnitData.OnLeave()` | `0x180873b60` | `[this+0x18] = 0` + write barrier → 再撤特效 |

两者的字节码几乎相同（同一编译产物模板），差别只在第一行赋 `param_2` 还是 `0`。

**直接写 `+0x18` 的全镜像扫描：零命中**（1190 处 `mov [reg+0x18], r64` 中，14 处邻近 `GridUnitData` 特征读的全部落在 IL2CPP runtime blob `0x18012xxxx`–`0x1801ffxxx`）→ **不存在野写者**。

### 3.4 `OnEnter` / `OnLeave` 的调用点

全镜像 `E8 rel32` 扫描（**未被内联**）：

| 方法 | VA | 调用点 | 所属 |
|---|---|---|---|
| `OnEnter(BattleUnit)` | `0x180873b20` | **1 处** `0x1808d16a4` | `BattleUnit.EnterGrid` +0x3d4 |
| `OnLeave()` | `0x180873b60` | **3 处** | `0x1808d13b8`（`EnterGrid`+0xe8）、`0x1808d2a99`（`LeaveBattleField`+0x49）、`0x1808d2bc2`（`LeaveGrid`+0x22） |

**时序关键**：一次 `EnterGrid` 内部是**先 `OnLeave(旧格)`、后 `OnEnter(新格)`**：

```
FUN_1808d12d0 (BattleUnit.EnterGrid):
  if (unit.mapGrid != 0) { FUN_1808d4670(unit,0); OnLeave(unit.mapGrid); unit.mapGrid = 0; ... }
  unit.mapGrid = targetGrid;
  ...
  code_r0x0001808d169b:  OnEnter(targetGrid, unit)      ← 覆盖发生在这一步
```

参数序：`OnEnter(rcx=TARGET grid, rdx=UNIT)`；`OnLeave(rcx=OLD grid)`。

**推论**：**路过 B 的格子不会触发 B 的 `OnLeave`**，只会触发移动单位自己旧格的 `OnLeave`；但 `OnEnter(路过格, 我)` 会无条件覆盖那格的 `battleUnit` —— 这才是元凶。所以修复必须在移动单位的 `OnLeave` 触发时，检测并修复**其他**格上被破坏的登记，不是简单对称。

### 3.5 `EnterGrid` 的 6 个调用者（全部合法移动路径）

`BattleController.RegretMove`+0x68 (`0x18081a2f8`)、
`BattleUnit.EnterBattleField`+0x385 (`0x1808d0e25`) / +0x5b1 (`0x1808d1051`)、
`BattleController.<HeroEnterGridDelay>d__253.MoveNext`+0xaf (`0x18092ff5f`)、
`BattleUnit.<MoveFromTarget>d__90.MoveNext`+0x47c (`0x180930b4c`)、
`BattleController.<PlayBattleUnitMove>d__273.MoveNext`+0x2f9 (`0x180931849`)。

### 3.6 `OnEnter` / `OnLeave` 的精简结构

`OnEnter` 实质只有开头 8 条指令：

```asm
180873b20: 40 53                push rbx
180873b22: 48 83 ec 20          sub  rsp,0x20
180873b26: 48 8b d9             mov  rbx,rcx
180873b29: 48 89 51 18          mov  [rcx+0x18],rdx      ; ★ 唯一的 battleUnit 写
180873b2d: 48 83 c1 18          add  rcx,0x18
180873b31: e8 fa 24 86 ff       call 0x1800d6030          ; write barrier
180873b36: 33 d2                xor  edx,edx
180873b38: 48 8b cb             mov  rcx,rbx
180873b3b: e8 10 02 00 00       call 0x180873d50          ; 查 map/controller
180873b40: 48 85 c0             test rax,rax
180873b43: 74 0f                je   0x180873b54          ; 失败 -> crash
180873b45: 33 d2                xor  edx,edx
180873b47: 48 8b c8             mov  rcx,rax
180873b4a: 48 83 c4 20          add  rsp,0x20
180873b4e: 5b                   pop  rbx
180873b4f: e9 cc c8 ff ff       jmp  0x180870420          ; ★ 尾调用，不是 ret
180873b54: e8 c7 2a 86 ff       call 0x1800d6620          ; il2cpp_raise_null_ref
180873b59: cc                   int3
```

`OnLeave`（`0x180873b60`）结构**完全对称**，同样以 `jmp 0x180870420` 结尾。

> `0x180870420` 是**共享尾部块**，不是 `OnEnter` 的一部分：全镜像 `call` 引用 = 0、`jmp` 引用 = 4（来自 `0x180870162`、`0x18087034a`、`0x180873b4f`、`0x180873b95`）。它自己的收尾是 `0x18087067a: add rsp,0x30; pop rdi; ret`。

### 3.7 `script.json` 里的重载陷阱

| 签名 | RVA | 说明 |
|---|---|---|
| `void OnEnter()` | `0x870160` | **空桩** |
| `void OnEnter(BattleUnit battleUnit)` | `0x873b20` | **真实现** |
| `void OnLeave()` | `0x870160` | ⚠️ **与空桩 `OnEnter` 同地址** |
| `void OnLeave()` | `0x873b60` | **真实现** |

→ `script.json` 里 `OnLeave()` **同名出现两条**，`Type.GetMethod("OnLeave")` 返回哪一个**不确定**。**挂补丁必须按参数个数选。**

### 3.8 `BattleController` 状态机

来源：`output/decomp_ghidra/RunBattle.c`（1811 行）、`output/decomp_ghidra/BattleGridClicked.c`（789 行）。这两个函数**不在 `script.json` 的 ScriptMethod 集合里**，Ghidra 显示为 `FUN_xxx`，但反编译完整可用。

**关键字段偏移**：

| 偏移 | 名字 | 作用 |
|---|---|---|
| `+0x110` | `nowActiveUnit` | 当前行动单位 |
| `+0x118` | `originUnitData` | 移动起点格 |
| `+0x120` | `nowActiveUnitMoved` | 本回合是否已移动 |
| `+0x124` | `nowActiveState` | **状态机** |
| `+0x1d8` | `moveRangeGridUnits` | 移动范围（`GetMoveRangeGrids` 输出） |
| `+0x1e0` | `movePath` | 高亮路径 |
| `+0x1e8` | `hoverMoveTargetGrid` | 悬停格 |
| `+0x1f0` | `moveTargetGrid` | **已选落点**（非 0 即触发移动） |

**`RunBattle` 状态机**：

| case | 行为 |
|---|---|
| 1 | 回合开始：清 `+0x1d8` → **`GetMoveRangeGrids` 填充** → 逐个上高亮 |
| 3 | 单位初始化 |
| 4 | 技能取消 |
| 6 | **进入移动**：清 `+0x1d8` → `GetMoveRangeGrids` 填充 → 遍历上高亮 → **设 state = 7** |
| 7 | **等待点击落点**。`+0x1f0 != 0` 时 → **设 state = 8** |
| 8 | 移动执行 |
| 9 | 移动后（攻击/技能选择） |
| 10 | 技能目标选择 |
| 11/12/13 | 后续阶段 |

```
case 1 (范围算好) → case 2? → case 6 (重算范围) → case 7 (等点击)
                                                        ↓ 点击 + 0x1f0 写入
                                                     case 8 (移动)
                                                        ↓
                                                     case 9 (攻击) → case 10
```

**与 mod 的关系**：

- `GetMoveRangeGrids` **只在 `RunBattle` case 1 与 case 6 被调用**，写入 `+0x1d8`。
- 玩家点击落点判据 = `+0x1d8` 的 `Contains`（state 7）—— **剔除占位格因此是有效手段**。
- ⚠️ **case 6 会重新调用 `GetMoveRangeGrids`**。若补丁只在某一次生效，两次调用结果会不一致 → 「格子亮但走不过去」或「暗但能走」。

### 3.9 关键类型速查

- **`GridUnitData`**（9 字段）：`mapID, gridType, battleUnit, passes, row, column, obstale, speGridObjData, tempRef`
  - 方法：`_ctor / get_GridType / set_GridType / get_GridObj / get_GridUnitController / Distance / OnEnter / OnLeave / isEmpty() / isEmpty(bool includeSpeObj) / Equals`
  - ⚠️ `obstale` 拼写缺 a（不是 `obstacle`）
- **`ObstacleData`**：`obstalceType`（**缺 a**）、`obstacleID, obstacleName, obstacleSpriteID, obstacleHp, obstacleMaxHp, teamID, bigObstacle, targetGridUnit, needRefreshOcclusion, occlusionState, explodeObstacle`
  - `ObstacleType = Normal | Wall`，**Wall = 城防**；`teamID` 可用于区分敌我城防
- **`SpeGridObjData`**（地面可重叠元素，即「药丸」）：`speGridObjType, onGround, abovePlayer, teamID, hp, maxHp, destroyAfterTrigger, valueRate, name, describe, flipX, soundEffect`
  - ⚠️ **`speGridObjData != null` 不代表语义有效** —— 它在 `speGridObjType == None` 时也非空。判空 ≠ 有效（曾因此误报「特殊元素=31」）。真实分布：`None=259, Defence=2, StoneTrap=2, PoisonInsect=2, PoisonLake=1, MudLake=1, WaterPool=1, MedGrass=1, OddVine=1`
- **`BattleController`**：`battleIncludeTeamMate`、`playerSpeControlTeamID`、`nowActiveUnit`、`moveRangeGridUnits`、`nowActiveUnitMoved`、`nowActiveUnitAttacked`、`selectedGrid`、`moveTargetGrid`、`playerBattleUnit`
- **`BattleUnit`**：`targetGrid`, `battleTeam`, `mapGrid`（偏移 `+0x60`）, `IsAlive`
- **`BattleTeam`**：`ID`(int), `havePlayer`, `battleUnits`, `needProtectUnits`, `needProtectUnitDestroyed`

**网格表**（`output/dumper_out/dump.cs:336625`）：`mapID 0x10`、`battleMapTypeData 0x18`、`mapWidth 0x20`、`mapHeight 0x24`、`wallColumn 0x28`、**`object[] mapGrids 0x30`**、`List<GridUnitData> mustEmptyGrids 0x38`、**`normalGrids 0x40`**、`obstacleGrids 0x48`。

⚠️ `mapGrids` 是**列主序**（`height*col + row`），数据从 `mapGrids + 0x20` 起。另有 `0x180127f90` 是**行主序**索引器（`row*width+col`，192 个调用者）—— **两个不同的访问路径，不要混用**。

### 3.10 metadata 锚点

| 类型 | typedef idx | fieldStart | fc | methodStart | mc |
|---|---|---|---|---|---|
| `BattleController` | 8075 | 34239 | 132 | 59684 | 166 |
| `BattleMapData` | 8076 | 34520 | **11** | **59969** | 30 |
| `BattleUnit` | 8086 | 34569 | 53 | 60010 | 54 |
| `SpeGridObjData` | 8093 | 34698 | 12 | 60105 | 3 |
| `ObstacleData` | 8097 | 34727 | 12 | 60112 | 5 |
| `GridUnitData` | 8098 | 34739 | 9 | 60117 | 11 |
| `MapNavigator` | 8100 | 34748 | 3 | 60128 | 6 |

---

## 4. 经验教训

### 4.1 方法论的（已泛化进 `AGENTS.md`，此处只记事件）

1. **「挂载成功」≠「触发」** —— 挂到空桩重载上和真实现上，日志一模一样。必须打印真实签名 + IL 地址。
2. **「日志没打印」≠「代码没执行」** —— `[登记]` 受 `Diagnostics` 门控，`grep -c` = 0 曾被误读为「补丁零触发」，**白耗整整一轮**。取证仪表要不受门控。
3. **部署后必须核对 md5** —— 15:52 与 17:20 两轮都因为部署的是旧 DLL 而白测（把 Prefix 版当 Postfix 版测）。
4. **用 MCP 直读活进程比读日志快得多** —— `PatchManager.GetPatchInfo(m).postfixes.Count()` 一眼看出 `postfixes=0`（Postfix 从未注册）。
5. **手算地址必错** —— 曾把 `modbase + 0x2300000` 算成 `0x6ffff8270000`，实为 `0x6ffff82a0000`，差 `0x30000`。
6. **crashes 要二分隔离** —— 把可疑功能各配开关逐项关掉，比读代码快得多。
7. **外部文档交叉验证能纠正反编译误判** —— 本次靠 BepInEx 源码推翻了「DMD 包装」的错误结论。「websearch + 自己反编译两方对比」是有效方法论（用户 m02872 要求）。
8. **不要假定某个集合是「场上单位」** —— 先报告各候选集合的元素数（`managedUnits` 恒为 0 曾让一轮审计完全空转）。

### 4.2 本案特有的

9. **「有原生调用者」不是判据，「该次调用是否走托管代理」才是**。本项目一度把「补丁零触发」归因于「目标只被原生 `call` 调用」，实为观察错误。
10. **判据要完全局部**。旁表方案被否，就是因为它需要跟踪战斗开始/结束/死亡/撤销才能正确 —— 而这几件事**都不在 `OnLeave`/`OnEnter` 路径上**。
11. **不要和尾调用（tail call）较劲**。`OnEnter` 以 `jmp 0x180870420` 结尾，自建 detour stub 无法与共享尾部块共存。**遇到这种函数就用 Harmony。**
12. **Prefix 写回是无效的**（原函数体随后会覆盖），**Postfix 里读「本格当前值」也是无效的**（`OnLeave` 语义下它天然是 null）。这两条各浪费过一轮。
13. **取证要打「不受门控」的计数器**。`[命中] OnLeave 第 N 次` 这种计数器是本项目定位问题的关键工具。
14. **hook 在「条件跳转」上时，必须自己重判那个条件**。`0x180a8d8b6` 是 `je`，它在 `gridType == Obstacle` 与 `!= Obstacle` **两种情况下都会被执行**。初期只处理了前者，把普通格也归入「无障碍物数据 → skip」，后果是**移动范围只剩脚下那一格**（实机复现）。
15. **stub 的出口跳转不能用 `rax` 中转**。`fallThrough`(`0x180a8d8bc`) 是紧跟 `cmp` 的**原有代码**，它依赖 `cmp` 留下的 `rax` = 邻格指针；而 `mov rax,imm64; jmp rax` 会把 `rax` 改成代码地址 → `mov r9,[rax]` 读到代码字节当类指针 → SIGSEGV。
    gdb 现场：`rip=0x180a8d8ca`（`mov r8,[r9+0x140]`）、`rax=rcx=0x180a8d8bc`。
    → 改用 `EmitJumpViaR11`（`mov r11,imm64; jmp r11`）。**判据：目标是否是一段原有的代码，且它是否读 `rax`。**
16. **偏移不要手算**。stub 的 rel8 回填曾因「指令布局改了、偏移没跟着改」而错乱。
    现改为：发射时 `AddRel8()` 返回实际偏移 → `PatchRel8()` 回填 → `VerifyStub()` 反查每条跳转的目的地。
    算错会在安装瞬间写入 `ERROR` 日志，而不是变成玄学现象。
17. ★★ **「输入对、判定对、但就是不生效」时，要去查【上限/计数/容量】这类边界参数，
    而不是继续在判定逻辑里找。** 本项目在城墙问题上反复走错（四次：`expand` 出口、
    `fallThrough` 出口、栈损坏误判、高亮方案），最后发现真因是
    **`Navigate` 的搜索上限 = `from.row × from.passes`，而障碍格 `passes=0`** ——
    搜索循环**根本一次都没跑**。再怎么调判定都无济于事。
18. **不能只看一个函数的「返回值」就断定行为**。`Navigate` 对「空→墙」返回 `True`，
    于是曾误判「能穿墙」——但**必须看它返回的 `path` 内容**：实际是 12 步绕路。
    （返回值只说「总体可达」，不说「怎么走」。）
19. **符号名会误导类型判断**。`0x1808CA250` 符号名是 `BattleMapData.get_GridCount`，
    但 `Navigate` 调用它时 `rcx` 是 **`GridUnitData`** —— 实际算的是 `passes × row`。
    看符号后仍要结合**调用点的寄存器内容**判断。
20. **钩子不能钩得太早或太晚**。`BattleMapData.Generate` 只做布局，跑完时
    `obstacleGrids` 还是空的（实测打印「已放行 **0** 面」）；必须在
    `GenerateMapObjs`（障碍物的真正创建处）之后。
21. **「补丁已挂上」≠「代码跑了」**。本项目有两次都是 `patcherIsValid=true`、
    `attached=true`，但 postfix 因为**注册晚于该时机**而从没执行过。
    → 排查时先看**自己代码打的那条日志**，而不是 Harmony 的「已挂上」。
22. **永远先确认「跑的是哪个构建」**。本项目因跑旧产物白耗过两轮，而症状就是
    「代码不生效」—— 与真正的逻辑 bug **无法区分**。
    现在启动日志会打**自身 md5**（与 `md5sum` 直接对账；构造计数曾用时间戳，
    那会破坏 Deterministic —— 见 AGENTS.md §3.2.0）。
---

## 5. 已否定方案（**别再试**）

| 方案 | 否证方式 |
|---|---|
| 写 `BattleController.battleIncludeTeamMate` | 托管侧零读写、原生不读；**用户实测无效**（m00542） |
| Postfix 往 `GetMoveRangeGrids` 输出列表追加格子 | 寻路自己会重跑同一套过滤，**加了也走不过去** |
| 改 `isEmpty()` 让友方格变空 | 会造成重叠，**违反硬约束 2** |
| 在 `Navigate` 里加「是否终点」判断（`g.Equals(to)`） | **用户 m03844 否决**：「我觉得你可能没办法在Navigate里改，而且改了也不符合我们的意图。」 |
| 在 `OnEnter` 的 **Prefix** 里写回原主登记 | **用户 m03836 指出**：「prefix怎么保留状态？OnEnter是在prefix之后执行的吧」→ Prefix 写回会被原方法体覆盖 |
| 自建跳板改写 `Navigate` 的 `jne`（`0x180a8d929`）为比较队伍 | **装上即崩**，点击人物瞬间 coredump，无日志。`Navigate` 内层不是合适的注入点 |
| NOP 掉 `0x180a8d8da` 的 `jne` | 不崩且能穿人，但**敌我通吃** → 能踩敌人、能重叠 |
| 复用 `0x180a8d92f`–`0x180a8d95d` 段改造成友方判定 | 那段是**空格落点路径**（问「这个空格是否邻接敌人」），不是占位格的敌我判定 |
| 按 cpp2il `[Calls]` 属性选补丁目标 | 它是传递近似推导；曾据此选到**全二进制零调用者**的函数 |
| 在 `Navigate` 的 13 字节窗口内做等长改写 | 读队伍 ID + 比较需 20+ 字节；**全节最大 `0xcc` 空洞仅 17 字节**，最近 ≥16 字节空洞在 `0x180a8b930`（约 `0x1ff9` 外）→ **无可用 code cave** |
| 用 `NativeHook<T>` 在函数中途挂钩并读取 `rsi`/`[rsp+0xd8]` | 托管 delegate 只能拿到 Win64 参数寄存器 `rcx/rdx/r8/r9`，**读不到中途使用的寄存器** |
| 「`+0xd0` / `+0x118` 是 `GridUnitData` 字段」 | 探针打出全部 9 个托管字段，**无一列能区分**范围内的被占格与范围外的 |
| 「`AroundGridHaveEnemy` 是寻路的敌我判定点」 | `e8 rel32` 全量扫描：**零调用者**（是 17 条指令的薄壳，尾调用 `FindConnectedGrid`） |
| 「`GetMoveRangeGrids` 做 3 轮遍历同集合」 | 反汇编背边 `0x180a8b140 jmp 0x180a8af46`：**一次遍历，每元素 4 次判定** |
| 在 `GridUnitData.OnEnter` 上装自建 native detour | **一进战斗即崩**。`OnEnter` 以尾调用结尾，自建 stub 的帧使得到达共享尾部块时 `rsp` 低 0x30，`xmm6`/`r15`/`r14` 从垃圾内存恢复。Dobby 跳板对尾调用语义零处理 → **根本矛盾** |
| 把写回放在 `OnLeave` 的 **Prefix** | 原函数第一条指令就是 `[grid+0x18] = 0`，写回被立刻抹掉。**必须用 Postfix** |
| 修复判据用「本格当前 == 穿越者」 | Postfix 里 `grid.battleUnit` **天然是 null**，该条件永不成立 → 每次都「跳过」。**判据必须看原主自己的 `mapGrid`** |
| 城防 hook 用 `gridType == Obstacle` 作判据 | 该分类含**中立障碍**（造景/木桶…），会连树一起放行。**必须下沉到 `obstalceType == Wall`** |
| 城防 stub **漏掉非障碍格分支** | 移动范围只剩脚下那一格。`je` 在两种 `gridType` 下都会执行，stub 必须自己重判并回 `0x180a8d8bc` |
| stub 出口用 **`rax` 中转** | `fallThrough` 依赖 `cmp` 留下的 `rax`；被改成代码地址后 `mov r9,[rax]` 读到代码字节 → SIGSEGV（`rip=0x180a8d8ca`）。**必须用 `r11`** |
| 用 Reloaded.Assembler 替代硬编码 x86 字节 | 收益不足（见 §7） |
| 直接写 `GridUnitData+0x18` 的野写者 | 全镜像扫描**零命中** |
| `MethodAddressToToken.db` 取原生地址 | 两侧都是 token，是 token→token 映射 |

### 5.1 「`+0xd0` / `+0x118` 过滤点」的完整否证过程（值得保留）

早期分析认为占位过滤在 `GetEmptyGrid` (`0x180a88360`) 与 `GetMoveRangeGrids` (`0x180a8aee0`) 两处，依据是 `cmp byte[x+0xd0],0` / `cmp byte[y+0x118],0` 的字节模式。**整节作废**，两条独立证据：

1. **地址算错了** —— 这两个地址是用「全局方法号 − 类型 methodStart」索引 `methodPointers` 得到的，而正确索引是 `ptrs[(token & 0x00FFFFFF) − 1]`。真实地址是 `GetMoveRangeGrids = 0x1808C8B60`、`GetEmptyGrid = 0x1808C88A0`。
2. **那两个函数不可达** —— `e8 rel32` 全量扫描显示调用者为 **0**；真实函数体内这两个字节模式命中数也是 **0**。

**判据（已泛化为标准）**：IL2CPP 地址可信度需**三条同时成立** —— ①与独立 dumper 交叉验证一致 ②调用者数 > 0 ③函数体内能找到预期字节。

**保留的方法论教训**：***唯一*不等于*可达*** —— 那四处签名在 `GameAssembly.dll` 全文件扫描各命中 1 次，但唯一性并不代表它们是活的代码路径。另外，签名**必须含 `call` 位移**才能区分 `GMRG` 与 `GEG`（两处 `cmp` 前后的字节除 call 位移外完全相同）。

---

## 6. 代码状态

**构建**：`dotnet build FriendlyNoclip/FriendlyNoclip.csproj -c Debug`
**产物**：`FriendlyNoclip/bin/Debug/net6.0/FriendlyNoclip.dll`，**0 错误 0 警告**。

> ✅ 启动日志会打印**自身 md5**（如 `【构建 1.0.0 md5=ef20e0d5…】`）。
> **看日志就能确认跑的是哪个产物**，直接与 `md5sum` 对账。
>
> ⚠️ 这里曾经打的是「构建时刻」（csproj 的 `BuildStamp`）。
>
> **历史始末**（三次尝试，别重走）：
> 1. **`<InformationalVersion>$(BuildStamp)</...>`** —— 时间戳进程序集内容，
>    破坏了 `<Deterministic>`，同源码两次构建 md5 不同。**实测确认**。
> 2. **改用 `AssemblyMetadata`** —— 本以为能绕开，**实测仍会变**（它同样是
>    `[assembly: ...]` 特性，即程序集内容）。**教训：任何嵌进程序集的值都是内容。**
> 3. **旁车文件 `.buildstamp`**（MSBuild target 写到 DLL 同目录）—— 确实能兼顾，
>    但引入了一个额外的部署产物与一套 target，**复杂度不划算**。
>
> **最终方案：直接删掉时间戳机制。** 既然 `<Deterministic>` 已经生效，
> md5 就是稳定且可靠的判据，日志打印自身 md5 即可 —— 不需要时间戳。
>
> 现在 md5 稳定了，它就是可靠判据（见 [`AGENTS.md`](../AGENTS.md) §3.2.0）。

| 文件 | 状态 |
|---|---|
| `NativeHookBase.cs` | **抽象基类** —— 安装/卸载骨架 + 共用工具（`RuntimeVa` / `EmitJumpViaR11` / `IsRel32Jcc` / `Hex`）。子类只写差异：`Tag` / `HookVa` / `HookSiteVa` / `OriginalBytes` / `ValidateSite` / `BuildStub` |
| `FriendlyPassHook.cs` | **穿友方** ✅ 在用 —— hook `0x180a8d929`，按 `battleTeam.ID` 判队伍 |
| **`WallPassData.cs`** | **★ 穿己方城墙的核心** ✅ 在用 —— 写 `GridUnitData.passes`（§2.2c） |
| `WallPassHook.cs` | 改 `Navigate` 的判定 ✅ 在用（`wall_pass_hook`），但**城墙功能已不再依赖它** |
| `WallHighlightHook.cs` | ⚠️ **实验性、已验证会崩溃、默认关闭**（`wall_highlight_hook=false`）。保留仅为记录与将来可能的重做 |
| `NativeProbeLog.cs` | 回溯探针 —— **默认不启用**（每进 hook 写 112 字节 + 2s 落盘，有开销）。保留供将来排查 |
| `NativeMemory.cs` | 模块定位 / 签名扫描 / 原生读写 / `VirtualProtect` / `WriteInt32` |

**已删除**：`NativeOnEnterDetour.cs`（前提错误 + 尾调用崩溃）、`NativePatchProbe.cs`、`NativeProbe.cs`；
开关 `native_probe` / `native_patch_probe` / `native_onenter_detour` / `allow_friendly` /
`wall_ignore_team` / `wall_native_hooks`；函数 `CallerReturnAddress()`。
**诊断开关 `diagnostics`**：打开后打印 `[审计]` / `[点击]` / `[染色]` / `[普查]` / `[登记]` / `[穿越]`。`[命中] OnLeave 第 N 次` 与 `[修复·登记]` **不受门控**，始终打印。

**git 提交序列**：`acd50b7 initial commit` → `e8c8e65 feat: (incomplete) noclip for the friendly` → `38282f0 feat: 穿越友方 + 穿越不留痕（功能闭环）` → `98d8a17 chore: 清理配置项，只保留 4 个`。

### 6.1 两个 stub 的机器码布局（备查）

**【穿友方】`FriendlyPassHook`**（`0x180a8d929`）

- `OriginalBytes = { 0x0F, 0x85, 0x3C, 0x01, 0x00, 0x00 }`（6 字节 `jne rel32`）
- 布局（**60 字节，两出口** —— 2026-10 由三出口改为两出口）：
  ```
  +0x00  test al, al                  ; get_IsAlive(g.battleUnit)
  +0x02  je   <pass>                  ; 无存活单位 -> 空格链
  +0x04  mov  rax, [rsi+0x18]         ; g.battleUnit
  +0x08  test rax, rax
  +0x0B  je   <skip>
  +0x0D  mov  rax, [rax+0x58]         ; battleUnit.battleTeam
  +0x11  test rax, rax
  +0x14  je   <skip>
  +0x16  mov  eax, [rax+0x10]         ; battleTeam.ID
  +0x19  cmp  eax, [rsp+0xd8]         ; selfTeamID
  +0x20  je   <pass>                  ; ★ 同队 -> 空格链（不可直接接受！）
  +0x22  -> skip (0x180a8da6b)
  +0x2F  -> pass (0x180a8d92f)
  ```
- ⚠️ **两个出口都用 `EmitJumpViaR11`**（与 `WallPassHook` 统一）。
  旧版用 `EmitAbsoluteJump`（经 `rax`）—— 当时以为三个出口都不读 `rax`，
  现在出口改回 `0x180a8d92f`（原版代码），**更不能碰 `rax`**。
- ❗ `jeSameTeam` 必须指向 `pass`。指向「直接接受」就是「AI 站到玩家头上」那个 bug。
**【穿己方城墙】`WallPassHook`**（`0x180a8d8b6`，59 字节）

- `OriginalBytes = { 0x0F, 0x84, 0xAF, 0x01, 0x00, 0x00 }`（6 字节 `je rel32`）
- 布局：
  ```
  +0x00  cmp dword [rax+0x14], 2     ; gridType == Obstacle ?
  +0x04  jne <fallThrough>           ; 非障碍格 -> 0x180a8d8bc（必须保留 rax）
  +0x06  mov rcx, [rax+0x30]         ; g.obstale
  +0x0a  test rcx, rcx
  +0x0d  jz  <skip>
  +0x0f  cmp dword [rcx+0x10], 1     ; obstalceType == Wall ?
  +0x13  jne <skip>
  +0x15  mov edx, [rcx+0x2c]         ; obstale.teamID
  +0x18  cmp edx, [rsp+0xd8]         ; == selfTeamID ?
  +0x1f  jne <skip>
  +0x21  -> pass     (0x180a8d8bc)   ; 己方城墙 -> 放行（与非障碍格同一出口）
  +0x2e  -> skip     (0x180a8da6b)
  
  ```
- ⚠️ **两个出口全部用 `mov r11,imm64; jmp r11`**，`rax` 全程不动
- ⚠️ `VerifyStub` 接收**实际发射时的 rel8 偏移**，不写死 `code[5]/[14]/…`

### 6.2 为什么会写成 `EmitJumpViaR11`（血泪）

最初 `WallPassHook` 也用 `mov rax,imm64; jmp rax` 跳出口。结果：

- `fallThrough`(`0x180a8d8bc`) 第一条指令是 `mov r9,[rax]` —— 它**期望 rax 仍是上一条 `cmp` 留下的邻格指针**
- 但 `rax` 已被改成**代码地址** → `r9` = 代码字节被当成类指针 → `mov r8,[r9+0x140]` **SIGSEGV**
- gdb 现场：`rip=0x180a8d8ca`、`rax=rcx=0x180a8d8bc`

**判据**：若跳转目标是**一段原有的代码**（而不是你自己的标签），
先查它是否读 `rax`；读则**必须**用 `r11`（或其他不被目标读取的易失寄存器）中转。

**而且这类 bug 不会在安装时报错** —— 它表现为「移动范围只剩脚下那一格」或
「某些情况下必崩」，静态看代码极难发现。所以 `VerifyStub()` 里的**跳转目标自检**是必要的。

---

## 7. 取舍记录

### 7.1 为什么放弃「托管 BFS 回退方案」

早期方案：在 `GetMoveRangeGrids` 的 Postfix 里，用游戏自己的 O(1) 邻格查询 `GetGridDataByDir(row, column, dir)` 自行做一遍 BFS，友方占据的格子「可穿过但不加入落点」，步数预算取原范围最远格的曼哈顿距离。

**放弃的直接原因**：实际现象是「**格子是亮的但无法走过去。路径上没有阻拦**」（用户 m02609）—— 手写 BFS 与游戏后续 `GenerateMovePath` 使用的 `Navigate` **不一致**，产生了「高亮范围」与「可行路径」的分裂。

→ 换到 `Navigate` 内层改判定，**一处生效两处一致**。

（附带教训：邻格查询**必须**用 `GetGridDataByDir`。手写遍历 `normalGrids` 是 O(n) 跨界循环，曾在 AI 回合把游戏拖死。）

### 7.1b 寻路代价模型与「高亮走另一套判定」的实测记录（2026-10）

#### （一）高亮侧确实是**独立**的一套障碍判定

`BattleMapData.GetMoveRangeGrids`（`0x1808C8B60`）里有**三处**独立的
`cmp dword [reg+0x14], 2`（`gridType == Obstacle`），与 `Navigate` 那处互不相干：

| Gate | 地址 | 原行为 |
|---|---|---|
| G1 | `0x1808C8CBC` `je 0x1808C8E07` | 当前格是障碍 → 跳到 G2（测下一格） |
| G2 | `0x1808C8E47` `je 0x1808C9143` | 下一格是障碍 → 终止本行扫描 |
| G3 | `0x1808C8FEF` `je 0x1808C9133` | 外层循环：该格是障碍 → 不加入范围 |

两个 gate 的落空点**都会调用 `Navigate`** 做可达性测试，且只有返回 true 才 `grids.Add`：

- G2 路径：`0x1808C8F24 call Navigate` → `0x1808C8F29 test al,al` → `0x1808C8F89 List.Add`
- G3 路径：`0x1808C90C7 call Navigate` → `0x1808C90CC test al,al`

所以**即使放行两个 gate，仍然是 `Navigate` 在最终裁判** —— 这个设计本身是自洽的。

#### （二）但「跨墙」在 `Navigate` 里**不被优先选**

活进程实测（MCP，同一场战斗）：

| 起始 → 目标 | 曼哈顿距离 | `Navigate` 返回 | **实际 path 长度** |
|---|---|---|---|
| `(5,12)` → `(5,14)`（隔 1 面墙） | 2 | `True` | **12** |
| `(7,11)` → `(7,14)`（隔 1 面墙） | 3 | `True` | **7** |
| `(6,11)` → `(6,14)`（隔 2 面墙） | 3 | `True` | **9** |

`(5,12)→(5,14)` 的实际路径：
`(5,11)(6,11)(7,11)(8,11)(9,11)(9,12)(9,13)(9,14)(8,14)(7,14)(6,14)(5,14)`
—— **完全绕过了 `(5,13)` 那面墙**。

> ⚠️ **重要教训**：不能只看 `Navigate` 的**返回值**就断定「能穿墙」。
> 返回值只说明「总体可达」；**必须检查返回的 `path` 内容**。
> 本项目曾因只看返回值而误判方案可行（白白多跑一轮）。

#### （三）代价模型（已逐条反汇编确认）

```
GridUnitData.Distance(a, b)  @ 0x180873A10   = |Δrow| + |Δcol|    (曼哈顿)

Navigate 扩展邻格 rsi（当前节点 rbp）时：
  0x180a8d977  mov  edi, [rbp+0x18]        ; edi = 当前节点的 G
  0x180a8d97d  call 0x180873A10            ; eax = H（邻格到目标的曼哈顿）
  0x180a8d98a  lea  r9d, [rdi+1]           ; ★ G_new = G + 1  —— 每步恒为 1
  0x180a8d9a1  call 0x180A8D370            ; GetEmptyNavigationData(…, G_new, H, 0)

GetEmptyNavigationData @ 0x180A8D370：
  0x180a8d45e  mov  [rdi+0x1c], ecx         ; node.H = H
  0x180a8d461  add  ecx, r14d              ; ecx = H + G
  0x180a8d464  mov  [rdi+0x14], ecx         ; node.F = H + G
  0x180a8d46b  mov  [rdi+0x18], r14d        ; node.G = G
```

`NavigationData` 字段：`open=+0x10`、`F=+0x14`、`G=+0x18`、`H=+0x1C`、`thisGrid=+0x20`、`preGrid=+0x28`。

**结论：`F = G + H`，而每一步的代价恒为 `1`，与格子类型（空地 / 城墙）无关。**

#### （四）因此：要「跨墙」就必须改**代价**，而不是改判定

- 既然每步恒为 1，穿 1 面墙（2 步）理应胜过绕 12 步 —— 但实测选了 12 步。
- 说明被放行的城墙格**要么没被当作可扩展节点压入 open 表**，要么在平局/排序上输给了先入队的绕路节点。
- 所以「只改判定（`WallPassHook` 现状）」**不足以**让穿墙成为优先选择。

#### （五）★★ 真根因：`Navigate` 的搜索上限是 `from.row × from.passes`，而障碍格的 `passes` 恒为 **0**

这是城防穿越一直失败的**真正原因**，与判据、出口地址、高亮层**都无关**。

**搜索上限的计算**（`Navigate` 起点处理）：

```
0x180a8d6df  call 0x1808CA250          ; eax = from.row * from.passes
0x180a8d6eb  mov  [rsp+0x38], eax      ; ★ 存为搜索上限 iVar3
 ...
0x180a8d769  cmp  [rsp+0x38], ecx
 ...
0x180a8d80d  sub  ebx,1 / jns ...      ; 主循环

Ghidra: iVar3 = FUN_1808ca250(param_2) ; while (iVar14 <= iVar3)   // param_2 = from
```

**`0x1808CA250` 本体**（全二进制只有 **1** 个调用者，即上面那处）：

```
mov  eax, [rcx + 0x24]     ; GridUnitData.row
imul eax, [rcx + 0x20]     ; × GridUnitData.passes
ret
```

> ⚠️ **符号名陷阱（2026-10 实测）**：导入 Il2CppDumper 符号后，`0x1808CA250` 的名字是
> `BattleMapData.get_GridCount` —— **这个名字是错的／至少是误导的**。它读的是 `[rcx+0x20]`
> 与 `[rcx+0x24]`，而调用点（`0x180a8d6df`）传给它的 `rcx` 是 **`from`（一个 `GridUnitData`）**，
> 不是 `BattleMapData`。所以实际算的是 `GridUnitData.row × GridUnitData.passes`。
> 若照着符号名去 `BattleMapData` 里找 `GridCount` 字段，会完全找错方向。
> **教训：符号名来自 metadata，不反映调用点的实际参数类型；必须结合调用处的寄存器内容判断。**

**现场实测**：

| 格子 | `gridType` | `passes` (+0x20) | `row × passes` |
|---|---|---|---|
| `(5,12)` 空地 | 1 | **15** | 75 |
| `(5,13)` 己方城墙 | 2 | **0** | **0** |
| `(6,12)` / `(6,13)` 城墙 | 2 | **0** | **0** |

**→ 从城墙出发时搜索上限 = `row × 0` = `0`，主循环一次都不执行。**

实测佐证：

```
Navigate((5,13) -> (5,14)) = false    searched=401
Navigate((5,13) -> (5,12)) = false    searched=401
Navigate((5,13) -> (4,13)) = false    searched=401
Navigate((5,12) -> (5,13)) = ok len=1 searched=1     ← 起点是空地，搜索正常跑
```

#### （六）这个根因解释了之前**全部**矛盾观察

| 观察 | 解释 |
|---|---|
| `Navigate(空地 → 城墙)` = `True` | 起点是空地，搜索正常跑；城墙作为**终点**由 `Equals`（`[r9+0x138]`）匹配 |
| `Navigate(城墙 → 任意格)` = `false` | 搜索上限 0，**主循环没跑**；`searched=401` 是 `ResetPool` 的残迹 |
| 高亮能亮但走不过去 | 高亮只调 `Navigate` 的**返回值**，而返回值无法区分「真穿」与「绕路」 |
| 绕 12 步而非穿 2 步 | 穿墙路线需要城墙作**中转节点**，而中转 = 从城墙继续扩展 = 上限 0 → **无法探索** |
| 9 个 obstacle 出现在 `searched` 里 | 是上一轮搜索的残迹，**不代表城墙被真正扩展** |

#### （七）结论

**障碍格的 `passes = 0` 不是巧合，而是游戏的设计意图：“障碍格不参与通行传播”。**

因此「把城墙当中转格」这条路在现有 `Navigate` 下**从原理上就走不通** ——
不是寻路判定严不严，而是**搜索根本不会从城墙格向外扩展一步**。

相应地，`NavigationData.G = G + 1` 那个“每步代价恒为 1”的分析虽然正确，
但对本问题**没有意义** —— 因为城墙节点从未被真正展开过。

**后续若要实现「跨墙」，必须解决 `passes = 0` 这个问题**，方向有三：

1. **改 `0x1808CA250`**：把城墙格的 `passes` 造一个非 0 值参与上限计算
   （例如 `max(row,1) × max(passes,N)`）。风险：该返回值语义可能被其他地方依赖。
2. **绕过 `Navigate`**：自己做一层「跨墙可达」判定（不依赖 `Navigate` 的返回值），
   但**必须自己实现与游戏一致的路径与代价**，否则又回到 §7.1 那个
   「高亮与可行路径分裂」的老坑。
3. **改 `passes` 字段本身**：让己方城墙的 `passes` 非 0。
   最直接，但 `passes` 可能被多个子系统读取，需先全量排查引用点。

> ⚠️ **本项目在城墙问题上反复走错的根本原因**：一直只盯「判定」与「出口地址」，
> 而没有去查**搜索本身能不能跑起来**。教训：当“输入对、判定对、但就是不生效”时，
> 应该去查**上限 / 计数 / 容量**这类**边界参数**，而不是继续在判定逻辑里找。

### 7.2 运行时汇编：**已从手写机器码迁移到 Iced**

**结论：2026-10 已完成迁移。三个 stub 全部改用 `Iced.Assembler` 生成，不再手写字节。**

**迁移的触发点**：手写汇编在本项目**真实崩过两次**，而且两次的症状都不是「编译报错」：

| 事故 | 根因 | 症状 |
|---|---|---|
| SIGILL（战斗刚开始即崩） | 把 rel8 的**操作数偏移**当成 **opcode 偏移**，回填时把位移写盖在 `0x74` 操作码上 | 立即崩溃 |
| AI 站到玩家头上 | 出口选在**接受路径的中段**，跳过了 `AroundGridHaveEnemy` | **不崩**，行为静默错误 |

第二类不是编码问题（Iced 帮不上忙，见 §4.2 教训 17–18），
但第一类**正是编码问题** —— 而它属于「只要还手写就可能再犯」的那一类。用户因此提出用汇编器（m0xxxx）。

**验证方式：逐字节对比，而不是「看起来能跑」**。
迁移的每一步都拿**迁移前的硬编码字节当基准**：

```
WallPassHook      手写 59 字节 / Iced 59 字节   ✅ 逐字节完全一致
FriendlyPassHook  手写 60 字节 / Iced 60 字节   ✅ 逐字节完全一致
```

包括四个 `jcc` 的位移（`1B` / `1F` / `19` / `0D`）与两个出口。
**离线**（独立小程序跑 `Iced`）与**活进程**（MCP `execute_csharp`）两路都验过。

> 这一点很重要：如果不是逐字节一致，就无法区分「迁移引入的新问题」与「原有的老问题」。
> **有已知正确的输出当基准，重构才是可验证的** —— 这是本次迁移能安全完成的前提。

**迁移后新增的两项能力**（手写时代做不到）：

1. **出口可重定位**。出口改用**尾部绑定标签** + 相对寻址的形态，
   而不是把绝对地址写死成立即数。位置无关，便于离线打基准与将来可能的原地热替换。
   （实测 `mov r11,imm64` 与 `lea r11,[label]` **输出逐字节相同**，
   本项目为保持与既有版本一致，当前用前者，但结构上已支持后者。）
2. **安装时自检出口**。`NativeHookBase.VerifyExits` 扫描 stub 里的出口立即数，
   与各 hook 声明的 `ExpectedExits` 对账，不一致就打 `ERROR`。
   → 把「出口跳错」从**玄学现象**提前成**安装日志里的一行**。

**安装顺序也跟着改了：先分配地址、再构码**（`BuildStub(long stubRip)`）。
可重定位出口需要在汇编时就知道 stub 自己会落在哪里，所以必须
`AllocateExecutable` → `BuildStub(rip)` → 校验码长 → 拷贝。
早期是「先构码、后分配」，那时 stub 还不知道自己的地址。

**为什么还是不用 Reloaded.Assembler**（原结论保留）：
它会向一个**已经因原生侧崩溃吃过两次亏**的 mod 再引入一个**原生 DLL 加载器**
（NuGet + `FASMX64.dll`，LGPL v3，需随 mod 分发，且在 Wine 下要额外验证）。
而 `Iced` 1.21 **进程里本来就有**（MelonLoader 自带，`MelonLoader/net6/Iced.dll`，1.9MB），
**纯托管、零 P/Invoke、零新增依赖** —— 没有任何理由选前者。

| | Iced | Reloaded.Assembler |
|---|---|---|
| 新增依赖 | **零**（进程内已在） | NuGet + 原生 `FASMX64.dll` |
| 文本助记符 | 无（强类型 API） | 有 |
| 自动分支编码 | `BlockEncoder` 自动选 rel8/rel32 | FASM 也做 |
| **P/Invoke** | **纯托管** | **P/Invoke 原生 DLL** |

**踩到的 Iced API 规则**（详见 AGENTS.md §6.1c）：一个指令位置最多绑一个标签、
标签之后必须有指令（所以不要建收尾标签）、内存操作数写 `__dword_ptr[...]` 而非 `dword_ptr(...)`。

> ⚠️ **迁移不改变的一件事**：Iced 只消除「手算编码」错误。
> **「hook 点选在哪」「出口跳到哪」仍然是靠读懂反汇编得到的判断** ——
> 本项目最大的两个坑都在这里，Iced 帮不上忙。

### 7.3 为什么判据是「原主自己的 `mapGrid`」而不是旁表

见 §4.2 第 10 条。一句话：**要让判据完全局部**，不能依赖任何跨回合状态。

---

## 8. 开放事项

| # | 事项 | 状态 |
|---|---|---|
| 1 | 城墙/城防穿越 | ✅ **已完成并实机确认可用**。最终实现是写 `GridUnitData.passes`（§2.2c），不是 hook 判定 |
| 2 | 城墙穿越崩溃 | ✅ **已结案**，由 `WallHighlightHook` 引起，已默认关闭（§8.1） |
| 4 | 「穿越不留痕」的时序验证 | `OnLeave` 在**离开**时触发，而覆盖发生在 **`OnEnter`** 时刻。若游戏在两格之间做了别的读取（如渲染），修复可能**太晚**。判据：若出现「中途闪一下被穿单位的模型消失」，说明太晚 → 需转「手写 `OnEnter` 替代实现」 |
| 5 | 箭塔/战鼓/分舵是否会被误穿 | 实测它们是**普通 `BattleUnit`**（`g.obstale == null`），归「穿友方」那条线按队伍处理。**当前无异常**，但守城战里需再看一眼 |

### 8.1 ✅ 城墙穿越崩溃（2026-10-04，**已结案**）

**结论：由 `WallHighlightHook` 引起，且它并非功能所需 → 默认关闭，问题消失。**

**二分过程**（每轮均冷启动，日志以构建指纹核实版本）：

| 轮次 | 配置 | 结果 |
|---|---|---|
| 1 | `wall_native_hooks=false` | **不崩**（`passes` 仍在，22 面）→ 排除数据写入与既有问题 |
| 2 | `wall_pass_hook=true` + `wall_highlight_hook=false` | **不崩，且城墙穿越可用** ✅ |

**为何高亮 hook 不是必需的**：`passes` 已让 `Navigate` 能穿墙；
而墙对面格子亮不亮，本来就由**游戏自己的** `GetMoveRangeGrids` 调 `Navigate` 决定。

**崩溃现场（存档）** —— 与结论一致：

```
exception : none recorded        <- FailFast/abort，不是野指针
#0        <ntdll.dll+0xEA94>    <- Wine 的 syscall 跳板，不是崩溃点
#9        BattleController::RunBattle+0x17C3
```

**`RunBattle+0x17C3` 的反汇编**（就在一条调用之后）：

```asm
0x18081C06E  call 0x1808C8B60     ; ★ BattleMapData::GetMoveRangeGrids
0x18081C073  mov  rcx, [r15]      ; <- dump 报的帧
```

**该处的源码形态**（Ghidra 反编译 `RunBattle_named.c`）—— **AI 单位分支**：

```c
if (NowActiveUnitCanMove(...)) uVar7 = HeroData_GetMoveRange(...);
BattleMapData_GetMoveRangeGrids(map, row, col, 0, uVar7, grids, selfTeamID, 0);
[controller+0x238] = -1.0f;
AISettingControlable(...);
```

→ 每个 AI 单位的回合都会经由 `GetMoveRangeGrids` 算移动范围，
而 `WallHighlightHook` 正是往这个函数里装两个 `ff25` detour
—— **与二分的结论完全一致**。

> ⚠️ **保留这段的价值：dump 只能做到「指出嫌疑」，不能「定罪」。**
> 当时列出的局限仍然成立：FailFast 不产生硬件异常（无 Exception 流），
> 帧标着 `(stack scan)`（启发式恢复的返回地址，不保证是精确 IP），
> 且 `DOTNET_DbgMiniDumpType=1` 不抓模块映像（看不到故障指令）。
> **真正定案靠的是二分实验，不是 dump。**
（当时列的待办已完成，见上方二分表。）

---

## 9. 参考文件

| 文件 | 内容 |
|---|---|
| `output/decomp_gud/BattleUnit_EnterGrid.c` | `EnterGrid` 反编译（含 `code_r0x0001808d169b` 标签） |
| `output/decomp_gud/BattleUnit_LeaveGrid.c` | `LeaveGrid` 反编译 |
| `output/decomp_gud/BattleUnit_LeaveBattleField.c` | `LeaveBattleField` 反编译 |
| `output/decomp_gud/GridUnitData_OnEnter.c` / `_OnLeave.c` | `battleUnit` 的两个唯一写者 |
| `output/decomp_gud/BattleUnit_f4670.c` | 城墙特效检查（良性，不碰 `battleUnit`） |
| `output/decomp_ghidra/RunBattle.c` | 状态机（1811 行） |
| `output/decomp_ghidra/BattleGridClicked.c` | UI 回调（789 行） |
| `output/decomp/full/Il2Cpp/` | 完整托管反编译（签名与 `NativeMethodInfoPtr_*`） |
| `tools/gdb_catch.sh` | 附加游戏进程、列候选代码区、捕获 SIGSEGV 并打印寄存器/回溯 |
| `output/dumper_out/dump.cs` / `script.json` | Il2CppDumper 产物（地址权威来源，`script.json` 存 RVA） |
