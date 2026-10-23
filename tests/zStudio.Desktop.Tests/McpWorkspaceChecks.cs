using System.IO;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Threading;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using zStudio.Core;
using zStudio.Core.Animation;
using zStudio.Mcp;
using Xunit;

namespace zStudio.Desktop.Tests;

internal static class McpWorkspaceChecks
{
    internal static async Task Run(Application app)
    {
        var main = new MainWindow { Left=-12000,ShowInTaskbar=false }; main.Show();
        var package = new AnimationPackage { Prefix=new byte[72],Tail=[] };
        var entry = new AnimationEntry(new byte[308],0,0); entry.SetText(0,"mcp fixture");
        var sequence = new AnimationSequence(new byte[64]) { Name="sequence" }; sequence.Events.Add(AnimationCatalog.Create(10)); entry.Sequences.Add(sequence); package.Entries.Add(entry);
        var source = new AssetDocument(Path.Combine(Path.GetTempPath(),"mcp-fixture.zbd"),new(0,DateTime.MinValue),new(FormatFamily.Animation,28,Recognition.Supported,"MCP fixture"),ReadOnlyMemory<byte>.Empty) { Animations=package };
        source.Add(AssetKind.Raw,0,"Metadata",0,0); source.Add(AssetKind.Raw,1,"Recovery fixture",0,0); var doc = new DocumentModel(source);
        main.ViewModel.Documents.Add(doc); main.ViewModel.SelectedDocument=doc;
        try
        {
            McpParityChecks.Run(main.Commands);
            byte[] imageBytes = Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVQIHWP4z8DwHwAFgAI/ScLttAAAAABJRU5ErkJggg==");
            main.Commands.Add(new("zstudio_test_image", "Synthetic protocol image", false, [], (_,_)=>Task.FromResult(new zStudio.Automation.StudioResult(new JsonObject { ["image"]="fixture" },imageBytes))));
            await using var host = new LocalMcpHost(main.Commands,"test");
            await using var pipe = new NamedPipeClientStream(".",host.Instance.Pipe,PipeDirection.InOut,PipeOptions.Asynchronous|PipeOptions.CurrentUserOnly);
            await pipe.ConnectAsync(5000);
            await using var client = await McpClient.CreateAsync(new StreamClientTransport(pipe,pipe));
            var tools = await client.ListToolsAsync(); Assert.True(tools.Count>25);
            Assert.Contains(tools,t=>t.Name=="zstudio_save_document");
            foreach(var tool in tools) Assert.Equal("object",tool.ProtocolTool.InputSchema.GetProperty("type").GetString());
            var imageResult = await client.CallToolAsync("zstudio_test_image");
            Assert.False(imageResult.IsError == true);
            Assert.Equal(imageBytes,imageResult.Content.OfType<ImageContentBlock>().Single().DecodedData.ToArray());
            var resources = await client.ListResourcesAsync(); Assert.Equal(2,resources.Count);
            var state=await Call("state",new()); Assert.Equal(doc.SessionId.ToString(),state["documents"]![0]!["id"]!.GetValue<string>());
            // Help: the menu's guides are help's, in order, each a guide of this repository; an item opens the URL help reports.
            var help = await Call("help", new());
            var guides = help["guides"]!.AsArray().Select(g => g!["path"]!.GetValue<string>()).ToArray();
            var guideItems = ((MenuItem)main.FindName("HelpMenu")).Items.OfType<MenuItem>().Where(i => i.Tag is string).ToArray();
            Assert.Equal(guideItems.Select(i => (string)i.Tag), guides);
            string repository = AppContext.BaseDirectory;
            while (!File.Exists(Path.Combine(repository, "zStudio.slnx"))) repository = Directory.GetParent(repository)!.FullName;
            Assert.All(guides, g => Assert.True(File.Exists(Path.Combine(repository, g)), g));
            string helpVersion = help["version"]!.GetValue<string>();
            Assert.Equal($"https://github.com/Vorta/zStudio/releases/tag/v{helpVersion}", help["releaseNotes"]!.GetValue<string>());
            string? opened = null; main.OpenLinkOverride = url => opened = url;
            try { guideItems[2].RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent)); } finally { main.OpenLinkOverride = null; }
            Assert.Equal($"https://github.com/Vorta/zStudio/blob/v{helpVersion}/{guides[2]}", opened);
            Assert.Equal(opened, help["guides"]![2]!["url"]!.GetValue<string>());
            string originalPreview = state["preview"]!.GetValue<string>();
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            CheckNativeCorners(main);
            foreach (string action in new[] { "maximize", "restore", "minimize", "restore" })
            {
                var windowState = await Call("window", new() { ["action"] = action });
                string expected = action == "maximize" ? "Maximized" : action == "minimize" ? "Minimized" : "Normal";
                Assert.Equal(expected, windowState["state"]!.GetValue<string>());
                Assert.Equal(expected, main.WindowState.ToString());
                await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
                if (action != "minimize") CheckNativeCorners(main);
            }
            if (OperatingSystem.IsWindowsVersionAtLeast(10,0,22000))
                foreach (uint message in new uint[] { 0x031A, 0x031E })
                {
                    nint handle = new WindowInteropHelper(main).Handle;
                    int square = 1;
                    Assert.Equal(0,DwmSetWindowAttribute(handle,33,ref square,sizeof(int)));
                    SendMessage(handle,message,0,0);
                    await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
                    CheckNativeCorners(main);
                }
            await CancelPreviewJob("select_asset", new() { ["document"] = doc.SessionId.ToString(), ["kind"] = "Raw", ["index"] = 1 }, "Raw #1:");
            Assert.Equal(1, doc.SelectedAsset!.Index);
            Assert.Equal(Visibility.Collapsed, ((TextBlock)main.FindName("EmptyPreview")).Visibility);
            Assert.Equal(Visibility.Visible, ((TabControl)main.FindName("StructuredPanel")).Visibility);
            await Call("inspect_asset", new() { ["document"] = doc.SessionId.ToString(), ["kind"] = "Raw", ["index"] = 1 });
            await Call("capture", new() { ["target"] = "preview" }, "stale_preview");
            await Call("capture", new() { ["target"] = "preview", ["preview"] = Guid.NewGuid().ToString() }, "stale_preview");
            main.WindowState = WindowState.Minimized;
            await Call("capture", new() { ["target"] = "window" }, "not_visible");
            main.WindowState = WindowState.Normal;
            var layoutBefore = await Call("workspace_view", new());
            string otherTheme = layoutBefore["theme"]!.GetValue<string>() == "Light" ? "Dark" : "Light";
            await Call("workspace_view", new() { ["changes"] = new JsonObject { ["theme"] = otherTheme, ["toolsTab"] = 999 } }, "unavailable_tab");
            Assert.True(JsonNode.DeepEquals(layoutBefore, await Call("workspace_view", new())));
            await CheckUpdates();
            var filtered = await Call("animation_records", new() { ["document"] = doc.SessionId.ToString(), ["entry"] = 0, ["query"] = "SEQUENCE", ["limit"] = 1 });
            Assert.Equal(sequence.Id.ToString(), filtered["sequences"]!["items"]![0]!["id"]!.GetValue<string>());
            filtered = await Call("animation_records", new() { ["document"] = doc.SessionId.ToString(), ["entry"] = 0, ["query"] = "missing-sequence" });
            Assert.Equal(0, filtered["sequences"]!["total"]!.GetValue<int>());
            // A maximum event page must fit the real protocol without expanding
            // payloads in unreturned rows or keyframe streams in inspection.
            foreach (uint version in new uint[] { 28, 39 })
            {
                var largeEntry = new AnimationEntry(new byte[version == 39 ? 316 : 308], 0, 0);
                var largePackage = new AnimationPackage { Prefix = new byte[version == 39 ? 80 : 72], Tail = [] }; largePackage.Entries.Add(largeEntry);
                for (int i = 0; i < 201; i++)
                {
                    byte[] payload = new byte[16384]; payload[0] = 254; payload[1] = 1;
                    largeEntry.Primary.Events.Add(new(payload) { Version = version });
                }
                var largeSource = new AssetDocument(Path.Combine(Path.GetTempPath(), $"inspection-{version}.zbd"), new(0, DateTime.MinValue), new(FormatFamily.Animation, version, Recognition.Supported, "fixture"), ReadOnlyMemory<byte>.Empty) { Animations = largePackage };
                largeSource.Add(AssetKind.Animation, 0, "large", 0, 0, largeEntry.ToPreviewJson(), largeEntry);
                var largeDoc = new DocumentModel(largeSource); main.ViewModel.Documents.Add(largeDoc);
                try
                {
                    string sequenceId = largeDoc.Document.Animations!.Entries[0].Primary.Id.ToString();
                    foreach (string query in new[] { "", "Unknown event", "absent" })
                    {
                        var page = await Call("animation_records", new() { ["document"] = largeDoc.SessionId.ToString(), ["entry"] = 0, ["sequence"] = sequenceId, ["limit"] = 200, ["query"] = query });
                        Assert.Equal(query == "absent" ? 0 : 201, page["events"]!["total"]!.GetValue<int>());
                        if (query != "absent")
                        {
                            Assert.Equal(200, page["events"]!["items"]!.AsArray().Count);
                            Assert.True(page["events"]!["items"]![0]!["data"]!["raw_hex_truncated"]!.GetValue<bool>());
                        }
                        Assert.True(page.ToJsonString().Length < 1024 * 1024);
                    }
                    var inspection = await Call("inspect_asset", new() { ["document"] = largeDoc.SessionId.ToString(), ["kind"] = "Animation", ["index"] = 0 });
                    Assert.True(inspection.ToJsonString().Length < 65536);
                    Assert.Equal(0, largeDoc.Revision);
                }
                finally { main.ViewModel.CloseResolved(largeDoc); }
            }
            main.ViewModel.AddProblem("MCP search fixture", file: "fixture.zbd");
            filtered = await Call("problems", new() { ["query"] = "SEARCH FIXTURE" });
            Assert.Equal(1, filtered["total"]!.GetValue<int>());
            filtered = await Call("problems", new() { ["query"] = "missing-problem" });
            Assert.Equal(0, filtered["total"]!.GetValue<int>());
            await Call("source_bytes",new() { ["document"]=doc.SessionId.ToString(),["offset"]=0,["length"]=4097 },"invalid_argument");
            await Call("state",new() { ["unexpected"]=true },"invalid_argument");
            await Call("close_document",new() { ["document"]=doc.SessionId.ToString(),["revision"]=doc.Revision,["discard"]=true });
            await Call("assets",new() { ["document"]=doc.SessionId.ToString() },"stale_document");
            await Call("capture", new() { ["target"] = "preview", ["preview"] = originalPreview }, "stale_preview");
            Assert.DoesNotContain(app.Windows.Cast<Window>(),w=>w.Title=="Resolve property input");

            // Cancel the real MCP operation during indexing, then prove that the
            // retained workspace can still open files after access is stopped.
            string root = Path.Combine(Path.GetTempPath(), "zstudio-mcp-cancel-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            Task? shutdown = null;
            int indexed = 0;
            System.ComponentModel.PropertyChangedEventHandler changed = (_, e) =>
            {
                if (e.PropertyName == nameof(MainViewModel.Status) && main.ViewModel.Status.StartsWith("Indexed ", StringComparison.Ordinal))
                { indexed++; shutdown ??= main.StopMcpAsync(); }
            };
            try
            {
                for (int i = 0; i < 3; i++) File.WriteAllBytes(Path.Combine(root, i + ".zbd"), [1, 0, 0, 0, 0, 0, 0, 0]);
                main.ViewModel.PropertyChanged += changed;
                var job = await Call("open_root", new() { ["path"] = root });
                string id = job["id"]!.GetValue<string>();
                using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
                while (shutdown == null) await Task.Delay(10, deadline.Token);
                await shutdown.WaitAsync(deadline.Token);
                job = await Call("operation", new() { ["id"] = id });
                // Published, the root stays open: the cancel stopped only indexing, which the completed result reports.
                Assert.Equal("completed", job["State"]!.GetValue<string>()); Assert.False(job["result"]!["indexComplete"]!.GetValue<bool>());
                Assert.Equal(1, indexed); Assert.False(main.ViewModel.IsBusy); Assert.Equal(Path.GetFullPath(root), main.ViewModel.RootPath);
                var reopened = await main.ViewModel.OpenFileAsync(Path.Combine(root, "0.zbd"));
                Assert.NotNull(reopened); main.ViewModel.CloseResolved(reopened);
                main.ViewModel.PropertyChanged -= changed;
                string recoveryPath = Path.Combine(root, "recovery.zbd"); File.WriteAllBytes(recoveryPath, [255,255,255,255,255,255,255,255]);
                await CancelPreviewJob("open_document", new() { ["path"] = recoveryPath }, "Raw #0: recovery.zbd");
                Assert.Equal(recoveryPath, main.ViewModel.SelectedDocument!.Path);
                Assert.Equal(Visibility.Collapsed, ((TextBlock)main.FindName("EmptyPreview")).Visibility);
                var reloading = main.ViewModel.SelectedDocument;
                var load = main.ViewModel.LoadDocumentAsync;
                TaskCompletionSource reloadStarted = new(TaskCreationOptions.RunContinuationsAsynchronously);
                main.ViewModel.LoadDocumentAsync = async (_, token) =>
                { reloadStarted.SetResult(); await Task.Delay(Timeout.Infinite, token); throw new InvalidOperationException("unreachable"); };
                try
                {
                    var canceledReload = await Call("reload_document", new() { ["document"] = reloading.SessionId.ToString(), ["revision"] = reloading.Revision });
                    await reloadStarted.Task.WaitAsync(deadline.Token);
                    await main.StopMcpAsync().WaitAsync(deadline.Token);
                    canceledReload = await Call("operation", new() { ["id"] = canceledReload["id"]!.GetValue<string>() });
                    Assert.Equal("canceled", canceledReload["State"]!.GetValue<string>());
                    Assert.Same(reloading, main.ViewModel.SelectedDocument); Assert.False(reloading.IsDisposed);
                    Assert.StartsWith("Reload canceled", main.ViewModel.Status);
                    Assert.Equal(Visibility.Collapsed, ((TextBlock)main.FindName("EmptyPreview")).Visibility);
                }
                finally { main.ViewModel.LoadDocumentAsync = load; }
                File.Delete(recoveryPath);
                var reload = await Call("reload_document", new() { ["document"] = reloading.SessionId.ToString(), ["revision"] = reloading.Revision });
                while (reload["State"]!.GetValue<string>() is "queued" or "running")
                {
                    await Task.Delay(10, deadline.Token);
                    reload = await Call("operation", new() { ["id"] = reload["id"]!.GetValue<string>() });
                }
                Assert.Equal("failed", reload["State"]!.GetValue<string>());
                Assert.Equal("open_failed", reload["result"]!["code"]!.GetValue<string>());
                Assert.Same(reloading, main.ViewModel.SelectedDocument); Assert.False(reloading.IsDisposed);
                Assert.DoesNotContain("Reloading", main.ViewModel.Status);
                File.WriteAllBytes(recoveryPath, [255,255,255,255,255,255,255,255]);
                main.ViewModel.CloseResolved(reloading);
                await CancelPreviewJob("open_document", new() { ["path"] = recoveryPath }, "Raw #0: recovery.zbd", close: true);
                Assert.Null(main.ViewModel.SelectedDocument);
            }
            finally { main.ViewModel.PropertyChanged -= changed; Directory.Delete(root, true); }

            async Task CancelPreviewJob(string name, JsonObject args, string statusPrefix, bool close = false)
            {
                Task? stopped = null; bool canceled = false;
                System.ComponentModel.PropertyChangedEventHandler cancel = (_, e) =>
                {
                    if (canceled || e.PropertyName != nameof(MainViewModel.Status) || !main.ViewModel.Status.StartsWith(statusPrefix, StringComparison.Ordinal)) return;
                    canceled = true;
                    stopped = main.StopMcpAsync();
                    if (close) main.ViewModel.CloseResolved(main.ViewModel.SelectedDocument!);
                };
                main.ViewModel.PropertyChanged += cancel;
                try
                {
                    var job = await Call(name, args);
                    using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
                    do
                    {
                        await Task.Delay(10, deadline.Token);
                        job = await Call("operation", new() { ["id"] = job["id"]!.GetValue<string>() });
                    } while (job["State"]!.GetValue<string>() is "queued" or "running");
                    Assert.True(canceled, name + ": " + job.ToJsonString());
                    await stopped!;
                    Assert.Equal(close ? "failed" : "canceled", job["State"]!.GetValue<string>());
                    if (close) Assert.Equal("context_changed", job["result"]!["code"]!.GetValue<string>());
                }
                finally { main.ViewModel.PropertyChanged -= cancel; }
            }

            // The update check over MCP against a fake GitHub (the private profile's own checker never contacts it): a refusal
            // is reported, never thrown; a newer release shows Update available in the status bar without a source map, directly
            // left of where Preview notices sit; the automatic setting is saved and the check sends GitHub's required headers.
            async Task CheckUpdates()
            {
                var original = main.Updates; FakeGitHub github = new();
                main.Updates = new zStudio.Automation.UpdateChecker("0.9.7", github);
                try
                {
                    var update = (await Call("state", new()))["update"]!;
                    Assert.Equal(("unknown", true, "0.9.7"), (update["status"]!.GetValue<string>(), update["automatic"]!.GetValue<bool>(), update["current"]!.GetValue<string>()));
                    var item = (Button)main.FindName("StatusUpdate"); var text = (TextBlock)main.FindName("StatusUpdateText");
                    Assert.Equal(Visibility.Collapsed, item.Visibility);
                    var bar = (DockPanel)item.Parent; var right = (FrameworkElement)main.FindName("SourceStatusRight");
                    Assert.Equal(bar.Children.IndexOf(right) + 1, bar.Children.IndexOf(item)); Assert.Equal(Dock.Right, DockPanel.GetDock(item));
                    Assert.Same(main.FindName("StatusNotices"), ((Panel)right).Children[0]);

                    github.Next = FakeGitHub.RateLimited;
                    update = await Call("update_check", new() { ["action"] = "check" });
                    Assert.Equal("unknown", update["status"]!.GetValue<string>());
                    Assert.Contains("limit", update["failure"]!.GetValue<string>()); Assert.Equal(update["failure"]!.GetValue<string>(), update["reason"]!.GetValue<string>());
                    Assert.Equal((Visibility.Visible, "Couldn't check for updates"), (item.Visibility, text.Text));

                    const string page = "https://github.com/Vorta/zStudio/releases/tag/v0.9.8";
                    github.Next = () => FakeGitHub.Release("v0.9.8", page);
                    update = await Call("update_check", new() { ["action"] = "check" });
                    Assert.Equal(("available", "0.9.8", page), (update["status"]!.GetValue<string>(), update["latest"]!.GetValue<string>(), update["url"]!.GetValue<string>()));
                    Assert.Null(update["failure"]); Assert.Null(update["reason"]); Assert.NotNull(update["checkedUtc"]);
                    Assert.Equal((Visibility.Visible, "Update available", "0.9.8"), (item.Visibility, text.Text, ((TextBlock)main.FindName("StatusUpdateVersionText")).Text));
                    Assert.Equal((new Uri(zStudio.Automation.UpdateChecker.LatestReleaseApi), "zStudio/0.9.7", "application/vnd.github+json", "2022-11-28"), github.Requests[^1]);
                    update.AsObject().Remove("failure");
                    Assert.True(JsonNode.DeepEquals(update, (await Call("state", new()))["update"]));

                    await Call("update_check", new() { ["action"] = "automatic" }, "invalid_argument");
                    await Call("update_check", new() { ["enabled"] = false }, "invalid_argument");
                    update = await Call("update_check", new() { ["action"] = "automatic", ["enabled"] = false });
                    // Found by an explicit check, the update still shows.
                    Assert.Equal((false, "available"), (update["automatic"]!.GetValue<bool>(), update["status"]!.GetValue<string>()));
                    Assert.False(((MenuItem)main.FindName("AutomaticUpdatesMenu")).IsChecked);
                    var saved = StudioSettings.Load(); Assert.Equal((false, "0.9.8", page), (saved.CheckForUpdates, saved.UpdateLatestVersion, saved.UpdateLatestUrl));
                    int requests = github.Requests.Count;
                    update = await Call("update_check", new() { ["action"] = "automatic", ["enabled"] = true });
                    // Checked minutes ago: turning automatic checks on asks GitHub again only a day later.
                    Assert.Equal((true, requests), (update["automatic"]!.GetValue<bool>(), github.Requests.Count));
                    Assert.True(((MenuItem)main.FindName("AutomaticUpdatesMenu")).IsChecked);
                }
                finally
                {
                    // Later windows of the chain load the settings: no saved release may show Update available there.
                    main.Updates = original;
                    var settings = main.ViewModel.Settings;
                    settings.CheckForUpdates = true; settings.UpdateCheckedUtc = null; settings.UpdateLatestVersion = null; settings.UpdateLatestUrl = null; settings.Save();
                }
            }

            async Task<JsonNode> Call(string name,JsonObject arguments,string? error=null)
            {
                using var timeout=new CancellationTokenSource(TimeSpan.FromSeconds(15));
                var args=arguments.ToDictionary(p=>p.Key,p=>(object?)JsonSerializer.SerializeToElement(p.Value));
                var result=await client.CallToolAsync("zstudio_"+name,args,cancellationToken:timeout.Token);
                var json=JsonNode.Parse(result.Content.OfType<TextContentBlock>().First().Text)!;
                if(error==null) Assert.False(result.IsError == true,json.ToJsonString()); else { Assert.True(result.IsError); Assert.Equal(error,json["code"]!.GetValue<string>()); }
                return json;
            }
        }
        finally { main.OpenPropertiesWindow?.CloseResolved(); main.Close(); }
    }
    private static void CheckNativeCorners(Window window)
    {
        if (!OperatingSystem.IsWindowsVersionAtLeast(10,0,22000)) return;
        nint handle = new WindowInteropHelper(window).Handle;
        Assert.Equal(0, DwmGetWindowAttribute(handle,33,out int preference,sizeof(int)));
        Assert.Equal(2,preference);
        Assert.Equal(0,DwmIsCompositionEnabled(out bool composed));
        if (!composed) return;
        nint region = CreateRectRgn(0,0,0,0);
        Assert.NotEqual(0,region);
        try { Assert.Equal(0,GetWindowRgn(handle,region)); }
        finally { DeleteObject(region); }
    }
    [DllImport("dwmapi.dll")] private static extern int DwmGetWindowAttribute(nint handle,int attribute,out int value,int size);
    [DllImport("dwmapi.dll")] private static extern int DwmSetWindowAttribute(nint handle,int attribute,ref int value,int size);
    [DllImport("dwmapi.dll")] private static extern int DwmIsCompositionEnabled([MarshalAs(UnmanagedType.Bool)] out bool enabled);
    [DllImport("user32.dll")] private static extern int GetWindowRgn(nint handle,nint region);
    [DllImport("user32.dll")] private static extern nint SendMessage(nint handle,uint message,nint wParam,nint lParam);
    [DllImport("gdi32.dll")] private static extern nint CreateRectRgn(int left,int top,int right,int bottom);
    [DllImport("gdi32.dll")] private static extern bool DeleteObject(nint handle);
}
