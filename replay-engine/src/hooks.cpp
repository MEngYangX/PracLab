// 引擎函数 Hook 实现。
// 拦截 7 个关键引擎函数实现录制/回放框架：
//   1. ProcessMovement    — 录制 pre/post 快照，回放注入 pre 状态
//   2. FinishMove          — 回放写入 post 状态 + 提交
//   3. PlayerRunCommand    — 录制/替换 subtick_moves（protobuf API）
//   4. PhysicsSimulate     — Tick 边界提交（录制 pre/post + 回放提交）
//   5. CCSBot::Update      — 回放时设置 tickEd 标志并跳过 AI 决策
//   6. CCSBot::Upkeep      — 回放时跳过视角维护
//   7. CCSBot::UpdateLookAngles / CCSPlayerPawn::SetEyeAngles — 回放时跳过引擎视角覆盖
//
// 仿照 CS2-Bot-Controller 的设计：
//   - CCSBot::Update 回放时设置 kBot_AiTickedFlag=1，告诉引擎 "AI 已执行"，
//     同时跳过 AI 决策逻辑，但引擎仍会为 bot 生成 user command 并调用
//     ProcessMovement，确保回放 hook 链每 tick 都被触发。
//   - SetEyeAngles 回放时跳过，改用 ApplyReplayEyeAngles 主动调用原函数
//     （临时清除 FL_FAKECLIENT 标志绕过引擎的 fake-client 早退路径）。
//
// 录制逻辑（Task 4）已接入 Recorder API；回放逻辑（Task 5）已接入 Replayer API。
// PlayerRunCommand 中对 CUserCmd subtick_moves 的读取/写入使用 protobuf API
// 完整实现，因为它是 hook 的核心逻辑。

#include "hooks.h"
#include "hook.h"
#include "memory.h"
#include "platform.h"
#include "player_command.h"
#include "recorder.h"
#include "replayer.h"
#include "sig_scan.h"
#include "slot_resolver.h"
#include "version_targets.h"
#include "weapon_switcher.h"

#include <array>
#include <atomic>
#include <cmath>
#include <cstdio>
#include <cstdint>
#include <cstring>
#include <string>

// protobuf 生成的头文件（CMake PROTO 模块）
#include "cs_usercmd.pb.h"
#include "usercmd.pb.h"

namespace tg = PracLab::ReplayEngine::targets;

namespace PracLab::ReplayEngine::Hooks
{
    // ---- 函数指针类型 ----
    // 所有目标函数在 Windows x64 上使用 fastcall 调用约定；
    // Linux System V x64 默认约定等价，PRL_FASTCALL 在 Linux 上展开为空。
    using ProcessMovement_t = void(PRL_FASTCALL *)(void *services, void *moveData);
    using FinishMove_t      = void(PRL_FASTCALL *)(void *services, void *cmd, void *moveData);
    using PlayerRunCommand_t = void(PRL_FASTCALL *)(void *services, void *cmd);
    using PhysicsSimulate_t  = void(PRL_FASTCALL *)(void *controller);
    using CCSBotUpdate_t      = void(PRL_FASTCALL *)(void *bot);
    using CCSBotUpkeep_t      = void(PRL_FASTCALL *)(void *bot);
    using CCSBotUpdateLookAngles_t = void(PRL_FASTCALL *)(void *bot);
    using SetEyeAngles_t     = void(PRL_FASTCALL *)(void *pawn, float *angle);

    namespace
    {
        // ---- trampoline 指针（调用原函数用） ----
        ProcessMovement_t  g_origProcessMovement  = nullptr;
        FinishMove_t       g_origFinishMove       = nullptr;
        PlayerRunCommand_t g_origPlayerRunCommand = nullptr;
        PhysicsSimulate_t  g_origPhysicsSimulate   = nullptr;
        CCSBotUpdate_t     g_origCCSBotUpdate      = nullptr;
        CCSBotUpkeep_t     g_origCCSBotUpkeep      = nullptr;
        CCSBotUpdateLookAngles_t g_origCCSBotUpdateLookAngles = nullptr;
        SetEyeAngles_t     g_origSetEyeAngles      = nullptr;

        // ---- 已解析的函数地址（用于卸载时清空状态） ----
        void *g_addrProcessMovement  = nullptr;
        void *g_addrFinishMove       = nullptr;
        void *g_addrPlayerRunCommand = nullptr;
        void *g_addrPhysicsSimulate  = nullptr;
        void *g_addrCCSBotUpdate     = nullptr;
        void *g_addrCCSBotUpkeep     = nullptr;
        void *g_addrCCSBotUpdateLookAngles = nullptr;
        void *g_addrSetEyeAngles     = nullptr;

        // ---- Hook 实例 ----
        Hook g_hookProcessMovement;
        Hook g_hookFinishMove;
        Hook g_hookPlayerRunCommand;
        Hook g_hookPhysicsSimulate;
        Hook g_hookCCSBotUpdate;
        Hook g_hookCCSBotUpkeep;
        Hook g_hookCCSBotUpdateLookAngles;
        Hook g_hookSetEyeAngles;

        bool g_installed = false;
        bool g_physicsActive = false;   // PhysicsSimulate hook 是否激活
        bool g_subtickActive = false;   // PlayerRunCommand hook 是否激活
        std::string g_status = "not_attempted";

        // slot -> 当前 tick 的 CCSPlayer_MovementServices*（由 ProcessMovement 缓存，PhysicsSimulate 读取）
        std::array<std::atomic<void *>, kMaxSlots> g_slotServices{};
        // slot -> 注册的回放 pawn（由 SetReplayPawn 写入）
        std::array<std::atomic<void *>, kMaxSlots> g_slotPawns{};
        // slot -> 冻结标志（回放结束后设置，阻止 AI 激活，bot 保持静止）
        std::array<std::atomic<bool>, kMaxSlots> g_frozenSlots{};

#if defined(_WIN32)
        // SetEyeAngles 函数中引用的实体 identity chunks 指针（Windows 专用）。
        // 用于通过 pawn 的 controller handle 反查 live CCSPlayerController*，
        // 以便在调用 SetEyeAngles 前临时清除 FL_FAKECLIENT 标志绕过早退路径。
        void **g_ppEntityIdentityChunks = nullptr;
#endif

        // vtable hook 是否已尝试安装（保证 EnsureVtableHooks 只执行一次）
        std::atomic<bool> g_vtHooksTried{false};

        // ---- 诊断计数（仅用于日志） ----
        std::atomic<uint64_t> g_processMovementCalls{0};
        std::atomic<uint64_t> g_finishMoveCalls{0};
        std::atomic<uint64_t> g_playerRunCommandCalls{0};
        std::atomic<uint64_t> g_physicsSimulateCalls{0};
        std::atomic<uint64_t> g_ccsbotUpdateCalls{0};
        std::atomic<uint64_t> g_ccsbotUpkeepCalls{0};
        std::atomic<uint64_t> g_ccsbotUpdateLookAnglesCalls{0};
        std::atomic<uint64_t> g_setEyeAnglesCalls{0};
        std::atomic<int>      g_lastSlot{-1};
        std::atomic<int>      g_lastPhysicsSlot{-1};

        // 判断 slot 是否在合法范围。
        bool ValidSlotIndex(int slot)
        {
            return slot >= 0 && slot < kMaxSlots;
        }

        // ---- 录制/回放状态查询 ----
        bool IsRecording(int slot)
        {
            return Recorder::IsRecording(slot);
        }

        bool IsReplaying(int slot)
        {
            return Replayer::IsReplaying(slot);
        }

        // 查询 slot 是否被冻结（回放结束后保持静止）
        bool IsFrozen(int slot)
        {
            if (!ValidSlotIndex(slot))
                return false;
            return g_frozenSlots[slot].load(std::memory_order_acquire);
        }

        // 冻结 slot（回放结束后调用，阻止 AI 激活）
        void SetFrozen(int slot, bool frozen)
        {
            if (!ValidSlotIndex(slot))
                return;
            g_frozenSlots[slot].store(frozen, std::memory_order_release);
        }

        // 角度归一化到 [-180, 180]。
        // 回放写入 viewangles 时避免跨越 360° 边界造成跳变。
        float NormalizeDeg(float a)
        {
            a = std::fmod(a + 180.0f, 360.0f);
            if (a < 0.0f)
                a += 360.0f;
            return a - 180.0f;
        }

#if defined(_WIN32)
        // 在 SetEyeAngles 函数体内扫描引用 entity identity chunks 的指令。
        // 模式：4C 8B 05 ?? ?? ?? ?? 4D 85 C0（lea rcx, [rip+disp32]; test r8, r8）
        // 解析出 g_ppEntityIdentityChunks 全局指针，用于反查 controller。
        void ResolveSetEyeAnglesEntityChunks(void *setEyeAngles)
        {
            g_ppEntityIdentityChunks = nullptr;
            if (!setEyeAngles)
                return;

            constexpr size_t kSearchBytes = 0x120;
            uint8_t code[kSearchBytes] = {};
            if (!Mem::TryReadMemory(setEyeAngles, 0, code, sizeof(code)))
                return;

            auto *functionBase = reinterpret_cast<uint8_t *>(setEyeAngles);
            for (size_t i = 0; i + 10 <= kSearchBytes; ++i)
            {
                if (code[i] != 0x4C || code[i + 1] != 0x8B || code[i + 2] != 0x05 ||
                    code[i + 7] != 0x4D || code[i + 8] != 0x85 || code[i + 9] != 0xC0)
                    continue;

                int32_t relative = 0;
                std::memcpy(&relative, code + i + 3, sizeof(relative));
                g_ppEntityIdentityChunks =
                    reinterpret_cast<void **>(functionBase + i + 7 + relative);
                return;
            }
        }

        // 通过 pawn 的 controller handle 反查 live CCSPlayerController*。
        // 用于在调用 SetEyeAngles 前临时清除 FL_FAKECLIENT 标志。
        void *ReplayControllerForPawn(void *pawn)
        {
            if (!pawn || !g_ppEntityIdentityChunks)
                return nullptr;

            uint32_t handle = 0;
            if (!Mem::SafeRead(pawn, tg::kPawn_Controller, handle) ||
                handle == 0xFFFFFFFFu || handle == 0xFFFFFFFEu)
                return nullptr;

            void *chunks = nullptr;
            if (!Mem::TryReadMemory(g_ppEntityIdentityChunks, 0, &chunks, sizeof(chunks)) || !chunks)
                return nullptr;

            const uint32_t entityIndex = handle & 0x7FFFu;
            void *chunk = nullptr;
            if (!Mem::TryReadMemory(chunks,
                               static_cast<int>((entityIndex >> 9) * sizeof(void *)),
                               &chunk, sizeof(chunk)) ||
                !chunk)
                return nullptr;

            constexpr int kIdentitySize = 0x70;
            auto *identity = reinterpret_cast<uint8_t *>(chunk) +
                             static_cast<size_t>(entityIndex & 0x1FFu) * kIdentitySize;
            uint32_t liveHandle = 0;
            void *controller = nullptr;
            if (!Mem::SafeRead(identity, 0x10, liveHandle) || liveHandle != handle ||
                !Mem::SafeRead(identity, 0x00, controller))
                return nullptr;
            return controller;
        }
#endif

        // 调用原 SetEyeAngles 设置视角，临时清除 FL_FAKECLIENT 绕过早退路径。
        // 仿照 CS2-Bot-Controller 的 ApplyReplayEyeAngles 设计：
        //   1. Windows 上 bot controller 有 FL_FAKECLIENT(0x100) 标志，
        //      SetEyeAngles 检测到该标志会早退不设置视角。
        //   2. 临时清除该标志 → 调用原 SetEyeAngles → 恢复标志。
        bool ApplyReplayEyeAnglesInternal(void *pawn, float pitch, float yaw)
        {
            if (!pawn || !g_origSetEyeAngles)
                return false;

            float angle[3] = {pitch, NormalizeDeg(yaw), 0.0f};
#if defined(_WIN32)
            void *controller = ReplayControllerForPawn(pawn);
            uint32_t controllerFlags = 0;
            bool restoreFakeClient = false;
            if (controller &&
                Mem::SafeRead(controller, tg::kEnt_Flags, controllerFlags) &&
                (controllerFlags & 0x100u) != 0)
            {
                const uint32_t publishedFlags = controllerFlags & ~0x100u;
                restoreFakeClient =
                    Mem::WriteField(controller, tg::kEnt_Flags, publishedFlags);
            }
#endif
            g_origSetEyeAngles(pawn, angle);
#if defined(_WIN32)
            if (restoreFakeClient)
                Mem::WriteField(controller, tg::kEnt_Flags, controllerFlags);
#endif
            return true;
        }

        // 从 movement services 读取 m_pawn 字段。
        void *ServicesToPawnField(void *services)
        {
            if (!services)
                return nullptr;
            void *pawn = nullptr;
            return Mem::SafeRead(services, tg::kServices_Pawn, pawn) ? pawn : nullptr;
        }

        // 从 movement services 经 pawn 读取 WeaponServices*。
        // 供录制引擎追踪当前武器服务对象。
        void *ServicesToWeaponServices(void *services)
        {
            void *pawn = ServicesToPawnField(services);
            if (!pawn)
                return nullptr;
            void *ws = nullptr;
            return Mem::SafeRead(pawn, tg::kPawn_WeaponServices, ws) ? ws : nullptr;
        }

        // 验证 pawn 当前是否持有指定的 movement services。
        // 防止实体回收后旧指针误匹配。
        bool PawnOwnsServices(void *pawn, void *services)
        {
            if (!pawn || !services)
                return false;
            void *liveServices = nullptr;
            return Mem::SafeRead(pawn, tg::kPawn_MovementServices, liveServices) &&
                   liveServices == services;
        }

        // 遍历注册表查找当前持有 services 的 pawn。
        // 当 controller handle 失效时作为兜底。
        int RegisteredSlotForServices(void *services)
        {
            if (!services)
                return -1;
            for (int slot = 0; slot < kMaxSlots; ++slot)
            {
                void *pawn = g_slotPawns[slot].load(std::memory_order_acquire);
                if (PawnOwnsServices(pawn, services))
                    return slot;
            }
            return -1;
        }

        // 从 movement services 解析 slot。
        // 优先通过 pawn 的 controller handle，失败再遍历注册表。
        int ServicesToSlot(void *services)
        {
            if (!services)
                return -1;

            void *pawn = ServicesToPawnField(services);
            // pawn 字段与 services 不匹配时认为无效
            if (!PawnOwnsServices(pawn, services))
                pawn = nullptr;

            // 优先：pawn controller handle
            int slot = Slot::ControllerSlotForPawn(pawn);
            if (slot < 0)
                slot = RegisteredSlotForServices(services);
            return slot;
        }

        // 从 movement services 提取回放 pawn。
        // 先查注册表（SetReplayPawn 设置），再回退到 services 内嵌字段。
        void *ResolveReplayPawnImpl(int slot, void *services)
        {
            if (ValidSlotIndex(slot))
            {
                void *registered = g_slotPawns[slot].load(std::memory_order_acquire);
                if (PawnOwnsServices(registered, services))
                    return registered;
            }
            void *fieldPawn = ServicesToPawnField(services);
            return PawnOwnsServices(fieldPawn, services) ? fieldPawn : nullptr;
        }

        // 前向声明：vtable 懒加载 hook
        void EnsureVtableHooks(void *services);

        // ============================================================
        // Detour 1: ProcessMovement
        // ============================================================
        // 录制：pre 快照（若 PhysicsSimulate 未激活）→ 调用原函数 → post 快照
        // 回放：注入 pre 状态到 CMoveData → 调用原函数
        void PRL_FASTCALL HookedProcessMovement(void *services, void *moveData)
        {
            g_processMovementCalls.fetch_add(1, std::memory_order_relaxed);
            int slot = ServicesToSlot(services);
            g_lastSlot.store(slot, std::memory_order_relaxed);

            // 首次调用时从 services vtable 懒加载 FinishMove / PlayerRunCommand hook
            EnsureVtableHooks(services);

            // 缓存 slot -> services，供 PhysicsSimulate 提交时反查
            if (ValidSlotIndex(slot))
                g_slotServices[slot].store(services, std::memory_order_release);

            bool recording = ValidSlotIndex(slot) && IsRecording(slot);
            bool replaying = ValidSlotIndex(slot) && IsReplaying(slot);

            // 录制：追踪当前 WeaponServices* 和 active weapon def index
            // 供回放时武器切换使用（WeaponSwitcher::CurrentReplayWeaponSelect 读取此 def）
            if (recording)
            {
                void *ws = ServicesToWeaponServices(services);
                Recorder::SetLiveWs(slot, ws);
                // 通过 WeaponSwitcher 读取当前 active weapon 的 def index
                // 若 WeaponSwitcher 未安装则 def=-1，回放时武器切换不可用（降级）
                if (ws && WeaponSwitcher::WeaponHooksReady())
                {
                    int def = WeaponSwitcher::ActiveWeaponDef(ws);
                    Recorder::SetCurrentDef(slot, def);
                }
            }

            // 录制 pre 快照（仅在 PhysicsSimulate 未激活时由 ProcessMovement 负责提交）
            if (recording && !g_physicsActive)
            {
                Recorder::OnCapturePre(slot, services, moveData);
            }

            // 回放：向 CMoveData + pawn 注入本 tick 的 pre 快照
            if (replaying)
            {
                Replayer::OnReplayPre(slot, services, moveData);
            }

            g_origProcessMovement(services, moveData);

            // 录制 post 快照
            if (recording && !g_physicsActive)
            {
                Recorder::OnCapturePost(slot, services, moveData);
            }
        }

        // ============================================================
        // Detour 2: FinishMove
        // ============================================================
        // 回放：写入 post 状态到 CMoveData → 调用原函数 → 提交并推进游标
        void PRL_FASTCALL HookedFinishMove(void *services, void *cmd, void *moveData)
        {
            g_finishMoveCalls.fetch_add(1, std::memory_order_relaxed);
            int slot = ServicesToSlot(services);
            bool replaying = ValidSlotIndex(slot) && IsReplaying(slot);

            // 原函数调用前：写入 post 快照
            if (replaying)
            {
                Replayer::OnReplayFinishMove(slot, services, moveData);
            }

            g_origFinishMove(services, cmd, moveData);

            // 原函数调用后：提交 moveType/flags + 推进游标
            // 仅在 PhysicsSimulate 未激活时由 FinishMove 负责提交
            if (replaying && !g_physicsActive)
            {
                Replayer::OnReplayCommit(slot, services);
            }
        }

        // ============================================================
        // Detour 3: PlayerRunCommand
        // ============================================================
        // 录制：读取 subtick_moves 到 SubtickMove[]
        // 回放：替换 cmd 的 viewangles / buttons / subtick_moves / weaponselect
        void PRL_FASTCALL HookedPlayerRunCommand(void *services, void *cmd)
        {
            g_playerRunCommandCalls.fetch_add(1, std::memory_order_relaxed);
            int slot = ServicesToSlot(services);
            bool recording = ValidSlotIndex(slot) && IsRecording(slot);
            bool replaying = ValidSlotIndex(slot) && IsReplaying(slot);

            // 回放：缓存 slot -> WeaponServices*，供 WeaponSwitcher 查询/切换武器
            // 仿照 CS2-Bot-Controller 在 PlayerRunCommand 中维护 ws 缓存
            if (replaying && ValidSlotIndex(slot))
            {
                WeaponSwitcher::SetSlotWs(slot, ServicesToWeaponServices(services));
            }

            if (cmd && (recording || replaying))
            {
                // 编译器计算多重继承偏移，reinterpret_cast 即可得到 PlayerCommand* 中的 base 字段
                auto *pc = reinterpret_cast<PlayerCommand *>(cmd);
                CBaseUserCmdPB *base = pc->mutable_base();

                // ---- 录制：读取 subtick_moves ----
                if (recording && base)
                {
                    // protobuf API：subtick_moves_size / subtick_moves(i)
                    int n = base->subtick_moves_size();
                    if (n > Recorder::kMaxSubtickPerTick)
                        n = Recorder::kMaxSubtickPerTick;

                    SubtickMove moves[Recorder::kMaxSubtickPerTick];
                    for (int i = 0; i < n; ++i)
                    {
                        const CSubtickMoveStep &s = base->subtick_moves(i);
                        moves[i].when          = s.when();
                        moves[i].button         = static_cast<uint32_t>(s.button());
                        moves[i].pressed        = s.pressed() ? 1.0f : 0.0f;
                        moves[i].analogForward = s.analog_forward_delta();
                        moves[i].analogLeft     = s.analog_left_delta();
                        moves[i].pitchDelta     = s.pitch_delta();
                        moves[i].yawDelta       = s.yaw_delta();
                    }

                    Recorder::OnCaptureSubticks(slot, moves, n);
                }

                // ---- 回放：替换 viewangles / buttons / subtick_moves / weaponselect ----
                if (replaying && base)
                {
                    // 仿照 CS2-Bot-Controller InputInjector.cpp 的回放武器切换逻辑：
                    //   1. recordedDef: 当前 tick 录制的武器 def index
                    //   2. wsel: 调用 CurrentReplayWeaponSelect 切换武器后返回的 entity index
                    //      （内部会比较 active def，相同则不切换返回 -1）
                    //   3. suppressUnsafeUtilityAttack: 若录制的是投掷道具但 bot 未能切换到该武器，
                    //      则抑制 IN_ATTACK 按钮，防止用错误武器（如 AK47）开火
                    constexpr uint64_t kInAttack = 1ull; // IN_ATTACK bit0

                    // viewangles：从录制的 pre 快照读取并写入 cmd
                    MovementSnapshot cmdView{};
                    if (Replayer::ReplayCommandViewSnapshot(slot, cmdView))
                    {
                        CMsgQAngle *view = base->mutable_viewangles();
                        view->set_x(cmdView.pitch);
                        view->set_y(NormalizeDeg(cmdView.yaw));
                        view->set_z(0.0f);
                    }

                    // weaponselect：调用 WeaponSwitcher 切换到录制的武器
                    int recordedDef = Replayer::CurrentReplayWeaponDef(slot);
                    int wsel = WeaponSwitcher::CurrentReplayWeaponSelect(slot);
                    if (wsel >= 0)
                        base->set_weaponselect(wsel);

                    // suppressUnsafeUtilityAttack：录制的是投掷道具但未能切换时抑制 IN_ATTACK
                    // 防止 bot 用 AK47 等错误武器开火（投掷道具的 IN_ATTACK 是投掷动作）
                    bool suppressUnsafeUtilityAttack = false;
                    if (recordedDef >= 43 && recordedDef <= 48 && wsel < 0)
                    {
                        int botActiveDef = WeaponSwitcher::BotActiveWeaponDef(slot);
                        suppressUnsafeUtilityAttack = (botActiveDef != recordedDef);
                    }

                    // buttons：从录制读取并替换 cmd 的按钮状态
                    uint64_t b0 = 0, b1 = 0, b2 = 0;
                    if (Replayer::CurrentReplayInputButtons(slot, b0, b1, b2))
                    {
                        if (suppressUnsafeUtilityAttack)
                        {
                            b0 &= ~kInAttack;
                            b1 &= ~kInAttack;
                            b2 &= ~kInAttack;
                        }

                        CInButtonStatePB *bp = base->mutable_buttons_pb();
                        bp->set_buttonstate1(b0);
                        bp->set_buttonstate2(b1);
                        bp->set_buttonstate3(b2);
                        pc->buttonstates.m_pButtonStates[0] = b0;
                        pc->buttonstates.m_pButtonStates[1] = b1;
                        pc->buttonstates.m_pButtonStates[2] = b2;
                    }

                    // subtick_moves：清空后从录制重新写入
                    SubtickMove out[Recorder::kMaxSubtickPerTick];
                    int n = Replayer::CurrentReplaySubticks(
                        slot, out, Recorder::kMaxSubtickPerTick);

                    // n < 0 表示未回放（理论上不进入此分支）；n >= 0 时清空并写入
                    if (n >= 0)
                    {
                        base->clear_subtick_moves();
                        for (int i = 0; i < n; ++i)
                        {
                            // 应用 suppressUnsafeUtilityAttack 到 subtick 的 button
                            uint32_t button = out[i].button;
                            float pressed = out[i].pressed;
                            if (suppressUnsafeUtilityAttack && (button & kInAttack))
                            {
                                button &= ~static_cast<uint32_t>(kInAttack);
                                if (button == 0)
                                    pressed = 0.0f;
                            }

                            CSubtickMoveStep *m = base->add_subtick_moves();
                            m->set_when(out[i].when);
                            m->set_button(button);
                            if (button != 0u)
                                m->set_pressed(pressed != 0.0f);
                            if (out[i].pitchDelta != 0.0f)
                                m->set_pitch_delta(out[i].pitchDelta);
                            if (out[i].yawDelta != 0.0f)
                                m->set_yaw_delta(out[i].yawDelta);
                            if (out[i].analogForward != 0.0f)
                                m->set_analog_forward_delta(out[i].analogForward);
                            if (out[i].analogLeft != 0.0f)
                                m->set_analog_left_delta(out[i].analogLeft);
                        }
                    }
                }
            }

            g_origPlayerRunCommand(services, cmd);
        }

        // ============================================================
        // Detour 4: PhysicsSimulate (OnSimulateUserCommands)
        // ============================================================
        // Tick 边界：录制 pre/post + 提交；回放提交
        void PRL_FASTCALL HookedPhysicsSimulate(void *controller)
        {
            g_physicsSimulateCalls.fetch_add(1, std::memory_order_relaxed);
            int slot = Slot::ControllerToSlot(controller);
            g_lastPhysicsSlot.store(slot, std::memory_order_relaxed);

            // 从缓存读取 services（ProcessMovement 已写入）
            void *services = ValidSlotIndex(slot)
                                ? g_slotServices[slot].load(std::memory_order_acquire)
                                : nullptr;

            bool recording = ValidSlotIndex(slot) && services && IsRecording(slot);
            bool replaying = ValidSlotIndex(slot) && services && IsReplaying(slot);

            // pre：tick 开始时记录一次起始状态
            if (recording)
            {
                Recorder::OnCapturePre(slot, services, nullptr);
            }

            g_origPhysicsSimulate(controller);

            // post：tick 结束时记录终态并提交一帧
            if (recording)
            {
                Recorder::OnCapturePost(slot, services, nullptr);
            }
            if (replaying)
            {
                Replayer::OnReplayCommit(slot, services);
            }
        }

        // ============================================================
        // Detour 5: CCSBot::Update
        // ============================================================
        // 仿照 CS2-Bot-Controller：
        //   回放时设置 kBot_AiTickedFlag=1 告诉引擎 "AI 已执行本 tick"，
        //   然后跳过 AI 决策逻辑。引擎仍会为 bot 生成 user command 并调用
        //   ProcessMovement，确保回放 hook 链（OnReplayPre/OnReplayFinishMove）
        //   每 tick 都被触发。
        void PRL_FASTCALL HookedCCSBotUpdate(void *bot)
        {
            g_ccsbotUpdateCalls.fetch_add(1, std::memory_order_relaxed);

            int slot = Slot::CCSBotContextToSlot(bot);
            // 回放中或冻结中：设置 tickEd 标志，跳过 AI 决策
            // 冻结状态用于回放结束后保持 bot 静止（不激活 AI）
            if (ValidSlotIndex(slot) && (IsReplaying(slot) || IsFrozen(slot)))
            {
                const uint8_t ticked = 1;
                Mem::WriteField(bot, tg::kBot_AiTickedFlag, ticked);
                return;
            }

            g_origCCSBotUpdate(bot);
        }

        // ============================================================
        // Detour 6: CCSBot::Upkeep
        // ============================================================
        // 回放时或冻结时跳过视角维护（UpdateLookAround / jitter / UpdateLookAngles）
        void PRL_FASTCALL HookedCCSBotUpkeep(void *bot)
        {
            g_ccsbotUpkeepCalls.fetch_add(1, std::memory_order_relaxed);

            int slot = Slot::CCSBotContextToSlot(bot);
            if (ValidSlotIndex(slot) && (IsReplaying(slot) || IsFrozen(slot)))
                return;

            g_origCCSBotUpkeep(bot);
        }

        // ============================================================
        // Detour 7: CCSBot::UpdateLookAngles
        // ============================================================
        // 回放时或冻结时跳过弹簧平滑视角更新
        void PRL_FASTCALL HookedCCSBotUpdateLookAngles(void *bot)
        {
            g_ccsbotUpdateLookAnglesCalls.fetch_add(1, std::memory_order_relaxed);

            int slot = Slot::CCSBotContextToSlot(bot);
            if (ValidSlotIndex(slot) && (IsReplaying(slot) || IsFrozen(slot)))
                return;

            g_origCCSBotUpdateLookAngles(bot);
        }

        // ============================================================
        // Detour 8: CCSPlayerPawn::SetEyeAngles
        // ============================================================
        // 回放时跳过引擎的 SetEyeAngles 调用，改由 ApplyReplayEyeAngles
        // 主动调用原函数（临时清除 FL_FAKECLIENT 绕过早退路径）
        void PRL_FASTCALL HookedSetEyeAngles(void *pawn, float *angle)
        {
            g_setEyeAnglesCalls.fetch_add(1, std::memory_order_relaxed);

            int slot = pawn ? Slot::ControllerSlotForPawn(pawn) : -1;
            if (ValidSlotIndex(slot) && IsReplaying(slot))
            {
                return;
            }
            g_origSetEyeAngles(pawn, angle);
        }

        // ============================================================
        // vtable 懒加载 hook
        // ============================================================
        // ProcessMovement 首次调用时从 services vtable 读取 FinishMove / PlayerRunCommand 地址
        // 并安装 hook。这样无需在编译时硬编码 vtable 偏移。
        void EnsureVtableHooks(void *services)
        {
            // 仅执行一次（atomic CAS）
            if (g_vtHooksTried.exchange(true, std::memory_order_acq_rel))
                return;
            if (!services)
                return;

            // services 对象首成员是 vtable 指针
            void **vt = nullptr;
            if (!Mem::SafeRead(services, 0, vt) || !vt)
                return;

            // ---- FinishMove ----
            // vtable 中按索引读取：偏移 = index * sizeof(void*)
            if (!Mem::SafeRead(vt,
                               tg::kVtIdx_FinishMove * static_cast<int>(sizeof(void *)),
                               g_addrFinishMove))
                g_addrFinishMove = nullptr;

            if (g_addrFinishMove &&
                g_hookFinishMove.Create(g_addrFinishMove,
                                        reinterpret_cast<void *>(&HookedFinishMove),
                                        reinterpret_cast<void **>(&g_origFinishMove)))
            {
                g_hookFinishMove.Enable();
            }

            // ---- PlayerRunCommand ----
            if (!Mem::SafeRead(vt,
                               tg::kVtIdx_PlayerRunCommand * static_cast<int>(sizeof(void *)),
                               g_addrPlayerRunCommand))
                g_addrPlayerRunCommand = nullptr;

            if (g_addrPlayerRunCommand &&
                g_hookPlayerRunCommand.Create(g_addrPlayerRunCommand,
                                              reinterpret_cast<void *>(&HookedPlayerRunCommand),
                                              reinterpret_cast<void **>(&g_origPlayerRunCommand)) &&
                g_hookPlayerRunCommand.Enable())
            {
                g_subtickActive = true;
            }
            else if (g_addrPlayerRunCommand)
            {
                // 创建/启用失败：清理资源避免悬空
                g_hookPlayerRunCommand.Remove();
                g_addrPlayerRunCommand = nullptr;
                g_origPlayerRunCommand = nullptr;
            }

            char dbg[256];
            std::snprintf(dbg, sizeof(dbg),
                          "[PracLabReplayEngine] vtable hooks: FinishMove @ %p, "
                          "PlayerRunCommand @ %p (subtick=%d)\n",
                          g_addrFinishMove, g_addrPlayerRunCommand,
                          g_subtickActive ? 1 : 0);
            Platform::DebugOut(dbg);
        }
    } // namespace

    // ============================================================
    // 公开 API
    // ============================================================

    bool Install(const nlohmann::json &gd, const Sig::ModuleInfo &serverModule,
                 char *errorOut, size_t errorOutLen)
    {
        // ---- 1. ProcessMovement（必装） ----
        g_addrProcessMovement = Sig::ResolveSig(
            gd, serverModule, "CCSPlayer_MovementServices::ProcessMovement",
            errorOut, errorOutLen);
        if (!g_addrProcessMovement)
        {
            g_status = "failed: ProcessMovement sig";
            return false;
        }
        if (!g_hookProcessMovement.Create(g_addrProcessMovement,
                                          reinterpret_cast<void *>(&HookedProcessMovement),
                                          reinterpret_cast<void **>(&g_origProcessMovement)) ||
            !g_hookProcessMovement.Enable())
        {
            std::snprintf(errorOut, errorOutLen, "hook ProcessMovement failed");
            g_hookProcessMovement.Remove();
            g_origProcessMovement = nullptr;
            g_addrProcessMovement = nullptr;
            g_status = "failed: hook ProcessMovement";
            return false;
        }

        // ---- 2. PhysicsSimulate（可选：失败降级） ----
        char psErr[256] = {0};
        g_addrPhysicsSimulate = Sig::ResolveSig(
            gd, serverModule, "CBasePlayerController::OnSimulateUserCommands",
            psErr, sizeof(psErr));
        if (g_addrPhysicsSimulate &&
            g_hookPhysicsSimulate.Create(g_addrPhysicsSimulate,
                                          reinterpret_cast<void *>(&HookedPhysicsSimulate),
                                          reinterpret_cast<void **>(&g_origPhysicsSimulate)) &&
            g_hookPhysicsSimulate.Enable())
        {
            g_physicsActive = true;
        }
        else
        {
            // 降级：录制/回放退化为每 subtick 边界提交，可能造成轻微抖动
            if (g_addrPhysicsSimulate)
            {
                g_hookPhysicsSimulate.Remove();
                g_addrPhysicsSimulate = nullptr;
            }
            g_origPhysicsSimulate = nullptr;
            char dbg[320];
            std::snprintf(dbg, sizeof(dbg),
                          "[PracLabReplayEngine] WARN: PhysicsSimulate hook unavailable (%s); "
                          "replay falls back to per-subtick boundary (may stutter)\n",
                          psErr[0] ? psErr : "funchook failed");
            Platform::DebugOut(dbg);
        }

        // ---- 3. CCSBot::Update（可选） ----
        char buErr[256] = {0};
        g_addrCCSBotUpdate = Sig::ResolveSig(
            gd, serverModule, "CCSBot::Update", buErr, sizeof(buErr));
        if (g_addrCCSBotUpdate &&
            g_hookCCSBotUpdate.Create(g_addrCCSBotUpdate,
                                       reinterpret_cast<void *>(&HookedCCSBotUpdate),
                                       reinterpret_cast<void **>(&g_origCCSBotUpdate)) &&
            g_hookCCSBotUpdate.Enable())
        {
            // Bot AI 锁定功能就绪
        }
        else
        {
            if (g_addrCCSBotUpdate)
            {
                g_hookCCSBotUpdate.Remove();
                g_addrCCSBotUpdate = nullptr;
            }
            g_origCCSBotUpdate = nullptr;
            char dbg[256];
            std::snprintf(dbg, sizeof(dbg),
                          "[PracLabReplayEngine] WARN: CCSBot::Update hook unavailable (%s); "
                          "bot AI lock for Update disabled\n",
                          buErr[0] ? buErr : "funchook failed");
            Platform::DebugOut(dbg);
        }

        // ---- 4. CCSBot::Upkeep（可选） ----
        char bkErr[256] = {0};
        g_addrCCSBotUpkeep = Sig::ResolveSig(
            gd, serverModule, "CCSBot::Upkeep", bkErr, sizeof(bkErr));
        if (g_addrCCSBotUpkeep &&
            g_hookCCSBotUpkeep.Create(g_addrCCSBotUpkeep,
                                       reinterpret_cast<void *>(&HookedCCSBotUpkeep),
                                       reinterpret_cast<void **>(&g_origCCSBotUpkeep)) &&
            g_hookCCSBotUpkeep.Enable())
        {
            // 回放时跳过 Upkeep 视角维护就绪
        }
        else
        {
            if (g_addrCCSBotUpkeep)
            {
                g_hookCCSBotUpkeep.Remove();
                g_addrCCSBotUpkeep = nullptr;
            }
            g_origCCSBotUpkeep = nullptr;
            char dbg[256];
            std::snprintf(dbg, sizeof(dbg),
                          "[PracLabReplayEngine] WARN: CCSBot::Upkeep hook unavailable (%s); "
                          "replay Upkeep skip disabled\n",
                          bkErr[0] ? bkErr : "funchook failed");
            Platform::DebugOut(dbg);
        }

        // ---- 5. CCSBot::UpdateLookAngles（可选） ----
        // 仿照 CS2-Bot-Controller：回放时跳过引擎对 bot 视角的自主维护，
        // 防止覆盖回放注入的视角。
        char ulaErr[256] = {0};
        g_addrCCSBotUpdateLookAngles = Sig::ResolveSig(
            gd, serverModule, "CCSBot::UpdateLookAngles", ulaErr, sizeof(ulaErr));
        if (g_addrCCSBotUpdateLookAngles &&
            g_hookCCSBotUpdateLookAngles.Create(
                g_addrCCSBotUpdateLookAngles,
                reinterpret_cast<void *>(&HookedCCSBotUpdateLookAngles),
                reinterpret_cast<void **>(&g_origCCSBotUpdateLookAngles)) &&
            g_hookCCSBotUpdateLookAngles.Enable())
        {
            // 回放时跳过 UpdateLookAngles 就绪
        }
        else
        {
            if (g_addrCCSBotUpdateLookAngles)
            {
                g_hookCCSBotUpdateLookAngles.Remove();
                g_addrCCSBotUpdateLookAngles = nullptr;
            }
            g_origCCSBotUpdateLookAngles = nullptr;
            char dbg[256];
            std::snprintf(dbg, sizeof(dbg),
                          "[PracLabReplayEngine] WARN: CCSBot::UpdateLookAngles hook unavailable (%s); "
                          "replay lookangle skip disabled\n",
                          ulaErr[0] ? ulaErr : "funchook failed");
            Platform::DebugOut(dbg);
        }

        // ---- 6. CCSPlayerPawn::SetEyeAngles（可选，Windows 专用） ----
        // 仿照 CS2-Bot-Controller：回放时拦截引擎对 pawn 视角的写入，
        // 改由 ApplyReplayEyeAngles 主动调用原函数（临时清除 FL_FAKECLIENT
        // 绕过早退路径）。SetEyeAngles 也是 ReplayControllerForPawn 反查
        // controller 所需的 entity identity chunks 的来源。
        char seaErr[256] = {0};
        g_addrSetEyeAngles = Sig::ResolveSig(
            gd, serverModule, "CCSPlayerPawn::SetEyeAngles", seaErr, sizeof(seaErr));
        if (g_addrSetEyeAngles &&
            g_hookSetEyeAngles.Create(g_addrSetEyeAngles,
                                       reinterpret_cast<void *>(&HookedSetEyeAngles),
                                       reinterpret_cast<void **>(&g_origSetEyeAngles)) &&
            g_hookSetEyeAngles.Enable())
        {
#if defined(_WIN32)
            // 解析 SetEyeAngles 函数体内引用的 entity identity chunks 指针，
            // 供 ApplyReplayEyeAngles 反查 controller 临时清除 FL_FAKECLIENT。
            ResolveSetEyeAnglesEntityChunks(g_addrSetEyeAngles);
#endif
        }
        else
        {
            if (g_addrSetEyeAngles)
            {
                g_hookSetEyeAngles.Remove();
                g_addrSetEyeAngles = nullptr;
            }
            g_origSetEyeAngles = nullptr;
            char dbg[256];
            std::snprintf(dbg, sizeof(dbg),
                          "[PracLabReplayEngine] WARN: CCSPlayerPawn::SetEyeAngles hook unavailable (%s); "
                          "ApplyReplayEyeAngles disabled (viewangles will fall back to direct write)\n",
                          seaErr[0] ? seaErr : "funchook failed");
            Platform::DebugOut(dbg);
        }

        // FinishMove / PlayerRunCommand 在首次 ProcessMovement 调用时懒加载

        g_installed = true;
        g_status = "ok";
        char dbg[320];
        std::snprintf(dbg, sizeof(dbg),
                      "[PracLabReplayEngine] hooks installed: ProcessMovement @ %p, "
                      "PhysicsSimulate @ %p (active=%d), "
                      "CCSBot::Update @ %p, CCSBot::Upkeep @ %p, "
                      "CCSBot::UpdateLookAngles @ %p, CCSPlayerPawn::SetEyeAngles @ %p "
                      "(entityChunks=%p)\n",
                      g_addrProcessMovement, g_addrPhysicsSimulate,
                      g_physicsActive ? 1 : 0,
                      g_addrCCSBotUpdate, g_addrCCSBotUpkeep,
                      g_addrCCSBotUpdateLookAngles, g_addrSetEyeAngles,
#if defined(_WIN32)
                      g_ppEntityIdentityChunks
#else
                      nullptr
#endif
                      );
        Platform::DebugOut(dbg);
        return true;
    }

    void Remove()
    {
        if (!g_installed)
            return;

        g_hookProcessMovement.Remove();
        g_hookFinishMove.Remove();
        g_hookPlayerRunCommand.Remove();
        g_hookPhysicsSimulate.Remove();
        g_hookCCSBotUpdate.Remove();
        g_hookCCSBotUpkeep.Remove();
        g_hookCCSBotUpdateLookAngles.Remove();
        g_hookSetEyeAngles.Remove();

        g_origProcessMovement  = nullptr;
        g_origFinishMove       = nullptr;
        g_origPlayerRunCommand = nullptr;
        g_origPhysicsSimulate  = nullptr;
        g_origCCSBotUpdate     = nullptr;
        g_origCCSBotUpkeep     = nullptr;
        g_origCCSBotUpdateLookAngles = nullptr;
        g_origSetEyeAngles     = nullptr;

        g_addrProcessMovement  = nullptr;
        g_addrFinishMove       = nullptr;
        g_addrPlayerRunCommand = nullptr;
        g_addrPhysicsSimulate  = nullptr;
        g_addrCCSBotUpdate     = nullptr;
        g_addrCCSBotUpkeep     = nullptr;
        g_addrCCSBotUpdateLookAngles = nullptr;
        g_addrSetEyeAngles     = nullptr;

        g_physicsActive = false;
        g_subtickActive = false;
        g_vtHooksTried.store(false, std::memory_order_release);

        for (auto &s : g_slotServices)
            s.store(nullptr, std::memory_order_release);
        for (auto &p : g_slotPawns)
            p.store(nullptr, std::memory_order_release);

#if defined(_WIN32)
        g_ppEntityIdentityChunks = nullptr;
#endif

        g_installed = false;
        g_status = "removed";
        Platform::DebugOut("[PracLabReplayEngine] hooks removed\n");
    }

    const char *Status() { return g_status.c_str(); }

    bool IsPhysicsActive() { return g_physicsActive; }
    bool IsSubtickActive() { return g_subtickActive; }

    bool SetReplayPawn(int slot, void *pawn)
    {
        if (!ValidSlotIndex(slot))
            return false;

        // 先清空旧值，避免校验期间出现 race
        g_slotPawns[slot].store(nullptr, std::memory_order_release);
        if (!pawn)
            return false;

        // 校验 pawn 的 identity 与 EHandle 有效
        void *identity = nullptr;
        uint32_t handle = 0;
        if (!Mem::SafeRead(pawn, tg::kEnt_Identity, identity) || !identity ||
            !Mem::SafeRead(identity, tg::kEntIdentity_EHandle, handle) ||
            handle == 0u || handle == 0xFFFFFFFFu)
            return false;

        // 校验 controller 归属与 slot 一致
        int ownerSlot = Slot::ControllerSlotForPawn(pawn);
        if (ownerSlot >= 0 && ownerSlot != slot)
            return false;

        g_slotPawns[slot].store(pawn, std::memory_order_release);
        return true;
    }

    void ClearReplayPawn(int slot)
    {
        if (ValidSlotIndex(slot))
            g_slotPawns[slot].store(nullptr, std::memory_order_release);
    }

    void *ResolveReplayPawn(int slot, void *services)
    {
        return ResolveReplayPawnImpl(slot, services);
    }

    // ============================================================
    // ApplyReplayEyeAngles 公开接口
    // ============================================================
    // 供 replayer.cpp 在 OnReplayPre / OnReplayCommit 调用，
    // 通过原 SetEyeAngles 写入视角（临时清除 FL_FAKECLIENT 绕过早退）。
    // 返回 false 表示 hook 未就绪或 pawn 无效，调用方应回退到直接写字段。
    bool ApplyReplayEyeAngles(void *pawn, float pitch, float yaw)
    {
        return ApplyReplayEyeAnglesInternal(pawn, pitch, yaw);
    }

    // ============================================================
    // 诊断接口实现
    // ============================================================
    uint64_t ProcessMovementCalls()   { return g_processMovementCalls.load(std::memory_order_relaxed); }
    uint64_t FinishMoveCalls()        { return g_finishMoveCalls.load(std::memory_order_relaxed); }
    uint64_t PhysicsSimulateCalls()   { return g_physicsSimulateCalls.load(std::memory_order_relaxed); }
    int      LastProcessMovementSlot() { return g_lastSlot.load(std::memory_order_relaxed); }
    int      LastPhysicsSimulateSlot() { return g_lastPhysicsSlot.load(std::memory_order_relaxed); }

    // 新增诊断：用于排查 bot AI 是否被调用 / user command 是否被处理
    // ReplayPre 仅调用 1 次说明 ProcessMovement 没有为 replay bot 执行，
    // 通过对比 ccsbotUpdateCalls 与 playerRunCommandCalls 可定位是 AI 层未触发
    // 还是引擎未生成 user command
    uint64_t CCSBotUpdateCalls()      { return g_ccsbotUpdateCalls.load(std::memory_order_relaxed); }
    uint64_t CCSBotUpkeepCalls()      { return g_ccsbotUpkeepCalls.load(std::memory_order_relaxed); }
    uint64_t PlayerRunCommandCalls()  { return g_playerRunCommandCalls.load(std::memory_order_relaxed); }

    // 新增：UpdateLookAngles / SetEyeAngles 调用次数
    // 用于排查回放期间引擎是否仍在尝试覆盖 bot 视角
    uint64_t CCSBotUpdateLookAnglesCalls() { return g_ccsbotUpdateLookAnglesCalls.load(std::memory_order_relaxed); }
    uint64_t SetEyeAnglesCalls()           { return g_setEyeAnglesCalls.load(std::memory_order_relaxed); }

    // 冻结/解冻 slot 的 bot AI（转发到内部 SetFrozen）
    void SetSlotFrozen(int slot, bool frozen) { SetFrozen(slot, frozen); }
}
