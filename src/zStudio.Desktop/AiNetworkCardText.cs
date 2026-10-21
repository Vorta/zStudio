using zStudio.Core;

namespace zStudio.Desktop;

/// <summary>
/// What the AI network card says about each header field: what it does in play (docs/agents/engine-evidence.md#net_nnzrd),
/// following the type and attack strategy chosen in the draft, and the game's value when it is not stored. The card and its
/// MCP draft report show the same lines.
/// </summary>
internal static class AiNetworkCardText
{
    /// <summary>The type the game runs: it reads the first two letters (ST, HI, FI, DE); anything else is standard.</summary>
    internal static string GameType(string? stored) => stored is { Length: >= 2 } ? stored[..2].ToUpperInvariant() switch
    {
        "HI" => "hide", "FI" => "fixed", "DE" => "defilade", _ => "standard",
    } : "standard";
    internal static string TypeName(string type) => type switch { "hide" => "Hide", "fixed" => "Fixed", "defilade" => "Defilade", _ => "Standard" };
    /// <summary>The strategy the game runs: it reads the first three letters; missing or unknown attacks head-on.</summary>
    internal static AiAttackStrategyKind GameStrategy(string? stored) =>
        stored != null && AiAttackStrategy.Stored(stored).Kind is var kind and not AiAttackStrategyKind.Unknown ? kind : AiAttackStrategyKind.HEA;
    internal static string StrategyName(AiAttackStrategyKind kind) => OutlinerKinds.StrategyName(kind);
    internal static string StrategyName(string value) => StrategyName(AiAttackStrategy.Stored(value).Kind);

    internal static string TypeHelp(string type) => type switch
    {
        "hide" => "Stays where it is placed and faces the player; the retail game runs it as Fixed.",
        "fixed" => "Stays where it is placed and faces the player.",
        "defilade" => "Drives from its home node to the next, waits 4 s, backs home, waits 14 s, repeats.",
        _ => "Drives the network's route; leaves it to attack, then drives back.",
    };
    internal static string StrategyHelp(AiAttackStrategyKind kind) => kind switch
    {
        AiAttackStrategyKind.CIR => "While attacking, circles the player at Pursuit Radius Min.",
        AiAttackStrategyKind.BAC => "While attacking, charges to Pursuit Radius Min, backs out to twice that, and repeats.",
        AiAttackStrategyKind.FOL => "While attacking, keeps driving its route and shoots on the way.",
        AiAttackStrategyKind.ZIG => "Not implemented in the game: an attacking vehicle keeps its last controls.",
        AiAttackStrategyKind.SIT => "While attacking, stops and turns in place to face the player.",
        _ => "While attacking, faces the player and holds between Pursuit Radius Min and Max.",
    };
    /// <summary>A pursuit value's line under a strategy, and whether that strategy ignores it.</summary>
    internal static (string Text, bool Inactive) PursuitHelp(AiAttackStrategyKind kind, int part) => (kind, part) switch
    {
        (AiAttackStrategyKind.HEA, 0) => ("Backs away when the player is nearer than this.", false),
        (AiAttackStrategyKind.HEA, _) => ("Drives in when the player is farther than this.", false),
        (AiAttackStrategyKind.CIR, 0) => ("Circles the player at this distance.", false),
        (AiAttackStrategyKind.BAC, 0) => ("Charges to this distance from the player, then backs out to twice it.", false),
        (AiAttackStrategyKind.BAC, _) => ("How far it swings sideways.", false),
        (_, 0) => ($"Not used by {StrategyName(kind)}. Head-on, Circle and Back and forth use it.", true),
        _ => ($"Not used by {StrategyName(kind)}. Head-on and Back and forth use it.", true),
    };
    internal static string NameHelp(string number) => $"Vehicles find the network by its file number ({number}), not by this name. Up to 19 characters.";
    /// <summary>The line of a field that follows neither the type nor the strategy.</summary>
    internal static string FieldHelp(string key, int part) => (key, part) switch
    {
        ("activate_rad", _) => "Acts only while the player is this close or it is alerted. 0 or not stored: only when alerted.",
        ("attack_rad", _) => "Attacks when it sees the player within this distance. 0 or not stored: about 38.7.",
        ("return_range", _) => "Ends a chase this far from the node it was driving to when it began. 0 or not stored: at once.",
        ("attack_dwell", _) => "Length of each burst of fire. 0 or not stored: 10 s.",
        ("not_pursuit_dwell", _) => "Pause between bursts, and before a new attack once a chase ends. 0 or not stored: no pause.",
        ("attack_buddy", _) => "When a vehicle on this network starts an attack or is hit, every vehicle on it joins in.",
        ("path_width", _) => "Never used. Not stored: 10.",
        ("hide_times", 0) => "Never used; Defilade waits a fixed 4 s and 14 s. Not stored: 8 s.",
        ("hide_times", _) => "Never used. Not stored: 4 s.",
        _ => "",
    };
    internal static string BuddiesActivateHelp(bool stored, bool willStore) =>
        stored ? "Its value is never used, but while it is stored, on or off, the game keeps Buddies Attack."
        : willStore ? "Apply stores it, so the game keeps Buddies Attack."
        : "Its value is never used, but while it is not stored, the game ignores Buddies Attack.";
    internal const string BuddiesIgnored = "Ignored by the game: Buddies Activate is not stored. ";
    internal const string NoEffectInfo = "The retail game never uses these values. They stay in the file and can still be edited.";

    /// <summary>The game's value, shown in an empty box while the field is not stored.</summary>
    internal static string Hint(string key, int part) => (key, part) switch
    {
        ("activate_rad" or "return_range" or "not_pursuit_dwell", _) => "0",
        ("attack_rad", _) => "about 38.7",
        ("attack_dwell" or "path_width", _) => "10",
        ("hide_times", 0) => "8",
        ("hide_times", _) => "4",
        _ => "Not stored",
    };
    /// <summary>Times are seconds (shown in the box); the other numbers are distances in game units.</summary>
    internal static bool Seconds(string key) => key is "attack_dwell" or "not_pursuit_dwell" or "hide_times";
    /// <summary>The fields the retail game never uses.</summary>
    internal static bool NoEffect(string key) => key is "activate_buddy" or "path_width" or "hide_times";
}
