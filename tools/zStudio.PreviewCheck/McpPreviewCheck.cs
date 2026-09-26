using System.IO;
using System.IO.Pipes;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Windows;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using Recoil.Zbd.Desktop;
using Recoil.Zbd.Mcp;

internal static class McpPreviewCheck
{
    public static int Run(string root)
    {
        root = Path.GetFullPath(root);
        string settings = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "RecoilZbdStudio", "settings.json");
        byte[]? originalSettings = File.Exists(settings) ? File.ReadAllBytes(settings) : null;
        string output = Path.Combine(Path.GetTempPath(), "zstudio-mcp-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(output);
        var app = new App { ProcessCommandLine = false, ShutdownMode = ShutdownMode.OnExplicitShutdown };
        app.InitializeComponent();
        int exit = 0;
        app.Startup += async (_, _) =>
        {
            var window = (MainWindow)app.MainWindow;
            try
            {
                await using var host = new LocalMcpHost(window.Commands, "check");
                await using var pipe = new NamedPipeClientStream(".", host.Instance.Pipe, PipeDirection.InOut, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
                await pipe.ConnectAsync(5000);
                await using var client = await McpClient.CreateAsync(new StreamClientTransport(pipe, pipe));
                async Task<JsonNode> Call(string name, object arguments)
                {
                    using var deadline = new CancellationTokenSource(TimeSpan.FromMinutes(3));
                    var args = JsonSerializer.SerializeToNode(arguments)!.AsObject().ToDictionary(p => p.Key, p => (object?)JsonSerializer.SerializeToElement(p.Value));
                    var reply = await client.CallToolAsync("zstudio_" + name, args, cancellationToken: deadline.Token);
                    var data = JsonNode.Parse(reply.Content.OfType<TextContentBlock>().First().Text)!;
                    if (reply.IsError == true) throw new InvalidDataException(name + ": " + data);
                    foreach (var image in reply.Content.OfType<ImageContentBlock>()) File.WriteAllBytes(Path.Combine(output, "capture-" + Guid.NewGuid().ToString("N") + ".png"), image.DecodedData.ToArray());
                    if (name != "operation" && data is JsonObject obj && obj.ContainsKey("State"))
                    {
                        string id = data["id"]!.GetValue<string>();
                        while (data["State"]!.GetValue<string>() is "queued" or "running") { await Task.Delay(100, deadline.Token); data = await Call("operation", new { id }); }
                        if (data["State"]!.GetValue<string>() != "completed") throw new InvalidDataException(name + ": " + data);
                        return data["result"]!;
                    }
                    return data;
                }
                await Call("open_root", new { path = root });
                async Task<(string Document, string Preview)> Select(string path, string kind, string query)
                {
                    var d = await Call("open_document", new { path }); string document = d["id"]!.GetValue<string>();
                    var list = await Call("assets", new { document, query });
                    var item = list["items"]!.AsArray().First();
                    await Call("select_asset", new { document, kind, index = item!["Index"]!.GetValue<int>() });
                    var state = await Call("state", new { });
                    return (document, state["preview"]!.GetValue<string>());
                }
                var animPath = Path.Combine(root, "m1", "anim.zbd"); byte[] animHash = SHA256.HashData(File.ReadAllBytes(animPath));
                var (animation, preview) = await Select(animPath, "Animation", "vtol_destruction1");
                await Call("animation_options", new { preview, changes = new { mute = true, height = 50, grid = true, collision = true } });
                var editor = (AnimationEditor)((System.Windows.Controls.ContentControl)window.FindName("AnimationHost")).Content;
                ((System.Windows.Controls.TextBox)editor.FindName("PreviewHeight")).Text = "-";
                bool draftRejected = false;
                try { await Call("animation_transport", new { preview, action = "play" }); }
                catch (InvalidDataException ex) when (ex.Message.Contains("pending_drafts",StringComparison.Ordinal)) { draftRejected = true; }
                if (!draftRejected) throw new InvalidDataException("Partial height input was not guarded.");
                var draft = await Call("drafts", new { target = "preview" });
                await Call("resolve_drafts", new { document = animation, target = "preview", token = draft["drafts"]!["token"]!.GetValue<string>(), action = "discard" });
                await Call("animation_transport", new { preview, action = "seek", seconds = .5 });
                // Cancel an in-flight seek at its actual UI boundary, before any
                // render callback; the retained player must remain usable afterward.
                var playButton = (System.Windows.Controls.Button)editor.FindName("PlayButton");
                using (var canceledSeek = new CancellationTokenSource())
                {
                    DependencyPropertyChangedEventHandler cancelSeek = (_, _) => { if (!playButton.IsEnabled) canceledSeek.Cancel(); };
                    playButton.IsEnabledChanged += cancelSeek;
                    try { using (PreviewOperation.Begin(canceledSeek.Token)) await editor.SeekAsync(.25); }
                    finally { playButton.IsEnabledChanged -= cancelSeek; }
                    if (!canceledSeek.IsCancellationRequested || !playButton.IsEnabled) throw new InvalidDataException("Canceled MCP seek left playback disabled.");
                }
                await Call("animation_transport", new { preview, action = "seek", seconds = .5 });
                var captured = await Call("capture", new { target = "preview", preview, width = 800, height = 600 });
                var loadingPanel = (FrameworkElement)editor.FindName("LoadingPanel");
                var showLevel = (System.Windows.Controls.Primitives.ToggleButton)editor.FindName("ShowLevel");
                bool originalMap = showLevel.IsChecked == true;
                using (var canceledRefresh = new CancellationTokenSource())
                {
                    var visibility = System.ComponentModel.DependencyPropertyDescriptor.FromProperty(UIElement.VisibilityProperty, loadingPanel.GetType());
                    EventHandler cancelRefresh = (_, _) => { if (loadingPanel.Visibility == Visibility.Visible) canceledRefresh.Cancel(); };
                    visibility.AddValueChanged(loadingPanel, cancelRefresh);
                    try
                    {
                        using (PreviewOperation.Begin(canceledRefresh.Token)) await editor.SetPreviewOptionAsync("map", JsonValue.Create(!originalMap)!);
                        if (!canceledRefresh.IsCancellationRequested || loadingPanel.Visibility != Visibility.Collapsed) throw new InvalidDataException("Canceled MCP level refresh retained the loading overlay.");
                    }
                    finally { visibility.RemoveValueChanged(loadingPanel, cancelRefresh); }
                }
                // A second rendering option and transport must work without reselecting the asset.
                await Call("animation_options", new { preview, changes = new { map = originalMap } });
                await Call("animation_transport", new { preview, action = "seek", seconds = .5 });
                await Call("animation_transport", new { preview, action = "play" });
                await Call("animation_transport", new { preview, action = "pause" });
                double viewportAspect = editor.Viewport.ActualWidth / editor.Viewport.ActualHeight;
                double imageAspect = captured["PixelWidth"]!.GetValue<double>() / captured["PixelHeight"]!.GetValue<double>();
                if (Math.Abs(imageAspect / viewportAspect - 1) > .02) throw new InvalidDataException("MCP capture distorted the viewport aspect ratio.");
                var records = await Call("assets", new { document = animation, query = "vtol_destruction1" });
                int entry = records["items"]![0]!["Index"]!.GetValue<int>();
                var fields = await Call("property_fields", new { document = animation, entry });
                string field = fields["fields"]!["fields"]!.AsArray().Single(f => f!["Label"]!.GetValue<string>() == "Reset delay (s)")!["Id"]!.GetValue<string>();
                await Call("property_edit", new { document = animation, revision = fields["Revision"]!.GetValue<long>(), entry, field, value = "2.5" });
                fields = await Call("property_fields", new { document = animation, entry });
                await Call("save_document", new { document = animation, revision = fields["Revision"]!.GetValue<long>(), destination = Path.Combine(output, "edited-anim.zbd") });
                if (!animHash.SequenceEqual(SHA256.HashData(File.ReadAllBytes(animPath)))) throw new InvalidDataException("Animation source changed.");
                Console.WriteLine("MCP animation seek, render, property edit and verified Save As passed.");
                var textureFile = window.ViewModel.Files.First(f => f.RelativePath.EndsWith("image.zbd", StringComparison.OrdinalIgnoreCase));
                var (texture, texturePreview) = await Select(textureFile.Path, "Texture", "");
                await Call("texture_view", new { preview = texturePreview, zoom = 1, channel = 2, x = 0, y = 0 });
                await Call("capture", new { target = "preview", preview = texturePreview });
                await Call("export", new { document = texture, destination = Path.Combine(output, "texture"), assets = new[] { new { kind = "Texture", index = 0 } } });
                Console.WriteLine("MCP texture controls, capture and export passed.");
                var (sound, soundPreview) = await Select(Path.Combine(root,"soundsh.zbd"), "Sound", "");
                await Call("sound_transport", new { preview = soundPreview, action = "read" });
                await Call("sound_transport", new { preview = soundPreview, action = "play" });
                await Call("sound_transport", new { preview = soundPreview, action = "pause" });
                await Call("sound_transport", new { preview = soundPreview, action = "seek", seconds = 0 });
                await Call("sound_transport", new { preview = soundPreview, action = "stop" });
                Console.WriteLine("MCP sound initialization/play/pause/seek/stop passed.");
                var gamezPath = Path.Combine(root, "m1", "gamez.zbd");
                var (world, worldPreview) = await Select(gamezPath, "World", "Whole world");
                async Task CheckCanceledStaticRefresh()
                {
                    var state = await Call("state", new { });
                    string retainedPreview = state["preview"]!.GetValue<string>();
                    var sceneHost = (System.Windows.Controls.ContentControl)window.FindName("SceneHost");
                    var retainedScene = (Recoil.Zbd.Rendering.SceneViewport)sceneHost.Content;
                    var retainedData = retainedScene.PreviewScene;
                    var retainedView = retainedScene.CaptureView();
                    var before = await Call("preview_state", new { preview = retainedPreview });
                    var lod = (System.Windows.Controls.ComboBox)window.FindName("LodCombo");
                    var pack = (System.Windows.Controls.ComboBox)window.FindName("TexturePackCombo");
                    int retainedPack = pack.SelectedIndex;
                    foreach (var changes in new object[]
                    {
                        new { lod = lod.Items.Count > 1 ? (lod.SelectedIndex + 1) % lod.Items.Count : lod.SelectedIndex },
                        new { horizon = !before["horizon"]!.GetValue<bool>() },
                        new { texturePack = before["texturePacks"]!.AsArray().Last()!["Path"]?.GetValue<string>() ?? "" }
                    })
                    {
                        Task? shutdown = null;
                        System.ComponentModel.PropertyChangedEventHandler cancel = async (_, e) =>
                        {
                            if (e.PropertyName != nameof(MainViewModel.Status) || window.ViewModel.Status != "Updating preview…") return;
                            // Let replacement preparation start, then stop the actual
                            // operation through the same shutdown path as the GUI.
                            await System.Windows.Threading.Dispatcher.Yield(System.Windows.Threading.DispatcherPriority.Background);
                            shutdown = window.StopMcpAsync();
                        };
                        window.ViewModel.PropertyChanged += cancel;
                        bool canceled = false;
                        try { await Call("scene_options", new { preview = retainedPreview, changes }); }
                        catch (InvalidDataException ex) when (ex.Message.Contains("canceled", StringComparison.Ordinal)) { canceled = true; }
                        finally { window.ViewModel.PropertyChanged -= cancel; }
                        if (shutdown != null) await shutdown;
                        if (!canceled || !ReferenceEquals(sceneHost.Content, retainedScene) || !ReferenceEquals(retainedScene.PreviewScene, retainedData) || retainedScene.CaptureView() != retainedView)
                            throw new InvalidDataException("Canceled static refresh replaced the retained scene or camera.");
                        if (((FrameworkElement)window.FindName("EmptyPreview")).Visibility != Visibility.Collapsed || pack.SelectedIndex != retainedPack)
                            throw new InvalidDataException("Canceled static refresh left an overlay or changed the texture picker.");
                        var after = await Call("preview_state", new { preview = retainedPreview });
                        if (!JsonNode.DeepEquals(before["lod"], after["lod"]) || !JsonNode.DeepEquals(before["horizon"], after["horizon"]))
                            throw new InvalidDataException("Canceled static refresh retained uncommitted option values.");
                        await Call("capture", new { target = "preview", preview = retainedPreview, width = 800, height = 600 });
                    }
                    await Call("scene_options", new { preview = retainedPreview, changes = new { horizon = !before["horizon"]!.GetValue<bool>() } });
                    var published = await Call("state", new { });
                    if (published["preview"]!.GetValue<string>() == retainedPreview || ReferenceEquals(sceneHost.Content, retainedScene))
                        throw new InvalidDataException("Successful static refresh did not publish its replacement.");
                    await Call("capture", new { target = "preview", preview = published["preview"]!.GetValue<string>(), width = 800, height = 600 });
                }
                await CheckCanceledStaticRefresh();
                var modelRecord = window.ViewModel.SelectedDocument!.Document.Assets.First(a => a.Kind == Recoil.Zbd.Core.AssetKind.Model);
                await Call("select_asset", new { document = world, kind = "Model", index = modelRecord.Index });
                await CheckCanceledStaticRefresh();
                (_, worldPreview) = await Select(gamezPath, "World", "Whole world");
                foreach (double vertical in new[] { 1.0, -1.0 })
                {
                    var upright = await Call("camera", new { preview = worldPreview, action = "set", position = new[] { 2000, 300, 2000 }, look = new[] { 0.0, vertical, 0.0 } });
                    double y = upright["LookDirection"]!["Y"]!.GetValue<double>();
                    if (Math.Abs(y) >= 1) throw new InvalidDataException("Vertical MCP camera was not clamped before returning.");
                    await Call("camera", new { preview = worldPreview, action = "move", right = 10 });
                }
                await Call("camera", new { preview = worldPreview, action = "move", forward = 10, up = 10 });
                var worldCapture = await Call("capture", new { target = "preview", preview = worldPreview, width = 800, height = 600 });
                if (worldCapture["preview"]!.GetValue<string>() != worldPreview || worldCapture["asset"]!["Kind"]!.GetValue<string>() != "World") throw new InvalidDataException("Capture identity differs from the selected preview.");
                var placements = await Call("pickups", new { document = world });
                var pickup = placements["items"]![0]!;
                var state2 = await Call("state", new { });
                long revision = state2["documents"]!.AsArray().Single(d => d!["id"]!.GetValue<string>() == world)!["Revision"]!.GetValue<long>();
                await Call("pickup_lock", new { document = world, revision, locked = false });
                var position = pickup["position"]!;
                await Call("pickup_move", new { document = world, revision, source = pickup["source"]!.DeepClone(), x = position["X"]!.GetValue<double>() + 1, y = position["Y"]!.GetValue<double>(), z = position["Z"]!.GetValue<double>() });
                state2 = await Call("state", new { });
                revision = state2["documents"]!.AsArray().Single(d => d!["id"]!.GetValue<string>() == world)!["Revision"]!.GetValue<long>();
                await Call("undo_redo", new { document = world, revision, action = "undo" });
                await Call("validate", new { document = world });
                await Call("capture", new { target = "window" });
                Console.WriteLine("MCP Whole world, camera, pickup editing/undo and validation passed.");
                File.WriteAllText(Path.Combine(output, "result.json"), (await Call("state", new { })).ToJsonString());
                Console.WriteLine("PASS " + output);
            }
            catch (Exception ex) { Console.Error.WriteLine(ex); exit = 1; }
            finally
            {
                foreach (var doc in window.ViewModel.Documents.ToArray()) window.ViewModel.CloseResolved(doc);
                window.Close(); app.Shutdown(exit);
            }
        };
        try { app.Run(); }
        finally { if (originalSettings != null) File.WriteAllBytes(settings, originalSettings); else if (File.Exists(settings)) File.Delete(settings); }
        return exit;
    }
}
