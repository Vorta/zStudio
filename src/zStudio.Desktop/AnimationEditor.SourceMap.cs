using System.Windows;

namespace zStudio.Desktop;

public partial class AnimationEditor
{
    internal void ApplySourceMapDisplay(bool wireframe, bool textured, bool bounds, bool faceOrientation)
    {
        viewport.SetWireframe(wireframe); viewport.SetTextured(textured); viewport.SetBounds(bounds);
        ShowFaceOrientation.IsChecked = faceOrientation; viewport.SetFaceOrientation(faceOrientation);
    }
    internal void SetSourceMapPreview()
    {
        changing = true;
        try { ShowLevel.IsChecked = true; FollowCamera.IsChecked = false; GroundCollision.IsChecked = false; }
        finally { changing = false; }
        // The map owns presentation controls; this host supplies its existing viewport and transport.
        if (EditorRoot.Children.Count > 0) EditorRoot.Children[0].Visibility = Visibility.Collapsed;
    }
}
