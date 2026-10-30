using System.Windows;
using zStudio.Core;
using zStudio.Core.Animation;

namespace zStudio.Desktop;

public partial class AnimationEditor
{
    internal event Action? SceneHierarchyChanged;
    private GameScene? publishedHierarchy;
    /// <summary>Stop: the preview at 0 s (the scenario stays).</summary>
    public void Stop() { if (ResolvePendingDrafts()) { pendingPlay = false; _ = SeekAsync(0); } }
    /// <summary>One 1/60 s preview tick back or forward.</summary>
    public void Step(int direction) { if (ResolvePendingDrafts()) _ = SeekAsync(Math.Clamp((frame?.Time ?? 0) + direction * AnimationPlayer.StepSeconds, 0, rangeEnd)); }
    private bool inspectorReplacement;
    internal bool PresentationUiHidden { get; private set; }
    internal void SetPresentationUiHidden(bool hidden)
    {
        PresentationUiHidden = hidden;
        ViewOptionsRow.Visibility = hidden ? Visibility.Collapsed : Visibility.Visible;
        ViewportArea.Visibility = !hidden && inspectorReplacement ? Visibility.Hidden : Visibility.Visible;
    }
    internal void ShowInspectorReplacement(bool show)
    {
        inspectorReplacement = show;
        ViewportArea.Visibility = !PresentationUiHidden && show ? Visibility.Hidden : Visibility.Visible;
    }
    internal FrameworkElement ViewportRegion => ViewportArea;
    /// <summary>The labelled view commands (LOD, Difficulty, Camera, Display, Ground): the window shows them in the compiled command bar.</summary>
    internal FrameworkElement Commands => ViewOptionsRow;
}
