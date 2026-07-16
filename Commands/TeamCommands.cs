using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Core.Translations;
using CounterStrikeSharp.API.Modules.Utils;

namespace PracLab;

/// <summary>
/// 队伍切换命令：仅保留 .watch / .fas。
/// .ct / .t / .spec 已移除，使用 CS2 自带换队逻辑（M 键队伍选择菜单），避免插件层重生流程不可靠。
/// </summary>
public partial class PracLab
{
    /// <summary>
    /// .watch / .fas — 强制所有其他在线玩家进入观察者模式，仅保留命令使用者活动。
    /// 使用 ChangeTeam 而非 SwitchTeam，SwitchTeam 切到观察者会留下残影。
    /// </summary>
    private void HandleWatch(CCSPlayerController player, string args)
    {
        Server.PrintToConsole("[PracLab] HandleWatch: executing...");

        var count = 0;
        foreach (var p in Utilities.GetPlayers().Where(x => x.IsValid && !x.IsBot && x != player))
        {
            p.ChangeTeam(CsTeam.Spectator);
            count++;
        }

        player.PrintToChat(Localizer.ForPlayer(player, "team.fas", count));
        Server.PrintToConsole($"[PracLab] {DateTime.Now:HH:mm:ss} Team {player.PlayerName} moved {count} players to spectator");
    }
}
