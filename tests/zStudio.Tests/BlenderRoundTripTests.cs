using System.IO;
using System.Numerics;
using System.Text;
using System.Text.Json.Nodes;
using Recoil.Zbd.Core;
using Recoil.Zbd.Core.Formats;
using Recoil.Zbd.Core.Gltf;
using Recoil.Zbd.Core.Sources;
using Recoil.Zbd.Core.Worlds;
using Xunit;

namespace Recoil.Zbd.Tests;

/// <summary>
/// What a Blender round trip must keep that Blender does not know about: the zones nodes inherit, the copies of a shared
/// node, the transforms of mission database groups, and the presentation hints of the copy Blender opens.
/// </summary>
public sealed class BlenderRoundTripTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;
    private const string Database = "data/m1/models/m1.gltf", Zones = "data/m1/models/zones.gltf";

    /// <summary>Writes the checkout's input into the outbox (below <paramref name="folder"/>) with an edit, as a Blender export.</summary>
    private static string Export(BlenderCheckout checkout, string folder, Action<JsonObject>? change = null)
    {
        string input = Path.GetDirectoryName(checkout.Input)!, outbox = Path.Combine(checkout.Outbox, folder);
        foreach (string file in Directory.GetFiles(input, "*", SearchOption.AllDirectories))
        {
            string target = Path.Combine(outbox, Path.GetRelativePath(input, file));
            Directory.CreateDirectory(Path.GetDirectoryName(target)!); File.Copy(file, target, true);
        }
        var json = JsonNode.Parse(File.ReadAllText(checkout.Input))!.AsObject();
        change?.Invoke(json);
        string gltf = Path.Combine(outbox, Path.GetFileName(checkout.Input));
        File.WriteAllText(gltf, json.ToJsonString());
        return Path.GetRelativePath(checkout.Outbox, gltf).Replace('\\', '/');
    }
    private static JsonObject Plan(SourceWorkspace workspace, BlenderCheckout checkout, string export, string model) =>
        JsonNode.Parse(SourceBlender.PlanUpdate(workspace, checkout, export, token: Token).Changes.Single(c => c.Relative == model).Content)!.AsObject();
    private static JsonArray Nodes(JsonObject gltf) => gltf["nodes"]!.AsArray();
    private static int IndexOf(JsonObject gltf, string name) => Nodes(gltf).Select((n, i) => (n, i)).Single(p => (string?)p.n!["name"] == name).i;
    private static JsonObject Named(JsonObject gltf, string name) => Nodes(gltf)[IndexOf(gltf, name)]!.AsObject();
    private static JsonObject? Engine(JsonObject gltf, string name) => Named(gltf, name)["extras"]?[WorldGltf.Key] as JsonObject;
    /// <summary>Moves a node under another, or to the scene's roots, as re-parenting in Blender (keeping its local transform) writes it.</summary>
    private static void Move(JsonObject gltf, string name, string? parent)
    {
        int index = IndexOf(gltf, name);
        foreach (var node in Nodes(gltf)) if (node!["children"] is JsonArray children) { foreach (var c in children.Where(c => c!.GetValue<int>() == index).ToList()) children.Remove(c); if (children.Count == 0) node.AsObject().Remove("children"); }
        var roots = gltf["scenes"]![0]!["nodes"]!.AsArray();
        foreach (var r in roots.Where(r => r!.GetValue<int>() == index).ToList()) roots.Remove(r);
        if (parent == null) { roots.Add(index); return; }
        var holder = Named(gltf, parent);
        if (holder["children"] is not JsonArray list) holder["children"] = list = [];
        list.Add(index);
    }
    private static WorldNode Node(string name, uint zone = 0xFF, WorldMaterial? material = null)
    {
        ModelBuilder builder = new();
        builder.Add(new([new(0, 0, 0), new(4, 0, 0), new(4, 0, -4), new(0, 0, -4)], [new(0, 0), new(1, 0), new(1, 1), new(0, 1)], [], [], material ?? new() { Color = new(90, 80, 70), Flags = 0xFF }));
        WorldNode node = new(name, WorldNodeClass.Object3D) { Model = builder.Finish(), Flags = WorldGltf.DefaultCarried, Zone = zone };
        node.SetPayloadInt(0, 0x28);
        return node;
    }
    private static List<WorldNode> Import(JsonObject gltf, byte[] bin) =>
        WorldGltf.Import(GltfDocument.Read(Encoding.UTF8.GetBytes(gltf.ToJsonString()), _ => bin, Token), "model.gltf", 0xFF,
            new() { World = new(), Reference = (_, _) => throw new InvalidOperationException(), TextureName = (uri, name, _) => name ?? Path.GetFileNameWithoutExtension(uri) });
    private static WorldNode Find(IEnumerable<WorldNode> roots, string name) => roots.SelectMany(Subtree).First(n => n.Name == name);
    private static IEnumerable<WorldNode> Subtree(WorldNode node) => node.Children.SelectMany(Subtree).Prepend(node);

    [Fact]
    public void NodesBlenderMovesKeepTheirZones()
    {
        using SourceWorldFixture fixture = new();
        // a (zone 3) holds a1, which inherits it; b and b1 take theirs from what loads the file (0xFF).
        WorldNode a = Node("a", 3), a1 = Node("a1", 3), b = Node("b"), b1 = Node("b1");
        a.Children.Add(a1); b.Children.Add(b1);
        var (json, bin) = WorldGltf.Export([a, b], 0xFF, new() { Texture = _ => ("", 0) }).Write("zones.bin", TestContext.Current.CancellationToken);
        fixture.Write(Zones, json); fixture.Write("data/m1/models/zones.bin", bin);
        var original = JsonNode.Parse(json)!.AsObject();
        Assert.Null(Engine(original, "a1"));
        SourceWorkspace workspace = new(fixture.Project);
        var checkout = SourceBlender.Checkout(workspace, Zones, Token);
        // The copy Blender opens states every node's zone, or marks it as the load's.
        var input = JsonNode.Parse(File.ReadAllText(checkout.Input))!.AsObject();
        Assert.Equal(3u, WorldGltf.StatedZone(Engine(input, "a1")));
        Assert.True((bool?)Engine(input, "b1")?[WorldGltf.ZoneFromLoad]);
        // Unchanged, the update gives the project's nodes back as they were.
        Assert.True(JsonNode.DeepEquals(Nodes(original), Nodes(Plan(workspace, checkout, Export(checkout, "same"), Zones))));
        // a1 moved under b keeps zone 3, which the file now states.
        var moved = Plan(workspace, checkout, Export(checkout, "moved", g => Move(g, "a1", "b")), Zones);
        Assert.Equal(3u, WorldGltf.StatedZone(Engine(moved, "a1")));
        Assert.Equal(3u, Find(Import(moved, bin), "a1").Zone);
        // b1 under a would take a's zone 3 instead of the load's, which the file cannot tell: refused, saying how to keep it.
        var refused = Assert.Throws<InvalidDataException>(() => SourceBlender.PlanUpdate(workspace, checkout, Export(checkout, "refused", g => Move(g, "b1", "a")), token: Token));
        Assert.Contains("took its zone from what loads the file", refused.Message);
        // Given a zone of its own in Blender, it moves.
        var given = Plan(workspace, checkout, Export(checkout, "given", g => { Move(g, "b1", "a"); Engine(g, "b1")!["zone"] = 255; }), Zones);
        Assert.Equal(0xFFu, Find(Import(given, bin), "b1").Zone);
        // b given a zone in Blender: b1, still under it, takes that zone, as the artist meant.
        var zoned = Plan(workspace, checkout, Export(checkout, "zoned", g => Engine(g, "b")!["zone"] = 6), Zones);
        Assert.Equal(6u, Find(Import(zoned, bin), "b1").Zone);
    }

    [Fact]
    public void CopiesOfASharedNodeMustAgree()
    {
        using SourceWorldFixture fixture = new();
        fixture.WriteSharedDatabase();
        SourceWorkspace workspace = new(fixture.Project);
        var checkout = SourceBlender.Checkout(workspace, Database, Token);
        List<JsonObject> Gates(JsonObject g) => [.. Nodes(g).OfType<JsonObject>().Where(n => (string?)n["name"] == "gate")];
        Assert.Equal(2, Gates(JsonNode.Parse(File.ReadAllText(checkout.Input))!.AsObject()).Count);
        Plan(workspace, checkout, Export(checkout, "same"), Database);
        // The gate moved in the second copy only: import reads the first, so the move would be lost.
        var refused = Assert.Throws<InvalidDataException>(() => SourceBlender.PlanUpdate(workspace, checkout, Export(checkout, "one", g => Gates(g)[1]["translation"] = new JsonArray(0f, 5f, 0f)), token: Token));
        Assert.Contains("copies of shared node", refused.Message); Assert.Contains("the transform of gate", refused.Message);
        // So would a gate given another mesh, or a child, in one copy.
        Assert.Throws<InvalidDataException>(() => SourceBlender.PlanUpdate(workspace, checkout, Export(checkout, "child", g =>
        {
            Nodes(g).Add(new JsonObject { ["name"] = "flag" });
            Gates(g)[1]["children"] = new JsonArray(Nodes(g).Count - 1);
        }), token: Token));
        // Moved alike in every copy, it is one edit of the shared node.
        var both = Plan(workspace, checkout, Export(checkout, "both", g => { foreach (var gate in Gates(g)) gate["translation"] = new JsonArray(0f, 5f, 0f); }), Database);
        Assert.All(Gates(both), gate => Assert.Equal([0f, 5f, 0f], gate["translation"]!.AsArray().Select(v => v!.GetValue<float>())));
    }

    [Fact]
    public async Task AGroupMovedInBlenderMovesItsObjects()
    {
        using SourceWorldFixture fixture = new();
        fixture.WriteTerrainDatabase(grouped: true);
        SourceWorkspace workspace = new(fixture.Project);
        var checkout = SourceBlender.Checkout(workspace, Database, Token);
        // The group g1 (an empty in Blender) moved: its objects take the move, and the group stays without a transform.
        var plan = SourceBlender.PlanUpdate(workspace, checkout, Export(checkout, "moved", g => Named(g, "g1")["translation"] = new JsonArray(10f, 0f, 5f)), token: Token);
        workspace.Apply(plan.Label, plan.Changes.Select(c => (c.Relative, (byte[]?)c.Content)), Token);
        var updated = JsonNode.Parse(workspace.Read(Database, Token)!)!.AsObject();
        Assert.Null(Named(updated, "g1")["translation"]); Assert.Null(Named(updated, "g1")["matrix"]);
        Assert.Equal([10f, 0f, 5f], Named(updated, "flat_a")["translation"]!.AsArray().Select(v => v!.GetValue<float>()));
        // The build takes it, and the objects stand where Blender showed them.
        var build = await SourceWorlds.BuildPreviewAsync(fixture.Project, "m1", Path.Combine(SourceWorlds.PreviewRoot(fixture.Project), "moved"), workspace.Overlay(), token: Token);
        Assert.Null(build.Outputs.FirstOrDefault(o => o.Error != null)?.Error);
        var world = GameZWorldReader.FromDocument(FormatRegistry.Default.OpenBytes("gamez.zbd", await File.ReadAllBytesAsync(build.WorldPath, Token), token: Token), Token);
        Assert.Equal(new Vector3(10, 0, 5), WorldUpdate.LocalMatrix(world.Nodes.Single(n => n.Name == "flat_a"))!.Value.Translation);
        workspace.Undo();
        // Where the objects cannot take the move, the update says why: a part's objects are in its own file, and a
        // level-of-detail node stands where its parent is.
        var part = Assert.Throws<InvalidDataException>(() => SourceBlender.PlanUpdate(workspace, checkout, Export(checkout, "part", g => Named(g, "m1_01.flt")["translation"] = new JsonArray(1f, 0f, 0f)), token: Token));
        Assert.Contains("are in m1_01.gltf", part.Message);
        var lod = Assert.Throws<InvalidDataException>(() => SourceBlender.PlanUpdate(workspace, checkout, Export(checkout, "lod", g =>
        {
            Nodes(g).Add(new JsonObject { ["name"] = "band", ["extras"] = new JsonObject { [WorldGltf.Key] = new JsonObject { ["class"] = "lod" } } });
            Named(g, "g1")["children"]!.AsArray().Add(Nodes(g).Count - 1);
            Named(g, "g1")["translation"] = new JsonArray(1f, 0f, 0f);
        }), token: Token));
        Assert.Contains("level-of-detail node band", lod.Message);
        // Geometry on a group is refused here too, not only by the build.
        Assert.Contains("geometry", Assert.Throws<InvalidDataException>(() => SourceBlender.PlanUpdate(workspace, checkout, Export(checkout, "mesh", g => Named(g, "g1")["mesh"] = 0), token: Token)).Message);
    }

    [Fact]
    public void OnlyTheVolumeAPickupSwitchesOffIsHidden()
    {
        WorldMaterial violet = new() { Color = new(63, 15, 254), Flags = 0xFF };
        // One volume among the roots and one in the crate: FindSubNodeByName from the pickup visits the roots from last to
        // first, so it finds the crate's.
        var crate = Node("crate"); crate.Children.Add(Node("bvol", material: violet));
        var (json, bin) = WorldGltf.Export([Node("bvol", material: violet), crate], 0xFF, new() { Texture = _ => ("", 0) }).Write("ammo.bin", TestContext.Current.CancellationToken);
        var pickup = JsonNode.Parse(json)!.AsObject();
        int MaterialOf(JsonObject gltf, int node) => gltf["meshes"]![Nodes(gltf)[node]!["mesh"]!.GetValue<int>()]!["primitives"]![0]!["material"]!.GetValue<int>();
        int[] volumes = [.. Nodes(pickup).Select((n, i) => (n, i)).Where(p => (string?)p.n!["name"] == "bvol").Select(p => p.i)];
        int inCrate = volumes.Single(i => Nodes(pickup).Any(n => n!["children"] is JsonArray c && c.Any(x => x!.GetValue<int>() == i))), atRoot = volumes.Single(i => i != inCrate);
        Assert.True(WorldGltf.ApplyPresentation(pickup, _ => null, pickup: true));
        Assert.Equal("MASK", (string?)pickup["materials"]![MaterialOf(pickup, inCrate)]!["alphaMode"]);
        Assert.Null(pickup["materials"]![MaterialOf(pickup, atRoot)]!["alphaMode"]);
        // Outside a pickup the game draws a node named bvol.
        Assert.False(WorldGltf.ApplyPresentation(JsonNode.Parse(json)!.AsObject(), _ => null, pickup: false));
        // A material with a malformed base colour is left alone, not copied or repainted.
        var odd = JsonNode.Parse(json)!.AsObject();
        odd["materials"]![MaterialOf(odd, inCrate)]!["pbrMetallicRoughness"] = new JsonObject { ["baseColorFactor"] = new JsonArray(0.2, "x", 0.3, 1) };
        string before = odd.ToJsonString();
        Assert.False(WorldGltf.ApplyPresentation(odd, _ => null, pickup: true));
        Assert.Equal(before, odd.ToJsonString());

        // A volume material without engine attributes (Blender's own), made translucent: hidden, it records what import
        // read, so import reads the same values whatever an editor writes back for the look.
        var bare = JsonNode.Parse(json)!.AsObject();
        foreach (var material in bare["materials"]!.AsArray()) { material!.AsObject().Remove("extras"); material["alphaMode"] = "BLEND"; material["pbrMetallicRoughness"]!["baseColorFactor"]![3] = 0.5; }
        List<string> Surfaces(JsonObject gltf) => [.. Import(gltf, bin).SelectMany(Subtree).Where(n => n.Model != null).SelectMany(n => n.Model!.Polygons.Select(p =>
            $"{n.Name} {p.Material!.Flags:X} {p.Material.PackedColor:X} {p.Material.Color} {p.Material.Soil} {p.Flags:X} {p.Zone:X} {p.Normals.Length}"))];
        var expected = Surfaces(bare);
        Assert.Contains(expected, s => s.StartsWith("bvol 80 ", StringComparison.Ordinal));
        Assert.True(WorldGltf.ApplyPresentation(bare, _ => null, pickup: true));
        Assert.Equal(expected, Surfaces(bare));
        int hidden = MaterialOf(bare, inCrate);
        Assert.Equal(128, bare["materials"]![hidden]!["extras"]![WorldGltf.Key]!["opacity"]!.GetValue<int>());
        bare["materials"]![hidden]!["alphaMode"] = "BLEND";
        Assert.Equal(expected, Surfaces(bare));
    }

    [Fact]
    public void CheckoutsHideCollisionVolumesOnlyOfPickups()
    {
        using SourceWorldFixture fixture = new();
        var (json, bin) = WorldGltf.Export([Node("box"), Node("bvol", material: new() { Color = new(63, 15, 254), Flags = 0xFF })], 0xFF, new() { Texture = _ => ("", 0) }).Write("crate.bin", TestContext.Current.CancellationToken);
        fixture.Write("data/m1/models/crate.gltf", json); fixture.Write("data/m1/models/crate.bin", bin);
        SourceWorkspace workspace = new(fixture.Project);
        // No script loads the crate as a pickup: the game draws its bvol.
        Assert.DoesNotContain("~hidden", File.ReadAllText(SourceBlender.Checkout(workspace, "data/m1/models/crate.gltf", Token).Input));
        // A placed pickup's name counts as well as a template's, a number past the 40 types does not.
        Assert.True(WorldGltf.IsPickupName("pu012")); Assert.True(WorldGltf.IsPickupName("pu01203")); Assert.False(WorldGltf.IsPickupName("pu040"));
        Assert.False(WorldGltf.IsPickupName("pu4101")); Assert.False(WorldGltf.IsPickupName("pump01")); Assert.True(WorldGltf.IsPickupName("pu4099x"));
        // A script m1's build does not run loads nothing; once m1 sources it, the crate is a pickup.
        fixture.Write("gamegen/support/pickup.gw", "LoadGameGen crate.gltf pu01203\r\n");
        Assert.DoesNotContain("~hidden", File.ReadAllText(SourceBlender.Checkout(workspace, "data/m1/models/crate.gltf", Token).Input));
        fixture.Write("gamegen/m1.gs", File.ReadAllText(fixture.Path("gamegen/m1.gs")).Replace("# no vehicles", "source support\\pickup.gw", StringComparison.Ordinal));
        Assert.Contains("~hidden", File.ReadAllText(SourceBlender.Checkout(workspace, "data/m1/models/crate.gltf", Token).Input));
    }

    [Fact]
    public async Task UpdatesReadThePresentationBackAndBuildTheImagesAssigned()
    {
        using SourceWorldFixture fixture = new();
        const string Crate = "data/m1/models/crate.gltf";
        static byte[] Png(bool keyed)
        {
            byte[] rgba = new byte[8 * 8 * 4];
            for (int i = 0; i < 64; i++) { rgba[i * 4] = 90; rgba[i * 4 + 3] = keyed && i % 2 == 0 ? (byte)0 : (byte)255; }
            return Recoil.Zbd.Core.Export.PngEncoder.Encode(new DecodedImage(8, 8, rgba));
        }
        // A pickup m1's build loads: a recorded rock body and two translucent lamps (whose repeated name each records), and a
        // net and its collision volume in materials made in Blender (without engine attributes) over textures whose alpha is
        // only 0 or 255.
        WorldMaterial lamp = new() { Color = new(90, 80, 70), Flags = 0x80 };
        var (json, bin) = WorldGltf.Export([Node("body", material: new() { Texture = new("rock"), Flags = 0x1FF }), Node("net", material: new() { Texture = new("net"), Flags = 0x1FF }),
            Node("bvol", material: new() { Texture = new("grid"), Flags = 0x1FF }), Node("lamp", material: lamp), Node("lamp", material: lamp)],
            0xFF, new() { Texture = t => ($"../textures/{t.Name}.png", 0) }).Write("crate.bin", Token);
        var source = JsonNode.Parse(json)!.AsObject();
        foreach (var material in source["materials"]!.AsArray().Where(m => !((string)m!["name"]!).StartsWith("rock", StringComparison.Ordinal) && !((string)m!["name"]!).StartsWith("color", StringComparison.Ordinal))) material!.AsObject().Remove("extras");
        fixture.Write(Crate, source.ToJsonString()); fixture.Write("data/m1/models/crate.bin", bin);
        fixture.Write("data/m1/textures/net.png", Png(keyed: true)); fixture.Write("data/m1/textures/grid.png", Png(keyed: true));
        fixture.Write("gamegen/support/pickup.gw", "LoadGameGen crate.flt pu01203\r\nFindNode world\r\nAddChild pu01203\r\n");
        fixture.Write("gamegen/m1.gs", File.ReadAllText(fixture.Path("gamegen/m1.gs")).Replace("# no vehicles", "source support\\pickup.gw", StringComparison.Ordinal));
        SourceWorkspace workspace = new(fixture.Project);
        var checkout = SourceBlender.Checkout(workspace, Crate, Token);
        JsonObject Material(JsonObject gltf, string node) => gltf["materials"]![gltf["meshes"]![Named(gltf, node)["mesh"]!.GetValue<int>()]!["primitives"]![0]!["material"]!.GetValue<int>()]!.AsObject();
        // Blender keeps the checkout's Alpha Clip (at the default cutoff) and the hidden volume: the update reads the engine's
        // materials back, also when Blender drops the engine attributes (Custom Properties off).
        Assert.Null(Material(Plan(workspace, checkout, Export(checkout, "same"), Crate), "net")["alphaMode"]);
        static void Strip(JsonNode? node) { if (node is JsonObject o) { o.Remove("extras"); foreach (var (_, value) in o.ToList()) Strip(value); } else if (node is JsonArray a) foreach (var item in a) Strip(item); }
        var dropped = JsonNode.Parse(SourceBlender.PlanUpdate(workspace, checkout, Export(checkout, "dropped", Strip), force: true, token: Token).Changes.Single(c => c.Relative == Crate).Content)!.AsObject();
        Assert.Null(Material(dropped, "bvol")["alphaMode"]); Assert.Null(Material(dropped, "net")["alphaMode"]);
        // A clip the artist set at another cutoff is refused.
        Assert.Contains("MASK", Assert.Throws<InvalidDataException>(() => SourceBlender.PlanUpdate(workspace, checkout, Export(checkout, "clip", g => Material(g, "net")["alphaCutoff"] = 0.3), token: Token)).Message);
        // Its image removed in Blender, the body would still build with the texture it records: refused, saying how to untexture it.
        Assert.Contains("shows no image", Assert.Throws<InvalidDataException>(() => SourceBlender.PlanUpdate(workspace, checkout, Export(checkout, "removed", g => Material(g, "body")["pbrMetallicRoughness"]!.AsObject().Remove("baseColorTexture")), token: Token)).Message);
        // Given a name an earlier texture folder of m1 holds, the build would show that file instead: the update asks, naming it.
        fixture.Write("data/common/textures/shade.png", Png(keyed: true));
        string shaded = Export(checkout, "shade", g => { foreach (var image in g["images"]!.AsArray()) if ((string?)image!["uri"] == "textures/rock.png") image["uri"] = "textures/shade.png"; });
        File.WriteAllBytes(Path.Combine(checkout.Outbox, "shade", "textures", "shade.png"), Png(keyed: false));
        Assert.Contains("m1 finds data/common/textures/shade.png first", Assert.Throws<BlenderConflictException>(() => SourceBlender.PlanUpdate(workspace, checkout, shaded, token: Token)).Message);
        // The body given another image in Blender: its recorded texture name follows the image; the lamp's colour and
        // transparency changed in Blender are what its record holds. The second lamp renamed in Blender takes its new name,
        // which the update reports; the first, only numbered by Blender as a repeated name, keeps its own.
        string swapped = Export(checkout, "swap", g =>
        {
            foreach (var image in g["images"]!.AsArray()) if ((string?)image!["uri"] == "textures/rock.png") image["uri"] = "textures/newtex.png";
            var lamps = Nodes(g).OfType<JsonObject>().Where(n => (string?)n["name"] == "lamp").ToList();
            lamps[0]["name"] = "lamp.002"; lamps[1]["name"] = "beacon";
            Material(g, "lamp.002")["pbrMetallicRoughness"]!["baseColorFactor"] = new JsonArray(1.0, 0.0, 0.0, 0.25);
        });
        File.WriteAllBytes(Path.Combine(checkout.Outbox, "swap", "textures", "newtex.png"), Png(keyed: false));
        var plan = SourceBlender.PlanUpdate(workspace, checkout, swapped, token: Token);
        Assert.Contains(plan.Notes, note => note.EndsWith("1 renamed): lamp → beacon.", StringComparison.Ordinal));
        Assert.Equal("newtex", (string?)Material(JsonNode.Parse(plan.Changes.Single(c => c.Relative == Crate).Content)!.AsObject(), "body")["extras"]![WorldGltf.Key]!["texture"]);
        workspace.Apply(plan.Label, plan.Changes.Select(c => (c.Relative, (byte[]?)c.Content)), Token);
        // Even where a file still records the old name, the build uses the image the material shows.
        var stale = JsonNode.Parse(workspace.Read(Crate, Token)!)!.AsObject();
        Material(stale, "body")["extras"]![WorldGltf.Key]!["texture"] = "rock";
        workspace.Apply("stale", [(Crate, Encoding.UTF8.GetBytes(stale.ToJsonString()))], Token);
        var build = await SourceWorlds.BuildPreviewAsync(fixture.Project, "m1", Path.Combine(SourceWorlds.PreviewRoot(fixture.Project), "images"), workspace.Overlay(), token: Token);
        Assert.Null(build.Outputs.FirstOrDefault(o => o.Error != null)?.Error);
        var world = GameZWorldReader.FromDocument(FormatRegistry.Default.OpenBytes("gamez.zbd", await File.ReadAllBytesAsync(build.WorldPath, Token), token: Token), Token);
        string Surface(string name) => world.Nodes.Single(n => n.Name == name).Model!.Polygons.Select(p => $"{p.Material!.Texture?.Name} {p.Material.Flags:X}").Distinct().Single();
        Assert.Equal(["newtex 1FF", "net 1FF", "grid 1FF", " 40", " 40"], new[] { Surface("body"), Surface("net"), Surface("bvol"), Surface("lamp"), Surface("beacon") });
        Assert.Equal(new Vector3(255, 0, 0), world.Nodes.Single(n => n.Name == "lamp").Model!.Polygons.Select(p => p.Material!.Color).Distinct().Single());
    }

    private sealed class MemoryFiles(Dictionary<string, byte[]> files) : IProjectFiles
    {
        public Dictionary<string, byte[]> Files { get; } = files;
        public bool Exists(string relative) => Files.ContainsKey(relative);
        public byte[] Read(string relative, CancellationToken token) => Read(relative, token, ProjectReadLimits.Document);
        public byte[] Read(string relative, CancellationToken token, ProjectReadLimits limits) { token.ThrowIfCancellationRequested(); byte[] result = Files[relative]; limits.Validate(result); return result; }
    }

    [Fact]
    public void ReconstructionHidesTheVolumeOfModelsLoadedAsPickups()
    {
        // A database and a model the script loads twice: as a pickup template and as an ordinary object.
        const string Script = """
            set worldName world
            SetModelDirectory ..\data\m1\models
            SetTextureDirectory ..\data\m1\textures
            NewWorld %worldName%
            FindNode %worldName%
            GameGenSetWorld %worldName%
            FindNode %worldName%
            WorldOrigin 0.0 512.0
            WorldExtents 512.0 -512.0
            WorldPartition 256 -256
            LoadGameGen m1.flt m1.flt
            DeleteTree m1.flt
            LoadGameGen ammo.flt pu012
            LoadGameGen crate.flt crate1
            FindNode world
            AddChild crate1
            GameZWriteZBDFile ..\m1\gamez.zbd
            """;
        Dictionary<string, byte[]> files = new(StringComparer.Ordinal) { ["gamegen/m1.gs"] = Encoding.ASCII.GetBytes(Script) };
        void Model(string stem, params WorldNode[] roots)
        {
            var (json, bin) = WorldGltf.Export(roots, 0xFF, new() { Texture = _ => ("", 0) }).Write(stem + ".bin");
            files[$"data/m1/models/{stem}.gltf"] = json; files[$"data/m1/models/{stem}.bin"] = bin;
        }
        WorldMaterial violet = new() { Color = new(63, 15, 254), Flags = 0xFF };
        Model("m1", Node("ground"));
        Model("ammo", Node("shell"), Node("bvol", material: violet));
        Model("crate", Node("body"), Node("bvol", material: new() { Color = new(10, 200, 30), Flags = 0xFF }));
        var world = new WorldAssembler(new MemoryFiles(files), Token).Assemble("m1.gs");
        byte[] bytes = GameZWriter.Write(world, Token);
        var shipped = GameZWorldReader.FromDocument(FormatRegistry.Default.OpenBytes("gamez.zbd", bytes, token: Token), Token);
        var scripts = new Dictionary<string, IReadOnlyList<IReadOnlyList<string>>>(StringComparer.OrdinalIgnoreCase) { ["m1.gs"] = GameGenScriptText.Tokenize(Script) };
        List<string> notes = [];
        var outputs = WorldSources.Reconstruct([new(1, shipped)], n => scripts.GetValueOrDefault(n), (_, name) => $"data/m1/textures/{name}.png", new HashSet<string>(), _ => 0, notes, Token);
        Assert.Contains("~hidden", Encoding.UTF8.GetString(outputs.Single(o => o.Path.EndsWith("/ammo.gltf", StringComparison.Ordinal)).Bytes));
        Assert.DoesNotContain("~hidden", Encoding.UTF8.GetString(outputs.Single(o => o.Path.EndsWith("/crate.gltf", StringComparison.Ordinal)).Bytes));
    }
}
