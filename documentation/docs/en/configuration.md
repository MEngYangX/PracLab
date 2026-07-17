# Configuration

PracLab's config files all live under the server's `<CS2>/csgo/cfg/PracLab/` directory. The repository's [`cfg/PracLab/`](../../cfg/PracLab/) provides default templates. After editing, restart the plugin or the server for changes to take effect.

## 1. `config.cfg` — Master Toggle

**Path**: `<CS2>/csgo/cfg/PracLab/config.cfg`
**Format**: One ConVar per line; `key value` separated by a space; `//` starts a comment.

| ConVar | Type | Default | Description |
| --- | --- | --- | --- |
| `praclab_enabled` | bool | `true` | Master toggle. When `false`, no command (including `.prac`, `.map`, map switches) responds; the sender sees "practice mode is not enabled". |
| `praclab_default_language` | string | `zh-CN` | Server default language code. Options: `zh-CN` / `en`. |

## 2. Recordings Directory

**Path**: `<CS2>/csgo/cfg/PracLab/recordings/`

JSON recording files saved by `.record` are stored here, with the filename pattern `<Id>_<PlayerName>.json`. `.clearrecord <Id>` and `.clearrecordall` delete both the file and the in-memory entry.

**Auto-created**: The plugin's `Load` calls `EnsureRecordingsDir()` to ensure the directory exists; no manual setup is required.

**Cleared on map change**: The `OnMapStart` listener clears the in-memory recording list (`_recordings.Clear()`), but **does not delete the JSON files**. To preserve recordings across maps, record the Ids via `.currentrecord` before switching maps.

## 3. Spawn Box Per-Map Configuration

Each map's box Z offset and index-text yaw rotation is hardcoded in [`Commands/SpawnMarkerCommands.cs`](../../Commands/SpawnMarkerCommands.cs):

- `SpawnTextYawByMap`: yaw rotation (degrees) of the index text per map per team
- `SpawnExtraZOffsetByMap`: additional Z raise for the box/text per map

**Current configuration**:

| Map | T yaw | CT yaw | Extra Z offset |
| --- | --- | --- | --- |
| de\_cache | 90 | 180 | 0 |
| de\_train | -90 | 0 | 0 |
| de\_anubis | 0 | 180 | 0 |
| de\_vertigo | 90 | 180 | 0 |
| de\_ancient | 0 | 180 | 16 |
| de\_nuke | -90 | 90 | 0 |
| de\_mirage | 90 | -90 | 0 |
| de\_inferno | -90 | 90 | 0 |

Unconfigured maps default to yaw=0 and Z offset=0. When adding support for a new map, extend these tables and rebuild.
