// 回放引擎实现。
// 接收录制引擎（Task 4）产出的 ReplayTick[] / SubtickMove[] 数据，按 tick 顺序
// 将快照写回引擎内存，复现玩家移动轨迹。
//
// 数据流：
//   LoadReplay           → 拷贝 ticks/subs + 构建 subOffset 前缀和
//   StartReplay          → playing=true, cursor=0
//   PlayerRunCommand     → 通过查询接口读取当前 tick 的视角/按键/subtick
//   OnReplayPre          → 写入 pre 快照到 CMoveData + pawn + services
//   OnReplayFinishMove   → 写入 post 快照到 CMoveData + pawn + 场景节点 + 视角
//   OnReplayCommit       → 重写 post moveType/flags + 推进 cursor（loop/stop）
//
// 线程安全：
//   ticks/subs/subOffset 由 ReplayState::mu 保护；
//   playing/loop/cursor 用 std::atomic 跨线程访问。
//   回放 hook 由游戏主线程驱动（单线程访问），mutex 主要防御 LoadReplay /
//   ClearAll 与回放并发的情形。

#include "replayer.h"
#include "memory.h"
#include "version_targets.h"
#include "hooks.h"
#include "platform.h"

#include <array>
#include <atomic>
#include <mutex>
#include <vector>

namespace tg = PracLab::ReplayEngine::targets;

namespace PracLab::ReplayEngine::Replayer
{
    // ---- 诊断计数器（供 PRL_GetDiagnosticCounters 读取）----
    // 用于排查回放不移动的问题：确认各 hook 注入点是否被调用、pawn 是否解析成功
    std::atomic<uint64_t> g_replayPreCalls{0};
    std::atomic<uint64_t> g_replayFinishMoveCalls{0};
    std::atomic<uint64_t> g_replayCommitCalls{0};
    std::atomic<uint64_t> g_replayPrePawnNull{0};      // OnReplayPre 中 ResolveReplayPawn 返回 null
    std::atomic<uint64_t> g_replayCommitPawnNull{0};   // OnReplayCommit 中 ResolveReplayPawn 返回 null
    std::atomic<int>      g_lastReplaySlot{-1};         // 最后一次回放调用的 slot
    std::atomic<int>      g_lastReplayCursor{-1};       // 最后一次回放的 cursor

    // ---- 位置读回验证（排查 OnReplayCommit 写入是否真正生效）----
    // 写入场景节点 origin 后立即读回，对比写入值与读回值：
    //   1. 若读回值 == 写入值：写入成功，位置应已定格
    //   2. 若读回值 != 写入值：写入失败（SEH 拦截 / 地址错误 / 引擎已覆盖）
    //   3. 若读回值与上一 tick 的写入值相同：引擎未推进位置，bot 可能已死
    std::atomic<float>    g_lastWrittenOriginX{0.0f};
    std::atomic<float>    g_lastWrittenOriginY{0.0f};
    std::atomic<float>    g_lastWrittenOriginZ{0.0f};
    std::atomic<float>    g_lastReadBackOriginX{0.0f};
    std::atomic<float>    g_lastReadBackOriginY{0.0f};
    std::atomic<float>    g_lastReadBackOriginZ{0.0f};
    std::atomic<uint32_t> g_lastPawnIdentity{0};       // pawn->identity（每次 commit 刷新）
    std::atomic<int>      g_lastPawnMoveType{-1};      // pawn->m_MoveType（验证写入）

    namespace
    {
        // 单个 slot 的回放状态。
        // ticks: 回放的 ReplayTick 数组（按时间顺序）。
        // subs: 并行的 SubtickMove 数组，由 subOffset 索引。
        // subOffset: 前缀和数组，size = ticks.size()+1；
        //   tick i 的 subtick 范围 = [subOffset[i], subOffset[i+1])。
        struct ReplayState
        {
            std::atomic<bool> playing{false};
            std::atomic<bool> loop{false};
            std::vector<ReplayTick> ticks;
            std::vector<SubtickMove> subs;
            std::vector<uint32_t> subOffset;  // 前缀和，size = ticks.size()+1
            std::atomic<int> cursor{0};
            std::mutex mu;  // 保护 ticks/subs/subOffset
        };

        // 全局回放状态数组（每个 slot 一份）。
        std::array<ReplayState, kMaxSlots> g_rep;

        // 判断 slot 是否在合法范围。
        bool ValidSlot(int s) { return s >= 0 && s < kMaxSlots; }

        // 一次性写入 3 个 float 到引擎内存，减少内存访问次数。
        // base: 起始地址；offset: 相对偏移；x/y/z: 三个分量。
        bool WriteVector3(void *base, int offset, float x, float y, float z)
        {
            const float values[3] = {x, y, z};
            return Mem::TryWriteMemory(base, offset, values, sizeof(values));
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

        // 重建 subOffset 前缀和数组。
        // subOffset[i] = 第 i 个 tick 的首个 subtick 索引；size = nTicks+1。
        void RebuildSubOffset(ReplayState &p)
        {
            p.subOffset.assign(p.ticks.size() + 1, 0);
            uint32_t acc = 0;
            for (size_t i = 0; i < p.ticks.size(); ++i)
            {
                p.subOffset[i] = acc;
                acc += p.ticks[i].numSubtick;
            }
            p.subOffset[p.ticks.size()] = acc;
        }
    } // namespace

    // ============================================================
    // 回放控制
    // ============================================================

    bool LoadReplay(int slot, const ReplayTick *ticks, int tickCount,
                    const SubtickMove *subs, int subCount)
    {
        if (!ValidSlot(slot) || !ticks || tickCount < 0 ||
            (subCount > 0 && !subs))
            return false;

        ReplayState &p = g_rep[slot];
        // 回放进行中拒绝加载，避免数据竞争
        if (p.playing.load(std::memory_order_acquire))
            return false;

        std::lock_guard<std::mutex> lk(p.mu);
        p.ticks.assign(ticks, ticks + tickCount);
        p.subs.assign(subs, subs + (subCount > 0 ? subCount : 0));
        RebuildSubOffset(p);
        p.cursor.store(0, std::memory_order_relaxed);
        // 加载后不自动开始回放，需显式调用 StartReplay
        p.playing.store(false, std::memory_order_release);
        return true;
    }

    bool SetReplayPawn(int slot, void *pawn)
    {
        // 委托给 Hooks 模块，复用其 pawn 校验（identity / controller 归属）
        return Hooks::SetReplayPawn(slot, pawn);
    }

    bool StartReplay(int slot, bool loop)
    {
        if (!ValidSlot(slot))
            return false;
        ReplayState &p = g_rep[slot];
        // 缓冲区为空时拒绝开始
        {
            std::lock_guard<std::mutex> lk(p.mu);
            if (p.ticks.empty())
                return false;
        }
        p.cursor.store(0, std::memory_order_relaxed);
        p.loop.store(loop, std::memory_order_relaxed);
        p.playing.store(true, std::memory_order_release);
        return true;
    }

    bool StopReplay(int slot)
    {
        if (!ValidSlot(slot))
            return false;
        g_rep[slot].playing.store(false, std::memory_order_release);
        return true;
    }

    bool IsReplaying(int slot)
    {
        return ValidSlot(slot) &&
               g_rep[slot].playing.load(std::memory_order_acquire);
    }

    int ReplayCursor(int slot)
    {
        if (!ValidSlot(slot))
            return -1;
        ReplayState &p = g_rep[slot];
        if (!p.playing.load(std::memory_order_acquire))
            return -1;
        return p.cursor.load(std::memory_order_relaxed);
    }

    int ReplayTotal(int slot)
    {
        if (!ValidSlot(slot))
            return 0;
        ReplayState &p = g_rep[slot];
        std::lock_guard<std::mutex> lk(p.mu);
        return static_cast<int>(p.ticks.size());
    }

    // ============================================================
    // hook 注入点
    // ============================================================

    void OnReplayPre(int slot, void *services, void *moveData)
    {
        if (!ValidSlot(slot) || !services || !moveData)
            return;
        ReplayState &p = g_rep[slot];
        if (!p.playing.load(std::memory_order_acquire))
            return;

        g_replayPreCalls.fetch_add(1, std::memory_order_relaxed);
        g_lastReplaySlot.store(slot, std::memory_order_relaxed);

        // 读取当前 tick 的数据（ticks[cursor]）
        ReplayTick t{};
        {
            std::lock_guard<std::mutex> lk(p.mu);
            int total = static_cast<int>(p.ticks.size());
            int cur = p.cursor.load(std::memory_order_relaxed);
            if (cur < 0 || cur >= total)
                return;  // 游标越界，由 OnReplayCommit 处理 loop/stop
            t = p.ticks[cur];
            g_lastReplayCursor.store(cur, std::memory_order_relaxed);
        }

        // 解析回放目标 pawn
        void *pawn = Hooks::ResolveReplayPawn(slot, services);
        if (!pawn)
            g_replayPrePawnNull.fetch_add(1, std::memory_order_relaxed);

        // 写入 CMoveData：速度 + 位置
        WriteVector3(moveData, tg::kMove_Velocity, t.pre.velX, t.pre.velY, t.pre.velZ);
        WriteVector3(moveData, tg::kMove_AbsOrigin,
                     t.pre.originX, t.pre.originY, t.pre.originZ);

        // 写入 pawn：移动类型 / 速度 / 场景节点 origin / 视角
        // 对齐 CS2-Bot-Controller OnReplayPre：不写入 actualMoveType 和 entityFlags。
        // 全量覆盖 entityFlags 会破坏引擎运行时状态机（FL_ONGROUND/FL_INWATER 等是
        // 引擎动态计算的），导致 bot 被判定为异常状态而停止移动。
        if (pawn)
        {
            Mem::WriteField(pawn, tg::kEnt_MoveType, t.pre.moveType);
            WriteVector3(pawn, tg::kEnt_AbsVelocity, t.pre.velX, t.pre.velY, t.pre.velZ);

            // 写入场景节点 origin（pre 起始位置）
            void *node = ResolveSceneNode(pawn);
            if (node)
            {
                WriteVector3(node, tg::kNode_AbsOrigin,
                             t.pre.originX, t.pre.originY, t.pre.originZ);
            }

            // 写入视角：通过 ApplyReplayEyeAngles 调用原 SetEyeAngles
            if (!Hooks::ApplyReplayEyeAngles(pawn, t.pre.pitch, t.pre.yaw))
            {
                WriteVector3(pawn, tg::kPawn_ViewAngle, t.pre.pitch, t.pre.yaw, t.pre.roll);
                WriteVector3(pawn, tg::kPawn_EyeAngles, t.pre.pitch, t.pre.yaw, t.pre.roll);
            }
        }

        // 写入 services：按键 + 蹲伏/梯子状态
        Mem::WriteField(services, tg::kServices_Buttons, t.pre.buttons);
        Mem::WriteField(services, tg::kServices_Buttons1, t.pre.buttons1);
        Mem::WriteField(services, tg::kServices_Buttons2, t.pre.buttons2);
        Mem::WriteField(services, tg::kServices_DuckAmount, t.pre.duckAmount);
        Mem::WriteField(services, tg::kServices_DuckSpeed, t.pre.duckSpeed);
        Mem::WriteField(services, tg::kServices_Ducked, t.pre.ducked);
        Mem::WriteField(services, tg::kServices_Ducking, t.pre.ducking);
        Mem::WriteField(services, tg::kServices_DesiresDuck, t.pre.desiresDuck);
        WriteVector3(services, tg::kServices_LadderNormal,
                     t.pre.ladderNormalX, t.pre.ladderNormalY, t.pre.ladderNormalZ);
    }

    void OnReplayFinishMove(int slot, void *services, void *moveData)
    {
        if (!ValidSlot(slot) || !services || !moveData)
            return;
        ReplayState &p = g_rep[slot];
        if (!p.playing.load(std::memory_order_acquire))
            return;

        g_replayFinishMoveCalls.fetch_add(1, std::memory_order_relaxed);

        // 读取当前 tick 的数据
        ReplayTick t{};
        {
            std::lock_guard<std::mutex> lk(p.mu);
            int total = static_cast<int>(p.ticks.size());
            int cur = p.cursor.load(std::memory_order_relaxed);
            if (cur < 0 || cur >= total)
                return;
            t = p.ticks[cur];
        }

        // 对齐 CS2-Bot-Controller OnReplayFinishMove：
        // 只写 CMoveData 和场景节点 origin（带 zBias），不写入 pawn 的其他字段。
        // 写入 pawn 的 moveType/actualMoveType/entityFlags/AbsVelocity 会干扰引擎
        // FinishMove 后的状态更新，特别是全量覆盖 entityFlags 会破坏引擎运行时状态机。
        WriteVector3(moveData, tg::kMove_Velocity, t.post.velX, t.post.velY, t.post.velZ);
        WriteVector3(moveData, tg::kMove_AbsOrigin,
                     t.post.originX, t.post.originY, t.post.originZ);

        void *pawn = Hooks::ResolveReplayPawn(slot, services);
        if (pawn)
        {
            // 写入场景节点 origin（最终提交位置）
            // Windows 上加 zBias=1000 强制引擎识别位置变化（参考实现的做法）
            void *node = ResolveSceneNode(pawn);
            if (node)
            {
#if defined(_WIN32)
                WriteVector3(node, tg::kNode_AbsOrigin,
                             t.post.originX, t.post.originY, t.post.originZ + 1000.0f);
#else
                WriteVector3(node, tg::kNode_AbsOrigin,
                             t.post.originX, t.post.originY, t.post.originZ);
#endif
            }
        }
    }

    void OnReplayCommit(int slot, void *services)
    {
        if (!ValidSlot(slot) || !services)
            return;
        ReplayState &p = g_rep[slot];
        if (!p.playing.load(std::memory_order_acquire))
            return;

        g_replayCommitCalls.fetch_add(1, std::memory_order_relaxed);
        g_lastReplaySlot.store(slot, std::memory_order_relaxed);

        ReplayTick t{};
        int cur;
        int total;
        bool loop;
        {
            std::lock_guard<std::mutex> lk(p.mu);
            total = static_cast<int>(p.ticks.size());
            cur = p.cursor.load(std::memory_order_relaxed);
            loop = p.loop.load(std::memory_order_relaxed);
            g_lastReplayCursor.store(cur, std::memory_order_relaxed);

            // 游标已越过末尾（OnReplayPre 未注入）：处理 loop/stop
            if (cur >= total)
            {
                if (loop && total > 0)
                {
                    p.cursor.store(0, std::memory_order_relaxed);
                }
                else
                {
                    p.playing.store(false, std::memory_order_release);
                }
                return;
            }

            t = p.ticks[cur];
        }

        // 关键：PhysicsSimulate 完成后，引擎已按自身逻辑运行了 bot 的移动，
        // bot 当前位置是引擎计算的结果，而非录制位置。
        // 必须在此处用录制的 post 快照覆盖引擎结果，确保位置 "定格" 到录制轨迹。
        void *pawn = Hooks::ResolveReplayPawn(slot, services);
        if (!pawn)
        {
            g_replayCommitPawnNull.fetch_add(1, std::memory_order_relaxed);
        }
        if (pawn)
        {
            Mem::WriteField(pawn, tg::kEnt_MoveType, t.post.moveType);
            Mem::WriteField(pawn, tg::kEnt_ActualMoveType, t.post.actualMoveType);

            // 合并 flags：仅覆盖 ONGROUND + DUCKING 位，保留引擎设置的其他位
            // 全量覆盖会破坏引擎的 FL_ONGROUND/FL_INWATER 等运行时状态
            uint32_t liveFlags = 0;
            if (Mem::SafeRead(pawn, tg::kEnt_Flags, liveFlags))
            {
                constexpr uint32_t mask = tg::kFL_OnGround | tg::kFL_Ducking;
                liveFlags = (liveFlags & ~mask) | (t.post.entityFlags & mask);
                Mem::WriteField(pawn, tg::kEnt_Flags, liveFlags);
            }
            else
            {
                Mem::WriteField(pawn, tg::kEnt_Flags, t.post.entityFlags);
            }

            // 写入速度（覆盖引擎计算的速度）
            WriteVector3(pawn, tg::kEnt_AbsVelocity, t.post.velX, t.post.velY, t.post.velZ);

            // 写入场景节点 origin（post 终态位置）——这是 tick 的最终定格位置
            void *node = ResolveSceneNode(pawn);
            if (node)
            {
                WriteVector3(node, tg::kNode_AbsOrigin,
                             t.post.originX, t.post.originY, t.post.originZ);

                // 诊断：立即读回场景节点 origin，验证写入是否真正生效
                // 若读回值 != 写入值，说明写入被 SEH 拦截或地址无效
                float readBack[3] = {0.0f, 0.0f, 0.0f};
                if (Mem::TryReadMemory(node, tg::kNode_AbsOrigin, readBack, sizeof(readBack)))
                {
                    g_lastReadBackOriginX.store(readBack[0], std::memory_order_relaxed);
                    g_lastReadBackOriginY.store(readBack[1], std::memory_order_relaxed);
                    g_lastReadBackOriginZ.store(readBack[2], std::memory_order_relaxed);
                }
                g_lastWrittenOriginX.store(t.post.originX, std::memory_order_relaxed);
                g_lastWrittenOriginY.store(t.post.originY, std::memory_order_relaxed);
                g_lastWrittenOriginZ.store(t.post.originZ, std::memory_order_relaxed);
            }

            // 写入视角：通过 ApplyReplayEyeAngles 调用原 SetEyeAngles（post 终态视角）
            if (!Hooks::ApplyReplayEyeAngles(pawn, t.post.pitch, t.post.yaw))
            {
                WriteVector3(pawn, tg::kPawn_ViewAngle, t.post.pitch, t.post.yaw, t.post.roll);
                WriteVector3(pawn, tg::kPawn_EyeAngles, t.post.pitch, t.post.yaw, t.post.roll);
            }

            // 诊断：读取 pawn 的 identity 和 moveType，验证 pawn 是否被回收
            // identity 在 pawn 创建时分配，回收后会改变；用于判断 pawn 是否仍然有效
            uint32_t identity = 0;
            if (Mem::SafeRead(pawn, tg::kEnt_Identity, identity))
            {
                g_lastPawnIdentity.store(identity, std::memory_order_relaxed);
            }
            uint8_t mt = 0;
            if (Mem::SafeRead(pawn, tg::kEnt_MoveType, mt))
            {
                g_lastPawnMoveType.store(static_cast<int>(mt), std::memory_order_relaxed);
            }
        }

        // 写入 services：蹲伏/梯子状态（覆盖引擎中间状态）
        Mem::WriteField(services, tg::kServices_DuckAmount, t.post.duckAmount);
        Mem::WriteField(services, tg::kServices_DuckSpeed, t.post.duckSpeed);
        Mem::WriteField(services, tg::kServices_Ducked, t.post.ducked);
        Mem::WriteField(services, tg::kServices_Ducking, t.post.ducking);
        Mem::WriteField(services, tg::kServices_DesiresDuck, t.post.desiresDuck);
        WriteVector3(services, tg::kServices_LadderNormal,
                     t.post.ladderNormalX, t.post.ladderNormalY, t.post.ladderNormalZ);

        // 推进游标
        int next = cur + 1;
        if (next >= total)
        {
            // 到达末尾：loop 回到 0，否则停止
            if (loop && total > 0)
            {
                p.cursor.store(0, std::memory_order_relaxed);
            }
            else
            {
                p.playing.store(false, std::memory_order_release);
            }
        }
        else
        {
            p.cursor.store(next, std::memory_order_relaxed);
        }
    }

    // ============================================================
    // 查询接口
    // ============================================================

    bool CurrentReplayTick(int slot, ReplayTick &out)
    {
        if (!ValidSlot(slot))
            return false;
        ReplayState &p = g_rep[slot];
        if (!p.playing.load(std::memory_order_acquire))
            return false;
        std::lock_guard<std::mutex> lk(p.mu);
        int total = static_cast<int>(p.ticks.size());
        int cur = p.cursor.load(std::memory_order_relaxed);
        if (cur < 0 || cur >= total)
            return false;
        out = p.ticks[cur];
        return true;
    }

    bool ReplayCommandViewSnapshot(int slot, MovementSnapshot &out)
    {
        if (!ValidSlot(slot))
            return false;
        ReplayState &p = g_rep[slot];
        if (!p.playing.load(std::memory_order_acquire))
            return false;
        std::lock_guard<std::mutex> lk(p.mu);
        int total = static_cast<int>(p.ticks.size());
        int cur = p.cursor.load(std::memory_order_relaxed);
        if (cur < 0 || cur >= total)
            return false;
        // 返回 pre 快照（包含视角），供 PlayerRunCommand 设置 cmd viewangles
        out = p.ticks[cur].pre;
        return true;
    }

    bool CurrentReplayInputButtons(int slot, uint64_t &b0, uint64_t &b1, uint64_t &b2)
    {
        if (!ValidSlot(slot))
            return false;
        ReplayState &p = g_rep[slot];
        if (!p.playing.load(std::memory_order_acquire))
            return false;
        std::lock_guard<std::mutex> lk(p.mu);
        int total = static_cast<int>(p.ticks.size());
        int cur = p.cursor.load(std::memory_order_relaxed);
        if (cur < 0 || cur >= total)
            return false;
        const MovementSnapshot &pre = p.ticks[cur].pre;
        b0 = pre.buttons;
        b1 = pre.buttons1;
        b2 = pre.buttons2;
        return true;
    }

    int CurrentReplaySubticks(int slot, SubtickMove *out, int maxOut)
    {
        if (!ValidSlot(slot) || !out || maxOut <= 0)
            return -1;
        ReplayState &p = g_rep[slot];
        if (!p.playing.load(std::memory_order_acquire))
            return -1;
        std::lock_guard<std::mutex> lk(p.mu);
        int total = static_cast<int>(p.ticks.size());
        int cur = p.cursor.load(std::memory_order_relaxed);
        if (cur < 0 || cur >= total)
            return -1;
        // subOffset 大小 = ticks.size()+1，cur+1 一定在范围内
        uint32_t begin = p.subOffset[cur];
        uint32_t end = p.subOffset[cur + 1];
        int n = static_cast<int>(end - begin);
        if (n > maxOut)
            n = maxOut;
        for (int i = 0; i < n; ++i)
            out[i] = p.subs[begin + i];
        return n;
    }

    int CurrentReplayWeaponDef(int slot)
    {
        if (!ValidSlot(slot))
            return -1;
        ReplayState &p = g_rep[slot];
        if (!p.playing.load(std::memory_order_acquire))
            return -1;
        std::lock_guard<std::mutex> lk(p.mu);
        int total = static_cast<int>(p.ticks.size());
        int cur = p.cursor.load(std::memory_order_relaxed);
        if (cur < 0 || cur >= total)
            return -1;
        return p.ticks[cur].weaponDefIndex;
    }

    // ============================================================
    // 清理
    // ============================================================

    void ClearAll()
    {
        for (int i = 0; i < kMaxSlots; ++i)
        {
            ReplayState &p = g_rep[i];
            p.playing.store(false, std::memory_order_release);
            p.loop.store(false, std::memory_order_release);
            {
                std::lock_guard<std::mutex> lk(p.mu);
                p.ticks.clear();
                p.subs.clear();
                p.subOffset.clear();
            }
            p.cursor.store(0, std::memory_order_release);
        }
        Platform::DebugOut("[PracLabReplayEngine] Replayer: all slots cleared\n");
    }

    // ============================================================
    // 诊断接口实现
    // ============================================================
    uint64_t ReplayPreCalls()         { return g_replayPreCalls.load(std::memory_order_relaxed); }
    uint64_t ReplayFinishMoveCalls()  { return g_replayFinishMoveCalls.load(std::memory_order_relaxed); }
    uint64_t ReplayCommitCalls()      { return g_replayCommitCalls.load(std::memory_order_relaxed); }
    uint64_t ReplayPrePawnNull()      { return g_replayPrePawnNull.load(std::memory_order_relaxed); }
    uint64_t ReplayCommitPawnNull()   { return g_replayCommitPawnNull.load(std::memory_order_relaxed); }
    int      LastReplaySlot()         { return g_lastReplaySlot.load(std::memory_order_relaxed); }
    int      LastReplayCursor()       { return g_lastReplayCursor.load(std::memory_order_relaxed); }

    // 位置读回验证：对比 OnReplayCommit 写入值与读回值
    float    LastWrittenOriginX()     { return g_lastWrittenOriginX.load(std::memory_order_relaxed); }
    float    LastWrittenOriginY()     { return g_lastWrittenOriginY.load(std::memory_order_relaxed); }
    float    LastWrittenOriginZ()     { return g_lastWrittenOriginZ.load(std::memory_order_relaxed); }
    float    LastReadBackOriginX()    { return g_lastReadBackOriginX.load(std::memory_order_relaxed); }
    float    LastReadBackOriginY()    { return g_lastReadBackOriginY.load(std::memory_order_relaxed); }
    float    LastReadBackOriginZ()    { return g_lastReadBackOriginZ.load(std::memory_order_relaxed); }
    uint32_t LastPawnIdentity()       { return g_lastPawnIdentity.load(std::memory_order_relaxed); }
    int      LastPawnMoveType()       { return g_lastPawnMoveType.load(std::memory_order_relaxed); }
} // namespace PracLab::ReplayEngine::Replayer
