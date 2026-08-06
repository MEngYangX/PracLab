// PracLabReplayEngine Metamod:Source plugin entry point.
//
// Wraps the BotController v0.6.0 record/replay engine under the
// PracLab::ReplayEngine namespace and exposes PRL_* C-ABI exports for
// the CounterStrikeSharp managed plugin to P/Invoke.

#pragma once

#include <ISmmPlugin.h>

namespace PracLab::ReplayEngine {

// Metamod:Source plugin class. Load/Unload orchestrate the BotController
// subsystems (schema, sig-scan, hooks, recorder, replayer).
class PracLabReplayEnginePlugin : public ISmmPlugin
{
  public:
    bool Load(PluginId id, ISmmAPI* ismm, char* error, size_t maxlen, bool late) override;
    bool Unload(char* error, size_t maxlen) override;

    bool Pause(char* /*error*/, size_t /*maxlen*/) override { return true; }
    bool Unpause(char* /*error*/, size_t /*maxlen*/) override { return true; }
    void AllPluginsLoaded() override {}

    const char* GetAuthor() override { return "PracLab"; }
    const char* GetName() override { return "PracLabReplayEngine"; }
    const char* GetDescription() override { return "Record & Replay engine for CS2 bots (based on BotController v0.6.0)."; }
    const char* GetURL() override { return ""; }
    const char* GetLicense() override { return "AGPL-3.0"; }
    const char* GetVersion() override { return "0.2.0"; }
    const char* GetDate() override { return __DATE__; }
    const char* GetLogTag() override { return "PRL"; }
};

} // namespace PracLab::ReplayEngine
