using System.IO;
using System.IO.Pipes;
using System.Reflection;
using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Controls;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using Recoil.Zbd.Core.Formats;
using Recoil.Zbd.Core.Sources;
using Recoil.Zbd.Desktop;
using Recoil.Zbd.Mcp;
using Recoil.Zbd.Tests;
using Xunit;

namespace Recoil.Zbd.Desktop.Tests;

/// <summary>Source projects through the real named-pipe MCP connection and the shared GUI operations.</summary>
internal static class SourceProjectMcpChecks
{
    internal static async Task Run()
    {
        using var fixture = new SourceFixture();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(60)); var token = deadline.Token;
        var main = new MainWindow { Left = -12000, ShowInTaskbar = false }; main.Show();
        try
        {
            await using var host = new LocalMcpHost(main.Commands, "test");
            await using var pipe = new NamedPipeClientStream(".", host.Instance.Pipe, PipeDirection.InOut, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly); await pipe.ConnectAsync(token);
            await using var client = await McpClient.CreateAsync(new StreamClientTransport(pipe, pipe), cancellationToken: token);

            await WelcomeInitializeChecks(main, fixture, token);
            // Open under Work with source project refuses a folder that is not a project; the same check is open_root's project.
            var notProject = await Job("open_root", new() { ["path"] = fixture.Corpus, ["project"] = true }, "failed");
            Assert.Equal("not_project", notProject["code"]!.GetValue<string>());
            Assert.Equal(Path.GetFullPath(Path.Combine(fixture.Root, "initialized")), Path.GetFullPath(main.ViewModel.RootPath!));
            var reopened = await Job("open_root", new() { ["path"] = Path.Combine(fixture.Root, "initialized"), ["project"] = true });
            Assert.Equal(Path.GetFullPath(Path.Combine(fixture.Root, "initialized")), Path.GetFullPath(reopened["RootPath"]!.GetValue<string>()));
            Assert.Equal(["MCP integration…", "Compare worlds…", "-", "Export all ZBD files…", "Export ZBD file", "Check source project", "Resolve interrupted save…", "Build profile", "Open mission world", "Add model to world…", "Edit in Blender…", "Update from Blender export…", "Edit map zones…", "Create terrain…", "Convert to editable terrain…", "-", "Validate source file on disk", "Reload current file", "Cancel export or validation"], ToolsMenu(main));
            await Job("open_root", new() { ["path"] = fixture.Corpus });
            Assert.Equal(["MCP integration…", "Compare worlds…", "-", "Validate source file on disk", "Reload current file", "Cancel export or validation"], ToolsMenu(main));

            var failed = await Job("source_export", new(), "failed"); Assert.Equal("no_project", failed["code"]!.GetValue<string>());
            var inside = await Job("source_reconstruct", new() { ["source"] = fixture.Corpus, ["destination"] = Path.Combine(fixture.Corpus, "project") }, "failed");
            Assert.Equal("invalid_argument", inside["code"]!.GetValue<string>());
            // A relative folder would land beside zStudio itself, which a new build replaces.
            var relative = await Job("source_reconstruct", new() { ["source"] = fixture.Corpus, ["destination"] = "project" }, "failed");
            Assert.Contains("full paths", relative["message"]!.GetValue<string>());
            Assert.False(Directory.Exists(Path.Combine(AppContext.BaseDirectory, "project")));

            var built = await Job("source_reconstruct", new() { ["source"] = fixture.Corpus, ["destination"] = fixture.Project });
            Assert.True(built["opened"]!.GetValue<bool>());
            Assert.Equal(1, built["families"]!["scripts"]!.GetValue<int>()); Assert.Equal(3, built["families"]!["sounds"]!.GetValue<int>());
            Assert.Equal("other.bin", built["notReconstructed"]![0]!.GetValue<string>()); Assert.Equal(1, built["noteCount"]!.GetValue<int>());
            Assert.Equal(Path.GetFullPath(fixture.Project), Path.GetFullPath(main.ViewModel.RootPath!));
            Assert.Contains(main.ViewModel.Files, f => f.RelativePath == Path.Combine("data", "m1", "zrdr", "ai.zrd"));
            var status = await Job("source_status", new() { ["query"] = "m1", ["limit"] = 10 });
            var mission = status["outputs"]!["items"]!.AsArray().Single()!;
            Assert.Equal("m1/zrdr.zbd", mission["path"]!.GetValue<string>()); Assert.Equal(2, mission["inputCount"]!.GetValue<int>());
            Assert.Equal(2, status["families"]!["archive"]!.GetValue<int>()); Assert.Equal(3, status["families"]!["sounds"]!.GetValue<int>());

            // Export commands appear once a source project is the root; the one-file menu lists every buildable game file.
            main.SourceProfilesReading = ct => Assert.False(main.Dispatcher.CheckAccess());
            typeof(MainWindow).GetMethod("ToolsMenuOpened", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(main, [main, new RoutedEventArgs(MenuItem.SubmenuOpenedEvent, main)]);
            Assert.Equal(Visibility.Visible, ((MenuItem)main.FindName("ExportSourceMenu")).Visibility);
            var single = (MenuItem)main.FindName("ExportSourceFileMenu");
            while (single.Items.Count != 6) { token.ThrowIfCancellationRequested(); await Task.Delay(10, token); }
            Assert.Equal(["zrdr.zbd", "interp.zbd", "soundsh.zbd", "soundsm.zbd", "soundsl.zbd", "m1/zrdr.zbd"], single.Items.Cast<MenuItem>().Select(i => ((TextBlock)i.Header).Text));
            CheckOutputMenuPages();
            // Build profiles: the GUI's choice and source_status list the same profiles; source_export names the one it built with.
            var profiles = (MenuItem)main.FindName("SourceProfileMenu");
            while (profiles.Items.Count != 2) await Task.Delay(10, token);
            Assert.Equal(["modern (default) · experimental", "original"], profiles.Items.Cast<MenuItem>().Select(i => ((TextBlock)i.Header).Text));
            Assert.True(((MenuItem)profiles.Items[0]).IsChecked);
            Assert.Equal(["modern", "original"], status["profiles"]!.AsArray().Select(p => p!["name"]!.GetValue<string>()));
            // Per-mission packs: the original profile's texture8 is built only for m6, as shipped.
            var shippedPacks = status["profiles"]!.AsArray().Single(p => p!["name"]!.GetValue<string>() == "original")!["texturePacks"]!.AsArray();
            Assert.Equal(["m6"], shippedPacks.Single(t => t!["file"]!.GetValue<string>() == "texture8.zbd")!["missions"]!.AsArray().Select(m => m!.GetValue<string>()));
            Assert.Null(shippedPacks.Single(t => t!["file"]!.GetValue<string>() == "rtexture4.zbd")!["missions"]);
            Assert.Equal("modern", status["profile"]!.GetValue<string>());
            // Profile discovery is a cancellable worker; state requests and Tools Cancel work while it is held.
            TaskCompletionSource reading = new(TaskCreationOptions.RunContinuationsAsynchronously);
            main.SourceProfilesReading = ct =>
            {
                Assert.False(main.Dispatcher.CheckAccess()); reading.TrySetResult();
                Assert.True(ct.WaitHandle.WaitOne(TimeSpan.FromSeconds(20))); ct.ThrowIfCancellationRequested();
            };
            var cancelledChoice = Job("source_profile", new() { ["profile"] = "original" }, "canceled");
            await reading.Task.WaitAsync(token);
            await Call("state", new());
            ((MenuItem)main.FindName("CancelOperationItem")).RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
            await cancelledChoice;
            Assert.True(((MenuItem)profiles.Items[0]).IsChecked);
            main.SourceProfilesReading = ct => Assert.False(main.Dispatcher.CheckAccess());
            var chosen = await Job("source_profile", new() { ["profile"] = "original" });
            Assert.Equal("original", chosen["profile"]!.GetValue<string>());
            Assert.True(((MenuItem)profiles.Items[1]).IsChecked);
            Assert.False(((MenuItem)profiles.Items[0]).IsChecked);
            var badChoice = await Job("source_profile", new() { ["profile"] = "missing" }, "failed");
            Assert.Equal("invalid_argument", badChoice["code"]!.GetValue<string>());
            Assert.Equal("original", (await Job("source_status", new()))["profile"]!.GetValue<string>());
            await Job("source_profile", new());
            Assert.True(((MenuItem)profiles.Items[0]).IsChecked);
            // A menu scan that began before a new selection cannot overwrite its checked item afterward.
            using (ManualResetEventSlim release = new())
            {
                TaskCompletionSource oldMenuEntered = new(TaskCreationOptions.RunContinuationsAsynchronously);
                main.SourceProfilesReading = ct => { Assert.False(main.Dispatcher.CheckAccess()); oldMenuEntered.TrySetResult(); Assert.True(release.Wait(TimeSpan.FromSeconds(20), ct)); };
                var oldMenu = (Task)typeof(MainWindow).GetMethod("FillSourceProfileMenuAsync", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(main, [fixture.Project])!;
                await oldMenuEntered.Task.WaitAsync(token);
                main.SourceProfilesReading = ct => Assert.False(main.Dispatcher.CheckAccess());
                try { await Job("source_profile", new() { ["profile"] = "original" }); }
                finally { release.Set(); }
                await oldMenu;
                Assert.True(((MenuItem)profiles.Items[1]).IsChecked);
            }
            await Job("source_profile", new());
            ((MenuItem)profiles.Items[1]).RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
            while (!((MenuItem)profiles.Items[1]).IsChecked) await Task.Delay(10, token);
            Assert.Equal("original", ((ValueTuple<string, string>?)typeof(MainWindow).GetField("sourceProfileChoice", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(main))!.Value.Item2);
            var badProfile = await Job("source_export", new() { ["profile"] = "missing" }, "failed");
            Assert.Equal("invalid_argument", badProfile["code"]!.GetValue<string>());
            var relativeExport = await Job("source_export", new() { ["destination"] = "game" }, "failed");
            Assert.Contains("full path", relativeExport["message"]!.GetValue<string>());

            // A reconstructed .zrd opens in the shared ZRD editor and saves text.
            string source = Path.Combine(fixture.Project, "data", "m1", "zrdr", "ai.zrd");
            await Job("open_document", new() { ["path"] = source });
            var doc = main.ViewModel.Documents.Single(d => d.Path.Equals(source, StringComparison.OrdinalIgnoreCase));
            Assert.Equal("zrd-text", doc.Document.SourceSyntax);
            var members = await Call("archive_members", new() { ["document"] = doc.SessionId.ToString() });
            Guid member = Guid.Parse(members["members"]!["items"]![0]!["member"]!.GetValue<string>());
            var gravity = doc.ResourceEdits!.Tree(doc.ResourceEdits.Member(member), token).Children[1].Children[0];
            await Job("zrd_edit", new() { ["document"] = doc.SessionId.ToString(), ["revision"] = doc.Revision, ["action"] = "set", ["member"] = member.ToString(), ["node"] = gravity.Id.ToString(), ["value"] = "-1.5" });
            var unsaved = await Job("source_export", new(), "failed"); Assert.Equal("unsaved_changes", unsaved["code"]!.GetValue<string>());
            await Job("save_document", new() { ["document"] = doc.SessionId.ToString(), ["revision"] = doc.Revision });
            Assert.Contains("GRAVITY ( -1.5 )", await File.ReadAllTextAsync(source, token));

            var check = await Job("source_export", new());
            Assert.False(check["written"]!.GetValue<bool>()); Assert.Equal(6, check["built"]!.GetValue<int>()); Assert.Equal(0, check["failed"]!.GetValue<int>());
            Assert.Equal("original", check["profile"]!.GetValue<string>());
            string exported = Path.Combine(fixture.Root, "zbd");
            var written = await Job("source_export", new() { ["destination"] = exported, ["outputs"] = new[] { "m1/zrdr.zbd" } });
            Assert.True(written["written"]!.GetValue<bool>()); Assert.Equal("m1/zrdr.zbd", written["outputs"]![0]!["path"]!.GetValue<string>());
            Assert.Equal([Path.Combine(exported, "m1", "zrdr.zbd")], Directory.GetFiles(exported, "*", SearchOption.AllDirectories));
            var archive = FormatRegistry.Default.OpenBytes("zrdr.zbd", await File.ReadAllBytesAsync(Path.Combine(exported, "m1", "zrdr.zbd"), token), token: token);
            var ai = archive.Assets.Single(a => a.Name == "ai.zrd");
            Assert.Equal(-1.5f, BitConverter.UInt32BitsToSingle(ZrdDecoder.Read(archive.Slice(ai.Offset, ai.Length), token).Children[1].Children[0].Bits));
            // Existing game files are replaced only on request; unknown outputs are refused.
            var exists = await Job("source_export", new() { ["destination"] = exported, ["outputs"] = new[] { "m1/zrdr.zbd" } }, "failed");
            Assert.Equal("io_failed", exists["code"]!.GetValue<string>());
            await Job("source_export", new() { ["destination"] = exported, ["outputs"] = new[] { "m1/zrdr.zbd" }, ["overwrite"] = true });
            var unknown = await Job("source_export", new() { ["outputs"] = new[] { "m9/zrdr.zbd" } }, "failed");
            Assert.Equal("invalid_argument", unknown["code"]!.GetValue<string>());

            // An export whose workspace is replaced never reports into the new workspace.
            string elsewhere = Path.Combine(fixture.Root, "elsewhere"); Directory.CreateDirectory(elsewhere);
            main.SourceExportFinishing = () => main.ViewModel.OpenRootAsync(elsewhere, token);
            var superseded = await Job("source_export", new(), "failed");
            main.SourceExportFinishing = null;
            Assert.Equal("context_changed", superseded["code"]!.GetValue<string>());
            Assert.DoesNotContain("Checked", main.ViewModel.Status);
            Assert.Equal(Path.GetFullPath(elsewhere), Path.GetFullPath(main.ViewModel.RootPath!));
            // An export that already wrote its files says so when the workspace changes before it reports.
            await main.ViewModel.OpenRootAsync(fixture.Project, token);
            string late = Path.Combine(fixture.Root, "late");
            main.SourceExportFinishing = () => main.ViewModel.OpenRootAsync(elsewhere, token);
            var written2 = await Job("source_export", new() { ["destination"] = late, ["outputs"] = new[] { "m1/zrdr.zbd" } }, "failed");
            main.SourceExportFinishing = null;
            Assert.Equal("context_changed", written2["code"]!.GetValue<string>()); Assert.Contains("wrote 1 game file", written2["message"]!.GetValue<string>());
            Assert.True(File.Exists(Path.Combine(late, "m1", "zrdr.zbd")));
            // An unsupported world mutation is visible in both the protocol report and the shared GUI Problems list.
            using (var worldFixture = new SourceWorldFixture())
            {
                string script = Path.Combine(worldFixture.Project, "gamegen", "m1.gs");
                string original = await File.ReadAllTextAsync(script, token);
                await File.WriteAllTextAsync(script, original.Replace("# no vehicles", "Object3DSetPriority 2", StringComparison.Ordinal), token);
                await Job("open_root", new() { ["path"] = worldFixture.Project, ["project"] = true });
                var warning = await Job("source_export", new() { ["outputs"] = new[] { "m1/gamez.zbd" } });
                Assert.Equal(0, warning["failed"]!.GetValue<int>());
                Assert.Contains(warning["outputs"]![0]!["warnings"]!.AsArray(), w => w!.GetValue<string>().Contains("Object3DSetPriority", StringComparison.Ordinal));
                Assert.Contains(main.ViewModel.Problems, p => p.Message.Contains("Object3DSetPriority", StringComparison.Ordinal));
                // Distinct valid interpreter lookups produce more diagnostics than the GUI preview keeps.
                string misses = string.Join("\r\n", Enumerable.Range(0, 70).Select(i => $"FindNode absent_warning_{i:D2}"));
                await File.WriteAllTextAsync(script, original.Replace("# no vehicles", misses, StringComparison.Ordinal), token);
                warning = await Job("source_export", new() { ["outputs"] = new[] { "m1/gamez.zbd" } });
                int warningCount = warning["outputs"]![0]!["warningCount"]!.GetValue<int>();
                Assert.True(warningCount >= 70); Assert.True(warning["outputs"]![0]!["warningsTruncated"]!.GetValue<bool>());
                string omission = $"Showing 64 of {warningCount} warnings; {warningCount - 64} more not shown.";
                Assert.Contains(main.ViewModel.Problems, p => p.File == worldFixture.Project && p.Message.Contains(omission, StringComparison.Ordinal));
                await Job("source_world_open", new() { ["mission"] = "m1" });
                var world = main.ViewModel.Documents.Single(d => d.SourceWorld != null);
                int buildWarnings = world.SourceBuild!.Outputs.Single(o => o.Path == "m1/gamez.zbd").Warnings.Count;
                Assert.True(buildWarnings >= 70);
                string buildOmission = $"Showing 64 of {buildWarnings} warnings; {buildWarnings - 64} more not shown.";
                Assert.Contains(main.ViewModel.Problems, p => p.File == script && p.Message.Contains(buildOmission, StringComparison.Ordinal));
                var problems = await Call("problems", new() { ["query"] = "more not shown" });
                Assert.Contains(problems["items"]!.AsArray(), p => p!["File"]!.GetValue<string>() == script && p["Message"]!.GetValue<string>().Contains(buildOmission, StringComparison.Ordinal));
                await Job("open_root", new() { ["path"] = elsewhere });
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
        finally { foreach (var doc in main.ViewModel.Documents.ToArray()) main.ViewModel.CloseResolved(doc); main.Close(); }
    }

    /// <summary>
    /// The welcome screen's Work with source project → Initialize dialog: Initialize and open waits for both folders, a refused
    /// folder is reported in the dialog, which stays open, and a valid pair reconstructs the project, which then opens.
    /// </summary>
    private static async Task WelcomeInitializeChecks(MainWindow main, SourceFixture fixture, CancellationToken token)
    {
        Assert.False(main.ViewModel.HasRoot);
        Assert.Equal(Visibility.Visible, ((FrameworkElement)main.FindName("WelcomeChoices")).Visibility);
        foreach (string name in new[] { "WelcomeOpen", "WelcomeInitializeProject", "WelcomeOpenProject" }) Assert.True(((Button)main.FindName(name)).IsVisible, name);
        // Initialize is the GUI's only reconstruction: Tools has no reconstruct command, and no separator without a folder;
        // comparing two world files needs no folder.
        Assert.Equal(["MCP integration…", "Compare worlds…"], ToolsMenu(main));

        // Cancel before starting closes the dialog without a result.
        var canceled = main.CreateInitializeDialog();
        bool? canceledResult = Modal(canceled, async () => { await Loaded(canceled); Click(Find<Button>(canceled, b => b.Content as string == "Cancel")); });
        Assert.NotEqual(true, canceledResult); Assert.Null(canceled.Report);

        string project = Path.Combine(fixture.Root, "initialized");
        var dialog = main.CreateInitializeDialog();
        string? refusal = null; bool disabledUntilBoth = false;
        bool? result = Modal(dialog, async () =>
        {
            await Loaded(dialog);
            var retail = Find<TextBox>(dialog, t => System.Windows.Automation.AutomationProperties.GetName(t) == "Retail ZBD folder");
            var destination = Find<TextBox>(dialog, t => System.Windows.Automation.AutomationProperties.GetName(t) == "Source project folder");
            var start = Find<Button>(dialog, b => b.Content as string == "Initialize and open");
            disabledUntilBoth = !start.IsEnabled;
            retail.Text = fixture.Corpus;
            disabledUntilBoth &= !start.IsEnabled;
            // A project folder inside the retail folder is refused; the dialog stays open with the reason.
            destination.Text = Path.Combine(fixture.Corpus, "project");
            Assert.True(start.IsEnabled);
            Click(start);
            while (!start.IsEnabled) { token.ThrowIfCancellationRequested(); await Task.Delay(10, token); }
            var error = Find<TextBlock>(dialog, t => t.Foreground == System.Windows.Media.Brushes.IndianRed);
            refusal = error.Text;
            Assert.True(dialog.IsVisible);
            destination.Text = project;
            Assert.Equal("", error.Text);
            Click(start);
            // The dialog closes once the project is open; a refusal now would leave it open.
            while (dialog.IsVisible) { token.ThrowIfCancellationRequested(); if (error.Text.Length > 0) throw new InvalidOperationException(error.Text); await Task.Delay(10, token); }
        });
        Assert.True(disabledUntilBoth);
        Assert.False(string.IsNullOrEmpty(refusal));
        Assert.False(Directory.Exists(Path.Combine(fixture.Corpus, "project")));
        Assert.True(result); Assert.NotNull(dialog.Report);
        Assert.True(File.Exists(Path.Combine(project, "data", "m1", "zrdr", "ai.zrd")));
        // The welcome screen opens the project once the dialog has closed.
        await main.OpenInitializedProjectAsync(dialog.Report!);
        Assert.Equal(Path.GetFullPath(project), Path.GetFullPath(main.ViewModel.RootPath!));
        Assert.Contains(main.ViewModel.Files, f => f.RelativePath == Path.Combine("data", "m1", "zrdr", "ai.zrd"));
        // Opening the project cleared Problems; the reconstruction's notes are listed after it.
        Assert.NotEmpty(dialog.Report!.Notes);
        foreach (string note in dialog.Report.Notes) Assert.Contains(main.ViewModel.Problems, p => p.Severity == "Warning" && p.Message == note);
        Assert.Equal(Visibility.Collapsed, ((FrameworkElement)main.FindName("WelcomeChoices")).Visibility);

        static async Task Loaded(Window window) { while (!window.IsLoaded) await Task.Delay(10); }
        static void Click(Button button) => button.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
        // A modal dialog runs a nested message loop: the queued driver runs inside it until the dialog closes.
        bool? Modal(Window window, Func<Task> drive)
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
    }
    // Consequential regression: a valid source plan can hold thousands of outputs. Keep WPF publication bounded
    // while the final output remains selectable through the same callback, including stale retained controls.
    private static void CheckOutputMenuPages()
    {
        var plan = Enumerable.Range(1, 129).Select(i => new SourceOutputPlan($"m{i}/texture_max.zbd", "textures", [])).ToArray();
        MenuItem menu = new(); bool current = true; string? selected = null;
        MainWindow.FillSourceOutputMenu(menu, plan, () => current, path => selected = path);
        MenuItem Item(string name) => menu.Items.Cast<MenuItem>().Single(i => System.Windows.Automation.AutomationProperties.GetName(i) == name);
        void Click(MenuItem item) => item.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
        Assert.InRange(menu.Items.Count, 1, 67);
        var first = Item("m1/texture_max.zbd"); var oldNext = Item("Next outputs");
        Assert.IsType<TextBlock>(first.Header); // Underscores remain literal, and the full path is its identity.
        Click(first); Assert.Equal(plan[0].Path, selected);
        Assert.True(oldNext.StaysOpenOnClick);
        Click(oldNext);
        Assert.InRange(menu.Items.Count, 1, 67);
        Assert.DoesNotContain(first, menu.Items.Cast<MenuItem>());
        Assert.NotNull(Item("m65/texture_max.zbd"));
        Click(Item("Next outputs"));
        Assert.InRange(menu.Items.Count, 1, 67);
        var last = Item("m129/texture_max.zbd"); Click(last); Assert.Equal(plan[^1].Path, selected);
        Assert.DoesNotContain(menu.Items.Cast<MenuItem>(), i => System.Windows.Automation.AutomationProperties.GetName(i) == "Next outputs");
        Click(Item("Previous outputs"));
        Assert.NotNull(Item("m65/texture_max.zbd"));
        var retained = menu.Items.Cast<MenuItem>().ToArray();
        current = false;
        Click(oldNext); Click(first);
        Assert.Equal(retained, menu.Items.Cast<MenuItem>());
        Assert.Equal(plan[^1].Path, selected);
    }

    /// <summary>The Tools menu's visible entries as the menu shows them once opened ("-" for a separator).</summary>
    internal static string[] ToolsMenu(MainWindow main)
    {
        var tools = (MenuItem)main.FindName("ToolsMenu");
        typeof(MainWindow).GetMethod("ToolsMenuOpened", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(main, [tools, new RoutedEventArgs(MenuItem.SubmenuOpenedEvent, tools)]);
        return tools.Items.OfType<Control>().Where(c => c.Visibility == Visibility.Visible).Select(c => c is Separator ? "-" : (string)((MenuItem)c).Header).ToArray();
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
