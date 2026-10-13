using System.Text;
using System.Text.Json.Nodes;
using Recoil.Zbd.Core.Gltf;
using Recoil.Zbd.Core;
using Recoil.Zbd.Core.Formats;
using Recoil.Zbd.Core.Sources;
using Recoil.Zbd.Core.Worlds;
using Xunit;

namespace Recoil.Zbd.Tests;

public sealed class SourceMapZonesTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;
    private static readonly string Fingerprint = new('a', 64);
    private static WorldZoneProfile Profile => new(Fingerprint, [new(0xAABBCC07, true)],
        [new uint[] { 0xFFFF0001, 0xFFFF0101, 0x03010203, 0x12345600, 0xFFFFFF01 }], new(0xFF0102FF, false));
    private static SourceMapZoneAsset Asset(string path = "data/m3/models/turret.gltf") => new(path, path, Profile, []);

    [Fact]
    public void LoadRootCannotClaimIgnoredInheritance()
    {
        var inherited = Profile with { LoadRoot = new(7, true, true) };
        Assert.Throws<InvalidDataException>(() => new SourceMapZones([Asset() with { Profile = inherited }]));
        string json = Encoding.UTF8.GetString(new SourceMapZones([Asset()]).Write(Token));
        const string root = "\"loadRoot\":{\"word\":\"0xFF0102FF\",\"gate\":false}";
        Assert.Contains(root, json);
        Assert.Throws<InvalidDataException>(() => SourceMapZones.Parse(Encoding.UTF8.GetBytes(json.Replace(root,
            "\"loadRoot\":{\"word\":\"0xFF0102FF\",\"gate\":false,\"inherit\":true}", StringComparison.Ordinal)), Token));
    }

    [Fact]
    public void CodecPreservesFullWordsCountZeroOrderPaddingGateAndPhysicalReferences()
    {
        var owner = Asset("data/m3/models/map.gltf") with
        {
            References = [new(0, "data/m3/models/turret_2.gltf", "./turret_2.gltf")]
        };
        SourceMapZones zones = new([owner, Asset("data/m3/models/turret_2.gltf")], [new(7, "Water \"edge\""), new(255, "Any")]);
        var bytes = zones.Write(Token); var read = SourceMapZones.Parse(bytes, Token);
        Assert.Equal(bytes, read.Write(Token));
        Assert.True(read.TryGetAsset("DATA/M3/MODELS/TURRET_2.GLTF", out var asset));
        Assert.Equal("data/m3/models/turret_2.gltf", asset.GeometryPath);
        Assert.Equal(Profile.Nodes, asset.Profile.Nodes); Assert.Equal(Profile.LoadRoot, asset.Profile.LoadRoot);
        Assert.Equal(Profile.MeshPolygons[0], asset.Profile.MeshPolygons[0]);
        Assert.Equal("./turret_2.gltf", read.Assets[0].References[0].Spelling);
        Assert.Equal("Water \"edge\"", read.Label(7)); Assert.Equal("Zone 8", read.Label(8)); Assert.Equal("Any", read.Label(255));
    }

    [Fact]
    public void AdmissionRejectsConflictingIdentityAndUnsafeOrUnmatchedReferences()
    {
        Assert.Throws<InvalidDataException>(() => new SourceMapZones([Asset(), Asset("DATA/M3/MODELS/TURRET.GLTF")]));
        Assert.Throws<InvalidDataException>(() => new SourceMapZones([Asset("../escape.gltf")]));
        Assert.Throws<InvalidDataException>(() => new SourceMapZones([Asset("data/m3/models/not-a-model.txt")]));
        Assert.Throws<InvalidDataException>(() => new SourceMapZones([Asset() with { GeometryPath = "gamegen/model.gltf" }]));
        foreach (var reference in new[]
        {
            new SourceMapZoneReference(0, "data/m3/models/other.gltf", "turret.gltf"),
            new SourceMapZoneReference(1, "data/m3/models/turret.gltf", "turret.gltf"),
            new SourceMapZoneReference(0, "data/m3/models/turret.gltf", "../../../../escape.gltf")
        }) Assert.Throws<InvalidDataException>(() => new SourceMapZones([Asset() with { References = [reference] }]));
        var valid = new SourceMapZoneReference(0, "data/m3/models/turret.gltf", "./turret.gltf");
        Assert.Throws<InvalidDataException>(() => new SourceMapZones([Asset() with { References = [valid, valid] }]));
        Assert.Throws<InvalidDataException>(() => new SourceMapZones([Asset()], [new(1, "a"), new(1, "b")]));
        Assert.Throws<InvalidDataException>(() => new SourceMapZones([Asset()], [new(255, "Renamed")]));
    }

    [Theory]
    [InlineData("\"version\":1", "\"version\":2")]
    [InlineData("\"version\":1", "\"version\":1,\"version\":1")]
    [InlineData("\"version\":1", "\"version\":1,\"unknown\":0")]
    [InlineData("0xFFFF0001", "0x1")]
    [InlineData("0xFFFF0001", "0xGGFF0001")]
    public void MalformedMetadataCannotBecomeAZoneDocument(string original, string replacement)
    {
        string json = Encoding.UTF8.GetString(new SourceMapZones([Asset()]).Write(Token));
        Assert.Contains(original, json);
        Assert.Throws<InvalidDataException>(() => SourceMapZones.Parse(Encoding.UTF8.GetBytes(json.Replace(original, replacement, StringComparison.Ordinal)), Token));
    }

    [Fact]
    public void AggregateSerializationAndScalarLimitsApplyBeforeWritingAndCancellationCanRetry()
    {
        // Nine profiles can share one small managed input array while their serialized node words exceed 32 MiB.
        var nodes = Enumerable.Repeat(new WorldNodeZone(0xFF, false), 100_000).ToArray();
        var large = Asset() with { Profile = new(Fingerprint, nodes, []) };
        Assert.Throws<InvalidDataException>(() => new SourceMapZones(Enumerable.Range(0, 9)
            .Select(i => large with { LogicalPath = $"data/m3/models/model{i}.gltf" }).ToArray()));
        Assert.Throws<InvalidDataException>(() => new SourceMapZones([Asset()], [new(1, new('x', SourceMapZones.MaximumLabelCharacters + 1))]));
        SourceMapZones valid = new([Asset()]); byte[] before = valid.Write(Token);
        using var canceled = CancellationTokenSource.CreateLinkedTokenSource(Token); canceled.Cancel();
        Assert.ThrowsAny<OperationCanceledException>(() => valid.Write(canceled.Token));
        Assert.ThrowsAny<OperationCanceledException>(() => SourceMapZones.Parse(before, canceled.Token));
        Assert.Equal(before, valid.Write(Token));
    }

    [Fact]
    public void ReconstructionReferencesOneOriginalFileWithIndependentEmbeddedPlacementProfiles()
    {
        GameZWorld world = new();
        WorldNode root = new("loaded", WorldNodeClass.Object3D), map = new("world", WorldNodeClass.World);
        world.Nodes.Add(map); world.Nodes.Add(root);
        WorldMaterial material = new() { Color = new(1, 2, 3), Flags = 0xFF };
        foreach (uint zone in new uint[] { 0xFFFF0001, 0xFFFF0101 })
        {
            ModelBuilder builder = new();
            builder.Add(new([new(0, 0, 0), new(1, 0, 0), new(0, 1, 0)], [], [], [], material, Zone: zone)); builder.Finish();
            WorldNode reference = new("lturret.flt", WorldNodeClass.Object3D);
            WorldNode body = new("turret", WorldNodeClass.Object3D) { Model = builder.Model, Zone = zone == 0xFFFF0001 ? 0u : 1u };
            root.Children.Add(reference); reference.Parents.Add(root); reference.Children.Add(body); body.Parents.Add(reference);
            world.Nodes.Add(reference); world.Nodes.Add(body); world.Models.Add(builder.Model);
        }
        world.Materials.Add(material);
        byte[] bytes = GameZWriter.Write(world, Token);
        var shipped = GameZWorldReader.FromDocument(FormatRegistry.Default.OpenBytes("gamez.zbd", bytes, token: Token), Token);
        var script = GameGenScriptText.Tokenize("NewWorld world\nSetModelDirectory ../data/m3/models\nLoadGameGen assembly.flt loaded\nGameZWriteZBDFile gamez.zbd\n");
        var outputs = WorldSources.Reconstruct([new(3, shipped)], _ => script, (_, _) => null, new HashSet<string>(), _ => 0, [], Token);
        Assert.DoesNotContain(outputs, o => o.Path.EndsWith("/meta/zones.json", StringComparison.Ordinal));
        var files = outputs.ToDictionary(o => o.Path, o => o.Bytes, StringComparer.OrdinalIgnoreCase);
        string physical = Assert.Single(outputs, o => Path.GetFileName(o.Path) == "lturret.gltf").Path;
        string assembly = Assert.Single(outputs, o => Path.GetFileName(o.Path) == "assembly.gltf").Path;
        GltfDocument Read(string path) => GltfDocument.Read(files[path], uri => files[WorldAssembler.Relative(path, uri)], Token);
        var shared = Read(physical);
        var defaults = WorldGltf.CaptureZoneProfile(shared, token: Token);
        var references = Read(assembly).AllNodes().Where(n => n.Extras?[WorldGltf.Key]?["ref"] != null).ToArray();
        Assert.Equal(2, references.Length);
        Assert.All(references, n => Assert.Equal(physical, WorldAssembler.Relative(assembly, n.Extras![WorldGltf.Key]!["ref"]!.GetValue<string>())));
        var profiles = references.Select(n => n.Extras![WorldGltf.Key]!["zoneOverrides"] is JsonObject value
            ? WorldGltf.ReadZoneOverride(value, Token) : defaults).ToArray();
        Assert.Equal(new uint[] { 0xFFFF0001, 0xFFFF0101 }, profiles.SelectMany(p => p.MeshPolygons.SelectMany(words => words)).Order());
        Assert.Equal(new uint[] { 0, 1 }, profiles.Select(p => Assert.Single(p.Nodes).Word).Order());
        Assert.Equal(2, outputs.Count(o => o.Path.EndsWith(".gltf", StringComparison.Ordinal)));
    }
}
