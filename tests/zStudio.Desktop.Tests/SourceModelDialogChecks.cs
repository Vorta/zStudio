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
/// canceled) and listed as zstudio_source_world_definitions lists them, at most 64 in path order with the total; a longer
/// list starts unchecked, as zstudio_source_world_add_model refuses to add it whole.
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
            Assert.Equal($"{3000:N0} definition files of other missions list animations for tank; the first {SourceWorlds.MaximumDefinitionChoices} in path order are listed, unchecked. Checked files are added to m1's animation list; files not listed are not added.", note.Text);
            var boxes = animations.Children.OfType<CheckBox>().ToArray();
            Assert.Equal(SourceWorlds.MaximumDefinitionChoices, boxes.Length);
            Assert.Equal(many.Take(SourceWorlds.MaximumDefinitionChoices).Select(f => f.Path), boxes.Select(b => (string)b.Tag));
            Assert.All(boxes, b => Assert.False(b.IsChecked));
            // Each row names a bounded number of the missions that list the file.
            Assert.EndsWith("(used by m2, m3, m4, m5, m6, m7, m8, m9 and 12 more)", ((TextBlock)boxes[0].Content).Text);

            // A list within the bound starts checked, as the MCP default adds it whole.
            name.Text = "turret";
            while (!note.Text.Contains("turret", StringComparison.Ordinal) || note.Text.Contains('…')) await Task.Delay(20, token);
            Assert.Equal(["tank", "turret"], lookups);
            boxes = animations.Children.OfType<CheckBox>().ToArray();
            Assert.Equal(3, boxes.Length); Assert.All(boxes, b => Assert.True(b.IsChecked));
            Assert.Equal("Other missions list these animation definitions for turret. Checked files are added to m1's animation list:", note.Text);

            // Choosing a model enables Add, and a narrower filter that still lists it keeps the choice.
            list.SelectedIndex = 0;
            Assert.True(Field<Button>("add").IsEnabled);
            Field<TextBox>("filter").Text = "model000";
            Assert.Equal(10, list.Items.Count);
            Assert.Equal(models[0], typeof(SourceModelDialog).GetProperty("Model", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(dialog));

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
}
