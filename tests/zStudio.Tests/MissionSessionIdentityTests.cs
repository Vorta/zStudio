using System.Numerics;
using Recoil.Zbd.Core;
using Recoil.Zbd.Core.Formats;
using Xunit;

namespace Recoil.Zbd.Tests;

public sealed class MissionSessionIdentityTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Fact]
    public void ExactComparerMatchesPublicEqualityAndDoesNotRetainUnknownQueries()
    {
        var comparer = new MissionSourceComparer();
        var first = new MissionPickupSource("C:\\full identity\\reader.zbd", 2, "puppies.zrd", 7);
        var equal = Copy(first);
        MissionPickupSource[] values = [first, equal, first with { ArchivePath = first.ArchivePath.ToUpperInvariant() },
            first with { ResourceName = "PUPPIES.ZRD" }, first with { AssetIndex = 3 }, first with { RecordIndex = 8 },
            first with { ArchivePath = null! }, first with { ResourceName = null! }];
        foreach (var value in values) comparer.Register(value);
        foreach (var a in values)
            foreach (var b in values)
            {
                bool expected = EqualityComparer<MissionPickupSource>.Default.Equals(a, b);
                Assert.Equal(expected, comparer.Equals(a, b));
                if (expected) Assert.Equal(comparer.GetHashCode(a), comparer.GetHashCode(b));
            }
        Assert.True(comparer.Equals(null, null)); Assert.False(comparer.Equals(first, null));
        Assert.Equal(EqualityComparer<MissionPickupSource>.Default.GetHashCode(null!), comparer.GetHashCode(null!));
        int retained = comparer.RetainedPrefixCount;
        for (int i = 0; i < 16; i++) _ = comparer.GetHashCode(first with { ArchivePath = "unknown-" + i });
        Assert.Equal(retained, comparer.RetainedPrefixCount);
    }

    [Fact]
    public void NegativeMemoRegistrationAndResetKeepStoredHashesAndEqualQueriesValid()
    {
        var comparer = new MissionSourceComparer();
        var source = new MissionPickupSource("C:\\stable\\archive.zbd", 2, "NET_01.ZRD", 8);
        var external = Copy(source);
        int hash = comparer.GetHashCode(external); // A query precedes admission of this prefix.
        comparer.Register(source);
        Dictionary<MissionPickupSource, int> values = new(comparer) { [source] = 91 };
        Assert.Equal(91, values[external]);
        comparer.Reset([Copy(source)]);
        Assert.Equal(hash, comparer.GetHashCode(external)); Assert.Equal(91, values[Copy(source)]);
        var other = source with { ArchivePath = "C:\\fresh\\archive.zbd" };
        comparer.Register(other); values.Add(other, 92);
        Assert.Equal(91, values[external]); Assert.Equal(92, values[Copy(other)]);
    }

    [Fact]
    public void SharedExternalPrefixResolutionDoesNotScaleWithRecordCount()
    {
        var comparer = new MissionSourceComparer();
        string archive = "C:\\" + new string('P', 128) + "\\records.zbd", member = "NET_01.ZRD";
        var keys = Enumerable.Range(0, 8).Select(i => new MissionPickupSource(archive, 2, member, i)).ToArray();
        foreach (var key in keys) comparer.Register(key);
        var values = keys.ToDictionary(k => k, k => k.RecordIndex, comparer);
        string otherArchive = new(archive.ToCharArray()), otherMember = new(member.ToCharArray());
        long before = comparer.PrefixResolutions;
        foreach (var key in keys) Assert.Equal(key.RecordIndex, values[key with { ArchivePath = otherArchive, ResourceName = otherMember }]);
        Assert.Equal(before + 2, comparer.PrefixResolutions);
        foreach (var key in keys) Assert.Equal(key.RecordIndex, values[key with { ArchivePath = otherArchive, ResourceName = otherMember }]);
        Assert.Equal(before + 2, comparer.PrefixResolutions);
    }

    [Fact]
    public async Task ConcurrentReadMemoizationAndRegistryReplacementPreserveEquality()
    {
        var comparer = new MissionSourceComparer();
        var keys = Enumerable.Range(0, 8).Select(i => new MissionPickupSource("C:\\shared\\archive.zbd", 2, "net.zrd", i)).ToArray();
        foreach (var key in keys) comparer.Register(key);
        var map = keys.ToDictionary(k => k, k => k.RecordIndex, comparer);
        var queries = keys.Select(Copy).ToArray();
        await Task.WhenAll(Task.Run(() =>
        {
            for (int pass = 0; pass < 20; pass++)
                foreach (var query in queries) Assert.Equal(query.RecordIndex, map[query]);
        }, Token), Task.Run(() =>
        {
            for (int pass = 0; pass < 20; pass++) comparer.Reset(keys);
        }, Token));
    }

    [Fact]
    public void MixedSessionReadsAndDistinctMissionKeysResolveFullPrefixesOnlyOnce()
    {
        var doc = Archive("mixed-identity.zbd", ("puppies.zrd", Pickups(0, 1, 2, 3)),
            ("aiv.zrd", A(S("tank_01"), Actor(9))), ("net_01.zrd", A(S("node_00"), Ai(7))));
        var edits = PickupPlacementEditSession.Create([new(MissionDifficulty.Medium, doc, doc.Assets[0])], token: Token);
        edits.AddCoordinates(doc.Assets.Skip(1).Select(a => (doc, a)), Token, mw3: true);
        string archive = Assert.Single(edits.ArchivePaths);
        var sources = edits.Records.Select(r => r.Source).Concat(edits.OtherCoordinates.Select(r => r.Source)).Select(Copy).ToArray();
        List<MissionActor> actors = [];
        foreach (var record in edits.Records)
            actors.Add(new(actors.Count, 0, "pickup", "test", new(0, record.Type, 1, 1, record.OriginalPosition, record.Rotation, 0, sources[actors.Count])));
        var tank = Assert.Single(edits.OtherCoordinates, r => r.Kind == "tank");
        actors.Add(new(actors.Count, 0, "tank", "test", CoordinateSource: Copy(tank.Source)));
        var mission = new MissionSceneContext(new(), [], actors, [], [], MissionLayoutSelection.For(MissionDifficulty.Medium), 0);
        Read(); long resolved = edits.IdentityPrefixResolutions;
        for (int pass = 0; pass < 8; pass++) Read();
        Assert.Equal(resolved, edits.IdentityPrefixResolutions);
        Assert.Equal(4, edits.Records.Count); Assert.Equal(doc.Bytes.ToArray(), edits.EncodeArchive(archive));
        void Read()
        {
            Assert.False(edits.IsDirty); Assert.False(edits.IsArchiveDirty(archive));
            Assert.Equal(4, edits.PreviewPositions(mission).Count); Assert.Single(edits.TankPreviewPositions(mission));
            foreach (var source in sources)
            {
                Assert.True(edits.Find(source) != null || edits.Coordinate(source) != null);
                Assert.Equal(edits.Position(source), edits.Transform(source).Position); _ = edits.RotationKind(source);
            }
        }
    }

    [Fact]
    public void ScopeIndexesOriginalRowsOnceAndPreservesOrderedAmbiguityAndSignedZero()
    {
        var doc = Archive("scopes.zbd", ("puppies.zrd", Pickups(0, 5)),
            ("puppies_easy.zrd", Pickups(-0f, 5, 5)), ("puppies_hard.zrd", Pickups(0, 5)));
        var edits = PickupPlacementEditSession.Create([
            new(MissionDifficulty.Medium, doc, doc.Assets[0]), new(MissionDifficulty.Easy, doc, doc.Assets[1]),
            new(MissionDifficulty.Hard, doc, doc.Assets[2])], token: Token);
        var rows = edits.Records;
        foreach (var row in rows) _ = edits.Scope(Copy(row.Source));
        Assert.Equal(rows.Count, edits.ScopeIndexRows);
        var zero = edits.Scope(rows[0].Source);
        Assert.Equal(new[] { 0, 1, 2 }, zero.Sources.Select(s => s.AssetIndex));
        Assert.Same(zero, edits.Scope(rows[2].Source));
        Assert.Throws<NotSupportedException>(() => ((IList<MissionPickupSource>)zero.Sources).Clear());
        var five = edits.Scope(rows[1].Source);
        Assert.Equal(new[] { 0, 2 }, five.Sources.Select(s => s.AssetIndex));
        Assert.Single(edits.Scope(rows[3].Source).Sources); Assert.Single(edits.Scope(rows[4].Source).Sources);
        Assert.Contains("Easy", five.Description); Assert.Contains("ambiguous", five.Description);
        Assert.True(edits.MoveTo(Copy(rows[0].Source), new(10, 20, 30)));
        foreach (var source in zero.Sources) Assert.Equal(new Vector3(10, 20, 30), edits.Position(source));
        Assert.Same(zero, edits.Scope(rows[0].Source)); Assert.Equal(rows.Count, edits.ScopeIndexRows);
        edits.Undo(); Assert.False(edits.IsDirty); edits.Redo(); Assert.True(edits.IsDirty);
        Assert.Equal(rows.Count, edits.ScopeIndexRows);
    }

    [Fact]
    public void TemplateBindingAndCanceledAdmissionInvalidateOriginalScopeViews()
    {
        var doc = Archive("templates.zbd", ("aiv.zrd", A(S("tank_01"), Actor(1))), ("aiv_hard.zrd", A(S("tank_02"), Actor(1))));
        var edits = PickupPlacementEditSession.Create([], token: Token);
        edits.AddCoordinates(doc.Assets.Select(a => (doc, a)), Token);
        var source = edits.OtherCoordinates[0].Source;
        Assert.Contains("missing", edits.Scope(source).Description);
        GameScene scene = new(); scene.Nodes.Add(new(0, "tank", "object3d", null, [], [], new(), new()));
        edits.BindCoordinateTemplates(scene); Assert.Equal(2, edits.Scope(source).Sources.Count);
        scene.Nodes.Add(new(1, "tank", "object3d", null, [], [], new(), new()));
        edits.BindCoordinateTemplates(scene); Assert.Contains("missing", edits.Scope(source).Description);
        long prior = edits.ScopeIndexRows;
        using var canceled = CancellationTokenSource.CreateLinkedTokenSource(Token); canceled.Cancel();
        Assert.Throws<OperationCanceledException>(() => edits.AddCoordinates(doc.Assets.Select(a => (doc, a)), canceled.Token));
        Assert.Contains("missing", edits.Scope(source).Description); Assert.Equal(prior + 2, edits.ScopeIndexRows);
    }

    [Fact]
    public void MixedPublicMissionSpecificRowsRemainOrdinaryTankCounterparts()
    {
        var specific = Archive("specific.zbd", ("aiv.zrd", A(S("tank_01"), Actor(1))));
        var ordinary = Archive("ordinary.zbd", ("aiv.zrd", A(S("tank_02"), Actor(1))));
        var edits = PickupPlacementEditSession.Create([], token: Token);
        edits.AddCoordinates([(specific, specific.Assets[0])], Token, mw3: true);
        edits.AddCoordinates([(ordinary, ordinary.Assets[0])], Token);
        GameScene scene = new(); scene.Nodes.Add(new(0, "tank", "object3d", null, [], [], new(), new()));
        edits.BindCoordinateTemplates(scene);
        var rows = edits.OtherCoordinates;
        Assert.Single(edits.Scope(rows[0].Source).Sources);
        Assert.Equal(new[] { rows[0].Source, rows[1].Source }, edits.Scope(rows[1].Source).Sources);
    }

    [Fact]
    public void RebaseAdoptsFreshKeysAndScopesButKeepsTouchedHistory()
    {
        var first = Archive("first.zbd", ("puppies.zrd", Pickups(1)));
        var second = Archive("second.zbd", ("puppies.zrd", Pickups(2)));
        var edits = PickupPlacementEditSession.Create([new(MissionDifficulty.Medium, first, first.Assets[0]), new(MissionDifficulty.Easy, second, second.Assets[0])], token: Token);
        var touched = edits.Records[0].Source; var oldUntouched = Copy(edits.Records[1].Source);
        _ = edits.Scope(oldUntouched); edits.MoveTo(touched, new(9, 8, 7));
        var changed = Archive("second.zbd", ("puppies.zrd", Pickups(3, 4)));
        var fresh = PickupPlacementEditSession.Create([new(MissionDifficulty.Easy, changed, changed.Assets[0])], token: Token);
        edits.RebaseUntouched(fresh);
        Assert.Equal(3, edits.Records.Count); Assert.Equal(new Vector3(3, 2, 3), edits.Position(oldUntouched));
        Assert.Single(edits.Scope(oldUntouched).Sources); Assert.Equal(new Vector3(9, 8, 7), edits.Position(Copy(touched)));
        edits.Undo(); Assert.Equal(new Vector3(1, 2, 3), edits.Position(touched)); Assert.False(edits.IsDirty);
        edits.Redo(); Assert.Equal(new Vector3(9, 8, 7), edits.Position(touched)); Assert.True(edits.IsDirty);
    }

    private static MissionPickupSource Copy(MissionPickupSource source) => source with
    { ArchivePath = new(source.ArchivePath.ToCharArray()), ResourceName = new(source.ResourceName.ToCharArray()) };
    private static ZbdDocument Archive(string path, params (string Name, ZrdNode Root)[] entries) =>
        FormatRegistry.Default.OpenBytes(Path.GetFullPath(path), ResourceEditingTests.Archive(entries.Select(e => (e.Name, ZrdWriter.Write(e.Root, Token))).ToArray()), token: Token);
    private static ZrdNode Pickups(params float[] xs) => A(A(xs.Select(x => A(S("NANITE"), I(1), A(F(x), F(2), F(3)), A(F(0), F(0), F(0)), F(0))).ToArray()));
    private static ZrdNode Actor(float x) => A(I(0), A(F(x), F(2), F(3)), F(0));
    private static ZrdNode Ai(float x) => A(I(12), A(F(x), F(2), F(3)), A(I(-1), I(-1), I(-1)));
    private static ZrdNode A(params ZrdNode[] children) => ZrdNode.Create(ZrdKind.Array) with { Children = children };
    private static ZrdNode S(string value) => ZrdNode.Create(ZrdKind.String) with { Text = value };
    private static ZrdNode I(int value) => ZrdNode.Create(ZrdKind.Int) with { Bits = unchecked((uint)value) };
    private static ZrdNode F(float value) => ZrdNode.Create(ZrdKind.Float) with { Bits = BitConverter.SingleToUInt32Bits(value) };
}

public sealed partial class AnimationTests
{
    [Fact]
    public async Task SessionIdentityIndexesKeepVerifiedSaveBaselineAndUndoSemantics()
    {
        await WithMissionArchiveAsync(new() { ["puppies.zrd"] = Zrd(PickupList(PickupRow())) }, async (world, resolver) =>
        {
            var token = TestContext.Current.CancellationToken;
            var edits = await PickupPlacementEditSession.LoadAsync(world.Path, resolver, token);
            var row = Assert.Single(edits.Records); string archive = Assert.Single(edits.ArchivePaths);
            var external = row.Source with { ArchivePath = new(row.Source.ArchivePath.ToCharArray()), ResourceName = new(row.Source.ResourceName.ToCharArray()) };
            var scope = edits.Scope(external); var before = edits.EncodeArchive(archive);
            int accepted = 0; edits.EditAccepted += () => accepted++;
            var target = row.OriginalPosition + new Vector3(9, 8, 7);
            var draft = edits.PreviewTransform(external, new(target, row.Rotation));
            Assert.Same(edits.SourceComparer, Assert.IsType<Dictionary<MissionPickupSource, PlacementTransform>>(draft).Comparer);
            Assert.False(edits.IsDirty); Assert.False(edits.CanUndo);
            Assert.True(edits.MoveTo(external, target)); Assert.Equal(1, accepted);
            var expected = edits.EncodeArchive(archive);
            Assert.Empty((await edits.SaveAsync(token: token)).Errors); Assert.False(edits.IsDirty);
            Assert.Equal(expected, await File.ReadAllBytesAsync(archive, token)); Assert.Same(scope, edits.Scope(external));
            edits.Undo(); Assert.True(edits.IsDirty); Assert.Equal(before, edits.EncodeArchive(archive));
            edits.Redo(); Assert.False(edits.IsDirty); Assert.Equal(target, edits.Position(external));
            Assert.Equal(1, accepted);
        });
    }
}
