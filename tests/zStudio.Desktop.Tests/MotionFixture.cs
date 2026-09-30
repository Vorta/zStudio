using System.Buffers.Binary;
using System.IO;
using System.Text;

namespace Recoil.Zbd.Desktop.Tests;

internal static class MotionFixture
{
    internal static byte[] Library(bool textured = false)
    {
        byte[] materials = new byte[44]; Int(materials, 0, 1); materials[4] = 255;
        if (textured)
        {
            materials[5] = 1; // Material flags.
            using MemoryStream s = new(); using BinaryWriter w = new(s);
            w.Write(materials); w.Write(6); w.Write("sample"u8); materials = s.ToArray();
        }
        byte[] model = new byte[208 + 144 + 92 + 36 + 36 + 12 + 36];
        "body"u8.CopyTo(model); Int(model, 36, 0x300); Int(model, 52, 5); Int(model, 60, 1); Int(model, 208, 8);
        for (int box = 116; box <= 140; box += 24)
            for (int axis = 0; axis < 3; axis++) { Float(model, box + axis * 4, -2); Float(model, box + 12 + axis * 4, 2); }
        int header = 208 + 144; Int(model, header + 16, 1); Int(model, header + 20, 3);
        int data = header + 92;
        Float(model, data, -1); Float(model, data + 12, 1); Float(model, data + 28, 2);
        Int(model, data + 36, 3); Int(model, data + 56, 1); // Optional corner-color array is present.
        for (int i = 0; i < 3; i++) { Int(model, data + 72 + i * 4, i); for (int j = 0; j < 3; j++) Float(model, data + 84 + (i * 3 + j) * 4, 1); }
        byte[] version = new byte[4], format = new byte[4]; Int(version, 0, 27); Int(format, 0, 1);
        return Archive(("version", version), ("format", format), ("materials", materials), ("mech_body.flt", model), ("mech_other.flt", model));
    }
    internal static byte[] Archive(params (string Name, byte[] Bytes)[] members)
    {
        using MemoryStream s = new(); using BinaryWriter w = new(s);
        foreach (var m in members) w.Write(m.Bytes);
        int offset = 0;
        foreach (var m in members)
        {
            byte[] record = new byte[148]; Int(record, 0, offset); Int(record, 4, m.Bytes.Length); Encoding.Latin1.GetBytes(m.Name).CopyTo(record, 8);
            w.Write(record); offset += m.Bytes.Length;
        }
        w.Write(1); w.Write(members.Length); return s.ToArray();
    }
    private static void Int(byte[] bytes, int offset, int value) => BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(offset), value);
    private static void Float(byte[] bytes, int offset, float value) => Int(bytes, offset, BitConverter.SingleToInt32Bits(value));
}
