using Recoil.Zbd.Core.Formats;

namespace Recoil.Zbd.Core.Worlds;

/// <summary>The shared reader's aggregate geometry limits, checked while building and before serializing a world.</summary>
internal sealed class WorldGeometryBudget
{
    private long records, vectors, corners;
    internal long Records => records;
    internal void Add(WorldModel model)
    {
        long nextRecords = records + model.Polygons.Count + model.Points.Count;
        long nextVectors = vectors + model.Vertices.Count + model.Normals.Count + model.Morphs.Count;
        long nextCorners = corners;
        foreach (var point in model.Points) nextRecords += point.Vertices.Length;
        foreach (var polygon in model.Polygons) nextCorners += (long)polygon.Vertices.Length + polygon.Normals.Length + polygon.Uvs.Length;
        GameZLayouts.CheckEntries("polygon/light record", nextRecords, GameZLayouts.MaximumGeometryRecords);
        GameZLayouts.CheckEntries("model vector", nextVectors, GameZLayouts.MaximumModelVectors);
        GameZLayouts.CheckEntries("polygon corner", nextCorners, GameZLayouts.MaximumPolygonCorners);
        (records, vectors, corners) = (nextRecords, nextVectors, nextCorners);
    }
}
