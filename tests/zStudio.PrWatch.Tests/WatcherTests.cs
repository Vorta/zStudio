using Xunit;

namespace Recoil.Zbd.PrWatch.Tests;

public sealed class WatcherTests
{
    internal const string Head = "1111111111111111111111111111111111111111";
    internal const string Next = "2222222222222222222222222222222222222222";
    internal static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-09-29T12:00:00Z");
    internal static Feedback Comment(int id, string kind = "inline") => new(kind + ":" + id, "https://github.com/o/r/pull/14#" + id, "reviewer", "Review " + id, Now);
    internal static Observation Observe(params Feedback[] comments) => new(Head, true, comments, null);
    internal static WatchState State() => new() { Workspace = "unused", Repository = "o/r", Pr = 14, Thread = Guid.NewGuid(), ExpectedHead = Head, Active = true, CommentsArmed = true };

    [Fact]
    public void ACommentBurstDisarmsBeforeSubmissionAndCannotRepeat()
    {
        var state = State(); var observation = Observe(Enumerable.Range(1, 500).Select(i => Comment(i)).ToArray());
        var notice = WatchLogic.Observe(state, observation, Now);
        Assert.NotNull(notice); Assert.False(state.CommentsArmed); Assert.Equal("claimed", notice.Delivery);
        Assert.Null(WatchLogic.Observe(state, observation, Now.AddMinutes(1)));
        Assert.Single(state.Notices); Assert.Empty(state.Handled);
    }

    [Theory]
    [InlineData("conversation")][InlineData("inline")][InlineData("review")]
    public void NewIdentitiesTriggerRegardlessOfTimestampOrSource(string kind)
    {
        var state = State(); state.Handled.Add(kind + ":100");
        Assert.NotNull(WatchLogic.Observe(state, Observe(Comment(1, kind) with { Published = Now.AddDays(-1) }), Now));
    }

    [Fact]
    public void ChangedTextOnAnExistingIdentityDoesNotTrigger()
    {
        var state = State(); state.Handled.Add("inline:1");
        Assert.Null(WatchLogic.Observe(state, Observe(Comment(1) with { Body = "edited" }), Now));
        Assert.True(state.CommentsArmed);
    }

    [Fact]
    public void AcknowledgmentOnlyConsumesTheReadSnapshotAndRearmKeepsLateFeedback()
    {
        var state = State(); var first = Observe(Comment(1));
        var notice = WatchLogic.Observe(state, first, Now)!;
        var snapshot = new ReadSnapshot(Guid.NewGuid(), state.Id, notice.Id, Now, first);
        WatchLogic.Acknowledge(state, snapshot, notice.Id, Now.AddSeconds(5));
        Assert.False(state.CommentsArmed); // Informational-only handling stays disarmed.
        var afterPush = Observe(Comment(1), Comment(2)) with { Head = Next };
        WatchLogic.Arm(state, afterPush, Next, false, Now.AddSeconds(10));
        Assert.DoesNotContain("inline:2", state.Handled);
        Assert.NotNull(WatchLogic.Observe(state, afterPush, Now.AddSeconds(11)));
        Assert.Equal(2, state.Notices.Count);
    }

    [Fact]
    public void InitialArmBaselinesHistoryButRearmRequiresAnAcknowledgment()
    {
        var state = State(); WatchLogic.Arm(state, Observe(Comment(1)), Head, true, Now);
        Assert.Null(WatchLogic.Observe(state, Observe(Comment(1)), Now));
        WatchLogic.Observe(state, Observe(Comment(2)), Now);
        Assert.Throws<InvalidOperationException>(() => WatchLogic.Arm(state, Observe(Comment(2)), Head, false, Now));
    }

    [Fact]
    public void ApprovalContinuesWhileCommentsAreDisarmedAndIsOncePerHead()
    {
        var state = State(); state.CommentsArmed = false;
        var approved = Observe(Comment(1)) with { Approval = new(Head, 5, Now, "summary") };
        var notice = WatchLogic.Observe(state, approved, Now)!;
        Assert.Equal(["approval"], notice.Reasons);
        WatchLogic.Acknowledge(state, new(Guid.NewGuid(), state.Id, notice.Id, Now, approved), notice.Id, Now);
        Assert.Null(WatchLogic.Observe(state, approved, Now.AddMinutes(1)));
        Assert.False(state.CommentsArmed);
    }

    [Fact]
    public void SimultaneousFeedbackAndApprovalShareOneNotice()
    {
        var state = State(); var both = Observe(Comment(1)) with { Approval = new(Head, 5, Now, "summary") };
        Assert.Equal(["comments", "approval"], WatchLogic.Observe(state, both, Now)!.Reasons);
        Assert.Single(state.Notices);
    }

    [Fact]
    public void ApprovalArrivingDuringOutstandingFeedbackIsNotLost()
    {
        var state = State(); var first = Observe(Comment(1));
        var notice = WatchLogic.Observe(state, first, Now)!;
        var approved = first with { Approval = new(Head, 5, Now, "summary") };
        Assert.Null(WatchLogic.Observe(state, approved, Now));
        Assert.Empty(state.ApprovalNotifiedHeads);
        WatchLogic.Acknowledge(state, new(Guid.NewGuid(), state.Id, notice.Id, Now, first), notice.Id, Now);
        Assert.Equal(["approval"], WatchLogic.Observe(state, approved, Now)!.Reasons);
    }

    [Fact]
    public void ChangedHeadCannotReuseApprovalAndClosedPrStopsBothChannels()
    {
        var state = State(); state.CommentsArmed = false;
        var other = Observe() with { Head = Next, Approval = new(Next, 5, Now, "summary") };
        Assert.Null(WatchLogic.Observe(state, other, Now));
        Assert.Null(WatchLogic.Observe(state, other with { Open = false }, Now));
        Assert.False(state.Active); Assert.False(state.ApprovalEnabled);
        Assert.Throws<InvalidOperationException>(() => WatchLogic.Arm(state, other with { Open = false }, Next, false, Now));
    }

    [Fact]
    public void ForeignOrOldSnapshotsCannotAcknowledgeANotice()
    {
        var state = State(); var observation = Observe(Comment(1)); var notice = WatchLogic.Observe(state, observation, Now)!;
        Assert.Throws<InvalidOperationException>(() => WatchLogic.Acknowledge(state, new(Guid.NewGuid(), Guid.NewGuid(), notice.Id, Now, observation), notice.Id, Now));
        Assert.Throws<InvalidOperationException>(() => WatchLogic.Acknowledge(state, new(Guid.NewGuid(), state.Id, notice.Id, Now.AddSeconds(-1), observation), notice.Id, Now));
        Assert.Empty(state.Handled); Assert.Null(notice.Acknowledged);
    }

    [Fact]
    public void BackoffHonorsRetryAfterAndHasAnOrdinaryCap()
    {
        Assert.Equal(TimeSpan.FromSeconds(60), WatchLogic.RetryDelay(1));
        Assert.Equal(TimeSpan.FromMinutes(10), WatchLogic.RetryDelay(100));
        Assert.Equal(TimeSpan.FromHours(1), WatchLogic.RetryDelay(1, TimeSpan.FromHours(1)));
    }

    [Theory]
    [InlineData("claimed")][InlineData("unknown")][InlineData("queued")]
    public async Task RestartNeverResendsADurableClaim(string delivery)
    {
        using var fixture = new Fixture(); var state = fixture.NewState();
        var notice = WatchLogic.Observe(state, Observe(Comment(1)), Now)!;
        notice.Delivery = delivery; fixture.Store.Save(state);
        var queue = new FakeQueue();
        await new WatchService(fixture.Store, new FakeSource(Observe(Comment(1), Comment(2))), queue).PollAsync(TestContext.Current.CancellationToken);
        Assert.Equal(0, queue.Adds); Assert.Single(fixture.Store.Load()!.Notices);
    }

    [Fact]
    public async Task ConcurrentPollsSubmitOnlyOneNotice()
    {
        using var fixture = new Fixture(); fixture.Store.Save(fixture.NewState()); var queue = new FakeQueue();
        var service = new WatchService(fixture.Store, new FakeSource(Observe(Comment(1))), queue);
        await Task.WhenAll(service.PollAsync(TestContext.Current.CancellationToken), service.PollAsync(TestContext.Current.CancellationToken));
        Assert.Equal(1, queue.Adds); Assert.Single(fixture.Store.Load()!.Notices);
    }

    [Fact]
    public async Task SendFailureLeavesAnUncertainClaimAndNoAutomaticRetry()
    {
        using var fixture = new Fixture(); fixture.Store.Save(fixture.NewState()); var queue = new FakeQueue { Fail = true };
        var service = new WatchService(fixture.Store, new FakeSource(Observe(Comment(1))), queue);
        await service.PollAsync(TestContext.Current.CancellationToken); await service.PollAsync(TestContext.Current.CancellationToken);
        Assert.Equal(1, queue.Adds); Assert.Equal("unknown", fixture.Store.Load()!.Outstanding!.Delivery);
    }

    [Fact]
    public async Task StopDuringFetchPreventsAnyQueueSideEffect()
    {
        using var fixture = new Fixture(); fixture.Store.Save(fixture.NewState()); var queue = new FakeQueue();
        TaskCompletionSource<Observation> pending = new(TaskCreationOptions.RunContinuationsAsynchronously);
        var source = new FakeSource(Observe()) { Pending = pending };
        var poll = new WatchService(fixture.Store, source, queue).PollAsync(TestContext.Current.CancellationToken);
        await source.Started.Task.WaitAsync(TestContext.Current.CancellationToken);
        using (await fixture.Store.LockAsync("state", TestContext.Current.CancellationToken)) { var state = fixture.Store.Load()!; state.Active = false; state.Generation++; fixture.Store.Save(state); }
        pending.SetResult(Observe(Comment(1))); await poll;
        Assert.Equal(0, queue.Adds); Assert.Empty(fixture.Store.Load()!.Notices);
    }

    [Fact]
    public async Task FailedFetchDoesNotConsumeCommentsOrDisarm()
    {
        using var fixture = new Fixture(); fixture.Store.Save(fixture.NewState());
        var source = new FakeSource(Observe()) { Failure = new GitHubException("rate limit", TimeSpan.FromMinutes(17)) };
        var delay = await new WatchService(fixture.Store, source, new FakeQueue()).PollAsync(TestContext.Current.CancellationToken);
        var state = fixture.Store.Load()!;
        Assert.True(state.CommentsArmed); Assert.Empty(state.Handled); Assert.Empty(state.Notices);
        Assert.Equal(TimeSpan.FromMinutes(17), delay); Assert.Contains("rate limit", state.LastError);
    }

    [Fact]
    public async Task StorePreservesClaimsAndUsesExclusiveProcessLocks()
    {
        using var fixture = new Fixture(); var state = fixture.NewState(); fixture.Store.Save(state);
        using (await fixture.Store.LockAsync("worker", TestContext.Current.CancellationToken))
            await Assert.ThrowsAsync<IOException>(() => fixture.Store.LockAsync("worker", TestContext.Current.CancellationToken, false));
        using (await fixture.Store.LockAsync("worker", TestContext.Current.CancellationToken, false)) { }
        Assert.Equal(state.Id, fixture.Store.Load()!.Id);
        var notice = WatchLogic.Observe(state, Observe(Comment(1)), Now)!; fixture.Store.Save(state);
        Assert.Equal(notice.Id, new WatchStore(fixture.Root, 14).Load()!.Outstanding!.Id);
        Assert.Throws<IOException>(() => fixture.Store.EnsureSafe(Path.GetDirectoryName(fixture.Root)!));
    }

    [Fact]
    public void RejectedStatePublicationRetainsTheLastReadableClaim()
    {
        using var fixture = new Fixture(); var state = fixture.NewState();
        var notice = WatchLogic.Observe(state, Observe(Comment(1)), Now)!;
        fixture.Store.Save(state);
        state.Handled.UnionWith(Enumerable.Range(0, 30001).Select(i => "inline:" + i));
        notice.Acknowledged = Now;
        Assert.Throws<InvalidDataException>(() => fixture.Store.Save(state));
        var retained = fixture.Store.Load()!;
        Assert.Empty(retained.Handled); Assert.Equal(notice.Id, retained.Outstanding!.Id);
    }

    internal sealed class Fixture : IDisposable
    {
        public string Root { get; } = Path.Combine(Path.GetTempPath(), "zstudio-pr-watch-" + Guid.NewGuid().ToString("N"));
        public WatchStore Store { get; }
        public Fixture() { Directory.CreateDirectory(Root); Store = new(Root, 14); }
        public WatchState NewState() { var state = State(); state.Workspace = Root; return state; }
        public void Dispose() => Directory.Delete(Root, true);
    }
    internal sealed class FakeSource(Observation observation) : IPrSource
    {
        public TaskCompletionSource<Observation>? Pending { get; init; }
        public Exception? Failure { get; init; }
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Task<Observation> ReadAsync(string repository, int pr, CancellationToken token)
        { Started.TrySetResult(); return Failure != null ? Task.FromException<Observation>(Failure) : Pending?.Task ?? Task.FromResult(observation); }
    }
    internal sealed class FakeQueue : INoticeQueue
    {
        public int Adds; public bool Fail { get; init; } public bool FailRemove { get; init; }
        public Task CheckAsync(Guid thread, CancellationToken token) => Task.CompletedTask;
        public Task<string> RemoveAsync(Guid thread, Notice notice, CancellationToken token) =>
            FailRemove ? Task.FromException<string>(new IOException("simulated queue list failure")) : Task.FromResult("absent");
        public Task<string> AddAsync(Guid thread, Notice notice, CancellationToken token)
        { Interlocked.Increment(ref Adds); return Fail ? Task.FromException<string>(new IOException("simulated broken pipe")) : Task.FromResult("submission"); }
    }
}
