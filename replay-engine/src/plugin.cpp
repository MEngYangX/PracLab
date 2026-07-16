// PracLabReplayEngine Metamod:Source 插件入口实现。
// Task 1: 空壳 Load/Unload，获取引擎接口并打印日志。
// Task 2: 在 Load 中集成 SigScan 扫描。
// Task 3: 在 Load 中加载偏移量、安装 funchook；在 Unload 中卸载 hook。

#include "plugin.h"
#include "platform.h"
#include "sig_scan.h"
#include "version_targets.h"
#include "hooks.h"
#include "weapon_switcher.h"

#include <cstdio>
#include <cstring>
#include <filesystem>
#include <string>

#include <ISmmPlugin.h>
#include <eiface.h>
#include <icvar.h>
#include <convar.h>
#include <interfaces/interfaces.h>

using namespace PracLab::ReplayEngine;

// 命名空间级变量：ISource2GameClients 锚点指针。
// Task 2 的 SigScan 通过此指针反查 server 模块；
// Task 3 的 hooks 模块直接访问此变量。
// 声明见 plugin.h 的 extern void *g_pGameClientsAnchor;
void *PracLab::ReplayEngine::g_pGameClientsAnchor = nullptr;

namespace
{
    // 文件内静态：引擎接口指针。
    // Task 2 起供 SigScan（锚点定位）和 Hook（命令注入）使用。
    IVEngineServer2 *_pEngine = nullptr;

    // 计算 gamedata.json 的部署路径。
    // 部署结构：
    //   addons/PracLabReplayEngine/bin/<platform>/PracLabReplayEngine.dll
    //   addons/PracLabReplayEngine/gamedata.json
    // 因此从插件 DLL/SO 路径向上退 3 级目录（bin/<platform>/ -> bin/ -> PracLabReplayEngine/）。
    // 返回空字符串表示获取本模块路径失败。
    std::string ComputeGamedataPath()
    {
        std::string self = Platform::SelfModulePath();
        if (self.empty())
            return "";

        std::filesystem::path p(self);
        // bin/<platform>/PracLabReplayEngine.dll -> bin/<platform>/
        p = p.parent_path();
        // bin/<platform>/ -> bin/
        p = p.parent_path();
        // bin/ -> PracLabReplayEngine/
        p = p.parent_path();
        p /= "gamedata.json";
        return p.string();
    }
}

// 全局插件实例，由 PLUGIN_EXPOSE 宏向 Metamod 暴露 CreateInterface_Mm 导出。
PracLab::ReplayEngine::PracLabReplayEnginePlugin g_PracLabReplayEnginePlugin;
PLUGIN_EXPOSE(PracLab::ReplayEngine::PracLabReplayEnginePlugin, g_PracLabReplayEnginePlugin);

bool PracLabReplayEnginePlugin::Load(PluginId id, ISmmAPI *ismm,
                                     char *error, size_t maxlen, bool /*late*/)
{
    PLUGIN_SAVEVARS();

    // 获取 ICvar 接口，ConVar_Register 依赖全局 g_pCVar 被正确设置。
    // g_pCVar 由 SDK convar.cpp 定义，icvar.h 中 extern 声明。
    g_pCVar = static_cast<ICvar *>(
        ismm->GetEngineFactory()(CVAR_INTERFACE_VERSION, nullptr));
    if (!g_pCVar)
    {
        std::snprintf(error, maxlen,
                      "Failed to get ICvar (%s) via engine factory",
                      CVAR_INTERFACE_VERSION);
        return false;
    }
    ConVar_Register(FCVAR_RELEASE | FCVAR_GAMEDLL);

    // 获取 IVEngineServer2，后续用于服务端命令执行（ClientPrintf 等）。
    _pEngine = static_cast<IVEngineServer2 *>(
        ismm->GetEngineFactory()(INTERFACEVERSION_VENGINESERVER, nullptr));
    if (!_pEngine)
    {
        std::snprintf(error, maxlen,
                      "Failed to get IVEngineServer2 (%s)",
                      INTERFACEVERSION_VENGINESERVER);
        return false;
    }

    // 获取 ISource2GameClients 作为 SigScan 锚点。
    // Task 2 通过该指针反查所属模块并扫描函数签名。
    void *serverIface =
        ismm->GetServerFactory()(INTERFACEVERSION_SERVERGAMECLIENTS, nullptr);
    if (!serverIface)
    {
        std::snprintf(error, maxlen,
                      "Failed to get ISource2GameClients (%s)",
                      INTERFACEVERSION_SERVERGAMECLIENTS);
        return false;
    }
    g_pGameClientsAnchor = serverIface;

    // ---- Task 3: 加载 gamedata.json + 偏移量 + 安装 hooks ----
    std::string gamedataPath = ComputeGamedataPath();
    if (gamedataPath.empty())
    {
        std::snprintf(error, maxlen,
                      "Failed to compute gamedata path from self module");
        return false;
    }

    nlohmann::json gd;
    if (!Sig::LoadGamedata(gamedataPath.c_str(), gd))
    {
        std::snprintf(error, maxlen,
                      "Failed to load gamedata: %s", gamedataPath.c_str());
        return false;
    }

    // 从锚点反查 server 模块（server.dll / libserver.so）
    Sig::ModuleInfo serverModule = Sig::ModuleFromInterfacePtr(g_pGameClientsAnchor);
    if (!serverModule)
    {
        std::snprintf(error, maxlen,
                      "Failed to resolve server module from game clients anchor");
        return false;
    }

    // 加载偏移量（覆盖编译时默认值）
    targets::LoadFromGamedata(gd);

    // 安装引擎函数 hook（ProcessMovement 必装，其余失败降级）
    char hookErr[512] = {0};
    if (!Hooks::Install(gd, serverModule, hookErr, sizeof(hookErr)))
    {
        std::snprintf(error, maxlen,
                      "Hooks::Install failed: %s", hookErr);
        return false;
    }

    // 安装武器切换模块（可选：失败不阻断插件加载，仅武器切换不可用）
    // 解析 CCSPlayer_WeaponServices::GetSlot/SelectItem 签名
    char wsErr[256] = {0};
    if (!WeaponSwitcher::Install(gd, serverModule, wsErr, sizeof(wsErr)))
    {
        // 降级：回放仍可工作，但 bot 不会按 tick 切换武器
        char dbg[384];
        std::snprintf(dbg, sizeof(dbg),
                      "[PracLabReplayEngine] WARN: WeaponSwitcher unavailable (%s); "
                      "replay weapon switching disabled\n",
                      wsErr[0] ? wsErr : "unknown");
        Platform::DebugOut(dbg);
    }

    char status[256];
    std::snprintf(status, sizeof(status),
                  "[PracLabReplayEngine] plugin loaded; hooks: %s; weapon_switcher: %s\n",
                  Hooks::Status(), WeaponSwitcher::Status());
    Platform::DebugOut(status);
    return true;
}

bool PracLabReplayEnginePlugin::Unload(char * /*error*/, size_t /*maxlen*/)
{
    // 先卸载 hook，再 unregister ConVar，避免 hook 仍在运行时引用已释放资源
    Hooks::Remove();
    WeaponSwitcher::Remove();
    ConVar_Unregister();
    _pEngine = nullptr;
    g_pGameClientsAnchor = nullptr;
    g_pCVar = nullptr;
    Platform::DebugOut("[PracLabReplayEngine] plugin unloaded\n");
    return true;
}
