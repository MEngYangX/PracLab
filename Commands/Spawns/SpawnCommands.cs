using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Core.Translations;
using CounterStrikeSharp.API.Modules.Utils;

namespace PracLab;

/// <summary>
/// 出生点传送命令：.spawn / .ctspawn / .tspawn / .bestspawn 等 9 条。
/// </summary>
public partial class PracLab
{
    /// <summary>
    /// .spawn &lt;N&gt; / .s &lt;N&gt; — 传送到同队第 N 个出生点（默认 N=1）。
    /// </summary>
    private void HandleSpawn(CCSPlayerController player, string args)
    {
        HandleSpawnTeleport(player, args, (CsTeam)player.TeamNum);
    }

    /// <summary>
    /// .ctspawn &lt;N&gt; / .cts &lt;N&gt; — 传送到 CT 方第 N 个出生点。
    /// </summary>
    private void HandleCtSpawn(CCSPlayerController player, string args)
    {
        HandleSpawnTeleport(player, args, CsTeam.CounterTerrorist);
    }

    /// <summary>
    /// .tspawn &lt;N&gt; / .ts &lt;N&gt; — 传送到 T 方第 N 个出生点。
    /// </summary>
    private void HandleTSpawn(CCSPlayerController player, string args)
    {
        HandleSpawnTeleport(player, args, CsTeam.Terrorist);
    }

    /// <summary>
    /// .bestspawn / .bs — 传送到同队最近的出生点。
    /// </summary>
    private void HandleBestSpawn(CCSPlayerController player, string args)
    {
        HandleSpawnByDistance(player, (CsTeam)player.TeamNum, nearest: true);
    }

    /// <summary>
    /// .worstspawn / .ws — 传送到同队最远的出生点。
    /// </summary>
    private void HandleWorstSpawn(CCSPlayerController player, string args)
    {
        HandleSpawnByDistance(player, (CsTeam)player.TeamNum, nearest: false);
    }

    /// <summary>
    /// .bestctspawn / .bcts — 传送到最近的 CT 出生点。
    /// </summary>
    private void HandleBestCtSpawn(CCSPlayerController player, string args)
    {
        HandleSpawnByDistance(player, CsTeam.CounterTerrorist, nearest: true);
    }

    /// <summary>
    /// .worstctspawn / .wcts — 传送到最远的 CT 出生点。
    /// </summary>
    private void HandleWorstCtSpawn(CCSPlayerController player, string args)
    {
        HandleSpawnByDistance(player, CsTeam.CounterTerrorist, nearest: false);
    }

    /// <summary>
    /// .besttspawn / .bts — 传送到最近的 T 出生点。
    /// </summary>
    private void HandleBestTSpawn(CCSPlayerController player, string args)
    {
        HandleSpawnByDistance(player, CsTeam.Terrorist, nearest: true);
    }

    /// <summary>
    /// .worsttspawn / .wts — 传送到最远的 T 出生点。
    /// </summary>
    private void HandleWorstTSpawn(CCSPlayerController player, string args)
    {
        HandleSpawnByDistance(player, CsTeam.Terrorist, nearest: false);
    }

    /// <summary>
    /// 枚举指定队伍的竞技模式出生点实体。
    /// CT 用 info_player_counterterrorist，T 用 info_player_terrorist。
    /// 参考 MEngZy PracticeMode.cs GetSpawns 实现：仅保留 Enabled 且 Priority 最小的那批出生点
    /// （CS2 中竞技模式出生点的 Priority 通常为 1，普通出生点为 0；minPriority 初始值 1 的设计
    /// 使得优先使用 Priority=1 的竞技出生点，若无则回退到 Priority=0 的普通出生点）。
    /// </summary>
    /// <param name="team">目标队伍。</param>
    /// <returns>竞技模式出生点列表（可能为空）。</returns>
    private static List<SpawnPoint> GetSpawns(CsTeam team)
    {
        var designerName = team == CsTeam.CounterTerrorist
            ? "info_player_counterterrorist"
            : "info_player_terrorist";
        var allSpawns = Utilities.FindAllEntitiesByDesignerName<SpawnPoint>(designerName);

        // 第一遍：找出 Enabled 出生点中 Priority 的最小值
        // minPriority 初始为 1（与 MEngZy 一致），优先使用竞技出生点（Priority=1），
        // 若存在 Priority<1（即 0）的则回退到普通出生点
        int minPriority = 1;
        foreach (var spawn in allSpawns)
        {
            if (spawn.IsValid && spawn.Enabled && spawn.Priority < minPriority)
            {
                minPriority = spawn.Priority;
            }
        }

        // 第二遍：筛选 Priority == minPriority 的出生点
        var result = new List<SpawnPoint>();
        foreach (var spawn in allSpawns)
        {
            if (spawn.IsValid && spawn.Enabled && spawn.Priority == minPriority)
            {
                result.Add(spawn);
            }
        }

        Server.PrintToConsole($"[PracLab] {DateTime.Now:HH:mm:ss} Spawn {team} found {result.Count} competitive spawn points (minPriority={minPriority}, total={allSpawns.Count()})");
        return result;
    }

    /// <summary>
    /// 通用：传送到指定队伍的第 N 个出生点（N 从 1 开始，缺省 1，越界提示）。
    /// </summary>
    /// <param name="player">玩家。</param>
    /// <param name="args">参数字符串（数字）。</param>
    /// <param name="team">目标队伍。</param>
    private void HandleSpawnTeleport(CCSPlayerController player, string args, CsTeam team)
    {
        var spawns = GetSpawns(team);
        if (spawns.Count == 0)
        {
            player.PrintToChat(Localizer.ForPlayer(player, "spawn.none"));
            Server.PrintToConsole($"[PracLab] {DateTime.Now:HH:mm:ss} Spawn {player.PlayerName} no {team} spawn point found");
            return;
        }

        var n = (int.TryParse(args, out var val) && val > 0) ? val : 1;
        if (n > spawns.Count)
        {
            player.PrintToChat(Localizer.ForPlayer(player, "spawn.out_of_range", spawns.Count));
            Server.PrintToConsole($"[PracLab] {DateTime.Now:HH:mm:ss} Spawn {player.PlayerName} spawn index {n} out of range (total {spawns.Count})");
            return;
        }

        var spawn = spawns[n - 1];
        if (TeleportPlayerTo(player, spawn.AbsOrigin, spawn.AbsRotation))
        {
            var teamName = team == CsTeam.CounterTerrorist ? "CT" : "T";
            player.PrintToChat(Localizer.ForPlayer(player, "spawn.teleported", teamName, n));
            Server.PrintToConsole($"[PracLab] {DateTime.Now:HH:mm:ss} Spawn {player.PlayerName} teleported to {team} spawn point {n}");
        }
    }

    /// <summary>
    /// 通用：按距离传送（最近或最远）。
    /// </summary>
    /// <param name="player">玩家。</param>
    /// <param name="team">目标队伍。</param>
    /// <param name="nearest">true=最近，false=最远。</param>
    private void HandleSpawnByDistance(CCSPlayerController player, CsTeam team, bool nearest)
    {
        var spawns = GetSpawns(team);
        if (spawns.Count == 0)
        {
            player.PrintToChat(Localizer.ForPlayer(player, "spawn.none"));
            Server.PrintToConsole($"[PracLab] {DateTime.Now:HH:mm:ss} Spawn {player.PlayerName} no {team} spawn point found");
            return;
        }

        var pawn = player.PlayerPawn.Value;
        if (pawn == null || !pawn.IsValid || pawn.AbsOrigin == null)
        {
            player.PrintToChat(Localizer.ForPlayer(player, "spawn.no_pawn"));
            Server.PrintToConsole($"[PracLab] {DateTime.Now:HH:mm:ss} Warning Spawn {player.PlayerName} pawn/position unavailable");
            return;
        }

        var playerPos = pawn.AbsOrigin;
        SpawnPoint? target = null;
        float bestDist = nearest ? float.MaxValue : float.MinValue;

        foreach (var spawn in spawns)
        {
            if (spawn.AbsOrigin == null) continue;
            var dist = DistanceSquared(playerPos, spawn.AbsOrigin);
            if (nearest)
            {
                if (dist < bestDist) { bestDist = dist; target = spawn; }
            }
            else
            {
                if (dist > bestDist) { bestDist = dist; target = spawn; }
            }
        }

        if (target == null || target.AbsOrigin == null)
        {
            player.PrintToChat(Localizer.ForPlayer(player, "spawn.none"));
            return;
        }

        if (TeleportPlayerTo(player, target.AbsOrigin, target.AbsRotation))
        {
            var teamName = team == CsTeam.CounterTerrorist ? "CT" : "T";
            player.PrintToChat(Localizer.ForPlayer(player, nearest ? "spawn.nearest" : "spawn.farthest", teamName));
            Server.PrintToConsole($"[PracLab] {DateTime.Now:HH:mm:ss} Spawn {player.PlayerName} teleported to {team} {(nearest ? "nearest" : "farthest")} spawn point");
        }
    }
}
