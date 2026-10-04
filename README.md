# 龙胤立志传 Mod

使用 MelonLoader 0.7 + net6.0 开发的 IL2CPP mod 集合。

## 对开发者

需要创建一个指向游戏根目录的软链接到此位置，命名为 `gamedir`，否则无法找到依赖的 dll：

```bash
ln -s <游戏根目录> gamedir
```

## 文档

| 文件 | 内容 |
|---|---|
| [AGENTS.md](AGENTS.md) | 通用工作手册：项目结构、构建环境、反编译流程、Harmony/原生内存/运行时探查工作流 |
| [docs/friendlynoclip.md](docs/friendlynoclip.md) | 项目：战斗格子地图穿越友方 —— 设计方案、实现细节、经验教训、取舍 |
| [docs/shiftclickupgrade.md](docs/shiftclickupgrade.md) | 项目：Shift+单击直接升级建筑 —— 设计、实现、验证过程与经验教训 |
| [docs/game-internals.md](docs/game-internals.md) | 游戏机制：音效系统、资源系统、建筑/道路 —— 做别的 mod 时会复用的发现 |
