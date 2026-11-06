using System.Numerics;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using zStudio.Core;
using zStudio.Core.Formats;
using zStudio.Core.Sources;
using Xunit;

namespace zStudio.Tests;

// Relies on a weak cache entry surviving between two reads; kept apart from tests that run in parallel.
[Collection("Allocation-sensitive")]
public sealed class AiNetworkTests
{
    [Fact]
    public void NetworkHeaderEditsChangeOnlyTheirTokens()
    {
        const string path = "data/m1/zrdr/aipath/net_05.zrd";
        string text = "( version ( 105 ) name ( net_05 ) type ( standard ) path_width ( 10.0 )\r\n  # keep this comment\r\n  attack_rad ( 200.0 ) attack_dwell ( 0.7 )\r\n  node_00 ( 12 ( 1.0 2.0 3.0 ) ( -1 -1 -1 ) ) )\r\n";
        byte[] source = System.Text.Encoding.Latin1.GetBytes(text);
        var token = TestContext.Current.CancellationToken;
        // A stored value is replaced in place and a missing key goes before the nodes; the comment, layout and nodes stay.
        var edit = SourceAiNetworkEdits.Plan(path, source, [new("attack_rad", ["150"]), new("attack_strategy", ["circle"]), new("name", ["net_05"])], token);
        string after = System.Text.Encoding.Latin1.GetString(edit.Content);
        Assert.Equal("Edit net_05: Attack Radius, Attack Strategy", edit.Label);
        Assert.Contains("# keep this comment", after);
        Assert.StartsWith("( version ( 105 ) name ( net_05 ) type ( standard ) path_width ( 10.0 )\r\n", after);
        Assert.True(after.IndexOf("attack_strategy", StringComparison.Ordinal) < after.IndexOf("node_00", StringComparison.Ordinal));
        Assert.EndsWith("node_00 ( 12 ( 1.0 2.0 3.0 ) ( -1 -1 -1 ) ) )\r\n", after);
        var header = SourceAiNetworkEdits.Read(ZrdText.Parse(after, token));
        Assert.Equal("150", header.Fields["attack_rad"]!.Text());
        Assert.Equal("circle", header.Fields["attack_strategy"]!.Text());
        Assert.Equal("0.7", header.Fields["attack_dwell"]!.Text());
        // The game keeps attack_buddy only while activate_buddy is stored, so turning it on stores both.
        var buddies = SourceAiNetworkEdits.Read(ZrdText.Parse(System.Text.Encoding.Latin1.GetString(
            SourceAiNetworkEdits.Plan(path, source, [new("attack_buddy", ["1"])], token).Content), token));
        Assert.True(buddies.Fields["attack_buddy"]!.On && buddies.Fields["activate_buddy"] is { On: false });
        // Values the game cannot take, a binary source and an edit that changes nothing are refused.
        Assert.Throws<InvalidDataException>(() => SourceAiNetworkEdits.Plan(path, source, [new("attack_strategy", ["evade"])], token));
        Assert.Throws<InvalidDataException>(() => SourceAiNetworkEdits.Plan(path, source, [new("name", [new string('n', 20)])], token));
        Assert.Throws<InvalidDataException>(() => SourceAiNetworkEdits.Plan(path, source, [new("path_width", ["0"])], token));
        Assert.Throws<InvalidDataException>(() => SourceAiNetworkEdits.Plan(path, ZrdWriter.Write(ZrdText.Parse(text, token), token), [new("attack_rad", ["1"])], token));
        Assert.Throws<InvalidOperationException>(() => SourceAiNetworkEdits.Plan(path, source, [new("attack_rad", ["200"])], token));
    }

    [Fact]
    public void NodeLinkEditsWriteOnlyTheirSlots()
    {
        const string path = "data/m1/zrdr/aipath/net_05.zrd";
        string text = "(\r\n  version ( 105 )\r\n  attack_rad ( 200.0 )\r\n  node_00 ( 12 ( 1.0 2.0 3.0 ) ( 1 -1 -1 ) )  # first\r\n  node_01 (\r\n    12\r\n    ( 5.0 2.0 7.0 )\r\n    ( 0 -1 -1 )\r\n  )\r\n"
            + "  node_2 ( 12 ( 9.0 2.0 7.0 ) ( -1 -1 -1 ) )\r\n  node_3 ( 12 ( 9.0 2.0 8.0 ) ( -1 -1 -1 ) )\r\n  node_150 ( 12 ( 9.0 2.0 9.0 ) ( -1 -1 -1 ) )\r\n)\r\n";
        byte[] source = System.Text.Encoding.Latin1.GetBytes(text);
        var token = TestContext.Current.CancellationToken;
        // A node takes any number of links, in order; fewer than three are followed by empty slots up to three. Header and
        // links change in one edit, and only the edited slots change: comments, layout and the other nodes stay.
        var edit = SourceAiNetworkEdits.Plan(path, source, [], token, [new(0, [150, 1, 2, 3]), new(1, []), new(2, [0])]);
        string after = System.Text.Encoding.Latin1.GetString(edit.Content);
        Assert.Equal("Edit net_05: Links of node_00, Links of node_01, Links of node_2", edit.Label);
        Assert.Equal(text.Replace("( 1 -1 -1 ) )  # first", "( 150 1 2 3 ) )  # first")
            .Replace("    ( 0 -1 -1 )", "    ( -1 -1 -1 )").Replace("node_2 ( 12 ( 9.0 2.0 7.0 ) ( -1 -1 -1 ) )", "node_2 ( 12 ( 9.0 2.0 7.0 ) ( 0 -1 -1 ) )"), after);
        Assert.Equal("Edit net_05: Attack Radius, Links of node_00", SourceAiNetworkEdits.Plan(path, source, [new("attack_rad", ["150"])], token, [new(0, [2])]).Label);
        var network = Decode(ZrdText.Parse(after, token));
        Assert.Empty(network.Diagnostics);
        Assert.Equal([150, 1, 2, 3], network.Nodes[0].Links.Select(l => l.TargetIndex));
        Assert.All(network.Nodes[0].Links, l => Assert.NotNull(l.Target));
        Assert.Equal([150, 1, 2, 3], SourceAiNetworkEdits.ReadNodes(ZrdText.Parse(after, token), token)[0].Links);
        // A link names another node of the network once; the node and its targets must each be one tag of the file.
        foreach (var refused in new SourceAiNetworkEdits.LinkChange[][] { [new(0, [0])], [new(0, [7])], [new(0, [1, 1])], [new(9, [1])], [new(0, [1]), new(0, [2])] })
            Assert.Throws<InvalidDataException>(() => SourceAiNetworkEdits.Plan(path, source, [], token, refused));
        Assert.Throws<InvalidOperationException>(() => SourceAiNetworkEdits.Plan(path, source, [], token, [new(0, [1])]));
        Assert.Throws<InvalidDataException>(() => SourceAiNetworkEdits.ParseLinks("1 -2"));
        Assert.Equal([3, 150, 0], SourceAiNetworkEdits.ParseLinks(" 3, 150  000 "));
        byte[] repeated = System.Text.Encoding.Latin1.GetBytes(text.Replace("node_150", "node_03"));
        Assert.Throws<InvalidDataException>(() => SourceAiNetworkEdits.Plan(path, repeated, [], token, [new(0, [3])]));
        Assert.Throws<InvalidDataException>(() => SourceAiNetworkEdits.Plan(path, repeated, [], token, [new(3, [0])]));
        Assert.Equal([true, true], SourceAiNetworkEdits.ReadNodes(ZrdText.Parse(text.Replace("node_150", "node_03"), token), token).Where(n => n.Index == 3).Select(n => n.Repeated));
    }

    [Fact]
    public void NetworksPastRecoilLimitsReadAsReRecoilV2ReadsThem()
    {
        // reRECOIL v2 reads net_<n> with two or more digits from 1, any node number and any number of link slots, in version 105 or 200.
        Assert.True(MissionAiNetworks.IsCandidate("net_100.zrd")); Assert.True(MissionAiNetworks.IsCandidate("NET_005.ZRD"));
        Assert.True(MissionAiNetworks.TryNetworkNumber("net_2147483647.zrd", out int number) && number == int.MaxValue);
        foreach (string name in new[] { "net_5.zrd", "net_00.zrd", "net_2147483648.zrd", "net_x1.zrd" }) Assert.False(MissionAiNetworks.IsCandidate(name));
        // MechWarrior 3 missions keep their two-digit names.
        Assert.False(MissionAiNetworks.IsCandidate("net_100.zrd", mw3: true)); Assert.True(MissionAiNetworks.IsCandidate("net_01.zrd", mw3: true));
        foreach (int version in new[] { 105, 200 })
        {
            var network = Decode(A(S("version"), A(I(version)), S("node_5"), Node(150, 0), S("node_000"), Node(5),
                S("node_150"), A(I(12), A(F("0"), F("0"), F("0")), A(I(5), I(0), I(5000), I(9), I(-1))), S("node_5000"), Node(), S("node_9"), A(I(12), A(F("1"), F("0"), F("0")), A())));
            Assert.Empty(network.Diagnostics);
            Assert.Equal([5, 0, 150, 5000, 9], network.Nodes.Select(n => n.Index));
            Assert.Equal(["node_5", "node_000", "node_150", "node_5000", "node_9"], network.Nodes.Select(n => n.Key));
            Assert.Equal(5, network.Nodes[2].LinkCount);
            Assert.Equal([network.Nodes[0].Id, network.Nodes[1].Id, network.Nodes[3].Id, network.Nodes[4].Id], network.Nodes[2].Links.Take(4).Select(l => l.Target));
            Assert.Empty(network.Nodes[4].Links);
        }
    }

    [Fact]
    public void NetworkExportWritesVersion105WithinRecoilLimitsAndVersion200PastThem()
    {
        var token = TestContext.Current.CancellationToken;
        ZrdNode Prepare(string member, ZrdNode root, ExportTarget target) => AiNetworkExport.Prepare(member, root, target, token);
        byte[] Bytes(ZrdNode root) => ZrdWriter.Write(root, token);
        // Within Recoil's limits a network is the same file for both targets: version 105 with three link slots a node.
        var shipped = A(A(S("version"), A(I(105)), S("node_00"), Node(1), S("node_01"), Node(0)));
        Assert.Same(shipped, Prepare("net_05.zrd", shipped, ExportTarget.V1)); Assert.Same(shipped, Prepare("net_05.zrd", shipped, ExportTarget.V2));
        var loose = A(A(S("version"), A(I(200)), S("node_00"), A(I(12), A(F("1"), F("2"), F("3")), A(I(1))), S("node_01"), A(I(12), A(F("1"), F("2"), F("3")), A(I(0), I(-1), I(-1), I(-1)))));
        var written = Bytes(A(A(S("version"), A(I(105)), S("node_00"), A(I(12), A(F("1"), F("2"), F("3")), A(I(1), I(-1), I(-1))), S("node_01"), A(I(12), A(F("1"), F("2"), F("3")), A(I(0), I(-1), I(-1))))));
        Assert.Equal(written, Bytes(Prepare("net_05.zrd", loose, ExportTarget.V1))); Assert.Equal(written, Bytes(Prepare("net_05.zrd", loose, ExportTarget.V2)));
        // A network number above 99 (or a name Recoil does not read), a node above node_98 and a link in a fourth slot make
        // version 200, which target v1 refuses; the links stay as authored.
        var cases = new (string Member, ZrdNode Root, string Named)[]
        {
            ("net_100.zrd", A(S("node_00"), Node()), "net_100.zrd"),
            ("net_005.zrd", A(S("node_00"), Node()), "net_005.zrd"),
            ("net_05.zrd", A(S("version"), A(I(105)), S("node_00"), Node(99), S("node_99"), Node(0)), "node_99"),
            ("net_05.zrd", A(S("node_00"), Node(5), S("node_5"), Node(0)), "node_5"),
            ("net_05.zrd", A(S("node_00"), A(I(12), A(F("1"), F("2"), F("3")), A(I(1), I(2), I(3), I(4), I(-1))), S("node_01"), Node(), S("node_02"), Node(), S("node_03"), Node(), S("node_04"), Node()), "slot 4"),
        };
        foreach (var (member, root, named) in cases)
        {
            Assert.Contains(named, Assert.Throws<InvalidDataException>(() => Prepare(member, root, ExportTarget.V1)).Message);
            var wide = Prepare(member, root, ExportTarget.V2);
            Assert.Equal(["version", "200"], wide.Children.Take(2).Select(c => c.Kind == ZrdKind.Array ? c.Children[0].Value : c.Text));
            Assert.Equal(root.Children.Skip(root.Children[0] is { Kind: ZrdKind.String, Text: "version" } ? 2 : 0), wide.Children.Skip(2));
        }
        // MechWarrior 3's version 106 and a version the game drops are written as they are.
        foreach (int version in new[] { 106, 104 })
        {
            var other = A(S("version"), A(I(version)), S("node_150"), A(I(12), A(F("1"), F("2"), F("3")), A(I(1))));
            Assert.Same(other, Prepare("net_100.zrd", other, ExportTarget.V1));
        }
    }

    [Fact]
    public async Task ExportBuildsNetworksPastRecoilLimitsOnlyForTargetV2()
    {
        using var fixture = new SourceWorldFixture();
        var token = TestContext.Current.CancellationToken;
        const string within = "(\r\n  version ( 105 )\r\n  node_00 ( 12 ( 1.0 2.0 3.0 ) ( 1 -1 -1 ) )\r\n  node_01 ( 12 ( 5.0 2.0 7.0 ) ( 0 -1 -1 ) )\r\n)\r\n";
        fixture.Write("data/m1/zrdr/aipath/net_05.zrd", within);
        fixture.Write("data/m1/zrdr/aipath/net_150.zrd", within.Replace("( 1 -1 -1 )", "( 1 -1 -1 -1 -1 )"));
        // Target v1 refuses the archive, naming the network; nothing is written.
        var v1 = await SourceBuilder.CheckAsync(fixture.Project, ["m1/zrdr.zbd"], token: token, target: ExportTarget.V1);
        Assert.Contains("net_150.zrd", Assert.Single(v1.Outputs).Error);
        // Target v2 writes it as version 200 and the network within Recoil's limits exactly as target v1 does without it.
        string destination = Path.Combine(fixture.Root, "zbd");
        await SourceBuilder.ExportAsync(fixture.Project, destination, ["m1/zrdr.zbd"], token: token, target: ExportTarget.V2);
        var members = ArchiveSources.Read(await File.ReadAllBytesAsync(Path.Combine(destination, "m1", "zrdr.zbd"), token));
        Assert.Equal(ZrdWriter.Write(ZrdText.Parse(within, token), token), members.Single(m => m.Name == "net_05.zrd").Payload.ToArray());
        var wide = ZrdDecoder.Read(members.Single(m => m.Name == "net_150.zrd").Payload.ToArray(), token);
        Assert.Equal(200, unchecked((int)wide.Children[0].Children[1].Children[0].Bits));
        Assert.Equal(5, Decode(wide).Nodes[0].LinkCount);
    }

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
    private static AssetDocument Archive(params ZrdNode[] roots) => FormatRegistry.Default.OpenBytes("ai-test.zbd",
        ResourceReaderTests.Archive(roots.Select(root => ("net_01.zrd", ZrdWriter.Write(root, TestContext.Current.CancellationToken))).ToArray()), token: TestContext.Current.CancellationToken);
    private static AiNetworkSnapshot Read(AssetDocument doc) => MissionAiNetworks.Read(doc.Assets.Select(a => (doc, a)), TestContext.Current.CancellationToken);
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
    private static bool ReusesSnapshot(WeakReference<AssetDocument> archive, WeakReference snapshot) =>
        archive.TryGetTarget(out var doc) && ReferenceEquals(snapshot.Target, Read(doc));
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static (WeakReference<AssetDocument> Archive, WeakReference Snapshot) CachedSnapshot()
    {
        var doc = Archive(A(S("node_00"), Node())); var snapshot = Read(doc);
        Assert.NotNull(Assert.Single(snapshot.Networks).Source);
        return (new(doc), new(snapshot));
    }
    [Fact]
    public void SnapshotDiagnosticsShareOneBudgetAcrossNetworkMembers()
    {
        // Identically named members are valid; each undecodable one reports a diagnostic, but the snapshot keeps one budget.
        var doc = FormatRegistry.Default.OpenBytes("ai-test.zbd", ResourceReaderTests.Archive(Enumerable.Range(0, 1_000).Select(_ => ("net_01.zrd", new byte[] { 9, 9, 9, 9 })).ToArray()), token: TestContext.Current.CancellationToken);
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
    public void DirectedSlotsRetainOrderAndAllNegativeSentinelsWithoutInventingReverseEdges()
    {
        var network = Decode(A(A(S("version"), A(I(105)), S("name"), A(S("Name")), S("type"), A(S("standard")),
            S("path_width"), A(F("7.5")), S("node_00"), Node(2, -7, 2), S("node_02"), Node())));
        // The game follows only the links before a node's first empty slot: the one after it is noted.
        Assert.Contains("link slot 2 follows the empty slot 1", Assert.Single(network.Diagnostics).Message);
        Assert.Equal(7.5f, network.PathWidth); Assert.Equal("standard", network.Type);
        var first = network.Nodes[0]; Assert.Equal(12, first.RawValue); Assert.Equal(new Vector3(1.25f, -2, 3), first.Position);
        Assert.Equal(new[] { 0, 1, 2 }, first.Links.Select(l => l.Slot));
        Assert.Equal(new[] { 2, -7, 2 }, first.Links.Select(l => l.TargetIndex));
        Assert.Equal(network.Nodes[1].Id, first.Links[0].Target); Assert.Equal(first.Links[0].Target, first.Links[2].Target);
        Assert.Null(first.Links[1].Target); Assert.All(network.Nodes[1].Links, l => Assert.Null(l.Target));
    }
    [Fact]
    public void DuplicateIndicesMissingLinksAndUnsupportedKeysRemainDiagnosed()
    {
        var graph = Decode(A(S("node_00"), Node(1, 9, 0), S("node_01"), Node(), S("node_01"), Node(), S("node_2147483648"), Node(), S("node_bad"), Node()));
        Assert.Equal(3, graph.Nodes.Count); Assert.Equal(3, graph.Nodes.Select(n => n.Id).Distinct().Count());
        Assert.Equal("Ambiguous target", graph.Nodes[0].Links[0].Problem); Assert.Equal("Missing target", graph.Nodes[0].Links[1].Problem);
        Assert.Equal(graph.Nodes[0].Id, graph.Nodes[0].Links[2].Target);
        Assert.Contains(graph.Diagnostics, d => d.Message.Contains("node_2147483648")); Assert.Contains(graph.Diagnostics, d => d.Message.Contains("node_bad"));
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
