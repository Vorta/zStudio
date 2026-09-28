using System.Buffers.Binary;
using System.IO;
using System.IO.Pipes;
using System.Numerics;
using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Controls;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using Recoil.Zbd.Core;
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
            await using var host = new LocalMcpHost(main.Commands, "test");
            await using var pipe = new NamedPipeClientStream(".", host.Instance.Pipe, PipeDirection.InOut, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly); await pipe.ConnectAsync(token);
            await using var client = await McpClient.CreateAsync(new StreamClientTransport(pipe, pipe), cancellationToken: token);
            await Job("open_document", new() { ["path"] = path }); var doc = main.ViewModel.Documents.Single(); Guid member = doc.ResourceEdits!.Current.Members[0].Id;
            Dictionary<string, object?> Args(params (string Key, object Value)[] values)
            { var a = new Dictionary<string, object?> { ["document"] = doc.SessionId.ToString(), ["member"] = member.ToString() }; foreach (var (k, v) in values) a[k] = v; return a; }
            var tracks = await Call("motion_records", Args()); Assert.Equal(2, tracks["FrameCount"]!.GetValue<int>());
            var frames = await Call("motion_records", Args(("part", 0))); Assert.Equal(3, frames["rows"]!["items"]!.AsArray().Count);
            await Job("resource_properties", Args(("action", "open"), ("part", 0), ("frame", 1)));
            var pinned = main.OpenPropertiesWindow!.ResourceFields!; Assert.Equal(1, pinned.MotionFrameIndex);
            var fields = await Job("resource_properties", Args(("action", "fields"), ("part", 0), ("frame", 1)));
            string translation = fields["fields"]!["fields"]!.AsArray().Single(f => f!["Label"]!.GetValue<string>() == "Translation")!["Id"]!.GetValue<string>();
            await Job("resource_properties", Args(("action", "edit"), ("revision", doc.Revision), ("part", 0), ("frame", 1), ("field", translation), ("value", "1, 2, 3")));
            Assert.Equal(new Vector3(1, 2, 3), ((MotionClip)doc.PreviewDocument.Assets[0].Content!).Parts[0].Frames[1].Translation);
            Assert.True(((Button)main.FindName("DocumentUndo")).IsEnabled); Assert.Same(pinned, main.OpenPropertiesWindow.ResourceFields);
            var invalid = await Job("motion_edit", Args(("revision", doc.Revision), ("action", "set"), ("part", 0), ("frame", 0), ("translation", new[] { 0, 0, 0 }), ("quaternionWxyz", new[] { 0, 0, 0, 0 })), "failed");
            Assert.Contains("quaternion", invalid.ToJsonString());
            long revision = doc.Revision;
            await Job("motion_edit", Args(("revision", revision), ("action", "insert"), ("frame", 0)));
            Assert.Equal(3, ((MotionClip)doc.PreviewDocument.Assets[0].Content!).FrameCount);
            var stale = await Job("motion_edit", Args(("revision", revision), ("action", "delete"), ("frame", 0)), "failed"); Assert.Equal("revision_conflict", stale["code"]!.GetValue<string>());
            await Call("undo_redo", new() { ["document"] = doc.SessionId.ToString(), ["revision"] = doc.Revision, ["action"] = "undo" });
            Assert.Equal(2, ((MotionClip)doc.PreviewDocument.Assets[0].Content!).FrameCount);
            await Job("save_document", new() { ["document"] = doc.SessionId.ToString(), ["revision"] = doc.Revision, ["destination"] = copy });
            Assert.False(doc.IsDirty); Assert.Equal(archive, await File.ReadAllBytesAsync(path, token));
            var catalog = await Call("event_catalog", new() { ["version"] = 39 }); Assert.Contains(catalog["items"]!.AsArray(), e => e!["Type"]!.GetValue<int>() == 42);
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
