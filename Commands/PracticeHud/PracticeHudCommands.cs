using System.Drawing;
using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Core.Translations;
using CounterStrikeSharp.API.Modules.Utils;

namespace PracLab;

/// <summary>
/// 练习 HUD 命令与驱动：.strafe / .shot / .sync / .recoil（独立 toggle）与 .hudreset（清空统计）。
/// OnTick 驱动采样（仅对开启模块的玩家，按键边沿每 tick 检测，面板推送按 hud_push_interval_ticks 节流）。
/// HUD 实体生命周期：任一玩家开启模块时创建，全部玩家模块关闭时销毁。
/// </summary>
public partial class PracLab
{
    /// <summary>HUD 配置文件相对路径（相对 csgo/ 目录，双布局探测同 config.cfg）。</summary>
    private const string HudConfigRelativePath = "cfg/PracLab/hud.cfg";

    /// <summary>急停面板 ID。</summary>
    private const string HudStrafePanelId = "strafe-panel";

    /// <summary>开枪稳定面板 ID。</summary>
    private const string HudShotPanelId = "shot-panel";

    /// <summary>空中同步面板 ID。</summary>
    private const string HudSyncPanelId = "sync-panel";

    // 弹道追踪（.recoil）无 HUD 面板：压枪轨迹仅以世界空间 beam 绘制（复用 .nadedraw 基建）

    /// <summary>弹道轨迹 beam 折线颜色（亮绿，对应 cs-match-hud 弹道轨迹曲线配色）。</summary>
    private static readonly Color RecoilBeamColor = Color.FromArgb(255, 80, 255, 80);

    /// <summary>弹道轨迹 beam 线宽。</summary>
    private const float RecoilBeamWidth = 2.0f;

    /// <summary>压枪轨迹落段阈值（画板世界单位）：小位移先累积，达到此长度才画一段 beam。</summary>
    private const float RecoilTraceSegmentMinUnits = 0.5f;

    /// <summary>隐藏用 CSS class 名。</summary>
    private const string HudHiddenClass = "hidden";

    /// <summary>HUD 判定参数配置。</summary>
    private readonly PracticeHudConfig _hudConfig = new();

    /// <summary>HUD 实体与推送管理器。</summary>
    private readonly CustomHudManager _hudManager = new();

    /// <summary>每玩家 HUD 会话。键为玩家槽位。</summary>
    private readonly Dictionary<int, PracticeHudSession> _hudSessions = new();

    /// <summary>OnTick 计数器（推送节流用）。</summary>
    private int _hudTickCounter;

    /// <summary>HUD OnTick 监听器是否已注册（RegisterListener 无法注销，用标志守卫）。</summary>
    private bool _hudTickRegistered;

    // ==================== 初始化 ====================

    /// <summary>
    /// 初始化练习 HUD：加载 hud.cfg（插件 Load 时调用）。
    /// HUD 推送使用 CSSharp 内置 CCSCustomHudLayoutExtensions，无需签名扫描；
    /// 布局实体在玩家首次开启模块时按需创建，创建失败时相关命令提示未就绪。
    /// </summary>
    private void InitPracticeHud()
    {
        Server.PrintToConsole("[PracLab] InitPracticeHud: executing...");

        // 加载 hud.cfg（路径双布局探测，与 LoadConfig/EnsureRecordingsDir 同理）
        try
        {
            string[] candidates =
            {
                Path.Combine(Server.GameDirectory, HudConfigRelativePath),
                Path.Combine(Server.GameDirectory, "csgo", HudConfigRelativePath),
            };
            var configPath = Array.Find(candidates, File.Exists) ?? candidates[0];
            _hudConfig.Load(configPath);
        }
        catch (Exception ex)
        {
            Server.PrintToConsole($"[PracLab] {DateTime.Now:HH:mm:ss} Warning PracticeHud config path resolve failed - {ex.Message}");
        }

        // 注册 weapon_fire 事件（压枪轨迹逐发序列判定，覆盖全自动连发）
        RegisterPracticeHudRecoilEvents();
    }

    // ==================== 命令处理 ====================

    /// <summary>
    /// .strafe / .shot / .sync / .recoil — 对应模块开关（toggle，独立记忆）。
    /// 门控：prac 模式（路由层 EnsurePracMode）+ HUD 布局实体就绪（首次开启时按需创建）。
    /// </summary>
    private void HandleHudModuleToggle(CCSPlayerController player, PracticeHudModule module, string commandName)
    {
        Server.PrintToConsole($"[PracLab] HandleHudModuleToggle: executing... command={commandName}");

        var session = GetOrCreateHudSession(player);
        if (session == null)
        {
            player.PrintToChat(Localizer.ForPlayer(player, "spawn.no_pawn"));
            return;
        }

        var newValue = !session.IsModuleEnabled(module);
        session.SetModuleEnabled(module, newValue);

        // 关闭弹道追踪：清除世界空间轨迹折线（开启时不清理，继续延长当前轨迹）
        if (!newValue && module == PracticeHudModule.Recoil)
            ClearRecoilBeams(session);

        if (newValue)
        {
            // 弹道追踪无 HUD 面板（仅世界空间 beam），不依赖布局实体；其余模块需实体就绪
            if (module != PracticeHudModule.Recoil)
            {
                // 任一模块开启：确保实体存在
                if (!_hudManager.EnsureLayout())
                {
                    session.SetModuleEnabled(module, false);
                    player.PrintToChat(Localizer.ForPlayer(player, "hud.not_ready"));
                    Server.PrintToConsole($"[PracLab] {DateTime.Now:HH:mm:ss} Error PracticeHud layout entity unavailable, {player.PlayerName} {commandName} rejected");
                    return;
                }

                // 实体重建后新实体无任何状态：失效全部会话差量缓存，下一个推送周期全量重推
                if (_hudManager.LayoutWasRecreated)
                {
                    foreach (var s in _hudSessions.Values)
                        s.InvalidateCaches();
                }
            }
        }
        else if (!HasAnyHudModuleEnabled())
        {
            // 全部玩家的全部模块已关闭：销毁实体（面板显隐已由 PushHudPanelVisibility 全部置 hidden）
            _hudManager.ClearLayout();
        }

        // 面板显隐差量推送（实体刚创建时缓存为空会全量推送）
        PushHudPanelVisibility(session);

        player.PrintToChat(Localizer.ForPlayer(player, newValue ? $"hud.{commandName}.enabled" : $"hud.{commandName}.disabled"));
        Server.PrintToConsole($"[PracLab] {DateTime.Now:HH:mm:ss} HUD {commandName} {(newValue ? "enabled" : "disabled")} for {player.PlayerName} slot={player.Slot}");

        // 开启新模块后立即刷新一次面板数据
        PushHudForSession(session);
    }

    /// <summary>
    /// .hudreset — 清空当前玩家全部 HUD 统计（开关状态保留）。
    /// </summary>
    private void HandleHudReset(CCSPlayerController player, string args)
    {
        Server.PrintToConsole("[PracLab] HandleHudReset: executing...");

        var session = GetOrCreateHudSession(player);
        if (session == null)
        {
            player.PrintToChat(Localizer.ForPlayer(player, "spawn.no_pawn"));
            return;
        }

        session.ResetAllStats();
        ClearRecoilBeams(session);
        session.InvalidateCaches();
        PushHudForSession(session);

        player.PrintToChat(Localizer.ForPlayer(player, "hud.reset"));
        Server.PrintToConsole($"[PracLab] {DateTime.Now:HH:mm:ss} HUD stats reset for {player.PlayerName} slot={player.Slot}");
    }

    /// <summary>
    /// 获取或创建玩家会话；玩家 pawn 不可用（未进场）时返回 null。
    /// </summary>
    private PracticeHudSession? GetOrCreateHudSession(CCSPlayerController player)
    {
        var pawn = player.PlayerPawn.Value;
        if (pawn == null || !pawn.IsValid)
            return null;

        if (_hudSessions.TryGetValue(player.Slot, out var session))
            return session;

        session = new PracticeHudSession(player.Slot, _hudConfig);
        _hudSessions[player.Slot] = session;
        return session;
    }

    /// <summary>
    /// 是否存在任一玩家开启了任意模块（决定全局实体是否保留）。
    /// </summary>
    private bool HasAnyHudModuleEnabled()
    {
        foreach (var session in _hudSessions.Values)
        {
            if (session.IsAnyModuleEnabled)
                return true;
        }

        return false;
    }

    // ==================== OnTick 采样与推送 ====================

    /// <summary>
    /// 注册 OnTick 监听器（仅一次；热重载后由 _hudTickRegistered 守卫）。
    /// </summary>
    private void RegisterHudTickListener()
    {
        if (_hudTickRegistered)
            return;

        RegisterListener<Listeners.OnTick>(() =>
        {
            try
            {
                PracticeHudTick();
            }
            catch (Exception ex)
            {
                Server.PrintToConsole($"[PracLab] {DateTime.Now:HH:mm:ss} Error PracticeHud OnTick failed - {ex.Message}");
            }
        });

        _hudTickRegistered = true;
        Server.PrintToConsole($"[PracLab] {DateTime.Now:HH:mm:ss} PracticeHud OnTick listener registered");
    }

    /// <summary>
    /// 每 tick 采样：仅处理开启了模块的玩家会话。按键边沿每 tick 检测，
    /// 面板文本按 hud_push_interval_ticks 节流差量推送。高频路径不打印日志。
    /// </summary>
    private void PracticeHudTick()
    {
        if (_hudSessions.Count == 0)
            return;

        // 收集开启模块的会话（无则直接返回，避免每 tick 遍历玩家列表）
        List<PracticeHudSession>? activeSessions = null;
        foreach (var session in _hudSessions.Values)
        {
            if (session.IsAnyModuleEnabled)
            {
                activeSessions ??= new List<PracticeHudSession>(4);
                activeSessions.Add(session);
            }
        }

        if (activeSessions == null)
            return;

        var tick = Server.TickCount;
        var players = Utilities.GetPlayers();

        foreach (var session in activeSessions)
        {
            // 查找玩家控制器
            CCSPlayerController? player = null;
            for (var i = 0; i < players.Count; i++)
            {
                var p = players[i];
                if (p is { IsValid: true } && p.Slot == session.Slot)
                {
                    player = p;
                    break;
                }
            }

            if (player == null || !player.IsValid)
                continue;

            var pawn = player.PlayerPawn.Value;
            if (pawn == null || !pawn.IsValid)
            {
                // 死亡/观察阶段：重置输入状态机（统计保留），避免复活后残留按键状态
                session.ResetInputStates();
                continue;
            }

            var buttons = player.Buttons;

            // —— 急停评估：四移动键边沿 ——
            if (session.IsModuleEnabled(PracticeHudModule.Strafe))
            {
                UpdateStrafeModule(session, tick, buttons);
            }

            // —— 开枪稳定 / 压枪轨迹 / 空中同步：共享速度采样 ——
            var shotEnabled = session.IsModuleEnabled(PracticeHudModule.Shot);
            var recoilEnabled = session.IsModuleEnabled(PracticeHudModule.Recoil);
            var syncEnabled = session.IsModuleEnabled(PracticeHudModule.Sync);
            var attackDown = (buttons & PlayerButtons.Attack) != 0;
            var fireDownEdge = attackDown && !session.LastAttackDown;
            session.LastAttackDown = attackDown;

            if (shotEnabled || recoilEnabled || syncEnabled)
            {
                var vel = pawn.AbsVelocity;
                var velX = vel?.X ?? 0f;
                var velY = vel?.Y ?? 0f;

                if (shotEnabled)
                {
                    // 武器阈值：开火边沿时读取当前武器，平时用默认值
                    float threshold;
                    if (fireDownEdge)
                    {
                        threshold = GetPlayerWeaponThreshold(player);
                    }
                    else
                    {
                        threshold = _hudConfig.ShotDefaultThreshold;
                    }

                    var yaw = pawn.EyeAngles.Y;
                    _ = session.ShotEngine.Update(tick, buttons, velX, velY, yaw, threshold, fireDownEdge);
                }

                // 压枪轨迹：每 tick 视角差分采样。
                // 逐发序列判定由 weapon_fire 事件驱动；仅在按住攻击键（正在开火）且序列活跃
                // （最后一发真实开火在重置阈值内）时延长，停火（松键）立即停笔。
                if (recoilEnabled && attackDown && session.RecoilEngine.SequenceCount > 0 && session.RecoilEngine.IsSequenceActive(tick))
                {
                    UpdateRecoilTrace(session, pawn);
                }

                // —— 空中同步：每 tick（空中）以速度增益判定（cs2kz 算法）——
                if (syncEnabled)
                {
                    var onGround = (pawn.Flags & 1u) != 0;
                    var forwardPressed = (buttons & PlayerButtons.Forward) != 0;
                    var backPressed = (buttons & PlayerButtons.Back) != 0;
                    var leftPressed = (buttons & PlayerButtons.Moveleft) != 0;
                    var rightPressed = (buttons & PlayerButtons.Moveright) != 0;
                    var horizontalSpeed = MathF.Sqrt(velX * velX + velY * velY);
                    _ = session.SyncEngine.Update(tick, onGround, forwardPressed, backPressed, leftPressed, rightPressed, horizontalSpeed);
                }
            }
            else
            {
                // 模块关闭期间维持边沿状态同步，避免重新开启时误触发
                session.LastAttackDown = attackDown;
            }
        }

        // —— 面板推送节流 ——
        _hudTickCounter++;
        if (_hudTickCounter % _hudConfig.HudPushIntervalTicks != 0)
            return;

        if (!_hudManager.IsAvailable)
            return;

        foreach (var session in activeSessions)
        {
            PushHudForSession(session);
        }
    }

    /// <summary>
    /// 急停评估模块：将本 tick 四键状态传给引擎产生边沿事件（引擎内幂等，同状态不触发）。
    /// </summary>
    private static void UpdateStrafeModule(PracticeHudSession session, int tick, PlayerButtons buttons)
    {
        session.StrafeEngine.OnKey(CounterStrafeEngine.KeyLeft, (buttons & PlayerButtons.Moveleft) != 0, tick);
        session.StrafeEngine.OnKey(CounterStrafeEngine.KeyRight, (buttons & PlayerButtons.Moveright) != 0, tick);
        session.StrafeEngine.OnKey(CounterStrafeEngine.KeyForward, (buttons & PlayerButtons.Forward) != 0, tick);
        session.StrafeEngine.OnKey(CounterStrafeEngine.KeyBack, (buttons & PlayerButtons.Back) != 0, tick);
    }

    /// <summary>
    /// 读取开火稳定判定的武器精度阈值（开火边沿时调用；高频路径避免武器遍历）。
    /// </summary>
    private float GetPlayerWeaponThreshold(CCSPlayerController player)
    {
        try
        {
            var pawn = player.PlayerPawn.Value;
            var weapon = pawn?.WeaponServices?.ActiveWeapon.Value;
            if (weapon == null || !weapon.IsValid)
                return _hudConfig.ShotDefaultThreshold;

            var name = weapon.DesignerName;
            if (string.IsNullOrEmpty(name))
                return _hudConfig.ShotDefaultThreshold;

            if (_hudConfig.ShotWeaponThresholdsEnabled)
                return _hudConfig.GetWeaponThreshold(name);

            return _hudConfig.ShotDefaultThreshold;
        }
        catch (Exception)
        {
            return _hudConfig.ShotDefaultThreshold;
        }
    }

    /// <summary>
    /// 读取弹道追踪输入：武器 recoil index 与视角 punch 角。
    /// recoil index 读失败时降级为仅射击间隔判定序列。
    /// </summary>
    private static (uint RecoilIndex, bool Available, float PunchPitch, float PunchYaw) ReadRecoilInputs(CCSPlayerPawn pawn)
    {
        uint recoilIndex = 0;
        var indexAvailable = false;
        float punchPitch = 0f;
        float punchYaw = 0f;

        try
        {
            var weapon = pawn.WeaponServices?.ActiveWeapon.Value;
            if (weapon is { IsValid: true } && weapon is CCSWeaponBase weaponBase)
            {
                recoilIndex = (uint)weaponBase.FlRecoilIndex;
                indexAvailable = true;
            }
        }
        catch (Exception)
        {
            indexAvailable = false;
        }

        try
        {
            var camera = pawn.CameraServices;
            if (camera != null)
            {
                var punch = camera.CsViewPunchAngle;
                punchPitch = punch.X;
                punchYaw = punch.Y;
            }
        }
        catch (Exception)
        {
            // punch 角不可读时保持 0，序列仍以开火计数推进
        }

        return (recoilIndex, indexAvailable, punchPitch, punchYaw);
    }

    // ==================== 压枪轨迹（鼠标移动轨迹可视化） ====================

    /// <summary>
    /// 注册压枪轨迹事件（InitPracticeHud 调用）：weapon_fire 在每发真实开火时触发，
    /// 覆盖全自动连发的后续各发（按键按下边沿只触发一次，无法驱动逐发序列判定）。
    /// </summary>
    private void RegisterPracticeHudRecoilEvents()
    {
        RegisterEventHandler<EventWeaponFire>(OnWeaponFire);
        Server.PrintToConsole($"[PracLab] {DateTime.Now:HH:mm:ss} PracticeHud weapon fire listener registered");
    }

    /// <summary>
    /// weapon_fire 事件处理：每发真实开火时记录 punch/recoil index 序列并判定序列重置。
    /// </summary>
    /// <param name="event">开火事件（Userid + Weapon）。</param>
    /// <param name="info">事件信息（未使用）。</param>
    /// <returns>继续事件传播。</returns>
    private HookResult OnWeaponFire(EventWeaponFire @event, GameEventInfo info)
    {
        var player = @event.Userid;
        if (player == null || !player.IsValid)
            return HookResult.Continue;

        if (!_hudSessions.TryGetValue(player.Slot, out var session))
            return HookResult.Continue;

        if (!session.IsModuleEnabled(PracticeHudModule.Recoil))
            return HookResult.Continue;

        var pawn = player.PlayerPawn.Value;
        if (pawn == null || !pawn.IsValid)
            return HookResult.Continue;

        try
        {
            var (recoilIndex, indexAvailable, punchPitch, punchYaw) = ReadRecoilInputs(pawn);

            var wasReset = session.RecoilEngine.OnFire(Server.TickCount, punchPitch, punchYaw, recoilIndex, indexAvailable);

            // 序列重置（停顿/换弹/切枪）：清除上一条轨迹的折线段后开始新轨迹
            if (wasReset)
                ClearRecoilBeams(session);

            // 新序列第一发：以当前视角锚定压枪轨迹画板（连续开火不重复锚定）
            if (_hudConfig.RecoilBeamEnabled && session.RecoilEngine.SequenceCount == 1)
                AnchorRecoilTrace(session, pawn);
        }
        catch (Exception ex)
        {
            Server.PrintToConsole($"[PracLab] {DateTime.Now:HH:mm:ss} Warning PracticeHud weapon fire handling failed - {ex.Message}");
        }

        return HookResult.Continue;
    }

    /// <summary>
    /// 新开火序列锚定压枪轨迹画板：以玩家当前眼位与视角构造垂直于视线的虚拟画板。
    /// 画板在序列期间世界固定（不随视角转动），视角差分映射到画板 2D 平面连线，
    /// 即 cs-match-hud 鼠标原始输入轨迹的世界空间等价物（压枪下拉 → 轨迹向下延伸）。
    /// </summary>
    /// <param name="session">玩家 HUD 会话。</param>
    /// <param name="pawn">玩家 pawn。</param>
    private void AnchorRecoilTrace(PracticeHudSession session, CCSPlayerPawn pawn)
    {
        try
        {
            var origin = pawn.AbsOrigin;
            var angles = pawn.EyeAngles;
            if (origin == null || angles == null)
                return;

            var view = pawn.ViewOffset;
            var eyePos = new Vector(origin.X + view.X, origin.Y + view.Y, origin.Z + view.Z);

            // 视线方向（Source 约定：pitch 正 = 向下，yaw 正 = 向左）
            var yawRad = angles.Y * MathF.PI / 180f;
            var pitchRad = angles.X * MathF.PI / 180f;
            var cosP = MathF.Cos(pitchRad);
            var forward = new Vector(cosP * MathF.Cos(yawRad), cosP * MathF.Sin(yawRad), -MathF.Sin(pitchRad));

            var distance = _hudConfig.RecoilTraceDistance;
            session.RecoilTraceOrigin = new Vector(
                eyePos.X + forward.X * distance,
                eyePos.Y + forward.Y * distance,
                eyePos.Z + forward.Z * distance);

            // 画板右向量（水平面内，forward × worldUp 归一化后 = (sin yaw, -cos yaw, 0)）
            session.RecoilTraceRight = new Vector(MathF.Sin(yawRad), -MathF.Cos(yawRad), 0f);

            // 画板上向量（right × forward）
            session.RecoilTraceUp = new Vector(
                MathF.Cos(yawRad) * MathF.Sin(pitchRad),
                MathF.Sin(yawRad) * MathF.Sin(pitchRad),
                MathF.Cos(pitchRad));

            // EyeAngles 即鼠标真实视角（不含后坐力 punch），差分即纯鼠标移动
            session.RecoilTraceLastAimPitch = angles.X;
            session.RecoilTraceLastAimYaw = angles.Y;
            session.RecoilTraceLastPoint = session.RecoilTraceOrigin;
        }
        catch (Exception ex)
        {
            Server.PrintToConsole($"[PracLab] {DateTime.Now:HH:mm:ss} Warning PracticeHud recoil trace anchor failed - {ex.Message}");
        }
    }

    /// <summary>
    /// 每 tick 采样视角差分延长世界空间压枪轨迹：鼠标右移 → 轨迹向右，鼠标下拉（压枪）→ 轨迹向下。
    /// EyeAngles 即鼠标真实视角（不含后坐力 punch），差分即纯鼠标移动（与 cs-match-hud raw input 语义一致）。
    /// </summary>
    /// <param name="session">玩家 HUD 会话。</param>
    /// <param name="pawn">玩家 pawn。</param>
    private void UpdateRecoilTrace(PracticeHudSession session, CCSPlayerPawn pawn)
    {
        var origin = session.RecoilTraceOrigin;
        if (origin == null)
            return;

        if (!_hudConfig.RecoilBeamEnabled || session.RecoilBeams.Count >= _hudConfig.RecoilTraceMaxSegments)
            return;

        try
        {
            var angles = pawn.EyeAngles;
            if (angles == null)
                return;

            var aimPitch = angles.X;
            var aimYaw = angles.Y;

            var yawDelta = aimYaw - session.RecoilTraceLastAimYaw;
            // yaw 环绕处理（±180 度跨越时不产生瞬移段）
            if (yawDelta > 180f)
                yawDelta -= 360f;
            else if (yawDelta < -180f)
                yawDelta += 360f;

            var pitchDelta = aimPitch - session.RecoilTraceLastAimPitch;
            session.RecoilTraceLastAimPitch = aimPitch;
            session.RecoilTraceLastAimYaw = aimYaw;

            // 小位移累积落段：低于阈值的位移先累积（减少碎 beam），达到阈值一次性画出
            session.RecoilTracePendingPitch += pitchDelta;
            session.RecoilTracePendingYaw += yawDelta;

            var scale = _hudConfig.RecoilTraceScale;
            var pendingUnits = (MathF.Abs(session.RecoilTracePendingPitch) + MathF.Abs(session.RecoilTracePendingYaw)) * scale;
            if (pendingUnits < RecoilTraceSegmentMinUnits)
                return;

            var pendingPitch = session.RecoilTracePendingPitch;
            var pendingYaw = session.RecoilTracePendingYaw;
            session.RecoilTracePendingPitch = 0f;
            session.RecoilTracePendingYaw = 0f;

            // 鼠标右移 → yaw 减小 → 轨迹 x 取反为正（向右）；鼠标下拉 → pitch 增大 → 轨迹 y 取反为正（向下）
            // 位置为累积语义：从上一点延伸累积位移（对应 cs-match-hud 累计 raw input 投影）
            var last = session.RecoilTraceLastPoint;
            if (last == null)
                return;

            var right = session.RecoilTraceRight;
            var up = session.RecoilTraceUp;
            var point = new Vector(
                last.X + right.X * (-pendingYaw * scale) + up.X * (-pendingPitch * scale),
                last.Y + right.Y * (-pendingYaw * scale) + up.Y * (-pendingPitch * scale),
                last.Z + right.Z * (-pendingYaw * scale) + up.Z * (-pendingPitch * scale));

            AddDrawBeam(session.RecoilBeams, last, point, RecoilBeamColor, RecoilBeamWidth);

            session.RecoilTraceLastPoint = point;
        }
        catch (Exception ex)
        {
            Server.PrintToConsole($"[PracLab] {DateTime.Now:HH:mm:ss} Warning PracticeHud recoil trace update failed - {ex.Message}");
        }
    }

    /// <summary>
    /// 清除当前序列的压枪轨迹 beam 与画板锚定状态。
    /// 触发点：序列重置（停顿/换弹/切枪）、.recoil 关闭、.hudreset、会话销毁、插件卸载。
    /// </summary>
    /// <param name="session">玩家 HUD 会话。</param>
    private void ClearRecoilBeams(PracticeHudSession session)
    {
        if (session.RecoilBeams.Count > 0)
            ClearEntityList(session.RecoilBeams);

        session.RecoilTraceOrigin = null;
        session.RecoilTraceLastAimPitch = 0f;
        session.RecoilTraceLastAimYaw = 0f;
        session.RecoilTraceLastPoint = null;
        session.RecoilTracePendingPitch = 0f;
        session.RecoilTracePendingYaw = 0f;
    }

    // ==================== 面板渲染与推送 ====================

    /// <summary>
    /// 按模块开关差量推送三个面板的显隐 class（根面板无 id 且恒显示，显隐全部由子面板控制）。
    /// 弹道追踪（.recoil）无 HUD 面板，压枪轨迹仅以世界空间 beam 呈现。
    /// </summary>
    private void PushHudPanelVisibility(PracticeHudSession session)
    {
        _hudManager.PushClass(session, HudStrafePanelId, HudHiddenClass, !session.IsModuleEnabled(PracticeHudModule.Strafe));
        _hudManager.PushClass(session, HudShotPanelId, HudHiddenClass, !session.IsModuleEnabled(PracticeHudModule.Shot));
        _hudManager.PushClass(session, HudSyncPanelId, HudHiddenClass, !session.IsModuleEnabled(PracticeHudModule.Sync));
    }

    /// <summary>
    /// 渲染并推送单个会话的面板文本（差量：值未变化不调用原生函数）。
    /// </summary>
    private void PushHudForSession(PracticeHudSession session)
    {
        var player = FindControllerBySlot(session.Slot);
        if (player == null)
            return;

        PushHudPanelVisibility(session);

        // —— strafe-panel ——
        if (session.IsModuleEnabled(PracticeHudModule.Strafe))
        {
            var engine = session.StrafeEngine;
            var last = engine.LastRecord;
            var stats = engine.GetStats();

            var label = last.HasValue ? Localizer.ForPlayer(player, StrafeLabelKey(last.Value)) : string.Empty;
            var diff = last.HasValue ? $"{last.Value.DiffTicks.ToString("+0;-0;0")} tick" : string.Empty;
            var tendencyKey = stats.Tendency switch
            {
                -1 => "hud.label.early_tendency",
                1 => "hud.label.late_tendency",
                _ => "hud.label.normal_tendency",
            };
            var tendency = Localizer.ForPlayer(player, tendencyKey);
            var statsText = Localizer.ForPlayer(player, "hud.strafe.stats",
                stats.AvgDiffTicks.ToString("+0.#;-0.#;0"),
                stats.SuccessRatePercent.ToString("0.#"),
                stats.StdDevTicks.ToString("0.#"),
                tendency);

            // 判定色钩子（互斥叠加）：完美=绿 / 优秀=黄 / 偏早偏晚=红，对应 prac_hud.css
            _hudManager.PushClass(session, HudStrafePanelId, "strafe-perfect", last.HasValue && last.Value.IsPerfect);
            _hudManager.PushClass(session, HudStrafePanelId, "strafe-good", last.HasValue && !last.Value.IsPerfect && last.Value.IsSuccess);
            _hudManager.PushClass(session, HudStrafePanelId, "strafe-bad", last.HasValue && !last.Value.IsSuccess);

            _hudManager.PushDialogVariable(session, HudStrafePanelId, "strafe-label", label);
            _hudManager.PushDialogVariable(session, HudStrafePanelId, "strafe-diff", diff);
            _hudManager.PushDialogVariable(session, HudStrafePanelId, "strafe-stats", statsText);
        }

        // —— shot-panel ——
        if (session.IsModuleEnabled(PracticeHudModule.Shot))
        {
            var engine = session.ShotEngine;
            var last = engine.LastRecord;
            var stats = engine.GetStats();

            var label = last.HasValue ? Localizer.ForPlayer(player, ShotLabelKey(last.Value.Label)) : string.Empty;
            var error = last.HasValue ? last.Value.SpeedRatio.ToString("0.00") : string.Empty;
            var statsText = Localizer.ForPlayer(player, "hud.shot.stats", stats.StableRatePercent.ToString("0.#"));

            _hudManager.PushDialogVariable(session, HudShotPanelId, "shot-label", label);
            _hudManager.PushDialogVariable(session, HudShotPanelId, "shot-error", error);
            _hudManager.PushDialogVariable(session, HudShotPanelId, "shot-stats", statsText);
        }

        // —— sync-panel ——
        if (session.IsModuleEnabled(PracticeHudModule.Sync))
        {
            var engine = session.SyncEngine;
            var current = engine.CurrentSyncRate.ToString("0.#");
            var avg = engine.AverageSyncRate().ToString("0.#");
            var best = engine.BestSyncRate().ToString("0.#");

            _hudManager.PushDialogVariable(session, HudSyncPanelId, "sync-current", Localizer.ForPlayer(player, "hud.sync.current", current));
            _hudManager.PushDialogVariable(session, HudSyncPanelId, "sync-avg", Localizer.ForPlayer(player, "hud.sync.average", avg));
            _hudManager.PushDialogVariable(session, HudSyncPanelId, "sync-best", Localizer.ForPlayer(player, "hud.sync.best", best));
        }
    }

    /// <summary>
    /// 按槽位查找在线玩家控制器。
    /// </summary>
    private CCSPlayerController? FindControllerBySlot(int slot)
    {
        foreach (var p in Utilities.GetPlayers())
        {
            if (p is { IsValid: true } && p.Slot == slot)
                return p;
        }

        return null;
    }

    /// <summary>急停判定标签 lang 键（完美/优秀/偏早/偏晚）。</summary>
    private static string StrafeLabelKey(CounterStrafeRecord record) => record.IsPerfect
        ? "hud.label.perfect"
        : record.IsSuccess
            ? "hud.label.excellent"
            : record.DiffTicks < 0
                ? "hud.label.early"
                : "hud.label.late";

    /// <summary>开枪稳定标签 lang 键。</summary>
    private static string ShotLabelKey(ShotLabelKind label) => label switch
    {
        ShotLabelKind.Stable => "hud.label.stable",
        ShotLabelKind.Crouch => "hud.label.crouch",
        ShotLabelKind.MicroMove => "hud.label.micro",
        ShotLabelKind.RunShot => "hud.label.run",
        ShotLabelKind.LowSpeedSway => "hud.label.low_sway",
        _ => "hud.label.startup_low",
    };

    // ==================== 生命周期 ====================

    /// <summary>
    /// 玩家断线清理：移除会话；实体仅在全部模块关闭时保留逻辑统一由 HandleHudModuleToggle 处理。
    /// </summary>
    private void RemoveHudSession(CCSPlayerController player)
    {
        if (!_hudSessions.TryGetValue(player.Slot, out var session))
            return;

        // 断线时其弹道轨迹 beam 仍存活于世界内，需随会话销毁
        ClearRecoilBeams(session);
        _hudSessions.Remove(player.Slot);

        Server.PrintToConsole($"[PracLab] {DateTime.Now:HH:mm:ss} HUD session removed for {player.PlayerName} slot={player.Slot}");

        if (!HasAnyHudModuleEnabled())
            _hudManager.ClearLayout();
    }

    /// <summary>
    /// 地图切换清理：清空会话与实体引用（实体随地图销毁）。
    /// 由 EventHandlers.OnMapStart 调用。
    /// </summary>
    private void ResetPracticeHudForMapChange()
    {
        if (_hudSessions.Count > 0)
            _hudSessions.Clear();

        _hudManager.ResetForMapChange();
    }

    /// <summary>
    /// 插件卸载：销毁 HUD 实体（保留会话无意义，一并清空）。
    /// </summary>
    public override void Unload(bool hotReload)
    {
        Server.PrintToConsole("[PracLab] Unload: executing...");

        // beam 实体独立于 HUD 布局实体，卸载时需逐会话显式销毁
        foreach (var session in _hudSessions.Values)
            ClearRecoilBeams(session);

        _hudSessions.Clear();
        _hudManager.ClearLayout();
    }
}
