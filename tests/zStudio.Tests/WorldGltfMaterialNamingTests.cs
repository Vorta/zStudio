using System.Numerics;
using Recoil.Zbd.Core.Formats;
using Recoil.Zbd.Core.Worlds;
using Xunit;

namespace Recoil.Zbd.Tests;

public sealed class WorldGltfMaterialNamingTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Fact]
    public void DistinctMaterialsWithOneNameDoNotRetryEveryEarlierSuffix()
    {
        const int count = 4_000;
        var world = World([.. Enumerable.Range(0, count).Select(i => new WorldMaterial { Color = new(200), Field14 = i })]);
        byte[] input = GameZWriter.Write(world, Token);
        var read = GameZWorldReader.FromDocument(FormatRegistry.Default.OpenBytes("world.zbd", input, token: Token), Token);
        Assert.Equal(count, read.Materials.Count);
        _ = WorldGltf.Export([World([new WorldMaterial()]).Nodes[0]], 255, Context());

        long start = GC.GetAllocatedBytesForCurrentThread();
        var exported = WorldGltf.Export([read.Nodes[0]], 255, Context());
        long allocated = GC.GetAllocatedBytesForCurrentThread() - start;

        Assert.True(allocated < 64L * 1024 * 1024, $"Export allocated {allocated:N0} bytes for {count} materials.");
        var materials = exported.Roots[0].Mesh!.Primitives.Select(p => p.Material!).ToArray();
        Assert.Equal(count, materials.Length);
        Assert.Equal(count, materials.Select(m => m.Name).Distinct(StringComparer.Ordinal).Count());
        Assert.Equal("color_C8C8C8~flat", materials[0].Name);
        Assert.Equal("color_C8C8C8~flat~4000", materials[^1].Name);
        Assert.Equal(3999f, materials[^1].Extras![WorldGltf.Key]!["fields"]![0]!.GetValue<float>());
        var (json, binary) = exported.Write("geometry.bin", TestContext.Current.CancellationToken);
        Assert.NotEmpty(json);
        Assert.NotEmpty(binary);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void SuffixNamesOccupiedByAnotherBaseKeepTheOriginalOrder(bool occupiedFirst)
    {
        WorldTexture rock = new("rock"), suffix = new("rock~2");
        WorldMaterial first = new() { Texture = rock }, second = new() { Texture = rock, Field14 = 1 },
            third = new() { Texture = rock, Field14 = 2 }, other = new() { Texture = suffix };
        WorldMaterial[] source = occupiedFirst ? [other, first, second, third, first] : [first, second, other, third, first];
        var world = World(source, normals: true);
        var materials = WorldGltf.Export([world.Nodes[0]], 255, Context()).Roots[0].Mesh!.Primitives.Select(p => p.Material!).ToArray();

        Assert.Equal(occupiedFirst ? ["rock~2", "rock", "rock~3", "rock~4", "rock"]
            : ["rock", "rock~2", "rock~2~2", "rock~3", "rock"], materials.Select(m => m.Name));
        Assert.Same(materials[occupiedFirst ? 1 : 0], materials[^1]);
    }

    private static WorldGltf.ExportContext Context() => new() { Texture = t => (t.Name + ".png", 0), Token = Token };

    private static GameZWorld World(IReadOnlyList<WorldMaterial> materials, bool normals = false)
    {
        WorldModel model = new();
        model.Vertices.AddRange([new(0, 0, 0), new(0, 0, 1), new(1, 0, 1)]);
        if (normals) model.Normals.Add(Vector3.UnitY);
        foreach (var material in materials)
            model.Polygons.Add(new()
            {
                Vertices = [0, 1, 2], Material = material, Normals = normals ? [0, 0, 0] : [],
                Uvs = material.Texture == null ? [] : [new(0, 0), new(0, 1), new(1, 1)],
            });
        WorldNode node = new("part", WorldNodeClass.Object3D) { Model = model }; node.SetPayloadInt(0, 0x28);
        GameZWorld world = new(); world.Nodes.Add(node); world.Models.Add(model);
        world.Materials.AddRange(materials.Distinct());
        world.Textures.AddRange(materials.Select(m => m.Texture).OfType<WorldTexture>().Distinct());
        return world;
    }
}
