# Changelog

本项目所有重要变更将记录在此文件中。

格式基于 [Keep a Changelog](https://keepachangelog.com/zh-CN/1.1.0/)，版本号遵循 [语义化版本](https://semver.org/lang/zh-CN/)。

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
