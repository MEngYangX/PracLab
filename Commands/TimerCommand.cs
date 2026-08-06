using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Core.Translations;

namespace PracLab;

/// <summary>
/// 计时器命令：.timer。
/// 开始计时后实时在屏幕中央提示区（CenterHint）显示耗时，停止时输出结果到聊天栏。
/// </summary>
public partial class PracLab
{
    /// <summary>
    /// OnTick 监听器是否已注册（Listeners.OnTick 一旦注册无法注销，用此标志守卫）。
    /// </summary>
    private bool _isTimerTickRegistered;

    /// <summary>
    /// .timer — 开始计时，再次输入时停止并向玩家打印耗时。
    /// 计时中实时在 CenterHint 显示当前耗时。
    /// </summary>
    private void HandleTimer(CCSPlayerController player, string args)
    {
        Server.PrintToConsole("[PracLab] HandleTimer: executing...");
        var steamId = player.SteamID;
        if (_timerState.TryGetValue(steamId, out var startTime))
        {
            // 第二次输入：停止计时并打印耗时到聊天栏
            var elapsed = DateTime.Now - startTime;
            _timerState.Remove(steamId);
            player.PrintToChat(Localizer.ForPlayer(player, "timer.stopped", elapsed.TotalSeconds));
            Server.PrintToConsole($"[PracLab] {DateTime.Now:HH:mm:ss} Timer {player.PlayerName} timer ended: {elapsed.TotalSeconds:F2}s");
        }
        else
        {
            // 第一次输入：开始计时，注册 OnTick 实时刷新 CenterHint
            _timerState[steamId] = DateTime.Now;
            if (!_isTimerTickRegistered)
                RegisterTimerTickListener();
            player.PrintToChat(Localizer.ForPlayer(player, "timer.started"));
            Server.PrintToConsole($"[PracLab] {DateTime.Now:HH:mm:ss} Timer {player.PlayerName} timer started");
        }
    }

    /// <summary>
    /// 注册 OnTick 监听器实时刷新计时 CenterHint（仅注册一次）。
    /// 回调内通过 _timerState.Count == 0 短路返回，避免无计时玩家时空转。
    /// </summary>
    private void RegisterTimerTickListener()
    {
        RegisterListener<Listeners.OnTick>(() =>
        {
            // 短路：无计时中的玩家时直接返回
            if (_timerState.Count == 0) return;

            try
            {
                // 取键快照后迭代，避免 foreach 期间修改字典
                foreach (var steamId in _timerState.Keys)
                {
                    var startTime = _timerState[steamId];
                    var elapsed = (DateTime.Now - startTime).TotalSeconds;

                    // 查找玩家对象
                    CCSPlayerController? player = null;
                    foreach (var p in Utilities.GetPlayers())
                    {
                        if (p.IsValid && p.SteamID == steamId)
                        {
                            player = p;
                            break;
                        }
                    }

                    if (player == null) continue;

                    // 实时显示到屏幕中央提示区
                    player.PrintToCenter(Localizer.ForPlayer(player, "timer.center", elapsed));
                }
            }
            catch (Exception ex)
            {
                Server.PrintToConsole($"[PracLab] {DateTime.Now:HH:mm:ss} Error Timer OnTick failed - {ex.Message}");
            }
        });

        _isTimerTickRegistered = true;
        Server.PrintToConsole($"[PracLab] {DateTime.Now:HH:mm:ss} Timer OnTick listener registered");
    }
}
