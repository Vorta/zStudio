using Recoil.Zbd.Core.Sources;
using Xunit;

namespace Recoil.Zbd.Tests;

public sealed partial class SourcePublisherTests
{
    [Fact]
    public void ManyFilesSharingADeepParentAreRefusedBeforePrefixExpansion()
    {
        using Project project = new();
        string prefix = "data/" + string.Concat(Enumerable.Repeat("a/", 400));
        SourceFileWrite[] writes = [.. Enumerable.Range(0, 500).Select(i => new SourceFileWrite(prefix + $"file{i}.zrd", null, [1]))];
        var before = project.Sources();
        SourcePublisher publisher = new(project.Root);
        long allocated = GC.GetAllocatedBytesForCurrentThread();
        var error = Assert.Throws<InvalidDataException>(() => publisher.Publish(writes, "deep batch", Token));
        allocated = GC.GetAllocatedBytesForCurrentThread() - allocated;
        Assert.Contains("planning budget", error.Message);
        Assert.True(allocated < 4L * 1024 * 1024, $"Rejected planning allocated {allocated:N0} bytes.");
        Assert.Equal(before, project.Sources());
        Assert.False(Directory.Exists(project.Full("zstudio")));
        publisher.Publish(ScriptChange(project), "valid after refusal", Token);
        Assert.Empty(publisher.FindInterrupted(Token));
    }

    [Fact]
    public void ExistingAndUnchangedPathsAlsoConsumeThePlanningBudget()
    {
        using Project project = new();
        string prefix = "data/" + string.Concat(Enumerable.Repeat("a/", 20));
        Directory.CreateDirectory(project.Full(prefix));
        var writes = Enumerable.Range(0, 20).Select(i => new SourceFileWrite(prefix + $"file{i}.zrd", [1], [1])).ToList();
        foreach (var write in writes) File.WriteAllBytes(project.Full(write.Relative), write.Expected!);
        writes.AddRange(ScriptChange(project));
        var before = project.Sources();
        var error = Assert.Throws<InvalidDataException>(() => new SourcePublisher(project.Root) { PlanningPathBytesLimit = 32 * 1024 }
            .Publish(writes, "existing paths", Token));
        Assert.Contains("planning budget", error.Message);
        Assert.Equal(before, project.Sources());
        Assert.False(Directory.Exists(project.Full("zstudio")));
    }

    [Fact]
    public void SharedNewParentsRemainRecoverableWithoutRemovingExistingAncestors()
    {
        using Project project = new();
        string prefix = "data/m1/" + string.Concat(Enumerable.Repeat("a/", 65));
        SourceFileWrite[] writes =
        [
            new(prefix + "first.zrd", null, [1]),
            new(prefix + "second.zrd", null, [2]),
            new(prefix + "branch/third.zrd", null, [3]),
            new("data/m1/sibling/fourth.zrd", null, [4]),
        ];
        var before = project.Sources();
        SourcePublisher publisher = Failing(project, "commit", -1, () => new SourcePublisher.Crash());
        Assert.Throws<SourcePublisher.Crash>(() => publisher.Publish(writes, "shared parents", Token));
        foreach (var write in writes) Assert.Equal(write.Content, project.Read(write.Relative));
        publisher = new(project.Root);
        string id = Assert.Single(publisher.FindInterrupted(Token)).SaveId;
        Assert.True(publisher.Resolve(id, SourceRecoveryAction.RollBack, Token).Resolved);
        Assert.Equal(before, project.Sources());
        Assert.Empty(project.Leftovers());
        Assert.Equal(writes.Select(w => w.Relative), publisher.Publish(writes, "retry shared parents", Token).Written);
        foreach (var write in writes) Assert.Equal(write.Content, project.Read(write.Relative));
    }
}
