using System.Numerics;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using Recoil.Zbd.Core.Sources;
using Recoil.Zbd.Desktop;
using Xunit;

namespace Recoil.Zbd.Desktop.Tests;

/// <summary>
/// The Add model dialog of a large project: the model list is virtualized and says when a filter matches more models than it
/// shows, and the animation definitions for a name are looked up once typing pauses (off the dispatcher, superseded lookups
/// canceled) and paged/filtered without losing any identity; at most 64 files can be selected. A longer list starts
/// unchecked, as zstudio_source_world_add_model refuses to add it whole.
/// </summary>
internal static class SourceModelDialogChecks
{
    internal static async Task Run()
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(60)); var token = deadline.Token;
        var owner = new Window { Left = -12000, Width = 300, Height = 200, ShowInTaskbar = false }; owner.Show();
        SourceModelDialog? dialog = null;
        try
        {
            SourceModelChoice[] models = [.. Enumerable.Range(0, 2500).Select(i => new SourceModelChoice($"data/m{1 + i % 3}/models/model{i:D4}.gltf", $"data/m{1 + i % 3}/models", $"model{i:D4}"))];
            // Thousands of definition files bind "tank", each listed by many missions; a few bind "turret".
            SourceDefinitionFile[] many = [.. Enumerable.Range(0, 3000).Select(i => new SourceDefinitionFile($"data/common/zrdr/enemies/tank{i:D4}.zad", ["tank_die", "tank_fire"], [.. Enumerable.Range(2, 20).Select(m => "m" + m)]))];
            SourceDefinitionFile[] few = [.. Enumerable.Range(0, 3).Select(i => new SourceDefinitionFile($"data/common/zrdr/turret{i}.zad", ["turret_die"], ["m2"]))];
            List<string> lookups = [];
            dialog = new SourceModelDialog(owner, "m1", models, new HashSet<string>(), Vector3.Zero, (root, t) =>
            {
                lookups.Add(root);
                return Task.Run(() => (IReadOnlyList<SourceDefinitionFile>)(root == "tank" ? many : root == "turret" ? few : []), t);
            }, _ => null);
            dialog.Show();
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);

            // At most 2000 rows, of which only the visible ones have controls; the note says how many the filter matches.
            var list = Field<ListBox>("list"); var listNote = Field<TextBlock>("listNote");
            Assert.Equal(SourceModelDialog.MaximumListedModels, list.Items.Count);
            Assert.Equal($"Showing {2000:N0} of {2500:N0} models; type part of a path to find the others.", listNote.Text);
            int realized = Enumerable.Range(0, list.Items.Count).Count(i => list.ItemContainerGenerator.ContainerFromIndex(i) != null);
            Assert.InRange(realized, 1, 64);
            // A row shows the model's name and folder as literal text, with its path for tooltips and automation.
            var first = (ListBoxItem)list.ItemContainerGenerator.ContainerFromIndex(0);
            Assert.Equal(models[0].Path, System.Windows.Automation.AutomationProperties.GetName(first));
            Assert.Equal(models[0].Path, first.ToolTip);
            Assert.Equal("model0000   (data/m1/models)", Texts(first).Single());
            Field<TextBox>("filter").Text = "model24";
            Assert.Equal(100, list.Items.Count); Assert.Equal(Visibility.Collapsed, listNote.Visibility);
            Field<TextBox>("filter").Text = "";
            Assert.Equal(Visibility.Visible, listNote.Visibility);

            // Typing a name looks it up once, when typing pauses.
            var name = Field<TextBox>("name"); var note = Field<TextBlock>("animationNote"); var animations = Field<StackPanel>("animations");
            foreach (string text in new[] { "t", "ta", "tan", "tank" }) name.Text = text;
            while (!note.Text.Contains($"{3000:N0} ", StringComparison.Ordinal)) await Task.Delay(20, token);
            Assert.Equal(["tank"], lookups);
            Assert.Contains($"filter or browse all files and choose up to {SourceWorlds.MaximumDefinitionChoices}", note.Text);
            var boxes = animations.Children.OfType<CheckBox>().ToArray();
            Assert.Equal(SourceWorlds.MaximumDefinitionChoices, boxes.Length);
            Assert.Equal(many.Take(SourceWorlds.MaximumDefinitionChoices).Select(f => f.Path), boxes.Select(b => (string)b.Tag));
            Assert.All(boxes, b => Assert.False(b.IsChecked));
            // Each row names a bounded number of the missions that list the file.
            Assert.EndsWith("(used by m2, m3, m4, m5, m6, m7, m8, m9 and 12 more)", ((TextBlock)boxes[0].Content).Text);
            // Every candidate remains reachable, with selected identities retained while browsing other pages.
            var definitionFilter = Field<TextBox>("animationFilter");
            definitionFilter.Text = "tank2999";
            var lastDefinition = Assert.Single(animations.Children.OfType<CheckBox>()); lastDefinition.IsChecked = true;
            Assert.Equal(many[^1].Path, lastDefinition.Tag);
            definitionFilter.Text = "";
            Assert.Equal(64, animations.Children.Count);
            foreach (var box in animations.Children.OfType<CheckBox>().Take(63)) box.IsChecked = true;
            var refused = animations.Children.OfType<CheckBox>().Last(); refused.IsChecked = true;
            Assert.False(refused.IsChecked); Assert.Contains("at most 64", Field<TextBlock>("error").Text);
            Field<Button>("animationNext").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Assert.Equal(many[64].Path, animations.Children.OfType<CheckBox>().First().Tag);
            definitionFilter.Text = "tank2999";
            Assert.True(Assert.Single(animations.Children.OfType<CheckBox>()).IsChecked);
            var chosen = Field<HashSet<string>>("chosenAnimations");
            Assert.Equal(64, chosen.Count); Assert.Contains(many[^1].Path, chosen);

            // A list within the bound starts checked, as the MCP default adds it whole.
            name.Text = "turret";
            while (!note.Text.Contains("turret", StringComparison.Ordinal) || note.Text.Contains('…')) await Task.Delay(20, token);
            Assert.Equal(["tank", "turret"], lookups);
            boxes = animations.Children.OfType<CheckBox>().ToArray();
            Assert.Equal(3, boxes.Length); Assert.All(boxes, b => Assert.True(b.IsChecked));
            Assert.DoesNotContain(many[^1].Path, chosen);
            Assert.Equal("Other missions list these animation definitions for turret. Checked files are added to m1's animation list:", note.Text);

            // Choosing a model enables Add, and a narrower filter that still lists it keeps the choice.
            list.SelectedIndex = 0;
            Assert.True(Field<Button>("add").IsEnabled);
            Field<TextBox>("filter").Text = "model000";
            Assert.Equal(10, list.Items.Count);
            Assert.Equal(models[0], typeof(SourceModelDialog).GetProperty("Model", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(dialog));
            await AcceptanceRaces(owner, models.Take(2).ToArray(), few);
            TerrainChooser(owner);

            T Field<T>(string field) => (T)typeof(SourceModelDialog).GetField(field, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(dialog)!;
            static IEnumerable<string> Texts(DependencyObject parent)
            {
                for (int i = 0; i < System.Windows.Media.VisualTreeHelper.GetChildrenCount(parent); i++)
                {
                    var child = System.Windows.Media.VisualTreeHelper.GetChild(parent, i);
                    if (child is TextBlock text) yield return text.Text;
                    foreach (string nested in Texts(child)) yield return nested;
                }
            }
        }
        finally
        {
            dialog?.Close();
            owner.Close();
        }
    }

    private static async Task AcceptanceRaces(Window owner, SourceModelChoice[] models, SourceDefinitionFile[] definitions)
    {
        foreach (string outcome in new[] { "renamed", "renamed-back", "failed", "canceled", "current", "closed" })
        {
            TaskCompletionSource<IReadOnlyList<SourceDefinitionFile>> completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
            List<string> requested = [];
            var dialog = new SourceModelDialog(owner, "m1", models, new HashSet<string>(), Vector3.Zero,
                (name, _) => { requested.Add(name); return completion.Task; }, _ => null);
            try
            {
                dialog.Show(); await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
                T Field<T>(string name) => (T)typeof(SourceModelDialog).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(dialog)!;
                Field<ListBox>("list").SelectedIndex = 0;
                var name = Field<TextBox>("name"); name.Text = "A";
                var timer = Field<DispatcherTimer>("lookup"); timer.Stop();
                var accepting = (Task)typeof(SourceModelDialog).GetMethod("AcceptAsync", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(dialog, [])!;
                Assert.Equal(["A"], requested);
                Assert.False(Field<Button>("add").IsEnabled);
                if (outcome is "renamed" or "renamed-back")
                {
                    name.Text = "B";
                    if (outcome == "renamed-back") name.Text = "A";
                    timer.Stop();
                }
                if (outcome == "closed") dialog.Close();
                if (outcome == "failed") completion.SetException(new System.IO.IOException("Lookup unavailable"));
                else if (outcome == "canceled") completion.SetException(new OperationCanceledException());
                else completion.SetResult(definitions);
                await accepting;
                Assert.Null(dialog.Result);
                Assert.Equal(outcome != "closed", dialog.IsVisible);
                if (outcome == "current")
                {
                    Assert.Equal(definitions.Select(d => d.Path), Field<StackPanel>("animations").Children.OfType<CheckBox>().Select(b => (string)b.Tag));
                    Assert.Contains("Review", Field<TextBlock>("error").Text);
                }
                else Assert.Empty(Field<StackPanel>("animations").Children.OfType<CheckBox>());
                if (outcome is "failed" or "canceled") Assert.Contains("retry", Field<TextBlock>("error").Text);
            }
            finally { dialog.Close(); }
        }
    }

    private static void TerrainChooser(Window owner)
    {
        string[] nodes = [.. Enumerable.Range(0, 5000).Select(i => $"mesh_{i:D4}")];
        var dialog = new TerrainCreateDialog("surfaces.gltf", nodes) { Owner = owner };
        try
        {
            dialog.Show();
            T Field<T>(string name) => (T)typeof(TerrainCreateDialog).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(dialog)!;
            var list = Field<StackPanel>("list"); var filter = Field<TextBox>("filter");
            Assert.Equal(TerrainCreateDialog.PageSize, list.Children.Count);
            // A candidate after the old 256 cutoff is reachable directly, and literal underscores stay visible.
            filter.Text = nodes[^1];
            var last = Assert.Single(list.Children.OfType<CheckBox>());
            Assert.Equal(nodes[^1], ((TextBlock)last.Content).Text);
            last.IsChecked = true;
            Assert.Equal([nodes[^1]], dialog.Chosen);
            filter.Text = "";
            Assert.Equal(TerrainCreateDialog.PageSize, list.Children.Count);
            // Selections survive filter/page changes; the recipe limit applies to selected rows, not available rows.
            while (dialog.Chosen.Count < 256)
            {
                foreach (var box in list.Children.OfType<CheckBox>().ToArray())
                {
                    if (dialog.Chosen.Count == 256) break;
                    box.IsChecked = true;
                }
                if (dialog.Chosen.Count < 256) Field<Button>("next").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                Assert.InRange(list.Children.Count, 1, TerrainCreateDialog.PageSize);
            }
            filter.Text = nodes[1000];
            var extra = Assert.Single(list.Children.OfType<CheckBox>()); extra.IsChecked = true;
            Assert.False(extra.IsChecked); Assert.Equal(256, dialog.Chosen.Count);
            Assert.Contains("at most 256", Field<TextBlock>("error").Text);
            filter.Text = nodes[^1];
            last = Assert.Single(list.Children.OfType<CheckBox>()); Assert.True(last.IsChecked); last.IsChecked = false;
            filter.Text = nodes[1000]; extra = Assert.Single(list.Children.OfType<CheckBox>()); extra.IsChecked = true;
            Assert.True(extra.IsChecked); Assert.Contains(nodes[1000], dialog.Chosen); Assert.DoesNotContain(nodes[^1], dialog.Chosen);
        }
        finally { dialog.Close(); }
    }
}
