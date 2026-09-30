using System.IO;
using System.IO.Pipes;
using System.Reflection;
using System.Text.Json.Nodes;
using System.Windows.Controls;
using System.Windows.Threading;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using Recoil.Zbd.Core;
using Recoil.Zbd.Core.Formats;
using Recoil.Zbd.Desktop;
using Recoil.Zbd.Mcp;
using Xunit;

namespace Recoil.Zbd.Desktop.Tests;

/// <summary>Dependent motion previews after edits elsewhere: accepted edits stay successful and bindings never move by index.</summary>
internal static class MotionLibraryRefreshChecks
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
            string path = Path.Combine(folder, "motion.zbd"), libraryPath = Path.Combine(folder, "library.zbd"), otherPath = Path.Combine(folder, "other.zbd"), ambiguous = Path.Combine(folder, "library_copy.zbd");
            await File.WriteAllBytesAsync(path, MotionFixture.Archive(("raw", [1, 2, 3, 4]), ("test_motion", stream.ToArray())), token);
            await File.WriteAllBytesAsync(libraryPath, MotionFixture.Library(), token);
            await File.WriteAllBytesAsync(otherPath, MotionFixture.Archive(("data", [5, 6, 7, 8])), token);
            await using var host = new LocalMcpHost(main.Commands, "test");
            await using var pipe = new NamedPipeClientStream(".", host.Instance.Pipe, PipeDirection.InOut, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly); await pipe.ConnectAsync(token);
            await using var client = await McpClient.CreateAsync(new StreamClientTransport(pipe, pipe), cancellationToken: token);
            await Job("open_document", new() { ["path"] = path }); var doc = main.ViewModel.Documents.Single();
            await Job("select_asset", new() { ["document"] = doc.SessionId.ToString(), ["kind"] = "Motion", ["index"] = 1 });
            var editor = Assert.IsType<MotionEditor>(CurrentMotion()); Guid clip = doc.ResourceEdits!.Current.Members[1].Id; Assert.Equal(clip, editor.MemberId);
            await editor.SelectAssemblyAsync(3, token); Assert.Equal(3, editor.AssemblyMember);

            // A cleared or filtered Assets selection is not a request to switch the displayed motion, whether or
            // not the Assets grid (TwoWay SelectedItem binding) is realized.
            var tabs = (TabControl)main.FindName("NavigationTabs");
            foreach (int tab in new[] { 1, 0 })
            {
                tabs.SelectedIndex = tab; await main.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.Background);
                doc.SelectedAsset = null; double seconds = 3 + tab;
                await Job("motion_edit", new() { ["document"] = doc.SessionId.ToString(), ["revision"] = doc.Revision, ["member"] = clip.ToString(), ["action"] = "timing", ["loopSeconds"] = seconds });
                Assert.Same(editor, CurrentMotion()); Assert.Equal(clip, editor.MemberId); Assert.Equal(3, editor.AssemblyMember);
                Assert.Equal(seconds, State()["loopSeconds"]!.GetValue<double>(), 3);
                Assert.True(doc.SelectedAsset == null || doc.SelectedAsset.ResourceId == clip);
            }

            // An accepted edit elsewhere succeeds even when the dependent library becomes ambiguous.
            var other = await main.ViewModel.OpenFileAsync(otherPath, token, activate: false) ?? throw new InvalidDataException("Other archive did not open.");
            await File.WriteAllBytesAsync(ambiguous, MotionFixture.Library(), token);
            long before = other.Revision;
            await RenameOther("renamed");
            Assert.Equal(before + 1, other.Revision); Assert.Same(editor, CurrentMotion());
            Assert.Null(editor.AssemblyMember); Assert.Null(State()["library"]); Assert.Empty(editor.Assemblies);
            File.Delete(ambiguous);
            await RenameOther("restored");
            Assert.Equal(libraryPath, State()["library"]!.GetValue<string>()); Assert.Null(editor.AssemblyMember);
            await editor.SelectAssemblyAsync(3, token);
            await RenameOther("unchanged");
            Assert.Equal(3, editor.AssemblyMember);

            // Session edits remap by original identity; after that session is discarded, the stale index is not reused.
            var library = await main.ViewModel.OpenFileAsync(libraryPath, token, activate: false) ?? throw new InvalidDataException("Library did not open.");
            await Job("archive_edit", new() { ["document"] = library.SessionId.ToString(), ["revision"] = library.Revision, ["action"] = "move", ["member"] = library.ResourceEdits!.Current.Members[4].Id.ToString(), ["position"] = 0 });
            Assert.Equal(4, editor.AssemblyMember); Assert.Equal("mech_body.flt", editor.Assemblies.Single(a => a.Index == 4).Name);
            main.ViewModel.CloseResolved(library);
            await RenameOther("discarded");
            Assert.Null(editor.AssemblyMember); Assert.Equal(libraryPath, State()["library"]!.GetValue<string>());
            Assert.Equal("mech_other.flt", editor.Assemblies.Single(a => a.Index == 4).Name);

            // A refresh delayed inside library loading must not restore the assembly bound before a newer user choice.
            await editor.SelectAssemblyAsync(3, token);
            var gate = (SemaphoreSlim)typeof(AssetResolver).GetField("gate", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(main.ViewModel.Resolver)!;
            await gate.WaitAsync(token); Task refresh, choice;
            try
            {
                refresh = editor.RefreshLibraryAsync(); Assert.False(refresh.IsCompleted); // Blocked while opening the library.
                choice = editor.SelectAssemblyAsync(4, token);
            }
            finally { gate.Release(); }
            await refresh; await choice;
            Assert.Equal(4, editor.AssemblyMember); Assert.Equal("mech_other.flt", editor.Assemblies.Single(a => a.Index == 4).Name);

            MotionEditor? CurrentMotion() => (MotionEditor?)typeof(MainWindow).GetField("motion", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(main);
            JsonNode State() => System.Text.Json.JsonSerializer.SerializeToNode(editor.State)!;
            Task RenameOther(string name) => Job("archive_edit", new() { ["document"] = other.SessionId.ToString(), ["revision"] = other.Revision, ["action"] = "rename", ["member"] = other.ResourceEdits!.Current.Members[0].Id.ToString(), ["name"] = name });
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
