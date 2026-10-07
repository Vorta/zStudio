using System.Text;
using Recoil.Zbd.Core;
using Recoil.Zbd.Core.Export;
using Recoil.Zbd.Core.Formats;
using Recoil.Zbd.Core.Sources;
using Xunit;

namespace Recoil.Zbd.Tests;

public sealed class ImportRound22SourceTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;
    private const string Load = "gamegen/support/loadm1.gw";
    private const string Multiplayer = "source support\\bftmulti.gw\n";
    private const string SinglePlayer = "# single player\n";

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PendingMissionModeAndSavedBytesSelectTheSameTextures(bool removeMultiplayer)
    {
        using SourceWorldFixture fixture = new();
        fixture.Write(Load, removeMultiplayer ? Multiplayer : SinglePlayer);
        fixture.Write("gamegen/support/bftmulti.gw", "# vehicle setup\n");
        fixture.Write("data/common/multi_bft/textures/multi.png", PngEncoder.Encode(new DecodedImage(1, 1, [0, 255, 0, 255]), Token));
        byte[] pending = Encoding.ASCII.GetBytes(removeMultiplayer ? SinglePlayer : Multiplayer);
        Dictionary<string, byte[]> overlay = new(StringComparer.OrdinalIgnoreCase) { [Load] = pending };
        var preview = await SourceWorlds.BuildPreviewAsync(fixture.Project, "m1", Path.Combine(SourceWorlds.PreviewRoot(fixture.Project), "pending"), overlay, token: Token);
        string[] pendingNames = Names(preview.Folder);
        fixture.Write(Load, pending);
        var saved = await SourceWorlds.BuildPreviewAsync(fixture.Project, "m1", Path.Combine(SourceWorlds.PreviewRoot(fixture.Project), "saved"), token: Token);
        Assert.Equal(Names(saved.Folder), pendingNames);
        Assert.Equal(!removeMultiplayer, pendingNames.Contains("multi"));
        Assert.Contains("rock", pendingNames);

        string[] Names(string folder) => FormatRegistry.Default.OpenBytes("rtexture16.zbd", File.ReadAllBytes(Path.Combine(folder, "m1", "rtexture16.zbd")), token: Token)
            .Assets.Select(a => a.Name).OrderBy(n => n, StringComparer.Ordinal).ToArray();
    }

    [Fact]
    public async Task PreviewPlanningDoesNotProbeUnrelatedMissingMissionLoadScripts()
    {
        using SourceWorldFixture fixture = new();
        fixture.Write(Load, SinglePlayer);
        var preview = await SourceWorlds.BuildPreviewAsync(fixture.Project, "m1", Path.Combine(SourceWorlds.PreviewRoot(fixture.Project), "scope"), token: Token);
        Assert.Contains(Load, preview.Dependencies);
        Assert.DoesNotContain("gamegen/support/loadm2.gw", preview.Dependencies);
        Assert.DoesNotContain("gamegen/support/loadm2.gw", preview.InputHashes.Keys);
    }

    [Fact]
    public void PlanningTracksMissingLoadScriptsAndSameMetadataContentChanges()
    {
        using SourceWorldFixture fixture = new();
        SourceBuilder.Snapshot missing = new(fixture.Project);
        SourceBuilder.Plan(fixture.Project, missing.Added, null, false, Token, missing);
        Assert.Contains(Load, missing.Dependencies());
        fixture.Write(Load, SinglePlayer);
        Assert.Throws<InvalidDataException>(() => missing.CheckUnchanged(Token));

        SourceBuilder.Snapshot existing = new(fixture.Project);
        SourceBuilder.Plan(fixture.Project, existing.Added, null, false, Token, existing);
        DateTime stamp = File.GetLastWriteTimeUtc(fixture.Path(Load));
        fixture.Write(Load, SinglePlayer.Replace("single", "edited", StringComparison.Ordinal));
        File.SetLastWriteTimeUtc(fixture.Path(Load), stamp);
        Assert.Throws<InvalidDataException>(() => SourceBuilder.Plan(fixture.Project, existing.Added, null, false, Token, existing));
    }
}
