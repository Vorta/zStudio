using System.Numerics;
using System.Security.Cryptography;
using System.Text;
using zStudio.Core;
using zStudio.Core.Formats;
using Xunit;

namespace zStudio.Tests;

public sealed class MissionIdentityProjectionTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    // Essential batch contract: one history operation, atomic rejection and only coordinate bytes changed.

[Fact]
    public void CustomPreviewComparerKeepsItsPublicLookupSemantics()
    {
        var (_, edits, snapshot) = Coordinates(1);
        var source = Assert.Single(edits.OtherCoordinates).Source;
        Dictionary<MissionPickupSource, PlacementTransform> preview = new(new CaseInsensitiveSources())
        {
            [source with { ArchivePath = source.ArchivePath.ToLowerInvariant(), ResourceName = source.ResourceName.ToLowerInvariant() }] = new(new(7, 8, 9), Vector3.Zero)
        };
        var result = edits.ApplyAiPositions(snapshot, preview);
        Assert.Equal(snapshot.Id, result.Id);
        Assert.Equal(new Vector3(7, 8, 9), Assert.Single(result.Networks[0].Nodes).Position);
    }

    [Fact]
    public void PrefixProjectionHashesSharedStringsOnceAndPreservesCompleteValueEquality()
    {
        string path = "C:\\" + new string('P', 160) + "\\NETWORK.ZBD", member = "NET_01.ZRD";
        var values = Enumerable.Range(0, 8).Select(i => new KeyValuePair<MissionPickupSource, int>(new(path, 3, member, i), i * 10)).ToArray();
        var projection = new MissionCoordinateProjection<int>(values);
        Assert.Equal(1, projection.ArchiveValueLookups); Assert.Equal(1, projection.MemberValueLookups);
        var records = projection.Find(path, 3, member); Assert.NotNull(records);
        for (int i = 0; i < 8; i++) Assert.Equal(i * 10, records[i]);
        Assert.Equal(1, projection.ArchiveValueLookups); Assert.Equal(1, projection.MemberValueLookups);
        string equalPath = new(path.ToCharArray()), equalMember = new(member.ToCharArray());
        Assert.Same(records, projection.Find(equalPath, 3, equalMember));
        Assert.Equal(2, projection.ArchiveValueLookups); Assert.Equal(2, projection.MemberValueLookups);
        Assert.Same(records, projection.Find(equalPath, 3, equalMember));
        Assert.Equal(2, projection.ArchiveValueLookups); Assert.Equal(2, projection.MemberValueLookups);
        Assert.Null(projection.Find(path, 4, member)); Assert.Null(projection.Find(path.ToLowerInvariant(), 3, member));
        Assert.Null(projection.Find(path, 3, "NET_02.ZRD"));
    }

    [Fact]
    public void ActualAiRefreshDoesNotAllocateOneLongPathCopyPerNode()
    {
        var edits = PickupPlacementEditSession.Create([], token: Token);
        string drive = Path.GetPathRoot(Path.GetFullPath("network.zbd"))!;
        string longer = Path.Combine(drive, string.Join(Path.DirectorySeparatorChar.ToString(), Enumerable.Repeat(new string('a', 80), 8)), "network.zbd");
        AiNode[] nodes = Enumerable.Range(0, 64).Select(i => new AiNode("node" + i, i, 12, Vector3.Zero, i, [])).ToArray();
        var shortSnapshot = Snapshot(Path.Combine(drive, "short.zbd")); var longSnapshot = Snapshot(longer);
        _ = edits.ApplyAiPositions(shortSnapshot); _ = edits.ApplyAiPositions(longSnapshot);
        long shortBytes = Allocation(shortSnapshot), longBytes = Allocation(longSnapshot);
        Assert.True(longBytes - shortBytes < 24 * 1024, $"Long identity added {longBytes - shortBytes:N0} bytes for 64 nodes.");
        // Only in-memory identities are used; no directories/files are created for either path.
        AiNetworkSnapshot Snapshot(string path) => new("allocation", [new("net", path, 0, "net_01.zrd", "network", "standard", 0, nodes, [])]);
        long Allocation(AiNetworkSnapshot input)
        {
            long before = GC.GetAllocatedBytesForCurrentThread(); var result = edits.ApplyAiPositions(input);
            long used = GC.GetAllocatedBytesForCurrentThread() - before;
            Assert.Same(input, result); return used;
        }
    }

    [Fact]
    public void EmptyNetworkDoesNotEvaluateAnUnusedInvalidIdentity()
    {
        var (_, edits, snapshot) = Coordinates(1);
        var empty = snapshot.Networks[0] with { Archive = "\0", Member = "unused", Nodes = [] };
        var onlyEmpty = snapshot with { Networks = [empty] };
        Assert.Same(onlyEmpty, edits.ApplyAiPositions(onlyEmpty));
        var source = Assert.Single(edits.OtherCoordinates).Source;
        var preview = edits.PreviewAiPositions(new Dictionary<MissionPickupSource, Vector3>(edits.SourceComparer) { [source] = new(4, 5, 6) });
        var combined = snapshot with { Networks = [empty, snapshot.Networks[0]] };
        var updated = edits.ApplyAiPositions(combined, preview);
        Assert.Empty(updated.Networks[0].Nodes); Assert.Equal(new Vector3(4, 5, 6), updated.Networks[1].Nodes[0].Position);
    }

    [Fact]
    public async Task ActualMw3LoaderSharesFullIdentityAndDescriptionAcrossDistinctActors()
    {
        using var fixture = new Mw3MissionFixture("actor_01", "actor_02", "actor_03");
        using var canceled = CancellationTokenSource.CreateLinkedTokenSource(Token); canceled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => MissionSceneLoader.LoadAsync(fixture.World, fixture.Resolver, token: canceled.Token));
        var mission = await MissionSceneLoader.LoadAsync(fixture.World, fixture.Resolver, token: Token);
        Assert.Equal(3, mission.Actors.Count);
        var first = mission.Actors[0]; var firstSource = Assert.IsType<MissionPickupSource>(first.CoordinateSource);
        Assert.Equal(new[] { 0, 1, 2 }, mission.Actors.Select(a => a.CoordinateSource!.RecordIndex));
        Assert.All(mission.Actors, actor =>
        {
            var source = Assert.IsType<MissionPickupSource>(actor.CoordinateSource);
            Assert.Equal(Path.GetFullPath(fixture.ReaderPath).ToUpperInvariant(), source.ArchivePath);
            Assert.Equal("AIV.ZRD", source.ResourceName);
            Assert.Same(firstSource.ArchivePath, source.ArchivePath);
            Assert.Same(firstSource.ResourceName, source.ResourceName);
            Assert.Same(first.PlacementSource, actor.PlacementSource); Assert.Equal(mission.Layout.Description, actor.PlacementSource);
        });
        Assert.Equal(3, mission.Actors.Select(a => a.Root).Distinct().Count());
        Assert.All(mission.Actors, a => Assert.Equal(1, a.SourceRoot));
        Assert.Equal(fixture.ReaderBytes, File.ReadAllBytes(fixture.ReaderPath)); Assert.Equal(2, fixture.World.Scene!.Nodes.Count);
    }

    private static (AssetDocument Archive, PickupPlacementEditSession Edits, AiNetworkSnapshot Snapshot) Coordinates(int count)
    {
        var root = A(Enumerable.Range(0, count).SelectMany(i => new[] { S($"node_{i:D2}"), A(I(12), A(F(i + 1), F(2), F(3)), A(I(-1), I(-1), I(-1))) }).ToArray());
        var archive = FormatRegistry.Default.OpenBytes(Path.GetFullPath("identity-network.zbd"),
            ResourceReaderTests.Archive(("net_01.zrd", ZrdWriter.Write(root, Token))), token: Token);
        var resource = Assert.Single(archive.Assets);
        var edits = PickupPlacementEditSession.Create([], token: Token); edits.AddCoordinates([(archive, resource)], Token);
        var snapshot = MissionAiNetworks.Read([(archive, resource)], Token);
        Assert.Equal(count, edits.OtherCoordinates.Count); Assert.Equal(count, Assert.Single(snapshot.Networks).Nodes.Count);
        return (archive, edits, snapshot);
    }
    private sealed class CaseInsensitiveSources : IEqualityComparer<MissionPickupSource>
    {
        public bool Equals(MissionPickupSource? x, MissionPickupSource? y) => x?.AssetIndex == y?.AssetIndex && x?.RecordIndex == y?.RecordIndex &&
            StringComparer.OrdinalIgnoreCase.Equals(x?.ArchivePath, y?.ArchivePath) && StringComparer.OrdinalIgnoreCase.Equals(x?.ResourceName, y?.ResourceName);
        public int GetHashCode(MissionPickupSource value) => HashCode.Combine(value.AssetIndex, value.RecordIndex,
            StringComparer.OrdinalIgnoreCase.GetHashCode(value.ArchivePath), StringComparer.OrdinalIgnoreCase.GetHashCode(value.ResourceName));
    }
    private static ZrdNode A(params ZrdNode[] children) => ZrdNode.Create(ZrdKind.Array) with { Children = children };
    private static ZrdNode S(string value) => ZrdNode.Create(ZrdKind.String) with { Text = value };
    private static ZrdNode I(int value) => ZrdNode.Create(ZrdKind.Int) with { Bits = unchecked((uint)value) };
    private static ZrdNode F(float value) => ZrdNode.Create(ZrdKind.Float) with { Bits = BitConverter.SingleToUInt32Bits(value) };
}
