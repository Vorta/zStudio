using System.Numerics;
using System.Text.Json.Nodes;
using Recoil.Zbd.Core;
using Recoil.Zbd.Core.Export;
using Recoil.Zbd.Core.Formats;
using Recoil.Zbd.Core.Worlds;
using Xunit;

namespace Recoil.Zbd.Tests;

public sealed class ModelBundleBudgetTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    private sealed class Fixture : IDisposable
    {
        private readonly string root = Path.Combine(Path.GetTempPath(), "zstudio-bundle-budget-" + Guid.NewGuid().ToString("N"));
        internal readonly ZbdDocument Document;
        internal readonly AssetResolver Resolver;
        internal string Output => Path.Combine(root, "output");
        internal Fixture(int copies)
        {
            string input = Path.Combine(root, "input"); Directory.CreateDirectory(input);
            GameZWorld world = new() { NodeCapacity = 4, ModelCapacity = 1, MaterialCapacity = 1 };
            WorldNode top = new("world", WorldNodeClass.World), group = new("group<&>", WorldNodeClass.Object3D) { Flags = 4 }, leaf = new("leaf<&>", WorldNodeClass.Object3D) { Flags = 4 };
            group.SetPayloadInt(0, 8); leaf.SetPayloadInt(0, 8);
            world.Nodes.AddRange([top, group, leaf]); top.Children.Add(group); group.Parents.Add(top);
            for (int i = 0; i < copies; i++) { group.Children.Add(leaf); leaf.Parents.Add(group); }
            WorldModel model = new(); model.Vertices.AddRange([Vector3.Zero, Vector3.UnitX, Vector3.UnitZ]); model.Polygons.Add(new() { Vertices = [0, 1, 2] });
            world.Models.Add(model); leaf.Model = model;
            byte[] bytes = GameZWriter.Write(world, Token); string path = Path.Combine(input, "source&.zbd"); File.WriteAllBytes(path, bytes);
            Document = FormatRegistry.Default.OpenBytes(path, bytes, token: Token);
            Assert.DoesNotContain(Document.Diagnostics, d => d.Severity == "Error");
            Assert.Equal(copies, Document.Scene!.Nodes[1].Children.Length);
            Assert.Equal(copies, Document.Scene.Nodes[2].Parents.Length);
            Resolver = new(input);
        }
        public void Dispose() { Resolver.Dispose(); if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    [Fact]
    public async Task RepeatedParentMetadataRefusesBeforeCreatingAnyOutput()
    {
        using Fixture fixture = new(64);
        //65 occurrences pass the10,000-node cap, but repeated parent lists exceed this small aggregate allowance.
        var failure = await Assert.ThrowsAsync<InvalidDataException>(() => new ExportService(fixture.Resolver).ExportModelBundleAsync(
            fixture.Document, 1, fixture.Output, null, Token, new(Token, maximumReferences: 256)));
        Assert.Contains("metadata budget", failure.Message);
        Assert.False(Directory.Exists(fixture.Output));
    }

    [Fact]
    public async Task ModelRowsAlsoCountAndAcceptedManifestKeepsEveryIdentity()
    {
        using Fixture fixture = new(8);
        var exporter = new ExportService(fixture.Resolver);
        //Group(1+8), eight leaves(8 each), model row(8):81 retained reference values.
        await Assert.ThrowsAsync<InvalidDataException>(() => exporter.ExportModelBundleAsync(
            fixture.Document, 1, fixture.Output, null, Token, new(Token, maximumReferences: 80)));
        Assert.False(Directory.Exists(fixture.Output));
        var result = await exporter.ExportModelBundleAsync(fixture.Document, 1, fixture.Output, null, Token, new(Token, maximumReferences: 81));
        Assert.Equal(8, result.Placements); Assert.Equal(1, result.Models);
        byte[] bytes = await File.ReadAllBytesAsync(result.Manifest, Token);
        Assert.InRange(bytes.Length, 1, (int)ExportService.ModelBundleBudget.MaximumBytes);
        var manifest = JsonNode.Parse(bytes)!.AsObject();
        Assert.Equal(fixture.Document.Path, manifest["source"]!.GetValue<string>());
        var nodes = manifest["nodes"]!.AsArray(); Assert.Equal(9, nodes.Count);
        Assert.Equal("group<&>", nodes[0]!["name"]!.GetValue<string>());
        Assert.Equal(Enumerable.Repeat(2, 8), nodes[0]!["children"]!.AsArray().Select(n => n!.GetValue<int>()));
        Assert.All(nodes.Skip(1), node =>
        {
            Assert.Equal(2, node!["nodeIndex"]!.GetValue<int>());
            Assert.Equal("leaf<&>", node["name"]!.GetValue<string>());
            Assert.Equal(Enumerable.Repeat(1, 8), node["parents"]!.AsArray().Select(n => n!.GetValue<int>()));
            Assert.Equal(16, node["localTransform"]!.AsArray().Count);
            Assert.Equal(16, node["assembledTransform"]!.AsArray().Count);
        });
        Assert.Equal(Enumerable.Repeat(2, 8), manifest["models"]![0]!["nodes"]!.AsArray().Select(n => n!.GetValue<int>()));
        Assert.True(File.Exists(Path.Combine(result.Directory, "assembled.obj")));
        Assert.True(File.Exists(Path.Combine(result.Directory, "local", "model_0.obj")));
    }

    [Fact]
    public async Task EscapedNamesAndMatrixEnvelopeSpendBytesBeforeOutput()
    {
        using Fixture fixture = new(2);
        var scene = fixture.Document.Scene!;
        long bytes = 4096L + 6L * fixture.Document.Path.Length;
        foreach (int index in new[] { 1, 2, 2 })
        {
            var node = scene.Nodes[index];
            bytes += 2048L + 6L * node.Name.Length + 64L * (node.Parents.Length + node.Children.Length);
        }
        bytes += 512 + 64 * 2;
        var exporter = new ExportService(fixture.Resolver);
        await Assert.ThrowsAsync<InvalidDataException>(() => exporter.ExportModelBundleAsync(
            fixture.Document, 1, fixture.Output, null, Token, new(Token, maximumBytes: bytes - 1)));
        Assert.False(Directory.Exists(fixture.Output));
        var result = await exporter.ExportModelBundleAsync(fixture.Document, 1, fixture.Output, null, Token, new(Token, maximumBytes: bytes));
        //Independent serialized-output oracle, including default JSON escaping and indentation.
        Assert.InRange(new FileInfo(result.Manifest).Length, 1, bytes);
    }

    [Fact]
    public async Task CancellationPrecedesManifestPlanningAndOutputCreation()
    {
        using Fixture fixture = new(2);
        using CancellationTokenSource cancel = CancellationTokenSource.CreateLinkedTokenSource(Token); cancel.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new ExportService(fixture.Resolver).ExportModelBundleAsync(
            fixture.Document, 1, fixture.Output, token: cancel.Token));
        Assert.False(Directory.Exists(fixture.Output));
    }
}
