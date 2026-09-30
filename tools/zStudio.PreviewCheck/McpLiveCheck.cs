using System.IO;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using Recoil.Zbd.Core;
using Recoil.Zbd.Core.Formats;

/// <summary>Corpus regression through the packaged stdio connector and its real visible GUI.</summary>
internal static class McpLiveCheck
{
    internal static async Task Run(McpClient client, string root)
    {
        root = Path.GetFullPath(root);
        string output = Path.Combine(Path.GetTempPath(), "zstudio-live-regression-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(output);
        string[] sources = ["m1/anim.zbd", "m1/gamez.zbd", "m1/zrdr.zbd"];
        var hashes = sources.ToDictionary(p => p, p => SHA256.HashData(File.ReadAllBytes(Path.Combine(root, p))));
        var exercised = new HashSet<string>();
        var originalLayout = await Call("workspace_view", new { });
        try
        {
            await Call("open_root", new { path = root });
            var world = await Call("open_document", new { path = Path.Combine(root, "m1/gamez.zbd") });
            string worldId = world["id"]!.GetValue<string>();
            await Call("select_asset", new { document = worldId, kind = "World", index = 0 });
            string preview = (await Call("state", new { }))["preview"]!.GetValue<string>();
            await Call("scene_options", new { preview, changes = new { lod = 1, horizon = false } });
            preview = (await Call("state", new { }))["preview"]!.GetValue<string>();
            await Call("scene_card", new { preview, action = "select", node = 0 }, "locked");
            await Call("pickup_lock", new { document = worldId, revision = 0, locked = false });
            await CheckSceneTree(worldId, preview);
            var graph = await Call("ai_networks", new { preview, limit = 1 });
            Equal(91, graph["total"]!.GetValue<int>(), "AI network count");
            string aiSnapshot = graph["items"]![0]!["snapshot"]!.GetValue<string>(), network = graph["items"]![0]!["Id"]!.GetValue<string>();
            var aiNodes = await Call("ai_nodes", new { preview, snapshot = aiSnapshot, network });
            var attackStrategy = graph["items"]![0]!["attack_strategy"]!;
            Equal(true, JsonNode.DeepEquals(attackStrategy, aiNodes["items"]![0]!["attack_strategy"]), "network/node strategy parity");
            Equal("stored", attackStrategy["status"]!.GetValue<string>(), "corpus authored attack strategy");
            string aiNode = aiNodes["items"]![0]!["node_id"]!.GetValue<string>();
            await Call("scene_options", new { preview, changes = new { aiNodes = true, aiNetwork = network, aiSnapshot } });
            await Call("ai_selection", new { preview, snapshot = aiSnapshot, action = "select", node = aiNode });
            await Call("ai_selection", new { preview, snapshot = aiSnapshot, action = "properties", node = aiNode });
            Equal(aiNode, (await Call("properties_state", new { }))["content"]!["node_id"]!.GetValue<string>(), "AI pinned Properties");
            Equal(true, JsonNode.DeepEquals(attackStrategy, (await Call("properties_state", new { }))["content"]!["attack_strategy"]), "Properties strategy parity");
            await Call("camera", new { preview, action = "frame", target = "selected" });
            await CheckInspection();
            async Task CheckInspection()
            {
                var inspected = await Call("scene_inspect", new { preview });
                Equal(true, JsonNode.DeepEquals(attackStrategy, inspected["attackStrategy"]), "card strategy metadata parity");
                Equal(attackStrategy["value"]!.GetValue<string>(), inspected["inspection"]!["Attack strategy"]!.GetValue<string>(), "card stored strategy");
                Equal("Attack strategy: " + attackStrategy["value"]!.GetValue<string>(), (await Call("scene_card", new { preview, action = "copy", field = "Attack strategy" }))["text"]!.GetValue<string>(), "copy strategy");
                Equal(true, inspected["inspection"]!["Editable"]!.GetValue<bool>(), "AI editor unlocked");
                Equal(false, inspected["draft"]!["handlesVisible"]!.GetValue<bool>(), "selection has no handles before Edit");
                Equal(true, inspected["draft"]!["boundsVisible"]!.GetValue<bool>(), "selection bounds");
                var copied = await Call("scene_card", new { preview, action = "copy", field = "Authored placement XYZ" });
                Equal(true, copied["text"]!.GetValue<string>().Contains("XYZ"), "copy coordinate space");
                await Call("capture", new { target = "window", width = 1400, height = 900 });
                File.Copy(Path.Combine(output, "preview.png"), Path.Combine(output, "ai-card.png"), true);
                Equal(1, (await Call("scene_card", new { preview, action = "copy" }))["text"]!.GetValue<string>()
                    .Split(Environment.NewLine).Count(line => line.StartsWith("Attack strategy: ")), "copy all includes strategy once");
                foreach (string density in new[] { "Compact", "Comfortable" })
                {
                    await Call("workspace_view", new { changes = new { density, tools = true, inspectionPanelHeight = 216 } });
                    await Call("capture", new { target = "window", width = 1600, height = 1000 });
                    File.Copy(Path.Combine(output, "preview.png"), Path.Combine(output, "ai-card-minimum-" + density + ".png"), true);
                }
                await Call("workspace_view", new { changes = new { density = originalLayout["layout"]!["Density"]!.GetValue<string>(),
                    tools = originalLayout["layout"]!["ToolsVisible"]!.GetValue<bool>(), inspectionPanelHeight = originalLayout["layout"]!["InspectionPanelHeight"]!.GetValue<double>() } });
                await EditSelected("ai");
                foreach (string label in new[] { "tank", "pickup" })
                {
                    bool found = false;
                    for (int offset = 0; !found; offset += 200)
                    {
                        var nodes = await Call("scene_nodes", new { preview, offset, limit = 200 });
                        foreach (var node in nodes["items"]!.AsArray().Where(n => n?["actor"]?[label == "tank" ? "CoordinateSource" : "Pickup"] != null && n?["Metadata"]?["model_index"] != null))
                        {
                            try { await Call("scene_card", new { preview, action = "select", node = node!["Index"]!.GetValue<int>() }); }
                            catch (InvalidDataException ex) when (ex.Message.Contains("stale_record")) { continue; }
                            var details = (await Call("scene_inspect", new { preview }))["inspection"]!;
                            if (details["Editable"]?.GetValue<bool>() != true) continue;
                            await Call("camera", new { preview, action = "frame", target = "selected" });
                            await Call("capture", new { target = "window", width = 1400, height = 900 });
                            File.Copy(Path.Combine(output, "preview.png"), Path.Combine(output, label + "-card.png"), true);
                            await EditSelected(label); found = true; break;
                        }
                        if (nodes["nextOffset"] == null) break;
                    }
                    Equal(true, found, "rendered " + label + " was selectable and editable");
                }
                await Call("ai_selection", new { preview, snapshot = (await Call("preview_state", new { preview }))["ai"]!["snapshot"]!.GetValue<string>(), action = "select", node = aiNode });
                await Call("camera", new { preview, action = "frame", target = "selected" });
                async Task EditSelected(string label)
                {
                    var before = await Call("scene_inspect", new { preview }); long revision = before["revision"]!.GetValue<long>();
                    string target = before["inspection"]!["Target"]!.GetValue<string>();
                    var locked = await Call("pickup_lock", new { document = worldId, revision, locked = true });
                    Equal(true, locked["PickupsLocked"]!.GetValue<bool>(), label + " lock state");
                    Equal(true, (await Call("scene_inspect", new { preview }))["selected"] == null, label + " locked card closes");
                    await Call("scene_card", new { preview, action = "select", target }, "locked");
                    await Call("scene_card", new { preview, action = "begin", document = worldId, revision }, "locked");
                    await Call("pickup_lock", new { document = worldId, revision, locked = false });
                    await Call("scene_card", new { preview, action = "select", target });
                    Equal(true, (await Call("scene_inspect", new { preview }))["inspection"]!["Editable"]!.GetValue<bool>(), label + " unlocked card");
                    var p = before["inspection"]!["Authored placement XYZ"]!;
                    string[] xyz = new[] { p["x"]!.GetValue<float>() + 1.25f, p["y"]!.GetValue<float>(), p["z"]!.GetValue<float>() }
                        .Select(v => v.ToString("R", System.Globalization.CultureInfo.InvariantCulture)).ToArray();
                    var draft = await Call("scene_card", new { preview, action = "begin", document = worldId, revision });
                    Equal(true, draft["draft"]!["handlesVisible"]!.GetValue<bool>(), label + " handles after Edit");
                    draft = await Call("scene_card", new { preview, action = "set", document = worldId, revision, token = draft["draft"]!["token"]!.GetValue<string>(), position = xyz });
                    string? rotationField = label == "pickup" ? "Authored rotation XYZ (degrees)" : label == "tank" ? "Heading degrees" : null;
                    if (label == "pickup")
                        draft = await Call("scene_card", new { preview, action = "set", document = worldId, revision, token = draft["draft"]!["token"]!.GetValue<string>(), rotationDegrees = new[] { "17.5", "45", "-12" }, transformMode = "rotate" });
                    else if (label == "tank")
                        draft = await Call("scene_card", new { preview, action = "set", document = worldId, revision, token = draft["draft"]!["token"]!.GetValue<string>(), headingDegrees = "132.5", transformMode = "rotate" });
                    else await Call("scene_card", new { preview, action = "set", document = worldId, revision, token = draft["draft"]!["token"]!.GetValue<string>(), transformMode = "rotate" }, "read_only");
                    foreach (double height in new[] { 216d, 620d })
                    {
                        await Call("workspace_view", new { changes = new { inspectionPanelHeight = height } });
                        var resized = await Call("scene_inspect", new { preview });
                        Equal(height, resized["panel"]!["preferredHeight"]!.GetValue<double>(), label + " preferred panel height");
                        Equal(true, resized["panel"]!["effectiveHeight"]!.GetValue<double>() <= resized["panel"]!["maximumHeight"]!.GetValue<double>(), label + " bounded panel height");
                        Equal(draft["draft"]!["token"]!.GetValue<string>(), resized["draft"]!["token"]!.GetValue<string>(), label + " resizing retains draft");
                        Equal(revision, resized["revision"]!.GetValue<long>(), label + " resizing leaves revision unchanged");
                    }
                    await Call("pickup_lock", new { document = worldId, revision, locked = true }, "pending_drafts");
                    await Task.Delay(200);
                    await Call("capture", new { target = "window", width = 1400, height = 900 });
                    File.Copy(Path.Combine(output, "preview.png"), Path.Combine(output, label + "-edit.png"), true);
                    var applied = await Call("scene_card", new { preview, action = "apply", document = worldId, revision, token = draft["draft"]!["token"]!.GetValue<string>() });
                    Equal(revision + 1, applied["revision"]!.GetValue<long>(), label + " one revision per move");
                    var changed = await Call("scene_inspect", new { preview });
                    Equal(float.Parse(xyz[0], System.Globalization.CultureInfo.InvariantCulture), changed["inspection"]!["Authored placement XYZ"]!["x"]!.GetValue<float>(), label + " applied XYZ");
                    if (rotationField != null) Equal(false, JsonNode.DeepEquals(before["inspection"]![rotationField], changed["inspection"]![rotationField]), label + " applied rotation");
                    await Call("undo_redo", new { document = worldId, revision = revision + 1, action = "undo" });
                    var undone = await Call("scene_inspect", new { preview });
                    Equal(true, JsonNode.DeepEquals(p, undone["inspection"]!["Authored placement XYZ"]), label + " undo restores XYZ");
                    if (rotationField != null) Equal(true, JsonNode.DeepEquals(before["inspection"]![rotationField], undone["inspection"]![rotationField]), label + " same undo restores rotation");
                    await Call("undo_redo", new { document = worldId, revision = undone["revision"]!.GetValue<long>(), action = "redo" });
                    var redone = await Call("scene_inspect", new { preview });
                    string source = redone["inspection"]!["Source archive"]!.GetValue<string>();
                    string copy = Path.Combine(output, label + "-coordinates.zbd");
                    var saved = await Call("save_document", new { document = worldId, revision = redone["revision"]!.GetValue<long>(), destinations = new Dictionary<string, string> { [source] = copy } });
                    Equal(0, saved["result"]!["Errors"]!.AsArray().Count, label + " coordinate Save As");
                    var reopened = FormatRegistry.Default.OpenBytes(copy, await File.ReadAllBytesAsync(copy));
                    Equal(false, reopened.Diagnostics.Any(d => d.Severity == "Error"), label + " saved archive reparses");
                    var coordinates = PickupPlacementEditSession.Create(Enum.GetValues<MissionDifficulty>().SelectMany(difficulty =>
                        reopened.Assets.Where(a => a.Name.Equals(MissionLayoutSelection.For(difficulty).PickupResource, StringComparison.OrdinalIgnoreCase))
                            .Select(a => new PickupPlacementResource(difficulty, reopened, a))));
                    coordinates.AddCoordinates(reopened.Assets.Select(a => (reopened, a)));
                    var expected = new System.Numerics.Vector3(float.Parse(xyz[0], System.Globalization.CultureInfo.InvariantCulture), p["y"]!.GetValue<float>(), p["z"]!.GetValue<float>());
                    Equal(true, label == "pickup" ? coordinates.Records.Any(c => c.OriginalPosition == expected)
                        : coordinates.OtherCoordinates.Any(c => c.Kind == label && c.OriginalPosition == expected), label + " persisted XYZ survives reopen");
                    if (label == "pickup") Equal(true, coordinates.Records.Any(c => c.OriginalPosition == expected && Math.Abs(c.Rotation.Y - Math.PI / 4) < 1e-6), "pickup radians survive reopen");
                    if (label == "tank") Equal(true, coordinates.OtherCoordinates.Any(c => c.Kind == "tank" && c.OriginalPosition == expected && c.Rotation.Y == 132.5f), "tank heading survives reopen");
                    var afterSave = await Call("scene_inspect", new { preview });
                    Equal(copy, afterSave["inspection"]!["Save archive"]!.GetValue<string>(), label + " save target retargeted");
                    await Call("undo_redo", new { document = worldId, revision = afterSave["revision"]!.GetValue<long>(), action = "undo" });
                    var restored = await Call("scene_inspect", new { preview });
                    saved = await Call("save_document", new { document = worldId, revision = restored["revision"]!.GetValue<long>() });
                    Equal(0, saved["result"]!["Errors"]!.AsArray().Count, label + " ordinary Save after Save As");
                    byte[] originalBytes = await File.ReadAllBytesAsync(source), copyBytes = await File.ReadAllBytesAsync(copy);
                    Equal(true, originalBytes.SequenceEqual(copyBytes), label + " undo/save restores byte-exact archive at copy");
                    var finalState = await Call("scene_inspect", new { preview });
                    await Call("pickup_lock", new { document = worldId, revision = finalState["revision"]!.GetValue<long>(), locked = false });
                }
            }
            var cameraBefore = await Call("camera", new { preview, action = "read" });
            var cameraAfter = await Call("camera", new { preview, action = "zoom", steps = .5, screenPoint = new[] { 10d, 10d } });
            double zoomTravel = .06 * Math.Max(.01, cameraAfter["NavigationReferenceDistance"]!.GetValue<double>());
            var delta = new[] { "X", "Y", "Z" }.Select(axis => cameraAfter["Position"]![axis]!.GetValue<double>() - cameraBefore["Position"]![axis]!.GetValue<double>()).ToArray();
            Equal(true, Math.Abs(Math.Sqrt(delta.Sum(v => v * v)) - zoomTravel) < 1e-6, "packaged pointed zoom travel");
            var movement = new System.Numerics.Vector3((float)delta[0], (float)delta[1], (float)delta[2]);
            var forward = ReadVector(cameraBefore["LookDirection"]!); var up = ReadVector(cameraBefore["UpDirection"]!);
            var right = System.Numerics.Vector3.Cross(forward, up);
            Equal(true, System.Numerics.Vector3.Dot(movement, right) < 0 && System.Numerics.Vector3.Dot(movement, up) > 0
                && System.Numerics.Vector3.Dot(movement, forward) > 0, "packaged top-left pointer zoom direction");
            await Call("camera", new { preview, action = "projection", projection = "orthographic" });
            var orthoBefore = await Call("camera", new { preview, action = "read" });
            var orthoAfter = await Call("camera", new { preview, action = "zoom", steps = .5, screenPoint = new[] { 10d, 10d } });
            Equal(true, Math.Abs(orthoAfter["OrthographicWidth"]!.GetValue<double>() / orthoBefore["OrthographicWidth"]!.GetValue<double>() - Math.Exp(-.06)) < 1e-9,
                "packaged orthographic width");
            Equal(false, JsonNode.DeepEquals(orthoBefore["Position"], orthoAfter["Position"]), "packaged orthographic pointer anchoring translates eye");
            await Call("camera", new { preview, action = "projection", projection = "perspective" });
            await Call("properties_close", new { });
            await Call("scene_options", new { preview, changes = new { aiNodes = false } });
            var pickups = await Call("pickups", new { document = worldId, query = "no-such-pickup" });
            Equal(0, pickups["total"]!.GetValue<int>(), "pickup query");
            var anim = await Call("open_document", new { path = Path.Combine(root, "m1/anim.zbd") });
            string document = anim["id"]!.GetValue<string>();
            var assets = await Call("assets", new { document, query = "vtol_destruction1" });
            int entry = assets["items"]![0]!["Index"]!.GetValue<int>();
            await Call("select_asset", new { document, kind = "Animation", index = entry });
            preview = (await Call("state", new { }))["preview"]!.GetValue<string>();
            await Call("animation_options", new { preview, changes = new { mute = true, height = 50, lod = 0, horizon = true } });
            await Call("animation_transport", new { preview, action = "seek", seconds = .5 });
            // Descendant meshes and spawned effects need not share their animation root's name.
            await Call("animation_options", new { preview, changes = new { map = false } });
            var animationCandidates = new List<JsonNode>();
            for (int offset = 0; ; offset += 200)
            {
                var page = await Call("scene_nodes", new { preview, offset, limit = 200 });
                animationCandidates.AddRange(page["items"]!.AsArray().Where(n => n?["Metadata"]?["model_index"] != null).Select(n => n!));
                if (page["nextOffset"] == null) break;
            }
            int animationRoot = (await Call("preview_state", new { preview }))["animation"]?["root"]?.GetValue<int>() ?? 0;
            bool selectedAnimation = false;
            foreach (var node in animationCandidates.OrderBy(n => Math.Abs(n["Index"]!.GetValue<int>() - animationRoot)))
            {
                try { await Call("scene_card", new { preview, action = "select", node = node!["Index"]!.GetValue<int>() }); }
                catch (InvalidDataException ex) when (ex.Message.Contains("stale_record")) { continue; }
                var info = await Call("scene_inspect", new { preview });
                if (info["inspection"]?["Runtime instance"] == null) continue;
                var hierarchy = await Call("scene_tree", new { document });
                var treeSelection = await Call("scene_tree", new { document, context = hierarchy["context"]!.GetValue<string>(), row = hierarchy["selected"]!.GetValue<string>() });
                Equal(node["Index"]!.GetValue<int>(), treeSelection["row"]!["node"]!.GetValue<int>(), "animation card reveals exact scene node");
                string hierarchyContext = hierarchy["context"]!.GetValue<string>();
                await Call("animation_transport", new { preview, action = "seek", seconds = .75 });
                Equal(hierarchyContext, (await Call("scene_tree", new { document }))["context"]!.GetValue<string>(), "animation seek preserves stable hierarchy");
                await Call("animation_transport", new { preview, action = "seek", seconds = .5 });
                await Call("workspace_view", new { changes = new { navigatorTab = 3, navigator = true } });
                Equal(false, info["inspection"]!["Editable"]!.GetValue<bool>(), "animation runtime inspection remains read-only");
                await Call("camera", new { preview, action = "frame", target = "selected" });
                await Task.Delay(200);
                await Call("capture", new { target = "window", width = 1400, height = 900 });
                File.Copy(Path.Combine(output, "preview.png"), Path.Combine(output, "animation-card.png"), true);
                await Call("scene_card", new { preview, action = "clear" });
                Equal(null, (await Call("scene_tree", new { document }))["selected"], "closing animation card clears hierarchy selection");
                Equal(null, (await Call("camera", new { preview, action = "read" }))["FramingSelection"], "closing animation card clears framing target");
                // A bound source row survives renderer/context replacement, without
                // inventing a runtime instance or moving the retained camera.
                int rootNode = hierarchy["children"]!["items"]![0]!["node"]!.GetValue<int>();
                await Call("scene_tree", new { document, preview, context = hierarchyContext, action = "select", node = rootNode });
                foreach (string difficulty in new[] { "Easy", "Hard" })
                {
                    var beforeRefresh = await Call("camera", new { preview, action = "read" });
                    await Call("animation_options", new { preview, changes = new { difficulty } });
                    var afterRefresh = await Call("camera", new { preview, action = "read" });
                    Equal(rootNode, afterRefresh["FramingSelection"]!.GetValue<int>(), "difficulty retains the selected source framing target");
                    Equal(true, JsonNode.DeepEquals(beforeRefresh["Position"], afterRefresh["Position"]), "hierarchy restoration leaves camera position unchanged");
                    var refreshed = await Call("scene_tree", new { document });
                    Equal(true, refreshed["selected"] != null, "difficulty retains hierarchy selection");
                }
                selectedAnimation = true; break;
            }
            Equal(true, selectedAnimation, "animation exact runtime node inspection");
            var state = await Call("preview_state", new { preview });
            Equal(0, state["lod"]!.GetValue<int>(), "active animation LOD");
            Equal(true, state["horizon"]!.GetValue<bool>(), "active animation horizon");
            foreach (string section in new[] { "sequences", "events", "scene", "problems" })
            {
                var filtered = await Call("animation_runtime", new { preview, section, query = "no-such-record" });
                Equal(0, filtered["total"]!.GetValue<int>(), section + " query");
                Equal(0, filtered["items"]!.AsArray().Count, section + " page");
            }
            var references = await Call("references", new { document, entry, table = 1, query = "HEALTHY", limit = 1 });
            Equal(1, references["records"]!["items"]![0]!["index"]!.GetValue<int>(), "reference identity after filtering");
            var records = await Call("animation_records", new { document, entry, query = "no-such-sequence" });
            Equal(0, records["sequences"]!["total"]!.GetValue<int>(), "sequence query");
            records = await Call("animation_records", new { document, entry, query = "healthy_to_destroyed" });
            string sequence = records["sequences"]!["items"]![0]!["id"]!.GetValue<string>();
            var events = await Call("animation_records", new { document, entry, sequence, query = "Set active state", offset = 1, limit = 1 });
            Equal(2, events["events"]!["total"]!.GetValue<int>(), "event filter total before paging");
            Equal(1, events["events"]!["items"]!.AsArray().Count, "event page");
            var layout = await Call("workspace_view", new { });
            var rejected = await client.CallToolAsync("zstudio_workspace_view", new Dictionary<string, object?>
            { ["changes"] = new { theme = layout["theme"]!.GetValue<string>() == "Light" ? "Dark" : "Light", toolsTab = 999 } });
            Equal(true, rejected.IsError == true, "invalid layout rejection");
            Equal(true, JsonNode.DeepEquals(layout, await Call("workspace_view", new { })), "rejected layout remains unchanged");
            await Call("camera", new { preview, action = "frame" });
            await Call("capture", new { target = "preview", preview, width = 800, height = 600 });
            var fields = await Call("property_fields", new { document, entry });
            string field = fields["fields"]!["fields"]![0]!["Id"]!.GetValue<string>();
            var changed = await Call("property_edit", new { document, entry, revision = fields["Revision"]!.GetValue<long>(), field, value = "2.5" });
            await Call("save_document", new { document, revision = changed["Revision"]!.GetValue<long>(), destination = Path.Combine(output, "edited-anim.zbd") });
            foreach (string path in new[] { "image.zbd", "soundsh.zbd", "interp.zbd", "m1/zrdr.zbd" })
            {
                var doc = await Call("open_document", new { path = Path.Combine(root, path) });
                string id = doc["id"]!.GetValue<string>();
                var list = await Call("assets", new { document = id, limit = 1 });
                var asset = list["items"]![0]!;
                string kind = asset["Kind"]!.GetValue<string>(); int index = asset["Index"]!.GetValue<int>();
                await Call("select_asset", new { document = id, kind, index });
                await Call("inspect_asset", new { document = id, kind, index });
                if (kind == "Texture")
                {
                    string texturePreview = (await Call("state", new { }))["preview"]!.GetValue<string>();
                    var view = await Call("preview_state", new { preview = texturePreview });
                    Equal(true, view["lod"] == null && view["horizon"] == null, "texture has no stale 3D controls");
                    await Call("texture_view", new { preview = texturePreview, zoom = 1, channel = 2, x = 0, y = 0 });
                }
                await Call("export", new { document = id, destination = Path.Combine(output, "exports"), assets = new[] { new { kind, index } }, jsonOnly = kind != "Texture" });
            }
            foreach (string source in sources) Equal(true, hashes[source].SequenceEqual(SHA256.HashData(File.ReadAllBytes(Path.Combine(root, source)))), "source hash " + source);
            File.WriteAllText(Path.Combine(output, "result.json"), JsonSerializer.Serialize(new { passed = true, exercised = exercised.Order().ToArray(), sourceHashesUnchanged = true }));
            Console.WriteLine("PASS: packaged live corpus regressions; " + output);
        }
        finally
        {
            var state = await Call("state", new { });
            foreach (var doc in state["documents"]!.AsArray())
                await Call("close_document", new { document = doc!["id"]!.GetValue<string>(), revision = doc["Revision"]!.GetValue<long>(), discard = true });
            await Call("workspace_view", new { changes = new { theme = originalLayout["theme"]!.GetValue<string>(),
                density = originalLayout["layout"]!["Density"]!.GetValue<string>(), tools = originalLayout["layout"]!["ToolsVisible"]!.GetValue<bool>(),
                inspectionPanelHeight = originalLayout["layout"]!["InspectionPanelHeight"]!.GetValue<double>() } });
        }

        async Task CheckSceneTree(string document, string preview)
        {
            var tree = await Call("scene_tree", new { document });
            string context = tree["context"]!.GetValue<string>();
            Equal("bound preview scene", tree["hierarchy"]!.GetValue<string>(), "active mission hierarchy");
            string rootRow = tree["children"]!["items"]![0]!["row"]!.GetValue<string>();
            var page = await Call("scene_tree", new { document, context, row = rootRow, limit = 200 });
            var candidate = page["children"]!["items"]!.AsArray().First(n => n!["Problem"] == null && n["ChildCount"]!.GetValue<int>() > 0);
            string row = candidate!["row"]!.GetValue<string>();
            var camera = await Call("camera", new { preview, action = "read" });
            var selected = await Call("scene_tree", new { document, preview, context, action = "select", row });
            Equal(row, selected["selected"]!.GetValue<string>(), "tree row selection");
            var after = await Call("camera", new { preview, action = "read" });
            Equal(true, JsonNode.DeepEquals(camera["Position"], after["Position"]) && JsonNode.DeepEquals(camera["LookDirection"], after["LookDirection"]), "tree selection leaves camera unchanged");
            await Call("scene_tree", new { document, preview, context, action = "expand", row });
            var layout = await Call("workspace_view", new { });
            foreach (string theme in new[] { "Dark", "Light" })
            {
                await Call("workspace_view", new { changes = new { navigatorTab = 3, navigator = true, navigatorWidth = 250, theme } });
                await Task.Delay(200);
                await Call("capture", new { target = "window", width = 1400, height = 900 });
                File.Copy(Path.Combine(output, "preview.png"), Path.Combine(output, "scene-tree-" + theme.ToLowerInvariant() + ".png"), true);
                Equal(context, (await Call("scene_tree", new { document }))["context"]!.GetValue<string>(), "theme/layout retains hierarchy");
            }
            await Call("workspace_view", new { changes = new { theme = originalLayout["theme"]!.GetValue<string>(), navigatorTab = 1 } });
            var collapsed = await Call("scene_tree", new { document, preview, context, action = "collapse", row });
            Equal(false, collapsed["row"]!["IsExpanded"]!.GetValue<bool>(), "tree collapse retained");
        }

        async Task<JsonNode> Call(string name, object arguments, string? expectedError = null)
        {
            exercised.Add(name);
            using var deadline = new CancellationTokenSource(TimeSpan.FromMinutes(2));
            var args = JsonSerializer.SerializeToNode(arguments)!.AsObject().ToDictionary(p => p.Key, p => (object?)JsonSerializer.SerializeToElement(p.Value));
            var reply = await client.CallToolAsync("zstudio_" + name, args, cancellationToken: deadline.Token);
            var data = JsonNode.Parse(reply.Content.OfType<TextContentBlock>().First().Text)!;
            if (expectedError != null)
            {
                Equal(true, reply.IsError == true, name + " expected rejection");
                Equal(expectedError, data["code"]!.GetValue<string>(), name + " rejection code");
                return data;
            }
            if (reply.IsError == true) throw new InvalidDataException(name + ": " + data);
            foreach (var image in reply.Content.OfType<ImageContentBlock>()) File.WriteAllBytes(Path.Combine(output, "preview.png"), image.DecodedData.ToArray());
            if (name != "operation" && data is JsonObject o && o.ContainsKey("State"))
            {
                while (data["State"]!.GetValue<string>() is "queued" or "running") { await Task.Delay(100, deadline.Token); data = await Call("operation", new { id = data["id"]!.GetValue<string>() }); }
                Equal("completed", data["State"]!.GetValue<string>(), name + ": " + data);
                return data["result"]!;
            }
            return data;
        }
    }

    private static System.Numerics.Vector3 ReadVector(JsonNode value) => new(value["X"]!.GetValue<float>(), value["Y"]!.GetValue<float>(), value["Z"]!.GetValue<float>());
    private static void Equal<T>(T expected, T actual, string message)
    { if (!EqualityComparer<T>.Default.Equals(expected, actual)) throw new InvalidDataException(message + $": expected {expected}, got {actual}"); }
}
