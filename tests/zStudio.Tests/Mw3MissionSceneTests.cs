using System.Text.Json.Nodes;
using Recoil.Zbd.Core;
using Xunit;

namespace Recoil.Zbd.Tests;

[Collection("Allocation-sensitive")]
public sealed class Mw3MissionSceneTests
{
    [Fact]
    public async Task ManyPlacementsPublishOrderedEdgesWithoutQuadraticArrayCopies()
    {
        using var fixture = new Mw3MissionFixture(Enumerable.Range(0, 4096).Select(i => $"actor_{i:0000}").ToArray());
        var scene = fixture.World.Scene!;
        scene.Nodes[0] = scene.Nodes[0] with { Children = [1, 1] };
        long before = GC.GetTotalAllocatedBytes(precise: true);
        var mission = await MissionSceneLoader.LoadAsync(fixture.World, fixture.Resolver, token: TestContext.Current.CancellationToken);
        long allocated = GC.GetTotalAllocatedBytes(precise: true) - before;
        Assert.Equal(4096, mission.Actors.Count);
        Assert.Equal(new[] { 1 }.Concat(mission.Actors.Select(a => a.Root)), mission.Scene.Nodes[0].Children);
        Assert.Equal(new[] { 1, 1 }, scene.Nodes[0].Children);
        Assert.All(mission.Actors, a => Assert.Equal(new[] { 0 }, mission.Scene.Nodes[a.Root].Parents));
        // 4096 repeated Distinct/ToArray publications alone used hundreds of MiB.
        Assert.True(allocated < 96L * 1024 * 1024, $"Placement allocated {allocated} bytes.");
    }

    [Theory]
    [InlineData(float.MaxValue)]
    [InlineData(-float.MaxValue)]
    [InlineData(90)]
    [InlineData(-45)]
    public async Task FiniteHeadingsDoNotOverflowBeforeConversionToRadians(float heading)
    {
        using var fixture = new Mw3MissionFixture(heading, "actor_01");
        var mission = await MissionSceneLoader.LoadAsync(fixture.World, fixture.Resolver, token: TestContext.Current.CancellationToken);
        var actor = Assert.Single(mission.Actors);
        Assert.Equal(heading, actor.PlacementRotation!.Value.Y);
        Assert.True(float.IsFinite(SceneBuilder.LocalTransform(mission.Scene.Nodes[actor.Root]).GetDeterminant()));
        var expected = System.Numerics.Matrix4x4.CreateFromQuaternion(PlacementTransform.Orientation(PlacementRotationKind.HeadingDegrees, actor.PlacementRotation.Value)) *
            System.Numerics.Matrix4x4.CreateTranslation(actor.PlacementPosition!.Value);
        Assert.Equal(expected, SceneBuilder.LocalTransform(mission.Scene.Nodes[actor.Root]));
        Assert.Equal(fixture.ReaderBytes, File.ReadAllBytes(fixture.ReaderPath));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FailedActorCloningDoesNotPublishPartialHierarchyOrConsumeTheNextIdentity(bool missingChild)
    {
        using var fixture = new Mw3MissionFixture("actor_01", "valid_01");
        var original = fixture.World.Scene!;
        original.Nodes[1] = original.Nodes[1] with { Children = [2] };
        original.Nodes.Add(new(2, "cycle", "object3d", null, [1], [missingChild ? 99 : 1], new(), new()));
        original.Nodes.Add(new(3, "valid", "object3d", null, [], [], new(), new()));
        var mission = await MissionSceneLoader.LoadAsync(fixture.World, fixture.Resolver, token: TestContext.Current.CancellationToken);
        var actor = Assert.Single(mission.Actors);
        Assert.Equal(4, actor.Root); Assert.Equal(3, actor.SourceRoot);
        Assert.Equal(5, mission.Scene.Nodes.Count); Assert.Equal(new[] { 0, 1, 2, 3, 3 }, mission.SourceNodes);
        Assert.Equal(new[] { 4 }, mission.Scene.Nodes[0].Children);
        Assert.Contains(mission.Diagnostics, message => message.Contains("AIV record 0") && message.Contains("hierarchy"));
        Assert.Equal(4, original.Nodes.Count); Assert.Empty(original.Nodes[0].Children);
        Assert.Equal(fixture.ReaderBytes, File.ReadAllBytes(fixture.ReaderPath));
    }

    [Theory]
    [InlineData(126)]
    [InlineData(127)]
    [InlineData(128)]
    [InlineData(129)]
    [InlineData(1_048_576)]
    public async Task ActorLabelsAreBoundedWithoutChangingSourcesOrPlacementIdentity(int length)
    {
        string name = "actor_1" + new string('é', length - 7);
        using var fixture = new Mw3MissionFixture(name + "a", name + "b");
        var token = TestContext.Current.CancellationToken;
        var mission = await MissionSceneLoader.LoadAsync(fixture.World, fixture.Resolver, token: token);
        Assert.Equal(2, mission.Actors.Count);
        foreach (var actor in mission.Actors)
        {
            var node = mission.Scene.Nodes[actor.Root];
            Assert.InRange(actor.Name.Length, 1, 128); Assert.Equal(actor.Name, node.Name);
            var json = System.Text.Json.JsonSerializer.SerializeToNode(actor)!;
            Assert.Equal(length + 1, json["NameCharacters"]!.GetValue<int>());
            Assert.Equal(length + 1 > 128, json["NameTruncated"]!.GetValue<bool>());
            Assert.Equal(length + 1, node.Metadata["name_characters"]!.GetValue<int>());
            Assert.Equal(length + 1 > 128, node.Metadata["name_truncated"]!.GetValue<bool>());
            Assert.Equal(actor.PlacementPosition, SceneBuilder.LocalTransform(node).Translation);
        }
        Assert.NotEqual(mission.Actors[0].Root, mission.Actors[1].Root);
        Assert.NotEqual(mission.Actors[0].CoordinateSource, mission.Actors[1].CoordinateSource);
        var again = await MissionSceneLoader.LoadAsync(fixture.World, fixture.Resolver, token: token);
        Assert.All(mission.Actors, actor => Assert.Equal(actor.Root, again.RemapNodeFrom(mission, actor.Root)));
        Assert.Equal("actor", fixture.World.Scene!.Nodes[1].Name);
        Assert.Equal(fixture.ReaderBytes, await File.ReadAllBytesAsync(fixture.ReaderPath, token));
        var archive = await fixture.Resolver.OpenCachedAsync(fixture.ReaderPath, token);
        var aiv = Assert.IsType<Recoil.Zbd.Core.Formats.ZrdNode>(Assert.Single(archive.Assets).Content);
        Assert.Equal(name + "a", aiv.Children[0].Text); Assert.Equal(name + "b", aiv.Children[2].Text);
    }

    [Fact]
    public async Task MissingActorDiagnosticsDoNotCopyTheFullAuthoredName()
    {
        string name = "missing_1" + new string('x', 1_048_576);
        using var fixture = new Mw3MissionFixture(name);
        var mission = await MissionSceneLoader.LoadAsync(fixture.World, fixture.Resolver, token: TestContext.Current.CancellationToken);
        Assert.Empty(mission.Actors);
        Assert.All(mission.Diagnostics, message => Assert.InRange(message.Length, 1, 512));
        Assert.Contains(mission.Diagnostics, message => message.Contains("AIV record 0") && message.Contains("truncated"));
        Assert.Equal(fixture.ReaderBytes, File.ReadAllBytes(fixture.ReaderPath));
    }

    [Fact]
    public async Task TemplateLookupRetainsOrdinalMatchingAndRejectsDuplicateNames()
    {
        using var fixture = new Mw3MissionFixture("Actor_01", "actor_01", "actor_extra_01", "duplicate_01");
        var scene = fixture.World.Scene!;
        scene.Nodes.Add(new(2, "duplicate", "object3d", null, [], [], new(), new()));
        scene.Nodes.Add(new(3, "duplicate", "object3d", null, [], [], new(), new()));
        var mission = await MissionSceneLoader.LoadAsync(fixture.World, fixture.Resolver, token: TestContext.Current.CancellationToken);
        var actor = Assert.Single(mission.Actors);
        Assert.Equal("actor_01", actor.Name); Assert.Equal(1, actor.CoordinateSource!.RecordIndex);
        Assert.Equal(4, mission.Diagnostics.Count); Assert.Equal(5, mission.Scene.Nodes.Count);
        Assert.Equal(fixture.ReaderBytes, File.ReadAllBytes(fixture.ReaderPath));
    }
}
