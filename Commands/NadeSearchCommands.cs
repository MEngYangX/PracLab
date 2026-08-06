using System.Diagnostics;
using System.Numerics;
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
/// 投掷物反解搜索模块（T15）— 内核基础类型、纯函数与每玩家状态。
/// 搜索空间 = 3 种投掷方式 × 3 档力度；出手速度模型与粗筛为纯函数（无游戏 API 依赖，可单测）。
/// 弹道仿真器（GrenadeSim）、SearchJob 调度与命令处理器均在本文件内分区域实现。
/// </summary>
public partial class PracLab
{
    // ==================== 枚举与道具注册表 ====================

    /// <summary>
    /// 投掷方式：普通 / 跳投 / 前跳投 / 蹲下投掷 / 蹲下跳投 / 蹲下前跳投（只改变初始速度与出手眼位，仿真器不变）。
    /// 枚举序即 GroupIndexOf 的组索引基址（mode × 3 + strength），新增值须追加在末尾。
    /// </summary>
    private enum ThrowMode
    {
        Normal,
        Jump,
        RunJump,
        Duck,
        DuckJump,
        DuckRunJump,
    }

    /// <summary>
    /// 投掷力度：左键满力 / 中键（左右同按）中等 / 右键轻抛。
    /// </summary>
    private enum ThrowStrength
    {
        Left,
        Mid,
        Right,
    }

    /// <summary>
    /// 搜索精度档位：low=4° 无细化；mid=2° Top-5 细化 1°；high=1° Top-5 细化 0.25°。
    /// </summary>
    private enum NadeAccuracy
    {
        Low,
        Mid,
        High,
    }

    /// <summary>
    /// 道具终止类型：引信空爆 / 低速静止 / 触地或空爆超时。
    /// </summary>
    private enum NadeTermType
    {
        /// <summary>flash/he：固定引信 1.5s 取引爆瞬间空中位置。</summary>
        Fuse,

        /// <summary>smoke/decoy：低速静止或 10s 超时取当前位置。</summary>
        Rest,

        /// <summary>molo/inc：首次触地或 2s 未触地空爆超时（先到为准）。</summary>
        GroundOrAirburst,
    }

    /// <summary>
    /// 道具注册表条目。反弹衰减为 .nadetest 轨迹录制实测常数，按碰撞类型与速度分段（SimulateGrenade 中应用）：
    /// 弹墙（任意速度）：切向 + 法向均 0.45（6 次弹墙总衰减 0.448~0.457，墙面近切向入射衰减轻）；
    /// 高速弹地（|v_in|>SpeedDampThreshold≈500，首次弹地 |v_in|≈920）：切向 + 法向均 0.35
    ///   （实测切向 0.345、法向 0.347；旧值 0.45/0.42 在高速场景偏高 31%/21%，弹道飞过目标框）；
    /// 中低速弹地（|v_in|≤500，二次弹地 |v_in|≈225）：切向 + 法向均 0.45
    ///   （实测切向 0.450~0.477、法向 0.410~0.477；旧值统一 0.35 过度衰减，落点 y 偏短 ~17 跌出目标框）。
    /// 字段语义：BounceDampTangential=弹墙/中低速弹地切向(0.45)；BounceDampNormalWall=弹墙法向(0.45)；
    /// BounceDampNormalGround=高速弹地切向+法向(0.35，SimulateGrenade 中高速分支复用此字段)。
    /// 显示名通过 lang 键 nadesearch.nade_{Key} 本地化（GetNadeDisplayName）。
    /// </summary>
    /// <param name="Key">.nt 命令参数（smoke/flash/he/molo/inc/decoy）。</param>
    /// <param name="WeaponName">对应武器实体名。</param>
    /// <param name="Abbrev">结果 ID 前缀（SMK/FLH/HE/MOL/INC/DCY）。</param>
    /// <param name="Term">终止类型。</param>
    /// <param name="BounceDampTangential">弹墙/中低速弹地切向衰减系数（实测 0.45）；高速弹地切向见 BounceDampNormalGround。</param>
    /// <param name="BounceDampNormalWall">弹墙法向衰减系数（实测 0.45，墙面近切向入射衰减轻）。</param>
    /// <param name="BounceDampNormalGround">高速弹地（切向+法向）衰减系数（实测 0.345/0.347，舍入 0.35）；中低速弹地见 BounceDampTangential。</param>
    private sealed record NadeInfo(
        string Key,
        string WeaponName,
        string Abbrev,
        NadeTermType Term,
        float BounceDampTangential,
        float BounceDampNormalWall,
        float BounceDampNormalGround);

    /// <summary>
    /// 道具注册表（键即 .nt 参数，序即 .nt 无参数列表展示顺序）。
    /// </summary>
    private static readonly NadeInfo[] NadeRegistry =
    [
        new("smoke", "weapon_smokegrenade", "SMK", NadeTermType.Rest,             0.45f, 0.45f, 0.35f),
        new("flash", "weapon_flashbang",    "FLH", NadeTermType.Fuse,             0.45f, 0.45f, 0.35f),
        new("he",    "weapon_hegrenade",    "HE",  NadeTermType.Fuse,             0.45f, 0.45f, 0.35f),
        new("molo",  "weapon_molotov",      "MOL", NadeTermType.GroundOrAirburst, 0.45f, 0.45f, 0.35f),
        new("inc",   "weapon_incgrenade",   "INC", NadeTermType.GroundOrAirburst, 0.45f, 0.45f, 0.35f),
        new("decoy", "weapon_decoy",        "DCY", NadeTermType.Rest,             0.45f, 0.45f, 0.35f),
    ];

    /// <summary>
    /// 道具本地化显示名（lang 键 nadesearch.nade_{Key}，按玩家语言返回中/英文）。
    /// </summary>
    /// <param name="player">目标玩家。</param>
    /// <param name="nade">道具注册表条目。</param>
    /// <returns>本地化显示名。</returns>
    private string GetNadeDisplayName(CCSPlayerController player, NadeInfo nade)
        => Localizer.ForPlayer(player, $"nadesearch.nade_{nade.Key}");

    /// <summary>
    /// 每玩家搜索精度档位（默认 Mid）。
    /// </summary>
    private readonly Dictionary<ulong, NadeAccuracy> _nadeAccuracy = new();

    /// <summary>
    /// 每玩家道具选择（NadeRegistry.Key；未设置时默认 smoke 并提示）。
    /// </summary>
    private readonly Dictionary<ulong, string> _nadeType = new();

    // ==================== 物理常数（设计文档 §4.1 + CS2 社区逆向校准） ====================

    /// <summary>
    /// 出手基础速度（单位/秒）。CS2 逆向：clamp(m_flThrowVelocity × 0.9, 15, 750)，各雷 m_flThrowVelocity=750 → 675。
    /// </summary>
    private const float NadeBaseSpeed = 675.0f;

    /// <summary>
    /// 跳投竖直起跳速度增量（单位/秒）。.nadetest 18 组跳投系（站跳/蹲跳/前跳/蹲前跳 × 3 力度）实测：
    /// Z 超出量 = 1.25 × 起跳按压瞬间 pawn 竖直速度，均值 278.1（范围 271.7~284.9），站跳与蹲跳无显著差异。
    /// </summary>
    private const float JumpThrowVelocityZ = 278.0f;

    /// <summary>
    /// 蹲跳投竖直起跳速度增量（单位/秒）。.nadetest 18 组跳投系实测与站跳一致（见 JumpThrowVelocityZ）。
    /// </summary>
    private const float DuckJumpThrowVelocityZ = 278.0f;

    /// <summary>
    /// 跳投系出手眼位抬升量（单位）= 玩家起跳高度 jumpHeight。
    /// .nadetest 实测（duckjumpthrow/Left）：玩家起跳脚底 z=77.7，按压时刻脚底 z=121.3，
    /// jumpHeight = 43.6。投掷物在按压时刻生成，按压眼位 = 起跳后脚底 + 蹲/站眼高。
    /// 仿真流程：releaseEye = eye - DuckEyeOffsetZ(duck 系) + JumpReleaseLiftZ，
    /// 其中 DuckEyeOffsetZ 补偿站→蹲眼高位差，JumpReleaseLiftZ 补偿起跳抬升（= jumpHeight）。
    /// 旧值 29 误用「按压眼位 - 站姿眼位」(对 DuckJump ≈ 25.6~28.9) 作为抬升量，
    /// 但该差值已含 DuckEyeOffsetZ 抵消（= jumpHeight - 18），导致 releaseEye 偏低 14.6，
    /// 轨迹系统性偏低撞墙（.fdjl smoke 搜索失败根因，详见 debugger.md）。
    /// </summary>
    private const float JumpReleaseLiftZ = 43.6f;

    /// <summary>
    /// 前跳投水平助跑速度增量（单位/秒）。.nadetest 实测：跳投出手瞬间玩家水平速度 pawnVel.XY ≈30（非跑速250），
    /// 继承系数 1.25 → 增量 ≈37.5；两组前跳投（L/M）实测增量 38.1/40.8，均值 39.5，取 39。
    /// 旧值 312.5（=250×1.25）假设玩家以持刀跑速 250 奔跑，但实测 pawnVel.XY 仅 ~30，
    /// 导致预测水平速度偏大 ~58%，弹墙后轨迹系统性偏高、落点偏远。
    /// </summary>
    private const float RunJumpThrowVelocityXY = 39.0f;

    /// <summary>
    /// 重力加速度（单位/秒²）。.nadetest 轨迹录制实测：直飞段垂直速度每 2 tick 恒定 -10.0
    /// （64tick → 320/s² ≈ sv_gravity 800 × 0.4 投掷物重力系数），水平速度全程恒定（无空气阻力）。
    /// 此前误用 sv_gravity 原始值 800，导致预测下落过快、落点系统性偏近（"实际远度比预测大很多"根因）。
    /// </summary>
    private const float NadeGravity = 320.0f;

    /// <summary>pitch 修正幅度（度）：pitch' = pitch - (90-|pitch|) * PitchCorrectionDeg / 90。
    /// .nadetest 4 组全方式实测拟合（Normal/Duck k=9.5, Jump k=9.0，均值 9.25）：
    /// N-L-01(p=-18) actualCorr=-7.6→k=9.50；D-L-01(p=-19.9) actualCorr=-7.4→k=9.50；
    /// J-L-01(p=12) actualCorr=-7.8→k=9.00；J-L-02(p=16) actualCorr=-7.4→k=9.00。
    /// 取均值 9.25 兼顾 Normal/Duck（修正偏小 0.4°）与 Jump（修正偏大 0.1°），误差均控制在 0.15° 内。
    /// 注：Jump 模式 k 偏小可能源于起跳姿态差异，待更多样本验证后可考虑分方式拟合。</summary>
    private const float PitchCorrectionDeg = 9.25f;

    /// <summary>
    /// flash/he 固定引信时长（秒）。.nadetest 轨迹录制实测：闪/雷均在生成后第 ~105 tick 引爆（64tick ≈ 1.65s）。
    /// </summary>
    private const float FuseSeconds = 1.65f;

    /// <summary>molo/inc 空爆超时时长（秒）。</summary>
    private const float AirburstTimeoutSeconds = 2.0f;

    /// <summary>
    /// smoke/decoy 静止判定速度阈值（单位/秒）。
    /// 20：CS2 引擎在 |v| < ~20 时直接归零速度（.nadetest 实测 i=268 |v|=18.1 → i=270 |v|=0）。
    /// 旧值 50 导致第 4 次弹跳后（|v| 在 18.8~40.2 间波动，全部 &lt;50）连续 8 tick 误判静止，
    /// 漏掉第 5 次弹跳（i=262 |v|=15.9），表现为"后半段预测轨迹落地后没有弹跳"。
    /// 20 能让仿真正确模拟第 5 次弹跳，并在第 5 次弹跳后约 4~8 tick 判定静止，接近实际 i=270 静止点。
    /// 撞地反弹静止判定（.nadetest 10 组触地样本，弹地法向衰减 0.42 换算反弹后 |v|）：
    /// 静止组（引擎下一 tick 直接 v=0）|v|=9.1 / 13.9 / 16.4，全部 &lt;20；
    /// 弹跳组 |v|=32.8 / 36.3 / ≥20.9，全部 &gt;20——阈值 20 干净区分两组。
    /// 反例：组1 第 6 次触地反弹后 v.Z=12.56（超出旧 v.Z&lt;12 条件）但 |v|=13.9，引擎判定静止，
    /// 证明仅用 |v|&lt;20 一条规则即可，无需 v.Z 条件。
    /// </summary>
    private const float RestSpeedThreshold = 20.0f;

    /// <summary>smoke/decoy 静止判定持续 tick 数（仿真 tick）。</summary>
    private const int RestTicksRequired = 8;

    /// <summary>smoke/decoy 仿真超时时长（秒）。</summary>
    private const float RestTimeoutSeconds = 10.0f;

    /// <summary>pitch 扫描下限（度，负值=抬头，覆盖高抛 80°）。</summary>
    private const float PitchScanMin = -80.0f;

    /// <summary>pitch 扫描上限（度，正值=低头）。</summary>
    private const float PitchScanMax = 60.0f;

    /// <summary>蹲下出手眼位相对站立眼位的下沉量（CS2 站立眼高 ~64、蹲下 ~46）。duck 系列搜索时站立玩家自动下调眼位。</summary>
    private const float DuckEyeOffsetZ = 18.0f;

    /// <summary>
    /// 出手点相对眼位的水平前向距离基值（沿 yaw 方向，随 cos(pitch) 缩放，单位）。
    /// .nadetest 静止投掷实测：H = H0(s)·cos(pitch)，H0(0)=19.4（p=-2.4°/4.3° 两样本均值），
    /// H0(1)=26.1（p=-2.1°），力度线性：H0(s) = 19.4 + 6.7·s。
    /// </summary>
    private const float SpawnForwardBase = 19.4f;

    /// <summary>出手点水平前向距离的力度系数（见 SpawnForwardBase）。</summary>
    private const float SpawnForwardPerStrength = 6.7f;

    /// <summary>
    /// 出手点垂直偏移拟合：Z(s,p) = A(s) + B(s)·sin(p) + E(s)·cos(p)，各系数按力度线性插值。
    /// .nadetest 六组站立静止投掷全 pitch 扫掠（-89°/-2.4°/+89° × 左/右键）精确拟合（残差 &lt;0.1）。
    /// E·cos(p) 项不可省略：平视时出手点偏上 ~3.3~4.6 单位，纯 sin 模型在 p≈0° 处残留 2~3 单位系统偏差。
    /// 注意：SMK-N-R-01（p=-56°）与旧跳投中键（p=34.1°，Z=-25.8）样本均为 .ng 传送后
    /// 「躺倒」状态所测（AbsRotation.X 异常导致身体骨骼倾斜、手臂下垂），属脏数据已剔除；
    /// 站立状态出手点待干净跳投扫掠样本验证。
    /// </summary>
    private const float SpawnVertABase = -12.11f;

    /// <summary>垂直偏移截距 A 的力度系数（见 SpawnVertABase）。</summary>
    private const float SpawnVertAPerStrength = 11.98f;

    /// <summary>垂直偏移 sin(p) 系数 B 基值（见 SpawnVertABase）。</summary>
    private const float SpawnVertBBase = -19.15f;

    /// <summary>垂直偏移 sin(p) 系数 B 的力度系数（见 SpawnVertABase）。</summary>
    private const float SpawnVertBPerStrength = -7.40f;

    /// <summary>垂直偏移 cos(p) 系数 E 基值（见 SpawnVertABase）。</summary>
    private const float SpawnVertEBase = 3.31f;

    /// <summary>垂直偏移 cos(p) 系数 E 的力度系数（见 SpawnVertABase）。</summary>
    private const float SpawnVertEPerStrength = 1.25f;

    /// <summary>粗筛抛物线弧采样点数。</summary>
    private const int ArcCoarseSamples = 64;

    /// <summary>
    /// 粗筛抛物线弧采样总时长（秒）。长于引信/飞行时间不影响正确性（保守保留）。
    /// 8 秒覆盖远距离高抛投掷的完整飞行（直飞弧线到达目标盒体需足够采样时长，
    /// 5 秒对部分远距离点位采样提前结束，弧线未触及盒体被误剔除）。
    /// </summary>
    private const float ArcCoarseDuration = 8.0f;

    // ==================== 出手速度模型（纯函数） ====================

    /// <summary>
    /// QAngle → 单位方向向量（Source 2 约定：pitch +down/-up，负值抬头；yaw 逆时针）。
    /// forward = (cosP·cosY, cosP·sinY, -sinP)。
    /// </summary>
    /// <param name="pitchDeg">俯仰角（度，Source 约定）。</param>
    /// <param name="yawDeg">偏航角（度）。</param>
    /// <returns>单位方向向量。</returns>
    private static Vector3 AngleToDirection(float pitchDeg, float yawDeg)
    {
        float pitchRad = pitchDeg * MathF.PI / 180.0f;
        float yawRad = yawDeg * MathF.PI / 180.0f;
        float cosP = MathF.Cos(pitchRad);
        return new Vector3(
            cosP * MathF.Cos(yawRad),
            cosP * MathF.Sin(yawRad),
            -MathF.Sin(pitchRad));
    }

    /// <summary>
    /// 力度档位 → 力度系数（左键满力 1.0 / 中键 0.5 / 右键 0.0）。
    /// </summary>
    private static float StrengthValue(ThrowStrength strength) => strength switch
    {
        ThrowStrength.Left => 1.0f,
        ThrowStrength.Mid => 0.5f,
        _ => 0.0f,
    };

    /// <summary>
    /// 计算出手初速度：pitch 修正 + 力度→速度映射 + 投掷方式速度叠加（.nadetest 实测校准）。
    /// pitch' = pitch - (90-|pitch|)·9.1/90；v0 = dir·((strength·0.7+0.3)·675)；
    /// Jump +270z；RunJump +312.5 水平（跑速 250×1.25，沿 yaw）+270z；
    /// Duck 与 Normal 速度模型一致（实测相同）；DuckJump +280z；DuckRunJump +312.5 水平 +280z。
    /// </summary>
    /// <param name="pitchDeg">玩家瞄准 pitch（度，Source 约定）。</param>
    /// <param name="yawDeg">玩家瞄准 yaw（度）。</param>
    /// <param name="strength">投掷力度。</param>
    /// <param name="mode">投掷方式。</param>
    /// <returns>出手初速度向量（单位/秒）。</returns>
    private static Vector3 ComputeThrowVelocity(float pitchDeg, float yawDeg, ThrowStrength strength, ThrowMode mode)
    {
        float strengthValue = StrengthValue(strength);

        float correctedPitch = pitchDeg - (90.0f - MathF.Abs(pitchDeg)) * PitchCorrectionDeg / 90.0f;
        var dir = AngleToDirection(correctedPitch, yawDeg);
        var v0 = dir * ((strengthValue * 0.7f + 0.3f) * NadeBaseSpeed);

        return mode switch
        {
            ThrowMode.Jump => v0 + new Vector3(0, 0, JumpThrowVelocityZ),
            ThrowMode.RunJump => v0 + AngleToDirection(0, yawDeg) * RunJumpThrowVelocityXY + new Vector3(0, 0, JumpThrowVelocityZ),
            ThrowMode.DuckJump => v0 + new Vector3(0, 0, DuckJumpThrowVelocityZ),
            ThrowMode.DuckRunJump => v0 + AngleToDirection(0, yawDeg) * RunJumpThrowVelocityXY + new Vector3(0, 0, DuckJumpThrowVelocityZ),
            // Normal / Duck：实测蹲投与站投速度模型完全一致
            _ => v0,
        };
    }

    /// <summary>是否为 duck 系列投掷方式（蹲投/蹲跳投/蹲前跳投）。</summary>
    private static bool IsDuckMode(ThrowMode mode) => mode >= ThrowMode.Duck;

    /// <summary>是否为跳投系投掷方式（跳投/前跳投/蹲跳投/蹲前跳投，出手时玩家已起跳上升）。</summary>
    private static bool IsJumpMode(ThrowMode mode) => mode is ThrowMode.Jump or ThrowMode.RunJump or ThrowMode.DuckJump or ThrowMode.DuckRunJump;

    /// <summary>
    /// 按投掷方式取仿真出手眼位：duck 系列自传入眼位（通常为站立眼位）下沉 DuckEyeOffsetZ，
    /// 使站立玩家也能直接搜索蹲投点位（用户确认的设计，眼高 64→46）。
    /// </summary>
    private static Vector3 EyeForMode(Vector3 eye, ThrowMode mode)
        => IsDuckMode(mode) ? new Vector3(eye.X, eye.Y, eye.Z - DuckEyeOffsetZ) : eye;

    /// <summary>
    /// 按投掷方式取按压时刻出手眼位：在 EyeForMode 基础上，跳投系再抬升 JumpReleaseLiftZ（= jumpHeight 43.6）。
    /// 物理流程：玩家先蹲（EyeForMode 减 DuckEyeOffsetZ）→ 起跳（+ jumpHeight）→ 按压时刻投掷物生成。
    /// </summary>
    private static Vector3 ReleaseEyeForMode(Vector3 eye, ThrowMode mode)
    {
        var e = EyeForMode(eye, mode);
        return IsJumpMode(mode) ? new Vector3(e.X, e.Y, e.Z + JumpReleaseLiftZ) : e;
    }

    /// <summary>
    /// 计算投掷物真实生成位置（出手点）：眼位 + 水平前向 H(s,p)·dir_yaw + 垂直偏移 Z(s,p)。
    /// H(s,p) = (19.4+6.7·s)·cos(p)；Z(s,p) = A(s) + B(s)·sin(p) + E(s)·cos(p)，
    /// A(s) = -12.11+11.98·s，B(s) = -19.15-7.40·s，E(s) = 3.31+1.25·s。
    /// .nadetest 六组站立静止投掷全 pitch 扫掠精确拟合（残差 &lt;0.1）。仿真必须从出手点起步，
    /// 从眼位起步会让落点系统性偏远 ~20+ 单位（短中距离目标尤其明显）。
    /// 脏数据说明：极端抬头（pitch≈-42°）右键单样本（水平偏移眼位后方 29，与任何前向模型矛盾）、
    /// SMK-N-R-01（p=-56°）与旧跳投中键（p=34.1°）样本均来自 .ng 传送后「躺倒」状态
    /// （AbsRotation.X 异常 → 身体骨骼倾斜、手臂下垂，躺倒修复前所测），已全部剔除；
    /// 站立跳投出手点待干净扫掠样本验证。
    /// </summary>
    /// <param name="eye">出手眼位（已经过 EyeForMode/ReleaseEyeForMode 方式调整）。</param>
    /// <param name="pitchDeg">瞄准 pitch（度，Source 约定）。</param>
    /// <param name="yawDeg">瞄准 yaw（度）。</param>
    /// <param name="strength">投掷力度。</param>
    /// <returns>出手点世界坐标。</returns>
    private static Vector3 ComputeThrowOrigin(Vector3 eye, float pitchDeg, float yawDeg, ThrowStrength strength)
    {
        float s = StrengthValue(strength);
        float pitchRad = pitchDeg * MathF.PI / 180.0f;
        float yawRad = yawDeg * MathF.PI / 180.0f;
        float sinP = MathF.Sin(pitchRad);
        float cosP = MathF.Cos(pitchRad);
        float h = (SpawnForwardBase + SpawnForwardPerStrength * s) * cosP;
        float z = SpawnVertABase + SpawnVertAPerStrength * s
                + (SpawnVertBBase + SpawnVertBPerStrength * s) * sinP
                + (SpawnVertEBase + SpawnVertEPerStrength * s) * cosP;
        return new Vector3(eye.X + h * MathF.Cos(yawRad), eye.Y + h * MathF.Sin(yawRad), eye.Z + z);
    }

    // ==================== 粗筛与搜索空间（纯函数） ====================

    /// <summary>
    /// 计算点到 AABB 盒体的最小距离（盒内为 0）。
    /// </summary>
    private static float DistanceToBox(Vector3 p, Vector3 min, Vector3 max)
    {
        float dx = MathF.Max(MathF.Max(min.X - p.X, 0f), p.X - max.X);
        float dy = MathF.Max(MathF.Max(min.Y - p.Y, 0f), p.Y - max.Y);
        float dz = MathF.Max(MathF.Max(min.Z - p.Z, 0f), p.Z - max.Z);
        return MathF.Sqrt(dx * dx + dy * dy + dz * dz);
    }

    /// <summary>
    /// 粗筛：整条无碰撞抛物线弧（采样 ArcCoarseSamples 点 / ArcCoarseDuration 秒）到目标盒体的最小距离。
    /// 调用方以「距离 > arc_margin 则跳过」剪枝——弧从盒体上方/旁边路过的角度全部保留。
    /// 弹墙场景限制：纯抛物线无法模拟反弹，弹墙弹道的直飞弧线（玩家→墙壁）不经过目标盒体，
    /// 依赖 arc_margin 放宽（默认 1500）让墙壁附近路过的弧线通过，完整仿真阶段（含 TraceShape 反弹）
    /// 会正确识别弹墙弹道落点。粗筛在 BuildSearchJob 同步执行，不能调用 TraceShape（会阻塞主线程）。
    /// </summary>
    /// <param name="origin">出手点（眼位）。</param>
    /// <param name="velocity">出手初速度。</param>
    /// <param name="boxMin">目标盒体最小角。</param>
    /// <param name="boxMax">目标盒体最大角。</param>
    /// <returns>弧到盒体的最小距离（游戏单位）。</returns>
    private static float ArcMinDistanceToBox(Vector3 origin, Vector3 velocity, Vector3 boxMin, Vector3 boxMax)
    {
        float minDist = float.MaxValue;
        float dt = ArcCoarseDuration / ArcCoarseSamples;

        for (int i = 0; i <= ArcCoarseSamples; i++)
        {
            float t = i * dt;
            // 自由抛物线：p = origin + v·t - (0,0,g·t²/2)
            var p = new Vector3(
                origin.X + velocity.X * t,
                origin.Y + velocity.Y * t,
                origin.Z + velocity.Z * t - NadeGravity * t * t * 0.5f);

            float dist = DistanceToBox(p, boxMin, boxMax);
            if (dist < minDist)
                minDist = dist;

            // 已远低于盒体且仍在下落：提前退出（后续只会更远）
            if (p.Z < boxMin.Z - 2000.0f && velocity.Z - NadeGravity * t < 0f)
                break;
        }

        return minDist;
    }

    /// <summary>
    /// 将角度规范化到 (-180, 180] 区间。
    /// </summary>
    private static float NormalizeAngleDeg(float angleDeg)
    {
        angleDeg %= 360.0f;
        if (angleDeg > 180.0f) angleDeg -= 360.0f;
        else if (angleDeg <= -180.0f) angleDeg += 360.0f;
        return angleDeg;
    }

    /// <summary>
    /// yaw 搜索范围。始终返回全圆 360°。
    /// 早期版本仅生成朝向目标盒体的 yaw 扇形（+marginDeg 余量）以减少候选数，
    /// 但弹墙点位（玩家朝墙壁投掷、雷反弹后到达目标）的投掷方向不在朝向目标的扇形内，
    /// 导致弹墙道具系统性搜索失败。改为全扫 360° 覆盖所有可能方向（含弹墙方向），
    /// 候选数增加的代价由粗筛（纯数学）与完整仿真命中判定吸收。
    /// </summary>
    /// <param name="standPos">站位（眼位，保留参数以维持签名兼容）。</param>
    /// <param name="boxMin">目标盒体最小角（保留参数以维持签名兼容）。</param>
    /// <param name="boxMax">目标盒体最大角（保留参数以维持签名兼容）。</param>
    /// <param name="marginDeg">扇形余量（保留参数以维持签名兼容，不再使用）。</param>
    /// <returns>(0, 0, true)：始终全扫 360°。</returns>
    private static (float YawMin, float YawMax, bool FullCircle) ComputeYawSector(
        Vector3 standPos, Vector3 boxMin, Vector3 boxMax, float marginDeg)
        => (0f, 0f, true);

    /// <summary>
    /// 解析求解命中盒体中心所需的低弧 pitch（无碰撞弹道，供候选排序参考）。
    /// 无解（超出射程）时回退 -45°（45° 高抛）。
    /// </summary>
    /// <param name="speed">出手速率（单位/秒）。</param>
    /// <param name="horizontalDist">到目标的水平距离。</param>
    /// <param name="heightDiff">目标高度 - 出手高度。</param>
    /// <returns>参考 pitch（度，Source 约定，负值=抬头）。</returns>
    private static float SolveBallisticPitch(float speed, float horizontalDist, float heightDiff)
    {
        if (horizontalDist < 1e-3f)
            return -89.0f; // 正上方

        float s2 = speed * speed;
        float g = NadeGravity;
        float discriminant = s2 * s2 - g * (g * horizontalDist * horizontalDist + 2.0f * heightDiff * s2);

        if (discriminant <= 0f)
            return -45.0f; // 超出射程：45° 高抛参考

        // 低弧解：tanθ = (s² - √Δ) / (g·d)
        float tanTheta = (s2 - MathF.Sqrt(discriminant)) / (g * horizontalDist);
        float pitchUpDeg = MathF.Atan(tanTheta) * 180.0f / MathF.PI;

        // 数学解的 θ 为抬头正角 → Source 约定取负
        return -pitchUpDeg;
    }

    // ==================== GrenadeSim 弹道仿真器（主线程专用） ====================

    /// <summary>反弹后沿法线的推出距离（游戏单位），避免下一步重复命中同一表面。</summary>
    private const float BounceEpsilon = 0.125f;

    /// <summary>
    /// 投掷物静止半径（游戏单位）。仿真用 TraceShape（零半径射线）命中地面表面，真实投掷物有碰撞半径，
    /// 静止时中心位于 地面表面 + 半径。.nadetest 实测：仿真落点 z=-28.9（表面），真实落点 z=-26.9（中心），差 2.0。
    /// 在地面静止/触地终止时给 LandPoint 沿法线加此偏移，补偿 TraceHull 弃用（引擎 AllSolid 缺陷）带来的半径缺失。
    /// </summary>
    private const float GrenadeRestRadius = 2.0f;

    /// <summary>
    /// 手雷碰撞掩码：仅 Solid（世界几何/静态道具）。
    /// 不含 PassBullets —— 栅栏/铁丝网等可穿弹面被真实 grenade 物理穿过不发生反弹，
    /// 含此位时预测会提前撞墙反弹（SMK-N-L-01 案例：预测在 (523,641.7,188) 撞 PassBullets 面反弹，
    /// 真实 grenade 穿过继续飞行 0.042s 后才在 (515,652,192) 撞真正墙体，导致 bounce[0] 之后轨迹完全偏离）；
    /// 不含 Window —— 真实投掷物击碎玻璃后近乎无减速穿过，排除后窗口类点位轨迹更接近真实；
    /// 不含 Player/Npc —— 避免搜索者自身与 bot 干扰弹道；
    /// 不含 Sky —— 含 Sky 位时引擎将眼位误判为处于天空体内部，TraceShape 恒返回 AllSolid/Fraction=0
    /// （search-no-results 根因，见 debug-search-no-results.md）。
    /// </summary>
    private const ulong GrenadeTraceMask = (ulong)Contents.Solid;

    /// <summary>命中面法线 Z 阈值（&gt; 此值视为地面，与绘制模块共用）。</summary>
    private const float SimGroundNormalZ = 0.7f;

    /// <summary>
    /// 弹地衰减速度分段阈值（单位/秒）。地面碰撞速度高于此值取高速衰减 0.35，低于取中低速衰减 0.45。
    /// .nadetest 实测：首次弹地 |v_in|≈920（高速，衰减 0.345/0.347），二次弹地 |v_in|≈234（中速，衰减 0.450~0.477）；
    /// 阈值 500 居中分隔。弹墙衰减不随速度变化（始终 0.45）。详见 NadeInfo 类注释与 debugger.md「切向衰减速度相关修复」。
    /// </summary>
    private const float SpeedDampThreshold = 500.0f;

    /// <summary>
    /// 弹道仿真结果。
    /// </summary>
    private sealed class GrenadeSimResult
    {
        /// <summary>落点（按道具定义：引信空爆点 / 静止点 / 触地点或空爆超时点）。</summary>
        public Vector3 LandPoint;

        /// <summary>终止原因（fuse/rest/timeout/ground/airburst/sky/allsolid/trace_error/hit_box），供调试日志。</summary>
        public string TermReason = string.Empty;

        /// <summary>仿真飞行时长（秒）。</summary>
        public float FlightTime;

        /// <summary>
        /// "任意触地即命中"首次落入框选盒体的触地位置（仅 boxMin/boxMax 非 null 时可能非 null）。
        /// 仿真不会在此位置提前终止（保证轨迹完整），由调用方优先使用此值作为命中落点。
        /// 修复"预测轨迹在接触到绘制框时就戛然而止"问题：旧实现提前终止仿真导致轨迹不完整。
        /// </summary>
        public Vector3? BoxHitPoint;
    }

    /// <summary>
    /// 单次碰撞信息（调试插桩，仅记录数值避免热路径字符串分配）。
    /// 命中后由调用方格式化为 NadeResult.BounceLog 字符串。
    /// </summary>
    private struct BounceInfo
    {
        public Vector3 Pos;
        public Vector3 RawN;   // 引擎原始法线
        public Vector3 ProbeN; // ProbeWallNormalZ 修正后法线
        public Vector3 VIn;    // 碰撞时刻入射速度
        public Vector3 VOut;   // 反弹后速度
        public uint Contents;  // 碰撞面 contents 位掩码（用于诊断误撞 PlayerClip 等）
        public string? ProbeNote; // ProbeWallNormalZ 降级原因（null=成功恢复）
    }

    /// <summary>
    /// 单 tick 内子步碰撞上限：高速雷（770u/s）在 128Hz 下每 tick 位移 6u，
    /// 撞击斜坡/墙地交界时一个 tick 内可能发生 2-3 次碰撞（先撞墙→再撞地）。
    /// 设上限 4 避免极端场景（如卡在缝隙）无限循环。
    /// </summary>
    private const int MaxSubSteps = 4;

    /// <summary>
    /// 单次仿真墙碰撞次数上限：防止 ProbeWallNormalZ 修改反弹方向后 grenade 进入墙缝/复杂地形
    /// 反复撞墙，10s 超时内 trace 次数激增（最坏 1280 tick × 8 trace = 10240 次）导致单次仿真
    /// 耗时数十秒卡死主线程。正常 smoke 仿真墙碰撞 1-3 次，30 次上限足够宽松。
    /// </summary>
    private const int MaxWallBounces = 30;

    /// <summary>
    /// 单次仿真真实耗时上限（毫秒）：兜底保护，防止 MaxWallBounces 未覆盖的慢路径
    /// （如 TraceShape 在复杂几何体上的慢路径）导致单次仿真卡死主线程。
    /// 50ms 远高于正常仿真（0.1-1ms），不会误杀。
    /// 用 Environment.TickCount64 计时避免 Stopwatch 堆分配。
    /// </summary>
    private const int MaxSimMs = 50;

    /// <summary>
    /// ProbeWallNormalZ 多点探测高度集合（避开 0，防止探测点正好在碰撞点高度）：
    /// 6 点覆盖 Z=±5/±10/±15，扩大采样范围以分辨碰撞点附近垂直、远处倾斜的墙面。
    /// 类级别 static readonly 避免每次调用堆分配。
    /// </summary>
    private static readonly float[] ProbeHeights = { -15.0f, -10.0f, -5.0f, 5.0f, 10.0f, 15.0f };

    /// <summary>
    /// GrenadeSim：固定步长弹道仿真（仅可在主线程调用，每步一次或多次引擎 TraceShape 线段扫掠）。
    /// 积分：半隐式欧拉（先重力后位移），dt = 1/simHz；碰撞体按线段近似（grenade 半径 4 远小于目标盒体尺度，
    /// TraceHull+CTraceFilter 通道存在引擎层封送缺陷——全部查询恒返回 AllSolid，已弃用，见 debug-search-no-results.md）。
    /// 反弹模型：法向/切向分离衰减，v' = D_t × v_t - D_n × v_n（切向 D_t=0.45，法向 D_n=0.35 弹地 / 0.45 弹墙）。
    /// 子步碰撞处理：一个 tick 内检测到碰撞后，用剩余时间 (1-fraction)×dt 继续做 TraceShape，
    /// 直到走完整个 dt 或达到 MaxSubSteps 上限。解决高速撞击斜坡/墙地交界处单 tick 多次碰撞遗漏问题。
    /// 重力分摊：每个子步开始时按子步时间施加重力（v.Z -= g·remainingDt），碰撞时修正碰撞时刻速度
    /// （加回多减的 g·(1-fraction)·remainingDt），确保反弹后的剩余时间内 v 继续受重力影响。
    /// 旧实现（tick 开始一次施加整 dt 重力 + 子步内不再追加）会导致弹墙后 v.Z 系统性偏大、落点偏远。
    /// 终止与落点按道具分开：
    /// - flash/he：1.5s 固定引信，取引爆瞬间空中位置；
    /// - smoke/decoy：速度 &lt; 20 且最后接触面法线 z &gt; 0.7 持续 8 tick 判静止，或 10s 超时，取当前位置；
    /// - molo/inc：首次触地（法线 z &gt; 0.7）取命中点，或 2s 未触地空爆超时取当前位置（先到为准）。
    /// 命中天空盒（Contents.Sky）立即终止（防御性保留——掩码不含 Sky 位后该分支不可达）；AllSolid / trace 异常同样终止。
    /// 任意触地即命中（boxMin/boxMax 非 null 时启用）：smoke/decoy 在每次触地（法线 z &gt; SimGroundNormalZ）后
    /// 检查碰撞位置是否在框选盒体内，若在则记录到 result.BoxHitPoint（首次命中位置）。
    /// 不提前终止仿真：保证轨迹完整显示后续弹跳（修复"预测轨迹在接触到绘制框时就戛然而止"问题）。
    /// 调用方优先使用 BoxHitPoint 作为命中落点；若 BoxHitPoint 为 null 则回退到最终静止位置（覆盖场景2）。
    /// 修复"第一次落地在区域内但弹跳出区域外"漏搜问题：旧实现只检查最终静止位置，导致场景1漏搜。
    /// </summary>
    /// <param name="origin">出手点（眼位）。</param>
    /// <param name="velocity">出手初速度（ComputeThrowVelocity）。</param>
    /// <param name="nade">道具注册表条目。</param>
    /// <param name="simHz">仿真步频（praclab_nade_sim_hz）。</param>
    /// <param name="trajectory">轨迹采样输出（调用方复用的 scratch 列表，每 4 tick 追加一点；null 不采样）。</param>
    /// <param name="skipPawn">trace 跳过的实体句柄（搜索者自身 pawn；Zero 不跳过）。</param>
    /// <param name="boxMin">目标盒体最小角（非 null 时启用"任意触地即命中"判定；null 仅仿真不判命中）。</param>
    /// <param name="boxMax">目标盒体最大角（与 boxMin 配对）。</param>
    /// <param name="bounces">碰撞复用缓冲（调用方传入，仿真前 Clear；前 3 次碰撞记录数值，命中后格式化）。</param>
    /// <returns>仿真结果（落点 + 终止原因 + 飞行时长）。</returns>
    private GrenadeSimResult SimulateGrenade(Vector3 origin, Vector3 velocity, NadeInfo nade, int simHz, List<Vector3>? trajectory, nint skipPawn, Vector3? boxMin = null, Vector3? boxMax = null, List<BounceInfo>? bounces = null)
    {
        float dt = 1.0f / simHz;
        var position = origin;
        var v = velocity;
        float elapsed = 0f;
        int restTicks = 0;
        float lastSurfaceNormalZ = 0f;
        // 碰撞计数：前 3 次碰撞记录到 bounces 缓冲，定位"弹墙方向与真实不符"
        int bounceCount = 0;
        // 墙碰撞计数：超过 MaxWallBounces 提前终止，防止 ProbeWallNormalZ 修改反弹方向后
        // grenade 卡在墙缝/复杂地形反复撞墙导致单次仿真卡死主线程
        int wallBounceCount = 0;
        // 单次仿真真实耗时上限（Environment.TickCount64 无堆分配，适合热路径）
        long simStartMs = Environment.TickCount64;
        int tick = 0;

        var result = new GrenadeSimResult();

        while (true)
        {
            // 单次仿真真实耗时兜底：防止 MaxWallBounces 未覆盖的慢路径卡死主线程。
            // 每 16 tick 检查一次（约 125ms 仿真时间），降低 Environment.TickCount64 调用开销。
            if ((tick & 15) == 0 && Environment.TickCount64 - simStartMs >= MaxSimMs)
            {
                result.LandPoint = position;
                result.TermReason = "sim_budget";
                result.FlightTime = elapsed;
                Server.PrintToConsole($"[PracLab] {DateTime.Now:HH:mm:ss} Warning SimulateGrenade exceeded {MaxSimMs}ms budget (wallBounces={wallBounceCount} tick={tick}), terminating early");
                return result;
            }

            // 子步碰撞处理：用 remainingDt 跟踪当前 tick 内剩余的位移时间。
            // 重力按子步时间分摊（不在 tick 开始一次施加），确保反弹后的剩余时间内 v 继续受重力影响。
            // 旧实现（tick 开始一次施加整 dt 重力 + 子步内不再追加）会导致弹墙后 v.Z 系统性偏大、落点偏远。
            float remainingDt = dt;
            int subStep = 0;

            while (remainingDt > 1e-4f && subStep < MaxSubSteps)
            {
                subStep++;

                // 半隐式欧拉：按子步时间施加重力（先重力后位移）
                v.Z -= NadeGravity * remainingDt;

                var subNext = position + v * remainingDt;

                CGameTrace trace;
                try
                {
                    // TraceShape 线段扫掠（与 NadeDraw 取点同一通道，跳过搜索者自身 pawn）；
                    // mask 语义：仅 Solid（不撞栅栏/玻璃/玩家；不含 Sky 位，见 GrenadeTraceMask 注释），NoDraw 面忽略
                    trace = TraceRay.TraceShape(ToCssVector(position), ToCssVector(subNext), GrenadeTraceMask, (ulong)Contents.NoDraw, skipPawn);
                }
                catch (Exception ex)
                {
                    // TraceShape 异常（gamedata 签名失效等）：落点取当前位置并记录，禁止静默失败
                    Server.PrintToConsole($"[PracLab] {DateTime.Now:HH:mm:ss} Error GrenadeSim TraceShape failed - {ex.Message}");
                    result.LandPoint = position;
                    result.TermReason = "trace_error";
                    result.FlightTime = elapsed;
                    return result;
                }

                if (trace.Fraction >= 1.0f)
                {
                    // 无碰撞：走完剩余时间。
                    // 注意：短线段无命中时引擎返回病态 AllSolid=True（探针 C 证据：frac=1.0 + allsolid=True + contents=0x0），
                    // AllSolid 仅在 Fraction<1.0 时可信（探针 F 真实卡体内特征：frac=0 + allsolid + 命中实体），必须先判 Fraction。
                    position = subNext;
                    remainingDt = 0f;
                }
                else
                {
                    // 命中天空盒（防御性保留：掩码不含 Sky 位后不可达）：真实投掷物被删除，立即终止
                    if ((trace.Contents & (uint)Contents.Sky) != 0)
                    {
                        result.LandPoint = trace.Position;
                        result.TermReason = "sky";
                        result.FlightTime = elapsed;
                        return result;
                    }

                    // 非固体面穿透：contents 不含 Solid(0x1) 的面（PlayerClip/trigger/clip 等）
                    // 真实 grenade 用 collision group 过滤这类面，仿真 TraceShape 无 filter 会误撞。
                    // 实测 .nts 90.3 -13.5 DJ L bounce[0] contents=0x40000030（无 Solid bit）误撞导致弹道反向。
                    // 穿透策略：position 推进到碰撞点 + 沿运动方向微偏移，不反弹，继续子步受重力影响。
                    // 安全性：MaxSubSteps 限制子步数，RestTimeoutSeconds 限制总时长，不会死循环。
                    if ((trace.Contents & 0x1u) == 0)
                    {
                        var moveDir = v.LengthSquared() > 1.0f ? Vector3.Normalize(v) : new Vector3(0, 0, 1);
                        position = trace.Position + moveDir * BounceEpsilon;
                        remainingDt *= (1.0f - trace.Fraction);
                        continue;
                    }

                    // AllSolid 处理：当 trace 起点被认为在固体内部时，不直接终止，
                    // 而是落入下方正常碰撞处理流程（用法线弹跳）。
                    // 证据：SMK-N-L-01 预测在地面对撞点 (766,854,2.6) 返回 allsolid 终止，
                    // 但真实投掷物在同一位置正常弹跳（v.Z 从 -365 反弹到 +158）。
                    // 原因：子步内 grenade shape 深入地面，trace 起点被判定为"卡体"，
                    // 但 trace.Position 仍是有效碰撞点，trace.Normal 仍可用。
                    // 安全性：MaxSubSteps 限制子步数，RestTimeoutSeconds 限制总时长，不会死循环。

                    var n = Vector3.Normalize(trace.Normal);
                    // 保存引擎原始法线（ProbeWallNormalZ 会覆盖 n），用于 BounceLog
                    var rawN = n;
                    lastSurfaceNormalZ = n.Z;

                    // 任意触地命中检查（仅 Rest 类型 smoke/decoy，boxMin/boxMax 非 null 时启用）：
                    // 每次触地（法线朝上）后检查碰撞位置是否在框选盒体内，仅用于调试日志/轨迹标记，
                    // 不决定搜索结果：命中判定仍使用最终静止位置，避免"弹一下进框"被误认为命中。
                    // molo/inc 首次触地即终止（下方 GroundOrAirburst 分支），无需此判定；
                    // flash/he 为空中引爆（Fuse 终止条件），不检查触地。
                    if (boxMin.HasValue && boxMax.HasValue && result.BoxHitPoint == null
                        && nade.Term == NadeTermType.Rest && n.Z > SimGroundNormalZ)
                    {
                        var bMin = boxMin.Value;
                        var bMax = boxMax.Value;
                        var bp = trace.Position;
                        if (bp.X >= bMin.X && bp.X <= bMax.X
                            && bp.Y >= bMin.Y && bp.Y <= bMax.Y
                            && bp.Z >= bMin.Z && bp.Z <= bMax.Z)
                        {
                            result.BoxHitPoint = trace.Position;
                        }
                    }

                    // molo/inc：首次触地（法线朝上）即终止取命中点
                    if (nade.Term == NadeTermType.GroundOrAirburst && n.Z > SimGroundNormalZ)
                    {
                        // 沿法线加半径补偿：TraceShape 命中表面，真实投掷物中心在 表面+半径
                        result.LandPoint = trace.Position + n * GrenadeRestRadius;
                        result.TermReason = "ground";
                        result.FlightTime = elapsed;
                        trajectory?.Add(result.LandPoint);
                        return result;
                    }

                    // 碰撞时刻速度修正：当前 v 已施加整个 remainingDt 的重力，但碰撞发生在 fraction*remainingDt 时刻，
                    // 多减了 (1-fraction)*remainingDt 的重力，需要加回得到碰撞时刻真实速度 vAtCollision。
                    // 这样反弹后的 v 才是碰撞时刻真实速度的反射，后续子步继续受重力影响。
                    float overshoot = NadeGravity * (1.0f - trace.Fraction) * remainingDt;
                    var vAtCollision = new Vector3(v.X, v.Y, v.Z + overshoot);

                    // 法线 z 分量恢复：CS2TraceRay 返回的法线 z 分量为 0（水平化），导致弹墙时 z 速度仅做
                    // BounceDamp 衰减，没有因墙面轻微上倾而额外减小，预测轨迹比实际偏高。
                    // 通过在碰撞点附近沿墙面方向偏移采样，根据命中点 z 坐标差推断墙面倾斜，恢复法线 z 分量。
                    string probeNote = string.Empty;
                    if (MathF.Abs(n.Z) < SimGroundNormalZ)
                    {
                        // 墙碰撞次数上限：超过 MaxWallBounces 提前终止，防止 ProbeWallNormalZ
                        // 修改反弹方向后 grenade 卡在墙缝/复杂地形反复撞墙导致单次仿真卡死主线程
                        if (++wallBounceCount > MaxWallBounces)
                        {
                            result.LandPoint = trace.Position;
                            result.TermReason = "max_wall_bounces";
                            result.FlightTime = elapsed;
                            trajectory?.Add(trace.Position);
                            Server.PrintToConsole($"[PracLab] {DateTime.Now:HH:mm:ss} Warning SimulateGrenade exceeded {MaxWallBounces} wall bounces (tick={tick}), terminating early");
                            return result;
                        }
                        n = ProbeWallNormalZ(trace.Position, n, skipPawn, out probeNote);
                        lastSurfaceNormalZ = n.Z;
                    }

                    // 反弹：法向/切向分离衰减，按碰撞类型与速度分段（.nadetest 实测，详见 NadeInfo 类注释）：
                    // 弹墙（任意速度）：D_t = D_n = 0.45（墙面近切向入射衰减轻）；
                    // 高速弹地（|v|>SpeedDampThreshold，首次弹地 |v|≈920）：D_t = D_n = 0.35（实测 0.345/0.347）；
                    // 中低速弹地（|v|≤500，二次弹地 |v|≈225）：D_t = D_n = 0.45（实测 0.450~0.477，旧统一 0.35 过度衰减致落点偏短）。
                    // v' = D_t × v_t - D_n × v_n，其中 v_n = (v·n)n，v_t = v - v_n。
                    float vDotN = Vector3.Dot(vAtCollision, n);
                    var normalComp = n * vDotN;
                    var tangentComp = vAtCollision - normalComp;
                    bool isGround = n.Z > SimGroundNormalZ;
                    bool highSpeedGround = isGround && vAtCollision.Length() > SpeedDampThreshold;
                    float dampT = highSpeedGround ? nade.BounceDampNormalGround : nade.BounceDampTangential;
                    float dampNormal = highSpeedGround ? nade.BounceDampNormalGround : nade.BounceDampNormalWall;
                    v = tangentComp * dampT - normalComp * dampNormal;
                    position = trace.Position + n * BounceEpsilon;

                    // 前 3 次碰撞记录数值到复用缓冲（无字符串分配，命中后格式化）。
                    // 用于定位"预测弹墙方向与真实不符"：若 rawN 与 probeN 方向相反，说明 ProbeWallNormalZ 修正反了。
                    if (bounces != null && bounces.Count < 3)
                    {
                        bounces.Add(new BounceInfo
                        {
                            Pos = trace.Position,
                            RawN = rawN,
                            ProbeN = n,
                            VIn = vAtCollision,
                            VOut = v,
                            Contents = trace.Contents,
                            ProbeNote = probeNote,
                        });
                    }
                    bounceCount++;

                    // 撞地反弹能量不足直接静止（CS2 引擎行为，见 RestSpeedThreshold 注释）：
                    // 反弹后整体速度 |v| 低于 RestSpeedThreshold 时，引擎下一 tick 直接 v=0（不反弹）。
                    // 仅用 |v| 判定：反弹后 |v|<20 的样本（9.1/13.9/16.4）引擎全部判定静止，
                    // |v|>20 的样本（32.8/36.3/≥20.9）全部继续弹跳；同时保护高水平速度触地场景
                    // （前跳投进入框选区域 |v|=213 >> 20），不会错误终止丢失后续弹跳。
                    if (nade.Term == NadeTermType.Rest && n.Z > SimGroundNormalZ
                        && v.LengthSquared() < RestSpeedThreshold * RestSpeedThreshold)
                    {
                        // 沿法线加半径补偿：TraceShape 命中地面表面，真实投掷物中心静止在 表面+半径
                        // 不补偿会导致落点 z 系统性偏低 ~2 单位，可能跌出目标框底（.fdjl 搜索未命中根因）
                        result.LandPoint = trace.Position + n * GrenadeRestRadius;
                        result.TermReason = "rest_bounce";
                        result.FlightTime = elapsed;
                        trajectory?.Add(result.LandPoint);
                        return result;
                    }

                    // 剩余时间按未走完的比例缩减，进入下一子步（下一子步会继续施加重力）
                    remainingDt *= (1.0f - trace.Fraction);
                }
            }

            elapsed += dt;
            tick++;

            // 轨迹采样：每 4 tick 一点
            if (trajectory != null && (tick & 3) == 0)
                trajectory.Add(position);

            // —— 分道具终止判定 ——
            switch (nade.Term)
            {
                case NadeTermType.Fuse:
                    if (elapsed >= FuseSeconds)
                    {
                        result.LandPoint = position;
                        result.TermReason = "fuse";
                        result.FlightTime = elapsed;
                        return result;
                    }
                    break;

                case NadeTermType.Rest:
                    // 低速且贴地（最后接触面法线朝上）持续累计；任一条件破坏即清零
                    if (v.LengthSquared() < RestSpeedThreshold * RestSpeedThreshold && lastSurfaceNormalZ > SimGroundNormalZ)
                    {
                        if (++restTicks >= RestTicksRequired)
                        {
                            result.LandPoint = position;
                            result.TermReason = "rest";
                            result.FlightTime = elapsed;
                            return result;
                        }
                    }
                    else
                    {
                        restTicks = 0;
                    }

                    if (elapsed >= RestTimeoutSeconds)
                    {
                        result.LandPoint = position;
                        result.TermReason = "timeout";
                        result.FlightTime = elapsed;
                        return result;
                    }
                    break;

                case NadeTermType.GroundOrAirburst:
                    if (elapsed >= AirburstTimeoutSeconds)
                    {
                        result.LandPoint = position;
                        result.TermReason = "airburst";
                        result.FlightTime = elapsed;
                        return result;
                    }
                    break;
            }
        }
    }

    /// <summary>
    /// 将碰撞复用缓冲格式化为字符串列表（仅命中时调用，避免热路径字符串分配）。
    /// </summary>
    private static List<string> FormatBounceLog(List<BounceInfo> bounces)
    {
        var log = new List<string>(bounces.Count);
        for (int i = 0; i < bounces.Count; i++)
        {
            var b = bounces[i];
            // 若 ProbeWallNormalZ 降级，追加 note 说明原因
            var note = string.IsNullOrEmpty(b.ProbeNote) ? "" : $" NOTE={b.ProbeNote}";
            // contents 十六进制：诊断误撞 PlayerClip(0x40)/CsgoGrenadeClip 等
            // Solid=0x1 Window=0x2 PassBullets=0x20000 PlayerClip=0x40 CsgoGrenadeClip=0x400000
            log.Add(
                $"bounce[{i}] pos=({b.Pos.X:F1},{b.Pos.Y:F1},{b.Pos.Z:F1}) " +
                $"rawN=({b.RawN.X:F2},{b.RawN.Y:F2},{b.RawN.Z:F2}) " +
                $"probeN=({b.ProbeN.X:F2},{b.ProbeN.Y:F2},{b.ProbeN.Z:F2}) " +
                $"vIn=({b.VIn.X:F1},{b.VIn.Y:F1},{b.VIn.Z:F1}) " +
                $"vOut=({b.VOut.X:F1},{b.VOut.Y:F1},{b.VOut.Z:F1}) " +
                $"contents=0x{b.Contents:X}{note}");
        }
        return log;
    }

    /// <summary>
    /// 恢复墙面碰撞法线的 z 分量。
    /// CS2TraceRay 对墙面碰撞返回的法线 z 分量为 0（水平化），导致弹墙时 z 速度仅做 BounceDamp 衰减，
    /// 没有因墙面轻微下倾而额外减小，预测轨迹比实际偏高、落点偏远。
    /// 多点探测法：从碰撞点上下多个高度（沿水平法线偏移到墙外）各做一次 TraceShape，
    /// 命中距离 d 与高度 h 线性关系：d ≈ WallOffset + n.Z * h（n.Z 小时近似），
    /// 用最小二乘线性回归求斜率即得 n.Z。墙向下倾（n.Z&lt;0）时上方探测点命中距离更短。
    /// 多点（6 点 Z=±5/±10/±15）相比原两点（Z=±5）的优势：
    ///   - 碰撞点附近墙面垂直时（d1=d2）仍可通过更远点分辨倾斜（SMK-N-L-01 案例：
    ///     Z=183~193 范围墙面垂直，原两点法 normalZ=0，但真实 n.Z≈-0.04，需扩大范围）。
    ///   - 部分探测点超出墙顶/地面 miss 时，仍可用其余有效点回归，避免整体降级。
    /// .nadetest 实测（前跳投弹墙）：墙面法线 z ≈ -0.018（轻微下倾），
    /// 不恢复时反弹 v.Z 预测 -4.2 vs 实际 -12.8，弹墙后整段轨迹系统性偏高。
    /// 注意：必须从墙外朝墙探测——墙下倾时碰撞点正上方已在墙内，从墙内探测行为不可靠。
    /// </summary>
    /// <param name="hitPos">原始碰撞点。</param>
    /// <param name="flatNormal">水平化的法线（z=0，已归一化）。</param>
    /// <param name="skipPawn">trace 跳过的实体句柄。</param>
    /// <returns>恢复 z 分量后的法线（已归一化）；探测失败时返回原始水平化法线。</returns>
    private static Vector3 ProbeWallNormalZ(Vector3 hitPos, Vector3 flatNormal, nint skipPawn, out string note)
    {
        note = string.Empty;
        // 墙外偏移：大于 BounceEpsilon(0.125)，确保探测起点在墙外不卡墙
        const float WallOffset = 2.0f;
        // 朝墙探测距离：大于 WallOffset + 倾斜余量即可
        const float ProbeDist = 8.0f;
        // ProbeHeights 见类级别字段定义

        var outward = flatNormal * WallOffset;
        var dir = -flatNormal;

        // 收集有效探测点 (h, d)：过滤 miss/allsolid/法线方向不一致的点。
        // 几何关系：探测点 q = hitPos + outward + (0,0,h)，从 q 沿 dir 探测命中距离 d；
        // 墙面过 hitPos、法线 n=(nx,ny,nz) 时 d ≈ WallOffset + n.Z * h（n.Z 小时近似），
        // 故 d 与 h 线性：d = a + b*h，斜率 b = n.Z。
        // 用 stackalloc 避免热路径堆分配（受 MaxWallBounces 限制，调用次数有界）
        Span<(float h, float d)> samples = stackalloc (float, float)[ProbeHeights.Length];
        int validCount = 0;
        float hSum = 0, dSum = 0;

        for (int i = 0; i < ProbeHeights.Length; i++)
        {
            float h = ProbeHeights[i];
            var q = hitPos + outward + new Vector3(0, 0, h);
            CGameTrace t;
            try
            {
                t = TraceRay.TraceShape(ToCssVector(q), ToCssVector(q + dir * ProbeDist), GrenadeTraceMask, (ulong)Contents.NoDraw, skipPawn);
            }
            catch
            {
                // 单点异常跳过（上层 TraceShape 异常处理覆盖日志）
                continue;
            }

            // 过滤：未命中/卡固体
            if (t.Fraction >= 1.0f || t.AllSolid)
                continue;

            // 过滤：命中法线水平方向与原始法线不一致（命中地面/天花/其他物体）
            var nrm = Vector3.Normalize(t.Normal);
            if (MathF.Abs(nrm.X - flatNormal.X) > 0.2f || MathF.Abs(nrm.Y - flatNormal.Y) > 0.2f)
                continue;

            float d = t.Fraction * ProbeDist;
            samples[validCount] = (h, d);
            hSum += h;
            dSum += d;
            validCount++;
        }

        if (validCount < 2)
        {
            // 有效点不足：无法推断倾斜，返回水平法线（墙脚碰撞时下方点可能命中地面被过滤，合理降级）
            note = $"multi-valid<{validCount}";
            return flatNormal;
        }

        // 最小二乘线性回归：d = a + b*h，b = n.Z
        float hMean = hSum / validCount;
        float dMean = dSum / validCount;
        float num = 0, den = 0;
        for (int i = 0; i < validCount; i++)
        {
            float dh = samples[i].h - hMean;
            num += dh * (samples[i].d - dMean);
            den += dh * dh;
        }

        if (MathF.Abs(den) < 1e-6f)
        {
            // 退化：所有 h 相同（不应发生，ProbeHeights 互异）
            note = "multi-degenerate";
            return flatNormal;
        }

        float normalZ = Math.Clamp(num / den, -0.5f, 0.5f);

        // 记录有效点数与回归结果（成功路径），由 .ng 输出
        note = $"multi pts={validCount} b={normalZ:F4}";

        return Vector3.Normalize(new Vector3(flatNormal.X, flatNormal.Y, normalZ));
    }

    /// <summary>
    /// pitch 符号验证探针：以给定 pitch/yaw 计算出手速度，打印无碰撞解析位置（0.5s / 1.0s）到控制台。
    /// 开工时对比游戏内真实远投方向，验证 QAngle pitch +down/-up 约定（负值=抬头）。
    /// pitch 为负（抬头）时 pos@0.5s 的 z 必须高于眼位，否则符号约定反了。
    /// </summary>
    /// <param name="eyePos">玩家眼位。</param>
    /// <param name="pitchDeg">瞄准 pitch（度，Source 约定）。</param>
    /// <param name="yawDeg">瞄准 yaw（度）。</param>
    private void LogPitchSignProbe(Vector3 eyePos, float pitchDeg, float yawDeg)
    {
        var v0 = ComputeThrowVelocity(pitchDeg, yawDeg, ThrowStrength.Left, ThrowMode.Normal);
        var p05 = eyePos + v0 * 0.5f;
        p05.Z -= NadeGravity * 0.25f * 0.5f;
        var p10 = eyePos + v0 * 1.0f;
        p10.Z -= NadeGravity * 1.0f * 0.5f;
        Server.PrintToConsole($"[PracLab] {DateTime.Now:HH:mm:ss} PitchProbe eye=({eyePos.X:F1},{eyePos.Y:F1},{eyePos.Z:F1}) pitch={pitchDeg:F1} yaw={yawDeg:F1} v0=({v0.X:F1},{v0.Y:F1},{v0.Z:F1}) pos@0.5s=({p05.X:F1},{p05.Y:F1},{p05.Z:F1}) pos@1.0s=({p10.X:F1},{p10.Y:F1},{p10.Z:F1})");
    }

    /// <summary>
    /// CSS Vector → System.Numerics.Vector3 转换辅助。
    /// </summary>
    private static Vector3 ToNumerics(CounterStrikeSharp.API.Modules.Utils.Vector v) => new(v.X, v.Y, v.Z);

    // ==================== SearchJob 调度与网格搜索 ====================

    /// <summary>细化阶段对主扫描命中的 Top-K 邻域再扫一轮。</summary>
    private const int RefineTopK = 5;

    /// <summary>组数上限：6 投掷方式（含 duck 系列）× 3 力度。</summary>
    private const int MaxSearchGroups = 18;

    /// <summary>进度提示刷新间隔（秒），PrintToCenter 单行覆盖刷新。</summary>
    private const double SearchProgressSeconds = 0.5;

    /// <summary>
    /// 搜索结果条目（探测结果，供 .nl/.ng 使用）。
    /// </summary>
    private sealed class NadeResult
    {
        /// <summary>结果 ID：道具缩写-方式-力度-序号（如 SMK-N-L-01），Job 完成时统一编号。</summary>
        public string Id = string.Empty;

        /// <summary>道具。</summary>
        public NadeInfo Nade = null!;

        /// <summary>投掷方式。</summary>
        public ThrowMode Mode;

        /// <summary>投掷力度。</summary>
        public ThrowStrength Strength;

        /// <summary>描点 yaw（度）。</summary>
        public float Yaw;

        /// <summary>描点 pitch（度，Source 约定）。</summary>
        public float Pitch;

        /// <summary>站位快照（出手眼位）。</summary>
        public Vector3 EyePos;

        /// <summary>落点（按道具定义）。</summary>
        public Vector3 LandPoint;

        /// <summary>弹道轨迹采样点（.ng 预览用）。</summary>
        public List<Vector3> Trajectory = new();

        /// <summary>搜索目标盒体最小角（.ng 终端调试输出用）。</summary>
        public Vector3 BoxMin;

        /// <summary>搜索目标盒体最大角（.ng 终端调试输出用）。</summary>
        public Vector3 BoxMax;

        /// <summary>仿真终止原因（.ng 终端调试输出用，定位"提前终止"问题）。</summary>
        public string TermReason = string.Empty;

        /// <summary>仿真飞行时长（秒，.ng 终端调试输出用）。</summary>
        public float FlightTime;

        /// <summary>出手点（投掷物真实生成位置，.ng 终端调试输出用，对比预测/真实起点偏移）。</summary>
        public Vector3 ThrowOrigin;

        /// <summary>前 3 次碰撞详情（调试插桩，定位"弹墙方向与真实不符"问题）。</summary>
        public List<string> BounceLog = new();

        /// <summary>.ng 重建的可视化实体（描点十字 + 弹道 beam）。</summary>
        public List<CEntityInstance> VisualEntities = new();
    }

    /// <summary>
    /// 单个搜索候选角度。主扫描候选 RefineParent 为 null；细化候选非 null（命中后与父解比较替换，不进 Found）。
    /// </summary>
    private struct SearchCandidate
    {
        /// <summary>所属组索引（GroupIndexOf(mode, strength)），早停用。</summary>
        public int GroupIndex;

        /// <summary>投掷方式。</summary>
        public ThrowMode Mode;

        /// <summary>投掷力度。</summary>
        public ThrowStrength Strength;

        /// <summary>描点 yaw（度，已规范化）。</summary>
        public float Yaw;

        /// <summary>描点 pitch（度，Source 约定）。</summary>
        public float Pitch;

        /// <summary>细化候选的父解（主扫描命中）；null 表示主扫描候选。</summary>
        public NadeResult? RefineParent;
    }

    /// <summary>
    /// 进行中的搜索任务（每玩家至多一个）。
    /// 候选按组连续存储，游标顺序推进；主扫描完成后追加细化候选（RefineParent 非 null）。
    /// </summary>
    private sealed class SearchJob
    {
        /// <summary>所属玩家 SteamID。</summary>
        public ulong SteamId;

        /// <summary>目标区域（立方体）。</summary>
        public TargetRegion Target = null!;

        /// <summary>道具。</summary>
        public NadeInfo Nade = null!;

        /// <summary>精度档位。</summary>
        public NadeAccuracy Accuracy;

        /// <summary>出手点（眼位快照，搜索期间玩家移动不影响结果）。</summary>
        public Vector3 EyePos;

        /// <summary>弹道 trace 需跳过的搜索者自身 pawn 句柄（构建 Job 时快照；Zero 表示不可用）。</summary>
        public nint SkipPawnHandle;

        /// <summary>粗筛 + 排序后的候选列表（主扫描候选在前，细化候选追加在后）。</summary>
        public List<SearchCandidate> Candidates = new();

        /// <summary>各组主扫描候选在 Candidates 中的末尾索引（早停跳组用；未搜索组为 0 但不会被触碰）。</summary>
        public int[] GroupEnd = new int[MaxSearchGroups];

        /// <summary>各组已入库代表解数量（早停判定）。</summary>
        public int[] GroupHits = new int[MaxSearchGroups];

        /// <summary>当前候选游标。</summary>
        public int Cursor;

        /// <summary>粗筛前候选总数（提示与日志）。</summary>
        public int PreFilterCount;

        /// <summary>主扫描候选数（细化追加前快照）。</summary>
        public int MainCandidateCount;

        /// <summary>主扫描步长（度，精度档位）。</summary>
        public float BaseStepDeg;

        /// <summary>细化步长（度；Low 档为 0 不细化）。</summary>
        public float RefineStepDeg;

        /// <summary>细化候选是否已追加。</summary>
        public bool RefineQueued;

        /// <summary>已入库的代表解列表。</summary>
        public List<NadeResult> Found = new();

        /// <summary>上次进度提示时间。</summary>
        public DateTime LastProgressPrint = DateTime.MinValue;

        /// <summary>仿真轨迹复用缓冲（命中时复制到 NadeResult，避免每候选分配）。</summary>
        public List<Vector3> ScratchTrajectory = new(256);

        /// <summary>碰撞复用缓冲（仿真前 Clear，前 3 次碰撞记录数值，命中后格式化）。</summary>
        public List<BounceInfo> ScratchBounces = new(4);
    }

    /// <summary>
    /// 每玩家进行中的搜索任务。
    /// </summary>
    private readonly Dictionary<ulong, SearchJob> _searchJobs = new();

    /// <summary>
    /// 每玩家最近一次搜索完成的结果列表（.nl/.ng 数据源；新搜索完成时整体替换）。
    /// </summary>
    private readonly Dictionary<ulong, List<NadeResult>> _nadeResults = new();

    /// <summary>
    /// 搜索 OnTick 监听器是否已注册（守卫式单次注册）。
    /// </summary>
    private bool _isSearchTickRegistered;

    /// <summary>
    /// 精度档位 → (主扫描步长, 细化步长)。low=4° 无细化；mid=2° 细化 1°；high=1° 细化 0.25°。
    /// </summary>
    private static (float BaseStep, float RefineStep) GetAccuracySteps(NadeAccuracy accuracy) => accuracy switch
    {
        NadeAccuracy.Low => (4.0f, 0.0f),
        NadeAccuracy.High => (1.0f, 0.25f),
        _ => (2.0f, 1.0f),
    };

    /// <summary>
    /// 组索引：mode × 3 + strength（0~17）。
    /// </summary>
    private static int GroupIndexOf(ThrowMode mode, ThrowStrength strength) => (int)mode * 3 + (int)strength;

    /// <summary>投掷方式 ID 缩写（N/J/RJ/D/DJ/DRJ）。</summary>
    private static string ModeAbbrev(ThrowMode mode) => mode switch
    {
        ThrowMode.Normal => "N",
        ThrowMode.Jump => "J",
        ThrowMode.RunJump => "RJ",
        ThrowMode.Duck => "D",
        ThrowMode.DuckJump => "DJ",
        _ => "DRJ",
    };

    /// <summary>投掷力度 ID 缩写（L/M/R）。</summary>
    private static string StrengthAbbrev(ThrowStrength strength) => strength switch
    {
        ThrowStrength.Left => "L",
        ThrowStrength.Mid => "M",
        _ => "R",
    };

    /// <summary>
    /// 构建搜索任务：枚举组（方式×力度，按命令过滤）→ yaw 扇形 × pitch 网格 → 粗筛 → 组内排序。
    /// 候选生成同步执行（命令触发一次性开销，粗筛为纯数学运算）；返回 null 表示玩家 pawn 无效。
    /// </summary>
    /// <param name="player">发起搜索的玩家。</param>
    /// <param name="target">目标区域。</param>
    /// <param name="nade">道具。</param>
    /// <param name="accuracy">精度档位。</param>
    /// <param name="modeFilter">投掷方式过滤（null = 全部 3 种）。</param>
    /// <param name="strengthFilter">力度过滤（null = 全部 3 档）。</param>
    /// <returns>构建完成的 SearchJob（候选可能为空，调用方判定提示）。</returns>
    private SearchJob? BuildSearchJob(CCSPlayerController player, TargetRegion target, NadeInfo nade, NadeAccuracy accuracy, ThrowMode? modeFilter, ThrowStrength? strengthFilter)
    {
        var eye = player.GetEyePosition();
        if (eye == null)
        {
            Server.PrintToConsole($"[PracLab] {DateTime.Now:HH:mm:ss} Warning BuildSearchJob eye position unavailable for {player.PlayerName}");
            return null;
        }

        var eyePos = ToNumerics(eye);
        var boxMin = ToNumerics(target.Min);
        var boxMax = ToNumerics(target.Max);
        var boxCenter = (boxMin + boxMax) * 0.5f;

        var (baseStep, refineStep) = GetAccuracySteps(accuracy);

        var job = new SearchJob
        {
            SteamId = player.SteamID,
            Target = target,
            Nade = nade,
            Accuracy = accuracy,
            EyePos = eyePos,
            BaseStepDeg = baseStep,
            RefineStepDeg = refineStep,
            // 弹道 trace 跳过搜索者自身 pawn（与 NadeDraw 取点一致的 skip 语义）
            SkipPawnHandle = player.PlayerPawn.Value?.Handle ?? nint.Zero,
        };

        // yaw 扇形（站位在盒体 2D 范围内全扫 360°）
        var (yawMin, yawMax, fullCircle) = ComputeYawSector(eyePos, boxMin, boxMax, _config.NadeYawMargin);
        if (fullCircle)
        {
            yawMin = -180.0f;
            yawMax = 180.0f;
        }

        // 候选排序参考：站位看向盒体中心的 yaw、解析低弧 pitch
        float lookYaw = MathF.Atan2(boxCenter.Y - eyePos.Y, boxCenter.X - eyePos.X) * 180.0f / MathF.PI;
        float horizDist = MathF.Sqrt((boxCenter.X - eyePos.X) * (boxCenter.X - eyePos.X) + (boxCenter.Y - eyePos.Y) * (boxCenter.Y - eyePos.Y));
        float arcMargin = _config.NadeArcMargin;

        ThrowMode[] modes = modeFilter.HasValue ? [modeFilter.Value] : [ThrowMode.Normal, ThrowMode.Jump, ThrowMode.RunJump, ThrowMode.Duck, ThrowMode.DuckJump, ThrowMode.DuckRunJump];
        ThrowStrength[] strengths = strengthFilter.HasValue ? [strengthFilter.Value] : [ThrowStrength.Left, ThrowStrength.Mid, ThrowStrength.Right];

        var groupCandidates = new List<SearchCandidate>();

        foreach (var mode in modes)
        {
            // duck 系列自站立眼位下沉、跳投系再抬升（按压时刻眼位，实测见 JumpReleaseLiftZ）
            var releaseEye = ReleaseEyeForMode(eyePos, mode);
            float modeHeightDiff = boxCenter.Z - releaseEye.Z;

            foreach (var strength in strengths)
            {
                int g = GroupIndexOf(mode, strength);
                groupCandidates.Clear();

                // 该组出手速率（不依赖 pitch/mode 增量）→ 解析参考 pitch
                float speed = (StrengthValue(strength) * 0.7f + 0.3f) * NadeBaseSpeed;
                float refPitch = SolveBallisticPitch(speed, horizDist, modeHeightDiff);

                for (float yaw = yawMin; yaw <= yawMax + 1e-3f; yaw += baseStep)
                {
                    float normYaw = NormalizeAngleDeg(yaw);
                    for (float pitch = PitchScanMin; pitch <= PitchScanMax + 1e-3f; pitch += baseStep)
                    {
                        job.PreFilterCount++;

                        // 粗筛：整条无碰撞抛物线弧（从出手点起步，非眼位）到盒体的最小距离，弧从盒体旁路过即保留
                        var v0 = ComputeThrowVelocity(pitch, normYaw, strength, mode);
                        var throwOrigin = ComputeThrowOrigin(releaseEye, pitch, normYaw, strength);
                        if (ArcMinDistanceToBox(throwOrigin, v0, boxMin, boxMax) > arcMargin)
                            continue;

                        groupCandidates.Add(new SearchCandidate
                        {
                            GroupIndex = g,
                            Mode = mode,
                            Strength = strength,
                            Yaw = normYaw,
                            Pitch = pitch,
                        });
                    }
                }

                // 组内排序：yaw 朝盒体中心优先、pitch 接近解析解优先
                float Score(SearchCandidate c) => MathF.Abs(NormalizeAngleDeg(c.Yaw - lookYaw)) + MathF.Abs(c.Pitch - refPitch);
                groupCandidates.Sort((a, b) => Score(a).CompareTo(Score(b)));

                job.Candidates.AddRange(groupCandidates);
                job.GroupEnd[g] = job.Candidates.Count;
            }
        }

        job.MainCandidateCount = job.Candidates.Count;

        return job;
    }

    /// <summary>
    /// 启动搜索任务：新搜索自动取消本人旧 Job（控制台英文日志），注册 OnTick 摊销驱动。
    /// 候选为空（粗筛全灭）时聊天提示且不创建 Job。
    /// </summary>
    /// <param name="player">发起搜索的玩家。</param>
    /// <param name="target">目标区域。</param>
    /// <param name="nade">道具。</param>
    /// <param name="accuracy">精度档位。</param>
    /// <param name="modeFilter">投掷方式过滤（null = 全部）。</param>
    /// <param name="strengthFilter">力度过滤（null = 全部）。</param>
    private void StartSearchJob(CCSPlayerController player, TargetRegion target, NadeInfo nade, NadeAccuracy accuracy, ThrowMode? modeFilter, ThrowStrength? strengthFilter)
    {
        Server.PrintToConsole("[PracLab] StartSearchJob: executing...");

        var steamId = player.SteamID;

        // 重复发起搜索：旧 Job 被取消，新 Job 取代之
        if (_searchJobs.Remove(steamId, out var oldJob))
        {
            Server.PrintToConsole($"[PracLab] {DateTime.Now:HH:mm:ss} Search job replaced: player={player.PlayerName} oldNade={oldJob.Nade.Key} oldAcc={oldJob.Accuracy} oldCursor={oldJob.Cursor}/{oldJob.Candidates.Count}");
        }

        var job = BuildSearchJob(player, target, nade, accuracy, modeFilter, strengthFilter);
        if (job == null)
            return;

        if (job.Candidates.Count == 0)
        {
            player.PrintToChat(Localizer.ForPlayer(player, "nadesearch.no_candidates"));
            Server.PrintToConsole($"[PracLab] {DateTime.Now:HH:mm:ss} Search job empty after coarse filter: player={player.PlayerName} pre={job.PreFilterCount}");
            return;
        }

        _searchJobs[steamId] = job;
        RegisterSearchTickListener();

        // 启动提示：道具 / 精度 / 组合数（粗筛后/前）
        player.PrintToChat(Localizer.ForPlayer(player, "nadesearch.started", GetNadeDisplayName(player, nade), accuracy.ToString().ToLowerInvariant(), job.MainCandidateCount, job.PreFilterCount));

        Server.PrintToConsole($"[PracLab] {DateTime.Now:HH:mm:ss} Search job started: player={player.PlayerName} nade={nade.Key} acc={accuracy} candidates={job.PreFilterCount}->{job.MainCandidateCount}");
    }

    /// <summary>
    /// 取消指定玩家的进行中搜索任务（换图 / 回合结束 / 断连 / 异常时调用）。
    /// </summary>
    /// <param name="steamId">玩家 SteamID。</param>
    /// <param name="reason">取消原因（控制台英文日志）。</param>
    /// <param name="notifyPlayer">是否聊天通知玩家（换图批量清理时 false）。</param>
    /// <returns>true 表示存在进行中 Job 且已取消。</returns>
    private bool CancelSearchJob(ulong steamId, string reason, bool notifyPlayer = true)
    {
        if (!_searchJobs.Remove(steamId, out var job))
            return false;

        Server.PrintToConsole($"[PracLab] {DateTime.Now:HH:mm:ss} Search job cancelled: steamId={steamId} reason={reason} cursor={job.Cursor}/{job.Candidates.Count}");

        if (notifyPlayer)
        {
            var player = FindPlayerBySteamId(steamId);
            if (player != null && player.IsValid)
                player.PrintToChat(Localizer.ForPlayer(player, "nadesearch.cancelled"));
        }

        return true;
    }

    /// <summary>
    /// 清空全部玩家的搜索状态：进行中 Job、结果列表与可视化实体（不通知玩家）。供 OnMapStart 调用。
    /// </summary>
    private void ClearAllSearchState()
    {
        Server.PrintToConsole("[PracLab] ClearAllSearchState: executing...");

        int jobCount = _searchJobs.Count;
        _searchJobs.Clear();

        int resultCount = 0;
        foreach (var results in _nadeResults.Values)
        {
            foreach (var r in results)
                ClearEntityList(r.VisualEntities);
            resultCount += results.Count;
        }
        _nadeResults.Clear();

        Server.PrintToConsole($"[PracLab] {DateTime.Now:HH:mm:ss} NadeSearch all search state cleared ({jobCount} jobs, {resultCount} results)");
    }

    /// <summary>
    /// 取消全部进行中的搜索 Job（保留已完成结果，不通知玩家）。供 OnRoundEnd 调用。
    /// </summary>
    /// <param name="reason">取消原因（控制台英文日志）。</param>
    private void CancelAllSearchJobs(string reason)
    {
        if (_searchJobs.Count == 0) return;

        int jobCount = _searchJobs.Count;
        _searchJobs.Clear();

        Server.PrintToConsole($"[PracLab] {DateTime.Now:HH:mm:ss} NadeSearch all search jobs cancelled ({jobCount} jobs, reason={reason})");
    }

    /// <summary>
    /// 注册搜索 OnTick 监听器（仅注册一次，无 Job 短路返回）。
    /// 毫秒预算全局共享：每 tick 全部 Job 合计耗时不超过 praclab_nade_trace_ms（Stopwatch 卡预算）。
    /// </summary>
    private void RegisterSearchTickListener()
    {
        if (_isSearchTickRegistered) return;

        RegisterListener<Listeners.OnTick>(() =>
        {
            // 短路：无进行中 Job 时直接返回
            if (_searchJobs.Count == 0) return;

            double budgetMs = _config.NadeTraceMs;
            var sw = Stopwatch.StartNew();
            List<ulong>? finishedJobs = null;

            foreach (var (steamId, job) in _searchJobs)
            {
                // 全局预算耗尽：剩余 Job 下一 tick 继续
                if (sw.Elapsed.TotalMilliseconds >= budgetMs)
                    break;

                try
                {
                    if (AdvanceSearchJob(steamId, job, sw, budgetMs))
                        (finishedJobs ??= new List<ulong>()).Add(steamId);
                }
                catch (Exception ex)
                {
                    // 单 Job 异常不波及其他 Job：取消并提示，禁止静默失败
                    Server.PrintToConsole($"[PracLab] {DateTime.Now:HH:mm:ss} Error Search job failed - {ex.Message}");
                    var player = FindPlayerBySteamId(steamId);
                    if (player != null && player.IsValid)
                        player.PrintToChat(Localizer.ForPlayer(player, "nadesearch.cancelled"));
                    (finishedJobs ??= new List<ulong>()).Add(steamId);
                }
            }

            if (finishedJobs != null)
            {
                foreach (var steamId in finishedJobs)
                    _searchJobs.Remove(steamId);
            }
        });

        _isSearchTickRegistered = true;
        Server.PrintToConsole($"[PracLab] {DateTime.Now:HH:mm:ss} NadeSearch OnTick listener registered");
    }

    /// <summary>
    /// 在毫秒预算内推进单个搜索任务：游标顺序扫描候选 → 组早停跳组 → 仿真 → 命中去重入库 → 细化追加 → 进度提示。
    /// </summary>
    /// <param name="steamId">所属玩家 SteamID。</param>
    /// <param name="job">搜索任务。</param>
    /// <param name="budget">全局预算计时器（全部 Job 共享）。</param>
    /// <param name="budgetMs">毫秒预算上限。</param>
    /// <returns>true 表示 Job 结束（完成 / 玩家失效取消），调用方应从 _searchJobs 移除。</returns>
    private bool AdvanceSearchJob(ulong steamId, SearchJob job, Stopwatch budget, double budgetMs)
    {
        var player = FindPlayerBySteamId(steamId);
        if (player == null || !player.IsValid)
        {
            // 玩家断连：静默取消（玩家已看不到提示）
            Server.PrintToConsole($"[PracLab] {DateTime.Now:HH:mm:ss} Search job cancelled (player gone) steamId={steamId}");
            return true;
        }

        int maxHits = _config.NadeGroupMaxHits;
        int simHz = _config.NadeSimHz;
        var boxMin = ToNumerics(job.Target.Min);
        var boxMax = ToNumerics(job.Target.Max);
        var boxCenter = (boxMin + boxMax) * 0.5f;
        int mainCount = job.MainCandidateCount;
        float dedupeDeg = job.BaseStepDeg * 1.5f;

        while (job.Cursor < job.Candidates.Count)
        {
            // 毫秒预算耗尽：让出本 tick，下一 tick 从游标继续
            if (budget.Elapsed.TotalMilliseconds >= budgetMs)
                return false;

            var c = job.Candidates[job.Cursor];

            // 早停：主扫描候选且组已凑够代表解 → 跳过该组剩余角度
            if (c.RefineParent == null && job.GroupHits[c.GroupIndex] >= maxHits)
            {
                job.Cursor = job.GroupEnd[c.GroupIndex];
                continue;
            }

            // 弹道仿真（轨迹采样到复用缓冲，命中才复制；duck 系列自站立眼位下沉）
            // 起点为出手点（投掷物真实生成位置，跳投系按按压眼位抬升 JumpReleaseLiftZ）：
            // 从眼位起步会让落点系统性偏远 ~20+ 单位；注意 candEye 保持站姿眼位供 .ng 传送使用
            // 传递 boxMin/boxMax 启用"任意触地即命中"判定（Rest 类型记录 BoxHitPoint 但不提前终止，保证轨迹完整）
            job.ScratchTrajectory.Clear();
            job.ScratchBounces.Clear();
            var v0 = ComputeThrowVelocity(c.Pitch, c.Yaw, c.Strength, c.Mode);
            var candEye = EyeForMode(job.EyePos, c.Mode);
            var throwOrigin = ComputeThrowOrigin(ReleaseEyeForMode(job.EyePos, c.Mode), c.Pitch, c.Yaw, c.Strength);
            var sim = SimulateGrenade(throwOrigin, v0, job.Nade, simHz, job.ScratchTrajectory, job.SkipPawnHandle, boxMin, boxMax, job.ScratchBounces);
            job.Cursor++;

            // 命中判定：最终静止位置必须在框选盒体内。
            // 之前使用"任意触地即命中"导致"弹一下进框但后续弹出"被误认为命中，与用户期望不符。
            // 仿真过程中仍记录 BoxHitPoint（首次触地命中点）供日志/调试，但不参与命中判定。
            var p = sim.LandPoint;
            bool hit = p.X >= boxMin.X && p.X <= boxMax.X
                    && p.Y >= boxMin.Y && p.Y <= boxMax.Y
                    && p.Z >= boxMin.Z && p.Z <= boxMax.Z;

            if (hit)
            {
                if (c.RefineParent is { } parent)
                {
                    // 细化命中：落点距盒体中心更近则替换父解的角度/落点/轨迹（不进 Found）
                    if (Vector3.Distance(sim.LandPoint, boxCenter) < Vector3.Distance(parent.LandPoint, boxCenter))
                    {
                        parent.Yaw = c.Yaw;
                        parent.Pitch = c.Pitch;
                        parent.LandPoint = sim.LandPoint;
                        parent.Trajectory = new List<Vector3>(job.ScratchTrajectory);
                        // 同步出手点与前 3 次碰撞日志
                        parent.ThrowOrigin = throwOrigin;
                        parent.BounceLog = FormatBounceLog(job.ScratchBounces);
                    }
                }
                else
                {
                    // 同组去重：与已有代表解角度差 ≤ dedupe 视为同一点位，保留距中心更近者
                    NadeResult? dup = null;
                    foreach (var r in job.Found)
                    {
                        if (GroupIndexOf(r.Mode, r.Strength) != c.GroupIndex) continue;
                        if (MathF.Abs(NormalizeAngleDeg(r.Yaw - c.Yaw)) <= dedupeDeg && MathF.Abs(r.Pitch - c.Pitch) <= dedupeDeg)
                        {
                            dup = r;
                            break;
                        }
                    }

                    if (dup != null)
                    {
                        if (Vector3.Distance(sim.LandPoint, boxCenter) < Vector3.Distance(dup.LandPoint, boxCenter))
                        {
                            dup.Yaw = c.Yaw;
                            dup.Pitch = c.Pitch;
                            dup.LandPoint = sim.LandPoint;
                            dup.Trajectory = new List<Vector3>(job.ScratchTrajectory);
                            // 同步出手点与前 3 次碰撞日志
                            dup.ThrowOrigin = throwOrigin;
                            dup.BounceLog = FormatBounceLog(job.ScratchBounces);
                        }
                    }
                    else
                    {
                        job.Found.Add(new NadeResult
                        {
                            Nade = job.Nade,
                            Mode = c.Mode,
                            Strength = c.Strength,
                            Yaw = c.Yaw,
                            Pitch = c.Pitch,
                            EyePos = candEye,
                            LandPoint = sim.LandPoint,
                            Trajectory = new List<Vector3>(job.ScratchTrajectory),
                            BoxMin = boxMin,
                            BoxMax = boxMax,
                            TermReason = sim.TermReason,
                            FlightTime = sim.FlightTime,
                            // 出手点与前 3 次碰撞日志
                            ThrowOrigin = throwOrigin,
                            BounceLog = FormatBounceLog(job.ScratchBounces),
                        });
                        job.GroupHits[c.GroupIndex]++;
                    }
                }
            }

            // 主扫描结束：追加细化候选（mid/high 且有命中时）
            if (!job.RefineQueued && job.Cursor >= mainCount)
            {
                AppendRefinementCandidates(job, boxCenter);
                job.RefineQueued = true;
            }

            // 0.5 秒单行进度刷新（中心提示覆盖）
            var now = DateTime.Now;
            if ((now - job.LastProgressPrint).TotalSeconds >= SearchProgressSeconds)
            {
                job.LastProgressPrint = now;
                int percent = job.Cursor * 100 / job.Candidates.Count;
                player.PrintToCenter(Localizer.ForPlayer(player, "nadesearch.progress", percent, job.Found.Count));
            }
        }

        FinishSearchJob(job, player, boxCenter);
        return true;
    }

    /// <summary>
    /// 追加细化候选：对主扫描命中中距盒体中心最近的 Top-K，在其 ±BaseStep 邻域以细化步长再扫一轮。
    /// 细化命中与父解比较替换（不新增 Found），目的是提升已有点位的角度精度。
    /// </summary>
    /// <param name="job">搜索任务（主扫描已完成）。</param>
    /// <param name="boxCenter">目标盒体中心。</param>
    private void AppendRefinementCandidates(SearchJob job, Vector3 boxCenter)
    {
        if (job.Accuracy == NadeAccuracy.Low || job.Found.Count == 0)
            return;

        var top = job.Found
            .OrderBy(r => Vector3.DistanceSquared(r.LandPoint, boxCenter))
            .Take(RefineTopK)
            .ToList();

        float half = job.BaseStepDeg;
        float step = job.RefineStepDeg;

        foreach (var parent in top)
        {
            for (float dy = -half; dy <= half + 1e-4f; dy += step)
            {
                float ny = NormalizeAngleDeg(parent.Yaw + dy);
                for (float dp = -half; dp <= half + 1e-4f; dp += step)
                {
                    job.Candidates.Add(new SearchCandidate
                    {
                        GroupIndex = GroupIndexOf(parent.Mode, parent.Strength),
                        Mode = parent.Mode,
                        Strength = parent.Strength,
                        Yaw = ny,
                        Pitch = Math.Clamp(parent.Pitch + dp, PitchScanMin, PitchScanMax),
                        RefineParent = parent,
                    });
                }
            }
        }

        Server.PrintToConsole($"[PracLab] {DateTime.Now:HH:mm:ss} Search refinement queued: steamId={job.SteamId} topK={top.Count} added={job.Candidates.Count - job.MainCandidateCount}");
    }

    /// <summary>
    /// 完成搜索任务：按组排序编号（道具缩写-方式-力度-序号），结果整体替换入库，聊天输出完成提示。
    /// </summary>
    /// <param name="job">已完成的搜索任务。</param>
    /// <param name="player">所属玩家（已确认有效）。</param>
    /// <param name="boxCenter">目标盒体中心（组内排序按距中心距离）。</param>
    private void FinishSearchJob(SearchJob job, CCSPlayerController player, Vector3 boxCenter)
    {
        // 组索引升序、组内距盒体中心最近优先 → 统一编号
        var ordered = job.Found
            .OrderBy(r => GroupIndexOf(r.Mode, r.Strength))
            .ThenBy(r => Vector3.DistanceSquared(r.LandPoint, boxCenter))
            .ToList();

        var counters = new Dictionary<int, int>();
        foreach (var r in ordered)
        {
            int g = GroupIndexOf(r.Mode, r.Strength);
            counters.TryGetValue(g, out int n);
            r.Id = $"{r.Nade.Abbrev}-{ModeAbbrev(r.Mode)}-{StrengthAbbrev(r.Strength)}-{n + 1:D2}";
            counters[g] = n + 1;
        }

        // 新结果整体替换旧结果（旧结果的可视化实体由 .cdr/.ncl/.ng 生命周期管理）
        _nadeResults[job.SteamId] = ordered;

        if (ordered.Count == 0)
            player.PrintToChat(Localizer.ForPlayer(player, "nadesearch.done_empty"));
        else
            player.PrintToChat(Localizer.ForPlayer(player, "nadesearch.done", ordered.Count));

        Server.PrintToConsole($"[PracLab] {DateTime.Now:HH:mm:ss} Search job finished: player={player.PlayerName} nade={job.Nade.Key} acc={job.Accuracy} candidates={job.PreFilterCount}->{job.MainCandidateCount}+{job.Candidates.Count - job.MainCandidateCount} found={ordered.Count}");
    }

    // ==================== 搜索命令处理器（12 条 find + 2 条配置） ====================

    /// <summary>
    /// 按键查找道具注册表条目；未命中返回 null。
    /// </summary>
    private static NadeInfo? FindNadeByKey(string key)
    {
        foreach (var nade in NadeRegistry)
        {
            if (string.Equals(nade.Key, key, StringComparison.OrdinalIgnoreCase))
                return nade;
        }
        return null;
    }

    /// <summary>
    /// find 系列命令通用处理器（.fa/.fn/.fj/.frj 及 9 条力度子命令经路由 lambda 包装至此）。
    /// 前置校验：无绘制区域 → 拒绝；道具未设置 → 默认 smoke 并提示；目标区域取最新绘制的一个。
    /// </summary>
    /// <param name="player">调用者玩家。</param>
    /// <param name="modeFilter">投掷方式过滤（null = 全部 3 种）。</param>
    /// <param name="strengthFilter">力度过滤（null = 全部 3 档）。</param>
    private void HandleFind(CCSPlayerController player, ThrowMode? modeFilter, ThrowStrength? strengthFilter)
    {
        Server.PrintToConsole("[PracLab] HandleFind: executing...");

        var steamId = player.SteamID;

        // 前置校验：无绘制区域时拒绝搜索
        if (!_targetRegions.TryGetValue(steamId, out var regions) || regions.Count == 0)
        {
            player.PrintToChat(Localizer.ForPlayer(player, "nadesearch.no_region"));
            Server.PrintToConsole($"[PracLab] {DateTime.Now:HH:mm:ss} NadeSearch {player.PlayerName} has no target region");
            return;
        }

        // 道具：未设置时默认 smoke 并提示
        if (!_nadeType.TryGetValue(steamId, out var nadeKey) || FindNadeByKey(nadeKey) is not { } nade)
        {
            nade = NadeRegistry[0]; // smoke
            _nadeType[steamId] = nade.Key;
            player.PrintToChat(Localizer.ForPlayer(player, "nadesearch.default_type", nade.Key, GetNadeDisplayName(player, nade)));
        }

        var accuracy = _nadeAccuracy.TryGetValue(steamId, out var acc) ? acc : NadeAccuracy.Mid;

        // 目标区域：最新绘制的一个
        var target = regions[^1];

        StartSearchJob(player, target, nade, accuracy, modeFilter, strengthFilter);
    }

    /// <summary>
    /// .nadeaccuracy/.nac 命令处理器：无参数显示当前档位；high|mid|low 切换；非法参数提示。
    /// </summary>
    /// <param name="player">调用者玩家。</param>
    /// <param name="args">命令参数。</param>
    private void HandleNadeAccuracy(CCSPlayerController player, string args)
    {
        Server.PrintToConsole("[PracLab] HandleNadeAccuracy: executing...");

        var steamId = player.SteamID;
        var input = args.Trim().ToLowerInvariant();

        if (input.Length == 0)
        {
            var current = _nadeAccuracy.TryGetValue(steamId, out var acc) ? acc : NadeAccuracy.Mid;
            player.PrintToChat(Localizer.ForPlayer(player, "nadesearch.accuracy_show", current.ToString().ToLowerInvariant()));
            return;
        }

        var next = input switch
        {
            "low" => NadeAccuracy.Low,
            "mid" => NadeAccuracy.Mid,
            "high" => NadeAccuracy.High,
            _ => (NadeAccuracy?)null,
        };

        if (next == null)
        {
            player.PrintToChat(Localizer.ForPlayer(player, "nadesearch.accuracy_invalid"));
            return;
        }

        _nadeAccuracy[steamId] = next.Value;
        player.PrintToChat(Localizer.ForPlayer(player, "nadesearch.accuracy_set", input));
        Server.PrintToConsole($"[PracLab] {DateTime.Now:HH:mm:ss} NadeSearch {player.PlayerName} set accuracy={input}");
    }

    /// <summary>
    /// .nadetype/.nt 命令处理器：无参数显示当前类型及可用列表；6 种类型切换；非法参数提示。
    /// </summary>
    /// <param name="player">调用者玩家。</param>
    /// <param name="args">命令参数。</param>
    private void HandleNadeType(CCSPlayerController player, string args)
    {
        Server.PrintToConsole("[PracLab] HandleNadeType: executing...");

        var steamId = player.SteamID;
        var input = args.Trim().ToLowerInvariant();

        if (input.Length == 0)
        {
            var key = _nadeType.TryGetValue(steamId, out var k) ? k : NadeRegistry[0].Key;
            var current = FindNadeByKey(key) ?? NadeRegistry[0];
            var available = string.Join(" / ", NadeRegistry.Select(n => n.Key));
            player.PrintToChat(Localizer.ForPlayer(player, "nadesearch.type_show", current.Key, GetNadeDisplayName(player, current), available));
            return;
        }

        var found = FindNadeByKey(input);
        if (found == null)
        {
            player.PrintToChat(Localizer.ForPlayer(player, "nadesearch.type_invalid"));
            return;
        }

        _nadeType[steamId] = found.Key;
        player.PrintToChat(Localizer.ForPlayer(player, "nadesearch.type_set", found.Key, GetNadeDisplayName(player, found)));
        Server.PrintToConsole($"[PracLab] {DateTime.Now:HH:mm:ss} NadeSearch {player.PlayerName} set type={found.Key}");
    }

    // ==================== 单次仿真调试命令（.nadetestsim / .nts） ====================

    /// <summary>
    /// .nadetestsim / .nts 命令处理器：按指定 yaw/pitch/mode/strength 在玩家当前位置跑一次弹道仿真，
    /// 将出手点、前 3 次碰撞详情、落点、终止原因、飞行时长输出到玩家控制台（前缀）。
    /// 用于对比 .nadetest 录制的真实弹道数据，定位「弹地衰减系数 / 出手点 / 法线恢复」等根因。
    /// 参数格式（任意顺序、可省略，全部省略时取玩家当前视角 + 默认 DuckJump/Left）：
    ///   .nts                      → yaw/pitch 取当前视角，mode=DuckJump，strength=Left
    ///   .nts 90.5 -13.4           → yaw=90.5 pitch=-13.4，mode/strength 默认
    ///   .nts 90.5 -13.4 DJ L      → 全部显式（mode/strength 顺序可互换）
    ///   .nts DJ R                 → yaw/pitch 取当前视角，mode=DuckJump strength=Right
    /// mode 关键字：N(ormal) / J(ump) / RJ / D(uck) / DJ / DRJ
    /// strength 关键字：L(eft) / M(id) / R(ight)
    /// </summary>
    /// <param name="player">调用者玩家（站位与默认视角来源）。</param>
    /// <param name="args">命令参数（yaw/pitch 浮点 + mode/strength 关键字，任意顺序）。</param>
    private void HandleNadeTestSim(CCSPlayerController player, string args)
    {
        Server.PrintToConsole("[PracLab] HandleNadeTestSim: executing...");

        var pawn = player.PlayerPawn.Value;
        if (pawn == null || !pawn.IsValid || pawn.AbsOrigin == null)
        {
            Server.PrintToConsole($"[PracLab] {DateTime.Now:HH:mm:ss} Warning NadeTestSim pawn invalid for {player.PlayerName}");
            player.PrintToChat(Localizer.ForPlayer(player, "nadetestsim.pawn_invalid"));
            return;
        }

        // 默认值：yaw/pitch 取玩家当前视角；mode=DuckJump（常见练习场景）；strength=Left（满力）
        float yaw = pawn.EyeAngles.Y;
        float pitch = pawn.EyeAngles.X;
        var mode = ThrowMode.DuckJump;
        var strength = ThrowStrength.Left;

        // 参数解析：浮点（按出现顺序赋给 yaw、pitch）+ 关键字（mode/strength 任意顺序）
        var parts = args.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var pendingFloats = new List<float>(2);
        foreach (var part in parts)
        {
            // 先按关键字匹配，未命中再尝试解析为浮点
            var lower = part.ToLowerInvariant();
            switch (lower)
            {
                case "n":
                case "normal":
                    mode = ThrowMode.Normal;
                    break;
                case "j":
                case "jump":
                    mode = ThrowMode.Jump;
                    break;
                case "rj":
                case "runjump":
                    mode = ThrowMode.RunJump;
                    break;
                case "d":
                case "duck":
                    mode = ThrowMode.Duck;
                    break;
                case "dj":
                case "duckjump":
                    mode = ThrowMode.DuckJump;
                    break;
                case "drj":
                case "duckrunjump":
                    mode = ThrowMode.DuckRunJump;
                    break;
                case "l":
                case "left":
                    strength = ThrowStrength.Left;
                    break;
                case "m":
                case "mid":
                    strength = ThrowStrength.Mid;
                    break;
                case "r":
                case "right":
                    strength = ThrowStrength.Right;
                    break;
                default:
                    // 浮点解析（InvariantCulture，兼容 90.5 / -13.4 等格式）
                    if (float.TryParse(part, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var f))
                    {
                        if (pendingFloats.Count < 2)
                            pendingFloats.Add(f);
                        else
                        {
                            player.PrintToChat(Localizer.ForPlayer(player, "nadetestsim.invalid", part));
                            return;
                        }
                    }
                    else
                    {
                        player.PrintToChat(Localizer.ForPlayer(player, "nadetestsim.invalid", part));
                        return;
                    }
                    break;
            }
        }

        if (pendingFloats.Count >= 1) yaw = pendingFloats[0];
        if (pendingFloats.Count >= 2) pitch = pendingFloats[1];

        // 道具：未设置时默认 smoke 并提示（与 HandleFind 一致）
        var steamId = player.SteamID;
        if (!_nadeType.TryGetValue(steamId, out var nadeKey) || FindNadeByKey(nadeKey) is not { } nade)
        {
            nade = NadeRegistry[0]; // smoke
            _nadeType[steamId] = nade.Key;
            player.PrintToChat(Localizer.ForPlayer(player, "nadesearch.default_type", nade.Key, GetNadeDisplayName(player, nade)));
        }

        // 取眼位 → 按方式下沉/抬升 → 计算出手点与初速度
        var eye = player.GetEyePosition();
        if (eye == null)
        {
            Server.PrintToConsole($"[PracLab] {DateTime.Now:HH:mm:ss} Warning NadeTestSim eye position unavailable for {player.PlayerName}");
            player.PrintToChat(Localizer.ForPlayer(player, "nadetestsim.pawn_invalid"));
            return;
        }

        var eyePos = ToNumerics(eye);
        var releaseEye = ReleaseEyeForMode(eyePos, mode);
        var throwOrigin = ComputeThrowOrigin(releaseEye, pitch, yaw, strength);
        var v0 = ComputeThrowVelocity(pitch, yaw, strength, mode);
        int simHz = _config.NadeSimHz;
        var skipPawn = pawn.Handle;

        // 仿真：轨迹采样到 scratch，前 3 次碰撞记录到 bounces 缓冲（参考 .ng 调试输出格式）
        var trajectory = new List<Vector3>(256);
        var bounces = new List<BounceInfo>(3);
        var sim = SimulateGrenade(throwOrigin, v0, nade, simHz, trajectory, skipPawn, null, null, bounces);

        // —— 玩家控制台诊断输出（中文，[PracLab] 前缀，便于与 .nadetest 真实数据对照）——
        var modeName = GetModeLocalizedName(player, mode);
        var strengthName = GetStrengthLocalizedName(player, strength);
        var nadeName = GetNadeDisplayName(player, nade);

        player.PrintToConsole($"[PracLab] ===== NadeTestSim 开始 =====");
        player.PrintToConsole($"[PracLab] 参数: nade={nade.Key} mode={mode} strength={strength} yaw={yaw:F2} pitch={pitch:F2}");
        player.PrintToConsole($"[PracLab] 仿真: simHz={simHz} dt={1000.0f / simHz:F3}ms");
        player.PrintToConsole($"[PracLab] 出手: eye=({eyePos.X:F1},{eyePos.Y:F1},{eyePos.Z:F1}) releaseEye=({releaseEye.X:F1},{releaseEye.Y:F1},{releaseEye.Z:F1})");
        player.PrintToConsole($"[PracLab]       origin=({throwOrigin.X:F1},{throwOrigin.Y:F1},{throwOrigin.Z:F1}) v0=({v0.X:F1},{v0.Y:F1},{v0.Z:F1}) |v0|={v0.Length():F1}");

        player.PrintToConsole($"[PracLab] 碰撞日志（前 {bounces.Count} 次）:");
        var bounceLog = FormatBounceLog(bounces);
        foreach (var entry in bounceLog)
            player.PrintToConsole($"[PracLab]   {entry}");

        var land = sim.LandPoint;
        player.PrintToConsole($"[PracLab] 结果: land=({land.X:F1},{land.Y:F1},{land.Z:F1}) term={sim.TermReason} flight={sim.FlightTime:F3}s");
        player.PrintToConsole($"[PracLab]       轨迹采样 {trajectory.Count} 点（每 4 tick 一点）");
        player.PrintToConsole($"[PracLab] ===== NadeTestSim 结束 =====");

        // 聊天提示：参数 + 结果摘要，引导玩家查看控制台
        player.PrintToChat(Localizer.ForPlayer(player, "nadetestsim.done", nadeName, modeName, strengthName, yaw, pitch, sim.TermReason, sim.FlightTime));

        Server.PrintToConsole($"[PracLab] {DateTime.Now:HH:mm:ss} NadeTestSim {player.PlayerName} nade={nade.Key} mode={mode} strength={strength} yaw={yaw:F2} pitch={pitch:F2} term={sim.TermReason} flight={sim.FlightTime:F3}s");
    }
}
