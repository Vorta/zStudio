using System.Numerics;
using System.Text.Json.Nodes;
using Recoil.Zbd.Core;
using Recoil.Zbd.Core.Gltf;
using Recoil.Zbd.Core.Sources;
using Recoil.Zbd.Core.Worlds;
using Xunit;

namespace Recoil.Zbd.Tests;

public sealed class SourceZoneEditTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;
    private const string Geometry = "data/m1/models/zone.gltf", Master = "data/m1/models/m1.gltf";

    private sealed class Fixture : IDisposable
    {
        internal SourceWorldFixture Files { get; } = new();
        internal SourceWorkspace Workspace { get; }
        internal SourceWorldBuild Build { get; }
        internal byte[] GeometryBytes { get; }
        internal byte[] BufferBytes { get; }
        internal WorldZoneProfile Profile { get; }
        internal Fixture(bool shared = false)
        {
            GltfPrimitive primitive = new();
            primitive.Positions.AddRange([Vector3.Zero, Vector3.UnitX, Vector3.UnitZ, new(2, 0, 0), new(3, 0, 0), new(2, 0, 1)]);
            primitive.Indices.AddRange([0, 2, 1, 3, 5, 4]);
            GltfMesh mesh = new(); mesh.Primitives.Add(primitive);
            GltfDocument doc = new(); doc.Roots.Add(new() { Name = "ground", Mesh = mesh });
            Profile = new(WorldGltf.LayoutFingerprint(doc, Token), [new(0xAABBCC03, true)],
                [(IReadOnlyList<uint>)new uint[] { 0xFE030102, 0xCAFE0701 }], new(5, true));
            WorldGltf.EmbedZones(doc, Profile, token: Token);
            var (json, bin) = doc.Write("zone.bin", Token);
            GeometryBytes = json; BufferBytes = bin;
            File.WriteAllBytes(Files.Path(Geometry), json); File.WriteAllBytes(Files.Path("data/m1/models/zone.bin"), bin);
            WorldNodeProvenance Origin() => new()
            {
                LogicalModelFile = Geometry, ModelFile = Geometry, ModelNode = 0, ZoneNode = 0, ZoneMesh = 0,
                ZonePolygons = [0, 1],
                ImportedZoneWord = 9, ImportedZoneGate = true, ZoneOwnerPath = Geometry,
            };
            Dictionary<int, WorldNodeProvenance> origins = new() { [10] = Origin() };
            if (shared) origins.Add(11, Origin());
            Build = new("m1", "", "", [], new Dictionary<string, FileStamp>()) { Provenance = origins };
            Workspace = new(Files.Project);
        }
        internal void Apply(SourceZoneEditPlan plan) => Workspace.Apply(plan.Label,
            plan.Changes.Select(x => (x.Relative, (byte[]?)x.Content)), Token);
        internal WorldZoneProfile Current => WorldGltf.CaptureZoneProfile(WorldAssembler.ReadModel(Workspace.Read(Geometry, Token)!, Geometry,
            (path, remaining) => Workspace.Read(path, Token, remaining)!, Token), token: Token);
        public void Dispose() => Files.Dispose();
    }

    [Fact]
    public void PolygonEditPreservesOtherWordsGeometryAndHistory()
    {
        using Fixture f = new();
        var plan = SourceZoneEdits.PlanAssignments(f.Workspace, f.Build, [new(10, 0)], new(PolygonZones: [2, 255, 8]), false, Token);
        Assert.False(f.Workspace.IsDirty);
        f.Apply(plan);
        Assert.Equal(0x08FF0203u, f.Current.MeshPolygons[0][0]);
        Assert.Equal(0xCAFE0701u, f.Current.MeshPolygons[0][1]);
        Assert.Equal(f.Profile.Nodes, f.Current.Nodes);
        Assert.Equal(f.BufferBytes, f.Workspace.Read("data/m1/models/zone.bin", Token));
        Assert.Equal([Geometry], f.Workspace.DirtyFiles);
        Assert.False(File.Exists(f.Files.Path("data/m1/meta/zones.json")));
        f.Workspace.Undo(); Assert.False(f.Workspace.IsDirty);
        f.Workspace.Redo(); Assert.Equal(0x08FF0203u, f.Current.MeshPolygons[0][0]);
        f.Workspace.Save(Token);
        Assert.False(f.Workspace.IsDirty);
        Assert.Equal(0x08FF0203u, WorldGltf.CaptureZoneProfile(WorldAssembler.ReadModel(File.ReadAllBytes(f.Files.Path(Geometry)), Geometry,
            (path, remaining) => File.ReadAllBytes(f.Files.Path(path)), Token), token: Token).MeshPolygons[0][0]);
    }

    [Fact]
    public void ObjectEditKeepsUpperWordAndNeedsExplicitSharedScope()
    {
        using Fixture f = new(shared: true);
        var inspected = SourceZoneEdits.Inspect(f.Workspace, f.Build, [new(10)], token: Token);
        Assert.True(inspected.RequiresSharedScope); Assert.Equal(new[] { 10, 11 }, inspected.AffectedNodes);
        Assert.Throws<InvalidDataException>(() => SourceZoneEdits.PlanAssignments(f.Workspace, f.Build, [new(10)], new(7, false), false, Token));
        Assert.False(f.Workspace.IsDirty);
        f.Apply(SourceZoneEdits.PlanAssignments(f.Workspace, f.Build, [new(10)], new(7, false), true, Token));
        Assert.Equal(new WorldNodeZone(0xAABBCC07, false), f.Current.Nodes[0]);
        Assert.Equal(f.Profile.MeshPolygons[0], f.Current.MeshPolygons[0]);
    }

    [Fact]
    public void PlainModelLoadRootsInspectTheirImportBaselineAndAuthorItOnlyAfterSharedConsent()
    {
        using SourceWorldFixture fixture = new();
        var json = JsonNode.Parse(File.ReadAllBytes(fixture.Path(fixture.Tank)))!.AsObject();
        var scene = json["scenes"]![0]!;
        scene["extras"] ??= new JsonObject(); scene["extras"]![WorldGltf.Key] ??= new JsonObject();
        scene["extras"]![WorldGltf.Key]!["rootFlags"] = "0x70800058";
        Assert.Null(scene["extras"]![WorldGltf.Key]!["loadRoot"]);
        fixture.Write(fixture.Tank, json.ToJsonString());
        string script = File.ReadAllText(fixture.Path("gamegen/m2.gs"));
        fixture.Write("gamegen/m2.gs", script.Replace("GameZWriteZBDFile", "LoadGameGen tank.flt tank_again\r\nGameZWriteZBDFile", StringComparison.Ordinal));
        byte[] original = File.ReadAllBytes(fixture.Path(fixture.Tank));
        byte[] buffer = File.ReadAllBytes(fixture.Path("data/m2/models/bft/tank.bin"));
        SourceWorkspace workspace = new(fixture.Project);
        WorldAssembler Build()
        {
            WorldAssembler assembler = new(new SourceWorlds.DiskFiles(fixture.Project, workspace.Overlay()), Token);
            assembler.Assemble("m2.gs"); return assembler;
        }
        var before = Build(); var picked = before.World.Nodes.Single(n => n.Name == "tank");
        var slots = GameZWriter.NodeSlots(before.World, Token);
        SourceWorldBuild build = new("m2", "", "", [], new Dictionary<string, FileStamp>())
        { Provenance = before.Provenance.Where(p => slots.ContainsKey(p.Key)).ToDictionary(p => slots[p.Key], p => p.Value) };
        // Inspection uses the source import baseline, never a later visible/runtime node value.
        picked.Zone = 99; picked.Flags |= WorldGltf.ZoneGate;
        var selection = new SourceZoneSelection(slots[picked]);
        var inspected = SourceZoneEdits.Inspect(workspace, build, [selection], token: Token);
        Assert.Equal(255u, Assert.Single(inspected.Targets).Word); Assert.False(inspected.Targets[0].Gate);
        Assert.True(inspected.RequiresSharedScope); Assert.Equal(2, inspected.AffectedCount);
        Assert.Equal(original, workspace.Read(fixture.Tank, Token)); Assert.False(workspace.IsDirty); Assert.Equal(0, workspace.UndoCount);
        Assert.Throws<InvalidDataException>(() => SourceZoneEdits.PlanAssignments(workspace, build, [selection], new(Gate: true), false, Token));
        var plan = SourceZoneEdits.PlanAssignments(workspace, build, [selection], new(Gate: true), true, Token);
        Assert.False(workspace.IsDirty);
        workspace.Apply(plan.Label, plan.Changes.Select(c => (c.Relative, (byte[]?)c.Content)), Token);
        Assert.Equal([fixture.Tank], workspace.DirtyFiles);
        var changed = JsonNode.Parse(workspace.Read(fixture.Tank, Token)!)!;
        Assert.Equal("0x70800058", changed["scenes"]![0]!["extras"]![WorldGltf.Key]!["rootFlags"]!.GetValue<string>());
        Assert.Equal(buffer, workspace.Read("data/m2/models/bft/tank.bin", Token));
        Assert.All(Build().World.Nodes.Where(n => n.Name is "tank" or "tank_again"), n =>
        { Assert.Equal(255u, n.Zone); Assert.NotEqual(0u, n.Flags & WorldGltf.ZoneGate); });
        workspace.Undo(); Assert.False(workspace.IsDirty); Assert.Equal(original, workspace.Read(fixture.Tank, Token));
        workspace.Redo(); workspace.Save(Token);
        var reopened = WorldAssembler.ReadModel(File.ReadAllBytes(fixture.Path(fixture.Tank)), fixture.Tank,
            (path, _) => File.ReadAllBytes(fixture.Path(path)), Token);
        Assert.Equal(new WorldNodeZone(255, true), WorldGltf.EmbeddedLoadRoot(reopened));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void InheritedNodeInspectionUsesItsPlacementAfterParentEdits(bool referenced)
    {
        using SourceWorldFixture fixture = new();
        if (referenced) fixture.WriteReferencingDatabase(); else fixture.WriteNestedDatabase();
        var master = JsonNode.Parse(File.ReadAllBytes(fixture.Path(Master)))!.AsObject();
        JsonObject Values(JsonNode node)
        {
            node["extras"] ??= new JsonObject();
            node["extras"]![WorldGltf.Key] ??= new JsonObject();
            return node["extras"]![WorldGltf.Key]!.AsObject();
        }
        var crate = master["nodes"]!.AsArray().Single(n => n?["name"]?.GetValue<string>() == "crate")!;
        Values(crate)["zone"] = 3;
        if (referenced)
        {
            const string leaf = "data/m1/models/lidm.gltf";
            var child = JsonNode.Parse(File.ReadAllBytes(fixture.Path(leaf)))!.AsObject();
            var values = Values(child["nodes"]!.AsArray().Single(n => n?["name"]?.GetValue<string>() == "lid")!);
            values["zone"] = "inherit"; values["zonePadding"] = new JsonArray(18, 52, 86);
            fixture.Write(leaf, child.ToJsonString());
        }
        else Values(master["nodes"]!.AsArray().Single(n => n?["name"]?.GetValue<string>() == "lid")!).Remove("zone");
        fixture.Write(Master, master.ToJsonString());
        SourceWorkspace workspace = new(fixture.Project);
        (SourceWorldBuild Build, SourceZoneSelection Parent, SourceZoneSelection Child) Rebuild()
        {
            WorldAssembler assembler = new(new SourceWorlds.DiskFiles(fixture.Project, workspace.Overlay()), Token);
            assembler.Assemble("m1.gs");
            var slots = GameZWriter.NodeSlots(assembler.World, Token);
            SourceWorldBuild build = new("m1", "", "", [], new Dictionary<string, FileStamp>())
            { Provenance = assembler.Provenance.Where(p => slots.ContainsKey(p.Key)).ToDictionary(p => slots[p.Key], p => p.Value) };
            return (build, new(slots[assembler.World.Nodes.Single(n => n.Name == "crate")]), new(slots[assembler.World.Nodes.Single(n => n.Name == "lid")]));
        }
        void Apply(SourceWorldBuild build, SourceZoneSelection selected, SourceZoneAssignment assignment)
        {
            var plan = SourceZoneEdits.PlanAssignments(workspace, build, [selected], assignment, true, Token);
            workspace.Apply(plan.Label, plan.Changes.Select(c => (c.Relative, (byte[]?)c.Content)), Token);
        }
        uint padding = referenced ? 0x56341200u : 0;
        var before = Rebuild();
        Assert.Equal(padding | 3u, Assert.Single(SourceZoneEdits.Inspect(workspace, before.Build, [before.Child], token: Token).Targets).Word);
        Apply(before.Build, before.Parent, new(NodeZone: 7));
        var changed = Rebuild();
        Assert.Equal(padding | 7u, Assert.Single(SourceZoneEdits.Inspect(workspace, changed.Build, [changed.Child], token: Token).Targets).Word);
        // A gate-only edit must keep inheritance and the node's own opaque bytes.
        Apply(changed.Build, changed.Child, new(Gate: false));
        var gated = Rebuild();
        Apply(gated.Build, gated.Parent, new(NodeZone: 9));
        var moved = Rebuild();
        var inspected = Assert.Single(SourceZoneEdits.Inspect(workspace, moved.Build, [moved.Child], token: Token).Targets);
        Assert.Equal(padding | 9u, inspected.Word); Assert.False(inspected.Gate);
        // Explicit painting then stops inheritance without discarding the opaque bytes.
        Apply(moved.Build, moved.Child, new(NodeZone: 11));
        var painted = Rebuild();
        Apply(painted.Build, painted.Parent, new(NodeZone: 15));
        var final = Rebuild();
        Assert.Equal(padding | 11u, Assert.Single(SourceZoneEdits.Inspect(workspace, final.Build, [final.Child], token: Token).Targets).Word);
    }

    [Fact]
    public void ReferenceDescendantsOfSharedInstancesRequireConsentWithoutChangingTheirLocalIdentity()
    {
        using SourceWorldFixture fixture = new();
        fixture.WriteSharedDatabase();
        byte[] original = File.ReadAllBytes(fixture.Path(Master));
        var root = JsonNode.Parse(original)!.AsObject();
        var document = WorldAssembler.ReadModel(original, Master, (path, _) => File.ReadAllBytes(fixture.Path(path)), Token);
        GltfDocument leaf = new();
        leaf.Roots.Add(new() { Name = "leaf", Mesh = document.AllNodes().First(n => n.Name == "gate").Mesh });
        var written = leaf.Write("leaf.bin", Token);
        fixture.Write("data/m1/models/leaf.gltf", written.Json); fixture.Write("data/m1/models/leaf.bin", written.Binary);
        GltfDocument bridge = new();
        bridge.Roots.Add(new() { Name = "bridge", Extras = new() { [WorldGltf.Key] = new JsonObject { ["ref"] = "leaf.gltf" } } });
        fixture.Write("data/m1/models/bridge.gltf", bridge.Write("bridge.bin", Token).Json);
        foreach (var node in root["nodes"]!.AsArray().Where(n => n?["name"]?.GetValue<string>() == "gate"))
        {
            node!.AsObject().Remove("mesh");
            node["extras"] ??= new JsonObject(); node["extras"]![WorldGltf.Key] ??= new JsonObject();
            node["extras"]![WorldGltf.Key]!["ref"] = "bridge.gltf";
        }
        fixture.Write(Master, root.ToJsonString());
        SourceWorkspace workspace = new(fixture.Project);
        WorldAssembler Build()
        {
            WorldAssembler assembler = new(new SourceWorlds.DiskFiles(fixture.Project, workspace.Overlay()), Token);
            assembler.Assemble("m1.gs"); return assembler;
        }
        var before = Build();
        var picked = before.World.Nodes.Single(n => n.Name == "leaf");
        Assert.Equal(2, picked.Parents.Single().Parents.Single().Parents.Single().Parents.Count);
        Assert.Null(before.Provenance[picked].Instance);
        Assert.True(before.Provenance[picked].SharedPlacement);
        var slots = GameZWriter.NodeSlots(before.World, Token);
        SourceWorldBuild build = new("m1", "", "", [], new Dictionary<string, FileStamp>())
        { Provenance = before.Provenance.Where(p => slots.ContainsKey(p.Key)).ToDictionary(p => slots[p.Key], p => p.Value) };
        var selection = new SourceZoneSelection(slots[picked]);
        var inspection = SourceZoneEdits.Inspect(workspace, build, [selection], token: Token);
        Assert.True(inspection.RequiresSharedScope); Assert.Equal([slots[picked]], inspection.AffectedNodes);
        var face = SourceZoneEdits.FromCompiled(build, slots[picked], 0);
        Assert.True(SourceZoneEdits.Inspect(workspace, build, [face], token: Token).RequiresSharedScope);
        Assert.Throws<InvalidDataException>(() => SourceZoneEdits.PlanAssignments(workspace, build, [selection], new(77), false, Token));
        Assert.Throws<InvalidDataException>(() => SourceZoneEdits.PlanAssignments(workspace, build, [face], new(PolygonZones: [77]), false, Token));
        Assert.False(workspace.IsDirty); Assert.Equal(0, workspace.UndoCount);
        var plan = SourceZoneEdits.PlanAssignments(workspace, build, [selection], new(77), true, Token);
        workspace.Apply(plan.Label, plan.Changes.Select(c => (c.Relative, (byte[]?)c.Content)), Token);
        Assert.Equal(77u, Build().World.Nodes.Single(n => n.Name == "leaf").Zone);
        Assert.Equal(written.Json, workspace.Read("data/m1/models/leaf.gltf", Token));
        workspace.Undo(); Assert.False(workspace.IsDirty);
        workspace.Redo(); workspace.Save(Token);
        Assert.Equal(77u, Build().World.Nodes.Single(n => n.Name == "leaf").Zone);
    }

    [Fact]
    public void DefaultEditDisclosesInheritingPlacementsAndOtherMissionsButNotExplicitOverrides()
    {
        using Fixture f = new();
        GltfDocument master = new();
        master.Roots.Add(new() { Name = "inherits", Extras = new() { [WorldGltf.Key] = new JsonObject { ["ref"] = "zone.gltf" } } });
        master.Roots.Add(new() { Name = "isolated", Extras = new() { [WorldGltf.Key] = new JsonObject
            { ["ref"] = "zone.gltf", ["zoneOverrides"] = WorldGltf.WriteZoneOverride(f.Profile, Token) } } });
        f.Files.Write(Master, master.Write("m1.bin", Token).Json);
        string script = File.ReadAllText(f.Files.Path("gamegen/m1.gs"));
        f.Files.Write("gamegen/m1.gs", script.Replace("GameZWriteZBDFile", "LoadGameGen zone.gltf zone\r\nGameZWriteZBDFile", StringComparison.Ordinal));
        GltfDocument other = new();
        other.Roots.Add(new() { Name = "otherMission", Extras = new() { [WorldGltf.Key] = new JsonObject { ["ref"] = "../../m1/models/zone.gltf" } } });
        f.Files.Write("data/m2/models/m2.gltf", other.Write("m2.bin", Token).Json);
        var origins = (Dictionary<int, WorldNodeProvenance>)f.Build.Provenance;
        for (int index = 0; index < 2; index++) origins.Add(11 + index, new()
        {
            LogicalModelFile = Geometry, ModelFile = Geometry, ModelNode = 0, ZoneNode = 0, ZoneMesh = 0,
            ZonePolygons = [0, 1], ZoneOwnerPath = Master, ZoneOwnerReferences = [index],
        });
        var inspection = SourceZoneEdits.Inspect(f.Workspace, f.Build, [new(10)], token: Token);
        Assert.True(inspection.RequiresSharedScope); Assert.Equal([10, 11], inspection.AffectedNodes);
        Assert.Equal(["m1", "m2"], inspection.AffectedMissions);
        Assert.Throws<InvalidDataException>(() => SourceZoneEdits.PlanAssignments(f.Workspace, f.Build, [new(10)], new(7), false, Token));
        Assert.False(f.Workspace.IsDirty);
        f.Apply(SourceZoneEdits.PlanAssignments(f.Workspace, f.Build, [new(10)], new(7), true, Token));
        Assert.Equal(0xAABBCC07u, Assert.Single(SourceZoneEdits.Inspect(f.Workspace, f.Build, [new(11)], token: Token).Targets).Word);
        Assert.Equal(0xAABBCC03u, Assert.Single(SourceZoneEdits.Inspect(f.Workspace, f.Build, [new(12)], token: Token).Targets).Word);
        f.Apply(SourceZoneEdits.PlanLabel(f.Workspace, "m1", 3, "Floor", Token));
        int history = f.Workspace.UndoCount;
        byte[] before = f.Workspace.Read(Master, Token)!;
        var refusal = Assert.Throws<InvalidDataException>(() => SourceZoneEdits.PlanRemoveLabel(f.Workspace, "m1", 3, 4, Token));
        Assert.Contains("m2", refusal.Message); Assert.Contains("shared-scope", refusal.Message);
        Assert.Equal(history, f.Workspace.UndoCount); Assert.Equal(before, f.Workspace.Read(Master, Token));
    }

    [Fact]
    public void ReferenceOccurrenceEditPreservesOtherPlacementDefaultsAndNestedOverrides()
    {
        using Fixture f = new();
        GltfDocument branch = new();
        branch.Roots.Add(new() { Name = "nested", Extras = new() { [WorldGltf.Key] = new JsonObject { ["ref"] = "zone.gltf" } } });
        var (branchJson, _) = branch.Write("branch.bin", Token);
        f.Files.Write("data/m1/models/branch.gltf", branchJson);
        GltfDocument master = new();
        master.Roots.Add(new() { Name = "first", Extras = new() { [WorldGltf.Key] = new JsonObject { ["ref"] = "branch.gltf" } } });
        master.Roots.Add(new() { Name = "second", Extras = new() { [WorldGltf.Key] = new JsonObject { ["ref"] = "branch.gltf" } } });
        var (masterJson, _) = master.Write("m1.bin", Token);
        f.Files.Write(Master, masterJson);
        f.Build.Provenance[10].ZoneOwnerPath = Master;
        f.Build.Provenance[10].ZoneOwnerReferences = [0, 0];
        f.Apply(SourceZoneEdits.PlanAssignments(f.Workspace, f.Build, [new(10, 0)], new(PolygonZones: [8]), false, Token));
        Assert.Equal([Master], f.Workspace.DirtyFiles);
        Assert.Equal(f.GeometryBytes, f.Workspace.Read(Geometry, Token));
        var first = SourceZoneEdits.Inspect(f.Workspace, f.Build, [new(10, 0)], token: Token);
        Assert.Equal(0xFFFF0801u, Assert.Single(first.Targets).Word);
        Assert.Equal(Master, first.MasterPath); Assert.Equal([Master], first.SourcePaths);
        f.Build.Provenance[10].ZoneOwnerReferences = [1, 0];
        Assert.Equal(0xFE030102u, Assert.Single(SourceZoneEdits.Inspect(f.Workspace, f.Build, [new(10, 0)], token: Token).Targets).Word);
        f.Workspace.Undo(); Assert.False(f.Workspace.IsDirty);
        f.Workspace.Redo(); f.Workspace.Save(Token);
        Assert.Equal(f.GeometryBytes, File.ReadAllBytes(f.Files.Path(Geometry)));
        f.Build.Provenance[10].ZoneOwnerReferences = [0, 0];
        Assert.Equal(0xFFFF0801u, Assert.Single(SourceZoneEdits.Inspect(f.Workspace, f.Build, [new(10, 0)], token: Token).Targets).Word);
        // The map catalog is embedded in the script-selected master; remapping defaults creates
        // placement overrides without changing the referenced file used by another map.
        f.Apply(SourceZoneEdits.PlanLabel(f.Workspace, "m1", 3, "Floor", Token));
        Assert.Equal("Floor", Assert.Single(SourceZoneEdits.Catalog(f.Workspace, "m1", Token).Labels, z => z.Id == 3).Label);
        Assert.Throws<InvalidDataException>(() => SourceZoneEdits.PlanRemoveLabel(f.Workspace, "m1", 3, token: Token));
        f.Apply(SourceZoneEdits.PlanRemoveLabel(f.Workspace, "m1", 3, 4, Token));
        Assert.Equal(0xFFFF0801u, Assert.Single(SourceZoneEdits.Inspect(f.Workspace, f.Build, [new(10, 0)], token: Token).Targets).Word);
        f.Build.Provenance[10].ZoneOwnerReferences = [1, 0];
        Assert.Equal(0xFE040102u, Assert.Single(SourceZoneEdits.Inspect(f.Workspace, f.Build, [new(10, 0)], token: Token).Targets).Word);
        Assert.Equal(f.GeometryBytes, f.Workspace.Read(Geometry, Token));
        Assert.Equal([Master], f.Workspace.DirtyFiles);
    }

    [Theory]
    [InlineData(false, 0xFFFFFF00u)]
    [InlineData(true, 0xFFFFFF01u)]
    public void NoZonesAndAnyRemainDifferent(bool any, uint expected)
    {
        using Fixture f = new();
        f.Apply(SourceZoneEdits.PlanAssignments(f.Workspace, f.Build, [new(10, 1)], new(PolygonZones: any ? [255] : []), false, Token));
        Assert.Equal(expected, f.Current.MeshPolygons[0][1]);
    }

    [Fact]
    public void CancellationAndInvalidPolygonLeaveNoTransaction()
    {
        using Fixture f = new();
        Assert.Throws<InvalidDataException>(() => SourceZoneEdits.PlanAssignments(f.Workspace, f.Build, [new(10, 2)], new(PolygonZones: [2]), false, Token));
        using CancellationTokenSource cancel = new(); cancel.Cancel();
        Assert.Throws<OperationCanceledException>(() => SourceZoneEdits.PlanAssignments(f.Workspace, f.Build, [new(10, 0)], new(PolygonZones: [2]), false, cancel.Token));
        Assert.False(f.Workspace.IsDirty); Assert.Equal(0, f.Workspace.UndoCount);
    }

    [Fact]
    public void SelectionsUseTheSourcePolygonsInspectionReturns()
    {
        using Fixture f = new();
        // Source polygon 0 was split in two when built, so built face 2 comes from source polygon 1: a pick of either piece of
        // polygon 0 is one target, and the index inspection returns must reach the same polygon when selected again.
        f.Build.Provenance[10].ZonePolygons = [0, 0, 1];
        Assert.Equal(SourceZoneEdits.FromCompiled(f.Build, 10, 0), SourceZoneEdits.FromCompiled(f.Build, 10, 1));
        var inspected = Assert.Single(SourceZoneEdits.Inspect(f.Workspace, f.Build, [SourceZoneEdits.FromCompiled(f.Build, 10, 2)], token: Token).Targets).Target;
        Assert.Equal(1, inspected.Polygon);
        f.Apply(SourceZoneEdits.PlanAssignments(f.Workspace, f.Build, [new(10, inspected.Polygon)], new(PolygonZones: [8]), false, Token));
        Assert.Equal(0xFE030102u, f.Current.MeshPolygons[0][0]);
        Assert.Equal(0xFFFF0801u, f.Current.MeshPolygons[0][1]);
        // A source polygon the build discarded (or an index from an older layout) is refused, not applied to another face.
        f.Build.Provenance[10].ZonePolygons = [1, 1];
        Assert.Throws<InvalidDataException>(() => SourceZoneEdits.RequireBuiltPolygons(f.Build, [new(10, 0)], Token));
        Assert.Throws<InvalidDataException>(() => SourceZoneEdits.Inspect(f.Workspace, f.Build, [new(10, 0)], token: Token));
    }
}
