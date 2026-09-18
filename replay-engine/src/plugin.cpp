// PracLabReplayEngine Metamod:Source plugin entry point.
//
// Load/Unload orchestrate the BotController v0.6.3 subsystems:
//   Schema init → sig-scan → hook install (WeaponLocker/BotController/BuyController/InputInjector)
//   → record/replay ready for PRL_* C-ABI calls.

#include "plugin.h"

#include <ISmmPlugin.h>

#include <cstdio>
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
#include "ProjectileBirthAlign.h"
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
    std::string p = bot_controller::SelfModulePath();
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
    if (!bot_controller::schema::Init(schemaError, sizeof(schemaError)))
    {
        std::snprintf(error, maxlen, "Schema initialization failed: %s", schemaError);
        return false;
    }
    if (!bot_controller::targets::LoadFromSchema(schemaError, sizeof(schemaError)))
    {
        bot_controller::schema::Reset();
        std::snprintf(error, maxlen, "Schema target resolution failed: %s", schemaError);
        return false;
    }
    if (bot_controller::projectile_birth_align::ConfigureOffsets(bot_controller::targets::g_projectileInitialPosition,
                                                                 bot_controller::targets::g_projectileInitialVelocity) != 0)
    {
        Warning("[PracLab] projectile birth alignment offsets unavailable\n");
    }
    ConVar_Register(FCVAR_RELEASE | FCVAR_GAMEDLL);

    // IVEngineServer2::ClientCommand
    bot_controller::dispatch::g_engine = static_cast<IVEngineServer2*>(ismm->GetEngineFactory()(INTERFACEVERSION_VENGINESERVER, nullptr));
    if (!bot_controller::dispatch::g_engine)
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
    bot_controller::commands::g_engine = bot_controller::dispatch::g_engine;

    // Server-side command executor for issuing bot "buy" commands.
    bot_controller::dispatch::g_gameClients = static_cast<ISource2GameClients*>(serverIface);

    // NetworkMessages lets the C ABI send recorded voice frames to clients.
    auto* networkMessages = static_cast<INetworkMessages*>(ismm->GetEngineFactory()(NETWORKMESSAGES_INTERFACE_VERSION, nullptr));
    if (!networkMessages)
    {
        networkMessages = static_cast<INetworkMessages*>(ismm->GetServerFactory()(NETWORKMESSAGES_INTERFACE_VERSION, nullptr));
    }
    bot_controller::voice_sender::SetInterfaces(bot_controller::dispatch::g_engine, networkMessages);
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
    if (!bot_controller::sig::LoadGamedata(gamedataPath.c_str(), gd))
    {
        std::snprintf(error, maxlen, "Failed to load gamedata: %s", gamedataPath.c_str());
        return false;
    }

    bot_controller::sig::ModuleInfo serverModule = bot_controller::sig::ModuleFromInterfacePtr(serverIface);
    if (!serverModule)
    {
        std::snprintf(error, maxlen, "ModuleFromInterfacePtr returned null");
        return false;
    }

    // Resolve non-Schema offsets before installing hooks that read targets
    bot_controller::targets::LoadFromGamedata(gd);

    if (!bot_controller::weapon_locker_hooks::Install(gd, serverModule, error, maxlen)) return false;

    if (!bot_controller::bot_controller_hooks::Install(gd, serverModule, error, maxlen))
    {
        bot_controller::weapon_locker_hooks::Remove();
        return false;
    }

    // BuyController is optional; missing sig only disables buy control
    char buyErr[256] = { 0 };
    if (!bot_controller::buy_controller_hooks::Install(gd, serverModule, buyErr, sizeof(buyErr)))
    {
        Warning("[PracLab] BuyController::Install failed (%s); bot buy control disabled\n", buyErr);
    }

    // movement hooks for record/replay
    char injErr[256] = { 0 };
    if (!bot_controller::input_injector::Install(gd, serverModule, injErr, sizeof(injErr)))
    {
        Warning("[PracLab] InputInjector::Install failed (%s); record/replay movement will be a no-op\n", injErr);
    }

    Msg("[PracLab] PracLabReplayEnginePlugin::Load: completed successfully\n");
    return true;
}

bool PracLab::ReplayEngine::PracLabReplayEnginePlugin::Unload(char* /*error*/, size_t /*maxlen*/)
{
    Msg("[PracLab] PracLabReplayEnginePlugin::Unload: Executing...\n");

    bot_controller::motion_recorder::ClearAll();
    bot_controller::projectile_birth_align::Clear();
    bot_controller::input_injector::Remove();
    bot_controller::buy_controller_hooks::Remove();
    bot_controller::buy_controller_state::ClearAll();
    bot_controller::bot_controller_hooks::Remove();
    bot_controller::weapon_locker_hooks::Remove();
    bot_controller::weapon_locker_state::ClearAll();
    bot_controller::bot_controller_state::ClearAllAll();
    bot_controller::bot_controller_state::ClearAllAim();
    bot_controller::dispatch::g_engine = nullptr;
    bot_controller::dispatch::g_gameClients = nullptr;
    bot_controller::voice_sender::SetInterfaces(nullptr, nullptr);
    bot_controller::commands::g_engine = nullptr;
    bot_controller::schema::Reset();
    ConVar_Unregister();
    g_pCVar = nullptr;

    Msg("[PracLab] PracLabReplayEnginePlugin::Unload: completed\n");
    return true;
}
