// CCSBot Update/Upkeep detours

#include "BotController.h"
#include "BotControllerState.h"
#include "ccsbot_slot.h"
#include "nlohmann/json.hpp"
#include "sig_scan.h"
#include "MotionRecorder.h"
#include "version_targets.h"
#include "hook.h"

#include <tier0/dbg.h>

#include <cstdint>
#include <cstdio>
#include <cmath>
#include <cstring>
#include <mutex>
#include <string>

namespace tg = bot_controller::targets;

using UpdateT = void(BC_FASTCALL*)(void* bot);
using UpkeepT = void(BC_FASTCALL*)(void* bot);
using UpdateLookAnglesT = void(BC_FASTCALL*)(void* bot);
using SetEyeAnglesT = void(BC_FASTCALL*)(void* pawn, float* angle);

namespace bot_controller {
namespace bot_controller_hooks {

namespace {
UpdateT g_origUpdate = nullptr;
void* g_addrUpdate = nullptr;
UpkeepT g_origUpkeep = nullptr;
void* g_addrUpkeep = nullptr;
UpdateLookAnglesT g_origUpdateLookAngles = nullptr;
void* g_addrUpdateLookAngles = nullptr;
SetEyeAnglesT g_origSetEyeAngles = nullptr;
void* g_addrSetEyeAngles = nullptr;
#ifdef _WIN32
void** g_entityIdentityChunks = nullptr;
#endif
bool g_installed = false;
std::string g_status = "not_attempted"; // NOLINT(bugprone-throwing-static-initialization)

// slot -> last CCSBot* seen in Update (for profile reads by slot)
void* g_slotToBot[64] = { nullptr };
std::mutex g_slotToBotMu;

Hook g_hookUpdate;
Hook g_hookUpkeep;
Hook g_hookUpdateLookAngles;
Hook g_hookSetEyeAngles;

// Normalizes an angle to the engine's expected [-180, 180) range.
float NormalizeDeg(float angle)
{
    angle = std::fmod(angle + 180.0F, 360.0F);
    if (angle < 0.0F) angle += 360.0F;
    return angle - 180.0F;
}

#ifdef _WIN32
// Resolves the entity identity chunk pointer referenced by SetEyeAngles.
void ResolveSetEyeAnglesEntityChunks(void* setEyeAngles)
{
    g_entityIdentityChunks = nullptr;
    if (!setEyeAngles) return;

    constexpr size_t kSearchBytes = 0x120;
    uint8_t code[kSearchBytes] = {};
    if (!TryReadMemory(setEyeAngles, 0, code, sizeof(code))) return;

    auto* functionBase = reinterpret_cast<uint8_t*>(setEyeAngles);
    for (size_t i = 0; i + 10 <= kSearchBytes; ++i)
    {
        if (code[i] != 0x4C || code[i + 1] != 0x8B || code[i + 2] != 0x05 || code[i + 7] != 0x4D || code[i + 8] != 0x85 ||
            code[i + 9] != 0xC0)
            continue;

        int32_t relative = 0;
        std::memcpy(&relative, code + i + 3, sizeof(relative));
        g_entityIdentityChunks = reinterpret_cast<void**>(functionBase + i + 7 + relative);
        return;
    }
}

// Resolves the live controller owning a replay pawn through entity chunks.
void* ReplayControllerForPawn(void* pawn)
{
    if (!pawn || !g_entityIdentityChunks) return nullptr;

    uint32_t handle = 0;
    if (!SafeRead(pawn, tg::g_pawnController, handle) || handle == 0xFFFFFFFFU || handle == 0xFFFFFFFEU) return nullptr;

    void* chunks = nullptr;
    if (!TryReadMemory(static_cast<const void*>(g_entityIdentityChunks), 0, static_cast<void*>(&chunks), sizeof(chunks)) || !chunks)
        return nullptr;

    const uint32_t entityIndex = handle & 0x7FFFU;
    void* chunk = nullptr;
    if (!TryReadMemory(chunks, static_cast<int>((entityIndex >> 9) * sizeof(void*)), static_cast<void*>(&chunk), sizeof(chunk)) || !chunk)
        return nullptr;

    constexpr int kIdentitySize = 0x70;
    auto* identity = reinterpret_cast<uint8_t*>(chunk) + (static_cast<size_t>(entityIndex & 0x1FFU) * kIdentitySize);
    uint32_t liveHandle = 0;
    void* controller = nullptr;
    if (!SafeRead(identity, 0x10, liveHandle) || liveHandle != handle || !SafeRead(identity, 0x00, controller)) return nullptr;
    return controller;
}
#endif

// Calls SetEyeAngles while temporarily bypassing the new fake-client early-out.
bool ApplyReplayEyeAnglesInternal(void* pawn, float pitch, float yaw)
{
    if (!pawn || !g_origSetEyeAngles) return false;

    float angle[3] = { pitch, NormalizeDeg(yaw), 0.0F };
#ifdef _WIN32
    void* controller = ReplayControllerForPawn(pawn);
    uint32_t controllerFlags = 0;
    bool restoreFakeClient = false;
    if (controller && SafeRead(controller, tg::g_entFlags, controllerFlags) && (controllerFlags & 0x100U) != 0)
    {
        const uint32_t publishedFlags = controllerFlags & ~0x100U;
        restoreFakeClient = WriteField(controller, tg::g_entFlags, publishedFlags);
    }
#endif
    g_origSetEyeAngles(pawn, angle);
#ifdef _WIN32
    if (restoreFakeClient) WriteField(controller, tg::g_entFlags, controllerFlags);
#endif
    return true;
}

// Skip the Bot tick under All lock OR while replaying
void BC_FASTCALL HookedUpdate(void* bot)
{
    int slot = CCSBotToSlot(bot);
    if (slot >= 0 && slot < 64)
    {
        std::scoped_lock lk(g_slotToBotMu);
        g_slotToBot[slot] = bot;
    }
    if (slot >= 0 && (bot_controller_state::GetAll(slot) || motion_recorder::IsReplaying(slot)))
    {
        const uint8_t ticked = 1;
        WriteField(bot, tg::g_botAiTickedFlag, ticked);
        return;
    }
    g_origUpdate(bot);
}

// Skip the per-frame view tick under All or Aim lock.
// EXCEPTION: while a slot is replaying, drive ONLY the view
static void BC_FASTCALL HookedUpdateLookAngles(void* bot); // fwd decl

void BC_FASTCALL HookedUpkeep(void* bot)
{
    int slot = CCSBotContextToSlot(bot);
    if (slot >= 0 && motion_recorder::IsReplaying(slot)) return;
    if (slot >= 0 && (bot_controller_state::GetAll(slot) || bot_controller_state::GetAim(slot)))
    {
        return;
    }
    g_origUpkeep(bot);
}

// view replay
void BC_FASTCALL HookedUpdateLookAngles(void* bot)
{
    int slot = CCSBotContextToSlot(bot);
    if (slot >= 0 && (motion_recorder::IsReplaying(slot) || bot_controller_state::GetAll(slot) || bot_controller_state::GetAim(slot)))
        return;
    g_origUpdateLookAngles(bot);
}

// Engine eye-angle
void BC_FASTCALL HookedSetEyeAngles(void* pawn, float* angle)
{
    int slot = pawn ? ControllerSlotForPawn(pawn) : -1;
    if (slot >= 0 && motion_recorder::IsReplaying(slot)) return;
    g_origSetEyeAngles(pawn, angle);
}

// Resolve a sig from gamedata against the loaded server.dll.
} // namespace

bool Install(const nlohmann::json& gd, const sig::ModuleInfo& serverModule, char* errorOut, size_t errorOutLen)
{
    g_addrUpdate = sig::ResolveSig(gd, serverModule, "CCSBot::Update", errorOut, errorOutLen);
    if (!g_addrUpdate)
    {
        g_status = "failed: Update sig";
        return false;
    }

    g_addrUpkeep = sig::ResolveSig(gd, serverModule, "CCSBot::Upkeep", errorOut, errorOutLen);
    if (!g_addrUpkeep)
    {
        g_status = "failed: Upkeep sig";
        return false;
    }

    // UpdateLookAngles is optional
    char ulaErr[256] = { 0 };
    g_addrUpdateLookAngles = sig::ResolveSig(gd, serverModule, "CCSBot::UpdateLookAngles", ulaErr, sizeof(ulaErr));
    if (!g_addrUpdateLookAngles)
    {
        Warning("[BotController] CCSBot::UpdateLookAngles sig not resolved (%s); replay view-drive disabled\n", ulaErr);
    }

    // SetEyeAngles is optional; without it replay view falls back to
    // the (smoothing) UpdateLookAngles hook only.
    char seaErr[256] = { 0 };
    g_addrSetEyeAngles = sig::ResolveSig(gd, serverModule, "CCSPlayerPawn::SetEyeAngles", seaErr, sizeof(seaErr));
    if (!g_addrSetEyeAngles)
    {
        Warning("[BotController] CCSPlayerPawn::SetEyeAngles sig not resolved (%s); replay 1:1 view disabled\n", seaErr);
    }
#ifdef _WIN32
    else
    {
        ResolveSetEyeAnglesEntityChunks(g_addrSetEyeAngles);
    }
#endif

    // required: Update
    if (!g_hookUpdate.Create(g_addrUpdate, reinterpret_cast<void*>(&HookedUpdate), reinterpret_cast<void**>(&g_origUpdate)) ||
        !g_hookUpdate.Enable())
    {
        std::snprintf(errorOut, errorOutLen, "hook CCSBot::Update failed");
        g_hookUpdate.Remove();
        g_origUpdate = nullptr;
        g_status = "failed: hook Update";
        return false;
    }

    // required: Upkeep
    if (!g_hookUpkeep.Create(g_addrUpkeep, reinterpret_cast<void*>(&HookedUpkeep), reinterpret_cast<void**>(&g_origUpkeep)) ||
        !g_hookUpkeep.Enable())
    {
        std::snprintf(errorOut, errorOutLen, "hook CCSBot::Upkeep failed");
        g_hookUpkeep.Remove();
        g_origUpkeep = nullptr;
        g_hookUpdate.Remove();
        g_origUpdate = nullptr;
        g_status = "failed: hook Upkeep";
        return false;
    }

    // optional: UpdateLookAngles
    if (g_addrUpdateLookAngles)
    {
        if (!g_hookUpdateLookAngles.Create(g_addrUpdateLookAngles, reinterpret_cast<void*>(&HookedUpdateLookAngles),
                                           reinterpret_cast<void**>(&g_origUpdateLookAngles)) ||
            !g_hookUpdateLookAngles.Enable())
        {
            Warning("[BotController] hook UpdateLookAngles failed; replay view-drive disabled\n");
            g_hookUpdateLookAngles.Remove();
            g_origUpdateLookAngles = nullptr;
            g_addrUpdateLookAngles = nullptr;
        }
    }

    // optional: SetEyeAngles
    if (g_addrSetEyeAngles)
    {
        if (!g_hookSetEyeAngles.Create(g_addrSetEyeAngles, reinterpret_cast<void*>(&HookedSetEyeAngles),
                                       reinterpret_cast<void**>(&g_origSetEyeAngles)) ||
            !g_hookSetEyeAngles.Enable())
        {
            Warning("[BotController] hook SetEyeAngles failed; replay 1:1 view disabled\n");
            g_hookSetEyeAngles.Remove();
            g_origSetEyeAngles = nullptr;
            g_addrSetEyeAngles = nullptr;
        }
    }

    g_installed = true;
    g_status = "ok";
    return true;
}

void Remove()
{
    if (!g_installed) return;
    g_hookSetEyeAngles.Remove();
    g_origSetEyeAngles = nullptr;
#ifdef _WIN32
    g_entityIdentityChunks = nullptr;
#endif
    g_hookUpdateLookAngles.Remove();
    g_origUpdateLookAngles = nullptr;
    g_hookUpkeep.Remove();
    g_origUpkeep = nullptr;
    g_hookUpdate.Remove();
    g_origUpdate = nullptr;
    g_installed = false;
    g_status = "not_attempted";
    {
        std::scoped_lock lk(g_slotToBotMu);
        for (auto& i : g_slotToBot)
            i = nullptr;
    }
}

const char* Status() { return g_status.c_str(); }
void* UpdateAddress() { return g_addrUpdate; }
void* UpkeepAddress() { return g_addrUpkeep; }
void* UpdateLookAnglesAddress() { return g_addrUpdateLookAngles; }

// Publishes a replay angle without depending on the bot upkeep path.
bool ApplyReplayEyeAngles(void* pawn, float pitch, float yaw) { return ApplyReplayEyeAnglesInternal(pawn, pitch, yaw); }

// Last CCSBot* seen in Update for this slot
void* BotForSlot(int slot)
{
    if (slot < 0 || slot >= 64) return nullptr;
    std::scoped_lock lk(g_slotToBotMu);
    return g_slotToBot[slot];
}
} // namespace bot_controller_hooks
} // namespace bot_controller
