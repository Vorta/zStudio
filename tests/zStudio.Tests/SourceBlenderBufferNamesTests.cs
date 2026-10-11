using System.IO;
using System.Text.Json.Nodes;
using Recoil.Zbd.Core.Sources;
using Xunit;

namespace Recoil.Zbd.Tests;

public sealed class SourceBlenderBufferNamesTests
{
    private const string Model = "data/m1/models/m1.gltf";
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Fact]
    public void ThousandsOfCollidingBufferNamesKeepBoundedNamingWork()
    {
        using SourceWorldFixture fixture = new();
        string[] paths = [.. Enumerable.Range(0, 2000).Select(i => $"parts{i}/same.bin")];
        AddBuffers(fixture, paths);
        long before = GC.GetAllocatedBytesForCurrentThread();
        var checkout = SourceBlender.Checkout(new SourceWorkspace(fixture.Project), Model, Token);
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        Assert.InRange(allocated, 0, 96L * 1024 * 1024);
        var copies = checkout.Files.Where(f => f.Project.Contains("/parts", StringComparison.Ordinal)).ToArray();
        Assert.Equal(2000, copies.Length);
        for (int i = 0; i < copies.Length; i++)
        {
            Assert.Equal("input/" + (i == 0 ? "same.bin" : $"same.{i}.bin"), copies[i].Checkout);
            Assert.Equal(new byte[] { (byte)i }, File.ReadAllBytes(Path.Combine(checkout.Folder, copies[i].Checkout)));
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void SuffixNamesFromAnotherBaseAndRepeatedReferencesKeepFirstFreeOrder(bool suffixFirst)
    {
        using SourceWorldFixture fixture = new();
        string[] paths = suffixFirst
            ? ["a/same.1.bin", "b/same.bin", "c/SAME.bin", "d/same.1.bin", "e/same.bin", "b/same.bin"]
            : ["a/same.bin", "b/same.bin", "c/same.1.bin", "d/SAME.bin", "e/same.1.bin", "a/same.bin"];
        AddBuffers(fixture, paths);
        var checkout = SourceBlender.Checkout(new SourceWorkspace(fixture.Project), Model, Token);
        var root = JsonNode.Parse(File.ReadAllBytes(checkout.Input))!;
        var names = root["buffers"]!.AsArray().Skip(1).Select(b => b!["uri"]!.GetValue<string>()).ToArray();
        Assert.Equal(suffixFirst
            ? ["same.1.bin", "same.bin", "SAME.2.bin", "same.1.1.bin", "same.3.bin", "same.bin"]
            : ["same.bin", "same.1.bin", "same.1.1.bin", "SAME.2.bin", "same.1.2.bin", "same.bin"], names);
        Assert.Equal(5, checkout.Files.Count(f => paths.Any(p => f.Project == "data/m1/models/" + p)));
    }

    private static void AddBuffers(SourceWorldFixture fixture, IReadOnlyList<string> paths)
    {
        var root = JsonNode.Parse(File.ReadAllBytes(fixture.Path(Model)))!;
        var buffers = root["buffers"]!.AsArray();
        for (int i = 0; i < paths.Count; i++)
        {
            buffers.Add(new JsonObject { ["uri"] = paths[i], ["byteLength"] = 1 });
            fixture.Write("data/m1/models/" + paths[i], [(byte)i]);
        }
        fixture.Write(Model, root.ToJsonString());
    }
}
