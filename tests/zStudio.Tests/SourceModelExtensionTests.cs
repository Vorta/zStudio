using System.Buffers.Binary;
using System.IO;
using System.Text;
using Recoil.Zbd.Core.Formats;
using Recoil.Zbd.Core.Sources;
using Recoil.Zbd.Core.Worlds;
using Xunit;

namespace Recoil.Zbd.Tests;

public sealed class SourceModelExtensionTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Fact]
    public async Task ExplicitGlbAdditionCanBePreparedValidatedAcceptedAndUndoneWithoutChangingSourceFiles()
    {
        using SourceWorldFixture fixture = new();
        WriteSiblingModels(fixture);
        string[] originals = ["gamegen/m1.gs", "data/m1/models/tank.gltf", "data/m1/models/tank.glb"];
        var bytes = originals.ToDictionary(p => p, p => File.ReadAllBytes(fixture.Path(p)));
        SourceWorkspace workspace = new(fixture.Project);
        var prepared = workspace.BeginPreparedEdit();
        SourceModelAddition model = new("data/m1/models/tank.glb", "new_tank", new(10, 0, 20));
        var transaction = SourceWorlds.AddModel(prepared.Workspace, "m1", new(model, []), Token);
        Assert.False(workspace.IsDirty);
        Assert.True(prepared.Workspace.IsDirty);
        Assert.Contains("LoadGameGen tank.glb new_tank", Encoding.Latin1.GetString(prepared.Workspace.Read("gamegen/m1.gs", Token)!));
        var build = await SourceWorlds.BuildPreviewAsync(fixture.Project, "m1", Path.Combine(SourceWorlds.PreviewRoot(fixture.Project), "glb-add"), prepared.Workspace.Overlay(), token: Token, additions: [model]);
        Assert.All(build.Outputs, output => Assert.Equal("built", output.Status));
        var world = GameZWorldReader.FromDocument(FormatRegistry.Default.OpenBytes("gamez.zbd", await File.ReadAllBytesAsync(build.WorldPath, Token), token: Token), Token);
        Assert.Single(world.Nodes, node => node.Name == "glb_content");
        Assert.DoesNotContain(world.Nodes, node => node.Name == "gltf_content");
        var added = Assert.Single(world.Nodes, node => node.Name == "new_tank");
        Assert.Equal(WorldNodeClass.World, Assert.Single(added.Parents).Class);
        using (var verified = workspace.VerifyPreparedEdit(prepared, Token))
            Assert.Equal(transaction.Id, workspace.AcceptPreparedEdit(prepared, Token, verified)!.Id);
        Assert.True(workspace.IsDirty); Assert.Equal(1, workspace.UndoCount);
        WorldAssembler assembler = new(new SourceWorlds.DiskFiles(fixture.Project, workspace.Overlay()), Token);
        Assert.Single(assembler.Assemble("m1.gs").Nodes, node => node.Name == "glb_content");
        foreach (string file in originals) Assert.Equal(bytes[file], File.ReadAllBytes(fixture.Path(file)));
        workspace.Undo();
        Assert.False(workspace.IsDirty);
        Assert.Equal(bytes["gamegen/m1.gs"], workspace.Read("gamegen/m1.gs", Token));
        WorldAssembler undone = new(new SourceWorlds.DiskFiles(fixture.Project, workspace.Overlay()), Token);
        Assert.DoesNotContain(undone.Assemble("m1.gs").Nodes, node => node.Name is "new_tank" or "glb_content");
        Assert.Null(Assert.Single((await SourceBuilder.CheckAsync(fixture.Project, ["m1/gamez.zbd"], token: Token)).Outputs).Error);
    }

    [Theory]
    [InlineData(".glb", "glb_content")]
    [InlineData(".GLB", "glb_content")]
    [InlineData(".gltf", "gltf_content")]
    [InlineData("", "gltf_content")]
    [InlineData(".flt", "gltf_content")]
    public async Task ModelAdditionHonorsExplicitExtensionAndLegacyPreference(string extension, string expected)
    {
        using SourceWorldFixture fixture = new();
        WriteSiblingModels(fixture);
        const string folder = "data/m1/models/";
        string explicitExtension = extension is "" or ".flt" ? ".gltf" : extension;
        SourceModelAddition addition = new(folder + "tank" + explicitExtension, "new_tank");
        SourceWorlds.Validate(fixture.Project, addition);
        var lines = SourceWorlds.ScriptLines(addition);
        Assert.Contains("LoadGameGen tank" + explicitExtension + " new_tank", lines);
        string script = Encoding.Latin1.GetString(File.ReadAllBytes(fixture.Path("gamegen/m1.gs")));
        string load = string.Join("\r\n", lines).Replace("LoadGameGen tank" + explicitExtension + " ", "LoadGameGen tank" + extension + " ", StringComparison.Ordinal);
        fixture.Write("gamegen/m1.gs", script.Replace("GameZWriteZBDFile", load + "\r\nGameZWriteZBDFile", StringComparison.Ordinal));
        WorldAssembler assembler = new(new SourceWorlds.DiskFiles(fixture.Project, null), Token);
        var world = assembler.Assemble("m1.gs");
        Assert.Single(world.Nodes, n => n.Name == expected);
        Assert.DoesNotContain(world.Nodes, n => n.Name == (expected == "glb_content" ? "gltf_content" : "glb_content"));
        Assert.NotEmpty(GameZWriter.Write(world, Token));
        var report = await SourceBuilder.CheckAsync(fixture.Project, ["m1/gamez.zbd"], token: Token);
        Assert.Null(Assert.Single(report.Outputs).Error);
    }

    private static void WriteSiblingModels(SourceWorldFixture fixture)
    {
        const string folder = "data/m1/models/";
        string Json(string name) => "{\"asset\":{\"version\":\"2.0\"},\"nodes\":[{\"name\":\"" + name + "\"}]}";
        fixture.Write(folder + "tank.gltf", Json("gltf_content"));
        byte[] json = Encoding.UTF8.GetBytes(Json("glb_content"));
        int padded = (json.Length + 3) & ~3;
        byte[] glb = new byte[20 + padded];
        "glTF"u8.CopyTo(glb); BinaryPrimitives.WriteInt32LittleEndian(glb.AsSpan(4), 2);
        BinaryPrimitives.WriteInt32LittleEndian(glb.AsSpan(8), glb.Length); BinaryPrimitives.WriteInt32LittleEndian(glb.AsSpan(12), padded);
        BinaryPrimitives.WriteUInt32LittleEndian(glb.AsSpan(16), 0x4e4f534a); glb.AsSpan(20).Fill(32); json.CopyTo(glb, 20);
        fixture.Write(folder + "tank.glb", glb);
    }
}
