using zStudio.Core.Formats;
using zStudio.Core.Sources;
using Xunit;

namespace zStudio.Tests;

/// <summary>Must-have export fidelity and publication checks for opaque fonts alongside compiled resources.</summary>
public sealed class SourceFontExportTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;
    // Deliberately opaque (and text-like): the exporter must never compile or normalize a font's bytes.
    internal static readonly byte[] Font = "VALUE ( 1 )\r\n"u8.ToArray();

    [Theory]
    [InlineData(ExportTarget.V1)]
    [InlineData(ExportTarget.V2)]
    public async Task FontsKeepTheirBytesAndNamesWithoutChangingImagesOrPreview(ExportTarget target)
    {
        using SourceWorldFixture fixture = new();
        fixture.Write("data/common/zrdr/value.zrd", "VALUE ( 2 )");
        fixture.Write("data/common/fonts/bitmap.png", File.ReadAllBytes(fixture.Path("data/m1/textures/rock.png")));
        string[] outputs = target == ExportTarget.V1 ? ["zrdr.zbd", "image.zbd"] : ["zrdr.zbd", "image.zbd", "rgbaimage.zbd"];
        string before = Path.Combine(fixture.Root, "before"), after = Path.Combine(fixture.Root, "after");
        await SourceBuilder.ExportAsync(fixture.Project, before, outputs, token: Token, target: target);
        var sceneBefore = await SourceWorlds.LoadSceneAsync(fixture.Project, "m1", token: Token);
        fixture.Write("data/common/fonts/Face.ttf", Font);
        fixture.Write("data/common/fonts/nested/Bold.TTF", new byte[] { 0, 1, 0, 0, 255, 128, 0 });
        fixture.Write("data/common/fonts/ignored.otf", Font);
        fixture.Write("data/common/zrdr/ignored.ttf", Font);
        fixture.Write("data/m1/zrdr/ignored.ttf", Font);
        var plan = SourceBuilder.Plan(fixture.Project, token: Token);
        Assert.Equal(new[] { "data/common/fonts/Face.ttf", "data/common/fonts/nested/Bold.TTF", "data/common/zrdr/value.zrd" },
            plan.Single(p => p.Path == "zrdr.zbd").Inputs.Order(StringComparer.Ordinal));
        var report = await SourceBuilder.ExportAsync(fixture.Project, after, outputs, token: Token, target: target);
        Assert.Equal(0, report.Failed);
        Assert.Equal(3, report.Outputs.Single(p => p.Path == "zrdr.zbd").Items);
        byte[] archive = File.ReadAllBytes(Path.Combine(after, "zrdr.zbd"));
        var members = ArchiveSources.Read(archive);
        Assert.Equal(new[] { "Face.ttf", "Bold.TTF", "value.zrd" }, members.Select(m => m.Name));
        Assert.Equal(Font, members[0].Payload.ToArray());
        Assert.Equal(new byte[] { 0, 1, 0, 0, 255, 128, 0 }, members[1].Payload.ToArray());
        Assert.Equal("data\\common\\fonts\\Face.ttf", members[0].SourceField);
        Assert.Equal(ZrdWriter.Write(ZrdText.Parse("VALUE ( 2 )", Token), Token), members[2].Payload.ToArray());
        Assert.DoesNotContain(FormatRegistry.Default.OpenBytes("zrdr.zbd", archive, token: Token).Diagnostics, d => d.Severity == "Error");
        foreach (string image in outputs.Skip(1)) Assert.Equal(File.ReadAllBytes(Path.Combine(before, image)), File.ReadAllBytes(Path.Combine(after, image)));
        var sceneAfter = await SourceWorlds.LoadSceneAsync(fixture.Project, "m1", token: Token);
        Assert.Equal(sceneBefore.ResourceInputs, sceneAfter.ResourceInputs);
        Assert.Equal(sceneBefore.Notes, sceneAfter.Notes);
        Assert.DoesNotContain(sceneAfter.Dependencies, p => p.EndsWith(".ttf", StringComparison.OrdinalIgnoreCase));
        await SourceBuilder.ExportAsync(fixture.Project, after, outputs, overwrite: true, token: Token, target: target);
        Assert.Equal(archive, File.ReadAllBytes(Path.Combine(after, "zrdr.zbd")));
        Assert.Equal(Font, File.ReadAllBytes(fixture.Path("data/common/fonts/Face.ttf")));
    }

    [Fact]
    public async Task FontOnlyArchivesIncludePendingFontsAndRefuseDuplicateBasenames()
    {
        using SourceWorldFixture fixture = new();
        const string path = "data/common/fonts/face.ttf", pending = "data/common/fonts/new.TTF";
        fixture.Write(path, Font);
        var planned = SourceBuilder.Plan(fixture.Project, [pending], automaticPacks: false, token: Token);
        Assert.Equal(new[] { path, pending }, planned.Single(p => p.Path == "zrdr.zbd").Inputs);
        Assert.Equal(1, (await SourceBuilder.CheckAsync(fixture.Project, ["zrdr.zbd"], token: Token)).Built);
        fixture.Write("data/common/fonts/nested/FACE.TTF", Font);
        var failed = Assert.Single((await SourceBuilder.CheckAsync(fixture.Project, ["zrdr.zbd"], token: Token)).Outputs);
        Assert.Equal("failed", failed.Status);
        Assert.Contains("both become archive member", failed.Error);
        string destination = Path.Combine(fixture.Root, "export");
        await Assert.ThrowsAsync<InvalidDataException>(() => SourceBuilder.ExportAsync(fixture.Project, destination, ["zrdr.zbd"], token: Token));
        Assert.Empty(Directory.GetFileSystemEntries(destination));
    }

    [Theory]
    [InlineData("edit")]
    [InlineData("add")]
    [InlineData("remove")]
    public async Task FontChangesDuringExportLeaveTheDestinationUntouched(string change)
    {
        using SourceWorldFixture fixture = new();
        const string path = "data/common/fonts/face.ttf";
        fixture.Write(path, Font);
        string destination = Path.Combine(fixture.Root, "export");
        Directory.CreateDirectory(destination);
        string archive = Path.Combine(destination, "zrdr.zbd");
        File.WriteAllBytes(archive, [42]);
        bool changed = false;
        var progress = new OnReport(value =>
        {
            if (changed || value.Item != "Publishing") return;
            changed = true;
            if (change == "add") fixture.Write("data/common/fonts/new.ttf", Font);
            else if (change == "remove") File.Delete(fixture.Path(path));
            else
            {
                var stamp = File.GetLastWriteTimeUtc(fixture.Path(path));
                fixture.Write(path, Font.Select(b => (byte)(b ^ 1)).ToArray());
                File.SetLastWriteTimeUtc(fixture.Path(path), stamp);
            }
        });
        await Assert.ThrowsAsync<InvalidDataException>(() => SourceBuilder.ExportAsync(fixture.Project, destination, ["zrdr.zbd"], overwrite: true, progress: progress, token: Token));
        Assert.True(changed);
        Assert.Equal(new byte[] { 42 }, File.ReadAllBytes(archive));
        Assert.Equal(new[] { archive }, Directory.GetFileSystemEntries(destination));
    }

    private sealed class OnReport(Action<SourceProgress> report) : IProgress<SourceProgress>
    {
        public void Report(SourceProgress value) => report(value);
    }
}
