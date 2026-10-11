using System.IO;
using System.IO.Pipes;
using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Controls;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using Recoil.Zbd.Core;
using Recoil.Zbd.Core.Formats;
using Recoil.Zbd.Core.Sources;
using Recoil.Zbd.Desktop;
using Recoil.Zbd.Mcp;
using Recoil.Zbd.Tests;
using Xunit;

namespace Recoil.Zbd.Desktop.Tests;

/// <summary>
/// What the GUI and MCP promise around exports and source projects: path arguments are full paths, the Blender checkout is
/// an operation, a late Cancel in Initialize does not open the project, and lookup changes are compared with what was saved.
/// </summary>
internal static class ExportSafetyMcpChecks
{
    internal static async Task Run()
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(120)); var token = deadline.Token;
        var main = new MainWindow { Left = -12000, ShowInTaskbar = false }; main.Show();
        try
        {
            await using var host = new LocalMcpHost(main.Commands, "test");
            await using var pipe = new NamedPipeClientStream(".", host.Instance.Pipe, PipeDirection.InOut, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly); await pipe.ConnectAsync(token);
            await using var client = await McpClient.CreateAsync(new StreamClientTransport(pipe, pipe), cancellationToken: token);
            await LateCancelChecks(main, token);
            await RelativePathChecks(main, Job, token);
            await SourceResourceOperationChecks.Run(main, Job, token);
            await ExportChoiceContextChecks(main, token);
            await TextureFailureAndMissingInputChecks(main, Job, token);
            await RejectedAnimationChecks(main, Job, token);
            await LookupBaselineChecks(main, Call, Job, token);

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
        finally { foreach (var doc in main.ViewModel.Documents.ToArray()) main.ViewModel.CloseResolved(doc); main.Close(); }
    }

    private static async Task RejectedAnimationChecks(MainWindow main, Func<string, Dictionary<string, object?>, string, Task<JsonNode>> job, CancellationToken token)
    {
        using SourceWorldFixture fixture = new();
        fixture.Write("data/m1/zrdr/gates.zad", "ANIMATION_DEFINITIONS ( ANIMATION_LIST ( ANIMATION_DEFINITION ( NAME ( ground ) ANIMATION_ROOT_NAME ( absent ) ) ) )");
        await main.ViewModel.OpenRootAsync(fixture.Project, token);
        var check = await job("source_export", new() { ["outputs"] = new[] { "m1/anim.zbd" } }, "completed");
        Assert.Equal(1, check["failed"]!.GetValue<int>());
        Assert.Contains("absent", check.ToJsonString());
        var gui = await main.ExportSourceProjectAsync(null, ["m1/anim.zbd"], false, token);
        Assert.Equal(1, gui.Failed);
        Assert.Contains(main.ViewModel.Problems, p => p.Severity == "Error" && p.Message.Contains("rejects", StringComparison.Ordinal));
        string destination = Path.Combine(fixture.Root, "export");
        await job("source_export", new() { ["destination"] = destination, ["outputs"] = new[] { "m1/anim.zbd" } }, "failed");
        Assert.Empty(Directory.GetFileSystemEntries(destination));
    }

    private static async Task TextureFailureAndMissingInputChecks(MainWindow main, Func<string, Dictionary<string, object?>, string, Task<JsonNode>> job, CancellationToken token)
    {
        using SourceWorldFixture fixture = new();
        const string model = "data/m1/models/m1.gltf", definition = "data/m1/zrdr/gates.zad";
        byte[] original = File.ReadAllBytes(fixture.Path(model)); fixture.Write(model, "invalid JSON");
        await main.ViewModel.OpenRootAsync(fixture.Project, token);
        var check = await job("source_export", new() { ["outputs"] = new[] { "m1/rtexture16.zbd" } }, "completed");
        Assert.Equal(1, check["failed"]!.GetValue<int>());
        var gui = await main.ExportSourceProjectAsync(null, ["m1/rtexture16.zbd"], false, token);
        Assert.Equal(1, gui.Failed);
        Assert.Contains(main.ViewModel.Problems, p => p.Severity == "Error" && p.Message.Contains("world does not assemble", StringComparison.Ordinal));
        string destination = Path.Combine(fixture.Root, "export");
        await job("source_export", new() { ["destination"] = destination, ["outputs"] = new[] { "m1/rtexture16.zbd" } }, "failed");
        Assert.Empty(Directory.GetFileSystemEntries(destination));
        fixture.Write(model, original);
        byte[] definitions = File.ReadAllBytes(fixture.Path(definition)); File.Delete(fixture.Path(definition));
        await job("source_world_open", new() { ["mission"] = "m1" }, "completed");
        var doc = main.ViewModel.Documents.Single(d => d.SourceWorld != null);
        Assert.Contains(definition, doc.SourceBuild!.MissingInputs); Assert.False(doc.SourceInputsChanged());
        fixture.Write(definition, definitions); Assert.True(doc.SourceInputsChanged());
        main.ViewModel.CloseResolved(doc);
        await job("source_world_open", new() { ["mission"] = "m1" }, "completed");
        doc = main.ViewModel.Documents.Single(d => d.SourceWorld != null);
        string path = fixture.Path(model); DateTime stamp = File.GetLastWriteTimeUtc(path);
        string json = File.ReadAllText(path); Assert.Contains("ground", json);
        fixture.Write(model, json.Replace("ground", "GROUND", StringComparison.Ordinal)); File.SetLastWriteTimeUtc(path, stamp);
        Assert.True(await Task.Run(() => doc.SourceInputsChanged(token), token));
        main.ViewModel.CloseResolved(doc);

        fixture.Write(model, original);
        fixture.Write("gamegen/build-profiles/pair.json", """{"format":"recoil-build-profile","version":1,"texturePacks":[{"file":"rtexture2.zbd"},{"file":"rtexture16.zbd"}]}""");
        string stale = Path.Combine(destination, "m1/rtexture16.zbd"); Directory.CreateDirectory(Path.GetDirectoryName(stale)!); File.WriteAllBytes(stale, [1, 2, 3]);
        var exported = await job("source_export", new() { ["destination"] = destination, ["outputs"] = new[] { "m1/rtexture2.zbd" }, ["profile"] = "pair" }, "completed");
        Assert.Contains("m1/rtexture16.zbd", exported.ToJsonString());
        var exportedGui = await main.ExportSourceProjectAsync(destination, ["m1/rtexture2.zbd"], true, token, profile: "pair");
        Assert.Contains(exportedGui.Notes, n => n.StartsWith("m1/rtexture16.zbd", StringComparison.Ordinal));
    }

    private static async Task ExportChoiceContextChecks(MainWindow main, CancellationToken token)
    {
        using var original = new SourceWorldFixture(); using var replacement = new SourceWorldFixture();
        await main.ViewModel.OpenRootAsync(original.Project, token);
        long generation = main.ViewModel.WorkspaceGeneration;
        // The root changes while the GUI's off-thread output plan/destination confirmation is pending.
        await main.ViewModel.OpenRootAsync(replacement.Project, token);
        string destination = Path.Combine(replacement.Project, "..", "unexpected-export");
        var error = await Assert.ThrowsAsync<Recoil.Zbd.Automation.StudioCommandException>(() =>
            main.ExportSourceProjectAsync(destination, ["m1/gamez.zbd"], true, token, expectedGeneration: generation));
        Assert.Equal("context_changed", error.Code);
        Assert.False(Directory.Exists(destination));
        // Returning to the same path must not revive the stale confirmation either.
        await main.ViewModel.OpenRootAsync(original.Project, token);
        await Assert.ThrowsAsync<Recoil.Zbd.Automation.StudioCommandException>(() =>
            main.ExportSourceProjectAsync(destination, ["m1/gamez.zbd"], true, token, expectedGeneration: generation));
    }

    /// <summary>
    /// Cancel pressed once the work has passed its last check: the project is written, but the dialog does not open it (as
    /// source_reconstruct leaves a project cancelled while it opens); it says where the project is and stays open.
    /// </summary>
    private static async Task LateCancelChecks(MainWindow main, CancellationToken token)
    {
        string project = Path.Combine(Path.GetTempPath(), "zstudio-late-" + Guid.NewGuid().ToString("N"));
        TaskCompletionSource<SourceReconstructionReport> finished = new(TaskCreationOptions.RunContinuationsAsynchronously);
        bool started = false;
        var dialog = new SourceInitializeDialog(main, Path.GetTempPath(), (_, _, _, _) => { started = true; return finished.Task; });
        string? shown = null; bool stayedOpen = false;
        bool? result = Modal(dialog, async () =>
        {
            while (!dialog.IsLoaded) await Task.Delay(10, token);
            Find<TextBox>(dialog, t => System.Windows.Automation.AutomationProperties.GetName(t) == "Source project folder").Text = project;
            Click(Find<Button>(dialog, b => b.Content as string == "Initialize and open"));
            while (!started) { token.ThrowIfCancellationRequested(); await Task.Delay(10, token); }
            // Cancel, then the work (which no longer looks at its token) finishes.
            var cancel = Find<Button>(dialog, b => b.Content as string == "Cancel");
            Click(cancel);
            finished.SetResult(new(project, 1, new Dictionary<string, int>(), [], []));
            var error = Find<TextBlock>(dialog, t => t.Foreground == System.Windows.Media.Brushes.IndianRed);
            while (dialog.IsVisible && error.Text.Length == 0) { token.ThrowIfCancellationRequested(); await Task.Delay(10, token); }
            shown = error.Text; stayedOpen = dialog.IsVisible;
            if (dialog.IsVisible) Click(cancel);
        });
        Assert.NotEqual(true, result);
        Assert.Null(dialog.Report);
        Assert.True(stayedOpen);
        Assert.Contains(project, shown);
        Assert.Contains("was not opened", shown);
        Assert.False(main.ViewModel.HasRoot);
    }

    /// <summary>File and folder arguments resolve against nothing but themselves: a relative one is refused before anything is read or written.</summary>
    private static async Task RelativePathChecks(MainWindow main, Func<string, Dictionary<string, object?>, string, Task<JsonNode>> job, CancellationToken token)
    {
        using var fixture = new SourceFixture();
        // A texture pack beside the corpus's archives, for texture_import.
        byte[] rgba = new byte[16 * 16 * 4]; Array.Fill(rgba, (byte)200);
        var pack = TexturePackBuilder.Build([new("rock", "rock", new DecodedImage(16, 16, rgba))], TexturePackVariant.FromFileName("rtexture4.zbd")!, token);
        await File.WriteAllBytesAsync(Path.Combine(fixture.Corpus, "m1", "rtexture4.zbd"), pack.Bytes, token);
        await job("open_root", new() { ["path"] = fixture.Corpus }, "completed");
        await Refused("open_document", new() { ["path"] = "m1/zrdr.zbd" });
        string archivePath = Path.Combine(fixture.Corpus, "m1", "zrdr.zbd");
        var archive = Document(await job("open_document", new() { ["path"] = archivePath }, "completed"));
        var asset = archive.PreviewDocument.Assets[0];
        await Refused("save_document", new() { ["document"] = Id(archive), ["revision"] = archive.Revision, ["destination"] = "copy.zbd" });
        await Refused("save_document", new() { ["document"] = Id(archive), ["revision"] = archive.Revision, ["destinations"] = new Dictionary<string, object?> { [archivePath] = "copy.zbd" } });
        await Refused("save_document", new() { ["document"] = Id(archive), ["revision"] = archive.Revision, ["modelDirectory"] = "models" });
        await Refused("export", new() { ["document"] = Id(archive), ["destination"] = "exported" });
        await Refused("model_bundle_export", new() { ["document"] = Id(archive), ["kind"] = asset.Kind.ToString(), ["index"] = asset.Index, ["destination"] = "bundle" });
        await Refused("model_replace", new() { ["document"] = Id(archive), ["revision"] = archive.Revision, ["manifest"] = "manifest.json" });
        await Refused("mech_model_replace", new() { ["document"] = Id(archive), ["revision"] = archive.Revision, ["member"] = Guid.NewGuid().ToString(), ["localModel"] = 0, ["material"] = 0, ["path"] = "model.obj" });
        await Refused("archive_edit", new() { ["document"] = Id(archive), ["revision"] = archive.Revision, ["action"] = "add", ["name"] = "extra.zrd", ["path"] = "extra.zrd" });
        var textures = Document(await job("open_document", new() { ["path"] = Path.Combine(fixture.Corpus, "m1", "rtexture4.zbd") }, "completed"));
        await Refused("texture_import", new() { ["document"] = Id(textures), ["revision"] = textures.Revision, ["path"] = "rock.png", ["index"] = 0 });
        await Refused("texture_import", new() { ["document"] = Id(textures), ["revision"] = textures.Revision, ["path"] = Path.Combine(fixture.Root, "rock.png"), ["index"] = 0,
            ["targets"] = new[] { new Dictionary<string, object?> { ["path"] = "m1/rtexture4.zbd", ["index"] = 0 } } });
        // Nothing was edited or written beside zStudio.
        Assert.False(archive.IsDirty); Assert.False(textures.IsDirty);
        foreach (string name in new[] { "copy.zbd", "models", "exported", "bundle" })
            Assert.False(Path.Exists(Path.Combine(AppContext.BaseDirectory, name)) || Path.Exists(Path.Combine(Environment.CurrentDirectory, name)), name);
        foreach (var doc in main.ViewModel.Documents.ToArray()) main.ViewModel.CloseResolved(doc);

        async Task Refused(string name, Dictionary<string, object?> arguments)
        {
            var failure = await job(name, arguments, "failed");
            Assert.Equal("invalid_argument", failure["code"]!.GetValue<string>());
            Assert.Contains("as a full path", failure["message"]!.GetValue<string>());
        }
        DocumentModel Document(JsonNode state) => main.ViewModel.Documents.Single(d => d.SessionId.ToString() == state["id"]!.GetValue<string>());
    }

    /// <summary>
    /// The Blender checkout is an operation like the other long source operations; and lookup changes are compared with what
    /// was saved: read from the world the document holds (not its build's files, which another program may hold), and, for a
    /// world showing a build older than the saved sources, only from a build that reads saved sources alone.
    /// </summary>
    private static async Task LookupBaselineChecks(MainWindow main, Func<string, Dictionary<string, object?>, Task<JsonNode>> call, Func<string, Dictionary<string, object?>, string, Task<JsonNode>> job, CancellationToken token)
    {
        using var fixture = new SourceWorldFixture();
        // m1 and m2 both load m1's database, whose part (a crate with a lid) it copies twice; each mission's texture effects find the lid.
        fixture.WritePartDatabase();
        foreach (string mission in new[] { "m1", "m2" })
        {
            fixture.Write($"gamegen/{mission}_zbd.gs", $"source support\\tex_fx{mission}.gw\r\nQuit\r\n");
            fixture.Write($"gamegen/support/tex_fx{mission}.gw", "FindNode lid\r\nQuit\r\n");
        }
        fixture.Write("gamegen/m2.gs", string.Join("\r\n", "set worldName world", "SetModelDirectory ..\\data\\m1\\models", "SetTextureDirectory ..\\data\\m1\\textures",
            "NewWorld %worldName%", "FindNode %worldName%", "GameGenSetWorld %worldName%", "FindNode %worldName%", "WorldOrigin 0.0 512.0", "WorldExtents 512.0 -512.0", "WorldPartition 256 -256",
            "LoadGameGen m1.flt m1.flt", "DeleteTree m1.flt", "GameZWriteZBDFile ..\\m2\\gamez.zbd", "Quit", ""));
        // A profile file written for looser rules blocks only itself: source_status and Tools → Build profile list it with the
        // reason, and the project's default profile stays usable.
        fixture.Write("gamegen/build-profiles/old.json", """{ "format": "recoil-build-profile", "version": 1, "texturePacks": [ { "file": "rtexture4.zbd" }, { "file": "texturemax.zbd", "maximumDimension": 2048 } ] }""");
        await job("open_root", new() { ["path"] = fixture.Project }, "completed");
        var status = await job("source_status", new(), "completed");
        Assert.Equal("modern", status["profile"]!.GetValue<string>());
        Assert.Contains("software renderer", status["profiles"]!.AsArray().Single(p => p!["name"]!.GetValue<string>() == "old")!["error"]!.GetValue<string>());
        typeof(MainWindow).GetMethod("ToolsMenuOpened", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.Invoke(main, [main, new RoutedEventArgs(MenuItem.SubmenuOpenedEvent, main)]);
        while (((MenuItem)main.FindName("SourceProfileMenu")).Items.Count != 3) await Task.Delay(10, token);
        var profiles = ((MenuItem)main.FindName("SourceProfileMenu")).Items.Cast<MenuItem>().ToArray();
        Assert.False(profiles.Single(i => System.Windows.Automation.AutomationProperties.GetName(i) == "old").IsEnabled);
        Assert.True(profiles.Single(i => System.Windows.Automation.AutomationProperties.GetName(i) == "modern").IsChecked);
        // The profile chosen in Tools → Build profile can break later: source_status still answers, listing the profiles with
        // the chosen one's error, so another can be named.
        fixture.Write("gamegen/build-profiles/fine.json", """{ "format": "recoil-build-profile", "version": 1, "texturePacks": [ { "file": "rtexture4.zbd" } ] }""");
        typeof(MainWindow).GetMethod("ToolsMenuOpened", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.Invoke(main, [main, new RoutedEventArgs(MenuItem.SubmenuOpenedEvent, main)]);
        while (((MenuItem)main.FindName("SourceProfileMenu")).Items.Count != 4) await Task.Delay(10, token);
        ((MenuItem)main.FindName("SourceProfileMenu")).Items.Cast<MenuItem>().Single(i => System.Windows.Automation.AutomationProperties.GetName(i) == "fine").RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
        while (!((MenuItem)main.FindName("SourceProfileMenu")).Items.Cast<MenuItem>().Single(i => System.Windows.Automation.AutomationProperties.GetName(i) == "fine").IsChecked) await Task.Delay(10, token);
        fixture.Write("gamegen/build-profiles/fine.json", """{ "format": "recoil-build-profile", "version": 1, "texturePacks": [ { "file": "rtexture4.zbd" }, { "file": "texturemax.zbd", "maximumDimension": 2048 } ] }""");
        status = await job("source_status", new(), "completed");
        Assert.Equal(("fine", "fine"), (status["profile"]!.GetValue<string>(), status["selected"]!.GetValue<string>()));
        Assert.Contains("software renderer", status["profileError"]!.GetValue<string>());
        Assert.Equal(["fine", "modern", "old", "original"], status["profiles"]!.AsArray().Select(p => p!["name"]!.GetValue<string>()).Order());
        File.Delete(fixture.Path("gamegen/build-profiles/fine.json"));
        // An unrelated unreadable profile may block listing/default resolution, but not a valid named selection.
        fixture.Write("gamegen/build-profiles/broken.json", "{");
        var selected = await job("source_profile", new() { ["profile"] = "original" }, "completed");
        Assert.Equal("original", selected["profile"]!.GetValue<string>());
        File.Delete(fixture.Path("gamegen/build-profiles/broken.json"));
        await job("source_profile", new(), "completed");

        // The checkout returns an operation, which can be cancelled, and completes with the checkout.
        var started = await call("source_blender_checkout", new() { ["model"] = "data/m1/models/m1.gltf" });
        Assert.Equal(("source_blender_checkout", (bool?)true), (started["Name"]?.GetValue<string>(), started["Cancellable"]?.GetValue<bool>()));
        var operation = started;
        while (operation["State"]!.GetValue<string>() is "queued" or "running") { await Task.Delay(10, token); operation = await call("operation", new() { ["id"] = started["id"]!.GetValue<string>() }); }
        Assert.Equal("completed", operation["State"]!.GetValue<string>());
        Assert.StartsWith(Path.Combine(fixture.Project, "zstudio", "export"), operation["result"]!["folder"]!.GetValue<string>());

        var m1 = Document((await job("source_world_open", new() { ["mission"] = "m1" }, "completed"))["document"]!);
        var m2 = Document((await job("source_world_open", new() { ["mission"] = "m2" }, "completed"))["document"]!);
        Assert.DoesNotContain(main.ViewModel.Problems, p => Changed(p, "m1") || Changed(p, "m2"));
        // Copying the crate in m1 copies its lid, which FindNode lid then finds; m2 reads the same database and is now stale.
        m1 = await Duplicate(m1, "crate", "crate2");
        Assert.Contains(main.ViewModel.Problems, p => Changed(p, "m1") && p.Message.Contains("crate2/lid", StringComparison.Ordinal));
        Assert.True(m2.SourceInputsChanged());

        // Saved while another program holds the shown build's world file: the baseline is the world the document holds.
        using (new FileStream(m1.SourceBuild!.WorldPath, FileMode.Open, FileAccess.Read, FileShare.None))
            await job("save_document", new() { ["document"] = Id(m1), ["revision"] = m1.Revision }, "completed");
        Assert.DoesNotContain(main.ViewModel.Problems, p => Changed(p, "m1") || Changed(p, "m2"));

        // Another edit of the database keeps the project unsaved; m2 reloaded with it finds the saved copy's lid, which is no
        // change since the save (m2 showed a build from before it, so nothing is compared until a build reads saved sources alone).
        int ground = m1.PreviewDocument.Scene!.Nodes.First(n => n.Name == "ground" && m1.SourceBuild!.Provenance.ContainsKey(n.Index)).Index;
        m1 = Document((await job("source_world_object_edit", new() { ["document"] = Id(m1), ["revision"] = m1.Revision, ["node"] = ground, ["position"] = new Dictionary<string, object?> { ["x"] = 5, ["y"] = 0, ["z"] = -5 } }, "completed"))["document"]!);
        m2 = main.ViewModel.Documents.Single(d => d.SourceWorld?.Mission == "m2");
        m2 = Document((await job("reload_document", new() { ["document"] = Id(m2), ["revision"] = m2.Revision }, "completed"))!);
        Assert.DoesNotContain(main.ViewModel.Problems, p => Changed(p, "m2"));
        // m1's next copy, of crate2, is compared with what was saved: its lid is found now, not crate2's.
        m1 = await Duplicate(m1, "crate2", "crate3");
        Assert.Contains(main.ViewModel.Problems, p => Changed(p, "m1") && p.Message.Contains("crate3/lid", StringComparison.Ordinal));

        async Task<DocumentModel> Duplicate(DocumentModel doc, string source, string name)
        {
            int crate = doc.PreviewDocument.Scene!.Nodes.First(n => n.Name == source && doc.SourceBuild!.Provenance.ContainsKey(n.Index)).Index;
            return Document((await job("source_world_object_edit", new() { ["document"] = Id(doc), ["revision"] = doc.Revision, ["node"] = crate, ["action"] = "duplicate", ["name"] = name }, "completed"))["document"]!);
        }
        bool Changed(StudioProblem p, string mission) => p.File == Path.Combine(fixture.Project, "gamegen", mission + ".gs")
            && p.Message.Contains($"FindNode lid in gamegen/support/tex_fx{mission}.gw finds", StringComparison.Ordinal) && p.Message.Contains("when the world was opened or last saved", StringComparison.Ordinal);
        DocumentModel Document(JsonNode state) => main.ViewModel.Documents.Single(d => d.SessionId.ToString() == state["id"]!.GetValue<string>());
    }

    private static string Id(DocumentModel d) => d.SessionId.ToString();
    private static void Click(Button button) => button.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
    /// <summary>A modal dialog runs a nested message loop: the queued driver runs inside it until the dialog closes.</summary>
    private static bool? Modal(Window window, Func<Task> drive)
    {
        Exception? failure = null;
        _ = window.Dispatcher.InvokeAsync(async () =>
        {
            try { await drive(); }
            catch (Exception ex) { failure = ex; if (window.IsVisible) window.Close(); }
        });
        bool? shown = window.ShowDialog();
        if (failure != null) throw new Xunit.Sdk.XunitException("Driving the dialog failed: " + failure);
        return shown;
    }
    private static T Find<T>(DependencyObject root, Func<T, bool> match) where T : DependencyObject
    {
        foreach (object child in LogicalTreeHelper.GetChildren(root))
        {
            if (child is not DependencyObject node) continue;
            if (node is T found && match(found)) return found;
            try { return Find(node, match); } catch (InvalidOperationException) { }
        }
        throw new InvalidOperationException($"No {typeof(T).Name} matched.");
    }
}
