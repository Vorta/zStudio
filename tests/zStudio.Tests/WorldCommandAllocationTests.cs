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
    /// <summary>
    /// LightNew (gwLightNew 0x452fd0) and the light setters as the engine stores them: a white point source at full intensity
    /// with ranges 32 and 64; LightSetDirectional sets only its own flag (0x4532a0); the directed and point sources exclude
    /// each other; LightSetActive is gwNodeSetActive (0x447c60), the node's flag 0x04; equal ranges become 10 apart
    /// (gwLightSetRange 0x453400); colours are clamped to 0–1 (0x49b5a0). Offsets: 0 dirty, 4 enabled, 164 diffuse,
    /// 168 ambient, 172 colour, 184 directional, 188 directed, 192 point, 196 saturated, 204 near, 208 far, 212 far², 216 1/(far−near).
    /// </summary>
    [Theory]
    [InlineData("", "0=1 4=1 144:1 164:0 168:1 172:1 176:1 180:1 184=0 188=0 192=1 196=1 200=1 204:32 208:64 212:4096 216:0.03125", 0x0108011Cu)]
    [InlineData("LightSetDirectional on\nLightSetDirectional off", "184=0 188=0 192=1", 0x0108011Cu)]
    [InlineData("LightSetDirectional on\nLightSetDirectedSource", "184=1 188=1 192=0", 0x0108011Cu)]
    [InlineData("LightSetDirectedSource\nLightSetPointSource", "184=0 188=0 192=1", 0x0108011Cu)]
    [InlineData("LightSetActive off", "4=1", 0x01080118u)]
    [InlineData("LightSetRanges 50 50", "204:50 208:60 212:3600 216:0.1", 0x0108011Cu)]
    [InlineData("LightSetRanges 540 480", "204:480 208:540 212:291600 216:0.016666668", 0x0108011Cu)]
    [InlineData("LightSetColor 2 -1 0.5\nLightSetDiffuse .55\nLightSetAmbient 3\nLightSetSaturated off", "172:1 176:0 180:0.5 164:0.55 168:3 196=0", 0x0108011Cu)]
    public void LightCommandsStoreWhatTheEngineStores(string commands, string expected, uint flags)
    {
        var light = new WorldAssembler(new Files($"LightNew sun\n{commands}\nGameZWriteZBDFile out\n"), Token).Assemble("m1.gs").Nodes.Single(n => n.Name == "sun");
        Assert.Equal(flags, light.Flags);
        foreach (string field in expected.Split(' '))
        {
            int at = field.IndexOfAny([':', '=']), offset = int.Parse(field[..at]);
            if (field[at] == '=') Assert.Equal(int.Parse(field[(at + 1)..]), light.PayloadInt(offset));
            else Assert.Equal(float.Parse(field[(at + 1)..], System.Globalization.CultureInfo.InvariantCulture), light.PayloadFloat(offset));
        }
    }
    /// <summary>
    /// CameraSetWorld (gwCameraSetWorld 0x449dd0) keeps the camera's world when no world has the name; CameraSetHorizon and
    /// CameraSetHorizonXZ find any node (FindByTypeAndName bucket 6, which gwNodeNew 0x4478c0 adds every node to).
    /// </summary>
    [Fact]
    public void CameraLookupsFollowTheEngine()
    {
        var world = new WorldAssembler(new Files("NewWorld world\nLightNew sky\nNewCamera camera\nCameraSetWorld world\nCameraSetWorld missing\nCameraSetHorizon sky\nCameraSetHorizonXZ world\nGameZWriteZBDFile out\n"), Token).Assemble("m1.gs");
        var camera = world.Nodes.Single(n => n.Class == WorldNodeClass.Camera);
        Assert.Equal(("world", "sky", "world"), (camera.CameraWorld?.Name, camera.CameraHorizon?.Name, camera.CameraHorizonXZ?.Name));
        Assert.NotEmpty(GameZWriter.Write(world, Token));
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
