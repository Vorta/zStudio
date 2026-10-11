using System.Text;
using Recoil.Zbd.Core;
using Recoil.Zbd.Core.Sources;
using Xunit;

namespace Recoil.Zbd.Tests;

public sealed class SourceSoundDiagnosticBudgetTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Fact]
    public void UndeclaredSoundsBoundWarningsBeforeRetentionWithoutDroppingArchiveMembers()
    {
        using Project project = new();
        byte[] wave = SourceFixture.Tone(SourceFixture.Low, 1, null);
        // Small payloads, real maximum-length archive names and the ordinary shared builder.
        // No large disk workload or injected diagnostic limit is needed to exhaust formatting allowance.
        var inputs = Enumerable.Range(0, 1200).Select(i =>
            $"{SourceBuilder.SoundsFolder}/{new string('&', 55)}{i:D4}.wav").ToArray();
        var pending = inputs.ToDictionary(p => p, _ => wave, StringComparer.OrdinalIgnoreCase);
        var snapshot = new SourceBuilder.Snapshot(project.Root, pending);
        foreach (string bank in SourceBuilder.Banks)
        {
            var built = SourceBuilder.Build(project.Root, new(bank, "sounds", inputs), snapshot, Token);
            Assert.Equal(inputs.Length, built.Items);
            var members = ArchiveSources.Read(built.Bytes);
            Assert.Equal(inputs.Select(Path.GetFileName), members.Select(m => m.Name));
            Assert.All(members, m => Assert.True(m.Payload.Span.SequenceEqual(wave)));
            if (bank == "soundsh.zbd")
            {
                Assert.Equal(BoundedDiagnostics.OmissionNotice, built.Warnings[0]);
                Assert.Contains(BoundedDiagnostics.OmissionNotice, built.Warnings.Take(4));
                Assert.InRange(built.Warnings.Count, 2, BoundedDiagnostics.MaximumMessages);
                Assert.True(built.Warnings.Sum(w => w.Length) <= BoundedDiagnostics.MaximumRetainedCharacters);
                Assert.All(built.Warnings, w => Assert.True(w.Length <= BoundedDiagnostics.MaximumMessageCharacters));
                Assert.Contains(Path.GetFileName(inputs[0]), built.Warnings[1]);
            }
            else Assert.Empty(built.Warnings);
        }
        Assert.All(pending.Values, bytes => Assert.Equal(wave, bytes));
    }

    [Fact]
    public async Task OrdinaryExportPreservesFullNamesBytesAndUncappedWarnings()
    {
        using Project project = new();
        byte[] wave = SourceFixture.Tone(SourceFixture.Low, 1, null);
        string first = new string('a', 58) + "1.wav", second = new string('a', 58) + "2.wav";
        foreach (string name in new[] { first, second, "declared.wav" }) project.Write($"{SourceBuilder.SoundsFolder}/{name}", wave);
        project.Write(SourceBuilder.SoundDefinitions, Encoding.UTF8.GetBytes(
            "( declared.wav HIGH ( 11025 8 1 ) MED ( 11025 8 1 ) LOW ( 11025 8 1 ) )"));
        string destination = Path.Combine(project.Parent, "export");
        var report = await SourceBuilder.ExportAsync(project.Root, destination, SourceBuilder.Banks, token: Token);
        Assert.Equal(3, report.Built); Assert.Equal(0, report.Failed);
        var high = report.Outputs.Single(o => o.Path == "soundsh.zbd");
        Assert.Equal(new[] { first, second }.Select(name => $"{name} has no format in sounds.zrd; every bank uses the source format."), high.Warnings);
        Assert.All(report.Outputs.Where(o => o.Path != "soundsh.zbd"), o => Assert.Empty(o.Warnings));
        foreach (string bank in SourceBuilder.Banks)
        {
            var members = ArchiveSources.Read(File.ReadAllBytes(Path.Combine(destination, bank)));
            Assert.Equal(new[] { first, second, "declared.wav" }.Order(StringComparer.OrdinalIgnoreCase), members.Select(m => m.Name));
            Assert.All(members, m => Assert.True(m.Payload.Span.SequenceEqual(wave)));
        }
        Assert.Equal(wave, File.ReadAllBytes(SourceProject.Resolve(project.Root, $"{SourceBuilder.SoundsFolder}/{first}")));
    }

    [Fact]
    public void DiagnosticBoundsDoNotMakeDuplicateSoundIdentitiesAcceptable()
    {
        using Project project = new();
        byte[] wave = SourceFixture.Tone(SourceFixture.Low, 1, null);
        string[] inputs = [$"{SourceBuilder.SoundsFolder}/a/shared.wav", $"{SourceBuilder.SoundsFolder}/b/SHARED.wav"];
        var snapshot = new SourceBuilder.Snapshot(project.Root, inputs.ToDictionary(p => p, _ => wave));
        var error = Assert.Throws<InvalidDataException>(() => SourceBuilder.Build(project.Root, new("soundsh.zbd", "sounds", inputs), snapshot, Token));
        Assert.Contains("would both become sound", error.Message);
        // The failed operation does not corrupt the snapshot or the next valid build.
        var retry = SourceBuilder.Build(project.Root, new("soundsh.zbd", "sounds", [inputs[0]]), snapshot, Token);
        Assert.Equal("shared.wav", Assert.Single(ArchiveSources.Read(retry.Bytes)).Name);
        Assert.Single(retry.Warnings);
    }

    private sealed class Project : IDisposable
    {
        internal string Parent { get; } = Path.Combine(Path.GetTempPath(), "zstudio-sound-warning-" + Guid.NewGuid().ToString("N"));
        internal string Root => Path.Combine(Parent, "project");
        internal Project()
        {
            Directory.CreateDirectory(Path.Combine(Root, "data"));
            Directory.CreateDirectory(Path.Combine(Root, "gamegen"));
        }
        internal void Write(string relative, byte[] bytes)
        {
            string path = SourceProject.Resolve(Root, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllBytes(path, bytes);
        }
        public void Dispose() => Directory.Delete(Parent, true);
    }
}
