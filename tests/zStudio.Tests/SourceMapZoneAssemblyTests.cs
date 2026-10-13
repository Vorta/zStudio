using System.Numerics;
using System.Text;
using System.Text.Json.Nodes;
using Recoil.Zbd.Core.Gltf;
using Recoil.Zbd.Core.Sources;
using Recoil.Zbd.Core.Worlds;
using Xunit;

namespace Recoil.Zbd.Tests;

public sealed class SourceMapZoneAssemblyTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;
    private const string Geometry = "data/common/models/turret.gltf";
    private const string First = "data/m1/models/turret.gltf", Second = "data/m1/models/turret_2.gltf";

    private sealed class Files : IProjectFiles
    {
        internal Dictionary<string, byte[]> Content { get; } = new(StringComparer.OrdinalIgnoreCase);
        internal List<string> Probes { get; } = [];
        internal List<string> Reads { get; } = [];
        public bool Exists(string relative) { Probes.Add(relative); return Content.ContainsKey(relative); }
        public byte[] Read(string relative, CancellationToken token, ProjectReadLimits limits)
        {
            token.ThrowIfCancellationRequested(); Reads.Add(relative);
            byte[] bytes = Content.TryGetValue(relative, out var found) ? found : throw new FileNotFoundException(relative);
            limits.Validate(bytes); return bytes;
        }
    }

    private static GltfDocument Model()
    {
        GltfPrimitive primitive = new() { Material = new() { Name = "surface" } };
        primitive.Positions.AddRange([Vector3.Zero, Vector3.UnitX, Vector3.UnitY,
            new(3, 0, 0), new(4, 0, 0), new(3, 1, 0)]);
        primitive.Indices.AddRange([0, 1, 2, 3, 4, 5]);
        GltfMesh mesh = new(); mesh.Primitives.Add(primitive);
        GltfDocument doc = new(); doc.Roots.Add(new() { Name = "turret", Mesh = mesh });
        return doc;
    }

    private static void Store(Files files, string path, GltfDocument doc)
    {
        string buffer = Path.ChangeExtension(path, ".bin");
        var (json, bytes) = doc.Write(Path.GetFileName(buffer));
        files.Content[path] = json; files.Content[buffer] = bytes;
    }

    private static WorldZoneProfile Profile(GltfDocument doc, uint zone, uint node = 0xAABBCC03)
        => new(WorldGltf.LayoutFingerprint(doc, Token), [new(node, false)],
            [(IReadOnlyList<uint>)new uint[] { 0xFFFFFF01, zone }], new(0x11223305, true));

    private static void StoreProfile(Files files, string path, GltfDocument document, WorldZoneProfile profile)
    {
        WorldGltf.EmbedZones(document, profile, token: Token);
        Store(files, path, document);
    }

    private static GltfDocument References(params (string Uri, uint Zone, WorldZoneProfile? Override)[] references)
    {
        GltfDocument document = new();
        foreach (var (uri, zone, profile) in references)
        {
            JsonObject values = new() { ["ref"] = uri, ["zone"] = zone };
            if (profile != null) values["zoneOverrides"] = WorldGltf.WriteZoneOverride(profile, Token);
            document.Roots.Add(new() { Name = "placement" + document.Roots.Count, Extras = new() { [WorldGltf.Key] = values } });
        }
        return document;
    }

    private static WorldAssembler Assemble(Files files, string loads, string mission = "m1")
    {
        files.Content[$"gamegen/{mission}.gs"] = Encoding.UTF8.GetBytes(
            "NewWorld world\nSetModelDirectory ../data/" + mission + "/models\n" + loads + "\nGameZWriteZBDFile gamez.zbd\n");
        WorldAssembler assembler = new(files, Token); assembler.Assemble(mission + ".gs"); return assembler;
    }

    [Fact]
    public void DistinctPhysicalFilesKeepExactMixedAssignments()
    {
        Files files = new(); var firstDocument = Model(); var secondDocument = Model();
        StoreProfile(files, First, firstDocument, Profile(firstDocument, 0xFFFF0001));
        StoreProfile(files, Second, secondDocument, Profile(secondDocument, 0xFFFF0101));
        byte[] original = files.Content[First].ToArray();
        var assembler = Assemble(files, "LoadGameGen turret.flt first\nLoadGameGen turret_2.flt second");
        var first = Assert.Single(assembler.LoadedRoots[0].Children); var second = Assert.Single(assembler.LoadedRoots[1].Children);
        Assert.Equal(new uint[] { 0xFFFFFF01, 0xFFFF0001 }, first.Model!.Polygons.Select(p => p.Zone));
        Assert.Equal(new uint[] { 0xFFFFFF01, 0xFFFF0101 }, second.Model!.Polygons.Select(p => p.Zone));
        Assert.NotSame(first.Model, second.Model);
        Assert.Equal(0xAABBCC03u, first.Zone); Assert.Equal(0u, first.Flags & WorldGltf.ZoneGate);
        Assert.Equal(0x11223305u, assembler.LoadedRoots[0].Zone);
        var origin = assembler.Provenance[second];
        Assert.Equal(Second, origin.ModelFile); Assert.Equal(Second, origin.LogicalModelFile);
        Assert.Equal(Second, origin.ZoneOwnerPath); Assert.Empty(origin.ZoneOwnerReferences);
        Assert.Null(origin.ZoneManifest); Assert.Equal(0, origin.ZoneNode); Assert.Equal(0, origin.ZoneMesh);
        Assert.Contains(First, files.Reads); Assert.Contains(Second, files.Reads);
        Assert.Equal(original, files.Content[First]);
    }

    [Fact]
    public void PolygonProvenanceSkipsDiscardedSourcePolygons()
    {
        Files files = new(); var doc = Model();
        doc.Roots[0].Mesh!.Primitives[0].Positions[2] = new(2, 0, 0);
        StoreProfile(files, First, doc, Profile(doc, 0xFFFF0701));
        var assembler = Assemble(files, "LoadGameGen turret.gltf root");
        var node = Assert.Single(assembler.LoadedRoots[0].Children);
        Assert.Equal(0xFFFF0701u, Assert.Single(node.Model!.Polygons).Zone);
        Assert.Equal(new[] { 1 }, assembler.Provenance[node].ZonePolygons);
    }

    [Fact]
    public void NestedReferencesRetainSameReadingAndSecondPathModelSharing()
    {
        Files files = new(); var first = Model(); var second = Model();
        StoreProfile(files, First, first, Profile(first, 0xFFFF0001));
        StoreProfile(files, Second, second, Profile(second, 0xFFFF0101));
        const string holder = "data/m1/models/holder.gltf";
        Store(files, holder, References(("turret.gltf", 255, null), ("turret.gltf", 255, null), ("./turret.gltf", 255, null), ("turret_2.gltf", 255, null)));
        var assembler = Assemble(files, "LoadGameGen holder.flt root");
        var children = assembler.LoadedRoots[0].Children.Select(n => Assert.Single(n.Children)).ToArray();
        Assert.Equal(4, children.Length);
        Assert.Same(children[0].Model, children[1].Model);
        Assert.NotSame(children[0].Model, children[2].Model);
        Assert.NotSame(children[2].Model, children[3].Model);
        Assert.Equal(0xFFFF0101u, children[3].Model!.Polygons[1].Zone);
        Assert.Single(files.Reads, p => p == First);
        Assert.All(children[..3], n => Assert.Equal(First, assembler.Provenance[n].ModelFile));
        Assert.Equal(Second, assembler.Provenance[children[3]].ModelFile);
    }

    [Fact]
    public void OnePhysicalFileKeepsInheritedNodeZonesAtDifferentPlacements()
    {
        Files files = new(); var model = Model();
        StoreProfile(files, First, model, Profile(model, 0xFFFF0001) with { Nodes = [new(0, true, Inherit: true)], LoadRoot = null });
        const string holder = "data/m1/models/holder.gltf";
        Store(files, holder, References(("turret.gltf", 2, null), ("turret.gltf", 3, null)));
        var assembler = Assemble(files, "LoadGameGen holder.flt root");
        var children = assembler.LoadedRoots[0].Children.Select(n => Assert.Single(n.Children)).ToArray();
        Assert.Equal(new uint[] { 2, 3 }, children.Select(n => n.Zone));
        Assert.Same(children[0].Model, children[1].Model);
    }

    [Fact]
    public void MissingReferencedFileRefusesInsteadOfFallingThroughModelDirectories()
    {
        Files files = new(); var doc = Model(); Store(files, "data/fallback/turret.gltf", doc);
        Store(files, "data/m1/models/holder.gltf", References(("turret.gltf", 255, null)));
        Assert.Throws<InvalidDataException>(() => Assemble(files,
            "SetModelDirectory ../data/fallback\nSetModelDirectory ../data/m1/models\nLoadGameGen holder.flt root"));
        Assert.DoesNotContain("data/fallback/turret.gltf", files.Reads);
    }

    [Fact]
    public void GeometryDeformationKeepsPlacementAssignmentsButChangedConnectivityRefuses()
    {
        Files files = new(); var doc = Model();
        var profile = Profile(doc, 0xFFFF0001);
        Store(files, "data/m1/models/holder.gltf", References(("turret.gltf", 255, profile)));
        doc.Roots[0].Mesh!.Primitives[0].Positions[1] = new(2, 0, 0); Store(files, First, doc);
        var accepted = Assemble(files, "LoadGameGen holder.flt root");
        Assert.Equal(0xFFFF0001u, Assert.Single(Assert.Single(accepted.LoadedRoots[0].Children).Children).Model!.Polygons[1].Zone);
        doc.Roots[0].Mesh!.Primitives[0].Indices[1] = 2;
        doc.Roots[0].Mesh!.Primitives[0].Indices[2] = 1; Store(files, First, doc);
        Assert.Contains("topology", Assert.Throws<InvalidDataException>(() => Assemble(files, "LoadGameGen holder.flt root")).Message);
    }

    [Fact]
    public void DifferentMapsHaveIndependentPlacementProfilesForTheSamePhysicalFile()
    {
        Files files = new(); var doc = Model(); StoreProfile(files, Geometry, doc, Profile(doc, 0xFFFF0701));
        byte[] original = files.Content[Geometry].ToArray();
        Store(files, "data/m1/models/m1.gltf", References(("../../common/models/turret.gltf", 255, Profile(doc, 0xFF020102))));
        Store(files, "data/m2/models/m2.gltf", References(("../../common/models/turret.gltf", 255, Profile(doc, 0xFF010202))));
        var first = Assemble(files, "LoadGameGen m1.flt root");
        var second = Assemble(files, "LoadGameGen m2.flt root", "m2");
        var firstNode = Assert.Single(Assert.Single(first.LoadedRoots[0].Children).Children);
        var secondNode = Assert.Single(Assert.Single(second.LoadedRoots[0].Children).Children);
        Assert.Equal(0xFF020102u, firstNode.Model!.Polygons[1].Zone);
        Assert.Equal(0xFF010202u, secondNode.Model!.Polygons[1].Zone);
        Assert.Equal("data/m1/models/m1.gltf", first.Provenance[firstNode].ZoneOwnerPath);
        Assert.Equal("data/m2/models/m2.gltf", second.Provenance[secondNode].ZoneOwnerPath);
        Assert.Equal(new[] { 0 }, first.Provenance[firstNode].ZoneOwnerReferences);
        Assert.Equal(original, files.Content[Geometry]);
    }

    [Fact]
    public void InlineLoadingDoesNotRequireSidecarOrVersionMarker()
    {
        Files files = new(); var doc = Model();
        doc.Roots[0].Extras = new() { [WorldGltf.Key] = new JsonObject { ["zone"] = 7 } }; Store(files, First, doc);
        var assembler = Assemble(files, "LoadGameGen turret.flt root");
        var node = Assert.Single(assembler.LoadedRoots[0].Children);
        Assert.Equal(7u, node.Zone); Assert.Null(assembler.Provenance[node].ZoneManifest);
        Assert.Equal(0, assembler.Provenance[node].ZoneNode);
        Assert.Equal(First, assembler.Provenance[node].ZoneOwnerPath);
        Assert.DoesNotContain("data/m1/meta/zones.json", files.Content.Keys);
    }
}
