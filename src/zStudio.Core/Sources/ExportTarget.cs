using zStudio.Core.Animation;
using zStudio.Core.Formats;
using zStudio.Core.Worlds;

namespace zStudio.Core.Sources;

/// <summary>
/// The game an export is for. It chooses the formats of each mission's world (<c>gamez.zbd</c>) and animations
/// (<c>anim.zbd</c>), under the same file names, and which of the project's outputs the export builds
/// (<see cref="ExportTargets.Builds"/>).
/// </summary>
public enum ExportTarget
{
    /// <summary>
    /// Recoil 1.x and reRECOIL v1: world version 15 and animation format 28, the 16-bit packs (Direct3D and software) and
    /// the three sound banks; no 32-bit packs, which neither reads.
    /// </summary>
    V1,
    /// <summary>
    /// reRECOIL v2: world version 200 and animation format 200 (version 15 and format 28 with the fields that capped
    /// content widened: docs/zbd-v200.md), the 32-bit packs, the 16-bit Direct3D packs, which reRECOIL v2 draws until its
    /// new renderer, and <c>soundsh.zbd</c>, the only sound bank it reads; no software packs, whose paletted images it
    /// cannot use, and no medium or low sound bank.
    /// </summary>
    V2,
}

/// <summary>
/// The formats and outputs of each <see cref="ExportTarget"/>, and its MCP names: the one place that says which target
/// builds an output (the plan's consumers, exports and MCP all use <see cref="Builds"/>).
/// </summary>
public static class ExportTargets
{
    /// <summary>Both targets, in order.</summary>
    public static IReadOnlyList<ExportTarget> All { get; } = [ExportTarget.V1, ExportTarget.V2];
    /// <summary>The GameZ world version the target writes: 15 or 200.</summary>
    public static uint WorldVersion(this ExportTarget target) => target == ExportTarget.V2 ? GameZWriter.WideVersion : GameZWriter.Version;
    /// <summary>The animation format the target writes: 28 or 200.</summary>
    public static uint AnimationFormat(this ExportTarget target) => target == ExportTarget.V2 ? AnimationPackage.WideFormat : 28u;
    /// <summary>The target's name in MCP: <c>v1</c> or <c>v2</c>.</summary>
    public static string Name(this ExportTarget target) => target == ExportTarget.V2 ? "v2" : "v1";
    /// <summary>The target a <see cref="Name"/> names, or null for another text.</summary>
    public static ExportTarget? Parse(string? name) => name switch { "v1" => ExportTarget.V1, "v2" => ExportTarget.V2, _ => null };

    /// <summary>The medium- and low-quality sound banks, which only Recoil and reRECOIL v1 read.</summary>
    private static bool LowerSoundBank(SourceOutputPlan output) =>
        output.Family == "sounds" && (output.Path.Equals("soundsm.zbd", StringComparison.OrdinalIgnoreCase) || output.Path.Equals("soundsl.zbd", StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// Whether an export for <paramref name="target"/> builds <paramref name="output"/>: the software packs and the medium
    /// and low sound banks only for v1, the 32-bit packs only for v2, every other output (the worlds and animations in the
    /// target's formats) for both.
    /// </summary>
    public static bool Builds(this ExportTarget target, SourceOutputPlan output) => output.Pack?.Kind switch
    {
        TexturePackKind.Rgba or TexturePackKind.RgbaInterface => target == ExportTarget.V2,
        TexturePackKind.Software => target == ExportTarget.V1,
        _ => !LowerSoundBank(output) || target == ExportTarget.V1,
    };

    /// <summary>The targets that build <paramref name="output"/>, by name (<c>v1</c>, <c>v2</c>).</summary>
    public static string[] Targets(SourceOutputPlan output) => [.. All.Where(t => t.Builds(output)).Select(t => t.Name())];

    /// <summary>Why an export for <paramref name="target"/> does not build <paramref name="output"/> (see <see cref="Builds"/>), naming the target that does.</summary>
    internal static string NotBuilt(this ExportTarget target, SourceOutputPlan output) => output.Pack?.Kind switch
    {
        TexturePackKind.Rgba or TexturePackKind.RgbaInterface => $"{output.Path} is a 32-bit pack, which only target v2 builds (reRECOIL v2).",
        TexturePackKind.Software => $"{output.Path} is a software pack, which only target v1 builds (Recoil's software mode).",
        _ => $"{output.Path} is a lower-quality sound bank, which only target v1 builds (reRECOIL v2 reads only soundsh.zbd).",
    };
}
