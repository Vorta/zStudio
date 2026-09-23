using System.Globalization;
using System.Numerics;

namespace Recoil.Zbd.Core;

public sealed record MissionResourceSource(string ArchivePath, int AssetIndex, string ResourceName);
public sealed record MissionPickupSource(string ArchivePath, int AssetIndex, string ResourceName, int RecordIndex);
public sealed record MissionPickup(int TypeIndex, string LogicalName, int AuthoredAmount, int EffectiveAmount,
    Vector3 Position, Vector3 Rotation, float RespawnDelay, MissionPickupSource Source);

/// <summary>Retail pickup.cpp g_PickupTypes at 0x4DB6E8. Ordering is an engine contract, not ZRD record order.</summary>
public sealed record MissionPickupType(int Index, string Name, int DefaultAmount)
{
    public string TemplateName => "pu" + Index.ToString("D3", CultureInfo.InvariantCulture);
    public static IReadOnlyList<MissionPickupType> Catalog { get; } = Array.AsReadOnly<MissionPickupType>([
        new(0, "ERFPG_AMMO", 30), new(1, "HEMORTAR_AMMO", 3), new(2, "QMORTAR_AMMO", 3),
        new(3, "FREON_AMMO", 10), new(4, "NAPALM_AMMO", 10), new(5, "P_HEMINE_AMMO", 5),
        new(6, "P_QMINE_AMMO", 5), new(7, "R_HEMINE_AMMO", 5), new(8, "R_QMINE_AMMO", 5),
        new(9, "LOCKON_LASER_AMMO", 10), new(10, "LASER_SABRE_AMMO", 10), new(11, "SONIC_CANNON_AMMO", 2),
        new(12, "ARC_SABRE_AMMO", 10), new(13, "MISSILE_AMMO", 3), new(14, "GUIDED_MISSILE_AMMO", 5),
        new(15, "NUKE_AMMO", 1), new(16, "GUIDED_NUKE_AMMO", 1),
        new(17, "ERFPG_WEAPON", 30), new(18, "HEMORTAR_WEAPON", 3), new(19, "QMORTAR_WEAPON", 3),
        new(20, "FREON_WEAPON", 10), new(21, "NAPALM_WEAPON", 10), new(22, "P_HEMINE_WEAPON", 5),
        new(23, "P_QMINE_WEAPON", 5), new(24, "R_HEMINE_WEAPON", 5), new(25, "R_QMINE_WEAPON", 5),
        new(26, "LOCKON_LASER_WEAPON", 10), new(27, "LASER_SABRE_WEAPON", 10), new(28, "SONIC_CANNON_WEAPON", 2),
        new(29, "ARC_SABRE_WEAPON", 10), new(30, "MISSILE_WEAPON", 3), new(31, "GUIDED_MISSILE_WEAPON", 5),
        new(32, "NUKE_WEAPON", 1), new(33, "GUIDED_NUKE_WEAPON", 1), new(34, "NANITE", 60),
        new(35, "NANITE100", 100), new(36, "NANO-CANISTER", 1), new(37, "PUP_AMPHIB", 1),
        new(38, "PUP_HOVER", 1), new(39, "PUP_SUB", 1)
    ]);
}
