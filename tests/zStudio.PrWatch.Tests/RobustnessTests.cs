using System.Diagnostics;
using Xunit;
using static Recoil.Zbd.PrWatch.Tests.WatcherTests;

namespace Recoil.Zbd.PrWatch.Tests;

public sealed class RobustnessTests
{
    // Readers such as editors, scanners and Get-Content open without delete sharing and block atomic replacement.
    private static FileStream Reader(WatchStore store) => new(store.StatePath, FileMode.Open, FileAccess.Read, FileShare.Read);

    [Fact]
    public async Task PollTreatsABlockedStateReplacementAsRetryable()
    {
        using var fixture = new Fixture(); fixture.Store.Save(fixture.NewState()); var queue = new FakeQueue();
        var store = new WatchStore(fixture.Root, 14) { ReplaceTimeout = TimeSpan.FromMilliseconds(100) };
        var service = new WatchService(store, new FakeSource(Observe(Comment(1))), queue);
        TimeSpan delay;
        using (Reader(store)) delay = await service.PollAsync(TestContext.Current.CancellationToken);
        Assert.True(delay > TimeSpan.Zero); Assert.Equal(0, queue.Adds);
        var state = store.Load()!; Assert.True(state.Active); Assert.True(state.CommentsArmed); Assert.Empty(state.Notices);
        Assert.Equal(TimeSpan.FromSeconds(60), await service.PollAsync(TestContext.Current.CancellationToken));
        Assert.Equal(1, queue.Adds); Assert.Equal("queued", store.Load()!.Outstanding!.Delivery);
    }

    [Fact]
    public async Task AShortLivedReaderOnlyDelaysPublication()
    {
        using var fixture = new Fixture(); fixture.Store.Save(fixture.NewState()); var queue = new FakeQueue();
        var reader = Reader(fixture.Store);
        var release = Task.Run(async () => { await Task.Delay(300); await reader.DisposeAsync(); }, TestContext.Current.CancellationToken);
        Assert.Equal(TimeSpan.FromSeconds(60), await new WatchService(fixture.Store, new FakeSource(Observe(Comment(1))), queue).PollAsync(TestContext.Current.CancellationToken));
        await release;
        Assert.Equal(1, queue.Adds); Assert.Equal("queued", fixture.Store.Load()!.Outstanding!.Delivery);
    }

    [Fact]
    public void EachArmRecordsExactlyTheSuppliedReleaseAuthorization()
    {
        var state = State();
        WatchLogic.Arm(state, Observe(), Head, true, Now, releaseAuthorized: true); Assert.True(state.ReleaseAuthorized);
        WatchLogic.Arm(state, Observe() with { Head = Next }, Next, false, Now); Assert.False(state.ReleaseAuthorized);
    }

    [Fact]
    public void StatusWarnsWhenAnActiveWatchHasNoWorker()
    {
        var state = State(); state.ObservedHead = Head;
        Assert.Contains("worker is not running", WatchLogic.StatusWarning(state, false));
        Assert.Null(WatchLogic.StatusWarning(state, true));
        state.Active = false; Assert.Null(WatchLogic.StatusWarning(state, false));
    }

    [Fact]
    public async Task ProbeReceiptsKeepDeliveryAndCleanupOutcomesSeparate()
    {
        using var fixture = new Fixture();
        await Assert.ThrowsAsync<IOException>(() => WatchService.NotifyTestAsync(fixture.Store, new FakeQueue { FailRemove = true }, Guid.NewGuid(), TestContext.Current.CancellationToken));
        var removal = Probes(fixture).Single();
        Assert.Equal("queued", removal.Delivery); Assert.Equal("submission", removal.Submission); Assert.StartsWith("uncertain:", removal.Cleanup); Assert.Null(removal.Acknowledged);
        using var failedAdd = new Fixture();
        await Assert.ThrowsAsync<IOException>(() => WatchService.NotifyTestAsync(failedAdd.Store, new FakeQueue { Fail = true }, Guid.NewGuid(), TestContext.Current.CancellationToken));
        var add = Probes(failedAdd).Single(); Assert.Equal("unknown", add.Delivery); Assert.Null(add.Cleanup);
        using var success = new Fixture();
        var (probe, path) = await WatchService.NotifyTestAsync(success.Store, new FakeQueue(), Guid.NewGuid(), TestContext.Current.CancellationToken);
        Assert.Equal("queued", probe.Delivery); Assert.Equal("absent", probe.Cleanup); Assert.NotNull(probe.Acknowledged); Assert.True(File.Exists(path));
    }

    [Fact]
    public async Task RuntimeCopyReplacesAnInterruptedCopyAtomically()
    {
        using var fixture = new Fixture(); string source = Path.Combine(fixture.Root, "build"); Directory.CreateDirectory(source);
        string[] names = ["a.dll", "a.deps.json"];
        await File.WriteAllBytesAsync(Path.Combine(source, names[0]), Enumerable.Range(0, 4096).Select(i => (byte)i).ToArray(), TestContext.Current.CancellationToken);
        await File.WriteAllTextAsync(Path.Combine(source, names[1]), "{}", TestContext.Current.CancellationToken);
        string runtime = await WatchService.PrepareRuntimeAsync(fixture.Store, source, names, TestContext.Current.CancellationToken);
        await File.WriteAllBytesAsync(Path.Combine(runtime, names[0]), new byte[17], TestContext.Current.CancellationToken); // Interrupted earlier copy.
        Assert.Equal(runtime, await WatchService.PrepareRuntimeAsync(fixture.Store, source, names, TestContext.Current.CancellationToken));
        foreach (string name in names)
            Assert.Equal(await File.ReadAllBytesAsync(Path.Combine(source, name), TestContext.Current.CancellationToken), await File.ReadAllBytesAsync(Path.Combine(runtime, name), TestContext.Current.CancellationToken));
        Assert.Equal(names.Length, Directory.GetFiles(runtime).Length);
    }

    [Fact]
    public void SnapshotsSavedBeforeInformationalClassificationStillLoad()
    {
        // The pre-classification record shape: comments carry no Informational property.
        string json = System.Text.Json.JsonSerializer.Serialize(new
        {
            Id = Guid.NewGuid(), Watch = Guid.NewGuid(), Notice = (Guid?)null, Created = Now,
            Observation = new { Head, Open = true, Comments = new[] { new { Key = "conversation:1", Url = "u", Author = "a", Body = "@codex review", Published = Now } }, Approval = (Approval?)null }
        });
        Assert.DoesNotContain("Informational", json);
        var snapshot = System.Text.Json.JsonSerializer.Deserialize<ReadSnapshot>(json, WatchStore.Json)!;
        Assert.False(snapshot.Observation.Comments.Single().Informational);
        var state = State(); state.CommentsArmed = false;
        var notice = WatchLogic.Observe(state, Observe() with { Approval = new(Head, 5, Now, "summary") }, Now)!;
        WatchLogic.Acknowledge(state, snapshot with { Watch = state.Id, Notice = notice.Id, Created = Now }, notice.Id, Now);
        Assert.Contains("conversation:1", state.Handled);
    }

    private static Notice[] Probes(Fixture fixture) => Directory.GetFiles(Path.Combine(fixture.Store.Folder, "probes"))
        .Select(p => System.Text.Json.JsonSerializer.Deserialize<Notice>(File.ReadAllText(p), WatchStore.Json)!).ToArray();

    [Fact]
    public async Task WorkerKeepsRunningWhileAReaderBlocksStateReplacement()
    {
        using var fixture = new Fixture(); fixture.Store.Save(fixture.NewState()); var queue = new FakeQueue();
        var store = new WatchStore(fixture.Root, 14) { ReplaceTimeout = TimeSpan.FromMilliseconds(50) };
        using var cancel = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        Task run;
        using (Reader(store))
        {
            // Registration fails several times while the reader holds the record.
            run = Task.Run(() => new WatchService(store, new FakeSource(Observe(Comment(1))), queue, new FastTime()).RunAsync(cancel.Token), TestContext.Current.CancellationToken);
            await Task.Delay(400, TestContext.Current.CancellationToken);
            Assert.False(run.IsCompleted, run.Exception?.InnerException?.Message);
            Assert.Equal(0, store.Load()!.WorkerPid);
        }
        var deadline = Stopwatch.StartNew();
        while (Volatile.Read(ref queue.Adds) == 0 && deadline.Elapsed < TimeSpan.FromSeconds(30)) await Task.Delay(20, TestContext.Current.CancellationToken);
        await cancel.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => run);
        var state = fixture.Store.Load()!;
        Assert.Equal(1, queue.Adds); Assert.Single(state.Notices); Assert.Equal(Environment.ProcessId, state.WorkerPid);
    }

    /// <summary>Scales time 1000:1 so backoff and one-second wake-ups remain deterministic without real minutes.</summary>
    internal sealed class FastTime : TimeProvider
    {
        private readonly DateTimeOffset start = DateTimeOffset.UtcNow; private readonly long ticks = Stopwatch.GetTimestamp();
        public override DateTimeOffset GetUtcNow() => start + Stopwatch.GetElapsedTime(ticks) * 1000;
        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period) =>
            System.CreateTimer(callback, state, Scale(dueTime), Scale(period));
        private static TimeSpan Scale(TimeSpan value) => value == Timeout.InfiniteTimeSpan ? value : value / 1000;
    }
}
