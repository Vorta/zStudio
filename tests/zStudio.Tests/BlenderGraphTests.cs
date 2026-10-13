using System.Numerics;
using System.Diagnostics;
using System.Text;
using System.Text.Json.Nodes;
using Recoil.Zbd.Core.Gltf;
using Recoil.Zbd.Core.Sources;
using Recoil.Zbd.Core.Worlds;
using Xunit;

namespace Recoil.Zbd.Tests;

public sealed class BlenderGraphTests
{
    // Optional independent application integration; ordinary .NET builds and tests need no Python or Blender.
    [Fact]
    public async Task InstalledBlenderRoundTrip()
    {
        if (Environment.GetEnvironmentVariable("ZSTUDIO_BLENDER") is not { Length: > 0 } blender) return;
        string helper = Environment.GetEnvironmentVariable("ZSTUDIO_BLENDER_HELPER") ?? throw new InvalidOperationException("Set ZSTUDIO_BLENDER_HELPER to the validated portable zStudio.exe.");
        var repository = new DirectoryInfo(AppContext.BaseDirectory);
        while (repository != null && !File.Exists(Path.Combine(repository.FullName, "zStudio.slnx"))) repository = repository.Parent;
        Assert.NotNull(repository);
        string folder = Path.Combine(Path.GetTempPath(), "zstudio-blender-integration-" + Guid.NewGuid().ToString("N"));
        CreateFixture(Path.Combine(folder, "source"), missingImage: true);
        using Process process = new();
        bool started = false;
        try
        {
            process.StartInfo = new(blender) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
            process.StartInfo.Environment["BLENDER_USER_RESOURCES"] = Path.Combine(folder, "preferences");
            foreach (string arg in new[] { "--background", "--factory-startup", "--python-exit-code", "1", "--python", Path.Combine(repository.FullName, "tools", "blender-roundtrip.py"), "--", repository.FullName, helper, Path.Combine(folder, "source"), Path.Combine(folder, "output") }) process.StartInfo.ArgumentList.Add(arg);
            started = process.Start(); Assert.True(started);
            var stdout = process.StandardOutput.ReadToEndAsync(TestContext.Current.CancellationToken);
            var stderr = process.StandardError.ReadToEndAsync(TestContext.Current.CancellationToken);
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken); timeout.CancelAfter(TimeSpan.FromMinutes(3));
            await process.WaitForExitAsync(timeout.Token);
            Assert.True(process.ExitCode == 0, await stdout + await stderr);
            Assert.Contains("ZSTUDIO_BLENDER_ROUNDTRIP_PASS", await stdout);
        }
        finally
        {
            if (started && !process.HasExited) { process.Kill(entireProcessTree: true); await process.WaitForExitAsync(CancellationToken.None); }
            Directory.Delete(folder, recursive: true);
        }
    }

    [Fact]
    public void GraphUpdateUsesOneUndoableVerifiedSourceTransaction()
    {
        var token = TestContext.Current.CancellationToken;
        using SourceWorldFixture fixture = new();
        CreateFixture(fixture.Path("data/m1/models"));
        fixture.Write("data/m1/models/m1.gltf", File.ReadAllBytes(fixture.Path("data/m1/models/world.gltf")));
        const string model = "data/m1/models/m1.gltf", part = "data/m1/models/part.gltf";
        var workspace = new SourceWorkspace(fixture.Project);
        const string other = "data/m1/models/other.gltf";
        var otherJson = JsonNode.Parse(File.ReadAllBytes(fixture.Path(model)))!;
        var partDocument = GltfDocument.Read(File.ReadAllBytes(fixture.Path(part)), uri => File.ReadAllBytes(fixture.Path("data/m1/models/" + uri)), token);
        var otherProfile = WorldGltf.WriteZoneOverride(WorldGltf.CaptureZoneProfile(partDocument, token: token), token);
        otherProfile["meshes"]![0]!["polygonZones"] = new JsonArray(new JsonArray(9));
        otherJson["nodes"]![0]!["extras"]!["recoil"]!["zoneOverrides"] = otherProfile;
        fixture.Write(other, Encoding.UTF8.GetBytes(otherJson.ToJsonString()));
        var checkout = SourceBlender.Checkout(workspace, model, token);
        Assert.Equal(2, SourceBlender.Find(fixture.Project, checkout.Id).Version);
        var baseline = workspace.Read(part, token)!;
        string session = Path.Combine(fixture.Root, "session");
        SourceBlender.PrepareBlenderImport(Path.Combine(checkout.Folder, "input"), model, session, token);
        string raw = Path.Combine(session, "input");
        var json = JsonNode.Parse(File.ReadAllBytes(Path.Combine(raw, part)))!;
        json["nodes"]![0]!["extras"]!["recoil"]!["landmark"] = true;
        json["meshes"]![0]!["extras"]!["recoil"] = new JsonObject();
        json["meshes"]![0]!["primitives"]![0]!["extras"]!["zstudio_blender"] = new JsonObject
        { ["origins"] = new JsonArray(0, 0), ["zones"] = new JsonArray(new JsonArray(3), new JsonArray(4)), ["faces"] = new JsonArray(1, 2) };
        File.WriteAllText(Path.Combine(raw, part), json.ToJsonString());
        string edit = Path.Combine(raw, "edit.json");
        File.WriteAllText(edit, new JsonObject { ["protocol"] = 1, ["session"] = session, ["models"] = new JsonArray
        (new JsonObject { ["path"] = model }, new JsonObject { ["path"] = part, ["geometryChanged"] = true }) }.ToJsonString());
        SourceBlender.PrepareBlenderExport(edit, checkout.Outbox, token);
        Assert.Equal(baseline, File.ReadAllBytes(fixture.Path(part)));
        var plan = SourceBlender.PlanUpdate(workspace, checkout, token: token);
        Assert.Contains(plan.Changes, c => c.Relative == part);
        workspace.Apply(plan.Label, plan.Changes.Select(c => (c.Relative, (byte[]?)c.Content)), token);
        Assert.True(JsonNode.Parse(workspace.Read(part, token)!)!["nodes"]![0]!["extras"]!["recoil"]!["landmark"]!.GetValue<bool>());
        Assert.Equal(baseline, File.ReadAllBytes(fixture.Path(part)));
        workspace.Undo(); Assert.Equal(baseline, workspace.Read(part, token)); workspace.Redo();
        SourceBlender.RecordApplied(checkout, plan);
        workspace.Save(token);
        Assert.Equal(workspace.Read(part, token), File.ReadAllBytes(fixture.Path(part)));
        // Another placement can be painted after an update. A subsequent export
        // from the same Blender baseline retains that independent assignment.
        var painted = JsonNode.Parse(workspace.Read(other, token)!)!;
        painted["nodes"]![0]!["extras"]!["recoil"]!["zoneOverrides"]!["meshes"]![0]!["polygonZones"]![1] = new JsonArray(10);
        workspace.Apply("Paint another placement", [(other, Encoding.UTF8.GetBytes(painted.ToJsonString()))], token);
        json["nodes"]![0]!["extras"]!["recoil"]!["landmark"] = false;
        File.WriteAllText(Path.Combine(raw, part), json.ToJsonString());
        SourceBlender.PrepareBlenderExport(edit, checkout.Outbox, token);
        checkout = SourceBlender.Find(fixture.Project, checkout.Id);
        var again = SourceBlender.PlanUpdate(workspace, checkout, token: token);
        workspace.Apply(again.Label, again.Changes.Select(c => (c.Relative, (byte[]?)c.Content)), token);
        Assert.Equal(new JsonArray(new JsonArray(9), new JsonArray(10)).ToJsonString(),
            JsonNode.Parse(workspace.Read(other, token)!)!["nodes"]![0]!["extras"]!["recoil"]!["zoneOverrides"]!["meshes"]![0]!["polygonZones"]!.ToJsonString());
        SourceBlender.RecordApplied(checkout, again);
        // A completed generation cannot be altered after its receipt was written, even with force.
        var selected = SourceBlender.Exports(checkout, token)[0];
        File.AppendAllText(Path.Combine(Path.GetDirectoryName(selected.Gltf)!, part), " ");
        Assert.Throws<InvalidDataException>(() => SourceBlender.PlanUpdate(workspace, checkout, force: true, token: token));
        json["meshes"]![0]!["primitives"]![0]!["extras"]!["zstudio_blender"]!["zones"] = new JsonArray(new JsonArray(3), new JsonArray(3));
        File.WriteAllText(Path.Combine(raw, part), json.ToJsonString());
        SourceBlender.PrepareBlenderExport(edit, checkout.Outbox, token);
        var ambiguous = Assert.Throws<InvalidDataException>(() => SourceBlender.PlanUpdate(workspace, SourceBlender.Find(fixture.Project, checkout.Id), token: token));
        Assert.Contains("different placement zones", ambiguous.Message);
    }

    // Protects the graph's physical-file identity, source immutability and the helper's windowless protocol.
    [Fact]
    public async Task HelperSnapshotsReferencesAndTheirIndependentZones()
    {
        string folder = Path.Combine(Path.GetTempPath(), "zstudio-blender-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        try
        {
            CreateFixture(folder);
            string drive = Path.GetPathRoot(folder)!;
            Assert.Equal(Path.Combine(drive, "world.gltf"), SourceBlender.GraphPath(drive, "world.gltf"));
            byte[] source = File.ReadAllBytes(Path.Combine(folder, "world.gltf"));
            JsonObject request = new() { ["protocol"] = 1, ["operation"] = "import", ["root"] = folder, ["model"] = "world.gltf", ["destination"] = Path.Combine(folder, "prepared") };
            using MemoryStream input = new(Encoding.UTF8.GetBytes(request.ToJsonString())); using MemoryStream output = new();
            Assert.Equal(0, await BlenderBridge.RunAsync(input, output, TestContext.Current.CancellationToken));
            var result = JsonNode.Parse(output.ToArray())!;
            Assert.True(result["ok"]!.GetValue<bool>());
            var manifest = JsonNode.Parse(File.ReadAllBytes(result["session"]!.GetValue<string>()))!;
            Assert.Equal(2, manifest["models"]!.AsArray().Count);
            var prepared = JsonNode.Parse(File.ReadAllBytes(Path.Combine(folder, "prepared/input/world.gltf")))!;
            Assert.Equal("part.gltf", prepared["nodes"]![0]!["extras"]!["recoil"]!["ref"]!.GetValue<string>());
            Assert.Equal("./part.gltf", prepared["nodes"]![1]!["extras"]!["recoil"]!["ref"]!.GetValue<string>());
            Assert.Equal(3, prepared["nodes"]![0]!["extras"]!["recoil"]!["zone"]!.GetValue<int>());
            Assert.Equal(7, prepared["nodes"]![1]!["extras"]!["recoil"]!["zone"]!.GetValue<int>());
            Assert.Equal(source, File.ReadAllBytes(Path.Combine(folder, "world.gltf")));
            Assert.Throws<IOException>(() => SourceBlender.PrepareBlenderImport(folder, "world.gltf", Path.Combine(folder, "prepared"), TestContext.Current.CancellationToken));
            request["model"] = "../escape.gltf";
            using MemoryStream invalid = new(Encoding.UTF8.GetBytes(request.ToJsonString())); using MemoryStream error = new();
            Assert.Equal(1, await BlenderBridge.RunAsync(invalid, error, TestContext.Current.CancellationToken));
            Assert.False(JsonNode.Parse(error.ToArray())!["ok"]!.GetValue<bool>());
        }
        finally { Directory.Delete(folder, recursive: true); }
    }

    internal static void CreateFixture(string folder, bool missingImage = false)
    {
        Directory.CreateDirectory(folder);
        GltfPrimitive p = new() { Material = new() { Name = "surface", Extras = new() { ["recoil"] = new JsonObject { ["normals"] = false } } },
            Extras = new() { ["recoil"] = new JsonObject { ["polygons"] = new JsonArray(2), ["polygonZones"] = new JsonArray(new JsonArray(3)) } } };
        p.Positions.AddRange([new(0, 0, 0), new(2, 0, 0), new(2, 0, 2), new(0, 0, 2)]);
        p.Indices.AddRange([0, 1, 2, 0, 2, 3]);
        GltfMesh mesh = new() { Name = "shared mesh" }; mesh.Primitives.Add(p);
        GltfDocument model = new();
        model.SceneExtras = new() { ["recoil"] = new JsonObject { ["sourceFormat"] = 4, ["loadRoot"] = new JsonObject { ["zone"] = "any" } } };
        model.Roots.Add(new() { Name = "surface", Mesh = mesh, Extras = new() { ["recoil"] = new JsonObject { ["zone"] = "inherit", ["altitudeSurface"] = true, ["intersectSurface"] = true } } });
        if (missingImage)
        {
            GltfPrimitive cycle = new() { Material = new() { Name = "cycle", ImageUri = "missing.png", Extras = new() { ["recoil"] = new JsonObject { ["texture"] = "missing", ["normals"] = false } } } };
            cycle.Positions.AddRange([new(0, 0, 0), new(1, 0, 0), new(0, 0, 1)]);
            cycle.TexCoords.AddRange([new(0, 0), new(1, 0), new(0, 1)]); cycle.Indices.AddRange([0, 1, 2]);
            GltfMesh missing = new() { Name = "cycle surface" }; missing.Primitives.Add(cycle);
            model.Roots.Add(new() { Name = "cycle surface", Mesh = missing, Extras = new() { ["recoil"] = new JsonObject { ["zone"] = "inherit" } } });
        }
        var encoded = model.Write("part.bin"); File.WriteAllBytes(Path.Combine(folder, "part.gltf"), encoded.Json); File.WriteAllBytes(Path.Combine(folder, "part.bin"), encoded.Binary);
        GltfDocument world = new() { SceneExtras = model.SceneExtras.DeepClone().AsObject() };
        world.Roots.Add(new() { Name = "placement", Extras = new() { ["recoil"] = new JsonObject { ["ref"] = "part.gltf", ["zone"] = 3 } } });
        world.Roots.Add(new() { Name = "placement", Matrix = Matrix4x4.CreateTranslation(5, 0, 0), Extras = new() { ["recoil"] = new JsonObject { ["ref"] = "./part.gltf", ["zone"] = 7 } } });
        var database = world.Write("world.bin"); File.WriteAllBytes(Path.Combine(folder, "world.gltf"), database.Json); File.WriteAllBytes(Path.Combine(folder, "world.bin"), database.Binary);
    }
}
