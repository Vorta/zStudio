using System.IO;
using System.Text;
using System.Text.Json.Nodes;
using Recoil.Zbd.Core;
using Recoil.Zbd.Core.Formats;
using Recoil.Zbd.Core.Sources;
using Recoil.Zbd.Core.Worlds;
using Xunit;

namespace Recoil.Zbd.Tests;

/// <summary>
/// Which nodes a source world's script instructions act on, compared between the shown build and the rebuild after a copy
/// or move in a glTF file (<see cref="SourceObjectEdits.TargetChange"/>).
/// </summary>
public sealed class SourceObjectTargetTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Fact]
    public async Task CopiesOfAnInstanceAreOneNodeWhicheverTheBuildReads()
    {
        using SourceWorldFixture fixture = new();
        fixture.WriteSharedDatabase();
        Script(fixture, "# no vehicles", "FindNode gate\r\nSetLandmark on");
        SourceWorkspace workspace = new(fixture.Project);
        var (build, world) = await BuildAsync(fixture, workspace);
        // Under sgate2, sgate1's copy of the shared node comes after sgate2's, so the build reads that one instead.
        Apply(workspace, SourceObjectEdits.PlanReparent(Target(workspace, build, world, world.Nodes.Single(n => n.Name == "sgate1")), world.Nodes.Single(n => n.Name == "sgate2"), Token));
        var (after, moved) = await BuildAsync(fixture, workspace);
        Assert.NotEqual(Origin(build, world, "gate").ModelNode, Origin(after, moved, "gate").ModelNode);
        // It is the same node, marked by the script as before: the move is no retarget.
        Assert.Equal(0x80u, moved.Nodes.Single(n => n.Name == "gate").Flags & 0x80);
        Assert.Equal("1/0", Origin(after, moved, "gate").Instance?.ToString());
        Assert.Null(SourceObjectEdits.TargetChange(build, after));
    }

    [Fact]
    public async Task AnotherInstanceFoundByNameIsAnotherNode()
    {
        using SourceWorldFixture fixture = new();
        // Two shared nodes, each holding a gate. The build makes shared nodes first, in the order the records reach them,
        // so the script marks the second one's gate, the newest.
        WorldMaterial rock = new() { Texture = new("rock"), Flags = 0x1FF };
        WorldNode Shared() { var shared = Node(""); shared.Children.Add(Node("gate", Quad(rock, 2, 1))); return shared; }
        WorldNode first = Shared(), second = Shared();
        var gates = Enumerable.Range(1, 4).Select(i => Node($"sgate{i}")).ToArray();
        for (int i = 0; i < 4; i++) { var shared = i < 2 ? first : second; gates[i].Children.Add(shared); shared.Parents.Add(gates[i]); }
        WriteDatabase(fixture, [Node("ground", Quad(rock, 64, 0)), .. gates]);
        Script(fixture, "# no vehicles", "FindNode gate\r\nSetLandmark on");
        SourceWorkspace workspace = new(fixture.Project);
        var (build, world) = await BuildAsync(fixture, workspace);
        Assert.Equal("sgate3", Marked(world));
        // Under the ground, sgate3 reaches the second shared node first: the first one's gate is now the newest.
        Apply(workspace, SourceObjectEdits.PlanReparent(Target(workspace, build, world, world.Nodes.Single(n => n.Name == "sgate3")), world.Nodes.Single(n => n.Name == "ground"), Token));
        var (after, moved) = await BuildAsync(fixture, workspace);
        Assert.Equal("sgate1", Marked(moved));
        Assert.Contains("gamegen/m1.gs line 14 would act on gate (data/m1/models/m1.gltf instance 1/0, loaded at gamegen/m1.gs line 11) instead of gate (data/m1/models/m1.gltf instance 2/0", SourceObjectEdits.TargetChange(build, after));
        static string Marked(GameZWorld world) => Ancestors(world.Nodes.Single(n => n.Name == "gate" && (n.Flags & 0x80) != 0)).First(a => a.Name.StartsWith("sgate", StringComparison.Ordinal)).Name;
    }

    [Fact]
    public async Task InstructionsOnNodesTheBuildFreesAreCompared()
    {
        using SourceWorldFixture fixture = new();
        // Two groups named g, which the build frees with the database's root; before that, the script marks the newer.
        WorldMaterial rock = new() { Texture = new("rock"), Flags = 0x1FF };
        WorldNode one = Node("g"), two = Node("g");
        one.Children.Add(Node("a", Quad(rock, 2, 1))); two.Children.Add(Node("b", Quad(rock, 2, 2)));
        WriteDatabase(fixture, [Node("ground", Quad(rock, 64, 0)), one, two], n => n == one || n == two);
        Script(fixture, "DeleteTree m1.flt", "FindNode g\r\nSetLandmark on\r\nDeleteTree m1.flt");
        SourceWorkspace workspace = new(fixture.Project);
        var (build, world) = await BuildAsync(fixture, workspace);
        Assert.DoesNotContain(world.Nodes, n => n.Name == "g");
        // The groups change places in the file, keeping their node numbers: the line marks the other one.
        var gltf = JsonNode.Parse(workspace.Read("data/m1/models/m1.gltf", Token)!)!;
        var roots = gltf["scenes"]![0]!["nodes"]!.AsArray();
        int a = roots[1]!.GetValue<int>(), b = roots[2]!.GetValue<int>();
        roots[1] = b; roots[2] = a;
        Assert.NotNull(workspace.Apply("Swap the groups", [("data/m1/models/m1.gltf", Encoding.UTF8.GetBytes(gltf.ToJsonString()))], Token));
        var (after, _) = await BuildAsync(fixture, workspace);
        Assert.Contains($"gamegen/m1.gs line 13 would act on g (data/m1/models/m1.gltf node {a}, loaded at gamegen/m1.gs line 11) instead of g (data/m1/models/m1.gltf node {b}", SourceObjectEdits.TargetChange(build, after));
        Assert.Null(SourceObjectEdits.TargetChange(build, build));
    }

    [Fact]
    public async Task TheTakeBackMessageIsBounded()
    {
        using SourceWorldFixture fixture = new();
        fixture.WritePartDatabase(secondGround: true);
        // Five lines mark the newest ground, whose glTF node has a very long name (the world's name comes from the extras).
        Script(fixture, "# no vehicles", string.Concat(Enumerable.Repeat("FindNode ground\r\nSetLandmark on\r\n", 5)).TrimEnd());
        var gltf = JsonNode.Parse(File.ReadAllBytes(fixture.Path("data/m1/models/m1.gltf")))!;
        foreach (var node in gltf["nodes"]!.AsArray().Where(n => n!["name"]!.GetValue<string>() == "ground"))
        {
            node!["name"] = new string('x', 5000);
            var extras = node["extras"] as JsonObject ?? (JsonObject)(node["extras"] = new JsonObject());
            var recoil = extras[WorldGltf.Key] as JsonObject ?? (JsonObject)(extras[WorldGltf.Key] = new JsonObject());
            recoil["name"] = "ground";
        }
        fixture.Write("data/m1/models/m1.gltf", Encoding.UTF8.GetBytes(gltf.ToJsonString()));
        SourceWorkspace workspace = new(fixture.Project);
        var (build, world) = await BuildAsync(fixture, workspace);
        var slots = GameZWriter.NodeSlots(world, TestContext.Current.CancellationToken);
        var grounds = world.Nodes.Where(n => n.Name == "ground").ToList();
        var flagged = grounds.Single(g => build.Provenance[slots[g]].Applied.Count > 0);
        Apply(workspace, SourceObjectEdits.PlanReparent(Target(workspace, build, world, grounds.Single(g => !ReferenceEquals(g, flagged))), flagged, Token));
        var (after, _) = await BuildAsync(fixture, workspace);
        string message = SourceObjectEdits.TargetChange(build, after)!;
        // The first three lines, then a count; names are shortened.
        Assert.Contains("gamegen/m1.gs line 14 would act on", message);
        Assert.Contains("; and 2 more instructions:", message);
        Assert.DoesNotContain("line 20 would act on", message);
        Assert.Contains(new string('x', 64) + "…", message);
        Assert.InRange(message.Length, 1, 2048);
    }

    private static async Task<(SourceWorldBuild Build, GameZWorld World)> BuildAsync(SourceWorldFixture fixture, SourceWorkspace workspace)
    {
        var build = await SourceWorlds.BuildPreviewAsync(fixture.Project, "m1", Path.Combine(SourceWorlds.PreviewRoot(fixture.Project), "p-" + Guid.NewGuid().ToString("N")), workspace.Overlay(), token: Token);
        Assert.Null(build.Outputs.FirstOrDefault(o => o.Error != null)?.Error);
        var world = GameZWorldReader.FromDocument(FormatRegistry.Default.OpenBytes("gamez.zbd", await File.ReadAllBytesAsync(build.WorldPath, Token), token: Token), Token);
        return (build, world);
    }
    private static SourceObjectTarget Target(SourceWorkspace workspace, SourceWorldBuild build, GameZWorld world, WorldNode picked)
    {
        Dictionary<WorldNode, WorldNodeProvenance> provenance = new(ReferenceEqualityComparer.Instance);
        foreach (var (node, slot) in GameZWriter.NodeSlots(world, TestContext.Current.CancellationToken)) if (build.Provenance.TryGetValue(slot, out var origin)) provenance[node] = origin;
        return new(workspace, "m1", world, SourceObjectEdits.ObjectOf(picked, provenance), provenance, build.Executions);
    }
    private static WorldNodeProvenance Origin(SourceWorldBuild build, GameZWorld world, string name) => build.Provenance[GameZWriter.NodeSlots(world, TestContext.Current.CancellationToken)[world.Nodes.Single(n => n.Name == name)]];
    private static void Apply(SourceWorkspace workspace, SourceEditPlan plan) => Assert.NotNull(workspace.Apply(plan.Label, plan.Changes.Select(c => (c.Relative, (byte[]?)c.Content)), Token));
    private static void Script(SourceWorldFixture fixture, string line, string replacement)
    {
        string script = Encoding.Latin1.GetString(File.ReadAllBytes(fixture.Path("gamegen/m1.gs")));
        Assert.Contains(line, script);
        fixture.Write("gamegen/m1.gs", script.Replace(line, replacement, StringComparison.Ordinal));
    }
    private static IEnumerable<WorldNode> Ancestors(WorldNode node)
    {
        for (var at = node.Parents.FirstOrDefault(); at != null; at = at.Parents.FirstOrDefault()) yield return at;
    }
    private static void WriteDatabase(SourceWorldFixture fixture, IReadOnlyList<WorldNode> roots, Func<WorldNode, bool>? group = null)
    {
        var (json, bin) = WorldGltf.Export(roots, 0xFF, new() { Texture = t => ($"../textures/{t.Name}.png", 0), Group = group ?? (_ => false) }).Write("m1.bin");
        fixture.Write("data/m1/models/m1.bin", bin); fixture.Write("data/m1/models/m1.gltf", json);
    }
    private static WorldNode Node(string name, WorldModel? model = null)
    {
        WorldNode node = new(name, WorldNodeClass.Object3D) { Model = model, Flags = WorldGltf.DefaultCarried };
        node.SetPayloadInt(0, 0x28);
        return node;
    }
    private static WorldModel Quad(WorldMaterial material, float size, float y)
    {
        ModelBuilder builder = new();
        builder.Add(new([new(0, y, 0), new(size, y, 0), new(size, y, -size), new(0, y, -size)], [new(0, 0), new(1, 0), new(1, 1), new(0, 1)], [], [], material));
        return builder.Finish();
    }
}
