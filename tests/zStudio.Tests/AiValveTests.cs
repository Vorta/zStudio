using System.Text.Json;
using zStudio.Core;
using zStudio.Core.Formats;
using Xunit;

namespace zStudio.Tests;

public sealed class AiValveTests
{
    [Fact]
    public void ExactAssociationsRetainLateNodeAndEdgeAttributesAndUnionTerms()
    {
        var attrs = Enumerable.Range(0, 20).SelectMany(i => new[] { S("valve"), A(I(1), S("node" + i)) }).ToArray();
        var edges = Enumerable.Range(0, 1100).SelectMany(i => new[] { S("valve_assign"), A(S("edge" + i), I(1)) }).ToArray();
        var root = A(S("version"), A(I(106)), S("node_00"), A([I(1), A(F(), F(), F()), A(), ..attrs, S("valveunion"), A(Enumerable.Range(0, 40).Select(i => A(I(1), S("term" + i))).ToArray())]),
            S("node_01"), A(I(1), A(F(), F(), F()), A()), S("node_02"), A([A(I(0), I(1)), ..edges]));
        var graph = Decode("net", "", 0, "net_01.zrd", root);
        var sourceRefs = Records("net_01.zrd", root).SelectMany(r => MissionAiValves.References(r, TestContext.Current.CancellationToken)).Select(r => r.Name).ToArray();
        Assert.Contains("node19", sourceRefs); Assert.Contains("edge1099", sourceRefs); Assert.Contains("term39", sourceRefs);
        var semantic = MissionAiValves.ForNode(graph, graph.Nodes[0]).SelectMany(r => MissionAiValves.References(r, TestContext.Current.CancellationToken)).Select(r => r.Name).ToArray();
        Assert.Contains("node19", semantic); Assert.Contains("term39", semantic); Assert.Contains("edge1099", semantic);
        Assert.Contains(MissionAiValves.ForNode(graph, graph.Nodes[1]), r => r.Value.Children[0].Text == "edge1099");
        Assert.Equal(1024, graph.Constraints.Count);
        foreach (string name in new[] { "node19", "term39", "edge1099" })
            Assert.True(MissionAiValves.HasAssociation(graph, graph.Nodes[0], name));
        Assert.True(MissionAiValves.HasAssociation(graph, graph.Nodes[1], "edge1099"));
        Assert.False(MissionAiValves.HasAssociation(graph, graph.Nodes[1], "term39"));
        Assert.Single(MissionAiValves.EdgeAssignments(graph, "edge1099"));
        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int repeat = 0; repeat < 100; repeat++)
            Assert.False(MissionAiValves.HasAssociation(graph, graph.Nodes[0], "absent"));
        Assert.True(GC.GetAllocatedBytesForCurrentThread() - before < 100_000);

    }
    [Fact]
    public async Task OptionalMw3ValveCorpusKeepsCompleteResourcesAndRecognizesAuthoredFamilies()
    {
        string? root = Environment.GetEnvironmentVariable("ZSTUDIO_MW3_CORPUS"); if (root == null) return;
        var token = TestContext.Current.CancellationToken; HashSet<string> kinds = []; int definitions = 0, networks = 0;
        foreach (string file in Directory.EnumerateFiles(root, "*read*.zbd", SearchOption.AllDirectories))
        {
            var archive = await FormatRegistry.Default.OpenAsync(file, token);
            foreach (var asset in archive.Assets.Where(a => MissionAiValves.IsResource(a.Name)))
            {
                if (asset.Content is not ZrdNode tree) continue;
                Assert.Equal(archive.Slice(asset.Offset, asset.Length).ToArray(), Write(tree));
                var records = Records(asset.Name, tree).ToArray();
                if (asset.Name == "valves.zrd") definitions += records.Length;
                if (MissionAiNetworks.IsCandidate(asset.Name))
                {
                    networks++; var graph = Decode(file + asset.Index, file, asset.Index, asset.Name, tree);
                    Assert.True(graph.IsMw3); Assert.True(graph.Nodes.Count > 0 || records.Length == 0 || records.All(r => r.Kind == "edge"));
                }
                foreach (var record in records)
                {
                    kinds.Add(record.Kind); _ = MissionAiValves.Describe(record, token);
                    if (record.Kind == "definition" && !record.Name.StartsWith(':')) Assert.Null(MissionAiValves.Problem(record, token));
                }
            }
        }
        Assert.Equal(1510, definitions); Assert.True(networks > 100);
        Assert.Equal(new[] { "definition", "edge", "node", "objective" }, kinds.Order());
    }
    [Theory]
    [InlineData("valveunion")]
    [InlineData("valve_assign")]
    [InlineData("set_valve")]
    public void DefinitionNamesDoNotChangeTheirRecordKind(string name)
    {
        var root = A(S(name), A(S("sound"), A(S("sample"), I(1))));
        var record = Assert.Single(Records("valves.zrd", root));
        Assert.Null(MissionAiValves.Problem(record, TestContext.Current.CancellationToken));
    }
    [Fact]
    public void ObjectiveValveNamesAndDescriptionTextAreNotOperations()
    {
        var actionName = S("set_valve");
        var action = A(S("set_valve"), I(1));
        var root = A(S("objective"), A(S("description"), A(S("caption"), S("set_valve"), S("text")), actionName, action));
        var records = Records("objectives.zrd", root).ToArray();
        var record = Assert.Single(records); Assert.Equal(actionName.Id, record.Id);
        Assert.Equal("set_valve", Assert.Single(MissionAiValves.References(record, TestContext.Current.CancellationToken)).Name);
    }
    [Fact]
    public void MalformedDuplicateConstraintsCannotCreateFalseValveAssociations()
    {
        var root = A(S("version"), A(I(106)), S("node_00"), A(I(1), A(F(), F(), F()), A()),
            S("node_01"), A(I(1), A(F(), F(), F()), A()),
            S("node_01"), A(A(I(0), I(1)), S("valve_assign"), A(S("go"), I(1)), S("broken")),
            S("node_02"), A(A(I(0), I(1)), S("valve_assign"), A(S("go"), I(1))));
        var graph = Decode("net", "", 0, "net_01.zrd", root);
        Assert.Contains(1, graph.AmbiguousIndices);
        Assert.Single(Records("net_01.zrd", root));
        Assert.All(graph.Nodes, n => Assert.Empty(MissionAiValves.ForNode(graph, n)));
        var invalid = root with { Children = [..root.Children, S("version"), A(I(105))] };
        Assert.Empty(Records("net_01.zrd", invalid));
    }
    [Fact]
    public void MalformedNodeAttributePairsAreNotNodes()
    {
        // Valid position and links, but a non-string attribute key or a dangling field.
        var root = A(S("version"), A(I(106)), S("node_00"), A(I(1), A(F(), F(), F()), A(), S("valve"), A(I(1), S("go"))),
            S("node_01"), A(I(1), A(F(), F(), F()), A(), I(7), A(S("x"))),
            S("node_02"), A(I(1), A(F(), F(), F()), A(), S("valve"), A(I(1), S("go")), S("dangling")));
        var graph = Decode("net", "", 0, "net_01.zrd", root);
        Assert.Equal(0, Assert.Single(graph.Nodes).Index);
        Assert.Equal(2, graph.Diagnostics.Count(d => d.Message.Contains("attribute pairs", StringComparison.Ordinal)));
        Assert.Single(Records("net_01.zrd", root));
    }
    [Fact]
    public void RecordSearchMatchesReferencedValveNames()
    {
        var network = A(S("version"), A(I(106)), S("node_00"), A(I(1), A(F(), F(), F()), A(), S("valve"), A(I(1), S("gate_open"))),
            S("node_01"), A(I(1), A(F(), F(), F()), A()), S("node_02"), A(A(I(0), I(1)), S("valve_assign"), A(S("bridge_up"), I(1))));
        var records = Records("net_01.zrd", network).ToArray(); Assert.Equal(2, records.Length);
        Assert.Equal(records[0], Assert.Single(records, r => MissionAiValves.MatchesSearch(r, "GATE_OPEN")));
        Assert.Equal(records[1], Assert.Single(records, r => MissionAiValves.MatchesSearch(r, "bridge")));
        var objective = Assert.Single(Records("objectives.zrd", A(S("objective"), A(S("set_valve"), A(S("alarm"), I(1))))));
        Assert.True(MissionAiValves.MatchesSearch(objective, "alarm")); Assert.False(MissionAiValves.MatchesSearch(objective, "absent"));
    }
    [Fact]
    public async Task SemanticValvePropertiesRequireAuthoredValveStructureNotOnlyTheSharedName()
    {
        var token = TestContext.Current.CancellationToken;
        var recoil = A(S("objective"), A(S("text"), S("Destroy the base"), S("kill"), A(S("target"), I(1))));
        var mw3 = A(S("objective"), A(S("text"), S("Open the gate"), S("valve_change"), A(S("gate"), I(1))));
        Assert.False(MissionAiValves.HasSemanticRecords("objectives.zrd", recoil, token));
        Assert.True(MissionAiValves.HasSemanticRecords("objectives.zrd", mw3, token));
        Assert.True(MissionAiValves.HasSemanticRecords("valves.zrd", A(S("go"), A(S("shutdown"), A(S("actor"), I(0), I(1)))), token));
        Assert.False(MissionAiValves.HasSemanticRecords("valves.zrd", A(), token));
        Assert.True(MissionAiValves.HasSemanticRecords("net_01.zrd", A(S("version"), A(I(106))), token));
        Assert.False(MissionAiValves.HasSemanticRecords("net_01.zrd", A(S("version"), A(I(105))), token));
        Assert.False(MissionAiValves.HasSemanticRecords("sounds.zrd", mw3, token));
        string? corpus = Environment.GetEnvironmentVariable("ZSTUDIO_CORPUS"); if (string.IsNullOrEmpty(corpus)) return;
        int objectives = 0;
        foreach (string file in Directory.EnumerateFiles(corpus, "zrdr.zbd", SearchOption.AllDirectories))
        {
            var archive = await FormatRegistry.Default.OpenAsync(file, token);
            foreach (var asset in archive.Assets.Where(a => MissionAiValves.IsResource(a.Name) && a.Content is ZrdNode))
            { objectives += asset.Name == "objectives.zrd" ? 1 : 0; Assert.False(MissionAiValves.HasSemanticRecords(asset.Name, (ZrdNode)asset.Content!, token), file + " " + asset.Name); }
        }
        Assert.True(objectives > 0);
    }
    private static IEnumerable<AiValveRecord> Records(string name, ZrdNode root) => MissionAiValves.Records(name, root, TestContext.Current.CancellationToken);
    private static IEnumerable<AiValveReference> References(AiValveRecord record) => MissionAiValves.References(record, TestContext.Current.CancellationToken);
    private static byte[] Write(ZrdNode root) => ZrdWriter.Write(root, TestContext.Current.CancellationToken);
    private static AiNetwork Decode(string id, string path, int member, string name, ZrdNode root) => MissionAiNetworks.Decode(id, path, member, name, root, TestContext.Current.CancellationToken);
    private static ZrdNode S(string text) => ZrdNode.Create(ZrdKind.String, JsonSerializer.Serialize(text));
    private static ZrdNode I(int n) => ZrdNode.Create(ZrdKind.Int, n.ToString(System.Globalization.CultureInfo.InvariantCulture));
    private static ZrdNode F() => ZrdNode.Create(ZrdKind.Float, "0");
    private static ZrdNode A(params ZrdNode[] children) => ZrdNode.Create(ZrdKind.Array) with { Children = children };
    [Fact]
    public void OrderedRepeatedTriggersCompoundInputsAndUnknownBlocksKeepTheirIdentities()
    {
        var root = A(A(S("go"), A(S("shutdown"), A(S("actor"), I(0), I(1)), S("delayupdate"), A(S("later"), F())),
            S("go"), A(S("sound"), A(S("sample"), I(1))), S("later"), A(S("all_nonzero"), S("namelist"), A(S("go"), S("external"))), S(":disabled"), A(S(":"))));
        var bytes = Write(root); var rows = Records("valves.zrd", root).ToArray();
        Assert.Equal(4, rows.Length); Assert.NotEqual(rows[0].Id, rows[1].Id);
        Assert.Equal(new[] { "go", "later" }, References(rows[0]).Select(r => r.Name));
        Assert.Equal(new[] { "later", "go", "external" }, References(rows[2]).Select(r => r.Name));
        Assert.Null(MissionAiValves.Problem(rows[0], TestContext.Current.CancellationToken)); Assert.NotNull(MissionAiValves.Problem(rows[3], TestContext.Current.CancellationToken));
        Assert.Equal(bytes, Write(root));
    }
    [Fact]
    public void LargeMw3IndicesBindSpatialAndConstraintRecordsIndependently()
    {
        var root = A(S("version"), A(I(106)), S("node_742"), A(I(1), A(F(), F(), F()), A(I(100)), S("valve"), A(I(1), S("go"))),
            S("node_100"), A(I(1), A(F(), F(), F()), A(I(742))), S("node_742"), A(A(I(742), I(100)), S("valve_assign"), A(S("done"), I(1))));
        var graph = Decode("net", "test", 0, "net_01.zrd", root);
        Assert.Equal(2, graph.Nodes.Count); Assert.Single(graph.Constraints); Assert.Empty(graph.Diagnostics);
        Assert.Equal(graph.Nodes[1].Id, graph.Nodes[0].Links[0].Target);
        var records = Records("net_01.zrd", root).ToArray();
        Assert.Equal(new[] { "node", "edge" }, records.Select(r => r.Kind));
        Assert.Equal(742, records[0].NodeIndex); Assert.Equal(742, records[1].From);
        Assert.Equal("go", Assert.Single(References(records[0])).Name);
        var recoil = root with { Children = [S("version"), A(I(105)), ..root.Children.Skip(2)] };
        Assert.Empty(Decode("net", "test", 0, "net_01.zrd", recoil).Nodes);
        Assert.Empty(Records("net_01.zrd", recoil));
    }
    [Fact]
    public void ConstraintAccumulationAndParameterExpansionAreBoundedBeforePublication()
    {
        var attributes = Enumerable.Range(0, 50_000).SelectMany(_ => new[] { S("valve_assign"), A(S(new string('x', 1024)), I(1)) }).ToArray();
        var root = A(S("version"), A(I(106)), S("node_123"), A([A(I(0), I(1)), ..attributes]));
        long before = GC.GetAllocatedBytesForCurrentThread();
        var graph = Decode("net", "test", 0, "net_01.zrd", root);
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        Assert.Equal(50_000, graph.ConstraintCount); Assert.Equal(1024, graph.Constraints.Count);
        Assert.True(allocated < 16_000_000, $"Allocated {allocated:N0} bytes");
        Assert.Equal(50_000, Records("net_01.zrd", root).Count());
    }
}
