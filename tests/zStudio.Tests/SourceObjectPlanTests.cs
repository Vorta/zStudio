using System.IO;
using System.Numerics;
using System.Text;
using System.Text.Json.Nodes;
using Recoil.Zbd.Core;
using Recoil.Zbd.Core.Formats;
using Recoil.Zbd.Core.Sources;
using Recoil.Zbd.Core.Worlds;
using Xunit;

namespace Recoil.Zbd.Tests;

/// <summary>
/// What a source-world object edit reaches beyond the edited node: every copy of an instance, every load of a model file,
/// lighting and levels of detail along the hierarchy, groups the build deletes, and scripts other missions run.
/// </summary>
public sealed class SourceObjectPlanTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    private static async Task<(SourceWorldBuild Build, GameZWorld World)> BuildAsync(SourceWorldFixture fixture, SourceWorkspace workspace, string mission)
    {
        var build = await SourceWorlds.BuildPreviewAsync(fixture.Project, mission, Path.Combine(SourceWorlds.PreviewRoot(fixture.Project), "p-" + Guid.NewGuid().ToString("N")), workspace.Overlay(), token: Token);
        Assert.Null(build.Outputs.FirstOrDefault(o => o.Error != null)?.Error);
        var world = GameZWorldReader.FromDocument(FormatRegistry.Default.OpenBytes("gamez.zbd", await File.ReadAllBytesAsync(build.WorldPath, Token), token: Token), Token);
        return (build, world);
    }
    private static SourceObjectTarget Target(SourceWorkspace workspace, string mission, SourceWorldBuild build, GameZWorld world, string name) =>
        Target(workspace, mission, build, world, world.Nodes.Single(n => n.Name == name));
    private static SourceObjectTarget Target(SourceWorkspace workspace, string mission, SourceWorldBuild build, GameZWorld world, WorldNode picked)
    {
        Dictionary<WorldNode, WorldNodeProvenance> provenance = new(ReferenceEqualityComparer.Instance);
        foreach (var (node, slot) in GameZWriter.NodeSlots(world, TestContext.Current.CancellationToken)) if (build.Provenance.TryGetValue(slot, out var origin)) provenance[node] = origin;
        return new(workspace, mission, world, SourceObjectEdits.ObjectOf(picked, provenance), provenance, build.Executions) { Write = build.WriteInstruction };
    }
    private static WorldNodeProvenance Origin(SourceWorldBuild build, GameZWorld world, WorldNode node) => build.Provenance[GameZWriter.NodeSlots(world, TestContext.Current.CancellationToken)[node]];
    private static void Apply(SourceWorkspace workspace, SourceEditPlan plan) => Assert.NotNull(workspace.Apply(plan.Label, plan.Changes.Select(c => (c.Relative, (byte[]?)c.Content)), Token));
    private static string Text(SourceWorkspace workspace, string path) => Encoding.Latin1.GetString(workspace.Read(path, Token)!);
    /// <summary>Adds script lines right before m1's world is written.</summary>
    private static void BeforeWrite(SourceWorkspace workspace, string lines) =>
        workspace.Apply("Lines", [("gamegen/m1.gs", Encoding.Latin1.GetBytes(Text(workspace, "gamegen/m1.gs").Replace("GameZWriteZBDFile", lines + "\r\nGameZWriteZBDFile", StringComparison.Ordinal)))], Token);
    private static string Message(Action plan) => Assert.Throws<InvalidDataException>(plan).Message;

    private static readonly WorldMaterial Rock = new() { Texture = new("rock"), Flags = 0x1FF };
    private static WorldNode Node(string name, WorldModel? model = null, WorldNodeClass kind = WorldNodeClass.Object3D, uint zone = 0)
    {
        WorldNode node = new(name, kind) { Model = model, Flags = WorldGltf.DefaultCarried, Zone = zone };
        node.SetPayloadInt(0, 0x28);
        return node;
    }
    private static WorldModel Quad(float size, float y)
    {
        ModelBuilder builder = new();
        builder.Add(new([new(0, y, 0), new(size, y, 0), new(size, y, -size), new(0, y, -size)], [new(0, 0), new(1, 0), new(1, 1), new(0, 1)], [], [], Rock));
        return builder.Finish();
    }
    /// <summary>A reference to the part <c>m1_01.gltf</c>, as the database writes one (a group).</summary>
    private static WorldNode Reference(uint zone = 0) { var r = Node("m1_01.flt", zone: zone); r.Children.Add(Node("placeholder")); return r; }
    /// <summary>Writes m1's database (and with <paramref name="part"/> its part m1_01.gltf); <paramref name="edit"/> changes the database's JSON.</summary>
    private static void Database(SourceWorldFixture fixture, IReadOnlyList<WorldNode> roots, IReadOnlyList<WorldNode>? part = null, ISet<WorldNode>? groups = null, Action<JsonNode>? edit = null, Action<JsonNode>? editPart = null)
    {
        if (part != null)
        {
            var (partJson, partBin) = WorldGltf.Export(part, 0xFF, new() { Texture = t => ($"../textures/{t.Name}.png", 0) }).Write("m1_01.bin");
            var partNode = JsonNode.Parse(partJson)!; editPart?.Invoke(partNode);
            fixture.Write("data/m1/models/m1_01.bin", partBin); fixture.Write("data/m1/models/m1_01.gltf", partNode.ToJsonString());
        }
        bool IsReference(WorldNode n) => n.Name == "m1_01.flt";
        var (json, bin) = WorldGltf.Export(roots, 0xFF, new()
        {
            Texture = t => ($"../textures/{t.Name}.png", 0),
            Reference = n => IsReference(n) ? "m1_01.gltf" : null, Content = n => IsReference(n) ? [.. n.Children] : null,
            Group = n => IsReference(n) || groups?.Contains(n) == true,
        }).Write("m1.bin");
        var database = JsonNode.Parse(json)!; edit?.Invoke(database);
        fixture.Write("data/m1/models/m1.bin", bin); fixture.Write("data/m1/models/m1.gltf", database.ToJsonString());
    }
    private static JsonObject Named(JsonNode file, string name) => file["nodes"]!.AsArray().Select(n => n!.AsObject()).First(n => n["name"]?.GetValue<string>() == name);
    /// <summary>Two gates sharing one node, holder (an instance), which holds <paramref name="inside"/>.</summary>
    private static WorldNode[] SharedGates(WorldNode inside)
    {
        var holder = Node("holder"); holder.Children.Add(inside);
        var gates = new[] { Node("sgate1"), Node("sgate2") };
        foreach (var g in gates) { g.Children.Add(holder); holder.Parents.Add(g); }
        return gates;
    }

    [Fact]
    public async Task EditsInsideAnInstanceChangeEveryCopy()
    {
        using SourceWorldFixture fixture = new();
        fixture.WriteSharedDatabase();
        SourceWorkspace workspace = new(fixture.Project);
        var (build, world) = await BuildAsync(fixture, workspace, "m1");
        // gate stands in both copies of the shared node: a move changes it in both, as do a flag of the shared node, a copy
        // of gate beside itself and a deletion of gate.
        var gate = Target(workspace, "m1", build, world, "gate");
        var moved = SourceObjectEdits.PlanTransform(workspace, "gate", gate.Origin, build.Executions, new(new(0, 5, 0), Vector3.Zero, Vector3.One), Token, "m1", ObjectTransform.Of(gate.Node));
        Assert.Contains(moved.Notes, n => n.Contains("each of its 2 copies", StringComparison.Ordinal));
        Apply(workspace, moved);
        (build, world) = await BuildAsync(fixture, workspace, "m1");
        var shared = Assert.Single(world.Nodes.Single(n => n.Name == "gate").Parents);
        Apply(workspace, SourceObjectEdits.PlanFlag(workspace, "", Origin(build, world, shared), build.Executions, 0x80, true, Token, "m1"));
        (build, world) = await BuildAsync(fixture, workspace, "m1");
        Apply(workspace, SourceObjectEdits.PlanDuplicate(Target(workspace, "m1", build, world, "gate"), "gate2", null, Token));
        (build, world) = await BuildAsync(fixture, workspace, "m1");
        Assert.Same(world.Nodes.Single(n => n.Name == "gate").Parents[0], Assert.Single(world.Nodes.Single(n => n.Name == "gate2").Parents));
        Apply(workspace, SourceObjectEdits.PlanDelete(Target(workspace, "m1", build, world, "gate"), Token));
        (build, world) = await BuildAsync(fixture, workspace, "m1");
        Assert.DoesNotContain(world.Nodes, n => n.Name == "gate");
        // Without sgate1 the build reads the second copy: it holds the same edits (gate2, moved with gate, and the flag).
        Apply(workspace, SourceObjectEdits.PlanDelete(Target(workspace, "m1", build, world, "sgate1"), Token));
        (_, world) = await BuildAsync(fixture, workspace, "m1");
        Assert.DoesNotContain(world.Nodes, n => n.Name == "gate");
        var copy = world.Nodes.Single(n => n.Name == "gate2");
        Assert.Equal(new Vector3(0, 5, 0), WorldUpdate.LocalMatrix(copy)!.Value.Translation);
        Assert.Equal("sgate2", Assert.Single(Assert.Single(copy.Parents).Parents).Name);
        Assert.NotEqual(0u, copy.Parents[0].Flags & 0x80);
        // The shared node itself leaves every gate.
        (build, world) = await BuildAsync(fixture, workspace, "m1");
        for (int i = 0; i < 5; i++) workspace.Undo();
        (build, world) = await BuildAsync(fixture, workspace, "m1");
        var holder = Assert.Single(world.Nodes.Single(n => n.Name == "gate").Parents);
        Assert.Equal(2, holder.Parents.Count);
        Apply(workspace, SourceObjectEdits.PlanDelete(Target(workspace, "m1", build, world, holder), Token));
        (_, world) = await BuildAsync(fixture, workspace, "m1");
        Assert.DoesNotContain(world.Nodes, n => n.Name == "gate");
        Assert.All(world.Nodes.Where(n => n.Name.StartsWith("sgate", StringComparison.Ordinal)), g => Assert.Empty(g.Children));

        // Copies an editor made differ are not edited as one: their nodes would not stand for each other.
        using SourceWorldFixture differing = new();
        differing.WriteSharedDatabase();
        var file = JsonNode.Parse(File.ReadAllText(differing.Path("data/m1/models/m1.gltf")))!;
        file["nodes"]!.AsArray().Select(n => n!.AsObject()).Last(n => n["name"]?.GetValue<string>() == "gate")["translation"] = new JsonArray(0f, 1f, 0f);
        differing.Write("data/m1/models/m1.gltf", file.ToJsonString());
        workspace = new(differing.Project);
        (build, world) = await BuildAsync(differing, workspace, "m1");
        gate = Target(workspace, "m1", build, world, "gate");
        Assert.Contains("differ", Message(() => SourceObjectEdits.PlanDelete(gate, Token)));
    }

    [Fact]
    public async Task PartsAnInstanceHoldsMoveWithinThemAndNothingMovesIntoAHiddenInstance()
    {
        using SourceWorldFixture fixture = new();
        // A part referenced from inside the shared node holder: the build reads it once, and every placement of holder shows it.
        var crate = Node("crate", Quad(4, 1)); crate.Children.Add(Node("lid", Quad(4, 2)));
        Database(fixture, [Node("ground", Quad(64, 0)), .. SharedGates(Reference())], part: [crate, Node("post", Quad(1, 0))]);
        SourceWorkspace workspace = new(fixture.Project);
        var (build, world) = await BuildAsync(fixture, workspace, "m1");
        var lid = world.Nodes.Single(n => n.Name == "lid");
        Assert.True(Origin(build, world, lid).Part);
        Assert.Equal(2, world.Nodes.Single(n => n.Name == "holder").Parents.Count);
        // The part file holds one copy, so a move within it keeps every placement.
        Apply(workspace, SourceObjectEdits.PlanReparent(Target(workspace, "m1", build, world, lid), world.Nodes.Single(n => n.Name == "post"), Token));
        (build, world) = await BuildAsync(fixture, workspace, "m1");
        Assert.Equal("post", Assert.Single(world.Nodes.Single(n => n.Name == "lid").Parents).Name);
        Assert.Equal(2, world.Nodes.Single(n => n.Name == "holder").Parents.Count);

        // A script takes holder out of sgate2: the world shows it under sgate1 only, while the file still holds both copies.
        using SourceWorldFixture hidden = new();
        Database(hidden, [Node("ground", Quad(64, 0)), .. SharedGates(Node("gate", Quad(2, 1)))]);
        hidden.Write("gamegen/m1.gs", Encoding.Latin1.GetString(File.ReadAllBytes(hidden.Path("gamegen/m1.gs"))).Replace("# no vehicles", "FindNode sgate2\r\nDeleteChild holder", StringComparison.Ordinal));
        workspace = new(hidden.Project);
        (build, world) = await BuildAsync(hidden, workspace, "m1");
        Assert.Single(world.Nodes.Single(n => n.Name == "holder").Parents);
        Assert.Contains("one copy only", Message(() => SourceObjectEdits.PlanReparent(Target(workspace, "m1", build, world, "ground"), world.Nodes.Single(n => n.Name == "gate"), Token)));
    }

    [Fact]
    public async Task TransformsOfAModelLoadedTwiceRespectEachLoadsScript()
    {
        using SourceWorldFixture fixture = new();
        SourceWorkspace workspace = new(fixture.Project);
        SourceWorlds.AddModel(workspace, "m1", new(new(fixture.Tank, "tank_a", new(100, 0, -50)), []), Token);
        SourceWorlds.AddModel(workspace, "m1", new(new(fixture.Tank, "tank_b", new(0, 0, 30)), []), Token);
        // A script turns tank_b's hull; tank.gltf gives the hull no transform of its own, so the turn applies.
        BeforeWrite(workspace, "FindNode tank_b\r\nFindSubNode hull\r\nObject3DRotate 0.0 45.0 0.0");
        var (build, world) = await BuildAsync(fixture, workspace, "m1");
        var hulls = world.Nodes.Where(n => n.Name == "hull").Select(n => Origin(build, world, n)).ToList();
        Assert.Equal(2, hulls.Count);
        var plain = hulls.Single(h => h.Writers.Count == 0);
        // Turning tank_a's hull writes tank.gltf, which every load reads: tank_b's hull would take that authored matrix and
        // drop its script turn.
        Assert.Contains("another load of hull", Message(() => SourceObjectEdits.PlanTransform(workspace, "hull", plain, build.Executions, new(Vector3.Zero, new(0, 30, 0), Vector3.One), Token, "m1", ObjectTransform.Identity,
            SourceObjectEdits.CopiesOf(plain, build.Provenance.Values))));
    }

    [Fact]
    public async Task MovesKeepTheLightingScriptsGiveAndTellLevelsOfDetail()
    {
        using SourceWorldFixture fixture = new();
        fixture.WriteNestedDatabase();
        // NodeSetLighting lights the crate and its lid as the build has them: moved out, the lid would no longer be lit.
        fixture.Write("gamegen/m1.gs", Encoding.Latin1.GetString(File.ReadAllBytes(fixture.Path("gamegen/m1.gs"))).Replace("# no vehicles", "FindNode crate\r\nNodeSetLighting on", StringComparison.Ordinal));
        SourceWorkspace workspace = new(fixture.Project);
        var (build, world) = await BuildAsync(fixture, workspace, "m1");
        Assert.Contains("NodeSetLighting", Message(() => SourceObjectEdits.PlanReparent(Target(workspace, "m1", build, world, "lid"), null, Token)));
        Assert.Contains("NodeSetLighting", Message(() => SourceObjectEdits.PlanReparent(Target(workspace, "m1", build, world, "ground"), world.Nodes.Single(n => n.Name == "crate"), Token)));
        // A script object attached again at the end would miss a NodeSetLighting of its old parent too.
        SourceWorlds.AddModel(workspace, "m1", new(new(fixture.Tank, "tank_at", new(100, 0, -50)), []), Token);
        BeforeWrite(workspace, "FindNode world\r\nNodeSetLighting on");
        (build, world) = await BuildAsync(fixture, workspace, "m1");
        Assert.Contains("NodeSetLighting", Message(() => SourceObjectEdits.PlanReparent(Target(workspace, "m1", build, world, "tank_at"), world.Nodes.Single(n => n.Name == "ground"), Token)));
    }

    [Fact]
    public async Task LevelsOfDetailKeepTheirPlaceAsTheFileHasIt()
    {
        using SourceWorldFixture fixture = new();
        // a and b, a hundredth of the world's size, differ by 0.9%; c is an ordinary object.
        var lod = Node("lod", kind: WorldNodeClass.Lod); lod.Children.Add(Node("inner", Quad(4, 1)));
        var a = Node("a"); a.Children.Add(lod);
        Database(fixture, [Node("ground", Quad(64, 0)), a, Node("b"), Node("c", Quad(2, 0))], edit: json =>
        {
            Named(json, "a")["scale"] = new JsonArray(0.01f, 0.01f, 0.01f);
            Named(json, "b")["scale"] = new JsonArray(0.01009f, 0.01009f, 0.01009f);
        });
        SourceWorkspace workspace = new(fixture.Project);
        var (build, world) = await BuildAsync(fixture, workspace, "m1");
        Assert.Equal(WorldNodeClass.Lod, world.Nodes.Single(n => n.Name == "lod").Class);
        // Relative to the parents' own size, b does not stand where a does.
        Assert.Contains("level-of-detail", Message(() => SourceObjectEdits.PlanReparent(Target(workspace, "m1", build, world, "lod"), world.Nodes.Single(n => n.Name == "b"), Token)));
        // An object moved under the level-of-detail node draws only within its range, which the plan says.
        var under = SourceObjectEdits.PlanReparent(Target(workspace, "m1", build, world, "c"), world.Nodes.Single(n => n.Name == "lod"), Token);
        Assert.Contains(under.Notes, n => n.Contains("distance range of lod", StringComparison.Ordinal));

        // A part referenced twice, once by a group under an object far from the world's origin: in the part's file both
        // copies are alike, so the level-of-detail node of the far copy moves to the top of the part, or under q (found
        // by name in the near copy, as Properties may), and not under q2, which stands elsewhere in the file.
        using SourceWorldFixture parts = new();
        var partLod = Node("plod", kind: WorldNodeClass.Lod); partLod.Children.Add(Node("pinner", Quad(4, 1)));
        var p0 = Node("p0"); p0.Children.Add(partLod);
        var far = Node("far"); far.Children.Add(Reference());
        Database(parts, [Node("ground", Quad(64, 0)), Reference(), far], part: [p0, Node("q"), Node("q2")],
            edit: json => Named(json, "far")["translation"] = new JsonArray(100f, 0f, 0f), editPart: json => Named(json, "q2")["translation"] = new JsonArray(5f, 0f, 0f));
        workspace = new(parts.Project);
        (build, world) = await BuildAsync(parts, workspace, "m1");
        var lods = world.Nodes.Where(n => n.Name == "plod").ToList();
        Assert.Equal(2, lods.Count);
        static bool IsFar(WorldNode n) { for (var at = n; at != null; at = at.Parents.FirstOrDefault()) if (at.Name == "far") return true; return false; }
        var farLod = lods.Single(IsFar);
        var nearQ = world.Nodes.Where(n => n.Name == "q").Single(n => !IsFar(n));
        Assert.Equal("Move plod to the top of m1_01.gltf", SourceObjectEdits.PlanReparent(Target(workspace, "m1", build, world, farLod), null, Token).Label);
        Assert.NotEmpty(SourceObjectEdits.PlanReparent(Target(workspace, "m1", build, world, farLod), nearQ, Token).Changes);
        Assert.Contains("level-of-detail", Message(() => SourceObjectEdits.PlanReparent(Target(workspace, "m1", build, world, farLod), world.Nodes.Where(n => n.Name == "q2").First(n => IsFar(n)), Token)));
    }

    [Fact]
    public async Task PartCopiesInDifferentZonesKeepThemWhereTheFileCan()
    {
        using SourceWorldFixture fixture = new();
        // The database references the part from zones 3 and 5; post and box take the zone of each reference, crate has zone 7.
        Database(fixture, [Node("ground", Quad(64, 0)), Reference(3), Reference(5)], part: [Node("crate", Quad(4, 1), zone: 7), Node("post", Quad(1, 0), zone: 0xFF), Node("box", Quad(2, 0), zone: 0xFF)]);
        SourceWorkspace workspace = new(fixture.Project);
        var (build, world) = await BuildAsync(fixture, workspace, "m1");
        IEnumerable<uint> Zones(string name) => world.Nodes.Where(n => n.Name == name).Select(n => n.Zone & 0xFF).Order();
        Assert.Equal([3u, 5u], Zones("post"));
        var post = world.Nodes.First(n => n.Name == "post");
        // Under crate, the file's zone 7 would replace both, and one file cannot keep each copy's own.
        Assert.Contains("differs between its copies", Message(() => SourceObjectEdits.PlanReparent(Target(workspace, "m1", build, world, post), world.Nodes.First(n => n.Name == "crate"), Token)));
        // Under box, which has no zone in the file either, each copy keeps the zone of its reference.
        Apply(workspace, SourceObjectEdits.PlanReparent(Target(workspace, "m1", build, world, post), world.Nodes.First(n => n.Name == "box"), Token));
        (_, world) = await BuildAsync(fixture, workspace, "m1");
        Assert.All(world.Nodes.Where(n => n.Name == "post"), p => Assert.Equal("box", Assert.Single(p.Parents).Name));
        Assert.Equal([3u, 5u], Zones("post"));
    }

    [Fact]
    public async Task NodesAScriptLeftWithoutAParentMoveOnlyInTheScripts()
    {
        using SourceWorldFixture fixture = new();
        fixture.WriteNestedDatabase();
        // As morfUtil.gw does with morph LODs: moved to the world, the lid would join it, and the script's DeleteChild
        // would match nothing.
        fixture.Write("gamegen/m1.gs", Encoding.Latin1.GetString(File.ReadAllBytes(fixture.Path("gamegen/m1.gs"))).Replace("# no vehicles", "FindNode crate\r\nDeleteChild lid", StringComparison.Ordinal));
        SourceWorkspace workspace = new(fixture.Project);
        var (build, world) = await BuildAsync(fixture, workspace, "m1");
        var lid = world.Nodes.Single(n => n.Name == "lid");
        Assert.Empty(lid.Parents);
        Assert.Contains("not placed in the world", Message(() => SourceObjectEdits.PlanReparent(Target(workspace, "m1", build, world, lid), null, Token)));
    }

    [Fact]
    public async Task DeletingAScriptLoadedObjectTakesOutWhatItsPartsGot()
    {
        using SourceWorldFixture fixture = new();
        SourceWorkspace workspace = new(fixture.Project);
        SourceWorlds.AddModel(workspace, "m1", new(new(fixture.Tank, "tank_at", new(100, 0, -50)), []), Token);
        // The script takes the hull out of the tank and attaches it to the ground: deleting the tank takes that AddChild out
        // too, which would otherwise find no hull.
        BeforeWrite(workspace, "FindNode tank_at\r\nDeleteChild hull\r\nFindNode ground\r\nAddChild hull");
        var (build, world) = await BuildAsync(fixture, workspace, "m1");
        Assert.Equal("ground", Assert.Single(world.Nodes.Single(n => n.Name == "hull").Parents).Name);
        Apply(workspace, SourceObjectEdits.PlanDelete(Target(workspace, "m1", build, world, "tank_at"), Token));
        Assert.Contains("# AddChild hull\r\n", Text(workspace, "gamegen/m1.gs"));
        (build, world) = await BuildAsync(fixture, workspace, "m1");
        Assert.DoesNotContain(world.Nodes, n => n.Name is "hull" or "tank_at");
        Assert.DoesNotContain(build.Outputs.SelectMany(o => o.Warnings), w => w.Contains("AddChild hull", StringComparison.Ordinal));

        // As bft1.gw names a copter's rotors: the renaming of a part and the FindSubNode before it are taken out with it.
        using SourceWorldFixture copter = new();
        workspace = new(copter.Project);
        SourceWorlds.AddModel(workspace, "m1", new(new(copter.Tank, "tank_at", new(100, 0, -50)), []), Token);
        BeforeWrite(workspace, "FindNode tank_at\r\nSetLandmark on\r\nFindSubNode hull\r\nNodeSetDescription hull_1");
        (build, world) = await BuildAsync(copter, workspace, "m1");
        Assert.Contains(world.Nodes, n => n.Name == "hull_1");
        var deletion = SourceObjectEdits.PlanDelete(Target(workspace, "m1", build, world, "tank_at"), Token);
        Assert.Contains(deletion.Notes, n => n.Contains("find hull_1", StringComparison.Ordinal));
        Apply(workspace, deletion);
        foreach (string line in new[] { "FindSubNode hull", "NodeSetDescription hull_1", "SetLandmark on" }) Assert.Contains("# " + line, Text(workspace, "gamegen/m1.gs"));
        (build, world) = await BuildAsync(copter, workspace, "m1");
        Assert.DoesNotContain(world.Nodes, n => n.Name is "hull_1" or "tank_at");
        // Once another node was attached below it, FindSubNode could have found that one: refused.
        workspace.Undo();
        BeforeWrite(workspace, "NewObject3D marker\r\nFindNode tank_at\r\nAddChild marker");
        (build, world) = await BuildAsync(copter, workspace, "m1");
        Assert.Contains("FindSubNode", Message(() => SourceObjectEdits.PlanDelete(Target(workspace, "m1", build, world, "tank_at"), Token)));
    }

    [Fact]
    public async Task ValuesNothingSetGoToTheMissionWhenAnotherRunsTheScript()
    {
        using SourceWorldFixture fixture = new();
        // Both missions make their world (and a marker) in world.gw.
        foreach (string mission in new[] { "m1", "m2" })
            fixture.Write($"gamegen/{mission}.gs", Encoding.Latin1.GetString(File.ReadAllBytes(fixture.Path($"gamegen/{mission}.gs"))).Replace("NewWorld %worldName%", "source world.gw", StringComparison.Ordinal));
        fixture.Write("gamegen/world.gw", "NewWorld %worldName%\r\nNewObject3D marker\r\nFindNode %worldName%\r\nAddChild marker\r\n");
        SourceWorkspace workspace = new(fixture.Project);
        var (build, world) = await BuildAsync(fixture, workspace, "m1");
        var worldNode = world.Nodes.Single(n => n.Class == WorldNodeClass.World);
        var origin = Origin(build, world, worldNode);
        Assert.Equal("gamegen/world.gw", origin.Created!.Script);
        // Inserted after NewWorld, the fog would reach m2 too; m1 sets it before writing its world instead.
        Assert.Contains("also runs in m2", Message(() => SourceObjectEdits.PlanCommand(workspace, "world", origin, build.Executions, "WorldSetFogDensity", ["0.25"], Token, "m1")));
        var fog = SourceObjectEdits.PlanCommand(workspace, "world", origin, build.Executions, "WorldSetFogDensity", ["0.25"], Token, "m1", world, build.WriteInstruction);
        Assert.Equal("gamegen/m1.gs", Assert.Single(fog.Changes).Relative);
        Apply(workspace, fog);
        Assert.Contains("FindNode world\r\nWorldSetFogDensity 0.25\r\nGameZWriteZBDFile", Text(workspace, "gamegen/m1.gs"));
        (build, world) = await BuildAsync(fixture, workspace, "m1");
        Assert.Equal("gamegen/m1.gs", Origin(build, world, world.Nodes.Single(n => n.Class == WorldNodeClass.World)).Writers["WorldSetFogDensity"].Script);
        // So does a flag of the marker world.gw makes.
        var marker = world.Nodes.Single(n => n.Name == "marker");
        Apply(workspace, SourceObjectEdits.PlanFlag(workspace, "marker", Origin(build, world, marker), build.Executions, 0x80, true, Token, "m1", null, world, build.WriteInstruction));
        (_, world) = await BuildAsync(fixture, workspace, "m1");
        Assert.NotEqual(0u, world.Nodes.Single(n => n.Name == "marker").Flags & 0x80);
        Assert.False(workspace.IsFileDirty("gamegen/world.gw"));
    }

    [Fact]
    public async Task ScriptObjectsMoveWhereTheirScriptsRunOnlyInThisMission()
    {
        using SourceWorldFixture fixture = new();
        // mission1.gw, which only m1 runs, loads a tank and attaches it to the world.
        fixture.Write("gamegen/m1.gs", Encoding.Latin1.GetString(File.ReadAllBytes(fixture.Path("gamegen/m1.gs"))).Replace("# no vehicles", "source mission1.gw", StringComparison.Ordinal));
        fixture.Write("gamegen/mission1.gw", "SetModelDirectory ..\\data\\m2\\models\\bft\r\nLoadGameGen tank.flt tank_x\r\nObject3DTranslate 10.0 0.0 20.0\r\nFindNode %worldName%\r\nAddChild tank_x\r\n");
        SourceWorkspace workspace = new(fixture.Project);
        var (build, world) = await BuildAsync(fixture, workspace, "m1");
        var before = WorldUpdate.LocalMatrix(world.Nodes.Single(n => n.Name == "tank_x"))!.Value;
        var plan = SourceObjectEdits.PlanReparent(Target(workspace, "m1", build, world, "tank_x"), world.Nodes.Single(n => n.Name == "ground"), Token);
        Assert.Equal(["gamegen/m1.gs", "gamegen/mission1.gw"], plan.Changes.Select(c => c.Relative).Order(StringComparer.Ordinal));
        Apply(workspace, plan);
        Assert.Contains("# AddChild tank_x\r\n", Text(workspace, "gamegen/mission1.gw"));
        (build, world) = await BuildAsync(fixture, workspace, "m1");
        var tank = world.Nodes.Single(n => n.Name == "tank_x");
        Assert.Equal("ground", Assert.Single(tank.Parents).Name);
        Assert.Equal(before.Translation, WorldUpdate.LocalMatrix(tank)!.Value.Translation);
        // Once m2 runs mission1.gw too, editing it would change m2: refused.
        workspace.Undo();
        (build, world) = await BuildAsync(fixture, workspace, "m1");
        fixture.Write("gamegen/m2.gs", Encoding.Latin1.GetString(File.ReadAllBytes(fixture.Path("gamegen/m2.gs"))).Replace("SetModelDirectory ..\\data\\m2\\models\\bft", "source mission1.gw\r\nSetModelDirectory ..\\data\\m2\\models\\bft", StringComparison.Ordinal));
        Assert.Contains("also runs in m2", Message(() => SourceObjectEdits.PlanReparent(Target(workspace, "m1", build, world, "tank_x"), world.Nodes.Single(n => n.Name == "ground"), Token)));
    }

    [Fact]
    public async Task CopiesCheckOnlyTheValuesTheyChange()
    {
        using SourceWorldFixture fixture = new();
        // Blender left the ground at a scale below what an edit may set; a copy taking only a new rotation keeps it.
        var json = JsonNode.Parse(File.ReadAllText(fixture.Path("data/m1/models/m1.gltf")))!;
        Named(json, "ground")["scale"] = new JsonArray(5e-6f, 5e-6f, 5e-6f);
        fixture.Write("data/m1/models/m1.gltf", json.ToJsonString());
        SourceWorkspace workspace = new(fixture.Project);
        var (build, world) = await BuildAsync(fixture, workspace, "m1");
        var target = Target(workspace, "m1", build, world, "ground");
        var shown = ObjectTransform.Of(target.Node);
        Assert.True(shown.Scale.X < 1e-5f);
        Assert.NotEmpty(SourceObjectEdits.PlanDuplicate(target, "ground2", shown with { RotationDegrees = new(0, 45, 0) }, Token).Changes);
        // A new scale is still checked.
        Assert.Contains("0.00001", Message(() => SourceObjectEdits.PlanDuplicate(target, "ground2", shown with { Scale = new(2e-6f, 1, 1) }, Token)));
    }

    [Fact]
    public void VerticalTurnsShowOneYawAndRollWhateverTheScale()
    {
        // At ±90° pitch yaw and roll turn about one axis: the shown pair must not follow float noise, which a scale edit
        // (rebuilding the matrix, through glTF's quaternion) changes.
        Random random = new(5);
        for (int i = 0; i < 400; i++)
        {
            float pitch = i % 2 == 0 ? 90 : -90;
            JsonObject node = [];
            GltfNodeEdits.SetLocal(node, new ObjectTransform(Vector3.Zero, new(pitch, random.Next(-179, 180), random.Next(-179, 180)), Vector3.One).Matrix());
            var shown = ObjectTransform.Snap(ObjectTransform.FromMatrix(GltfNodeEdits.Local(node)).RotationDegrees, angles: true);
            Assert.Equal(0, shown.Y);
            for (int edit = 0; edit < 4; edit++)
            {
                var m = new ObjectTransform(Vector3.Zero, shown, new Vector3(edit % 2 == 0 ? 2.5f : 1)).Matrix();
                GltfNodeEdits.SetLocal(node, m);
                var again = ObjectTransform.Snap(ObjectTransform.FromMatrix(GltfNodeEdits.Local(JsonNode.Parse(node.ToJsonString())!.AsObject())).RotationDegrees, angles: true);
                Assert.True(Vector3.Distance(again, shown) < 2e-3f, $"{shown} became {again}");
                // The same turn: the matrix the shown values make is the one stored.
                var back = new ObjectTransform(Vector3.Zero, again, new Vector3(edit % 2 == 0 ? 2.5f : 1)).Matrix();
                for (int r = 0; r < 3; r++) for (int c = 0; c < 3; c++) Assert.True(MathF.Abs(back[r, c] - m[r, c]) < 1e-4f * 2.5f, $"{shown}: {back} is not {m}");
            }
        }
    }

    [Fact]
    public async Task FlagsAnotherCopysScriptSetsAreNamedAndScriptsSetOnlyTheirCommands()
    {
        using SourceWorldFixture fixture = new();
        fixture.WritePartDatabase();
        // A script makes the newest lid a landmark: the part's file changes for both copies, but that lid keeps the script's value.
        fixture.Write("gamegen/m1.gs", Encoding.Latin1.GetString(File.ReadAllBytes(fixture.Path("gamegen/m1.gs"))).Replace("# no vehicles", "FindNode lid\r\nSetLandmark off", StringComparison.Ordinal));
        SourceWorkspace workspace = new(fixture.Project);
        var (build, world) = await BuildAsync(fixture, workspace, "m1");
        var plain = world.Nodes.Where(n => n.Name == "lid").Select(n => Origin(build, world, n)).Single(p => p.Writers.Count == 0);
        var flag = SourceObjectEdits.PlanFlag(workspace, "lid", plain, build.Executions, 0x80, true, Token, "m1", SourceObjectEdits.CopiesOf(plain, build.Provenance.Values));
        Assert.Contains(flag.Notes, n => n.Contains("(SetLandmark) sets this flag on another copy of lid, which keeps the value it sets", StringComparison.Ordinal));
        // A glTF node carries every flag; a script object only those a command sets (none sets ClipTo).
        Assert.True(SourceObjectEdits.FlagSettable(plain, 0x20000));
        SourceWorlds.AddModel(workspace, "m1", new(new(fixture.Tank, "tank_at", new(100, 0, -50)), []), Token);
        (build, world) = await BuildAsync(fixture, workspace, "m1");
        var tank = Origin(build, world, world.Nodes.Single(n => n.Name == "tank_at"));
        Assert.False(SourceObjectEdits.FlagSettable(tank, 0x20000));
        Assert.True(SourceObjectEdits.FlagSettable(tank, 0x10000));
    }

    [Fact]
    public async Task GroupsMovedWhereTheBuildDeletesThemAreCheckedWhenPlanned()
    {
        using SourceWorldFixture fixture = new();
        // g, a group of the database, stands under the object holder (which the build keeps), holding member.
        var g = Node("g"); g.Children.Add(Node("member", Quad(2, 0)));
        var holder = Node("holder", Quad(4, 0)); holder.Children.Add(g);
        HashSet<WorldNode> groups = new(ReferenceEqualityComparer.Instance) { g };
        Database(fixture, [Node("ground", Quad(64, 0)), holder], groups: groups, edit: json => Named(json, "holder")["translation"] = new JsonArray(5f, 0f, 0f));
        SourceWorkspace workspace = new(fixture.Project);
        var (build, world) = await BuildAsync(fixture, workspace, "m1");
        Assert.Equal("holder", Assert.Single(world.Nodes.Single(n => n.Name == "g").Parents).Name);
        // At the top, the build would delete g and its member would join the world: g would need holder's offset as a
        // transform of its own, which a group cannot have.
        Assert.Contains("group of the mission database", Message(() => SourceObjectEdits.PlanReparent(Target(workspace, "m1", build, world, "g"), null, Token)));
        // Under the ground, an object, it stays a node.
        Assert.NotEmpty(SourceObjectEdits.PlanReparent(Target(workspace, "m1", build, world, "g"), world.Nodes.Single(n => n.Name == "ground"), Token).Changes);
        // Where holder does not move it, it joins the top and its member the world.
        using SourceWorldFixture still = new();
        g = Node("g"); g.Children.Add(Node("member", Quad(2, 0)));
        holder = Node("holder", Quad(4, 0)); holder.Children.Add(g);
        Database(still, [Node("ground", Quad(64, 0)), holder], groups: new HashSet<WorldNode>(ReferenceEqualityComparer.Instance) { g });
        workspace = new(still.Project);
        (build, world) = await BuildAsync(still, workspace, "m1");
        Apply(workspace, SourceObjectEdits.PlanReparent(Target(workspace, "m1", build, world, "g"), null, Token));
        (_, world) = await BuildAsync(still, workspace, "m1");
        Assert.Equal(WorldNodeClass.World, Assert.Single(world.Nodes.Single(n => n.Name == "member").Parents).Class);
    }
}
