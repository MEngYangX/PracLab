// Cross-platform sig scanning + gamedata.json loader

#pragma once

#include <cstdint>
#include <cstddef>
#include <string>
#include <vector>

#include <nlohmann/json.hpp>

namespace bot_controller::sig {
struct ModuleSegment
{
    unsigned char* base = nullptr;
    size_t size = 0;
};

struct ModuleInfo
{
    unsigned char* base = nullptr;
    size_t size = 0;
    std::vector<ModuleSegment> segments;
    explicit operator bool() const { return base != nullptr && size != 0; }
};

// Read + parse gamedata.json into out; false on open/parse error
bool LoadGamedata(const char* path, nlohmann::json& out);
// gamedata[name].signatures[platform]
std::string FindPlatformSig(const nlohmann::json& gamedata, const std::string& name);
// gamedata[name].offsets[platform]; fallback if missing/non-integer
int FindPlatformOffset(const nlohmann::json& gamedata, const std::string& name, int fallback);
const char* PlatformName();
bool ParseSigString(const std::string& sigStr, std::vector<uint8_t>& outBytes, std::vector<bool>& outWild);
void* FindPatternIn(const ModuleInfo& module, const std::vector<uint8_t>& pattern, const std::vector<bool>& wild);
// Resolve module by basename, e.g. server.dll / libserver.so
ModuleInfo ModuleFromName(const char* moduleName);
ModuleInfo ModuleFromInterfacePtr(void* interfacePtr);
// Resolve sig from gamedata against module; errorOut on failure
void* ResolveSig(const nlohmann::json& gamedata, const ModuleInfo& module, const char* name, char* errorOut, size_t errorOutLen);
} // namespace bot_controller::sig
