using System.Globalization;
using CounterStrikeSharp.API;

namespace PracLab;

/// <summary>
/// 练习 HUD 判定参数配置：从 csgo/cfg/PracLab/hud.cfg 加载（键值对格式同 config.cfg，// 注释）。
/// 所有判定阈值以 tick 为粒度（64 tick 服务器 1 tick ≈ 15.6ms）。
/// </summary>
internal sealed class PracticeHudConfig
{
    /// <summary>急停评估：同轴双键切换差超过此 tick 数丢弃（默认 8 ≈ 125ms）。</summary>
    public int StrafeMaxDiffTicks { get; set; } = 8;

    /// <summary>急停评估：|差| ≤ 此 tick 数判「完美」（默认 0 = 同 tick 完成切换）。</summary>
    public int StrafePerfectTicks { get; set; } = 0;

    /// <summary>急停评估：|差| ≤ 此 tick 数判「优秀」（默认 1）。</summary>
    public int StrafeSuccessTicks { get; set; } = 1;

    /// <summary>急停评估：防抖最小记录间隔（tick），防止一次切换产生多条记录。</summary>
    public int StrafeDebounceTicks { get; set; } = 4;

    /// <summary>急停评估：历史记录环形上限。</summary>
    public int StrafeHistoryLimit { get; set; } = 50;

    /// <summary>开枪稳定：历史记录环形上限。</summary>
    public int ShotHistoryLimit { get; set; } = 20;

    /// <summary>空中同步：历史记录环形上限（最近空中段）。</summary>
    public int SyncHistoryLimit { get; set; } = 30;

    /// <summary>HUD 面板推送节流间隔（tick），仅变化时差量推送。</summary>
    public int HudPushIntervalTicks { get; set; } = 2;

    /// <summary>开枪稳定：移动键启动宽限（tick，~180ms@64tick），起步低速不判跑打。</summary>
    public int ShotGraceTicks { get; set; } = 12;

    /// <summary>开枪稳定：下蹲释放宽限（tick，~45ms@64tick），松蹲后短时间内仍判蹲射精度。</summary>
    public int ShotCrouchReleaseGraceTicks { get; set; } = 3;

    /// <summary>开枪稳定：下蹲退出坡道（tick，~90ms@64tick），松蹲后误差按坡道线性恢复。</summary>
    public int ShotCrouchExitRampTicks { get; set; } = 6;

    /// <summary>开枪稳定：误差 ≤ 此值且 speed_ratio ≤ 1 判「稳定」成功。</summary>
    public float ShotSuccessErrorThreshold { get; set; } = 0.35f;

    /// <summary>开枪稳定：是否启用内置武器精度阈值表（false 时统一用默认阈值）。</summary>
    public bool ShotWeaponThresholdsEnabled { get; set; } = true;

    /// <summary>开枪稳定：未收录武器的默认精度阈值（units/s）。</summary>
    public float ShotDefaultThreshold { get; set; } = 250f;

    /// <summary>弹道追踪：射击间隔超过此 tick 数（~1s@64tick）重置开火序列。</summary>
    public int RecoilResetTicks { get; set; } = 64;

    /// <summary>弹道追踪：单个开火序列最大记录弹数（防连发无限增长）。</summary>
    public int RecoilMaxShotsPerSequence { get; set; } = 30;

    /// <summary>弹道追踪：是否在世界空间绘制压枪轨迹折线（鼠标移动轨迹可视化）。</summary>
    public bool RecoilBeamEnabled { get; set; } = true;

    /// <summary>弹道追踪：轨迹缩放（1 度视角变化映射的画板世界单位数）。</summary>
    public float RecoilTraceScale { get; set; } = 2.5f;

    /// <summary>弹道追踪：轨迹画板与玩家眼位的距离（世界单位）。</summary>
    public float RecoilTraceDistance { get; set; } = 80f;

    /// <summary>弹道追踪：单条轨迹最大 beam 段数（防溢出）。</summary>
    public int RecoilTraceMaxSegments { get; set; } = 256;

    /// <summary>
    /// 内置武器精度阈值表（units/s）：速度低于阈值时该武器移动射击精度可接受。
    /// 语义参照 cs-match-hud speed_ratio = 实际水平速度 / 阈值；ratio ≤ 1 判稳定。
    /// cs-match-hud 原版无武器表（其 accuracy_threshold 为玩家设置乘法），本表按 CS2 武器
    /// 移动精度特性整理，未收录武器回退 ShotDefaultThreshold。
    /// </summary>
    public static readonly Dictionary<string, float> DefaultWeaponSpeedThresholds = new(StringComparer.Ordinal)
    {
        ["weapon_awp"] = 100f,
        ["weapon_ssg08"] = 120f,
        ["weapon_g3sg1"] = 130f,
        ["weapon_scar20"] = 130f,
        ["weapon_ak47"] = 140f,
        ["weapon_m4a1"] = 150f,
        ["weapon_m4a1_silencer"] = 150f,
        ["weapon_galil"] = 180f,
        ["weapon_famas"] = 180f,
        ["weapon_aug"] = 180f,
        ["weapon_sg556"] = 180f,
        ["weapon_deagle"] = 220f,
    };

    /// <summary>运行时武器阈值表（可被 cfg 扩展覆盖）。</summary>
    private readonly Dictionary<string, float> _weaponThresholds = new(DefaultWeaponSpeedThresholds, StringComparer.Ordinal);

    /// <summary>
    /// 查询武器精度阈值（units/s）。
    /// </summary>
    /// <param name="weaponName">武器 designer name（如 weapon_ak47）。</param>
    /// <returns>阈值速度。</returns>
    public float GetWeaponThreshold(string weaponName)
    {
        if (weaponName.Length > 0 && _weaponThresholds.TryGetValue(weaponName, out var value))
            return value;

        return ShotDefaultThreshold;
    }

    /// <summary>
    /// 从指定路径加载 hud.cfg（键值对格式：`key value`，// 注释行忽略）。
    /// 文件不存在时静默使用默认值（首次运行不强制生成，hud.cfg 随插件分发）。
    /// </summary>
    /// <param name="path">hud.cfg 绝对路径。</param>
    public void Load(string path)
    {
        Server.PrintToConsole("[PracLab] PracticeHudConfig.Load: executing...");

        if (!File.Exists(path))
        {
            Server.PrintToConsole($"[PracLab] {DateTime.Now:HH:mm:ss} PracticeHud config not found: {path}, using defaults");
            return;
        }

        try
        {
            foreach (var rawLine in File.ReadAllLines(path))
            {
                var line = rawLine.Trim();
                if (line.Length == 0 || line.StartsWith("//"))
                    continue;

                var sep = line.IndexOfAny([' ', '\t']);
                if (sep < 0)
                    continue;

                var key = line[..sep].Trim();
                var value = line[(sep + 1)..].Trim();

                switch (key)
                {
                    case "strafe_max_diff_ticks":
                        StrafeMaxDiffTicks = ReadInt(value, 1, 64, StrafeMaxDiffTicks, key);
                        break;
                    case "strafe_perfect_ticks":
                        StrafePerfectTicks = ReadInt(value, 0, 16, StrafePerfectTicks, key);
                        break;
                    case "strafe_success_ticks":
                        StrafeSuccessTicks = ReadInt(value, 0, 32, StrafeSuccessTicks, key);
                        break;
                    case "strafe_debounce_ticks":
                        StrafeDebounceTicks = ReadInt(value, 0, 32, StrafeDebounceTicks, key);
                        break;
                    case "strafe_history_limit":
                        StrafeHistoryLimit = ReadInt(value, 1, 500, StrafeHistoryLimit, key);
                        break;
                    case "shot_history_limit":
                        ShotHistoryLimit = ReadInt(value, 1, 500, ShotHistoryLimit, key);
                        break;
                    case "sync_history_limit":
                        SyncHistoryLimit = ReadInt(value, 1, 200, SyncHistoryLimit, key);
                        break;
                    case "hud_push_interval_ticks":
                        HudPushIntervalTicks = ReadInt(value, 1, 16, HudPushIntervalTicks, key);
                        break;
                    case "shot_grace_ticks":
                        ShotGraceTicks = ReadInt(value, 0, 64, ShotGraceTicks, key);
                        break;
                    case "shot_crouch_release_grace_ticks":
                        ShotCrouchReleaseGraceTicks = ReadInt(value, 0, 32, ShotCrouchReleaseGraceTicks, key);
                        break;
                    case "shot_crouch_exit_ramp_ticks":
                        ShotCrouchExitRampTicks = ReadInt(value, 0, 64, ShotCrouchExitRampTicks, key);
                        break;
                    case "shot_success_error_threshold":
                        if (float.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var err) && err > 0f && err <= 1f)
                            ShotSuccessErrorThreshold = err;
                        else
                            WarnInvalid(key, value);
                        break;
                    case "shot_weapon_thresholds_enabled":
                        if (bool.TryParse(value, out var tbl))
                            ShotWeaponThresholdsEnabled = tbl;
                        else
                            WarnInvalid(key, value);
                        break;
                    case "shot_default_threshold":
                        if (float.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var th) && th > 0f && th <= 1000f)
                            ShotDefaultThreshold = th;
                        else
                            WarnInvalid(key, value);
                        break;
                    case "recoil_reset_ticks":
                        RecoilResetTicks = ReadInt(value, 1, 256, RecoilResetTicks, key);
                        break;
                    case "recoil_max_shots_per_sequence":
                        RecoilMaxShotsPerSequence = ReadInt(value, 1, 100, RecoilMaxShotsPerSequence, key);
                        break;
                    case "recoil_beam_enabled":
                        if (bool.TryParse(value, out var beam))
                            RecoilBeamEnabled = beam;
                        else
                            WarnInvalid(key, value);
                        break;
                    case "recoil_trace_scale":
                        if (float.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var ts) && ts > 0f && ts <= 50f)
                            RecoilTraceScale = ts;
                        else
                            WarnInvalid(key, value);
                        break;
                    case "recoil_trace_distance":
                        if (float.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var td) && td >= 20f && td <= 500f)
                            RecoilTraceDistance = td;
                        else
                            WarnInvalid(key, value);
                        break;
                    case "recoil_trace_max_segments":
                        RecoilTraceMaxSegments = ReadInt(value, 8, 1024, RecoilTraceMaxSegments, key);
                        break;
                    default:
                        Server.PrintToConsole($"[PracLab] {DateTime.Now:HH:mm:ss} Warning unknown PracticeHud ConVar: {key}, ignored");
                        break;
                }
            }

            // 武器阈值表开关：关闭时清空运行时表，统一回退默认阈值
            _weaponThresholds.Clear();
            if (ShotWeaponThresholdsEnabled)
            {
                foreach (var (weapon, threshold) in DefaultWeaponSpeedThresholds)
                    _weaponThresholds[weapon] = threshold;
            }

            Server.PrintToConsole($"[PracLab] {DateTime.Now:HH:mm:ss} PracticeHud config loaded: strafe(max={StrafeMaxDiffTicks},perfect={StrafePerfectTicks},success={StrafeSuccessTicks},debounce={StrafeDebounceTicks}) push={HudPushIntervalTicks}t thresholds={(ShotWeaponThresholdsEnabled ? _weaponThresholds.Count.ToString() : "off")}");
        }
        catch (Exception ex)
        {
            Server.PrintToConsole($"[PracLab] {DateTime.Now:HH:mm:ss} Warning PracticeHud config load failed, using defaults - {ex.Message}");
        }
    }

    /// <summary>
    /// 解析整型配置值并校验范围，非法时打印警告并回退默认值。
    /// </summary>
    private int ReadInt(string value, int min, int max, int fallback, string key)
    {
        if (int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var result) && result >= min && result <= max)
            return result;

        WarnInvalid(key, value);
        return fallback;
    }

    /// <summary>
    /// 打印非法配置值警告（英文日志）。
    /// </summary>
    private static void WarnInvalid(string key, string value)
    {
        Server.PrintToConsole($"[PracLab] {DateTime.Now:HH:mm:ss} Warning PracticeHud {key} invalid value: {value}, using default");
    }
}
