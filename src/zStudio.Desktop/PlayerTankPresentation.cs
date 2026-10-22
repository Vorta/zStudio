using System.Globalization;
using zStudio.Core;

namespace zStudio.Desktop;

/// <summary>
/// The player tank's preview state as the Properties card, the status bar and MCP name it: types, weapons, notes and reasons.
/// The weapon names are the pickup rows' (<see cref="PickupPresentation"/>); RFPG, which has no pickup, has its own.
/// </summary>
internal static class PlayerTankPresentation
{
    internal const string PreviewToolTip = "Preview only. This changes how the view draws the player tank. It is not saved, has no undo step, and Apply and Revert leave it alone. In play the game decides the type and weapons.";
    internal const string RebuildingToolTip = "Available when the world has been rebuilt.";

    internal static readonly PlayerTankType[] Types = [PlayerTankType.Tracked, PlayerTankType.Amphibious, PlayerTankType.Hover, PlayerTankType.Sub];
    /// <summary>The MCP value of a type.</summary>
    internal static string Id(PlayerTankType type) => type.ToString().ToLowerInvariant();
    internal static PlayerTankType? Parse(string id) => Types.Cast<PlayerTankType?>().FirstOrDefault(t => Id(t!.Value) == id);
    internal static string Label(PlayerTankType type) => type.ToString();
    internal static string TypeToolTip(PlayerTankType type, MissionPlayerTank? tank)
    {
        if (type == PlayerTankType.Tracked) return "Tracked: the tank as the mission starts.";
        var choice = tank?.Types.FirstOrDefault(t => t.Type == type);
        string chain = choice is { Transitions.Count: > 0 } ? string.Join(", then ", choice.Transitions) : "its transitions";
        return $"{Label(type)}: the end of {chain}.";
    }

    /// <summary>The short name the radios, summaries and status use ("HE Mortar"; RFPG and ERFPG as such).</summary>
    internal static string ShortName(string id) => id == "RFPG" ? "RFPG" : PickupPresentation.For(id + "_WEAPON")?.ShortName ?? id;
    /// <summary>The game manual's name ("High Explosive Mortar Cannon").</summary>
    internal static string ManualName(string id) => id == "RFPG" ? "Rapid Fire Pulse Gun" : PickupPresentation.For(id + "_WEAPON")?.WeaponName ?? id;
    /// <summary>The manual's render, or null (RFPG has none).</summary>
    internal static System.Windows.Media.ImageSource? Render(string id) => PickupPresentation.For(id + "_WEAPON") is { IsWeapon: true } pickup ? pickup.Image : null;

    internal static string Summary(PlayerTankState state) => $"{Label(state.Type)} · {ShortName(state.Primary)} · {ShortName(state.Secondary)}";
    internal static string WeaponsSummary(PlayerTankState state) => $"{ShortName(state.Primary)} · {ShortName(state.Secondary)}";
    internal static string StatusText(PlayerTankState state) => $"Player tank: {Label(state.Type)}, {ShortName(state.Primary)}, {ShortName(state.Secondary)}";

    internal static string WeaponToolTip(MissionPlayerTank? tank, PlayerTankWeapon weapon)
    {
        var model = tank?.Weapons.FirstOrDefault(w => w.Weapon == weapon);
        string models = model is not { Listed: true, Model: not null } ? "no gun model on this tank" : model.Nodes.Count > 1
            ? $"gun models {string.Join(" and ", model.Nodes)}" : $"gun model {model.Nodes[0]}";
        return weapon.IsPrimary ? $"{weapon.Id}: {ManualName(weapon.Id)} · {models}" : $"{ManualName(weapon.Id)} · {models}";
    }

    /// <summary>The second line of the secondary readout: the gun model it draws, what shares it, or that nothing is drawn.</summary>
    internal static string ModelLine(MissionPlayerTank tank, string id)
    {
        var model = tank.Model(id);
        if (!model.Listed || model.Model == null) return $"This tank has no {ShortName(id)}, so no secondary weapon is drawn.";
        if (!model.Present) return $"This tank's gun has no {model.Model} model, so no secondary weapon is drawn.";
        var shared = SharedWith(tank, id);
        return shared.Count == 0 ? $"Gun model {model.Model}" : $"Gun model {model.Model}, shared with {Join(shared)}";
    }
    /// <summary>The other secondaries of this tank that draw the same gun model, in slot order.</summary>
    internal static IReadOnlyList<string> SharedWith(MissionPlayerTank tank, string id)
    {
        var model = tank.Model(id);
        if (!model.Listed || model.Model == null) return [];
        return [.. tank.Weapons.Where(w => !w.Weapon.IsPrimary && w.Weapon.Id != id && w.Listed && w.Model == model.Model).Select(w => ShortName(w.Weapon.Id))];
    }
    private static string Join(IReadOnlyList<string> names) => names.Count == 1 ? names[0] : string.Join(", ", names.Take(names.Count - 1)) + " and " + names[^1];

    /// <summary>Why a type is unavailable in this map ("m1's tank has no transition to hover (none_to_hover)."), or null.</summary>
    internal static string? Reason(string mission, MissionPlayerTank tank, PlayerTankType type)
    {
        var choice = tank.Choice(type);
        if (choice.Available) return null;
        string name = Id(type);
        return choice.Missing.Count > 0 ? $"{mission}'s tank has no transition to {name} ({choice.Missing[^1]})."
            : $"{mission}'s tank cannot become {name}: {choice.Problem}.";
    }
    /// <summary>A disabled type's tooltip ("Not in m1: its tank has no transition to amphibious (none_to_amphib)."), or null.</summary>
    internal static string? DisabledToolTip(string mission, MissionPlayerTank tank, PlayerTankType type)
    {
        var choice = tank.Choice(type);
        if (choice.Available) return null;
        return choice.Missing.Count > 0 ? $"Not in {mission}: its tank has no transition to {Id(type)} ({choice.Missing[^1]})." : $"Not in {mission}: {choice.Problem}.";
    }
    /// <summary>A disabled sub-mode secondary's reason.</summary>
    internal const string SubForbidden = "Not in sub: the game allows no beam or fire weapon in sub mode, and switches to another weapon.";

    /// <summary>
    /// The constant note under Master type: what this map's data offers, then the game's rules for a fresh start of this
    /// mission number (docs/agents/engine-evidence.md, missions: single player amphibious from 3, hover from 4, sub in 6 only;
    /// online amphibious only in 9, 11, 12 and 13).
    /// </summary>
    internal static string Note(string mission, MissionPlayerTank tank)
    {
        var missing = Types.Skip(1).Where(t => !tank.Choice(t).Available).Select(t => Id(t)).ToArray();
        string data = missing.Length == 3 ? $"{mission}'s tank has only its tracked form: it has no transitions to amphibious, hover or sub."
            : missing.Length > 0 ? $"{mission}'s tank has no {string.Join(" or ", missing)} transitions."
            : "Each type shows the end of this mission's transition, with the tank parked.";
        int number = mission.Length > 1 && int.TryParse(mission.AsSpan(1), NumberStyles.None, CultureInfo.InvariantCulture, out int n) ? n : 0;
        bool offered(PlayerTankType type) => tank.Choice(type).Available;
        List<string> rules = [];
        if (number is >= 1 and <= 6)
        {
            if (number < 3 && offered(PlayerTankType.Amphibious)) rules.Add("amphibious from mission 3");
            if (number < 4 && offered(PlayerTankType.Hover)) rules.Add("hover from mission 4");
            if (number < 6 && offered(PlayerTankType.Sub)) rules.Add("sub in mission 6 only");
        }
        string rule = rules.Count > 0 ? $" In single player the game allows {Join(rules)}." :
            number > 6 && number is not (9 or 11 or 12 or 13) && offered(PlayerTankType.Amphibious) ? " In multiplayer the game allows amphibious only in m9, m11, m12 and m13." : "";
        return data + rule;
    }
    internal const string HiddenNote = "The mission's opening hides the tank at the start (its drop-off), so the view does not draw it.";
}
