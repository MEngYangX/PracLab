// Per-slot lock target table

#pragma once

namespace bot_controller {
// Engine weapon slots
// Slot1 = primary
// Slot2 = pistol
// Slot3 = knife/zeus
// Slot4 = grenades (he/flash/smoke/molotov/decoy)
// Slot5 = C4
enum class LockTarget : int // NOLINT(performance-enum-size)
{
    None = 0,
    Slot1 = 1,
    Slot2 = 2,
    Slot3 = 3,
    Slot4 = 4,
    Slot5 = 5,
};

namespace weapon_locker_state {
constexpr int kMaxSlots = 64;

LockTarget Get(int slot);
void Set(int slot, LockTarget tgt);
void Clear(int slot);
void ClearAll();

// Returns count of currently locked slots.
int CountLocked();
} // namespace weapon_locker_state
} // namespace bot_controller
