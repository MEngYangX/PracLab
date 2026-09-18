// Cross-platform self-module path

#pragma once

#include <string>

namespace bot_controller {
// Absolute path of this shared library on disk; empty on failure
std::string SelfModulePath();
} // namespace bot_controller
