using Recoil.Zbd.Core;
using Recoil.Zbd.Core.Sources;
using Xunit;

namespace Recoil.Zbd.Tests;

public sealed class SourceBuilderNativeDirectoryTests
{
    [Fact]
    public async Task InPlaceStagingRedirectionCannotWriteOrCleanAnOutsideFolder()
    {
        if (!OperatingSystem.IsWindows()) return;
        using SourceFixture fixture = new();
        CancellationToken token = TestContext.Current.CancellationToken;
        await SourceExtractor.ExtractAsync(fixture.Corpus, fixture.Project, token: token);
        string destination = Path.Combine(fixture.Root, "export"), outside = Path.Combine(fixture.Root, "outside");
        Directory.CreateDirectory(outside);
        string sentinel = Path.Combine(outside, "interp.zbd"); File.WriteAllText(sentinel, "outside sentinel");
        InPlaceDirectoryJunction? mutation = null;
        try
        {
            var error = await Assert.ThrowsAsync<InvalidDataException>(() => SourceBuilder.ExportAsync(fixture.Project, destination, ["interp.zbd"],
                progress: new OnReport(p =>
                {
                    if (p.Completed != 0 || mutation != null) return;
                    string staging = Assert.Single(Directory.GetDirectories(destination, ".zstudio-staging-*"));
                    mutation = new(staging, outside);
                }), token: token));
            Assert.Contains("Nothing was written", error.Message);
            Assert.NotNull(mutation);
            Assert.Equal("outside sentinel", File.ReadAllText(sentinel));
            Assert.Equal([sentinel], Directory.GetFiles(outside));
            Assert.False(File.Exists(Path.Combine(destination, "interp.zbd")));
        }
        finally { mutation?.Dispose(); }
    }

    [Theory]
    [InlineData("replace")]
    [InlineData("install")]
    public void InPlaceTargetRedirectionCannotPublishOrRollbackOutsideTheCapturedParent(string at)
    {
        if (!OperatingSystem.IsWindows()) return;
        using SourceFixture fixture = new();
        string staging = Path.Combine(fixture.Root, "stage"), destination = Path.Combine(fixture.Root, "export"), outside = Path.Combine(fixture.Root, "outside");
        Directory.CreateDirectory(Path.Combine(staging, "m1")); Directory.CreateDirectory(Path.Combine(destination, "m1")); Directory.CreateDirectory(outside);
        File.WriteAllBytes(Path.Combine(staging, "m1", "gamez.zbd"), [1, 2, 3]);
        string sentinel = Path.Combine(outside, "gamez.zbd"); File.WriteAllText(sentinel, "outside sentinel");
        InPlaceDirectoryJunction? mutation = null;
        try
        {
            Assert.ThrowsAny<IOException>(() => SourceBuilder.Publish(staging, destination,
                [("m1/gamez.zbd", JournalDigest.OfContent([1, 2, 3]))], true, TestContext.Current.CancellationToken,
                (step, _) => { if (step == at) mutation = new(Path.Combine(destination, "m1"), outside); }));
            Assert.NotNull(mutation);
            Assert.Equal("outside sentinel", File.ReadAllText(sentinel));
            Assert.Equal([sentinel], Directory.GetFiles(outside));
        }
        finally { mutation?.Dispose(); }
        Assert.False(File.Exists(Path.Combine(destination, "m1", "gamez.zbd")));
    }

    private sealed class OnReport(Action<SourceProgress> action) : IProgress<SourceProgress>
    {
        public void Report(SourceProgress value) => action(value);
    }
}
