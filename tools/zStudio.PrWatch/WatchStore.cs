using System.Text.Json;
using System.Text.Json.Serialization;

namespace Recoil.Zbd.PrWatch;

public sealed class WatchStore
{
    public static readonly JsonSerializerOptions Json = new() { WriteIndented = true, DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull };
    public string Workspace { get; }
    public string Folder { get; }
    public string StatePath => Path.Combine(Folder, "state.json");
    public WatchStore(string workspace, int pr)
    {
        if (pr <= 0) throw new ArgumentOutOfRangeException(nameof(pr));
        Workspace = Path.TrimEndingDirectorySeparator(Path.GetFullPath(workspace));
        Folder = Path.Combine(Workspace, ".agent", "pr-watch", "pr-" + pr);
        EnsureSafe(Folder);
    }

    public void EnsureSafe(string path)
    {
        path = Path.GetFullPath(path);
        if (!path.StartsWith(Workspace + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            throw new IOException("Watcher path escaped the workspace.");
        for (string? item = path; item != null; item = Path.GetDirectoryName(item))
        {
            if ((File.Exists(item) || Directory.Exists(item)) && (File.GetAttributes(item) & FileAttributes.ReparsePoint) != 0)
                throw new IOException("Watcher paths cannot use symbolic links or junctions.");
            if (string.Equals(item, Workspace, StringComparison.OrdinalIgnoreCase)) break;
        }
    }

    public async Task<FileStream> LockAsync(string name, CancellationToken token, bool wait = true)
    {
        string path = Path.Combine(Folder, name + ".lock"); EnsureSafe(path); Directory.CreateDirectory(Folder);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token); deadline.CancelAfter(TimeSpan.FromSeconds(35));
        while (true)
        {
            try { return new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None); }
            catch (IOException) when (wait) { await Task.Delay(50, deadline.Token); }
        }
    }

    public WatchState? Load()
    {
        if (!File.Exists(StatePath)) return null;
        var state = Read<WatchState>(StatePath);
        Validate(state);
        return state;
    }
    private void Validate(WatchState state)
    {
        if (state.Schema != 1 || !string.Equals(state.Workspace, Workspace, StringComparison.OrdinalIgnoreCase) ||
            Path.GetFileName(Folder) != "pr-" + state.Pr || state.Id == Guid.Empty || state.Thread == Guid.Empty ||
            state.Handled.Count > 30000 || state.Notices.Count > 2000)
            throw new InvalidDataException("Invalid watcher state; preserve it for inspection.");
    }
    public void Save(WatchState state) { Validate(state); Write(StatePath, state); }
    public void SaveWorkerError(string error) => Write(Path.Combine(Folder, "worker-error.json"), new { time = DateTimeOffset.UtcNow, error = WatchService.Short(error) });
    public string SaveProbe(Notice notice)
    {
        string path = Path.Combine(Folder, "probes", notice.Id.ToString("D") + ".json"); Write(path, notice); return path;
    }
    public string SaveSnapshot(ReadSnapshot snapshot)
    {
        string path = Path.Combine(Folder, "snapshots", snapshot.Id.ToString("D") + ".json");
        Write(path, snapshot); return path;
    }
    public ReadSnapshot Snapshot(Guid id) => Read<ReadSnapshot>(Path.Combine(Folder, "snapshots", id.ToString("D") + ".json"));
    private T Read<T>(string path)
    {
        EnsureSafe(path);
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (stream.Length > 64 * 1024 * 1024) throw new InvalidDataException("Watcher record exceeds 64 MiB.");
        return JsonSerializer.Deserialize<T>(stream, Json) ?? throw new InvalidDataException("Empty watcher record.");
    }
    private void Write<T>(string path, T value)
    {
        EnsureSafe(path); Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        string temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                JsonSerializer.Serialize(stream, value, Json);
                if (stream.Length > 64 * 1024 * 1024) throw new InvalidDataException("Watcher record exceeds 64 MiB; previous record retained.");
                stream.Flush(true);
            }
            File.Move(temporary, path, true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
}
