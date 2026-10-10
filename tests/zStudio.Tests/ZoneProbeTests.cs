using System.Numerics;
using Recoil.Zbd.Core.Worlds;
using Xunit;

namespace Recoil.Zbd.Tests;

/// <summary>The engine's zone probes (retail 0x443d20, 0x4444b0, 0x4290f0, 0x406510, 0x428d60), one rule per case.</summary>
public sealed class ZoneProbeTests
{
    private const uint Surface = 0x1C; // active, altitude, intersection

    /// <summary>A 2 × 2-cell world (x 0..512, z 0..512) holding <paramref name="nodes"/>, partitioned as the engine does.</summary>
    private static WorldNode World(params WorldNode[] nodes)
    {
        GameZWorld world = new();
        WorldNode root = new("world", WorldNodeClass.World); world.Nodes.Add(root);
        root.SetPayloadFloat(0x34, 0); root.SetPayloadFloat(0x38, 512); root.SetPayloadFloat(0x3C, 512); root.SetPayloadFloat(0x40, -512);
        root.SetPayloadFloat(0x44, 512); root.SetPayloadFloat(0x48, 0);
        WorldUpdate.SetPartition(root, 256, -256);
        foreach (var node in nodes) Add(world, node);
        WorldUpdate.RebuildBounds(world);
        WorldUpdate.Partition(root, nodes);
        return root;
        static void Add(GameZWorld world, WorldNode node)
        {
            world.Nodes.Add(node); if (node.Model != null && !world.Models.Contains(node.Model)) world.Models.Add(node.Model);
            foreach (var child in node.Children) Add(world, child);
        }
    }
    /// <summary>A node with upward quads over x 0..200, z 0..200, one per (height, zone word) in stored order.</summary>
    private static WorldNode Node(string name, uint flags, params (float Y, uint Zone)[] quads)
    {
        WorldModel model = new();
        foreach (var (y, zone) in quads)
        {
            int at = model.Vertices.Count;
            model.Vertices.AddRange([new(0, y, 0), new(0, y, 200), new(200, y, 200), new(200, y, 0)]);
            model.Polygons.Add(new() { Material = new() { Flags = 0xFF }, Vertices = [at, at + 1, at + 2, at + 3], Zone = zone });
        }
        WorldNode node = new(name, WorldNodeClass.Object3D) { Model = model, Flags = flags };
        node.SetPayloadInt(0, 0x28);
        return node;
    }
    /// <summary>A polygon zone word: the count, then up to three ids (unused ones 0xFF).</summary>
    private static uint Zones(params byte[] ids) =>
        (uint)ids.Length | (ids.Length > 0 ? ids[0] : 0xFFu) << 8 | (ids.Length > 1 ? ids[1] : 0xFFu) << 16 | (ids.Length > 2 ? ids[2] : 0xFFu) << 24;
    private static ZoneSet Set(params byte[] ids) => ZoneSet.FromWord(Zones(ids));

    [Fact]
    public void ZoneSetsTestAsTheEngineDoes()
    {
        Assert.True(ZoneSet.Cleared.Allows(4)); Assert.True(Set(1, 2).Allows(2)); Assert.False(Set(1, 2).Allows(4));
        Assert.True(Set(1).Allows(0xFF)); Assert.True(Set(0xFF).Allows(9));
        Assert.True(Set(1, 3).Overlaps(Set(3))); Assert.False(Set(1).Overlaps(Set(2))); Assert.True(Set(1).Overlaps(ZoneSet.Cleared));
        Assert.True(Set(0xFF).Overlaps(Set(7))); Assert.True(Set(7).Overlaps(Set(0xFF)));
        Assert.Equal(0xFF0D0E02u, Set(14, 13).Word); Assert.Equal("14+13", Set(14, 13).ToString()); Assert.Equal("none", ZoneSet.Cleared.ToString());
    }

    [Fact]
    public void ThePointProbeTakesTheFirstPolygonOfANodeAndTheVehicleProbeEvery()
    {
        // A floor and a deck stacked in one node, the floor stored first.
        var world = World(Node("stack", Surface, (0, Zones(1)), (10, Zones(2))));
        var point = ZoneProbe.Probe(world, 100, 100, ZoneSet.Cleared, token: TestContext.Current.CancellationToken);
        Assert.Equal([0f], point.Hits.Select(h => h.Height));
        var vehicle = ZoneProbe.Probe(world, 100, 100, ZoneSet.Cleared, ZoneProbeKind.Vehicle, token: TestContext.Current.CancellationToken);
        Assert.Equal([0f, 10f], vehicle.Hits.Select(h => h.Height));
        // So a camera over the deck takes the floor's zones; a vehicle on the deck the deck's.
        Assert.Equal(Set(1), ZoneProbe.CameraZones(world, new(100, 20, 100), Set(9), ZoneSet.Cleared, token: TestContext.Current.CancellationToken));
        Assert.Equal(Set(2), ZoneProbe.VehicleZones(world, new(100, 10.5f, 100), ZoneSet.Cleared, 1, token: TestContext.Current.CancellationToken).Zones);
        // As separate nodes, both probes see both surfaces, and the camera takes the deck.
        var separate = World(Node("floor", Surface, (0, Zones(1))), Node("deck", Surface, (10, Zones(2))));
        Assert.Equal(Set(2), ZoneProbe.CameraZones(separate, new(100, 20, 100), Set(9), ZoneSet.Cleared, token: TestContext.Current.CancellationToken));
    }

    [Fact]
    public void ProbesStartAt500AndSeeOnlyActiveAltitudeSurfacesFacingUp()
    {
        var high = World(Node("roof", Surface, (499, Zones(1)), (501, Zones(2))));
        Assert.Equal([499f], ZoneProbe.Probe(high, 100, 100, ZoneSet.Cleared, ZoneProbeKind.Vehicle, token: TestContext.Current.CancellationToken).Hits.Select(h => h.Height));
        Assert.Empty(ZoneProbe.Probe(World(Node("roof", Surface, (501, Zones(2)))), 100, 100, ZoneSet.Cleared, token: TestContext.Current.CancellationToken).Hits);
        Assert.Empty(ZoneProbe.Probe(World(Node("wall", Surface & ~0x08u, (0, Zones(1)))), 100, 100, ZoneSet.Cleared, token: TestContext.Current.CancellationToken).Hits);
        Assert.Empty(ZoneProbe.Probe(World(Node("off", Surface & ~0x04u, (0, Zones(1)))), 100, 100, ZoneSet.Cleared, token: TestContext.Current.CancellationToken).Hits);
        // A polygon wound the other way faces down and holds no point.
        var down = Node("ceiling", Surface, (0, Zones(1)));
        down.Model!.Polygons[0].Vertices = [3, 2, 1, 0];
        Assert.Empty(ZoneProbe.Probe(World(down), 100, 100, ZoneSet.Cleared, token: TestContext.Current.CancellationToken).Hits);
        Assert.Empty(ZoneProbe.Probe(World(Node("ceiling", Surface, (0, Zones(1)))), 300, 100, ZoneSet.Cleared, token: TestContext.Current.CancellationToken).Hits);
    }

    [Fact]
    public void TheGateAdmitsANodeOnlyWhenTheCurrentZonesAllowItsZone()
    {
        var gated = Node("cave", Surface | ZoneProbe.GateFlag, (0, Zones(3))); gated.Zone = 3;
        var world = World(gated);
        Assert.Empty(ZoneProbe.Probe(world, 100, 100, Set(1), token: TestContext.Current.CancellationToken).Hits);
        Assert.Single(ZoneProbe.Probe(world, 100, 100, Set(1, 3), token: TestContext.Current.CancellationToken).Hits);
        Assert.Single(ZoneProbe.Probe(world, 100, 100, ZoneSet.Cleared, token: TestContext.Current.CancellationToken).Hits);
        // Without the gate the zone is not tested.
        var open = Node("cave", Surface, (0, Zones(3))); open.Zone = 3;
        Assert.Single(ZoneProbe.Probe(World(open), 100, 100, Set(1), token: TestContext.Current.CancellationToken).Hits);
        // The camera's gate is its previous zones: once outside, it does not find the gated surface.
        Assert.Equal(Set(1), ZoneProbe.CameraZones(world, new(100, 5, 100), Set(1), ZoneSet.Cleared, token: TestContext.Current.CancellationToken));
        Assert.Equal(Set(3), ZoneProbe.CameraZones(world, new(100, 5, 100), Set(3), ZoneSet.Cleared, token: TestContext.Current.CancellationToken));
    }

    [Fact]
    public void CameraZonesAddThePlayersAndKeepTheLastValidSet()
    {
        Vector3 eye = new(100, 5, 100);
        Assert.Equal(Set(1, 2), ZoneProbe.CameraZones(World(Node("g", Surface, (0, Zones(1)))), eye, Set(9), Set(2), token: TestContext.Current.CancellationToken));
        Assert.Equal(Set(1, 2, 3), ZoneProbe.CameraZones(World(Node("g", Surface, (0, Zones(1, 2, 3)))), eye, Set(9), Set(4), token: TestContext.Current.CancellationToken));
        Assert.Equal(Set(1), ZoneProbe.CameraZones(World(Node("g", Surface, (0, Zones(1)))), eye, Set(9), Set(1), token: TestContext.Current.CancellationToken));
        // No information, an "any" zone, or nothing under the camera: the previous zones stay.
        Assert.Equal(Set(9), ZoneProbe.CameraZones(World(Node("g", Surface, (0, Zones()))), eye, Set(9), Set(2), token: TestContext.Current.CancellationToken));
        Assert.Equal(Set(9), ZoneProbe.CameraZones(World(Node("g", Surface, (0, Zones(0xFF)))), eye, Set(9), Set(2), token: TestContext.Current.CancellationToken));
        Assert.Equal(Set(9), ZoneProbe.CameraZones(World(Node("g", Surface, (0, Zones(1)))), eye, Set(9), Set(0xFF), token: TestContext.Current.CancellationToken));
        Assert.Equal(Set(9), ZoneProbe.CameraZones(World(Node("g", Surface, (0, Zones(1)))), new(300, 5, 100), Set(9), Set(2), token: TestContext.Current.CancellationToken));
        // With nothing at or below the camera, the first surface found counts.
        Assert.Equal(Set(1), ZoneProbe.CameraZones(World(Node("g", Surface, (50, Zones(1)), (60, Zones(2)))), eye, Set(9), ZoneSet.Cleared, token: TestContext.Current.CancellationToken));
    }

    [Fact]
    public void VehicleZonesFollowTheGroundEvenWithoutInformation()
    {
        var ground = Node("ground", Surface, (0, Zones(4))); ground.Zone = 7;
        var (zones, root) = ZoneProbe.VehicleZones(World(ground), new(100, 0.5f, 100), Set(1), 1, token: TestContext.Current.CancellationToken);
        Assert.Equal(Set(4), zones); Assert.Equal(7, root);
        // A polygon without zones clears the vehicle's (it then passes every test); no ground keeps them, with root 0xFF.
        Assert.Equal(ZoneSet.Cleared, ZoneProbe.VehicleZones(World(Node("g", Surface, (0, Zones()))), new(100, 0.5f, 100), Set(1), 1, token: TestContext.Current.CancellationToken).Zones);
        Assert.Equal((Set(1), (byte)0xFF), ZoneProbe.VehicleZones(World(ground), new(300, 0.5f, 100), Set(1), 1, token: TestContext.Current.CancellationToken));
        // Spawning starts from no zones and looks up to 4 units above the spawn point.
        Assert.Equal(Set(4), ZoneProbe.SpawnZones(World(Node("g", Surface, (3, Zones(4)))), new(100, 0, 100), true, token: TestContext.Current.CancellationToken).Zones);
    }

    [Fact]
    public void SelectionTakesTheHighestReachableSurfaceAndCanSkipWater()
    {
        var water = Node("water", Surface, (20, Zones(1)));
        water.Model!.Polygons[0].Material!.Soil = ZoneProbe.WaterSoil;
        var world = World(Node("seabed", Surface, (0, Zones(14))), water);
        var hits = ZoneProbe.Probe(world, 100, 100, ZoneSet.Cleared, ZoneProbeKind.Vehicle, token: TestContext.Current.CancellationToken).Hits;
        Assert.Equal(20f, hits[ZoneProbe.Select(hits, 25, 1, false).Index].Height);
        Assert.Equal(0f, hits[ZoneProbe.Select(hits, 25, 1, true).Index].Height);
        Assert.Equal(0f, hits[ZoneProbe.Select(hits, 10, 1, false).Index].Height);
        // Nothing reachable: hit 0 and the nearest height.
        Assert.Equal((0, 0f), ZoneProbe.Select(hits, -50, 1, false));
        // An amphibious-locked spawn skips the water too.
        Assert.Equal(Set(14), ZoneProbe.SpawnZones(world, new(100, 25, 100), false, token: TestContext.Current.CancellationToken).Zones);
        Assert.Equal(Set(1), ZoneProbe.SpawnZones(world, new(100, 25, 100), true, token: TestContext.Current.CancellationToken).Zones);
    }

    [Fact]
    public void OutsideTheGridOnlyThePointProbeSearchesTheOverflowList()
    {
        // A landmark is never placed in a cell, so it sits in the world's overflow list.
        var sky = Node("sky", Surface | 0x80, (0, Zones(5)));
        sky.Model!.Vertices.Clear(); sky.Model.Vertices.AddRange([new(-500, 0, -500), new(-500, 0, 1000), new(1000, 0, 1000), new(1000, 0, -500)]);
        var world = World(sky);
        Assert.Contains(world.Children, n => n.Name == "sky");
        Assert.Single(ZoneProbe.Probe(world, 600, 100, ZoneSet.Cleared, token: TestContext.Current.CancellationToken).Hits);
        Assert.Empty(ZoneProbe.Probe(world, 600, 100, ZoneSet.Cleared, ZoneProbeKind.Vehicle, token: TestContext.Current.CancellationToken).Hits);
        Assert.Single(ZoneProbe.Probe(world, 100, 100, ZoneSet.Cleared, ZoneProbeKind.Vehicle, token: TestContext.Current.CancellationToken).Hits);
    }

    [Fact]
    public void ProbesHoldAtMost32SurfacesAndSkipFarLodBands()
    {
        var nodes = Enumerable.Range(0, 33).Select(i => Node($"layer{i}", Surface, (i, Zones(1)))).ToArray();
        var point = ZoneProbe.Probe(World(nodes), 100, 100, ZoneSet.Cleared, token: TestContext.Current.CancellationToken);
        Assert.True(point.Full); Assert.Equal(32, point.Hits.Count);
        var stack = ZoneProbe.Probe(World(Node("stack", Surface, [.. Enumerable.Range(0, 33).Select(i => ((float)i, Zones(1)))])), 100, 100, ZoneSet.Cleared, ZoneProbeKind.Vehicle, token: TestContext.Current.CancellationToken);
        Assert.True(stack.Full); Assert.Equal(32, stack.Hits.Count);
        // A LOD group is searched only when its band starts at the viewer.
        WorldNode near = new("near", WorldNodeClass.Lod) { Flags = Surface }, far = new("far", WorldNodeClass.Lod) { Flags = Surface };
        far.SetPayloadFloat(4, 100 * 100);
        foreach (var (lod, zone) in new[] { (near, (byte)1), (far, (byte)2) })
        {
            var child = Node($"{lod.Name}-ground", Surface, (0, Zones(zone)));
            lod.Children.Add(child); child.Parents.Add(lod);
        }
        Assert.Equal([Set(1)], ZoneProbe.Probe(World(near, far), 100, 100, ZoneSet.Cleared, token: TestContext.Current.CancellationToken).Hits.Select(h => h.Zones));
    }

    [Fact]
    public void ARendererWithoutTheGamesZonesProbesFromTheEye()
    {
        var world = World(Node("floor", Surface, (0, Zones(1))), Node("roof", Surface, (30, Zones(2))));
        Assert.Equal(Set(1), ZoneProbe.ViewZones(world, new(100, 20, 100), Set(9), token: TestContext.Current.CancellationToken));
        Assert.Equal(Set(2), ZoneProbe.ViewZones(world, new(100, 40, 100), Set(9), token: TestContext.Current.CancellationToken));
        Assert.Equal(ZoneSet.Cleared, ZoneProbe.ViewZones(world, new(300, 40, 100), Set(9), token: TestContext.Current.CancellationToken));
    }
}
