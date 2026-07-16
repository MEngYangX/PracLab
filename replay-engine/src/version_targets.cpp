// 从 gamedata.json 覆盖结构偏移量（平台感知）。
// 每个偏移量在 gamedata 中以 "<Name>": { "offsets": { "windows": N, "linux": M } } 形式存储。
// 当前平台的值通过 Sig::FindPlatformOffset 读取，缺失时保留代码中的默认值。

#include "version_targets.h"
#include "sig_scan.h"

namespace PracLab::ReplayEngine::targets
{
    // 逐项从 gamedata 读取当前平台的偏移量；缺失则保留编译时默认值。
    void LoadFromGamedata(const nlohmann::json &gd)
    {
        // ---- CCSBot ----
        kBot_AiTickedFlag = Sig::FindPlatformOffset(gd, "CCSBot::AiTickedFlag", kBot_AiTickedFlag);
        kBot_Pawn          = Sig::FindPlatformOffset(gd, "CCSBot::Pawn", kBot_Pawn);
        kBot_Profile       = Sig::FindPlatformOffset(gd, "CCSBot::Profile", kBot_Profile);

        // ---- CBaseEntity / CEntityIdentity ----
        kEnt_Identity         = Sig::FindPlatformOffset(gd, "CBaseEntity::Identity", kEnt_Identity);
        kEntIdentity_EHandle  = Sig::FindPlatformOffset(gd, "CEntityIdentity::EHandle", kEntIdentity_EHandle);
        kEnt_MoveType         = Sig::FindPlatformOffset(gd, "CBaseEntity::MoveType", kEnt_MoveType);
        kEnt_ActualMoveType   = Sig::FindPlatformOffset(gd, "CBaseEntity::ActualMoveType", kEnt_ActualMoveType);
        kEnt_Flags            = Sig::FindPlatformOffset(gd, "CBaseEntity::Flags", kEnt_Flags);
        kEnt_AbsVelocity      = Sig::FindPlatformOffset(gd, "CBaseEntity::AbsVelocity", kEnt_AbsVelocity);
        kEnt_BodyComponent    = Sig::FindPlatformOffset(gd, "CBaseEntity::BodyComponent", kEnt_BodyComponent);
        kBody_SceneNode       = Sig::FindPlatformOffset(gd, "CBodyComponent::SceneNode", kBody_SceneNode);
        kNode_AbsOrigin       = Sig::FindPlatformOffset(gd, "CGameSceneNode::AbsOrigin", kNode_AbsOrigin);

        // ---- CCSPlayerPawn ----
        kPawn_WeaponServices   = Sig::FindPlatformOffset(gd, "CCSPlayerPawn::WeaponServices", kPawn_WeaponServices);
        kPawn_MovementServices = Sig::FindPlatformOffset(gd, "CBasePlayerPawn::MovementServices", kPawn_MovementServices);
        kPawn_Controller       = Sig::FindPlatformOffset(gd, "CCSPlayerPawn::Controller", kPawn_Controller);
        kPawn_OriginalController = Sig::FindPlatformOffset(gd, "CCSPlayerPawn::OriginalController", kPawn_OriginalController);
        kPawn_ViewAngle        = Sig::FindPlatformOffset(gd, "CCSPlayerPawn::ViewAngle", kPawn_ViewAngle);
        kPawn_EyeAngles        = Sig::FindPlatformOffset(gd, "CCSPlayerPawn::EyeAngles", kPawn_EyeAngles);

        // ---- CCSPlayer_WeaponServices ----
        kWs_ActiveWeapon = Sig::FindPlatformOffset(gd, "CCSPlayer_WeaponServices::ActiveWeapon", kWs_ActiveWeapon);

        // ---- CBasePlayerWeapon ----
        kWeapon_ItemDefIndex = Sig::FindPlatformOffset(gd, "CBasePlayerWeapon::ItemDefIndex", kWeapon_ItemDefIndex);

        // ---- CCSPlayer_MovementServices ----
        kServices_Pawn         = Sig::FindPlatformOffset(gd, "CCSPlayer_MovementServices::Pawn", kServices_Pawn);
        kServices_Buttons      = Sig::FindPlatformOffset(gd, "CCSPlayer_MovementServices::Buttons", kServices_Buttons);
        kServices_Buttons1     = Sig::FindPlatformOffset(gd, "CCSPlayer_MovementServices::Buttons1", kServices_Buttons1);
        kServices_Buttons2     = Sig::FindPlatformOffset(gd, "CCSPlayer_MovementServices::Buttons2", kServices_Buttons2);
        kServices_LadderNormal = Sig::FindPlatformOffset(gd, "CCSPlayer_MovementServices::LadderNormal", kServices_LadderNormal);
        kServices_Ducked       = Sig::FindPlatformOffset(gd, "CCSPlayer_MovementServices::Ducked", kServices_Ducked);
        kServices_DuckAmount   = Sig::FindPlatformOffset(gd, "CCSPlayer_MovementServices::DuckAmount", kServices_DuckAmount);
        kServices_DuckSpeed    = Sig::FindPlatformOffset(gd, "CCSPlayer_MovementServices::DuckSpeed", kServices_DuckSpeed);
        kServices_DesiresDuck  = Sig::FindPlatformOffset(gd, "CCSPlayer_MovementServices::DesiresDuck", kServices_DesiresDuck);
        kServices_Ducking      = Sig::FindPlatformOffset(gd, "CCSPlayer_MovementServices::Ducking", kServices_Ducking);

        // ---- CMoveData ----
        kMove_Velocity  = Sig::FindPlatformOffset(gd, "CMoveData::Velocity", kMove_Velocity);
        kMove_AbsOrigin = Sig::FindPlatformOffset(gd, "CMoveData::AbsOrigin", kMove_AbsOrigin);

        // ---- vtable 索引 ----
        kVtIdx_PlayerRunCommand = Sig::FindPlatformOffset(gd, "vtidx::PlayerRunCommand", kVtIdx_PlayerRunCommand);
        kVtIdx_FinishMove       = Sig::FindPlatformOffset(gd, "vtidx::FinishMove", kVtIdx_FinishMove);
    }
}
