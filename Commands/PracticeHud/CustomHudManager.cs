using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Modules.Extensions;

namespace PracLab;

/// <summary>
/// CCSCustomHudLayout 实体生命周期与 HUD 推送管理：
/// - EnsureLayout：任一玩家开启模块时用 CreateEntityByName("custom_hud_layout") + StrLayout + DispatchSpawn
///   创建全局共享实体（HudText 验证模式，无需 gamedata 签名）；
/// - ClearLayout：全部玩家模块关闭（或地图切换/插件卸载）时销毁实体；
/// - PushDialogVariable / PushClass 直调 CSSharp 内置 CCSCustomHudLayoutExtensions（底层为
///   NativeAPI，C++ 侧 GlobalClass + SourceHook 实现，跨平台由框架负责），配合会话差量缓存仅推送变化值。
/// </summary>
internal sealed class CustomHudManager
{
    /// <summary>实体 designer name。</summary>
    private const string EntityDesignerName = "custom_hud_layout";

    /// <summary>VXML 布局资源路径。</summary>
    private const string LayoutResource = "panorama/layout/custom_game/prac_hud.xml";

    /// <summary>HUD 根面板 ID。</summary>
    public const string RootPanelId = "prac-hud";

    /// <summary>隐藏用 CSS class 名。</summary>
    public const string HiddenClass = "hidden";

    /// <summary>当前布局实体（全局共享单例，全部模块关闭时销毁）。</summary>
    private CCSCustomHudLayout? _layoutEntity;

    /// <summary>实体创建失败标志：失败后不再重试，直到地图切换重置。</summary>
    private bool _creationFailed;

    /// <summary>上次 EnsureLayout 是否重建了实体（重建后调用方应失效全部会话差量缓存）。</summary>
    public bool LayoutWasRecreated { get; private set; }

    /// <summary>
    /// HUD 功能是否就绪（布局实体已创建且有效）。实时求值：实体随地图销毁后自动变为不可用。
    /// </summary>
    public bool IsAvailable => _layoutEntity is { IsValid: true };

    /// <summary>
    /// 确保布局实体存在。已存在且有效则直接返回；创建失败置失败标志（不再重试）。
    /// </summary>
    /// <returns>true = 实体可用（含已存在），false = 创建失败（已打印日志）。</returns>
    public bool EnsureLayout()
    {
        Server.PrintToConsole("[PracLab] CustomHudManager.EnsureLayout: executing...");

        LayoutWasRecreated = false;

        if (_layoutEntity is { IsValid: true })
            return true;

        if (_creationFailed)
            return false;

        try
        {
            var entity = Utilities.CreateEntityByName<CCSCustomHudLayout>(EntityDesignerName);
            if (entity == null)
            {
                Server.PrintToConsole($"[PracLab] {DateTime.Now:HH:mm:ss} Error PracticeHud CreateEntityByName({EntityDesignerName}) returned null");
                _creationFailed = true;
                return false;
            }

            // HudText 验证模式：spawn 前设置 StrLayout 属性，无参 DispatchSpawn
            entity.StrLayout = LayoutResource;
            entity.DispatchSpawn();

            if (!entity.IsValid)
            {
                Server.PrintToConsole($"[PracLab] {DateTime.Now:HH:mm:ss} Error PracticeHud layout entity invalid after spawn");
                _creationFailed = true;
                return false;
            }

            _layoutEntity = entity;
            LayoutWasRecreated = true;
            Server.PrintToConsole($"[PracLab] {DateTime.Now:HH:mm:ss} PracticeHud layout entity spawned: index={entity.Index} layout={LayoutResource}");
            return true;
        }
        catch (Exception ex)
        {
            Server.PrintToConsole($"[PracLab] {DateTime.Now:HH:mm:ss} Error PracticeHud EnsureLayout failed - {ex.Message}");
            _creationFailed = true;
            return false;
        }
    }

    /// <summary>
    /// 销毁布局实体（全部玩家模块关闭 / 插件卸载时调用）。实体无效时仅清引用。
    /// </summary>
    public void ClearLayout()
    {
        Server.PrintToConsole("[PracLab] CustomHudManager.ClearLayout: executing...");

        var entity = _layoutEntity;
        _layoutEntity = null;

        if (entity == null || !entity.IsValid)
            return;

        try
        {
            entity.Remove();
            Server.PrintToConsole($"[PracLab] {DateTime.Now:HH:mm:ss} PracticeHud layout entity removed");
        }
        catch (Exception ex)
        {
            Server.PrintToConsole($"[PracLab] {DateTime.Now:HH:mm:ss} Warning PracticeHud ClearLayout failed - {ex.Message}");
        }
    }

    /// <summary>
    /// 地图切换重置：实体随地图销毁，仅清引用与失败标志（下次开启模块时重建）。
    /// </summary>
    public void ResetForMapChange()
    {
        _layoutEntity = null;
        _creationFailed = false;
        Server.PrintToConsole($"[PracLab] {DateTime.Now:HH:mm:ss} PracticeHud layout state reset for map change");
    }

    /// <summary>
    /// 对单个玩家差量推送面板 dialog variable 字符串（值未变化不调用原生函数）。
    /// 实体或玩家无效时直接跳过（不更新差量缓存，实体恢复后会全量重推）。
    /// </summary>
    /// <param name="session">目标玩家会话（提供差量缓存）。</param>
    /// <param name="panelId">面板 ID。</param>
    /// <param name="variableName">变量名。</param>
    /// <param name="value">待推送值。</param>
    public void PushDialogVariable(PracticeHudSession session, string panelId, string variableName, string value)
    {
        var entity = _layoutEntity;
        if (entity is not { IsValid: true })
            return;

        var player = Utilities.GetPlayerFromSlot(session.Slot);
        if (player == null || !player.IsValid)
            return;

        if (!session.ShouldPushDialogVariable(panelId, variableName, value))
            return;

        try
        {
            CCSCustomHudLayoutExtensions.SetDialogVariableStringForPlayer(entity, player, panelId, variableName, value);
        }
        catch (Exception ex)
        {
            // 推送失败（如实体竞态销毁）不中断采样，仅记录
            Server.PrintToConsole($"[PracLab] {DateTime.Now:HH:mm:ss} Warning PracticeHud PushDialogVariable failed slot={session.Slot} - {ex.Message}");
        }
    }

    /// <summary>
    /// 对单个玩家差量设置面板 CSS class（状态未变化不调用原生函数）。
    /// 实体或玩家无效时直接跳过（不更新差量缓存，实体恢复后会全量重推）。
    /// </summary>
    /// <param name="session">目标玩家会话。</param>
    /// <param name="panelId">面板 ID。</param>
    /// <param name="className">class 名。</param>
    /// <param name="hasClass">true = 应用（隐藏），false = 移除（显示）。</param>
    public void PushClass(PracticeHudSession session, string panelId, string className, bool hasClass)
    {
        var entity = _layoutEntity;
        if (entity is not { IsValid: true })
            return;

        var player = Utilities.GetPlayerFromSlot(session.Slot);
        if (player == null || !player.IsValid)
            return;

        if (!session.ShouldPushClass(panelId, className, hasClass))
            return;

        try
        {
            CCSCustomHudLayoutExtensions.SetHasClassForPlayer(entity, player, panelId, className, hasClass);
        }
        catch (Exception ex)
        {
            Server.PrintToConsole($"[PracLab] {DateTime.Now:HH:mm:ss} Warning PracticeHud PushClass failed slot={session.Slot} - {ex.Message}");
        }
    }
}
