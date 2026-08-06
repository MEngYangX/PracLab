using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Modules.Commands;

namespace PracLab;

/// <summary>
/// 按键绑定命令：F3（autobuy）→ .timer，F4（rebuy）→ .help。
/// 仅在 prac 模式下拦截并转调对应处理器；非 prac 模式放行原生购买行为。
/// CS2 客户端固定将 F3 绑定到 autobuy、F4 绑定到 rebuy，因此通过拦截这两条客户端命令实现按键重映射。
/// </summary>
public partial class PracLab
{
    /// <summary>
    /// 拦截 F3（autobuy）按键：prac 模式下转调 HandleTimer，非 prac 模式放行。
    /// </summary>
    /// <param name="player">调用者玩家。</param>
    /// <param name="command">命令信息（未使用）。</param>
    /// <returns>Handled 表示拦截，Continue 表示放行原生 autobuy。</returns>
    private HookResult OnAutobuyPressed(CCSPlayerController? player, CommandInfo command)
    {
        // 控制台或非 prac 模式放行原生 autobuy
        if (player == null || !player.IsValid)
            return HookResult.Continue;

        if (!_isPracMode)
            return HookResult.Continue;

        try
        {
            HandleTimer(player, string.Empty);
        }
        catch (Exception ex)
        {
            Server.PrintToConsole($"[PracLab] {DateTime.Now:HH:mm:ss} Error OnAutobuyPressed failed - {ex.Message}");
        }

        return HookResult.Handled;
    }

    /// <summary>
    /// 拦截 F4（rebuy）按键：prac 模式下转调 HandleHelp，非 prac 模式放行。
    /// </summary>
    /// <param name="player">调用者玩家。</param>
    /// <param name="command">命令信息（未使用）。</param>
    /// <returns>Handled 表示拦截，Continue 表示放行原生 rebuy。</returns>
    private HookResult OnRebuyPressed(CCSPlayerController? player, CommandInfo command)
    {
        // 控制台或非 prac 模式放行原生 rebuy
        if (player == null || !player.IsValid)
            return HookResult.Continue;

        if (!_isPracMode)
            return HookResult.Continue;

        try
        {
            HandleHelp(player, string.Empty);
        }
        catch (Exception ex)
        {
            Server.PrintToConsole($"[PracLab] {DateTime.Now:HH:mm:ss} Error OnRebuyPressed failed - {ex.Message}");
        }

        return HookResult.Handled;
    }
}
