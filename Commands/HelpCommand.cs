using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Core.Translations;

namespace PracLab;

/// <summary>
/// .help / !help 命令实现：在聊天栏分类显示所有可用指令（仅发送者可见）。
/// 描述文本从语言文件读取，支持中英文。不要求 prac 模式，让玩家随时可查看。
/// </summary>
public partial class PracLab
{
    /// <summary>
    /// 命令分类数据：每个分类包含分类标题键和命令列表（命令名, 描述键）。
    /// 描述键对应 lang/*.json 中的 help.cmd.* 条目。
    /// </summary>
    private static readonly (string categoryKey, (string cmd, string descKey)[])[] HelpCategories =
    [
        ("help.cat.practice", [
            ("prac", "help.cmd.prac"),
        ]),
        ("help.cat.map", [
            ("map", "help.cmd.map"),
        ]),
        ("help.cat.bot", [
            ("bot", "help.cmd.bot"),
            ("crouchbot", "help.cmd.crouchbot"),
            ("kickall", "help.cmd.kickall"),
            ("kick", "help.cmd.kick"),
        ]),
        ("help.cat.clear", [
            ("clear", "help.cmd.clear"),
            ("break", "help.cmd.break"),
        ]),
        ("help.cat.time", [
            ("fastforward", "help.cmd.fastforward"),
            ("noflash", "help.cmd.noflash"),
            ("god", "help.cmd.god"),
        ]),
        ("help.cat.spawn", [
            ("spawn <N>", "help.cmd.spawn"),
            ("ctspawn <N>", "help.cmd.ctspawn"),
            ("tspawn <N>", "help.cmd.tspawn"),
            ("bestspawn", "help.cmd.bestspawn"),
            ("worstspawn", "help.cmd.worstspawn"),
        ]),
        ("help.cat.marker", [
            ("showspawns", "help.cmd.showspawns"),
            ("hidespawns", "help.cmd.hidespawns"),
            ("[E key]", "help.cmd.ekey"),
        ]),
        ("help.cat.team", [
            ("watch", "help.cmd.watch"),
        ]),
        ("help.cat.rethrow", [
            ("rethrow", "help.cmd.rethrow"),
            ("rethrowsmoke", "help.cmd.rethrowsmoke"),
            ("rethrownade", "help.cmd.rethrownade"),
            ("rethrowflash", "help.cmd.rethrowflash"),
            ("rethrowmolotov", "help.cmd.rethrowmolotov"),
            ("rethrowdecoy", "help.cmd.rethrowdecoy"),
            ("last", "help.cmd.last"),
        ]),
        ("help.cat.timer", [
            ("timer", "help.cmd.timer"),
        ]),
        ("help.cat.dryrun", [
            ("dryrun", "help.cmd.dryrun"),
            ("restartround", "help.cmd.restartround"),
        ]),
        ("help.cat.replay", [
            ("record", "help.cmd.record"),
            ("stoprecord", "help.cmd.stoprecord"),
            ("replay [Id]", "help.cmd.replay"),
            ("stopreplay", "help.cmd.stopreplay"),
            ("clearrecord <Id>", "help.cmd.clearrecord"),
            ("clearrecordall", "help.cmd.clearrecordall"),
            ("currentrecord", "help.cmd.currentrecord"),
        ]),
        ("help.cat.convar", [
            ("solid", "help.cmd.solid"),
            ("impacts", "help.cmd.impacts"),
            ("traj", "help.cmd.traj"),
        ]),
        ("help.cat.help", [
            ("help", "help.cmd.help"),
        ]),
    ];

    /// <summary>
    /// .help 命令处理器：向发送者按分类显示所有可用指令。
    /// 仅校验插件总开关 _config.Enabled，不要求 prac 模式。
    /// 所有描述从语言文件读取，支持中英文。
    /// </summary>
    private void HandleHelp(CCSPlayerController player, string args)
    {
        Server.PrintToConsole("[PracLab] HandleHelp: executing...");

        if (!EnsureEnabled(player)) return;

        // 标题
        player.PrintToChat(Localizer.ForPlayer(player, "help.title"));

        // 按分类输出
        foreach (var (categoryKey, commands) in HelpCategories)
        {
            // 分类标题
            player.PrintToChat(Localizer.ForPlayer(player, categoryKey));

            // 该分类下的所有命令
            foreach (var (cmd, descKey) in commands)
            {
                string desc = Localizer.ForPlayer(player, descKey);
                player.PrintToChat(Localizer.ForPlayer(player, "help.entry", cmd, desc));
            }
        }

        Server.PrintToConsole($"[PracLab] {DateTime.Now:HH:mm:ss} Help {player.PlayerName} viewed command list");
    }
}
