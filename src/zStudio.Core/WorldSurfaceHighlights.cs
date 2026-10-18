namespace zStudio.Core;

/// <summary>Zones tints by each node's (object's) zone; PolygonZones by each polygon's zone set, striped where it has several.</summary>
public enum WorldHighlightMode { None, NonDefaultSoils, CanModify, ClipTo, Zones, CanModifyAndClipTo, Landmarks, PolygonZones }

[Flags]
public enum WorldSurfaceKind { Default = 0, NonDefaultSoil = 1, CanModify = 2, ClipTo = 4, Landmark = 8 }

/// <summary>Stored material/node semantics, independent of rendering and gameplay simulation.</summary>
public static class WorldSurfaceHighlights
{
    // Retail Class.c 0x447D20/0x447D70; cls_util.c names these CanModify/ClipTo.
    public static WorldSurfaceKind NodeKind(GameScene scene, int nodeIndex)
    {
        if (nodeIndex < 0 || nodeIndex >= scene.Nodes.Count) return WorldSurfaceKind.Default;
        uint flags = scene.Nodes[nodeIndex].Metadata.UInt("flags");
        return ((flags & 0x10000) != 0 ? WorldSurfaceKind.CanModify : 0) |
            ((flags & 0x20000) != 0 ? WorldSurfaceKind.ClipTo : 0) |
            (IsLandmark(scene, nodeIndex) ? WorldSurfaceKind.Landmark : 0);
    }

    /// <summary>
    /// Whether a node is a landmark (0x80, never culled) or lies below one: a landmark object's geometry is drawn with it.
    /// The walk over stored parents is bounded and cycle-safe.
    /// </summary>
    public static bool IsLandmark(GameScene scene, int nodeIndex)
    {
        Stack<int> pending = new([nodeIndex]); HashSet<int> seen = [];
        while (pending.TryPop(out int node) && seen.Count < 4096)
        {
            if (node < 0 || node >= scene.Nodes.Count || !seen.Add(node)) continue;
            if ((scene.Nodes[node].Metadata.UInt("flags") & 0x80) != 0) return true;
            foreach (int parent in scene.Nodes[node].Parents) pending.Push(parent);
        }
        return false;
    }

    /// <summary>A node's zone (the low byte of its zone word; 255 admits every zone).</summary>
    public static int NodeZone(GameScene scene, int nodeIndex) =>
        nodeIndex < 0 || nodeIndex >= scene.Nodes.Count ? 0xFF : (int)(scene.Nodes[nodeIndex].Metadata.Int("zone_id", 0xFF) & 0xFF);
    /// <summary>
    /// A distinct colour for each zone, spread around the hue circle by the golden ratio so neighbouring numbers differ;
    /// zone 255 ("any") is pale grey.
    /// </summary>
    public static (float R, float G, float B) ZoneColor(int zone)
    {
        if (zone == 0xFF) return (0.8f, 0.8f, 0.8f);
        double hue = (zone * 0.618033988749895) % 1.0 * 6;
        int sector = (int)hue; float f = (float)(hue - sector), v = 0.95f, s = 0.75f;
        float p = v * (1 - s), q = v * (1 - s * f), t = v * (1 - s * (1 - f));
        return sector switch { 0 => (v, t, p), 1 => (q, v, p), 2 => (p, v, t), 3 => (p, q, v), 4 => (t, p, v), _ => (v, p, q) };
    }

    public static WorldSurfaceKind Classify(GameScene scene, int nodeIndex, int materialIndex) =>
        NodeKind(scene, nodeIndex) | (materialIndex >= 0 && materialIndex < scene.Materials.Count &&
            scene.Materials[materialIndex].UInt("soil") != 0 ? WorldSurfaceKind.NonDefaultSoil : 0);

    public static bool Matches(WorldSurfaceKind kind, WorldHighlightMode mode) => mode switch
    {
        WorldHighlightMode.None => false,
        WorldHighlightMode.NonDefaultSoils => (kind & WorldSurfaceKind.NonDefaultSoil) != 0,
        WorldHighlightMode.CanModify => (kind & WorldSurfaceKind.CanModify) != 0,
        WorldHighlightMode.ClipTo => (kind & WorldSurfaceKind.ClipTo) != 0,
        WorldHighlightMode.CanModifyAndClipTo => (kind & (WorldSurfaceKind.CanModify | WorldSurfaceKind.ClipTo)) != 0,
        WorldHighlightMode.Zones => true,
        WorldHighlightMode.PolygonZones => false, // per polygon, not per node
        WorldHighlightMode.Landmarks => (kind & WorldSurfaceKind.Landmark) != 0,
        _ => throw new ArgumentOutOfRangeException(nameof(mode))
    };
}
