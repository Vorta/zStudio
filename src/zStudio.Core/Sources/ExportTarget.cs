using zStudio.Core.Animation;
using zStudio.Core.Worlds;

namespace zStudio.Core.Sources;

/// <summary>
/// The game an export writes each mission's world (<c>gamez.zbd</c>) and animations (<c>anim.zbd</c>) for. The file names
/// and every other output are the same for both targets.
/// </summary>
public enum ExportTarget
{
    /// <summary>World version 15 and animation format 28, which Recoil and reRECOIL v1 read (and reRECOIL v2 too).</summary>
    Recoil,
    /// <summary>
    /// World version 200 and animation format 200, which only reRECOIL v2 reads: version 15 and format 28 with the fields
    /// that capped content widened (docs/zbd-v200.md).
    /// </summary>
    ReRecoilV2,
}

/// <summary>The formats an <see cref="ExportTarget"/> writes, and its MCP names.</summary>
public static class ExportTargets
{
    /// <summary>The GameZ world version the target writes: 15 or 200.</summary>
    public static uint WorldVersion(this ExportTarget target) => target == ExportTarget.ReRecoilV2 ? GameZWriter.WideVersion : GameZWriter.Version;
    /// <summary>The animation format the target writes: 28 or 200.</summary>
    public static uint AnimationFormat(this ExportTarget target) => target == ExportTarget.ReRecoilV2 ? AnimationPackage.WideFormat : 28u;
    /// <summary>The target's name in MCP and settings: <c>recoil</c> or <c>rerecoil-v2</c>.</summary>
    public static string Name(this ExportTarget target) => target == ExportTarget.ReRecoilV2 ? "rerecoil-v2" : "recoil";
    /// <summary>The target a <see cref="Name"/> names, or null for another text.</summary>
    public static ExportTarget? Parse(string? name) => name switch { "recoil" => ExportTarget.Recoil, "rerecoil-v2" => ExportTarget.ReRecoilV2, _ => null };
}
