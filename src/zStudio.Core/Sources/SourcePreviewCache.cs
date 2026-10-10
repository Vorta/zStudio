using System.Globalization;

namespace Recoil.Zbd.Core.Sources;

/// <summary>
/// One source world's derived preview cache. Keeps its physical directory identities from creation through every build
/// and deletion, including when the visible session closes while a canceled build is still returning.
/// </summary>
public sealed class SourcePreviewCache : IDisposable
{
    private readonly DirectoryLease directories = new();
    private readonly object sync = new();
    private readonly SemaphoreSlim buildGate = new(1, 1);
    private readonly CancellationTokenSource shutdown = new();
    private readonly HashSet<string> issued = new(StringComparer.OrdinalIgnoreCase), pendingRemoval = new(StringComparer.OrdinalIgnoreCase);
    private readonly FileStream lockFile;
    private readonly string root;
    private readonly int maximumCleanupEntries;
    private readonly long maximumCleanupPathUnits;
    private readonly Action? cleanupVisited;
    private int generation, builds;
    private bool disposed, closed;
    public string Folder { get; }

    public SourcePreviewCache(string projectRoot, CancellationToken token = default)
        : this(projectRoot, token, SourceProject.MaximumScannedEntries, InventoryBudget.MaximumUnits) { }

    internal SourcePreviewCache(string projectRoot, CancellationToken token, int maximumCleanupEntries,
        long maximumCleanupPathUnits, Action? cleanupVisited = null)
    {
        this.maximumCleanupEntries = maximumCleanupEntries;
        this.maximumCleanupPathUnits = maximumCleanupPathUnits;
        this.cleanupVisited = cleanupVisited;
        root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(projectRoot));
        string? created = null;
        try
        {
            token.ThrowIfCancellationRequested();
            directories.Hold(root);
            // Return stable physical paths to callers that subsequently open a built document, rather than a mutable
            // drive alias. The captured original remains held while the equivalent physical path is acquired.
            root = directories.CapturedPath(root);
            directories.Hold(root);
            if (!SourceProject.IsProject(root)) throw new DirectoryNotFoundException("A preview cache requires a source project with data and gamegen folders.");
            string previews = SourceWorlds.PreviewRoot(root);
            if (PickupPlacementEditSession.IsProtectedPath(previews)) throw new IOException("Preview caches cannot be created inside the protected zbd_1998/zbd_1999 folders.");
            SourceProject.RejectLinkedProject(root);
            SourceProject.RejectLinks(previews);
            if (!directories.CapturedPath(previews).Equals(previews, StringComparison.OrdinalIgnoreCase)) throw new IOException("The preview cache path does not belong to the captured source project.");
            directories.Hold(previews, create: true);
            RemoveAbandoned(previews, token);
            token.ThrowIfCancellationRequested();
            Folder = Path.Combine(previews, Guid.NewGuid().ToString("N"));
            directories.CreateDirectory(Folder); created = Folder;
            lockFile = directories.OpenFile(Path.Combine(Folder, ".lock"), FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None, 1, FileOptions.DeleteOnClose);
        }
        catch
        {
            if (created != null) BestEffortDelete(created);
            directories.Dispose(); buildGate.Dispose(); shutdown.Dispose();
            throw;
        }
    }

    /// <summary>A destination reserved for this session's next build; no directory is created yet.</summary>
    public string NextFolder()
    {
        lock (sync)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            string path = Path.Combine(Folder, (++generation).ToString(CultureInfo.InvariantCulture));
            issued.Add(path); return path;
        }
    }

    /// <summary>Builds one reserved destination. Superseded builds finish releasing their captured state before the next starts.</summary>
    public async Task<SourceWorldBuild> BuildAsync(string mission, string destination, IReadOnlyDictionary<string, byte[]>? overlay = null,
        IProgress<SourceProgress>? progress = null, CancellationToken token = default, IReadOnlyList<SourceModelAddition>? additions = null)
    {
        lock (sync)
        {
            ObjectDisposedException.ThrowIf(disposed, this); RequireOwned(destination); builds++;
        }
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(token, shutdown.Token);
        bool entered = false;
        try
        {
            await buildGate.WaitAsync(cancellation.Token).ConfigureAwait(false); entered = true;
            cancellation.Token.ThrowIfCancellationRequested();
            lock (sync) ObjectDisposedException.ThrowIf(disposed, this);
            return await SourceWorlds.BuildPreviewCoreAsync(root, mission, destination, overlay, progress, cancellation.Token, additions, directories).ConfigureAwait(false);
        }
        finally
        {
            if (entered) buildGate.Release();
            lock (sync)
            {
                builds--;
                if (builds == 0)
                {
                    DrainRemovals();
                    if (disposed) Close();
                }
            }
        }
    }

    /// <summary>Removes only a build reserved by this cache; cleanup waits while any build still uses its captured directories.</summary>
    public void DeleteBuild(string buildFolder)
    {
        lock (sync)
        {
            RequireOwned(buildFolder);
            if (closed) return;
            pendingRemoval.Add(Path.GetFullPath(buildFolder));
            if (builds == 0) DrainRemovals();
        }
    }

    private void RequireOwned(string folder)
    {
        if (!issued.Contains(Path.GetFullPath(folder))) throw new ArgumentException("The build folder was not reserved by this preview session.", nameof(folder));
    }
    private void DrainRemovals()
    {
        CleanupBudget budget = NewCleanupBudget();
        // HashSet permits removing the current member during enumeration. Failed paths remain retryable.
        try
        {
            foreach (string path in pendingRemoval)
                if (TryDelete(path, budget)) pendingRemoval.Remove(path);
        }
        catch (CleanupCapacityException) { }
    }
    private CleanupBudget NewCleanupBudget(CancellationToken token = default, bool observe = false)
        => new(maximumCleanupEntries, maximumCleanupPathUnits, token, observe ? cleanupVisited : null);

    private void BestEffortDelete(string path)
    {
        try { TryDelete(path, NewCleanupBudget()); }
        catch (CleanupCapacityException) { }
    }

    private bool TryDelete(string path, CleanupBudget budget)
    {
        try
        {
            budget.Visit(path.Length);
            directories.DeleteTree(path, budget.Visit, budget.Token);
            return true;
        }
        catch (CleanupCapacityException) { throw; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return false; }
    }
    private void RemoveAbandoned(string previews, CancellationToken token)
    {
        CleanupBudget budget = NewCleanupBudget(token, observe: true);
        DateTime before = DateTime.UtcNow.AddMinutes(-1);
        try
        {
            budget.Visit(previews.Length);
            foreach (var entry in directories.Entries(previews, token))
            {
                budget.Visit((long)previews.Length + entry.Name.Length + 1);
                if (!entry.Attributes.HasFlag(FileAttributes.Directory) || entry.Attributes.HasFlag(FileAttributes.ReparsePoint) || entry.CreationTimeUtc >= before) continue;
                string path = Path.Combine(previews, entry.Name);
                // A live sibling owns its own lifetime. Do not retain its directory merely because we inspected its
                // lock: that would prevent its final empty-folder removal until this session also closes.
                bool active;
                budget.Path((long)path.Length + 6);
                using (DirectoryLease probe = new()) active = probe.Exists(Path.Combine(path, ".lock"));
                if (!active) TryDelete(path, budget);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }
    public void Dispose()
    {
        lock (sync)
        {
            if (disposed) return;
            disposed = true;
            shutdown.Cancel();
            if (builds == 0) Close();
        }
    }
    private void Close()
    {
        if (closed) return;
        closed = true;
        try { lockFile.Dispose(); BestEffortDelete(Folder); }
        finally { directories.Dispose(); buildGate.Dispose(); shutdown.Dispose(); }
    }

    private sealed class CleanupCapacityException() : IOException;

    private sealed class CleanupBudget(int maximumEntries, long maximumPathUnits, CancellationToken token, Action? visited)
    {
        private readonly SourceProject.ScanBudget scan = new(maximumEntries, _ => new CleanupCapacityException(), token, new InventoryBudget(maximumPathUnits));
        internal CancellationToken Token => token;
        internal void Path(long characters)
        {
            try { scan.Path(characters); }
            catch (InventoryCapacityException) { throw new CleanupCapacityException(); }
        }
        internal void Visit(long characters)
        {
            scan.Visit(); Path(characters);
            visited?.Invoke(); token.ThrowIfCancellationRequested();
        }
    }
}
