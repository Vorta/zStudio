using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO.Pipes;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text.Json;
using Recoil.Zbd.Automation;

namespace Recoil.Zbd.Mcp;

public sealed record McpInstance(string Id, string Pipe, int ProcessId, long Started, string Executable);

public sealed class LocalMcpHost : IAsyncDisposable
{
    public static string RegistrationDirectory => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "RecoilZbdStudio", "mcp-instances");
    private readonly CancellationTokenSource lifetime = new();
    private readonly ConcurrentDictionary<int, NamedPipeServerStream> connections = new();
    private readonly List<Task> sessions = [];
    private readonly Task accepting;
    private readonly string registration;
    private readonly StudioCommands commands;
    private readonly string version;
    private int connectionId;
    public McpInstance Instance { get; }
    public int ConnectionCount => connections.Count;
    public event Action<string>? Activity;

    public LocalMcpHost(StudioCommands commands, string version)
    {
        this.commands = commands; this.version = version;
        string id = Guid.NewGuid().ToString("N");
        Instance = new(id, "zstudio-mcp-" + id, Environment.ProcessId, Process.GetCurrentProcess().StartTime.ToUniversalTime().Ticks, Environment.ProcessPath!);
        Directory.CreateDirectory(RegistrationDirectory);
        registration = Path.Combine(RegistrationDirectory, id + ".json");
        // Bind before advertising so the connector never races an uncreated pipe.
        var first = CreatePipe();
        try { File.WriteAllText(registration, JsonSerializer.Serialize(Instance)); }
        catch { first.Dispose(); throw; }
        accepting = AcceptAsync(first);
    }

    private NamedPipeServerStream CreatePipe()
    {
        PipeSecurity security = new();
        security.SetAccessRuleProtection(true, false);
        security.AddAccessRule(new(new SecurityIdentifier(WellKnownSidType.NetworkSid, null), PipeAccessRights.FullControl, AccessControlType.Deny));
        security.AddAccessRule(new(WindowsIdentity.GetCurrent().User!, PipeAccessRights.FullControl, AccessControlType.Allow));
        return NamedPipeServerStreamAcl.Create(Instance.Pipe, PipeDirection.InOut, NamedPipeServerStream.MaxAllowedServerInstances, PipeTransmissionMode.Byte, PipeOptions.Asynchronous, 65536, 65536, security);
    }

    private async Task AcceptAsync(NamedPipeServerStream pipe)
    {
        try
        {
            while (!lifetime.IsCancellationRequested)
            {
                await pipe.WaitForConnectionAsync(lifetime.Token);
                int id = Interlocked.Increment(ref connectionId); connections[id] = pipe;
                Task session = ServeAsync(id, pipe); lock (sessions) { sessions.RemoveAll(t => t.IsCompleted); sessions.Add(session); }
                pipe = CreatePipe();
            }
        }
        catch (Exception ex) when (ex is OperationCanceledException or IOException or ObjectDisposedException) { if (!lifetime.IsCancellationRequested) Activity?.Invoke("Connection listener: " + ex.Message); }
        finally { pipe.Dispose(); }
    }

    private async Task ServeAsync(int id, NamedPipeServerStream pipe)
    {
        try
        {
            Activity?.Invoke($"Client {id} connected");
            await using var server = StudioMcpServer.Create(pipe, commands, version, message => Activity?.Invoke($"Client {id}: {message}"));
            await server.RunAsync(lifetime.Token);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException and not StackOverflowException) { if (!lifetime.IsCancellationRequested) Activity?.Invoke($"Client {id}: {ex.Message}"); }
        finally { connections.TryRemove(id, out _); pipe.Dispose(); Activity?.Invoke($"Client {id} disconnected"); }
    }

    public void DisconnectClients() { foreach (var pipe in connections.Values) pipe.Dispose(); }
    public async ValueTask DisposeAsync()
    {
        lifetime.Cancel(); DisconnectClients();
        await accepting;
        Task[] pending; lock (sessions) pending = sessions.ToArray();
        await Task.WhenAll(pending);
        try { File.Delete(registration); } catch (IOException) { }
        lifetime.Dispose();
    }

    public static IReadOnlyList<McpInstance> Discover(string executable)
    {
        List<McpInstance> result = [];
        if (!Directory.Exists(RegistrationDirectory)) return result;
        foreach (string file in Directory.EnumerateFiles(RegistrationDirectory, "*.json"))
        {
            try
            {
                var instance = JsonSerializer.Deserialize<McpInstance>(File.ReadAllText(file));
                if (instance == null || !instance.Executable.Equals(executable, StringComparison.OrdinalIgnoreCase)) continue;
                using var process = Process.GetProcessById(instance.ProcessId);
                if (process.StartTime.ToUniversalTime().Ticks == instance.Started && process.SessionId == Process.GetCurrentProcess().SessionId) result.Add(instance);
            }
            catch (Exception ex) when (ex is IOException or JsonException or ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception) { }
        }
        return result;
    }

    public static async Task<int> ConnectStdioAsync(string executable, string? requestedInstance, Func<bool> enabled, string version, CancellationToken token = default)
    {
        try
        {
            await using var connector = new LazyMcpConnector(
                t => ConnectWorkspaceAsync(executable, requestedInstance, enabled, t), enabled);
            await connector.RunAsync(Console.OpenStandardInput(), Console.OpenStandardOutput(), version, token);
            return 0;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException and not StackOverflowException) { await Console.Error.WriteLineAsync("zStudio MCP: " + ex.Message); return 1; }
    }

    private static async Task<ModelContextProtocol.Client.McpClient> ConnectWorkspaceAsync(string executable, string? requestedInstance, Func<bool> enabled, CancellationToken token)
    {
        if (!enabled()) throw new StudioCommandException("access_disabled", "Enable MCP once in zStudio: Tools > MCP integration.");
        var instances = Discover(executable);
        if (instances.Count == 0 && requestedInstance == null)
        {
            // Cross-connector startup lease; the visible app owns its subsequent lifetime.
            using var lease = new Mutex(false, "Local\\zStudioMcpStartup-" + Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(executable)))[..24]);
            bool held = false;
            try
            {
                try { held = lease.WaitOne(TimeSpan.FromSeconds(20)); } catch (AbandonedMutexException) { held = true; }
                if (!held) throw new TimeoutException("Another connector is starting zStudio. Retry shortly.");
                instances = Discover(executable);
                if (instances.Count == 0)
                {
                    token.ThrowIfCancellationRequested();
                    if (!enabled()) throw new StudioCommandException("access_disabled", "Local MCP access was disabled.");
                    StartVisibleWorkspace(executable);
                    var deadline = Stopwatch.StartNew();
                    while ((instances = Discover(executable)).Count == 0 && deadline.Elapsed < TimeSpan.FromSeconds(20)) { token.ThrowIfCancellationRequested(); Thread.Sleep(100); }
                }
            }
            finally { if (held) lease.ReleaseMutex(); }
        }
        var candidates = instances.Where(i => requestedInstance == null || i.Id == requestedInstance).ToArray();
        if (candidates.Length != 1) throw new InvalidOperationException(candidates.Length == 0 ? "No enabled zStudio instance is available." : "Several workspaces are open. Add --instance followed by one of: " + string.Join(", ", candidates.Select(i => i.Id)));
        if (!enabled()) throw new StudioCommandException("access_disabled", "Local MCP access was disabled.");
        var pipe = new NamedPipeClientStream(".", candidates[0].Pipe, PipeDirection.InOut, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        try
        {
            await pipe.ConnectAsync(15000, token);
            return await ModelContextProtocol.Client.McpClient.CreateAsync(new ModelContextProtocol.Protocol.StreamClientTransport(pipe, pipe), cancellationToken: token);
        }
        catch { await pipe.DisposeAsync(); throw; }
    }

    private static void StartVisibleWorkspace(string executable)
    {
        // Explorer brokers the launch so MCP clients that kill their connector's
        // process tree cannot terminate the shared workspace and unsaved edits.
        var type = Type.GetTypeFromProgID("Shell.Application") ?? throw new InvalidOperationException("Windows Shell is unavailable. Open zStudio manually before connecting.");
        dynamic shell = Activator.CreateInstance(type)!;
        List<object> objects = [shell];
        try
        {
            dynamic windows = shell.Windows(); objects.Add(windows);
            object location = 0, unused = 0; int hwnd;
            dynamic desktop = windows.FindWindowSW(ref location, ref unused, 8, out hwnd, 1);
            if (desktop == null) throw new InvalidOperationException("Windows desktop is unavailable. Open zStudio manually before connecting.");
            objects.Add(desktop);
            dynamic document = desktop.Document; objects.Add(document);
            dynamic broker = document.Application; objects.Add(broker);
            broker.ShellExecute(executable, "--mcp-host", Path.GetDirectoryName(executable)!, "open", 1);
        }
        finally { foreach (object item in objects.AsEnumerable().Reverse()) System.Runtime.InteropServices.Marshal.ReleaseComObject(item); }
    }
}
