using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using Recoil.Zbd.Core.Sources;
using Xunit;

namespace Recoil.Zbd.Tests;

public sealed partial class SourcePublisherTests
{
    private static byte[] ColdManifest(JournalManifest manifest, bool escaped = true) => JsonSerializer.SerializeToUtf8Bytes(manifest,
        new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase, Encoder = escaped ? null : JavaScriptEncoder.UnsafeRelaxedJsonEscaping });

    [Fact]
    public void ColdFolderCardinalityIsAdmittedBeforeAllocatingTheCollection()
    {
        using Project project = new();
        SourcePublisher limited = new(project.Root) { JournalFoldersLimit = 2 };
        byte[] small = Encoding.UTF8.GetBytes("{\"folders\":[\"\",\"\",\"\"]}");
        Assert.Contains("more than 2 folders", Assert.Throws<InvalidDataException>(() => limited.ParseManifest(small, PathBudgetSaveId, Token)).Message);
        // 4,096 empty rows are a small input but exceed the allocation ceiling if deserialized into a growing list.
        byte[] many = Encoding.UTF8.GetBytes("{\"folders\":[" + string.Join(',', Enumerable.Repeat("\"\"", 4096)) + "]}");
        long before = GC.GetAllocatedBytesForCurrentThread();
        var error = Assert.Throws<InvalidDataException>(() => limited.ParseManifest(many, PathBudgetSaveId, Token));
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        Assert.Contains("more than 2 folders", error.Message);
        Assert.True(allocated < 32 * 1024, $"Cold collection refusal allocated {allocated:N0} bytes.");
        var valid = PathManifest("data/new/child/value.zrd") with { Folders = ["data/new", "data/new/child"] };
        Assert.Equal(2, limited.ParseManifest(ColdManifest(valid), valid.SaveId, Token).Folders.Count);
        Assert.False(Directory.Exists(project.Full("zstudio")));
    }

    [Fact]
    public void ColdFileRowsAndRepeatedCollectionPropertiesShareAdmission()
    {
        using Project project = new();
        SourcePublisher publisher = new(project.Root);
        byte[] files = Encoding.UTF8.GetBytes("{\"files\":[{\"relative\":\"gamegen/a.gs\"}],\"files\":[{\"relative\":\"gamegen/b.gs\"},{}]}");
        var error = Assert.Throws<InvalidDataException>(() => publisher.ParseManifest(files, PathBudgetSaveId, Token, maximumFiles: 2));
        Assert.Contains("more than 2 additional files", error.Message); // Before required-field deserialization of either incomplete row.
        var valid = PathManifest("gamegen/a.gs", "gamegen/b.gs");
        Assert.Equal(2, publisher.ParseManifest(ColdManifest(valid), valid.SaveId, Token, maximumFiles: 2).Files.Count);
        byte[] folders = Encoding.UTF8.GetBytes("{\"folders\":[\"\",\"\"],\"folders\":[\"\"]}");
        Assert.Contains("more than 2 folders", Assert.Throws<InvalidDataException>(() =>
            new SourcePublisher(project.Root) { JournalFoldersLimit = 2 }.ParseManifest(folders, PathBudgetSaveId, Token)).Message);
    }

    [Theory]
    [InlineData("description", 1025, "description")]
    [InlineData("saveId", 129, "identity")]
    [InlineData("createdUtc", 65, "creation date")]
    [InlineData("sha256", 65, "digest")]
    public void KnownStringsAreBoundedBeforeTheyAreDecoded(string field, int length, string diagnostic)
    {
        using Project project = new();
        string value = "\"" + string.Concat(Enumerable.Repeat("\\u2603", length)) + "\"";
        string json = field == "sha256" ? "{\"files\":[{\"content\":{\"sha256\":" + value + "}}]}" : "{\"" + field + "\":" + value + "}";
        byte[] bytes = Encoding.UTF8.GetBytes(json);
        var error = Assert.Throws<InvalidDataException>(() => new SourcePublisher(project.Root).ParseManifest(bytes, PathBudgetSaveId, Token));
        Assert.Contains(diagnostic, error.Message); Assert.InRange(error.Message.Length, 1, 128);
        Assert.False(Directory.Exists(project.Full("zstudio")));
    }

    [Fact]
    public void ColdStringAndEscapedUnknownKeyRefusalsDoNotAllocateTheirFullText()
    {
        using Project project = new();
        SourcePublisher publisher = new(project.Root);
        Assert.Throws<InvalidDataException>(() => publisher.ParseManifest(Encoding.UTF8.GetBytes("{\"description\":\"" + new string('x', 1025) + "\"}"), PathBudgetSaveId, Token));
        Assert.Throws<JsonException>(() => publisher.ParseManifest("{\"unknown\":0}"u8, PathBudgetSaveId, Token));
        byte[] description = Encoding.UTF8.GetBytes("{\"description\":\"" + string.Concat(Enumerable.Repeat("\\u2603", 16_384)) + "\"}");
        byte[] key = Encoding.UTF8.GetBytes("{\"" + string.Concat(Enumerable.Repeat("\\u2603", 16_384)) + "\":0}");
        long before = GC.GetAllocatedBytesForCurrentThread();
        Assert.Throws<InvalidDataException>(() => publisher.ParseManifest(description, PathBudgetSaveId, Token));
        long descriptionBytes = GC.GetAllocatedBytesForCurrentThread() - before;
        before = GC.GetAllocatedBytesForCurrentThread();
        Assert.Throws<JsonException>(() => publisher.ParseManifest(key, PathBudgetSaveId, Token));
        long keyBytes = GC.GetAllocatedBytesForCurrentThread() - before;
        Assert.True(descriptionBytes < 16 * 1024, $"Cold description refusal allocated {descriptionBytes:N0} bytes.");
        Assert.True(keyBytes < 16 * 1024, $"Cold escaped property refusal allocated {keyBytes:N0} bytes.");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void VersionOneEscapedAndUtf8IdentitiesKeepTheirExactPathCharge(bool escaped)
    {
        using Project project = new();
        const string relative = "data/é😀/value.zrd";
        string description = new string('\u2603', 512) + string.Concat(Enumerable.Repeat("😀", 256));
        var manifest = PathManifest(relative) with { Description = description };
        byte[] bytes = ColdManifest(manifest, escaped);
        if (escaped) bytes = Encoding.UTF8.GetBytes(Encoding.UTF8.GetString(bytes).Replace("/", "\\u002f").Replace("\"files\"", "\"f\\u0069les\""));
        long exact = PathCharge(project, relative);
        var parsed = new SourcePublisher(project.Root) { PlanningPathBytesLimit = exact }.ParseManifest(bytes, manifest.SaveId, Token);
        Assert.Equal(relative, Assert.Single(parsed.Files).Relative);
        Assert.Equal(description, parsed.Description); Assert.Equal(manifest.SaveId, parsed.SaveId); Assert.Equal(1, parsed.Format);
        Assert.Contains("planning budget", Assert.Throws<InvalidDataException>(() =>
            new SourcePublisher(project.Root) { PlanningPathBytesLimit = exact - 1 }.ParseManifest(bytes, manifest.SaveId, Token)).Message);
        Assert.False(Directory.Exists(project.Full("zstudio")));
    }

    [Theory]
    [InlineData("{\"files\":[null]}")]
    [InlineData("{\"folders\":[null]}")]
    [InlineData("{\"description\":null}")]
    [InlineData("{\"files\":{}}")]
    [InlineData("{\"files\":[{\"content\":[]}]}")]
    [InlineData("{\"files\":[{\"content\":{\"unknown\":0}}]}")]
    [InlineData("{\"unknown\":[]}")]
    [InlineData("{}")]
    public void ColdShapeAndRequiredFieldRefusalsRemainControlled(string json)
    {
        using Project project = new();
        byte[] bytes = Encoding.UTF8.GetBytes(json);
        Assert.Throws<JsonException>(() => new SourcePublisher(project.Root).ParseManifest(bytes, PathBudgetSaveId, Token));
    }

    [Fact]
    public void CanceledColdAdmissionCanBeRetriedWithTheCompleteManifest()
    {
        using Project project = new();
        SourcePublisher publisher = new(project.Root);
        byte[] bytes = ColdManifest(PathManifest(Script));
        using CancellationTokenSource cancellation = new(); cancellation.Cancel();
        Assert.ThrowsAny<OperationCanceledException>(() => publisher.ParseManifest(bytes, PathBudgetSaveId, cancellation.Token));
        Assert.Equal(Script, Assert.Single(publisher.ParseManifest(bytes, PathBudgetSaveId, Token).Files).Relative);
        Assert.False(Directory.Exists(project.Full("zstudio")));
    }

    [Fact]
    public void EveryColdReaderRouteRefusesBeforeChangingSourcesOrJournalHistory()
    {
        using Project project = new();
        var original = project.Sources();
        Assert.Throws<SourcePublisher.Crash>(() => Failing(project, "commit", -1, () => new SourcePublisher.Crash()).Publish(ScriptChange(project), "cold admission", Token));
        string id = Assert.Single(new SourcePublisher(project.Root).FindInterrupted(Token)).SaveId;
        string folder = project.Full($"zstudio/recovery/{id}"), path = Path.Combine(folder, "manifest.json");
        byte[] valid = File.ReadAllBytes(path);
        var json = JsonNode.Parse(valid)!; json["folders"] = new JsonArray("", "", "");
        File.WriteAllText(path, json.ToJsonString());
        var sources = project.Sources(); var history = JournalContents(folder);
        SourcePublisher limited = new(project.Root) { JournalFoldersLimit = 2 };
        var error = Assert.Throws<SourceRecoveryRequiredException>(() => limited.FindInterrupted(Token));
        Assert.Equal(id, error.SaveId); Assert.Contains("more than 2 folders", error.Message);
        Assert.Throws<SourceRecoveryRequiredException>(() => limited.SaveFiles(id));
        foreach (var action in Enum.GetValues<SourceRecoveryAction>())
            Assert.Throws<SourceRecoveryRequiredException>(() => limited.Resolve(id, action, Token));
        Assert.Throws<SourceRecoveryRequiredException>(() => limited.Publish([new("gamegen/next.gw", null, [1])], "blocked save", Token));
        Assert.Equal(sources, project.Sources()); Assert.Equal(history, JournalContents(folder));
        File.WriteAllBytes(path, valid);
        Assert.True(limited.Resolve(id, SourceRecoveryAction.RollBack, Token).Resolved);
        Assert.Equal(original, project.Sources()); Assert.Empty(project.Leftovers());
    }
}
