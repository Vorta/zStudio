using System.IO;
using System.Text;
using System.Text.Json.Nodes;
using Recoil.Zbd.Core;
using Recoil.Zbd.Core.Export;
using Recoil.Zbd.Core.Sources;
using Recoil.Zbd.Core.Worlds;
using Xunit;

namespace Recoil.Zbd.Tests;

/// <summary>The Blender round trip's safety: stale checkouts, the sealed copy's place, ids, and textures Blender did not change.</summary>
public sealed class SourceBlenderTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;
    private const string Model = "data/m1/models/m1.gltf";

    [Fact]
    public void AppliedManifestChargesEscapedPathsBeforeSerializationAndLeavesTheOriginal()
    {
        using SourceWorldFixture fixture = new();
        var checkout = SourceBlender.Checkout(new SourceWorkspace(fixture.Project), Model, Token);
        string manifest = Path.Combine(checkout.Folder, "manifest.json");
        byte[] original = File.ReadAllBytes(manifest);
        string longPath = new('é', 32000);
        var changes = Enumerable.Range(0, 4096).Select(i => (Relative: longPath + i, Content: Array.Empty<byte>())).ToArray();
        var plan = new BlenderUpdatePlan("test", changes, [], checkout.Folder);
        long before = GC.GetAllocatedBytesForCurrentThread();
        Assert.Contains("4 MiB", Assert.Throws<InvalidDataException>(() => SourceBlender.RecordApplied(checkout, plan)).Message);
        Assert.InRange(GC.GetAllocatedBytesForCurrentThread() - before, 0, 4 * 1024 * 1024);
        Assert.Equal(original, File.ReadAllBytes(manifest));
    }

    [Fact]
    public void RecordingAnUpdateNeverWritesThroughAPreexistingTemporaryLink()
    {
        using SourceWorldFixture fixture = new();
        var checkout = SourceBlender.Checkout(new SourceWorkspace(fixture.Project), Model, Token);
        string sentinel = Path.Combine(fixture.Project, "outside.txt"); File.WriteAllText(sentinel, "keep");
        File.CreateSymbolicLink(Path.Combine(checkout.Folder, "manifest.json.tmp"), sentinel);
        SourceBlender.RecordApplied(checkout, new("test", [], [], checkout.Folder));
        Assert.Equal("keep", File.ReadAllText(sentinel));
        Assert.False(File.GetAttributes(Path.Combine(checkout.Folder, "manifest.json")).HasFlag(FileAttributes.ReparsePoint));
    }

    /// <summary>"Exports" the checkout's input unchanged into the outbox (below <paramref name="folder"/>), as Blender would after an edit.</summary>
    private static string Export(BlenderCheckout checkout, string folder = "edit", Action<JsonObject>? change = null)
    {
        string input = Path.GetDirectoryName(checkout.Input)!, outbox = Path.Combine(checkout.Outbox, folder);
        Directory.CreateDirectory(Path.Combine(outbox, "textures"));
        var json = JsonNode.Parse(File.ReadAllText(checkout.Input))!.AsObject();
        change?.Invoke(json);
        File.WriteAllText(Path.Combine(outbox, "m1.gltf"), json.ToJsonString());
        File.Copy(Path.Combine(input, "m1.bin"), Path.Combine(outbox, "m1.bin"), true);
        File.Copy(Path.Combine(input, "textures", "rock.png"), Path.Combine(outbox, "textures", "rock.png"), true);
        return Path.Combine(folder, "m1.gltf").Replace('\\', '/');
    }
    private static void Apply(SourceWorkspace workspace, BlenderUpdatePlan plan) => workspace.Apply(plan.Label, plan.Changes.Select(c => (c.Relative, (byte[]?)c.Content)), Token);
    private static void Rename(JsonObject gltf, string name) => gltf["nodes"]![0]!["name"] = name;

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void UnsupportedMaterialOrMorphExportIsRejectedWithoutChangingWorkspace(bool morph)
    {
        using SourceWorldFixture fixture = new();
        SourceWorkspace workspace = new(fixture.Project);
        byte[] before = workspace.Read(Model, Token)!;
        var checkout = SourceBlender.Checkout(workspace, Model, Token);
        string exported = Export(checkout, change: root =>
        {
            if (morph)
                root["meshes"]![0]!["primitives"]![0]!["targets"] = new JsonArray(new JsonObject(), new JsonObject());
            else root["materials"]![0]!["pbrMetallicRoughness"]!["baseColorFactor"] = new JsonArray(.5f, 1f, 1f, 1f);
        });
        Assert.Throws<InvalidDataException>(() => SourceBlender.PlanUpdate(workspace, checkout, exported, force: true, token: Token));
        Assert.Empty(workspace.History); Assert.Empty(workspace.Overlay()); Assert.Equal(before, workspace.Read(Model, Token));
        var plan = SourceBlender.PlanUpdate(workspace, checkout, Export(checkout, "valid", root => Rename(root, "ground2")), token: Token);
        Apply(workspace, plan);
        Assert.Single(workspace.History);
        workspace.Undo(); Assert.Equal(before, workspace.Read(Model, Token));
    }

    [Fact]
    public void AnUpdateNeverSilentlyReplacesEditsMadeSinceTheCheckout()
    {
        using SourceWorldFixture fixture = new();
        SourceWorkspace workspace = new(fixture.Project);
        var checkout = SourceBlender.Checkout(workspace, Model, Token);
        // The model changes in zStudio while Blender is open.
        var edited = JsonNode.Parse(workspace.Read(Model, Token)!)!.AsObject(); edited["nodes"]![0]!["translation"] = new JsonArray(5f, 0f, 0f);
        workspace.Apply("Move ground", [(Model, Encoding.UTF8.GetBytes(edited.ToJsonString()))], Token);
        string export = Export(checkout, change: g => Rename(g, "ground2"));
        var conflict = Assert.Throws<BlenderConflictException>(() => SourceBlender.PlanUpdate(workspace, checkout, export, token: Token));
        Assert.Equal([Model], conflict.Files);
        // With force, the export replaces the edit and says so.
        var forced = SourceBlender.PlanUpdate(workspace, checkout, export, force: true, token: Token);
        Assert.Contains(forced.Notes, n => n.StartsWith("Replaced changes", StringComparison.Ordinal));
        // Undoing the edit makes the checkout current again; an applied update is recorded, so the next export of the same checkout applies too.
        workspace.Undo();
        var plan = SourceBlender.PlanUpdate(workspace, checkout, export, token: Token);
        Apply(workspace, plan); SourceBlender.RecordApplied(checkout, plan);
        checkout = SourceBlender.Find(fixture.Project, checkout.Id);
        string again = Export(checkout, "again", g => Rename(g, "ground3"));
        Apply(workspace, SourceBlender.PlanUpdate(workspace, checkout, again, token: Token));
        Assert.Contains("ground3", Encoding.UTF8.GetString(workspace.Read(Model, Token)!));
    }

    [Fact]
    public void TexturesBlenderWroteBackUnchangedKeepTheProjectsNewerVersion()
    {
        using SourceWorldFixture fixture = new();
        SourceWorkspace workspace = new(fixture.Project);
        var checkout = SourceBlender.Checkout(workspace, Model, Token);
        // Another edit changes the shared texture after the checkout.
        var image = PngDecoder.Decode(workspace.Read("data/m1/textures/rock.png", Token)!, token: Token); image.Rgba[0] ^= 0xFF;
        byte[] newer = PngEncoder.Encode(image, Token);
        workspace.Apply("Repaint rock", [("data/m1/textures/rock.png", newer)], Token);
        var plan = SourceBlender.PlanUpdate(workspace, checkout, Export(checkout, change: g => Rename(g, "ground2")), token: Token);
        Assert.DoesNotContain(plan.Changes, c => c.Relative == "data/m1/textures/rock.png");
        Apply(workspace, plan);
        Assert.Equal(newer, workspace.Read("data/m1/textures/rock.png", Token));
        // A texture Blender did change, over a newer project version, is a conflict.
        string outbox = Path.Combine(checkout.Outbox, "edit", "textures", "rock.png");
        var painted = PngDecoder.Decode(File.ReadAllBytes(outbox), token: Token); painted.Rgba[4] ^= 0xFF; File.WriteAllBytes(outbox, PngEncoder.Encode(painted, Token));
        workspace.Undo();
        Assert.Contains("data/m1/textures/rock.png", Assert.Throws<BlenderConflictException>(() => SourceBlender.PlanUpdate(workspace, checkout, "edit/m1.gltf", token: Token)).Files);
        // An unreadable PNG is refused before anything changes.
        File.WriteAllBytes(outbox, [0x89, 0x50, 0x4E, 0x47, 1, 2, 3]);
        Assert.Contains("not a readable PNG", Assert.Throws<InvalidDataException>(() => SourceBlender.PlanUpdate(workspace, checkout, "edit/m1.gltf", force: true, token: Token)).Message);
    }

    [Fact]
    public void ExportsThatDropEngineAttributesOrReplaceOtherTexturesAskFirst()
    {
        using SourceWorldFixture fixture = new();
        // Another model's texture, which this model's checkout does not hold.
        var image = PngDecoder.Decode(File.ReadAllBytes(fixture.Path("data/m1/textures/rock.png")), token: Token); image.Rgba[0] ^= 0xFF;
        fixture.Write("data/m1/textures/grass.png", PngEncoder.Encode(image, Token));
        SourceWorkspace workspace = new(fixture.Project);
        var checkout = SourceBlender.Checkout(workspace, Model, Token);
        Assert.Contains("\"recoil\"", File.ReadAllText(checkout.Input));
        // Blender's default (Custom Properties off) writes no extras: the update says what would be lost and asks.
        string bare = Export(checkout, "bare", g =>
        {
            foreach (string key in new[] { "nodes", "meshes", "materials", "scenes" })
                foreach (var item in g[key] as JsonArray ?? []) (item as JsonObject)?.Remove("extras");
        });
        var missing = Assert.Throws<BlenderConflictException>(() => SourceBlender.PlanUpdate(workspace, checkout, bare, token: Token));
        Assert.Contains("Custom Properties", missing.Message);
        Assert.Contains(SourceBlender.PlanUpdate(workspace, checkout, bare, force: true, token: Token).Notes, n => n.Contains("Custom Properties", StringComparison.Ordinal));
        // An exported texture named like a project texture outside the checkout would replace it for every model.
        string named = Export(checkout, "named", g => g["images"]![0]!["uri"] = "textures/grass.png");
        File.Copy(Path.Combine(checkout.Outbox, "named", "textures", "rock.png"), Path.Combine(checkout.Outbox, "named", "textures", "grass.png"));
        Assert.Contains("data/m1/textures/grass.png", Assert.Throws<BlenderConflictException>(() => SourceBlender.PlanUpdate(workspace, checkout, named, token: Token)).Files);
        // Both at once: one conflict names both, so "update anyway" never accepts one it did not show.
        string both = Export(checkout, "both", g =>
        {
            g["images"]![0]!["uri"] = "textures/grass.png";
            foreach (string key in new[] { "nodes", "meshes", "materials", "scenes" })
                foreach (var item in g[key] as JsonArray ?? []) (item as JsonObject)?.Remove("extras");
        });
        File.Copy(Path.Combine(checkout.Outbox, "both", "textures", "rock.png"), Path.Combine(checkout.Outbox, "both", "textures", "grass.png"));
        var combined = Assert.Throws<BlenderConflictException>(() => SourceBlender.PlanUpdate(workspace, checkout, both, token: Token));
        Assert.Contains("Custom Properties", combined.Message); Assert.Contains("grass.png", combined.Message);
        Assert.Contains("data/m1/textures/grass.png", combined.Files);
        // Refused updates leave no sealed copy behind; the forced one above left its own.
        Assert.Single(Directory.GetDirectories(Path.Combine(checkout.Folder, "sealed")));
    }

    [Fact]
    public void CheckoutsShowTransparentTexturesAndHideCollisionVolumes()
    {
        using SourceWorldFixture fixture = new();
        // A pickup model written before models carried these hints: a glow texture with graded alpha, and its collision volume.
        byte[] rgba = new byte[4 * 4 * 4];
        for (int i = 0; i < 16; i++) { rgba[i * 4] = 255; rgba[i * 4 + 3] = (byte)(i * 16); }
        fixture.Write("data/m1/textures/glow.png", PngEncoder.Encode(new DecodedImage(4, 4, rgba), Token));
        WorldNode Node(string name, WorldMaterial material)
        {
            ModelBuilder builder = new();
            builder.Add(new([new(0, 0, 0), new(1, 0, 0), new(1, 0, -1), new(0, 0, -1)], [new(0, 0), new(1, 0), new(1, 1), new(0, 1)], [], [], material));
            WorldNode node = new(name, WorldNodeClass.Object3D) { Model = builder.Finish(), Flags = WorldGltf.DefaultCarried }; node.SetPayloadInt(0, 0x28);
            return node;
        }
        var (json, bin) = WorldGltf.Export([Node("box", new() { Texture = new("glow"), Flags = 0x1FF }), Node("bvol", new() { Color = new(63, 15, 254), Flags = 0xFF })], 0xFF,
            new() { Texture = t => ($"../textures/{t.Name}.png", 0) }).Write("ammo.bin");
        Assert.DoesNotContain("alphaMode", Encoding.UTF8.GetString(json));
        fixture.Write("data/m1/models/ammo.gltf", json); fixture.Write("data/m1/models/ammo.bin", bin);
        // m1's build loads it as a pickup (from the model folder its script set), whose collision volume the game switches off.
        fixture.Write("gamegen/support/pickup.gw", "LoadGameGen ammo.gltf pu012\r\n");
        fixture.Write("gamegen/m1.gs", File.ReadAllText(fixture.Path("gamegen/m1.gs")).Replace("# no vehicles", "source support\\pickup.gw", StringComparison.Ordinal));

        var checkout = SourceBlender.Checkout(new SourceWorkspace(fixture.Project), "data/m1/models/ammo.gltf", Token);
        var materials = JsonNode.Parse(File.ReadAllText(checkout.Input))!["materials"]!.AsArray();
        Assert.Equal(["BLEND", "MASK"], materials.Select(m => (string?)m!["alphaMode"]));
        Assert.Equal(["glow~flat", "color_3F0FFE~flat~hidden"], materials.Select(m => (string?)m!["name"]));
        Assert.Equal(0, materials[1]!["pbrMetallicRoughness"]!["baseColorFactor"]![3]!.GetValue<double>());
        // The project's model is unchanged; only the copy Blender opens shows the hints.
        Assert.Equal(json, File.ReadAllBytes(fixture.Path("data/m1/models/ammo.gltf")));
    }

    [Fact]
    public void SealingStaysInsideTheCheckoutAndIdsCanBeFoundAgain()
    {
        using SourceWorldFixture fixture = new();
        // A model whose name has a space and a '+'.
        fixture.Write("data/m1/models/a b+c.gltf", File.ReadAllBytes(fixture.Path(Model)));
        SourceWorkspace workspace = new(fixture.Project);
        var odd = SourceBlender.Checkout(workspace, "data/m1/models/a b+c.gltf", Token);
        Assert.Equal(odd.Id, SourceBlender.Find(fixture.Project, odd.Id).Id);
        Assert.Throws<InvalidDataException>(() => SourceBlender.Find(fixture.Project, ".."));
        // A deep export that names a buffer above its own folder but inside the outbox: the sealed copy keeps the outbox layout.
        var checkout = SourceBlender.Checkout(workspace, Model, Token);
        string deep = Path.Combine(checkout.Outbox, "a", "b", "c", "d", "e");
        Directory.CreateDirectory(Path.Combine(deep, "textures")); Directory.CreateDirectory(Path.Combine(checkout.Outbox, "data"));
        var json = JsonNode.Parse(File.ReadAllText(checkout.Input))!.AsObject();
        json["buffers"]![0]!["uri"] = "../../../../../data/m1.bin";
        File.WriteAllText(Path.Combine(deep, "m1.gltf"), json.ToJsonString());
        File.Copy(Path.Combine(Path.GetDirectoryName(checkout.Input)!, "m1.bin"), Path.Combine(checkout.Outbox, "data", "m1.bin"));
        File.Copy(Path.Combine(Path.GetDirectoryName(checkout.Input)!, "textures", "rock.png"), Path.Combine(deep, "textures", "rock.png"));
        byte[] before = File.ReadAllBytes(fixture.Path("data/m1/models/m1.bin"));
        var plan = SourceBlender.PlanUpdate(workspace, checkout, "a/b/c/d/e/m1.gltf", token: Token);
        Assert.True(File.Exists(Path.Combine(plan.Sealed, "data", "m1.bin")));
        Assert.StartsWith(Path.Combine(checkout.Folder, "sealed"), plan.Sealed);
        Assert.Equal(before, File.ReadAllBytes(fixture.Path("data/m1/models/m1.bin")));
        Assert.False(File.Exists(fixture.Path("data/m1.bin")));
    }
}
