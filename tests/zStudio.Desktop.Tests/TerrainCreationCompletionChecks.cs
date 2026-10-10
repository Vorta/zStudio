using System.Collections.Specialized;
using System.IO;
using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Controls;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using Recoil.Zbd.Core.Sources;
using Recoil.Zbd.Desktop;
using Recoil.Zbd.Tests;
using Xunit;

namespace Recoil.Zbd.Desktop.Tests;

/// <summary>Terrain completion is prepared before acceptance; neither listing nor cancellation can conceal an accepted edit.</summary>
internal static class TerrainCreationCompletionChecks
{
    internal static async Task<DocumentModel> Run(MainWindow main, SourceWorldFixture fixture, DocumentModel doc, McpClient client, CancellationToken token)
    {
        const string model = "data/m1/models/terrain/hills.gltf", recipe = "data/m1/models/terrain/hills.terrain.json";
        var workspace = doc.SourceWorld!.Workspace;
        var originalList = main.TerrainRecipeFiles;
        byte[] database = File.ReadAllBytes(fixture.Path("data/m1/models/m1.gltf"));
        long revision = workspace.Revision;
        var history = workspace.History.ToArray();
        try
        {
            // The actual operation can be polled/cancelled while its completion inventory is still being prepared.
            TaskCompletionSource entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
            main.TerrainRecipeFiles = (owner, ct) =>
            {
                Assert.Same(workspace, owner); // Listing unrelated recipes must not register prepared dependencies.
                Assert.False(main.Dispatcher.CheckAccess()); Assert.True(ct.CanBeCanceled);
                entered.TrySetResult();
                Assert.True(ct.WaitHandle.WaitOne(TimeSpan.FromSeconds(20)), "Recipe enumeration was not canceled.");
                ct.ThrowIfCancellationRequested(); return [];
            };
            var operation = await Call("source_terrain_create", Arguments());
            await entered.Task.WaitAsync(token);
            await Call("state", new());
            await Call("operation", new() { ["id"] = operation["id"]!.GetValue<string>(), ["cancel"] = true });
            await Finish(operation, "canceled"); Unchanged();

            // The GUI's shared creation service has the same worker/cancellation boundary as the MCP job.
            entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
            var gui = main.CreateTerrainAsync(doc, model, ["land"], null, token);
            await entered.Task.WaitAsync(token);
            await main.Dispatcher.InvokeAsync(() => ((MenuItem)main.FindName("CancelOperationItem"))
                .RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent)));
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => gui); Unchanged();

            // A listing error after the isolated source edit still precedes acceptance in the owning workspace.
            main.TerrainRecipeFiles = (owner, ct) =>
            {
                Assert.Same(workspace, owner); Assert.False(main.Dispatcher.CheckAccess());
                ct.ThrowIfCancellationRequested(); throw new IOException("controlled recipe inventory refusal");
            };
            var failed = await Finish(await Call("source_terrain_create", Arguments()), "failed");
            Assert.Equal("io_failed", failed["code"]!.GetValue<string>());
            Assert.Contains("controlled recipe inventory refusal", failed["message"]!.GetValue<string>()); Unchanged();

            // Inspection used to schedule a worker but omit the token from the enumeration inside it.
            entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
            TaskCompletionSource stopped = new(TaskCreationOptions.RunContinuationsAsynchronously);
            main.TerrainRecipeFiles = (_, ct) =>
            {
                Assert.False(main.Dispatcher.CheckAccess()); Assert.True(ct.CanBeCanceled);
                entered.TrySetResult();
                try
                {
                    Assert.True(ct.WaitHandle.WaitOne(TimeSpan.FromSeconds(20)), "Inspection enumeration did not receive cancellation.");
                    ct.ThrowIfCancellationRequested(); return [];
                }
                finally { stopped.TrySetResult(); }
            };
            using (var request = CancellationTokenSource.CreateLinkedTokenSource(token))
            {
                var reading = client.CallToolAsync("zstudio_source_terrain", new Dictionary<string, object?> { ["document"] = doc.SessionId.ToString() }, cancellationToken: request.Token);
                await entered.Task.WaitAsync(token);
                await Call("state", new()); request.Cancel();
                try { var response = await reading; Assert.True(response.IsError); }
                catch (OperationCanceledException) { }
                await stopped.Task.WaitAsync(token);
            }
            Unchanged();

            // An accepted change made while inventory is held invalidates this prepared completion as well as its edit.
            entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
            using (var proceed = new SemaphoreSlim(0))
            {
                main.TerrainRecipeFiles = (owner, ct) =>
                {
                    var files = originalList(owner, ct); entered.TrySetResult();
                    Assert.True(proceed.Wait(TimeSpan.FromSeconds(20), ct)); return files;
                };
                operation = await Call("source_terrain_create", Arguments());
                await entered.Task.WaitAsync(token);
                var intervening = workspace.Apply("concurrent source", [("gamegen/completion-note.gs", new byte[] { 35, 10 })], token)!;
                try
                {
                    proceed.Release();
                    failed = await Finish(operation, "failed");
                    Assert.Equal("context_changed", failed["code"]!.GetValue<string>());
                    Assert.False(doc.IsDisposed); Assert.Single(workspace.History);
                    Assert.Equal(["gamegen/completion-note.gs"], workspace.DirtyFiles);
                    Assert.False(workspace.Exists(recipe, token));
                }
                finally { proceed.Release(); workspace.Retract(intervening); }
                revision = workspace.Revision; history = workspace.History.ToArray(); Unchanged();
            }

            // Refusal/retry reaches a single accepted transaction. Cancel exactly when the replacement is published;
            // the old document's linked lifetime is also canceled then. Completion must require no second inventory.
            int reads = 0;
            bool published = false;
            main.TerrainRecipeFiles = (owner, ct) =>
            {
                Assert.False(main.Dispatcher.CheckAccess()); Assert.False(published);
                Assert.Same(workspace, owner); Interlocked.Increment(ref reads);
                return originalList(owner, ct);
            };
            void Replaced(object? sender, NotifyCollectionChangedEventArgs e)
            {
                if (e.Action != NotifyCollectionChangedAction.Replace || !e.OldItems!.Contains(doc)) return;
                published = true;
                // RegisterOperationCommands performs this same cancellation on the dispatcher.
                var field = typeof(MainWindow).GetField("runningJob", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!;
                var job = field.GetValue(main)!;
                ((CancellationTokenSource)job.GetType().GetProperty("Cancellation")!.GetValue(job)!).Cancel();
                main.TerrainRecipeFiles = (_, _) => throw new IOException("No optional inventory may run after publication.");
            }
            main.ViewModel.Documents.CollectionChanged += Replaced;
            JsonNode accepted;
            try { operation = await Call("source_terrain_create", Arguments()); accepted = await Finish(operation, "completed"); }
            finally { main.ViewModel.Documents.CollectionChanged -= Replaced; }
            Assert.True(published); Assert.Equal(1, reads);
            Assert.Single(accepted["recipes"]!.AsArray()); Assert.Equal(recipe, accepted["recipes"]![0]!.GetValue<string>());
            Assert.Equal(1, accepted["recipeCount"]!.GetValue<int>()); Assert.Null(accepted["nextRecipeOffset"]);
            Assert.Equal(history.Length + 1, workspace.History.Count);
            Assert.Equal(["data/m1/models/m1.gltf", recipe], workspace.DirtyFiles);
            Assert.Equal(database, File.ReadAllBytes(fixture.Path("data/m1/models/m1.gltf"))); Assert.False(File.Exists(fixture.Path(recipe)));
            var retained = await Call("operation", new() { ["id"] = operation["id"]!.GetValue<string>() });
            Assert.Equal("completed", retained["State"]!.GetValue<string>()); Assert.True(JsonNode.DeepEquals(accepted, retained["result"]));
            doc = main.ViewModel.Documents.Single(d => d.SessionId.ToString() == accepted["document"]!["id"]!.GetValue<string>());
            // Return the fixture to its original source state for the ordinary terrain editing controls that follow.
            var undone = await Call("undo_redo", new() { ["document"] = doc.SessionId.ToString(), ["revision"] = doc.Revision, ["action"] = "undo" });
            doc = main.ViewModel.Documents.Single(d => d.SessionId.ToString() == undone["id"]!.GetValue<string>());
            Assert.False(workspace.IsDirty); Assert.Equal(database, workspace.Read("data/m1/models/m1.gltf", token));

            CheckPages(token);
            return doc;
        }
        finally { main.TerrainRecipeFiles = originalList; }

        Dictionary<string, object?> Arguments() => new() { ["document"] = doc.SessionId.ToString(), ["revision"] = doc.Revision, ["model"] = model, ["surfaces"] = new[] { "land" } };
        void Unchanged()
        {
            Assert.False(doc.IsDisposed); Assert.Same(doc, main.ViewModel.Documents.Single(d => d.SourceWorld != null));
            Assert.Equal(revision, workspace.Revision); Assert.Equal(history, workspace.History); Assert.False(workspace.IsDirty);
            Assert.Equal(database, workspace.Read("data/m1/models/m1.gltf", token)); Assert.False(workspace.Exists(recipe, token));
            Assert.Equal(database, File.ReadAllBytes(fixture.Path("data/m1/models/m1.gltf"))); Assert.False(File.Exists(fixture.Path(recipe)));
        }
        async Task<JsonNode> Call(string name, Dictionary<string, object?> args)
        {
            var response = await client.CallToolAsync("zstudio_" + name, args, cancellationToken: token);
            string text = response.Content.OfType<TextContentBlock>().Single().Text;
            Assert.False(response.IsError == true, text); return JsonNode.Parse(text)!;
        }
        async Task<JsonNode> Finish(JsonNode operation, string expected)
        {
            string id = operation["id"]!.GetValue<string>();
            while (operation["State"]!.GetValue<string>() is "queued" or "running")
            { await Task.Delay(10, token); operation = await Call("operation", new() { ["id"] = id }); }
            Assert.True(operation["State"]!.GetValue<string>() == expected, operation.ToJsonString()); return operation["result"]!;
        }
    }

    private static void CheckPages(CancellationToken token)
    {
        string created = "data/created.terrain.json", used = "data/z-used.terrain.json";
        string longPath = "data/" + string.Join('/', Enumerable.Repeat(new string('&', 190), 3)) + "/last.terrain.json";
        string[] files = [.. Enumerable.Range(0, 65).Select(i => $"data/r{i:D2}.terrain.json"), used, longPath];
        var active = MainWindow.TerrainCreationPage(files, [used], created, true, token);
        Assert.Equal(68, active["total"]!.GetValue<int>()); Assert.Equal(64, active["items"]!.AsArray().Count);
        Assert.Equal(64, active["nextOffset"]!.GetValue<int>());
        Assert.Equal(created, active["items"]![0]!.GetValue<string>()); Assert.Equal(used, active["items"]![1]!.GetValue<string>());
        Assert.Contains(longPath, active["items"]!.AsArray().Select(n => n!.GetValue<string>()));
        var noPieces = MainWindow.TerrainCreationPage(files, [used], created, false, token);
        Assert.Equal(used, noPieces["items"]![0]!.GetValue<string>());
        var single = MainWindow.TerrainCreationPage([], [], created, true, token);
        Assert.Single(single["items"]!.AsArray()); Assert.Null(single["nextOffset"]);
        string maximumPath = "data/" + string.Join('/', Enumerable.Repeat(new string('&', 199), 160)) + "/x.terrain.json";
        var bounded = MainWindow.TerrainCreationPage(Enumerable.Range(0, 64).Select(i => maximumPath + i).ToArray(), [], created, true, token);
        Assert.InRange(bounded["items"]!.AsArray().Count, 1, 63);
        Assert.True(bounded.ToJsonString().Length < 1024 * 1024 + 4096);
        using var canceled = new CancellationTokenSource(); canceled.Cancel();
        Assert.Throws<OperationCanceledException>(() => MainWindow.TerrainCreationPage(files, [], created, true, canceled.Token));
    }
}
