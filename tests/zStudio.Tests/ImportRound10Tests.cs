using System.Text;
using System.Buffers.Binary;
using System.Text.Json.Nodes;
using zStudio.Core.Gltf;
using zStudio.Core.Sources;
using zStudio.Core.Worlds;
using Xunit;

namespace zStudio.Tests;

public sealed class ImportRound10Tests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;
    [Fact]
    public async Task BuildsAndPreviewInputsCannotUseACheckoutBuffer()
    {
        using SourceWorldFixture fixture = new();
        const string path = "data/m1/models/m1.gltf";
        var json = JsonNode.Parse(File.ReadAllBytes(Path.Combine(fixture.Project, path)))!;
        json["buffers"]![0]!["uri"] = "../../../zstudio/export/id/input/mesh.bin";
        File.WriteAllText(Path.Combine(fixture.Project, path), json.ToJsonString());
        var report = await SourceBuilder.CheckAsync(fixture.Project, ["m1/gamez.zbd"], token: Token);
        Assert.Contains(report.Outputs, o => o.Error?.Contains("authoritative source", StringComparison.Ordinal) == true);
        var files = new SourceWorlds.DiskFiles(fixture.Project, new Dictionary<string, byte[]> { ["zstudio/export/id/input/mesh.bin"] = [1] });
        Assert.Throws<InvalidDataException>(() => files.Exists("zstudio/export/id/input/mesh.bin"));
        Assert.Throws<InvalidDataException>(() => files.Read("zstudio/export/id/input/mesh.bin", Token));
    }
    [Theory]
    [InlineData("\"pbrMetallicRoughness\":{\"metallicFactor\":0.5}")]
    [InlineData("\"pbrMetallicRoughness\":{\"metallicFactor\":0,\"roughnessFactor\":0.2}")]
    [InlineData("\"pbrMetallicRoughness\":{\"metallicRoughnessTexture\":{\"index\":0}}")]
    [InlineData("\"normalTexture\":{\"index\":0}")]
    [InlineData("\"occlusionTexture\":{\"index\":0}")]
    [InlineData("\"emissiveTexture\":{\"index\":0}")]
    [InlineData("\"pbrMetallicRoughness\":{\"metallicFactor\":0},\"emissiveFactor\":[1,0,0]")]
    public void AuthoredUnsupportedMaterialChannelsAreRefused(string field)
    {
        byte[] json = Encoding.UTF8.GetBytes("{\"asset\":{\"version\":\"2.0\"},\"materials\":[{" + field + "}]}");
        Assert.Contains("material", Assert.Throws<InvalidDataException>(() => GltfDocument.Read(json, _ => [], Token)).Message);
        Assert.Empty(GltfDocument.Read("{\"asset\":{\"version\":\"2.0\"},\"materials\":[{\"pbrMetallicRoughness\":{\"metallicFactor\":0,\"roughnessFactor\":1},\"emissiveFactor\":[0,0,0]}]}"u8, _ => [], Token).Roots);
    }

    [Theory]
    [InlineData("extras")][InlineData("unknown")]
    public void HugeJsonIsRefusedBeforeDomAllocation(string property)
    {
        byte[] json = Encoding.UTF8.GetBytes("{\"asset\":{\"version\":\"2.0\"},\"" + property + "\":\"" + new string('x', 33 * 1024 * 1024) + "\"}");
        // Warm the path without including the caller-owned input in the allocation measurement.
        GltfDocument.Read("{\"asset\":{\"version\":\"2.0\"}}"u8, _ => [], Token);
        long before = GC.GetAllocatedBytesForCurrentThread();
        Assert.Throws<InvalidDataException>(() => GltfDocument.Read(json, _ => [], Token));
        Assert.True(GC.GetAllocatedBytesForCurrentThread() - before < 1024 * 1024);
        byte[] glb = new byte[20 + (json.Length + 3) / 4 * 4]; glb.AsSpan().Fill(32);
        "glTF"u8.CopyTo(glb); BinaryPrimitives.WriteUInt32LittleEndian(glb.AsSpan(4), 2);
        BinaryPrimitives.WriteInt32LittleEndian(glb.AsSpan(8), glb.Length);
        BinaryPrimitives.WriteInt32LittleEndian(glb.AsSpan(12), glb.Length - 20);
        BinaryPrimitives.WriteUInt32LittleEndian(glb.AsSpan(16), 0x4E4F534A); json.CopyTo(glb, 20);
        before = GC.GetAllocatedBytesForCurrentThread();
        Assert.Throws<InvalidDataException>(() => GltfDocument.Read(glb, _ => [], Token));
        Assert.True(GC.GetAllocatedBytesForCurrentThread() - before < 1024 * 1024);
    }

    [Fact]
    public void ManyTinyUnknownValuesAreBoundedBeforeTheTokenTable()
    {
        byte[] json = Encoding.UTF8.GetBytes("{\"asset\":{\"version\":\"2.0\"},\"unknown\":[" + string.Join(',', Enumerable.Repeat("0", 4_000_001)) + "]}");
        GltfDocument.Read("{\"asset\":{\"version\":\"2.0\"}}"u8, _ => [], Token);
        long before = GC.GetAllocatedBytesForCurrentThread();
        Assert.Contains("tokens", Assert.Throws<InvalidDataException>(() => GltfDocument.Read(json, _ => [], Token)).Message);
        Assert.True(GC.GetAllocatedBytesForCurrentThread() - before < 1024 * 1024);
    }

    [Theory]
    [InlineData("../zstudio/export/id/input")][InlineData("../other/models")][InlineData("../DATA/../zstudio")]
    public void ScriptSearchesCannotNameWorkingOrNonSourceFolders(string path)
    {
        Assert.Null(WorldAssembler.ProjectPath(path));
        Assert.Equal("data/common/models", WorldAssembler.ProjectPath("..\\data\\common\\models"));
        Assert.Equal("gamegen/models", WorldAssembler.ProjectPath("../gamegen/models"));
    }

    [Fact]
    public async Task TerrainConversionPreservesImplicitRootsAndRefusesConflictingOutputsOrOwners()
    {
        using SourceWorldFixture fixture = new(); fixture.WriteTerrainDatabase();
        const string path = "data/m1/models/m1.gltf";
        string full = Path.Combine(fixture.Project, path);
        var json = JsonNode.Parse(File.ReadAllBytes(full))!.AsObject(); json.Remove("scene"); json.Remove("scenes");
        File.WriteAllText(full, json.ToJsonString());
        SourceWorkspace workspace = new(fixture.Project);
        var plan = SourceTerrainConversion.Plan(workspace, path, (new(StringComparer.Ordinal) { "ground" }, []), Token);
        Assert.True(plan.Converted > 0);
        byte[] original = File.ReadAllBytes(full);
        // An external export during conversion confirmation must never become an overwrite baseline.
        foreach (string output in new[] { plan.Surfaces, plan.Surfaces[..^".gltf".Length] + ".bin" }.Concat(plan.Recipes))
        {
            fixture.Write(output, original);
            var blocked = workspace.BeginPreparedEdit();
            Assert.Contains("already exists", Assert.Throws<InvalidDataException>(() =>
                SourceTerrainConversion.Apply(blocked.Workspace, plan, Token)).Message);
            Assert.False(blocked.Workspace.IsDirty);
            Assert.False(workspace.CanUndo);
            Assert.Equal(original, File.ReadAllBytes(fixture.Path(output)));
            Assert.Equal(original, File.ReadAllBytes(full));
            File.Delete(fixture.Path(output));
        }
        // A later appearance must also prevent prepared publication.
        var late = workspace.BeginPreparedEdit();
        SourceTerrainConversion.Apply(late.Workspace, plan, Token);
        fixture.Write(plan.Surfaces, original);
        Assert.Throws<SourceFileChangedException>(() => workspace.VerifyPreparedEdit(late, Token));
        Assert.False(workspace.IsDirty);
        Assert.Equal(original, File.ReadAllBytes(fixture.Path(plan.Surfaces)));
        File.Delete(fixture.Path(plan.Surfaces));

        var prepared = workspace.BeginPreparedEdit();
        SourceTerrainConversion.Apply(prepared.Workspace, plan, Token);
        using (var verified = workspace.VerifyPreparedEdit(prepared, Token))
            Assert.NotNull(workspace.AcceptPreparedEdit(prepared, Token, verified));
        var edited = JsonNode.Parse(workspace.Read(path, Token)!)!;
        var roots = edited["scenes"]![0]!["nodes"]!.AsArray().Select(n => edited["nodes"]![n!.GetValue<int>()]!["name"]!.GetValue<string>());
        Assert.Equal(["ground", "m1_terrain", "sky"], roots);
        Assert.True(workspace.CanUndo);

        // A second master references this database and owns its placement overrides in that reference node.
        const string profile = "data/m2/models/m1.gltf";
        fixture.Write(profile, """
            {"asset":{"version":"2.0"},"nodes":[{"name":"shared_database","extras":{"recoil":{"class":"group","ref":"../../m1/models/m1.gltf"}}}],"scenes":[{"nodes":[0]}],"scene":0}
            """);
        byte[] referenceSource = File.ReadAllBytes(fixture.Path(profile));
        fixture.Write("gamegen/m2.gs", File.ReadAllText(fixture.Path("gamegen/m1.gs"))
            .Replace("SetModelDirectory ..\\data\\m1\\models", "SetModelDirectory ..\\data\\m2\\models", StringComparison.Ordinal)
            .Replace("..\\m1\\gamez.zbd", "..\\m2\\gamez.zbd", StringComparison.Ordinal));
        async Task<SourceSceneState> BuildM2(string label, SourceWorkspace? pending = null) => (await SourceWorlds.LoadSceneAsync(fixture.Project, "m2", pending?.Overlay(), token: Token)).BuildState;
        var m2 = await BuildM2("before-shared-zones");
        int ground = m2.Provenance.First(p => p.Value.ModelNodeName == "ground").Key;
        SourceWorkspace other = new(fixture.Project);
        var otherPrepared = other.BeginPreparedEdit();
        var zoneEdit = SourceZoneEdits.PlanAssignments(otherPrepared.Workspace, m2, [new(ground)], new(NodeZone: 7), false, Token);
        otherPrepared.Workspace.Apply(zoneEdit.Label, zoneEdit.Changes.Select(c => (c.Relative, (byte[]?)c.Content)), Token);
        await BuildM2("prepared-shared-zones", otherPrepared.Workspace);
        using (var verified = other.VerifyPreparedEdit(otherPrepared, Token))
            other.AcceptPreparedEdit(otherPrepared, Token, verified);
        other.Save(Token);
        byte[] zones = File.ReadAllBytes(fixture.Path(profile));
        // Acceptance does not end the dependency: a second editor can initialize a profile before we save.
        await Assert.ThrowsAsync<InvalidDataException>(() => workspace.SaveAsync(Token));
        Assert.False(workspace.IsSaving); Assert.True(workspace.IsDirty); Assert.True(workspace.CanUndo);
        Assert.Equal(original, File.ReadAllBytes(full));
        Assert.False(File.Exists(fixture.Path(plan.Surfaces)));
        Assert.Equal(zones, File.ReadAllBytes(fixture.Path(profile)));
        workspace.Undo(); Assert.False(workspace.IsDirty);
        Assert.False(workspace.Exists("data/m1/meta/zones.json", Token));
        Assert.Throws<InvalidDataException>(() => SourceTerrainConversion.Plan(workspace, path,
            (new(StringComparer.Ordinal) { "ground" }, []), Token));
        var conflicting = workspace.BeginPreparedEdit();
        Assert.Throws<InvalidDataException>(() => SourceTerrainConversion.Apply(conflicting.Workspace, plan, Token));
        Assert.False(conflicting.Workspace.IsDirty);
        Assert.False(workspace.IsDirty); Assert.False(workspace.CanUndo);
        Assert.Equal(original, File.ReadAllBytes(full));
        Assert.Equal(zones, File.ReadAllBytes(fixture.Path(profile)));
        await BuildM2("after-refused-conversion");

        // Even without overrides, converting a referenced file would put terrain outside a master's roots.
        other.Discard(); fixture.Write(profile, referenceSource);
        Assert.Throws<InvalidDataException>(() => SourceTerrainConversion.Plan(new SourceWorkspace(fixture.Project), path,
            (new(StringComparer.Ordinal) { "ground" }, []), Token));

        // Reverse publication order on a direct master: pending inline zones must not overwrite a later conversion.
        fixture.Write(profile, """{"asset":{"version":"2.0"},"nodes":[],"scenes":[{"nodes":[]}],"scene":0}""");
        fixture.Write("gamegen/m2.gs", File.ReadAllText(fixture.Path("gamegen/m1.gs"))
            .Replace("..\\m1\\gamez.zbd", "..\\m2\\gamez.zbd", StringComparison.Ordinal));
        other = new(fixture.Project);
        var reverseBuild = await BuildM2("before-reverse-save");
        ground = reverseBuild.Provenance.First(p => p.Value.ModelNodeName == "ground").Key;
        var reverseZones = SourceZoneEdits.PlanAssignments(other, reverseBuild, [new(ground)], new(NodeZone: 7), true, Token);
        other.Apply(reverseZones.Label, reverseZones.Changes.Select(c => (c.Relative, (byte[]?)c.Content)), Token);
        SourceWorkspace converted = new(fixture.Project);
        var conversion = converted.BeginPreparedEdit();
        SourceTerrainConversion.Apply(conversion.Workspace, plan, Token);
        using (var verified = converted.VerifyPreparedEdit(conversion, Token))
            converted.AcceptPreparedEdit(conversion, Token, verified);
        converted.Save(Token);
        Assert.ThrowsAny<IOException>(() => other.Save(Token));
        Assert.True(other.IsDirty); Assert.False(other.IsSaving);
        Assert.NotEqual(original, File.ReadAllBytes(full));
        await BuildM2("after-refused-zone-save");
    }
}
