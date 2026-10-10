using System.Security.Cryptography;
using System.Text;
using Recoil.Zbd.Core;
using Recoil.Zbd.Core.Sources;
using Xunit;

namespace Recoil.Zbd.Tests;

public sealed partial class SourcePublisherTests
{
    [Fact]
    public void ANewFileUnderSixtyFiveFoldersCanBeRecoveredAndRolledBack()
    {
        using Project project = new();
        var before = project.Sources();
        string relative = "data/" + string.Concat(Enumerable.Repeat("a/", 65)) + "file.zrd";
        var publisher = Failing(project, "commit", -1, () => new SourcePublisher.Crash());
        Assert.Throws<SourcePublisher.Crash>(() => publisher.Publish([new(relative, null, [1, 2, 3])], "deep save", Token));
        Assert.Equal(new byte[] { 1, 2, 3 }, project.Read(relative));
        publisher = new(project.Root);
        var recovery = Assert.Single(publisher.FindInterrupted(Token));
        Assert.Equal(relative, Assert.Single(recovery.Files).Relative);
        Assert.True(publisher.Resolve(recovery.SaveId, SourceRecoveryAction.RollBack, Token).Resolved);
        Assert.Equal(before, project.Sources()); Assert.Empty(project.Leftovers());
        publisher.Publish(ScriptChange(project), "next save", Token);
        Assert.Empty(publisher.FindInterrupted(Token));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void JournalBudgetsRefuseBeforeCreatingWorkingDataOrSources(bool bytes)
    {
        using Project project = new();
        var before = project.Sources();
        SourcePublisher publisher = bytes ? new(project.Root) { ManifestBytesLimit = 2048 } : new(project.Root) { JournalFoldersLimit = 2 };
        string description = bytes ? new string('\u2603', 512) : "folders";
        var error = Assert.Throws<InvalidDataException>(() => publisher.Publish([new("data/a/b/c/file.zrd", null, [1])], description, Token));
        Assert.Contains(bytes ? "byte budget" : "folders", error.Message);
        Assert.Equal(before, project.Sources()); Assert.False(Directory.Exists(project.Full("zstudio")));
        new SourcePublisher(project.Root).Publish(ScriptChange(project), "valid next save", Token);
        Assert.Empty(project.Leftovers());
    }

    [Fact]
    public void RepeatedFailedCompletionDoesNotGrowEventsAndCanThenComplete()
    {
        using Project project = new();
        byte[] expected = Text("load m1\nload m2\n");
        Assert.Throws<SourcePublisher.Crash>(() => Failing(project, "prepared", -1, () => new SourcePublisher.Crash()).Publish(ScriptChange(project), "retry", Token));
        SourcePublisher publisher = new(project.Root);
        string id = Assert.Single(publisher.FindInterrupted(Token)).SaveId;
        string log = project.Full($"zstudio/recovery/{id}/events.log");
        FileStream? blocker = null;
        publisher.Fault = (step, _) =>
        {
            if (step == "complete") blocker = new(project.Full($"zstudio/staging/{id}/0.tmp"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        };
        long? firstLength = null;
        for (int attempt = 0; attempt < 8; attempt++)
        {
            try
            {
                var result = publisher.Resolve(id, SourceRecoveryAction.Complete, Token);
                Assert.False(result.Resolved); Assert.Single(result.Conflicts);
            }
            finally { blocker?.Dispose(); blocker = null; }
            long length = new FileInfo(log).Length;
            firstLength ??= length; Assert.Equal(firstLength.Value, length);
            Assert.Single(publisher.FindInterrupted(Token));
        }
        // The failure was after intent and after the original moved aside, not the read-only blocker preflight.
        Assert.Contains("|intent|0|", File.ReadAllText(log));
        Assert.Contains("|held|0|", File.ReadAllText(log));
        publisher.Fault = null;
        Assert.True(publisher.Resolve(id, SourceRecoveryAction.Complete, Token).Resolved);
        Assert.Equal(expected, project.Read(Script)); Assert.Empty(project.Leftovers());
    }

    [Fact]
    public void RepeatedRollbackConflictsDoNotConsumeTheRoomNeededToFinish()
    {
        using Project project = new();
        var before = project.Sources();
        Assert.Throws<SourcePublisher.Crash>(() => Failing(project, "commit", -1, () => new SourcePublisher.Crash()).Publish(ScriptChange(project), "retry undo", Token));
        SourcePublisher publisher = new(project.Root) { Fault = (step, _) => { if (step == "undo") throw new IOException("temporary conflict"); } };
        string id = Assert.Single(publisher.FindInterrupted(Token)).SaveId, log = project.Full($"zstudio/recovery/{id}/events.log");
        long? firstLength = null;
        for (int attempt = 0; attempt < 8; attempt++)
        {
            var result = publisher.Resolve(id, SourceRecoveryAction.RollBack, Token);
            Assert.False(result.Resolved); Assert.Single(result.Conflicts);
            long length = new FileInfo(log).Length;
            firstLength ??= length; Assert.Equal(firstLength.Value, length);
            Assert.Single(publisher.FindInterrupted(Token));
        }
        publisher.Fault = null;
        Assert.True(publisher.Resolve(id, SourceRecoveryAction.RollBack, Token).Resolved);
        Assert.Equal(before, project.Sources()); Assert.Empty(project.Leftovers());
    }

    [Fact]
    public void ACommitAfterAVoidedCommitIsStillRecorded()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Holding a journal against cleanup requires Windows sharing semantics.");
        using Project project = new();
        Assert.Throws<SourcePublisher.Crash>(() => Failing(project, "commit", -1, () => new SourcePublisher.Crash()).Publish(ScriptChange(project), "commit transition", Token));
        SourcePublisher publisher = new(project.Root);
        string id = Assert.Single(publisher.FindInterrupted(Token)).SaveId, journal = project.Full($"zstudio/recovery/{id}");
        string log = Path.Combine(journal, "events.log");
        int count = File.ReadAllLines(log).Length;
        File.AppendAllText(log, Record(++count, "committed") + Record(++count, "rollback"));
        Assert.False(Assert.Single(publisher.FindInterrupted(Token)).Committed);
        using (DirectoryLease held = new())
        {
            held.Hold(journal);
            Assert.False(publisher.Resolve(id, SourceRecoveryAction.Complete, Token).Resolved); // Only retirement is blocked.
            Assert.True(Assert.Single(publisher.FindInterrupted(Token)).Committed);
            Assert.Equal(2, File.ReadAllLines(log).Count(line => line.Contains("|committed|", StringComparison.Ordinal)));
        }
        Assert.True(publisher.Resolve(id, SourceRecoveryAction.Complete, Token).Resolved);
        Assert.Empty(project.Leftovers());

        static string Record(int sequence, string step)
        {
            string body = $"{sequence}|{step}|-1";
            return $"{body}|{Convert.ToHexStringLower(SHA256.HashData(Encoding.ASCII.GetBytes(body)))[..16]}\n";
        }
    }
}
