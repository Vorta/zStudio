using System.IO;
using zStudio.Core;
using zStudio.Core.Sources;
using Xunit;

namespace zStudio.Tests;

/// <summary>Source evaluation freezes source content without producing game archives.</summary>
public sealed class PreviewBuildRound6Tests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Theory]
    [InlineData("data/m1/models/m1.gltf")]
    [InlineData("gamegen/m1.gs")]
    public void AnEvaluatedInputChangedBeforeAcceptanceIsRefused(string relative)
    {
        using SourceWorldFixture fixture = new();
        SourceBuilder.Snapshot snapshot = new(fixture.Project,
            changed: path => new SourceFileChangedException("Source changed: " + path, [path]));
        snapshot.World("m1", Token);
        string path = fixture.Path(relative);
        byte[] bytes = File.ReadAllBytes(path);
        DateTime stamp = File.GetLastWriteTimeUtc(path);
        // Preserve timestamp and size: content, not metadata alone, defines the accepted revision.
        int offset = Array.IndexOf(bytes, (byte)' ');
        Assert.True(offset >= 0);
        bytes[offset] = (byte)'\t';
        File.WriteAllBytes(path, bytes); File.SetLastWriteTimeUtc(path, stamp);
        var refused = Assert.Throws<SourceFileChangedException>(() => snapshot.CheckUnchanged(Token));
        Assert.Equal([relative], refused.Files);
        Assert.Empty(Directory.EnumerateFiles(fixture.Project, "*.zbd", SearchOption.AllDirectories));
    }

    [Fact]
    public async Task SourceTexturesDoNotDependOnTheExportProfile()
    {
        using SourceWorldFixture fixture = new();
        fixture.Write("gamegen/build-profiles/small.json", """{ "format": "recoil-build-profile", "version": 1, "default": true, "texturePacks": [ { "file": "rtexture4.zbd" } ] }""");
        Assert.Equal("small", BuildProfiles.Find(fixture.Project, null, token: Token).Name);
        var scene = await SourceWorlds.LoadSceneAsync(fixture.Project, "m1", token: Token);
        Assert.Contains("rock", scene.Textures.Keys);
        Assert.Equal(File.ReadAllBytes(fixture.Path(scene.Textures["rock"].Path)), scene.Textures["rock"].Bytes);
        Assert.Empty(Directory.EnumerateFiles(fixture.Project, "*.zbd", SearchOption.AllDirectories));
        Assert.False(Directory.Exists(Path.Combine(fixture.Project, ".zstudio", "cache", "worlds")));
    }
}
