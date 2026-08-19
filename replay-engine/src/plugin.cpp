// PracLabReplayEngine Metamod:Source plugin entry point.
//
// Load/Unload orchestrate the BotController v0.6.1 subsystems:
//   Schema init → sig-scan → hook install (WeaponLocker/BotController/BuyController/InputInjector)
//   → record/replay ready for PRL_* C-ABI calls.

#include "plugin.h"

#include <ISmmPlugin.h>

#include <cstdio>
#include <cstring>
#include <string>

#include <eiface.h>
#include <icvar.h>
#include <convar.h>
#include <interfaces/interfaces.h>
#include <networksystem/inetworkmessages.h>
#include <tier0/dbg.h>

#include <nlohmann/json.hpp>

#include "WeaponLocker.h"
#include "BotController.h"
#include "BuyController.h"
#include "BuyControllerState.h"
#include "InputInjector.h"
#include "MotionRecorder.h"
#include "VoiceSender.h"
#include "dispatch.h"
#include "WeaponLockerState.h"
#include "BotControllerState.h"
#include "commands.h"
#include "sig_scan.h"
#include "schema_resolver.h"
#include "platform.h"
#include "version_targets.h"

namespace PracLab::ReplayEngine {

// addons/<name>/bin/<platform>/<lib> -> up 3 dirs -> addons/<name>/gamedata.json
static std::string ComputeGamedataPath()
{
    std::string p = BotController::SelfModulePath();
    if (p.empty()) return "";
    for (int i = 0; i < 3; ++i)
    {
        size_t slash = p.find_last_of("/\\");
        if (slash == std::string::npos) return "";
        p.resize(slash);
    }
    return p + "/gamedata.json";
}

} // namespace PracLab::ReplayEngine

// Global instance + Metamod export. Must appear before method definitions
// so PLUGIN_EXPOSE-defined globals (g_SMAPI, g_PLAPI, g_PLID, g_SHPtr) are
// visible to PLUGIN_SAVEVARS() inside Load().
PracLab::ReplayEngine::PracLabReplayEnginePlugin g_PracLabReplayEnginePlugin;
PLUGIN_EXPOSE(PracLab::ReplayEngine::PracLabReplayEnginePlugin, g_PracLabReplayEnginePlugin);

bool PracLab::ReplayEngine::PracLabReplayEnginePlugin::Load(PluginId id, ISmmAPI* ismm, char* error, size_t maxlen, bool /*late*/)
{
    Msg("[PracLab] PracLabReplayEnginePlugin::Load: Executing...\n");
    PLUGIN_SAVEVARS();

    g_pCVar = static_cast<ICvar*>(ismm->GetEngineFactory()(CVAR_INTERFACE_VERSION, nullptr));
    if (!g_pCVar)
    {
        std::snprintf(error, maxlen, "Failed to get ICvar (%s) via engine factory", CVAR_INTERFACE_VERSION);
        return false;
    }

    char schemaError[256] = { 0 };
    if (!BotController::Schema::Init(schemaError, sizeof(schemaError)))
    {
        std::snprintf(error, maxlen, "Schema initialization failed: %s", schemaError);
        return false;
    }
    if (!BotController::targets::LoadFromSchema(schemaError, sizeof(schemaError)))
    {
        BotController::Schema::Reset();
        std::snprintf(error, maxlen, "Schema target resolution failed: %s", schemaError);
        return false;
    }
    ConVar_Register(FCVAR_RELEASE | FCVAR_GAMEDLL);

    // IVEngineServer2::ClientCommand
    BotController::Dispatch::g_pEngine = static_cast<IVEngineServer2*>(ismm->GetEngineFactory()(INTERFACEVERSION_VENGINESERVER, nullptr));
    if (!BotController::Dispatch::g_pEngine)
    {
        std::snprintf(error, maxlen, "Failed to get IVEngineServer2 (%s)", INTERFACEVERSION_VENGINESERVER);
        return false;
    }

    // Need ISource2GameClients only as the anchor for sig-scan
    void* serverIface = ismm->GetServerFactory()(INTERFACEVERSION_SERVERGAMECLIENTS, nullptr);
    if (!serverIface)
    {
        std::snprintf(error, maxlen, "Failed to get ISource2GameClients (%s)", INTERFACEVERSION_SERVERGAMECLIENTS);
        return false;
    }

    // Engine interface used by console command output (ClientPrintf).
    BotController::Commands::g_pEngine = BotController::Dispatch::g_pEngine;

    // Server-side command executor for issuing bot "buy" commands.
    BotController::Dispatch::g_pGameClients = static_cast<ISource2GameClients*>(serverIface);

    // NetworkMessages lets the C ABI send recorded voice frames to clients.
    auto* networkMessages = static_cast<INetworkMessages*>(ismm->GetEngineFactory()(NETWORKMESSAGES_INTERFACE_VERSION, nullptr));
    if (!networkMessages)
    {
        networkMessages = static_cast<INetworkMessages*>(ismm->GetServerFactory()(NETWORKMESSAGES_INTERFACE_VERSION, nullptr));
    }
    BotController::VoiceSender::SetInterfaces(BotController::Dispatch::g_pEngine, networkMessages);
    if (!networkMessages)
    {
        Warning("[PracLab] network messages interface unavailable; voice send disabled\n");
    }

    std::string gamedataPath = ComputeGamedataPath();
    if (gamedataPath.empty())
    {
        std::snprintf(error, maxlen, "Failed to compute gamedata.json path");
        return false;
    }

    nlohmann::json gd;
    if (!BotController::Sig::LoadGamedata(gamedataPath.c_str(), gd))
    {
        std::snprintf(error, maxlen, "Failed to load gamedata: %s", gamedataPath.c_str());
        return false;
    }

    BotController::Sig::ModuleInfo serverModule = BotController::Sig::ModuleFromInterfacePtr(serverIface);
    if (!serverModule)
    {
        std::snprintf(error, maxlen, "ModuleFromInterfacePtr returned null");
        return false;
    }

    // Resolve non-Schema offsets before installing hooks that read targets
    BotController::targets::LoadFromGamedata(gd);

    if (!BotController::WeaponLockerHooks::Install(gd, serverModule, error, maxlen)) return false;

    if (!BotController::BotControllerHooks::Install(gd, serverModule, error, maxlen))
    {
        BotController::WeaponLockerHooks::Remove();
        return false;
    }

    // BuyController is optional; missing sig only disables buy control
    char buyErr[256] = { 0 };
    if (!BotController::BuyControllerHooks::Install(gd, serverModule, buyErr, sizeof(buyErr)))
    {
        Warning("[PracLab] BuyController::Install failed (%s); bot buy control disabled\n", buyErr);
    }

    // movement hooks for record/replay
    char injErr[256] = { 0 };
    if (!BotController::InputInjector::Install(gd, serverModule, injErr, sizeof(injErr)))
    {
        Warning("[PracLab] InputInjector::Install failed (%s); record/replay movement will be a no-op\n", injErr);
    }

    Msg("[PracLab] PracLabReplayEnginePlugin::Load: completed successfully\n");
    return true;
}

bool PracLab::ReplayEngine::PracLabReplayEnginePlugin::Unload(char* /*error*/, size_t /*maxlen*/)
{
    Msg("[PracLab] PracLabReplayEnginePlugin::Unload: Executing...\n");

    BotController::MotionRecorder::ClearAll();
    BotController::InputInjector::Remove();
    BotController::BuyControllerHooks::Remove();
    BotController::BuyControllerState::ClearAll();
    BotController::BotControllerHooks::Remove();
    BotController::WeaponLockerHooks::Remove();
    BotController::WeaponLockerState::ClearAll();
    BotController::BotControllerState::ClearAllAll();
    BotController::BotControllerState::ClearAllAim();
    BotController::Dispatch::g_pEngine = nullptr;
    BotController::Dispatch::g_pGameClients = nullptr;
    BotController::VoiceSender::SetInterfaces(nullptr, nullptr);
    BotController::Commands::g_pEngine = nullptr;
    BotController::Schema::Reset();
    ConVar_Unregister();
    g_pCVar = nullptr;

    Msg("[PracLab] PracLabReplayEnginePlugin::Unload: completed\n");
    return true;
}
