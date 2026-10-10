using System.Text;
using System.Text.Json.Nodes;
using Recoil.Zbd.Core;
using Recoil.Zbd.Core.Gltf;
using Recoil.Zbd.Core.Worlds;
using Xunit;

namespace Recoil.Zbd.Tests;

public sealed class JsonShownColdBudgetTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void CustomizedValuesAreSummarizedWithoutSerializingTheirGraph(bool asText)
    {
        _ = JsonData.Shown(JsonValue.Create(new[] { 0 }), asText);
        int[] values = new int[200_000];
        values[^1] = 37;
        var cold = JsonValue.Create(values)!;
        long before = GC.GetAllocatedBytesForCurrentThread();
        string shown = JsonData.Shown(cold, asText);
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        Assert.Equal("<value omitted>", shown);
        Assert.InRange(allocated, 0, 16 * 1024);
        Assert.Same(values, cold.GetValue<int[]>());
        Assert.Equal(37, JsonNode.Parse(cold.ToJsonString())!.AsArray()[^1]!.GetValue<int>());
        // A custom kind query also serializes in System.Text.Json. Neither formatting mode may invoke it.
        Assert.Equal("<value omitted>", JsonData.Shown(JsonValue.Create(new Poison()), asText));
    }

    [Fact]
    public void OrdinaryClrScalarDiagnosticsKeepTheirAuthoredValues()
    {
        JsonValue[] values =
        [
            JsonValue.Create(true)!, JsonValue.Create(-42)!, JsonValue.Create(uint.MaxValue)!,
            JsonValue.Create(long.MinValue)!, JsonValue.Create(ulong.MaxValue)!, JsonValue.Create(1.25f)!,
            JsonValue.Create(1.5d)!, JsonValue.Create(1.75m)!, JsonValue.Create((short)-2)!,
            JsonValue.Create((ushort)2)!, JsonValue.Create((byte)3)!, JsonValue.Create((sbyte)-3)!,
            JsonValue.Create('<')!, JsonValue.Create(Guid.Parse("b2657db4-7ff7-4da2-b7a8-c494f543d126"))!,
            JsonValue.Create(new DateTime(2020, 1, 2, 3, 4, 5, DateTimeKind.Utc))!,
            JsonValue.Create(new DateTimeOffset(2020, 1, 2, 3, 4, 5, TimeSpan.Zero))!
        ];
        foreach (var value in values)
        {
            Assert.Equal(value.ToJsonString(), JsonData.Shown(value));
            Assert.Equal(value.ToJsonString(), JsonData.Shown(value, asText: true));
        }
    }

    private sealed class Poison
    {
        public string Value => throw new InvalidOperationException("Inspection must not invoke arbitrary serialization.");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void FirstContainerDiagnosticDoesNotHydrateChildrenAndFullJsonRemainsAvailable(bool objectValue)
    {
        _ = JsonData.Shown(JsonNode.Parse(objectValue ? "{\"warm\":0}" : "[0]"));
        string json = LargeContainer(objectValue);
        JsonNode cold = JsonNode.Parse(json)!;
        long before = GC.GetAllocatedBytesForCurrentThread();
        string shown = JsonData.Shown(cold);
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        Assert.Equal(objectValue ? "{…}" : "[…]", shown);
        Assert.InRange(allocated, 0, 16 * 1024);
        // Explicit serialization and lookup still have the complete original data. Do these only after measurement.
        Assert.Equal(json, cold.ToJsonString());
        if (objectValue) Assert.Equal(0, cold["key99999"]!.GetValue<int>());
        else Assert.Equal(200_000, cold.AsArray().Count);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ActualFirstImportRefusalDoesNotExpandMalformedContainerMetadata(bool objectValue)
    {
        _ = Refuse(Model(objectValue ? "{\"warm\":0}" : "[0]"));
        string flags = LargeContainer(objectValue);
        var cold = Model(flags);
        long before = GC.GetAllocatedBytesForCurrentThread();
        var error = Refuse(cold);
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        Assert.Equal($"cold.gltf: the engine value 'flags' is invalid ({(objectValue ? "{…}" : "[…]")}).", error.Message);
        Assert.InRange(allocated, 0, 128 * 1024);
        Assert.Equal(flags, cold.Roots.Single().Extras![WorldGltf.Key]!["flags"]!.ToJsonString());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ColdEscapedScalarIsClippedBeforeUnescapingAndItsValueIsPreserved(bool asText)
    {
        _ = JsonData.Shown(JsonNode.Parse("\"\\u003C\""), asText);
        const int length = 200_000;
        string json = "\"" + string.Concat(Enumerable.Repeat("\\u003C", length)) + "\"";
        JsonNode cold = JsonNode.Parse(json)!;
        long before = GC.GetAllocatedBytesForCurrentThread();
        string shown = JsonData.Shown(cold, asText);
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        Assert.Equal(JsonData.ShownCharacters + 1, shown.Length);
        Assert.StartsWith(asText ? "\\u003C" : "\"\\u003C", shown, StringComparison.Ordinal);
        Assert.EndsWith("…", shown, StringComparison.Ordinal);
        Assert.InRange(allocated, 0, 16 * 1024);
        Assert.Equal(new string('<', length), cold.GetValue<string>());
    }

    [Theory]
    [InlineData(0, "…", "…")]
    [InlineData(1, "{…", "[…")]
    [InlineData(2, "{…", "[…")]
    [InlineData(3, "{…}", "[…]")]
    public void ContainerSummariesRespectEvenSmallCharacterAllowances(int characters, string objectText, string arrayText)
    {
        Assert.Equal(objectText, JsonData.Shown(JsonNode.Parse("{\"a\":1}"), characters: characters));
        Assert.Equal(arrayText, JsonData.Shown(JsonNode.Parse("[1]"), asText: true, characters: characters));
    }

    private static string LargeContainer(bool objectValue) => objectValue
        ? "{" + string.Join(',', Enumerable.Range(0, 100_000).Select(i => $"\"key{i}\":0")) + "}"
        : "[" + string.Join(',', Enumerable.Repeat("0", 200_000)) + "]";

    private static GltfDocument Model(string flags) => GltfDocument.Read(Encoding.UTF8.GetBytes(
        "{\"asset\":{\"version\":\"2.0\"},\"scene\":0,\"scenes\":[{\"nodes\":[0]}],\"nodes\":[{\"name\":\"crate\",\"extras\":{\"" + WorldGltf.Key + "\":{\"flags\":" + flags + "}}}]}"),
        _ => throw new InvalidOperationException(), Token);

    private static InvalidDataException Refuse(GltfDocument document) => Assert.Throws<InvalidDataException>(() => WorldGltf.Import(document,
        "cold.gltf", 0xFF, new() { World = new(), Reference = (_, _) => throw new InvalidOperationException(), TextureName = (u, n, _) => n ?? u, Token = Token }));
}
