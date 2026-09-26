using System.IO;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;

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
            await Call("capture", new { target = "preview", width = 800, height = 600 });
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
            await Call("workspace_view", new { changes = new { theme = originalLayout["theme"]!.GetValue<string>() } });
        }

        async Task<JsonNode> Call(string name, object arguments)
        {
            exercised.Add(name);
            using var deadline = new CancellationTokenSource(TimeSpan.FromMinutes(2));
            var args = JsonSerializer.SerializeToNode(arguments)!.AsObject().ToDictionary(p => p.Key, p => (object?)JsonSerializer.SerializeToElement(p.Value));
            var reply = await client.CallToolAsync("zstudio_" + name, args, cancellationToken: deadline.Token);
            var data = JsonNode.Parse(reply.Content.OfType<TextContentBlock>().First().Text)!;
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

    private static void Equal<T>(T expected, T actual, string message)
    { if (!EqualityComparer<T>.Default.Equals(expected, actual)) throw new InvalidDataException(message + $": expected {expected}, got {actual}"); }
}
