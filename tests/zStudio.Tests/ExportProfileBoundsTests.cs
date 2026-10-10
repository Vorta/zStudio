using System.IO;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using Recoil.Zbd.Core;
using Recoil.Zbd.Core.Export;
using Recoil.Zbd.Core.Formats;
using Recoil.Zbd.Core.Sources;
using Xunit;

namespace Recoil.Zbd.Tests;

/// <summary>
/// What an export promises beyond its outputs: the automatic pack holds no more than its name, planning reads only what it
/// needs, a report never fails a written export, a stale profile file blocks only itself, and a stopped reconstruction
/// removes only what it wrote.
/// </summary>
public sealed class ExportProfileBoundsTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;
    private sealed class OnReport(Action<SourceProgress> action) : IProgress<SourceProgress> { public void Report(SourceProgress value) => action(value); }
    /// <summary>One fixed 1 MB Direct3D pack, so a few 512-texel textures make the automatic pack rtexture2.</summary>
    private const string Small = """{ "format": "recoil-build-profile", "version": 1, "texturePacks": [ { "file": "rtexture1.zbd" }, { "file": "rtexture*.zbd", "budgetMiB": 64 } ] }""";

    private static byte[] Png(int side, byte alpha)
    {
        byte[] rgba = new byte[side * side * 4];
        for (int i = 0; i < side * side; i++) { rgba[i * 4] = (byte)i; rgba[i * 4 + 1] = (byte)(i >> 8); rgba[i * 4 + 2] = 90; rgba[i * 4 + 3] = alpha; }
        return PngEncoder.Encode(new(side, side, rgba));
    }
    private static async Task<ZbdDocument> ExportPack(SourceWorldFixture fixture, string pack, List<string> warnings)
    {
        string destination = Path.Combine(fixture.Root, "game-" + Guid.NewGuid().ToString("N"));
        var report = await SourceBuilder.ExportAsync(fixture.Project, destination, [pack], token: Token, profile: "small");
        var output = Assert.Single(report.Outputs);
        Assert.Equal("built", output.Status);
        warnings.AddRange(output.Warnings);
        return FormatRegistry.Default.OpenBytes(Path.GetFileName(pack), await File.ReadAllBytesAsync(Path.Combine(destination, pack), Token), token: Token);
    }
    private static int Width(ZbdDocument pack, string name) => ((TextureInfo)pack.Assets.Single(a => a.Name == name).Content!).Width;

    [Fact]
    public async Task PacksCountAlphaPlanesAsTheirBudgetsDo()
    {
        using SourceWorldFixture fixture = new();
        fixture.Write("gamegen/build-profiles/small.json", Small);
        // Three translucent 512-texel textures need 1.5 MB at two bytes a texel, so planning names the pack rtexture2; with
        // their alpha planes, counted as every pack's budget counts them, they need 2.25 MB.
        byte[] glass = Png(512, 128);
        for (int i = 0; i < 3; i++) fixture.Write($"data/m1/textures/glass{i}.png", glass);
        var plan = Assert.Single(SourceBuilder.Plan(fixture.Project, null, BuildProfiles.Find(fixture.Project, "small", token: TestContext.Current.CancellationToken), token: TestContext.Current.CancellationToken), p => p.Automatic && p.Path.StartsWith("m1/", StringComparison.Ordinal));
        Assert.Equal("m1/rtexture2.zbd", plan.Path);
        Assert.Empty(plan.Notes);

        List<string> warnings = [];
        var pack = await ExportPack(fixture, "m1/rtexture2.zbd", warnings);
        // The pack keeps its name's promise: one texture is halved, and the export says why.
        Assert.Contains(warnings, w => w.Contains("more than rtexture2.zbd holds; they were reduced to fit", StringComparison.Ordinal) && w.Contains("alpha planes", StringComparison.Ordinal));
        Assert.Contains(Enumerable.Range(0, 3), i => Width(pack, $"glass{i}") < 512);
        long stored = pack.Assets.Where(a => a.Kind == AssetKind.Texture).Sum(a => ((TextureInfo)a.Content!) is var t ? (long)t.Width * t.Height * (a.Name.StartsWith("glass", StringComparison.Ordinal) ? 3 : 2) : 0);
        Assert.InRange(stored, 1, 2L << 20);

        // Seven translucent 256-texel panes take 0.9 MB at two bytes a texel, so their PNG headers need no automatic pack and
        // rtexture1 is the pack that must hold them at full size; with their alpha planes they need 1.3 MB.
        for (int i = 0; i < 3; i++) File.Delete(fixture.Path($"data/m1/textures/glass{i}.png"));
        byte[] pane = Png(256, 128);
        for (int i = 0; i < 7; i++) fixture.Write($"data/m1/textures/pane{i}.png", pane);
        var packs = SourceBuilder.Plan(fixture.Project, null, BuildProfiles.Find(fixture.Project, "small", token: Token), token: Token).Where(p => p.Path.StartsWith("m1/", StringComparison.Ordinal)).ToArray();
        Assert.Equal(["m1/rtexture1.zbd"], packs.Where(p => p.FullSize).Select(p => p.Path));
        Assert.DoesNotContain(packs, p => p.Automatic);
        // Exported alone, it says which textures its budget reduced; an export of every output counts them as their build
        // does and adds the automatic pack that holds them at full size.
        warnings.Clear();
        await ExportPack(fixture, "m1/rtexture1.zbd", warnings);
        Assert.Contains(warnings, w => w.Contains("more than rtexture1.zbd holds; they were reduced to fit: pane0 (256 × 256, stored 128 × 256)", StringComparison.Ordinal));
        string all = Path.Combine(fixture.Root, "all");
        var full = await SourceBuilder.ExportAsync(fixture.Project, all, token: Token, profile: "small");
        Assert.Equal(0, full.Failed);
        Assert.DoesNotContain(full.Outputs.SelectMany(o => o.Warnings), w => w.Contains("reduced to fit", StringComparison.Ordinal));
        var automatic = FormatRegistry.Default.OpenBytes("rtexture2.zbd", await File.ReadAllBytesAsync(Path.Combine(all, "m1", "rtexture2.zbd"), Token), token: Token);
        Assert.All(Enumerable.Range(0, 7), i => Assert.Equal(256, Width(automatic, $"pane{i}")));
    }

    [Fact]
    public async Task TheAutomaticPackCountsTexturesItsWorldBringsFromOtherFolders()
    {
        using SourceWorldFixture fixture = new();
        fixture.Write("gamegen/build-profiles/small.json", Small);
        // m1 also loads m2's tank, whose camouflage only m2's vehicle folder holds; planning counts only m1's folders.
        fixture.Write("gamegen/m1.gs", string.Join("\r\n", "set worldName world", "SetModelDirectory ..\\data\\m1\\models", "SetTextureDirectory ..\\data\\m1\\textures",
            "NewWorld %worldName%", "FindNode %worldName%", "GameGenSetWorld %worldName%", "FindNode %worldName%", "WorldOrigin 0.0 512.0", "WorldExtents 512.0 -512.0", "WorldPartition 256 -256",
            "LoadGameGen m1.flt m1.flt", "DeleteTree m1.flt", "SetModelDirectory ..\\data\\m2\\models\\bft", "LoadGameGen tank.flt tank", "GameZWriteZBDFile ..\\m1\\gamez.zbd", "Quit", ""));
        fixture.Write("data/m2/textures/bft/camo.png", Png(1024, 255));
        byte[] stone = Png(512, 255);
        for (int i = 0; i < 3; i++) fixture.Write($"data/m1/textures/stone{i}.png", stone);
        var plan = Assert.Single(SourceBuilder.Plan(fixture.Project, null, BuildProfiles.Find(fixture.Project, "small", token: TestContext.Current.CancellationToken), token: TestContext.Current.CancellationToken), p => p.Automatic && p.Path.StartsWith("m1/", StringComparison.Ordinal));
        Assert.Equal("m1/rtexture2.zbd", plan.Path);

        List<string> warnings = [];
        var pack = await ExportPack(fixture, "m1/rtexture2.zbd", warnings);
        // 1.5 MB of m1's own textures and the 2 MB camouflage do not fit 2 MB at full size.
        Assert.Contains(pack.Assets, a => a.Name == "camo");
        Assert.Contains(warnings, w => w.Contains("those its world brings from other folders", StringComparison.Ordinal) && w.Contains("more than rtexture2.zbd holds", StringComparison.Ordinal));
        Assert.True(Width(pack, "camo") < 1024 || Enumerable.Range(0, 3).Any(i => Width(pack, $"stone{i}") < 512));
        long stored = pack.Assets.Where(a => a.Kind == AssetKind.Texture).Select(a => (TextureInfo)a.Content!).Sum(t => 2L * t.Width * t.Height);
        Assert.InRange(stored, 1, 2L << 20);
    }

    [Fact]
    public async Task ListingWorldsAndBuildingPreviewsReadNoTextureHeaders()
    {
        using SourceWorldFixture fixture = new();
        fixture.Write("data/m1/textures/stone.png", Png(64, 255));
        string stone = SourceProject.Resolve(fixture.Project, "data/m1/textures/stone.png");
        // The world menu lists missions, and a preview builds a fixed pack: neither names an automatic pack.
        Assert.Equal(["m1", "m2"], SourceWorlds.Missions(fixture.Project, TestContext.Current.CancellationToken));
        Assert.DoesNotContain(SourceBuilder.Plan(fixture.Project, automaticPacks: false, token: TestContext.Current.CancellationToken), p => p.Automatic);
        var build = await SourceWorlds.BuildPreviewAsync(fixture.Project, "m1", Path.Combine(SourceWorlds.PreviewRoot(fixture.Project), "bounds"), token: Token);
        Assert.Contains(build.Outputs, o => o.Path == "m1/rtexture16.zbd" && o.Status == "built");
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

    [Fact]
    public async Task AProfileFileThatCannotBeUsedBlocksOnlyItself()
    {
        // Written for looser rules: the software renderer does not draw textures wider than 1024 texels.
        const string Old = """{ "format": "recoil-build-profile", "version": 1, "texturePacks": [ { "file": "rtexture4.zbd" }, { "file": "texturemax.zbd", "maximumDimension": 2048 } ] }""";
        var files = new Dictionary<string, byte[]> { ["gamegen/build-profiles/old.json"] = Encoding.UTF8.GetBytes(Old) };
        var listed = BuildProfiles.List("unused", f => files[f], files.Keys, token: TestContext.Current.CancellationToken);
        var old = Assert.Single(listed, p => p.Name == "old");
        Assert.Contains("software renderer", old.Error);
        Assert.Empty(old.TexturePacks); Assert.False(old.IsDefault);
        Assert.Equal("modern", Assert.Single(listed, p => p.IsDefault).Name);
        Assert.All(listed.Where(p => p.Name != "old"), p => Assert.Null(p.Error));
        Assert.Same(BuildProfiles.Modern.TexturePacks, BuildProfiles.Find("unused", null, f => files[f], files.Keys, token: TestContext.Current.CancellationToken).TexturePacks);
        Assert.Contains("software renderer", Assert.Throws<InvalidDataException>(() => BuildProfiles.Find("unused", "old", f => files[f], files.Keys, token: TestContext.Current.CancellationToken)).Message);
        // When it may be the default (it says so, or its JSON cannot be read), the default cannot be chosen.
        foreach (string json in new[] { Old.Replace("\"version\": 1,", "\"version\": 1, \"default\": true,"), Old.Replace("\"version\": 1,", "\"version\": 1, \"default\": \"yes\","), "{ \"format\": " })
        {
            files["gamegen/build-profiles/old.json"] = Encoding.UTF8.GetBytes(json);
            Assert.Contains("old.json", Assert.Throws<InvalidDataException>(() => BuildProfiles.List("unused", f => files[f], files.Keys, token: TestContext.Current.CancellationToken)).Message);
        }
        // So is a file replacing the modern profile, the default without a project's own.
        files.Clear(); files["gamegen/build-profiles/modern.json"] = Encoding.UTF8.GetBytes(Old);
        Assert.Contains("software renderer", Assert.Throws<InvalidDataException>(() => BuildProfiles.List("unused", f => files[f], files.Keys, token: TestContext.Current.CancellationToken)).Message);

        // A project's default export and check build while the file is there.
        using SourceWorldFixture fixture = new();
        fixture.Write("gamegen/build-profiles/old.json", Old);
        var check = await SourceBuilder.CheckAsync(fixture.Project, ["m1/rtexture4.zbd"], token: Token);
        Assert.Equal(("modern", 1), (check.Profile, check.Built));
        await Assert.ThrowsAsync<InvalidDataException>(() => SourceBuilder.CheckAsync(fixture.Project, ["m1/rtexture4.zbd"], token: Token, profile: "old"));
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
