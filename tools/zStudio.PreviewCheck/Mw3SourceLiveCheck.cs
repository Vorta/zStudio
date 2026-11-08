using System.IO;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using zStudio.Core;
using zStudio.Core.Export;
using zStudio.Core.Sources;

/// <summary>Packaged native source authoring on a disposable copy, through the real shared GUI/MCP services.</summary>
internal static class Mw3SourceLiveCheck
{
    internal static async Task Run(McpClient client, string original)
    {
        string output = Path.Combine(Path.GetTempPath(), "zstudio-mw3-source-live-" + Guid.NewGuid().ToString("N"));
        string root = Path.Combine(output, "source"); Directory.CreateDirectory(root);
        Dictionary<string, string> hashes = new(StringComparer.OrdinalIgnoreCase);
        long bytes = 0, characters = 0;
        foreach (string folder in new[] { "data", "gamegen" })
            foreach (string input in Directory.EnumerateFiles(Path.Combine(original, folder), "*", new EnumerationOptions { RecurseSubdirectories = true, AttributesToSkip = FileAttributes.ReparsePoint }))
            {
                if (hashes.Count >= SourceProject.MaximumFiles || (bytes += new FileInfo(input).Length) > 2L << 30 || (characters += input.Length) > 32L << 20)
                    throw new InvalidDataException("The native source smoke-check copy exceeds its file, byte or path allowance.");
                string relative = Path.GetRelativePath(original, input), destination = Path.Combine(root, relative);
                Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                File.Copy(input, destination);
                hashes.Add(relative, Hash(destination));
            }
        Console.WriteLine("Native source check copy and captures: " + output);
        await Call("open_root", new { path = root, project = true });
        var maps = (await Call("source_world_list", new { }))["items"]!.AsArray();
        if (maps.Any(m => m!["mission"]!.GetValue<string>() == "lab")) throw new InvalidDataException("The library was listed as a campaign map.");
        foreach (string map in new[] { "c1", "c2", "c3", "c4", "c4b", "t1" })
        {
            await Call("source_world_open", new { mission = map });
            var state = await Call("state", new { }); string preview = state["preview"]!.GetValue<string>();
            await Call("camera", new { preview, action = "frame", target = "all" });
            await Task.Delay(700);
            var capture = await client.CallToolAsync("zstudio_capture", new Dictionary<string, object?> { ["target"] = "preview", ["preview"] = preview, ["width"] = 1400, ["height"] = 900 });
            if (capture.IsError == true) throw new InvalidDataException("The native source preview could not be captured.");
            byte[] image = capture.Content.OfType<ImageContentBlock>().First().DecodedData.ToArray();
            File.WriteAllBytes(Path.Combine(output, map + "-source.png"), image);
            byte[] rgba = PngDecoder.Decode(image).Rgba; bool varied = false;
            for (int i = 4; i < rgba.Length; i += 4) if (!rgba.AsSpan(0, 4).SequenceEqual(rgba.AsSpan(i, 4))) { varied = true; break; }
            if (!varied) throw new InvalidDataException(map + ": no rendered native source content.");
            var source = await SourceWorlds.LoadSceneAsync(root, map);
            var baseline = source.MissionScene(zStudio.Core.MissionDifficulty.Medium);
            if (baseline.AnimationEntryRoots.Count == 0 || baseline.AnimationUses.Count == 0 || baseline.PlayerStart == null)
                throw new InvalidDataException(map + ": native animation bindings, associations or player start missing.");
            if (map == "c1")
            {
                var groups = baseline.PreviewDiagnostics.Where(d => d.Category == "Animation references").ToArray();
                if (groups.Length != 6 || groups.Sum(d => d.Count ?? 0) != 39 || groups.Any(d => d.Severity != "Warning" || string.IsNullOrEmpty(d.Details)))
                    throw new InvalidDataException("Retail c1 missing-animation diagnostics lost grouping or call-site details.");
                if (!baseline.PreviewDiagnostics.Any(d => d.Severity == "Information" && d.Message.Contains("procedural motion with flat-ground")))
                    throw new InvalidDataException("Native procedural-motion limitation is not informational.");
                var problems = await Call("problems", new { query = "Animation associations:", limit = 100 });
                var rows = problems["items"]!.AsArray();
                if (rows.Count != 6 || rows.Any(d => d!["Severity"]!.GetValue<string>() != "Warning" || d["ContextDetails"] == null))
                    throw new InvalidDataException("GUI/MCP grouped diagnostic presentation differs from Core.");
                var searched = await Call("problems", new { query = "mech_destroy02", limit = 100 });
                if (searched["items"]!.AsArray().Count == 0) throw new InvalidDataException("Grouped call-site details are not searchable.");
                await Call("workspace_view", new { tools = true, toolsHeight = 330 });
                var diagnosticCapture = await client.CallToolAsync("zstudio_capture", new Dictionary<string, object?> { ["target"] = "window" });
                if (diagnosticCapture.IsError == true) throw new InvalidDataException("Cannot capture native map diagnostics.");
                File.WriteAllBytes(Path.Combine(output, "c1-diagnostics.png"), diagnosticCapture.Content.OfType<ImageContentBlock>().First().DecodedData.ToArray());
                var tower = baseline.Scene.Nodes.Single(n => n.Name == "rbrgtwr6");
                foreach (int child in tower.Children)
                {
                    var node = baseline.Scene.Nodes[child];
                    if (node.Name is "healthy" or "destroy" or "dbase" && ((node.Metadata.UInt("flags") & 4) != 0) != (node.Name == "healthy"))
                        throw new InvalidDataException("rbrgtwr6 starting branches overlap.");
                }
                await Call("camera", new { preview, action = "frame", node = tower.Index });
                var closeup = await client.CallToolAsync("zstudio_capture", new Dictionary<string, object?> { ["target"] = "preview", ["preview"] = preview, ["width"] = 1400, ["height"] = 900 });
                if (closeup.IsError == true) throw new InvalidDataException("Cannot capture initialized bridge tower.");
                File.WriteAllBytes(Path.Combine(output, "c1-rbrgtwr6.png"), closeup.Content.OfType<ImageContentBlock>().First().DecodedData.ToArray());
                var documentId = Document(state)["id"]!.GetValue<string>();
                var tree = await Call("scene_tree", new { document = documentId });
                var uses = await Call("scene_tree", new { document = documentId, preview, context = tree["context"]!.GetValue<string>(), action = "animationUses", node = tower.Index });
                if (uses["total"]!.GetValue<int>() == 0) throw new InvalidDataException("The bridge tower has no animation navigation.");
                int repeated = source.Animations!.Entries.Single(e => e.Name == "dest_shrubgrp#17").Index;
                int bridge = source.Animations.Entries.First(e => e.RootName == "rbrgtwr6").Index;
                long revision = Document(state)["Revision"]!.GetValue<long>();
                await Call("animation_mode", new { document = documentId, preview, revision, action = "open", entry = repeated });
                preview = (await Call("state", new { }))["preview"]!.GetValue<string>();
                await Call("animation_mode", new { document = documentId, preview, revision, action = "open", entry = bridge });
                preview = (await Call("state", new { }))["preview"]!.GetValue<string>();
                var restored = await Call("animation_mode", new { preview, action = "back" });
                if (restored["opened"]!["entry"]!.GetValue<int>() != repeated) throw new InvalidDataException("Back lost the selected native repeated-root binding.");
                preview = (await Call("state", new { }))["preview"]!.GetValue<string>();
                restored = await Call("animation_mode", new { preview, action = "exit" });
                if (restored["keptScenario"]!["entry"]!.GetValue<int>() != repeated) throw new InvalidDataException("Exit lost the kept native repeated-root binding.");
                preview = (await Call("state", new { }))["preview"]!.GetValue<string>();
            }
            var choices = (await Call("missions", new { preview }))["missions"]!["items"]!.AsArray();
            if (choices.Count < 2) throw new InvalidDataException(map + ": missing native mission choices.");
            await Call("missions", new { preview, sourceMission = choices[1]!["SourceMission"]!.GetValue<string>() });
            state = await Call("state", new { }); preview = state["preview"]!.GetValue<string>();
            await Call("ai_networks", new { preview, limit = 1 });
            var document = Document(state);
            await Call("source_group_add", new { document = document["id"]!.GetValue<string>(), revision = document["Revision"]!.GetValue<long>(), name = "authoring_proof" });
            document = Document(await Call("state", new { }));
            await Call("undo_redo", new { document = document["id"]!.GetValue<string>(), revision = document["Revision"]!.GetValue<long>(), action = "undo" });
            document = Document(await Call("state", new { }));
            if (document["IsDirty"]!.GetValue<bool>()) throw new InvalidDataException("Native source undo did not restore clean inputs.");
            await Call("close_document", new { document = document["id"]!.GetValue<string>(), revision = document["Revision"]!.GetValue<long>() });
            Console.WriteLine($"PASS: {map} native source render, mission switch, AI and undoable group.");
        }
        foreach (var (relative, hash) in hashes)
            if (Hash(Path.Combine(original, relative)) != hash || Hash(Path.Combine(root, relative)) != hash)
                throw new InvalidDataException("The native source check changed " + relative);
        Console.WriteLine($"PASS: {hashes.Count} native source inputs and copied files unchanged.");

        static string Hash(string path) { using var stream = File.OpenRead(path); return Convert.ToHexString(SHA256.HashData(stream)); }
        static JsonNode Document(JsonNode state) => state["documents"]!.AsArray().Single(d => d!["id"]!.GetValue<string>() == state["activeDocument"]!.GetValue<string>())!;
        async Task<JsonNode> Call(string name, object values)
        {
            using var deadline = new CancellationTokenSource(TimeSpan.FromMinutes(4));
            var arguments = JsonSerializer.SerializeToNode(values)!.AsObject().ToDictionary(p => p.Key, p => (object?)JsonSerializer.SerializeToElement(p.Value));
            var reply = await client.CallToolAsync("zstudio_" + name, arguments, cancellationToken: deadline.Token);
            var data = JsonNode.Parse(reply.Content.OfType<TextContentBlock>().First().Text)!;
            if (reply.IsError == true) throw new InvalidDataException(name + ": " + data);
            if (name != "operation" && data["State"] != null)
            {
                while (data["State"]!.GetValue<string>() is "queued" or "running") { await Task.Delay(100, deadline.Token); data = await Call("operation", new { id = data["id"]!.GetValue<string>() }); }
                if (data["State"]!.GetValue<string>() != "completed") throw new InvalidDataException(name + ": " + data);
                return data["result"]!;
            }
            return data;
        }
    }
}
