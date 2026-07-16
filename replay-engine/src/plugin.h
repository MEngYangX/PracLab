// PracLabReplayEngine Metamod:Source 插件入口声明。
// 继承 ISmmPlugin，实现 Load/Unload 生命周期。
// Task 1: 空壳实现，验证 Metamod 能加载并打印日志。
// Task 2+: 在 Load 中集成 SigScan、funchook、录制/回放引擎。

#pragma once

#include <ISmmPlugin.h>

namespace PracLab::ReplayEngine
{
    // ISource2GameClients 接口指针，作为 SigScan 锚点。
    // Task 2 通过该指针反查所属模块（server.dll / libserver.so），
    // Task 3 的 hooks 模块直接访问此变量定位引擎函数。
    // 由 plugin.cpp 在 Load 中赋值，Unload 中置空。
    extern void *g_pGameClientsAnchor;

    // Metamod 插件主类。
    // 负责：插件生命周期管理、引擎接口获取。
    // Task 1 仅实现 Load/Unload 空壳；Task 2 起在 Load 中初始化 SigScan 与 Hook。
    class PracLabReplayEnginePlugin : public ISmmPlugin
    {
    public:
        bool Load(PluginId id, ISmmAPI *ismm, char *error, size_t maxlen, bool late) override;
        bool Unload(char *error, size_t maxlen) override;

        bool Pause(char * /*error*/, size_t /*maxlen*/) override { return true; }
        bool Unpause(char * /*error*/, size_t /*maxlen*/) override { return true; }
        void AllPluginsLoaded() override {}

        const char *GetAuthor() override { return "PracLab"; }
        const char *GetName() override { return "PracLabReplayEngine"; }
        const char *GetDescription() override { return "PracLab replay engine: record & replay player movement for CS2."; }
        const char *GetURL() override { return ""; }
        const char *GetLicense() override { return "MIT"; }
        const char *GetVersion() override { return "0.1.0"; }
        const char *GetDate() override { return __DATE__; }
        const char *GetLogTag() override { return "PRL"; }
    };
}

