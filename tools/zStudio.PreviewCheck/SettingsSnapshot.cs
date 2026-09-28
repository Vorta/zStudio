using System.IO;

/// <summary>Keep synthetic windows and catalog generation from changing the user's layout.</summary>
internal sealed class SettingsSnapshot : IDisposable
{
    private readonly string path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "RecoilZbdStudio", "settings.json");
    private readonly byte[]? original;
    public SettingsSnapshot() => original = File.Exists(path) ? File.ReadAllBytes(path) : null;
    public void Dispose()
    {
        if (original != null) File.WriteAllBytes(path, original);
        else if (File.Exists(path)) File.Delete(path);
    }
}
