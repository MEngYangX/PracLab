using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Core.Translations;
using CounterStrikeSharp.API.Modules.Utils;
using CS2TraceRay.Class;

namespace PracLab;

/// <summary>
/// 投掷校准命令（.nadetest/.ntt）：登记后 30 秒内投出一颗雷，
/// OnEntitySpawned 捕获真实出手位置/速度并与仿真模型预测值对比打印（控制台英文）。
/// 投掷方式与力度自动识别（按键锁存 + 地面累积距离分类树），
/// 也可通过参数手动声明用于对比。用于校准 NadeBaseSpeed / pitch 修正 / 玩家速度继承系数。
/// </summary>
public partial class PracLab
{
    /// <summary>测试意图有效期（秒），超时自动作废。</summary>
    private const double NadeTestIntentSeconds = 30.0;

    /// <summary>CS2 玩家速度继承系数（.nadetest 18 组实测：水平各组 1.22~1.27，取 1.25；原社区逆向 1.45 偏大）。</summary>
    private const float NadeVelocityInheritFactor = 1.25f;

    /// <summary>静止判定 tick 数：地面无移动/跳跃输入持续该 tick 数后重置累积状态（经验值 8）。</summary>
    private const int NadeTestStillTicks = 8;

    /// <summary>
    /// 前跳投识别的空中水平速度阈值（单位/秒）。
    /// .nadetest 实测：原地跳投空中水平速度 ≈0（玩家只按 Jump），前跳投空中水平速度 ~30（玩家按 W+Jump，W 键加速度使水平速度逐渐增加）。
    /// 阈值 15 区分两种情况：&lt;15 视为原地跳投，≥15 视为前跳投。
    /// 用于修复前跳投识别错误：AccumulatedDistance 只在地面累积，前跳投玩家立即起跳导致距离为 0 被误判为原地跳投。
    /// </summary>
    private const float RunJumpAirSpeedThreshold = 15.0f;

    /// <summary>每玩家待测意图（力度/方式为空 = 自动识别, 声明时间），一次性消费。</summary>
    private readonly Dictionary<ulong, (ThrowStrength? Strength, ThrowMode? Mode, DateTime At)> _nadeTestIntents = new();

    /// <summary>每玩家按键追踪状态（仅有待测意图的玩家被追踪）。</summary>
    private readonly Dictionary<ulong, NadeTestTrackState> _nadeTestTrackStates = new();

    /// <summary>OnTick 监听器是否已注册（守卫式单次注册）。</summary>
    private bool _isNadeTestTickRegistered;

    /// <summary>轨迹录制最大时长（tick，64tick 服务器约 6 秒）。</summary>
    private const int NadeTestTrajMaxTicks = 384;

    /// <summary>录制结束静止判定：速度模长 &lt; 1 持续该 tick 数（滚动收尾阶段提前结束）。</summary>
    private const int NadeTestTrajStillTicks = 16;

    /// <summary>
    /// 真实轨迹录制状态：.nadetest 捕获成功后对投射物逐 tick 采样 AbsOrigin/AbsVelocity，
    /// 用于拟合真实重力、空气阻力与反弹系数（仿真三大量均未实测，"远度偏差"根因排查）。
    /// </summary>
    private sealed class NadeTestTrajRec
    {
        public nint Handle;
        public int Tick;
        public int StillTicks;
    }

    /// <summary>每玩家进行中的轨迹录制（新投掷覆盖旧录制）。</summary>
    private readonly Dictionary<ulong, NadeTestTrajRec> _nadeTestTrajRecs = new();

    /// <summary>
    /// 单 tick 移动样本：记录玩家位置/速度/视角/按键/地面状态，用于前跳投等投掷方式的完整移动信息回放。
    /// </summary>
    private readonly struct NadeTestMoveSample
    {
        /// <summary>tick 序号（从 .ntt 登记开始递增）。</summary>
        public readonly int Tick;

        /// <summary>玩家脚底位置。</summary>
        public readonly float X, Y, Z;

        /// <summary>玩家速度。</summary>
        public readonly float Vx, Vy, Vz;

        /// <summary>视角 pitch/yaw（度）。</summary>
        public readonly float Pitch, Yaw;

        /// <summary>按键状态（原始位掩码）。</summary>
        public readonly uint Buttons;

        /// <summary>是否在地面（FL_ONGROUND）。</summary>
        public readonly bool OnGround;

        public NadeTestMoveSample(int tick, float x, float y, float z, float vx, float vy, float vz, float pitch, float yaw, uint buttons, bool onGround)
        {
            Tick = tick; X = x; Y = y; Z = z;
            Vx = vx; Vy = vy; Vz = vz;
            Pitch = pitch; Yaw = yaw;
            Buttons = buttons; OnGround = onGround;
        }
    }

    /// <summary>
    /// 按键追踪状态（连续移动缓冲的简化版）：
    /// 跳跃/蹲/静步/攻击键按下后锁存；地面站立静止 NadeTestStillTicks 后整体重置；
    /// 累积距离仅统计地面水平位移，用于区分 normal / step / run。
    /// 增强版：同时逐 tick 记录完整移动样本（位置/速度/视角/按键/地面状态），
    /// 并跟踪跳跃高度、最大速度等聚合数据，用于前跳投移动信息回放。
    /// </summary>
    private sealed class NadeTestTrackState
    {
        public bool DidPressJump;
        public bool DidPressDuck;
        public bool DidPressWalkKey;
        public bool DidMoveOnGround;
        public bool DidPressAttack;
        public bool DidPressAttack2;
        public bool WasAttacking;
        public int ConsecutiveStillTicks;
        public float AccumulatedDistance;
        public float PrevX;
        public float PrevY;
        public bool HasPrevPosition;

        /// <summary>逐 tick 移动样本（输入后的一切移动信息）。</summary>
        public readonly List<NadeTestMoveSample> MoveSamples = new(256);

        /// <summary>当前已记录的 tick 数（独立于 MoveSamples.Count，避免静止重置后序号回退）。</summary>
        public int CurrentTick;

        /// <summary>起跳前地面 Z（用于计算跳跃高度）。</summary>
        public float GroundZ;

        /// <summary>空中最大 Z（用于计算跳跃高度 = MaxAirZ - GroundZ）。</summary>
        public float MaxAirZ;

        /// <summary>是否已进入过空中（区分首次起跳与全程地面）。</summary>
        public bool HasBeenAirborne;

        /// <summary>最大速度模长（单位/秒）。</summary>
        public float MaxSpeed;

        /// <summary>空中最大水平速度模长（单位/秒）。用于识别前跳投：玩家按 W+Jump 同时起跳时
        /// 地面累积距离为 0 但空中水平速度 ~30（来自 W 键加速度），需用此字段区分原地跳投与前跳投。</summary>
        public float MaxAirHorizontalSpeed;

        /// <summary>上一 tick 是否在地面（用于检测起跳/落地瞬间）。</summary>
        public bool WasOnGround = true;

        /// <summary>站立静止后整体重置（视为一次新投掷尝试的起点）。</summary>
        public void ResetAccumulated()
        {
            AccumulatedDistance = 0f;
            DidPressJump = false;
            DidPressDuck = false;
            DidPressWalkKey = false;
            DidMoveOnGround = false;
            HasPrevPosition = false;
            // 移动样本与聚合数据一并重置：新投掷尝试从干净状态开始
            MoveSamples.Clear();
            CurrentTick = 0;
            GroundZ = 0f;
            MaxAirZ = 0f;
            HasBeenAirborne = false;
            MaxSpeed = 0f;
            MaxAirHorizontalSpeed = 0f;
            WasOnGround = true;
        }
    }

    /// <summary>
    /// .nadetest/.ntt 命令处理器：
    /// 无参数 = 自动识别模式（推荐），投掷时按真实按键状态判定力度/方式；
    /// 带参数「left|mid|right + normal|jump|runjump|duck|duckjump|duckrunjump」= 手动声明，对比数据按声明的模式/力度计算。
    /// </summary>
    /// <param name="player">调用者玩家。</param>
    /// <param name="args">命令参数（力度/方式关键字，可任意组合、任意顺序、可省略）。</param>
    private void HandleNadeTest(CCSPlayerController player, string args)
    {
        var parts = args.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        ThrowStrength? strength = null;
        ThrowMode? mode = null;

        foreach (var part in parts)
        {
            switch (part.ToLowerInvariant())
            {
                case "left": strength = ThrowStrength.Left; break;
                case "mid": strength = ThrowStrength.Mid; break;
                case "right": strength = ThrowStrength.Right; break;
                case "normal": mode = ThrowMode.Normal; break;
                case "jump": mode = ThrowMode.Jump; break;
                case "runjump": mode = ThrowMode.RunJump; break;
                case "duck": mode = ThrowMode.Duck; break;
                case "duckjump": mode = ThrowMode.DuckJump; break;
                case "duckrunjump": mode = ThrowMode.DuckRunJump; break;
                default:
                    player.PrintToChat(Localizer.ForPlayer(player, "nadetest.invalid", part));
                    return;
            }
        }

        _nadeTestIntents[player.SteamID] = (strength, mode, DateTime.Now);
        _nadeTestTrackStates.Remove(player.SteamID);
        EnsureNadeTestTickListener();

        if (strength.HasValue || mode.HasValue)
        {
            player.PrintToChat(Localizer.ForPlayer(player, "nadetest.armed_manual",
                GetStrengthLocalizedName(player, strength ?? ThrowStrength.Left),
                GetModeLocalizedName(player, mode ?? ThrowMode.Normal),
                (int)NadeTestIntentSeconds));
        }
        else
        {
            player.PrintToChat(Localizer.ForPlayer(player, "nadetest.armed_auto", (int)NadeTestIntentSeconds));
        }

        player.PrintToConsole($"[PracLab] ===== NadeTest 调试开始 =====");
        player.PrintToConsole($"[PracLab] NadeTest intent={(strength?.ToString() ?? "auto")}/{(mode?.ToString() ?? "auto")}");
    }

    /// <summary>
    /// 注册 OnTick 监听器追踪待测玩家的按键状态（仅注册一次，无意图时短路返回）。
    /// </summary>
    private void EnsureNadeTestTickListener()
    {
        if (_isNadeTestTickRegistered) return;
        _isNadeTestTickRegistered = true;

        RegisterListener<Listeners.OnTick>(() =>
        {
            if (_nadeTestIntents.Count == 0 && _nadeTestTrajRecs.Count == 0) return;

            try
            {
                ProcessNadeTestTrajRecs();

                List<ulong>? expired = null;

                foreach (var (steamId, intent) in _nadeTestIntents)
                {
                    // 过期清扫：超过 2 倍有效期仍未投掷的意图直接移除，避免无限追踪
                    if ((DateTime.Now - intent.At).TotalSeconds > NadeTestIntentSeconds * 2)
                    {
                        (expired ??= new List<ulong>()).Add(steamId);
                        continue;
                    }

                    var player = Utilities.GetPlayerFromSteamId(steamId);
                    var pawn = player?.PlayerPawn.Value;
                    if (player == null || !player.IsValid || pawn == null || !pawn.IsValid || pawn.AbsOrigin == null)
                        continue;

                    if (!_nadeTestTrackStates.TryGetValue(steamId, out var state))
                    {
                        state = new NadeTestTrackState();
                        _nadeTestTrackStates[steamId] = state;
                    }

                    TrackPlayerButtons(player, pawn, state);
                }

                if (expired != null)
                {
                    foreach (var id in expired)
                    {
                        _nadeTestIntents.Remove(id);
                        _nadeTestTrackStates.Remove(id);
                    }
                }
            }
            catch (Exception ex)
            {
                Server.PrintToConsole($"[PracLab] {DateTime.Now:HH:mm:ss} Error NadeTest OnTick failed - {ex.Message}");
            }
        });

        Server.PrintToConsole($"[PracLab] {DateTime.Now:HH:mm:ss} NadeTest OnTick listener registered");
    }

    /// <summary>
    /// 每 tick 追踪单个玩家按键：
    /// 跳跃/蹲/静步/攻击键锁存；地面静止 NadeTestStillTicks 后重置；地面水平位移累积距离。
    /// 增强版：同时逐 tick 记录完整移动样本（位置/速度/视角/按键/地面状态），
    /// 并跟踪跳跃高度（GroundZ/MaxAirZ）、最大速度，用于前跳投移动信息回放。
    /// 注意：静步键用 PlayerButtons.Speed（= 1&lt;&lt;17，CS2 shift 走路键），
    /// 而非原始值 65536（该值在 CSS 枚举中为 Score/Tab，不符合静步语义）。
    /// </summary>
    /// <param name="player">玩家控制器。</param>
    /// <param name="pawn">玩家 pawn。</param>
    /// <param name="state">追踪状态。</param>
    private static void TrackPlayerButtons(CCSPlayerController player, CCSPlayerPawn pawn, NadeTestTrackState state)
    {
        var origin = pawn.AbsOrigin!;
        var buttons = player.Buttons;
        var vel = pawn.AbsVelocity;
        float vx = vel?.X ?? 0f;
        float vy = vel?.Y ?? 0f;
        float vz = vel?.Z ?? 0f;

        bool onGround = (pawn.Flags & 1) != 0; // FL_ONGROUND
        bool moveKeys = (buttons & (PlayerButtons.Forward | PlayerButtons.Back | PlayerButtons.Moveleft | PlayerButtons.Moveright)) != 0;
        bool jumpKey = (buttons & PlayerButtons.Jump) != 0;

        if (onGround && !moveKeys && !jumpKey)
        {
            state.ConsecutiveStillTicks++;
            if (state.ConsecutiveStillTicks >= NadeTestStillTicks)
                state.ResetAccumulated();
        }
        else
        {
            state.ConsecutiveStillTicks = 0;
        }

        // 跳跃高度跟踪：起跳前记录 GroundZ，空中更新 MaxAirZ
        if (onGround)
        {
            if (!state.HasBeenAirborne)
                state.GroundZ = origin.Z;
        }
        else
        {
            state.HasBeenAirborne = true;
            if (origin.Z > state.MaxAirZ)
                state.MaxAirZ = origin.Z;
        }

        // 最大速度跟踪
        float speed = MathF.Sqrt(vx * vx + vy * vy + vz * vz);
        if (speed > state.MaxSpeed)
            state.MaxSpeed = speed;

        // 空中最大水平速度跟踪：仅在空中时更新，用于识别前跳投
        // （前跳投玩家按 W+Jump 起跳，空中水平速度 ~30；原地跳投 ≈0）
        if (!onGround)
        {
            float hSpeed = MathF.Sqrt(vx * vx + vy * vy);
            if (hSpeed > state.MaxAirHorizontalSpeed)
                state.MaxAirHorizontalSpeed = hSpeed;
        }

        // 记录本 tick 移动样本（位置/速度/视角/按键/地面状态）
        float pitch = pawn.EyeAngles.X;
        float yaw = pawn.EyeAngles.Y;
        state.MoveSamples.Add(new NadeTestMoveSample(
            state.CurrentTick, origin.X, origin.Y, origin.Z,
            vx, vy, vz, pitch, yaw, (uint)buttons, onGround));
        state.CurrentTick++;
        state.WasOnGround = onGround;

        if (jumpKey) state.DidPressJump = true;
        if ((buttons & PlayerButtons.Duck) != 0) state.DidPressDuck = true;
        if ((buttons & PlayerButtons.Speed) != 0) state.DidPressWalkKey = true;

        // 攻击沿检测：新一轮按下先清锁存，再记录本次按住左键/右键（可能双键 = 中力）
        bool attacking = (buttons & (PlayerButtons.Attack | PlayerButtons.Attack2)) != 0;
        if (attacking && !state.WasAttacking)
        {
            state.DidPressAttack = false;
            state.DidPressAttack2 = false;
        }
        state.WasAttacking = attacking;
        if ((buttons & PlayerButtons.Attack) != 0) state.DidPressAttack = true;
        if ((buttons & PlayerButtons.Attack2) != 0) state.DidPressAttack2 = true;

        if (onGround)
        {
            if (moveKeys) state.DidMoveOnGround = true;

            float x = origin.X;
            float y = origin.Y;
            if (state.HasPrevPosition)
            {
                float dx = x - state.PrevX;
                float dy = y - state.PrevY;
                float dist = MathF.Sqrt(dx * dx + dy * dy);
                if (dist > 0.1f)
                    state.AccumulatedDistance += dist;
            }
            state.PrevX = x;
            state.PrevY = y;
            state.HasPrevPosition = true;
        }
    }

    /// <summary>
    /// 按键状态解码为可读字符串（用于移动样本日志输出）。
    /// F=Forward B=Back L=Moveleft R=Moveright J=Jump D=Duck W=Walk(Speed) A=Attack a=Attack2
    /// </summary>
    /// <param name="buttons">按键位掩码。</param>
    /// <returns>可读按键串（无按键返回 "-"）。</returns>
    private static string DecodeButtons(PlayerButtons buttons)
    {
        var s = "";
        if ((buttons & PlayerButtons.Forward) != 0) s += 'F';
        if ((buttons & PlayerButtons.Back) != 0) s += 'B';
        if ((buttons & PlayerButtons.Moveleft) != 0) s += 'L';
        if ((buttons & PlayerButtons.Moveright) != 0) s += 'R';
        if ((buttons & PlayerButtons.Jump) != 0) s += 'J';
        if ((buttons & PlayerButtons.Duck) != 0) s += 'D';
        if ((buttons & PlayerButtons.Speed) != 0) s += 'W';
        if ((buttons & PlayerButtons.Attack) != 0) s += 'A';
        if ((buttons & PlayerButtons.Attack2) != 0) s += 'a';
        return s.Length == 0 ? "-" : s;
    }

    /// <summary>
    /// 投掷方式分类树（11 类）：
    /// 跳投系：未地面移动 + 蹲 → duckjumpthrow；未地面移动 → jumpthrow；累积距离 &lt;5 → wjumpthrow；
    /// &lt;50 → stepjumpthrow；静步 → walkjumpthrow；否则 runjumpthrow。非跳系同理（duck/normal/step/walk/run）。
    /// 前跳投识别：AccumulatedDistance 只在地面累积，玩家按 W+Jump 立即起跳时 DidMoveOnGround=false 且距离为 0，
    /// 会被误判为 jumpthrow/wjumpthrow（原地跳投）。修复：在所有"未地面移动或距离&lt;5"的分支中，
    /// 额外检查 MaxAirHorizontalSpeed，若 ≥ RunJumpAirSpeedThreshold（空中水平速度 ~30）则判定为前跳投（runjumpthrow）。
    /// 返回详细类型名（控制台英文）与映射到仿真六模式（含 duck 系列，按 DidPressDuck 细分）。
    /// </summary>
    private static (string TypeName, ThrowMode Mapped) ClassifyThrowType(NadeTestTrackState s)
    {
        // 前跳投识别：玩家按 W+Jump 立即起跳时，地面累积距离为 0，但空中水平速度 ~30（W 键加速度）
        bool isRunJump = s.MaxAirHorizontalSpeed >= RunJumpAirSpeedThreshold;

        string type;
        if (s.DidPressJump)
        {
            if (!s.DidMoveOnGround && s.DidPressDuck) type = isRunJump ? "runjumpthrow" : "duckjumpthrow";
            else if (!s.DidMoveOnGround) type = isRunJump ? "runjumpthrow" : "jumpthrow";
            else if (s.AccumulatedDistance < 5f) type = isRunJump ? "runjumpthrow" : "wjumpthrow";
            else if (s.AccumulatedDistance < 50f) type = "stepjumpthrow";
            else if (s.DidPressWalkKey) type = "walkjumpthrow";
            else type = "runjumpthrow";
        }
        else
        {
            if (s.AccumulatedDistance < 5f && s.DidPressDuck) type = "duckthrow";
            else if (s.AccumulatedDistance < 5f) type = "normal";
            else if (s.AccumulatedDistance < 50f) type = "stepthrow";
            else if (s.DidPressWalkKey) type = "walkthrow";
            else type = "runthrow";
        }

        // 映射到仿真六模式：带跳且地面助跑距离 >=5 的归入 RunJump 系（wjump 视为原地跳投）；
        // 按下蹲键（含 wjump 等未单独命名 duck 变体的类型）细分到 duck 系
        var mapped = type switch
        {
            "stepjumpthrow" or "walkjumpthrow" or "runjumpthrow" => s.DidPressDuck ? ThrowMode.DuckRunJump : ThrowMode.RunJump,
            "jumpthrow" or "wjumpthrow" => s.DidPressDuck ? ThrowMode.DuckJump : ThrowMode.Jump,
            "duckjumpthrow" => ThrowMode.DuckJump,
            "duckthrow" => ThrowMode.Duck,
            "stepthrow" or "walkthrow" or "runthrow" => s.DidPressDuck ? ThrowMode.Duck : ThrowMode.Normal,
            _ => ThrowMode.Normal,
        };
        return (type, mapped);
    }

    /// <summary>
    /// 力度识别：左右键同按 = 中力；仅右键 = 右键轻抛；否则左键满力。
    /// </summary>
    private static ThrowStrength DetectStrength(NadeTestTrackState s)
    {
        if (s.DidPressAttack && s.DidPressAttack2) return ThrowStrength.Mid;
        if (s.DidPressAttack2) return ThrowStrength.Right;
        return ThrowStrength.Left;
    }

    /// <summary>
    /// 每 tick 采样录制中的投射物并打印（每 2 tick 一行）：
    /// 投射物失效（引爆）/超时/低速持续时结束录制并打印收尾行。
    /// 输出通道为玩家控制台（英文），用户复制含 "NadeTest traj" 的日志段即可用于物理参数拟合。
    /// </summary>
    private void ProcessNadeTestTrajRecs()
    {
        if (_nadeTestTrajRecs.Count == 0) return;

        List<ulong>? finished = null;

        foreach (var (steamId, rec) in _nadeTestTrajRecs)
        {
            rec.Tick++;

            var proj = new CBaseCSGrenadeProjectile(rec.Handle);
            var pos = proj.IsValid ? proj.AbsOrigin : null;
            var vel = proj.IsValid ? proj.AbsVelocity : null;

            string? endReason = null;
            if (!proj.IsValid || pos == null || vel == null)
            {
                endReason = "detonated";
            }
            else if (rec.Tick >= NadeTestTrajMaxTicks)
            {
                endReason = "timeout";
            }
            else
            {
                float speed = MathF.Sqrt(vel.X * vel.X + vel.Y * vel.Y + vel.Z * vel.Z);
                if (speed < 1.0f)
                {
                    if (++rec.StillTicks >= NadeTestTrajStillTicks) endReason = "rest";
                }
                else
                {
                    rec.StillTicks = 0;
                }
            }

            // 输出到玩家控制台：通过 steamId 查找玩家后 PrintToConsole
            var player = Utilities.GetPlayerFromSteamId(steamId);

            if (endReason == null && pos != null && vel != null)
            {
                if ((rec.Tick & 1) == 0 && player != null && player.IsValid)
                    player.PrintToConsole($"[PracLab] NadeTest traj i={rec.Tick} t={rec.Tick / 64.0f:F3} p=({pos.X:F1},{pos.Y:F1},{pos.Z:F1}) v=({vel.X:F1},{vel.Y:F1},{vel.Z:F1})");
            }
            else if (endReason != null)
            {
                // 收尾：打印最后一个有效采样（若有）+ 结束行，然后移除录制
                if (pos != null && vel != null && player != null && player.IsValid)
                    player.PrintToConsole($"[PracLab] NadeTest traj last i={rec.Tick} t={rec.Tick / 64.0f:F3} p=({pos.X:F1},{pos.Y:F1},{pos.Z:F1}) v=({vel.X:F1},{vel.Y:F1},{vel.Z:F1})");
                if (player != null && player.IsValid)
                {
                    player.PrintToConsole($"[PracLab] NadeTest traj end ticks={rec.Tick} reason={endReason}");
                    player.PrintToConsole($"[PracLab] ===== NadeTest 调试结束 =====");
                }
                (finished ??= new List<ulong>()).Add(steamId);
            }
        }

        if (finished != null)
        {
            foreach (var id in finished)
                _nadeTestTrajRecs.Remove(id);
        }
    }

    /// <summary>
    /// OnEntitySpawned 捕获真实投掷时调用（EventHandlers.cs 注入点）：
    /// 若玩家有待测意图且在有效期内，打印「真实 vs 模型」对比数据到玩家控制台并消费意图；否则静默返回。
    /// 输出三组核心证据：
    /// 1. real：真实出手速度（金标准）与模长；
    /// 2. inherit：straight_throw 逆向公式（方向速度 + 真实玩家速度×1.25），对任何投掷方式通用；
    /// 3. model：搜索仿真模型（固定增量），按自动识别/声明的方式与力度计算；
    /// 另附真实出手方向反推 pitch 与视角 pitch 的差值（验证 pitch 修正式）、出手点与眼位差值。
    /// </summary>
    /// <param name="thrower">投掷者。</param>
    /// <param name="pos">投掷物出生位置（projectile.AbsOrigin）。</param>
    /// <param name="vel">投掷物真实初速度（projectile.AbsVelocity）。</param>
    /// <param name="designerName">投掷物实体类名（区分道具类型，便于核对各道具基础速度）。</param>
    /// <param name="entityHandle">投掷物实体句柄（用于启动轨迹录制）。</param>
    private void ReportNadeTestCapture(CCSPlayerController thrower, Vector pos, Vector vel, string designerName, nint entityHandle)
    {
        var steamId = thrower.SteamID;
        if (!_nadeTestIntents.Remove(steamId, out var intent))
            return;

        _nadeTestTrackStates.Remove(steamId, out var track);

        if ((DateTime.Now - intent.At).TotalSeconds > NadeTestIntentSeconds)
        {
            thrower.PrintToChat(Localizer.ForPlayer(thrower, "nadetest.expired"));
            return;
        }

        // 启动真实轨迹录制：逐 tick 采样投射物位置/速度，打印供拟合真实重力/阻力/反弹参数
        _nadeTestTrajRecs[steamId] = new NadeTestTrajRec { Handle = entityHandle };
        thrower.PrintToConsole($"[PracLab] NadeTest traj start i=0 t=0.000 p=({pos.X:F1},{pos.Y:F1},{pos.Z:F1}) v=({vel.X:F1},{vel.Y:F1},{vel.Z:F1})");

        var pawn = thrower.PlayerPawn.Value;
        var eye = thrower.GetEyePosition();
        if (pawn == null || !pawn.IsValid || eye == null)
        {
            Server.PrintToConsole($"[PracLab] {DateTime.Now:HH:mm:ss} Warning NadeTest pawn/eye unavailable for {thrower.PlayerName}");
            return;
        }

        float viewPitch = pawn.EyeAngles.X;
        float viewYaw = pawn.EyeAngles.Y;
        var pv = pawn.AbsVelocity != null ? ToNumerics(pawn.AbsVelocity) : System.Numerics.Vector3.Zero;
        var real = ToNumerics(vel);
        float realSpeed = real.Length();

        // 自动识别（按键追踪可用时）；手动声明优先作为搜索模型对比基准
        string detectedType = "unknown";
        ThrowMode detectedMode = ThrowMode.Normal;
        ThrowStrength detectedStrength = ThrowStrength.Left;
        if (track != null)
        {
            (detectedType, detectedMode) = ClassifyThrowType(track);
            detectedStrength = DetectStrength(track);
        }

        var effStrength = intent.Strength ?? detectedStrength;
        var effMode = intent.Mode ?? detectedMode;

        // 模型 A：搜索仿真模型（站/跳/跑跳固定增量）
        var model = ComputeThrowVelocity(viewPitch, viewYaw, effStrength, effMode);
        float modelSpeed = model.Length();

        // 模型出手点（验证 ComputeThrowOrigin 拟合：水平 (19.4+6.7·s)·cos(p)，垂直 A(s)+B(s)·sin(p)+E(s)·cos(p)）
        var modelOrigin = ComputeThrowOrigin(ToNumerics(eye), viewPitch, viewYaw, effStrength);
        float dOriginXY = MathF.Sqrt((pos.X - modelOrigin.X) * (pos.X - modelOrigin.X) + (pos.Y - modelOrigin.Y) * (pos.Y - modelOrigin.Y));

        // 模型 B：straight_throw 逆向公式（方向速度 + 真实玩家速度×1.25）
        float correctedPitch = viewPitch - (90.0f - MathF.Abs(viewPitch)) * PitchCorrectionDeg / 90.0f;
        var dir = AngleToDirection(correctedPitch, viewYaw);
        float speedScale = (StrengthValue(effStrength) * 0.7f + 0.3f) * NadeBaseSpeed;
        var inherit = dir * speedScale + pv * NadeVelocityInheritFactor;
        float inheritSpeed = inherit.Length();

        // 真实出手方向（剔除投掷方式附加分量后归一化）反推出手 pitch。
        // 跳投系：Z 超出为固定值 JumpThrowVelocityZ（.nadetest 9 组跳投实测不随 pawnVel.z 波动），
        // 水平继承 1.25×pv.xy；若错误地按 1.25×pawnVel.z 扣除（pawnVel.z 随机波动 150~210），
        // 反推 dirPitch 会产生严重伪影（曾致 actualCorr=-29.5° 假异常）。非跳投沿用全向量继承。
        var realDirVec = IsJumpMode(effMode)
            ? real - new System.Numerics.Vector3(pv.X * NadeVelocityInheritFactor, pv.Y * NadeVelocityInheritFactor, JumpThrowVelocityZ)
            : real - pv * NadeVelocityInheritFactor;
        float realDirPitch = float.NaN;
        float pitchCorrActual = float.NaN;
        if (realDirVec.LengthSquared() > 1.0f)
        {
            var nd = System.Numerics.Vector3.Normalize(realDirVec);
            realDirPitch = -MathF.Asin(Math.Clamp(nd.Z, -1.0f, 1.0f)) * 180.0f / MathF.PI;
            pitchCorrActual = realDirPitch - viewPitch;
        }

        thrower.PrintToConsole($"[PracLab] NadeTest class={designerName} detected={detectedType}/{detectedStrength} declared={(intent.Strength?.ToString() ?? "-")}/{(intent.Mode?.ToString() ?? "-")} accDist={(track?.AccumulatedDistance ?? 0f):F1} moved={(track?.DidMoveOnGround ?? false)} duck={(track?.DidPressDuck ?? false)} walk={(track?.DidPressWalkKey ?? false)} view=({viewPitch:F1},{viewYaw:F1}) pawnVel=({pv.X:F0},{pv.Y:F0},{pv.Z:F0}) eye=({eye.X:F0},{eye.Y:F0},{eye.Z:F0}) spawn=({pos.X:F0},{pos.Y:F0},{pos.Z:F0}) spawnEyeDz={pos.Z - eye.Z:F1}");
        thrower.PrintToConsole($"[PracLab] NadeTest real=({real.X:F1},{real.Y:F1},{real.Z:F1}) |v|={realSpeed:F1} | inherit=({inherit.X:F1},{inherit.Y:F1},{inherit.Z:F1}) |v|={inheritSpeed:F1} dSpeed={realSpeed - inheritSpeed:+0.0;-0.0} dAngle={AngleBetweenDeg(real, inherit):F1} | model[{effMode}/{effStrength}]=({model.X:F1},{model.Y:F1},{model.Z:F1}) |v|={modelSpeed:F1} dSpeed2={realSpeed - modelSpeed:+0.0;-0.0} dAngle2={AngleBetweenDeg(real, model):F1}");
        thrower.PrintToConsole($"[PracLab] NadeTest dirPitch: real={realDirPitch:F1} view={viewPitch:F1} actualCorr={pitchCorrActual:F1} expectedCorr={correctedPitch - viewPitch:F1}");
        thrower.PrintToConsole($"[PracLab] NadeTest origin: real=({pos.X:F1},{pos.Y:F1},{pos.Z:F1}) model=({modelOrigin.X:F1},{modelOrigin.Y:F1},{modelOrigin.Z:F1}) dXY={dOriginXY:F1} dZ={pos.Z - modelOrigin.Z:F1}");

        // 输出完整移动信息：逐 tick 位置/速度/视角/按键/地面状态 + 聚合数据（跳跃高度/最大速度/累积距离）
        // 用于前跳投等投掷方式的移动信息回放，便于分析弹墙后下落衰减偏差根因
        if (track != null && track.MoveSamples.Count > 0)
        {
            float jumpHeight = track.HasBeenAirborne ? track.MaxAirZ - track.GroundZ : 0f;
            thrower.PrintToConsole($"[PracLab] NadeTest move summary: ticks={track.MoveSamples.Count} type={detectedType} strength={detectedStrength} jumpHeight={jumpHeight:F1} maxSpeed={track.MaxSpeed:F1} accDist={track.AccumulatedDistance:F1} groundZ={track.GroundZ:F1} maxAirZ={track.MaxAirZ:F1} maxAirHSpeed={track.MaxAirHorizontalSpeed:F1} airborne={track.HasBeenAirborne}");
            thrower.PrintToConsole($"[PracLab] NadeTest move begin: ticks={track.MoveSamples.Count} (btn: F=Forward B=Back L=Left R=Right J=Jump D=Duck W=Walk A=Attack a=Attack2)");
            foreach (var s in track.MoveSamples)
            {
                var btnStr = DecodeButtons((PlayerButtons)s.Buttons);
                thrower.PrintToConsole($"[PracLab] NadeTest move i={s.Tick} t={s.Tick / 64.0f:F3} p=({s.X:F1},{s.Y:F1},{s.Z:F1}) v=({s.Vx:F1},{s.Vy:F1},{s.Vz:F1}) ang=({s.Pitch:F1},{s.Yaw:F1}) btn={btnStr} ground={(s.OnGround ? 1 : 0)}");
            }
            thrower.PrintToConsole($"[PracLab] NadeTest move end: ticks={track.MoveSamples.Count}");
        }

        thrower.PrintToChat(Localizer.ForPlayer(thrower, "nadetest.captured",
            GetModeLocalizedName(thrower, effMode), GetStrengthLocalizedName(thrower, effStrength), realSpeed.ToString("F0")));
    }

    /// <summary>
    /// 玩家断连时清理其待测意图与按键追踪状态（EventHandlers.cs 注入点）。
    /// </summary>
    private void ClearNadeTestState(ulong steamId)
    {
        _nadeTestIntents.Remove(steamId);
        _nadeTestTrackStates.Remove(steamId);
    }

    /// <summary>
    /// 计算两向量夹角（度）；任一向量近零时返回 NaN。
    /// </summary>
    private static float AngleBetweenDeg(System.Numerics.Vector3 a, System.Numerics.Vector3 b)
    {
        if (a.LengthSquared() < 1e-6f || b.LengthSquared() < 1e-6f)
            return float.NaN;
        var na = System.Numerics.Vector3.Normalize(a);
        var nb = System.Numerics.Vector3.Normalize(b);
        return MathF.Acos(Math.Clamp(System.Numerics.Vector3.Dot(na, nb), -1.0f, 1.0f)) * 180.0f / MathF.PI;
    }
}
