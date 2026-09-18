// Read a bot's BotProfile by slot. CCSBot* -> +kBot_Profile -> members.

#include "BotProfile.h"
#include "BotController.h"
#include "ccsbot_slot.h"
#include "version_targets.h"

#include <algorithm>
#include <cstring>

namespace tg = bot_controller::targets;

namespace bot_controller {
namespace bot_profile {
// Read profile members off a live bot for this slot
bool ReadProfile(int slot, BotProfileData& out)
{
    void* bot = bot_controller_hooks::BotForSlot(slot);
    if (!bot) return false;
    // Guard against a stale pointer: it must still resolve to this slot
    if (CCSBotToSlot(bot) != slot) return false;

    void* prof = nullptr;
    if (!GuardedRead(bot, tg::g_botProfile, prof)) return false;
    if (!prof) return false;

    std::memset(&out, 0, sizeof(out));
    if (!SafeRead(prof, tg::g_profAggression, out.aggression) || !SafeRead(prof, tg::g_profSkill, out.skill) ||
        !SafeRead(prof, tg::g_profTeamwork, out.teamwork) || !SafeRead(prof, tg::g_profReactionTime, out.reactionTime) ||
        !SafeRead(prof, tg::g_profAttackDelay, out.attackDelay) || !SafeRead(prof, tg::g_profLookAccelAtk, out.lookAccelAtk) ||
        !SafeRead(prof, tg::g_profLookStiffAtk, out.lookStiffAtk) || !SafeRead(prof, tg::g_profLookDampAtk, out.lookDampAtk) ||
        !SafeRead(prof, tg::g_profCost, out.cost) || !SafeRead(prof, tg::g_profDifficulty, out.difficulty))
        return false;

    int count = 0;
    if (!SafeRead(prof, tg::g_profWeaponPrefCount, count)) return false;
    count = std::max(count, 0);
    count = std::min(count, 16);
    out.weaponPrefCount = count;
    for (int i = 0; i < count; ++i)
        if (!SafeRead(prof, tg::g_profWeaponPref + (i * 2), out.weaponPref[i])) return false;
    return true;
}
} // namespace bot_profile
} // namespace bot_controller
