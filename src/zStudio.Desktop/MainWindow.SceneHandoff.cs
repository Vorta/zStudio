using System.Windows;
using zStudio.Rendering;

namespace zStudio.Desktop;

/// <summary>
/// A structural source edit can replace the presentation. Its previous scene stays frozen on screen, darkened under a
/// notice, while a new viewport is prepared. Ordinary transforms retain the viewport.
/// </summary>
public partial class MainWindow
{
    /// <summary>The rebuilt document that takes over the shown scene when it becomes the selected document.</summary>
    private DocumentModel? sceneHandoff;
    /// <summary>The replaced document's scene, shown without input until the rebuilt world's scene is ready.</summary>
    private SceneViewport? retiringScene;
    /// <summary>A rebuilt scene is replacing the shown one (MCP preview_state reloading).</summary>
    internal bool IsViewReloading => retiringScene != null;
    private System.Windows.Threading.DispatcherTimer? rebuildNoticeDelay;
    /// <summary>The rebuild has run for longer than a moment.</summary>
    private bool rebuildNoticeDue;
    /// <summary>The rebuild evaluates the whole world (not a transform the view keeps): only that darkens the view before the swap.</summary>
    private bool rebuildEvaluating;
    /// <summary>The document the notice's state belongs to: another shown document (a replacement without a handoff) starts afresh.</summary>
    private DocumentModel? noticeDocument;
    /// <summary>Marks, from any thread, that <paramref name="doc"/>'s rebuild evaluates the whole world (when it is the shown one).</summary>
    private void MarkWorldEvaluating(DocumentModel doc) => Dispatcher.BeginInvoke(() => { if (shownDocument == doc) { rebuildEvaluating = true; UpdateReloadingView(); } });

    /// <summary>Keeps the shown scene on screen as the retiring scene; the next 3D preview loads into a new viewport.</summary>
    private void RetireScene()
    {
        DropRetiringScene();
        if (scene == null) return;
        // An edit's object transform preview stays on the frozen scene until the rebuilt one shows it for real.
        retiringScene = scene; scene = null;
        retiringScene.CancelPickupDrag(); retiringScene.SetAiPathEditing(null); retiringScene.CancelNavigation();
        // Disabled, it takes no mouse or keyboard input (focus leaves it) while it stays drawn.
        retiringScene.IsHitTestVisible = false; retiringScene.IsEnabled = false;
        SetSceneControlsEnabled(false);
        UpdateReloadingView();
    }

    /// <summary>Removes the retiring scene: the new scene took its place, or the rebuilt world could not be shown.</summary>
    private void DropRetiringScene()
    {
        if (retiringScene is not { } retiring) return;
        retiringScene = null;
        // The rebuilt scene is shown (or could not be): the rebuild's notice ends with it.
        rebuildEvaluating = rebuildNoticeDue = false; rebuildNoticeDelay?.Stop();
        if (ReferenceEquals(SceneHost.Content, retiring)) SceneHost.Content = scene;
        retiring.Dispose();
        // Still disabled while the project evaluates (the rebuild's own end enables them).
        SetSceneControlsEnabled(!(sourceWorkspaceBusy || shownDocument?.SourceWorld?.IsRebuilding == true));
        UpdateReloadingView();
    }

    /// <summary>Shows the rebuilt scene in place of the retiring one.</summary>
    private void CompleteSceneHandoff()
    {
        if (retiringScene is not { } retiring || scene == null) return;
        // Presentation the replaced viewport kept across a rebuild, as a refreshed scene keeps it (RefreshStaticSceneCoreAsync).
        scene.SetValveOptions(retiring.ValveOverlayVisible,
            scene.Mission?.Layout.MissionArchive == retiring.Mission?.Layout.MissionArchive ? retiring.ValveFilter : null);
        SceneHost.Content = scene;
        DropRetiringScene();
        ApplyAiOptions();
    }

    /// <summary>
    /// While an edit of the shown source world takes longer than a moment, a card names the step and its progress. The view
    /// darkens under it once it is reloading: the whole world is evaluated again, then the rebuilt scene replaces the shown one.
    /// A transform edit the view keeps only shows the card while it is prepared. An edit's phases (preparation, then
    /// acceptance) follow each other without a gap: the notice ends only once no rebuild follows.
    /// </summary>
    private void UpdateReloadingView()
    {
        if (shownDocument != noticeDocument && retiringScene == null) { rebuildNoticeDelay?.Stop(); rebuildNoticeDue = rebuildEvaluating = false; }
        noticeDocument = shownDocument;
        bool rebuilding = shownDocument?.SourceWorld?.IsRebuilding == true && SceneHost.Visibility == Visibility.Visible;
        if (rebuilding && !rebuildNoticeDue && rebuildNoticeDelay?.IsEnabled != true)
        {
            rebuildNoticeDelay ??= new(System.Windows.Threading.DispatcherPriority.Normal, Dispatcher) { Interval = TimeSpan.FromMilliseconds(350) };
            rebuildNoticeDelay.Tick -= RebuildNoticeDue; rebuildNoticeDelay.Tick += RebuildNoticeDue;
            rebuildNoticeDelay.Start();
        }
        if (!rebuilding && retiringScene == null && (rebuildNoticeDue || rebuildEvaluating || rebuildNoticeDelay?.IsEnabled == true))
        {
            // A tick after the work ended must not bring the notice (or its shade) back before it is ended.
            rebuildNoticeDelay?.Stop();
            Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Background, EndRebuildNotice);
        }
        bool reloading = retiringScene != null || rebuildNoticeDue && rebuildEvaluating && SceneHost.Visibility == Visibility.Visible;
        bool shown = reloading || rebuildNoticeDue && rebuilding;
        ReloadingTitle.Text = retiringScene != null ? "Loading the rebuilt world…" : rebuildEvaluating ? "Rebuilding the world…" : "Applying the edit…";
        if (reloading) ReloadingView.SetResourceReference(System.Windows.Controls.Panel.BackgroundProperty, "ReloadingShade");
        else ReloadingView.Background = System.Windows.Media.Brushes.Transparent;
        // Hidden UI shows the view alone: no notice or shade over it.
        ReloadingView.Visibility = shown && !presentationUiHidden ? Visibility.Visible : Visibility.Collapsed;
        RefreshStaleBanner();
    }
    private void RebuildNoticeDue(object? sender, EventArgs e) { rebuildNoticeDelay?.Stop(); rebuildNoticeDue = true; UpdateReloadingView(); }
    private void EndRebuildNotice()
    {
        if (shownDocument?.SourceWorld?.IsRebuilding == true || retiringScene != null) return;
        rebuildNoticeDelay?.Stop(); rebuildNoticeDue = rebuildEvaluating = false;
        UpdateReloadingView();
    }
    /// <summary>The "Source file changed on disk" banner gives way to the reload notice (and hides with the UI).</summary>
    private void RefreshStaleBanner() =>
        SourceStaleHost.Visibility = presentationUiHidden || ReloadingView.Visibility == Visibility.Visible ? Visibility.Collapsed : Visibility.Visible;
}
