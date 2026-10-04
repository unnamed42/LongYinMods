# 游戏机制与系统（非 mod 专属的发现）

> ⚠️ **本文件的地址、字段偏移、调用链结论绑定到当前游戏构建**
> （`GameAssembly.dll` 33661952 字节 / `global-metadata.dat` 7959028 字节 / IL2CPP metadata **v27** / Unity 2020.3.48f1c1）。
> **游戏一更新即失效**，需按 `AGENTS.md` §4 的流程重新推导。
>
> 本文件收录**与 mod 具体功能无关、但以后做别的 mod 会复用到**的游戏内部机制。
> 各 mod 自己的实现细节写在 `docs/<项目>.md`（如 [`shiftclickupgrade.md`](shiftclickupgrade.md)）。

---

## 1. 音效系统

### 1.1 两套 UI 体系，一个汇聚点

游戏里**同时存在两套 UI 事件体系**，点击音各有来源，但最终都汇聚到**同一个函数**：

```
UIClickSound  (实现 UnityEngine.EventSystems.IPointerClickHandler)
      ┐        ← **uGUI / EventSystem** 体系
      │
      ├──> NGUITools.PlaySound(AudioClip, float volume, float pitch)  ★ 汇聚点
      │
UIPlaySound   (自带 Trigger 枚举: OnClick / OnMouseOver / OnMouseOut / OnPress / OnRelease)
      ┘        ← **NGUI** 体系；其 Play() 尾部 jmp 到上面那个函数
```

| 组件 | 体系 | 触发方式 |
|---|---|---|
| `UIClickSound` | **uGUI**（`IPointerClickHandler.OnPointerClick(PointerEventData)`）| EventSystem 射线 |
| `UIPlaySound` | **NGUI**（`Trigger` 枚举 + `OnHover`/`OnPress` 回调）| 自身回调，或**手动调 `Play()`** |

> 💡 两套并存说明这是**从 NGUI 迁移到 uGUI 中途**（或长期混用）的项目。
> 看到 `UIPlaySound` 不要以为“全项目都是 NGUI” —— 按钮本身可能是 uGUI `Button`。

**关键点**：`UIPlaySound.Play()` 是 `jmp NGUITools.PlaySound`（尾调用），
所以**挂 `NGUITools.PlaySound(AudioClip,float,float)` 的 Prefix 就能抓到全部 UI 音效**
（两套体系都收得到）。

⚠️ `PlaySound` 有 **3 个同名重载**（1/2/3 参），必须按参数个数选
（见 [`harmony-il2cpp.md`](harmony-il2cpp.md) §5.1）。

### 1.2 ⚠️ `UIClickSound` 由**射线**触发，不是由 `OnClick()` 触发

这是最容易踩的一点。如果你用 Harmony **跳过**了某个组件的 `OnClick()`
（例如为了“不弹 UI 直接做事”），那么：

- 挂在该按钮 GameObject 上的 `UIClickSound` **不会被踩到**
- → **点击音消失**

因为音效是「UI 被真正点击」的副产品，而你把那条路断了。

**要恢复音效，有两种正路**：

1. **别拦原点击** —— 让 UI 正常走（推荐，见 §1.3）
2. 自己造一个 `UIPlaySound` 宿主并调 `Play()`（或直接调 `NGUITools.PlaySound`）

### 1.3 ★ 推荐做法：触发 UI 按钮本身，而不是自己造音效

本项目的教训是：**“手工复现副作用”是一笔重债**（详见
[`shiftclickupgrade.md`](shiftclickupgrade.md) §4.8）。

```csharp
// 找到游戏自己的按钮，直接调它的 onClick —— 等价于“玩家点了这个按钮”
button.onClick.Invoke();
```

这样**音效、资源扣除、UI 刷新、计时、前置检查全部自动正确**，
不需要知道任何内部实现，也不需要手工补音效。

### 1.4 播放方式的两个坑

| 写法 | 结果 |
|---|---|
| `AudioSource.PlayClipAtPoint(clip, pos)` | ❌ **另开一路**：新建 **3D 衰减**声源，听感与原生不同（原生是 2D）|
| 建 `UIPlaySound` 宿主 + `Play()` | ✅ 与游戏同路径 |
| 直接 `NGUITools.PlaySound(clip, 1f, 1f)` | ✅ 也行（就是同一个函数）|

- `NGUITools.PlaySound` 会**复用 `Main Camera` 上的 `AudioSource`**（`spatialBlend=0`）。
- `NGUITools.soundVolume` 是全局 UI 音量（实测 = 1）。
- ⚠️ 自建 `UIPlaySound` 宿主时：**不能加 `DontDestroyOnLoad`** ——
  实测加了之后对象**立刻就找不到了**。挂到 `Canvas` 下即可。
- ⚠️ **每个 clip 要一个宿主** —— `UIPlaySound.clip` 是实例字段，共用一个会互相覆盖。

### 1.5 音效资源名速查（实测 2026-10-04）

全场景按使用次数排序（`UIClickSound` 163 个 + `UIPlaySound` 93 个）：

| clip | 用量 | 典型用途 |
|---|---|---|
| **`TabButton`** | **166** | ★ **通用按钮点击音**（绝对主力）|
| `Woosh` | 39+ | 面板滑入/过渡 |
| `PaperQuick` | 14 | 轻量翻页 |
| `Paper` | 10 | 翻书 |
| `BigButton` | 5 | 大按钮 |
| `WoodWork` | 少量 | **建筑升级/施工**（2.48s）|
| `CraftButton` / `WoodButton` / `ConfirmButton` / `Lock` / `OpenBook` / `Bag` / `Success` / `Deal` | 各 1–3 | 专用场景 |

> **`TabButton` 出现 166 次** —— 反过来说：**它不可能是任何功能专属音**。
> 本项目曾误以为“升级按钮挂的音 = 升级音”，就是忽略了这一点
> （见 [`shiftclickupgrade.md`](shiftclickupgrade.md) §4.5）。

---

## 2. 资源（Resource）系统

### 2.1 资源是 **float 数组**，不是整数

势力资源（`ForceData.resourceStore`）是一维 `float` 数组，**长度 6**，
与存档文件里的字段名一致（用户已核实存档中就叫 `resourceStore` / `resourceStoreMax`）。

**资源量可以是非整数**（生产速率 / 折扣会带小数）。
校验或比较资源时**不要强转 int**，否则会丢精度。

实测某势力（`forceDataBase[0..29]` 均如此）：

```
resourceStore    = [0, 0, 0, 0, 0, 0]
resourceStoreMax = [1000, ...]          <- 上限能正常读到
```

> ⚠️ **这份数据是“可疑”的，不要据此下结论。**
> `resourceStoreMax` 能正常读到值（1000），说明**字段访问方式没问题**；
> 但 `resourceStore` 全 0，而玩家当时实际持有大量资源（银钱 54600 等）。
> `HaveResource(0, 100)` 也返回 `false` —— 与“池子为空”自洽。
>
> **两个可能**：
> 1. 我读的 `GameDataController.Instance.forceDataBase[id]` **不是运行时那份实例**
>    （尽管 `forceLv` / `mainAreaID` 能读出真实值，但 `ownAreas` / `ownHeros`
>    同样是空的 —— 像是“部分初始化”的副本）；
> 2. `resourceStore` 确实不是“势力持有的资源”，而是别的语义。
>
> **未解**，需要重新定位正确的实例后再下结论。
> （曾经的定位方式不可复用，见下。）

> 📌 **踩坑记录 —— 内存地址不可当锚点**：
> 本项目一度用 Cheat Engine 找到“银钱地址 `0x12b254570`”并展开分析
> （确认它指向一个 `System.Single[8]`，`[0] = 54600`）。
> 但用户随后指出：**该地址每次读档都会变**，不是稳定地址。
> → 存档/运行时对象会被整体重建，**任何裸内存地址都只在当次会话有效**。
>
> **正确做法**：用**字段路径**（如 `ForceData.resourceStore`）而不是地址来定位数据；
> 需要地址时每次现场重新求值（`Il2CPP.Il2CppObjectBaseToPtr(...)`）。

### 2.2 `ForceData` 上的资源相关 API

| 成员 | 说明 |
|---|---|
| `resourceStore : List<float>` | 资源数组（长度 6）。用户已核实**存档里就叫这个名字** |
| `resourceStoreMax : List<float>` | 资源上限 |
| `CostResource(List<float>, bool showInfo)` | 扣资源 |
| `ChangeResource(List<float>, bool, bool)` | 增减资源 |
| `HaveResource(Int32 id, Single num)` / `HaveResource(List<float>)` | 是否够 |
| `GetResourcePercent(Int32)` | 资源百分比 |

> ⚠️ **实测 `GameDataController.Instance.forceDataBase[id]` 上的 `resourceStore` 全为 0，
> 且 `HaveResource(0, 100)` 返回 `false`** —— 与玩家实际拥有大量资源的事实不符。
> 该实例的 `ownAreas` / `ownHeros` 同样是空的（但 `forceLv` / `mainAreaID` 有真值），
> 像是“部分初始化的副本”。**正确的实例尚未定位**，详见 §2.1。

### 2.3 升级/建造消耗的表示

`AreaBuildingData.GetUpgradeCostResource(float rate)` 返回 `List<float>`，
**按资源槽位索引**：

```
磨坊 lv=4 的 GetUpgradeCostResource() = [1250, 1250, 0, 0, 0, 0]
                                          ↑      ↑
                                       银钱    粮食
```

**所以「消耗 1250 银钱和粮食」= 合计 2500，不是翻倍。**
（读消耗文本 `GetUpgradeCostText()` 更容易看清对应关系。）

### 2.4 建筑与道路是**两套数据**，但**共用一个菜单**

```
AreaUnitController.areaTileData : AreaTileData
  ├─ building      : AreaBuildingData   （建筑格）—— 建筑图标另挂 AreaBuildingIconController
  └─ areaRoadData  : AreaRoadData       （道路格：roadLv / upgradeTimeLeft）
```

| 目标 | 点击接收者 |
|---|---|
| 建筑 | `AreaBuildingIconController`（挂在 `AreaBuildingUnit(Clone)` 上）|
| 道路 / 空地 | `AreaUnitController`（挂在 `AreaGridRoot/<row>_<col>` 上）|

⚠️ **道路没有 `AreaBuildingIconController`** —— 实测（扬州 areaID=9）24 条道路，
带图标的 **0** 条。所以「点道路」这条链**必须**挂 `AreaUnitController`。

⚠️ **障碍物两条链都会触发**（它同时挂在两个控制器上，实测确认）——
这是「一次点击触发多次」的**结构性**原因，不是偶发。
去重时要用「同一目标指针 + 极短时间窗」，不能只靠时间。

实测分布（同一张图）：

```
AreaBuildingIconController  114 个：普通建筑(buildingID>=0) + 障碍物(buildingID=-1)
AreaUnitController          226 个：其中 114 个带 building 引用（与上面重叠 = 障碍物所在格）
                                    24 条道路 / 77 城墙 / 4 城门 / 7 纯空地 —— 均无图标
```

| `AreaTileData` 字段 | 含义 |
|---|---|
| `building != null` | 建筑**或障碍物**（看 `buildingID`：`-1` = 障碍物）|
| `areaRoadData != null` | 道路 |
| 两者皆 `null` | 空地（`tileType=EmptySpace`）|

`tileType` 枚举：`EmptySpace` / `Road` / `MainBuilding` / `Null` / `CityGate` / `CityWall`。
⚠️ 障碍物所在格的 `tileType` 是 **`EmptySpace`**（不是独立类型）——
**不能靠 `tileType` 识别障碍物**。

**但锤子模式下的菜单是同一个**：

```
Canvas/AreaUIPanel/BuildChoiceGrid
  ├─ BuildChoiceButton(Clone)  文字=拆除
  ├─ BuildChoiceButton(Clone)  文字=迁移
  └─ BuildChoiceButton(Clone)  文字=升级   ← 建筑与道路共用
```

> 💡 另有 `Canvas/BuildingUIPanel/BuildingUI/ExtraButtonGrid/UpgradeButton`
> —— 那是**非锤子模式**的另一套建筑 UI，锤子流程不走它。**别搞混。**

### 2.4b ★ 障碍物（残垣/废墟）与普通建筑的区别

**游戏自带权威名单**（别自己猜）：

```csharp
Il2Cpp.AreaBuildController.AreaObstacleName   // static List<string>
// 实测 = [杂草, 砂砾, 碎石, 残垣, 废墟, 池泽]
```

| | 障碍物 | 普通建筑 |
|---|---|---|
| `buildingID` | **`-1`** | `>= 0` |
| `DataBase()` | **`null`** | 有效对象 |
| `Name(false)` | **抛 NRE** | 正常 |
| `GetUpgradeCostResource(1f)` | **抛 NRE** | 返回 `List<float>` |
| `GetObstacleRemoveCostResource(1f)` | 返回 `List<float>` | 也返回（**不能用来区分**）|
| 菜单按钮 | **只有「拆除」** | 有「升级」等 |

⚠️ **不要用异常当判据**。虽然 `GetUpgradeCostResource` 对障碍物必抛 NRE，
但那是「用异常做控制流」，慢且脆弱。**直接问 `DataBase() == null`** 即可。

⚠️ **`GameController.ObstacleCanDestroy()` / `BuildingCanUpgrade()` 名字像类型谓词，
实际是资源谓词** —— 实测一次全场景扫描中，73 个**普通建筑**的
`ObstacleCanDestroy` **全部返回 `true`**。它们回答的是「钱够不够」，
**不能**用来区分障碍物。（`AreaBuildingIconController.Update` 用它们决定
提示精灵显示与否 —— 那是「此刻能不能」的指示，不是身份。）

### 2.4c ★★ 菜单按钮**会残留**：`interactable` 只覆盖门槛，不覆盖「进行中」

> ⚠️ 这一节的内容经过一次**结论反转**，值得完整读。
> 一开始我们以为「`interactable` 就是游戏的权威前置检查，照着用就行」——
> **对门槛类条件成立，对「进行中」状态不成立**。

`AreaBuildController.SetBuildTarget(GameObject)` 建菜单时确实会调
`Selectable.set_interactable(...)`（反编译调用图可见），
所以 `interactable` 反映了**门槛类**条件：资源不足 / 已满级 / ForceLv 不够 / 官府等级不够。

**但按钮对象不会被销毁 —— 游戏只是复用/改 `enabled`。**
于是切换状态后，菜单里会残留上一个状态的按钮，而游戏**不会为它们重算 `interactable`**。

#### 实测：三种「进行中」状态下的菜单（佛山镇 areaID=63 + 扬州 areaID=9）

| 状态 | 字段 | 菜单内容 | 有可点的「升级」？ |
|---|---|---|---|
| 空闲 | 全 `0` | 拆除 / 迁移 / 升级（3 个，全 `enabled=True` `interactable=True`）| 是（正常）|
| **拆除中** | `destroyTimeLeft=1` | 拆除 / 迁移 / **升级** / 取消拆除（4 个，**全 `enabled=True` `interactable=True`**）| 🔴 **有** |
| **升级中** | `upgradeTimeLeft=1` | **只有「取消升级」** | ✅ 没有 |
| **迁移中** | `buildTimeLeft=1` | **菜单根本不打开**（`grid.activeInHierarchy=False`，0 按钮）| ✅ 没有 |

**结论：只有「拆除中」会残留可点的「升级」按钮。**
升级中 / 迁移中游戏自己处理对了（迁移中甚至不给菜单）。

拆除中的完整复现（木匠 `id=32`，点「拆除」后 `dLeft: 0→1`）：

```
[拆除]     enabled=True interactable=True
[迁移]     enabled=True interactable=True
[升级]     enabled=True interactable=True   <-- 残留，可点，而 CanUpgrade() 仍为 True
[取消拆除] enabled=True interactable=True
```

#### 判别残留按钮：用 **`Button.enabled`**，不是 `IsActive`

实测同一菜单里会**并存两个同名按钮**：

```
[取消拆除] enabled=False interactable=True   <-- 残留（旧的，被禁用）
[取消拆除] enabled=True  interactable=True   <-- 真正生效的
```

| 字段 | 残留按钮 | 当前按钮 | 能判别？ |
|---|---|---|---|
| `activeSelf` | True | True | ❌ |
| `activeInHierarchy` | True | True | ❌ |
| `interactable` | **True** | True | ❌ |
| **`enabled`** | **False** | **True** | ✅ |

⚠️ **而且 `enabled` 的值会随时机变化** —— 同一栋建筑（园林 `id=29`）
在同一次拆除中，两次读到的残留「升级」按钮不一样：

| 时机 | 残留「升级」的 `enabled` |
|---|---|
| 点「拆除」后**第一次**打开菜单 | **`True`** ← 只靠 `enabled` 会漏！|
| 稍后再次打开同一菜单 | **`False`** ← `enabled` 过滤生效 |

⇒ **`enabled` 只能当作“尽力而为”的辅助过滤，不能作为唯一防线。**
它读的是 **UI 对象状态**（会被复用 / 延迟重算影响）；
真正可靠的 ③ 读的是 **数据**（不会被 UI 复用影响）。
#### 所以判断「能不能操作」需要三层

```csharp
if (button == null) return;                        // ① 菜单里没有
if (!button.enabled || !button.interactable) return; // ② 残留 / 门槛不满足
if (bd.buildTimeLeft > 0 || bd.upgradeTimeLeft > 0
    || bd.destroyTimeLeft > 0) return;             // ③ 进行中（游戏不重算，只能自己判）
```

> 📌 **泛化教训**：
> 「消费系统的结论，不要自己重算」这个原则**只在系统真的重算了的前提下成立**。
> 系统的结论可能是**陈旧的**——尤其当它复用对象、只增量改状态时。
> 用之前先问一句：**这个结论是什么时候算的？之后状态变过吗？**

配套：`CanUpgrade()` 在这三种状态下**都可能返回 `True`**（它只算门槛类条件），
所以它既不能代替 ② 也不能代替 ③。

### 2.4d ⚠️ 排查提示：菜单会自己重建，读数必须同调用内完成

`BuildChoiceGrid` 的内容会在游戏自己的 `Update` 里重建。
**跨调用**（两次 MCP `execute_csharp`）读到的按钮集合**会变** ——
排查时出现过「上一次读到 4 个按钮、下一次读到 1 个」的自相矛盾结果。

→ **必须在同一次调用里完成「打开菜单 + 读取按钮」**，否则读数不可信。

### 2.5 升级状态字段：`upgradeTimeLeft`

`AreaBuildingData` 与 `AreaRoadData` **都有同名的 `upgradeTimeLeft`**：

| 值 | 含义 |
|---|---|
| `0` | 空闲，可升级 |
| `>0` | **正在升级中** |

⚠️ 但 **`CanUpgrade()` 在“已在升级中”时仍返回 `true`** ——
**不能只靠它**判断“能不能升”。

`AreaBuildingData` 上的三个「进行中」计数器**互不相干**，要**全部**检查：

| 字段 | 含义 |
|---|---|
| `buildTimeLeft` | 建造 / **迁移**中 |
| `upgradeTimeLeft` | 升级中 |
| `destroyTimeLeft` | 拆除中 |

---

## 3. 常见陷阱速查

| 陷阱 | 说明 | 详见 |
|---|---|---|
| `UIClickSound` 靠**射线**触发 | 跳过原 `OnClick` 会丢点击音 | §1.2 |
| `Resources.Load(名字)` 拿不到 AudioClip | 得用 `Resources.LoadAll("")` | §3.1 |
| AudioClip 是**懒加载**的 | 不能假定“它一定在内存里” | §3.1 |
| `Resources.FindObjectsOfTypeAll` 返回 `Il2CppArrayBase<T>` | **不支持 LINQ** | §3.1 |
| `CanUpgrade()` 不反映“正在升级” | 要看 `upgradeTimeLeft` | §2.5 |
| `forceDataBase[id]` 的 `resourceStore` 全 0 | **实例可疑**，不是“字段名是假的” | §2.1 |
| 裸内存地址（CE 找到的）| **每次读档都会变**，不可当锚点 | §2.1 |
| 资源是 float | 别强转 int | §2.1 |

### 3.1 资源（Asset）加载

| 写法 | 实测结果 |
|---|---|
| `Resources.Load<AudioClip>("WoodWork")` | ❌ **恒为 null**（试过多种子路径）|
| `Resources.LoadAll<AudioClip>("")` | ✅ **359 个**，且能**主动把未加载的也取出来** |
| `Resources.FindObjectsOfTypeAll<AudioClip>()` | ⚠️ 只返回**当时已加载**的（同一场景不同时刻实测 58 / 61 / 359 都有过）|

**所以取 clip 的正确顺序是**：先 `FindObjectsOfTypeAll`（快），
未命中再 `Resources.LoadAll("")`（能主动加载，但**很慢** —— 实测同步跑要 **4.3 秒**）。

> ⚠️ **`LoadAll` 不能放在交互热路径上**。本项目曾因此让“首次点击卡死 4 秒”。
> 要么只在启动时跑一次，要么缓存结果。

`LoadAll` 返回的实例与游戏运行使用的是**同一份**（已用 `GetInstanceID` 比对确认）。

> 📌 **`Resources.Load` 失败 ≠ 资源不存在** ——
> 它只说明“不在 `Resources` 根目录下、无法按名字直接寻址”。
> 这些 clip 实际是被**预制体/场景**引用的。

---

## 4. 记录约定

- **只记与 mod 无关的游戏机制**（世界观、数值规则、内部系统）。
- 需要具体地址/偏移的结论 → 仍写在这里，但**必须标注实测日期与构建指纹**（游戏一更新即失效）。
- 游戏设定/世界观类（非代码）→ 可放 MCP 知识库
  （但注意它**不随 git 同步**，见 [`runtime-probing.md`](runtime-probing.md) §7.1）。
