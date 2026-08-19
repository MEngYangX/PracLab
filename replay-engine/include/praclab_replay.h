// PracLabReplayEngine C API 头文件。
// 供 PracLab CSSharp 插件通过 P/Invoke 调用。
//
// 本头文件定义跨语言 ABI 契约：
//   - 数据结构（PRL_MovementSnapshot / PRL_ReplayTick / PRL_SubtickMove）的
//     内存布局必须与 C++ 侧 src/recorder.h 中的 MovementSnapshot / ReplayTick /
//     SubtickMove 完全一致（同 #pragma pack(4) + 等价字段类型）。
//   - C# 侧（Task 8）通过 [StructLayout(LayoutKind.Sequential, Pack=4)]
//     镜像这些结构。
//   - 修改字段顺序/类型会破坏 P/Invoke 兼容性，须同步更新三层代码。

// 平台导出宏：Windows 使用 dllexport，Linux 使用 visibility(default)。
// 配合 CMakeLists.txt 中 Linux 的 -fvisibility=hidden，仅 PRL_API 标记的
// 符号会被导出，其余符号保持隐藏，避免污染动态符号表。
#ifdef _WIN32
#define PRL_API __declspec(dllexport)
#else
#define PRL_API __attribute__((visibility("default")))
#endif

#ifdef __cplusplus
extern "C" {
#endif

// ---- 跨语言数据结构 ----
// 使用 C 兼容类型（unsigned int / unsigned char / unsigned long long / int），
// 而非 stdint.h 类型（uint32_t 等），以避免不同编译器对 stdint 类型在 C ABI
// 边界的差异。这些类型在 x64 平台（Windows MSVC、Linux GCC）上大小固定：
//   float               = 4 字节
//   unsigned int        = 4 字节  (= uint32_t)
//   unsigned char       = 1 字节  (= uint8_t)
//   unsigned long long  = 8 字节  (= uint64_t)
//   int                 = 4 字节  (= int32_t)
#pragma pack(push, 4)

// 一帧移动快照。对应 C++ 侧 PracLab::ReplayEngine::MovementSnapshot。
// 在 ProcessMovement 前（pre）和后（post）各捕获一次。
typedef struct PRL_MovementSnapshot {
    float originX, originY, originZ;        // 场景节点 m_vecAbsOrigin
    float velX, velY, velZ;                 // m_vecAbsVelocity
    float pitch, yaw, roll;                  // 视角
    unsigned int entityFlags;                // m_fFlags
    unsigned char moveType;                  // m_MoveType
    unsigned char _pad[3];                   // 4 字节对齐填充
    unsigned long long buttons;              // m_nButtons states[0]
    unsigned long long buttons1;              // states[1]
    unsigned long long buttons2;              // states[2]
    float duckAmount;                        // m_flDuckAmount
    float duckSpeed;                         // m_flDuckSpeed
    float ladderNormalX, ladderNormalY, ladderNormalZ;  // m_vecLadderNormal
    unsigned char ducked, ducking, desiresDuck;  // 蹲下状态机
    unsigned char actualMoveType;            // m_nActualMoveType
} PRL_MovementSnapshot;

// 一个录制的服务器 tick。
// pre/post 为 ProcessMovement 前/后快照；numSubtick 指示本 tick 关联的
// subtick 输入数量（对应并行 SubtickMove 缓冲区中的连续段）。
typedef struct PRL_ReplayTick {
    PRL_MovementSnapshot pre;     // ProcessMovement 前
    PRL_MovementSnapshot post;    // ProcessMovement 后
    int weaponDefIndex;           // 当前武器（-1 = 无）
    unsigned int numSubtick;      // subtick 数量
} PRL_ReplayTick;

// 一个 subtick 输入步。
// 对应 CSubtickMoveStep（protobuf），在 tick 内的 [0,1) 时间点触发。
typedef struct PRL_SubtickMove {
    float when;              // tick 内时间点 [0,1)
    unsigned int button;     // 按钮位（0 = 模拟量输入）
    float pressed;           // 按下/释放（1.0=按下 0.0=释放）
    float analogForward;     // 前进模拟量
    float analogLeft;        // 左移模拟量
    float pitchDelta;        // 俯仰增量
    float yawDelta;          // 偏航增量
} PRL_SubtickMove;

// 可选的每 tick 命令帧数据（68 字节）。
// 对应 C++ 侧 BotController::ReplayCommandFrameData。
typedef struct PRL_ReplayCommandFrameData {
    float forwardMove;
    float leftMove;
    float upMove;
    float pitch;
    float yaw;
    float roll;
    unsigned long long buttons;
    unsigned long long buttons1;
    unsigned long long buttons2;
    int mouseDx;
    int mouseDy;
    int weaponSelect;
    unsigned int fields;
    unsigned char leftHandDesired;
    unsigned char _pad[3];
} PRL_ReplayCommandFrameData;

// 可选的每 tick 移动服务额外状态（48 字节）。
// 对应 C++ 侧 BotController::ReplayMovementExtra。
typedef struct PRL_ReplayMovementExtra {
    unsigned int fields;
    float jumpPressedTime;
    float lastDuckTime;
    int lastActualJumpPressTick;
    float lastActualJumpPressFrac;
    int lastUsableJumpPressTick;
    float lastUsableJumpPressFrac;
    int lastLandedTick;
    float lastLandedFrac;
    float lastLandedVelocityX;
    float lastLandedVelocityY;
    float lastLandedVelocityZ;
} PRL_ReplayMovementExtra;

#pragma pack(pop)

// Bot 配置文件数据（只读快照）。
// 对应 C++ 侧 BotController::BotProfileData。
#pragma pack(push, 4)
typedef struct PRL_BotProfileData {
    float aggression;            // 0..1
    float skill;                 // 0..1
    float teamwork;              // 0..1
    float reactionTime;          // seconds
    float attackDelay;           // seconds
    float lookAccelAtk;          // m_lookAngleMaxAccelAttacking
    float lookStiffAtk;          // m_lookAngleStiffnessAttacking
    float lookDampAtk;           // m_lookAngleDampingAttacking
    int cost;                    // m_cost
    int difficulty;              // m_difficultyFlags (bitmask)
    int weaponPrefCount;         // valid entries in weaponPref
    unsigned short weaponPref[16]; // item def index
} PRL_BotProfileData;
#pragma pack(pop)

// 锁定类型枚举（对应 BotController::LockKind）。
typedef enum PRL_LockKind {
    PRL_LockKind_All = 0,     // Update + Upkeep 全锁
    PRL_LockKind_Aim = 1,     // 仅 Upkeep（视角锁）
    PRL_LockKind_Weapon = 2,  // 武器锁
} PRL_LockKind;

// ---- 录制 API ----
// 所有函数返回 int：1 = 成功，0 = 失败。
// 使用 int 而非 bool，因为 C 的 bool 跨语言 ABI 不一致（C99 <stdbool.h>
// 与 C++ bool 大小可能不同），C# P/Invoke 侧统一用 int 接收更稳妥。

// 开始录制。slot: 玩家槽位 [0,63]。清空旧缓冲区并设置录制标志。
PRL_API int PRL_StartRecord(int slot);
// 停止录制，冻结缓冲区（不清空数据，供 PRL_GetRecordedMotion 读取）。
PRL_API int PRL_StopRecord(int slot);
// 返回已录制的 tick 数量。<0 表示 slot 非法。
PRL_API int PRL_GetRecordedTickCount(int slot);
// 获取录制数据。调用方分配 outTicks/outSubs 缓冲区。
// 输入时 *outTickCount / *outSubCount 为缓冲区容量，返回时为实际写入元素数。
// 返回 1 成功，0 失败（slot 非法或指针为空）。
// 即使返回 1，*outTickCount / *outSubCount 也可能为 0（无录制数据）。
PRL_API int PRL_GetRecordedMotion(int slot,
                                  PRL_ReplayTick *outTicks, int *outTickCount,
                                  PRL_SubtickMove *outSubs, int *outSubCount);

// ---- 回放 API ----
// 加载录制数据到 slot 的回放缓冲区。回放进行中拒绝加载（返回 0）。
// ticks/tickCount: 录制数据；subs/subCount: 并行 subtick 数据（subs 可为
// null，但此时 subCount 必须为 0）。
PRL_API int PRL_LoadReplay(int slot,
                           const PRL_ReplayTick *ticks, int tickCount,
                           const PRL_SubtickMove *subs, int subCount);
// 注册回放目标 pawn。pawn: CCSPlayerPawn* 指针（由 C# 侧传入 Handle）。
// 注册前会校验 pawn 的 identity 与 controller 归属。
PRL_API int PRL_SetReplayPawn(int slot, void *pawn);
// 开始回放。loop: 非 0 时到末尾后循环回到 tick 0。
// 要求缓冲区非空，否则返回 0。
PRL_API int PRL_StartReplay(int slot, int loop);
// 停止回放（不清空缓冲区，不清除 pawn 注册）。
PRL_API int PRL_StopReplay(int slot);
// 查询 slot 是否正在回放。
PRL_API int PRL_IsReplaying(int slot);
// 冻结 slot 的 bot AI（回放结束后调用，阻止 AI 激活）。
// 设置后 CCSBot::Update hook 继续设置 kBot_AiTickedFlag=1，bot 保持静止。
PRL_API int PRL_FreezeSlot(int slot);
// 解冻 slot 的 bot AI（踢出 bot 前调用，恢复 AI）。
PRL_API int PRL_UnfreezeSlot(int slot);
// 获取当前回放 tick 的武器 def index。未回放或 slot 非法返回 -1。
// 供 C# 侧 OnTick 检查是否需要切换 bot 武器。
PRL_API int PRL_GetCurrentReplayWeaponDef(int slot);
// 获取 bot 当前 active weapon 的 def index（通过 GetSlot 遍历槽位匹配）。
// WeaponSwitcher 未安装或 slot 未缓存返回 -1。
// 供 C# 侧诊断：对比录制 def 与 bot active def，验证武器切换是否生效。
PRL_API int PRL_GetBotActiveWeaponDef(int slot);

// ---- 诊断 ----
// 诊断计数器结构。用于排查回放不移动问题。
// 所有字段为原子计数器快照，反映自插件加载以来各 hook 注入点的调用次数。
typedef struct PRL_DiagnosticCounters {
    unsigned long long processMovementCalls;     // ProcessMovement hook 总调用次数
    unsigned long long finishMoveCalls;           // FinishMove hook 总调用次数
    unsigned long long physicsSimulateCalls;      // PhysicsSimulate hook 总调用次数
    unsigned long long replayPreCalls;            // OnReplayPre 调用次数
    unsigned long long replayFinishMoveCalls;     // OnReplayFinishMove 调用次数
    unsigned long long replayCommitCalls;         // OnReplayCommit 调用次数
    unsigned long long replayPrePawnNull;         // OnReplayPre 中 ResolveReplayPawn 返回 null 次数
    unsigned long long replayCommitPawnNull;      // OnReplayCommit 中 ResolveReplayPawn 返回 null 次数
    int physicsActive;                            // PhysicsSimulate hook 是否激活（1=是 0=否）
    int subtickActive;                            // PlayerRunCommand hook 是否激活
    int lastProcessMovementSlot;                  // 最后一次 ProcessMovement 的 slot
    int lastPhysicsSimulateSlot;                  // 最后一次 PhysicsSimulate 的 slot
    int lastReplaySlot;                           // 最后一次回放调用的 slot
    int lastReplayCursor;                         // 最后一次回放的 cursor

    // AI 层与 user command 层调用次数（判断 bot 是否还在生成 user command）
    unsigned long long ccsbotUpdateCalls;         // CCSBot::Update hook 调用次数
    unsigned long long ccsbotUpkeepCalls;         // CCSBot::Upkeep hook 调用次数
    unsigned long long playerRunCommandCalls;     // PlayerRunCommand hook 调用次数

    // UpdateLookAngles / SetEyeAngles 调用次数
    // 用于排查回放期间引擎是否仍在尝试覆盖 bot 视角
    unsigned long long ccsbotUpdateLookAnglesCalls; // CCSBot::UpdateLookAngles hook 调用次数
    unsigned long long setEyeAnglesCalls;           // CCSPlayerPawn::SetEyeAngles hook 调用次数

    // 位置读回验证（OnReplayCommit 写入场景节点 origin 后立即读回）
    // 若 writtenX != readBackX，说明写入失败（SEH 拦截 / 地址无效）
    // 若 readBackX 在多个 tick 间不变，说明引擎未使用写入的位置
    float lastWrittenOriginX;                     // 最后一次写入的 origin X
    float lastWrittenOriginY;                     // 最后一次写入的 origin Y
    float lastWrittenOriginZ;                     // 最后一次写入的 origin Z
    float lastReadBackOriginX;                    // 读回的 origin X
    float lastReadBackOriginY;                    // 读回的 origin Y
    float lastReadBackOriginZ;                    // 读回的 origin Z
    unsigned int lastPawnIdentity;                // pawn->identity（每次 commit 刷新）
    int lastPawnMoveType;                         // pawn->m_MoveType（验证写入）
} PRL_DiagnosticCounters;

// 读取诊断计数器快照。返回 1 成功，0 失败（指针为空）。
PRL_API int PRL_GetDiagnosticCounters(PRL_DiagnosticCounters *out);

// ---- 扩展 API（v0.2.1+，对应 BotController v0.6.1 ABI 18）----

// ABI 版本号。返回 18。
PRL_API int PRL_GetAbiVersion(void);

// 加载扩展回放数据（含命令帧 + 移动额外状态）。返回 1 成功，0 失败。
PRL_API int PRL_LoadReplayExtended(int slot,
                                   const PRL_ReplayTick *ticks, int tickCount,
                                   const PRL_SubtickMove *subs, int subCount,
                                   const PRL_ReplayCommandFrameData *commands, int commandCount,
                                   const PRL_ReplayMovementExtra *movementExtras, int movementExtraCount);

// 当前回放 tick 索引。<0 表示未在回放。
PRL_API int PRL_GetReplayCursor(int slot);
// 回放缓冲区总 tick 数。
PRL_API int PRL_GetReplayTotal(int slot);
// 拷贝当前正在回放的 tick。返回 1 成功，0 失败（未回放或指针空）。
PRL_API int PRL_GetReplayTick(int slot, PRL_ReplayTick *out);

// 切换 bot 武器到指定 def index。返回 1 成功，0 失败。
PRL_API int PRL_SwitchBotWeapon(int slot, int defIndex);

// 注入 usercmd 按钮输入（独立可取消）。返回 injectionId，<0 失败。
PRL_API long long PRL_InjectUsercmd(int slot, unsigned long long buttonMask, int durationMs);
// 取消一个 usercmd 注入。返回 1 成功，0 失败。
PRL_API int PRL_CancelUsercmdInjection(int slot, long long injectionId);
// 抑制指定 usercmd 按钮一段时长。返回 1 成功，0 失败。
PRL_API int PRL_SuppressUsercmd(int slot, unsigned long long buttonMask, int durationMs);

// ---- Buy plan ----
// 设置 bot 购买计划（空格/逗号分隔的别名列表）。返回 0 成功，-2 slot 非法。
PRL_API int PRL_SetBuyPlan(int slot, const char *aliases);
// 标记 slot 本回合不购买。返回 0 成功，-2 slot 非法。
PRL_API int PRL_SetBuySkip(int slot);
// 清除 slot 的购买计划。返回 0 成功，-2 slot 非法。
PRL_API int PRL_ClearBuyPlan(int slot);

// ---- Bot profile ----
// 读取 bot 的 BotProfile 数据。返回 1 成功，0 失败（无 bot 或指针空）。
PRL_API int PRL_GetProfile(int slot, PRL_BotProfileData *out);

// ---- Voice ----
// 查询语音发送是否可用。返回 1 可用，0 不可用。
PRL_API int PRL_CanSendVoice(void);
// 发送一帧编码 Opus 语音到指定接收者。返回 0 成功，<0 失败。
PRL_API int PRL_SendVoiceFrame(int recipientSlot,
                               int senderClient,
                               unsigned long long senderXuid,
                               const unsigned char *audio,
                               int audioBytes,
                               int sampleRate,
                               float voiceLevel,
                               int sequenceBytes,
                               int sectionNumber,
                               int uncompressedSampleOffset,
                               unsigned int numPackets,
                               const unsigned int *packetOffsets,
                               int packetOffsetCount,
                               int tick,
                               int audibleMask);

// ---- Lock / Unlock ----
// 锁定 slot 的指定类型。arg 仅 Weapon 类型使用（LockTarget int）。
// 返回 0 成功，-2 slot 非法，-3 正在回放。
PRL_API int PRL_Lock(int slot, int kind, int arg);
// 解锁 slot 的指定类型。返回 0 成功，-2 slot 非法。
PRL_API int PRL_Unlock(int slot, int kind);

#ifdef __cplusplus
} // extern "C"
#endif
