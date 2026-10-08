using System.Text.Json;
using System.Text.Json.Nodes;
using Recoil.Zbd.Core.Sources;
using Xunit;

namespace Recoil.Zbd.Tests;

public sealed partial class SourcePublisherTests
{
    [Fact]
    public void WrongManifestIdentityIsClippedBeforeBuildingTheException()
    {
        using Project project = new();
        SourcePublisher publisher = new(project.Root);
        var ordinary = PathManifest(Script) with { SaveId = "other-save" };
        Assert.Contains("other-save", Assert.Throws<InvalidDataException>(() => publisher.ValidateManifest(ordinary, PathBudgetSaveId, Token)).Message);
        var wide = ordinary with { SaveId = new string('&', 65_536) };
        long before = GC.GetAllocatedBytesForCurrentThread();
        var error = Assert.Throws<InvalidDataException>(() => publisher.ValidateManifest(wide, PathBudgetSaveId, Token));
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        Assert.InRange(error.Message.Length, 1, 256);
        Assert.True(allocated < 32_768, $"Formatting a clipped identity allocated {allocated:N0} bytes.");
        Assert.Equal(65_536, wide.SaveId.Length);
        Assert.False(Directory.Exists(project.Full("zstudio")));
    }

    [Theory]
    [InlineData(8)]
    [InlineData(4096)]
    public void MalformedIdentityKeepsTheRealJournalIdAndDoesNotChangeSourcesOrHistory(int length)
    {
        using Project project = new();
        var original = project.Sources();
        Assert.Throws<SourcePublisher.Crash>(() => Failing(project, "commit", -1, () => new SourcePublisher.Crash())
            .Publish(ScriptChange(project), "diagnostic", Token));
        string id = Assert.Single(new SourcePublisher(project.Root).FindInterrupted(Token)).SaveId;
        string folder = project.Full($"zstudio/recovery/{id}"), path = Path.Combine(folder, "manifest.json");
        byte[] valid = File.ReadAllBytes(path);
        var json = JsonNode.Parse(valid)!;
        string wrong = new('x', length);
        json["saveId"] = wrong;
        File.WriteAllText(path, json.ToJsonString());
        var sources = project.Sources(); var journal = JournalContents(folder);
        SourcePublisher publisher = new(project.Root);
        var error = Assert.Throws<SourceRecoveryRequiredException>(() => publisher.FindInterrupted(Token));
        Assert.Equal(id, error.SaveId);
        Assert.Contains(id, error.Message);
        Assert.Contains("held", error.Message);
        Assert.InRange(error.Message.Length, 1, 1024);
        Assert.InRange(Assert.IsType<InvalidDataException>(error.InnerException).Message.Length, 1, 256);
        if (length == 8) Assert.Contains(wrong, error.Message);
        Assert.Equal(id, Assert.Throws<SourceRecoveryRequiredException>(() => publisher.Resolve(id, SourceRecoveryAction.Complete, Token)).SaveId);
        Assert.Equal(sources, project.Sources()); Assert.Equal(journal, JournalContents(folder));
        File.WriteAllBytes(path, valid);
        Assert.True(publisher.Resolve(id, SourceRecoveryAction.RollBack, Token).Resolved);
        Assert.Equal(original, project.Sources());
    }

    [Fact]
    public void LoaderDoesNotCopyAnEntireSerializerDiagnosticIntoItsWrapper()
    {
        using Project project = new();
        Assert.Throws<SourcePublisher.Crash>(() => Failing(project, "prepared", -1, () => new SourcePublisher.Crash())
            .Publish(ScriptChange(project), "serializer diagnostic", Token));
        string id = Assert.Single(new SourcePublisher(project.Root).FindInterrupted(Token)).SaveId;
        string folder = project.Full($"zstudio/recovery/{id}"), path = Path.Combine(folder, "manifest.json");
        var json = JsonNode.Parse(File.ReadAllBytes(path))!;
        json[new string('x', 4096)] = 1; // Valid small JSON, rejected by UnmappedMemberHandling.Disallow.
        File.WriteAllText(path, json.ToJsonString());
        var journal = JournalContents(folder); var sources = project.Sources();
        var error = Assert.Throws<SourceRecoveryRequiredException>(() => new SourcePublisher(project.Root).FindInterrupted(Token));
        Assert.Equal(id, error.SaveId);
        Assert.IsType<JsonException>(error.InnerException);
        Assert.InRange(error.Message.Length, 1, 1024);
        Assert.Equal(journal, JournalContents(folder)); Assert.Equal(sources, project.Sources());
    }

    [Theory]
    [InlineData("duplicate-file")]
    [InlineData("not-a-change")]
    [InlineData("duplicate-folder")]
    [InlineData("unowned-folder")]
    [InlineData("invalid-path")]
    [InlineData("invalid-component")]
    public void ManifestPathDiagnosticsClipFieldsWithoutChangingTheirValidation(string kind)
    {
        using Project project = new();
        string folder = "data/" + new string('a', 200) + "/" + new string('b', 200);
        string file = folder + "/value.zrd";
        var manifest = PathManifest(file);
        manifest = kind switch
        {
            "duplicate-file" => PathManifest(file, file),
            "not-a-change" => manifest with { Files = [new(file, null, null)] },
            "duplicate-folder" => manifest with { Folders = [folder, folder] },
            "unowned-folder" => manifest with { Folders = [folder + "x"] },
            "invalid-path" => PathManifest("data/../" + new string('x', 512)),
            _ => PathManifest(folder + "/bad:")
        };
        var error = Assert.Throws<InvalidDataException>(() => new SourcePublisher(project.Root).ValidateManifest(manifest, manifest.SaveId, Token));
        Assert.InRange(error.Message.Length, 1, 768);
        Assert.Contains("…", error.Message);
        Assert.False(Directory.Exists(project.Full("zstudio")));
    }

    [Fact]
    public void InvalidResolveIdentityIsBoundedBeforeInterpolation()
    {
        using Project project = new();
        var before = project.Sources();
        string invalid = new('x', 4096);
        var error = Assert.Throws<ArgumentException>(() => new SourcePublisher(project.Root).Resolve(invalid, SourceRecoveryAction.Complete, Token));
        Assert.Equal("saveId", error.ParamName);
        Assert.InRange(error.Message.Length, 1, 256);
        Assert.Equal(before, project.Sources());
        Assert.False(Directory.Exists(project.Full("zstudio")));
    }

    [Fact]
    public void AggregateFailureAndCancellationMessagesKeepFullStructuredIdentities()
    {
        string[] paths = [.. Enumerable.Range(0, 20).Select(i => "data/" + new string('&', 1024) + $"/{i}.zrd")];
        SourceRecoveryConflict[] conflicts = [.. paths.Select(p => new SourceRecoveryConflict(p, new string('&', 1024)))];
        Exception cause = new IOException(new string('&', 65_536));
        var conflict = SourcePublisher.ConflictingSources(paths);
        Assert.Same(paths, conflict.Files);
        Assert.InRange(conflict.Message.Length, 1, 4096);
        Assert.Contains("12 more files", conflict.Message);
        var unfinished = SourcePublisher.Unfinished(PathBudgetSaveId, paths, cause, conflicts);
        Assert.Equal(PathBudgetSaveId, unfinished.SaveId);
        Assert.Same(paths, unfinished.Files); Assert.Same(cause, unfinished.InnerException);
        Assert.InRange(unfinished.Message.Length, 1, 6000);
        Assert.Contains("12 more conflicts", unfinished.Message);
        using CancellationTokenSource cancellation = new(); cancellation.Cancel();
        var canceled = SourcePublisher.Canceled(PathBudgetSaveId, paths, conflicts, cancellation.Token);
        Assert.Equal(cancellation.Token, canceled.CancellationToken);
        Assert.IsType<OperationCanceledException>(canceled.InnerException);
        Assert.InRange(canceled.Message.Length, 1, 8000);
        Assert.Contains("12 more files", canceled.Message); Assert.Contains("12 more conflicts", canceled.Message);
        // Even the duplicated, JSON-escaped error envelope remains small; source identities and reasons stay whole.
        Assert.True(JsonSerializer.Serialize(new { message = canceled.Message, structured = new { message = canceled.Message } }).Length < 100_000);
        Assert.All(conflicts, c => { Assert.True(c.Relative.Length > 1024); Assert.Equal(1024, c.Reason.Length); });
        Assert.Contains(Script, SourcePublisher.ConflictingSources([Script]).Message);
    }
}
