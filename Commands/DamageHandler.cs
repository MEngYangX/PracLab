using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Core.Translations;
using CounterStrikeSharp.API.Modules.Events;

namespace PracLab;

/// <summary>
/// 实时伤害信息显示。
/// Bug 4：玩家攻击玩家或 bot 时，向攻击者显示伤害详情。
/// 用户要求格式："你对 <名称> 造成 <攻击血量> 伤害 [剩余血量 <剩余血量>]"。
/// 参考 MEngZy/MatchZy MatchZy.cs 第 414-438 行：注册 EventPlayerHurt，
/// 在 prac 模式下读取 DmgHealth 与 Health 并 PrintToChat。
/// 与 MatchZy 区别：MatchZy 仅对 victim.IsBot 显示，本实现按用户要求对玩家与 bot 均显示。
/// 注意：事件在 PracLab.Load 中通过 RegisterEventHandler 注册，此处不再使用 [GameEventHandler] 特性，
/// 避免与手动注册冲突导致伤害信息打印两次。
/// </summary>
public partial class PracLab
{
    /// <summary>
    /// EventPlayerHurt 事件处理：向攻击者实时显示伤害信息。
    /// 仅在 prac 模式下处理；仅处理攻击者为真实玩家且受害者与攻击者非同一玩家的情况。
    /// </summary>
    /// <param name="event">player_hurt 事件对象。</param>
    /// <param name="info">事件元信息。</param>
    /// <returns>HookResult.Continue（不阻断后续处理器）。</returns>
    public HookResult OnPlayerHurt(EventPlayerHurt @event, GameEventInfo info)
    {
        // 仅在 prac 模式下显示伤害信息（避免影响正常比赛流程）
        if (!_isPracMode) return HookResult.Continue;

        try
        {
            var attacker = @event.Attacker;
            var victim = @event.Userid;

            // 校验攻击者与受害者有效，且不是自伤
            if (attacker == null || !attacker.IsValid) return HookResult.Continue;
            if (victim == null || !victim.IsValid) return HookResult.Continue;
            if (attacker == victim) return HookResult.Continue;

            // 仅对真实玩家攻击者显示（bot 攻击不向其显示，bot 无 PrintToChat 意义）
            if (attacker.IsBot || attacker.IsHLTV) return HookResult.Continue;
            if (!attacker.UserId.HasValue) return HookResult.Continue;

            var damage = @event.DmgHealth;
            var remainingHealth = @event.Health;
            var victimName = victim.PlayerName;

            // 读取剩余血量：@event.Health 在致命一击时可能为 0 或负值，统一夹取到 0
            if (remainingHealth < 0) remainingHealth = 0;

            attacker.PrintToChat(Localizer.ForPlayer(attacker, "damage.info", victimName, damage, remainingHealth));
            Server.PrintToConsole($"[PracLab] {DateTime.Now:HH:mm:ss} Damage {attacker.PlayerName} -> {victimName} dmg={damage} hp={remainingHealth}");
        }
        catch (Exception ex)
        {
            Server.PrintToConsole($"[PracLab] {DateTime.Now:HH:mm:ss} Error OnPlayerHurt failed - {ex.Message}");
        }

        return HookResult.Continue;
    }
}
