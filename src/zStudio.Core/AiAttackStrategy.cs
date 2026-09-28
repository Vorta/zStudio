using System.Text.Json.Nodes;

namespace Recoil.Zbd.Core;

public enum AiAttackStrategyState { Missing, Stored, Invalid }
public enum AiAttackStrategyKind { Unknown, HEA, CIR, BAC, FOL, ZIG, SIT }

/// <summary>Authored network metadata, independent of gameplay defaults and rendering.</summary>
public sealed record AiAttackStrategy
{
    public string? Value { get; }
    public AiAttackStrategyState State { get; }
    public AiAttackStrategyKind Kind { get; }
    private AiAttackStrategy(string? value, AiAttackStrategyState state)
    {
        Value = value; State = state;
        // Retail 0x403367-0x40342E uppercases and compares the first three
        // characters. Do not trim, infer a runtime default, or rewrite source text.
        Kind = value is { Length: >= 3 } ? value[..3].ToUpperInvariant() switch
        {
            "HEA" => AiAttackStrategyKind.HEA, "CIR" => AiAttackStrategyKind.CIR,
            "BAC" => AiAttackStrategyKind.BAC, "FOL" => AiAttackStrategyKind.FOL,
            "ZIG" => AiAttackStrategyKind.ZIG, "SIT" => AiAttackStrategyKind.SIT,
            _ => AiAttackStrategyKind.Unknown
        } : AiAttackStrategyKind.Unknown;
    }
    public static AiAttackStrategy Missing { get; } = new(null, AiAttackStrategyState.Missing);
    public static AiAttackStrategy Invalid { get; } = new(null, AiAttackStrategyState.Invalid);
    public static AiAttackStrategy Stored(string value) => new(value, AiAttackStrategyState.Stored);
    public string? BoundedValue(int limit) => Value is { } value && value.Length > limit ? value[..limit] + "… [truncated]" : Value;
    public JsonObject Describe() => new()
    {
        ["value"] = BoundedValue(4096), ["status"] = State.ToString().ToLowerInvariant(),
        ["key"] = Kind == AiAttackStrategyKind.Unknown ? "unknown" : Kind.ToString(),
        ["characters"] = Value?.Length, ["truncated"] = Value?.Length > 4096
    };
}
