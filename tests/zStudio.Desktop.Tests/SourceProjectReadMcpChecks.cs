using System.IO;
using System.IO.Pipes;
using System.Text;
using System.Text.Json.Nodes;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using Recoil.Zbd.Core.Sources;
using Recoil.Zbd.Desktop;
using Recoil.Zbd.Mcp;
using Recoil.Zbd.Tests;
using Xunit;

namespace Recoil.Zbd.Desktop.Tests;

/// <summary>Root-scoped read results must belong to the current project, including a round trip back to the same path.</summary>
internal static class SourceProjectReadMcpChecks
{
    internal static async Task Run()
    {
        using var a = new SourceWorldFixture();
        using var b = new SourceWorldFixture();
        using var deadline = new CancellationTokenSource(TimeSpan.FromMinutes(2));
        var token = deadline.Token;
        a.Write("data/only_a.gltf", "{\"asset\":{\"version\":\"2.0\"}}");
        b.Write("data/only_b.gltf", "{\"asset\":{\"version\":\"2.0\"}}");
        var publisher = new SourcePublisher(a.Project) { Fault = (step, _) => { if (step == "cleanup") throw new SourcePublisher.Crash(); } };
        Assert.Throws<SourcePublisher.Crash>(() => publisher.Publish([new("gamegen/notice.gs", null, Encoding.ASCII.GetBytes("# saved"))], "Completed A save", token));
        string save = Assert.Single(new SourcePublisher(a.Project).FindInterrupted(token)).SaveId;
        var main = new MainWindow { Left = -12000, ShowInTaskbar = false }; main.Show();
        try
        {
            await using var host = new LocalMcpHost(main.Commands, "test");
            await using var pipe = new NamedPipeClientStream(".", host.Instance.Pipe, PipeDirection.InOut, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
            await pipe.ConnectAsync(token);
            await using var client = await McpClient.CreateAsync(new StreamClientTransport(pipe, pipe), cancellationToken: token);
            foreach (string command in new[] { "source_world_models", "source_recovery" })
            {
                await Open(a.Project);
                await VerifyCurrent(command, inA: true);
                foreach (bool returnToA in new[] { false, true })
                {
                    await Open(a.Project);
                    TaskCompletionSource entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
                    using SemaphoreSlim proceed = new(0);
                    CancellationToken observed = default;
                    main.SourceProjectReading = (name, ct) =>
                    {
                        Assert.Equal(command, name); Assert.False(main.Dispatcher.CheckAccess());
                        observed = ct; entered.TrySetResult();
                        // Deliberately defer observation of cancellation until the completed root replacement(s).
                        Assert.True(proceed.Wait(TimeSpan.FromSeconds(30), CancellationToken.None), "The read barrier was not released.");
                    };
                    var pending = client.CallToolAsync("zstudio_" + command, new Dictionary<string, object?>(), cancellationToken: token);
                    try
                    {
                        await entered.Task.WaitAsync(token);
                        await Open(b.Project);
                        if (returnToA) await Open(a.Project);
                        Assert.True(observed.IsCancellationRequested);
                    }
                    finally { main.SourceProjectReading = null; proceed.Release(); }
                    var stale = await pending;
                    Assert.True(stale.IsError);
                    Assert.Equal("context_changed", stale.StructuredContent!.Value.GetProperty("code").GetString());
                    await VerifyCurrent(command, inA: returnToA);
                }

                TaskCompletionSource waiting = new(TaskCreationOptions.RunContinuationsAsynchronously);
                TaskCompletionSource canceled = new(TaskCreationOptions.RunContinuationsAsynchronously);
                using SemaphoreSlim release = new(0);
                using var request = CancellationTokenSource.CreateLinkedTokenSource(token);
                main.SourceProjectReading = (_, ct) =>
                {
                    using var registration = ct.Register(() => canceled.TrySetResult());
                    waiting.TrySetResult();
                    Assert.True(release.Wait(TimeSpan.FromSeconds(30), CancellationToken.None), "The cancellation barrier was not released.");
                    ct.ThrowIfCancellationRequested();
                };
                var canceling = client.CallToolAsync("zstudio_" + command, new Dictionary<string, object?>(), cancellationToken: request.Token);
                try { await waiting.Task.WaitAsync(token); request.Cancel(); await canceled.Task.WaitAsync(token); }
                finally { main.SourceProjectReading = null; release.Release(); }
                await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => { await canceling; });
                await VerifyCurrent(command, inA: true);
            }

            async Task VerifyCurrent(string command, bool inA)
            {
                var result = await Call(command, new());
                if (command == "source_recovery")
                {
                    Assert.Equal(inA ? 1 : 0, result["saveCount"]!.GetValue<int>());
                    if (inA) Assert.Equal(save, result["saves"]![0]!["id"]!.GetValue<string>());
                }
                else
                {
                    string json = result.ToJsonString();
                    Assert.Contains(inA ? "only_a.gltf" : "only_b.gltf", json);
                    Assert.DoesNotContain(inA ? "only_b.gltf" : "only_a.gltf", json);
                }
            }
            async Task Open(string root)
            {
                var job = await Call("open_root", new() { ["path"] = root, ["project"] = true });
                string id = job["id"]!.GetValue<string>();
                while (job["State"]!.GetValue<string>() is "queued" or "running")
                { await Task.Delay(10, token); job = await Call("operation", new() { ["id"] = id }); }
                Assert.Equal("completed", job["State"]!.GetValue<string>());
            }
            async Task<JsonNode> Call(string name, Dictionary<string, object?> args)
            {
                var result = await client.CallToolAsync("zstudio_" + name, args, cancellationToken: token);
                string text = result.Content.OfType<TextContentBlock>().Single().Text;
                Assert.False(result.IsError == true, text); return JsonNode.Parse(text)!;
            }
        }
        finally { main.SourceProjectReading = null; main.Close(); }
    }
}
