namespace PracLab;

/// <summary>
/// 弹道追踪引擎：开火时记录弹序与视角 punch 角偏移序列（移植 cs-match-hud RecoilTrace 模块的
/// 数据组织方式，采样源为服务器侧 schema：m_flRecoilIndex 判定序列 + m_csViewPunchAngle 偏移）。
/// 射击间隔超过阈值或 recoil index 回退（换弹/切枪）时重置当前开火序列。
/// 世界弹着点由 bullet_impact 事件侧采集（命令层），不经过本引擎。
/// </summary>
internal sealed class RecoilTraceEngine
{
    private readonly PracticeHudConfig _config;

    /// <summary>当前开火序列：每发的（弹序，punch pitch，punch yaw）。</summary>
    private readonly List<(int Shot, float Pitch, float Yaw)> _sequence;

    /// <summary>上次开火 tick（-1 = 无）。</summary>
    private int _lastFireTick = -1;

    /// <summary>上次开火时武器的 recoil index（用于检测换弹/切枪导致的回退）。</summary>
    private uint _lastRecoilIndex;

    /// <summary>是否已有 recoil index 基准。</summary>
    private bool _hasRecoilIndex;

    public RecoilTraceEngine(PracticeHudConfig config)
    {
        _config = config;
        _sequence = new List<(int, float, float)>(config.RecoilMaxShotsPerSequence);
    }

    /// <summary>当前开火序列弹数。</summary>
    public int SequenceCount => _sequence.Count;

    /// <summary>
    /// 当前序列是否活跃（距上次开火未超过重置阈值）。
    /// 停火超阈值后轨迹停止延长（但旧轨迹保留到下一次开火重置时清除）。
    /// </summary>
    public bool IsSequenceActive(int tick)
    {
        return _lastFireTick >= 0 && tick - _lastFireTick <= _config.RecoilResetTicks;
    }

    /// <summary>
    /// 处理一次开火（攻击键按下边沿或 weapon_fire 事件）。
    /// </summary>
    /// <param name="tick">当前服务器 tick。</param>
    /// <param name="viewPunchPitch">视角 punch 俯仰分量（度）。</param>
    /// <param name="viewPunchYaw">视角 punch 偏航分量（度）。</param>
    /// <param name="recoilIndex">当前武器 m_flRecoilIndex（取整；不可用时传 0）。</param>
    /// <param name="recoilIndexAvailable">recoil index 是否可读（false 时仅靠射击间隔判定序列）。</param>
    /// <returns>true = 本次开火触发了序列重置（上一条轨迹结束），调用方应清除旧轨迹 beam。</returns>
    public bool OnFire(int tick, float viewPunchPitch, float viewPunchYaw, uint recoilIndex, bool recoilIndexAvailable)
    {
        // 序列重置判定：
        // 1. 距上次开火超过阈值（停顿后重新开火）
        // 2. recoil index 回退或大幅跳变（换弹/切枪后重置）
        var reset = false;
        if (_lastFireTick < 0 || tick - _lastFireTick > _config.RecoilResetTicks)
        {
            reset = true;
        }
        else if (recoilIndexAvailable && _hasRecoilIndex)
        {
            // 仅检测回退（换弹/切枪后 index 重置）：边沿时读到的可能是开火前旧值，
            // 前进方向可能 +1 跳变，不能以超前进判定重置
            if (recoilIndex < _lastRecoilIndex)
                reset = true;
        }

        if (reset)
        {
            _sequence.Clear();
            _hasRecoilIndex = false;
        }

        _lastFireTick = tick;
        _lastRecoilIndex = recoilIndex;
        _hasRecoilIndex = recoilIndexAvailable;

        // 弹序 = 当前序列长度 + 1；超过上限丢弃新弹（防连发无限增长）
        if (_sequence.Count >= _config.RecoilMaxShotsPerSequence)
            return reset;

        _sequence.Add((_sequence.Count + 1, viewPunchPitch, viewPunchYaw));
        return reset;
    }

    /// <summary>
    /// 清空当前序列（.hudreset）。
    /// </summary>
    public void Reset()
    {
        _sequence.Clear();
        _lastFireTick = -1;
        _lastRecoilIndex = 0;
        _hasRecoilIndex = false;
    }

    /// <summary>
    /// 重置序列判定状态但保留当前弹序显示（死亡/重生时调用）。
    /// </summary>
    public void ResetInputState()
    {
        _sequence.Clear();
        _lastFireTick = -1;
        _lastRecoilIndex = 0;
        _hasRecoilIndex = false;
    }
}
