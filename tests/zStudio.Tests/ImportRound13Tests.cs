using System.Text;
using System.Text.Json.Nodes;
using Recoil.Zbd.Core.Gltf;
using Recoil.Zbd.Core.Sources;
using Recoil.Zbd.Core.Worlds;
using Xunit;

namespace Recoil.Zbd.Tests;

public sealed class ImportRound13Tests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;
    private static GltfDocument Read(JsonObject json) => GltfDocument.Read(Encoding.UTF8.GetBytes(json.ToJsonString()), _ => [], Token);
    private static JsonObject Textured() => JsonNode.Parse("""
        {"asset":{"version":"2.0"},"materials":[{"pbrMetallicRoughness":{"metallicFactor":0,"baseColorTexture":{"index":0}}}],
        "textures":[{"source":0}],"images":[{"uri":"rock.png"}],"nodes":[{"mesh":0}],
        "meshes":[{"primitives":[{"material":0,"attributes":{"POSITION":0,"TEXCOORD_0":1}}]}],
        "accessors":[{"componentType":5126,"type":"VEC3","count":3},{"componentType":5126,"type":"VEC2","count":3}]}
        """)!.AsObject();

    [Theory]
    [InlineData("index")][InlineData("source")]
    public void IncompleteTextureReferenceIsRefused(string missing)
    {
        var json = Textured();
        if (missing == "index") json["materials"]![0]!["pbrMetallicRoughness"]!["baseColorTexture"]!.AsObject().Remove(missing);
        else json["textures"]![0]!.AsObject().Remove(missing);
        Assert.Contains(missing, Assert.Throws<InvalidDataException>(() => Read(json)).Message);
    }

    [Theory]
    [InlineData(0)][InlineData(1)]
    public void TextureNeedsItsSelectedUvSet(int set)
    {
        var json = Textured();
        json["materials"]![0]!["pbrMetallicRoughness"]!["baseColorTexture"]!["texCoord"] = set;
        if (set == 0) json["meshes"]![0]!["primitives"]![0]!["attributes"]!.AsObject().Remove("TEXCOORD_0");
        Assert.Contains("TEXCOORD_" + set, Assert.Throws<InvalidDataException>(() => WorldGltf.ValidateSupported(Read(json), "test.gltf")).Message);
    }

    [Fact]
    public void SharedMaterialCannotHideMissingUvOnSecondPrimitive()
    {
        var json = Textured();
        var primitives = json["meshes"]![0]!["primitives"]!.AsArray();
        var copy = primitives[0]!.DeepClone(); copy["attributes"]!.AsObject().Remove("TEXCOORD_0"); primitives.Add(copy);
        Assert.Throws<InvalidDataException>(() => WorldGltf.ValidateSupported(Read(json), "test.gltf"));
    }

    [Fact]
    public void CoreCameraIsRefusedBeforeBecomingAnEmptyNode()
    {
        var json = JsonNode.Parse("""
            {"asset":{"version":"2.0"},"cameras":[{"type":"perspective","perspective":{"yfov":1,"znear":0.1}}],"nodes":[{"camera":0}]}
            """)!.AsObject();
        Assert.Contains("camera", Assert.Throws<InvalidDataException>(() => Read(json)).Message);
    }

    private static GameZWorld TwoWorlds(bool second = true)
    {
        GameZWorld world = new(); world.Nodes.Add(new("first", WorldNodeClass.World));
        if (second)
        {
            WorldNode other = new("second", WorldNodeClass.World), child = new("child", WorldNodeClass.Object3D);
            other.Children.Add(child); child.Parents.Add(other); world.Nodes.AddRange([other, child]);
        }
        return world;
    }

    [Fact]
    public void DetachedWorldRemovalIsADifference()
    {
        var compared = WorldComparer.CompareTree(TwoWorlds(), TwoWorlds(false), token: Token);
        Assert.True(compared.DifferenceCount > 0);
        var second = Assert.Single(compared.Roots, n => n.Name == "second");
        Assert.Equal(WorldComparisonStatus.OnlyExpected, second.Status);
        Assert.Equal("child", Assert.Single(second.Children).Name);
    }

    [Fact]
    public void ReorderedDistinctWorldsKeepTheirCounterparts()
    {
        var a = TwoWorlds(); var b = TwoWorlds(); b.Nodes.Reverse();
        var compared = WorldComparer.CompareTree(a, b, token: Token);
        Assert.Empty(compared.Differences);
        Assert.Equal("first", compared.Counterparts[a.Nodes[0]].Name);
        Assert.Equal("second", compared.Counterparts[a.Nodes[1]].Name);
    }

    [Fact]
    public void DetachedWorldDescendantIsCompared()
    {
        var a = TwoWorlds(); var b = TwoWorlds(); b.Nodes[2].Zone = 7;
        var compared = WorldComparer.CompareTree(a, b, token: Token);
        Assert.Contains(compared.Differences, d => d.Field == "zone");
        Assert.Same(b.Nodes[2], compared.Counterparts[a.Nodes[2]]);
        Assert.Empty(WorldComparer.Compare(TwoWorlds(), TwoWorlds()));
    }

    [Theory]
    [InlineData(false)][InlineData(true)]
    public void DetachedWorldAreaOnlyDescendantIsCompared(bool nested)
    {
        var a = TwoWorlds(); var b = TwoWorlds();
        foreach (var world in new[] { a, b })
        {
            var holder = world.Nodes[1]; var child = world.Nodes[2];
            holder.Children.Clear();
            if (nested)
            {
                WorldNode inner = new("inner", WorldNodeClass.World); holder.Children.Add(inner); inner.Parents.Add(holder);
                world.Nodes.Add(inner); child.Parents.Clear(); child.Parents.Add(inner); holder = inner;
            }
            WorldArea area = new(); area.Nodes.Add(child); holder.Areas.Add(area);
        }
        b.Nodes[2].Zone = 9;
        var compared = WorldComparer.CompareTree(a, b, token: Token);
        Assert.Contains(compared.Differences, d => d.Field == "zone");
        Assert.Same(b.Nodes[2], compared.Counterparts[a.Nodes[2]]);
    }

    [Fact]
    public void UnpairedFirstWorldKeepsItsSubtree()
    {
        var compared = WorldComparer.CompareTree(TwoWorlds(), new(), token: Token);
        Assert.Contains(compared.Roots, n => n.Name == "first" && n.Status == WorldComparisonStatus.OnlyExpected);
        Assert.Equal("child", Assert.Single(compared.Roots.Single(n => n.Name == "second").Children).Name);
    }

    [Fact]
    public void SameNamedWorldsWithDifferentAreaOnlyMembersAreNotInterchangeable()
    {
        WorldNode a = new("world", WorldNodeClass.World), b = new("world", WorldNodeClass.World);
        WorldNode x = new("member", WorldNodeClass.Object3D), y = new("member", WorldNodeClass.Object3D) { Zone = 7 };
        WorldArea aa = new(), bb = new(); aa.Nodes.Add(x); bb.Nodes.Add(y); a.Areas.Add(aa); b.Areas.Add(bb);
        Assert.False(WorldComparer.Interchangeable(a, b, token: Token));
        y.Zone = x.Zone;
        Assert.True(WorldComparer.Interchangeable(a, b, token: Token));
    }

    [Fact]
    public void LargeAppliedUpdateRecordsAllFilesAndRepeatedUpdates()
    {
        using SourceWorldFixture fixture = new();
        var checkout = SourceBlender.Checkout(new SourceWorkspace(fixture.Project), "data/m1/models/m1.gltf", Token);
        var changes = Enumerable.Range(0, 4096).Select(i => ($"data/m1/textures/t{i}.png", new byte[] { 1 })).ToList();
        changes.Add((checkout.Model, [2])); changes.Add(("data/m1/models/m1.bin", [3]));
        SourceBlender.RecordApplied(checkout, new("many", changes, [], checkout.Folder));
        var read = SourceBlender.Find(fixture.Project, checkout.Id);
        Assert.Equal(changes.Count, read.Applied.Count);
        Assert.All(changes, change => Assert.Contains(read.Applied, state => state.Project == change.Item1 && state.Sha256 == SourceProject.Sha256(change.Item2)));
        SourceBlender.RecordApplied(read, new("same", changes, [], checkout.Folder));
        Assert.Equal(changes.Count, SourceBlender.Find(fixture.Project, checkout.Id).Applied.Count);
        SourceBlender.RecordApplied(read, new("next", [(checkout.Model, new byte[] { 4 })], [], checkout.Folder));
        read = SourceBlender.Find(fixture.Project, checkout.Id);
        Assert.Contains(read.Applied, state => state.Project == checkout.Model && state.Sha256 == SourceProject.Sha256([4]));
        Assert.Contains(read.Applied, state => state.Project == checkout.Model && state.Sha256 == SourceProject.Sha256([2]));
    }
}
