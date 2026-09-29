using System.Globalization;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Recoil.Zbd.PrWatch;

public sealed class GitHubException(string message, TimeSpan? retryAfter = null) : IOException(message)
{
    public TimeSpan? RetryAfter { get; } = retryAfter;
}
public interface IPrSource { Task<Observation> ReadAsync(string repository, int pr, CancellationToken token); }

public sealed partial class GitHub(ICommandRunner runner, string executable, string workspace, TimeProvider? time = null) : IPrSource
{
    public const string ReviewBot = "chatgpt-codex-connector[bot]";
    private readonly TimeProvider clock = time ?? TimeProvider.System;
    private const int MaxRows = 10000, MaxCharacters = 32 * 1024 * 1024;
    public static bool ValidRepository(string value) => RepositoryPattern().IsMatch(value);
    public static bool ValidHead(string value) => HeadPattern().IsMatch(value);
    [GeneratedRegex(@"\A[A-Za-z0-9_.-]+/[A-Za-z0-9_.-]+\z")] private static partial Regex RepositoryPattern();
    [GeneratedRegex(@"\A[0-9a-f]{40}\z")] private static partial Regex HeadPattern();
    [GeneratedRegex("datetime=\"([^\"]+)\"")] private static partial Regex CompletionTime();
    [GeneratedRegex(@"`([0-9a-f]{7,40})`")] private static partial Regex CommitCell();

    public async Task<string> RepositoryAsync(CancellationToken token)
    {
        // Resolve from the workspace's origin, not GH_REPO or the shell's active directory.
        var result = await runner.RunAsync(CommandRunner.Executable("git"), ["remote", "get-url", "origin"], workspace, token);
        string origin = result.Output.Trim();
        string? repo = origin.StartsWith("https://github.com/", StringComparison.OrdinalIgnoreCase) ? origin[19..] :
            origin.StartsWith("git@github.com:", StringComparison.OrdinalIgnoreCase) ? origin[15..] : null;
        if (repo?.EndsWith(".git", StringComparison.OrdinalIgnoreCase) == true) repo = repo[..^4];
        if (result.ExitCode != 0 || repo == null || !ValidRepository(repo)) throw new InvalidOperationException("Origin must identify a github.com repository.");
        return repo;
    }
    public async Task<Observation> ReadAsync(string repository, int pr, CancellationToken token)
    {
        if (!ValidRepository(repository) || pr <= 0) throw new ArgumentException("Invalid repository or PR.");
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token); deadline.CancelAfter(TimeSpan.FromMinutes(3));
        string root = $"repos/{repository}";
        var info = await GetAsync($"{root}/pulls/{pr}", deadline.Token);
        string head = Text(info["head"]!, "sha");
        if (!ValidHead(head)) throw new InvalidDataException("Invalid PR head.");
        if (Text(info, "state") == "closed") return new(head, false, [], null);
        if (Text(info, "state") != "open") throw new InvalidDataException("Unknown PR state.");
        int characters = 0, rows = 0;
        async Task<JsonObject[]> Pages(string endpoint)
        {
            List<JsonObject> found = []; HashSet<long> ids = [];
            for (int page = 1; page <= 100; page++)
            {
                var data = (await GetAsync(endpoint + $"?per_page=100&page={page}", deadline.Token)).AsArray();
                if (data.Count > 100) throw new InvalidDataException("Unexpected GitHub page size.");
                foreach (var node in data)
                {
                    if (node is not JsonObject obj) throw new InvalidDataException("Invalid GitHub row.");
                    long id = obj["id"]!.GetValue<long>();
                    if (id <= 0) throw new InvalidDataException("Invalid GitHub identity.");
                    if (!ids.Add(id)) continue; // Pagination can overlap as authors delete comments.
                    if (++rows > MaxRows || (characters += obj["body"]?.GetValue<string>()?.Length ?? 0) > MaxCharacters)
                        throw new InvalidDataException("PR data exceeds watcher limits; no observation was accepted.");
                    found.Add(obj);
                }
                if (data.Count < 100) return found.ToArray();
            }
            throw new InvalidDataException("PR pagination limit reached; no observation was accepted.");
        }
        var conversation = await Pages($"{root}/issues/{pr}/comments");
        var reviews = await Pages($"{root}/pulls/{pr}/reviews");
        var published = reviews.Where(r => r["submitted_at"] != null && Text(r, "state") != "PENDING").ToArray();
        var publishedIds = published.Select(r => r["id"]!.GetValue<long>()).ToHashSet();
        var inline = await Pages($"{root}/pulls/{pr}/comments");
        var reactions = await Pages($"{root}/issues/{pr}/reactions");
        List<Feedback> comments = [];
        void Add(JsonObject row, string kind, string date)
        {
            string body = Text(row, "body");
            if (kind == "review" && string.IsNullOrWhiteSpace(body)) return;
            comments.Add(new(kind + ":" + row["id"]!.GetValue<long>(), Text(row, "html_url"), Text(row["user"]!, "login"), body, Date(row, date),
                kind == "conversation" && Informational(body, IsBot(row))));
        }
        foreach (var row in conversation) Add(row, "conversation", "created_at");
        foreach (var row in published) Add(row, "review", "submitted_at");
        foreach (var row in inline)
            if (row["pull_request_review_id"] is { } review && publishedIds.Contains(review.GetValue<long>())) Add(row, "inline", "created_at");
        Approval? approval = null;
        // Only the newest bot summary for this head can approve. Another review of the same head, running or
        // completed after an earlier reaction, supersedes the older completed summary and that reaction.
        var summary = conversation.Where(IsBot).Where(s => SummaryForHead(Text(s, "body"), head))
            .OrderBy(s => Date(s, "created_at")).ThenBy(s => s["id"]!.GetValue<long>()).LastOrDefault();
        if (summary != null && SummaryCompletion(Text(summary, "body"), head) is { } completed)
            foreach (var reaction in reactions.Where(IsBot).Where(r => Text(r, "content") == "+1"))
            {
                DateTimeOffset created = Date(reaction, "created_at");
                // GitHub reaction times have second precision. Old retained reactions cannot approve a new review.
                if (created.ToUnixTimeSeconds() >= completed.ToUnixTimeSeconds())
                    approval = new(head, reaction["id"]!.GetValue<long>(), created, Text(summary, "html_url"));
            }
        // A push during pagination invalidates the entire mixed snapshot.
        var final = await GetAsync($"{root}/pulls/{pr}", deadline.Token);
        if (Text(final["head"]!, "sha") != head || Text(final, "state") != "open")
            throw new GitHubException("PR head/state changed while reading; retry a fresh observation.");
        return new(head, true, comments.ToArray(), approval);
    }

    /// <summary>Whether a review-bot summary lists any code/security review of this head, in any state.</summary>
    public static bool SummaryForHead(string body, string head)
    {
        if (!body.StartsWith("<!-- codex-pull-request-review-summary -->", StringComparison.Ordinal)) return false;
        foreach (string line in body.Split('\n'))
        {
            var cells = line.Split('|');
            if (cells.Length < 5 || !(cells[1].Contains("**Code Review**", StringComparison.Ordinal) || cells[1].Contains("**Security Review**", StringComparison.Ordinal))) continue;
            if (CommitCell().Match(cells[3]) is { Success: true } commit && head.StartsWith(commit.Groups[1].Value, StringComparison.Ordinal)) return true;
        }
        return false;
    }
    public static DateTimeOffset? SummaryCompletion(string body, string head)
    {
        if (!body.StartsWith("<!-- codex-pull-request-review-summary -->", StringComparison.Ordinal)) return null;
        bool code = false; DateTimeOffset? latest = null;
        foreach (string line in body.Split('\n'))
        {
            var cells = line.Split('|');
            if (cells.Length < 5 || !(cells[1].Contains("**Code Review**", StringComparison.Ordinal) || cells[1].Contains("**Security Review**", StringComparison.Ordinal))) continue;
            code |= cells[1].Contains("**Code Review**", StringComparison.Ordinal);
            var commit = CommitCell().Match(cells[3]); var date = CompletionTime().Match(cells[2]);
            if (!cells[2].Contains("✅ **Completed**", StringComparison.Ordinal) || !commit.Success || !head.StartsWith(commit.Groups[1].Value, StringComparison.Ordinal) ||
                !date.Success || !DateTimeOffset.TryParse(date.Groups[1].Value, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var ended)) return null;
            if (latest == null || ended > latest) latest = ended;
        }
        return code ? latest : null;
    }
    /// <summary>Review requests and the review bot's summary/usage-limit status posts are not reviewer feedback.</summary>
    public static bool Informational(string body, bool reviewBot) =>
        string.Join(' ', body.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)).Equals("@codex review", StringComparison.OrdinalIgnoreCase) ||
        reviewBot && (body.StartsWith("<!-- codex-pull-request-review-summary -->", StringComparison.Ordinal) ||
            body.Contains("Codex usage limits", StringComparison.Ordinal) && body.Contains("https://chatgpt.com/codex/cloud/settings/usage", StringComparison.Ordinal));
    private static bool IsBot(JsonObject row) => Text(row["user"]!, "login") == ReviewBot && Text(row["user"]!, "type") == "Bot";
    private static string Text(JsonNode row, string key) => row[key]?.GetValue<string>() ?? throw new InvalidDataException("GitHub omitted " + key);
    private static DateTimeOffset Date(JsonNode row, string key) => DateTimeOffset.Parse(Text(row, key), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal);
    private async Task<JsonNode> GetAsync(string endpoint, CancellationToken token)
    {
        var result = await runner.RunAsync(executable, ["api", "--hostname", "github.com", "--method", "GET", "--include", "-H", "Accept: application/vnd.github+json", endpoint], workspace, token);
        string output = result.Output.Replace("\r\n", "\n"); int boundary = output.IndexOf("\n\n", StringComparison.Ordinal);
        if (boundary < 0) throw new GitHubException("GitHub response omitted headers: " + result.Error[..Math.Min(1000, result.Error.Length)]);
        string headers = output[..boundary];
        if (result.ExitCode != 0)
        {
            TimeSpan? retry = null, reset = null; bool exhausted = false;
            foreach (string header in headers.Split('\n'))
            {
                int colon = header.IndexOf(':'); if (colon < 0) continue;
                string name = header[..colon].Trim(), value = header[(colon + 1)..].Trim();
                if (name.Equals("retry-after", StringComparison.OrdinalIgnoreCase) && double.TryParse(value, CultureInfo.InvariantCulture, out double seconds) && double.IsFinite(seconds) && seconds > 0)
                    retry = TimeSpan.FromSeconds(Math.Min(seconds, 86400));
                if (name.Equals("x-ratelimit-remaining", StringComparison.OrdinalIgnoreCase)) exhausted = value == "0";
                if (name.Equals("x-ratelimit-reset", StringComparison.OrdinalIgnoreCase) && long.TryParse(value, out long at) && at > clock.GetUtcNow().ToUnixTimeSeconds() && at < clock.GetUtcNow().AddDays(1).ToUnixTimeSeconds())
                    reset = TimeSpan.FromSeconds(at - clock.GetUtcNow().ToUnixTimeSeconds() + 1);
            }
            // GitHub reports the hourly reset on every response; only an exhausted primary limit waits for it.
            throw new GitHubException("GitHub request failed: " + result.Error[..Math.Min(1000, result.Error.Length)], retry ?? (exhausted ? reset : null));
        }
        return JsonNode.Parse(output[(boundary + 2)..]) ?? throw new InvalidDataException("Empty GitHub response.");
    }
}
