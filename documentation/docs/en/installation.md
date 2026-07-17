# Installation

PracLab consists of two independent layers that can be deployed on demand:

- **Layer 1 (required)**: `PracLab.dll` — the CounterStrikeSharp C# plugin that provides all commands and UI.
- **Layer 2 (optional)**: `PracLabReplayEngine.dll/.so` — the Metamod C++ plugin that provides frame-level movement recording/playback. When not deployed, only the `.record/.replay` family commands are unavailable; other features are unaffected.

> Build artifacts can be downloaded from the Release page, or compiled from source — see the [Developers](developers.md) guide.

## 1. Prerequisites

| Dependency | Version |
| --- | --- |
| CS2 server | Latest |
| [CounterStrikeSharp](https://github.com/roflmuffin/CounterStrikeSharp) | 1.0.371+ |
| [Metamod:Source](https://www.sourcemm.net/) | Latest stable |

## 2. Deploy Layer 1 to the CS2 server

Assume the CS2 server root directory is `<CS2>` (the directory that contains `csgo/`):

### 2.1 Copy plugin files

```
<CS2>/csgo/addons/counterstrikesharp/plugins/PracLab/
├── PracLab.dll
└── lang/
    ├── zh-CN.json
    └── en.json
```

### 2.2 Copy config files

```
<CS2>/csgo/cfg/PracLab/
├── config.cfg                 # Master toggle + default language
├── prac.cfg                   # Practice-mode ConVars
└── dryrun.cfg                 # Competitive-mode ConVars (used by .dryrun)
```

The repository's `cfg/PracLab/` directory already contains default configs — simply copy the whole directory.

### 2.3 Start the server

On startup, the console should show:

```
[PracLab] Load: executing...
[PracLab] HH:mm:ss Core default language: zh-CN, plugin enabled: True (hotReload: False)
[PracLab] HH:mm:ss Core registered 9 map change commands, route table has XX keys
```

Players can type `.prac` in chat to enable practice mode.

## 3. Deploy Layer 2 to the CS2 server

### 3.1 Copy the shared library

**Windows**:

```
<CS2>/csgo/addons/PracLabReplayEngine/bin/win64/PracLabReplayEngine.dll
```

**Linux**:

```
<CS2>/csgo/addons/PracLabReplayEngine/bin/linuxsteamrt64/PracLabReplayEngine.so
```

### 3.2 Copy the Metamod VDF and gamedata

The repository's [`replay-engine/configs/`](../../replay-engine/configs/) directory provides ready-made files:

```
<CS2>/csgo/addons/metamod/PracLabReplayEngine.vdf         # Windows
<CS2>/csgo/addons/metamod/PracLabReplayEngine.linux.vdf   # Linux (rename to .vdf)
<CS2>/csgo/addons/PracLabReplayEngine/gamedata.json       # Signature scan config
```

### 3.3 Verify

Start the server; the console should show:

```
[PracLab] HH:mm:ss Replay engine detected: PracLabReplayEngine loaded
```

Or run `.currentrecord` in the PracLab console — if it says "replay engine not loaded", Layer 2 is not properly deployed.

## 4. Upgrade & Rollback

| Operation | Steps |
| --- | --- |
| Upgrade Layer 1 | Overwrite `PracLab.dll` and `lang/*.json` etc. |
| Upgrade Layer 2 | Stop the server → overwrite `PracLabReplayEngine.dll/.so` → start the server (Metamod plugins do not support hot reload) |
| Rollback | Replace with the older files; config files are backward compatible |
