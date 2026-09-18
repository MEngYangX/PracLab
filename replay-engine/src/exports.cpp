// PracLabReplayEngine C-ABI exports for CounterStrikeSharp P/Invoke.
//
// Wraps BotController v0.6.3 C++ API under PRL_* functions matching the
// praclab_replay.h header contract. Existing PRL_* signatures are preserved
// for backward compatibility; new PRL_* extension functions expose the
// upstream v0.6.3 feature set (usercmd movement/suppression, projectile
// birth align, drop weapon replay, etc.).

#include "praclab_replay.h"

#include "dispatch.h"
#include "MotionRecorder.h"
#include "InputInjector.h"
#include "BuyControllerState.h"
#include "BotProfile.h"
#include "VoiceSender.h"
#include "ProjectileBirthAlign.h"
#include "BotControllerState.h"
#include "BotController.h"

#include <cstdint>
#include <cstring>
#include <string>
#include <vector>

using namespace bot_controller;

// ---- ABI layout verification ----
// PRL_* structs use C-compatible types (unsigned int / unsigned char /
// unsigned long long / unsigned short) which match stdint types on x64.
// static_assert guarantees layout compatibility at compile time.
static_assert(sizeof(PRL_MovementSnapshot) == sizeof(MovementSnapshot), "PRL_MovementSnapshot layout mismatch");
static_assert(sizeof(PRL_ReplayTick) == sizeof(ReplayTick), "PRL_ReplayTick layout mismatch");
static_assert(sizeof(PRL_SubtickMove) == sizeof(SubtickMove), "PRL_SubtickMove layout mismatch");
static_assert(sizeof(PRL_ReplayCommandFrameData) == sizeof(ReplayCommandFrameData), "PRL_ReplayCommandFrameData layout mismatch");
static_assert(sizeof(PRL_ReplayMovementExtra) == sizeof(ReplayMovementExtra), "PRL_ReplayMovementExtra layout mismatch");
static_assert(sizeof(PRL_BotProfileData) == sizeof(BotProfileData), "PRL_BotProfileData layout mismatch");
static_assert(sizeof(PRL_ProjectileBirthAlignStatus) == sizeof(projectile_birth_align::Status), "PRL_ProjectileBirthAlignStatus layout mismatch");

// ---- Recording ----

PRL_API int PRL_StartRecord(int slot)
{
    return motion_recorder::StartRecord(slot) ? 1 : 0;
}

PRL_API int PRL_StopRecord(int slot)
{
    return motion_recorder::StopRecord(slot) ? 1 : 0;
}

PRL_API int PRL_GetRecordedTickCount(int slot)
{
    return motion_recorder::RecordedTickCount(slot);
}

PRL_API int PRL_GetRecordedMotion(int slot,
                                  PRL_ReplayTick *outTicks, int *outTickCount,
                                  PRL_SubtickMove *outSubs, int *outSubCount)
{
    if (!outTicks || !outTickCount || !outSubs || !outSubCount) return 0;
    int tickCap = *outTickCount;
    int subCap = *outSubCount;
    int gotT = motion_recorder::CopyTicks(slot, reinterpret_cast<ReplayTick*>(outTicks), tickCap);
    if (gotT < 0) return 0;
    int gotS = motion_recorder::CopySubticks(slot, reinterpret_cast<SubtickMove*>(outSubs), subCap);
    if (gotS < 0) gotS = 0;
    *outTickCount = gotT;
    *outSubCount = gotS;
    return 1;
}

// ---- Replay ----

PRL_API int PRL_LoadReplay(int slot,
                           const PRL_ReplayTick *ticks, int tickCount,
                           const PRL_SubtickMove *subs, int subCount)
{
    return motion_recorder::LoadReplay(slot, reinterpret_cast<const ReplayTick*>(ticks), tickCount,
                                       reinterpret_cast<const SubtickMove*>(subs), subCount)
               ? 1
               : 0;
}

PRL_API int PRL_SetReplayPawn(int slot, void *pawn)
{
    return input_injector::SetReplayPawn(slot, pawn) ? 1 : 0;
}

PRL_API int PRL_StartReplay(int slot, int loop)
{
    return motion_recorder::StartReplay(slot, loop != 0) ? 1 : 0;
}

PRL_API int PRL_StopReplay(int slot)
{
    return motion_recorder::StopReplay(slot) ? 1 : 0;
}

PRL_API int PRL_IsReplaying(int slot)
{
    return motion_recorder::IsReplaying(slot) ? 1 : 0;
}

PRL_API int PRL_FreezeSlot(int slot)
{
    return dispatch::Lock(slot, LockKind::All, 0) == 0 ? 1 : 0;
}

PRL_API int PRL_UnfreezeSlot(int slot)
{
    return dispatch::Unlock(slot, LockKind::All) == 0 ? 1 : 0;
}

PRL_API int PRL_GetCurrentReplayWeaponDef(int slot)
{
    return motion_recorder::CurrentReplayWeaponDef(slot);
}

PRL_API int PRL_GetBotActiveWeaponDef(int slot)
{
    return motion_recorder::BotActiveWeaponDef(slot);
}

// ---- Diagnostics ----

PRL_API int PRL_GetDiagnosticCounters(PRL_DiagnosticCounters *out)
{
    if (!out) return 0;
    std::memset(out, 0, sizeof(*out));

    out->processMovementCalls = input_injector::HookCallCount();
    out->finishMoveCalls = input_injector::FinishMoveCallCount();
    out->physicsSimulateCalls = input_injector::PhysicsSimulateCallCount();
    out->replayCommitCalls = input_injector::ReplayCommitCount();
    out->playerRunCommandCalls = input_injector::PlayerRunCommandCallCount();

    const bool hooksInstalled = input_injector::ProcessUsercmdAddress() != nullptr;
    out->physicsActive = hooksInstalled ? 1 : 0;
    out->subtickActive = hooksInstalled ? 1 : 0;

    out->lastProcessMovementSlot = input_injector::LastResolvedSlot();
    out->lastPhysicsSimulateSlot = input_injector::LastPhysicsSlot();

    // Scan slots to find the one currently replaying for diagnostic purposes.
    out->lastReplaySlot = -1;
    out->lastReplayCursor = -1;
    for (int s = 0; s < motion_recorder::kMaxSlots; ++s)
    {
        if (motion_recorder::IsReplaying(s))
        {
            out->lastReplaySlot = s;
            out->lastReplayCursor = motion_recorder::ReplayCursor(s);
            break;
        }
    }

    // Fields with no direct upstream equivalent are left as 0:
    //   replayPreCalls, replayFinishMoveCalls, replayPrePawnNull, replayCommitPawnNull,
    //   ccsbotUpdateCalls, ccsbotUpkeepCalls, ccsbotUpdateLookAnglesCalls, setEyeAnglesCalls,
    //   lastWrittenOrigin*, lastReadBackOrigin*, lastPawnIdentity, lastPawnMoveType
    return 1;
}

// ---- Extended API (v0.2.2+, BotController v0.6.3 ABI 20) ----

PRL_API int PRL_GetAbiVersion(void)
{
    return 20;
}

PRL_API int PRL_LoadReplayExtended(int slot,
                                   const PRL_ReplayTick *ticks, int tickCount,
                                   const PRL_SubtickMove *subs, int subCount,
                                   const PRL_ReplayCommandFrameData *commands, int commandCount,
                                   const PRL_ReplayMovementExtra *movementExtras, int movementExtraCount)
{
    return motion_recorder::LoadReplayExtended(slot,
                                               reinterpret_cast<const ReplayTick*>(ticks), tickCount,
                                               reinterpret_cast<const SubtickMove*>(subs), subCount,
                                               reinterpret_cast<const ReplayCommandFrameData*>(commands), commandCount,
                                               reinterpret_cast<const ReplayMovementExtra*>(movementExtras), movementExtraCount)
               ? 1
               : 0;
}

PRL_API int PRL_GetReplayCursor(int slot)
{
    return motion_recorder::ReplayCursor(slot);
}

PRL_API int PRL_GetReplayTotal(int slot)
{
    return motion_recorder::ReplayTotal(slot);
}

PRL_API int PRL_GetReplayTick(int slot, PRL_ReplayTick *out)
{
    if (!out) return 0;
    return motion_recorder::CurrentReplayTick(slot, reinterpret_cast<ReplayTick&>(*out)) ? 1 : 0;
}

PRL_API int PRL_SwitchBotWeapon(int slot, int defIndex)
{
    return motion_recorder::SwitchBotWeaponByDef(slot, defIndex) ? 1 : 0;
}

PRL_API long long PRL_InjectUsercmd(int slot, unsigned long long buttonMask, int durationMs)
{
    return static_cast<long long>(input_injector::InjectUsercmd(slot, buttonMask, durationMs));
}

PRL_API int PRL_CancelUsercmdInjection(int slot, long long injectionId)
{
    return input_injector::CancelUsercmdInjection(slot, static_cast<int64_t>(injectionId)) ? 1 : 0;
}

PRL_API int PRL_SuppressUsercmd(int slot, unsigned long long buttonMask, int durationMs)
{
    return input_injector::SuppressUsercmd(slot, buttonMask, durationMs) ? 1 : 0;
}

// ---- Usercmd movement override (upstream v0.6.3) ----

// Create an independently cancellable persistent analog movement override.
// Returns movementId, <0 on failure.
PRL_API long long PRL_StartUsercmdMovement(int slot, float forwardMove, float leftMove)
{
    return static_cast<long long>(input_injector::StartUsercmdMovement(slot, forwardMove, leftMove));
}

// Update one persistent analog movement override. Returns 1 success, 0 failure.
PRL_API int PRL_UpdateUsercmdMovement(int slot, long long movementId, float forwardMove, float leftMove)
{
    return input_injector::UpdateUsercmdMovement(slot, static_cast<int64_t>(movementId), forwardMove, leftMove) ? 1 : 0;
}

// Cancel one persistent analog movement override. Returns 1 success, 0 failure.
PRL_API int PRL_CancelUsercmdMovement(int slot, long long movementId)
{
    return input_injector::CancelUsercmdMovement(slot, static_cast<int64_t>(movementId)) ? 1 : 0;
}

// ---- Persistent usercmd suppression (upstream v0.6.3) ----

// Create an independently cancellable persistent usercmd suppression.
// Returns suppressionId, <0 on failure.
PRL_API long long PRL_StartUsercmdSuppression(int slot, unsigned long long buttonMask)
{
    return static_cast<long long>(input_injector::StartUsercmdSuppression(slot, buttonMask));
}

// Cancel one persistent usercmd suppression. Returns 1 success, 0 failure.
PRL_API int PRL_CancelUsercmdSuppression(int slot, long long suppressionId)
{
    return input_injector::CancelUsercmdSuppression(slot, static_cast<int64_t>(suppressionId)) ? 1 : 0;
}

// ---- Projectile birth align (upstream v0.6.3) ----

// Configure native projectile birth field offsets for the current build.
// Returns 0 success, <0 failure.
PRL_API int PRL_SetProjectileBirthAlignOffsets(int initialPositionOffset, int initialVelocityOffset)
{
    return projectile_birth_align::ConfigureOffsets(initialPositionOffset, initialVelocityOffset);
}

// Queue one projectile's recorded birth position and velocity.
// Returns 0 success, <0 failure.
PRL_API int PRL_QueueProjectileBirthAlign(unsigned long long entityPtr,
                                          float posX, float posY, float posZ,
                                          float velX, float velY, float velZ)
{
    return projectile_birth_align::Queue(static_cast<uint64_t>(entityPtr), posX, posY, posZ, velX, velY, velZ);
}

// Clear pending projectile alignment writes. Returns the number removed.
PRL_API int PRL_ClearProjectileBirthAlign(void)
{
    return projectile_birth_align::Clear();
}

// Copy projectile alignment diagnostics into the caller's status buffer.
// Returns 0 success, <0 failure.
PRL_API int PRL_GetProjectileBirthAlignStatus(PRL_ProjectileBirthAlignStatus *out, int size)
{
    if (!out) return -1;
    return projectile_birth_align::GetStatus(reinterpret_cast<projectile_birth_align::Status*>(out), size);
}

// ---- Buy plan ----

// Split a space/comma separated alias string into tokens.
static std::vector<std::string> SplitAliases(const char *csv)
{
    std::vector<std::string> out;
    if (!csv) return out;
    std::string cur;
    for (const char *p = csv; *p; ++p)
    {
        char c = *p;
        if (c == ' ' || c == ',' || c == '\t')
        {
            if (!cur.empty())
            {
                out.push_back(cur);
                cur.clear();
            }
        }
        else
        {
            cur.push_back(c);
        }
    }
    if (!cur.empty()) out.push_back(cur);
    return out;
}

PRL_API int PRL_SetBuyPlan(int slot, const char *aliases)
{
    if (slot < 0 || slot >= buy_controller_state::kMaxSlots) return -2;
    buy_controller_state::Set(slot, SplitAliases(aliases), false);
    return 0;
}

PRL_API int PRL_SetBuySkip(int slot)
{
    if (slot < 0 || slot >= buy_controller_state::kMaxSlots) return -2;
    buy_controller_state::Set(slot, {}, true);
    return 0;
}

PRL_API int PRL_ClearBuyPlan(int slot)
{
    if (slot < 0 || slot >= buy_controller_state::kMaxSlots) return -2;
    buy_controller_state::Clear(slot);
    return 0;
}

// ---- Bot profile ----

PRL_API int PRL_GetProfile(int slot, PRL_BotProfileData *out)
{
    if (!out) return 0;
    return bot_profile::ReadProfile(slot, reinterpret_cast<BotProfileData&>(*out)) ? 1 : 0;
}

// ---- Voice ----

PRL_API int PRL_CanSendVoice(void)
{
    return voice_sender::IsAvailable() ? 1 : 0;
}

PRL_API int PRL_SendVoiceFrame(int recipientSlot,
                               int senderClient,
                               unsigned long long senderXuid,
                               const unsigned char *audio,
                               int audioBytes,
                               int sampleRate,
                               float voiceLevel,
                               int sequenceBytes,
                               int sectionNumber,
                               int uncompressedSampleOffset,
                               unsigned int numPackets,
                               const unsigned int *packetOffsets,
                               int packetOffsetCount,
                               int tick,
                               int audibleMask)
{
    return voice_sender::SendVoiceFrame(recipientSlot, senderClient, senderXuid,
                                        audio, audioBytes, sampleRate, voiceLevel,
                                        sequenceBytes, sectionNumber, uncompressedSampleOffset,
                                        numPackets, packetOffsets, packetOffsetCount,
                                        tick, audibleMask);
}

// ---- Lock / Unlock ----

PRL_API int PRL_Lock(int slot, int kind, int arg)
{
    return dispatch::Lock(slot, static_cast<LockKind>(kind), arg);
}

PRL_API int PRL_Unlock(int slot, int kind)
{
    return dispatch::Unlock(slot, static_cast<LockKind>(kind));
}
