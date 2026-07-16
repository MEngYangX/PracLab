using System.Drawing;
using System.Globalization;
using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Core.Translations;
using CounterStrikeSharp.API.Modules.Utils;

namespace PracLab;

/// <summary>
/// 出生点方框可视化与 E 键传送功能。
/// 参考 MEngZy PracticeMode.cs 的 ShowSpawnBeam / CreateBeamLine / GetSpawnPlayerIsLookingAt 实现。
/// prac 模式开启时自动显示 CT（蓝色）/ T（橙色）双方出生点方框，玩家对准方框按 E 键可传送。
/// </summary>
public partial class PracLab
{
    /// <summary>
    /// 方框半边长（与 MEngZy SPAWN_SQUARE_SIZE 一致）。
    /// </summary>
    private const float SpawnSquareSize = 20.0f;

    /// <summary>
    /// 射线-点距离阈值，小于此值视为玩家瞄准了该方框。
    /// </summary>
    private const float SpawnLookThreshold = 30.0f;

    /// <summary>
    /// 最大瞄准距离（单位：游戏单位）。
    /// </summary>
    private const float SpawnLookMaxDistance = 300.0f;

    /// <summary>
    /// E 键冷却时间（秒），避免按住 E 键连续传送。
    /// </summary>
    private const float SpawnUseCooldownSeconds = 1.0f;

    /// <summary>
    /// CS2 玩家眼睛距离脚底的偏移高度。
    /// </summary>
    private const float PlayerEyeHeight = 64.0f;

    /// <summary>
    /// 方框映射：spawnId("x_y_z") -> 出生点信息（位置/角度/队伍/索引）。
    /// 用于 E 键瞄准检测时反查目标出生点。
    /// </summary>
    private readonly Dictionary<string, SpawnMarkerInfo> _spawnMarkers = new();

    /// <summary>
    /// 已创建的 beam 实体列表，用于精确清理（避免 FindAllEntitiesByDesignerName 误伤其他 beam）。
    /// </summary>
    private readonly List<CBeam> _spawnBeamEntities = new();

    /// <summary>
    /// 玩家 E 键冷却时间戳（按 UserId 索引）。
    /// </summary>
    private readonly Dictionary<int, DateTime> _spawnUseCooldown = new();

    /// <summary>
    /// OnTick 监听器是否已注册（Listeners.OnTick 一旦注册无法注销，用此标志守卫）。
    /// </summary>
    private bool _isSpawnTickRegistered;

    /// <summary>
    /// 出生点方框信息记录。
    /// </summary>
    /// <param name="Position">出生点世界坐标。</param>
    /// <param name="Angle">出生点角度。</param>
    /// <param name="Team">所属队伍。</param>
    /// <param name="Index">在本队伍出生点列表中的索引（从 0 开始，传送时显示 N+1）。</param>
    private readonly record struct SpawnMarkerInfo(
        Vector Position,
        QAngle Angle,
        CsTeam Team,
        int Index);

    /// <summary>
    /// 显示 CT 和 T 双方所有出生点方框，并填充 _spawnMarkers 映射。
    /// 在 prac 模式开启或 .showspawns 命令时调用。
    /// </summary>
    private void ShowAllSpawnBeams()
    {
        Server.PrintToConsole("[PracLab] ShowAllSpawnBeams: executing...");

        // 清除上一轮残留
        RemoveSpawnBeams();

        try
        {
            // CT 出生点：绿色
            var ctSpawns = GetSpawns(CsTeam.CounterTerrorist);
            for (int i = 0; i < ctSpawns.Count; i++)
            {
                if (ctSpawns[i].AbsOrigin != null)
                    ShowSpawnBeam(ctSpawns[i], Color.LimeGreen, CsTeam.CounterTerrorist, i);
            }

            // T 出生点：绿色
            var tSpawns = GetSpawns(CsTeam.Terrorist);
            for (int i = 0; i < tSpawns.Count; i++)
            {
                if (tSpawns[i].AbsOrigin != null)
                    ShowSpawnBeam(tSpawns[i], Color.LimeGreen, CsTeam.Terrorist, i);
            }

            Server.PrintToConsole($"[PracLab] {DateTime.Now:HH:mm:ss} SpawnMarker shown {ctSpawns.Count} CT + {tSpawns.Count} T spawn beams (green)");
        }
        catch (Exception ex)
        {
            Server.PrintToConsole($"[PracLab] {DateTime.Now:HH:mm:ss} Error ShowAllSpawnBeams failed - {ex.Message}");
        }
    }

    /// <summary>
    /// 为单个出生点绘制正方形方框（4 条 beam 边）。
    /// 参考 MEngZy PracticeMode.cs ShowSpawnBeam 实现。
    /// </summary>
    /// <param name="spawn">出生点实体。</param>
    /// <param name="color">方框颜色。</param>
    /// <param name="team">所属队伍。</param>
    /// <param name="index">在本队伍出生点列表中的索引。</param>
    private void ShowSpawnBeam(SpawnPoint spawn, Color color, CsTeam team, int index)
    {
        var pos = spawn.AbsOrigin!;
        var ang = spawn.AbsRotation ?? new QAngle(0, 0, 0);

        float x = pos.X;
        float y = pos.Y;
        float z = pos.Z;

        // 生成唯一标识符（坐标字符串）
        string spawnId = $"{x}_{y}_{z}";
        _spawnMarkers[spawnId] = new SpawnMarkerInfo(pos, ang, team, index);

        // 计算正方形 4 个顶点（在 X-Y 平面，Z 不变）
        Vector p1 = new(x + SpawnSquareSize, y + SpawnSquareSize, z);
        Vector p2 = new(x + SpawnSquareSize, y - SpawnSquareSize, z);
        Vector p3 = new(x - SpawnSquareSize, y + SpawnSquareSize, z);
        Vector p4 = new(x - SpawnSquareSize, y - SpawnSquareSize, z);

        // 绘制 4 条边
        CreateBeamLine(p1, p2, color);
        CreateBeamLine(p2, p4, color);
        CreateBeamLine(p4, p3, color);
        CreateBeamLine(p3, p1, color);
    }

    /// <summary>
    /// 创建一条 beam 线段实体。
    /// 参考 MEngZy PracticeMode.cs CreateBeamLine 实现。
    /// </summary>
    /// <param name="start">起点世界坐标。</param>
    /// <param name="end">终点世界坐标。</param>
    /// <param name="color">线段颜色。</param>
    private void CreateBeamLine(Vector start, Vector end, Color color)
    {
        var beam = Utilities.CreateEntityByName<CBeam>("beam");
        if (beam == null || !beam.IsValid)
        {
            Server.PrintToConsole($"[PracLab] {DateTime.Now:HH:mm:ss} Warning CreateBeamLine failed to create beam entity");
            return;
        }

        beam.LifeState = 1;       // 保持存活
        beam.Width = 1;           // 线宽
        beam.Render = color;

        // 设置起点
        beam.Teleport(start, new QAngle(0, 0, 0), new Vector(0, 0, 0));

        // 设置终点（EndPos 各分量单独赋值）
        beam.EndPos.X = end.X;
        beam.EndPos.Y = end.Y;
        beam.EndPos.Z = end.Z;

        beam.DispatchSpawn();
        _spawnBeamEntities.Add(beam);
    }

    /// <summary>
    /// 清除所有出生点方框 beam 实体并清空映射。
    /// 使用 _spawnBeamEntities 精确清理，避免误伤其他插件创建的 beam。
    /// </summary>
    private void RemoveSpawnBeams()
    {
        Server.PrintToConsole("[PracLab] RemoveSpawnBeams: executing...");

        foreach (var beam in _spawnBeamEntities)
        {
            try
            {
                if (beam != null && beam.IsValid)
                    beam.Remove();
            }
            catch { /* 单个 beam 移除失败不应中断清理 */ }
        }

        _spawnBeamEntities.Clear();
        _spawnMarkers.Clear();

        Server.PrintToConsole($"[PracLab] {DateTime.Now:HH:mm:ss} SpawnMarker all beams removed");
    }

    /// <summary>
    /// 注册 OnTick 监听器检测 E 键按下（仅注册一次）。
    /// 回调内通过 _isPracMode 与 _spawnMarkers.Count 短路返回，避免 prac 模式关闭时无效运算。
    /// </summary>
    private void RegisterSpawnUseKeyListener()
    {
        if (_isSpawnTickRegistered) return;

        RegisterListener<Listeners.OnTick>(() =>
        {
            // 短路：prac 模式关闭或无方框时直接返回
            if (!_isPracMode || _spawnMarkers.Count == 0) return;

            try
            {
                foreach (var player in Utilities.GetPlayers())
                {
                    if (player == null || !player.IsValid) continue;
                    if (player.IsBot || player.IsHLTV) continue;
                    if (player.UserId == null) continue;

                    // 检测 E 键（使用键）
                    if ((player.Buttons & PlayerButtons.Use) == 0) continue;

                    int userId = player.UserId.Value;

                    // 冷却检查（1 秒）
                    if (_spawnUseCooldown.TryGetValue(userId, out var lastTime) &&
                        (DateTime.Now - lastTime).TotalSeconds < SpawnUseCooldownSeconds)
                        continue;

                    // 检测玩家瞄准的出生点
                    var target = GetSpawnPlayerIsLookingAt(player);
                    if (target == null) continue;

                    // 更新冷却时间
                    _spawnUseCooldown[userId] = DateTime.Now;

                    // 传送到目标出生点
                    if (TeleportPlayerTo(player, target.Value.Position, target.Value.Angle))
                    {
                        // 聊天栏提示（复用现有 spawn.teleported 本地化键）
                        player.PrintToChat(Localizer.ForPlayer(player, "spawn.teleported", target.Value.Index + 1));

                        var teamName = target.Value.Team == CsTeam.CounterTerrorist ? "CT" : "T";
                        Server.PrintToConsole($"[PracLab] {DateTime.Now:HH:mm:ss} SpawnMarker {player.PlayerName} teleported to {teamName} spawn {target.Value.Index + 1} via E key");
                    }
                }
            }
            catch (Exception ex)
            {
                Server.PrintToConsole($"[PracLab] {DateTime.Now:HH:mm:ss} Error SpawnMarker OnTick failed - {ex.Message}");
            }
        });

        _isSpawnTickRegistered = true;
        Server.PrintToConsole($"[PracLab] {DateTime.Now:HH:mm:ss} SpawnMarker OnTick listener registered");
    }

    /// <summary>
    /// 判断玩家正在瞄准哪个出生点方框。
    /// 使用射线-点距离算法：计算从玩家眼睛出发的视线射线到每个方框中心的最短距离，取最近且小于阈值的。
    /// 参考 MEngZy PracticeMode.cs GetSpawnPlayerIsLookingAt 实现。
    /// </summary>
    /// <param name="player">玩家控制器。</param>
    /// <returns>瞄准的出生点信息，未瞄准任何方框时返回 null。</returns>
    private SpawnMarkerInfo? GetSpawnPlayerIsLookingAt(CCSPlayerController player)
    {
        var pawn = player.PlayerPawn.Value;
        if (pawn == null || !pawn.IsValid) return null;

        var pawnOrigin = pawn.AbsOrigin;
        if (pawnOrigin == null) return null;

        // 眼睛位置 = 玩家位置 + (0, 0, 64)
        Vector eyePosition = new(pawnOrigin.X, pawnOrigin.Y, pawnOrigin.Z + PlayerEyeHeight);

        // 视线方向（从 EyeAngles 转换）
        var eyeAngles = pawn.EyeAngles;
        Vector forward = QAngleToForwardVector(eyeAngles);

        // 射线终点 = 眼睛位置 + 方向 * 最大距离
        Vector rayEnd = new(
            eyePosition.X + forward.X * SpawnLookMaxDistance,
            eyePosition.Y + forward.Y * SpawnLookMaxDistance,
            eyePosition.Z + forward.Z * SpawnLookMaxDistance);

        SpawnMarkerInfo? closest = null;
        float closestDist = float.MaxValue;

        foreach (var entry in _spawnMarkers.Values)
        {
            // 射线-点最短距离
            float dist = DistanceFromPointToRay(entry.Position, eyePosition, rayEnd);

            // 距离眼睛也需小于最大瞄准距离
            float eyeDist = DistanceSquared(eyePosition, entry.Position);
            if (eyeDist > SpawnLookMaxDistance * SpawnLookMaxDistance) continue;

            if (dist < SpawnLookThreshold && dist < closestDist)
            {
                closestDist = dist;
                closest = entry;
            }
        }

        return closest;
    }

    /// <summary>
    /// 计算点到射线（线段）的最短距离。
    /// 射线起点为 rayStart，终点为 rayEnd，视为线段处理。
    /// 算法：投影参数 t = dot(point-start, end-start) / |end-start|^2，t 钳制到 [0,1]，最近点 = start + t*(end-start)。
    /// </summary>
    /// <param name="point">待计算的点。</param>
    /// <param name="rayStart">射线起点。</param>
    /// <param name="rayEnd">射线终点。</param>
    /// <returns>点到线段的最短距离。</returns>
    private static float DistanceFromPointToRay(Vector point, Vector rayStart, Vector rayEnd)
    {
        float dx = rayEnd.X - rayStart.X;
        float dy = rayEnd.Y - rayStart.Y;
        float dz = rayEnd.Z - rayStart.Z;

        float segmentLengthSq = dx * dx + dy * dy + dz * dz;
        if (segmentLengthSq < 1e-6f) return float.MaxValue;

        // 投影参数 t
        float t = ((point.X - rayStart.X) * dx +
                   (point.Y - rayStart.Y) * dy +
                   (point.Z - rayStart.Z) * dz) / segmentLengthSq;

        // 钳制到 [0, 1]（视为线段）
        t = Math.Max(0f, Math.Min(1f, t));

        // 最近点
        float closestX = rayStart.X + t * dx;
        float closestY = rayStart.Y + t * dy;
        float closestZ = rayStart.Z + t * dz;

        // 距离
        float diffX = point.X - closestX;
        float diffY = point.Y - closestY;
        float diffZ = point.Z - closestZ;

        return MathF.Sqrt(diffX * diffX + diffY * diffY + diffZ * diffZ);
    }

    /// <summary>
    /// 将 QAngle（pitch X, yaw Y, roll Z）转换为前方向向量。
    /// 参考 MEngZy QAngleToVector 实现：
    ///   forward.X = cos(pitch) * cos(yaw)
    ///   forward.Y = cos(pitch) * sin(yaw)
    ///   forward.Z = -sin(pitch)
    /// 输入角度需从度转弧度。
    /// </summary>
    /// <param name="angles">玩家眼睛角度（pitch/yaw/roll）。</param>
    /// <returns>单位前方向向量。</returns>
    private static Vector QAngleToForwardVector(QAngle angles)
    {
        float pitchRad = angles.X * (MathF.PI / 180f);
        float yawRad = angles.Y * (MathF.PI / 180f);

        float cosPitch = MathF.Cos(pitchRad);
        float sinPitch = MathF.Sin(pitchRad);
        float cosYaw = MathF.Cos(yawRad);
        float sinYaw = MathF.Sin(yawRad);

        return new Vector(cosPitch * cosYaw, cosPitch * sinYaw, -sinPitch);
    }

    /// <summary>
    /// .showspawns 命令处理器：重新显示双方出生点方框。
    /// </summary>
    private void HandleShowSpawns(CCSPlayerController player, string args)
    {
        Server.PrintToConsole("[PracLab] HandleShowSpawns: executing...");

        ShowAllSpawnBeams();
        RegisterSpawnUseKeyListener();

        player.PrintToChat(Localizer.ForPlayer(player, "spawn.showspawns"));
        Server.PrintToConsole($"[PracLab] {DateTime.Now:HH:mm:ss} SpawnMarker {player.PlayerName} showed spawn beams");
    }

    /// <summary>
    /// .hidespawns 命令处理器：隐藏所有出生点方框。
    /// </summary>
    private void HandleHideSpawns(CCSPlayerController player, string args)
    {
        Server.PrintToConsole("[PracLab] HandleHideSpawns: executing...");

        RemoveSpawnBeams();

        player.PrintToChat(Localizer.ForPlayer(player, "spawn.hidespawns"));
        Server.PrintToConsole($"[PracLab] {DateTime.Now:HH:mm:ss} SpawnMarker {player.PlayerName} hid spawn beams");
    }
}
