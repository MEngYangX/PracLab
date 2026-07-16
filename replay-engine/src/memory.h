// 安全内存读写工具。
// 引擎对象的指针在版本更新后可能失效，直接 deref 会触发访问违例。
// Windows 使用 SEH (__try/__except) 把访问违例转换为布尔失败；
// Linux 无 SEH，假设传入地址有效，直接 memcpy。
//
// 提供：
//   - TryReadMemory / TryWriteMemory：字节级安全读写
//   - SafeRead<T> / WriteField<T>：trivially-copyable 类型字段读写模板

#pragma once

#include <cstddef>
#include <cstdint>
#include <cstring>
#include <type_traits>

namespace PracLab::ReplayEngine::Mem
{
    // 安全读取引擎内存。
    // base: 起始地址；offset: 相对偏移；out: 输出缓冲区；size: 字节数。
    // 返回 false 表示地址非法或读取触发访问违例。
    bool TryReadMemory(const void *base, int offset, void *out, size_t size);

    // 安全写入引擎内存。
    // base: 起始地址；offset: 相对偏移；value: 待写入数据；size: 字节数。
    // 返回 false 表示地址非法或写入触发访问违例。
    bool TryWriteMemory(void *base, int offset, const void *value, size_t size);

    // 模板：读取 trivially-copyable 字段。
    // 先读取到栈临时变量，全部成功后再发布到 out，避免部分写入脏数据。
    template <typename T>
    bool SafeRead(const void *base, int offset, T &out)
    {
        static_assert(std::is_trivially_copyable_v<T>,
                      "SafeRead requires trivially-copyable T");
        // 对齐缓冲区：防止未对齐访问在部分平台产生异常
        alignas(T) std::byte staging[sizeof(T)]{};
        if (!TryReadMemory(base, offset, staging, sizeof(T)))
            return false;
        std::memcpy(&out, staging, sizeof(T));
        return true;
    }

    // 模板：写入 trivially-copyable 字段到引擎内存。
    template <typename T>
    bool WriteField(void *base, int offset, const T &value)
    {
        static_assert(std::is_trivially_copyable_v<T>,
                      "WriteField requires trivially-copyable T");
        return TryWriteMemory(base, offset, &value, sizeof(T));
    }
}
