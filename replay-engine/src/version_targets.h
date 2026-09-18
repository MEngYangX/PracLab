// version_targets.h

#pragma once

#include <cstddef>

#include <nlohmann/json.hpp>

namespace bot_controller::targets {
// ---- CCSBot ----

// AI-ran-this-tick byte flag; set to 1 to fake a completed tick
inline int g_botAiTickedFlag = 0x610;
// CCSBot -> pawn (CCSPlayerPawn*)
inline int g_botPawn = 0x18;
// CCSBot -> m_profile (BotProfile*)
inline int g_botProfile = 0x08;

// ---- BotProfile (CCSBot+kBot_Profile) ----

inline int g_profAggression = 0x08; // float, 0..1
inline int g_profSkill = 0x0C; // float, 0..1
inline int g_profTeamwork = 0x10; // float, 0..1
inline int g_profWeaponPref = 0x24; // WORD[16] item def index, stride 2
inline int g_profWeaponPrefCount = 0x44; // int
inline int g_profCost = 0x48; // int
inline int g_profDifficulty = 0x50; // u8 bitflags EASY/NORMAL/HARD/EXPERT
inline int g_profReactionTime = 0x58; // float
inline int g_profAttackDelay = 0x5C; // float
inline int g_profLookAccelAtk = 0x78; // float m_lookAngleMaxAccelAttacking
inline int g_profLookStiffAtk = 0x7C; // float m_lookAngleStiffnessAttacking
inline int g_profLookDampAtk = 0x80; // float m_lookAngleDampingAttacking

// ---- BuyState ----

// m_isInitialDelay (bool)
inline int g_buyInitialDelay = 0x08;
// m_doneBuying (bool)
inline int g_buyDoneBuying = 0x18;

// ---- CBaseEntity / CEntityIdentity ----

// entity -> CEntityIdentity*
inline int g_entIdentity = 0x10;
// CEntityIdentity -> m_EHandle (low 15 bits = entity index)
inline int g_entIdentityEHandle = 0x10;
// m_MoveType (MoveType_t, 1 byte) — restored each replay tick
inline int g_entMoveType = 0x2F3;
// m_nActualMoveType (MoveType_t, 1 byte) — networked move type
inline int g_entActualMoveType = 0x2F5;
// m_fFlags (bit0 = FL_ONGROUND, bit1 = FL_DUCKING)
inline int g_entFlags = 0x388;
// m_fFlags bit masks restored on replay (constants, not offsets)
inline constexpr unsigned kFlOnGround = 1U << 0;
inline constexpr unsigned kFlDucking = 1U << 1;
// m_vecAbsVelocity
inline int g_entAbsVelocity = 0x38C;
// entity -> m_CBodyComponent -> m_pSceneNode
inline int g_entBodyComponent = 0x30;
inline int g_bodySceneNode = 0x08;
inline int g_nodeAbsOrigin = 0xC8;

// ---- CCSPlayerPawn ----

// m_pWeaponServices
inline int g_pawnWeaponServices = 0xA30;
// m_pItemServices
inline int g_pawnItemServices = 0xA20;
// m_pMovementServices
inline int g_pawnMovementServices = 0xA70;
// m_hController (CHandle)
inline int g_pawnController = 0xBB0;
// m_hOriginalController (CHandle)
inline int g_pawnOriginalController = 0xD24;
// CCSPlayerPawn -> v_angle (QAngle)
inline int g_pawnViewAngle = 0xAE8;
// CCSPlayerPawn -> v_anglePrevious (QAngle)
inline int g_pawnViewAnglePrevious = 0xAF4;
// Embedded server view-angle change vector
inline int g_pawnServerViewAngleChanges = 0xA80;
// m_angEyeAngles (QAngle) — written each replay tick alongside v_angle
inline int g_pawnEyeAngles = 0x1368;

// ---- CBaseCSGrenadeProjectile ----

inline int g_projectileInitialPosition = -1;
inline int g_projectileInitialVelocity = -1;

// ---- CCSPlayer_WeaponServices ----

// m_hActiveWeapon (CHandle)
inline int g_wsActiveWeapon = 0x60;

// ---- CBasePlayerWeapon ----

// m_AttributeManager -> m_Item -> m_iItemDefinitionIndex,
inline int g_weaponItemDefIndex = 0x978 + 0x50 + 0x38;

// ---- CCSPlayer_MovementServices ----

// m_pawn (CCSPlayerPawn*)
inline int g_servicesPawn = 56;
// m_nButtons.m_pButtonStates[0..2] — engine button state block (CInButtonState)
inline int g_servicesButtons = 88; // states[0] (pressed)
inline int g_servicesButtons1 = 88 + 8; // states[1]
inline int g_servicesButtons2 = 88 + 16; // states[2]
// Previous command view angles consumed by PlayerRunCommand
inline int g_servicesOldViewAngles = 0x240;

// duck/ladder state
inline int g_servicesLadderNormal = 0x3F8; // Vector m_vecLadderNormal
inline int g_servicesDucked = 0x408; // bool m_bDucked
inline int g_servicesDuckAmount = 0x40C; // float m_flDuckAmount
inline int g_servicesDuckSpeed = 0x410; // float m_flDuckSpeed
inline int g_servicesDesiresDuck = 0x415; // bool m_bDesiresDuck
inline int g_servicesDucking = 0x416; // bool m_bDucking

// ---- CMoveData  ----

// m_vecVelocity — the velocity TryPlayerMove integrates into origin
inline int g_moveVelocity = 56;
// m_vecAbsOrigin — post-move origin written here before FinishMove commits
inline int g_moveAbsOrigin = 200;

// ---- vtable indices (CCSPlayer_MovementServices) ----

inline int g_vtIdxPlayerRunCommand = 25;
inline int g_vtIdxFinishMove = 38;
inline int g_vtIdxDropWeapon = 24;

void LoadFromGamedata(const nlohmann::json& gd);

// Resolves every required Schema-backed target or reports the first failure
bool LoadFromSchema(char* errorOut, size_t errorOutLen);

} // namespace bot_controller::targets
