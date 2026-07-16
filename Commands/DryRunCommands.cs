using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Core.Translations;

namespace PracLab;

/// <summary>
/// Dryrun 与回合控制命令：.dryrun / .dry / .restartround / .rr。
/// .dryrun：从 prac 模式临时切换到竞技配置打一个回合，回合结束自动回到 prac 模式。
/// .restartround：重新开始当前回合（mp_restartgame）。
/// 参考 MEngZy PracticeMode.cs OnDryRunCommand / ExecDryRunCFG / ExecUnpracCommands。
/// </summary>
public partial class PracLab
{
    /// <summary>
    /// .dryrun / .dry — 从 prac 模式临时切换到竞技配置打一个回合。
    /// 流程（参考 MEngZy OnDryRunCommand）：
    /// 1. 踢掉所有 bot
    /// 2. 执行 unprac 命令（关闭 prac 特有的 sv_cheats、traj、infinite_ammo 等）
    /// 3. 执行 dryrun.cfg（竞技模式配置）
    /// 4. mp_restartgame 1 + mp_warmup_end 开始回合
    /// 5. 设置 _isDryRun = true，回合结束（EventRoundEnd）后自动回到 prac 模式
    /// </summary>
    private void HandleDryRun(CCSPlayerController player, string args)
    {
        Server.PrintToConsole("[PracLab] HandleDryRun: executing...");

        try
        {
            var callerName = player.PlayerName;
            Server.PrintToConsole($"[PracLab] {DateTime.Now:HH:mm:ss} DryRun {callerName} triggered dryrun");

            // 第一步：踢掉所有 bot
            Server.ExecuteCommand("bot_kick");
            _pracBots.Clear();

            // 第二步：执行 unprac 命令，关闭 prac 特有的配置
            // 参考 MEngZy PracticeMode.cs ExecUnpracCommands
            Server.ExecuteCommand("sv_cheats false;sv_grenade_trajectory_prac_pipreview false;sv_grenade_trajectory_prac_trailtime 0;mp_ct_default_grenades \"\";mp_ct_default_primary \"\";mp_t_default_grenades \"\";mp_t_default_primary \"\";mp_teammates_are_enemies false;");
            Server.ExecuteCommand("mp_death_drop_breachcharge true;mp_death_drop_defuser true;mp_death_drop_taser true;mp_drop_knife_enable false;mp_death_drop_grenade 2;ammo_grenade_limit_total 4;mp_defuser_allocation 0;sv_infinite_ammo 0;mp_force_pick_time 15");

            // 第三步：执行 dryrun.cfg（竞技模式配置）
            Server.ExecuteCommand($"exec {DryRunConfigPath}");

            // 第四步：延迟执行 mp_warmup_end 和 mp_restartgame，确保 dryrun.cfg 已加载
            // 直接连续执行会导致 prac.cfg 的 mp_roundtime 60 等配置残留（回合未重启，新配置未应用）
            // 分步延迟：先结束热身，再重启回合，确保竞技配置完全生效
            AddTimer(0.5f, () =>
            {
                Server.ExecuteCommand("mp_warmup_end");
                Server.ExecuteCommand("mp_restartgame 1");
                Server.PrintToConsole($"[PracLab] {DateTime.Now:HH:mm:ss} DryRun warmup ended and round restarted");
            });

            // 第五步：标记 dryrun 模式，回合结束后自动回到 prac
            _isDryRun = true;

            // 广播到全体玩家（使用各自语言）
            foreach (var p in Utilities.GetPlayers().Where(x => x.IsValid && !x.IsBot))
                p.PrintToChat(Localizer.ForPlayer(p, "dryrun.started", callerName));

            Server.PrintToConsole($"[PracLab] {DateTime.Now:HH:mm:ss} DryRun started by {callerName}");
        }
        catch (Exception ex)
        {
            Server.PrintToConsole($"[PracLab] {DateTime.Now:HH:mm:ss} Error HandleDryRun failed - {ex.Message}");
            player.PrintToChat(Localizer.ForPlayer(player, "dryrun.error"));
        }
    }

    /// <summary>
    /// .restartround / .rr — 重新开始当前回合。
    /// 执行 mp_restartgame 1，1 秒后重启当前回合（保持当前阵营、经济、武器配置）。
    /// </summary>
    private void HandleRestartRound(CCSPlayerController player, string args)
    {
        Server.PrintToConsole("[PracLab] HandleRestartRound: executing...");

        try
        {
            Server.ExecuteCommand("mp_restartgame 1");

            // 广播到全体玩家（使用各自语言）
            foreach (var p in Utilities.GetPlayers().Where(x => x.IsValid && !x.IsBot))
                p.PrintToChat(Localizer.ForPlayer(p, "restartround.executed", player.PlayerName));

            Server.PrintToConsole($"[PracLab] {DateTime.Now:HH:mm:ss} RestartRound {player.PlayerName} restarted round");
        }
        catch (Exception ex)
        {
            Server.PrintToConsole($"[PracLab] {DateTime.Now:HH:mm:ss} Error HandleRestartRound failed - {ex.Message}");
        }
    }
}
