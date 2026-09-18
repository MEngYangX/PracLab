// Per-slot bot lock flags.

#pragma once

namespace bot_controller {
namespace bot_controller_state {
constexpr int kMaxSlots = 64;

// All lock: Update + Upkeep.
bool GetAll(int slot);
void SetAll(int slot, bool locked);
void ClearAllAll();
int CountAll();

// Aim lock: Upkeep only.
bool GetAim(int slot);
void SetAim(int slot, bool locked);
void ClearAllAim();
int CountAim();
} // namespace bot_controller_state
} // namespace bot_controller
