using Xunit;
using static Recoil.Zbd.PrWatch.Tests.WatcherTests;

namespace Recoil.Zbd.PrWatch.Tests;

/// <summary>Foreground (Claude Code Monitor) delivery: one notice per settled burst, durable claims and channel isolation.</summary>
public sealed class ListenTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Fact]
    public void CodexCannotArmTheForegroundOnlyChannel()
    {
        Assert.Throws<InvalidOperationException>(() => WatchLogic.ValidateChannel(true, Guid.NewGuid().ToString()));
        WatchLogic.ValidateChannel(false, Guid.NewGuid().ToString());
        WatchLogic.ValidateChannel(true, null);
    }

    [Fact]
    public async Task ListenerIdentityIsLiveOnlyWhileListeningAndClearedOnCancellation()
    {
        using var fixture = new ClaudeFixture(); fixture.Save(fixture.NewState());
        using var cancel = CancellationTokenSource.CreateLinkedTokenSource(Token);
        TaskCompletionSource entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        var source = new HeldSource(entered);
        var service = new WatchService(fixture.Store, source, new FakeQueue());
        var listening = service.ListenAsync(cancel.Token);
        await entered.Task.WaitAsync(Token);
        var state = fixture.Store.Load()!;
        Assert.True(CommandRunner.Alive(state.WorkerPid, state.WorkerStartTicks));
        await Assert.ThrowsAsync<IOException>(() => service.ListenAsync(Token));
        cancel.Cancel(); await Assert.ThrowsAnyAsync<OperationCanceledException>(() => listening);
        state = fixture.Store.Load()!;
        Assert.False(CommandRunner.Alive(state.WorkerPid, state.WorkerStartTicks));
        Assert.Contains("not running", WatchLogic.StatusWarning(state, false));
    }
    private sealed class HeldSource(TaskCompletionSource entered) : IPrSource
    {
        public async Task<Observation> ReadAsync(string repository, int pr, CancellationToken token)
        { entered.TrySetResult(); await Task.Delay(Timeout.InfiniteTimeSpan, token); return Observe(); }
    }

    [Fact]
    public async Task AGrowingBurstProducesOneNoticeAfterItSettles()
    {
        using var fixture = new ClaudeFixture(); fixture.Save(fixture.NewState());
        var a = Comment(1); var b = Comment(2, "review"); var c = Comment(3, "conversation");
        var source = new ScriptedSource(Observe(a), Observe(a, b), Observe(a, b, c));
        var queue = new FakeQueue(); var service = new WatchService(fixture.Store, source, queue, new SteppedTime());
        var result = await service.ListenAsync(Token);
        Assert.Equal("notice", result.Outcome); Assert.Equal(["comments"], result.Notice!.Reasons);
        Assert.True(source.Reads >= 4, $"Only {source.Reads} reads; the burst was not allowed to settle.");
        Assert.Equal(0, queue.Adds);
        var state = fixture.Store.Load()!;
        Assert.Equal(0, state.WorkerPid);
        Assert.Single(state.Notices); Assert.False(state.CommentsArmed); Assert.Equal("claimed", state.Notices[0].Delivery);
        await service.MarkEmittedAsync(result.Notice, Token);
        Assert.Equal("emitted", fixture.Store.Load()!.Notices[0].Delivery);
        // No second notification until the reviewed snapshot is acknowledged and the watch re-armed.
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.ListenAsync(Token));
        state = fixture.Store.Load()!;
        WatchLogic.Acknowledge(state, new(Guid.NewGuid(), state.Id, result.Notice.Id, result.Notice.Created.AddSeconds(1), Observe(a, b, c)), result.Notice.Id, result.Notice.Created.AddSeconds(2));
        var d = Comment(4); var afterPush = Observe(a, b, c, d) with { Head = Next };
        WatchLogic.Arm(state, afterPush, Next, false, DateTimeOffset.UtcNow); fixture.Save(state);
        var later = await new WatchService(fixture.Store, new ScriptedSource(afterPush), queue, new SteppedTime()).ListenAsync(Token);
        Assert.Equal("notice", later.Outcome); Assert.Equal(2, fixture.Store.Load()!.Notices.Count);
        Assert.DoesNotContain(d.Key, fixture.Store.Load()!.Handled);
    }

    [Fact]
    public async Task InformationalCommentsNeverNotify()
    {
        using var fixture = new ClaudeFixture(); fixture.Save(fixture.NewState());
        var request = Comment(1, "conversation") with { Informational = true };
        // The next ReadAsync cannot begin until the preceding poll's state was saved. Four completed polls also
        // span the normal 45-second settle window if the comment is accidentally treated as review feedback.
        var source = new PollingBarrierSource(Observe(request), completedPolls: 4);
        var queue = new FakeQueue(); var clock = new SteppedTime(); var started = clock.GetUtcNow();
        using var cancel = CancellationTokenSource.CreateLinkedTokenSource(Token);
        var listening = new WatchService(fixture.Store, source, queue, clock).ListenAsync(cancel.Token);
        try
        {
            // A mistaken notice (or another early exit) fails immediately instead of hanging at the barrier.
            Assert.Same(source.Waiting, await Task.WhenAny(source.Waiting, listening).WaitAsync(Token));
            var observed = fixture.Store.Load()!;
            Assert.Equal(5, source.Reads);
            Assert.Equal(started.AddMinutes(3), observed.LastSuccess);
            Assert.Equal(observed.LastSuccess, observed.Heartbeat);
            Assert.Equal(Head, observed.ObservedHead);
            Assert.True(CommandRunner.Alive(observed.WorkerPid, observed.WorkerStartTicks));
            Assert.Empty(observed.Notices); Assert.True(observed.CommentsArmed);
        }
        finally
        {
            cancel.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => listening);
        }
        var state = fixture.Store.Load()!;
        Assert.Empty(state.Notices); Assert.True(state.CommentsArmed); Assert.True(state.Active);
        Assert.Equal(0, queue.Adds);
        Assert.Equal(0, state.WorkerPid); Assert.Equal(0, state.WorkerStartTicks);
    }

    [Fact]
    public async Task AContinuousStreamIsBoundedByTheMaximumSettle()
    {
        using var fixture = new ClaudeFixture(); fixture.Save(fixture.NewState());
        var source = new GrowingSource();
        var result = await new WatchService(fixture.Store, source, new FakeQueue(), new SteppedTime()).ListenAsync(Token, settle: TimeSpan.FromSeconds(45), maximumSettle: TimeSpan.FromMinutes(2));
        Assert.Equal("notice", result.Outcome);
        Assert.Equal(9, source.Reads); // Reads at 0, 15, …, 120 seconds: the two-minute maximum ends the settle, then one notice.
        Assert.Single(fixture.Store.Load()!.Notices);
    }

    [Fact]
    public async Task ClosedPullRequestsAndApprovalsEndListeningWithoutRepeats()
    {
        using var closed = new ClaudeFixture(); closed.Save(closed.NewState());
        var ended = await new WatchService(closed.Store, new ScriptedSource(Observe(Comment(1)) with { Open = false }), new FakeQueue(), new SteppedTime()).ListenAsync(Token);
        Assert.Equal("closed", ended.Outcome); Assert.Null(ended.Notice); Assert.False(closed.Store.Load()!.Active);
        Assert.Equal("stopped", (await new WatchService(closed.Store, new ScriptedSource(Observe()), new FakeQueue()).ListenAsync(Token)).Outcome);

        using var approved = new ClaudeFixture(); var state = approved.NewState(); state.CommentsArmed = false; approved.Save(state);
        var approval = Observe() with { Approval = new(Head, 5, DateTimeOffset.UtcNow, "summary") };
        var notice = await new WatchService(approved.Store, new ScriptedSource(approval), new FakeQueue(), new SteppedTime()).ListenAsync(Token);
        Assert.Equal(["approval"], notice.Notice!.Reasons);
    }

    [Fact]
    public void TheClaudeChannelIsIndependentOfTheCodexWatch()
    {
        using var fixture = new ClaudeFixture(); fixture.Save(fixture.NewState());
        Assert.EndsWith("pr-14-claude", fixture.Store.Folder, StringComparison.Ordinal);
        var codex = new WatchStore(fixture.Root, 14);
        Assert.Null(codex.Load());
        Assert.NotEqual(codex.StatePath, fixture.Store.StatePath);
        Assert.Throws<ArgumentException>(() => new WatchStore(fixture.Root, 14, "other"));
    }

    private sealed class ClaudeFixture : IDisposable
    {
        public string Root { get; } = Path.Combine(Path.GetTempPath(), "zstudio-pr-listen-" + Guid.NewGuid().ToString("N"));
        public WatchStore Store { get; }
        public ClaudeFixture() { Directory.CreateDirectory(Root); Store = new(Root, 14, "claude"); }
        public WatchState NewState() { var state = State(); state.Workspace = Root; return state; }
        public void Save(WatchState state) => Store.Save(state);
        public void Dispose() => Directory.Delete(Root, true);
    }
    /// <summary>Returns each observation in turn, then repeats the last one.</summary>
    private sealed class ScriptedSource(params Observation[] script) : IPrSource
    {
        private int reads;
        public int Reads => Volatile.Read(ref reads);
        public Task<Observation> ReadAsync(string repository, int pr, CancellationToken token)
        { int index = Interlocked.Increment(ref reads) - 1; return Task.FromResult(script[Math.Min(index, script.Length - 1)]); }
    }
    /// <summary>Blocks the next read after a known number of complete service polls, until the test cancels.</summary>
    private sealed class PollingBarrierSource(Observation observation, int completedPolls) : IPrSource
    {
        private int reads;
        private readonly TaskCompletionSource waiting = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int Reads => Volatile.Read(ref reads);
        public Task Waiting => waiting.Task;
        public async Task<Observation> ReadAsync(string repository, int pr, CancellationToken token)
        {
            if (Interlocked.Increment(ref reads) > completedPolls)
            {
                waiting.TrySetResult();
                await Task.Delay(Timeout.InfiniteTimeSpan, token);
            }
            return observation;
        }
    }
    /// <summary>Deterministic time: each delay advances the clock by exactly its due time and completes at once, so settle
    /// windows count polls rather than depending on how long real file I/O takes on the test machine.</summary>
    private sealed class SteppedTime : TimeProvider
    {
        private long ticks = DateTimeOffset.UtcNow.UtcTicks;
        public override DateTimeOffset GetUtcNow() => new(Interlocked.Read(ref ticks), TimeSpan.Zero);
        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            if (dueTime > TimeSpan.Zero && dueTime != Timeout.InfiniteTimeSpan) Interlocked.Add(ref ticks, dueTime.Ticks);
            return System.CreateTimer(callback, state, TimeSpan.Zero, Timeout.InfiniteTimeSpan);
        }
    }
    /// <summary>A comment arrives before every read, so the set never settles on its own.</summary>
    private sealed class GrowingSource : IPrSource
    {
        private int reads;
        public int Reads => Volatile.Read(ref reads);
        public Task<Observation> ReadAsync(string repository, int pr, CancellationToken token)
        { int count = Interlocked.Increment(ref reads); return Task.FromResult(Observe(Enumerable.Range(1, count).Select(i => Comment(i)).ToArray())); }
    }
}
