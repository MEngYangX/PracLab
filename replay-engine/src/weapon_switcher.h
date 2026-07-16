// 回放武器切换模块。
// 负责：
//   1. 通过签名扫描解析 CCSPlayer_WeaponServices::GetSlot 和 ::SelectItem
//   2. 维护 slot -> WeaponServices* 缓存（由 hooks.cpp 在 PlayerRunCommand 中更新）
//   3. 提供武器查询/切换辅助：ActiveWeaponDef / FindWeaponByDef / SelectWeaponRaw /
//      WeaponEntIndex / BotActiveWeaponDef / CurrentReplayWeaponSelect
//
// 设计参考：CS2-Bot-Controller 的 WeaponLocker 模块（简化版，去除 EquipBestWeapon
// /EquipPistol hook，仅保留回放所需的 GetSlot/SelectItem 解析与武器切换辅助）。
//
// 调用链：
//   hooks.cpp HookedPlayerRunCommand(replaying)
//     -> WeaponSwitcher::SetSlotWs(slot, ws)            // 缓存 ws
//     -> Replayer::CurrentReplayWeaponSelect(slot)      // 比较并切换
//          -> Replayer::CurrentReplayWeaponDef(slot)    // 录制的 def
//          -> WeaponSwitcher::ActiveWeaponDef(ws)       // 当前 active def
//          -> WeaponSwitcher::FindWeaponByDef(ws, def)  // 找到武器实体
//          -> WeaponSwitcher::SelectWeaponRaw(ws, w)    // 调用原 SelectItem
//          -> WeaponSwitcher::WeaponEntIndex(w)         // 返回 entity index
//     -> base->set_weaponselect(wsel)                   // 写入 user command

#pragma once

#include <cstdint>
#include <string>

#include <nlohmann/json.hpp>

#include "sig_scan.h"

namespace PracLab::ReplayEngine::WeaponSwitcher
{
    // 任意刀类型的归一化 def index（与 CS2-Bot-Controller 一致）
    constexpr int kKnifeDef = 9001;

    // ---- 生命周期 ----

    // 解析 GetSlot/SelectItem 签名并初始化函数指针。
    // gd: 已加载的 gamedata.json；serverModule: server 模块信息。
    // 失败时通过 errorOut 写入错误描述并返回 false。
    // 注意：与 ProcessMovement 等"必装"hook 不同，本模块失败不影响回放基础功能，
    // 仅武器切换不可用（调用方应降级为只抑制不安全 attack）。
    bool Install(const nlohmann::json &gd, const Sig::ModuleInfo &serverModule,
                 char *errorOut, size_t errorOutLen);

    // 卸载：清空函数指针与 slot->ws 缓存。
    void Remove();

    // 状态字符串（用于诊断日志）。
    const char *Status();

    // ---- 查询 ----

    // GetSlot + SelectItem 是否均已解析完成。
    bool WeaponHooksReady();

    // 已解析的函数地址（诊断用）
    void *GetSlotAddress();
    void *SelectItemAddress();

    // ---- slot -> WeaponServices* 缓存 ----

    // 由 hooks.cpp 在 PlayerRunCommand 中调用：缓存 slot 对应的 WeaponServices*。
    // 回放武器切换通过 WsForSlot(slot) 读取此缓存。
    void SetSlotWs(int slot, void *ws);

    // 清空指定 slot 的 ws 缓存（bot 被踢出时调用）。
    void ClearSlotWs(int slot);

    // 读取 slot 缓存的 WeaponServices*。未缓存返回 nullptr。
    void *WsForSlot(int slot);

    // ---- 武器查询/切换辅助 ----

    // 读取武器实体的 item definition index。失败返回 -1。
    int ReadDefIndex(void *weapon);

    // 读取武器实体的 entity index（用于 cmd.weaponselect）。失败返回 -1。
    int WeaponEntIndex(void *weapon);

    // 获取 WeaponServices 当前 active weapon 的 def index。
    // 通过 GetSlot 遍历 0..4 槽位，匹配 m_hActiveWeapon 的 entity index。
    // slot 2 的非 taser 武器归一化为 kKnifeDef。
    // 失败或无 active 返回 -1。
    int ActiveWeaponDef(void *ws);

    // 在 WeaponServices 的 0..4 槽位中查找指定 def index 的武器。
    // kKnifeDef 表示"该 bot 自己的 slot-2 刀"（任意皮肤）。
    // 未找到返回 nullptr。
    void *FindWeaponByDef(void *ws, int def);

    // 调用原（未 hook 的）SelectItem 切换武器。
    // ws: WeaponServices*；weapon: 目标武器实体。
    // 成功返回 true。
    bool SelectWeaponRaw(void *ws, void *weapon);

    // ---- 回放专用 ----

    // 获取 slot 缓存 ws 对应的 active weapon def index。
    // 等价于 ActiveWeaponDef(WsForSlot(slot))，封装便于 C# 通过 P/Invoke 调用。
    // slot 非法或未缓存返回 -1。
    int BotActiveWeaponDef(int slot);

    // 计算本 tick 应写入 cmd.weaponselect 的 entity index。
    // 逻辑：
    //   1. 读取当前 tick 录制的 weaponDefIndex
    //   2. 若与 bot 当前 active def 相同 -> 返回 -1（无需切换）
    //   3. 在 ws 中查找匹配武器，调用 SelectWeaponRaw 切换
    //   4. 返回新 active 武器的 entity index
    // 任何一步失败返回 -1（调用方应抑制不安全 attack）。
    int CurrentReplayWeaponSelect(int slot);
}
