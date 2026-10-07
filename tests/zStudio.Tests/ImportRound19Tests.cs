using System.Numerics;
using System.Text.Json.Nodes;
using Recoil.Zbd.Core.Formats;
using Recoil.Zbd.Core.Gltf;
using Recoil.Zbd.Core.Sources;
using Recoil.Zbd.Core.Worlds;
using Xunit;

namespace Recoil.Zbd.Tests;

public sealed class ImportRound19Tests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;
    private static WorldGltf.ImportContext Context() => new() { World = new(), Reference = (_, _) => throw new InvalidOperationException(), TextureName = (_, _, _) => "", Token = Token };

    [Theory]
    [InlineData("node", "null")][InlineData("node", "[]")][InlineData("node", "3")]
    [InlineData("material", "null")][InlineData("material", "[]")][InlineData("material", "3")]
    [InlineData("mesh", "null")][InlineData("primitive", "[]")][InlineData("scene", "3")]
    public void MalformedEngineMetadataCannotDisappear(string target, string value)
    {
        JsonObject extras = new() { [WorldGltf.Key] = JsonNode.Parse(value) };
        GltfMaterial material = new() { MetallicFactor = 0 }; GltfPrimitive primitive = new() { Material = material };
        primitive.Positions.AddRange([Vector3.Zero, Vector3.UnitX, Vector3.UnitZ]); primitive.Indices.AddRange([0,1,2]);
        GltfMesh mesh = new(); mesh.Primitives.Add(primitive); GltfDocument doc = new(); doc.Roots.Add(new() { Name = "valid", Mesh = mesh });
        switch (target)
        {
            case "node": doc.Roots[0].Extras = extras; break;
            case "material": material.Extras = extras; break;
            case "mesh": mesh.Extras = extras; break;
            case "primitive": primitive.Extras = extras; break;
            case "scene": doc.SceneExtras = extras; break;
        }
        Assert.Throws<InvalidDataException>(() => WorldGltf.Import(doc, "model.gltf", 255, Context()));
    }

    [Fact]
    public void Latin1BoundaryAndBlenderDisplayNamesKeepEffectiveIdentity()
    {
        string name = new string('x', 34) + "\u00e9";
        GltfDocument doc = new(); doc.Roots.Add(new() { Name = name + ".001" });
        Assert.Equal(name, Assert.Single(WorldGltf.Import(doc,"model.gltf",255,Context())).Name);
        doc.Roots[0].Name = new string('x', 100_000);
        doc.Roots[0].Extras = new JsonObject { [WorldGltf.Key] = new JsonObject { ["name"] = name } };
        Assert.Equal(name, Assert.Single(WorldGltf.Import(doc,"model.gltf",255,Context())).Name);
    }

    [Fact]
    public void AreaMembershipRetainsMultiplicityButNotListOrder()
    {
        GameZWorld Make(bool changed)
        {
            GameZWorld w = new(); WorldNode root = new("world",WorldNodeClass.World), a = new("a",WorldNodeClass.Object3D), b = new("b",WorldNodeClass.Object3D);
            root.Children.AddRange([a,b]); a.Parents.Add(root); b.Parents.Add(root); WorldArea area = new(); area.Nodes.AddRange(changed ? [a,b,b] : [b,a,a]); root.Areas.Add(area); w.Nodes.AddRange([root,a,b]); return w;
        }
        var a = Make(false); var b = Make(false); b.Nodes[0].Areas[0].Nodes.Reverse();
        Assert.Equal(0,WorldComparer.CompareTree(a,b,token:Token).DifferenceCount);
        Assert.Contains(WorldComparer.CompareTree(a,Make(true),token:Token).Differences,d=>d.Field=="world.area0");
    }

    [Theory]
    [InlineData("abcdefghijklmnopqrstuvwxyz0123456789")][InlineData("snow\u2603")][InlineData("hidden\0suffix")]
    public void EffectiveEngineNamesMustSurviveSerialization(string name)
    {
        GltfDocument doc = new(); doc.Roots.Add(new() { Name = name });
        Assert.Throws<InvalidDataException>(() => WorldGltf.Import(doc, "model.gltf", 255, Context()));
        doc.Roots[0].Name = "safe";
        doc.Roots[0].Extras = new JsonObject { [WorldGltf.Key] = new JsonObject { ["name"] = name } };
        Assert.Throws<InvalidDataException>(() => WorldGltf.Import(doc, "model.gltf", 255, Context()));
    }

    [Theory]
    [InlineData("fields", "[]")][InlineData("fields", "[1,2]")][InlineData("fields", "[1,2,3,4]")]
    [InlineData("fields", "{}")] [InlineData("fields", "null")]
    [InlineData("color", "[1,2]")][InlineData("color", "null")]
    public void PresentMaterialVectorsCannotFallBackToDefaults(string field, string value)
    {
        var material = new GltfMaterial { MetallicFactor = 0, Extras = new JsonObject { [WorldGltf.Key] = new JsonObject { [field] = JsonNode.Parse(value) } } };
        GltfPrimitive primitive = new() { Material = material };
        primitive.Positions.AddRange([Vector3.Zero, Vector3.UnitX, Vector3.UnitZ]); primitive.Indices.AddRange([0,1,2]);
        GltfMesh mesh = new(); mesh.Primitives.Add(primitive); GltfDocument doc = new(); doc.Roots.Add(new() { Name = "valid", Mesh = mesh });
        var context = Context();
        Assert.Throws<InvalidDataException>(() => WorldGltf.Import(doc, "model.gltf", 255, context));
        Assert.Empty(context.World.Materials);
        material.Extras[WorldGltf.Key]![field] = new JsonArray(1,2,3);
        Assert.Single(WorldGltf.Import(doc, "model.gltf", 255, context));
    }

    [Theory]
    [InlineData(false)][InlineData(true)]
    public void ImagePlacementSkipsHugeStringsBeforeCopying(bool path)
    {
        static ZrdNode S(string s) => new(Guid.NewGuid(), ZrdKind.String, 0, s, []);
        static ZrdNode A(params ZrdNode[] c) => new(Guid.NewGuid(), ZrdKind.Array, 0, "", c);
        var tree = path ? A(S("IMAGE_PATH"), A(S("..\\data\\" + new string('X', 2_000_000))), S("icon")) : A(S(new string('X', 2_000_000)), S("icon"));
        TextureSources.PlaceImages(["icon"], [("zrdr.zbd", "hud.zrd", A(S("icon")))]);
        long before = GC.GetAllocatedBytesForCurrentThread();
        var folders = TextureSources.PlaceImages(["ICON"], [("zrdr.zbd", "hud.zrd", tree)]);
        Assert.True(GC.GetAllocatedBytesForCurrentThread() - before < 100_000);
        Assert.Equal(TextureSources.HudImages, Assert.Single(folders));
    }

    [Theory]
    [InlineData("area")][InlineData("camera")][InlineData("light")][InlineData("worldLight")][InlineData("worldSound")]
    public void ReferencesToDistinctSameNamedNodesAreDifferent(string kind)
    {
        GameZWorld Make(bool swapped)
        {
            GameZWorld w = new(); WorldNode root = new("world", WorldNodeClass.World);
            WorldNode a = new("duplicate", WorldNodeClass.Object3D), b = new("duplicate", WorldNodeClass.Object3D);
            a.Flags = 1; b.Flags = 2; root.Children.AddRange([a,b]); a.Parents.Add(root); b.Parents.Add(root); w.Nodes.AddRange([root,a,b]);
            WorldNode selected = swapped ? b : a;
            if (kind == "area") { WorldArea area = new(); area.Nodes.Add(selected); root.Areas.Add(area); }
            if (kind == "camera") { WorldNode camera = new("camera",WorldNodeClass.Camera) { CameraHorizon = selected }; w.Nodes.Add(camera); }
            if (kind == "light") { WorldNode light = new("light",WorldNodeClass.Light); light.AttachedWorlds.Add(selected); w.Nodes.Add(light); }
            if (kind == "worldLight") root.WorldLights.Add(selected);
            if (kind == "worldSound") root.WorldSounds.Add(selected);
            return w;
        }
        var expected = Make(false); var same = Make(false); same.Nodes.Reverse();
        Assert.Equal(0, WorldComparer.CompareTree(expected, same, token: Token).DifferenceCount);
        Assert.NotEqual(0, WorldComparer.CompareTree(expected, Make(true), token: Token).DifferenceCount);
    }

    [Theory]
    [InlineData(false)][InlineData(true)]
    public async Task DifferentDuplicateScriptsRefuseReconstruction(bool different)
    {
        using var fixture = new SourceFixture(); string file = Path.Combine(fixture.Corpus,"interp.zbd");
        var package = FormatRegistry.Default.OpenBytes(file, File.ReadAllBytes(file), token: Token).Scripts!;
        var first = package.Entries[0];
        var duplicate = first with { Id = Guid.NewGuid(), Name = first.Name.ToUpperInvariant(), Instructions = different ? [new(Guid.NewGuid(), ["Quit"], ReadOnlyMemory<byte>.Empty, null)] : first.Instructions };
        File.WriteAllBytes(file, PreparedScriptWriter.Write(package with { Entries = [.. package.Entries, duplicate] }, Token));
        if (different)
        {
            Assert.Contains("duplicate", (await Assert.ThrowsAsync<IOException>(() => SourceExtractor.ExtractAsync(fixture.Corpus,fixture.Project,token:Token))).Message, StringComparison.OrdinalIgnoreCase);
            Assert.False(Directory.Exists(fixture.Project) && Directory.EnumerateFileSystemEntries(fixture.Project).Any());
        }
        else await SourceExtractor.ExtractAsync(fixture.Corpus,fixture.Project,token:Token);
    }
}

