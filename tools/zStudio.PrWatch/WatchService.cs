using System.Diagnostics;
using System.Security.Cryptography;

namespace Recoil.Zbd.PrWatch;

public sealed class WatchService(WatchStore store, IPrSource source, INoticeQueue queue, TimeProvider? time = null,
    Func<WatchState, IPrSource>? sourceFactory = null, Func<WatchState, INoticeQueue>? queueFactory = null)
{
    private readonly TimeProvider clock = time ?? TimeProvider.System;
    private int storeFailures;
    // State contention (a reader without delete sharing, a lock held across a slow CLI call) is retryable.
    // Missing or invalid state remains fatal and is preserved for inspection.
    private static bool Transient(Exception ex, CancellationToken token) =>
        !token.IsCancellationRequested && ex is IOException or UnauthorizedAccessException or OperationCanceledException;
    private TimeSpan StoreRetry() => WatchLogic.RetryDelay(storeFailures = Math.Min(100, storeFailures + 1));
    public async Task<TimeSpan> PollAsync(CancellationToken token)
    {
        WatchState before;
        try
        {
            using (await store.LockAsync("state", token))
            {
                before = store.Load() ?? throw new InvalidOperationException("No watch exists.");
                if (!before.Active) return TimeSpan.Zero;
                before.Heartbeat = clock.GetUtcNow(); store.Save(before);
            }
        }
        catch (Exception ex) when (Transient(ex, token)) { return StoreRetry(); }
        try
        {
            var observation = await (sourceFactory?.Invoke(before) ?? source).ReadAsync(before.Repository, before.Pr, token);
            // Serialize publication and queue submission with stop/ack/re-arm. Stop cannot race a send.
            using (await store.LockAsync("state", token))
            {
                var state = store.Load()!;
                if (state.Id != before.Id || state.Generation != before.Generation || !state.Active) return TimeSpan.FromSeconds(1);
                var notice = WatchLogic.Observe(state, observation, clock.GetUtcNow());
                state.Heartbeat = clock.GetUtcNow(); store.Save(state); // The durable claim precedes any queue side effect.
                if (notice != null)
                {
                    try { notice.Submission = await (queueFactory?.Invoke(state) ?? queue).AddAsync(state.Thread, notice, token); notice.Delivery = "queued"; }
                    catch (Exception ex) when (ex is not OutOfMemoryException and not StackOverflowException)
                    { notice.Delivery = "unknown"; notice.Error = Short(ex.Message); }
                    store.Save(state);
                }
                storeFailures = 0;
                return state.Active ? TimeSpan.FromSeconds(60) : TimeSpan.Zero;
            }
        }
        catch (Exception ex) when (ex is not OutOfMemoryException and not StackOverflowException && !token.IsCancellationRequested)
        {
            try
            {
                using (await store.LockAsync("state", token))
                {
                    var state = store.Load()!;
                    if (state.Generation != before.Generation || state.Id != before.Id || !state.Active) return TimeSpan.FromSeconds(1);
                    state.LastError = Short(ex.Message); state.FailureCount = Math.Min(100, state.FailureCount + 1);
                    state.Heartbeat = clock.GetUtcNow(); store.Save(state);
                    return WatchLogic.RetryDelay(state.FailureCount, (ex as GitHubException)?.RetryAfter);
                }
            }
            catch (Exception saveError) when (Transient(saveError, token))
            { var delay = StoreRetry(); return (ex as GitHubException)?.RetryAfter is { } requested && requested > delay ? requested : delay; }
        }
    }
    public static string Short(string message) => message[..Math.Min(2000, message.Length)];
    public async Task RunAsync(CancellationToken token)
    {
        FileStream lease;
        try { lease = await store.LockAsync("worker", token, wait: false); }
        catch (IOException) { return; } // Another healthy worker owns this PR.
        using (lease)
        {
            using var process = Process.GetCurrentProcess();
            while (true)
            {
                try
                {
                    using (await store.LockAsync("state", token))
                    {
                        var state = store.Load() ?? throw new InvalidOperationException("No watch exists.");
                        if (!state.Active) return;
                        state.WorkerPid = process.Id; state.WorkerStartTicks = process.StartTime.ToUniversalTime().Ticks;
                        state.Heartbeat = clock.GetUtcNow(); store.Save(state);
                    }
                    break;
                }
                catch (Exception ex) when (Transient(ex, token)) { await Task.Delay(TimeSpan.FromSeconds(1), clock, token); }
            }
            while (!token.IsCancellationRequested)
            {
                var delay = await PollAsync(token); if (delay == TimeSpan.Zero) break;
                // Wake promptly for stop/re-arm even during backoff. A new generation resets the delay.
                int? generation = null;
                try { using (await store.LockAsync("state", token)) generation = store.Load()!.Generation; }
                catch (Exception ex) when (Transient(ex, token)) { }
                DateTimeOffset due = clock.GetUtcNow() + delay;
                while (clock.GetUtcNow() < due)
                {
                    await Task.Delay(TimeSpan.FromSeconds(1), clock, token);
                    try
                    {
                        using (await store.LockAsync("state", token))
                        { var state = store.Load()!; if (!state.Active) return; if (generation != null && state.Generation != generation) break; }
                    }
                    catch (Exception ex) when (Transient(ex, token)) { }
                }
            }
        }
    }
    public static async Task<(Notice Probe, string Path)> NotifyTestAsync(WatchStore store, INoticeQueue queue, Guid thread, CancellationToken token)
    {
        Notice probe = new() { Created = DateTimeOffset.UtcNow, Reasons = ["transport-test"] };
        probe.Message = $"zStudio PR watch transport test {probe.Id:D}. This is a controlled queue add/read/remove test, not PR feedback or approval. " +
            "Do not edit, push, merge, release, or re-arm because of this message. The caller owns verification and cleanup.";
        string path = store.SaveProbe(probe);
        // Delivery and cleanup are separate outcomes: a failed removal must not relabel a queued probe.
        try { probe.Submission = await queue.AddAsync(thread, probe, token); probe.Delivery = "queued"; }
        catch (Exception ex) when (ex is not OutOfMemoryException and not StackOverflowException)
        { probe.Delivery = "unknown"; probe.Error = Short(ex.Message); store.SaveProbe(probe); throw; }
        store.SaveProbe(probe);
        try { probe.Cleanup = await queue.RemoveAsync(thread, probe, token); probe.Acknowledged = DateTimeOffset.UtcNow; }
        catch (Exception ex) when (ex is not OutOfMemoryException and not StackOverflowException)
        { probe.Cleanup = "uncertain: " + Short(ex.Message); store.SaveProbe(probe); throw; }
        store.SaveProbe(probe); return (probe, path);
    }
    public static async Task<string> PrepareRuntimeAsync(WatchStore store, string baseFolder, IReadOnlyList<string> names, CancellationToken token)
    {
        // Hash exactly the bytes that are copied, so a concurrent build cannot mix versions.
        List<byte[]> contents = [];
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        foreach (string name in names) { byte[] bytes = await File.ReadAllBytesAsync(Path.Combine(baseFolder, name), token); contents.Add(bytes); hash.AppendData(bytes); }
        string runtime = Path.Combine(store.Folder, "runtime", Convert.ToHexString(hash.GetHashAndReset()));
        store.EnsureSafe(runtime); Directory.CreateDirectory(runtime);
        for (int i = 0; i < names.Count; i++)
        {
            string target = Path.Combine(runtime, names[i]); store.EnsureSafe(target);
            if (File.Exists(target) && await File.ReadAllBytesAsync(target, token) is var existing && existing.AsSpan().SequenceEqual(contents[i])) continue;
            // The folder is content-addressed. Publish complete copies atomically and replace a copy truncated by an interrupted launch.
            string temporary = target + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try { await File.WriteAllBytesAsync(temporary, contents[i], token); File.Move(temporary, target, true); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { throw new IOException("Worker runtime copy differs and could not be replaced: " + ex.Message, ex); }
            finally { if (File.Exists(temporary)) File.Delete(temporary); }
        }
        return runtime;
    }
    public static async Task StartWorkerAsync(WatchStore store, CancellationToken token)
    {
        using var launch = await store.LockAsync("launch", token);
        // Run a private immutable copy so subsequent solution builds do not hit locked tool binaries.
        string[] names = ["Recoil.Zbd.PrWatch.dll", "Recoil.Zbd.PrWatch.deps.json", "Recoil.Zbd.PrWatch.runtimeconfig.json"];
        string runtime = await PrepareRuntimeAsync(store, AppContext.BaseDirectory, names, token);
        using var child = Process.Start(CommandRunner.StartInfo(CommandRunner.Executable("dotnet"),
            [Path.Combine(runtime, names[0]), "worker", "--workspace", store.Workspace, "--pr", Path.GetFileName(store.Folder)[3..]], store.Workspace))
            ?? throw new IOException("Could not start watcher worker.");
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token); timeout.CancelAfter(TimeSpan.FromSeconds(15));
        while (true)
        {
            using (await store.LockAsync("state", timeout.Token))
            {
                var state = store.Load()!;
                if (!state.Active || CommandRunner.Alive(state.WorkerPid, state.WorkerStartTicks)) return;
            }
            if (child.HasExited && child.ExitCode != 0) throw new IOException("Watcher worker exited before becoming ready. Use status for saved diagnostics.");
            await Task.Delay(100, timeout.Token);
        }
    }
}
