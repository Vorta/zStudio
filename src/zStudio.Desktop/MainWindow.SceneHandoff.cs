using System.Windows;
using zStudio.Rendering;

namespace zStudio.Desktop;

/// <summary>
/// A structural source edit can replace the presentation. Its previous scene stays frozen on screen while a new viewport
/// is prepared; a "Reloading view" notice covers that presentation transition. Ordinary transforms retain the viewport.
/// </summary>
public partial class MainWindow
{
    /// <summary>The rebuilt document that takes over the shown scene when it becomes the selected document.</summary>
    private DocumentModel? sceneHandoff;
    /// <summary>The replaced document's scene, shown without input until the rebuilt world's scene is ready.</summary>
    private SceneViewport? retiringScene;
    internal bool IsViewReloading => ReloadingView.Visibility == Visibility.Visible;

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
        SceneToolbar.IsEnabled = false;
        UpdateReloadingView();
    }

    /// <summary>Removes the retiring scene: the new scene took its place, or the rebuilt world could not be shown.</summary>
    private void DropRetiringScene()
    {
        if (retiringScene is not { } retiring) return;
        retiringScene = null;
        if (ReferenceEquals(SceneHost.Content, retiring)) SceneHost.Content = scene;
        retiring.Dispose();
        SceneToolbar.IsEnabled = true;
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

    /// <summary>The notice shows while the shown source world rebuilds and until its new scene replaces the old one.</summary>
    private void UpdateReloadingView() =>
        ReloadingView.Visibility = retiringScene != null
            ? Visibility.Visible : Visibility.Collapsed;
}
