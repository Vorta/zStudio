using System.Buffers.Binary;
using System.Numerics;
using System.Text.Json.Nodes;
using Recoil.Zbd.Core;
using Recoil.Zbd.Core.Export;
using Recoil.Zbd.Core.Formats;
using Recoil.Zbd.Core.Worlds;
using Xunit;

namespace Recoil.Zbd.Tests;

/// <summary>Version-13 worlds (the 1998 demos): read, read-only, into the same model as the releases' version 15.</summary>
public sealed class GameZVersion13Tests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    /// <summary>
    /// A world with what version 13 stores differently: a rotated, moved object with a child (its corners are not an
    /// axis-aligned box), an object at the origin, a group, an LOD, a light, a camera and a freed slot.
    /// </summary>
    internal static GameZWorld SampleWorld()
    {
        GameZWorld world = new();
        WorldTexture rock = new("rock"); world.Textures.Add(rock);
        WorldMaterial textured = new() { Texture = rock, Soil = 1 }; world.Materials.Add(textured);
        WorldModel box = new();
        box.Vertices.AddRange([new(-2, 0, -1), new(2, 0, -1), new(2, 3, 1), new(-2, 3, 1)]);
        box.Polygons.Add(new() { Material = textured, Vertices = [0, 1, 2, 3], Uvs = [new(0, 0), new(1, 0), new(1, 1), new(0, 1)] });
        world.Models.Add(box);

        WorldNode root = new("world1", WorldNodeClass.World); world.Nodes.Add(root);
        root.SetPayloadFloat(0x34, 0); root.SetPayloadFloat(0x38, 512); root.SetPayloadFloat(0x3C, 512); root.SetPayloadFloat(0x40, -512);
        root.SetPayloadFloat(0x44, 512); root.SetPayloadFloat(0x48, 0);
        WorldUpdate.SetPartition(root, 256, -256); root.SetPayloadFloat(0x70, 3); root.SetPayloadFloat(0x74, 3);
        WorldNode camera = new("camera1", WorldNodeClass.Camera) { CameraWorld = root }; world.Nodes.Add(camera);
        world.FreedSlots[2] = FreedSlot("oldcrate");
        world.FreeHead = 2;
        WorldNode tower = Object("tower", box, Matrix4x4.CreateRotationY(0.7f) * Matrix4x4.CreateRotationX(0.2f) * Matrix4x4.CreateTranslation(100, 5, 400)); world.Nodes.Add(tower);
        WorldNode flag = Object("flag", box, Matrix4x4.CreateRotationZ(0.3f) * Matrix4x4.CreateTranslation(0, 6, 0)); world.Nodes.Add(flag);
        tower.Children.Add(flag); flag.Parents.Add(tower);
        WorldNode crate = Object("crate", box, null); world.Nodes.Add(crate);
        WorldNode group = Object("group", null, null); world.Nodes.Add(group);
        WorldNode lod = new("lod1", WorldNodeClass.Lod) { Flags = 0x0308001C }; world.Nodes.Add(lod);
        WorldNode near = Object("near", box, Matrix4x4.CreateTranslation(3, 0, 0)); world.Nodes.Add(near);
        group.Children.Add(lod); lod.Parents.Add(group); lod.Children.Add(near); near.Parents.Add(lod);
        WorldNode light = new("sunlight", WorldNodeClass.Light) { Flags = 0x0108011C }; world.Nodes.Add(light);
        root.WorldLights.Add(light); light.AttachedWorlds.Add(root);
        WorldUpdate.RebuildBounds(world);
        WorldUpdate.Partition(root, [tower, crate, group]);
        return world;

        static WorldNode Object(string name, WorldModel? model, Matrix4x4? matrix)
        {
            WorldNode node = new(name, WorldNodeClass.Object3D) { Model = model, Flags = 0x0308001C };
            if (matrix is not { } m) { node.SetPayloadInt(0, 0x28); return node; }
            node.SetPayloadInt(0, 0x30);
            float[] rows = [m.M11, m.M12, m.M13, m.M21, m.M22, m.M23, m.M31, m.M32, m.M33, m.M41, m.M42, m.M43];
            for (int i = 0; i < 12; i++) node.SetPayloadFloat(0x30 + i * 4, rows[i]);
            return node;
        }
        static byte[] FreedSlot(string name)
        {
            byte[] slot = new byte[196];
            System.Text.Encoding.ASCII.GetBytes(name).CopyTo(slot, 0);
            BinaryPrimitives.WriteInt32LittleEndian(slot.AsSpan(60), -1);
            BinaryPrimitives.WriteUInt32LittleEndian(slot.AsSpan(192), 0x00FFFFFF);
            return slot;
        }
    }

    [Fact]
    public void ADemoWorldReadsIntoTheReleasesModel()
    {
        byte[] v15 = GameZWriter.Write(SampleWorld(), Token), v13 = DemoWorldFixture.FromVersion15(v15);
        var probe = FormatRegistry.Probe(v13, [], v13.Length, ".zbd");
        Assert.Equal((FormatFamily.GameZ, 13u, Recognition.Supported), (probe.Family, probe.Version!.Value, probe.Recognition));
        Assert.Contains("1998 demo, read-only", probe.Description);
        var doc13 = FormatRegistry.Default.OpenBytes("gamez.zbd", v13, token: Token); var doc15 = FormatRegistry.Default.OpenBytes("gamez.zbd", v15, token: Token);
        Assert.Empty(doc13.Diagnostics);
        Assert.Equal(doc15.Scene!.Nodes.Select(n => (n.Name, n.Class)), doc13.Scene!.Nodes.Select(n => (n.Name, n.Class)));
        Assert.Contains(doc13.Assets, a => a.Kind == AssetKind.World);

        // The decoded fields show what version 13 stores: the corners of the node's box in the parent's space, and the translation.
        var tower = doc13.Scene.Nodes.Single(n => n.Name == "tower");
        Assert.Equal(24, (tower.Metadata["node_corners"] as JsonArray)!.Count);
        Assert.Null(tower.Metadata["node_bbox"]);
        Assert.Equal((100f, 5f, 400f), (tower.Data["translate"].Float("x"), tower.Data["translate"].Float("y"), tower.Data["translate"].Float("z")));
        Assert.Equal(12, (tower.Data["transform"] as JsonArray)!.Count);
        Assert.Equal(SceneBuilder.LocalTransform(doc15.Scene.Nodes.Single(n => n.Name == "tower")), SceneBuilder.LocalTransform(tower));

        var world15 = GameZWorldReader.FromDocument(doc15, Token); var world13 = GameZWorldReader.FromDocument(doc13, Token);
        Assert.Equal((15u, 13u), (world15.SourceVersion, world13.SourceVersion));
        Assert.Empty(WorldComparer.CompareTree(world15, world13, token: Token).Differences);
        for (int i = 0; i < world15.Nodes.Count; i++)
        {
            WorldNode a = world15.Nodes[i], b = world13.Nodes[i];
            Assert.Equal(a.Payload, b.Payload);
            Assert.Equal((a.CachedBounds, a.PrimaryBounds, a.SecondaryBounds), (b.CachedBounds, b.PrimaryBounds, b.SecondaryBounds));
        }
        Assert.Equal(world15.FreedSlots[2], world13.FreedSlots[2]);
        // Written again, a demo world is the releases' version-15 file it came from.
        Assert.Equal(v15, GameZWriter.Write(world13, Token));
    }

    [Fact]
    public void CornersTheBoxesDoNotGiveAreMappedBackThroughTheMatrix()
    {
        var sample = SampleWorld();
        byte[] v15 = GameZWriter.Write(sample, Token), v13 = DemoWorldFixture.FromVersion15(v15);
        var original = sample.Nodes.Single(n => n.Name == "tower").CachedBounds;
        // Without its bounds flags the tower's stored boxes no longer explain its corners.
        int slot = GameZWriter.NodeSlots(sample, TestContext.Current.CancellationToken)[sample.Nodes.Single(n => n.Name == "tower")], nodeTable = BinaryPrimitives.ReadInt32LittleEndian(v13.AsSpan(32));
        uint flags = BinaryPrimitives.ReadUInt32LittleEndian(v13.AsSpan(nodeTable + slot * 268 + 36));
        BinaryPrimitives.WriteUInt32LittleEndian(v13.AsSpan(nodeTable + slot * 268 + 36), flags & ~(WorldUpdate.ModelBoundsFlag | WorldUpdate.ChildBoundsFlag));
        var tower = GameZWorldReader.FromDocument(FormatRegistry.Default.OpenBytes("gamez.zbd", v13, token: Token), Token).Nodes.Single(n => n.Name == "tower");
        Assert.True(Vector3.Distance(original.Min, tower.CachedBounds.Min) < 1e-3f && Vector3.Distance(original.Max, tower.CachedBounds.Max) < 1e-3f, $"{original} vs {tower.CachedBounds}");
        Assert.NotEqual(original, WorldBox.Of(WorldUpdate.ParentCorners(tower)));
    }

    [Fact]
    public void DemoWorldsStayReadOnlyAndMalformedOnesAreRefused()
    {
        byte[] v13 = DemoWorldFixture.FromVersion15(GameZWriter.Write(SampleWorld(), Token));
        var doc = FormatRegistry.Default.OpenBytes("gamez.zbd", v13, token: Token);
        ImportedMesh triangle = new([new(0, 0, 0), new(1, 0, 0), new(0, 1, 0)], [Vector3.UnitZ, Vector3.UnitZ, Vector3.UnitZ], [new(0, 0), new(1, 0), new(0, 1)], [0, 1, 2]);
        var refused = Assert.Throws<InvalidDataException>(() => ModelReplacementWriter.Replace(doc, new Dictionary<int, ImportedMesh> { [0] = triangle }, "rock", Token));
        Assert.Contains("v15 or v27", refused.Message);

        // Cut inside the node records: the reader reports it and the world reader refuses the file.
        int nodeTable = BinaryPrimitives.ReadInt32LittleEndian(v13.AsSpan(32));
        byte[] cut = v13[..(nodeTable + 16000 * 268 + 100)];
        var truncated = FormatRegistry.Default.OpenBytes("gamez.zbd", cut, token: Token);
        Assert.Contains(truncated.Diagnostics, d => d.Severity == "Error");
        Assert.Throws<InvalidDataException>(() => GameZWorldReader.FromDocument(truncated, Token));
        // Version 14 was never seen and stays unsupported.
        byte[] other = (byte[])v13.Clone(); BinaryPrimitives.WriteUInt32LittleEndian(other.AsSpan(4), 14);
        var probe = FormatRegistry.Probe(other, [], other.Length, ".zbd");
        Assert.Equal(Recognition.UnsupportedVersion, probe.Recognition);
        Assert.Contains("13, 15 or 27", probe.Description);
    }

    /// <summary>
    /// Every version-13 world of a 1998 demo (ZSTUDIO_DEMO_CORPUS: a demo's zbd folder) reads without diagnostics, and every
    /// node's box from the reader gives back the eight corners the file stores.
    /// </summary>
    [Fact]
    public void DemoCorpusWorldsGiveBackTheirStoredCorners()
    {
        string? root = Environment.GetEnvironmentVariable("ZSTUDIO_DEMO_CORPUS"); if (root == null) return;
        var files = Directory.GetDirectories(root, "m*").Select(d => Path.Combine(d, "gamez.zbd")).Where(File.Exists).ToList();
        Assert.NotEmpty(files);
        int read = 0;
        foreach (var path in files)
        {
            var doc = FormatRegistry.Default.OpenBytes(path, File.ReadAllBytes(path), token: Token);
            if (doc.Probe.Version != 13) continue;
            Assert.Empty(doc.Diagnostics);
            var world = GameZWorldReader.FromDocument(doc, Token); read++;
            var live = doc.Scene!.Nodes.Where(n => n.Class != "none").ToList();
            Assert.Equal(live.Count, world.Nodes.Count);
            for (int i = 0; i < live.Count; i++)
            {
                float[] stored = [.. (live[i].Metadata["node_corners"] as JsonArray)!.Select(v => (float)v!.GetValue<double>())];
                var corners = Enumerable.Range(0, 8).Select(k => new Vector3(stored[k * 3], stored[k * 3 + 1], stored[k * 3 + 2])).ToArray();
                if (corners.All(c => c == Vector3.Zero)) continue;
                var derived = WorldUpdate.ParentCorners(world.Nodes[i]).ToArray();
                Assert.True(corners.All(c => derived.Any(d => Vector3.Distance(c, d) <= 1e-3f * (1 + c.Length()))), $"{path}: {live[i].Name}");
            }
        }
        Assert.True(read > 0, "The folder has no version-13 world.");
    }
}
