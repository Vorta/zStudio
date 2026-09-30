using System.Buffers.Binary;
using System.Diagnostics;
using System.Text;
using Recoil.Zbd.Core;
using Recoil.Zbd.Core.Formats;
using Recoil.Zbd.Core.Sources;
using Xunit;

namespace Recoil.Zbd.Tests;

/// <summary>Publication, ownership, snapshot and input-validation guarantees of source projects.</summary>
public sealed class SourceProjectSafetyTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;
    private sealed class OnReport(Action<SourceProgress> action) : IProgress<SourceProgress> { public void Report(SourceProgress value) => action(value); }
    private static Dictionary<string, byte[]> Files(string root) => Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories)
        .ToDictionary(f => Path.GetRelativePath(root, f), File.ReadAllBytes, StringComparer.OrdinalIgnoreCase);

    [Fact]
    public async Task AFailedPublicationRestoresThePreviousPackExactly()
    {
        using var fixture = new SourceFixture();
        await SourceExtractor.ExtractAsync(fixture.Corpus, fixture.Project, token: Token);
        string packed = Path.Combine(fixture.Root, "packed");
        await SourcePacker.PackAsync(fixture.Project, packed, token: Token);
        var before = Files(packed);
        File.WriteAllText(Path.Combine(fixture.Project, "data", "m1", "zrdr", "ai.zrd"), "( GRAVITY ( -1.0 ) )");
        // The last output cannot be replaced: earlier replacements (including the changed archive) must be undone.
        using (new FileStream(Path.Combine(packed, "other.bin"), FileMode.Open, FileAccess.Read, FileShare.None))
            await Assert.ThrowsAnyAsync<IOException>(() => SourcePacker.PackAsync(fixture.Project, packed, token: Token));
        var after = Files(packed);
        Assert.Equal(before.Keys.Order(), after.Keys.Order());
        Assert.All(before, file => Assert.Equal(file.Value, after[file.Key]));
        Assert.Empty(Directory.GetDirectories(packed, ".zstudio-*"));
    }

    [Fact]
    public async Task PackFoldersBelongToOneProject()
    {
        using var fixture = new SourceFixture();
        await SourceExtractor.ExtractAsync(fixture.Corpus, fixture.Project, token: Token);
        string other = Path.Combine(fixture.Root, "other-project"), packed = Path.Combine(fixture.Root, "packed");
        await SourceExtractor.ExtractAsync(fixture.Corpus, other, token: Token);
        await SourcePacker.PackAsync(fixture.Project, packed, token: Token);
        var before = Files(packed);
        var error = await Assert.ThrowsAsync<IOException>(() => SourcePacker.PackAsync(other, packed, token: Token));
        Assert.Contains("different source project", error.Message);
        var after = Files(packed); Assert.All(before, file => Assert.Equal(file.Value, after[file.Key]));
    }

    [Fact]
    public async Task NestedLinksInAPackFolderAreRefusedBeforeAnythingMoves()
    {
        using var fixture = new SourceFixture();
        await SourceExtractor.ExtractAsync(fixture.Corpus, fixture.Project, token: Token);
        string packed = Path.Combine(fixture.Root, "packed"), outside = Path.Combine(fixture.Root, "outside");
        await SourcePacker.PackAsync(fixture.Project, packed, token: Token);
        Directory.CreateDirectory(outside); File.WriteAllBytes(Path.Combine(outside, "zrdr.zbd"), [42]);
        Directory.Delete(Path.Combine(packed, "m1"), true);
        if (!Junction(Path.Combine(packed, "m1"), outside)) return; // Junctions unavailable on this file system.
        await Assert.ThrowsAsync<IOException>(() => SourcePacker.PackAsync(fixture.Project, packed, token: Token));
        Assert.Equal([42], File.ReadAllBytes(Path.Combine(outside, "zrdr.zbd")));
        Directory.Delete(Path.Combine(packed, "m1"));
    }
    private static bool Junction(string link, string target)
    {
        using var process = Process.Start(new ProcessStartInfo("cmd.exe", $"/c mklink /J \"{link}\" \"{target}\"") { CreateNoWindow = true, UseShellExecute = false, RedirectStandardOutput = true });
        process!.WaitForExit(); return Directory.Exists(link) && new DirectoryInfo(link).Attributes.HasFlag(FileAttributes.ReparsePoint);
    }

    [Fact]
    public async Task SourcesThatChangeDuringAPackAreNeverPublished()
    {
        using var fixture = new SourceFixture();
        await SourceExtractor.ExtractAsync(fixture.Corpus, fixture.Project, token: Token);
        string wave = Path.Combine(fixture.Project, "data", "common", "sounds", "b.wav"), packed = Path.Combine(fixture.Root, "packed");
        // The high-quality bank reads b.wav first; touching it before the medium bank is built makes the pack incoherent.
        var progress = new OnReport(p => { if (p.Item == "soundsm.zbd") File.SetLastWriteTimeUtc(wave, File.GetLastWriteTimeUtc(wave).AddMinutes(1)); });
        var error = await Assert.ThrowsAsync<InvalidDataException>(() => SourcePacker.PackAsync(fixture.Project, packed, progress, Token));
        Assert.Contains("changed while packing", error.Message);
        Assert.Empty(Directory.EnumerateFiles(packed, "*", SearchOption.AllDirectories));
        // Direct snapshot checks: a second read or the final check detects the change.
        SourcePacker.Snapshot snapshot = new(fixture.Project);
        snapshot.Read("data/common/sounds/b.wav", Token);
        File.SetLastWriteTimeUtc(wave, File.GetLastWriteTimeUtc(wave).AddMinutes(1));
        Assert.Throws<InvalidDataException>(() => snapshot.CheckUnchanged(Token));
        Assert.Throws<InvalidDataException>(() => snapshot.Read("data/common/sounds/b.wav", Token));
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
        Assert.Equal(5, (await SourceExtractor.ExtractAsync(fixture.Corpus, fixture.Project, token: Token)).Outputs.Count);
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
    public async Task ManifestFieldsAreBoundedWhenAProjectLoads()
    {
        using var fixture = new SourceFixture();
        await SourceExtractor.ExtractAsync(fixture.Corpus, fixture.Project, token: Token);
        string path = SourceProject.ManifestPath(fixture.Project), original = File.ReadAllText(path);
        var manifest = await SourceProject.LoadAsync(fixture.Project, Token);
        foreach (string damaged in new[]
        {
            original.Replace($"\"name\": \"{manifest.Origin.Name}\"", $"\"name\": \"{new string('n', 300)}\"", StringComparison.Ordinal),
            original.Replace("\"family\": \"passthrough\"", "\"family\": \"mystery\"", StringComparison.Ordinal),
            original.Replace($"\"sha256\": \"{manifest.Outputs[0].Sha256}\"", "\"sha256\": \"xyz\"", StringComparison.Ordinal),
            original.Replace($"\"id\": \"{manifest.Id}\"", "\"id\": \"00000000-0000-0000-0000-000000000000\"", StringComparison.Ordinal),
        })
        {
            Assert.NotEqual(original, damaged);
            File.WriteAllText(path, damaged);
            await Assert.ThrowsAsync<InvalidDataException>(() => SourceProject.LoadAsync(fixture.Project, Token));
        }
    }
}
