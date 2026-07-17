# Chat Commands

PracLab intercepts global chat (`say`) and team chat (`say_team`) to recognize player commands. All commands support both of the following prefix forms, case-insensitive:

| Prefix | Example  |
| --- | ------- |
| `.` | `.prac` |
| `!` | `!prac` |

***

## Command Overview

### Practice Mode Control

| Command | Alias | Description                       |
| ------- | -- | --------------------------------- |
| `.prac` | —  | Load practice config and enable practice mode |

### Map Management

| Command    | Alias | Description                            |
| ---------- | -- | -------------------------------------- |
| `.map`     | —  | Show the list of available maps (visible to sender only) |
| `.inferno` | —  | Switch to de_inferno                   |
| `.mirage`  | —  | Switch to de_mirage                    |
| `.nuke`    | —  | Switch to de_nuke                      |
| `.ancient` | —  | Switch to de_ancient                   |
| `.vertigo` | —  | Switch to de_vertigo                   |
| `.anubis`  | —  | Switch to de_anubis                    |
| `.dust2`   | —  | Switch to de_dust2                     |
| `.train`   | —  | Switch to de_train                     |
| `.cache`   | —  | Switch to de_cache                     |

### Bot Management

| Command      | Alias    | Description                                          |
| ------------ | ------- | ---------------------------------------------------- |
| `.bot`       | —       | Spawn a bot at the player position                    |
| `.crouchbot` | `.cbot` | Spawn a crouching bot at the player position          |
| `.kickall`   | —       | Remove all bots                                       |
| `.kick`      | —       | Remove the bot the player is aiming at               |

### Utility & Environment

| Command  | Alias   | Description                       |
| -------- | ----- | --------------------------------- |
| `.clear` | —     | Remove all grenades/projectiles   |
| `.break` | `.br` | Break all breakable entities      |

### Time & God Mode

| Command        | Alias | Description                                                                   |
| -------------- | ----- | ----------------------------------------------------------------------------- |
| `.fastforward` | `.ff` | Start 10× server time fast-forward for 20 seconds, then auto-revert |
| `.noflash`     | —     | Toggle flashbang immunity for the player (event-driven, other players unaffected) |
| `.god`         | —     | Toggle god mode for the player                                                |

### Spawn Point Teleport

| Command          | Alias    | Description                                  |
| --------------- | ------- | -------------------------------------------- |
| `.spawn <N>`    | `.s`    | Teleport to the N-th spawn of the same team (default N=1) |
| `.ctspawn <N>`  | `.cts`  | Teleport to the N-th CT spawn                |
| `.tspawn <N>`   | `.ts`   | Teleport to the N-th T spawn                 |
| `.bestspawn`    | `.bs`   | Teleport to the nearest own-team spawn       |
| `.worstspawn`   | `.ws`   | Teleport to the farthest own-team spawn      |
| `.bestctspawn`  | `.bcts` | Teleport to the nearest CT spawn             |
| `.worstctspawn` | `.wcts` | Teleport to the farthest CT spawn            |
| `.besttspawn`   | `.bts`  | Teleport to the nearest T spawn              |
| `.worsttspawn`  | `.wts`  | Teleport to the farthest T spawn             |

### Spawn Point Boxes

| Command       | Alias | Description                                                   |
| ------------ | -- | ----------------------------------------------------------- |
| `.showspawns` | —  | Re-display both teams' spawn boxes (auto-shown when prac mode is enabled) |
| `.hidespawns` | —  | Hide spawn boxes                                            |

> **E-key teleport**: Aim at a box and press the use key (default E) to teleport to that spawn; the chat shows "Teleported to spawn N". CT and T boxes are both green; the box center shows an index number (rotated per-map config). In prac mode there is no team restriction — players may aim at any box to teleport. Boxes only show competitive-mode spawns (consistent with the `.spawn` family commands).

### Team Switching

| Command  | Alias   | Description                                                              |
| -------- | ------ | ----------------------------------------------------------------------- |
| `.watch` | `.fas` | Force all other players into spectator mode, leaving only the command issuer active |

### Grenade Rethrow & Position Recall

| Command           | Alias          | Description                                |
| ---------------- | ------------- | ------------------------------------------ |
| `.rethrow`        | `.rt`         | Rethrow the player's last thrown grenade    |
| `.rethrowsmoke`   | `.rethrows`   | Rethrow only the last smoke grenade         |
| `.rethrownade`    | `.rethrown`   | Rethrow only the last HE grenade            |
| `.rethrowflash`   | `.rethrowf`   | Rethrow only the last flashbang             |
| `.rethrowmolotov` | `.rethrowm`   | Rethrow only the last molotov/incgrenade    |
| `.rethrowdecoy`   | `.rethrowd`   | Rethrow only the last decoy                 |
| `.last`           | `.ls`         | Teleport back to the last grenade position  |

### Dryrun & Round Control

| Command         | Alias | Description                                                                  |
| --------------- | ----- | --------------------------------------------------------------------------- |
| `.dryrun`       | `.dry` | Temporarily switch from prac to competitive config for one round; auto-revert to prac when the round ends |
| `.restartround` | `.rr` | Restart the current round (executes `mp_restartgame`)                       |

> **Dryrun flow**: kick all bots → disable prac-specific ConVars (sv_cheats, traj, infinite_ammo, etc.) → load `dryrun.cfg` competitive config → restart the round to begin; when the round ends (`EventRoundEnd`), prac config is automatically restored. Use this to play a real competitive round from within a practice session.

### Replay System

| Command            | Alias            | Description                                                                  |
| ----------------- | ------------- | --------------------------------------------------------------------------- |
| `.record`          | —             | Enter pending-record state; **starts recording when the player moves or presses F (inspect key)**; press F again or use `.stoprecord` to stop and save |
| `.stoprecord`      | —             | Stop recording and save (equivalent to pressing F)                          |
| `.replay [Id]`     | —             | No argument: play back all stopped recordings in parallel; with Id: play back only that recording. A bot of the same team is auto-spawned as the playback carrier |
| `.stopreplay`      | —             | Stop all ongoing playbacks and kick the corresponding bots                  |
| `.clearrecord <Id>` | —             | Delete the recording with the given Id (removes both JSON file and list entry) |
| `.clearrecordall`  | —             | Clear all recordings                                                         |
| `.currentrecord`   | `.currentrec` | Print the recording list table (ID / Name / Status / Bot) to the **server console** |

> **Two-layer architecture**: The replay system consists of two parts —
> - **Layer 1 (CSSharp C#)**: command dispatch, UI prompts, JSON read/write for recording files (under `cfg/PracLab/recordings/`).
> - **Layer 2 (C++ Metamod plugin `PracLabReplayEngine`)**: hooks engine functions (`ProcessMovement`, `CCSBot::Update`, `PlayerRunCommand`, etc.) to implement frame-level movement recording and playback.
>
> The cross-language ABI contract is documented in [`replay-engine/include/praclab_replay.h`](../../../replay-engine/include/praclab_replay.h). Any change to field order/type must be synchronized across both layers.
>
> **When the replay engine is not deployed**: all `.record/.replay/...` commands show a "replay engine not loaded, replay unavailable" message to the player, but other plugin features are unaffected.

### Timer

| Command  | Alias | Description                                            |
| -------- | -- | ------------------------------------------------------ |
| `.timer` | —  | Start timing; entering `.timer` again stops it and prints the elapsed time to the player |

### ConVar Toggles

| Command    | Alias | Description                                              |
| --------- | -- | ------------------------------------------------------- |
| `.solid`   | —  | Toggle `mp_solid_teammates` (teammate collision)         |
| `.impacts` | —  | Toggle `sv_showimpacts` (show bullet impact markers)     |
| `.traj`    | —  | Toggle `sv_grenade_trajectory_prac_pipreview` (show grenade trajectory) |

### Help

| Command | Alias | Description                                                |
| ------- | -- | --------------------------------------------------------- |
| `.help` | —  | Display all available commands grouped by category in chat (visible to sender only) |

***
