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
        // Planning and construction both count the alpha planes: twelve textures need 36 MB.
        byte[] glass = Png(1024, 128);
        for (int i = 0; i < 12; i++) fixture.Write($"data/m1/textures/glass{i}.png", glass);
        var plan = Assert.Single(SourceBuilder.Plan(fixture.Project, token: TestContext.Current.CancellationToken), p => p.Automatic && p.Path.StartsWith("m1/", StringComparison.Ordinal));
        Assert.Equal("m1/rtexture64.zbd", plan.Path);
        Assert.Empty(plan.Notes);

        List<string> warnings = [];
        var pack = await ExportPack(fixture, plan.Path, warnings);
        Assert.DoesNotContain(warnings, w => w.Contains("reduced to fit", StringComparison.Ordinal));
        Assert.All(Enumerable.Range(0, 12), i => Assert.Equal(1024, Width(pack, $"glass{i}")));
        long stored = pack.Assets.Where(a => a.Kind == AssetKind.Texture).Sum(a => ((TextureInfo)a.Content!) is var t ? (long)t.Width * t.Height * (a.Name.StartsWith("glass", StringComparison.Ordinal) ? 3 : 2) : 0);
        Assert.InRange(stored, 36L << 20, 64L << 20);

        // Selected-output exports and full exports choose the same automatic pack for 21 MB of panes.
        for (int i = 0; i < 12; i++) File.Delete(fixture.Path($"data/m1/textures/glass{i}.png"));
        for (int i = 0; i < 7; i++) fixture.Write($"data/m1/textures/pane{i}.png", glass);
        var packs = SourceBuilder.Plan(fixture.Project, token: Token).Where(p => p.Path.StartsWith("m1/", StringComparison.Ordinal)).ToArray();
        Assert.Equal(["m1/rtexture32.zbd"], packs.Where(p => p.FullSize && p.Family == "textures").Select(p => p.Path));
        warnings.Clear();
        var selected = await ExportPack(fixture, "m1/rtexture32.zbd", warnings);
        Assert.DoesNotContain(warnings, w => w.Contains("reduced to fit", StringComparison.Ordinal));
        Assert.All(Enumerable.Range(0, 7), i => Assert.Equal(1024, Width(selected, $"pane{i}")));
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
        // Planning includes the camouflage brought from another mission's folder.
        fixture.Write("gamegen/m1.gs", string.Join("\r\n", "set worldName world", "SetModelDirectory ..\\data\\m1\\models", "SetTextureDirectory ..\\data\\m1\\textures",
            "NewWorld %worldName%", "FindNode %worldName%", "GameGenSetWorld %worldName%", "FindNode %worldName%", "WorldOrigin 0.0 512.0", "WorldExtents 512.0 -512.0", "WorldPartition 256 -256",
            "LoadGameGen m1.gltf m1.flt", "DeleteTree m1.flt", "SetModelDirectory ..\\data\\m2\\models\\bft", "LoadGameGen tank.gltf tank", "GameZWriteZBDFile ..\\m1\\gamez.zbd", "Quit", ""));
        fixture.Write("data/m2/textures/bft/camo.png", Png(1024, 255));
        byte[] stone = Png(1024, 255);
        for (int i = 0; i < 15; i++) fixture.Write($"data/m1/textures/stone{i}.png", stone);
        var plan = Assert.Single(SourceBuilder.Plan(fixture.Project, token: TestContext.Current.CancellationToken), p => p.Automatic && p.Path.StartsWith("m1/", StringComparison.Ordinal));
        Assert.Equal("m1/rtexture64.zbd", plan.Path);

        List<string> warnings = [];
        var pack = await ExportPack(fixture, plan.Path, warnings);
        Assert.Contains(pack.Assets, a => a.Name == "camo");
        Assert.DoesNotContain(warnings, w => w.Contains("reduced to fit", StringComparison.Ordinal));
        Assert.Equal(1024, Width(pack, "camo"));
        Assert.All(Enumerable.Range(0, 15), i => Assert.Equal(1024, Width(pack, $"stone{i}")));
        long stored = pack.Assets.Where(a => a.Kind == AssetKind.Texture).Select(a => (TextureInfo)a.Content!).Sum(t => 2L * t.Width * t.Height);
        Assert.InRange(stored, 32L << 20, 64L << 20);
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
        // Automatic planning counts decoded sources; these small textures need no extra pack.
        _ = SourceBuilder.Plan(fixture.Project, token: TestContext.Current.CancellationToken);
        Assert.DoesNotContain(SourceBuilder.Plan(fixture.Project, token: Token), p => p.Path.StartsWith("m1/", StringComparison.Ordinal) && p.Automatic);
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

    /// <summary>
    /// The sizes the Export ZBD files window shows for the 16-bit packs (design/export-zbd-files-rgba.md, owner feedback): the
    /// estimate from the textures' sizes and transparency is the size of the file the build writes, with the same records
    /// reduced and the same need, for budgeted Direct3D and software packs that must fit, an unbudgeted software pack and
    /// image.zbd; opaque, keyed and translucent textures, direct and paletted ones in a software pack, two palette pages.
    /// </summary>
    [Fact]
    public void SixteenBitEstimatesMatchWrittenSizesWhenPalettePagesRemainPopulated()
    {
        List<PackTexture> textures = [];
        for (int i = 0; i < 30; i++)
        {
            int w = 64 << (i % 5), h = (64 << (i % 3)) + i;
            byte[] rgba = new byte[w * h * 4];
            for (int p = 0; p < w * h; p++)
            {
                rgba[p * 4] = (byte)(i * 8 + p); rgba[p * 4 + 1] = (byte)(i * 5); rgba[p * 4 + 2] = (byte)(255 - i * 7);
                rgba[p * 4 + 3] = (i % 3) switch { 0 => 255, 1 => (byte)(p % 2 == 0 ? 0 : 255), _ => (byte)(p % 200 + 20) };
            }
            textures.Add(new($"t{i:00}", $"data/m1/textures/t{i:00}.png", new(w, h, rgba), Direct: i % 7 == 0));
        }
        var planned = textures.Select(t => new PlannedTexture(t.SortKey, t.Name, t.Master.Width, t.Master.Height, TexturePackBuilder.Classify(t.Master), t.Direct)).ToArray();
        foreach (string pack in new[] { "rtexture2.zbd", "rtexture16.zbd", "texture2.zbd", "texturemax.zbd", "image.zbd" })
        {
            var variant = TexturePackVariant.FromFileName(pack)!;
            var built = TexturePackBuilder.Build(textures, variant, Token);
            var estimate = TexturePackBuilder.Estimate(planned, variant, Token);
            Assert.Equal((pack, built.Bytes.LongLength, built.Reduced.Count, built.FullSizeBytes, textures.Count), (pack, estimate.Bytes, estimate.Reduced, estimate.NeedBytes, estimate.Textures));
            if (pack is "rtexture2.zbd" or "texture2.zbd") Assert.True(estimate.Reduced > 0, pack);
        }
    }
}
