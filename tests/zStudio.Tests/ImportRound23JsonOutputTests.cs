using System.Text;
using System.Text.Json.Nodes;
using Recoil.Zbd.Core.Gltf;
using Recoil.Zbd.Core.Sources;
using Xunit;

namespace Recoil.Zbd.Tests;

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

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void BlenderRefusesReaderSizedInputWhoseEditedSerializationExceedsTheReaderLimit(bool update)
    {
        using SourceWorldFixture fixture = new();
        byte[] input = NestedMetadata(350_000);
        Assert.True(input.Length < 1024 * 1024);
        Assert.Single(GltfDocument.Read(input, _ => throw new InvalidOperationException(), Token).Roots);
        SourceWorkspace workspace = new(fixture.Project);
        if (!update)
        {
            fixture.Write(Model, input);
            Assert.Throws<InvalidDataException>(() => SourceBlender.Checkout(workspace, Model, Token));
            Assert.Empty(SourceBlender.Checkouts(fixture.Project, Token));
        }
        else
        {
            fixture.Write(Model, "{\"asset\":{\"version\":\"2.0\"},\"nodes\":[{\"name\":\"object\"}]}");
            var checkout = SourceBlender.Checkout(workspace, Model, Token);
            File.WriteAllBytes(Path.Combine(checkout.Outbox, "edited.gltf"), input);
            Assert.Throws<InvalidDataException>(() => SourceBlender.PlanUpdate(workspace, checkout, "edited.gltf", token: Token));
            Assert.Empty(workspace.Overlay());
            Assert.False(Directory.Exists(Path.Combine(checkout.Folder, "sealed"))
                && Directory.EnumerateDirectories(Path.Combine(checkout.Folder, "sealed")).Any());
        }
    }
}
