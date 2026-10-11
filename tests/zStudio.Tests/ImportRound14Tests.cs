using System.Numerics;
using Recoil.Zbd.Core.Formats;
using Recoil.Zbd.Core.Sources;
using Recoil.Zbd.Core.Terrain;
using Xunit;

namespace Recoil.Zbd.Tests;

public sealed class ImportRound14Tests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;
    [Fact]
    public void RecipeWritingDoesNotAllocateAJsonNodeForEveryCoordinate()
    {
        var points = Enumerable.Range(0, 100_000).Select(i => new Vector2(i, -i)).ToArray();
        var recipe = new TerrainRecipe(1, [new("land", "land.gltf", "land", TerrainAttributes.None)], TerrainAttributes.None,
            [new("region", [], new([new(points, [])]), TerrainAttributes.None)]);
        _ = (recipe with { Regions = [] }).Write(); // Warm serializer metadata outside measurement.
        long before = GC.GetAllocatedBytesForCurrentThread();
        byte[] bytes = recipe.Write();
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        Assert.InRange(allocated, 0, 32 * 1024 * 1024);
        Assert.Equal(points, TerrainRecipe.Parse(bytes, "fixture").Regions[0].Shape!.Polygons[0].Outer);
    }

    [Fact]
    public void RecipeWriterBoundsEncodedBytesAndRejectsOversizedFieldsBeforeEncoding()
    {
        var points = Enumerable.Repeat(new Vector2(-999999.94f, 999999.94f), TerrainRecipe.MaximumRingPoints).ToArray();
        var recipe = new TerrainRecipe(1, [new("land", "land.gltf", "land", TerrainAttributes.None)], TerrainAttributes.None,
            [new("region", [], new(Enumerable.Repeat(new TerrainOutline(points, []), 5).ToArray()), TerrainAttributes.None)]);
        Assert.Contains("64 MB", Assert.Throws<InvalidDataException>(() => recipe.Write()).Message);
        var oversized = recipe with { Regions = [], Surfaces = [new("land", new string('\u0001', 1_000_000), "land", TerrainAttributes.None)] };
        long before = GC.GetAllocatedBytesForCurrentThread();
        Assert.Throws<InvalidDataException>(() => oversized.Write());
        Assert.InRange(GC.GetAllocatedBytesForCurrentThread() - before, 0, 100_000);
    }

    [Fact]
    public void LargeValidRegionsRemainInspectableWithoutRelaxingEditLimits()
    {
        var ring = Enumerable.Range(0, 150_000).Select(i => new Vector2(i, 0)).ToArray();
        var recipe = new TerrainRecipe(1, [new("land", "land.gltf", "land", TerrainAttributes.None)], TerrainAttributes.None,
            [new("large", [], new([new(ring, []), new(ring, [])]), TerrainAttributes.None)]);
        var shape = TerrainRecipe.Parse(recipe.Write(), "fixture").Regions[0].Shape!;
        long before = GC.GetAllocatedBytesForCurrentThread();
        var area = TerrainShapes.InspectArea(shape.Polygons);
        Assert.Null(area.SquareUnits); Assert.Contains("complexity", area.Unavailable);
        Assert.InRange(GC.GetAllocatedBytesForCurrentThread() - before, 0, 100_000);
        Assert.Throws<InvalidDataException>(() => TerrainShapes.Normalize(shape.Polygons));
        Assert.Equal(1, TerrainShapes.InspectArea([new([new(0, 0), new(1, 0), new(1, 1), new(0, 1)], [])]).SquareUnits);
        // Even small dense crossings must not make ordinary inspection fail.
        var crossing = Enumerable.Range(0, 3001).Select(i =>
        {
            double angle = (i * 1500 % 3001) * Math.Tau / 3001;
            return new Vector2((float)Math.Cos(angle) * 1000, (float)Math.Sin(angle) * 1000);
        }).ToArray();
        Assert.NotNull(TerrainShapes.InspectArea([new(crossing, [])]).Unavailable);
    }

    [Fact]
    public async Task DefinitionPreflightReservesDecodedTreeBeforeTryingMalformedBytes()
    {
        using SourceFixture fixture = new();
        File.WriteAllBytes(Path.Combine(fixture.Corpus, "zrdr.zbd"), SourceFixture.Archive(("anim.zrd", new byte[100_000], new byte[64])));
        var error = await Assert.ThrowsAsync<IOException>(() => SourceExtractor.ExtractAsync(fixture.Corpus, fixture.Project, 1_000_000, token: Token));
        Assert.Contains("remaining budget", error.Message);
        Assert.False(Directory.Exists(fixture.Project));
    }

    [Fact]
    public void StreamedRecipeKeepsCanonicalOrderingEscapesNumbersAndNewline()
    {
        const string source = """
            {"format":"recoil-terrain","version":1,"compiler":1,"surfaces":[{"id":"land","model":"café.gltf","node":"land","defaults":{"zones":[1,2],"flags":"0x00000008"}}],"defaults":{"soil":"water"},"regions":[{"name":"line\nquoted\"","surfaces":["land"],"shape":{"plane":"xz","minY":-1.25,"maxY":2.5,"polygons":[{"outer":[[0,0],[2,0],[0,2]],"holes":[[[0,0],[1,0],[0,1]]]}]},"set":{"priority":3}}]}
            """;
        var options = new System.Text.Json.JsonSerializerOptions { WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping };
        byte[] expected = System.Text.Encoding.UTF8.GetBytes(System.Text.Json.Nodes.JsonNode.Parse(source)!.ToJsonString(options).Replace("\r\n", "\n") + "\n");
        Assert.Equal(expected, TerrainRecipe.Parse(System.Text.Encoding.UTF8.GetBytes(source), "fixture").Write());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DefinitionsInAnotherFolderOrADuplicateDoNotQualify(bool duplicate)
    {
        using SourceFixture fixture = new();
        byte[] definitions = ArchiveSources.Read(File.ReadAllBytes(Path.Combine(fixture.Corpus, "zrdr.zbd"))).Single(m => m.Name == "anim.zrd").Payload.ToArray();
        if (duplicate)
            File.WriteAllBytes(Path.Combine(fixture.Corpus, "zrdr.zbd"), SourceFixture.Archive(("anim.zrd", definitions, new byte[64]), ("ANIM.ZRD", definitions, new byte[64])));
        else
            File.WriteAllBytes(Path.Combine(fixture.Corpus, "m1", "anim.zbd"), [1, 2, 3]);
        var error = await Assert.ThrowsAsync<InvalidDataException>(() => SourceExtractor.ExtractAsync(fixture.Corpus, fixture.Project, token: Token));
        Assert.Equal(SourceExtractor.NotOriginal, error.Message);
        Assert.False(Directory.Exists(fixture.Project) && Directory.EnumerateFiles(fixture.Project, "*", SearchOption.AllDirectories).Any());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ReconstructionRequiresDefinitionsInsideTheNamedMember(bool malformed)
    {
        using SourceFixture fixture = new();
        byte[] payload = malformed ? [1, 2, 3] : ZrdWriter.Write(ZrdText.Parse("( VALUE ( 1 ) )", Token), Token);
        File.WriteAllBytes(Path.Combine(fixture.Corpus, "zrdr.zbd"), SourceFixture.Archive(("anim.zrd", payload, new byte[64])));
        var error = await Assert.ThrowsAsync<InvalidDataException>(() => SourceExtractor.ExtractAsync(fixture.Corpus, fixture.Project, token: Token));
        Assert.Equal(SourceExtractor.NotOriginal, error.Message);
        Assert.False(Directory.Exists(fixture.Project) && Directory.EnumerateFiles(fixture.Project, "*", SearchOption.AllDirectories).Any());
    }
}
