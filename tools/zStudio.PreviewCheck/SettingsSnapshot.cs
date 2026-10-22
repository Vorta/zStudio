using System.IO;

/// <summary>Keep synthetic windows and catalog generation from changing the settings of the profile in use (PrivateProfile).</summary>
internal sealed class SettingsSnapshot : IDisposable
{
    private readonly string path = zStudio.Desktop.StudioSettings.SettingsPath;
    private readonly byte[]? original;
    public SettingsSnapshot() => original = File.Exists(path) ? File.ReadAllBytes(path) : null;
    public void Dispose()
    {
        if (original != null) File.WriteAllBytes(path, original);
        else if (File.Exists(path)) File.Delete(path);
    }
}
