using System.Drawing;
using System.Globalization;
using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Core.Translations;
using CounterStrikeSharp.API.Modules.Utils;
using CS2TraceRay.Class;
using Contents = CS2TraceRay.Enum.Contents;
using TraceMask = CS2TraceRay.Enum.TraceMask;

namespace PracLab;

/// <summary>
/// 投掷物探测结果模块（T16）。
/// 提供 .nadelist/.nl（控制台结果表格）、.nadeclearlist/.ncl（清除结果与可视化）、
/// .nadegoto/.ng &lt;ID&gt;（传送到站位快照 + 视角对准描点角 + 重建描点十字与弹道 beam 预览）。
/// 结果数据由搜索模块（T15）写入 _nadeResults，本模块负责展示与传送。
/// </summary>
public partial class PracLab
{
    /// <summary>
    /// 站立眼位高度（游戏单位），眼位 - 此值 ≈ 脚底（贴地失败时的退化值）。
    /// </summary>
    private const float StandEyeHeight = 64.0f;

    // ==================== 命令处理 ====================

    /// <summary>
    /// .nadelist/.nl 命令处理器：玩家控制台输出结果表格（ID / 投掷方法 / 道具类型 / 描点角度）。
    /// 空列表时聊天提示，不输出表格。
    /// </summary>
    /// <param name="player">调用者玩家。</param>
    /// <param name="args">命令参数（未使用）。</param>
    private void HandleNadeList(CCSPlayerController player, string args)
    {
        Server.PrintToConsole("[PracLab] HandleNadeList: executing...");

        var steamId = player.SteamID;

        if (!_nadeResults.TryGetValue(steamId, out var results) || results.Count == 0)
        {
            player.PrintToChat(Localizer.ForPlayer(player, "naderesult.list_empty"));
            return;
        }

        player.PrintToConsole(Localizer.ForPlayer(player, "naderesult.list_header"));
        foreach (var r in results)
        {
            var modeName = GetModeLocalizedName(player, r.Mode);
            var strengthName = GetStrengthLocalizedName(player, r.Strength);
            player.PrintToConsole(Localizer.ForPlayer(player, "naderesult.list_row", r.Id, modeName, strengthName, GetNadeDisplayName(player, r.Nade), r.Yaw, r.Pitch));
        }

        player.PrintToChat(Localizer.ForPlayer(player, "naderesult.list_hint", results.Count));
        Server.PrintToConsole($"[PracLab] {DateTime.Now:HH:mm:ss} NadeResult {player.PlayerName} listed {results.Count} results");
    }

    /// <summary>
    /// .nadeclearlist/.ncl 命令处理器：清除本人全部结果及其可视化实体并提示。
    /// </summary>
    /// <param name="player">调用者玩家。</param>
    /// <param name="args">命令参数（未使用）。</param>
    private void HandleNadeClearList(CCSPlayerController player, string args)
    {
        Server.PrintToConsole("[PracLab] HandleNadeClearList: executing...");

        var steamId = player.SteamID;

        int cleared = ClearNadeResults(steamId);

        player.PrintToChat(Localizer.ForPlayer(player, "naderesult.cleared", cleared));
        Server.PrintToConsole($"[PracLab] {DateTime.Now:HH:mm:ss} NadeResult {player.PlayerName} cleared {cleared} results");
    }

    /// <summary>
    /// .nadegoto/.ng &lt;ID&gt; 命令处理器：校验 ID → 传送站位快照（z 向下贴地）→ 视角对准描点角 →
    /// 重建可视化（描点十字 + 弹道 beam 折线，替换旧可视化）→ 按投掷方式/力度输出差异化提示。
    /// </summary>
    /// <param name="player">调用者玩家。</param>
    /// <param name="args">结果 ID（如 SMK-N-L-01）。</param>
    private void HandleNadeGoto(CCSPlayerController player, string args)
    {
        Server.PrintToConsole("[PracLab] HandleNadeGoto: executing...");

        var steamId = player.SteamID;
        var input = args.Trim();

        if (input.Length == 0)
        {
            player.PrintToChat(Localizer.ForPlayer(player, "naderesult.goto_usage"));
            return;
        }

        if (!_nadeResults.TryGetValue(steamId, out var results) || results.Count == 0)
        {
            player.PrintToChat(Localizer.ForPlayer(player, "naderesult.list_empty"));
            return;
        }

        // ID 校验（大小写不敏感）
        NadeResult? result = null;
        foreach (var r in results)
        {
            if (string.Equals(r.Id, input, StringComparison.OrdinalIgnoreCase))
            {
                result = r;
                break;
            }
        }

        if (result == null)
        {
            player.PrintToChat(Localizer.ForPlayer(player, "naderesult.goto_not_found", input));
            return;
        }

        var pawn = player.PlayerPawn.Value;
        if (pawn == null || !pawn.IsValid || pawn.Health <= 0)
        {
            Server.PrintToConsole($"[PracLab] {DateTime.Now:HH:mm:ss} Warning NadeGoto pawn invalid or dead for {player.PlayerName}");
            return;
        }

        try
        {
            // 传送：眼位向下 trace 贴地；贴地失败（站位悬空/地图外）退化为眼位 - 站立眼高
            var eyePos = result.EyePos;
            var eyeVec = ToCssVector(eyePos);
            var ground = TraceDownGround(eyeVec, pawn.Handle);
            var feet = ground ?? new Vector(eyePos.X, eyePos.Y, eyePos.Z - StandEyeHeight);

            // 传送：使用 setpos + setang 客户端命令。
            // pawn.Teleport 会把 qangle.X 同时写入 eye angles 和 CGameSceneNode.m_angRotation（身体本地旋转源），
            // m_angRotation.X=pitch 导致身体绕 X 轴翻转（躺倒）+ 第一人称相机异常，且移动视角时引擎持续重同步。
            // setpos 只设位置、setang 只设 eye angles，均不写 m_angRotation → 身体保持直立、视角对准描点角。
            // 需 sv_cheats 1（prac.cfg 已开启）。
            var inv = CultureInfo.InvariantCulture;
            player.ExecuteClientCommandFromServer($"setpos {feet.X.ToString("F4", inv)} {feet.Y.ToString("F4", inv)} {feet.Z.ToString("F4", inv)}");
            player.ExecuteClientCommandFromServer($"setang {result.Pitch.ToString("F4", inv)} {result.Yaw.ToString("F4", inv)}");

            // 重建可视化：.ng 同一时刻只展示一条预测轨迹，先清除本人全部旧可视化（含上一次 .ng 的弹道 beam），再重建当前结果
            foreach (var r in results)
                ClearEntityList(r.VisualEntities);
            RebuildResultVisualization(result, feet);

            // --- 终端调试输出：目标框坐标 + 预测轨迹 ---
            PrintNadeGotoDebug(player, result);

            // 方式/力度差异化提示
            var strengthName = GetStrengthLocalizedName(player, result.Strength);
            var hintKey = result.Mode switch
            {
                ThrowMode.Normal => "naderesult.goto_normal",
                ThrowMode.Jump => "naderesult.goto_jump",
                ThrowMode.RunJump => "naderesult.goto_runjump",
                ThrowMode.Duck => "naderesult.goto_duck",
                ThrowMode.DuckJump => "naderesult.goto_duckjump",
                _ => "naderesult.goto_duckrunjump",
            };
            player.PrintToChat(Localizer.ForPlayer(player, hintKey, result.Id, strengthName));

            Server.PrintToConsole($"[PracLab] {DateTime.Now:HH:mm:ss} NadeResult {player.PlayerName} goto {result.Id} yaw={result.Yaw:F1} pitch={result.Pitch:F1}");
        }
        catch (Exception ex)
        {
            Server.PrintToConsole($"[PracLab] {DateTime.Now:HH:mm:ss} Error NadeGoto failed - {ex.Message}");
            player.PrintToChat(Localizer.ForPlayer(player, "naderesult.goto_failed"));
        }
    }

    // ==================== 内部辅助 ====================

    /// <summary>
    /// 终端调试输出：打印搜索目标框三维坐标、预测轨迹采样点、仿真终止诊断。
    /// 用于对比预测轨迹与实际轨迹的差异，定位"预测与实际不符"问题。
    /// 输出到服务器终端（Server.PrintToConsole），避免刷玩家控制台。
    /// </summary>
    /// <param name="player">目标玩家（仅用于日志标识）。</param>
    /// <param name="result">搜索结果条目（含目标框和轨迹数据）。</param>
    private void PrintNadeGotoDebug(CCSPlayerController player, NadeResult result)
    {
        // 目标框三维坐标
        var bMin = result.BoxMin;
        var bMax = result.BoxMax;
        var center = (bMin + bMax) * 0.5f;
        var size = bMax - bMin;
        var ts = DateTime.Now.ToString("HH:mm:ss");

        Server.PrintToConsole($"[PracLab] {ts} ===== NadeGoto 调试开始 ===== player={player.PlayerName} id={result.Id}");
        Server.PrintToConsole($"[PracLab] {ts} NadeGoto target box: min=({bMin.X:F1},{bMin.Y:F1},{bMin.Z:F1}) max=({bMax.X:F1},{bMax.Y:F1},{bMax.Z:F1}) center=({center.X:F1},{center.Y:F1},{center.Z:F1}) size=({size.X:F1},{size.Y:F1},{size.Z:F1})");

        // 预测轨迹
        var traj = result.Trajectory;
        var land = result.LandPoint;
        var origin = result.ThrowOrigin;
        Server.PrintToConsole($"[PracLab] {ts} NadeGoto trajectory: {traj.Count} points, origin=({origin.X:F1},{origin.Y:F1},{origin.Z:F1}) land=({land.X:F1},{land.Y:F1},{land.Z:F1}) term={result.TermReason} flight={result.FlightTime:F3}s");
        for (int i = 0; i < traj.Count; i++)
        {
            var p = traj[i];
            string tag = (i == traj.Count - 1) ? " [END]" : "";
            Server.PrintToConsole($"[PracLab] {ts}   traj[{i}] = ({p.X:F1},{p.Y:F1},{p.Z:F1}){tag}");
        }

        // 前 3 次碰撞详情（定位"弹墙方向与真实不符"：对比 rawN 与 probeN，确认法线修正是否反了方向）
        Server.PrintToConsole($"[PracLab] {ts} NadeGoto bounce log ({result.BounceLog.Count} entries):");
        foreach (var entry in result.BounceLog)
        {
            Server.PrintToConsole($"[PracLab] {ts}   {entry}");
        }
        Server.PrintToConsole($"[PracLab] {ts} ===== NadeGoto 调试结束 =====");
    }

    /// <summary>
    /// 清除指定玩家的全部搜索结果及其可视化实体（.ncl / .cdr 联动 / 换图清理共用）。
    /// </summary>
    /// <param name="steamId">玩家 SteamID。</param>
    /// <returns>清除的结果条数。</returns>
    private int ClearNadeResults(ulong steamId)
    {
        if (!_nadeResults.TryGetValue(steamId, out var results))
            return 0;

        int cleared = results.Count;
        foreach (var r in results)
            ClearEntityList(r.VisualEntities);
        _nadeResults.Remove(steamId);
        return cleared;
    }

    /// <summary>
    /// 重建结果可视化：描点十字（站位处）+ 完整弹道 beam 折线（眼位 → 轨迹采样点 → 落点）。
    /// 先清除该结果的旧可视化实体再重建。
    /// </summary>
    /// <param name="result">目标结果。</param>
    /// <param name="feetPos">传送后的脚底位置（十字中心，z 加偏移防埋地）。</param>
    private void RebuildResultVisualization(NadeResult result, Vector feetPos)
    {
        ClearEntityList(result.VisualEntities);

        // 描点十字（青色，与区域绿框/红色预览区分）
        var crossCenter = new Vector(feetPos.X, feetPos.Y, feetPos.Z + RegionBeamZOffset);
        AddCrossMarker(result.VisualEntities, crossCenter, Color.Cyan);

        // 弹道 beam 折线：眼位 → 各采样点
        var points = result.Trajectory;
        if (points.Count == 0)
            return;

        var prev = ToCssVector(result.EyePos);
        foreach (var p in points)
        {
            var cur = ToCssVector(p);
            AddDrawBeam(result.VisualEntities, prev, cur, Color.Cyan);
            prev = cur;
        }
    }

    /// <summary>
    /// 从指定位置垂直向下 trace 找地面（跳过指定实体），未命中返回 null。
    /// </summary>
    /// <param name="from">起始位置。</param>
    /// <param name="skip">跳过的实体句柄（玩家自身 pawn）。</param>
    /// <returns>地面命中点；未命中为 null。</returns>
    private static Vector? TraceDownGround(Vector from, nint skip)
    {
        var downEnd = new Vector(from.X, from.Y, from.Z - TraceDistance);
        var down = TraceRay.TraceShape(from, downEnd, (ulong)TraceMask.MaskSolid, (ulong)Contents.NoDraw, skip);
        return down.Fraction < 1.0f ? ToCssVector(down.Position) : null;
    }

    /// <summary>
    /// 投掷方式本地化名称（普通/跳投/前跳投/蹲投/蹲跳投/蹲前跳投）。
    /// </summary>
    private string GetModeLocalizedName(CCSPlayerController player, ThrowMode mode) => Localizer.ForPlayer(player, mode switch
    {
        ThrowMode.Normal => "naderesult.mode_normal",
        ThrowMode.Jump => "naderesult.mode_jump",
        ThrowMode.RunJump => "naderesult.mode_runjump",
        ThrowMode.Duck => "naderesult.mode_duck",
        ThrowMode.DuckJump => "naderesult.mode_duckjump",
        _ => "naderesult.mode_duckrunjump",
    });

    /// <summary>
    /// 投掷力度本地化名称（左键/左右键同按/右键）。
    /// </summary>
    private string GetStrengthLocalizedName(CCSPlayerController player, ThrowStrength strength) => Localizer.ForPlayer(player, strength switch
    {
        ThrowStrength.Left => "naderesult.strength_left",
        ThrowStrength.Mid => "naderesult.strength_mid",
        _ => "naderesult.strength_right",
    });
}
