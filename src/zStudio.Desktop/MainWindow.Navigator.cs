using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using KeyboardInput = System.Windows.Input.Keyboard;
using Recoil.Zbd.Automation;

namespace Recoil.Zbd.Desktop;

public partial class MainWindow
{
    private bool filesDetached, changingNavigator;
    private int navigatorLayoutGeneration, navigatorInteractionGeneration;
    private Point filesScroll;
    internal string NavigatorMode => NavigatorHost.Visibility != Visibility.Visible ? "hidden" : filesDetached ? "split" : "tabbed";

    private void InitializeResponsiveNavigator()
    {
        // Reparenting through TabControl temporarily disconnects inherited data
        // contexts. Keep ItemsSource stable; row presentation lives on FolderNode.
        FileTree.DataContext = ViewModel;
        FileTree.GotKeyboardFocus += (_, _) =>
        {
            if (ready && !changingNavigator && !IsChangingLayout) RememberNavigatorSection(0);
        };
        NavigationTabs.GotKeyboardFocus += (_, _) =>
        {
            if (ready && !changingNavigator && !IsChangingLayout && NavigationTabs.SelectedIndex > 0)
                RememberNavigatorSection(NavigationTabs.SelectedIndex);
        };
    }
    private bool NavigatorSectionAvailable(int index) => index switch
    {
        0 or 2 => true,
        1 => ViewModel.SelectedDocument != null,
        3 => ViewModel.SelectedDocument?.SceneRoots.Count > 0,
        _ => false
    };
    private void ValidateNavigatorSection(int index)
    {
        if (!NavigatorSectionAvailable(index))
            throw new StudioCommandException("unavailable_tab","This section is unavailable in the current workspace.");
    }
    private void RememberNavigatorSection(int index)
    {
        navigatorInteractionGeneration++;
        Layout.BrowserTab = index;
        if (index > 0) Layout.ContentBrowserTab = index;
    }
    internal void SelectNavigatorSection(int index)
    {
        // An asynchronous open/select can lose its document during publication.
        // Its owning operation reports that conflict; presentation must not mask it.
        if (!NavigatorSectionAvailable(index)) return;
        RememberNavigatorSection(index);
        UpdateNavigatorAvailability();
    }
    private void NavigatorSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!ready || changingNavigator || e.Source != NavigationTabs || NavigationTabs.SelectedIndex < 0) return;
        int index = NavigationTabs.SelectedIndex;
        if (NavigatorSectionAvailable(index)) RememberNavigatorSection(index);
        UpdateNavigatorAvailability();
    }
    private void UpdateNavigatorAvailability()
    {
        bool previous = changingNavigator; changingNavigator = true;
        try
        {
            var layout = Layout;
            bool hasDocument = ViewModel.SelectedDocument != null;
            if (!NavigatorSectionAvailable(layout.BrowserTab)) layout.BrowserTab = hasDocument ? 1 : 0;
            int content = NavigatorSectionAvailable(layout.ContentBrowserTab) ? layout.ContentBrowserTab : hasDocument ? 1 : 2;
            // Fallback presentation must not overwrite the remembered non-Files tab.
            int selected = filesDetached ? layout.BrowserTab > 0 ? layout.BrowserTab : content : layout.BrowserTab;
            NavigationTabs.SelectedIndex = selected;
            FilesTab.Visibility = filesDetached ? Visibility.Collapsed : Visibility.Visible;
            AssetsTab.Visibility = hasDocument ? Visibility.Visible : Visibility.Collapsed;
            DocumentSceneTab.Visibility = NavigatorSectionAvailable(3) ? Visibility.Visible : Visibility.Collapsed;
            AssetsTab.IsEnabled = hasDocument; DocumentSceneTab.IsEnabled = NavigatorSectionAvailable(3);
        }
        finally { changingNavigator = previous; }
    }
    private void SetFilesDetached(bool detached)
    {
        if (filesDetached == detached) return;
        bool previous = changingNavigator; changingNavigator = true;
        var focused = FileTree.IsKeyboardFocusWithin ? KeyboardInput.FocusedElement : null;
        FolderNode? focusedNode = null;
        for (var parent = focused as DependencyObject; parent is Visual; parent = VisualTreeHelper.GetParent(parent))
            if (parent is TreeViewItem { DataContext: FolderNode node }) { focusedNode = node; break; }
        int interaction = navigatorInteractionGeneration;
        var scroller = WorkspaceScrollers(FileTree).FirstOrDefault();
        if (FileTree.IsVisible && scroller != null) filesScroll = new(scroller.HorizontalOffset,scroller.VerticalOffset);
        int generation = ++navigatorLayoutGeneration;
        try
        {
            filesDetached = detached;
            // Keep one tree and its node identities; only change the host.
            if (detached) { FilesTab.Content = null; DetachedFilesContent.Content = FileTree; }
            else { DetachedFilesContent.Content = null; FilesTab.Content = FileTree; }
            DetachedFilesHost.Visibility = FilesSplitter.Visibility = detached ? Visibility.Visible : Visibility.Collapsed;
            UpdateNavigatorAvailability();
        }
        finally { changingNavigator = previous; }
        Dispatcher.BeginInvoke(DispatcherPriority.Loaded, () =>
        {
            if (generation != navigatorLayoutGeneration || !IsLoaded || !FileTree.IsVisible) return;
            var scroll = WorkspaceScrollers(FileTree).FirstOrDefault();
            if (focused != null && interaction == navigatorInteractionGeneration &&
                (KeyboardInput.FocusedElement == null || ReferenceEquals(KeyboardInput.FocusedElement,focused) || KeyboardInput.FocusedElement is DependencyObject active && (ReferenceEquals(active,this) || NavigatorHost.IsAncestorOf(active))))
            {
                bool wasChanging = changingNavigator; changingNavigator = true;
                try
                {
                    IInputElement target = focused is DependencyObject original && FileTree.IsAncestorOf(original) ? focused
                        : focusedNode != null ? FindFileContainer(FileTree,focusedNode) ?? (IInputElement)FileTree : FileTree;
                    KeyboardInput.Focus(target);
                }
                finally { changingNavigator = wasChanging; }
            }
            scroll?.ScrollToHorizontalOffset(filesScroll.X); scroll?.ScrollToVerticalOffset(filesScroll.Y);
        });
    }
    private static TreeViewItem? FindFileContainer(ItemsControl parent,FolderNode node)
    {
        foreach (object item in parent.Items)
            if (parent.ItemContainerGenerator.ContainerFromItem(item) is TreeViewItem container)
            {
                if (ReferenceEquals(item,node)) return container;
                if (FindFileContainer(container,node) is { } child) return child;
            }
        return null;
    }
}
