using System.Text;
using System.IO;
using Recoil.Zbd.Core.Formats;
using Recoil.Zbd.Core.Sources;
using Recoil.Zbd.Core.Worlds;
using Xunit;

namespace Recoil.Zbd.Tests;

public sealed class WorldOwnedEdgeTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;
    private sealed class Files(string script) : IProjectFiles
    {
        private readonly byte[] ownedInput = Encoding.ASCII.GetBytes(script);
        public bool Exists(string path) => path == "gamegen/m1.gs";
        public byte[] Read(string path, CancellationToken token) => Read(path, token, ProjectReadLimits.Document);
        public byte[] Read(string path, CancellationToken token, ProjectReadLimits limits) { token.ThrowIfCancellationRequested(); byte[] result = ownedInput; limits.Validate(result); return result; }
    }
    [Theory]
    [InlineData("", "leaf", "leaf", "a,b")]
    [InlineData("FindNode a\nAddChild leaf\n", "leaf,leaf", "leaf", "a,b,a")]
    [InlineData("FindNode a\nAddChild leaf\nDeleteChild leaf\n", "leaf", "leaf", "b,a")]
    [InlineData("FindNode a\nDeleteChild leaf\n", "", "leaf", "b")]
    [InlineData("DeleteTree a\n", null, "leaf", "b")]
    public void EachWorldRetainsItsOwnOrderedEdgeOccurrences(string commands, string? expectedA, string expectedB, string expectedParents)
    {
        string script = "NewObject3D leaf\nNewWorld a\nAddChild leaf\nNewWorld b\nAddChild leaf\n" + commands + "GameZWriteZBDFile out\n";
        var world = new WorldAssembler(new Files(script), Token).Assemble("m1.gs");
        Check(world);
        var read = GameZWorldReader.FromDocument(FormatRegistry.Default.OpenBytes("gamez.zbd", GameZWriter.Write(world, Token), token: Token), Token);
        Check(read);
        void Check(GameZWorld value)
        {
            var a = value.Nodes.SingleOrDefault(n => n.Name == "a");
            if (expectedA == null) Assert.Null(a); else Assert.Equal(expectedA, string.Join(',', a!.Children.Select(n => n.Name)));
            Assert.Equal(expectedB, string.Join(',', value.Nodes.Single(n => n.Name == "b").Children.Select(n => n.Name)));
            Assert.Equal(expectedParents, string.Join(',', value.Nodes.Single(n => n.Name == "leaf").Parents.Select(n => n.Name)));
        }
    }
    [Fact]
    public void LiveWorldChildrenSupportSubtreeLookupAndRecursiveDeletionBeforeWrite()
    {
        var world = new WorldAssembler(new Files("NewObject3D leaf\nNewWorld a\nAddChild leaf\nFindSubNode leaf\nNodeSetDescription renamed\nFindNode a\nDeleteTree a\nNewWorld survivor\nGameZWriteZBDFile out\n"), Token).Assemble("m1.gs");
        Assert.Equal("survivor", Assert.Single(world.Nodes).Name);
        var bytes = GameZWriter.Write(world, Token);
        var read = GameZWorldReader.FromDocument(FormatRegistry.Default.OpenBytes("gamez.zbd", bytes, token: Token), Token);
        Assert.Equal("survivor", Assert.Single(read.Nodes).Name);
        Assert.Contains(read.FreedSlots.Values, slot => Encoding.Latin1.GetString(slot.AsSpan(0, 36)).StartsWith("renamed\0", StringComparison.Ordinal));
    }
    [Fact]
    public void PendingDatabaseAndDirectAttachmentsPreserveCellAndOverflowOccurrences()
    {
        using SourceWorldFixture fixture = new();
        string path = "gamegen/m1.gs";
        string script = Encoding.Latin1.GetString(File.ReadAllBytes(fixture.Path(path)))
            .Replace("WorldOrigin 0.0 512.0", "WorldOrigin 0.0 0.0", StringComparison.Ordinal)
            .Replace("GameZWriteZBDFile", """
                NewObject3D overflow
                FindNode world
                AddChild overflow
                AddChild ground
                NewWorld b
                WorldOrigin 0 0
                WorldExtents 512 -512
                WorldPartition 256 -256
                AddChild ground
                FindNode world
                FindSubNode ground
                NodeSetDescription terrain
                GameZWriteZBDFile
                """.TrimEnd(), StringComparison.Ordinal);
        fixture.Write(path, script);
        var assembled = new WorldAssembler(new SourceWorlds.DiskFiles(fixture.Project, null), Token).Assemble("m1.gs");
        Check(assembled);
        Check(GameZWorldReader.FromDocument(FormatRegistry.Default.OpenBytes("gamez.zbd", GameZWriter.Write(assembled, Token), token: Token), Token));
        void Check(GameZWorld world)
        {
            var primary = world.Nodes.Single(n => n.Name == "world"); var other = world.Nodes.Single(n => n.Name == "b");
            Assert.Equal(["overflow"], primary.Children.Select(n => n.Name));
            Assert.Empty(other.Children);
            Assert.Equal(["terrain", "terrain"], primary.Areas.SelectMany(a => a.Nodes).Select(n => n.Name));
            Assert.Equal(["terrain"], other.Areas.SelectMany(a => a.Nodes).Select(n => n.Name));
            Assert.Equal(["world", "world", "b"], world.Nodes.Single(n => n.Name == "terrain").Parents.Select(n => n.Name));
            Assert.DoesNotContain(world.Nodes, n => n.Name == "m1.flt");
        }
    }
    [Fact]
    public void PublicPartitionKeepsOccurrenceCountsAndAddsOnlyMissingParentMembership()
    {
        WorldNode world = new("world", WorldNodeClass.World), other = new("other", WorldNodeClass.World), child = new("leaf", WorldNodeClass.Object3D);
        child.Parents.Add(other); child.Parents.Add(world); child.Parents.Add(world);
        WorldUpdate.Partition(world, [child, child]);
        Assert.Equal([child, child], world.Children);
        Assert.Equal([other, world, world], child.Parents);
        WorldNode absent = new("absent", WorldNodeClass.Object3D);
        WorldUpdate.Partition(world, [child, child, absent, absent]);
        Assert.Equal([child, child, absent, absent], world.Children);
        Assert.Equal([world], absent.Parents);
        Assert.Equal([other, world, world], child.Parents);
    }
}
