# Chat Commands

PracLab intercepts global chat (`say`) and team chat (`say_team`) to recognize player commands. All commands support both of the following prefix forms, case-insensitive:

| Prefix | Example |
| ---    | ------- |
| `.`    | `.prac` |
| `!`    | `!prac` |

***

## Command Overview

### Practice Mode Control

| Command | Alias | Description                                   |
| ------- | --    | ---------------------------------             |
| `.prac` | —     | Load practice config and enable practice mode |

### Map Management

| Command    | Alias | Description                                              |
| ---------- | --    | --------------------------------------                   |
| `.map`     | —     | Show the list of available maps (visible to sender only) |
| `.inferno` | —     | Switch to de_inferno                                     |
| `.mirage`  | —     | Switch to de_mirage                                      |
| `.nuke`    | —     | Switch to de_nuke                                        |
| `.ancient` | —     | Switch to de_ancient                                     |
| `.vertigo` | —     | Switch to de_vertigo                                     |
| `.anubis`  | —     | Switch to de_anubis                                      |
| `.dust2`   | —     | Switch to de_dust2                                       |
| `.train`   | —     | Switch to de_train                                       |
| `.cache`   | —     | Switch to de_cache                                       |

### Bot Management

| Command      | Alias   | Description                                          |
| ------------ | ------- | ---------------------------------------------------- |
| `.bot`       | —       | Spawn a bot at the player position                   |
| `.crouchbot` | `.cbot` | Spawn a crouching bot at the player position         |
| `.kickall`   | —       | Remove all bots                                      |
| `.kick`      | —       | Remove the bot the player is aiming at               |

### Utility & Environment

| Command  | Alias | Description                       |
| -------- | ----- | --------------------------------- |
| `.clear` | `.cl` | Remove all grenades/projectiles   |
| `.break` | `.br` | Break all breakable entities      |

### Time & God Mode

| Command        | Alias | Description                                                                   |
| -------------- | ----- | ----------------------------------------------------------------------------- |
| `.fastforward` | `.ff` | Start 10× server time fast-forward for 20 seconds, then auto-revert           |
| `.noflash`     | —     | Toggle flashbang immunity for the player                                      |
| `.god`         | —     | Toggle god mode for the player                                                |

### Spawn Point Teleport

| Command         | Alias   | Description                                               |
| --------------- | ------- | --------------------------------------------              |
| `.spawn <N>`    | `.s`    | Teleport to the N-th spawn of the same team (default N=1) |
| `.ctspawn <N>`  | `.cts`  | Teleport to the N-th CT spawn                             |
| `.tspawn <N>`   | `.ts`   | Teleport to the N-th T spawn                              |
| `.bestspawn`    | `.bs`   | Teleport to the nearest own-team spawn                    |
| `.worstspawn`   | `.ws`   | Teleport to the farthest own-team spawn                   |
| `.bestctspawn`  | `.bcts` | Teleport to the nearest CT spawn                          |
| `.worstctspawn` | `.wcts` | Teleport to the farthest CT spawn                         |
| `.besttspawn`   | `.bts`  | Teleport to the nearest T spawn                           |
| `.worsttspawn`  | `.wts`  | Teleport to the farthest T spawn                          |

### Spawn Point Boxes

| Command       | Alias | Description                                                               |
| ------------  | --    | -----------------------------------------------------------               |
| `.showspawns` | —     | Re-display both teams' spawn boxes (auto-shown when prac mode is enabled) |
| `.hidespawns` | —     | Hide spawn boxes                                                          |

> **E-key teleport**: Aim at a box and press the use key (default E) to teleport to that spawn; the chat shows "Teleported to spawn N". CT and T boxes are both green; the box center shows an index number (rotated per-map config). In prac mode there is no team restriction — players may aim at any box to teleport. Boxes only show competitive-mode spawns (consistent with the `.spawn` family commands).

### Target Area Drawing

| Command      | Alias  | Description                                                                                                                                                                                   |
| ------------ | ------ | ----------------------------------------------------------------------------------------------------------------------------                                                                  |
| `.nadedraw`  | `.ndr` | Three-step box target region drawing (left-click to confirm base corner / base diagonal corner / height point, right-click to cancel; red preview / green confirmed wireframe, max 8 regions) |
| `.cleardraw` | `.cdr` | Clear all your target regions (also clears search results)                                                                                                                                    |

> **Box semantics**: the base corner and diagonal corner define the base rectangle (air points are projected down to the ground); the height point's z becomes the box top (must be above the base). The 3D box expresses height-aware targets such as "window flashes" and "on-box smokes".

### Grenade Inverse Search

| Command            | Alias                  | Description                                                                                                                                                                                                    |
| --------------     | ---------------------- | ---------------------------------------------------------------------------------------------                                                                                                                  |
| `.findall`         | `.fa`                  | Search all throw modes × strengths for aim angles that land inside the newest target region                                                                                                                    |
| `.findnormal`      | `.fn`                  | Search normal throws only (`.fnl`/`.fnm`/`.fnr` select left/mid/right strength)                                                                                                                                |
| `.findjump`        | `.fj`                  | Search jump throws only (`.fjl`/`.fjm`/`.fjr` select strength)                                                                                                                                                 |
| `.findrunjump`     | `.frj`                 | Search run-jump throws only (`.frjl`/`.frjm`/`.frjr` select strength)                                                                                                                                          |
| `.findduck`        | `.fd`                  | Search crouch throws only (`.fdl`/`.fdm`/`.fdr` select strength)                                                                                                                                               |
| `.findduckjump`    | `.fdj`                 | Search crouch-jump throws only (`.fdjl`/`.fdjm`/`.fdjr` select strength)                                                                                                                                       |
| `.findduckrunjump` | `.fdrj`                | Search crouch run-jump throws only (`.fdrjl`/`.fdrjm`/`.fdrjr` select strength)                                                                                                                                |
| `.nadeaccuracy`    | `.nac`                 | Show/switch search accuracy `low` (4°) / `mid` (2° + refine) / `high` (1° + refine)                                                                                                                            |
| `.nadetype`        | `.nt`                  | Show/switch item type `smoke`/`flash`/`he`/`molo`/`inc`/`decoy`                                                                                                                                                |
| `.nadetest`        | `.ntt`                 | Throw calibration: throw within 30s after arming, auto-detect strength/mode and print real vs predicted comparison to the server console                                                                       |
| `.nadetestsim`     | `.nts`                 | Single-shot sim debug: run one trajectory sim with `yaw pitch mode strength` and print diagnostics (origin/bounce log/land point/term reason/flight time) to console, for comparing with `.nadetest` real data |

> **Search notes**: draw a target region with `.nadedraw` first; searching is amortized under a per-tick millisecond budget and never stalls the server, with live progress in the center hint; flash/HE use a fixed 1.65s fuse air-burst as the landing point, molotov/incendiary use first ground contact or a 2s air-burst timeout, smoke/decoy use rest; wall-bounce spots are never killed by the pre-filter. The yaw scan sector is ±15° around the direction from your position to the target (known v1 coverage limit: direct + shallow-bounce lineups mainly).

### Search Results

| Command          | Alias  | Description                                                                                          |
| ---------------  | -----  | -----------------------------------------------------------------------------                        |
| `.nadelist`      | `.nl`  | Print the search result table to console (ID / mode / strength / item / aim angle)                   |
| `.nadeclearlist` | `.ncl` | Clear all your search results and their visualization entities                                       |
| `.nadegoto`      | `.ng`  | Teleport to the spot of a result ID, aim at the stored angle, show spot cross and trajectory preview |

> **Result ID rule**: `item-mode-strength-index` (e.g. `SMK-N-L-01`). After teleporting, throw with the hinted strength and mode to reproduce the found trajectory.

### Team Switching

| Command  | Alias  | Description                                                                         |
| -------- | ------ | -----------------------------------------------------------------------             |
| `.watch` | `.fas` | Force all other players into spectator mode, leaving only the command issuer active |

### Grenade Rethrow & Position Recall

| Command           | Alias         | Description                                |
| ----------------  | ------------- | ------------------------------------------ |
| `.rethrow`        | `.rt`         | Rethrow the player's last thrown grenade   |
| `.rethrowsmoke`   | `.rethrows`   | Rethrow only the last smoke grenade        |
| `.rethrownade`    | `.rethrown`   | Rethrow only the last HE grenade           |
| `.rethrowflash`   | `.rethrowf`   | Rethrow only the last flashbang            |
| `.rethrowmolotov` | `.rethrowm`   | Rethrow only the last molotov/incgrenade   |
| `.rethrowdecoy`   | `.rethrowd`   | Rethrow only the last decoy                |
| `.last`           | `.ls`         | Teleport back to the last grenade position |

### Dryrun & Round Control

| Command         | Alias  | Description                                                                                               |
| --------------- | -----  | ---------------------------------------------------------------------------                               |
| `.dryrun`       | `.dry` | Temporarily switch from prac to competitive config for one round; auto-revert to prac when the round ends |
| `.restartround` | `.rr`  | Restart the current round (executes `mp_restartgame`)                                                     |

> **Dryrun flow**: kick all bots → disable prac-specific ConVars (sv_cheats, traj, infinite_ammo, etc.) → load `dryrun.cfg` competitive config → restart the round to begin; when the round ends (`EventRoundEnd`), prac config is automatically restored. Use this to play a real competitive round from within a practice session.

### Replay System

| Command             | Alias         | Description                                                                                                                                                       |
| -----------------   | ------------- | ---------------------------------------------------------------------------                                                                                       |
| `.record`           | —             | Enter pending-record state; **starts recording when the player moves or presses F (inspect key)**; press F again or use `.stoprecord` to stop and save            |
| `.stoprecord`       | —             | Stop recording and save (equivalent to pressing F)                                                                                                                |
| `.replay [Id]`      | —             | No argument: play back all stopped recordings in parallel; with Id: play back only that recording. A bot of the same team is auto-spawned as the playback carrier |
| `.stopreplay`       | —             | Stop all ongoing playbacks and kick the corresponding bots                                                                                                        |
| `.clearrecord <Id>` | —             | Delete the recording with the given Id (removes both JSON file and list entry)                                                                                    |
| `.clearrecordall`   | —             | Clear all recordings                                                                                                                                              |
| `.currentrecord`    | `.currentrec` | Print the recording list table (ID / Name / Status / Bot) to the **server console**                                                                               |

> **Two-layer architecture**: The replay system consists of two parts —
> - **Layer 1 (CSSharp C#)**: command dispatch, UI prompts, JSON read/write for recording files (under `cfg/PracLab/recordings/`).
> - **Layer 2 (C++ Metamod plugin `PracLabReplayEngine`)**: hooks engine functions (`ProcessMovement`, `CCSBot::Update`, `PlayerRunCommand`, etc.) to implement frame-level movement recording and playback.
>
> The cross-language ABI contract is documented in [`replay-engine/include/praclab_replay.h`](../../../replay-engine/include/praclab_replay.h). Any change to field order/type must be synchronized across both layers.
>
> **When the replay engine is not deployed**: all `.record/.replay/...` commands show a "replay engine not loaded, replay unavailable" message to the player, but other plugin features are unaffected.

### Practice HUD

| Command     | Alias | Description                                                                     |
| ----------- | --    | ------------------------------------------------------------------------------  |
| `.strafe`   | —     | Toggle counter-strafe assessment (HUD panel shows the timing verdict and stats) |
| `.shot`     | —     | Toggle shot stability (HUD panel shows the speed error and stable rate)         |
| `.sync`     | —     | Toggle air strafe sync (HUD panel shows the strafe sync rate)                   |
| `.hudmove`  | —     | Move HUD panels (edit mode, see below)                                          |
| `.hudreset` | —     | Clear all your HUD statistics                                                   |

> **Usage**: all four commands require practice mode (`.prac`). The three modules are independent toggles — run the same command again to disable; each keeps its own state and none affects the others (no master toggle). Panels show while enabled and hide it when all are disabled.
>
> - **Counter-strafe assessment**: judges the timing of same-axis movement-key switches (A/D, W/S), showing "Perfect/Excellent/Early/Late" plus average diff (ticks), success rate, standard deviation and overall tendency.
> - **Shot stability**: samples the horizontal speed at fire time and compares it with the current weapon's accuracy threshold, showing the speed error, a label (Stable/Crouch/Micro/Moving, etc.) and the recent stable rate.
> - **Air strafe sync**: ports the cs2kz jumpstats sync algorithm — an air tick counts as synced when there is movement input and the horizontal speed actually gains; dead air, overlapped keys and bad-angle ticks count toward the denominator but not the sync. Shows current, average and best sync rate.
>
> `.hudreset` clears statistics only; toggle states are kept.
>
> **The HUD panels require the client-side VPK resource**: without `prac_hud.vpk` installed, judgment and statistics still work server-side — you just don't see the panels (see the "Practice HUD resource" section in the [installation guide](installation.md)).

#### Moving Panels (.hudmove)

`.hudmove` enters the panel-move edit mode to place panels at any of the nine screen slots:

1. Run `.hudmove`: the server releases your mouse cursor (you cannot turn the view or shoot meanwhile).
2. Click the panel you want to move: it gets highlighted and a 3x3 transparent grid appears.
3. Click the target slot in the grid: the panel instantly moves to the matching screen position (top left / top center / top right / ... / bottom right) and the edit mode exits automatically; **if the slot is taken by another panel, the two swap** (the other panel moves to this panel's previous slot), so panels never overlap.
4. Ways to exit: after placing / pressing **Tab** / running `.hudmove` again — all of them take the cursor back and restore view control.

> - Panel positions are a **personal preference and last only for the current session**: changing map or reconnecting restores the default layout (left column, top/middle/bottom).
> - Disabling any panel while in the edit mode (e.g. `.shot`) exits the edit mode immediately.
> - Clicking panels requires the client-side `prac_hud.vpk` (button interaction is provided by the VXML).

#### Panel Readouts

**Counter-strafe assessment**

| Readout       | Meaning                                                                                                                                                                                                                                                                                                                                                    |
| ---           | ---                                                                                                                                                                                                                                                                                                                                                        |
| Verdict label | Same-axis key (A/D, W/S) switch timing: **Perfect** = switch completed within the same tick; **Excellent** = diff ≤1 tick; **Early** (negative diff) = the opposite key was pressed first; **Late** (positive diff) = the key was released before pressing the opposite one. Verdict colors: green for Perfect / yellow for Excellent / red for Early-Late |
| `±N tick`     | The actual diff of this switch (ticks; 1 tick ≈ 15.6 ms@64tick / 7.8 ms@128tick), `0` is best                                                                                                                                                                                                                                                              |
| Avg ±X tick   | Average diff across all records; the closer to 0 the better                                                                                                                                                                                                                                                                                                |
| Success Y%    | Share of records within "Excellent" (diff ≤1 tick)                                                                                                                                                                                                                                                                                                         |
| Std Z         | Standard deviation of the records; the smaller the more consistent                                                                                                                                                                                                                                                                                         |
| Tendency      | Avg diff `< -1` tick shows "tending early", `> 1` tick "tending late", otherwise "normal"                                                                                                                                                                                                                                                                  |

**Shot stability**

| Readout     | Meaning                                                                                                                                                                                                                                            |
| ---         | ---                                                                                                                                                                                                                                                |
| Label       | Movement state at fire time: **Stable** = ratio ≤1 (within the weapon's moving accuracy); **Micro** = ≤1.5; **Low-speed sway** / **Startup low** = movement keys just pressed or slow drift; **Moving** = >1.5; **Crouch** = firing while crouched |
| `0.75` etc. | Speed ratio = horizontal speed at fire time ÷ current weapon's accuracy threshold; `≤ 1.00` means within accuracy, the lower the better                                                                                                            |
| Stable Y%   | Share of recent records with ratio ≤1                                                                                                                                                                                                              |

> Per-weapon accuracy thresholds are built into the plugin (see the `hud.cfg` section in the [configuration guide](configuration.md)); unlisted weapons fall back to the default.

**Air strafe sync**

| Readout      | Meaning                                                                                                                                                                                        |
| ---          | ---                                                                                                                                                                                            |
| First value  | Sync rate (%) of the current air segment = ticks with movement input and horizontal speed gain ÷ all air ticks (dead air / overlap / bad angles count toward the denominator but not the sync) |
| Second value | Average sync rate of recent air segments                                                                                                                                                       |
| Third value  | Best sync rate on record                                                                                                                                                                       |

### Spray Trace

| Command   | Alias | Description                                                    |
| --------- | --    | --------------------------------------------------------------- |
| `.recoil` | —     | Toggle the spray-control mouse trace (green world-space beams) |

> **How it works**: on the first shot of a sequence a virtual board is anchored in front of your view; each tick your mouse movement (view-angle delta) is drawn as a green world-space beam polyline — pulling down to control the spray extends the trace downward, side adjustments bend it sideways.
>
> **Trace lifecycle**: reload / weapon switch / a pause longer than the reset interval clears the old trace automatically; releasing the fire button stops recording immediately.
>
> **No HUD panel**: it does not depend on the VPK resource and is drawn in world space only. Trace parameters (scale / board distance / segment cap / reset interval) are listed in the `hud.cfg` section of the [configuration guide](configuration.md).

### Timer

| Command  | Alias | Description                                                                              |
| -------- | --    | ------------------------------------------------------                                   |
| `.timer` | —     | Start timing; entering `.timer` again stops it and prints the elapsed time to the player |

### ConVar Toggles

| Command    | Alias | Description                                                             |
| ---------  | --    | -------------------------------------------------------                 |
| `.solid`   | —     | Toggle `mp_solid_teammates` (teammate collision)                        |
| `.impacts` | —     | Toggle `sv_showimpacts` (show bullet impact markers)                    |
| `.traj`    | —     | Toggle `sv_grenade_trajectory_prac_pipreview` (show grenade trajectory) |

### Help

| Command | Alias | Description                                                                         |
| ------- | --    | ---------------------------------------------------------                           |
| `.help` | —     | Display all available commands grouped by category in chat (visible to sender only) |

***
