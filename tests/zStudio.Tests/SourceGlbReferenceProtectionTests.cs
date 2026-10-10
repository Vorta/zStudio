using System.Buffers.Binary;
using System.IO;
using System.Text;
using System.Text.Json.Nodes;
using Recoil.Zbd.Core.Gltf;
using Recoil.Zbd.Core.Sources;
using Recoil.Zbd.Core.Worlds;
using Xunit;

namespace Recoil.Zbd.Tests;

public sealed class SourceGlbReferenceProtectionTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Fact]
    public void TerrainCreationProtectsModelsReachedThroughAValidGlb()
    {
        using SourceWorldFixture fixture = new();
        const string database = "data/m1/models/m1.gltf";
        fixture.Write(database, Reference("../../../gamegen/bridge.glb"));
        WriteBridge(fixture);
        // Establish that the actual world loader follows this binary container and loads the protected hull.
        WorldAssembler assembler = new(new SourceWorlds.DiskFiles(fixture.Project, null), Token);
        Assert.Single(assembler.Assemble("m1.gs").Nodes, n => n.Name == "hull");
        SourceWorkspace workspace = new(fixture.Project);
        byte[] before = File.ReadAllBytes(fixture.Path(database));

        var error = Assert.Throws<InvalidDataException>(() => SourceTerrain.Create(workspace, database, fixture.Tank, ["hull"], token: Token));
        Assert.Contains("loaded with the mission database", error.Message);
        Assert.False(workspace.IsDirty);
        Assert.False(workspace.CanUndo);
        Assert.Equal(0, workspace.Revision);
        Assert.Equal(before, File.ReadAllBytes(fixture.Path(database)));
        Assert.False(File.Exists(fixture.Path("data/m2/models/bft/tank.terrain.json")));
    }

    [Theory]
    [InlineData(0)] // Existing physical GLB reference.
    [InlineData(1)] // A direct logical alias has no physical file.
    [InlineData(2)] // A neutral holder resolves its reference through the manifest.
    public void OtherMissionTransformProtectionFollowsTheSameGlbReference(int binding)
    {
        using SourceWorldFixture fixture = new();
        WriteBridge(fixture);
        const string alias = "data/m2/models/bft/alias.gltf", bridge = "data/common/models/bridge.gltf";
        string folder = "gamegen", model = "bridge.glb";
        if (binding != 0)
        {
            var tank = GltfDocument.Read(File.ReadAllBytes(fixture.Path(fixture.Tank)), uri => File.ReadAllBytes(fixture.Path(WorldAssembler.Relative(fixture.Tank, uri))), Token);
            List<SourceMapZoneAsset> assets = [new(alias, fixture.Tank, WorldGltf.CaptureZoneProfile(tank, token: Token), [])];
            folder = "data/m2/models/bft"; model = "alias.gltf";
            if (binding == 2)
            {
                var neutral = JsonNode.Parse(Reference("../../m1/models/m1.gltf"))!;
                var engine = (JsonObject)neutral["nodes"]![0]!["extras"]![WorldGltf.Key]!;
                engine.Remove("ref"); engine[WorldGltf.ZoneReference] = true;
                fixture.Write(bridge, neutral.ToJsonString());
                var holder = GltfDocument.Read(File.ReadAllBytes(fixture.Path(bridge)), _ => throw new InvalidDataException(), Token);
                assets.Add(new(bridge, bridge, WorldGltf.CaptureZoneProfile(holder, token: Token), [new(0, alias, "../../m2/models/bft/./alias.gltf")]));
                folder = "data/common/models"; model = "bridge.gltf";
            }
            fixture.Write(SourceMapZones.PathForMission("m2"), new SourceMapZones(assets).Write(Token));
        }
        fixture.Write("gamegen/m2.gs", $"NewWorld world\nSetModelDirectory ../{folder}\nLoadGameGen {model} copy\nFindNode copy\nFindSubNode hull\nObject3DRotate 0 1 0\nGameZWriteZBDFile world.zbd\n");
        WorldAssembler assembler = new(new SourceWorlds.DiskFiles(fixture.Project, null), Token);
        Assert.Single(assembler.Assemble("m2.gs").Nodes, n => n.Name == "hull");
        SourceWorkspace workspace = new(fixture.Project);

        var hit = SourceObjectEdits.TransformElsewhere(workspace, "m1", new() { ModelFile = fixture.Tank }, "hull", Token);
        Assert.NotNull(hit);
        Assert.Equal("m2", hit.Value.Mission);
        Assert.True(hit.Value.Certain);
        Assert.Equal("Object3DRotate", hit.Value.Instruction.Command);
        Assert.False(workspace.IsDirty);
        if (binding == 2)
        {
            // A pending retarget away from the edited geometry must remove the protection; undo restores it.
            const string ground = "data/m1/models/m1.gltf";
            var map = SourceMapZones.Parse(workspace.Read(SourceMapZones.PathForMission("m2"), Token)!, Token);
            var changed = map.Assets.Select(a => a.LogicalPath == bridge ? a with { References = [new(0, ground, "../../m1/models/m1.gltf")] } : a).ToArray();
            workspace.Apply("Retarget bridge", [(SourceMapZones.PathForMission("m2"), new SourceMapZones(changed).Write(Token))], Token);
            Assert.Null(SourceObjectEdits.TransformElsewhere(workspace, "m1", new() { ModelFile = fixture.Tank }, "hull", Token));
            workspace.Undo();
            Assert.NotNull(SourceObjectEdits.TransformElsewhere(workspace, "m1", new() { ModelFile = fixture.Tank }, "hull", Token));
        }
    }

    private static void WriteBridge(SourceWorldFixture fixture)
    {
        byte[] json = Encoding.UTF8.GetBytes(Reference("../" + fixture.Tank));
        int paddedLength = (json.Length + 3) & ~3;
        byte[] glb = new byte[20 + paddedLength];
        BinaryPrimitives.WriteUInt32LittleEndian(glb, 0x46546C67);
        BinaryPrimitives.WriteUInt32LittleEndian(glb.AsSpan(4), 2);
        BinaryPrimitives.WriteInt32LittleEndian(glb.AsSpan(8), glb.Length);
        BinaryPrimitives.WriteInt32LittleEndian(glb.AsSpan(12), paddedLength);
        BinaryPrimitives.WriteUInt32LittleEndian(glb.AsSpan(16), 0x4E4F534A);
        glb.AsSpan(20).Fill((byte)' ');
        json.CopyTo(glb, 20);
        Assert.Single(GltfDocument.Read(glb, _ => throw new InvalidDataException("No buffers are used."), Token).AllNodes());
        fixture.Write("gamegen/bridge.glb", glb);
    }

    private static string Reference(string path) => new JsonObject
    {
        ["asset"] = new JsonObject { ["version"] = "2.0" },
        ["nodes"] = new JsonArray(new JsonObject { ["extras"] = new JsonObject { ["recoil"] = new JsonObject { ["ref"] = path } } }),
        ["scenes"] = new JsonArray(new JsonObject { ["nodes"] = new JsonArray(0) }), ["scene"] = 0
    }.ToJsonString();
}
