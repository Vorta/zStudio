using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using Recoil.Zbd.Core.Sources;
using Xunit;
using State = Recoil.Zbd.Core.Sources.SourceRecoveryFileState;

namespace Recoil.Zbd.Tests;

/// <summary>Journaled multi-file source saves: publication, undo, crash recovery and refusal of unexpected files.</summary>
public sealed partial class SourcePublisherTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;
    private static byte[] Text(string text) => Encoding.ASCII.GetBytes(text);
    private const string Ai = "data/m1/ai.zrd", Added = "data/m1/new/added.zrd", Old = "data/m1/old.zrd", Script = "gamegen/m1.gw";

    /// <summary>A temporary source project with a script, a resource a save modifies and one it deletes.</summary>
    private sealed class Project : IDisposable
    {
        public Project(string? parent = null)
        {
            Root = Path.Combine(parent ?? Path.GetTempPath(), "zstudio-publisher-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Full("data/m1")); Directory.CreateDirectory(Full("gamegen"));
            File.WriteAllBytes(Full(Ai), Text("GRAVITY ( -9.8 )\n"));
            File.WriteAllBytes(Full(Old), Text("OLD ( 1 )\n"));
            File.WriteAllBytes(Full(Script), Text("load m1\n"));
        }
        public string Root { get; }
        public string Full(string relative) => Path.Combine(Root, relative.Replace('/', Path.DirectorySeparatorChar));
        public byte[] Read(string relative) => File.ReadAllBytes(Full(relative));
        /// <summary>Every source file (with its text) and folder (null), leaving out zStudio's working data.</summary>
        public SortedDictionary<string, string?> Sources()
        {
            SortedDictionary<string, string?> entries = new(StringComparer.Ordinal);
            foreach (string path in Directory.EnumerateFileSystemEntries(Root, "*", SearchOption.AllDirectories))
            {
                string relative = Path.GetRelativePath(Root, path).Replace('\\', '/');
                if (relative == "zstudio" || relative.StartsWith("zstudio/", StringComparison.Ordinal)) continue;
                entries[relative] = File.Exists(path) ? Encoding.ASCII.GetString(File.ReadAllBytes(path)) : null;
            }
            return entries;
        }
        /// <summary>Journal folders in the recovery folder, and save folders in staging.</summary>
        public string[] Leftovers() =>
        [
            .. Entries("zstudio/recovery").Where(n => Path.GetFileName(n) is not (".lock" or "abandoned")),
            .. Entries("zstudio/staging"),
        ];
        private IEnumerable<string> Entries(string relative) => Directory.Exists(Full(relative)) ? Directory.EnumerateFileSystemEntries(Full(relative)).Select(p => relative + "/" + Path.GetFileName(p)) : [];
        public void Dispose()
        {
            try
            {
                foreach (string file in Directory.EnumerateFiles(Root, "*", SearchOption.AllDirectories)) File.SetAttributes(file, FileAttributes.Normal);
                Directory.Delete(Root, true);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
    }

    /// <summary>Modify, create (in a new folder) and delete.</summary>
    private static SourceFileWrite[] ThreeFiles(Project project) =>
    [
        new(Ai, project.Read(Ai), Text("GRAVITY ( -4.9 )\n")),
        new(Added, null, Text("ADDED ( 2 )\n")),
        new(Old, project.Read(Old), null),
    ];
    private static SortedDictionary<string, string?> Saved(SortedDictionary<string, string?> before)
    {
        SortedDictionary<string, string?> after = new(before, StringComparer.Ordinal)
        {
            [Ai] = "GRAVITY ( -4.9 )\n", ["data/m1/new"] = null, [Added] = "ADDED ( 2 )\n",
        };
        after.Remove(Old);
        return after;
    }
    private static SourceFileWrite[] ScriptChange(Project project) => [new(Script, project.Read(Script), Text("load m1\nload m2\n"))];
    private static SourcePublisher Failing(Project project, string step, int index, Func<Exception> failure) =>
        new(project.Root) { Fault = (s, i) => { if (s == step && i == index) throw failure(); } };

    /// <summary>Every step of the three-file save, in the order the publisher announces them.</summary>
    private static readonly (string Step, int Index)[] SaveSteps =
    [
        ("after", 0), ("after", 1), ("manifest", -1), ("prepared", -1), ("stage", 0), ("stage", 1),
        ("intent", 0), ("hold", 0), ("install", 0), ("intent", 1), ("hold", 1), ("install", 1), ("intent", 2), ("hold", 2),
        ("commit", -1), ("cleanup", -1),
    ];
    public static TheoryData<string, int> FailureCases()
    {
        TheoryData<string, int> data = [];
        foreach (var (step, index) in SaveSteps) data.Add(step, index);
        return data;
    }
    public static TheoryData<string, int, SourceRecoveryAction> CrashCases()
    {
        TheoryData<string, int, SourceRecoveryAction> data = [];
        foreach (var (step, index) in SaveSteps) { data.Add(step, index, SourceRecoveryAction.RollBack); data.Add(step, index, SourceRecoveryAction.Complete); }
        return data;
    }

    [Fact]
    public void ThreeFilesAreModifiedCreatedAndDeletedExactly()
    {
        using Project project = new();
        var before = project.Sources(); List<(string, int)> steps = [];
        SourcePublisher publisher = new(project.Root) { Fault = (step, index) => steps.Add((step, index)) };
        var result = publisher.Publish(ThreeFiles(project), "Move the m1 gravity", Token);
        Assert.Equal([Ai, Added, Old], result.Written);
        Assert.Matches("^[0-9]{8}T[0-9]{9}Z-[0-9a-f]{8}$", result.SaveId);
        Assert.Equal(Saved(before), project.Sources());
        Assert.Empty(project.Leftovers());
        Assert.Empty(publisher.FindInterrupted(Token));
        // The fault cases below cover exactly the steps a save takes.
        Assert.Equal(SaveSteps, steps);
    }

    [Fact]
    public void AFileChangedBeforePublicationIsAConflictAndNothingChanges()
    {
        using Project project = new();
        var writes = ThreeFiles(project);
        File.WriteAllBytes(project.Full(Ai), Text("EXTERNAL\n"));
        var before = project.Sources();
        var conflict = Assert.Throws<SourceConflictException>(() => new SourcePublisher(project.Root).Publish(writes, "stale", Token));
        Assert.Equal([Ai], conflict.Files);
        Assert.Equal(before, project.Sources());
        Assert.Empty(project.Leftovers());
        // An unchanged entry is a precondition: the save is refused when that file changed, although it is not written.
        var checks = new[] { new SourceFileWrite(Old, Text("OLD ( 0 )\n"), Text("OLD ( 0 )\n")), new SourceFileWrite(Script, project.Read(Script), Text("load m2\n")) };
        Assert.Equal([Old], Assert.Throws<SourceConflictException>(() => new SourcePublisher(project.Root).Publish(checks, "stale", Token)).Files);
        Assert.Equal(before, project.Sources());
        // A file that a save creates must still be absent.
        File.WriteAllBytes(project.Full("data/m1/late.zrd"), Text("LATE\n"));
        before = project.Sources();
        Assert.Equal(["data/m1/late.zrd"], Assert.Throws<SourceConflictException>(() => new SourcePublisher(project.Root).Publish([new("data/m1/late.zrd", null, Text("MINE\n"))], "late", Token)).Files);
        Assert.Equal(before, project.Sources());
    }

    [Fact]
    public void AFileCreatedBeforeItsInstallIsNeverReplaced()
    {
        using Project project = new();
        var before = project.Sources();
        SourcePublisher publisher = new(project.Root)
        {
            Fault = (step, index) =>
            {
                if (step != "install" || index != 1) return;
                Directory.CreateDirectory(project.Full("data/m1/new")); File.WriteAllBytes(project.Full(Added), Text("FOREIGN\n"));
            },
        };
        var conflict = Assert.Throws<SourceConflictException>(() => publisher.Publish(ThreeFiles(project), "appear", Token));
        Assert.Equal([Added], conflict.Files);
        before["data/m1/new"] = null; before[Added] = "FOREIGN\n";
        Assert.Equal(before, project.Sources());
        Assert.Empty(project.Leftovers());
    }

    [Fact]
    public void AnOriginalChangedAfterTheCheckIsNeverMovedAside()
    {
        using Project project = new();
        var before = project.Sources();
        SourcePublisher publisher = new(project.Root) { Fault = (step, index) => { if (step == "hold" && index == 2) File.WriteAllBytes(project.Full(Old), Text("EDITED\n")); } };
        Assert.Equal([Old], Assert.Throws<SourceConflictException>(() => publisher.Publish(ThreeFiles(project), "edited", Token)).Files);
        before[Old] = "EDITED\n";
        Assert.Equal(before, project.Sources());
        Assert.Empty(project.Leftovers());
    }

    [Theory, MemberData(nameof(FailureCases))]
    public void AFailureAtAnyStepLeavesTheProjectUnchanged(string step, int index)
    {
        using Project project = new();
        var before = project.Sources();
        SourcePublisher publisher = Failing(project, step, index, () => new IOException($"Injected failure at {step} {index}."));
        if (step == "cleanup")
        {
            // After the commit, a failed cleanup does not fail the save; the next save retires the committed journal.
            publisher.Publish(ThreeFiles(project), "committed", Token);
            Assert.Equal(Saved(before), project.Sources());
            Assert.True(Assert.Single(publisher.FindInterrupted(Token)).Committed);
            new SourcePublisher(project.Root).Publish(ScriptChange(project), "next", Token);
            Assert.Empty(project.Leftovers());
            return;
        }
        var error = Assert.Throws<IOException>(() => publisher.Publish(ThreeFiles(project), "failure", Token));
        Assert.Equal($"Injected failure at {step} {index}.", error.Message);
        Assert.Equal(before, project.Sources());
        Assert.Empty(project.Leftovers());
        Assert.Empty(publisher.FindInterrupted(Token));
    }

    [Theory, MemberData(nameof(CrashCases))]
    public void ACrashAtAnyStepIsRolledBackOrCompletedExactly(string step, int index, SourceRecoveryAction action)
    {
        using Project project = new();
        var before = project.Sources(); var after = Saved(before);
        Assert.Throws<SourcePublisher.Crash>(() => Failing(project, step, index, () => new SourcePublisher.Crash()).Publish(ThreeFiles(project), "crash", Token));
        SourcePublisher publisher = new(project.Root); // As the next process sees the project.
        var cases = publisher.FindInterrupted(Token);
        if (cases.Count == 0)
        {
            // The process ended before the manifest existed, so no source changed; the next save removes the partial journal.
            Assert.Contains(step, new[] { "after", "manifest" });
            Assert.Equal(before, project.Sources());
            publisher.Publish(ScriptChange(project), "next", Token);
            Assert.Empty(project.Leftovers());
            return;
        }
        var found = Assert.Single(cases);
        Assert.Equal("crash", found.Description);
        Assert.Equal([Ai, Added, Old], found.Files.Select(f => f.Relative));
        Assert.All(found.Files, f => Assert.True(f.State is State.Before or State.After || f.State == State.Missing && f.HeldOriginal, $"{f.Relative} is {f.State}"));
        Assert.Equal(step == "cleanup", found.Committed);
        if (found.Committed)
        {
            Assert.Equal(after, project.Sources());
            Assert.Throws<InvalidOperationException>(() => publisher.Resolve(found.SaveId, SourceRecoveryAction.RollBack, Token));
        }
        else Assert.Throws<SourceRecoveryRequiredException>(() => publisher.Publish(ScriptChange(project), "blocked", Token));
        bool rollBack = action == SourceRecoveryAction.RollBack && !found.Committed;
        // Preserve a bounded diagnostic for intermittent native cleanup refusals. The
        // publisher intentionally catches those after source recovery has committed.
        List<string> cleanupFailures = [];
        int recoveryThread = Environment.CurrentManagedThreadId;
        void CleanupException(object? sender, System.Runtime.ExceptionServices.FirstChanceExceptionEventArgs args)
        {
            if (Environment.CurrentManagedThreadId == recoveryThread && cleanupFailures.Count < 8 &&
                args.Exception is IOException or UnauthorizedAccessException &&
                args.Exception.StackTrace?.Contains("SourcePublisher.TryDelete", StringComparison.Ordinal) == true)
                cleanupFailures.Add($"0x{args.Exception.HResult:X8}: {args.Exception.Message[..Math.Min(512, args.Exception.Message.Length)]}");
        }
        SourceRecoveryResult result;
        AppDomain.CurrentDomain.FirstChanceException += CleanupException;
        try { result = publisher.Resolve(found.SaveId, found.Committed ? SourceRecoveryAction.Complete : action, Token); }
        finally { AppDomain.CurrentDomain.FirstChanceException -= CleanupException; }
        Assert.True(result.Resolved); Assert.Empty(result.Conflicts);
        Assert.Equal(found.Files.Where(f => f.State != (rollBack ? State.Before : State.After)).Select(f => f.Relative), result.Changed);
        Assert.Equal(rollBack ? before : after, project.Sources());
        var leftovers = project.Leftovers();
        if (leftovers.Length != 0) Assert.Fail($"Recovery left {leftovers.Length} working entries: {string.Join(", ", leftovers.Take(8))}. Cleanup refusals: {string.Join("; ", cleanupFailures)}");
        Assert.Empty(publisher.FindInterrupted(Token));
        publisher.Publish(ScriptChange(project), "next", Token);
    }

    [Fact]
    public void AnIncompleteRollbackKeepsTheJournalUntilItIsRolledBack()
    {
        using Project project = new();
        var before = project.Sources();
        SourcePublisher publisher = new(project.Root)
        {
            Fault = (step, index) =>
            {
                if (step == "intent" && index == 2) throw new IOException("Injected failure.");
                if (step == "undo" && index == 0) throw new IOException("Injected undo failure.");
            },
        };
        var required = Assert.Throws<SourceRecoveryRequiredException>(() => publisher.Publish(ThreeFiles(project), "partial", Token));
        Assert.Equal([Ai], required.Files);
        Assert.Contains("Injected undo failure", required.Message);
        // The created file was taken back; the modified one still has the new content and its original is in the journal.
        var found = Assert.Single(publisher.FindInterrupted(Token));
        Assert.Equal(required.SaveId, found.SaveId);
        Assert.Equal([new(Ai, State.After, true), new(Added, State.Before, false), new(Old, State.Before, false)], found.Files);
        publisher.Fault = null;
        var result = publisher.Resolve(found.SaveId, SourceRecoveryAction.RollBack, Token);
        Assert.True(result.Resolved); Assert.Equal([Ai], result.Changed);
        Assert.Equal(before, project.Sources());
        Assert.Empty(project.Leftovers());
    }

    [Fact]
    public void RollBackLeavesAnExternalEditAndRestoresTheOtherFiles()
    {
        using Project project = new();
        var before = project.Sources(); byte[] original = project.Read(Ai);
        Assert.Throws<SourcePublisher.Crash>(() => Failing(project, "commit", -1, () => new SourcePublisher.Crash()).Publish(ThreeFiles(project), "crash", Token));
        File.WriteAllBytes(project.Full(Ai), Text("EXTERNAL\n"));
        SourcePublisher publisher = new(project.Root);
        var found = Assert.Single(publisher.FindInterrupted(Token));
        Assert.Equal([new(Ai, State.Other, true), new(Added, State.After, false), new(Old, State.After, true)], found.Files);
        // Completing would replace the external edit, so nothing is done.
        var crashed = project.Sources();
        var refused = publisher.Resolve(found.SaveId, SourceRecoveryAction.Complete, Token);
        Assert.False(refused.Resolved); Assert.Empty(refused.Changed); Assert.Equal([Ai], refused.Conflicts.Select(c => c.Relative));
        Assert.Equal(crashed, project.Sources());
        // Rolling back restores the rest and leaves the edited file alone.
        var result = publisher.Resolve(found.SaveId, SourceRecoveryAction.RollBack, Token);
        Assert.False(result.Resolved);
        Assert.Equal([Added, Old], result.Changed);
        Assert.Equal([Ai], result.Conflicts.Select(c => c.Relative));
        before[Ai] = "EXTERNAL\n";
        Assert.Equal(before, project.Sources());
        Assert.Throws<SourceRecoveryRequiredException>(() => publisher.Publish(ScriptChange(project), "blocked", Token));
        // Abandoning keeps the files and the original the journal still holds.
        Assert.True(publisher.Resolve(found.SaveId, SourceRecoveryAction.Abandon, Token).Resolved);
        Assert.Equal(before, project.Sources());
        Assert.Equal(original, File.ReadAllBytes(project.Full($"zstudio/recovery/abandoned/{found.SaveId}/held/0.bin")));
        Assert.Empty(project.Leftovers()); Assert.Empty(publisher.FindInterrupted(Token));
        publisher.Publish(ScriptChange(project), "next", Token);
    }

    [Fact]
    public void AFileCreatedWhileItsOriginalIsAsideRequiresRecovery()
    {
        using Project project = new();
        byte[] original = project.Read(Ai);
        SourcePublisher publisher = new(project.Root) { Fault = (step, index) => { if (step == "install" && index == 0) File.WriteAllBytes(project.Full(Ai), Text("FOREIGN\n")); } };
        var required = Assert.Throws<SourceRecoveryRequiredException>(() => publisher.Publish(ThreeFiles(project), "vacancy", Token));
        Assert.Equal([Ai], required.Files);
        Assert.Equal("FOREIGN\n", Encoding.ASCII.GetString(project.Read(Ai)));
        var found = Assert.Single(publisher.FindInterrupted(Token));
        Assert.Equal(new SourceRecoveryFile(Ai, State.Other, true), found.Files[0]);
        publisher.Fault = null;
        Assert.False(publisher.Resolve(found.SaveId, SourceRecoveryAction.RollBack, Token).Resolved);
        File.Delete(project.Full(Ai)); // The user removes the foreign file; the original can now go back.
        Assert.True(publisher.Resolve(found.SaveId, SourceRecoveryAction.RollBack, Token).Resolved);
        Assert.Equal(original, project.Read(Ai));
        Assert.Empty(project.Leftovers());
    }

    [Theory]
    [InlineData(0, SourceRecoveryAction.RollBack), InlineData(1, SourceRecoveryAction.RollBack), InlineData(2, SourceRecoveryAction.RollBack)]
    [InlineData(0, SourceRecoveryAction.Complete), InlineData(1, SourceRecoveryAction.Complete), InlineData(2, SourceRecoveryAction.Complete)]
    public void ARecoveryThatEndsPartWayCanBeResolvedAgain(int index, SourceRecoveryAction finish)
    {
        using Project project = new();
        var before = project.Sources(); var after = Saved(before);
        Assert.Throws<SourcePublisher.Crash>(() => Failing(project, "commit", -1, () => new SourcePublisher.Crash()).Publish(ThreeFiles(project), "crash", Token));
        string id = Assert.Single(new SourcePublisher(project.Root).FindInterrupted(Token)).SaveId;
        // The recovering processes end too: first part-way through a rollback, then part-way through completing.
        Assert.Throws<SourcePublisher.Crash>(() => Failing(project, "undo", index, () => new SourcePublisher.Crash()).Resolve(id, SourceRecoveryAction.RollBack, Token));
        Assert.Throws<SourcePublisher.Crash>(() => Failing(project, "complete", index, () => new SourcePublisher.Crash()).Resolve(id, SourceRecoveryAction.Complete, Token));
        var result = new SourcePublisher(project.Root).Resolve(id, finish, Token);
        Assert.True(result.Resolved, string.Join("; ", result.Conflicts));
        Assert.Equal(finish == SourceRecoveryAction.RollBack ? before : after, project.Sources());
        Assert.Empty(project.Leftovers());
    }

    [Fact]
    public void AbandoningAJournalWithoutOriginalsRemovesIt()
    {
        using Project project = new();
        var before = project.Sources();
        Assert.Throws<SourcePublisher.Crash>(() => Failing(project, "hold", 0, () => new SourcePublisher.Crash()).Publish(ThreeFiles(project), "early", Token));
        SourcePublisher publisher = new(project.Root);
        var found = Assert.Single(publisher.FindInterrupted(Token));
        Assert.All(found.Files, f => Assert.Equal(new SourceRecoveryFile(f.Relative, State.Before, false), f));
        Assert.True(publisher.Resolve(found.SaveId, SourceRecoveryAction.Abandon, Token).Resolved);
        Assert.Equal(before, project.Sources());
        Assert.Empty(project.Leftovers());
        Assert.False(Directory.Exists(project.Full("zstudio/recovery/abandoned")));
        Assert.Equal(found.SaveId, Assert.Throws<SourceRecoveryNotFoundException>(() => publisher.Resolve(found.SaveId, SourceRecoveryAction.RollBack, Token)).SaveId);
        Assert.Throws<ArgumentException>(() => publisher.Resolve("../outside", SourceRecoveryAction.RollBack, Token));
    }

    [Fact]
    public void ACommittedJournalIsReportedAndRetired()
    {
        using Project project = new();
        var after = Saved(project.Sources());
        Assert.Throws<SourcePublisher.Crash>(() => Failing(project, "cleanup", -1, () => new SourcePublisher.Crash()).Publish(ThreeFiles(project), "done", Token));
        SourcePublisher publisher = new(project.Root);
        var found = Assert.Single(publisher.FindInterrupted(Token));
        Assert.True(found.Committed);
        Assert.All(found.Files, f => Assert.Equal(State.After, f.State));
        Assert.Throws<InvalidOperationException>(() => publisher.Resolve(found.SaveId, SourceRecoveryAction.RollBack, Token));
        var result = publisher.Resolve(found.SaveId, SourceRecoveryAction.Complete, Token);
        Assert.True(result.Resolved); Assert.Empty(result.Changed);
        Assert.Equal(after, project.Sources());
        Assert.Empty(project.Leftovers());
    }

    [Fact]
    public void ATornLastEventIsIgnoredAndADamagedLogBlocksSaving()
    {
        using Project project = new();
        var before = project.Sources();
        Assert.Throws<SourcePublisher.Crash>(() => Failing(project, "install", 0, () => new SourcePublisher.Crash()).Publish(ThreeFiles(project), "torn", Token));
        SourcePublisher publisher = new(project.Root);
        string id = Assert.Single(publisher.FindInterrupted(Token)).SaveId, log = project.Full($"zstudio/recovery/{id}/events.log");
        string[] lines = File.ReadAllText(log).Split('\n', StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal(["prepared", "intent", "held"], lines.Select(l => l.Split('|')[1]));
        Assert.All(lines, line => Assert.Matches("^[0-9]+\\|[a-z-]+\\|-?[0-9]+\\|[0-9a-f]{16}$", line));
        string manifest = File.ReadAllText(project.Full($"zstudio/recovery/{id}/manifest.json"));
        Assert.Contains($"\"saveId\": \"{id}\"", manifest); Assert.Contains("\"sha256\":", manifest);

        // A commit record whose write did not finish, with or without its line end, does not count.
        File.AppendAllText(log, Record(4, "committed", -1)[..^5]);
        Assert.False(Assert.Single(publisher.FindInterrupted(Token)).Committed);
        File.WriteAllText(log, string.Join('\n', lines) + "\n" + Record(4, "committed", -1)[..^3] + "000\n");
        Assert.False(Assert.Single(publisher.FindInterrupted(Token)).Committed);
        // A damaged record followed by valid ones is not a torn write.
        File.WriteAllText(log, lines[0] + "\n" + lines[1][..^1] + "x\n" + lines[2] + "\n");
        Assert.Throws<SourceRecoveryRequiredException>(() => publisher.FindInterrupted(Token));
        Assert.Throws<SourceRecoveryRequiredException>(() => publisher.Publish(ScriptChange(project), "blocked", Token));
        // A complete commit record does count.
        File.WriteAllText(log, string.Join('\n', lines) + "\n" + Record(4, "committed", -1));
        Assert.True(Assert.Single(publisher.FindInterrupted(Token)).Committed);
        // Rolling back cuts the torn tail off before appending.
        File.WriteAllText(log, string.Join('\n', lines) + "\n" + Record(4, "committed", -1)[..^5]);
        Assert.True(publisher.Resolve(id, SourceRecoveryAction.RollBack, Token).Resolved);
        Assert.Equal(before, project.Sources());
        Assert.Empty(project.Leftovers());

        static string Record(int sequence, string step, int index)
        {
            string body = $"{sequence}|{step}|{index}";
            return $"{body}|{Convert.ToHexStringLower(SHA256.HashData(Encoding.ASCII.GetBytes(body)))[..16]}\n";
        }
    }

    [Fact]
    public void ASecondSaveFailsCleanlyWhileTheLockIsHeld()
    {
        using Project project = new();
        var before = project.Sources();
        Directory.CreateDirectory(project.Full("zstudio/recovery"));
        using (new FileStream(project.Full("zstudio/recovery/.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None))
        {
            var busy = Assert.Throws<IOException>(() => new SourcePublisher(project.Root).Publish(ThreeFiles(project), "busy", Token));
            Assert.Contains("Another save or recovery", busy.Message);
            Assert.Throws<IOException>(() => new SourcePublisher(project.Root).FindInterrupted(Token));
        }
        Assert.Equal(before, project.Sources());
        Assert.Empty(project.Leftovers());
        // A save in progress holds the lock against another publisher of the same project.
        IOException? second = null;
        SourcePublisher first = new(project.Root)
        {
            Fault = (step, index) =>
            {
                if (step == "intent" && index == 1) second = Assert.Throws<IOException>(() => new SourcePublisher(project.Root).Publish(ScriptChange(project), "second", Token));
            },
        };
        first.Publish(ThreeFiles(project), "first", Token);
        Assert.Contains("Another save or recovery", second!.Message);
        Assert.Equal(Saved(before), project.Sources());
    }

    [Fact]
    public void PathsOutsideTheSourceFoldersAreRefusedBeforeAnyChange()
    {
        using Project project = new();
        var before = project.Sources(); byte[] content = Text("X\n");
        string[] invalid =
        [
            "../outside.zrd", "data/../../outside.zrd", "data/./m1.zrd", "data//m1.zrd", "", " ", "/data/m1.zrd", "C:/data/m1.zrd", "data\\m1\\ai.zrd",
            "zstudio/recovery/x.bin", "zstudio/staging/0.tmp", "other/x.zrd", "data", "gamegen",
            "data/m1/ai.zrd:stream", "data/m1/CON.zrd", "data/m1/nul", "data/m1/ai.zrd.", "data/m1/ai.zrd ", "data/m1/*.zrd", "data/m1/ai.zrd/inside.zrd",
        ];
        foreach (string relative in invalid)
        {
            Assert.Throws<InvalidDataException>(() => new SourcePublisher(project.Root).Publish([new(relative, null, content)], "invalid", Token));
            Assert.False(Directory.Exists(project.Full("zstudio")), relative);
        }
        Assert.Throws<ArgumentException>(() => new SourcePublisher(project.Root).Publish([new(Ai, project.Read(Ai), content), new("DATA/M1/AI.ZRD", project.Read(Ai), content)], "twice", Token));
        Assert.Throws<ArgumentException>(() => new SourcePublisher(project.Root).Publish([new(Ai, project.Read(Ai), project.Read(Ai))], "nothing", Token));
        Assert.Throws<ArgumentException>(() => new SourcePublisher(project.Root).Publish([], "nothing", Token));
        Assert.Equal(before, project.Sources());
        Assert.False(Directory.Exists(project.Full("zstudio")));

        // A project inside a protected corpus folder is never written.
        string parent = Path.Combine(Path.GetTempPath(), "zstudio-publisher-protected-" + Guid.NewGuid().ToString("N"), "zbd_1999");
        try
        {
            using Project inside = new(parent);
            var protectedBefore = inside.Sources();
            Assert.Contains("protected", Assert.Throws<IOException>(() => new SourcePublisher(inside.Root).Publish(ThreeFiles(inside), "protected", Token)).Message);
            Assert.Equal(protectedBefore, inside.Sources());
            Assert.False(Directory.Exists(inside.Full("zstudio")));
        }
        finally { Directory.Delete(Path.GetDirectoryName(parent)!, true); }
    }

    [Fact]
    public void ACasedOrShortSpellingNeverNamesAFileTwice()
    {
        using Project project = new();
        string longName = "data/m1/averylongresourcename.zrd";
        File.WriteAllBytes(project.Full(longName), Text("LONG\n"));
        // A differently cased path writes the existing file under its own name.
        var result = new SourcePublisher(project.Root).Publish([new("DATA/M1/AVERYLONGRESOURCENAME.ZRD", Text("LONG\n"), Text("LONGER\n"))], "case", Token);
        Assert.Equal(["DATA/M1/AVERYLONGRESOURCENAME.ZRD"], result.Written);
        Assert.Equal("averylongresourcename.zrd", Path.GetFileName(Assert.Single(Directory.GetFiles(project.Full("data/m1"), "averylong*"))));
        Assert.Equal("LONGER\n", Encoding.ASCII.GetString(project.Read(longName)));
        char[] buffer = new char[32768];
        uint length = GetShortPathName(project.Full(longName), buffer, (uint)buffer.Length);
        if (length == 0) throw new Win32Exception(Marshal.GetLastPInvokeError());
        string alias = Path.GetFileName(new string(buffer, 0, (int)length));
        Assert.SkipWhen(alias.Equals("averylongresourcename.zrd", StringComparison.OrdinalIgnoreCase), "The temporary volume does not provide 8.3 names.");
        var before = project.Sources();
        Assert.Throws<InvalidDataException>(() => new SourcePublisher(project.Root).Publish([new("data/m1/" + alias, Text("LONGER\n"), Text("ALIAS\n"))], "alias", Token));
        Assert.Equal(before, project.Sources());
    }

    [Fact]
    public void CancellationStopsASaveOnlyBeforePublication()
    {
        using Project project = new();
        var before = project.Sources();
        using (CancellationTokenSource canceled = new())
        {
            canceled.Cancel();
            Assert.Throws<OperationCanceledException>(() => new SourcePublisher(project.Root).Publish(ThreeFiles(project), "canceled", canceled.Token));
            Assert.False(Directory.Exists(project.Full("zstudio")));
        }
        using (CancellationTokenSource staging = new())
        {
            SourcePublisher publisher = new(project.Root) { Fault = (step, index) => { if (step == "stage" && index == 0) staging.Cancel(); } };
            Assert.Throws<OperationCanceledException>(() => publisher.Publish(ThreeFiles(project), "canceled", staging.Token));
            Assert.Equal(before, project.Sources());
            Assert.Empty(project.Leftovers());
        }
        using (CancellationTokenSource publishing = new())
        {
            SourcePublisher publisher = new(project.Root) { Fault = (step, index) => { if (step == "intent" && index == 0) publishing.Cancel(); } };
            publisher.Publish(ThreeFiles(project), "finished", publishing.Token);
            Assert.Equal(Saved(before), project.Sources());
            Assert.Empty(project.Leftovers());
        }
    }

    [Fact]
    public void AReadOnlyTargetIsRefused()
    {
        using Project project = new();
        var before = project.Sources();
        File.SetAttributes(project.Full(Old), FileAttributes.ReadOnly);
        Assert.Throws<UnauthorizedAccessException>(() => new SourcePublisher(project.Root).Publish(ThreeFiles(project), "read-only", Token));
        File.SetAttributes(project.Full(Old), FileAttributes.Normal);
        Assert.Equal(before, project.Sources());
        Assert.Empty(project.Leftovers());
    }

    [LibraryImport("kernel32.dll", EntryPoint = "GetShortPathNameW", StringMarshalling = StringMarshalling.Utf16, SetLastError = true)]
    private static partial uint GetShortPathName(string path, [Out] char[] buffer, uint length);
}
