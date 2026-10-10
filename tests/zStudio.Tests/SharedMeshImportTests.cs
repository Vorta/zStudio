using System.Numerics;
using System.Text.Json.Nodes;
using Recoil.Zbd.Core.Formats;
using Recoil.Zbd.Core.Gltf;
using Recoil.Zbd.Core.Worlds;
using Xunit;

namespace Recoil.Zbd.Tests;

public sealed class SharedMeshImportTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Fact]
    public void RepeatedParsedMeshSharesOneModelPerReadingAndWritesSharedReferences()
    {
        var source = Document(2048, 512);
        var document = Read(source);
        Assert.All(document.Roots, n => Assert.Same(document.Roots[0].Mesh, n.Mesh));
        var context = Context();
        var first = WorldGltf.Import(document, "first.gltf", 0, context);
        var again = WorldGltf.Import(document, "first.gltf", 0, context);
        var otherReading = WorldGltf.Import(document, "second.gltf", 0, context);
        Assert.Equal(2, context.World.Models.Count);
        Assert.All(first.Concat(again), n => Assert.Same(first[0].Model, n.Model));
        Assert.All(otherReading, n => Assert.Same(otherReading[0].Model, n.Model));
        Assert.NotSame(first[0].Model, otherReading[0].Model);
        Assert.Equal(512, first[0].Model!.Polygons.Count);
        Assert.Single(context.World.Materials);

        var world = context.World;
        world.Nodes.AddRange(first.Concat(again).Concat(otherReading));
        world.NodeCapacity = world.Nodes.Count;
        world.ModelCapacity = world.Models.Count;
        world.MaterialCapacity = world.Materials.Count;
        var reopened = new FormatRegistry().OpenBytes("shared.zbd", GameZWriter.Write(world, Token), token: Token);
        Assert.Empty(reopened.Diagnostics);
        var roundtrip = GameZWorldReader.FromDocument(reopened, Token);
        Assert.Equal(2, roundtrip.Models.Count);
        Assert.All(roundtrip.Nodes.Take(first.Count + again.Count), n => Assert.Same(roundtrip.Models[0], n.Model));
        Assert.All(roundtrip.Nodes.Skip(first.Count + again.Count), n => Assert.Same(roundtrip.Models[1], n.Model));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void MalformedSharedMeshIsRejectedBeforeAnyWorldPoolChanges(bool multipleMorphTargets)
    {
        var source = Document(16, 8);
        var primitive = source.Roots[^1].Mesh!.Primitives[^1];
        if (multipleMorphTargets)
        {
            primitive.Targets.Add([Vector3.Zero, Vector3.Zero, Vector3.Zero]);
            primitive.Targets.Add([Vector3.Zero, Vector3.Zero, Vector3.Zero]);
        }
        else primitive.Extras = new() { [WorldGltf.Key] = new JsonArray() };
        // Even a valid earlier mesh must not be published before the later shared mesh fails preflight.
        source.Roots.Insert(0, Document(1, 1).Roots[0]);
        var document = Read(source);
        var context = Context();
        var error = Assert.Throws<InvalidDataException>(() => WorldGltf.Import(document, "malformed.gltf", 0, context));
        Assert.Contains(multipleMorphTargets ? "multiple morph targets" : "must be an object", error.Message);
        Assert.Empty(context.World.Models);
        Assert.Empty(context.World.Materials);
        Assert.Empty(context.World.Textures);
        Assert.Empty(context.World.Nodes);
    }

    private static GltfDocument Document(int nodes, int primitives)
    {
        GltfDocument document = new();
        GltfMesh mesh = new();
        GltfMaterial material = new();
        for (int i = 0; i < primitives; i++)
        {
            GltfPrimitive primitive = new() { Material = material };
            primitive.Positions.AddRange([Vector3.Zero, Vector3.UnitX, Vector3.UnitZ]);
            primitive.Indices.AddRange([0, 1, 2]);
            mesh.Primitives.Add(primitive);
        }
        for (int i = 0; i < nodes; i++) document.Roots.Add(new() { Name = $"part{i}", Mesh = mesh });
        return document;
    }

    private static GltfDocument Read(GltfDocument source)
    {
        var data = source.Write("shared.bin", Token);
        return GltfDocument.Read(data.Json, _ => data.Binary, Token);
    }

    private static WorldGltf.ImportContext Context() => new()
    {
        World = new(), Reference = (_, _) => throw new InvalidOperationException(),
        TextureName = (_, _, _) => throw new InvalidOperationException(), Token = Token,
    };
}
