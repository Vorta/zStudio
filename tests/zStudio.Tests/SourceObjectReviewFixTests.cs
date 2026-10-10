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
/// Source-world object edits where a node's values come from elsewhere than its own file or script: a shared node's zone
/// from the copy the build reads first, a value from a script other missions run too, a model file's node transformed by
/// another mission's script, and the place of a node inside a shared node.
/// </summary>
public sealed class SourceObjectReviewFixTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    private static async Task<(SourceWorldBuild Build, GameZWorld World)> BuildAsync(SourceWorldFixture fixture, SourceWorkspace workspace, string mission = "m1")
    {
        var build = await SourceWorlds.BuildPreviewAsync(fixture.Project, mission, Path.Combine(SourceWorlds.PreviewRoot(fixture.Project), "p-" + Guid.NewGuid().ToString("N")), workspace.Overlay(), token: Token);
        Assert.Null(build.Outputs.FirstOrDefault(o => o.Error != null)?.Error);
        var world = GameZWorldReader.FromDocument(FormatRegistry.Default.OpenBytes("gamez.zbd", await File.ReadAllBytesAsync(build.WorldPath, Token), token: Token), Token);
        return (build, world);
    }
    private static Dictionary<WorldNode, WorldNodeProvenance> Provenance(SourceWorldBuild build, GameZWorld world)
    {
        Dictionary<WorldNode, WorldNodeProvenance> provenance = new(ReferenceEqualityComparer.Instance);
        foreach (var (node, slot) in GameZWriter.NodeSlots(world, TestContext.Current.CancellationToken)) if (build.Provenance.TryGetValue(slot, out var origin)) provenance[node] = origin;
        return provenance;
    }
    private static SourceObjectTarget Target(SourceWorkspace workspace, SourceWorldBuild build, GameZWorld world, WorldNode picked, string mission = "m1")
    {
        var provenance = Provenance(build, world);
        return new(workspace, mission, world, SourceObjectEdits.ObjectOf(picked, provenance), provenance, build.Executions) { Write = build.WriteInstruction };
    }
    private static SourceObjectTarget Target(SourceWorkspace workspace, SourceWorldBuild build, GameZWorld world, string name) => Target(workspace, build, world, world.Nodes.Single(n => n.Name == name));
    private static WorldNodeProvenance Origin(SourceWorldBuild build, GameZWorld world, WorldNode node) => build.Provenance[GameZWriter.NodeSlots(world, TestContext.Current.CancellationToken)[node]];
    private static WorldNodeProvenance Origin(SourceWorldBuild build, GameZWorld world, string name) => Origin(build, world, world.Nodes.Single(n => n.Name == name));
    private static void Apply(SourceWorkspace workspace, SourceEditPlan plan) => Assert.NotNull(workspace.Apply(plan.Label, plan.Changes.Select(c => (c.Relative, (byte[]?)c.Content)), Token));
    private static string Text(SourceWorkspace workspace, string path) => Encoding.Latin1.GetString(workspace.Read(path, Token)!);
    private static string Message(Action plan) => Assert.Throws<InvalidDataException>(plan).Message;
    private static void Replace(SourceWorldFixture fixture, string script, string line, string replacement)
    {
        string text = Encoding.Latin1.GetString(File.ReadAllBytes(fixture.Path(script)));
        Assert.Contains(line, text);
        fixture.Write(script, text.Replace(line, replacement, StringComparison.Ordinal));
    }
    private static uint Zone(WorldNode node) => node.Zone & 0xFF;

    private static readonly WorldMaterial Rock = new() { Texture = new("rock"), Flags = 0x1FF };
    private static WorldNode Node(string name, WorldModel? model = null)
    {
        // Zone 0xFF, what the load passes on: the files state no zones until a test gives one.
        WorldNode node = new(name, WorldNodeClass.Object3D) { Model = model, Flags = WorldGltf.DefaultCarried, Zone = 0xFF };
        node.SetPayloadInt(0, 0x28);
        return node;
    }
    private static WorldModel Quad(float size, float y)
    {
        ModelBuilder builder = new();
        builder.Add(new([new(0, y, 0), new(size, y, 0), new(size, y, -size), new(0, y, -size)], [new(0, 0), new(1, 0), new(1, 1), new(0, 1)], [], [], Rock));
        return builder.Finish();
    }
    private static JsonObject Named(JsonNode file, string name) => file["nodes"]!.AsArray().Select(n => n!.AsObject()).First(n => n["name"]?.GetValue<string>() == name);
    private static void SetZone(JsonNode file, string name, int zone)
    {
        var node = Named(file, name);
        var extras = node["extras"] as JsonObject ?? (JsonObject)(node["extras"] = new JsonObject());
        var engine = extras[WorldGltf.Key] as JsonObject ?? (JsonObject)(extras[WorldGltf.Key] = new JsonObject());
        engine["zone"] = zone;
    }
    /// <summary>
    /// Writes <paramref name="roots"/> as a model of <paramref name="folder"/> named <paramref name="stem"/>; a node named
    /// m1_01.flt references the part m1_01.gltf (a group). <paramref name="edit"/> changes the JSON.
    /// </summary>
    private static void Model(SourceWorldFixture fixture, string folder, string stem, IReadOnlyList<WorldNode> roots, Action<JsonNode>? edit = null)
    {
        static bool IsReference(WorldNode n) => n.Name == "m1_01.flt";
        var (json, bin) = WorldGltf.Export(roots, 0xFF, new()
        {
            Texture = t => ($"../textures/{t.Name}.png", 0),
            Reference = n => IsReference(n) ? "m1_01.gltf" : null, Content = n => IsReference(n) ? [.. n.Children] : null, Group = IsReference,
        }).Write(stem + ".bin");
        var file = JsonNode.Parse(json)!; edit?.Invoke(file);
        fixture.Write($"{folder}/{stem}.bin", bin); fixture.Write($"{folder}/{stem}.gltf", file.ToJsonString());
    }
    /// <summary>Holders that share one node, holder (an instance), which holds <paramref name="inside"/>.</summary>
    private static WorldNode[] Holders(string[] names, params WorldNode[] inside)
    {
        var holder = Node("holder"); foreach (var n in inside) holder.Children.Add(n);
        var holders = names.Select(n => Node(n)).ToArray();
        foreach (var h in holders) { h.Children.Add(holder); holder.Parents.Add(h); }
        return holders;
    }

    [Fact]
    public async Task SharedNodesKeepTheZoneTheyWereBuiltWith()
    {
        // m1's database: sgate1 (zone 3) and sgate2 (zone 5) share holder, which states no zone: the build gives it, and its
        // gate, the zone of sgate1, whose copy it reads first.
        using SourceWorldFixture fixture = new();
        Model(fixture, "data/m1/models", "m1", [Node("ground", Quad(64, 0)), .. Holders(["sgate1", "sgate2"], Node("gate", Quad(2, 1))), Node("sgate3")],
            json => { SetZone(json, "sgate1", 3); SetZone(json, "sgate2", 5); });
        SourceWorkspace workspace = new(fixture.Project);
        var (build, world) = await BuildAsync(fixture, workspace);
        Assert.Equal(3u, Zone(world.Nodes.Single(n => n.Name == "gate")));
        Assert.Equal(2, world.Nodes.Single(n => n.Name == "holder").Parents.Count);

        // Under sgate3, sgate1 comes after sgate2, whose copy the build then reads: the shared node keeps zone 3.
        var moved = SourceObjectEdits.PlanReparent(Target(workspace, build, world, "sgate1"), world.Nodes.Single(n => n.Name == "sgate3"), Token);
        Assert.Contains(moved.Notes, n => n.Contains("keeps zone 3", StringComparison.Ordinal));
        Apply(workspace, moved);
        var (_, after) = await BuildAsync(fixture, workspace);
        Assert.Equal("sgate3", after.Nodes.Single(n => n.Name == "sgate1").Parents.Single().Name);
        Assert.Equal(3u, Zone(after.Nodes.Single(n => n.Name == "holder")));
        Assert.Equal(3u, Zone(after.Nodes.Single(n => n.Name == "gate")));
        // Every copy states it, so the copies stay alike.
        var file = JsonNode.Parse(workspace.Read("data/m1/models/m1.gltf", Token)!)!;
        Assert.All(file["nodes"]!.AsArray().Where(n => n!["name"]!.GetValue<string>() == "holder"), h => Assert.Equal(3, h!["extras"]![WorldGltf.Key]!["zone"]!.GetValue<int>()));
        workspace.Undo();

        // Without sgate1 the build reads sgate2's copy: the same.
        Apply(workspace, SourceObjectEdits.PlanDelete(Target(workspace, build, world, "sgate1"), Token));
        (_, after) = await BuildAsync(fixture, workspace);
        Assert.DoesNotContain(after.Nodes, n => n.Name == "sgate1");
        Assert.Equal(3u, Zone(after.Nodes.Single(n => n.Name == "gate")));
        workspace.Undo();

        // A copy of sgate2 holds a shared node of its own, read under the copy (zone 5): it keeps the zone of the one it copies.
        Apply(workspace, SourceObjectEdits.PlanDuplicate(Target(workspace, build, world, "sgate2"), "sgate4", null, Token));
        (_, after) = await BuildAsync(fixture, workspace);
        Assert.Equal(2, after.Nodes.Count(n => n.Name == "gate"));
        Assert.All(after.Nodes.Where(n => n.Name is "gate" or "holder"), n => Assert.Equal(3u, Zone(n)));
        Assert.Equal(5u, Zone(after.Nodes.Single(n => n.Name == "sgate4")));
        workspace.Undo();

        // A zone that came from the load (sgate1 states none) is written as the build gave it.
        using SourceWorldFixture loaded = new();
        Model(loaded, "data/m1/models", "m1", [Node("ground", Quad(64, 0)), .. Holders(["sgate1", "sgate2"], Node("gate", Quad(2, 1))), Node("sgate3")], json => SetZone(json, "sgate2", 5));
        workspace = new(loaded.Project);
        (build, world) = await BuildAsync(loaded, workspace);
        Assert.Equal(0xFFu, Zone(world.Nodes.Single(n => n.Name == "gate")));
        Apply(workspace, SourceObjectEdits.PlanReparent(Target(workspace, build, world, "sgate1"), world.Nodes.Single(n => n.Name == "sgate3"), Token));
        (_, after) = await BuildAsync(loaded, workspace);
        Assert.Equal(0xFFu, Zone(after.Nodes.Single(n => n.Name == "gate")));

        // In a part the database references from zones 3 and 5, a zone from the load differs between the part's copies: a
        // move that would give the shared node hB's zone 7 instead is refused, since one file cannot keep both.
        using SourceWorldFixture part = new();
        var hC = Node("hC");
        var holders = Holders(["hA", "hB"], Node("gate", Quad(2, 1)));
        Model(part, "data/m1/models", "m1_01", [.. holders, hC], json => SetZone(json, "hB", 7));
        WorldNode Reference(uint zone) { var r = Node("m1_01.flt"); r.Zone = zone; r.Children.Add(Node("placeholder")); return r; }
        Model(part, "data/m1/models", "m1", [Node("ground", Quad(64, 0)), Reference(3), Reference(5)]);
        workspace = new(part.Project);
        (build, world) = await BuildAsync(part, workspace);
        Assert.Equal([3u, 5u], world.Nodes.Where(n => n.Name == "gate").Select(Zone).Order());
        var hA = world.Nodes.First(n => n.Name == "hA");
        Assert.Contains("differs between the file's copies", Message(() => SourceObjectEdits.PlanReparent(Target(workspace, build, world, hA), world.Nodes.First(n => n.Name == "hC"), Token)));
    }

    [Fact]
    public async Task LinesTakingAMacroGetTheValueSetAfterThem()
    {
        // world.gw, which m2 runs too, scales the marker; m1 places it with a macro. A move cannot change the macro's line, so
        // the position is set before m1 writes its world.
        using SourceWorldFixture fixture = new();
        foreach (string mission in new[] { "m1", "m2" }) Replace(fixture, $"gamegen/{mission}.gs", "NewWorld %worldName%", "source world.gw");
        Replace(fixture, "gamegen/m1.gs", "source world.gw", "source world.gw\r\nset px 4.0\r\nFindNode marker\r\nObject3DTranslate %px% 2.0 3.0");
        fixture.Write("gamegen/world.gw", string.Join("\r\n", "NewWorld %worldName%", "NewObject3D marker", "Object3DScale 2.0 2.0 2.0", "FindNode %worldName%", "AddChild marker", ""));
        SourceWorkspace workspace = new(fixture.Project);
        var (build, world) = await BuildAsync(fixture, workspace);
        var marker = world.Nodes.Single(n => n.Name == "marker"); var shown = ObjectTransform.Of(marker);
        Assert.Equal(new Vector3(4, 2, 3), shown.Position);
        Apply(workspace, SourceObjectEdits.PlanTransform(workspace, "marker", Origin(build, world, marker), build.Executions, shown with { Position = new(5, 2, 3) }, Token, "m1", shown, null, world, build.WriteInstruction));
        Assert.Contains("Object3DTranslate %px% 2.0 3.0", Text(workspace, "gamegen/m1.gs"));
        (_, world) = await BuildAsync(fixture, workspace);
        Assert.Equal(new Vector3(5, 2, 3), ObjectTransform.Of(world.Nodes.Single(n => n.Name == "marker")).Position);
    }

    [Fact]
    public async Task ValuesAScriptOtherMissionsRunSetChangeInThisMissionOnly()
    {
        // Both missions make their world in world.gw, which also makes a marker, a template (as weapons.gw does) and sets
        // values on them; m1 changes them before writing its world.
        using SourceWorldFixture fixture = new();
        foreach (string mission in new[] { "m1", "m2" }) Replace(fixture, $"gamegen/{mission}.gs", "NewWorld %worldName%", "source world.gw");
        fixture.Write("gamegen/world.gw", string.Join("\r\n",
            "NewWorld %worldName%", "WorldSetFogColor 0.1 0.2 0.3", "NewObject3D marker", "Object3DTranslate 1.0 2.0 3.0", "Object3DScale 2.0 2.0 2.0", "SetLandmark off", "FindNode %worldName%", "AddChild marker",
            "SetModelDirectory ..\\data\\m2\\models\\bft", "LoadGameGen tank.flt tmpl", "SetIntersectSurface off", "FindSubNode hull", "SetLandmark on", ""));
        SourceWorkspace workspace = new(fixture.Project);
        var (build, world) = await BuildAsync(fixture, workspace);

        // A flag world.gw set: the line goes before m1 writes its world, after a FindNode of the marker.
        var flag = SourceObjectEdits.PlanFlag(workspace, world.Nodes.Single(n => n.Name == "marker"), Origin(build, world, "marker"), build.Executions, 0x80, true, Token, "m1", null, world, build.WriteInstruction);
        Assert.Equal("gamegen/m1.gs", Assert.Single(flag.Changes).Relative);
        Apply(workspace, flag);
        Assert.Contains("FindNode marker\r\nSetLandmark on\r\nGameZWriteZBDFile", Text(workspace, "gamegen/m1.gs"));
        // The template's flag and a flag of a node of its model file, both set by world.gw.
        (build, world) = await BuildAsync(fixture, workspace);
        Apply(workspace, SourceObjectEdits.PlanFlag(workspace, world.Nodes.Single(n => n.Name == "tmpl"), Origin(build, world, "tmpl"), build.Executions, 0x10, true, Token, "m1", null, world, build.WriteInstruction));
        (build, world) = await BuildAsync(fixture, workspace);
        var hull = Origin(build, world, "hull");
        Assert.NotNull(hull.ModelFile);
        Apply(workspace, SourceObjectEdits.PlanFlag(workspace, world.Nodes.Single(n => n.Name == "hull"), hull, build.Executions, 0x80, false, Token, "m1", SourceObjectEdits.CopiesOf(hull, build.Provenance.Values), world, build.WriteInstruction));
        // A transform world.gw set (and a rotation nothing set): only the changed position component takes the new value.
        (build, world) = await BuildAsync(fixture, workspace);
        var marker = world.Nodes.Single(n => n.Name == "marker");
        var shown = ObjectTransform.Of(marker);
        Apply(workspace, SourceObjectEdits.PlanTransform(workspace, "marker", Origin(build, world, marker), build.Executions, shown with { Position = new(5, 2, 3), RotationDegrees = new(0, 90, 0) }, Token, "m1", shown,
            null, world, build.WriteInstruction));
        Assert.Contains("FindNode marker\r\nObject3DTranslate 5.0 2.0 3.0\r\nObject3DRotate 0.0 90.0 0.0\r\nGameZWriteZBDFile", Text(workspace, "gamegen/m1.gs"));
        // A property command world.gw set.
        (build, world) = await BuildAsync(fixture, workspace);
        Apply(workspace, SourceObjectEdits.PlanCommand(workspace, "world", Origin(build, world, world.Nodes.Single(n => n.Class == WorldNodeClass.World)), build.Executions, "WorldSetFogColor", ["0.5", "0.5", "0.5"], Token, "m1", world, build.WriteInstruction));
        Assert.False(workspace.IsFileDirty("gamegen/world.gw"));

        (_, world) = await BuildAsync(fixture, workspace);
        Assert.NotEqual(0u, world.Nodes.Single(n => n.Name == "marker").Flags & 0x80);
        Assert.NotEqual(0u, world.Nodes.Single(n => n.Name == "tmpl").Flags & 0x10);
        Assert.Equal(0u, world.Nodes.Single(n => n.Name == "hull").Flags & 0x80);
        var placed = ObjectTransform.Of(world.Nodes.Single(n => n.Name == "marker"));
        Assert.Equal(new Vector3(5, 2, 3), placed.Position);
        Assert.Equal(new Vector3(0, 90, 0), placed.RotationDegrees);
        Assert.Equal(0.5f, world.Nodes.Single(n => n.Class == WorldNodeClass.World).PayloadFloat(0x14));
        // m2 keeps what world.gw sets.
        var (_, other) = await BuildAsync(fixture, workspace, "m2");
        Assert.Equal(0u, other.Nodes.Single(n => n.Name == "marker").Flags & 0x80);
        Assert.Equal(new Vector3(1, 2, 3), ObjectTransform.Of(other.Nodes.Single(n => n.Name == "marker")).Position);
        Assert.Equal(0.1f, other.Nodes.Single(n => n.Class == WorldNodeClass.World).PayloadFloat(0x14));
        // Edited again, the values set for this mission change where they are, with no further FindNode; m2 still keeps its own.
        (build, world) = await BuildAsync(fixture, workspace);
        marker = world.Nodes.Single(n => n.Name == "marker"); shown = ObjectTransform.Of(marker);
        int blocks = Text(workspace, "gamegen/m1.gs").Split("FindNode marker").Length;
        Apply(workspace, SourceObjectEdits.PlanTransform(workspace, "marker", Origin(build, world, marker), build.Executions, shown with { Position = new(7, 2, 3), RotationDegrees = new(0, 45, 0) }, Token, "m1", shown,
            null, world, build.WriteInstruction));
        Assert.Contains("FindNode marker\r\nObject3DTranslate 7.0 2.0 3.0\r\nObject3DRotate 0.0 45.0 0.0\r\n", Text(workspace, "gamegen/m1.gs"));
        Assert.Equal(blocks, Text(workspace, "gamegen/m1.gs").Split("FindNode marker").Length);
        (_, world) = await BuildAsync(fixture, workspace);
        placed = ObjectTransform.Of(world.Nodes.Single(n => n.Name == "marker"));
        Assert.Equal((new Vector3(7, 2, 3), new Vector3(0, 45, 0)), (placed.Position, placed.RotationDegrees));
        (_, other) = await BuildAsync(fixture, workspace, "m2");
        Assert.Equal(new Vector3(1, 2, 3), ObjectTransform.Of(other.Nodes.Single(n => n.Name == "marker")).Position);

        // FindNode finds the newest node of a name: with a second hull in m1 the line could not find this one.
        for (int i = 0; i < 5; i++) workspace.Undo();
        SourceWorlds.AddModel(workspace, "m1", new(new(fixture.Tank, "tank_a", new(100, 0, -50)), []), Token);
        (build, world) = await BuildAsync(fixture, workspace);
        var firstHull = world.Nodes.Where(n => n.Name == "hull").Single(n => Origin(build, world, n).Writers.ContainsKey("SetLandmark"));
        Assert.Contains("2 nodes are named hull", Message(() => SourceObjectEdits.PlanFlag(workspace, firstHull, Origin(build, world, firstHull), build.Executions, 0x80, false, Token, "m1", null, world, build.WriteInstruction)));
        // An object a shared script made has no exact deletion in one mission.
        Assert.Contains("no line of this mission deletes it exactly", Message(() => SourceObjectEdits.PlanDelete(Target(workspace, build, world, "marker"), Token)));
    }

    [Fact]
    public async Task BlenderUpdatesWarnWhereAScriptTurnsANodeThatGainsATransform()
    {
        // m1 loads the tank m2 loads; m2's script turns the hull of its load. An export giving the hull a transform of its own
        // would stop that turn in m2: the update says so (it is not refused: a Blender update replaces the model whole).
        using SourceWorldFixture fixture = new();
        Replace(fixture, "gamegen/m2.gs", "LoadGameGen tank.flt tank", "LoadGameGen tank.flt tank\r\nFindSubNode hull\r\nObject3DRotate 0.0 45.0 0.0");
        SourceWorkspace workspace = new(fixture.Project);
        SourceWorlds.AddModel(workspace, "m1", new(new(fixture.Tank, "tank_a", new(100, 0, -50)), []), Token);
        var (build, world) = await BuildAsync(fixture, workspace);
        string model = Origin(build, world, "hull").ModelFile!;
        byte[] before = workspace.Read(model, Token)!;
        var json = JsonNode.Parse(before)!;
        foreach (var node in json["nodes"]!.AsArray().OfType<JsonObject>().Where(n => n["name"]?.GetValue<string>() == "hull")) node["rotation"] = new JsonArray(0f, 0.38268343f, 0f, 0.9238795f);
        var notes = SourceObjectEdits.ScriptTransformsReached(workspace, "m1", model, before, Encoding.UTF8.GetBytes(json.ToJsonString()), build.Provenance.Values, Token);
        Assert.Contains("hull gains a transform of its own in the export, so gamegen/m2.gs line 16 (Object3DRotate, where m2 loads the file) stops turning or scaling it.", notes);
        // An export that keeps every node's transform as it was warns about nothing.
        Assert.Empty(SourceObjectEdits.ScriptTransformsReached(workspace, "m1", model, before, before, build.Provenance.Values, Token));
        // Blender renames repeated names (hull.001); the engine name is what the build finds. A world that does not load the
        // model (no provenance for it) still hears about other missions' scripts.
        foreach (var node in json["nodes"]!.AsArray().OfType<JsonObject>().Where(n => n["name"]?.GetValue<string>() == "hull")) { node["name"] = "hull.001"; (node["extras"]?["recoil"] as JsonObject)?.Remove("name"); }
        Assert.Contains("hull gains a transform of its own in the export, so gamegen/m2.gs line 16 (Object3DRotate, where m2 loads the file) stops turning or scaling it.",
            SourceObjectEdits.ScriptTransformsReached(workspace, "m1", model, before, Encoding.UTF8.GetBytes(json.ToJsonString()), [], Token));
    }

    [Fact]
    public async Task ModelNodesAnotherMissionTransformsByScriptKeepTheirTransform()
    {
        // m1 loads the tank m2 loads; m2's script turns the hull of its load.
        using SourceWorldFixture fixture = new();
        Replace(fixture, "gamegen/m2.gs", "LoadGameGen tank.flt tank", "LoadGameGen tank.flt tank\r\nFindSubNode hull\r\nObject3DRotate 0.0 45.0 0.0");
        SourceWorkspace workspace = new(fixture.Project);
        SourceWorlds.AddModel(workspace, "m1", new(new(fixture.Tank, "tank_a", new(100, 0, -50)), []), Token);
        var (build, world) = await BuildAsync(fixture, workspace);
        var hull = Origin(build, world, "hull");
        var shown = ObjectTransform.Of(world.Nodes.Single(n => n.Name == "hull"));
        string Plan(ObjectTransform requested) => Message(() => SourceObjectEdits.PlanTransform(workspace, "hull", hull, build.Executions, requested, Token, "m1", shown, SourceObjectEdits.CopiesOf(hull, build.Provenance.Values)));
        // The glTF would give the hull an authored matrix in m2 too, where the script's turn would stop applying.
        Assert.Contains("gamegen/m2.gs line 16 (Object3DRotate) sets the transform of hull where m2 loads data/m2/models/bft/tank.gltf", Plan(shown with { RotationDegrees = new(0, 30, 0) }));
        Assert.Contains("gamegen/m2.gs line 16", Plan(shown with { Position = new(1, 0, 0) }));

        // A FindNode of the name, once the file is loaded, may find that load's hull.
        Replace(fixture, "gamegen/m2.gs", "FindSubNode hull\r\nObject3DRotate 0.0 45.0 0.0", "FindNode hull\r\nObject3DScale 2.0 2.0 2.0");
        Assert.Contains("(Object3DScale) may set the transform of hull", Plan(shown with { RotationDegrees = new(0, 30, 0) }));
        // A load's root found by its name, then the hull below it, also once renamed.
        Replace(fixture, "gamegen/m2.gs", "FindNode hull\r\nObject3DScale 2.0 2.0 2.0", "FindNode tank\r\nFindSubNode hull\r\nNodeSetDescription hull_1\r\nFindNode tank\r\nFindSubNode hull_1\r\nObject3DScale 1.5 1.5 1.5");
        Assert.Contains("gamegen/m2.gs line 20 (Object3DScale) sets the transform", Plan(shown with { RotationDegrees = new(0, 30, 0) }));
        // A translation there is no reason: it replaces the file's translation whatever the file holds.
        Replace(fixture, "gamegen/m2.gs", "FindSubNode hull_1\r\nObject3DScale 1.5 1.5 1.5", "FindSubNode hull_1\r\nObject3DTranslate 1.0 0.0 0.0");
        Assert.NotEmpty(SourceObjectEdits.PlanTransform(workspace, "hull", hull, build.Executions, shown with { RotationDegrees = new(0, 30, 0) }, Token, "m1", shown, SourceObjectEdits.CopiesOf(hull, build.Provenance.Values)).Changes);
        // Through a file that references the tank.
        fixture.Write("data/m2/models/bft/wrapper.gltf", """{"asset":{"version":"2.0"},"scenes":[{"nodes":[0]}],"nodes":[{"name":"wrap","extras":{"recoil":{"ref":"tank.gltf"}}}]}""");
        Replace(fixture, "gamegen/m2.gs", "LoadGameGen tank.flt tank\r\nFindNode tank\r\nFindSubNode hull\r\nNodeSetDescription hull_1\r\nFindNode tank\r\nFindSubNode hull_1\r\nObject3DTranslate 1.0 0.0 0.0",
            "LoadGameGen wrapper.flt wrapped\r\nFindSubNode hull\r\nObject3DRotate 0.0 45.0 0.0");
        Assert.Contains("gamegen/m2.gs line 16 (Object3DRotate) sets the transform of hull where m2 loads", Plan(shown with { RotationDegrees = new(0, 30, 0) }));

        // A node of another name, or the hull of a file that does not hold this one, is no reason.
        Replace(fixture, "gamegen/m2.gs", "LoadGameGen wrapper.flt wrapped\r\nFindSubNode hull", "LoadGameGen tank.flt tank\r\nFindSubNode turret");
        Assert.NotEmpty(SourceObjectEdits.PlanTransform(workspace, "hull", hull, build.Executions, shown with { RotationDegrees = new(0, 30, 0) }, Token, "m1", shown, SourceObjectEdits.CopiesOf(hull, build.Provenance.Values)).Changes);
        Replace(fixture, "gamegen/m2.gs", "LoadGameGen tank.flt tank\r\nFindSubNode turret", "LoadGameGen m2.flt other\r\nFindSubNode hull");
        Assert.NotEmpty(SourceObjectEdits.PlanTransform(workspace, "hull", hull, build.Executions, shown with { RotationDegrees = new(0, 30, 0) }, Token, "m1", shown, SourceObjectEdits.CopiesOf(hull, build.Provenance.Values)).Changes);
    }

    [Fact]
    public async Task CopiesInsideASharedNodeKeepTheirSiblingsPlaces()
    {
        // holder, shared by sgate1 and sgate2, holds gate and post; the script marks post.
        using SourceWorldFixture fixture = new();
        Model(fixture, "data/m1/models", "m1", [Node("ground", Quad(64, 0)), .. Holders(["sgate1", "sgate2"], Node("gate", Quad(2, 1)), Node("post", Quad(1, 0)))]);
        Replace(fixture, "gamegen/m1.gs", "# no vehicles", "FindNode post\r\nSetLandmark on");
        SourceWorkspace workspace = new(fixture.Project);
        var (build, world) = await BuildAsync(fixture, workspace);
        Assert.Equal("1/1", Origin(build, world, "post").Instance?.ToString());
        // A copy of gate, beside it in every copy of holder, moves post to the third position: it is still the node the
        // script marks.
        Apply(workspace, SourceObjectEdits.PlanDuplicate(Target(workspace, build, world, "gate"), "gate2", null, Token));
        var (after, copied) = await BuildAsync(fixture, workspace);
        Assert.Equal("1/2", Origin(after, copied, "post").Instance?.ToString());
        Assert.NotEqual(0u, copied.Nodes.Single(n => n.Name == "post").Flags & 0x80);
        Assert.Null(SourceObjectEdits.TargetChange(build, after));
    }
}
