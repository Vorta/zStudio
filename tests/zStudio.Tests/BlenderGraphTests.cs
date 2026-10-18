using System.Numerics;
using System.Buffers.Binary;
using System.Diagnostics;
using System.Text;
using System.Text.Json.Nodes;
using zStudio.Core;
using zStudio.Core.Export;
using zStudio.Core.Gltf;
using zStudio.Core.Sources;
using zStudio.Core.Worlds;
using Xunit;

namespace zStudio.Tests;

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
            // The extension keeps its import sessions in the temporary folder: this run's go with the fixture.
            process.StartInfo.Environment["TEMP"] = process.StartInfo.Environment["TMP"] = Directory.CreateDirectory(Path.Combine(folder, "temp")).FullName;
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
        // Separate (or a duplicate) gives the mesh and its object new editor identities while its faces keep theirs: the
        // placement still finds the polygons they came from.
        json["meshes"]![0]!["extras"]!["zstudio_editor_id"] = "separatedmesh";
        foreach (var holder in json["nodes"]!.AsArray().Where(n => n?["mesh"]?.GetValue<int>() == 0))
        { holder!["extras"]!["zstudio_editor_id"] = "separatedobject"; holder["name"] = "separated"; }
        File.WriteAllText(Path.Combine(raw, part), json.ToJsonString());
        SourceBlender.PrepareBlenderExport(edit, checkout.Outbox, token);
        checkout = SourceBlender.Find(fixture.Project, checkout.Id);
        var again = SourceBlender.PlanUpdate(workspace, checkout, token: token);
        workspace.Apply(again.Label, again.Changes.Select(c => (c.Relative, (byte[]?)c.Content)), token);
        Assert.Equal(new JsonArray(new JsonArray(9), new JsonArray(10)).ToJsonString(),
            JsonNode.Parse(workspace.Read(other, token)!)!["nodes"]![0]!["extras"]!["recoil"]!["zoneOverrides"]!["meshes"]![0]!["polygonZones"]!.ToJsonString());
        SourceBlender.RecordApplied(checkout, again);
        // Another Blender import of the checkout starts from its input again: replacing what an update applied since would undo it.
        json["nodes"]![0]!["extras"]!["recoil"]!["landmark"] = true;
        File.WriteAllText(Path.Combine(raw, part), json.ToJsonString());
        var reimport = JsonNode.Parse(File.ReadAllText(edit))!; reimport["import"] = "RECOIL second import";
        string second = Path.Combine(raw, "edit-second.json"); File.WriteAllText(second, reimport.ToJsonString());
        SourceBlender.PrepareBlenderExport(second, checkout.Outbox, token);
        // Its placement geometry changed under another import's update: a fresh checkout is required.
        Assert.Contains("outside this Blender file's exports", Assert.Throws<InvalidDataException>(() => SourceBlender.PlanUpdate(workspace, SourceBlender.Find(fixture.Project, checkout.Id), token: token)).Message);
        json["nodes"]![0]!["extras"]!["recoil"]!["landmark"] = false;
        File.WriteAllText(Path.Combine(raw, part), json.ToJsonString());
        // A completed generation cannot be altered after its receipt was written, even with force.
        var selected = SourceBlender.Exports(checkout, token)[0];
        File.AppendAllText(Path.Combine(Path.GetDirectoryName(selected.Gltf)!, part), " ");
        Assert.Contains("changed after graph validation", Assert.Throws<InvalidDataException>(() => SourceBlender.PlanUpdate(workspace, checkout, force: true, token: token)).Message);
        json["meshes"]![0]!["primitives"]![0]!["extras"]!["zstudio_blender"]!["zones"] = new JsonArray(new JsonArray(3), new JsonArray(3));
        File.WriteAllText(Path.Combine(raw, part), json.ToJsonString());
        SourceBlender.PrepareBlenderExport(edit, checkout.Outbox, token);
        var ambiguous = Assert.Throws<InvalidDataException>(() => SourceBlender.PlanUpdate(workspace, SourceBlender.Find(fixture.Project, checkout.Id), token: token));
        Assert.Contains("different placement zones", ambiguous.Message);
        // An older save of the same import (its exports name no generation an update applied since) would undo that update;
        // an export from the file that made the applied generation replaces it.
        json["meshes"]![0]!["primitives"]![0]!["extras"]!["zstudio_blender"]!["zones"] = new JsonArray(new JsonArray(3), new JsonArray(4));
        json["nodes"]![0]!["extras"]!["recoil"]!["landmark"] = true;
        File.WriteAllText(Path.Combine(raw, part), json.ToJsonString());
        var saved = JsonNode.Parse(File.ReadAllText(edit))!; string savedEdit = Path.Combine(raw, "edit-saved.json");
        foreach (var lineage in new[] { new JsonArray(), new JsonArray(again.Generation) })
        {
            saved["lineage"] = lineage; File.WriteAllText(savedEdit, saved.ToJsonString());
            SourceBlender.PrepareBlenderExport(savedEdit, checkout.Outbox, token);
            // Its placement owners' history is not this file's: refused (a fresh checkout), never silently replaced.
            if (lineage.Count == 0) Assert.Contains("outside this Blender file's exports", Assert.Throws<InvalidDataException>(() => SourceBlender.PlanUpdate(workspace, SourceBlender.Find(fixture.Project, checkout.Id), token: token)).Message);
            else Assert.Contains(SourceBlender.PlanUpdate(workspace, SourceBlender.Find(fixture.Project, checkout.Id), token: token).Changes, c => c.Relative == part);
        }
    }

    [Fact]
    public void RepeatedSameLayoutExportsRetainExternalFaceIdentity()
    {
        var token = TestContext.Current.CancellationToken;
        using SourceWorldFixture fixture = new();
        const string part = "data/m1/models/part.gltf", model = "data/m1/models/m1.gltf", other = "data/m1/models/other.gltf";
        GltfPrimitive primitive = new()
        {
            Material = new() { Name = "surface", Extras = new() { ["recoil"] = new JsonObject { ["normals"] = false } } },
            Extras = new() { ["recoil"] = new JsonObject { ["polygons"] = new JsonArray(1, 1), ["polygonZones"] = new JsonArray(new JsonArray(3), new JsonArray(4)) } }
        };
        primitive.Positions.AddRange([new(0, 0, 0), new(2, 0, 0), new(0, 0, 2), new(10, 0, 0), new(12, 0, 0), new(10, 0, 2)]);
        primitive.Indices.AddRange([0, 1, 2, 3, 4, 5]);
        GltfMesh mesh = new() { Name = "shared" }; mesh.Primitives.Add(primitive);
        GltfDocument document = new() { SceneExtras = new() { ["recoil"] = new JsonObject { ["sourceFormat"] = 4, ["loadRoot"] = new JsonObject { ["zone"] = "any" } } } };
        document.Roots.Add(new() { Name = "surface", Mesh = mesh, Extras = new() { ["recoil"] = new JsonObject { ["zone"] = "inherit" } } });
        var encoded = document.Write("part.bin", token); fixture.Write(part, encoded.Json); fixture.Write("data/m1/models/part.bin", encoded.Binary);
        GltfDocument world = new() { SceneExtras = document.SceneExtras.DeepClone().AsObject() };
        world.Roots.Add(new() { Name = "placement", Extras = new() { ["recoil"] = new JsonObject { ["ref"] = "part.gltf", ["zone"] = 3 } } });
        var master = world.Write("world.bin", token); fixture.Write(model, master.Json); fixture.Write("data/m1/models/world.bin", master.Binary);
        var external = JsonNode.Parse(master.Json)!;
        var profile = WorldGltf.WriteZoneOverride(WorldGltf.CaptureZoneProfile(GltfDocument.Read(encoded.Json, _ => encoded.Binary, token), token: token), token);
        profile["meshes"]![0]!["polygonZones"] = new JsonArray(new JsonArray(9), new JsonArray(10));
        external["nodes"]![0]!["extras"]!["recoil"]!["zoneOverrides"] = profile;
        fixture.Write(other, Encoding.UTF8.GetBytes(external.ToJsonString()));
        const string binaryOwner = "data/m1/models/other.glb", nestedOwner = "data/m1/models/nested.gltf";
        // An external GLB keeps its own mesh/BIN and an unknown trailing chunk,
        // while owning independent zones on the edited shared part.
        var binaryJson = external.DeepClone(); var ownMesh = JsonNode.Parse(encoded.Json)!;
        foreach (string field in new[] { "buffers", "bufferViews", "accessors", "meshes", "materials" })
            binaryJson[field] = ownMesh[field]!.DeepClone();
        binaryJson["buffers"]![0]!.AsObject().Remove("uri");
        binaryJson["nodes"]!.AsArray().Add(ownMesh["nodes"]![0]!.DeepClone());
        binaryJson["scenes"]![0]!["nodes"]!.AsArray().Add(1);
        byte[] glbJson = Encoding.UTF8.GetBytes(binaryJson.ToJsonString());
        int jsonSize = (glbJson.Length + 3) & ~3, binarySize = (encoded.Binary.Length + 3) & ~3;
        byte[] originalGlb = new byte[20 + jsonSize + 8 + binarySize + 12];
        BinaryPrimitives.WriteUInt32LittleEndian(originalGlb, 0x46546C67);
        BinaryPrimitives.WriteUInt32LittleEndian(originalGlb.AsSpan(4), 2);
        BinaryPrimitives.WriteInt32LittleEndian(originalGlb.AsSpan(8), originalGlb.Length);
        BinaryPrimitives.WriteInt32LittleEndian(originalGlb.AsSpan(12), jsonSize);
        BinaryPrimitives.WriteUInt32LittleEndian(originalGlb.AsSpan(16), 0x4E4F534A);
        originalGlb.AsSpan(20, jsonSize).Fill(32); glbJson.CopyTo(originalGlb, 20);
        BinaryPrimitives.WriteInt32LittleEndian(originalGlb.AsSpan(20 + jsonSize), binarySize);
        BinaryPrimitives.WriteUInt32LittleEndian(originalGlb.AsSpan(24 + jsonSize), 0x004E4942);
        encoded.Binary.CopyTo(originalGlb, 28 + jsonSize);
        BinaryPrimitives.WriteInt32LittleEndian(originalGlb.AsSpan(28 + jsonSize + binarySize), 4);
        BinaryPrimitives.WriteUInt32LittleEndian(originalGlb.AsSpan(32 + jsonSize + binarySize), 0x12345678);
        originalGlb.AsSpan(originalGlb.Length - 4).Fill(0xAB);
        fixture.Write(binaryOwner, originalGlb);
        var nested = JsonNode.Parse(master.Json)!;
        nested["nodes"]![0]!["extras"]!["recoil"]!["ref"] = "other.glb";
        nested["nodes"]![0]!["extras"]!["recoil"]!["zoneOverrides"] = new JsonObject { ["references"] = new JsonObject { ["0"] = profile.DeepClone() } };
        fixture.Write(nestedOwner, Encoding.UTF8.GetBytes(nested.ToJsonString()));
        // An owner outside a checkout can hold a much larger face assignment than
        // the reference being duplicated. Admit the whole expansion before cloning,
        // using a small allowance here instead of allocating a huge mission profile.
        SourceBlender.Graph graph = new((path, _) => File.ReadAllBytes(fixture.Path(path)), token);
        graph.Load(model);
        var scopeRoots = graph.Models.ToDictionary(p => p.Key, p => p.Value.Json, StringComparer.OrdinalIgnoreCase);
        var scopeDocuments = graph.Models.ToDictionary(p => p.Key, p => p.Value.Document, StringComparer.OrdinalIgnoreCase);
        Dictionary<string, SourceBlender.GraphMapping> scopeMappings = new(StringComparer.OrdinalIgnoreCase)
        {
            [model] = new(new() { [0] = 0 }, []),
            [part] = new(new() { [0] = 0 }, new() { [0] = (0, [0, 1]) }),
        };
        JsonObject Scope() => new() { ["references"] = new JsonObject { ["0"] = profile.DeepClone() } };
        var retained = Scope(); var unchanged = retained.DeepClone();
        SourceBlender.ScopeRemapBudget allowance = new(1200, 2400);
        allowance.BeginOwner(model);
        SourceBlender.RemapScope(Scope(), model, graph, scopeDocuments, scopeRoots, scopeMappings, token, allowance);
        // The same owner has already retained one scope, so another cannot consume
        // a fresh allowance. Starting a different owner resets only the owner cap.
        Assert.Throws<InvalidDataException>(() => SourceBlender.RemapScope(retained, model, graph, scopeDocuments, scopeRoots, scopeMappings, token, allowance));
        Assert.True(JsonNode.DeepEquals(unchanged, retained));
        allowance.BeginOwner(other);
        SourceBlender.RemapScope(Scope(), model, graph, scopeDocuments, scopeRoots, scopeMappings, token, allowance);
        allowance.BeginOwner(nestedOwner);
        Assert.Throws<InvalidDataException>(() => SourceBlender.RemapScope(retained, model, graph, scopeDocuments, scopeRoots, scopeMappings, token, allowance));
        scopeMappings[model].Nodes.Add(1, 0);
        Assert.Throws<InvalidDataException>(() => SourceBlender.RemapScope(retained, model, graph, scopeDocuments, scopeRoots, scopeMappings, token, new(1200)));
        Assert.True(JsonNode.DeepEquals(unchanged, retained));
        SourceBlender.RemapScope(retained, model, graph, scopeDocuments, scopeRoots, scopeMappings, token, new());
        Assert.True(JsonNode.DeepEquals(retained["references"]!["0"]!["meshes"], retained["references"]!["1"]!["meshes"]));
        Assert.Equal("[[9],[10]]", retained["references"]!["1"]!["meshes"]![0]!["polygonZones"]!.ToJsonString());
        scopeMappings[model].Nodes.Remove(1);
        scopeMappings[part].EditorNodes[0] = "surface";
        scopeMappings[part].EditorMeshes[0] = "mesh";
        Dictionary<string, Dictionary<int, int[]>> placementFaces = new(StringComparer.OrdinalIgnoreCase) { [part] = new() { [0] = [1, 2] } };
        JsonObject Placement(SourceBlender.ScopeRemapBudget budget)
        {
            var holder = scopeRoots[model]["nodes"]![0]!.DeepClone().AsObject();
            JsonArray accepted = [new JsonObject
            {
                ["chain"] = new JsonArray(), ["nodes"] = new JsonObject { ["surface"] = new JsonObject { ["zone"] = 7 } },
                ["meshes"] = new JsonObject { ["mesh"] = new JsonObject { ["1"] = new JsonArray(12) } },
            }];
            SourceBlender.ApplyPlacementEdits(model, holder, accepted, scopeRoots, scopeDocuments, scopeMappings, placementFaces, token, budget);
            return holder;
        }
        // Sparse accepted edits materialize a complete current geometry profile.
        // Their admission shares the earlier remap charge even after visiting other owners.
        SourceBlender.ScopeRemapBudget combined = new(1200);
        combined.BeginOwner(model);
        SourceBlender.RemapScope(Scope(), model, graph, scopeDocuments, scopeRoots, scopeMappings, token, combined);
        combined.BeginOwner(other); combined.BeginOwner(model);
        Assert.Throws<InvalidDataException>(() => Placement(combined));
        SourceBlender.ScopeRemapBudget placements = new(1200);
        placements.BeginOwner(model);
        var placed = Placement(placements); Placement(placements);
        Assert.Throws<InvalidDataException>(() => Placement(placements));
        Assert.Equal("[[12],[4]]", placed["extras"]!["recoil"]!["zoneOverrides"]!["meshes"]![0]!["polygonZones"]!.ToJsonString());
        Assert.Equal(7, placed["extras"]!["recoil"]!["zoneOverrides"]!["nodes"]![0]!["zone"]!.GetValue<int>());
        var workspace = new SourceWorkspace(fixture.Project);
        var checkout = SourceBlender.Checkout(workspace, model, token);
        string session = Path.Combine(fixture.Root, "session");
        SourceBlender.PrepareBlenderImport(Path.Combine(checkout.Folder, "input"), model, session, token);
        string raw = Path.Combine(session, "input");
        var json = JsonNode.Parse(File.ReadAllBytes(Path.Combine(raw, part)))!;
        json["meshes"]![0]!["extras"]!["recoil"] = new JsonObject();
        json["meshes"]![0]!["primitives"]![0]!["extras"]!["zstudio_blender"] = new JsonObject
        { ["origins"] = new JsonArray(1, 0), ["zones"] = new JsonArray(new JsonArray(4), new JsonArray(3)), ["faces"] = new JsonArray(2, 1) };
        // Swap spatially separate faces while retaining identical index topology.
        byte[] binary = File.ReadAllBytes(Path.Combine(raw, "data/m1/models/part.bin"));
        var accessor = json["accessors"]![json["meshes"]![0]!["primitives"]![0]!["attributes"]!["POSITION"]!.GetValue<int>()]!;
        var view = json["bufferViews"]![accessor["bufferView"]!.GetValue<int>()]!;
        int offset = (view["byteOffset"]?.GetValue<int>() ?? 0) + (accessor["byteOffset"]?.GetValue<int>() ?? 0);
        byte[] first = binary.AsSpan(offset, 36).ToArray();
        binary.AsSpan(offset + 36, 36).CopyTo(binary.AsSpan(offset, 36)); first.CopyTo(binary, offset + 36);
        File.WriteAllBytes(Path.Combine(raw, "data/m1/models/part.bin"), binary);
        File.WriteAllText(Path.Combine(raw, part), json.ToJsonString());
        string edits = Path.Combine(raw, "edit.json");
        File.WriteAllText(edits, new JsonObject { ["protocol"] = 1, ["session"] = session, ["models"] = new JsonArray
            (new JsonObject { ["path"] = model }, new JsonObject { ["path"] = part, ["geometryChanged"] = true }) }.ToJsonString());
        void Update()
        {
            SourceBlender.PrepareBlenderExport(edits, checkout.Outbox, token);
            checkout = SourceBlender.Find(fixture.Project, checkout.Id);
            var plan = SourceBlender.PlanUpdate(workspace, checkout, token: token);
            workspace.Apply(plan.Label, plan.Changes.Select(c => (c.Relative, (byte[]?)c.Content)), token);
            SourceBlender.RecordApplied(checkout, plan);
        }
        string Zones()
        {
            string expected = JsonNode.Parse(workspace.Read(other, token)!)!["nodes"]![0]!["extras"]!["recoil"]!["zoneOverrides"]!["meshes"]![0]!["polygonZones"]!.ToJsonString();
            byte[] binaryBytes = workspace.Read(binaryOwner, token)!;
            var container = JsonNode.Parse(GltfDocument.ContainerJson(binaryBytes, token, out _))!;
            Assert.Equal(expected, container["nodes"]![0]!["extras"]!["recoil"]!["zoneOverrides"]!["meshes"]![0]!["polygonZones"]!.ToJsonString());
            Assert.Equal(originalGlb.AsSpan(20 + jsonSize).ToArray(), binaryBytes.AsSpan(20 + BinaryPrimitives.ReadInt32LittleEndian(binaryBytes.AsSpan(12))).ToArray());
            var nestedContainer = JsonNode.Parse(workspace.Read(nestedOwner, token)!)!;
            Assert.Equal(expected, nestedContainer["nodes"]![0]!["extras"]!["recoil"]!["zoneOverrides"]!["references"]!["0"]!["meshes"]![0]!["polygonZones"]!.ToJsonString());
            return expected;
        }
        Update(); Assert.Equal("[[10],[9]]", Zones());
        Update(); Assert.Equal("[[10],[9]]", Zones());
        // Independent metadata edits must retain sealed geometry identity while
        // still requiring explicit consent before replacing the source changes.
        var live = JsonNode.Parse(workspace.Read(part, token)!)!;
        var attributes = live["nodes"]![0]!["extras"]!["recoil"]!;
        attributes["zone"] = 12;
        attributes["landmark"] = true;
        attributes["declient"] = "canModify";
        attributes["appearance"] = new JsonObject { ["flags"] = "0x00000080", ["alphaScale"] = 0.5f, ["color"] = new JsonArray(1, 0, 0), ["colorAlpha"] = 1 };
        live["meshes"]![0]!["extras"]!["recoil"]!["flags"] = 7;
        live["materials"]![0]!["extras"]!["recoil"]!["opacity"] = 128;
        live["scenes"]![0]!["extras"]!["recoil"]!["loadRoot"]!["zone"] = 13;
        live["scenes"]![0]!["extras"]!["recoil"]!["loadRoot"]!["altitudeZoneCheck"] = true;
        live["scenes"]![0]!["extras"]!["recoil"]!["loadRoot"]!["proximity"] = true;
        workspace.Apply("Paint source node", [(part, Encoding.UTF8.GetBytes(live.ToJsonString()))], token);
        SourceBlender.PrepareBlenderExport(edits, checkout.Outbox, token);
        checkout = SourceBlender.Find(fixture.Project, checkout.Id);
        Assert.Throws<BlenderConflictException>(() => SourceBlender.PlanUpdate(workspace, checkout, token: token));
        var forced = SourceBlender.PlanUpdate(workspace, checkout, force: true, token: token);
        workspace.Apply(forced.Label, forced.Changes.Select(c => (c.Relative, (byte[]?)c.Content)), token);
        Assert.Equal("[[10],[9]]", Zones());
        workspace.Undo(); workspace.Undo(); workspace.Undo();
        Assert.Equal("[[9],[10]]", Zones());
        Update(); Assert.Equal("[[10],[9]]", Zones());
        workspace.Save(token);
        Assert.Equal(workspace.Read(binaryOwner, token), File.ReadAllBytes(fixture.Path(binaryOwner)));
        Assert.Equal("[[10],[9]]", Zones());
    }

    [Fact]
    public void GraphExportRequiresExplicitTextureRemovalButKeepsOriginallyImagelessMaterials()
    {
        var token = TestContext.Current.CancellationToken;
        using SourceWorldFixture fixture = new();
        foreach (string kind in new[] { "supplied", "missing", "imageless" })
        {
            string source = fixture.Path(kind), session = fixture.Path(kind + "-session"), output = fixture.Path(kind + "-output");
            CreateFixture(source, missingImage: true);
            if (kind != "missing") File.WriteAllBytes(Path.Combine(source, "missing.png"), PngEncoder.Encode(new DecodedImage(1, 1, [255, 0, 0, 255]), token));
            if (kind == "imageless")
            {
                var original = JsonNode.Parse(File.ReadAllBytes(Path.Combine(source, "part.gltf")))!;
                original["materials"]![1]!["pbrMetallicRoughness"]!.AsObject().Remove("baseColorTexture");
                File.WriteAllText(Path.Combine(source, "part.gltf"), original.ToJsonString());
            }
            SourceBlender.PrepareBlenderImport(source, "world.gltf", session, token);
            string raw = Path.Combine(session, "input"), model = Path.Combine(raw, "part.gltf");
            var json = JsonNode.Parse(File.ReadAllBytes(model))!;
            for (int m = 0; m < 2; m++)
            {
                var primitive = json["meshes"]![m]!["primitives"]![0]!;
                int count = m == 0 ? 2 : 1;
                primitive["extras"] ??= new JsonObject();
                primitive["extras"]!["zstudio_blender"] = new JsonObject
                {
                    ["origins"] = new JsonArray(Enumerable.Range(0, count).Select(_ => (JsonNode)JsonValue.Create(0)!).ToArray()),
                    ["faces"] = new JsonArray(Enumerable.Range(0, count).Select(_ => (JsonNode)JsonValue.Create(1)!).ToArray()),
                    ["zones"] = new JsonArray(Enumerable.Range(0, count).Select(_ => (JsonNode)(m == 0 ? new JsonArray(3) : new JsonArray())).ToArray())
                };
            }
            var material = json["materials"]![1]!;
            material["pbrMetallicRoughness"]!.AsObject().Remove("baseColorTexture");
            json.AsObject().Remove("images"); json.AsObject().Remove("textures");
            File.WriteAllText(model, json.ToJsonString());
            string edits = Path.Combine(raw, "edit.json");
            File.WriteAllText(edits, new JsonObject { ["protocol"] = 1, ["session"] = session, ["models"] = new JsonArray(
                new JsonObject { ["path"] = "world.gltf" }, new JsonObject { ["path"] = "part.gltf", ["geometryChanged"] = true }) }.ToJsonString());
            if (kind != "imageless")
            {
                Assert.Contains("Reconnect the image", Assert.Throws<InvalidDataException>(() => SourceBlender.PrepareBlenderExport(edits, output, token)).Message);
                Assert.False(Directory.Exists(output));
                material["extras"]!["recoil"]!.AsObject().Remove("texture");
                File.WriteAllText(model, json.ToJsonString());
            }
            var result = SourceBlender.PrepareBlenderExport(edits, output, token);
            var exported = JsonNode.Parse(File.ReadAllBytes(Path.Combine(Path.GetDirectoryName(result["root"]!.GetValue<string>())!, "part.gltf")))!;
            Assert.Equal(kind == "imageless" ? "missing" : null, exported["materials"]![1]!["extras"]!["recoil"]?["texture"]?.GetValue<string>());
        }
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
