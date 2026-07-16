// 跨平台调试输出与模块路径工具。
// Windows: OutputDebugStringA 输出到调试器。
// Linux: fprintf(stderr) 输出到服务器控制台。

#pragma once

#include <string>

namespace PracLab::ReplayEngine::Platform
{
    // 输出调试信息到平台调试通道。
    // Windows: OutputDebugStringA；Linux: fprintf(stderr)。
    void DebugOut(const char *msg);

    // 获取当前共享库（本插件 DLL/SO）在磁盘上的绝对路径。
    // 用于定位同目录下的 gamedata.json。
    // 失败时返回空字符串。
    std::string SelfModulePath();
}

