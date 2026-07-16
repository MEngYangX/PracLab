// 函数签名扫描框架。
// 通过 gamedata.json 中存储的特征码（signature）在引擎模块内存中
// 定位关键函数地址，供 funchook 拦截使用。
//
// 支持双平台：
//   - Windows: GetModuleInformation + GetModuleHandleA + VirtualQuery
//   - Linux:   dl_iterate_phdr + dladdr
//
// 签名格式："AA BB ?? CC"，其中 ?? 表示通配符字节。

#pragma once

#include <cstddef>
#include <cstdint>
#include <string>
#include <vector>

#include <nlohmann/json.hpp>

namespace PracLab::ReplayEngine::Sig
{
    // 模块内一段可执行内存区间。
    struct ModuleSegment
    {
        unsigned char *Base = nullptr;
        size_t Size = 0;
    };

    // 模块信息：基址 + 总大小 + 各段。
    // bool 转换：Base 和 Size 均非零时视为有效。
    struct ModuleInfo
    {
        unsigned char *Base = nullptr;
        size_t Size = 0;
        std::vector<ModuleSegment> Segments;
        explicit operator bool() const { return Base != nullptr && Size != 0; }
    };

    // 从磁盘读取并解析 gamedata.json。
    // path: 文件路径；out: 解析后的 JSON 对象。
    // 失败（文件不存在或解析错误）返回 false。
    bool LoadGamedata(const char *path, nlohmann::json &out);

    // 返回当前平台名："windows" 或 "linux"。
    const char *PlatformName();

    // 从 gamedata 读取 gamedata[name].signatures[platform]。
    // 条目缺失或类型不符返回空字符串。
    std::string FindPlatformSig(const nlohmann::json &gamedata, const std::string &name);

    // 从 gamedata 读取 gamedata[name].offsets[platform]。
    // 条目缺失或非整数类型返回 fallback。
    int FindPlatformOffset(const nlohmann::json &gamedata, const std::string &name, int fallback);

    // 解析签名字符串。
    // sigStr: "AA BB ?? CC" 格式，?? 为通配符。
    // outBytes: 解析出的字节序列（通配符位置填 0）。
    // outWild: 对应位置是否为通配符。
    // 空字符串或格式错误返回 false。
    bool ParseSigString(const std::string &sigStr,
                        std::vector<uint8_t> &outBytes,
                        std::vector<bool> &outWild);

    // 在模块各段中查找模式。
    // module: 目标模块；pattern: 字节序列；wild: 通配符掩码。
    // 找到返回首字节地址，未找到返回 nullptr。
    void *FindPatternIn(const ModuleInfo &module,
                        const std::vector<uint8_t> &pattern,
                        const std::vector<bool> &wild);

    // 通过模块名定位模块（如 "server.dll" / "libserver.so"）。
    // 失败返回无效 ModuleInfo（bool 转换为 false）。
    ModuleInfo ModuleFromName(const char *moduleName);

    // 通过接口对象指针反查所属模块。
    // 先读取对象的 vtable 指针（*(void**)interfacePtr），
    // 再通过 vtable 地址定位所属模块。
    // 用于从 ISource2GameClients* 定位 server 模块。
    ModuleInfo ModuleFromInterfacePtr(void *interfacePtr);

    // 综合 API：从 gamedata 读取签名并在模块中扫描。
    // gamedata: 已加载的 gamedata.json；module: 目标模块；
    // name: gamedata 中的条目名。
    // 失败时返回 nullptr，并通过 errorOut 写入错误描述。
    void *ResolveSig(const nlohmann::json &gamedata, const ModuleInfo &module,
                     const char *name, char *errorOut, size_t errorOutLen);
}
