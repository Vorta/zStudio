using System.Text.Json;
using System.Text.Json.Nodes;
using Recoil.Zbd.Core;
using Xunit;

namespace Recoil.Zbd.Tests;

public sealed class JsonPreviewColdBudgetTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void FirstContainerPreviewCopiesOnlyItsBoundedChildrenAndPreservesFullExport(bool objectValue)
    {
        _ = JsonData.Preview(JsonNode.Parse(objectValue ? "{\"warm\":0}" : "[0]"), token: Token);
        string json = objectValue
            ? "{" + string.Join(',', Enumerable.Range(0, 100_000).Select(i => $"\"key{i}\":0")) + "}"
            : "[" + string.Join(',', Enumerable.Repeat("0", 200_000)) + "]";
        JsonNode cold = JsonNode.Parse(json)!;
        long before = GC.GetAllocatedBytesForCurrentThread();
        var preview = JsonData.Preview(cold, token: Token);
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        Assert.True(preview.Truncated);
        Assert.InRange(allocated, 0, 512 * 1024);
        if (objectValue)
        {
            var obj = Assert.IsType<JsonObject>(preview.Value);
            Assert.Equal(511, obj.Count);
            Assert.Equal(0, obj["key510"]!.GetValue<int>());
        }
        else
        {
            var array = Assert.IsType<JsonArray>(preview.Value);
            Assert.Equal(511, array.Count);
            Assert.Equal(0, array[510]!.GetValue<int>());
        }
        Assert.Equal(json, cold.ToJsonString());
    }

    [Theory]
    [InlineData("name")]
    [InlineData("string")]
    [InlineData("number")]
    public void ColdLargeEscapedNamesAndScalarsAreBoundedBeforeDecoding(string kind)
    {
        _ = JsonData.Preview(JsonNode.Parse("{\"warm\":\"ok\"}"), token: Token);
        string escaped = string.Concat(Enumerable.Repeat("\\u003C", 200_000));
        string json = kind switch
        {
            "name" => "{\"" + escaped + "\":1}",
            "string" => "\"" + escaped + "\"",
            _ => "1" + new string('0', 200_000)
        };
        JsonNode cold = JsonNode.Parse(json)!;
        long before = GC.GetAllocatedBytesForCurrentThread();
        var preview = JsonData.Preview(cold, token: Token);
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        Assert.True(preview.Truncated);
        Assert.InRange(allocated, 0, 32 * 1024);
        if (kind == "name") Assert.Empty(Assert.IsType<JsonObject>(preview.Value));
        else Assert.Null(preview.Value);
        // The full raw representation survives even though this bounded inspection omits it.
        Assert.Equal(json, cold.ToJsonString());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void MissingOrFailedRuntimeAccessorDisclosesTruncationWithoutHydrating(bool throws)
    {
        Func<JsonNode, JsonElement?>? accessor = throws ? _ => throw new InvalidOperationException("Unavailable runtime accessor") : null;
        _ = JsonData.PreviewCore(JsonNode.Parse("[0]"), 512, 8192, Token, accessor);
        string json = "[" + string.Join(',', Enumerable.Repeat("0", 200_000)) + "]";
        JsonNode cold = JsonNode.Parse(json)!;
        long before = GC.GetAllocatedBytesForCurrentThread();
        var preview = JsonData.PreviewCore(cold, 512, 8192, Token, accessor);
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        Assert.True(preview.Truncated);
        Assert.Empty(Assert.IsType<JsonArray>(preview.Value));
        Assert.InRange(allocated, 0, 32 * 1024);
        Assert.Equal(json, cold.ToJsonString());
        var obj = JsonData.PreviewCore(JsonNode.Parse("{\"kept\":1}"), 512, 8192, Token, accessor);
        Assert.True(obj.Truncated);
        Assert.Empty(Assert.IsType<JsonObject>(obj.Value));
        // Without a supported getter, even an in-memory container must be treated conservatively.
        var materialized = JsonData.PreviewCore(new JsonObject { ["value"] = 1 }, 512, 8192, Token, accessor);
        Assert.True(materialized.Truncated);
        Assert.Empty(Assert.IsType<JsonObject>(materialized.Value));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void OrdinarySceneMetadataRetainsItsStructuredValues(bool cold)
    {
        JsonObject metadata = new()
        {
            ["name"] = "crate_01", ["class"] = "Object3D", ["flags"] = 32u,
            ["position"] = new JsonArray(1.5f, 2f, -3f),
            ["children"] = new JsonArray(new JsonObject { ["name"] = "door", ["visible"] = true }),
            ["notes"] = "literal <value> and 😀"
        };
        string full = metadata.ToJsonString();
        if (cold) metadata = JsonNode.Parse(full)!.AsObject();
        JsonObject preview = JsonData.PreviewObject(metadata, token: Token);
        Assert.True(JsonNode.DeepEquals(metadata, preview));
        Assert.False(preview.ContainsKey("inspection_truncated"));
        Assert.Equal(full, metadata.ToJsonString());
        preview["name"] = "preview only";
        Assert.Equal("crate_01", metadata["name"]!.GetValue<string>());
    }

    [Fact]
    public void EscapedOrdinaryNamesAndStringsDecodeWithinTheBudget()
    {
        var source = JsonNode.Parse("{\"\\u003Cname\\u003E\":\"\\uD83D\\uDE00\\u003Cok\\u003E\"}")!.AsObject();
        var preview = JsonData.PreviewObject(source, token: Token);
        Assert.Equal("😀<ok>", preview["<name>"]!.GetValue<string>());
        Assert.False(preview.ContainsKey("inspection_truncated"));
    }

    [Fact]
    public void ExhaustedInspectionKeepsTheObjectContractAndChecksCancellation()
    {
        var source = JsonNode.Parse("{\"a\":1}")!.AsObject();
        var empty = JsonData.PreviewObject(source, nodes: 0, token: Token);
        Assert.True(empty["inspection_truncated"]!.GetValue<bool>());
        Assert.Single(empty);
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        Assert.Throws<OperationCanceledException>(() => JsonData.Preview(source, token: cancelled.Token));
        Assert.Equal("{\"a\":1}", source.ToJsonString());
    }

    [Fact]
    public void CustomizedScalarGraphsAreOmittedWithoutSerialization()
    {
        int[] values = new int[200_000];
        var custom = JsonValue.Create(values)!;
        _ = JsonData.Preview(JsonValue.Create(new[] { 0 }), token: Token);
        long before = GC.GetAllocatedBytesForCurrentThread();
        var preview = JsonData.Preview(custom, token: Token);
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        Assert.True(preview.Truncated);
        Assert.Null(preview.Value);
        Assert.InRange(allocated, 0, 16 * 1024);
        Assert.Same(values, custom.GetValue<int[]>());
        Assert.Equal(200_000, JsonNode.Parse(custom.ToJsonString())!.AsArray().Count);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void PrimitiveCharacterAllowanceAppliesToBothParsedAndMaterializedValues(bool cold)
    {
        JsonArray source = new(ulong.MaxValue, ulong.MaxValue, ulong.MaxValue);
        if (cold) source = JsonNode.Parse(source.ToJsonString())!.AsArray();
        var preview = JsonData.Preview(source, characters: 40, token: Token);
        Assert.True(preview.Truncated);
        var array = Assert.IsType<JsonArray>(preview.Value);
        Assert.Equal(3, array.Count);
        Assert.Equal(ulong.MaxValue, array[0]!.GetValue<ulong>());
        Assert.Equal(ulong.MaxValue, array[1]!.GetValue<ulong>());
        Assert.Null(array[2]);
        var tiny = JsonData.Preview(cold ? JsonNode.Parse("123") : JsonValue.Create(123), characters: 2, token: Token);
        Assert.True(tiny.Truncated);
        Assert.Null(tiny.Value);
        Assert.Equal(ulong.MaxValue, source[2]!.GetValue<ulong>());
    }
}
