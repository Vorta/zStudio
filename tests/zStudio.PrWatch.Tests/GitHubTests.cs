using System.Text.Json.Nodes;
using Xunit;
using static Recoil.Zbd.PrWatch.Tests.WatcherTests;

namespace Recoil.Zbd.PrWatch.Tests;

public sealed class GitHubTests
{
    internal static string Summary(string head = Head, string status = "✅ **Completed**", string time = "2026-09-29T11:59:50.123Z") =>
        "<!-- codex-pull-request-review-summary -->\n| Review | Status | Commit | Trigger |\n" +
        $"| 📝 **Code Review** | {status} <relative-time datetime=\"{time}\">time</relative-time> | `{head[..7]}` | New commits |\n";
    private static JsonObject User(string login = "reviewer", string type = "User") => new() { ["login"] = login, ["type"] = type };
    private static JsonObject Row(int id, string body = "feedback", JsonObject? user = null) => new()
    {
        ["id"] = id, ["body"] = body, ["user"] = user ?? User(), ["html_url"] = "https://github.com/o/r/pull/14#" + id,
        ["created_at"] = "2026-09-29T12:00:00Z", ["updated_at"] = "2026-09-29T12:00:00Z"
    };
    private static JsonObject Review(int id, bool pending = false, string body = "review")
    {
        var row = Row(id, body); row["state"] = pending ? "PENDING" : "COMMENTED";
        row["submitted_at"] = pending ? null : "2026-09-29T12:00:00Z"; return row;
    }
    private static JsonObject Inline(int id, int review)
    { var row = Row(id); row["pull_request_review_id"] = review; return row; }
    private static JsonObject Reaction(int id, string login = GitHub.ReviewBot, string type = "Bot", string created = "2026-09-29T12:00:00Z") =>
        new() { ["id"] = id, ["content"] = "+1", ["created_at"] = created, ["user"] = User(login, type) };

    [Fact]
    public async Task ReadsAllCommentFamiliesAndLaterPagesAndIgnoresUnpublishedDrafts()
    {
        var runner = new GitHubRunner
        {
            Conversation = Enumerable.Range(1, 101).Select(i => Row(i)).ToArray(),
            Reviews = [Review(1), Review(2, pending: true), Review(3, body: "")],
            Inline = [Inline(1, 1), Inline(2, 2), Inline(3, 3)]
        };
        var result = await Source(runner).ReadAsync("o/r", 14, TestContext.Current.CancellationToken);
        Assert.Equal(104, result.Comments.Length);
        Assert.Contains(result.Comments, c => c.Key == "conversation:101");
        Assert.Contains(result.Comments, c => c.Key == "review:1");
        Assert.DoesNotContain(result.Comments, c => c.Key is "inline:2" or "review:2" or "review:3");
        Assert.Contains(result.Comments, c => c.Key == "inline:3");
        Assert.Equal(2, runner.HeadReads);
        Assert.All(runner.Arguments, args => { Assert.Contains("GET", args); Assert.Contains("--include", args); });
    }

    [Fact]
    public async Task DraftSubmissionWithOldIdBecomesNewFeedback()
    {
        var runner = new GitHubRunner { Reviews = [Review(1, pending: true)], Inline = [Inline(2, 1)] };
        var source = Source(runner); var baseline = await source.ReadAsync("o/r", 14, TestContext.Current.CancellationToken);
        var state = State(); WatchLogic.Arm(state, baseline, Head, true, Now);
        runner.Reviews = [Review(1)];
        var submitted = await source.ReadAsync("o/r", 14, TestContext.Current.CancellationToken);
        Assert.NotNull(WatchLogic.Observe(state, submitted, Now));
        Assert.Equal(2, submitted.Comments.Length);
    }

    [Theory]
    [InlineData(GitHub.ReviewBot, "Bot", "2026-09-29T12:00:00Z", true)]
    [InlineData("someone", "User", "2026-09-29T12:00:00Z", false)]
    [InlineData(GitHub.ReviewBot, "User", "2026-09-29T12:00:00Z", false)]
    [InlineData(GitHub.ReviewBot, "Bot", "2026-09-29T11:00:00Z", false)]
    public async Task ApprovalRequiresTheExactBotAndFreshReaction(string author, string type, string created, bool approved)
    {
        var runner = new GitHubRunner { Conversation = [Row(1, Summary(), User(GitHub.ReviewBot, "Bot"))], Reactions = [Reaction(1, author, type, created)] };
        Assert.Equal(approved, (await Source(runner).ReadAsync("o/r", 14, TestContext.Current.CancellationToken)).Approval != null);
    }

    [Fact]
    public async Task AUserCannotForgeTheBotSummary()
    {
        var runner = new GitHubRunner { Conversation = [Row(1, Summary())], Reactions = [Reaction(1)] };
        Assert.Null((await Source(runner).ReadAsync("o/r", 14, TestContext.Current.CancellationToken)).Approval);
    }

    [Fact]
    public void ApprovalRequiresAllListedReviewsCompleteOnTheCurrentHead()
    {
        Assert.NotNull(GitHub.SummaryCompletion(Summary(), Head));
        Assert.Null(GitHub.SummaryCompletion(Summary(Next), Head));
        Assert.Null(GitHub.SummaryCompletion(Summary(status: "🔄 **Running**"), Head));
        Assert.Null(GitHub.SummaryCompletion(Summary() + $"| 🔒 **Security Review** | 🔄 **Running** | `{Head[..7]}` | New commits |", Head));
        Assert.Null(GitHub.SummaryCompletion(Summary().Replace("**Code Review**", "**Security Review**", StringComparison.Ordinal), Head));
        Assert.Null(GitHub.SummaryCompletion("unknown changed format", Head));
    }

    [Fact]
    public async Task HeadChangingDuringPaginationRejectsTheEntireObservation()
    {
        var runner = new GitHubRunner { ChangeHead = true, Conversation = [Row(1)] };
        await Assert.ThrowsAsync<GitHubException>(() => Source(runner).ReadAsync("o/r", 14, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task PaginationFailureDoesNotReturnPartialFeedback()
    {
        var runner = new GitHubRunner { Conversation = Enumerable.Range(1, 101).Select(i => Row(i)).ToArray(), FailPage2 = true };
        var error = await Assert.ThrowsAsync<GitHubException>(() => Source(runner).ReadAsync("o/r", 14, TestContext.Current.CancellationToken));
        Assert.Equal(TimeSpan.FromSeconds(125), error.RetryAfter);
    }

    [Fact]
    public async Task AggregateLimitRejectsRatherThanTruncatesACommentBatch()
    {
        var runner = new GitHubRunner { Conversation = Enumerable.Range(1, 10001).Select(i => Row(i, "")).ToArray() };
        await Assert.ThrowsAsync<InvalidDataException>(() => Source(runner).ReadAsync("o/r", 14, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task ClosedPrNeedsNoCommentOrReactionFetch()
    {
        var runner = new GitHubRunner { Closed = true };
        Assert.False((await Source(runner).ReadAsync("o/r", 14, TestContext.Current.CancellationToken)).Open);
        Assert.Single(runner.Arguments);
    }

    [Fact]
    public async Task ReviewRequestsAndBotStatusCommentsDoNotConsumeTheFeedbackNotice()
    {
        static JsonObject Bot() => User(GitHub.ReviewBot, "Bot"); var runner = new GitHubRunner(); var source = Source(runner);
        var state = State(); WatchLogic.Arm(state, await source.ReadAsync("o/r", 14, TestContext.Current.CancellationToken), Head, true, Now);
        runner.Conversation = [Row(10, " @Codex \n review "), Row(11, Summary(), Bot()),
            Row(12, "You have reached your Codex usage limits for code reviews. You can see your limits in the [Codex usage dashboard](https://chatgpt.com/codex/cloud/settings/usage).", Bot()),
            Row(13, "The account paying for this security review has reached its Codex usage limits. The payer can check the [Codex usage dashboard](https://chatgpt.com/codex/cloud/settings/usage).", Bot())];
        var informational = await source.ReadAsync("o/r", 14, TestContext.Current.CancellationToken);
        Assert.Equal(4, informational.Comments.Length);
        Assert.Null(WatchLogic.Observe(state, informational, Now));
        Assert.True(state.CommentsArmed); Assert.Empty(state.Notices);
        var review = Review(20); review["user"] = Bot(); runner.Reviews = [review];
        Assert.Equal(["comments"], WatchLogic.Observe(state, await source.ReadAsync("o/r", 14, TestContext.Current.CancellationToken), Now)!.Reasons);
    }

    [Theory]
    [InlineData("@codex review, then also check the save path", "reviewer", "User")]
    [InlineData("You have reached your Codex usage limits. See https://chatgpt.com/codex/cloud/settings/usage", "reviewer", "User")]
    [InlineData("Codex found an issue in the save path.", GitHub.ReviewBot, "Bot")]
    public async Task OtherConversationCommentsStillNotify(string body, string login, string type)
    {
        var runner = new GitHubRunner(); var source = Source(runner);
        var state = State(); WatchLogic.Arm(state, await source.ReadAsync("o/r", 14, TestContext.Current.CancellationToken), Head, true, Now);
        runner.Conversation = [Row(10, body, User(login, type))];
        Assert.NotNull(WatchLogic.Observe(state, await source.ReadAsync("o/r", 14, TestContext.Current.CancellationToken), Now));
    }

    [Fact]
    public async Task OnlyAnExhaustedPrimaryLimitWaitsForTheRateLimitReset()
    {
        long reset = DateTimeOffset.UtcNow.ToUnixTimeSeconds() + 3000;
        var transient = new GitHubRunner { Conversation = Enumerable.Range(1, 101).Select(i => Row(i)).ToArray(), Page2Failure = $"HTTP/2.0 502 Bad Gateway\nX-RateLimit-Remaining: 4999\nX-RateLimit-Reset: {reset}\n\n{{}}" };
        Assert.Null((await Assert.ThrowsAsync<GitHubException>(() => Source(transient).ReadAsync("o/r", 14, TestContext.Current.CancellationToken))).RetryAfter);
        var limited = new GitHubRunner { Conversation = Enumerable.Range(1, 101).Select(i => Row(i)).ToArray(), Page2Failure = $"HTTP/2.0 403 Forbidden\nX-RateLimit-Remaining: 0\nX-RateLimit-Reset: {reset}\n\n{{}}" };
        var delay = (await Assert.ThrowsAsync<GitHubException>(() => Source(limited).ReadAsync("o/r", 14, TestContext.Current.CancellationToken))).RetryAfter!.Value;
        Assert.InRange(delay.TotalSeconds, 2980, 3002);
    }

    private static GitHub Source(GitHubRunner runner) => new(runner, "gh.exe", "workspace");
    private sealed class GitHubRunner : ICommandRunner
    {
        public JsonObject[] Conversation { get; set; } = [];
        public JsonObject[] Reviews { get; set; } = [];
        public JsonObject[] Inline { get; init; } = [];
        public JsonObject[] Reactions { get; init; } = [];
        public bool ChangeHead { get; init; }
        public bool Closed { get; init; }
        public bool FailPage2 { get; init; }
        public string? Page2Failure { get; init; }
        public int HeadReads;
        public List<IReadOnlyList<string>> Arguments { get; } = [];
        public Task<CommandResult> RunAsync(string executable, IReadOnlyList<string> arguments, string cwd, CancellationToken token)
        {
            Arguments.Add(arguments);
            string endpoint = arguments[^1]; JsonNode body;
            if (endpoint == "repos/o/r/pulls/14")
            { HeadReads++; body = new JsonObject { ["head"] = new JsonObject { ["sha"] = ChangeHead && HeadReads > 1 ? Next : Head }, ["state"] = Closed ? "closed" : "open" }; }
            else
            {
                int page = int.Parse(endpoint[(endpoint.LastIndexOf("page=", StringComparison.Ordinal) + 5)..]);
                if (FailPage2 && page == 2) return Task.FromResult(new CommandResult(1, "HTTP/2.0 429 Too Many Requests\nRetry-After: 125\n\n{}", "rate limited"));
                if (Page2Failure != null && page == 2) return Task.FromResult(new CommandResult(1, Page2Failure, "failed"));
                JsonObject[] rows = endpoint.Contains("/issues/14/comments?", StringComparison.Ordinal) ? Conversation :
                    endpoint.Contains("/reviews?", StringComparison.Ordinal) ? Reviews : endpoint.Contains("/reactions?", StringComparison.Ordinal) ? Reactions : Inline;
                body = new JsonArray(rows.Skip((page - 1) * 100).Take(100).Select(r => r.DeepClone()).ToArray());
            }
            return Task.FromResult(new CommandResult(0, "HTTP/2.0 200 OK\r\nContent-Type: application/json\r\n\r\n" + body.ToJsonString(), ""));
        }
    }
}
