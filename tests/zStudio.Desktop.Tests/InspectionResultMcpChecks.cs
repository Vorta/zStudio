using System.IO;
using System.IO.Pipes;
using System.Reflection;
using System.Text;
using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Controls;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using Recoil.Zbd.Core;
using Recoil.Zbd.Core.Animation;
using Recoil.Zbd.Core.Formats;
using Recoil.Zbd.Desktop;
using Recoil.Zbd.Mcp;
using Recoil.Zbd.Rendering;
using Recoil.Zbd.Tests;
using Xunit;

namespace Recoil.Zbd.Desktop.Tests;

/// <summary>Actual pipe responses over small valid resources with long, escaped, complete filesystem identities.</summary>
internal static class InspectionResultMcpChecks
{
    internal static async Task Run()
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(90));
        var token = deadline.Token;
        string temporary = Path.Combine(Path.GetTempPath(), "zstudio-inspection-pages-" + Guid.NewGuid().ToString("N"));
        string root = temporary;
        for (int i = 0; i < 32; i++) root = Path.Combine(root, new string('&', 180));
        Directory.CreateDirectory(root);
        var main = new MainWindow { Left = -12000, ShowInTaskbar = false };
        using var viewport = new SceneViewport();
        try
        {
            const int count = 128;
            string archivePath = Path.Combine(root, "resources.zbd"), worldPath = Path.Combine(root, "gamez.zbd");
            byte[] worldBytes = ModelFixture.GameZ();
            await File.WriteAllBytesAsync(worldPath, worldBytes, token);
            var rows = Enumerable.Range(0, count).Select(i => A(S("ERFPG_AMMO"), I(1), A(I(i), I(2), I(3)), A(I(0), I(0), I(0)), I(1))).ToArray();
            byte[] resource = ZrdWriter.Write(A(A(rows)));
            using (MemoryStream data = new())
            {
                using var writer = new BinaryWriter(data, Encoding.Latin1, true);
                writer.Write(resource); writer.Write(0); writer.Write(resource.Length);
                byte[] entry = new byte[140]; Encoding.Latin1.GetBytes("puppies.zrd").CopyTo(entry, 0); writer.Write(entry);
                writer.Write(1); writer.Write(1);
                await File.WriteAllBytesAsync(archivePath, data.ToArray(), token);
            }
            for (int i = 0; i < count; i++) await File.WriteAllBytesAsync(Path.Combine(root, $"rtexture{i + 1}.zbd"), ModelFixture.Texture(), token);
            await main.ViewModel.OpenRootAsync(root, token);
            var resolver = main.ViewModel.Resolver!;
            var world = new ZbdDocument(worldPath, FileStamp.Read(worldPath), new(FormatFamily.GameZ, 15, Recognition.Supported, "bounded response fixture"), worldBytes) { Scene = new() };
            var asset = world.Add(AssetKind.World, 0, "Whole world", 0, worldBytes.Length);
            using var doc = new DocumentModel(world); doc.AttachResolver(resolver); main.ViewModel.Documents.Add(doc);
            typeof(MainViewModel).GetField("selectedDocument", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(main.ViewModel, doc);
            var edits = await doc.GetPickupEditsAsync(resolver, token);
            Assert.Equal(count, edits.Records.Count);
            string sourceArchive = edits.Records[0].Source.ArchivePath;
            Assert.Equal(archivePath.ToUpperInvariant(), sourceArchive);
            List<MissionActor> actors = [];
            foreach (var record in edits.Records)
            {
                int i = actors.Count;
                world.Scene.Nodes.Add(new(i, $"pickup{i}", "object3d", null, [], [], new(), new()));
                actors.Add(new(i, i, $"pickup{i}", "puppies.zrd", new(0, record.Type, 1, 1, record.OriginalPosition, record.Rotation, 1, record.Source)));
            }
            var mission = new MissionSceneContext(world.Scene, Enumerable.Range(0, count).ToList(), actors, [], [], MissionLayoutSelection.For(MissionDifficulty.Medium), count);
            typeof(SceneViewport).GetProperty("Mission")!.SetValue(viewport, mission);
            typeof(SceneViewport).GetProperty(nameof(SceneViewport.PreviewScene))!.SetValue(viewport, world.Scene);
            Set("scene", viewport); Set("shownDocument", doc); Set("shownAsset", asset);
            ((FrameworkElement)main.FindName("SceneHost")).Visibility = Visibility.Visible;
            ((FrameworkElement)main.FindName("EmptyPreview")).Visibility = Visibility.Collapsed;
            string preview = ((Guid)Get("previewId")!).ToString();
            // Use the same real pack inventory and exact retained choice type as the visible picker.
            var packs = resolver.TexturePacks(worldPath, token);
            Assert.Equal(count, packs.Length);
            var choice = typeof(MainWindow).GetNestedType("PackChoice", BindingFlags.NonPublic)!;
            var combo = (ComboBox)main.FindName("TexturePackCombo");
            combo.ItemsSource = packs.Select(p => Activator.CreateInstance(choice, Path.GetFileName(p), p)).Prepend(Activator.CreateInstance(choice, "Automatic", null)).ToArray();
            await using var host = new LocalMcpHost(main.Commands, "test");
            await using var pipe = new NamedPipeClientStream(".", host.Instance.Pipe, PipeDirection.InOut, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
            await pipe.ConnectAsync(token);
            await using var client = await McpClient.CreateAsync(new StreamClientTransport(pipe, pipe), cancellationToken: token);
            // Reuse the actual long-path inventory: even these small files must page before full identities
            // expand during JSON escaping. Every recognized file remains reachable exactly once.
            var expectedFiles = main.ViewModel.Files.ToArray();
            foreach (int limit in new[] { 1, 200 })
            {
                List<string> seen = []; int? offset = 0;
                do
                {
                    var page = await Call("files", Args(offset.Value, limit));
                    Assert.Equal(expectedFiles.Length, page["total"]!.GetValue<int>());
                    var items = page["items"]!.AsArray();
                    if (limit == 200 && offset == 0) Assert.InRange(items.Count, 1, expectedFiles.Length - 1);
                    foreach (var item in items)
                    {
                        var expected = expectedFiles[seen.Count];
                        Assert.Equal(expected.Path, item!["Path"]!.GetValue<string>());
                        Assert.Equal(expected.RelativePath, item["RelativePath"]!.GetValue<string>());
                        Assert.Equal(expected.Name, item["Name"]!.GetValue<string>());
                        Assert.Equal(expected.Probe.Description, item["Probe"]!["Description"]!.GetValue<string>());
                        Assert.Equal(expected.Detail, item["Detail"]!.GetValue<string>());
                        seen.Add(expected.Path);
                    }
                    offset = page["nextOffset"]?.GetValue<int>();
                    if (seen.Count < expectedFiles.Length) Assert.Equal(seen.Count, offset);
                } while (offset != null);
                Assert.Equal(expectedFiles.Select(f => f.Path), seen);
            }
            foreach (int? limit in new int?[] { 1, null, 200 })
            {
                await AllPages("pickups", "items", "nextOffset", "total", count, limit,
                    row => { Assert.Equal(sourceArchive, row["source"]!["ArchivePath"]!.GetValue<string>()); Assert.Equal(archivePath, row["target"]!.GetValue<string>()); return row["source"]!["RecordIndex"]!.GetValue<int>(); });
                await AllPages("scene_nodes", "items", "nextOffset", "total", count, limit,
                    row => { Assert.Equal(sourceArchive, row["actor"]!["Pickup"]!["Source"]!["ArchivePath"]!.GetValue<string>()); return row["Index"]!.GetValue<int>(); });
                List<string?> seen = []; int? offset = 0;
                do
                {
                    var state = await Call("preview_state", Args(offset.Value, limit));
                    Assert.Equal(count + 1, state["texturePackCount"]!.GetValue<int>());
                    Assert.Equal(offset, state["texturePackOffset"]!.GetValue<int>());
                    seen.AddRange(state["texturePacks"]!.AsArray().Select(p => p!["Path"]?.GetValue<string>()));
                    offset = state["texturePackNextOffset"]?.GetValue<int>();
                } while (offset != null);
                Assert.Equal(new string?[] { null }.Concat(packs), seen);
            }
            var filtered = await Job("pickups", new() { ["document"] = doc.SessionId.ToString(), ["query"] = "AMMO PUPPIES.ZRD", ["limit"] = 1 });
            Assert.Equal(count, filtered["total"]!.GetValue<int>());
            Assert.Single(filtered["items"]!.AsArray());
            foreach (string name in new[] { "pickups", "scene_nodes", "preview_state" })
            {
                var empty = name == "pickups" ? await Job(name, Args(count + 1, 200)) : await Call(name, Args(count + 1, 200));
                Assert.Empty(empty[name == "preview_state" ? "texturePacks" : "items"]!.AsArray());
            }
            // Reuse the real escaped path to exercise source-bearing diagnostic pages as well. File is
            // present both directly and in computed Details; result paging must account for both.
            main.ViewModel.Problems.Clear();
            for (int i = 0; i < count; i++) main.ViewModel.AddProblem("Archive member range is invalid.", file: archivePath, assetIndex: i);
            var problem = Assert.Single((await Call("problems", new() { ["limit"] = 1 }))["items"]!.AsArray())!;
            Assert.Equal(archivePath, problem["File"]!.GetValue<string>());
            await AllPages("problems", "items", "nextOffset", "total", count, 200,
                row => { Assert.Equal(archivePath, row["File"]!.GetValue<string>()); Assert.StartsWith(archivePath, row["Details"]!.GetValue<string>()); return row["AssetIndex"]!.GetValue<int>(); });
            // These retained source identities are synthetic: no extra deep filesystem is needed.
            // The same full archive path belongs to distinct member/node occurrences, never to a label.
            string aiArchive = Path.Combine(temporary, string.Join(Path.DirectorySeparatorChar,
                Enumerable.Repeat(new string('界', 100), 60)), "networks.zbd");
            var aiNetworks = Enumerable.Range(0, count).Select(i => new AiNetwork($"network{i}", aiArchive, i, "net_01.zrd", "network", "type", 1,
                [new AiNode($"node{i}", i, 0, default, i, [])], [])).ToArray();
            var aiGraph = new AiNetworkSnapshot("escaped-paths", aiNetworks)
            {
                ValveSources = Enumerable.Range(0, count).Select(i => new AiValveSource(aiArchive, i, "net_01.zrd", A(), ReadOnlyMemory<byte>.Empty)).ToArray()
            };
            typeof(SceneViewport).GetProperty(nameof(SceneViewport.AiNetworks))!.SetValue(viewport, aiGraph);
            try
            {
                foreach (string name in new[] { "ai_networks", "ai_nodes", "ai_valve_selection" })
                {
                    var single = await AiPage(name, 0, 1);
                    Assert.Equal(count, single["total"]!.GetValue<int>());
                    Assert.Equal(1, single["nextOffset"]!.GetValue<int>());
                    Assert.Equal(aiArchive, Assert.Single(single["items"]!.AsArray())![name == "ai_nodes" ? "source_archive" : "Archive"]!.GetValue<string>());
                    List<int> seen = []; int? offset = 0;
                    do
                    {
                        var page = await AiPage(name, offset.Value, 200);
                        Assert.Equal(count, page["total"]!.GetValue<int>());
                        var items = page["items"]!.AsArray();
                        Assert.NotEmpty(items);
                        if (offset == 0) Assert.InRange(items.Count, 1, count - 1);
                        foreach (var item in items)
                        {
                            Assert.Equal(aiArchive, item![name == "ai_nodes" ? "source_archive" : "Archive"]!.GetValue<string>());
                            Assert.Equal("net_01.zrd", item[name == "ai_nodes" ? "source_member" : "Member"]!.GetValue<string>());
                            seen.Add(item[name == "ai_nodes" ? "node_index" : "MemberIndex"]!.GetValue<int>());
                        }
                        offset = page["nextOffset"]?.GetValue<int>();
                        if (seen.Count < count) Assert.Equal(seen.Count, offset);
                    } while (offset != null);
                    Assert.Equal(Enumerable.Range(0, count), seen);
                }
                async Task<JsonNode> AiPage(string name, int offset, int limit)
                {
                    var args = Args(offset, limit); args["preview"] = preview; args["snapshot"] = aiGraph.Id;
                    if (name == "ai_networks") args.Remove("snapshot");
                    if (name == "ai_valve_selection") { args["action"] = "sources"; return await Job(name, args); }
                    return await Call(name, args);
                }
            }
            finally { typeof(SceneViewport).GetProperty(nameof(SceneViewport.AiNetworks))!.SetValue(viewport, AiNetworkSnapshot.Empty); }
            // The animation runtime scene must use the same bounded projection as static scene_nodes,
            // even for one world node with nested cell metadata. A small fixture crosses the projection
            // limit without constructing the multi-megabyte source that originally defeated limit=1.
            JsonArray cells = new(Enumerable.Range(0, 96).Select(i => (JsonNode?)new JsonObject { ["index"] = i, ["label"] = "<&>\"", ["nodes"] = new JsonArray() }).ToArray());
            world.Scene.Nodes[0] = world.Scene.Nodes[0] with { Class = "world" };
            world.Scene.Nodes[0].Metadata["cells"] = cells;
            string originalMetadata = world.Scene.Nodes[0].Metadata.ToJsonString();
            var package = new AnimationPackage { Prefix = new byte[72], Tail = [] };
            package.Entries.Add(new(new byte[308], 0, 0));
            using var animationDocument = new DocumentModel(new ZbdDocument("animation", new(0, DateTime.MinValue), new(FormatFamily.Animation, 28, Recognition.Supported, "fixture"), ReadOnlyMemory<byte>.Empty) { Animations = package });
            using (var editor = new AnimationEditor(animationDocument, 0, resolver, token))
            {
                var flags = BindingFlags.Instance | BindingFlags.NonPublic;
                typeof(AnimationEditor).GetField("context", flags)!.SetValue(editor, new AnimationPreviewContext { Package = animationDocument.AnimationEdits!.Package, World = world });
                typeof(AnimationEditor).GetField("frame", flags)!.SetValue(editor, new AnimationFrame(0, [], [], [], [], null, null, default, default, [], [], []));
                editor.SetOperationDiagnostics(main.ViewModel.Problems);
                Set("animation", editor);
                try
                {
                    var single = await Call("animation_runtime", new() { ["section"] = "scene", ["limit"] = 1 });
                    var first = Assert.Single(single["items"]!.AsArray())!;
                    Assert.Equal(0, first["Index"]!.GetValue<int>());
                    Assert.Equal("pickup0", first["Name"]!.GetValue<string>());
                    Assert.True(first["Metadata"]!["inspection_truncated"]!.GetValue<bool>());
                    Assert.True(first["Metadata"]!.ToJsonString().Length < 16_384);
                    await AllPages("animation_runtime", "items", "nextOffset", "total", count, 200,
                        row => { int index = row["Index"]!.GetValue<int>(); Assert.Equal($"pickup{index}", row["Name"]!.GetValue<string>()); return index; });
                    var runtimeProblems = await Call("animation_runtime", new() { ["section"] = "problems", ["limit"] = 200 });
                    Assert.NotNull(runtimeProblems["nextOffset"]);
                    Assert.All(runtimeProblems["items"]!.AsArray(), row => Assert.Equal(archivePath, row!["FileProblem"]!["File"]!.GetValue<string>()));
                    Assert.Equal(originalMetadata, world.Scene.Nodes[0].Metadata.ToJsonString());
                    Assert.Equal(96, cells.Count); // Complete metadata is still present for explicit export.
                }
                finally { Set("animation", null); }
            }
            Assert.False(doc.IsDirty); Assert.False(edits.CanUndo);
            Assert.Equal(worldBytes, await File.ReadAllBytesAsync(worldPath, token));

            JsonObject Args(int offset, int? limit)
            {
                JsonObject result = new() { ["offset"] = offset };
                if (limit != null) result["limit"] = limit.Value;
                return result;
            }
            async Task AllPages(string name, string items, string next, string total, int expected, int? limit, Func<JsonNode, int> identity)
            {
                List<int> seen = []; int? offset = 0;
                do
                {
                    var arguments = Args(offset.Value, limit);
                    if (name == "animation_runtime") arguments["section"] = "scene";
                    var page = name == "pickups" ? await Job(name, arguments) : await Call(name, arguments);
                    Assert.Equal(expected, page[total]!.GetValue<int>());
                    if ((name is "animation_runtime" or "problems") && offset == 0) Assert.InRange(page[items]!.AsArray().Count, 1, expected - 1);
                    seen.AddRange(page[items]!.AsArray().Select(r => identity(r!)));
                    offset = page[next]?.GetValue<int>();
                    if (name == "animation_runtime" && seen.Count < expected) Assert.NotNull(offset);
                } while (offset != null);
                Assert.Equal(Enumerable.Range(0, expected), seen);
            }
            async Task<JsonNode> Call(string name, JsonObject args)
            {
                if (name is "preview_state" or "scene_nodes" or "animation_runtime") args["preview"] = preview;
                if (name == "pickups") args["document"] = doc.SessionId.ToString();
                var response = await client.CallToolAsync("zstudio_" + name, args.ToDictionary(p => p.Key, p => (object?)p.Value), cancellationToken: token);
                string text = response.Content.OfType<TextContentBlock>().Single().Text;
                Assert.False(response.IsError == true, text);
                Assert.True(text.Length < 1200 * 1024, $"{name}: {text.Length} response characters");
                return JsonNode.Parse(text)!;
            }
            async Task<JsonNode> Job(string name, JsonObject args)
            {
                var operation = await Call(name, args); string id = operation["id"]!.GetValue<string>();
                while (operation["State"]!.GetValue<string>() is "queued" or "running")
                { await Task.Delay(10, token); operation = await Call("operation", new() { ["id"] = id }); }
                Assert.True(operation["State"]!.GetValue<string>() == "completed", operation.ToJsonString());
                return operation["result"]!;
            }
        }
        finally
        {
            Set("scene", null);
            foreach (var doc in main.ViewModel.Documents.ToArray()) main.ViewModel.CloseResolved(doc);
            main.Close(); Directory.Delete(temporary, true);
        }
        object? Get(string name) => typeof(MainWindow).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(main);
        void Set(string name, object? value) => typeof(MainWindow).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(main, value);
    }
    private static ZrdNode A(params ZrdNode[] values) => ZrdNode.Create(ZrdKind.Array) with { Children = values };
    private static ZrdNode S(string value) => ZrdNode.Create(ZrdKind.String) with { Text = value };
    private static ZrdNode I(int value) => ZrdNode.Create(ZrdKind.Int, value.ToString(System.Globalization.CultureInfo.InvariantCulture));
}
