using System.IO;
using System.IO.Pipes;
using System.Reflection;
using System.Text.Json.Nodes;
using System.Windows.Controls.Primitives;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using Recoil.Zbd.Core;
using Recoil.Zbd.Core.Formats;
using Recoil.Zbd.Desktop;
using Recoil.Zbd.Mcp;
using Xunit;

namespace Recoil.Zbd.Desktop.Tests;

/// <summary>A 1998 demo world (GameZ version 13) opens read-only: Unlock editing stays off in the window and through MCP.</summary>
internal static class DemoWorldMcpChecks
{
    internal static async Task Run()
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        string root = Path.Combine(Path.GetTempPath(), "zstudio-demo-lock-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root);
        using var resolver = new AssetResolver(root);
        var main = new MainWindow { Left = -12000, ShowInTaskbar = false }; main.Show();
        var world = new ZbdDocument(Path.Combine(root, "gamez.zbd"), new(0, DateTime.MinValue), new(FormatFamily.GameZ, 13, Recognition.Supported, "fixture"), ReadOnlyMemory<byte>.Empty) { Scene = new() };
        world.Scene.Nodes.Add(new(0, "world", "world", null, [], [], new(), new()));
        world.Add(AssetKind.World, 0, "Whole world", 0, 0);
        using var doc = new DocumentModel(world); doc.AttachResolver(resolver); main.ViewModel.Documents.Add(doc);
        typeof(MainViewModel).GetField("selectedDocument", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(main.ViewModel, doc);
        typeof(MainWindow).GetField("shownDocument", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(main, doc);
        typeof(MainWindow).GetMethod("AttachPickupEditor", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(main, [doc]);
        try
        {
            // The window offers no Unlock editing, and checking it anyway leaves the world locked.
            var unlock = (ToggleButton)main.FindName("EditingUnlocked");
            Assert.False(unlock.IsEnabled);
            unlock.IsChecked = true;
            Assert.True(doc.PickupsLocked); Assert.False(unlock.IsChecked);

            await using var host = new LocalMcpHost(main.Commands, "test");
            await using var pipe = new NamedPipeClientStream(".", host.Instance.Pipe, PipeDirection.InOut, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
            await pipe.ConnectAsync(deadline.Token);
            await using var client = await McpClient.CreateAsync(new StreamClientTransport(pipe, pipe), cancellationToken: deadline.Token);
            var refused = await Lock(false, "read_only");
            Assert.Contains("1998 demo", refused["message"]!.GetValue<string>());
            Assert.True(doc.PickupsLocked);
            Assert.True((await Lock(true))["PickupsLocked"]!.GetValue<bool>());

            Task<JsonNode> Lock(bool locked, string? error = null) => Call("pickup_lock", new() { ["document"] = doc.SessionId.ToString(), ["revision"] = doc.Revision, ["locked"] = locked }, error);
            async Task<JsonNode> Call(string name, JsonObject args, string? error = null)
            {
                var response = await client.CallToolAsync("zstudio_" + name, args.ToDictionary(p => p.Key, p => (object?)p.Value), cancellationToken: deadline.Token);
                var json = JsonNode.Parse(response.Content.OfType<TextContentBlock>().Single().Text)!;
                if (error == null) Assert.False(response.IsError == true, json.ToJsonString()); else Assert.Equal(error, json["code"]!.GetValue<string>());
                return json;
            }
        }
        finally
        {
            typeof(MainWindow).GetMethod("DetachPickupEditor", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(main, []);
            main.Close(); Directory.Delete(root, true);
        }
    }
}
