using System.Buffers.Binary;
using System.Numerics;
using System.Text.Json.Nodes;
using Recoil.Zbd.Core.Gltf;
using Recoil.Zbd.Core.Worlds;
using Xunit;
namespace Recoil.Zbd.Tests;

public sealed class ImportRound9Tests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;
    private static GltfDocument Triangle()
    {
        GltfPrimitive p = new(); p.Positions.AddRange([Vector3.Zero, Vector3.UnitX, Vector3.UnitY]);
        p.Normals.AddRange([Vector3.UnitZ, Vector3.UnitZ, Vector3.UnitZ]);
        p.TexCoords.AddRange([Vector2.Zero, Vector2.UnitX, Vector2.UnitY]); p.Indices.AddRange([0, 1, 2]);
        p.Targets.Add([Vector3.Zero, Vector3.Zero, Vector3.Zero]);
        GltfMesh m = new(); m.Primitives.Add(p); GltfDocument d = new(); d.Roots.Add(new() { Mesh = m }); return d;
    }
    private static GltfDocument Read(JsonNode root, byte[] bin) => GltfDocument.Read(System.Text.Encoding.UTF8.GetBytes(root.ToJsonString()), _ => bin, Token);
    private static List<WorldNode> Import(GltfDocument d) => WorldGltf.Import(d, "data/model.gltf", 255,
        new() { World = new(), Reference = (_, _) => throw new InvalidOperationException(), TextureName = (_, _, _) => "rock" });
    [Fact]
    public void NonfiniteGeneratedGeometryCannotBecomeASuccessfulPartialModel()
    {
        ModelBuilder builder = new();
        var polygon = new PolygonInput([Vector3.Zero, new(float.PositiveInfinity, 0, 0), Vector3.UnitY], [], [], [], new());
        Assert.Throws<InvalidDataException>(() => builder.Add(polygon));
        Assert.Empty(builder.Model.Vertices); Assert.Empty(builder.Model.Polygons);
        Assert.True(builder.Add(polygon with { Points = [Vector3.Zero, Vector3.UnitX, Vector3.UnitY] }));
        Assert.Single(builder.Finish().Polygons);
    }
    [Fact]
    public void WriterRejectsNonfiniteModelBoundsBeforeWriting()
    {
        GameZWorld world = new(); WorldModel model = new() { BoundsRadius = float.NaN }; world.Models.Add(model);
        Assert.Throws<InvalidDataException>(() => GameZWriter.Write(world, Token));
        model.BoundsRadius = 0;
        Assert.NotEmpty(GameZWriter.Write(world, Token));
    }
    [Theory]
    [InlineData(float.PositiveInfinity)][InlineData(float.NegativeInfinity)][InlineData(float.NaN)]
    public void SparseOverridesMustBeFinite(float value)
    {
        var (json, bin) = Triangle().Write("mesh.bin", TestContext.Current.CancellationToken); var root = JsonNode.Parse(json)!;
        int oldLength = bin.Length; Array.Resize(ref bin, oldLength + 16);
        BinaryPrimitives.WriteSingleLittleEndian(bin.AsSpan(oldLength + 4), value);
        var views = root["bufferViews"]!.AsArray(); int vi = views.Count;
        views.Add(new JsonObject { ["buffer"] = 0, ["byteOffset"] = oldLength, ["byteLength"] = 1 });
        views.Add(new JsonObject { ["buffer"] = 0, ["byteOffset"] = oldLength + 4, ["byteLength"] = 12 });
        root["buffers"]![0]!["byteLength"] = bin.Length;
        root["accessors"]![0]!["sparse"] = new JsonObject { ["count"] = 1,
            ["indices"] = new JsonObject { ["bufferView"] = vi, ["componentType"] = 5121 },
            ["values"] = new JsonObject { ["bufferView"] = vi + 1 } };
        Assert.Contains("accessor 0", Assert.Throws<InvalidDataException>(() => Read(root, bin)).Message);
    }
    [Theory]
    [InlineData(false, 0.5f, false)][InlineData(true, 0.2f, false)][InlineData(true, 0.5f, true)]
    public void TexturedMasksRequireRecordedEngineSemantics(bool recorded, float cutoff, bool accepted)
    {
        var doc = Triangle(); var material = new GltfMaterial { ImageUri = "rock.png", AlphaMode = "MASK", AlphaCutoff = cutoff };
        if (recorded) material.Extras = new JsonObject { ["recoil"] = new JsonObject { ["texture"] = "rock" } };
        doc.Roots[0].Mesh!.Primitives[0].Material = material;
        var (json, bin) = doc.Write("mesh.bin", TestContext.Current.CancellationToken); var parsed = Read(JsonNode.Parse(json)!, bin);
        Assert.Equal(cutoff, parsed.Roots[0].Mesh!.Primitives[0].Material!.AlphaCutoff);
        if (accepted) Assert.NotEmpty(Import(parsed));
        else Assert.Contains("MASK", Assert.Throws<InvalidDataException>(() => Import(parsed)).Message);
        Assert.NotEmpty(Import(Triangle()));
    }
    [Theory]
    [InlineData(0)][InlineData(1)][InlineData(2)][InlineData(3)][InlineData(7)]
    public void UnsupportedPrimitiveModesCannotDisappear(int mode)
    {
        var (json, bin) = Triangle().Write("mesh.bin", TestContext.Current.CancellationToken); var root = JsonNode.Parse(json)!;
        root["meshes"]![0]!["primitives"]![0]!["mode"] = mode;
        Assert.Contains("mode", Assert.Throws<InvalidDataException>(() => Read(root, bin)).Message);
    }
    [Theory]
    [InlineData("POSITION", false)][InlineData("NORMAL", false)][InlineData("TEXCOORD_0", false)]
    [InlineData("POSITION", true)]
    public void NonfiniteAccessorComponentsFailBeforeGeometryImport(string attribute, bool morph)
    {
        var (json, bin) = Triangle().Write("mesh.bin", TestContext.Current.CancellationToken); var root = JsonNode.Parse(json)!;
        var p = root["meshes"]![0]!["primitives"]![0]!;
        int index = (morph ? p["targets"]![0]![attribute] : p["attributes"]![attribute])!.GetValue<int>();
        var a = root["accessors"]![index]!;
        int view = a["bufferView"]!.GetValue<int>(); int offset = root["bufferViews"]![view]!["byteOffset"]?.GetValue<int>() ?? 0;
        BinaryPrimitives.WriteSingleLittleEndian(bin.AsSpan(offset), float.NaN);
        Assert.Contains($"accessor {index}", Assert.Throws<InvalidDataException>(() => Read(root, bin)).Message);
    }
    [Theory]
    [InlineData(0f, 0.5f, 0)][InlineData(0.4f, 0.5f, 0)][InlineData(0.5f, 0.5f, 255)][InlineData(0f, 0f, 255)]
    public void UntexturedMasksRetainTheCutoffResult(float alpha, float cutoff, int expected)
    {
        var doc = Triangle(); doc.Roots[0].Mesh!.Primitives[0].Material = new() { AlphaMode = "MASK", BaseColor = new(1,1,1,alpha) };
        var (json, bin) = doc.Write("mesh.bin", TestContext.Current.CancellationToken); var root = JsonNode.Parse(json)!; root["materials"]![0]!["alphaCutoff"] = cutoff;
        var imported = Import(Read(root, bin)); Assert.Equal(expected, (int)(imported[0].Model!.Polygons[0].Material!.Flags & 255));
    }
}
