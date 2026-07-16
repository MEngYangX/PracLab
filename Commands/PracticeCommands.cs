using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Core.Attributes;
using CounterStrikeSharp.API.Core.Attributes.Registration;
using CounterStrikeSharp.API.Core.Translations;
using CounterStrikeSharp.API.Modules.Commands;
using CounterStrikeSharp.API.Modules.Utils;

namespace PracLab;

/// <summary>
/// practice 模式控制命令：.prac / .map / 地图切换倒计时。
/// </summary>
public partial class PracLab
{
    /// <summary>
    /// 处理 .prac 命令：加载 practice 配置并标记模式已开启。
    /// </summary>
    private void HandlePrac(CCSPlayerController player, string args)
    {
        Server.PrintToConsole("[PracLab] HandlePrac: executing...");
        LoadPracConfig(player);
    }

    /// <summary>
    /// 处理 .map 命令：向玩家显示可用地图列表（仅发送者可见）。
    /// </summary>
    private void HandleMap(CCSPlayerController player, string args)
    {
        Server.PrintToConsole("[PracLab] HandleMap: executing...");
        ShowMapList(player);
    }

    /// <summary>
    /// 处理 css_prac 控制台命令。聊天触发已在 OnPlayerSay 中处理，此处仅用于服务器控制台。
    /// </summary>
    [ConsoleCommand("css_prac", "加载 practice 模式配置")]
    [CommandHelper(whoCanExecute: CommandUsage.CLIENT_AND_SERVER)]
    public void OnPracCommand(CCSPlayerController? player, CommandInfo command)
    {
        Server.PrintToConsole("[PracLab] OnPracCommand: executing...");
        if (EnsureEnabled(player))
            LoadPracConfig(player);
    }

    /// <summary>
    /// 处理 css_map 控制台命令。聊天触发已在 OnPlayerSay 中处理，此处仅用于服务器控制台。
    /// </summary>
    [ConsoleCommand("css_map", "显示地图列表")]
    [CommandHelper(whoCanExecute: CommandUsage.CLIENT_AND_SERVER)]
    public void OnMapCommand(CCSPlayerController? player, CommandInfo command)
    {
        Server.PrintToConsole("[PracLab] OnMapCommand: executing...");

        if (player == null)
        {
            Server.PrintToConsole($"[PracLab] {DateTime.Now:HH:mm:ss} Map server console requested map list");
            foreach (var (cmd, mapFile) in PracticeMaps)
                Server.PrintToConsole($"  .{cmd} -> {mapFile}");
            return;
        }

        if (!player.IsValid)
            return;

        if (EnsureEnabled(player))
            ShowMapList(player);
    }

    /// <summary>
    /// 加载练习配置文件并标记 practice 模式已开启。
    /// </summary>
    /// <param name="player">触发者，为 null 时表示服务器控制台。</param>
    private void LoadPracConfig(CCSPlayerController? player)
    {
        try
        {
            var callerName = player?.PlayerName ?? "服务器控制台";
            Server.PrintToConsole($"[PracLab] {DateTime.Now:HH:mm:ss} Command {callerName} triggered practice mode load");

            if (player != null)
                player.PrintToChat(Localizer.ForPlayer(player, "prac.command.loading"));
            else
                Server.PrintToConsole($"[PracLab] {DateTime.Now:HH:mm:ss} Command loading practice mode...");

            Server.ExecuteCommand($"exec {PracConfigPath}");
            _isPracMode = true;

            if (player != null)
                player.PrintToChat(Localizer.ForPlayer(player, "prac.command.loaded"));

            // 广播到全体玩家（使用各自语言）
            foreach (var p in Utilities.GetPlayers().Where(x => x.IsValid && !x.IsBot))
                p.PrintToChat(Localizer.ForPlayer(p, "prac.command.broadcast", callerName));

            // 自动显示出生点方框：延迟 2.5 秒，确保 prac.cfg 中 mp_warmup_pausetimer、bot_quota 等已应用
            // 参考 MEngZy PracticeMode.cs 的 2.5f 延迟，等待引擎完成 spawn 实体初始化
            AddTimer(2.5f, () =>
            {
                if (!_isPracMode) return;
                ShowAllSpawnBeams();
                RegisterSpawnUseKeyListener();

                // 广播提示（使用各自语言）
                foreach (var p in Utilities.GetPlayers().Where(x => x.IsValid && !x.IsBot))
                    p.PrintToChat(Localizer.ForPlayer(p, "spawn.look_hint"));
            });

            Server.PrintToConsole($"[PracLab] {DateTime.Now:HH:mm:ss} Command practice mode loaded");
        }
        catch (Exception ex)
        {
            Server.PrintToConsole($"[PracLab] {DateTime.Now:HH:mm:ss} Error load practice mode failed - {ex.Message}");

            if (player != null)
                player.PrintToChat(Localizer.ForPlayer(player, "prac.command.error", ex.Message));
        }
    }

    /// <summary>
    /// 向玩家显示可用地图列表（仅发送者可见，2 列布局）。
    /// 仅在 practice 模式下可用。
    /// </summary>
    /// <param name="player">请求地图列表的玩家。</param>
    private void ShowMapList(CCSPlayerController player)
    {
        if (!_isPracMode)
        {
            player.PrintToChat(Localizer.ForPlayer(player, "prac.mode.required"));
            Server.PrintToConsole($"[PracLab] {DateTime.Now:HH:mm:ss} Map {player.PlayerName} practice mode not enabled, refused to show map list");
            return;
        }

        player.PrintToChat(Localizer.ForPlayer(player, "map.list.title"));

        for (int i = 0; i < PracticeMaps.Length; i += 2)
        {
            var left = PracticeMaps[i];
            var leftName = Localizer.ForPlayer(player, $"map.name.{left.Command}");
            var leftEntry = Localizer.ForPlayer(player, "map.list.entry", left.Command, leftName);

            if (i + 1 < PracticeMaps.Length)
            {
                var right = PracticeMaps[i + 1];
                var rightName = Localizer.ForPlayer(player, $"map.name.{right.Command}");
                var rightEntry = Localizer.ForPlayer(player, "map.list.entry", right.Command, rightName);
                player.PrintToChat($"{leftEntry}    {rightEntry}");
            }
            else
            {
                player.PrintToChat(leftEntry);
            }
        }

        Server.PrintToConsole($"[PracLab] {DateTime.Now:HH:mm:ss} Map {player.PlayerName} viewed map list");
    }

    /// <summary>
    /// 切换到指定地图。仅在 practice 模式下可用。
    /// 玩家发起时启动 5 秒倒计时，倒计时结束后执行 changelevel。
    /// </summary>
    /// <param name="player">触发切换的玩家，为 null 时表示服务器控制台。</param>
    /// <param name="commandName">地图命令名（如 inferno），用于本地化显示。</param>
    /// <param name="mapFile">地图文件名（如 de_inferno），用于 changelevel。</param>
    private void ChangeMap(CCSPlayerController? player, string commandName, string mapFile)
    {
        Server.PrintToConsole("[PracLab] ChangeMap: executing...");

        // 服务器控制台直接切换（无倒计时）
        if (player == null)
        {
            Server.PrintToConsole($"[PracLab] {DateTime.Now:HH:mm:ss} Map server console switching to {mapFile}");
            Server.ExecuteCommand($"changelevel {mapFile}");
            return;
        }

        if (!player.IsValid)
            return;

        // 检查 practice 模式
        if (!_isPracMode)
        {
            player.PrintToChat(Localizer.ForPlayer(player, "prac.mode.required"));
            Server.PrintToConsole($"[PracLab] {DateTime.Now:HH:mm:ss} Map {player.PlayerName} practice mode not enabled, refused to switch map");
            return;
        }

        // 检查是否有待执行的换图
        if (_isMapChangePending)
        {
            player.PrintToChat(Localizer.ForPlayer(player, "map.changing.pending"));
            return;
        }

        // 获取本地化地图名，启动倒计时
        var mapDisplayName = Localizer.ForPlayer(player, $"map.name.{commandName}");
        var playerName = player.PlayerName;

        _isMapChangePending = true;
        StartMapChangeCountdown(playerName, mapDisplayName, mapFile, MapChangeCountdownSeconds);

        Server.PrintToConsole($"[PracLab] {DateTime.Now:HH:mm:ss} Map {playerName} initiated switch to {mapFile}, executing in {MapChangeCountdownSeconds}s");
    }

    /// <summary>
    /// 换图倒计时。每秒广播剩余时间，倒计时归零后执行 changelevel。
    /// 使用链式单次定时器实现，避免重复定时器需要手动 Kill。
    /// </summary>
    /// <param name="playerName">发起换图的玩家名。</param>
    /// <param name="mapDisplayName">地图本地化显示名。</param>
    /// <param name="mapFile">地图文件名（如 de_inferno）。</param>
    /// <param name="countdown">剩余秒数。</param>
    private void StartMapChangeCountdown(string playerName, string mapDisplayName, string mapFile, int countdown)
    {
        // 向全体玩家广播倒计时消息（使用各自语言）
        foreach (var p in Utilities.GetPlayers().Where(x => x.IsValid && !x.IsBot))
            p.PrintToChat(Localizer.ForPlayer(p, "map.changing.countdown", playerName, mapDisplayName, countdown));

        // 安排下一秒
        AddTimer(1.0f, () =>
        {
            if (countdown > 1)
            {
                StartMapChangeCountdown(playerName, mapDisplayName, mapFile, countdown - 1);
            }
            else
            {
                // 倒计时结束，执行换图（地图加载后插件状态重置，_isMapChangePending 自然归零）
                Server.PrintToConsole($"[PracLab] {DateTime.Now:HH:mm:ss} Map countdown finished, executing changelevel {mapFile}");
                Server.ExecuteCommand($"changelevel {mapFile}");
            }
        });
    }
}
