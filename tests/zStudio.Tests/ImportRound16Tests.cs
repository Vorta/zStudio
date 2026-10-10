using System.Text;
using System.Buffers.Binary;
using Recoil.Zbd.Core.Formats;
using System.Text.Json.Nodes;
using Recoil.Zbd.Core.Gltf;
using Recoil.Zbd.Core.Sources;
using Recoil.Zbd.Core.Worlds;
using Xunit;

namespace Recoil.Zbd.Tests;

public sealed class ImportRound16Tests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;
    private static GameZWorld World(params WorldTexture[] textures)
    {
        GameZWorld world = new(); world.Nodes.Add(new("world", WorldNodeClass.World)); world.Textures.AddRange(textures); return world;
    }

    [Theory]
    [InlineData(-2)][InlineData(1)]
    public void InvalidStoredTextureLinksCannotDisappearDuringComparison(int link)
    {
        byte[] bytes = GameZWriter.Write(World(new WorldTexture("rock")), Token);
        int start = BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(12));
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(start + 32), link);
        var doc = FormatRegistry.Default.OpenBytes("test.zbd", bytes, token: Token);
        Assert.Contains("variant", Assert.Throws<InvalidDataException>(() => GameZWorldReader.FromDocument(doc, Token)).Message, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("0.0")][InlineData("1.0")][InlineData("2.0")]
    public void SupportedAssetsMayDeclareALowerCanonicalMinimum(string minimum)
    {
        var bytes = Encoding.UTF8.GetBytes("{\"asset\":{\"version\":\"2.0\",\"minVersion\":\"" + minimum + "\"}}");
        Assert.NotNull(GltfDocument.Read(bytes, _ => [], Token));
    }

    [Fact]
    public void ValidStoredTextureCyclesRoundTripAndCompareWithoutTraversal()
    {
        WorldTexture a = new("a"), b = new("b"); a.NextVariant = b; b.NextVariant = a;
        var world = World(a, b);
        var loaded = GameZWorldReader.FromDocument(FormatRegistry.Default.OpenBytes("cycle.zbd", GameZWriter.Write(world, Token), token: Token), Token);
        Assert.Same(loaded.Textures[1], loaded.Textures[0].NextVariant);
        Assert.Same(loaded.Textures[0], loaded.Textures[1].NextVariant);
        Assert.Equal(0, WorldComparer.CompareTree(world, loaded, token: Token).DifferenceCount);
    }

    [Fact]
    public void ReconstructionHashWorkIsBoundedBeforeOpeningInputs()
    {
        SourceExtractor.Input[] files = [new("absent-one", "one.zbd", 9, DateTime.MinValue), new("absent-two", "two.zbd", 9, DateTime.MinValue)];
        Assert.Contains("consistency-check limit", Assert.Throws<IOException>(() => SourceExtractor.RequireCheckedInputCapacity(files, Token, 17)).Message);
        SourceExtractor.RequireCheckedInputCapacity(files, Token, 18);
    }

    [Fact]
    public void TextureDirectoryComparisonKeepsMultiplicityAndVariantTargets()
    {
        WorldTexture a = new("rock"), b = new("rock_low"); a.NextVariant = b;
        WorldTexture copyA = new("ROCK"), copyB = new("ROCK_LOW"); copyA.NextVariant = copyB;
        var expected = World(a, b); var actual = World(copyB, copyA);
        Assert.Equal(0, WorldComparer.CompareTree(expected, actual, token: Token).DifferenceCount);
        copyA.NextVariant = null;
        Assert.Contains(WorldComparer.CompareTree(expected, actual, token: Token).Differences, d => d.Field == "nextVariant");
        copyA.NextVariant = copyB; actual.Textures.Add(new("rock"));
        Assert.NotEqual(0, WorldComparer.CompareTree(expected, actual, token: Token).DifferenceCount);
    }

    [Fact]
    public void DuplicateTextureTargetsAreNotResolvedByNameAlone()
    {
        WorldTexture a = new("rock"), b = new("rock"), c = new("rock"), d = new("rock");
        a.NextVariant = b; c.NextVariant = c;
        Assert.NotEqual(0, WorldComparer.CompareTree(World(a, b), World(c, d), token: Token).DifferenceCount);
    }

    [Theory]
    [InlineData("20")][InlineData("2x")][InlineData("2")][InlineData("2.1")][InlineData("02.0")][InlineData("2.00")]
    public void UnsupportedOrMalformedAssetVersionsAreRefused(string version)
    {
        byte[] bytes = Encoding.UTF8.GetBytes("{\"asset\":{\"version\":\"" + version + "\"}}");
        Assert.Contains("version", Assert.Throws<InvalidDataException>(() => GltfDocument.Read(bytes, _ => [], Token)).Message, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("null")][InlineData("[]")][InlineData("3")][InlineData("{}")][InlineData("{\"version\":2}")][InlineData("{\"version\":\"2.0\",\"minVersion\":\"02.0\"}")][InlineData("{\"version\":\"2.0\",\"minVersion\":\"2.00\"}")]
    [InlineData("{\"version\":\"2.0\",\"minVersion\":\"2x\"}")][InlineData("{\"version\":\"2.0\",\"minVersion\":\"2.1\"}")]
    public void InvalidVersionTypesAndMinimumsHaveInputDiagnostics(string asset)
    {
        var bytes = Encoding.UTF8.GetBytes("{\"asset\":" + asset + "}");
        Assert.Throws<InvalidDataException>(() => GltfDocument.Read(bytes, _ => [], Token));
    }

    [Fact]
    public void DirectoryDetailsRemainBoundedWhileEveryEntryDifferenceIsCounted()
    {
        var left = Enumerable.Range(0, 1000).Select(i => new WorldTexture("texture" + i)).ToArray();
        var right = Enumerable.Range(0, 1000).Select(i => new WorldTexture("texture" + i) { State = 1 }).ToArray();
        var comparison = WorldComparer.CompareTree(World(left), World(right), limit: 0, token: Token);
        Assert.Equal(1000, comparison.DifferenceCount); Assert.Empty(comparison.Differences);
        var row = Assert.Single(comparison.Roots); Assert.Equal(1000, row.DifferenceCount);
        Assert.Equal(WorldComparer.MaximumNodeDifferences, row.Differences.Count);
        Assert.Equal(WorldComparisonStatus.Changed, row.Status);
    }

    [Theory]
    [InlineData("flags")][InlineData("packedColor")]
    public void MaterialWordsCannotSilentlyLoseHighBits(string field)
    {
        var material = new GltfMaterial { MetallicFactor = 0, Extras = new JsonObject { [WorldGltf.Key] = new JsonObject { [field] = "0x10000" } } };
        GltfPrimitive primitive = new() { Material = material };
        primitive.Positions.AddRange([new(0, 0, 0), new(1, 0, 0), new(0, 0, 1)]); primitive.Indices.AddRange([0, 1, 2]);
        GltfMesh mesh = new(); mesh.Primitives.Add(primitive);
        GltfDocument document = new(); document.Roots.Add(new() { Mesh = mesh });
        GameZWorld world = new();
        var context = new WorldGltf.ImportContext { World = world, Reference = (_, _) => throw new InvalidOperationException(), TextureName = (u, n, _) => n ?? u };
        Assert.Contains(field, Assert.Throws<InvalidDataException>(() => WorldGltf.Import(document, "test.gltf", 255, context)).Message);
        Assert.Empty(world.Models); Assert.Empty(world.Materials);
        // A corrected edit immediately succeeds on the same context and retains the full stored width.
        material.Extras![WorldGltf.Key]![field] = "0xFFFF";
        var result = WorldGltf.Import(document, "test.gltf", 255, context).Single();
        var imported = Assert.Single(result.Model!.Polygons).Material!;
        if (field == "flags") Assert.Equal(0xFE00, imported.Flags & 0xFE00);
        else Assert.Equal(ushort.MaxValue, imported.PackedColor);
    }
}
