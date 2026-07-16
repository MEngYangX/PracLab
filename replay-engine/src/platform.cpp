// 跨平台调试输出与模块路径实现。
// 通过函数地址反查所属模块，避免依赖硬编码路径。

#include "platform.h"

#if defined(_WIN32)
#include <Windows.h>
#else
#include <dlfcn.h>
#include <cstdio>
#endif

namespace PracLab::ReplayEngine::Platform
{
    // 输出调试信息到平台调试通道。
    // Windows 使用 OutputDebugStringA；Linux 输出到 stderr。
    void DebugOut(const char *msg)
    {
#if defined(_WIN32)
        OutputDebugStringA(msg);
#else
        // Linux 无 OutputDebugString 等价物，输出到 stderr 方便调试
        std::fprintf(stderr, "%s", msg);
#endif
    }

    // 解析本模块在磁盘上的绝对路径。
    // 通过 SelfModulePath 函数自身的地址反查所属共享库路径。
    std::string SelfModulePath()
    {
#if defined(_WIN32)
        HMODULE mod = nullptr;
        // GET_MODULE_HANDLE_EX_FLAG_FROM_ADDRESS: 用函数地址定位模块句柄
        // GET_MODULE_HANDLE_EX_FLAG_UNCHANGED_REFCOUNT: 不增加引用计数
        if (!GetModuleHandleExA(
                GET_MODULE_HANDLE_EX_FLAG_FROM_ADDRESS |
                    GET_MODULE_HANDLE_EX_FLAG_UNCHANGED_REFCOUNT,
                reinterpret_cast<LPCSTR>(&SelfModulePath), &mod))
            return "";
        char path[MAX_PATH] = {0};
        if (GetModuleFileNameA(mod, path, MAX_PATH) == 0)
            return "";
        return std::string(path);
#else
        // dladdr 通过函数地址获取所属共享库文件名
        Dl_info info{};
        if (dladdr(reinterpret_cast<void *>(&SelfModulePath), &info) == 0 ||
            !info.dli_fname)
            return "";
        return std::string(info.dli_fname);
#endif
    }
}

