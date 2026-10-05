# `find_callers` —— 找谁调用了某方法

```bash
tools/find_callers/find_callers.sh <方法名> [选项]
```

```
5 callers of GetObstacleRemoveCostResource  [58 assemblies, 67500 methods]

  GameController.ObstacleCanDestroy              [total callers of this method: 3]
  AreaBuildController.SetBuildTarget             [total callers of this method: 2]
  GameController.ObstacleDestroyStart            [total callers of this method: 2]
  AreaBuildController.BuildChoiceButtonClicked   [total callers of this method: 1]
  GameController.ManagePlayerForceAutoBuild      [total callers of this method: 1]
```

| 选项 | 说明 |
|---|---|
| `-t, --type <name>` | 只留某一端的类型（短名即可） |
| `-o, --outgoing` | 反向：这个方法**调用了谁**（默认是「谁调用它」） |
| `-d, --dir <path>` | cpp2il 产物目录（默认 `output/cpp2il_out`） |
| `-v, --verbose` | 附完整签名 |

退出码：`0` 有结果 · `1` 参数/输入有问题 · `2` 跑通了但**没找到**（方法名拼错，或确实无人调用）。

---

## 为什么不做成 MCP 工具

**因为它读的是静态文件，不是运行中的游戏。**

`output/cpp2il_out/` 是 20 MB 的反编译产物。做成 MCP 工具就得：在游戏进程里加载这些
程序集、往 `MelonMCP/` 里再塞一个 Cecil 依赖、而且**游戏没开就不能用** ——
这一切只为回答一个与运行时状态毫无关系的问题。

它属于 `tools/` 下的**离线分析器**，和 `il2cpp_unwind.py`、`recompile_mod.sh` 同类。

## 为什么用 Cecil 而不用反编译

cpp2il 产物里**已经带调用图属性**，直接读取即可：

| 做法 | 实测耗时 |
|---|---|
| **读 Cecil 属性**（本工具） | **1.5 秒**（全部 58 个程序集） |
| 全量反编译 + grep（原提案的方案 A） | **约 10 分钟** |

原提案以为必须先全量反编译，**那是错的路**。两者结果相同，只有一个值得反复跑。

**无新依赖**：`Mono.Cecil.dll` 随 MelonLoader 自带，wrapper 脚本自动解析路径并首次构建。

## ⚠️ 两个方向，属性指向相反

这是最容易踩的坑 —— 有个「看起来对」的选择是错的：

| 属性 | 挂在哪 | 描述的是谁 | 用途 |
|---|---|---|---|
| `[Calls]` | **调用者**上 | 被调用者 | 「谁调用了 X」**用这个** |
| `[CalledBy]` | **被调用者**上 | 调用者 | 看着像答案，**其实是反的** |

实测：拿 `GetObstacleRemoveCostResource` 去查 `CalledBy` 得 **0 命中**，
查 `Calls` 才得到 5 个真实调用者。

> 直觉会选 `CalledBy`（名字就是「被谁调用」），但它挂在**目标方法自己**身上，
> 列的是目标的调用者 —— 用它当索引查不到任何东西。

## ⚠️ 精度：这是**线索**，不是真值

> **`[Calls]` 是传递/近似推导，不是精确调用列表。**
> 本项目曾据此认定某函数是目标判定点并写好补丁，随后用 `e8 rel32` 全量扫描发现
> 该函数**在整个二进制里零调用者**，补丁根本不会生效。见
> [decompilation.md](decompilation.md) §4.2。

**本工具每次输出都会打印这条警告**，因为它正是那个坑的产物。

**已验证的可靠度**（本项目实测一次）：

```
GetObstacleRemoveCostResource
  工具报告:  5 个调用者
  e8 rel32 真值: 6 个调用点 -> 映射到 5 个方法
  结论: 一致（SetBuildTarget 调了两次）
```

一次一致**不足以推翻**那条纪律。作为**补丁目标**之前，仍要用
[decompilation.md](decompilation.md) §4.3 的反汇编核实。

## 实现备注

- **属性字段是 named field，不是构造参数。** Cpp2IL 生成 `[Calls(Type=..., Member=...)]`，
  构造函数无参，全走命名参数。读 `ConstructorArguments` 会**静默拿到空**。
- **`typeof(X)` 的值是嵌套的 `CustomAttributeArgument`**，要逐层解包；
  直接取值拿到的是包装对象，不是类型名。
- **`--type` 过滤的是「另一端」**，不是被查询方法自己的类型。搞反了会导致
  `-t GameController` 查一个调用者都在 `GameController` 里的方法却返回 0 ——
  这个错误我犯过一次，靠实机对拍才发现。

## 相关性

- 调用图属性由 cpp2il 的 `--use-processor attributeanalyzer,callanalyzer` 生成，
  命令见 [decompilation.md](decompilation.md) §4.2
- 同类离线工具：`tools/il2cpp_unwind.py`（minidump 回溯）、`tools/recompile_mod.sh`（反编译第三方 mod）
