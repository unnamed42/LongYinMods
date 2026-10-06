# 游戏机制与系统（非 mod 专属的发现）

> ⚠️ **本文件的地址、字段偏移、调用链结论绑定到当前游戏构建**
> （`GameAssembly.dll` 33661952 字节 / `global-metadata.dat` 7959028 字节 / IL2CPP metadata **v27** / Unity 2020.3.48f1c1）。
> **游戏一更新即失效**，需按 `AGENTS.md` §4 的流程重新推导。
>
> 本文件收录**与 mod 具体功能无关、但以后做别的 mod 会复用到**的游戏内部机制。
> 各 mod 自己的实现细节写在 `docs/<项目>.md`（如 [`shiftclickupgrade.md`](shiftclickupgrade.md)）。

---

## 0. 本文收录什么（结论先行）

> 只收**与 mod 功能无关、但做别的 mod 会复用**的游戏机制；各 mod 自己的实现细节写在
> `docs/<项目>.md`。**结论都绑定当前游戏构建，游戏一更新即失效。**

| 系统 | 一句话结论 | 节 |
|---|---|---|
| 音效 | 两套 UI 事件体系（`TabButton` / `Button`）最终汇聚到**同一个函数** | §1 |
| 资源 | 资源是 **float 数组**，不是整数 —— 别强转 | §2 |
| 角色三维 | 详情面板与战斗 UI 用**同一套字段**；缓存挂在**角色**上而控件**共用**，交错切人会显示上一个角色的值（**已复现的显示 bug**）| §4 |
| ⚠️ 陷阱 | **动手前先扫一遍 §3「常见陷阱速查」**（本节原来埋在文件中部）| §3 |

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
| `ChangeResource(Int32 id, Single num, Boolean, Boolean)` | 单项增减资源（三个重载都实际存在，补丁要各自挂） |
| `HaveResource(Int32 id, Single num)` / `HaveResource(List<float>)` | 是否够 |
| `GetResourcePercent(Int32)` | 资源百分比 |
| `GetForceName(Boolean replacedForce)` | 门派名。⚠️ **签名已变**，见 §2.2a |
| `GetOwnHeros() : List<HeroData>` / `GetLeader() : HeroData` | 门派成员 / 掌门 |
| `forceName : String` | 门派名字段（**不走 `GetForceName`**，签名永远不会变） |

#### 2.2a ⚠️ `GetForceName` 的签名变了（旧 mod 的 `MissingMethodException` 根源）

**旧签名（早于 2026-10）**：`System.String ForceData.GetForceName()` —— 无参。
**当前签名（实测 2026-10-04）**：`System.String ForceData.GetForceName(Boolean replacedForce = true)`。

实测该类型上 `GetForceName` **只有一个重载**，即那个带 `bool` 的：

```
重载数量 = 1
  System.String GetForceName(Boolean)
```

后果：任何**在旧签名时期编译**、没重新构建的 mod，其 IL 里绑定的是
`GetForceName()`。运行时找不回该签名，于是**每次调用都抛**：

```
System.MissingMethodException: Method not found: 'System.String Il2Cpp.ForceData.GetForceName()'.
```

> 💡 **`replacedForce` 的语义**：该参数与 `ForceData.replacedForce` / `forceSetName`
> （替换门派名）相关；传 `true` 走「替换后」的名字，即与游戏 UI 显示一致。
> 实测 `forceID=0` 传 `true` 返回 `长乐帮`，字段 `forceName` 同样是 `长乐帮`。

**这个 bug 为什么隐蔽 —— 「在同一方法内包 try/catch」是无效的**：

典型的写法和 `ForceOverflowDividend` 一样：

```csharp
private static string SafeForceName(ForceData force)
{
    try   { return force.GetForceName() ?? "(未知门派)"; }   // ← 死代码
    catch { return "(未知门派)"; }                             // ← 永远不会执行
}
```

**这个 `catch` 从来没生效过。** 因为 `MissingMethodException` 是 **JIT 期**异常，
不是运行时异常：`call` 指令指向已不存在的令牌，CLR 在**编译该方法体时**就要解析它，
此时方法还没开始执行、**`try` 保护区域尚未建立**，异常直接从方法帧上抛出。

活进程实测（拿真实 `ForceData` 调旧版 `SafeForceName`）：

```
异常从方法**内部逃逸**出来: MissingMethodException

抛出异常的原始 StackTrace:
   at ForceOverflowDividend.ForceOverflowRuntime.SafeForceName(ForceData force)
```

异常逃逸到**调用方**，由调用方的 catch（此处是 Harmony `Postfix`）才兜住，
于是每结算一次就刷一条 `[ERROR]`。

> ⚠️ **通用教训**：`try/catch` 只能兜住**方法开始执行之后**的异常。
> 对「调用了不存在的成员」这类**绑定失败**（`MissingMethodException` /
> `MissingFieldException` / `TypeLoadException`），**在同一方法内**包 try/catch
> **无效** —— 要么从**调用方**包，要么改用**反射调用**（从根上不产生该异常）。

**正确写法（对未来签名漂移免疫）**：不要编译期绑定，改用反射探测：

```csharp
foreach (var m in typeof(ForceData).GetMethods())
{
    if (m.Name != "GetForceName") continue;
    var ps = m.GetParameters();
    if (ps.Length == 1 && ps[0].ParameterType == typeof(bool))
        return f => m.Invoke(f, new object[] { true }) as string;
    if (ps.Length == 0)
        return f => m.Invoke(f, null) as string;
}
return null;   // 退化到直接读 forceName 字段
```

反射**不会**因签名不符而抛异常 —— 找不到就返回 `null`。这正是想要的：
下次游戏再改签名，只退化成「少一个门派名」，而不是每次调用炸一次。
最省事的兜底是直接读 `ForceData.forceName` 字段（字段不会因方法签名变化而失效）。
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
| 游戏更新改了**方法签名** | 旧编译的 mod 抛 `MissingMethodException`；**方法内**的 `catch` 拦不住（JIT 期抛出），要改反射 | §2.2a |
| 缓存字段挂在**角色**上、控件却**共用** | 交错切人时「第二次回到 A」会跳过刷新，显示上一个角色的值 | §4.4 |
| 手工改写被守卫读取的缓存字段做实验 | 会亲手满足「跳过」条件，得出反向结论 | §4.6 |

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
## 4. 角色三维（生命 / 内力 / 体力）的显示与刷新缓存

> 实测日期 **2026-10-07**，构建指纹同上。本节包含**当前版本一个已复现的显示 bug**（§4.4）。

### 4.1 数值、控件与格式

三条 bar 在**两个界面里是同一套命名、同一套字段**：

| 数值 | 字段（`HeroData`） | 详情面板控件 | 战斗控件 |
|---|---|---|---|
| 生命 | `hp` / `maxhp` / `realMaxHp` | `Canvas/HeroDetailPanel/Hp/HpText` | `Canvas/BattleUIPanel/NowActiveHero/Hp/HpText` |
| 内力 | `mana` / `maxMana` / `realMaxMana` | `…/Mp/ManaText` | `…/Mp/ManaText` |
| 体力 | `power` / `maxPower` / `realMaxPower` | `…/Power/PowerText` | `…/Power/PowerText` |

> ⚠️ 「内力 ↔ `mana`、体力 ↔ `power`」是按控件顺序 + `BattleController.startMovePower` 命名**推断**的，
> **没有用一次真实消耗动作验证过**。用到时先确认：做一个消耗体力的动作，看哪条掉。

每条 bar 的结构固定为四个子节点（`X` = `Hp` / `Mp` / `Power`；**注意 Mp 的子节点前缀是 `Mana`**）：

| 子节点 | 作用 |
|---|---|
| `XBarBack` | 底图（`Image`） |
| `XBar` | **填充条**（`Image`，用 `fillAmount`） |
| `XReduceBar` | 「最近损失」的减量条 |
| `XText` | 文本（`Text` + `Outline` + `SimpleDetailText`） |

- 文本格式：`(int)(cur + 0.5f) + "/" + (int)(max + 0.5f)`
  实测 `184.5 → 185`、`192.5 → 193` ⇒ 是**四舍五入（half away from zero）**，
  **不是** `Mathf.RoundToInt`（银行家舍入会给 184 / 192）。
- 填充量：`Mathf.Min(cur / max, 1)`（实测 `60/200 → fillAmount = 0.3`）。
- 小数不进显示：数值都按**整数**渲染，所以 `±0.5` 的差异在界面上看不出来（排查时要注意）。

### 4.2 谁在刷新它

三条 bar 的写入口都是 `HeroData` 上的公开方法：`SetHpBar(GameObject)` / `SetMpBar` / `SetPowerBar`。
`GameObject` 参数就是上面那条 bar 的**根节点**。

调用图（`tools/find_callers` 实测；这些是**推断边**，见 [`find-callers.md`](find-callers.md)）：

```
详情面板点另一个角色 tab
  HeroDetailTabController.OnClick
    └─► HeroDetailController.FreshNowHeroDetail(HeroData, bool)
          └─► HeroData.Set{Hp,Mp,Power}Bar        （三项都调）

详情面板打开 / 战斗点格子 / 点角色头像
  ShowHeroDetail.OnClick / HeroIconController.OnClick / BattleController.BattleGridClicked
    └─► HeroDetailController.ShowHeroDetail(HeroData, bool)
          └─► FreshHeroDetail(bool)

战斗切换当前操作角色
  BattleController.RefreshActiveUnitUI()
    └─► HeroData.Set{Hp,Mp,Power}Bar
```

`SetHpBar` 共 **8** 个 caller，其中包含**每帧路径**：

| caller | 频率 |
|---|---|
| `HudController.Update` | **每帧** |
| `StudyAttackSkillController.Update` / `StudyDodgeSkillController.Update` / `StudyUniqueSkillController.Update` | **每帧** |
| `HeroIconController.RefreshHeroIcon` / `BattleUnit.RefreshFollowUI` | 事件 |
| `HeroDetailController.FreshNowHeroDetail` / `BattleController.RefreshActiveUnitUI` | 事件 |

`SetPowerBar` 只有 **2** 个 caller（`FreshNowHeroDetail`、`RefreshActiveUnitUI`），都是**事件驱动**。
> ⚠️ **`FreshNowHeroDetail` 在面板未激活时会提前返回**：实测面板关闭时调它，三个文本都不变。
> 所以任何基于它的补丁 / 自检都必须等面板打开，**不能在启动时跑**。

### 4.3 ⚠️ 渲染缓存：`shown*` 记账

`HeroData` 上有一组 **12 个字段**，记录「上次往哪个控件画了什么值」：

```
shownHpBarRoot    shownHp    shownMaxHp    shownRealMaxHp
shownMpBarRoot    shownMana  shownMaxMana  shownRealMaxMana
shownPowerBarRoot shownPower shownMaxPower shownRealMaxPower
```

`SetXBar(root)` 的**实测**守卫语义（三者行为完全一致）：

| 调用情形 | `SetHpBar` | `SetMpBar` | `SetPowerBar` |
|---|---|---|---|
| `shownXBarRoot == null` | 写 | 写 | 写 |
| **同 root、同数值** | **跳过** | **跳过** | **跳过** |
| 同 root、**数值变化** | 写 | 写 | 写 |

即 `root == shownXBarRoot && cur == shownX && max == shownMaxX` ⇒ 直接 return。

> 📌 缓存存在的意义看 caller 就明白：`SetHpBar` 在 `HudController.Update` 里**每帧**被调，
> 无条件写 = 每帧重排文本 + `LTLocalization.SetText`。

> ⚠️ **全场没有任何一处代码清空这些字段**：`grep -l 'shown*BarRoot'` 只命中 `HeroData.cs` 自己，
> 即**没有失效机制**。旁证：`BattleController` 上是同一套缓存模式（`uiLastPostureValues`、
> `uiLastExternalInjury`…），那边配了时间兜底 `ActiveUIStateFallbackInterval` + `uiLastStateRefreshTime`，
> 而 `ResetActiveUIValueCache()` 这个看名字就是「主动失效」的方法 **0 个调用者**。

### 4.4 ⚠️ 已知 bug：缓存挂在**角色**上，控件却是**共用**的

缓存字段在 `HeroData`（每角色一份），但详情面板那条 bar 与战斗的 `NowActiveHero` 那条 bar
是**一个控件轮流给所有角色用**。于是不变量「我说控件上是我的值 ⇒ 控件上就是我的值」不成立。

实测（三条 bar 表现**完全一致**；`A` = 白云天，`B` = 陆良宫，同一个控件）：

| 调用 | 控件最终显示 |
|---|---|
| `A.SetXBar(w)` | A 的值 ✅ |
| `B.SetXBar(w)` | B 的值 ✅ |
| `A.SetXBar(w)`（**第二次回到 A**） | **仍是 B 的值** ❌ |

原因：A 的缓存仍写着「控件上是 A 的值」，而 A 的数值没变 ⇒ 守卫跳过。

**为什么突出表现为体力**：守卫只在「数值变了」时才会发现异常。
生命/内力在游玩中频繁变化 ⇒ 会自愈；**体力不常变 ⇒ 缓存永远“自认正确” ⇒ 一直卡住**。

当前版本实测到的面板混合状态（`/Canvas/HeroDetailPanel`，节选）：

| 面板标题 | 生命 | 内力 | 体力 |
|---|---|---|---|
| 白云天 | 3975（= 陆良宫 `hp=3975.26`）❌ | 5311（= 陆良宫 `mana=5311.42`）❌ | 193（= 白云天 `power=192.5`）✅ |

> 📌 **推测（未证实）**：该 bug 疑似**本次更新引入** —— 猜测他们把「每角色一套 bar」改成了
> 「共用一套 bar + 记账缓存」。**本机没有旧版 DLL（Steam 随时更新）、无法对照，故仅作推测。**

### 4.5 怎么判断「官方是否已修」

| 判据 | 方法 | 可靠性 |
|---|---|---|
| **行为**（推荐） | A→B→A 探针：记下 A 的值 → 切到 B → **切回 A**，看三条 bar 是否跟随 A。跟随=已修，停在 B=未修 | 高（直接测现象） |
| 结构 1 | `HeroData` 上 `shown*BarRoot` / `shown*` 是否还在；字段消失或改名 ⇒ 缓存归属被改过 | 中 |
| 结构 2 | `SetPowerBar` 等是否还在、签名是否变（签名变则旧 mod 抛 `MissingMethodException`，见 §2.2a） | 中 |
| 结构 3 | `ResetActiveUIValueCache()` 是否开始有调用者 | 低（易误判） |

探针可以机械化：`execute_csharp` 里用 `HeroDetailTabController.OnClick()`（或战斗里的
`BattleController.RefreshActiveUnitUI()`）驱动**真实路径**，前后各读一次 bar 文本即可 ——
**前后读数必须在同一次调用内完成**（面板会自己重建，同类问题见 §2.4d）。

> ⚠️ **不建议做「启动时自动自检」**：探针必须真的切一次角色，会动 UI / 游戏状态，
> 侵入性比 bug 本身还大；而且官方若只改 native 守卫，从 metadata 读不出语义。

### 4.6 实验纪律（这次踩过的坑）

- ⚠️ **不要手工改写 `shown*` / `shown*BarRoot` 这类「被判定逻辑读取的缓存字段」。**
  本次调查中把 `shownPower` 手工设成与 `power` 相同的值，等于**亲手满足了「数值没变」的跳过条件**，
  于是得出「`SetPowerBar` 只比 root、不比数值」的**反向结论**，白费两轮。
  —— §4.3 那张表必须先 `shownXBarRoot = null` 再测，原因就在这里。
- 读 UI 必须**在同一次调用内**完成（面板/菜单会自己重建）。
- `Resources.FindObjectsOfTypeAll` **包含 inactive** 对象；面板关掉后 `nowShowHero` / `mainShowHero` 会变 `null`。
- 判断一个控件「到底写没写」：先写入哨兵字符串（并同步 `SimpleDetailText.text`），调用目标方法后回读；
  必要时再做一次全场景文本 diff，以区分「没写」和「写到别处去了」。

---

## 5. 记录约定

- **只记与 mod 无关的游戏机制**（世界观、数值规则、内部系统）。
- 需要具体地址/偏移的结论 → 仍写在这里，但**必须标注实测日期与构建指纹**（游戏一更新即失效）。
- 游戏设定/世界观类（非代码）→ 可放 MCP 知识库
  （但注意它**不随 git 同步**，见 [`runtime-probing.md`](runtime-probing.md) §7.1）。
