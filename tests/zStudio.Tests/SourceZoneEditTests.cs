using System.Numerics;
using Recoil.Zbd.Core;
using Recoil.Zbd.Core.Gltf;
using Recoil.Zbd.Core.Sources;
using Recoil.Zbd.Core.Worlds;
using Xunit;

namespace Recoil.Zbd.Tests;

public sealed class SourceZoneEditTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;
    private const string Geometry = "data/m1/models/zone.gltf", Manifest = "data/m1/meta/zones.json";

    private sealed class Fixture : IDisposable
    {
        internal SourceWorldFixture Files { get; } = new();
        internal SourceWorkspace Workspace { get; }
        internal SourceWorldBuild Build { get; }
        internal byte[] GeometryBytes { get; }
        internal WorldZoneProfile Profile { get; }
        internal Fixture(bool legacy = false, bool shared = false)
        {
            GltfPrimitive primitive = new();
            primitive.Positions.AddRange([Vector3.Zero, Vector3.UnitX, Vector3.UnitZ, new(2, 0, 0), new(3, 0, 0), new(2, 0, 1)]);
            primitive.Indices.AddRange([0, 2, 1, 3, 5, 4]);
            GltfMesh mesh = new(); mesh.Primitives.Add(primitive);
            GltfDocument doc = new(); doc.Roots.Add(new() { Name = "ground", Mesh = mesh });
            var (json, bin) = doc.Write("zone.bin", Token);
            GeometryBytes = json;
            File.WriteAllBytes(Files.Path(Geometry), json); File.WriteAllBytes(Files.Path("data/m1/models/zone.bin"), bin);
            Profile = new(WorldGltf.LayoutFingerprint(doc, Token), [new(0xAABBCC03, true)],
                [(IReadOnlyList<uint>)new uint[] { 0xFE030102, 0xCAFE0701 }], new(5, true));
            if (!legacy)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(Files.Path(Manifest))!);
                File.WriteAllBytes(Files.Path(Manifest), new SourceMapZones([new(Geometry, Geometry, Profile, [])]).Write(Token));
            }
            WorldNodeProvenance Origin() => new()
            {
                LogicalModelFile = Geometry, ModelFile = Geometry, ModelNode = 0, ZoneNode = 0, ZoneMesh = 0,
                ZonePolygons = [0, 1],
                ImportedZoneWord = 9, ImportedZoneGate = true, ZoneManifest = legacy ? null : Manifest,
            };
            Dictionary<int, WorldNodeProvenance> origins = new() { [10] = Origin() };
            if (shared) origins.Add(11, Origin());
            Build = new("m1", "", "", [], new Dictionary<string, FileStamp>()) { Provenance = origins };
            Workspace = new(Files.Project);
        }
        internal void Apply(SourceZoneEditPlan plan) => Workspace.Apply(plan.Label,
            plan.Changes.Select(x => (x.Relative, (byte[]?)x.Content)), Token);
        internal SourceMapZones Map => SourceMapZones.Parse(Workspace.Read(Manifest, Token)!, Token);
        public void Dispose() => Files.Dispose();
    }

    [Fact]
    public void PolygonEditPreservesOtherWordsGeometryAndHistory()
    {
        using Fixture f = new();
        var plan = SourceZoneEdits.PlanAssignments(f.Workspace, f.Build, [new(10, 0)], new(PolygonZones: [2, 255, 8]), false, Token);
        Assert.False(f.Workspace.IsDirty);
        f.Apply(plan);
        Assert.Equal(0x08FF0203u, f.Map.Assets[0].Profile.MeshPolygons[0][0]);
        Assert.Equal(0xCAFE0701u, f.Map.Assets[0].Profile.MeshPolygons[0][1]);
        Assert.Equal(f.Profile.Nodes, f.Map.Assets[0].Profile.Nodes);
        Assert.Equal(f.GeometryBytes, f.Workspace.Read(Geometry, Token));
        Assert.Equal([Manifest], f.Workspace.DirtyFiles);
        f.Workspace.Undo(); Assert.False(f.Workspace.IsDirty);
        f.Workspace.Redo(); Assert.Equal(0x08FF0203u, f.Map.Assets[0].Profile.MeshPolygons[0][0]);
        f.Workspace.Save(Token);
        Assert.False(f.Workspace.IsDirty);
        Assert.Equal(0x08FF0203u, SourceMapZones.Parse(File.ReadAllBytes(f.Files.Path(Manifest)), Token).Assets[0].Profile.MeshPolygons[0][0]);
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
        Assert.Equal(new WorldNodeZone(0xAABBCC07, false), f.Map.Assets[0].Profile.Nodes[0]);
        Assert.Equal(f.Profile.MeshPolygons[0], f.Map.Assets[0].Profile.MeshPolygons[0]);
    }

    [Fact]
    public void FirstLegacyEditCapturesProfileWithoutRewritingGeometry()
    {
        using Fixture f = new(legacy: true);
        Assert.False(File.Exists(f.Files.Path(Manifest)));
        var inspected = SourceZoneEdits.Inspect(f.Workspace, f.Build, [new(10)], token: Token);
        Assert.True(inspected.InitializesManifest);
        Assert.False(File.Exists(f.Files.Path(Manifest)));
        f.Apply(SourceZoneEdits.PlanAssignments(f.Workspace, f.Build, [new(10)], new(4), false, Token));
        Assert.Equal(new WorldNodeZone(4, true), f.Map.Assets[0].Profile.Nodes[0]);
        Assert.Equal(f.GeometryBytes, f.Workspace.Read(Geometry, Token));
        f.Workspace.Undo(); Assert.Null(f.Workspace.Read(Manifest, Token));
    }

    [Theory]
    [InlineData(false, 0xFFFFFF00u)]
    [InlineData(true, 0xFFFFFF01u)]
    public void NoZonesAndAnyRemainDifferent(bool any, uint expected)
    {
        using Fixture f = new();
        f.Apply(SourceZoneEdits.PlanAssignments(f.Workspace, f.Build, [new(10, 1)], new(PolygonZones: any ? [255] : []), false, Token));
        Assert.Equal(expected, f.Map.Assets[0].Profile.MeshPolygons[0][1]);
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
    public void CompiledSelectionMapsPastDroppedFacesAndDeduplicatesSplitFaces()
    {
        using Fixture f = new();
        f.Build.Provenance[10].ZonePolygons = [1, 1];
        var plan = SourceZoneEdits.PlanAssignments(f.Workspace, f.Build, [new(10, 0), new(10, 1)], new(PolygonZones: [8]), false, Token);
        f.Apply(plan);
        Assert.Equal(0xFE030102u, f.Map.Assets[0].Profile.MeshPolygons[0][0]);
        Assert.Equal(0xFFFF0801u, f.Map.Assets[0].Profile.MeshPolygons[0][1]);
        Assert.Single(SourceZoneEdits.Inspect(f.Workspace, f.Build, [new(10, 0), new(10, 1)], token: Token).Targets);
    }
}
