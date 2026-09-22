# PracLab

> CS2（Counter-Strike 2）练习模式插件，基于 [CounterStrikeSharp](https://github.com/roflmuffin/CounterStrikeSharp) 与 [Metamod:Source](https://www.sourcemm.net/)，为竞技训练场景提供完整的道具/出生点/Bot/回放辅助命令。

## 功能概览

| 分类          | 说明                                           |
| ----------- | -------------------------------------------- |
| **地图管理**    | 地图快速切换（`.inferno`、`.mirage` 等）               |
| **机器人**     | 在玩家位置生成 Bot（站/蹲），自动管理碰撞，准星指向踢出               |
| **出生点**     | 9 条传送命令（同队/CT/T × 编号/最近/最远）+ 方框可视化 + E 键瞄准传送 |
| **投掷物反解搜索** | 按投掷方式 × 力度网格搜索可投进目标区域的描点角度，6 种道具类型 × 3 档精度   |
| **道具重投**    | 7 条命令重投任意类型的最后投掷物，并支持回到投掷位置                  |
| **Dryrun**  | 从 prac 临时切换到竞技配置打一个回合，结束自动回到 prac            |
| **回放系统**    | 录制玩家移动轨迹并由 Bot 复现，支持并行回放、按 Id 回放、列表管理        |
| **多语言**     | 中文（zh-CN，默认）与英文（en），所有玩家可见文本走本地化文件           |

## 安装

从 [Releases](https://github.com/MEngYangX/PracLab/releases) 下载最新版本压缩包，解压到 CS2 服务器 `game/csgo/` 目录（保留压缩包内 `addons/` 结构）：

- **PracLab-x.x.x-with-cssharp-\<platform\>.zip** — 首次安装请选择此版本，已内含 CounterStrikeSharp 运行时
- **PracLab-x.x.x.zip** — 仅插件本体，需自行安装 [CounterStrikeSharp](https://github.com/roflmuffin/CounterStrikeSharp) 与 [Metamod:Source](https://www.sourcemm.net/)
- **PracLabReplayEngine-x.x.x.zip** — 可选的回放引擎（Metamod C++ 插件），提供 `.record/.replay` 系列功能

详细安装步骤见[安装部署](安装.md)。

## 架构

```
┌─────────────────────────────────────────────────────────────┐
│                CS2 Server (Source 2 engine)                  │
├────────────────────────────┬────────────────────────────────┤
│  CounterStrikeSharp (CSS)  │     Metamod:Source             │
│  ────────────────────────  │     ──────────────────────     │
│  PracLab (C# / .NET 10)    │  PracLabReplayEngine (C++20)   │
│  - 命令路由 / 门控          │  - Hook ProcessMovement        │
│  - 配置 / 本地化 / JSON IO  │  - Hook CCSBot::Update         │
│  - 出生点 / Bot / 道具 UI   │  - Hook PlayerRunCommand       │
│  ─────────┬─────────────────│  - 帧级移动录制与回放           │
│           │  P/Invoke (PRL_* C ABI)                         │
│           └─────────────────────────────────────────────────►
└─────────────────────────────────────────────────────────────┘
```



**两层架构**：

- **Layer 1 — PracLab CSSharp C#**：玩家命令、聊天 UI、配置加载、本地化、录制文件 JSON 读写。
- **Layer 2 — PracLabReplayEngine C++ Metamod**：通过 Hook 引擎函数实现帧级移动录制与回放，导出 `PRL_*` C API 供 C# P/Invoke 调用。

跨语言 ABI 契约见 [`replay-engine/include/praclab_replay.h`](../../replay-engine/include/praclab_replay.h)。**未部署 Layer 2 时**：插件主体功能仍可用，仅 `.record/.replay` 系列命令向玩家提示「回放引擎未加载」。

## 项目结构

```
PracLab/
├── PracLab.cs                # 插件主类（路由表/门控/配置加载/共享数据）
├── PracLab.csproj            # .NET 10 项目文件
├── Commands/                 # 各功能 partial 类
│   ├── BotCommands.cs            # .bot / .crouchbot / .kick / .kickall
│   ├── BotCollisionHandler.cs    # Bot 碰撞管理 + 重生传送
│   ├── ClearCommands.cs          # .clear / .break
│   ├── ConVarCommands.cs         # .solid / .impacts / .traj
│   ├── DamageHandler.cs          # 实时伤害提示
│   ├── DryRunCommands.cs         # .dryrun / .restartround
│   ├── EventHandlers.cs          # OnMapStart / OnEntitySpawned / EventPlayerBlind 等
│   ├── GrenadeCommands.cs        # .rethrow* / .last
│   ├── GrenadeFunctions.cs       # 投掷物生成辅助
│   ├── HelpCommand.cs            # .help
│   ├── PracticeCommands.cs       # .prac / .map
│   ├── ReplayCommands.cs         # .record / .replay / .clearrecord* / .currentrecord
│   ├── SpawnCommands.cs          # .spawn 系列 9 条
│   ├── SpawnMarkerCommands.cs    # .showspawns / .hidespawns + E 键传送
│   ├── TeamCommands.cs           # .watch / .fas
│   ├── TimeGodCommands.cs        # .fastforward / .noflash / .god
│   └── TimerCommand.cs           # .timer
├── cfg/PracLab/              # 服务器配置文件
│   ├── config.cfg                # 插件总开关 + 默认语言
│   ├── prac.cfg                  # 练习模式 ConVar
│   └── dryrun.cfg                # 竞技模式 ConVar
├── lang/                     # 本地化
│   ├── zh-CN.json
│   └── en.json
├── replay-engine/            # Layer 2 C++ Metamod 插件
│   ├── src/                      # 录制/回放/hook 实现
│   ├── include/praclab_replay.h  # 跨语言 ABI 头文件
│   ├── configs/                  # Metamod VDF + gamedata.json
│   ├── configure.ps1             # CMake 配置脚本
│   ├── build.ps1                 # 编译脚本
│   └── CMakeLists.txt
└── documentation/            # 本文档（MkDocs）
    ├── docs/
    └── mkdocs.yml
```

## 致谢

本项目在开发过程中参考了以下开源项目：

- [CS2-Bot-Controller](https://github.com/XBribo/CS2-Bot-Controller) — 回放引擎基于上游代码集成，包含 Bot 控制（CCSBot::Update/Upkeep Hook）、移动录制与回放（ProcessMovement/PlayerRunCommand）、武器锁定、购买控制、语音发送、BotProfile、丢掷武器事件回放等模块。
- [MatchZy](https://github.com/shobhit-pathak/MatchZy) — 项目结构、文档组织与 CS2 插件工程实践的参考。

## 许可证

参见仓库根目录 [LICENSE](../../LICENSE)。
