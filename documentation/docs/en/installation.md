# Installation

PracLab consists of two independent layers that can be deployed on demand:

- **Layer 1 (required)**: `PracLab.dll` — the CounterStrikeSharp C# plugin that provides all commands and UI.
- **Layer 2 (optional)**: `PracLabReplayEngine.dll/.so` — the Metamod C++ plugin that provides frame-level movement recording/playback. When not deployed, only the `.record/.replay` family commands are unavailable; other features are unaffected.

> Build artifacts can be downloaded from the Release page, or compiled from source — see the [Developers](developers.md) guide.

## 1. Prerequisites

| Dependency                                                             |
| ---                                                                    |
| CS2 server                                                             |
| [CounterStrikeSharp](https://github.com/roflmuffin/CounterStrikeSharp) |
| [Metamod:Source](https://www.sourcemm.net/)                            |

## 2. Deploy Metamod:Source and CounterStrikeSharp

The two layers of PracLab rely on these frameworks respectively: Layer 2 (the C++ engine plugin) is loaded by [Metamod:Source](https://www.sourcemm.net/), and Layer 1 (the C# plugin) runs on [CounterStrikeSharp](https://github.com/roflmuffin/CounterStrikeSharp). If they are not installed yet, follow the official guide first:

- [CounterStrikeSharp Getting Started](https://docs.cssharp.dev/docs/guides/getting-started.html)

After deployment, run `meta list` in the server console — CounterStrikeSharp should appear in the output.

## 3. Deploy Layer 1 to the CS2 server

Assume the CS2 server root directory is `<CS2>`:

### 3.1 Copy plugin files

```
<CS2>/game/csgo/addons/counterstrikesharp/plugins/PracLab/
├── PracLab.dll
└── lang/
    ├── zh-CN.json
    └── en.json
```

### 3.2 Copy config files

```
<CS2>/game/csgo/cfg/PracLab/
├── config.cfg                 # Plugin config
├── prac.cfg                   # Practice-mode config
└── dryrun.cfg                 # Competitive-mode config
```

The repository's `cfg/PracLab/` directory already contains default configs — simply copy the whole directory.

### 3.3 Start the server

On startup, the console should show:

```
[PracLab] Load: executing...
[PracLab] HH:mm:ss Core default language: zh-CN, plugin enabled: True (hotReload: False)
[PracLab] HH:mm:ss Core registered 9 map change commands, route table has XX keys
```

Players can type `.prac` in chat to enable practice mode.

## 4. Deploy Layer 2 to the CS2 server

### 4.1 Download the Release package and extract

Download `PracLabReplayEngine.zip` from [GitHub Releases](https://github.com/MEngYangX/PracLab/releases/latest) and extract it into `<CS2>/game/csgo/` (the archive already contains the `addons/` directory structure):

```
<CS2>/game/csgo/addons/PracLabReplayEngine/
├── gamedata.json                               # Signature scan config
└── bin/
    ├── win64/PracLabReplayEngine.dll           # Windows
    └── linuxsteamrt64/PracLabReplayEngine.so   # Linux
<CS2>/game/csgo/addons/metamod/
├── PracLabReplayEngine.vdf                     # Windows
└── PracLabReplayEngine.linux.vdf               # Linux
```

### 4.2 Remove the VDF for the other platform

The archive ships VDFs for both platforms, and Metamod loads every `*.vdf` under `addons/metamod/` — a wrong VDF causes load errors. Delete the one that does not match your platform:

- **Windows**: keep `PracLabReplayEngine.vdf`, delete `PracLabReplayEngine.linux.vdf`
- **Linux**: keep `PracLabReplayEngine.linux.vdf`, delete `PracLabReplayEngine.vdf`

### 4.3 Verify

Start the server; the console should show:

```
[PracLab] HH:mm:ss Replay engine detected: PracLabReplayEngine loaded
```

Or run `.currentrecord` in the PracLab console — if it says "replay engine not loaded", Layer 2 is not properly deployed.

## 5. Practice HUD Resource (Optional)

The in-game HUD panels (`.strafe` / `.shot` / `.sync`) require the client to load the VPK resource `prac_hud.vpk` (the `.recoil` spray trace is drawn in world space and does not depend on this resource). Its sources live in the repository's [`hud/`](../../hud/) directory; see the [developer guide](developers.md) for how to build it.

**Without this resource, plugin-side functionality is unaffected**: judgment and statistics still work server-side (commands toggle normally, stats keep accumulating) — players simply don't see the HUD panels.

Two distribution paths:

**① Steam Workshop (recommended for public servers)**: publish `prac_hud.vpk` to the Steam Workshop; players who subscribe load it automatically, no manual steps needed.

**② Local overrides (local dev / self-hosted servers)**: copy `prac_hud.vpk` into `<CS2>/game/csgo/overrides/` on both the client and the server, and add the following line to both `gameinfo.gi` files (above the `Game csgo` line):

```
Game  csgo/overrides/prac_hud.vpk
```

Restart the client and the server for the change to take effect.

## 6. Upgrade & Rollback

| Operation       | Steps                                                                                                                    |
| ---             | ---                                                                                                                      |
| Upgrade Layer 1 | Overwrite `PracLab.dll` and `lang/*.json` etc.                                                                           |
| Upgrade Layer 2 | Stop the server → overwrite `PracLabReplayEngine.dll/.so` → start the server (Metamod plugins do not support hot reload) |
| Rollback        | Replace with the older files; config files are backward compatible                                                       |
