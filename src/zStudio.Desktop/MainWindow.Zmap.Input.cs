using System.IO;
using System.Numerics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using zStudio.Automation;

namespace zStudio.Desktop;

public partial class MainWindow
{
    private Point zmapDragPointer;
    private bool zmapDragMoved, zmapDragSnap;

    private void ZmapPointerDown(object sender, MouseButtonEventArgs e)
    {
        if (zmap == null || ZmapLayer.Viewport is not { } viewport) return;
        if (zmapDrag != null) { e.Handled = true; return; }
        // The chips over the view keep their own clicks (the banner's Finish, Split and Cancel).
        if (e.ChangedButton == MouseButton.Middle || OnZmapChrome(e.OriginalSource)) return;
        var screen = e.GetPosition(viewport.RenderSurface);
        if (!viewport.IsNavigationPointInside(screen)) return;
        e.Handled = true;
        if (zmapBusy || zmapFieldsDirty || e.ChangedButton != MouseButton.Left) return;
        viewport.FocusView();
        EditZmap(() =>
        {
            if (zmapDrawing != null)
            {
                if (viewport.TryZmapPoint(screen, 0, out var p)) AddZmapDraftPoint(p);
                return;
            }
            var overlayPoint = e.GetPosition(ZmapLayer);
            if (zmapSplit != null)
            {
                if (ZmapLayer.PickSelectedVertex(overlayPoint) is Guid endpoint) PickZmapSplit(endpoint);
                else ZmapMessage("Pick a vertex on the selected outline.");
                return;
            }
            var hit = ZmapLayer.Pick(overlayPoint, zmapInsert);
            if (zmapInsert)
            {
                if (hit is { EdgeStart: Guid edge })
                {
                    zmap.Insert(hit.Value.Outline, edge, hit.Value.Fraction);
                    zmapInsert = false;
                }
                else ZmapMessage("Click between the vertices of an edge on the selected outline.");
                return;
            }
            zmap.Select(hit?.Outline, hit?.Vertex);
            if (hit is not { Vertex: Guid point } selection || SelectedZmapPosition() is not { } position) return;
            viewport.StopCameraMotion();
            if (!viewport.ProjectToView(position, out var projected, out _) || !PreviewHost.CaptureMouse()) return;
            zmapDrag = (selection.Outline, point, screen, projected, position);
            zmapDragPointer = screen;
            zmapDragMoved = zmapDragSnap = false;
            zmap.Touch();
        });
    }

    private void ZmapPointerMove(object sender, MouseEventArgs e)
    {
        if (zmap == null || ZmapLayer.Viewport is not { } viewport) return;
        var pointer = e.GetPosition(viewport.RenderSurface);
        if (zmapDrag != null)
        {
            e.Handled = true;
            UpdateZmapDrag(pointer, System.Windows.Input.Keyboard.Modifiers.HasFlag(ModifierKeys.Control));
        }
        else if (OnZmapChrome(e.OriginalSource))
        {
            // Over the banner's buttons a click presses the button: the view previews no point or endpoint there.
            if (zmapSplitHover != null) { zmapSplitHover = null; UpdateZmapSplitPreview(); }
            if (ZmapLayer.Pointer != null) { ZmapLayer.Pointer = null; ZmapLayer.InvalidateVisual(); }
        }
        else if (zmapSplit is { CanApply: false })
        {
            var candidate = ZmapLayer.PickSelectedVertex(e.GetPosition(ZmapLayer));
            if (zmapSplitHover == candidate) return;
            zmapSplitHover = candidate;
            UpdateZmapSplitPreview();
        }
        else if (zmapDrawing != null)
        {
            ZmapLayer.Pointer = viewport.TryZmapPoint(pointer, 0, out var position) ? position : null;
            ZmapLayer.InvalidateVisual();
        }
    }

    private void ZmapPointerUp(object sender, MouseButtonEventArgs e)
    {
        if (zmap == null || e.ChangedButton != MouseButton.Left || zmapDrag is not { } drag) return;
        e.Handled = true;
        ZmapPointerMove(sender, e);
        var pending = ZmapLayer.Drag;
        CancelZmapDrag();
        EditZmap(() => { if (pending is { } move) zmap.Move(drag.Outline, drag.Vertex, move.Position); });
    }

    private void UpdateZmapDrag(Point pointer, bool snap)
    {
        if (zmapDrag is not { } drag || ZmapLayer.Viewport is not { } viewport) return;
        zmapDragPointer = pointer;
        var delta = pointer - drag.Press;
        if (!zmapDragMoved && delta.LengthSquared < 1) return;
        zmapDragMoved = true;
        ZmapLayer.SnapTarget = null;
        if (viewport.TryZmapPoint(drag.Projected + delta, drag.Position.Y, out var position))
        {
            if (snap && viewport.TrySnapZmapVertex(position, out var snapped))
            {
                position = snapped;
                ZmapLayer.SnapTarget = snapped;
            }
            ZmapLayer.Drag = (drag.Vertex, position);
        }
        else ZmapLayer.Drag = null;
        zmapDragSnap = snap;
        RefreshZmapBanner();
        zmap!.Touch();
        ZmapLayer.InvalidateVisual();
    }

    private void ZmapModifierChanged(object sender, KeyEventArgs e)
    {
        if (zmapDrag == null || e.IsRepeat || e.Key is not (Key.LeftCtrl or Key.RightCtrl)) return;
        Key other = e.Key == Key.LeftCtrl ? Key.RightCtrl : Key.LeftCtrl;
        bool snap = e.RoutedEvent == System.Windows.Input.Keyboard.PreviewKeyDownEvent || System.Windows.Input.Keyboard.IsKeyDown(other);
        UpdateZmapDrag(zmapDragPointer, snap);
    }

    private void ZmapSnapChanged()
    {
        if (zmapDrag != null)
            UpdateZmapDrag(zmapDragPointer, System.Windows.Input.Keyboard.Modifiers.HasFlag(ModifierKeys.Control));
        else RefreshZmap();
    }

    private bool ZmapKeyboard(KeyEventArgs e)
    {
        if (zmap == null) return false;
        var modifiers = System.Windows.Input.Keyboard.Modifiers;
        bool native = CameraInputIsNative(e.OriginalSource as DependencyObject);
        if (e.Key == Key.Escape && (zmapOutlines.ContextMenu?.IsOpen == true || zmapObjectives.IsDropDownOpen || zmapVertices.IsDropDownOpen ||
            zmapFirst.IsDropDownOpen || zmapSecond.IsDropDownOpen)) return false;
        if (e.Key == Key.Escape)
            EditZmap(() => { if (HasZmapDraft) CancelZmapDraft(); else zmap.Select(null, null); });
        else if ((e.Key == Key.System ? e.SystemKey : e.Key) == Key.F10 && modifiers == ModifierKeys.None)
            ZmapMessage("Finish ZMAP editing to hide the interface.");
        // Alt+Enter focuses the outline's card, as it focuses the docked card in the map layout.
        else if ((e.Key == Key.Enter || e.SystemKey == Key.Enter) && modifiers == ModifierKeys.Alt)
        {
            // What stands in the way says so: a draft or tool disables the fields without clearing the selection.
            string? wait = HasZmapPathDraft ? "Finish or cancel the outline draft first." : zmap.Selected == null ? "Select an outline to edit its properties."
                : zmapInsert ? "Finish or cancel Insert vertex first." : !zmapColor.IsEnabled ? "Finish the current ZMAP action first." : null;
            if (wait != null) ZmapMessage(wait);
            else
            {
                // A collapsed Outline section opens first: a field inside it could not take the focus.
                zmapOutlineSection.Expand();
                zmapColor.BringIntoView();
                zmapColor.Focus(); zmapColor.SelectAll();
            }
        }
        else if (e.Key == Key.Enter && zmapDrawing != null && !native) EditZmap(FinishZmapDraw);
        else if (e.Key == Key.Enter && zmapSplit != null && (!native ||
            zmapFirst.IsKeyboardFocusWithin && !zmapFirst.IsDropDownOpen ||
            zmapSecond.IsKeyboardFocusWithin && !zmapSecond.IsDropDownOpen)) EditZmap(FinishZmapSplit);
        else if (e.Key == Key.Enter && zmapFieldsDirty && e.OriginalSource is TextBox) EditZmap(ApplyZmapFields);
        else if (e.Key == Key.Delete && e.OriginalSource is not (TextBoxBase or ComboBox) && zmap.Vertex != null) EditZmap(RemoveZmapVertex);
        else if (e.Key == Key.O && modifiers == ModifierKeys.Control) ZmapUiAsync(OpenZmapUiAsync);
        else if (e.Key == Key.N && modifiers == ModifierKeys.Control) ZmapUiAsync(() => ChangeZmapUiAsync(null, true));
        else if (e.Key == Key.S && (modifiers == ModifierKeys.Control || modifiers == (ModifierKeys.Control | ModifierKeys.Shift)))
            ZmapUiAsync(() => SaveZmapUiAsync(modifiers.HasFlag(ModifierKeys.Shift)));
        else if (e.Key == Key.Z && modifiers == ModifierKeys.Control && e.OriginalSource is not TextBoxBase) EditZmap(() => ChangeZmapHistory(false));
        else if (e.Key == Key.Y && modifiers == ModifierKeys.Control && e.OriginalSource is not TextBoxBase) EditZmap(() => ChangeZmapHistory(true));
        else if (CameraKeyAction(e.Key, modifiers) is { } navigation && !native &&
            (ZmapLayer.Viewport?.IsKeyboardFocusWithin == true || ZmapLayer.Viewport?.IsMouseOver == true))
        {
            if (navigation == "frameAll") EditZmap(() => FrameZmap("map"));
            else if (navigation == "frameSelected" && zmap.Outline != null) EditZmap(() => FrameZmap(zmap.Vertex == null ? "outline" : "vertex"));
            else if (zmapDrag == null && (navigation.StartsWith("pan", StringComparison.Ordinal) || navigation.StartsWith("zoom", StringComparison.Ordinal)))
                RunCameraNavigation(navigation);
        }
        else return false;
        e.Handled = true;
        return true;
    }
}
