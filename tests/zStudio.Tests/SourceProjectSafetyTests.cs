using System.Buffers.Binary;
using System.Diagnostics;
using System.Text;
using Recoil.Zbd.Core;
using Recoil.Zbd.Core.Formats;
using Recoil.Zbd.Core.Sources;
using Xunit;

namespace Recoil.Zbd.Tests;

/// <summary>Publication, snapshot, link and input-validation guarantees of source projects.</summary>
public sealed class SourceProjectSafetyTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;
    private sealed class OnReport(Action<SourceProgress> action) : IProgress<SourceProgress> { public void Report(SourceProgress value) => action(value); }
    private static Dictionary<string, byte[]> Files(string root) => Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories)
        .ToDictionary(f => Path.GetRelativePath(root, f), File.ReadAllBytes, StringComparer.OrdinalIgnoreCase);

    [Fact]
    public async Task ReconstructionRefusesAParentReplacedByALinkAfterTheInitialCheck()
    {
        using var fixture = new SourceFixture();
        string outside = Path.Combine(fixture.Root, "outside"); Directory.CreateDirectory(outside);
        string link = Path.Combine(fixture.Project, "data"); bool replaced = false;
        var progress = new OnReport(_ =>
        {
            if (replaced) return;
            replaced = true;
            Directory.Delete(link);
            Directory.CreateSymbolicLink(link, outside);
        });
        try
        {
            await Assert.ThrowsAsync<IOException>(() => SourceExtractor.ExtractAsync(fixture.Corpus, fixture.Project, progress, Token));
            Assert.Empty(Directory.EnumerateFileSystemEntries(outside));
        }
        finally { if (Directory.Exists(link) && File.GetAttributes(link).HasFlag(FileAttributes.ReparsePoint)) Directory.Delete(link); }
    }

    [Fact]
    public async Task AFailedPublicationRestoresThePreviousFilesExactly()
    {
        using var fixture = new SourceFixture();
        await SourceExtractor.ExtractAsync(fixture.Corpus, fixture.Project, token: Token);
        string exported = Path.Combine(fixture.Root, "zbd");
        await SourceBuilder.ExportAsync(fixture.Project, exported, token: Token);
        var before = Files(exported);
        File.WriteAllText(Path.Combine(fixture.Project, "data", "common", "zrdr", "extra.zrd"), "VALUE ( 1 )");
        // The last output cannot be replaced: earlier replacements (including the changed archive) must be undone.
        using (new FileStream(Path.Combine(exported, "m1", "zrdr.zbd"), FileMode.Open, FileAccess.Read, FileShare.None))
            await Assert.ThrowsAnyAsync<IOException>(() => SourceBuilder.ExportAsync(fixture.Project, exported, overwrite: true, token: Token));
        var after = Files(exported);
        Assert.Equal(before.Keys.Order(), after.Keys.Order());
        Assert.All(before, file => Assert.Equal(file.Value, after[file.Key]));
        Assert.Empty(Directory.GetDirectories(exported, ".zstudio-*"));
    }

    [Fact]
    public async Task NestedLinksInAnExportFolderAreRefusedBeforeAnythingMoves()
    {
        using var fixture = new SourceFixture();
        await SourceExtractor.ExtractAsync(fixture.Corpus, fixture.Project, token: Token);
        string exported = Path.Combine(fixture.Root, "zbd"), outside = Path.Combine(fixture.Root, "outside");
        await SourceBuilder.ExportAsync(fixture.Project, exported, token: Token);
        var before = Files(exported);
        Directory.CreateDirectory(outside); File.WriteAllBytes(Path.Combine(outside, "zrdr.zbd"), [42]);
        Directory.Delete(Path.Combine(exported, "m1"), true);
        if (!Junction(Path.Combine(exported, "m1"), outside)) return; // Junctions unavailable on this file system.
        await Assert.ThrowsAsync<IOException>(() => SourceBuilder.ExportAsync(fixture.Project, exported, overwrite: true, token: Token));
        Assert.Equal([42], File.ReadAllBytes(Path.Combine(outside, "zrdr.zbd")));
        Assert.Equal(before["interp.zbd"], File.ReadAllBytes(Path.Combine(exported, "interp.zbd")));
        Directory.Delete(Path.Combine(exported, "m1"));
    }

    [Fact]
    public async Task LinksInsideAProjectAreRefused()
    {
        using var fixture = new SourceFixture();
        await SourceExtractor.ExtractAsync(fixture.Corpus, fixture.Project, token: Token);
        string outside = Path.Combine(fixture.Root, "outside");
        Directory.CreateDirectory(outside); File.WriteAllText(Path.Combine(outside, "secret.zrd"), "VALUE ( 1 )");
        if (!Junction(Path.Combine(fixture.Project, "data", "m1", "zrdr", "linked"), outside)) return;
        Assert.Throws<IOException>(() => SourceBuilder.Plan(fixture.Project, token: TestContext.Current.CancellationToken));
        Directory.Delete(Path.Combine(fixture.Project, "data", "m1", "zrdr", "linked"));
        // A linked mission folder is refused too, although enumeration starts inside it.
        Directory.CreateDirectory(Path.Combine(outside, "zrdr"));
        Assert.True(Junction(Path.Combine(fixture.Project, "data", "m2"), outside));
        Assert.Throws<IOException>(() => SourceBuilder.Plan(fixture.Project, token: TestContext.Current.CancellationToken));
        Directory.Delete(Path.Combine(fixture.Project, "data", "m2"));
        Assert.Equal(6, SourceBuilder.Plan(fixture.Project, token: TestContext.Current.CancellationToken).Count);
    }

    private static bool Junction(string link, string target)
    {
        using var process = Process.Start(new ProcessStartInfo("cmd.exe", $"/c mklink /J \"{link}\" \"{target}\"") { CreateNoWindow = true, UseShellExecute = false, RedirectStandardOutput = true });
        process!.WaitForExit(); return Directory.Exists(link) && new DirectoryInfo(link).Attributes.HasFlag(FileAttributes.ReparsePoint);
    }

    [Fact]
    public async Task SourcesThatChangeDuringAnExportAreNeverPublished()
    {
        using var fixture = new SourceFixture();
        await SourceExtractor.ExtractAsync(fixture.Corpus, fixture.Project, token: Token);
        string wave = Path.Combine(fixture.Project, "data", "common", "sounds", "b.wav"), exported = Path.Combine(fixture.Root, "zbd");
        // The high-quality bank reads b.wav first; touching it before the medium bank is built makes the export incoherent.
        var progress = new OnReport(p => { if (p.Item == "soundsm.zbd") File.SetLastWriteTimeUtc(wave, File.GetLastWriteTimeUtc(wave).AddMinutes(1)); });
        var error = await Assert.ThrowsAsync<InvalidDataException>(() => SourceBuilder.ExportAsync(fixture.Project, exported, progress: progress, token: Token));
        Assert.Contains("changed while exporting", error.Message);
        Assert.Empty(Directory.EnumerateFileSystemEntries(exported));
        // Direct snapshot checks: a second read or the final check detects the change.
        SourceBuilder.Snapshot snapshot = new(fixture.Project);
        snapshot.Read("data/common/sounds/b.wav", Token);
        File.SetLastWriteTimeUtc(wave, File.GetLastWriteTimeUtc(wave).AddMinutes(1));
        Assert.Throws<InvalidDataException>(() => snapshot.CheckUnchanged(Token));
        Assert.Throws<InvalidDataException>(() => snapshot.Read("data/common/sounds/b.wav", Token));
    }

    [Fact]
    public async Task CanceledExportsWriteNothing()
    {
        using var fixture = new SourceFixture();
        await SourceExtractor.ExtractAsync(fixture.Corpus, fixture.Project, token: Token);
        string exported = Path.Combine(fixture.Root, "zbd");
        using var cancel = CancellationTokenSource.CreateLinkedTokenSource(Token);
        var progress = new OnReport(p => { if (p.Completed == 2) cancel.Cancel(); });
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => SourceBuilder.ExportAsync(fixture.Project, exported, progress: progress, token: cancel.Token));
        Assert.Empty(Directory.EnumerateFileSystemEntries(exported));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task CanceledReconstructionLeavesARetryableFolder(bool existing)
    {
        using var fixture = new SourceFixture();
        if (existing) Directory.CreateDirectory(fixture.Project);
        using var cancel = CancellationTokenSource.CreateLinkedTokenSource(Token);
        var progress = new OnReport(p => { if (p.Completed == 3) cancel.Cancel(); });
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => SourceExtractor.ExtractAsync(fixture.Corpus, fixture.Project, progress, cancel.Token));
        Assert.Equal(existing, Directory.Exists(fixture.Project));
        if (existing) Assert.Empty(Directory.EnumerateFileSystemEntries(fixture.Project));
        Assert.Equal(6, (await SourceExtractor.ExtractAsync(fixture.Corpus, fixture.Project, token: Token)).Families.Values.Sum());
    }

    [Fact]
    public async Task DestinationsStaySeparateFromTheirInputs()
    {
        using var fixture = new SourceFixture();
        Directory.CreateDirectory(fixture.Project); File.WriteAllText(Path.Combine(fixture.Project, "note.txt"), "mine");
        await Assert.ThrowsAsync<IOException>(() => SourceExtractor.ExtractAsync(fixture.Corpus, fixture.Project, token: Token));
        Assert.Equal(["note.txt"], Directory.GetFileSystemEntries(fixture.Project).Select(Path.GetFileName));
        await Assert.ThrowsAsync<InvalidDataException>(() => SourceExtractor.ExtractAsync(fixture.Corpus, Path.Combine(fixture.Corpus, "project"), token: Token));
        File.Delete(Path.Combine(fixture.Project, "note.txt"));
        await SourceExtractor.ExtractAsync(fixture.Corpus, fixture.Project, token: Token);
        foreach (string destination in new[] { fixture.Project, Path.Combine(fixture.Project, "zbd"), fixture.Root, Path.Combine(fixture.Root, "zbd_1999", "export") })
            await Assert.ThrowsAsync<InvalidDataException>(() => SourceBuilder.ExportAsync(fixture.Project, destination, token: Token));
        Assert.False(Directory.Exists(Path.Combine(fixture.Project, "zbd")));
        Assert.False(Directory.Exists(Path.Combine(fixture.Root, "zbd_1999")));
    }

    [Fact]
    public async Task OverlapIsDetectedThroughDriveRootsAndAliases()
    {
        // A drive root contains every folder on it.
        string drive = Path.GetPathRoot(Path.GetTempPath())!;
        Assert.Throws<InvalidDataException>(() => SourceProject.ValidateSeparate(Path.Combine(drive, "zstudio-overlap-" + Guid.NewGuid().ToString("N")), drive, "project folder"));
        // A short (8.3) spelling of the project is the project.
        using var fixture = new SourceFixture();
        await SourceExtractor.ExtractAsync(fixture.Corpus, fixture.Project, token: Token);
        string shortRoot = ShortPath(fixture.Root);
        if (shortRoot.Equals(fixture.Root, StringComparison.OrdinalIgnoreCase)) return; // 8.3 names are not generated on this volume.
        string aliased = Path.Combine(shortRoot, "project", "exported");
        await Assert.ThrowsAsync<InvalidDataException>(() => SourceBuilder.ExportAsync(fixture.Project, aliased, token: Token));
        Assert.False(Directory.Exists(Path.Combine(fixture.Project, "exported")));
        await Assert.ThrowsAsync<InvalidDataException>(() => SourceExtractor.ExtractAsync(fixture.Corpus, Path.Combine(shortRoot, "game", "project"), token: Token));
        Assert.False(Directory.Exists(Path.Combine(fixture.Corpus, "project")));
    }
    private static string ShortPath(string path)
    {
        char[] buffer = new char[1024];
        uint length = GetShortPathName(path, buffer, (uint)buffer.Length);
        return length is > 0 and < 1024 ? new string(buffer, 0, (int)length) : path;
    }
    [System.Runtime.InteropServices.DllImport("kernel32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode, SetLastError = true)]
    private static extern uint GetShortPathName(string longPath, char[] shortPath, uint length);

    [Fact]
    public async Task ExportsNeverReplaceFilesThatAppearWithoutConsent()
    {
        using var fixture = new SourceFixture();
        await SourceExtractor.ExtractAsync(fixture.Corpus, fixture.Project, token: Token);
        string exported = Path.Combine(fixture.Root, "zbd"), late = Path.Combine(exported, "m1", "zrdr.zbd");
        // Another program writes one of the game files after the existence check, while the outputs build.
        var progress = new OnReport(p => { if (p.Item == "Publishing") { Directory.CreateDirectory(Path.GetDirectoryName(late)!); File.WriteAllBytes(late, [7]); } });
        await Assert.ThrowsAnyAsync<IOException>(() => SourceBuilder.ExportAsync(fixture.Project, exported, progress: progress, token: Token));
        Assert.Equal([7], File.ReadAllBytes(late));
        Assert.Equal([late], Directory.GetFiles(exported, "*", SearchOption.AllDirectories));
    }

    [Fact]
    public async Task FoldersWithoutRecoilDataAreRefused()
    {
        using var fixture = new SourceFixture();
        string empty = Path.Combine(fixture.Root, "empty"), unrelated = Path.Combine(fixture.Root, "unrelated");
        Directory.CreateDirectory(empty); Directory.CreateDirectory(unrelated); File.WriteAllText(Path.Combine(unrelated, "readme.txt"), "hello");
        foreach (string input in new[] { empty, unrelated })
        {
            var error = await Assert.ThrowsAsync<InvalidDataException>(() => SourceExtractor.ExtractAsync(input, Path.Combine(fixture.Root, "project-" + Path.GetFileName(input)), token: Token));
            Assert.Contains("No RECOIL game data", error.Message);
            Assert.False(Directory.Exists(Path.Combine(fixture.Root, "project-" + Path.GetFileName(input))));
        }
    }

    [Fact]
    public async Task DemoFoldersAreRefused()
    {
        // A RECOIL folder whose world is a 1998 demo's (version 13): its scripts are RECOIL evidence, but the demos open read-only.
        using var fixture = new SourceFixture();
        string demo = Path.Combine(fixture.Root, "demo"), project = Path.Combine(fixture.Root, "project-demo");
        foreach (var file in Directory.GetFiles(fixture.Corpus, "*", SearchOption.AllDirectories))
        {
            string target = Path.Combine(demo, Path.GetRelativePath(fixture.Corpus, file)); Directory.CreateDirectory(Path.GetDirectoryName(target)!); File.Copy(file, target);
        }
        Directory.CreateDirectory(Path.Combine(demo, "m1"));
        File.WriteAllBytes(Path.Combine(demo, "m1", "gamez.zbd"), DemoWorldFixture.FromVersion15(Recoil.Zbd.Core.Worlds.GameZWriter.Write(GameZVersion13Tests.SampleWorld(), Token)));
        var error = await Assert.ThrowsAsync<InvalidDataException>(() => SourceExtractor.ExtractAsync(demo, project, token: Token));
        Assert.Contains("1998 demo world (GameZ version 13)", error.Message);
        Assert.False(Directory.Exists(project));
    }

    [Fact]
    public void OversizedTextAndMisnamedArchivesStayBounded()
    {
        byte[] huge = new byte[SourceProject.MaximumSourceTextBytes + 1]; huge.AsSpan().Fill((byte)'a');
        foreach (string name in new[] { "big.zrd", "big.gw" })
        {
            long before = GC.GetAllocatedBytesForCurrentThread();
            var doc = FormatRegistry.Default.OpenBytes(name, huge, token: Token);
            Assert.Contains(doc.Diagnostics, d => d.Severity == "Error" && d.Message.Contains("16 MiB", StringComparison.Ordinal));
            Assert.True(GC.GetAllocatedBytesForCurrentThread() - before < 4_000_000, "The oversized text was decoded.");
        }
        // A real archive keeps its structural identity whatever its name.
        byte[] archive = new byte[16 + 148 + 8]; BinaryPrimitives.WriteInt32LittleEndian(archive, 0x7FFFFFFF);
        BinaryPrimitives.WriteInt32LittleEndian(archive.AsSpan(16 + 4), 16); Encoding.Latin1.GetBytes("a.bin").CopyTo(archive, 16 + 8);
        BinaryPrimitives.WriteInt32LittleEndian(archive.AsSpan(archive.Length - 8), 1); BinaryPrimitives.WriteInt32LittleEndian(archive.AsSpan(archive.Length - 4), 1);
        foreach (string extension in new[] { ".gw", ".gs", ".zrd" })
            Assert.Equal(FormatFamily.Archive, FormatRegistry.Probe(archive.AsSpan(0, 36), archive.AsSpan(archive.Length - 8), archive.Length, extension).Family);
    }

    [Fact]
    public void TextFormsAreBoundedBeforeTheyAreBuilt()
    {
        static ZrdNode A(params ZrdNode[] c) => new(Guid.NewGuid(), ZrdKind.Array, 0, "", c);
        var huge = A(new ZrdNode(Guid.NewGuid(), ZrdKind.String, 0, new string('\x01', 4_000_000), []));
        long before = GC.GetAllocatedBytesForCurrentThread();
        Assert.Throws<InvalidDataException>(() => ZrdText.Write(huge, Token, 1_000_000));
        Assert.True(GC.GetAllocatedBytesForCurrentThread() - before < 4_000_000, "The oversized text was built.");
        var many = A([.. Enumerable.Range(0, 400_000).Select(i => new ZrdNode(Guid.NewGuid(), ZrdKind.Int, (uint)i, "", []))]);
        Assert.Throws<InvalidDataException>(() => ZrdText.Write(many, Token, 1_000_000));
        Assert.Equal(ZrdText.Write(A(A()), Token), ZrdText.Write(A(A()), Token, 16));
    }

    [Fact]
    public void ResolvedPathsNeverLeaveTheirRoot()
    {
        string root = Path.Combine(Path.GetTempPath(), "zstudio-root");
        foreach (string bad in new[] { "", "../x", "a/../../x", "a\\b", "/abs", "C:/abs", "a//b", "./a" })
            Assert.Throws<InvalidDataException>(() => SourceProject.Resolve(root, bad));
        Assert.Equal(Path.Combine(root, "data", "m1", "zrdr.zbd"), SourceProject.Resolve(root, "data/m1/zrdr.zbd"));
        Assert.Null(SourceExtractor.SourceDirectory("D:\\data\\..\\evil.TMP"));
        Assert.Null(SourceExtractor.SourceDirectory("somewhere\\else.TMP"));
        Assert.Equal("data/m1/zrdr/envmodels", SourceExtractor.SourceDirectory("D:\\battlesportdev\\data\\M1\\zrdr\\envmodels\\frcE3B0.TMP"));
        Assert.Equal("data/m1/zrdr/envmodels", SourceExtractor.SourceDirectory("data\\m1\\zrdr\\envmodels\\frcgate.zrd"));
    }
}
