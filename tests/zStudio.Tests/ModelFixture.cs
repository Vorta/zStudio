using System.Buffers.Binary;
using System.Numerics;
using Recoil.Zbd.Core.Export;

namespace Recoil.Zbd.Tests;

internal static class ModelFixture
{
    internal static ImportedMesh Mesh => new([new(-.3f,0,0),new(.3f,0,0),new(0,2,.1f)], [Vector3.UnitZ,Vector3.UnitZ,Vector3.UnitZ], [new(0,1),new(1,1),new(.5f,0)], [0,1,2]);
    /// <summary>Slot 0 is the one active material; each import takes the next free slot.</summary>
    internal static byte[] GameZ(int materialSlots = 4)
    {
        const int materials = 36;
        int models = materials + 16 + materialSlots * 44, data = models + 12 + 2 * 88, nodes = data + 2 * 100, nodeData = nodes + 2 * 196;
        byte[] b = new byte[nodeData + 144 + 4 + 144 + 4];
        void I(int o, int v) => BinaryPrimitives.WriteInt32LittleEndian(b.AsSpan(o), v);
        void F(int o, float v) => I(o,BitConverter.SingleToInt32Bits(v));
        I(0,0x02971222); I(4,15); I(8,0); I(12,36); I(16,materials); I(20,models); I(24,2); I(28,-1); I(32,nodes);
        I(materials,materialSlots); I(materials + 4,1); I(materials + 8,1); I(materials + 12,0);
        for (int i = 0; i < materialSlots; i++) { int o = materials + 16 + i * 44; b[o] = 255; I(o + 16,-1); BinaryPrimitives.WriteInt16LittleEndian(b.AsSpan(o + 42),(short)(i == 0 || i == materialSlots - 1 ? -1 : i+1)); BinaryPrimitives.WriteInt16LittleEndian(b.AsSpan(o + 40),(short)(i < 2 ? -1 : i-1)); }
        I(models,2); I(models + 4,2); I(models + 8,-1);
        for (int i = 0; i < 2; i++)
        {
            int h = models + 12 + i * 88, d = data + i * 100;
            I(h,1); I(h + 4,3); I(h + 8,1); I(h + 12,1); I(h + 16,3); I(h + 48,123); I(h + 64,456); F(h + 80,2); I(h + 84,d);
            var m = Mesh;
            for (int v = 0; v < 3; v++) { F(d + v * 12,m.Positions[v].X); F(d + v * 12 + 4,m.Positions[v].Y); F(d + v * 12 + 8,m.Positions[v].Z); }
            I(d + 36,3); I(d + 44,111); I(d + 52,222); I(d + 56,0);
            for (int v = 0; v < 3; v++) { I(d + 64 + v * 4,v); F(d + 76 + v * 8,m.Uvs[v].X); F(d + 80 + v * 8,m.Uvs[v].Y); }
            h = nodes + i * 196; System.Text.Encoding.ASCII.GetBytes(i == 0 ? "root" : "child").CopyTo(b,h);
            I(h + 52,5); I(h + 60,i); I(h + 84,i); I(h + 92,1-i);
            // Retail node flags: 0x100 cached, 0x200 model and 0x400 child bounds valid.
            I(h + 36,i == 0 ? 0x700 : 0x300);
            for (int box = 116; box <= (i == 0 ? 164 : 140); box += 24) { F(h + box,-3); F(h + box + 4,-3); F(h + box + 8,-3); F(h + box + 12,3); F(h + box + 16,3); F(h + box + 20,3); }
            I(h + 192,nodeData + i * 148); I(nodeData + i * 148,8); I(nodeData + i * 148 + 144,1-i);
        }
        return b;
    }
    internal static byte[] Texture()
    { byte[] b = new byte[24]; BinaryPrimitives.WriteInt32LittleEndian(b.AsSpan(4),1); return b; }
}
