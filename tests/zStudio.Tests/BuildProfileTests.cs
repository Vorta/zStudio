using zStudio.Core;
using zStudio.Core.Export;
using zStudio.Core.Formats;
using zStudio.Core.Sources;
using Xunit;

namespace zStudio.Tests;

/// <summary>The texture packs every export builds: the shipped packs, larger tiers for modern cards and the automatic pack.</summary>
public sealed class BuildProfileTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Fact]
    public void ExportsBuildTheShippedPacksAndLargerTiersInEveryMission()
    {
        var packs = BuildProfiles.TexturePacks;
        Assert.Equal(["rtexture2.zbd", "rtexture4.zbd", "texture2.zbd", "texture4.zbd", "texture6.zbd", "rtexture8.zbd", "rtexture16.zbd", "rtexture*.zbd", "texture8.zbd", "texturemax.zbd"], packs.Select(p => p.File));
        // Every pack a mission shipped with, at its shipped budget and largest side: rtexture2 and rtexture4 at 256 texels,
        // texture2, texture4, texture6 and texture8 at 1024 (texture6 and texture8 shipped only in some missions).
        foreach (var (file, megabytes, side) in new[] { ("rtexture2.zbd", 2, 256), ("rtexture4.zbd", 4, 256), ("texture2.zbd", 2, 1024), ("texture4.zbd", 4, 1024), ("texture6.zbd", 6, 1024), ("texture8.zbd", 8, 1024) })
            Assert.Contains(new BuildProfilePack(file, (long)megabytes << 20, side), packs);
        foreach (var pack in packs.Where(p => !p.Automatic))
            Assert.Equal(TexturePackVariant.FromFileName(pack.File), pack.Variant);
        // The automatic pack is a Direct3D pack whose budget is the largest pack it makes.
        var automatic = Assert.Single(packs, p => p.Automatic);
        Assert.Equal((TexturePackKind.Hardware, (long)BuildProfiles.AutomaticMegabytes << 20, 1024), (automatic.Variant.Kind, automatic.BudgetBytes!.Value, automatic.MaximumDimension));
        // The software renderer skips textures wider than 1024 texels.
        Assert.Equal(TexturePackBuilder.SoftwareMaximumDimension, TexturePackVariant.FromFileName("texturemax.zbd")!.MaximumDimension);
    }

    [Fact]
    public void TheAutomaticPackIsNamedForTheMemoryItsTexturesNeed()
    {
        const long MiB = 1 << 20;
        // Up to the largest fixed pack (rtexture16) nothing is added; above it, N rounds the need up to a power of two.
        Assert.Null(BuildProfiles.AutomaticPack(16 * MiB));
        var next = BuildProfiles.AutomaticPack(16 * MiB + 1)!;
        // Its budget is what its name holds: the build fits the textures to it only when they need more.
        Assert.Equal(("rtexture32.zbd", TexturePackKind.Hardware, (long?)(32 * MiB), 1024), (next.FileName, next.Kind, next.BudgetBytes, next.MaximumDimension));
        Assert.Equal("rtexture256.zbd", BuildProfiles.AutomaticPack(200 * MiB)!.FileName);
        // Beyond the budget the pack is the budget's size and fitted to it.
        var capped = BuildProfiles.AutomaticPack(300 * MiB)!;
        Assert.Equal(("rtexture256.zbd", (long?)(256 * MiB)), (capped.FileName, capped.BudgetBytes));
    }

    [Fact]
    public void TextureMemoryIgnoresHeadersABuildWouldRefuse()
    {
        // Planning reads only PNG headers. A corrupt header claiming 2^31 − 1 texels used to make the power-of-two search
        // overflow and spin forever; sizes a build would refuse (over 4096) are not counted, and the build reports them.
        string folder = Directory.CreateTempSubdirectory("zstudio-pngsize-").FullName;
        try
        {
            byte[] header = [137, 80, 78, 71, 13, 10, 26, 10, 0, 0, 0, 13, (byte)'I', (byte)'H', (byte)'D', (byte)'R', 0x7F, 0xFF, 0xFF, 0xFF, 0, 0, 0, 8, 8, 6, 0, 0, 0];
            File.WriteAllBytes(Path.Combine(folder, "huge.png"), header);
            header[16] = 0; header[17] = 0; header[18] = 0x10; header[19] = 0x01; // 4097
            File.WriteAllBytes(Path.Combine(folder, "wide.png"), header);
            header[18] = 0; header[19] = 0x40; // 64 × 8
            File.WriteAllBytes(Path.Combine(folder, "fine.png"), header);
            Assert.Null(TextureSources.PngSize(Path.Combine(folder, "huge.png")));
            Assert.Null(TextureSources.PngSize(Path.Combine(folder, "wide.png")));
            var variant = BuildProfiles.TexturePacks.Single(p => p.Automatic).Variant;
            Assert.Equal(2L * 64 * 8, SourceBuilder.TextureMemory(folder, ["huge.png", "wide.png", "fine.png"], variant, TestContext.Current.CancellationToken));
            // The sizing itself stays bounded for any side.
            Assert.Equal((1024, 1024), TexturePackBuilder.Normalize(int.MaxValue, int.MaxValue, variant));
            // A file another program holds without sharing is not counted rather than failing the plan.
            File.WriteAllBytes(Path.Combine(folder, "locked.png"), header);
            using (new FileStream(Path.Combine(folder, "locked.png"), FileMode.Open, FileAccess.ReadWrite, FileShare.None))
                Assert.Null(TextureSources.PngSize(Path.Combine(folder, "locked.png")));
        }
        finally { Directory.Delete(folder, true); }
    }

    [Fact]
    public async Task ExportsAddTheAutomaticPackWhenTheTexturesOutgrowTheFixedOnes()
    {
        using SourceWorldFixture fixture = new();
        // Nine 1024-texel textures need 18 MB at full size, more than rtexture16: the export adds rtexture32 to m1 alone.
        byte[] large = PngEncoder.Encode(new(1024, 1024, new byte[1024 * 1024 * 4]), Token);
        for (int i = 0; i < 9; i++) fixture.Write($"data/m1/textures/large{i}.png", large);
        var plan = SourceBuilder.Plan(fixture.Project, token: TestContext.Current.CancellationToken);
        var automatic = Assert.Single(plan, p => p.Path == "m1/rtexture32.zbd");
        Assert.Equal((32L << 20, true), (automatic.Pack!.BudgetBytes!.Value, automatic.Automatic));
        Assert.Equal(["m2/rtexture2.zbd", "m2/rtexture4.zbd", "m2/rtexture8.zbd", "m2/rtexture16.zbd"], plan.Where(p => p.Path.StartsWith("m2/rtexture", StringComparison.Ordinal)).Select(p => p.Path));
        // Every mission gets the same packs, texture6 and texture8 included.
        Assert.Equal(plan.Where(p => p.Path.StartsWith("m2/", StringComparison.Ordinal) && p.Family == "textures").Select(p => p.Path[3..]),
            plan.Where(p => p.Path.StartsWith("m1/", StringComparison.Ordinal) && p.Family == "textures" && !p.Automatic).Select(p => p.Path[3..]));

        // Exported, it keeps them at full size; a pack left from an earlier export is reported, the one it writes is not.
        string destination = Path.Combine(fixture.Root, "game");
        Directory.CreateDirectory(Path.Combine(destination, "m1"));
        await File.WriteAllBytesAsync(Path.Combine(destination, "m1", "rtexture64.zbd"), [1], Token);
        var report = await SourceBuilder.ExportAsync(fixture.Project, destination, ["m1/rtexture32.zbd"], token: Token);
        Assert.Equal(0, report.Failed);
        var pack = FormatRegistry.Default.OpenBytes("rtexture32.zbd", await File.ReadAllBytesAsync(Path.Combine(destination, "m1", "rtexture32.zbd"), Token), token: Token);
        Assert.Equal((1024, 1024), pack.Assets.Where(a => a.Name.StartsWith("large", StringComparison.Ordinal)).Select(a => (TextureInfo)a.Content!).Select(t => (t.Width, t.Height)).Distinct().Single());
        Assert.Contains(report.Notes, n => n.StartsWith("m1/rtexture64.zbd", StringComparison.Ordinal));
        Assert.DoesNotContain(report.Notes, n => n.StartsWith("m1/rtexture32.zbd", StringComparison.Ordinal));

        // An export of every output names its automatic pack from the textures as their build counts them, alpha planes
        // included: fifteen of them with graded alpha need rtexture64 (45 MB), where their PNG headers (30 MB) give rtexture32.
        // So the existing files it asks to replace come from its own plan: rtexture64, left above, not the rtexture32 it no
        // longer writes. Declined, nothing is written.
        byte[] rgba = new byte[1024 * 1024 * 4];
        for (int i = 3; i < rgba.Length; i += 4) rgba[i] = 128;
        byte[] graded = PngEncoder.Encode(new(1024, 1024, rgba), Token);
        for (int i = 0; i < 15; i++) fixture.Write($"data/m1/textures/large{i}.png", graded);
        Assert.Contains(SourceBuilder.Plan(fixture.Project, token: Token), p => p.Path == "m1/rtexture32.zbd");
        List<IReadOnlyList<string>> asked = [];
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => SourceBuilder.ExportAsync(fixture.Project, destination, token: Token,
            confirmReplace: (existing, _) => { asked.Add(existing); return Task.FromResult(false); }));
        Assert.Equal(["m1/rtexture64.zbd"], Assert.Single(asked));
        Assert.Equal([1], await File.ReadAllBytesAsync(Path.Combine(destination, "m1", "rtexture64.zbd"), Token));
        Assert.Empty(Directory.GetDirectories(destination, ".zstudio-*"));

        // Beyond the automatic pack's 256 MB its plan says that it holds them reduced (planning reads only the PNG headers).
        for (int i = 15; i < 130; i++) fixture.Write($"data/m1/textures/large{i}.png", large);
        var capped = Assert.Single(SourceBuilder.Plan(fixture.Project, token: Token), p => p.Automatic && p.Path.StartsWith("m1/", StringComparison.Ordinal));
        Assert.Equal("m1/rtexture256.zbd", capped.Path);
        Assert.Equal("The m1 textures need 261 MB of texture memory at full size, more than the 256 MB the automatic rtexture pack may hold; rtexture256.zbd holds them reduced to fit.", Assert.Single(capped.Notes));
    }

    [Fact]
    public async Task ExportsWarnAboutPacksLeftBehindThatTheGameMayLoad()
    {
        using SourceWorldFixture fixture = new();
        string destination = Path.Combine(fixture.Root, "game");
        Directory.CreateDirectory(Path.Combine(destination, "m1"));
        await File.WriteAllBytesAsync(Path.Combine(destination, "m1", "rtexture16.zbd"), [1, 2, 3], Token);
        var report = await SourceBuilder.ExportAsync(fixture.Project, destination, ["m1/rtexture4.zbd"], token: Token);
        Assert.Equal(0, report.Failed);
        Assert.Contains(report.Notes, n => n.StartsWith("m1/rtexture16.zbd", StringComparison.Ordinal));
        // A smaller stale pack can be chosen too (a 2 MB card would open rtexture2).
        await File.WriteAllBytesAsync(Path.Combine(destination, "m1", "rtexture2.zbd"), [1], Token);
        var again = await SourceBuilder.ExportAsync(fixture.Project, destination, ["m1/rtexture4.zbd"], overwrite: true, token: Token);
        Assert.Contains(again.Notes, n => n.StartsWith("m1/rtexture2.zbd", StringComparison.Ordinal));
        // So can a software pack the export does not write: the software renderer opens it by its option.
        await File.WriteAllBytesAsync(Path.Combine(destination, "m1", "texture8.zbd"), [1], Token);
        var software = await SourceBuilder.ExportAsync(fixture.Project, destination, ["m1/rtexture4.zbd"], overwrite: true, token: Token);
        Assert.Contains(software.Notes, n => n.StartsWith("m1/texture8.zbd", StringComparison.Ordinal));
        Assert.DoesNotContain(software.Notes, n => n.StartsWith("m1/rtexture4.zbd", StringComparison.Ordinal));
    }
}
