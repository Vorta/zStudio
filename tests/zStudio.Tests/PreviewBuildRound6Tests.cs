using System.IO;
using Recoil.Zbd.Core;
using Recoil.Zbd.Core.Sources;
using Xunit;

namespace Recoil.Zbd.Tests;

/// <summary>
/// A world preview is one state of the project, as an export is: a source another program changes after the build read it, or
/// adds after the build planned its outputs, fails the build rather than show a world assembled from two states, and the stamps a
/// shown build gives are those verified. Its texture pack is the same whatever profile the project's exports use.
/// </summary>
public sealed class PreviewBuildRound6Tests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;
    private sealed class OnReport(Action<SourceProgress> action) : IProgress<SourceProgress> { public void Report(SourceProgress value) => action(value); }
    private const string Model = "data/m1/models/m1.gltf";
    private static string Folder(SourceWorldFixture fixture) => Path.Combine(SourceWorlds.PreviewRoot(fixture.Project), Guid.NewGuid().ToString("N"));
    private static void Touch(string path, int minutes) => File.SetLastWriteTimeUtc(path, File.GetLastWriteTimeUtc(path).AddMinutes(minutes));

    [Fact]
    public async Task AModelChangedAfterTheWorldReadItFailsThePreview()
    {
        using SourceWorldFixture fixture = new();
        string model = fixture.Path(Model);
        // The world read the model; another program saves it while the animations build.
        bool changed = false;
        OnReport progress = new(p => { if (p.Item == "m1/anim.zbd" && !changed) { changed = true; Touch(model, 1); } });

        var refused = await Assert.ThrowsAsync<SourceFileChangedException>(() => SourceWorlds.BuildPreviewAsync(fixture.Project, "m1", Folder(fixture), progress: progress, token: Token));
        Assert.True(changed);
        Assert.Equal([Model], refused.Files);
        Assert.StartsWith($"{Model} changed on disk while the m1 world was building; the build was not shown.", refused.Message, StringComparison.Ordinal);

        // Built again, the build shows the model as it is now, and says so in its stamps.
        var build = await SourceWorlds.BuildPreviewAsync(fixture.Project, "m1", Folder(fixture), token: Token);
        Assert.Equal(FileStamp.Read(model), build.Inputs[Model]);
    }

    [Fact]
    public async Task AModelChangedAfterPlanningButBeforeItWasReadIsOneStateOfTheProject()
    {
        using SourceWorldFixture fixture = new();
        string model = fixture.Path(Model);
        // Planning lists the model but does not read it: a change before the world reads it is the state the world shows.
        bool changed = false;
        OnReport progress = new(p => { if (p.Completed == 0 && !changed) { changed = true; Touch(model, 1); } });

        var build = await SourceWorlds.BuildPreviewAsync(fixture.Project, "m1", Folder(fixture), progress: progress, token: Token);
        Assert.True(changed);
        Assert.Equal(FileStamp.Read(model), build.Inputs[Model]);
        Assert.All(build.Outputs, o => Assert.Equal("built", o.Status));
    }

    [Fact]
    public async Task AModelRemovedAfterPlanningFailsThePreviewAsAChangeNotABrokenWorld()
    {
        using SourceWorldFixture fixture = new();
        const string Buffer = "data/m1/models/m1.bin";
        string buffer = fixture.Path(Buffer), moved = buffer + ".moved", model = fixture.Path(Model);
        // Another program renames the buffer the model names after planning listed it and before the world reads it, so the world
        // fails to read the model.
        bool changed = false;
        OnReport progress = new(p => { if (p.Completed == 0 && !changed) { changed = true; File.Move(buffer, moved); } });

        var refused = await Assert.ThrowsAsync<SourceFileChangedException>(() => SourceWorlds.BuildPreviewAsync(fixture.Project, "m1", Folder(fixture), progress: progress, token: Token));
        Assert.True(changed);
        Assert.Contains(Buffer, refused.Message, StringComparison.Ordinal);
        Assert.StartsWith("Files of the project were added, removed or renamed while the m1 world was building", refused.Message, StringComparison.Ordinal);

        // A world that fails with the project as planned is still a world that does not build.
        File.Move(moved, buffer);
        File.WriteAllText(model, "{");
        var broken = await Assert.ThrowsAsync<InvalidDataException>(() => SourceWorlds.BuildPreviewAsync(fixture.Project, "m1", Folder(fixture), token: Token));
        Assert.StartsWith("The m1 world does not build: ", broken.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AScriptThatChangesWhileItIsReadAgainFailsThePreviewRatherThanOneOutput()
    {
        using SourceWorldFixture fixture = new();
        const string Script = "gamegen/m1.gs";
        string script = fixture.Path(Script);
        // The world reads its script, and the texture pack reads every script again for its damage masks: a save in between,
        // undone before the scripts are packed, is a change the pack read, however the files look when the build ends.
        int step = 0;
        OnReport progress = new(p =>
        {
            if (p.Item == "m1/rtexture16.zbd" && step == 0) { step = 1; Touch(script, 1); }
            else if (step == 1) { step = 2; Touch(script, -1); }
        });

        var refused = await Assert.ThrowsAsync<SourceFileChangedException>(() => SourceWorlds.BuildPreviewAsync(fixture.Project, "m1", Folder(fixture), progress: progress, token: Token));
        // The pack's read failed the build at once, before the save was undone.
        Assert.Equal(1, step);
        Assert.Equal([Script], refused.Files);
        Assert.Contains("changed on disk while the m1 world was building", refused.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task AResourceAddedAfterPlanningFailsThePreviewRatherThanBeingLeftOut(bool existing)
    {
        using SourceWorldFixture fixture = new();
        // With an archive already planned, the archive's list changes; without one, the mission gains an archive.
        if (existing) fixture.Write("data/m1/zrdr/ai.zrd", "( GRAVITY ( -9.8 ) )\n");
        bool added = false;
        OnReport progress = new(_ => { if (!added) { added = true; fixture.Write("data/m1/zrdr/late.zrd", "( LATE ( 1 ) )\n"); } });

        var refused = await Assert.ThrowsAsync<SourceFileChangedException>(() => SourceWorlds.BuildPreviewAsync(fixture.Project, "m1", Folder(fixture), progress: progress, token: Token));
        Assert.True(added);
        Assert.Equal(["data/m1/zrdr/late.zrd"], refused.Files);
        Assert.Contains("added, removed or renamed while the m1 world was building (data/m1/zrdr/late.zrd); the build was not shown.", refused.Message, StringComparison.Ordinal);

        // Built again, the mission's archive holds it.
        var build = await SourceWorlds.BuildPreviewAsync(fixture.Project, "m1", Folder(fixture), token: Token);
        Assert.Equal(existing ? 2 : 1, build.Outputs.Single(o => o.Path == "m1/zrdr.zbd").Items);
    }

    [Fact]
    public async Task ThePreviewPackDoesNotDependOnTheProjectsDefaultProfile()
    {
        using SourceWorldFixture fixture = new();
        // The project's exports build only rtexture4 by default.
        fixture.Write("gamegen/build-profiles/small.json", """{ "format": "recoil-build-profile", "version": 1, "default": true, "texturePacks": [ { "file": "rtexture4.zbd" } ] }""");
        var profile = BuildProfiles.Find(fixture.Project, null, token: TestContext.Current.CancellationToken);
        Assert.Equal("small", profile.Name);
        Assert.DoesNotContain(SourceBuilder.Plan(fixture.Project, null, profile, token: Token), p => p.Path == "m1/rtexture16.zbd");

        var build = await SourceWorlds.BuildPreviewAsync(fixture.Project, "m1", Folder(fixture), token: Token);
        var pack = Assert.Single(build.Outputs, o => o.Family == "textures");
        Assert.Equal(("m1/rtexture16.zbd", "built"), (pack.Path, pack.Status));
        // The pack the modern profile exports, byte for byte.
        string destination = Path.Combine(fixture.Root, "game");
        await SourceBuilder.ExportAsync(fixture.Project, destination, ["m1/rtexture16.zbd"], token: Token, profile: "modern");
        Assert.Equal(await File.ReadAllBytesAsync(Path.Combine(destination, "m1", "rtexture16.zbd"), Token), await File.ReadAllBytesAsync(SourceProject.Resolve(build.Folder, "m1/rtexture16.zbd"), Token));
    }
}
