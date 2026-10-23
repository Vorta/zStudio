using System.Text.Json;
using System.Text.Json.Nodes;
using zStudio.Core.Sources;
using zStudio.Core.Worlds;
using Xunit;

namespace zStudio.Tests;

public sealed class InstanceZoneLookupTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Fact]
    public void ManyMarksVisitProvenanceOnceAndRetainExactFileAndAmbiguitySemantics()
    {
        Dictionary<WorldNode, WorldNodeProvenance> source = [];
        for (int i = 0; i < 100; i++) Add("m.gltf", i, 0xFFFFFF03);
        Add("M.GLTF", 0, 3); // Case-insensitive file and masked-zone equality agree.
        Add("m.gltf", 1, 4); Add("m.gltf", 1, 3); // A later equal value cannot undo ambiguity.
        Add("other.gltf", 2, 9);
        source.Add(new("nested", WorldNodeClass.Object3D) { Zone = 8 }, new()
        { ModelFile = "m.gltf", Instance = new(2, new(99)) });
        var lookup = new SourceObjectEdits.InstanceZoneLookup(source, "m.gltf", Token, 10000);
        Assert.Equal(3u, lookup.Find(0)); long cold = lookup.Used;
        Assert.Null(lookup.Find(1)); Assert.Equal(3u, lookup.Find(2));
        for (int i = 3; i < 100; i++) Assert.Equal(3u, lookup.Find(i));
        Assert.Null(lookup.Find(long.MaxValue)); Assert.Null(lookup.Find(-1));
        Assert.Equal(101, lookup.Used - cold); // One charged query each; no per-mark rescan.
        Assert.InRange(cold, 100, 10000);

        // A source node is identified by its physical owner and exact reference occurrence. Cached copies
        // that differ remain ambiguous; another placement of the same physical model remains independent.
        const string owner = "data/m1/models/m1.gltf", model = "m.gltf";
        foreach (var (node, zone) in new[] { (4, 0xFFFFFF05u), (4, 5u), (6, 5u), (6, 0xFFu) })
            source.Add(new("copy", WorldNodeClass.Object3D) { Zone = zone }, new()
            { ModelFile = model, ZoneOwnerPath = owner, ZoneOwnerReferences = [2, 3], ModelNode = node });
        source.Add(new("other placement", WorldNodeClass.Object3D) { Zone = 9 }, new()
        { ModelFile = model, ZoneOwnerPath = owner, ZoneOwnerReferences = [2, 4], ModelNode = 4 });
        var nodes = new SourceObjectEdits.InstanceZoneLookup(source, "m.gltf", Token);
        Assert.Equal(5u, nodes.FindNode("DATA/m1/models/m1.gltf", "M.GLTF", [2, 3], 4));
        Assert.Null(nodes.FindNode(owner, model, [2, 3], 6));
        Assert.Equal(9u, nodes.FindNode(owner, model, [2, 4], 4));
        Assert.Null(nodes.FindNode(owner, model, [], 4));
        Assert.Null(nodes.FindNode("data/m2/models/m2.gltf", model, [2, 3], 4));
        void Add(string file, int mark, uint zone) => source.Add(new("node", WorldNodeClass.Object3D) { Zone = zone },
            new() { ModelFile = file, Instance = new(mark) });
    }

    [Fact]
    public void RefusalAndCancellationCannotExposePartialMapsOrModifyProvenance()
    {
        Dictionary<WorldNode, WorldNodeProvenance> source = [];
        for (int i = 0; i < 20; i++) source.Add(new("node", WorldNodeClass.Object3D) { Zone = (uint)i },
            new() { ModelFile = "m.gltf", Instance = new(i) });
        uint[] before = source.Keys.Select(n => n.Zone).ToArray();
        var small = new SourceObjectEdits.InstanceZoneLookup(source, "m.gltf", Token, 100);
        Assert.Throws<InvalidDataException>(() => small.Find(0));
        Assert.Throws<InvalidDataException>(() => small.Find(0));
        using var cancel = CancellationTokenSource.CreateLinkedTokenSource(Token); cancel.Cancel();
        var canceled = new SourceObjectEdits.InstanceZoneLookup(source, "m.gltf", cancel.Token);
        Assert.ThrowsAny<OperationCanceledException>(() => canceled.Find(0));
        var retry = new SourceObjectEdits.InstanceZoneLookup(source, "m.gltf", Token);
        Assert.Equal(0u, retry.Find(0)); Assert.Equal(19u, retry.Find(19));
        Assert.Equal(before, source.Keys.Select(n => n.Zone));
    }

    [Fact]
    public async Task ImportRecordsWhichNodeSuppliedAnInheritedZone()
    {
        using SourceWorldFixture fixture = new();
        const string path = "data/m1/models/m1.gltf";
        static JsonObject Zone(JsonNode zone) => new() { ["recoil"] = new JsonObject { ["zone"] = zone } };
        JsonArray nodes =
        [
            new JsonObject { ["name"] = "group_a", ["children"] = new JsonArray(1), ["extras"] = Zone(7) },
            new JsonObject { ["name"] = "inner", ["children"] = new JsonArray(2) },
            new JsonObject { ["name"] = "leaf", ["extras"] = Zone("inherit") },
            new JsonObject { ["name"] = "loose" },
            new JsonObject { ["name"] = "own", ["extras"] = Zone("any") },
            // A shared node first defined as a root keeps the load root's zone, although a later root holds a copy.
            new JsonObject { ["name"] = "shared", ["extras"] = new JsonObject { ["recoil"] = new JsonObject { ["instance"] = 1 } } },
            new JsonObject { ["name"] = "holder", ["children"] = new JsonArray(7), ["extras"] = Zone(3) },
            new JsonObject { ["name"] = "shared", ["extras"] = new JsonObject { ["recoil"] = new JsonObject { ["instance"] = 1 } } },
        ];
        fixture.Write(path, JsonSerializer.SerializeToUtf8Bytes(new JsonObject { ["asset"] = new JsonObject { ["version"] = "2.0" }, ["scene"] = 0,
            ["scenes"] = new JsonArray(new JsonObject { ["nodes"] = new JsonArray(0, 3, 4, 5, 6) }), ["nodes"] = nodes }));
        var build = await SourceWorlds.LoadSceneAsync(fixture.Project, "m1", new SourceWorkspace(fixture.Project).Overlay(), token: Token);
        Assert.NotNull(build.Scene);
        // Database groups are freed by the build; their provenance still names the supplier.
        var origins = build.Provenance.Values.Concat(build.BuildState.Freed).Where(p => p.ModelFile == path).ToDictionary(p => p.ModelNodeName!);
        Assert.False(origins["group_a"].ImportedZoneInherited);
        Assert.Same(origins["group_a"], origins["inner"].ImportedZoneSupplier);
        Assert.Same(origins["group_a"], origins["leaf"].ImportedZoneSupplier);
        Assert.Equal(7u, origins["leaf"].ImportedZoneWord & 255);
        Assert.True(origins["loose"].ImportedZoneInherited);
        Assert.Equal(path, origins["loose"].ImportedZoneSupplier!.LoadedFile);
        Assert.False(origins["own"].ImportedZoneInherited);
        Assert.Null(origins["own"].ImportedZoneSupplier);
        Assert.Equal(255u, origins["own"].ImportedZoneWord & 255);
        Assert.Equal(path, origins["shared"].ImportedZoneSupplier!.LoadedFile);
        Assert.Equal(255u, origins["shared"].ImportedZoneWord & 255);
    }

    [Theory]
    [InlineData("delete")]
    [InlineData("move")]
    [InlineData("copy")]
    public async Task SharedDatabaseEditsKeepFirstReadZonesThroughActualBuildAndUndo(string edit)
    {
        using SourceWorldFixture fixture = new();
        const string path = "data/m1/models/m1.gltf";
        const int count = 8;
        JsonArray nodes = []; JsonArray first = [], second = [];
        nodes.Add(new JsonObject { ["name"] = "holder_a", ["children"] = first });
        nodes.Add(new JsonObject { ["name"] = "holder_b", ["children"] = second,
            ["extras"] = new JsonObject { ["recoil"] = new JsonObject { ["zone"] = 3 } } });
        nodes.Add(new JsonObject { ["name"] = "holder_c" });
        for (int i = 0; i < count; i++)
        {
            first.Add(nodes.Count); nodes.Add(Leaf(i)); second.Add(nodes.Count); nodes.Add(Leaf(i));
        }
        JsonObject model = new() { ["asset"] = new JsonObject { ["version"] = "2.0" }, ["scene"] = 0,
            ["scenes"] = new JsonArray(new JsonObject { ["nodes"] = new JsonArray(0, 1, 2) }), ["nodes"] = nodes };
        byte[] original = JsonSerializer.SerializeToUtf8Bytes(model); fixture.Write(path, original);
        SourceWorkspace workspace = new(fixture.Project);
        var (build, world) = await Build();
        var slots = GameZWriter.NodeSlots(world, TestContext.Current.CancellationToken);
        Dictionary<WorldNode, WorldNodeProvenance> origins = [];
        foreach (var (node, slot) in slots) if (build.Provenance.TryGetValue(slot, out var p)) origins.Add(node, p);
        var holder = world.Nodes.Single(n => n.Name == "holder_a");
        var target = new SourceObjectTarget(workspace, "m1", world, holder, origins, build.Executions);
        Assert.Equal(count, world.Nodes.Count(n => n.Name.StartsWith("leaf", StringComparison.Ordinal)));
        Assert.All(world.Nodes.Where(n => n.Name.StartsWith("leaf", StringComparison.Ordinal)), n => Assert.Equal(255u, n.Zone & 255));
        var plan = edit switch
        {
            "delete" => SourceObjectEdits.PlanDelete(target, Token),
            "move" => SourceObjectEdits.PlanReparent(target, world.Nodes.Single(n => n.Name == "holder_c"), Token),
            _ => SourceObjectEdits.PlanDuplicate(target, "holder_copy", null, Token)
        };
        Assert.Single(plan.Changes); Assert.Equal(original, workspace.Read(path, Token));
        Assert.NotNull(workspace.Apply(plan.Label, plan.Changes.Select(c => (c.Relative, (byte[]?)c.Content)), Token));
        (_, var after) = await Build();
        Assert.Equal(edit == "copy" ? 2 * count : count, after.Nodes.Count(n => n.Name.StartsWith("leaf", StringComparison.Ordinal)));
        Assert.All(after.Nodes.Where(n => n.Name.StartsWith("leaf", StringComparison.Ordinal)), n => Assert.Equal(255u, n.Zone & 255));
        if (edit == "delete") Assert.DoesNotContain(after.Nodes, n => n.Name == "holder_a");
        if (edit == "move") Assert.Equal("holder_c", Assert.Single(after.Nodes.Single(n => n.Name == "holder_a").Parents).Name);
        workspace.Undo(); Assert.Equal(original, workspace.Read(path, Token));

        static JsonObject Leaf(int mark) => new() { ["name"] = "leaf" + mark,
            ["extras"] = new JsonObject { ["recoil"] = new JsonObject { ["instance"] = mark + 1 } } };
        async Task<(SourceSceneState, GameZWorld)> Build()
        {
            var result = await SourceWorlds.LoadSceneAsync(fixture.Project, "m1", workspace.Overlay(), token: Token);
            Assert.NotNull(result.Scene);
            var parsed = result.World;
            return (result.BuildState, parsed);
        }
    }
}
