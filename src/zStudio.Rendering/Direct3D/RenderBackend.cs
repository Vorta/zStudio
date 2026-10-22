namespace zStudio.Rendering.Direct3D;

/// <summary>
/// Temporary A/B switch while both renderers exist (removed with the Helix adapter): <c>ZSTUDIO_RENDERER=direct3d</c> selects the
/// in-house Direct3D 11 backend, anything else the Helix adapter. Read once per process.
/// </summary>
internal static class RenderBackend
{
    public static bool UseHelix { get; } = !string.Equals(Environment.GetEnvironmentVariable("ZSTUDIO_RENDERER")?.Trim(), "direct3d", StringComparison.OrdinalIgnoreCase);
}
