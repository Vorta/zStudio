using System.Numerics;
namespace Recoil.Zbd.Core.Worlds;

/// <summary>Finite stored and derived values required by the source compiler and world writer.</summary>
internal static class WorldNumbers
{
    internal static float Finite(float value)
    {
        if (!float.IsFinite(value)) throw new InvalidDataException("A world number or derived value exceeds the finite float range.");
        return value;
    }
    internal static void Vector(Vector3 v) { Finite(v.X); Finite(v.Y); Finite(v.Z); }
    internal static void Box(WorldBox box) { Vector(box.Min); Vector(box.Max); }
    internal static void Model(WorldModel model)
    {
        Vector(model.BoundsCentre); Finite(model.BoundsRadius); Finite(model.MorphFactor); Finite(model.ScrollU); Finite(model.ScrollV);
        foreach (var v in model.Vertices) Vector(v);
        foreach (var v in model.Normals) Vector(v);
        foreach (var v in model.Morphs) Vector(v);
        foreach (var p in model.Points) foreach (var v in p.Vertices) Vector(v);
        foreach (var p in model.Polygons) foreach (var uv in p.Uvs) { Finite(uv.X); Finite(uv.Y); }
    }
    internal static void Node(WorldNode node)
    {
        Box(node.CachedBounds); Box(node.PrimaryBounds); Box(node.SecondaryBounds);
        switch (node.Class)
        {
            case WorldNodeClass.Object3D: Range(0x18, 0x5C); break;
            case WorldNodeClass.World: Range(0x14, 0x48); Range(0x54, 0x74); Range(0x84, 0x8C); break;
            case WorldNodeClass.Camera: Range(176, 180); Range(208, 244); Range(468, 472); break;
            case WorldNodeClass.Light: Range(8, 28); Range(164, 180); Range(204, 216); break;
            case WorldNodeClass.Display: Range(16, 24); break;
        }
        void Range(int first, int last) { for (int i = first; i <= last; i += 4) Finite(node.PayloadFloat(i)); }
    }
}
