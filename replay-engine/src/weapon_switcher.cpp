// 回放武器切换模块实现。
// 仿照 CS2-Bot-Controller 的 WeaponLocker.cpp 简化版：
//   - 只解析 GetSlot/SelectItem 签名，不 hook EquipBestWeapon/EquipPistol
//   - 不实现 SelectItem hook（不需要"锁武器"功能，回放期间 AI 已被 kBot_AiTickedFlag 跳过）
//   - 仅提供武器查询/切换辅助供 Replayer::CurrentReplayWeaponSelect 调用
//
// 线程安全：
//   g_slotToWs 使用 std::atomic<void*> 存储，SetSlotWs/WsForSlot 通过 atomic 读写，
//   无需 mutex。回放期间单线程访问为主场景，atomic 足以防御诊断线程的并发读取。

#include "weapon_switcher.h"
#include "hook.h"  // PRL_FASTCALL
#include "memory.h"
#include "platform.h"
#include "replayer.h"
#include "sig_scan.h"
#include "version_targets.h"

#include <array>
#include <atomic>
#include <cstdio>
#include <cstring>

namespace tg = PracLab::ReplayEngine::targets;

namespace PracLab::ReplayEngine::WeaponSwitcher
{
    // ---- 函数指针类型 ----
    // CS2 引擎 Windows x64 使用 fastcall；Linux System V x64 默认约定等价。
    // PRL_FASTCALL 在 hooks.h 中定义，Linux 上展开为空。
    using GetSlot_t    = void *(PRL_FASTCALL *)(void *ws, int slot, unsigned int posMask);
    using SelectItem_t = char  (PRL_FASTCALL *)(void *ws, void *weapon, int flag);

    namespace
    {
        // ---- trampoline 指针 ----
        GetSlot_t    g_pGetSlot       = nullptr;
        SelectItem_t g_origSelectItem = nullptr;

        // ---- 已解析的函数地址 ----
        void *g_addrGetSlot    = nullptr;
        void *g_addrSelectItem = nullptr;

        bool g_installed = false;
        std::string g_status = "not_attempted";

        // slot -> CCSPlayer_WeaponServices* 缓存
        // 由 hooks.cpp HookedPlayerRunCommand 在每次 tick 时更新
        std::array<std::atomic<void *>, 64> g_slotToWs{};

        // entity -> identity -> m_EHandle，低 15 位 = entity index
        int EntIndexOf(void *entity)
        {
            if (!entity)
                return -1;
            void *identity = nullptr;
            if (!Mem::SafeRead(entity, tg::kEnt_Identity, identity) || !identity)
                return -1;
            uint32_t h = 0;
            if (!Mem::SafeRead(identity, tg::kEntIdentity_EHandle, h))
                return -1;
            if (h == 0u || h == 0xFFFFFFFFu)
                return -1;
            return static_cast<int>(h & 0x7FFFu);
        }
    } // namespace

    // ============================================================
    // 生命周期
    // ============================================================

    bool Install(const nlohmann::json &gd, const Sig::ModuleInfo &serverModule,
                 char *errorOut, size_t errorOutLen)
    {
        // 1. 解析 SelectItem 签名
        g_addrSelectItem = Sig::ResolveSig(
            gd, serverModule, "CCSPlayer_WeaponServices::SelectItem",
            errorOut, errorOutLen);
        if (!g_addrSelectItem)
        {
            g_status = "failed: SelectItem sig";
            return false;
        }
        g_origSelectItem = reinterpret_cast<SelectItem_t>(g_addrSelectItem);

        // 2. 解析 GetSlot 签名
        g_addrGetSlot = Sig::ResolveSig(
            gd, serverModule, "CCSPlayer_WeaponServices::GetSlot",
            errorOut, errorOutLen);
        if (!g_addrGetSlot)
        {
            g_status = "failed: GetSlot sig";
            // 降级：保留 SelectItem 但无 GetSlot 无法工作，直接失败
            g_origSelectItem = nullptr;
            g_addrSelectItem = nullptr;
            return false;
        }
        g_pGetSlot = reinterpret_cast<GetSlot_t>(g_addrGetSlot);

        // 注意：不 hook SelectItem，仅保留原函数指针。
        // CS2-Bot-Controller 通过 hook SelectItem 阻止 AI 切换武器（"锁武器"），
        // PracLab 不需要此功能：回放期间 CCSBot::Update 已被 kBot_AiTickedFlag=1 跳过，
        // AI 不会主动调用 SelectItem。

        g_installed = true;
        g_status = "ok";

        char dbg[256];
        std::snprintf(dbg, sizeof(dbg),
                      "[PracLabReplayEngine] WeaponSwitcher installed: "
                      "GetSlot=%p SelectItem=%p\n",
                      g_addrGetSlot, g_addrSelectItem);
        Platform::DebugOut(dbg);
        return true;
    }

    void Remove()
    {
        g_pGetSlot = nullptr;
        g_origSelectItem = nullptr;
        g_addrGetSlot = nullptr;
        g_addrSelectItem = nullptr;
        g_installed = false;
        g_status = "not_attempted";
        for (auto &s : g_slotToWs)
            s.store(nullptr, std::memory_order_release);
    }

    const char *Status() { return g_status.c_str(); }

    bool WeaponHooksReady()
    {
        return g_installed && g_pGetSlot != nullptr && g_origSelectItem != nullptr;
    }

    void *GetSlotAddress()    { return g_addrGetSlot; }
    void *SelectItemAddress() { return g_addrSelectItem; }

    // ============================================================
    // slot -> WeaponServices* 缓存
    // ============================================================

    void SetSlotWs(int slot, void *ws)
    {
        if (slot < 0 || slot >= 64)
            return;
        // 使用 atomic store，无需 mutex（读取端用 atomic load）
        g_slotToWs[slot].store(ws, std::memory_order_release);
    }

    void ClearSlotWs(int slot)
    {
        if (slot < 0 || slot >= 64)
            return;
        g_slotToWs[slot].store(nullptr, std::memory_order_release);
    }

    void *WsForSlot(int slot)
    {
        if (slot < 0 || slot >= 64)
            return nullptr;
        return g_slotToWs[slot].load(std::memory_order_acquire);
    }

    // ============================================================
    // 武器查询/切换辅助
    // ============================================================

    int ReadDefIndex(void *weapon)
    {
        if (!weapon)
            return -1;
        // m_iItemDefinitionIndex 是 uint16_t
        uint16_t def = 0;
        return Mem::SafeRead(weapon, tg::kWeapon_ItemDefIndex, def) ? static_cast<int>(def) : -1;
    }

    int WeaponEntIndex(void *weapon)
    {
        return EntIndexOf(weapon);
    }

    int ActiveWeaponDef(void *ws)
    {
        if (!ws || !g_pGetSlot)
            return -1;

        // m_hActiveWeapon 是 CHandle，低 15 位 = entity index
        uint32_t activeH = 0;
        if (!Mem::SafeRead(ws, tg::kWs_ActiveWeapon, activeH))
            return -1;
        if (activeH == 0u || activeH == 0xFFFFFFFFu)
            return -1;
        int activeIdx = static_cast<int>(activeH & 0x7FFFu);

        // 遍历 0..4 引擎槽位，通过 GetSlot 拿到武器实体并匹配 entity index
        // 槽位 3（GEAR_SLOT_GRENADES）可同时容纳多种投掷道具，需枚举位置 0..7
        for (int slot = 0; slot <= 4; ++slot)
        {
            unsigned int maxPos = (slot == 3) ? 8u : 1u;
            for (unsigned int pos = 0; pos < maxPos; ++pos)
            {
                unsigned int posArg = (slot == 3) ? pos : 0xFFFFFFFFu;
                void *w = g_pGetSlot(ws, slot, posArg);
                if (w && EntIndexOf(w) == activeIdx)
                {
                    int def = ReadDefIndex(w);
                    // 引擎 slot 2 同时容纳刀和 taser；将非 taser 的刀归一化为 kKnifeDef
                    // 以便与录制端归一化保持一致
                    if (slot == 2 && def != 31)
                        return kKnifeDef;
                    return def;
                }
            }
        }
        return -1;
    }

    void *FindWeaponByDef(void *ws, int def)
    {
        if (!ws || def < 0 || !g_pGetSlot)
            return nullptr;

        // kKnifeDef 表示"该 bot 自己的 slot-2 刀"（任意皮肤）
        if (def == kKnifeDef)
            return g_pGetSlot(ws, 2, 0xFFFFFFFFu);

        // 非投掷道具槽位（0,1,2,4）每个槽位一把武器
        for (int slot = 0; slot <= 4; ++slot)
        {
            if (slot == 3)
                continue;
            void *w = g_pGetSlot(ws, slot, 0xFFFFFFFFu);
            if (w && ReadDefIndex(w) == def)
                return w;
        }

        // 投掷道具槽位 3：枚举位置 0..7
        for (unsigned int pos = 0; pos < 8u; ++pos)
        {
            void *w = g_pGetSlot(ws, 3, pos);
            if (w && ReadDefIndex(w) == def)
                return w;
        }
        return nullptr;
    }

    bool SelectWeaponRaw(void *ws, void *weapon)
    {
        if (!ws || !weapon || !g_origSelectItem)
            return false;
        // 调用原 SelectItem(ws, weapon, 0)
        // 返回值非 0 表示切换成功
        return g_origSelectItem(ws, weapon, 0) != 0;
    }

    // ============================================================
    // 回放专用
    // ============================================================

    int BotActiveWeaponDef(int slot)
    {
        if (!WeaponHooksReady())
            return -1;
        void *ws = WsForSlot(slot);
        if (!ws)
            return -1;
        return ActiveWeaponDef(ws);
    }

    int CurrentReplayWeaponSelect(int slot)
    {
        if (!WeaponHooksReady())
            return -1;

        // 1. 读取当前 tick 录制的 weaponDefIndex
        int recordedDef = Replayer::CurrentReplayWeaponDef(slot);
        if (recordedDef < 0)
            return -1;

        // 2. 读取 bot 当前的 WeaponServices*
        void *ws = WsForSlot(slot);
        if (!ws)
            return -1;

        // 3. 若 active 已是录制的 def -> 无需切换
        if (ActiveWeaponDef(ws) == recordedDef)
            return -1;

        // 4. 在 ws 中查找匹配武器
        void *weapon = FindWeaponByDef(ws, recordedDef);
        if (!weapon)
            return -1;

        // 5. 调用原 SelectItem 切换
        if (!SelectWeaponRaw(ws, weapon))
            return -1;

        // 6. 返回新 active 武器的 entity index，供 cmd.weaponselect 使用
        return WeaponEntIndex(weapon);
    }
} // namespace PracLab::ReplayEngine::WeaponSwitcher
