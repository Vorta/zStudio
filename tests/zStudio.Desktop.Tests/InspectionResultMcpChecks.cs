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
                    var page = name == "pickups" ? await Job(name, Args(offset.Value, limit)) : await Call(name, Args(offset.Value, limit));
                    Assert.Equal(expected, page[total]!.GetValue<int>());
                    seen.AddRange(page[items]!.AsArray().Select(r => identity(r!)));
                    offset = page[next]?.GetValue<int>();
                } while (offset != null);
                Assert.Equal(Enumerable.Range(0, expected), seen);
            }
            async Task<JsonNode> Call(string name, JsonObject args)
            {
                if (name is "preview_state" or "scene_nodes") args["preview"] = preview;
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
