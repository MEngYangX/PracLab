using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Core.Translations;
using CounterStrikeSharp.API.Modules.Utils;

namespace PracLab;

/// <summary>
/// 机器人管理命令：.bot / .crouchbot / .kickall / .kick。
/// 参考 MEngZy PracticeMode.cs 实现：
/// - bot_join_team T/CT（生成敌方 bot）+ bot_add_t/ct + AddTimer(0.1f) 等待生成
/// - 维护 pracUsedBots 字典（key = bot UserId）
/// - .kick 使用准星射线检测 GetBotPlayerIsAimingAt，用 kickid 精确踢出
/// Bug 修复：
/// - 生成前检查目标队伍人数，已满则拒绝并告知
/// - 生成后校验 bot 阵营，错误则强制 ChangeTeam 切换
/// - bot_quota_mode 由 prac.cfg 设为 fill（quota 0），不在代码中设置
/// </summary>
public partial class PracLab
{
    /// <summary>
    /// 每队允许的最大玩家数阈值，超过则拒绝生成 bot。
    /// </summary>
    private const int MaxPlayersPerTeamForBot = 10;

    /// <summary>
    /// .bot — 在玩家当前位置生成一个 bot，关闭 AI 并冻结。
    /// 实现步骤：1) 检查目标队伍人数；2) 临时设置 bot_quota_mode normal；
    /// 3) bot_join_team + bot_add_t/ct；4) AddTimer(0.1f) 等待生成；
    /// 5) SpawnBot 遍历 cs_player_controller 找未注册 bot，校验阵营并传送到玩家位置。
    /// </summary>
    /// <param name="forceCrouch">强制生成蹲着的 bot（.cbot 调用时为 true）。</param>
    private void HandleBot(CCSPlayerController player, string args, bool forceCrouch = false)
    {
        Server.PrintToConsole("[PracLab] HandleBot: executing...");

        var pawn = player.PlayerPawn.Value;
        if (pawn == null || !pawn.IsValid || pawn.AbsOrigin == null || pawn.AbsRotation == null)
        {
            player.PrintToChat(Localizer.ForPlayer(player, "spawn.no_pawn"));
            Server.PrintToConsole($"[PracLab] {DateTime.Now:HH:mm:ss} Warning Bot {player.PlayerName} pawn unavailable");
            return;
        }

        // Bug 1 修复：.cbot 强制 forceCrouch=true，不再依赖玩家蹲下状态检测
        var crouch = forceCrouch;
        if (!crouch)
        {
            // 非 .cbot 调用时，检测玩家当前是否蹲下（蹲下时自动生成蹲着的 bot）
            try
            {
                if (pawn.MovementServices != null)
                {
                    var movementService = new CCSPlayer_MovementServices(pawn.MovementServices.Handle);
                    if ((int)movementService.DuckAmount == 1)
                        crouch = true;
                }
            }
            catch (Exception ex)
            {
                Server.PrintToConsole($"[PracLab] {DateTime.Now:HH:mm:ss} Warning read DuckAmount failed - {ex.Message}");
            }
        }

        var playerTeam = (CsTeam)player.TeamNum;
        // 目标敌方阵营：玩家 CT -> bot T；玩家 T -> bot CT
        var targetTeam = playerTeam == CsTeam.CounterTerrorist ? CsTeam.Terrorist : CsTeam.CounterTerrorist;
        var targetTeamNum = (byte)targetTeam;

        // Bug 1：生成前检查目标队伍人数，已满则拒绝
        var targetTeamPlayerCount = Utilities.GetPlayers()
            .Count(p => p.IsValid && p.TeamNum == targetTeamNum);
        if (targetTeamPlayerCount >= MaxPlayersPerTeamForBot)
        {
            player.PrintToChat(Localizer.ForPlayer(player, "bot.team_full", targetTeam));
            Server.PrintToConsole($"[PracLab] {DateTime.Now:HH:mm:ss} Warning Bot {player.PlayerName} target team {targetTeam} is full ({targetTeamPlayerCount} players)");
            return;
        }

        // 生成敌方 bot（玩家是 CT 则 bot 加入 T，反之亦然），用于练习射击
        // bot_quota_mode 由 prac.cfg 设为 fill（quota 0），fill 模式不会自动补充/踢出 bot
        // 参照 MEngZy AddBot：不在此处设置 bot_quota，直接 bot_join_team + bot_add_t/ct
        Server.ExecuteCommand(targetTeam == CsTeam.Terrorist ? "bot_join_team T" : "bot_join_team CT");
        Server.ExecuteCommand(targetTeam == CsTeam.Terrorist ? "bot_add_t" : "bot_add_ct");

        // 关闭 bot AI：停止移动、冻结、僵尸模式
        Server.ExecuteCommand("bot_stop 1");
        Server.ExecuteCommand("bot_freeze 1");
        Server.ExecuteCommand("bot_zombie 1");

        // 延迟 0.1 秒后传送 bot 到玩家位置（参照 MEngZy，0.1f 足够）
        var callerName = player.PlayerName;
        AddTimer(0.1f, () => SpawnBot(player, crouch, callerName, targetTeam));

        player.PrintToChat(Localizer.ForPlayer(player, "bot.added"));
    }

    /// <summary>
    /// 在 bot 生成后将最新 bot 传送到玩家位置并加入 _pracBots。
    /// 参考 MEngZy SpawnBot：遍历 cs_player_controller 找未注册 bot，多余的踢出。
    /// Bug 2 修复：生成后校验 bot 阵营，如果与预期不符则强制 ChangeTeam 切换，
    /// 切换失败则踢出 bot（fill 模式可能将 bot 分配到错误阵营）。
    /// </summary>
    /// <param name="botOwner">bot 归属玩家（命令调用者）。</param>
    /// <param name="crouch">是否蹲下。</param>
    /// <param name="callerName">命令调用者名（用于日志）。</param>
    /// <param name="expectedTeam">预期 bot 加入的敌方阵营。</param>
    private void SpawnBot(CCSPlayerController botOwner, bool crouch, string callerName, CsTeam expectedTeam)
    {
        try
        {
            if (!botOwner.IsValid || botOwner.PlayerPawn.Value == null) return;

            var playerEntities = Utilities.FindAllEntitiesByDesignerName<CCSPlayerController>("cs_player_controller");
            var unusedBotFound = false;

            // 统计当前所有 bot 数量，用于诊断
            var totalBots = 0;
            foreach (var p in playerEntities)
            {
                if (p.IsValid && p.IsBot && !p.IsHLTV) totalBots++;
            }
            Server.PrintToConsole($"[PracLab] {DateTime.Now:HH:mm:ss} Bot SpawnBot: total bots={totalBots}, registered={_pracBots.Count}, expectedTeam={expectedTeam}");

            foreach (var tempPlayer in playerEntities)
            {
                if (!tempPlayer.IsValid || !tempPlayer.IsBot || tempPlayer.IsHLTV) continue;
                if (!tempPlayer.UserId.HasValue) continue;

                var userId = tempPlayer.UserId.Value;

                // 已注册的 bot 跳过
                if (_pracBots.ContainsKey(userId))
                    continue;

                // 找到未注册 bot 后，后续的都踢出（bot_add 可能生成多个，只保留第一个）
                // 参照 MEngZy SpawnBot：直接 kickid，不设置 quota（prac.cfg 的 fill 模式不会自动补充）
                if (unusedBotFound)
                {
                    Server.PrintToConsole($"[PracLab] {DateTime.Now:HH:mm:ss} Bot found extra bot UserId={userId}, kicking");
                    Server.ExecuteCommand($"kickid {userId}");
                    continue;
                }

                // Bug 2：校验 bot 阵营，如果与预期不符则强制切换
                var actualTeam = (CsTeam)tempPlayer.TeamNum;
                if (actualTeam != expectedTeam && actualTeam != CsTeam.None)
                {
                    Server.PrintToConsole($"[PracLab] {DateTime.Now:HH:mm:ss} Bot {tempPlayer.PlayerName} wrong team {actualTeam}, expected {expectedTeam}, force switching");
                    try
                    {
                        tempPlayer.ChangeTeam(expectedTeam);
                    }
                    catch (Exception ex)
                    {
                        Server.PrintToConsole($"[PracLab] {DateTime.Now:HH:mm:ss} Error ChangeTeam failed - {ex.Message}");
                    }

                    // 短延迟后再次检查阵营，仍错误则踢出
                    var botRef = tempPlayer;
                    AddTimer(0.2f, () =>
                    {
                        if (botRef.IsValid && (CsTeam)botRef.TeamNum != expectedTeam)
                        {
                            Server.PrintToConsole($"[PracLab] {DateTime.Now:HH:mm:ss} Bot {botRef.PlayerName} team switch failed, kicking");
                            if (botRef.UserId.HasValue)
                                Server.ExecuteCommand($"kickid {botRef.UserId.Value}");
                        }
                    });
                }

                // 注册新 bot
                _pracBots[userId] = new Dictionary<string, object>();

                var botPawn = tempPlayer.PlayerPawn.Value;
                Vector? botPos = null;
                QAngle? botAng = null;
                if (botPawn != null && botPawn.IsValid)
                {
                    var ownerPawn = botOwner.PlayerPawn.Value;
                    var ownerPos = ownerPawn.AbsOrigin;
                    var ownerAng = ownerPawn.AbsRotation;
                    if (ownerPos != null && ownerAng != null)
                    {
                        botPawn.Teleport(ownerPos, ownerAng, new Vector(0, 0, 0));
                        // Bug 5: 记录 bot 传送到的位置和角度，用于重生时传回
                        botPos = new Vector(ownerPos.X, ownerPos.Y, ownerPos.Z);
                        botAng = new QAngle(ownerAng.X, ownerAng.Y, ownerAng.Z);
                    }
                }

                // 存储属性（Bug 5: 包含 position/angle 用于重生位置）
                _pracBots[userId]["controller"] = tempPlayer;
                _pracBots[userId]["owner"] = botOwner;
                _pracBots[userId]["crouchstate"] = crouch;
                _pracBots[userId]["expectedteam"] = expectedTeam;
                if (botPos != null) _pracBots[userId]["position"] = botPos;
                if (botAng != null) _pracBots[userId]["angle"] = botAng;

                // 蹲下处理
                if (crouch && botPawn != null && botPawn.MovementServices != null)
                {
                    var movementService = new CCSPlayer_MovementServices(botPawn.MovementServices.Handle);
                    AddTimer(0.1f, () => movementService.DuckAmount = 1);
                    // Bug 5: 还需设置 Bot.IsCrouching（MatchZy 模式）
                    AddTimer(0.2f, () =>
                    {
                        if (botPawn.Bot != null) botPawn.Bot.IsCrouching = true;
                    });
                }

                // Bug 3: 启动碰撞管理定时器（重合时关闭碰撞，不重合时开启）
                if (botOwner.IsValid && botOwner.PlayerPawn.IsValid)
                {
                    AddTimer(0.2f, () => ManageBotCollisions(botOwner, tempPlayer));
                }

                unusedBotFound = true;
                Server.PrintToConsole($"[PracLab] {DateTime.Now:HH:mm:ss} Bot {callerName} spawned bot {tempPlayer.PlayerName} UserId={userId} team={tempPlayer.TeamNum}");
            }

            if (!unusedBotFound)
            {
                Server.PrintToConsole($"[PracLab] {DateTime.Now:HH:mm:ss} Warning Bot spawn failed, no unregistered bot found (team may be full)");
                // 向玩家发送失败提示，说明可能的原因
                if (botOwner.IsValid)
                {
                    botOwner.PrintToChat(Localizer.ForPlayer(botOwner, "bot.spawn_failed", expectedTeam));
                }
            }
        }
        catch (Exception ex)
        {
            Server.PrintToConsole($"[PracLab] {DateTime.Now:HH:mm:ss} Error SpawnBot failed - {ex.Message}");
            if (botOwner.IsValid)
            {
                botOwner.PrintToChat(Localizer.ForPlayer(botOwner, "bot.spawn_failed", expectedTeam));
            }
        }
    }

    /// <summary>
    /// .crouchbot / .cbot — 在玩家位置生成一个蹲着的 bot。
    /// Bug 1 修复：强制 forceCrouch=true，不再依赖全局 bot_crouch 命令（不可靠）。
    /// SpawnBot 内部会直接设置 bot 的 DuckAmount=1。
    /// </summary>
    private void HandleCrouchBot(CCSPlayerController player, string args)
    {
        Server.PrintToConsole("[PracLab] HandleCrouchBot: executing...");

        // 强制 forceCrouch=true，SpawnBot 会直接设置 DuckAmount=1
        HandleBot(player, args, forceCrouch: true);

        player.PrintToChat(Localizer.ForPlayer(player, "bot.crouch_added"));
        Server.PrintToConsole($"[PracLab] {DateTime.Now:HH:mm:ss} Bot {player.PlayerName} spawned a crouching bot");
    }

    /// <summary>
    /// .kickall — 移除所有练习 bot 并清空 _pracBots。
    /// 遍历 _pracBots 的所有 UserId 逐个 kickid（避免 bot_kick all 踢出非练习 bot）。
    /// 同时停止所有碰撞管理定时器。
    /// Bug 修复：用 Server.NextFrame 延迟执行 kickid，避免在命令处理器中直接执行不生效。
    /// </summary>
    private void HandleKickAllBots(CCSPlayerController player, string args)
    {
        Server.PrintToConsole("[PracLab] HandleKickAllBots: executing...");

        var userIds = _pracBots.Keys.ToList();
        var count = userIds.Count;

        // 停止所有碰撞管理定时器
        foreach (var userId in userIds)
        {
            if (_botCollisionTimers.TryGetValue(userId, out var timer))
            {
                timer.Kill();
                _botCollisionTimers.Remove(userId);
            }
        }

        // 清空 _pracBots
        _pracBots.Clear();

        // 用 NextFrame 延迟执行 kickid，避免在命令处理器中直接执行不生效
        Server.NextFrame(() =>
        {
            foreach (var userId in userIds)
            {
                try
                {
                    Server.ExecuteCommand($"kickid {userId}");
                }
                catch (Exception ex)
                {
                    Server.PrintToConsole($"[PracLab] {DateTime.Now:HH:mm:ss} Error kickid {userId} failed - {ex.Message}");
                }
            }
            Server.PrintToConsole($"[PracLab] {DateTime.Now:HH:mm:ss} Bot kicked {userIds.Count} bots via kickid");
        });

        player.PrintToChat(Localizer.ForPlayer(player, "bot.kicked_all"));
        Server.PrintToConsole($"[PracLab] {DateTime.Now:HH:mm:ss} Bot {player.PlayerName} removed {count} practice bots");
    }

    /// <summary>
    /// .kick — 移除玩家准星指向的 bot。
    /// 参考 MEngZy OnKickBotCommand：用 GetBotPlayerIsAimingAt 找准星指向的 bot，用 kickid 精确踢出。
    /// Bug 修复：
    /// - 检查瞄准的 bot 是否在 _pracBots 中，不在则提示用户
    /// - 用 Server.NextFrame 延迟执行 kickid，避免在命令处理器中直接执行不生效
    /// </summary>
    private void HandleKickBot(CCSPlayerController player, string args)
    {
        Server.PrintToConsole("[PracLab] HandleKickBot: executing...");

        if (_pracBots.Count == 0)
        {
            player.PrintToChat(Localizer.ForPlayer(player, "bot.none_to_kick"));
            Server.PrintToConsole($"[PracLab] {DateTime.Now:HH:mm:ss} Warning Bot {player.PlayerName} has no practice bots to kick");
            return;
        }

        var targetBot = GetBotPlayerIsAimingAt(player);

        if (targetBot != null && targetBot.IsBot && targetBot.UserId.HasValue)
        {
            var botUserId = targetBot.UserId.Value;
            var botName = targetBot.PlayerName;

            // 检查瞄准的 bot 是否是练习 bot
            if (!_pracBots.ContainsKey(botUserId))
            {
                player.PrintToChat(Localizer.ForPlayer(player, "bot.aim_not_prac"));
                Server.PrintToConsole($"[PracLab] {DateTime.Now:HH:mm:ss} Warning Bot {player.PlayerName} aimed at non-practice bot {botName} UserId={botUserId}");
                return;
            }

            // 从 _pracBots 移除
            _pracBots.Remove(botUserId);

            // 停止碰撞管理定时器
            StopBotCollisionTimer(targetBot);

            // 用 NextFrame 延迟执行 kickid，避免在命令处理器中直接执行不生效
            Server.NextFrame(() =>
            {
                try
                {
                    Server.ExecuteCommand($"kickid {botUserId}");
                    Server.PrintToConsole($"[PracLab] {DateTime.Now:HH:mm:ss} Bot kicked bot {botName} UserId={botUserId} via kickid");
                }
                catch (Exception ex)
                {
                    Server.PrintToConsole($"[PracLab] {DateTime.Now:HH:mm:ss} Error kickid failed - {ex.Message}");
                }
            });

            player.PrintToChat(Localizer.ForPlayer(player, "bot.kicked_aim", botName));
            Server.PrintToConsole($"[PracLab] {DateTime.Now:HH:mm:ss} Bot {player.PlayerName} kicked crosshair bot {botName} UserId={botUserId}");
        }
        else
        {
            player.PrintToChat(Localizer.ForPlayer(player, "bot.aim_none"));
            Server.PrintToConsole($"[PracLab] {DateTime.Now:HH:mm:ss} Warning Bot {player.PlayerName} found no bot at crosshair");
        }
    }

    /// <summary>
    /// 通过准星射线检测找到玩家瞄准的 bot。
    /// 参考 MEngZy GetBotPlayerIsAimingAt：用玩家眼睛位置 + 视角方向构造射线，
    /// 遍历 _pracBots 计算每个 bot 到射线的距离，返回距离最近且在阈值内的 bot。
    /// </summary>
    /// <param name="player">玩家。</param>
    /// <returns>准星指向的 bot，未找到返回 null。</returns>
    private CCSPlayerController? GetBotPlayerIsAimingAt(CCSPlayerController player)
    {
        if (player == null || !player.IsValid || !player.PlayerPawn.IsValid || player.PlayerPawn.Value == null)
            return null;

        var playerOrigin = player.PlayerPawn.Value.AbsOrigin;
        if (playerOrigin == null) return null;

        // 眼睛位置 = 原点 + 64 单位垂直偏移
        var playerPosition = new Vector(playerOrigin.X, playerOrigin.Y, playerOrigin.Z + 64.0f);
        var playerAngle = player.PlayerPawn.Value.EyeAngles;

        var forward = QAngleToVector(playerAngle);
        var maxDistance = 1000.0f;
        var rayEnd = new Vector(
            playerPosition.X + forward.X * maxDistance,
            playerPosition.Y + forward.Y * maxDistance,
            playerPosition.Z + forward.Z * maxDistance);

        CCSPlayerController? closestBot = null;
        var closestDistance = float.MaxValue;

        foreach (var kvp in _pracBots)
        {
            if (kvp.Value["controller"] is not CCSPlayerController bot || !bot.IsValid || !bot.IsBot)
                continue;

            var botPawn = bot.PlayerPawn.Value;
            if (botPawn == null || botPawn.AbsOrigin == null) continue;

            var botPosition = botPawn.AbsOrigin;
            var distance = DistanceToRay(botPosition, playerPosition, rayEnd);

            // 距离阈值 100 单位，距离更近的优先
            if (distance < 100.0f && distance < closestDistance)
            {
                closestBot = bot;
                closestDistance = distance;
            }
        }

        return closestBot;
    }

    /// <summary>
    /// 将 QAngle 角度转换为单位前方向向量。
    /// 参考 MEngZy QAngleToVector。
    /// </summary>
    private static Vector QAngleToVector(QAngle angle)
    {
        var pitch = angle.X * (float)Math.PI / 180.0f;
        var yaw = angle.Y * (float)Math.PI / 180.0f;

        var sp = (float)Math.Sin(pitch);
        var cp = (float)Math.Cos(pitch);
        var sy = (float)Math.Sin(yaw);
        var cy = (float)Math.Cos(yaw);

        return new Vector(cp * cy, cp * sy, -sp);
    }

    /// <summary>
    /// 计算点到射线的最短距离。
    /// 参考 MEngZy DistanceToRay。
    /// </summary>
    private static float DistanceToRay(Vector point, Vector rayStart, Vector rayEnd)
    {
        var rayDir = new Vector(rayEnd.X - rayStart.X, rayEnd.Y - rayStart.Y, rayEnd.Z - rayStart.Z);
        var rayLength = (float)Math.Sqrt(rayDir.X * rayDir.X + rayDir.Y * rayDir.Y + rayDir.Z * rayDir.Z);
        if (rayLength == 0) return float.MaxValue;
        rayDir.X /= rayLength; rayDir.Y /= rayLength; rayDir.Z /= rayLength;

        var toPoint = new Vector(point.X - rayStart.X, point.Y - rayStart.Y, point.Z - rayStart.Z);
        var projectionLength = toPoint.X * rayDir.X + toPoint.Y * rayDir.Y + toPoint.Z * rayDir.Z;

        if (projectionLength < 0)
            projectionLength = 0;
        else if (projectionLength > rayLength)
            projectionLength = rayLength;

        var projectedPoint = new Vector(
            rayStart.X + rayDir.X * projectionLength,
            rayStart.Y + rayDir.Y * projectionLength,
            rayStart.Z + rayDir.Z * projectionLength);

        var dx = point.X - projectedPoint.X;
        var dy = point.Y - projectedPoint.Y;
        var dz = point.Z - projectedPoint.Z;
        return (float)Math.Sqrt(dx * dx + dy * dy + dz * dz);
    }
}
