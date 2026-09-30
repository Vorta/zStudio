using System.Buffers.Binary;
using System.Numerics;

namespace Recoil.Zbd.Core.Worlds;

/// <summary>
/// Computes what the engine derives before it writes a world (CZClass::gwNodeUpdateAll): model bounds and spheres,
/// node bounds from their models and children, and the world's area partition with each area's height range. The
/// loader recomputes none of it, so a compiled world must carry these values.
/// </summary>
public static class WorldUpdate
{
    public const uint ActiveFlag = 0x04, LandmarkFlag = 0x80, CachedBoundsFlag = 0x100, ModelBoundsFlag = 0x200, ChildBoundsFlag = 0x400;

    /// <summary>zDi::RebuildBounds (retail 0x483AD0): the model's box (vertices, points, blended vertices; facades symmetric about the origin) and approximate sphere.</summary>
    public static WorldBox ModelBounds(WorldModel model)
    {
        WorldBox box;
        if (model.Vertices.Count > 0) box = new(model.Vertices[0], model.Vertices[0]);
        else if (model.Points.FirstOrDefault(p => p.Vertices.Length > 0) is { } first) box = new(first.Vertices[0], first.Vertices[0]);
        else box = WorldBox.Empty;
        Vector3 min = box.Min, max = box.Max;
        void Add(Vector3 p) { min = Vector3.Min(min, p); max = Vector3.Max(max, p); }
        foreach (var point in model.Points) foreach (var v in point.Vertices) Add(v);
        for (int i = 1; i < model.Vertices.Count; i++) Add(model.Vertices[i]);
        for (int i = 0; i < model.Morphs.Count && i < model.Vertices.Count; i++) Add(model.Vertices[i] + model.Morphs[i]);
        if (model.Mode == 1)
        {
            // BuildOriginSymmetricAabb (retail 0x483E60).
            Vector3 extent = new(Math.Max(max.X, Math.Abs(min.X)), Math.Max(max.Y, Math.Abs(min.Y)), Math.Max(max.Z, Math.Abs(min.Z)));
            if ((model.Flags & 0x10) != 0) { float e = Math.Max(extent.X, Math.Max(extent.Y, extent.Z)); extent = new(e); }
            else { float xz = Math.Max(extent.X, extent.Z); extent = new(xz, extent.Y, xz); }
            min = -extent; max = extent;
        }
        return new(min, max);
    }

    public static void RebuildModel(WorldModel model)
    {
        var box = ModelBounds(model);
        (model.BoundsCentre, model.BoundsRadius) = Sphere(box);
    }

    /// <summary>The engine's centre and approximate radius (MinMaxToBoundingSphere, retail 0x4525D0): the radius halves the float's exponent bits.</summary>
    /// <remarks>The engine keeps the half extents in x87 registers, so they are computed here at double precision and rounded once.</remarks>
    public static (Vector3 Centre, float Radius) Sphere(WorldBox box)
    {
        double hx = ((double)box.Max.X - box.Min.X) * 0.5, hy = ((double)box.Max.Y - box.Min.Y) * 0.5, hz = ((double)box.Max.Z - box.Min.Z) * 0.5;
        float squared = (float)(hx * hx + hy * hy + hz * hz);
        int bits = (BitConverter.SingleToInt32Bits(squared) >> 1) + 0x1FC00000;
        return (new((float)(hx + box.Min.X), (float)(hy + box.Min.Y), (float)(hz + box.Min.Z)), BitConverter.Int32BitsToSingle(bits));
    }

    /// <summary>A node's local transform as gwNodeGetWorldBBoxCorners applies it: object3d data unless its identity flag (0x08) is set.</summary>
    public static Matrix4x4? LocalMatrix(WorldNode node)
    {
        if (node.Class != WorldNodeClass.Object3D || (BinaryPrimitives.ReadUInt32LittleEndian(node.Payload) & 0x08) != 0) return null;
        float F(int i) => BinaryPrimitives.ReadSingleLittleEndian(node.Payload.AsSpan(0x30 + i * 4));
        // Rows: X basis, Y basis, Z basis, translation; a point maps as p' = p.x·X + p.y·Y + p.z·Z + T.
        return new(F(0), F(1), F(2), 0, F(3), F(4), F(5), 0, F(6), F(7), F(8), 0, F(9), F(10), F(11), 1);
    }

    /// <summary>The eight corners of a node's cached box in its parent's space.</summary>
    public static IEnumerable<Vector3> ParentCorners(WorldNode node)
    {
        var matrix = LocalMatrix(node);
        return node.CachedBounds.Corners().Select(c => matrix is { } m ? Vector3.Transform(c, m) : c);
    }

    /// <summary>
    /// Recomputes every node's model, child and cached bounds bottom-up (gwNodeUpdateDisplayInstance,
    /// gwNodeComputeChildBBox, gwNodeRecalcBBox) and every model's sphere. World nodes keep no bounds.
    /// </summary>
    public static void RebuildBounds(GameZWorld world)
    {
        foreach (var model in world.Models) RebuildModel(model);
        HashSet<WorldNode> done = new(ReferenceEqualityComparer.Instance), visiting = new(ReferenceEqualityComparer.Instance);
        foreach (var node in world.Nodes) Visit(node);
        void Visit(WorldNode node)
        {
            if (done.Contains(node)) return;
            if (!visiting.Add(node)) throw new InvalidDataException($"Node {node.Name} is its own descendant.");
            foreach (var child in node.Children) Visit(child);
            visiting.Remove(node); done.Add(node);
            // Only geometry nodes are updated; lights, cameras, windows and displays keep the bounds they were created with.
            if (node.Class is not (WorldNodeClass.Object3D or WorldNodeClass.Lod or WorldNodeClass.Plain)) return;
            uint flags = node.Flags & ~(CachedBoundsFlag | ModelBoundsFlag | ChildBoundsFlag);
            WorldBox? primary = null, secondary = null;
            if (node.Model != null) { primary = ModelBounds(node.Model); flags |= ModelBoundsFlag; node.PrimaryBounds = primary.Value; }
            foreach (var child in node.Children.Where(c => (c.Flags & CachedBoundsFlag) != 0))
            {
                var box = WorldBox.Of(ParentCorners(child));
                secondary = secondary is { } s ? s.Union(box) : box;
            }
            if (secondary is { } sb) { flags |= ChildBoundsFlag; node.SecondaryBounds = sb; }
            WorldBox? cached = primary is { } p ? secondary is { } s2 ? p.Union(s2) : p : secondary;
            if (cached is { } c) { flags |= CachedBoundsFlag; node.CachedBounds = c; node.BoundsFlags |= 4; }
            node.Flags = flags;
        }
    }

    /// <summary>World record fields the partition uses (CZWorldDataPartial).</summary>
    private readonly record struct Grid(float OriginX, float OriginZ, float MaxX, float MaxZ, float CellX, float CellZ, float InverseX, float InverseZ, float ToleranceX, float ToleranceZ, int Columns, int Rows);
    private static Grid ReadGrid(WorldNode world)
    {
        float F(int o) => world.PayloadFloat(o);
        return new(F(0x34), F(0x38), F(0x44), F(0x48), F(0x54), F(0x58), F(0x64), F(0x68), F(0x70), F(0x74), world.PayloadInt(0x78), world.PayloadInt(0x7C));
    }

    /// <summary>
    /// WorldSetVirtualAreaPartition (retail 0x450C60): cell size, halves, inverses, radius bias, grid size and each area's
    /// rectangle and sphere; it resets the inclusion tolerance to an eighth of a cell, so a later tolerance command wins.
    /// </summary>
    public static void SetPartition(WorldNode world, float cellX, float cellZ)
    {
        world.SetPayloadFloat(0x54, cellX); world.SetPayloadFloat(0x58, cellZ);
        world.SetPayloadFloat(0x70, cellX * 0.125f); world.SetPayloadFloat(0x74, cellZ * -0.125f);
        world.SetPayloadFloat(0x5C, cellX * 0.5f); world.SetPayloadFloat(0x60, cellZ * 0.5f);
        world.SetPayloadFloat(0x64, 1.0f / cellX); world.SetPayloadFloat(0x68, 1.0f / cellZ);
        float range = cellX * cellX + cellZ * cellZ;
        world.SetPayloadFloat(0x6C, BitConverter.Int32BitsToSingle((BitConverter.SingleToInt32Bits(range) >> 1) + 0x1FC00000) * -0.5f);
        float sizeX = world.PayloadFloat(0x3C), sizeZ = world.PayloadFloat(0x40), originX = world.PayloadFloat(0x34), originZ = world.PayloadFloat(0x38);
        int columns = (int)(sizeX / cellX); if (columns * cellX < sizeX) columns++;
        int rows = (int)(sizeZ / cellZ); if (rows * cellZ > sizeZ) rows++;
        world.SetPayloadInt(0x78, columns); world.SetPayloadInt(0x7C, rows);
        world.Areas.Clear();
        for (int row = 0; row < rows; row++)
            for (int col = 0; col < columns; col++)
            {
                WorldArea area = new(); var r = area.Record.AsSpan();
                float minX = col * cellX + originX, minZ = row * cellZ + originZ;
                WorldBox box = new(new(minX, 0, minZ + cellZ), new(minX + cellX, 0, minZ));
                WriteArea(r, 0x100, minX, minZ, box);
                world.Areas.Add(area);
            }
    }
    private static void WriteArea(Span<byte> r, uint flags, float minX, float minZ, WorldBox box)
    {
        BinaryPrimitives.WriteUInt32LittleEndian(r, flags); BinaryPrimitives.WriteInt32LittleEndian(r[4..], -1);
        BinaryPrimitives.WriteSingleLittleEndian(r[8..], minX); BinaryPrimitives.WriteSingleLittleEndian(r[12..], minZ);
        WriteBox(r[16..], box); var (centre, radius) = Sphere(box);
        BinaryPrimitives.WriteSingleLittleEndian(r[40..], centre.X); BinaryPrimitives.WriteSingleLittleEndian(r[44..], centre.Y); BinaryPrimitives.WriteSingleLittleEndian(r[48..], centre.Z);
        BinaryPrimitives.WriteSingleLittleEndian(r[52..], radius);
    }
    private static void WriteBox(Span<byte> r, WorldBox b)
    {
        float[] v = [b.Min.X, b.Min.Y, b.Min.Z, b.Max.X, b.Max.Y, b.Max.Z];
        for (int i = 0; i < 6; i++) BinaryPrimitives.WriteSingleLittleEndian(r[(i * 4)..], v[i]);
    }

    /// <summary>
    /// WorldRectToGridIndex (retail 0x450840): the cell holding a rectangle's centre, or none when the rectangle leaves
    /// the world or overhangs that cell by more than the inclusion tolerance.
    /// </summary>
    public static (int Column, int Row) GridIndex(WorldNode world, float minX, float maxX, float minZ, float maxZ)
    {
        var g = ReadGrid(world);
        if (g.OriginX - g.ToleranceX > minX || maxX >= g.MaxX + g.ToleranceX || maxZ > g.OriginZ + g.ToleranceZ || minZ <= g.MaxZ - g.ToleranceZ) return (-1, -1);
        float centreX = (maxX + minX) * 0.5f - g.OriginX, centreZ = (maxZ + minZ) * 0.5f - g.OriginZ;
        int col = Math.Clamp((int)(centreX * g.InverseX), 0, g.Columns - 1), row = Math.Clamp((int)(centreZ * g.InverseZ), 0, g.Rows - 1);
        var area = world.Areas[row * g.Columns + col].Record.AsSpan();
        float cellMinX = BinaryPrimitives.ReadSingleLittleEndian(area[8..]), cellMinZ = BinaryPrimitives.ReadSingleLittleEndian(area[12..]);
        float cellMaxX = g.CellX + cellMinX, cellMaxZ = g.CellZ + cellMinZ;
        if (minX < cellMinX && cellMinX - minX > g.ToleranceX || maxX > cellMaxX && maxX - cellMaxX > g.ToleranceX) return (-1, -1);
        if (minZ < cellMaxZ && cellMaxZ - minZ > g.ToleranceZ || maxZ > cellMinZ && maxZ - cellMinZ > g.ToleranceZ) return (-1, -1);
        return (col, row);
    }

    /// <summary>
    /// Places world children (in the order they were added) into their grid cells or the world's own child list
    /// (AddChildAtGrid/AddChildToGridCell), then sets each filled area's height range and sphere (RebuildAreaBounds).
    /// Landmarks (0x80) and nodes without bounds stay in the world's list.
    /// </summary>
    public static void Partition(WorldNode world, IEnumerable<WorldNode> children)
    {
        if (world.Class != WorldNodeClass.World) throw new ArgumentException("A world node is required.", nameof(world));
        var g = ReadGrid(world);
        foreach (var area in world.Areas) area.Nodes.Clear();
        world.Children.Clear();
        foreach (var child in children)
        {
            (int col, int row) = (-1, -1);
            if ((child.Flags & LandmarkFlag) == 0 && (child.Flags & CachedBoundsFlag) != 0 && world.Areas.Count > 0)
            {
                var box = WorldBox.Of(ParentCorners(child));
                (col, row) = GridIndex(world, box.Min.X, box.Max.X, box.Min.Z, box.Max.Z);
            }
            if (col >= 0 && world.Areas[row * g.Columns + col].Nodes.Count < 0x7FFF) world.Areas[row * g.Columns + col].Nodes.Add(child);
            else { (col, row) = (-1, -1); world.Children.Add(child); }
            child.GridColumn = col; child.GridRow = row;
            if (!child.Parents.Contains(world)) child.Parents.Add(world);
        }
        foreach (var area in world.Areas.Where(a => a.Nodes.Count > 0))
        {
            var r = area.Record.AsSpan(); uint flags = BinaryPrimitives.ReadUInt32LittleEndian(r) & ~0x100u;
            float low = 0, high = 0; bool any = false;
            foreach (var child in area.Nodes.Where(c => (c.Flags & CachedBoundsFlag) != 0))
                foreach (var corner in ParentCorners(child))
                {
                    if (!any) { low = high = corner.Y; any = true; } else { low = Math.Min(low, corner.Y); high = Math.Max(high, corner.Y); }
                }
            if (!any) { BinaryPrimitives.WriteUInt32LittleEndian(r, flags); continue; }
            WorldBox box = new(new(BinaryPrimitives.ReadSingleLittleEndian(r[16..]), low, BinaryPrimitives.ReadSingleLittleEndian(r[24..])),
                new(BinaryPrimitives.ReadSingleLittleEndian(r[28..]), high, BinaryPrimitives.ReadSingleLittleEndian(r[36..])));
            WriteArea(r, flags | 0x100, BinaryPrimitives.ReadSingleLittleEndian(r[8..]), BinaryPrimitives.ReadSingleLittleEndian(r[12..]), box);
        }
    }
}
