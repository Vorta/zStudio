using System.Numerics;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Media3D;
using System.Windows.Shapes;
using zStudio.Core;

namespace zStudio.Rendering;

public sealed partial class SceneViewport
{
    private readonly HashSet<string> aiPathSelection = [];
    private readonly HashSet<string> aiPathNetworkNodes = [];
    private readonly Dictionary<string, Vector3> aiPathPreview = [];
    private Point? aiPathBoxStart, aiPathPointerStart;
    private Point? aiPathBoxLast, aiPathBoxScreenStart;
    private string aiPathBoxMode = "replace";
    private Rectangle? aiPathBox;
    private Vector3 aiPathPointerRight, aiPathPointerUp;
    private float aiPathPointerScale;
    public string? AiPathEditingNetwork { get; private set; }
    public IReadOnlyList<string> SelectedAiPathNodes => aiPathSelection.Order(StringComparer.Ordinal).ToArray();
    public bool IsAiPathPointerTransform => aiPathPointerStart != null;
    public double AiPathPointerUnitsPerPixel => aiPathPointerScale;
    public Func<bool>? AiPathSelectionChanging { get; set; }
    public event Action? AiPathSelectionChanged;
    public event Action? AiPathEditingChanged;
    public event Action<Vector3, double, double>? AiPathPointerMoved;
    public event Action? AiPathTransformConfirmRequested;
    public event Action? AiPathTransformCancelRequested;

    public void SetAiPathEditing(string? networkId, IEnumerable<string>? selectedIds = null)
    {
        if (networkId == null && AiPathEditingNetwork == null) return;
        var selection = selectedIds?.Take(MaximumRenderedAiNodes + 1).ToArray() ?? [];
        if (networkId != null)
        {
            RequireAiPathInteraction();
            if (!AiVisible || AiNetworkFilter != null && AiNetworkFilter != networkId ||
                AiNetworks.Networks.FirstOrDefault(n => n.Id == networkId) is not { } network)
                throw new InvalidOperationException("Show the AI network before editing it.");
            var ids = network.Nodes.Select(n => n.Id).ToHashSet(StringComparer.Ordinal);
            if (selection.Length > MaximumRenderedAiNodes || selection.Any(id => !ids.Contains(id)))
                throw new ArgumentException("Select at most 16,384 nodes from this AI network.", nameof(selectedIds));
        }
        CancelAiPathBox(); aiPathPointerStart = null; aiPathPreview.Clear();
        AiPathEditingNetwork = networkId; aiPathSelection.Clear(); aiPathNetworkNodes.Clear();
        if (networkId != null)
        {
            aiPathSelection.UnionWith(selection);
            aiPathNetworkNodes.UnionWith(AiNetworks.Networks.Single(n => n.Id == networkId).Nodes.Select(n => n.Id));
            SelectedInspection = null; SelectedAiNode = null; SelectPickup(null);
            FramingSelection = null; RefreshPickupSelection(); InspectionChanged?.Invoke();
        }
        aiMarkerPose = null; UpdateAiMarkers(); RefreshAiSelection();
        AiPathEditingChanged?.Invoke(); AiPathSelectionChanged?.Invoke();
    }

    private void RequireAiPathInteraction()
    {
        if (!PlacementEditingEnabled) throw new InvalidOperationException("This world is read only: edit its source project instead.");
        if (IsFlyActive || IsPickupDragging || HasTransformDraft || TerrainBrushActive || ZonePaintActive || HasParachutePreview || IsParachutePointerMoving)
            throw new InvalidOperationException("Finish the active navigation or editing operation before editing an AI path.");
    }

    public bool SetAiPathSelection(IEnumerable<string> ids, string mode = "replace")
    {
        RequireAiPathInteraction();
        if (AiPathEditingNetwork is not { } networkId) throw new InvalidOperationException("Enter AI path Edit mode first.");
        if (mode is not ("replace" or "add" or "toggle")) throw new ArgumentException("Use replace, add or toggle selection.", nameof(mode));
        var requested = ids.Take(MaximumRenderedAiNodes + 1).ToArray();
        if (requested.Length > MaximumRenderedAiNodes || requested.Any(id => !aiPathNetworkNodes.Contains(id)))
            throw new ArgumentException("Select at most 16,384 nodes from the edited network.", nameof(ids));
        if (IsAiPathPointerTransform || aiPathPreview.Count != 0 || AiPathSelectionChanging?.Invoke() == false) return false;
        HashSet<string> next = mode == "replace" ? [] : new(aiPathSelection, StringComparer.Ordinal);
        foreach (string id in requested.Distinct(StringComparer.Ordinal))
            if (mode != "toggle" || !next.Remove(id)) next.Add(id);
        if (next.Count > MaximumRenderedAiNodes) throw new ArgumentException("Select at most 16,384 nodes.", nameof(ids));
        aiPathSelection.Clear(); aiPathSelection.UnionWith(next); RefreshAiSelection(); AiPathSelectionChanged?.Invoke(); return true;
    }

    /// <summary>Viewport-local rectangle selection uses the same visible marker and occlusion rules as a click.</summary>
    public bool SelectAiPathBox(Rect rectangle, string mode = "replace")
    {
        if (rectangle.IsEmpty || !double.IsFinite(rectangle.X) || !double.IsFinite(rectangle.Y) ||
            !double.IsFinite(rectangle.Width) || !double.IsFinite(rectangle.Height) || rectangle.Left < 0 || rectangle.Top < 0 ||
            rectangle.Right > viewport.ActualWidth || rectangle.Bottom > viewport.ActualHeight)
            throw new ArgumentException("The selection rectangle must be finite and inside the viewport.", nameof(rectangle));
        var ids = aiRendered.Where(n => aiPathNetworkNodes.Contains(n.Id) &&
            TryProjectAiNode(n, out var screen, out var depth) && rectangle.Contains(screen) && !IsAiNodeOccluded(n, screen, depth)).Select(n => n.Id).ToArray();
        return SetAiPathSelection(ids, mode);
    }

    /// <summary>Only presentation positions change. The accepted snapshot and its record identities remain intact.</summary>
    public void SetAiPathPreview(IReadOnlyDictionary<string, Vector3>? positions)
    {
        if (positions != null)
        {
            RequireAiPathInteraction();
            if (AiPathEditingNetwork == null || positions.Count > MaximumRenderedAiNodes ||
                positions.Any(p => !aiPathSelection.Contains(p.Key) || !Finite(p.Value) ||
                    Math.Abs(p.Value.X) > 1e12 || Math.Abs(p.Value.Y) > 1e12 || Math.Abs(p.Value.Z) > 1e12))
                throw new ArgumentException("Preview positions must be finite selected AI nodes within the supported coordinate range.", nameof(positions));
        }
        aiPathPreview.Clear();
        if (positions != null) foreach (var pair in positions) aiPathPreview.Add(pair.Key, pair.Value);
        ++inspectionSerial; aiMarkerPose = null; UpdateAiMarkers(); viewport.InvalidateRender();
    }

    private Vector3 AiPosition(AiNode node) => aiPathPreview.GetValueOrDefault(node.Id, node.Position);

    public void SetAiPathPointerTransform(bool active)
    {
        if (!active) { aiPathPointerStart = null; return; }
        RequireAiPathInteraction();
        if (AiPathEditingNetwork == null || aiPathSelection.Count == 0)
            throw new InvalidOperationException("Select AI nodes before starting a transform.");
        CancelAiPathBox(); CancelNavigation(); StopCameraMotion();
        var pose = CaptureView(); var forward = pose.LookDirection; forward.Normalize();
        var right = Vector3D.CrossProduct(forward, pose.UpDirection); right.Normalize();
        var up = Vector3D.CrossProduct(right, forward); up.Normalize();
        aiPathPointerRight = new((float)right.X, (float)right.Y, (float)right.Z);
        aiPathPointerUp = new((float)up.X, (float)up.Y, (float)up.Z);
        var nodes = AiNetworks.Networks.Single(n => n.Id == AiPathEditingNetwork).Nodes.Where(n => aiPathSelection.Contains(n.Id)).ToArray();
        var center = new Vector3((float)nodes.Average(n => (double)AiPosition(n).X), (float)nodes.Average(n => (double)AiPosition(n).Y), (float)nodes.Average(n => (double)AiPosition(n).Z));
        aiPathPointerScale = AiScale(center, 1); aiPointerDelta = default; aiPresentationRebase = false;
        aiPathPointerStart = Mouse.GetPosition(viewport); viewport.Focus();
    }

    private bool HandleAiPathDown(Point point, MouseButtonEventArgs e)
    {
        if (AiPathEditingNetwork == null || !IsInsidePickupViewport(point)) return false;
        if (e.ChangedButton is not (MouseButton.Left or MouseButton.Right)) return IsAiPathPointerTransform;
        // Returning from a numeric field to the viewport restores modal keyboard commands,
        // even when the draft vetoes selection. This click does not accept a numeric draft.
        viewport.Focus();
        if (IsAiPathPointerTransform)
        {
            if (e.ChangedButton == MouseButton.Left) AiPathTransformConfirmRequested?.Invoke(); else AiPathTransformCancelRequested?.Invoke();
            return true;
        }
        if (e.ChangedButton == MouseButton.Right)
        {
            if (aiPathSelection.Count != 0) AiPropertiesRequested?.Invoke();
            return true;
        }
        if (!PlacementEditingEnabled || IsFlyActive || aiPathPreview.Count != 0 || AiPathSelectionChanging?.Invoke() == false) return true;
        string mode = Keyboard.Modifiers.HasFlag(ModifierKeys.Control) ? "toggle" : Keyboard.Modifiers.HasFlag(ModifierKeys.Shift) ? "add" : "replace";
        if (PickAiNode(point) is { } id && aiPathNetworkNodes.Contains(id))
            SetAiPathSelection([id], mode);
        else
        {
            CancelNavigation(); aiPathBoxStart = aiPathBoxLast = point; aiBoxPresentationRebase = false; aiPathBoxMode = mode;
            aiPathBoxScreenStart = PresentationSource.FromVisual(viewport) == null ? null : viewport.PointToScreen(point);
            aiPathBox ??= new() { Stroke = Brushes.Gold, StrokeThickness = 1, Fill = new SolidColorBrush(Color.FromArgb(28, 255, 215, 0)),
                HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top, IsHitTestVisible = false };
            if (!presentationOverlays.Children.Contains(aiPathBox)) presentationOverlays.Children.Add(aiPathBox);
            aiPathBox.Width = aiPathBox.Height = 0; aiPathBox.Margin = new(point.X, point.Y, 0, 0); aiPathBox.Visibility = Visibility.Visible;
            viewport.CaptureMouse();
        }
        viewport.Focus(); return true;
    }

    private bool HandleAiPathMove(Point point)
    {
        if (aiPathPointerStart is { } origin)
        {
            if (aiPresentationRebase)
            { aiPathPointerStart = point - aiPointerDelta; aiPresentationRebase = false; return true; }
            var delta = point - origin; aiPointerDelta = delta;
            var movement = (aiPathPointerRight * (float)delta.X - aiPathPointerUp * (float)delta.Y) * aiPathPointerScale;
            if (Finite(movement)) AiPathPointerMoved?.Invoke(movement, delta.X, delta.Y);
            return true;
        }
        if (aiPathBoxStart is not { } start || aiPathBox == null) return false;
        RebaseAiPathBox(point);
        start = aiPathBoxStart!.Value; aiPathBoxLast = point;
        var rectangle = AiPathRectangle(start, point);
        aiPathBox.Margin = new(rectangle.X, rectangle.Y, 0, 0); aiPathBox.Width = rectangle.Width; aiPathBox.Height = rectangle.Height; return true;
    }

    private Rect AiPathRectangle(Point start, Point end)
    {
        Point Clamp(Point point) => new(Math.Clamp(point.X, 0, Math.Max(0, viewport.ActualWidth)), Math.Clamp(point.Y, 0, Math.Max(0, viewport.ActualHeight)));
        return new(Clamp(start), Clamp(end));
    }

    private bool HandleAiPathUp(Point point, MouseButtonEventArgs e)
    {
        if (aiPathBoxStart is not { } start || e.ChangedButton != MouseButton.Left) return false;
        RebaseAiPathBox(point); start = aiPathBoxStart!.Value;
        var rectangle = AiPathRectangle(start, point); string mode = aiPathBoxMode; CancelAiPathBox();
        SelectAiPathBox(rectangle, mode); return true;
    }

    private void RebaseAiPathBox(Point point)
    {
        if (aiPathBoxStart is not { } start) return;
        // The first post-layout event may already contain real movement, or may be the
        // release itself. Retain the physical press point rather than inferring its shift
        // from the latest pointer delta; PointFromScreen also handles a DPI transition.
        if (aiPathBoxScreenStart is { } screen && PresentationSource.FromVisual(viewport) != null)
        {
            aiPathBoxStart = viewport.PointFromScreen(screen);
            aiBoxPresentationRebase = false; aiPathBoxLast = point; return;
        }
        if (!aiBoxPresentationRebase) return;
        aiBoxPresentationRebase = false;
        // The last endpoint and the start move together when the viewport's local origin
        // changes. The physical selection rectangle must not grow across that layout shift.
        aiPathBoxStart = start + (point - (aiPathBoxLast ?? start));
        aiPathBoxLast = point;
    }

    public bool CancelAiPathBox()
    {
        if (aiPathBoxStart == null) return false;
        aiPathBoxStart = aiPathBoxLast = aiPathBoxScreenStart = null; aiBoxPresentationRebase = false;
        if (aiPathBox != null) aiPathBox.Visibility = Visibility.Collapsed;
        if (viewport.IsMouseCaptured) viewport.ReleaseMouseCapture();
        return true;
    }
}
