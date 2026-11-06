using System.Numerics;
using zStudio.Core.Sources;
using Xunit;

namespace zStudio.Tests;

/// <summary>Required asset admission: allowed nested inputs must build/export; rejected inputs must never become edits.</summary>
public sealed class SourcePlacementAdmissionTests
{
    [Fact]
    public async Task CurrentMapNestedAssetsAreAcceptedAndOtherMapsAndTextureShadowingAreRejected()
    {
        using SourceWorldFixture fixture = new();
        var token = TestContext.Current.CancellationToken;
        SourceWorkspace workspace = new(fixture.Project);
        SourceModelAddition addition = new(fixture.Tank, "new_tank", Vector3.Zero);
        Assert.Contains(fixture.Tank, SourceWorlds.Models(workspace, "m2", token).Select(m => m.Path));
        Assert.DoesNotContain(fixture.Tank, SourceWorlds.Models(workspace, "m1", token).Select(m => m.Path));
        string error = Assert.Throws<InvalidDataException>(() => SourceWorlds.ValidatePlacement(workspace, "m1", addition, token)).Message;
        Assert.Contains("data/m1/models/", error); Assert.Contains("data/m1/textures/", error); Assert.False(workspace.IsDirty);
        var assets = SourceWorlds.ValidatePlacement(workspace, "m2", addition, token);
        var prepared = workspace.BeginPreparedEdit();
        SourceWorlds.AddModel(prepared.Workspace, "m2", new(addition, []), token);
        var built = await SourceWorlds.LoadSceneAsync(fixture.Project, "m2", prepared.Workspace.Overlay(), token, [addition]);
        SourceWorlds.ValidatePlacementTextures(assets, built, token);
        Assert.False(workspace.IsDirty);
        SourceWorlds.AddModel(workspace, "m2", new(addition, []), token); workspace.Save(token);
        var exported = Path.Combine(fixture.Root, "placed-game");
        var report = await SourceBuilder.ExportAsync(fixture.Project, exported, ["m2/gamez.zbd", "m2/rtexture4.zbd", "m2/rgbatexture64.zbd"], token: token, target: ExportTarget.V2);
        Assert.Equal(0, report.Failed);
        foreach (string pack in new[] { "rtexture4.zbd", "rgbatexture64.zbd" })
            Assert.Contains(zStudio.Core.Formats.FormatRegistry.Default.OpenBytes(pack, File.ReadAllBytes(Path.Combine(exported, "m2", pack)), token: token).Assets, a => a.Name == "camo");
        Assert.Contains((await SourceWorlds.LoadSceneAsync(fixture.Project, "m2", token: token)).Scene!.Nodes, n => n.Name == "new_tank");
        // A later name-only material must not discard the physical identity established by an explicit URI.
        var gltf = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllBytes(fixture.Path(fixture.Tank)))!;
        var material = gltf["materials"]![0]!.DeepClone();
        material["pbrMetallicRoughness"]!.AsObject().Remove("baseColorTexture");
        gltf["materials"]!.AsArray().Add(material);
        var primitive = gltf["meshes"]![0]!["primitives"]![0]!.DeepClone(); primitive["material"] = 1;
        gltf["meshes"]![0]!["primitives"]!.AsArray().Add(primitive);
        fixture.Write(fixture.Tank, gltf.ToJsonString());
        var merged = SourceWorlds.ValidatePlacement(new(fixture.Project), "m2", addition, token);
        Assert.Equal("data/m2/textures/bft/camo.png", merged.Textures["camo"]);
        var shadowed = new SourcePlacementAssets(assets.Names, new Dictionary<string, string?> { ["camo"] = "data/common/textures/camo.png" });
        Assert.Contains("instead of", Assert.Throws<InvalidDataException>(() => SourceWorlds.ValidatePlacementTextures(shadowed, built, token)).Message);
        File.Delete(fixture.Path("data/m2/textures/bft/camo.png"));
        Assert.Contains("existing external PNG", Assert.Throws<InvalidDataException>(() => SourceWorlds.ValidatePlacement(new(fixture.Project), "m2", addition, token)).Message);
    }
}
