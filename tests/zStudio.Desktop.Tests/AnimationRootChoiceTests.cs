using System.Numerics;
using Recoil.Zbd.Core;
using Recoil.Zbd.Desktop;
using Xunit;

namespace Recoil.Zbd.Desktop.Tests;

public sealed class AnimationRootChoiceTests
{
    [Fact]
    public void DuplicateActorsAreIndexedOnceAndFirstOccurrenceIsDescribed()
    {
        GameScene scene = Scene(12); int visits = 0, transforms = 0;
        IEnumerable<MissionActor> Actors()
        {
            for (int i = 0; i < 40; i++) { visits++; yield return new(5, 5, "same", i == 0 ? "first/source" : "later/source"); }
        }
        var choices = new AnimationRootChoices(scene, Actors(), _ => { transforms++; return Matrix4x4.CreateTranslation(1, 2, 3); }, TestContext.Current.CancellationToken);
        var rows = choices.Page("", TestContext.Current.CancellationToken);
        Assert.Equal(40, visits); Assert.Equal(12, rows.Count); Assert.Equal(1, transforms);
        Assert.Contains("first/source", rows[5].Label); Assert.DoesNotContain("later/source", rows[5].Label);
        Assert.Equal(5, Assert.Single(choices.Page("FIRST/SOURCE", TestContext.Current.CancellationToken)).Index);
        Assert.Equal(40, visits); Assert.Equal(2, transforms);
    }
    [Fact]
    public void PageStopsAfter500MatchesAndPreservesSceneOrderAndFullLabels()
    {
        GameScene scene = Scene(503); string name = "a_" + new string('z', 700); scene.Nodes[499] = scene.Nodes[499] with { Name = name };
        var choices = new AnimationRootChoices(scene, [new(502, 502, "late", "late/source")], _ => throw new InvalidOperationException("Off-page actor must not be projected."), TestContext.Current.CancellationToken);
        var page = choices.Page("", TestContext.Current.CancellationToken);
        Assert.Equal(Enumerable.Range(0, 500), page.Select(p => p.Index)); Assert.Contains(name, page[499].Label);
        var searchable = new AnimationRootChoices(scene, [new(502, 502, "late", "late/source")], _ => Matrix4x4.Identity, TestContext.Current.CancellationToken);
        Assert.Equal(499, Assert.Single(searchable.Page(name, TestContext.Current.CancellationToken)).Index);
    }
    [Fact]
    public void CanceledIndexAndPageDoNotPreventFreshRetry()
    {
        using var canceled = new CancellationTokenSource(); canceled.Cancel(); var scene = Scene(3);
        Assert.ThrowsAny<OperationCanceledException>(() => new AnimationRootChoices(scene, [], _ => Matrix4x4.Identity, canceled.Token));
        var choices = new AnimationRootChoices(scene, [], _ => Matrix4x4.Identity, TestContext.Current.CancellationToken);
        Assert.ThrowsAny<OperationCanceledException>(() => choices.Page("", canceled.Token));
        Assert.Equal(3, choices.Page("", TestContext.Current.CancellationToken).Count);
    }
    private static GameScene Scene(int count)
    {
        GameScene scene = new();
        for (int i = 0; i < count; i++) scene.Nodes.Add(new(i, "node_" + i, "object3d", null, [], [], new(), new()));
        return scene;
    }
}
