using System.Numerics;
using System.Text.Json.Nodes;
using zStudio.Core;
using zStudio.Core.Animation;
using Xunit;

namespace zStudio.Tests;

public sealed class VtolDropWindowsTests
{
    [Fact]
    public void WholeWindowMovePlansUseTheExactModelLessRoot()
    {
        var token = TestContext.Current.CancellationToken;
        var package = VtolDropFixture.Compile(VtolDropFixture.Files(), token).Package;
        var discovery = VtolDropWindows.Discover(package, token);
        var candidate = Assert.Single(discovery.Candidates);
        Assert.Empty(discovery.Diagnostics);
        var context = Context(package);
        byte[] original = AnimationWriter.Write(package, token);
        // The callback's PLAYER_RANGE follows the scenario, never a forced value: this fixture has no mission, so no player and
        // no release; a player near the flight receives every drop.
        Assert.Empty(VtolDropWindows.Analyze(context, candidate, token).Releases);
        var scenario = new AnimationExecutionPolicy(rules: new() { Player = AnimationPlayerReference.Custom(new(100, 0, 0)) });
        var snapshot = VtolDropWindows.Analyze(context, candidate, token, scenario);
        Assert.Same(scenario, snapshot.Scenario);
        Assert.InRange(snapshot.CycleSeconds, 10, 10.1);
        Assert.Equal(2, snapshot.RootNode);
        Assert.Equal(2, snapshot.Windows.Count);
        Assert.NotEmpty(snapshot.Releases);
        Assert.All(snapshot.Releases, r => { Assert.True(r.Conditional); Assert.InRange(r.Position.X, 100, 111); });
        Assert.Equal(original, AnimationWriter.Write(package, token));
        Assert.Throws<InvalidDataException>(() => VtolDropWindows.PlanMove(snapshot, snapshot.Windows[0].Id, 4));
        Assert.Throws<InvalidDataException>(() => VtolDropWindows.PlanMove(snapshot, snapshot.Windows[1].Id, 9));
        var plan = VtolDropWindows.PlanMove(snapshot, snapshot.Windows[0].Id, 2);
        var preview = VtolDropWindows.PreviewMove(context, snapshot, plan, token);
        Assert.Equal(2, preview.Windows[0].Start);
        Assert.Equal(snapshot.Windows[0].Duration, preview.Windows[0].Duration);
        Assert.Equal(original, AnimationWriter.Write(package, token));

        // The curve revisits the same origin. Segment identity selects its later phase rather than the nearest XYZ.
        var late = VtolDropWindows.PlanMove(snapshot, snapshot.Windows[1].Id, 360, 0);
        Assert.InRange(late.NewStart, 5.99f, 6.01f);
        using CancellationTokenSource canceled = new(); canceled.Cancel();
        Assert.Throws<OperationCanceledException>(() => VtolDropWindows.Analyze(context, candidate, canceled.Token));

        // Retail m8 has an already-due call behind a relative stop. Keep it inspectable, plan a no-op, and
        // refuse independently moving the predecessor whose stop determines the neighbor's dispatch.
        var overlapFiles = VtolDropFixture.Files();
        overlapFiles.Content[VtolDropFixture.Root] = System.Text.Encoding.Latin1.GetBytes(VtolDropFixture.Definitions()
            .Replace("EVENT_OFFSET 2", "EVENT_OFFSET 4", StringComparison.Ordinal));
        var overlapPackage = VtolDropFixture.Compile(overlapFiles, token).Package;
        var overlapCandidate = Assert.Single(VtolDropWindows.Discover(overlapPackage, token).Candidates);
        var overlap = VtolDropWindows.Analyze(Context(overlapPackage), overlapCandidate, token);
        Assert.NotNull(overlap.Windows[0].MoveRefusal);
        Assert.True(overlap.Windows[1].EffectiveStart > overlap.Windows[1].Start);
        VtolDropWindows.PlanMove(overlap, overlap.Windows[0].Id, overlap.Windows[0].Start);
        Assert.Throws<InvalidDataException>(() => VtolDropWindows.PlanMove(overlap, overlap.Windows[0].Id, .5f));
    }

    internal static AnimationPreviewContext Context(AnimationPackage package)
    {
        var world = new AssetDocument("fixture", new(0, DateTime.MinValue), new(FormatFamily.GameZ, 15, Recognition.Supported, "fixture"), ReadOnlyMemory<byte>.Empty) { Scene = new() };
        world.Scene.Nodes.Add(new(0, "world", "world", -1, [], [1], new JsonObject(), Data(0)));
        world.Scene.Nodes.Add(new(1, "parent", "object3d", -1, [0], [2], new JsonObject { ["flags"] = 4 }, Data(100)));
        world.Scene.Nodes.Add(new(2, "ground", "object3d", -1, [1], [], new JsonObject { ["flags"] = 4 }, Data(0)));
        return new() { Package = package, World = world };
        static JsonObject Data(float x) => new() { ["flags"] = 0, ["opacity"] = 1f, ["scale"] = JsonData.Vector(Vector3.One),
            ["transform"] = new JsonArray(1, 0, 0, 0, 1, 0, 0, 0, 1, x, 0, 0) };
    }
}
