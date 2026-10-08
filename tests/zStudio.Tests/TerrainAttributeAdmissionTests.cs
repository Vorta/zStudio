using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Recoil.Zbd.Core.Terrain;
using Recoil.Zbd.Core.Worlds;
using Xunit;

namespace Recoil.Zbd.Tests;

public sealed class TerrainAttributeAdmissionTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ColdZonesRefuseBeforeHydratingEvenBelowAWarmObject(bool warmParent)
    {
        _ = Assert.Throws<InvalidDataException>(() => TerrainAttributes.FromJson(JsonNode.Parse("{\"zones\":[]}"), "warm"));
        string source = "{\"zones\":[" + string.Join(',', Enumerable.Repeat("0", 8192)) + "]}";
        var node = JsonNode.Parse(source)!;
        if (warmParent) _ = node["zones"]; // Hydrate the one root property, never the child array.
        long before = GC.GetAllocatedBytesForCurrentThread();
        var error = Assert.Throws<InvalidDataException>(() => TerrainAttributes.FromJson(node, "test"));
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        Assert.Contains("zones", error.Message);
        Assert.InRange(allocated, 0, 32 * 1024);
        Assert.Equal(source, node.ToJsonString(new() { WriteIndented = false }));
    }

    [Theory]
    [InlineData("zones")]
    [InlineData("nodeZone")]
    [InlineData("craters")]
    [InlineData("soil")]
    [InlineData("flags")]
    public void ColdEscapedStringsRefuseBeforeDecoding(string key)
    {
        _ = Assert.Throws<InvalidDataException>(() => TerrainAttributes.FromJson(JsonNode.Parse("{\"zones\":[]}"), "warm"));
        string json = "{\"" + key + "\":\"" + string.Concat(Enumerable.Repeat("\\u0030", 32768)) + "\"}";
        using var document = JsonDocument.Parse(json);
        long before = GC.GetAllocatedBytesForCurrentThread();
        Assert.Throws<InvalidDataException>(() => TerrainAttributes.FromJson(document.RootElement, "test"));
        Assert.InRange(GC.GetAllocatedBytesForCurrentThread() - before, 0, 32 * 1024);
    }

    [Fact]
    public void UnknownPropertyAndRepeatedKnownPropertiesDoNotHydrateTheObject()
    {
        _ = Assert.Throws<InvalidDataException>(() => TerrainAttributes.FromJson(JsonNode.Parse("{\"unknown\":0}"), "warm"));
        var unknown = JsonNode.Parse("{\"" + new string('k', 32768) + "\":0}");
        long before = GC.GetAllocatedBytesForCurrentThread();
        Assert.Contains("unknown attribute", Assert.Throws<InvalidDataException>(() => TerrainAttributes.FromJson(unknown, "test")).Message);
        Assert.InRange(GC.GetAllocatedBytesForCurrentThread() - before, 0, 32 * 1024);
        var duplicates = JsonNode.Parse("{" + string.Join(',', Enumerable.Repeat("\"zones\":null", 1024)) + "}");
        Assert.Contains("zones twice", Assert.Throws<InvalidDataException>(() => TerrainAttributes.FromJson(duplicates, "test")).Message);
        var escapedDuplicate = JsonNode.Parse("{\"zones\":null,\"zo\\u006ees\":null}");
        Assert.Contains("zones twice", Assert.Throws<InvalidDataException>(() => TerrainAttributes.FromJson(escapedDuplicate, "test")).Message);
    }

    [Theory]
    [InlineData("[]")]
    [InlineData("[0,1,2,3]")]
    [InlineData("[null]")]
    [InlineData("[{}]")]
    [InlineData("[[0]]")]
    [InlineData("[255]")]
    [InlineData("[-1]")]
    [InlineData("[1.0]")]
    public void ZoneCardinalityAndScalarSemanticsRemainStrict(string zones)
    {
        Assert.Throws<InvalidDataException>(() => TerrainAttributes.FromJson(JsonNode.Parse("{\"zones\":" + zones + "}"), "test"));
        Assert.Equal(new byte[] { 0, 2, 254 }, TerrainAttributes.FromJson(JsonNode.Parse("{\"zones\":[254,2,0]}"), "test").Zones!.Ids);
        Assert.Equal(new byte[] { 2 }, TerrainAttributes.FromJson(JsonNode.Parse("{\"zones\":[2,2,2]}"), "test").Zones!.Ids);
    }

    [Theory]
    [InlineData("defaults")]
    [InlineData("surface")]
    [InlineData("region")]
    public void EveryRecipeAttributeLocationUsesAdmissionAndCanRetry(string location)
    {
        byte[] Recipe(string attributes)
        {
            string surface = "{\"id\":\"land\",\"model\":\"land.gltf\",\"node\":\"ground\"" + (location == "surface" ? ",\"defaults\":" + attributes : "") + "}";
            string rest = location == "defaults" ? ",\"defaults\":" + attributes : location == "region" ? ",\"regions\":[{\"name\":\"paint\",\"set\":" + attributes + "}]" : "";
            return Encoding.UTF8.GetBytes("{\"format\":\"recoil-terrain\",\"version\":1,\"compiler\":1,\"surfaces\":[" + surface + "]" + rest + "}");
        }
        Assert.Contains("zones", Assert.Throws<InvalidDataException>(() => TerrainRecipe.Parse(Recipe("{\"zones\":[0,1,2,3]}"), "test.terrain.json")).Message);
        var recipe = TerrainRecipe.Parse(Recipe("{\"zones\":[7],\"collision\":false}"), "test.terrain.json");
        var actual = location == "defaults" ? recipe.Defaults : location == "surface" ? recipe.Surfaces[0].Defaults : recipe.Regions[0].Set;
        Assert.Equal(new TerrainAttributes { Zones = new([7]), Collision = false }, actual);
        Assert.Equal(recipe.Write(), TerrainRecipe.Parse(recipe.Write(), "roundtrip").Write());
    }

    [Fact]
    public void EscapedWordsFlagsAndNullPatchesKeepTheirMeaning()
    {
        var baseline = new TerrainAttributes { Zones = new([4]), Soil = 5, Collision = true, Flags = 0x10 };
        var expected = baseline with { Zones = null, NodeZone = TerrainAttributes.AutoZone, NodeGate = false, Standable = true,
            Craters = TerrainCraters.Ignored, Soil = 3, Priority = 255, Flags = WorldGltf.CarriedFlags };
        string source = "{\"zones\":null,\"nodeZone\":\"\\u0061uto\",\"nodeGate\":false,\"standable\":true,\"craters\":\"ignored\",\"soil\":\"quicksand\",\"priority\":255,\"flags\":\"0X" + WorldGltf.CarriedFlags.ToString("X8") + "\"}";
        var cold = JsonNode.Parse(source)!;
        Assert.Equal(expected, TerrainAttributes.FromJson(cold, "patch", baseline));
        Assert.Equal(expected, TerrainAttributes.FromJson(cold, "patch", baseline)); // Already materialized path.
        Assert.Same(baseline, TerrainAttributes.FromJson((JsonNode?)null, "patch", baseline));
        Assert.Equal(TerrainAttributes.None, TerrainAttributes.FromJson((JsonElement?)null, "missing"));
        Assert.Equal(TerrainZones.Any, TerrainAttributes.FromJson(JsonNode.Parse("{\"zones\":\"\\u0061ny\"}"), "test").Zones);
        string exact = "0x" + new string('0', 251) + "8 \t";
        Assert.Equal(256, exact.Length);
        Assert.Equal(8u, TerrainAttributes.FromJson(new JsonObject { ["flags"] = exact }, "flags").Flags);
        Assert.Equal(8u, TerrainAttributes.FromJson(JsonNode.Parse(JsonSerializer.Serialize(new { flags = exact })), "flags").Flags);
        Assert.Throws<InvalidDataException>(() => TerrainAttributes.FromJson(new JsonObject { ["flags"] = exact + " " }, "flags"));
        foreach (string invalid in new[] { "0x1", "0xFFFFFFFF", "0x100000000", "0x", "10", "0xZZ" })
            Assert.Throws<InvalidDataException>(() => TerrainAttributes.FromJson(new JsonObject { ["flags"] = invalid }, "flags"));
        var flagsOnly = new TerrainAttributes { Flags = WorldGltf.CarriedFlags };
        Assert.Equal(flagsOnly, TerrainAttributes.FromJson(flagsOnly.ToJson(), "flags"));
    }

    [Fact]
    public void MissingRuntimeAccessorFailsClosedWithoutTouchingColdChildren()
    {
        var cold = JsonNode.Parse("{\"zones\":[1,2,3]}");
        Assert.Contains("cannot safely inspect", Assert.Throws<InvalidDataException>(() => TerrainAttributes.FromJson(cold, "test", null, null)).Message);
        Assert.Throws<InvalidDataException>(() => TerrainAttributes.FromJson(cold, "test", null, _ => throw new NotSupportedException()));
        Assert.Equal(new byte[] { 1, 2, 3 }, TerrainAttributes.FromJson(cold, "retry").Zones!.Ids);
    }
}
