// Detours for CCSBot::EquipBestWeapon, EquipPistol, and
// CCSPlayer_WeaponServices::SelectItem

#include "WeaponLocker.h"
#include "nlohmann/json.hpp"
#include "sig_scan.h"
#include "WeaponLockerState.h"
#include "ccsbot_slot.h"
#include "MotionRecorder.h"
#include "version_targets.h"
#include "hook.h"

#include <cstdint>
#include <cstdio>
#include <string>
#include <mutex>
#include <unordered_map>

namespace tg = bot_controller::targets;

using EquipBestWeaponT = void(BC_FASTCALL*)(void* self, char mustEquip);
using EquipPistolT = void(BC_FASTCALL*)(void* self, char mustEquip);
using SelectItemT = char(BC_FASTCALL*)(void* ws, void* weapon, int flag);
using GetSlotT = void*(BC_FASTCALL*)(void* ws, int slot, unsigned int mask);

namespace bot_controller {
namespace weapon_locker_hooks {

namespace {
EquipBestWeaponT g_origEquipBestWeapon = nullptr;
EquipPistolT g_origEquipPistol = nullptr;
SelectItemT g_origSelectItem = nullptr;
GetSlotT g_getSlot = nullptr;

void* g_addrEquipBestWeapon = nullptr;
void* g_addrEquipPistol = nullptr;
void* g_addrSelectItem = nullptr;
void* g_addrGetSlot = nullptr;

Hook g_hookEquipBestWeapon;
Hook g_hookEquipPistol;
Hook g_hookSelectItem;

std::string g_status = "not_attempted"; // NOLINT(bugprone-throwing-static-initialization)
bool g_installed = false;

// WeaponServices* -> (bot slot, pawn)
struct WsBinding
{
    int slot;
    void* pawn;
};
std::unordered_map<void*, WsBinding> g_wsToBinding; // NOLINT(bugprone-throwing-static-initialization)
// Inverse: slot -> WeaponServices*
void* g_slotToWs[64] = { nullptr };
std::mutex g_wsToSlotMu;

void RememberWsForBot(void* bot, int slot)
{
    if (!bot || slot < 0 || slot >= 64) return;
    void* pawn = nullptr;
    if (!GuardedRead(bot, tg::g_botPawn, pawn)) return;
    if (!pawn) return;
    void* ws = nullptr;
    if (!GuardedRead(pawn, tg::g_pawnWeaponServices, ws)) return;
    if (!ws) return;
    std::scoped_lock lk(g_wsToSlotMu);
    g_wsToBinding[ws] = { .slot = slot, .pawn = pawn };
    g_slotToWs[slot] = ws;
}

WsBinding LookupBindingForWs(void* ws)
{
    if (!ws) return { .slot = -1, .pawn = nullptr };
    std::scoped_lock lk(g_wsToSlotMu);
    auto it = g_wsToBinding.find(ws);
    return it == g_wsToBinding.end() ? WsBinding{ .slot = -1, .pawn = nullptr } : it->second;
}

// LockTarget -> engine weapon-slot index
int LockTargetToEngineSlot(LockTarget t)
{
    int v = static_cast<int>(t);
    if (v < 1 || v > 5) return -1;
    return v - 1;
}

bool IsGrenadeDef(int def) { return def >= 43 && def <= 48; }

// ---- detours ----

void BC_FASTCALL HookedEquipBestWeapon(void* bot, char mustEquip)
{
    auto sr = ResolveSlot(bot);
    if (sr.slot >= 0) RememberWsForBot(bot, sr.slot);
    if (sr.slot >= 0 && motion_recorder::IsReplaying(sr.slot)) return;
    LockTarget lt = (sr.slot >= 0) ? weapon_locker_state::Get(sr.slot) : LockTarget::None;
    if (lt != LockTarget::None) return;
    g_origEquipBestWeapon(bot, mustEquip);
}

void BC_FASTCALL HookedEquipPistol(void* bot, char mustEquip)
{
    auto sr = ResolveSlot(bot);
    if (sr.slot >= 0) RememberWsForBot(bot, sr.slot);
    if (sr.slot >= 0 && motion_recorder::IsReplaying(sr.slot)) return;
    LockTarget lt = (sr.slot >= 0) ? weapon_locker_state::Get(sr.slot) : LockTarget::None;
    if (lt != LockTarget::None) return;
    g_origEquipPistol(bot, mustEquip);
}

char BC_FASTCALL HookedSelectItem(void* ws, void* weapon, int flag)
{
    // Recording : a human switching weapons calls SelectItem
    if (weapon)
    {
        int def = ReadDefIndex(weapon);
        if (def >= 0)
            for (int s = 0; s < motion_recorder::kMaxSlots; ++s)
            {
                if (motion_recorder::IsRecording(s) && motion_recorder::LiveWs(s) == ws) motion_recorder::SetCurrentDef(s, def);
            }
    }

    WsBinding bind = LookupBindingForWs(ws);
    if (bind.slot < 0) return g_origSelectItem(ws, weapon, flag);

    // Human took over this pawn -> current m_hController != bot slot
    // we cached; don't block player's weapon switches.
    int curSlot = ControllerSlotForPawn(bind.pawn);
    if (curSlot != bind.slot) return g_origSelectItem(ws, weapon, flag);

    if (motion_recorder::IsReplaying(bind.slot)) return g_origSelectItem(ws, weapon, flag);

    LockTarget lt = weapon_locker_state::Get(bind.slot);
    if (lt == LockTarget::None) return g_origSelectItem(ws, weapon, flag);

    int engineSlot = LockTargetToEngineSlot(lt);
    if (engineSlot < 0 || !g_getSlot) return g_origSelectItem(ws, weapon, flag);

    if (engineSlot == 3 && weapon && IsGrenadeDef(ReadDefIndex(weapon))) return g_origSelectItem(ws, weapon, flag);

    void* targetWeapon = g_getSlot(ws, engineSlot, 0xFFFFFFFFU);
    // No weapon in the locked slot -> can't enforce, let it through.
    if (!targetWeapon) return g_origSelectItem(ws, weapon, flag);

    // Switch is to the lock target -> allow.
    if (weapon == targetWeapon) return g_origSelectItem(ws, weapon, flag);

    // Switch is to something else -> block.
    return 0;
}

// ---- install / remove ----

} // namespace

bool Install(const nlohmann::json& gd, const sig::ModuleInfo& serverModule, char* errorOut, size_t errorOutLen)
{
    g_addrEquipBestWeapon = sig::ResolveSig(gd, serverModule, "CCSBot::EquipBestWeapon", errorOut, errorOutLen);
    if (!g_addrEquipBestWeapon)
    {
        g_status = "failed: EquipBestWeapon sig";
        return false;
    }

    g_addrEquipPistol = sig::ResolveSig(gd, serverModule, "CCSBot::EquipPistol", errorOut, errorOutLen);
    if (!g_addrEquipPistol)
    {
        g_status = "failed: EquipPistol sig";
        return false;
    }

    g_addrSelectItem = sig::ResolveSig(gd, serverModule, "CCSPlayer_WeaponServices::SelectItem", errorOut, errorOutLen);
    if (!g_addrSelectItem)
    {
        g_status = "failed: SelectItem sig";
        return false;
    }

    g_addrGetSlot = sig::ResolveSig(gd, serverModule, "CCSPlayer_WeaponServices::GetSlot", errorOut, errorOutLen);
    if (!g_addrGetSlot)
    {
        g_status = "failed: GetSlot sig";
        return false;
    }
    g_getSlot = reinterpret_cast<GetSlotT>(g_addrGetSlot);

    auto failCleanup = [&](const char* what) -> bool {
        std::snprintf(errorOut, errorOutLen, "%s failed", what);
        g_hookEquipBestWeapon.Remove();
        g_hookEquipPistol.Remove();
        g_hookSelectItem.Remove();
        g_origEquipBestWeapon = nullptr;
        g_origEquipPistol = nullptr;
        g_origSelectItem = nullptr;
        return false;
    };

    if (!g_hookEquipBestWeapon.Create(g_addrEquipBestWeapon, reinterpret_cast<void*>(&HookedEquipBestWeapon),
                                      reinterpret_cast<void**>(&g_origEquipBestWeapon)))
    {
        g_status = "failed: Create EquipBestWeapon";
        return failCleanup("Create EquipBestWeapon");
    }

    if (!g_hookEquipPistol.Create(g_addrEquipPistol, reinterpret_cast<void*>(&HookedEquipPistol),
                                  reinterpret_cast<void**>(&g_origEquipPistol)))
    {
        g_status = "failed: Create EquipPistol";
        return failCleanup("Create EquipPistol");
    }

    if (!g_hookSelectItem.Create(g_addrSelectItem, reinterpret_cast<void*>(&HookedSelectItem), reinterpret_cast<void**>(&g_origSelectItem)))
    {
        g_status = "failed: Create SelectItem";
        return failCleanup("Create SelectItem");
    }

    if (!g_hookEquipBestWeapon.Enable() || !g_hookEquipPistol.Enable() || !g_hookSelectItem.Enable())
    {
        g_status = "failed: Enable";
        return failCleanup("Enable");
    }

    g_installed = true;
    g_status = "ok";
    return true;
}

void Remove()
{
    if (!g_installed) return;
    g_hookSelectItem.Remove();
    g_hookEquipPistol.Remove();
    g_hookEquipBestWeapon.Remove();
    g_origEquipBestWeapon = nullptr;
    g_origEquipPistol = nullptr;
    g_origSelectItem = nullptr;
    g_installed = false;
    g_status = "not_attempted";
    {
        std::scoped_lock lk(g_wsToSlotMu);
        g_wsToBinding.clear();
        for (auto& slotToWs : g_slotToWs)
            slotToWs = nullptr;
    }
}

const char* Status() { return g_status.c_str(); }
void* EquipBestWeaponAddress() { return g_addrEquipBestWeapon; }
void* EquipPistolAddress() { return g_addrEquipPistol; }
void* SelectItemAddress() { return g_addrSelectItem; }
void* GetSlotAddress() { return g_addrGetSlot; }

// ---- MotionRecorder helpers ----

bool WeaponHooksReady() { return g_installed && g_getSlot && g_origSelectItem; }

int ReadDefIndex(void* weapon)
{
    if (!weapon) return -1;
    uint16_t def = 0;
    return SafeRead(weapon, tg::g_weaponItemDefIndex, def) ? def : -1;
}

// entity -> identity(0x10) -> m_EHandle(0x10), low 15 bits = index.
namespace {

int EntIndexOf(void* entity)
{
    if (!entity) return -1;
    void* identity = nullptr;
    if (!GuardedRead(entity, tg::g_entIdentity, identity)) return -1;
    if (!identity) return -1;
    uint32_t h = 0;
    if (!SafeRead(identity, tg::g_entIdentityEHandle, h)) return -1;
    if (h == 0U || h == 0xFFFFFFFFU) return -1;
    return static_cast<int>(h & 0x7FFFU);
}

} // namespace

// entity index of a weapon, for cmd.weaponselect on replay.
int WeaponEntIndex(void* weapon) { return EntIndexOf(weapon); }

int ActiveWeaponDef(void* ws)
{
    if (!ws || !g_getSlot) return -1;
    // m_hActiveWeapon is a handle; resolve it by matching its entity
    // index against the pointers GetSlot returns
    uint32_t activeH = 0;
    if (!SafeRead(ws, tg::g_wsActiveWeapon, activeH)) return -1;
    if (activeH == 0U || activeH == 0xFFFFFFFFU) return -1;
    int activeIdx = static_cast<int>(activeH & 0x7FFFU);
    for (int slot = 0; slot <= 4; ++slot)
    {
        // GEAR_SLOT_GRENADES (3) holds every grenade type at once
        unsigned int maxPos = (slot == 3) ? 8U : 1U;
        for (unsigned int pos = 0; pos < maxPos; ++pos)
        {
            unsigned int posArg = (slot == 3) ? pos : 0xFFFFFFFFU;
            void* w = g_getSlot(ws, slot, posArg);
            if (w && EntIndexOf(w) == activeIdx)
            {
                int def = ReadDefIndex(w);
                // Engine slot 2 holds knife AND taser. Normalize any
                // knife skin to kKnifeDef; keep the taser (31) as-is.
                if (slot == 2 && def != 31) return kKnifeDef;
                return def;
            }
        }
    }
    return -1;
}

void* FindWeaponByDef(void* ws, int def)
{
    if (!ws || def < 0 || !g_getSlot) return nullptr;
    // kKnifeDef means "the bot's own slot-2 knife", whatever skin it is.
    if (def == kKnifeDef) return g_getSlot(ws, 2, 0xFFFFFFFFU);
    // Non-grenade gear slots hold one weapon each
    for (int slot = 0; slot <= 4; ++slot)
    {
        if (slot == 3) continue;
        void* w = g_getSlot(ws, slot, 0xFFFFFFFFU);
        if (w && ReadDefIndex(w) == def) return w;
    }
    // GEAR_SLOT_GRENADES (3) holds every grenade type at once
    for (unsigned int pos = 0; pos < 8; ++pos)
    {
        void* w = g_getSlot(ws, 3, pos);
        if (w && ReadDefIndex(w) == def) return w;
    }
    return nullptr;
}

bool SelectWeaponRaw(void* ws, void* weapon)
{
    if (!ws || !weapon || !g_origSelectItem) return false;
    g_origSelectItem(ws, weapon, 0);
    return true;
}

void* WsForSlot(int slot)
{
    if (slot < 0 || slot >= 64) return nullptr;
    std::scoped_lock lk(g_wsToSlotMu);
    return g_slotToWs[slot];
}

int SwitchToLockTarget(int slot)
{
    if (!g_installed || !g_origSelectItem || !g_getSlot) return 3;
    if (slot < 0 || slot >= 64) return 3;
    if (motion_recorder::IsReplaying(slot)) return 4;

    LockTarget lt = weapon_locker_state::Get(slot);
    if (lt == LockTarget::None) return 3;
    int engineSlot = LockTargetToEngineSlot(lt);
    if (engineSlot < 0) return 3;

    void* ws = nullptr;
    {
        std::scoped_lock lk(g_wsToSlotMu);
        ws = g_slotToWs[slot];
    }
    if (!ws) return 1; // bot hasn't ticked yet; lock will still take effect once AI runs.

    void* target = g_getSlot(ws, engineSlot, 0xFFFFFFFFU);
    if (!target) return 2;

    // Route through the original (un-hooked) function so we don't
    // ping-pong through HookedSelectItem.
    g_origSelectItem(ws, target, 0);
    return 0;
}
} // namespace weapon_locker_hooks
} // namespace bot_controller
