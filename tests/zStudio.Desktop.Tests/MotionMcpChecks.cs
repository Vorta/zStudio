using System.Buffers.Binary;
using System.IO;
using System.IO.Pipes;
using System.Reflection;
using System.Text.Json.Nodes;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using zStudio.Core;
using zStudio.Core.Formats;
using zStudio.Mcp;
using Xunit;

namespace zStudio.Desktop.Tests;

internal static class MotionMcpChecks
{
    internal static async Task Run()
    {
        string folder = Path.Combine(Path.GetTempPath(), "zstudio-motion-protocol-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(folder);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(45)); var token = deadline.Token;
        var main = new MainWindow { Left = -12000, ShowInTaskbar = false }; main.Show();
        try
        {
            using MemoryStream stream = new(); using BinaryWriter writer = new(stream);
            writer.Write(4); writer.Write(2f); writer.Write(2); writer.Write(1); writer.Write(-1f); writer.Write(1f);
            writer.Write(4); writer.Write("body"u8); writer.Write(12);
            for (int i = 0; i < 9; i++) writer.Write(i >= 6 ? i + 1f : 0f);
            for (int i = 0; i < 3; i++) { writer.Write(1f); writer.Write(0f); writer.Write(0f); writer.Write(0f); }
            byte[] payload = stream.ToArray(), archive = new byte[payload.Length + 156]; payload.CopyTo(archive, 0);
            BinaryPrimitives.WriteInt32LittleEndian(archive.AsSpan(payload.Length + 4), payload.Length); "test_motion"u8.CopyTo(archive.AsSpan(payload.Length + 8));
            BinaryPrimitives.WriteInt32LittleEndian(archive.AsSpan(archive.Length - 8), 1); BinaryPrimitives.WriteInt32LittleEndian(archive.AsSpan(archive.Length - 4), 1);
            string path = Path.Combine(folder, "motion.zbd"); await File.WriteAllBytesAsync(path, archive, token);
            string libraryPath = Path.Combine(folder, "library.zbd");
            await File.WriteAllBytesAsync(libraryPath, MotionFixture.Library(), token);
            await using var host = new LocalMcpHost(main.Commands, "test");
            await using var pipe = new NamedPipeClientStream(".", host.Instance.Pipe, PipeDirection.InOut, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly); await pipe.ConnectAsync(token);
            await using var client = await McpClient.CreateAsync(new StreamClientTransport(pipe, pipe), cancellationToken: token);
            await Job("open_document", new() { ["path"] = path }); var doc = main.ViewModel.Documents.Single(); Guid member = doc.ResourceMembers!.Members[0].Id;
            Dictionary<string, object?> Args(params (string Key, object Value)[] values)
            { var a = new Dictionary<string, object?> { ["document"] = doc.SessionId.ToString(), ["member"] = member.ToString() }; foreach (var (k, v) in values) a[k] = v; return a; }
            var tracks = await Call("motion_records", Args()); Assert.Equal(2, tracks["FrameCount"]!.GetValue<int>());
            using (MemoryStream largeStream = new())
            {
                using BinaryWriter largeWriter = new(largeStream);
                largeWriter.Write(4); largeWriter.Write(1f); largeWriter.Write(1); largeWriter.Write(200); largeWriter.Write(-1f); largeWriter.Write(1f);
                for (int part = 0; part < 200; part++)
                {
                    largeWriter.Write(4096); largeWriter.Write(Enumerable.Repeat((byte)1, 4096).ToArray()); largeWriter.Write(12);
                    for (int i = 0; i < 6; i++) largeWriter.Write(0f);
                    for (int i = 0; i < 2; i++) { largeWriter.Write(1f); largeWriter.Write(0f); largeWriter.Write(0f); largeWriter.Write(0f); }
                }
                using var largeDoc = new DocumentModel(FormatRegistry.Default.OpenBytes(Path.Combine(folder, "large.zbd"), MotionFixture.Archive(("large", largeStream.ToArray())), token: token));
                main.ViewModel.Documents.Add(largeDoc);
                try
                {
                    var page = await Call("motion_records", new() { ["document"] = largeDoc.SessionId.ToString(), ["member"] = largeDoc.ResourceMembers!.Members[0].Id.ToString(), ["limit"] = 200 });
                    Assert.Equal(200, page["rows"]!["items"]!.AsArray().Count);
                    Assert.True(page["rows"]!["items"]![0]!["nameTruncated"]!.GetValue<bool>());
                    Assert.Equal(4096, page["rows"]!["items"]![0]!["nameCharacters"]!.GetValue<int>());
                    Assert.True(page.ToJsonString().Length < 1024 * 1024);
                }
                finally { main.ViewModel.CloseResolved(largeDoc); }
            }
            var frames = await Call("motion_records", Args(("part", 0))); Assert.Equal(3, frames["rows"]!["items"]!.AsArray().Count);
            {
                // A clip at the 100,000-frame limit: a one-row page constructs only its rows, including the closing sample.
                using MemoryStream longStream = new(); using BinaryWriter longWriter = new(longStream);
                longWriter.Write(4); longWriter.Write(1f); longWriter.Write(100_000); longWriter.Write(1); longWriter.Write(-1f); longWriter.Write(1f);
                longWriter.Write(4); longWriter.Write("body"u8); longWriter.Write(12); longWriter.Write(new byte[100_001 * 12]);
                for (int i = 0; i <= 100_000; i++) { longWriter.Write(1f); longWriter.Write(0f); longWriter.Write(0f); longWriter.Write(0f); }
                using var longDoc = new DocumentModel(FormatRegistry.Default.OpenBytes(Path.Combine(folder, "long.zbd"), MotionFixture.Archive(("long", longStream.ToArray())), token: token));
                main.ViewModel.Documents.Add(longDoc);
                try
                {
                    Dictionary<string, object?> LongArgs(int offset, int limit) => new() { ["document"] = longDoc.SessionId.ToString(), ["member"] = longDoc.ResourceMembers!.Members[0].Id.ToString(), ["part"] = 0, ["offset"] = offset, ["limit"] = limit };
                    await Call("motion_records", LongArgs(0, 1));
                    long before = GC.GetTotalAllocatedBytes(true);
                    var tail = await Call("motion_records", LongArgs(99_999, 5));
                    long allocated = GC.GetTotalAllocatedBytes(true) - before;
                    Assert.True(allocated < 6_000_000, $"Allocated {allocated:N0} bytes for a two-row frame page.");
                    Assert.Equal(100_001, tail["rows"]!["total"]!.GetValue<int>()); Assert.Null(tail["rows"]!["nextOffset"]);
                    var rows = tail["rows"]!["items"]!.AsArray(); Assert.Equal(2, rows.Count);
                    Assert.Equal(99_999, rows[0]!["index"]!.GetValue<int>()); Assert.True(rows[1]!["closing"]!.GetValue<bool>());
                    Assert.Empty((await Call("motion_records", LongArgs(200_000, 5)))["rows"]!["items"]!.AsArray());
                }
                finally { main.ViewModel.CloseResolved(longDoc); }
            }
            var editor = (MotionEditor)typeof(MainWindow).GetField("motion", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(main)!;
            Assert.NotNull(editor); await editor.SelectAssemblyAsync(3, token); editor.Seek(1);
            string preview = (await Call("state", new()))["preview"]!.GetValue<string>();
            var visibleNodes = (await Call("scene_nodes", new() { ["preview"] = preview }))["items"]!.AsArray();
            Assert.Single(visibleNodes); Assert.Equal(0, visibleNodes[0]!["Index"]!.GetValue<int>());
            var hierarchy = await Call("scene_tree", new() { ["document"] = doc.SessionId.ToString() });
            Assert.Single(hierarchy["children"]!["items"]!.AsArray());
            await editor.SelectAssemblyAsync(4, token);
            visibleNodes = (await Call("scene_nodes", new() { ["preview"] = preview }))["items"]!.AsArray();
            Assert.Single(visibleNodes); Assert.Equal(1, visibleNodes[0]!["Index"]!.GetValue<int>());
            hierarchy = await Call("scene_tree", new() { ["document"] = doc.SessionId.ToString() });
            Assert.Equal(1, hierarchy["children"]!["items"]![0]!["node"]!.GetValue<int>());
            foreach (string command in new[] { "scene_selection", "scene_properties", "camera" })
            {
                var targetArgs = new Dictionary<string, object?> { ["preview"] = preview, ["node"] = 0 };
                if (command != "scene_properties") targetArgs["action"] = command == "scene_selection" ? "select" : "frame";
                var invalidTarget = await client.CallToolAsync("zstudio_" + command, targetArgs, cancellationToken: token);
                Assert.True(invalidTarget.IsError == true);
                Assert.Contains("stale_record", invalidTarget.Content.OfType<TextContentBlock>().Single().Text);
            }
            await editor.SelectAssemblyAsync(3, token);
            await CheckAssemblyLoads();
            var catalog = await Call("event_catalog", new() { ["version"] = 39 }); Assert.Contains(catalog["items"]!.AsArray(), e => e!["Type"]!.GetValue<int>() == 42);
            await Job("open_document", new() { ["path"] = libraryPath });
            var libraryDoc = main.ViewModel.Documents.Single(d => d.Path == libraryPath);
            await Job("select_asset", new() { ["document"] = libraryDoc.SessionId.ToString(), ["kind"] = "Model", ["index"] = 3 });
            await CheckLargeMotionInspection();
            var libraryScene = libraryDoc.Document.Scene!; var libraryAsset = libraryDoc.Document.Assets[3];
            var assembly = (MechAssembly)libraryAsset.Content!;
            libraryScene.Nodes.Clear();
            for (int i = 0; i < 1000; i++) libraryScene.Nodes.Add(new(i, $"reference_{i:D4}", "object3d", assembly.FirstModel, [], [], [], []));
            libraryAsset.Content = assembly with { RootNode = 0, NodeCount = 1000 };
            Dictionary<string, object?> MechArgs(params (string Key, object Value)[] values)
            { var a = new Dictionary<string, object?> { ["document"] = libraryDoc.SessionId.ToString(), ["member"] = libraryDoc.ResourceMembers!.Members[3].Id.ToString() }; foreach (var (k, v) in values) a[k] = v; return a; }
            var models = await Call("mech_models", MechArgs(("limit", 1)));
            var model = Assert.Single(models["models"]!["items"]!.AsArray())!;
            Assert.Equal(32, model["nodes"]!.AsArray().Count); Assert.Equal(1000, model["nodeCount"]!.GetValue<int>()); Assert.True(model["nodesTruncated"]!.GetValue<bool>());
            Assert.True(models.ToJsonString().Length < 10_000);
            var references = await Call("mech_models", MechArgs(("section", "nodes"), ("localModel", 0), ("offset", 995), ("limit", 5)));
            Assert.Equal(1000, references["nodes"]!["total"]!.GetValue<int>()); Assert.Null(references["nodes"]!["nextOffset"]);
            Assert.Equal(995, references["nodes"]!["items"]![0]!["localNode"]!.GetValue<int>());
            references = await Call("mech_models", MechArgs(("section", "nodes"), ("localModel", 0), ("query", "REFERENCE_0999")));
            Assert.Equal(1, references["nodes"]!["total"]!.GetValue<int>()); Assert.Equal(999, references["nodes"]!["items"]![0]!["localNode"]!.GetValue<int>());
            references = await Call("mech_models", MechArgs(("section", "nodes"), ("localModel", 0), ("query", "absent")));
            // Model rows are found by the node labels users see, not only their synthetic "Model N" label.
            var byNode = await Call("mech_models", MechArgs(("query", "REFERENCE_0999")));
            Assert.Equal(1, byNode["models"]!["total"]!.GetValue<int>()); Assert.Equal(0, byNode["models"]!["items"]![0]!["localModel"]!.GetValue<int>());
            Assert.Equal(0, references["nodes"]!["total"]!.GetValue<int>());
            foreach (var invalidArgs in new[] { MechArgs(("section", "nodes")), MechArgs(("localModel", -1)), MechArgs(("localModel", assembly.ModelCount)) })
                Assert.True((await client.CallToolAsync("zstudio_mech_models", invalidArgs, cancellationToken: token)).IsError);
            var sharedMaterials = await Call("mech_models", MechArgs(("section", "materials"), ("localModel", 0)));
            Assert.Equal(1, sharedMaterials["materials"]!["total"]!.GetValue<int>());
            for (int i = libraryScene.Materials.Count; i < 40; i++) libraryScene.Materials.Add(new() { ["alpha"] = 255 });
            var meshModel = libraryScene.Models[assembly.FirstModel];
            libraryScene.Models[assembly.FirstModel] = meshModel with { Polygons = Enumerable.Range(0, 40).Select(i => meshModel.Polygons[0] with { MaterialIndex = i }).ToArray() };
            models = await Call("mech_models", MechArgs(("query", "MODEL 0")));
            model = Assert.Single(models["models"]!["items"]!.AsArray())!;
            Assert.Equal(32, model["materials"]!.AsArray().Count); Assert.Equal(40, model["materialCount"]!.GetValue<int>()); Assert.True(model["materialsTruncated"]!.GetValue<bool>());
            sharedMaterials = await Call("mech_models", MechArgs(("section", "materials"), ("localModel", 0), ("offset", 38), ("limit", 2)));
            Assert.Equal(40, sharedMaterials["materials"]!["total"]!.GetValue<int>()); Assert.Null(sharedMaterials["materials"]!["nextOffset"]);
            Assert.Equal(38, sharedMaterials["materials"]!["items"]![0]!["index"]!.GetValue<int>());
            sharedMaterials = await Call("mech_models", MechArgs(("section", "materials"), ("query", "MATERIAL 39")));
            // Material labels include the texture name shown in the GUI; material 0 is the textured one here.
            Assert.Equal(39, (await Call("mech_models", MechArgs(("section", "materials"), ("query", "SOLID COLOR"))))["materials"]!["total"]!.GetValue<int>());
            var textured = await Call("mech_models", MechArgs(("section", "materials"), ("query", "SAMPLE")));
            Assert.Equal(1, textured["materials"]!["total"]!.GetValue<int>()); Assert.Equal(0, textured["materials"]!["items"]![0]!["index"]!.GetValue<int>());
            // Rows carry the GUI picker label that the query matched.
            Assert.Contains("sample", textured["materials"]!["items"]![0]!["label"]!.GetValue<string>(), StringComparison.OrdinalIgnoreCase);
            Assert.Equal("Material 39 · solid color", sharedMaterials["materials"]!["items"]![0]!["label"]!.GetValue<string>());
            Assert.Equal(1, sharedMaterials["materials"]!["total"]!.GetValue<int>());
            MotionEditor? CurrentMotion() => (MotionEditor?)typeof(MainWindow).GetField("motion", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(main);
            async Task CheckLargeMotionInspection()
            {
                var seed = MotionClip.Read(payload, token); string name = new('x', 4096); byte[] nameBytes = new byte[4100];
                BinaryPrimitives.WriteInt32LittleEndian(nameBytes, 4096); System.Text.Encoding.Latin1.GetBytes(name).CopyTo(nameBytes, 4);
                var part = seed.Parts[0] with { Name = name, NameBytes = nameBytes };
                var clip = new MotionClip { Header = seed.Header, LoopTime = seed.LoopTime, FrameCount = seed.FrameCount, Parts = Enumerable.Repeat(part, 4096).ToArray() };
                var large = new DocumentModel(FormatRegistry.Default.OpenBytes(Path.Combine(folder, "large.zbd"), MotionFixture.Archive(("large_motion", MotionFixture.Write(clip))), token: token));
                main.ViewModel.Documents.Add(large);
                var inspected = await Call("inspect_asset", new() { ["document"] = large.SessionId.ToString(), ["kind"] = "Motion", ["index"] = 0 });
                var metadata = inspected["source"]!["properties"]!["motion"]!;
                Assert.Equal(4096, metadata["part_count"]!.GetValue<int>()); Assert.True(metadata["parts_truncated"]!.GetValue<bool>());
                Assert.Equal(32, metadata["parts"]!.AsArray().Count);
                Assert.True(inspected.ToJsonString().Length < 40_000);
                var page = await Call("motion_records", new() { ["document"] = large.SessionId.ToString(), ["member"] = large.ResourceMembers!.Members[0].Id.ToString(), ["offset"] = 4095, ["limit"] = 1 });
                Assert.Equal(4096, page["rows"]!["total"]!.GetValue<int>()); Assert.Null(page["rows"]!["nextOffset"]);
                var row = Assert.Single(page["rows"]!["items"]!.AsArray())!;
                Assert.Equal(name[..512], row["Name"]!.GetValue<string>());
                Assert.Equal(name.Length, row["nameCharacters"]!.GetValue<int>()); Assert.True(row["nameTruncated"]!.GetValue<bool>());
                Assert.Equal(name, ((MotionClip)large.Document.Assets[0].Content!).Parts[^1].Name);
                await Job("select_asset", new() { ["document"] = large.SessionId.ToString(), ["kind"] = "Motion", ["index"] = 0 });
                var active = Assert.IsType<MotionEditor>(CurrentMotion()); await active.SelectAssemblyAsync(3, token);
                string preview = ((Guid)typeof(MainWindow).GetField("previewId", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(main)!).ToString();
                var state = await Job("motion_preview", new() { ["preview"] = preview, ["action"] = "state" });
                Assert.Equal(4097, state["diagnosticCount"]!.GetValue<int>()); Assert.True(state["diagnosticsTruncated"]!.GetValue<bool>());
                Assert.Equal(32, state["diagnostics"]!.AsArray().Count); Assert.All(state["diagnostics"]!.AsArray(), n => Assert.InRange(n!.GetValue<string>().Length, 1, 513));
                Assert.True(state.ToJsonString().Length < 25_000);
                var combined = await Call("preview_state", new() { ["preview"] = preview });
                Assert.True(JsonNode.DeepEquals(state["diagnostics"], combined["motion"]!["diagnostics"]));
                Assert.True(combined.ToJsonString().Length < 30_000);
                var boundLibrary = (AssetDocument)typeof(MotionEditor).GetField("library", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(active)!;
                for (int i = 0; i < 40; i++) boundLibrary.Add(AssetKind.Model, boundLibrary.Assets.Count, $"extra_{i:D2}", 0, 0, content: new MechAssembly(0, 0, 1, 0, 1));
                state = await Job("motion_preview", new() { ["preview"] = preview, ["action"] = "state" });
                Assert.Equal(42, state["assemblyCount"]!.GetValue<int>()); Assert.True(state["assembliesTruncated"]!.GetValue<bool>()); Assert.Equal(32, state["assemblies"]!.AsArray().Count);
                var bindings = await Job("motion_preview", new() { ["preview"] = preview, ["action"] = "assemblies", ["offset"] = 40, ["limit"] = 2 });
                Assert.Equal(42, bindings["total"]!.GetValue<int>()); Assert.Null(bindings["nextOffset"]); Assert.Equal("extra_38", bindings["items"]![0]!["Name"]!.GetValue<string>());
                bindings = await Job("motion_preview", new() { ["preview"] = preview, ["action"] = "assemblies", ["query"] = "EXTRA_39" });
                Assert.Equal(1, bindings["total"]!.GetValue<int>());
            }
            async Task CheckAssemblyLoads()
            {
                await File.WriteAllBytesAsync(libraryPath, MotionFixture.Library(textured: true), token);
                await File.WriteAllBytesAsync(Path.Combine(folder, "image.zbd"), zStudio.Tests.ContentFixture.Texture(2, 2, false), token);
                var resolver = main.ViewModel.Resolver!;
                await editor.SelectAssemblyAsync(3, token);
                await CheckBindingSupersession();
                var gate = (SemaphoreSlim)typeof(AssetResolver).GetField("gate", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(resolver)!;
                foreach (string action in new[] { "keep", "pause", "play", "cancel" })
                {
                    editor.Play(); await gate.WaitAsync(token);
                    Task first, second;
                    using var canceled = new CancellationTokenSource();
                    try
                    {
                        first = editor.SelectAssemblyAsync(4, token); Assert.False(first.IsCompleted);
                        if (action == "pause") editor.Pause();
                        if (action == "play") { editor.Pause(); editor.Play(); }
                        second = editor.SelectAssemblyAsync(3, action == "cancel" ? canceled.Token : token); Assert.False(second.IsCompleted);
                        var state = System.Text.Json.JsonSerializer.SerializeToNode(editor.State)!;
                        Assert.True(state["loading"]!.GetValue<bool>()); Assert.Equal(action != "pause", state["playbackRequested"]!.GetValue<bool>());
                        if (action == "cancel") canceled.Cancel();
                    }
                    finally { gate.Release(); }
                    await Assert.ThrowsAnyAsync<OperationCanceledException>(() => first);
                    if (action == "cancel") await Assert.ThrowsAnyAsync<OperationCanceledException>(() => second); else await second;
                    Assert.Equal(action != "pause", editor.IsPlaying); Assert.Equal(3, editor.AssemblyMember);
                    Assert.False(System.Text.Json.JsonSerializer.SerializeToNode(editor.State)!["loading"]!.GetValue<bool>());
                }
                editor.Pause(); await editor.SelectAssemblyAsync(3, token); editor.Seek(1);
            }
            async Task CheckBindingSupersession()
            {
                var asset = doc.Document.Assets[0]; var seed = Assert.IsType<MotionClip>(asset.Content);
                using var release = new ManualResetEventSlim();
                TaskCompletionSource entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
                var blocked = new BindingParts(seed.Parts, () =>
                {
                    Assert.False(main.Dispatcher.CheckAccess());
                    entered.TrySetResult();
                    Assert.True(release.Wait(TimeSpan.FromSeconds(5), token), "Motion binding did not yield to a newer workspace request.");
                });
                asset.Content = new MotionClip { Header = seed.Header, LoopTime = seed.LoopTime, FrameCount = seed.FrameCount, Parts = blocked };
                string preview = ((Guid)typeof(MainWindow).GetField("previewId", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(main)!).ToString();
                editor.Play(); JsonNode? operation = null;
                try
                {
                    operation = await Call("motion_preview", new() { ["preview"] = preview, ["action"] = "assembly", ["memberIndex"] = 4 });
                    await entered.Task.WaitAsync(TimeSpan.FromSeconds(5), token);
                    var state = await Call("preview_state", new() { ["preview"] = preview });
                    Assert.True(state["motion"]!["loading"]!.GetValue<bool>()); Assert.True(state["motion"]!["playbackRequested"]!.GetValue<bool>());
                    asset.Content = seed;
                    // GUI changes can supersede an MCP job; MCP mutations themselves remain serialized.
                    var replacement = editor.SelectAssemblyAsync(3, token);
                    release.Set(); await replacement;
                    Assert.Equal(3, editor.AssemblyMember); Assert.True(editor.IsPlaying);
                }
                finally { asset.Content = seed; release.Set(); }
                Assert.NotNull(operation); string id = operation["id"]!.GetValue<string>();
                while (operation["State"]!.GetValue<string>() is "queued" or "running")
                { await Task.Delay(10, token); operation = await Call("operation", new() { ["id"] = id }); }
                Assert.True(operation["State"]!.GetValue<string>() == "canceled", operation.ToJsonString());
                Assert.Equal(3, editor.AssemblyMember); Assert.True(editor.IsPlaying); editor.Pause();
            }
            async Task<JsonNode> Call(string name, Dictionary<string, object?> arguments)
            {
                var result = await client.CallToolAsync("zstudio_" + name, arguments, cancellationToken: token);
                Assert.False(result.IsError == true, string.Join(";", result.Content.OfType<TextContentBlock>().Select(c => c.Text)));
                return JsonNode.Parse(result.Content.OfType<TextContentBlock>().Single().Text)!;
            }
            async Task<JsonNode> Job(string name, Dictionary<string, object?> arguments, string expected = "completed")
            {
                var job = await Call(name, arguments); string id = job["id"]!.GetValue<string>();
                while (job["State"]!.GetValue<string>() is "queued" or "running") { await Task.Delay(10, token); job = await Call("operation", new() { ["id"] = id }); }
                Assert.True(job["State"]!.GetValue<string>() == expected, job.ToJsonString()); return job["result"]!;
            }
        }
        finally { foreach (var doc in main.ViewModel.Documents.ToArray()) main.ViewModel.CloseResolved(doc); main.Close(); Directory.Delete(folder, true); }
    }
    private sealed class BindingParts(IReadOnlyList<MotionPart> parts, Action read) : IReadOnlyList<MotionPart>
    {
        public int Count => parts.Count;
        public MotionPart this[int index] => parts[index];
        public IEnumerator<MotionPart> GetEnumerator() { read(); return parts.GetEnumerator(); }
        System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();
    }
}
