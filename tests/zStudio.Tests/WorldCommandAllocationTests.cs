using System.IO;
using System.Text;
using Recoil.Zbd.Core.Worlds;
using Xunit;

namespace Recoil.Zbd.Tests;

public sealed class WorldCommandAllocationTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;
    private sealed class Files(string script) : IProjectFiles
    {
        public byte[] Bytes { get; } = Encoding.ASCII.GetBytes(script);
        public bool Exists(string path) => path == "gamegen/m1.gs";
        public byte[] Read(string path, CancellationToken token) => Read(path, token, ProjectReadLimits.Document);
        public byte[] Read(string path, CancellationToken token, ProjectReadLimits limits) { token.ThrowIfCancellationRequested(); byte[] result = Bytes; limits.Validate(result); return result; }
    }
    [Fact]
    public void RepeatedLightingChargesEveryQueuedOccurrenceAcrossCommands()
    {
        // Two identical child edges still cost two queue entries, although lighting visits their shared node once.
        const string prefix = "NewObject3D leaf\nNewWorld world\nAddChild leaf\nAddChild leaf\n";
        long oneWalk = 3 * WorldCommandBudget.TraversalEntryBytes;
        Files files = new(prefix + "NodeSetLighting on\nNodeSetLighting off\nGameZWriteZBDFile out\n");
        byte[] original = [.. files.Bytes];
        var refused = new WorldAssembler(files, Token) { CommandAllocationLimit = 2 * oneWalk - 1 };
        Assert.Contains("repeated command work", Assert.Throws<InvalidDataException>(() => refused.Assemble("m1.gs")).Message);
        Assert.Equal(original, files.Bytes);
        var accepted = new WorldAssembler(files, Token) { CommandAllocationLimit = 2 * oneWalk };
        Assert.Equal(2, accepted.Assemble("m1.gs").Nodes.Count);
        Assert.NotEmpty(GameZWriter.Write(accepted.World, Token));
    }
    [Fact]
    public void LightingAndRepeatedPartitionShareTheAssemblyAllowance()
    {
        const string script = "NewWorld world\nWorldExtents 2 -2\nWorldPartition 1 -1\nNodeSetLighting on\nWorldPartition 1 -1\nGameZWriteZBDFile out\n";
        long needed = 8 * WorldCommandBudget.PartitionCellBytes + WorldCommandBudget.TraversalEntryBytes;
        var refused = new WorldAssembler(new Files(script), Token) { CommandAllocationLimit = needed - 1 };
        Assert.Contains("repeated command work", Assert.Throws<InvalidDataException>(() => refused.Assemble("m1.gs")).Message);
        var accepted = new WorldAssembler(new Files(script), Token) { CommandAllocationLimit = needed };
        Assert.Equal(4, Assert.Single(accepted.Assemble("m1.gs").Nodes).Areas.Count);
        // Commands after the first write cannot repeat either allocation or Finish.
        var written = new WorldAssembler(new Files(script + "WorldPartition 1 -1\nNodeSetLighting on\nGameZWriteZBDFile again\n"), Token) { CommandAllocationLimit = needed };
        Assert.Equal(4, Assert.Single(written.Assemble("m1.gs").Nodes).Areas.Count);
    }
    [Fact]
    public void PartitionReservationAndCancellationPrecedeCellReplacement()
    {
        WorldNode world = new("world", WorldNodeClass.World);
        world.SetPayloadFloat(0x3C, 2); world.SetPayloadFloat(0x40, -2);
        WorldCommandBudget budget = new(4 * WorldCommandBudget.PartitionCellBytes, Token);
        WorldUpdate.SetPartition(world, 1, -1, budget, Token);
        var original = world.Areas.ToArray(); var payload = world.Payload.ToArray();
        Assert.Throws<InvalidDataException>(() => WorldUpdate.SetPartition(world, 2, -2, budget, Token));
        Assert.Equal(original, world.Areas); Assert.Equal(payload, world.Payload);
        using CancellationTokenSource cancelled = new(); cancelled.Cancel();
        Assert.Throws<OperationCanceledException>(() => WorldUpdate.SetPartition(world, 2, -2, new(), cancelled.Token));
        Assert.Equal(original, world.Areas); Assert.Equal(payload, world.Payload);
    }
    [Fact]
    public void DuplicateParentRemovalChargesScanAndShiftBeforeChangingAnOccurrence()
    {
        const string prefix = "NewObject3D leaf\nNewWorld world\nAddChild leaf\nAddChild leaf\nAddChild leaf\n";
        // The prefix must fit the same allowance, so refusal cannot be mistaken for a setup lookup failure.
        var setup = new WorldAssembler(new Files(prefix + "GameZWriteZBDFile out\n"), Token) { LookupWorkLimit = 14 };
        setup.Assemble("m1.gs");
        // An ample traversal allocation allowance cannot hide the independent list-scan/shift ceiling.
        string script = prefix + "DeleteTree world\nNewWorld survivor\nGameZWriteZBDFile out\n";
        var bounded = new WorldAssembler(new Files(script), Token) { LookupWorkLimit = 14 };
        Assert.Contains("lookup work", Assert.Throws<InvalidDataException>(() => bounded.Assemble("m1.gs")).Message);
        Assert.Single(bounded.World.Nodes.Single(n => n.Name == "leaf").Parents); // Last shift was refused before removing it.
        var accepted = new WorldAssembler(new Files(script), Token);
        Assert.Equal("survivor", Assert.Single(accepted.Assemble("m1.gs").Nodes).Name);
    }
    [Fact]
    public void UnlinkChargesBothListsBeforeRemovingTheFirstOccurrence()
    {
        const string prefix = "NewObject3D leaf\nNewWorld world\nAddChild leaf\nAddChild leaf\nAddChild leaf\n";
        var bounded = new WorldAssembler(new Files(prefix + "DeleteChild leaf\nGameZWriteZBDFile out\n"), Token) { LookupWorkLimit = 18 };
        Assert.Contains("lookup work", Assert.Throws<InvalidDataException>(() => bounded.Assemble("m1.gs")).Message);
        Assert.Equal(3, bounded.World.Nodes.Single(n => n.Name == "leaf").Parents.Count);
        Assert.Equal(3, bounded.World.Nodes.Single(n => n.Name == "world").Children.Count);
        var accepted = new WorldAssembler(new Files(prefix + "DeleteChild leaf\nGameZWriteZBDFile out\n"), Token) { LookupWorkLimit = 19 };
        Assert.Equal(2, accepted.Assemble("m1.gs").Nodes.Single(n => n.Name == "leaf").Parents.Count);
    }
}
