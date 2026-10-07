using System.IO;
using System.Text;
using Recoil.Zbd.Core;
using Recoil.Zbd.Core.Export;
using Recoil.Zbd.Core.Formats;
using Recoil.Zbd.Core.Sources;
using Xunit;

namespace Recoil.Zbd.Tests;

/// <summary>Build profiles: which texture packs an export builds, from the built-in profiles or the project's files.</summary>
public sealed class BuildProfileTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;
    private const string Retro = """
        // A profile for 4 MB cards only.
        { "format": "recoil-build-profile", "version": 1, "status": "measured", "default": true,
          "description": "Small cards.",
          "texturePacks": [ { "file": "rtexture4.zbd", "budgetMiB": 4, "maximumDimension": 128 }, { "file": "TEXTURE4.ZBD" } ] }
        """;

    [Fact]
    public void TheBuiltInProfilesMatchTheShippedAndModernPackSets()
    {
        Assert.Equal(["rtexture2.zbd", "rtexture4.zbd", "texture2.zbd", "texture4.zbd", "texture6.zbd", "texture8.zbd"], BuildProfiles.Original.TexturePacks.Select(p => p.File));
        Assert.All(BuildProfiles.Original.TexturePacks.Where(p => p.File.StartsWith("rtexture", StringComparison.Ordinal)), p => Assert.Equal(256, p.MaximumDimension));
        // Each mission gets the packs it shipped with: texture6 in the campaign and m13, texture8 in m6 alone (both releases).
        string[] Packs(string mission) => [.. BuildProfiles.Original.TexturePacks.Where(p => p.Builds(mission)).Select(p => p.File)];
        Assert.Equal(["rtexture2.zbd", "rtexture4.zbd", "texture2.zbd", "texture4.zbd", "texture6.zbd", "texture8.zbd"], Packs("m6"));
        Assert.Equal(["rtexture2.zbd", "rtexture4.zbd", "texture2.zbd", "texture4.zbd", "texture6.zbd"], Packs("m1"));
        Assert.Equal(Packs("m1"), Packs("m13"));
        Assert.Equal(["rtexture2.zbd", "rtexture4.zbd", "texture2.zbd", "texture4.zbd"], Packs("m7"));
        Assert.All(BuildProfiles.Modern.TexturePacks, p => Assert.Null(p.Missions));
        // The modern profile is what exports built before profiles existed.
        Assert.Equal(new[] { "rtexture2.zbd", "rtexture4.zbd", "rtexture8.zbd", "rtexture16.zbd", "rtexture*.zbd", "texture2.zbd", "texture4.zbd", "texture6.zbd", "texture8.zbd", "texturemax.zbd" }.Order(StringComparer.Ordinal), SourceBuilder.TexturePacks.Order(StringComparer.Ordinal));
        foreach (var pack in BuildProfiles.Modern.TexturePacks.Where(p => !p.Automatic))
            Assert.Equal(TexturePackVariant.FromFileName(pack.File), pack.Variant);
        // The automatic pack is a Direct3D pack whose budget is the largest pack it makes.
        var automatic = Assert.Single(BuildProfiles.Modern.TexturePacks, p => p.Automatic);
        Assert.Equal((TexturePackKind.Hardware, (long)BuildProfiles.AutomaticMegabytes << 20, 1024), (automatic.Variant.Kind, automatic.BudgetBytes!.Value, automatic.MaximumDimension));
        Assert.Equal("experimental", BuildProfiles.Modern.Status);
    }

    [Fact]
    public void ProjectProfilesAreValidatedAndOneIsTheDefault()
    {
        var retro = BuildProfiles.Parse("retro", Encoding.UTF8.GetBytes(Retro), "gamegen/build-profiles/retro.json");
        Assert.True(retro.IsDefault);
        Assert.Equal([("rtexture4.zbd", 4L << 20, 128), ("texture4.zbd", 4L << 20, 1024)], retro.TexturePacks.Select(p => (p.File, p.BudgetBytes!.Value, p.MaximumDimension)));
        var files = new Dictionary<string, byte[]> { ["gamegen/build-profiles/retro.json"] = Encoding.UTF8.GetBytes(Retro) };
        var listed = BuildProfiles.List("unused", f => files[f], files.Keys, token: TestContext.Current.CancellationToken);
        Assert.Equal(["modern", "original", "retro"], listed.Select(p => p.Name));
        Assert.Equal("retro", Assert.Single(listed, p => p.IsDefault).Name);
        // Without a project default the modern profile is the default.
        Assert.True(BuildProfiles.List("unused", _ => null, [], token: TestContext.Current.CancellationToken).Single(p => p.Name == "modern").IsDefault);
        // Two defaults, unknown packs, software-only profiles and bad sizes are refused with the file named.
        files["gamegen/build-profiles/second.json"] = Encoding.UTF8.GetBytes(Retro);
        Assert.Contains("Several build profiles", Assert.Throws<InvalidDataException>(() => BuildProfiles.List("unused", f => files[f], files.Keys, token: TestContext.Current.CancellationToken)).Message);
        foreach (string bad in new[]
        {
            Retro.Replace("rtexture4.zbd", "image.zbd"), Retro.Replace("\"rtexture4.zbd\", \"budgetMiB\": 4, \"maximumDimension\": 128", "\"texture8.zbd\""),
            Retro.Replace("128", "100"), Retro.Replace("\"budgetMiB\": 4", "\"budgetMiB\": 0"), Retro.Replace("\"version\": 1", "\"version\": 2"),
            Retro.Replace("TEXTURE4.ZBD", "rtexture4.zbd"), Retro.Replace("measured", "guessed"), "[]",
        })
            Assert.Contains("x.json", Assert.Throws<InvalidDataException>(() => BuildProfiles.Parse("x", Encoding.UTF8.GetBytes(bad), "x.json")).Message);
    }

    [Fact]
    public void ExplicitNullBudgetsKeepFullSizeAndPackNamesAreTheGamesOwn()
    {
        var profile = BuildProfiles.Parse("big", Encoding.UTF8.GetBytes(Retro.Replace("\"budgetMiB\": 4", "\"budgetMiB\": null")), "big.json");
        Assert.Null(profile.TexturePacks[0].BudgetBytes);
        Assert.Null(profile.TexturePacks[0].Variant.BudgetBytes);
        // An omitted budget keeps the one the name implies.
        Assert.Equal(4L << 20, profile.TexturePacks[1].BudgetBytes);
        // rtexture08 is not a name the game opens.
        Assert.Throws<InvalidDataException>(() => BuildProfiles.Parse("x", Encoding.UTF8.GetBytes(Retro.Replace("rtexture4.zbd", "rtexture04.zbd")), "x.json"));
    }

    [Fact]
    public void TheAutomaticPackIsNamedForTheMemoryItsTexturesNeed()
    {
        const long MiB = 1 << 20;
        // Up to the largest fixed pack (rtexture16) nothing is added; above it, N rounds the need up to a power of two.
        Assert.Null(BuildProfiles.AutomaticPack(BuildProfiles.Modern, "m1", 16 * MiB));
        var next = BuildProfiles.AutomaticPack(BuildProfiles.Modern, "m1", 16 * MiB + 1)!;
        // Its budget is what its name holds: the build fits the textures to it only when they need more.
        Assert.Equal(("rtexture32.zbd", TexturePackKind.Hardware, (long?)(32 * MiB), 1024), (next.FileName, next.Kind, next.BudgetBytes, next.MaximumDimension));
        Assert.Equal("rtexture256.zbd", BuildProfiles.AutomaticPack(BuildProfiles.Modern, "m6", 200 * MiB)!.FileName);
        // Beyond the budget the pack is the budget's size and fitted to it.
        var capped = BuildProfiles.AutomaticPack(BuildProfiles.Modern, "m6", 300 * MiB)!;
        Assert.Equal(("rtexture256.zbd", (long?)(256 * MiB)), (capped.FileName, capped.BudgetBytes));
        // A project profile sets its largest size in whole MiB, and limits it to missions like any pack.
        string json = Retro.Replace("{ \"file\": \"TEXTURE4.ZBD\" }", "{ \"file\": \"RTEXTURE*.ZBD\", \"budgetMiB\": 64, \"maximumDimension\": 2048, \"missions\": [\"m6\"] }");
        var profile = BuildProfiles.Parse("auto", Encoding.UTF8.GetBytes(json), "auto.json");
        Assert.Equal("rtexture64.zbd", BuildProfiles.AutomaticPack(profile, "m6", 50 * MiB)!.FileName);
        Assert.Equal(2048, BuildProfiles.AutomaticPack(profile, "m6", 50 * MiB)!.MaximumDimension);
        Assert.Null(BuildProfiles.AutomaticPack(profile, "m1", 50 * MiB));
        Assert.Null(BuildProfiles.AutomaticPack(profile, "m6", 4 * MiB)); // rtexture4 holds it
        foreach (string budget in new[] { "null", "0.5", "2000" })
            Assert.Contains("whole number", Assert.Throws<InvalidDataException>(() => BuildProfiles.Parse("x", Encoding.UTF8.GetBytes(json.Replace("\"budgetMiB\": 64", "\"budgetMiB\": " + budget)), "x.json")).Message);
        Assert.Throws<InvalidDataException>(() => BuildProfiles.Parse("x", Encoding.UTF8.GetBytes(json.Replace("RTEXTURE*", "TEXTURE*")), "x.json"));
        // A profile whose only Direct3D pack is the automatic one is valid, but every mission needs one: without an rtexture
        // pack, Direct3D falls back to the paletted software packs and refuses them.
        string only = json.Replace("{ \"file\": \"rtexture4.zbd\", \"budgetMiB\": 4, \"maximumDimension\": 128 }, ", "");
        BuildProfiles.Parse("only", Encoding.UTF8.GetBytes(only.Replace(", \"missions\": [\"m6\"]", "")), "only.json");
        Assert.Contains("every mission", Assert.Throws<InvalidDataException>(() => BuildProfiles.Parse("only", Encoding.UTF8.GetBytes(only), "only.json")).Message);
        Assert.Contains("every mission", Assert.Throws<InvalidDataException>(() => BuildProfiles.Parse("x", Encoding.UTF8.GetBytes(
            """{ "format": "recoil-build-profile", "version": 1, "texturePacks": [ { "file": "rtexture4.zbd", "missions": ["m6"] }, { "file": "texture4.zbd" } ] }"""), "x.json")).Message);
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
            var variant = BuildProfiles.Modern.TexturePacks.Single(p => p.Automatic).Variant;
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
        // Nine 1024-texel textures need 18 MB at full size, more than rtexture16: the modern profile adds rtexture32 to m1 alone.
        byte[] large = PngEncoder.Encode(new(1024, 1024, new byte[1024 * 1024 * 4]), Token);
        for (int i = 0; i < 9; i++) fixture.Write($"data/m1/textures/large{i}.png", large);
        var plan = SourceBuilder.Plan(fixture.Project, null, BuildProfiles.Modern, token: TestContext.Current.CancellationToken);
        var automatic = Assert.Single(plan, p => p.Path == "m1/rtexture32.zbd");
        Assert.Equal((32L << 20, true), (automatic.Pack!.BudgetBytes!.Value, automatic.Automatic));
        Assert.Equal(["m2/rtexture2.zbd", "m2/rtexture4.zbd", "m2/rtexture8.zbd", "m2/rtexture16.zbd"], plan.Where(p => p.Path.StartsWith("m2/rtexture", StringComparison.Ordinal)).Select(p => p.Path));

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

        // Capped below what they need, the pack takes the cap's name, shrinks them to fit and says so.
        fixture.Write("gamegen/build-profiles/capped.json", Retro.Replace("{ \"file\": \"TEXTURE4.ZBD\" }", "{ \"file\": \"rtexture*.zbd\", \"budgetMiB\": 8 }"));
        var capped = await SourceBuilder.ExportAsync(fixture.Project, Path.Combine(fixture.Root, "capped"), ["m1/rtexture8.zbd"], token: Token, profile: "capped");
        var result = Assert.Single(capped.Outputs);
        // 18 MiB and the fixture's rock texture, rounded up.
        Assert.Contains(result.Warnings, w => w.StartsWith("The m1 textures need 19 MB of texture memory at full size", StringComparison.Ordinal));
        var fitted = FormatRegistry.Default.OpenBytes("rtexture8.zbd", await File.ReadAllBytesAsync(Path.Combine(fixture.Root, "capped", "m1", "rtexture8.zbd"), Token), token: Token);
        Assert.True(fitted.Assets.Where(a => a.Name.StartsWith("large", StringComparison.Ordinal)).All(a => ((TextureInfo)a.Content!).Width < 1024));
    }

    [Fact]
    public void SoftwarePacksStopAt1024Texels()
    {
        // Direct3D packs may ask for what modern devices report; the software renderer skips textures wider than 1024.
        var big = BuildProfiles.Parse("big", Encoding.UTF8.GetBytes(Retro.Replace("128", "4096").Replace("{ \"file\": \"TEXTURE4.ZBD\" }", "{ \"file\": \"texturemax.zbd\", \"maximumDimension\": 1024 }")), "big.json");
        Assert.Equal([4096, 1024], big.TexturePacks.Select(p => p.MaximumDimension));
        foreach (string pack in new[] { "texturemax.zbd", "texture8.zbd" })
        {
            string json = Retro.Replace("{ \"file\": \"TEXTURE4.ZBD\" }", $"{{ \"file\": \"{pack}\", \"maximumDimension\": 2048 }}");
            Assert.Contains("software renderer does not draw wider textures", Assert.Throws<InvalidDataException>(() => BuildProfiles.Parse("x", Encoding.UTF8.GetBytes(json), "x.json")).Message);
        }
        Assert.Equal(TexturePackBuilder.SoftwareMaximumDimension, TexturePackVariant.FromFileName("texturemax.zbd")!.MaximumDimension);
    }

    [Fact]
    public void ANamedProfileIsFoundEvenWhenAnotherFileIsBroken()
    {
        var files = new Dictionary<string, byte[]>
        {
            ["gamegen/build-profiles/retro.json"] = Encoding.UTF8.GetBytes(Retro),
            ["gamegen/build-profiles/broken.json"] = Encoding.UTF8.GetBytes("{ not json"),
            ["gamegen/build-profiles/old/retro.json"] = Encoding.UTF8.GetBytes("ignored: not directly in the folder"),
        };
        Assert.Equal("retro", BuildProfiles.Find("unused", "retro", f => files[f], files.Keys, token: TestContext.Current.CancellationToken).Name);
        Assert.Same(BuildProfiles.Original, BuildProfiles.Find("unused", "original", f => files[f], files.Keys, token: TestContext.Current.CancellationToken));
        // The default needs every file, so the broken one is reported.
        Assert.Contains("broken.json", Assert.Throws<InvalidDataException>(() => BuildProfiles.Find("unused", null, f => files[f], files.Keys, token: TestContext.Current.CancellationToken)).Message);
    }

    [Fact]
    public void APackCanBeLimitedToMissions()
    {
        const string limited = """
            { "format": "recoil-build-profile", "version": 1,
              "texturePacks": [ { "file": "rtexture4.zbd" }, { "file": "texture8.zbd", "missions": [ "m6", "M6", "m13" ] } ] }
            """;
        var profile = BuildProfiles.Parse("limited", Encoding.UTF8.GetBytes(limited), "limited.json");
        Assert.Null(profile.TexturePacks[0].Missions);
        Assert.Equal(["m6", "m13"], profile.TexturePacks[1].Missions!);
        Assert.True(profile.TexturePacks[1].Builds("M6")); Assert.False(profile.TexturePacks[1].Builds("m1"));
        foreach (string bad in new[] { "[]", "[\"six\"]", "[\"m0\"]", "\"m6\"", "[6]" })
        {
            string json = limited.Replace("[ \"m6\", \"M6\", \"m13\" ]", bad, StringComparison.Ordinal);
            var error = Assert.Throws<InvalidDataException>(() => BuildProfiles.Parse("limited", Encoding.UTF8.GetBytes(json), "limited.json"));
            Assert.Contains("missions", error.Message);
        }
    }

    [Fact]
    public async Task ExportsBuildTheChosenProfilesPacksAndWarnAboutLargerPacksLeftBehind()
    {
        using SourceWorldFixture fixture = new();
        fixture.Write("gamegen/build-profiles/retro.json", Retro);
        // The plan follows the profile: the default (the project's retro) builds two packs per mission.
        var plan = SourceBuilder.Plan(fixture.Project, null, BuildProfiles.Find(fixture.Project, null, token: TestContext.Current.CancellationToken), token: TestContext.Current.CancellationToken);
        Assert.Equal(["m1/rtexture4.zbd", "m1/texture4.zbd"], plan.Where(p => p.Family == "textures" && p.Path.StartsWith("m1/", StringComparison.Ordinal)).Select(p => p.Path));
        Assert.Equal(128, plan.First(p => p.Path == "m1/rtexture4.zbd").Pack!.MaximumDimension);
        string destination = Path.Combine(fixture.Root, "game");
        Directory.CreateDirectory(Path.Combine(destination, "m1"));
        await File.WriteAllBytesAsync(Path.Combine(destination, "m1", "rtexture16.zbd"), [1, 2, 3], Token);
        var report = await SourceBuilder.ExportAsync(fixture.Project, destination, ["m1/rtexture4.zbd"], token: Token);
        Assert.Equal("retro", report.Profile);
        Assert.Equal(0, report.Failed);
        Assert.Contains(report.Notes, n => n.StartsWith("m1/rtexture16.zbd", StringComparison.Ordinal));
        // A smaller stale pack can be chosen too (a 2 MB card would open rtexture2).
        await File.WriteAllBytesAsync(Path.Combine(destination, "m1", "rtexture2.zbd"), [1], Token);
        var again = await SourceBuilder.ExportAsync(fixture.Project, destination, ["m1/rtexture4.zbd"], overwrite: true, token: Token);
        Assert.Contains(again.Notes, n => n.StartsWith("m1/rtexture2.zbd", StringComparison.Ordinal));
        // So can a software pack the profile does not build: the software renderer opens it by its option.
        await File.WriteAllBytesAsync(Path.Combine(destination, "m1", "texture8.zbd"), [1], Token);
        var software = await SourceBuilder.ExportAsync(fixture.Project, destination, ["m1/rtexture4.zbd"], overwrite: true, token: Token);
        Assert.Contains(software.Notes, n => n.StartsWith("m1/texture8.zbd", StringComparison.Ordinal));
        Assert.DoesNotContain(software.Notes, n => n.StartsWith("m1/texture4.zbd", StringComparison.Ordinal));
        // Another profile can be chosen by name; an unknown one is refused.
        var original = await SourceBuilder.CheckAsync(fixture.Project, ["m1/rtexture2.zbd"], token: Token, profile: "original");
        Assert.Equal("original", original.Profile);
        // The original profile plans the packs m1 shipped with: texture6, but not m6's texture8.
        var shipped = SourceBuilder.Plan(fixture.Project, null, BuildProfiles.Original, token: TestContext.Current.CancellationToken).Where(p => p.Family == "textures" && p.Path.StartsWith("m1/", StringComparison.Ordinal)).Select(p => p.Path).ToArray();
        Assert.Contains("m1/texture6.zbd", shipped); Assert.DoesNotContain("m1/texture8.zbd", shipped);
        await Assert.ThrowsAsync<InvalidDataException>(() => SourceBuilder.CheckAsync(fixture.Project, null, token: Token, profile: "missing"));
    }
}
