// CCSBot* -> player slot via pawn (+0x18) -> controller handle.

#include "ccsbot_slot.h"
#include "version_targets.h"

#include <cstdint>
#include <cstring>

#ifdef _WIN32
#ifndef WIN32_LEAN_AND_MEAN
#define WIN32_LEAN_AND_MEAN
#endif
#ifndef NOMINMAX
#define NOMINMAX
#endif
#include <excpt.h>
#else
#include <sys/types.h>
#include <sys/uio.h>
#include <unistd.h>
#endif

namespace tg = bot_controller::targets;

namespace bot_controller {

namespace {
int EntIndexFromHandle(uint32_t h)
{
    if (h == 0U || h == 0xFFFFFFFFU) return -1;
    return static_cast<int>(h & 0x7FFFU);
}

int SlotFromEntityIndex(int idx)
{
    if (idx < 1 || idx > 64) return -1;
    return idx - 1;
}

// Reads an engine field and converts Windows access violations into failure
} // namespace

bool TryReadMemory(const void* base, int offset, void* out, size_t size)
{
    if (!base || !out || offset < 0 || size == 0) return false;

    const auto baseAddress = reinterpret_cast<uintptr_t>(base);
    const auto address = baseAddress + static_cast<uintptr_t>(offset);
    if (address < 0x10000U || address < baseAddress || address + size < address) return false;

#ifdef _WIN32
    __try
    {
        const void* addressPointer = reinterpret_cast<const void*>(address); // NOLINT(performance-no-int-to-ptr)
        std::memcpy(out, addressPointer, size);
    }
    __except (EXCEPTION_EXECUTE_HANDLER)
    {
        return false;
    }
#else
    std::memcpy(out, reinterpret_cast<const void*>(address), size);
#endif
    return true;
}

// Writes an engine field and converts Windows access violations into failure
bool TryWriteMemory(void* base, int offset, const void* value, size_t size)
{
    if (!base || !value || offset < 0 || size == 0) return false;

    const auto baseAddress = reinterpret_cast<uintptr_t>(base);
    const auto address = baseAddress + static_cast<uintptr_t>(offset);
    if (address < 0x10000U || address < baseAddress || address + size < address) return false;

#ifdef _WIN32
    __try
    {
        void* addressPointer = reinterpret_cast<void*>(address); // NOLINT(performance-no-int-to-ptr)
        std::memcpy(addressPointer, value, size);
    }
    __except (EXCEPTION_EXECUTE_HANDLER)
    {
        return false;
    }
#else
    std::memcpy(reinterpret_cast<void*>(address), value, size);
#endif
    return true;
}

// Reads untrusted memory without allowing an invalid page to terminate the server
bool TryReadMemoryGuarded(const void* base, int offset, void* out, size_t size)
{
#ifdef _WIN32
    return TryReadMemory(base, offset, out, size);
#else
    if (!base || !out || offset < 0 || size == 0) return false;

    const auto baseAddress = reinterpret_cast<uintptr_t>(base);
    const auto address = baseAddress + static_cast<uintptr_t>(offset);
    if (address < 0x10000U || address < baseAddress || address + size < address) return false;

    struct iovec local{ out, size };
    struct iovec remote{ reinterpret_cast<void*>(address), size };
    return process_vm_readv(getpid(), &local, 1, &remote, 1, 0) == static_cast<ssize_t>(size);
#endif
}

// Writes untrusted memory without allowing an invalid page to terminate the server
bool TryWriteMemoryGuarded(void* base, int offset, const void* value, size_t size)
{
#ifdef _WIN32
    return TryWriteMemory(base, offset, value, size);
#else
    if (!base || !value || offset < 0 || size == 0) return false;

    const auto baseAddress = reinterpret_cast<uintptr_t>(base);
    const auto address = baseAddress + static_cast<uintptr_t>(offset);
    if (address < 0x10000U || address < baseAddress || address + size < address) return false;

    struct iovec local{ const_cast<void*>(value), size };
    struct iovec remote{ reinterpret_cast<void*>(address), size };
    return process_vm_writev(getpid(), &local, 1, &remote, 1, 0) == static_cast<ssize_t>(size);
#endif
}

PawnControllerHandles ReadPawnControllerHandles(void* pawn)
{
    PawnControllerHandles out{};
    out.controllerIndex = -1;
    out.originalControllerIndex = -1;
    out.controllerSlot = -1;
    out.ownerSlot = -1;

    if (!pawn) return out;

    if (!GuardedRead(pawn, tg::g_pawnController, out.controllerHandle) ||
        !GuardedRead(pawn, tg::g_pawnOriginalController, out.originalControllerHandle))
        return out;
    out.controllerIndex = EntIndexFromHandle(out.controllerHandle);
    out.originalControllerIndex = EntIndexFromHandle(out.originalControllerHandle);
    out.controllerSlot = SlotFromEntityIndex(out.controllerIndex);
    out.ownerSlot = out.controllerSlot >= 0 ? out.controllerSlot : SlotFromEntityIndex(out.originalControllerIndex);
    return out;
}

SlotResolution ResolveSlot(void* bot)
{
    SlotResolution out{ .pawn = nullptr, .pawnEntIndex = -1, .slot = -1 };
    if (!bot) return out;

    void* pawn = nullptr;
    if (!GuardedRead(bot, tg::g_botPawn, pawn)) return out;
    if (!pawn) return out;
    out.pawn = pawn;

    void* identity = nullptr;
    if (!GuardedRead(pawn, tg::g_entIdentity, identity)) return out;
    if (!identity) return out;

    uint32_t handle = 0;
    if (!GuardedRead(identity, tg::g_entIdentityEHandle, handle)) return out;
    out.pawnEntIndex = EntIndexFromHandle(handle);
    if (out.pawnEntIndex <= 0) return out;

    out.slot = ReadPawnControllerHandles(pawn).ownerSlot;
    return out;
}

int CCSBotToSlot(void* bot) { return ResolveSlot(bot).slot; }

// Resolves direct bot pointers first, then the July 2026 helper context layout.
SlotResolution ResolveSlotFromBotOrContext(void* botOrContext)
{
    SlotResolution direct = ResolveSlot(botOrContext);
    if (direct.slot >= 0) return direct;

    void* bot = nullptr;
    if (!GuardedRead(botOrContext, 0x10, bot)) return direct;

    SlotResolution viaContext = ResolveSlot(bot);
    return viaContext.slot >= 0 ? viaContext : direct;
}

// Returns the slot resolved from either supported bot argument shape.
int CCSBotContextToSlot(void* botOrContext) { return ResolveSlotFromBotOrContext(botOrContext).slot; }

// Resolves the current controller and falls back to the stable original owner.
int ControllerSlotForPawn(void* pawn)
{
    if (!pawn) return -1;
    return ReadPawnControllerHandles(pawn).ownerSlot;
}

// CCSPlayerController*'s own identity ehandle -> entindex -> slot.
int ControllerToSlot(void* controller)
{
    if (!controller) return -1;
    void* identity = nullptr;
    if (!GuardedRead(controller, tg::g_entIdentity, identity)) return -1;
    if (!identity) return -1;
    uint32_t h = 0;
    if (!GuardedRead(identity, tg::g_entIdentityEHandle, h)) return -1;
    int idx = EntIndexFromHandle(h);
    if (idx < 1 || idx > 64) return -1;
    return idx - 1;
}
} // namespace bot_controller
