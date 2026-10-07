using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Reflection;
using System.Text.Json.Nodes;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using Recoil.Zbd.Automation;
using Recoil.Zbd.Desktop;
using Recoil.Zbd.Mcp;
using Recoil.Zbd.Tests;
using Xunit;

namespace Recoil.Zbd.Desktop.Tests;

/// <summary>
/// A source project that is a link, or lies below one, through the GUI's operations and the named-pipe MCP connection:
/// Work with source project → Open and open_root with project refuse it alike; opened as a plain folder, its worlds, edits
/// and recovery are refused, and nothing is written into the folder the link leads to.
/// </summary>
internal static class LinkedSourceProjectChecks
{
    internal static async Task Run()
    {
        using var fixture = new SourceWorldFixture();
        string links = Path.Combine(Path.GetTempPath(), "zstudio-links-" + Guid.NewGuid().ToString("N"));
        string direct = Path.Combine(links, "direct"), above = Path.Combine(links, "above");
        Directory.CreateDirectory(links);
        try
        {
            if (!Junction(direct, fixture.Project) || !Junction(above, fixture.Root)) return; // Junctions unavailable on this file system.
            string sentinelFolder = Path.Combine(fixture.Root, "retained-cache"); Directory.CreateDirectory(sentinelFolder);
            string sentinel = Path.Combine(sentinelFolder, "keep.txt"); File.WriteAllText(sentinel, "keep");
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(90)); var token = deadline.Token;
            var main = new MainWindow { Left = -12000, ShowInTaskbar = false }; main.Show();
            try
            {
                await using var host = new LocalMcpHost(main.Commands, "test");
                await using var pipe = new NamedPipeClientStream(".", host.Instance.Pipe, PipeDirection.InOut, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly); await pipe.ConnectAsync(token);
                await using var client = await McpClient.CreateAsync(new StreamClientTransport(pipe, pipe), cancellationToken: token);
                var require = typeof(MainWindow).GetMethod("RequireSourceProjectAsync", BindingFlags.Static | BindingFlags.NonPublic)!;

                foreach (string linked in new[] { direct, Path.Combine(above, "project") })
                {
                    // The welcome screen's Open and open_root with project give the same refusal, and nothing opens.
                    string before = main.ViewModel.RootPath;
                    var refused = await Job("open_root", new() { ["path"] = linked, ["project"] = true }, "failed");
                    Assert.Equal("not_project", refused["code"]!.GetValue<string>());
                    Assert.Contains("is a link; source projects are opened only from regular folders", refused["message"]!.GetValue<string>());
                    var gui = await Assert.ThrowsAsync<StudioCommandException>(() => (Task)require.Invoke(null, [linked, token])!);
                    Assert.Equal(("not_project", refused["message"]!.GetValue<string>()), (gui.Code, gui.Message));
                    Assert.Equal(before, main.ViewModel.RootPath);

                    // File → Open folder and open_root without project open it to browse; nothing edits it or writes into it.
                    await Job("open_root", new() { ["path"] = linked });
                    Assert.Equal(Path.GetFullPath(linked), Path.GetFullPath(main.ViewModel.RootPath));
                    while (!main.ViewModel.Problems.Any(p => p.Message.StartsWith("Source project recovery could not be checked", StringComparison.Ordinal) && p.Message.Contains("is a link", StringComparison.Ordinal)))
                    { token.ThrowIfCancellationRequested(); await Task.Delay(10, token); }
                    var world = await Job("source_world_open", new() { ["mission"] = "m1" }, "failed");
                    Assert.Equal("io_failed", world["code"]!.GetValue<string>());
                    Assert.Contains("source projects are opened only from regular folders", world["message"]!.GetValue<string>());
                    Assert.Empty(main.ViewModel.Documents);
                    foreach (string command in new[] { "source_changes", "source_recovery" })
                    {
                        string error = await Error(command);
                        Assert.Contains("io_failed", error); Assert.Contains("is a link", error);
                    }
                    Assert.False(Directory.Exists(Path.Combine(fixture.Project, "zstudio")));
                }

                using (var cache = new Recoil.Zbd.Core.Sources.SourcePreviewCache(fixture.Project))
                    Assert.Throws<ArgumentException>(() => cache.DeleteBuild(Path.Combine(above, "retained-cache")));
                Assert.Equal("keep", File.ReadAllText(sentinel));

                // The project's own folder is a project like any other.
                await Job("open_root", new() { ["path"] = fixture.Project, ["project"] = true });
                var opened = await Job("source_world_open", new() { ["mission"] = "m1" });
                Assert.NotNull(opened["document"]);
                Assert.Equal(Path.GetFullPath(fixture.Project), Path.GetFullPath((await Call("source_changes", new()))["project"]!.GetValue<string>()));

                async Task<JsonNode> Call(string name, Dictionary<string, object?> arguments)
                {
                    var result = await client.CallToolAsync("zstudio_" + name, arguments, cancellationToken: token);
                    Assert.False(result.IsError == true, string.Join(";", result.Content.OfType<TextContentBlock>().Select(c => c.Text)));
                    return JsonNode.Parse(result.Content.OfType<TextContentBlock>().Single().Text)!;
                }
                async Task<string> Error(string name)
                {
                    var result = await client.CallToolAsync("zstudio_" + name, new Dictionary<string, object?>(), cancellationToken: token);
                    string text = string.Join(";", result.Content.OfType<TextContentBlock>().Select(c => c.Text));
                    Assert.True(result.IsError == true, text);
                    return text;
                }
                async Task<JsonNode> Job(string name, Dictionary<string, object?> arguments, string expected = "completed")
                {
                    var job = await Call(name, arguments); string id = job["id"]!.GetValue<string>();
                    while (job["State"]!.GetValue<string>() is "queued" or "running") { await Task.Delay(10, token); job = await Call("operation", new() { ["id"] = id }); }
                    Assert.True(job["State"]!.GetValue<string>() == expected, job.ToJsonString()); return job["result"]!;
                }
            }
            finally { foreach (var doc in main.ViewModel.Documents.ToArray()) main.ViewModel.CloseResolved(doc); main.Close(); }
        }
        finally
        {
            // The links first, so that removing their folder never reaches the project.
            foreach (string link in new[] { direct, above }) try { if (Directory.Exists(link)) Directory.Delete(link); } catch (IOException) { }
            try { Directory.Delete(links, true); } catch (IOException) { }
        }
    }

    private static bool Junction(string link, string target)
    {
        using var process = Process.Start(new ProcessStartInfo("cmd.exe", $"/c mklink /J \"{link}\" \"{target}\"") { CreateNoWindow = true, UseShellExecute = false, RedirectStandardOutput = true });
        process!.WaitForExit(); return Directory.Exists(link) && new DirectoryInfo(link).Attributes.HasFlag(FileAttributes.ReparsePoint);
    }
}
