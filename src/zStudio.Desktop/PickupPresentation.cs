using System.Windows.Media;
using System.Windows.Media.Imaging;
using zStudio.Core;

namespace zStudio.Desktop;

/// <summary>
/// Pickup catalog presentation shared by source-map rows and their MCP description: the group a type is listed under, its
/// picture (a weapon shows the weapon, as the game's manual renders it; ammo, health and technology their HUD icons) and its
/// names (the manual's, and the short form rows use).
/// </summary>
internal sealed record PickupPresentation(int Index, string Type, string Kind, string IconFile, string? WeaponIconFile, string ShortName, string WeaponName)
{
    /// <summary>The group under Pickups: weapons, ammo or other (health and technology).</summary>
    internal string Group => Kind switch { "weapon" => "weapons", "ammo" => "ammo", _ => "other" };
    internal bool IsWeapon => WeaponIconFile != null;
    /// <summary>The manual's name; ammo is its weapon's name with " ammo".</summary>
    internal string ManualName => Kind == "ammo" ? WeaponName + " ammo" : WeaponName;
    internal string TemplateName => "pu" + Index.ToString("D3", System.Globalization.CultureInfo.InvariantCulture);
    internal ImageSource Image => Images[WeaponIconFile ?? IconFile].Value;
    /// <summary>A picture's tooltip: "Napalm Cannon · weapon", "Napalm Cannon ammo", "Nanite 100 · health".</summary>
    internal string PictureToolTip => Kind == "ammo" ? ManualName : $"{ManualName} · {Kind}";

    internal static PickupPresentation? For(string? type) => type == null ? null : Types.GetValueOrDefault(type);
    internal static string GroupLabel(string group) => group switch { "weapons" => "Weapons", "ammo" => "Ammo", _ => "Other" };
    /// <summary>"weapon pickups", "ammo pickups", "health and technology pickups".</summary>
    internal static string GroupPickups(string group) => group switch { "weapons" => "weapon pickups", "ammo" => "ammo pickups", _ => "health and technology pickups" };
    internal static readonly string[] Groups = ["weapons", "ammo", "other"];

    // Names from the game's manual (RECOIL's docs); rows use a short form that puts what tells similar types apart first. The four mine
    // launchers share one render: a plain mine is high explosive, Distortion is molecular distortion (the owner's names).
    private static readonly (string Short, string Manual)[] Weapons =
    [
        ("ERFPG", "Enhanced Rapid Fire Pulse Gun"), ("HE Mortar", "High Explosive Mortar Cannon"), ("MD Mortar", "Molecular Distortion Mortar Cannon"),
        ("Freon", "Freon Cannon"), ("Napalm", "Napalm Cannon"),
        ("Proximity Mine", "Proximity High Explosive Mine Launcher"), ("Distortion Mine", "Proximity Molecular Distortion Mine Launcher"),
        ("Remote Mine", "Remote High Explosive Mine Launcher"), ("Remote Distortion Mine", "Remote Molecular Distortion Mine Launcher"),
        ("Laser Designator", "Laser Designator"), ("Laser Sabre", "Laser Sabre"), ("Sonic Burst", "Sonic Burst Cannon"), ("Arc Sabre", "Arc Sabre"),
        ("Missile", "Lock On Missile Launcher"), ("Tether Missile", "Tether Guided Missile Launcher"), ("Nuke", "Lock On Nuke Launcher"), ("Tether Nuke", "Tether Guided Nuke Launcher"),
    ];
    private static readonly (string Short, string Manual)[] Others =
        [("Nanite", "Nanite"), ("Nanite 100", "Nanite 100"), ("Nano-Canister", "Nano-Canister"), ("Amphib Tech", "Amphib Technology"), ("Hover Tech", "Hover Technology"), ("Sub Tech", "Sub Technology")];

    // HUD primary/secondary artwork for ammo, keyed by the engine's pickup catalog rather than generated instance names.
    // ERFPG is weapon 1.1 (red); the green RFPG icon is the ordinary weapon, which has no pickup type.
    private static PickupPresentation? Describe(MissionPickupType type)
    {
        string? icon = type.Index switch
        {
            0 or 17 => "ic_rfpg2", 1 or 18 => "ic_mort1", 2 or 19 => "ic_mort2",
            3 or 20 => "ic_nplm1", 4 or 21 => "ic_nplm2",
            5 or 22 => "ic_mina1", 6 or 23 => "ic_mina2",
            7 or 24 => "ic_rmin1", 8 or 25 => "ic_rmin2",
            9 or 26 => "ic_lazr1", 10 or 27 => "ic_lazr2",
            11 or 28 => "ic_sonc1", 12 or 29 => "ic_arc1",
            13 or 30 => "ic_miss1", 14 or 31 => "ic_miss2",
            15 or 32 => "ic_nuke1", 16 or 33 => "ic_nuke2",
            34 => "nanocan", 35 => "nanite100", 36 => "nanite",
            37 => "amphchip", 38 => "hovchp01", 39 => "subchp01", _ => null,
        };
        // The manual's weapon renders (the HUD's gun_01–gun_13): both mortars share one, as do the four mine launchers.
        string? weapon = type.Index switch
        {
            17 => "gun_01", 18 or 19 => "gun_02", 20 => "gun_04", 21 => "gun_05", >= 22 and <= 25 => "gun_03",
            26 => "gun_06", 27 => "gun_07", 28 => "gun_09", 29 => "gun_08", 30 => "gun_10", 31 => "gun_11", 32 => "gun_12", 33 => "gun_13", _ => null,
        };
        if (icon == null) return null;
        var (shortName, manual) = type.Index < 34 ? Weapons[type.Index % 17] : Others[type.Index - 34];
        string kind = type.Index < 17 ? "ammo" : type.Index < 34 ? "weapon" : type.Index < 37 ? "health" : "technology";
        return new(type.Index, type.Name, kind, icon + ".png", weapon == null ? null : "Weapons/" + weapon + ".png", shortName, manual);
    }

    private static readonly IReadOnlyDictionary<string, PickupPresentation> Types = MissionPickupType.Catalog
        .Select(Describe).OfType<PickupPresentation>().ToDictionary(p => p.Type, StringComparer.Ordinal);
    private static readonly IReadOnlyDictionary<string, Lazy<ImageSource>> Images = Types.Values.Select(p => p.IconFile)
        .Concat(Types.Values.Select(p => p.WeaponIconFile).OfType<string>())
        .Distinct(StringComparer.Ordinal).ToDictionary(file => file, file => new Lazy<ImageSource>(() =>
        {
            BitmapImage image = new();
            image.BeginInit();
            image.CacheOption = BitmapCacheOption.OnLoad;
            image.UriSource = new Uri($"pack://application:,,,/zStudio.Desktop;component/Assets/Pickups/{file}", UriKind.Absolute);
            image.EndInit();
            image.Freeze();
            return image;
        }), StringComparer.Ordinal);
}
