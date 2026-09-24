namespace PracLab;

/// <summary>急停评估轴类型（水平 A/D，垂直 W/S）。</summary>
internal enum HudStrafeAxis
{
    Horizontal,
    Vertical,
}

/// <summary>急停判定时序：完美 / 偏早（对侧先按）/ 偏晚（对侧后按）。</summary>
internal enum HudStrafeTiming
{
    Perfect,
    Early,
    Late,
}

/// <summary>
/// 一条急停评估记录。DiffTicks 正值 = 偏晚（先松键后按对侧），负值 = 偏早（对侧先按后松键）。
/// </summary>
/// <param name="DiffTicks">同轴双键切换 tick 差。</param>
/// <param name="Axis">所属轴。</param>
/// <param name="FromKey">释放的键（A/D/W/S）。</param>
/// <param name="ToKey">按下的键。</param>
/// <param name="IsPerfect">是否完美（|差| ≤ perfect）。</param>
/// <param name="IsSuccess">是否优秀（|差| ≤ success）。</param>
/// <param name="Timing">时序分类。</param>
/// <param name="Tick">记录发生的服务器 tick。</param>
internal readonly record struct CounterStrafeRecord(
    int DiffTicks,
    HudStrafeAxis Axis,
    string FromKey,
    string ToKey,
    bool IsPerfect,
    bool IsSuccess,
    HudStrafeTiming Timing,
    int Tick);

/// <summary>
/// 急停评估统计快照。
/// </summary>
/// <param name="AvgDiffTicks">平均差（保留 1 位小数，带符号）。</param>
/// <param name="SuccessRatePercent">优秀占比（0-100，保留 1 位小数）。</param>
/// <param name="StdDevTicks">标准差（保留 1 位小数）。</param>
/// <param name="Tendency">整体趋势：-1 偏早 / 0 正常 / 1 偏晚。</param>
internal readonly record struct CounterStrafeStats(float AvgDiffTicks, float SuccessRatePercent, float StdDevTicks, int Tendency);

/// <summary>
/// 急停评估引擎：1:1 移植 cs-match-hud assessment_engine.rs 状态机，时间粒度从 f64 秒改为服务器 tick。
/// 同轴（A/D 或 W/S）双键切换时序：先松后按 = 偏晚（diff &gt; 0），先按后松 = 偏早（diff &lt; 0），
/// |diff| &gt; max_diff 丢弃，debounce 防抖，环形历史上限。
/// </summary>
internal sealed class CounterStrafeEngine
{
    /// <summary>移动键索引：0=Left(A) 1=Right(D) 2=Forward(W) 3=Back(S)。</summary>
    public const int KeyLeft = 0;
    public const int KeyRight = 1;
    public const int KeyForward = 2;
    public const int KeyBack = 3;

    /// <summary>固定键标签（物理键盘键位，无需本地化）。</summary>
    private static readonly string[] KeyLabels = ["A", "D", "W", "S"];

    /// <summary>判定参数（tick 粒度）。</summary>
    private readonly PracticeHudConfig _config;

    /// <summary>四键按下状态。</summary>
    private readonly bool[] _pressed = new bool[4];

    /// <summary>四键最近按下 tick。</summary>
    private readonly int[] _pressTick = new int[4];

    /// <summary>双轴等待状态：0=水平 1=垂直，true 表示有键松开且对侧未按下（等待反方向按下）。</summary>
    private readonly bool[] _axisWaiting = new bool[2];

    /// <summary>等待中的释放键索引（-1 = 无）。</summary>
    private readonly int[] _axisReleasedKey = [-1, -1];

    /// <summary>等待中的释放 tick。</summary>
    private readonly int[] _axisReleaseTick = new int[2];

    /// <summary>上次成功记录的 tick（防抖）。</summary>
    private int _lastRecordTick = int.MinValue;

    /// <summary>记录历史（环形上限，超出从头部移除）。</summary>
    private readonly List<CounterStrafeRecord> _records;

    public CounterStrafeEngine(PracticeHudConfig config)
    {
        _config = config;
        _records = new List<CounterStrafeRecord>(config.StrafeHistoryLimit);
    }

    /// <summary>最近一条记录（无记录返回 null）。</summary>
    public CounterStrafeRecord? LastRecord => _records.Count > 0 ? _records[^1] : null;

    /// <summary>记录条数。</summary>
    public int RecordCount => _records.Count;

    /// <summary>
    /// 处理一次移动键边沿事件（由每 tick 按键扫描驱动）。
    /// </summary>
    /// <param name="keyIndex">键索引（KeyLeft/KeyRight/KeyForward/KeyBack）。</param>
    /// <param name="isDown">true = 按下，false = 释放。</param>
    /// <param name="tick">当前服务器 tick。</param>
    /// <returns>产生记录则返回，否则 null。</returns>
    public CounterStrafeRecord? OnKey(int keyIndex, bool isDown, int tick)
    {
        return isDown ? OnKeyDown(keyIndex, tick) : OnKeyUp(keyIndex, tick);
    }

    /// <summary>
    /// 松键处理：对侧键已按下 → 立即判定（先按对侧后松 = 偏早）；否则进入轴等待状态。
    /// 幂等：键本就未按下时忽略（每 tick 传入当前状态即可）。
    /// 对应 Rust assessment_engine.rs on_key_up。
    /// </summary>
    private CounterStrafeRecord? OnKeyUp(int keyIndex, int tick)
    {
        if (!_pressed[keyIndex])
            return null;

        _pressed[keyIndex] = false;

        var opposite = OppositeKey(keyIndex);
        var axisIdx = AxisIndexOf(keyIndex);

        if (_pressed[opposite])
        {
            // 对侧键先按下（早）：diff = 对侧按下时刻 - 本键释放时刻（负值）
            var diff = _pressTick[opposite] - tick;
            return TryRecord(keyIndex, opposite, diff, tick);
        }

        // 对侧未按下：进入等待状态，期待反方向键按下时判定（先松后按 = 偏晚）
        _axisWaiting[axisIdx] = true;
        _axisReleasedKey[axisIdx] = keyIndex;
        _axisReleaseTick[axisIdx] = tick;
        return null;
    }

    /// <summary>
    /// 按键处理：若同轴处于等待状态且本次按下的是释放键的对立键 → 判定（偏晚）。
    /// 幂等：键本已按下时忽略（每 tick 传入当前状态即可）。
    /// 对应 Rust assessment_engine.rs on_key_down。
    /// </summary>
    private CounterStrafeRecord? OnKeyDown(int keyIndex, int tick)
    {
        if (_pressed[keyIndex])
            return null;

        _pressed[keyIndex] = true;
        _pressTick[keyIndex] = tick;

        var axisIdx = AxisIndexOf(keyIndex);
        if (!_axisWaiting[axisIdx])
            return null;

        var released = _axisReleasedKey[axisIdx];
        if (released < 0 || OppositeKey(released) != keyIndex)
            return null;

        // 清除等待状态后判定：diff = 按下时刻 - 释放时刻（正值 = 偏晚）
        _axisWaiting[axisIdx] = false;
        _axisReleasedKey[axisIdx] = -1;
        var diff = tick - _axisReleaseTick[axisIdx];
        return TryRecord(released, keyIndex, diff, tick);
    }

    /// <summary>
    /// 判定与记录：超出 max_diff 丢弃、防抖过滤、按 perfect/success 阈值分级，入环形历史。
    /// 对应 Rust assessment_engine.rs try_record（时间阈值毫秒换算为 tick）。
    /// </summary>
    private CounterStrafeRecord? TryRecord(int fromKey, int toKey, int diffTicks, int tick)
    {
        // 防抖：距上次记录不足 debounce tick 丢弃（对应 Rust MIN_RECORD_INTERVAL_SECS=0.05s）
        if (_lastRecordTick != int.MinValue && tick - _lastRecordTick < _config.StrafeDebounceTicks)
            return null;

        // 超大间隔丢弃（不影响统计）
        if (Math.Abs(diffTicks) > _config.StrafeMaxDiffTicks)
            return null;

        var isPerfect = Math.Abs(diffTicks) <= _config.StrafePerfectTicks;
        var isSuccess = Math.Abs(diffTicks) <= _config.StrafeSuccessTicks;
        var timing = isPerfect ? HudStrafeTiming.Perfect : (diffTicks < 0 ? HudStrafeTiming.Early : HudStrafeTiming.Late);

        var record = new CounterStrafeRecord(
            DiffTicks: diffTicks,
            Axis: AxisOf(fromKey),
            FromKey: KeyLabels[fromKey],
            ToKey: KeyLabels[toKey],
            IsPerfect: isPerfect,
            IsSuccess: isSuccess,
            Timing: timing,
            Tick: tick);

        _lastRecordTick = tick;
        _records.Add(record);

        var limit = _config.StrafeHistoryLimit;
        if (_records.Count > limit)
            _records.RemoveRange(0, _records.Count - limit);

        return record;
    }

    /// <summary>
    /// 计算统计：平均差 / 优秀占比 / 标准差 / 整体趋势。
    /// 趋势阈值参照原版 ±5ms 折算为 ±1 tick（任务定义）。
    /// </summary>
    public CounterStrafeStats GetStats()
    {
        var count = _records.Count;
        if (count == 0)
            return new CounterStrafeStats(0f, 0f, 0f, 0);

        float sum = 0;
        int successCount = 0;
        for (var i = 0; i < count; i++)
        {
            sum += _records[i].DiffTicks;
            if (_records[i].IsSuccess)
                successCount++;
        }

        var avg = MathF.Round(sum / count, 1);

        float varianceSum = 0;
        for (var i = 0; i < count; i++)
        {
            var d = _records[i].DiffTicks - avg;
            varianceSum += d * d;
        }

        var stdDev = MathF.Round(MathF.Sqrt(varianceSum / count), 1);
        var successRate = MathF.Round(successCount * 100f / count, 1);
        var tendency = avg < -1f ? -1 : (avg > 1f ? 1 : 0);

        return new CounterStrafeStats(avg, successRate, stdDev, tendency);
    }

    /// <summary>
    /// 重置按键状态机（死亡/重生/回合切换时调用，统计保留）。
    /// </summary>
    public void ResetInputState()
    {
        Array.Clear(_pressed);
        Array.Clear(_pressTick);
        Array.Clear(_axisWaiting);
        _axisReleasedKey[0] = -1;
        _axisReleasedKey[1] = -1;
        Array.Clear(_axisReleaseTick);
        _lastRecordTick = int.MinValue;
    }

    /// <summary>
    /// 清空全部历史记录（.hudreset）。
    /// </summary>
    public void ClearRecords()
    {
        _records.Clear();
        _lastRecordTick = int.MinValue;
    }

    /// <summary>键的对立键：A↔D、W↔S。</summary>
    private static int OppositeKey(int keyIndex) => keyIndex switch
    {
        KeyLeft => KeyRight,
        KeyRight => KeyLeft,
        KeyForward => KeyBack,
        _ => KeyForward,
    };

    /// <summary>键所属轴索引：A/D = 0（水平），W/S = 1（垂直）。</summary>
    private static int AxisIndexOf(int keyIndex) => keyIndex switch
    {
        KeyLeft or KeyRight => 0,
        _ => 1,
    };

    /// <summary>键所属轴类型。</summary>
    private static HudStrafeAxis AxisOf(int keyIndex) => keyIndex switch
    {
        KeyLeft or KeyRight => HudStrafeAxis.Horizontal,
        _ => HudStrafeAxis.Vertical,
    };
}
