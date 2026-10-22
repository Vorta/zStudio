namespace zStudio.Rendering.Direct3D;

/// <summary>
/// Temporary A/B switch while both renderers exist (removed with the Helix adapter): the in-house Direct3D 11 backend is the
/// default; <c>ZSTUDIO_RENDERER=helix</c> selects the Helix adapter. Read once per process.
/// </summary>
internal static class RenderBackend
{
    public static bool UseHelix { get; } = string.Equals(Environment.GetEnvironmentVariable("ZSTUDIO_RENDERER")?.Trim(), "helix", StringComparison.OrdinalIgnoreCase);
}
