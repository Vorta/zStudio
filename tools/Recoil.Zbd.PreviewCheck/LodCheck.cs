using System.IO;
using System.Windows;
using System.Windows.Controls;
using HelixToolkit.Wpf.SharpDX;
using Recoil.Zbd.Core;
using Recoil.Zbd.Desktop;
using Recoil.Zbd.Rendering;

internal static class LodCheck
{
    public static int Run(string root)
    {
        int exit = 0; root = Path.GetFullPath(root);
        string settings = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "RecoilZbdStudio", "settings.json");
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
                        // Scene overview excludes the optional horizon backdrop.
                        HashSet<int> backdrop = []; Stack<int> pending = new(displayed.Nodes.Where(n => n.Name.Equals("horizon", StringComparison.OrdinalIgnoreCase)).Select(n => n.Index));
                        while (pending.TryPop(out int i)) { if (!backdrop.Add(i)) continue; foreach (int child in SceneBuilder.Children(displayed.Nodes[i])) pending.Push(child); }
                        int target = expected.Placements.Where(p => asset.Record.Kind != AssetKind.World || !backdrop.Contains(p.NodeIndex)).Sum(p => GeometryBuilder.Build(data.Models[p.ModelIndex]).Sum(part => part.Indices.Length/3));
                        var viewport = (Viewport3DX)((SceneViewport)((ContentControl)window.FindName("SceneHost")).Content).Content;
                        var meshes = viewport.Items.OfType<MeshGeometryModel3D>().Concat(viewport.Items.OfType<SortingGroupModel3D>().SelectMany(g => g.Children.OfType<MeshGeometryModel3D>()));
                        int drawn = meshes.Where(m => m.Visibility == Visibility.Visible).Sum(m => m.Geometry!.Indices!.Count / 3 * (m.Instances?.Count ?? 1));
                        if (drawn != target) throw new InvalidDataException($"Rendered LOD triangle count {drawn} != {target}."); return drawn;
                    }
                }
                async Task Wait()
                {
                    var empty = (TextBlock)window.FindName("EmptyPreview"); using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(60));
                    do { await Task.Delay(50, timeout.Token); } while (empty.Visibility == Visibility.Visible && empty.Text == "Loading preview…");
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
