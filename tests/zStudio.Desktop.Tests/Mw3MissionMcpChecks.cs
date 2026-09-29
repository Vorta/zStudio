using System.IO;
using System.IO.Pipes;
using System.Reflection;
using System.Text;
using System.Text.Json.Nodes;
using System.Windows;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using Recoil.Zbd.Core;
using Recoil.Zbd.Desktop;
using Recoil.Zbd.Mcp;
using Recoil.Zbd.Rendering;
using Recoil.Zbd.Tests;
using Xunit;

namespace Recoil.Zbd.Desktop.Tests;

internal static class Mw3MissionMcpChecks
{
    internal static async Task Run()
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        // Escaped Latin-1 stresses serialized size, not just .NET string length.
        string prefix = "actor_1" + new string('\u0001', 121);
        string[] names = Enumerable.Range(0, 200).Select(i => prefix + new string('é', i < 2 ? 1_048_576 : 1024) + i).ToArray();
        using var fixture = new Mw3MissionFixture(names);
        var mission = await MissionSceneLoader.LoadAsync(fixture.World, fixture.Resolver, token: deadline.Token);
        using var document = new DocumentModel(fixture.World);
        var asset = fixture.World.Add(AssetKind.World, 0, "Whole world", 0, 0);
        var main = new MainWindow { Left = -12000, ShowInTaskbar = false }; main.Show();
        using var viewport = new SceneViewport();
        const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
        main.ViewModel.Documents.Add(document);
        Set("shownDocument", document); Set("shownAsset", asset); Set("scene", viewport);
        typeof(SceneViewport).GetProperty(nameof(SceneViewport.Mission))!.SetValue(viewport, mission);
        typeof(SceneViewport).GetProperty(nameof(SceneViewport.PreviewScene))!.SetValue(viewport, mission.Scene);
        ((FrameworkElement)main.FindName("SceneHost")).Visibility = Visibility.Visible;
        ((FrameworkElement)main.FindName("EmptyPreview")).Visibility = Visibility.Collapsed;
        string preview = ((Guid)typeof(MainWindow).GetField("previewId", flags)!.GetValue(main)!).ToString();
        try
        {
            await using var host = new LocalMcpHost(main.Commands, "test");
            await using var pipe = new NamedPipeClientStream(".", host.Instance.Pipe, PipeDirection.InOut, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
            await pipe.ConnectAsync(deadline.Token);
            await using var client = await McpClient.CreateAsync(new StreamClientTransport(pipe, pipe), cancellationToken: deadline.Token);
            var page = await Call("scene_nodes", new() { ["preview"] = preview, ["query"] = "actor_1", ["limit"] = 200 });
            Assert.Equal(200, page["total"]!.GetValue<int>()); Assert.Equal(200, page["items"]!.AsArray().Count);
            foreach (var row in page["items"]!.AsArray())
            {
                Assert.Equal(prefix, row!["Name"]!.GetValue<string>());
                Assert.Equal(prefix, row["actor"]!["Name"]!.GetValue<string>());
                Assert.True(row["actor"]!["NameTruncated"]!.GetValue<bool>());
                Assert.Equal(row["actor"]!["NameCharacters"]!.GetValue<int>(), row["Metadata"]!["name_characters"]!.GetValue<int>());
                Assert.True(row["Metadata"]!["name_truncated"]!.GetValue<bool>());
            }
            var one = await Call("scene_nodes", new() { ["preview"] = preview, ["query"] = "actor_1", ["limit"] = 1, ["offset"] = 1 });
            Assert.Equal(names[1].Length, one["items"]![0]!["actor"]!["NameCharacters"]!.GetValue<int>());
            Assert.Equal(1, one["items"]![0]!["actor"]!["CoordinateSource"]!["RecordIndex"]!.GetValue<int>());
            var actor = mission.Actors[0];
            var properties = await Call("scene_properties", new() { ["preview"] = preview, ["node"] = actor.Root, ["open"] = true });
            Assert.Equal(prefix, properties["Name"]!.GetValue<string>());
            var pinned = await Call("properties_state", new());
            Assert.True(pinned["open"]!.GetValue<bool>());
            Assert.Equal(names[0].Length, pinned["content"]!["name_characters"]!.GetValue<int>());
            var selected = await Call("scene_selection", new() { ["preview"] = preview, ["action"] = "select", ["node"] = actor.Root });
            Assert.NotNull(selected);
            var card = (JsonObject)typeof(MainWindow).GetMethod("DescribeInspection", flags)!.Invoke(main,
                [viewport, new SceneInspection("node:" + actor.Root, actor.Root, -1, -1, null, null, null, null, actor.PlacementPosition, true, null)])!;
            Assert.True(card["Name truncated"]!.GetValue<bool>());
            Assert.Equal(names[0].Length, card["Authored name characters"]!.GetValue<int>());
            Assert.True(card.ToJsonString().Length < 10_000);
            Assert.Equal(0, document.Revision); Assert.False(document.IsDirty);
            // Large nested metadata must be bounded for a full page, selection
            // and pinned Properties, before anything is serialized to the pipe.
            foreach (var item in mission.Actors)
                mission.Scene.Nodes[item.Root].Metadata["large_fixture"] = new string('\u0001', 32768);
            page = await Call("scene_nodes", new() { ["preview"] = preview, ["query"] = "actor_1", ["limit"] = 200 });
            Assert.All(page["items"]!.AsArray(), row => Assert.True(row!["Metadata"]!["inspection_truncated"]!.GetValue<bool>()));
            properties = await Call("scene_properties", new() { ["preview"] = preview, ["node"] = actor.Root, ["open"] = true });
            Assert.True(properties["Metadata"]!["inspection_truncated"]!.GetValue<bool>());
            pinned = await Call("properties_state", new());
            Assert.True(pinned["content"]!["inspection_truncated"]!.GetValue<bool>());
            selected = await Call("scene_selection", new() { ["preview"] = preview, ["action"] = "select", ["node"] = actor.Root });
            Assert.True(selected["inspection_truncated"]!.GetValue<bool>());
            Assert.Equal(fixture.ReaderBytes, await File.ReadAllBytesAsync(fixture.ReaderPath, deadline.Token));

            async Task<JsonNode> Call(string command, JsonObject arguments)
            {
                var result = await client.CallToolAsync("zstudio_" + command, arguments.ToDictionary(p => p.Key, p => (object?)p.Value), cancellationToken: deadline.Token);
                string json = result.Content.OfType<TextContentBlock>().Single().Text;
                Assert.False(result.IsError == true, json);
                Assert.True(Encoding.UTF8.GetByteCount(json) < 3 * 1_048_576, "Mission inspection must retain headroom below the 4 MiB protocol cap, including a fully escaped metadata page.");
                return JsonNode.Parse(json)!;
            }
        }
        finally { Set("scene", null); main.Close(); }
        void Set(string field, object? value) => typeof(MainWindow).GetField(field, flags)!.SetValue(main, value);
    }
}
