using zStudio.Core.Animation;

namespace zStudio.Desktop;

/// <summary>
/// The what-if runs behind the excluded-effect notices (design/animation-redesign.md §15, docs/agents/preview.md): of the excluded
/// calls a run reached that could change it (<see cref="AnimationExcludedEffects.Candidates"/>), the first
/// <see cref="MaximumChecks"/> in the order they dispatched run in the background, one after another, as the same scenario with
/// that call path and everything below it included (<see cref="AnimationPlayer.RunWhatIf"/>), to the playback range's end, after one run of the scenario as it is (<see cref="AnimationPlayer.RunBaseline"/>). Results
/// are kept per what they belong to (<see cref="Key"/>: the run's frozen context and player settings, the opened binding, the
/// scenario's value and the range's end), so a scenario change undone or Exit and reopening finds them again; a newer key
/// cancels the runs of the one before (a scenario change, an edit, a rebuild) and <see cref="Cancel"/> ends them (Exit). They never
/// touch the visible run, the playhead or the 3D view. UI thread only.
/// </summary>
internal sealed class AnimationWhatIfChecks
{
    public const int MaximumChecks = 8;
    private const int MaximumKeys = 16;
    /// <summary>What a what-if belongs to: the run's frozen context and player settings, the opened binding, the scenario's value and the range's end.</summary>
    internal sealed record Key(object Run, int Entry, AnimationScenario Scenario, double End);
    /// <summary>One checked candidate: its result once its what-if ran, or null while it runs or waits.</summary>
    internal sealed record Check(AnimationExcludedCall Call, AnimationWhatIfResult? Result);
    /// <summary>What the notices show: the checked candidates and how many more the cap left unchecked.</summary>
    internal sealed record Status(IReadOnlyList<Check> Checks, int NotChecked)
    {
        public static Status Empty { get; } = new([], 0);
        public int Checking => Checks.Count(c => c.Result == null);
        /// <summary>Candidates whose what-if could not run to the range's end and found nothing (a preview allowance).</summary>
        public IReadOnlyList<Check> Failed => [.. Checks.Where(c => c.Result is { Effect: null, Failure: not null })];
    }

    private readonly LinkedList<(Key Key, Dictionary<string, AnimationWhatIfResult> Results)> cache = [];
    private readonly CancellationToken lifetime;
    private Key? running;
    private CancellationTokenSource? cancellation;
    private Func<string, CancellationToken, AnimationWhatIfResult>? runner;
    private readonly Queue<string> queue = [];
    private readonly HashSet<string> queued = new(StringComparer.Ordinal);
    private bool looping;

    internal AnimationWhatIfChecks(CancellationToken lifetime) => this.lifetime = lifetime;
    /// <summary>Changes with every result that arrives (the notices' cache key).</summary>
    public long Version { get; private set; }
    /// <summary>A what-if result arrived: the notices, badges and cards refresh.</summary>
    public event Action? Changed;
    /// <summary>The longest what-if run so far, in milliseconds (diagnostics).</summary>
    public double LongestMilliseconds { get; private set; }

    /// <summary>
    /// The checks of <paramref name="candidates"/> (in dispatch order) under <paramref name="key"/>: the first
    /// <see cref="MaximumChecks"/> with their results or checking, the rest not checked. Schedules what is missing, unless
    /// <paramref name="ready"/> is false (the range's end is still measured): those read as checking meanwhile.
    /// <paramref name="prepare"/> runs on the UI thread when a key's first run starts and returns what runs one what-if off it
    /// (a frozen snapshot of the context, the scenario and the player settings).
    /// </summary>
    internal Status Evaluate(Key key, IReadOnlyList<AnimationExcludedCall> candidates, bool ready, Func<Func<string, CancellationToken, AnimationWhatIfResult>> prepare)
    {
        // Another scenario, binding, range or world: what still runs for the one before stops (its results so far stay).
        if (running != null && running != key) Cancel();
        if (candidates.Count == 0) return Status.Empty;
        var results = Results(key);
        List<Check> checks = [];
        List<string> missing = [];
        foreach (var call in candidates.Take(MaximumChecks))
        {
            if (results.TryGetValue(call.Path, out var result)) checks.Add(new(call, result));
            else { checks.Add(new(call, null)); missing.Add(call.Path); }
        }
        if (missing.Count > 0 && ready) Schedule(key, missing, prepare);
        return new(checks, Math.Max(0, candidates.Count - MaximumChecks));
    }

    /// <summary>Ends the runs in progress (Exit, the end of the mode or of the compiled preview); results already found stay.</summary>
    internal void Cancel()
    {
        // A run in progress observes the cancellation and ends; its loop then releases the source.
        if (cancellation != null) { cancellation.Cancel(); if (!looping) cancellation.Dispose(); }
        cancellation = null; running = null; runner = null; queue.Clear(); queued.Clear(); looping = false;
    }

    private Dictionary<string, AnimationWhatIfResult> Results(Key key)
    {
        for (var node = cache.First; node != null; node = node.Next)
            if (node.Value.Key == key)
            {
                if (node != cache.First) { cache.Remove(node); cache.AddFirst(node); }
                return node.Value.Results;
            }
        Dictionary<string, AnimationWhatIfResult> created = new(StringComparer.Ordinal);
        cache.AddFirst((key, created));
        while (cache.Count > MaximumKeys) cache.RemoveLast();
        return created;
    }

    private void Schedule(Key key, IEnumerable<string> paths, Func<Func<string, CancellationToken, AnimationWhatIfResult>> prepare)
    {
        if (running != key)
        {
            running = key; cancellation = CancellationTokenSource.CreateLinkedTokenSource(lifetime);
            runner = prepare();
        }
        foreach (string path in paths) if (queued.Add(path)) queue.Enqueue(path);
        if (!looping && queue.Count > 0) { looping = true; _ = LoopAsync(key, cancellation!, runner!); }
    }

    private async Task LoopAsync(Key key, CancellationTokenSource source, Func<string, CancellationToken, AnimationWhatIfResult> run)
    {
        var token = source.Token;
        try
        {
            while (!token.IsCancellationRequested && queue.TryDequeue(out string? path))
            {
                AnimationWhatIfResult result;
                var watch = System.Diagnostics.Stopwatch.StartNew();
                try { result = await Task.Run(() => run(path, token), token); }
                catch (OperationCanceledException) { return; }
                catch (Exception ex) when (ex is not OutOfMemoryException and not StackOverflowException)
                { result = new(path, null, [], 0) { Failure = ex.Message }; }
                if (token.IsCancellationRequested || !ReferenceEquals(cancellation, source)) return;
                LongestMilliseconds = Math.Max(LongestMilliseconds, watch.Elapsed.TotalMilliseconds);
                Results(key)[path] = result; queued.Remove(path);
                Version++; Changed?.Invoke();
            }
        }
        finally
        {
            if (ReferenceEquals(cancellation, source)) looping = false;
            else source.Dispose();
        }
    }
}
