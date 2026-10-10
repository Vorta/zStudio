using Recoil.Zbd.Core.Sources;
using System.Text.Json.Nodes;
using Xunit;

namespace Recoil.Zbd.Tests;

public sealed partial class SourcePublisherTests
{
    [Fact]
    public void RecoveryBoundsAllManifestsLogsAndFileRowsTogether()
    {
        using Project project = new();
        var crashing = Failing(project, "commit", -1, () => new SourcePublisher.Crash());
        Assert.Throws<SourcePublisher.Crash>(() => crashing.Publish(ScriptChange(project), "crash", Token));
        string id = Assert.Single(new SourcePublisher(project.Root).FindInterrupted(Token)).SaveId;
        string original = project.Full($"zstudio/recovery/{id}");
        string secondId = id[..^8] + (id.EndsWith("00000000", StringComparison.Ordinal) ? "00000001" : "00000000");
        string second = project.Full($"zstudio/recovery/{secondId}"); Directory.CreateDirectory(second);
        var json = JsonNode.Parse(File.ReadAllText(Path.Combine(original, "manifest.json")))!; json["saveId"] = secondId;
        File.WriteAllText(Path.Combine(second, "manifest.json"), json.ToJsonString());
        File.Copy(Path.Combine(original, "events.log"), Path.Combine(second, "events.log"));
        long bytes = new[] { original, second }.Sum(folder => new[] { "manifest.json", "events.log" }.Sum(name => new FileInfo(Path.Combine(folder, name)).Length));
        Assert.Equal(2, new SourcePublisher(project.Root) { RecoveryBytesLimit = bytes, RecoveryFilesLimit = 2 }.FindInterrupted(Token).Count);
        Assert.Throws<SourceRecoveryRequiredException>(() => new SourcePublisher(project.Root) { RecoveryBytesLimit = bytes - 1 }.FindInterrupted(Token));
        Assert.Throws<SourceRecoveryRequiredException>(() => new SourcePublisher(project.Root) { RecoveryFilesLimit = 1 }.FindInterrupted(Token));
        json["folders"] = new JsonArray((JsonNode?)null);
        File.WriteAllText(Path.Combine(second, "manifest.json"), json.ToJsonString());
        Assert.Throws<SourceRecoveryRequiredException>(() => new SourcePublisher(project.Root).FindInterrupted(Token));
    }

    [Theory]
    [InlineData("recovery")]
    [InlineData("staging")]
    public void LinkedWorkingDirectoryCannotPublishOrTidyOutsideProject(string folder)
    {
        using Project project = new(), outside = new();
        string foreign = outside.Full("20260101T000000000Z-1234abcd");
        Directory.CreateDirectory(foreign);
        string sentinel = Path.Combine(foreign, "sentinel.txt"); File.WriteAllText(sentinel, "keep");
        Directory.CreateDirectory(project.Full("zstudio"));
        string link = project.Full("zstudio/" + folder);
        var before = project.Sources();
        Directory.CreateSymbolicLink(link, outside.Root);
        try
        {
            Assert.Throws<IOException>(() => new SourcePublisher(project.Root).Publish(ThreeFiles(project), "linked", Token));
            Assert.Equal("keep", File.ReadAllText(sentinel));
            Assert.Equal(before, project.Sources());
        }
        finally { Directory.Delete(link); }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void DamagedOrMissingHeldOriginalNeverReplacesInstalledFile(bool missing)
    {
        using Project project = new();
        var publisher = Failing(project, "commit", -1, () => new SourcePublisher.Crash());
        Assert.Throws<SourcePublisher.Crash>(() => publisher.Publish(ScriptChange(project), "crash", Token));
        publisher.Fault = null;
        string id = Assert.Single(publisher.FindInterrupted(Token)).SaveId;
        string held = project.Full($"zstudio/recovery/{id}/held/0.bin");
        if (missing) File.Delete(held); else File.WriteAllText(held, "corrupt");
        byte[] installed = project.Read(Script);
        var result = publisher.Resolve(id, SourceRecoveryAction.RollBack, Token);
        Assert.False(result.Resolved);
        Assert.NotEmpty(result.Conflicts);
        Assert.Empty(result.Changed);
        Assert.Equal(installed, project.Read(Script));
        Assert.True(Directory.Exists(project.Full($"zstudio/recovery/{id}")));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void RecoveryNeverFollowsLinksInsideJournalOrStaging(bool stagedFile)
    {
        using Project project = new(), outside = new();
        var publisher = Failing(project, stagedFile ? "install" : "commit", stagedFile ? 0 : -1, () => new SourcePublisher.Crash());
        Assert.Throws<SourcePublisher.Crash>(() => publisher.Publish(ScriptChange(project), "crash", Token));
        publisher.Fault = null;
        string id = Assert.Single(publisher.FindInterrupted(Token)).SaveId;
        string foreign = outside.Full("0.bin"), link;
        if (stagedFile)
        {
            File.WriteAllText(foreign, "sentinel");
            link = project.Full($"zstudio/staging/{id}/0.tmp"); File.Delete(link); File.CreateSymbolicLink(link, foreign);
        }
        else
        {
            link = project.Full($"zstudio/recovery/{id}/held");
            File.Move(Path.Combine(link, "0.bin"), foreign); Directory.Delete(link); Directory.CreateSymbolicLink(link, outside.Root);
        }
        byte[] expected = File.ReadAllBytes(foreign);
        try
        {
            try { publisher.Resolve(id, stagedFile ? SourceRecoveryAction.Complete : SourceRecoveryAction.RollBack, Token); }
            catch (IOException) { }
            Assert.True(File.Exists(foreign)); Assert.Equal(expected, File.ReadAllBytes(foreign));
        }
        finally
        {
            if (Path.Exists(link)) { if (stagedFile) File.Delete(link); else Directory.Delete(link); }
        }
    }
}
