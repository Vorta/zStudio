using System.Numerics;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Media3D;
using HelixToolkit;
using HelixToolkit.Maths;
using HelixToolkit.SharpDX;
using HelixToolkit.Wpf.SharpDX;

namespace zStudio.Rendering;

public sealed record ParachutePathPoint(double Phase, Vector3 Position);
public sealed record ParachuteRelease(double Phase, Vector3 Position, bool Conditional = true);
public sealed record ParachuteWindowOverlay(Guid Id, double Start, IReadOnlyList<ParachuteRelease> Releases);

public sealed partial class SceneViewport
{
    public const int MaximumParachuteSamples = 216_001, MaximumParachuteWindows = 256, MaximumParachuteReleases = 4096;
    private ParachutePathPoint[] parachutePath = [];
    private int[] parachuteNextMoving = [], parachutePreviousMoving = [];
    private ParachuteWindowOverlay[] parachuteWindows = [];
    private ParachuteWindowOverlay? parachutePreview;
    private TopMostGroup3D? parachuteOverlay;
    private LineGeometryModel3D? parachuteLine;
    private MeshGeometryModel3D? parachuteStarts, parachuteReleases, parachuteSelected;
    private ViewPose? parachuteMarkerPose;
    private double parachuteMarkerWidth, parachuteMarkerHeight;
    private int parachutePointerSegment;
    private double parachutePointerPhase;
    private int parachutePointerDirection;
    public Guid? SelectedParachuteWindow { get; private set; }
    public bool IsParachutePointerMoving { get; private set; }
    public bool HasParachutePreview => parachutePreview != null;
    public Func<bool>? ParachuteSelectionChanging { get; set; }
    public event Action<Guid?>? ParachuteWindowSelected;
    public event Action<int, double>? ParachuteMoveRequested;
    public event Action? ParachuteConfirmRequested;
    public event Action? ParachuteCancelRequested;

    /// <summary>A presentation-only overlay. Samples retain their ordered phase identity at crossings.</summary>
    public void SetParachuteOverlay(IReadOnlyList<ParachutePathPoint> path, IReadOnlyList<ParachuteWindowOverlay> windows)
    {
        if (path.Count == 0 && windows.Count == 0) { ClearParachutes(); return; }
        if (path.Count is < 2 or > MaximumParachuteSamples || windows.Count > MaximumParachuteWindows)
            throw new ArgumentException("The flight overlay exceeds its bounded sample/window allowance.");
        double previous = -1;
        foreach (var point in path)
        {
            if (!double.IsFinite(point.Phase) || point.Phase < 0 || point.Phase <= previous || !Finite(point.Position))
                throw new ArgumentException("Flight samples need finite positions and strictly increasing nonnegative phases.");
            previous = point.Phase;
        }
        if (windows.Select(w => w.Id).Distinct().Count() != windows.Count)
            throw new ArgumentException("Flight windows require distinct identities.");
        long releases = 0;
        foreach (var window in windows)
        {
            releases += window.Releases.Count;
            if (releases > MaximumParachuteReleases) throw new ArgumentException("The flight overlay exceeds its release-marker allowance.");
            ValidateParachuteWindow(window, path[0].Phase, path[^1].Phase);
        }
        var frozenPath = path.ToArray();
        var frozenWindows = windows.Select(w => w with { Releases = w.Releases.ToArray() }).ToArray();
        ClearParachutes(); parachutePath = frozenPath; parachuteWindows = frozenWindows;
        // Stationary samples still carry distinct times. Cache only their adjoining moving segments,
        // so crossing a long hover takes constant work without searching unrelated visits of the path.
        parachuteNextMoving = new int[path.Count - 1]; parachutePreviousMoving = new int[path.Count - 1];
        int moving = -1;
        for (int i = 0; i < parachutePreviousMoving.Length; i++)
        {
            if (frozenPath[i].Position != frozenPath[i + 1].Position) moving = i;
            parachutePreviousMoving[i] = moving;
        }
        moving = -1;
        for (int i = parachuteNextMoving.Length - 1; i >= 0; i--)
        {
            if (frozenPath[i].Position != frozenPath[i + 1].Position) moving = i;
            parachuteNextMoving[i] = moving;
        }
        parachuteOverlay = new(); viewport.Items.Add(parachuteOverlay);
        parachuteLine = new()
        {
            Geometry = new LineGeometry3D { Positions = new(parachutePath.Select(p => p.Position)),
                // Sample zero is the pre-flight pose. Keep its time for release/edit calculations,
                // but do not draw the initial jump from that pose to the flight path.
                Indices = new(Enumerable.Range(1, parachutePath.Length - 2).SelectMany(i => new[] { i, i + 1 })) },
            // The overlay does not enlarge authored scene depth bounds, just like its release markers.
            Color = Colors.DeepSkyBlue, Thickness = 1.6, IsHitTestVisible = false, IsDepthClipEnabled = false
        };
        parachuteOverlay.Children.Add(parachuteLine);
        parachuteStarts = ParachuteMesh(new(0, .8f, 1, 1));
        parachuteReleases = ParachuteMesh(new(1, .6f, .15f, 1));
        parachuteSelected = ParachuteMesh(new(1, 1, 1, 1));
        UpdateParachuteMarkers(); viewport.InvalidateRender();
    }

    private static void ValidateParachuteWindow(ParachuteWindowOverlay window, double first, double last)
    {
        if (!double.IsFinite(window.Start) || window.Start < first || window.Start > last ||
            window.Releases.Any(r => !double.IsFinite(r.Phase) || r.Phase < first || r.Phase > last || !Finite(r.Position)))
            throw new ArgumentException("Flight starts and release markers must lie inside the finite analyzed cycle.");
    }

    private MeshGeometryModel3D ParachuteMesh(Color4 color)
    {
        var material = PreviewMaterials.Create(true); material.DiffuseColor = color; material.EnableUnLit = true;
        var mesh = new MeshGeometryModel3D { Geometry = AiOctahedron(), Material = material, IsHitTestVisible = false,
            IsTransparent = true, CullMode = SharpDX.Direct3D11.CullMode.None, IsDepthClipEnabled = false };
        parachuteOverlay!.Children.Add(mesh); return mesh;
    }

    private void RequireParachuteInteraction()
    {
        if (!PlacementEditingEnabled) throw new InvalidOperationException("Unlock editing first.");
        if (IsFlyActive || IsPickupDragging || HasTransformDraft || TerrainBrushActive || ZonePaintActive || AiPathEditingNetwork != null)
            throw new InvalidOperationException("Finish the active navigation or editing operation before editing drop windows.");
        if (parachutePath.Length < 2) throw new InvalidOperationException("Analyze a flight path first.");
    }

    public bool SelectParachuteWindow(Guid? id)
    {
        if (id != null)
        {
            RequireParachuteInteraction();
            if (!parachuteWindows.Any(w => w.Id == id)) throw new ArgumentException("Drop window unavailable.", nameof(id));
        }
        if (IsParachutePointerMoving || parachutePreview != null || ParachuteSelectionChanging?.Invoke() == false) return false;
        SelectedParachuteWindow = id;
        if (id != null)
        {
            SelectedInspection = null; SelectAiNode(null); SelectPickup(null, false, true); FramingSelection = null;
            RefreshPickupSelection(); InspectionChanged?.Invoke();
        }
        parachuteMarkerPose = null; UpdateParachuteMarkers(); ParachuteWindowSelected?.Invoke(id); return true;
    }

    public void SetParachutePreview(Guid windowId, double start, IReadOnlyList<ParachuteRelease> releases)
    {
        RequireParachuteInteraction();
        if (SelectedParachuteWindow != windowId || !parachuteWindows.Any(w => w.Id == windowId))
            throw new ArgumentException("Select the drop window before previewing its move.", nameof(windowId));
        if (releases.Count + parachuteWindows.Where(w => w.Id != windowId).Sum(w => w.Releases.Count) > MaximumParachuteReleases)
            throw new ArgumentException("The preview exceeds its release-marker allowance.", nameof(releases));
        var preview = new ParachuteWindowOverlay(windowId, start, releases);
        ValidateParachuteWindow(preview, parachutePath[0].Phase, parachutePath[^1].Phase);
        parachutePreview = preview with { Releases = releases.ToArray() };
        parachuteMarkerPose = null; UpdateParachuteMarkers();
    }

    public void ClearParachutePreview()
    {
        IsParachutePointerMoving = false; parachutePreview = null;
        parachuteMarkerPose = null; UpdateParachuteMarkers();
    }

    private void ResetParachuteSelection()
    {
        IsParachutePointerMoving = false; parachutePreview = null; SelectedParachuteWindow = null;
        parachuteMarkerPose = null; UpdateParachuteMarkers(); ParachuteWindowSelected?.Invoke(null);
    }

    public void SetParachutePointerMove(bool active)
    {
        if (!active) { IsParachutePointerMoving = false; return; }
        RequireParachuteInteraction();
        var window = EffectiveParachuteWindows().FirstOrDefault(w => w.Id == SelectedParachuteWindow)
            ?? throw new InvalidOperationException("Select a drop window before moving it.");
        CancelNavigation(); StopCameraMotion(); parachutePointerSegment = ParachuteSegment(window.Start);
        parachutePointerPhase = window.Start; parachutePointerDirection = 0;
        parachutePointerOffset = default; parachutePresentationRebase = false;
        IsParachutePointerMoving = true; viewport.Focus();
    }

    private IEnumerable<ParachuteWindowOverlay> EffectiveParachuteWindows() => parachuteWindows.Select(w => parachutePreview is { } preview && preview.Id == w.Id ? preview : w);
    private int ParachuteSegment(double phase)
    {
        int lower = 0, upper = parachutePath.Length - 1;
        while (lower + 1 < upper) { int middle = (lower + upper) / 2; if (parachutePath[middle].Phase <= phase) lower = middle; else upper = middle; }
        return Math.Min(lower, parachutePath.Length - 2);
    }
    private Vector3 ParachutePosition(double phase)
    {
        int index = ParachuteSegment(phase); var a = parachutePath[index]; var b = parachutePath[index + 1];
        return Vector3.Lerp(a.Position, b.Position, (float)Math.Clamp((phase - a.Phase) / (b.Phase - a.Phase), 0, 1));
    }
    private void UpdateParachuteMarkers()
    {
        if (parachuteOverlay == null) return;
        var pose = CaptureView();
        if (parachuteMarkerPose == pose && parachuteMarkerWidth == viewport.ActualWidth && parachuteMarkerHeight == viewport.ActualHeight) return;
        parachuteMarkerPose = pose; parachuteMarkerWidth = viewport.ActualWidth; parachuteMarkerHeight = viewport.ActualHeight;
        var windows = EffectiveParachuteWindows().ToArray();
        Matrix4x4 Instance(Vector3 position, double pixels) => Matrix4x4.CreateScale(AiScale(position, pixels)) * Matrix4x4.CreateTranslation(position);
        parachuteStarts!.Instances = windows.Where(w => w.Id != SelectedParachuteWindow).Select(w => Instance(ParachutePosition(w.Start), 6)).ToArray();
        parachuteReleases!.Instances = windows.Where(w => w.Id != SelectedParachuteWindow).SelectMany(w => w.Releases).Select(r => Instance(r.Position, 3.5)).ToArray();
        var selected = windows.FirstOrDefault(w => w.Id == SelectedParachuteWindow);
        parachuteSelected!.Instances = selected == null ? [] : selected.Releases.Select(r => Instance(r.Position, 5))
            .Prepend(Instance(ParachutePosition(selected.Start), 8)).ToArray();
        viewport.InvalidateRender();
    }

    private bool ProjectParachute(Vector3 position, out Point screen)
    {
        screen = default;
        if (viewport.Camera is not ProjectionCamera camera || viewport.ActualWidth <= 0 || viewport.ActualHeight <= 0) return false;
        // Match TryNavigationRay's current-camera projection; the render context may still hold the previous frame.
        var forward = camera.LookDirection; forward.Normalize();
        var right = Vector3D.CrossProduct(forward, camera.UpDirection); right.Normalize();
        var up = Vector3D.CrossProduct(right, forward);
        var offset = new Point3D(position.X, position.Y, position.Z) - camera.Position;
        double depth = Vector3D.DotProduct(offset, forward);
        // Orthographic overlay geometry remains visible on either side of the eye plane when depth clipping is disabled.
        if (camera is not OrthographicCamera && depth <= 0) return false;
        var projection = camera.CreateProjectionMatrix(Aspect);
        double divisor = camera is OrthographicCamera ? 1 : depth;
        double x = Vector3D.DotProduct(offset, right) * projection.M11 / divisor;
        double y = Vector3D.DotProduct(offset, up) * projection.M22 / divisor;
        screen = new((x + 1) * viewport.ActualWidth / 2, (1 - y) * viewport.ActualHeight / 2);
        return double.IsFinite(screen.X) && double.IsFinite(screen.Y);
    }
    private bool HandleParachuteDown(Point point, MouseButtonEventArgs e)
    {
        if (parachuteOverlay == null || !IsInsidePickupViewport(point)) return false;
        if (e.ChangedButton is not (MouseButton.Left or MouseButton.Right)) return IsParachutePointerMoving;
        if (IsParachutePointerMoving)
        {
            viewport.Focus();
            if (e.ChangedButton == MouseButton.Left) ParachuteConfirmRequested?.Invoke(); else ParachuteCancelRequested?.Invoke();
            return true;
        }
        if (parachutePreview != null) { viewport.Focus(); return true; }
        if (TerrainBrushActive || ZonePaintActive || AiPathEditingNetwork != null) return false;
        if (e.ChangedButton != MouseButton.Left) return false;
        double nearest = 100; Guid? chosen = null; bool ambiguous = false;
        foreach (var window in EffectiveParachuteWindows())
        foreach (var position in window.Releases.Select(r => r.Position).Prepend(ParachutePosition(window.Start)))
        {
            if (!ProjectParachute(position, out var screen)) continue;
            double distance = (screen - point).LengthSquared;
            if (distance > nearest + .01) continue;
            if (Math.Abs(distance - nearest) <= .01 && chosen != null && chosen != window.Id)
            {
                if (window.Id == SelectedParachuteWindow) { chosen = window.Id; ambiguous = false; }
                else if (chosen != SelectedParachuteWindow) ambiguous = true;
            }
            else { chosen = window.Id; nearest = distance; ambiguous = false; }
        }
        if (chosen == null) return false;
        viewport.Focus();
        if (ambiguous) { Information?.Invoke("Several drop windows meet here. Select the window in the drop-window list to retain its flight phase."); return true; }
        if (!PlacementEditingEnabled || IsFlyActive || IsPickupDragging || HasTransformDraft || TerrainBrushActive || ZonePaintActive || AiPathEditingNetwork != null) return true;
        SelectParachuteWindow(chosen); return true;
    }

    /// <summary>Walk only adjoining segments from the selected phase. Equal-distance crossings keep the current visit.</summary>
    internal bool MoveParachutePointer(Point point)
    {
        if (!IsParachutePointerMoving) return false;
        if (!IsInsidePickupViewport(point)) return false;
        if (parachutePresentationRebase)
        {
            if (ProjectParachute(ParachutePosition(parachutePointerPhase), out var anchor))
            { parachutePointerOffset = anchor - point; parachutePresentationRebase = false; }
            return true;
        }
        point += parachutePointerOffset;
        (double Distance, double Fraction) Project(int index)
        {
            if (!ProjectParachute(parachutePath[index].Position, out var a) || !ProjectParachute(parachutePath[index + 1].Position, out var b)) return (double.PositiveInfinity, 0);
            var edge = b - a;
            double fraction = edge.LengthSquared < 1e-10
                ? Math.Clamp((parachutePointerPhase - parachutePath[index].Phase) / (parachutePath[index + 1].Phase - parachutePath[index].Phase), 0, 1)
                : Math.Clamp(System.Windows.Vector.Multiply(point - a, edge) / edge.LengthSquared, 0, 1);
            return ((point - (a + edge * fraction)).LengthSquared, fraction);
        }
        int current = parachutePointerSegment; var best = Project(current);
        // Strict distance descent can cross an adjoining run of identical world positions, but never
        // skip a moving segment merely because its projection overlaps a different visit or is edge-on.
        for (int count = 0; count < Math.Min(parachutePath.Length, 1024); count++)
        {
            int next = current; var candidate = best; bool ambiguous = false;
            foreach (int direction in new[] { -1, 1 })
            {
                int neighbor = current + direction;
                if (neighbor < 0 || neighbor >= parachutePath.Length - 1) continue;
                int adjacent = direction < 0 ? parachutePreviousMoving[neighbor] : parachuteNextMoving[neighbor];
                if (adjacent < 0) continue;
                var projected = Project(adjacent);
                if (projected.Distance + 1e-8 < candidate.Distance) { next = adjacent; candidate = projected; ambiguous = false; }
                else if (next != current && Math.Abs(projected.Distance - candidate.Distance) <= 1e-8 && projected.Distance + 1e-8 < best.Distance)
                {
                    if (parachutePointerDirection == direction) { next = adjacent; candidate = projected; ambiguous = false; }
                    else if (parachutePointerDirection != Math.Sign(next - current)) ambiguous = true;
                }
            }
            if (ambiguous)
            {
                Information?.Invoke("Both flight phases leave this hover along the same screen path. Use the local start time to choose the intended phase.");
                return true;
            }
            if (next == current) break;
            current = next; best = candidate;
        }
        if (double.IsFinite(best.Distance))
        {
            parachutePointerSegment = current;
            // A screen-space fraction is not a world-segment fraction under perspective projection.
            double fraction = best.Fraction;
            var pose = CaptureView();
            if (pose.Projection != "orthographic" && ProjectParachute(parachutePath[current].Position, out var screenStart) &&
                ProjectParachute(parachutePath[current + 1].Position, out var screenEnd) && (screenEnd - screenStart).LengthSquared >= 1e-10)
            {
                var forward = pose.LookDirection; forward.Normalize();
                double Depth(Vector3 p) => Vector3D.DotProduct(new Point3D(p.X, p.Y, p.Z) - pose.Position, forward);
                double a = Depth(parachutePath[current].Position), b = Depth(parachutePath[current + 1].Position);
                fraction = fraction / b / ((1 - fraction) / a + fraction / b);
            }
            if (double.IsFinite(fraction))
            {
                fraction = Math.Clamp(fraction, 0, 1);
                double phase = parachutePath[current].Phase + fraction * (parachutePath[current + 1].Phase - parachutePath[current].Phase);
                if (Math.Abs(phase - parachutePointerPhase) > 1e-10) parachutePointerDirection = Math.Sign(phase - parachutePointerPhase);
                parachutePointerPhase = phase; ParachuteMoveRequested?.Invoke(current, fraction);
            }
        }
        return true;
    }

    private void ClearParachutes()
    {
        IsParachutePointerMoving = false; parachutePreview = null; parachutePath = []; parachuteWindows = [];
        parachuteNextMoving = []; parachutePreviousMoving = [];
        SelectedParachuteWindow = null; parachuteMarkerPose = null;
        if (parachuteOverlay != null) { viewport.Items.Remove(parachuteOverlay); parachuteOverlay.Children.Clear(); parachuteOverlay.Dispose(); parachuteOverlay = null; }
        parachuteLine?.Dispose(); parachuteStarts?.Dispose(); parachuteReleases?.Dispose(); parachuteSelected?.Dispose();
        parachuteLine = null; parachuteStarts = parachuteReleases = parachuteSelected = null;
        ParachuteWindowSelected?.Invoke(null);
    }
}
