using System.IO;
using System.IO.Pipes;
using System.Numerics;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using Recoil.Zbd.Core;
using Recoil.Zbd.Core.Formats;
using Recoil.Zbd.Desktop;
using Recoil.Zbd.Mcp;
using Xunit;

namespace Recoil.Zbd.Desktop.Tests;

internal static class MotionBindingMcpChecks
{
    internal static async Task Run()
    {
        string folder = Path.Combine(Path.GetTempPath(), "zstudio-motion-binding-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(folder);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(60)); var token = deadline.Token;
        var main = new MainWindow { Left = -12000, ShowInTaskbar = false }; main.Show();
        try
        {
            string libraryPath = Path.Combine(folder, "library.zbd"), motionPath = Path.Combine(folder, "motion.zbd");
            byte[] libraryBytes = MotionFixture.Library(); await File.WriteAllBytesAsync(libraryPath, libraryBytes, token);
            using MemoryStream stream = new(); using BinaryWriter writer = new(stream);
            writer.Write(4); writer.Write(2f); writer.Write(2); writer.Write(1); writer.Write(-1f); writer.Write(1f);
            writer.Write(4); writer.Write("body"u8); writer.Write(12);
            for (int i = 0; i < 9; i++) writer.Write(0f);
            for (int i = 0; i < 3; i++) { writer.Write(1f); writer.Write(0f); writer.Write(0f); writer.Write(0f); }
            byte[] motionBytes = MotionFixture.Archive(("test_motion", stream.ToArray()));
            await File.WriteAllBytesAsync(motionPath, motionBytes, token);
            string imported = Path.Combine(folder, "new.flt"), obj = Path.Combine(folder, "replacement.obj");
            var source = FormatRegistry.Default.OpenBytes(libraryPath, libraryBytes, token: token);
            await File.WriteAllBytesAsync(imported, source.Slice(source.Assets[3].Offset, source.Assets[3].Length).ToArray(), token);
            await File.WriteAllTextAsync(obj, "v 0 0 0\nv 4 0 0\nv 0 4 0\nvt 0 0\nvt 1 0\nvt 0 1\nvn 0 0 1\nf 1/1/1 2/2/1 3/3/1\n", token);
            await using var host = new LocalMcpHost(main.Commands, "test");
            await using var pipe = new NamedPipeClientStream(".", host.Instance.Pipe, PipeDirection.InOut, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly); await pipe.ConnectAsync(token);
            await using var client = await McpClient.CreateAsync(new StreamClientTransport(pipe, pipe), cancellationToken: token);
            await Job("open_document", new() { ["path"] = libraryPath });
            var library = main.ViewModel.Documents.Single(d => d.Path == libraryPath); var edits = library.ResourceEdits!;
            Guid original = edits.Current.Members[3].Id;
            foreach (string action in new[] { "duplicate", "add" })
            {
                var previousIds = edits.Current.Members.Select(m => m.Id).ToHashSet();
                // Reuse the source's name deliberately: names cannot identify the added member.
                await Job("archive_edit", Args(("action", action), ("member", original.ToString()), ("name", "mech_body.flt"), ("path", imported)));
                Guid added = edits.Current.Members.Single(m => !previousIds.Contains(m.Id)).Id;
                Assert.Null(edits.Member(added).SourceIndex);
                await Job("open_document", new() { ["path"] = motionPath });
                var motion = main.ViewModel.Documents.Single(d => d.Path == motionPath);
                await Job("select_asset", new() { ["document"] = motion.SessionId.ToString(), ["kind"] = "Motion", ["index"] = 0 });
                var editor = Assert.IsType<MotionEditor>(typeof(MainWindow).GetField("motion", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(main));
                string preview = ((Guid)typeof(MainWindow).GetField("previewId", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(main)!).ToString();
                await Job("motion_preview", new() { ["preview"] = preview, ["action"] = "assembly", ["memberIndex"] = Index(added) });
                editor.Seek(.5); editor.Viewport.RestoreView(new(new(0, 0, 100), new(0, 0, -100), new(0, 1, 0), 45));
                var camera = editor.Viewport.CaptureView();
                using (var canceled = new CancellationTokenSource())
                {
                    canceled.Cancel();
                    await Assert.ThrowsAnyAsync<OperationCanceledException>(() => editor.SelectAssemblyAsync(Index(original), canceled.Token));
                }
                await Job("archive_edit", Args(("action", "rename"), ("member", added.ToString()), ("name", "renamed.flt")));
                await Check(1); Assert.Equal(.5, State()["seconds"]!.GetValue<double>());
                editor.Play();
                await Job("archive_edit", Args(("action", "move"), ("member", added.ToString()), ("position", 3)));
                await Check(1); Assert.True(editor.IsPlaying);
                await Job("mech_model_replace", Args(("member", added.ToString()), ("localModel", 0), ("material", 0), ("path", obj)));
                await Check(4); Assert.True(editor.IsPlaying);
                await History("undo"); await Check(1);
                await History("undo"); await Check(1);
                await History("undo"); await Check(1);
                await History("redo"); await Check(1);
                await History("redo"); await Check(1);
                await History("redo"); await Check(4);
                editor.Pause();
                // Selecting another member must replace the retained identity, even after a reorder.
                await Job("motion_preview", new() { ["preview"] = preview, ["action"] = "assembly", ["memberIndex"] = Index(original) });
                await Job("archive_edit", Args(("action", "rename"), ("member", original.ToString()), ("name", "original_renamed.flt")));
                Assert.Equal(Index(original), editor.AssemblyMember);
                await History("undo");
                await Job("motion_preview", new() { ["preview"] = preview, ["action"] = "assembly", ["memberIndex"] = Index(added) });
                await Job("archive_edit", Args(("action", "delete"), ("member", added.ToString())));
                Assert.Null(editor.AssemblyMember); Assert.Empty(editor.Viewport.PreviewScene?.Nodes ?? []);
                await History("undo");
                // Undo restores the member, but a cleared binding must not silently select a different row.
                Assert.Null(editor.AssemblyMember);
                await History("undo"); await History("undo"); await History("undo"); await History("undo");
                Assert.False(library.IsDirty);

                int Index(Guid id) => edits.Current.Members.ToList().FindIndex(m => m.Id == id);
                JsonNode State() => JsonSerializer.SerializeToNode(editor.State)!;
                async Task Check(float extent)
                {
                    Assert.Equal(Index(added), editor.AssemblyMember); Assert.Equal(camera, editor.Viewport.CaptureView());
                    var assembly = Assert.IsType<MechAssembly>(edits.Current.Document.Assets[Index(added)].Content);
                    Assert.Same(edits.Current.Document.Scene, editor.Viewport.PreviewScene);
                    Assert.Equal(extent, editor.Viewport.PreviewScene!.Models[assembly.FirstModel].Vertices[1].X);
                    var state = await Call("preview_state", new() { ["preview"] = preview });
                    Assert.Equal(Index(added), state["motion"]!["assembly"]!.GetValue<int>());
                }
            }
            Assert.Equal(libraryBytes, await File.ReadAllBytesAsync(libraryPath, token));
            Assert.Equal(motionBytes, await File.ReadAllBytesAsync(motionPath, token));
            Dictionary<string, object?> Args(params (string Key, object Value)[] values)
            { var a = new Dictionary<string, object?> { ["document"] = library.SessionId.ToString(), ["revision"] = library.Revision }; foreach (var (k, v) in values) a[k] = v; return a; }
            async Task History(string action) => await Call("undo_redo", Args(("action", action)));
            async Task<JsonNode> Call(string name, Dictionary<string, object?> arguments)
            {
                var result = await client.CallToolAsync("zstudio_" + name, arguments, cancellationToken: token);
                Assert.False(result.IsError == true, string.Join(";", result.Content.OfType<TextContentBlock>().Select(c => c.Text)));
                return JsonNode.Parse(result.Content.OfType<TextContentBlock>().Single().Text)!;
            }
            async Task<JsonNode> Job(string name, Dictionary<string, object?> arguments)
            {
                var job = await Call(name, arguments); string id = job["id"]!.GetValue<string>();
                while (job["State"]!.GetValue<string>() is "queued" or "running") { await Task.Delay(10, token); job = await Call("operation", new() { ["id"] = id }); }
                Assert.True(job["State"]!.GetValue<string>() == "completed", job.ToJsonString()); return job["result"]!;
            }
        }
        finally { foreach (var doc in main.ViewModel.Documents.ToArray()) main.ViewModel.CloseResolved(doc); main.Close(); Directory.Delete(folder, true); }
    }
}
