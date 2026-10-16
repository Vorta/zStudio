using System.Text;
using System.Text.Json.Nodes;
using zStudio.Core.Gltf;
using zStudio.Core.Sources;
using Xunit;

namespace zStudio.Tests;

public sealed class ImportRound23JsonOutputTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;
    private const string Model = "data/common/models/model.gltf";
    private static byte[] NestedMetadata(int values) => Encoding.UTF8.GetBytes(
        "{\"asset\":{\"version\":\"2.0\"},\"nodes\":[{\"name\":\"object\"}],\"ignored\":"
        + new string('[', 55) + string.Join(',', Enumerable.Repeat("0", values)) + new string(']', 55) + "}");

    [Fact]
    public void OutputBudgetStopsIndentationGrowthBeforeAllocatingTheExpandedText()
    {
        byte[] input = NestedMetadata(20_000);
        JsonNode root = JsonNode.Parse(input)!;
        long before = GC.GetAllocatedBytesForCurrentThread();
        Assert.Throws<InvalidDataException>(() => GltfJson.Write(root, true, Token, maximumBytes: 64 * 1024));
        Assert.True(GC.GetAllocatedBytesForCurrentThread() - before < 512 * 1024);
        byte[] compact = GltfJson.Write(root, false, Token);
        Assert.True(JsonNode.DeepEquals(root, JsonNode.Parse(compact))); // Unknown metadata remains intact.
    }
}
