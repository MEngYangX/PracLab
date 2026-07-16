using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Core.Translations;

namespace PracLab;

/// <summary>
/// 道具与环境清理命令：.clear / .break。
/// 使用 CSS Entity API（Utilities.FindAllEntitiesByDesignerName + entity.Remove/AcceptInput）替代无效的 ent_fire。
/// </summary>
public partial class PracLab
{
    /// <summary>
    /// .clear — 清除所有活跃的烟雾弹、闪光弹、手雷、燃烧瓶、诱饵弹及其衍生实体（inferno）。
    /// 实现说明：原 ent_fire ... kill 在 CSS 中无效，改用 Utilities.FindAllEntitiesByDesignerName + entity.Remove()。
    /// </summary>
    private void HandleClear(CCSPlayerController player, string args)
    {
        Server.PrintToConsole("[PracLab] HandleClear: executing...");

        var removed = 0;
        // 投掷物实体类名（含 inferno 燃烧实体）
        string[] projectileClasses =
        [
            "smokegrenade_projectile",
            "flashbang_projectile",
            "hegrenade_projectile",
            "molotov_projectile",
            "inferno",
            "decoy_projectile",
        ];

        try
        {
            foreach (var cls in projectileClasses)
            {
                foreach (var ent in Utilities.FindAllEntitiesByDesignerName<CBaseEntity>(cls))
                {
                    if (ent == null || !ent.IsValid) continue;
                    ent.Remove();
                    removed++;
                }
            }

            player.PrintToChat(Localizer.ForPlayer(player, "clear.done"));
            Server.PrintToConsole($"[PracLab] {DateTime.Now:HH:mm:ss} Clear {player.PlayerName} removed {removed} projectile entities");
        }
        catch (Exception ex)
        {
            Server.PrintToConsole($"[PracLab] {DateTime.Now:HH:mm:ss} Error HandleClear failed - {ex.Message}");
            player.PrintToChat(Localizer.ForPlayer(player, "command.error", ex.Message));
        }
    }

    /// <summary>
    /// .break / .br — 破坏所有可破坏实体（木板、玻璃、动态道具等）。
    /// 实现说明：原 ent_fire func_breakable break 在 CSS 中无效，改用 Utilities.FindAllEntitiesByDesignerName + entity.AcceptInput("Break")。
    /// </summary>
    private void HandleBreak(CCSPlayerController player, string args)
    {
        Server.PrintToConsole("[PracLab] HandleBreak: executing...");

        var broken = 0;
        string[] breakableClasses =
        [
            "func_breakable",
            "func_breakable_surf",
            "prop_dynamic",
        ];

        try
        {
            foreach (var cls in breakableClasses)
            {
                foreach (var ent in Utilities.FindAllEntitiesByDesignerName<CBaseEntity>(cls))
                {
                    if (ent == null || !ent.IsValid) continue;
                    ent.AcceptInput("Break");
                    broken++;
                }
            }

            player.PrintToChat(Localizer.ForPlayer(player, "break.done"));
            Server.PrintToConsole($"[PracLab] {DateTime.Now:HH:mm:ss} Break {player.PlayerName} broke {broken} entities");
        }
        catch (Exception ex)
        {
            Server.PrintToConsole($"[PracLab] {DateTime.Now:HH:mm:ss} Error HandleBreak failed - {ex.Message}");
            player.PrintToChat(Localizer.ForPlayer(player, "command.error", ex.Message));
        }
    }
}
