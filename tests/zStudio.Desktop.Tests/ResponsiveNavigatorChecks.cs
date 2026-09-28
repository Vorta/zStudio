using System.IO;
using System.IO.Pipes;
using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using Recoil.Zbd.Core;
using Recoil.Zbd.Core.Animation;
using Recoil.Zbd.Desktop;
using Recoil.Zbd.Mcp;
using Xunit;

namespace Recoil.Zbd.Desktop.Tests;

internal static class ResponsiveNavigatorChecks
{
    internal static async Task Run(Application app)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(40));
        string root = Path.Combine(Path.GetTempPath(),"zstudio-responsive-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        for (int i = 0; i < 65; i++) File.WriteAllBytes(Path.Combine(root,$"file{i:D2}.zbd"),[1,0,0,0,0,0,0,0]);
        // Hosted Windows runners can cap native window width below the split
        // breakpoint. Lay out the real content at explicit DIP widths inside a
        // deliberately smaller HWND so every runner exercises both modes.
        var main = new MainWindow { Left = -12000, ShowInTaskbar = false, MinWidth = 0, MaxWidth = 800, Width = 800, Height = 750 };
        var content = (FrameworkElement)main.Content;
        content.Width = 1080; content.Height = 750;
        main.ViewModel.Settings.Workspace = new();
        main.Show(); await Idle();
        try
        {
            Assert.Equal("hidden",main.NavigatorMode);
            await main.ViewModel.OpenRootAsync(root,deadline.Token);
            await Idle(); Assert.Equal("tabbed",main.NavigatorMode);
            var tree = (TreeView)main.FindName("FileTree");
            var tabs = (TabControl)main.FindName("NavigationTabs");
            var rootItem = (TreeViewItem)tree.ItemContainerGenerator.ContainerFromIndex(0);
            rootItem.IsExpanded = true; await Idle();
            var first = (TreeViewItem)rootItem.ItemContainerGenerator.ContainerFromIndex(0);
            first.IsSelected = true;
            var scroll = Descendants(tree).OfType<ScrollViewer>().First();
            scroll.ScrollToVerticalOffset(15); await Idle();
            double offset = scroll.VerticalOffset; Assert.True(offset > 0);
            object selection = tree.SelectedItem;

            await using var host = new LocalMcpHost(main.Commands,"test");
            await using var pipe = new NamedPipeClientStream(".",host.Instance.Pipe,PipeDirection.InOut,PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
            await pipe.ConnectAsync(deadline.Token);
            await using var client = await McpClient.CreateAsync(new StreamClientTransport(pipe,pipe),cancellationToken:deadline.Token);
            await Resize(1180,"tabbed"); // Below the entry threshold.
            await Resize(1210,"split");
            Assert.Same(tree,((ContentControl)main.FindName("DetachedFilesContent")).Content);
            rootItem = (TreeViewItem)tree.ItemContainerGenerator.ContainerFromIndex(0);
            Assert.True(rootItem.IsExpanded); Assert.Same(selection,tree.SelectedItem);
            Assert.InRange(Descendants(tree).OfType<ScrollViewer>().First().VerticalOffset,offset - .1,offset + .1);
            Assert.Equal(Visibility.Collapsed,((TabItem)main.FindName("FilesTab")).Visibility);
            Assert.Equal(2,tabs.SelectedIndex); Assert.Equal(0,main.ViewModel.Settings.GetWorkspace().BrowserTab);
            await Resize(1180,"split"); // Hysteresis retains split.
            await Resize(1140,"tabbed"); Assert.Equal(0,tabs.SelectedIndex);
            await Resize(1180,"tabbed");
            await Resize(1600,"split");
            await Changes(new() { ["navigatorTab"] = 0 });
            Assert.Equal(0,main.ViewModel.Settings.GetWorkspace().BrowserTab);
            rootItem = (TreeViewItem)tree.ItemContainerGenerator.ContainerFromIndex(0);
            first = (TreeViewItem)rootItem.ItemContainerGenerator.ContainerFromIndex(0);
            bool activated = main.Activate(), focused = first.Focus(); await Idle(); Assert.True(tree.IsKeyboardFocusWithin, FocusState());
            await Resize(1080,"tabbed"); Assert.True(tree.IsKeyboardFocusWithin, FocusState());
            await Resize(1600,"split"); Assert.True(tree.IsKeyboardFocusWithin, FocusState());
            string FocusState() => $"activate={activated}; focus={focused}; active={main.IsActive}; focused={Keyboard.FocusedElement}; logical={FocusManager.GetFocusedElement(main)}; tree visible={tree.IsVisible}";
            await Changes(new() { ["navigatorTab"] = 2 });
            ((TextBox)main.FindName("GlobalSearch")).Text = "file";
            await Resize(1080,"tabbed"); Assert.Equal(2,tabs.SelectedIndex);
            await Resize(1600,"split"); Assert.Equal("file",((TextBox)main.FindName("GlobalSearch")).Text);
            await Changes(new() { ["filesWidth"] = 280, ["navigatorWidth"] = 320 });
            Assert.Equal(280,((ColumnDefinition)main.FindName("DetachedFilesColumn")).ActualWidth,1);
            Assert.Equal(320,((ColumnDefinition)main.FindName("NavigatorContentColumn")).ActualWidth,1);
            await Resize(1080,"tabbed"); await Resize(1600,"split");
            Assert.Equal(280,main.ViewModel.Settings.GetWorkspace().FilesWidth);
            Assert.Equal(320,main.ViewModel.Settings.GetWorkspace().NavigatorWidth);
            ((ColumnDefinition)main.FindName("DetachedFilesColumn")).Width = new(310);
            ((ColumnDefinition)main.FindName("NavigatorContentColumn")).Width = new(290);
            await Idle();
            ((GridSplitter)main.FindName("FilesSplitter")).RaiseEvent(new DragCompletedEventArgs(0,0,false) { RoutedEvent = Thumb.DragCompletedEvent });
            await Idle(); Assert.Equal(310,main.ViewModel.Settings.GetWorkspace().FilesWidth); Assert.Equal(290,main.ViewModel.Settings.GetWorkspace().NavigatorWidth);
            ((ColumnDefinition)main.FindName("FilesColumn")).Width = new(636);
            await Idle();
            ((GridSplitter)main.FindName("NavigatorSplitter")).RaiseEvent(new DragCompletedEventArgs(0,0,false) { RoutedEvent = Thumb.DragCompletedEvent });
            await Idle(); Assert.Equal(310,main.ViewModel.Settings.GetWorkspace().FilesWidth); Assert.Equal(320,main.ViewModel.Settings.GetWorkspace().NavigatorWidth);
            await Changes(new() { ["filesWidth"] = 280, ["navigatorWidth"] = 320 });
            ((FolderNode)tree.Items[0]).IsExpanded = false;
            await Resize(1080,"tabbed"); await Resize(1600,"split");
            Assert.False(((TreeViewItem)tree.ItemContainerGenerator.ContainerFromIndex(0)).IsExpanded);
            ((FolderNode)tree.Items[0]).IsExpanded = true; await Idle();
            await Changes(new() { ["navigatorTab"] = 3, ["filesWidth"] = 400 },error:"unavailable_tab");
            Assert.Equal(280,main.ViewModel.Settings.GetWorkspace().FilesWidth);
            await Changes(new() { ["navigator"] = false }); Assert.Equal("hidden",main.NavigatorMode);
            await Changes(new() { ["navigator"] = true }); Assert.Equal("split",main.NavigatorMode);
            await Changes(new() { ["preset"] = "Focus preview" }); Assert.Equal("hidden",main.NavigatorMode);
            await Changes(new() { ["preset"] = "Edit" }); Assert.Equal("split",main.NavigatorMode);

            // GUI opening still selects Assets even if it was already the visible
            // content tab while Files was the logical active section.
            await Changes(new() { ["navigatorTab"] = 0 });
            rootItem = (TreeViewItem)tree.ItemContainerGenerator.ContainerFromIndex(0);
            first = (TreeViewItem)rootItem.ItemContainerGenerator.ContainerFromIndex(0);
            first.IsSelected = true;
            tree.RaiseEvent(new KeyEventArgs(Keyboard.PrimaryDevice,PresentationSource.FromVisual(main)!,Environment.TickCount,Key.Enter) { RoutedEvent = Keyboard.KeyDownEvent });
            while (main.ViewModel.SelectedDocument == null) await Task.Delay(10,deadline.Token);
            await Idle();
            Assert.Equal(1,main.ViewModel.Settings.GetWorkspace().BrowserTab); Assert.Equal(1,tabs.SelectedIndex);
            var opened = main.ViewModel.SelectedDocument!;
            Assert.True(((FolderNode)selection).IsOpen);
            ((TextBox)main.FindName("AssetFilter")).Text = "partial filter";
            await Changes(new() { ["navigatorTab"] = 0 });
            await Resize(1080,"tabbed"); Assert.Equal(0,tabs.SelectedIndex);
            await Resize(1600,"split"); Assert.Equal(1,tabs.SelectedIndex);
            Assert.Equal("partial filter",((TextBox)main.FindName("AssetFilter")).Text);

            var package = new AnimationPackage { Prefix = new byte[72], Tail = [] };
            package.Entries.Add(new(new byte[308],0,0));
            var documentScene = new GameScene(); documentScene.Nodes.Add(new(0,"root","world",null,[],[],new(),new()));
            using var propertiesDoc = new DocumentModel(new ZbdDocument(Path.Combine(root,"properties.zbd"),new(0,DateTime.MinValue),
                new(FormatFamily.Animation,28,Recognition.Supported,"fixture"),ReadOnlyMemory<byte>.Empty) { Animations = package, Scene = documentScene });
            main.ViewModel.Documents.Add(propertiesDoc);
            main.ViewModel.SelectedDocument = propertiesDoc; await Idle();
            await Changes(new() { ["navigatorTab"] = 3 });
            await Resize(1080,"tabbed"); Assert.Equal(3,tabs.SelectedIndex);
            await Resize(1600,"split"); Assert.Equal(3,tabs.SelectedIndex);
            await Changes(new() { ["navigatorTab"] = 0 });
            await Resize(1080,"tabbed"); Assert.Equal(0,tabs.SelectedIndex);
            await Resize(1600,"split"); Assert.Equal(3,tabs.SelectedIndex);
            main.ViewModel.SelectedDocument = opened; await Idle();
            Assert.Equal(1,tabs.SelectedIndex); Assert.Equal(0,main.ViewModel.Settings.GetWorkspace().BrowserTab);
            main.OpenAnimationProperties(propertiesDoc,0,Guid.Empty,Guid.Empty);
            var form = main.OpenPropertiesWindow!.AnimationFields!;
            var input = Descendants(form).OfType<TextBox>().Single(t => AutomationProperties.GetName(t) == "Reset delay (s)");
            input.Text = "-";
            foreach (string theme in new[] { "Light","Dark","System" })
            foreach (string density in new[] { "Compact","Comfortable" })
            {
                await Changes(new() { ["theme"] = theme, ["density"] = density });
                await Resize(1080,"tabbed"); await Resize(1600,"split");
                Assert.Equal("-",input.Text); Assert.True(form.HasPendingDrafts);
                Assert.Same(form,main.OpenPropertiesWindow.AnimationFields);
                Assert.Same(opened,main.ViewModel.SelectedDocument);
                Assert.Same(tree,main.FindName("FileTree")); Assert.Same(selection,tree.SelectedItem);
            }
            await Changes(new() { ["resetLayout"] = true });
            Assert.Equal(0,main.ViewModel.Settings.GetWorkspace().BrowserTab);
            Assert.Equal(240,main.ViewModel.Settings.GetWorkspace().FilesWidth);
            Assert.Equal(294,main.ViewModel.Settings.GetWorkspace().NavigatorWidth);
            Assert.True(form.HasPendingDrafts); input.Text = "0";
            main.OpenPropertiesWindow.CloseResolved();
            main.ViewModel.CloseResolved(propertiesDoc);
            await main.ViewModel.CloseAsync(opened); await Idle();
            Assert.Equal(0,main.ViewModel.Settings.GetWorkspace().BrowserTab);
            Assert.Equal(2,tabs.SelectedIndex);
            Assert.Equal(Visibility.Collapsed,((TabItem)main.FindName("AssetsTab")).Visibility);
            Assert.DoesNotContain(app.Windows.Cast<Window>(),w => w.Title == "Resolve property input");

            async Task Resize(double width,string mode)
            {
                content.Width = width; await Idle();
                Assert.Equal(width,content.ActualWidth,1);
                Assert.True(main.ActualWidth <= 800);
                Assert.Equal(mode,main.NavigatorMode);
                Assert.Equal(mode,(await Changes(null))["navigatorMode"]!.GetValue<string>());
                if (mode == "split")
                {
                    var workbench = (FrameworkElement)main.FindName("Workbench");
                    double navigation = ((ColumnDefinition)main.FindName("FilesColumn")).ActualWidth;
                    Assert.True(workbench.ActualWidth - navigation - 6 >= 599.9);
                }
            }
            async Task<JsonNode> Changes(JsonObject? changes,string? error = null)
            {
                Dictionary<string,object?> args = new();
                if (changes != null) args["changes"] = changes;
                var response = await client.CallToolAsync("zstudio_workspace_view",args,cancellationToken:deadline.Token);
                var json = JsonNode.Parse(response.Content.OfType<TextContentBlock>().Single().Text)!;
                if (error == null) Assert.False(response.IsError == true,json.ToJsonString());
                else { Assert.True(response.IsError); Assert.Equal(error,json["code"]!.GetValue<string>()); }
                await Idle(); return json;
            }
        }
        finally
        {
            main.OpenPropertiesWindow?.CloseResolved();
            foreach (var doc in main.ViewModel.Documents.ToArray()) main.ViewModel.CloseResolved(doc);
            main.Close(); Directory.Delete(root,true);
        }
        static async Task Idle() { await Dispatcher.Yield(DispatcherPriority.ApplicationIdle); }
    }
    private static IEnumerable<DependencyObject> Descendants(DependencyObject root)
    {
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root,i); yield return child;
            foreach (var descendant in Descendants(child)) yield return descendant;
        }
    }
}
