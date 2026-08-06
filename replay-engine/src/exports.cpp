// PracLabReplayEngine C-ABI exports for CounterStrikeSharp P/Invoke.
//
// Wraps BotController v0.6.0 C++ API under PRL_* functions matching the
// praclab_replay.h header contract. Existing PRL_* signatures are preserved
// for backward compatibility; new PRL_* extension functions expose the
// upstream v0.6.0 feature set (buy plans, voice, profile, locks, etc.).

#include "praclab_replay.h"

#include "dispatch.h"
#include "MotionRecorder.h"
#include "InputInjector.h"
#include "BuyControllerState.h"
#include "BotProfile.h"
#include "VoiceSender.h"
#include "BotControllerState.h"
#include "BotController.h"

#include <cstdint>
#include <cstring>
#include <string>
#include <vector>

using namespace BotController;

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

// ---- Recording ----

PRL_API int PRL_StartRecord(int slot)
{
    return MotionRecorder::StartRecord(slot) ? 1 : 0;
}

PRL_API int PRL_StopRecord(int slot)
{
    return MotionRecorder::StopRecord(slot) ? 1 : 0;
}

PRL_API int PRL_GetRecordedTickCount(int slot)
{
    return MotionRecorder::RecordedTickCount(slot);
}

PRL_API int PRL_GetRecordedMotion(int slot,
                                  PRL_ReplayTick *outTicks, int *outTickCount,
                                  PRL_SubtickMove *outSubs, int *outSubCount)
{
    if (!outTicks || !outTickCount || !outSubs || !outSubCount) return 0;
    int tickCap = *outTickCount;
    int subCap = *outSubCount;
    int gotT = MotionRecorder::CopyTicks(slot, reinterpret_cast<ReplayTick*>(outTicks), tickCap);
    if (gotT < 0) return 0;
    int gotS = MotionRecorder::CopySubticks(slot, reinterpret_cast<SubtickMove*>(outSubs), subCap);
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
    return MotionRecorder::LoadReplay(slot, reinterpret_cast<const ReplayTick*>(ticks), tickCount,
                                      reinterpret_cast<const SubtickMove*>(subs), subCount)
               ? 1
               : 0;
}

PRL_API int PRL_SetReplayPawn(int slot, void *pawn)
{
    return InputInjector::SetReplayPawn(slot, pawn) ? 1 : 0;
}

PRL_API int PRL_StartReplay(int slot, int loop)
{
    return MotionRecorder::StartReplay(slot, loop != 0) ? 1 : 0;
}

PRL_API int PRL_StopReplay(int slot)
{
    return MotionRecorder::StopReplay(slot) ? 1 : 0;
}

PRL_API int PRL_IsReplaying(int slot)
{
    return MotionRecorder::IsReplaying(slot) ? 1 : 0;
}

PRL_API int PRL_FreezeSlot(int slot)
{
    return Dispatch::Lock(slot, LockKind::All, 0) == 0 ? 1 : 0;
}

PRL_API int PRL_UnfreezeSlot(int slot)
{
    return Dispatch::Unlock(slot, LockKind::All) == 0 ? 1 : 0;
}

PRL_API int PRL_GetCurrentReplayWeaponDef(int slot)
{
    return MotionRecorder::CurrentReplayWeaponDef(slot);
}

PRL_API int PRL_GetBotActiveWeaponDef(int slot)
{
    return MotionRecorder::BotActiveWeaponDef(slot);
}

// ---- Diagnostics ----

PRL_API int PRL_GetDiagnosticCounters(PRL_DiagnosticCounters *out)
{
    if (!out) return 0;
    std::memset(out, 0, sizeof(*out));

    out->processMovementCalls = InputInjector::HookCallCount();
    out->finishMoveCalls = InputInjector::FinishMoveCallCount();
    out->physicsSimulateCalls = InputInjector::PhysicsSimulateCallCount();
    out->replayCommitCalls = InputInjector::ReplayCommitCount();
    out->playerRunCommandCalls = InputInjector::PlayerRunCommandCallCount();

    const bool hooksInstalled = InputInjector::ProcessUsercmdAddress() != nullptr;
    out->physicsActive = hooksInstalled ? 1 : 0;
    out->subtickActive = hooksInstalled ? 1 : 0;

    out->lastProcessMovementSlot = InputInjector::LastResolvedSlot();
    out->lastPhysicsSimulateSlot = InputInjector::LastPhysicsSlot();

    // Scan slots to find the one currently replaying for diagnostic purposes.
    out->lastReplaySlot = -1;
    out->lastReplayCursor = -1;
    for (int s = 0; s < MotionRecorder::kMaxSlots; ++s)
    {
        if (MotionRecorder::IsReplaying(s))
        {
            out->lastReplaySlot = s;
            out->lastReplayCursor = MotionRecorder::ReplayCursor(s);
            break;
        }
    }

    // Fields with no direct upstream equivalent are left as 0:
    //   replayPreCalls, replayFinishMoveCalls, replayPrePawnNull, replayCommitPawnNull,
    //   ccsbotUpdateCalls, ccsbotUpkeepCalls, ccsbotUpdateLookAnglesCalls, setEyeAnglesCalls,
    //   lastWrittenOrigin*, lastReadBackOrigin*, lastPawnIdentity, lastPawnMoveType
    return 1;
}

// ---- Extended API (v0.2.0+, BotController v0.6.0 ABI 17) ----

PRL_API int PRL_GetAbiVersion(void)
{
    return 17;
}

PRL_API int PRL_LoadReplayExtended(int slot,
                                   const PRL_ReplayTick *ticks, int tickCount,
                                   const PRL_SubtickMove *subs, int subCount,
                                   const PRL_ReplayCommandFrameData *commands, int commandCount,
                                   const PRL_ReplayMovementExtra *movementExtras, int movementExtraCount)
{
    return MotionRecorder::LoadReplayExtended(slot,
                                              reinterpret_cast<const ReplayTick*>(ticks), tickCount,
                                              reinterpret_cast<const SubtickMove*>(subs), subCount,
                                              reinterpret_cast<const ReplayCommandFrameData*>(commands), commandCount,
                                              reinterpret_cast<const ReplayMovementExtra*>(movementExtras), movementExtraCount)
               ? 1
               : 0;
}

PRL_API int PRL_GetReplayCursor(int slot)
{
    return MotionRecorder::ReplayCursor(slot);
}

PRL_API int PRL_GetReplayTotal(int slot)
{
    return MotionRecorder::ReplayTotal(slot);
}

PRL_API int PRL_GetReplayTick(int slot, PRL_ReplayTick *out)
{
    if (!out) return 0;
    return MotionRecorder::CurrentReplayTick(slot, reinterpret_cast<ReplayTick&>(*out)) ? 1 : 0;
}

PRL_API int PRL_SwitchBotWeapon(int slot, int defIndex)
{
    return MotionRecorder::SwitchBotWeaponByDef(slot, defIndex) ? 1 : 0;
}

PRL_API long long PRL_InjectUsercmd(int slot, unsigned long long buttonMask, int durationMs)
{
    return static_cast<long long>(InputInjector::InjectUsercmd(slot, buttonMask, durationMs));
}

PRL_API int PRL_CancelUsercmdInjection(int slot, long long injectionId)
{
    return InputInjector::CancelUsercmdInjection(slot, static_cast<int64_t>(injectionId)) ? 1 : 0;
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
    if (slot < 0 || slot >= BuyControllerState::kMaxSlots) return -2;
    BuyControllerState::Set(slot, SplitAliases(aliases), false);
    return 0;
}

PRL_API int PRL_SetBuySkip(int slot)
{
    if (slot < 0 || slot >= BuyControllerState::kMaxSlots) return -2;
    BuyControllerState::Set(slot, {}, true);
    return 0;
}

PRL_API int PRL_ClearBuyPlan(int slot)
{
    if (slot < 0 || slot >= BuyControllerState::kMaxSlots) return -2;
    BuyControllerState::Clear(slot);
    return 0;
}

// ---- Bot profile ----

PRL_API int PRL_GetProfile(int slot, PRL_BotProfileData *out)
{
    if (!out) return 0;
    return BotProfile::ReadProfile(slot, reinterpret_cast<BotProfileData&>(*out)) ? 1 : 0;
}

// ---- Voice ----

PRL_API int PRL_CanSendVoice(void)
{
    return VoiceSender::IsAvailable() ? 1 : 0;
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
    return VoiceSender::SendVoiceFrame(recipientSlot, senderClient, senderXuid,
                                       audio, audioBytes, sampleRate, voiceLevel,
                                       sequenceBytes, sectionNumber, uncompressedSampleOffset,
                                       numPackets, packetOffsets, packetOffsetCount,
                                       tick, audibleMask);
}

// ---- Lock / Unlock ----

PRL_API int PRL_Lock(int slot, int kind, int arg)
{
    return Dispatch::Lock(slot, static_cast<LockKind>(kind), arg);
}

PRL_API int PRL_Unlock(int slot, int kind)
{
    return Dispatch::Unlock(slot, static_cast<LockKind>(kind));
}
