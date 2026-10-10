using System.Text.Json.Nodes;
using Recoil.Zbd.Core.Sources;
using Xunit;

namespace Recoil.Zbd.Tests;

public sealed partial class SourcePublisherTests
{
    private const string PathBudgetSaveId = "20260101T000000000Z-1234abcd";
    private static JournalManifest PathManifest(params string[] paths) => new(1, PathBudgetSaveId, "path admission", DateTime.UnixEpoch,
        paths.Select(p => new JournalFile(p, null, JournalDigest.OfContent([1]))).ToArray(), []);

    // Independent arithmetic for these small fixtures; no directory probing or ancestor materialization.
    private static long PathCharge(Project project, string relative) =>
        16L * (project.Root.Length + relative.Length + 1) * (relative.Count(c => c == '/') + 1);

    [Fact]
    public void ManifestFilesAndCleanupFoldersShareOnePreflightAllowance()
    {
        using Project project = new();
        const string file = "data/new/child/value.zrd";
        string[] folders = ["data/new", "data/new/child"];
        var manifest = PathManifest(file) with { Folders = folders };
        long exact = PathCharge(project, file) + folders.Sum(f => PathCharge(project, f));
        var before = project.Sources();
        var error = Assert.Throws<InvalidDataException>(() => new SourcePublisher(project.Root) { PlanningPathBytesLimit = exact - 1 }
            .ValidateManifest(manifest, manifest.SaveId, Token));
        Assert.Contains("planning budget", error.Message);
        new SourcePublisher(project.Root) { PlanningPathBytesLimit = exact }.ValidateManifest(manifest, manifest.SaveId, Token);
        Assert.Equal(before, project.Sources());
        Assert.False(Directory.Exists(project.Full("zstudio")));
    }

    [Fact]
    public void ManifestPreflightAdmitsTheWholeBatchBeforeExpandingItsFirstPath()
    {
        using Project project = new();
        // An admitted batch would reach syntax validation and refuse this non-normalized first path.
        // With insufficient aggregate work, the second row must exhaust admission before that happens.
        var manifest = PathManifest("data/../invalid.zrd", "gamegen/other.gw");
        long first = PathCharge(project, manifest.Files[0].Relative);
        var error = Assert.Throws<InvalidDataException>(() => new SourcePublisher(project.Root) { PlanningPathBytesLimit = first }
            .ValidateManifest(manifest, manifest.SaveId, Token));
        Assert.Contains("planning budget", error.Message);
        var syntax = Assert.Throws<InvalidDataException>(() => new SourcePublisher(project.Root).ValidateManifest(manifest, manifest.SaveId, Token));
        Assert.DoesNotContain("planning budget", syntax.Message);
        Assert.False(Directory.Exists(project.Full("zstudio")));
    }

    [Fact]
    public void CanceledManifestAdmissionLeavesThePublisherReusable()
    {
        using Project project = new();
        using CancellationTokenSource cancellation = new();
        var manifest = PathManifest("gamegen/one.gw");
        long exact = PathCharge(project, manifest.Files[0].Relative);
        SourcePublisher publisher = new(project.Root) { PlanningPathBytesLimit = exact };
        cancellation.Cancel();
        Assert.ThrowsAny<OperationCanceledException>(() => publisher.ValidateManifest(manifest, manifest.SaveId, cancellation.Token));
        publisher.ValidateManifest(manifest, manifest.SaveId, Token);
        Assert.False(Directory.Exists(project.Full("zstudio")));
    }

    [Fact]
    public void WriterRefusesAManifestWhoseCleanupWouldExceedRecoveryAdmission()
    {
        using Project project = new();
        const string relative = "data/new/value.zrd";
        long exact = PathCharge(project, relative) + PathCharge(project, "data/new");
        var before = project.Sources();
        var error = Assert.Throws<InvalidDataException>(() => new SourcePublisher(project.Root) { PlanningPathBytesLimit = exact - 1 }
            .Publish([new(relative, null, [1])], "folder budget", Token));
        Assert.Contains("planning budget", error.Message);
        Assert.Equal(before, project.Sources());
        Assert.False(Directory.Exists(project.Full("zstudio")));
        new SourcePublisher(project.Root) { PlanningPathBytesLimit = exact }.Publish([new(relative, null, [1])], "exact fit", Token);
        Assert.Equal(new byte[] { 1 }, project.Read(relative));
    }

    [Fact]
    public void EverySingleJournalEntryPointRefusesBeforeChangingSourcesOrHistory()
    {
        using Project project = new();
        var original = project.Sources();
        Assert.Throws<SourcePublisher.Crash>(() => Failing(project, "commit", -1, () => new SourcePublisher.Crash())
            .Publish(ScriptChange(project), "shallow recovery", Token));
        string id = Assert.Single(new SourcePublisher(project.Root).FindInterrupted(Token)).SaveId;
        string folder = project.Full($"zstudio/recovery/{id}");
        var sources = project.Sources();
        var journal = JournalContents(folder);
        SourcePublisher limited = new(project.Root) { PlanningPathBytesLimit = PathCharge(project, Script) - 1 };
        Assert.Contains("planning budget", Assert.Throws<SourceRecoveryRequiredException>(() => limited.FindInterrupted(Token)).Message);
        Assert.Contains("planning budget", Assert.Throws<SourceRecoveryRequiredException>(() => limited.SaveFiles(id)).Message);
        foreach (var action in Enum.GetValues<SourceRecoveryAction>())
            Assert.Contains("planning budget", Assert.Throws<SourceRecoveryRequiredException>(() => limited.Resolve(id, action, Token)).Message);
        Assert.Equal(sources, project.Sources());
        Assert.Equal(journal, JournalContents(folder));
        Assert.True(new SourcePublisher(project.Root).Resolve(id, SourceRecoveryAction.RollBack, Token).Resolved);
        Assert.Equal(original, project.Sources());
        Assert.Empty(project.Leftovers());
    }

    [Fact]
    public void DiscoverySharesPathWorkAcrossIndividuallyAdmissibleJournals()
    {
        using Project project = new();
        Assert.Throws<SourcePublisher.Crash>(() => Failing(project, "prepared", -1, () => new SourcePublisher.Crash())
            .Publish(ScriptChange(project), "two short journals", Token));
        string id = Assert.Single(new SourcePublisher(project.Root).FindInterrupted(Token)).SaveId;
        string original = project.Full($"zstudio/recovery/{id}");
        string secondId = id[..^8] + (id.EndsWith("00000000", StringComparison.Ordinal) ? "00000001" : "00000000");
        string second = project.Full($"zstudio/recovery/{secondId}");
        Directory.CreateDirectory(second);
        var manifest = JsonNode.Parse(File.ReadAllText(Path.Combine(original, "manifest.json")))!;
        manifest["saveId"] = secondId;
        File.WriteAllText(Path.Combine(second, "manifest.json"), manifest.ToJsonString());
        File.Copy(Path.Combine(original, "events.log"), Path.Combine(second, "events.log"));
        long each = PathCharge(project, Script);
        var sources = project.Sources();
        var firstHistory = JournalContents(original); var secondHistory = JournalContents(second);
        SourcePublisher limited = new(project.Root) { PlanningPathBytesLimit = each };
        Assert.Equal(new[] { Script }, limited.SaveFiles(id));
        Assert.Equal(new[] { Script }, limited.SaveFiles(secondId));
        Assert.Contains("planning budget", Assert.Throws<SourceRecoveryRequiredException>(() => limited.FindInterrupted(Token)).Message);
        Assert.Equal(2, new SourcePublisher(project.Root) { PlanningPathBytesLimit = 2 * each }.FindInterrupted(Token).Count);
        Assert.Equal(sources, project.Sources());
        Assert.Equal(firstHistory, JournalContents(original)); Assert.Equal(secondHistory, JournalContents(second));
    }

    private static SortedDictionary<string, string> JournalContents(string folder) => new(
        Directory.GetFiles(folder, "*", SearchOption.AllDirectories).ToDictionary(p => Path.GetRelativePath(folder, p), p => Convert.ToHexString(File.ReadAllBytes(p))),
        StringComparer.Ordinal);
}
