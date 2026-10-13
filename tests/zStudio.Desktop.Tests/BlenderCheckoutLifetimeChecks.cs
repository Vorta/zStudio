using System.IO;
using System.IO.Pipes;
using System.Reflection;
using System.Text.Json.Nodes;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using Recoil.Zbd.Automation;
using Recoil.Zbd.Core.Sources;
using Recoil.Zbd.Desktop;
using Recoil.Zbd.Mcp;
using Recoil.Zbd.Tests;
using Xunit;

namespace Recoil.Zbd.Desktop.Tests;

/// <summary>The read-only checkout listing belongs to the root that started it.</summary>
internal static class BlenderCheckoutLifetimeChecks
{
    private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;

    internal static async Task Run()
    {
        using SourceWorldFixture original = new(), replacement = new();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(90));
        var token = deadline.Token;
        var main = new MainWindow { Left = -12000, ShowInTaskbar = false }; main.Show();
        try
        {
            await using var host = new LocalMcpHost(main.Commands, "test");
            await using var pipe = new NamedPipeClientStream(".", host.Instance.Pipe, PipeDirection.InOut, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
            await pipe.ConnectAsync(token);
            await using var client = await McpClient.CreateAsync(new StreamClientTransport(pipe, pipe), cancellationToken: token);
            var protocol = new Protocol(client, token);
            foreach (bool mcp in new[] { false, true })
                await Listing(main, original, replacement, protocol, mcp, token);
        }
        finally
        {
            main.ReadCheckoutExports = SourceBlender.CheckoutExports;
            foreach (var doc in main.ViewModel.Documents.ToArray()) main.ViewModel.CloseResolved(doc);
            main.Close();
        }
    }

    private static async Task Listing(MainWindow main, SourceWorldFixture original, SourceWorldFixture replacement,
        Protocol protocol, bool mcp, CancellationToken token)
    {
        await main.ViewModel.OpenRootAsync(original.Project, token);
        using ManualResetEventSlim release = new();
        TaskCompletionSource<CancellationToken> entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        main.ReadCheckoutExports = (root, ct) =>
        {
            Assert.False(main.Dispatcher.CheckAccess());
            entered.TrySetResult(ct);
            Assert.True(release.Wait(TimeSpan.FromSeconds(20), token));
            return []; // A listing that completed at the cancellation boundary must still not publish in the new root.
        };
        Task<JsonNode>? remote = mcp ? protocol.Call("source_blender_checkouts", new(), "context_changed") : null;
        Task? gui = mcp ? null : (Task)typeof(MainWindow).GetMethod("CheckoutExportsAsync", Private)!.Invoke(main, [original.Project, token])!;
        try
        {
            var captured = await entered.Task.WaitAsync(token);
            await main.ViewModel.OpenRootAsync(replacement.Project, token);
            Assert.True(captured.IsCancellationRequested);
        }
        finally { release.Set(); }
        if (mcp) await remote!;
        else Assert.Equal("context_changed", (await Assert.ThrowsAsync<StudioCommandException>(async () => { await gui!; })).Code);
        main.ReadCheckoutExports = SourceBlender.CheckoutExports;
        if (mcp) await protocol.Call("source_blender_checkouts", new());
        else await (Task)typeof(MainWindow).GetMethod("CheckoutExportsAsync", Private)!.Invoke(main, [replacement.Project, token])!;
    }

    private sealed class Protocol(McpClient client, CancellationToken token)
    {
        internal async Task<JsonNode> Call(string name, Dictionary<string, object?> arguments, string? error = null)
        {
            var result = await client.CallToolAsync("zstudio_" + name, arguments, cancellationToken: token);
            var data = JsonNode.Parse(result.Content.OfType<TextContentBlock>().Single().Text)!;
            if (error == null) Assert.False(result.IsError == true, data.ToJsonString());
            else { Assert.True(result.IsError == true, data.ToJsonString()); Assert.Equal(error, data["code"]!.GetValue<string>()); }
            return data;
        }
    }
}
