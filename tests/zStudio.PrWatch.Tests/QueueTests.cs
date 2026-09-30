using System.Text.Json.Nodes;
using Xunit;

namespace Recoil.Zbd.PrWatch.Tests;

public sealed class QueueTests
{
    [Fact]
    public async Task SubmissionVerifiesIdentityAndExactContent()
    {
        var notice = new Notice { Message = "Fixed notice with a path D:\\folder with spaces\\state.json" };
        Guid thread = Guid.NewGuid(); var rpc = new FakeRpc();
        var queue = Queue(rpc);
        Assert.Equal("queued-id", await queue.AddAsync(thread, notice, TestContext.Current.CancellationToken));
        Assert.Equal("thread/queue/add", rpc.Method);
        Assert.Equal(thread.ToString("D"), rpc.Args!["threadId"]!.GetValue<string>());
        Assert.Equal(notice.Id.ToString("D"), rpc.Args["clientUserMessageId"]!.GetValue<string>());
        Assert.Equal(notice.Message, rpc.Args["input"]![0]!["text"]!.GetValue<string>());
        Assert.True(rpc.Disposed);
    }
    [Theory]
    [InlineData("identity")][InlineData("content")][InlineData("missing")]
    public async Task InconsistentReceiptIsUncertainAndNeverRetried(string corruption)
    {
        var rpc = new FakeRpc { Corruption = corruption };
        await Assert.ThrowsAsync<IOException>(() => Queue(rpc).AddAsync(Guid.NewGuid(), new Notice { Message = "notice" }, TestContext.Current.CancellationToken));
        Assert.Equal(1, rpc.Calls);
    }
    [Fact]
    public async Task CleanupRemovesOnlyTheExactNoticeAndPreservesOtherMessages()
    {
        var notice = new Notice { Message = "notice", Submission = "mine" };
        var mine = Submission(notice); var other = Submission(new Notice { Message = "other" });
        var rpc = new FakeRpc { Rows = [other, mine] };
        Assert.Equal("removed", await Queue(rpc).RemoveAsync(Guid.NewGuid(), notice, TestContext.Current.CancellationToken));
        Assert.Equal("thread/queue/delete", rpc.Method);
        Assert.Equal("mine", rpc.Args!["queuedSubmissionId"]!.GetValue<string>());
        Assert.Equal(1, rpc.Calls);
    }
    [Theory]
    [InlineData("changed")][InlineData("duplicate")][InlineData("different-id")]
    public async Task AmbiguousCleanupDoesNotDeleteAnything(string corruption)
    {
        var notice = new Notice { Message = "notice", Submission = "mine" };
        var row = Submission(notice);
        if (corruption == "changed") row["input"]![0]!["text"] = "changed";
        if (corruption == "different-id") row["id"] = "another-id";
        var rpc = new FakeRpc { Rows = corruption == "duplicate" ? [row, (JsonObject)row.DeepClone()] : [row] };
        await Assert.ThrowsAsync<IOException>(() => Queue(rpc).RemoveAsync(Guid.NewGuid(), notice, TestContext.Current.CancellationToken));
        Assert.Equal(0, rpc.Calls);
    }
    [Fact]
    public async Task MissingDeletionReceiptIsNotReportedAsSuccessfulCleanup()
    {
        var notice = new Notice { Message = "notice" }; var rpc = new FakeRpc { Rows = [Submission(notice)], Corruption = "missing" };
        await Assert.ThrowsAsync<IOException>(() => Queue(rpc).RemoveAsync(Guid.NewGuid(), notice, TestContext.Current.CancellationToken));
    }
    [Fact]
    public async Task ReadOnlyCheckNeverPostsAMessage()
    {
        var rpc = new FakeRpc(); await Queue(rpc).CheckAsync(Guid.NewGuid(), TestContext.Current.CancellationToken);
        Assert.Equal(1, rpc.Lists); Assert.Equal(0, rpc.Calls);
    }
    private static CodexQueue Queue(FakeRpc rpc) => new("unused", "unused", _ => Task.FromResult<IQueueRpc>(rpc));
    private static JsonObject Submission(Notice notice) => new()
    { ["id"] = notice.Submission ?? "mine", ["clientUserMessageId"] = notice.Id.ToString("D"), ["input"] = new JsonArray(new JsonObject { ["type"] = "text", ["text"] = notice.Message, ["text_elements"] = new JsonArray() }) };
    private sealed class FakeRpc : IQueueRpc
    {
        public string? Corruption { get; init; }
        public JsonObject[] Rows { get; init; } = [];
        public int Calls, Lists;
        public string? Method;
        public JsonObject? Args;
        public bool Disposed;
        public Task<JsonObject[]> ListAsync(Guid thread) { Lists++; return Task.FromResult(Rows); }
        public Task<JsonObject> CallAsync(string method, JsonObject args)
        {
            Calls++; Method = method; Args = args;
            if (Corruption == "missing") return Task.FromResult(new JsonObject());
            if (method == "thread/queue/delete") return Task.FromResult(new JsonObject { ["deleted"] = true });
            var submission = new JsonObject { ["id"] = "queued-id", ["clientUserMessageId"] = Corruption == "identity" ? "wrong" : args["clientUserMessageId"]!.GetValue<string>(), ["input"] = args["input"]!.DeepClone() };
            if (Corruption == "content") submission["input"]![0]!["text"] = "wrong";
            return Task.FromResult(new JsonObject { ["queuedSubmission"] = submission });
        }
        public ValueTask DisposeAsync() { Disposed = true; return ValueTask.CompletedTask; }
    }
}
