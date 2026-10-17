# ExpandBookStorageMod（第三方）—— 藏经阁扩容 mod 的改动面、污染半径与持久化判定

> 归属：[`AGENTS.md`](../AGENTS.md) §7 的模块表里有一行指向本文件。
> **读者**：想装/想排查这个第三方 mod 的人；以及想写「只改某一个容器」类补丁的人 ——
> 本文件是一份**反面教材**：无门控的容器补丁会污染到什么程度、以及「改了存档字段」为什么不等于「持久生效」。
> **不在这里**：Harmony/IL2CPP 的通用写法去 [`harmony-il2cpp.md`](harmony-il2cpp.md)、
> 原生 hook 去 [`native-hooks.md`](native-hooks.md)、反编译流程去 [`decompilation.md`](decompilation.md)。

---

## 0. 判据速查

* 目标 DLL：`gamedir/Mods/ExpandBookStorageMod.dll`（md5 `0bc330087b4c49a5a860747f4c756216`，v1.0.0，作者 ameki），MelonInfo 名 `Expand Book Storage`。
* 它**只**做两件事：①把**交易/容器界面右栏**容器的 `ItemListData.maxWeight` 无差别写成 `99999`；②手点按钮才把 `GlobalData.BookStorageRateLvAddRate` 逐项乘倍率。
* ⚠️ **它没有任何藏经阁门控。** 私人存储 / 官府仓库 / 门派仓库 / 商店 / NPC 交易全走同一个 `ShowTradeUI` 10 参数重载。它日志里的「藏经阁」三字是**硬编码文案**，不能当证据。
* ⚠️ **「写进存档字段」≠「持久生效」。** `maxWeight` 确是存档字段，但对这两个容器它是**被 `CountForceData`/`CountHeroData` 重算覆盖的缓存** —— 一旦触发重算就被推导值盖回。
* ⚠️ **倍率那个旋钮是惰性的**：只有手点按钮才生效；本机 10 份日志里**从未执行过**；且静态表不进存档，重启复原。
* **要只改藏经阁**：别改 `maxWeight`（它不是藏经阁的真实上限来源）；若一定要改，**不能靠 `TradeUIType` 门控** —— 藏经阁与私人存储**都是 `TradeUIType.Storage = 1`**。可用信号是 `ItemListType.BookType == 3`（只有藏经阁传）或 `rightItemList` 的**实例身份**（`ForceData.bookStorage` vs `HeroData.selfStorage`）。否则就会复现本文的污染。

---

## 1. 结论先行

| 问题 | 结论 |
|---|---|
| 私人存储上限为什么也被改？ | 因为补丁挂在 `TradeUIController.ShowTradeUI` 的 **10 参数重载**上，无差别写形参 `rightItemList.maxWeight`；而**三个短重载全部内部转发到它**，全游戏容器界面都汇聚于此。不是两个存储共享数据 —— 是补丁不区分场景。 |
| 改动会持久化到存档吗？ | **容量会**（`maxWeight` 是 `[Serializable]` 字段，生效期间存一次档就落盘），**而且读档后仍在** —— 实测载入的是存档里的值、**读档不重算**；只有 `forceDetailDirty` 触发的重算才会覆盖它。**倍率不会**（静态内存表，存档 0 命中）。 |
| 「藏经阁读书倍率」这个宣称成立吗？ | 在此安装里**从未生效**（按钮从未被按）。字段本身是 `GlobalData.BookStorageRateLvAddRate`，**不驱动**藏经阁容量阶梯；具体消费点未证实。 |
| 现在我的存档被污染了吗？ | 见 §4.5 —— 00:15 那一次**没有**落盘（`"maxWeight":99999` 命中 0）；但**后续任何一次存档都会把它写进去**，而且读档不会自愈。 |

---

## 2. 改动面：恰好 3 个写入点

穷尽性证明来自 `mod-analyst`（`output/expandbook/mod/findings.md`）：全 DLL 810 行 IL 里**只有 3 条写游戏数据的指令** ——
`ItemListData.set_maxWeight`×2 + `List<float>.set_Item`×1；三方枚举（ilspycmd / monodis / IL）一致给出 7 类型 / 15 方法 / 0 资源 / 0 P/Invoke / 0 lambda；
`monodis --customattr` 的 27 条特性里只有 2 条 `HarmonyPatch`；活进程 `hook_patch_info` 返回 `patchedMethodCount=2`，与静态吻合。
**无**原生改写、**无**文件 IO、**无** `OnApplicationQuit`/保存钩子。

| # | 补丁 | 写入 | 触发 |
|---|---|---|---|
| 1 | `[HarmonyPatch(TradeUIController,"ShowTradeUI", 10 参数)]` → `TradeUIPatch.Prefix(ItemListData rightItemList)` | `rightItemList.maxWeight = float.TryParse(MaxWeightInput)`（默认 `"99999"`） | **每次**打开任一容器/交易界面 |
| 2 | `[HarmonyPatch(TradeUIController,"FreshResourceLabel")]` → `FreshLabelPatch.Postfix(TradeUIController __instance)` | `__instance.rightList.targetItemList.maxWeight = 同值` | `ShowTradeUI` 内部与每次界面刷新都会调它 |
| 3 | `Main.ApplyModifications()`（只由 Alt+B 窗口里的按钮调用） | `GlobalData.BookStorageRateLvAddRate[i] = Mathf.Round(_originalRates[i]*result)/100f` | **仅手点按钮**；启动/切场景/存档都不触发 |

两点容易看错：

* **是 `float.TryParse` 不是 `Parse`** —— 非法输入**静默跳过**，不抛异常。
* **参数是按名绑定的**，不是靠位置猜。行为反证：`rightList.targetItemList=99999` 而 `leftList.targetItemList=348` ⇒ Prefix 拿到的确实是第 4 个参数。

---

## 3. 为什么改藏经阁会连带改私人存储

`TradeUIController.ShowTradeUI` 在托管侧有 **4 个重载**，但三个短重载**内部都调用 10 参数那个**（cpp2il `[Calls]` 属性）：

```
ShowTradeUI(TradeUIType, ItemListData, ItemListData, bool)            ─┐
ShowTradeUI(TradeUIType, ItemListType, ItemListData, ItemListData)    ─┼─→ ShowTradeUI(10 参数)  ← 补丁挂在这里
ShowTradeUI(TradeUIType, ItemListData, ItemListData, int, int)        ─┘
```

而**短重载**的 `[CalledBy]`（CallerCount=11，另加 10 参数自己的 6 个直接调用者）覆盖了：

`PlotController.OpenSelfStorage`（**私人存储**）/ `ShowGovernStorage`（官府仓库）/ `OpenForceStorage`（门派仓库）/
`OpenServantForceStorage` / `OpenOtherForceStorage` / `PlotOpenRandomShop` / `OpenInnShop` / `TradeWithNPC` /
`BuildingUIController.ShowBuildingShop` / `ShowBuildingShopForceStorage` / `ManageBookStore` /
`HeroDetailController.ExchangeTeamButtonClicked` / `DiscardButtonClicked` …

补丁**不检查** `TradeUIType` / `ItemListType`，也不检查容器身份 —— 谁被当 `rightItemList` 传进来，谁就被写成 99999。

两条链路已用**反汇编**核实（当前构建；`ui/findings.md`，注意别用 10-02 的旧 RVA）：

```
【私人存储】PlotController.OpenSelfStorage
    → ShowTradeUI(TradeUIType.Storage=1, left=HeroData.itemListData, right=HeroData.selfStorage)
    → 4 参数重载体 0x180aabcc0 → 10 参数重载体 0x180aab240   ← mod Prefix 在此
【藏经阁】  BookStoreController.ShowBookStoreUI
    → BuildingUIController.ManageBookStore
    → ShowTradeUI(TradeUIType.Storage=1, ItemListType.BookType=3,
                  left=HeroData.itemListData, right=ForceData.bookStorage)
    → ItemListType 重载体 0x180aabd20 → 10 参数重载体 0x180aab240   ← 同一处 Prefix
```

⇒ **两个界面的 `TradeUIType` 都是 `Storage = 1`**：「按 `TradeUIType` 门控」区分不了它们。
区分信号只有 `ItemListType.BookType`（仅藏经阁传 3）或 `rightItemList` 的实例身份 ——
这正是「只改藏经阁」的意图落空的原因。

活进程实测（游戏运行中，只读）：

| 容器 | 字段 | 存档里的值 | 被 mod 改后 |
|---|---|---|---|
| 私人存储 | `HeroData.selfStorage` | `maxWeight = 1689.99976` | **99999** |
| 藏经阁 | `ForceData.bookStorage`（玩家门派 forceID=25） | `maxWeight = 300.0` | **99999** |
| 背包（对照） | `HeroData.itemListData` | `348.0` | 348.0（未动） |
| 门派仓库（对照） | `ForceData.forceStorage` | `1920.0` | 1920.0（未动） |

⇒ 两个存储是**各自独立的对象**（分属 `HeroData` / `ForceData`），并不共享数据；
污染来自「补丁无差别写右栏」。门派仓库没被改，是因为它不落在右栏 —— 这恰好说明
**能被改的是「被传进右栏的那个容器」，而不是「藏经阁」**。

> 补充：3 处改动都会在 Harmony 之外叠加 —— `ShowTradeUI(10 参数)` 上同时还挂着
> `FoodAutomation`×2 与 `BookOwnMark`×1 个 postfix。将来观察到容量异常**不要默认归给本 mod**。

---

## 4. 持久化：会写进存档，但不是权威值

### 4.1 存档是真的明文 JSON（先记三个坑）

* 真存档在 **`gamedir/LongYinLiZhiZhuan_Data/Save/`**：`SaveSlotN/{Save,Hero,TempHero,Info}` + `PlayerprefData.dat` + `ExternalStorateData.dat`；LocalLow 侧是逐字节镜像。
* ⚠️ **`~/.wine/.../LocalLow/SuvGus Studio/Solar Wave/` 不是本作**（那份 `Player.log` 是 Unity 2022 + SolarCrown）。认存档先看 `gamedir/LongYinLiZhiZhuan_Data/app.info`（本作 = TppStudio / LongYinLiZhiZhuan）。
* ⚠️ **整份 JSON 是单行** —— `grep -c maxWeight` **恒返回 1**，必须 `grep -o … | wc -l`。这个坑曾让「存档里只有 1 处 maxWeight」成为假结论。

### 4.2 `maxWeight` 是存档字段

`ItemListData` 标了 `[Serializable]`，`maxWeight` **没有** `JsonIgnore`（只有 `itemTypeList` 带 `[NonSerialized][JsonIgnore]`）。
实证：`Save` 里 `"maxWeight"` 命中 **6048** 处；玩家 `selfStorage.maxWeight=1689.99976`、`Forces[25].bookStorage.maxWeight=300.0` 都在磁盘上。
→ **在 mod 生效期间存一次档，99999 就会落盘。**

### 4.3 但它是「重算缓存」，会被盖回

`verifier` 拿到了直接反汇编证据（游戏运行中 `disasm`，`maxWeight` 字段偏移实测 **`+0x20`**）：

| 写入者 | 目标 | 证据 |
|---|---|---|
| `GameController.CountForceData(ForceData)` | `force+0xA0`（forceStorage）、`force+0xB8`（**bookStorage**） | `movss [rdi+0x20],xmm0`，RVA `0x79a733` / `0x79a7c2`；值 = `表格*k1+k2` |
| `GameController.CountHeroData(HeroData)` | `hero+0x220`（itemListData）、`hero+0x228`（**selfStorage**） | `movss [rbx+0x20],xmm6`，RVA `0x79f124` / `0x79ec13` |

调用者是 `GameController.LateUpdate`，但**不是每帧都算** —— 实测反汇编是
`cmp byte [force+0x10C],0; je skip; call CountForceData`，而 `+0x10C` 是
`ForceData.forceDetailDirty` ⇒ **脏标记驱动**。（这条纠正过一次错误结论：
早先按 cpp2il 调用图写成「每帧重算」，是错的。）
⇒ **mod 写的 99999 是内存态；下一次重算就把它换成等级/属性推导值**（藏经阁 → 300 档，私人存储 → ~1690）。

旁证（30 个门派逐一对齐磁盘与内存，`output/expandbook/verify/live_force_map.txt`）：
`bookStorage` **29/30 磁盘==内存**，唯一不同的正是被 mod 改的 forceID=25（300→99999）——
说明**未触发重算时磁盘值确实被采用**；而 `forceStorage` 有 5/30 不同（如 710→730），独立证明了**重算真的会发生**。

> ⚠️ 别把 `WuMingPerformance` 的 `CountForceDataDeferPatch` 当「重算被挡住」的理由：
> 它只是把重算 `Submit` 到后台线程后**仍然调用原方法**。

**重算到底什么时候发生（2026-10-18 实测，纠正过一次错判）**：

| 动作 | 是否重算 | 证据 |
|---|---|---|
| 存书 / 取书 | **否** | `bookStorageDirty=False`、`forceDetailDirty=False`，无重算日志 |
| **变动影响藏经阁上限的建筑** | **是** | `[藏经阁] 复位漂移：240 → 999（CountForceData force=25）` —— 重算值 240 被自建 mod 的 Postfix 当场改回 |
| 读档 | **否** | 载入的是存档里的 99，而游戏推导值是 300 档（§4.5） |

### 4.4 四态结论

| 改动 | 重启游戏后 | 重新读档后 | 是否写进存档 | 真实生效性 |
|---|---|---|---|---|
| **容量** `maxWeight` | 不在（内存态，重启即失） | **仍在** —— 实测读档采用存档值、**不重算**（见 §4.5） | **会** —— 条件：生效期间存过一次档 | 一旦落盘就"半永久"：只有 `forceDetailDirty` 触发的重算才覆盖 |
| **倍率** `BookStorageRateLvAddRate` | 不在 | 未能证实 | **不会**（8 份存档产物 `grep -o 'BookStorageRate[A-Za-z]*'` 全 0；同文件 `"maxWeight"` 命中 6114 作对照） | **从未被触发**（见 §5） |

**本机现状**：`SaveSlot0/2/1` 里 `"maxWeight":99999` 命中 **0**（那几次存档都早于 00:15:37）。
但 **00:15 之后只要存一次档就会写进去，而且读档不会自愈** —— 见下。

### 4.6 藏经容量从哪来：**建筑词条求和**（2026-10-18 已证实）

`ForceData.bookStorage.maxWeight` = 该门派**当前有效建筑**提供的
`ForceSpeAddDataType.BookStoreStorage`（枚举第 **67** 项）词条之**和**。

证据（活进程只读探针 + 存档对照）：

| 建筑 | `buildingID` | `lv` | `BookStoreStorage` | 迁移状态 |
|---|---|---|---|---|
| **藏经阁** | 1 | 10 | **240** | `buildTimeLeft=2`（迁移中） |
| **星辰楼**（不是「星辰阁」） | 62 | 10 | **60** | `buildTimeLeft=2`（迁移中） |
| | | | **Σ = 300** | = 迁移**前**的推导值（旧存档里 force 25 就是 300）✓ |

迁移中的建筑**不计入**求和 ⇒ 用户先后看到两次重算：
`240 → 999`（此时只剩藏经阁有效）与 `0 → 999`（两个都在迁移）。

* 读建筑链：`WorldData.Areas` → `AreaData.areaTiles`（`List<AreaTileData>`）→ `.building`
  （`AreaBuildingData`：`buildingID` / `lv` / `buildTimeLeft`）→
  `GameDataController.buildingDataBase[id]`（`AreaBuildingDataBase`）→
  `.GetBuildingSpeAddData(lv)`（`ForceSpeAddData`，本质是 `Dictionary<int,float>`）→ `.Get(BookStoreStorage)`。
* 这也解释了为什么各门派上限呈 `{100,120,140,160,200,300}` 阶梯 —— 就是各自建筑的词条和。
* ⚠️ 同表里还有个 **`SkillBookExpRate`（第 7 项）** 才是「读书经验率」词条 ——
  第三方 mod 声称改「藏经阁读书倍率」却去动 `GlobalData.BookStorageRateLvAddRate`（§5），
  连词条体系都没进；它到底消费哪条公式**仍未证实**。

### 4.5 「读档会重算盖回」是**错的**（2026-10-18 实测纠正）

早先按「`maxWeight` 是缓存」推断出「读档后会被重算盖回」。**实测推翻了它**：

* `SaveSlot10/Save`（用户 01:04:40 存档）里 `Forces[25].bookStorage.maxWeight = 99.0` ——
  99 是用户当时用自建 mod 设的配置值（不是游戏推导值；游戏推导值是 300 档）。
* 下一次冷启动（01:19:41）载入时，该字段就是 **99**（mod 日志：`容量上限生效：99 → 154（扫描）`），
  **没有**被重算成 300。

⇒ **读档直接采用存档里的值**；`CountForceData` 的重算由 `forceDetailDirty` 驱动，
**不在读档路径上**。所以第三方 mod 写进去的 99999 一旦落盘，会**跨重启保留**，
直到某个动作把 `forceDetailDirty` 置位（例如藏经阁升级）才可能被覆盖。

> 同一份 `SaveSlot10` 也给出了**最硬的反证**：`heroID=0` 的
> `selfStorage.maxWeight = 1689.99976` —— **私人存储在整个过程中（内存 + 存档）都没被碰过**。

---

## 5. 倍率那个字段到底是什么

* `GlobalData.BookStorageRateLvAddRate` 是 **`List<float>` 静态字段**，活值 `[0.20, 0.40, 0.80, 1.60, 2.40, 3.20]`（6 项）。
* 它**不驱动**藏经阁容量阶梯：各门派 `bookStorage.maxWeight` 恰为 6 档 `{100,120,140,160,200,300}`，
  相对 100 的增量是 `100×{0,0.2,0.4,0.6,1.0,2.0}`，与表只有前两项重合 ⇒ 二者**不同源**。
* 它是不是读书效率也**未证实**：唯一的读书率出口 `HeroData.GetForceBookStorageExpRate(int)` 对已入藏经阁的技能实测**全返回 0**（skillID 0..399）。
  → mod 把标签写成「藏经阁读书倍率」是**缺乏证据的**；能确定的只是它**不是**容量来源。
* **最重要的一条**：本机现存 **10 份日志**（10-08 起）里 `修改前效率`/`修改后效率` **0 命中**，
  连 Alt+B 面板的开合日志（`藏经阁扩容MOD已切换设置界面`）也是 0 命中
  ⇒ **这个旋钮从未被按过，`ApplyModifications` 从未执行**。所以「游戏内读书倍率已变」目前是**零证据**。
  （限定：日志窗口只覆盖 10-08 起，更早的会话无从查证。）
* 即便按了：它只改静态内存、不进存档；而且 `_originalRates` 快照是 `static` 且只在 `null` 时拍 ——
  **热重载后快照清空而游戏 List 已被改**，再点按钮会以「已改值」为基准**重复乘**。`Count` 变长还会 `IndexOutOfRangeException`（无长度校验）。

---

## 6. 复现与验证手段（活进程只读通道）

DSH 的 MCP 客户端可能显示 `longyin` 断连，但 **MelonMCP 的 streamable-http 端点本身可用**（`http://127.0.0.1:27015/mcp`），
可直接用 curl 走 JSON-RPC。仓内留了两个助手（`output/` 不进 git）：

```bash
output/expandbook/mcp.sh tools                              # 列 40 个工具
output/expandbook/mcp.sh call execute_csharp '{"code":"…"}' # 注意 C# 转义
output/expandbook/run_cs.sh output/expandbook/probes/p1_storage_maxweight.cs   # 推荐：C# 写文件再送
```

有用的工具：`execute_csharp` / `evaluate_expression` / `list_patches` / `hook_patch_info` / `disasm` / `read_mem` / `watch_field` / `get_type_info`。

**纪律与坑**（都是这次踩到的）：

* **只读**。那是用户正在玩的进程 —— 不要写状态、不要改配置、不要卸补丁。
* ⚠️ **「只读」也不等于安全**：本次分析中，一个只读探针用**猜的字段名**调 `IL2CPP.GetIl2CppField(cls, "listType")`，
  该字段在 `ItemListController` 上并不存在 → 进程当场消失（日志最后一行只有
  `[ERROR] [Il2CppInterop] Field listType was not found on class ItemListController`）。
  **读字段前先枚举字段表**，`try/catch` 兜不住。详见 [`runtime-probing.md`](runtime-probing.md) §7.1。
* `typeof(X).GetMethod("M").MethodHandle.GetFunctionPointer()` 给的是 **Il2CppInterop 托管桩**，`disasm` 出来是 `call <thunk>; pop rsi`，**不是**游戏原生代码。要真实指针走 il2cpp API / `hook_patch_info` 的 `codeAddress`。
* 静态字段在 cpp2il 层**没有字段读取边** —— 别把「`[Calls]` 里没有」当成「没人读这个字段」。
* `output/dumper_out/dump.cs`（10-02）与 `gamedir/GameAssembly.dll`（10-08）**不是同一次构建**：按旧 RVA 做的原生反汇编会错位，结论作废。做原生定位前先重新 dump，或从活进程取指针。

---

## 7. 未决 / 需要实验才能定论

1. ✅ **`maxWeight` 是否真的强制容量（到顶拒收）** —— **已证实（2026-10-18，用户实测）**：
   把藏经阁上限改小后，装满即拒收。所以这个字段不只是显示值，是**真正的容量闸门**；
   这也解释了为什么第三方 mod 的做法确实能"扩容"，代价是它同时把私人存储的闸门也拆了。
2. **`BookStorageRateLvAddRate` 到底被哪条公式消费** —— 未能证实。
3. **读档是否重置这张静态表** —— 未能证实。
4. ✅ **`bookStorage.maxWeight` 什么时候会被重算** —— **已证实（2026-10-18）**：
   存/取书**不**触发（脏标记保持 `False`）；**迁移/变动影响藏经阁上限的建筑会触发**，
   实测 `240 → 999`、`0 → 999` 两次（`[藏经阁] 复位漂移：…（CountForceData force=25）`）。
   ⇒ 重算由 `forceDetailDirty` 驱动，**不在读档路径上**（§4.5）。
5. ✅ **推导公式** —— **已证实（§4.6）**：等于门派内**有效**建筑的
   `ForceSpeAddDataType.BookStoreStorage` 词条之和（藏经阁 240 + 星辰楼 60 = 300；
   迁移中的建筑不计入，故可降到 0）。

**给用户的一分钟判定实验**（可只读复核）：

1. 在游戏里存一次档 → 看该存档 `Forces[N].bookStorage.maxWeight` 是否等于你设的值
   （实测 `SaveSlot10` 为 `99.0`，即 mod 值确实落盘）⇒ 验证「容量会写档」。
2. **不碰任何 UI** 重新读档，看 `bookStorage.maxWeight` 是否仍是你设的值 ⇒ 验证 §4.5 的「读档不重算」。
3. 把「读书倍率」设 900 并点同步，观察藏经阁上限 / 读书经验 / 私存上限哪个变 ⇒ 定 §5 语义。

---

## 8. 替代方案：本仓自建 `BookStorageCap`（2026-10-18）

第三方 mod 被替换为本仓的 [`BookStorageCap/`](../BookStorageCap/) —— **同样改藏经阁上限，但不碰私人存储**。
工程：`BookStorageCap/{Plugin,Core,BookStorageCapPatch}.cs`；产物 `BookStorageCap.dll`（Release，逐字节可复现）。

**与第三方 mod 的三点本质区别**：

| | 第三方 `ExpandBookStorageMod` | 本仓 `BookStorageCap` |
|---|---|---|
| 挂在哪 | 只挂 `ShowTradeUI` 10 参数重载，**无差别**写形参 `rightItemList` | ① `GameController.CountForceData(ForceData)` Postfix（**参数就是 ForceData**，结构上不可能是私存）② `ShowTradeUI` 10 参数 Postfix，先过 `ItemListType.BookType`，再比对容器**指针身份** ③ 每 0.5 秒兜底扫描 |
| 重算怎么办 | 不知道 `maxWeight` 会被重算 → 靠 `FreshResourceLabel` 反复补写 | 明知 `CountForceData`（脏标记驱动）会覆盖它 → **在它算完之后覆盖回去** |
| 射程 | 所有走该重载的容器（私存/官府仓库/商店/NPC 交易） | 仅本门派 `bookStorage`（`only_player_force=false` 时才是全部门派） |

**门控为什么够**（`verify-mod/findings.md` 的反汇编，比 `docs` 里的早期结论更强）：
`ShowTradeUI`(10 参数) 真入口 `0xAAB240` 有**恰好 6 个**直接调用点，其中 5 个传/硬编码
`ItemListType.None(-1)`（**包括 `PlotController.OpenSelfStorage` 私人存储那条**）；
唯一能传 `BookType` 的是 4 参数+ItemListType 重载 `0xAABD20`，而它**只有一个调用者**
`BuildingUIController.ManageBookStore`，现场是 `mov rax,[rax+0xB8]`（= `ForceData.bookStorage`）
＋ `lea r8d,[rdx+2]`（= `BookType=3`）。
⇒ 「`BookType` ⇔ 右栏是 `bookStorage`」成立，私人存储**在第一道门就被挡住，即使 `only_player_force=false`**；
第二道门（指针身份）是冗余的纵深防御，不是缺一不可。

**配置**（`gamedir/UserData/BookStorageCap.cfg`）：
`enabled` / `max_weight`（`>0` 设值；`-1` 尝试表示「不限」，见下）/ `only_player_force` / `diagnostics`。
⚠️ **热生效只对「用配置管理器改」成立**（读的是 `Entry.Value`）；**直接手改 .cfg 文件不会热加载**。
⚠️ 生效时延是**扫描周期 0.5 秒**，不是「1 秒」。

**部署后活体验证（2026-10-18，游戏运行中只读探针）**：

```
[挂载] GameController.CountForceData(ForceData) → …；patcher=Il2CppDetourMethodPatcher [IsValid=True]
[挂载] TradeUIController.ShowTradeUI(10 参数)   → …；patcher=Il2CppDetourMethodPatcher [IsValid=True]
[藏经阁] 容量上限生效：300 → 999（扫描 force=25）     ← 读档后首次生效
[藏经阁] 容量上限生效：999 → 99（扫描 force=25）      ← 配置管理器改成 99，≤1s 生效
[藏经阁] 容量上限生效：99 → 154（扫描 force=25）      ← 再改成 154
```

6 次采样（跨 2.4 s）稳定：`bookStorage.maxWeight=154`（配置值）、
**`selfStorage.maxWeight=1689.9998`（游戏原值，未动）**、`forceStorage=1920` / `itemListData=348` 未动。
用户另行实测：**装满即拒收** ⇒ `maxWeight` 是真闸门（见 §7 第 1 条）。

> ⚠️ **同一个持久化语义仍然适用**：本 mod 写的也是存档字段，所以「生效期间存一次档」就会把该值写进存档。
> 与第三方 mod 不同的是：这个值是**用户自己设的**，且不会污染私人存储。
>
> ⚠️ **仍未实测**：`max_weight=-1` 对 **bookStorage** 是否真等于「不限」——
> `-1` 作为哨兵只在 `governStorage` 上观察到一次。
>
> ✅ **`CountForceData` Postfix 已实测触发（2026-10-18 第三轮）**：
> 用户「迁移了一次影响藏经阁上限的建筑」后，日志出现
> ```
> [01:32:39.982] [藏经阁] 复位漂移：240 → 999（CountForceData force=25）
> ```
> 即：建筑变动置脏 → `CountForceData` 重算 `bookStorage.maxWeight = 240`（新的等级推导值）
> → 我们的 Postfix **当场把它改回 999**。同时 `selfStorage` 仍为 `1689.9998`（未动）。
>
> 顺带纠正一条**中途的错误结论**：第二轮「开诊断 + 存书 + 取书」时没有这条日志，
> 一度被读成「Postfix 从未触发」；实测那两个脏标记都是 `False` ⇒ **存/取书不置脏**，
> 而**建筑变动才置脏**。所以：兜底扫描负责「读档后首次生效」，Postfix 负责
> 「游戏重算的那一帧立即纠正」，两者**都在真实路径上被观察到过**。
> （排查提示：`Latest.log` 是**边写边刷**的，可能读到半行；对账请用 `Logs/<会话>.log`。）

**存档级验证（第二轮会话新增，比内存探针更硬）**：用户存到 `SaveSlot10`（01:04:40）后，
该存档里 `Forces[25].bookStorage.maxWeight = 99.0`（= mod 当时的配置值，**落盘成功**），
而同一份存档里 `heroID=0` 的 `selfStorage.maxWeight = 1689.99976`（**私人存储未被污染**）。
下一轮冷启动载入的正是 99 ⇒ 顺带推翻了「读档会重算盖回」的旧推断（见 §4.5）。

> ⚠️ **本 mod 是「替换」而不是「加成」**：它把游戏按建筑词条算出来的和（§4.6）**整个换成配置值**。
> 因此当门派建筑**迁移中**、游戏把和算成 **0** 时，mod 仍会把它顶成 999（实测 `0 → 999`）。
> 这符合"我要固定上限"的意图，但也**掩盖了「藏经阁正在施工」这个状态** ——
> 若哪天想让施工期间保持游戏原语义，可给 `Core.OnForceCounted` 加一条「原值为 0 时不覆盖」的例外。

**版本与指纹**：v1.0.0 md5 `a7d6032cc0c5d3a44ed436dfd37fcffc`（首个部署版，已活体验证）；
v1.0.1 md5 `9f8e7620a5ee5c7c79cd863201e6a98e`（按 `verify-mod` 复核意见修：扫描改**时间**基准、
拦 `NaN/inf`、`Install`/`UnpatchSelf` 补异常兜底、纠正「每帧重算」错判）。
⚠️ 本机 `UserData/HotReload.toml` 是 `AutoReload = true` —— **运行中覆盖 `Mods/` 里的 DLL 会触发热重载**，
而本项目纪律要求改代码后**冷启动**（§3.2.1），所以 v1.0.1 只在游戏关闭后部署。

---

## 9. 参考

| 位置 | 内容 |
|---|---|
| `output/expandbook/recon/ExpandBookStorageMod.decompiled.cs` | 目标 DLL 的 ilspycmd 反编译（240 行） |
| `output/expandbook/mod/findings.md` | 改动面穷尽核验（IL 级、939 行） |
| `output/expandbook/ui/findings.md` | `ShowTradeUI` 重载 / 调用点 / 枚举表 |
| `output/expandbook/persist/findings.md` | 存档格式与持久化四态 |
| `output/expandbook/verify/findings.md` | 独立证伪（含 `movss` 反汇编证据、30 门派对齐） |
| `gamedir/MelonLoader/Logs/*.log` | 活进程日志（mod 触发记录） |

> 关联：[`forceoverflowdividend.md`](forceoverflowdividend.md)、[`wumingperformance-fix.md`](wumingperformance-fix.md) 同样是**第三方 mod** 的逆向结论。
