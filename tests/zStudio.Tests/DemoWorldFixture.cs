using System.Buffers.Binary;
using System.IO;
using System.Numerics;
using Recoil.Zbd.Core.Formats;

namespace Recoil.Zbd.Tests;

/// <summary>
/// Version-13 worlds for tests: a version-15 file rewritten in the layout of the 1998 demos. Node slots grow to 268 bytes and
/// hold the cached box as its eight corners in the parent's space (the box under the node's own matrix); Object3D records
/// hold their translation after the scale. Every other section is unchanged.
/// </summary>
internal static class DemoWorldFixture
{
    private const int Slot15 = 196, Slot13 = 268;
    /// <summary>The corners in the demos' order: the bottom face (lowest Y), then the top, each from (min X, max Z).</summary>
    private static readonly (bool X, bool Y, bool Z)[] Order =
        [(false, false, true), (true, false, true), (true, false, false), (false, false, false), (false, true, true), (true, true, true), (true, true, false), (false, true, false)];

    public static byte[] FromVersion15(byte[] source)
    {
        var doc = FormatRegistry.Default.OpenBytes("gamez.zbd", source);
        var layout = doc.GameZLayout ?? throw new InvalidDataException("A version-15 world is required.");
        var scene = doc.Scene!;
        using MemoryStream output = new();
        output.Write(source, 0, layout.NodeOffset);
        byte[] table = new byte[(long)layout.NodeCapacity * Slot13];
        for (int slot = 0; slot < layout.NodeCapacity; slot++)
        {
            var s = source.AsSpan(layout.NodeOffset + slot * Slot15, Slot15); var t = table.AsSpan(slot * Slot13, Slot13);
            s[..116].CopyTo(t);
            Matrix4x4? matrix = slot < scene.Nodes.Count && scene.Nodes[slot].Class == "object3d" ? Matrix(source.AsSpan((int)layout.NodeDataOffsets[slot])) : null;
            bool empty = s.Slice(116, 24).IndexOfAnyExcept((byte)0) < 0;
            Vector3 min = Vec(s[116..]), max = Vec(s[128..]);
            for (int k = 0; k < 8; k++)
            {
                Vector3 corner = empty ? Vector3.Zero : new(Order[k].X ? max.X : min.X, Order[k].Y ? max.Y : min.Y, Order[k].Z ? max.Z : min.Z);
                if (!empty && matrix is { } m) corner = Vector3.Transform(corner, m);
                Put(t[(116 + k * 12)..], corner);
            }
            s[140..192].CopyTo(t[212..]); s[192..196].CopyTo(t[264..]);
        }
        output.Write(table);
        for (int i = 0; i < scene.Nodes.Count; i++)
        {
            int start = (int)layout.NodeDataOffsets[i], end = i + 1 < scene.Nodes.Count ? (int)layout.NodeDataOffsets[i + 1] : source.Length;
            var data = source.AsSpan(start, end - start);
            if (scene.Nodes[i].Class != "object3d") { output.Write(data); continue; }
            output.Write(data[..48]);
            byte[] translation = new byte[12];
            if (Matrix(data) is { } m) Put(translation, m.Translation);
            output.Write(translation); output.Write(data[48..]);
        }
        byte[] result = output.ToArray();
        BinaryPrimitives.WriteUInt32LittleEndian(result.AsSpan(4), 13);
        return result;
    }

    /// <summary>A version-15 Object3D record's matrix, unless its flags say it has none.</summary>
    private static Matrix4x4? Matrix(ReadOnlySpan<byte> data)
    {
        if ((BinaryPrimitives.ReadUInt32LittleEndian(data) & 8) != 0) return null;
        float[] f = new float[12];
        for (int i = 0; i < 12; i++) f[i] = BinaryPrimitives.ReadSingleLittleEndian(data[(0x30 + i * 4)..]);
        return new(f[0], f[1], f[2], 0, f[3], f[4], f[5], 0, f[6], f[7], f[8], 0, f[9], f[10], f[11], 1);
    }
    private static Vector3 Vec(ReadOnlySpan<byte> b) => new(BinaryPrimitives.ReadSingleLittleEndian(b), BinaryPrimitives.ReadSingleLittleEndian(b[4..]), BinaryPrimitives.ReadSingleLittleEndian(b[8..]));
    private static void Put(Span<byte> b, Vector3 v)
    {
        BinaryPrimitives.WriteSingleLittleEndian(b, v.X); BinaryPrimitives.WriteSingleLittleEndian(b[4..], v.Y); BinaryPrimitives.WriteSingleLittleEndian(b[8..], v.Z);
    }
}
