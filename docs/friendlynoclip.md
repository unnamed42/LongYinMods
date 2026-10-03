# FriendlyNoclip — 战斗格子地图穿越友方

> ⚠️ **本文件的地址、偏移、metadata token、调用链结论全部绑定到当前游戏构建**
> （`GameAssembly.dll` 33661952 字节 / `global-metadata.dat` 7959028 字节 / IL2CPP metadata **v27** / Unity 2020.3.48f1c1）。
> **游戏一更新即失效**，需按 `AGENTS.md` §4 的流程重新推导。
>
> 通用工具用法见 `AGENTS.md`，不重复。

**状态**：✅ **「穿越友方」+「穿越不留痕」已闭环**（用户 m01137 确认）。
⚠️ **「穿越己方城墙」代码已完成，但尚未实机验证** —— 判据需要 `obstale.teamID == selfTeamID`，
即玩家守城的战斗。已完成的只是回归验证：当前攻方场景下「不崩 + 穿不过敌方城墙 + 穿友方正常」。详见 §8。

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

### 2.1 两个开关，各管一件事

```toml
[FriendlyNoclip]
native_detour = true      # ★ 「能穿友方」的能力本身
wall_pass     = true      # ★ 「能穿己方城墙」
fix_occupancy = true      # ★ 「穿越不留痕」
diagnostics   = false     # 平时关掉，日志量大
dump_all_grids = false    # 极慢，排查用
```

> ⚠️ **三个功能性开关的分工必须分清**（曾因混淆而误判一轮）：

| 开关 | 作用 | 关掉会怎样 |
|---|---|---|
| `native_detour` | `Navigate` 内 `0x180a8d929` 的 detour，**允许穿过友方单位** | **穿不过友方** |
| `wall_pass` | `Navigate` 内 `0x180a8d8b6` 的 detour，**允许穿过己方城墙** | 穿不过己方城墙（其余不受影响） |
| `fix_occupancy` | `EnterGrid` Prefix + `OnLeave` Postfix，**写回被踩掉的登记** | 能穿，但**被穿的 NPC 点不动** |

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

### 2.4 当前挂载的补丁（6 个 Harmony + 2 个 native detour）

**Harmony：**
```
BattleMapData.GetMoveRangeGrids        -> Postfix  （审计）
GridUnitController.set_GridRenderType  -> Prefix   （审计/染色）
BattleController.BattleGridClicked     -> Prefix   （审计/点击）
GridUnitData.OnLeave                   -> Prefix   （[命中] 计数 + [登记] 诊断）
GridUnitData.OnLeave                   -> Postfix  （★ 穿越不留痕：写回原主）
BattleUnit.EnterGrid                   -> Prefix   （★ 穿越不留痕：记录被覆盖的原主）
```

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
**产物**：`FriendlyNoclip/bin/Debug/net6.0/FriendlyNoclip.dll`，md5 **`79ae85f82992323a8e4fd2aeb7d402f2`**，46592 字节，**0 错误 0 警告**。

> ⚠️ 部署后**务必核对 md5** —— 本项目因测了旧 DLL 而白耗过两轮。

| 文件 | 状态 |
|---|---|
| `FriendlyNoclip/NativeHookBase.cs` | **抽象基类** —— 安装/卸载骨架 + 共用工具（`RuntimeVa` / `EmitAbsoluteJump` / `EmitJumpViaR11` / `IsRel32Jcc` / `BytesEqual` / `Hex`）。子类只写差异：`Tag` / `HookVa` / `OriginalBytes` / `ValidateSite` / `BuildStub` |
| `FriendlyNoclip/FriendlyPassHook.cs` | **穿友方** —— hook `0x180a8d929`，按 `battleTeam.ID` 判队伍 |
| `FriendlyNoclip/WallPassHook.cs` | **穿己方城墙** —— hook `0x180a8d8b6`，按 `obstalceType + teamID` 判；含 `AddRel8`/`PatchRel8`/`VerifyStub` |
| `FriendlyNoclip/NativeMemory.cs`（286 行） | **有效** —— 模块定位 / 签名扫描 / 原生读写 / `VirtualProtect` |

**已删除**：`NativeOnEnterDetour.cs`（前提错误 + 尾调用崩溃）、`NativePatchProbe.cs`、`NativeProbe.cs`；开关 `native_probe` / `native_patch_probe` / `native_onenter_detour` / `allow_friendly`；函数 `CallerReturnAddress()`。

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

### 7.2 为什么不用 Reloaded.Assembler 做运行时汇编

**结论：能做、已验证可行，但决定不做**（用户 m01567：「好吧，那就算了。」）。

**替代品更好**：游戏进程里**已有 `Iced` 1.21.0**（`gamedir/MelonLoader/net6/Iced.dll`，MelonLoader 自带），含完整 `Encoder` / `BlockEncoder` / `Assembler` / `Decoder`，**纯托管、零 P/Invoke**。MCP 活进程实测它与现有硬编码逐字节一致，且能自动算 rel8 回填（`test al,al; je L; nop; L: nop` → `84 c0 74 01 90 90`，偏移是自动回填的）。

| | Iced | Reloaded.Assembler |
|---|---|---|
| 新增依赖 | **零**（进程内已在） | NuGet + 原生 `FASMX64.dll`（LGPL v3，需随 mod 分发） |
| 文本助记符 | 无（强类型 API） | 有 |
| 自动分支编码 | `BlockEncoder` 自动选 rel8/rel32 | FASM 也做 |
| **P/Invoke** | **纯托管** | **P/Invoke 原生 DLL**，Linux/Wine 下要额外验证 |

**决定性理由**：Reloaded.Assembler 会向一个**已经因原生侧崩溃吃过两次亏**的 mod 再引入一个原生 DLL 加载器。

**收益不足的理由**：手写代码只有约 **70 字节、9 条指令**，其复杂度不来自「手写机器码」，而来自两件与汇编器无关的事 ——（1）`[rsp+0xd8]` 依赖 `Navigate` 的栈帧布局；（2）三个出口地址是运行时算的。且该代码长期稳定运行，而它的邻居（`OnEnter` detour）出过两次崩溃，**风险/收益比不划算**。

**若将来要做**（分两步，别一次做完）：第一步只换 `BuildStub()` + `EmitAbsoluteJump()`，csproj 加 `<Reference Include="Iced"><HintPath>$(MelonLoaderDir)\Iced.dll</HintPath><Private>False</Private></Reference>`，用 `CreateLabel`/`Label` 替代 4 处 rel8 回填，**加 `diagnostics` 门控的字节对比验证**（Iced 输出与现有硬编码逐字节一致才切换）；第二步等要写更复杂 stub（如操作栈帧）时再考虑。

（`Assembler` API 要点：必须用 `CreateLabel(string)` 创建 label；`Label(ref Label)` 是 ByRef；**label 之后必须还有指令**，否则 `Assemble` 抛 `Unused label`。）

### 7.3 为什么判据是「原主自己的 `mapGrid`」而不是旁表

见 §4.2 第 10 条。一句话：**要让判据完全局部**，不能依赖任何跨回合状态。

---

## 8. 开放事项

| # | 事项 | 状态 |
|---|---|---|
| 1 | ~~城墙/城防穿越~~ | ✅ **已修复并定性**。出口从 `0x180a8d963` 改为 `0x180a8d8bc`，
活进程三方对照证实 `Navigate` 对相邻己方城墙格返回 **True**。**待用户实机确认。** |
| 2 | ~~验证 `expand` 路径~~ | ✅ **已结案：`expand` 是错的**（§2.2）。它位于准入判定**之后**，
跳过判定且寄存器依赖不成立。改用 `0x180a8d8bc`（pass）后两出口合一。 |
| 4 | 「穿越不留痕」的时序验证 | `OnLeave` 在**离开**时触发，而覆盖发生在 **`OnEnter`** 时刻。若游戏在两格之间做了别的读取（如渲染），修复可能**太晚**。判据：若出现「中途闪一下被穿单位的模型消失」，说明太晚 → 需转「手写 `OnEnter` 替代实现」 |
| 5 | 箭塔/战鼓/分舵是否会被误穿 | 实测它们是**普通 `BattleUnit`**（`g.obstale == null`），归「穿友方」那条线按队伍处理。**当前无异常**，但守城战里需再看一眼 |

### 8.1 城墙穿越崩溃（2026-10-04，未结案）

**现象**：`passes` 已正确写入（日志 `已放行 22 面己方城墙`），随后战斗崩溃。

**崩溃现场**（`tools/il2cpp_unwind.py` 分析 `crash_1791043849.dmp`）：

```
exception : none recorded        <- FailFast/abort，不是野指针
#0        <ntdll.dll+0xEA94>    <- Wine 的 syscall 跳板，不是崩溃点
#9        BattleController::RunBattle+0x17C3
#10       BattleController::Update+0xCB3
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
而 **`WallHighlightHook` 正是往这个函数里装两个 `ff25` detour**。

**结论强度（重要）**：

- ✅ 与「高亮 hook 有嫌疑」**一致**
- ❌ **但不是证据**：FailFast 不产生硬件异常，dump 里没有 Exception 流；
  且帧标着 `(stack scan)`（启发式恢复的返回地址，不保证是精确 IP）
- dump **看不到故障指令**（`DOTNET_DbgMiniDumpType=1` 不抓模块映像）

**已做**：拆出 `wall_native_hooks` 开关（单独控制 `WallPassHook` + `WallHighlightHook`），
`passes` 数据写入仍保留 —— 用于二分。

**待办**：`wall_native_hooks=false` 冷启动打完一场 →
- 不崩 ⇒ 定位到原生 detour，再细分 `WallPassHook` vs `WallHighlightHook`
- 还崩 ⇒ 与原生 hook 无关，嫌疑转向 `passes` 数据写入或既有问题

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
