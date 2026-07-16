// funchook 跨平台 inline hook 封装。
// 每个 Hook 实例持有一个 funchook_t*，提供 Create/Enable/Remove 三阶段生命周期，
// 语义对齐 MinHook 的 create/enable/remove 用法。
//
// PRL_FASTCALL 宏：
//   Windows x64 调用约定中前两个参数通过 RCX/RDX 传递，与 __fastcall 一致；
//   Linux System V x64 调用约定默认即 RDI/RSI，无需额外修饰。

#pragma once

#include <funchook.h>

// Windows 上 funchook_prepare 的目标函数地址需要保留原始调用约定；
// __fastcall 在 x64 上与默认约定一致，仅为显式标注。
#if defined(_MSC_VER)
#  define PRL_FASTCALL __fastcall
#else
#  define PRL_FASTCALL
#endif

namespace PracLab::ReplayEngine
{
    // 单点 inline hook 封装。
    // 一个实例对应一个被拦截的引擎函数。
    // 生命周期：Create() 准备 trampoline → Enable() 安装 → Remove() 卸载并释放资源。
    class Hook
    {
    public:
        Hook() = default;
        // 析构时自动卸载，避免插件卸载阶段遗漏清理。
        ~Hook() { Remove(); }

        Hook(const Hook &) = delete;
        Hook &operator=(const Hook &) = delete;
        Hook(Hook &&) = delete;
        Hook &operator=(Hook &&) = delete;

        // 准备 hook：将 *orig 重写为 trampoline 入口。
        // target:  被拦截的引擎函数地址（来自 SigScan）
        // detour:  自定义 detour 函数指针
        // orig:    输出参数，接受 trampoline 入口（调用原函数用）
        // 返回 true 表示成功，可以继续 Enable()。
        bool Create(void *target, void *detour, void **orig)
        {
            if (m_fh || !target || !detour || !orig)
                return false;

            m_fh = funchook_create();
            if (!m_fh)
                return false;

            // funchook_prepare 会读取 *orig 作为目标地址，并写回 trampoline 入口。
            *orig = target;
            int rc = funchook_prepare(m_fh, orig, detour);
            if (rc != FUNCHOOK_ERROR_SUCCESS)
            {
                funchook_destroy(m_fh);
                m_fh = nullptr;
                return false;
            }
            return true;
        }

        // 安装已准备好的 hook。
        // 重复调用安全：已启用时直接返回 false。
        bool Enable()
        {
            if (!m_fh || m_enabled)
                return false;
            if (funchook_install(m_fh, 0) != FUNCHOOK_ERROR_SUCCESS)
                return false;
            m_enabled = true;
            return true;
        }

        // 卸载并销毁 hook。对未激活实例调用安全（no-op）。
        void Remove()
        {
            if (!m_fh)
                return;
            if (m_enabled)
            {
                // 卸载时引擎必须不在被拦截函数内部执行，否则可能崩溃。
                funchook_uninstall(m_fh, 0);
                m_enabled = false;
            }
            funchook_destroy(m_fh);
            m_fh = nullptr;
        }

        // 当前 hook 是否处于已安装状态。
        bool Active() const { return m_enabled; }

    private:
        funchook_t *m_fh = nullptr;
        bool m_enabled = false;
    };
}
