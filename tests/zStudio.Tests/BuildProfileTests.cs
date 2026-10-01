using System.IO;
using System.Text;
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
        Assert.Equal(["rtexture2.zbd", "rtexture4.zbd", "texture2.zbd", "texture4.zbd", "texture6.zbd"], BuildProfiles.Original.TexturePacks.Select(p => p.File));
        Assert.All(BuildProfiles.Original.TexturePacks.Where(p => p.File.StartsWith("rtexture", StringComparison.Ordinal)), p => Assert.Equal(256, p.MaximumDimension));
        // The modern profile is what exports built before profiles existed.
        Assert.Equal(new[] { "rtexture2.zbd", "rtexture4.zbd", "rtexture8.zbd", "rtexture16.zbd", "texture2.zbd", "texture4.zbd", "texture6.zbd", "texture8.zbd", "texturemax.zbd" }.Order(StringComparer.Ordinal), SourceBuilder.TexturePacks.Order(StringComparer.Ordinal));
        foreach (var pack in BuildProfiles.Modern.TexturePacks)
            Assert.Equal(TexturePackVariant.FromFileName(pack.File), pack.Variant);
        Assert.Equal("experimental", BuildProfiles.Modern.Status);
    }

    [Fact]
    public void ProjectProfilesAreValidatedAndOneIsTheDefault()
    {
        var retro = BuildProfiles.Parse("retro", Encoding.UTF8.GetBytes(Retro), "gamegen/build-profiles/retro.json");
        Assert.True(retro.IsDefault);
        Assert.Equal([("rtexture4.zbd", 4L << 20, 128), ("texture4.zbd", 4L << 20, 1024)], retro.TexturePacks.Select(p => (p.File, p.BudgetBytes!.Value, p.MaximumDimension)));
        var files = new Dictionary<string, byte[]> { ["gamegen/build-profiles/retro.json"] = Encoding.UTF8.GetBytes(Retro) };
        var listed = BuildProfiles.List("unused", f => files[f], files.Keys);
        Assert.Equal(["modern", "original", "retro"], listed.Select(p => p.Name));
        Assert.Equal("retro", Assert.Single(listed, p => p.IsDefault).Name);
        // Without a project default the modern profile is the default.
        Assert.True(BuildProfiles.List("unused", _ => null, []).Single(p => p.Name == "modern").IsDefault);
        // Two defaults, unknown packs, software-only profiles and bad sizes are refused with the file named.
        files["gamegen/build-profiles/second.json"] = Encoding.UTF8.GetBytes(Retro);
        Assert.Contains("Several build profiles", Assert.Throws<InvalidDataException>(() => BuildProfiles.List("unused", f => files[f], files.Keys)).Message);
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
    public void ANamedProfileIsFoundEvenWhenAnotherFileIsBroken()
    {
        var files = new Dictionary<string, byte[]>
        {
            ["gamegen/build-profiles/retro.json"] = Encoding.UTF8.GetBytes(Retro),
            ["gamegen/build-profiles/broken.json"] = Encoding.UTF8.GetBytes("{ not json"),
            ["gamegen/build-profiles/old/retro.json"] = Encoding.UTF8.GetBytes("ignored: not directly in the folder"),
        };
        Assert.Equal("retro", BuildProfiles.Find("unused", "retro", f => files[f], files.Keys).Name);
        Assert.Same(BuildProfiles.Original, BuildProfiles.Find("unused", "original", f => files[f], files.Keys));
        // The default needs every file, so the broken one is reported.
        Assert.Contains("broken.json", Assert.Throws<InvalidDataException>(() => BuildProfiles.Find("unused", null, f => files[f], files.Keys)).Message);
    }

    [Fact]
    public async Task ExportsBuildTheChosenProfilesPacksAndWarnAboutLargerPacksLeftBehind()
    {
        using SourceWorldFixture fixture = new();
        fixture.Write("gamegen/build-profiles/retro.json", Retro);
        // The plan follows the profile: the default (the project's retro) builds two packs per mission.
        var plan = SourceBuilder.Plan(fixture.Project, null, BuildProfiles.Find(fixture.Project, null));
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
        // Another profile can be chosen by name; an unknown one is refused.
        var original = await SourceBuilder.CheckAsync(fixture.Project, ["m1/rtexture2.zbd"], token: Token, profile: "original");
        Assert.Equal("original", original.Profile);
        await Assert.ThrowsAsync<InvalidDataException>(() => SourceBuilder.CheckAsync(fixture.Project, null, token: Token, profile: "missing"));
    }
}
