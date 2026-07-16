// CS2 运行时 PlayerCommand 内存布局。
// PlayerCommand 同时继承引擎宿主块（CUserCmdBase）与 protobuf 消息（CSGOUserCmdPB），
// 编译器自动计算多重继承的指针调整，因此 reinterpret_cast 即可在 detour 中获取正确偏移。
//
// 依赖：protobuf 编译生成的 cs_usercmd.pb.h（CMakeLists.txt 中 PROTO 模块生成）。
// sizeof 校验：Linux=144, Windows=152（受 vptr 指针对齐影响）。

#pragma once

#include <cstdint>

// 由 protoc 在 ${GENERATED_DIR} 下生成
#include "cs_usercmd.pb.h"

namespace PracLab::ReplayEngine
{
    // 引擎按钮状态块（3 个 uint64 状态字）。
    // 保留 vtable 占位以匹配引擎实际布局；不要调用虚函数。
    class CInButtonState
    {
        virtual void Schema_DynamicBinding_Unused() {}

    public:
        uint64_t m_pButtonStates[3];
    };

    // 宿主块：vptr + cmdNum + 填充。
    class CUserCmdBase
    {
    public:
        int cmdNum;
        uint8_t unk[4];

        virtual ~CUserCmdBase();

    private:
        // 占位的引擎虚函数槽（具体语义未公开，仅维持布局）
        virtual void unk0();
        virtual void unk1();
        virtual void unk2();
        virtual void unk3();
        virtual void unk4();
        virtual void unk5();
        virtual void unk6();
    };

    // 将 protobuf 消息作为基类引入，而非成员。
    // 这样编译器计算多继承偏移，PlayerCommand* 与 CSGOUserCmdPB* 之间可以正确调整。
    template <typename T>
    class CUserCmdBaseHost : public CUserCmdBase, public T
    {
    };

    // 标准 CUserCmd：宿主块 + protobuf 消息。
    class CUserCmd : public CUserCmdBaseHost<CSGOUserCmdPB>
    {
    };

    // 扩展：CUserCmd + 按钮状态 + 未知字段。
    class CUserCmdExtended : public CUserCmd
    {
    public:
        CInButtonState buttonstates;
        uint32_t unknown;
    };

    // 顶层 PlayerCommand：包含 flags 与链表指针。
    // reinterpret_cast<CPlayerCmd*>(engineCmdPtr) 即可访问全部字段。
    class PlayerCommand : public CUserCmdExtended
    {
    public:
        uint32_t flags;
        PlayerCommand *unknowncmd;
        PlayerCommand *parentcmd;
    };

// 布局校验：早期发现引擎结构变化导致的偏移漂移。
#ifndef _WIN32
    static_assert(sizeof(PlayerCommand) == 144, "PlayerCommand size mismatch (Linux expected 144)");
#else
    static_assert(sizeof(PlayerCommand) == 152, "PlayerCommand size mismatch (Windows expected 152)");
#endif
}
