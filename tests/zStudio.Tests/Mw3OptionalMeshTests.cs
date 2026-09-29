using System.Numerics;
using System.Text.Json.Nodes;
using Recoil.Zbd.Core;
using Recoil.Zbd.Core.Formats;
using Xunit;

namespace Recoil.Zbd.Tests;

public sealed class Mw3OptionalMeshTests
{
    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public void OptionalCornerArraysPreserveTheNextPolygonAndData(bool uv, bool colors)
    {
        using MemoryStream s = new(); using BinaryWriter w = new(s);
        foreach (var point in new[] { Vector3.Zero, Vector3.UnitX, Vector3.UnitY }) { w.Write(point.X); w.Write(point.Y); w.Write(point.Z); }
        foreach (bool first in new[] { true, false })
        {
            w.Write(3); w.Write(0); w.Write(1); w.Write(0); w.Write(first && uv ? 1 : 0);
            w.Write(first && colors ? 0x12345678 : 0); w.Write(0); w.Write(first ? 7 : 9); w.Write(0);
        }
        w.Write(0); w.Write(1); w.Write(2);
        if (uv) for (int i = 0; i < 3; i++) { w.Write(i * .25f); w.Write(i * .5f); }
        if (colors) for (int i = 0; i < 3; i++) { w.Write(255f); w.Write(128f); w.Write(0f); }
        w.Write(2); w.Write(1); w.Write(0); w.Write(0x76543210);
        BinaryCursor cursor = new(s.ToArray());
        var model = GameZReader.ReadModelData(cursor, new JsonObject { ["vertex_count"] = 3, ["polygon_count"] = 2 }, 0, GameZLayouts.For(27), new("polygon/light record"), null, TestContext.Current.CancellationToken);
        Assert.Equal(new[] { 7, 9 }, model.Polygons.Select(p => p.MaterialIndex));
        Assert.Equal(new[] { 2, 1, 0 }, model.Polygons[1].Vertices);
        Assert.Equal(colors ? 3 : 0, model.Polygons[0].Colors.Length); Assert.Empty(model.Polygons[1].Colors);
        Assert.Equal(uv ? 3 : 0, model.Polygons[0].Uvs.Length);
        Assert.Equal(0x76543210, cursor.I32()); Assert.Equal(0, cursor.Remaining);
    }
}
