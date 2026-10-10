using Recoil.Zbd.Core.Formats;
using Recoil.Zbd.Core.Terrain;
using Recoil.Zbd.Core.Worlds;
using Xunit;

namespace Recoil.Zbd.Tests;

public sealed class TerrainProbeTieTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ReorderedCoincidentZoneOrSoilHitsRemainARealMismatch(bool differentPolygonZones)
    {
        var before = Read(false, differentPolygonZones: differentPolygonZones);
        var after = Read(true, differentPolygonZones: differentPolygonZones);
        var a = Root(before); var b = Root(after);
        // Independent engine selection retains the first equal-height candidate; neither set is interchangeable.
        Assert.Equal(3u, Selected(a).Node.Zone);
        Assert.Equal(4u, Selected(b).Node.Zone);
        var report = TerrainProbe.Compare(a.Children, b.Children, 2, Token);
        Assert.True(report.Complete);
        Assert.Equal(16, report.Samples);
        Assert.Equal(16, report.Mismatches);
    }

    [Fact]
    public void GridComparisonUsesEachWorldMembershipInsteadOfSerializedSlotOrder()
    {
        var before = Read(false); var after = Read(true);
        // Both files retain n0 then n1 in the node table. Only the owning world's child order changed.
        Assert.Equal(Slots(before).Select(n => n.Name), Slots(after).Select(n => n.Name));
        var changed = TerrainProbe.Compare(Slots(before), Slots(after), 2, Token, Root(before));
        Assert.True(changed.Complete);
        Assert.Equal(changed.Samples, changed.Mismatches);
        // Reversing an inspection list cannot invent a change to an unchanged world's membership.
        var unchanged = TerrainProbe.Compare(Slots(before), Slots(before).Reverse().ToArray(), 2, Token, Root(before));
        Assert.True(unchanged.Complete);
        Assert.Equal(0, unchanged.Mismatches);

        // The shared GUI/MCP world comparison must also retain that semantic order, rather than only matching members.
        AssertOrderChanged(before, after, "world.overflowOrder");
        var slotsOnly = Read(false);
        slotsOnly.Nodes.Reverse(1, slotsOnly.Nodes.Count - 1);
        slotsOnly = Reread(slotsOnly);
        Assert.Equal(0, WorldComparer.CompareTree(before, slotsOnly, token: Token).DifferenceCount);
        Assert.True(WorldComparer.Interchangeable(Root(before), Root(slotsOnly), token: Token));

        // The same two reader-backed surfaces can be encountered through a cell or a subtree, not just overflow.
        foreach (bool cell in new[] { true, false })
        {
            var first = Arrange(Read(false), cell); var reversed = Arrange(Read(true), cell);
            AssertOrderChanged(first, reversed, cell ? "world.area0" : "children");
        }

        // A cell can keep the same node in the merged tree after its overflow visit is removed. Outside that cell,
        // only the overflow edge reaches the part of the quad extending beyond the grid.
        var withOverflow = Overflow(true); var cellOnly = Overflow(false);
        Assert.Single(ZoneProbe.Probe(Root(withOverflow), 12, 1, ZoneSet.Cleared, token: Token).Hits);
        Assert.Empty(ZoneProbe.Probe(Root(cellOnly), 12, 1, ZoneSet.Cleared, token: Token).Hits);
        Assert.Contains(WorldComparer.CompareTree(withOverflow, cellOnly, token: Token).Differences, d => d.Field == "world.overflowOccurrences");
        Assert.Contains(WorldComparer.CompareTree(cellOnly, withOverflow, token: Token).Differences, d => d.Field == "world.overflowOccurrences");

        static GameZWorld Overflow(bool include)
        {
            var world = Arrange(Read(false), true); var root = Root(world); var node = root.Areas[0].Nodes[0];
            var model = node.Model!;
            for (int i = 0; i < model.Vertices.Count; i++)
            { var p = model.Vertices[i]; model.Vertices[i] = new(p.X * 2, p.Y, p.Z); }
            node.CachedBounds = new(new(0, 0, 0), new(16, 0, 8));
            if (include) root.Children.Add(node);
            return Reread(world);
        }

        static GameZWorld Arrange(GameZWorld world, bool cell)
        {
            var root = Root(world); var children = root.Children.ToArray(); root.Children.Clear();
            if (cell)
            {
                root.SetPayloadFloat(0x38, 8); root.SetPayloadFloat(0x3C, 8); root.SetPayloadFloat(0x40, -8); root.SetPayloadFloat(0x44, 8);
                WorldUpdate.SetPartition(root, 8, -8);
                root.Areas[0].Nodes.AddRange(children);
                foreach (var child in children) { child.GridColumn = 0; child.GridRow = 0; }
            }
            else
            {
                WorldNode group = new("group", WorldNodeClass.Object3D) { Flags = 0x11C, CachedBounds = children[0].CachedBounds };
                group.SetPayloadInt(0, 0x28); group.Parents.Add(root); root.Children.Add(group); world.Nodes.Add(group);
                foreach (var child in children) { child.Parents.Clear(); child.Parents.Add(group); group.Children.Add(child); }
            }
            return Reread(world);
        }
        static void AssertOrderChanged(GameZWorld first, GameZWorld reversed, string field)
        {
            Assert.Equal(3u, Selected(Root(first)).Node.Zone);
            Assert.Equal(4u, Selected(Root(reversed)).Node.Zone);
            var comparison = WorldComparer.CompareTree(first, reversed, token: Token);
            Assert.Contains(comparison.Differences, d => d.Field == field);
            // Semantic-copy grouping must not erase the same change when two roots share names and geometry.
            Assert.False(WorldComparer.Interchangeable(Root(first), Root(reversed), token: Token));
        }
    }

    [Theory]
    [InlineData(true, 0)]
    [InlineData(false, 2)]
    [InlineData(false, 0.004f)]
    public void EquivalentAttributesOrUnequalPhysicalHeightsDoNotInventTieChanges(bool sameAttributes, float secondHeight)
    {
        var before = Read(false, sameAttributes, secondHeight); var after = Read(true, sameAttributes, secondHeight);
        var report = TerrainProbe.Compare(Root(before).Children, Root(after).Children, 2, Token);
        Assert.True(report.Complete);
        Assert.Equal(0, report.Mismatches);
        Assert.Equal(Selected(Root(before)).Soil, Selected(Root(after)).Soil);
        Assert.Equal(Selected(Root(before)).Node.Zone, Selected(Root(after)).Node.Zone);
        if (secondHeight > 0)
        {
            var hits = TerrainProbe.At(Root(after).Children, 1, 1, Token);
            Assert.Equal(["n0", "n1"], hits.Select(h => h.Node));
            if (secondHeight < 0.005f) Assert.All(hits, h => Assert.Equal(0, h.Height));
        }
    }

    [Fact]
    public void CellCandidatesPrecedeOverflowCandidatesRegardlessOfSlotOrder()
    {
        var before = Read(false, cell: 0); var after = Read(false, cell: 1);
        Assert.Equal("n0", Selected(Root(before)).Node.Name);
        Assert.Equal("n1", Selected(Root(after)).Node.Name);
        var report = TerrainProbe.Compare(Slots(before).Reverse().ToArray(), Slots(after), 2, Token, Root(before));
        Assert.True(report.Complete);
        Assert.Equal(16, report.Mismatches);
        Assert.Equal(0, report.Revealed);
    }

    [Fact]
    public void SortingAndMembershipExhaustionAreIncompleteAndDoNotPoisonRetry()
    {
        var world = Read(false);
        Assert.Throws<InvalidDataException>(() => TerrainProbe.At(Root(world).Children, 1, 1, Token, 20));
        Assert.Equal(2, TerrainProbe.At(Root(world).Children, 1, 1, Token).Count);
        var limited = TerrainProbe.Compare(Slots(world), Slots(world), 2, Token, Root(world), 2, 1000);
        Assert.False(limited.Complete);
        Assert.Equal(0, limited.Samples);
        var good = TerrainProbe.Compare(Slots(world), Slots(world), 2, Token, Root(world));
        Assert.True(good.Complete);
        Assert.Equal(0, good.Mismatches);
    }

    [Fact]
    public void EqualPhysicalHeightsKeepEncounterOrderBeyondTheSmallSortPath()
    {
        var source = Read(false);
        var root = Root(source); var template = root.Children[0];
        source.Nodes.Clear(); source.Nodes.Add(root); root.Children.Clear();
        for (int i = 0; i < 24; i++)
        {
            WorldNode node = new($"equal{i}", WorldNodeClass.Object3D)
            { Flags = template.Flags, Zone = (uint)i, Model = template.Model, CachedBounds = template.CachedBounds };
            node.SetPayloadInt(0, 0x28); node.Parents.Add(root); root.Children.Add(node); source.Nodes.Add(node);
        }
        var reread = GameZWorldReader.FromDocument(FormatRegistry.Default.OpenBytes("many.zbd", GameZWriter.Write(source, Token), token: Token), Token);
        var candidates = Root(reread).Children;
        Assert.Equal(candidates.Select(n => n.Name), TerrainProbe.At(candidates, 1, 1, Token).Select(h => h.Node));
    }

    [Fact]
    public void CrossCellSharedMembershipIsIncompleteWhileSameCellOccurrencesRemainDistinct()
    {
        var source = Read(false, cell: 0); var root = Root(source); var shared = root.Areas[0].Nodes[0];
        root.Children.Add(shared); // One compiled node occurs in its cell and the overflow list.
        var parsed = FormatRegistry.Default.OpenBytes("shared.zbd", GameZWriter.Write(source, Token), token: Token);
        Assert.Empty(parsed.Diagnostics);
        var reread = GameZWorldReader.FromDocument(parsed, Token);
        var limited = TerrainProbe.Compare(Slots(reread), Slots(reread), 2, Token, Root(reread));
        Assert.False(limited.Complete);
        Assert.Contains("shared node in different cells", limited.Limitation);
        Assert.Equal(0, limited.Samples);

        root.Children.Remove(shared); root.Areas[0].Nodes.Add(shared);
        reread = GameZWorldReader.FromDocument(FormatRegistry.Default.OpenBytes("same-cell.zbd", GameZWriter.Write(source, Token), token: Token), Token);
        var complete = TerrainProbe.Compare(Slots(reread), Slots(reread), 2, Token, Root(reread));
        Assert.True(complete.Complete);
        Assert.Equal(0, complete.Mismatches);
        Assert.Equal(3 * complete.Samples, complete.Hits);
    }

    private static WorldNode Root(GameZWorld world) => world.Nodes.Single(n => n.Class == WorldNodeClass.World);
    private static WorldNode[] Slots(GameZWorld world) => world.Nodes.Where(n => n.Model != null).ToArray();
    private static ZoneHit Selected(WorldNode world)
    {
        var hits = ZoneProbe.Probe(world, 1, 1, ZoneSet.Cleared, token: Token).Hits;
        Assert.Equal(2, hits.Count);
        return hits[ZoneProbe.Select(hits, 5, 4, false).Index];
    }

    private static GameZWorld Read(bool reverse, bool sameAttributes = false, float secondHeight = 0, bool differentPolygonZones = true, int cell = -1)
    {
        GameZWorld world = new(); WorldNode root = new("world", WorldNodeClass.World); world.Nodes.Add(root);
        for (int i = 0; i < 2; i++)
        {
            float y = i == 0 ? 0 : secondHeight;
            WorldMaterial material = new() { Soil = (uint)(sameAttributes ? 0 : i * 2) };
            WorldModel model = new(); model.Vertices.AddRange([new(0, y, 0), new(0, y, 8), new(8, y, 8), new(8, y, 0)]);
            model.Polygons.Add(new() { Vertices = [0, 1, 2, 3], Material = material,
                Zone = 0xFFFF0001u | (uint)(!sameAttributes && differentPolygonZones ? i + 3 : 3) << 8 });
            WorldNode node = new($"n{i}", WorldNodeClass.Object3D)
            {
                Flags = 0x11C, Zone = (uint)(sameAttributes ? 3 : i + 3), Model = model,
                CachedBounds = new(new(0, y, 0), new(8, y, 8)),
            };
            node.SetPayloadInt(0, 0x28); root.Children.Add(node); node.Parents.Add(root);
            world.Nodes.Add(node); world.Models.Add(model); world.Materials.Add(material);
        }
        if (reverse) root.Children.Reverse();
        if (cell >= 0)
        {
            root.SetPayloadFloat(0x38, 8); root.SetPayloadFloat(0x3C, 8); root.SetPayloadFloat(0x40, -8); root.SetPayloadFloat(0x44, 8);
            WorldUpdate.SetPartition(root, 8, -8);
            var node = world.Nodes[cell + 1]; root.Children.Remove(node); root.Areas[0].Nodes.Add(node);
            node.GridColumn = 0; node.GridRow = 0;
        }
        return Reread(world);
    }

    private static GameZWorld Reread(GameZWorld world)
    {
        var document = FormatRegistry.Default.OpenBytes("terrain.zbd", GameZWriter.Write(world, Token), token: Token);
        Assert.Empty(document.Diagnostics);
        return GameZWorldReader.FromDocument(document, Token);
    }
}
