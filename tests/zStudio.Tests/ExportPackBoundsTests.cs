using System.Security.AccessControl;
using System.Security.Principal;
using zStudio.Core;
using zStudio.Core.Export;
using zStudio.Core.Formats;
using zStudio.Core.Sources;
using Xunit;

namespace zStudio.Tests;

/// <summary>
/// What an export promises beyond its outputs: the automatic pack holds no more than its name, planning reads only what it
/// needs, a report never fails a written export, and a stopped reconstruction removes only what it wrote.
/// </summary>
public sealed class ExportPackBoundsTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;
    private sealed class OnReport(Action<SourceProgress> action) : IProgress<SourceProgress> { public void Report(SourceProgress value) => action(value); }

    private static byte[] Png(int side, byte alpha)
    {
        byte[] rgba = new byte[side * side * 4];
        for (int i = 0; i < side * side; i++) { rgba[i * 4] = (byte)i; rgba[i * 4 + 1] = (byte)(i >> 8); rgba[i * 4 + 2] = 90; rgba[i * 4 + 3] = alpha; }
        return PngEncoder.Encode(new(side, side, rgba));
    }
    private static async Task<AssetDocument> ExportPack(SourceWorldFixture fixture, string pack, List<string> warnings)
    {
        string destination = Path.Combine(fixture.Root, "game-" + Guid.NewGuid().ToString("N"));
        var report = await SourceBuilder.ExportAsync(fixture.Project, destination, [pack], token: Token);
        var output = Assert.Single(report.Outputs);
        Assert.Equal("built", output.Status);
        warnings.AddRange(output.Warnings);
        return FormatRegistry.Default.OpenBytes(Path.GetFileName(pack), await File.ReadAllBytesAsync(Path.Combine(destination, pack), Token), token: Token);
    }
    private static int Width(AssetDocument pack, string name) => ((TextureInfo)pack.Assets.Single(a => a.Name == name).Content!).Width;

    [Fact]
    public async Task PacksCountAlphaPlanesAsTheirBudgetsDo()
    {
        using SourceWorldFixture fixture = new();
        // Twelve translucent 1024-texel textures need 24 MB at two bytes a texel, more than rtexture16, so planning names the
        // automatic pack rtexture32; with their alpha planes, counted as every pack's budget counts them, they need 36 MB.
        byte[] glass = Png(1024, 128);
        for (int i = 0; i < 12; i++) fixture.Write($"data/m1/textures/glass{i}.png", glass);
        var plan = Assert.Single(SourceBuilder.Plan(fixture.Project, token: TestContext.Current.CancellationToken), p => p.Automatic && p.Path.StartsWith("m1/", StringComparison.Ordinal));
        Assert.Equal("m1/rtexture32.zbd", plan.Path);
        Assert.Empty(plan.Notes);

        List<string> warnings = [];
        var pack = await ExportPack(fixture, "m1/rtexture32.zbd", warnings);
        // The pack keeps its name's promise: textures are halved, and the export says why.
        Assert.Contains(warnings, w => w.Contains("more than rtexture32.zbd holds; they were reduced to fit", StringComparison.Ordinal) && w.Contains("alpha planes", StringComparison.Ordinal));
        Assert.Contains(Enumerable.Range(0, 12), i => Width(pack, $"glass{i}") < 1024);
        long stored = pack.Assets.Where(a => a.Kind == AssetKind.Texture).Sum(a => ((TextureInfo)a.Content!) is var t ? (long)t.Width * t.Height * (a.Name.StartsWith("glass", StringComparison.Ordinal) ? 3 : 2) : 0);
        Assert.InRange(stored, 1, 32L << 20);

        // Seven translucent panes of 1024 texels take 14 MB at two bytes a texel, so their PNG headers need no automatic pack
        // and rtexture16 is the pack that must hold them at full size; with their alpha planes they need 21 MB.
        for (int i = 0; i < 12; i++) File.Delete(fixture.Path($"data/m1/textures/glass{i}.png"));
        for (int i = 0; i < 7; i++) fixture.Write($"data/m1/textures/pane{i}.png", glass);
        var packs = SourceBuilder.Plan(fixture.Project, token: Token).Where(p => p.Path.StartsWith("m1/", StringComparison.Ordinal)).ToArray();
        Assert.Equal(["m1/rtexture16.zbd"], packs.Where(p => p.FullSize).Select(p => p.Path));
        Assert.DoesNotContain(packs, p => p.Automatic);
        // Exported alone, it says which textures its budget reduced; an export of every output counts them as their build
        // does and adds the automatic pack that holds them at full size.
        warnings.Clear();
        await ExportPack(fixture, "m1/rtexture16.zbd", warnings);
        Assert.Contains(warnings, w => w.Contains("more than rtexture16.zbd holds; they were reduced to fit: pane0 (1024 × 1024, stored 512 × 1024)", StringComparison.Ordinal));
        string all = Path.Combine(fixture.Root, "all");
        var full = await SourceBuilder.ExportAsync(fixture.Project, all, token: Token);
        Assert.Equal(0, full.Failed);
        Assert.DoesNotContain(full.Outputs.SelectMany(o => o.Warnings), w => w.Contains("reduced to fit", StringComparison.Ordinal));
        var automatic = FormatRegistry.Default.OpenBytes("rtexture32.zbd", await File.ReadAllBytesAsync(Path.Combine(all, "m1", "rtexture32.zbd"), Token), token: Token);
        Assert.All(Enumerable.Range(0, 7), i => Assert.Equal(1024, Width(automatic, $"pane{i}")));
    }

    [Fact]
    public async Task TheAutomaticPackCountsTexturesItsWorldBringsFromOtherFolders()
    {
        using SourceWorldFixture fixture = new();
        // m1 also loads m2's tank, whose camouflage only m2's vehicle folder holds; planning counts only m1's folders.
        fixture.Write("gamegen/m1.gs", string.Join("\r\n", "set worldName world", "SetModelDirectory ..\\data\\m1\\models", "SetTextureDirectory ..\\data\\m1\\textures",
            "NewWorld %worldName%", "FindNode %worldName%", "GameGenSetWorld %worldName%", "FindNode %worldName%", "WorldOrigin 0.0 512.0", "WorldExtents 512.0 -512.0", "WorldPartition 256 -256",
            "LoadGameGen m1.gltf m1.flt", "DeleteTree m1.flt", "SetModelDirectory ..\\data\\m2\\models\\bft", "LoadGameGen tank.gltf tank", "GameZWriteZBDFile ..\\m1\\gamez.zbd", "Quit", ""));
        fixture.Write("data/m2/textures/bft/camo.png", Png(1024, 255));
        byte[] stone = Png(1024, 255);
        for (int i = 0; i < 15; i++) fixture.Write($"data/m1/textures/stone{i}.png", stone);
        var plan = Assert.Single(SourceBuilder.Plan(fixture.Project, token: TestContext.Current.CancellationToken), p => p.Automatic && p.Path.StartsWith("m1/", StringComparison.Ordinal));
        Assert.Equal("m1/rtexture32.zbd", plan.Path);

        List<string> warnings = [];
        var pack = await ExportPack(fixture, "m1/rtexture32.zbd", warnings);
        // 30 MB of m1's own textures and the 2 MB camouflage do not fit 32 MB at full size beside the rock.
        Assert.Contains(pack.Assets, a => a.Name == "camo");
        Assert.Contains(warnings, w => w.Contains("those its world brings from other folders", StringComparison.Ordinal) && w.Contains("more than rtexture32.zbd holds", StringComparison.Ordinal));
        Assert.True(Width(pack, "camo") < 1024 || Enumerable.Range(0, 15).Any(i => Width(pack, $"stone{i}") < 1024));
        long stored = pack.Assets.Where(a => a.Kind == AssetKind.Texture).Select(a => (TextureInfo)a.Content!).Sum(t => 2L * t.Width * t.Height);
        Assert.InRange(stored, 1, 32L << 20);
    }

    [Fact]
    public async Task ListingWorldsDoesNotSizeExportPacksAndSourceSceneKeepsOriginalTextures()
    {
        using SourceWorldFixture fixture = new();
        fixture.Write("data/m1/textures/stone.png", Png(64, 255));
        string stone = SourceProject.Resolve(fixture.Project, "data/m1/textures/stone.png");
        // Mission listing does not size export packs; source scenes retain the authored PNG dimensions.
        Assert.Equal(["m1", "m2"], SourceWorlds.Missions(fixture.Project, TestContext.Current.CancellationToken));
        Assert.DoesNotContain(SourceBuilder.Plan(fixture.Project, automaticPacks: false, token: TestContext.Current.CancellationToken), p => p.Automatic);
        var build = await SourceWorlds.LoadSceneAsync(fixture.Project, "m1", token: Token);
        Assert.Equal(64, build.Textures["stone"].Width);
        Assert.Empty(Directory.EnumerateFiles(fixture.Project, "*.zbd", SearchOption.AllDirectories));
        Assert.False(TextureSources.HeaderRead(stone));
        // An export's plan reads them to name the automatic pack.
        _ = SourceBuilder.Plan(fixture.Project, token: TestContext.Current.CancellationToken);
        Assert.True(TextureSources.HeaderRead(stone));
    }

    [Fact]
    public async Task ADestinationThatCannotBeListedDoesNotFailAWrittenExport()
    {
        if (!OperatingSystem.IsWindows()) return; // The listing is denied with a Windows access rule.
        using SourceWorldFixture fixture = new();
        string destination = Path.Combine(fixture.Root, "game"), mission = Path.Combine(destination, "m1");
        Directory.CreateDirectory(mission);
        // Writing into m1 stays allowed; only listing it (which looks for packs left by other exports) is denied.
        DirectoryInfo folder = new(mission);
        var security = folder.GetAccessControl();
        FileSystemAccessRule deny = new(WindowsIdentity.GetCurrent().User!, FileSystemRights.ListDirectory, AccessControlType.Deny);
        security.AddAccessRule(deny); folder.SetAccessControl(security);
        try
        {
            Assert.ThrowsAny<UnauthorizedAccessException>(() => Directory.GetFiles(mission));
            var report = await SourceBuilder.ExportAsync(fixture.Project, destination, ["m1/rtexture4.zbd"], token: Token);
            Assert.Equal((1, 0), (report.Built, report.Failed));
            Assert.True(File.Exists(Path.Combine(mission, "rtexture4.zbd")));
            Assert.Contains(report.Notes, n => n.StartsWith("The destination's texture packs could not be listed", StringComparison.Ordinal));
        }
        finally { security.RemoveAccessRule(deny); folder.SetAccessControl(security); }
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task AStoppedReconstructionKeepsWhatOthersPutInItsFolder(bool existing)
    {
        using SourceFixture fixture = new();
        if (existing) Directory.CreateDirectory(fixture.Project);
        using var cancel = CancellationTokenSource.CreateLinkedTokenSource(Token);
        string readme = Path.Combine(fixture.Project, "readme.txt"), mine = Path.Combine(fixture.Project, "data", "m1", "mine.txt");
        // Another program writes into the folder while the reconstruction runs (it has written the scripts and m1's resources).
        var progress = new OnReport(p =>
        {
            if (p.Completed != 3 || cancel.IsCancellationRequested) return;
            Assert.True(File.Exists(Path.Combine(fixture.Project, "data", "m1", "zrdr", "ai.zrd")));
            File.WriteAllText(readme, "mine"); File.WriteAllText(mine, "mine");
            cancel.Cancel();
        });
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => SourceExtractor.ExtractAsync(fixture.Corpus, fixture.Project, progress, cancel.Token));
        // Everything the reconstruction wrote is gone; the other program's files stay, with the folders that hold them.
        Assert.Equal(["data", "readme.txt"], Directory.GetFileSystemEntries(fixture.Project).Select(Path.GetFileName).Order(StringComparer.Ordinal));
        Assert.Equal(["mine.txt"], Directory.GetFileSystemEntries(Path.Combine(fixture.Project, "data", "m1")).Select(Path.GetFileName));
        Assert.Equal(["m1"], Directory.GetFileSystemEntries(Path.Combine(fixture.Project, "data")).Select(Path.GetFileName));
        Assert.Equal("mine", await File.ReadAllTextAsync(readme, Token));
    }
}
