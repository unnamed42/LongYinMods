# ShiftClickUpgrade — Shift+单击直接升级建筑

> ⚠️ **本文件的地址、字段偏移、metadata token、调用链结论全部绑定到当前游戏构建**
> （`GameAssembly.dll` 33661952 字节 / `global-metadata.dat` 7959028 字节 / IL2CPP metadata **v27** / Unity 2020.3.48f1c1）。
> **游戏一更新即失效**，需按 `AGENTS.md` §4 的流程重新推导。
>
> 通用工具用法见 `AGENTS.md`，不重复。

**状态**：✅ **功能已实机确认可用**（2026-10-04）

- ✅ 钩点已实机验证（`OnClick` 4/4 触发）
- ✅ 发现并避开一个**静默失效**的钩点（`set_buildMode`，见 §3.3）
- ✅ 四道闸全通，`★ 触发升级` + `[探针·升级入口]` 实机命中
- ✅ 音效已与原生对齐（`TabButton` + `WoodWork` 两个都放，见 §4.5）

> 💡 `probe_only` 必须为 `false`，且**改文件不生效** —— 见 §5.2b。

---

## 0. 判据速查（只想知道「能不能这么做」就读这一节）

**4 条硬约束**（§1 有来源）：只在按住 Shift 时改变行为（普通单击必须保持原样）·
**不得绕过游戏自己的前置检查** · 升级副作用必须与手动点按钮**等价** · AI 不得自行装工具。

**判据**：**优先复用游戏自己的升级入口**，不要自己写「扣资源 + 改 `lv`」——
自己赋 `AreaBuildingData.lv` 会漏掉计时、资源扣除、UI 刷新、`aroundBuildingRateChange`
等一串副作用（§1 约束 3 的直接推论）。

**★ 本项目代价最大的教训**（§2）：「X 是两条路径的交汇点」必须用**调用者扫描 / 活体验证**
确认，不能因为名字像入口就当成入口 —— `GenerateMovePath` 听起来像入口，AI 根本不走它。
→ 所以第一阶段**只做探针，不写副作用**。

**运行方式**：`probe_only` 必须为 `false`，且**改文件不生效**（用 MCP 改，见 §5.2b）。
本 mod 全部**手挂** Harmony，刻意不用 `[HarmonyPatch]` 特性（§6.2）。

---

## 1. 目标

按住 **Shift 单击**建筑图标时，**直接触发该建筑的升级**，不必先打开建筑 UI 再点「升级」按钮。

### 硬约束

| # | 约束 | 来源 |
|---|---|---|
| 1 | **只在按住 Shift 时改变行为**；普通单击必须保持原样（打开建筑 UI） | 用户 |
| 2 | **不得绕过游戏自己的前置检查**（资源、等级上限、ForceLv 门槛等） | 本项目一贯原则：不替游戏改写意图 |
| 3 | 升级的**副作用必须与手动点按钮等价**（扣资源、走计时、刷新 UI） | 同上 |
| 4 | AI 不得自行安装工具 | `AGENTS.md` §6 |

> 约束 2、3 的直接推论：**优先「复用游戏自己的升级入口」，而不是自己写一套扣资源 + 改 lv 的赋值**。
> 自己赋 `AreaBuildingData.lv` 会漏掉计时、资源扣除、UI 刷新、周边建筑速率重算（`aroundBuildingRateChange`）等一串副作用。

---

## 2. 🔴 为什么第一阶段只做探针

本项目代价最大的教训（见 [`friendlynoclip.md`](friendlynoclip.md) §2.2d）是：

> **「X 是两条路径的交汇点」这种判断必须用调用者扫描 / 活体验证确认，
> 不能因为名字听起来像入口就当成入口。**

当时 `BattleController.GenerateMovePath`「听起来像入口」，实际 AI 根本不走它 ——
结果玩家点击被拦住、**AI 却照样站上城墙**，白耗整整一轮。

**本 mod 的第一阶段因此只做探针，不写副作用。**
在一条没被验证过的链路上写副作用，失败形式会是**「静默无效」**或
**「看起来正常但语义错误」** —— 两者都比崩溃更难查。


---

## 3. 钩点（经 2026-10-04 实机探针验证）

| # | 钩点 | 作用 | 实机验证状态 |
|---|---|---|---|
| 1 | `AreaBuildingIconController.OnClick()` | **主入口**：建筑图标被点击 | ✅ **已触发 4/4** |
| 2 | `AreaBuildController.ChangeBuildMode(bool)` | **前置态**：锤子开关 | ✅ 可挂原生补丁（`patcherIsValid=true`） |
| 3 | `AreaBuildController.PlayerUpgradeBuilding(AreaBuildingData)` | 带目标的升级入口 | ❓ 调用可行，**但点「升级」时是否走到未验证** |
| 4 | `BuildingUIController.UpgradeButtonClicked()` | UI 升级按钮回调 | ❓ 未验证 |
| 5 | `BuildingUIController.SureUpgradeBuliding()` | 可能的二次确认 | ❓ 未验证 |
| ❌ | ~~`AreaBuildController.set_buildMode(bool)`~~ | —— | ❌ **静默失效，永不使用**，见 §3.3 |

### 3.1 第一轮探针结果（关键日志）

运行方式：[HotReload]（未冷启动），构建 `261004-042218`。用户点了 **4 次**同一建筑（药房）：

```
[12:24:40.409] [探针·图标点击] 第 1 次 | shift=False | buildingID=34 lv=0 areaID=8 name=药房
[12:24:40.987] [探针·图标点击] 第 2 次 | shift=True  | buildingID=34 lv=0 areaID=8 name=药房
[12:25:54.763] [探针·图标点击] 第 3 次 | shift=False | buildingID=34 lv=0 areaID=8 name=药房
[12:25:55.877] [探针·图标点击] 第 4 次 | shift=True  | buildingID=34 lv=0 areaID=8 name=药房
```

> 用户说明：**第 1/2 次没点锤子，第 3/4 次点了锤子**。

| 结论 | 依据 |
|---|---|
| ✅ `OnClick` 是可靠主入口 | 4/4 次全部命中，**与锤子开关无关** |
| ✅ `ShiftHeld` 读得到 | `shift=True/False` 与用户实际操作一一对应 |
| ✅ 拿得到目标建筑 | `buildingID=34 lv=0 areaID=8 name=药房` |
| ❌ **`OnClick` 无法区分锤子模式** | 开/关锤子四次都触发 → 必须另取前置态 |
| ❌ **升级链的三个钩点全部未触发** | `[探针·升级入口]` / `[探针·升级按钮]` / `[探针·确认升级]` 各 **0** 行 |
| ⚠️ 运行方式不规范 | `[HotReload]` 而非冷启动，见 §4.1 |

### 3.2 锤子在哪里，前置态叫什么

现场探查（MCP）：

```
path = Canvas / AreaUIPanel / BuildingQuickButtonPanel / BuildModeButton
comp: Button, MonoBehaviour
```

→ 锤子 = `BuildModeButton`，回调 = `AreaBuildController.BuildModeButtonClicked()`。

活体调用实测：

```
abc.BuildModeButtonClicked();   //  buildMode False -> True   ✅
abc.ChangeBuildMode(false);     //  buildMode True  -> False  ✅
```

→ **`AreaBuildController.buildMode`（`bool`）就是「锤子是否开着」。**

### 3.3 ⚠️ `set_buildMode` 是个**静默失效**的钩点（本 mod 第一个真实陷阱）

第一轮实现里我曾把 `set_buildMode(bool)` 当作可选钩点（想用属性 setter 监听 `buildMode`）。
在活进程里试挂后，游戏日志出现：

```
[12:26:52.485] [WARNING] [Il2CppInterop] Failed to init IL2CPP patch backend for
void Il2Cpp.AreaBuildController::set_buildMode(bool ), using normal patch handlers:
Method void Il2Cpp.AreaBuildController::set_buildMode(bool ) is a field accessor, it can't be patched.
```

`hook_patch_info` 也一致：

| 方法 | `patcherType` | 能否触发 |
|---|---|---|
| `ChangeBuildMode(bool)` | `Il2CppDetourMethodPatcher [IsValid=True]` | ✅ |
| `set_buildMode(bool)` | `ManagedMethodPatcher`（**无** `nativeEntryAddress`） | ❌ **永不触发** |

**为什么值得写下来**：这是 `AGENTS.md` 纪律 2（挂载成功 ≠ 触发）的又一实例，
而且它比已知的那几个**更隐蔽** —— 属性访问器在 IL2CPP 下**根本没有原生实体**，
Harmony 会默默地用托管 handler 接住，`Patch()` **不抛异常**。
如果不去读 `patcherType`，会得到一个「挂上了但永远不响」的钩子。

> ✅ **判据（已泛化）**：挂补丁前先查 `patcherType` ——
> `Il2CppDetourMethodPatcher` 才是真的；`ManagedMethodPatcher` 在 IL2CPP 下等于没挂。

### 3.4 `OnClick` 的 patcher 状态（`hook_patch_info`）

```json
{
  "target": "Il2Cpp.AreaBuildingIconController.OnClick",
  "signature": "Void AreaBuildingIconController.OnClick()",
  "nativeEntryAddress": "0x6FFFF698B050",
  "nativeEntryResolvedVia": "NativeMethodInfoPtr_OnClick_Public_Void_0 -> Il2CppMethodInfo 0x6537C1A8 -> methodPointer",
  "patcherType": "Il2CppInterop.HarmonySupport.Il2CppDetourMethodPatcher",
  "patcherIsValid": true
}
```

→ `patcherIsValid=true`，且**且已由日志坐实真的会触发**（4/4）。

> ⚠️ `entryBytes` 显示的是**原始序言**（`40 53 48 83 EC 40 …`），**不是** `ff 25`。
> 这不矛盾：`nativeEntryAddress` 是 `Il2CppMethodInfo.methodPointer`，
> 而这里的 IL 地址是**另一个**东西。`hook_patch_info` 自己会提示
> 「若 patcherIsValid 为 true 却看不到跳转，怀疑这不是真正的原生入口，去查 `nativeEntryResolvedVia`」。

### 3.5 相关类型结构（活进程 + 托管反编译交叉确认）

```
AreaBuildingIconController            AreaUnitController
  buildingData : AreaBuildingData       building : AreaBuildingIconController
  areaTile     : AreaUnitController     areaTileData : AreaTileData
  upgradeHintSprite : GameObject        OnClick() / OnHover(bool)
  OnClick() / OnHover(bool) / OnDrag / OnScroll

AreaBuildController (MonoBehaviour, 单例 Instance)
  buildMode : bool
  buildModeMovingBuilding : bool
  PlayerUpgradeBuilding(AreaBuildingData)   ← 候选 2
  ChangeBuildMode(bool) / EndBuildMode() / BuildModeButtonClicked()
  static UpgradeBuildNeedForceLv / NewBuildNeedForceLv /
         DestroyBuildNeedForceLv / MoveBuildNeedForceLv / UpgradeRoadNeedForceLv

BuildingUIController (MonoBehaviour, 单例 Instance)
  buildingData / targetBuildingData : AreaBuildingData
  UpgradeButtonClicked() / SureUpgradeBuliding()   ← 候选 3/4
  ShowBuildingUI(AreaBuildingData, Vector3) / HideBuildingUI()
  RefreshUpgradeButton() / RefreshBuildingUI()

AreaBuildingData (普通 Il2CppSystem.Object)
  buildingID / lv / areaID / belongHeroID
  buildTimeLeft / upgradeTimeLeft / destroyTimeLeft
  Name(bool withLv) / DataBase() / CanUpgrade() / GetUpgradeCostResource(float)
  BuildingAvailable() / GetBuyMoney()
```

> 💡 `AreaUnitController` 上还有 `OnClick()`，且它持有 `building : AreaBuildingIconController`。
> **建筑图标与地块是两个独立的 Click 接收者**，点击到底打到哪一个取决于碰撞体 ——
> 所以图标探针必须能区分「点了建筑」与「点了空地」。

---

## 4. 实现

### 4.1 验收操作步骤

1. 把 cfg 里的 `probe_only` 改为 `false`（见 §5.2）。
2. **冷启动游戏**（必须 —— 见 `AGENTS.md` §3.2.1）。
3. 确认启动日志里的 `【构建 …】` 与刚构建的一致：
   ```
   ShiftClickUpgrade 初始化完成：挂载 5 个补丁，enabled=True，probe_only=False。【构建 1.0.0 md5=44fefa2e…】
   已挂载补丁：AreaBuildingIconController.OnClick (IL=0x...) -> ...OnClick_Prefix | patcher=Il2CppDetourMethodPatcher [IsValid=True]
   ```
4. 进大地图 → **点锤子**（建造模式）→ **按住 Shift 点建筑**。
5. 预期：**直接升级，不弹建筑 UI**。读 `Latest.log` 确认分支（对照 §5.2 的表）。

### 4.1b 上一轮的探针操作（已完成，保留供参考）

进大地图 → 点锤子 → 点建筑 → 点「升级」→ 读 `Latest.log` 里的 `[探针·…]` 行。
### 4.2 已实现方案：四道闸

实现位于 `ShiftClickUpgradePatch.AreaBuildingIconController_OnClick_Prefix`。

> 💡 实现时**没有**依赖「`PlayerUpgradeBuilding` 是不是“点升级”时走的那条路」这个未知项 ——
> 我们把 `OnClick`（已 4/4 验证）作为入口，**主动调用** `PlayerUpgradeBuilding`，
> 而不是去钩一个还没验证的旧链路。这样即使原版 UI 走的是别的路，也不影响本功能。

（下文 §4.2b 保留当时的验证记录。）

### 4.2b ✅ 已实现细节

四道闸已全部落地，位于 `ShiftClickUpgradePatch.AreaBuildingIconController_OnClick_Prefix`。

```
AreaBuildingIconController.OnClick()  的 Prefix（返回 bool）：

  未按 Shift                        → return true  （放行原逻辑）
  enabled=false                     → return true
  probe_only=true                   → return true  （安全态，只记日志）
  buildMode=false（锤子没开）        → return true
  buildingData == null              → return true
  CanUpgrade() == false（或抛异常） → return true  （放行原 UI，拿到游戏原本的反馈）
  upgradeTimeLeft > 0（已在升级中）  → return false （闸④，见 §4.6）
  ────────────────────────────────────────────────
  上面全过 → PlayerUpgradeBuilding(data)，return false（拦掉原点击，不弹建筑 UI）
```

**关键细节：异常时一律 `return true`** —— 若异常时返回 `false`，
「我们的代码出错了」会表现为「点了没反应」，极难排查。

### 4.3 为什么是这三道闸

**闸②为何必需**：`OnClick` 在锤子**开/关两种情形下都会触发**（实机 4/4 证据），
不看 `buildMode` 就无法区分「点锤子后的 shift 点击」与「平时的 shift 点击」，
会改变普通状态下的行为 → 违反硬约束 1。

**闸③为何必需**：不预判就调，会在不可升级时让游戏自己撞墙（弹错或静默失败）；
先用 `CanUpgrade()` 筛一道，不可升级时直接放行原 UI，体验与手动操作一致。

### 4.4 实现时的三个硬约束（另有闸④，见 §4.6）

| 事项 | 规则 |
|---|---|
| **返回 `bool` 的补丁必须挂 Prefix** | 写成 Postfix 会被 Harmony 当成「透传 postfix」而报 `Return type of pass through postfix …` |
| **必须先确认原方法的返回值语义** | `OnClick()` 返回 `void` → Prefix 返回 `false` **安全**。<br>若目标是返回 `IEnumerator` 的方法，返回 `false` 会把 `null` 交给调用方 → **崩溃**（见 `friendlynoclip.md` §2.2d 的返回值表） |
| **改属性 setter 没用** | `set_buildMode` 等字段访问器在 IL2CPP 下**没有原生实体**，挂了也永不触发（§3.3） |

### 4.5 音效：一段值得完整记下的错判

这个子问题花了 **5 轮**才走对，而且每一轮的错误形式都不同。完整记下。

#### 需求

Shift 升级时应该听到**与游戏原生一样的**声音。

#### ❌ 错误一：以为「没声音」= 需要自己造一个

**最初的错误结论**：「游戏原本没有升级音效」。
依据是反汇编 `UpgradeButtonClicked`（RVA `0xb7d0d0`）后只看到：

```
AreaBuildingData.Name(bool) -> GetUpgradeTime() -> String.Format -> SureMenu.CallSureMenu
```

→ 以为它只弹确认框、不放声音。**这个推断本身就不成立** ——
「反汇编里没看到显式播音调用」不等于「这条路径不会发声」，
声音很可能在**被调用的下游**里。

#### ❌ 错误二：读按钮的 `UIPlaySound.clip` 当作升级音

现场把「升级」按钮读了出来，并做了同一性校验：

```
路径: Canvas / AreaUIPanel / BuildChoiceGrid / BuildChoiceButton(Clone)
按钮文字 = [拆除]  [迁移]  [升级]   —— 三个都是 UIPlaySound.clip = TabButton
mod 找到的 TabButton 与按钮上的 TabButton：instanceID 相同（=7104，同一对象）
```

看起来“证据链完整”。**但方向从一开始就错了**：
`TabButton` 是**按钮按下时的反馈音**，与「升级」这件事无关 ——
拆除、迁移、升级三个键挂的是**同一个**音。

> 📌 **反证其实就在数据里**：全场景 `TabButton` 出现 **166 次**。
> 一个“升级专用音”不可能被 166 个地方共用。
> 我却把这个数字当成了“找对了”的旁证 —— **它恰恰证明相反的事**。

#### ✅ 决定性实验（用户提出）

用户直接把「升级」按钮的 clip 改成 `BigButton`，然后点它：

```
结果：BigButton 响了，同时“建筑升级音效”也还在响
```

**两个声音同时存在** —— 这就否证了“升级音走按钮组件”。
音效是**两条独立路径**：点击反馈 + 真正的升级音。

> 💡 这个实验设计得很漂亮：它不是“换个值看看”，
> 而是**用一个必然可辨认的信号去切分两个共存的来源**。

#### ✅ 正确方法：挂汇聚点，让日志回答

游戏全部 UI 音效都汇聚到 `NGUITools.PlaySound(AudioClip, float, float)`
（`UIPlaySound.Play()` 反汇编后尾调用到它）。挂它的 Prefix：

```csharp
harmony.Patch(select(NGUITools, "PlaySound", argCount: 3), prefix: ...)
```

⚠️ `PlaySound` 有 **3 个同名重载**，必须按参数个数选
（见 [harmony-il2cpp.md](harmony-il2cpp.md) §5.1）。

点一次真实「升级」，日志给出答案（`13:14:42.423`，升级“岗哨”）：

```
13:14:41.859  [图标点击] 岗哨   + TabButton   ← 点击反馈
13:14:42.423  Woosh + WoodWork + TabButton    ← ★ 升级执行
```

| clip | 长度 | 作用 |
|---|---|---|
| `TabButton` | 0.23s | 点击反馈（与拆除/迁移共用） |
| **`WoodWork`** | **2.48s** | ★ **升级施工声** |
| `Woosh` | 0.34s | 面板过渡声 |

→ **升级音 = `WoodWork`**，已由用户听感确认。

#### ⚠️ 错误三：只补一个声音

确定 `WoodWork` 后，我把 `TabButton` **换成了** `WoodWork` —— 又错了。
用户反馈：「还是普通按钮的点击音效」。

因为游戏原生是**两个都发**的，我只放一个，剩下那个自然就显得“不对”。
→ **最终实现：两个都放**（`TabButton` + `WoodWork`）。

#### ⚠️ 错误四：播放方式

用过 `AudioSource.PlayClipAtPoint` —— 那是**新建 3D 衰减声源**，
而游戏 UI 音走 `NGUITools.PlaySound`（复用 `Main Camera` 的 `AudioSource`，`spatialBlend=0`）。
即使 clip 一模一样，另开一路也会听感不同。

→ 改成建一个 `UIPlaySound` 宿主，调它的 `Play()`（游戏自己的路径）。

> ⚠️ 宿主对象**不能加 `DontDestroyOnLoad`** ——
> 实测加了之后对象立刻就找不到了。改挂到 `Canvas` 下。
> 另：**每个 clip 一个播放器**（`UIPlaySound.clip` 是实例字段，共用会互相覆盖）。

#### 关键实现坑：这些 clip **不在 `Resources` 里**

| 写法 | 结果 |
|---|---|
| `Resources.Load<AudioClip>("TabButton")` | ❌ **恒返回 null** |
| `Resources.FindObjectsOfTypeAll<AudioClip>()` | ✅ 能找到 |

它们被场景 / 预制体直接引用，不在任何 `Resources/` 目录下。

> 📌 **泛化教训**：`Resources.Load` 失败**不代表资源不存在**。
> 另外：`FindObjectsOfTypeAll` 返回 `Il2CppArrayBase<T>`，**不支持 LINQ**（报 `CS1061`）。

#### ★★ 真正的 bug：clip 是**懒加载**的（用户提出）

确定 `WoodWork` 后依旧听不到，用户提了关键猜想：**「是不是游戏做了懒加载？」**
—— **完全正确。**

| 时刻 | 内存里的 clip 数 | `WoodWork` 在吗 | 结果 |
|---|---|---|---|
| 13:26（我们 shift 升级） | 58 | ❌ **不在** | 解析失败 → 只响了 `TabButton` |
| 13:37（手动点了几栋建筑后） | 61 | ✅ **在** | 可解析 |

→ 这些 clip **不是一开始就在内存里**的，
它们随「打开建筑 UI / 首次升级」才被游戏加载。

**而我的实现把“查不到”当成了结论**：

```csharp
if (_upgradeSound == null) { _upgradeSound = FindLoadedClip(...); }   // ❌
```

叠上另一个设计缺陷就成了**静默失败**：`PlayViaUIPlaySound(null)` 直接 `return`、
**不打任何日志** —— “没声音”既没报错也没线索。

**修法**：

```csharp
ResolveSound(ref _upgradeSound, UpgradeSoundName);   // ✅ 失败不缓存，每次重试
```

代价是每次点击多扫一遍 clip 数组（实测约 60 个元素），相比一次声音播放可忽略。

#### ✅ 进一步：**主动加载**（用户提出「能不能主动加载」）

上面只解决了“下次会重试”，但玩家还是得**先手动点一次升级**才能听到声音。

**✅ 可行的方法：`Resources.LoadAll<AudioClip>("")`**

| 写法 | 结果 |
|---|---|
| `Resources.Load<AudioClip>("WoodWork")` | ❌ 恒为 null（各种子路径也都不行）|
| `Resources.FindObjectsOfTypeAll<AudioClip>()` | ⚠️ 只能看到**当时已加载**的（实测 58～61 个）|
| **`Resources.LoadAll<AudioClip>("")`** | ✅ **359 个**，且能**主动把尚未加载的也取出来** |

返回实例与运行时用的是**同一份**（`GetInstanceID` 比对一致），
所以不需要玩家先手动点一次。

最终 `FindLoadedClip` 的策略：**先扫已加载的（快），未命中再 `LoadAll` 主动加载**。

> 📌 **`Resources.Load(name)` 失败 ≠ 资源不存在**，也不等于“只能等它自己加载”。
> `LoadAll("")` 能枚举整张资源表 —— 这是一个比逐个猜路径可靠得多的入口。

> 📌 **泛化教训**：**「查不到」可能是暂时状态，不是结论。**
> 对懒加载资源，失败必须可重试，且**失败路径必须有日志** ——
> 静默 `return` 会把“还没加载”伪装成“功能没实现”。

#### 📌 本节最值得记住的四件事

1. **「引用了 X」≠「X 就是正在用的那个」**。
   按钮上挂着 `TabButton`，但升级的声音是**两个共存来源**中的一个（`WoodWork` 在别处）。
2. **全局统计是双刃剑**。`TabButton` 出现 166 次 ——
   这个数字既可以说“它很重要”，也可以说“它太通用、不可能是专用音”。
3. **对懒加载资源，“暂不可得”≠“不存在”**。失败路径必须可重试且打日志。
4. **用户提出的实验往往比苦读反汇编快**。
   “改成 BigButton 看两个声音是否并存”与“是不是懒加载”两个猜想，
   都直接锚定了问题 —— 而我之前在反汇编里绕了好几轮。

> ⚠️ **并且要相信已经验证过的结论**：我在这一段里一度因为
> “13:26 没出现 WoodWork”而**自我否定**了 `WoodWork` 是升级音这个
> 已经由用户听感确认的事实。"某次没出现" 的原因是**懒加载**，
> 不是 "它不是升级音" —— **不要把“现象缺失”当成“结论推翻”**。

#### 保留的取证工具

`NGUITools.PlaySound` 的 Prefix 探针**保留下来了**（`sound_probe`，默认关）：
游戏更新后音效若变了，开它点一下就能重新抓到真名。

### 4.6 刚升级完的建筑会被**重复升级**

**实机问题**（用户发现）：「正在升级的建筑会重复播放 WoodWork」。

日志坐实（同一栋「工棚」在 1.2 秒内被触发两次）：

```
13:45:40.806  [探针·图标点击] 第 22 次 | buildingID=41 工棚
13:45:40.806  ★ 触发升级：工棚
13:45:42.068  [探针·图标点击] 第 23 次 | buildingID=41 工棚   ← 同栋、同一个等级
13:45:42.069  ★ 触发升级：工棚                              ← 又升一次！
```

#### 根因：`CanUpgrade()` 在“已在升级中”时**仍返回 True**

所以闸③拦不住它。

#### 修法：加闸④，看 `upgradeTimeLeft`

`AreaBuildingData` 的 `upgradeTimeLeft`（升级剩余时间）：

| 值 | 含义 |
|---|---|
| `0` | 空闲，可升级 |
| `>0` | **正在升级中** |

```csharp
if (data.upgradeTimeLeft > 0) { return false; }   // 已在施工，不重复触发
```

> ⚠️ **为什么不用“同一格短时间只能点一次”的节流**：
> 那样会挡住「刚升完、立刻再升一级」的合法操作。
> 用游戏自己的状态字段才语义正确。

返回 `false`（而非 `true`）：仍然拦掉原点击、不弹建筑 UI ——
玩家“点了没反应”的观感与“已经在升级了”一致，不会困惑。

### 4.7 ★★ 方案转折：**触发 UI 元素**，而不是直接调 callback

**用户提出的思路**：「能不能通过触发 UI 元素，而不是直接代码调用各种 callback
来手动实现相同效果？」

**→ 这是整个 mod 最关键的一次思路修正，已验证完全可行。**

#### 起因：道路走不通

用户报告：「shift 点击道路（也是可以升级的）没有触发升级」。

**根因：道路格上没有 `AreaBuildingIconController`。**
建筑与道路是**两个不同的点击接收者**：

| 目标 | 对象 | 组件 |
|---|---|---|
| 建筑 | `AreaBuildingUnit(Clone)` | `AreaBuildingIconController` |
| **道路 / 空地** | `14_14` 这类地块 | **`AreaUnitController`**（实测 137 个道路格）|

数据结构也不同：

```
AreaUnitController.areaTileData : AreaTileData
  ├─ building      : AreaBuildingData   （建筑格）
  └─ areaRoadData  : AreaRoadData       （道路格：roadLv / upgradeTimeLeft）
```

而 `PlayerUpgradeBuilding(AreaBuildingData)` **只收建筑** ——
翻遍 `AreaBuildController` / `AreaController` / `AreaUnitController`，
**根本没有 `PlayerUpgradeRoad`**（只有门槛字段 `UpgradeRoadNeedForceLv`）。
按旧思路，我得再去找一条没验证过的道路入口。

#### 新思路：直接“点按钮”

游戏的按钮 `onClick` 上绑的本来就是完整入口，
调 `onClick.Invoke()` 等价于「玩家点了这个按钮」。

**实测验证**（2026-10-04 14:01:17，对道路 `4_5` 调
`BuildChoiceButton.onClick.Invoke()`）：

```
roadLv=2  upgradeTimeLeft: 0 -> 1     ← 真的开始升级了
音效: Woosh + WoodWork + TabButton    ← 与手动点击完全一致
buildTargetObj: 4_5 -> null           ← 菜单关闭，流程完整走完
```

三项都对了 —— 而且**音效根本不是我们补的**，是游戏自己发出来的。

#### 为什么这比“找 callback”好得多

| | 直接调 callback | **触发 UI 元素** |
|---|---|---|
| 要知道升级代码在哪 | ✅ 必须 | ❌ **不需要**，游戏自己走 |
| 音效 / 资源扣除 / UI 刷新 / 计时 | 得**逐个手工补**（本 mod 为此烧了好几轮）| ✅ **自动全对** |
| 建筑与道路 | 两套不同入口 | ✅ **统一**（都是“点那个升级按钮”）|
| 前置检查 | 得自己判断（还得猜到哪些字段）| ✅ **游戏自己做** |
| 游戏版本更新 | 入口一变就失效 | ✅ **耐改** |

#### 实现要点

| 事项 | 做法 |
|---|---|
| 找按钮 | 按**文字**扫（两个升级按钮的文字都是「升级」），**不写死层级** —— 菜单是运行时 Instantiate 的 |
| 可用性 | 先查 `button.interactable`；不可用则**什么都不做**（见下）|
| 触发 | `button.onClick.Invoke()` |
| 回退 | **无**（旧版有，已删除 —— 见 §4.10）|

> ⚠️ **局限**：按钮必须先存在。建筑/道路菜单都是玩家点开后才 Instantiate 的。
> 我们是从 `OnClick` 进来的，那时游戏刚处理完点击、菜单已在 —— 所以刚好赶得上。
> 但这也意味着**这个方案依赖于“菜单会被打开”这个前提**。

> 📌 **泛化教训**：**「找到正确的内部函数」不一定是好解法。**
> 当内部入口分散、难找、且会连带一大串需要复现的副作用时，
> **退一步去触发那个已经存在的 UI 入口**，往往同时解决了“找入口”和“复现副作用”两个问题。
> 代价是要接受它的前置依赖（这里是“按钮得在”）。

#### ⚠️ 实施后踩到的两个问题（均已修）

**① 首次点击卡顿约 4.3 秒**（用户报告：「首次点击僵死了一段时间」）

用户推测是主动加载 `WoodWork` 导致的 —— **完全正确**。日志坐实：

```
14:07:40.811  触发升级
14:07:45.085  主动加载音效 'WoodWork'（LoadAll 共 359 个）   ← 4.3s 后！
```

`Resources.LoadAll<AudioClip>("")` 是**同步**的，扫整个资源表要几秒，
而它又在**点击的热路径**上 —— 于是点一下卡半天。

**修法**：`LoadAll` **只跑一次**（`_audioTableScanned`），之后再也不碰。
代价是“若首次扫描时游戏还没有某 clip 就永远拿不到”，
但这个权衡明显比“每次卡 4 秒”好。

> 📌 **泛化教训**：**“能主动加载”不等于“随时可以加载”**。
> 同步的资源加载 API 放在交互热路径上，就是一个卡顿源。
> 要么缓存、要么异步、要么一次性。

**② “没有通过 UI 按钮触发升级”**（用户报告）

日志里**一次 `[探针·地块点击]` 都没有**，全是 `[探针·图标点击]`（建筑）。
一开始怀疑补丁没挂上 —— 但 `hook_patch_info` 显示
`AreaUnitController.OnClick` 的 `attached=true`、`prefixes=1`。

于是直接调了一次验证补丁本身：

```
[探针·地块点击] 第 2 次 | shift=False | isRoad=True roadLv=2 | 物体=9_5
              ↑ 补丁工作正常，道路数据也读到了
```

**结论：补丁没问题，是那几次点击落在了建筑上（或空地上）。**
道路格与建筑格在屏幕上是分开的，**得真的点在道路格上**。

> 📌 **排查顺序的价值**：这次差一点又去改代码。
> 「先直接调一次目标方法，确认补丁/逻辑本身对不对」
> 一步就把“代码错”与“输入不对”分开了。

**顺带澄清一个易混点**：道路格是 `AreaGridRoot` 下形如 `9_5` / `14_14` 的对象，
`tileType` 可能是 `Road`（已铺路）也可能是 `EmptySpace`（带 `areaRoadData` 但还没铺）。
而名为 `RoadDecoration` 的那一堆只是**贴图装饰**（无组件、无碰撞体）。

**③ 道路要“点两次”才能升级**（用户报告）

用户：「需要点两次，第一次弹出来菜单，第二次才能走到升级。
我想要的一次点击就能直接走到升级」。

#### 根因：**Prefix 里永远找不到按钮**

菜单与「升级」按钮是游戏在 `OnClick` **执行过程中**才创建/激活的。
实测（同一时刻数可见的「升级」按钮）：

```
调用 OnClick 前：可见的「升级」按钮数 = 0
调用 OnClick 后：可见的「升级」按钮数 = 1
```

而我的升级逻辑写在 **Prefix** 里 —— 那时按钮还不存在，
必然走回退路径（直接调 `PlayerUpgradeBuilding`），对道路无效。
第二次点击时菜单已在，所以能成功 —— 这就是“点两次”。

#### 修法：Prefix 记意图，Postfix 做升级

| 阶段 | 职责 |
|---|---|
| **Prefix** | 判断「Shift + 道路」→ 置 `PendingUiUpgrade = true`，**放行**原方法让游戏建菜单 |
| **Postfix** | 消费标记，此时菜单已建好 → 找到按钮 → `onClick.Invoke()` |

```csharp
// Prefix
if (shift && isRoad) { PendingUiUpgrade = true; }
return true;                    // 让游戏把菜单建出来

// Postfix
if (!PendingUiUpgrade) return;
PendingUiUpgrade = false;
TryUpgradeViaUiButton(__instance, roadLv);   // 此刻按钮已存在
```

> ⚠️ **用 `ref`/`out bool __state`，不要用静态字段**（用户指出）：
> 静态字段会被并发/重入污染，而 `__state` 是 Harmony 官方的
> **per-invocation** 机制（详见 [harmony-il2cpp.md](harmony-il2cpp.md) §5.4）。
> 实测绑定形态（`hook_patch_info`）：
> ```
> prefix : Boolean AreaUnitController_OnClick_Prefix(AreaUnitController __instance, Boolean& __state)
> postfix: Void    AreaUnitController_OnClick_Postfix(AreaUnitController __instance, Boolean __state)
> ```

> 📌 **泛化教训**：**“钩在方法前面”不一定可行 —— 要看依赖的对象何时存在。**
> 当你要用的 UI/资源是**目标方法自己创建**的，Prefix 就必然看不见它。
> 判据：**先查那个对象在 Prefix / Postfix 两个时刻分别存不存在**
> （本例就是数按钮个数，一步定价）。
> 这与 `friendlynoclip.md` §4.2 的“Prefix 写回无效 / Postfix 读本格无效”
> 是同一类错误：**Prefix / Postfix 的时序差异是有语义的**。

**④ 道路连续点击仍会触发两次升级**（用户报告）

用户：「前两次 shift 单击都能触发到升级（第二次就不应该再触发了），
第三次应该是被拦截了，会弹出原生菜单。不过就算点了多次，
升级行为也只升一级，这个还是正常的。」

#### 根因：道路没有和建筑一样的闸④

建筑的防重复靠 `AreaBuildingData.upgradeTimeLeft > 0`（见 §4.6）。
而道路的 `AreaRoadData` **也有完全同名的 `upgradeTimeLeft`**，
但我当时只在建筑那条路加了闸，**道路漏了**。

前两次能触发的原因：升级**刚发起**时 `upgradeTimeLeft` 可能还没被游戏写入，
所以第一、二次点击时它仍为 0；第三次游戏已经写入，于是（在菜单侧）被拦住。

#### 修法：给道路补上同一道闸

```csharp
if (road.upgradeTimeLeft > 0)
{
    CloseBuildMenuSafely();   // 已在施工 → 不重复升级，也不把菜单留在屏幕上
    return;
}
```

> 📌 **泛化教训**：**给一条路径加了防护，要主动检查“兄弟路径”是否也需要。**
> 建筑与道路的数据结构几乎同构（都有 `upgradeTimeLeft`），
> 但前者加了闸、后者漏了 —— 这类遗漏不会报错，只会表现为“偶尔多触发一次”。
> 修法不只是补这一处，还要问：**还有哪条路径共享同一个语义？**

### 4.8 ★ 建筑也统一到 UI 触发（用户提议，已验证）

用户：「建筑的升级也能走 UI 菜单的方式吗？我发现这两个现在行为好像不一样。
你先 mcp 验证一下是否可行，要是可行的话我们一开始弄的方式就可以去掉了，
那个方式不保险。」

#### 验证结果：✅ 可行，而且比预想的更彻底

**关键发现：建筑与道路共用同一个菜单。**

```
锤子开启 → 点建筑 → Canvas/AreaUIPanel/BuildChoiceGrid 出现
                        ├─ 拆除
                        ├─ 迁移
                        └─ 升级   ← 同一个按钮
```

实测对建筑调该按钮的 `onClick.Invoke()`：

```
摊贩:  upgradeTimeLeft  0 -> 1        ← 真的升级了
音效:  Woosh + WoodWork + TabButton   ← 与手动点完全一致
```

> 💡 另一个 `Canvas/BuildingUIPanel/BuildingUI/ExtraButtonGrid/UpgradeButton`
> 是**另一套**（非锤子模式的建筑 UI），实测其父 panel `activeSelf=False`，
> 锤子流程根本不走它。**别把两者搞混。**

#### 于是删掉了旧方案

旧方案（建筑专用）是：直接调 `PlayerUpgradeBuilding` + **手工补两个音效**
（`TabButton` + `WoodWork`，包含了那个 → 359 个 clip 的 `LoadAll`、
`UIPlaySound` 宿主对象、`FindLoadedClip` 等一整套）。

现在建筑与道路**同构**，全部删掉：

| 删除的东西 | 行数 |
|---|---|
| `PlayClickSoundSafely` / `PlayViaUIPlaySound` / `ResolveSound` | |
| `FindLoadedClip`（含 `LoadAll` 主动加载）、`GetOrCreatePlayer`、`ClickPlayers` | |
| `ClickSoundName` / `UpgradeSoundName` / `_clickSound` / `_upgradeSound` / `_audioTableScanned` | **共 335 行** |

> ✅ **留下的**：`NGUITools.PlaySound` 探针（`sound_probe`，默认关）——
> 它现在是纯取证工具，不再参与功能。

#### 两条路径如今完全同构

```
建筑：AreaBuildingIconController.OnClick  Prefix(__state) → Postfix → 点「升级」按钮
道路：AreaUnitController.OnClick          Prefix(__state) → Postfix → 点「升级」按钮
```

两者都是：Prefix 记意图并放行（让游戏建菜单）→ Postfix 拿现成按钮触发。

> 📌 **本节最大的教训**：**“手工复现副作用”是一笔重债。**
> 回过头看，本 mod 在音效上烧的那几轮、`CanUpgrade` 猜错的那次、
> 以及“道路找不到入口”——**根源都是“不用 UI 入口、自己调内部函数”**。
> 一旦改走 UI 触发，这些债一次全部消失。
> 用户提议“先 MCP 验证可行性再改造”，比直接开工更省事 ——
> 先花几分钟验证，避免推翻一个已经在运行的设计。

### 4.9 ★★ 一次点击触发三次升级（用户发现「多扣资源」）

**用户报告**：「shift 升级多扣了资源。四级磨坊升级消耗 1250 银钱和粮食；
shift 单击会消耗掉 2500 资源。」

#### 先回答：2500 是**正确值**，不是多扣

用户当时用 Cheat Engine 定位到一个资源数组，反查它的结构：

```
klass      = 0x64E350F8   （与已知 List<float>.items 的 klass 一致）
max_length = 8
vector[0]  = 54600        <- 银钱
vector[1]  = 10755.56     <- 粮食（浮点存储，所以非整数）
```

`GetUpgradeCostResource()` 返回 `[1250, 1250, 0, 0, 0, 0]` ——
**前两项各 1250**（银钱 + 粮食），合计 2500。

> 用户原话「1250 银钱和粮食」= 银钱 1250 **且** 粮食 1250。
> 所以单次升级扣 2500 是原版行为。之前“不扣”是因为另一个 mod 去掉了消耗。

> ⚠️ **那个内存地址不可复用**（用户后续指出：**每次读档都会变**）。
> 本节只保留结论，**不记地址** —— 定位数据请用字段路径。
> 详见 [`game-internals.md`](game-internals.md) §2.1 的“内存地址不可当锚点”。

#### 但确实存在重复触发 —— 一次点击升了 **3 次**

日志（`15:02:00`，点的是「磨坊」，`[探针·图标点击]` 只出现 **1 次**）：

```
★ 通过 UI 按钮触发升级（建筑 磨坊）        <- ① 建筑 Postfix
★ 通过 UI 按钮触发升级（roadLv=4）         <- ② 地块 Postfix
★ 直接调用升级（回退路径）：磨坊            <- ③ 旧代码的返回值 bug
```

三个**独立**缺陷叠加：

| # | 缺陷 | 说明 |
|---|---|---|
| ① | 建筑与地块**各自**收到 OnClick | 同一屏幕位置的「建筑图标」与「地块」是**两个接收者**，一次鼠标点击给两者都发了事件。于是两个 Postfix 都跑，都 Invoke 了同一个按钮 |
| ② | **返回值语义接反** | `TryUpgradeViaUiButton` 返回 `true`=放行 / `false`=已接管，而调用处写 `if (!TryUpgradeViaUiButton(...))` —— **成功时反而走了回退**，又调了一次 `PlayerUpgradeBuilding` |
| ③ | 闸④ 拦不住 | 前两次都在 `upgradeTimeLeft` 写入**之前**发生（相隔约 20ms），所以防重复闸没生效 |

#### 修法

**② → 用枚举消除歧义**（这类错误不该靠“下次小心”）：

```csharp
private enum UiUpgradeResult { NotHandled, Upgraded }
```

调用处从 `if (!TryUpgradeViaUiButton(...))` 改成
`if (result == UiUpgradeResult.NotHandled)`，语义**无法**再接反。

**① → 跨补丁去重**（两个 Postfix 共享一份状态）：

```csharp
private const int UpgradeDedupWindowMs = 75;

// 触发成功时记时间戳 + 目标指针
MarkUpgradeTriggered(targetPtr);

// 两个 Postfix 各自开头检查
if (ShouldSkipAsDuplicate(GetObjectPtr(__instance))) return;
```

##### ★ 判据用「同一目标 + 极短间隔」，而不是单纯的时间窗

**用户指出**：「间隔可以再缩短一点，我连续点击下中间的应该会被识别成重复。」

用户是对的 —— 旧版 250ms 纯时间窗**误杀了真实连点**。日志证据：

```
【真正的重复】同一次点击的两个事件 —— 同一物体，间隔 2～6ms
  15:31:58.386  [探针·地块点击] 物体=8_7
  15:31:58.640  [探针·地块点击] 物体=9_7    <- 不同物体，这才是真人点击

【被 250ms 误杀的真点击】—— 全是不同物体
  249ms / 240ms / 190ms / 160ms
```

于是判据升级为**两个维度同时满足**：

| 判据 | 真重复 | 真人连点 |
|---|---|---|
| **目标对象** | **同一个**（同一次点击的两个接收者指向同一目标）| **不同**物体 |
| **间隔** | 2～6ms | ≥130ms（实测最小 160ms）|

```csharp
bool sameTarget = (targetPtr != 0) && (targetPtr == _lastUpgradeTargetPtr);

if (sameTarget && delta < 75) return true;   // 才算重复
```

**75ms** 远大于重复间隔（2～6ms）、远小于真人连点（≥130ms），两边都有充裕余量。

> 📌 **教训**：**单一阈值判据往往过粗**。当能拿到一个**语义上更本质的维度**
> （这里是“是否同一个目标”）时，用它做主判据、时间只当辅助，
> 误判率会低得多 —— 而且阈值可以取得更宽松，不必在“太紧漏掉重复 / 太松误杀”之间纠结。
>
> 而且 `GetObjectPtr` 取不到时返回 0、`sameTarget` 即为 false →
> **退化为“不去重”**。宁可能重复（用户会看到两次音效），也不错杀（功能失效）。

> 用 `Environment.TickCount` 并**用减法比较**（它在 C# 里溢出环绕是定义良好的）。

#### ⚠️ 去重本身又踩了一个坑：哨兵值让功能全废

去重上线后，用户立刻报告**「现在完全不触发了」**。日志：

```
[探针·图标点击] 第 1 次 | shift=True | 磨坊
同一次点击已触发过升级（-1762815977ms 内），跳过重复。   <- 负数！
```

**根因**：时间戳初值用了 `int.MinValue` 当哨兵，于是首次计算

```csharp
unchecked(now - int.MinValue)   // = 一个巨大的负数
```

而 `负数 < 250` **恒成立** —— 首次点击就被判为“重复”，永不升级。

**修法**：加一个显式的 `_hasUpgradeTriggered` 标志来区分“从未触发过”，
**不要**靠给时间戳设哨兵值来表达这个状态。

```csharp
if (!_hasUpgradeTriggered) return false;   // 从未触发 → 不是重复
int delta = unchecked(now - _lastUpgradeTick);
```

> 📌 **教训**：**“用特殊值表示“无””是个陷阱**，在配合溢出算术时尤其危险。
> `int.MinValue` 在 `unchecked` 减法下不再是“很小”，而是**回绕成大正数**，
> 让 `x < 阈值` 这类判断彻底失效。
> 用**独立的布尔标志**表达“有没有”，把时间戳只当时间戳用。

> 📌 **另外两条泛化教训**：
> 1. **`bool` 返回值承载“方向性”语义时容易接反**（`true` 是“成功”还是“该放行”？）。
>    当同一个 bool 既要表达“做了什么”又要表达“你该做什么”时，**换成枚举**。
> 2. **“同一个 UI 点击被多个补丁各收到一次”是常事** ——
>    只要两个钩子挂在同一事件的**不同接收者**上，就要考虑去重。
>    本例里建筑与地块在屏幕上重叠，游戏给两者都派发了事件。
>
> **并且去重窗口的“首次”判断必须单独处理** —— 那是这次翻车的直接原因。

### 4.10 删除“回退路径”：它不只是多余，而是**错的**

**用户提议**：「回退路径还留着么？我觉得是不是可以删掉了。不过之前探索得到的经验可以放文档里。」

查证后确认该删 —— 而且理由比“没用”更强。

#### 事实依据

自 UI 触发方案上线以来，日志里 **`回退路径` 出现 0 次**（23/23 次升级全部走 UI 按钮成功）。
主路径覆盖了全部实际场景。

#### 但真正的理由是：回退会**破坏语义**

旧回退逻辑是「UI 不可用 → 直接调 `PlayerUpgradeBuilding`」。问题在于：

| UI 状态 | 含义 | 回退的后果 |
|---|---|---|
| 找不到按钮 | 菜单没开（理论上不会发生）| 无所谓 |
| **`interactable == false`** | **前置条件不满足**（资源不足 / 已满级 / ForceLv 不够）| ❌ **绕过这个检查，照样升级** |

`PlayerUpgradeBuilding` 内部**不查 `CanUpgrade`**，所以回退等于在
“本来该拦住”的情况下强行升级 —— 与闸②③的语义**直接矛盾**。

> 📌 **泛化教训 —— “回退路径”往往是技术债的伪装**：
>
> 1. **回退最常见的用途是掩盖“我不确定主路径是否总可用”**。
>    一旦验证了主路径覆盖全部场景（本例：23/23），回退就从“保险”变成了
>    **纯负担**：它有自己的语义、要单独测、还可能像本例一样**悄悄做错事**。
> 2. **回退最容易错的地方是“它绕过了哪些检查”**。
>    主路径经过的每一道关卡（这里是按钮的 `interactable`），
>    回退都默认跳过 —— 而那道关卡往往就是**功能正确性所在**。
>    写回退前先问：**主路径做了哪些校验是回退没做的？**
> 3. 本次的旧回退甚至**从未被执行过**，却在代码里“看起来很稳妥”，
>    还让我在 §4.9 的 bug 里多绕了一圈（返回值语义接反后正好落到它上面）。
>    **未被执行的分支不会暴露自己的错误** —— 这是它最危险的地方。

#### 删除清单

| 删除 | 说明 |
|---|---|
| `TryUpgradeBuildingDirectly(...)` | 整个方法 |
| `UiUpgradeResult` 枚举 | 两个调用处都不再需要返回值 → 函数改回 `void` |
| 调用处的 `if (result == NotHandled)` 分支 | 见上 |

**保留的**：`GetObjectPtr`（去重仍需要）、按钮查找与 `interactable` 检查
（现在 `interactable == false` 时**什么都不做** —— 与玩家在原生 UI 里点灰按钮的体验一致）。

---

### 4.11 ★★★ 设计收敛：**删掉所有前置检查**（用户提出）

**用户原话**：

> 「我在想我们是不是只要在 postfix 里找到按钮，如果**找得到且可点**的话触发 onclick
> 就行了。前置检查都不需要，这是游戏本身就做了的。
> 实际只需要区分出来障碍物和普通建筑/道路，用来区分是执行升级还是拆除。」

这个判断是对的，而且它一次性解释了 §4.6 / §4.9 两个 bug 的**共同根因**。

#### 依据：`interactable` 就是游戏的结论

看游戏自己的代码（`AreaBuildController.SetBuildTarget`，反编译调用图）：

```
SetBuildTarget(GameObject target)
  └─ [Calls] Selectable.set_interactable(bool)     ← 游戏在这里置灰按钮
```

`SetBuildTarget` **正是**建出菜单的那个函数（`AreaBuildingIconController.OnClick`
与 `AreaUnitController.OnClick` 都汇入它）。
所以我们在 Postfix 里读到的 `button.interactable`，是游戏**刚刚算完**的前置检查结论，
且覆盖全部条件：资源不足 / 已满级 / 正在施工 / ForceLv 不够 / 主城等级不够。

#### 我们原来的做法错在哪

| 旧代码 | 问题 |
|---|---|
| `data.CanUpgrade()` | 重算游戏已算过的门槛判断 |
| `IsBuildingBusy()` 手工判 `buildTimeLeft`/`upgradeTimeLeft`/`destroyTimeLeft` | 重算游戏已算过的“进行中”状态 |
| `IsRemovableObstacle()` 用 `GetObstacleRemoveCostResource` | 重算游戏已算过的可拆判断 |

**本质是同一个错误**：本地持有了一份游戏本该独占的判据，
于是必然出现「我算的」与「游戏算的」不一致。实测踩中两次：

- 拆除中的木匠（`destroyTimeLeft=1`）被判为可升级
- 迁移中的摊贩（`buildTimeLeft=1`）同样

`CanUpgrade()` 之所以拦不住，是因为它**只覆盖门槛类条件，不含进行中状态**
（实测：满级建筑 `CanUpgrade=false`，但 `GetUpgradeCostResource()` 仍有值 ——
说明它在算“够不够格”，不是在算“此刻能不能”）。

> 📌 **泛化教训 —— 不要在旁边重算别人的判据**：
> 当一个系统已经算过某件事并把结果**暴露出来**（这里是 `interactable`），
> 你在旁边重算的第二份实现**不会更准，只会不一致**。
> 正确做法是**消费它的结论**，只保留“系统没暴露、你必须自己决定”的那部分
> （这里是：该点「升级」还是「拆除」）。
>
> 判据：**如果游戏 UI 上能看出这个状态，那就不要自己算。**

#### 收敛后的实现

```csharp
// Prefix：只判一件事 —— 是不是障碍物（决定点哪个按钮）
bool isObstacle = IsObstacle(data);
__state = isObstacle;          // 传给 Postfix
_handledThisClick = true;      // “这次点击归我们管”（与上一维正交，不能挤进 __state）

// Postfix：找按钮 → 可点就点，否则静默
string label = isObstacle ? "拆除" : "升级";
var button = FindActiveChoiceButton(label);
if (button == null) return;            // 静默
if (!button.interactable) return;      // 静默（游戏的正常业务结论）
button.onClick.Invoke();
```

删掉的方法：`IsBuildingBusy`、`IsRemovableObstacle`、以及 Prefix 里的 `CanUpgrade()` 调用。
道路 Postfix 里手工判 `upgradeTimeLeft` 的那段也一并删除（同理由）。

#### 障碍物怎么区分 —— 用游戏自己的名单

```csharp
Il2Cpp.AreaBuildController.AreaObstacleName   // static List<string>
// 实测 = [杂草, 砂砾, 碎石, 残垣, 废墟, 池泽]
```

⚠️ 两个坑：

1. 它是 **static** —— 写 `Instance.AreaObstacleName` 会 `CS0176`。
2. 它在 C# 侧**只有声明、没有引用**（反编译可见），由 Lua 侧填充，
   所以**不能假定它一定非空**。退化判据：`data.DataBase() == null`
   （障碍物 `buildingID = -1`，在建筑表里查不到，`DataBase()` 返回 null）。

#### 空地为什么不用特判

用户设计的一个额外好处：**空地自动被覆盖**。
空地（`tileType=EmptySpace` + `building==null` + `areaRoadData==null`）
根本没有「升级」也没有「拆除」按钮 → Postfix 找不到 → 静默。
不需要为它写任何分支。（但道路 Prefix 里顺手拦一下，省掉一次全场景按钮扫描。）

---

### 4.12 ★★★ 结论反转：`interactable` **不**够，残留按钮必须自己判

§4.11 的收敛方向是对的（**删掉门槛类的重复计算**），但**删过头了**。

#### 用户报告

> 「拆除中建筑还能升级的问题还没有解决。」
>
> 「还是说，拆除之后短时间菜单里依然保留着升级按钮？」

**后半句正是根因 —— 而且不是"短时间"，是只要拆除没结束就一直保留。**

#### 实测复现（木匠 `id=32`，扬州 `(3,10)`）

| 步骤 | 菜单内容 | `destroyTimeLeft` | `CanUpgrade()` |
|---|---|---|---|
| 空闲 | 拆除 / 迁移 / 升级（3 个，全 `enabled=True`）| `0` | `True` |
| 点「拆除」| 关闭，按钮**只是隐藏**（未销毁）| **`1`** | **`True`** |
| 重新打开菜单 | 拆除 / 迁移 / **升级** / 取消拆除（**4 个全 `enabled=True` 全 `interactable=True`**）| **`1`** | **`True`** |

**「升级」按钮在拆除期间一直存在、`enabled=True`、`interactable=True`。**
Shift+单击点到它 → 就升级了。**这就是 bug 的机制。**

#### 三种「进行中」状态的完整实测

| 状态 | 字段 | 菜单内容 | 有可点的「升级」？ |
|---|---|---|---|
| 空闲 | 全 `0` | 拆除/迁移/升级 | 是（正常）|
| **拆除中** | `dL=1` | 拆除/迁移/**升级**/取消拆除 | 🔴 **有** |
| **升级中** | `uL=1` | **只有「取消升级」** | ✅ 没有 |
| **迁移中** | `bL=1` | **菜单根本不打开**（`grid active=False`，0 按钮）| ✅ 没有 |

→ **只有「拆除中」会中招。** 升级中 / 迁移中游戏自己处理对了 ——
用户担心的「迁移中也能升级」**实测不会**。

#### 残留按钮的判别字段是 `enabled`（用户提议验证）

用户提议「加上 `IsActive` 的检查」—— 方向对，但实测**字段不对**：

| 字段 | 残留按钮 | 当前按钮 | 能判别？ |
|---|---|---|---|
| `activeSelf` | True | True | ❌ |
| `activeInHierarchy` | True | True | ❌ |
| `interactable` | **True** | True | ❌ |
| **`enabled`** | **False** | **True** | ✅ |

证据是同一菜单里**并存两个同名按钮**：

```
[取消拆除] enabled=False interactable=True   <-- 残留（旧的）
[取消拆除] enabled=True  interactable=True   <-- 真正生效的
```

⚠️ **而且 `enabled` 的值会随时机变化** —— 同一栋建筑（园林 `id=29`）
在同一次拆除中，两次读到的残留「升级」按钮不一样：

| 时机 | 残留「升级」的 `enabled` |
|---|---|
| 点「拆除」后**第一次**打开菜单 | **`True`** ← 只靠 `enabled` 会漏！|
| 稍后再次打开同一菜单 | **`False`** ← `enabled` 过滤生效 |

⇒ **`enabled` 只能当作“尽力而为”的辅助过滤，不能作为唯一防线。**
理由：它读的是 **UI 对象状态**（会被复用 / 延迟重算影响），
真正可靠的 ③ 读的是 **数据**（不会被 UI 复用影响）。

#### 最终判据（三层）

```csharp
if (button == null) return;                         // ① 菜单里没有这个按钮
if (!button.enabled || !button.interactable) return; // ② 残留 / 门槛不满足
if (bd.buildTimeLeft > 0 || bd.upgradeTimeLeft > 0
    || bd.destroyTimeLeft > 0) return;              // ③ 进行中（游戏不重算，只能自己判）
```

**为什么 ③ 必须保留而不能只靠 ①②**：拆除中的「升级」残留按钮
`enabled=True`、`interactable=True` —— 两层都过得去。

#### 这与 §4.11 的关系（重要，别读成互相矛盾）

| 判据类型 | 谁负责 | 为什么 |
|---|---|---|
| **门槛类**（资源/等级/满级/ForceLv）| **交给 `interactable`** | 游戏在切换目标时**会**重算，不要重复实现 |
| **进行中**（建造/升级/拆除/迁移）| **自己判三个倒计时** | 游戏**不会**为重算残留按钮，`interactable` 是陈旧的 |

§4.11 删掉的应该是**门槛类**的重复计算（`CanUpgrade()`）；
`IsBuildingBusy`（只判三个倒计时）**当时不该跟着删** —— 已恢复。

> 📌 **泛化教训 —— 「消费系统的结论」有个前提**：
> §4.11 得出的原则是「系统算过就不要自己算」。
> 但那条原则**只在系统真的重算了的前提下成立**。
> **系统的结论可能是陈旧的** —— 尤其当它复用对象、只增量改状态时（本例：追加新按钮，
> 旧按钮既不销毁也不重算）。用之前先问：
> **这个结论是什么时候算的？之后状态变过吗？**

#### 排查提示：读数必须同调用内完成

`BuildChoiceGrid` 的内容会在游戏自己的 `Update` 里重建。
跨调用读到的按钮集合**会变** —— 本轮排查中多次出现
「上一次 4 个按钮、下一次 1 个」的自相矛盾读数，白绕了一圈。
→ **必须在同一次调用里完成「打开菜单 + 读取按钮」。**

## 5. 开关

```toml
[ShiftClickUpgrade]
enabled      = true    # 功能总开关
probe_only   = false   # ★ 必须为 false 才会真的升级；true = 只记日志的安全态
diagnostics  = false   # 排查型日志总开关（点击/地块/锤子/升级入口/按钮/确认）
sound_probe  = false   # 音效取证探针（游戏更新后重查音效名用）
```

| 开关 | 作用 | 关掉会怎样 |
|---|---|---|
| `enabled` | 功能总开关 | Shift 点击完全不干预（等同卸载） |
| `probe_only` | **安全态**：只记日志、不升级 | 回到“只观察”，用于二分排查 |
| `diagnostics` | 全部**排查型**日志 | 静默；只留 `★ 触发升级` 等关键行 |
| `sound_probe` | 音频汇聚点取证 | 无声效日志（功能不受影响） |

> **日志分级**（沿用 `friendlynoclip` 的策略）：
>
> | 类别 | API | 受 `diagnostics` 控制？ |
> |---|---|---|
> | 排查型（点击/判定/挂载/残留按钮/静默跳过） | `Plugin.LogInfo(() => …)` | ✅ **是** |
> | **整个会话首次成功**（`★ 功能生效：…`） | `Plugin.Log.Msg(…)` | ❌ 否（每次启动只一行）|
> | 音频取证（`sound_probe=true` 时才挂） | `Plugin.Log.Msg(…)` | 由 `sound_probe` 控制 |
> | 真正的错误 | `Plugin.LogError(…)` | ❌ 否，**永不门控** |
>
> ⚠️ 早期把「点击探针」设成**不受门控**，理由是“它是补丁是否触发的唯一证据”。
> 但那些钩子挂在**每次点击**的高频路径上，功能稳定后没必要常开 ——
> 现在统一归 `diagnostics`。**需要取证时打开它即可**，不会丢信息。
> 详见 §5.3（含 163 → 收敛后的实测对比）。

> ⚠️ 改这些值**不能直接改 cfg 文件** —— 见 §5.2b。
> 用 MCP：
> ```csharp
> var e = MelonLoader.MelonPreferences.GetEntry("ShiftClickUpgrade", "probe_only");
> e.GetType().GetProperty("Value").SetValue(e, false);
> MelonLoader.MelonPreferences.Save();
> ```

### 5.1 为什么保留 `probe_only` 这个开关

它不只是脚手架阶段的遗留。它同时是：

- **安全态**：任何可疑行为（比如升级了不该升级的、或者游戏变得卡顿）
  都能不改代码、只改 cfg（**立即生效，不需重启**）就回到「只观察不干预」；
- **二分工具**：若怀疑是本 mod 导致的问题，关掉它就能把本 mod 排除在外
  （`AGENTS.md` §5 排查顺序第 5 条：二分隔离比读代码快得多）。

### 5.2 使用步骤

1. 把 `probe_only` 改为 `false` —— ⚠️ **改法见 §5.2b，直接改文件不行**。
2. **冷启动**游戏（`AGENTS.md` §3.2.1）。
3. 进大地图 → **点锤子**（进入建造模式）→ **按住 Shift 点建筑**。
4. 预期：建筑直接升级，**不弹建筑 UI**。

**看日志确认走的是哪条分支**：

| 日志 | 含义 |
|---|---|
| `[Shift+单击升级] ★ 功能生效：…` | ✅ 成功接管（**不受门控**，每次启动只一行）|
| `[Shift+单击升级] 命中建筑图标：…` | 走到建筑入口了（需开 `diagnostics`）|
| `[Shift+单击升级] 判定为 障碍物 -> 拆除` | 正确识别为障碍物（需开 `diagnostics`）|
| `[Shift+单击升级] 「升级」是残留按钮（enabled=false）` | §4.12 的残留过滤生效（需开 `diagnostics`）|
| `目标正在拆除中（剩 N），不触发` | ③ 进行中判据挡下了（需开 `diagnostics`）|
| `「升级」按钮不可用（游戏判定门槛不满足）` | 门槛不够（资源/等级），静默跳过（需开 `diagnostics`）|
| `probe_only=true，…` | ⚠️ 忘了改 cfg |
| **完全没有输出** | 先开 `diagnostics` 看上面那几条是否出现 |

> 💡 现在**建筑与道路走同一条路径**，日志形态一致。
> 排查“点了没反应”时：先开 `diagnostics`，
> 看 `命中建筑图标`（建筑）或 `命中地块`（含道路）是否出现 ——
> 不出现说明钩子没被走到（点错地方 / 锤子没开 / 没按 Shift），出现则往下看分支日志。

### 5.2b ⚠️ ⚠️ **直接改 `MelonPreferences.cfg` 文件不生效**

这是本项目一个新发现的、**很容易误导排查**的坑（用户 2026-10-04 指出）。

**症状**：用编辑器把 `gamedir/UserData/MelonPreferences.cfg` 里的
`probe_only = true` 改成 `false`，保存。一小时后回来看，**文件里是 false，
但 mod 行为还是探针模式**，且**文件被改回 true**。

**根因**：MelonPreferences 是**内存优先**的 ——
`MelonPreferences_Entry.Value` 是权威值，
cfg 文件只在**启动时读一次**，之后**由运行中的进程回写**。
所以外部改文件会：

1. 对当前进程**无效**（内存里的值没变）；
2. 在进程下一次 `MelonPreferences.Save()`（切场景、退出、MCP 保存等）时
   **被内存值覆盖回去**。

**正确改法（三选一）**：

| 方式 | 做法 | 生效时机 |
|---|---|---|
| **MCP（推荐）** | 用 `execute_csharp` 直接设 `BoxedValue` 再 `MelonPreferences.Save()`，见下方代码 | **立即** |
| 游戏内 UI | MelonPreferencesManager 界面里改 | 立即 |
| 改文件 | 改完后**必须重启游戏**，且不能有进程在跑（否则被回写） | 重启后 |

**MCP 代码片段**（已实测有效）：

```csharp
var e = MelonLoader.MelonPreferences.GetEntry("ShiftClickUpgrade", "probe_only");
e.GetType().GetProperty("Value").SetValue(e, false);
MelonLoader.MelonPreferences.Save();
```

**验证当前生效值**（比读文件可靠）：

```csharp
var e = MelonLoader.MelonPreferences.GetEntry("ShiftClickUpgrade", "probe_only");
e.GetType().GetProperty("BoxedValue").GetValue(e)
```

> 📌 **泛化教训**：**“配置文件的文本”不等于“运行时生效值”。**
> 改配置后不要用 `cat` / `read` 验证，要用 MCP 读内存值验证 ——
> 这与 `AGENTS.md` 纪律 1（先确认产物与时机）是同一类问题：
> **要验证的是“实际跑的东西”，不是“磁盘上写的东西”。**

### 5.3 日志策略

**用户反馈**：「探针的问题，日志有点多。应该控制到 diagnostic 里。」

收敛前实测日志量（一次游玩，共 **163 行**）：

| 类别 | 条数 |
|---|---|
| `[探针·图标点击]` | 32 |
| `★ 通过 UI 按钮触发「升级」` | 28 |
| `[探针·Shift 点击]` | 26 |
| `[Shift+单击升级] 待操作目标：…` | 27（每种建筑一行）|
| `[探针·地块点击]` | 19 |
| `[探针·锤子切换]` | 4 |

绝大多数是**重复描述同一件事**。

#### 收敛后的分级

| 类别 | API | 受 `diagnostics`？ |
|---|---|---|
| **整个会话首次成功**（`★ 功能生效：…`）| `Log.Msg` | ❌ 否（每次启动只一行）|
| 音频取证（`sound_probe=true` 时才挂）| `Log.Msg` | 由 `sound_probe` 控制 |
| **其余全部**（点击 / 判定 / 挂载 / 残留按钮 / 静默跳过）| `Plugin.LogInfo(() => ...)` | ✅ **是** |
| 真正的错误（挂载失败、异常）| `Plugin.LogError(...)` | ❌ **否，永不门控** |

#### 三处具体收敛

1. **合并重叠日志**。原先一次 Shift 点击连打三条
   （`[探针·图标点击]` / `[探针·Shift 点击]` / `待操作目标`），内容高度重叠
   → 合并为两条，且只在 **Shift 点击**时打：一条「命中什么」，一条「判定为升级还是拆除」。

2. **普通点击不再打印**。非 Shift 的点击是最常见的操作，原先也各打一行
   （`shift=False`）→ 现在直接静默返回。

3. **删掉无价值的序号**。`第 N 次` 对排查没有增量价值，反而让日志更难一眼扫过
   → 连同 `_iconClickHits` / `_unitClickHits` 计数器一起删除。

> 📌 **判定标准**：不是「取证日志是否该门控」，而是
> **「这条日志是不是（a）高频、（b）仅在排查时需要？」** 两条都是 → 归 `diagnostics`。

> ⚠️ **一个踩过的坑（原写法在这里是错的）**：
> 早先写的是「`★ 触发升级` 首次用 `Msg`、之后走 `LogInfo`」——
> 看似合理，但 `LogInfo` 在 `diagnostics=true` 时**仍然会打**，
> 而排查时恰恰会把 `diagnostics` 打开，于是「减噪」完全失效
> （实测仍留下 28 行 `★ 触发升级`）。
>
> → 真正的分级标准是「**这条日志当下有没有价值**」，
> 而不是「有没有开 `diagnostics`」。首次用 `Msg` 是为了回答
> 「补丁到底有没有在工作」，一次就够；之后每一行都是同义重复。

> **策略演进值得记下。**
>
> 早期把点击探针设成**不受门控**，依据是 `AGENTS.md` 纪律 2
> 「取证仪表要永不受门控」—— 那个教训（日志被门控 → `grep -c` = 0 →
> 误判补丁零触发）是真实吃过的亏。
> 但那条纪律的前提是**那些日志在排查时是必需且稀少的**；
> 而这里的探针挂在**每次点击**的高频路径上（一局几百行），
> 功能稳定后常开只会淹没真正重要的行。

---

## 6. 代码状态

**构建**：`dotnet build ShiftClickUpgrade/ShiftClickUpgrade.csproj -c Debug`
**产物**：`ShiftClickUpgrade/bin/Debug/net6.0/ShiftClickUpgrade.dll`，**0 错误**。

| 文件 | 状态 |
|---|---|
| `Plugin.cs` | MelonLoader 骨架：开关 / `LogInfo` 门控 / `DescribePatcher` / 按参数个数选重载 / 构建指纹 |
| `ShiftClickUpgradePatch.cs` | **功能实现**（四道闸 + UI 触发升级 + 拦原点击）+ 七个钩点 |

**已部署**：`gamedir/Mods/ShiftClickUpgrade.dll`（md5 `8379c3a2142fd8f54d65d83507034a4f`）。

> ⚠️ `probe_only` 已通过 **MCP** 设为 `false`（改文件无效，见 §5.2b）。

### 6.1 ⚠️ 本轮唯一的运行方式瑕疵

启动日志开头是：

```
[12:23:02.077] [HotReload] Melon Assembly loaded: '.\Mods\ShiftClickUpgrade.dll'
[12:23:02.098] [HotReload] Loaded new mod ShiftClickUpgrade ... (Harmony: 4 patched; replayed 1 scene(s))
```

即 **Mod 是热重载进来的，不是随游戏冷启动加载的**（`AGENTS.md` §3.2.1）。

本次结论仍然成立，因为：

- 补丁挂在**常驻的 Controller 方法**上（`OnClick` / `ChangeBuildMode`），
  它们**不是「只触发一次」的开局钩子** —— 热重载后注册依然来得及；
- 事实也证明了：重载后 4 次点击全部命中。

但**下一步接真实升级调用时，必须冷启动验证**（`AGENTS.md` §3.2.1）。

### 6.2 刻意没做的事

- **没有 `[HarmonyPatch]` 特性**：MelonLoader 会对整个程序集跑 `PatchAll()`，
  扫到「光秃秃的 `[HarmonyPatch]` + 看起来像补丁的方法」却无目标可绑，
  会每次启动报 `Undefined target method`（`docs/harmony-il2cpp.md` §5.8）。
  本 mod 全部**手挂**。
- **没有自己写扣资源 / 改 `lv` 的赋值**：会漏掉计时、速率重算、UI 刷新等一串副作用（见 §1 约束 3）。
- **没有 `OnUpdate` 里轮询 `Input.GetKeyDown(Shift)`**：Shift 是**修饰键**，
  单独按下不代表有意图；挂在点击事件里读 `Input.GetKey` 语义更准，也不会在没点击时误触发。

---

## 7. 参考

| 文件 | 内容 |
|---|---|
| `output/decomp/full/Il2Cpp/AreaBuildingIconController.cs` | 图标点击回调 |
| `output/decomp/full/Il2Cpp/AreaUnitController.cs` | 地块点击回调（另一个 Click 接收者） |
| `output/decomp/full/Il2Cpp/AreaBuildController.cs` | `PlayerUpgradeBuilding` 所在类型 |
| `output/decomp/full/Il2Cpp/BuildingUIController.cs` | 建筑 UI 与升级按钮 |
| `output/decomp/full/Il2Cpp/AreaBuildingData.cs` | 建筑数据（`lv` / `CanUpgrade` / 成本） |
| [`friendlynoclip.md`](friendlynoclip.md) §2.2d | 「以为 X 是入口」的完整错判记录 |
| [`harmony-il2cpp.md`](harmony-il2cpp.md) §5.1 / §5.3b / §5.8 | 选重载 / Prefix-vs-Postfix / 空 `[HarmonyPatch]` |
