using System.IO;
using System.Numerics;
using HelixToolkit.Wpf.SharpDX;
using zStudio.Core;

namespace zStudio.Rendering;

public sealed partial class SceneViewport
{
    // Per changed mesh and bounds box: the rows as shown before and the rows the preview put in their place. Clearing restores
    // only rows the preview still owns, so a later Isolate or pickup update is not undone.
    private Dictionary<MeshGeometryModel3D, (ScenePlacement[] Before, ScenePlacement[] Moved)>? objectPreviewMeshes;
    private Dictionary<LineGeometryModel3D, (ScenePlacement[] Before, ScenePlacement[] Moved)>? objectPreviewBounds;
    private Dictionary<ScenePlacement, ScenePlacement>? objectPreviewOriginals;
    private long objectPreviewSerial, objectPreviewToken;
    public int? ObjectTransformPreviewNode { get; private set; }

    /// <summary>
    /// Shows node <paramref name="node"/> and everything below it with the local transform <paramref name="local"/> under its
    /// parent, by moving the drawn instances in place; nothing is rebuilt and no data changes. It lasts until
    /// <see cref="ClearObjectTransformPreview"/> or until the scene is replaced (an edit's rebuilt world).
    /// <paramref name="expected"/> is the local transform the caller believes the node has: when the scene draws it otherwise
    /// (an animation's start pose, or a shear the caller's decomposition drops) nothing is previewed. With
    /// <paramref name="positionOnly"/> the drawn basis is kept and only the translation changes. Returns a token for clearing
    /// this preview, or 0 when nothing is previewed: the node is not drawn, or it or an ancestor has several parents (its
    /// copies stand in different frames).
    /// </summary>
    public long PreviewObjectTransform(int node, Matrix4x4 expected, Matrix4x4 local, bool positionOnly)
    {
        var data = Mission?.Scene ?? PreviewScene;
        if (data == null || node < 0 || node >= data.Nodes.Count || !IsFinite(local) || !IsFinite(expected)) return 0;
        HashSet<int> chain = [];
        for (int at = node; at >= 0 && at < data.Nodes.Count && chain.Add(at); at = data.Nodes[at].Parents.FirstOrDefault(-1))
            if (data.Nodes[at].Parents.Length > 1) return 0;
        Matrix4x4 current;
        try { current = SceneBuilder.LocalTransform(data.Nodes[node]); } catch (InvalidDataException) { return 0; }
        if (!Near(current, expected)) return 0;
        if (positionOnly) { var kept = current; kept.Translation = local.Translation; local = kept; }
        ClearObjectTransformPreview();
        int parent = data.Nodes[node].Parents.FirstOrDefault(-1);
        Matrix4x4 before, after;
        try { before = WorldTransform(data, node); after = local * (parent >= 0 ? WorldTransform(data, parent) : Matrix4x4.Identity); }
        catch (InvalidDataException) { return 0; }
        if (!Matrix4x4.Invert(before, out var inverse) || !IsFinite(inverse)) return 0;
        var delta = inverse * after;
        if (!IsFinite(delta)) return 0;
        HashSet<int> subtree = [];
        Stack<int> pending = new([node]);
        while (pending.TryPop(out int index))
            if (index >= 0 && index < data.Nodes.Count && subtree.Add(index))
                foreach (int child in SceneBuilder.Children(data.Nodes[index])) pending.Push(child);
        // A part also held by a node outside the object is drawn there too, in that holder's frame.
        foreach (int index in subtree)
            if (index != node && data.Nodes[index].Parents.Any(p => !subtree.Contains(p))) return 0;
        Dictionary<ScenePlacement, ScenePlacement> originals = [];
        ScenePlacement Moved(ScenePlacement p)
        {
            if (!subtree.Contains(p.NodeIndex)) return p;
            var moved = p with { Transform = p.Transform * delta };
            originals.TryAdd(moved, p);
            return moved;
        }
        Dictionary<MeshGeometryModel3D, (ScenePlacement[], ScenePlacement[])> meshes = [];
        Dictionary<LineGeometryModel3D, (ScenePlacement[], ScenePlacement[])> boxes = [];
        // Recorded before any row moves, so a failure part way can still be cleared.
        objectPreviewMeshes = meshes; objectPreviewBounds = boxes; objectPreviewOriginals = originals;
        ObjectTransformPreviewNode = node; objectPreviewToken = ++objectPreviewSerial;
        foreach (var (mesh, rows) in placements.ToArray())
        {
            if (!rows.Any(p => subtree.Contains(p.NodeIndex))) continue;
            ScenePlacement[] moved = [.. rows.Select(Moved)];
            meshes[mesh] = (rows, moved);
            placements[mesh] = moved;
            visiblePlacements[mesh] = [.. visiblePlacements.GetValueOrDefault(mesh, rows).Select(Moved)];
            mesh.Instances = visiblePlacements[mesh].Select(p => HorizonTransform(p.NodeIndex, p.Transform)).ToList();
        }
        foreach (var (box, rows) in boundsPlacements.ToArray())
        {
            if (!rows.Any(p => subtree.Contains(p.NodeIndex))) continue;
            ScenePlacement[] moved = [.. rows.Select(Moved)];
            boxes[box] = (rows, moved);
            boundsPlacements[box] = moved;
        }
        RefreshBoundsBoxes();
        if (meshes.Count == 0 && boxes.Count == 0)
        { objectPreviewMeshes = null; objectPreviewBounds = null; objectPreviewOriginals = null; ObjectTransformPreviewNode = null; objectPreviewToken = 0; return 0; }
        RefreshObjectPreview();
        return objectPreviewToken;
    }

    /// <summary>
    /// Puts the instances an object transform preview moved back where the shown scene has them: the preview of
    /// <paramref name="token"/>, or any preview when it is 0. A newer preview, or none, is left alone.
    /// </summary>
    public void ClearObjectTransformPreview(long token = 0)
    {
        if (objectPreviewMeshes == null || token != 0 && token != objectPreviewToken) return;
        var originals = objectPreviewOriginals!;
        ScenePlacement Original(ScenePlacement p) => originals.GetValueOrDefault(p, p);
        foreach (var (mesh, (before, moved)) in objectPreviewMeshes)
        {
            // Another update replaced the rows (pickups rebuild theirs from base placements): the preview is already gone.
            if (!placements.TryGetValue(mesh, out var rows) || !ReferenceEquals(rows, moved)) continue;
            placements[mesh] = before;
            // The visible set may have changed since (Isolate); restore the rows it shows now.
            visiblePlacements[mesh] = [.. visiblePlacements.GetValueOrDefault(mesh, moved).Select(Original)];
            mesh.Instances = visiblePlacements[mesh].Select(p => HorizonTransform(p.NodeIndex, p.Transform)).ToList();
        }
        foreach (var (box, (before, moved)) in objectPreviewBounds ?? [])
            if (boundsPlacements.TryGetValue(box, out var rows) && ReferenceEquals(rows, moved)) boundsPlacements[box] = before;
        RefreshBoundsBoxes();
        objectPreviewMeshes = null; objectPreviewBounds = null; objectPreviewOriginals = null; ObjectTransformPreviewNode = null; objectPreviewToken = 0;
        RefreshObjectPreview();
    }

    private void RefreshObjectPreview()
    {
        ++inspectionSerial;
        RecalculateSceneBounds(); RefreshPickupSelection();
        cameraPoseDirty = true; view.InvalidateRender();
        InspectionChanged?.Invoke();
    }

    /// <summary>
    /// Equal within rounding relative to each row's size: a decomposed and recomposed basis reads back a hair off, while the
    /// translation is the build's own value, so an animation's start pose of even a fraction of a unit is told apart.
    /// </summary>
    private static bool Near(Matrix4x4 a, Matrix4x4 b)
    {
        static bool Row(float ax, float ay, float az, float bx, float by, float bz, float tolerance)
        {
            float size = MathF.Max(1, MathF.Max(MathF.Abs(ax), MathF.Max(MathF.Abs(ay), MathF.Abs(az))));
            return MathF.Abs(ax - bx) <= tolerance * size && MathF.Abs(ay - by) <= tolerance * size && MathF.Abs(az - bz) <= tolerance * size;
        }
        return Row(a.M11, a.M12, a.M13, b.M11, b.M12, b.M13, 1e-3f) && Row(a.M21, a.M22, a.M23, b.M21, b.M22, b.M23, 1e-3f)
            && Row(a.M31, a.M32, a.M33, b.M31, b.M32, b.M33, 1e-3f) && Row(a.M41, a.M42, a.M43, b.M41, b.M42, b.M43, 1e-5f);
    }

    private static bool IsFinite(Matrix4x4 m) =>
        float.IsFinite(m.M11) && float.IsFinite(m.M12) && float.IsFinite(m.M13) && float.IsFinite(m.M14) &&
        float.IsFinite(m.M21) && float.IsFinite(m.M22) && float.IsFinite(m.M23) && float.IsFinite(m.M24) &&
        float.IsFinite(m.M31) && float.IsFinite(m.M32) && float.IsFinite(m.M33) && float.IsFinite(m.M34) &&
        float.IsFinite(m.M41) && float.IsFinite(m.M42) && float.IsFinite(m.M43) && float.IsFinite(m.M44);
}
