using HelixToolkit.Maths;
using Recoil.Zbd.Core;

namespace Recoil.Zbd.Rendering;

/// <summary>One presentation palette shared by the renderer, GUI legend and MCP.</summary>
public static class AiNetworkColors
{
    private static readonly (AiAttackStrategyKind Kind, uint Rgb, string Name)[] Palette =
    [
        (AiAttackStrategyKind.HEA, 0xFFA640, "Orange"), (AiAttackStrategyKind.CIR, 0x33D9FF, "Cyan"),
        (AiAttackStrategyKind.BAC, 0xB380FF, "Purple"), (AiAttackStrategyKind.FOL, 0x4DFFA6, "Green"),
        (AiAttackStrategyKind.ZIG, 0xFF66B3, "Pink"), (AiAttackStrategyKind.SIT, 0xF2F24D, "Yellow"),
        (AiAttackStrategyKind.Unknown, 0xA0A0A0, "Gray")
    ];
    private const uint MissingRgb = 0xFF6666;
    private static uint Rgb(AiAttackStrategy strategy) => strategy.State == AiAttackStrategyState.Missing
        ? MissingRgb : Palette.FirstOrDefault(p => p.Kind == strategy.Kind, Palette[^1]).Rgb;
    public static string Hex(AiAttackStrategy strategy) => $"#{Rgb(strategy):X6}";
    public static Color4 Color(AiAttackStrategy strategy)
    {
        uint rgb = Rgb(strategy);
        return new(((rgb >> 16) & 255) / 255f, ((rgb >> 8) & 255) / 255f, (rgb & 255) / 255f, 1);
    }
    public static string Legend { get; } = "Nodes and arrows use attack-strategy colors:\n" + string.Join("\n", Palette.Select(p =>
        (p.Kind == AiAttackStrategyKind.Unknown ? "Empty / unrecognized / invalid" : p.Kind.ToString()) + $" — {p.Name} (#{p.Rgb:X6})")) + $"\nMissing — Red (#{MissingRgb:X6})\nSelected node — White";
}
