using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Recoil.Zbd.Core;
using Recoil.Zbd.Desktop;
using Recoil.Zbd.Rendering;

internal static class SceneTreeLayoutCheck
{
    internal static async Task Run(MainWindow window, string root, string output, CancellationToken token)
    {
        await window.ViewModel.OpenRootAsync(Path.GetFullPath(root));
        var doc = await window.ViewModel.OpenFileAsync(Path.Combine(Path.GetFullPath(root), "m1", "gamez.zbd")) ?? throw new InvalidDataException("Map unavailable");
        doc.SelectedAsset = doc.Assets.First(a => a.Record.Kind == AssetKind.World);
        await Wait(() => ((ContentControl)window.FindName("SceneHost")).Content is SceneViewport { Mission: not null } && ((FrameworkElement)window.FindName("EmptyPreview")).Visibility == Visibility.Collapsed);
        window.SelectNavigatorSection(3);
        var tree = (TreeView)window.FindName("DocumentSceneTree");
        await Wait(() => tree.IsVisible && tree.Items.Count > 0);
        var treeRoot = (SceneTreeItem)tree.Items[0];
        var branch = treeRoot.Children.First(r => r.ChildCount > 0 && r.Problem == null);
        branch.IsExpanded = true;
        var leaf = branch.Children.First(r => r.Problem == null);
        typeof(MainWindow).GetMethod("RevealSceneNode", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(window, [leaf.Index!.Value]);
        foreach (string theme in new[] { "Dark", "Light" })
        foreach (int width in new[] { 1600, 1080 })
        {
#pragma warning disable WPF0001
            Application.Current.ThemeMode = theme == "Dark" ? ThemeMode.Dark : ThemeMode.Light;
#pragma warning restore WPF0001
            window.Width = width;
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            await Task.Delay(300, token); window.UpdateLayout();
            Require(ReferenceEquals(treeRoot, tree.Items[0]) && branch.IsExpanded && leaf.IsSelected, "Theme/width lost hierarchy state");
            Require(tree.ActualWidth >= 200 && tree.ActualHeight > 100, "Tree is too small to use");
            var rootContainer = tree.ItemContainerGenerator.ContainerFromItem(treeRoot) as TreeViewItem;
            var branchContainer = rootContainer?.ItemContainerGenerator.ContainerFromItem(branch) as TreeViewItem;
            var leafContainer = branchContainer?.ItemContainerGenerator.ContainerFromItem(leaf) as TreeViewItem;
            Require(leafContainer is { IsVisible: true, IsSelected: true }, "Selected hierarchy descendant was not realized");
            Require(leafContainer!.TranslatePoint(new(), tree).X > branchContainer!.TranslatePoint(new(), tree).X, "Parent/child indentation is missing");
            var dpi = VisualTreeHelper.GetDpi(tree);
            var bitmap = new RenderTargetBitmap((int)Math.Ceiling(tree.ActualWidth * dpi.DpiScaleX), (int)Math.Ceiling(tree.ActualHeight * dpi.DpiScaleY), dpi.PixelsPerInchX, dpi.PixelsPerInchY, PixelFormats.Pbgra32);
            // The tree inherits the window's backdrop. Composite that behind
            // its transparent Fluent surfaces for an independently viewable PNG.
            var drawing = new DrawingVisual();
            using (var context = drawing.RenderOpen())
            {
                var area = new Rect(0, 0, tree.ActualWidth, tree.ActualHeight);
                context.DrawRectangle(window.Background, null, area);
                context.DrawRectangle(new VisualBrush(tree), null, area);
            }
            bitmap.Render(drawing);
            var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
            using var stream = File.Create(Path.Combine(output, $"tree-{theme}-{width}.png")); encoder.Save(stream);
        }
        // Reveal a far-away child through actual virtualization, then switch away
        // and back: these are presentation changes, without new source navigation.
        var collapseRoot = (TreeViewItem)tree.ItemContainerGenerator.ContainerFromItem(treeRoot);
        var collapseBranch = (TreeViewItem)collapseRoot.ItemContainerGenerator.ContainerFromItem(branch);
        collapseBranch.IsExpanded = false;
        await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
        Require(leaf.IsSelected && !branch.IsExpanded, "Collapsing a selected descendant changed source selection or reopened its branch");
        collapseBranch.IsSelected = true;
        await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
        Require(branch.IsSelected && !leaf.IsSelected, "Intentional selection of a collapsed ancestor was ignored");
        var distant = treeRoot.Children.Last(r => r.Problem == null);
        typeof(MainWindow).GetMethod("RevealSceneNode", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(window, [distant.Index!.Value]);
        await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
        Require(ReferenceEquals(tree.SelectedItem, distant), "Virtualized distant node was not selected");
        window.SelectNavigatorSection(1); window.SelectNavigatorSection(3);
        await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
        Require(ReferenceEquals(tree.SelectedItem, distant) && !doc.IsDirty, "Tab realization changed selection or source");
        Console.WriteLine("PASS: real mission hierarchy in dark/light themes at 1600/1080 DIP, parent indentation, selected descendant realization, distant virtualized reveal and passive tab restoration.");
        async Task Wait(Func<bool> ready) { while (!ready()) await Task.Delay(100, token); }
    }
    private static void Require(bool value, string message) { if (!value) throw new InvalidDataException(message); }
}
