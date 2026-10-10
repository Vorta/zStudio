using System.IO;
using System.Numerics;
using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using Recoil.Zbd.Core.Sources;
using Recoil.Zbd.Core.Worlds;
using Recoil.Zbd.Desktop;
using Recoil.Zbd.Tests;
using Xunit;

namespace Recoil.Zbd.Desktop.Tests;

internal static class SourceDecisionDialogChecks
{
    internal static async Task Run()
    {
        using var fixture = new SourceWorldFixture();
        var token = TestContext.Current.CancellationToken;
        var owner = new MainWindow { Left = -12000, ShowInTaskbar = false }; owner.Show();
        try
        {
            string Nested(int count) => string.Join('/', Enumerable.Repeat(new string('q', 190), count));
            string model = "data/" + Nested(5) + "/model.gltf";
            const string empty = "{\"asset\":{\"version\":\"2.0\"},\"nodes\":[{\"name\":\"external\"}],\"scenes\":[{\"nodes\":[0]}],\"scene\":0}";
            fixture.Write(model, empty);
            await owner.ViewModel.OpenRootAsync(fixture.Project, token);
            await Job("source_world_open", new() { ["mission"] = "m1" });
            var checkout = await Job("source_blender_checkout", new() { ["model"] = model });
            string outbox = checkout["outbox"]!.GetValue<string>();
            JsonArray buffers = [];
            for (int i = 0; i < 5; i++)
            {
                string name = $"buffer{i}.bin"; await File.WriteAllBytesAsync(Path.Combine(outbox, name), new byte[] { 1, 2, 3, 4 }, token);
                buffers.Add(new JsonObject { ["uri"] = name, ["byteLength"] = 4 });
            }
            var exported = JsonNode.Parse(empty)!; exported["buffers"] = buffers;
            await File.WriteAllTextAsync(Path.Combine(outbox, "model.gltf"), exported.ToJsonString(), token);
            var document = owner.ViewModel.Documents.Single();
            await Job("source_blender_update", new() { ["document"] = document.SessionId.ToString(), ["revision"] = document.Revision, ["checkout"] = checkout["id"]!.GetValue<string>() });
            document = owner.ViewModel.Documents.Single();
            var workspace = document.SourceWorld!.Workspace;
            Assert.Equal(6, workspace.DirtyFiles.Count); Assert.All(workspace.DirtyFiles, path => Assert.True(path.Length > 950));
            // An older export beside it: Update from Blender export offers both as source_blender_checkouts lists them, the newest chosen.
            string older = Path.Combine(outbox, "older", "model.gltf"); Directory.CreateDirectory(Path.GetDirectoryName(older)!);
            await File.WriteAllTextAsync(older, empty, token); File.SetLastWriteTimeUtc(older, new DateTime(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc));
            var exports = SourceBlender.CheckoutExports(fixture.Project, token);

            string terrainPath = "data/" + Nested(21) + "/surface.gltf";
            ModelBuilder builder = new();
            builder.Add(new([Vector3.Zero, Vector3.UnitX, Vector3.UnitZ], [Vector2.Zero, Vector2.UnitX, Vector2.UnitY], [], [], new() { Flags = 0x1FF }));
            var terrain = WorldGltf.Export([new("land", WorldNodeClass.Object3D) { Model = builder.Finish() }], 0xFF,
                new() { Texture = _ => throw new InvalidOperationException("The terrain fixture has no textures."), Token = token }).Write("surface.bin", token);
            fixture.Write(terrainPath, terrain.Json); fixture.Write(terrainPath[..^5] + ".bin", terrain.Binary);
            var nodes = SourceTerrain.MeshNodes(workspace, terrainPath, token);
            Assert.Equal(["land"], nodes); Assert.True(terrainPath.Length > 4000);

            foreach (var available in new[] { new Size(1280, 720), new Size(1920, 1080), new Size(320, 360) })
            {
                string? chosen = null;
                var unsaved = owner.CreateUnsavedDialog(document, true, "Save", value => chosen = value, available);
                await Check(unsaved, available, () =>
                {
                    var expander = Descendants(unsaved).OfType<Expander>().Single(); expander.IsExpanded = true; expander.UpdateLayout();
                    Assert.Equal(workspace.DirtyFiles.Take(6), Descendants(expander).OfType<TextBox>().Select(t => t.Text));
                });
                ButtonOf(unsaved, "Cancel").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                Assert.Equal("Cancel", chosen); Assert.True(workspace.IsDirty);

                var terrainDialog = new TerrainCreateDialog(terrainPath, nodes, available) { Owner = owner };
                await Check(terrainDialog, available, () =>
                {
                    Assert.Equal(terrainPath, Descendants(terrainDialog).OfType<TextBox>().Single(t => System.Windows.Automation.AutomationProperties.GetName(t) == "Terrain source model path").Text);
                    Assert.Equal(nodes, terrainDialog.Chosen);
                    Assert.True(ButtonOf(terrainDialog, "Create").IsEnabled);
                });
                terrainDialog.Close();

                var blender = new BlenderUpdateDialog(exports, available) { Owner = owner };
                await Check(blender, available, () =>
                {
                    var choices = Descendants(blender).OfType<RadioButton>().ToArray();
                    Assert.Equal(2, choices.Length);
                    Assert.Equal((checkout["id"]!.GetValue<string>(), "model.gltf"), (blender.Chosen.Checkout.Id, blender.Chosen.Export.Relative));
                    choices.Single(c => c.IsChecked != true).IsChecked = true;
                    Assert.Equal("older/model.gltf", blender.Chosen.Export.Relative);
                });
                blender.Close();

                TaskCompletionSource<SourceReconstructionReport> finished = new(TaskCreationOptions.RunContinuationsAsynchronously);
                TaskCompletionSource started = new(TaskCreationOptions.RunContinuationsAsynchronously);
                CancellationToken initializing = default;
                var initialize = new SourceInitializeDialog(owner, fixture.Project, (_, _, _, ct) => { initializing = ct; started.TrySetResult(); return finished.Task; }, available);
                await Check(initialize, available);
                string destination = fixture.Path("data/" + Nested(21) + "/initialized");
                Descendants(initialize).OfType<TextBox>().Single(t => System.Windows.Automation.AutomationProperties.GetName(t) == "Source project folder").Text = destination;
                ButtonOf(initialize, "Initialize and open").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                await started.Task.WaitAsync(token);
                ButtonOf(initialize, "Cancel").RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); Assert.True(initializing.IsCancellationRequested);
                finished.SetResult(new(destination, 1, new Dictionary<string, int>(), [], []));
                var error = Descendants(initialize).OfType<TextBlock>().Single(t => t.Foreground == Brushes.IndianRed);
                while (error.Text.Length == 0) { await Task.Delay(10, token); }
                Assert.Contains(destination, error.Text); Assert.Null(initialize.Report); Assert.True(initialize.IsVisible);
                await Check(initialize, available); Assert.True(ButtonOf(initialize, "Cancel").IsEnabled);
                ButtonOf(initialize, "Cancel").RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); Assert.False(initialize.IsVisible);
            }
            // Short ordinary controls retain their decisions and single selected terrain identity.
            workspace.Undo();
            workspace.Apply("Short source edit", [("data/note.zrd", new byte[] { (byte)'(', (byte)')' })], token);
            string? shortChoice = null;
            var shortUnsaved = owner.CreateUnsavedDialog(document, true, "Save", choice => shortChoice = choice, new(1280, 720));
            await Check(shortUnsaved, new(1280, 720));
            Assert.False(Assert.Single(((Grid)shortUnsaved.Content).Children.OfType<ScrollViewer>()).ScrollableHeight > 0);
            ButtonOf(shortUnsaved, "Cancel").RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); Assert.Equal("Cancel", shortChoice); Assert.True(workspace.IsDirty);
            var ordinary = new TerrainCreateDialog("surface.gltf", ["land"], new Size(1280, 720)) { Owner = owner };
            // The recipe defaults to the one source_terrain_create makes without a recipe argument.
            await Check(ordinary, new(1280, 720)); Assert.Equal(["land"], ordinary.Chosen); Assert.Equal("surface.terrain.json", ordinary.Recipe); ordinary.Close();
            var shortInitialize = new SourceInitializeDialog(owner, null, (_, _, _, _) => throw new InvalidOperationException("Should not initialize."), new(1280, 720));
            await Check(shortInitialize, new(1280, 720)); Assert.False(ButtonOf(shortInitialize, "Initialize and open").IsEnabled); shortInitialize.Close();

            async Task<JsonNode> Job(string name, JsonObject arguments)
            {
                var job = (await owner.Commands.ExecuteAsync("zstudio_" + name, arguments, token)).Data;
                string id = job["id"]!.GetValue<string>();
                while (job["State"]!.GetValue<string>() is "queued" or "running")
                { await Task.Delay(10, token); job = (await owner.Commands.ExecuteAsync("zstudio_operation", new JsonObject { ["id"] = id }, token)).Data; }
                Assert.True(job["State"]!.GetValue<string>() == "completed", job.ToJsonString()); return job["result"]!;
            }
        }
        finally
        {
            foreach (Window dialog in owner.OwnedWindows.Cast<Window>().ToArray()) dialog.Close();
            foreach (var document in owner.ViewModel.Documents.ToArray()) owner.ViewModel.CloseResolved(document);
            owner.Close();
        }
    }

    private static async Task Check(Window dialog, Size available, Action? inspect = null)
    {
        if (!dialog.IsVisible)
        {
            dialog.WindowStartupLocation = WindowStartupLocation.Manual; dialog.Left = -12000; dialog.Top = 0; dialog.ShowActivated = false; dialog.Show();
        }
        await Dispatcher.Yield(DispatcherPriority.ContextIdle); inspect?.Invoke(); dialog.UpdateLayout();
        Assert.InRange(dialog.ActualWidth, 1, available.Width); Assert.InRange(dialog.ActualHeight, 1, available.Height);
        var layout = Assert.IsType<Grid>(dialog.Content);
        var scroll = Assert.Single(layout.Children.OfType<ScrollViewer>());
        var actions = Assert.Single(layout.Children.OfType<WrapPanel>());
        Assert.True(scroll.ViewportHeight > 0);
        Rect[] Bounds() => [.. actions.Children.OfType<Button>().Select(b => b.TransformToAncestor(layout).TransformBounds(new Rect(b.RenderSize)))];
        Rect[] before = Bounds();
        foreach (var bounds in before)
            Assert.True(bounds.Width > 0 && bounds.Height > 0 && bounds.Left >= -0.5 && bounds.Top >= -0.5 && bounds.Right <= layout.ActualWidth + 0.5 && bounds.Bottom <= layout.ActualHeight + 0.5,
                $"Action {bounds} exceeds viewport {layout.RenderSize}.");
        scroll.ScrollToBottom(); scroll.UpdateLayout(); await Dispatcher.Yield(DispatcherPriority.ContextIdle);
        Assert.Equal(before, Bounds()); Assert.InRange(scroll.VerticalOffset, Math.Max(0, scroll.ScrollableHeight - 0.5), scroll.ScrollableHeight + 0.5);
        scroll.ScrollToTop();
    }

    private static Button ButtonOf(Window dialog, string label) => Descendants(dialog).OfType<Button>().Single(b => b.Content as string == label);
    private static IEnumerable<DependencyObject> Descendants(DependencyObject root)
    {
        yield return root;
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
            foreach (var child in Descendants(VisualTreeHelper.GetChild(root, i))) yield return child;
    }
}
