using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json.Nodes;
using Microsoft.Win32.SafeHandles;
using Recoil.Zbd.Core;
using Recoil.Zbd.Core.Sources;
using Xunit;
using State = Recoil.Zbd.Core.Sources.SourceRecoveryFileState;

namespace Recoil.Zbd.Tests;

/// <summary>
/// Saves and their recovery decide a file of another length than the journal's without reading it, and read one of the
/// journal's length in blocks that give way to cancellation; scans of a source project's folders count every file and
/// folder they visit, and stop when canceled.
/// </summary>
public sealed partial class RecoveryScanRound5Tests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;
    /// <summary>A terabyte, stored as a sparse file: nothing is written, but reading it whole would take many minutes.</summary>
    private const long Huge = 1L << 40;
    /// <summary>What the checks below take at most; without reading the huge files they finish in moments.</summary>
    private static readonly TimeSpan Limit = TimeSpan.FromSeconds(30);

    private static async Task<T> Promptly<T>(Func<T> work)
    {
        Task<T> task = Task.Run(work);
        await Task.WhenAny(task, Task.Delay(Limit, Token));
        Assert.True(task.IsCompleted, $"The work did not finish within {Limit.TotalSeconds:N0} s: it is reading a terabyte.");
        return await task;
    }

    /// <summary>Creates a sparse file of <paramref name="length"/> bytes, all zero, that takes no space on the disk.</summary>
    private static void Sparse(string path, long length)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        using FileStream stream = new(path, FileMode.Create, FileAccess.ReadWrite, FileShare.None);
        Assert.True(DeviceIoControl(stream.SafeFileHandle, SetSparse, 0, 0, 0, 0, out _, 0), "The temporary folder's file system cannot store sparse files.");
        stream.SetLength(length);
    }
    private const uint SetSparse = 0x000900C4;
    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool DeviceIoControl(SafeFileHandle device, uint code, nint input, uint inputSize, nint output, uint outputSize, out uint returned, nint overlapped);

    private const string Ai = "data/m1/ai.zrd", Added = "data/m1/new/added.zrd", Old = "data/m1/old.zrd";

    /// <summary>A source project whose save of three files (modify, create, delete) stopped at <paramref name="step"/>.</summary>
    private sealed class Interrupted : IDisposable
    {
        public Interrupted(string step, int index)
        {
            Directory.CreateDirectory(Full("data/m1")); Directory.CreateDirectory(Full("gamegen"));
            File.WriteAllText(Full(Ai), "GRAVITY ( -9.8 )\n"); File.WriteAllText(Full(Old), "OLD ( 1 )\n"); File.WriteAllText(Full("gamegen/m1.gs"), "Quit\n");
            SourceFileWrite[] writes = [new(Ai, Read(Ai), Encoding.ASCII.GetBytes("GRAVITY ( -4.9 )\n")), new(Added, null, Encoding.ASCII.GetBytes("ADDED ( 2 )\n")), new(Old, Read(Old), null)];
            SourcePublisher crashing = new(Root) { Fault = (s, i) => { if (s == step && i == index) throw new SourcePublisher.Crash(); } };
            Assert.Throws<SourcePublisher.Crash>(() => crashing.Publish(writes, "interrupted", Token));
            SaveId = Assert.Single(new SourcePublisher(Root).FindInterrupted(Token)).SaveId;
        }
        public string Root { get; } = Path.Combine(Path.GetTempPath(), "zstudio-recovery5-" + Guid.NewGuid().ToString("N"));
        public string SaveId { get; }
        public string Journal => Full($"zstudio/recovery/{SaveId}");
        public string Full(string relative) => Path.Combine(Root, relative.Replace('/', Path.DirectorySeparatorChar));
        public byte[] Read(string relative) => File.ReadAllBytes(Full(relative));
        public byte[] Events => File.ReadAllBytes(Path.Combine(Journal, "events.log"));
        /// <summary>Another program replaces a file of the save with a terabyte.</summary>
        public void Replace(string relative) { File.Delete(Full(relative)); Sparse(Full(relative), Huge); }
        /// <summary>The journal records a terabyte as the new content of the first file (as the huge file has that length, it must be read to compare it).</summary>
        public void JournalHugeContent() => Journaled(0, "content", Huge, new string('a', 64));
        /// <summary>Changes what the journal records as a file's <paramref name="field"/> (expected or content).</summary>
        public void Journaled(int index, string field, long length, string sha256)
        {
            string path = Path.Combine(Journal, "manifest.json");
            JsonObject manifest = JsonNode.Parse(File.ReadAllBytes(path))!.AsObject();
            manifest["files"]![index]![field] = new JsonObject { ["length"] = length, ["sha256"] = sha256 };
            File.WriteAllText(path, manifest.ToJsonString());
        }
        public void Dispose()
        {
            try { Directory.Delete(Root, true); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
    }

    // ------------------------------------------------------------ files of another length are not read

    [Fact]
    public async Task AFileReplacedWithATerabyteIsReportedWithoutBeingRead()
    {
        // Stopped before committing: the first file has its new content, its original is in the journal.
        using Interrupted project = new("commit", -1);
        project.Replace(Ai);
        SourcePublisher publisher = new(project.Root);

        var found = await Promptly(() => Assert.Single(publisher.FindInterrupted(Token)));
        Assert.Equal([new(Ai, State.Other, true), new(Added, State.After, false), new(Old, State.After, true)], found.Files);

        // Completing it refuses the replaced file before anything changes.
        var completed = await Promptly(() => publisher.Resolve(project.SaveId, SourceRecoveryAction.Complete, Token));
        Assert.False(completed.Resolved); Assert.Empty(completed.Changed);
        Assert.Equal(Ai, Assert.Single(completed.Conflicts).Relative);
        Assert.Contains("was changed by another program", completed.Conflicts[0].Reason);

        // Rolling back leaves it, and its original in the journal, and undoes the others.
        var rolledBack = await Promptly(() => publisher.Resolve(project.SaveId, SourceRecoveryAction.RollBack, Token));
        Assert.False(rolledBack.Resolved); Assert.Equal([Added, Old], rolledBack.Changed);
        Assert.Equal(Ai, Assert.Single(rolledBack.Conflicts).Relative);
        Assert.Contains("holds content from another program", rolledBack.Conflicts[0].Reason);
        Assert.Equal(Huge, new FileInfo(project.Full(Ai)).Length);
        Assert.Equal("GRAVITY ( -9.8 )\n", File.ReadAllText(Path.Combine(project.Journal, "held", "0.bin")));
    }

    [Fact]
    public async Task ASaveAndItsMovesDoNotReadAFileOfAnotherLength()
    {
        string root = Path.Combine(Path.GetTempPath(), "zstudio-recovery5-" + Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(Path.Combine(root, "gamegen"));
            string replaced = Path.Combine(root, "data", "m1", "ai.zrd"), created = Path.Combine(root, "data", "m1", "new.zrd");
            Sparse(replaced, Huge); Sparse(created, Huge);
            byte[] original = Encoding.ASCII.GetBytes("GRAVITY ( -9.8 )\n");
            // A file read as the original, and a new file's name, that another program filled since.
            var conflict = await Promptly(() => Assert.Throws<SourceConflictException>(() => new SourcePublisher(root).Publish(
                [new(Ai, original, Encoding.ASCII.GetBytes("GRAVITY ( -4.9 )\n")), new("data/m1/new.zrd", null, original)], "conflict", Token)));
            Assert.Equal([Ai, "data/m1/new.zrd"], conflict.Files);
            // Moving a file only while it has some content, and holding a staged copy while it has it, neither reads it.
            var digest = JournalDigest.Of(original)!;
            Assert.Equal(SourcePublisher.Moved.Unchanged, await Promptly(() => SourcePublisher.MoveIfContent(replaced, Path.Combine(root, "moved.bin"), digest)));
            Assert.True(File.Exists(replaced));
            await Promptly(() => Assert.Throws<IOException>(() => SealedFile.Open(replaced, digest).Dispose()));
        }
        finally
        {
            try { Directory.Delete(root, true); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
    }

    [Fact]
    public void AJournalCopyReplacedAfterItWasCheckedIsNotReadWhole()
    {
        // Stopped before the first file was touched; the journal's copy of its new content is replaced with 64 MiB after the
        // completion checked it, as it reaches the file.
        using Interrupted project = new("intent", 0);
        string copy = Path.Combine(project.Journal, "after", "0.bin");
        SourcePublisher publisher = new(project.Root) { Fault = (step, index) => { if (step == "complete" && index == 0) { File.Delete(copy); Sparse(copy, 64L << 20); } } };
        long before = GC.GetAllocatedBytesForCurrentThread();
        var result = publisher.Resolve(project.SaveId, SourceRecoveryAction.Complete, Token);
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        Assert.InRange(allocated, 0, 16L << 20);
        Assert.False(result.Resolved);
        Assert.Contains(result.Conflicts, c => c.Relative == Ai && c.Reason.Contains("new content is damaged", StringComparison.Ordinal));
    }

    // ------------------------------------------------------------ files of the journal's length are read in blocks

    /// <summary>A stream that counts its reads and runs an action at the first.</summary>
    private sealed class Watched(byte[] bytes, Action? first = null) : MemoryStream(bytes)
    {
        public int Reads { get; private set; }
        // A derived MemoryStream reads spans through this overload.
        public override int Read(byte[] buffer, int offset, int count) { if (Reads++ == 0) first?.Invoke(); return base.Read(buffer, offset, count); }
    }

    [Fact]
    public void ADigestIsReadInBlocksThatGiveWayToCancellation()
    {
        // The digest of a stream is that of its bytes, across block boundaries.
        foreach (int length in new[] { 0, 1, 1 << 20, (1 << 20) + 1, (3 << 20) + 5 })
        {
            byte[] bytes = new byte[length]; new Random(length).NextBytes(bytes);
            using Watched stream = new(bytes);
            Assert.Equal(JournalDigest.Of(bytes), JournalDigest.Of(stream, Token));
            stream.Position = 0; Assert.True(JournalDigest.Of(bytes)!.Matches(stream, Token));
        }
        // A stream of another length differs without a read.
        using (Watched other = new(new byte[5 << 20]))
        {
            Assert.False(JournalDigest.Of(new byte[(5 << 20) - 1])!.Matches(other, Token));
            Assert.Equal(0, other.Reads);
        }
        // Canceled during the first block, the next is not read.
        using CancellationTokenSource cancellation = new();
        using Watched large = new(new byte[16 << 20], cancellation.Cancel);
        Assert.ThrowsAny<OperationCanceledException>(() => JournalDigest.Of(large, cancellation.Token));
        Assert.Equal(1, large.Reads);
    }

    [Fact]
    public async Task RecoveryIsCanceledWhileItReadsAFileOfTheJournaledLength()
    {
        using Interrupted project = new("commit", -1);
        project.Replace(Ai); project.JournalHugeContent();
        byte[] events = project.Events;
        SourcePublisher publisher = new(project.Root);
        // Listing the save must read the file to compare it: the cancellation stops it.
        using (CancellationTokenSource listing = new(TimeSpan.FromMilliseconds(250)))
            await Promptly(() => Assert.ThrowsAny<OperationCanceledException>(() => publisher.FindInterrupted(listing.Token)));
        // So does completing it, which checks every file before it changes any (a file whose turn has come is completed, as
        // ScriptRecoveryRound4Tests shows).
        using (CancellationTokenSource completing = new(TimeSpan.FromMilliseconds(250)))
        {
            var stopped = await Promptly(() => Assert.ThrowsAny<OperationCanceledException>(() => publisher.Resolve(project.SaveId, SourceRecoveryAction.Complete, completing.Token)));
            Assert.Null(stopped.InnerException);
        }
        Assert.Equal(events, project.Events);
        Assert.Equal(Huge, new FileInfo(project.Full(Ai)).Length);
        // Rolling back reaches the file last, after it undid the others (the cancellation comes once it undid the second),
        // and says so.
        using (CancellationTokenSource rollingBack = new())
        {
            SourcePublisher undoing = new(project.Root) { Fault = (step, index) => { if (step == "undo" && index == 1) rollingBack.CancelAfter(250); } };
            var stopped = await Promptly(() => Assert.ThrowsAny<OperationCanceledException>(() => undoing.Resolve(project.SaveId, SourceRecoveryAction.RollBack, rollingBack.Token)));
            Assert.IsAssignableFrom<OperationCanceledException>(stopped.InnerException);
            Assert.Contains($"changed 2 files ({Old}, {Added})", stopped.Message);
            Assert.Contains("still needs a decision", stopped.Message);
        }
        Assert.Equal("OLD ( 1 )\n", File.ReadAllText(project.Full(Old)));
        Assert.False(File.Exists(project.Full(Added)));
        Assert.Equal(Huge, new FileInfo(project.Full(Ai)).Length);
    }

    // ------------------------------------------------------------ scans of source folders

    private sealed class Project : IDisposable
    {
        public string Root { get; } = Path.Combine(Path.GetTempPath(), "zstudio-scan5-" + Guid.NewGuid().ToString("N"));
        public Project(bool mission = true)
        {
            Write("gamegen/m1.gs", "Quit\n");
            if (mission) Write("data/m1/models/tank.gltf", "{}");
            else Directory.CreateDirectory(Full("data"));
        }
        public string Full(string relative) => Path.Combine(Root, relative.Replace('/', Path.DirectorySeparatorChar));
        public void Write(string relative, string text = "")
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Full(relative))!);
            File.WriteAllText(Full(relative), text);
        }
        public void Dispose()
        {
            try { Directory.Delete(Root, true); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
    }
    private static bool Gltf(string name) => name.EndsWith(".gltf", StringComparison.OrdinalIgnoreCase);

    [Fact]
    public void AScanCountsEveryFileAndFolderItVisits()
    {
        using Project project = new();
        // One model among the files an editor leaves beside it, and folders of its cache.
        for (int i = 0; i < 150; i++) project.Write($"data/m1/models/backup/tank{i}.blend1");
        for (int i = 0; i < 40; i++) Directory.CreateDirectory(project.Full($"data/m1/cache/{i}"));
        // m1, models, tank.gltf, backup, 150 backups, cache and its 40 folders.
        const int entries = 195;
        Assert.Equal(["data/m1/models/tank.gltf"], SourceProject.Files(project.Root, "data", Gltf, null, entries, Token));
        var refused = Assert.Throws<IOException>(() => SourceProject.Files(project.Root, "data", Gltf, null, entries - 1, Token));
        Assert.Contains($"data holds more than {entries - 1} files and folders", refused.Message);
        Assert.Contains("Move files the build does not use (editor caches, backups, design files) out of the project's data and gamegen folders", refused.Message);
        // Folders that hold nothing count too, here and in the listing of a game folder to reconstruct.
        Assert.Throws<IOException>(() => SourceProject.Files(project.Root, "data/m1/cache", Gltf, null, 39, Token));
        Assert.Empty(SourceProject.Files(project.Root, "data/m1/cache", Gltf, null, 40, Token));
        var corpus = Assert.Throws<IOException>(() => SourceExtractor.Corpus(project.Full("data/m1/cache"), Token, 39));
        Assert.Contains("more than 39 files and folders", corpus.Message);
        Assert.Empty(SourceExtractor.Corpus(project.Full("data/m1/cache"), Token, 40));
        // The bound leaves the releases' 5,750 files and folders far behind.
        Assert.True(SourceProject.MaximumScannedEntries >= 40 * 5_750);
    }

    [Fact]
    public void ScansStopWhenCanceled()
    {
        using Project project = new();
        for (int i = 0; i < 20; i++) project.Write($"data/m1/models/backup/tank{i}.blend1");
        // Canceled while it scans: no further entry is looked at.
        using (CancellationTokenSource cancellation = new())
        {
            int seen = 0;
            Assert.ThrowsAny<OperationCanceledException>(() => SourceProject.Files(project.Root, "data", n => { seen++; cancellation.Cancel(); return false; }, null, cancellation.Token));
            Assert.Equal(1, seen);
        }
        // Planning the build (source_status, exports and checks), listing models and missions, and listing a game folder.
        using CancellationTokenSource canceled = new();
        canceled.Cancel();
        Assert.ThrowsAny<OperationCanceledException>(() => SourceBuilder.Plan(project.Root, token: canceled.Token));
        // Also where only its scans can stop it: a project without mission folders.
        using (Project scripts = new(mission: false))
        {
            Assert.ThrowsAny<OperationCanceledException>(() => SourceBuilder.Plan(scripts.Root, token: canceled.Token));
            Assert.Equal(["interp.zbd"], SourceBuilder.Plan(scripts.Root, token: Token).Select(p => p.Path));
        }
        Assert.ThrowsAny<OperationCanceledException>(() => SourceWorlds.Models(project.Root, canceled.Token));
        Assert.ThrowsAny<OperationCanceledException>(() => SourceWorlds.Missions(project.Root, canceled.Token));
        Assert.ThrowsAny<OperationCanceledException>(() => SourceExtractor.Corpus(project.Full("data"), canceled.Token));
        // Without a cancellation they list the project as before.
        Assert.Equal(["data/m1/models/tank.gltf"], SourceWorlds.Models(project.Root, Token).Select(m => m.Path));
        Assert.Contains(SourceBuilder.Plan(project.Root, token: Token), p => p.Path == "interp.zbd");
    }
}
