using Recoil.Zbd.Core.Sources;
using Xunit;

namespace Recoil.Zbd.Tests;

public sealed partial class SourcePublisherTests
{
    [Theory]
    [InlineData(SourceRecoveryAction.RollBack)]
    [InlineData(SourceRecoveryAction.Complete)]
    public void FailedCompletionReportsRemovedOriginalAndCanThenRecover(SourceRecoveryAction next)
    {
        using Project project = new();
        var before = project.Sources();
        byte[] original = project.Read(Script), replacement = Text("load m1\nload m2\n");
        Assert.Throws<SourcePublisher.Crash>(() => Failing(project, "prepared", -1, () => new SourcePublisher.Crash())
            .Publish(ScriptChange(project), "partial completion", Token));
        SourcePublisher publisher = new(project.Root);
        string id = Assert.Single(publisher.FindInterrupted(Token)).SaveId;
        FileStream? blocker = null;
        publisher.Fault = (step, _) =>
        {
            if (step == "complete") blocker = new(project.Full($"zstudio/staging/{id}/0.tmp"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        };
        try
        {
            var failed = publisher.Resolve(id, SourceRecoveryAction.Complete, Token);
            Assert.False(failed.Resolved);
            Assert.Equal(Script, Assert.Single(failed.Changed)); // Also the MCP changedCount's single source.
            Assert.Equal(Script, Assert.Single(failed.Conflicts).Relative);
        }
        finally { blocker?.Dispose(); blocker = null; }
        var missing = new SortedDictionary<string, string?>(before, StringComparer.Ordinal);
        missing.Remove(Script);
        Assert.Equal(missing, project.Sources());
        Assert.Equal(original, project.Read($"zstudio/recovery/{id}/held/0.bin"));
        var state = Assert.Single(Assert.Single(publisher.FindInterrupted(Token)).Files);
        Assert.Equal(SourceRecoveryFileState.Missing, state.State);
        Assert.True(state.HeldOriginal);

        // An identical failed retry does not remove it again: Changed describes this invocation's mutations.
        try
        {
            var failedAgain = publisher.Resolve(id, SourceRecoveryAction.Complete, Token);
            Assert.False(failedAgain.Resolved);
            Assert.Empty(failedAgain.Changed);
            Assert.Single(failedAgain.Conflicts);
        }
        finally { blocker?.Dispose(); }
        Assert.Equal(missing, project.Sources());
        Assert.Equal(original, project.Read($"zstudio/recovery/{id}/held/0.bin"));

        publisher.Fault = null;
        var recovered = publisher.Resolve(id, next, Token);
        Assert.True(recovered.Resolved);
        Assert.Equal(Script, Assert.Single(recovered.Changed));
        Assert.Empty(recovered.Conflicts);
        Assert.Equal(next == SourceRecoveryAction.RollBack ? original : replacement, project.Read(Script));
        var expected = new SortedDictionary<string, string?>(before, StringComparer.Ordinal);
        if (next == SourceRecoveryAction.Complete) expected[Script] = "load m1\nload m2\n";
        Assert.Equal(expected, project.Sources());
        Assert.Empty(project.Leftovers());
    }

    [Fact]
    public void CompletionCountsModifiedCreatedAndDeletedFilesOnceEach()
    {
        using Project project = new();
        var before = project.Sources();
        Assert.Throws<SourcePublisher.Crash>(() => Failing(project, "prepared", -1, () => new SourcePublisher.Crash())
            .Publish(ThreeFiles(project), "all changes", Token));
        SourcePublisher publisher = new(project.Root);
        string id = Assert.Single(publisher.FindInterrupted(Token)).SaveId;
        var result = publisher.Resolve(id, SourceRecoveryAction.Complete, Token);
        Assert.True(result.Resolved);
        Assert.Equal([Ai, Added, Old], result.Changed);
        Assert.Empty(result.Conflicts);
        Assert.Equal(Saved(before), project.Sources());
        Assert.Empty(project.Leftovers());
    }

    [Fact]
    public void FailedStagingOfANewFileReportsNoSourceChange()
    {
        using Project project = new();
        var before = project.Sources();
        const string created = "data/m1/created.zrd";
        byte[] content = Text("CREATED ( 1 )\n");
        Assert.Throws<SourcePublisher.Crash>(() => Failing(project, "prepared", -1, () => new SourcePublisher.Crash())
            .Publish([new(created, null, content)], "new file", Token));
        SourcePublisher publisher = new(project.Root);
        string id = Assert.Single(publisher.FindInterrupted(Token)).SaveId;
        FileStream? blocker = null;
        publisher.Fault = (step, _) =>
        {
            if (step == "complete") blocker = new(project.Full($"zstudio/staging/{id}/0.tmp"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        };
        try
        {
            var failed = publisher.Resolve(id, SourceRecoveryAction.Complete, Token);
            Assert.False(failed.Resolved);
            Assert.Empty(failed.Changed);
            Assert.Equal(created, Assert.Single(failed.Conflicts).Relative);
            Assert.Equal(before, project.Sources());
        }
        finally { blocker?.Dispose(); }
        publisher.Fault = null;
        var complete = publisher.Resolve(id, SourceRecoveryAction.Complete, Token);
        Assert.True(complete.Resolved);
        Assert.Equal(created, Assert.Single(complete.Changed));
        Assert.Equal(content, project.Read(created));
        Assert.Empty(project.Leftovers());
    }

    [Fact]
    public void CancellationAfterFailedStagingNamesTheRemovedOriginal()
    {
        using Project project = new();
        var before = project.Sources();
        Assert.Throws<SourcePublisher.Crash>(() => Failing(project, "prepared", -1, () => new SourcePublisher.Crash())
            .Publish(ThreeFiles(project), "cancel partial completion", Token));
        SourcePublisher publisher = new(project.Root);
        string id = Assert.Single(publisher.FindInterrupted(Token)).SaveId;
        using CancellationTokenSource cancellation = new();
        FileStream? blocker = null;
        publisher.Fault = (step, index) =>
        {
            if (step != "complete" || index != 0) return;
            blocker = new(project.Full($"zstudio/staging/{id}/0.tmp"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
            cancellation.Cancel(); // This file finishes its attempt; cancellation is observed before the next file.
        };
        try
        {
            var stopped = Assert.ThrowsAny<OperationCanceledException>(() => publisher.Resolve(id, SourceRecoveryAction.Complete, cancellation.Token));
            Assert.IsAssignableFrom<OperationCanceledException>(stopped.InnerException);
            Assert.Contains($"changed 1 file ({Ai})", stopped.Message);
            Assert.Contains($"1 file could not be resolved ({Ai} could not be completed:", stopped.Message);
            Assert.Contains("still needs a decision", stopped.Message);
        }
        finally { blocker?.Dispose(); }
        var missing = new SortedDictionary<string, string?>(before, StringComparer.Ordinal);
        missing.Remove(Ai);
        Assert.Equal(missing, project.Sources());
        Assert.Equal(Text("GRAVITY ( -9.8 )\n"), project.Read($"zstudio/recovery/{id}/held/0.bin"));
        publisher.Fault = null;
        var restored = publisher.Resolve(id, SourceRecoveryAction.RollBack, Token);
        Assert.True(restored.Resolved);
        Assert.Equal(Ai, Assert.Single(restored.Changed));
        Assert.Equal(before, project.Sources());
        Assert.Empty(project.Leftovers());
    }
}
