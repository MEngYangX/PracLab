# Changelog

本项目所有重要变更将记录在此文件中。

格式基于 [Keep a Changelog](https://keepachangelog.com/zh-CN/1.1.0/)，版本号遵循 [语义化版本](https://semver.org/lang/zh-CN/)。

## [0.3.1] - 2026-09-18

### Fixed

- **ReplayEngine DLL 预加载路径错误**：兼容 CSSharp v1.0.372 `GameDirectory` 路径变更，改为双路径探测，[Issue #5](https://github.com/MEngYangX/PracLab/issues/5)。

### Changed

- **回放引擎更新至 BotController v0.6.3**：新增丢掷武器录制回放与投掷物出生对齐模块，ABI 18 → 20。
- **CSSharp 依赖升级**：1.0.372 → 1.0.374，详见 [v1.0.374](https://github.com/roflmuffin/CounterStrikeSharp/releases/tag/v1.0.374)。

## [0.3.0] - 2026-08-19

### Fixed

- **`.clearrecord` 用法提示 HTML 转义**：输入 `.clearrecord`（无参数）后聊天栏显示 `.clearrecord &lt;Id&gt;` 而非 `.clearrecord <Id>`。根因：`lang/zh-CN.json` 与 `lang/en.json` 中 `record.usage` 键的值使用了 HTML 转义 `&lt;`/`&gt;`，CounterStrikeSharp 聊天栏不解析 HTML 实体（颜色占位符是 `{colorname}` 格式），原样输出。修复为直接尖括号。已扫描全部 lang 文件，仅此一处残留。

### Changed

- **迁移到 CSSharp 原生 Trace API**：移除 CS2TraceRay NuGet 依赖及 `CS2TraceRay.gamedata.json`，全面切换至 v1.0.372 新增的 `Trace.TraceShape` / `Trace.TraceEndShape` 原生 API。涉及 `NadeDrawCommands`、`NadeSearchCommands`、`NadeResultCommands`、`NadeTestCommands` 四个模块的射线调用点，统一使用 `CounterStrikeSharp.API.Modules.Utils` 命名空间下的 `Trace` / `TraceResult` / `Contents` / `Masks` 类型；`SkipPawn` 参数类型从 `nint` 调整为 `CBaseEntity?`，新增 `ToCssVector` / `ToSystemVector` 在 `CounterStrikeSharp.API.Modules.Utils.Vector` 与 `System.Numerics.Vector3` 之间转换。同步简化 `build.yml`（移除 gamedata.json 复制步骤）、移除 README 致谢中的 CS2TraceRay 引用。消除因 CS2 游戏更新导致签名失效引发 Linux segfault 的稳定性隐患。
- **CSSharp 依赖升级**：`PracLab.csproj` 中 `CounterStrikeSharp.API` 版本由 1.0.371 升级到 1.0.372。v1.0.372 主要变更为新增 Ray/Hull Trace API（INavPhysicsInterface，PR #1331）与更新 Schema Definitions 至 1.41.6.9（PR #1356）。
- **回放引擎更新至 BotController v0.6.1**：同步上游 [CS2-Bot-Controller](https://github.com/XBribo/CS2-Bot-Controller) v0.6.1（ABI 17→18）。新增 `SuppressUsercmd` 导出（抑制指定 usercmd 按钮一段时长，PracLab 当前未使用但保持 ABI 一致）；`InputInjector` 新增 `UsercmdSuppression` 结构与 `ApplyUsercmdSuppressions` 处理路径；`ClearUsercmdInjections` 与 `Remove()` 同步清理 suppressions。更新 `praclab_replay.h`、`exports.cpp`、`plugin.h` 版本与注释。

## [0.2.1] - 2026-08-06

### Fixed

- **`.last` / `.ls` 传送异常**：传送回最后投掷位置后出现三个问题——传送点悬空、玩家视角倾斜（roll）、玩家模型翻转（身体躺倒）。根因有三：①记录时位置取的是投掷物 `AbsOrigin`（玩家眼前上方）而非玩家脚下位置；②角度取的是投掷物 `AbsRotation`（飞行中含 roll）而非玩家视角；③用 `pawn.Teleport` 设置角度会把 pitch 写入 `CGameSceneNode.m_angRotation`（身体旋转源）导致模型翻转。修复：在 `GrenadeThrowRecord` 新增玩家脚下位置（`PlayerPawn.AbsOrigin`）与玩家瞄准视角（`EyeAngles`）字段；传送改用 `setpos` + `setang` 客户端命令（只设位置与 eye angles，不写 `m_angRotation`），身体保持直立。`.rethrow` 系列不受影响（仍用投掷物数据复现弹道）。

## [0.2.0] - 2026-08-06

### Added

- **投掷物反解搜索系统**：完整的弹道反解工具链，支持 6 种道具类型（smoke/flash/he/molo/inc/decoy）× 7 种投掷方式 × 3 档力度网格搜索，3 档精度（low 4° / mid 2°+细化 / high 1°+细化），毫秒预算摊销执行不卡服务器。
  - **目标区域绘制**（`.nadedraw` / `.ndr`）：三段式立方体绘制（底面角点 → 底面斜对角 → 高度点），左键确认 / 右键取消，红色预览 / 绿色已确认线框，支持空中取点自动投影到地面，最多 8 个区域。
  - **搜索命令**：`.findall`（`.fa`）、`.findnormal`（`.fn`）、`.findjump`（`.fj`）、`.findrunjump`（`.frj`）、`.findduck`（`.fd`）、`.findduckjump`（`.fdj`）、`.findduckrunjump`（`.fdrj`），每种方式均支持左/中/右力度子命令（如 `.fnl`/`.fnm`/`.fnr`）。
  - **精度/类型切换**：`.nadeaccuracy`（`.nac`）、`.nadetype`（`.nt`）。
  - **投掷校准**：`.nadetest`（`.ntt`）登记后 30 秒内投掷，自动识别力度/方式并在服务器控制台输出真实 vs 预测对比数据。
  - **单次仿真调试**：`.nadetestsim`（`.nts`）指定 `yaw pitch mode strength` 跑一次弹道仿真，输出出手点/碰撞日志/落点/终止原因/飞行时长。
  - **搜索结果管理**：`.nadelist`（`.nl`）控制台输出结果表格、`.nadeclearlist`（`.ncl`）清除结果及可视化实体、`.nadegoto`（`.ng`）传送到指定结果站位并显示弹道预览。
- **投掷物数据提示**：道具落地后显示飞行时间与反弹次数；闪光弹命中时显示对目标的致盲时间。
- **F3 按键绑定**：按下 F3（自动购买键）自动开始 `.timer` 计时，再次按下结束。
- **F4 按键绑定**：按下 F4（重新购买键）显示 `.help` 帮助信息。
- **`.clear` 缩写 `.cl`**。

### Fixed

- **Bot 阵营归属**：切换到与 bot 同阵营时击杀 bot 会错误提示「你击杀了一名队友」。Bot 现在不属于任何一方阵营，消除误报。
- **CS2TraceRay Linux 签名失效**：2026-07 游戏更新导致 `GameTraceManager` Linux 签名（`4C 8D 3D ? ? ? ? 48 8B 80 18 02 00 00`）匹配 19 处且全部指向 .text 段 RTTI/vtable 地址，解引用后 segfault。从 `libserver.so` 重新提取唯一匹配签名 `48 8D 0D ? ? ? ? F3 41 0F 10 4F 08`（LEA rcx,[rip+disp] 加载 .data 段全局变量），经反汇编验证与 TraceFunc 调用链一致。

### Changed

- **统一输出格式**：控制台、聊天栏、终端输出统一为 `[PracLab] {Time} {Module} {Message}` 结构化日志格式。
- **清除调试信息**：移除所有 debug/DBG 残留输出。
- **回放引擎更新**：replay-engine 同步至 [CS2-Bot-Controller](https://github.com/XBribo/CS2-Bot-Controller) 上游最新版本，保证 Linux 和 Windows 双平台正常运行。

## [0.1.2] - 2026-07-20

### Fixed

- **Linux 下投掷物 .rethrow 失效**：2026-07 游戏更新导致 Linux 端 Create 函数签名整体失效——`CHEGrenadeProjectile::Create` / `CDecoyProjectile::Create` 旧签名失效，fallback 路径创建无引信"空壳"实体；`CSmokeGrenadeProjectile::Create` 旧签名命中 2 个函数且首个匹配为 flashbang Create（0xd16ed0），导致 .rethrowsmoke 实际投出闪光弹。从 `libserver.so` (2026-07-16, ServerVersion 2000876) 重新提取唯一匹配签名：smoke 函数起始 0x1408d80、HE 0xd17860、decoy 0x1407fe0。

## [0.1.1] - 2026-07-19

### Fixed

- **诱饵弹/手雷 .rethrow 错误**：2026-07 游戏更新导致 `CHEGrenadeProjectile::Create`（HE）和 `CDecoyProjectile::Create` 的 Windows 函数签名失效，fallback 路径创建的"空壳"实体没有引信且缺少物理阻尼。从 `server.dll` (2026-07-17) 重新提取唯一匹配的函数序言签名：HE 栈帧 `sub rsp,0x50` → `0x40`（函数起始 0x1803896a0），decoy 栈帧 `0x168` → `0x158`（函数起始 0x1809567b0）。同时移除已被证据证明无效的 HE fallback `AcceptInput("Detonate")` 定时器。
- **出生点序号显示方向错误（de_dust2 CT）**：CT 队伍出生点方框的序号文字朝向需相对 T 队伍旋转 180° 才能正向朝向玩家。在 `SpawnTextYawByMap` 中为 `de_dust2` 的 CT 队伍配置 `180f` 旋转。

## [0.1.0] - 2026-07-17

### Added

- **Practice 模式**：一键加载练习配置（`.prac`），包含无限弹药、全甲、轨迹线、即时购买等
- **地图管理**：9 张地图快速切换（`.inferno`、`.mirage`、`.nuke`、`.ancient`、`.vertigo`、`.anubis`、`.dust2`、`.train`、`.cache`）
- **机器人系统**：在玩家位置生成 Bot（`.bot`/`.crouchbot`），准星指向踢出（`.kick`/`.kickall`），自动碰撞管理
- **出生点系统**：9 条传送命令 + 方框可视化与序号显示（`.showspawns`/`.hidespawns`）+ E 键瞄准传送
- **道具重投**：7 条命令重投任意类型最后投掷物（`.rethrow`/`.rethrowflash`/`.rethrowsmoke`/`.rethrowhe`/`.rethrowdecoy`/`.rethrowmolotov`）+ 回到投掷位置（`.last`）
- **Dryrun 模式**：临时切换到竞技配置打一个回合（`.dryrun`/`.dry`），回合重启（`.restartround`/`.rr`）
- **回放系统**：录制玩家移动轨迹并由 Bot 复现（`.record`/`.stoprecord`/`.replay`/`.stopreplay`/`.clearrecord`/`.clearrecordall`/`.currentrecord`）
- **时间/无敌**：10× 时间快进（`.fastforward`）、闪光免疫（`.noflash`）、上帝模式（`.god`）
- **ConVar 切换**：`.solid`、`.impacts`、`.traj`
- **多语言支持**：中文（zh-CN，默认）与英文（en），所有玩家可见文本走本地化文件
- **两层架构**：C# 插件（Layer 1）+ C++ Metamod 回放引擎（Layer 2），通过 P/Invoke 跨语言调用
- **出生点方框**：每张地图的方框 Z 偏移与序号 yaw 旋转已硬编码配置
