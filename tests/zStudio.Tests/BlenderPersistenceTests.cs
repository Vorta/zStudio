using System.Text;
using System.Text.Json.Nodes;
using Recoil.Zbd.Core.Gltf;
using Recoil.Zbd.Core.Sources;
using Recoil.Zbd.Core.Worlds;
using Xunit;

namespace Recoil.Zbd.Tests;

public sealed class BlenderPersistenceTests
{
    private const string Model = "data/world.gltf", Part = "data/part.gltf";

    // Geometry updates create immutable buffers; saving must not remove ordinary Undo/Redo.
    [Fact]
    public void SavedGraphGeometryCanUndoSaveAndRedoWithoutChangingOtherBufferConsumers()
    {
        var token = TestContext.Current.CancellationToken;
        using SourceWorldFixture fixture = new();
        BlenderGraphTests.CreateFixture(fixture.Path("data"));
        byte[] original = File.ReadAllBytes(fixture.Path(Part)), buffer = File.ReadAllBytes(fixture.Path("data/part.bin"));
        fixture.Write("data/untouched.gltf", original);
        var workspace = new SourceWorkspace(fixture.Project);
        var checkout = ExportSplitGeometry(fixture, workspace);
        var plan = SourceBlender.PlanUpdate(workspace, checkout, token: token);
        workspace.Apply(plan.Label, plan.Changes.Select(c => (c.Relative, (byte[]?)c.Content)), token);
        byte[] edited = workspace.Read(Part, token)!;
        string addedBuffer = Assert.Single(plan.Changes, c => c.Relative.EndsWith(".bin", StringComparison.OrdinalIgnoreCase)).Relative;
        workspace.Save(token);
        workspace.Undo();
        Assert.Equal(original, workspace.Read(Part, token));
        Assert.False(workspace.IsFileDirty(addedBuffer));
        workspace.Save(token);
        Assert.Equal(original, File.ReadAllBytes(fixture.Path(Part)));
        Assert.True(File.Exists(fixture.Path(addedBuffer)));
        Assert.Equal(buffer, File.ReadAllBytes(fixture.Path("data/part.bin")));
        Assert.Equal(original, File.ReadAllBytes(fixture.Path("data/untouched.gltf")));
        workspace.Redo(); workspace.Save(token);
        Assert.Equal(edited, File.ReadAllBytes(fixture.Path(Part)));
        Assert.Equal(plan.Changes.Single(c => c.Relative == addedBuffer).Content, File.ReadAllBytes(fixture.Path(addedBuffer)));

        // A filename alone cannot authorize retaining corrupted content or deleting a source.
        byte[] savedBuffer = File.ReadAllBytes(fixture.Path(addedBuffer));
        File.WriteAllBytes(fixture.Path(addedBuffer), [1, 2, 3]);
        Assert.Throws<SourceFileChangedException>(() => workspace.Undo());
        Assert.Equal(edited, workspace.Read(Part, token));
        File.WriteAllBytes(fixture.Path(addedBuffer), savedBuffer);
        workspace.Apply("Create ordinary source", [("data/new.gltf", original)], token);
        workspace.Save(token);
        Assert.Throws<NotSupportedException>(() => workspace.Undo());
        Assert.True(File.Exists(fixture.Path("data/new.gltf")));
        string falseAddress = "data/new." + new string('0', 64) + ".bin";
        workspace.Apply("Create falsely addressed buffer", [(falseAddress, new byte[] { 1, 2, 3 })], token);
        workspace.Save(token);
        Assert.Throws<NotSupportedException>(() => workspace.Undo());
    }

    // Both direct and prepared callers must recheck owners arriving after the update and after Undo.
    [Fact]
    public void GraphSaveRefusesLatePlacementProfilesForAChangedLayout()
    {
        var token = TestContext.Current.CancellationToken;
        foreach (bool preparedCaller in new[] { false, true })
        {
            using SourceWorldFixture fixture = new();
            BlenderGraphTests.CreateFixture(fixture.Path("data"));
            byte[] original = File.ReadAllBytes(fixture.Path(Part));
            var workspace = new SourceWorkspace(fixture.Project);
            var checkout = ExportSplitGeometry(fixture, workspace);
            var prepared = preparedCaller ? workspace.BeginPreparedEdit() : null;
            var target = prepared?.Workspace ?? workspace;
            var plan = SourceBlender.PlanUpdate(target, checkout, token: token);
            target.Apply(plan.Label, plan.Changes.Select(c => (c.Relative, (byte[]?)c.Content)), token);
            if (prepared != null) workspace.AcceptPreparedEdit(prepared, token);
            byte[] edited = workspace.Read(Part, token)!;
            WriteOwner(original, p => File.ReadAllBytes(fixture.Path("data/" + p)));
            Assert.Throws<SourceFileChangedException>(() => workspace.Save(token));
            Assert.Equal(original, File.ReadAllBytes(fixture.Path(Part)));
            File.Delete(fixture.Path("data/late-owner.gltf"));
            workspace.Save(token);
            WriteOwner(edited, p => File.ReadAllBytes(fixture.Path("data/" + p)));
            workspace.Undo();
            Assert.Throws<SourceFileChangedException>(() => workspace.Save(token));
            Assert.Equal(edited, File.ReadAllBytes(fixture.Path(Part)));

            void WriteOwner(byte[] geometry, Func<string, byte[]> read)
            {
                var owner = JsonNode.Parse(File.ReadAllBytes(fixture.Path(Model)))!;
                var document = GltfDocument.Read(geometry, read, token);
                owner["nodes"]![0]!["extras"]!["recoil"]!["zoneOverrides"] =
                    WorldGltf.WriteZoneOverride(WorldGltf.CaptureZoneProfile(document, token: token), token);
                fixture.Write("data/late-owner.gltf", Encoding.UTF8.GetBytes(owner.ToJsonString()));
            }
        }
    }

    [Fact]
    public void SameLayoutReorderRejectsLateOwnersButAllowsAcceptedZoneEditsAndHistory()
    {
        var token = TestContext.Current.CancellationToken;
        using SourceWorldFixture fixture = new();
        BlenderGraphTests.CreateFixture(fixture.Path("data"));
        var source = GltfDocument.Read(File.ReadAllBytes(fixture.Path(Part)), p => File.ReadAllBytes(fixture.Path("data/" + p)), token);
        var primitive = source.Roots[0].Mesh!.Primitives[0];
        primitive.Positions.Clear(); primitive.Positions.AddRange([new(0, 0, 0), new(2, 0, 0), new(0, 0, 2), new(10, 0, 0), new(12, 0, 0), new(10, 0, 2)]);
        primitive.Indices.Clear(); primitive.Indices.AddRange([0, 1, 2, 3, 4, 5]);
        primitive.Extras!["recoil"]!["polygons"] = new JsonArray(1, 1);
        primitive.Extras["recoil"]!["polygonZones"] = new JsonArray(new JsonArray(3), new JsonArray(4));
        var encoded = source.Write("part.bin", token: token); fixture.Write(Part, encoded.Json); fixture.Write("data/part.bin", encoded.Binary);
        var profile = WorldGltf.WriteZoneOverride(WorldGltf.CaptureZoneProfile(GltfDocument.Read(encoded.Json, _ => encoded.Binary, token), token: token), token);
        profile["meshes"]![0]!["polygonZones"] = new JsonArray(new JsonArray(9), new JsonArray(9));
        var owner = JsonNode.Parse(File.ReadAllBytes(fixture.Path(Model)))!;
        owner["nodes"]![0]!["extras"]!["recoil"]!["zoneOverrides"] = profile;
        byte[] uniform = Encoding.UTF8.GetBytes(owner.ToJsonString()); fixture.Write("data/owner.gltf", uniform);
        var workspace = new SourceWorkspace(fixture.Project);
        var checkout = ExportSplitGeometry(fixture, workspace, reorder: true);
        var prepared = workspace.BeginPreparedEdit();
        var plan = SourceBlender.PlanUpdate(prepared.Workspace, checkout, token: token);
        prepared.Workspace.Apply(plan.Label, plan.Changes.Select(c => (c.Relative, (byte[]?)c.Content)), token);
        workspace.AcceptPreparedEdit(prepared, token);
        profile["meshes"]![0]!["polygonZones"] = new JsonArray(new JsonArray(9), new JsonArray(10));
        fixture.Write("data/owner.gltf", Encoding.UTF8.GetBytes(owner.ToJsonString()));
        Assert.Throws<SourceFileChangedException>(() => workspace.Save(token));
        Assert.Equal(encoded.Json, File.ReadAllBytes(fixture.Path(Part)));
        fixture.Write("data/owner.gltf", uniform);
        var nested = JsonNode.Parse(File.ReadAllBytes(fixture.Path(Model)))!;
        nested["nodes"]![0]!["extras"]!["recoil"]!["ref"] = "world.gltf";
        nested["nodes"]![0]!["extras"]!["recoil"]!["zoneOverrides"] = new JsonObject
        { ["references"] = new JsonObject { ["0"] = profile.DeepClone() } };
        fixture.Write("data/late-nested.gltf", Encoding.UTF8.GetBytes(nested.ToJsonString()));
        Assert.Throws<SourceFileChangedException>(() => workspace.Save(token));
        Assert.Equal(encoded.Json, File.ReadAllBytes(fixture.Path(Part)));
        File.Delete(fixture.Path("data/late-nested.gltf"));

        // The same kind of profile edit accepted in this workspace targets its current geometry.
        profile["meshes"]![0]!["polygonZones"] = new JsonArray(new JsonArray(10), new JsonArray(9));
        byte[] painted = Encoding.UTF8.GetBytes(owner.ToJsonString());
        workspace.Apply("Paint current placement", [("data/owner.gltf", painted)], token);
        workspace.Save(token);
        workspace.Undo(); workspace.Save(token);
        Assert.Equal(uniform, File.ReadAllBytes(fixture.Path("data/owner.gltf")));
        workspace.Redo(); workspace.Save(token);
        Assert.Equal(painted, File.ReadAllBytes(fixture.Path("data/owner.gltf")));
        workspace.Undo(); workspace.Undo(); workspace.Save(token);
        Assert.Equal(encoded.Json, File.ReadAllBytes(fixture.Path(Part)));
        Assert.Equal(uniform, File.ReadAllBytes(fixture.Path("data/owner.gltf")));
    }

    [Fact]
    public void CooperatingSavesCannotInsertPlacementOwnersBetweenValidationAndPublication()
    {
        var token = TestContext.Current.CancellationToken;
        using SourceWorldFixture fixture = new();
        BlenderGraphTests.CreateFixture(fixture.Path("data"));
        var part = GltfDocument.Read(File.ReadAllBytes(fixture.Path(Part)), p => File.ReadAllBytes(fixture.Path("data/" + p)), token);
        var primitive = part.Roots[0].Mesh!.Primitives[0];
        primitive.Positions.Clear(); primitive.Positions.AddRange([new(0, 0, 0), new(2, 0, 0), new(0, 0, 2), new(10, 0, 0), new(12, 0, 0), new(10, 0, 2)]);
        primitive.Indices.Clear(); primitive.Indices.AddRange([0, 1, 2, 3, 4, 5]);
        primitive.Extras!["recoil"]!["polygons"] = new JsonArray(1, 1);
        primitive.Extras["recoil"]!["polygonZones"] = new JsonArray(new JsonArray(3), new JsonArray(4));
        var encoded = part.Write("part.bin", token); fixture.Write(Part, encoded.Json); fixture.Write("data/part.bin", encoded.Binary);
        var profile = WorldGltf.WriteZoneOverride(WorldGltf.CaptureZoneProfile(GltfDocument.Read(encoded.Json, _ => encoded.Binary, token), token: token), token);
        profile["meshes"]![0]!["polygonZones"] = new JsonArray(new JsonArray(9), new JsonArray(10));
        var owner = JsonNode.Parse(File.ReadAllBytes(fixture.Path(Model)))!;
        owner["nodes"]![0]!["extras"]!["recoil"]!["zoneOverrides"] = profile;
        byte[] ownerBytes = Encoding.UTF8.GetBytes(owner.ToJsonString());
        bool beforeLock = true, failStaging = false;
        var workspace = new SourceWorkspace(fixture.Project, (writes, description, t, validation) =>
        {
            if (beforeLock) SaveOwner(t);
            var publisher = new SourcePublisher(fixture.Project)
            {
                Fault = (step, _) => { if (failStaging && step == "stage") throw new IOException("Injected staging failure."); }
            };
            return publisher.Publish([.. writes.Select(w => new SourceFileWrite(w.Relative, w.Expected, w.Content))], description, t, ct =>
            {
                var verified = validation(ct);
                try
                {
                    // This is a real second publisher, after the first graph scan, under the same project lock.
                    Assert.Contains("Another save", Assert.Throws<IOException>(() => SaveOwner(ct)).Message);
                    return verified;
                }
                catch { verified.Dispose(); throw; }
            }).Written;
        });
        var checkout = ExportSplitGeometry(fixture, workspace, reorder: true);
        var plan = SourceBlender.PlanUpdate(workspace, checkout, token: token);
        workspace.Apply(plan.Label, plan.Changes.Select(c => (c.Relative, (byte[]?)c.Content)), token);
        // A second save that finishes before our lock is acquired must be included in our semantic scan.
        Assert.Throws<SourceFileChangedException>(() => workspace.Save(token));
        Assert.Equal(encoded.Json, File.ReadAllBytes(fixture.Path(Part)));
        Assert.Equal(ownerBytes, File.ReadAllBytes(fixture.Path("data/late-owner.gltf")));
        File.Delete(fixture.Path("data/late-owner.gltf")); beforeLock = false;
        // Both a failed publisher and a successful one must dispose their held semantic inputs and project lock.
        failStaging = true;
        Assert.Equal("Injected staging failure.", Assert.Throws<IOException>(() => workspace.Save(token)).Message);
        Assert.Equal(encoded.Json, File.ReadAllBytes(fixture.Path(Part)));
        File.WriteAllBytes(fixture.Path("data/part.bin"), encoded.Binary);
        failStaging = false; workspace.Save(token);
        Assert.False(workspace.IsDirty);
        Assert.False(File.Exists(fixture.Path("data/late-owner.gltf")));
        File.WriteAllBytes(fixture.Path("data/part.bin"), encoded.Binary);

        void SaveOwner(CancellationToken cancellation)
        {
            var second = new SourceWorkspace(fixture.Project);
            second.Apply("Create old-geometry placement", [("data/late-owner.gltf", ownerBytes)], cancellation);
            second.Save(cancellation);
        }
    }

    [Fact]
    public void OwnerOnlyZoneSaveChecksAcceptedGeometryButAllowsIndependentFlagsAndLabels()
    {
        var token = TestContext.Current.CancellationToken;
        using SourceWorldFixture fixture = new();
        BlenderGraphTests.CreateFixture(fixture.Path("data"));
        var part = GltfDocument.Read(File.ReadAllBytes(fixture.Path(Part)), p => File.ReadAllBytes(fixture.Path("data/" + p)), token);
        var primitive = part.Roots[0].Mesh!.Primitives[0];
        primitive.Positions.Clear(); primitive.Positions.AddRange([new(0, 0, 0), new(2, 0, 0), new(0, 0, 2), new(10, 0, 0), new(12, 0, 0), new(10, 0, 2)]);
        primitive.Indices.Clear(); primitive.Indices.AddRange([0, 1, 2, 3, 4, 5]);
        primitive.Extras!["recoil"]!["polygons"] = new JsonArray(1, 1);
        primitive.Extras["recoil"]!["polygonZones"] = new JsonArray(new JsonArray(3), new JsonArray(4));
        var encoded = part.Write("part.bin", token); fixture.Write(Part, encoded.Json); fixture.Write("data/part.bin", encoded.Binary);
        var profile = WorldGltf.WriteZoneOverride(WorldGltf.CaptureZoneProfile(GltfDocument.Read(encoded.Json, _ => encoded.Binary, token), token: token), token);
        profile["meshes"]![0]!["polygonZones"] = new JsonArray(new JsonArray(9), new JsonArray(10));
        var owner = JsonNode.Parse(File.ReadAllBytes(fixture.Path(Model)))!;
        owner["nodes"]![0]!["extras"]!["recoil"]!["zoneOverrides"] = profile;
        fixture.Write(Model, Encoding.UTF8.GetBytes(owner.ToJsonString()));
        var workspace = new SourceWorkspace(fixture.Project);
        Zone(11); workspace.Save(token); // Canonical export, then a genuinely owner-only edit.
        byte[] savedOwner = File.ReadAllBytes(fixture.Path(Model));
        Zone(12); Assert.Equal([Model], workspace.DirtyFiles);
        byte[] originalBuffer = File.ReadAllBytes(fixture.Path("data/part.bin"));
        byte[] reordered = originalBuffer.ToArray();
        var currentPart = JsonNode.Parse(File.ReadAllBytes(fixture.Path(Part)))!;
        var accessor = currentPart["accessors"]![currentPart["meshes"]![0]!["primitives"]![0]!["attributes"]!["POSITION"]!.GetValue<int>()]!;
        var view = currentPart["bufferViews"]![accessor["bufferView"]!.GetValue<int>()]!;
        int offset = (view["byteOffset"]?.GetValue<int>() ?? 0) + (accessor["byteOffset"]?.GetValue<int>() ?? 0);
        originalBuffer.AsSpan(offset + 36, 36).CopyTo(reordered.AsSpan(offset, 36));
        originalBuffer.AsSpan(offset, 36).CopyTo(reordered.AsSpan(offset + 36, 36));
        fixture.Write("data/part.bin", reordered);
        Assert.Throws<SourceFileChangedException>(() => workspace.Save(token));
        Assert.Equal(savedOwner, File.ReadAllBytes(fixture.Path(Model)));
        Assert.Equal(reordered, File.ReadAllBytes(fixture.Path("data/part.bin")));
        fixture.Write("data/part.bin", originalBuffer);
        currentPart["nodes"]![0]!["extras"]!["recoil"]!["landmark"] = true;
        fixture.Write(Part, Encoding.UTF8.GetBytes(currentPart.ToJsonString()));
        workspace.Save(token); // An independent flag changes no face identity.
        workspace.Undo(); workspace.Save(token); workspace.Redo(); workspace.Save(token);

        // Editing the externally changed part itself requires reloading, as the normal
        // workspace guard deliberately invalidates its old file-edit history.
        workspace.Reload();
        Zone(13); workspace.Save(token);
        SourceEmbeddedZones labels = new(workspace, token);
        labels.SetLabels(Part, [new(255, "Any"), new(12, "Painted region")]);
        var labelChanges = labels.Changes();
        workspace.Apply("Name zone", labelChanges.Select(c => (c.Relative, (byte[]?)c.Content)), token);
        fixture.Write("data/part.bin", reordered);
        workspace.Save(token); // A label alone does not publish face assignments.
        workspace.Undo(); workspace.Save(token); // Undo the label, still without changing assignments.
        workspace.Undo();
        Assert.Throws<SourceFileChangedException>(() => workspace.Save(token)); // Label writes did not bless the changed buffer.
        fixture.Write("data/part.bin", originalBuffer);
        workspace.Redo(); workspace.Save(token);
        fixture.Write("data/part.bin", reordered);
        Zone(14); workspace.Save(token); // A new edit on a clean owner uses current geometry.
        Zone(15, accept: false);
        fixture.Write("data/part.bin", originalBuffer);
        Zone(16); workspace.Save(token); // An abandoned plan must not freeze a clean owner.

        void Zone(int zone, bool accept = true)
        {
            var checkout = SourceBlender.Checkout(workspace, Model, token);
            string session = Path.Combine(fixture.Root, "session-" + Guid.NewGuid().ToString("N"));
            SourceBlender.PrepareBlenderImport(Path.Combine(checkout.Folder, "input"), Model, session, token);
            string raw = Path.Combine(session, "input");
            var rawPart = JsonNode.Parse(File.ReadAllBytes(Path.Combine(raw, Part)))!;
            rawPart["meshes"]![0]!["extras"]!["recoil"] ??= new JsonObject();
            rawPart["meshes"]![0]!["primitives"]![0]!["extras"]!["zstudio_blender"] = new JsonObject
            { ["origins"] = new JsonArray(0, 1), ["zones"] = new JsonArray(new JsonArray(3), new JsonArray(4)), ["faces"] = new JsonArray(1, 2) };
            File.WriteAllText(Path.Combine(raw, Part), rawPart.ToJsonString());
            var rawWorld = JsonNode.Parse(File.ReadAllBytes(Path.Combine(raw, Model)))!;
            rawWorld["nodes"]![0]!["extras"]!["recoil"]!["zoneOverrides"]!["meshes"]![0]!["polygonZones"] = new JsonArray(new JsonArray(zone), new JsonArray(10));
            File.WriteAllText(Path.Combine(raw, Model), rawWorld.ToJsonString());
            string edit = Path.Combine(raw, "edit.json");
            File.WriteAllText(edit, new JsonObject { ["protocol"] = 1, ["session"] = session,
                ["models"] = new JsonArray(new JsonObject { ["path"] = Model }, new JsonObject { ["path"] = Part }) }.ToJsonString());
            SourceBlender.PrepareBlenderExport(edit, checkout.Outbox, token);
            var prepared = workspace.BeginPreparedEdit();
            var plan = SourceBlender.PlanUpdate(prepared.Workspace, checkout, token: token);
            if (!accept) return;
            prepared.Workspace.Apply(plan.Label, plan.Changes.Select(c => (c.Relative, (byte[]?)c.Content)), token);
            workspace.AcceptPreparedEdit(prepared, token);
        }
    }

    private static BlenderCheckout ExportSplitGeometry(SourceWorldFixture fixture, SourceWorkspace workspace, bool reorder = false)
    {
        var token = TestContext.Current.CancellationToken;
        var checkout = SourceBlender.Checkout(workspace, Model, token);
        string session = Path.Combine(fixture.Root, "session");
        SourceBlender.PrepareBlenderImport(Path.Combine(checkout.Folder, "input"), Model, session, token);
        string raw = Path.Combine(session, "input"), part = Path.Combine(raw, Part);
        var json = JsonNode.Parse(File.ReadAllBytes(part))!;
        json["meshes"]![0]!["extras"]!["recoil"] = new JsonObject();
        json["meshes"]![0]!["primitives"]![0]!["extras"]!["zstudio_blender"] = new JsonObject
        {
            ["origins"] = new JsonArray(0, 0), ["zones"] = new JsonArray(new JsonArray(3), new JsonArray(4)),
            ["faces"] = new JsonArray(1, 2)
        };
        if (reorder)
        {
            var provenance = json["meshes"]![0]!["primitives"]![0]!["extras"]!["zstudio_blender"]!;
            provenance["origins"] = new JsonArray(1, 0); provenance["faces"] = new JsonArray(2, 1);
            provenance["zones"] = new JsonArray(new JsonArray(4), new JsonArray(3));
            byte[] buffer = File.ReadAllBytes(Path.Combine(raw, "data/part.bin"));
            var accessor = json["accessors"]![json["meshes"]![0]!["primitives"]![0]!["attributes"]!["POSITION"]!.GetValue<int>()]!;
            var view = json["bufferViews"]![accessor["bufferView"]!.GetValue<int>()]!;
            int offset = (view["byteOffset"]?.GetValue<int>() ?? 0) + (accessor["byteOffset"]?.GetValue<int>() ?? 0);
            byte[] first = buffer.AsSpan(offset, 36).ToArray();
            buffer.AsSpan(offset + 36, 36).CopyTo(buffer.AsSpan(offset, 36)); first.CopyTo(buffer, offset + 36);
            File.WriteAllBytes(Path.Combine(raw, "data/part.bin"), buffer);
        }
        File.WriteAllText(part, json.ToJsonString());
        string edit = Path.Combine(raw, "edit.json");
        File.WriteAllText(edit, new JsonObject
        {
            ["protocol"] = 1, ["session"] = session,
            ["models"] = new JsonArray(new JsonObject { ["path"] = Model }, new JsonObject { ["path"] = Part, ["geometryChanged"] = true })
        }.ToJsonString());
        SourceBlender.PrepareBlenderExport(edit, checkout.Outbox, token);
        return checkout;
    }
}
