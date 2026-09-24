using CounterStrikeSharp.API;

namespace PracLab;

/// <summary>开枪稳定采样标签（渲染文本走 lang 键 hud.label.*）。</summary>
internal enum ShotLabelKind
{
    Stable,
    Crouch,
    MicroMove,
    RunShot,
    LowSpeedSway,
    StartupLow,
}

/// <summary>开枪稳定采样原因（对应 cs-match-hud ShootingErrorReason 的 tick 化子集）。</summary>
internal enum ShotReasonKind
{
    NoMovement,
    Crouching,
    CrouchGrace,
    SingleDirectionHeld,
    AxisConflict,
    CounterStrafeBraking,
    NaturalDeceleration,
    LowSpeedMovement,
}

/// <summary>
/// 一条开枪稳定采样记录。
/// </summary>
/// <param name="SpeedRatio">水平速度 / 武器精度阈值（≤1 稳定）。</param>
/// <param name="Error">归一化误差（0-1，ratio 1.5 以上封顶 1）。</param>
/// <param name="Label">显示标签类别。</param>
/// <param name="Reason">原因分类。</param>
/// <param name="IsStable">是否稳定成功。</param>
/// <param name="Crouching">采样时是否蹲下。</param>
/// <param name="Tick">采样 tick。</param>
internal readonly record struct ShotRecord(
    float SpeedRatio,
    float Error,
    ShotLabelKind Label,
    ShotReasonKind Reason,
    bool IsStable,
    bool Crouching,
    int Tick);

/// <summary>
/// 开枪稳定统计快照。
/// </summary>
/// <param name="AvgError">平均误差（保留 3 位）。</param>
/// <param name="StableRatePercent">稳定占比（0-100，保留 1 位）。</param>
internal readonly record struct ShotStats(float AvgError, float StableRatePercent);

/// <summary>
/// 开枪稳定引擎：IN_ATTACK 按下 tick 采样真实水平速度（替代 cs-match-hud 的按键模拟移速模型），
/// 对比武器精度阈值输出 speed_ratio 与误差；处理蹲射、下蹲释放宽限、移动键启动宽限、
/// 同轴冲突与反向制动语义（照搬 cs-match-hud engine.rs evaluate_sample 并 tick 化）。
/// </summary>
internal sealed class ShotStabilityEngine
{
    /// <summary>微动判定上限：speed_ratio ≤ 1.5 判「微动」，否则「跑打」。</summary>
    private const float MicroSpeedRatio = 1.5f;

    /// <summary>移动键启动宽限的 speed_ratio 上限（超过 2.0 视为真的在跑）。</summary>
    private const float StartupGraceMaxSpeedRatio = 2.0f;

    /// <summary>反向制动判定的最小速度分量（units/s），低于视为静止。</summary>
    private const float CounterStrafeMinSpeed = 10f;

    /// <summary>移动键索引：0=Forward 1=Back 2=Left 3=Right（对应 MovementEstimator 顺序）。</summary>
    private const int MvForward = 0;
    private const int MvBack = 1;
    private const int MvLeft = 2;
    private const int MvRight = 3;

    private readonly PracticeHudConfig _config;

    /// <summary>四移动键按下状态。</summary>
    private readonly bool[] _pressed = new bool[4];

    /// <summary>四移动键最近按下 tick。</summary>
    private readonly int[] _pressTick = new int[4];

    private bool _crouchPressed;

    /// <summary>上次松开蹲键的 tick（-1 = 无）。</summary>
    private int _lastCrouchReleaseTick = -1;

    /// <summary>上次任意移动键输入（按下或释放）的 tick（-1 = 无）。</summary>
    private int _lastMovementInputTick = -1;

    /// <summary>同轴双键冲突状态（本 tick 双键同按即置位，单键恢复时清除）。</summary>
    private bool _axisConflict;

    /// <summary>反向制动中（输入方向与速度方向相反）。</summary>
    private bool _counterStrafeActive;

    /// <summary>记录历史（环形上限）。</summary>
    private readonly List<ShotRecord> _records;

    public ShotStabilityEngine(PracticeHudConfig config)
    {
        _config = config;
        _records = new List<ShotRecord>(config.ShotHistoryLimit);
    }

    /// <summary>最近一条记录（无记录返回 null）。</summary>
    public ShotRecord? LastRecord => _records.Count > 0 ? _records[^1] : null;

    /// <summary>记录条数。</summary>
    public int RecordCount => _records.Count;

    /// <summary>
    /// 每 tick 更新状态；攻击键按下边沿时采样并产生记录。
    /// </summary>
    /// <param name="tick">当前服务器 tick。</param>
    /// <param name="buttons">玩家按钮位掩码。</param>
    /// <param name="velX">世界速度 X。</param>
    /// <param name="velY">世界速度 Y。</param>
    /// <param name="yawDegrees">玩家视角 yaw（度）。</param>
    /// <param name="threshold">当前武器精度阈值（units/s）。</param>
    /// <param name="fireDownEdge">本 tick 是否为攻击键按下边沿。</param>
    /// <returns>攻击边沿时返回采样记录，否则 null。</returns>
    public ShotRecord? Update(int tick, PlayerButtons buttons, float velX, float velY, float yawDegrees, float threshold, bool fireDownEdge)
    {
        // —— 1. 移动键 / 蹲下状态更新 ——
        var forward = (buttons & PlayerButtons.Forward) != 0;
        var back = (buttons & PlayerButtons.Back) != 0;
        var left = (buttons & PlayerButtons.Moveleft) != 0;
        var right = (buttons & PlayerButtons.Moveright) != 0;
        var duck = (buttons & PlayerButtons.Duck) != 0;

        UpdateKey(MvForward, forward, tick);
        UpdateKey(MvBack, back, tick);
        UpdateKey(MvLeft, left, tick);
        UpdateKey(MvRight, right, tick);

        if (_crouchPressed && !duck)
        {
            _lastCrouchReleaseTick = tick;
        }

        if (duck && !_crouchPressed)
        {
            _lastCrouchReleaseTick = -1;
        }

        _crouchPressed = duck;

        // —— 2. 速度状态更新（真实速度替代模拟模型）——
        // 同轴冲突：本 tick 同轴双键按下
        _axisConflict = (forward && back) || (left && right);

        // 输入意图：同轴双键时取最近按下的方向（对应 resolve_axis_input 的 tick 化）
        var inputForward = ResolveAxisInput(MvBack, MvForward);
        var inputRight = ResolveAxisInput(MvLeft, MvRight);

        // 输入向量投影到世界轴（Source 惯例：yaw=0 面朝 +X，右手边为 -Y）
        var yawRad = yawDegrees * MathF.PI / 180f;
        var cosYaw = MathF.Cos(yawRad);
        var sinYaw = MathF.Sin(yawRad);
        var forwardSpeed = velX * cosYaw + velY * sinYaw;
        var rightSpeed = velX * sinYaw - velY * cosYaw;

        // 反向制动：任一轴输入方向与该轴速度分量反号且速度显著（对应 counter_strafe_active）
        _counterStrafeActive =
            (inputForward != 0 && inputForward * forwardSpeed < 0 && MathF.Abs(forwardSpeed) > CounterStrafeMinSpeed) ||
            (inputRight != 0 && inputRight * rightSpeed < 0 && MathF.Abs(rightSpeed) > CounterStrafeMinSpeed);

        // —— 3. 攻击边沿采样 ——
        if (!fireDownEdge)
            return null;

        var horizontalSpeed = MathF.Sqrt(velX * velX + velY * velY);
        var record = Evaluate(tick, horizontalSpeed, threshold);
        _records.Add(record);

        var limit = _config.ShotHistoryLimit;
        if (_records.Count > limit)
            _records.RemoveRange(0, _records.Count - limit);

        return record;
    }

    /// <summary>
    /// 采样判定：蹲射 / 蹲宽限 / 起步宽限 / 轴冲突 / 反向制动 / 自然减速 / 稳定（对应 evaluate_sample）。
    /// </summary>
    private ShotRecord Evaluate(int tick, float speed, float threshold)
    {
        var crouching = _crouchPressed;
        var crouchGrace = CrouchGraceActive(tick);
        var exitBlend = CrouchExitBlend(tick);

        var speedRatio = threshold > 0f ? speed / threshold : 0f;
        var movementKeysDown = CountPressed();

        if (crouchGrace)
        {
            return CreateRecord(0f, ShotLabelKind.Stable, ShotReasonKind.CrouchGrace, tick, crouching, speedRatio, crouchGraceActive: true);
        }

        if (crouching)
        {
            return CreateRecord(0f, ShotLabelKind.Crouch, ShotReasonKind.Crouching, tick, crouching, speedRatio, crouchGraceActive: false);
        }

        // 下蹲退出坡道：误差按坡道线性恢复
        if (exitBlend < 1f && speedRatio > 1f)
        {
            speedRatio = 1f + (speedRatio - 1f) * exitBlend;
        }

        // 移动键启动宽限：刚按下移动键的短时间内 speed_ratio 未超过宽限上限不判跑打
        if (MovementPressInWindow(tick) && movementKeysDown > 0 && !_axisConflict &&
            speedRatio > 1f && speedRatio <= StartupGraceMaxSpeedRatio)
        {
            speedRatio = 1f;
        }

        if (speedRatio <= 1f)
        {
            var label = StableLabel(tick, movementKeysDown);
            var reason = movementKeysDown == 0 && !MovementInStartWindow(tick)
                ? ShotReasonKind.NoMovement
                : ShotReasonKind.LowSpeedMovement;
            return CreateRecord(0f, label, reason, tick, crouching, speedRatio, crouchGraceActive: false);
        }

        // speed_ratio > 1：误差归一化（ratio 1.0→1.5 线性映射 0→1，≥1.5 封顶）
        var error = ErrorFromSpeedRatio(speedRatio);
        var label2 = speedRatio <= MicroSpeedRatio ? ShotLabelKind.MicroMove : ShotLabelKind.RunShot;
        var reason2 = _axisConflict
            ? ShotReasonKind.AxisConflict
            : movementKeysDown > 0 && !_counterStrafeActive
                ? ShotReasonKind.SingleDirectionHeld
                : _counterStrafeActive
                    ? ShotReasonKind.CounterStrafeBraking
                    : ShotReasonKind.NaturalDeceleration;

        return CreateRecord(error, label2, reason2, tick, crouching, speedRatio, crouchGraceActive: false);
    }

    /// <summary>
    /// 构造记录并计算稳定判定。
    /// </summary>
    private ShotRecord CreateRecord(float error, ShotLabelKind label, ShotReasonKind reason, int tick,
        bool crouching, float speedRatio, bool crouchGraceActive)
    {
        var isStable = speedRatio <= 1f && error <= _config.ShotSuccessErrorThreshold && !crouchGraceActive;
        return new ShotRecord(speedRatio, error, label, reason, isStable, crouching, tick);
    }

    /// <summary>
    /// 稳定场景标签（对应 stable_reason_label）。
    /// </summary>
    private ShotLabelKind StableLabel(int tick, int movementKeysDown)
    {
        if (movementKeysDown == 0)
        {
            return MovementInStartWindow(tick) ? ShotLabelKind.LowSpeedSway : ShotLabelKind.Stable;
        }

        if (MovementInStartWindow(tick))
        {
            return ShotLabelKind.StartupLow;
        }

        if (_axisConflict)
        {
            return ShotLabelKind.LowSpeedSway;
        }

        return ShotLabelKind.Stable;
    }

    /// <summary>下蹲释放宽限是否激活（蹲未按下且距松蹲 ≤ 宽限 tick）。</summary>
    private bool CrouchGraceActive(int tick)
    {
        if (_crouchPressed)
            return false;

        return _lastCrouchReleaseTick >= 0 && tick - _lastCrouchReleaseTick <= _config.ShotCrouchReleaseGraceTicks;
    }

    /// <summary>
    /// 下蹲退出坡道系数（0 = 完全宽限，1 = 正常判定；对应 crouch_exit_blend）。
    /// </summary>
    private float CrouchExitBlend(int tick)
    {
        if (_crouchPressed || CrouchGraceActive(tick))
            return 0f;

        if (_lastCrouchReleaseTick < 0)
            return 1f;

        var age = tick - _lastCrouchReleaseTick;
        var grace = _config.ShotCrouchReleaseGraceTicks;
        var ramp = _config.ShotCrouchExitRampTicks;

        if (age <= grace)
            return 0f;

        if (age >= grace + ramp)
            return 1f;

        return (age - grace) / (float)ramp;
    }

    /// <summary>移动键输入在启动宽限窗口内（对应 movement_in_start_window）。</summary>
    private bool MovementInStartWindow(int tick)
    {
        return _lastMovementInputTick >= 0 && tick - _lastMovementInputTick <= _config.ShotGraceTicks;
    }

    /// <summary>存在已按下且按住时间在宽限窗口内的移动键（对应 movement_press_in_window）。</summary>
    private bool MovementPressInWindow(int tick)
    {
        for (var i = 0; i < 4; i++)
        {
            if (_pressed[i] && tick - _pressTick[i] <= _config.ShotGraceTicks)
                return true;
        }

        return false;
    }

    /// <summary>同轴输入解析：双键同按取最近按下方向，单键取其方向，无键为 0（对应 resolve_axis_input）。</summary>
    private int ResolveAxisInput(int negIndex, int posIndex)
    {
        var neg = _pressed[negIndex];
        var pos = _pressed[posIndex];

        if (neg && pos)
        {
            // 双键：取最近按下的方向
            return _pressTick[posIndex] >= _pressTick[negIndex] ? 1 : -1;
        }

        if (neg)
            return -1;

        if (pos)
            return 1;

        return 0;
    }

    /// <summary>更新单个移动键状态并追踪输入时刻（对应 handle_event 的 movement 分支）。</summary>
    private void UpdateKey(int index, bool isDown, int tick)
    {
        if (isDown != _pressed[index])
        {
            _lastMovementInputTick = tick;
        }

        if (isDown && !_pressed[index])
        {
            _pressTick[index] = tick;
        }

        _pressed[index] = isDown;
    }

    /// <summary>当前按下的移动键数量。</summary>
    private int CountPressed()
    {
        var count = 0;
        for (var i = 0; i < 4; i++)
        {
            if (_pressed[i])
                count++;
        }

        return count;
    }

    /// <summary>误差归一化（对应 error_from_speed_ratio）。</summary>
    private static float ErrorFromSpeedRatio(float speedRatio)
    {
        if (speedRatio <= 1f)
            return 0f;

        if (speedRatio >= MicroSpeedRatio)
            return 1f;

        return (speedRatio - 1f) / (MicroSpeedRatio - 1f);
    }

    /// <summary>
    /// 计算统计：平均误差与稳定占比。
    /// </summary>
    public ShotStats GetStats()
    {
        var count = _records.Count;
        if (count == 0)
            return new ShotStats(0f, 0f);

        float errorSum = 0;
        int stableCount = 0;
        for (var i = 0; i < count; i++)
        {
            errorSum += _records[i].Error;
            if (_records[i].IsStable)
                stableCount++;
        }

        return new ShotStats(
            MathF.Round(errorSum / count, 3),
            MathF.Round(stableCount * 100f / count, 1));
    }

    /// <summary>
    /// 清空全部记录（.hudreset）。
    /// </summary>
    public void ClearRecords()
    {
        _records.Clear();
    }

    /// <summary>
    /// 重置输入状态机（死亡/重生/回合切换，统计保留）。
    /// </summary>
    public void ResetInputState()
    {
        Array.Clear(_pressed);
        Array.Clear(_pressTick);
        _crouchPressed = false;
        _lastCrouchReleaseTick = -1;
        _lastMovementInputTick = -1;
        _axisConflict = false;
        _counterStrafeActive = false;
    }
}
