# Configuration

PracLab's config files all live under the server's `<CS2>/game/csgo/cfg/PracLab/` directory. The repository's [`cfg/PracLab/`](../../cfg/PracLab/) provides default templates. After editing, restart the plugin or the server for changes to take effect.

## 1. `config.cfg` — Master Toggle

**Path**: `<CS2>/game/csgo/cfg/PracLab/config.cfg`
**Format**: One ConVar per line; `key value` separated by a space; `//` starts a comment.

| ConVar                     | Type   | Default | Description                                                                                                                                 |
| ---                        | ---    | ---     | ---                                                                                                                                         |
| `praclab_enabled`          | bool   | `true`  | Master toggle. When `false`, no command (including `.prac`, `.map`, map switches) responds; the sender sees "practice mode is not enabled". |
| `praclab_default_language` | string | `zh-CN` | Server default language code. Options: `zh-CN` / `en`.                                                                                      |

## 2. Recordings Directory

**Path**: `<CS2>/game/csgo/cfg/PracLab/recordings/`

JSON recording files saved by `.record` are stored here, with the filename pattern `<Id>_<PlayerName>.json`. `.clearrecord <Id>` and `.clearrecordall` delete both the file and the in-memory entry.

**Auto-created**: The plugin's `Load` calls `EnsureRecordingsDir()` to ensure the directory exists; no manual setup is required.

**Cleared on map change**: The `OnMapStart` listener clears the in-memory recording list (`_recordings.Clear()`), but **does not delete the JSON files**. To preserve recordings across maps, record the Ids via `.currentrecord` before switching maps.

## 3. Spawn Box Per-Map Configuration

Each map's box Z offset and index-text yaw rotation is hardcoded in [`Commands/SpawnMarkerCommands.cs`](../../Commands/SpawnMarkerCommands.cs):

- `SpawnTextYawByMap`: yaw rotation (degrees) of the index text per map per team
- `SpawnExtraZOffsetByMap`: additional Z raise for the box/text per map

**Current configuration**:

| Map         | T yaw | CT yaw | Extra Z offset |
| ---         | ---   | ---    | ---            |
| de\_cache   | 90    | 180    | 0              |
| de\_train   | -90   | 0      | 0              |
| de\_anubis  | 0     | 180    | 0              |
| de\_vertigo | 90    | 180    | 0              |
| de\_ancient | 0     | 180    | 16             |
| de\_nuke    | -90   | 90     | 0              |
| de\_mirage  | 90    | -90    | 0              |
| de\_inferno | -90   | 90     | 0              |

Unconfigured maps default to yaw=0 and Z offset=0. When adding support for a new map, extend these tables and rebuild.

## 4. `hud.cfg` — Practice HUD Tuning Parameters

**Path**: `<CS2>/game/csgo/cfg/PracLab/hud.cfg`
**Format**: One ConVar per line; `key value` separated by a space; `//` starts a comment.

**Tick granularity**: all time thresholds are defined in ticks — 1 tick ≈ 15.6ms on a 64-tick server, ≈ 7.8ms on 128-tick. Judgment scales automatically with the server tickrate; no millisecond conversion is needed.

| ConVar                            | Type  | Default | Description                                                                                                                                                              |
| ---                               | ---   | ---     | ---                                                                                                                                                                      |
| `strafe_max_diff_ticks`           | int   | `8`     | Counter-strafe: same-axis key switches (A/D, W/S) with a diff beyond this many ticks are discarded (no record)                                                           |
| `strafe_perfect_ticks`            | int   | `0`     | Counter-strafe: a switch diff with absolute value ≤ this many ticks is judged "Perfect" (0 = switch completed within the same tick)                                      |
| `strafe_success_ticks`            | int   | `1`     | Counter-strafe: a switch diff with absolute value ≤ this many ticks is judged "Excellent"                                                                                |
| `strafe_debounce_ticks`           | int   | `4`     | Counter-strafe: debounce — minimum interval in ticks between records, preventing one switch from counting twice                                                          |
| `strafe_history_limit`            | int   | `50`    | Counter-strafe: record history cap (used for avg diff / success rate / std dev / tendency stats)                                                                         |
| `shot_history_limit`              | int   | `20`    | Shot stability: sample history cap                                                                                                                                       |
| `shot_grace_ticks`                | int   | `12`    | Shot stability: movement-start grace — moving right after pressing a movement key is not judged as run-and-gun                                                           |
| `shot_crouch_release_grace_ticks` | int   | `3`     | Shot stability: crouch-release grace — crouch accuracy still applies briefly after releasing the crouch key                                                              |
| `shot_crouch_exit_ramp_ticks`     | int   | `6`     | Shot stability: crouch-exit ramp — the error recovers linearly over this many ticks after releasing crouch                                                               |
| `shot_success_error_threshold`    | float | `0.35`  | Shot stability: success error threshold (normalized speed_ratio error ≤ this value counts as stable, 0.05–1.0)                                                           |
| `shot_weapon_thresholds_enabled`  | bool  | `true`  | Shot stability: enable the built-in weapon accuracy threshold table (lower thresholds for AWP/snipers etc.; `false` falls back to the default threshold for all weapons) |
| `shot_default_threshold`          | float | `250`   | Shot stability: default accuracy threshold for unlisted weapons (units/s; speed_ratio = horizontal speed / threshold)                                                    |
| `sync_history_limit`              | int   | `30`    | Air sync: cap on recent air segments (used for average/best stats)                                                                                                       |
| `recoil_reset_ticks`              | int   | `64`    | Spray trace: reset the fire sequence when shots are spaced further apart than this many ticks                                                                           |
| `recoil_max_shots_per_sequence`   | int   | `30`    | Spray trace: max recorded shots per fire sequence (overflow guard for full-auto)                                                                                        |
| `recoil_beam_enabled`             | bool  | `true`  | Spray trace: draw the mouse spray-control path as a world-space beam polyline                                                                                           |
| `recoil_trace_scale`              | float | `2.5`   | Spray trace: trace scale (world units per degree of view-angle change)                                                                                                  |
| `recoil_trace_distance`           | float | `80`    | Spray trace: distance from the player's eye to the trace board (world units)                                                                                            |
| `recoil_trace_max_segments`       | int   | `256`   | Spray trace: max beam segments per trace (overflow guard)                                                                                                               |
| `hud_push_interval_ticks`         | int   | `2`     | HUD panel text push interval in ticks (delta-push only on change)                                                                                                       |

**Built-in weapon accuracy threshold table**: active when `shot_weapon_thresholds_enabled` is on (units/s); unlisted weapons fall back to `shot_default_threshold`:

| Weapon          | Threshold | Weapon                 | Threshold |
| ---             | ---       | ---                    | ---       |
| `weapon_awp`    | 100       | `weapon_galil`         | 180       |
| `weapon_ssg08`  | 120       | `weapon_famas`         | 180       |
| `weapon_g3sg1`  | 130       | `weapon_aug`           | 180       |
| `weapon_scar20` | 130       | `weapon_sg556`         | 180       |
| `weapon_ak47`   | 140       | `weapon_deagle`        | 220       |
| `weapon_m4a1`   | 150       | `weapon_m4a1_silencer` | 150       |

Out-of-range values print a warning and fall back to defaults; when `hud.cfg` is missing, all defaults are used.
