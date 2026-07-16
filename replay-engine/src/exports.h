// C API 导出层内部头文件。
// 本模块实现 include/praclab_replay.h 中声明的 PRL_* 系列 C 函数，
// 将调用委托给 Recorder / Replayer / Hooks 模块。
//
// 本头文件仅供 exports.cpp 内部使用；对外 C API 声明见
// include/praclab_replay.h，不要在此重复声明 PRL_* 函数。

#pragma once

namespace PracLab::ReplayEngine::Exports
{
    // 玩家槽位有效范围上界（与 Recorder/Replayer/Hooks 的 kMaxSlots 一致）。
    // 此处复制常量而非引用 recorder.h，以保持 C ABI 导出层与内部模块解耦，
    // 避免 praclab_replay.h 的使用者被迫引入 recorder.h。
    constexpr int kMaxSlots = 64;
}
