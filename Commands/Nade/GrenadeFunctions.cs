using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Modules.Memory.DynamicFunctions;
using System.Runtime.InteropServices;

namespace PracLab;

/// <summary>
/// 投掷物引擎内部创建函数（CreateFunc）。
/// 直接从 MatchZy GrenadeProjectiles.cs 移植。
/// Bug 2 根因：Utilities.CreateEntityByName 创建的投掷物是"空壳"，
/// 即使设置 InitialPosition/InitialVelocity/Thrower 等属性，引信也不会启动。
/// 必须通过 MemoryFunctionWithReturn 调用引擎内部的 CreateFunc，
/// 这些函数会正确初始化投掷物的所有内部状态（引信计时器、爆炸逻辑等）。
/// </summary>
public static class GrenadeFunctions
{
    public static bool IsLinux => RuntimeInformation.IsOSPlatform(OSPlatform.Linux);

    // Bug 2 修复（2026-07 游戏更新）：Linux 旧签名失效（命中 2 个错误函数）。
    // 新签名从 libserver.so (2026-07-16, ServerVersion 2000876) 提取，唯一匹配函数起始 0x1408d80。
    public static MemoryFunctionWithReturn<IntPtr, IntPtr, IntPtr, IntPtr, IntPtr, int, int, CSmokeGrenadeProjectile>
        CSmokeGrenadeProjectile_CreateFunc = new(
            IsLinux
                ? @"55 4C 89 C1 48 89 E5 41 57 49 89 FF 41 56 45 89 CE"
                : @"48 8B C4 48 89 58 ? 48 89 68 ? 48 89 70 ? 57 41 56 41 57 48 81 EC ? ? ? ? 48 8B B4 24 ? ? ? ? 4D 8B F8"
        );

    // Bug 2 修复（2026-07 游戏更新）：旧 Windows 签名失效（栈帧 sub rsp,0x50 → 0x40）。
    // Windows 新签名从 server.dll (2026-07-17) 提取，唯一匹配函数起始 0x1803896a0，
    // 函数模式与 CSmokeGrenadeProjectile::Create 一致（lea "<nade>_projectile" → call 实体工厂）。
    // Linux 旧签名同步失效（0 匹配），新签名从 libserver.so (2026-07-16) 提取，唯一匹配函数起始 0xd17860。
    public static MemoryFunctionWithReturn<IntPtr, IntPtr, IntPtr, IntPtr, IntPtr, int, CHEGrenadeProjectile>
        CHEGrenadeProjectile_CreateFunc = new(
            IsLinux
                ? "55 4C 89 C1 48 89 E5 41 57 49 89 FF 41 56 49 89 D6"
                : "48 89 5C 24 08 48 89 6C 24 10 48 89 74 24 18 57 48 83 EC 40 48 8B 6C 24 70 49 8B F8"
        );

    public static MemoryFunctionWithReturn<IntPtr, IntPtr, IntPtr, IntPtr, IntPtr, int, CMolotovProjectile>
        CMolotovProjectile_CreateFunc = new(
            IsLinux
                ? "55 48 8D 05 ? ? ? ? 48 89 E5 41 57 41 56 41 55 41 54 49 89 FC 53 48 81 EC ? ? ? ? 4C 8D 35"
                : "48 8B C4 48 89 58 10 4C 89 40 18 48 89 48 08"
        );

    // Bug 2 修复（2026-07 游戏更新）：旧 Windows 签名失效（栈帧 sub rsp,0x168 → 0x158）。
    // Windows 新签名从 server.dll (2026-07-17) 提取，唯一匹配函数起始 0x1809567b0。
    // Linux 旧签名同步失效（0 匹配），新签名从 libserver.so (2026-07-16) 提取，唯一匹配函数起始 0x1407fe0。
    public static MemoryFunctionWithReturn<IntPtr, IntPtr, IntPtr, IntPtr, IntPtr, int, CDecoyProjectile>
        CDecoyProjectile_CreateFunc = new(
            IsLinux
                ? "55 4C 89 C1 48 89 E5 41 57 45 89 CF 41 56 49 89 FE 41 55 49 89 D5 48 89 F2 48 89 FE 41 54 48 8D 3D ?? ?? ?? ?? 4D 89 C4 53 48 83 EC 48"
                : "48 8B C4 55 56 48 81 EC 58 01 00 00 48 89 58 08 48 8B D9 48 89 78 10 49 8B F8 4C 89 78 E8"
        );
}
