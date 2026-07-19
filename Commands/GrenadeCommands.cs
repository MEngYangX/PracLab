using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Core.Translations;
using CounterStrikeSharp.API.Modules.Utils;

namespace PracLab;

/// <summary>
/// 道具重投与位置回溯命令：.rethrow / .rethrowsmoke / .rethrownade 等 7 条。
/// Fix 8+9：原实现使用 EventGrenadeThrown 记录且 SpawnGrenadeAtRecord 不赋予初速度，
/// 导致投掷物原地掉落，弹道与原始投掷完全不同。
/// 现改用 OnEntitySpawned（EventHandlers.cs）记录位置/角度/速度/类名，
/// 重投时通过 Teleport(pos, ang, velocity) 完整复现弹道。
/// </summary>
public partial class PracLab
{
    /// <summary>
    /// .rethrow / .rt — 重新投掷玩家最后投掷的任意道具（按记录的类型与速度）。
    /// Fix 8+9：从 GrenadeThrowRecord 读取 VelX/VelY/VelZ 作为初速度，完整复现弹道。
    /// </summary>
    private void HandleRethrow(CCSPlayerController player, string args)
    {
        Server.PrintToConsole("[PracLab] HandleRethrow: executing...");
        var steamId = player.SteamID;
        if (!_lastGrenadeThrow.TryGetValue(steamId, out var record))
        {
            player.PrintToChat(Localizer.ForPlayer(player, "rethrow.no_record"));
            Server.PrintToConsole($"[PracLab] {DateTime.Now:HH:mm:ss} Rethrow {player.PlayerName} no last throw record");
            return;
        }

        // 使用记录中的投掷物类名（而非反查武器名），保证与原投掷一致
        SpawnGrenadeAtRecord(player, record.ProjectileClass, record);
    }

    /// <summary>
    /// .rethrowsmoke / .rethrows — 仅重投最后投掷的烟雾弹。
    /// 若最后投掷的不是 smoke 则提示类型不匹配。
    /// </summary>
    private void HandleRethrowSmoke(CCSPlayerController player, string args)
    {
        Server.PrintToConsole("[PracLab] HandleRethrowSmoke: executing...");
        if (!_lastGrenadeThrow.TryGetValue(player.SteamID, out var record))
        {
            player.PrintToChat(Localizer.ForPlayer(player, "rethrow.no_record"));
            return;
        }
        if (record.ProjectileClass != "smokegrenade_projectile")
        {
            player.PrintToChat(Localizer.ForPlayer(player, "rethrow.type_mismatch", "smoke"));
            Server.PrintToConsole($"[PracLab] {DateTime.Now:HH:mm:ss} RethrowSmoke {player.PlayerName} last throw is {record.ProjectileClass}, type mismatch");
            return;
        }
        SpawnGrenadeAtRecord(player, "smokegrenade_projectile", record);
    }

    /// <summary>
    /// .rethrownade / .rethrown — 仅重投最后投掷的手雷。
    /// </summary>
    private void HandleRethrowNade(CCSPlayerController player, string args)
    {
        Server.PrintToConsole("[PracLab] HandleRethrowNade: executing...");
        if (!_lastGrenadeThrow.TryGetValue(player.SteamID, out var record))
        {
            player.PrintToChat(Localizer.ForPlayer(player, "rethrow.no_record"));
            return;
        }
        if (record.ProjectileClass != "hegrenade_projectile")
        {
            player.PrintToChat(Localizer.ForPlayer(player, "rethrow.type_mismatch", "HE"));
            Server.PrintToConsole($"[PracLab] {DateTime.Now:HH:mm:ss} RethrowNade {player.PlayerName} last throw is {record.ProjectileClass}, type mismatch");
            return;
        }
        SpawnGrenadeAtRecord(player, "hegrenade_projectile", record);
    }

    /// <summary>
    /// .rethrowflash / .rethrowf — 仅重投最后投掷的闪光弹。
    /// </summary>
    private void HandleRethrowFlash(CCSPlayerController player, string args)
    {
        Server.PrintToConsole("[PracLab] HandleRethrowFlash: executing...");
        if (!_lastGrenadeThrow.TryGetValue(player.SteamID, out var record))
        {
            player.PrintToChat(Localizer.ForPlayer(player, "rethrow.no_record"));
            return;
        }
        if (record.ProjectileClass != "flashbang_projectile")
        {
            player.PrintToChat(Localizer.ForPlayer(player, "rethrow.type_mismatch", "flash"));
            Server.PrintToConsole($"[PracLab] {DateTime.Now:HH:mm:ss} RethrowFlash {player.PlayerName} last throw is {record.ProjectileClass}, type mismatch");
            return;
        }
        SpawnGrenadeAtRecord(player, "flashbang_projectile", record);
    }

    /// <summary>
    /// .rethrowmolotov / .rethrowm — 仅重投最后投掷的燃烧瓶（含 CT incgrenade）。
    /// </summary>
    private void HandleRethrowMolotov(CCSPlayerController player, string args)
    {
        Server.PrintToConsole("[PracLab] HandleRethrowMolotov: executing...");
        if (!_lastGrenadeThrow.TryGetValue(player.SteamID, out var record))
        {
            player.PrintToChat(Localizer.ForPlayer(player, "rethrow.no_record"));
            return;
        }
        // molotov 和 incgrenade 都视为燃烧瓶类型
        if (record.ProjectileClass != "molotov_projectile" && record.ProjectileClass != "incgrenade_projectile")
        {
            player.PrintToChat(Localizer.ForPlayer(player, "rethrow.type_mismatch", "molotov"));
            Server.PrintToConsole($"[PracLab] {DateTime.Now:HH:mm:ss} RethrowMolotov {player.PlayerName} last throw is {record.ProjectileClass}, type mismatch");
            return;
        }
        SpawnGrenadeAtRecord(player, record.ProjectileClass, record);
    }

    /// <summary>
    /// .rethrowdecoy / .rethrowd — 仅重投最后投掷的诱饵弹。
    /// </summary>
    private void HandleRethrowDecoy(CCSPlayerController player, string args)
    {
        Server.PrintToConsole("[PracLab] HandleRethrowDecoy: executing...");
        if (!_lastGrenadeThrow.TryGetValue(player.SteamID, out var record))
        {
            player.PrintToChat(Localizer.ForPlayer(player, "rethrow.no_record"));
            return;
        }
        if (record.ProjectileClass != "decoy_projectile")
        {
            player.PrintToChat(Localizer.ForPlayer(player, "rethrow.type_mismatch", "decoy"));
            Server.PrintToConsole($"[PracLab] {DateTime.Now:HH:mm:ss} RethrowDecoy {player.PlayerName} last throw is {record.ProjectileClass}, type mismatch");
            return;
        }
        SpawnGrenadeAtRecord(player, "decoy_projectile", record);
    }

    /// <summary>
    /// .last / .ls — 传送回最后投掷道具的位置。
    /// </summary>
    private void HandleLast(CCSPlayerController player, string args)
    {
        Server.PrintToConsole("[PracLab] HandleLast: executing...");
        var steamId = player.SteamID;
        if (!_lastGrenadeThrow.TryGetValue(steamId, out var record))
        {
            player.PrintToChat(Localizer.ForPlayer(player, "rethrow.no_record"));
            Server.PrintToConsole($"[PracLab] {DateTime.Now:HH:mm:ss} Last {player.PlayerName} no last throw record");
            return;
        }

        var position = new Vector(record.PosX, record.PosY, record.PosZ);
        var angle = new QAngle(record.AngX, record.AngY, record.AngZ);
        if (TeleportPlayerTo(player, position, angle))
        {
            player.PrintToChat(Localizer.ForPlayer(player, "rethrow.teleported_last"));
            Server.PrintToConsole($"[PracLab] {DateTime.Now:HH:mm:ss} Last {player.PlayerName} teleported to last throw position");
        }
    }

    /// <summary>
    /// 在记录的最后投掷位置生成指定类型的投掷物实体并赋予原始速度。
    /// Fix 8+9：原实现用 new Vector(0, 0, 0)，导致投掷物原地掉落。
    /// 现从 GrenadeThrowRecord 读取 VelX/VelY/VelZ，通过 Teleport 第三参数传入，完整复现弹道。
    /// </summary>
    /// <param name="player">玩家（用于打印提示）。</param>
    /// <param name="projectileClass">投掷物实体类名（如 smokegrenade_projectile）。</param>
    /// <param name="record">可选的投掷记录；为 null 时使用玩家当前位置与 0 速度（仅 .rethrow 调用）。</param>
    private void SpawnGrenadeAtRecord(CCSPlayerController player, string projectileClass, GrenadeThrowRecord? record = null)
    {
        Vector position;
        QAngle? angle;
        Vector velocity;

        if (record.HasValue)
        {
            var r = record.Value;
            position = new Vector(r.PosX, r.PosY, r.PosZ);
            angle = new QAngle(r.AngX, r.AngY, r.AngZ);
            velocity = new Vector(r.VelX, r.VelY, r.VelZ);
        }
        else
        {
            // 无记录时使用玩家当前位置与 0 速度（仅作为兜底，正常路径都传 record）
            var pawn = player.PlayerPawn.Value;
            if (pawn == null || !pawn.IsValid || pawn.AbsOrigin == null)
            {
                player.PrintToChat(Localizer.ForPlayer(player, "rethrow.no_position"));
                return;
            }
            position = pawn.AbsOrigin;
            angle = pawn.AbsRotation;
            velocity = new Vector(0, 0, 0);
        }

        try
        {
            // Bug 2 关键修复：通过引擎内部 CreateFunc 创建投掷物（引信会自动启动）
            // CreateFunc 依赖签名特征码扫描，游戏更新可能导致签名失效
            // 如果 CreateFunc 失败（Invalid function pointer），fallback 到 CreateEntityByName
            var ang = angle ?? new QAngle(0, 0, 0);
            var itemIndex = record.HasValue ? record.Value.ItemIndex : (ushort)0;
            CBaseCSGrenadeProjectile? projectile = null;

            try
            {
                switch (projectileClass)
                {
                    case "smokegrenade_projectile":
                        projectile = GrenadeFunctions.CSmokeGrenadeProjectile_CreateFunc.Invoke(
                            position.Handle, ang.Handle, velocity.Handle, velocity.Handle, IntPtr.Zero, itemIndex, player.TeamNum);
                        break;
                    case "hegrenade_projectile":
                        projectile = GrenadeFunctions.CHEGrenadeProjectile_CreateFunc.Invoke(
                            position.Handle, ang.Handle, velocity.Handle, velocity.Handle, IntPtr.Zero, itemIndex);
                        break;
                    case "molotov_projectile":
                    case "incgrenade_projectile":
                        projectile = GrenadeFunctions.CMolotovProjectile_CreateFunc.Invoke(
                            position.Handle, ang.Handle, velocity.Handle, velocity.Handle, IntPtr.Zero, itemIndex);
                        break;
                    case "decoy_projectile":
                        projectile = GrenadeFunctions.CDecoyProjectile_CreateFunc.Invoke(
                            position.Handle, ang.Handle, velocity.Handle, velocity.Handle, IntPtr.Zero, itemIndex);
                        break;
                    case "flashbang_projectile":
                        // Flash 使用 Utilities.CreateEntityByName（与 MatchZy 一致），DispatchSpawn 会启动引信
                        projectile = Utilities.CreateEntityByName<CFlashbangProjectile>("flashbang_projectile");
                        if (projectile != null) projectile.DispatchSpawn();
                        break;
                    default:
                        projectile = Utilities.CreateEntityByName<CBaseCSGrenadeProjectile>(projectileClass);
                        if (projectile != null) projectile.DispatchSpawn();
                        break;
                }
            }
            catch (Exception createEx)
            {
                // CreateFunc 失败（签名特征码过期），fallback 到 CreateEntityByName
                Server.PrintToConsole($"[PracLab] {DateTime.Now:HH:mm:ss} Warning CreateFunc for {projectileClass} failed - {createEx.Message}, falling back to CreateEntityByName");
                projectile = Utilities.CreateEntityByName<CBaseCSGrenadeProjectile>(projectileClass);
                if (projectile != null) projectile.DispatchSpawn();
            }

            if (projectile == null || !projectile.IsValid)
            {
                player.PrintToChat(Localizer.ForPlayer(player, "rethrow.spawn_failed", projectileClass));
                Server.PrintToConsole($"[PracLab] {DateTime.Now:HH:mm:ss} Warning failed to create projectile entity: {projectileClass}");
                return;
            }

            // 对非 smoke 投掷物设置关键属性（参考 MatchZy Throw() 第 107-127 行）
            // smoke 的 CreateFunc 已处理这些属性，无需重复设置
            if (projectile.DesignerName != "smokegrenade_projectile")
            {
                projectile.InitialPosition.X = position.X;
                projectile.InitialPosition.Y = position.Y;
                projectile.InitialPosition.Z = position.Z;

                projectile.InitialVelocity.X = velocity.X;
                projectile.InitialVelocity.Y = velocity.Y;
                projectile.InitialVelocity.Z = velocity.Z;

                projectile.AngVelocity.X = velocity.X;
                projectile.AngVelocity.Y = velocity.Y;
                projectile.AngVelocity.Z = velocity.Z;

                projectile.TeamNum = player.TeamNum;

                // 修复 Thrower：player.PlayerPawn 是 CHandle<CCSPlayerPawn>，.Raw 返回 EHandle 编码值
                // 旧代码用 (uint)playerPawn.Handle 返回原生指针，不是 EHandle，导致引信无法追踪投掷者
                var pawnRaw = player.PlayerPawn.Raw;
                projectile.Thrower.Raw = pawnRaw;
                projectile.OriginalThrower.Raw = pawnRaw;
                projectile.OwnerEntity.Raw = pawnRaw;

                projectile.Teleport(position, ang, velocity);
            }

            // Bug 5 配套：标记为自定义实体，避免被 OnEntitySpawned 重复记录
            projectile.Globalname = "custom";

            player.PrintToChat(Localizer.ForPlayer(player, "rethrow.thrown", projectileClass));
            Server.PrintToConsole($"[PracLab] {DateTime.Now:HH:mm:ss} Rethrow {player.PlayerName} spawned {projectileClass} velocity=({velocity.X:F1},{velocity.Y:F1},{velocity.Z:F1})");
        }
        catch (Exception ex)
        {
            Server.PrintToConsole($"[PracLab] {DateTime.Now:HH:mm:ss} Error spawn projectile failed - {ex}");
            player.PrintToChat(Localizer.ForPlayer(player, "command.error", ex.Message));
        }
    }

    /// <summary>
    /// 将武器名（weapon_smokegrenade 等）映射为投掷物实体类名。
    /// 保留供未来扩展（如 .savenade 加载阵容时根据武器名生成实体）。
    /// 与 EventHandlers.MapProjectileToWeapon 互为逆映射。
    /// </summary>
    private static string MapWeaponToProjectile(string weapon) => weapon switch
    {
        "weapon_smokegrenade" => "smokegrenade_projectile",
        "weapon_flashbang" => "flashbang_projectile",
        "weapon_hegrenade" => "hegrenade_projectile",
        "weapon_molotov" => "molotov_projectile",
        "weapon_incgrenade" => "incgrenade_projectile",
        "weapon_decoy" => "decoy_projectile",
        _ => "smokegrenade_projectile",
    };
}
