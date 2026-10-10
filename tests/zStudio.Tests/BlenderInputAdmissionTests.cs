using System.IO;
using System.Text;
using System.Text.Json.Nodes;
using Recoil.Zbd.Core.Sources;
using Xunit;

namespace Recoil.Zbd.Tests;

/// <summary>Small budgets exercise the actual checkout/update readers, including external trailing bytes.</summary>
public sealed class BlenderInputAdmissionTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;
    private const string Model = "data/m1/models/admission.gltf";
    private const string Json = """{"asset":{"version":"2.0"},"nodes":[{"name":"ground"}],"scenes":[{"nodes":[0]}]}""";

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void DistinctActualBufferBytesMustFitBeforeTheNextRead(bool image)
    {
        using SourceWorldFixture fixture = new();
        var (workspace, checkout) = Checkout(fixture);
        JsonObject root = JsonNode.Parse(Json)!.AsObject();
        root["buffers"] = new JsonArray(new JsonObject { ["uri"] = "first.bin", ["byteLength"] = 1 });
        if (image) root["images"] = new JsonArray(new JsonObject { ["uri"] = "second.png" });
        else root["buffers"]!.AsArray().Add(new JsonObject { ["uri"] = "second.bin", ["byteLength"] = 1 });
        byte[] json = Encoding.UTF8.GetBytes(root.ToJsonString());
        File.WriteAllBytes(Path.Combine(checkout.Outbox, "edit.gltf"), json);
        File.WriteAllBytes(Path.Combine(checkout.Outbox, "first.bin"), new byte[128]);
        File.WriteAllBytes(Path.Combine(checkout.Outbox, image ? "second.png" : "second.bin"), new byte[128]);
        byte[] original = workspace.Read(Model, Token)!;
        byte[] manifest = File.ReadAllBytes(Path.Combine(checkout.Folder, "manifest.json"));

        // Both declared lengths fit. The second actual file must be refused by SourceRead's opened-length
        // admission with 127 remaining, before PNG decoding (the image case deliberately is not a PNG).
        var error = Assert.Throws<InvalidDataException>(() => SourceBlender.PlanUpdate(workspace, checkout, "edit.gltf", false, Token, json.Length + 255));
        Assert.Contains("exceeds 127 bytes", error.Message);
        Assert.Equal(original, workspace.Read(Model, Token));
        Assert.Equal(manifest, File.ReadAllBytes(Path.Combine(checkout.Folder, "manifest.json")));
        Assert.Empty(Directory.GetDirectories(Path.Combine(checkout.Folder, "sealed")));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ExactAggregateBoundaryAcceptsActualBytesAndCachesRepeatedIdentity(bool image)
    {
        using SourceWorldFixture fixture = new();
        var (workspace, checkout) = Checkout(fixture);
        JsonObject root = JsonNode.Parse(Json)!.AsObject();
        byte[] data = image ? File.ReadAllBytes(fixture.Path("data/m1/textures/rock.png")) : new byte[128];
        string name = image ? "rock.png" : "shared.bin";
        root[image ? "images" : "buffers"] = image
            ? new JsonArray(new JsonObject { ["uri"] = name }, new JsonObject { ["uri"] = "./" + name })
            : new JsonArray(new JsonObject { ["uri"] = name, ["byteLength"] = 1 }, new JsonObject { ["uri"] = "./" + name, ["byteLength"] = 1 });
        byte[] json = Encoding.UTF8.GetBytes(root.ToJsonString());
        File.WriteAllBytes(Path.Combine(checkout.Outbox, "edit.gltf"), json);
        File.WriteAllBytes(Path.Combine(checkout.Outbox, name), data);
        var plan = SourceBlender.PlanUpdate(workspace, checkout, "edit.gltf", false, Token, json.Length + data.Length);
        Assert.Equal(2, Directory.GetFiles(plan.Sealed).Length);
        Assert.Equal(data, File.ReadAllBytes(Path.Combine(plan.Sealed, name)));
        Assert.Equal(Encoding.UTF8.GetBytes(Json), workspace.Read(Model, Token));
    }

    [Fact]
    public void SelectedJsonIsAdmittedAgainstBothTypeAndRemainingLimits()
    {
        using SourceWorldFixture fixture = new();
        var (workspace, checkout) = Checkout(fixture);
        byte[] json = Encoding.UTF8.GetBytes(Json);
        File.WriteAllBytes(Path.Combine(checkout.Outbox, "edit.gltf"), json);
        foreach (var limits in new[] { (Total: 4096L, Json: json.Length - 1), (Total: (long)json.Length - 1, Json: 4096) })
        {
            var error = Assert.Throws<InvalidDataException>(() => SourceBlender.PlanUpdate(workspace, checkout, "edit.gltf", false, Token, limits.Total, limits.Json));
            Assert.Contains($"exceeds {json.Length - 1:N0} bytes", error.Message);
            Assert.Empty(Directory.GetDirectories(Path.Combine(checkout.Folder, "sealed")));
        }
        var plan = SourceBlender.PlanUpdate(workspace, checkout, "edit.gltf", false, Token, json.Length, json.Length);
        Assert.Single(Directory.GetFiles(plan.Sealed));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void CheckoutJsonLimitAppliesToDiskAndPendingWorkspaceContent(bool pending)
    {
        using SourceWorldFixture fixture = new();
        fixture.Write(Model, Json);
        SourceWorkspace workspace = new(fixture.Project);
        byte[] content = Encoding.UTF8.GetBytes(Json + " ");
        if (pending) workspace.Apply("Pending model", [(Model, (byte[]?)content)], Token);
        else File.WriteAllBytes(fixture.Path(Model), content);
        var error = Assert.Throws<InvalidDataException>(() => SourceBlender.Checkout(workspace, Model, Token, null, maximumJsonBytes: content.Length - 1));
        Assert.Contains(pending ? $"{content.Length - 1:N0}-byte limit" : $"exceeds {content.Length - 1:N0} bytes", error.Message);
        Assert.Equal(content, workspace.Read(Model, Token));
        Assert.Empty(SourceBlender.Checkouts(fixture.Project, Token));
        var checkout = SourceBlender.Checkout(workspace, Model, Token, null, maximumJsonBytes: content.Length);
        Assert.True(File.Exists(checkout.Input));
    }

    [Theory]
    [InlineData("edit.glb")]
    [InlineData("edit.gltf")]
    public void BinaryContainersRemainRefusedRegardlessOfExportName(string name)
    {
        using SourceWorldFixture fixture = new();
        var (workspace, checkout) = Checkout(fixture);
        // A valid GLB 2 container with JSON-only content: no extension-based assumption of support.
        byte[] json = Encoding.UTF8.GetBytes(Json.PadRight((Json.Length + 3) / 4 * 4));
        using MemoryStream bytes = new();
        using (BinaryWriter writer = new(bytes, Encoding.UTF8, leaveOpen: true))
        {
            writer.Write(0x46546C67); writer.Write(2); writer.Write(20 + json.Length);
            writer.Write(json.Length); writer.Write(0x4E4F534A); writer.Write(json);
        }
        File.WriteAllBytes(Path.Combine(checkout.Outbox, name), bytes.ToArray());
        Assert.Throws<InvalidDataException>(() => SourceBlender.PlanUpdate(workspace, checkout, name, token: Token));
        File.WriteAllBytes(fixture.Path(Model), bytes.ToArray());
        Assert.Throws<InvalidDataException>(() => SourceBlender.Checkout(workspace, Model, Token));
        string sealedRoot = Path.Combine(checkout.Folder, "sealed");
        Assert.False(Directory.Exists(sealedRoot) && Directory.GetDirectories(sealedRoot).Length != 0);
    }

    [Fact]
    public void CancelledAdmissionPreservesExistingSealedPlans()
    {
        using SourceWorldFixture fixture = new();
        var (workspace, checkout) = Checkout(fixture);
        File.WriteAllText(Path.Combine(checkout.Outbox, "edit.gltf"), Json);
        var plan = SourceBlender.PlanUpdate(workspace, checkout, "edit.gltf", token: Token);
        byte[] sealedJson = File.ReadAllBytes(Path.Combine(plan.Sealed, "edit.gltf"));
        using CancellationTokenSource cancelled = new(); cancelled.Cancel();
        Assert.ThrowsAny<OperationCanceledException>(() => SourceBlender.PlanUpdate(workspace, checkout, "edit.gltf", token: cancelled.Token));
        Assert.Equal(sealedJson, File.ReadAllBytes(Path.Combine(plan.Sealed, "edit.gltf")));
        Assert.Single(Directory.GetDirectories(Path.Combine(checkout.Folder, "sealed")));
    }

    private static (SourceWorkspace Workspace, BlenderCheckout Checkout) Checkout(SourceWorldFixture fixture)
    {
        fixture.Write(Model, Json);
        SourceWorkspace workspace = new(fixture.Project);
        return (workspace, SourceBlender.Checkout(workspace, Model, Token));
    }
}
