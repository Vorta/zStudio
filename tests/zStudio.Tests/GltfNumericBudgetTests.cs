using System.Text;
using System.Text.Json.Nodes;
using Recoil.Zbd.Core.Gltf;
using Xunit;

namespace Recoil.Zbd.Tests;

public sealed class GltfNumericBudgetTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Theory]
    [InlineData("9223372036854775807.000", long.MaxValue)]
    [InlineData("-9223372036854775808e0", long.MinValue)]
    [InlineData("300e-2", 3L)]
    [InlineData("-0e99999999", 0L)]
    public void BoundedAdmissionPreservesExactIntegerValues(string text, long expected)
        => Assert.Equal(expected, GltfInteger.Int64(JsonNode.Parse(text), "test", new(token: Token)));

    [Theory]
    [InlineData("3.0000000000000000000001")]
    [InlineData("9223372036854775808")]
    [InlineData("-9223372036854775809")]
    [InlineData("1e-99999")]
    public void BoundedAdmissionNeverRounds(string text)
        => Assert.False(GltfInteger.TryInt64(JsonNode.Parse(text), out _, new(token: Token)));

    [Fact]
    public void RawLengthIsAdmittedBeforeScanningAndFailedReservationLatches()
    {
        var accepted = JsonNode.Parse("3." + new string('0', GltfInteger.MaximumNumericBytes - 2));
        Assert.Equal(3, GltfInteger.Int32(accepted));
        var refused = JsonNode.Parse("3." + new string('0', GltfInteger.MaximumNumericBytes - 1));
        GltfInteger.NumericWork empty = new(0, Token);
        Assert.Contains("4,096-byte", Assert.Throws<InvalidDataException>(() => GltfInteger.TryInt64(refused, out _, empty)).Message);
        Assert.Equal(0, empty.Used);
        // Five raw bytes cost 4*5+1 work units, independently of their numeric value.
        var value = JsonNode.Parse("3.000");
        GltfInteger.NumericWork work = new(42, Token);
        Assert.Equal(3, GltfInteger.Int32(value, work: work));
        Assert.Equal(3, GltfInteger.Int32(value, work: work));
        Assert.Equal(42, work.Used);
        Assert.Contains("work budget", Assert.Throws<InvalidDataException>(() => GltfInteger.Int32(value, work: work)).Message);
        Assert.Throws<InvalidDataException>(() => GltfInteger.Int32(JsonValue.Create(0), work: work));
        Assert.Equal(42, work.Used);
    }

    [Theory]
    [InlineData("count")]
    [InlineData("componentType")]
    [InlineData("mode")]
    [InlineData("scene")]
    [InlineData("translation")]
    [InlineData("sparseCount")]
    [InlineData("byteOffset")]
    [InlineData("wrapS")]
    public void ActualReaderBoundsOnlyInterpretedNumericTokens(string field)
    {
        JsonObject root = Triangle();
        JsonNode number = JsonNode.Parse("0." + new string('0', GltfInteger.MaximumNumericBytes))!;
        switch (field)
        {
            case "count": case "componentType": root["accessors"]![0]![field] = number; break;
            case "mode": root["meshes"]![0]!["primitives"]![0]![field] = number; break;
            case "scene": root[field] = number; break;
            case "sparseCount": root["accessors"]![0]!["sparse"] = new JsonObject { ["count"] = number }; break;
            case "byteOffset":
                root["buffers"] = new JsonArray(new JsonObject { ["uri"] = "data:application/octet-stream;base64," + new string('A', 48), ["byteLength"] = 36 });
                root["bufferViews"] = new JsonArray(new JsonObject { ["buffer"] = 0, ["byteLength"] = 36, ["byteOffset"] = number });
                root["accessors"]![0]!["bufferView"] = 0;
                break;
            case "wrapS":
                root["images"] = new JsonArray(new JsonObject { ["uri"] = "image.png" });
                root["textures"] = new JsonArray(new JsonObject { ["source"] = 0, ["sampler"] = 0 });
                root["samplers"] = new JsonArray(new JsonObject { ["wrapS"] = number });
                root["materials"]![0]!["pbrMetallicRoughness"]!["baseColorTexture"] = new JsonObject { ["index"] = 0 };
                break;
            default: root["nodes"]![0]![field] = new JsonArray(number, 0, 0); break;
        }
        byte[] bytes = Bytes(root), original = bytes.ToArray();
        Assert.Contains("4,096-byte", Assert.Throws<InvalidDataException>(() => Read(bytes)).Message);
        Assert.Equal(original, bytes);
        Assert.Single(Read(Bytes(Triangle())).Roots);
    }

    [Fact]
    public void UnknownNumericMetadataRemainsExactAndExportable()
    {
        string spelling = "3." + new string('0', GltfInteger.MaximumNumericBytes + 1);
        var root = Triangle();
        root["nodes"]![0]!["extras"] = new JsonObject { ["unknown"] = JsonNode.Parse(spelling) };
        var doc = Read(Bytes(root));
        Assert.Equal(spelling, doc.Roots.Single().Extras!["unknown"]!.ToJsonString());
        byte[] exported = GltfJson.Write(root, false, Token);
        Assert.Equal(spelling, JsonNode.Parse(exported)!["nodes"]![0]!["extras"]!["unknown"]!.ToJsonString());
    }

    [Fact]
    public void SharedAccessorRepeatedWorkHasOneImportBudgetAndFreshRetry()
    {
        var root = Triangle();
        root["accessors"]![0]!["count"] = JsonNode.Parse("3." + new string('0', 128));
        var limits = GltfDocument.ReadLimits.Default with { NumericWorkBytes = 1000 };
        Assert.Single(Read(Bytes(root), limits).Roots.Single().Mesh!.Primitives);
        var primitives = root["meshes"]![0]!["primitives"]!.AsArray();
        primitives.Add(primitives[0]!.DeepClone());
        byte[] bytes = Bytes(root), original = bytes.ToArray();
        Assert.Contains("numeric interpretation work budget", Assert.Throws<InvalidDataException>(() => Read(bytes, limits)).Message);
        Assert.Equal(original, bytes);
        Assert.Equal(2, Read(bytes).Roots.Single().Mesh!.Primitives.Count);
    }

    [Fact]
    public void NumericWorkCancellationKeepsItsTokenAndDoesNotPoisonNextImport()
    {
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(Token);
        GltfInteger.NumericWork work = new(token: cancellation.Token);
        Assert.Equal(3, GltfInteger.Int32(JsonNode.Parse("3.0"), work: work));
        cancellation.Cancel();
        var error = Assert.Throws<OperationCanceledException>(() => GltfInteger.Int32(JsonNode.Parse("3.0"), work: work));
        Assert.Equal(cancellation.Token, error.CancellationToken);
        var root = Triangle();
        root["buffers"] = new JsonArray(new JsonObject { ["uri"] = "empty.bin", ["byteLength"] = 1 });
        using var duringRead = CancellationTokenSource.CreateLinkedTokenSource(Token);
        byte[] bytes = Bytes(root);
        var readError = Assert.Throws<OperationCanceledException>(() => GltfDocument.Read(bytes, _ => { duringRead.Cancel(); return [0]; }, duringRead.Token));
        Assert.Equal(duringRead.Token, readError.CancellationToken);
        Assert.Single(GltfDocument.Read(bytes, _ => [0], Token).Roots);
    }

    private static GltfDocument Read(byte[] bytes, GltfDocument.ReadLimits? limits = null)
        => GltfDocument.Read(bytes, _ => throw new InvalidOperationException("Unexpected external buffer."), limits ?? GltfDocument.ReadLimits.Default, Token);
    private static byte[] Bytes(JsonObject root) => Encoding.UTF8.GetBytes(root.ToJsonString());
    private static JsonObject Triangle() => JsonNode.Parse("""
        {"asset":{"version":"2.0"},"scene":0,"scenes":[{"nodes":[0]}],"nodes":[{"mesh":0}],
         "materials":[{"pbrMetallicRoughness":{"metallicFactor":0}}],
         "accessors":[{"componentType":5126,"type":"VEC3","count":3}],
         "meshes":[{"primitives":[{"attributes":{"POSITION":0},"material":0}]}]}
        """)!.AsObject();
}
