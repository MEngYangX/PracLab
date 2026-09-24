using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Modules.Timers;
using CounterStrikeSharp.API.Modules.Utils;

namespace PracLab;

/// <summary>
/// Bot 碰撞管理与重生位置处理。
/// Bug 3：bot 与玩家碰撞箱重合时会卡住，需要持续检测——重合时关闭碰撞，不重合时开启碰撞。
/// Bug 5：bot 被杀死后重生时应该传送到原始创建位置。
/// 参考 MatchZy PracticeMode.cs 第 1015-1063 行（TemporarilyDisableCollisions/DoPlayersCollide）
/// 和第 1097-1131 行（OnPlayerSpawn bot 重生位置）。
/// 与 MatchZy 区别：MatchZy 是一次性关闭碰撞，不重合后永久恢复；本实现为持续循环检测。
/// </summary>
public partial class PracLab
{
    /// <summary>
    /// 碰撞组常量：DEBRIS（碎片，不与玩家碰撞）。
    /// </summary>
    private const byte CollisionGroupDebris = 2;

    /// <summary>
    /// 碰撞组常量：PLAYER_MOVEMENT（玩家移动组，正常碰撞，可被攻击）。
    /// </summary>
    private const byte CollisionGroupPlayerMovement = 8;

    /// <summary>
    /// 启动 bot 与 owner 之间的碰撞管理定时器。
    /// Bug 3 修复：循环检测两者碰撞箱是否重合——
    ///   - 重合时：设置 CollisionGroup 为 DEBRIS，避免卡住
    ///   - 不重合时：恢复 CollisionGroup 为 PLAYER_MOVEMENT，可以正常攻击
    /// 定时器以 0.1 秒间隔循环执行，bot 被踢出或玩家失效时自动停止。
    /// 参考 MatchZy PracticeMode.cs 第 1015-1048 行，区别在于使用循环检测而非一次性。
    /// </summary>
    /// <param name="owner">bot 归属玩家。</param>
    /// <param name="bot">练习 bot。</param>
    private void ManageBotCollisions(CCSPlayerController owner, CCSPlayerController bot)
    {
        // 有效性检查：bot 可能已被踢出或玩家已离开，此时访问 PlayerName 会抛 ArgumentNullException
        if (!owner.IsValid || !bot.IsValid || !owner.PlayerPawn.IsValid || !bot.PlayerPawn.IsValid)
        {
            Server.PrintToConsole($"[PracLab] {DateTime.Now:HH:mm:ss} Bot ManageBotCollisions skipped, owner or bot invalid");
            return;
        }
        Server.PrintToConsole($"[PracLab] {DateTime.Now:HH:mm:ss} Bot ManageBotCollisions: {owner.PlayerName} <-> {bot.PlayerName}");

        // 若该 bot 已有定时器，先 Kill 避免重复
        if (bot.UserId.HasValue && _botCollisionTimers.TryGetValue(bot.UserId.Value, out var existingTimer))
        {
            existingTimer.Kill();
            _botCollisionTimers.Remove(bot.UserId.Value);
        }

        var ownerPawnRef = owner.PlayerPawn;
        var botPawnRef = bot.PlayerPawn;
        var ownerRef = owner;
        var botRef = bot;

        // 循环定时器：持续检测碰撞状态
        var timer = AddTimer(0.1f, () =>
        {
            try
            {
                // 任一玩家或 pawn 失效 → 停止定时器
                if (!ownerRef.IsValid || !botRef.IsValid ||
                    !ownerPawnRef.IsValid || !botPawnRef.IsValid ||
                    ownerPawnRef.Value == null || botPawnRef.Value == null ||
                    !ownerPawnRef.Value.IsValid || !botPawnRef.Value.IsValid)
                {
                    StopBotCollisionTimer(botRef);
                    return;
                }

                var ownerPawn = ownerPawnRef.Value;
                var botPawn = botPawnRef.Value;

                if (DoPlayersCollide(ownerPawn, botPawn))
                {
                    // 重合 → 关闭碰撞（避免卡住）
                    SetCollisionGroup(ownerPawn, CollisionGroupDebris);
                    SetCollisionGroup(botPawn, CollisionGroupDebris);
                }
                else
                {
                    // 不重合 → 开启碰撞（可以攻击）
                    SetCollisionGroup(ownerPawn, CollisionGroupPlayerMovement);
                    SetCollisionGroup(botPawn, CollisionGroupPlayerMovement);
                }
            }
            catch (Exception ex)
            {
                Server.PrintToConsole($"[PracLab] {DateTime.Now:HH:mm:ss} Error ManageBotCollisions timer failed - {ex.Message}");
                StopBotCollisionTimer(botRef);
            }
        }, TimerFlags.REPEAT | TimerFlags.STOP_ON_MAPCHANGE);

        if (bot.UserId.HasValue)
        {
            _botCollisionTimers[bot.UserId.Value] = timer;
        }
    }

    /// <summary>
    /// 设置玩家 pawn 的碰撞组（同时设置 CollisionAttribute.CollisionGroup 与 Collision.CollisionGroup）。
    /// </summary>
    /// <param name="pawn">玩家 pawn。</param>
    /// <param name="group">目标碰撞组。</param>
    private static void SetCollisionGroup(CCSPlayerPawn pawn, byte group)
    {
        pawn.Collision.CollisionAttribute.CollisionGroup = group;
        pawn.Collision.CollisionGroup = group;
    }

    /// <summary>
    /// 停止指定 bot 的碰撞管理定时器。
    /// 用于 bot 被踢出或玩家失效时清理定时器，避免泄漏。
    /// </summary>
    /// <param name="bot">练习 bot。</param>
    private void StopBotCollisionTimer(CCSPlayerController bot)
    {
        if (!bot.IsValid || !bot.UserId.HasValue) return;
        if (_botCollisionTimers.TryGetValue(bot.UserId.Value, out var timer))
        {
            timer.Kill();
            _botCollisionTimers.Remove(bot.UserId.Value);
            Server.PrintToConsole($"[PracLab] {DateTime.Now:HH:mm:ss} Bot stopped collision management timer for {bot.PlayerName}");
        }
    }

    /// <summary>
    /// 检测两个玩家的碰撞箱是否重叠。
    /// 参考 MatchZy PracticeMode.cs 第 1050-1063 行。
    /// </summary>
    private static bool DoPlayersCollide(CCSPlayerPawn p1, CCSPlayerPawn p2)
    {
        var p1pos = p1.AbsOrigin;
        var p2pos = p2.AbsOrigin;
        if (p1pos == null || p2pos == null) return false;

        var p1min = p1.Collision.Mins + p1pos;
        var p1max = p1.Collision.Maxs + p1pos;
        var p2min = p2.Collision.Mins + p2pos;
        var p2max = p2.Collision.Maxs + p2pos;

        return p1min.X <= p2max.X && p1max.X >= p2min.X &&
               p1min.Y <= p2max.Y && p1max.Y >= p2min.Y &&
               p1min.Z <= p2max.Z && p1max.Z >= p2min.Z;
    }

    /// <summary>
    /// EventPlayerSpawn 事件处理：bot 重生时传送到原始创建位置。
    /// Bug 5：bot 被杀死后重生时 CS2 会传送到出生点，需要传回原始创建位置。
    /// 参考 MatchZy PracticeMode.cs 第 1097-1131 行。
    /// 注意：事件在 PracLab.Load 中通过 RegisterEventHandler 注册，不使用 [GameEventHandler] 特性。
    /// </summary>
    public HookResult OnPlayerSpawn(EventPlayerSpawn @event, GameEventInfo info)
    {
        var player = @event.Userid;
        if (player == null || !player.IsValid) return HookResult.Continue;

        // 仅在练习模式下处理
        if (!_isPracMode) return HookResult.Continue;

        // 仅处理 bot
        if (!player.IsBot || player.IsHLTV) return HookResult.Continue;
        if (!player.UserId.HasValue) return HookResult.Continue;

        var userId = player.UserId.Value;

        // 检查是否是我们创建的练习 bot
        if (_pracBots.ContainsKey(userId))
        {
            try
            {
                var botData = _pracBots[userId];

                // 传送到原始创建位置
                if (botData.TryGetValue("position", out var posObj) && posObj is Vector botPos)
                {
                    var botPawn = player.PlayerPawn.Value;
                    if (botPawn != null && botPawn.IsValid)
                    {
                        QAngle? botAng = botData.TryGetValue("angle", out var angObj) ? angObj as QAngle : null;
                        botPawn.Teleport(botPos, botAng ?? new QAngle(0, 0, 0), new Vector(0, 0, 0));
                        Server.PrintToConsole($"[PracLab] {DateTime.Now:HH:mm:ss} Bot {player.PlayerName} respawned to original position");
                    }
                }

                // 恢复蹲下状态
                if (botData.TryGetValue("crouchstate", out var crouchObj) && crouchObj is bool isCrouched && isCrouched)
                {
                    var botPawn = player.PlayerPawn.Value;
                    if (botPawn != null && botPawn.IsValid)
                    {
                        botPawn.Flags |= (uint)PlayerFlags.FL_DUCKING;
                        if (botPawn.MovementServices != null)
                        {
                            var movementService = new CCSPlayer_MovementServices(botPawn.MovementServices.Handle);
                            AddTimer(0.1f, () => movementService.DuckAmount = 1);
                        }
                        AddTimer(0.2f, () =>
                        {
                            if (botPawn.Bot != null) botPawn.Bot.IsCrouching = true;
                        });
                    }
                }

                // 重启碰撞管理定时器（bot 重生后 pawn 是新的，需要重新建立引用）
                if (botData.TryGetValue("owner", out var ownerObj) && ownerObj is CCSPlayerController botOwner)
                {
                    if (botOwner.IsValid && botOwner.PlayerPawn.IsValid)
                    {
                        AddTimer(0.2f, () => ManageBotCollisions(botOwner, player));
                    }
                }
            }
            catch (Exception ex)
            {
                Server.PrintToConsole($"[PracLab] {DateTime.Now:HH:mm:ss} Error OnPlayerSpawn bot respawn handling failed - {ex.Message}");
            }
        }
        // 注意：不在此处踢出未注册的 bot（erroneous bot 检测）。
        // 参照 MEngZy 实现：多余 bot 由 SpawnBot 中的 kickid 处理，不依赖 OnPlayerSpawn。
        // MatchZy 的 erroneous bot 检测会导致 bot 被误踢（OnPlayerSpawn 在 bot 生成时触发，
        // 此时 _pracBots 尚未填充，2.5 秒后检查会误踢所有 bot）。

        return HookResult.Continue;
    }
}
