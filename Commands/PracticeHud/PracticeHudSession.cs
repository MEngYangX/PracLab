namespace PracLab;

using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Modules.Utils;

/// <summary>练习 HUD 模块标识。</summary>
internal enum PracticeHudModule
{
    Strafe,
    Shot,
    Sync,
    Recoil,
}

/// <summary>
/// 每玩家练习 HUD 会话：四个模块判定引擎实例、模块开关状态与 dialog variable 差量推送缓存。
/// 玩家断线时整体销毁；统计跨开关保留，仅 .hudreset / 死亡输入重置时清空。
/// </summary>
internal sealed class PracticeHudSession
{
    /// <summary>玩家槽位。</summary>
    public int Slot { get; }

    /// <summary>急停评估引擎。</summary>
    public readonly CounterStrafeEngine StrafeEngine;

    /// <summary>开枪稳定引擎。</summary>
    public readonly ShotStabilityEngine ShotEngine;

    /// <summary>空中同步引擎。</summary>
    public readonly AirSyncEngine SyncEngine;

    /// <summary>弹道追踪引擎。</summary>
    public readonly RecoilTraceEngine RecoilEngine;

    /// <summary>上一 tick 攻击键按下状态（边沿检测）。</summary>
    public bool LastAttackDown;

    /// <summary>弹道轨迹 beam 实体列表（当前开火序列的折线段，新序列/清理时销毁）。</summary>
    public readonly List<CEntityInstance> RecoilBeams = new();

    /// <summary>压枪轨迹画板原点（世界坐标，null = 未锚定）。</summary>
    public Vector? RecoilTraceOrigin;

    /// <summary>压枪轨迹画板右基向量（锚定时由视角构造，水平面内）。</summary>
    public Vector RecoilTraceRight = new(0, 0, 0);

    /// <summary>压枪轨迹画板上基向量（锚定时由视角构造，垂直于视线）。</summary>
    public Vector RecoilTraceUp = new(0, 0, 0);

    /// <summary>上一采样帧的 aim pitch（鼠标真实俯仰，差分基准）。</summary>
    public float RecoilTraceLastAimPitch;

    /// <summary>上一采样帧的 aim yaw（鼠标真实偏航，差分基准）。</summary>
    public float RecoilTraceLastAimYaw;

    /// <summary>上一采样点世界坐标（null = 尚无采样点，下一采样从它连线）。</summary>
    public Vector? RecoilTraceLastPoint;

    /// <summary>压枪轨迹小位移累积（pitch，度）：低于落段阈值的位移先累积，减少碎 beam。</summary>
    public float RecoilTracePendingPitch;

    /// <summary>压枪轨迹小位移累积（yaw，度）。</summary>
    public float RecoilTracePendingYaw;

    /// <summary>各模块开关（独立记忆，互不影响）。</summary>
    private readonly bool[] _moduleEnabled = new bool[4];

    /// <summary>
    /// 是否处于 .hudmove 移动编辑模式（输入捕获开启、可点击面板选中和放置）。
    /// 退出路径：放置完成 / Tab 键（Scoreboard 位）/ 再次 .hudmove / 断线 / 地图切换。
    /// </summary>
    public bool IsMoveEditMode;

    /// <summary>移动编辑模式中已选中的面板（null = 尚未选中，网格层不可见）。</summary>
    public PracticeHudModule? MoveSelectedModule;

    /// <summary>
    /// 各模块面板当前位置（位置索引 = 行*3+列；行 0-2、列 0-2，共 3×3=9 档九宫格）。
    /// 默认为左列三点（strafe=r0c0、shot=r1c0、sync=r2c0，即纵向均匀分布），与 VXML/CSS 初始布局一致；
    /// 位置互斥：目标格被占用时交换。仅会话内记忆：换图/重连后回到默认位置。
    /// Recoil 无 HUD 面板，对应槽位恒为 null。
    /// </summary>
    public readonly int?[] PanelPositions = { 0, 3, 6, null };

    /// <summary>dialog variable 差量推送缓存（键 = panelId|variableName）。</summary>
    private readonly Dictionary<string, string> _dialogCache = new(StringComparer.Ordinal);

    /// <summary>CSS class 差量推送缓存（键 = slot|panelId|className，值 = 上次 hasClass）。</summary>
    private readonly Dictionary<string, bool> _classCache = new(StringComparer.Ordinal);

    public PracticeHudSession(int slot, PracticeHudConfig config)
    {
        Slot = slot;
        StrafeEngine = new CounterStrafeEngine(config);
        ShotEngine = new ShotStabilityEngine(config);
        SyncEngine = new AirSyncEngine(config);
        RecoilEngine = new RecoilTraceEngine(config);
    }

    /// <summary>
    /// 查询模块开关状态。
    /// </summary>
    public bool IsModuleEnabled(PracticeHudModule module) => _moduleEnabled[(int)module];

    /// <summary>
    /// 是否存在任一开启的模块（决定 HUD 实体显隐与采样是否运行）。
    /// </summary>
    public bool IsAnyModuleEnabled => _moduleEnabled[0] || _moduleEnabled[1] || _moduleEnabled[2] || _moduleEnabled[3];

    /// <summary>
    /// 设置模块开关（不触发 HUD 实体管理，由命令层联动）。
    /// </summary>
    /// <returns>设置后的开关状态。</returns>
    public bool SetModuleEnabled(PracticeHudModule module, bool enabled)
    {
        _moduleEnabled[(int)module] = enabled;
        return enabled;
    }

    /// <summary>
    /// dialog variable 差量判断：值未变化返回 false（跳过推送），变化则更新缓存并返回 true。
    /// </summary>
    /// <param name="panelId">面板 ID。</param>
    /// <param name="variableName">变量名。</param>
    /// <param name="value">待推送值。</param>
    public bool ShouldPushDialogVariable(string panelId, string variableName, string value)
    {
        var key = string.Concat(panelId, "|", variableName);
        if (_dialogCache.TryGetValue(key, out var last) && string.Equals(last, value, StringComparison.Ordinal))
            return false;

        _dialogCache[key] = value;
        return true;
    }

    /// <summary>
    /// CSS class 差量判断：状态未变化返回 false，变化则更新缓存并返回 true。
    /// </summary>
    /// <param name="panelId">面板 ID。</param>
    /// <param name="className">class 名。</param>
    /// <param name="hasClass">待推送状态。</param>
    public bool ShouldPushClass(string panelId, string className, bool hasClass)
    {
        var key = string.Concat(panelId, "|", className);
        if (_classCache.TryGetValue(key, out var last) && last == hasClass)
            return false;

        _classCache[key] = hasClass;
        return true;
    }

    /// <summary>
    /// 清空差量缓存（实体重建 / 地图切换后全量重推）。
    /// </summary>
    public void InvalidateCaches()
    {
        _dialogCache.Clear();
        _classCache.Clear();
    }

    /// <summary>
    /// .hudreset：清空四个引擎的全部统计（开关状态保留）。
    /// </summary>
    public void ResetAllStats()
    {
        StrafeEngine.ClearRecords();
        StrafeEngine.ResetInputState();
        ShotEngine.ClearRecords();
        ShotEngine.ResetInputState();
        SyncEngine.ClearRecords();
        RecoilEngine.Reset();
    }

    /// <summary>
    /// 仅重置输入状态机（死亡/重生/pawn 失效时调用，统计保留）。
    /// </summary>
    public void ResetInputStates()
    {
        StrafeEngine.ResetInputState();
        ShotEngine.ResetInputState();
        SyncEngine.ResetInputState();
        RecoilEngine.ResetInputState();
        LastAttackDown = false;
    }
}
