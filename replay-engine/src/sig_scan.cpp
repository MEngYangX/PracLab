// 函数签名扫描框架实现。
// 独立编写，仅参考 CS2-Bot-Controller 的技术思路（平台 API 使用方式、
// 模块定位算法），不复制其源代码。

#include "sig_scan.h"
#include "memory.h"

#include <algorithm>
#include <cstdio>
#include <cstdlib>
#include <cstring>
#include <fstream>
#include <string>

#if defined(_WIN32)
#ifndef WIN32_LEAN_AND_MEAN
#define WIN32_LEAN_AND_MEAN
#endif
#ifndef NOMINMAX
#define NOMINMAX
#endif
#include <Windows.h>
#include <psapi.h>
#else
#include <dlfcn.h>
#include <link.h>
#endif

namespace PracLab::ReplayEngine::Sig
{
    namespace
    {
        // 取路径的文件名部分（去掉目录前缀）。
        // 同时处理 '/' 和 '\' 分隔符，兼容 Windows 路径在 Linux 上的比较。
        const char *PathBasename(const char *path)
        {
            if (!path)
                return "";
            const char *slash = std::strrchr(path, '/');
            const char *backslash = std::strrchr(path, '\\');
            const char *tail;
            if (slash && backslash)
                tail = slash > backslash ? slash : backslash;
            else if (slash)
                tail = slash;
            else
                tail = backslash;
            return tail ? tail + 1 : path;
        }

        // 向 errorOut 缓冲区写入格式化错误信息。
        void WriteError(char *out, size_t outLen, const char *fmt,
                        const char *a, const char *b = nullptr)
        {
            if (!out || outLen == 0)
                return;
            if (b)
                std::snprintf(out, outLen, fmt, a, b);
            else
                std::snprintf(out, outLen, fmt, a);
        }

#if defined(_WIN32)
        // Windows：通过 HMODULE 构建 ModuleInfo。
        // 使用 GetModuleInformation 获取基址和镜像大小，
        // 整个镜像作为一个段返回。
        ModuleInfo BuildModuleFromHandle(HMODULE handle)
        {
            ModuleInfo info;
            if (!handle)
                return info;

            MODULEINFO mi{};
            if (!GetModuleInformation(GetCurrentProcess(), handle, &mi, sizeof(mi)))
                return info;

            info.Base = static_cast<unsigned char *>(mi.lpBaseOfDll);
            info.Size = static_cast<size_t>(mi.SizeOfImage);
            info.Segments.push_back({info.Base, info.Size});
            return info;
        }
#else
        // Linux：从 dl_phdr_info 填充 ModuleInfo。
        // 遍历所有 PT_LOAD 段，收集每段基址和大小，
        // 同时计算模块整体的最小/最大地址范围。
        void FillModuleFromPhdr(dl_phdr_info *info, ModuleInfo &out)
        {
            uintptr_t minAddr = UINTPTR_MAX;
            uintptr_t maxAddr = 0;
            out.Segments.clear();

            for (int i = 0; i < info->dlpi_phnum; ++i)
            {
                const ElfW(Phdr) &ph = info->dlpi_phdr[i];
                // 只关注可加载段（PT_LOAD），跳过 NOTE/GNU_STACK 等
                if (ph.p_type != PT_LOAD || ph.p_memsz == 0)
                    continue;

                auto segBase =
                    reinterpret_cast<unsigned char *>(info->dlpi_addr + ph.p_vaddr);
                size_t segSize = static_cast<size_t>(ph.p_memsz);
                out.Segments.push_back({segBase, segSize});

                uintptr_t start = reinterpret_cast<uintptr_t>(segBase);
                uintptr_t end = start + segSize;
                if (start < minAddr)
                    minAddr = start;
                if (end > maxAddr)
                    maxAddr = end;
            }

            if (minAddr != UINTPTR_MAX && maxAddr > minAddr)
            {
                out.Base = reinterpret_cast<unsigned char *>(minAddr);
                out.Size = static_cast<size_t>(maxAddr - minAddr);
            }
        }

        // 比较已加载模块路径与目标模块名是否匹配（基于 basename）。
        bool ModuleNameMatches(const char *loadedPath, const char *wantName)
        {
            if (!loadedPath || !loadedPath[0] || !wantName || !wantName[0])
                return false;
            return std::strcmp(PathBasename(loadedPath), PathBasename(wantName)) == 0;
        }

        // dl_iterate_phdr 回调上下文：按模块名查找。
        struct NameSearchContext
        {
            const char *Target = nullptr;
            ModuleInfo Result;
        };

        int NameSearchCallback(dl_phdr_info *info, size_t /*size*/, void *data)
        {
            auto *ctx = static_cast<NameSearchContext *>(data);
            if (!ModuleNameMatches(info->dlpi_name, ctx->Target))
                return 0;
            FillModuleFromPhdr(info, ctx->Result);
            // 找到匹配模块即停止遍历
            return ctx->Result ? 1 : 0;
        }

        // dl_iterate_phdr 回调上下文：按地址查找所属模块。
        struct AddressSearchContext
        {
            uintptr_t Needle = 0;
            ModuleInfo Result;
        };

        int AddressSearchCallback(dl_phdr_info *info, size_t /*size*/, void *data)
        {
            auto *ctx = static_cast<AddressSearchContext *>(data);
            for (int i = 0; i < info->dlpi_phnum; ++i)
            {
                const ElfW(Phdr) &ph = info->dlpi_phdr[i];
                if (ph.p_type != PT_LOAD || ph.p_memsz == 0)
                    continue;

                uintptr_t start = info->dlpi_addr + ph.p_vaddr;
                uintptr_t end = start + ph.p_memsz;
                // 地址落在段范围内即为所属模块
                if (ctx->Needle >= start && ctx->Needle < end)
                {
                    FillModuleFromPhdr(info, ctx->Result);
                    return ctx->Result ? 1 : 0;
                }
            }
            return 0;
        }
#endif
    } // namespace

    bool LoadGamedata(const char *path, nlohmann::json &out)
    {
        if (!path || !path[0])
            return false;

        std::ifstream ifs(path, std::ios::binary);
        if (!ifs.is_open())
            return false;

        try
        {
            out = nlohmann::json::parse(ifs);
        }
        catch (...)
        {
            // JSON 解析异常（格式错误、编码问题等）
            return false;
        }
        return out.is_object();
    }

    const char *PlatformName()
    {
#if defined(_WIN32)
        return "windows";
#else
        return "linux";
#endif
    }

    std::string FindPlatformSig(const nlohmann::json &gamedata, const std::string &name)
    {
        auto entryIt = gamedata.find(name);
        if (entryIt == gamedata.end() || !entryIt->is_object())
            return "";

        auto sigIt = entryIt->find("signatures");
        if (sigIt == entryIt->end() || !sigIt->is_object())
            return "";

        auto platIt = sigIt->find(PlatformName());
        if (platIt == sigIt->end() || !platIt->is_string())
            return "";

        return platIt->get<std::string>();
    }

    int FindPlatformOffset(const nlohmann::json &gamedata, const std::string &name, int fallback)
    {
        auto entryIt = gamedata.find(name);
        if (entryIt == gamedata.end() || !entryIt->is_object())
            return fallback;

        auto offIt = entryIt->find("offsets");
        if (offIt == entryIt->end() || !offIt->is_object())
            return fallback;

        auto platIt = offIt->find(PlatformName());
        if (platIt == offIt->end() || !platIt->is_number_integer())
            return fallback;

        return platIt->get<int>();
    }

    bool ParseSigString(const std::string &sigStr,
                        std::vector<uint8_t> &outBytes,
                        std::vector<bool> &outWild)
    {
        outBytes.clear();
        outWild.clear();

        const char *p = sigStr.c_str();
        while (*p)
        {
            // 跳过空格分隔符
            if (*p == ' ')
            {
                ++p;
                continue;
            }

            // 通配符：?? 或 ?
            if (*p == '?')
            {
                outBytes.push_back(0);
                outWild.push_back(true);
                ++p;
                if (*p == '?')
                    ++p;
                continue;
            }

            // 十六进制字节：1-2 个字符
            char *endPtr = nullptr;
            unsigned long val = std::strtoul(p, &endPtr, 16);
            // 解析失败或字符过多或超出字节范围
            if (endPtr == p || endPtr - p > 2 || val > 0xFF)
                return false;

            outBytes.push_back(static_cast<uint8_t>(val));
            outWild.push_back(false);
            p = endPtr;
        }

        return !outBytes.empty();
    }

    void *FindPatternIn(const ModuleInfo &module,
                        const std::vector<uint8_t> &pattern,
                        const std::vector<bool> &wild)
    {
        if (!module || pattern.empty() || pattern.size() != wild.size())
            return nullptr;

        const size_t patLen = pattern.size();

        // 逐段扫描：签名通常在可执行段中，遍历所有段以确保覆盖
        for (const ModuleSegment &segment : module.Segments)
        {
            if (!segment.Base || segment.Size < patLen)
                continue;

            // 在段内滑动窗口匹配
            for (size_t i = 0; i + patLen <= segment.Size; ++i)
            {
                bool matched = true;
                for (size_t j = 0; j < patLen; ++j)
                {
                    // 通配符位置跳过比较
                    if (wild[j])
                        continue;
                    if (segment.Base[i + j] != pattern[j])
                    {
                        matched = false;
                        break;
                    }
                }
                if (matched)
                    return segment.Base + i;
            }
        }
        return nullptr;
    }

    ModuleInfo ModuleFromName(const char *moduleName)
    {
        if (!moduleName || !moduleName[0])
            return {};

#if defined(_WIN32)
        // Windows：GetModuleHandleA 返回已加载模块的句柄（不增加引用计数）
        return BuildModuleFromHandle(GetModuleHandleA(moduleName));
#else
        NameSearchContext ctx{};
        ctx.Target = moduleName;
        dl_iterate_phdr(NameSearchCallback, &ctx);
        return ctx.Result;
#endif
    }

    ModuleInfo ModuleFromInterfacePtr(void *interfacePtr)
    {
        if (!interfacePtr)
            return {};

        // 接口对象首成员是 vtable 指针，通过它反查所属模块
        void *vtable = nullptr;
        if (!Mem::SafeRead(interfacePtr, 0, vtable) || !vtable)
            return {};

#if defined(_WIN32)
        // Windows：VirtualQuery 查询 vtable 所在内存区域，
        // AllocationBase 即为模块基址（HMODULE）。
        MEMORY_BASIC_INFORMATION mbi{};
        if (!VirtualQuery(vtable, &mbi, sizeof(mbi)))
            return {};
        // 仅处理内存映射镜像（DLL/SO），排除堆分配等
        if (mbi.Type != MEM_IMAGE)
            return {};
        return BuildModuleFromHandle(reinterpret_cast<HMODULE>(mbi.AllocationBase));
#else
        // Linux：遍历 phdr 查找包含 vtable 地址的模块
        AddressSearchContext ctx{};
        ctx.Needle = reinterpret_cast<uintptr_t>(vtable);
        dl_iterate_phdr(AddressSearchCallback, &ctx);
        return ctx.Result;
#endif
    }

    void *ResolveSig(const nlohmann::json &gamedata, const ModuleInfo &module,
                     const char *name, char *errorOut, size_t errorOutLen)
    {
        if (!name)
        {
            WriteError(errorOut, errorOutLen, "%s", "ResolveSig: null name");
            return nullptr;
        }

        // 1. 从 gamedata 读取当前平台签名
        std::string sig = FindPlatformSig(gamedata, name);
        if (sig.empty())
        {
            // 签名为空可能是 Linux 通过符号导出定位（如 CCSBot::Update），
            // Task 3 可改用 dlsym 方式；此处返回 nullptr 并提示。
            WriteError(errorOut, errorOutLen,
                       "gamedata missing '%s.signatures.%s'", name, PlatformName());
            return nullptr;
        }

        // 2. 解析签名字符串为字节序列 + 通配符掩码
        std::vector<uint8_t> bytes;
        std::vector<bool> wild;
        if (!ParseSigString(sig, bytes, wild))
        {
            WriteError(errorOut, errorOutLen,
                       "failed to parse sig '%s': '%s'", name, sig.c_str());
            return nullptr;
        }

        // 3. 在模块内查找模式
        void *addr = FindPatternIn(module, bytes, wild);
        if (!addr)
        {
            WriteError(errorOut, errorOutLen,
                       "signature '%s' not found in target module", name);
            return nullptr;
        }

        return addr;
    }
}
