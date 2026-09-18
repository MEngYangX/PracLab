// Per-slot bot lock flags.

#include "BotControllerState.h"

#include <array>
#include <atomic>

namespace bot_controller {
namespace bot_controller_state {

namespace {
std::array<std::atomic<bool>, kMaxSlots> g_allLocks{};
std::array<std::atomic<bool>, kMaxSlots> g_aimLocks{};

} // namespace

bool GetAll(int slot)
{
    if (slot < 0 || slot >= kMaxSlots) return false;
    return g_allLocks[slot].load(std::memory_order_relaxed);
}

void SetAll(int slot, bool locked)
{
    if (slot < 0 || slot >= kMaxSlots) return;
    g_allLocks[slot].store(locked, std::memory_order_relaxed);
}

void ClearAllAll()
{
    for (auto& x : g_allLocks)
        x.store(false, std::memory_order_relaxed);
}

int CountAll()
{
    int n = 0;
    for (auto& x : g_allLocks)
        if (x.load(std::memory_order_relaxed)) ++n;
    return n;
}

bool GetAim(int slot)
{
    if (slot < 0 || slot >= kMaxSlots) return false;
    return g_aimLocks[slot].load(std::memory_order_relaxed);
}

void SetAim(int slot, bool locked)
{
    if (slot < 0 || slot >= kMaxSlots) return;
    g_aimLocks[slot].store(locked, std::memory_order_relaxed);
}

void ClearAllAim()
{
    for (auto& x : g_aimLocks)
        x.store(false, std::memory_order_relaxed);
}

int CountAim()
{
    int n = 0;
    for (auto& x : g_aimLocks)
        if (x.load(std::memory_order_relaxed)) ++n;
    return n;
}
} // namespace bot_controller_state
} // namespace bot_controller
