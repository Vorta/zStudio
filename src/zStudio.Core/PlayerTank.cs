namespace zStudio.Core;

/// <summary>The player tank's master type: the vehicle resource's mode blocks track, amphib, hover and sub.</summary>
public enum PlayerTankType { Tracked, Amphibious, Hover, Sub }

/// <summary>
/// A weapon of the player tank: its slot in the vehicle resource's weapon list (key <c>wep_B.S</c>, bank B and side S, which
/// <c>LoadWeaponBanksAndSelectDefaults</c> 0x438ba0 reads from the key's characters 4 and 6) and its name in the pickup
/// catalog without <c>_WEAPON</c>. <c>Pickup::ApplyEffect</c> (0x41d220) grants the catalog's weapon types 0x11–0x21 to banks
/// 1.1, 2.0, 2.1 … 9.1 in that order, so the slots' order is the catalog's; RFPG (1.0) has no pickup and keeps its own name.
/// </summary>
public sealed record PlayerTankWeapon(int Bank, int Side, string Id)
{
    public string Key => $"wep_{Bank}.{Side}";
    /// <summary>Bank 1 holds the primary weapons (RFPG, ERFPG); banks 2–9 the secondaries.</summary>
    public bool IsPrimary => Bank == 1;
    public static IReadOnlyList<PlayerTankWeapon> Catalog { get; } = Array.AsReadOnly(Build());
    public static IReadOnlyList<PlayerTankWeapon> Primaries { get; } = Array.AsReadOnly(Catalog.Where(w => w.IsPrimary).ToArray());
    public static IReadOnlyList<PlayerTankWeapon> Secondaries { get; } = Array.AsReadOnly(Catalog.Where(w => !w.IsPrimary).ToArray());
    public static PlayerTankWeapon? Find(string? id) => id == null ? null : Catalog.FirstOrDefault(w => w.Id == id);
    public static PlayerTankWeapon? At(int bank, int side) => Catalog.FirstOrDefault(w => w.Bank == bank && w.Side == side);

    private static PlayerTankWeapon[] Build()
    {
        List<PlayerTankWeapon> weapons = [new(1, 0, "RFPG")];
        for (int slot = 1; slot <= 17; slot++)
        {
            string name = MissionPickupType.Catalog[16 + slot].Name;
            weapons.Add(new(slot / 2 + 1, slot % 2, name[..^"_WEAPON".Length]));
        }
        return [.. weapons];
    }
}

/// <summary>
/// How the previews draw the local player's tank: a preview choice, never written to sources or settings. The default is
/// the game's fresh single-player start (every spawn is tracked; the first and second weapons the mission allows are HE
/// Mortar and RFPG).
/// </summary>
public sealed record PlayerTankState(PlayerTankType Type, string Primary, string Secondary)
{
    public static PlayerTankState Default { get; } = new(PlayerTankType.Tracked, "RFPG", "HEMORTAR");
    public bool IsDefault => Equals(Default);
    /// <summary>Whether the weapons name a primary and a secondary of the catalog.</summary>
    public bool IsValid => PlayerTankWeapon.Find(Primary) is { IsPrimary: true } && PlayerTankWeapon.Find(Secondary) is { IsPrimary: false } && Enum.IsDefined(Type);
}

/// <summary>Whether the map's data takes the tank to a type: the mode-change lists the vehicle resource names and the entries they start.</summary>
/// <param name="Transitions">The animation entries the change plays, in order.</param>
/// <param name="Missing">The entries this map's animations lack, in chain order.</param>
/// <param name="Problem">Why the type is unavailable apart from missing entries (a missing list or mode block, no animations).</param>
public sealed record PlayerTankTypeChoice(PlayerTankType Type, bool Available, IReadOnlyList<string> Transitions, IReadOnlyList<string> Missing, string? Problem);

/// <summary>What a weapon draws on this tank's gun.</summary>
/// <param name="Listed">The vehicle resource lists the weapon for this tank.</param>
/// <param name="Model">The gun model's name: the weapon's <c>weapons.zrd</c> NAME, else its key.</param>
/// <param name="Nodes">The nodes it shows: NAME_L and NAME_R for a dual mount (the primaries), else NAME.</param>
/// <param name="Present">Every node was found under the tank's gun.</param>
/// <param name="AllowedInSub">Neither BEAM nor CATCHES_FIRE: <c>IsAltWeaponAllowedInCurrentMasterMode</c> (0x43c630) allows it in sub.</param>
public sealed record PlayerTankWeaponModel(PlayerTankWeapon Weapon, bool Listed, string? Model, IReadOnlyList<string> Nodes, bool Present, bool AllowedInSub);

/// <summary>The local player's tank in a mission starting scene and how the preview draws it.</summary>
/// <param name="Requested">The state the preview was asked for.</param>
/// <param name="Shown">What it draws: Tracked when the requested type is unavailable or failed, and in sub the secondary the game switches to.</param>
public sealed record MissionPlayerTank(MissionActor Actor, string Template, PlayerTankState Requested, PlayerTankState Shown)
{
    public required IReadOnlyList<PlayerTankTypeChoice> Types { get; init; }
    public required IReadOnlyList<PlayerTankWeaponModel> Weapons { get; init; }
    /// <summary>The gun nodes the preview draws, primary pair first.</summary>
    public IReadOnlyList<string> Drawn { get; init; } = [];
    /// <summary>The tank has a <c>gun</c> node.</summary>
    public bool HasGun { get; init; }
    /// <summary>Why the requested type is not shown although the data offers it (its transition did not run to its end).</summary>
    public string? Failure { get; init; }
    /// <summary>The requested type is no longer offered (an edit or another difficulty removed its transition), so Tracked is shown.</summary>
    public bool TypeWithdrawn { get; init; }
    /// <summary>The requested secondary is not allowed in sub; <see cref="Shown"/> holds the weapon the game switches to.</summary>
    public bool SecondarySubstituted { get; init; }
    /// <summary>The starting scene hides the tank (an opening drop-off sets its root inactive).</summary>
    public bool HiddenAtStart { get; init; }
    public PlayerTankTypeChoice Choice(PlayerTankType type) => Types.First(t => t.Type == type);
    public PlayerTankWeaponModel Model(string id) => Weapons.First(w => w.Weapon.Id == id);
}
