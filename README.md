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
