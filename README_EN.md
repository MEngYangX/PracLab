# PracLab

English | [中文](README.md)

<p align="center">
  A CS2 (Counter-Strike 2) practice-mode plugin built on CounterStrikeSharp and Metamod:Source, providing a complete set of utility commands for grenade/spawn/bot/replay training scenarios.
</p>

<p align="center">
  <sub><i>Note: Some documentation and code in this project were generated with AI assistance.</i></sub>
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

## Feature Overview

| Category | Description |
| --- | --- |
| **Map management** | Quick switch between 9 active/reserve maps (`.inferno`, `.mirage`, etc.) |
| **Bots** | Spawn a bot at the player position (standing/crouching), auto-managed collision, crosshair-targeted kick |
| **Spawn points** | 9 teleport commands (same-team/CT/T × numbered/nearest/farthest) + box visualization + E-key aim teleport |
| **Grenade rethrow** | 7 commands to rethrow the last grenade of any type, plus return-to-throw-position |
| **Dryrun** | Temporarily switch from prac to competitive config for one round, auto-revert to prac when round ends |
| **Replay system** | Record player movement trajectories and play them back via bots; supports parallel playback, playback-by-Id, list management |
| **Localization** | Chinese (zh-CN, default) and English (en); all player-visible text is driven by localization files |

## Documentation

Full documentation is available at: https://mengyangx.github.io/PracLab

## Credits

This project drew inspiration from the following open-source projects during development:

- [CS2-Bot-Controller](https://github.com/XBribo/CS2-Bot-Controller) — Reference for the replay engine's bot control, `CCSBot::Update` hook, and `PlayerRunCommand` recording.
- [MatchZy](https://github.com/shobhit-pathak/MatchZy) — Reference for project structure, documentation organization, and CS2 plugin engineering practices.

## License

See [LICENSE](LICENSE).
