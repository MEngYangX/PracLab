namespace PracLab;

/// <summary>
/// 一条空中段同步记录。
/// </summary>
/// <param name="SyncRate">本次空中段同步率（0-100）。</param>
/// <param name="AirTicks">计入统计的空中 tick 数（分母，含 dead air / overlap / bad angles）。</param>
/// <param name="Tick">落地的服务器 tick。</param>
internal readonly record struct AirSyncRecord(float SyncRate, int AirTicks, int Tick);

/// <summary>
/// 空中同步引擎：移植 cs2kz-metamod jumpstats 的 sync 判定（research/cs2kz-metamod，
/// Strafe::End 与 Jump 结算：sync = 速度增益时长 / 总空中时长）。
/// 空中每 tick 以水平速度大小变化判定：有移动输入且速度增益超过阈值记同步 tick；
/// 无输入（dead air）、同轴双键同按（overlap）、无增益（bad angles）与速度损失 tick
/// 一并计入分母但不记同步。落地时输出本次空中段同步率并统计近期均值/最佳。
/// </summary>
internal sealed class AirSyncEngine
{
    /// <summary>速度增益判定阈值（units/s，与 cs2kz JS_EPSILON 同值 0.03125）。</summary>
    private const float SpeedEpsilon = 0.03125f;

    private readonly PracticeHudConfig _config;

    /// <summary>上一 tick 是否在空中。</summary>
    private bool _airborne;

    /// <summary>本空中段 tick 数（分母：除起跳基准 tick 外的全部空中 tick）。</summary>
    private int _airTicks;

    /// <summary>本空中段：速度增益 tick 数（分子）。</summary>
    private int _matchedTicks;

    /// <summary>上一 tick 水平速度（空中速度基准，跳跃不改水平速度故跨起跳连续）。</summary>
    private float _prevAirSpeed;

    /// <summary>是否已有速度基准（空中第一 tick 只建立基准不判定）。</summary>
    private bool _hasPrevSpeed;

    /// <summary>最近空中段记录（环形上限）。</summary>
    private readonly List<AirSyncRecord> _records;

    public AirSyncEngine(PracticeHudConfig config)
    {
        _config = config;
        _records = new List<AirSyncRecord>(config.SyncHistoryLimit);
    }

    /// <summary>最近一条落地记录（无记录返回 null）。</summary>
    public AirSyncRecord? LastRecord => _records.Count > 0 ? _records[^1] : null;

    /// <summary>记录条数。</summary>
    public int RecordCount => _records.Count;

    /// <summary>
    /// 每 tick 更新：维护空中/落地边沿，空中段以速度增益判定同步（cs2kz 算法）。
    /// </summary>
    /// <param name="tick">当前服务器 tick。</param>
    /// <param name="onGround">玩家是否在地面。</param>
    /// <param name="forwardPressed">W 键是否按下。</param>
    /// <param name="backPressed">S 键是否按下。</param>
    /// <param name="leftPressed">A 键是否按下。</param>
    /// <param name="rightPressed">D 键是否按下。</param>
    /// <param name="horizontalSpeed">本 tick 水平速度大小（units/s）。</param>
    /// <returns>本 tick 恰好落地且空中段有效时返回本次记录，否则 null。</returns>
    public AirSyncRecord? Update(int tick, bool onGround, bool forwardPressed, bool backPressed, bool leftPressed, bool rightPressed, float horizontalSpeed)
    {
        AirSyncRecord? result = null;

        if (onGround)
        {
            // 落地边沿：输出本次空中段记录
            if (_airborne && _airTicks > 0)
            {
                var syncRate = MathF.Round(_matchedTicks * 100f / _airTicks, 1);
                var record = new AirSyncRecord(syncRate, _airTicks, tick);
                _records.Add(record);

                var limit = _config.SyncHistoryLimit;
                if (_records.Count > limit)
                    _records.RemoveRange(0, _records.Count - limit);

                result = record;
            }

            _airborne = false;
            _airTicks = 0;
            _matchedTicks = 0;
            _hasPrevSpeed = false;
            return result;
        }

        // —— 空中 ——
        if (!_airborne)
        {
            _airborne = true;
            _airTicks = 0;
            _matchedTicks = 0;
            _hasPrevSpeed = false;
        }

        if (!_hasPrevSpeed)
        {
            // 空中第一 tick：只建立速度基准，不判定（跳跃不改水平速度，基准与地面末速连续）
            _hasPrevSpeed = true;
        }
        else
        {
            var speedDelta = horizontalSpeed - _prevAirSpeed;
            var hasInput = forwardPressed || backPressed || leftPressed || rightPressed;
            var overlap = (forwardPressed && backPressed) || (leftPressed && rightPressed);

            // cs2kz 语义：分母为全部空中 tick（dead air / overlap / bad angles 均计入但不记同步）
            _airTicks++;

            // 有移动输入且水平速度实际增益 → 同步 tick；速度增益只来自有效的空中加速（strafe）
            if (!overlap && hasInput && speedDelta > SpeedEpsilon)
                _matchedTicks++;
        }

        _prevAirSpeed = horizontalSpeed;
        return result;
    }

    /// <summary>
    /// 当前空中段实时同步率（未落地时返回 0）。
    /// </summary>
    public float CurrentSyncRate => _airTicks > 0 ? MathF.Round(_matchedTicks * 100f / _airTicks, 1) : 0f;

    /// <summary>
    /// 近期空中段平均同步率。
    /// </summary>
    public float AverageSyncRate()
    {
        var count = _records.Count;
        if (count == 0)
            return 0f;

        float sum = 0;
        for (var i = 0; i < count; i++)
        {
            sum += _records[i].SyncRate;
        }

        return MathF.Round(sum / count, 1);
    }

    /// <summary>
    /// 近期最佳同步率。
    /// </summary>
    public float BestSyncRate()
    {
        var count = _records.Count;
        var best = 0f;
        for (var i = 0; i < count; i++)
        {
            if (_records[i].SyncRate > best)
                best = _records[i].SyncRate;
        }

        return best;
    }

    /// <summary>
    /// 清空全部记录（.hudreset）。
    /// </summary>
    public void ClearRecords()
    {
        _records.Clear();
        _airTicks = 0;
        _matchedTicks = 0;
        _airborne = false;
        _hasPrevSpeed = false;
    }

    /// <summary>
    /// 重置空中状态机（死亡/重生/回合切换，统计保留）。
    /// </summary>
    public void ResetInputState()
    {
        _airborne = false;
        _airTicks = 0;
        _matchedTicks = 0;
        _hasPrevSpeed = false;
    }
}
