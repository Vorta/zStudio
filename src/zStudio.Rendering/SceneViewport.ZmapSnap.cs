using System.Numerics;
using System.Windows;
using zStudio.Rendering.Direct3D;

namespace zStudio.Rendering;

public sealed partial class SceneViewport
{
    private const double ZmapSnapRadius = 12;
    private const int MaximumZmapSnapPositions = 2_000_000;
    private CancellationTokenSource? zmapSnapBuild;
    private GeometryReservation? zmapSnapStorage;
    private ZmapSnapIndex? zmapSnapIndex;
    public event Action? ZmapSnapChanged;
    public string ZmapSnapStatus => zmapSnapBuild != null ? "preparing" : zmapSnapIndex != null ? "ready" : "unavailable";

    /// <summary>Find the nearest map vertex in the current Top view radius, retaining the supplied height.</summary>
    public bool TrySnapZmapVertex(Vector3 target, out Vector3 snapped)
    {
        snapped = target;
        if (!IsZmapView || zmapSnapIndex == null || !ProjectToView(target, out var screen, out _) ||
            !IsNavigationPointInside(screen) || !TryNavigationRay(default, out var topLeft, out _) ||
            !TryCameraRay(new(viewport.ActualWidth, viewport.ActualHeight), out var bottomRight, out _, requireInside: false))
            return false;
        Rect visible = new(new Point(topLeft.X, topLeft.Z), new Point(bottomRight.X, bottomRight.Z));
        if (!zmapSnapIndex.Nearest(new(target.X, target.Z), UnitsPerPixel * ZmapSnapRadius, visible, out var point)) return false;
        snapped = new(point.X, target.Y, point.Y);
        return true;
    }

    private void ResetZmapSnap()
    {
        var build = zmapSnapBuild;
        zmapSnapBuild = null;
        build?.Cancel();
        zmapSnapIndex = null;
        zmapSnapStorage?.Dispose();
        zmapSnapStorage = null;
    }

    private void RefreshZmapSnap()
    {
        if (!IsZmapView) return;
        ResetZmapSnap();
        _ = PrepareZmapSnapAsync();
    }

    private readonly record struct SnapMesh(Vector3[] Positions, Matrix4x4 Transform, float? MaxY);

    private async Task PrepareZmapSnapAsync()
    {
        using var source = new CancellationTokenSource();
        var storage = new GeometryReservation(this);
        using var geometry = staticGeometry?.Retain();
        zmapSnapBuild = source;
        try
        {
            // Capture only immutable positions and effective renderer transforms on the dispatcher.
            var actors = Mission is { } mission ? mission.PrepareActorIndex(mission.Scene) : null;
            int instances = visiblePlacements.Values.Sum(rows => rows.Length);
            storage.Resize(checked(256L + instances * 80L));
            List<SnapMesh> inputs = new(instances);
            int count = 0;
            foreach (var (mesh, rows) in visiblePlacements)
            {
                if (mesh.IsGuide || !mesh.IsDepthClipEnabled || !SceneRenderer.IsDrawn(view, mesh) ||
                    mesh.Geometry is not { Positions.Length: > 0, Indices.Length: >= 3 } part) continue;
                var world = StaticMeshWorld(mesh);
                for (int i = 0; i < rows.Length; i++)
                {
                    int node = rows[i].NodeIndex;
                    if (IsHorizon(node) || actors?.At(node) != null || actors?.IsAmbiguous(node) == true) continue;
                    if (part.Positions.Length > MaximumZmapSnapPositions - count) return;
                    count += part.Positions.Length;
                    inputs.Add(new(part.Positions, (mesh.Instances is { } current ? current[i] : rows[i].Transform) * world, mesh.HeightClip?.At(i)));
                }
            }
            if (count == 0) return;
            storage.Resize(checked(256L + instances * 80L + count * 8L));
            ZmapSnapChanged?.Invoke();
            var prepared = await Task.Run(() => ZmapSnapIndex.Build(inputs, count, source.Token), source.Token);
            if (zmapSnapBuild != source || prepared == null) return;
            storage.Resize(checked(256L + count * 8L));
            zmapSnapStorage = storage;
            zmapSnapIndex = prepared;
        }
        catch (OperationCanceledException) when (source.IsCancellationRequested) { }
        catch (RenderLimitException) { }
        finally
        {
            if (zmapSnapStorage != storage) storage.Dispose();
            if (zmapSnapBuild == source)
            {
                zmapSnapBuild = null;
                ZmapSnapChanged?.Invoke();
            }
        }
    }

    /// <summary>A balanced k-d tree stored in place. Equal plan positions from different surfaces share one target.</summary>
    private sealed class ZmapSnapIndex(Vector2[] points, int length)
    {
        private static readonly Comparer<Vector2> XOrder = Comparer<Vector2>.Create(static (a, b) =>
            a.X == b.X ? a.Y.CompareTo(b.Y) : a.X.CompareTo(b.X));
        private static readonly Comparer<Vector2> ZOrder = Comparer<Vector2>.Create(static (a, b) =>
            a.Y == b.Y ? a.X.CompareTo(b.X) : a.Y.CompareTo(b.Y));

        public static ZmapSnapIndex? Build(List<SnapMesh> inputs, int capacity, CancellationToken token)
        {
            Vector2[] points = new Vector2[capacity];
            int size = 0;
            foreach (var input in inputs)
            {
                for (int i = 0; i < input.Positions.Length; i++)
                {
                    if ((i & 255) == 0) token.ThrowIfCancellationRequested();
                    var world = Vector3.Transform(input.Positions[i], input.Transform);
                    if (float.IsFinite(world.X) && float.IsFinite(world.Z) && (input.MaxY is not float cutoff || world.Y <= cutoff))
                        points[size++] = new(world.X, world.Z);
                }
            }
            if (size == 0) return null;
            Array.Sort(points, 0, size, XOrder);
            int unique = 1;
            for (int i = 1; i < size; i++)
            {
                if ((i & 255) == 0) token.ThrowIfCancellationRequested();
                if (points[i] != points[unique - 1]) points[unique++] = points[i];
            }
            BuildRange(0, unique, true, sorted: true);
            return new(points, unique);

            void BuildRange(int start, int end, bool x, bool sorted = false)
            {
                if (end - start <= 1) return;
                token.ThrowIfCancellationRequested();
                if (!sorted) Array.Sort(points, start, end - start, x ? XOrder : ZOrder);
                int middle = (start + end) / 2;
                BuildRange(start, middle, !x);
                BuildRange(middle + 1, end, !x);
            }
        }

        public bool Nearest(Vector2 center, double radius, Rect visible, out Vector2 vertex)
        {
            Vector2 best = default;
            bool found = false;
            double distance = radius * radius;
            Search(0, length, true);
            vertex = best;
            return found;

            void Search(int start, int end, bool x)
            {
                if (start == end) return;
                int middle = (start + end) / 2;
                var point = points[middle];
                double dx = (double)center.X - point.X, dz = (double)center.Y - point.Y;
                double squared = dx * dx + dz * dz;
                if (point.X >= visible.Left && point.X < visible.Right && point.Y >= visible.Top && point.Y < visible.Bottom &&
                    (squared < distance || squared == distance && (!found || XOrder.Compare(point, best) < 0)))
                {
                    found = true;
                    distance = squared;
                    best = point;
                }
                double offset = x ? dx : dz;
                double pivot = x ? point.X : point.Y;
                bool visibleLeft = pivot >= (x ? visible.Left : visible.Top);
                bool visibleRight = pivot < (x ? visible.Right : visible.Bottom);
                if (offset <= 0)
                {
                    if (visibleLeft) Search(start, middle, !x);
                    if (visibleRight && offset * offset <= distance) Search(middle + 1, end, !x);
                }
                else
                {
                    if (visibleRight) Search(middle + 1, end, !x);
                    if (visibleLeft && offset * offset <= distance) Search(start, middle, !x);
                }
            }
        }
    }
}
