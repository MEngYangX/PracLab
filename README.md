# PracLab

English | [中文](README_CN.md)

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
    <img src="https://img.shields.io/badge/CounterStrikeSharp-1.0.375+-blue" alt="CounterStrikeSharp 1.0.375+">
  </a>
</p>

## Feature Overview

| Category | Description |
| --- | --- |
| **Map management** | Quick map switching (`.inferno`, `.mirage`, etc.) |
| **Bots** | Spawn a bot at the player position (standing/crouching), auto-managed collision, crosshair-targeted kick |
| **Spawn points** | 9 teleport commands (same-team/CT/T × numbered/nearest/farthest) + box visualization + E-key aim teleport |
| **Grenade inverse search** | Grid-searches aim angles that land in the target region across throw modes × strengths; 6 item types × 3 accuracy tiers |
| **Grenade rethrow** | 7 commands to rethrow the last grenade of any type, plus return-to-throw-position |
| **Dryrun** | Temporarily switch from prac to competitive config for one round, auto-revert to prac when round ends |
| **Replay system** | Record player movement trajectories and play them back via bots; supports parallel playback, playback-by-Id, list management |
| **Practice HUD** | In-game real-time training feedback: counter-strafe assessment, shot stability, air strafe sync, spray trace (the former three require the client-side VPK resource; the spray trace is drawn in world space) |
| **Localization** | Chinese (zh-CN, default) and English (en); all player-visible text is driven by localization files |

## Installation

Download the latest release archive from [Releases](https://github.com/MEngYangX/PracLab/releases) and extract it into the CS2 server's `game/csgo/` directory (keep the `addons/` structure inside the archive):

- **PracLab-x.x.x-with-cssharp-\<platform\>.zip** — pick this one for a first-time install; it bundles the CounterStrikeSharp runtime
- **PracLab-x.x.x.zip** — plugin only; requires installing [CounterStrikeSharp](https://github.com/roflmuffin/CounterStrikeSharp) and [Metamod:Source](https://www.sourcemm.net/) yourself
- **PracLabReplayEngine-x.x.x.zip** — optional replay engine (Metamod C++ plugin) providing the `.record/.replay` command family

The Practice HUD (`.strafe`/`.shot`/`.sync`) panel display requires the client to install the optional `prac_hud.vpk` resource; judgment and statistics still work without it. The spray trace (`.recoil`) is drawn in world space and does not depend on this resource. See the "Practice HUD Resource" section of the [installation guide](https://mengyangx.github.io/PracLab/en/installation/).

For detailed steps, see the [installation guide](https://mengyangx.github.io/PracLab/en/installation/).

## Documentation

Full documentation is available at: https://mengyangx.github.io/PracLab

## Credits

This project drew inspiration from the following open-source projects during development:

- [CounterStrikeSharp](https://github.com/roflmuffin/CounterStrikeSharp) — The C# plugin framework this project runs on (Layer 1).
- [Metamod:Source](https://github.com/alliedmodders/metamod-source/) — The Metamod framework loading the C++ engine plugin (Layer 2).
- [CS2-Bot-Controller](https://github.com/XBribo/CS2-Bot-Controller) — Replay engine integrated from upstream, including bot control (`CCSBot::Update`/`Upkeep` hooks), movement recording and playback (`ProcessMovement`/`PlayerRunCommand`), weapon locking, purchase control, voice chat, `BotProfile`, and drop-weapon event playback modules.
- [MatchZy](https://github.com/shobhit-pathak/MatchZy) — Reference for project structure, documentation organization, and CS2 plugin engineering practices.
- [cs-match-hud](https://github.com/qianjiachun/cs-match-hud) — Product design and data-organization reference for the Practice HUD (counter-strafe assessment / shot stability / air sync / spray trace).
- [cs2kz-metamod](https://github.com/KZGlobalTeam/cs2kz-metamod) — The air-sync rate algorithm is ported from its jumpstats module (speed-gain tick detection).

## License

See [LICENSE](LICENSE).
