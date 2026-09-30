using System.Diagnostics;
using System.Text;

namespace Recoil.Zbd.PrWatch;

public sealed record CommandResult(int ExitCode, string Output, string Error);
public interface ICommandRunner
{
    Task<CommandResult> RunAsync(string executable, IReadOnlyList<string> arguments, string cwd, CancellationToken token);
}
public sealed class CommandRunner : ICommandRunner
{
    public static ProcessStartInfo StartInfo(string executable, IEnumerable<string> arguments, string cwd)
    {
        ProcessStartInfo info = new(executable) { WorkingDirectory = cwd, UseShellExecute = false, CreateNoWindow = true };
        foreach (string arg in arguments) info.ArgumentList.Add(arg);
        info.Environment["GH_PROMPT_DISABLED"] = "1";
        return info;
    }
    public async Task<CommandResult> RunAsync(string executable, IReadOnlyList<string> arguments, string cwd, CancellationToken token)
    {
        var info = StartInfo(executable, arguments, cwd);
        info.RedirectStandardOutput = info.RedirectStandardError = info.RedirectStandardInput = true;
        using var process = Process.Start(info) ?? throw new IOException("Could not launch " + executable);
        process.StandardInput.Close();
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token); deadline.CancelAfter(TimeSpan.FromSeconds(30));
        // Oversized output cancels all readers and the child, rather than waiting on a full pipe.
        async Task<string> Read(StreamReader reader, int limit)
        {
            try { return await BoundedReadAsync(reader, limit, deadline.Token); }
            catch { deadline.Cancel(); throw; }
        }
        try
        {
            var stdout = Read(process.StandardOutput, 16 * 1024 * 1024);
            var stderr = Read(process.StandardError, 64 * 1024);
            await Task.WhenAll(stdout, stderr, process.WaitForExitAsync(deadline.Token));
            return new(process.ExitCode, await stdout, await stderr);
        }
        finally { if (!process.HasExited) { process.Kill(true); await process.WaitForExitAsync(CancellationToken.None); } }
    }
    public static async Task<string> BoundedReadAsync(StreamReader reader, int limit, CancellationToken token)
    {
        StringBuilder result = new(); char[] buffer = new char[4096]; int n;
        while ((n = await reader.ReadAsync(buffer.AsMemory(), token)) != 0)
        {
            if (n > limit - result.Length) throw new InvalidDataException("Process output exceeded its limit.");
            result.Append(buffer, 0, n);
        }
        return result.ToString();
    }
    public static string Executable(string name, string? explicitPath = null)
    {
        IEnumerable<string?> candidates = explicitPath != null ? [explicitPath] : name == "codex"
            ? new[] { Environment.GetEnvironmentVariable("CC_PLUGIN_CODEX_EXECUTABLE") }.Concat(SearchPath(name)) : SearchPath(name);
        foreach (string? candidate in candidates)
            if (!string.IsNullOrWhiteSpace(candidate) && Path.IsPathFullyQualified(candidate) &&
                candidate.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) && File.Exists(candidate)) return Path.GetFullPath(candidate);
        throw new FileNotFoundException($"No native {name}.exe found. Install it or supply its absolute path; shell shims are not supported.");
    }
    private static IEnumerable<string> SearchPath(string name) => (Environment.GetEnvironmentVariable("PATH") ?? "")
        .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries).Select(p => Path.Combine(p.Trim('"'), name + ".exe"));
    public static bool Alive(int pid, long ticks)
    {
        try { using var process = Process.GetProcessById(pid); return !process.HasExited && process.StartTime.ToUniversalTime().Ticks == ticks; }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception) { return false; }
    }
}
