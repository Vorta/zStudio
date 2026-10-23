namespace zStudio.Automation;

/// <summary>
/// The per-user folder that holds zStudio's settings, error log and local MCP registrations:
/// <c>%LOCALAPPDATA%/zStudio</c>. Every reader and writer of that folder resolves it here.
/// </summary>
public static class StudioProfile
{
    /// <summary>
    /// Tests and tools only, not a user feature: an absolute folder that replaces the profile for this process
    /// and the processes it starts, so test runs never share settings or MCP registrations with the user's zStudio.
    /// Read once, on first use.
    /// </summary>
    public const string OverrideVariable = "ZSTUDIO_PROFILE";

    private static readonly Lazy<string> directory = new(Resolve);

    /// <summary>The user's own profile folder, which applies when <see cref="OverrideVariable"/> is not set.</summary>
    public static string DefaultDirectory { get; } = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "zStudio");

    /// <summary>The profile folder of this process; throws when <see cref="OverrideVariable"/> holds something other than an absolute path.</summary>
    public static string Directory => directory.Value;

    /// <summary>Whether this process uses a profile other than the user's own.</summary>
    public static bool IsPrivate => !string.Equals(Directory, DefaultDirectory, StringComparison.OrdinalIgnoreCase);

    private static string Resolve()
    {
        string? value = Environment.GetEnvironmentVariable(OverrideVariable);
        if (string.IsNullOrEmpty(value)) return DefaultDirectory;
        if (!Path.IsPathFullyQualified(value)) throw new InvalidOperationException($"{OverrideVariable} must be an absolute folder path: {value}");
        return Path.TrimEndingDirectorySeparator(Path.GetFullPath(value));
    }
}
