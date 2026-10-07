using System.IO;
using Recoil.Zbd.Core.Sources;
using Xunit;

namespace Recoil.Zbd.Tests;

public sealed class SourceRecoveryEventLineBudgetTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Fact]
    public void OversizedFinalRecordKeepsTheIntentNeededToRollBackANewFile()
    {
        using SourceWorldFixture fixture = new();
        const string relative = "data/new-eventline.txt";
        byte[] after = "new file"u8.ToArray();
        SourcePublisher interrupted = new(fixture.Project)
        {
            Fault = (step, _) => { if (step == "commit") throw new SourcePublisher.Crash(); }
        };
        Assert.Throws<SourcePublisher.Crash>(() => interrupted.Publish([new(relative, null, after)], "intent retained", Token));
        Assert.Equal(after, File.ReadAllBytes(fixture.Path(relative)));
        SourcePublisher publisher = new(fixture.Project);
        var original = Assert.Single(publisher.FindInterrupted(Token));
        string eventPath = fixture.Path(SourcePublisher.RecoveryFolder + "/" + original.SaveId + "/events.log");
        File.AppendAllText(eventPath, new string('|', 1024) + "\n");
        var found = Assert.Single(publisher.FindInterrupted(Token));
        Assert.False(found.Committed);
        Assert.Equal(SourceRecoveryFileState.After, Assert.Single(found.Files).State);

        // With no held original, rollback relies on the intent in the valid prefix before removing this new file.
        var undone = publisher.Resolve(found.SaveId, SourceRecoveryAction.RollBack, Token);
        Assert.True(undone.Resolved);
        Assert.Empty(undone.Conflicts);
        Assert.Equal([relative], undone.Changed);
        Assert.False(File.Exists(fixture.Path(relative)));
        Assert.Empty(publisher.FindInterrupted(Token));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void RecoveryBoundsDamagedLinesBeforeDecodingAndPreservesKnownFacts(bool hasLaterRecord)
    {
        using SourceWorldFixture fixture = new();
        const string relative = "data/eventline.txt";
        byte[] before = "before"u8.ToArray(), after = "after"u8.ToArray();
        fixture.Write(relative, before);
        SourcePublisher interrupted = new(fixture.Project)
        {
            Fault = (step, _) => { if (step == "stage") throw new SourcePublisher.Crash(); }
        };
        Assert.Throws<SourcePublisher.Crash>(() => interrupted.Publish([new(relative, before, after)], "event line bounds", Token));
        SourcePublisher publisher = new(fixture.Project);
        var original = Assert.Single(publisher.FindInterrupted(Token)); // Warm public parsing/inspection before measurement.
        string eventPath = fixture.Path(SourcePublisher.RecoveryFolder + "/" + original.SaveId + "/events.log");
        byte[] prefix = File.ReadAllBytes(eventPath); // A real checksummed prepared record, kept ahead of the damaged tail.
        Assert.NotEmpty(prefix);
        byte[] damaged = new byte[prefix.Length + 1024 * 1024 + 1 + (hasLaterRecord ? prefix.Length : 0)];
        prefix.CopyTo(damaged, 0);
        damaged.AsSpan(prefix.Length, 1024 * 1024).Fill((byte)'|');
        damaged[prefix.Length + 1024 * 1024] = (byte)'\n';
        if (hasLaterRecord) prefix.CopyTo(damaged, prefix.Length + 1024 * 1024 + 1);
        File.WriteAllBytes(eventPath, damaged);

        long start = GC.GetAllocatedBytesForCurrentThread();
        if (hasLaterRecord)
        {
            var failure = Assert.Throws<SourceRecoveryRequiredException>(() => publisher.FindInterrupted(Token));
            Assert.Contains("damaged at record 2", failure.Message);
        }
        else
        {
            var found = Assert.Single(publisher.FindInterrupted(Token));
            Assert.Equal(original.SaveId, found.SaveId);
            Assert.False(found.Committed);
            Assert.Equal(SourceRecoveryFileState.Before, Assert.Single(found.Files).State);
        }
        long allocated = GC.GetAllocatedBytesForCurrentThread() - start;
        Assert.True(allocated < 8L * 1024 * 1024, $"A one-MiB damaged event line allocated {allocated:N0} bytes during recovery inspection.");
        Assert.Equal(before, File.ReadAllBytes(fixture.Path(relative)));
        Assert.Equal(damaged, File.ReadAllBytes(eventPath)); // Inspection never truncates the log or loses known facts.

        if (hasLaterRecord)
        {
            Assert.Throws<SourceRecoveryRequiredException>(() => publisher.Resolve(original.SaveId, SourceRecoveryAction.Complete, Token));
            Assert.Equal(before, File.ReadAllBytes(fixture.Path(relative)));
            File.WriteAllBytes(eventPath, prefix); // Restore the recorded facts for a valid same-publisher follow-up.
        }
        var completed = publisher.Resolve(original.SaveId, SourceRecoveryAction.Complete, Token);
        Assert.True(completed.Resolved);
        Assert.Empty(completed.Conflicts);
        Assert.Equal([relative], completed.Changed);
        Assert.Equal(after, File.ReadAllBytes(fixture.Path(relative)));
        Assert.Empty(publisher.FindInterrupted(Token));
    }
}
