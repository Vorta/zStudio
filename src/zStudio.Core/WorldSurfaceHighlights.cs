namespace Recoil.Zbd.Core;

public enum WorldHighlightMode { None, NonDefaultSoils, CanModify, ClipTo }

[Flags]
public enum WorldSurfaceKind { Default = 0, NonDefaultSoil = 1, CanModify = 2, ClipTo = 4 }

/// <summary>Stored material/node semantics, independent of rendering and gameplay simulation.</summary>
public static class WorldSurfaceHighlights
{
    // Retail Class.c 0x447D20/0x447D70; cls_util.c names these CanModify/ClipTo.
    public static WorldSurfaceKind NodeKind(GameScene scene, int nodeIndex)
    {
        if (nodeIndex < 0 || nodeIndex >= scene.Nodes.Count) return WorldSurfaceKind.Default;
        uint flags = scene.Nodes[nodeIndex].Metadata.UInt("flags");
        return ((flags & 0x10000) != 0 ? WorldSurfaceKind.CanModify : 0) |
            ((flags & 0x20000) != 0 ? WorldSurfaceKind.ClipTo : 0);
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
        _ => throw new ArgumentOutOfRangeException(nameof(mode))
    };
}
