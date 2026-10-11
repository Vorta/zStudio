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

    private static void Manifest(Files files, string mission, params SourceMapZoneAsset[] assets)
        => files.Content[SourceMapZones.PathForMission(mission)] = new SourceMapZones(assets).Write(Token);

    private static WorldAssembler Assemble(Files files, string loads, string mission = "m1")
    {
        files.Content[$"gamegen/{mission}.gs"] = Encoding.UTF8.GetBytes(
            "NewWorld world\nSetModelDirectory ../data/" + mission + "/models\n" + loads + "\nGameZWriteZBDFile gamez.zbd\n");
        WorldAssembler assembler = new(files, Token); assembler.Assemble(mission + ".gs"); return assembler;
    }

    [Fact]
    public void DirectLogicalAliasesShareOnlyPhysicalGeometryAndKeepExactMixedAssignments()
    {
        Files files = new(); var doc = Model(); Store(files, Geometry, doc);
        Manifest(files, "m1", new SourceMapZoneAsset(First, Geometry, Profile(doc, 0xFFFF0001), []), new(Second, Geometry, Profile(doc, 0xFFFF0101), []));
        byte[] original = files.Content[Geometry].ToArray();
        var assembler = Assemble(files, "LoadGameGen turret.flt first\nLoadGameGen turret_2.flt second");
        var first = Assert.Single(assembler.LoadedRoots[0].Children); var second = Assert.Single(assembler.LoadedRoots[1].Children);
        Assert.Equal(new uint[] { 0xFFFFFF01, 0xFFFF0001 }, first.Model!.Polygons.Select(p => p.Zone));
        Assert.Equal(new uint[] { 0xFFFFFF01, 0xFFFF0101 }, second.Model!.Polygons.Select(p => p.Zone));
        Assert.NotSame(first.Model, second.Model);
        Assert.Equal(0xAABBCC03u, first.Zone); Assert.Equal(0u, first.Flags & WorldGltf.ZoneGate);
        Assert.Equal(0x11223305u, assembler.LoadedRoots[0].Zone);
        var origin = assembler.Provenance[second];
        Assert.Equal(Geometry, origin.ModelFile); Assert.Equal(Second, origin.LogicalModelFile);
        Assert.Equal("data/m1/meta/zones.json", origin.ZoneManifest); Assert.Equal(0, origin.ZoneNode); Assert.Equal(0, origin.ZoneMesh);
        Assert.DoesNotContain(First, files.Reads); Assert.DoesNotContain(Second, files.Reads);
        Assert.Equal(original, files.Content[Geometry]);
    }

    [Fact]
    public void PolygonProvenanceSkipsDiscardedSourcePolygons()
    {
        Files files = new(); var doc = Model();
        doc.Roots[0].Mesh!.Primitives[0].Positions[2] = new(2, 0, 0);
        Store(files, Geometry, doc);
        Manifest(files, "m1", new SourceMapZoneAsset(First, Geometry, Profile(doc, 0xFFFF0701), []));
        var assembler = Assemble(files, "LoadGameGen turret.gltf root");
        var node = Assert.Single(assembler.LoadedRoots[0].Children);
        Assert.Equal(0xFFFF0701u, Assert.Single(node.Model!.Polygons).Zone);
        Assert.Equal(new[] { 1 }, assembler.Provenance[node].ZonePolygons);
    }

    [Fact]
    public void NestedReferencesRetainSameReadingAndSecondPathModelSharing()
    {
        Files files = new(); var model = Model(); Store(files, Geometry, model);
        const string holder = "data/m1/models/holder.gltf";
        GltfDocument document = new();
        foreach (string name in new[] { "one", "two", "three", "four" })
            document.Roots.Add(new() { Name = name, Extras = new() { [WorldGltf.Key] = new JsonObject { [WorldGltf.ZoneReference] = true } } });
        Store(files, holder, document);
        WorldZoneProfile holding = new(WorldGltf.LayoutFingerprint(document, Token),
            Enumerable.Repeat(new WorldNodeZone(255, true), 4).ToArray(), []);
        Manifest(files, "m1", new SourceMapZoneAsset(First, Geometry, Profile(model, 0xFFFF0001), []), new(Second, Geometry, Profile(model, 0xFFFF0101), []),
            new(holder, holder, holding, [new(0, First, "turret.gltf"), new(1, First, "turret.gltf"), new(2, First, "./turret.gltf"), new(3, Second, "turret_2.gltf")]));
        var assembler = Assemble(files, "LoadGameGen holder.flt root");
        var children = assembler.LoadedRoots[0].Children.Select(n => Assert.Single(n.Children)).ToArray();
        Assert.Equal(4, children.Length);
        Assert.Same(children[0].Model, children[1].Model);
        Assert.NotSame(children[0].Model, children[2].Model);
        Assert.NotSame(children[2].Model, children[3].Model);
        Assert.Equal(0xFFFF0101u, children[3].Model!.Polygons[1].Zone);
        Assert.Single(files.Reads, p => p == Geometry);
        Assert.All(children, n => Assert.Equal(Geometry, assembler.Provenance[n].ModelFile));
    }

    [Fact]
    public void APhysicalFileCannotBeSilentlyHiddenByAnotherGeometryAlias()
    {
        Files files = new(); var doc = Model(); Store(files, Geometry, doc); Store(files, First, doc);
        Manifest(files, "m1", new SourceMapZoneAsset(First, Geometry, Profile(doc, 0xFFFF0001), []));
        Assert.Contains("both as a map zone alias", Assert.Throws<InvalidDataException>(() => Assemble(files, "LoadGameGen turret.flt root")).Message);
    }

    [Fact]
    public void OneLogicalFileKeepsInheritedNodeZonesAtDifferentPlacements()
    {
        Files files = new(); var model = Model(); Store(files, Geometry, model);
        const string holder = "data/m1/models/holder.gltf";
        GltfDocument document = new();
        foreach (string name in new[] { "zone2", "zone3" })
            document.Roots.Add(new() { Name = name, Extras = new() { [WorldGltf.Key] = new JsonObject { [WorldGltf.ZoneReference] = true } } });
        Store(files, holder, document);
        var inherited = Profile(model, 0xFFFF0001) with { Nodes = [new(0, true, Inherit: true)] };
        WorldZoneProfile holding = new(WorldGltf.LayoutFingerprint(document, Token), [new(2, true), new(3, true)], []);
        Manifest(files, "m1", new(First, Geometry, inherited, []), new(holder, holder, holding,
            [new(0, First, "turret.gltf"), new(1, First, "turret.gltf")]));
        var assembler = Assemble(files, "LoadGameGen holder.flt root");
        var children = assembler.LoadedRoots[0].Children.Select(n => Assert.Single(n.Children)).ToArray();
        Assert.Equal(new uint[] { 2, 3 }, children.Select(n => n.Zone));
        Assert.Same(children[0].Model, children[1].Model);
    }

    [Fact]
    public void MissingBackingGeometryRefusesInsteadOfFallingThroughModelDirectories()
    {
        Files files = new(); var doc = Model(); Store(files, "data/fallback/turret.gltf", doc);
        Manifest(files, "m1", new SourceMapZoneAsset(First, Geometry, Profile(doc, 0xFFFF0001), []));
        Assert.Contains("does not exist", Assert.Throws<InvalidDataException>(() => Assemble(files,
            // Another spelling of the mission's folder (listed first) searches it before the fallback again.
            "SetModelDirectory ../data/fallback\nSetModelDirectory ..\\data\\m1\\models\nLoadGameGen turret.flt root")).Message);
        Assert.DoesNotContain("data/fallback/turret.gltf", files.Reads);
    }

    [Fact]
    public void GeometryDeformationKeepsAssignmentsButChangedConnectivityRefuses()
    {
        Files files = new(); var doc = Model();
        Manifest(files, "m1", new SourceMapZoneAsset(First, Geometry, Profile(doc, 0xFFFF0001), []));
        doc.Roots[0].Mesh!.Primitives[0].Positions[1] = new(2, 0, 0); Store(files, Geometry, doc);
        var accepted = Assemble(files, "LoadGameGen turret.flt root");
        Assert.Equal(0xFFFF0001u, Assert.Single(accepted.LoadedRoots[0].Children).Model!.Polygons[1].Zone);
        doc.Roots[0].Mesh!.Primitives[0].Indices[1] = 2;
        doc.Roots[0].Mesh!.Primitives[0].Indices[2] = 1; Store(files, Geometry, doc);
        Assert.Contains("topology", Assert.Throws<InvalidDataException>(() => Assemble(files, "LoadGameGen turret.flt root")).Message);
    }

    [Fact]
    public void DifferentMapsHaveIndependentProfilesForTheSamePhysicalFile()
    {
        Files files = new(); var doc = Model(); Store(files, Geometry, doc);
        const string other = "data/m2/models/turret.gltf";
        Manifest(files, "m1", new SourceMapZoneAsset(First, Geometry, Profile(doc, 0xFF020102), []));
        Manifest(files, "m2", new SourceMapZoneAsset(other, Geometry, Profile(doc, 0xFF010202), []));
        var first = Assemble(files, "LoadGameGen turret.flt root");
        var second = Assemble(files, "LoadGameGen turret.flt root", "m2");
        Assert.Equal(0xFF020102u, Assert.Single(first.LoadedRoots[0].Children).Model!.Polygons[1].Zone);
        Assert.Equal(0xFF010202u, Assert.Single(second.LoadedRoots[0].Children).Model!.Polygons[1].Zone);
    }

    [Fact]
    public void LegacyLoadingRecordsAbsentManifestWithoutChangingItsInlineZone()
    {
        Files files = new(); var doc = Model();
        doc.Roots[0].Extras = new() { [WorldGltf.Key] = new JsonObject { ["zone"] = 7 } }; Store(files, First, doc);
        var assembler = Assemble(files, "LoadGameGen turret.flt root");
        var node = Assert.Single(assembler.LoadedRoots[0].Children);
        Assert.Equal(7u, node.Zone); Assert.Null(assembler.Provenance[node].ZoneManifest);
        Assert.Equal(0, assembler.Provenance[node].ZoneNode);
        Assert.Contains("data/m1/meta/zones.json", files.Probes);
        Assert.DoesNotContain("data/m1/meta/zones.json", files.Content.Keys);
    }
}
