using Recoil.Zbd.Core.Sources;
using Xunit;

namespace Recoil.Zbd.Tests;

public sealed partial class SourcePublisherTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void PrepublicationRecursiveCleanupObservesCancellationAndCanRetry(bool committed)
    {
        using Project project = new();
        using CancellationTokenSource cancel = CancellationTokenSource.CreateLinkedTokenSource(Token);
        Assert.Throws<SourcePublisher.Crash>(() => Failing(project, committed ? "cleanup" : "prepared", -1,
            () => new SourcePublisher.Crash()).Publish(ScriptChange(project), "previous save", Token));
        var journal = Assert.Single(new SourcePublisher(project.Root).FindInterrupted(Token));
        if (!committed)
        {
            // A process may stop after the journal was renamed for deletion but before its descendants were removed.
            Directory.Move(project.Full($"zstudio/recovery/{journal.SaveId}"), project.Full($"zstudio/recovery/{journal.SaveId}.removed"));
        }
        var before = project.Sources();
        int visits = 0;
        SourcePublisher publisher = new(project.Root)
        {
            CleanupVisiting = () => { if (++visits == 2) cancel.Cancel(); }
        };
        SourceFileWrite[] next = [new(Ai, project.Read(Ai), Text("GRAVITY ( -1 )\n"))];
        Assert.ThrowsAny<OperationCanceledException>(() => publisher.Publish(next, "canceled before publication", cancel.Token));
        Assert.Equal(2, visits);
        Assert.Equal(before, project.Sources());
        Assert.NotEmpty(project.Leftovers());
        publisher.CleanupVisiting = null;
        Assert.Equal(Ai, Assert.Single(publisher.Publish(next, "retry", Token).Written));
        Assert.Equal(next[0].Content, project.Read(Ai));
        Assert.Empty(project.Leftovers());
    }

    [Fact]
    public void CancellationDuringCommittedCleanupDoesNotReclassifyAnAcceptedSave()
    {
        using Project project = new();
        using CancellationTokenSource cancel = CancellationTokenSource.CreateLinkedTokenSource(Token);
        int visits = 0;
        SourcePublisher publisher = new(project.Root) { CleanupVisiting = () => { visits++; cancel.Cancel(); } };
        var writes = ScriptChange(project);
        var saved = publisher.Publish(writes, "accepted save", cancel.Token);
        Assert.True(visits > 0);
        Assert.True(cancel.IsCancellationRequested);
        Assert.Equal(Script, Assert.Single(saved.Written));
        Assert.Equal(writes[0].Content, project.Read(Script));
        Assert.Empty(project.Leftovers());
    }
}
