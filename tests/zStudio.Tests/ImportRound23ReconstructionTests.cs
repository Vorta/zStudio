using System.Text;
using Recoil.Zbd.Core.Sources;
using Xunit;

namespace Recoil.Zbd.Tests;

public sealed class ImportRound23ReconstructionTests
{
    [Fact]
    public async Task CancellationKeepsAChangedWrittenFileAndRemovesUnchangedSiblings()
    {
        using SourceFixture fixture = new();
        using var cancel = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        string? changed = null;
        byte[] foreign = Encoding.UTF8.GetBytes("changed outside the reconstruction");
        var progress = new OnReport(_ =>
        {
            if (changed != null) return;
            var written = Directory.GetFiles(fixture.Project, "*", SearchOption.AllDirectories);
            if (written.Length < 2) return;
            changed = written[0];
            File.WriteAllBytes(changed, foreign);
            cancel.Cancel();
        });
        var error = await Assert.ThrowsAsync<IOException>(() =>
            SourceExtractor.ExtractAsync(fixture.Corpus, fixture.Project, progress, cancel.Token));
        Assert.NotNull(changed);
        Assert.Contains("another program changed", error.Message);
        Assert.Equal(foreign, File.ReadAllBytes(changed));
        Assert.Equal(changed, Assert.Single(Directory.GetFiles(fixture.Project, "*", SearchOption.AllDirectories)));
    }

    [Fact]
    public async Task CancellationNeverRemovesAnUnrelatedFileAddedToTheNewProject()
    {
        using SourceFixture fixture = new();
        using var cancel = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        string note = Path.Combine(fixture.Project, "keep.txt");
        var progress = new OnReport(_ =>
        {
            if (cancel.IsCancellationRequested) return;
            File.WriteAllText(note, "keep this file");
            cancel.Cancel();
        });
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            SourceExtractor.ExtractAsync(fixture.Corpus, fixture.Project, progress, cancel.Token));
        Assert.Equal("keep this file", File.ReadAllText(note));
        Assert.Equal(note, Assert.Single(Directory.GetFileSystemEntries(fixture.Project)));
    }

    private sealed class OnReport(Action<SourceProgress> action) : IProgress<SourceProgress>
    {
        public void Report(SourceProgress value) => action(value);
    }
}
