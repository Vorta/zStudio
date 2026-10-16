using System.IO;
using System.Numerics;
using System.Text;
using System.Text.Json.Nodes;
using zStudio.Core;
using zStudio.Core.Formats;
using zStudio.Core.Gltf;
using zStudio.Core.Sources;
using zStudio.Core.Worlds;
using Xunit;

namespace zStudio.Tests;

/// <summary>
/// Pickup presentation is preserved by reconstruction and ordinary glTF model import.
/// </summary>
public sealed class BlenderRoundTripTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;
    private static JsonArray Nodes(JsonObject gltf) => gltf["nodes"]!.AsArray();
    private static WorldNode Node(string name, uint zone = 0xFF, WorldMaterial? material = null)
    {
        ModelBuilder builder = new();
        builder.Add(new([new(0, 0, 0), new(4, 0, 0), new(4, 0, -4), new(0, 0, -4)], [new(0, 0), new(1, 0), new(1, 1), new(0, 1)], [], [], material ?? new() { Color = new(90, 80, 70), Flags = 0xFF }));
        WorldNode node = new(name, WorldNodeClass.Object3D) { Model = builder.Finish(), Flags = WorldGltf.DefaultCarried, Zone = zone };
        node.SetPayloadInt(0, 0x28);
        return node;
    }
    private static List<WorldNode> Import(JsonObject gltf, byte[] bin) =>
        WorldGltf.Import(GltfDocument.Read(Encoding.UTF8.GetBytes(gltf.ToJsonString()), _ => bin, Token), "model.gltf", 0xFF,
            new() { World = new(), Reference = (_, _) => throw new InvalidOperationException(), TextureName = (uri, name, _) => name ?? Path.GetFileNameWithoutExtension(uri) });
    private static IEnumerable<WorldNode> Subtree(WorldNode node) => node.Children.SelectMany(Subtree).Prepend(node);

    [Fact]
    public void OnlyTheVolumeAPickupSwitchesOffIsHidden()
    {
        WorldMaterial violet = new() { Color = new(63, 15, 254), Flags = 0xFF };
        // One volume among the roots and one in the crate: FindSubNodeByName from the pickup visits the roots from last to
        // first, so it finds the crate's.
        var crate = Node("crate"); crate.Children.Add(Node("bvol", material: violet));
        var (json, bin) = WorldGltf.Export([Node("bvol", material: violet), crate], 0xFF, new() { Texture = _ => ("", 0) }).Write("ammo.bin", TestContext.Current.CancellationToken);
        var pickup = JsonNode.Parse(json)!.AsObject();
        int MaterialOf(JsonObject gltf, int node) => gltf["meshes"]![Nodes(gltf)[node]!["mesh"]!.GetValue<int>()]!["primitives"]![0]!["material"]!.GetValue<int>();
        int[] volumes = [.. Nodes(pickup).Select((n, i) => (n, i)).Where(p => (string?)p.n!["name"] == "bvol").Select(p => p.i)];
        int inCrate = volumes.Single(i => Nodes(pickup).Any(n => n!["children"] is JsonArray c && c.Any(x => x!.GetValue<int>() == i))), atRoot = volumes.Single(i => i != inCrate);
        Assert.True(WorldGltf.ApplyPresentation(pickup, _ => null, pickup: true));
        Assert.Equal("MASK", (string?)pickup["materials"]![MaterialOf(pickup, inCrate)]!["alphaMode"]);
        Assert.Null(pickup["materials"]![MaterialOf(pickup, atRoot)]!["alphaMode"]);
        // Outside a pickup the game draws a node named bvol.
        Assert.False(WorldGltf.ApplyPresentation(JsonNode.Parse(json)!.AsObject(), _ => null, pickup: false));
        // A material with a malformed base colour is left alone, not copied or repainted.
        var odd = JsonNode.Parse(json)!.AsObject();
        odd["materials"]![MaterialOf(odd, inCrate)]!["pbrMetallicRoughness"] = new JsonObject { ["baseColorFactor"] = new JsonArray(0.2, "x", 0.3, 1) };
        string before = odd.ToJsonString();
        Assert.False(WorldGltf.ApplyPresentation(odd, _ => null, pickup: true));
        Assert.Equal(before, odd.ToJsonString());

        // A volume material without engine attributes (Blender's own), made translucent: hidden, it records what import
        // read, so import reads the same values whatever an editor writes back for the look.
        var bare = JsonNode.Parse(json)!.AsObject();
        foreach (var material in bare["materials"]!.AsArray()) { material!.AsObject().Remove("extras"); material["alphaMode"] = "BLEND"; material["pbrMetallicRoughness"]!["baseColorFactor"]![3] = 0.5; }
        List<string> Surfaces(JsonObject gltf) => [.. Import(gltf, bin).SelectMany(Subtree).Where(n => n.Model != null).SelectMany(n => n.Model!.Polygons.Select(p =>
            $"{n.Name} {p.Material!.Flags:X} {p.Material.PackedColor:X} {p.Material.Color} {p.Material.Soil} {p.Flags:X} {p.Zone:X} {p.Normals.Length}"))];
        var expected = Surfaces(bare);
        Assert.Contains(expected, s => s.StartsWith("bvol 80 ", StringComparison.Ordinal));
        Assert.True(WorldGltf.ApplyPresentation(bare, _ => null, pickup: true));
        Assert.Equal(expected, Surfaces(bare));
        int hidden = MaterialOf(bare, inCrate);
        Assert.Equal(128, bare["materials"]![hidden]!["extras"]![WorldGltf.Key]!["opacity"]!.GetValue<int>());
        bare["materials"]![hidden]!["alphaMode"] = "BLEND";
        Assert.Equal(expected, Surfaces(bare));
    }

    private sealed class MemoryFiles(Dictionary<string, byte[]> files) : IProjectFiles
    {
        public Dictionary<string, byte[]> Files { get; } = files;
        public bool Exists(string relative) => Files.ContainsKey(relative);
        public byte[] Read(string relative, CancellationToken token) => Read(relative, token, ProjectReadLimits.Document);
        public byte[] Read(string relative, CancellationToken token, ProjectReadLimits limits) { token.ThrowIfCancellationRequested(); byte[] result = Files[relative]; limits.Validate(result); return result; }
    }

    [Fact]
    public void ReconstructionHidesTheVolumeOfModelsLoadedAsPickups()
    {
        // A database and a model the script loads twice: as a pickup template and as an ordinary object.
        const string Script = """
            set worldName world
            SetModelDirectory ..\data\m1\models
            SetTextureDirectory ..\data\m1\textures
            NewWorld %worldName%
            FindNode %worldName%
            GameGenSetWorld %worldName%
            FindNode %worldName%
            WorldOrigin 0.0 512.0
            WorldExtents 512.0 -512.0
            WorldPartition 256 -256
            LoadGameGen m1.flt m1.flt
            DeleteTree m1.flt
            LoadGameGen ammo.flt pu012
            LoadGameGen crate.flt crate1
            FindNode world
            AddChild crate1
            GameZWriteZBDFile ..\m1\gamez.zbd
            """;
        // Source builds use reconstructed glTF paths; reconstruction below still receives original FLT evidence.
        string sourceScript = Script.Replace("LoadGameGen m1.flt ", "LoadGameGen m1.gltf ", StringComparison.Ordinal)
            .Replace("LoadGameGen ammo.flt ", "LoadGameGen ammo.gltf ", StringComparison.Ordinal)
            .Replace("LoadGameGen crate.flt ", "LoadGameGen crate.gltf ", StringComparison.Ordinal);
        Dictionary<string, byte[]> files = new(StringComparer.Ordinal) { ["gamegen/m1.gs"] = Encoding.ASCII.GetBytes(sourceScript) };
        void Model(string stem, params WorldNode[] roots)
        {
            var (json, bin) = WorldGltf.Export(roots, 0xFF, new() { Texture = _ => ("", 0) }).Write(stem + ".bin");
            files[$"data/m1/models/{stem}.gltf"] = json; files[$"data/m1/models/{stem}.bin"] = bin;
        }
        WorldMaterial violet = new() { Color = new(63, 15, 254), Flags = 0xFF };
        Model("m1", Node("ground"));
        Model("ammo", Node("shell"), Node("bvol", material: violet));
        Model("crate", Node("body"), Node("bvol", material: new() { Color = new(10, 200, 30), Flags = 0xFF }));
        var world = new WorldAssembler(new MemoryFiles(files), Token).Assemble("m1.gs");
        byte[] bytes = GameZWriter.Write(world, Token);
        var shipped = GameZWorldReader.FromDocument(FormatRegistry.Default.OpenBytes("gamez.zbd", bytes, token: Token), Token);
        var scripts = new Dictionary<string, IReadOnlyList<IReadOnlyList<string>>>(StringComparer.OrdinalIgnoreCase) { ["m1.gs"] = GameGenScriptText.Tokenize(Script) };
        List<string> notes = [];
        var outputs = WorldSources.Reconstruct([new(1, shipped)], n => scripts.GetValueOrDefault(n), (_, name) => $"data/m1/textures/{name}.png", new HashSet<string>(), _ => 0, notes, Token);
        Assert.Contains("~hidden", Encoding.UTF8.GetString(outputs.Single(o => o.Path.EndsWith("/ammo.gltf", StringComparison.Ordinal)).Bytes));
        Assert.DoesNotContain("~hidden", Encoding.UTF8.GetString(outputs.Single(o => o.Path.EndsWith("/crate.gltf", StringComparison.Ordinal)).Bytes));
    }
}
