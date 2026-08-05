using System.Drawing;
using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Core.Translations;
using CounterStrikeSharp.API.Modules.Utils;
using CS2TraceRay.Class;
using CGameTrace = CS2TraceRay.Struct.CGameTrace;
using Contents = CS2TraceRay.Enum.Contents;
using TraceMask = CS2TraceRay.Enum.TraceMask;

namespace PracLab;

/// <summary>
/// 投掷物目标区域绘制模块（T14，立方体化）。
/// 玩家通过 .nadedraw 进入三段式立方体绘制会话（P1 底面角点 → P2 底面斜对角 → P3 高度点），
/// 左键确认 / 右键取消；红色 beam 实时预览，入库后转为绿色 12 边线框 + 底面角十字 + 底面编号文本。
/// P1/P2 取点基于 CS2TraceRay 引擎射线：命中面法线 z &gt; 0.7 视为地面直接取点，否则按空中绘制垂直投影到地面；
/// P3 为空中取点：视线命中点或未命中视线上 2000 单位处，直接取其 z 作为盒体顶面高度，不做投影。
/// </summary>
public partial class PracLab
{
    /// <summary>
    /// 每个玩家的目标区域数量上限。
    /// </summary>
    private const int MaxRegionsPerPlayer = 8;

    /// <summary>
    /// 预览刷新节流间隔（秒），限制 beam 实体重建开销。
    /// </summary>
    private const double PreviewThrottleSeconds = 0.1;

    /// <summary>
    /// 底面角点 P1 与斜对角 P2 的最小 2D 距离（游戏单位），小于此值拒绝取点。
    /// </summary>
    private const float MinDrawEdgeLength = 8.0f;

    /// <summary>
    /// 命中面法线 Z 分量阈值，大于此值判定为地面。
    /// </summary>
    private const float GroundNormalZThreshold = 0.7f;

    /// <summary>
    /// 引擎射线长度（游戏单位）。
    /// </summary>
    private const float TraceDistance = 8192.0f;

    /// <summary>
    /// 视线未命中时空中点的取点距离（游戏单位）。
    /// </summary>
    private const float AirPointDistance = 2000.0f;

    /// <summary>
    /// 十字标记的半边长（游戏单位）。
    /// </summary>
    private const float CrossHalfSize = 6.0f;

    /// <summary>
    /// 常驻 beam / 十字相对地面高度的 Z 偏移（防止埋入地面）。
    /// </summary>
    private const float RegionBeamZOffset = 2.0f;

    /// <summary>
    /// 区域编号文本相对底面 beam 平面的 Z 偏移（与出生点序号文本一致，贴地且避免 Z-fighting）。
    /// </summary>
    private const float RegionLabelZOffset = 2.0f;

    /// <summary>
    /// 投影指示实体的存活时间（秒）。
    /// </summary>
    private const float IndicatorDurationSeconds = 2.0f;

    /// <summary>
    /// 进行中的绘制会话（按 SteamID 索引）。
    /// </summary>
    private readonly Dictionary<ulong, DrawSession> _drawSessions = new();

    /// <summary>
    /// 已入库的目标区域列表（按 SteamID 索引）。
    /// </summary>
    private readonly Dictionary<ulong, List<TargetRegion>> _targetRegions = new();

    /// <summary>
    /// OnTick 监听器是否已注册（Listeners.OnTick 一旦注册无法注销，用此标志守卫）。
    /// </summary>
    private bool _isDrawTickRegistered;

    /// <summary>
    /// 绘制会话阶段：等待底面角点 P1 → 等待底面斜对角 P2 → 等待高度点 P3。
    /// </summary>
    private enum DrawStage
    {
        WaitP1,
        WaitP2,
        WaitP3,
    }

    /// <summary>
    /// 单个玩家的进行中绘制会话状态。
    /// </summary>
    private sealed class DrawSession
    {
        /// <summary>当前阶段。</summary>
        public DrawStage Stage = DrawStage.WaitP1;

        /// <summary>已确认的底面角点 P1。</summary>
        public Vector? CornerA;

        /// <summary>已确认的底面斜对角 P2。</summary>
        public Vector? CornerB;

        /// <summary>上一 tick 的按键快照，用于按下沿检测。</summary>
        public PlayerButtons LastButtons;

        /// <summary>红色预览实体列表（每次刷新整体重建）。</summary>
        public List<CEntityInstance> PreviewEntities = new(16);

        /// <summary>投影指示实体列表（竖直 beam + 空中十字，由 2 秒定时器回收）。</summary>
        public List<CEntityInstance> IndicatorEntities = new(4);

        /// <summary>上次预览刷新时间。</summary>
        public DateTime LastPreviewRefresh = DateTime.MinValue;
    }

    /// <summary>
    /// 已入库的目标区域：3D AABB 立方体（Min/Max 盒体）+ 常驻实体。
    /// </summary>
    private sealed class TargetRegion
    {
        /// <summary>区域编号（从 1 递增，显示 R{Id}）。</summary>
        public int Id;

        /// <summary>所属地图名。</summary>
        public string MapName = string.Empty;

        /// <summary>盒体最小角（min x, min y, 底面 z）。</summary>
        public Vector Min = new(0, 0, 0);

        /// <summary>盒体最大角（max x, max y, 顶面 z）。</summary>
        public Vector Max = new(0, 0, 0);

        /// <summary>常驻实体列表（绿色 12 边线框 + 底面角十字 + 底面编号文本）。</summary>
    public List<CEntityInstance> Entities = new(16);

        /// <summary>
        /// 判断点是否在目标区域内：纯 3D 盒体逐分量包含判定（min ≤ p ≤ max）。
        /// 供搜索模块命中判定复用。
        /// </summary>
        /// <param name="p">待判定的世界坐标。</param>
        /// <returns>true 表示点在盒体内。</returns>
        public bool Contains(Vector p)
        {
            return p.X >= Min.X && p.X <= Max.X
                && p.Y >= Min.Y && p.Y <= Max.Y
                && p.Z >= Min.Z && p.Z <= Max.Z;
        }
    }

    // ==================== 命令处理 ====================

    /// <summary>
    /// .nadedraw 命令处理器：进入三段式矩形绘制会话。
    /// 区域数量达到上限（8 个）时拒绝；已有会话时先清理再重新开始。
    /// </summary>
    /// <param name="player">调用者玩家。</param>
    /// <param name="args">命令参数（未使用）。</param>
    private void HandleNadeDraw(CCSPlayerController player, string args)
    {
        Server.PrintToConsole("[PracLab] HandleNadeDraw: executing...");

        var steamId = player.SteamID;

        if (!_targetRegions.TryGetValue(steamId, out var regions))
        {
            regions = new List<TargetRegion>(MaxRegionsPerPlayer);
            _targetRegions[steamId] = regions;
        }

        // 区域数量上限校验
        if (regions.Count >= MaxRegionsPerPlayer)
        {
            player.PrintToChat(Localizer.ForPlayer(player, "nadedraw.limit_reached", MaxRegionsPerPlayer));
            Server.PrintToConsole($"[PracLab] {DateTime.Now:HH:mm:ss} NadeDraw {player.PlayerName} reached region limit ({MaxRegionsPerPlayer})");
            return;
        }

        // 已有会话：清理旧会话的预览与指示实体后重新开始
        if (_drawSessions.TryGetValue(steamId, out var oldSession))
        {
            ClearSessionEntities(oldSession);
            _drawSessions.Remove(steamId);
            Server.PrintToConsole($"[PracLab] {DateTime.Now:HH:mm:ss} NadeDraw {player.PlayerName} previous session discarded");
        }

        _drawSessions[steamId] = new DrawSession
        {
            Stage = DrawStage.WaitP1,
            LastButtons = player.Buttons,
        };

        player.PrintToChat(Localizer.ForPlayer(player, "nadedraw.enter"));
        RegisterDrawTickListener();

        Server.PrintToConsole($"[PracLab] {DateTime.Now:HH:mm:ss} NadeDraw {player.PlayerName} entered draw session ({regions.Count}/{MaxRegionsPerPlayer} regions)");
    }

    /// <summary>
    /// .cleardraw 命令处理器：取消本人会话并清除本人全部目标区域及其常驻实体。
    /// </summary>
    /// <param name="player">调用者玩家。</param>
    /// <param name="args">命令参数（未使用）。</param>
    private void HandleClearDraw(CCSPlayerController player, string args)
    {
        Server.PrintToConsole("[PracLab] HandleClearDraw: executing...");

        var steamId = player.SteamID;

        // 取消本人进行中的会话
        if (_drawSessions.TryGetValue(steamId, out var session))
        {
            ClearSessionEntities(session);
            _drawSessions.Remove(steamId);
        }

        // 移除本人全部区域常驻实体与数据
        int cleared = 0;
        if (_targetRegions.TryGetValue(steamId, out var regions))
        {
            cleared = regions.Count;
            foreach (var region in regions)
                ClearEntityList(region.Entities);
            _targetRegions.Remove(steamId);
        }

        // .cdr 联动：本人搜索结果与可视化实体一并失效
        int clearedResults = ClearNadeResults(steamId);
        if (clearedResults > 0)
            Server.PrintToConsole($"[PracLab] {DateTime.Now:HH:mm:ss} NadeDraw {player.PlayerName} cleared {clearedResults} search results (cdr linkage)");

        player.PrintToChat(Localizer.ForPlayer(player, "nadedraw.cleared", cleared));
        Server.PrintToConsole($"[PracLab] {DateTime.Now:HH:mm:ss} NadeDraw {player.PlayerName} cleared {cleared} regions");
    }

    // ==================== 状态机（OnTick 驱动） ====================

    /// <summary>
    /// 注册 OnTick 监听器驱动绘制会话状态机（仅注册一次）。
    /// 回调内通过 _drawSessions.Count 短路返回，避免无会话时无效运算。
    /// </summary>
    private void RegisterDrawTickListener()
    {
        if (_isDrawTickRegistered) return;

        RegisterListener<Listeners.OnTick>(() =>
        {
            // 短路：无进行中会话时直接返回
            if (_drawSessions.Count == 0) return;

            List<ulong>? finishedSessions = null;
            foreach (var (steamId, session) in _drawSessions)
            {
                try
                {
                    var player = FindPlayerBySteamId(steamId);
                    var pawn = player?.PlayerPawn.Value;

                    // 玩家无效或已死亡：静默取消会话（清预览与指示实体）
                    if (player == null || !player.IsValid || pawn == null || !pawn.IsValid || pawn.Health <= 0)
                    {
                        ClearSessionEntities(session);
                        (finishedSessions ??= new List<ulong>()).Add(steamId);
                        Server.PrintToConsole($"[PracLab] {DateTime.Now:HH:mm:ss} NadeDraw session cancelled (player invalid or dead) steamId={steamId}");
                        continue;
                    }

                    // 返回 true 表示会话结束（右键取消或矩形入库），延迟到遍历后移除
                    if (ProcessDrawSession(player, session))
                        (finishedSessions ??= new List<ulong>()).Add(steamId);
                }
                catch (Exception ex)
                {
                    Server.PrintToConsole($"[PracLab] {DateTime.Now:HH:mm:ss} Error NadeDraw OnTick session failed - {ex.Message}");
                }
            }

            if (finishedSessions != null)
            {
                foreach (var steamId in finishedSessions)
                    _drawSessions.Remove(steamId);
            }
        });

        _isDrawTickRegistered = true;
        Server.PrintToConsole($"[PracLab] {DateTime.Now:HH:mm:ss} NadeDraw OnTick listener registered");
    }

    /// <summary>
    /// 处理单个会话的每 tick 逻辑：按键沿检测（左键确认 / 右键取消）与节流预览刷新。
    /// </summary>
    /// <param name="player">会话所属玩家（已确认有效且存活）。</param>
    /// <param name="session">会话状态。</param>
    /// <returns>true 表示会话结束（取消或矩形入库），调用方应从 _drawSessions 移除。</returns>
    private bool ProcessDrawSession(CCSPlayerController player, DrawSession session)
    {
        // 按键沿检测：仅响应本 tick 新按下的键
        var now = player.Buttons;
        var pressed = now & ~session.LastButtons;
        session.LastButtons = now;

        // 右键：任意阶段取消会话
        if ((pressed & PlayerButtons.Attack2) != 0)
        {
            ClearSessionEntities(session);
            player.PrintToChat(Localizer.ForPlayer(player, "nadedraw.cancelled"));
            Server.PrintToConsole($"[PracLab] {DateTime.Now:HH:mm:ss} NadeDraw {player.PlayerName} cancelled draw session at stage {session.Stage}");
            return true;
        }

        // 左键：取点成功才确认（取点失败的提示由 TryPickPoint/TryPickAirPoint 内部输出）
        if ((pressed & PlayerButtons.Attack) != 0)
        {
            // P3 高度点走空中取点分支（直接取 z，不投影）；P1/P2 走地面/空中投影取点分支
            if (session.Stage == DrawStage.WaitP3)
            {
                if (TryPickAirPoint(player, out var heightPoint))
                {
                    if (TryCommitRegion(player, session, heightPoint, out int regionId))
                    {
                        player.PrintToChat(Localizer.ForPlayer(player, "nadedraw.region_added", regionId));
                        // 入库完成：清除红色预览（投影指示由各自的 2 秒定时器回收），会话结束
                        ClearEntityList(session.PreviewEntities);
                        return true;
                    }

                    // 入库失败唯一用户可达原因：高度点不高于底面
                    player.PrintToChat(Localizer.ForPlayer(player, "nadedraw.height_invalid"));
                }
            }
            else if (TryPickPoint(player, out var groundPoint, out var airPoint))
            {
                bool confirmed = false;

                switch (session.Stage)
                {
                    case DrawStage.WaitP1:
                        session.CornerA = groundPoint;
                        session.Stage = DrawStage.WaitP2;
                        player.PrintToChat(Localizer.ForPlayer(player, "nadedraw.point1_confirmed"));
                        confirmed = true;
                        break;

                    case DrawStage.WaitP2:
                        if (session.CornerA is not { } cornerA)
                        {
                            // 不变式破坏：底面角点 P1 丢失，重置回第一阶段
                            Server.PrintToConsole($"[PracLab] {DateTime.Now:HH:mm:ss} Warning NadeDraw session missing corner A, reset to WaitP1");
                            session.Stage = DrawStage.WaitP1;
                            break;
                        }
                        if (Distance2D(cornerA, groundPoint) < MinDrawEdgeLength)
                        {
                            player.PrintToChat(Localizer.ForPlayer(player, "nadedraw.point_too_close"));
                        }
                        else
                        {
                            session.CornerB = groundPoint;
                            session.Stage = DrawStage.WaitP3;
                            player.PrintToChat(Localizer.ForPlayer(player, "nadedraw.point2_confirmed"));
                            confirmed = true;
                        }
                        break;
                }

                // 确认成功且为空中取点时，显示投影指示（竖直 beam + 空中十字，2 秒后自动移除）
                if (confirmed && airPoint != null)
                    ShowProjectionIndicator(session, airPoint, groundPoint);
            }
        }

        // 预览刷新（每会话节流 0.1 秒；静默取点，失败保留上次预览）
        if ((DateTime.Now - session.LastPreviewRefresh).TotalSeconds >= PreviewThrottleSeconds)
        {
            if (session.Stage == DrawStage.WaitP3)
            {
                // WaitP3：高度跟随当前空中取点 z
                if (TryPickAirPoint(player, out var heightPoint, silent: true))
                {
                    session.LastPreviewRefresh = DateTime.Now;
                    RebuildPreview(session, heightPoint, null);
                }
            }
            else if (TryPickPoint(player, out var previewPoint, out var previewAir, silent: true))
            {
                session.LastPreviewRefresh = DateTime.Now;
                RebuildPreview(session, previewPoint, previewAir);
            }
        }

        return false;
    }

    // ==================== 取点 ====================

    /// <summary>
    /// 从玩家眼位沿视线方向取点：命中面法线 z &gt; 0.7 判定为地面直接取点；
    /// 否则判定为空中绘制，取命中点（未命中取视线上 2000 单位处）垂直向下投影找地面。
    /// </summary>
    /// <param name="player">取点玩家。</param>
    /// <param name="groundPoint">成功时输出的地面角点。</param>
    /// <param name="airPoint">空中绘制时输出的空中点（地面直接取点时为 null）。</param>
    /// <param name="silent">true 时失败不向玩家发送聊天提示（用于预览刷新，避免刷屏）。</param>
    /// <returns>true 表示取点成功，false 表示失败（射线不可用或向下无地面）。</returns>
    private bool TryPickPoint(CCSPlayerController player, out Vector groundPoint, out Vector? airPoint, bool silent = false)
    {
        groundPoint = new Vector(0, 0, 0);
        airPoint = null;

        try
        {
            // 眼位射线（沿 EyeAngles 打 8192 单位，跳过自身 pawn）
            CGameTrace? result = player.GetGameTraceByEyePosition(TraceMask.MaskSolid, Contents.NoDraw, player);
            if (result is not { } trace)
            {
                Server.PrintToConsole($"[PracLab] {DateTime.Now:HH:mm:ss} Warning NadeDraw eye trace unavailable for {player.PlayerName}");
                if (!silent) player.PrintToChat(Localizer.ForPlayer(player, "nadedraw.trace_unavailable"));
                return false;
            }

            // 命中且法线朝上：地面取点，直接使用命中点
            if (trace.Fraction < 1.0f && trace.Normal.Z > GroundNormalZThreshold)
            {
                groundPoint = ToCssVector(trace.Position);
                return true;
            }

            // 空中绘制：取命中点；未命中时取视线上 2000 单位处
            if (!TryResolveAirPoint(player, trace, out var air, silent))
                return false;
            airPoint = air;

            // 从空中点垂直向下投影找地面（跳过自身 pawn）
            var pawn = player.PlayerPawn.Value;
            nint skip = pawn != null ? pawn.Handle : nint.Zero;
            var downEnd = new Vector(air.X, air.Y, air.Z - TraceDistance);
            var down = TraceRay.TraceShape(air, downEnd, (ulong)TraceMask.MaskSolid, (ulong)Contents.NoDraw, skip);
            if (down.Fraction < 1.0f)
            {
                groundPoint = ToCssVector(down.Position);
                return true;
            }

            // 向下无地面（地图外）：拒绝取点
            if (!silent) player.PrintToChat(Localizer.ForPlayer(player, "nadedraw.invalid_point"));
            return false;
        }
        catch (Exception ex)
        {
            // 打印 InnerException：CS2TraceRay 静态构造失败（gamedata 缺失/签名失配）时外层仅显示 TypeInitializationException
            var detail = ex.InnerException != null ? $"{ex.Message} | Inner: {ex.InnerException.Message}" : ex.Message;
            Server.PrintToConsole($"[PracLab] {DateTime.Now:HH:mm:ss} Error NadeDraw TryPickPoint failed - {detail}");
            if (!silent) player.PrintToChat(Localizer.ForPlayer(player, "nadedraw.trace_unavailable"));
            return false;
        }
    }

    /// <summary>
    /// P3 高度点取点：视线命中点或未命中时视线上 2000 单位处，直接取其世界坐标（调用方只使用 z）。
    /// 不做法线分类与地面投影。
    /// </summary>
    /// <param name="player">取点玩家。</param>
    /// <param name="point">成功时输出的空中点。</param>
    /// <param name="silent">true 时失败不向玩家发送聊天提示（用于预览刷新，避免刷屏）。</param>
    /// <returns>true 表示取点成功，false 表示失败（射线不可用）。</returns>
    private bool TryPickAirPoint(CCSPlayerController player, out Vector point, bool silent = false)
    {
        point = new Vector(0, 0, 0);

        try
        {
            CGameTrace? result = player.GetGameTraceByEyePosition(TraceMask.MaskSolid, Contents.NoDraw, player);
            if (result is not { } trace)
            {
                Server.PrintToConsole($"[PracLab] {DateTime.Now:HH:mm:ss} Warning NadeDraw eye trace unavailable for {player.PlayerName}");
                if (!silent) player.PrintToChat(Localizer.ForPlayer(player, "nadedraw.trace_unavailable"));
                return false;
            }

            return TryResolveAirPoint(player, trace, out point, silent);
        }
        catch (Exception ex)
        {
            var detail = ex.InnerException != null ? $"{ex.Message} | Inner: {ex.InnerException.Message}" : ex.Message;
            Server.PrintToConsole($"[PracLab] {DateTime.Now:HH:mm:ss} Error NadeDraw TryPickAirPoint failed - {detail}");
            if (!silent) player.PrintToChat(Localizer.ForPlayer(player, "nadedraw.trace_unavailable"));
            return false;
        }
    }

    /// <summary>
    /// 由眼位射线结果解析空中点：命中取命中点，未命中取眼位沿视线方向 2000 单位处。
    /// </summary>
    /// <param name="player">取点玩家（用于取眼位）。</param>
    /// <param name="trace">眼位射线结果。</param>
    /// <param name="air">成功时输出的空中点。</param>
    /// <param name="silent">true 时失败不向玩家发送聊天提示。</param>
    /// <returns>true 表示解析成功，false 表示眼位或射线方向不可用。</returns>
    private bool TryResolveAirPoint(CCSPlayerController player, CGameTrace trace, out Vector air, bool silent)
    {
        air = new Vector(0, 0, 0);

        if (trace.Fraction < 1.0f)
        {
            air = ToCssVector(trace.Position);
            return true;
        }

        var eye = player.GetEyePosition();
        if (eye == null)
        {
            Server.PrintToConsole($"[PracLab] {DateTime.Now:HH:mm:ss} Warning NadeDraw eye position unavailable for {player.PlayerName}");
            if (!silent) player.PrintToChat(Localizer.ForPlayer(player, "nadedraw.trace_unavailable"));
            return false;
        }

        // 视线方向 = 射线终点 - 起点，归一化后取 2000 单位处
        float dx = trace.EndPos.X - trace.StartPos.X;
        float dy = trace.EndPos.Y - trace.StartPos.Y;
        float dz = trace.EndPos.Z - trace.StartPos.Z;
        float length = MathF.Sqrt(dx * dx + dy * dy + dz * dz);
        if (length < 1e-6f)
        {
            Server.PrintToConsole($"[PracLab] {DateTime.Now:HH:mm:ss} Warning NadeDraw trace direction degenerate for {player.PlayerName}");
            if (!silent) player.PrintToChat(Localizer.ForPlayer(player, "nadedraw.trace_unavailable"));
            return false;
        }

        float scale = AirPointDistance / length;
        air = new Vector(eye.X + dx * scale, eye.Y + dy * scale, eye.Z + dz * scale);
        return true;
    }

    // ==================== 盒体几何与入库 ====================

    /// <summary>
    /// 将确认的立方体入库：由 P1/P2 构成轴对齐底面、P3 的 z 为顶面高度构造 AABB 盒体，
    /// 生成绿色常驻实体并加入玩家区域列表。
    /// </summary>
    /// <param name="player">会话所属玩家。</param>
    /// <param name="session">会话状态（提供底面角点 P1/P2）。</param>
    /// <param name="heightPoint">确认的高度点（仅使用其 z）。</param>
    /// <param name="regionId">成功时输出新区域编号。</param>
    /// <returns>true 表示入库成功；高度点不高于底面或数据缺失时返回 false。</returns>
    private bool TryCommitRegion(CCSPlayerController player, DrawSession session, Vector heightPoint, out int regionId)
    {
        regionId = 0;

        if (session.CornerA is not { } a || session.CornerB is not { } b)
        {
            Server.PrintToConsole($"[PracLab] {DateTime.Now:HH:mm:ss} Warning NadeDraw commit without corners for {player.PlayerName}");
            return false;
        }

        // 高度点必须高于底面（底面 z 取 P1/P2 较低者，顶面以上限为基准校验）
        float baseZ = MathF.Min(a.Z, b.Z);
        if (heightPoint.Z <= MathF.Max(a.Z, b.Z))
        {
            Server.PrintToConsole($"[PracLab] {DateTime.Now:HH:mm:ss} NadeDraw {player.PlayerName} height point too low (z={heightPoint.Z:F1}, baseTop={MathF.Max(a.Z, b.Z):F1})");
            return false;
        }

        if (!_targetRegions.TryGetValue(player.SteamID, out var regions))
        {
            regions = new List<TargetRegion>(MaxRegionsPerPlayer);
            _targetRegions[player.SteamID] = regions;
        }

        var region = new TargetRegion
        {
            Id = regions.Count + 1,
            MapName = Server.MapName ?? string.Empty,
            Min = new Vector(MathF.Min(a.X, b.X), MathF.Min(a.Y, b.Y), baseZ),
            Max = new Vector(MathF.Max(a.X, b.X), MathF.Max(a.Y, b.Y), heightPoint.Z),
        };

        SpawnRegionEntities(region);
        regions.Add(region);
        regionId = region.Id;

        Server.PrintToConsole($"[PracLab] {DateTime.Now:HH:mm:ss} NadeDraw {player.PlayerName} added region R{region.Id} box=({region.Min.X:F0},{region.Min.Y:F0},{region.Min.Z:F0})~({region.Max.X:F0},{region.Max.Y:F0},{region.Max.Z:F0}) ({regions.Count}/{MaxRegionsPerPlayer})");
        // 终端打印三维立方体框详细坐标（min/max/center/size），与 .ng 调试输出格式一致
        var rCenter = ((region.Min.X + region.Max.X) * 0.5f, (region.Min.Y + region.Max.Y) * 0.5f, (region.Min.Z + region.Max.Z) * 0.5f);
        var rSize = (region.Max.X - region.Min.X, region.Max.Y - region.Min.Y, region.Max.Z - region.Min.Z);
        Server.PrintToConsole($"[PracLab] NadeDraw region R{region.Id} detail: min=({region.Min.X:F1},{region.Min.Y:F1},{region.Min.Z:F1}) max=({region.Max.X:F1},{region.Max.Y:F1},{region.Max.Z:F1}) center=({rCenter.Item1:F1},{rCenter.Item2:F1},{rCenter.Item3:F1}) size=({rSize.Item1:F1},{rSize.Item2:F1},{rSize.Item3:F1})");
        return true;
    }

    // ==================== 渲染 ====================

    /// <summary>
    /// 按当前阶段重建红色预览实体（先清空上次预览）。
    /// WaitP1：当前点十字；WaitP2：P1→当前点的底面矩形框 + 当前点十字；WaitP3：完整 12 边立方体线框（高度跟随当前取点 z）。
    /// </summary>
    /// <param name="session">会话状态。</param>
    /// <param name="pickPoint">当前取点坐标（WaitP1/P2 为地面点，WaitP3 为空中高度点）。</param>
    /// <param name="airPoint">当前取点的空中坐标（WaitP3 或地面直接取点时为 null）。</param>
    private void RebuildPreview(DrawSession session, Vector pickPoint, Vector? airPoint)
    {
        ClearEntityList(session.PreviewEntities);

        switch (session.Stage)
        {
            case DrawStage.WaitP1:
                AddCrossMarker(session.PreviewEntities, pickPoint, Color.Red);
                break;

            case DrawStage.WaitP2:
                if (session.CornerA is { } cornerA)
                {
                    // 底面预览：P1 与当前点构成的轴对齐矩形（z 取两者较低者，与入库逻辑一致）
                    float baseZ = MathF.Min(cornerA.Z, pickPoint.Z);
                    AddBaseRectFrame(session.PreviewEntities, cornerA, pickPoint, baseZ, Color.Red);
                    AddCrossMarker(session.PreviewEntities, pickPoint, Color.Red);

                    // 空中取点时显示当前投影指示（竖直 beam + 空中十字）
                    if (airPoint != null)
                    {
                        AddDrawBeam(session.PreviewEntities, airPoint, pickPoint, Color.Red);
                        AddCrossMarker(session.PreviewEntities, airPoint, Color.Red);
                    }
                }
                break;

            case DrawStage.WaitP3:
                if (session.CornerA is { } a && session.CornerB is { } b)
                {
                    // 12 边立方体线框：底面 z 取 P1/P2 较低者，顶面 z 跟随当前高度点
                    float bottomZ = MathF.Min(a.Z, b.Z);
                    AddBoxWireframe(session.PreviewEntities, a, b, bottomZ, pickPoint.Z, Color.Red);
                }
                break;
        }
    }

    /// <summary>
    /// 创建轴对齐底面矩形框（4 条 beam，z 统一为 baseZ）并登记到指定列表。
    /// </summary>
    /// <param name="registry">实体登记列表。</param>
    /// <param name="a">底面角点一（仅使用 X/Y）。</param>
    /// <param name="b">底面角点二（仅使用 X/Y）。</param>
    /// <param name="baseZ">底面高度。</param>
    /// <param name="color">线段颜色。</param>
    private void AddBaseRectFrame(List<CEntityInstance> registry, Vector a, Vector b, float baseZ, Color color)
    {
        float minX = MathF.Min(a.X, b.X);
        float maxX = MathF.Max(a.X, b.X);
        float minY = MathF.Min(a.Y, b.Y);
        float maxY = MathF.Max(a.Y, b.Y);

        var c1 = new Vector(minX, minY, baseZ);
        var c2 = new Vector(maxX, minY, baseZ);
        var c3 = new Vector(maxX, maxY, baseZ);
        var c4 = new Vector(minX, maxY, baseZ);

        AddDrawBeam(registry, c1, c2, color);
        AddDrawBeam(registry, c2, c3, color);
        AddDrawBeam(registry, c3, c4, color);
        AddDrawBeam(registry, c4, c1, color);
    }

    /// <summary>
    /// 创建立方体 12 边线框（底 4 边 + 顶 4 边 + 竖 4 边）并登记到指定列表。
    /// </summary>
    /// <param name="registry">实体登记列表。</param>
    /// <param name="a">底面角点一（仅使用 X/Y）。</param>
    /// <param name="b">底面角点二（仅使用 X/Y）。</param>
    /// <param name="bottomZ">底面高度。</param>
    /// <param name="topZ">顶面高度。</param>
    /// <param name="color">线段颜色。</param>
    private void AddBoxWireframe(List<CEntityInstance> registry, Vector a, Vector b, float bottomZ, float topZ, Color color)
    {
        float minX = MathF.Min(a.X, b.X);
        float maxX = MathF.Max(a.X, b.X);
        float minY = MathF.Min(a.Y, b.Y);
        float maxY = MathF.Max(a.Y, b.Y);

        // 底面 4 角与顶面 4 角
        var b1 = new Vector(minX, minY, bottomZ);
        var b2 = new Vector(maxX, minY, bottomZ);
        var b3 = new Vector(maxX, maxY, bottomZ);
        var b4 = new Vector(minX, maxY, bottomZ);
        var t1 = new Vector(minX, minY, topZ);
        var t2 = new Vector(maxX, minY, topZ);
        var t3 = new Vector(maxX, maxY, topZ);
        var t4 = new Vector(minX, maxY, topZ);

        // 底 4 边 + 顶 4 边
        AddDrawBeam(registry, b1, b2, color);
        AddDrawBeam(registry, b2, b3, color);
        AddDrawBeam(registry, b3, b4, color);
        AddDrawBeam(registry, b4, b1, color);
        AddDrawBeam(registry, t1, t2, color);
        AddDrawBeam(registry, t2, t3, color);
        AddDrawBeam(registry, t3, t4, color);
        AddDrawBeam(registry, t4, t1, color);

        // 竖 4 边
        AddDrawBeam(registry, b1, t1, color);
        AddDrawBeam(registry, b2, t2, color);
        AddDrawBeam(registry, b3, t3, color);
        AddDrawBeam(registry, b4, t4, color);
    }

    /// <summary>
    /// 确认空中取点时生成投影指示：airPoint→groundPoint 竖直 beam + airPoint 处十字，2 秒后自动移除。
    /// </summary>
    /// <param name="session">会话状态（指示实体登记到其 IndicatorEntities）。</param>
    /// <param name="airPoint">空中点。</param>
    /// <param name="groundPoint">投影的地面点。</param>
    private void ShowProjectionIndicator(DrawSession session, Vector airPoint, Vector groundPoint)
    {
        var created = new List<CEntityInstance>(4);
        AddDrawBeam(created, airPoint, groundPoint, Color.Red);
        AddCrossMarker(created, airPoint, Color.Red);
        if (created.Count == 0) return;

        session.IndicatorEntities.AddRange(created);

        // 2 秒后回收指示实体；会话可能已结束（入库），定时器闭包持有列表引用仍可精确清理
        AddTimer(IndicatorDurationSeconds, () =>
        {
            foreach (var entity in created)
            {
                session.IndicatorEntities.Remove(entity);
                try
                {
                    if (entity != null && entity.IsValid)
                        entity.Remove();
                }
                catch (Exception ex)
                {
                    Server.PrintToConsole($"[PracLab] {DateTime.Now:HH:mm:ss} Warning NadeDraw indicator remove failed - {ex.Message}");
                }
            }
        });
    }

    /// <summary>
    /// 为已入库区域生成绿色常驻实体：12 边立方体线框 + 底面四角十字 + 底面中心编号文本（R{Id}）。
    /// 底面 z 抬高 RegionBeamZOffset 防埋地；编号文本与出生点方框一致贴底面显示（z = 底面 beam 高度 + RegionLabelZOffset）。
    /// </summary>
    /// <param name="region">已填充几何数据的区域（实体追加到其 Entities）。</param>
    private void SpawnRegionEntities(TargetRegion region)
    {
        var min = region.Min;
        var max = region.Max;
        float bottomZ = min.Z + RegionBeamZOffset;

        // 12 边线框
        var minCorner = new Vector(min.X, min.Y, 0);
        var maxCorner = new Vector(max.X, max.Y, 0);
        AddBoxWireframe(region.Entities, minCorner, maxCorner, bottomZ, max.Z, Color.LimeGreen);

        // 底面四角十字
        AddCrossMarker(region.Entities, new Vector(min.X, min.Y, bottomZ), Color.LimeGreen);
        AddCrossMarker(region.Entities, new Vector(max.X, min.Y, bottomZ), Color.LimeGreen);
        AddCrossMarker(region.Entities, new Vector(max.X, max.Y, bottomZ), Color.LimeGreen);
        AddCrossMarker(region.Entities, new Vector(min.X, max.Y, bottomZ), Color.LimeGreen);

        // 编号文本：底面中心，z = 底面 beam 高度 + 2（与出生点序号文本一致平铺贴地）
        var labelPos = new Vector((min.X + max.X) / 2f, (min.Y + max.Y) / 2f, bottomZ + RegionLabelZOffset);
        AddRegionLabel(region.Entities, labelPos, Color.LimeGreen, $"R{region.Id}");
    }

    /// <summary>
    /// 创建一条 beam 线段实体并登记到指定列表。
    /// 参数设置与 SpawnMarkerCommands.CreateBeamLine 一致，但实体登记到调用方列表，避免被 RemoveSpawnBeams 误删。
    /// </summary>
    /// <param name="registry">实体登记列表。</param>
    /// <param name="start">起点世界坐标。</param>
    /// <param name="end">终点世界坐标。</param>
    /// <param name="color">线段颜色。</param>
    private void AddDrawBeam(List<CEntityInstance> registry, Vector start, Vector end, Color color)
    {
        var beam = Utilities.CreateEntityByName<CBeam>("beam");
        if (beam == null || !beam.IsValid)
        {
            Server.PrintToConsole($"[PracLab] {DateTime.Now:HH:mm:ss} Warning NadeDraw failed to create beam entity");
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
        registry.Add(beam);
    }

    /// <summary>
    /// 在指定点创建十字标记（两条正交 beam）并登记到指定列表。
    /// </summary>
    /// <param name="registry">实体登记列表。</param>
    /// <param name="center">十字中心世界坐标。</param>
    /// <param name="color">标记颜色。</param>
    private void AddCrossMarker(List<CEntityInstance> registry, Vector center, Color color)
    {
        AddDrawBeam(registry,
            new Vector(center.X - CrossHalfSize, center.Y, center.Z),
            new Vector(center.X + CrossHalfSize, center.Y, center.Z),
            color);
        AddDrawBeam(registry,
            new Vector(center.X, center.Y - CrossHalfSize, center.Z),
            new Vector(center.X, center.Y + CrossHalfSize, center.Z),
            color);
    }

    /// <summary>
    /// 创建区域编号文本实体（point_worldtext）并登记到指定列表。
    /// 参数设置与 SpawnMarkerCommands.CreateSpawnText 一致（yaw 固定 0），spawn 后 AcceptInput("SetMessage") 双保险。
    /// </summary>
    /// <param name="registry">实体登记列表。</param>
    /// <param name="position">文本世界坐标。</param>
    /// <param name="color">文本颜色。</param>
    /// <param name="message">显示内容（如 R1）。</param>
    private void AddRegionLabel(List<CEntityInstance> registry, Vector position, Color color, string message)
    {
        var text = Utilities.CreateEntityByName<CPointWorldText>("point_worldtext");
        if (text == null || !text.IsValid)
        {
            Server.PrintToConsole($"[PracLab] {DateTime.Now:HH:mm:ss} Warning NadeDraw failed to create point_worldtext entity");
            return;
        }

        text.MessageText = message;
        text.Color = color;
        // FontSize=42 + WorldUnitsPerPx=1：渲染大小约 42 世界单位且保持分辨率
        text.FontSize = 42.0f;
        text.Fullbright = true;   // 必须 true，否则颜色被世界光照过滤
        text.Enabled = true;
        text.FontName = "Noto Sans";  // CS2 UI 常用字体，比默认字体更清晰

        // 关键：WorldUnitsPerPx 控制渲染大小，默认为 0 导致文本不可见
        text.WorldUnitsPerPx = 1.0f;

        // 水平 / 垂直居中对齐
        text.JustifyHorizontal = PointWorldTextJustifyHorizontal_t.POINT_WORLD_TEXT_JUSTIFY_HORIZONTAL_CENTER;
        text.JustifyVertical = PointWorldTextJustifyVertical_t.POINT_WORLD_TEXT_JUSTIFY_VERTICAL_CENTER;

        // 固定朝向：不跟随玩家旋转，yaw 固定 0 度平铺朝上
        text.ReorientMode = PointWorldTextReorientMode_t.POINT_WORLD_TEXT_REORIENT_NONE;

        text.Teleport(position, new QAngle(0, 0, 0), new Vector(0, 0, 0));
        text.DispatchSpawn();

        // 双保险：spawn 后触发 SetMessage 输入，确保文本立即刷新
        text.AcceptInput("SetMessage", text, text, message);

        registry.Add(text);
    }

    // ==================== 清理 ====================

    /// <summary>
    /// 清空全部玩家的绘制状态：会话、预览与指示实体、区域常驻实体与区域数据。
    /// 供 OnMapStart 调用。
    /// </summary>
    private void ClearAllDrawState()
    {
        Server.PrintToConsole("[PracLab] ClearAllDrawState: executing...");

        CancelAllDrawSessions();

        int regionCount = 0;
        foreach (var regions in _targetRegions.Values)
        {
            foreach (var region in regions)
            {
                ClearEntityList(region.Entities);
                regionCount++;
            }
        }
        _targetRegions.Clear();

        Server.PrintToConsole($"[PracLab] {DateTime.Now:HH:mm:ss} NadeDraw all draw state cleared ({regionCount} regions)");
    }

    /// <summary>
    /// 取消全部进行中的绘制会话（清预览与指示实体），保留已入库区域。
    /// 供 OnRoundEnd 调用。
    /// </summary>
    private void CancelAllDrawSessions()
    {
        Server.PrintToConsole("[PracLab] CancelAllDrawSessions: executing...");

        int sessionCount = _drawSessions.Count;
        foreach (var session in _drawSessions.Values)
            ClearSessionEntities(session);
        _drawSessions.Clear();

        Server.PrintToConsole($"[PracLab] {DateTime.Now:HH:mm:ss} NadeDraw all draw sessions cancelled ({sessionCount} sessions)");
    }

    /// <summary>
    /// 清除会话的全部临时实体（预览 + 投影指示）。
    /// </summary>
    /// <param name="session">会话状态。</param>
    private void ClearSessionEntities(DrawSession session)
    {
        ClearEntityList(session.PreviewEntities);
        ClearEntityList(session.IndicatorEntities);
    }

    /// <summary>
    /// 移除列表中全部实体并清空列表。单个实体移除失败不中断整体清理。
    /// </summary>
    /// <param name="entities">实体列表。</param>
    private static void ClearEntityList(List<CEntityInstance> entities)
    {
        foreach (var entity in entities)
        {
            try
            {
                if (entity != null && entity.IsValid)
                    entity.Remove();
            }
            catch (Exception ex)
            {
                Server.PrintToConsole($"[PracLab] {DateTime.Now:HH:mm:ss} Warning NadeDraw failed to remove entity - {ex.Message}");
            }
        }
        entities.Clear();
    }

    // ==================== 辅助 ====================

    /// <summary>
    /// 按 SteamID 在在线玩家中查找控制器。
    /// </summary>
    /// <param name="steamId">玩家 SteamID。</param>
    /// <returns>有效的玩家控制器；未找到时返回 null。</returns>
    private static CCSPlayerController? FindPlayerBySteamId(ulong steamId)
    {
        foreach (var player in Utilities.GetPlayers())
        {
            if (player != null && player.IsValid && player.SteamID == steamId)
                return player;
        }
        return null;
    }

    /// <summary>
    /// 计算两点在 X-Y 平面的 2D 距离。
    /// </summary>
    private static float Distance2D(Vector a, Vector b)
    {
        float dx = a.X - b.X;
        float dy = a.Y - b.Y;
        return MathF.Sqrt(dx * dx + dy * dy);
    }

    /// <summary>
    /// System.Numerics.Vector3 → CounterStrikeSharp Vector 转换。
    /// </summary>
    private static Vector ToCssVector(System.Numerics.Vector3 v) => new(v.X, v.Y, v.Z);
}
