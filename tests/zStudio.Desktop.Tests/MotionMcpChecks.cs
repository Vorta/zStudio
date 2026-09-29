using System.Buffers.Binary;
using System.IO;
using System.IO.Pipes;
using System.Numerics;
using System.Reflection;
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
using Xunit;

namespace Recoil.Zbd.Desktop.Tests;

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
            for (int i = 0; i < 9; i++) writer.Write(0f);
            for (int i = 0; i < 3; i++) { writer.Write(1f); writer.Write(0f); writer.Write(0f); writer.Write(0f); }
            byte[] payload = stream.ToArray(), archive = new byte[payload.Length + 156]; payload.CopyTo(archive, 0);
            BinaryPrimitives.WriteInt32LittleEndian(archive.AsSpan(payload.Length + 4), payload.Length); "test_motion"u8.CopyTo(archive.AsSpan(payload.Length + 8));
            BinaryPrimitives.WriteInt32LittleEndian(archive.AsSpan(archive.Length - 8), 1); BinaryPrimitives.WriteInt32LittleEndian(archive.AsSpan(archive.Length - 4), 1);
            string path = Path.Combine(folder, "motion.zbd"), copy = Path.Combine(folder, "copy.zbd"); await File.WriteAllBytesAsync(path, archive, token);
            string libraryPath = Path.Combine(folder, "library.zbd");
            await File.WriteAllBytesAsync(libraryPath, MotionFixture.Library(), token);
            await using var host = new LocalMcpHost(main.Commands, "test");
            await using var pipe = new NamedPipeClientStream(".", host.Instance.Pipe, PipeDirection.InOut, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly); await pipe.ConnectAsync(token);
            await using var client = await McpClient.CreateAsync(new StreamClientTransport(pipe, pipe), cancellationToken: token);
            await Job("open_document", new() { ["path"] = path }); var doc = main.ViewModel.Documents.Single(); Guid member = doc.ResourceEdits!.Current.Members[0].Id;
            Dictionary<string, object?> Args(params (string Key, object Value)[] values)
            { var a = new Dictionary<string, object?> { ["document"] = doc.SessionId.ToString(), ["member"] = member.ToString() }; foreach (var (k, v) in values) a[k] = v; return a; }
            var tracks = await Call("motion_records", Args()); Assert.Equal(2, tracks["FrameCount"]!.GetValue<int>());
            var frames = await Call("motion_records", Args(("part", 0))); Assert.Equal(3, frames["rows"]!["items"]!.AsArray().Count);
            var editor = (MotionEditor)typeof(MainWindow).GetField("motion", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(main)!;
            Assert.NotNull(editor); await editor.SelectAssemblyAsync(3, token); editor.Seek(1);
            await CheckAssemblyLoads();
            editor.Viewport.RestoreView(new(new(0, 0, 100), new(0, 0, -100), new(0, 1, 0), 45));
            var view = editor.Viewport.CaptureView();
            await Job("resource_properties", Args(("action", "open"), ("part", 0), ("frame", 1)));
            var pinned = main.OpenPropertiesWindow!.ResourceFields!; Assert.Equal(1, pinned.MotionFrameIndex);
            var fields = await Job("resource_properties", Args(("action", "fields"), ("part", 0), ("frame", 1)));
            string translation = fields["fields"]!["fields"]!.AsArray().Single(f => f!["Label"]!.GetValue<string>() == "Translation")!["Id"]!.GetValue<string>();
            await Job("resource_properties", Args(("action", "edit"), ("revision", doc.Revision), ("part", 0), ("frame", 1), ("field", translation), ("value", "1, 2, 3")));
            Assert.Equal(new Vector3(1, 2, 3), ((MotionClip)doc.PreviewDocument.Assets[0].Content!).Parts[0].Frames[1].Translation);
            Assert.Equal(new Vector3(1, 2, 3), Presented().Nodes.Single().Transform.Translation);
            Assert.Equal(view, editor.Viewport.CaptureView());
            Assert.True(((Button)main.FindName("DocumentUndo")).IsEnabled); Assert.Same(pinned, main.OpenPropertiesWindow.ResourceFields);
            editor.Play();
            var invalid = await Job("motion_edit", Args(("revision", doc.Revision), ("action", "set"), ("part", 0), ("frame", 0), ("translation", new[] { 0, 0, 0 }), ("quaternionWxyz", new[] { 0, 0, 0, 0 })), "failed");
            Assert.Contains("quaternion", invalid.ToJsonString()); Assert.True(editor.IsPlaying);
            long revision = doc.Revision;
            editor.Play();
            await Job("motion_edit", Args(("revision", revision), ("action", "insert"), ("frame", 0)));
            Assert.Equal(3, ((MotionClip)doc.PreviewDocument.Assets[0].Content!).FrameCount);
            Assert.True(editor.IsPlaying); editor.Pause();
            Assert.Equal(3, System.Text.Json.JsonSerializer.SerializeToNode(editor.State)!["frameCount"]!.GetValue<int>());
            var stale = await Job("motion_edit", Args(("revision", revision), ("action", "delete"), ("frame", 0)), "failed"); Assert.Equal("revision_conflict", stale["code"]!.GetValue<string>());
            await Call("undo_redo", new() { ["document"] = doc.SessionId.ToString(), ["revision"] = doc.Revision, ["action"] = "undo" });
            Assert.Equal(2, ((MotionClip)doc.PreviewDocument.Assets[0].Content!).FrameCount);
            Assert.Equal(2, System.Text.Json.JsonSerializer.SerializeToNode(editor.State)!["frameCount"]!.GetValue<int>());
            await Job("save_document", new() { ["document"] = doc.SessionId.ToString(), ["revision"] = doc.Revision, ["destination"] = copy });
            Assert.False(doc.IsDirty); Assert.Equal(archive, await File.ReadAllBytesAsync(path, token));
            var catalog = await Call("event_catalog", new() { ["version"] = 39 }); Assert.Contains(catalog["items"]!.AsArray(), e => e!["Type"]!.GetValue<int>() == 42);
            await Job("open_document", new() { ["path"] = libraryPath });
            var libraryDoc = main.ViewModel.Documents.Single(d => d.Path == libraryPath);
            await Job("select_asset", new() { ["document"] = libraryDoc.SessionId.ToString(), ["kind"] = "Model", ["index"] = 3 });
            var beforeScene = StaticViewport(); beforeScene.RestoreView(new(new(0, 0, 100), new(0, 0, -100), new(0, 1, 0), 45)); var beforeCamera = beforeScene.CaptureView();
            string obj = Path.Combine(folder, "replacement.obj");
            await File.WriteAllTextAsync(obj, "v 0 0 0\nv 4 0 0\nv 0 4 0\nvt 0 0\nvt 1 0\nvt 0 1\nvn 0 0 1\nf 1/1/1 2/2/1 3/3/1\n", token);
            await Job("mech_model_replace", new() { ["document"] = libraryDoc.SessionId.ToString(), ["revision"] = libraryDoc.Revision, ["member"] = libraryDoc.ResourceEdits!.Current.Members[3].Id.ToString(), ["localModel"] = 0, ["material"] = 0, ["path"] = obj });
            Assert.NotSame(beforeScene, StaticViewport()); Assert.Same(libraryDoc.PreviewDocument.Scene, StaticViewport().PreviewScene);
            Assert.Equal(4, StaticViewport().PreviewScene!.Models[0].Vertices[1].X); Assert.Equal(beforeCamera, StaticViewport().CaptureView());
            await Call("undo_redo", new() { ["document"] = libraryDoc.SessionId.ToString(), ["revision"] = libraryDoc.Revision, ["action"] = "undo" });
            Assert.Equal(1, StaticViewport().PreviewScene!.Models[0].Vertices[1].X);
            await Call("undo_redo", new() { ["document"] = libraryDoc.SessionId.ToString(), ["revision"] = libraryDoc.Revision, ["action"] = "redo" });
            Assert.Equal(4, StaticViewport().PreviewScene!.Models[0].Vertices[1].X);
            var libraryScene = libraryDoc.PreviewDocument.Scene!; var libraryAsset = libraryDoc.PreviewDocument.Assets[3];
            var assembly = (MechAssembly)libraryAsset.Content!;
            libraryScene.Nodes.Clear();
            for (int i = 0; i < 1000; i++) libraryScene.Nodes.Add(new(i, $"reference_{i:D4}", "object3d", assembly.FirstModel, [], [], [], []));
            libraryAsset.Content = assembly with { RootNode = 0, NodeCount = 1000 };
            Dictionary<string, object?> MechArgs(params (string Key, object Value)[] values)
            { var a = new Dictionary<string, object?> { ["document"] = libraryDoc.SessionId.ToString(), ["member"] = libraryDoc.ResourceEdits.Current.Members[3].Id.ToString() }; foreach (var (k, v) in values) a[k] = v; return a; }
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
            Assert.Equal(1, sharedMaterials["materials"]!["total"]!.GetValue<int>());
            await CheckLargeMotionInspection();
            Recoil.Zbd.Rendering.SceneViewport StaticViewport() => (Recoil.Zbd.Rendering.SceneViewport)typeof(MainWindow).GetField("scene", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(main)!;
            AnimationFrame Presented() => (AnimationFrame)typeof(Recoil.Zbd.Rendering.SceneViewport).GetField("animationFrame", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(editor.Viewport)!;
            async Task CheckLargeMotionInspection()
            {
                var seed = MotionClip.Read(payload, token); string name = new('x', 4096); byte[] nameBytes = new byte[4100];
                BinaryPrimitives.WriteInt32LittleEndian(nameBytes, 4096); System.Text.Encoding.Latin1.GetBytes(name).CopyTo(nameBytes, 4);
                var part = seed.Parts[0] with { Name = name, NameBytes = nameBytes };
                var clip = new MotionClip { Header = seed.Header, LoopTime = seed.LoopTime, FrameCount = seed.FrameCount, Parts = Enumerable.Repeat(part, 4096).ToArray() };
                var large = new DocumentModel(FormatRegistry.Default.OpenBytes(Path.Combine(folder, "large.zbd"), MotionFixture.Archive(("large_motion", clip.Write(token))), token: token));
                main.ViewModel.Documents.Add(large);
                var inspected = await Call("inspect_asset", new() { ["document"] = large.SessionId.ToString(), ["kind"] = "Motion", ["index"] = 0 });
                foreach (string snapshot in new[] { "source", "edited" })
                {
                    var metadata = inspected[snapshot]!["properties"]!["motion"]!;
                    Assert.Equal(4096, metadata["part_count"]!.GetValue<int>()); Assert.True(metadata["parts_truncated"]!.GetValue<bool>());
                    Assert.Equal(32, metadata["parts"]!.AsArray().Count);
                }
                Assert.True(inspected.ToJsonString().Length < 40_000);
                var page = await Call("motion_records", new() { ["document"] = large.SessionId.ToString(), ["member"] = large.ResourceEdits!.Current.Members[0].Id.ToString(), ["offset"] = 4095, ["limit"] = 1 });
                Assert.Equal(4096, page["rows"]!["total"]!.GetValue<int>()); Assert.Null(page["rows"]!["nextOffset"]);
                Assert.Equal(name, Assert.Single(page["rows"]!["items"]!.AsArray())!["Name"]!.GetValue<string>());
            }
            async Task CheckAssemblyLoads()
            {
                await File.WriteAllBytesAsync(libraryPath, MotionFixture.Library(textured: true), token);
                await File.WriteAllBytesAsync(Path.Combine(folder, "image.zbd"), Recoil.Zbd.Tests.ContentFixture.Texture(2, 2, false), token);
                var resolver = main.ViewModel.Resolver!; await resolver.InvalidateAsync([libraryPath], token); await editor.RefreshLibraryAsync(null);
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
                editor.Pause(); editor.Seek(1);
                await gate.WaitAsync(token); Task pending;
                try
                {
                    pending = editor.SelectAssemblyAsync(4, token); Assert.False(pending.IsCompleted);
                    await Job("motion_edit", Args(("revision", doc.Revision), ("action", "set"), ("part", 0), ("frame", 1), ("translation", new[] { 8, 9, 10 }), ("quaternionWxyz", new[] { 1, 0, 0, 0 })));
                    Assert.Equal(new Vector3(8, 9, 10), Presented().Nodes.Single().Transform.Translation);
                }
                finally { gate.Release(); }
                await pending; Assert.Equal(new Vector3(8, 9, 10), Presented().Nodes.Single().Transform.Translation);
                await Call("undo_redo", new() { ["document"] = doc.SessionId.ToString(), ["revision"] = doc.Revision, ["action"] = "undo" });
                await editor.SelectAssemblyAsync(3, token); editor.Seek(1);
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
}
