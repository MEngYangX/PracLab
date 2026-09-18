// CS2 movement hooks
// ProcessMovement (record + apply pre)
// FinishMove (replay post into MoveData + commit)
// PlayerRunCommand(subtick record + re-inject)

#include "networkbasetypes.pb.h"
#include "nlohmann/json.hpp" // NOLINT(misc-include-cleaner)
#include "playercommand.h"

#include "InputInjector.h"
#include "ccsbot_slot.h"
#include "sig_scan.h"
#include "MotionRecorder.h"
#include "ProjectileBirthAlign.h"
#include "usercmd.pb.h"
#include "version_targets.h"
#include "hook.h"

#include <algorithm> // NOLINT(misc-include-cleaner)
#include <array>
#include <atomic>
#include <chrono>
#include <cstdint>
#include <cmath>
#include <cstdio>
#include <mutex>
#include <string>
#include <vector> // NOLINT(misc-include-cleaner)

#include <tier0/dbg.h>

namespace tg = bot_controller::targets;

using ProcessMovementT = void(BC_FASTCALL*)(void* services, void* moveData);
using FinishMoveT = void(BC_FASTCALL*)(void* services, void* cmd, void* moveData);
using PlayerRunCommandT = void(BC_FASTCALL*)(void* services, void* cmd);
using PhysicsSimulateT = void(BC_FASTCALL*)(void* controller);

namespace bot_controller {
namespace input_injector {

namespace {
constexpr float kUsercmdKeyboardMoveScale = 450.0F;
constexpr uint64_t kInForward = 1ULL << 3;
constexpr uint64_t kInBack = 1ULL << 4;
constexpr uint64_t kInMoveLeft = 1ULL << 9;
constexpr uint64_t kInMoveRight = 1ULL << 10;
constexpr uint64_t kMovementButtonMask = kInForward | kInBack | kInMoveLeft | kInMoveRight;

ProcessMovementT g_origProcessMovement = nullptr;
FinishMoveT g_origFinishMove = nullptr;
PlayerRunCommandT g_origPlayerRunCommand = nullptr;
PhysicsSimulateT g_origPhysicsSimulate = nullptr;

void* g_addrProcessMovement = nullptr;
void* g_addrFinishMove = nullptr;
void* g_addrPlayerRunCommand = nullptr;
void* g_addrPhysicsSimulate = nullptr;

Hook g_hookProcessMovement;
Hook g_hookFinishMove;
Hook g_hookPlayerRunCommand;
Hook g_hookPhysicsSimulate;
bool g_installed = false;
// True once PhysicsSimulate is hooked
bool g_physicsActive = false;
// True once PlayerRunCommand is hooked
bool g_subtickActive = false;
std::string g_status = "not_attempted"; // NOLINT(bugprone-throwing-static-initialization)

// slot -> live CCSPlayer_MovementServices*
std::array<std::atomic<void*>, kMaxSlots> g_slotServices{};
std::array<std::atomic<void*>, kMaxSlots> g_slotPawns{};

enum class UsercmdInjectionPhase : uint8_t
{
    PendingPress,
    Holding,
    PendingRelease
};

struct UsercmdInjection
{
    int64_t id;
    uint64_t buttonMask;
    int64_t expiresAtMs;
    int durationMs;
    UsercmdInjectionPhase phase;
};

struct UsercmdMovement
{
    int64_t id;
    float forwardMove;
    float leftMove;
};

struct UsercmdSuppression
{
    int64_t id;
    uint64_t buttonMask;
    int64_t expiresAtMs;
    bool persistent;
    bool releasePending;
};

std::array<std::vector<UsercmdInjection>, kMaxSlots> g_usercmdInjections{};
std::array<std::vector<UsercmdSuppression>, kMaxSlots> g_usercmdSuppressions{};
std::array<std::vector<UsercmdMovement>, kMaxSlots> g_usercmdMovements{};
std::array<uint64_t, kMaxSlots> g_injectedHeldMasks{};
std::array<uint64_t, kMaxSlots> g_movementHeldMasks{};
std::mutex g_usercmdInjectionMutex;
std::atomic<int64_t> g_nextUsercmdInjectionId{ 1 };
std::atomic<int64_t> g_nextUsercmdSuppressionId{ 1 };
std::atomic<int64_t> g_nextUsercmdMovementId{ 1 };

std::atomic<uint64_t> g_hookCalls{ 0 };
std::atomic<int> g_lastSlot{ -1 };
std::atomic<uint64_t> g_finishMoveCalls{ 0 };
std::atomic<uint64_t> g_playerRunCommandCalls{ 0 };
std::atomic<uint64_t> g_usercmdMovementApplyCalls{ 0 };
std::atomic<int> g_lastUsercmdMovementSlot{ -1 };
std::atomic<int> g_lastUsercmdForwardMove{ 0 };
std::atomic<int> g_lastUsercmdLeftMove{ 0 };
std::atomic<uint64_t> g_physicsSimulateCalls{ 0 };
std::atomic<int> g_lastPhysicsSlot{ -1 };
std::atomic<uint64_t> g_replayCommitCalls{ 0 };
std::atomic<uint64_t> g_slotResolveCalls{ 0 };
std::atomic<uint64_t> g_slotResolveFailures{ 0 };
std::atomic<uintptr_t> g_lastServices{ 0 };
std::atomic<uintptr_t> g_lastPawn{ 0 };
std::atomic<uint32_t> g_lastControllerHandle{ 0 };
std::atomic<uint32_t> g_lastOriginalControllerHandle{ 0 };
std::atomic<int> g_lastControllerIndex{ -1 };
std::atomic<int> g_lastOriginalControllerIndex{ -1 };
std::atomic<int> g_lastOwnerSlot{ -1 };

bool IsThrowableUtilityDef(int def) { return def >= 43 && def <= 48; }

// Reports whether a slot can index the fixed replay state arrays.
bool ValidSlotIndex(int slot) { return slot >= 0 && slot < kMaxSlots; }

// Reads the helper pawn field embedded in movement services.
void* ServicesToPawnField(void* services)
{
    void* pawn = nullptr;
    return GuardedRead(services, tg::g_servicesPawn, pawn) ? pawn : nullptr;
}

// Verifies that a pawn currently owns the supplied movement services.
bool PawnOwnsServices(void* pawn, void* services)
{
    if (!pawn || !services) return false;
    void* liveServices = nullptr;
    return GuardedRead(pawn, tg::g_pawnMovementServices, liveServices) && liveServices == services;
}

// Registers a readable pawn whose current owner matches the requested slot.
} // namespace

bool SetReplayPawn(int slot, void* pawn)
{
    if (!ValidSlotIndex(slot) || motion_recorder::IsReplaying(slot)) return false;
    g_slotPawns[slot].store(nullptr, std::memory_order_release);
    if (!pawn) return false;

    void* identity = nullptr;
    uint32_t handle = 0;
    if (!GuardedRead(pawn, tg::g_entIdentity, identity) || !identity || !GuardedRead(identity, tg::g_entIdentityEHandle, handle) ||
        handle == 0U || handle == 0xFFFFFFFFU)
        return false;

    int ownerSlot = ControllerSlotForPawn(pawn);
    if (ownerSlot >= 0 && ownerSlot != slot) return false;

    g_slotPawns[slot].store(pawn, std::memory_order_release);
    return true;
}

// Removes a registered pawn before the entity can be recycled.
void ClearReplayPawn(int slot)
{
    if (ValidSlotIndex(slot)) g_slotPawns[slot].store(nullptr, std::memory_order_release);
}

// Returns a registered pawn only when its movement-services link is current.
void* ResolveReplayPawn(int slot, void* services)
{
    if (ValidSlotIndex(slot))
    {
        void* registered = g_slotPawns[slot].load(std::memory_order_acquire);
        if (PawnOwnsServices(registered, services)) return registered;
    }

    void* fieldPawn = ServicesToPawnField(services);
    return PawnOwnsServices(fieldPawn, services) ? fieldPawn : nullptr;
}

// Finds a registered slot by validating every pawn-to-services link.
namespace {

int RegisteredSlotForServices(void* services)
{
    if (!services) return -1;
    for (int slot = 0; slot < kMaxSlots; ++slot)
    {
        void* pawn = g_slotPawns[slot].load(std::memory_order_acquire);
        if (PawnOwnsServices(pawn, services)) return slot;
    }
    return -1;
}

int ServicesToSlot(void* services)
{
    g_slotResolveCalls.fetch_add(1, std::memory_order_relaxed);
    g_lastServices.store(reinterpret_cast<uintptr_t>(services), std::memory_order_relaxed);
    g_lastPawn.store(0, std::memory_order_relaxed);
    g_lastControllerHandle.store(0, std::memory_order_relaxed);
    g_lastOriginalControllerHandle.store(0, std::memory_order_relaxed);
    g_lastControllerIndex.store(-1, std::memory_order_relaxed);
    g_lastOriginalControllerIndex.store(-1, std::memory_order_relaxed);
    g_lastOwnerSlot.store(-1, std::memory_order_relaxed);

    if (!services)
    {
        g_slotResolveFailures.fetch_add(1, std::memory_order_relaxed);
        return -1;
    }
    void* pawn = ServicesToPawnField(services);
    if (!PawnOwnsServices(pawn, services)) pawn = nullptr;

    PawnControllerHandles handles = ReadPawnControllerHandles(pawn);
    g_lastPawn.store(reinterpret_cast<uintptr_t>(pawn), std::memory_order_relaxed);
    g_lastControllerHandle.store(handles.controllerHandle, std::memory_order_relaxed);
    g_lastOriginalControllerHandle.store(handles.originalControllerHandle, std::memory_order_relaxed);
    g_lastControllerIndex.store(handles.controllerIndex, std::memory_order_relaxed);
    g_lastOriginalControllerIndex.store(handles.originalControllerIndex, std::memory_order_relaxed);
    int ownerSlot = handles.ownerSlot;
    if (ownerSlot < 0) ownerSlot = RegisteredSlotForServices(services);
    g_lastOwnerSlot.store(ownerSlot, std::memory_order_relaxed);
    if (ownerSlot < 0) g_slotResolveFailures.fetch_add(1, std::memory_order_relaxed);
    return ownerSlot;
}

// services -> pawn -> WeaponServices*, for the recording weapon tap.
void* ServicesToWeaponServices(int slot, void* services)
{
    void* pawn = ResolveReplayPawn(slot, services);
    if (!pawn) return nullptr;
    void* weaponServices = nullptr;
    return GuardedRead(pawn, tg::g_pawnWeaponServices, weaponServices) ? weaponServices : nullptr;
}

float NormalizeDeg(float a)
{
    a = std::fmod(a + 180.0F, 360.0F);
    if (a < 0.0F) a += 360.0F;
    return a - 180.0F;
}

// Returns monotonic time in milliseconds for injection expiry checks
int64_t MonotonicMilliseconds()
{
    return std::chrono::duration_cast<std::chrono::milliseconds>(std::chrono::steady_clock::now().time_since_epoch()).count();
}

// Creates an independently cancellable usercmd button injection
} // namespace

int64_t InjectUsercmd(int slot, uint64_t buttonMask, int durationMs)
{
    if (!ValidSlotIndex(slot) || buttonMask == 0 || durationMs < 0 || !g_subtickActive || motion_recorder::IsReplaying(slot)) return -1;

    int64_t id = g_nextUsercmdInjectionId.fetch_add(1, std::memory_order_relaxed);
    std::scoped_lock lock(g_usercmdInjectionMutex);
    if (motion_recorder::IsReplaying(slot)) return -1;
    g_usercmdInjections[slot].push_back(
        { .id = id, .buttonMask = buttonMask, .expiresAtMs = 0, .durationMs = durationMs, .phase = UsercmdInjectionPhase::PendingPress });
    return id;
}

// Creates an independently cancellable persistent analog movement override
int64_t StartUsercmdMovement(int slot, float forwardMove, float leftMove)
{
    if (!ValidSlotIndex(slot) || !std::isfinite(forwardMove) || !std::isfinite(leftMove) || !g_subtickActive ||
        motion_recorder::IsReplaying(slot))
        return -1;

    int64_t id = g_nextUsercmdMovementId.fetch_add(1, std::memory_order_relaxed);
    std::scoped_lock lock(g_usercmdInjectionMutex);
    if (motion_recorder::IsReplaying(slot)) return -1;
    g_usercmdMovements[slot].push_back(
        { .id = id, .forwardMove = std::clamp(forwardMove, -1.0F, 1.0F), .leftMove = std::clamp(leftMove, -1.0F, 1.0F) });
    return id;
}

// Updates one persistent analog movement override
bool UpdateUsercmdMovement(int slot, int64_t movementId, float forwardMove, float leftMove)
{
    if (!ValidSlotIndex(slot) || movementId <= 0 || !std::isfinite(forwardMove) || !std::isfinite(leftMove)) return false;

    std::scoped_lock lock(g_usercmdInjectionMutex);
    for (UsercmdMovement& movement : g_usercmdMovements[slot])
    {
        if (movement.id != movementId) continue;
        movement.forwardMove = std::clamp(forwardMove, -1.0F, 1.0F);
        movement.leftMove = std::clamp(leftMove, -1.0F, 1.0F);
        return true;
    }
    return false;
}

// Cancels one persistent analog movement override
bool CancelUsercmdMovement(int slot, int64_t movementId)
{
    if (!ValidSlotIndex(slot) || movementId <= 0) return false;

    std::scoped_lock lock(g_usercmdInjectionMutex);
    auto& movements = g_usercmdMovements[slot];
    for (auto it = movements.begin(); it != movements.end(); ++it)
    {
        if (it->id != movementId) continue;
        movements.erase(it);
        return true;
    }
    return false;
}

// Cancels one injection without affecting other active tokens
bool CancelUsercmdInjection(int slot, int64_t injectionId)
{
    if (!ValidSlotIndex(slot) || injectionId <= 0) return false;

    std::scoped_lock lock(g_usercmdInjectionMutex);
    auto& injections = g_usercmdInjections[slot];
    for (auto it = injections.begin(); it != injections.end(); ++it)
    {
        if (it->id != injectionId) continue;

        if (it->phase == UsercmdInjectionPhase::PendingPress) injections.erase(it);
        else
            it->phase = UsercmdInjectionPhase::PendingRelease;
        return true;
    }
    return false;
}

// Suppresses selected usercmd buttons until the requested duration expires
bool SuppressUsercmd(int slot, uint64_t buttonMask, int durationMs)
{
    if (!ValidSlotIndex(slot) || buttonMask == 0 || durationMs <= 0 || !g_subtickActive || motion_recorder::IsReplaying(slot)) return false;

    int64_t expiresAtMs = MonotonicMilliseconds() + durationMs;
    std::scoped_lock lock(g_usercmdInjectionMutex);
    if (motion_recorder::IsReplaying(slot)) return false;
    g_usercmdSuppressions[slot].push_back(
        { .id = 0, .buttonMask = buttonMask, .expiresAtMs = expiresAtMs, .persistent = false, .releasePending = true });
    return true;
}

// Creates an independently cancellable persistent usercmd suppression
int64_t StartUsercmdSuppression(int slot, uint64_t buttonMask)
{
    if (!ValidSlotIndex(slot) || buttonMask == 0 || !g_subtickActive || motion_recorder::IsReplaying(slot)) return -1;

    int64_t id = g_nextUsercmdSuppressionId.fetch_add(1, std::memory_order_relaxed);
    std::scoped_lock lock(g_usercmdInjectionMutex);
    if (motion_recorder::IsReplaying(slot)) return -1;
    g_usercmdSuppressions[slot].push_back(
        { .id = id, .buttonMask = buttonMask, .expiresAtMs = 0, .persistent = true, .releasePending = true });
    return id;
}

// Cancels one persistent usercmd suppression by its token
bool CancelUsercmdSuppression(int slot, int64_t suppressionId)
{
    if (!ValidSlotIndex(slot) || suppressionId <= 0) return false;

    std::scoped_lock lock(g_usercmdInjectionMutex);
    auto& suppressions = g_usercmdSuppressions[slot];
    for (auto it = suppressions.begin(); it != suppressions.end(); ++it)
    {
        if (it->id != suppressionId) continue;
        suppressions.erase(it);
        return true;
    }
    return false;
}

// Removes every injection and suppression so replay starts without deferred input
void ClearUsercmdInjections(int slot)
{
    if (!ValidSlotIndex(slot)) return;

    std::scoped_lock lock(g_usercmdInjectionMutex);
    g_usercmdInjections[slot].clear();
    g_usercmdSuppressions[slot].clear();
    g_usercmdMovements[slot].clear();
    g_injectedHeldMasks[slot] = 0;
    g_movementHeldMasks[slot] = 0;
}

// Reports whether a slot has injections waiting for command processing
namespace {

bool HasUsercmdInjection(int slot)
{
    if (!ValidSlotIndex(slot)) return false;

    std::scoped_lock lock(g_usercmdInjectionMutex);
    return !g_usercmdInjections[slot].empty();
}

// Reports whether a slot has button suppressions waiting for command processing
bool HasUsercmdSuppression(int slot)
{
    if (!ValidSlotIndex(slot)) return false;

    std::scoped_lock lock(g_usercmdInjectionMutex);
    return !g_usercmdSuppressions[slot].empty();
}

// Reports whether a slot has an active analog movement override
bool HasUsercmdMovement(int slot)
{
    if (!ValidSlotIndex(slot)) return false;

    std::scoped_lock lock(g_usercmdInjectionMutex);
    return !g_usercmdMovements[slot].empty();
}

// Replaces Bot AI analog movement after the final command is generated
bool ApplyUsercmdMovement(int slot, PlayerCommand* pc, CBaseUserCmdPB* base) // NOLINT(readability-non-const-parameter)
{
    if (!ValidSlotIndex(slot) || pc == nullptr || base == nullptr) return false;

    UsercmdMovement movement{};
    uint64_t previousMask = 0;
    {
        std::scoped_lock lock(g_usercmdInjectionMutex);
        const auto& movements = g_usercmdMovements[slot];
        if (movements.empty()) return false;
        movement = movements.back();
        previousMask = g_movementHeldMasks[slot];
    }

    uint64_t movementMask = 0;
    if (movement.forwardMove > 0.0F) movementMask |= kInForward;
    else if (movement.forwardMove < 0.0F)
        movementMask |= kInBack;
    if (movement.leftMove > 0.0F) movementMask |= kInMoveLeft;
    else if (movement.leftMove < 0.0F)
        movementMask |= kInMoveRight;

    {
        std::scoped_lock lock(g_usercmdInjectionMutex);
        g_movementHeldMasks[slot] = movementMask;
    }

    uint64_t pressedMask = movementMask & ~previousMask;
    uint64_t releasedMask = previousMask & ~movementMask;
    uint64_t held = (pc->buttonstates.m_pButtonStates[0] & ~kMovementButtonMask) | movementMask;
    uint64_t pressed = (pc->buttonstates.m_pButtonStates[1] & ~kMovementButtonMask) | pressedMask;
    uint64_t released = (pc->buttonstates.m_pButtonStates[2] & ~movementMask) | releasedMask;

    CInButtonStatePB* buttons = base->mutable_buttons_pb();
    buttons->set_buttonstate1(held);
    buttons->set_buttonstate2(pressed);
    buttons->set_buttonstate3(released);
    pc->buttonstates.m_pButtonStates[0] = held;
    pc->buttonstates.m_pButtonStates[1] = pressed;
    pc->buttonstates.m_pButtonStates[2] = released;

    base->set_forwardmove(movement.forwardMove * kUsercmdKeyboardMoveScale);
    base->set_leftmove(movement.leftMove * kUsercmdKeyboardMoveScale);
    g_usercmdMovementApplyCalls.fetch_add(1, std::memory_order_relaxed);
    g_lastUsercmdMovementSlot.store(slot, std::memory_order_relaxed);
    g_lastUsercmdForwardMove.store(static_cast<int>(std::lround(movement.forwardMove * kUsercmdKeyboardMoveScale)),
                                   std::memory_order_relaxed);
    g_lastUsercmdLeftMove.store(static_cast<int>(std::lround(movement.leftMove * kUsercmdKeyboardMoveScale)), std::memory_order_relaxed);
    for (int index = 0; index < base->subtick_moves_size(); ++index)
    {
        CSubtickMoveStep* step = base->mutable_subtick_moves(index);
        step->set_analog_forward_delta(0.0F);
        step->set_analog_left_delta(0.0F);
    }
    return true;
}

// Merges active injections and emits aggregate press and release edges
bool ApplyUsercmdInjections(int slot, PlayerCommand* pc, CBaseUserCmdPB* base)
{
    if (!ValidSlotIndex(slot) || !pc || !base) return false;

    uint64_t activeMask = 0;
    uint64_t previousMask = 0;
    {
        std::scoped_lock lock(g_usercmdInjectionMutex);
        auto& injections = g_usercmdInjections[slot];
        int64_t nowMs = MonotonicMilliseconds();
        for (auto it = injections.begin(); it != injections.end();)
        {
            UsercmdInjectionPhase phase = it->phase;
            if (phase == UsercmdInjectionPhase::PendingRelease || (phase == UsercmdInjectionPhase::Holding && nowMs >= it->expiresAtMs))
            {
                it = injections.erase(it);
                continue;
            }

            activeMask |= it->buttonMask;
            if (phase == UsercmdInjectionPhase::PendingPress)
            {
                if (it->durationMs > 0) it->expiresAtMs = nowMs + it->durationMs;
                it->phase = it->durationMs == 0 ? UsercmdInjectionPhase::PendingRelease : UsercmdInjectionPhase::Holding;
            }
            ++it;
        }

        previousMask = g_injectedHeldMasks[slot];
        g_injectedHeldMasks[slot] = activeMask;
    }

    uint64_t pressedMask = activeMask & ~previousMask;
    uint64_t releasedMask = previousMask & ~activeMask;
    uint64_t held = pc->buttonstates.m_pButtonStates[0];
    uint64_t pressed = pc->buttonstates.m_pButtonStates[1];
    uint64_t released = pc->buttonstates.m_pButtonStates[2];

    held = (held & ~releasedMask) | activeMask;
    pressed = (pressed & ~releasedMask) | pressedMask;
    released = (released & ~pressedMask) | releasedMask;

    CInButtonStatePB* buttons = base->mutable_buttons_pb();
    buttons->set_buttonstate1(held);
    buttons->set_buttonstate2(pressed);
    buttons->set_buttonstate3(released);
    pc->buttonstates.m_pButtonStates[0] = held;
    pc->buttonstates.m_pButtonStates[1] = pressed;
    pc->buttonstates.m_pButtonStates[2] = released;
    return true;
}

// Removes suppressed buttons after Bot AI has produced the final command
bool ApplyUsercmdSuppressions(int slot, PlayerCommand* pc, CBaseUserCmdPB* base) // NOLINT(readability-non-const-parameter)
{
    if (!ValidSlotIndex(slot) || !pc || !base) return false;

    uint64_t suppressedMask = 0;
    uint64_t releasedMask = 0;
    {
        std::scoped_lock lock(g_usercmdInjectionMutex);
        auto& suppressions = g_usercmdSuppressions[slot];
        int64_t nowMs = MonotonicMilliseconds();
        for (auto it = suppressions.begin(); it != suppressions.end();)
        {
            if (!it->persistent && nowMs >= it->expiresAtMs)
            {
                it = suppressions.erase(it);
                continue;
            }

            suppressedMask |= it->buttonMask;
            if (it->releasePending)
            {
                releasedMask |= it->buttonMask;
                it->releasePending = false;
            }
            ++it;
        }
    }

    if (suppressedMask == 0) return false;

    uint64_t held = pc->buttonstates.m_pButtonStates[0] & ~suppressedMask;
    uint64_t pressed = pc->buttonstates.m_pButtonStates[1] & ~suppressedMask;
    uint64_t released = pc->buttonstates.m_pButtonStates[2] | releasedMask;

    CInButtonStatePB* buttons = base->mutable_buttons_pb();
    buttons->set_buttonstate1(held);
    buttons->set_buttonstate2(pressed);
    buttons->set_buttonstate3(released);
    pc->buttonstates.m_pButtonStates[0] = held;
    pc->buttonstates.m_pButtonStates[1] = pressed;
    pc->buttonstates.m_pButtonStates[2] = released;

    for (int i = 0; i < base->subtick_moves_size(); ++i)
    {
        CSubtickMoveStep* move = base->mutable_subtick_moves(i);
        uint64_t buttonsMask = move->button() & ~suppressedMask;
        move->set_button(buttonsMask);
        if (buttonsMask == 0) move->set_pressed(false);
    }
    return true;
}

// Applies one recorded G-drop event through the bot client-command path
void ApplyReplayDrop(int slot, void* services)
{
    ReplayDropEvent event{};
    if (!motion_recorder::TakeCurrentReplayDrop(slot, event)) return;
    motion_recorder::DropReplayEventWeapon(slot, services, event);
}

// ---- ProcessMovement: record pre/post + replay pre ----

// Defined after HookedFinishMove
void EnsureVtableHooks(void* services);

void BC_FASTCALL HookedProcessMovement(void* services, void* moveData)
{
    g_hookCalls.fetch_add(1, std::memory_order_relaxed);
    int slot = ServicesToSlot(services);
    g_lastSlot.store(slot, std::memory_order_relaxed);

    // Lazily hook FinishMove from the live services vtable on first tick.
    EnsureVtableHooks(services);

    // Cache slot -> services so PhysicsSimulate
    if (slot >= 0 && slot < kMaxSlots) g_slotServices[slot].store(services, std::memory_order_release);

    bool recording = slot >= 0 && slot < kMaxSlots && motion_recorder::IsRecording(slot);
    bool replaying = slot >= 0 && slot < kMaxSlots && motion_recorder::IsReplaying(slot);

    // Recording weapon tap
    if (recording)
    {
        motion_recorder::SetLiveWs(slot, ServicesToWeaponServices(slot, services));
        if (!g_physicsActive) motion_recorder::OnCapturePre(slot, services, moveData);
    }

    // Replay: seed CMoveData + pawn with this tick's pre snapshot
    if (replaying) motion_recorder::OnReplayPre(slot, services, moveData);

    g_origProcessMovement(services, moveData);

    // Recording: commit the tick here only when PhysicsSimulate isn't the boundary
    if (recording && !g_physicsActive) motion_recorder::OnCapturePost(slot, services, moveData);
}

// ---- FinishMove: replay post-write + commit ----

void BC_FASTCALL HookedFinishMove(void* services, void* cmd, void* moveData)
{
    g_finishMoveCalls.fetch_add(1, std::memory_order_relaxed);
    int slot = ServicesToSlot(services);
    bool replaying = slot >= 0 && slot < kMaxSlots && motion_recorder::IsReplaying(slot);

    // Apply commands before FinishMove so their effects belong to this replay tick.
    if (replaying && !g_physicsActive) ApplyReplayDrop(slot, services);

    // Before original: write post snapshot into MoveData.
    if (replaying) motion_recorder::OnReplayFinishMove(slot, services, moveData);

    g_origFinishMove(services, cmd, moveData);

    // After original: commit moveType/flags + advance the replay cursor
    if (replaying && !g_physicsActive)
    {
        motion_recorder::OnReplayCommit(slot, services);
        g_replayCommitCalls.fetch_add(1, std::memory_order_relaxed);
    }
}

// ---- PlayerRunCommand: subtick record + re-inject ----

void BC_FASTCALL HookedPlayerRunCommand(void* services, void* cmd)
{
    projectile_birth_align::ProcessPending();
    g_playerRunCommandCalls.fetch_add(1, std::memory_order_relaxed);
    int slot = ServicesToSlot(services);
    bool recording = slot >= 0 && slot < kMaxSlots && motion_recorder::IsRecording(slot);
    bool replaying = slot >= 0 && slot < kMaxSlots && motion_recorder::IsReplaying(slot);
    bool hasUsercmdInjection = HasUsercmdInjection(slot);
    bool hasUsercmdSuppression = HasUsercmdSuppression(slot);
    bool hasUsercmdMovement = HasUsercmdMovement(slot);

    if (cmd && (recording || replaying || hasUsercmdInjection || hasUsercmdSuppression || hasUsercmdMovement))
    {
        // Compiler computes the multiple-inheritance adjust here.
        auto* pc = reinterpret_cast<PlayerCommand*>(cmd);
        CBaseUserCmdPB* base = pc->mutable_base();

        if (recording)
        {
            // Read this tick's subtick_moves into SubtickMove[] and
            // stash; OnCapturePost (PhysicsSimulate-post) commits them.
            int n = base->subtick_moves_size();
            n = std::min(n, motion_recorder::kMaxSubtickPerTick);
            SubtickMove moves[motion_recorder::kMaxSubtickPerTick];
            for (int i = 0; i < n; ++i)
            {
                const CSubtickMoveStep& s = base->subtick_moves(i);
                moves[i].when = s.when();
                moves[i].button = static_cast<uint32_t>(s.button());
                moves[i].pressed = s.pressed() ? 1.0F : 0.0F;
                moves[i].analogForward = s.analog_forward_delta();
                moves[i].analogLeft = s.analog_left_delta();
                moves[i].pitchDelta = s.pitch_delta();
                moves[i].yawDelta = s.yaw_delta();
            }
            motion_recorder::OnCaptureSubticks(slot, moves, n);

            ReplayCommandFrameData command{};
            command.buttons = pc->buttonstates.m_pButtonStates[0];
            command.buttons1 = pc->buttonstates.m_pButtonStates[1];
            command.buttons2 = pc->buttonstates.m_pButtonStates[2];
            command.fields |= motion_recorder::kCommandFieldButtons;
            if (base->has_forwardmove())
            {
                command.forwardMove = base->forwardmove();
                command.fields |= motion_recorder::kCommandFieldForwardMove;
            }
            if (base->has_leftmove())
            {
                command.leftMove = base->leftmove();
                command.fields |= motion_recorder::kCommandFieldLeftMove;
            }
            if (base->has_upmove())
            {
                command.upMove = base->upmove();
                command.fields |= motion_recorder::kCommandFieldUpMove;
            }
            if (base->has_viewangles())
            {
                const CMsgQAngle& view = base->viewangles();
                command.pitch = view.x();
                command.yaw = view.y();
                command.roll = view.z();
                command.fields |= motion_recorder::kCommandFieldViewAngles;
            }
            if (base->has_mousedx() || base->has_mousedy())
            {
                command.mouseDx = base->mousedx();
                command.mouseDy = base->mousedy();
                command.fields |= motion_recorder::kCommandFieldMouse;
            }
            if (base->has_weaponselect())
            {
                command.weaponSelect = base->weaponselect();
                command.fields |= motion_recorder::kCommandFieldWeaponSelect;
            }
            if (pc->has_left_hand_desired())
            {
                command.leftHandDesired = pc->left_hand_desired() ? 1 : 0;
                command.fields |= motion_recorder::kCommandFieldLeftHand;
            }
            motion_recorder::OnCaptureCommand(slot, command);
        }

        if (replaying)
        {
            constexpr uint64_t kGrenadeAttackMask = (1ULL << 0) | (1ULL << 11);
            motion_recorder::ReplayCommandFrame frame{};
            if (motion_recorder::ReplayCommandFrameForSimulation(slot, frame))
            {
                bool suppressUnsafeUtilityAttack =
                    IsThrowableUtilityDef(frame.tick.weaponDefIndex) && frame.weaponSelect < 0 &&
                    !motion_recorder::ReplayWeaponDefsMatch(motion_recorder::BotActiveWeaponDef(slot), frame.tick.weaponDefIndex);
                if (suppressUnsafeUtilityAttack)
                {
                    frame.buttons0 &= ~kGrenadeAttackMask;
                    frame.buttons1 &= ~kGrenadeAttackMask;
                    frame.buttons2 &= ~kGrenadeAttackMask;
                }

                CInButtonStatePB* bp = base->mutable_buttons_pb();
                bp->set_buttonstate1(frame.buttons0);
                bp->set_buttonstate2(frame.buttons1);
                bp->set_buttonstate3(frame.buttons2);
                pc->buttonstates.m_pButtonStates[0] = frame.buttons0;
                pc->buttonstates.m_pButtonStates[1] = frame.buttons1;
                pc->buttonstates.m_pButtonStates[2] = frame.buttons2;

                CMsgQAngle* view = base->mutable_viewangles();
                view->set_x(frame.commandView.pitch);
                view->set_y(NormalizeDeg(frame.commandView.yaw));
                view->set_z((frame.commandFields & motion_recorder::kCommandFieldViewAngles) != 0 ? frame.commandView.roll : 0.0F);

                if ((frame.commandFields & motion_recorder::kCommandFieldForwardMove) != 0) base->set_forwardmove(frame.forwardMove);
                if ((frame.commandFields & motion_recorder::kCommandFieldLeftMove) != 0) base->set_leftmove(frame.leftMove);
                if ((frame.commandFields & motion_recorder::kCommandFieldUpMove) != 0) base->set_upmove(frame.upMove);
                if ((frame.commandFields & motion_recorder::kCommandFieldMouse) != 0)
                {
                    base->set_mousedx(frame.mouseDx);
                    base->set_mousedy(frame.mouseDy);
                }
                if ((frame.commandFields & motion_recorder::kCommandFieldLeftHand) != 0)
                    pc->set_left_hand_desired(frame.leftHandDesired != 0);
                if (frame.weaponSelect >= 0) base->set_weaponselect(frame.weaponSelect);

                // Replace the command's subtick moves with the recorded set for this tick
                base->clear_subtick_moves();
                for (int i = 0; i < frame.subtickCount; ++i)
                {
                    uint32_t button = frame.subticks[i].button;
                    float pressed = frame.subticks[i].pressed;
                    if (suppressUnsafeUtilityAttack && (button & static_cast<uint32_t>(kGrenadeAttackMask)) != 0)
                    {
                        button &= ~static_cast<uint32_t>(kGrenadeAttackMask);
                        if (button == 0) pressed = 0.0F;
                    }

                    CSubtickMoveStep* m = base->add_subtick_moves();
                    m->set_when(frame.subticks[i].when);
                    m->set_button(button);
                    if (button != 0) // digital press/release
                        m->set_pressed(pressed != 0.0F);
                    if (frame.subticks[i].pitchDelta != 0.0F) m->set_pitch_delta(frame.subticks[i].pitchDelta);
                    if (frame.subticks[i].yawDelta != 0.0F) m->set_yaw_delta(frame.subticks[i].yawDelta);
                    if (frame.subticks[i].analogForward != 0.0F) m->set_analog_forward_delta(frame.subticks[i].analogForward);
                    if (frame.subticks[i].analogLeft != 0.0F) m->set_analog_left_delta(frame.subticks[i].analogLeft);
                }

                motion_recorder::OnReplayCommandPre(slot, services, frame.tick, frame.commandView);
            }
        }

        if (hasUsercmdSuppression && !replaying) ApplyUsercmdSuppressions(slot, pc, base);
        if (hasUsercmdInjection && !replaying) ApplyUsercmdInjections(slot, pc, base);
        if (hasUsercmdMovement && !replaying) ApplyUsercmdMovement(slot, pc, base);
    }

    g_origPlayerRunCommand(services, cmd);
}

// ---- PhysicsSimulate: the per-tick boundary ----
// Records pre/post + commits

void BC_FASTCALL HookedPhysicsSimulate(void* controller)
{
    projectile_birth_align::ProcessPending();
    g_physicsSimulateCalls.fetch_add(1, std::memory_order_relaxed);
    int slot = ControllerToSlot(controller);
    g_lastPhysicsSlot.store(slot, std::memory_order_relaxed);
    void* services = (slot >= 0 && slot < kMaxSlots) ? g_slotServices[slot].load(std::memory_order_acquire) : nullptr;

    bool recording = slot >= 0 && slot < kMaxSlots && services && motion_recorder::IsRecording(slot);
    bool replaying = slot >= 0 && slot < kMaxSlots && services && motion_recorder::IsReplaying(slot);

    // pre: snapshot start-of-tick state once (before any subtick mover).
    if (recording) motion_recorder::OnCapturePre(slot, services, nullptr);

    // Client commands are normally handled before this tick's player simulation.
    if (replaying) ApplyReplayDrop(slot, services);

    g_origPhysicsSimulate(controller);

    // post: snapshot end-of-tick state + commit one frame
    if (recording) motion_recorder::OnCapturePost(slot, services, nullptr);
    if (replaying)
    {
        motion_recorder::OnReplayCommit(slot, services);
        g_replayCommitCalls.fetch_add(1, std::memory_order_relaxed);
    }
}

std::atomic<bool> g_vtHooksTried{ false };

void EnsureVtableHooks(void* services)
{
    if (g_vtHooksTried.exchange(true, std::memory_order_acq_rel)) return;
    if (!services) return;
    void** vt = nullptr;
    if (!GuardedRead(services, 0, vt) || !vt) return;

    if (!GuardedRead(static_cast<const void*>(vt), tg::g_vtIdxFinishMove * static_cast<int>(sizeof(void*)), g_addrFinishMove))
        g_addrFinishMove = nullptr;
    if (g_addrFinishMove &&
        g_hookFinishMove.Create(g_addrFinishMove, reinterpret_cast<void*>(&HookedFinishMove), reinterpret_cast<void**>(&g_origFinishMove)))
        g_hookFinishMove.Enable();

    // PlayerRunCommand (subtick record/re-inject)
    if (!GuardedRead(static_cast<const void*>(vt), tg::g_vtIdxPlayerRunCommand * static_cast<int>(sizeof(void*)), g_addrPlayerRunCommand))
        g_addrPlayerRunCommand = nullptr;
    if (g_addrPlayerRunCommand &&
        g_hookPlayerRunCommand.Create(g_addrPlayerRunCommand, reinterpret_cast<void*>(&HookedPlayerRunCommand),
                                      reinterpret_cast<void**>(&g_origPlayerRunCommand)) &&
        g_hookPlayerRunCommand.Enable())
    {
        g_subtickActive = true;
    }
    else if (g_addrPlayerRunCommand)
    {
        g_hookPlayerRunCommand.Remove();
        g_addrPlayerRunCommand = nullptr;
        g_origPlayerRunCommand = nullptr;
    }
}

} // namespace

bool Install( // NOLINT(misc-use-internal-linkage)
    const nlohmann::json& gd,
    const sig::ModuleInfo& serverModule,
    char* errorOut,
    size_t errorOutLen) // NOLINT(misc-use-internal-linkage)
{
    g_addrProcessMovement = sig::ResolveSig(gd, serverModule, "CCSPlayer_MovementServices::ProcessMovement", errorOut, errorOutLen);
    if (!g_addrProcessMovement)
    {
        g_status = "failed: ProcessMovement sig";
        return false;
    }
    if (!g_hookProcessMovement.Create(g_addrProcessMovement, reinterpret_cast<void*>(&HookedProcessMovement),
                                      reinterpret_cast<void**>(&g_origProcessMovement)) ||
        !g_hookProcessMovement.Enable())
    {
        std::snprintf(errorOut, errorOutLen, "hook ProcessMovement failed");
        g_hookProcessMovement.Remove();
        g_origProcessMovement = nullptr;
        g_status = "failed: hook ProcessMovement";
        return false;
    }

    // PhysicsSimulate: the per-tick boundary
    char psErr[256] = { 0 };
    g_addrPhysicsSimulate = sig::ResolveSig(gd, serverModule, "CBasePlayerController::OnSimulateUserCommands", psErr, sizeof(psErr));
    if (g_addrPhysicsSimulate &&
        g_hookPhysicsSimulate.Create(g_addrPhysicsSimulate, reinterpret_cast<void*>(&HookedPhysicsSimulate),
                                     reinterpret_cast<void**>(&g_origPhysicsSimulate)) &&
        g_hookPhysicsSimulate.Enable())
    {
        g_physicsActive = true;
    }
    else
    {
        if (g_addrPhysicsSimulate)
        {
            g_hookPhysicsSimulate.Remove();
            g_addrPhysicsSimulate = nullptr;
        }
        g_origPhysicsSimulate = nullptr;
        Warning("[BotController] PhysicsSimulate hook unavailable (%s); replay falls back to per-subtick boundary (may stutter)\n",
                psErr[0] ? psErr : "funchook failed");
    }

    // FinishMove is hooked lazily from the live vtable on the first ProcessMovement tick.
    g_installed = true;
    g_status = "ok";
    return true;
}

void Remove()
{
    if (!g_installed) return;
    g_hookProcessMovement.Remove();
    g_hookFinishMove.Remove();
    g_hookPlayerRunCommand.Remove();
    g_hookPhysicsSimulate.Remove();
    g_origProcessMovement = nullptr;
    g_origFinishMove = nullptr;
    g_origPlayerRunCommand = nullptr;
    g_origPhysicsSimulate = nullptr;
    g_addrProcessMovement = nullptr;
    g_addrFinishMove = nullptr;
    g_addrPlayerRunCommand = nullptr;
    g_addrPhysicsSimulate = nullptr;
    g_physicsActive = false;
    g_subtickActive = false;
    g_vtHooksTried.store(false, std::memory_order_release);
    for (auto& s : g_slotServices)
        s.store(nullptr, std::memory_order_release);
    for (auto& pawn : g_slotPawns)
        pawn.store(nullptr, std::memory_order_release);
    {
        std::scoped_lock lock(g_usercmdInjectionMutex);
        for (auto& injections : g_usercmdInjections)
            injections.clear();
        for (auto& suppressions : g_usercmdSuppressions)
            suppressions.clear();
        for (auto& movements : g_usercmdMovements)
            movements.clear();
        g_injectedHeldMasks.fill(0);
        g_movementHeldMasks.fill(0);
    }
    g_installed = false;
    g_status = "not_attempted";
}

const char* Status() { return g_status.c_str(); }

void* ProcessUsercmdAddress() { return g_addrProcessMovement; }

uint64_t HookCallCount() { return g_hookCalls.load(std::memory_order_relaxed); }
int LastResolvedSlot() { return g_lastSlot.load(std::memory_order_relaxed); }
uint64_t FinishMoveCallCount() { return g_finishMoveCalls.load(std::memory_order_relaxed); }
uint64_t PlayerRunCommandCallCount() { return g_playerRunCommandCalls.load(std::memory_order_relaxed); }
// Reports how many final bot commands received a movement override
uint64_t UsercmdMovementApplyCount() { return g_usercmdMovementApplyCalls.load(std::memory_order_relaxed); }
// Reports the last slot whose final bot command was overridden
int LastUsercmdMovementSlot() { return g_lastUsercmdMovementSlot.load(std::memory_order_relaxed); }
// Reports the last forward command magnitude written by the override
int LastUsercmdForwardMove() { return g_lastUsercmdForwardMove.load(std::memory_order_relaxed); }
// Reports the last left command magnitude written by the override
int LastUsercmdLeftMove() { return g_lastUsercmdLeftMove.load(std::memory_order_relaxed); }
uint64_t PhysicsSimulateCallCount() { return g_physicsSimulateCalls.load(std::memory_order_relaxed); }
int LastPhysicsSlot() { return g_lastPhysicsSlot.load(std::memory_order_relaxed); }
uint64_t ReplayCommitCount() { return g_replayCommitCalls.load(std::memory_order_relaxed); }
uint64_t SlotResolveCallCount() { return g_slotResolveCalls.load(std::memory_order_relaxed); }
uint64_t SlotResolveFailureCount() { return g_slotResolveFailures.load(std::memory_order_relaxed); }
uintptr_t LastServices() { return g_lastServices.load(std::memory_order_relaxed); }
uintptr_t LastPawn() { return g_lastPawn.load(std::memory_order_relaxed); }
uint32_t LastControllerHandle() { return g_lastControllerHandle.load(std::memory_order_relaxed); }
uint32_t LastOriginalControllerHandle() { return g_lastOriginalControllerHandle.load(std::memory_order_relaxed); }
int LastControllerIndex() { return g_lastControllerIndex.load(std::memory_order_relaxed); }
int LastOriginalControllerIndex() { return g_lastOriginalControllerIndex.load(std::memory_order_relaxed); }
int LastOwnerSlot() { return g_lastOwnerSlot.load(std::memory_order_relaxed); }
} // namespace input_injector
} // namespace bot_controller
