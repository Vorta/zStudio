using System.Text;
using System.Text.Json.Nodes;
using Recoil.Zbd.Core.Gltf;
using Recoil.Zbd.Core.Worlds;
using Xunit;

namespace Recoil.Zbd.Tests;

public sealed class ImportRound17Tests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;
    [Theory]
    [InlineData("\"nodes\":{}")][InlineData("\"meshes\":null")][InlineData("\"buffers\":{}")]
    [InlineData("\"nodes\":[{\"children\":{}}]")][InlineData("\"scenes\":[{\"nodes\":{}}]")]
    [InlineData("\"extensionsUsed\":\"KHR_mesh_quantization\"")]
    public void MalformedCollectionsCannotSilentlyDisappear(string member)
    {
        string json = """{"asset":{"version":"2.0"},""" + member + "}";
        Assert.Throws<InvalidDataException>(() => GltfDocument.Read(Encoding.UTF8.GetBytes(json), _ => [], Token));
    }

    [Theory]
    [InlineData(-1,0)][InlineData(0,-1)][InlineData(-1,-1)][InlineData(0,2)][InlineData(2,0)]
    public void WriterCannotPublishInvalidGridDimensions(int columns, int rows)
    {
        GameZWorld world = new(); WorldNode node = new("world",WorldNodeClass.World); world.Nodes.Add(node);
        node.SetPayloadInt(0x78,columns); node.SetPayloadInt(0x7C,rows);
        Assert.Throws<InvalidDataException>(() => GameZWriter.Write(world,Token));
    }

    [Fact]
    public void WriterBoundsCellsAcrossAllWorldsBeforeCreatingOutput()
    {
        GameZWorld world = new(); WorldArea area = new();
        for (int i=0;i<2;i++)
        {
            WorldNode node = new("world"+i,WorldNodeClass.World); world.Nodes.Add(node);
            node.SetPayloadInt(0x78,32769); node.SetPayloadInt(0x7C,1);
            node.Areas.AddRange(Enumerable.Repeat(area,32769));
        }
        long before = GC.GetAllocatedBytesForCurrentThread();
        Assert.Contains("cell count",Assert.Throws<InvalidDataException>(() => GameZWriter.Write(world,Token)).Message);
        Assert.True(GC.GetAllocatedBytesForCurrentThread()-before < 1_000_000);
    }

    [Fact]
    public void AffineTransformsRetainReflectionShearAndTranslation()
    {
        float[] values = [-2,0,0,0, 0.5f,3,0,0, 0,0,4,0, 5,6,7,1];
        JsonObject node = new() { ["matrix"] = new JsonArray(values.Select(f => (JsonNode?)JsonValue.Create(f)).ToArray()) };
        var matrix = GltfDocument.LocalTransform(node)!.Value;
        Assert.Equal(new System.Numerics.Vector3(4,12,19), System.Numerics.Vector3.Transform(new(1,2,3),matrix));
    }

    [Theory]
    [InlineData("[]")][InlineData("[{}]")][InlineData("[{\"record\":\"00\",\"vertices\":[]}]")]
    public void EmptyMeshesCannotMasqueradeAsLegacyPointRecords(string points)
    {
        string json = """{"asset":{"version":"2.0"},"meshes":[{"primitives":[],"extras":{"recoil":{"points":""" + points + "}}}]}";
        Assert.Throws<InvalidDataException>(() => GltfDocument.Read(Encoding.UTF8.GetBytes(json), _ => [], Token));
    }

    [Theory]
    [InlineData("{}")][InlineData("{\"primitives\":[]}")][InlineData("{\"primitives\":null}")]
    public void MeshesMustContainPrimitives(string mesh)
    {
        string json = "{\"asset\":{\"version\":\"2.0\"},\"meshes\":[" + mesh + "],\"nodes\":[{\"mesh\":0}]}";
        Assert.Contains("primitive", Assert.Throws<InvalidDataException>(() => GltfDocument.Read(Encoding.UTF8.GetBytes(json), _ => [], Token)).Message, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("\"EXT_mesh_gpu_instancing\"")][InlineData("{}")][InlineData("null")][InlineData("7")]
    public void RequiredExtensionsMustBeAnArray(string declaration)
    {
        string json = "{\"asset\":{\"version\":\"2.0\"},\"extensionsRequired\":" + declaration + "}";
        Assert.Contains("extensionsRequired", Assert.Throws<InvalidDataException>(() => GltfDocument.Read(Encoding.UTF8.GetBytes(json), _ => [], Token)).Message);
    }

    [Theory]
    [InlineData(3, 0.25f)][InlineData(7, -1f)][InlineData(11, 1f)][InlineData(15, 0f)][InlineData(15, 2f)]
    public void NodeMatricesCannotLoseHomogeneousTerms(int index, float value)
    {
        float[] matrix = [1,0,0,0, 0,1,0,0, 0,0,1,0, 4,5,6,1]; matrix[index] = value;
        JsonObject node = new() { ["matrix"] = new JsonArray(matrix.Select(f => (JsonNode?)JsonValue.Create(f)).ToArray()) };
        string json = new JsonObject { ["asset"] = new JsonObject { ["version"] = "2.0" }, ["nodes"] = new JsonArray(node) }.ToJsonString();
        Assert.Contains("affine", Assert.Throws<InvalidDataException>(() => GltfDocument.Read(Encoding.UTF8.GetBytes(json), _ => [], Token)).Message, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData(-512,512,-256,256)][InlineData(-512,-512,-256,-256)][InlineData(512,512,256,256)]
    public void PartitionsCannotReverseTheGridAxes(float extentX, float extentZ, float cellX, float cellZ)
    {
        WorldNode world = new("world",WorldNodeClass.World);
        world.SetPayloadFloat(0x3C,extentX); world.SetPayloadFloat(0x40,extentZ);
        Assert.Throws<InvalidDataException>(() => WorldUpdate.SetPartition(world,cellX,cellZ));
        Assert.Empty(world.Areas);
    }

    [Theory]
    [InlineData(-256,256)][InlineData(-256,-256)][InlineData(256,256)]
    public void InvalidPartitionsFailBeforeChangingTheWorld(float x, float z)
    {
        WorldNode world = new("world", WorldNodeClass.World);
        world.SetPayloadFloat(0x3C,512); world.SetPayloadFloat(0x40,-512);
        WorldUpdate.SetPartition(world,256,-256);
        var before = world.Payload.ToArray(); var areas = world.Areas.ToArray();
        Assert.Contains("cell", Assert.Throws<InvalidDataException>(() => WorldUpdate.SetPartition(world,x,z)).Message);
        Assert.Equal(before,world.Payload); Assert.Equal(areas,world.Areas);
        WorldUpdate.SetPartition(world,128,-128); Assert.Equal(16,world.Areas.Count);
    }
}
