// Detour for BuyState::OnUpdate to force a bot's per-round buy plan.

#pragma once

#include <nlohmann/json.hpp>
#include "sig_scan.h"

namespace bot_controller {
namespace buy_controller_hooks {
// Resolve sig + offsets and install the detour.
bool Install(const nlohmann::json& gd, const sig::ModuleInfo& serverModule, char* errorOut, size_t errorOutLen);

void Remove();

const char* Status();

void* OnUpdateAddress();
} // namespace buy_controller_hooks
} // namespace bot_controller
