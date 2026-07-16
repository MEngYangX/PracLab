// 回放引擎接口。
// 负责：
//   1. 接收录制数据（ReplayTick[] + SubtickMove[]）加载到 slot 的回放缓冲区
//   2. 维护回放状态机（playing / loop / cursor），按 tick 推进
//   3. 在 ProcessMovement pre / FinishMove pre / FinishMove post 或 PhysicsSimulate post
//      注入点将录制快照写回引擎内存（CMoveData / pawn / services / 场景节点）
//   4. 提供查询接口供 PlayerRunCommand hook 读取当前 tick 的视角/按键/subtick/武器
//
// 回放状态机说明：
//   cursor 指向"当前正在回放的 tick"。OnReplayPre / OnReplayFinishMove 写入
//   ticks[cursor] 的 pre/post 快照；OnReplayCommit 在原函数返回后将 cursor
//   推进到 cursor+1，到达末尾时根据 loop 标志循环或停止。
//
// 线程安全：
//   ticks / subs / subOffset 由 ReplayState::mu 保护；
//   playing / loop / cursor 用 std::atomic 跨线程访问。
//   回放 hook 由游戏主线程驱动（单线程访问），mutex 主要防御 LoadReplay /
//   ClearAll 与回放并发的情形。

#pragma once

#include <cstdint>

#include "recorder.h"  // MovementSnapshot / ReplayTick / SubtickMove

namespace PracLab::ReplayEngine::Replayer
{
    // CS2 最大玩家槽位（与 MaxPlayers 一致）。
    constexpr int kMaxSlots = 64;

    // ---- 回放控制 ----
    // 加载录制数据到 slot 的回放缓冲区。
    // ticks/tickCount: 录制的 ReplayTick 数组；subs/subCount: 并行的 SubtickMove 数组。
    // 回放进行中拒绝加载（返回 false）。加载后 cursor=0, playing=false。
    bool LoadReplay(int slot, const ReplayTick *ticks, int tickCount,
                    const SubtickMove *subs, int subCount);
    // 注册回放目标 pawn（委托给 Hooks::SetReplayPawn）。
    // slot: 目标槽位；pawn: CCSPlayerPawn* 指针。
    bool SetReplayPawn(int slot, void *pawn);
    // 开始回放。loop=true 时到末尾后回到 tick 0 循环。
    // 要求缓冲区非空，否则返回 false。
    bool StartReplay(int slot, bool loop);
    // 停止回放，置 playing=false（不清空缓冲区，不清除 pawn 注册）。
    bool StopReplay(int slot);
    // 查询 slot 是否正在回放。
    bool IsReplaying(int slot);
    // 当前回放游标（tick 索引）。未回放或 slot 非法时返回 -1。
    int ReplayCursor(int slot);
    // 已加载的 tick 总数。slot 非法时返回 0。
    int ReplayTotal(int slot);

    // ---- hook 注入点（由 hooks.cpp 调用）----
    // ProcessMovement pre: 将当前 tick 的 pre 快照写入 CMoveData + pawn + services。
    // 在调用原 ProcessMovement 之前调用。
    void OnReplayPre(int slot, void *services, void *moveData);
    // FinishMove pre: 将当前 tick 的 post 快照写入 CMoveData + pawn + 场景节点 + 视角。
    // 在调用原 FinishMove 之前调用。
    void OnReplayFinishMove(int slot, void *services, void *moveData);
    // FinishMove post / PhysicsSimulate post: 重新写入 post 的 moveType/flags，
    // 并推进游标（loop/stop 在此处理）。
    void OnReplayCommit(int slot, void *services);

    // ---- 查询接口（供 hook 中的 PlayerRunCommand 使用）----
    // 获取当前 tick 的完整数据（ticks[cursor]）。未回放或越界返回 false。
    bool CurrentReplayTick(int slot, ReplayTick &out);
    // 获取当前 tick 的视角快照（ticks[cursor].pre），用于设置 cmd viewangles。
    bool ReplayCommandViewSnapshot(int slot, MovementSnapshot &out);
    // 获取当前 tick 的按键状态（pre.buttons / buttons1 / buttons2）。
    bool CurrentReplayInputButtons(int slot, uint64_t &b0, uint64_t &b1, uint64_t &b2);
    // 拷贝当前 tick 的 subtick moves 到 out，返回拷贝数量。
    // 返回 -1 表示未回放或 slot 非法。
    int CurrentReplaySubticks(int slot, SubtickMove *out, int maxOut);
    // 当前 tick 的武器 def index。未回放返回 -1。
    int CurrentReplayWeaponDef(int slot);

    // 清空所有 slot 的回放数据（插件卸载时调用）。
    void ClearAll();

    // ---- 诊断接口（供 PRL_GetDiagnosticCounters 读取）----
    // 返回各 hook 注入点的调用次数与 pawn 解析失败次数，用于排查回放不移动问题。
    uint64_t ReplayPreCalls();
    uint64_t ReplayFinishMoveCalls();
    uint64_t ReplayCommitCalls();
    uint64_t ReplayPrePawnNull();
    uint64_t ReplayCommitPawnNull();
    int      LastReplaySlot();
    int      LastReplayCursor();

    // 位置读回验证：对比 OnReplayCommit 写入值与读回值，判断写入是否生效
    float    LastWrittenOriginX();
    float    LastWrittenOriginY();
    float    LastWrittenOriginZ();
    float    LastReadBackOriginX();
    float    LastReadBackOriginY();
    float    LastReadBackOriginZ();
    uint32_t LastPawnIdentity();
    int      LastPawnMoveType();
}
