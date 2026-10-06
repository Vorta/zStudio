using System.Collections;
using System.Numerics;
using Recoil.Zbd.Core;
using Recoil.Zbd.Core.Animation;
using Recoil.Zbd.Core.Formats;
using Xunit;

namespace Recoil.Zbd.Tests;

public sealed class MissionCoordinateGrowthTests
{
    private static ZrdNode A(params ZrdNode[] children) => ZrdNode.Create(ZrdKind.Array) with { Children = children };
    private static ZrdNode S(string value) => ZrdNode.Create(ZrdKind.String) with { Text = value };
    private static ZrdNode F(float value) => ZrdNode.Create(ZrdKind.Float) with { Bits = BitConverter.SingleToUInt32Bits(value) };
    private static ZrdNode I(int value) => ZrdNode.Create(ZrdKind.Int) with { Bits = unchecked((uint)value) };
    private static ZrdNode Actor(float x = 1, params ZrdNode[] extra) => A([I(0), A(F(x), F(2), F(3)), F(90), ..extra]);
    private static ZbdDocument Archive(string name, ZrdNode root) => FormatRegistry.Default.OpenBytes(Path.GetFullPath("coordinates.zbd"),
        ResourceEditingTests.Archive((name, ZrdWriter.Write(root, TestContext.Current.CancellationToken))), token: TestContext.Current.CancellationToken);

    [Fact]
    public void CoordinateIndexReusesTheTypedTreeWithoutExpandingInactivePayload()
    {
        var archive = Archive("aiv.zrd", A(S("actor_01"), Actor(1, S(new string('x', 2_000_000)))));
        var edits = PickupPlacementEditSession.Create([], token: TestContext.Current.CancellationToken);
        long before = GC.GetAllocatedBytesForCurrentThread();
        edits.AddCoordinates([(archive, archive.Assets[0])], TestContext.Current.CancellationToken, mw3: true);
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        Assert.True(allocated < archive.Bytes.Length + 1_000_000, $"Index allocated {allocated:N0} bytes for {archive.Bytes.Length:N0} source bytes.");
        Assert.Equal(new Vector3(1, 2, 3), Assert.Single(edits.OtherCoordinates).OriginalPosition);
        Assert.Equal(archive.Bytes.ToArray(), edits.EncodeArchive(archive.Path));
    }

    [Fact]
    public void MalformedCoordinateDiagnosticsAreCappedAndDiscloseOmittedCount()
    {
        var root = A(Enumerable.Range(0, 1000).SelectMany(i => new[] { S("actor_" + i), Actor(float.NaN) }).ToArray());
        var archive = Archive("aiv.zrd", root); var edits = PickupPlacementEditSession.Create([], token: TestContext.Current.CancellationToken);
        edits.AddCoordinates([(archive, archive.Assets[0])], TestContext.Current.CancellationToken, mw3: true);
        Assert.Empty(edits.OtherCoordinates); Assert.Equal(257, edits.Diagnostics.Count);
        Assert.Contains("744", edits.Diagnostics[^1]); Assert.Contains("omitted", edits.Diagnostics[^1]);
        Assert.False(edits.IsDirty); Assert.False(edits.CanUndo);
    }

    [Fact]
    public void ManyPlacementsShareOneArchiveBaseline()
    {
        var root = A(Enumerable.Range(0, 50).SelectMany(i => new[] { S("actor_" + i), Actor(1, S(i == 0 ? new string('x', 1_000_000) : "")) }).ToArray());
        var archive = Archive("aiv.zrd", root); var edits = PickupPlacementEditSession.Create([], token: TestContext.Current.CancellationToken);
        long before = GC.GetAllocatedBytesForCurrentThread();
        edits.AddCoordinates([(archive, archive.Assets[0])], TestContext.Current.CancellationToken, mw3: true);
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        Assert.Equal(50, edits.OtherCoordinates.Count);
        Assert.True(allocated < archive.Bytes.Length + 1_000_000, $"Allocated {allocated:N0} for one shared baseline.");
    }

    [Theory]
    [InlineData("nested", true)][InlineData("enclosing", true)][InlineData("equal", true)]
    [InlineData("partial", true)][InlineData("adjacent", false)][InlineData("empty-end", false)]
    public void IndexedOverlapGuardPreservesSourceProtection(string shape, bool rejected)
    {
        var archive = Archive("aiv.zrd", A(S("actor_01"), Actor()));
        var member = archive.Assets[0];
        (long offset, long length) = shape switch
        {
            "nested" => (member.Offset + 8, 8),
            "enclosing" => (member.Offset - 1, member.Length + 2),
            "equal" => (member.Offset, member.Length),
            "partial" => (member.Offset - 1, 2),
            "adjacent" => (member.Offset - 1, 1),
            _ => (member.Offset + member.Length, 0)
        };
        archive.Add(AssetKind.Raw, 1, "opaque", offset, length);
        var edits = PickupPlacementEditSession.Create([], token: TestContext.Current.CancellationToken);
        edits.AddCoordinates([(archive, member)], TestContext.Current.CancellationToken, mw3: true);
        if (rejected)
        {
            Assert.Empty(edits.OtherCoordinates); Assert.Contains("non-overlapping", Assert.Single(edits.Diagnostics));
            Assert.Empty(edits.ArchivePaths);
        }
        else Assert.Single(edits.OtherCoordinates);
        Assert.False(edits.IsDirty); Assert.False(edits.CanUndo);
    }

    [Fact]
    public void FullValveNameSearchDoesNotConcatenateTheAuthoredString()
    {
        var record = new AiValveRecord(Guid.NewGuid(), Guid.NewGuid(), 0, S(new string('x', 2_000_000) + "tail"), A(), "definition", 123);
        _ = MissionAiValves.MatchesSearch(record, "warm");
        long before = GC.GetAllocatedBytesForCurrentThread();
        Assert.True(MissionAiValves.MatchesSearch(record, "TAIL"));
        Assert.True(MissionAiValves.MatchesSearch(record, "123"));
        Assert.False(MissionAiValves.MatchesSearch(record, "absent"));
        Assert.True(GC.GetAllocatedBytesForCurrentThread() - before < 4096);
    }

    [Fact]
    public void MergedBoundedDiagnosticsRetainTheOriginalOmittedTotal()
    {
        PreviewNotes first = new(); for (int i = 0; i < 1000; i++) first.Add("notice");
        var edits = PickupPlacementEditSession.Create([], first, TestContext.Current.CancellationToken);
        Assert.Equal(1000, edits.DiagnosticCount); Assert.Equal(257, edits.Diagnostics.Count); Assert.Contains("744", edits.Diagnostics[^1]);
    }

    [Fact]
    public void SoundAliasLookupRetainsNamesAndLoopFlagsWithoutCopyingIgnoredPayload()
    {
        var tree = A(S(new string('x', 2_000_000)), A(S("fire"), S("audio\\fire.WAV"), S("LOOPED")), A(S("hit"), S("hit.wav")));
        // Warm the non-empty iterator, filename and tuple materialization paths too;
        // the measured call still traverses the entire two-million-character fixture.
        _ = AnimationPreviewContext.ReadSoundAliases(tree, TestContext.Current.CancellationToken).ToArray();
        long before = GC.GetAllocatedBytesForCurrentThread();
        var aliases = AnimationPreviewContext.ReadSoundAliases(tree, TestContext.Current.CancellationToken).ToArray();
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        Assert.Equal(new[] { ("fire", "fire.WAV", true), ("hit", "hit.wav", false) }, aliases);
        Assert.True(allocated < 4096, $"Allocated {allocated:N0} bytes to inspect an ignored string.");
    }

    [Theory]
    [InlineData(0, float.MaxValue)][InlineData(1, -float.MaxValue)][InlineData(2, 2e12f)]
    public void MissionActorEditsRejectUnsupportedPositionsBeforeHistoryOrPublication(int axis, float value)
    {
        var archive = Archive("aiv.zrd", A(S("actor_01"), Actor())); var edits = PickupPlacementEditSession.Create([], token: TestContext.Current.CancellationToken);
        edits.AddCoordinates([(archive, archive.Assets[0])], TestContext.Current.CancellationToken, mw3: true);
        var record = Assert.Single(edits.OtherCoordinates); var original = edits.Transform(record.Source);
        Vector3 invalid = original.Position; invalid[axis] = value;
        Assert.Throws<InvalidDataException>(() => edits.PreviewTransform(record.Source, original with { Position = invalid }));
        Assert.Throws<InvalidDataException>(() => edits.MoveTo(record.Source, invalid));
        Assert.Equal(original, edits.Transform(record.Source)); Assert.False(edits.CanUndo); Assert.False(edits.IsDirty);
        Assert.True(edits.MoveTo(record.Source, new(4, 5, 6))); edits.Undo();
        Assert.Equal(archive.Bytes.ToArray(), edits.EncodeArchive(archive.Path));
    }

    [Fact]
    public void ValveSourceAttachmentIndexesArchiveAndMemberInsteadOfRescanningNetworks()
    {
        byte[] root = ZrdWriter.Write(A(S("version"), A(I(106))), TestContext.Current.CancellationToken);
        var archive = FormatRegistry.Default.OpenBytes("valve-index.zbd", ResourceEditingTests.Archive(Enumerable.Range(0, 600).Select(_ => ("net_01.zrd", root)).ToArray()), token: TestContext.Current.CancellationToken);
        var networks = archive.Assets.Select(a => MissionAiNetworks.Decode("net" + a.Index, archive.Path.ToUpperInvariant(), a.Index, a.Name, (ZrdNode)a.Content!, TestContext.Current.CancellationToken)).ToArray();
        var counted = new CountedNetworks(networks);
        var result = MissionAiValves.Attach(new("graph", counted), [archive], TestContext.Current.CancellationToken);
        Assert.Equal(600, result.ValveSources.Count);
        for (int i = 0; i < networks.Length; i++) Assert.Same(networks[i].Source, result.ValveSources[i].Root);
        Assert.True(counted.Reads <= networks.Length * 2, $"Visited {counted.Reads:N0} network roots for {networks.Length} members.");
    }
    private sealed class CountedNetworks(AiNetwork[] values) : IReadOnlyList<AiNetwork>
    {
        public int Reads;
        public int Count => values.Length;
        public AiNetwork this[int index] { get { Reads++; return values[index]; } }
        public IEnumerator<AiNetwork> GetEnumerator() { for (int i = 0; i < Count; i++) yield return this[i]; }
        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
    }
}
