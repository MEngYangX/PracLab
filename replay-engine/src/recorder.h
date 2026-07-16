// 录制引擎接口。
// 负责：
//   1. 在 ProcessMovement pre/post、PhysicsSimulate 边界捕获 MovementSnapshot
//   2. 在 PlayerRunCommand 中暂存 SubtickMove 序列
//   3. 按 tick 组织为 ReplayTick 序列，供回放引擎（Task 5）或外部读取
//
// 数据结构（MovementSnapshot/ReplayTick/SubtickMove）的内存布局是跨语言
// ABI 契约：C# 侧（Task 8）通过 P/Invoke 以相同布局读取，修改字段顺序或
// 类型会破坏兼容性。

#pragma once

#include <cstdint>
#include <vector>
#include <atomic>
#include <mutex>

namespace PracLab::ReplayEngine
{
    // 一帧移动快照。在 ProcessMovement 前（pre）和后（post）各捕获一次。
    // 内存布局必须与 C# 侧 MovementSnapshot 结构一致（Pack=4）。
    #pragma pack(push, 4)
    struct MovementSnapshot
    {
        float originX, originY, originZ;      // 场景节点 m_vecAbsOrigin
        float velX, velY, velZ;               // m_vecAbsVelocity
        float pitch, yaw, roll;               // 视角
        uint32_t entityFlags;                 // m_fFlags
        uint8_t moveType;                     // m_MoveType
        uint8_t _pad[3];                      // 4 字节对齐
        uint64_t buttons;                     // m_nButtons states[0]
        uint64_t buttons1;                    // states[1]
        uint64_t buttons2;                    // states[2]
        float duckAmount;                     // m_flDuckAmount
        float duckSpeed;                      // m_flDuckSpeed
        float ladderNormalX, ladderNormalY, ladderNormalZ;  // m_vecLadderNormal
        uint8_t ducked, ducking, desiresDuck; // 蹲下状态机
        uint8_t actualMoveType;               // m_nActualMoveType
    };

    // 一个录制的服务器 tick。
    // pre/post 分别为 ProcessMovement 前/后的快照；numSubtick 指示本 tick
    // 关联的 subtick 输入数量，这些 subtick 存储在并行的 SubtickMove 缓冲区中。
    struct ReplayTick
    {
        MovementSnapshot pre;     // ProcessMovement 前
        MovementSnapshot post;    // ProcessMovement 后
        int32_t weaponDefIndex;   // 当前武器（-1 = 无）
        uint32_t numSubtick;      // subtick 数量
    };

    // 一个 subtick 输入步。
    // 对应 CSubtickMoveStep（protobuf），在 tick 内的 [0,1) 时间点触发。
    struct SubtickMove
    {
        float when;              // tick 内时间点 [0,1)
        uint32_t button;         // 按钮位（0 = 模拟量输入）
        float pressed;           // 按下/释放（1.0=按下 0.0=释放）
        float analogForward;     // 前进模拟量
        float analogLeft;        // 左移模拟量
        float pitchDelta;        // 俯仰增量
        float yawDelta;          // 偏航增量
    };
    #pragma pack(pop)

    namespace Recorder
    {
        // CS2 最大玩家槽位（与 MaxPlayers 一致）。
        constexpr int kMaxSlots = 64;
        // 单 tick 允许的最大 subtick 数量（与引擎 CUserCmd 上限一致）。
        constexpr int kMaxSubtickPerTick = 36;

        // ---- 录制控制 ----
        // 清空旧缓冲区并开始录制。slot: [0,63]。
        bool StartRecord(int slot);
        // 停止录制，冻结缓冲区（不清空数据，供 CopyTicks 读取）。
        bool StopRecord(int slot);
        // 查询 slot 是否正在录制。
        bool IsRecording(int slot);
        // 返回已录制的 tick 数量，<0 表示 slot 非法。
        int RecordedTickCount(int slot);
        // 返回已录制的 subtick 总数，<0 表示 slot 非法。
        int RecordedSubtickCount(int slot);

        // ---- hook 注入点（由 hooks.cpp 调用）----
        // ProcessMovement pre: 捕获 pre 快照暂存到 pendingPre。
        // services: CCSPlayer_MovementServices*；moveData: CMoveData*（pre 不使用）。
        void OnCapturePre(int slot, void *services, void *moveData);
        // ProcessMovement post: 捕获 post 快照 + 提交 ReplayTick + 追加 pendingSubs。
        // moveData 非 null 时用其 m_vecAbsOrigin 覆盖场景节点原点（更精确）。
        void OnCapturePost(int slot, void *services, void *moveData);
        // PlayerRunCommand: 暂存本 tick 的 subtick moves 到 pendingSubs。
        // moves: SubtickMove 数组；count: 元素数（上限 kMaxSubtickPerTick）。
        void OnCaptureSubticks(int slot, const SubtickMove *moves, int count);

        // ---- 武器追踪 ----
        // 记录 slot 当前持有的 WeaponServices* 指针。
        void SetLiveWs(int slot, void *ws);
        // 读取 slot 当前 WeaponServices* 指针。
        void *LiveWs(int slot);
        // 更新 slot 当前武器 def index（由武器切换 hook 调用）。
        void SetCurrentDef(int slot, int defIndex);

        // ---- 数据输出 ----
        // 拷贝录制数据到调用方缓冲区，返回写入的元素数。
        // out: 输出缓冲区；maxTicks: 缓冲区容量。
        int CopyTicks(int slot, ReplayTick *out, int maxTicks);
        // out: 输出缓冲区；maxSubticks: 缓冲区容量。
        int CopySubticks(int slot, SubtickMove *out, int maxSubticks);

        // 清空所有 slot 的录制数据（插件卸载时调用）。
        void ClearAll();
    }
}
