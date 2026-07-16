// Pawn 与 Controller 到玩家 slot 的解析工具。
// 通过读取引擎的 m_hController / m_hOriginalController / EHandle 字段，
// 将 CCSPlayerPawn* 或 CCSPlayerController* 转换为 [0, 63] 的 slot 索引。
//
// 所有内存读取都通过 Mem::SafeRead，避免无效指针触发访问违例。
// 偏移量来源：version_targets.h（运行时由 gamedata.json 覆盖）。

#pragma once

#include <cstdint>

namespace PracLab::ReplayEngine::Slot
{
    // Pawn 的两个 controller 句柄及解析结果。
    // 用于调试与回放 pawn 校验。
    struct PawnControllerHandles
    {
        uint32_t controllerHandle = 0;
        uint32_t originalControllerHandle = 0;
        int controllerIndex = -1;          // 由 controllerHandle 低 15 位得到
        int originalControllerIndex = -1; // 由 originalControllerHandle 低 15 位得到
        int controllerSlot = -1;           // controllerIndex - 1
        int ownerSlot = -1;                // 优先 controllerSlot，否则 originalControllerSlot
    };

    // 从 pawn 读取 controller 句柄并解析 slot。
    // 失败时 ownerSlot 为 -1。
    // pawn: CCSPlayerPawn* 指针。
    PawnControllerHandles ReadPawnControllerHandles(void *pawn);

    // 通过 m_hController / m_hOriginalController 解析 pawn 所属 slot。
    // 失败返回 -1。
    int ControllerSlotForPawn(void *pawn);

    // 通过 controller 自身的 EHandle 解析 slot。
    // 用于 PhysicsSimulate(controller) 直接入参场景。
    // 失败返回 -1。
    int ControllerToSlot(void *controller);

    // ---- CCSBot* -> slot 解析（仿照 CS2-Bot-Controller）----
    // CCSBot 内嵌了 pawn 指针 (+kBot_Pawn)，通过 pawn -> controller handle 解析 slot。
    // 用于 CCSBot::Update / Upkeep / UpdateLookAngles hook 的入参 bot*。
    // 失败返回 -1。
    int CCSBotToSlot(void *bot);

    // 支持 July 2026 helper context 布局的 slot 解析。
    // 先尝试直接从 bot* 解析，失败则从偏移 0x10 读取 bot* 再解析。
    // 用于 CCSBot::Upkeep 等可能传入 helper context 的 hook。
    // 失败返回 -1。
    int CCSBotContextToSlot(void *botOrContext);
}
