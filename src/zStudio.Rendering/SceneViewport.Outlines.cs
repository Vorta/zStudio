using System.Numerics;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Media3D;
using zStudio.Rendering.Direct3D;

namespace zStudio.Rendering;

public sealed partial class SceneViewport
{
    /// <summary>At most this many objects are outlined at once (an animation definition can bind to many).</summary>
    public const int MaximumOutlinedNodes = 64;
    private TopMostGroupNode? outlineOverlay;
    private LineNode? outlineBoxes;
    /// <summary>The roots outlined now (each with what it holds), at most <see cref="MaximumOutlinedNodes"/>.</summary>
    public IReadOnlyList<int> OutlinedNodes { get; private set; } = [];
    /// <summary>How many of the outlined roots show something (hidden objects get no box).</summary>
    public int OutlinedBoxes { get; private set; }

    /// <summary>
    /// Outlines several objects with the selection's yellow box, one box around each root and what it holds, as the view
    /// shows them (hidden and isolated-away objects get none). An empty list removes the outlines. A presentation guide only.
    /// </summary>
    public void SetOutlinedNodes(IReadOnlyList<int> roots)
    {
        OutlinedNodes = roots.Take(MaximumOutlinedNodes).ToArray();
        RefreshOutlines();
    }

    /// <summary>The outlines follow what the view shows (after a hide, an isolation or a moved object).</summary>
    public void RefreshOutlines()
    {
        OutlinedBoxes = 0;
        if (OutlinedNodes.Count == 0 || PreviewScene == null)
        {
            if (outlineBoxes != null) outlineBoxes.Visibility = Visibility.Collapsed;
            view.InvalidateRender(); return;
        }
        // Which root each shown node belongs to, then one pass over the shown meshes.
        Dictionary<int, int> rootOf = [];
        for (int i = 0; i < OutlinedNodes.Count; i++)
        {
            HashSet<int> members = []; Subtree(OutlinedNodes[i], members);
            foreach (int member in members) rootOf.TryAdd(member, i);
        }
        var boxes = Enumerable.Range(0, OutlinedNodes.Count).Select(_ => Rect3D.Empty).ToArray();
        foreach (var mesh in meshes)
        {
            if (mesh.Visibility != Visibility.Visible || !mesh.IsRendering || !visiblePlacements.TryGetValue(mesh, out var items)) continue;
            if (mesh.Geometry is not { } geometry || !staticMeshBounds.TryGetValue(geometry, out var local) || !float.IsFinite(local.Min.X)) continue;
            for (int i = 0; i < items.Length; i++)
            {
                if (IsHorizon(items[i].NodeIndex) || !rootOf.TryGetValue(items[i].NodeIndex, out int box)) continue;
                var transform = ToWpf(mesh.Instances is { } instances && i < instances.Count ? instances[i] : items[i].Transform);
                transform.Append(ToWpf(mesh.Transform ?? Matrix4x4.Identity));
                for (int corner = 0; corner < 8; corner++)
                {
                    var point = transform.Transform(new Point3D((corner & 1) == 0 ? local.Min.X : local.Max.X, (corner & 2) == 0 ? local.Min.Y : local.Max.Y, (corner & 4) == 0 ? local.Min.Z : local.Max.Z));
                    if (double.IsFinite(point.X) && double.IsFinite(point.Y) && double.IsFinite(point.Z)) boxes[box].Union(point);
                }
            }
        }
        List<Vector3> positions = []; List<int> indices = [];
        foreach (var box in boxes.Where(b => !b.IsEmpty))
        {
            OutlinedBoxes++;
            int at = positions.Count;
            for (int corner = 0; corner < 8; corner++)
                positions.Add(new((float)(box.X + ((corner & 1) == 0 ? 0 : box.SizeX)), (float)(box.Y + ((corner & 2) == 0 ? 0 : box.SizeY)), (float)(box.Z + ((corner & 4) == 0 ? 0 : box.SizeZ))));
            foreach (int index in (int[])[0, 1, 1, 3, 3, 2, 2, 0, 4, 5, 5, 7, 7, 6, 6, 4, 0, 4, 1, 5, 2, 6, 3, 7]) indices.Add(at + index);
        }
        if (positions.Count == 0) { if (outlineBoxes != null) outlineBoxes.Visibility = Visibility.Collapsed; view.InvalidateRender(); return; }
        if (outlineBoxes == null)
        {
            outlineOverlay = new() { IsGuide = true }; view.Items.Add(outlineOverlay);
            outlineBoxes = new() { Color = Colors.Gold, Thickness = 1.5, IsHitTestVisible = false };
            outlineOverlay.Children.Add(outlineBoxes);
        }
        outlineBoxes.Geometry = new LineData([.. positions], [.. indices]);
        outlineBoxes.Visibility = Visibility.Visible;
        view.InvalidateRender();
    }

    private void ClearOutlines()
    {
        OutlinedNodes = []; OutlinedBoxes = 0;
        if (outlineOverlay != null) { view.Items.Remove(outlineOverlay); outlineOverlay.Children.Clear(); outlineOverlay.Dispose(); outlineOverlay = null; }
        outlineBoxes?.Dispose(); outlineBoxes = null;
    }
}
