using System.Numerics;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using zStudio.Core;
using zStudio.Rendering.Direct3D;

namespace zStudio.Rendering;

// Activation radii of AI vehicles: presentation only, never part of framing, scene bounds, depth range, scene picking,
// isolation or export (docs/agents/preview.md). The radius is the vehicle's network's activate_rad (AiVehicleActivation).
public sealed partial class SceneViewport
{
    public const int MaximumRenderedActivationRadii = 1024;
    private const int ActivationRingSegments = 96;
    private const double ActivationHandleHitRadius = 12;
    private int[] activationRoots = [];
    private int? activationHandleRoot;
    private string? activationEmphasized;
    private (string Network, float Radius)? activationPreview;
    private GroupNode? activationGroup;
    private TopMostGroupNode? activationOverlay;
    private readonly List<Node> activationNodes = [];
    private readonly List<(int Root, Vector3 Centre, float Radius)> activationRings = [];
    // The handle root's drawn ring, the handle (null without one), its spoke (depth-tested and see-through) and where it is.
    private (Vector3 Centre, float Radius)? activationHandleRing;
    private MeshNode? activationHandle;
    private (LineNode Front, LineNode Behind)? activationSpoke;
    private Vector3 activationHandleDirection = Vector3.UnitX;
    private Vector3? activationHandlePoint;
    private ViewPose? activationHandlePose;
    private double activationHandleWidth, activationHandleHeight;
    // A handle drag: its root, the ring's centre, the radial direction fixed at the start, the last radius and the press point,
    // the screen direction of that radial and world units per DIP there (the fallback when the pointer ray grazes the ring's
    // plane). The radius is a mapping's raw value plus an offset: set at the press so the grab keeps the ring's radius, and
    // rebased whenever the mapping changes (plane hit or screen fallback) or the presentation moves the pointer, so the radius
    // continues from its last value instead of jumping.
    private int activationDragRoot;
    private Vector3 activationDragCentre, activationDragDirection;
    private float activationDragRadius, activationDragScale;
    private double activationDragOffset;
    private Point activationDragPoint;
    private System.Windows.Vector activationDragScreen;
    private bool activationDragMoved, activationDragOnPlane, activationPresentationRebase, activationHover;
    private Cursor? activationPreviousCursor;

    /// <summary>Rings built for the shown vehicles (at most <see cref="MaximumRenderedActivationRadii"/>, the first by root order), drawn while guides show.</summary>
    public int RenderedActivationRadii => activationRings.Count;
    /// <summary>More shown vehicles have a ring than the budget draws.</summary>
    public bool ActivationRadiiTruncated { get; private set; }
    public bool IsActivationRadiusDragging { get; private set; }
    /// <summary>A handle of the view is dragged: a transform handle (<see cref="IsPickupDragging"/>) or the activation-radius handle. Selection, framing, the camera and edits wait for its end.</summary>
    public bool IsHandleDragging => IsPickupDragging || IsActivationRadiusDragging;
    /// <summary>Rings the view draws now: <see cref="RenderedActivationRadii"/>, or none while guides are hidden (Hide UI, ZMAP).</summary>
    public int DrawnActivationRadii => view.GuidesVisible ? activationRings.Count : 0;
    /// <summary>The edit handle is drawn (its ring is drawn and editing, Fly, Hide UI and ZMAP allow it).</summary>
    public bool ActivationRadiusHandleShown => activationHandle != null && view.GuidesVisible;
    /// <summary>The handle's raw radius in world units (1 to 1e6) while it is dragged.</summary>
    public event Action<float>? ActivationRadiusDragged;
    /// <summary>A handle drag ended: true when released, false when cancelled (Escape, lost capture, leaving the view, Fly, read-only editing, teardown).</summary>
    public event Action<bool>? ActivationRadiusDragEnded;

    /// <summary>
    /// The vehicles (actor roots of the shown mission) whose ring is shown, the one whose ring carries the edit handle (null: none)
    /// and the network whose rings are emphasised (null: none). Each ring is the vehicle's network's radius around its rendered
    /// position (a pending move included); a vehicle the view hides, without a network or with radius 0 draws none.
    /// </summary>
    public void SetActivationRadiusPresentation(IReadOnlySet<int> roots, int? handleRoot, string? emphasizedNetwork)
    {
        int[] next = [.. roots.Order()];
        if (next.AsSpan().SequenceEqual(activationRoots) && handleRoot == activationHandleRoot && emphasizedNetwork == activationEmphasized) return;
        activationRoots = next; activationHandleRoot = handleRoot; activationEmphasized = emphasizedNetwork;
        RebuildActivationRadii();
    }

    /// <summary>A draft radius for one network's rings (null network or null radius: none; drawn as its magnitude). Never changes the snapshot.</summary>
    public void SetActivationRadiusPreview(string? networkId, float? radius)
    {
        if (radius is float value && !float.IsFinite(value)) throw new ArgumentException("A finite radius is required.", nameof(radius));
        (string, float)? next = networkId != null && radius is float r ? (networkId, Math.Abs(r)) : null;
        if (next == activationPreview) return;
        activationPreview = next; RebuildActivationRadii();
    }

    /// <summary>Cancels a handle drag (raising <see cref="ActivationRadiusDragEnded"/> with false); false when none is under way.</summary>
    public bool CancelActivationRadiusDrag()
    {
        if (!IsActivationRadiusDragging) return false;
        EndActivationRadiusDrag(); ActivationRadiusDragEnded?.Invoke(false); return true;
    }

    /// <summary>A shown vehicle's ring: its network, radius (the preview's for that network) and rendered centre; null when it draws none.</summary>
    private (Vector3 Centre, float Radius, AiNetwork Network)? ActivationRing(int root)
    {
        if (Mission == null || placementEdits == null || !IsNodeShown(root) || ActorAt(root) is not { CoordinateSource: { } source } actor || actor.Root != root) return null;
        var record = placementEdits.Coordinate(source);
        if (AiVehicleActivation.Resolve(record, AiNetworks) is not { Network: { } network } activation) return null;
        float radius = activationPreview is { } preview && preview.Network == network.Id ? preview.Radius : activation.Radius;
        if (!(radius > 0) || !float.IsFinite(radius)) return null;
        var world = WorldTransform(Mission.Scene, root);
        var delta = placementDeltas.GetValueOrDefault(root, Matrix4x4.Identity);
        // A transform-handle drag of this vehicle moves its ring with the draft, before the card previews it.
        if (IsPickupDragging && transformSource is { } dragged && record != null && ReferenceEquals(placementEdits.Coordinate(dragged), record))
            delta = transformDraft.DeltaFrom(new(actor.PlacementPosition ?? world.Translation, actor.PlacementRotation ?? Vector3.Zero), placementEdits.RotationKind(source));
        var centre = (world * delta).Translation;
        return Finite(centre) && Finite(centre + new Vector3(radius, 0, radius)) ? (centre, radius, network) : null;
    }

    private bool ActivationHandleAllowed => PlacementEditingEnabled && !IsFlyActive && !PresentationUiHidden && !IsZmapView;

    private void RebuildActivationRadii()
    {
        if (activationRoots.Length == 0 && activationRings.Count == 0 && activationNodes.Count == 0 && !IsActivationRadiusDragging) return;
        ClearActivationRadiusNodes();
        activationRings.Clear(); ActivationRadiiTruncated = false; activationHandleRing = null; activationHandlePoint = null;
        Dictionary<(Color Color, double Thickness), List<(Vector3 Centre, float Radius)>> buckets = [];
        (Vector3 Centre, float Radius)? handleRing = null;
        foreach (int root in activationRoots)
        {
            if (ActivationRing(root) is not { } ring) continue;
            if (activationRings.Count == MaximumRenderedActivationRadii) { ActivationRadiiTruncated = true; break; }
            activationRings.Add((root, ring.Centre, ring.Radius));
            if (root == activationHandleRoot) { handleRing = (ring.Centre, ring.Radius); continue; }
            var tint = AiNetworkColors.Color(ring.Network.AttackStrategy);
            var key = (Color.FromRgb((byte)Math.Round(tint.Red * 255), (byte)Math.Round(tint.Green * 255), (byte)Math.Round(tint.Blue * 255)),
                ring.Network.Id == activationEmphasized ? 1.5 : 1.0);
            if (!buckets.TryGetValue(key, out var list)) buckets.Add(key, list = []);
            list.Add((ring.Centre, ring.Radius));
        }
        // The handle's ring is drawn last, over the others.
        if (handleRing is { } handled) buckets[(Colors.White, 1.5)] = [handled];
        if (buckets.Count > 0)
        {
            activationGroup = new() { IsGuide = true }; view.Items.Add(activationGroup);
            activationOverlay = new() { IsGuide = true }; view.Items.Add(activationOverlay);
            foreach (var ((color, thickness), rings) in buckets) ActivationLines(ActivationRingGeometry(rings), color, thickness);
        }
        activationHandleRing = handleRing;
        if (handleRing != null && ActivationHandleAllowed)
        {
            activationSpoke = ActivationLines(new([handleRing.Value.Centre, handleRing.Value.Centre], [0, 1]), Colors.White, .75);
            var material = PreviewMaterials.Create(true); material.DiffuseColor = new(1, 1, 1, 1); material.Unlit = true;
            activationHandle = new() { Geometry = ActivationHandleDisc(), Material = material, IsTransparent = true, IsHitTestVisible = false,
                CullMode = CullMode.None, RenderOrder = 10, IsDepthClipEnabled = false };
            activationNodes.Add(activationHandle); activationOverlay!.Children.Add(activationHandle);
            UpdateActivationRadiusHandle();
        }
        else SetActivationRadiusHover(false);
        view.InvalidateRender();
        // A drag needs its ring: the vehicle hidden, its network gone or another handle root ends it.
        if (IsActivationRadiusDragging && (handleRing == null || activationHandleRoot != activationDragRoot)) CancelActivationRadiusDrag();
    }

    /// <summary>A depth-tested line and its see-through twin at <see cref="AiBehindGeometry"/> strength.</summary>
    private (LineNode Front, LineNode Behind) ActivationLines(LineData geometry, Color color, double thickness)
    {
        LineNode Line(byte alpha, GroupNode group)
        {
            var line = new LineNode { Geometry = geometry, Color = Color.FromArgb(alpha, color.R, color.G, color.B), Thickness = thickness,
                IsHitTestVisible = false, IsDepthClipEnabled = false, RenderOrder = 10 };
            activationNodes.Add(line); group.Children.Add(line); return line;
        }
        return (Line(color.A, activationGroup!), Line((byte)Math.Round(color.A * AiBehindGeometry), activationOverlay!));
    }

    private static LineData ActivationRingGeometry(List<(Vector3 Centre, float Radius)> rings)
    {
        var positions = new Vector3[rings.Count * ActivationRingSegments];
        var indices = new int[positions.Length * 2];
        for (int r = 0; r < rings.Count; r++)
        {
            var (centre, radius) = rings[r];
            int first = r * ActivationRingSegments;
            for (int i = 0; i < ActivationRingSegments; i++)
            {
                float angle = i * MathF.Tau / ActivationRingSegments;
                positions[first + i] = centre + new Vector3(MathF.Cos(angle) * radius, 0, MathF.Sin(angle) * radius);
                indices[2 * (first + i)] = first + i; indices[2 * (first + i) + 1] = first + (i + 1) % ActivationRingSegments;
            }
        }
        return new(positions, indices);
    }

    private static MeshData ActivationHandleDisc()
    {
        const int segments = 32;
        List<Vector3> p = [Vector3.Zero]; List<int> indices = [];
        for (int i = 0; i < segments; i++)
        {
            float a = i * MathF.Tau / segments;
            p.Add(new(MathF.Cos(a), MathF.Sin(a), 0));
            indices.AddRange([0, 1 + i, 1 + (i + 1) % segments]);
        }
        return new([.. p], [.. indices], [.. p.Select(_ => Vector3.UnitZ)]);
    }

    /// <summary>
    /// Places the handle on its ring where the radial lies across the screen (the camera's right on XZ), or opposite when only
    /// that side is in view; a drag keeps its direction. Per frame, cached by pose and size.
    /// </summary>
    private void UpdateActivationRadiusHandle()
    {
        if (activationHandle == null || activationHandleRing is not { } ring) return;
        var pose = CaptureView();
        if (activationHandlePose == pose && activationHandleWidth == viewport.ActualWidth && activationHandleHeight == viewport.ActualHeight) return;
        activationHandlePose = pose; activationHandleWidth = viewport.ActualWidth; activationHandleHeight = viewport.ActualHeight;
        var direction = activationDragDirection;
        if (!IsActivationRadiusDragging)
        {
            var forward = new Vector3((float)pose.LookDirection.X, (float)pose.LookDirection.Y, (float)pose.LookDirection.Z);
            var up = new Vector3((float)pose.UpDirection.X, (float)pose.UpDirection.Y, (float)pose.UpDirection.Z);
            var right = Vector3.Cross(forward, up); right.Y = 0;
            direction = right.LengthSquared() > 1e-12f && Finite(right) ? Vector3.Normalize(right) : Vector3.UnitX;
            bool Inside(Vector3 point) => ProjectParachute(point, out var screen) && IsInsidePickupViewport(screen);
            if (!Inside(ring.Centre + direction * ring.Radius) && Inside(ring.Centre - direction * ring.Radius)) direction = -direction;
        }
        activationHandleDirection = direction;
        var handle = ring.Centre + direction * ring.Radius;
        activationHandlePoint = handle;
        activationHandle.Instances = [AiMarkerTransform(handle, 7)];
        if (activationSpoke is ({ } front, { } behind))
        {
            var spoke = new LineData([ring.Centre, handle], [0, 1]);
            front.Geometry = spoke; behind.Geometry = spoke;
        }
        view.InvalidateRender();
    }

    /// <summary>Whether a view point (DIPs) is within 12 DIP of the drawn handle, when editing tools and modes allow it.</summary>
    internal bool PickActivationRadiusHandle(Point point)
    {
        if (activationHandle == null || PresentationUiHidden || !PlacementEditingEnabled || IsFlyActive || IsPickupDragging || IsZmapView ||
            AiPathEditingNetwork != null || ZonePaintActive || TerrainBrushActive || worldAnimation != null || !IsInsidePickupViewport(point)) return false;
        UpdateActivationRadiusHandle();
        return activationHandlePoint is { } handle && ProjectParachute(handle, out var screen)
            && (screen - point).LengthSquared <= ActivationHandleHitRadius * ActivationHandleHitRadius;
    }

    internal bool HandleActivationRadiusPointerDown(Point point, MouseButtonEventArgs e)
    {
        if (IsActivationRadiusDragging) return true;
        if (e.ChangedButton != MouseButton.Left || !PickActivationRadiusHandle(point)) return false;
        SetActivationRadiusHover(false);
        BeginActivationRadiusDrag(point);
        return true;
    }

    private void BeginActivationRadiusDrag(Point point)
    {
        if (activationHandleRing is not { } ring || activationHandlePoint is not { } handle || activationHandleRoot is not int root) return;
        StopNavigationMotion();
        pickupCameraPose = CaptureView(); pickupCameraInertia = navigationInertia; navigationInertia = false;
        activationDragRoot = root; activationDragCentre = ring.Centre; activationDragDirection = activationHandleDirection;
        activationDragRadius = ring.Radius; activationDragPoint = point; activationDragMoved = false;
        activationDragScale = AiScale(handle, 1); activationPresentationRebase = false;
        activationDragScreen = ProjectParachute(ring.Centre, out var centre) && ProjectParachute(handle, out var edge) && (edge - centre).LengthSquared > 1e-6
            ? (edge - centre) / (edge - centre).Length : new(1, 0);
        // Anchored to the grab: the radius under the press point (anywhere in the hit disc) is the ring's radius.
        var grab = ActivationDragRaw(point);
        activationDragOnPlane = grab?.Plane ?? false;
        activationDragOffset = ring.Radius - (grab?.Radius ?? 0);
        IsActivationRadiusDragging = true;
    }

    /// <summary>
    /// A raw radius under a view point, up to the drag's offset: the pointer ray's hit on the ring's plane along the drag's radial
    /// (<c>Plane</c>); when the ray nearly parallels the plane, meets it behind the eye, or lands farther out than any radius the
    /// drag allows, the screen movement from the press point along the radial (world units per DIP at the handle). Null when not finite.
    /// </summary>
    private (double Radius, bool Plane)? ActivationDragRaw(Point point)
    {
        if (TryNavigationRay(point, out var origin, out var ray) && Math.Abs(ray.Y) >= .02)
        {
            double distance = (activationDragCentre.Y - origin.Y) / ray.Y;
            if (view.Camera is not PerspectiveRenderCamera || distance >= 0)
            {
                double x = origin.X + ray.X * distance - activationDragCentre.X, z = origin.Z + ray.Z * distance - activationDragCentre.Z;
                double radius = x * activationDragDirection.X + z * activationDragDirection.Z;
                if (double.IsFinite(radius) && Math.Abs(radius) <= 1e6) return (radius, true);
            }
        }
        double screen = System.Windows.Vector.Multiply(point - activationDragPoint, activationDragScreen) * activationDragScale;
        return double.IsFinite(screen) ? (screen, false) : null;
    }

    internal bool HandleActivationRadiusPointerMove(Point point)
    {
        if (!IsActivationRadiusDragging)
        {
            // Transform handles keep priority where they overlap; the handle wins over AI node hover.
            bool hover = activationHandle != null && Mouse.LeftButton == MouseButtonState.Released && Mouse.MiddleButton == MouseButtonState.Released &&
                Mouse.RightButton == MouseButtonState.Released && PickActivationRadiusHandle(point) && PickPickupHandle(point) == null;
            if (hover)
            {
                SetPickupHover(false);
                if (hoveredAiNode != null) { hoveredAiNode = null; PublishAiLabel(); }
            }
            SetActivationRadiusHover(hover);
            return hover;
        }
        if (!IsInsidePickupViewport(point))
        {
            // A smaller presentation viewport can leave the stationary pointer outside: wait for its first usable point.
            if (!activationPresentationRebase) CancelActivationRadiusDrag();
            return true;
        }
        // Capture can resend the press position: a click without movement changes nothing.
        if (!activationDragMoved && point == activationDragPoint) return true;
        activationDragMoved = true;
        if (ActivationDragRaw(point) is not { } raw) return true;
        // A moved presentation or the other mapping: the radius continues from its last value.
        if (activationPresentationRebase || raw.Plane != activationDragOnPlane)
        {
            activationDragOffset = activationDragRadius - raw.Radius; activationDragOnPlane = raw.Plane;
            if (activationPresentationRebase) { activationPresentationRebase = false; return true; }
        }
        double radius = raw.Radius + activationDragOffset;
        if (!double.IsFinite(radius)) return true;
        activationDragRadius = (float)Math.Clamp(radius, 1, 1e6);
        ActivationRadiusDragged?.Invoke(activationDragRadius);
        return true;
    }

    internal bool HandleActivationRadiusPointerUp(Point point, MouseButtonEventArgs e)
    {
        if (!IsActivationRadiusDragging || e.ChangedButton != MouseButton.Left) return false;
        if (!IsInsidePickupViewport(point)) { CancelActivationRadiusDrag(); return true; }
        EndActivationRadiusDrag(); ActivationRadiusDragEnded?.Invoke(true);
        SetActivationRadiusHover(PickActivationRadiusHandle(point));
        return true;
    }

    private void EndActivationRadiusDrag()
    {
        IsActivationRadiusDragging = false; activationPresentationRebase = false;
        ResumePickupCamera();
        if (viewport.IsMouseCaptured) viewport.ReleaseMouseCapture();
        // The handle may take the other side again.
        activationHandlePose = null; UpdateActivationRadiusHandle();
    }

    private void SetActivationRadiusHover(bool value)
    {
        if (activationHover == value) return;
        activationHover = value;
        if (value) { activationPreviousCursor = viewport.Cursor; viewport.Cursor = Cursors.Hand; }
        else { viewport.Cursor = activationPreviousCursor; activationPreviousCursor = null; }
    }

    private void ClearActivationRadiusNodes()
    {
        if (activationGroup != null) { view.Items.Remove(activationGroup); activationGroup.Children.Clear(); activationGroup.Dispose(); activationGroup = null; }
        if (activationOverlay != null) { view.Items.Remove(activationOverlay); activationOverlay.Children.Clear(); activationOverlay.Dispose(); activationOverlay = null; }
        foreach (var node in activationNodes) node.Dispose();
        activationNodes.Clear(); activationHandle = null; activationSpoke = null; activationHandlePose = null;
    }

    private void ClearActivationRadii()
    {
        CancelActivationRadiusDrag(); SetActivationRadiusHover(false);
        activationRoots = []; activationHandleRoot = null; activationEmphasized = null; activationPreview = null;
        ClearActivationRadiusNodes();
        activationRings.Clear(); ActivationRadiiTruncated = false; activationHandleRing = null; activationHandlePoint = null;
    }
}
