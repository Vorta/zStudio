using zStudio.Core.Animation;
using zStudio.Core.Formats;
using zStudio.Core.Worlds;

namespace zStudio.Core.Sources;

/// <summary>
/// The game an export is for. It chooses the formats of each mission's world (<c>gamez.zbd</c>) and animations
/// (<c>anim.zbd</c>), under the same file names, and which texture and image packs the export writes; every other output
/// is the same for both.
/// </summary>
public enum ExportTarget
{
    /// <summary>
    /// Recoil 1.x and reRECOIL v1: world version 15 and animation format 28, and the 16-bit packs (Direct3D and software);
    /// no 32-bit packs, which neither reads.
    /// </summary>
    V1,
    /// <summary>
    /// reRECOIL v2: world version 200 and animation format 200 (version 15 and format 28 with the fields that capped
    /// content widened: docs/zbd-v200.md), the 32-bit packs, and the 16-bit Direct3D packs, which reRECOIL v2 draws until
    /// its renderer takes the 32-bit packs; no software packs, which only Recoil's software renderer reads.
    /// </summary>
    V2,
}

/// <summary>The formats and files an <see cref="ExportTarget"/> writes, and its MCP names.</summary>
public static class ExportTargets
{
    /// <summary>The GameZ world version the target writes: 15 or 200.</summary>
    public static uint WorldVersion(this ExportTarget target) => target == ExportTarget.V2 ? GameZWriter.WideVersion : GameZWriter.Version;
    /// <summary>The animation format the target writes: 28 or 200.</summary>
    public static uint AnimationFormat(this ExportTarget target) => target == ExportTarget.V2 ? AnimationPackage.WideFormat : 28u;
    /// <summary>The target's name in MCP: <c>v1</c> or <c>v2</c>.</summary>
    public static string Name(this ExportTarget target) => target == ExportTarget.V2 ? "v2" : "v1";
    /// <summary>The target a <see cref="Name"/> names, or null for another text.</summary>
    public static ExportTarget? Parse(string? name) => name switch { "v1" => ExportTarget.V1, "v2" => ExportTarget.V2, _ => null };

    /// <summary>
    /// Whether an export for <paramref name="target"/> writes <paramref name="output"/>: the 32-bit packs only for v2, the
    /// software packs only for v1, every other output for both.
    /// </summary>
    public static bool Writes(this ExportTarget target, SourceOutputPlan output) => output.Pack?.Kind switch
    {
        TexturePackKind.Rgba or TexturePackKind.RgbaInterface => target == ExportTarget.V2,
        TexturePackKind.Software => target == ExportTarget.V1,
        _ => true,
    };

    /// <summary>Why an export for <paramref name="target"/> does not write <paramref name="output"/> (see <see cref="Writes"/>).</summary>
    internal static string NotWritten(this ExportTarget target, SourceOutputPlan output) => target == ExportTarget.V1
        ? $"{output.Path} is a 32-bit pack, which only reRECOIL v2 reads: export it with target v2."
        : $"{output.Path} is a software pack, which only Recoil's software renderer reads: export it with target v1.";
}
