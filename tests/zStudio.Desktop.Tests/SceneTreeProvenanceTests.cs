using System.IO;
using System.Text.Json.Nodes;
using Recoil.Zbd.Core;
using Recoil.Zbd.Core.Formats;
using Recoil.Zbd.Desktop;
using Xunit;

namespace Recoil.Zbd.Desktop.Tests;

public sealed class SceneTreeProvenanceTests
{
    [Fact]
    public void FullSharedPrefixesAreHashedOnceAndFramedWithoutChangingTheRetainedTuple()
    {
        string archive = "C:/" + string.Join('/', Enumerable.Repeat(new string('a', 120), 20)) + "/archive.zbd";
        SceneTreeProvenance keys = new();
        var records = Enumerable.Range(0, 12).Select(i => new MissionPickupSource(archive, 4, "PUPPIES.ZRD", i)).ToArray();
        var values = records.Select(keys.Source).ToArray();
        Assert.Equal(12, values.Distinct().Count()); Assert.All(values, v => Assert.Equal(64, v.Length));
        Assert.Equal(archive.Length, keys.PrefixCharacters); Assert.Equal(1, keys.PrefixCount);
        Assert.Equal(values[0], keys.Source(records[0] with { ResourceName = "RENAMED.ZRD" }));
        Assert.Equal(values[0], keys.Source(records[0] with { ArchivePath = new string(archive.AsSpan()) }));
        Assert.NotEqual(values[0], keys.Source(records[0] with { ArchivePath = archive + "x" }));
        Assert.NotEqual(values[0], keys.Source(records[0] with { AssetIndex = 5 }));
        Assert.Equal(keys.Scope(records), keys.Scope(records.Reverse().ToArray()));
        Assert.NotEqual(keys.Source(new("a:1", 2, "x", 3)), keys.Source(new("a", 1, "x", 23)));
        Assert.NotEqual(keys.Actor(4, archive), keys.Source(records[0]));
    }

    [Fact]
    public void ActualPickupProducerKeepsFullSourcesWhileEverySiblingGetsACompactKey()
    {
        GameScene scene = new();
        scene.Nodes.Add(Node(0, "pu001", children: [1])); scene.Nodes.Add(Node(1, "bvol", parents: [0]));
        scene.Nodes.Add(Node(2, "world", "world"));
        var world = new ZbdDocument(Path.GetFullPath("tree-provenance.zbd"), new(0, DateTime.MinValue),
            new(FormatFamily.GameZ, 15, Recognition.Supported, "fixture"), ReadOnlyMemory<byte>.Empty) { Scene = scene };
        string archive = "C:/" + string.Join('/', Enumerable.Repeat(new string('p', 120), 20)) + "/resources.zbd";
        var rows = A(A(Enumerable.Range(0, 6).Select(i => PickupRow(i)).ToArray()));
        var mission = MissionSceneLoader.Build(world, null, null, null, null, token: TestContext.Current.CancellationToken,
            pickups: rows.ToJson(TestContext.Current.CancellationToken), pickupSource: new(archive, 7, "puppies.zrd"));
        Assert.Equal(6, mission.Actors.Count);
        var identity = MainWindow.TreeIdentities(mission.Scene, mission, null);
        var tree = new SceneTreeModel(mission.Scene, world.Path, true, new(), identity);
        var children = tree.Roots.Single(r => r.Node?.Class == "world").Children;
        Assert.Equal(6, children.Count);
        Assert.All(mission.Actors, actor =>
        {
            var row = identity(actor.Root);
            Assert.True(row.Key.Length < 100); Assert.DoesNotContain(archive, row.Key);
            Assert.Equal(archive.ToUpperInvariant(), actor.Pickup!.Source.ArchivePath);
            Assert.Same(actor.Name, row.ActorName); Assert.Same(actor.PlacementSource, row.Placement);
        });
        Assert.Equal(3, scene.Nodes.Count); // The production mission builder did not mutate source nodes.
    }

    [Fact]
    public void SharedDifficultyScopeRetainsSelectionAcrossReorderedClonesAndRejectsOtherRecords()
    {
        byte[] medium = ZrdWriter.Write(A(A(PickupRow(1), PickupRow(2))), TestContext.Current.CancellationToken);
        byte[] hard = ZrdWriter.Write(A(A(PickupRow(1), PickupRow(2))), TestContext.Current.CancellationToken);
        var archive = FormatRegistry.Default.OpenBytes(Path.GetFullPath("tree-scopes.zbd"),
            MotionFixture.Archive(("puppies.zrd", medium), ("puppies_hard.zrd", hard)), token: TestContext.Current.CancellationToken);
        var edits = PickupPlacementEditSession.Create([new(MissionDifficulty.Medium, archive, archive.Assets[0]),
            new(MissionDifficulty.Hard, archive, archive.Assets[1])], token: TestContext.Current.CancellationToken);
        Assert.Empty(edits.Diagnostics);
        var records = edits.Records;
        var first = records.Single(r => r.Source.AssetIndex == 0 && r.Source.RecordIndex == 0).Source;
        var counterpart = records.Single(r => r.Source.AssetIndex == 1 && r.Source.RecordIndex == 0).Source;
        var other = records.Single(r => r.Source.AssetIndex == 1 && r.Source.RecordIndex == 1).Source;
        Assert.Equal(2, edits.Scope(first).Sources.Count);
        SceneTreeState state = new();
        var before = Mission(first, other, reverse: false); var oldTree = Tree(before, state, edits);
        var selected = oldTree.Reveal(1)!; oldTree.Select(selected); selected.IsExpanded = true;
        var after = Mission(other, counterpart, reverse: true); var refreshed = Tree(after, state, edits);
        Assert.Equal(2, refreshed.Selected?.Index); Assert.True(refreshed.Selected?.IsExpanded);
        Assert.NotEqual(oldTree.Context, refreshed.Context); Assert.Null(refreshed.Find(selected.Id));
        // The refresh consumer's Scope-derived set keeps full ordinal record equality while remapping coordinates.
        MissionSceneContext Coordinates(MissionSceneContext value) => new(value.Scene, value.SourceNodes.ToList(),
            value.Actors.Select(a => a with { CoordinateSource = a.Pickup!.Source, Pickup = null }).ToList(), [], [], value.Layout, 1);
        var matches = edits.Scope(first).Sources.ToHashSet(edits.SourceComparer);
        Assert.Equal(2, Coordinates(after).RemapNodeFrom(Coordinates(before), 1, matches));
        Assert.DoesNotContain(matches, value => edits.SourceComparer.Equals(value, first with { ArchivePath = first.ArchivePath.ToLowerInvariant() }));
        Assert.DoesNotContain(matches, value => edits.SourceComparer.Equals(value, first with { ResourceName = "RENAMED.ZRD" }));
        var unrelated = Mission(other, other with { RecordIndex = 99 }, reverse: false);
        Assert.Null(Tree(unrelated, state, edits).Selected);
        Assert.False(edits.IsDirty); Assert.False(edits.CanUndo);
    }

    [Fact]
    public void MemberRenameKeepsTheOldTupleAndSharedOccurrencesRemainSeparate()
    {
        MissionPickupSource first = new("C:/archive.zbd", 4, "OLD.ZRD", 7), other = first with { RecordIndex = 8 };
        var before = Mission(first, other, false); SceneTreeState state = new();
        var tree = Tree(before, state, null); tree.Select(tree.Reveal(1));
        var renamed = Mission(other, first with { ResourceName = "NEW.ZRD" }, true);
        Assert.Equal(2, Tree(renamed, state, null).Selected?.Index);
        renamed.Scene.Nodes[0] = renamed.Scene.Nodes[0] with { Children = [2, 2, 1] };
        var repeated = Tree(renamed, new(), null).Roots[0].Children;
        Assert.Equal(repeated[0].Index, repeated[1].Index);
        Assert.NotEqual(repeated[0].StateKey, repeated[1].StateKey);
    }

    [Fact]
    public void UniqueActorsWithoutResourceProvenanceRetainSelectionBySourceRootAndName()
    {
        MissionPickupSource first = new("archive", 0, "member", 1), second = first with { RecordIndex = 2 };
        MissionSceneContext Unbound(MissionSceneContext value) => new(value.Scene, value.SourceNodes.ToList(),
            value.Actors.Select(a => a with { Name = "actor" + a.Pickup!.Source.RecordIndex, Pickup = null }).ToList(), [], [], value.Layout, 1);
        SceneTreeState state = new(); var before = Tree(Unbound(Mission(first, second, false)), state, null);
        before.Select(before.Reveal(1));
        Assert.Equal(2, Tree(Unbound(Mission(second, first, true)), state, null).Selected?.Index);
    }

    [Fact]
    public void DuplicateCounterpartGroupsAndFallbackActorsNeverAcquireAnotherSelection()
    {
        byte[] bytes = ZrdWriter.Write(A(A(PickupRow(1), PickupRow(1))), TestContext.Current.CancellationToken);
        var archive = FormatRegistry.Default.OpenBytes(Path.GetFullPath("ambiguous-tree.zbd"), MotionFixture.Archive(("puppies.zrd", bytes)), token: TestContext.Current.CancellationToken);
        var edits = PickupPlacementEditSession.Create([new(MissionDifficulty.Medium, archive, archive.Assets[0])], token: TestContext.Current.CancellationToken);
        var records = edits.Records;
        Assert.All(records, r => Assert.Single(edits.Scope(r.Source).Sources));
        var mission = Mission(records[0].Source, records[1].Source, false);
        var identities = MainWindow.TreeIdentities(mission.Scene, mission, edits);
        Assert.NotEqual(identities(1).Key, identities(2).Key);

        var duplicateActors = mission.Actors.Select(a => a with { Pickup = null, CoordinateSource = null, Name = "same", SourceRoot = 5 }).ToList();
        var ambiguous = new MissionSceneContext(mission.Scene, mission.SourceNodes.ToList(), duplicateActors, [], [], mission.Layout, 1);
        SceneTreeState state = new(); var tree = Tree(ambiguous, state, null); tree.Select(tree.Reveal(1));
        Assert.Null(Tree(ambiguous, state, null).Selected);
    }

    [Theory]
    [InlineData(0, 0)] [InlineData(10, 5000)] [InlineData(2045, 0)] [InlineData(2046, 10)] [InlineData(2048, 0)] [InlineData(5000, 5000)]
    public void TooltipComposesOnlyTheOldBoundedPrefix(int nameLength, int placementLength)
    {
        string name = new('n', nameLength), placement = new('p', placementLength);
        var identity = new SceneTreeIdentity("compact", 1, placement, name);
        Assert.Same(name, identity.ActorName); Assert.Same(placement, identity.Placement);
        string original = name + " · " + placement;
        Assert.Equal(original.Length <= 2048 ? original : original[..2048] + "…", identity.PlacementPreview());
        Assert.True(identity.PlacementPreview()!.Length <= 2049);
    }

    private static SceneTreeModel Tree(MissionSceneContext mission, SceneTreeState state, PickupPlacementEditSession? edits)
        => new(mission.Scene, "fixture", true, state, MainWindow.TreeIdentities(mission.Scene, mission, edits));
    private static MissionSceneContext Mission(MissionPickupSource first, MissionPickupSource second, bool reverse)
    {
        GameScene scene = new(); scene.Nodes.Add(Node(0, "world", "world", children: reverse ? [2, 1] : [1, 2]));
        scene.Nodes.Add(Node(1, "same", parents: [0])); scene.Nodes.Add(Node(2, "same", parents: [0]));
        MissionActor Actor(int root, MissionPickupSource source) => new(root, 5, "same", "placement",
            new(1, "HEMORTAR_AMMO", 0, 3, default, default, 0, source));
        return new(scene, [0, 5, 5], [Actor(1, first), Actor(2, second)], [], [], MissionLayoutSelection.For(MissionDifficulty.Medium), 1);
    }
    private static GameNode Node(int index, string name, string kind = "object3d", int[]? parents = null, int[]? children = null)
        => new(index, name, kind, null, parents ?? [], children ?? [], new() { ["flags"] = 4 }, new() { ["flags"] = 8 });
    private static ZrdNode A(params ZrdNode[] children) => ZrdNode.Create(ZrdKind.Array) with { Children = children };
    private static ZrdNode S(string value) => ZrdNode.Create(ZrdKind.String) with { Text = value };
    private static ZrdNode F(float value) => ZrdNode.Create(ZrdKind.Float) with { Bits = BitConverter.SingleToUInt32Bits(value) };
    private static ZrdNode PickupRow(float x) => A(S("HEMORTAR_AMMO"), ZrdNode.Create(ZrdKind.Int), A(F(x), F(0), F(0)), A(F(0), F(0), F(0)), F(1));
}
