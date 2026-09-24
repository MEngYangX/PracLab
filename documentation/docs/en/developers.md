# Developers

This page describes how to build both layers of PracLab from source, along with project structure notes. For deploying build artifacts, see [Installation](installation.md).

## 1. Build Dependencies

### 1.1 Layer 1 (C# plugin)

| Dependency                                                                      | Version  | Notes                   |
| ---                                                                             | ---      | ---                     |
| [.NET SDK](https://dotnet.microsoft.com/download)                               | 10.0+    | To build Layer 1        |
| [CounterStrikeSharp.API](https://www.nuget.org/packages/CounterStrikeSharp.API) | 1.0.375+ | Auto-restored via NuGet |

### 1.2 Layer 2 (C++ Metamod plugin)

| Dependency            | Version          | Notes                                |
| ---                   | ---              | ---                                  |
| Visual Studio 2022+   | MSVC 14.51+      | Windows build                        |
| CMake                 | 3.20+            | Build system                         |
| Ninja                 | Any              | CMake generator                      |
| HL2SDK CS2            | Match the server | Environment variable `HL2SDKCS2`     |
| Metamod:Source source | Match the server | Environment variable `MMSOURCE_DEV`  |
| protoc                | 3.21.x           | Environment variable `PROTOC`        |
| funchook              | v1.1.3           | CMake FetchContent (or local source) |
| nlohmann/json         | v3.11.3          | CMake FetchContent (or local source) |

## 2. Build Layer 1 (PracLab C# plugin)

```powershell
# Run from the repository root
dotnet build PracLab.csproj -c Release
```

Build output path:

```
bin/Release/net10.0/
├── PracLab.dll                # Main assembly
└── lang/
    ├── zh-CN.json             # Chinese localization (auto-copied at build time)
    └── en.json                # English localization
```

## 3. Build Layer 2 (PracLabReplayEngine C++ plugin)

### 3.1 Prepare the SDKs

Extract HL2SDK CS2, Metamod source, and protoc to `d:\PracLab\deps\`:

```
deps/
├── hl2sdk-cs2/
├── metamod-source/
├── protoc-3.21.8/
│   └── bin/protoc.exe
├── funchook/                 # funchook source (optional; otherwise fetched online)
└── nlohmann_json/            # nlohmann/json source (optional; otherwise fetched online)
```

### 3.2 Configure CMake

```powershell
cd d:\PracLab\replay-engine
.\configure.ps1
```

`configure.ps1` sets up the MSVC environment variables, cleans the old cache, and generates a Release build directory with the Ninja generator. The script hardcodes MSVC and Windows SDK paths — adjust them if your environment differs.

### 3.3 Build

```powershell
.\build.ps1
```

On success, the output is:

```
=== SUCCESS ===
DLL: d:\PracLab\replay-engine\out\build\x64-Release\package\addons\PracLabReplayEngine\bin\win64\PracLabReplayEngine.dll
Size: XXX.XX KB
```

### 3.4 Key Build Constraints

The following constraints stem from engine behavior and project conventions:

- **protoc version**: Is 3.21.x; CMakeLists.txt enforces this.
- **Static runtime**: Windows uses `/MT` (statically linked CRT) so the plugin can be deployed standalone.
- **protoc-generated code**: The `final` keyword in `.pb.h` classes is automatically stripped to allow `PlayerCommand` to inherit `CSGOUserCmdPB`.
- **gamedata.json**: Contains engine function signatures; may need rescanning after a CS2 update. See [`replay-engine/configs/addons/PracLabReplayEngine/gamedata.json`](../../replay-engine/configs/addons/PracLabReplayEngine/gamedata.json).

## 4. Building the HUD VPK Resource

The in-game HUD panels are built from the VXML/VCSS sources in [`hud/`](../../hud/) via `tools/build_hud_resources.ps1` into `dist/prac_hud.vpk`.

### 4.1 Build Dependencies

| Dependency                                                                       | Description                                                                      |
| -------------------------------------------------------------------------------- | -------------------------------------------------------------------------------- |
| [VPKEdit CLI](https://github.com/craftablescience/VPKEdit) (`vpkeditcli.exe`)     | Packs the VPK; download from release into `tools/vpkedit/` (gitignored)          |
| CS2 `resourcecompiler.exe`                                                        | Requires a CS2 client install with the free "Counter-Strike 2 Workshop Tools" DLC |

### 4.2 Build Command

```powershell
# Validate sources only (no compiler invocation)
.\tools\build_hud_resources.ps1 -Action Validate

# Full build (Validate + Compile + Pack); -Cs2Root must point to a CS2 client install with Workshop Tools
.\tools\build_hud_resources.ps1 -Action Build `
    -Cs2Root "D:\Steam\steamapps\common\Counter-Strike Global Offensive" `
    -ResourceCompiler "<client>\game\bin\win64\resourcecompiler.exe" `
    -VpkEditCli "<path>\vpkeditcli.exe"
```

Output: `dist/prac_hud.vpk` (deployment: see the "Practice HUD Resource" section in the [installation guide](installation.md)).

> **Commit convention for the artifact**: `dist/prac_hud.vpk` is distributed via the repository (the gitignore only exempts this file) and is attached to Release assets by CI. After changing `hud/` sources you must rebuild locally and commit the new vpk — CI has no compiler and cannot detect a stale vpk (the `validate-hud` job only validates source protocol).

### 4.3 Key Constraints

- **`-Cs2Root` must be a CS2 client install**: the server directory lacks the modtools module and resourcecompiler will FATAL.
- **Compiled outputs must carry the `_c` suffix**: only `.vxml_c`/`.vcss_c` may be packed into the runtime VPK; never pack uncompiled sources.
- **The root panel must not carry an `id` attribute** (CCSCustomHudLayout constraint); panel visibility is controlled entirely via the hidden class of child panels.
- **Restricted resource model**: no VJS/HTML/audio — only VXML/VCSS + dialog variables + CSS class toggles; the build script validates the VXML element whitelist and protocol consistency with the C# side (`Commands/PracticeHud/PracticeHudCommands.cs`).
- When changing the VXML/VCSS protocol (panel ids, variable names), update the build script's validation lists and the C# constants in sync.

## 5. Project Structure

```
PracLab/
├── PracLab.cs                # Plugin main class (route table / gating / config loader / shared state)
├── PracLab.csproj            # .NET 10 project file
├── Commands/                 # Feature partial classes (grouped by domain, all in namespace PracLab)
│   ├── Bots/                     # Bots domain
│   │   ├── BotCommands.cs            # .bot / .crouchbot / .kick / .kickall
│   │   └── BotCollisionHandler.cs    # Bot collision management + respawn teleport
│   ├── Nade/                     # Grenade domain (draw / search / results / calibration / rethrow)
│   │   ├── NadeDrawCommands.cs       # .nadedraw / .cleardraw target region drawing
│   │   ├── NadeSearchCommands.cs     # .find* grenade solver searches
│   │   ├── NadeResultCommands.cs     # .nadelist / .nadegoto / .nadeaccuracy result commands
│   │   ├── NadeTestCommands.cs       # .nadetest / .nadetestsim calibration & simulation
│   │   ├── GrenadeInfoCommands.cs    # Grenade info queries
│   │   ├── GrenadeCommands.cs        # .rethrow* / .last rethrow & position rewind
│   │   └── GrenadeFunctions.cs       # Grenade spawn helpers
│   ├── PracticeHud/              # Practice HUD (three panels + spray trace)
│   │   ├── PracticeHudCommands.cs    # Commands / sampling / push / spray trace / lifecycle
│   │   ├── PracticeHudConfig.cs      # hud.cfg loader
│   │   ├── PracticeHudSession.cs     # Per-player session
│   │   ├── CustomHudManager.cs       # CCSCustomHudLayout entity & push manager
│   │   ├── CounterStrafeEngine.cs    # Counter-strafe engine
│   │   ├── ShotStabilityEngine.cs    # Shot stability engine
│   │   ├── AirSyncEngine.cs          # Air sync engine (cs2kz algorithm)
│   │   └── RecoilTraceEngine.cs      # Spray sequence engine
│   ├── Replay/                   # Replay domain
│   │   └── ReplayCommands.cs         # .record / .replay / .clearrecord* / .currentrecord
│   ├── Spawns/                   # Spawns domain
│   │   ├── SpawnCommands.cs          # .spawn family (9 commands)
│   │   └── SpawnMarkerCommands.cs    # .showspawns / .hidespawns + E-key teleport
│   ├── ClearCommands.cs          # .clear / .break
│   ├── ConVarCommands.cs         # .solid / .impacts / .traj
│   ├── DamageHandler.cs          # Real-time damage feedback
│   ├── DryRunCommands.cs         # .dryrun / .restartround
│   ├── EventHandlers.cs          # OnMapStart / OnEntitySpawned / EventPlayerBlind etc.
│   ├── HelpCommand.cs            # .help
│   ├── KeyBindCommands.cs        # F3/F4 key remapping
│   ├── PracticeCommands.cs       # .prac / .map (core gating)
│   ├── TeamCommands.cs           # .watch / .fas
│   ├── TimeGodCommands.cs        # .fastforward / .noflash / .god
│   └── TimerCommand.cs           # .timer
├── cfg/PracLab/              # Server config files
│   ├── config.cfg                # Master toggle + default language
│   ├── hud.cfg                   # Practice HUD tuning parameters
│   ├── prac.cfg                  # Practice-mode ConVars
│   └── dryrun.cfg                # Competitive-mode ConVars
├── hud/                      # HUD VXML/VCSS sources (compiled into prac_hud.vpk)
├── tools/                    # HUD build script (vpkedit/ third-party tools are gitignored)
├── lang/                     # Localization
│   ├── zh-CN.json
│   └── en.json
├── replay-engine/            # Layer 2 C++ Metamod plugin
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

## 6. Cross-Language ABI Contract

Layer 1 (C#) and Layer 2 (C++) communicate via P/Invoke calls to the `PRL_*` family of C functions. The header lives at [`replay-engine/include/praclab_replay.h`](../../replay-engine/include/praclab_replay.h).

When modifying the ABI, both layers must be updated in sync:

| Change type             | Layer 1 (C#)                                              | Layer 2 (C++)                                                                   |
| ---                     | ---                                                       | ---                                                                             |
| Add/modify struct field | `[StructLayout]` class in `Commands/Replay/ReplayCommands.cs`    | `include/praclab_replay.h` + `src/exports.cpp`                                  |
| Add exported function   | `[DllImport]` declaration in `Commands/Replay/ReplayCommands.cs` | Declaration in `include/praclab_replay.h` + implementation in `src/exports.cpp` |
| Reorder fields          | Must sync                                                 | Must sync (affects memory layout)                                               |

## 7. Localization File Maintenance

All player-facing text must be loaded from `lang/*.json`:

- `lang/zh-CN.json` — Chinese (default language)
- `lang/en.json` — English
