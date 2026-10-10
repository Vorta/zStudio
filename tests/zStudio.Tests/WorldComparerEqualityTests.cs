using System.Buffers.Binary;
using System.Numerics;
using Recoil.Zbd.Core.Worlds;
using Xunit;

namespace Recoil.Zbd.Tests;

/// <summary>
/// What Compare worlds counts as the same: every value a model keeps that the game uses (not only its counts), for
/// copies of a name, the same fields a comparison reports rather than the raw class data with its stored pointers, and
/// never one node standing for two.
/// </summary>
public sealed class WorldComparerEqualityTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    /// <summary>
    /// A morphing, lit quad with per-corner normals and UVs on a textured material, a flat untextured triangle, and a lens
    /// flare entry with two points.
    /// </summary>
    private static WorldModel Model()
    {
        WorldModel model = new() { Flags = 0x3 };
        model.Vertices.AddRange([new(0, 0, 0), new(4, 0, 0), new(4, 0, 4), new(0, 0, 4), new(9, 1, 9)]);
        model.Normals.AddRange([new(0, 1, 0), new(0.6f, 0.8f, 0)]);
        model.Morphs.AddRange([new(0, 2, 0), new(0, 2, 0), new(0, 3, 0), new(0, 3, 0), Vector3.Zero]);
        WorldMaterial textured = new() { Texture = new("rock"), Flags = 0x1FF }, plain = new() { Color = new(200, 10, 10), PackedColor = 0, Flags = 0x0FF };
        model.Polygons.Add(new() { Vertices = [0, 1, 2, 3], Normals = [0, 0, 1, 1], Uvs = [new(0, 0), new(1, 0), new(1, 1), new(0, 1)], Material = textured });
        model.Polygons.Add(new() { Vertices = [0, 4, 1], Material = plain });
        byte[] record = new byte[76];
        for (int o = 0; o < 76; o += 4) BinaryPrimitives.WriteUInt32LittleEndian(record.AsSpan(o), (uint)(0x100 + o));
        BinaryPrimitives.WriteInt32LittleEndian(record.AsSpan(12), 2);
        model.Points.Add(new() { Record = record, Vertices = [new(1, 5, 1), new(2, 5, 2)] });
        model.BoundsCentre = new(4, 1, 4); model.BoundsRadius = 7;
        return model;
    }

    /// <summary>A copy sharing nothing with the model: its lists, points, polygons and materials are new objects.</summary>
    private static WorldModel Copy(WorldModel source)
    {
        WorldModel model = new()
        {
            Mode = source.Mode, Flags = source.Flags, MorphFactor = source.MorphFactor, ScrollU = source.ScrollU, ScrollV = source.ScrollV,
            ScrollFrame = source.ScrollFrame, BoundsCentre = source.BoundsCentre, BoundsRadius = source.BoundsRadius,
        };
        model.Vertices.AddRange(source.Vertices); model.Normals.AddRange(source.Normals); model.Morphs.AddRange(source.Morphs);
        foreach (var p in source.Points) model.Points.Add(new() { Record = (byte[])p.Record.Clone(), Vertices = (Vector3[])p.Vertices.Clone() });
        Dictionary<WorldMaterial, WorldMaterial> materials = new(ReferenceEqualityComparer.Instance);
        foreach (var p in source.Polygons)
        {
            var m = p.Material!;
            if (!materials.TryGetValue(m, out var copy))
                materials[m] = copy = new()
                {
                    Flags = m.Flags, PackedColor = m.PackedColor, Color = m.Color, Texture = m.Texture == null ? null : new(m.Texture.Name),
                    Field14 = m.Field14, Field18 = m.Field18, Field1C = m.Field1C, Soil = m.Soil,
                };
            model.Polygons.Add(new() { Flags = p.Flags, Priority = p.Priority, Zone = p.Zone, Material = copy, Vertices = (int[])p.Vertices.Clone(), Normals = (int[])p.Normals.Clone(), Uvs = (Vector2[])p.Uvs.Clone() });
        }
        return model;
    }

    /// <summary>A world with one object using the model.</summary>
    private static GameZWorld World(WorldModel model)
    {
        GameZWorld world = new();
        WorldNode root = new("world1", WorldNodeClass.World), rock = new("rock", WorldNodeClass.Object3D) { Flags = WorldGltf.DefaultCarried, Model = model };
        rock.SetPayloadInt(0, 0x28);
        root.Children.Add(rock); rock.Parents.Add(root);
        world.Nodes.AddRange([root, rock]); world.Models.Add(model);
        return world;
    }

    private static void SetPointWord(WorldModel model, int offset, uint value) => BinaryPrimitives.WriteUInt32LittleEndian(model.Points[0].Record.AsSpan(offset), value);

    public static TheoryData<string, string> RuntimeValues() => new()
    {
        // Same counts and the same polygon corners, materials and draw attributes; only one retained value differs.
        { "morph delta", "model.polygons" },
        { "normal vector", "model.polygons" },
        { "point entry word", "model.points" },
        { "point entry vertex", "model.points" },
        { "texture scrolling", "model.scroll" },
        { "scroll frame", "model.scroll" },
        { "morph factor", "model.morphFactor" },
        { "packed colour", "model.polygons" },
        { "material field 0x14", "model.polygons" },
        { "material field 0x18", "model.polygons" },
        { "material field 0x1C", "model.polygons" },
        { "pinned material", "model.polygons" },
        { "polygon flag", "model.polygons" },
    };

    [Theory]
    [MemberData(nameof(RuntimeValues))]
    public void ModelsThatDifferOnlyInARetainedValueAreChanged(string change, string field)
    {
        var retail = Model(); var rebuilt = Copy(retail);
        Action apply = change switch
        {
            "morph delta" => () => rebuilt.Morphs[2] = new(0, 5, 0),
            "normal vector" => () => rebuilt.Normals[1] = new(0, 0.8f, 0.6f),
            "point entry word" => () => SetPointWord(rebuilt, 0x1C, 0xBEEF),
            "point entry vertex" => () => rebuilt.Points[0].Vertices[1] = new(2, 6, 2),
            "texture scrolling" => () => rebuilt.ScrollV = 0.25f,
            "scroll frame" => () => rebuilt.ScrollFrame = 3,
            "morph factor" => () => rebuilt.MorphFactor = 0.5f,
            "packed colour" => () => rebuilt.Polygons[1].Material!.PackedColor = 0x7C00,
            "material field 0x14" => () => rebuilt.Polygons[0].Material!.Field14 = 1,
            "material field 0x18" => () => rebuilt.Polygons[0].Material!.Field18 = 0.25f,
            "material field 0x1C" => () => rebuilt.Polygons[0].Material!.Field1C = 0.75f,
            "pinned material" => () => rebuilt.Polygons[0].Material!.Flags |= 0x200,
            "polygon flag" => () => rebuilt.Polygons[1].Flags |= 0x1000,
            _ => throw new ArgumentException(change),
        };
        // Unchanged, the copy is the same.
        Assert.Empty(WorldComparer.CompareTree(World(retail), World(rebuilt), token: Token).Differences);
        apply();
        var comparison = WorldComparer.CompareTree(World(retail), World(rebuilt), token: Token);
        var rock = Assert.Single(comparison.Roots[0].Children);
        Assert.Equal(WorldComparisonStatus.Changed, rock.Status);
        var difference = Assert.Single(rock.Differences);
        Assert.Equal(field, difference.Field);
        Assert.NotEqual(difference.Expected, difference.Actual);
        // The flat list says the same.
        Assert.Equal(field, Assert.Single(WorldComparer.Compare(World(retail), World(rebuilt))).Field);
    }

    [Fact]
    public void DifferencesSayWhichCornerValueDiffers()
    {
        var retail = Model(); var rebuilt = Copy(retail);
        rebuilt.Morphs[2] = new(0, 5, 0); rebuilt.Normals[1] = new(0, 0.8f, 0.6f);
        var polygons = Assert.Single(Assert.Single(WorldComparer.CompareTree(World(retail), World(rebuilt), token: Token).Roots[0].Children).Differences);
        Assert.Equal("model.polygons", polygons.Field);
        Assert.Contains($"|n{new Vector3(0.6f, 0.8f, 0)}", polygons.Expected); Assert.Contains($"|m{new Vector3(0, 3, 0)}", polygons.Expected);
        Assert.Contains($"|n{new Vector3(0, 0.8f, 0.6f)}", polygons.Actual); Assert.Contains($"|m{new Vector3(0, 5, 0)}", polygons.Actual);
        Assert.Contains("(1 identical)", polygons.Actual);

        var points = Copy(retail); SetPointWord(points, 0x1C, 0xBEEF);
        var entry = Assert.Single(Assert.Single(WorldComparer.CompareTree(World(retail), World(points), token: Token).Roots[0].Children).Differences);
        Assert.StartsWith("1: entry 0 ", entry.Expected); Assert.Contains("0000011C", entry.Expected);
        Assert.StartsWith("1 (0 identical): entry 0 ", entry.Actual); Assert.Contains("0000BEEF", entry.Actual);
    }

    [Fact]
    public void WhatTheGameDoesNotReadOrRecomputesStaysTheSame()
    {
        var retail = Model(); var rebuilt = Copy(retail);
        // A point entry's vertex count (+12, from its points) and runtime fields: elapsed time and packed state (+16..+27),
        // packed colour and list pointer (+40..+47), the flare's runtime values (+60..+75). Reconstruction clears these.
        foreach (int offset in new[] { 12, 16, 20, 24, 40, 44, 60, 64, 68, 72 }) SetPointWord(rebuilt, offset, 0);
        // The pools in another order (vertices with their morph deltas, normals), as a rebuild deduplicates them.
        int[] vertexOrder = [4, 3, 2, 1, 0]; int[] normalOrder = [1, 0];
        rebuilt.Vertices.Clear(); rebuilt.Vertices.AddRange(vertexOrder.Select(i => retail.Vertices[i]));
        rebuilt.Morphs.Clear(); rebuilt.Morphs.AddRange(vertexOrder.Select(i => retail.Morphs[i]));
        rebuilt.Normals.Clear(); rebuilt.Normals.AddRange(normalOrder.Select(i => retail.Normals[i]));
        foreach (var p in rebuilt.Polygons)
        {
            p.Vertices = [.. p.Vertices.Select(v => Array.IndexOf(vertexOrder, v))];
            p.Normals = [.. p.Normals.Select(n => Array.IndexOf(normalOrder, n))];
        }
        // Material flags a world does not store: 0x100 follows the texture, 0x400 is cleared without one.
        rebuilt.Polygons[0].Material!.Flags &= 0xFEFF; rebuilt.Polygons[1].Material!.Flags |= 0x500;
        // Values within the rounding of positions, and a negative zero.
        rebuilt.Morphs[vertexOrder.Length - 1 - 3] += new Vector3(0, 0.0001f, 0);
        rebuilt.Points[0].Vertices[0] += new Vector3(0.0001f, 0, 0);
        rebuilt.MorphFactor = -0f;
        Assert.Empty(WorldComparer.CompareTree(World(retail), World(rebuilt), token: Token).Differences);
        // A model without morph deltas is a model whose deltas are zero (the count still differs).
        var still = Copy(retail); var none = Copy(retail);
        for (int i = 0; i < still.Morphs.Count; i++) still.Morphs[i] = Vector3.Zero;
        none.Morphs.Clear();
        Assert.Equal(["model.morphs"], WorldComparer.CompareTree(World(still), World(none), token: Token).Differences.Select(d => d.Field));
    }

    /// <summary>
    /// A world whose members are two lights named lamp, the second changed by <paramref name="change"/>; with
    /// <paramref name="swapSlots"/>, the first has the higher slot (the members keep their order).
    /// </summary>
    private static (GameZWorld World, WorldNode First, WorldNode Second) Lamps(bool swapSlots, Action<WorldNode>? change = null)
    {
        GameZWorld world = new();
        WorldNode root = new("world1", WorldNodeClass.World);
        WorldNode Lamp(uint pointer)
        {
            WorldNode lamp = new("lamp", WorldNodeClass.Light);
            lamp.SetPayloadFloat(0x10, 0.75f);
            // The attached worlds' count and the runtime pointer the engine keeps after it.
            lamp.SetPayloadInt(0xDC, 1); lamp.SetPayloadInt(0xE0, unchecked((int)pointer));
            lamp.AttachedWorlds.Add(root);
            root.Children.Add(lamp); lamp.Parents.Add(root);
            return lamp;
        }
        WorldNode first = Lamp(0x01A2B3C0), second = Lamp(0x01A2B7F0);
        change?.Invoke(second);
        world.Nodes.AddRange(swapSlots ? [root, second, first] : [root, first, second]);
        return (world, first, second);
    }

    [Fact]
    public void CopiesThatDifferOnlyInStoredPointersFindTheSameThing()
    {
        // Retail gives the second lamp the highest slot; the rebuilt world gives it to the first, whose class data differs
        // only in the pointer the engine keeps after the attached worlds (+0xE0), which no comparison counts.
        var (retail, _, _) = Lamps(false); var (rebuilt, first, second) = Lamps(true);
        var comparison = WorldComparer.CompareTree(retail, rebuilt, token: Token);
        Assert.Empty(comparison.Differences);
        var binding = Assert.Single(comparison.Bindings);
        Assert.Same(second, binding.Counterpart); Assert.Same(first, binding.Actual);
        Assert.True(binding.Interchangeable && binding.Same);
        Assert.DoesNotContain(comparison.Roots[0].Children, c => c.BindsElsewhere);
        Assert.True(WorldComparer.Interchangeable(first, second, token: Token));

        // The slots a camera stores for its nodes are skipped the same way: the nodes they name are compared.
        var (a, b) = Cameras(false);
        Assert.True(WorldComparer.Interchangeable(a, b, token: Token));
    }

    /// <summary>Two cameras named cam under the world, viewing through window1; the second through window2 when <paramref name="otherWindow"/>.</summary>
    private static (WorldNode First, WorldNode Second) Cameras(bool otherWindow)
    {
        GameZWorld world = new();
        WorldNode root = new("world1", WorldNodeClass.World), window = new("window1", WorldNodeClass.Window), other = new("window2", WorldNodeClass.Window);
        WorldNode Camera(WorldNode through, int slot)
        {
            WorldNode camera = new("cam", WorldNodeClass.Camera) { CameraWorld = root, CameraWindow = through };
            // The slots the file stores for them (another in each camera).
            camera.SetPayloadInt(0, 0); camera.SetPayloadInt(4, slot); camera.SetPayloadInt(8, slot * 7); camera.SetPayloadInt(12, -1);
            camera.SetPayloadFloat(0x20, 1.2f);
            root.Children.Add(camera); camera.Parents.Add(root);
            return camera;
        }
        WorldNode first = Camera(window, 1), last = Camera(otherWindow ? other : window, 2);
        world.Nodes.AddRange([root, window, other, first, last]);
        return (first, last);
    }

    [Fact]
    public void CopiesStillDifferInWhatComparisonsReport()
    {
        // Class data the comparison counts tells the lamps apart, and so do the nodes it names.
        var (_, first, second) = Lamps(true, lamp => lamp.SetPayloadFloat(0x10, 0.5f));
        Assert.False(WorldComparer.Interchangeable(first, second, token: Token));
        var (_, near, far) = Lamps(true, lamp => { lamp.AttachedWorlds.Clear(); lamp.AttachedWorlds.Add(new("world2", WorldNodeClass.World)); });
        Assert.False(WorldComparer.Interchangeable(near, far, token: Token));
        var (a, b) = Cameras(true);
        Assert.False(WorldComparer.Interchangeable(a, b, token: Token));

        // The rebuilt binding then finds another node, and the comparison says what differs.
        var (retail, _, _) = Lamps(false, lamp => lamp.SetPayloadFloat(0x10, 0.5f));
        var (rebuilt, _, _) = Lamps(true, lamp => lamp.SetPayloadFloat(0x10, 0.5f));
        var comparison = WorldComparer.CompareTree(retail, rebuilt, token: Token);
        Assert.False(Assert.Single(comparison.Bindings).Same);
        Assert.Empty(comparison.Differences);

        // Nodes the class data names that only their slots or counts gave before: a camera's XZ horizon, a light's worlds.
        GameZWorld Horizon(string name)
        {
            GameZWorld world = new(); WorldNode root = new("world1", WorldNodeClass.World), horizon = new(name, WorldNodeClass.Object3D);
            WorldNode camera = new("cam", WorldNodeClass.Camera) { CameraWorld = root, CameraHorizonXZ = horizon };
            world.Nodes.AddRange([root, camera, horizon]);
            return world;
        }
        var horizon = Assert.Single(WorldComparer.CompareTree(Horizon("sky"), Horizon("sky2"), token: Token).Differences, d => d.Field.StartsWith("camera", StringComparison.Ordinal));
        Assert.Equal(("camera.horizonXZ", "sky", "sky2"), (horizon.Field, horizon.Expected, horizon.Actual));
        var (lamps, _, _) = Lamps(false);
        var (moved, _, _) = Lamps(false, lamp => { lamp.AttachedWorlds.Clear(); lamp.AttachedWorlds.Add(new("world2", WorldNodeClass.World)); });
        var worlds = Assert.Single(WorldComparer.CompareTree(lamps, moved, token: Token).Differences);
        Assert.Equal(("light.worlds", "world1", "world2"), (worlds.Field, worlds.Expected, worlds.Actual));
    }

    /// <summary>
    /// world1 holding p1 and p2, each holding an object x: with <paramref name="shared"/> one x both hold, otherwise a copy
    /// each; with <paramref name="oneParent"/>, p1 alone holds both (the shared x listed twice).
    /// </summary>
    private static GameZWorld Places(bool shared, bool oneParent = false)
    {
        GameZWorld world = new();
        WorldNode Add(WorldNode node) { world.Nodes.Add(node); return node; }
        WorldNode Object(string name) { WorldNode node = new(name, WorldNodeClass.Object3D) { Flags = WorldGltf.DefaultCarried }; node.SetPayloadInt(0, 0x28); return Add(node); }
        static void Link(WorldNode parent, WorldNode child) { parent.Children.Add(child); child.Parents.Add(parent); }
        var root = Add(new("world1", WorldNodeClass.World));
        var p1 = Object("p1"); Link(root, p1);
        var p2 = p1;
        if (!oneParent) { p2 = Object("p2"); Link(root, p2); }
        var x = Object("x"); Link(p1, x);
        Link(p2, shared ? x : Object("x"));
        return world;
    }

    [Fact]
    public void OneNodeWhereTheOtherWorldHasTwoIsNotTheSame()
    {
        // Separate copies against one shared node, the reverse, and two copies in one parent against one node listed twice:
        // whichever world shares, both places of the node differ in their counterparts and the parent of the second in its
        // children, so counts agree with the rows.
        foreach (bool oneParent in new[] { false, true })
            foreach (bool retailShares in new[] { false, true })
            {
                var comparison = WorldComparer.CompareTree(Places(retailShares, oneParent), Places(!retailShares, oneParent), token: Token);
                var world = Assert.Single(comparison.Roots);
                var places = world.Children.SelectMany(p => p.Children).ToList();
                Assert.Equal(2, places.Count);
                // Each of the two nodes has a place of its own, against the one node.
                Assert.NotSame(retailShares ? places[0].Actual : places[0].Expected, retailShares ? places[1].Actual : places[1].Expected);
                foreach (var place in places)
                {
                    Assert.Equal(WorldComparisonStatus.Changed, place.Status);
                    var counterparts = Assert.Single(place.Differences);
                    Assert.Equal("counterparts", counterparts.Field);
                    // The separate copies on one side, the one node on the other.
                    Assert.Equal((retailShares ? 1 : 2, retailShares ? 2 : 1), (counterparts.Expected.Split(',').Length, counterparts.Actual.Split(',').Length));
                }
                var second = world.Children[^1];
                Assert.Equal(WorldComparisonStatus.Changed, second.Status);
                Assert.Equal("children", Assert.Single(second.Differences).Field);
                Assert.Equal(new[] { $"{second.Path} children", $"{places[0].Path} counterparts", $"{places[1].Path} counterparts" }.Order(StringComparer.Ordinal),
                    comparison.Differences.Select(d => $"{d.Path} {d.Field}").Order(StringComparer.Ordinal));
                Assert.Equal(3, comparison.DifferenceCount);
                Assert.Equal((oneParent ? 1 : 2, 3, 0, 0), (comparison.Counts[WorldComparisonStatus.Same], comparison.Counts[WorldComparisonStatus.Changed],
                    comparison.Counts[WorldComparisonStatus.OnlyExpected], comparison.Counts[WorldComparisonStatus.OnlyActual]));
            }
        // Each world against itself is the same.
        foreach (bool shared in new[] { false, true })
            Assert.Empty(WorldComparer.CompareTree(Places(shared), Places(shared), token: Token).Differences);
    }
}
