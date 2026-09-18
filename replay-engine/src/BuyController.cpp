// Detour for BuyState::OnUpdate

#include "BuyController.h"
#include "BuyControllerState.h"
#include "MotionRecorder.h"
#include "ccsbot_slot.h"
#include "nlohmann/json.hpp"
#include "sig_scan.h"
#include "version_targets.h"
#include "dispatch.h"
#include "hook.h"

#include <eiface.h>
#include <playerslot.h>
#include <convar.h>

#include <cstdint>
#include <cstdio>
#include <string>

namespace tg = bot_controller::targets;

using BuyUpdateT = void(BC_FASTCALL*)(void* self, void* me);

namespace bot_controller {
namespace buy_controller_hooks {

namespace {
BuyUpdateT g_origOnUpdate = nullptr;
void* g_addrOnUpdate = nullptr;
Hook g_hookOnUpdate;
bool g_installed = false;
std::string g_status = "not_attempted"; // NOLINT(bugprone-throwing-static-initialization)

// Per-slot last seen m_isInitialDelay, for rising-edge detection
uint8_t g_lastInitDelay[64] = { 0 };

// Run "buy <alias>" server-side for a bot slot
void IssueBuy(int slot, const char* alias)
{
    if (!dispatch::g_gameClients || slot < 0 || slot >= 64) return;
    char line[128];
    std::snprintf(line, sizeof(line), "buy %s", alias);
    CCommand cmd;
    if (!cmd.Tokenize(line)) return;
    dispatch::g_gameClients->ClientCommand(CPlayerSlot(slot), cmd);
}

// Execute a slot's whole plan in one tick, then mark vanilla done
void ApplyPlan(void* self, int slot)
{
    BuyPlan plan;
    if (!buy_controller_state::Copy(slot, plan)) return;

    if (!plan.skip)
        for (const auto& alias : plan.items)
            IssueBuy(slot, alias.c_str());

    // Tell vanilla buying is finished so it stops here and exits state
    const uint8_t done = 1;
    WriteField(self, tg::g_buyDoneBuying, done);
}

void BC_FASTCALL HookedOnUpdate(void* self, void* me)
{
    int slot = CCSBotToSlot(me);
    if (slot >= 0 && slot < 64 && motion_recorder::IsReplaying(slot)) return;
    if (slot < 0 || slot >= 64 || !buy_controller_state::HasPlan(slot))
    {
        g_origOnUpdate(self, me);
        return;
    }

    uint8_t init = 0;
    if (!SafeRead(self, tg::g_buyInitialDelay, init))
    {
        g_origOnUpdate(self, me);
        return;
    }
    // Rising edge of m_isInitialDelay = freshly entered BuyState this round
    if (init && !g_lastInitDelay[slot]) ApplyPlan(self, slot);
    g_lastInitDelay[slot] = init;

    g_origOnUpdate(self, me);
}

} // namespace

bool Install(const nlohmann::json& gd, const sig::ModuleInfo& serverModule, char* errorOut, size_t errorOutLen)
{
    g_addrOnUpdate = sig::ResolveSig(gd, serverModule, "BuyState::OnUpdate", errorOut, errorOutLen);
    if (!g_addrOnUpdate)
    {
        g_status = "failed: OnUpdate sig";
        return false;
    }

    if (!g_hookOnUpdate.Create(g_addrOnUpdate, reinterpret_cast<void*>(&HookedOnUpdate), reinterpret_cast<void**>(&g_origOnUpdate)) ||
        !g_hookOnUpdate.Enable())
    {
        std::snprintf(errorOut, errorOutLen, "hook BuyState::OnUpdate failed");
        g_hookOnUpdate.Remove();
        g_origOnUpdate = nullptr;
        g_status = "failed: hook OnUpdate";
        return false;
    }

    g_installed = true;
    g_status = "ok";
    return true;
}

void Remove()
{
    if (!g_installed) return;
    g_hookOnUpdate.Remove();
    g_origOnUpdate = nullptr;
    g_installed = false;
    g_status = "not_attempted";
    for (unsigned char& i : g_lastInitDelay)
        i = 0;
}

const char* Status() { return g_status.c_str(); }
void* OnUpdateAddress() { return g_addrOnUpdate; }
} // namespace buy_controller_hooks
} // namespace bot_controller
