using System.IO;
using Recoil.Zbd.Core;
using Recoil.Zbd.Core.Formats;
using Recoil.Zbd.Rendering;
using Xunit;

namespace Recoil.Zbd.Desktop.Tests;

/// <summary>GPU-free tests of the same lifetime owner used by static and animated viewports.</summary>
public sealed class PreviewTextureBudgetTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;
    [Fact]
    public async Task SixtyFourSceneOrAnimationAliasesSharePixelsAndWhiteAlphaMask()
    {
        var match = Texture(); TextureMemoryBudget budget = new(32); int decoded = 0;
        using var cache = new PreviewTextureCache(budget, (_, _) => Task.FromResult<ResolvedTexture?>(match), true,
            (value, ct) => { decoded++; return TextureDecoder.Decode(value.Document, value.Asset, ct); });
        var first = Assert.IsType<LoadedPreviewTexture>(await cache.GetAsync("first", Token));
        for (int i = 0; i < 64; i++) Assert.Same(first, await cache.GetAsync("alias-" + i, Token));
        Assert.Equal(1, decoded); Assert.Equal(32, budget.Used); Assert.True(first.Alpha);
        Assert.Equal([255, 255, 255, 0, 255, 255, 255, 128, 255, 255, 255, 255, 255, 255, 255, 255], first.WhiteAlphaMask);
    }

    [Fact]
    public async Task PixelAndPotentialMaskAdmissionPrecedesDecodeAndAllowsValidRetry()
    {
        var match = Texture(); int decoded = 0; TextureMemoryBudget budget = new(31);
        using (var denied = new PreviewTextureCache(budget, (_, _) => Task.FromResult<ResolvedTexture?>(match), true, Decode))
            await Assert.ThrowsAsync<InvalidDataException>(() => denied.GetAsync("alpha", Token));
        Assert.Equal(0, decoded); Assert.Equal(0, budget.Used);
        using (var retry = new PreviewTextureCache(budget, (_, _) => Task.FromResult<ResolvedTexture?>(match), false, Decode))
            Assert.NotNull(await retry.GetAsync("alpha", Token));
        Assert.Equal(1, decoded); Assert.Equal(0, budget.Used);
        DecodedImage Decode(ResolvedTexture value, CancellationToken ct)
        { decoded++; return TextureDecoder.Decode(value.Document, value.Asset, ct); }
    }

    [Theory]
    [InlineData("record")]
    [InlineData("offset")]
    [InlineData("flags")]
    [InlineData("palette")]
    [InlineData("alpha")]
    [InlineData("path")]
    public async Task IdenticalNamesCannotCollapseDistinctRecordOrDecodeMetadata(string difference)
    {
        var first = Texture(); var second = Texture(index: difference == "record" ? 1 : 0);
        var info = (TextureInfo)second.Asset.Content!;
        info = difference switch
        {
            "flags" => info with { Flags = 11 },
            "palette" => info with { PalettePage = 1 },
            "alpha" => info with { AlphaOffset = -1 },
            _ => info
        };
        var document = difference == "path" ? new ZbdDocument("other.zbd", second.Document.Stamp, second.Document.Probe, second.Document.Bytes) : second.Document;
        var asset = new AssetRecord { Id = new(document.Path, AssetKind.Texture, second.Asset.Index), Name = "texture", Offset = difference == "offset" ? 1 : 0, Content = info };
        second = new(document, asset, false);
        TextureMemoryBudget budget = new(32); int decoded = 0;
        using var cache = new PreviewTextureCache(budget, (name, _) => Task.FromResult<ResolvedTexture?>(name == "first" ? first : second), false,
            (value, ct) => { decoded++; return TextureDecoder.Decode(value.Document, value.Asset, ct); });
        Assert.NotSame(await cache.GetAsync("first", Token), await cache.GetAsync("second", Token));
        Assert.Equal(2, decoded); Assert.Equal(32, budget.Used);
    }

    [Fact]
    public async Task IndependentRecordsAndInflightStaticAnimatedStoresShareTheAllowance()
    {
        var a = Texture(); var b = Texture(index: 1); TextureMemoryBudget budget = new(32); int decoded = 0;
        using var staticCache = new PreviewTextureCache(budget, (_, _) => Task.FromResult<ResolvedTexture?>(a), false, Decode);
        using var animationCache = new PreviewTextureCache(budget, (_, _) => Task.FromResult<ResolvedTexture?>(b), false, Decode);
        await Task.WhenAll(staticCache.GetAsync("world", Token), animationCache.GetAsync("animated", Token));
        Assert.Equal(32, budget.Used); Assert.Equal(2, decoded);
        using var next = new PreviewTextureCache(budget, (_, _) => Task.FromResult<ResolvedTexture?>(a), false, Decode);
        await Assert.ThrowsAsync<InvalidDataException>(() => next.GetAsync("next", Token));
        Assert.Equal(2, decoded); staticCache.Dispose();
        Assert.NotNull(await next.GetAsync("next", Token)); Assert.Equal(3, decoded);
        DecodedImage Decode(ResolvedTexture value, CancellationToken ct)
        { decoded++; return TextureDecoder.Decode(value.Document, value.Asset, ct); }
    }

    [Fact]
    public async Task OwnerDisposalKeepsPresentedAndPendingContinuationBuffersCharged()
    {
        var match = Texture(); TextureMemoryBudget budget = new(16);
        var old = Cache(); var uiContinuation = old.Retain();
        Assert.NotNull(await old.GetAsync("old", Token)); old.Dispose();
        Assert.Equal(16, budget.Used);
        using var next = Cache();
        await Assert.ThrowsAsync<InvalidDataException>(() => next.GetAsync("replacement", Token));
        uiContinuation.Dispose(); Assert.Equal(0, budget.Used);
        Assert.NotNull(await next.GetAsync("replacement", Token));
        PreviewTextureCache Cache() => new(budget, (_, _) => Task.FromResult<ResolvedTexture?>(match), false,
            (value, ct) => TextureDecoder.Decode(value.Document, value.Asset, ct));
    }

    [Fact]
    public async Task CancellationDuringDecodeRetainsReservationUntilTheWorkerExits()
    {
        var match = Texture(); TextureMemoryBudget budget = new(16);
        using ManualResetEventSlim entered = new(), finish = new();
        using var cache = new PreviewTextureCache(budget, (_, _) => Task.FromResult<ResolvedTexture?>(match), false, (value, ct) =>
        {
            entered.Set(); Assert.True(finish.Wait(TimeSpan.FromSeconds(10), Token));
            ct.ThrowIfCancellationRequested(); return TextureDecoder.Decode(value.Document, value.Asset, ct);
        });
        var load = Task.Run(() => cache.GetAsync("pending", Token), Token);
        try
        {
            Assert.True(entered.Wait(TimeSpan.FromSeconds(10), Token)); Assert.Equal(16, budget.Used);
            cache.Dispose(); Assert.Equal(16, budget.Used);
        }
        finally { finish.Set(); }
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => load);
        Assert.Equal(0, budget.Used);
        using var retry = new PreviewTextureCache(budget, (_, _) => Task.FromResult<ResolvedTexture?>(match), false,
            (value, ct) => TextureDecoder.Decode(value.Document, value.Asset, ct));
        Assert.NotNull(await retry.GetAsync("retry", Token));
    }

    [Fact]
    public async Task MalformedDecodeReleasesItsReservationAndOpaqueMasksReleaseTheirAllowance()
    {
        var malformed = Texture(); malformed.Asset.Content = ((TextureInfo)malformed.Asset.Content!) with { PaletteCount = 1, PaletteOffset = 0, PaletteLength = 0 };
        var valid = Texture(opaque: true); TextureMemoryBudget budget = new(32);
        using var cache = new PreviewTextureCache(budget, (name, _) => Task.FromResult<ResolvedTexture?>(name == "bad" ? malformed : valid), true,
            (value, ct) => TextureDecoder.Decode(value.Document, value.Asset, ct));
        await Assert.ThrowsAsync<InvalidDataException>(() => cache.GetAsync("bad", Token)); Assert.Equal(0, budget.Used);
        var image = Assert.IsType<LoadedPreviewTexture>(await cache.GetAsync("valid", Token));
        Assert.False(image.Alpha); Assert.Null(image.WhiteAlphaMask); Assert.Equal(16, budget.Used);
    }

    private static ResolvedTexture Texture(int index = 0, bool opaque = false)
    {
        byte[] bytes = [0, 0, 0, 248, 224, 7, 255, 255, 0, 128, 255, 255];
        if (opaque) bytes.AsSpan(8).Fill(255);
        var document = new ZbdDocument("texture.zbd", new(bytes.Length, DateTime.MinValue), new(FormatFamily.TexturePack, 1, Recognition.Supported, "test"), bytes);
        var asset = document.Add(AssetKind.Texture, index, "texture", 0, bytes.Length, content: new TextureInfo(2, 2, 9, 0, -1, 0, 8, 8, -1, 0));
        return new(document, asset, false);
    }
}
