# PracLab

[English](README_EN.md) | 中文

<p align="center">
  CS2（Counter-Strike 2）练习模式插件，基于 CounterStrikeSharp 与 Metamod:Source
</p>

<p align="center">
  <sub><i>注：本项目部分文档与代码由 AI 辅助生成。</i></sub>
</p>

<p align="center">
  <a href="https://github.com/MEngYangX/PracLab/stargazers">
    <img src="https://img.shields.io/github/stars/MEngYangX/PracLab?style=social" alt="GitHub stars">
  </a>
  <a href="https://github.com/MEngYangX/PracLab/network/members">
    <img src="https://img.shields.io/github/forks/MEngYangX/PracLab?style=social" alt="GitHub forks">
  </a>
  <a href="https://github.com/MEngYangX/PracLab/issues">
    <img src="https://img.shields.io/github/issues/MEngYangX/PracLab" alt="GitHub issues">
  </a>
  <a href="https://github.com/MEngYangX/PracLab/blob/main/LICENSE">
    <img src="https://img.shields.io/github/license/MEngYangX/PracLab" alt="License">
  </a>
  <a href="#">
    <img src="https://img.shields.io/badge/.NET-10.0-512bd4" alt=".NET 10.0">
  </a>
  <a href="#">
    <img src="https://img.shields.io/badge/C%23-14-239120" alt="C# 14">
  </a>
  <a href="#">
    <img src="https://img.shields.io/badge/CounterStrikeSharp-1.0.371+-blue" alt="CounterStrikeSharp 1.0.371+">
  </a>
</p>

## 功能概览

| 分类         | 说明                                           |
| ---------- | -------------------------------------------- |
| **地图管理**   | 9 张现役/备用地图快速切换（`.inferno`、`.mirage` 等）       |
| **机器人**    | 在玩家位置生成 Bot（站/蹲），自动管理碰撞，准星指向踢出               |
| **出生点**    | 9 条传送命令（同队/CT/T × 编号/最近/最远）+ 方框可视化 + E 键瞄准传送 |
| **道具重投**   | 7 条命令重投任意类型的最后投掷物，并支持回到投掷位置                  |
| **Dryrun** | 从 prac 临时切换到竞技配置打一个回合，结束自动回到 prac            |
| **回放系统**   | 录制玩家移动轨迹并由 Bot 复现，支持并行回放、按 Id 回放、列表管理        |
| **多语言**    | 中文（zh-CN，默认）与英文（en），所有玩家可见文本走本地化文件           |

## 文档

完整文档请访问：https://mengyangx.github.io/PracLab

## 致谢

本项目在开发过程中参考了以下开源项目：

- [CS2-Bot-Controller](https://github.com/XBribo/CS2-Bot-Controller) — 回放引擎的 Bot 控制、`CCSBot::Update` Hook、`PlayerRunCommand` 录制等核心思路参考。
- [MatchZy](https://github.com/shobhit-pathak/MatchZy) — 项目结构、文档组织与 CS2 插件工程实践的参考。

## 许可证

参见 [LICENSE](LICENSE)。
