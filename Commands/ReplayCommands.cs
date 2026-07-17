using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.Json.Serialization;
using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Core.Translations;
using CounterStrikeSharp.API.Modules.Utils;

namespace PracLab;

/// <summary>
/// 回放系统命令实现：.record / .stoprecord / .replay / .stopreplay / .clearrecord / .clearrecordall / .currentrecord。
/// 通过 P/Invoke 调用 PracLabReplayEngine C++ Metamod 插件导出的 C API（PRL_* 系列）。
/// 二层架构：
///   Layer 1 = PracLab CSSharp（命令、UI、JSON IO）— 本文件
///   Layer 2 = PracLabReplayEngine C++ Metamod（引擎 hook、录制/回放核心）— replay-engine/
/// 跨语言 ABI 契约见 replay-engine/include/praclab_replay.h，修改字段顺序/类型须同步更新三层代码。
/// </summary>
public partial class PracLab
{
    // ==================== 常量 ====================

    /// <summary>
    /// PracLabReplayEngine 动态库名（不带扩展名，P/Invoke 运行时自动解析）。
    /// Windows: PracLabReplayEngine.dll；Linux: PracLabReplayEngine.so（无 lib 前缀）。
    /// 部署位置：csgo/addons/PracLabReplayEngine/bin/{win64|linuxsteamrt64}/
    /// </summary>
    private const string ReplayEngineLibrary = "PracLabReplayEngine";

    /// <summary>
    /// 录制 JSON 文件存储目录（相对于 csgo/ 目录）。
    /// 完整路径：csgo/cfg/PracLab/recordings/
    /// </summary>
    private const string RecordingsDirRelativePath = "cfg/PracLab/recordings";

    // ==================== 跨语言数据结构 ====================
    // 内存布局必须与 C++ 侧 src/recorder.h 中的 MovementSnapshot/ReplayTick/SubtickMove
    // 以及 include/praclab_replay.h 中的 PRL_* 结构完全一致（#pragma pack(4)）。
    // 修改字段顺序/类型会破坏 P/Invoke 兼容性。

    /// <summary>
    /// 一帧移动快照。对应 C++ 侧 PRL_MovementSnapshot。
    /// 在 ProcessMovement 前（pre）和后（post）各捕获一次。
    /// </summary>
    [StructLayout(LayoutKind.Sequential, Pack = 4)]
    private struct MovementSnapshot
    {
        public float OriginX, OriginY, OriginZ;        // 场景节点 m_vecAbsOrigin
        public float VelX, VelY, VelZ;                 // m_vecAbsVelocity
        public float Pitch, Yaw, Roll;                 // 视角
        public uint EntityFlags;                       // m_fFlags
        public byte MoveType;                          // m_MoveType
        [JsonIgnore]
        public byte Pad0, Pad1, Pad2;                  // 4 字节对齐填充（对应 C++ _pad[3]）
        public ulong Buttons;                          // m_nButtons states[0]
        public ulong Buttons1;                         // states[1]
        public ulong Buttons2;                         // states[2]
        public float DuckAmount;                       // m_flDuckAmount
        public float DuckSpeed;                        // m_flDuckSpeed
        public float LadderNormalX, LadderNormalY, LadderNormalZ;  // m_vecLadderNormal
        public byte Ducked, Ducking, DesiresDuck;      // 蹲下状态机
        public byte ActualMoveType;                    // m_nActualMoveType
    }

    /// <summary>
    /// 一个录制的服务器 tick。对应 C++ 侧 PRL_ReplayTick。
    /// pre/post 为 ProcessMovement 前/后快照；numSubtick 指示本 tick 关联的
    /// subtick 输入数量（对应并行 SubtickMove 缓冲区中的连续段）。
    /// </summary>
    [StructLayout(LayoutKind.Sequential, Pack = 4)]
    private struct ReplayTick
    {
        public MovementSnapshot Pre;     // ProcessMovement 前
        public MovementSnapshot Post;    // ProcessMovement 后
        public int WeaponDefIndex;       // 当前武器（-1 = 无）
        public uint NumSubtick;          // subtick 数量
    }

    /// <summary>
    /// 一个 subtick 输入步。对应 C++ 侧 PRL_SubtickMove。
    /// 对应 CSubtickMoveStep（protobuf），在 tick 内的 [0,1) 时间点触发。
    /// </summary>
    [StructLayout(LayoutKind.Sequential, Pack = 4)]
    private struct SubtickMove
    {
        public float When;              // tick 内时间点 [0,1)
        public uint Button;             // 按钮位（0 = 模拟量输入）
        public float Pressed;           // 按下/释放（1.0=按下 0.0=释放）
        public float AnalogForward;     // 前进模拟量
        public float AnalogLeft;        // 左移模拟量
        public float PitchDelta;        // 俯仰增量
        public float YawDelta;          // 偏航增量
    }

    /// <summary>
    /// 诊断计数器结构。对应 C++ 侧 PRL_DiagnosticCounters。
    /// 用于排查回放不移动问题：确认各 hook 注入点是否被调用、pawn 是否解析成功、
    /// 位置写入是否真正生效（读回验证）、bot AI 与 user command 是否被处理。
    /// </summary>
    [StructLayout(LayoutKind.Sequential, Pack = 4)]
    private struct PRL_DiagnosticCounters
    {
        public ulong ProcessMovementCalls;     // ProcessMovement hook 总调用次数
        public ulong FinishMoveCalls;           // FinishMove hook 总调用次数
        public ulong PhysicsSimulateCalls;      // PhysicsSimulate hook 总调用次数
        public ulong ReplayPreCalls;            // OnReplayPre 调用次数
        public ulong ReplayFinishMoveCalls;     // OnReplayFinishMove 调用次数
        public ulong ReplayCommitCalls;         // OnReplayCommit 调用次数
        public ulong ReplayPrePawnNull;         // OnReplayPre 中 ResolveReplayPawn 返回 null 次数
        public ulong ReplayCommitPawnNull;      // OnReplayCommit 中 ResolveReplayPawn 返回 null 次数
        public int PhysicsActive;               // PhysicsSimulate hook 是否激活（1=是 0=否）
        public int SubtickActive;               // PlayerRunCommand hook 是否激活
        public int LastProcessMovementSlot;     // 最后一次 ProcessMovement 的 slot
        public int LastPhysicsSimulateSlot;     // 最后一次 PhysicsSimulate 的 slot
        public int LastReplaySlot;              // 最后一次回放调用的 slot
        public int LastReplayCursor;            // 最后一次回放的 cursor

        // AI 层与 user command 层调用次数
        public ulong CCSBotUpdateCalls;         // CCSBot::Update hook 调用次数
        public ulong CCSBotUpkeepCalls;         // CCSBot::Upkeep hook 调用次数
        public ulong PlayerRunCommandCalls;     // PlayerRunCommand hook 调用次数

        // UpdateLookAngles / SetEyeAngles 调用次数
        // 用于排查回放期间引擎是否仍在尝试覆盖 bot 视角
        public ulong CCSBotUpdateLookAnglesCalls; // CCSBot::UpdateLookAngles hook 调用次数
        public ulong SetEyeAnglesCalls;           // CCSPlayerPawn::SetEyeAngles hook 调用次数

        // 位置读回验证：OnReplayCommit 写入场景节点 origin 后立即读回
        public float LastWrittenOriginX;        // 最后一次写入的 origin X
        public float LastWrittenOriginY;        // 最后一次写入的 origin Y
        public float LastWrittenOriginZ;        // 最后一次写入的 origin Z
        public float LastReadBackOriginX;       // 读回的 origin X
        public float LastReadBackOriginY;       // 读回的 origin Y
        public float LastReadBackOriginZ;       // 读回的 origin Z
        public uint LastPawnIdentity;           // pawn->identity（每次 commit 刷新）
        public int LastPawnMoveType;            // pawn->m_MoveType（验证写入）
    }

    // ==================== P/Invoke 声明 ====================
    // 所有函数返回 int：1 = 成功，0 = 失败。
    // 使用 int 而非 bool，避免 C99 bool 跨语言 ABI 不一致问题（见 praclab_replay.h 注释）。
    // CallingConvention.Cdecl 对应 extern "C" 的默认调用约定。

    [DllImport(ReplayEngineLibrary, CallingConvention = CallingConvention.Cdecl)]
    private static extern int PRL_StartRecord(int slot);

    [DllImport(ReplayEngineLibrary, CallingConvention = CallingConvention.Cdecl)]
    private static extern int PRL_StopRecord(int slot);

    [DllImport(ReplayEngineLibrary, CallingConvention = CallingConvention.Cdecl)]
    private static extern int PRL_GetRecordedTickCount(int slot);

    [DllImport(ReplayEngineLibrary, CallingConvention = CallingConvention.Cdecl)]
    private static extern int PRL_GetRecordedMotion(int slot,
        IntPtr outTicks, ref int outTickCount,
        IntPtr outSubs, ref int outSubCount);

    [DllImport(ReplayEngineLibrary, CallingConvention = CallingConvention.Cdecl)]
    private static extern int PRL_LoadReplay(int slot,
        IntPtr ticks, int tickCount,
        IntPtr subs, int subCount);

    [DllImport(ReplayEngineLibrary, CallingConvention = CallingConvention.Cdecl)]
    private static extern int PRL_SetReplayPawn(int slot, IntPtr pawn);

    [DllImport(ReplayEngineLibrary, CallingConvention = CallingConvention.Cdecl)]
    private static extern int PRL_StartReplay(int slot, int loop);

    [DllImport(ReplayEngineLibrary, CallingConvention = CallingConvention.Cdecl)]
    private static extern int PRL_StopReplay(int slot);

    [DllImport(ReplayEngineLibrary, CallingConvention = CallingConvention.Cdecl)]
    private static extern int PRL_IsReplaying(int slot);

    [DllImport(ReplayEngineLibrary, CallingConvention = CallingConvention.Cdecl)]
    private static extern int PRL_FreezeSlot(int slot);

    [DllImport(ReplayEngineLibrary, CallingConvention = CallingConvention.Cdecl)]
    private static extern int PRL_UnfreezeSlot(int slot);

    [DllImport(ReplayEngineLibrary, CallingConvention = CallingConvention.Cdecl)]
    private static extern int PRL_GetCurrentReplayWeaponDef(int slot);

    [DllImport(ReplayEngineLibrary, CallingConvention = CallingConvention.Cdecl)]
    private static extern int PRL_GetBotActiveWeaponDef(int slot);

    [DllImport(ReplayEngineLibrary, CallingConvention = CallingConvention.Cdecl)]
    private static extern int PRL_GetDiagnosticCounters(ref PRL_DiagnosticCounters outCounters);

    // ==================== 录制/回放状态 ====================

    /// <summary>
    /// PracLabReplayEngine 动态库是否已加载且可用。
    /// false 时所有 replay 命令向玩家提示 replay.engine_not_found。
    /// 在 Load 中通过 try-catch DllNotFoundException 一次性探测设置。
    /// </summary>
    private bool _replayEngineAvailable;

    /// <summary>
    /// 录制文件存储目录的绝对路径（在 Load 时计算并创建）。
    /// 完整路径：csgo/cfg/PracLab/recordings/
    /// </summary>
    private string _recordingsDirPath = string.Empty;

    /// <summary>
    /// 已保存的录制条目列表。每个条目对应一个 JSON 文件，可能关联到一个正在回放的 Bot 槽位。
    /// </summary>
    private readonly List<RecordingEntry> _recordings = new();

    /// <summary>
    /// 录制 ID 自增计数器（每次新建录制 +1）。
    /// </summary>
    private int _nextRecordingId = 1;

    /// <summary>
    /// 当前正在录制的玩家槽位（-1 = 无录制中）。
    /// 同一时刻仅允许一个玩家录制，避免 C++ 侧多 slot 并发录制干扰。
    /// </summary>
    private int _recordingPlayerSlot = -1;

    /// <summary>
    /// 待开始录制的玩家槽位（-1 = 无）。
    /// .record 命令后设置，等待玩家移动或按 F 键后才真正开始录制。
    /// </summary>
    private int _pendingRecordSlot = -1;

    /// <summary>
    /// 录制开始时扫描的玩家武器名列表。
    /// 回放时复制给 bot，确保 bot 有相同的武器装备来复刻动作。
    /// </summary>
    private List<string> _recordingPlayerWeapons = new();

    /// <summary>
    /// F 键停止录制检测的 OnTick 监听器是否已注册。
    /// RegisterListener 一旦注册无法注销，用此标志守卫避免重复注册。
    /// </summary>
    private bool _replayTickRegistered;

    /// <summary>
    /// 回放诊断打印的 tick 计数器。
    /// 每 32 tick（约 0.5 秒）打印一次诊断信息，避免控制台刷屏。
    /// </summary>
    private int _replayDiagTickCounter;

    /// <summary>
    /// F 键冷却字典。键为 UserId，值为上次触发时间。
    /// 防止按住 F 键时每 tick 都触发停止录制。
    /// </summary>
    private readonly Dictionary<int, DateTime> _recordFKeyCooldown = new();

    /// <summary>
    /// F 键冷却秒数。
    /// </summary>
    private const double RecordFKeyCooldownSeconds = 1.0;

    /// <summary>
    /// JSON 序列化选项。IncludeFields = true 以序列化结构体字段（非属性）。
    /// </summary>
    private static readonly JsonSerializerOptions ReplayJsonOpts = new()
    {
        IncludeFields = true,
        WriteIndented = false,
    };

    // ==================== 嵌套类型 ====================

    /// <summary>
    /// 录制状态枚举。
    /// </summary>
    private enum RecordingStatus
    {
        /// <summary>空闲（已保存，未回放）。</summary>
        Idle = 0,

        /// <summary>录制中。</summary>
        Recording = 1,

        /// <summary>回放中。</summary>
        Playing = 2,
    }

    /// <summary>
    /// 录制数据条目。每个条目对应一个 JSON 文件，可能关联到一个正在回放的 Bot 槽位。
    /// </summary>
    private sealed class RecordingEntry
    {
        /// <summary>唯一标识（自增，用于 .clearrecord &lt;Id&gt;）。</summary>
        public int Id { get; set; }

        /// <summary>录制名称（取自玩家名 + 时间戳）。</summary>
        public string Name { get; set; } = string.Empty;

        /// <summary>JSON 文件绝对路径。</summary>
        public string FilePath { get; set; } = string.Empty;

        /// <summary>当前状态：Idle / Recording / Playing。</summary>
        public RecordingStatus Status { get; set; } = RecordingStatus.Idle;

        /// <summary>若正在回放，关联的 Bot 槽位；否则 -1。</summary>
        public int BotSlot { get; set; } = -1;

        /// <summary>回放发起者的 UserId（用于回放结束时通知）。</summary>
        public int InitiatorUserId { get; set; } = -1;
    }

    /// <summary>
    /// 录制数据 JSON 文件格式。
    /// 通过 System.Text.Json 序列化/反序列化，IncludeFields = true 以支持结构体字段。
    /// </summary>
    private sealed class MotionRecordingJson
    {
        /// <summary>服务器 tickrate（录制时获取，用于参考）。</summary>
        public int Tickrate { get; set; }

        /// <summary>录制数据 tick 数组（含 pre/post 快照和武器信息）。</summary>
        public List<ReplayTick> Ticks { get; set; } = new();

        /// <summary>并行 subtick 输入数组（按 tick 分段，由 ReplayTick.NumSubtick 指示）。</summary>
        public List<SubtickMove> Subs { get; set; } = new();

        /// <summary>录制时玩家持有的武器名列表（如 weapon_ak47），回放时给 bot 复制相同装备。</summary>
        public List<string> PlayerWeapons { get; set; } = new();
    }

    // ==================== 初始化辅助方法 ====================

    /// <summary>
    /// 探测 PracLabReplayEngine 动态库是否已加载且可用。
    /// 通过调用 PRL_GetRecordedTickCount(-1)（无副作用，返回 &lt;0 表示 slot 非法）进行探测。
    /// 成功调用则设置 _replayEngineAvailable = true；DllNotFoundException 则设置为 false。
    /// 其他异常也视为不可用，记录日志但不阻塞插件加载。
    /// </summary>
    /// <remarks>
    /// 加载顺序问题：Metamod 按字母序加载插件（CounterStrikeSharp &lt; PracLabReplayEngine），
    /// 导致 CSSharp Load 执行此探测时 PracLabReplayEngine.dll 尚未进入进程地址空间。
    /// 修复：先用 NativeLibrary.TryLoad 以完整路径预加载 DLL，
    /// 一旦进入进程地址空间，后续 P/Invoke 通过 basename 即可匹配找到。
    /// </remarks>
    private void DetectReplayEngineAvailability()
    {
        Server.PrintToConsole("[PracLab] DetectReplayEngineAvailability: executing...");

        try
        {
            // 预加载 DLL：用完整路径将其加载到进程地址空间
            // 不依赖 Metamod 加载顺序，确保 P/Invoke 后续能通过 basename 找到
            PreloadReplayEngineDll();

            // 调用无副作用的查询函数探测 DLL 是否加载
            // slot=-1 是非法值，C++ 侧应返回 <0，但不抛异常
            var result = PRL_GetRecordedTickCount(-1);
            _replayEngineAvailable = true;
            Server.PrintToConsole($"[PracLab] {DateTime.Now:HH:mm:ss} ReplayEngine available (probe returned {result})");
        }
        catch (DllNotFoundException ex)
        {
            _replayEngineAvailable = false;
            Server.PrintToConsole($"[PracLab] {DateTime.Now:HH:mm:ss} Warning ReplayEngine not loaded - {ex.Message}");
        }
        catch (Exception ex)
        {
            _replayEngineAvailable = false;
            Server.PrintToConsole($"[PracLab] {DateTime.Now:HH:mm:ss} Warning ReplayEngine probe failed - {ex.Message}");
        }
    }

    /// <summary>
    /// 以完整路径预加载 PracLabReplayEngine 动态库到进程地址空间。
    /// 路径计算：Server.GameDirectory + addons/PracLabReplayEngine/bin/{platform}/。
    /// 加载后 P/Invoke 的 [DllImport("PracLabReplayEngine")] 通过 basename 匹配即可找到。
    /// 失败不抛异常，仅打印警告（后续 P/Invoke 探测会给出最终结论）。
    /// </summary>
    private void PreloadReplayEngineDll()
    {
        try
        {
            // 平台子目录与文件名
            // Windows: bin/win64/PracLabReplayEngine.dll
            // Linux:   bin/linuxsteamrt64/PracLabReplayEngine.so
            string platformDir = OperatingSystem.IsWindows() ? "win64" : "linuxsteamrt64";
            string dllName = OperatingSystem.IsWindows()
                ? "PracLabReplayEngine.dll"
                : "PracLabReplayEngine.so";

            // 诊断：打印 GameDirectory 实际值，便于排查路径问题
            Server.PrintToConsole($"[PracLab] {DateTime.Now:HH:mm:ss} Preload GameDirectory={Server.GameDirectory}");

            string dllPath = Path.Combine(
                Server.GameDirectory,
                "addons", "PracLabReplayEngine", "bin", platformDir, dllName);

            if (!File.Exists(dllPath))
            {
                Server.PrintToConsole($"[PracLab] {DateTime.Now:HH:mm:ss} Warning ReplayEngine DLL not found: {dllPath}");
                return;
            }

            // NativeLibrary.TryLoad 内部调用 LoadLibraryEx(LOAD_WITH_ALTERED_SEARCH_PATH)，
            // 将 DLL 及其依赖从指定目录加载到进程地址空间。
            // 返回的 handle 不需要保持引用：一旦加载，DLL 在进程生命周期内保持映射。
            if (NativeLibrary.TryLoad(dllPath, out _))
            {
                Server.PrintToConsole($"[PracLab] {DateTime.Now:HH:mm:ss} ReplayEngine preloaded: {dllPath}");
            }
            else
            {
                // 打印 Win32 错误码，便于诊断加载失败原因（如依赖缺失、位数不匹配等）
                int err = System.Runtime.InteropServices.Marshal.GetLastWin32Error();
                Server.PrintToConsole($"[PracLab] {DateTime.Now:HH:mm:ss} Warning NativeLibrary.TryLoad failed: {dllPath} win32err={err}");
            }
        }
        catch (Exception ex)
        {
            Server.PrintToConsole($"[PracLab] {DateTime.Now:HH:mm:ss} Warning PreloadReplayEngineDll failed - {ex.Message}");
        }
    }

    /// <summary>
    /// 确保录制文件存储目录存在。在 Load 时调用。
    /// 路径计算：Server.GameDirectory + RecordingsDirRelativePath。
    /// </summary>
    private void EnsureRecordingsDir()
    {
        Server.PrintToConsole("[PracLab] EnsureRecordingsDir: executing...");

        try
        {
            _recordingsDirPath = Path.Combine(Server.GameDirectory, RecordingsDirRelativePath);
            Directory.CreateDirectory(_recordingsDirPath);
            Server.PrintToConsole($"[PracLab] {DateTime.Now:HH:mm:ss} Replay recordings dir: {_recordingsDirPath}");
        }
        catch (Exception ex)
        {
            Server.PrintToConsole($"[PracLab] {DateTime.Now:HH:mm:ss} Error creating recordings dir failed - {ex.Message}");
            _recordingsDirPath = string.Empty;
        }
    }

    /// <summary>
    /// 校验回放引擎是否可用。不可用时向玩家提示并返回 false。
    /// 所有 replay 命令处理器在执行前必须调用此方法。
    /// </summary>
    /// <param name="player">调用者玩家。</param>
    /// <returns>true 表示可用，false 表示不可用（已向玩家打印原因）。</returns>
    private bool EnsureReplayEngine(CCSPlayerController player)
    {
        if (_replayEngineAvailable)
            return true;

        // 延迟重试：Metamod 按字母序加载（CounterStrikeSharp < PracLabReplayEngine），
        // CSSharp Load 时 PracLabReplayEngine.dll 可能尚未进入进程地址空间。
        // 首次命令时 Metamod 已完成所有插件加载，重试 P/Invoke 即可找到。
        try
        {
            var result = PRL_GetRecordedTickCount(-1);
            _replayEngineAvailable = true;
            Server.PrintToConsole($"[PracLab] {DateTime.Now:HH:mm:ss} ReplayEngine lazy-loaded (probe returned {result})");
            return true;
        }
        catch (DllNotFoundException ex)
        {
            _replayEngineAvailable = false;
            Server.PrintToConsole($"[PracLab] {DateTime.Now:HH:mm:ss} Warning ReplayEngine still not loaded - {ex.Message}");
        }
        catch (Exception ex)
        {
            _replayEngineAvailable = false;
            Server.PrintToConsole($"[PracLab] {DateTime.Now:HH:mm:ss} Warning ReplayEngine lazy probe failed - {ex.Message}");
        }

        player.PrintToChat(Localizer.ForPlayer(player, "replay.engine_not_found"));
        return false;
    }

    // ==================== .record 命令 ====================

    /// <summary>
    /// .record — 开始录制当前玩家的移动轨迹和动作。
    /// 流程：设置 pending 状态 → 等待玩家移动或按 F 键 → PRL_StartRecord(slot)。
    /// 玩家移动或按 F 键后由 CheckPendingRecordStart 在 OnTick 中触发真正录制。
    /// 同一时刻仅允许一个玩家录制，避免 C++ 侧多 slot 并发录制干扰。
    /// </summary>
    /// <param name="player">调用者玩家。</param>
    /// <param name="args">命令参数（未使用）。</param>
    private void HandleRecord(CCSPlayerController player, string args)
    {
        Server.PrintToConsole("[PracLab] HandleRecord: executing...");

        if (!EnsureReplayEngine(player)) return;

        // 检查是否已有录制进行中或等待开始
        if (_recordingPlayerSlot >= 0 || _pendingRecordSlot >= 0)
        {
            player.PrintToChat(Localizer.ForPlayer(player, "record.already_recording"));
            Server.PrintToConsole($"[PracLab] {DateTime.Now:HH:mm:ss} Warning Record already in progress slot={_recordingPlayerSlot} pending={_pendingRecordSlot}");
            return;
        }

        // 校验玩家 pawn 有效
        var pawn = player.PlayerPawn.Value;
        if (pawn == null || !pawn.IsValid)
        {
            player.PrintToChat(Localizer.ForPlayer(player, "spawn.no_pawn"));
            return;
        }

        // 设置 pending 状态，等待玩家移动或按 F 键后开始录制
        _pendingRecordSlot = player.Slot;
        player.PrintToChat(Localizer.ForPlayer(player, "record.waiting_to_start"));
        Server.PrintToConsole($"[PracLab] {DateTime.Now:HH:mm:ss} Record pending for player {player.PlayerName} slot={player.Slot}, waiting for movement or F key");

        // 注册 OnTick 监听器（仅注册一次），检测移动/F键启动录制和 F键停止录制
        RegisterReplayTickListener();
    }

    /// <summary>
    /// 检测待录制玩家是否移动或按下 F 键，触发真正录制开始。
    /// 由 OnTick 调用，当 _pendingRecordSlot >= 0 时每 tick 检查。
    /// 触发条件：玩家按下移动键（W/A/S/D）或 F 键（Inspect）。
    /// 若玩家断开连接则取消 pending 状态。
    /// </summary>
    private void CheckPendingRecordStart()
    {
        var slot = _pendingRecordSlot;
        CCSPlayerController? pendingPlayer = null;

        foreach (var p in Utilities.GetPlayers())
        {
            if (p == null || !p.IsValid || p.IsBot || p.IsHLTV) continue;
            if (p.Slot != slot) continue;
            pendingPlayer = p;
            break;
        }

        // 玩家断开连接：取消 pending
        if (pendingPlayer == null || pendingPlayer.UserId == null)
        {
            Server.PrintToConsole($"[PracLab] {DateTime.Now:HH:mm:ss} Warning Pending record player slot={slot} disconnected, cancelling");
            _pendingRecordSlot = -1;
            return;
        }

        // 检测移动键（W/A/S/D）或 F 键（Inspect）
        var buttons = pendingPlayer.Buttons;
        bool hasMovement = (buttons & (PlayerButtons.Forward | PlayerButtons.Back | PlayerButtons.Moveleft | PlayerButtons.Moveright)) != 0;
        bool hasInspect = (buttons & PlayerButtons.Inspect) != 0;

        if (!hasMovement && !hasInspect) return;

        // 若通过 F 键触发，设置冷却防止开始录制后立刻被 F 键停止
        if (hasInspect && pendingPlayer.UserId.HasValue)
        {
            _recordFKeyCooldown[pendingPlayer.UserId.Value] = DateTime.Now;
        }

        // 清除 pending 状态，开始真正录制
        _pendingRecordSlot = -1;
        var playerSlot = slot;

        var result = PRL_StartRecord(playerSlot);
        if (result == 1)
        {
            _recordingPlayerSlot = playerSlot;

            // 扫描玩家当前武器装备，保存到 _recordingPlayerWeapons
            _recordingPlayerWeapons = ScanPlayerWeapons(pendingPlayer);

            pendingPlayer.PrintToChat(Localizer.ForPlayer(pendingPlayer, "record.started"));
            Server.PrintToConsole($"[PracLab] {DateTime.Now:HH:mm:ss} Record started for player {pendingPlayer.PlayerName} slot={playerSlot} weapons=[{string.Join(", ", _recordingPlayerWeapons)}]");
        }
        else
        {
            pendingPlayer.PrintToChat(Localizer.ForPlayer(pendingPlayer, "record.start_failed"));
            Server.PrintToConsole($"[PracLab] {DateTime.Now:HH:mm:ss} Error PRL_StartRecord failed slot={playerSlot} result={result}");
        }
    }

    /// <summary>
    /// 注册 OnTick 监听器：
    /// 1. 检测 F 键（Inspect 键）按下以停止录制
    /// 2. 检测回放结束（PRL_IsReplaying 返回 0）
    /// 监听器仅注册一次，通过状态字段短路返回避免无效运算。
    /// </summary>
    private void RegisterReplayTickListener()
    {
        if (_replayTickRegistered) return;

        RegisterListener<Listeners.OnTick>(() =>
        {
            try
            {
                // ---- 0. 待录制：检测移动/F键启动录制 ----
                if (_pendingRecordSlot >= 0)
                {
                    CheckPendingRecordStart();
                }

                // ---- 1. 录制中：检测 F 键和玩家断开 ----
                if (_recordingPlayerSlot >= 0)
                {
                    CheckRecordingFKey();
                }

                // ---- 2. 回放中：检测回放结束 ----
                if (_replayEngineAvailable && _recordings.Count > 0)
                {
                    CheckReplayEnd();

                    // 仅在有正在回放的条目时打印诊断（避免冻结状态下持续输出日志）
                    bool hasPlaying = false;
                    foreach (var r in _recordings)
                    {
                        if (r.Status == RecordingStatus.Playing) { hasPlaying = true; break; }
                    }

                    if (hasPlaying)
                    {
                        _replayDiagTickCounter++;
                        if (_replayDiagTickCounter >= 32)
                        {
                            _replayDiagTickCounter = 0;
                            PrintDiagnostics("during replay");
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Server.PrintToConsole($"[PracLab] {DateTime.Now:HH:mm:ss} Error Replay OnTick failed - {ex.Message}");
            }
        });

        _replayTickRegistered = true;
        Server.PrintToConsole($"[PracLab] {DateTime.Now:HH:mm:ss} Replay OnTick listener registered");
    }

    /// <summary>
    /// 检测录制玩家是否按下 F 键或已断开连接。
    /// </summary>
    private void CheckRecordingFKey()
    {
        var slot = _recordingPlayerSlot;
        CCSPlayerController? recordingPlayer = null;

        // 查找当前录制玩家
        foreach (var p in Utilities.GetPlayers())
        {
            if (p == null || !p.IsValid || p.IsBot || p.IsHLTV) continue;
            if (p.Slot != slot) continue;
            recordingPlayer = p;
            break;
        }

        // 玩家已断开连接：自动停止录制并保存
        if (recordingPlayer == null || recordingPlayer.UserId == null)
        {
            Server.PrintToConsole($"[PracLab] {DateTime.Now:HH:mm:ss} Warning Recording player slot={slot} disconnected, auto-stopping");
            StopAndSaveRecording(slot, player: null);
            return;
        }

        // 检测 F 键（Inspect 按钮）
        if ((recordingPlayer.Buttons & PlayerButtons.Inspect) == 0) return;

        int userId = recordingPlayer.UserId.Value;

        // 冷却检查（1 秒）
        if (_recordFKeyCooldown.TryGetValue(userId, out var lastTime) &&
            (DateTime.Now - lastTime).TotalSeconds < RecordFKeyCooldownSeconds)
            return;

        // 更新冷却时间
        _recordFKeyCooldown[userId] = DateTime.Now;

        // 停止录制并保存
        StopAndSaveRecording(slot, recordingPlayer);
    }

    /// <summary>
    /// 检测所有回放中的录制是否已结束。
    /// PRL_IsReplaying 返回 0 时，更新状态为 Idle，解锁 Bot AI，通知发起者。
    /// </summary>
    private void CheckReplayEnd()
    {
        for (int i = _recordings.Count - 1; i >= 0; i--)
        {
            var entry = _recordings[i];
            if (entry.Status != RecordingStatus.Playing) continue;
            if (entry.BotSlot < 0) continue;

            // 查询 C++ 回放状态
            if (PRL_IsReplaying(entry.BotSlot) != 0) continue;

            // 回放已结束
            entry.Status = RecordingStatus.Idle;
            var botSlot = entry.BotSlot;
            // 保留 BotSlot（不设为 -1），以便 .stopreplay 能找到冻结的 bot 并踢出
            // BotSlot 在 StopReplayEntry 中踢出 bot 后才设为 -1

            // 冻结 bot AI：回放结束后 bot 应保持静止，不激活 AI。
            // CCSBot::Update hook 检测到冻结标志后继续设置 kBot_AiTickedFlag=1，
            // 阻止引擎运行 AI 决策，bot 停留在回放结束的最终位置。
            // 用户可通过 .stopreplay 命令踢出 bot（会先解冻再踢出）。
            try
            {
                PRL_FreezeSlot(botSlot);
                Server.PrintToConsole($"[PracLab] {DateTime.Now:HH:mm:ss} Replay: frozen bot slot={botSlot} (AI disabled, bot stays still)");
            }
            catch (Exception ex)
            {
                Server.PrintToConsole($"[PracLab] {DateTime.Now:HH:mm:ss} Warning PRL_FreezeSlot failed slot={botSlot} - {ex.Message}");
            }

            // 通知发起者
            if (entry.InitiatorUserId >= 0)
            {
                foreach (var p in Utilities.GetPlayers())
                {
                    if (p == null || !p.IsValid || p.IsBot) continue;
                    if (p.UserId == entry.InitiatorUserId)
                    {
                        p.PrintToChat(Localizer.ForPlayer(p, "replay.ended", entry.Id));
                        break;
                    }
                }
            }

            Server.PrintToConsole($"[PracLab] {DateTime.Now:HH:mm:ss} Replay ended: id={entry.Id} name={entry.Name}");

            // 打印诊断计数器，用于排查回放不移动问题
            PrintDiagnostics("after replay ended");
        }
    }

    /// <summary>
    /// 打印回放引擎诊断计数器到服务器控制台。
    /// 用于排查回放 bot 不移动的问题：确认各 hook 是否被调用、pawn 是否解析成功。
    /// </summary>
    private void PrintDiagnostics(string label)
    {
        try
        {
            var counters = new PRL_DiagnosticCounters();
            if (PRL_GetDiagnosticCounters(ref counters) != 1)
            {
                Server.PrintToConsole($"[PracLab] {DateTime.Now:HH:mm:ss} Diagnostics({label}): failed to get counters");
                return;
            }

            Server.PrintToConsole($"[PracLab] {DateTime.Now:HH:mm:ss} === Diagnostics({label}) ===");
            Server.PrintToConsole($"[PracLab]   physicsActive={counters.PhysicsActive} subtickActive={counters.SubtickActive}");
            Server.PrintToConsole($"[PracLab]   ProcessMovement: calls={counters.ProcessMovementCalls} lastSlot={counters.LastProcessMovementSlot}");
            Server.PrintToConsole($"[PracLab]   FinishMove:      calls={counters.FinishMoveCalls}");
            Server.PrintToConsole($"[PracLab]   PhysicsSimulate: calls={counters.PhysicsSimulateCalls} lastSlot={counters.LastPhysicsSimulateSlot}");
            Server.PrintToConsole($"[PracLab]   ReplayPre:       calls={counters.ReplayPreCalls} pawnNull={counters.ReplayPrePawnNull}");
            Server.PrintToConsole($"[PracLab]   ReplayFinishMove:calls={counters.ReplayFinishMoveCalls}");
            Server.PrintToConsole($"[PracLab]   ReplayCommit:    calls={counters.ReplayCommitCalls} pawnNull={counters.ReplayCommitPawnNull}");
            Server.PrintToConsole($"[PracLab]   lastReplaySlot={counters.LastReplaySlot} lastReplayCursor={counters.LastReplayCursor}");

            // AI 层与 user command 层调用次数：用于定位 ProcessMovement 为何只为 bot 调用 1 次
            // - CCSBot::Update calls=0  → AI 未运行（bot 可能未正确注册为 CCSBot）
            // - CCSBot::Update calls>0 但 PlayerRunCommand calls=0 → AI 运行但未生成 user command
            // - 两者都 >0 但 ProcessMovement 不为 replay slot 调用 → 引擎可能跳过了 replay bot 的物理模拟
            Server.PrintToConsole($"[PracLab]   CCSBot::Update:  calls={counters.CCSBotUpdateCalls}");
            Server.PrintToConsole($"[PracLab]   CCSBot::Upkeep:  calls={counters.CCSBotUpkeepCalls}");
            Server.PrintToConsole($"[PracLab]   PlayerRunCommand:calls={counters.PlayerRunCommandCalls}");

            // 视角 hook 调用次数：用于排查回放期间引擎是否仍在尝试覆盖 bot 视角
            // - UpdateLookAngles calls>0：引擎仍在对 bot 进行视角平滑（应被 hook 跳过）
            // - SetEyeAngles calls>0：引擎仍尝试写入视角（应被 hook 跳过，改由 ApplyReplayEyeAngles 写入）
            Server.PrintToConsole($"[PracLab]   CCSBot::UpdateLookAngles: calls={counters.CCSBotUpdateLookAnglesCalls}");
            Server.PrintToConsole($"[PracLab]   CCSPlayerPawn::SetEyeAngles: calls={counters.SetEyeAnglesCalls}");

            // 位置读回验证：对比 OnReplayCommit 写入值与读回值
            // - written == readBack：写入成功，位置应已定格
            // - written != readBack：写入失败（SEH 拦截 / 地址无效）
            // - readBack 在多 tick 间不变：引擎未使用写入的位置，或 bot 已停止物理模拟
            Server.PrintToConsole($"[PracLab]   WrittenOrigin: ({counters.LastWrittenOriginX:F2}, {counters.LastWrittenOriginY:F2}, {counters.LastWrittenOriginZ:F2})");
            Server.PrintToConsole($"[PracLab]   ReadBackOrigin:({counters.LastReadBackOriginX:F2}, {counters.LastReadBackOriginY:F2}, {counters.LastReadBackOriginZ:F2})");
            Server.PrintToConsole($"[PracLab]   pawnIdentity={counters.LastPawnIdentity} pawnMoveType={counters.LastPawnMoveType}");
        }
        catch (Exception ex)
        {
            Server.PrintToConsole($"[PracLab] {DateTime.Now:HH:mm:ss} Error PrintDiagnostics({label}) - {ex.Message}");
        }
    }

    // ==================== .stoprecord 命令 ====================

    /// <summary>
    /// .stoprecord — 停止录制并保存到 JSON 文件。
    /// 流程：PRL_StopRecord → PRL_GetRecordedMotion → 序列化 JSON → 创建 RecordingEntry。
    /// </summary>
    /// <param name="player">调用者玩家。</param>
    /// <param name="args">命令参数（未使用）。</param>
    private void HandleStopRecord(CCSPlayerController player, string args)
    {
        Server.PrintToConsole("[PracLab] HandleStopRecord: executing...");

        if (!EnsureReplayEngine(player)) return;

        // 处理 pending 状态：取消等待开始
        if (_pendingRecordSlot >= 0)
        {
            if (player.Slot != _pendingRecordSlot)
            {
                player.PrintToChat(Localizer.ForPlayer(player, "record.not_recording"));
                return;
            }
            _pendingRecordSlot = -1;
            player.PrintToChat(Localizer.ForPlayer(player, "record.cancelled"));
            Server.PrintToConsole($"[PracLab] {DateTime.Now:HH:mm:ss} Record pending cancelled by player {player.PlayerName} slot={player.Slot}");
            return;
        }

        if (_recordingPlayerSlot < 0)
        {
            player.PrintToChat(Localizer.ForPlayer(player, "record.not_recording"));
            return;
        }

        // 仅允许录制者本人停止
        if (player.Slot != _recordingPlayerSlot)
        {
            player.PrintToChat(Localizer.ForPlayer(player, "record.not_recording"));
            return;
        }

        StopAndSaveRecording(_recordingPlayerSlot, player);
    }

    /// <summary>
    /// 扫描玩家当前持有的所有武器，返回武器 designer name 列表。
    /// 回放时将这些武器名复制给 bot，确保 bot 有相同装备来复刻动作。
    /// 参考 CS2-Bot-Controller 的 WeaponLocker：通过 WeaponServices.MyWeapons 遍历武器槽。
    /// </summary>
    /// <param name="player">要扫描的玩家。</param>
    /// <returns>武器名列表（如 weapon_ak47, weapon_knife），空列表表示无武器或扫描失败。</returns>
    private List<string> ScanPlayerWeapons(CCSPlayerController player)
    {
        var weapons = new List<string>();
        var pawn = player.PlayerPawn.Value;
        if (pawn == null || !pawn.IsValid)
            return weapons;

        var weaponServices = pawn.WeaponServices;
        if (weaponServices == null)
            return weapons;

        var myWeapons = weaponServices.MyWeapons;
        if (myWeapons == null)
            return weapons;

        foreach (var handle in myWeapons)
        {
            var weapon = handle.Value;
            if (weapon == null || !weapon.IsValid)
                continue;

            var name = weapon.DesignerName;
            if (string.IsNullOrEmpty(name))
                continue;

            // 只记录 weapon_ 前缀的武器，排除 knife 以避免重复（bot 自带 knife）
            if (name.StartsWith("weapon_", StringComparison.Ordinal))
                weapons.Add(name);
        }

        Server.PrintToConsole($"[PracLab] {DateTime.Now:HH:mm:ss} Scanned player weapons: [{string.Join(", ", weapons)}]");
        return weapons;
    }

    /// <summary>
    /// 给 bot 复制指定的武器装备。
    /// 先移除 bot 所有武器，再按录制时的武器列表逐个 GiveNamedItem。
    /// 参考 CS2-Bot-Controller：回放前确保 bot 有与录制玩家相同的武器。
    /// </summary>
    /// <param name="botPlayer">目标 bot 玩家控制器。</param>
    /// <param name="weapons">武器名列表（如 weapon_ak47）。</param>
    private void GiveBotWeapons(CCSPlayerController botPlayer, List<string> weapons)
    {
        if (botPlayer == null || !botPlayer.IsValid || weapons.Count == 0)
            return;

        try
        {
            // 移除 bot 所有武器（包括默认 knife 和 pistol）
            botPlayer.RemoveWeapons();

            // 逐个给予录制时的武器
            foreach (var weaponName in weapons)
            {
                botPlayer.GiveNamedItem(weaponName);
            }

            Server.PrintToConsole($"[PracLab] {DateTime.Now:HH:mm:ss} Bot weapons given: bot={botPlayer.PlayerName} slot={botPlayer.Slot} weapons=[{string.Join(", ", weapons)}]");
        }
        catch (Exception ex)
        {
            Server.PrintToConsole($"[PracLab] {DateTime.Now:HH:mm:ss} Warning GiveBotWeapons failed bot={botPlayer.PlayerName} - {ex.Message}");
        }
    }

    /// <summary>
    /// 停止录制并保存到 JSON 文件的核心逻辑。
    /// 由 HandleStopRecord 和 F 键检测/玩家断开自动调用。
    /// </summary>
    /// <param name="slot">录制的玩家槽位。</param>
    /// <param name="player">调用者玩家（断开连接时为 null，仅保存不提示）。</param>
    private void StopAndSaveRecording(int slot, CCSPlayerController? player)
    {
        Server.PrintToConsole($"[PracLab] StopAndSaveRecording: executing... slot={slot}");

        try
        {
            // 停止录制（冻结缓冲区）
            var stopResult = PRL_StopRecord(slot);
            if (stopResult != 1)
            {
                if (player != null)
                    player.PrintToChat(Localizer.ForPlayer(player, "record.stop_failed"));
                Server.PrintToConsole($"[PracLab] {DateTime.Now:HH:mm:ss} Error PRL_StopRecord failed slot={slot} result={stopResult}");
                _recordingPlayerSlot = -1;
                return;
            }

            // 获取已录制的 tick 数量
            int tickCount = PRL_GetRecordedTickCount(slot);
            if (tickCount <= 0)
            {
                if (player != null)
                    player.PrintToChat(Localizer.ForPlayer(player, "record.no_data"));
                Server.PrintToConsole($"[PracLab] {DateTime.Now:HH:mm:ss} Warning Recording slot={slot} has no ticks");
                _recordingPlayerSlot = -1;
                return;
            }

            // 分配非托管缓冲区
            int tickSize = Marshal.SizeOf<ReplayTick>();
            int subSize = Marshal.SizeOf<SubtickMove>();
            int subCapacity = tickCount * 36; // 每 tick 最大 36 个 subtick

            IntPtr tickBuf = Marshal.AllocHGlobal(tickCount * tickSize);
            IntPtr subBuf = Marshal.AllocHGlobal(subCapacity * subSize);

            try
            {
                int actualTickCount = tickCount;
                int actualSubCount = subCapacity;

                // 从 C++ 获取录制数据
                var motionResult = PRL_GetRecordedMotion(slot, tickBuf, ref actualTickCount,
                                                         subBuf, ref actualSubCount);
                if (motionResult != 1)
                {
                    if (player != null)
                        player.PrintToChat(Localizer.ForPlayer(player, "record.fetch_failed"));
                    Server.PrintToConsole($"[PracLab] {DateTime.Now:HH:mm:ss} Error PRL_GetRecordedMotion failed slot={slot} result={motionResult}");
                    _recordingPlayerSlot = -1;
                    return;
                }

                // 从非托管内存读取结构体数组
                var ticks = new ReplayTick[actualTickCount];
                for (int i = 0; i < actualTickCount; i++)
                {
                    var ptr = (IntPtr)(tickBuf.ToInt64() + i * tickSize);
                    ticks[i] = Marshal.PtrToStructure<ReplayTick>(ptr);
                }

                var subs = new SubtickMove[actualSubCount];
                for (int i = 0; i < actualSubCount; i++)
                {
                    var ptr = (IntPtr)(subBuf.ToInt64() + i * subSize);
                    subs[i] = Marshal.PtrToStructure<SubtickMove>(ptr);
                }

                // 序列化到 JSON 文件
                var recording = new MotionRecordingJson
                {
                    Tickrate = (int)Math.Round(1.0f / Server.TickInterval),
                    Ticks = new List<ReplayTick>(ticks),
                    Subs = new List<SubtickMove>(subs),
                    PlayerWeapons = new List<string>(_recordingPlayerWeapons),
                };

                var playerName = player?.PlayerName ?? $"slot{slot}";
                var timestamp = DateTime.Now.ToString("yyyyMMdd_HHmmss");
                var fileName = $"rec_{SanitizeFileName(playerName)}_{timestamp}.json";
                var filePath = Path.Combine(_recordingsDirPath, fileName);

                var json = JsonSerializer.Serialize(recording, ReplayJsonOpts);
                File.WriteAllText(filePath, json);

                // 创建录制条目
                var entry = new RecordingEntry
                {
                    Id = _nextRecordingId++,
                    Name = $"{playerName}_{timestamp}",
                    FilePath = filePath,
                    Status = RecordingStatus.Idle,
                    BotSlot = -1,
                };
                _recordings.Add(entry);

                // 提示玩家
                if (player != null)
                {
                    player.PrintToChat(Localizer.ForPlayer(player, "record.stopped", entry.Id, actualTickCount));
                }

                Server.PrintToConsole($"[PracLab] {DateTime.Now:HH:mm:ss} Record saved: id={entry.Id} name={entry.Name} ticks={actualTickCount} subs={actualSubCount} file={fileName}");
            }
            finally
            {
                Marshal.FreeHGlobal(tickBuf);
                Marshal.FreeHGlobal(subBuf);
            }
        }
        catch (Exception ex)
        {
            if (player != null)
                player.PrintToChat(Localizer.ForPlayer(player, "record.stop_failed"));
            Server.PrintToConsole($"[PracLab] {DateTime.Now:HH:mm:ss} Error StopAndSaveRecording failed slot={slot} - {ex.Message}");
        }
        finally
        {
            // 无论成功失败，重置录制状态
            _recordingPlayerSlot = -1;
            _recordingPlayerWeapons.Clear();
        }
    }

    /// <summary>
    /// 清理文件名中的非法字符，确保生成的文件名在 Windows/Linux 上均合法。
    /// </summary>
    /// <param name="name">原始名称。</param>
    /// <returns>清理后的安全文件名。</returns>
    private static string SanitizeFileName(string name)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var sb = new System.Text.StringBuilder(name.Length);
        foreach (var c in name)
        {
            sb.Append(Array.IndexOf(invalid, c) >= 0 ? '_' : c);
        }
        return sb.ToString();
    }

    // ==================== .replay 命令 ====================

    /// <summary>
    /// .replay [entryId] — 自动创建一个 Bot（与玩家同队）并回放指定录制。
    /// 仿照 CS2-Bot-Controller 的回放逻辑，但内置 Bot 创建流程：
    ///   1. 解析 entryId（可选，未指定则使用最近一条 Idle 状态的录制）
    ///   2. 临时设置 bot_quota_mode=normal + bot_quota=(现有bot数+1)，由引擎创建 bot
    ///   3. 延迟 0.3s 等待引擎通过 quota 机制创建 Bot
    ///   4. 查找新创建的 Bot（不踢出多余 bot，用户指示多 bot 不重要）
    ///   5. 解除 Bot 冻结状态（bot_stop 0, bot_freeze 0, bot_zombie 0）
    ///   6. 从 JSON 加载录制数据 → PRL_LoadReplay + PRL_SetReplayPawn + PRL_StartReplay
    ///   7. OnTick 监听器检测回放结束（PRL_IsReplaying 返回 0）
    /// 关键修复（根因）：
    ///   - 必须设置 bot_quota_mode=normal，否则 fill/0 模式下引擎不管理 bot，
    ///     CCSBot::Update 不被调用 → 无 user command → ProcessMovement 不触发 → bot 卡死。
    ///   - 必须只用 bot_quota 让引擎创建 bot，不能调用 bot_add_t/ct。
    ///     bot_add_t 会强制创建额外 bot，引擎的 quota 管理会认为多了 bot，
    ///     停止为 bot_add_t 创建的 bot 生成 user command，导致 ProcessMovement
    ///     只调用 1 次后不再调用（log 证据：ReplayPre: calls=1）。
    ///     引擎 quota 创建的 bot 才会持续被 ProcessMovement 调用。
    /// 回放结束后在 StopReplayEntry 中恢复 bot_quota=0 + mode=fill。
    /// </summary>
    /// <param name="player">调用者玩家。</param>
    /// <param name="args">entryId（可选）。</param>
    private void HandleReplay(CCSPlayerController player, string args)
    {
        Server.PrintToConsole("[PracLab] HandleReplay: executing...");

        if (!EnsureReplayEngine(player)) return;

        // 清除所有已有的回放 bot（Playing 状态或回放结束已冻结的 bot）
        // 参考 CS2-Bot-Controller：每次回放前清理旧 bot，避免 bot_quota 累积和冻结 bot 残留
        var entriesToCleanup = _recordings
            .Where(e => e.Status == RecordingStatus.Playing || (e.Status == RecordingStatus.Idle && e.BotSlot >= 0))
            .ToList();
        if (entriesToCleanup.Count > 0)
        {
            Server.PrintToConsole($"[PracLab] {DateTime.Now:HH:mm:ss} Replay: cleaning up {entriesToCleanup.Count} existing replay bot(s) before starting new replay");
            foreach (var entryToClean in entriesToCleanup)
            {
                Server.PrintToConsole($"[PracLab] {DateTime.Now:HH:mm:ss} Replay: cleaning up entry id={entryToClean.Id} status={entryToClean.Status} botSlot={entryToClean.BotSlot}");
                // cleanup 场景不恢复 bot_quota（restoreBotQuota=false），
                // 因为 HandleReplay 会立即设置新 bot_quota 创建新 bot。
                // 若恢复 bot_quota=0 会覆盖新 bot_quota，导致 bot 生成失败。
                StopReplayEntry(entryToClean, restoreBotQuota: false);
            }
        }

        // 解析录制 ID（可选）：
        //   .replay <Id> — 回放指定 ID 的录制
        //   .replay       — 并行回放所有 Idle 状态的录制（为每条录制创建一个 bot）
        var parts = args.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);
        int specifiedId = 0;
        bool hasId = parts.Length >= 1 && int.TryParse(parts[0], out specifiedId);

        List<RecordingEntry> entriesToReplay;
        if (hasId)
        {
            var entry = _recordings.FirstOrDefault(e => e.Id == specifiedId);
            if (entry == null)
            {
                player.PrintToChat(Localizer.ForPlayer(player, "replay.no_record"));
                return;
            }
            if (entry.Status == RecordingStatus.Playing)
            {
                player.PrintToChat(Localizer.ForPlayer(player, "replay.already_playing", entry.Id));
                return;
            }
            if (!File.Exists(entry.FilePath))
            {
                player.PrintToChat(Localizer.ForPlayer(player, "replay.file_not_found", entry.Id));
                Server.PrintToConsole($"[PracLab] {DateTime.Now:HH:mm:ss} Error Recording file not found: {entry.FilePath}");
                return;
            }
            entriesToReplay = new List<RecordingEntry> { entry };
        }
        else
        {
            // 无参数：并行回放所有 Idle 状态且文件存在的录制
            entriesToReplay = _recordings
                .Where(e => e.Status == RecordingStatus.Idle && File.Exists(e.FilePath))
                .ToList();
            if (entriesToReplay.Count == 0)
            {
                player.PrintToChat(Localizer.ForPlayer(player, "replay.no_record"));
                return;
            }
        }

        // 记录创建前的 Bot UserId 集合，用于识别新创建的 Bot
        var existingBotUserIds = new HashSet<int>();
        foreach (var p in Utilities.GetPlayers())
        {
            if (p != null && p.IsValid && p.IsBot && !p.IsHLTV && p.UserId.HasValue)
                existingBotUserIds.Add(p.UserId.Value);
        }

        // 在玩家同队创建 Bot（项目规范：回放 bot 与玩家同队）
        // 关键修复（根因）：只用 bot_quota 让引擎创建 bot，不能调用 bot_add_t/ct。
        // 之前调用 bot_add_t 会导致引擎 quota 管理 "认为多了 bot"，停止为该 bot 生成
        // user command，ProcessMovement 只调用 1 次后不再调用（log: ReplayPre: calls=1）。
        // 引擎 quota 创建的 bot 才会持续被 ProcessMovement 调用，bot 才能真正移动。
        var playerTeam = (CsTeam)player.TeamNum;
        var targetTeam = playerTeam == CsTeam.Terrorist ? CsTeam.Terrorist : CsTeam.CounterTerrorist;

        // 统计当前所有 bot（含 prac bot），用于计算正确的 bot_quota
        // 注意：cleanup 踢出的旧 bot 仍在服务器上（kickid 是 NextFrame 异步执行），
        // 但即将离开，不应计入 existingBotCount，否则 bot_quota 会多算导致生成多余 bot
        var cleanupCount = entriesToCleanup.Count;
        var existingBotCount = 0;
        foreach (var p in Utilities.GetPlayers())
        {
            if (p != null && p.IsValid && p.IsBot && !p.IsHLTV)
                existingBotCount++;
        }
        existingBotCount = Math.Max(0, existingBotCount - cleanupCount);
        var replayCount = entriesToReplay.Count;
        var requiredQuota = existingBotCount + replayCount;
        Server.ExecuteCommand(targetTeam == CsTeam.Terrorist ? "bot_join_team T" : "bot_join_team CT");
        Server.ExecuteCommand("bot_quota_mode normal");
        Server.ExecuteCommand($"bot_quota {requiredQuota}");
        Server.PrintToConsole($"[PracLab] {DateTime.Now:HH:mm:ss} Replay: set bot_quota={requiredQuota} (existing={existingBotCount}+{replayCount}), mode=normal, team={targetTeam} (engine-managed, no bot_add)");

        var initiatorUserId = player.UserId ?? -1;
        var caller = player;
        var entryIds = entriesToReplay.Select(e => e.Id).ToList();

        // 延迟 0.3s 等待引擎通过 quota 机制创建 Bot（多 bot 时引擎需要足够时间）
        AddTimer(0.3f, () =>
        {
            try
            {
                // 查找所有新创建的 bot（按需要的数量）
                var newBots = FindAllNewlyCreatedBots(existingBotUserIds, targetTeam, replayCount);
                if (newBots.Count == 0)
                {
                    caller.PrintToChat(Localizer.ForPlayer(caller, "bot.spawn_failed", targetTeam));
                    Server.PrintToConsole($"[PracLab] {DateTime.Now:HH:mm:ss} Error Replay bot spawn failed for team {targetTeam}");
                    return;
                }

                // 解除所有 Bot 冻结状态：回放需要 Bot AI 运行以生成 user command，
                // ProcessMovement 才会被调用（C++ hook 在 ProcessMovement pre/post 注入录制数据）
                Server.ExecuteCommand("bot_stop 0");
                Server.ExecuteCommand("bot_freeze 0");
                Server.ExecuteCommand("bot_zombie 0");

                // 按 1:1 匹配 bot 和录制条目，逐个启动回放
                int matched = Math.Min(newBots.Count, entryIds.Count);
                for (int i = 0; i < matched; i++)
                {
                    var botPlayer = newBots[i];
                    var entryId = entryIds[i];
                    Server.PrintToConsole($"[PracLab] {DateTime.Now:HH:mm:ss} Replay: bot found slot={botPlayer.Slot} name={botPlayer.PlayerName}, unfreezing");
                    LoadAndStartReplay(botPlayer, entryId, initiatorUserId, caller);
                }

                // 部分 bot 生成失败时提示玩家
                if (newBots.Count < entryIds.Count)
                {
                    caller.PrintToChat(Localizer.ForPlayer(caller, "replay.batch_partial", newBots.Count, entryIds.Count));
                    Server.PrintToConsole($"[PracLab] {DateTime.Now:HH:mm:ss} Warning Only {newBots.Count}/{entryIds.Count} bots spawned, {entryIds.Count - newBots.Count} entries skipped");
                }
            }
            catch (Exception ex)
            {
                caller.PrintToChat(Localizer.ForPlayer(caller, "replay.start_failed", entryIds.Count > 0 ? entryIds[0] : 0));
                Server.PrintToConsole($"[PracLab] {DateTime.Now:HH:mm:ss} Error HandleReplay spawn timer failed - {ex.Message}");
            }
        });
    }

    /// <summary>
    /// 在指定队伍中查找新创建的 Bot（不在 existingBotUserIds 集合中的 Bot）。
    /// 仿照 BotCommands.SpawnBot 的查找逻辑：遍历 cs_player_controller，找到未注册的同队 Bot。
    /// 若发现多个新 Bot，保留第一个，踢出多余的（防止 fill 模式补充）。
    /// </summary>
    /// <param name="existingBotUserIds">创建前已存在的 Bot UserId 集合。</param>
    /// <param name="expectedTeam">期望的 Bot 队伍。</param>
    /// <returns>新创建的 Bot 控制器，未找到返回 null。</returns>
    private CCSPlayerController? FindNewlyCreatedBot(HashSet<int> existingBotUserIds, CsTeam expectedTeam)
    {
        var playerEntities = Utilities.FindAllEntitiesByDesignerName<CCSPlayerController>("cs_player_controller");
        CCSPlayerController? newBot = null;

        foreach (var p in playerEntities)
        {
            if (!p.IsValid || !p.IsBot || p.IsHLTV) continue;
            if (!p.UserId.HasValue) continue;

            var userId = p.UserId.Value;

            // 已存在的 Bot 跳过
            if (existingBotUserIds.Contains(userId))
                continue;

            // 已注册为练习 Bot 的跳过（避免误用 .bot 命令创建的练习 Bot）
            if (_pracBots.ContainsKey(userId))
                continue;

            // 校验队伍
            var actualTeam = (CsTeam)p.TeamNum;
            if (actualTeam != expectedTeam && actualTeam != CsTeam.None)
            {
                // 队伍不符，尝试切换
                Server.PrintToConsole($"[PracLab] {DateTime.Now:HH:mm:ss} Replay bot {p.PlayerName} wrong team {actualTeam}, expected {expectedTeam}, force switching");
                try
                {
                    p.ChangeTeam(expectedTeam);
                }
                catch (Exception ex)
                {
                    Server.PrintToConsole($"[PracLab] {DateTime.Now:HH:mm:ss} Error ChangeTeam failed - {ex.Message}");
                }
            }

            // 第一个新 Bot 保留作为回放 bot
            // 注意：不踢出多余 bot（用户指示多 bot 不重要）。
            // 踢出会触发引擎 quota 管理创建新 bot 来补齐，导致循环。
            // 多余 bot 不会干扰回放（OnReplayPre 只为注册的 slot 触发）。
            if (newBot == null)
            {
                newBot = p;
                Server.PrintToConsole($"[PracLab] {DateTime.Now:HH:mm:ss} Replay bot found: {p.PlayerName} UserId={userId} team={p.TeamNum}");
            }
            else
            {
                Server.PrintToConsole($"[PracLab] {DateTime.Now:HH:mm:ss} Replay found extra bot {p.PlayerName} UserId={userId}, keeping (not kicked)");
            }
        }

        return newBot;
    }

    /// <summary>
    /// 在指定队伍中查找所有新创建的 Bot（不在 existingBotUserIds 集合中的 Bot）。
    /// 用于 .replay 无参数批量回放场景：一次创建多个 bot，需要全部收集。
    /// </summary>
    /// <param name="existingBotUserIds">创建前已存在的 Bot UserId 集合。</param>
    /// <param name="expectedTeam">期望的 Bot 队伍。</param>
    /// <param name="expectedCount">期望找到的 Bot 数量（找到此数量后停止遍历）。</param>
    /// <returns>新创建的 Bot 控制器列表（可能少于 expectedCount）。</returns>
    private List<CCSPlayerController> FindAllNewlyCreatedBots(HashSet<int> existingBotUserIds, CsTeam expectedTeam, int expectedCount)
    {
        var result = new List<CCSPlayerController>();
        var playerEntities = Utilities.FindAllEntitiesByDesignerName<CCSPlayerController>("cs_player_controller");

        foreach (var p in playerEntities)
        {
            if (!p.IsValid || !p.IsBot || p.IsHLTV) continue;
            if (!p.UserId.HasValue) continue;

            var userId = p.UserId.Value;
            if (existingBotUserIds.Contains(userId)) continue;
            if (_pracBots.ContainsKey(userId)) continue;

            var actualTeam = (CsTeam)p.TeamNum;
            if (actualTeam != expectedTeam && actualTeam != CsTeam.None)
            {
                Server.PrintToConsole($"[PracLab] {DateTime.Now:HH:mm:ss} Replay bot {p.PlayerName} wrong team {actualTeam}, expected {expectedTeam}, force switching");
                try { p.ChangeTeam(expectedTeam); }
                catch (Exception ex) { Server.PrintToConsole($"[PracLab] {DateTime.Now:HH:mm:ss} Error ChangeTeam failed - {ex.Message}"); }
            }

            result.Add(p);
            Server.PrintToConsole($"[PracLab] {DateTime.Now:HH:mm:ss} Replay bot found: {p.PlayerName} UserId={userId} team={p.TeamNum}");

            if (result.Count >= expectedCount)
                break;
        }

        return result;
    }

    /// <summary>
    /// 从 JSON 文件加载录制数据并启动回放。
    /// 流程：反序列化 JSON → 分配非托管内存 → PRL_LoadReplay → PRL_SetReplayPawn → PRL_StartReplay。
    /// </summary>
    /// <param name="botPlayer">回放 Bot 控制器。</param>
    /// <param name="entryId">录制条目 ID。</param>
    /// <param name="initiatorUserId">回放发起者 UserId。</param>
    /// <param name="caller">调用者玩家（用于错误提示）。</param>
    private void LoadAndStartReplay(CCSPlayerController botPlayer, int entryId, int initiatorUserId, CCSPlayerController caller)
    {
        Server.PrintToConsole($"[PracLab] LoadAndStartReplay: executing... bot={botPlayer.PlayerName} entryId={entryId}");

        try
        {
            // 查找录制条目
            var entry = _recordings.FirstOrDefault(e => e.Id == entryId);
            if (entry == null)
            {
                caller.PrintToChat(Localizer.ForPlayer(caller, "replay.no_record"));
                return;
            }

            // 反序列化 JSON
            var json = File.ReadAllText(entry.FilePath);
            var recording = JsonSerializer.Deserialize<MotionRecordingJson>(json, ReplayJsonOpts);
            if (recording == null || recording.Ticks.Count == 0)
            {
                caller.PrintToChat(Localizer.ForPlayer(caller, "replay.no_data", entry.Id));
                return;
            }

            var botSlot = botPlayer.Slot;
            var botPawn = botPlayer.PlayerPawn.Value;
            if (botPawn == null || !botPawn.IsValid)
            {
                caller.PrintToChat(Localizer.ForPlayer(caller, "bot.spawn_failed", (CsTeam)botPlayer.TeamNum));
                return;
            }

            // 给 bot 复制录制时玩家的武器装备（参考 CS2-Bot-Controller WeaponLocker）
            // 确保 bot 有相同的武器来复刻武器相关动作（切枪、投掷道具等）
            if (recording.PlayerWeapons.Count > 0)
            {
                GiveBotWeapons(botPlayer, recording.PlayerWeapons);
            }

            // 分配非托管内存并拷贝结构体数据
            int tickCount = recording.Ticks.Count;
            int subCount = recording.Subs.Count;
            int tickSize = Marshal.SizeOf<ReplayTick>();
            int subSize = Marshal.SizeOf<SubtickMove>();

            IntPtr tickBuf = Marshal.AllocHGlobal(tickCount * tickSize);
            IntPtr subBuf = subCount > 0 ? Marshal.AllocHGlobal(subCount * subSize) : IntPtr.Zero;

            try
            {
                // 拷贝 ticks
                for (int i = 0; i < tickCount; i++)
                {
                    var ptr = (IntPtr)(tickBuf.ToInt64() + i * tickSize);
                    Marshal.StructureToPtr(recording.Ticks[i], ptr, false);
                }

                // 拷贝 subs
                for (int i = 0; i < subCount; i++)
                {
                    var ptr = (IntPtr)(subBuf.ToInt64() + i * subSize);
                    Marshal.StructureToPtr(recording.Subs[i], ptr, false);
                }

                // 调用 C++ 加载回放数据
                if (PRL_LoadReplay(botSlot, tickBuf, tickCount, subBuf, subCount) != 1)
                {
                    caller.PrintToChat(Localizer.ForPlayer(caller, "replay.load_failed", entry.Id));
                    Server.PrintToConsole($"[PracLab] {DateTime.Now:HH:mm:ss} Error PRL_LoadReplay failed slot={botSlot} entryId={entryId}");
                    return;
                }

                // 注册回放目标 pawn
                if (PRL_SetReplayPawn(botSlot, botPawn.Handle) != 1)
                {
                    caller.PrintToChat(Localizer.ForPlayer(caller, "replay.pawn_failed", entry.Id));
                    Server.PrintToConsole($"[PracLab] {DateTime.Now:HH:mm:ss} Error PRL_SetReplayPawn failed slot={botSlot} entryId={entryId}");
                    return;
                }

                // Bot AI 处理（仿照 CS2-Bot-Controller）：
                // C++ hook 在 CCSBot::Update 中检测到 IsReplaying(slot) 时：
                //   1. 设置 kBot_AiTickedFlag=1，告诉引擎 "AI 已执行"
                //   2. 直接 return，跳过 AI 决策逻辑（避免自主移动/瞄准/射击）
                // 引擎仍会为 bot 生成 user command 并调用 ProcessMovement，
                // 确保回放 hook 链（OnReplayPre / OnReplayFinishMove / OnReplayCommit）每 tick 都被触发。
                // 同时 CCSBot::Upkeep / UpdateLookAngles / SetEyeAngles hook 跳过引擎视角覆盖，
                // 改由 ApplyReplayEyeAngles 主动调用原 SetEyeAngles 写入回放视角。

                // 开始回放（非循环）
                if (PRL_StartReplay(botSlot, 0) != 1)
                {
                    caller.PrintToChat(Localizer.ForPlayer(caller, "replay.start_failed", entry.Id));
                    Server.PrintToConsole($"[PracLab] {DateTime.Now:HH:mm:ss} Error PRL_StartReplay failed slot={botSlot} entryId={entryId}");
                    return;
                }

                // 更新录制条目状态
                entry.Status = RecordingStatus.Playing;
                entry.BotSlot = botSlot;
                entry.InitiatorUserId = initiatorUserId;

                // 注册 OnTick 监听器（如果尚未注册）
                RegisterReplayTickListener();

                caller.PrintToChat(Localizer.ForPlayer(caller, "replay.started", entry.Id, tickCount));
                Server.PrintToConsole($"[PracLab] {DateTime.Now:HH:mm:ss} Replay started: id={entry.Id} bot={botPlayer.PlayerName} slot={botSlot} ticks={tickCount} subs={subCount}");

                // 打印回放开始前的诊断计数器（基线），用于和回放结束后对比
                PrintDiagnostics("before replay (baseline)");
            }
            finally
            {
                Marshal.FreeHGlobal(tickBuf);
                if (subBuf != IntPtr.Zero) Marshal.FreeHGlobal(subBuf);
            }
        }
        catch (Exception ex)
        {
            caller.PrintToChat(Localizer.ForPlayer(caller, "replay.start_failed", entryId));
            Server.PrintToConsole($"[PracLab] {DateTime.Now:HH:mm:ss} Error LoadAndStartReplay failed - {ex.Message}");
        }
    }

    // ==================== .stopreplay 命令 ====================

    /// <summary>
    /// 停止单个录制条目的回放并踢出对应 Bot。
    /// 由 HandleStopReplay、HandleClearRecord、HandleClearRecordAll 复用。
    /// 流程：PRL_StopReplay + 更新条目状态 + Server.NextFrame kickid 踢出回放 Bot。
    /// 注意：回放 Bot 是 .replay 命令自动创建的，停止回放时应踢出；
    /// 不设置 bot_quota/bot_quota_mode，避免 fill 模式补充。
    /// 支持 Playing（正在回放）和 Idle+BotSlot>=0（回放结束已冻结）两种状态：
    ///   - Playing：停止回放 → 解冻 → 踢出
    ///   - Idle+冻结：仅解冻 → 踢出（回放已自然结束，bot 被冻结保持静止）
    /// </summary>
    /// <param name="entry">要停止的录制条目（Playing 或 Idle+BotSlot>=0）。</param>
    /// <param name="restoreBotQuota">是否在踢出 bot 后恢复 bot_quota=0/mode=fill。
    /// cleanup 场景（HandleReplay 中连续回放）应设为 false，避免 NextFrame 回调中的
    /// bot_quota=0 覆盖 HandleReplay 刚设置的新 bot_quota。</param>
    /// <returns>true 表示成功停止；false 表示条目无关联 bot。</returns>
    private bool StopReplayEntry(RecordingEntry entry, bool restoreBotQuota = true)
    {
        // 接受 Playing（正在回放）或 Idle+BotSlot>=0（回放结束已冻结的 bot）
        if (entry.BotSlot < 0)
            return false;
        if (entry.Status != RecordingStatus.Playing && entry.Status != RecordingStatus.Idle)
            return false;

        var botSlot = entry.BotSlot;
        var wasPlaying = entry.Status == RecordingStatus.Playing;

        // 仅 Playing 状态需要停止回放（Idle 状态回放已自然结束）
        if (wasPlaying)
        {
            var stopResult = PRL_StopReplay(botSlot);
            if (stopResult != 1)
            {
                Server.PrintToConsole($"[PracLab] {DateTime.Now:HH:mm:ss} Warning PRL_StopReplay failed slot={botSlot} entryId={entry.Id} result={stopResult}");
            }
        }

        // 解冻 bot AI（CheckReplayEnd 中冻结的），恢复 AI 后再踢出
        try
        {
            PRL_UnfreezeSlot(botSlot);
            Server.PrintToConsole($"[PracLab] {DateTime.Now:HH:mm:ss} Replay: unfrozen bot slot={botSlot} (wasPlaying={wasPlaying})");
        }
        catch (Exception ex)
        {
            Server.PrintToConsole($"[PracLab] {DateTime.Now:HH:mm:ss} Warning PRL_UnfreezeSlot failed slot={botSlot} - {ex.Message}");
        }

        // 查找 Bot 控制器以获取 UserId 用于踢出
        int? botUserId = null;
        foreach (var p in Utilities.GetPlayers())
        {
            if (p == null || !p.IsValid || !p.IsBot || p.IsHLTV) continue;
            if (p.Slot != botSlot) continue;
            if (p.UserId.HasValue)
                botUserId = p.UserId.Value;
            break;
        }

        // 更新条目状态
        entry.Status = RecordingStatus.Idle;
        entry.BotSlot = -1;

        // 用 Server.NextFrame 延迟执行 kickid，避免在命令处理器中直接执行不生效（参考 HandleKickAllBots）
        // 同时恢复 bot_quota/bot_quota_mode 到 prac.cfg 原始设置（fill/0），避免影响后续 .bot 命令
        if (botUserId.HasValue)
        {
            var userId = botUserId.Value;
            Server.NextFrame(() =>
            {
                try
                {
                    Server.ExecuteCommand($"kickid {userId}");
                    Server.PrintToConsole($"[PracLab] {DateTime.Now:HH:mm:ss} Replay kicked bot UserId={userId}");
                }
                catch (Exception ex)
                {
                    Server.PrintToConsole($"[PracLab] {DateTime.Now:HH:mm:ss} Error kickid {userId} failed - {ex.Message}");
                }

                // 恢复 prac.cfg 原始 bot_quota 设置：fill/0 模式
                // 回放期间临时改为 normal/(N+1)，回放结束后必须恢复，否则影响 .bot 命令行为
                // cleanup 场景（restoreBotQuota=false）跳过恢复，因为 HandleReplay 会立即设置新 bot_quota
                if (restoreBotQuota)
                {
                    Server.ExecuteCommand("bot_quota 0");
                    Server.ExecuteCommand("bot_quota_mode fill");
                    Server.PrintToConsole($"[PracLab] {DateTime.Now:HH:mm:ss} Replay: restored bot_quota=0, mode=fill");
                }
            });
        }

        Server.PrintToConsole($"[PracLab] {DateTime.Now:HH:mm:ss} Replay stopped: id={entry.Id} name={entry.Name} botSlot={botSlot}");
        return true;
    }

    /// <summary>
    /// .stopreplay — 停止所有正在回放的录制并踢出对应 Bot。
    /// 遍历 _recordings 找出所有 Playing 条目，调用 StopReplayEntry 逐个停止。
    /// 同时处理 Idle+BotSlot>=0 的冻结 bot（回放自然结束后的静止 bot）。
    /// 若两者都没有，向玩家提示 replay.not_playing。
    /// </summary>
    /// <param name="player">调用者玩家。</param>
    /// <param name="args">命令参数（未使用）。</param>
    private void HandleStopReplay(CCSPlayerController player, string args)
    {
        Server.PrintToConsole("[PracLab] HandleStopReplay: executing...");

        if (!EnsureReplayEngine(player)) return;

        // 收集所有需要停止的条目：
        //   - Playing+BotSlot>=0：正在回放的 bot
        //   - Idle+BotSlot>=0：回放结束已冻结的 bot（需踢出）
        var entriesToStop = new List<RecordingEntry>();
        foreach (var entry in _recordings)
        {
            if (entry.BotSlot < 0) continue;
            if (entry.Status == RecordingStatus.Playing || entry.Status == RecordingStatus.Idle)
                entriesToStop.Add(entry);
        }

        if (entriesToStop.Count == 0)
        {
            player.PrintToChat(Localizer.ForPlayer(player, "replay.not_playing"));
            Server.PrintToConsole($"[PracLab] {DateTime.Now:HH:mm:ss} Warning StopReplay: no playing or frozen recordings");
            return;
        }

        // 逐个停止回放（或解冻踢出）
        var stoppedIds = new List<int>();
        foreach (var entry in entriesToStop)
        {
            if (StopReplayEntry(entry))
                stoppedIds.Add(entry.Id);
        }

        // 向玩家提示停止的回放数量
        player.PrintToChat(Localizer.ForPlayer(player, "replay.stopped", stoppedIds.Count));
        Server.PrintToConsole($"[PracLab] {DateTime.Now:HH:mm:ss} {player.PlayerName} stopped {stoppedIds.Count} replays: [{string.Join(", ", stoppedIds)}]");
    }

    // ==================== .clearrecord / .clearrecordall 命令 ====================

    /// <summary>
    /// .clearrecord &lt;Id&gt; — 删除指定 ID 的录制（文件 + 列表条目）。
    /// 若该录制正在回放，先停止回放并踢出 Bot；若正在录制，先停止录制。
    /// </summary>
    /// <param name="player">调用者玩家。</param>
    /// <param name="args">录制 ID（必填）。</param>
    private void HandleClearRecord(CCSPlayerController player, string args)
    {
        Server.PrintToConsole("[PracLab] HandleClearRecord: executing...");

        if (!EnsureReplayEngine(player)) return;

        // 参数校验
        if (string.IsNullOrWhiteSpace(args) || !int.TryParse(args.Trim(), out var id))
        {
            player.PrintToChat(Localizer.ForPlayer(player, "record.usage"));
            Server.PrintToConsole($"[PracLab] {DateTime.Now:HH:mm:ss} Warning ClearRecord: invalid args '{args}'");
            return;
        }

        // 查找录制条目
        var entry = _recordings.FirstOrDefault(e => e.Id == id);
        if (entry == null)
        {
            player.PrintToChat(Localizer.ForPlayer(player, "record.not_found", id));
            Server.PrintToConsole($"[PracLab] {DateTime.Now:HH:mm:ss} Warning ClearRecord: entry id={id} not found");
            return;
        }

        // 若正在回放或已冻结（回放结束后的静止 bot），先停止/解冻并踢出 Bot
        if (entry.Status == RecordingStatus.Playing ||
            (entry.Status == RecordingStatus.Idle && entry.BotSlot >= 0))
        {
            StopReplayEntry(entry);
        }

        // 若正在录制此条目（理论上 Recording 状态的条目不会在 _recordings 中，因为录制完成后才添加）
        // 但为了安全，如果 _recordingPlayerSlot 对应此条目，先停止录制
        // 注：录制中的条目尚未加入 _recordings，此处仅为防御性代码
        if (entry.Status == RecordingStatus.Recording)
        {
            StopAndSaveRecording(_recordingPlayerSlot, player);
        }

        // 删除文件
        try
        {
            if (File.Exists(entry.FilePath))
            {
                File.Delete(entry.FilePath);
            }
        }
        catch (Exception ex)
        {
            Server.PrintToConsole($"[PracLab] {DateTime.Now:HH:mm:ss} Warning delete file failed: {entry.FilePath} - {ex.Message}");
        }

        // 从列表中移除
        _recordings.Remove(entry);

        player.PrintToChat(Localizer.ForPlayer(player, "record.cleared", id));
        Server.PrintToConsole($"[PracLab] {DateTime.Now:HH:mm:ss} Record cleared: id={id} name={entry.Name}");
    }

    /// <summary>
    /// .clearrecordall — 清除所有录制（停止所有回放 + 删除所有文件 + 清空列表）。
    /// 流程：
    ///   1. 停止所有正在回放的录制（StopReplayEntry 逐个处理）
    ///   2. 停止当前录制（如果在录制中）
    ///   3. 删除所有录制 JSON 文件
    ///   4. 清空 _recordings 列表
    ///   5. 重置 _nextRecordingId 为 1
    /// </summary>
    /// <param name="player">调用者玩家。</param>
    /// <param name="args">命令参数（未使用）。</param>
    private void HandleClearRecordAll(CCSPlayerController player, string args)
    {
        Server.PrintToConsole("[PracLab] HandleClearRecordAll: executing...");

        if (!EnsureReplayEngine(player)) return;

        // 停止所有正在回放的录制（包括已冻结的 bot）
        int stoppedReplays = 0;
        foreach (var entry in _recordings)
        {
            if (entry.Status == RecordingStatus.Playing ||
                (entry.Status == RecordingStatus.Idle && entry.BotSlot >= 0))
            {
                if (StopReplayEntry(entry))
                    stoppedReplays++;
            }
        }

        // 停止当前录制（如果在录制中）
        if (_recordingPlayerSlot >= 0)
        {
            StopAndSaveRecording(_recordingPlayerSlot, player);
        }

        // 取消 pending 状态（如果玩家正在等待开始录制）
        if (_pendingRecordSlot >= 0)
        {
            _pendingRecordSlot = -1;
            Server.PrintToConsole($"[PracLab] {DateTime.Now:HH:mm:ss} Record pending cancelled by clearrecordall");
        }

        // 删除所有录制文件
        int deletedFiles = 0;
        foreach (var entry in _recordings)
        {
            try
            {
                if (File.Exists(entry.FilePath))
                {
                    File.Delete(entry.FilePath);
                    deletedFiles++;
                }
            }
            catch (Exception ex)
            {
                Server.PrintToConsole($"[PracLab] {DateTime.Now:HH:mm:ss} Warning delete file failed: {entry.FilePath} - {ex.Message}");
            }
        }

        int totalCleared = _recordings.Count;
        _recordings.Clear();
        _nextRecordingId = 1;

        player.PrintToChat(Localizer.ForPlayer(player, "record.cleared_all", totalCleared));
        Server.PrintToConsole($"[PracLab] {DateTime.Now:HH:mm:ss} {player.PlayerName} cleared all records: count={totalCleared} files={deletedFiles} replays_stopped={stoppedReplays}");
    }

    // ==================== .currentrecord 命令 ====================

    /// <summary>
    /// .currentrecord — 在聊天栏提示并往玩家控制台输出录制列表表格。
    /// 表格列：ID / Name / Status / Bot（Bot = 回放 bot 名称，未回放时显示 "—"）。
    /// 若 _recordings 为空，向玩家提示 record.no_records。
    /// </summary>
    /// <param name="player">调用者玩家。</param>
    /// <param name="args">命令参数（未使用）。</param>
    private void HandleCurrentRecord(CCSPlayerController player, string args)
    {
        Server.PrintToConsole("[PracLab] HandleCurrentRecord: executing...");

        if (!EnsureReplayEngine(player)) return;

        if (_recordings.Count == 0)
        {
            player.PrintToChat(Localizer.ForPlayer(player, "record.no_records"));
            Server.PrintToConsole($"[PracLab] {DateTime.Now:HH:mm:ss} CurrentRecord: no recordings for {player.PlayerName}");
            return;
        }

        // 聊天栏提示玩家查看控制台
        player.PrintToChat(Localizer.ForPlayer(player, "record.check_console"));

        // 控制台输出表格（英文，符合控制台日志规范）
        // 表头与分隔线通过本地化键获取（zh-CN/en 均为英文纯文本，无颜色代码）
        var header = Localizer.ForPlayer(player, "record.table_header");
        var separator = Localizer.ForPlayer(player, "record.table_separator");

        player.PrintToConsole("===== PracLab Recordings =====");
        player.PrintToConsole(header);
        player.PrintToConsole(separator);

        foreach (var entry in _recordings)
        {
            // 状态显示：Playing -> "Playing Back...."，Idle+BotSlot>=0 -> "Frozen"，其他 -> "Idle"
            var status = entry.Status switch
            {
                RecordingStatus.Playing => "Playing Back....",
                RecordingStatus.Idle when entry.BotSlot >= 0 => "Frozen",
                _ => "Idle"
            };

            // 查找回放 bot 名称：Playing 或 Frozen 状态都通过 BotSlot 查找对应 bot
            string botName = "-";
            if ((entry.Status == RecordingStatus.Playing || entry.Status == RecordingStatus.Idle) && entry.BotSlot >= 0)
            {
                foreach (var p in Utilities.GetPlayers())
                {
                    if (p == null || !p.IsValid || !p.IsBot || p.IsHLTV) continue;
                    if (p.Slot != entry.BotSlot) continue;
                    botName = p.PlayerName;
                    break;
                }
                // 若 bot 已断开但状态未更新，显示 slot 号
                if (botName == "-")
                    botName = $"slot:{entry.BotSlot}";
            }

            player.PrintToConsole($"{entry.Id,-6} {entry.Name,-40} {status,-16} {botName}");
        }

        player.PrintToConsole(separator);
        player.PrintToConsole($"Total: {_recordings.Count} recording(s)");

        Server.PrintToConsole($"[PracLab] {DateTime.Now:HH:mm:ss} {player.PlayerName} queried recordings: count={_recordings.Count}");
    }
}
