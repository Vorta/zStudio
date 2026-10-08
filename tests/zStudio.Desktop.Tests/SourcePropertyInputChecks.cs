using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Recoil.Zbd.Automation;
using Recoil.Zbd.Core.Terrain;
using Recoil.Zbd.Core.Worlds;
using Recoil.Zbd.Desktop;
using Xunit;

namespace Recoil.Zbd.Desktop.Tests;

// Runs in the existing STA Application. These are GUI drafts and their resolve_drafts consumer, not direct typed-array tools.
internal static class SourcePropertyInputChecks
{
    internal static async Task Run()
    {
        await SourceCommand();
        await TerrainList("Surfaces");
        await TerrainList("Defaults: Zones");
    }

    private static async Task SourceCommand()
    {
        const string name = "WorldSetFogColor";
        var origin = new WorldNodeProvenance();
        origin.Writers[name] = new("gamegen/m1.gs", 1, name, [name, "0", "0", "0"], ["0", "0", "0"]);
        var state = new SourceObjectState(0, "world", "World", null, 0, origin, "gamegen/m1.gs", []);
        byte[] bytes = Encoding.Latin1.GetBytes("WorldSetFogColor 0 0 0\n"), original = bytes.ToArray();
        int calls = 0; List<byte[]> history = []; string[]? accepted = null;
        using var editor = new SourceObjectPropertiesEditor(state, _ => Task.CompletedTask, (_, _) => Task.CompletedTask, (command, args) =>
        {
            calls++; accepted = args.ToArray(); history.Add(bytes);
            bytes = Encoding.Latin1.GetBytes(command + " " + string.Join(" ", args) + "\n");
            return Task.CompletedTask;
        });
        var window = Show(editor);
        try
        {
            var box = Box(editor, name);
            await Reject(editor, box, string.Concat(Enumerable.Repeat("0 ", 500_000)), () => { Assert.Equal(original, bytes); Assert.Empty(history); Assert.Equal(0, calls); });
            await Reject(editor, box, new string('0', 1_000_000), () => { Assert.Equal(original, bytes); Assert.Empty(history); Assert.Equal(0, calls); });
            string first = new('0', 63); first += "1";
            box.Text = first + ",\t0, 0";
            await editor.ResolveAutomationDraftsAsync(editor.DraftToken, true);
            Assert.NotNull(accepted);
            Assert.Equal([first, "0", "0"], accepted); Assert.Single(history); Assert.Equal(1, calls);
        }
        finally { window.Close(); }
    }

    private static async Task TerrainList(string field)
    {
        string[] ids = Enumerable.Range(0, 256).Select(i => $"s{i:D3}" + new string('x', 28)).ToArray();
        TerrainRecipe recipe = new(1, ids.Select((id, i) => new TerrainSurface(id, "surfaces.gltf", "node" + i, TerrainAttributes.None)).ToArray(),
            TerrainAttributes.None, [new("paint", [], null, TerrainAttributes.None)]);
        byte[] bytes = recipe.Write(), original = bytes.ToArray(); List<byte[]> history = []; int calls = 0;
        void Publish(TerrainRecipe next)
        {
            byte[] checkedBytes = next.Write(); history.Add(bytes); recipe = next; bytes = checkedBytes;
        }
        TerrainEditorActions actions = new((surface, change) => { calls++; Assert.Null(surface); Publish(recipe with { Defaults = change(recipe.Defaults) }); return Task.CompletedTask; },
            (name, change) => { calls++; Assert.Equal("paint", name); Publish(recipe with { Regions = [change(recipe.Regions[0])] }); return Task.CompletedTask; },
            _ => Task.CompletedTask, _ => Task.CompletedTask, (_, _) => Task.CompletedTask, _ => { }, _ => { });
        using var editor = new TerrainPropertiesEditor("data/m1/models/terrain.terrain.json", recipe, null, null, "paint", null, actions);
        var window = Show(editor);
        try
        {
            var box = Box(editor, field);
            await Reject(editor, box, string.Concat(Enumerable.Repeat("0 ", 500_000)), () => { Assert.Equal(original, bytes); Assert.Empty(history); Assert.Equal(0, calls); });
            await Reject(editor, box, new string('0', 1_000_000), () => { Assert.Equal(original, bytes); Assert.Empty(history); Assert.Equal(0, calls); });
            box.Text = field == "Surfaces" ? string.Join(", ", ids) : "001, 2 254";
            await editor.ResolveAutomationDraftsAsync(editor.DraftToken, true);
            Assert.Single(history); Assert.Equal(1, calls);
            if (field == "Surfaces") Assert.Equal(ids, recipe.Regions[0].Surfaces);
            else Assert.Equal(new byte[] { 1, 2, 254 }, recipe.Defaults.Zones!.Ids);
            // Successful source edits rebuild the real Properties editor. Recreate it against the accepted recipe
            // before testing removal, so the comparison uses the newly committed source snapshot too.
            using var refreshed = new TerrainPropertiesEditor("data/m1/models/terrain.terrain.json", recipe, null, null, "paint", null, actions);
            window.Content = refreshed; window.UpdateLayout();
            Box(refreshed, field).Text = field == "Surfaces" ? " , " : " aNy ";
            await refreshed.ResolveAutomationDraftsAsync(refreshed.DraftToken, true);
            Assert.Equal(2, history.Count); Assert.Equal(2, calls);
            if (field == "Surfaces") Assert.Empty(recipe.Regions[0].Surfaces);
            else Assert.True(recipe.Defaults.Zones!.IsAny);
        }
        finally { window.Close(); }
    }

    private static async Task Reject(SourcePropertiesEditor editor, TextBox box, string invalid, Action unchanged)
    {
        box.Text = invalid; box.CaretIndex = 123;
        // Enter reaches the actual async GUI callback, which completes its bounded refusal synchronously.
        long before = GC.GetAllocatedBytesForCurrentThread();
        box.RaiseEvent(new KeyEventArgs(Keyboard.PrimaryDevice, PresentationSource.FromVisual(box), Environment.TickCount, Key.Enter) { RoutedEvent = Keyboard.PreviewKeyDownEvent });
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
        Assert.InRange(allocated, 0, 256 * 1024);
        Assert.True(editor.HasPendingDrafts); Assert.Equal(invalid, box.Text); Assert.Equal(123, box.CaretIndex); unchanged();
        var pending = JsonSerializer.SerializeToNode(editor.DescribeDrafts())!["fields"]!.AsArray();
        Assert.Equal(invalid, Assert.Single(pending)!["text"]!.GetValue<string>());
        // This is the exact retained-draft path used by resolve_drafts target=properties. Token generation is outside
        // the callback allocation measurement because it intentionally hashes the complete retained draft identity.
        string token = editor.DraftToken;
        var error = await Assert.ThrowsAsync<StudioCommandException>(() => editor.ResolveAutomationDraftsAsync(token, true));
        Assert.Equal("invalid_draft", error.Code); Assert.Equal(invalid, box.Text); unchanged();
        await editor.ResolveAutomationDraftsAsync(editor.DraftToken, false);
        Assert.False(editor.HasPendingDrafts); unchanged();
    }

    private static Window Show(SourcePropertiesEditor editor)
    {
        Window window = new() { Content = editor, Width = 650, Height = 650, Left = -12000, ShowInTaskbar = false };
        window.Show(); window.UpdateLayout(); return window;
    }
    private static TextBox Box(DependencyObject root, string label) => Descendants(root).OfType<TextBox>().Single(box => AutomationProperties.GetName(box) == label);
    private static IEnumerable<DependencyObject> Descendants(DependencyObject root)
    {
        yield return root;
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
            foreach (var child in Descendants(VisualTreeHelper.GetChild(root, i))) yield return child;
    }
}
