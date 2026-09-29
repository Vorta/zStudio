namespace Recoil.Zbd.PrWatch;

public sealed record Feedback(string Key, string Url, string Author, string Body, DateTimeOffset Published);
public sealed record Approval(string Head, long ReactionId, DateTimeOffset Created, string SummaryUrl);
public sealed record Observation(string Head, bool Open, Feedback[] Comments, Approval? Approval);
public sealed record ReadSnapshot(Guid Id, Guid Watch, Guid? Notice, DateTimeOffset Created, Observation Observation);

public sealed class Notice
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public int Generation { get; set; }
    public DateTimeOffset Created { get; set; }
    public string Head { get; set; } = "";
    public string[] Reasons { get; set; } = [];
    public string Message { get; set; } = "";
    // Claim is durable before queue/add. A failed/uncertain send is never retried.
    public string Delivery { get; set; } = "claimed";
    public string? Submission { get; set; }
    public string? Error { get; set; }
    public DateTimeOffset? Acknowledged { get; set; }
    public string? Cleanup { get; set; }
}

public sealed class WatchState
{
    public int Schema { get; set; } = 1;
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Workspace { get; set; } = "";
    public string Repository { get; set; } = "";
    public int Pr { get; set; }
    public Guid Thread { get; set; }
    public string Codex { get; set; } = "";
    public string Gh { get; set; } = "";
    public string ExpectedHead { get; set; } = "";
    public int Generation { get; set; }
    public bool Active { get; set; }
    public bool CommentsArmed { get; set; }
    public bool ApprovalEnabled { get; set; } = true;
    public bool ReleaseAuthorized { get; set; }
    public HashSet<string> Handled { get; set; } = [];
    public HashSet<string> ApprovalNotifiedHeads { get; set; } = [];
    public List<Notice> Notices { get; set; } = [];
    public int WorkerPid { get; set; }
    public long WorkerStartTicks { get; set; }
    public DateTimeOffset? Heartbeat { get; set; }
    public DateTimeOffset? LastSuccess { get; set; }
    public string? LastError { get; set; }
    public string? ObservedHead { get; set; }
    public int FailureCount { get; set; }
    [System.Text.Json.Serialization.JsonIgnore]
    public Notice? Outstanding => Notices.LastOrDefault(n => n.Acknowledged == null);
}

public static class WatchLogic
{
    public static Notice? Observe(WatchState state, Observation observation, DateTimeOffset now, string statePath)
    {
        state.LastSuccess = now; state.LastError = null; state.FailureCount = 0;
        state.ObservedHead = observation.Head;
        if (!state.Active) return null;
        if (!observation.Open)
        {
            state.Active = state.CommentsArmed = state.ApprovalEnabled = false;
            return null;
        }
        bool feedback = state.CommentsArmed && observation.Comments.Any(c => !state.Handled.Contains(c.Key));
        bool approval = state.ApprovalEnabled && observation.Head == state.ExpectedHead && observation.Approval?.Head == observation.Head
            && !state.ApprovalNotifiedHeads.Contains(observation.Head);
        if (state.Outstanding != null || (!feedback && !approval)) return null;
        if (state.Notices.Count >= 2000) throw new InvalidOperationException("Watcher notice history limit reached; inspect the saved state.");
        if (feedback) state.CommentsArmed = false;
        if (approval) state.ApprovalNotifiedHeads.Add(observation.Head);
        Notice notice = new()
        {
            Generation = state.Generation, Created = now, Head = observation.Head,
            Reasons = feedback ? approval ? ["comments", "approval"] : ["comments"] : ["approval"]
        };
        notice.Message = $"zStudio PR watch notice {notice.Id:D}: {state.Repository}#{state.Pr}, {string.Join(" and ", notice.Reasons)}. " +
            $"Inspect {statePath}. Run tools/pr-watch.ps1 status and read for PR {state.Pr}; follow AGENTS.md PR watch handling. " +
            "Skip acknowledged/stopped notices. Honor newer stop/pause/scope instructions. This notice is not proof of approval, CI, or release readiness.";
        state.Notices.Add(notice);
        return notice;
    }

    public static void Arm(WatchState state, Observation observation, string expectedHead, bool first, DateTimeOffset now)
    {
        if (!observation.Open) throw new InvalidOperationException("The selected PR is closed.");
        if (observation.Head != expectedHead) throw new InvalidOperationException("Remote PR head differs from --head. Read it again before arming.");
        if (state.Outstanding != null) throw new InvalidOperationException("Read and acknowledge the outstanding notice before re-arming.");
        if (first) state.Handled.UnionWith(observation.Comments.Select(c => c.Key));
        state.ExpectedHead = expectedHead; state.Generation++;
        state.Active = state.CommentsArmed = state.ApprovalEnabled = true;
        state.LastSuccess = now; state.LastError = null;
        // Never add newly observed comments to Handled on a re-arm.
    }

    public static void Acknowledge(WatchState state, ReadSnapshot snapshot, Guid noticeId, DateTimeOffset now)
    {
        var notice = state.Notices.SingleOrDefault(n => n.Id == noticeId) ?? throw new InvalidOperationException("Unknown notice.");
        if (notice.Acknowledged != null) return;
        if (snapshot.Watch != state.Id || snapshot.Notice != noticeId || snapshot.Created < notice.Created)
            throw new InvalidOperationException("Read a fresh snapshot for this watch and notice before acknowledging.");
        state.Handled.UnionWith(snapshot.Observation.Comments.Select(c => c.Key));
        notice.Acknowledged = now;
    }

    public static TimeSpan RetryDelay(int failures, TimeSpan? requested = null) =>
        requested is { } delay && delay > TimeSpan.Zero ? delay : TimeSpan.FromSeconds(Math.Min(600, 60 * Math.Pow(2, Math.Clamp(failures - 1, 0, 4))));
}
