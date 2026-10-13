using System.Numerics;
using System.Text.Json.Nodes;
using Recoil.Zbd.Core.Gltf;
using Recoil.Zbd.Core.Worlds;
using Xunit;

namespace Recoil.Zbd.Tests;

public sealed class WorldZoneProfileTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;
    private static readonly uint[] FirstWords = [0xFFFFFF01, 0xFFFF0001, 0xFFFFFF00, 0x00010203];
    private static readonly uint[] SecondWords = [0xFFFFFF01, 0xFFFF0101, 0xFFFFFF00, 0x00010203];

    private static WorldNode Source(uint[] words, uint nodeWord = 0xABCD0007, bool gate = true)
    {
        WorldMaterial material = new() { PackedColor = 0, Color = new(30, 40, 50) };
        ModelBuilder builder = new();
        for (int i = 0; i < words.Length; i++)
        {
            float x = i * 3;
            Assert.True(builder.Add(new([new(x, 0, 0), new(x + 1, 0, 0), new(x, 1, 0)], [], [], [], material, Zone: words[i])));
        }
        WorldNode node = new("turret", WorldNodeClass.Object3D)
        {
            Model = builder.Finish(), Zone = nodeWord,
            Flags = WorldGltf.DefaultCarried | 0x70000000,
        };
        if (!gate) node.Flags &= ~WorldGltf.ZoneGate;
        node.SetPayloadInt(0, 0x28);
        return node;
    }

    private static WorldZoneExport Export(WorldNode source, Func<WorldNode, string?>? reference = null) =>
        WorldGltf.ExportZoned([source], 0xFF, new()
        {
            Texture = _ => throw new InvalidOperationException(), Token = Token,
            Reference = reference ?? (_ => null),
        });

    private static GltfDocument RoundTrip(GltfDocument document)
    {
        var (json, bytes) = document.Write("shared.bin", Token);
        return GltfDocument.Read(json, _ => bytes, Token);
    }

    [Fact]
    public void ZoneOnlyVariantsHaveIdenticalNeutralGeometryAndExactIndependentProfiles()
    {
        var first = Export(Source(FirstWords));
        var second = Export(Source(SecondWords, 0xFEDC0009, gate: false));
        var a = first.Geometry.Write("shared.bin", Token);
        var b = second.Geometry.Write("shared.bin", Token);
        Assert.Equal(a.Json, b.Json); Assert.Equal(a.Binary, b.Binary);
        Assert.Equal(first.Profile.Fingerprint, second.Profile.Fingerprint);
        Assert.Equal(FirstWords, Assert.Single(first.Profile.MeshPolygons));
        Assert.Equal(SecondWords, Assert.Single(second.Profile.MeshPolygons));
        Assert.Equal(new(0xABCD0007u, true), Assert.Single(first.Profile.Nodes));
        Assert.Equal(new(0xFEDC0009u, false), Assert.Single(second.Profile.Nodes));
        Assert.Single(first.Geometry.Roots[0].Mesh!.Primitives);
        Assert.Null(first.Geometry.Roots[0].Extras?[WorldGltf.Key]?["zoneWord"]);
        Assert.Null(first.Geometry.Roots[0].Mesh!.Primitives[0].Material!.Extras?[WorldGltf.Key]?["zone"]);
        WorldGltf.ValidateZoneProfile(RoundTrip(first.Geometry), first.Profile, Token);
    }

    [Fact]
    public void SharedGeometryDoesNotShareMutableModelsAcrossLogicalZoneProfiles()
    {
        var first = Export(Source(FirstWords)); var second = Export(Source(SecondWords, 0xFE000009, gate: false));
        var document = RoundTrip(first.Geometry); GameZWorld world = new();
        WorldGltf.ImportContext context = new()
        {
            World = world, Token = Token, Reference = (_, _) => throw new InvalidOperationException(), TextureName = (u, n, _) => n ?? u,
            ZoneProfile = (path, _) => path == "a.gltf" ? first.Profile : second.Profile,
        };
        var a = Assert.Single(WorldGltf.Import(document, "a.gltf", 0xFF, context));
        var b = Assert.Single(WorldGltf.Import(document, "b.gltf", 0xFF, context));
        Assert.NotSame(a.Model, b.Model);
        Assert.Equal(FirstWords, a.Model!.Polygons.Select(p => p.Zone));
        Assert.Equal(SecondWords, b.Model!.Polygons.Select(p => p.Zone));
        Assert.Equal(0xABCD0007u, a.Zone); Assert.Equal(0xFE000009u, b.Zone);
        Assert.NotEqual(0u, a.Flags & WorldGltf.ZoneGate); Assert.Equal(0u, b.Flags & WorldGltf.ZoneGate);
        Assert.Equal(0x70000000u, b.Flags & 0x70000000u);
        Assert.Same(a.Model.Polygons[0].Material, b.Model.Polygons[0].Material);
        a.Model.Polygons[0].Zone = 0;
        Assert.Equal(0xFFFFFF01u, b.Model.Polygons[0].Zone);
        Assert.Null(document.Roots[0].Mesh!.Primitives[0].Material!.Extras?[WorldGltf.Key]?["zone"]);
    }

    [Fact]
    public void GeometryDeformationMaterialAndTransformEditsRetainLayout()
    {
        var exported = Export(Source(FirstWords)); var doc = RoundTrip(exported.Geometry);
        var mesh = doc.Roots[0].Mesh!;
        mesh.Primitives[0].Positions[0] += new Vector3(0, 0, 2);
        mesh.Primitives[0].Material!.BaseColor = new(1, 0, 0, 1);
        doc.Roots[0].Matrix = Matrix4x4.CreateTranslation(10, 20, 30);
        WorldGltf.ValidateZoneProfile(doc, exported.Profile, Token);
        mesh.Primitives[0].Indices[0] = mesh.Primitives[0].Indices[1];
        Assert.Throws<InvalidDataException>(() => WorldGltf.ValidateZoneProfile(doc, exported.Profile, Token));
    }

    [Fact]
    public void DroppedRecordsUseSharedRecoveryOnlyWhenTheOrderedPolygonsStillMatch()
    {
        var exported = Export(Source(FirstWords)); var doc = RoundTrip(exported.Geometry);
        doc.Roots[0].Mesh!.Primitives[0].Extras = null;
        WorldGltf.ValidateZoneProfile(doc, exported.Profile, Token);
        var primitive = doc.Roots[0].Mesh!.Primitives[0];
        (primitive.Indices[0], primitive.Indices[3]) = (primitive.Indices[3], primitive.Indices[0]);
        Assert.Throws<InvalidDataException>(() => WorldGltf.ValidateZoneProfile(doc, exported.Profile, Token));
    }

    [Fact]
    public void MissingAssignmentRefusesBeforeCreatingWorldRecordsAndValidRetryWorks()
    {
        var exported = Export(Source(FirstWords)); var doc = RoundTrip(exported.Geometry); GameZWorld world = new();
        WorldZoneProfile current = exported.Profile with { MeshPolygons = [new uint[] { 0 }] };
        WorldGltf.ImportContext context = new()
        {
            World = world, Token = Token, Reference = (_, _) => throw new InvalidOperationException(), TextureName = (u, n, _) => n ?? u,
            ZoneProfile = (_, _) => current,
        };
        Assert.Throws<InvalidDataException>(() => WorldGltf.Import(doc, "turret.gltf", 0xFF, context));
        Assert.Empty(world.Models); Assert.Empty(world.Nodes); Assert.Empty(world.Materials);
        current = exported.Profile;
        Assert.Equal(FirstWords, Assert.Single(WorldGltf.Import(doc, "turret.gltf", 0xFF, context)).Model!.Polygons.Select(p => p.Zone));
    }

    [Fact]
    public void NeutralReferencesRequireEveryBindingAndKeepAuthoredSecondPath()
    {
        var holder = new WorldNode("holder", WorldNodeClass.Object3D) { Zone = 3, Flags = WorldGltf.DefaultCarried };
        var exported = Export(holder, _ => "./part.gltf");
        Assert.Equal("./part.gltf", exported.References[0]);
        Assert.Null(exported.Geometry.Roots[0].Extras?[WorldGltf.Key]?["ref"]);
        var part = Export(Source(FirstWords));
        GameZWorld world = new(); string? seen = null; string? binding = null;
        WorldGltf.ImportContext context = new()
        {
            World = world, Token = Token, TextureName = (u, n, _) => n ?? u,
            ZoneProfile = (path, _) => path == "holder.gltf" ? exported.Profile : part.Profile,
            AssetReference = (_, _) => binding,
            Reference = (uri, _) => { seen = uri; return (part.Geometry, "part.gltf"); },
        };
        Assert.Throws<InvalidDataException>(() => WorldGltf.Import(exported.Geometry, "holder.gltf", 0xFF, context));
        Assert.Empty(world.Models); Assert.Null(seen);
        binding = "./part.gltf";
        var accepted = Assert.Single(WorldGltf.Import(exported.Geometry, "holder.gltf", 0xFF, context));
        Assert.Single(accepted.Children); Assert.Equal("./part.gltf", seen);
        WorldGltf.ImportContext legacy = new() { World = new(), Token = Token, TextureName = (u, n, _) => n ?? u, Reference = (_, _) => throw new InvalidOperationException() };
        Assert.Throws<InvalidDataException>(() => WorldGltf.Import(exported.Geometry, "holder.gltf", 0xFF, legacy));
    }

    [Fact]
    public void LegacyCapturePreservesInheritedNodeWordGateAndPolygonBytesWithoutRewriting()
    {
        var source = Source(FirstWords, nodeWord: 0xCAFE0012, gate: false);
        var ordinary = WorldGltf.Export([source], 0xFF, new() { Texture = _ => throw new InvalidOperationException(), Token = Token });
        var before = ordinary.Write("model.bin", Token);
        var profile = WorldGltf.CaptureZoneProfile(ordinary, token: Token);
        Assert.Equal(new(0xCAFE0012u, false), Assert.Single(profile.Nodes));
        Assert.Equal(FirstWords, Assert.Single(profile.MeshPolygons));
        var after = ordinary.Write("model.bin", Token);
        Assert.Equal(before.Json, after.Json); Assert.Equal(before.Binary, after.Binary);
        GltfDocument inherited = new(); inherited.Roots.Add(new GltfNode { Name = "inherited" });
        Assert.Equal(7u, Assert.Single(WorldGltf.CaptureZoneProfile(inherited, 7, token: Token).Nodes).Word);
        Assert.Equal(9u, Assert.Single(WorldGltf.CaptureZoneProfile(inherited, 9, token: Token).Nodes).Word);
    }

    [Fact]
    public void EmbeddedZoneIdsRemainEditableWhileUninterpretedBytesAndPlacementAssignmentsSurvive()
    {
        uint[] words = [0xFFFFFF00, 0xFFFF0301, 0xFF070302, 0x03020103, 0x04050301, 0x11223304];
        var exported = Export(Source(words));
        var profile = exported.Profile with { LoadRoot = new(0xABCD0009, false) };
        WorldGltf.EmbedZones(exported.Geometry, profile, token: Token);
        var document = RoundTrip(exported.Geometry);
        var node = (JsonObject)document.Roots[0].Extras![WorldGltf.Key]!;
        Assert.Equal(7, node["zone"]!.GetValue<int>());
        Assert.Null(node["zoneWord"]);
        Assert.Equal(new[] { 0, 205, 171 }, ((JsonArray)node["zonePadding"]!).Select(n => n!.GetValue<int>()));
        var primitive = (JsonObject)document.Roots[0].Mesh!.Primitives[0].Extras![WorldGltf.Key]!;
        var zones = (JsonArray)primitive["polygonZones"]!;
        Assert.Empty((JsonArray)zones[0]!);
        Assert.Equal(new[] { 3 }, ((JsonArray)zones[1]!).Select(n => n!.GetValue<int>()));
        Assert.Equal(new[] { 3, 7 }, ((JsonArray)zones[2]!).Select(n => n!.GetValue<int>()));
        Assert.Equal(new[] { 1, 2, 3 }, ((JsonArray)zones[3]!).Select(n => n!.GetValue<int>()));
        Assert.Equal(new[] { 5, 4 }, ((JsonArray)primitive["polygonZonePadding"]!["4"]!).Select(n => n!.GetValue<int>()));
        Assert.Null(zones[5]);
        Assert.Equal(new[] { 4, 51, 34, 17 }, ((JsonArray)primitive["polygonZoneRaw"]!["5"]!).Select(n => n!.GetValue<int>()));
        var captured = WorldGltf.CaptureZoneProfile(document, token: Token);
        Assert.Equal(profile.Nodes, captured.Nodes);
        Assert.Equal(profile.LoadRoot, captured.LoadRoot);
        Assert.Equal(words, Assert.Single(captured.MeshPolygons));
        var overrides = WorldGltf.WriteZoneOverride(profile, Token);
        Assert.Equal(7, overrides["nodes"]![0]!["zone"]!.GetValue<int>());
        Assert.Equal(new[] { 3, 7 }, ((JsonArray)overrides["meshes"]![0]!["polygonZones"]![2]!).Select(n => n!.GetValue<int>()));
        var placed = WorldGltf.ReadZoneOverride(overrides, Token);
        Assert.Equal(profile.Nodes, placed.Nodes); Assert.Equal(profile.LoadRoot, placed.LoadRoot);
        Assert.Equal(words, Assert.Single(placed.MeshPolygons));

        node["zone"] = 13;
        ((JsonArray)zones[4]!)[0] = 13;
        var edited = WorldGltf.CaptureZoneProfile(document, token: Token);
        Assert.Equal(0xABCD000Du, Assert.Single(edited.Nodes).Word);
        Assert.Equal(0x04050D01u, Assert.Single(edited.MeshPolygons)[4]);
        var (json, binary) = document.Write("shared.bin", Token);
        var stored = (JsonObject)JsonNode.Parse(json)!;
        WorldGltf.PatchEmbeddedZones(stored, document, edited, Token);
        var reopened = GltfDocument.Read(System.Text.Encoding.UTF8.GetBytes(stored.ToJsonString()), _ => binary, Token);
        Assert.Equal(edited.Nodes, WorldGltf.CaptureZoneProfile(reopened, token: Token).Nodes);
        Assert.Equal(edited.MeshPolygons[0], WorldGltf.CaptureZoneProfile(reopened, token: Token).MeshPolygons[0]);

        node["zoneInherited"] = true;
        Assert.True(Assert.Single(WorldGltf.CaptureZoneProfile(document, token: Token).Nodes).Inherit);
        node["zone"] = 256;
        Assert.Throws<InvalidDataException>(() => WorldGltf.CaptureZoneProfile(document, token: Token));
        node["zone"] = 13;
        zones[4] = new JsonArray(3, 7);
        Assert.Throws<InvalidDataException>(() => WorldGltf.CaptureZoneProfile(document, token: Token));
        zones[4] = new JsonArray(256);
        Assert.Throws<InvalidDataException>(() => WorldGltf.CaptureZoneProfile(document, token: Token));
    }

    [Fact]
    public void SkippedDegeneratePolygonDoesNotShiftEmittedZoneAssignments()
    {
        var node = Source(FirstWords); var model = node.Model!;
        model.Polygons.Insert(1, new() { Material = model.Polygons[0].Material, Vertices = [0, 0, 0], Zone = 0x87654321 });
        var exported = Export(node);
        Assert.Equal(FirstWords, Assert.Single(exported.Profile.MeshPolygons));
        WorldGltf.ValidateZoneProfile(RoundTrip(exported.Geometry), exported.Profile, Token);
    }
}
