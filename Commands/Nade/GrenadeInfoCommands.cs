using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Core.Translations;
using CounterStrikeSharp.API.Modules.Events;
using CounterStrikeSharp.API.Modules.Utils;

namespace PracLab;

/// <summary>
/// 投掷物数据报告模块：飞行时间 + 反弹次数 + 闪光致盲时长。
/// 参考 MatchZy 实现：用 projectile.Index 作为 key，detonate 事件用 @event.Entityid 关联。
/// OnTick 追踪速度分量符号反转检测反弹。
/// </summary>
public partial class PracLab
{
    /// <summary>
    /// 反弹检测的最小速度阈值（游戏单位/秒）。
    /// 仅用于过滤数值精度导致的微小抖动（|v|&lt;1），保留所有真实反弹事件
    /// （包括落地后小速度弹跳，如 |v|=5 的滚动阶段反弹）。
    /// 旧值 50 会过滤掉落地后的小速度反弹（|X|=45.2、|Z|=14.8、|Z|=5.2），
    /// 导致 bounces 偏低（实际 5 次反转只计数 2 次）。
    /// </summary>
    private const float BounceMinSpeed = 1.0f;

    /// <summary>
    /// 投掷物飞行追踪字典。键为投掷物实体索引（projectile.Index）。
    /// detonate 事件的 Entityid 字段直接对应此 key，实现精准关联。
    /// </summary>
    private readonly Dictionary<uint, GrenadeFlightInfo> _grenadeFlightTracker = new();

    /// <summary>
    /// 最近一次闪光弹投掷者 SteamID。
    /// player_blind 事件本身不含投掷者字段，用此字段关联。
    /// detonate 事件触发时设置（@event.Userid 为投掷者）。
    /// </summary>
    private ulong _lastFlashThrower;

    /// <summary>
    /// OnTick 监听器是否已注册（Listeners.OnTick 一旦注册无法注销，用此标志守卫）。
    /// </summary>
    private bool _isGrenadeTickRegistered;

    /// <summary>
    /// 投掷物飞行信息（值类型，避免持有可能失效的 native 句柄包装）。
    /// </summary>
    /// <param name="Handle">投掷物实体句柄（用于 OnTick 重建 CBaseCSGrenadeProjectile 读取速度）。</param>
    /// <param name="SpawnTime">投掷物生成时间（用于 detonate 时计算飞行时长）。</param>
    /// <param name="BounceCount">反弹次数（OnTick 检测速度分量符号反转累加）。</param>
    /// <param name="LastVelX">上一帧速度 X（用于符号反转检测）。</param>
    /// <param name="LastVelY">上一帧速度 Y。</param>
    /// <param name="LastVelZ">上一帧速度 Z。</param>
    /// <param name="DesignerName">实体类名（用于 detonate 时选择道具显示名）。</param>
    /// <param name="ThrowerSteamId">投掷者 SteamID（用于输出消息时查找玩家）。</param>
    private struct GrenadeFlightInfo
    {
        public nint Handle;
        public DateTime SpawnTime;
        public int BounceCount;
        public float LastVelX;
        public float LastVelY;
        public float LastVelZ;
        public string DesignerName;
        public ulong ThrowerSteamId;
    }

    /// <summary>
    /// 投掷物生成时调用（由 EventHandlers.OnEntitySpawned 转调）。
    /// 在 _grenadeFlightTracker 中以 projectile.Index 为 key 登记飞行信息。
    /// 参考 MatchZy：lastGrenadeThrownTime[(int)projectile.Index] = DateTime.Now。
    /// </summary>
    /// <param name="thrower">投掷者。</param>
    /// <param name="entityHandle">投掷物实体句柄。</param>
    /// <param name="projectileIndex">投掷物实体索引（projectile.Index）。</param>
    /// <param name="designerName">实体类名。</param>
    /// <param name="velocity">投掷瞬间的世界速度（用于初始化 LastVel）。</param>
    private void RegisterGrenadeFlight(CCSPlayerController thrower, nint entityHandle, uint projectileIndex, string designerName, Vector velocity)
    {
        var info = new GrenadeFlightInfo
        {
            Handle = entityHandle,
            SpawnTime = DateTime.Now,
            BounceCount = 0,
            LastVelX = velocity.X,
            LastVelY = velocity.Y,
            LastVelZ = velocity.Z,
            DesignerName = designerName,
            ThrowerSteamId = thrower.SteamID,
        };

        _grenadeFlightTracker[projectileIndex] = info;

        // 闪光弹额外记录最近投掷者，供 OnPlayerBlind 关联
        if (designerName == "flashbang_projectile")
            _lastFlashThrower = thrower.SteamID;

        // 按需注册 OnTick 监听器（仅注册一次）
        if (!_isGrenadeTickRegistered)
            RegisterGrenadeTickListener();
    }

    /// <summary>
    /// 注册 OnTick 监听器检测投掷物反弹（仅注册一次）。
    /// 回调内通过 _isPracMode 与 _grenadeFlightTracker.Count 短路返回，避免非 prac 模式空转。
    /// 反弹检测：任一速度分量符号反转（lastVel * vel < 0）且该轴速度绝对值 > BounceMinSpeed。
    /// 用分量符号反转而非整体点积，因为单轴反转时整体点积仍可能为正（漏判）。
    /// </summary>
    private void RegisterGrenadeTickListener()
    {
        RegisterListener<Listeners.OnTick>(() =>
        {
            // 短路：prac 模式关闭或无追踪中的投掷物时直接返回
            if (!_isPracMode || _grenadeFlightTracker.Count == 0) return;

            try
            {
                List<uint>? staleKeys = null;

                // 取键快照后迭代，避免 foreach 期间修改字典抛 InvalidOperationException
                foreach (var index in _grenadeFlightTracker.Keys)
                {
                    var info = _grenadeFlightTracker[index];
                    var proj = new CBaseCSGrenadeProjectile(info.Handle);
                    if (!proj.IsValid)
                    {
                        // 实体已失效（引爆未触发事件 / 落出地图 / 被 .clear 清除）
                        (staleKeys ??= new List<uint>()).Add(index);
                        continue;
                    }

                    var vel = proj.AbsVelocity;
                    if (vel == null) continue;

                    // 反弹检测：任一速度分量符号反转且该轴速度足够大 → 判定为一次反弹
                    bool bounced = false;
                    string bounceAxis = "";
                    if (info.LastVelX * vel.X < 0 && Math.Abs(vel.X) > BounceMinSpeed) { bounced = true; bounceAxis += "X "; }
                    if (info.LastVelY * vel.Y < 0 && Math.Abs(vel.Y) > BounceMinSpeed) { bounced = true; bounceAxis += "Y "; }
                    if (info.LastVelZ * vel.Z < 0 && Math.Abs(vel.Z) > BounceMinSpeed) { bounced = true; bounceAxis += "Z "; }

                    if (bounced)
                    {
                        info.BounceCount++;
                        // 终端调试日志：记录每次反弹的轴和速度变化，便于对照 .nadetest 轨迹验证
                        Server.PrintToConsole($"[PracLab] {DateTime.Now:HH:mm:ss} GrenadeInfo bounce#{info.BounceCount} idx={index} axis=[{bounceAxis.Trim()}] prev=({info.LastVelX:F1},{info.LastVelY:F1},{info.LastVelZ:F1}) curr=({vel.X:F1},{vel.Y:F1},{vel.Z:F1})");
                    }

                    // 更新上一帧速度（值类型字段通过局部变量修改后写回字典）
                    info.LastVelX = vel.X;
                    info.LastVelY = vel.Y;
                    info.LastVelZ = vel.Z;
                    _grenadeFlightTracker[index] = info;
                }

                // 清理失效条目
                if (staleKeys != null)
                {
                    foreach (var key in staleKeys)
                    {
                        _grenadeFlightTracker.Remove(key);
                    }
                }
            }
            catch (Exception ex)
            {
                Server.PrintToConsole($"[PracLab] {DateTime.Now:HH:mm:ss} Error GrenadeInfo OnTick failed - {ex.Message}");
            }
        });

        _isGrenadeTickRegistered = true;
        Server.PrintToConsole($"[PracLab] {DateTime.Now:HH:mm:ss} GrenadeInfo OnTick listener registered");
    }

    /// <summary>
    /// 烟雾弹引爆事件回调：报告飞行时间与反弹次数到投掷者聊天栏。
    /// 用 @event.Entityid 关联 _grenadeFlightTracker 中的飞行信息。
    /// </summary>
    private HookResult OnSmokegrenadeDetonate(EventSmokegrenadeDetonate @event, GameEventInfo info)
        => ReportGrenadeDetonation(@event.Userid, (uint)@event.Entityid, "smokegrenade_projectile");

    /// <summary>
    /// 闪光弹引爆事件回调：报告飞行时间与反弹次数，并设置 _lastFlashThrower 供 OnPlayerBlind 关联。
    /// </summary>
    private HookResult OnFlashbangDetonate(EventFlashbangDetonate @event, GameEventInfo info)
        => ReportGrenadeDetonation(@event.Userid, (uint)@event.Entityid, "flashbang_projectile");

    /// <summary>
    /// 高爆手雷引爆事件回调：报告飞行时间与反弹次数到投掷者聊天栏。
    /// </summary>
    private HookResult OnHegrenadeDetonate(EventHegrenadeDetonate @event, GameEventInfo info)
        => ReportGrenadeDetonation(@event.Userid, (uint)@event.Entityid, "hegrenade_projectile");

    /// <summary>
    /// 燃烧瓶/燃烧弹引爆事件回调：报告飞行时间与反弹次数到投掷者聊天栏。
    /// 参考 MatchZy：molotov 事件用 @event.Get&lt;int&gt;("entityid") 获取实体 ID（无强类型 Entityid 属性）。
    /// </summary>
    private HookResult OnMolotovDetonate(EventMolotovDetonate @event, GameEventInfo info)
        => ReportGrenadeDetonation(@event.Userid, (uint)@event.Get<int>("entityid"), "molotov_projectile");

    /// <summary>
    /// 通用引爆报告：从 _grenadeFlightTracker 用 entityid 查找飞行信息，计算飞行时长并输出到聊天栏。
    /// 仅 prac 模式下报告；找不到条目时静默返回。
    /// </summary>
    /// <param name="thrower">投掷者（来自事件的 Userid）。</param>
    /// <param name="entityid">投掷物实体 ID（对应 projectile.Index）。</param>
    /// <param name="designerName">期望的实体类名（用于校验 tracker 中的条目匹配）。</param>
    private HookResult ReportGrenadeDetonation(CCSPlayerController? thrower, uint entityid, string designerName)
    {
        if (!_isPracMode) return HookResult.Continue;
        if (thrower == null || !thrower.IsValid) return HookResult.Continue;

        try
        {
            if (!_grenadeFlightTracker.TryGetValue(entityid, out var info)) return HookResult.Continue;

            // 校验类名匹配（防止实体索引被复用导致误关联）
            if (info.DesignerName != designerName) return HookResult.Continue;

            var flightTime = (float)(DateTime.Now - info.SpawnTime).TotalSeconds;
            var nadeNameKey = GetNadeDisplayNameKey(designerName);
            var nadeName = Localizer.ForPlayer(thrower, nadeNameKey);

            thrower.PrintToChat(Localizer.ForPlayer(thrower, "nade.flight.info", nadeName, flightTime, info.BounceCount));
            Server.PrintToConsole($"[PracLab] {DateTime.Now:HH:mm:ss} GrenadeInfo {thrower.PlayerName} {designerName} entityid={entityid} flight={flightTime:F2}s bounces={info.BounceCount}");

            _grenadeFlightTracker.Remove(entityid);
        }
        catch (Exception ex)
        {
            Server.PrintToConsole($"[PracLab] {DateTime.Now:HH:mm:ss} Error ReportGrenadeDetonation failed - {ex.Message}");
        }

        return HookResult.Continue;
    }

    /// <summary>
    /// 投掷物实体类名 → lang 文件中的道具显示名键。
    /// </summary>
    /// <param name="designerName">实体类名。</param>
    /// <returns>lang 键名（如 nade.smoke）。</returns>
    private static string GetNadeDisplayNameKey(string designerName) => designerName switch
    {
        "smokegrenade_projectile" => "nade.smoke",
        "flashbang_projectile" => "nade.flash",
        "hegrenade_projectile" => "nade.he",
        "molotov_projectile" => "nade.molotov",
        "incgrenade_projectile" => "nade.molotov", // 燃烧弹(CT) 复用燃烧瓶显示名
        "decoy_projectile" => "nade.decoy",
        _ => "nade.smoke",
    };

    /// <summary>
    /// 玩家被闪光致盲时报告致盲时长到投掷者（由 EventHandlers.OnPlayerBlind 转调）。
    /// 不影响 noflash 免疫逻辑（免疫玩家的 FlashDuration 被清零，但 player_blind 事件本身仍触发，
    /// BlindDuration 字段可读到引擎原始值）。
    /// </summary>
    /// <param name="blindedPlayer">被闪玩家。</param>
    /// <param name="blindDuration">致盲时长（秒）。</param>
    private void ReportFlashBlindInfo(CCSPlayerController blindedPlayer, float blindDuration)
    {
        if (!_isPracMode) return;

        // 终端调试日志：诊断致盲数据是否触发
        Server.PrintToConsole($"[PracLab] {DateTime.Now:HH:mm:ss} GrenadeInfo blind event: target={blindedPlayer.PlayerName} dur={blindDuration:F2}s lastThrower={_lastFlashThrower}");

        if (_lastFlashThrower == 0) return;

        // 降低阈值到 0.1 秒，避免短闪被过滤
        if (blindDuration < 0.1f) return;

        try
        {
            // 查找最近闪光投掷者玩家对象
            CCSPlayerController? thrower = null;
            foreach (var p in Utilities.GetPlayers())
            {
                if (p.IsValid && p.SteamID == _lastFlashThrower)
                {
                    thrower = p;
                    break;
                }
            }

            if (thrower == null)
            {
                Server.PrintToConsole($"[PracLab] {DateTime.Now:HH:mm:ss} Warning GrenadeInfo blind: thrower not found for SteamID={_lastFlashThrower}");
                return;
            }

            // 自闪也报告（prac 模式下玩家需要知道自闪时长以优化投掷）
            thrower.PrintToChat(Localizer.ForPlayer(thrower, "flash.blind.info", blindedPlayer.PlayerName, blindDuration));
            Server.PrintToConsole($"[PracLab] {DateTime.Now:HH:mm:ss} GrenadeInfo {thrower.PlayerName} flashed {blindedPlayer.PlayerName} for {blindDuration:F2}s");
        }
        catch (Exception ex)
        {
            Server.PrintToConsole($"[PracLab] {DateTime.Now:HH:mm:ss} Error ReportFlashBlindInfo failed - {ex.Message}");
        }
    }
}
