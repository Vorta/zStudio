using System.Numerics;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using Recoil.Zbd.Core;
using Recoil.Zbd.Core.Formats;
using Xunit;

namespace Recoil.Zbd.Tests;

public sealed class AiNetworkTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Version106ConstraintsDoNotMakeSpatialTargetsAmbiguous(bool constraintFirst)
    {
        var constraint = A(A(I(0), I(1)), S("canleave"), A(I(1)), S("scan_time"), A(I(2)));
        var node = Node(0);
        var root = A(S("version"), A(I(106)), S("node_00"), Node(1),
            S("node_01"), constraintFirst ? constraint : node, S("node_01"), constraintFirst ? node : constraint);
        byte[] source = ZrdWriter.Write(root, TestContext.Current.CancellationToken);
        var network = Decode(root);
        Assert.Equal(2, network.Nodes.Count); Assert.Equal(2, network.Constraints.Count);
        Assert.Empty(network.Diagnostics);
        Assert.Equal(network.Nodes[1].Id, network.Nodes[0].Links[0].Target);
        Assert.Equal(network.Nodes[0].Id, network.Nodes[1].Links[0].Target);
        Assert.Equal(source, ZrdWriter.Write(root, TestContext.Current.CancellationToken));
    }

    [Theory]
    [InlineData(105, false)]
    [InlineData(106, false)]
    [InlineData(106, true)]
    public void OnlyValidVersion106ConstraintsAreExcludedFromDuplicateCounts(int version, bool malformedConstraint)
    {
        var duplicate = malformedConstraint ? A(A(I(0), I(1)), S("canleave"), I(1)) : Node(0);
        var root = A(S("version"), A(I(version)), S("node_00"), Node(1), S("node_01"), Node(0),
            S("node_01"), A(A(I(0), I(1)), S("canleave"), A(I(1))), S("node_01"), duplicate);
        var network = Decode(root);
        Assert.Null(network.Nodes[0].Links[0].Target);
        Assert.Equal("Ambiguous target", network.Nodes[0].Links[0].Problem);
    }

    private static ZrdNode I(int n) => ZrdNode.Create(ZrdKind.Int, n.ToString(System.Globalization.CultureInfo.InvariantCulture));
    private static ZrdNode F(string n) => ZrdNode.Create(ZrdKind.Float, n);
    private static ZrdNode S(string text) => ZrdNode.Create(ZrdKind.String) with { Text = text };
    private static ZrdNode A(params ZrdNode[] children) => ZrdNode.Create(ZrdKind.Array) with { Children = children };
    private static ZrdNode Node(int a = -1, int b = -1, int c = -1) => A(I(12), A(F("1.25"), F("-2"), F("3")), A(I(a), I(b), I(c)));
    private static AiNetwork Decode(ZrdNode root) => MissionAiNetworks.Decode("network", "archive.zbd", 3, "net_01.zrd", root, TestContext.Current.CancellationToken);
    private static ZbdDocument Archive(params ZrdNode[] roots) => FormatRegistry.Default.OpenBytes("ai-test.zbd",
        ResourceEditingTests.Archive(roots.Select(root => ("net_01.zrd", ZrdWriter.Write(root, TestContext.Current.CancellationToken))).ToArray()), token: TestContext.Current.CancellationToken);
    private static AiNetworkSnapshot Read(ZbdDocument doc) => MissionAiNetworks.Read(doc.Assets.Select(a => (doc, a)), TestContext.Current.CancellationToken);
    [Fact]
    public void CachedSnapshotsLiveOnlyAsLongAsTheirSourceArchive()
    {
        var (archive, snapshot) = CachedSnapshot();
        Assert.True(ReusesSnapshot(archive, snapshot));
        for (int i = 0; i < 3 && snapshot.IsAlive; i++) { GC.Collect(); GC.WaitForPendingFinalizers(); }
        Assert.False(snapshot.IsAlive, "A closed archive's decoded AI source trees remained reachable through the cache.");
    }
    /// <summary>While the archive lives, reading it again reuses the decoded snapshot.</summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static bool ReusesSnapshot(WeakReference<ZbdDocument> archive, WeakReference snapshot) =>
        archive.TryGetTarget(out var doc) && ReferenceEquals(snapshot.Target, Read(doc));
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static (WeakReference<ZbdDocument> Archive, WeakReference Snapshot) CachedSnapshot()
    {
        var doc = Archive(A(S("node_00"), Node())); var snapshot = Read(doc);
        Assert.NotNull(Assert.Single(snapshot.Networks).Source);
        return (new(doc), new(snapshot));
    }
    [Fact]
    public void SnapshotDiagnosticsShareOneBudgetAcrossNetworkMembers()
    {
        // Identically named members are valid; each undecodable one reports a diagnostic, but the snapshot keeps one budget.
        var doc = FormatRegistry.Default.OpenBytes("ai-test.zbd", ResourceEditingTests.Archive(Enumerable.Range(0, 1_000).Select(_ => ("net_01.zrd", new byte[] { 9, 9, 9, 9 })).ToArray()), token: TestContext.Current.CancellationToken);
        var graph = Read(doc); var notes = graph.Diagnostics.ToArray();
        Assert.Equal(1_000, graph.Networks.Count);
        Assert.Equal(PreviewNotes.MaximumItems + 1, notes.Length); Assert.Equal(744, graph.OmittedDiagnostics);
        Assert.Contains("744 additional AI network diagnostics omitted", notes[^1].Message);
        Assert.Equal(PreviewNotes.MaximumItems, graph.Networks.Sum(n => n.Diagnostics.Count));
    }
    [Fact]
    public void Version106LinkPreviewIsBoundedWithoutChangingTheAuthoredTree()
    {
        var links = A(Enumerable.Range(0, 100_000).Select(_ => I(1)).ToArray());
        var root = A(S("version"), A(I(106)), S("node_00"), A(I(12), A(F("0"), F("0"), F("0")), links), S("node_01"), Node());
        byte[] source = ZrdWriter.Write(root, TestContext.Current.CancellationToken);
        var network = Decode(root); var node = network.Nodes[0];
        Assert.Equal(32, node.Links.Count);
        var description = new AiNetworkSnapshot("fixture", [network]).Describe(network, node);
        Assert.Equal(100_000, description["link_count"]!.GetValue<int>()); Assert.True(description["links_truncated"]!.GetValue<bool>());
        Assert.Equal(32, description["links"]!.AsArray().Count); Assert.True(description.ToJsonString().Length < 10_000);
        Assert.Equal(Enumerable.Range(0, 32), node.Links.Select(l => l.Slot));
        Assert.All(node.Links, link => Assert.Equal(network.Nodes[1].Id, link.Target));
        Assert.Contains(network.Diagnostics, d => d.Message.Contains("100000") && d.Message.Contains("32"));
        Assert.Equal(source, ZrdWriter.Write(root, TestContext.Current.CancellationToken));
    }
    [Fact]
    public void Version106EdgeConstraintsRetainAllOrderedAttributesWithoutInventingPositions()
    {
        var network = Decode(A(S("version"), A(I(106)), S("node_00"), Node(1), S("node_01"), Node(0),
            S("node_46"), A(A(I(0), I(1)), S("canleave"), A(I(1), I(1)), S("scan_time"), A(I(2), I(3)))));
        Assert.Equal(2, network.Nodes.Count); Assert.Empty(network.Diagnostics);
        Assert.Equal(["canleave", "scan_time"], network.Constraints.Select(c => c.Kind)); Assert.Equal([0, 1], network.Constraints.Select(c => c.AttributeIndex));
        Assert.All(network.Constraints, c => { Assert.Equal(46, c.Index); Assert.Equal(0, c.FromNode); Assert.Equal(1, c.ToNode); });
        var malformed = Decode(A(S("version"), A(I(106)), S("node_46"), A(A(I(0), I(1)), S("canleave"), A(I(1)), I(9), A())));
        Assert.Empty(malformed.Constraints); Assert.Empty(malformed.Nodes); Assert.Single(malformed.Diagnostics);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(32)]
    [InlineData(33)]
    public void Version106LinkBoundariesAndOmittedSlotsAreValidated(int count)
    {
        var links = A(Enumerable.Range(0, count).Select(_ => I(-1)).ToArray());
        ZrdNode Root(ZrdNode slots) => A(S("version"), A(I(106)), S("node_00"), A(I(12), A(F("0"), F("0"), F("0")), slots));
        var network = Decode(Root(links)); var node = Assert.Single(network.Nodes);
        Assert.Equal(Math.Min(32, count), node.Links.Count); Assert.Equal(count, node.LinkCount); Assert.Equal(count > 32, node.LinksTruncated);
        if (count > 32)
        {
            var invalid = Decode(Root(links with { Children = [.. links.Children.Take(count - 1), S("not an integer")] }));
            Assert.Empty(invalid.Nodes); Assert.Contains(invalid.Diagnostics, d => d.Message.Contains("Expected an integer"));
        }
    }

    [Theory]
    [InlineData("Head-on", AiAttackStrategyKind.HEA)]
    [InlineData("cIrClE", AiAttackStrategyKind.CIR)]
    [InlineData("BAC", AiAttackStrategyKind.BAC)]
    [InlineData("follow", AiAttackStrategyKind.FOL)]
    [InlineData("zigzag", AiAttackStrategyKind.ZIG)]
    [InlineData("SIT", AiAttackStrategyKind.SIT)]
    [InlineData("", AiAttackStrategyKind.Unknown)]
    [InlineData("new-strategy", AiAttackStrategyKind.Unknown)]
    [InlineData(" HEA", AiAttackStrategyKind.Unknown)]
    [InlineData("HE", AiAttackStrategyKind.Unknown)]
    public void AttackStrategyPreservesAuthoredTextAndClassifiesOnlyVerifiedPrefixes(string text, AiAttackStrategyKind kind)
    {
        var network = Decode(A(S("attack_strategy"), A(S(text)), S("node_00"), Node()));
        Assert.Equal(text, network.AttackStrategy.Value); Assert.Equal(kind, network.AttackStrategy.Kind);
        Assert.Equal(AiAttackStrategyState.Stored, network.AttackStrategy.State); Assert.Empty(network.Diagnostics);
        var info = new AiNetworkSnapshot("snapshot", [network]).Describe(network, network.Nodes[0])["attack_strategy"]!;
        Assert.Equal(text, info["value"]!.GetValue<string>()); Assert.Equal(text.Length, info["characters"]!.GetValue<int>());
        Assert.False(info["truncated"]!.GetValue<bool>());
    }
    [Fact]
    public void MissingInvalidAndDuplicateStrategiesRetainValidGraphAndBoundInspection()
    {
        var missing = Decode(A(S("node_00"), Node()));
        Assert.Equal(AiAttackStrategyState.Missing, missing.AttackStrategy.State); Assert.Empty(missing.Diagnostics);
        foreach (var value in new[] { I(4), S("HEA"), A(), A(I(1)), A(S("HEA"), S("CIR")) })
        {
            var invalid = Decode(A(S("attack_strategy"), value, S("node_00"), Node(0)));
            Assert.Equal(AiAttackStrategyState.Invalid, invalid.AttackStrategy.State);
            Assert.Single(invalid.Nodes); Assert.Equal(invalid.Nodes[0].Id, invalid.Nodes[0].Links[0].Target);
            Assert.Single(invalid.Diagnostics); Assert.Contains("attack_strategy", invalid.Diagnostics[0].Message);
        }
        var duplicate = Decode(A(S("attack_strategy"), A(S("HEA")), S("attack_strategy"), I(3), S("node_00"), Node()));
        Assert.Single(duplicate.Nodes); Assert.Equal(AiAttackStrategyState.Invalid, duplicate.AttackStrategy.State);
        Assert.Contains("Ambiguous", Assert.Single(duplicate.Diagnostics).Message);
        string longText = "CIR" + new string('x', 100_000);
        var large = Decode(A(S("attack_strategy"), A(S(longText)), S("node_00"), Node()));
        var info = large.AttackStrategy.Describe();
        Assert.Equal(longText, large.AttackStrategy.Value); Assert.Equal(AiAttackStrategyKind.CIR, large.AttackStrategy.Kind);
        Assert.Equal(longText.Length, info["characters"]!.GetValue<int>()); Assert.True(info["truncated"]!.GetValue<bool>());
        Assert.True(info["value"]!.GetValue<string>().Length < 4200);
    }
    [Fact]
    public async Task StrategyEditsAndUndoPublishNewSnapshotsWithoutTouchingSources()
    {
        var doc = Archive(A(S("attack_strategy"), A(S("Head-on")), S("node_00"), Node()));
        byte[] original = doc.Bytes.ToArray(); var before = Read(doc); var edits = new ResourceEditSession(doc);
        var member = edits.Current.Members[0]; var node = edits.Tree(member, TestContext.Current.CancellationToken).Children[1].Children[0];
        edits.Accept(await edits.PrepareZrdAsync(member.Id, node.Id, "set", value: "\"circle\"", token: TestContext.Current.CancellationToken));
        using AssetResolver resolver = new(Path.GetTempPath()); Guid owner = Guid.NewGuid();
        async Task<AiNetworkSnapshot> Current()
        { resolver.SetWorkspaceSnapshots(owner, [edits.Current.Document]); return Read(await resolver.OpenCachedAsync(doc.Path, TestContext.Current.CancellationToken)); }
        var changed = await Current();
        Assert.NotEqual(before.Id, changed.Id); Assert.Equal(AiAttackStrategyKind.CIR, changed.Networks[0].AttackStrategy.Kind);
        Assert.Equal(before.Networks[0].Nodes[0].Id, changed.Networks[0].Nodes[0].Id);
        edits.UndoRedo(false); Assert.Equal(before.Id, (await Current()).Id);
        edits.UndoRedo(true); Assert.Equal(changed.Id, (await Current()).Id);
        Assert.Equal(original, doc.Bytes.ToArray());
    }

    [Fact]
    public void DirectedSlotsRetainOrderAndAllNegativeSentinelsWithoutInventingReverseEdges()
    {
        var network = Decode(A(A(S("version"), A(I(105)), S("name"), A(S("Name")), S("type"), A(S("standard")),
            S("path_width"), A(F("7.5")), S("node_00"), Node(2, -7, 2), S("node_02"), Node())));
        Assert.Empty(network.Diagnostics); Assert.Equal(7.5f, network.PathWidth); Assert.Equal("standard", network.Type);
        var first = network.Nodes[0]; Assert.Equal(12, first.RawValue); Assert.Equal(new Vector3(1.25f, -2, 3), first.Position);
        Assert.Equal(new[] { 0, 1, 2 }, first.Links.Select(l => l.Slot));
        Assert.Equal(new[] { 2, -7, 2 }, first.Links.Select(l => l.TargetIndex));
        Assert.Equal(network.Nodes[1].Id, first.Links[0].Target); Assert.Equal(first.Links[0].Target, first.Links[2].Target);
        Assert.Null(first.Links[1].Target); Assert.All(network.Nodes[1].Links, l => Assert.Null(l.Target));
    }
    [Fact]
    public void DuplicateIndicesMissingLinksAndUnsupportedKeysRemainDiagnosed()
    {
        var graph = Decode(A(S("node_00"), Node(1, 9, 0), S("node_01"), Node(), S("node_01"), Node(), S("node_99"), Node(), S("node_bad"), Node()));
        Assert.Equal(3, graph.Nodes.Count); Assert.Equal(3, graph.Nodes.Select(n => n.Id).Distinct().Count());
        Assert.Equal("Ambiguous target", graph.Nodes[0].Links[0].Problem); Assert.Equal("Missing target", graph.Nodes[0].Links[1].Problem);
        Assert.Equal(graph.Nodes[0].Id, graph.Nodes[0].Links[2].Target);
        Assert.Contains(graph.Diagnostics, d => d.Message.Contains("node_99")); Assert.Contains(graph.Diagnostics, d => d.Message.Contains("node_bad"));
        Assert.All(graph.Diagnostics, d => { Assert.Equal("Warning", d.Severity); Assert.Equal(3, d.AssetIndex); Assert.Contains("archive.zbd", d.Message); });
    }
    [Fact]
    public void MalformedNodesDoNotDiscardValidNeighboursOrPublishNonFiniteCoordinates()
    {
        var graph = Decode(A(S("node_00"), A(I(12), A(F("0x7FC00000"), F("0"), F("0")), A(I(-1), I(-1), I(-1))),
            S("node_01"), A(I(12)), S("node_02"), Node(0, 1, -1)));
        Assert.Single(graph.Nodes); Assert.Equal(2, graph.Nodes[0].Index);
        Assert.Contains(graph.Diagnostics, d => d.Message.Contains("Non-finite"));
        Assert.All(graph.Nodes[0].Links.Take(2), l => Assert.Equal("Missing target", l.Problem));
        Assert.Throws<InvalidDataException>(() => Decode(A(S("version"), A(I(107)), S("node_00"), Node())));
        Assert.Throws<InvalidDataException>(() => Decode(A(S("version"), A(I(105)), S("version"), A(I(105)))));
        Assert.Throws<InvalidDataException>(() => Decode(A(S("node_00"))));
        Assert.Throws<InvalidDataException>(() => Decode(I(1)));
    }
    [Fact]
    public void WrongNodeValueTypeDoesNotHideValidNodesOrResolveAmbiguousLinks()
    {
        var graph = Decode(A(S("node_00"), I(7), S("node_00"), Node(), S("node_01"), Node(0), S("node_02"), S("malformed")));
        Assert.Equal(2, graph.Nodes.Count);
        Assert.Equal("Ambiguous target", graph.Nodes[1].Links[0].Problem); Assert.Null(graph.Nodes[1].Links[0].Target);
        Assert.Contains(graph.Diagnostics, d => d.Message.Contains("node_02"));
    }
    [Fact]
    public void MalformedDuplicateDoesNotRedirectLinksToAnotherRecord()
    {
        var graph = Decode(A(S("node_00"), A(I(1)), S("node_00"), Node(), S("node_01"), Node(0)));
        Assert.Equal(2, graph.Nodes.Count);
        Assert.Null(graph.Nodes[1].Links[0].Target); Assert.Equal("Ambiguous target", graph.Nodes[1].Links[0].Problem);
    }
    [Fact]
    public void DuplicateMembersHaveDifferentIdentitiesWhileUnchangedSnapshotsStayStable()
    {
        var doc = Archive(A(S("node_00"), Node()), A(S("node_00"), Node())); byte[] before = doc.Bytes.ToArray();
        var first = Read(doc); var second = Read(doc);
        Assert.Equal(first.Id, second.Id); Assert.Equal(first.Networks.Select(n => n.Id), second.Networks.Select(n => n.Id));
        Assert.NotEqual(first.Networks[0].Id, first.Networks[1].Id); Assert.NotEqual(first.Networks[0].Nodes[0].Id, first.Networks[1].Nodes[0].Id);
        Assert.True(first.Networks[0].Nodes[0].SourceOffset >= 0); Assert.Equal(before, doc.Bytes.ToArray());
        var changed = Read(Archive(A(S("node_00"), Node(0)), A(S("node_00"), Node())));
        Assert.NotEqual(first.Id, changed.Id);
        using var canceled = new CancellationTokenSource(); canceled.Cancel();
        Assert.Throws<OperationCanceledException>(() => MissionAiNetworks.Read(doc.Assets.Select(a => (doc, a)), canceled.Token));
    }
    [Fact]
    public async Task PublishedUnsavedResourcesAndUndoDriveTheSameGraphReader()
    {
        var doc = Archive(A(S("node_00"), Node())); var before = Read(doc); var edits = new ResourceEditSession(doc);
        var member = edits.Current.Members[0]; var root = edits.Tree(member, TestContext.Current.CancellationToken);
        var x = root.Children[1].Children[1].Children[0];
        edits.Accept(await edits.PrepareZrdAsync(member.Id, x.Id, "set", value: "88", token: TestContext.Current.CancellationToken));
        using AssetResolver resolver = new(Path.GetTempPath()); Guid owner = Guid.NewGuid();
        resolver.SetWorkspaceSnapshots(owner, [edits.Current.Document]);
        var current = Read(await resolver.OpenCachedAsync(doc.Path, TestContext.Current.CancellationToken));
        Assert.Equal(88, current.Networks[0].Nodes[0].Position.X); Assert.NotEqual(before.Id, current.Id);
        edits.UndoRedo(false); resolver.SetWorkspaceSnapshots(owner, [edits.Current.Document]);
        Assert.Equal(before.Id, Read(await resolver.OpenCachedAsync(doc.Path, TestContext.Current.CancellationToken)).Id);
        Assert.Equal(1.25f, Read(doc).Networks[0].Nodes[0].Position.X);
    }
    [Fact]
    public void UnsupportedAndMalformedMembersAreVisibleAsDiagnosticsAlongsideValidNetworks()
    {
        var doc = Archive(A(S("version"), A(I(104))), I(1), A(S("node_98"), Node(98)));
        var graph = Read(doc); Assert.Equal(3, graph.Networks.Count); Assert.Empty(graph.Networks[0].Nodes); Assert.Empty(graph.Networks[1].Nodes);
        Assert.Equal(2, graph.Diagnostics.Count()); Assert.Single(graph.Networks[2].Nodes);
    }
    [Fact]
    public void CanonicalKeysAndSafePreviewCoordinatesAreValidated()
    {
        Assert.True(MissionAiNetworks.IsCandidate("NET_01.ZRD"));
        Assert.False(MissionAiNetworks.IsCandidate("net_01.zrd\n"));
        var graph = Decode(A(S("node_00\n"), Node(), S("name"), A(I(1)), S("node_01"),
            A(I(12), A(F("1e30"), F("0"), F("0")), A(I(-1), I(-1), I(-1)))));
        Assert.Empty(graph.Nodes); Assert.Equal(3, graph.Diagnostics.Count);
        Assert.Contains(graph.Diagnostics, d => d.Message.Contains("preview range"));
        Assert.Contains(graph.Diagnostics, d => d.Message.Contains("Invalid name"));
    }
    [Fact]
    public async Task CorpusGraphsMatchAuthoredCountsWithoutChangingSourceFiles()
    {
        string? root = Environment.GetEnvironmentVariable("ZSTUDIO_CORPUS"); if (root == null) return;
        int networks = 0, nodes = 0, links = 0, archives = 0;
        foreach (string path in Directory.EnumerateFiles(root, "zrdr.zbd", SearchOption.AllDirectories))
        {
            byte[] before = SHA256.HashData(await File.ReadAllBytesAsync(path, TestContext.Current.CancellationToken));
            var doc = await FormatRegistry.Default.OpenAsync(path, TestContext.Current.CancellationToken);
            var graph = MissionAiNetworks.Read(doc.Assets.Where(a => MissionAiNetworks.IsCandidate(a.Name)).Select(a => (doc, a)), TestContext.Current.CancellationToken);
            Assert.Empty(graph.Diagnostics); networks += graph.Networks.Count; if (graph.Networks.Count > 0) archives++;
            nodes += graph.Networks.Sum(n => n.Nodes.Count); links += graph.Networks.Sum(n => n.Nodes.Sum(p => p.Links.Count(l => l.Target != null)));
            Assert.Equal(before, SHA256.HashData(await File.ReadAllBytesAsync(path, TestContext.Current.CancellationToken)));
        }
        Assert.Equal(6, archives); Assert.Equal(470, networks); Assert.Equal(3717, nodes); Assert.Equal(6240, links);
    }
}
