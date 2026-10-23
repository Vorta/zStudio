using System.IO;
using System.Runtime.CompilerServices;
using zStudio.Automation;

namespace zStudio.Tests;

/// <summary>
/// Test and tool hosts never share the user's zStudio profile (settings, error log, MCP registrations): unless the
/// caller names one in <see cref="StudioProfile.OverrideVariable"/>, this process and the processes it starts get a
/// fresh temporary profile, deleted at exit unless it holds an error log (named on stderr). Linked into zStudio.Desktop.Tests
/// and zStudio.PreviewCheck.
/// </summary>
internal static class PrivateProfile
{
    [ModuleInitializer]
    internal static void Initialize()
    {
        if (!string.IsNullOrEmpty(Environment.GetEnvironmentVariable(StudioProfile.OverrideVariable))) return;
        string folder = Path.Combine(Path.GetTempPath(), "zStudio-profile-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        Environment.SetEnvironmentVariable(StudioProfile.OverrideVariable, folder);
        AppDomain.CurrentDomain.ProcessExit += (_, _) =>
        {
            string log = Path.Combine(folder, "error.log");
            if (File.Exists(log)) { Console.Error.WriteLine($"zStudio error log kept: {log}"); return; }
            try { Directory.Delete(folder, true); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        };
    }
}
