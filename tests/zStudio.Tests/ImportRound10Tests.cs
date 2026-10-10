using System.Text;
using System.Buffers.Binary;
using System.Text.Json.Nodes;
using Recoil.Zbd.Core.Gltf;
using Recoil.Zbd.Core.Sources;
using Recoil.Zbd.Core.Worlds;
using Xunit;

namespace Recoil.Zbd.Tests;

public sealed class ImportRound10Tests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;
    [Fact]
    public async Task BuildsAndPreviewInputsCannotUseACheckoutBuffer()
    {
        using SourceWorldFixture fixture = new();
        const string path = "data/m1/models/m1.gltf";
        var json = JsonNode.Parse(File.ReadAllBytes(Path.Combine(fixture.Project, path)))!;
        json["buffers"]![0]!["uri"] = "../../../zstudio/export/id/input/mesh.bin";
        File.WriteAllText(Path.Combine(fixture.Project, path), json.ToJsonString());
        var report = await SourceBuilder.CheckAsync(fixture.Project, ["m1/gamez.zbd"], token: Token);
        Assert.Contains(report.Outputs, o => o.Error?.Contains("authoritative source", StringComparison.Ordinal) == true);
        var files = new SourceWorlds.DiskFiles(fixture.Project, new Dictionary<string, byte[]> { ["zstudio/export/id/input/mesh.bin"] = [1] });
        Assert.Throws<InvalidDataException>(() => files.Exists("zstudio/export/id/input/mesh.bin"));
        Assert.Throws<InvalidDataException>(() => files.Read("zstudio/export/id/input/mesh.bin", Token));
    }
    [Theory]
    [InlineData("\"pbrMetallicRoughness\":{\"metallicFactor\":0.5}")]
    [InlineData("\"pbrMetallicRoughness\":{\"metallicFactor\":0,\"roughnessFactor\":0.2}")]
    [InlineData("\"pbrMetallicRoughness\":{\"metallicRoughnessTexture\":{\"index\":0}}")]
    [InlineData("\"normalTexture\":{\"index\":0}")]
    [InlineData("\"occlusionTexture\":{\"index\":0}")]
    [InlineData("\"emissiveTexture\":{\"index\":0}")]
    [InlineData("\"pbrMetallicRoughness\":{\"metallicFactor\":0},\"emissiveFactor\":[1,0,0]")]
    public void AuthoredUnsupportedMaterialChannelsAreRefused(string field)
    {
        byte[] json = Encoding.UTF8.GetBytes("{\"asset\":{\"version\":\"2.0\"},\"materials\":[{" + field + "}]}");
        Assert.Contains("material", Assert.Throws<InvalidDataException>(() => GltfDocument.Read(json, _ => [], Token)).Message);
        Assert.Empty(GltfDocument.Read("{\"asset\":{\"version\":\"2.0\"},\"materials\":[{\"pbrMetallicRoughness\":{\"metallicFactor\":0,\"roughnessFactor\":1},\"emissiveFactor\":[0,0,0]}]}"u8, _ => [], Token).Roots);
    }

    [Theory]
    [InlineData("extras")][InlineData("unknown")]
    public void HugeJsonIsRefusedBeforeDomAllocation(string property)
    {
        byte[] json = Encoding.UTF8.GetBytes("{\"asset\":{\"version\":\"2.0\"},\"" + property + "\":\"" + new string('x', 33 * 1024 * 1024) + "\"}");
        // Warm the path without including the caller-owned input in the allocation measurement.
        GltfDocument.Read("{\"asset\":{\"version\":\"2.0\"}}"u8, _ => [], Token);
        long before = GC.GetAllocatedBytesForCurrentThread();
        Assert.Throws<InvalidDataException>(() => GltfDocument.Read(json, _ => [], Token));
        Assert.True(GC.GetAllocatedBytesForCurrentThread() - before < 1024 * 1024);
        byte[] glb = new byte[20 + (json.Length + 3) / 4 * 4]; glb.AsSpan().Fill(32);
        "glTF"u8.CopyTo(glb); BinaryPrimitives.WriteUInt32LittleEndian(glb.AsSpan(4), 2);
        BinaryPrimitives.WriteInt32LittleEndian(glb.AsSpan(8), glb.Length);
        BinaryPrimitives.WriteInt32LittleEndian(glb.AsSpan(12), glb.Length - 20);
        BinaryPrimitives.WriteUInt32LittleEndian(glb.AsSpan(16), 0x4E4F534A); json.CopyTo(glb, 20);
        before = GC.GetAllocatedBytesForCurrentThread();
        Assert.Throws<InvalidDataException>(() => GltfDocument.Read(glb, _ => [], Token));
        Assert.True(GC.GetAllocatedBytesForCurrentThread() - before < 1024 * 1024);
    }

    [Fact]
    public void ManyTinyUnknownValuesAreBoundedBeforeTheTokenTable()
    {
        byte[] json = Encoding.UTF8.GetBytes("{\"asset\":{\"version\":\"2.0\"},\"unknown\":[" + string.Join(',', Enumerable.Repeat("0", 4_000_001)) + "]}");
        GltfDocument.Read("{\"asset\":{\"version\":\"2.0\"}}"u8, _ => [], Token);
        long before = GC.GetAllocatedBytesForCurrentThread();
        Assert.Contains("tokens", Assert.Throws<InvalidDataException>(() => GltfDocument.Read(json, _ => [], Token)).Message);
        Assert.True(GC.GetAllocatedBytesForCurrentThread() - before < 1024 * 1024);
    }

    [Theory]
    [InlineData("../zstudio/export/id/input")][InlineData("../other/models")][InlineData("../DATA/../zstudio")]
    public void ScriptSearchesCannotNameWorkingOrNonSourceFolders(string path)
    {
        Assert.Null(WorldAssembler.ProjectPath(path));
        Assert.Equal("data/common/models", WorldAssembler.ProjectPath("..\\data\\common\\models"));
        Assert.Equal("gamegen/models", WorldAssembler.ProjectPath("../gamegen/models"));
    }

    [Fact]
    public void TerrainConversionPreservesImplicitRootsWithoutAScene()
    {
        using SourceWorldFixture fixture = new(); fixture.WriteTerrainDatabase();
        const string path = "data/m1/models/m1.gltf";
        string full = Path.Combine(fixture.Project, path);
        var json = JsonNode.Parse(File.ReadAllBytes(full))!.AsObject(); json.Remove("scene"); json.Remove("scenes");
        File.WriteAllText(full, json.ToJsonString());
        SourceWorkspace workspace = new(fixture.Project);
        var plan = SourceTerrainConversion.Plan(workspace, path, (new(StringComparer.Ordinal) { "ground" }, []), Token);
        Assert.True(plan.Converted > 0);
        SourceTerrainConversion.Apply(workspace, plan, Token);
        var edited = JsonNode.Parse(workspace.Read(path, Token)!)!;
        var roots = edited["scenes"]![0]!["nodes"]!.AsArray().Select(n => edited["nodes"]![n!.GetValue<int>()]!["name"]!.GetValue<string>());
        Assert.Equal(["ground", "m1_terrain", "sky"], roots);
        Assert.True(workspace.CanUndo); workspace.Undo(); Assert.False(workspace.IsDirty);
    }
}
