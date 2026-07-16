// C API 导出实现。
// 本文件实现 include/praclab_replay.h 中声明的 PRL_* 系列 C 函数，将调用
// 委托给 Recorder / Replayer / Hooks 模块。
//
// 设计要点：
//   1. 所有 PRL_* 函数使用 extern "C" + PRL_API 导出，保证 C ABI 兼容，
//      供 CSSharp C# 通过 P/Invoke 调用。
//   2. 函数返回 int（1=成功，0=失败），避免 C/C++ bool 跨语言 ABI 差异。
//   3. PRL_MovementSnapshot / PRL_ReplayTick / PRL_SubtickMove 与 C++ 侧
//      MovementSnapshot / ReplayTick / SubtickMove 内存布局完全一致（均使用
//      #pragma pack(4) + 等价字段类型），因此 reinterpret_cast 转换安全。
//      下方 static_assert 在编译期验证大小一致，防止布局漂移。
//   4. slot 范围校验在 ABI 边界完成，防止非法 slot 穿透到内部模块。

#include "praclab_replay.h"

#include "exports.h"
#include "recorder.h"
#include "replayer.h"
#include "hooks.h"
#include "weapon_switcher.h"

using namespace PracLab::ReplayEngine;

// 编译期验证：C ABI 结构与 C++ 内部结构大小一致。
// 若此处触发断言，说明 praclab_replay.h 与 recorder.h 的字段布局已漂移，
// 须同步修正后再编译。
static_assert(sizeof(PRL_MovementSnapshot) == sizeof(MovementSnapshot),
    "PRL_MovementSnapshot layout mismatch with MovementSnapshot");
static_assert(sizeof(PRL_ReplayTick) == sizeof(ReplayTick),
    "PRL_ReplayTick layout mismatch with ReplayTick");
static_assert(sizeof(PRL_SubtickMove) == sizeof(SubtickMove),
    "PRL_SubtickMove layout mismatch with SubtickMove");

// 校验 slot 是否在有效范围 [0, kMaxSlots)。
static bool ValidSlot(int slot)
{
    return slot >= 0 && slot < Exports::kMaxSlots;
}

// ==================== 录制 API ====================

// 开始录制。
extern "C" PRL_API int PRL_StartRecord(int slot)
{
    return ValidSlot(slot) && Recorder::StartRecord(slot) ? 1 : 0;
}

// 停止录制。
extern "C" PRL_API int PRL_StopRecord(int slot)
{
    return ValidSlot(slot) && Recorder::StopRecord(slot) ? 1 : 0;
}

// 返回已录制的 tick 数量。
extern "C" PRL_API int PRL_GetRecordedTickCount(int slot)
{
    if (!ValidSlot(slot))
    {
        return -1;
    }
    return Recorder::RecordedTickCount(slot);
}

// 获取录制数据到调用方缓冲区。
extern "C" PRL_API int PRL_GetRecordedMotion(int slot,
                                             PRL_ReplayTick *outTicks, int *outTickCount,
                                             PRL_SubtickMove *outSubs, int *outSubCount)
{
    if (!ValidSlot(slot) || !outTicks || !outTickCount || !outSubs || !outSubCount)
    {
        return 0;
    }

    // 输入时 *outTickCount / *outSubCount 为缓冲区容量。
    int tickCap = *outTickCount;
    int subCap = *outSubCount;

    // reinterpret_cast 安全性：两套结构布局由 static_assert 验证一致，
    // 字段顺序与类型等价（见文件头注释），转换无副作用。
    int tickWritten = Recorder::CopyTicks(slot, reinterpret_cast<ReplayTick *>(outTicks), tickCap);
    int subWritten = Recorder::CopySubticks(slot, reinterpret_cast<SubtickMove *>(outSubs), subCap);

    // 返回时写入实际元素数（可能为 0，表示无录制数据）。
    *outTickCount = tickWritten;
    *outSubCount = subWritten;
    return 1;
}

// ==================== 回放 API ====================

// 加载录制数据到回放缓冲区。
extern "C" PRL_API int PRL_LoadReplay(int slot,
                                      const PRL_ReplayTick *ticks, int tickCount,
                                      const PRL_SubtickMove *subs, int subCount)
{
    if (!ValidSlot(slot) || !ticks || tickCount <= 0)
    {
        return 0;
    }
    // subs 可为 null，但此时 subCount 必须为 0（无 subtick 的录制）。
    if (!subs && subCount > 0)
    {
        return 0;
    }
    return Replayer::LoadReplay(slot,
                                reinterpret_cast<const ReplayTick *>(ticks), tickCount,
                                reinterpret_cast<const SubtickMove *>(subs), subCount) ? 1 : 0;
}

// 注册回放目标 pawn。pawn 来自 C# 侧 IntPtr（CCSPlayerPawn*），须非空。
extern "C" PRL_API int PRL_SetReplayPawn(int slot, void *pawn)
{
    return ValidSlot(slot) && pawn && Hooks::SetReplayPawn(slot, pawn) ? 1 : 0;
}

// 开始回放。loop 非 0 时循环。
extern "C" PRL_API int PRL_StartReplay(int slot, int loop)
{
    return ValidSlot(slot) && Replayer::StartReplay(slot, loop != 0) ? 1 : 0;
}

// 停止回放。
extern "C" PRL_API int PRL_StopReplay(int slot)
{
    return ValidSlot(slot) && Replayer::StopReplay(slot) ? 1 : 0;
}

// 查询是否正在回放。
extern "C" PRL_API int PRL_IsReplaying(int slot)
{
    return ValidSlot(slot) && Replayer::IsReplaying(slot) ? 1 : 0;
}

// 冻结 slot 的 bot AI（回放结束后调用，阻止 AI 激活，bot 保持静止）。
extern "C" PRL_API int PRL_FreezeSlot(int slot)
{
    if (!ValidSlot(slot))
        return 0;
    Hooks::SetSlotFrozen(slot, true);
    return 1;
}

// 解冻 slot 的 bot AI（踢出 bot 前调用，恢复 AI）。
extern "C" PRL_API int PRL_UnfreezeSlot(int slot)
{
    if (!ValidSlot(slot))
        return 0;
    Hooks::SetSlotFrozen(slot, false);
    return 1;
}

// 获取当前回放 tick 的武器 def index。
// 供 C# 侧 OnTick 检查是否需要切换 bot 武器。
extern "C" PRL_API int PRL_GetCurrentReplayWeaponDef(int slot)
{
    if (!ValidSlot(slot))
        return -1;
    return Replayer::CurrentReplayWeaponDef(slot);
}

// 获取 bot 当前 active weapon 的 def index。
// 供 C# 侧诊断：对比录制 def 与 bot active def，验证武器切换是否生效。
extern "C" PRL_API int PRL_GetBotActiveWeaponDef(int slot)
{
    if (!ValidSlot(slot))
        return -1;
    return WeaponSwitcher::BotActiveWeaponDef(slot);
}

// ==================== 诊断 API ====================

// 读取诊断计数器快照。
extern "C" PRL_API int PRL_GetDiagnosticCounters(PRL_DiagnosticCounters *out)
{
    if (!out)
        return 0;

    out->processMovementCalls   = Hooks::ProcessMovementCalls();
    out->finishMoveCalls         = Hooks::FinishMoveCalls();
    out->physicsSimulateCalls    = Hooks::PhysicsSimulateCalls();
    out->replayPreCalls          = Replayer::ReplayPreCalls();
    out->replayFinishMoveCalls   = Replayer::ReplayFinishMoveCalls();
    out->replayCommitCalls       = Replayer::ReplayCommitCalls();
    out->replayPrePawnNull       = Replayer::ReplayPrePawnNull();
    out->replayCommitPawnNull    = Replayer::ReplayCommitPawnNull();
    out->physicsActive           = Hooks::IsPhysicsActive() ? 1 : 0;
    out->subtickActive           = Hooks::IsSubtickActive() ? 1 : 0;
    out->lastProcessMovementSlot = Hooks::LastProcessMovementSlot();
    out->lastPhysicsSimulateSlot = Hooks::LastPhysicsSimulateSlot();
    out->lastReplaySlot          = Replayer::LastReplaySlot();
    out->lastReplayCursor        = Replayer::LastReplayCursor();

    // AI 层与 user command 层调用次数
    out->ccsbotUpdateCalls       = Hooks::CCSBotUpdateCalls();
    out->ccsbotUpkeepCalls       = Hooks::CCSBotUpkeepCalls();
    out->playerRunCommandCalls   = Hooks::PlayerRunCommandCalls();

    // 新增：UpdateLookAngles / SetEyeAngles 调用次数
    // 用于排查回放期间引擎是否仍在尝试覆盖 bot 视角
    out->ccsbotUpdateLookAnglesCalls = Hooks::CCSBotUpdateLookAnglesCalls();
    out->setEyeAnglesCalls           = Hooks::SetEyeAnglesCalls();

    // 位置读回验证（OnReplayCommit 写入场景节点 origin 后立即读回）
    out->lastWrittenOriginX      = Replayer::LastWrittenOriginX();
    out->lastWrittenOriginY      = Replayer::LastWrittenOriginY();
    out->lastWrittenOriginZ      = Replayer::LastWrittenOriginZ();
    out->lastReadBackOriginX     = Replayer::LastReadBackOriginX();
    out->lastReadBackOriginY     = Replayer::LastReadBackOriginY();
    out->lastReadBackOriginZ     = Replayer::LastReadBackOriginZ();
    out->lastPawnIdentity        = Replayer::LastPawnIdentity();
    out->lastPawnMoveType        = Replayer::LastPawnMoveType();
    return 1;
}
