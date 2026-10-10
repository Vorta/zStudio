using System.IO;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Recoil.Zbd.Core.Gltf;
using Recoil.Zbd.Core.Sources;
using Recoil.Zbd.Core.Worlds;
using Xunit;

namespace Recoil.Zbd.Tests;

/// <summary>Zones a node passes to its children, and which files and groups terrain can be made from.</summary>
public sealed class TerrainSourceZoneTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    private static List<WorldNode> Import(JsonObject gltf) =>
        WorldGltf.Import(GltfDocument.Read(Encoding.UTF8.GetBytes(gltf.ToJsonString()), _ => throw new InvalidOperationException(), Token), "zones.gltf", 0xFF,
            new() { World = new(), Reference = (_, _) => throw new InvalidOperationException(), TextureName = (uri, name, _) => name ?? uri });
    private static WorldNode Find(IEnumerable<WorldNode> roots, string name) =>
        roots.SelectMany(Subtree).First(n => n.Name == name);
    private static IEnumerable<WorldNode> Subtree(WorldNode node) => node.Children.SelectMany(Subtree).Prepend(node);

    [Fact]
    public void AZoneWordPassesItsZoneOnAndMovesKeepIt()
    {
        // root (no zone: it takes the load's) → holder (a zone word for zone 4, no zone of its own) → leaf (no zone);
        // other states zone 7.
        var gltf = JsonNode.Parse("""
            {"asset":{"version":"2.0"},"scene":0,"scenes":[{"nodes":[0,3]}],
             "nodes":[{"name":"root","children":[1]},{"name":"holder","children":[2],"extras":{"recoil":{"zoneWord":"0x00000104"}}},
                      {"name":"leaf"},{"name":"other","extras":{"recoil":{"zone":7}}}]}
            """)!.AsObject();
        var roots = Import(gltf);
        // The holder's zone is the word's low byte, and that is what its children inherit.
        Assert.Equal(0x104u, Find(roots, "holder").Zone);
        Assert.Equal(4u, Find(roots, "leaf").Zone);
        // Moving the leaf under other keeps its zone from the holder's word, which the file states.
        var moved = gltf.DeepClone().AsObject();
        GltfNodeEdits.Reparent(moved, 2, 3, token: Token);
        Assert.Equal(4u, Find(Import(moved), "leaf").Zone);
        // Moving the holder keeps the word, so the leaf keeps the zone it inherits from it.
        moved = gltf.DeepClone().AsObject();
        GltfNodeEdits.Reparent(moved, 1, 3, currentZone: 4, token: Token);
        var after = Import(moved);
        Assert.Equal(0x104u, Find(after, "holder").Zone); Assert.Equal(4u, Find(after, "leaf").Zone);
        // A zone beside the word states the node's zone: it replaces the word's low byte, for the node and its children.
        var both = gltf.DeepClone().AsObject();
        both["nodes"]![1]!["extras"]!["recoil"]!["zone"] = 9;
        var stated = Import(both);
        Assert.Equal(0x109u, Find(stated, "holder").Zone); Assert.Equal(9u, Find(stated, "leaf").Zone);
    }

    [Fact]
    public void TerrainIsNotMadeFromAFileTheDatabaseLoads()
    {
        using SourceWorldFixture fixture = new();
        fixture.WritePartDatabase();
        SourceWorkspace workspace = new(fixture.Project);
        const string Database = "data/m1/models/m1.gltf";
        // m1_01.gltf is a part of the database: its post stays an object of the world whatever a recipe says.
        var refused = Assert.Throws<InvalidDataException>(() => SourceTerrain.Create(workspace, Database, "data/m1/models/m1_01.gltf", ["post"], token: Token));
        Assert.Contains("loaded with the mission database", refused.Message);
        Assert.False(workspace.IsDirty);
        // The same surfaces in a file of their own become terrain.
        fixture.Write("data/m1/models/rocks.gltf", File.ReadAllBytes(fixture.Path("data/m1/models/m1_01.gltf")));
        SourceTerrain.Create(workspace, Database, "data/m1/models/rocks.gltf", ["post"], token: Token);
        Assert.Contains("data/m1/models/rocks.terrain.json", workspace.DirtyFiles);
    }

    [Fact]
    public void PiecesOfAGroupSeveralParentsShareStayObjects()
    {
        using SourceWorldFixture fixture = new();
        // Two groups share a group holding a piece (written once under each, with instance marks); a third holds a piece of its own.
        WorldNode Node(string name, float x = 0, bool piece = false)
        {
            WorldModel? model = null;
            if (piece)
            {
                ModelBuilder builder = new();
                builder.Add(new([new(x, 0, 20), new(x + 20, 0, 20), new(x + 20, 0, 0), new(x, 0, 0)], [new(0, 0), new(1, 0), new(1, 1), new(0, 1)], [], [], new() { Texture = new("rock"), Flags = 0x1FF }));
                model = builder.Finish();
            }
            WorldNode node = new(name, WorldNodeClass.Object3D) { Model = model, Flags = WorldGltf.DefaultCarried, Zone = 3 };
            node.SetPayloadInt(0, 0x28);
            return node;
        }
        var shelf = Node("shelf"); shelf.Children.Add(Node("plank", 100, piece: true));
        WorldNode left = Node("left"), right = Node("right"), own = Node("own");
        foreach (var holder in new[] { left, right }) { holder.Children.Add(shelf); shelf.Parents.Add(holder); }
        own.Children.Add(Node("slab", 300, piece: true));
        HashSet<WorldNode> groups = new(ReferenceEqualityComparer.Instance) { shelf, left, right, own };
        var (json, bin) = WorldGltf.Export([left, right, own], 0xFF, new() { Texture = t => ($"../textures/{t.Name}.png", 0), Group = groups.Contains }).Write("m1.bin", TestContext.Current.CancellationToken);
        fixture.Write("data/m1/models/m1.gltf", json); fixture.Write("data/m1/models/m1.bin", bin);
        Assert.Equal(2, Regex.Count(Encoding.UTF8.GetString(json), "\"plank\""));
        SourceWorkspace workspace = new(fixture.Project);
        var plan = SourceTerrainConversion.Plan(workspace, "data/m1/models/m1.gltf", (new HashSet<string>(), []), Token);
        // Import reads the shared group's first copy only: converting each copy's plank would take it twice, and leave the copies different.
        Assert.Equal(1, plan.Converted);
        Assert.Equal(["slab"], plan.Groups.SelectMany(g => g.Nodes).Select(i => (string?)JsonNode.Parse(json)!["nodes"]![i]!["name"]));
        Assert.Single(plan.Kept, k => k.Node == "shelf" && k.Reason == "a group several parents share");
    }
}
