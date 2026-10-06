using System.IO;
using System.Text;
using System.Text.Json;
using Recoil.Zbd.Core.Sources;
using Recoil.Zbd.Core.Terrain;
using Xunit;

namespace Recoil.Zbd.Tests;

/// <summary>
/// Terrain recipes are measured before anything is built from their JSON (unknown keys, every token, a key given twice),
/// and terrain conversion keeps a bounded number of the names its sources mention.
/// </summary>
public sealed class TerrainRound6Tests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;
    private const string Head = "\"format\":\"recoil-terrain\",\"version\":1,\"compiler\":1,\"surfaces\":[{\"id\":\"s\",\"model\":\"s.gltf\",\"node\":\"n\"}]";

    /// <summary>Parses a recipe, returning what the parse allocated on this thread (the input is already allocated).</summary>
    private static (TerrainRecipe? Recipe, Exception? Refusal, long Allocated) Measured(byte[] json)
    {
        long before = GC.GetAllocatedBytesForCurrentThread();
        try { var recipe = TerrainRecipe.Parse(json, "big.terrain.json"); return (recipe, null, GC.GetAllocatedBytesForCurrentThread() - before); }
        catch (Exception ex) { return (null, ex, GC.GetAllocatedBytesForCurrentThread() - before); }
    }
    /// <summary>A JSON list of <paramref name="count"/> zeros, written straight into bytes.</summary>
    private static byte[] Recipe(string before, int count, string after)
    {
        byte[] head = Encoding.UTF8.GetBytes(before), tail = Encoding.UTF8.GetBytes(after);
        byte[] json = new byte[head.Length + 1 + 2 * count + tail.Length];
        head.CopyTo(json, 0);
        int at = head.Length; json[at++] = (byte)'[';
        for (int i = 0; i < count; i++) { json[at++] = (byte)'0'; json[at++] = (byte)','; }
        json[at - 1] = (byte)']';
        tail.CopyTo(json, at);
        return json;
    }

    [Fact]
    public void ContentUnderUnknownKeysIsRefusedBeforeTheDocumentIsBuilt()
    {
        // A usable recipe with 200,000 values nothing reads: refused while measuring, before the input is copied.
        var (recipe, refusal, allocated) = Measured(Recipe("{" + Head + ",\"notes\":", 200_000, "}"));
        Assert.Null(recipe);
        var refused = Assert.IsType<InvalidDataException>(refusal);
        Assert.Contains("big.terrain.json holds more than 65,536 JSON tokens under keys a terrain recipe does not have", refused.Message);
        Assert.True(allocated < 64 * 1024, $"Measuring allocated {allocated:N0} bytes.");
        // The same deep inside a region's polygon, and as many keys of their own.
        string polygon = "{\"name\":\"r\",\"shape\":{\"polygons\":[{\"outer\":[[0,0],[1,0],[0,1]],\"extra\":";
        Assert.Contains("under keys a terrain recipe does not have", Assert.Throws<InvalidDataException>(() => TerrainRecipe.Parse(Recipe("{" + Head + ",\"regions\":[" + polygon, 70_000, "}]}}]}"), "x")).Message);
        string keys = string.Join(",", Enumerable.Range(0, 40_000).Select(i => $"\"k{i}\":0"));
        Assert.Contains("under keys a terrain recipe does not have", Assert.Throws<InvalidDataException>(() => TerrainRecipe.Parse(Encoding.UTF8.GetBytes("{" + Head + "," + keys + "}"), "x")).Message);

        // A few notes are still read past, wherever they are; the recipe is the one without them.
        string plain = "{" + Head + ",\"regions\":[{\"name\":\"r\",\"surfaces\":[\"s\"],\"shape\":{\"plane\":\"xz\",\"polygons\":[{\"outer\":[[0,0],[1,0],[0,1]]}]},\"set\":{\"soil\":\"water\"}}]}";
        string noted = plain.Replace("\"version\":1", "\"comment\":\"made by hand\",\"version\":1").Replace("\"name\":\"r\"", "\"name\":\"r\",\"note\":{\"by\":[1,2,3]}")
            .Replace("\"outer\"", "\"tag\":null,\"outer\"");
        Assert.NotEqual(plain, noted);
        Assert.Equal(TerrainRecipe.Parse(Encoding.UTF8.GetBytes(plain), "x").Write(), TerrainRecipe.Parse(Encoding.UTF8.GetBytes(noted), "x").Write());
        // A known key whose value has the wrong kind is refused as before, not counted as unknown content.
        Assert.Equal("x needs format as text.", Assert.Throws<InvalidDataException>(() => TerrainRecipe.Parse(Recipe("{\"format\":", 100_000, "," + Head[(Head.IndexOf(",\"version\"", StringComparison.Ordinal) + 1)..] + "}"), "x")).Message);
    }

    [Fact]
    public void EveryTokenCountsBeforeTheDocumentIsBuilt()
    {
        // Ten million values under a known key: refused while measuring, with no copy or document of them.
        var (recipe, refusal, allocated) = Measured(Recipe("{\"format\":", TerrainRecipe.MaximumTokens, "," + Head[(Head.IndexOf(",\"version\"", StringComparison.Ordinal) + 1)..] + "}"));
        Assert.Null(recipe);
        Assert.Equal("big.terrain.json holds more than 10,000,000 JSON tokens, more than a recipe at every limit holds.", Assert.IsType<InvalidDataException>(refusal).Message);
        Assert.True(allocated < 64 * 1024, $"Measuring allocated {allocated:N0} bytes.");
        // A list over its limit is refused from its length, before any entry is read.
        string ring = "{" + Head + ",\"regions\":[{\"name\":\"r\",\"shape\":{\"polygons\":[{\"outer\":";
        string points = string.Join(",", Enumerable.Repeat("\"p\"", TerrainRecipe.MaximumRingPoints + 1));
        Assert.Equal($"x region r has a ring without 3–{TerrainRecipe.MaximumRingPoints} points.", Assert.Throws<InvalidDataException>(() => TerrainRecipe.Parse(Encoding.UTF8.GetBytes(ring + "[" + points + "]}]}}]}"), "x")).Message);
    }

    [Fact]
    public void ARecipeAtEveryLimitIsStillRead()
    {
        // 256 surfaces with every attribute, 4,096 regions each listing all of them with every attribute, and 995,328 shape
        // points in 3-point polygons (the most polygons the point limit allows): the most tokens a recipe can hold.
        const string attributes = "{\"zones\":[1,2,3],\"nodeZone\":\"auto\",\"nodeGate\":true,\"collision\":true,\"standable\":false,\"craters\":\"blocked\",\"soil\":\"water\",\"priority\":2,\"flags\":\"0x00000018\"}";
        using MemoryStream json = new();
        void Append(string text) => json.Write(Encoding.UTF8.GetBytes(text));
        Append("{\"format\":\"recoil-terrain\",\"version\":1,\"compiler\":1,\"surfaces\":[");
        for (int s = 0; s < TerrainRecipe.MaximumSurfaces; s++) Append((s == 0 ? "" : ",") + $"{{\"id\":\"s{s}\",\"model\":\"s.gltf\",\"node\":\"n{s}\",\"defaults\":{attributes}}}");
        Append($"],\"defaults\":{attributes},\"regions\":[");
        string surfaces = string.Join(",", Enumerable.Range(0, TerrainRecipe.MaximumSurfaces).Select(s => $"\"s{s}\""));
        int polygons = TerrainRecipe.MaximumPoints / 3 / TerrainRecipe.MaximumRegions;
        string shape = string.Join(",", Enumerable.Repeat("{\"outer\":[[0,0],[1,0],[0,1]],\"holes\":[]}", polygons));
        for (int r = 0; r < TerrainRecipe.MaximumRegions; r++)
            Append((r == 0 ? "" : ",") + $"{{\"name\":\"r{r}\",\"surfaces\":[{surfaces}],\"shape\":{{\"plane\":\"xz\",\"minY\":-1,\"maxY\":1,\"polygons\":[{shape}]}},\"set\":{attributes}}}");
        Append("]}");
        byte[] bytes = json.ToArray();
        int tokens = 0;
        for (Utf8JsonReader reader = new(bytes); reader.Read();) tokens++;
        Assert.InRange(tokens, 7_800_000, TerrainRecipe.MaximumTokens);
        var recipe = TerrainRecipe.Parse(bytes, "full.terrain.json");
        Assert.Equal(TerrainRecipe.MaximumRegions, recipe.Regions.Count);
        Assert.Equal(polygons * 3 * TerrainRecipe.MaximumRegions, recipe.Regions.Sum(r => TerrainShapes.PointCount(r.Shape!.Polygons)));
        Assert.Equal(TerrainRecipe.MaximumSurfaces, recipe.Regions[^1].Surfaces.Count);
        Assert.Equal(0x03020103u, recipe.Regions[0].Set.Zones!.Word);
    }

    [Fact]
    public void AKeyGivenTwiceIsRefusedAsARecipeProblem()
    {
        // Each of these used to escape as an ArgumentException from the JSON nodes, not as a problem with the recipe.
        string plain = "{" + Head + ",\"regions\":[{\"name\":\"r\",\"set\":{\"soil\":\"water\"}}]}";
        Assert.Equal("x gives format twice in one object.", Assert.Throws<InvalidDataException>(() => TerrainRecipe.Parse(Encoding.UTF8.GetBytes(plain.Replace("{\"format\"", "{\"format\":\"recoil-terrain\",\"form\\u0061t\"")), "x")).Message);
        Assert.Equal("x gives soil twice in one object.", Assert.Throws<InvalidDataException>(() => TerrainRecipe.Parse(Encoding.UTF8.GetBytes(plain.Replace("{\"soil\"", "{\"soil\":1,\"soil\"")), "x")).Message);
        Assert.StartsWith("x region r set has an unknown attribute colour (known: zones,",Assert.Throws<InvalidDataException>(() => TerrainRecipe.Parse(Encoding.UTF8.GetBytes(plain.Replace("{\"soil\"", "{\"colour\":1,\"colour\":2,\"soil\"")), "x")).Message);
        // Notes given twice are still read past.
        Assert.Single(TerrainRecipe.Parse(Encoding.UTF8.GetBytes(plain.Replace("\"version\"", "\"note\":1,\"note\":2,\"version\"")), "x").Regions);
    }

    [Fact]
    public void ConversionRefusesSourcesNamingTooManyWordsBeforeKeepingThem()
    {
        using SourceWorldFixture fixture = new();
        SourceWorkspace workspace = new(fixture.Project);
        // Short distinct words, as a valid script may hold: refused at the limit, naming the file.
        fixture.Write("gamegen/many.gs", string.Join("\r\n", Enumerable.Range(0, SourceTerrainConversion.MaximumNames + 1).Select(i => $"# w{i}")));
        var refused = Assert.Throws<InvalidDataException>(() => SourceTerrainConversion.References(workspace, ["gamegen/m1.gs", "gamegen/many.gs"], Token));
        Assert.Contains($"more than {SourceTerrainConversion.MaximumNames:N0} different words", refused.Message);
        Assert.Contains("gamegen/many.gs", refused.Message);
        // Long ones: their characters count.
        fixture.Write("gamegen/long.gs", string.Join("\r\n", Enumerable.Range(0, 5_000).Select(i => "# " + new string('x', 1_000) + i)));
        Assert.Contains("gamegen/long.gs", Assert.Throws<InvalidDataException>(() => SourceTerrainConversion.References(workspace, ["gamegen/long.gs"], Token)).Message);
        // Wildcard patterns: their characters count too, as each becomes an expression.
        fixture.Write("gamegen/stars.gs", string.Join("\r\n", Enumerable.Range(0, 100).Select(i => $"# p{i}" + new string('*', 3_000))));
        Assert.Contains("wildcard patterns hold more than", Assert.Throws<InvalidDataException>(() => SourceTerrainConversion.References(workspace, ["gamegen/stars.gs"], Token)).Message);

        // A word repeated is one name, however often; the names and patterns are those the sources mention.
        fixture.Write("gamegen/same.gs", string.Concat(Enumerable.Repeat("ground tank_* ", 400_000)) + "\r\n");
        var (names, patterns) = SourceTerrainConversion.References(workspace, ["gamegen/same.gs"], Token);
        Assert.Equal(["ground"], names.Order(StringComparer.Ordinal));
        Assert.Equal(["^tank_[0-9]$"], patterns.Select(p => p.ToString()));
    }
}
