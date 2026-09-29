using System.Diagnostics;
using System.Security.Cryptography;

namespace Recoil.Zbd.PrWatch;

public sealed class WatchService(WatchStore store, IPrSource source, INoticeQueue queue, TimeProvider? time = null,
    Func<WatchState, IPrSource>? sourceFactory = null, Func<WatchState, INoticeQueue>? queueFactory = null)
{
    private readonly TimeProvider clock = time ?? TimeProvider.System;
    public async Task<TimeSpan> PollAsync(CancellationToken token)
    {
        WatchState before;
        using (await store.LockAsync("state", token))
        {
            before = store.Load() ?? throw new InvalidOperationException("No watch exists.");
            if (!before.Active) return TimeSpan.Zero;
            before.Heartbeat = clock.GetUtcNow(); store.Save(before);
        }
        try
        {
            var observation = await (sourceFactory?.Invoke(before) ?? source).ReadAsync(before.Repository, before.Pr, token);
            // Serialize publication and queue submission with stop/ack/re-arm. Stop cannot race a send.
            using (await store.LockAsync("state", token))
            {
                var state = store.Load()!;
                if (state.Id != before.Id || state.Generation != before.Generation || !state.Active) return TimeSpan.FromSeconds(1);
                var notice = WatchLogic.Observe(state, observation, clock.GetUtcNow(), store.StatePath);
                state.Heartbeat = clock.GetUtcNow(); store.Save(state);
                if (notice != null)
                {
                    try { notice.Submission = await (queueFactory?.Invoke(state) ?? queue).AddAsync(state.Thread, notice, token); notice.Delivery = "queued"; }
                    catch (Exception ex) when (ex is not OutOfMemoryException and not StackOverflowException)
                    { notice.Delivery = "unknown"; notice.Error = Short(ex.Message); }
                    store.Save(state);
                }
                return state.Active ? TimeSpan.FromSeconds(60) : TimeSpan.Zero;
            }
        }
        catch (Exception ex) when (ex is not OutOfMemoryException and not StackOverflowException && !token.IsCancellationRequested)
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
            using (await store.LockAsync("state", token))
            {
                var state = store.Load() ?? throw new InvalidOperationException("No watch exists.");
                if (!state.Active) return;
                state.WorkerPid = process.Id; state.WorkerStartTicks = process.StartTime.ToUniversalTime().Ticks;
                state.Heartbeat = clock.GetUtcNow(); store.Save(state);
            }
            while (!token.IsCancellationRequested)
            {
                var delay = await PollAsync(token); if (delay == TimeSpan.Zero) break;
                // Wake promptly for stop/re-arm even during backoff. A new generation resets the delay.
                int generation;
                using (await store.LockAsync("state", token)) generation = store.Load()!.Generation;
                DateTimeOffset due = clock.GetUtcNow() + delay;
                while (clock.GetUtcNow() < due)
                {
                    await Task.Delay(TimeSpan.FromSeconds(1), clock, token);
                    using (await store.LockAsync("state", token))
                    { var state = store.Load()!; if (!state.Active) return; if (state.Generation != generation) break; }
                }
            }
        }
    }
    public static async Task StartWorkerAsync(WatchStore store, CancellationToken token)
    {
        using var launch = await store.LockAsync("launch", token);
        // Run a private immutable copy so subsequent solution builds do not hit locked tool binaries.
        string baseFolder = AppContext.BaseDirectory;
        string[] names = ["Recoil.Zbd.PrWatch.dll", "Recoil.Zbd.PrWatch.deps.json", "Recoil.Zbd.PrWatch.runtimeconfig.json"];
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        foreach (string name in names) hash.AppendData(await File.ReadAllBytesAsync(Path.Combine(baseFolder, name), token));
        string runtime = Path.Combine(store.Folder, "runtime", Convert.ToHexString(hash.GetHashAndReset()));
        store.EnsureSafe(runtime); Directory.CreateDirectory(runtime);
        foreach (string name in names)
        {
            string target = Path.Combine(runtime, name); store.EnsureSafe(target);
            byte[] bytes = await File.ReadAllBytesAsync(Path.Combine(baseFolder, name), token);
            if (File.Exists(target))
            { var existing = await File.ReadAllBytesAsync(target, token); if (!bytes.AsSpan().SequenceEqual(existing)) throw new IOException("Worker runtime copy changed."); }
            else await File.WriteAllBytesAsync(target, bytes, token);
        }
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
