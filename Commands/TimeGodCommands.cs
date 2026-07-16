using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Core.Translations;
using CounterStrikeSharp.API.Modules.Utils;

namespace PracLab;

/// <summary>
/// 时间与无敌命令：.fastforward / .noflash / .god。
/// </summary>
public partial class PracLab
{
    /// <summary>
    /// .fastforward / .ff — 启动 10 倍速服务器时间倍速，持续 20 秒后自动还原。
    /// Fix 4：原实现降级为 mp_restartgame，并非真正快进。现使用 host_timescale 10（需 sv_cheats），
    /// 同时冻结所有在线玩家移动（MoveType = MOVETYPE_NONE）避免倍速期间失控，20 秒后还原 host_timescale 1 与移动模式。
    /// 参考 MatchZy PracticeMode.cs 的 fastforward 实现。
    /// </summary>
    private void HandleFastForward(CCSPlayerController player, string args)
    {
        Server.PrintToConsole("[PracLab] HandleFastForward: executing...");

        if (_isFastForwardActive)
        {
            player.PrintToChat(Localizer.ForPlayer(player, "ff.already_active"));
            Server.PrintToConsole($"[PracLab] {DateTime.Now:HH:mm:ss} Warning FF {player.PlayerName} tried to trigger fastforward again, rejected");
            return;
        }

        try
        {
            _isFastForwardActive = true;

            // 冻结所有在线玩家（避免 fastforward 期间乱动）
            foreach (var p in Utilities.GetPlayers().Where(x => x.IsValid && !x.IsBot && x.PlayerPawn.Value != null))
            {
                p.PlayerPawn.Value!.MoveType = MoveType_t.MOVETYPE_NONE;
            }

            // 启用 10 倍速
            Server.ExecuteCommand("host_timescale 10");

            // 广播到全体玩家
            foreach (var p in Utilities.GetPlayers().Where(x => x.IsValid && !x.IsBot))
                p.PrintToChat(Localizer.ForPlayer(p, "ff.started"));

            Server.PrintToConsole($"[PracLab] {DateTime.Now:HH:mm:ss} FF {player.PlayerName} started 10x fastforward (auto restore after 20s)");

            // 20 秒后还原 host_timescale 与玩家移动模式
            AddTimer(20.0f, () =>
            {
                try
                {
                    Server.ExecuteCommand("host_timescale 1");
                    foreach (var p in Utilities.GetPlayers().Where(x => x.IsValid && !x.IsBot && x.PlayerPawn.Value != null))
                    {
                        p.PlayerPawn.Value!.MoveType = MoveType_t.MOVETYPE_WALK;
                    }
                    _isFastForwardActive = false;
                    Server.PrintToConsole($"[PracLab] {DateTime.Now:HH:mm:ss} FF fastforward restored");
                }
                catch (Exception ex)
                {
                    Server.PrintToConsole($"[PracLab] {DateTime.Now:HH:mm:ss} Error fastforward restore failed - {ex.Message}");
                    _isFastForwardActive = false;
                }
            });
        }
        catch (Exception ex)
        {
            Server.PrintToConsole($"[PracLab] {DateTime.Now:HH:mm:ss} Error HandleFastForward failed - {ex.Message}");
            player.PrintToChat(Localizer.ForPlayer(player, "command.error", ex.Message));
            _isFastForwardActive = false;
        }
    }

    /// <summary>
    /// .noflash — 切换该玩家的闪光弹免疫（其他玩家不受影响）。
    /// 实际生效由 OnPlayerBlind 事件处理（EventHandlers.cs），事件触发时用 Server.NextFrame 清零
    /// FlashDuration/FlashMaxAlpha，覆盖引擎写入。
    /// Fix 5：原实现使用 0.1 秒轮询定时器，存在浮点窗口与不必要开销，现已替换为事件驱动。
    /// </summary>
    private void HandleNoflash(CCSPlayerController player, string args)
    {
        Server.PrintToConsole("[PracLab] HandleNoflash: executing...");
        var steamId = player.SteamID;
        var current = _noflashState.GetValueOrDefault(steamId, false);
        var newValue = !current;
        _noflashState[steamId] = newValue;
        player.PrintToChat(Localizer.ForPlayer(player, newValue ? "noflash.enabled" : "noflash.disabled"));
        Server.PrintToConsole($"[PracLab] {DateTime.Now:HH:mm:ss} Noflash {player.PlayerName} flashbang immunity {(newValue ? "enabled" : "disabled")}");
    }

    /// <summary>
    /// .god — 切换该玩家无敌模式。
    /// 通过切换 pawn.TakesDamage 实现（false = 无敌，true = 正常受伤）。
    /// </summary>
    private void HandleGod(CCSPlayerController player, string args)
    {
        Server.PrintToConsole("[PracLab] HandleGod: executing...");
        var pawn = player.PlayerPawn.Value;
        if (pawn == null || !pawn.IsValid)
        {
            player.PrintToChat(Localizer.ForPlayer(player, "god.no_pawn"));
            Server.PrintToConsole($"[PracLab] {DateTime.Now:HH:mm:ss} Warning God {player.PlayerName} pawn unavailable");
            return;
        }

        pawn.TakesDamage = !pawn.TakesDamage;
        var godEnabled = !pawn.TakesDamage;
        player.PrintToChat(Localizer.ForPlayer(player, godEnabled ? "god.enabled" : "god.disabled"));
        Server.PrintToConsole($"[PracLab] {DateTime.Now:HH:mm:ss} God {player.PlayerName} god mode {(godEnabled ? "enabled" : "disabled")}");
    }
}
