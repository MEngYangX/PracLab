// 引擎结构偏移量常量与 vtable 索引。
// 所有偏移量默认值取自公开的 CS2 引擎结构数据（非原创代码）。
// 在插件 Load 阶段通过 LoadFromGamedata 从 gamedata.json 覆盖，
// 支持 CS2 版本更新后通过更新 gamedata.json 而无需重新编译插件。
//
// 命名约定：
//   kXxx_Yyy = 偏移量或 vtable 索引
//   kFL_*    = flags 位掩码常量（constexpr，非偏移量）

#pragma once

#include <nlohmann/json.hpp>

namespace PracLab::ReplayEngine::targets
{
    // ---- CCSBot ----

    // AI 在本 tick 已执行的标志字节（置 1 即可欺骗引擎跳过 AI 逻辑）
    inline int kBot_AiTickedFlag = 0x610;
    // CCSBot -> m_pPawn (CCSPlayerPawn*)
    inline int kBot_Pawn = 0x18;
    // CCSBot -> m_pProfile (BotProfile*)
    inline int kBot_Profile = 0x08;

    // ---- CBaseEntity / CEntityIdentity ----

    // 实体 -> CEntityIdentity*
    inline int kEnt_Identity = 0x10;
    // CEntityIdentity -> m_EHandle（低 15 位 = 实体索引）
    inline int kEntIdentity_EHandle = 0x10;
    // m_MoveType (1 字节) — 回放每 tick 恢复
    inline int kEnt_MoveType = 0x2F3;
    // m_nActualMoveType (1 字节) — 网络同步的移动类型
    inline int kEnt_ActualMoveType = 0x2F5;
    // m_fFlags (bit0 = FL_ONGROUND, bit1 = FL_DUCKING)
    inline int kEnt_Flags = 0x388;
    // FL_ONGROUND 位掩码
    inline constexpr unsigned kFL_OnGround = 1u << 0;
    // FL_DUCKING 位掩码
    inline constexpr unsigned kFL_Ducking = 1u << 1;
    // m_vecAbsVelocity
    inline int kEnt_AbsVelocity = 0x38C;
    // 实体 -> m_CBodyComponent
    inline int kEnt_BodyComponent = 0x30;
    // m_CBodyComponent -> m_pSceneNode
    inline int kBody_SceneNode = 0x08;
    // m_pSceneNode -> m_vecAbsOrigin
    inline int kNode_AbsOrigin = 0xC8;

    // ---- CCSPlayerPawn ----

    // m_pWeaponServices
    inline int kPawn_WeaponServices = 0xA30;
    // m_pMovementServices
    inline int kPawn_MovementServices = 0xA70;
    // m_hController (CHandle)
    inline int kPawn_Controller = 0xBB0;
    // m_hOriginalController (CHandle)
    inline int kPawn_OriginalController = 0xD24;
    // v_angle (QAngle)
    inline int kPawn_ViewAngle = 0xAE8;
    // m_angEyeAngles (QAngle) — 回放每 tick 与 v_angle 同步写入
    inline int kPawn_EyeAngles = 0x1368;

    // ---- CCSPlayer_WeaponServices ----

    // m_hActiveWeapon (CHandle)
    inline int kWs_ActiveWeapon = 0x60;

    // ---- CBasePlayerWeapon ----

    // m_AttributeManager -> m_Item -> m_iItemDefinitionIndex
    inline int kWeapon_ItemDefIndex = 0x978 + 0x50 + 0x38;

    // ---- CCSPlayer_MovementServices ----

    // m_pawn (CCSPlayerPawn*)
    inline int kServices_Pawn = 56;
    // m_nButtons.m_pButtonStates[0] — 引擎按钮状态块（CInButtonState）
    inline int kServices_Buttons = 88;        // states[0] (pressed)
    inline int kServices_Buttons1 = 88 + 8;    // states[1]
    inline int kServices_Buttons2 = 88 + 16;   // states[2]

    // 蹲伏 / 梯子状态
    inline int kServices_LadderNormal = 0x3F8; // Vector m_vecLadderNormal
    inline int kServices_Ducked = 0x408;        // bool m_bDucked
    inline int kServices_DuckAmount = 0x40C;    // float m_flDuckAmount
    inline int kServices_DuckSpeed = 0x410;     // float m_flDuckSpeed
    inline int kServices_DesiresDuck = 0x415;   // bool m_bDesiresDuck
    inline int kServices_Ducking = 0x416;        // bool m_bDucking

    // ---- CMoveData ----

    // m_vecVelocity — TryPlayerMove 将速度积分到 origin
    inline int kMove_Velocity = 56;
    // m_vecAbsOrigin — FinishMove 提交前的最终位置
    inline int kMove_AbsOrigin = 200;

    // ---- vtable 索引 (CCSPlayer_MovementServices) ----

    // PlayerRunCommand 在 vtable 中的索引（懒加载 hook 用）
    inline int kVtIdx_PlayerRunCommand = 25;
    // FinishMove 在 vtable 中的索引（懒加载 hook 用）
    inline int kVtIdx_FinishMove = 38;

    // 从 gamedata.json 覆盖所有偏移量。
    // gd: 已加载的 gamedata.json 对象。
    // 缺失的条目保留代码中的默认值。
    void LoadFromGamedata(const nlohmann::json &gd);
}

// 别名：使用 tg 命名空间引用偏移量
namespace tg = PracLab::ReplayEngine::targets;
