using System.IO;
using System.Numerics;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using Recoil.Zbd.Core;
using Recoil.Zbd.Core.Formats;

internal static class Mw3LiveCheck
{
    internal static async Task Run(McpClient client, string root)
    {
        root = Path.GetFullPath(root);
        string output = Path.Combine(Path.GetTempPath(), "zstudio-mw3-live-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(output);
        var sources = Directory.EnumerateFiles(root, "*.zbd", SearchOption.AllDirectories).ToDictionary(p => p, p => SHA256.HashData(File.ReadAllBytes(p)), StringComparer.OrdinalIgnoreCase);
        await Call("open_root", new { path = root });
        foreach (string map in new[] { "c1", "c2", "c3", "c4", "c4b", "t1" })
        {
            Console.WriteLine("Checking " + map + " world…");
            var doc = await Call("open_document", new { path = Path.Combine(root, map, "gamez.zbd") }); string document = doc["id"]!.GetValue<string>();
            await Call("select_asset", new { document, kind = "World", index = 0 }); string preview = await Preview();
            Console.WriteLine("Loaded " + map + " world.");
            var choices = await Call("missions", new { preview }); Require(choices["missions"]!["items"]!.AsArray().Count > 0, "No mission choices for " + map);
            await Call("camera", new { preview, action = "frame", target = "all" });
            await Capture(map + "-world");
            var graph = await Call("ai_networks", new { preview, limit = 1 });
            Require(graph["total"]!.GetValue<int>() > 0, "Missing mission AI for " + map);
            if (map == "c1")
            {
                var readers = choices["missions"]!["items"]!.AsArray();
                var camera = (await Call("preview_state", new { preview }))["camera"];
                await Call("missions", new { preview, archive = readers[1]!["Archive"]!.GetValue<string>() }); preview = await Preview();
                Require(JsonNode.DeepEquals(camera, (await Call("preview_state", new { preview }))["camera"]), "Mission selection changed camera");
                await Call("missions", new { preview, archive = readers[0]!["Archive"]!.GetValue<string>() }); preview = await Preview();
                await Call("scene_options", new { preview, changes = new { aiNodes = true } });
                var aiState = (await Call("preview_state", new { preview }))["ai"]!;
                Require(aiState["visible"]!.GetValue<bool>() && aiState["nodes"]!.GetValue<int>() > 0, "MW3 AI visualization is unavailable");
                var nodes = await Call("ai_nodes", new { preview, snapshot = aiState["snapshot"]!.GetValue<string>(), limit = 1 });
                await Call("pickup_lock", new { document, revision = doc["Revision"]!.GetValue<long>(), locked = false });
                await Call("ai_selection", new { preview, snapshot = aiState["snapshot"]!.GetValue<string>(), action = "select", node = nodes["items"]![0]!["node_id"]!.GetValue<string>() });
                await Call("camera", new { preview, action = "frame", target = "selected" });
                await Capture("c1-ai");
                string snapshot = aiState["snapshot"]!.GetValue<string>();
                var valveSources = await Call("ai_valve_selection", new { preview, snapshot, action = "sources" });
                var valveSource = valveSources["items"]!.AsArray().First(s => s!["Member"]!.GetValue<string>().Equals("valves.zrd", StringComparison.OrdinalIgnoreCase))!;
                var valveRows = await Call("ai_valve_selection", new { preview, snapshot, action = "records", archive = valveSource["Archive"]!.GetValue<string>(), memberIndex = valveSource["MemberIndex"]!.GetValue<int>(), limit = 1 });
                Require(valveRows["total"]!.GetValue<int>() > 0, "Missing authored valve definitions");
                var opened = await Call("ai_valve_selection", new { preview, snapshot, action = "properties", archive = valveSource["Archive"]!.GetValue<string>(), memberIndex = valveSource["MemberIndex"]!.GetValue<int>(), record = valveRows["items"]![0]!["record"]!.GetValue<string>() });
                Require(await Preview() == preview, "Valve Properties replaced the world preview");
                Require((await Call("properties_state", new { }))["content"]!["valves"]!.GetValue<bool>(), "Valve Properties was not published");
                await Call("ai_valve_selection", new { preview, snapshot, action = "overlay", visible = true });
                Require((await Call("preview_state", new { preview }))["ai"]!["valveOverlay"]!.GetValue<bool>(), "Valve overlay not enabled");
                await Capture("c1-valve-overlay");
                await Call("capture", new { target = "properties" });
                File.Copy(Path.Combine(output, "preview.png"), Path.Combine(output, "c1-valve-properties.png"), true);
                await Call("scene_options", new { preview, changes = new { wireframe = true } }); preview = await Preview();
                Require((await Call("preview_state", new { preview }))["ai"]!["valveOverlay"]!.GetValue<bool>(), "Scene refresh lost valve overlay");
                await Call("scene_options", new { preview, changes = new { wireframe = false } }); preview = await Preview();
                Console.WriteLine("PASS: authored valve discovery, pinned Properties, retained world preview and overlay across scene refresh.");
                await Call("close_document", new { document = opened["document"]!.GetValue<string>(), revision = opened["Revision"]!.GetValue<long>() });
            }
            Console.WriteLine($"PASS: {map} world, mission selector, textured capture and AI networks.");
            await Call("close_document", new { document, revision = doc["Revision"]!.GetValue<long>() });
        }
        var library = await Call("open_document", new { path = Path.Combine(root, "mechlib.zbd") }); string libraryId = library["id"]!.GetValue<string>();
        var models = await Call("assets", new { document = libraryId, query = "mech_vulture.flt" }); var model = models["items"]![0]!;
        await Call("select_asset", new { document = libraryId, kind = "Model", index = model["Index"]!.GetValue<int>() }); await Capture("mech-vulture");
        string member = model["member"]!.GetValue<string>(); var parts = await Call("mech_models", new { document = libraryId, member }); Require(parts["models"]!["total"]!.GetValue<int>() > 0, "Missing mech parts");
        var bundle = await Call("model_bundle_export", new { document = libraryId, kind = "Model", index = model["Index"]!.GetValue<int>(), destination = output });
        Require(File.Exists(bundle["Manifest"]!.GetValue<string>()), "Missing model bundle manifest");
        var sourceLibrary = await FormatRegistry.Default.OpenAsync(Path.Combine(root, "mechlib.zbd"));
        var assembly = (MechAssembly)sourceLibrary.Assets[model["Index"]!.GetValue<int>()].Content!;
        var firstMesh = sourceLibrary.Scene!.Models[assembly.FirstModel];
        var center = (firstMesh.Vertices.Aggregate(Vector3.Min) + firstMesh.Vertices.Aggregate(Vector3.Max)) / 2;
        string obj = Path.Combine(output, "replacement.obj");
        await File.WriteAllTextAsync(obj, string.Join('\n', new[] { center, center + new Vector3(.001f,0,0), center + new Vector3(0,.001f,0) }.Select(v => FormattableString.Invariant($"v {v.X:R} {v.Y:R} {v.Z:R}"))) + "\nvt 0 0\nvt 1 0\nvt 0 1\nvn 0 0 1\nf 1/1/1 2/2/1 3/3/1\n");
        var changedLibrary = await Call("mech_model_replace", new { document = libraryId, revision = 0, member, localModel = 0, material = firstMesh.Polygons[0].MaterialIndex, path = obj });
        var undoLibrary = await Call("undo_redo", new { document = libraryId, revision = changedLibrary["Revision"]!.GetValue<long>(), action = "undo" });
        var redoLibrary = await Call("undo_redo", new { document = libraryId, revision = undoLibrary["Revision"]!.GetValue<long>(), action = "redo" });
        string editedLibrary = Path.Combine(output, "edited-mechlib.zbd");
        var savedLibrary = await Call("save_document", new { document = libraryId, revision = redoLibrary["Revision"]!.GetValue<long>(), destination = editedLibrary });
        var reopenedLibrary = await FormatRegistry.Default.OpenAsync(editedLibrary);
        Require(reopenedLibrary.Scene!.Models[assembly.FirstModel].Vertices.Length == 3, "Mech replacement did not survive Save As");
        await Call("close_document", new { document = libraryId, revision = savedLibrary["Revision"]!.GetValue<long>() });
        var motion = await Call("open_document", new { path = Path.Combine(root, "motion.zbd") }); string motionId = motion["id"]!.GetValue<string>();
        var motionAssets = await Call("assets", new { document = motionId, query = "vulture_walk" }); var motionAsset = motionAssets["items"]![0]!;
        await Call("select_asset", new { document = motionId, kind = "Motion", index = motionAsset["Index"]!.GetValue<int>() }); string motionPreview = await Preview();
        var before = await Call("motion_preview", new { preview = motionPreview, action = "state" }); Require(before["assembly"] != null, "Missing automatic motion binding");
        await Call("motion_preview", new { preview = motionPreview, action = "seek", seconds = .2 }); await Capture("motion-vulture");
        await Call("motion_preview", new { preview = motionPreview, action = "play" }); await Task.Delay(120); var playing = await Call("motion_preview", new { preview = motionPreview, action = "pause" }); Require(playing["seconds"]!.GetValue<double>() != .2, "Motion did not advance");
        var changedMotion = await Call("motion_edit", new { document = motionId, revision = 0, member = motionAsset["member"]!.GetValue<string>(), action = "timing", loopSeconds = 3.2 });
        Require(Math.Abs((await Call("motion_preview", new { preview = await Preview(), action = "state" }))["loopSeconds"]!.GetValue<double>() - 3.2) < .0001, "Motion preview did not receive the accepted edit");
        var undoMotion = await Call("undo_redo", new { document = motionId, revision = changedMotion["Revision"]!.GetValue<long>(), action = "undo" });
        await Call("resource_properties", new { document = motionId, member = motionAsset["member"]!.GetValue<string>(), action = "open", part = 0, frame = 1 }); await Capture("motion-properties"); await Call("properties_close", new { });
        await Call("close_document", new { document = motionId, revision = undoMotion["Revision"]!.GetValue<long>() });
        Console.WriteLine("PASS: mech preview/export/replacement/undo/Save As, motion binding/play/seek/edit/undo and pinned motion Properties.");
        string animationPath = Path.Combine(root, "c1", "anim.zbd"); var source = await FormatRegistry.Default.OpenAsync(animationPath);
        using var resolver = new AssetResolver(root); var world = await resolver.OpenCachedAsync(Path.Combine(root, "c1", "gamez.zbd"), default);
        var entry = source.Animations!.Entries.First(e => world.Scene!.Nodes.Count(n => n.Name == e.RootName && n.Class == "object3d") == 1 && e.Sequences.Any(s => s.Events.Any(v => v.Spec?.DurationOffset is >= 0 and int at && v.F32(at) >= 1)));
        var animation = await Call("open_document", new { path = animationPath }); string animationId = animation["id"]!.GetValue<string>();
        await Call("select_asset", new { document = animationId, kind = "Animation", index = entry.Index }); string animationPreview = await Preview();
        await Call("animation_options", new { preview = animationPreview, changes = new { mute = true, map = true, range = 2 } });
        await Call("animation_transport", new { preview = animationPreview, action = "seek", seconds = .2 }); await Capture("compiled-animation");
        var animationChoices = await Call("missions", new { preview = animationPreview });
        await Call("missions", new { preview = animationPreview, archive = animationChoices["missions"]!["items"]![1]!["Archive"]!.GetValue<string>() });
        var state = await Call("preview_state", new { preview = await Preview() }); Require(state["animation"]!["time"]!.GetValue<double>() >= .19, "Mission change reset animation playhead");
        await Call("close_document", new { document = animationId, revision = 0 });
        foreach (var (path, hash) in sources) Require(hash.AsSpan().SequenceEqual(SHA256.HashData(File.ReadAllBytes(path))), "Source changed: " + path);
        Console.WriteLine($"PASS: MW3 version-39 animation playback/seek and mission refresh; {sources.Count} source hashes unchanged. Captures/exports: {output}");

        async Task<string> Preview() => (await Call("state", new { }))["preview"]!.GetValue<string>();
        async Task Capture(string name)
        {
            // Let framing animation and queued GPU work finish before visual evidence.
            await Task.Delay(1000); await Call("capture", new { target = "window", width = 1400, height = 900 });
            File.Copy(Path.Combine(output, "preview.png"), Path.Combine(output, name + ".png"), true);
        }
        async Task<JsonNode> Call(string name, object values)
        {
            using var deadline = new CancellationTokenSource(TimeSpan.FromMinutes(3));
            var args = JsonSerializer.SerializeToNode(values)!.AsObject().ToDictionary(p => p.Key, p => (object?)JsonSerializer.SerializeToElement(p.Value));
            var reply = await client.CallToolAsync("zstudio_" + name, args, cancellationToken: deadline.Token);
            var data = JsonNode.Parse(reply.Content.OfType<TextContentBlock>().First().Text)!;
            if (reply.IsError == true) throw new InvalidDataException(name + ": " + data);
            foreach (var image in reply.Content.OfType<ImageContentBlock>()) File.WriteAllBytes(Path.Combine(output, "preview.png"), image.DecodedData.ToArray());
            if (name != "operation" && data is JsonObject o && o.ContainsKey("State"))
            {
                while (data["State"]!.GetValue<string>() is "queued" or "running") { await Task.Delay(100, deadline.Token); data = await Call("operation", new { id = data["id"]!.GetValue<string>() }); }
                Require(data["State"]!.GetValue<string>() == "completed", name + ": " + data); return data["result"]!;
            }
            return data;
        }
    }
    private static void Require(bool condition, string message) { if (!condition) throw new InvalidDataException(message); }
}
