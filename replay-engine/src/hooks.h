// 引擎函数 Hook 模块接口。
// 负责：
//   1. 通过 funchook 拦截 ProcessMovement / PhysicsSimulate / CCSBot::Update /
//      CCSBot::Upkeep / CCSBot::UpdateLookAngles / CCSPlayerPawn::SetEyeAngles
//   2. 在首次 ProcessMovement 调用时从 vtable 懒加载 FinishMove / PlayerRunCommand hook
//   3. 维护 slot -> services 缓存，供 PhysicsSimulate 提交时反查
//   4. 维护 slot -> replay pawn 映射，供回放引擎（Task 5）注册目标 pawn
//   5. 提供 ApplyReplayEyeAngles 接口，回放期间通过原 SetEyeAngles 写入视角
//      （临时清除 FL_FAKECLIENT 绕过引擎早退路径）
//
// 录制（Task 4）和回放（Task 5）逻辑通过注入点接入，本模块仅实现 hook 框架。
//
// 仿照 CS2-Bot-Controller 的设计：
//   - CCSBot::Update 回放时设置 kBot_AiTickedFlag=1，告诉引擎 "AI 已执行"，
//     同时跳过 AI 决策逻辑，但引擎仍会为 bot 生成 user command 并调用
//     ProcessMovement，确保回放 hook 链每 tick 都被触发。
//   - SetEyeAngles 回放时跳过，改用 ApplyReplayEyeAngles 主动调用原函数
//     （临时清除 FL_FAKECLIENT 标志绕过引擎的 fake-client 早退路径）。

#pragma once

#include <cstddef>
#include <cstdint>

#include <nlohmann/json.hpp>

#include "sig_scan.h"

namespace PracLab::ReplayEngine::Hooks
{
    // CS2 最大玩家槽位（与 MaxPlayers 一致）。
    constexpr int kMaxSlots = 64;

    // 安装所有引擎 hook。
    // 必装的：ProcessMovement（失败则整体失败）
    // 可选的：PhysicsSimulate、CCSBot::Update、CCSBot::Upkeep、
    //         CCSBot::UpdateLookAngles、CCSPlayerPawn::SetEyeAngles（失败降级，不阻塞插件）
    // FinishMove 与 PlayerRunCommand 不在此处安装，而是在首次 ProcessMovement
    // 调用时由 EnsureVtableHooks 从 services vtable 懒加载（保证索引正确）。
    // gd: 已加载的 gamedata.json；serverModule: server 模块信息；
    // errorOut/maxlen: 失败时写入错误描述。
    bool Install(const nlohmann::json &gd, const Sig::ModuleInfo &serverModule,
                 char *errorOut, size_t errorOutLen);

    // 卸载所有已安装的 hook 并清理状态。
    // 重复调用安全。
    void Remove();

    // 返回当前 hook 状态描述（用于日志）。
    const char *Status();

    // PhysicsSimulate hook 是否已激活。
    // false 时录制/回放在每 subtick 边界提交，可能造成抖动。
    bool IsPhysicsActive();

    // PlayerRunCommand hook 是否已激活（subtick 录制/注入就绪）。
    bool IsSubtickActive();

    // 注册回放目标 pawn（供 Task 5 调用）。
    // slot: 目标槽位；pawn: CCSPlayerPawn* 指针。
    // 注册前会校验 pawn 的 identity 与 controller 归属。
    bool SetReplayPawn(int slot, void *pawn);

    // 清除已注册的回放 pawn。
    // slot: 目标槽位。
    void ClearReplayPawn(int slot);

    // 解析并验证 slot 对应的回放 pawn。
    // 先查注册表，再回退到 services 内嵌的 pawn 字段。
    // 返回 nullptr 表示当前 services 不属于注册的回放 pawn。
    void *ResolveReplayPawn(int slot, void *services);

    // 通过原 SetEyeAngles 写入视角（供 replayer.cpp 调用）。
    // 临时清除 controller 的 FL_FAKECLIENT 标志绕过引擎早退路径。
    // pawn: 目标 CCSPlayerPawn*；pitch/yaw: 视角角度（度）。
    // 返回 false 表示 hook 未就绪或 pawn 无效，调用方应回退到直接写字段。
    bool ApplyReplayEyeAngles(void *pawn, float pitch, float yaw);

    // ---- 诊断接口（供 PRL_GetDiagnosticCounters 读取）----
    // 返回各 hook 的调用次数，用于排查回放不移动问题。
    uint64_t ProcessMovementCalls();
    uint64_t FinishMoveCalls();
    uint64_t PhysicsSimulateCalls();
    int      LastProcessMovementSlot();
    int      LastPhysicsSimulateSlot();

    // 新增诊断：CCSBot::Update/Upkeep 调用次数（判断 AI 是否运行）；
    // PlayerRunCommand 调用次数（判断 user command 是否被处理）
    uint64_t CCSBotUpdateCalls();
    uint64_t CCSBotUpkeepCalls();
    uint64_t PlayerRunCommandCalls();

    // 新增诊断：UpdateLookAngles / SetEyeAngles 调用次数
    // 用于排查回放期间引擎是否仍在尝试覆盖 bot 视角
    uint64_t CCSBotUpdateLookAnglesCalls();
    uint64_t SetEyeAnglesCalls();

    // 冻结/解冻 slot 的 bot AI。
    // 冻结后 CCSBot::Update/Upkeep/UpdateLookAngles hook 继续跳过 AI，
    // bot 保持静止。用于回放结束后阻止 AI 激活。
    void SetSlotFrozen(int slot, bool frozen);
}
