// Pawn/Controller -> slot 解析实现。
// 依赖：version_targets.h 的偏移量常量，memory.h 的 SafeRead。

#include "slot_resolver.h"
#include "version_targets.h"
#include "memory.h"

namespace PracLab::ReplayEngine::Slot
{
    namespace
    {
        // CHandle 低 15 位是实体索引。
        // 0 / 0xFFFFFFFF 视为无效句柄。
        int EntIndexFromHandle(uint32_t h)
        {
            if (h == 0u || h == 0xFFFFFFFFu)
                return -1;
            return static_cast<int>(h & 0x7FFFu);
        }

        // CS2 玩家实体索引从 1 开始，slot = idx - 1。
        // 范围 [1, 64] 对应 slot [0, 63]。
        int SlotFromEntityIndex(int idx)
        {
            if (idx < 1 || idx > 64)
                return -1;
            return idx - 1;
        }
    } // namespace

    PawnControllerHandles ReadPawnControllerHandles(void *pawn)
    {
        PawnControllerHandles out{};
        if (!pawn)
            return out;

        // 顺序读取：任一失败即返回当前已读取字段
        if (!Mem::SafeRead(pawn, tg::kPawn_Controller, out.controllerHandle) ||
            !Mem::SafeRead(pawn, tg::kPawn_OriginalController, out.originalControllerHandle))
            return out;

        out.controllerIndex = EntIndexFromHandle(out.controllerHandle);
        out.originalControllerIndex = EntIndexFromHandle(out.originalControllerHandle);
        out.controllerSlot = SlotFromEntityIndex(out.controllerIndex);

        // 优先使用当前 controller，缺失时回退到原始 controller（bot 复用场景）
        out.ownerSlot = out.controllerSlot >= 0
                            ? out.controllerSlot
                            : SlotFromEntityIndex(out.originalControllerIndex);
        return out;
    }

    int ControllerSlotForPawn(void *pawn)
    {
        if (!pawn)
            return -1;
        return ReadPawnControllerHandles(pawn).ownerSlot;
    }

    int ControllerToSlot(void *controller)
    {
        if (!controller)
            return -1;

        // controller -> CEntityIdentity*
        void *identity = nullptr;
        if (!Mem::SafeRead(controller, tg::kEnt_Identity, identity) || !identity)
            return -1;

        // identity -> m_EHandle
        uint32_t handle = 0;
        if (!Mem::SafeRead(identity, tg::kEntIdentity_EHandle, handle))
            return -1;

        int idx = EntIndexFromHandle(handle);
        return SlotFromEntityIndex(idx);
    }

    // ---- CCSBot* -> slot 解析（仿照 CS2-Bot-Controller）----

    // 从 CCSBot* 解析 slot：bot -> pawn -> controller handle -> slot。
    // CCSBot 在 +kBot_Pawn 偏移存储了 m_pPawn 指针。
    int CCSBotToSlot(void *bot)
    {
        if (!bot)
            return -1;

        // bot -> pawn
        void *pawn = nullptr;
        if (!Mem::SafeRead(bot, tg::kBot_Pawn, pawn) || !pawn)
            return -1;

        // pawn -> controller handle -> slot
        return ReadPawnControllerHandles(pawn).ownerSlot;
    }

    // 支持 July 2026 helper context 布局：先尝试直接解析，失败则从 +0x10 读取 bot*。
    int CCSBotContextToSlot(void *botOrContext)
    {
        // 先尝试直接从 bot* 解析
        int slot = CCSBotToSlot(botOrContext);
        if (slot >= 0)
            return slot;

        // 失败则尝试 helper context 布局：bot* 在偏移 0x10 处
        if (!botOrContext)
            return -1;

        void *bot = nullptr;
        if (!Mem::SafeRead(botOrContext, 0x10, bot) || !bot)
            return -1;

        return CCSBotToSlot(bot);
    }
}
