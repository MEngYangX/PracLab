// 安全内存读写实现。
// Windows 使用 SEH 捕获访问违例，Linux 直接 memcpy（假设地址有效）。

#include "memory.h"

#if defined(_WIN32)
#ifndef WIN32_LEAN_AND_MEAN
#define WIN32_LEAN_AND_MEAN
#endif
#ifndef NOMINMAX
#define NOMINMAX
#endif
#include <Windows.h>
#endif

namespace PracLab::ReplayEngine::Mem
{
    // 检查目标地址范围是否合法。
    // base: 起始指针；offset: 偏移；size: 期望访问的字节数。
    // 拒绝：
    //   - 空指针
    //   - 负偏移
    //   - 零长度
    //   - 地址低于 0x10000（null 页区域，常见野指针）
    //   - 加法溢出
    static bool IsAddressRangeValid(const void *base, int offset, size_t size)
    {
        if (!base || offset < 0 || size == 0)
            return false;

        const auto baseAddr = reinterpret_cast<uintptr_t>(base);
        const auto addr = baseAddr + static_cast<uintptr_t>(offset);

        // 防止 offset 导致回绕到低于基址的地址
        if (addr < baseAddr)
            return false;

        // null 页保护：低于 0x10000 的地址几乎必然是野指针
        if (addr < 0x10000u)
            return false;

        // 检查 [addr, addr+size) 是否溢出
        const auto end = addr + size;
        if (end < addr)
            return false;

        return true;
    }

    bool TryReadMemory(const void *base, int offset, void *out, size_t size)
    {
        if (!out)
            return false;
        if (!IsAddressRangeValid(base, offset, size))
            return false;

        const auto addr =
            reinterpret_cast<const void *>(
                reinterpret_cast<uintptr_t>(base) + static_cast<uintptr_t>(offset));

#if defined(_WIN32)
        // SEH 捕获访问违例（EXCEPTION_ACCESS_VIOLATION 等），
        // 避免无效引擎指针导致整个服务器进程崩溃。
        __try
        {
            std::memcpy(out, addr, size);
        }
        __except (EXCEPTION_EXECUTE_HANDLER)
        {
            return false;
        }
#else
        // Linux 无 SEH 机制，假设地址有效；
        // 地址合法性已由 IsAddressRangeValid 预检。
        std::memcpy(out, addr, size);
#endif
        return true;
    }

    bool TryWriteMemory(void *base, int offset, const void *value, size_t size)
    {
        if (!value)
            return false;
        if (!IsAddressRangeValid(base, offset, size))
            return false;

        const auto addr =
            reinterpret_cast<void *>(
                reinterpret_cast<uintptr_t>(base) + static_cast<uintptr_t>(offset));

#if defined(_WIN32)
        __try
        {
            std::memcpy(addr, value, size);
        }
        __except (EXCEPTION_EXECUTE_HANDLER)
        {
            return false;
        }
#else
        std::memcpy(addr, value, size);
#endif
        return true;
    }
}
