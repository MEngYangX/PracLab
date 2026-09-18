#pragma once

#include <cstdint>

namespace bot_controller::projectile_birth_align {
#pragma pack(push, 4)
struct Status
{
    int32_t size;
    int32_t configured;
    int32_t pending;
    int32_t queued;
    int32_t applied;
    int32_t expired;
    int32_t failed;
    int32_t initialPositionOffset;
    int32_t initialVelocityOffset;
};
#pragma pack(pop)

static_assert(sizeof(Status) == 36);

// Configures the projectile fields written before the next physics step
int ConfigureOffsets(int initialPositionOffset, int initialVelocityOffset);

// Queues one projectile's recorded birth position and velocity
int Queue(uint64_t entityPtr, float posX, float posY, float posZ, float velX, float velY, float velZ);

// Clears pending projectile writes and returns the number removed
int Clear();

// Copies projectile alignment diagnostics into the caller's status buffer
int GetStatus(Status* out, int size);

// Applies pending projectile birth writes from a native simulation hook
void ProcessPending();
} // namespace bot_controller::projectile_birth_align
