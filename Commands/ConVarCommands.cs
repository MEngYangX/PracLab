using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Core.Translations;
using CounterStrikeSharp.API.Modules.Cvars;
using CounterStrikeSharp.API.Modules.Utils;

namespace PracLab;

/// <summary>
/// ConVar 快速切换命令：.solid / .impacts / .traj。
/// Bug 6+7 修复：原实现用 GetPrimitiveValue&lt;bool&gt; 读取所有 ConVar，
/// 但 mp_solid_teammates（int: 0/1/2）和 sv_showimpacts（int: 0/1）都是 Int32 类型。
/// 参考 MEngZy PracticeMode.cs 第 1717-1756 行，拆分为三个专用 handler：
/// - .solid: int 类型，切换 (0或1)→2, 2→1
/// - .impacts: int 类型，切换 1-v
/// - .traj: bool 类型，用 Server.ExecuteCommand 确保引擎同步
/// </summary>
public partial class PracLab
{
    /// <summary>
    /// .solid — 切换 mp_solid_teammates（队友碰撞）。
    /// Bug 6：int 类型（0=关闭, 1=仅站立, 2=全开），切换逻辑 (0或1)→2, 2→1。
    /// Bug 3 改进：先获取当前状态显示给用户，再切换，反馈"当前→新状态"。
    /// 参考 MEngZy PracticeMode.cs 第 1717-1729 行。
    /// </summary>
    private void HandleToggleSolid(CCSPlayerController player, string args)
    {
        Server.PrintToConsole("[PracLab] HandleToggleSolid: executing...");

        try
        {
            var cvar = ConVar.Find("mp_solid_teammates");
            if (cvar == null)
            {
                player.PrintToChat(Localizer.ForPlayer(player, "convar.not_found", "mp_solid_teammates"));
                Server.PrintToConsole($"[PracLab] {DateTime.Now:HH:mm:ss} Warning ConVar not found: mp_solid_teammates");
                return;
            }

            var currentValue = cvar.GetPrimitiveValue<int>();
            // 先显示当前状态
            var currentLabel = currentValue switch
            {
                0 => "关闭",
                1 => "仅站立",
                2 => "全开",
                _ => $"未知({currentValue})"
            };

            var newValue = (currentValue == 0 || currentValue == 1) ? 2 : 1;
            var newLabel = newValue switch
            {
                1 => "仅站立",
                2 => "全开",
                _ => $"未知({newValue})"
            };

            cvar.SetValue(newValue);

            // 显示"当前 → 新状态"，让用户清楚知道切换前后的值
            player.PrintToChat($" {ChatColors.Green}[PracLab]{ChatColors.Default} mp_solid_teammates: {ChatColors.Yellow}{currentLabel}{ChatColors.Default} → {ChatColors.Green}{newLabel}{ChatColors.Default}");
            Server.PrintToConsole($"[PracLab] {DateTime.Now:HH:mm:ss} ConVar {player.PlayerName} switched mp_solid_teammates {currentValue}({currentLabel}) -> {newValue}({newLabel})");
        }
        catch (Exception ex)
        {
            Server.PrintToConsole($"[PracLab] {DateTime.Now:HH:mm:ss} Error switch mp_solid_teammates failed - {ex.Message}");
            player.PrintToChat(Localizer.ForPlayer(player, "command.error", ex.Message));
        }
    }

    /// <summary>
    /// .impacts — 切换 sv_showimpacts（弹道显示）。
    /// Bug 6：int 类型（0/1），切换逻辑 1-v。用 Server.ExecuteCommand 确保引擎同步。
    /// Bug 3 改进：先获取当前状态显示给用户，再切换，反馈"当前→新状态"。
    /// 参考 MEngZy PracticeMode.cs 第 1731-1743 行。
    /// </summary>
    private void HandleToggleImpacts(CCSPlayerController player, string args)
    {
        Server.PrintToConsole("[PracLab] HandleToggleImpacts: executing...");

        try
        {
            var cvar = ConVar.Find("sv_showimpacts");
            if (cvar == null)
            {
                player.PrintToChat(Localizer.ForPlayer(player, "convar.not_found", "sv_showimpacts"));
                Server.PrintToConsole($"[PracLab] {DateTime.Now:HH:mm:ss} Warning ConVar not found: sv_showimpacts");
                return;
            }

            var currentValue = cvar.GetPrimitiveValue<int>();
            var currentLabel = currentValue == 1 ? "开" : "关";

            var newValue = 1 - currentValue;
            var newLabel = newValue == 1 ? "开" : "关";

            Server.ExecuteCommand($"sv_showimpacts {newValue}");

            player.PrintToChat($" {ChatColors.Green}[PracLab]{ChatColors.Default} sv_showimpacts: {ChatColors.Yellow}{currentLabel}{ChatColors.Default} → {ChatColors.Green}{newLabel}{ChatColors.Default}");
            Server.PrintToConsole($"[PracLab] {DateTime.Now:HH:mm:ss} ConVar {player.PlayerName} switched sv_showimpacts {currentValue}({currentLabel}) -> {newValue}({newLabel})");
        }
        catch (Exception ex)
        {
            Server.PrintToConsole($"[PracLab] {DateTime.Now:HH:mm:ss} Error switch sv_showimpacts failed - {ex.Message}");
            player.PrintToChat(Localizer.ForPlayer(player, "command.error", ex.Message));
        }
    }

    /// <summary>
    /// .traj — 切换 sv_grenade_trajectory_prac_pipreview（投掷物轨迹预览）。
    /// Bug 7：bool 类型正确，但原实现直接 ref 修改不触发引擎同步。
    /// 改用 Server.ExecuteCommand 让引擎执行命令，确保同步。
    /// Bug 3 改进：先获取当前状态显示给用户，再切换，反馈"当前→新状态"。
    /// 参考 MEngZy PracticeMode.cs 第 1745-1756 行。
    /// </summary>
    private void HandleToggleTraj(CCSPlayerController player, string args)
    {
        Server.PrintToConsole("[PracLab] HandleToggleTraj: executing...");

        try
        {
            var cvar = ConVar.Find("sv_grenade_trajectory_prac_pipreview");
            if (cvar == null)
            {
                player.PrintToChat(Localizer.ForPlayer(player, "convar.not_found", "sv_grenade_trajectory_prac_pipreview"));
                Server.PrintToConsole($"[PracLab] {DateTime.Now:HH:mm:ss} Warning ConVar not found: sv_grenade_trajectory_prac_pipreview");
                return;
            }

            var currentValue = cvar.GetPrimitiveValue<bool>();
            var currentLabel = currentValue ? "开" : "关";

            var newValue = !currentValue;
            var newLabel = newValue ? "开" : "关";

            // 用 Server.ExecuteCommand 而不是直接 SetValue，确保引擎同步
            Server.ExecuteCommand($"sv_grenade_trajectory_prac_pipreview {newValue.ToString().ToLower()}");

            player.PrintToChat($" {ChatColors.Green}[PracLab]{ChatColors.Default} sv_grenade_trajectory: {ChatColors.Yellow}{currentLabel}{ChatColors.Default} → {ChatColors.Green}{newLabel}{ChatColors.Default}");
            Server.PrintToConsole($"[PracLab] {DateTime.Now:HH:mm:ss} ConVar {player.PlayerName} switched sv_grenade_trajectory_prac_pipreview {currentValue}({currentLabel}) -> {newValue}({newLabel})");
        }
        catch (Exception ex)
        {
            Server.PrintToConsole($"[PracLab] {DateTime.Now:HH:mm:ss} Error switch sv_grenade_trajectory_prac_pipreview failed - {ex.Message}");
            player.PrintToChat(Localizer.ForPlayer(player, "command.error", ex.Message));
        }
    }
}
