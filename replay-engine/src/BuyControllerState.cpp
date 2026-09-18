// Per-slot bot buy plan table

#include "BuyControllerState.h"

#include <array>
#include <mutex>
#include <vector>
#include <string>

namespace bot_controller {
namespace buy_controller_state {

namespace {
struct Entry
{
    bool present = false;
    BuyPlan plan;
};

std::array<Entry, kMaxSlots> g_plans{};
std::mutex g_mu;

} // namespace

bool HasPlan(int slot)
{
    if (slot < 0 || slot >= kMaxSlots) return false;
    std::scoped_lock lk(g_mu);
    return g_plans[slot].present;
}

void Set(int slot, const std::vector<std::string>& items, bool skip)
{
    if (slot < 0 || slot >= kMaxSlots) return;
    std::scoped_lock lk(g_mu);
    g_plans[slot].present = true;
    g_plans[slot].plan.skip = skip;
    g_plans[slot].plan.items = items;
}

bool Copy(int slot, BuyPlan& out)
{
    if (slot < 0 || slot >= kMaxSlots) return false;
    std::scoped_lock lk(g_mu);
    if (!g_plans[slot].present) return false;
    out = g_plans[slot].plan;
    return true;
}

void Clear(int slot)
{
    if (slot < 0 || slot >= kMaxSlots) return;
    std::scoped_lock lk(g_mu);
    g_plans[slot] = Entry{};
}

void ClearAll()
{
    std::scoped_lock lk(g_mu);
    for (auto& e : g_plans)
        e = Entry{};
}

int ItemCount(int slot)
{
    if (slot < 0 || slot >= kMaxSlots) return -1;
    std::scoped_lock lk(g_mu);
    if (!g_plans[slot].present) return -1;
    return static_cast<int>(g_plans[slot].plan.items.size());
}

int CountPlans()
{
    std::scoped_lock lk(g_mu);
    int n = 0;
    for (auto& e : g_plans)
        if (e.present) ++n;
    return n;
}
} // namespace buy_controller_state
} // namespace bot_controller
