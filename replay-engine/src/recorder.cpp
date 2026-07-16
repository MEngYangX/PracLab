// 录制引擎实现。
// 在 ProcessMovement pre/post、PlayerRunCommand、PhysicsSimulate hook 中
// 捕获玩家移动状态，按 tick 组织为 ReplayTick 序列，供回放引擎（Task 5）回放。
//
// 数据流：
//   OnCaptureSubticks (PlayerRunCommand) → 暂存 pendingSubs
//   OnCapturePre (ProcessMovement/PhysicsSimulate pre) → 暂存 pendingPre
//   OnCapturePost (ProcessMovement/PhysicsSimulate post) → 提交 ReplayTick + 追加 subs
//
// 线程安全：
//   ticks/subs/pendingSubs/pendingPre/havePre 由 RecordState::mu 保护；
//   recording/liveWs/currentDef 用 std::atomic 跨线程访问。

#include "recorder.h"
#include "memory.h"
#include "version_targets.h"
#include "hooks.h"
#include "platform.h"

#include <array>
#include <cstdio>
#include <cstring>
#include <mutex>

namespace tg = PracLab::ReplayEngine::targets;

namespace PracLab::ReplayEngine::Recorder
{
    namespace
    {
        // 单个 slot 的录制状态。
        // ticks/subs: 已提交的录制数据，按 tick 顺序追加。
        // pendingSubs: PlayerRunCommand 暂存的 subtick，等待 OnCapturePost 提交。
        // pendingPre/havePre: OnCapturePre 暂存的 pre 快照，等待 OnCapturePost 配对。
        struct RecordState
        {
            std::atomic<bool> recording{false};
            std::vector<ReplayTick> ticks;
            std::vector<SubtickMove> subs;
            std::vector<SubtickMove> pendingSubs;  // PlayerRunCommand 暂存的 subtick
            MovementSnapshot pendingPre{};         // 待提交的 pre 快照
            bool havePre{false};
            std::atomic<void *> liveWs{nullptr};   // 当前 WeaponServices*
            std::atomic<int> currentDef{-1};       // 当前武器 def index
            std::mutex mu;                          // 保护 ticks/subs/pending/pre
        };

        // 全局录制状态数组（每个 slot 一份）。
        std::array<RecordState, kMaxSlots> g_rec;

        // 判断 slot 是否在合法范围。
        bool ValidSlot(int s) { return s >= 0 && s < kMaxSlots; }

        // 一次性读取 3 个 float，减少内存访问次数。
        // base: 起始地址；offset: 相对偏移；x/y/z: 输出三个分量。
        bool ReadVector3(void *base, int offset, float &x, float &y, float &z)
        {
            float values[3] = {};
            if (!Mem::TryReadMemory(base, offset, values, sizeof(values)))
                return false;
            x = values[0];
            y = values[1];
            z = values[2];
            return true;
        }

        // 通过 CBodyComponent 解析实体的场景节点。
        // entity -> m_CBodyComponent -> m_pSceneNode
        // 返回 nullptr 表示解析失败（实体可能已被回收）。
        void *ResolveSceneNode(void *entity)
        {
            if (!entity)
                return nullptr;
            void *body = nullptr;
            if (!Mem::SafeRead(entity, tg::kEnt_BodyComponent, body) || !body)
                return nullptr;
            void *node = nullptr;
            return Mem::SafeRead(body, tg::kBody_SceneNode, node) ? node : nullptr;
        }

        // 从 services -> pawn 读取所有字段到 MovementSnapshot。
        // slot: 用于通过 Hooks::ResolveReplayPawn 解析 pawn；
        // services: CCSPlayer_MovementServices*；
        // out: 输出快照。
        // 返回 false 表示任一关键字段读取失败（调用方应丢弃本次快照）。
        bool ReadSnapshot(int slot, void *services, MovementSnapshot &out)
        {
            if (!services)
                return false;

            // 通过 Hooks 解析当前 services 对应的 pawn
            void *pawn = Hooks::ResolveReplayPawn(slot, services);
            if (!pawn)
                return false;

            void *node = ResolveSceneNode(pawn);
            if (!node)
                return false;

            // 先清零，避免失败时残留脏数据
            out = MovementSnapshot{};

            // 逐字段读取：任一失败即放弃本次快照
            // 读取顺序：pawn 基本属性 → services 按钮状态 → services 蹲伏/梯子 → pawn 视角 → 节点位置
            return ReadVector3(pawn, tg::kEnt_AbsVelocity,
                               out.velX, out.velY, out.velZ) &&
                   Mem::SafeRead(pawn, tg::kEnt_Flags, out.entityFlags) &&
                   Mem::SafeRead(pawn, tg::kEnt_MoveType, out.moveType) &&
                   Mem::SafeRead(pawn, tg::kEnt_ActualMoveType, out.actualMoveType) &&
                   Mem::SafeRead(services, tg::kServices_Buttons, out.buttons) &&
                   Mem::SafeRead(services, tg::kServices_Buttons1, out.buttons1) &&
                   Mem::SafeRead(services, tg::kServices_Buttons2, out.buttons2) &&
                   Mem::SafeRead(services, tg::kServices_DuckAmount, out.duckAmount) &&
                   Mem::SafeRead(services, tg::kServices_DuckSpeed, out.duckSpeed) &&
                   ReadVector3(services, tg::kServices_LadderNormal,
                               out.ladderNormalX, out.ladderNormalY, out.ladderNormalZ) &&
                   Mem::SafeRead(services, tg::kServices_Ducked, out.ducked) &&
                   Mem::SafeRead(services, tg::kServices_Ducking, out.ducking) &&
                   Mem::SafeRead(services, tg::kServices_DesiresDuck, out.desiresDuck) &&
                   ReadVector3(pawn, tg::kPawn_ViewAngle,
                               out.pitch, out.yaw, out.roll) &&
                   ReadVector3(node, tg::kNode_AbsOrigin,
                               out.originX, out.originY, out.originZ);
        }
    } // namespace

    // ============================================================
    // 录制控制
    // ============================================================

    bool StartRecord(int slot)
    {
        if (!ValidSlot(slot))
            return false;
        RecordState &r = g_rec[slot];
        {
            std::lock_guard<std::mutex> lk(r.mu);
            r.ticks.clear();
            r.subs.clear();
            r.pendingSubs.clear();
            r.havePre = false;
            r.pendingPre = MovementSnapshot{};
            // 预分配容量：约 64 秒 @ 64 tick
            r.ticks.reserve(4096);
            r.subs.reserve(4096);
        }
        r.currentDef.store(-1, std::memory_order_relaxed);
        r.liveWs.store(nullptr, std::memory_order_relaxed);
        r.recording.store(true, std::memory_order_release);
        return true;
    }

    bool StopRecord(int slot)
    {
        if (!ValidSlot(slot))
            return false;
        g_rec[slot].recording.store(false, std::memory_order_release);
        return true;
    }

    bool IsRecording(int slot)
    {
        return ValidSlot(slot) &&
               g_rec[slot].recording.load(std::memory_order_acquire);
    }

    int RecordedTickCount(int slot)
    {
        if (!ValidSlot(slot))
            return -1;
        RecordState &r = g_rec[slot];
        std::lock_guard<std::mutex> lk(r.mu);
        return static_cast<int>(r.ticks.size());
    }

    int RecordedSubtickCount(int slot)
    {
        if (!ValidSlot(slot))
            return -1;
        RecordState &r = g_rec[slot];
        std::lock_guard<std::mutex> lk(r.mu);
        return static_cast<int>(r.subs.size());
    }

    // ============================================================
    // hook 注入点
    // ============================================================

    void OnCapturePre(int slot, void *services, void *moveData)
    {
        // pre 快照不依赖 moveData
        (void)moveData;

        if (!ValidSlot(slot) || !services)
            return;
        RecordState &r = g_rec[slot];
        if (!r.recording.load(std::memory_order_acquire))
            return;

        MovementSnapshot pre{};
        if (!ReadSnapshot(slot, services, pre))
            return;

        std::lock_guard<std::mutex> lk(r.mu);
        r.pendingPre = pre;
        r.havePre = true;
    }

    void OnCapturePost(int slot, void *services, void *moveData)
    {
        if (!ValidSlot(slot) || !services)
            return;
        RecordState &r = g_rec[slot];
        if (!r.recording.load(std::memory_order_acquire))
            return;

        // 读取 post 快照
        MovementSnapshot post{};
        if (!ReadSnapshot(slot, services, post))
            return;

        // 若 moveData 可用，用 CMoveData 的 m_vecAbsOrigin 覆盖场景节点原点。
        // 原因：FinishMove 提交前场景节点 origin 可能尚未更新，
        // CMoveData 中的 m_vecAbsOrigin 是 TryPlayerMove 积分后的最终位置。
        if (moveData)
        {
            ReadVector3(moveData, tg::kMove_AbsOrigin,
                        post.originX, post.originY, post.originZ);
        }

        // 读取当前武器 def index
        int def = r.currentDef.load(std::memory_order_relaxed);

        std::lock_guard<std::mutex> lk(r.mu);
        ReplayTick t{};
        // 若缺失 pre 快照（例如首帧 PhysicsSimulate 未先触发 pre），用 post 兜底
        t.pre = r.havePre ? r.pendingPre : post;
        t.post = post;
        t.weaponDefIndex = def;
        t.numSubtick = static_cast<uint32_t>(r.pendingSubs.size());

        // 将本 tick 的 subtick 追加到全局 subtick 缓冲区
        for (const auto &sm : r.pendingSubs)
            r.subs.push_back(sm);

        r.ticks.push_back(t);
        r.pendingSubs.clear();
        r.havePre = false;
    }

    void OnCaptureSubticks(int slot, const SubtickMove *moves, int count)
    {
        if (!ValidSlot(slot) || !moves || count < 0)
            return;
        RecordState &r = g_rec[slot];
        if (!r.recording.load(std::memory_order_acquire))
            return;
        if (count > kMaxSubtickPerTick)
            count = kMaxSubtickPerTick;

        std::lock_guard<std::mutex> lk(r.mu);
        // 清空上一 tick 残留的 pendingSubs（防御性：正常流程 OnCapturePost 已清空）
        r.pendingSubs.clear();
        for (int i = 0; i < count; ++i)
            r.pendingSubs.push_back(moves[i]);
    }

    // ============================================================
    // 武器追踪
    // ============================================================

    void SetLiveWs(int slot, void *ws)
    {
        if (ValidSlot(slot))
            g_rec[slot].liveWs.store(ws, std::memory_order_relaxed);
    }

    void *LiveWs(int slot)
    {
        return ValidSlot(slot)
                   ? g_rec[slot].liveWs.load(std::memory_order_relaxed)
                   : nullptr;
    }

    void SetCurrentDef(int slot, int defIndex)
    {
        if (ValidSlot(slot))
            g_rec[slot].currentDef.store(defIndex, std::memory_order_relaxed);
    }

    // ============================================================
    // 数据输出
    // ============================================================

    int CopyTicks(int slot, ReplayTick *out, int maxTicks)
    {
        if (!ValidSlot(slot) || !out || maxTicks <= 0)
            return 0;
        RecordState &r = g_rec[slot];
        std::lock_guard<std::mutex> lk(r.mu);
        int n = static_cast<int>(r.ticks.size());
        if (n > maxTicks)
            n = maxTicks;
        for (int i = 0; i < n; ++i)
            out[i] = r.ticks[i];
        return n;
    }

    int CopySubticks(int slot, SubtickMove *out, int maxSubticks)
    {
        if (!ValidSlot(slot) || !out || maxSubticks <= 0)
            return 0;
        RecordState &r = g_rec[slot];
        std::lock_guard<std::mutex> lk(r.mu);
        int n = static_cast<int>(r.subs.size());
        if (n > maxSubticks)
            n = maxSubticks;
        for (int i = 0; i < n; ++i)
            out[i] = r.subs[i];
        return n;
    }

    // ============================================================
    // 清理
    // ============================================================

    void ClearAll()
    {
        for (int i = 0; i < kMaxSlots; ++i)
        {
            RecordState &r = g_rec[i];
            r.recording.store(false, std::memory_order_release);
            {
                std::lock_guard<std::mutex> lk(r.mu);
                r.ticks.clear();
                r.subs.clear();
                r.pendingSubs.clear();
                r.pendingPre = MovementSnapshot{};
                r.havePre = false;
            }
            r.liveWs.store(nullptr, std::memory_order_release);
            r.currentDef.store(-1, std::memory_order_release);
        }
        Platform::DebugOut("[PracLabReplayEngine] Recorder: all slots cleared\n");
    }
} // namespace PracLab::ReplayEngine::Recorder
