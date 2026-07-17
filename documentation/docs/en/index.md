# PracLab

> A CS2 (Counter-Strike 2) practice-mode plugin built on [CounterStrikeSharp](https://github.com/roflmuffin/CounterStrikeSharp) and [Metamod:Source](https://www.sourcemm.net/), providing a complete set of utility commands for grenade/spawn/bot/replay training scenarios.

- **Target framework**: .NET 10 + CounterStrikeSharp.API 1.0.371
- **Supported maps**: Inferno, Mirage, Nuke, Ancient, Vertigo, Anubis, Dust II, Train, Cache

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

## Architecture

```
┌─────────────────────────────────────────────────────────────┐
│                CS2 Server (Source 2 engine)                  │
├────────────────────────────┬────────────────────────────────┤
│  CounterStrikeSharp (CSS)  │     Metamod:Source             │
│  ────────────────────────  │     ──────────────────────     │
│  PracLab (C# / .NET 10)    │  PracLabReplayEngine (C++20)   │
│  - Command routing / gating │  - Hook ProcessMovement        │
│  - Config / i18n / JSON IO │  - Hook CCSBot::Update         │
│  - Spawns / Bots / Nade UI │  - Hook PlayerRunCommand       │
│  ─────────┬─────────────────│  - Frame-level move record/replay │
│           │  P/Invoke (PRL_* C ABI)                         │
│           └─────────────────────────────────────────────────►
└─────────────────────────────────────────────────────────────┘
```

**Two-layer architecture**:

- **L1 — PracLab CSSharp C#**: player commands, chat UI, config loading, localization, JSON read/write for recording files.
- **L2 — PracLabReplayEngine C++ Metamod**: hooks engine functions to implement frame-level movement recording and playback; exports the `PRL_*` C API for C# P/Invoke.

The cross-language ABI contract is documented in [`replay-engine/include/praclab_replay.h`](../../replay-engine/include/praclab_replay.h). **When L 2 is not deployed**: the main plugin still works; only `.record/.replay` family commands show a "replay engine not loaded" message to players.

## Project Structure

```
PracLab/
├── PracLab.cs                # Plugin main class (route table / gating / config loader / shared state)
├── PracLab.csproj            # .NET 10 project file
├── Commands/                 # Feature partial classes
│   ├── BotCommands.cs            # .bot / .crouchbot / .kick / .kickall
│   ├── BotCollisionHandler.cs    # Bot collision management + respawn teleport
│   ├── ClearCommands.cs          # .clear / .break
│   ├── ConVarCommands.cs         # .solid / .impacts / .traj
│   ├── DamageHandler.cs          # Real-time damage feedback
│   ├── DryRunCommands.cs         # .dryrun / .restartround
│   ├── EventHandlers.cs          # OnMapStart / OnEntitySpawned / EventPlayerBlind etc.
│   ├── GrenadeCommands.cs        # .rethrow* / .last
│   ├── GrenadeFunctions.cs       # Grenade spawn helpers
│   ├── HelpCommand.cs            # .help
│   ├── PracticeCommands.cs       # .prac / .map
│   ├── ReplayCommands.cs         # .record / .replay / .clearrecord* / .currentrecord
│   ├── SpawnCommands.cs          # .spawn family (9 commands)
│   ├── SpawnMarkerCommands.cs    # .showspawns / .hidespawns + E-key teleport
│   ├── TeamCommands.cs           # .watch / .fas
│   ├── TimeGodCommands.cs        # .fastforward / .noflash / .god
│   └── TimerCommand.cs           # .timer
├── cfg/PracLab/              # Server config files
│   ├── config.cfg                # Master toggle + default language
│   ├── prac.cfg                  # Practice-mode ConVars
│   └── dryrun.cfg                # Competitive-mode ConVars (used by .dryrun)
├── lang/                     # Localization
│   ├── zh-CN.json
│   └── en.json
├── replay-engine/            # L2 C++ Metamod plugin
│   ├── src/                      # record/playback/hook implementations
│   ├── include/praclab_replay.h  # Cross-language ABI header
│   ├── configs/                  # Metamod VDF + gamedata.json
│   ├── configure.ps1             # CMake configure script
│   ├── build.ps1                 # Build script
│   └── CMakeLists.txt
└── documentation/            # This site (MkDocs)
    ├── docs/
    └── mkdocs.yml
```

## Credits

This project drew inspiration from the following open-source projects during development:

- [CS2-Bot-Controller](https://github.com/XBribo/CS2-Bot-Controller) — Reference for the replay engine's bot control, `CCSBot::Update` hook, and `PlayerRunCommand` recording.
- [MatchZy](https://github.com/shobhit-pathak/MatchZy) — Reference for project structure, documentation organization, and CS2 plugin engineering practices.

## License

See the repository root [LICENSE](../../LICENSE).
