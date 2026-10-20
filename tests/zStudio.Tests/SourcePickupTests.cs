using System.Text;
using zStudio.Core;
using zStudio.Core.Sources;
using Xunit;

namespace zStudio.Tests;

public sealed class SourcePickupTests
{
    [Theory]
    [InlineData("All", 1, 1, 1)]
    [InlineData("Medium", 0, 1, 0)]
    [InlineData("Hard", 0, 0, 1)]
    public async Task AppendPreservesSourceAndIsolatesFallbacks(string difficulty, int easy, int medium, int hard)
    {
        using SourceWorldFixture fixture = new();
        fixture.WritePickupTemplate();
        const string original = "# Keep this comment\r\n(\r\n  ( NANITE 50 ( 1.0 2.0 3.0 ) ( 0.0 0.0 0.0 ) 0.0 ) # keep this too\r\n)\r\n";
        fixture.Write("data/m1/zrdr/puppies.zrd", original);
        var token = TestContext.Current.CancellationToken;
        var workspace = new SourceWorkspace(fixture.Project);
        var before = await SourceWorlds.LoadSceneAsync(fixture.Project, "m1", token: token);
        SourcePickupAddition addition = new("PUP_AMPHIB", new(10, 2, 20), difficulty, new(0, 90, 0));
        var added = SourcePickupEdits.Add(workspace, before, addition, token);
        var snapshot = await SourceWorlds.LoadSceneAsync(fixture.Project, "m1", workspace.Overlay(), token);
        SourcePickupEdits.Verify(snapshot, added, addition, token);
        foreach (var (layout, expected) in new[] { (MissionDifficulty.Easy, easy), (MissionDifficulty.Medium, medium), (MissionDifficulty.Hard, hard) })
        {
            string requested = MissionLayoutSelection.For(layout).PickupResource;
            var resource = snapshot.Resources.GetValueOrDefault(requested) ?? snapshot.Resources["puppies.zrd"];
            Assert.Equal(1 + expected, resource.Root!.Children.Single().Children.Count);
            string text = Encoding.Latin1.GetString(resource.Bytes);
            Assert.Contains("# Keep this comment\r\n", text);
            Assert.Contains("( NANITE 50 ( 1.0 2.0 3.0 ) ( 0.0 0.0 0.0 ) 0.0 ) # keep this too", text);
            if (expected == 1)
            {
                var pickup = snapshot.MissionScene(layout, token).Actors.Single(a => a.Pickup?.LogicalName == "PUP_AMPHIB").Pickup!;
                Assert.Equal(1, pickup.AuthoredAmount); Assert.Equal(MathF.PI / 2, pickup.Rotation.Y);
            }
        }
        Assert.Single(workspace.History);
        workspace.Undo();
        Assert.False(workspace.IsDirty);
        Assert.Equal(original, Encoding.Latin1.GetString(workspace.Read("data/m1/zrdr/puppies.zrd", token)!));
        Assert.False(workspace.Exists("data/m1/zrdr/puppies_hard.zrd", token));
        workspace.Redo(); await workspace.SaveAsync(token);
        var reopened = await SourceWorlds.LoadSceneAsync(fixture.Project, "m1", token: token);
        SourcePickupEdits.Verify(reopened, added, addition, token);
    }
}
