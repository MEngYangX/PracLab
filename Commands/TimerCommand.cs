using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Core.Translations;

namespace PracLab;

/// <summary>
/// 计时器命令：.timer。
/// </summary>
public partial class PracLab
{
    /// <summary>
    /// .timer — 开始计时，再次输入时停止并向玩家打印耗时。
    /// </summary>
    private void HandleTimer(CCSPlayerController player, string args)
    {
        Server.PrintToConsole("[PracLab] HandleTimer: executing...");
        var steamId = player.SteamID;
        if (_timerState.TryGetValue(steamId, out var startTime))
        {
            // 第二次输入：停止计时并打印耗时
            var elapsed = DateTime.Now - startTime;
            _timerState.Remove(steamId);
            player.PrintToChat(Localizer.ForPlayer(player, "timer.stopped", elapsed.TotalSeconds));
            Server.PrintToConsole($"[PracLab] {DateTime.Now:HH:mm:ss} Timer {player.PlayerName} timer ended: {elapsed.TotalSeconds:F2}s");
        }
        else
        {
            // 第一次输入：开始计时
            _timerState[steamId] = DateTime.Now;
            player.PrintToChat(Localizer.ForPlayer(player, "timer.started"));
            Server.PrintToConsole($"[PracLab] {DateTime.Now:HH:mm:ss} Timer {player.PlayerName} timer started");
        }
    }
}
