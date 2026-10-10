using Recoil.Zbd.Core;
using Recoil.Zbd.Core.Export;
using Recoil.Zbd.Core.Formats;
using Recoil.Zbd.Core.Sources;
using Xunit;

namespace Recoil.Zbd.Tests;

[Collection("Allocation-sensitive")]
public sealed class TextureDiagnosticGrowthTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;
    private static readonly TexturePackVariant Interface = TexturePackVariant.FromFileName("image.zbd")!;
    private static readonly DecodedImage Red = new(1, 1, [255, 0, 0, 255]);
    private static readonly DecodedImage Blue = new(1, 1, [0, 0, 255, 255]);

    [Fact]
    public void LongSortKeysDoNotGetCopiedIntoWarningsOrChangePackOrderAndBytes()
    {
        PackTexture[] Make(string prefix) => [.. Enumerable.Range(0, 8).SelectMany(i => new[]
        {
            new PackTexture($"name{i}", $"{prefix}a/{i}.png", Red),
            new PackTexture($"name{i}", $"{prefix}b/{i}.png", Blue)
        })];
        var small = Make(""); var longPaths = Make(new string('x', 16384) + "/");
        string retainedIdentity = longPaths[0].SortKey;
        _ = TexturePackBuilder.Build(small, Interface, Token); _ = TexturePackBuilder.Build(longPaths, Interface, Token);
        long before = GC.GetAllocatedBytesForCurrentThread();
        var expected = TexturePackBuilder.Build(small, Interface, Token);
        long smallBytes = GC.GetAllocatedBytesForCurrentThread() - before;
        before = GC.GetAllocatedBytesForCurrentThread();
        var result = TexturePackBuilder.Build(longPaths, Interface, Token);
        long longBytes = GC.GetAllocatedBytesForCurrentThread() - before;
        // Inputs already exist; old interpolation allocated another full16k string for each of eight groups.
        Assert.InRange(longBytes - smallBytes, -4096, 64 * 1024);
        Assert.Equal(expected.Bytes, result.Bytes); Assert.Equal(16, result.Sizes.Count);
        Assert.Same(retainedIdentity, longPaths[0].SortKey);
        Assert.Equal(8, result.Warnings.Count);
        Assert.All(result.Warnings, warning => { Assert.True(warning.Length <= 1024); Assert.Contains("…", warning); });
        var reopened = FormatRegistry.Default.OpenBytes("image.zbd", result.Bytes, token: Token);
        Assert.Equal(16, reopened.Assets.Count); Assert.Equal(2, reopened.Assets.Count(a => a.Name == "name0"));
        Assert.Equal(Red.Rgba, TextureDecoder.Decode(reopened, reopened.Assets.First(a => a.Name == "name0"), Token).Rgba);
    }

    [Fact]
    public void AggregateOmissionIsFirstSoResultPreviewsDiscloseItWithoutDroppingRecords()
    {
        //2050 one-pixel records cross the fixed1024-message allowance, far below the4096-record file limit.
        var textures = Enumerable.Range(0, 1025).SelectMany(i => new[]
        {
            new PackTexture($"n{i}", $"a/{i:0000}.png", Red),
            new PackTexture($"n{i}", $"b/{i:0000}.png", Blue)
        }).ToArray();
        var result = TexturePackBuilder.Build(textures, Interface, Token);
        Assert.Equal(2050, result.Sizes.Count);
        Assert.Equal(BoundedDiagnostics.MaximumMessages, result.Warnings.Count);
        Assert.Equal(BoundedDiagnostics.OmissionNotice, result.Warnings[0]);
        Assert.Equal(1, result.Warnings.Count(w => w == BoundedDiagnostics.OmissionNotice));
        Assert.True(result.Warnings.Sum(w => w.Length) <= BoundedDiagnostics.MaximumRetainedCharacters);
        Assert.Equal(2050, FormatRegistry.Default.OpenBytes("image.zbd", result.Bytes, token: Token).Assets.Count);
    }

    [Fact]
    public void BudgetFitWarningAndOutputRefusalBoundTheirOwnAuthoredFields()
    {
        var variant = new TexturePackVariant(new string('z', 8192), TexturePackKind.Hardware, 0, 8);
        var fit = TexturePackBuilder.Build([new("small", "source", Red)], variant, Token);
        var warning = Assert.Single(fit.Warnings);
        Assert.Contains("even at 8 × 8", warning); Assert.Contains("…", warning); Assert.True(warning.Length < 512);
        int decoded = 0;
        var source = new PackSource("large", "unchanged", 16384, 16384, _ => { decoded++; return Red; })
        { File = new string('x', 16384) };
        var error = Assert.Throws<InvalidDataException>(() => TexturePackBuilder.BuildFromSources([source], Interface, Token));
        Assert.Contains("512 MiB", error.Message); Assert.Contains("…", error.Message);
        Assert.True(error.Message.Length < 1024); Assert.Equal(0, decoded);
        using CancellationTokenSource cancellation = new(); cancellation.Cancel();
        Assert.Throws<OperationCanceledException>(() => TexturePackBuilder.BuildFromSources([source], Interface, cancellation.Token));
        Assert.Equal(0, decoded);
        Assert.Empty(TexturePackBuilder.Build([new("small", "source", Red)], Interface, Token).Warnings);
    }

    [Fact]
    public async Task ActualSourceImageCheckAndExportKeepDuplicateInputsAndDiagnostics()
    {
        string root = Path.Combine(Path.GetTempPath(), "zstudio-texture-notes-" + Guid.NewGuid().ToString("N"));
        string project = Path.Combine(root, "project"), destination = Path.Combine(root, "export");
        Directory.CreateDirectory(Path.Combine(project, "gamegen"));
        try
        {
            foreach (var (folder, image) in new[] { ("a", Red), ("b", Blue) })
            {
                string path = Path.Combine(project, "data", "common", "images", folder);
                Directory.CreateDirectory(path);
                File.WriteAllBytes(Path.Combine(path, "same.png"), PngEncoder.Encode(image, Token));
            }
            var check = Assert.Single((await SourceBuilder.CheckAsync(project, ["image.zbd"], token: Token)).Outputs);
            Assert.Equal("built", check.Status); Assert.Equal(2, check.Items);
            Assert.Contains("appears 2 times", Assert.Single(check.Warnings));
            var exported = Assert.Single((await SourceBuilder.ExportAsync(project, destination, ["image.zbd"], token: Token)).Outputs);
            Assert.Equal(check.Warnings, exported.Warnings); Assert.Equal(2, exported.Items);
            var document = FormatRegistry.Default.OpenBytes("image.zbd", File.ReadAllBytes(Path.Combine(destination, "image.zbd")), token: Token);
            Assert.Equal(2, document.Assets.Count); Assert.All(document.Assets, asset => Assert.Equal("same", asset.Name));
            Assert.Equal(Red.Rgba, TextureDecoder.Decode(document, document.Assets[0], Token).Rgba);
        }
        finally { Directory.Delete(root, true); }
    }
}
