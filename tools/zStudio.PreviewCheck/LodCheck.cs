using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using zStudio.Core;
using zStudio.Desktop;
using zStudio.Rendering;

internal static class LodCheck
{
    public static int Run(string root)
    {
        int exit = 0; root = Path.GetFullPath(root);
        string settings = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "zStudio", "settings.json");
        byte[]? originalSettings = File.Exists(settings) ? File.ReadAllBytes(settings) : null;
        var app = new App { ShutdownMode = ShutdownMode.OnExplicitShutdown, ProcessCommandLine = false }; app.InitializeComponent();
        app.Startup += async (_, _) =>
        {
            var window = (MainWindow)app.MainWindow;
            try
            {
                await window.ViewModel.OpenRootAsync(root);
                var document = await window.ViewModel.OpenFileAsync(Path.Combine(root, "m1", "gamez.zbd")) ?? throw new InvalidDataException("No scene.");
                var data = document.Document.Scene!; var lods = new SceneLods(data);
                var world = document.Assets.First(a => a.Record.Kind == AssetKind.World);
                var model = document.Assets.First(a => a.Record.Kind == AssetKind.Model && SceneLods.PreviewRoot(data, a.Record) is int r && lods.Count([r]) > 1);
                foreach (var asset in new[] { world, model })
                {
                    document.SelectedAsset = asset; await Wait();
                    var picker = (ComboBox)window.FindName("LodCombo");
                    if (picker.SelectedIndex != 0 || picker.Items.Count < 2) throw new InvalidDataException("Highest LOD not selected or variants missing.");
                    int first = Check(0); picker.SelectedIndex = 1; await Wait(); int lower = Check(1);
                    if (first == lower) throw new InvalidDataException("LOD geometry unchanged.");
                    picker.SelectedIndex = 0; await Wait(); if (Check(0) != first) throw new InvalidDataException("Highest LOD did not restore.");
                    Console.WriteLine($"{asset.Name}: {picker.Items.Count} LOD choices, {first} → {lower} → {first} triangles; default/selection/restoration verified.");
                    int Check(int level)
                    {
                        var preview = (SceneViewport)((ContentControl)window.FindName("SceneHost")).Content;
                        var displayed = preview.PreviewScene ?? data;
                        var expected = SceneBuilder.ForAsset(displayed, asset.Record, level);
                        // Whole world shows the mission scene; with its Horizon toggle off (the default) the horizon subtrees are left out.
                        HashSet<int> backdrop = [];
                        bool horizon = ((System.Windows.Controls.Primitives.ToggleButton)window.FindName("BackdropEnabled")).IsChecked == true;
                        Stack<int> pending = new(asset.Record.Kind == AssetKind.World && !horizon ? MissionSceneContext.FindHorizons(displayed).Select(h => h.Root) : []);
                        while (pending.TryPop(out int i)) { if (!backdrop.Add(i)) continue; foreach (int child in SceneBuilder.Children(displayed.Nodes[i])) pending.Push(child); }
                        int target = expected.Placements.Where(p => !backdrop.Contains(p.NodeIndex)).Sum(p => GeometryBuilder.Build(displayed.Models[p.ModelIndex]).Sum(part => part.Indices.Length/3));
                        int drawn = preview.RenderedTriangleCount;
                        if (drawn != target) throw new InvalidDataException($"Rendered LOD triangle count {drawn} != {target}."); return drawn;
                    }
                }
                async Task Wait()
                {
                    // A LOD change refreshes the shown scene in place: wait for that refresh, not for a placeholder.
                    await Task.Delay(50);
                    await ((Task)typeof(MainWindow).GetField("previewWork", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(window)!).WaitAsync(TimeSpan.FromSeconds(60));
                    var empty = (TextBlock)window.FindName("EmptyPreview");
                    if (empty.Visibility == Visibility.Visible) throw new InvalidDataException(empty.Text);
                }
            }
            catch (Exception ex) { Console.Error.WriteLine(ex); exit = 1; }
            finally { window.Close(); app.Shutdown(exit); }
        };
        try { app.Run(); }
        finally { if (originalSettings != null) File.WriteAllBytes(settings, originalSettings); else if (File.Exists(settings)) File.Delete(settings); }
        return exit;
    }
}
