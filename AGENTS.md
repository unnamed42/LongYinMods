# AGENTS.md — 龙胤立志传 Mod 开发

面向 AI 助手与开发者的**工作手册入口**：项目结构、构建环境、以及到各专题文档的索引。

> **本文件只保留「每次都要知道」的内容 + 索引。** 展开细节在 `docs/` 下分专题存放。
> 这是**渐进披露**：先读本文件，需要时再按 §7 的索引跳到对应专题。

---

## 1. 这是什么项目

**龙胤立志传**（LongYinLiZhiZhuan）的 **MelonLoader IL2CPP mod 集合**。不是游戏本体开发，而是通过 Harmony / 原生内存改写扩展一个已发行 Unity 游戏的行为。

- 游戏：Unity **2020.3.48f1c1** / **IL2CPP** / **x64** / IL2CPP metadata **v27**
- Mod 框架：**MelonLoader 0.7.x Open-Beta** + **CoreCLR**（`net6.0`）
- 运行方式：Linux 下通过 **Wine / Proton**

因此所有代码都在**两个世界之间的边界**上：托管 C# 侧（MelonLoader / Harmony / Il2CppInterop）与原生侧（`GameAssembly.dll` 里编译后的 C++）。**本项目的大部分难度来自这条边界。**

### 1.1 文档分工（写东西前先看这条）

| 内容 | 写在哪 | 为什么 |
|---|---|---|
| **不随游戏构建变化的**：工具命令、环境、API 用法、踩坑、项目约定 | `AGENTS.md`（简述）+ `docs/<专题>.md`（详情） | 换游戏版本也不失效 |
| **具体的游戏地址 / 字段偏移 / metadata token / 调用链 / 实测行为** | `docs/<项目>.md`（如 [`friendlynoclip.md`](docs/friendlynoclip.md)） | **游戏一更新即失效** |
| **游戏本身的知识**（世界观、数值规则、机制） | MCP 知识库 `add_game_knowledge` | 与 mod 开发无关、更新也不变 |

---

## 2. 目录结构

```
LongYinMods/
├── AGENTS.md              本文件 —— 入口 + 索引
├── README.md              项目简介
├── docs/                  专题文档（见 §7 索引）+ 各 mod 的设计文档
├── gamedir -> ...         指向游戏根目录的软链接（必需，见下）
├── <Project>/             各 mod 的 C# 工程（csproj + 源码）

├── output/                本地临时产物（全部不提交 git，见下）
│   ├── decomp/            反编译辅助（il2cpp_map.py 等）
│   ├── decomp/full/Il2Cpp/  ilspycmd 产出的完整托管反编译
│   ├── decomp_gud/        Ghidra 反编译的 C 伪代码（关键函数）
│   ├── cpp2il_out/        cpp2il 产物（含调用图属性）
│   ├── dumper_out/        Il2CppDumper 产物（dump.cs / script.json）+ 自建 Ghidra 脚本
│   ├── ghidra_proj/       已分析好的 Ghidra 工程（勿重新导入）
│   └── logs/              Ghidra 等工具的日志
├── tools/                 自建脚本（gdb_catch.sh 等）
├── MelonMCP/              自建的 Unity MCP 服务端（见 [docs/runtime-probing.md](docs/runtime-probing.md)）
│   ├── MelonMCP/          工程本体（csproj / 源码 / Server / Tools）
│   ├── lib/net6/mcs.dll   内嵌依赖（Mono.CSharp），构建必需，**要进 git**
│   └── nuget.config       NuGet 缓存重定向
```

> **`gamedir` 只建在仓库根，各工程用相对路径上溯过去。**
>
> ```bash
> ln -s <游戏根目录> gamedir          # 只在仓库根建一个
> ```
>
> 各 csproj 里的写法是「从自己所在目录上溯到仓库根」，**深度不同、写法不同**：
>
> | 工程位置 | HintPath 前缀 |
> |---|---|
> | `<Project>/<Project>.csproj` | `..\gamedir\...` |
> | `<Project>/<Project>/<Project>.csproj`（多一层，如 MelonMCP） | `..\..\gamedir\...` |
>
> ⚠️ **不要在每个工程目录下各建一个 `gamedir` 软链接。** 那种做法看着方便，但：
> ①它们是独立文件，换机器 / 重装时要逐个重建，忘一个就构建失败；
> ②它们会被当成待提交内容（绝对路径的机器相关符号链接，**不能进 git**）；
> ③路径一旦挪动就静默失效。
> 统一从仓库根出发，只需维护**一个**链接（它在 `.gitignore` 里）。
>
> ⚠️ **`gamedir` 是跨工作区边界的软链接**（指向游戏安装盘），**默认沙箱策略拒绝写入**。
> 但**可以提权写入** —— 部署 DLL 这类写操作，**AI 自己提权完成即可**，
> 不必推给用户（见 §3.2）。只有提权也做不到的操作才需要用户介入。
>
> **`output/` 下全部是本地分析产物，不进 git**（`.gitignore` 已忽略 `output/`）。用反编译流程随时可重新生成。

---

## 3. 构建与部署

### 3.1 构建

```bash
dotnet build <Project>/<Project>.csproj -c Debug
# 产物：<Project>/bin/Debug/net6.0/<Project>.dll
```

内嵌第三方 DLL（ILRepack）的完整配方见 **[docs/ilrepack.md](docs/ilrepack.md)**。

### 3.2 部署：**读不用提权，只有写要**

先分清两件事（本项目曾无差别地每次提权，白白打断用户）：

| 操作 | 要不要提权 |
|---|---|
| **读** `gamedir/` 下任何文件（DLL / 日志 / cfg / dump） | ❌ **不用** —— 普通 `read`/`bash` 就行 |
| **写** `gamedir/`（部署 DLL、改 cfg） | ✅ **需要** `danger-full-access` |

实测：`ls gamedir/version.dll` 与 `ls /run/media/.../coredump/` 在默认沙箱下都能完成；
只有 `touch gamedir/Mods/x` 会报 `只读文件系统`。

→ **排查阶段（看日志、看 dump、读 DLL）一律不提权**；
只在**最后部署**那一下提权，并在同一次调用里核对 md5。

**⭐ 部署：AI 自己提权完成，不要推给用户。**

```bash
cp <Project>/bin/Debug/net6.0/<Project>.dll gamedir/Mods/
# 两边必须一致才算部署成功
md5sum gamedir/Mods/<Project>.dll <Project>/bin/Debug/net6.0/<Project>.dll
```

**为什么这条要写进手册**：本项目**因为跑的是旧产物而白耗过整整两轮**，
而且症状本身就是「代码不生效」—— **与真正的逻辑 bug 无法区分**，
于是 AI 会跑去读一堆正确的代码，找不存在的 bug。
让它自己在提权调用里顺手把 DLL 拷过去，就从根上消除了这一类误导。

> ⚠️ 无论谁拷的，**都必须核对两边 md5 一致**才算部署成功。

### 3.2.1 ⚠️ 改代码后必须**冷启动**（比拷错文件更隐蔽）

托管补丁（Harmony）才可能热重载；**原生改写不会**，且模块基址每次启动都变。
更关键的是：**很多钩子只在特定时机触发一次**（例如战斗开局建地图）。
在已经跑起来的进程里重新加载 DLL，**那个时机已经过去了**。

本项目的真实教训：`BattleMapData.Generate` 的 Postfix 明明
`patcherIsValid=true`、`attached=true`，但因为补丁是在该场战斗的地图
**生成之后**才注册的，postfix **从头到尾没执行过**（日志里一条都没有）。

**验证结论前必做**：

1. 确认 `Mods/` 里的 md5 == 刚构建的 md5；
2. **冷启动游戏**，重新走到触发路径；
3. 找**自己代码打的那条日志**（而非只看 Harmony 的「已挂上」）。

> 💡 「补丁已挂上」≠「代码跑了」（详见 [docs/harmony-il2cpp.md](docs/harmony-il2cpp.md)）。
> 排查时**先确认产物与时机**，再去读代码 —— 否则很容易在正确的代码里
> 找不存在的 bug。

### 3.3 csproj 必需引用

基础四个：`MelonLoader.dll`、`0Harmony.dll`、`Il2CppInterop.Runtime.dll`、`Assembly-CSharp.dll`。

**外加（漏了会报奇怪的错）**：

| 引用 | 不引会怎样 |
|---|---|
| `Il2Cppmscorlib.dll`、`Il2CppSystem.dll`、`Il2CppSystem.Core.dll` | `Il2CppSystem` / `Il2CppSystem.Collections.Generic` 命名空间**不在这三个之外**（不在 Il2CppInterop.Runtime 里） |
| `UnityEngine.CoreModule.dll` | `error CS0012: 类型"MonoBehaviour"在未引用的程序集中定义` |
| `UnityEngine.dll` | 同上，类型解析失败 |

全部用 `<Private>False</Private>`（不要把游戏 DLL 拷进输出目录），HintPath 走 `$(Il2cppDir)` / `$(MelonLoaderDir)`。建议加 `<Nullable>enable</Nullable>`（免得 CS8632 警告淹没日志）。

### 3.4 常见编译坑

- **`MelonBase.LoggerInstance` 是实例成员**，静态补丁方法里直接用会 `CS0120`。
  解法：`internal static MelonLogger.Instance Log = null!;`，在 `OnInitializeMelon` 里 `Log = LoggerInstance;`
- **日志 API 是 `Warning(...)` 不是 `Warn(...)`**（只有 `Msg` / `Warning` / `Error`），写成 `Warn` 报 `CS1061`。
- 跨文件引用日志要写 **`Plugin.Log.Warning(...)`**，不能裸写 `Log`。
- 碰原生内存需在 csproj 加 `<AllowUnsafeBlocks>true</AllowUnsafeBlocks>`，否则 `CS0227`。

---

## 4. 五条最贵重的纪律

**只有五条**（其余细节在专题文档里）。这些都是本项目**真金白银换来的**，
且**跨游戏、跨任务都成立**：

1. **改完代码先确认产物与时机**，再去读代码 —— 否则会在正确的代码里找不存在的 bug。
   见 §3.2（md5）与 §3.2.1（冷启动）。

2. **「挂载成功」≠「触发」≠「生效」。** 必须有**自己代码打的、不受门控的**运行时日志。
   见 [docs/harmony-il2cpp.md](docs/harmony-il2cpp.md)。

3. **stub 的「放行」出口要送回「游戏原版的落点」，不要跳到「接受路径的中段」。**
   这是本项目代价最大的坑：选错出口会**静默失效**，或者得到一个**看起来正常但语义错误**的功能。
   见 [docs/native-hooks.md](docs/native-hooks.md)。

4. **不要手写机器码 —— 用 `Iced` 汇编器**（MelonLoader 自带，零新依赖）。
   手写字节在本项目崩过；Iced 把「手算编码」这类错误降为零。
   见 [docs/native-hooks.md](docs/native-hooks.md)。

5. **工具由用户安装，AI 不得自行下载或安装**（含装到工作区内）。
   判断标准是**意图**：当前环境里原本不存在、需要你额外获取才能用的，就请用户装。
   见 §6。

---

## 5. 出问题时的排查顺序

遇到「代码不生效 / 崩溃 / 行为诡异」时，**按这个顺序**，别跳步：

| # | 先查 | 为什么 | 详见 |
|---|---|---|---|
| 1 | `Mods/` 里 DLL 的 md5 是否 == 刚构建的 | 跑旧产物时症状与逻辑 bug **无法区分** | §3.2 |
| 2 | 是否**冷启动**过、触发时机是否已过 | 钩子常常只触发一次 | §3.2.1 |
| 3 | 有没有**自己代码打的**日志（且未被开关门控） | 只看 Harmony 的「已挂上」等于没看 | [docs/harmony-il2cpp.md](docs/harmony-il2cpp.md) |
| 4 | 用 MCP 看**活进程**的真实状态，而不是猜 | 有活进程就别猜日志 | [docs/runtime-probing.md](docs/runtime-probing.md) |
| 5 | 二分隔离：把可疑功能各配一个开关 | 比读代码快得多 | [docs/runtime-probing.md](docs/runtime-probing.md) |

---

## 6. 工具链

**所有工具由用户安装，AI 一律不得自行下载或安装 —— 包括装到工作区内的情况。**

这条曾被执行成「只禁止写 `~/.dotnet` 等沙箱外路径」，于是有人把单个可执行脚本
`curl` 到工作区的 `tools/bin/` 里当作「不算安装」。**那是钻字面，不是守约定。**
判断标准是**意图**而不是落地路径：只要一个工具在当前环境里原本不存在、需要你额外
获取才能用，就应该停下来请用户装，不要自己想办法绕。

- ❌ `pip install` / `npm install` / `curl` 下载脚本 / 解压 tar 到任何位置
- ❌ `~/.dotnet/tools`（会报 `Read-only file system`）
- ✅ 发现缺工具 → 告诉用户「需要装 X」→ 用户装完继续

> 实际上多数工具已经装好了，先 `which <tool>` 确认，不要提前假设缺失。

| 工具 | 用途 |
|---|---|
| `/usr/bin/dotnet` | 构建（SDK 10.0.x） |
| `/usr/bin/ilspycmd` | 托管反编译（第 1 层） |
| `~/.local/bin/cpp2il` | 调用图恢复（第 2 层） |
| python + `capstone` | 反汇编核实（第 3 层） |
| `/opt/ghidra` | C 伪代码（第 3.5 层）。**运行需 `danger-full-access` 提权** |
| `pyghidra` 3.0.2 | 已装；但脚本仍推荐写 `.java` |
| `git-filter-repo` 2.47.0 | 历史重写。`/usr/bin/git-filter-repo` |
| `objdump` / `monodis` / `gdb` | 备用：反汇编 / 程序集 IL / 调试 |

---

## 7. 专题文档索引

**按「我现在要做什么」查**：

| 我要…… | 读 |
|---|---|
| 看懂游戏的原生代码（签名 / 调用图 / 汇编 / 伪代码 / metadata） | [docs/decompilation.md](docs/decompilation.md) |
| 给游戏方法挂 Harmony 补丁 | [docs/harmony-il2cpp.md](docs/harmony-il2cpp.md) |
| 读写原生内存、装自制 detour、用 Iced 汇编 stub | [docs/native-hooks.md](docs/native-hooks.md) |
| 在活进程里探查状态、或分析崩溃 | [docs/runtime-probing.md](docs/runtime-probing.md) |
| 往 mod 里内嵌第三方 DLL（ILRepack） | [docs/ilrepack.md](docs/ilrepack.md) |
| 看某个 mod 的设计与取舍 | [docs/friendlynoclip.md](docs/friendlynoclip.md) |
| 遇到不认识的数值字段（是不是枚举？有哪几档？） | [docs/native-hooks.md](docs/native-hooks.md) 的「不透明字段三步排查法」 |

---

## 8. 各项目文档

| 项目 | 说明 | 文档 |
|---|---|---|
| FriendlyNoclip | 战斗格子地图允许穿越友方 | [docs/friendlynoclip.md](docs/friendlynoclip.md) |

> **通用内容写 `AGENTS.md` / `docs/<专题>.md`，项目内容写 `docs/<项目>.md`，新发现随代码改动一起更新（不是以后补）。**
