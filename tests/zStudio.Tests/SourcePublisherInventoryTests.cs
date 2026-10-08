using Recoil.Zbd.Core.Sources;
using Xunit;

namespace Recoil.Zbd.Tests;

public sealed partial class SourcePublisherTests
{
    [Fact]
    public void InventoryRefusalPrecedesJournalAndSourceMutation()
    {
        using Project project = new(); var before = project.Sources();
        bool prepared = false;
        var publisher = new SourcePublisher(project.Root) { InventoryLimit = 0, Fault = (_, _) => prepared = true };
        Assert.Throws<InventoryCapacityException>(() => publisher.Publish(ThreeFiles(project), "refused inventory", Token));
        Assert.False(prepared); Assert.Equal(before, project.Sources()); Assert.Empty(project.Leftovers());
        Assert.Equal(3, new SourcePublisher(project.Root).Publish(ThreeFiles(project), "retry", Token).Written.Count);
    }

    [Fact]
    public void CleanupExhaustionCannotPreventRestoringMovedOriginals()
    {
        using Project project = new(); var before = project.Sources();
        var publisher = new SourcePublisher(project.Root)
        {
            CleanupInventoryLimit = 0,
            Fault = (step, index) => { if (step == "install" && index == 0) throw new IOException("controlled install refusal"); }
        };
        var failure = Assert.Throws<IOException>(() => publisher.Publish(ThreeFiles(project), "undo", Token));
        Assert.Equal("controlled install refusal", failure.Message);
        Assert.Equal(before, project.Sources());
        new SourcePublisher(project.Root).Publish(ThreeFiles(project), "fresh retry", Token);
        Assert.Empty(project.Leftovers());
    }

    [Fact]
    public void CommittedCleanupExhaustionLeavesSafeRemnantsAndRetryCleansThem()
    {
        using Project project = new();
        var publisher = new SourcePublisher(project.Root) { CleanupInventoryLimit = 0 };
        var committed = publisher.Publish(ThreeFiles(project), "committed", Token);
        Assert.Equal(3, committed.Written.Count);
        Assert.Equal(Text("GRAVITY ( -4.9 )\n"), project.Read(Ai));
        Assert.NotEmpty(project.Leftovers());
        new SourcePublisher(project.Root).Publish(ScriptChange(project), "next save", Token);
        Assert.Empty(project.Leftovers());
        Assert.Equal(Text("GRAVITY ( -4.9 )\n"), project.Read(Ai));
    }

    [Fact]
    public void RecoveryDiscoveryRefusesCompletelyAndFreshRetryPreservesCommittedState()
    {
        using Project project = new();
        var publisher = Failing(project, "cleanup", -1, () => new IOException("leave committed journal"));
        publisher.Publish(ThreeFiles(project), "committed", Token);
        var saved = project.Sources();
        var leftovers = project.Leftovers();
        Assert.Throws<InventoryCapacityException>(() => new SourcePublisher(project.Root) { InventoryLimit = 0 }.FindInterrupted(Token));
        Assert.Equal(saved, project.Sources()); Assert.Equal(leftovers, project.Leftovers());
        var retry = new SourcePublisher(project.Root);
        var found = Assert.Single(retry.FindInterrupted(Token));
        Assert.True(found.Committed); Assert.Equal([Ai, Added, Old], found.Files.Select(f => f.Relative));
        Assert.True(retry.Resolve(found.SaveId, SourceRecoveryAction.Complete, Token).Resolved);
        Assert.Equal(saved, project.Sources()); Assert.Empty(project.Leftovers());
    }
}
