using Recoil.Zbd.Core.Worlds;
using Xunit;

namespace Recoil.Zbd.Tests;

/// <summary>The zone probes on malformed worlds (bounded, no crash) and the parts of the engine's walk the plain cases miss.</summary>
public sealed class ZoneProbeBoundsTests
{
    private const uint Surface = 0x1C;

    /// <summary>
    /// A 2 × 2-cell world (x 0..512, z 0..512) with <paramref name="cells"/> partitioned and <paramref name="listed"/> in
    /// the world's own list; bounds as the update computes them (cameras and lights keep theirs).
    /// </summary>
    private static WorldNode World(WorldNode[] cells, params WorldNode[] listed)
    {
        GameZWorld world = new();
        WorldNode root = new("world", WorldNodeClass.World); world.Nodes.Add(root);
        root.SetPayloadFloat(0x34, 0); root.SetPayloadFloat(0x38, 512); root.SetPayloadFloat(0x3C, 512); root.SetPayloadFloat(0x40, -512);
        root.SetPayloadFloat(0x44, 512); root.SetPayloadFloat(0x48, 0);
        WorldUpdate.SetPartition(root, 256, -256);
        HashSet<WorldNode> added = new(ReferenceEqualityComparer.Instance);
        void Add(WorldNode node)
        {
            if (!added.Add(node)) return;
            world.Nodes.Add(node); if (node.Model != null && !world.Models.Contains(node.Model)) world.Models.Add(node.Model);
            foreach (var child in node.Children) Add(child);
        }
        foreach (var node in cells.Concat(listed)) Add(node);
        WorldUpdate.RebuildBounds(world);
        WorldUpdate.Partition(root, cells);
        foreach (var node in listed) { root.Children.Add(node); node.Parents.Add(root); }
        return root;
    }
    private static WorldPolygon Polygon(WorldModel model, float x0, float y, float z0, float size)
    {
        int at = model.Vertices.Count;
        model.Vertices.AddRange([new(x0, y, z0), new(x0, y, z0 + size), new(x0 + size, y, z0 + size), new(x0 + size, y, z0)]);
        WorldPolygon polygon = new() { Material = new() { Flags = 0xFF }, Vertices = [at, at + 1, at + 2, at + 3], Zone = 0xFFFF0501 };
        model.Polygons.Add(polygon);
        return polygon;
    }
    private static WorldNode Node(string name, WorldNodeClass kind = WorldNodeClass.Object3D, params (float X, float Y, float Z, float Size)[] quads)
    {
        WorldModel? model = quads.Length == 0 ? null : new();
        foreach (var (x, y, z, size) in quads) Polygon(model!, x, y, z, size);
        WorldNode node = new(name, kind) { Model = model, Flags = Surface };
        if (kind == WorldNodeClass.Object3D) node.SetPayloadInt(0, 0x28);
        return node;
    }
    private static void Link(WorldNode parent, WorldNode child) { parent.Children.Add(child); child.Parents.Add(parent); }
    /// <summary>A cached box over the whole world, which lets the walk into a node that has siblings.</summary>
    private static void Boxed(WorldNode node) { node.Flags |= ZoneProbe.BoundsFlag; node.CachedBounds = new(new(0, -1, 0), new(512, 1, 512)); }

    [Fact]
    public void ClampedProbesOfAWorldWithoutCellsSearchOnlyItsList()
    {
        // The clamp flag set on a world whose grid has no cell: the engine has no edge cell to move the point into.
        var world = World([], Node("sky", quads: (-500, 0, -500, 1500)));
        world.SetPayloadInt(0x78, 0); world.SetPayloadInt(0x7C, 0); world.SetPayloadInt(0x50, 1);
        Assert.Single(ZoneProbe.Probe(world, 100, 100, ZoneSet.Cleared).Hits);
        Assert.Single(ZoneProbe.Probe(world, 100, 100, ZoneSet.Cleared, ZoneProbeKind.Vehicle).Hits);
        // Coordinates the engine's conversion puts outside every grid (huge, infinite, not a number) find nothing.
        foreach (float x in new[] { 3e38f, float.PositiveInfinity, float.NaN, -3e38f })
            foreach (int cells in new[] { 0, 2 })
            {
                world.SetPayloadInt(0x78, cells); world.SetPayloadInt(0x7C, cells);
                Assert.Empty(ZoneProbe.Probe(world, x, 100, ZoneSet.Cleared).Hits);
                Assert.Empty(ZoneProbe.Probe(world, x, 100, ZoneSet.Cleared, ZoneProbeKind.Vehicle).Hits);
            }
        // A cell number beyond 32 bits is −2³¹ to the engine's (int)floor(), so a clamped probe searches the first column,
        // not the last (cells this small move the point by nothing).
        var ground = Node("ground", quads: (0, 0, 0, 200));
        var tiny = World([ground]);
        Assert.Equal((0, 1), (ground.GridColumn, ground.GridRow));
        tiny.SetPayloadInt(0x50, 1); tiny.SetPayloadFloat(0x64, 1e30f); tiny.SetPayloadFloat(0x54, 1e-20f);
        Assert.Single(ZoneProbe.Probe(tiny, 100, 100, ZoneSet.Cleared).Hits);
    }

    [Fact]
    public void CyclesAndExponentialSharingAreRefused()
    {
        // A node that is its own descendant (no reader builds one; a world in memory can hold it).
        WorldNode a = Node("a"), b = Node("b");
        var world = World([], a);
        Boxed(a); Link(a, b); Link(b, a);
        Assert.Contains("cyclic", Assert.Throws<InvalidDataException>(() => ZoneProbe.Probe(world, 100, 100, ZoneSet.Cleared)).Message);
        // Each node lists the next twice: 2^22 paths to the last one, all inside the boxes that let the walk through.
        List<WorldNode> chain = [Node("level0")];
        for (int level = 1; level <= 22; level++) { var next = Node($"level{level}"); Link(chain[^1], next); Link(chain[^1], next); chain.Add(next); }
        var deep = World([], chain[0]);
        chain.ForEach(Boxed);
        Assert.Contains("visit more than", Assert.Throws<InvalidDataException>(() => ZoneProbe.Probe(deep, 100, 100, ZoneSet.Cleared, ZoneProbeKind.Vehicle)).Message);
        // With a model of 100 corners on every node, the vertices it would place run out first.
        foreach (var node in chain) node.Model = Node("", quads: [.. Enumerable.Range(0, 25).Select(i => (0f, -1f - i, 0f, 200f))]).Model;
        Assert.Contains("place more than", Assert.Throws<InvalidDataException>(() => ZoneProbe.Probe(deep, 100, 100, ZoneSet.Cleared, ZoneProbeKind.Vehicle)).Message);
    }

    [Fact]
    public void TheVehicleBufferCountsOnlySurfacesBelowTheProbe()
    {
        // 32 surfaces below y = 500 fill the buffer; one above it is passed over, not dropped.
        var stack = Node("stack", quads: [.. Enumerable.Range(0, 32).Select(i => (0f, (float)i, 0f, 200f)), (0f, 600f, 0f, 200f)]);
        var full = ZoneProbe.Probe(World([stack]), 100, 100, ZoneSet.Cleared, ZoneProbeKind.Vehicle);
        Assert.Equal(32, full.Hits.Count); Assert.False(full.Full);
        // One more surface below the probe is dropped, and that fills the buffer.
        Polygon(stack.Model!, 0, 40, 0, 200);
        Assert.True(ZoneProbe.Probe(World([stack]), 100, 100, ZoneSet.Cleared, ZoneProbeKind.Vehicle).Full);
    }

    [Fact]
    public void CamerasAndLightsPassTheProbeToTheirChildren()
    {
        foreach (var kind in new[] { WorldNodeClass.Camera, WorldNodeClass.Light })
        {
            // The holder stands at (300, 0, 300) turned half around y (translation +0x14, angles +0x20), so its child's
            // quad, x and z 0..200 under it, lies at x 100..300 and z 100..300.
            var holder = Node("holder", kind);
            holder.SetPayloadFloat(0x14, 300); holder.SetPayloadFloat(0x1C, 300); holder.SetPayloadFloat(0x24, MathF.PI);
            Boxed(holder);
            Link(holder, Node("deck", quads: (0, 7, 0, 200)));
            var world = World([Node("ground", quads: (0, 0, 0, 50))], holder);
            Assert.Equal([7f], ZoneProbe.Probe(world, 150, 200, ZoneSet.Cleared).Hits.Select(h => h.Height));
            Assert.Equal([7f], ZoneProbe.Probe(world, 150, 200, ZoneSet.Cleared, ZoneProbeKind.Vehicle).Hits.Select(h => h.Height));
            Assert.Empty(ZoneProbe.Probe(world, 150, 50, ZoneSet.Cleared).Hits);
        }
    }
}
