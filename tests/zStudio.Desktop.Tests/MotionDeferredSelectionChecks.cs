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
using zStudio.Mcp;
using zStudio.Tests;
using Xunit;

namespace zStudio.Desktop.Tests;

/// <summary>Navigation starts its selected preview even while the Assets grid's selection binding is detached.</summary>
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

            // Resource/script commands and diagnostic links share select_asset's navigation contract. Two distinct
            // records expose a stale tree/metadata preview even if the grid has not projected the new selection yet.
            string resourcesPath = Path.Combine(folder, "resources.zbd"), scriptsPath = Path.Combine(folder, "scripts.zbd");
            byte[] Resource(int value) => ZrdWriter.Write(new(Guid.NewGuid(), ZrdKind.Array, 0, "", [ZrdNode.Create(ZrdKind.Int, value.ToString())]));
            await File.WriteAllBytesAsync(resourcesPath, MotionFixture.Archive(("first.zrd", Resource(7)), ("second.zrd", Resource(9))), token);
            var package = FormatRegistry.Default.OpenBytes(scriptsPath, ContentFixture.Scripts(), token: token).Scripts!;
            var entries = package.Entries.Select((entry, i) => entry with
            {
                Name = "script_" + i,
                Instructions = [new(Guid.NewGuid(), ["COMMAND_" + i, "argument_" + i], ReadOnlyMemory<byte>.Empty, null)]
            }).ToArray();
            await File.WriteAllBytesAsync(scriptsPath, PreparedScriptWriter.Write(package with { Entries = entries }, token), token);
            await Job("open_document", new() { ["path"] = resourcesPath });
            var resources = main.ViewModel.SelectedDocument!;
            BindingOperations.ClearBinding(assetGrid, Selector.SelectedItemProperty);
            var tree = (TreeView)main.FindName("CentralTree");
            foreach (int index in new[] { 1, 0 })
            {
                var member = resources.ResourceMembers!.Members[index]; var root = resources.ResourceMembers.Tree(member, token); var node = root.Children.Single();
                await Job("resource_select", new() { ["document"] = resources.SessionId.ToString(), ["member"] = member.Id.ToString(), ["node"] = node.Id.ToString() });
                AssertPreview(resources, index);
                var shownTree = Assert.Single(tree.Items.OfType<ResourceTreeItem>());
                Assert.Same(root, shownTree.Node); Assert.True(shownTree.Children.Single().IsSelected);
            }
            await main.NavigateProblemAsync(new("Warning", "File / operation", "Second resource diagnostic", resources.Path, 1));
            AssertPreview(resources, 1);
            Assert.Same(resources.ResourceMembers!.Members[1].Tree, Assert.Single(tree.Items.OfType<ResourceTreeItem>()).Node);

            await Job("open_document", new() { ["path"] = scriptsPath });
            var scripts = main.ViewModel.SelectedDocument!;
            var instructions = (DataGrid)Field("scriptGrid")!;
            foreach (int index in new[] { 1, 0 })
            {
                var entry = scripts.Document.Scripts!.Entries[index]; var instruction = entry.Instructions.Single();
                await Job("script_select", new() { ["document"] = scripts.SessionId.ToString(), ["script"] = entry.Id.ToString(), ["instruction"] = instruction.Id.ToString() });
                AssertPreview(scripts, index);
                Assert.Equal(entry.Name, tree.Items.OfType<InspectorNode>().Single(n => n.Name == "name").Value);
                Assert.Contains("COMMAND_" + index, ((TextBox)main.FindName("ContentText")).Text);
                Assert.Equal(instruction.Id, instructions.SelectedItem!.GetType().GetProperty("Id")!.GetValue(instructions.SelectedItem));
            }
            BindingOperations.SetBinding(assetGrid, Selector.SelectedItemProperty, selectionBinding);
            await main.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.Background);
            Assert.Same(scripts.SelectedAsset, assetGrid.SelectedItem); AssertPreview(scripts, 0);

            object? Field(string name) => typeof(MainWindow).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(main);
            void AssertPreview(DocumentModel selected, int index)
            {
                Assert.Same(selected, main.ViewModel.SelectedDocument); Assert.Same(selected.Assets[index], selected.SelectedAsset);
                Assert.Same(selected, Field("shownDocument")); Assert.Same(selected.SelectedAsset!.Record, Field("shownAsset"));
                Assert.Equal(selected.SelectedAsset.Name, ((TextBlock)main.FindName("PreviewTitle")).Text);
                Assert.Equal(selected.SelectedAsset.Name, ((JsonObject)Field("properties")!)["name"]!.GetValue<string>());
            }
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
