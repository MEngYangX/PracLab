using System.Globalization;
using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Core.Attributes;
using CounterStrikeSharp.API.Core.Attributes.Registration;
using CounterStrikeSharp.API.Core.Translations;
using CounterStrikeSharp.API.Modules.Commands;
using CounterStrikeSharp.API.Modules.Events;
using CounterStrikeSharp.API.Modules.Timers;
using CounterStrikeSharp.API.Modules.Utils;

namespace PracLab;

/// <summary>
/// PracLab 插件主类（partial）。
/// 提供 CS2 练习模式的配置加载、地图列表显示与切换、练习命令（机器人/道具/出生点/队伍等）功能。
/// 通过 config.cfg 控制总开关和默认语言，玩家使用 .prac / !prac 开启 practice 模式。
/// 各功能命令实现拆分到 Commands/ 目录下的 partial 文件，本文件保留插件入口、路由表、门控、配置加载与共享数据结构。
/// </summary>
public partial class PracLab : BasePlugin
{
    /// <summary>
    /// 练习配置的 exec 路径（相对于 csgo/cfg/ 目录）。
    /// </summary>
    private const string PracConfigPath = "PracLab/prac.cfg";

    /// <summary>
    /// Dryrun 配置的 exec 路径（相对于 csgo/cfg/ 目录）。
    /// 参考 MEngZy PracticeMode.cs ExecDryRunCFG：dryrun 时加载竞技模式配置打一个回合。
    /// </summary>
    private const string DryRunConfigPath = "PracLab/dryrun.cfg";

    /// <summary>
    /// 插件配置文件相对路径（相对于 csgo/ 目录）。
    /// </summary>
    private const string PluginConfigRelativePath = "cfg/PracLab/config.cfg";

    /// <summary>
    /// 换图倒计时秒数。
    /// </summary>
    private const int MapChangeCountdownSeconds = 5;

    /// <summary>
    /// 练习模式是否已开启。地图切换命令与新增命令仅在练习模式下可用。
    /// </summary>
    private bool _isPracMode;

    /// <summary>
    /// Dryrun 模式是否已开启。
    /// dryrun 是从 prac 模式临时切换到竞技配置打一个回合，回合结束后自动回到 prac 模式。
    /// 参考 MEngZy PracticeMode.cs isDryRun 字段与 EventRoundEnd 逻辑。
    /// </summary>
    private bool _isDryRun;

    /// <summary>
    /// 是否有待执行的换图倒计时，防止重复发起。
    /// </summary>
    private bool _isMapChangePending;

    /// <summary>
    /// 插件配置实例。
    /// </summary>
    private PracLabConfig _config = new();

    /// <summary>
    /// 命令路由表。键为命令名或别名（不区分大小写），值为对应路由定义。
    /// </summary>
    private readonly Dictionary<string, CommandRoute> _routes = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// 玩家闪光弹免疫状态。键为 SteamID，值为是否启用免疫。
    /// </summary>
    private readonly Dictionary<ulong, bool> _noflashState = new();

    /// <summary>
    /// 玩家计时器开始时间。键为 SteamID，值为开始计时的本地时间。
    /// </summary>
    private readonly Dictionary<ulong, DateTime> _timerState = new();

    /// <summary>
    /// 玩家最后投掷记录。键为 SteamID，值为投掷瞬间的位置/角度/武器名。
    /// </summary>
    private readonly Dictionary<ulong, GrenadeThrowRecord> _lastGrenadeThrow = new();

    /// <summary>
    /// 练习模式下生成的 bot 字典。键为 bot UserId，值为属性字典（controller/position/owner/crouchstate）。
    /// 参考 MEngZy pracUsedBots 实现，支持准星射线检测踢出指定 bot。
    /// </summary>
    private readonly Dictionary<int, Dictionary<string, object>> _pracBots = new();

    /// <summary>
    /// bot 碰撞管理定时器字典。键为 bot UserId，值为对应的循环定时器。
    /// 用于持续检测 bot 与 owner 的碰撞状态：重合时关闭碰撞（避免卡住），不重合时开启碰撞（可以攻击）。
    /// 定时器在 bot 被踢出或地图切换时需 Kill。
    /// </summary>
    private readonly Dictionary<int, CounterStrikeSharp.API.Modules.Timers.Timer> _botCollisionTimers = new();

    /// <summary>
    /// fastforward 是否激活中（防止重复触发）。
    /// </summary>
    private bool _isFastForwardActive;

    /// <summary>
    /// 可用练习地图列表（命令名, 地图文件名）。
    /// </summary>
    private static readonly (string Command, string MapFile)[] PracticeMaps =
    [
        ("inferno", "de_inferno"),
        ("mirage",  "de_mirage"),
        ("nuke",    "de_nuke"),
        ("ancient", "de_ancient"),
        ("vertigo", "de_vertigo"),
        ("anubis",  "de_anubis"),
        ("dust2",   "de_dust2"),
        ("train",   "de_train"),
        ("cache",   "de_cache"),
    ];

    public override string ModuleName => "PracLab Plugin";

    public override string ModuleVersion => "0.3.0";

    /// <summary>
    /// 插件加载入口。加载配置、设置默认语言、注册聊天监听器、构建路由表、注册事件监听器。
    /// 注册的事件：
    /// - OnEntitySpawned：记录玩家投掷物的位置/角度/速度/类名，用于 .rethrow 复现弹道（Fix 8+9）
    /// - EventPlayerBlind：玩家被闪时清零 FlashDuration/FlashMaxAlpha，用于 noflash 免疫（Fix 5）
    /// - EventPlayerHurt：实时向攻击者显示伤害信息，参考 MEngZy MatchZy.cs（Bug 4）
    /// - EventPlayerSpawn：bot 重生时传送到原始创建位置（Bug 5，在 BotCollisionHandler.cs）
    /// - OnMapStart：地图切换时重置所有插件状态（Fix 11）
    /// </summary>
    /// <param name="hotReload">是否为热重载。</param>
    public override void Load(bool hotReload)
    {
        Server.PrintToConsole("[PracLab] Load: executing...");

        // 加载配置文件（若不存在则自动创建默认配置）
        LoadConfig();

        // 设置服务器默认语言
        try
        {
            CultureInfo.DefaultThreadCurrentUICulture = new CultureInfo(_config.DefaultLanguage);
        }
        catch (CultureNotFoundException)
        {
            Server.PrintToConsole($"[PracLab] {DateTime.Now:HH:mm:ss} Warning invalid language code: {_config.DefaultLanguage}, fallback to zh-CN");
            CultureInfo.DefaultThreadCurrentUICulture = new CultureInfo("zh-CN");
        }

        Server.PrintToConsole($"[PracLab] {DateTime.Now:HH:mm:ss} Core default language: {_config.DefaultLanguage}, plugin enabled: {_config.Enabled} (hotReload: {hotReload})");

        // 注册 say 和 say_team 监听器（前缀严格化：仅 . 和 ! 触发）
        AddCommandListener("say", OnPlayerSay, HookMode.Pre);
        AddCommandListener("say_team", OnPlayerSay, HookMode.Pre);

        // 注册投掷物实体生成监听器：在实体生成时记录玩家最后投掷位置/角度/速度/类名
        // Fix 8+9：替代原 EventGrenadeThrown，OnEntitySpawned 触发时实体已有 AbsVelocity，可完整复现弹道
        RegisterListener<Listeners.OnEntitySpawned>(OnEntitySpawned);

        // 注册玩家被闪光弹闪到事件：用于 noflash 免疫（Fix 5，替代原 0.1 秒轮询定时器）
        RegisterEventHandler<EventPlayerBlind>(OnPlayerBlind);

        // 注册玩家受伤事件：用于实时伤害信息显示（Bug 4，参考 MEngZy MatchZy.cs 第 414-438 行）
        // 注意：不使用 [GameEventHandler] 特性，避免重复注册导致伤害信息打印两次
        RegisterEventHandler<EventPlayerHurt>(OnPlayerHurt);

        // 注册玩家重生事件：用于 bot 重生时传送回原始创建位置（Bug 5，在 BotCollisionHandler.cs）
        RegisterEventHandler<EventPlayerSpawn>(OnPlayerSpawn);

        // 注册回合结束事件：用于 dryrun 回合结束后自动回到 prac 模式（参考 MEngZy EventRoundEnd）
        RegisterEventHandler<EventRoundEnd>(OnRoundEnd);

        // 注册 OnMapStart 监听器：地图切换时重置所有插件状态（Fix 11）
        RegisterListener<Listeners.OnMapStart>(OnMapStart);

        // 构建命令路由表（既有命令 + 33 条新命令）
        BuildRouteTable();

        // 动态注册每个地图的切换命令（控制台 css_inferno 等）
        foreach (var (cmd, mapFile) in PracticeMaps)
        {
            var capturedCmd = cmd;
            var capturedMapFile = mapFile;
            AddCommand($"css_{cmd}", $"切换到地图 {mapFile}", (player, command) =>
            {
                ChangeMap(player, capturedCmd, capturedMapFile);
            });
        }

        Server.PrintToConsole($"[PracLab] {DateTime.Now:HH:mm:ss} Core registered {PracticeMaps.Length} map change commands, route table has {_routes.Count} keys");

        // 探测 PracLabReplayEngine C++ Metamod 插件是否已加载（DLL/SO 可能未部署）
        // 不可用时所有 replay 命令向玩家提示 replay.engine_not_found，但不阻塞插件加载
        DetectReplayEngineAvailability();

        // 确保录制文件存储目录存在（csgo/cfg/PracLab/recordings/）
        EnsureRecordingsDir();
    }

    /// <summary>
    /// 构建命令路由表。所有命令（含别名）统一注册到 _routes，由 OnPlayerSay 查表分发。
    /// </summary>
    private void BuildRouteTable()
    {
        Server.PrintToConsole("[PracLab] BuildRouteTable: executing...");

        // —— 既有命令：仅校验 _config.Enabled（prac 模式本身用 prac 命令开启）——
        AddRoute(new CommandRoute("prac", [], HandlePrac, RequiresPracMode: false));
        AddRoute(new CommandRoute("map", [], HandleMap, RequiresPracMode: false));
        foreach (var (cmd, mapFile) in PracticeMaps)
        {
            var capturedCmd = cmd;
            var capturedMapFile = mapFile;
            AddRoute(new CommandRoute(cmd, [], (p, _) => ChangeMap(p, capturedCmd, capturedMapFile), RequiresPracMode: false));
        }

        // —— T3 机器人管理（4 条）——
        // Bug 1: HandleBot 有 forceCrouch 默认参数，方法组无法直接转换，用 lambda 包装
        AddRoute(new CommandRoute("bot", [], (p, a) => HandleBot(p, a), RequiresPracMode: true));
        AddRoute(new CommandRoute("crouchbot", ["cbot"], HandleCrouchBot, RequiresPracMode: true));
        AddRoute(new CommandRoute("kickall", [], HandleKickAllBots, RequiresPracMode: true));
        AddRoute(new CommandRoute("kick", [], HandleKickBot, RequiresPracMode: true));

        // —— T4 道具与环境清理（2 条）——
        AddRoute(new CommandRoute("clear", [], HandleClear, RequiresPracMode: true));
        AddRoute(new CommandRoute("break", ["br"], HandleBreak, RequiresPracMode: true));

        // —— T5 时间与无敌（3 条）——
        AddRoute(new CommandRoute("fastforward", ["ff"], HandleFastForward, RequiresPracMode: true));
        AddRoute(new CommandRoute("noflash", [], HandleNoflash, RequiresPracMode: true));
        AddRoute(new CommandRoute("god", [], HandleGod, RequiresPracMode: true));

        // —— T6 出生点传送（9 条 + 别名）——
        AddRoute(new CommandRoute("spawn", ["s"], HandleSpawn, RequiresPracMode: true));
        AddRoute(new CommandRoute("ctspawn", ["cts"], HandleCtSpawn, RequiresPracMode: true));
        AddRoute(new CommandRoute("tspawn", ["ts"], HandleTSpawn, RequiresPracMode: true));
        AddRoute(new CommandRoute("bestspawn", ["bs"], HandleBestSpawn, RequiresPracMode: true));
        AddRoute(new CommandRoute("worstspawn", ["ws"], HandleWorstSpawn, RequiresPracMode: true));
        AddRoute(new CommandRoute("bestctspawn", ["bcts"], HandleBestCtSpawn, RequiresPracMode: true));
        AddRoute(new CommandRoute("worstctspawn", ["wcts"], HandleWorstCtSpawn, RequiresPracMode: true));
        AddRoute(new CommandRoute("besttspawn", ["bts"], HandleBestTSpawn, RequiresPracMode: true));
        AddRoute(new CommandRoute("worsttspawn", ["wts"], HandleWorstTSpawn, RequiresPracMode: true));

        // —— T6.1 出生点方框可视化（2 条）——
        // prac 模式开启时自动显示；提供 .showspawns / .hidespawns 手动控制
        // 玩家对准方框按 E 键（使用键）即可传送到该出生点
        AddRoute(new CommandRoute("showspawns", [], HandleShowSpawns, RequiresPracMode: true));
        AddRoute(new CommandRoute("hidespawns", [], HandleHideSpawns, RequiresPracMode: true));

        // —— T7 队伍切换（1 条）——
        // .ct / .t / .spec 已移除，使用 CS2 自带换队逻辑（M 键队伍选择菜单）
        // 仅保留 .watch：强制所有其他玩家进入观察者模式
        AddRoute(new CommandRoute("watch", ["fas"], HandleWatch, RequiresPracMode: true));

        // —— T8 道具重投与位置回溯（7 条 + 别名）——
        AddRoute(new CommandRoute("rethrow", ["rt"], HandleRethrow, RequiresPracMode: true));
        AddRoute(new CommandRoute("rethrowsmoke", ["rethrows"], HandleRethrowSmoke, RequiresPracMode: true));
        AddRoute(new CommandRoute("rethrownade", ["rethrown"], HandleRethrowNade, RequiresPracMode: true));
        AddRoute(new CommandRoute("rethrowflash", ["rethrowf"], HandleRethrowFlash, RequiresPracMode: true));
        AddRoute(new CommandRoute("rethrowmolotov", ["rethrowm"], HandleRethrowMolotov, RequiresPracMode: true));
        AddRoute(new CommandRoute("rethrowdecoy", ["rethrowd"], HandleRethrowDecoy, RequiresPracMode: true));
        AddRoute(new CommandRoute("last", ["ls"], HandleLast, RequiresPracMode: true));

        // —— T9 计时器（1 条）——
        AddRoute(new CommandRoute("timer", [], HandleTimer, RequiresPracMode: true));

        // —— T10 ConVar 快速切换（3 条）——
        // Bug 6+7 修复：拆分为专用 handler，避免类型错误（mp_solid_teammates 和 sv_showimpacts 是 int 而非 bool）
        AddRoute(new CommandRoute("solid", [], HandleToggleSolid, RequiresPracMode: true));
        AddRoute(new CommandRoute("impacts", [], HandleToggleImpacts, RequiresPracMode: true));
        AddRoute(new CommandRoute("traj", [], HandleToggleTraj, RequiresPracMode: true));

        // —— T11 帮助（1 条）——
        // 不要求 prac 模式，仅校验 _config.Enabled，让玩家随时可查看
        AddRoute(new CommandRoute("help", [], HandleHelp, RequiresPracMode: false));

        // —— T12 Dryrun 与回合控制（2 条 + 别名）——
        // .dryrun：从 prac 临时切换到竞技配置打一个回合，回合结束自动回到 prac（参考 MEngZy）
        // .restartround：重新开始当前回合（mp_restartgame）
        AddRoute(new CommandRoute("dryrun", ["dry"], HandleDryRun, RequiresPracMode: true));
        AddRoute(new CommandRoute("restartround", ["rr"], HandleRestartRound, RequiresPracMode: true));

        // —— T13 回放系统（7 条）——
        // 通过 P/Invoke 调用 PracLabReplayEngine C++ Metamod 插件录制/回放玩家移动轨迹
        // .record：3 秒倒计时后开始录制，按 F 键或 .stoprecord 停止
        // .replay [Id]：自动创建 Bot（与玩家同队）并回放指定录制（未指定 Id 则使用最近一条）
        //   关键：不设置 bot_quota/bot_quota_mode，否则引擎 fill 模式会持续补充 bot（根因）
        // .clearrecord <Id>：删除指定录制（文件 + 列表条目）
        // .clearrecordall：清除所有录制
        // .currentrecord：查看录制列表（控制台输出表格）
        AddRoute(new CommandRoute("record", [], HandleRecord, RequiresPracMode: true));
        AddRoute(new CommandRoute("stoprecord", [], HandleStopRecord, RequiresPracMode: true));
        AddRoute(new CommandRoute("replay", [], HandleReplay, RequiresPracMode: true));
        AddRoute(new CommandRoute("stopreplay", [], HandleStopReplay, RequiresPracMode: true));
        AddRoute(new CommandRoute("clearrecord", [], HandleClearRecord, RequiresPracMode: true));
        AddRoute(new CommandRoute("clearrecordall", [], HandleClearRecordAll, RequiresPracMode: true));
        AddRoute(new CommandRoute("currentrecord", ["currentrec"], HandleCurrentRecord, RequiresPracMode: true));
    }

    /// <summary>
    /// 注册一条命令路由（含所有别名）到路由表。
    /// </summary>
    /// <param name="route">命令路由定义。</param>
    private void AddRoute(CommandRoute route)
    {
        _routes[route.Name] = route;
        foreach (var alias in route.Aliases)
            _routes[alias] = route;
    }

    /// <summary>
    /// 拦截全局/队伍聊天消息，统一通过路由表分发命令。
    /// 仅当消息以 `.` 或 `!` 开头时才进入命令路由；其他形式一律放行给 CSS。
    /// </summary>
    private HookResult OnPlayerSay(CCSPlayerController? player, CommandInfo command)
    {
        if (player == null || !player.IsValid)
            return HookResult.Continue;

        var message = command.ArgString.Trim().Trim('"');

        // 严格前缀：仅 . 和 ! 触发 PracLab 命令
        if (message.Length == 0 || (message[0] != '.' && message[0] != '!'))
            return HookResult.Continue;

        var body = message[1..];

        // 拆分命令名与剩余参数
        var sep = body.IndexOf(' ');
        var cmdName = sep < 0 ? body : body[..sep];
        var args = sep < 0 ? string.Empty : body[(sep + 1)..].Trim();

        if (cmdName.Length == 0)
            return HookResult.Continue;

        // 查表分发
        if (!_routes.TryGetValue(cmdName, out var route))
            return HookResult.Continue;

        // 门控：prac / map 命令仅校验 _config.Enabled；新增命令需同时校验 prac 模式
        if (route.RequiresPracMode)
        {
            if (!EnsurePracMode(player))
                return HookResult.Handled;
        }
        else
        {
            if (!EnsureEnabled(player))
                return HookResult.Handled;
        }

        // 调用处理器（try-catch 防止异常导致插件崩溃）
        try
        {
            route.Handler(player, args);
        }
        catch (Exception ex)
        {
            Server.PrintToConsole($"[PracLab] {DateTime.Now:HH:mm:ss} Error command {cmdName} failed - {ex.Message}");
            player.PrintToChat(Localizer.ForPlayer(player, "command.error", ex.Message));
        }

        return HookResult.Handled;
    }

    /// <summary>
    /// 检查插件是否启用。未启用时向调用者发送提示。
    /// </summary>
    /// <param name="player">调用者，为 null 时表示服务器控制台。</param>
    /// <returns>true 表示已启用，false 表示未启用。</returns>
    private bool EnsureEnabled(CCSPlayerController? player)
    {
        if (_config.Enabled)
            return true;

        if (player != null)
            player.PrintToChat(Localizer.ForPlayer(player, "prac.disabled"));
        else
            Server.PrintToConsole("[PracLab] practice mode disabled");

        return false;
    }

    /// <summary>
    /// 校验新命令门控条件：praclab_enabled = true 且 _isPracMode = true。
    /// 不满足时按情况打印 prac.disabled 或 prac.mode.required。
    /// </summary>
    /// <param name="player">调用者玩家。</param>
    /// <returns>true 表示通过门控，false 表示拒绝。</returns>
    private bool EnsurePracMode(CCSPlayerController player)
    {
        if (!_config.Enabled)
        {
            player.PrintToChat(Localizer.ForPlayer(player, "prac.disabled"));
            return false;
        }

        if (!_isPracMode)
        {
            player.PrintToChat(Localizer.ForPlayer(player, "prac.mode.required"));
            return false;
        }

        return true;
    }

    /// <summary>
    /// 传送玩家到指定位置/角度。返回是否成功。
    /// 共享辅助方法，供 SpawnCommands / GrenadeCommands 使用。
    /// </summary>
    /// <param name="player">玩家。</param>
    /// <param name="position">目标位置。</param>
    /// <param name="angle">目标角度（可为 null，使用默认 0,0,0）。</param>
    /// <returns>true 表示传送成功，false 表示失败（已向玩家打印原因）。</returns>
    private bool TeleportPlayerTo(CCSPlayerController player, Vector? position, QAngle? angle)
    {
        if (position is not Vector pos)
        {
            player.PrintToChat(Localizer.ForPlayer(player, "spawn.no_position"));
            return false;
        }

        var pawn = player.PlayerPawn.Value;
        if (pawn == null || !pawn.IsValid)
        {
            player.PrintToChat(Localizer.ForPlayer(player, "spawn.no_pawn"));
            return false;
        }

        var ang = angle ?? new QAngle(0, 0, 0);
        pawn.Teleport(pos, ang, new Vector(0, 0, 0));
        return true;
    }

    /// <summary>
    /// 计算两个 CSS Vector 之间的平方距离（避免开方开销，仅用于比较）。
    /// </summary>
    private static float DistanceSquared(Vector a, Vector b)
    {
        var dx = a.X - b.X;
        var dy = a.Y - b.Y;
        var dz = a.Z - b.Z;
        return dx * dx + dy * dy + dz * dz;
    }

    // ==================== 配置加载 ====================

    /// <summary>
    /// 默认配置文件模板（CS2 ConVar 风格，自动创建时使用）。
    /// </summary>
    private const string DefaultConfigContent =
        "// PracLab 插件配置文件\n" +
        "// 放置位置：csgo/cfg/PracLab/config.cfg\n" +
        "// 格式：每行一个 ConVar，空格分隔 key value，// 开头为注释\n" +
        "\n" +
        "// 是否启用 practice 模式总开关 (true/false)\n" +
        "// 这是默认值，false 时 .prac / .map / 地图切换 等所有功能均不可用\n" +
        "praclab_enabled true\n" +
        "\n" +
        "// 默认语言代码 (zh-CN / en)\n" +
        "// 语言仅由此配置项控制，玩家无法在游戏内切换\n" +
        "praclab_default_language zh-CN\n";

    /// <summary>
    /// 从 csgo/cfg/PracLab/config.cfg 加载插件配置（CS2 ConVar 文本格式）。
    /// 文件不存在时自动创建默认配置。每行格式：`convar_name value`，
    /// 以 `//` 开头的行为注释，空行被忽略。未知 convar 跳过，已知但值非法时回退默认值。
    /// </summary>
    private void LoadConfig()
    {
        Server.PrintToConsole("[PracLab] LoadConfig: executing...");

        var config = new PracLabConfig();

        try
        {
            var configPath = Path.Combine(Server.GameDirectory, PluginConfigRelativePath);

            if (!File.Exists(configPath))
            {
                // 自动创建默认配置文件
                Directory.CreateDirectory(Path.GetDirectoryName(configPath)!);
                File.WriteAllText(configPath, DefaultConfigContent);
                Server.PrintToConsole($"[PracLab] {DateTime.Now:HH:mm:ss} Core created default config file: {configPath}");
                _config = config;
                return;
            }

            // 逐行解析 ConVar 文本格式
            foreach (var rawLine in File.ReadAllLines(configPath))
            {
                var line = rawLine.Trim();
                if (line.Length == 0 || line.StartsWith("//"))
                    continue;

                // 拆分 key value：首个空白为分隔符，其后内容整体作为 value
                var sep = line.IndexOfAny([' ', '\t']);
                if (sep < 0)
                    continue;

                var key = line[..sep].Trim();
                var value = line[(sep + 1)..].Trim();

                switch (key)
                {
                    case "praclab_enabled":
                        if (bool.TryParse(value, out var enabled))
                            config.Enabled = enabled;
                        else
                            Server.PrintToConsole($"[PracLab] {DateTime.Now:HH:mm:ss} Warning praclab_enabled invalid value: {value}, using default {config.Enabled}");
                        break;

                    case "praclab_default_language":
                        config.DefaultLanguage = value;
                        break;

                    default:
                        Server.PrintToConsole($"[PracLab] {DateTime.Now:HH:mm:ss} Warning unknown ConVar: {key}, ignored");
                        break;
                }
            }

            _config = config;
            Server.PrintToConsole($"[PracLab] {DateTime.Now:HH:mm:ss} Core config loaded: {configPath}");
        }
        catch (Exception ex)
        {
            Server.PrintToConsole($"[PracLab] {DateTime.Now:HH:mm:ss} Error config load failed, using defaults - {ex.Message}");
            _config = config;
        }
    }

    // ==================== 嵌套类型 ====================

    /// <summary>
    /// 命令路由定义：命令名、别名、处理器委托、是否需要 prac 模式。
    /// </summary>
    /// <param name="Name">主命令名（不含前缀）。</param>
    /// <param name="Aliases">别名数组（不含前缀）。</param>
    /// <param name="Handler">处理器委托，参数为玩家与命令名后的剩余参数。</param>
    /// <param name="RequiresPracMode">是否需要 prac 模式（true 则同时校验 praclab_enabled 与 _isPracMode）。</param>
    private readonly record struct CommandRoute(
        string Name,
        string[] Aliases,
        Action<CCSPlayerController, string> Handler,
        bool RequiresPracMode);

    /// <summary>
    /// 玩家最后投掷记录（值类型，避免持有可能失效的 native 句柄）。
    /// Fix 8+9：新增速度（VelX/VelY/VelZ）与投掷物类名（ProjectileClass），用于完整复现弹道与类型校验。
    /// </summary>
    /// <param name="PosX">位置 X。</param>
    /// <param name="PosY">位置 Y。</param>
    /// <param name="PosZ">位置 Z。</param>
    /// <param name="AngX">角度 X（pitch）。</param>
    /// <param name="AngY">角度 Y（yaw）。</param>
    /// <param name="AngZ">角度 Z（roll）。</param>
    /// <param name="VelX">速度 X（投掷瞬间的世界速度，用于重投复现弹道）。</param>
    /// <param name="VelY">速度 Y。</param>
    /// <param name="VelZ">速度 Z。</param>
    /// <param name="Weapon">武器名（如 weapon_smokegrenade）。</param>
    /// <param name="ProjectileClass">投掷物实体类名（如 smokegrenade_projectile），用于 .rethrowsmoke 等命令的类型校验。</param>
    private readonly record struct GrenadeThrowRecord(
        float PosX, float PosY, float PosZ,
        float AngX, float AngY, float AngZ,
        float VelX, float VelY, float VelZ,
        string Weapon,
        string ProjectileClass,
        ushort ItemIndex);

    /// <summary>
    /// 插件配置数据模型。
    /// </summary>
    private sealed class PracLabConfig
    {
        /// <summary>是否启用 practice 模式总开关。</summary>
        public bool Enabled { get; set; } = true;

        /// <summary>默认语言代码（如 zh-CN、en）。</summary>
        public string DefaultLanguage { get; set; } = "zh-CN";
    }
}
