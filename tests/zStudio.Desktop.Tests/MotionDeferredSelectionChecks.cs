using System.IO;
using System.IO.Pipes;
using System.Reflection;
using System.Text.Json.Nodes;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Threading;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using zStudio.Core;
using zStudio.Core.Formats;
using zStudio.Desktop;
using zStudio.Mcp;
using Xunit;

namespace zStudio.Desktop.Tests;

/// <summary>A motion selected while the Assets grid's selection binding is detached still starts and keeps its own preview.</summary>
internal static class MotionDeferredSelectionChecks
{
    internal static async Task Run()
    {
        string folder = Path.Combine(Path.GetTempPath(), "zstudio-motion-library-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(folder);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(45)); var token = deadline.Token;
        var main = new MainWindow { Left = -12000, ShowInTaskbar = false }; main.Show();
        try
        {
            using MemoryStream stream = new(); using BinaryWriter writer = new(stream);
            writer.Write(4); writer.Write(2f); writer.Write(2); writer.Write(1); writer.Write(-1f); writer.Write(1f);
            writer.Write(4); writer.Write("body"u8); writer.Write(12);
            for (int i = 0; i < 9; i++) writer.Write(i >= 6 ? i + 1f : 0f);
            for (int i = 0; i < 3; i++) { writer.Write(1f); writer.Write(0f); writer.Write(0f); writer.Write(0f); }
            // A raw member precedes the clip, so a lost Assets selection would fall back to a different member.
            string path = Path.Combine(folder, "motion.zbd"), libraryPath = Path.Combine(folder, "library.zbd");
            await File.WriteAllBytesAsync(path, MotionFixture.Archive(("raw", [1, 2, 3, 4]), ("test_motion", stream.ToArray())), token);
            await File.WriteAllBytesAsync(libraryPath, MotionFixture.Library(), token);
            await using var host = new LocalMcpHost(main.Commands, "test");
            await using var pipe = new NamedPipeClientStream(".", host.Instance.Pipe, PipeDirection.InOut, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly); await pipe.ConnectAsync(token);
            await using var client = await McpClient.CreateAsync(new StreamClientTransport(pipe, pipe), cancellationToken: token);
            await Job("open_document", new() { ["path"] = path }); var doc = main.ViewModel.Documents.Single();
            // Defer the grid's selection projection, as happens while its tab is unrealized/reparented. The command
            // must initiate and await its own preview even when no SelectionChanged event can start that work.
            var assetGrid = (DataGrid)main.FindName("AssetGrid");
            var selectionBinding = BindingOperations.GetBindingBase(assetGrid, Selector.SelectedItemProperty)!;
            BindingOperations.ClearBinding(assetGrid, Selector.SelectedItemProperty);
            await Job("select_asset", new() { ["document"] = doc.SessionId.ToString(), ["kind"] = "Motion", ["index"] = 1 });
            var editor = Assert.IsType<MotionEditor>(CurrentMotion()); Assert.Equal(doc.ResourceMembers!.Members[1].Id, editor.MemberId);
            Assert.Equal(2, State()["loopSeconds"]!.GetValue<double>());
            // Selecting the shown motion again keeps its preview.
            await Job("select_asset", new() { ["document"] = doc.SessionId.ToString(), ["kind"] = "Motion", ["index"] = 1 });
            Assert.Same(editor, CurrentMotion());
            Guid published = (Guid)typeof(MainWindow).GetField("previewId", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(main)!;
            var publishedWork = (Task)typeof(MainWindow).GetField("previewWork", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(main)!;
            BindingOperations.SetBinding(assetGrid, Selector.SelectedItemProperty, selectionBinding);
            await main.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.Background);
            Assert.Same(doc.SelectedAsset, assetGrid.SelectedItem);
            Assert.Same(editor, CurrentMotion());
            Assert.Equal(published, (Guid)typeof(MainWindow).GetField("previewId", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(main)!);
            Assert.Same(publishedWork, (Task)typeof(MainWindow).GetField("previewWork", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(main)!);
            await editor.SelectAssemblyAsync(3, token); Assert.Equal(3, editor.AssemblyMember);

            MotionEditor? CurrentMotion() => (MotionEditor?)typeof(MainWindow).GetField("motion", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(main);
            JsonNode State() => System.Text.Json.JsonSerializer.SerializeToNode(editor.State)!;
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
}
