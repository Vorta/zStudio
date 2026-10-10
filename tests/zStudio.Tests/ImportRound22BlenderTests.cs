using System.IO;
using System.Text.Json.Nodes;
using Recoil.Zbd.Core.Sources;
using Xunit;

namespace Recoil.Zbd.Tests;

public sealed class ImportRound22BlenderTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;
    private const string Model = "data/m1/models/m1.gltf";

    [Fact]
    public void CheckoutManifestRefusesGrowthBeforeCreatingAnyExportFiles()
    {
        using SourceWorldFixture fixture = new();
        ModelWithBuffers(fixture, 64);
        string exports = fixture.Path(SourceBlender.ExportFolder);
        var error = Assert.Throws<InvalidDataException>(() => SourceBlender.Checkout(new(fixture.Project), Model, Token,
            _ => Assert.False(Directory.Exists(exports)), maximumManifestBytes: 8192));
        Assert.Contains("manifest", error.Message);
        Assert.False(Directory.Exists(exports));
        Assert.Empty(SourceBlender.Checkouts(fixture.Project, Token));
        Assert.Equal(64, JsonNode.Parse(File.ReadAllText(fixture.Path(Model)))!["buffers"]!.AsArray().Count);
    }

    [Fact]
    public void SuccessfulCheckoutManifestCanBeFoundListedAndUpdatedWithEscapedPaths()
    {
        using SourceWorldFixture fixture = new();
        ModelWithBuffers(fixture, 4);
        var checkout = SourceBlender.Checkout(new(fixture.Project), Model, Token, null, maximumManifestBytes: 16384);
        string manifest = Path.Combine(checkout.Folder, "manifest.json");
        Assert.InRange(new FileInfo(manifest).Length, 1, 16384);
        Assert.Equal(checkout.Files, SourceBlender.Find(fixture.Project, checkout.Id).Files);
        Assert.Equal(checkout.Id, Assert.Single(SourceBlender.Checkouts(fixture.Project, Token)).Id);
        foreach (var file in checkout.Files)
            Assert.True(File.Exists(Path.Combine(checkout.Folder, file.Checkout)));
        SourceBlender.RecordApplied(checkout, new("unchanged", [(Model, File.ReadAllBytes(fixture.Path(Model)))], [], ""));
        Assert.Single(SourceBlender.Find(fixture.Project, checkout.Id).Applied);
    }

    [Fact]
    public void RefusedNestedExportRemovesOnlyItsCreatedSealedGeneration()
    {
        using SourceWorldFixture fixture = new();
        ModelWithBuffers(fixture, 4);
        SourceWorkspace workspace = new(fixture.Project);
        var checkout = SourceBlender.Checkout(workspace, Model, Token);
        string exported = Path.Combine(checkout.Outbox, "nested", "deeper");
        Directory.CreateDirectory(exported);
        foreach (string file in Directory.GetFiles(Path.Combine(checkout.Folder, "input")))
            File.Copy(file, Path.Combine(exported, Path.GetFileName(file)));
        // Parsing reads and seals the model and all four buffers before discovering this model has no nodes.
        Assert.Contains("no nodes", Assert.Throws<InvalidDataException>(() =>
            SourceBlender.PlanUpdate(workspace, checkout, "nested/deeper/m1.gltf", token: Token)).Message);
        Assert.Empty(Directory.GetFileSystemEntries(Path.Combine(checkout.Folder, "sealed")));
        Assert.Equal(5, Directory.GetFiles(exported).Length);
        Assert.Equal(checkout.Id, SourceBlender.Find(fixture.Project, checkout.Id).Id);
    }

    [Fact]
    public void MissionScriptCacheLimitRefusesTheCheckoutInsteadOfSkippingTheMission()
    {
        using SourceWorldFixture fixture = new();
        ModelWithBuffers(fixture, 0);
        for (int i = 1; i <= 4; i++)
        {
            Directory.CreateDirectory(fixture.Path($"data/m{i}"));
            fixture.Write($"gamegen/m{i}.gs", string.Concat(Enumerable.Repeat("echo hello\n", 100)));
        }
        List<string> read = [];
        var error = Assert.Throws<InvalidDataException>(() => SourceBlender.Checkout(new(fixture.Project), Model, Token, read.Add,
            maximumScriptCacheBytes: 60000));
        Assert.Contains("script cache", error.Message);
        Assert.True(read.Count(p => p.EndsWith(".gs", StringComparison.Ordinal)) >= 2);
        Assert.False(Directory.Exists(fixture.Path(SourceBlender.ExportFolder)));
    }

    [Fact]
    public void SharedScriptsAreReadAndChargedOnlyOnceAcrossMissions()
    {
        using SourceWorldFixture fixture = new();
        ModelWithBuffers(fixture, 0);
        fixture.Write("gamegen/m1.gs", "source shared.gs\n");
        fixture.Write("gamegen/m2.gs", "source shared.gs\n");
        fixture.Write("gamegen/shared.gs", "echo " + new string('a', 200) + "\n");
        List<string> read = [];
        var checkout = SourceBlender.Checkout(new(fixture.Project), Model, Token, read.Add, maximumScriptCacheBytes: 3000);
        Assert.Equal(1, read.Count(p => p == "gamegen/shared.gs"));
        Assert.Equal(checkout.Id, SourceBlender.Find(fixture.Project, checkout.Id).Id);
    }

    private static void ModelWithBuffers(SourceWorldFixture fixture, int count)
    {
        JsonArray buffers = [];
        for (int i = 0; i < count; i++)
        {
            string name = $"buffer-{i}-\u00e9&'.bin";
            fixture.Write("data/m1/models/" + name, new byte[] { (byte)i });
            buffers.Add(new JsonObject { ["uri"] = Uri.EscapeDataString(name), ["byteLength"] = 1 });
        }
        fixture.Write(Model, new JsonObject
        {
            ["asset"] = new JsonObject { ["version"] = "2.0" }, ["buffers"] = buffers,
            ["nodes"] = new JsonArray(), ["scenes"] = new JsonArray(new JsonObject { ["nodes"] = new JsonArray() }), ["scene"] = 0
        }.ToJsonString());
    }
}
