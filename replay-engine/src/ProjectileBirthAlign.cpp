#include "ProjectileBirthAlign.h"

#include "ccsbot_slot.h"
#include "version_targets.h"

#include <array>
#include <atomic>
#include <cstdint>
#include <cstring>
#include <mutex>
#include <vector>

namespace bot_controller::projectile_birth_align {
namespace {
constexpr int kMaxPending = 64;
constexpr int kMaxAttempts = 4;

struct Pending
{
    uint64_t entityPtr;
    std::array<float, 3> position;
    std::array<float, 3> velocity;
    int attemptsRemaining;
};

std::mutex g_mutex;
std::vector<Pending> g_pending;
std::atomic<int> g_pendingCount{ 0 };
int g_initialPositionOffset = -1;
int g_initialVelocityOffset = -1;
int g_queued = 0;
int g_applied = 0;
int g_expired = 0;
int g_failed = 0;

// Resolves a projectile's scene node through its body component
void* ResolveSceneNode(void* entity)
{
    void* body = nullptr;
    if (!GuardedRead(entity, targets::g_entBodyComponent, body) || !body) return nullptr;

    void* node = nullptr;
    return GuardedRead(body, targets::g_bodySceneNode, node) ? node : nullptr;
}

// Writes all projectile fields that establish the native trajectory
bool Apply(Pending& pending)
{
    if (g_initialPositionOffset < 0 || g_initialVelocityOffset < 0) return false;

    void* entity = reinterpret_cast<void*>(static_cast<uintptr_t>(pending.entityPtr)); // NOLINT(performance-no-int-to-ptr)
    if (!entity) return false;

    const size_t vectorSize = sizeof(float) * pending.position.size();
    if (!TryWriteMemoryGuarded(entity, g_initialPositionOffset, pending.position.data(), vectorSize) ||
        !TryWriteMemoryGuarded(entity, g_initialVelocityOffset, pending.velocity.data(), vectorSize) ||
        !TryWriteMemoryGuarded(entity, targets::g_entAbsVelocity, pending.velocity.data(), vectorSize))
    {
        return false;
    }

    void* node = ResolveSceneNode(entity);
    if (node) TryWriteMemoryGuarded(node, targets::g_nodeAbsOrigin, pending.position.data(), vectorSize);
    return true;
}
} // namespace

// Configures the projectile fields written before the next physics step
int ConfigureOffsets(int initialPositionOffset, int initialVelocityOffset)
{
    if (initialPositionOffset < 0 || initialVelocityOffset < 0) return -2;

    std::scoped_lock lock(g_mutex);
    g_initialPositionOffset = initialPositionOffset;
    g_initialVelocityOffset = initialVelocityOffset;
    return 0;
}

// Queues one projectile's recorded birth position and velocity
int Queue(uint64_t entityPtr, float posX, float posY, float posZ, float velX, float velY, float velZ)
{
    if (entityPtr == 0) return -2;

    std::scoped_lock lock(g_mutex);
    if (g_initialPositionOffset < 0 || g_initialVelocityOffset < 0) return -3;

    if (static_cast<int>(g_pending.size()) >= kMaxPending)
    {
        g_pending.erase(g_pending.begin());
        ++g_expired;
    }

    g_pending.push_back(
        { .entityPtr = entityPtr, .position = { posX, posY, posZ }, .velocity = { velX, velY, velZ }, .attemptsRemaining = kMaxAttempts });
    g_pendingCount.store(static_cast<int>(g_pending.size()), std::memory_order_release);
    ++g_queued;
    return 0;
}

// Clears pending projectile writes and returns the number removed
int Clear()
{
    std::scoped_lock lock(g_mutex);
    const int cleared = static_cast<int>(g_pending.size());
    g_pending.clear();
    g_pendingCount.store(0, std::memory_order_release);
    return cleared;
}

// Copies projectile alignment diagnostics into the caller's status buffer
int GetStatus(Status* out, int size)
{
    if (!out || size < 0 || static_cast<size_t>(size) < sizeof(Status)) return -1;

    std::scoped_lock lock(g_mutex);
    Status status{};
    status.size = static_cast<int32_t>(sizeof(Status));
    status.configured = g_initialPositionOffset >= 0 && g_initialVelocityOffset >= 0 ? 1 : 0;
    status.pending = static_cast<int32_t>(g_pending.size());
    status.queued = g_queued;
    status.applied = g_applied;
    status.expired = g_expired;
    status.failed = g_failed;
    status.initialPositionOffset = g_initialPositionOffset;
    status.initialVelocityOffset = g_initialVelocityOffset;
    std::memcpy(out, &status, sizeof(status));
    return 0;
}

// Applies pending projectile birth writes from a native simulation hook
void ProcessPending()
{
    if (g_pendingCount.load(std::memory_order_acquire) == 0) return;

    std::scoped_lock lock(g_mutex);
    for (auto it = g_pending.begin(); it != g_pending.end();)
    {
        if (Apply(*it))
        {
            ++g_applied;
            it = g_pending.erase(it);
            continue;
        }

        --it->attemptsRemaining;
        if (it->attemptsRemaining <= 0)
        {
            ++g_failed;
            it = g_pending.erase(it);
        }
        else
        {
            ++it;
        }
    }
    g_pendingCount.store(static_cast<int>(g_pending.size()), std::memory_order_release);
}
} // namespace bot_controller::projectile_birth_align
