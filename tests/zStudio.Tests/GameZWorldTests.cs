using System.Buffers.Binary;
using System.Numerics;
using Recoil.Zbd.Core;
using Recoil.Zbd.Core.Formats;
using Recoil.Zbd.Core.Worlds;
using Xunit;

namespace Recoil.Zbd.Tests;

public sealed class GameZWorldTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    /// <summary>A world built from nothing: world, camera, a partitioned object with a textured model, a child group and a light.</summary>
    private static GameZWorld SmallWorld()
    {
        GameZWorld world = new();
        WorldTexture rock = new("rock"); world.Textures.Add(rock);
        WorldMaterial textured = new() { Texture = rock, Soil = 1 }, plain = new() { Color = new(10, 20, 30) };
        world.Materials.Add(textured); world.Materials.Add(plain);
        WorldModel box = new();
        box.Vertices.AddRange([new(0, 0, 0), new(10, 0, 0), new(10, 5, 0), new(0, 5, -10)]);
        box.Polygons.Add(new() { Material = textured, Vertices = [0, 1, 2, 3], Uvs = [new(0, 0), new(1, 0), new(1, 1), new(0, 1)], Priority = 2 });
        box.Polygons.Add(new() { Material = plain, Vertices = [0, 2, 3] });
        world.Models.Add(box);

        WorldNode root = new("world1", WorldNodeClass.World); world.Nodes.Add(root);
        root.SetPayloadFloat(0x34, 0); root.SetPayloadFloat(0x38, 512); root.SetPayloadFloat(0x3C, 512); root.SetPayloadFloat(0x40, -512);
        root.SetPayloadFloat(0x44, 512); root.SetPayloadFloat(0x48, 0);
        WorldUpdate.SetPartition(root, 256, -256); root.SetPayloadFloat(0x70, 3); root.SetPayloadFloat(0x74, 3);
        WorldNode camera = new("camera1", WorldNodeClass.Camera) { CameraWorld = root }; world.Nodes.Add(camera);
        WorldNode building = new("building", WorldNodeClass.Object3D) { Model = box, Flags = 0x0308001C }; world.Nodes.Add(building);
        building.SetPayloadInt(0, 0x30);
        float[] matrix = [1, 0, 0, 0, 1, 0, 0, 0, 1, 100, 0, 400];
        for (int i = 0; i < 12; i++) building.SetPayloadFloat(0x30 + i * 4, matrix[i]);
        WorldNode group = new("group", WorldNodeClass.Object3D) { Flags = 0x0308001C }; world.Nodes.Add(group);
        group.SetPayloadInt(0, 0x28);
        WorldNode part = new("part", WorldNodeClass.Object3D) { Model = box, Flags = 0x0308001C }; world.Nodes.Add(part);
        part.SetPayloadInt(0, 0x28);
        group.Children.Add(part); part.Parents.Add(group);
        WorldNode light = new("sunlight", WorldNodeClass.Light) { Flags = 0x0108011C }; world.Nodes.Add(light);
        root.WorldLights.Add(light); light.AttachedWorlds.Add(root);
        WorldUpdate.RebuildBounds(world);
        WorldUpdate.Partition(root, [building, group]);
        return world;
    }

    [Fact]
    public void AWorldBuiltFromNothingReadsBack()
    {
        var world = SmallWorld();
        byte[] bytes = GameZWriter.Write(world, Token);
        var doc = FormatRegistry.Default.OpenBytes("gamez.zbd", bytes, token: Token);
        Assert.DoesNotContain(doc.Diagnostics, d => d.Severity == "Error");
        var scene = doc.Scene!;
        Assert.Equal(["world1", "camera1", "building", "group", "part", "sunlight"], scene.Nodes.Select(n => n.Name));
        Assert.Equal("rock", scene.Textures[0].Text("name"));
        Assert.Equal(2, scene.Models[0].Metadata.Int("parent_count"));
        Assert.Equal(new Vector2(1, 1), scene.Models[0].Polygons[0].Uvs[2]);
        Assert.Empty(scene.Models[0].Polygons[1].Uvs);
        // A 2 × 2 grid: the building sits in the first cell, the group reaches past the world's edge and stays in its child list.
        var root = scene.Nodes[0];
        Assert.Equal(4, (root.Data["partitions"] as System.Text.Json.Nodes.JsonArray)!.SelectMany(r => (r as System.Text.Json.Nodes.JsonArray)!).Count());
        var reread = GameZWorldReader.FromDocument(doc, Token);
        Assert.Equal((0, 0), (reread.Nodes[2].GridColumn, reread.Nodes[2].GridRow));
        Assert.Equal(["group"], reread.Nodes[0].Children.Select(n => n.Name));
        Assert.Equal([0], scene.Nodes[2].Parents);
        Assert.Equal([3], scene.Nodes[4].Parents);
        // Reading back through the world model rewrites the same file.
        Assert.Equal(bytes, GameZWriter.Write(GameZWorldReader.FromDocument(doc, Token), Token));
        // Free lists: the header's free head is the first unused slot; unused slots chain to 0x00FFFFFF.
        Assert.Equal(6, BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(28)));
        int nodeTable = BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(32));
        Assert.Equal(7, BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(nodeTable + 6 * 196 + 192)));
        Assert.Equal(0x00FFFFFF, BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(nodeTable + 15999 * 196 + 192)));
    }

    [Fact]
    public void UnnamedNodesStayLiveAndNamesKeepTheEngineResidue()
    {
        var world = SmallWorld();
        // gwNodeNew's default name stays behind a shorter name, as in the shipped worlds.
        Assert.Equal("world1\0_node_name"u8.ToArray(), world.Nodes[0].NameField[..17]);
        var part = world.Nodes.Single(n => n.Name == "part");
        part.Name = new string('x', 40);
        Assert.Equal(new string('x', 34), part.Name);
        // An empty name is still a live node, not the start of the free slots.
        part.Name = "";
        Assert.NotEqual(0, part.NameField[1]);
        byte[] bytes = GameZWriter.Write(world, Token);
        var reread = GameZWorldReader.FromDocument(FormatRegistry.Default.OpenBytes("gamez.zbd", bytes, token: Token), Token);
        Assert.Equal(["world1", "camera1", "building", "group", "", "sunlight"], reread.Nodes.Select(n => n.Name));
        Assert.Equal("group", reread.Nodes[4].Parents.Single().Name);
    }

    [Fact]
    public void DerivedValuesFollowTheEngine()
    {
        var world = SmallWorld();
        var model = world.Models[0];
        // zDi::RebuildBounds: centre = min + half extent; radius halves the exponent of the squared half extents.
        Assert.Equal(new Vector3(5, 2.5f, -5), model.BoundsCentre);
        var (_, radius) = WorldUpdate.Sphere(new(new(0, 0, -10), new(10, 5, 0)));
        Assert.Equal(radius, model.BoundsRadius);
        Assert.InRange(radius, 7.0f, 8.0f); // √56.25 = 7.5, approximately
        var building = world.Nodes[2]; var group = world.Nodes[3];
        Assert.Equal(0x0308031Cu, building.Flags & 0x0308071Cu);
        Assert.Equal(new WorldBox(new(0, 0, -10), new(10, 5, 0)), building.CachedBounds);
        // A group's child box is the union of its children's boxes in its own space.
        Assert.Equal(WorldUpdate.ChildBoundsFlag | WorldUpdate.CachedBoundsFlag, group.Flags & 0x700);
        Assert.Equal((0, 0), (building.GridColumn, building.GridRow)); // x 100..110, z 390..400: rows count down from z 512
        Assert.Equal([building], world.Nodes[0].Areas[0].Nodes);
        Assert.Equal((-1, -1), (group.GridColumn, group.GridRow));
        // Outside the world (beyond the tolerance) nodes overflow into the world's own list.
        var (col, row) = WorldUpdate.GridIndex(world.Nodes[0], -10, 5, 10, 20);
        Assert.Equal((-1, -1), (col, row));
        Assert.Equal((1, 1), WorldUpdate.GridIndex(world.Nodes[0], 300, 310, 10, 20));
        // Overhanging a cell by more than the tolerance also overflows.
        Assert.Equal((-1, -1), WorldUpdate.GridIndex(world.Nodes[0], 250, 262, 10, 20));
        Assert.Equal((1, 1), WorldUpdate.GridIndex(world.Nodes[0], 254, 262, 10, 20));
    }

    [Fact]
    public void WritingRejectsWorldsTheEngineCannotLoad()
    {
        var world = SmallWorld();
        world.Models[0].Polygons[1].Uvs = [new(0, 0), new(1, 0), new(1, 1)];
        Assert.Throws<InvalidDataException>(() => GameZWriter.Write(world, Token));
        world = SmallWorld(); world.Models[0].Polygons[0].Vertices = [0, 1, 9, 3];
        Assert.Throws<InvalidDataException>(() => GameZWriter.Write(world, Token));
        world = SmallWorld(); world.Textures[0].Name = "a_texture_name_longer_than_19";
        Assert.Throws<InvalidDataException>(() => GameZWriter.Write(world, Token));
        world = SmallWorld(); world.NodeCapacity = 3;
        Assert.Throws<InvalidDataException>(() => GameZWriter.Write(world, Token));
        world = SmallWorld(); world.Nodes[3].Children.Add(new("stray", WorldNodeClass.Object3D));
        Assert.Throws<InvalidDataException>(() => GameZWriter.Write(world, Token));
    }

    [Fact]
    public void WorldsWhoseLinksLoopOrRunTooDeepAreRefusedOnReading()
    {
        // A file's parent and child lists can describe any graph. Reconstruction walks the hierarchy recursively, so a
        // cycle (here a group that is its part's child) or an extreme depth is refused when the world is read.
        var world = SmallWorld();
        WorldNode group = world.Nodes.Single(n => n.Name == "group"), part = world.Nodes.Single(n => n.Name == "part");
        part.Children.Add(group); group.Parents.Add(part);
        byte[] cyclic = GameZWriter.Write(world, Token);
        var error = Assert.Throws<InvalidDataException>(() => GameZWorldReader.FromDocument(FormatRegistry.Default.OpenBytes("gamez.zbd", cyclic, token: Token), Token));
        Assert.Contains("own ancestor", error.Message);

        world = SmallWorld(); var parent = world.Nodes.Single(n => n.Name == "part");
        for (int i = 0; i < WorldUpdate.MaximumDepth + 10; i++)
        {
            WorldNode child = new($"link{i}", WorldNodeClass.Object3D) { Flags = 0x0308001C }; child.SetPayloadInt(0, 0x28);
            world.Nodes.Add(child); parent.Children.Add(child); child.Parents.Add(parent); parent = child;
        }
        byte[] deep = GameZWriter.Write(world, Token);
        error = Assert.Throws<InvalidDataException>(() => GameZWorldReader.FromDocument(FormatRegistry.Default.OpenBytes("gamez.zbd", deep, token: Token), Token));
        Assert.Contains("deeper", error.Message);

        // The reader only warns about a corner naming a missing vertex; the world model refuses it.
        byte[] bytes = GameZWriter.Write(SmallWorld(), Token);
        int models = BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(20)), data = BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(models + 12 + 84));
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(data + 4 * 12 + 2 * 28), 9);
        var document = FormatRegistry.Default.OpenBytes("gamez.zbd", bytes, token: Token);
        Assert.DoesNotContain(document.Diagnostics, d => d.Severity == "Error");
        error = Assert.Throws<InvalidDataException>(() => GameZWorldReader.FromDocument(document, Token));
        Assert.Contains("missing vertex", error.Message);
    }

    // Runtime pointers the engine replaces on load; retail files hold stale heap addresses there.
    private static readonly int[] NodePointers = [56, 57, 58, 59, 88, 89, 90, 91, 96, 97, 98, 99];

    [Fact]
    public void RetailWorldsRewriteAndRecomputeLikeTheEngine()
    {
        string? corpus = Environment.GetEnvironmentVariable("ZSTUDIO_CORPUS"); if (string.IsNullOrEmpty(corpus)) return;
        foreach (string path in Directory.GetFiles(corpus, "gamez.zbd", SearchOption.AllDirectories).Order())
        {
            byte[] original = File.ReadAllBytes(path);
            var doc = FormatRegistry.Default.OpenBytes(path, original, token: Token);
            var world = GameZWorldReader.FromDocument(doc, Token);
            byte[] written = GameZWriter.Write(world, Token);
            Assert.Equal(original.Length, written.Length);
            var masked = PointerMask(doc, original);
            for (int o = 0; o < original.Length; o++)
                if (original[o] != written[o] && !masked[o]) Assert.Fail($"{path}: byte {o} differs ({original[o]:X2} → {written[o]:X2}).");

            // Recomputing every derived value reproduces the stored ones (bounds to float rounding, cells exactly).
            var stored = GameZWorldReader.FromDocument(doc, Token);
            WorldUpdate.RebuildBounds(world);
            for (int i = 0; i < world.Models.Count; i++)
                Assert.True(world.Models[i].BoundsCentre == stored.Models[i].BoundsCentre && world.Models[i].BoundsRadius == stored.Models[i].BoundsRadius, $"{path}: model {i} sphere");
            for (int i = 0; i < world.Nodes.Count; i++)
            {
                Assert.Equal(stored.Nodes[i].Flags, world.Nodes[i].Flags);
                Assert.True(Close(stored.Nodes[i].CachedBounds, world.Nodes[i].CachedBounds), $"{path}: {stored.Nodes[i].Name} bounds");
            }
            var root = world.Nodes.First(n => n.Class == WorldNodeClass.World); var storedRoot = stored.Nodes.First(n => n.Class == WorldNodeClass.World);
            WorldUpdate.Partition(root, root.Children.Concat(root.Areas.SelectMany(a => a.Nodes)).ToList());
            for (int k = 0; k < root.Areas.Count; k++)
            {
                Assert.Equal(storedRoot.Areas[k].Nodes.Select(n => n.Name).Order(), root.Areas[k].Nodes.Select(n => n.Name).Order());
                Assert.Equal(storedRoot.Areas[k].Record.AsSpan(0, 56).ToArray(), root.Areas[k].Record.AsSpan(0, 56).ToArray());
            }
            Assert.Equal(storedRoot.Children.Select(n => n.Name).Order(), root.Children.Select(n => n.Name).Order());
        }
        static bool Close(WorldBox a, WorldBox b) => Vector3.Distance(a.Min, b.Min) <= 1e-4f * (1 + a.Min.Length()) && Vector3.Distance(a.Max, b.Max) <= 1e-4f * (1 + a.Max.Length());
    }

    /// <summary>Bytes holding stale runtime pointers: node data/list pointers, model and polygon arrays, point vertices, light, world and area lists.</summary>
    private static bool[] PointerMask(ZbdDocument doc, byte[] bytes)
    {
        bool[] mask = new bool[bytes.Length]; var l = doc.GameZLayout!;
        void Mark(long offset, int length) { for (int i = 0; i < length; i++) mask[offset + i] = true; }
        for (int s = 0; s < l.NodeCapacity; s++) foreach (int o in NodePointers) mask[l.NodeOffset + s * 196 + o] = true;
        int modelTable = l.ModelOffset + 12;
        foreach (var model in doc.Scene!.Models)
        {
            Mark(modelTable + model.Index * 88 + 48, 20);
            long start = doc.Assets.First(a => a.Kind == AssetKind.Model && a.Index == model.Index).Offset + 12L * (model.Vertices.Length + model.Normals.Length + model.Morphs.Length);
            int points = BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(modelTable + model.Index * 88 + 28)); long pointVertices = 0;
            for (int p = 0; p < points; p++) { Mark(start + 76L * p + 24, 4); Mark(start + 76L * p + 44, 4); pointVertices += 12L * BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan((int)(start + 76L * p + 12))); }
            long polygons = start + 76L * points + pointVertices;
            for (int p = 0; p < model.Polygons.Length; p++) Mark(polygons + 28L * p + 8, 12);
        }
        foreach (var node in doc.Scene.Nodes.Where(n => n.Class == "light")) Mark(l.NodeDataOffsets[node.Index] + 0xE0, 4);
        foreach (var node in doc.Scene.Nodes.Where(n => n.Class == "world"))
        {
            long data = l.NodeDataOffsets[node.Index];
            foreach (int o in new[] { 12, 128, 148, 152, 160, 164 }) Mark(data + o, 4);
            long area = data + 172 + 4L * (node.Data.Int("light_count") + node.Data.Int("sound_count"));
            foreach (var row in node.Data["partitions"] as System.Text.Json.Nodes.JsonArray ?? [])
                foreach (var cell in row as System.Text.Json.Nodes.JsonArray ?? []) { Mark(area + 60, 4); area += 64 + 4L * cell!.Int("node_count"); }
        }
        return mask;
    }
}
