# Changelog

All notable changes to this project will be documented in this file.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.1.0/), and this project adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

## [Unreleased]

### Added

- **Practice HUD**: new in-game practice HUD with independent toggle commands — counter-strafe assessment `.strafe` (same-axis movement-key switch timing with statistics), shot stability `.shot` (speed error and stable rate at fire time), air strafe sync `.sync` (sync-rate algorithm ported from cs2kz jumpstats), spray trace `.recoil` (draws the mouse-movement path as a world-space green beam polyline while the fire button is held; stops on release; auto-resets on reload/weapon switch/pause), and `.hudreset` to clear all HUD statistics. All require practice mode (`.prac`).
- **`hud.cfg` practice HUD configuration**: 20 tuning parameters (tick-granularity thresholds, debounce, history caps, weapon accuracy threshold table toggle, push throttling), shipped with the plugin.
- **HUD VPK build script**: `tools/build_hud_resources.ps1` compiles the `hud/` panel sources with the CS2 resourcecompiler and packs them into `prac_hud.vpk` via VPKEdit. The client must install this VPK resource (Workshop subscription or local `game/csgo/overrides/`) to see the `.strafe`/`.shot`/`.sync` panels (the spray trace is drawn in world space and does not depend on the VPK); without it, judgment and statistics still work server-side.
- **`.hudmove` panel moving**: edit mode that releases the mouse cursor and lets players move the HUD panels — click a panel to select it, click a slot in the 3x3 on-screen grid to place it at one of nine screen positions; a taken slot swaps the two panels so panels never overlap. Exits via Tab or `.hudmove` again. Positions are per-player, session-scoped. Built on the CSSharp `OnCustomHudClicked` listener and `SetInputCaptureEnabled` (no signature scanning).

### Fixed

- **HUD panel titles were not localized**: the three panel titles were hardcoded in Chinese inside the VXML; they are now pushed per player as dialog variables from the language files, so English clients see English titles.
- **Config path resolution**: plugin paths were resolved against the game root, so `LoadConfig` auto-created a default (zh-CN) `config.cfg` under `game/cfg/PracLab/` on startup, shadowing the real `game/csgo/cfg/PracLab/` copy. `LoadConfig`, `hud.cfg` loading and the recordings dir now share one resolver that prefers the `csgo/` layout.
- **ReplayEngine DLL preload path**: adapted to the CSSharp v1.0.372 `GameDirectory` path change with dual-path probing, [Issue #5](https://github.com/MEngYangX/PracLab/issues/5).

### Changed

- **CSSharp dependency upgrade**: 1.0.372 → 1.0.375, see [v1.0.375](https://github.com/roflmuffin/CounterStrikeSharp/releases/tag/v1.0.375).
- **Replay engine updated to BotController v0.6.3**: adds dropped-weapon recording/playback and projectile spawn alignment modules, ABI 18 → 20.

## [0.3.0] - 2026-08-19

### Fixed

- **`.clearrecord` usage hint HTML escaping**: entering `.clearrecord` (no argument) showed `.clearrecord &lt;Id&gt;` instead of `.clearrecord <Id>` in chat. Root cause: the `record.usage` values in `lang/zh-CN.json` and `lang/en.json` used HTML entities (`&lt;`/`&gt;`), which the CounterStrikeSharp chat does not parse (color placeholders use the `{colorname}` format), so they were printed literally. Fixed to plain angle brackets; scanned all lang files and this was the only occurrence.

### Changed

- **Migrated to the native CSSharp Trace API**: removed the CS2TraceRay NuGet dependency and `CS2TraceRay.gamedata.json`, fully switching to the `Trace.TraceShape` / `Trace.TraceEndShape` native APIs added in v1.0.372. Affected ray call sites in `NadeDrawCommands`, `NadeSearchCommands`, `NadeResultCommands` and `NadeTestCommands`, unified on the `Trace` / `TraceResult` / `Contents` / `Masks` types from `CounterStrikeSharp.API.Modules.Utils`; `SkipPawn` parameter type changed from `nint` to `CBaseEntity?`, and `ToCssVector` / `ToSystemVector` were added for conversion between `CounterStrikeSharp.API.Modules.Utils.Vector` and `System.Numerics.Vector3`. Also simplified `build.yml` (removed the gamedata.json copy step) and removed the CS2TraceRay credit from the README. Eliminates the stability risk of Linux segfaults caused by signature breakage from CS2 game updates.
- **CSSharp dependency upgrade**: `CounterStrikeSharp.API` in `PracLab.csproj` bumped from 1.0.371 to 1.0.372. v1.0.372 mainly adds the Ray/Hull Trace API (INavPhysicsInterface, PR #1331) and updates Schema Definitions to 1.41.6.9 (PR #1356).
- **Replay engine updated to BotController v0.6.1**: synced upstream [CS2-Bot-Controller](https://github.com/XBribo/CS2-Bot-Controller) v0.6.1 (ABI 17→18). Adds the `SuppressUsercmd` export (suppresses usercmd buttons for a duration; not currently used by PracLab but kept for ABI parity); `InputInjector` gains the `UsercmdSuppression` struct and the `ApplyUsercmdSuppressions` handling path; `ClearUsercmdInjections` and `Remove()` clean up suppressions as well. Updated `praclab_replay.h`, `exports.cpp`, plugin.h version and comments.

## [0.2.1] - 2026-08-06

### Fixed

- **`.last` / `.ls` teleport issues**: teleporting back to the last grenade-throw position had three problems — the spawn point was floating, the view was tilted (roll), and the player model flipped over. Three root causes: ① the recorded position was the projectile `AbsOrigin` (above and in front of the player's eyes) instead of the player's feet; ② the recorded angles came from the projectile `AbsRotation` (which includes roll in flight) instead of the player's view; ③ setting angles via `pawn.Teleport` writes pitch into `CGameSceneNode.m_angRotation` (the body-rotation source), flipping the model. Fix: `GrenadeThrowRecord` now stores the player's foot position (`PlayerPawn.AbsOrigin`) and the player's aim view (`EyeAngles`); teleportation uses the `setpos` + `setang` client commands (position and eye angles only, never writing `m_angRotation`), keeping the body upright. The `.rethrow` family is unaffected (still reproduces trajectories from projectile data).

## [0.2.0] - 2026-08-06

### Added

- **Grenade inverse-search system**: a complete trajectory inverse-solving toolchain supporting 6 item types (smoke/flash/he/molo/inc/decoy) × 7 throw modes × 3 strength tiers of grid search, with 3 accuracy tiers (low 4° / mid 2°+refined / high 1°+refined), executed with a millisecond budget amortized per tick so the server never stalls.
  - **Target region drawing** (`.nadedraw` / `.ndr`): three-step box drawing (bottom corner → bottom diagonal → height point), left-click to confirm / right-click to cancel, red preview / green confirmed wireframe, air points auto-projected to the ground, up to 8 regions.
  - **Search commands**: `.findall` (`.fa`), `.findnormal` (`.fn`), `.findjump` (`.fj`), `.findrunjump` (`.frj`), `.findduck` (`.fd`), `.findduckjump` (`.fdj`), `.findduckrunjump` (`.fdrj`); every mode supports left/mid/right strength subcommands (e.g. `.fnl`/`.fnm`/`.fnr`).
  - **Accuracy/type toggles**: `.nadeaccuracy` (`.nac`), `.nadetype` (`.nt`).
  - **Throw calibration**: `.nadetest` (`.ntt`) — register, throw within 30 seconds, and the server console prints real vs. predicted comparison data with auto-detected strength/mode.
  - **Single-shot simulation debug**: `.nadetestsim` (`.nts`) — given `yaw pitch mode strength`, runs one trajectory simulation and prints spawn origin/collision log/landing point/stop reason/flight time to the console.
  - **Result management**: `.nadelist` (`.nl`) prints the result table to the console, `.nadeclearlist` (`.ncl`) clears results and their visualization entities, `.nadegoto` (`.ng`) teleports to a result's standing position with trajectory preview.
- **Grenade data feedback**: after a grenade lands, shows flight time and bounce count; when a flashbang blinds a target, shows the blind duration.
- **F3 key binding**: pressing F3 (autobuy key) starts the `.timer`; pressing it again stops.
- **F4 key binding**: pressing F4 (rebuy key) shows the `.help` command list.
- **`.clear` alias `.cl`**.

### Fixed

- **Bot team attribution**: killing a bot after switching to the bot's team incorrectly reported "you killed a teammate". Bots now belong to no team, eliminating false positives.
- **CS2TraceRay Linux signature breakage**: the 2026-07 game update made the `GameTraceManager` Linux signature (`4C 8D 3D ? ? ? ? 48 8B 80 18 02 00 00`) match 19 sites, all pointing to .text-section RTTI/vtable addresses whose dereference segfaulted. A unique matching signature `48 8D 0D ? ? ? ? F3 41 0F 10 4F 08` (LEA rcx,[rip+disp] loading a .data-section global) was re-extracted from `libserver.so` and verified against the TraceFunc call chain via disassembly.

### Changed

- **Unified output format**: console, chat and terminal output unified into the `[PracLab] {Time} {Module} {Message}` structured log format.
- **Debug output cleanup**: removed all leftover debug/DBG output.
- **Replay engine update**: replay-engine synced to the latest upstream [CS2-Bot-Controller](https://github.com/XBribo/CS2-Bot-Controller) version, ensuring both Linux and Windows platforms work.

## [0.1.2] - 2026-07-20

### Fixed

- **`.rethrow` broken on Linux**: the 2026-07 game update invalidated the Linux Create function signatures wholesale — the old `CHEGrenadeProjectile::Create` / `CDecoyProjectile::Create` signatures failed and the fallback path created fuseless "empty shell" entities; the old `CSmokeGrenadeProjectile::Create` signature matched 2 functions and the first hit was the flashbang Create (0xd16ed0), making `.rethrowsmoke` actually throw a flashbang. Unique matching signatures were re-extracted from `libserver.so` (2026-07-16, ServerVersion 2000876): smoke at 0x1408d80, HE at 0xd17860, decoy at 0x1407fe0.

## [0.1.1] - 2026-07-19

### Fixed

- **Decoy/HE `.rethrow` errors**: the 2026-07 game update invalidated the Windows function signatures of `CHEGrenadeProjectile::Create` (HE) and `CDecoyProjectile::Create`; the fallback path created fuseless "empty shell" entities missing physical damping. Unique function-prologue signatures were re-extracted from `server.dll` (2026-07-17): the HE stack frame `sub rsp,0x50` → `0x40` (function at 0x1803896a0) and the decoy frame `0x168` → `0x158` (function at 0x1809567b0). Also removed the HE fallback `AcceptInput("Detonate")` timer proven ineffective.
- **Spawn label orientation on de_dust2 (CT)**: the CT spawn-box label had to be rotated 180° relative to the T side to face players. `SpawnTextYawByMap` now configures a `180f` rotation for `de_dust2` CT.

## [0.1.0] - 2026-07-17

### Added

- **Practice mode**: one-click practice config loading (`.prac`) with infinite ammo, full armor, trajectory lines, instant buy, etc.
- **Map management**: 9 quick map switches (`.inferno`, `.mirage`, `.nuke`, `.ancient`, `.vertigo`, `.anubis`, `.dust2`, `.train`, `.cache`)
- **Bot system**: spawn a bot at the player position (`.bot`/`.crouchbot`), crosshair-targeted kick (`.kick`/`.kickall`), automatic collision management
- **Spawn point system**: 9 teleport commands + box visualization with labels (`.showspawns`/`.hidespawns`) + E-key aim teleport
- **Grenade rethrow**: 7 commands to rethrow the last grenade of any type (`.rethrow`/`.rethrowflash`/`.rethrowsmoke`/`.rethrowhe`/`.rethrowdecoy`/`.rethrowmolotov`) + return to the throw position (`.last`)
- **Dryrun mode**: temporarily switch to competitive config for one round (`.dryrun`/`.dry`), round restart (`.restartround`/`.rr`)
- **Replay system**: record player movement trajectories and reproduce them with a bot (`.record`/`.stoprecord`/`.replay`/`.stopreplay`/`.clearrecord`/`.clearrecordall`/`.currentrecord`)
- **Time/invincibility**: 10× time fast-forward (`.fastforward`), flash immunity (`.noflash`), god mode (`.god`)
- **ConVar toggles**: `.solid`, `.impacts`, `.traj`
- **Localization**: Chinese (zh-CN, default) and English (en); all player-visible text loaded from localization files
- **Two-layer architecture**: C# plugin (Layer 1) + C++ Metamod replay engine (Layer 2), communicating via cross-language P/Invoke
- **Spawn boxes**: per-map box Z offset and label yaw rotation are hardcoded

[Unreleased]: https://github.com/MEngYangX/PracLab/compare/0.3.1...HEAD
[0.3.1]: https://github.com/MEngYangX/PracLab/compare/0.3.0...0.3.1
[0.3.0]: https://github.com/MEngYangX/PracLab/compare/0.2.1...0.3.0
[0.2.1]: https://github.com/MEngYangX/PracLab/compare/0.2.0...0.2.1
[0.2.0]: https://github.com/MEngYangX/PracLab/compare/0.1.2...0.2.0
[0.1.2]: https://github.com/MEngYangX/PracLab/compare/0.1.1...0.1.2
[0.1.1]: https://github.com/MEngYangX/PracLab/compare/0.1.0...0.1.1
[0.1.0]: https://github.com/MEngYangX/PracLab/releases/tag/0.1.0
