using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Core.Translations;
using CounterStrikeSharp.API.Modules.Events;

namespace PracLab;

/// <summary>
/// 事件监听器：OnMapStart / OnPlayerBlind / OnEntitySpawned。
/// </summary>
public partial class PracLab
{
    /// <summary>
    /// 地图切换时回调（Fix 11）。
    /// 重置所有跨地图持久的插件状态，避免 _isMapChangePending 等标志位残留导致后续功能不可用。
    /// </summary>
    /// <param name="mapName">新地图名称。</param>
    private void OnMapStart(string mapName)
    {
        Server.PrintToConsole($"[PracLab] OnMapStart: executing... map={mapName}");

        try
        {
            _isMapChangePending = false;
            _isPracMode = false;
            _isDryRun = false;
            _isFastForwardActive = false;

            _noflashState.Clear();
            _timerState.Clear();
            _lastGrenadeThrow.Clear();
            _pracBots.Clear();

            // 停止所有 bot 碰撞管理定时器
            foreach (var timer in _botCollisionTimers.Values)
            {
                try { timer.Kill(); } catch { /* 已停止的定时器忽略 */ }
            }
            _botCollisionTimers.Clear();

            // 清除出生点方框 beam 实体并重置 E 键冷却与监听器标志
            // _isSpawnTickRegistered 重置仅用于热重载场景；正常情况下 OnTick 回调内会通过 _isPracMode 短路返回
            RemoveSpawnBeams();
            _spawnUseCooldown.Clear();
            _isSpawnTickRegistered = false;

            // 重置回放系统状态（地图切换时所有实体被销毁，bot 不复存在，直接清空 C# 侧状态）
            // C++ 侧的回放状态会在下次 PRL_StartReplay 时被重置；_replayEngineAvailable 不重置（跨地图保持）
            // _replayTickRegistered 不重置（OnTick 监听器一旦注册无法注销，通过 _recordingPlayerSlot < 0 短路返回）
            // 清理冻结状态：防止地图切换后新 bot 复用已冻结的 slot 号导致 AI 被错误跳过
            if (_replayEngineAvailable)
            {
                try
                {
                    foreach (var entry in _recordings)
                    {
                        if (entry.BotSlot >= 0)
                            PRL_UnfreezeSlot(entry.BotSlot);
                    }
                }
                catch (Exception ex)
                {
                    Server.PrintToConsole($"[PracLab] {DateTime.Now:HH:mm:ss} Warning OnMapStart unfreeze slots failed - {ex.Message}");
                }
            }
            _recordings.Clear();
            _nextRecordingId = 1;
            _recordingPlayerSlot = -1;
            _pendingRecordSlot = -1;
            _recordFKeyCooldown.Clear();

            Server.PrintToConsole($"[PracLab] {DateTime.Now:HH:mm:ss} Core map changed to {mapName}, all states reset");
        }
        catch (Exception ex)
        {
            Server.PrintToConsole($"[PracLab] {DateTime.Now:HH:mm:ss} Error OnMapStart state reset failed - {ex.Message}");
        }
    }

    /// <summary>
    /// 玩家被闪光弹闪到时回调（Fix 5）。
    /// 若玩家在 _noflashState 中标记为启用，使用 Server.NextFrame 清零 FlashDuration/FlashMaxAlpha。
    /// 用事件驱动替代每 0.1 秒轮询，性能更好且 100% 可靠。
    /// 参考来源：CSS API CCSPlayerPawnBase.FlashDuration/FlashMaxAlpha 为 ref float，可直接赋值。
    /// </summary>
    private HookResult OnPlayerBlind(EventPlayerBlind @event, GameEventInfo info)
    {
        try
        {
            var player = @event.Userid;
            if (player == null || !player.IsValid) return HookResult.Continue;

            var steamId = player.SteamID;
            if (!_noflashState.TryGetValue(steamId, out var enabled) || !enabled)
                return HookResult.Continue;

            // 必须在下一帧重置：player_blind 触发时 FlashDuration 已被引擎写入
            // 同步赋值会被引擎后续逻辑覆盖，NextFrame 确保覆盖引擎的写入
            Server.NextFrame(() =>
            {
                try
                {
                    var pawn = player.PlayerPawn.Value;
                    if (pawn == null || !pawn.IsValid) return;
                    pawn.FlashDuration = 0f;
                    pawn.FlashMaxAlpha = 0f;
                }
                catch (Exception ex)
                {
                    Server.PrintToConsole($"[PracLab] {DateTime.Now:HH:mm:ss} Error OnPlayerBlind NextFrame failed - {ex.Message}");
                }
            });
        }
        catch (Exception ex)
        {
            Server.PrintToConsole($"[PracLab] {DateTime.Now:HH:mm:ss} Error OnPlayerBlind failed - {ex.Message}");
        }
        return HookResult.Continue;
    }

    /// <summary>
    /// 投掷物实体生成时回调（Fix 8+9，Bug 5 重写）。
    /// 记录玩家最后投掷的位置、角度、速度、投掷物类名到 _lastGrenadeThrow，用于 .rethrow 复现弹道。
    /// 参考 MEngZy OnEntitySpawnedHandler：
    /// 1. 用 Server.NextFrame 包裹整个逻辑，确保下一帧读取到真实 AbsVelocity（同步读取时速度可能为 0）
    /// 2. 用 projectile.Thrower.Value.Controller.Value 获取投掷者（替代不可靠的 OwnerEntity.Value）
    /// 3. 跳过 Globalname == "custom" 的实体（rethrow 生成的实体不应被重复记录）
    /// </summary>
    /// <param name="entity">刚生成的实体。</param>
    private void OnEntitySpawned(CEntityInstance entity)
    {
        try
        {
            var designerName = entity.DesignerName;
            if (designerName == null) return;

            // 早返回：非投掷物实体直接跳过
            if (designerName != "smokegrenade_projectile" &&
                designerName != "flashbang_projectile" &&
                designerName != "hegrenade_projectile" &&
                designerName != "molotov_projectile" &&
                designerName != "incgrenade_projectile" &&
                designerName != "decoy_projectile")
                return;

            // 关键修复：在下一帧读取，此时 AbsVelocity 已被引擎赋值
            Server.NextFrame(() =>
            {
                try
                {
                    var projectile = new CBaseCSGrenadeProjectile(entity.Handle);
                    if (!projectile.IsValid) return;

                    // 跳过我们自己 rethrow 生成的实体（Globalname == "custom"）
                    if (projectile.Globalname == "custom") return;

                    // 关键修复：用 Thrower.Value.Controller.Value 获取 player controller
                    // OwnerEntity 可能是 weapon entity 而非 player controller
                    if (!projectile.Thrower.IsValid ||
                        projectile.Thrower.Value == null ||
                        projectile.Thrower.Value.Controller.Value == null)
                        return;

                    var thrower = new CCSPlayerController(projectile.Thrower.Value.Controller.Value.Handle);
                    if (!thrower.IsValid) return;

                    var pos = projectile.AbsOrigin;
                    var ang = projectile.AbsRotation;
                    var vel = projectile.AbsVelocity;

                    if (pos == null || ang == null || vel == null) return;

                    var weapon = MapProjectileToWeapon(designerName);

                    // Bug 2: 记录 ItemIndex，用于 CreateFunc 调用
                    ushort itemIndex = 0;
                    try { itemIndex = (ushort)projectile.ItemIndex; }
                    catch { /* 某些实体可能没有此属性，默认 0 */ }

                    _lastGrenadeThrow[thrower.SteamID] = new GrenadeThrowRecord(
                        pos.X, pos.Y, pos.Z,
                        ang.X, ang.Y, ang.Z,
                        vel.X, vel.Y, vel.Z,
                        weapon,
                        designerName,
                        itemIndex);

                    Server.PrintToConsole($"[PracLab] {DateTime.Now:HH:mm:ss} Grenade {thrower.PlayerName} threw {weapon} velocity=({vel.X:F1},{vel.Y:F1},{vel.Z:F1}) class={designerName}");
                }
                catch (Exception ex)
                {
                    Server.PrintToConsole($"[PracLab] {DateTime.Now:HH:mm:ss} Error OnEntitySpawned NextFrame failed - {ex.Message}");
                }
            });
        }
        catch (Exception ex)
        {
            Server.PrintToConsole($"[PracLab] {DateTime.Now:HH:mm:ss} Error OnEntitySpawned failed - {ex.Message}");
        }
    }

    /// <summary>
    /// 投掷物类名 → 武器名（反向映射，用于记录与显示）。
    /// 与 GrenadeCommands.MapWeaponToProjectile 互为逆映射，独立维护避免 switch 反查复杂度。
    /// </summary>
    /// <param name="projectileClass">投掷物实体类名。</param>
    /// <returns>对应的武器名。</returns>
    private static string MapProjectileToWeapon(string projectileClass) => projectileClass switch
    {
        "smokegrenade_projectile" => "weapon_smokegrenade",
        "flashbang_projectile" => "weapon_flashbang",
        "hegrenade_projectile" => "weapon_hegrenade",
        "molotov_projectile" => "weapon_molotov",
        "incgrenade_projectile" => "weapon_incgrenade",
        "decoy_projectile" => "weapon_decoy",
        _ => "weapon_smokegrenade",
    };

    /// <summary>
    /// 回合结束事件回调。
    /// 如果当前处于 dryrun 模式，回合结束后自动重新加载 prac 配置回到练习模式。
    /// 参考 MEngZy MatchZy.cs L320-328 EventRoundEnd 中的 isDryRun 处理逻辑。
    /// </summary>
    private HookResult OnRoundEnd(EventRoundEnd @event, GameEventInfo info)
    {
        try
        {
            if (!_isDryRun) return HookResult.Continue;

            Server.PrintToConsole($"[PracLab] {DateTime.Now:HH:mm:ss} DryRun round ended, restoring practice mode");

            // 重新加载 prac 配置回到练习模式
            _isDryRun = false;
            Server.ExecuteCommand($"exec {PracConfigPath}");
            _isPracMode = true;

            // 广播到全体玩家（使用各自语言）
            foreach (var p in Utilities.GetPlayers().Where(x => x.IsValid && !x.IsBot))
                p.PrintToChat(Localizer.ForPlayer(p, "dryrun.ended"));

            // 重新显示出生点方框
            AddTimer(2.5f, () =>
            {
                if (!_isPracMode) return;
                ShowAllSpawnBeams();
            });

            Server.PrintToConsole($"[PracLab] {DateTime.Now:HH:mm:ss} DryRun practice mode restored");
        }
        catch (Exception ex)
        {
            Server.PrintToConsole($"[PracLab] {DateTime.Now:HH:mm:ss} Error OnRoundEnd failed - {ex.Message}");
        }

        return HookResult.Continue;
    }
}
